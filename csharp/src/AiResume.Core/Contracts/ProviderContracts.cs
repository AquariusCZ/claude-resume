namespace AiResume.Core.Contracts;

public sealed record ProviderStartRequest
{
    public RunId RunId { get; init; }
    public string ProfileId { get; init; } = string.Empty;
    public string Provider { get; init; } = string.Empty;
    public string Model { get; init; } = string.Empty;
    public string? Cwd { get; init; }
    public string InputRef { get; init; } = string.Empty;
    public string? CredentialRef { get; init; }
    public object? SessionRef { get; init; }
}

public sealed record ProviderStartResult
{
    public RunId RunId { get; init; }
    public bool Accepted { get; init; }
    public ErrorClass? ErrorClass { get; init; }
    public string? ErrorCode { get; init; }
}

public sealed record ProviderStatus
{
    public RunId RunId { get; init; }
    public DateTimeOffset? HeartbeatAt { get; init; }
    public DateTimeOffset? LastOutputAt { get; init; }
    public long? OutputBytes { get; init; }
    public long? TokenCount { get; init; }
    public bool SideEffectsStarted { get; init; }
}

public sealed record ProviderStopResult
{
    public RunId RunId { get; init; }
    public bool Stopped { get; init; }
}
