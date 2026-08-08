using System.Text.Json;
using System.Text.RegularExpressions;

namespace AiResume.Secrets;

/// <summary>
/// 机密脱敏器(S2-F;复刻 D-012/D-013 语义)。
///
/// 双重兜底:
/// 1. 键名 allowlist(主防线):序列化/日志结构化对象时,敏感键名(apiKey/appSecret/token/
///    password/credential 等词段)的值整体置换为 [redacted:&lt;key&gt;],非敏感键原样保留。
/// 2. 值模式扫描(次防线):自由文本(异常 message、拼接字符串)中已知机密形状
///    (sk- 前缀、Bearer token、JWT、ghp_ 等)置换为 [redacted]。
///
/// 使用约定:任何写日志/事件/异常/输出的路径都必须经过本类;
/// 机密明文绝不进入 formatter 以外的任何参数。
/// </summary>
public static class SecretRedactor
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>敏感键名词段(小写匹配):命中即整值置换。</summary>
    private static readonly string[] SensitiveKeyWords =
    {
        "apikey", "api_key", "apisecret", "appsecret", "clientsecret", "secret",
        "token", "password", "passwd", "credential", "privatekey", "authorization",
    };

    /// <summary>自由文本中的机密形状(次防线;保守,避免误伤普通文本)。</summary>
    private static readonly Regex[] SensitiveValuePatterns =
    {
        new(@"sk-[A-Za-z0-9_\-]{12,}", RegexOptions.Compiled),                       // OpenAI/DeepSeek 风格
        new(@"\bBearer\s+[A-Za-z0-9._\-]{16,}", RegexOptions.Compiled),               // Bearer token
        new(@"\bghp_[A-Za-z0-9]{20,}", RegexOptions.Compiled),                        // GitHub PAT
        new(@"\bxox[baprs]-[A-Za-z0-9-]{20,}", RegexOptions.Compiled),                // Slack token
        new(@"eyJ[A-Za-z0-9_\-]{20,}\.[A-Za-z0-9_\-]{10,}\.[A-Za-z0-9_\-]{10,}", RegexOptions.Compiled), // JWT
    };

    /// <summary>键名是否携带机密(词段匹配,大小写不敏感)。</summary>
    public static bool IsSensitiveKey(string key)
    {
        if (string.IsNullOrEmpty(key))
        {
            return false;
        }

        string lowered = key.ToLowerInvariant();
        return SensitiveKeyWords.Any(lowered.Contains);
    }

    /// <summary>按键名策略置换值:敏感键 → [redacted:&lt;key&gt;],否则原样(值仍过文本兜底)。</summary>
    public static object? RedactValue(string key, object? value)
    {
        if (IsSensitiveKey(key))
        {
            return $"[redacted:{key}]";
        }

        return value is string text ? RedactText(text) : value;
    }

    /// <summary>自由文本脱敏:已知机密形状置换为 [redacted];无命中则原样返回。</summary>
    public static string RedactText(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return text ?? string.Empty;
        }

        string result = text;
        foreach (Regex pattern in SensitiveValuePatterns)
        {
            result = pattern.Replace(result, "[redacted]");
        }

        return result;
    }

    /// <summary>
    /// 结构化序列化(事件/日志 data 输出统一入口):
    /// 递归遍历 JSON 树,敏感键名值整体置换 + 字符串值过文本兜底;输出绝不含明文。
    /// </summary>
    public static string SerializeSafe(object value)
    {
        ArgumentNullException.ThrowIfNull(value);
        using JsonDocument document = JsonSerializer.SerializeToDocument(value, JsonOptions);
        object? redacted = RedactNode(document.RootElement);
        return JsonSerializer.Serialize(redacted, JsonOptions);
    }

    private static object? RedactNode(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.Object => element.EnumerateObject().ToDictionary(
            property => property.Name,
            property => IsSensitiveKey(property.Name)
                ? $"[redacted:{property.Name}]"
                : RedactNode(property.Value)),
        JsonValueKind.Array => element.EnumerateArray().Select(RedactNode).ToArray(),
        JsonValueKind.String => RedactText(element.GetString()),
        JsonValueKind.Number => element.GetDecimal(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Null => null,
        _ => null,
    };
}
