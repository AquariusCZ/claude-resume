using AiResume.Core.Contracts;

namespace AiResume.Core.Abstractions;

/// <summary>
/// 进程监督:launcher + Windows Job Object、三态存活观察、完整进程树终止。
/// 无总时限;存活观察由 Worker 以 15-30 秒周期驱动。
/// </summary>
public interface IProcessSupervisor
{
    /// <summary>
    /// 取消异常只能表示尚未登记、尚未创建进程；一旦开始任何启动副作用，
    /// 实现必须返回结构化结果并继续持有该 RunId 的监督责任。
    /// </summary>
    Task<ProcessStartResult> StartAsync(ProcessStartRequest request, CancellationToken cancellationToken);

    Task<ProcessStatus> StatusAsync(RunId runId, CancellationToken cancellationToken);

    Task<ProcessStopResult> CancelAsync(RunId runId, CancellationToken cancellationToken);
}
