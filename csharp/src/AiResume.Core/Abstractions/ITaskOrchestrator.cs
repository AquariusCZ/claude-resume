using AiResume.Core.Contracts;

namespace AiResume.Core.Abstractions;

/// <summary>
/// 任务编排:Start 持久接纳、Status 只读快照、Cancel 幂等终止。
/// 本接口及实现不得创建任何客户端总时长计时器;cancellationToken 仅表示调用方中止本次调用。
/// </summary>
public interface ITaskOrchestrator
{
    Task<StartResponse> StartAsync(StartRequest request, CancellationToken cancellationToken);

    Task<RunSnapshot> StatusAsync(RunId runId, CancellationToken cancellationToken);

    Task<CancelResponse> CancelAsync(CancelRequest request, CancellationToken cancellationToken);
}
