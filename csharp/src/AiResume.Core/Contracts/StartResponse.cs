namespace AiResume.Core.Contracts;

public sealed record StartResponse
{
    /// <summary>是否已持久接纳;只有 queued 已在事务中提交后才为 true。</summary>
    public bool Accepted { get; init; }

    public RunId RunId { get; init; }

    /// <summary>首次响应通常为 queued。</summary>
    public RunState State { get; init; }

    /// <summary>乐观并发版本。</summary>
    public long StateVersion { get; init; }

    /// <summary>requestId 已存在时为 true。</summary>
    public bool Existing { get; init; }

    public ConflictKind Conflict { get; init; } = ConflictKind.None;

    /// <summary>run_key_busy 时返回占用 runId。</summary>
    public RunId? OccupyingRunId { get; init; }

    public ErrorClass? ErrorClass { get; init; }

    public string? ErrorCode { get; init; }
}
