using AiResume.Worker.Orchestration;
using Microsoft.Extensions.Options;

namespace AiResume.Worker;

/// <summary>
/// 观察循环(S2-E 装配完成)。每 15-30 秒(默认 20)驱动一次 TaskOrchestrator.ObserveAsync:
/// 只读持久状态与进程存活性(queued/starting 兜底驱动、running 结果判定、副作用标记),
/// 静默/耗时指标绝不触发失败;周期不是任务总时限。
/// </summary>
public sealed class ObservationWorker : BackgroundService
{
    private readonly ILogger<ObservationWorker> _logger;
    private readonly TimeSpan _interval;
    private readonly TaskOrchestrator _orchestrator;

    public ObservationWorker(ILogger<ObservationWorker> logger, IOptions<ObservationOptions> options,
        TaskOrchestrator orchestrator)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Value.Validate();
        ArgumentNullException.ThrowIfNull(orchestrator);
        _logger = logger;
        _interval = options.Value.Interval;
        _orchestrator = orchestrator;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation(
            "worker.observe.started component={Component} intervalSeconds={IntervalSeconds}",
            "worker",
            _interval.TotalSeconds);

        using PeriodicTimer timer = new(_interval);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            await ObserveAsync(stoppingToken);
        }
    }

    private async Task ObserveAsync(CancellationToken cancellationToken)
    {
        (int runsObserved, int childPending) =
            await _orchestrator.ObserveAsync(cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        _logger.LogInformation(
            "worker.observe.cycle component={Component} runCount={RunCount} childPendingCount={ChildPendingCount}",
            "worker",
            runsObserved,
            childPending);
    }
}
