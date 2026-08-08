using AiResume.Core.Contracts;

namespace AiResume.Core.Abstractions;

/// <summary>
/// Provider 适配:argv/env/session、结构化输出、服务端错误分类、副作用活动事件。
/// 真实 Codex/Claude 适配属 Stage 4/5;本接口不得出现总时限参数。
/// </summary>
public interface IProviderAdapter
{
    Task<ProviderStartResult> StartAsync(ProviderStartRequest request, CancellationToken cancellationToken);

    Task<ProviderStatus> StatusAsync(RunId runId, CancellationToken cancellationToken);

    Task<ProviderStopResult> CancelAsync(RunId runId, CancellationToken cancellationToken);
}
