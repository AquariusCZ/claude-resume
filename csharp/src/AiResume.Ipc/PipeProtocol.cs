namespace AiResume.Ipc;

/// <summary>
/// Named Pipe 帧协议常量与命令/响应类型(Stage 2-C 固化实现)。
/// 帧格式:4 字节小端长度前缀 + UTF-8 JSON;信封 envelopeVersion 唯一接受 <see cref="Version"/>。
/// </summary>
public static class PipeProtocol
{
    /// <summary>长度前缀帧的头部字节数。</summary>
    public const int HeaderBytes = 4;

    /// <summary>单帧上限;超长帧直接断连,不解析。</summary>
    public const int MaxFrameBytes = 1024 * 1024;

    /// <summary>协议版本(信封 envelopeVersion 唯一接受值)。</summary>
    public const string Version = "1";

    /// <summary>pipe 名前缀;实际名 = 前缀 + 当前用户 SID 的 SHA256 前 16 位。</summary>
    public const string PipeNamePrefix = "airesume-";

    // 请求命令类型。
    public const string CommandPing = "ping";
    public const string CommandStart = "start";
    public const string CommandStatus = "status";
    public const string CommandCancel = "cancel";
    public const string CommandListRuns = "list-runs";

    // 应答类型。
    public const string ResponsePong = "pong";
    public const string ResponseStarted = "started";
    public const string ResponseStatus = "status";
    public const string ResponseCancelled = "cancelled";
    public const string ResponseRuns = "runs";
    public const string ResponseError = "error";

    // 结构化错误码(错误帧 payload.code)。
    public const string ErrorUnsupportedEnvelopeVersion = "unsupported_envelope_version";
    public const string ErrorUnknownCommand = "unknown_command";
    public const string ErrorMalformedPayload = "malformed_payload";
    public const string ErrorInternal = "internal";
}
