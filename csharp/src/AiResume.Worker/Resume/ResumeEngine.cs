using AiResume.Core;
using AiResume.Core.Abstractions;
using AiResume.Core.Contracts;
using AiResume.Storage;
using AiResume.Worker.Probes;
using AiResume.Worker.Products;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AiResume.Worker.Resume;

/// <summary>
/// 续跑引擎:驱动布防周期状态机,定时探测,观测到限流后按队列逐项目续跑。
/// 本类只做驱动与编排,不实现任何状态机/登记表/探测器逻辑(全部复用 CheckerCycle 等既有组件)。
/// </summary>
public sealed class ResumeEngine : BackgroundService
{
    private readonly ProductConfigStore _configStore;
    private readonly ProductStateStore _stateStore;
    private readonly CheckerCycle _cycle;
    private readonly IClaudeUsageProbe _probe;
    private readonly IClaudeResumeRunner _runner;
    private readonly ILogger<ResumeEngine> _logger;
    private readonly TimeSpan _tickInterval;
    private readonly Func<string, bool?>? _activeRunDetector;
    private readonly IProcessSupervisor _processSupervisor;
    private readonly SemaphoreSlim _runOnceGate = new(1, 1);
    private string? _unpersistedPendingRunId;

    public ResumeEngine(
        ProductConfigStore configStore,
        ProductStateStore stateStore,
        CheckerCycle cycle,
        IClaudeUsageProbe probe,
        IClaudeResumeRunner runner,
        IProcessSupervisor processSupervisor,
        ILogger<ResumeEngine> logger,
        TimeSpan? tickInterval = null,
        Func<string, bool?>? activeRunDetector = null)
    {
        _configStore = configStore ?? throw new ArgumentNullException(nameof(configStore));
        _stateStore = stateStore ?? throw new ArgumentNullException(nameof(stateStore));
        _cycle = cycle ?? throw new ArgumentNullException(nameof(cycle));
        _probe = probe ?? throw new ArgumentNullException(nameof(probe));
        _runner = runner ?? throw new ArgumentNullException(nameof(runner));
        _processSupervisor = processSupervisor ?? throw new ArgumentNullException(nameof(processSupervisor));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _tickInterval = tickInterval ?? TimeSpan.FromSeconds(30);
        _activeRunDetector = activeRunDetector;
    }

    /// <summary>后台服务主循环:每拍执行一次 <see cref="RunOnceAsync"/>,异常不退出(除取消外)。</summary>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                // 正常取消,退出循环。
                break;
            }
            catch (Exception ex)
            {
                // 单拍异常绝不退出引擎,记日志后继续下一拍。
                _logger.LogError(ex, "resume.tick.error 引擎单拍执行异常,继续下一拍");
            }

            try
            {
                await Task.Delay(_tickInterval, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }

    /// <summary>
    /// 单拍逻辑:先对账待终止进程,再按布防周期状态机推进。未布防且无待对账进程时空转。
    /// 公开而非 internal 是因为它有两个真实调用方:后台主循环,以及 GUI 的「立即续跑」
    /// (用户不想等下一拍时手动触发一拍);测试也据此直接驱动,无需 InternalsVisibleTo。
    /// </summary>
    public async Task RunOnceAsync(CancellationToken cancellationToken)
    {
        if (!await _runOnceGate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            _logger.LogInformation("resume.tick.busy 上一拍仍在执行,跳过重复触发");
            return;
        }

        try
        {
            await RunOnceCoreAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _runOnceGate.Release();
        }
    }

    private async Task RunOnceCoreAsync(CancellationToken cancellationToken)
    {
        // cancel-pending 已经形成但 SQLite 状态写入恰好失败时,先用本进程内存门禁兜住。
        // Worker 崩溃则 Job Object 会终止子进程;Worker 未崩溃时这里阻止下一拍并发启动。
        string? unpersistedPending = Volatile.Read(ref _unpersistedPendingRunId);
        if (!string.IsNullOrEmpty(unpersistedPending))
        {
            bool? pending = await DetectActiveRunAsync(unpersistedPending, cancellationToken).ConfigureAwait(false);
            bool stopped = pending == false ||
                await TryCancelRunAsync(unpersistedPending, cancellationToken).ConfigureAwait(false);
            if (!stopped)
            {
                _logger.LogWarning(
                    "resume.cancel.unpersisted 待终止进程状态尚未落盘,跳过本拍,runId={RunId}",
                    unpersistedPending);
                return;
            }

            Interlocked.CompareExchange(ref _unpersistedPendingRunId, null, unpersistedPending);
        }

        // 1. 加载配置与状态。待终止进程独立于布防周期,即使已经解除布防也必须继续对账。
        var config = _configStore.Load();
        CheckerState state;
        try
        {
            state = _stateStore.LoadStrict();
        }
        catch (Exception ex)
        {
            // ActiveRunId/PendingCancellationRunId 是跨周期并发门禁。读不出状态时若降级为空,
            // 就可能在旧进程仍存活时启动新续跑;因此整拍停止,等待下次读取恢复。
            _logger.LogError(ex, "resume.state.load.error 状态读取失败,为防止重复续跑跳过本拍");
            return;
        }

        // 已请求终止但未确认退出的进程跨周期保留。Matched/Unverifiable 都阻止新续跑;
        // 只有精确 Gone/Mismatched 才清记录继续,避免新旧两个项目进程并发。
        if (!string.IsNullOrEmpty(state.PendingCancellationRunId))
        {
            bool? pending = await DetectActiveRunAsync(
                state.PendingCancellationRunId,
                cancellationToken).ConfigureAwait(false);
            bool stopped = pending == false ||
                await TryCancelRunAsync(state.PendingCancellationRunId, cancellationToken).ConfigureAwait(false);
            if (!stopped)
            {
                _logger.LogWarning(
                    "resume.cancel.pending 已重试终止但进程仍存活或不可核验,跳过本拍,runId={RunId},project={Project}",
                    state.PendingCancellationRunId, state.PendingCancellationProjectPath);
                return;
            }

            string clearedRunId = state.PendingCancellationRunId;
            state = SettleConfirmedCancellation(
                clearedRunId,
                state.PendingCancellationProjectPath,
                state.PendingCancellationCycleId);
            return;
        }

        // ActiveRunId 是“已经可能产生副作用”的精确运行身份。上一拍、旧 Worker 或状态写失败
        // 留下它时,Matched/Unverifiable 都必须阻止新续跑;只有确认 gone/mismatched 才清理。
        if (!string.IsNullOrEmpty(state.ActiveRunId))
        {
            bool? active = await DetectActiveRunAsync(
                state.ActiveRunId,
                cancellationToken).ConfigureAwait(false);
            if (active == false)
            {
                string clearedRunId = state.ActiveRunId;
                state = _stateStore.Update(latest =>
                {
                    if (!string.Equals(latest.ActiveRunId, clearedRunId, StringComparison.Ordinal))
                    {
                        return;
                    }

                    if (latest.ProjectStatus is not null &&
                        latest.ProjectStatus.TryGetValue(latest.ActiveProjectPath, out string? status) &&
                        status is "running" or "cancel-pending")
                    {
                        // 没有 PendingCancellationRunId 就没有“用户已请求终止”的证据。
                        // 进程消失但拿不到完成结果只能判为未确认完成,不能伪装成主动停止。
                        latest.ProjectStatus[latest.ActiveProjectPath] = "exit-null";
                    }

                    latest.ReplayBlocked = true;
                    latest.Phase = CheckerState.PhaseBlocked;
                    latest.ActiveRunId = string.Empty;
                    latest.ActiveProjectPath = string.Empty;
                });
                return;
            }

            if (ShouldKeepActiveRun(config, state))
            {
                _logger.LogWarning(
                    "resume.active.pending 已登记续跑仍存活或不可核验,跳过本拍,runId={RunId},project={Project}",
                    state.ActiveRunId, state.ActiveProjectPath);
                return;
            }

            string cancellingRunId = state.ActiveRunId;
            string cancellingProjectPath = state.ActiveProjectPath;
            string cancellingCycleId = state.CycleId;
            bool cancellationConfirmed = await TryCancelRunAsync(
                cancellingRunId,
                cancellationToken).ConfigureAwait(false);
            if (cancellationConfirmed)
            {
                state = SettleConfirmedCancellation(
                    cancellingRunId,
                    cancellingProjectPath,
                    cancellingCycleId);
                return;
            }

            state = _stateStore.Update(latest =>
            {
                if (!string.IsNullOrEmpty(latest.PendingCancellationRunId) &&
                    !string.Equals(latest.PendingCancellationRunId, cancellingRunId, StringComparison.Ordinal))
                {
                    return;
                }

                if (!string.Equals(latest.ActiveRunId, cancellingRunId, StringComparison.Ordinal) &&
                    !string.Equals(latest.PendingCancellationRunId, cancellingRunId, StringComparison.Ordinal))
                {
                    return;
                }

                latest.PendingCancellationRunId = cancellingRunId;
                latest.PendingCancellationProjectPath = cancellingProjectPath;
                latest.PendingCancellationCycleId = cancellingCycleId;
                if (string.Equals(latest.CycleId, cancellingCycleId, StringComparison.Ordinal))
                {
                    latest.ProjectStatus ??= new Dictionary<string, string>();
                    latest.ProjectStatus[cancellingProjectPath] = "cancel-pending";
                    latest.ReplayBlocked = true;
                    latest.Phase = CheckerState.PhaseBlocked;
                }
            });

            _logger.LogWarning(
                "resume.cancel.pending 续跑已失去布防授权,终止未确认并保留门禁,runId={RunId},project={Project}",
                cancellingRunId,
                cancellingProjectPath);
            return;
        }

        // 2. 未布防/无周期 → 对账完成后空转。
        if (!config.Enabled || !config.Armed || string.IsNullOrEmpty(config.ArmCycleId))
        {
            return;
        }

        // 3. 周期初始化(新周期重置状态;已对齐幂等)。
        if (!_cycle.Initialize(config, state))
        {
            return;
        }

        // v6 之前没有 ReplayBlocked 字段。升级前遗留的失败、停止或 running 状态
        // 也必须先锁存,不能因为新字段默认 false 就自动重放。
        if (_cycle.LatchReplayBlock(config, state))
        {
            return;
        }

        // 一次性周期在 SQLite 已落 done、但配置解除布防尚未落盘时崩溃,会留下
        // armed=true + phase=done。这里只补做解除,绝不能再探测或重放整轮。
        if (!config.Continuous && state.Phase == CheckerState.PhaseDone)
        {
            bool recovered = false;
            _configStore.Update(latest =>
            {
                if (latest.Armed && !latest.Continuous &&
                    _cycle.TestCycleActive(latest, state.CycleId))
                {
                    latest.Armed = false;
                    latest.ArmCycleId = string.Empty;
                    recovered = true;
                }
            });

            if (recovered)
            {
                _logger.LogWarning(
                    "resume.cycle.recovered 已完成的一次性周期仅补做解除布防,cycleId={CycleId}",
                    state.CycleId);
            }

            return;
        }

        // 4. 节奏判定:未到探测时间 → 本拍空转。
        if (!_cycle.ShouldProbe(config, state))
        {
            return;
        }

        // 5. 探测打点(周期失效则放弃本拍)。
        if (!_cycle.MarkProbeAttempt(config, state))
        {
            return;
        }

        // 6. 执行探测(工作目录固定 shadow 根,不落进用户项目)。
        ClaudeProbeResult probe;
        try
        {
            probe = await _probe.ProbeAsync(config.ProbeModel, ShadowPaths.Root, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // 探测异常:记日志,本拍结束(不触发任何状态变更)。
            _logger.LogError(ex, "resume.probe.error 探测执行异常,cycleId={CycleId}", config.ArmCycleId);
            return;
        }

        // 7. 按探测结果分派。
        if (probe.IsLimited)
        {
            _logger.LogInformation(
                "resume.probe.limited 观测到限流,cycleId={CycleId},phase={Phase}",
                config.ArmCycleId, state.Phase);
            _cycle.OnLimited(config, state, probe);
        }
        else if (probe.Ready)
        {
            var decision = _cycle.OnReady(config, state, probe);
            _logger.LogInformation(
                "resume.probe.ready 探测就绪,cycleId={CycleId},phase={Phase},decision={Decision}",
                config.ArmCycleId, state.Phase, decision);
            if (decision == ProbeDecision.StartResuming)
            {
                await RunResumeRoundAsync(state, cancellationToken);
            }
        }
        else
        {
            _logger.LogInformation(
                "resume.probe.notready 探测未就绪,cycleId={CycleId},phase={Phase}",
                config.ArmCycleId, state.Phase);
            _cycle.OnNotReady(config, state, probe);
        }
    }

    /// <summary>
    /// 续跑一轮:每次都按最新 Selected 顺序取第一个尚未成功的项目。
    /// 这样运行中新增的项目会进入本轮，移除的项目会自然消失；只有锁内复核最新队列
    /// 已全部成功后才能完成周期并解除布防。
    /// </summary>
    private async Task RunResumeRoundAsync(CheckerState state, CancellationToken cancellationToken)
    {
        while (true)
        {
            // 每个项目开始前重新加载配置,并校验"仍然布防 + 周期未变"。
            // **两个条件缺一不可**:TestCycleActive 只比对 Enabled 与周期 id,**不看 Armed**;
            // 只用它的话,用户中途点"解除布防"后剩余项目仍会照跑(规格 §3.3.1 要求当拍生效)。
            var freshConfig = _configStore.Load();
            if (!freshConfig.Armed || !_cycle.TestCycleActive(freshConfig, state.CycleId))
            {
                _logger.LogInformation(
                    "resume.cycle.superseded 续跑途中周期失效,中止本轮,cycleId={CycleId},project={Project}",
                    state.CycleId, state.ActiveProjectPath);
                return;
            }

            if (cancellationToken.IsCancellationRequested)
            {
                return;
            }

            ProjectRef? project = freshConfig.Selected.FirstOrDefault(candidate =>
                !HasSuccessfulProject(state, candidate.Path));
            if (project is null)
            {
                CycleCompletionKind completion = CycleCompletionKind.Superseded;
                bool queueChanged = false;
                _configStore.Update(latestConfig =>
                {
                    if (!latestConfig.Armed || !_cycle.TestCycleActive(latestConfig, state.CycleId))
                    {
                        completion = CycleCompletionKind.Superseded;
                        return;
                    }

                    // 与项目增删共享同一配置锁。若刚有新项目加入，本轮继续取它执行，
                    // 不能把最新队列尚未完成的状态提交成 done/解除布防。
                    if (latestConfig.Selected.Any(candidate =>
                            !HasSuccessfulProject(state, candidate.Path)))
                    {
                        queueChanged = true;
                        return;
                    }

                    completion = _cycle.Complete(latestConfig, state);
                    if (completion == CycleCompletionKind.Disarmed)
                    {
                        latestConfig.Armed = false;
                        latestConfig.ArmCycleId = string.Empty;
                    }
                });

                if (queueChanged)
                {
                    _logger.LogInformation(
                        "resume.queue.changed 完成提交前发现新项目,继续本轮,cycleId={CycleId}",
                        state.CycleId);
                    continue;
                }

                switch (completion)
                {
                    case CycleCompletionKind.Disarmed:
                        _logger.LogInformation(
                            "resume.cycle.disarmed 本轮完成,解除布防,cycleId={CycleId}",
                            state.CycleId);
                        break;

                    case CycleCompletionKind.Continuous:
                        _logger.LogInformation(
                            "resume.cycle.continuous 本轮完成,保持布防,cycleId={CycleId}",
                            state.CycleId);
                        break;

                    case CycleCompletionKind.Superseded:
                        _logger.LogInformation(
                            "resume.cycle.superseded 完成时周期已失效,cycleId={CycleId}",
                            state.CycleId);
                        break;

                    case CycleCompletionKind.Blocked:
                        _logger.LogWarning(
                            "resume.cycle.blocked 本周期包含不可自动重放的结果,保持布防并等待用户解除后重新布防,cycleId={CycleId}",
                            state.CycleId);
                        break;
                }

                return;
            }

            // 执行续跑。
            ResumeRunResult result;
            try
            {
                result = await _runner.RunAsync(
                    project,
                    freshConfig,
                    cancellationToken,
                    beforeStart: runId =>
                    {
                        ProductConfig activeConfig = _configStore.Load();
                        return activeConfig.Armed &&
                            ContainsSelectedProject(activeConfig, project.Path) &&
                            _cycle.PrepareActiveRun(activeConfig, state, project.Path, runId);
                    },
                    shouldContinue: _ =>
                    {
                        ProductConfig activeConfig = _configStore.Load();
                        return activeConfig.Armed &&
                            _cycle.TestCycleActive(activeConfig, state.CycleId) &&
                            ContainsSelectedProject(activeConfig, project.Path);
                    });
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                // 单项目异常:记日志,按 error 处理继续下一项目。
                _logger.LogError(ex,
                    "resume.project.error 续跑执行异常,cycleId={CycleId},project={Project}",
                    state.CycleId, project.Name);
                result = new ResumeRunResult
                {
                    ProjectPath = project.Path,
                    Status = "error",
                };
            }

            if (result.SideEffectsStarted && result.Status == "limited")
            {
                result = result with
                {
                    Status = "limited-side-effects",
                    StopRound = true,
                };
            }

            _logger.LogInformation(
                "resume.project.done 项目续跑完成,cycleId={CycleId},project={Project},status={Status}",
                state.CycleId, project.Name, result.Status);

            if (result.Status == "cancel-pending" && result.RunId is { } pendingRunId)
            {
                string pendingCycleId = state.CycleId;
                string pendingRunIdText = pendingRunId.ToString();
                Volatile.Write(ref _unpersistedPendingRunId, pendingRunIdText);
                try
                {
                    state = _stateStore.Update(latest =>
                    {
                        latest.PendingCancellationRunId = pendingRunIdText;
                        latest.PendingCancellationProjectPath = project.Path;
                        latest.PendingCancellationCycleId = pendingCycleId;

                        if (string.Equals(latest.CycleId, pendingCycleId, StringComparison.Ordinal))
                        {
                            latest.ProjectStatus ??= new Dictionary<string, string>();
                            latest.ProjectStatus[project.Path] = "cancel-pending";
                            latest.ReplayBlocked = true;
                            latest.Phase = CheckerState.PhaseBlocked;
                            latest.ActiveRunId = pendingRunIdText;
                            latest.ActiveProjectPath = project.Path;
                        }
                    });
                    Interlocked.CompareExchange(ref _unpersistedPendingRunId, null, pendingRunIdText);
                }
                catch
                {
                    // 保留内存门禁;后台循环下一拍会先核验这个 RunId,不会开始新续跑。
                    throw;
                }

                _logger.LogWarning(
                    "resume.cancel.pending 终止未确认,保留精确 RunId 并中止本轮,cycleId={CycleId},project={Project},runId={RunId}",
                    pendingCycleId, project.Name, pendingRunId);
                return;
            }

            // 长任务结束时配置可能已经变更;必须用最新快照判定周期,不能让旧快照回写旧周期。
            ProductConfig resultConfig = _configStore.Load();
            if (!resultConfig.Armed || !_cycle.TestCycleActive(resultConfig, state.CycleId))
            {
                SettleInactiveCycleRun(state.CycleId, project.Path, result);
                return;
            }

            var outcome = _cycle.ApplyProjectResult(
                resultConfig, state, project.Path, result.Status, result.StopRound);
            if (result.StopRound)
            {
                _logger.LogWarning(
                    "resume.cycle.stopped 项目未能安全继续,终止本轮,cycleId={CycleId},project={Project},status={Status}",
                    state.CycleId, project.Name, result.Status);
                return;
            }

            switch (outcome)
            {
                case ProjectOutcome.Continue:
                case ProjectOutcome.MarkedError:
                    // 继续下一项目。
                    break;

                case ProjectOutcome.BackToWaiting:
                    // 被判限流,回到等待,立即中止本轮。
                    _logger.LogInformation(
                        "resume.cycle.backtowaiting 续跑被判限流,回到等待,cycleId={CycleId},project={Project}",
                        state.CycleId, project.Name);
                    return;

                case ProjectOutcome.CycleSuperseded:
                    // 周期已变化,立即中止且不写状态。
                    _logger.LogInformation(
                        "resume.cycle.superseded 续跑途中周期失效,中止本轮,cycleId={CycleId},project={Project}",
                    state.CycleId, project.Name);
                    return;

                case ProjectOutcome.Blocked:
                    _logger.LogWarning(
                        "resume.cycle.blocked 项目结果禁止自动重放,终止本轮,cycleId={CycleId},project={Project},status={Status}",
                        state.CycleId, project.Name, result.Status);
                    return;
            }
        }
    }

    private void SettleInactiveCycleRun(string cycleId, string projectPath, ResumeRunResult result)
    {
        if (result.RunId is not { } runId)
        {
            return;
        }

        string runIdText = runId.ToString();
        _stateStore.Update(latest =>
        {
            if (!string.Equals(latest.CycleId, cycleId, StringComparison.Ordinal) ||
                !string.Equals(latest.ActiveRunId, runIdText, StringComparison.Ordinal))
            {
                return;
            }

            latest.ProjectStatus ??= new Dictionary<string, string>();
            latest.ProjectStatus[projectPath] = result.Status;
            if (result.Status != "success")
            {
                latest.ReplayBlocked = true;
                latest.Phase = CheckerState.PhaseBlocked;
            }

            latest.ActiveRunId = string.Empty;
            latest.ActiveProjectPath = string.Empty;
        });
    }

    /// <summary>
    /// Worker 进程内必须以 ProcessSupervisor 持有的 Job Object 为权威；外层 cmd 已退出时，
    /// Job 内仍可能有 Claude 后代进程。测试可用同步 detector 注入三态证据。
    /// </summary>
    private async Task<bool?> DetectActiveRunAsync(string runId, CancellationToken cancellationToken)
    {
        if (_activeRunDetector is not null)
        {
            return _activeRunDetector(runId);
        }

        try
        {
            ProcessStatus status = await _processSupervisor.StatusAsync(
                RunId.FromString(runId),
                cancellationToken).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(status.MonitorError))
            {
                return null;
            }

            return status.Liveness switch
            {
                ProcessLiveness.Alive => true,
                ProcessLiveness.Gone => false,
                _ => null,
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private async Task<bool> TryCancelRunAsync(string runId, CancellationToken cancellationToken)
    {
        try
        {
            ProcessStopResult stop = await _processSupervisor.CancelAsync(
                RunId.FromString(runId),
                cancellationToken).ConfigureAwait(false);
            return !stop.ChildPending;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "resume.cancel.error 精确 RunId 终止请求失败,稍后重试,runId={RunId}", runId);
            return false;
        }
    }

    private CheckerState SettleConfirmedCancellation(string runId, string projectPath, string cycleId) =>
        _stateStore.Update(latest =>
        {
            bool ownsPending = string.Equals(
                latest.PendingCancellationRunId,
                runId,
                StringComparison.Ordinal);
            bool ownsActive = string.Equals(latest.ActiveRunId, runId, StringComparison.Ordinal);
            if (!ownsPending && !ownsActive)
            {
                return;
            }

            if (string.Equals(latest.CycleId, cycleId, StringComparison.Ordinal))
            {
                if (latest.ProjectStatus is not null &&
                    latest.ProjectStatus.TryGetValue(projectPath, out string? status) &&
                    status is "running" or "cancel-pending")
                {
                    latest.ProjectStatus[projectPath] = "stopped";
                }

                latest.ReplayBlocked = true;
                latest.Phase = CheckerState.PhaseBlocked;
            }

            if (ownsActive)
            {
                latest.ActiveRunId = string.Empty;
                latest.ActiveProjectPath = string.Empty;
            }

            if (ownsPending)
            {
                latest.PendingCancellationRunId = string.Empty;
                latest.PendingCancellationProjectPath = string.Empty;
                latest.PendingCancellationCycleId = string.Empty;
            }
        });

    private bool ShouldKeepActiveRun(ProductConfig config, CheckerState state) =>
        config.Enabled &&
        config.Armed &&
        state.Phase == CheckerState.PhaseResuming &&
        _cycle.TestCycleActive(config, state.CycleId) &&
        ContainsSelectedProject(config, state.ActiveProjectPath);

    private static bool ContainsSelectedProject(ProductConfig config, string projectPath) =>
        config.Selected.Any(project => ProjectPathEquals(project.Path, projectPath));

    private static bool HasSuccessfulProject(CheckerState state, string projectPath) =>
        state.ProjectStatus?.Any(kv =>
            ProjectPathEquals(kv.Key, projectPath) &&
            string.Equals(kv.Value, "success", StringComparison.Ordinal)) == true;

    private static bool ProjectPathEquals(string? left, string? right)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
        {
            return false;
        }

        try
        {
            return string.Equals(
                RunKey.NormalizeProjectPath(left),
                RunKey.NormalizeProjectPath(right),
                StringComparison.Ordinal);
        }
        catch (ArgumentException)
        {
            return string.Equals(
                left.Trim().TrimEnd('\\', '/'),
                right.Trim().TrimEnd('\\', '/'),
                StringComparison.OrdinalIgnoreCase);
        }
    }
}
