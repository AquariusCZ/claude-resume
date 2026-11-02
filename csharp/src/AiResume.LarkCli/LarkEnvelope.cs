using System.Text.Json;

namespace AiResume.LarkCli;

/// <summary>
/// lark-cli 结构化 JSON 信封(S3-A)。
/// 成功形状:{"ok":true,"identity":"bot","data":...},写在 stdout;
/// 错误形状:{"ok":false,"error":{"type","subtype","code","message","hint","log_id","retryable",...},"_notice":{...}},
/// **写在 stderr**(lark-cli 1.0.81 本机实测与官方 errs/ERROR_CONTRACT.md 一致)。
/// 解析输入必须先经脱敏;非法 JSON 或缺少 ok 字段返回 null(调用方按契约处置)。
/// 信封持有底层 JsonDocument(Data 访问期间不可释放),使用方应在访问完后 Dispose。
/// </summary>
public sealed class LarkEnvelope : IDisposable
{
    private readonly JsonDocument? _doc;

    public bool Ok { get; }

    /// <summary>成功时的 data 子树;失败时为 null。仅在本信封 Dispose 前有效。</summary>
    public JsonElement? Data { get; }

    /// <summary>bot / user;未给出时为 null。</summary>
    public string? Identity { get; }

    public string? ErrorType { get; }

    public string? ErrorSubtype { get; }

    public string? ErrorMessage { get; }

    public string? ErrorHint { get; }

    /// <summary>飞书业务/通用错误码(如 230020 频控、99991663 token 无效);没有时为 null。</summary>
    public long? ErrorCode { get; }

    /// <summary>飞书 log_id,向开放平台排查时用;不含凭据。</summary>
    public string? LogId { get; }

    public bool? Retryable { get; }

    public int? RetryAfterSeconds { get; }

    /// <summary>成功发送时的 data.message_id。</summary>
    public string? MessageId { get; }

    private LarkEnvelope(JsonDocument? doc, bool ok, JsonElement? data, string? identity, string? errorType, string? errorSubtype,
        string? errorMessage, string? errorHint, long? errorCode, string? logId, bool? retryable, int? retryAfterSeconds, string? messageId)
    {
        _doc = doc;
        Ok = ok;
        Data = data;
        Identity = identity;
        ErrorType = errorType;
        ErrorSubtype = errorSubtype;
        ErrorMessage = errorMessage;
        ErrorHint = errorHint;
        ErrorCode = errorCode;
        LogId = logId;
        Retryable = retryable;
        RetryAfterSeconds = retryAfterSeconds;
        MessageId = messageId;
    }

    public static LarkEnvelope? TryParse(string stdout)
    {
        if (string.IsNullOrWhiteSpace(stdout))
        {
            return null;
        }

        JsonDocument? doc = null;
        try
        {
            doc = JsonDocument.Parse(stdout);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("ok", out var okProp)
                || (okProp.ValueKind != JsonValueKind.True && okProp.ValueKind != JsonValueKind.False))
            {
                doc.Dispose();
                return null;
            }

            var ok = okProp.GetBoolean();
            JsonElement? data = root.TryGetProperty("data", out var d) ? d : null;
            string? messageId = data is { ValueKind: JsonValueKind.Object } payload ? Text(payload, "message_id") : null;

            string? errorType = null, errorSubtype = null, errorMessage = null, errorHint = null, logId = null;
            long? errorCode = null;
            bool? retryable = null;
            int? retryAfter = null;
            if (root.TryGetProperty("error", out var err) && err.ValueKind == JsonValueKind.Object)
            {
                errorType = Text(err, "type");
                errorSubtype = Text(err, "subtype");
                errorMessage = Text(err, "message");
                errorHint = Text(err, "hint");
                logId = Text(err, "log_id");
                errorCode = err.TryGetProperty("code", out var c) ? c.ValueKind switch
                {
                    JsonValueKind.Number when c.TryGetInt64(out long number) => number,
                    JsonValueKind.String when long.TryParse(c.GetString(), out long parsed) => parsed,
                    _ => null,
                } : null;
                retryable = err.TryGetProperty("retryable", out var r) && r.ValueKind is JsonValueKind.True or JsonValueKind.False
                    ? r.GetBoolean()
                    : null;
                retryAfter = err.TryGetProperty("retry_after_seconds", out var after) && after.ValueKind == JsonValueKind.Number &&
                    after.TryGetInt32(out int seconds) && seconds >= 0
                    ? seconds
                    : null;
            }

            return new LarkEnvelope(doc, ok, data, Text(root, "identity"), errorType, errorSubtype, errorMessage, errorHint,
                errorCode, logId, retryable, retryAfter, messageId);
        }
        catch (JsonException)
        {
            doc?.Dispose();
            return null;
        }
    }

    public void Dispose()
    {
        _doc?.Dispose();
    }

    private static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
