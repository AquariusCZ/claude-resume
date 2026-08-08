namespace AiResume.Core.Contracts;

public sealed record CancelRequest
{
    /// <summary>Cancel 命令幂等 UUID。</summary>
    public Guid CommandId { get; init; }

    public RunId RunId { get; init; }

    /// <summary>用户/GUI/周期控制面。</summary>
    public string? RequestedBy { get; init; }

    public CancelReason Reason { get; init; }
}
