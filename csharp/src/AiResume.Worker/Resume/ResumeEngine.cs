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
    private readonly IProjectSessionInspector? _sessions;
    private readonly IResumeNotices? _notices;
    private readonly Func<DateTimeOffset> _clock;
    private readonly SemaphoreSlim _runOnceGate = new(1, 1);
    private readonly HashSet<string> _postedNotices = new(StringComparer.Ordinal);

    /// <summary>
    /// 同一项目连续因同一个「不会自己好」的原因没能启动(执行器或项目目录不在、执行条件核实不了)的次数。
    /// 这类原因每拍静默重试一整夜,用户第二天才在界面上看到;连续 3 次就发一次飞书提醒。键 = 周期|路径。
    /// </summary>
    private readonly Dictionary<string, (string Reason, int Count)> _notStartedStreak = new(StringComparer.OrdinalIgnoreCase);
    private const int NotStartedAlertAfter = 3;
    private string? _unpersistedPendingRunId;

    /// <summary>额度恢复后原生会话仍开着、且过了这么久尾部还是那条中断，才提醒用户(先给原生自动继续一个机会)。</summary>
    public static readonly TimeSpan SessionOpenGrace = TimeSpan.FromMinutes(10);

    public ResumeEngine(
        ProductConfigStore configStore,
        ProductStateStore stateStore,
        CheckerCycle cycle,
        IClaudeUsageProbe probe,
        IClaudeResumeRunner runner,
        IProcessSupervisor processSupervisor,
        ILogger<ResumeEngine> logger,
        TimeSpan? tickInterval = null,
        Func<string, bool?>? activeRunDetector = null,
        IProjectSessionInspector? sessions = null,
        IResumeNotices? notices = null,
        Func<DateTimeOffset>? clock = null)
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
        // 生产由 DI 注入会话检查器与通知写入器;测试不注入时既不读真实会话目录，也不写真实通知队列。
        _sessions = sessions;
        _notices = notices;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
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

        // 6. 执行探测(工作目录固定 shadow 根,不落进用户项目)。模型是额度授权的一部分；
        // 若它在异步请求期间变化，旧证据立即作废并针对新模型重新取证。
        ClaudeProbeResult probe;
        string admittedModel;
        while (true)
        {
            admittedModel = ResumeTarget.Identity(config);
            try
            {
                probe = await _probe.ProbeAsync(config, ShadowPaths.Root, cancellationToken);
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

            ProductConfig afterProbe = _configStore.Load();
            if (!afterProbe.Armed || !_cycle.TestCycleActive(afterProbe, state.CycleId))
            {
                return;
            }

            if (SameResumeModel(ResumeTarget.Identity(afterProbe), admittedModel))
            {
                config = afterProbe;
                break;
            }

            _logger.LogInformation(
                "resume.probe.model_changed 探测期间续跑模型变化,丢弃旧证据并重新取证,cycleId={CycleId},oldModel={OldModel},newModel={NewModel}",
                state.CycleId, admittedModel, afterProbe.ResumeModel);
            config = afterProbe;
            if (!_cycle.MarkProbeAttempt(config, state))
            {
                return;
            }
        }

        // 7. 按探测结果分派。逐项目取证(Projects 非 null)走统一入口:每个项目各自判定待续跑、
        // 额度、占用;旧的整体语义只保留给测试替身与未绑定目标。
        if (probe.Projects is not null)
        {
            ProbeDecision decision = _cycle.OnProbe(config, state, probe);
            LogEvaluation(config, state, probe, decision);
            PostAttention(config, state, probe);
            if (decision == ProbeDecision.StartResuming)
            {
                await RunResumeRoundAsync(state, probe, admittedModel, cancellationToken);
            }
            else if (decision == ProbeDecision.CompleteCycle)
            {
                CompleteCycle(state);
            }

            return;
        }

        if (probe.IsLimited)
        {
            _logger.LogInformation(
                "resume.probe.limited 观测到限流,cycleId={CycleId},phase={Phase},detail={Detail}",
                config.ArmCycleId, state.Phase, probe.Detail);
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
                await RunResumeRoundAsync(state, probe, admittedModel, cancellationToken);
            }
        }
        else
        {
            _logger.LogInformation(
                "resume.probe.notready 探测未就绪,cycleId={CycleId},phase={Phase},reason={Reason},detail={Detail}",
                config.ArmCycleId, state.Phase, probe.Reason, probe.Detail);
            _cycle.OnNotReady(config, state, probe);
        }
    }

    /// <summary>
    /// 续跑一轮，串行执行(同账号的额度才是吞吐瓶颈，并发只会让多个任务一起被截断)。
    /// <para>逐项目取证时：按 Selected 顺序取第一个「待续跑且本次证据放行」的项目;每跑完一个都重新取证
    /// 再选下一个——同模型的项目共用额度，前一个可能刚好把它用完。没能启动(会话在客户端打开、
    /// 令牌即将过期、执行条件未核实)只记原因并跳过该项目，绝不锁周期。</para>
    /// <para>旧的整体语义(探测不给逐项目事实)保持原样：取第一个尚未完成的项目，后续项目启动前重新取证。</para>
    /// 运行中新增的项目会进入本轮，移除的项目自然消失；只有锁内复核最新队列已全部完成后才能完成周期。
    /// </summary>
    private async Task RunResumeRoundAsync(
        CheckerState state,
        ClaudeProbeResult initialAdmission,
        string initialAdmittedModel,
        CancellationToken cancellationToken)
    {
        ClaudeProbeResult? admission = initialAdmission;
        string? admittedModel = initialAdmittedModel;
        bool perProject = initialAdmission.Projects is not null;
        var skipped = new List<string>();
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

            if (admission is not null && !SameResumeModel(ResumeTarget.Identity(freshConfig), admittedModel))
            {
                _logger.LogInformation(
                    "resume.project.quota.model_changed 启动项目前续跑模型变化,丢弃旧证据并重新取证,cycleId={CycleId},oldModel={OldModel},newModel={NewModel}",
                    state.CycleId, admittedModel, freshConfig.ResumeModel);
                admission = null;
                admittedModel = null;
            }

            ProjectRef? project;
            if (perProject)
            {
                if (admission is null)
                {
                    if (!_cycle.MarkProbeAttempt(freshConfig, state))
                    {
                        return;
                    }

                    string probeIdentity = ResumeTarget.Identity(freshConfig);
                    try
                    {
                        admission = await _probe.ProbeAsync(freshConfig, ShadowPaths.Root, cancellationToken);
                        admittedModel = probeIdentity;
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        return;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex,
                            "resume.project.quota.error 项目启动前重新取证异常,cycleId={CycleId}",
                            state.CycleId);
                        ProductConfig afterError = _configStore.Load();
                        if (afterError.Armed && _cycle.TestCycleActive(afterError, state.CycleId))
                        {
                            _cycle.OnNotReady(afterError, state, new ClaudeProbeResult { Reason = "probe-error" });
                        }
                        return;
                    }

                    // 取证是异步边界：等待期间用户可能解除/重新布防、改项目或模型。
                    ProductConfig admissionConfig = _configStore.Load();
                    if (!admissionConfig.Armed || !_cycle.TestCycleActive(admissionConfig, state.CycleId))
                    {
                        _logger.LogInformation(
                            "resume.cycle.superseded 重新取证后周期已失效,中止本轮,cycleId={CycleId}",
                            state.CycleId);
                        return;
                    }

                    if (!SameResumeModel(ResumeTarget.Identity(admissionConfig), admittedModel))
                    {
                        _logger.LogInformation(
                            "resume.project.quota.model_changed 重新取证期间项目或模型变化,丢弃旧证据并重新取证,cycleId={CycleId}",
                            state.CycleId);
                        admission = null;
                        admittedModel = null;
                        continue;
                    }

                    if (admission.Projects is null)
                    {
                        _cycle.OnNotReady(admissionConfig, state, admission);
                        return;
                    }

                    ProbeDecision decision = _cycle.OnProbe(admissionConfig, state, admission);
                    LogEvaluation(admissionConfig, state, admission, decision);
                    PostAttention(admissionConfig, state, admission);
                    if (decision == ProbeDecision.CompleteCycle)
                    {
                        CompleteCycle(state);
                        return;
                    }

                    if (decision != ProbeDecision.StartResuming)
                    {
                        return;
                    }

                    freshConfig = admissionConfig;
                }

                ClaudeProbeResult evidence = admission;
                project = freshConfig.Selected.FirstOrDefault(candidate =>
                    IsPendingProject(state, candidate.Path) &&
                    !skipped.Any(path => ProjectPaths.Equal(path, candidate.Path)) &&
                    AdmissionFor(evidence, candidate.Path)?.Admissible == true);
                if (project is null)
                {
                    // 其余待续跑项目都在等额度、会话或令牌(原因已逐项目记下),本轮到此为止。
                    _cycle.EndRound(freshConfig, state);
                    return;
                }
            }
            else
            {
                project = freshConfig.Selected.FirstOrDefault(candidate =>
                    !IsDoneProject(state, candidate.Path));
                if (project is null)
                {
                    if (CompleteCycle(state))
                    {
                        continue;
                    }

                    return;
                }

                // 上一项目可能恰好耗尽某个 scoped 窗口。首项目复用刚触发本轮的实时证据；
                // 后续每个项目都重新探测，确保一次 round-level ready 不会放行整支队列。
                if (admission is null)
                {
                    if (!_cycle.MarkProbeAttempt(freshConfig, state))
                    {
                        return;
                    }

                    string probeModel = ResumeTarget.Identity(freshConfig);
                    try
                    {
                        admission = await _probe.ProbeAsync(
                            freshConfig,
                            ShadowPaths.Root,
                            cancellationToken);
                        admittedModel = probeModel;
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        return;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex,
                            "resume.project.quota.error 项目启动前额度复核异常,cycleId={CycleId},project={Project}",
                            state.CycleId, project.Name);
                        ProductConfig afterError = _configStore.Load();
                        if (afterError.Armed && _cycle.TestCycleActive(afterError, state.CycleId))
                        {
                            _cycle.OnNotReady(
                                afterError, state, new ClaudeProbeResult { Reason = "probe-error" });
                        }
                        return;
                    }
                }

                // 额度请求是异步边界；等待期间用户可能解除/重新布防或移除项目。
                // 必须基于请求完成后的配置决定是否写状态、是否启动，不能拿旧周期快照回写。
                ProductConfig admissionConfig = _configStore.Load();
                if (!admissionConfig.Armed || !_cycle.TestCycleActive(admissionConfig, state.CycleId))
                {
                    _logger.LogInformation(
                        "resume.cycle.superseded 额度复核后周期已失效,中止本轮,cycleId={CycleId},project={Project}",
                        state.CycleId, project.Name);
                    return;
                }
                if (!ContainsSelectedProject(admissionConfig, project.Path))
                {
                    _logger.LogInformation(
                        "resume.queue.changed 额度复核后项目已移除,重新读取队列,cycleId={CycleId},project={Project}",
                        state.CycleId, project.Name);
                    continue;
                }
                if (!SameResumeModel(ResumeTarget.Identity(admissionConfig), admittedModel))
                {
                    _logger.LogInformation(
                        "resume.project.quota.model_changed 额度复核期间续跑模型变化,丢弃旧证据并重新取证,cycleId={CycleId},project={Project},oldModel={OldModel},newModel={NewModel}",
                        state.CycleId, project.Name, admittedModel, admissionConfig.ResumeModel);
                    admission = null;
                    admittedModel = null;
                    continue;
                }
                freshConfig = admissionConfig;

                if (admission.IsLimited)
                {
                    _logger.LogInformation(
                        "resume.project.quota.limited 项目启动前额度已收紧,回到等待,cycleId={CycleId},project={Project},detail={Detail}",
                        state.CycleId, project.Name, admission.Detail);
                    _cycle.OnLimited(freshConfig, state, admission);
                    return;
                }

                if (!admission.Ready)
                {
                    _logger.LogWarning(
                        "resume.project.quota.unverified 项目启动前没有可用证据,回到等待,cycleId={CycleId},project={Project},reason={Reason},detail={Detail}",
                        state.CycleId, project.Name, admission.Reason, admission.Detail);
                    _cycle.OnNotReady(freshConfig, state, admission);
                    return;
                }
            }

            ProjectAdmission? projectAdmission = perProject ? AdmissionFor(admission!, project.Path) : null;
            string? interruptionId = projectAdmission?.InterruptionId;
            bool verifyTranscript = perProject && _sessions is not null && projectAdmission?.Evidence == "interrupted";
            // 走到这里时 freshConfig 的整体身份等于取证时的身份;此后的门禁只看这个项目自己的身份:
            // 别的项目被移除或改模型，不影响它已经取得的授权，更不能把它正在跑的任务杀掉。
            string identityForRun = ResumeTarget.ProjectIdentity(freshConfig, project.Path);
            string runModel = ResumeTarget.EffectiveModel(project, freshConfig.ResumeTarget);
            ProjectRef runProject = project;

            // 执行续跑。
            ResumeRunResult result;
            string? startGateRejection = null;
            string? startGateObservedModel = null;
            bool startedNoticePosted = false;
            try
            {
                result = await _runner.RunAsync(
                    project,
                    freshConfig,
                    cancellationToken,
                    beforeStart: runId =>
                    {
                        ProductConfig activeConfig = _configStore.Load();
                        if (!activeConfig.Armed || !_cycle.TestCycleActive(activeConfig, state.CycleId))
                        {
                            startGateRejection = "cycle";
                            return false;
                        }
                        if (!ContainsSelectedProject(activeConfig, runProject.Path))
                        {
                            startGateRejection = "project";
                            return false;
                        }
                        if (!SameResumeModel(ResumeTarget.ProjectIdentity(activeConfig, runProject.Path), identityForRun))
                        {
                            startGateRejection = "model";
                            startGateObservedModel = activeConfig.ResumeModel;
                            return false;
                        }
                        if (verifyTranscript)
                        {
                            // 最终门禁：会话尾部必须仍是本次要续的那一条中断，且此刻没人打开它。
                            // 用户在手机或终端上已经续上、或刚把会话打开，都不能再由这里重放一次。
                            ClaudeSessionEvidence? latest = _sessions!.ReadEvidence(runProject);
                            if (latest is null)
                            {
                                startGateRejection = "evidence-unverified";
                                return false;
                            }
                            if (!latest.InterruptedByLimit)
                            {
                                startGateRejection = "continued-elsewhere";
                                return false;
                            }
                            if (!string.Equals(latest.LastAssistantUuid, interruptionId, StringComparison.Ordinal))
                            {
                                startGateRejection = "evidence-changed";
                                return false;
                            }
                            bool? continuedElsewhere = latest.LastAssistantAtUtc is { } interruptedAt
                                ? _sessions.ContinuedInAnotherSession(runProject, interruptedAt)
                                : false;
                            if (continuedElsewhere != false)
                            {
                                // 中断之后同目录新开的会话已经接着干了:旧上下文不能再去改这个工作树。判断不了同样不启动。
                                startGateRejection = continuedElsewhere == true ? "continued-in-other-session" : "evidence-unverified";
                                return false;
                            }
                            string occupancy = _sessions.Occupancy(runProject).State;
                            if (occupancy != "idle")
                            {
                                startGateRejection = occupancy == "in-use" ? "session-in-use" : occupancy;
                                return false;
                            }
                        }

                        return _cycle.PrepareActiveRun(activeConfig, state, runProject.Path, runId, interruptionId);
                    },
                    shouldContinue: _ =>
                    {
                        // 监控循环第一次询问时进程已经起来了:此刻发「已开始续跑」,不早于 spawn;写入失败下次询问再试。
                        if (!startedNoticePosted)
                        {
                            startedNoticePosted = Post(new ResumeNotice("resume-started", runProject.Path, state.CycleId + "|" + runProject.Path + "|" + interruptionId + "|started", runModel));
                        }

                        ProductConfig activeConfig = _configStore.Load();
                        return activeConfig.Armed &&
                            _cycle.TestCycleActive(activeConfig, state.CycleId) &&
                            SameResumeModel(ResumeTarget.ProjectIdentity(activeConfig, runProject.Path), identityForRun);
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

            if (result.StartCancelledBeforeSpawn)
            {
                ProductConfig cancellationConfig = _configStore.Load();
                RunId? preparedRunId = result.PreparedRunId;
                bool rolledBack = preparedRunId is { } exactRunId &&
                    cancellationConfig.Armed &&
                    _cycle.RollbackPreparedRun(
                        cancellationConfig, state, project.Path, exactRunId, interruptionId);
                if (rolledBack)
                {
                    _logger.LogInformation(
                        "resume.project.start_cancelled Worker关闭发生在spawn前,已撤销精确预登记,cycleId={CycleId},project={Project},runId={RunId}",
                        state.CycleId, project.Name, preparedRunId);
                }
                else if (cancellationConfig.Armed &&
                    _cycle.TestCycleActive(cancellationConfig, state.CycleId))
                {
                    _logger.LogWarning(
                        "resume.project.start_cancelled_unverified Worker关闭发生在spawn前,但预登记无法精确撤销,保持失败关闭,cycleId={CycleId},project={Project}",
                        state.CycleId, project.Name);
                }
                return;
            }

            // 明确没有 spawn 的结果：不可能有副作用，绝不锁周期。最终门禁拒绝(beforeStart 返回 false)
            // 本身就证明没有 spawn,不依赖运行器是否标了 NotStarted。
            if (result.RunId is null && (result.NotStarted || startGateRejection is not null))
            {
                ProductConfig notStartedConfig = _configStore.Load();
                if (!notStartedConfig.Armed ||
                    !_cycle.TestCycleActive(notStartedConfig, state.CycleId))
                {
                    _logger.LogInformation(
                        "resume.cycle.superseded 项目在最终启动门禁前周期已失效,未创建进程,cycleId={CycleId},project={Project}",
                        state.CycleId, project.Name);
                    return;
                }

                if (startGateRejection == "project")
                {
                    _logger.LogInformation(
                        "resume.queue.changed 项目在最终启动门禁前被移除,未创建进程并重新读取队列,cycleId={CycleId},project={Project}",
                        state.CycleId, project.Name);
                    continue;
                }

                if (startGateRejection == "model")
                {
                    _logger.LogInformation(
                        "resume.project.model_changed 项目在最终启动门禁前续跑模型变化,未创建进程并重新取证,cycleId={CycleId},project={Project},oldModel={OldModel},newModel={NewModel}",
                        state.CycleId, project.Name, runModel, startGateObservedModel);
                    admission = null;
                    admittedModel = null;
                    continue;
                }

                if (startGateRejection is "continued-elsewhere" or "continued-in-other-session")
                {
                    _cycle.ApplyProjectResult(notStartedConfig, state, project.Path, "continued-elsewhere", interruptionId: interruptionId);
                    if (startGateRejection == "continued-in-other-session")
                    {
                        _cycle.NoteProject(notStartedConfig, state, project.Path, "continued-elsewhere", "other-session");
                    }

                    _logger.LogInformation(
                        "resume.project.continued_elsewhere 会话已被续上(在客户端或手机上,或同目录新开的会话接着干了),不再自动续跑,cycleId={CycleId},project={Project},how={How}",
                        state.CycleId, project.Name, startGateRejection);
                    continue;
                }

                if (result.Status == "stopped" && startGateRejection is null)
                {
                    // PrepareActiveRun 拒绝：周期已失效或已被锁存，由下一拍状态机收口。
                    return;
                }

                if (result.PreparedRunId is { } prepared)
                {
                    _cycle.RollbackPreparedRun(notStartedConfig, state, project.Path, prepared, interruptionId);
                }

                string notStartedReason = startGateRejection ?? result.Status;
                _cycle.NoteProject(notStartedConfig, state, project.Path, "not-started:" + notStartedReason, result.NotStartedDetail);
                _logger.LogInformation(
                    "resume.project.not_started 项目本次未启动(未创建进程,不锁周期),cycleId={CycleId},project={Project},model={Model},reason={Reason},detail={Detail}",
                    state.CycleId, project.Name, runModel, notStartedReason, result.NotStartedDetail);
                NoteNotStartedStreak(state.CycleId, project.Path, notStartedReason, runModel);
                if (!perProject)
                {
                    return;
                }

                skipped.Add(project.Path);
                continue;
            }

            // 已经 spawn(或结果未知):本项目消耗了额度，下一个项目启动前必须重新取证。
            _notStartedStreak.Remove(state.CycleId + "|" + project.Path);
            admission = null;
            admittedModel = null;

            if (result.SideEffectsStarted && result.Status == "limited")
            {
                result = result with
                {
                    Status = "limited-side-effects",
                    StopRound = true,
                };
            }

            _logger.LogInformation(
                "resume.project.done 项目续跑完成,cycleId={CycleId},project={Project},model={Model},status={Status},exitCode={ExitCode}",
                state.CycleId, project.Name, runModel, result.Status, result.ExitCode);
            if (result.Status != "limited")
            {
                Post(new ResumeNotice(
                    result.Status == "success" ? "resume-succeeded" : "resume-failed",
                    project.Path,
                    state.CycleId + "|" + project.Path + "|" + (result.RunId?.ToString() ?? interruptionId) + "|" + result.Status,
                    runModel,
                    result.Status == "success" ? null : result.Status));
            }

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
                        latest.PendingCancellationProjectPath = runProject.Path;
                        latest.PendingCancellationCycleId = pendingCycleId;

                        if (string.Equals(latest.CycleId, pendingCycleId, StringComparison.Ordinal))
                        {
                            latest.ProjectStatus ??= new Dictionary<string, string>();
                            latest.ProjectStatus[ProjectPaths.KeyFor(latest.ProjectStatus, runProject.Path)] = "cancel-pending";
                            latest.ReplayBlocked = true;
                            latest.Phase = CheckerState.PhaseBlocked;
                            latest.ActiveRunId = pendingRunIdText;
                            latest.ActiveProjectPath = runProject.Path;
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
                    // 被判限流:逐项目取证时只让这一个项目回到等待、本轮跳过它,别的模型的项目照常轮到——
                    // 否则排在队首、反复误判的项目会把它们一直饿着。下一个项目启动前已经重新取证(admission 已清空)。
                    // 同模型的项目凭的是同一份额度证据，刚被实际请求推翻:本轮一并跳过，下一拍按新证据再评估，
                    // 免得误判时每拍按项目数成倍空跑(每次空跑都会往用户会话里追加一条续跑提示和一条限流记录)。
                    _logger.LogInformation(
                        "resume.cycle.backtowaiting 续跑被判限流,该项目与同模型项目本轮回到等待,cycleId={CycleId},project={Project},model={Model}",
                        state.CycleId, project.Name, runModel);
                    if (!perProject)
                    {
                        return;
                    }

                    skipped.Add(project.Path);
                    skipped.AddRange(resultConfig.Selected
                        .Where(other => string.Equals(ResumeTarget.EffectiveModel(other, resultConfig.ResumeTarget), runModel, StringComparison.Ordinal))
                        .Select(other => other.Path));
                    break;

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

    /// <summary>
    /// 锁内提交周期完成。与项目增删共享同一配置锁：刚有新项目加入时返回 true(队列变化，调用方继续本轮),
    /// 不能把最新队列尚未完成的状态提交成 done/解除布防。
    /// </summary>
    private bool CompleteCycle(CheckerState state)
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

            if (latestConfig.Selected.Any(candidate => !IsDoneProject(state, candidate.Path)))
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
            return true;
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

        return false;
    }

    /// <summary>
    /// 每次取证一行：决策、周期原因与逐项目「名=模型|额度[:窗口]|占用|会话证据|原因」。
    /// 只含窗口名、状态码与项目名，不含令牌、账号或会话正文。
    /// </summary>
    private void LogEvaluation(ProductConfig config, CheckerState state, ClaudeProbeResult probe, ProbeDecision decision)
    {
        string projects = string.Join(";", config.Selected.Select(project =>
        {
            ProjectAdmission? admission = AdmissionFor(probe, project.Path);
            ProjectNote? note = ProjectPaths.Find(state.ProjectNotes, project.Path);
            return project.Name + "=" + (admission?.ModelId ?? "?") + "|" + (admission?.Quota ?? "?") +
                (admission?.QuotaDetail is { Length: > 0 } detail ? ":" + detail : string.Empty) + "|" +
                (admission?.Session ?? "?") + "|" + (admission?.Evidence ?? "?") + "|" + (note?.Reason ?? "?");
        }));
        _logger.LogInformation(
            "resume.probe.evaluated 逐项目取证,cycleId={CycleId},decision={Decision},phase={Phase},reason={Reason},pending={Pending},projects={Projects}",
            state.CycleId, decision, state.Phase, state.LastProbeReason,
            state.ProjectStatus?.Count(pair => pair.Value == "limited") ?? 0, projects);
    }

    /// <summary>
    /// 需要人知道的等待：额度已恢复、会话却还开在客户端(过了宽限期尾部仍是那条中断,说明原生没有自动继续);
    /// Claude 登录令牌过期导致读不到额度;账号变化需要重新布防。每件事按稳定键只提醒一次。
    /// </summary>
    private void PostAttention(ProductConfig config, CheckerState state, ClaudeProbeResult probe)
    {
        DateTimeOffset now = _clock();
        foreach (ProjectRef project in config.Selected)
        {
            if (!IsPendingProject(state, project.Path) || AdmissionFor(probe, project.Path) is not { } admission)
            {
                continue;
            }

            if (admission.Quota == "ok" && admission.Session is "in-use" or "cwd-busy" && admission.Evidence == "interrupted" &&
                now >= (admission.LimitResetsAtUtc ?? admission.InterruptedAtUtc ?? now) + SessionOpenGrace)
            {
                Post(new ResumeNotice("session-open", project.Path,
                    state.CycleId + "|" + project.Path + "|" + admission.InterruptionId + "|session-open",
                    admission.ModelId, admission.Session == "cwd-busy" ? "cwd-busy" : admission.SessionClient));
            }
            else if (admission.Quota is "credentials-unavailable" or "identity-unverified")
            {
                // AI Resume 不刷新、不写回凭据,只能提醒人:每个原因每 4 小时最多一次(原因变了重新提醒,文案跟着原因走)。
                string cause = admission.QuotaDetail ?? admission.Quota;
                Post(new ResumeNotice("credentials-unavailable", project.Path,
                    state.CycleId + "|credentials|" + cause + "|" + now.ToUnixTimeSeconds() / (4 * 3600), admission.ModelId, cause));
            }
            else if (admission.Quota == "provider-changed")
            {
                Post(new ResumeNotice("provider-changed", project.Path, state.CycleId + "|provider-changed", admission.ModelId));
            }
        }
    }

    /// <summary>
    /// 记一次「没能启动」。只有不会自己恢复的原因才累计(执行器/项目目录不在、执行条件核实不了);
    /// 会话被打开、尾部变了、令牌待续期这些下一拍就可能好,另有专门的提醒或只需等待。
    /// </summary>
    private void NoteNotStartedStreak(string cycleId, string projectPath, string reason, string modelId)
    {
        string key = cycleId + "|" + projectPath;
        if (reason is not ("no-claude" or "execution-unverified"))
        {
            _notStartedStreak.Remove(key);
            return;
        }

        int count = _notStartedStreak.TryGetValue(key, out var streak) && streak.Reason == reason ? streak.Count + 1 : 1;
        _notStartedStreak[key] = (reason, count);
        // 用 >= 而不是 ==:第 3 次写入失败(去重键已撤回)时，第 4 次还能补发;成功后由去重键保证只发一次。
        if (count >= NotStartedAlertAfter)
        {
            Post(new ResumeNotice("resume-not-started", projectPath, key + "|" + reason + "|not-started", modelId, reason));
        }
    }

    /// <summary>写一条续跑提醒;已写过(去重)或没有通知通道也算完成。只有写入失败才返回 false,调用方可以稍后再试。</summary>
    private bool Post(ResumeNotice notice)
    {
        if (_notices is null || !_postedNotices.Add(notice.DedupeKey))
        {
            return true;
        }

        if (_postedNotices.Count > 512)
        {
            _postedNotices.Clear();
            _postedNotices.Add(notice.DedupeKey);
        }

        try
        {
            _notices.Post(notice);
            return true;
        }
        catch (Exception ex)
        {
            // 通知是辅助功能，写不进队列也不能影响续跑本身。去重键撤回:下一拍再遇到同一件事还能重写,
            // 否则一次写盘失败(杀软占用、磁盘满)就让这条提醒在本进程里永远发不出去。
            _postedNotices.Remove(notice.DedupeKey);
            _logger.LogWarning(ex, "resume.notice.error 续跑提醒写入失败,下一拍重试,type={Type}", notice.Type);
            return false;
        }
    }

    private static ProjectAdmission? AdmissionFor(ClaudeProbeResult probe, string projectPath) =>
        probe.Projects?.FirstOrDefault(admission => ProjectPaths.Equal(admission.Path, projectPath));

    private static bool IsPendingProject(CheckerState state, string projectPath) =>
        state.ProjectStatus?.Any(kv =>
            ProjectPaths.Equal(kv.Key, projectPath) &&
            string.Equals(kv.Value, "limited", StringComparison.Ordinal)) == true;

    private static bool SameResumeModel(string? left, string? right) =>
        string.Equals(left, right, StringComparison.Ordinal);

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

    private static bool IsDoneProject(CheckerState state, string projectPath) =>
        state.ProjectStatus?.Any(kv =>
            ProjectPathEquals(kv.Key, projectPath) &&
            kv.Value is "success" or "continued-elsewhere") == true;

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
