namespace AiResume.Core.Contracts;

public sealed record ProcessStartRequest
{
    public RunId RunId { get; init; }
    public string FileName { get; init; } = string.Empty;
    public string Arguments { get; init; } = string.Empty;
    public string? WorkingDirectory { get; init; }
    public IReadOnlyDictionary<string, string?>? Environment { get; init; }
    public string CommandSignature { get; init; } = string.Empty;
}

public sealed record ProcessStartResult
{
    public RunId RunId { get; init; }
    public bool Started { get; init; }
    public int? WrapperPid { get; init; }
    public int? ChildPid { get; init; }
    public string? JobId { get; init; }
    public ErrorClass? ErrorClass { get; init; }
    public string? ErrorCode { get; init; }
}

public sealed record ProcessStatus
{
    public RunId RunId { get; init; }
    public ProcessLiveness Liveness { get; init; }
    public bool ChildPending { get; init; }
    public DateTimeOffset ObservedAt { get; init; }
    public string? MonitorError { get; init; }
}

public sealed record ProcessStopResult
{
    public RunId RunId { get; init; }
    public bool TerminateRequested { get; init; }
    public bool ChildPending { get; init; }
}
