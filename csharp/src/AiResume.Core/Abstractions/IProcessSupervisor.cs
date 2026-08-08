using AiResume.Core.Contracts;

namespace AiResume.Core.Abstractions;

/// <summary>
/// 进程监督:launcher + Windows Job Object、三态存活观察、完整进程树终止。
/// 无总时限;存活观察由 Worker 以 15-30 秒周期驱动。
/// </summary>
public interface IProcessSupervisor
{
    Task<ProcessStartResult> StartAsync(ProcessStartRequest request, CancellationToken cancellationToken);

    Task<ProcessStatus> StatusAsync(RunId runId, CancellationToken cancellationToken);

    Task<ProcessStopResult> CancelAsync(RunId runId, CancellationToken cancellationToken);
}
