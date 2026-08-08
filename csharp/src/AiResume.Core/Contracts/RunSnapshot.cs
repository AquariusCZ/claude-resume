namespace AiResume.Core.Contracts;

/// <summary>
/// Status 只读快照(字段分组对齐 docs/RUN-CONTRACT.md 第 3 节)。
/// message 必须脱敏;不得含 prompt、密钥、token 或完整命令行。
/// </summary>
public sealed record RunSnapshot
{
    // identity
    public RunId RunId { get; init; }
    public Guid RequestId { get; init; }
    public string RunKey { get; init; } = string.Empty;
    public TaskKind TaskKind { get; init; }
    public string? Actor { get; init; }
    public Guid? AttemptGroupId { get; init; }
    public Guid? ParentRunId { get; init; }

    // selection
    public string ProfileId { get; init; } = string.Empty;
    public string? Provider { get; init; }
    public string? Model { get; init; }
    public string? Route { get; init; }
    public object? SessionRef { get; init; }

    // state
    public RunState State { get; init; }
    public long StateVersion { get; init; }
    public long Seq { get; init; }
    public string? TerminalReason { get; init; }

    // time
    public DateTimeOffset? QueuedAt { get; init; }
    public DateTimeOffset? StartingAt { get; init; }
    public DateTimeOffset? StartedAt { get; init; }
    public DateTimeOffset? TerminalAt { get; init; }
    public DateTimeOffset? ObservedAt { get; init; }

    // metrics(仅观测,不得触发 terminal)
    public DateTimeOffset? HeartbeatAt { get; init; }
    public DateTimeOffset? LastOutputAt { get; init; }
    public long? SilentSeconds { get; init; }
    public long? OutputBytes { get; init; }
    public long? TokenCount { get; init; }

    // process
    public int? WrapperPid { get; init; }
    public int? ChildPid { get; init; }
    public DateTimeOffset? ProcessStartedAt { get; init; }
    public string? ExecutablePathHash { get; init; }
    public string? CommandSignature { get; init; }
    public string? JobId { get; init; }
    public ProcessLiveness? ProcessLiveness { get; init; }
    public bool ChildPending { get; init; }

    // safety
    public bool SideEffectsStarted { get; init; }
    public DateTimeOffset? SideEffectsStartedAt { get; init; }
    public bool FallbackAllowed { get; init; }
    public bool ReplayAllowed { get; init; }
    public DateTimeOffset? CancelRequestedAt { get; init; }

    // error
    public ErrorClass? ErrorClass { get; init; }
    public string? ErrorCode { get; init; }
    public int? ProviderHttpStatus { get; init; }
    public string? ProviderErrorCode { get; init; }
    public string? Message { get; init; }

    // recovery
    public string? WorkerInstanceId { get; init; }
    public bool Recovered { get; init; }
    public string? RecoveryAction { get; init; }
    public bool? MonitorHealth { get; init; }
    public DateTimeOffset? LastMonitorErrorAt { get; init; }
}
