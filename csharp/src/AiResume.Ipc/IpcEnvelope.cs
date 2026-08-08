using System.Text.Json;
using System.Text.Json.Serialization;

namespace AiResume.Ipc;

/// <summary>
/// Named Pipe 帧的跨进程信封(JSON 载荷)。
/// 键名 snake_case 对齐 docs/EVENT-CONTRACTS.md;envelopeVersion 唯一接受 "1",
/// 未知版本由服务端回错误帧后断连。
/// </summary>
public sealed record IpcEnvelope
{
    public const string EnvelopeVersionValue = "1";

    [JsonPropertyName("envelopeVersion")]
    public string EnvelopeVersion { get; init; } = EnvelopeVersionValue;

    /// <summary>命令/事件类型(见 <see cref="PipeProtocol"/> 的 Command*/Response* 常量)。</summary>
    [JsonPropertyName("type")]
    public string Type { get; init; } = string.Empty;

    /// <summary>请求方生成的关联 ID;应答原样带回,用于并发客户端区分响应。</summary>
    [JsonPropertyName("correlationId")]
    public string? CorrelationId { get; init; }

    /// <summary>类型化内容;禁止机密与完整命令行(契约见 docs/EVENT-CONTRACTS.md 第 14 节)。</summary>
    [JsonPropertyName("payload")]
    public JsonElement? Payload { get; init; }
}

/// <summary>结构化错误帧的 payload(code/message 为稳定机器码与脱敏短消息)。</summary>
public sealed record IpcError
{
    [JsonPropertyName("code")]
    public string Code { get; init; } = string.Empty;

    [JsonPropertyName("message")]
    public string Message { get; init; } = string.Empty;
}

/// <summary>status 命令的 payload:目标 runId。</summary>
public sealed record IpcStatusPayload
{
    [JsonPropertyName("runId")]
    public string RunId { get; init; } = string.Empty;
}
