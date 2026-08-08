using System.Text.Json;

namespace AiResume.LarkCli;

/// <summary>
/// lark-cli 结构化 JSON 信封(S3-A)。
/// 成功形状:{"ok":true,"data":...};错误形状:{"ok":false,"error":{"type":..,"subtype":..,"message":..,"hint":..}}。
/// 解析输入必须先经脱敏;非法 JSON 或缺少 ok 字段返回 null(调用方按契约处置)。
/// 信封持有底层 JsonDocument(Data 访问期间不可释放),使用方应在访问完后 Dispose。
/// </summary>
public sealed class LarkEnvelope : IDisposable
{
    private readonly JsonDocument? _doc;

    public bool Ok { get; }

    /// <summary>成功时的 data 子树;失败时为 null。仅在本信封 Dispose 前有效。</summary>
    public JsonElement? Data { get; }

    public string? ErrorType { get; }

    public string? ErrorSubtype { get; }

    public string? ErrorMessage { get; }

    public string? ErrorHint { get; }

    private LarkEnvelope(JsonDocument? doc, bool ok, JsonElement? data, string? errorType, string? errorSubtype, string? errorMessage, string? errorHint)
    {
        _doc = doc;
        Ok = ok;
        Data = data;
        ErrorType = errorType;
        ErrorSubtype = errorSubtype;
        ErrorMessage = errorMessage;
        ErrorHint = errorHint;
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
                return null;
            }

            var ok = okProp.GetBoolean();
            JsonElement? data = root.TryGetProperty("data", out var d) ? d : null;

            string? errorType = null, errorSubtype = null, errorMessage = null, errorHint = null;
            if (root.TryGetProperty("error", out var err) && err.ValueKind == JsonValueKind.Object)
            {
                errorType = err.TryGetProperty("type", out var t) ? t.GetString() : null;
                errorSubtype = err.TryGetProperty("subtype", out var s) ? s.GetString() : null;
                errorMessage = err.TryGetProperty("message", out var m) ? m.GetString() : null;
                errorHint = err.TryGetProperty("hint", out var h) ? h.GetString() : null;
            }

            return new LarkEnvelope(doc, ok, data, errorType, errorSubtype, errorMessage, errorHint);
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
}
