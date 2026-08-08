using System.Text.Json;
using AiResume.Core;
using AiResume.Core.Abstractions;
using AiResume.Core.Contracts;
using AiResume.Core.Events;
using AiResume.Storage;
using AiResume.Worker.Fakes;
using AiResume.Worker.Orchestration;
using AiResume.Worker.Supervision;
using Microsoft.Data.Sqlite;
using Xunit;

namespace AiResume.Tests;

/// <summary>
/// S2-E 编排器出口门禁:同步启动序列、pre-spawn 取消不触碰 supervisor、
/// running 取消 childPending 直到真实 gone、挂起不判失败、同 runKey 拒绝、
/// side_effect_marked 后禁止 fallback 且绝无第二次 provider 调用、事件序。
/// 真实进程用例(cmd 回环 ping)验证 RunStore+ProcessSupervisor+Orchestrator 端到端契约。
/// </summary>
[Collection(SqliteCollection.Name)]
public sealed class OrchestratorTests : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly string _dir;
    private readonly string _dbPath;

    public OrchestratorTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "airesume-orchestrator-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _dbPath = Path.Combine(_dir, "runs.db");
        StorageDatabase.Migrate(_dbPath);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch { /* 测试临时目录残留可容忍 */ }
    }

    private static StartRequest NewStart(string runKey, Guid? requestId = null,
        FallbackPolicy fallback = FallbackPolicy.None) => new()
    {
        ContractVersion = StartRequest.ContractVersionValue,
        RequestId = requestId ?? Guid.NewGuid(),
        RunKey = runKey,
        TaskKind = TaskKind.Query,
        Actor = "ou_test",
        ProfileId = "profile-a",
        InputRef = "input-ref-1",
        FallbackPolicy = fallback,
    };

    private static CancelRequest NewCancel(RunId runId, Guid? commandId = null) => new()
    {
        CommandId = commandId ?? Guid.NewGuid(),
        RunId = runId,
        RequestedBy = "ou_test",
        Reason = CancelReason.UserStop,
    };

    private List<(long Seq, string Type)> ReadEvents(RunId runId)
    {
        using var connection = StorageDatabase.Open(_dbPath);
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT seq, envelope_json FROM run_events WHERE run_id = $run_id ORDER BY seq;";
        cmd.Parameters.AddWithValue("$run_id", runId.ToString());
        var result = new List<(long Seq, string Type)>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var envelope = JsonSerializer.Deserialize<EventEnvelopeV1>(reader.GetString(1), JsonOptions)!;
            result.Add((reader.GetInt64(0), envelope.Type));
        }

        return result;
    }

    /// <summary>真进程端到端:启动后同步到 running,进程退出后观察循环判 succeeded,事件序单调。</summary>
    [Fact]
    public async Task RealProcess_happyPath_events_started_before_terminal_seq_monotonic()
    {
        var store = new RunStore(_dbPath);
        using var supervisor = new ProcessSupervisor(_dbPath);
        var provider = new FakeProviderAdapter(new[] { FakeProviderAdapter.Step.SuccessStep() });
        var orchestrator = new TaskOrchestrator(store, supervisor, provider);

        var start = await orchestrator.StartAsync(NewStart("query|c:\\p1|ou_1"), CancellationToken.None);
        Assert.True(start.Accepted);
        Assert.Equal(RunState.Running, start.State);
        Assert.Equal(1, provider.StartCalls);

        // 轮询观察直到 terminal(真进程 ping -n 2 约 2 秒;上限 15 秒,非总时限语义)。
        RunState final = RunState.Running;
        for (int i = 0; i < 150 && !RunStateMachine.IsTerminal(final); i++)
        {
            await Task.Delay(100);
            await orchestrator.ObserveAsync(CancellationToken.None);
            final = (await orchestrator.StatusAsync(start.RunId, CancellationToken.None)).State;
        }

        Assert.Equal(RunState.Succeeded, final);
        var events = ReadEvents(start.RunId);
        Assert.Contains(events, e => e.Type == "run.started");
        Assert.Contains(events, e => e.Type == "run.terminal");
        long startedSeq = events.First(e => e.Type == "run.started").Seq;
        long terminalSeq = events.First(e => e.Type == "run.terminal").Seq;
        Assert.True(startedSeq < terminalSeq, $"run.started(seq={startedSeq}) 必须先于 run.terminal(seq={terminalSeq})。");
        long[] seqs = events.Select(e => e.Seq).ToArray();
        Assert.Equal(seqs, seqs.OrderBy(x => x).Distinct().ToArray());
    }

    /// <summary>pre-spawn 取消:queued 残留(崩溃恢复场景)取消后直接 cancelled,绝不触碰 supervisor。</summary>
    [Fact]
    public async Task PreSpawnCancel_no_process_spawned_supervisor_untouched()
    {
        var store = new RunStore(_dbPath);
        var supervisor = new FakeProcessSupervisor();
        var provider = new FakeProviderAdapter();
        var orchestrator = new TaskOrchestrator(store, supervisor, provider);

        // 绕过编排器同步驱动,直接落 queued(复刻崩溃恢复后的残留)。
        StartResponse queued = await store.StartAsync(NewStart("query|c:\\p2|ou_2"), CancellationToken.None);
        Assert.Equal(RunState.Queued, queued.State);

        CancelResponse cancel = await orchestrator.CancelAsync(NewCancel(queued.RunId), CancellationToken.None);
        Assert.Equal(RunState.Cancelled, cancel.State);
        Assert.False(cancel.ChildPending);

        // pre-spawn 取消不触碰 supervisor(无进程可终止,也绝不该启动)。
        Assert.Equal(0, supervisor.StartCalls);
        Assert.Equal(0, supervisor.CancelCalls);

        // 观察循环也不会为此 run 启动进程。
        await orchestrator.ObserveAsync(CancellationToken.None);
        Assert.Equal(0, supervisor.StartCalls);
    }

    /// <summary>running 取消:ChildPending 保持 running,直到真实 gone 后落 cancelled 且 runKey 释放。</summary>
    [Fact]
    public async Task RunningCancel_childPending_until_gone_then_cancelled_runKey_released()
    {
        var store = new RunStore(_dbPath);
        var supervisor = new FakeProcessSupervisor { CancelChildPending = true };
        var provider = new FakeProviderAdapter();
        var orchestrator = new TaskOrchestrator(store, supervisor, provider);
        string runKey = "query|c:\\p3|ou_3";

        StartResponse start = await orchestrator.StartAsync(NewStart(runKey), CancellationToken.None);
        Assert.Equal(RunState.Running, start.State);

        CancelResponse cancel = await orchestrator.CancelAsync(NewCancel(start.RunId), CancellationToken.None);
        Assert.True(cancel.ChildPending);
        Assert.Equal(RunState.Running, cancel.State);
        Assert.Equal(1, supervisor.CancelCalls);

        // 未确认退出前:观察循环保持 running(不提前落 terminal)。
        await orchestrator.ObserveAsync(CancellationToken.None);
        RunSnapshot pending = await orchestrator.StatusAsync(start.RunId, CancellationToken.None);
        Assert.Equal(RunState.Running, pending.State);
        Assert.True(pending.CancelRequestedAt is not null);

        // 真实 gone 后:观察循环落 cancelled,ChildPending 解除。
        supervisor.StatusProvider = () => new ProcessStatus { Liveness = ProcessLiveness.Gone };
        await orchestrator.ObserveAsync(CancellationToken.None);
        RunSnapshot final = await orchestrator.StatusAsync(start.RunId, CancellationToken.None);
        Assert.Equal(RunState.Cancelled, final.State);
        Assert.False(final.ChildPending);
        Assert.Equal(ErrorClass.Cancelled, final.ErrorClass);

        // runKey 已释放:同 key 新请求被接受。
        StartResponse again = await orchestrator.StartAsync(NewStart(runKey), CancellationToken.None);
        Assert.True(again.Accepted);
    }

    /// <summary>provider 挂起(Hang 步):3 个观察周期仍 running,静默永不触发失败。</summary>
    [Fact]
    public async Task ProviderHang_three_cycles_stays_running_no_failure()
    {
        var store = new RunStore(_dbPath);
        var supervisor = new FakeProcessSupervisor();
        var provider = new FakeProviderAdapter(new[] { FakeProviderAdapter.Step.HangStep() });
        var orchestrator = new TaskOrchestrator(store, supervisor, provider);

        StartResponse start = await orchestrator.StartAsync(NewStart("query|c:\\p4|ou_4"), CancellationToken.None);
        Assert.Equal(RunState.Running, start.State);

        for (int i = 0; i < 3; i++)
        {
            await orchestrator.ObserveAsync(CancellationToken.None);
            RunSnapshot snapshot = await orchestrator.StatusAsync(start.RunId, CancellationToken.None);
            Assert.Equal(RunState.Running, snapshot.State);
        }

        Assert.Equal(3, provider.StatusCalls);
    }

    /// <summary>同 runKey 并发所有权:第二个 Start 拒绝 RunKeyBusy 并返回占用者。</summary>
    [Fact]
    public async Task SameRunKey_second_start_rejected_RunKeyBusy()
    {
        var store = new RunStore(_dbPath);
        var supervisor = new FakeProcessSupervisor();
        var provider = new FakeProviderAdapter();
        var orchestrator = new TaskOrchestrator(store, supervisor, provider);
        string runKey = "query|c:\\p5|ou_5";

        StartResponse first = await orchestrator.StartAsync(NewStart(runKey), CancellationToken.None);
        Assert.True(first.Accepted);

        StartResponse second = await orchestrator.StartAsync(NewStart(runKey), CancellationToken.None);
        Assert.False(second.Accepted);
        Assert.Equal(ConflictKind.RunKeyBusy, second.Conflict);
        Assert.Equal(first.RunId, second.OccupyingRunId);
    }

    /// <summary>side_effect_marked 后 FallbackAllowed=false;随后 provider 失败落 failed_provider,绝无第二次调用。</summary>
    [Fact]
    public async Task SideEffectMarked_disables_fallback_then_failure_goes_failed_provider()
    {
        var store = new RunStore(_dbPath);
        var supervisor = new FakeProcessSupervisor();
        var provider = new FakeProviderAdapter(new[]
        {
            FakeProviderAdapter.Step.SideEffectStep(),
            FakeProviderAdapter.Step.FailStep(ErrorClass.Transient, "upstream_500"),
        });
        var orchestrator = new TaskOrchestrator(store, supervisor, provider);

        StartResponse start = await orchestrator.StartAsync(
            NewStart("query|c:\\p6|ou_6", fallback: FallbackPolicy.ProviderExplicitOnce), CancellationToken.None);
        Assert.Equal(RunState.Running, start.State);
        Assert.True((await orchestrator.StatusAsync(start.RunId, CancellationToken.None)).FallbackAllowed);

        // 第一轮:消费 SideEffect 步,编排器标记副作用 → fallback 立即失效。
        await orchestrator.ObserveAsync(CancellationToken.None);
        RunSnapshot marked = await orchestrator.StatusAsync(start.RunId, CancellationToken.None);
        Assert.True(marked.SideEffectsStarted);
        Assert.False(marked.FallbackAllowed);
        Assert.Equal(RunState.Running, marked.State);

        // 第二轮:消费 Fail 步 → failed_provider;先终止进程再落 terminal。
        await orchestrator.ObserveAsync(CancellationToken.None);
        RunSnapshot failed = await orchestrator.StatusAsync(start.RunId, CancellationToken.None);
        Assert.Equal(RunState.FailedProvider, failed.State);
        Assert.Equal(ErrorClass.Transient, failed.ErrorClass);
        Assert.Equal("upstream_500", failed.ErrorCode);
        Assert.Equal(1, provider.StartCalls);
        Assert.Equal(1, supervisor.CancelCalls);
    }

    /// <summary>provider 启动明确拒绝(Provider 类):failed_provider,进程绝不启动。</summary>
    [Fact]
    public async Task ProviderStartRejection_ProviderClass_fails_provider_no_process()
    {
        var store = new RunStore(_dbPath);
        var supervisor = new FakeProcessSupervisor();
        var provider = new FakeProviderAdapter(startRejected: true, startErrorClass: ErrorClass.Transient,
            startErrorCode: "provider_start_rejected");
        var orchestrator = new TaskOrchestrator(store, supervisor, provider);

        StartResponse start = await orchestrator.StartAsync(NewStart("query|c:\\p7|ou_7"), CancellationToken.None);
        Assert.Equal(RunState.FailedProvider, start.State);
        Assert.Equal("provider_start_rejected", start.ErrorCode);
        Assert.Equal(0, supervisor.StartCalls);
        Assert.Equal(1, provider.StartCalls);
    }

    /// <summary>provider 启动明确拒绝(Internal 类):failed_local(本地基础设施问题不 blame provider)。</summary>
    [Fact]
    public async Task ProviderStartRejection_InternalClass_fails_local()
    {
        var store = new RunStore(_dbPath);
        var supervisor = new FakeProcessSupervisor();
        var provider = new FakeProviderAdapter(startRejected: true, startErrorClass: ErrorClass.Internal,
            startErrorCode: "config_missing");
        var orchestrator = new TaskOrchestrator(store, supervisor, provider);

        StartResponse start = await orchestrator.StartAsync(NewStart("query|c:\\p8|ou_8"), CancellationToken.None);
        Assert.Equal(RunState.FailedLocal, start.State);
        Assert.Equal(0, supervisor.StartCalls);
    }

    /// <summary>RUN-CONTRACT §13 #9:同 requestId 重复 Start 返回同 runId,spawn 恰为 1(绝不二次驱动)。</summary>
    [Fact]
    public async Task DuplicateStart_sameRequestId_returnsSameRunId_noSecondSpawn()
    {
        var store = new RunStore(_dbPath);
        var supervisor = new FakeProcessSupervisor();
        var provider = new FakeProviderAdapter();
        var orchestrator = new TaskOrchestrator(store, supervisor, provider);
        Guid requestId = Guid.NewGuid();

        StartResponse first = await orchestrator.StartAsync(NewStart("query|c:\\p9|ou_9", requestId), CancellationToken.None);
        Assert.True(first.Accepted);
        Assert.False(first.Existing);
        Assert.Equal(RunState.Running, first.State);

        StartResponse second = await orchestrator.StartAsync(NewStart("query|c:\\p9|ou_9", requestId), CancellationToken.None);
        Assert.True(second.Accepted);
        Assert.True(second.Existing);
        Assert.Equal(first.RunId, second.RunId);

        // 幂等命中绝不二次 spawn(同一用户动作只启动一次)。
        Assert.Equal(1, provider.StartCalls);
        Assert.Equal(1, supervisor.StartCalls);
    }

    /// <summary>RUN-CONTRACT §13 #10:重复 Cancel(同 commandId)只终止一次,不重复触碰 supervisor。</summary>
    [Fact]
    public async Task DuplicateCancel_sameCommandId_terminatesOnce()
    {
        var store = new RunStore(_dbPath);
        var supervisor = new FakeProcessSupervisor();
        var provider = new FakeProviderAdapter();
        var orchestrator = new TaskOrchestrator(store, supervisor, provider);
        Guid commandId = Guid.NewGuid();

        StartResponse start = await orchestrator.StartAsync(NewStart("query|c:\\p10|ou_10"), CancellationToken.None);
        Assert.Equal(RunState.Running, start.State);

        CancelResponse first = await orchestrator.CancelAsync(NewCancel(start.RunId, commandId), CancellationToken.None);
        Assert.Equal(RunState.Cancelled, first.State);
        Assert.False(first.ChildPending);
        Assert.Equal(1, supervisor.CancelCalls);

        CancelResponse second = await orchestrator.CancelAsync(NewCancel(start.RunId, commandId), CancellationToken.None);
        Assert.Equal(RunState.Cancelled, second.State);

        // 重复命令幂等:不再次请求终止,也不重复落 terminal。
        Assert.Equal(1, supervisor.CancelCalls);
    }

    /// <summary>
    /// RUN-CONTRACT §13 #5 邻近路径:进程已 gone 且 provider 明确失败(本地类)→ failed_local。
    /// 骨架无法表达 provider terminal(接口冻结),"gone 且无任何失败"仍按骨架级 succeeded(注释已明示);
    /// 本用例验证 provider 失败在进程消失后仍然胜出,不因 gone 吞掉失败。
    /// </summary>
    [Fact]
    public async Task ProcessGone_withProviderFailure_fails_local_not_succeeded()
    {
        var store = new RunStore(_dbPath);
        var supervisor = new FakeProcessSupervisor
        {
            StatusProvider = () => new ProcessStatus { Liveness = ProcessLiveness.Gone },
        };
        var provider = new FakeProviderAdapter(new[] { FakeProviderAdapter.Step.FailStep(ErrorClass.Internal, "fake_failed") });
        var orchestrator = new TaskOrchestrator(store, supervisor, provider);

        StartResponse start = await orchestrator.StartAsync(NewStart("query|c:\\p11|ou_11"), CancellationToken.None);
        Assert.Equal(RunState.Running, start.State);

        await orchestrator.ObserveAsync(CancellationToken.None);
        RunSnapshot final = await orchestrator.StatusAsync(start.RunId, CancellationToken.None);

        Assert.Equal(RunState.FailedLocal, final.State);
        Assert.Equal(ErrorClass.Internal, final.ErrorClass);
        Assert.Equal("fake_failed", final.ErrorCode);
        Assert.False(final.ChildPending);
    }
}

/// <summary>
/// FakeProcessSupervisor:可编程进程存活/终止确认的测试替身。
/// 默认 Alive 且 Cancel 立即确认(ChildPending=false);测试可按需置 Gone/挂起确认。
/// </summary>
public sealed class FakeProcessSupervisor : IProcessSupervisor
{
    public int StartCalls { get; private set; }

    public int CancelCalls { get; private set; }

    public int StatusCalls { get; private set; }

    /// <summary>终止是否保持 childPending(未确认真实退出)。</summary>
    public bool CancelChildPending { get; set; }

    public Func<ProcessStatus> StatusProvider { get; set; } = () => new ProcessStatus
    {
        Liveness = ProcessLiveness.Alive,
    };

    public Task<ProcessStartResult> StartAsync(ProcessStartRequest request, CancellationToken cancellationToken)
    {
        StartCalls++;
        return Task.FromResult(new ProcessStartResult
        {
            RunId = request.RunId,
            Started = true,
            WrapperPid = 4242,
            ChildPid = 4343,
            JobId = "job-fake",
        });
    }

    public Task<ProcessStatus> StatusAsync(RunId runId, CancellationToken cancellationToken)
    {
        StatusCalls++;
        ProcessStatus status = StatusProvider();
        return Task.FromResult(status with { RunId = runId });
    }

    public Task<ProcessStopResult> CancelAsync(RunId runId, CancellationToken cancellationToken)
    {
        CancelCalls++;
        return Task.FromResult(new ProcessStopResult
        {
            RunId = runId,
            TerminateRequested = true,
            ChildPending = CancelChildPending,
        });
    }
}
