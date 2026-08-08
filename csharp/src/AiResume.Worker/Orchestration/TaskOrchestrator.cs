using System.Collections.Concurrent;
using AiResume.Core;
using AiResume.Core.Abstractions;
using AiResume.Core.Contracts;
using AiResume.Storage;
using AiResume.Worker.Fakes;
using AiResume.Worker.Supervision;

namespace AiResume.Worker.Orchestration;

/// <summary>
/// TaskOrchestrator(规格 §3.2,S2-E):组合 RunStore + ProcessSupervisor + IProviderAdapter。
///
/// 生命周期决策(对照 RUN-CONTRACT,不设任何总时限计时器):
/// 1. Start = RunStore 持久接纳(queued)→ 同步驱动启动序列(queued→starting→running):
///    - starting 前检查是否已被并发 Cancel(有则直接 cancelled,进程绝不启动);
///    - provider.StartAsync 明确拒绝 → failed_provider/failed_local(按 ErrorClass 归类),不再启动进程;
///    - ProcessSupervisor.StartAsync 失败 → failed_local + provider.Cancel 清理;
///    - 进程启动成功 → running。
/// 2. Terminal 唯一来源:provider 明确结果(启动拒绝/运行中 ProviderFailedException)、
///    本地明确失败(进程启动失败)、显式 Cancel。进程 gone + 无 cancel + 无 provider 失败 = succeeded
///    (骨架级:假 provider 干净退出即成功;真实结果解析属 Stage 4/5)。
/// 3. settle-once:runKey 所有权 = 非 terminal 状态占用(由 RunStore 保证),terminal 落库即释放;
///    cancel 的 ChildPending 仅在进程真实退出确认后置 false。
/// 4. 观察循环(ObserveAsync,由 ObservationWorker 每 15-30 秒驱动)只读持久状态 + 进程存活性:
///    静默/耗时指标永不触发状态变更;挂起进程保持 running。
/// 5. side_effect_marked 后禁止 provider fallback(FallbackAllowed=false),且编排器绝不自动
///    发起第二次 provider 调用(骨架级无 fallback 逻辑,复刻 D-002 语义)。
/// </summary>
public sealed class TaskOrchestrator : ITaskOrchestrator
{
    private readonly RunStore _store;
    private readonly IProcessSupervisor _supervisor;
    private readonly IProviderAdapter _provider;

    /// <summary>进程启动结果缓存(快照组装用);重启丢失后仅影响快照进程字段,不阻塞状态推进。</summary>
    private readonly ConcurrentDictionary<RunId, ProcessStartResult> _launches = new();

    public TaskOrchestrator(RunStore store, IProcessSupervisor supervisor, IProviderAdapter provider)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(supervisor);
        ArgumentNullException.ThrowIfNull(provider);
        _store = store;
        _supervisor = supervisor;
        _provider = provider;
    }

    public async Task<StartResponse> StartAsync(StartRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        StartResponse response = await _store.StartAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.Accepted || response.Existing)
        {
            // 拒绝(RunKeyBusy 等)或 requestId 幂等命中(RUN-CONTRACT §2.1):
            // 原样返回,绝不重复驱动启动(同一请求绝不二次 spawn)。
            return response;
        }

        // 同步驱动启动序列(在 StartAsync 内完成,不创建后台计时器)。
        await DriveStartAsync(response.RunId, request, cancellationToken).ConfigureAwait(false);
        RunSnapshot snapshot = await StatusAsync(response.RunId, cancellationToken).ConfigureAwait(false);
        return new StartResponse
        {
            Accepted = true,
            RunId = response.RunId,
            State = snapshot.State,
            StateVersion = snapshot.StateVersion,
            Existing = response.Existing,
            Conflict = response.Conflict,
            OccupyingRunId = response.OccupyingRunId,
            ErrorClass = snapshot.ErrorClass,
            ErrorCode = snapshot.ErrorCode,
        };
    }

    public async Task<RunSnapshot> StatusAsync(RunId runId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        RunSnapshot snapshot = await _store.StatusAsync(runId, cancellationToken).ConfigureAwait(false);
        if (RunStateMachine.IsTerminal(snapshot.State))
        {
            return snapshot;
        }

        // 非 terminal:合并进程观测(只读,不推进任何状态)。
        ProcessStatus processStatus = await _supervisor.StatusAsync(runId, cancellationToken).ConfigureAwait(false);
        _launches.TryGetValue(runId, out ProcessStartResult? launch);
        return snapshot with
        {
            ProcessLiveness = processStatus.Liveness,
            ChildPending = processStatus.ChildPending,
            WrapperPid = launch?.WrapperPid,
            ChildPid = launch?.ChildPid,
            JobId = launch?.JobId,
            ObservedAt = processStatus.ObservedAt,
            ErrorClass = snapshot.ErrorClass,
        };
    }

    public async Task<CancelResponse> CancelAsync(CancelRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        CancelResponse response = await _store.CancelAsync(request, cancellationToken).ConfigureAwait(false);

        // pre-spawn 取消:store 已直接置 cancelled,无进程可终止(不触碰 supervisor)。
        if (RunStateMachine.IsTerminal(response.State) && !response.ChildPending)
        {
            return response;
        }

        // starting/running:终止进程;确认 gone 才推进 cancelled(settle-once)。
        ProcessStopResult stop = await _supervisor.CancelAsync(request.RunId, cancellationToken).ConfigureAwait(false);
        if (!stop.ChildPending)
        {
            await _store.AdvanceStateAsync(request.RunId, RunState.Cancelled, "cancelled",
                ErrorClass.Cancelled, "user_stop", cancellationToken).ConfigureAwait(false);
            _launches.TryRemove(request.RunId, out _);
            return new CancelResponse
            {
                CommandId = request.CommandId,
                RunId = request.RunId,
                State = RunState.Cancelled,
                StateVersion = response.StateVersion + 1,
                ChildPending = false,
                TerminationRequested = true,
                CancelRequestedAt = response.CancelRequestedAt,
            };
        }

        // 未确认真实退出:登记与运行键保留,观察循环继续核验。
        return new CancelResponse
        {
            CommandId = request.CommandId,
            RunId = request.RunId,
            State = response.State,
            StateVersion = response.StateVersion,
            ChildPending = true,
            TerminationRequested = true,
            CancelRequestedAt = response.CancelRequestedAt,
        };
    }

    /// <summary>
    /// 观察循环入口(ObservationWorker 每 15-30 秒调用一次)。
    /// 只做:queued/starting 兜底驱动、running 的进程存活性核验与结果判定、副作用标记。
    /// 静默指标(无输出/心跳未更新)绝不触发失败。返回 (观察的 run 数, childPending 数)。
    /// </summary>
    public async Task<(int RunsObserved, int ChildPending)> ObserveAsync(CancellationToken cancellationToken)
    {
        int runsObserved = 0;
        int childPending = 0;
        foreach (RunId runId in _store.EnumerateActiveRuns())
        {
            cancellationToken.ThrowIfCancellationRequested();
            runsObserved++;
            RunSnapshot snapshot = await _store.StatusAsync(runId, cancellationToken).ConfigureAwait(false);
            switch (snapshot.State)
            {
                case RunState.Queued:
                case RunState.Starting:
                    // 兜底驱动(如宿主崩溃后重启,queued/starting 需要继续启动)。
                    await DriveStartAsync(runId, ToStartRequest(snapshot), cancellationToken).ConfigureAwait(false);
                    break;

                case RunState.Running:
                    bool cancelled = snapshot.CancelRequestedAt is not null;
                    ProcessStatus processStatus = await _supervisor.StatusAsync(runId, cancellationToken).ConfigureAwait(false);

                    // 单次 provider 状态读取:结果同时用于失败判定与副作用标记,脚本步只消费一次。
                    ProviderStatus? providerStatus = null;
                    ProviderFailedException? providerFailure = null;
                    try
                    {
                        providerStatus = await _provider.StatusAsync(runId, cancellationToken).ConfigureAwait(false);
                    }
                    catch (ProviderFailedException ex)
                    {
                        providerFailure = ex;
                    }

                    if (processStatus.Liveness == ProcessLiveness.Gone)
                    {
                        if (cancelled)
                        {
                            await _store.AdvanceStateAsync(runId, RunState.Cancelled, "cancelled",
                                ErrorClass.Cancelled, "user_stop", cancellationToken).ConfigureAwait(false);
                        }
                        else if (providerFailure is not null)
                        {
                            await FailAsync(runId, providerFailure, cancellationToken).ConfigureAwait(false);
                        }
                        else
                        {
                            // 骨架级:假 provider 干净退出 = 成功(真实结果解析属 Stage 4/5)。
                            await _store.AdvanceStateAsync(runId, RunState.Succeeded, "succeeded",
                                null, null, cancellationToken).ConfigureAwait(false);
                        }

                        _launches.TryRemove(runId, out _);
                    }
                    else if (providerFailure is not null)
                    {
                        // provider 明确失败:先终止进程,再落 failed(terminal 后运行键释放)。
                        await _supervisor.CancelAsync(runId, cancellationToken).ConfigureAwait(false);
                        await FailAsync(runId, providerFailure, cancellationToken).ConfigureAwait(false);
                        _launches.TryRemove(runId, out _);
                    }
                    else
                    {
                        // 存活且无失败:只更新副作用标记与计数,静默/耗时指标不判失败。
                        if (providerStatus?.SideEffectsStarted == true && !snapshot.SideEffectsStarted)
                        {
                            _store.MarkSideEffects(runId);
                        }

                        if (processStatus.ChildPending)
                        {
                            childPending++;
                        }
                    }

                    break;
            }
        }

        return (runsObserved, childPending);
    }

    /// <summary>
    /// 启动序列(幂等分两段,基于持久状态):
    /// queued → starting(run.started 事件)→ provider.StartAsync → ProcessSupervisor.StartAsync → running。
    /// 已 starting 时只继续进程启动段(崩溃恢复不重复调 provider)。
    /// </summary>
    private async Task DriveStartAsync(RunId runId, StartRequest request, CancellationToken cancellationToken)
    {
        RunSnapshot snapshot = await _store.StatusAsync(runId, cancellationToken).ConfigureAwait(false);

        if (snapshot.State == RunState.Queued)
        {
            // 并发 Cancel 检查:starting 前已被取消则直接 cancelled,进程绝不启动。
            if (snapshot.CancelRequestedAt is not null)
            {
                await _store.AdvanceStateAsync(runId, RunState.Cancelled, "cancelled",
                    ErrorClass.Cancelled, "user_stop", cancellationToken).ConfigureAwait(false);
                return;
            }

            if (!await _store.AdvanceStateAsync(runId, RunState.Starting, null, null, null, cancellationToken).ConfigureAwait(false))
            {
                return; // 并发推进冲突,交给观察循环下一轮。
            }

            ProviderStartResult providerResult = await _provider.StartAsync(
                new ProviderStartRequest
                {
                    RunId = runId,
                    ProfileId = request.ProfileId,
                    Provider = request.ProfileId,
                    Model = string.Empty,
                    Cwd = request.Cwd,
                    InputRef = request.InputRef,
                    CredentialRef = request.CredentialRef,
                    SessionRef = request.SessionRef,
                },
                cancellationToken).ConfigureAwait(false);

            if (!providerResult.Accepted)
            {
                // provider 明确拒绝:按 ErrorClass 归类 terminal(骨架级,绝无第二次调用)。
                await FailAsync(runId,
                    new ProviderFailedException(providerResult.ErrorClass ?? ErrorClass.Internal,
                        providerResult.ErrorCode ?? "provider_start_rejected", "provider 启动被拒绝。"),
                    cancellationToken).ConfigureAwait(false);
                return;
            }
        }

        if (snapshot.State is not (RunState.Queued or RunState.Starting))
        {
            return;
        }

        // 进程启动段(崩溃恢复时 queued 已被上段推进到 starting,这里只走一次)。
        ProcessStartResult processResult = await _supervisor.StartAsync(
            BuildProcessStartRequest(runId, request), cancellationToken).ConfigureAwait(false);
        if (!processResult.Started)
        {
            await _provider.CancelAsync(runId, cancellationToken).ConfigureAwait(false);
            await _store.AdvanceStateAsync(runId, RunState.FailedLocal, processResult.ErrorCode ?? "process_start_failed",
                ErrorClass.Internal, processResult.ErrorCode ?? "process_start_failed", cancellationToken).ConfigureAwait(false);
            return;
        }

        _launches[runId] = processResult;
        await _store.AdvanceStateAsync(runId, RunState.Running, null, null, null, cancellationToken).ConfigureAwait(false);
    }

    private async Task FailAsync(RunId runId, ProviderFailedException failure, CancellationToken cancellationToken)
    {
        RunState terminal = failure.ErrorClass switch
        {
            ErrorClass.Internal or ErrorClass.Config => RunState.FailedLocal,
            _ => RunState.FailedProvider,
        };
        await _store.AdvanceStateAsync(runId, terminal, failure.ErrorCode,
            failure.ErrorClass, failure.ErrorCode, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>骨架级假 provider 进程启动命令(cmd + 回环 ping);真实 provider 启动命令属 Stage 4/5。</summary>
    private static ProcessStartRequest BuildProcessStartRequest(RunId runId, StartRequest request) => new()
    {
        RunId = runId,
        FileName = "cmd.exe",
        Arguments = "/c ping -n 2 127.0.0.1 > NUL",
        WorkingDirectory = string.IsNullOrEmpty(request.Cwd) ? Path.GetTempPath() : request.Cwd,
        CommandSignature = ProcessSignature.Compute("cmd.exe"),
    };

    private static StartRequest ToStartRequest(RunSnapshot snapshot) => new()
    {
        ContractVersion = StartRequest.ContractVersionValue,
        RequestId = snapshot.RequestId,
        RunKey = snapshot.RunKey,
        TaskKind = snapshot.TaskKind,
        Actor = snapshot.Actor,
        ProfileId = snapshot.ProfileId,
        SessionRef = snapshot.SessionRef,
        Cwd = null,
        InputRef = string.Empty,
        CredentialRef = null,
        AttemptGroupId = snapshot.AttemptGroupId,
        ParentRunId = snapshot.ParentRunId,
        FallbackPolicy = snapshot.FallbackAllowed ? FallbackPolicy.ProviderExplicitOnce : FallbackPolicy.None,
    };
}
