using AiResume.Core.Contracts;

namespace AiResume.Core.Abstractions;

/// <summary>
/// Provider 健康探测,复用同一 RunContract(taskKind=probe)。
/// 必须真实最小请求成功才算 available;不设客户端总时限;DNS/TCP/TLS/reset 归 failed_local。
/// </summary>
public interface IHealthProbe
{
    Task<StartResponse> StartAsync(StartRequest request, CancellationToken cancellationToken);

    Task<RunSnapshot> StatusAsync(RunId runId, CancellationToken cancellationToken);

    Task<CancelResponse> CancelAsync(CancelRequest request, CancellationToken cancellationToken);
}
