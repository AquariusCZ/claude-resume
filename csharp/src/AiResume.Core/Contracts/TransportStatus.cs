namespace AiResume.Core.Contracts;

public sealed record TransportStatus
{
    public bool Running { get; init; }
    public int ConnectedClients { get; init; }
    public string ProtocolVersion { get; init; } = "1";
}
