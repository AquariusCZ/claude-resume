using System.Text.RegularExpressions;

namespace AiResume.LarkCli;

/// <summary>
/// 输出脱敏(S3-A):已知机密值全文置换 + 含授权/验证语义的 URL 整体置换。
/// 普通文档/消息 URL 保留;授权/验证链接(可能携带 code/token)整体替换为占位符。
/// </summary>
public sealed class LarkRedactor
{
    private static readonly Regex UrlPattern = new(@"https?://[^\s""]+", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly string[] AuthUrlMarkers =
    {
        "auth", "authorize", "authorization", "verify", "verification", "token", "approval",
    };

    private readonly IReadOnlyList<string> _knownSecrets;

    public LarkRedactor(IEnumerable<string>? knownSecrets = null)
    {
        _knownSecrets = (knownSecrets ?? Array.Empty<string>())
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .ToArray();
    }

    public string Redact(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return text;
        }

        var result = text;
        foreach (var secret in _knownSecrets)
        {
            result = result.Replace(secret, "[REDACTED]", StringComparison.Ordinal);
        }

        result = UrlPattern.Replace(result, static m =>
        {
            var url = m.Value;
            var lower = url.ToLowerInvariant();
            return AuthUrlMarkers.Any(marker => lower.Contains(marker, StringComparison.Ordinal))
                ? "[REDACTED-URL]"
                : url;
        });

        return result;
    }
}
