using AiResume.Core.Contracts;

namespace AiResume.Core.Abstractions;

/// <summary>
/// 运行状态唯一 writer(SQLite+WAL,Stage 2-B 实现)。
/// Start 先持久化 queued 再返回;Status 只读;Cancel 持久化命令后请求终止。
/// </summary>
public interface IRunStore
{
    Task<StartResponse> StartAsync(StartRequest request, CancellationToken cancellationToken);

    Task<RunSnapshot> StatusAsync(RunId runId, CancellationToken cancellationToken);

    Task<CancelResponse> CancelAsync(CancelRequest request, CancellationToken cancellationToken);
}
