using AiResume.Core;
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

    public ResumeEngine(
        ProductConfigStore configStore,
        ProductStateStore stateStore,
        CheckerCycle cycle,
        IClaudeUsageProbe probe,
        IClaudeResumeRunner runner,
        ILogger<ResumeEngine> logger,
        TimeSpan? tickInterval = null)
    {
        _configStore = configStore ?? throw new ArgumentNullException(nameof(configStore));
        _stateStore = stateStore ?? throw new ArgumentNullException(nameof(stateStore));
        _cycle = cycle ?? throw new ArgumentNullException(nameof(cycle));
        _probe = probe ?? throw new ArgumentNullException(nameof(probe));
        _runner = runner ?? throw new ArgumentNullException(nameof(runner));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _tickInterval = tickInterval ?? TimeSpan.FromSeconds(30);
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
    /// 单拍逻辑:加载配置与状态,按布防周期状态机推进。未布防时本拍空转,不写状态、不刷日志。
    /// 公开而非 internal 是因为它有两个真实调用方:后台主循环,以及 GUI 的「立即续跑」
    /// (用户不想等下一拍时手动触发一拍);测试也据此直接驱动,无需 InternalsVisibleTo。
    /// </summary>
    public async Task RunOnceAsync(CancellationToken cancellationToken)
    {
        // 1. 加载配置;未布防/无周期 → 空转。
        var config = _configStore.Load();
        if (!config.Enabled || !config.Armed || string.IsNullOrEmpty(config.ArmCycleId))
        {
            return;
        }

        // 2. 加载状态。
        var state = _stateStore.Load();

        // 3. 周期初始化(新周期重置状态;已对齐幂等)。
        if (!_cycle.Initialize(config, state))
        {
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
                await RunResumeRoundAsync(config, state, cancellationToken);
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
    /// 续跑一轮:按 config.Selected 原有顺序逐个项目续跑。
    /// 每个项目前重新加载配置并校验周期活跃;周期失效或用户解除布防立即中止。
    /// </summary>
    private async Task RunResumeRoundAsync(ProductConfig config, CheckerState state, CancellationToken cancellationToken)
    {
        foreach (var project in config.Selected)
        {
            // 每个项目开始前重新加载配置,并校验"仍然布防 + 周期未变"。
            // **两个条件缺一不可**:TestCycleActive 只比对 Enabled 与周期 id,**不看 Armed**;
            // 只用它的话,用户中途点"解除布防"后剩余项目仍会照跑(规格 §3.3.1 要求当拍生效)。
            var freshConfig = _configStore.Load();
            if (!freshConfig.Armed || !_cycle.TestCycleActive(freshConfig, state.CycleId))
            {
                _logger.LogInformation(
                    "resume.cycle.superseded 续跑途中周期失效,中止本轮,cycleId={CycleId},project={Project}",
                    state.CycleId, project.Name);
                return;
            }

            if (cancellationToken.IsCancellationRequested)
            {
                return;
            }

            // 执行续跑。
            ResumeRunResult result;
            try
            {
                result = await _runner.RunAsync(project, freshConfig, cancellationToken);
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

            _logger.LogInformation(
                "resume.project.done 项目续跑完成,cycleId={CycleId},project={Project},status={Status}",
                state.CycleId, project.Name, result.Status);

            // 应用项目结果。
            var outcome = _cycle.ApplyProjectResult(freshConfig, state, project.Path, result.Status);
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
            }
        }

        // 全部项目走完,完成本轮。
        // **必须重新加载配置**:一轮续跑没有时限,期间用户可能在 GUI 改过 Selected/Continuous。
        // 拿开拍时的旧快照整体写回会静默覆盖用户改动(红线:禁止锁外读旧快照后整体写回)。
        // Update 在**写锁内**重读并只改本次负责的两个字段:GUI 此刻可能正在写
        // Selected/Custom/Hidden(手动增删项目),锁外读快照再整体写回会把它们抹掉。
        CycleCompletionKind completion = CycleCompletionKind.Superseded;
        _configStore.Update(latestConfig =>
        {
            completion = _cycle.Complete(latestConfig, state);
            if (completion == CycleCompletionKind.Disarmed)
            {
                latestConfig.Armed = false;
                latestConfig.ArmCycleId = string.Empty;
            }
        });

        switch (completion)
        {
            case CycleCompletionKind.Disarmed:
                _logger.LogInformation(
                    "resume.cycle.disarmed 本轮完成,解除布防,cycleId={CycleId}",
                    state.CycleId);
                break;

            case CycleCompletionKind.Continuous:
                // 连续模式 → 保持布防,等待下一轮。
                _logger.LogInformation(
                    "resume.cycle.continuous 本轮完成,保持布防,cycleId={CycleId}",
                    state.CycleId);
                break;

            case CycleCompletionKind.Superseded:
                // 周期已变化 → 不写配置。
                _logger.LogInformation(
                    "resume.cycle.superseded 完成时周期已失效,cycleId={CycleId}",
                    state.CycleId);
                break;
        }
    }
}