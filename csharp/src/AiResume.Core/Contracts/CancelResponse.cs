namespace AiResume.Core.Contracts;

public sealed record CancelResponse
{
    public Guid CommandId { get; init; }

    public RunId RunId { get; init; }

    public RunState State { get; init; }

    public long StateVersion { get; init; }

    /// <summary>真实 close/gone 被验证前保持 true;runKey 不释放。</summary>
    public bool ChildPending { get; init; }

    /// <summary>本次是否首次发出终止请求(重复 commandId 返回相同结果)。</summary>
    public bool TerminationRequested { get; init; }

    public DateTimeOffset? CancelRequestedAt { get; init; }
}
