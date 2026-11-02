using System.Globalization;
using System.Text;
using System.Text.Json;

namespace AiResume.Worker.Resume;

/// <summary>
/// 原生 Claude 会话的尾部证据。只读文件尾部、只取元数据字段，绝不读取或输出对话正文。
///
/// 三件事都只能从会话自己身上取证，额度接口回答不了：
/// <list type="bullet">
/// <item><b>是不是被限额打断的</b>:限额打断时 Claude Code 写一条合成 assistant 记录
///   (<c>message.model="&lt;synthetic&gt;"</c>、<c>isApiErrorMessage=true</c>、<c>error="rate_limit"</c>,
///   2.1.28x 起另带 <c>quotaLimits.rateLimitType / resetsAt</c>)。最近一条主链 assistant 记录仍是它，
///   说明之后没人在任何客户端(桌面、终端、手机远程)把它续上过。</item>
/// <item><b>之后有没有人真的续上</b>:只有真实模型应答算数。限流之后再写下的其它合成错误记录
///   (<c>error</c> 为 <c>server_error</c> / <c>authentication_failed</c> / <c>unknown</c> 等:原生自动继续撞上 529、
///   令牌被拒……)说明续的那一下失败了，中断仍然成立;扫描时跳过它们，往前找最近一条真实应答或限流记录。</item>
/// <item><b>会话在用哪个模型</b>:最近一条非合成主链 assistant 记录的 <c>message.model</c>
///   ——服务端实际应答的模型 ID,比别名形式的 <c>requestedModel</c> 可靠。</item>
/// <item><b>用户给它的权限模式</b>:主链 user 记录的 <c>permissionMode</c>,只用于界面说明。
///   续跑不额外传权限参数:Claude Code 按用户自己的 settings(<c>permissions.defaultMode</c>)运行，
///   AI Resume 既不替用户提权，也不降权。</item>
/// </list>
/// 字段形状以 2026-10-30 本机 Claude Code 2.1.185–2.1.308 会话录制为准。
/// </summary>
public sealed record ClaudeSessionEvidence(
    string? LastModel,
    bool InterruptedByLimit,
    string? LimitType,
    DateTimeOffset? LimitResetsAtUtc,
    DateTimeOffset? LastAssistantAtUtc,
    string? LastAssistantUuid,
    string? PermissionMode,
    DateTimeOffset? LastReplyAtUtc = null)
{
    public const string SyntheticModel = "<synthetic>";

    // 先读尾部 1 MiB,不够再读 8 MiB 封顶:每个探测拍对每个项目都要读一次,
    // 不能为了一个可选字段(权限模式)去扫几十兆的大会话。
    private const int FirstWindowBytes = 1 << 20;
    private const long MaxWindowBytes = 8L << 20;

    /// <summary>读取会话尾部证据;文件不存在、读不了或尾部找不到任何主链 assistant 记录时返回 null。</summary>
    public static ClaudeSessionEvidence? TryRead(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            long length = stream.Length;
            for (long window = FirstWindowBytes; ; window *= 8)
            {
                long start = Math.Max(0, length - window);
                Scan scan = ScanTail(stream, start, length);
                if (scan.Complete || start == 0 || window >= MaxWindowBytes)
                {
                    return scan.LastAssistant is null ? null : scan.ToEvidence();
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static Scan ScanTail(FileStream stream, long start, long end)
    {
        var buffer = new byte[end - start];
        stream.Seek(start, SeekOrigin.Begin);
        int total = 0;
        while (total < buffer.Length)
        {
            int read = stream.Read(buffer, total, buffer.Length - total);
            if (read == 0) break;
            total += read;
        }

        string[] lines = Encoding.UTF8.GetString(buffer, 0, total).Split('\n');
        // 从文件中间截取时第一行多半是半截记录，丢掉;从头读时第一行完整。
        int first = start > 0 ? 1 : 0;
        var scan = new Scan();
        for (int i = lines.Length - 1; i >= first && !scan.Complete; i--)
        {
            string line = lines[i];
            if (line.Length == 0 ||
                (!line.Contains("\"assistant\"", StringComparison.Ordinal) &&
                 !line.Contains("\"permissionMode\"", StringComparison.Ordinal)))
            {
                continue;
            }

            try
            {
                using JsonDocument doc = JsonDocument.Parse(line);
                scan.Observe(doc.RootElement);
            }
            catch (JsonException)
            {
                // 正在被追加的末行或损坏行：跳过，不让一行坏数据否定整份证据。
            }
        }

        return scan;
    }

    private sealed class Scan
    {
        public JsonElementSnapshot? LastAssistant { get; private set; }
        public string? Model { get; private set; }
        public string? PermissionMode { get; private set; }

        /// <summary>最近一条真实模型应答的时间(不含任何合成记录)。</summary>
        public DateTimeOffset? ReplyAt { get; private set; }

        private bool _replySeen;

        public bool Complete => LastAssistant is not null && Model is not null && PermissionMode is not null;

        public void Observe(JsonElement row)
        {
            if (row.ValueKind != JsonValueKind.Object || IsTrue(row, "isSidechain") || IsTrue(row, "isMeta"))
            {
                return;
            }

            string? type = String(row, "type");
            if (type == "assistant")
            {
                string? model = row.TryGetProperty("message", out JsonElement message) && message.ValueKind == JsonValueKind.Object
                    ? String(message, "model")
                    : null;
                // 限流以外的合成记录(API 错误、本地占位应答)既不是新的中断，也不是有人续上:不能据此判「已在别处续上」。
                bool rateLimited = IsTrue(row, "isApiErrorMessage") && String(row, "error") == "rate_limit";
                bool otherSynthetic = !rateLimited && (IsTrue(row, "isApiErrorMessage") || model == SyntheticModel);
                if (!otherSynthetic)
                {
                    LastAssistant ??= JsonElementSnapshot.From(row);
                }

                if (rateLimited || otherSynthetic)
                {
                    return;
                }

                if (!_replySeen)
                {
                    _replySeen = true;
                    ReplyAt = JsonElementSnapshot.ParseTimestamp(row);
                }

                if (Model is null && model is { Length: > 0 })
                {
                    Model = model;
                }
            }
            else if (type == "user" && PermissionMode is null && String(row, "permissionMode") is { Length: > 0 } mode)
            {
                PermissionMode = mode;
            }
        }

        public ClaudeSessionEvidence ToEvidence()
        {
            JsonElementSnapshot last = LastAssistant!;
            return new ClaudeSessionEvidence(Model, last.RateLimited, last.LimitType, last.LimitResetsAt,
                last.Timestamp, last.Uuid, PermissionMode, ReplyAt);
        }
    }

    /// <summary>最近一条主链 assistant 记录里用得到的几个字段(JsonDocument 释放后仍可用)。</summary>
    private sealed record JsonElementSnapshot(
        bool RateLimited, string? LimitType, DateTimeOffset? LimitResetsAt, DateTimeOffset? Timestamp, string? Uuid)
    {
        public static JsonElementSnapshot From(JsonElement row)
        {
            bool limited = IsTrue(row, "isApiErrorMessage") && String(row, "error") == "rate_limit";
            string? limitType = null;
            DateTimeOffset? resetsAt = null;
            if (limited && row.TryGetProperty("quotaLimits", out JsonElement quota) && quota.ValueKind == JsonValueKind.Object)
            {
                limitType = String(quota, "rateLimitType");
                if (quota.TryGetProperty("resetsAt", out JsonElement reset) && reset.ValueKind == JsonValueKind.Number &&
                    reset.TryGetInt64(out long unix) && unix > 0 && unix < 253402300799)
                {
                    resetsAt = DateTimeOffset.FromUnixTimeSeconds(unix);
                }
            }

            return new JsonElementSnapshot(limited, limitType, resetsAt, ParseTimestamp(row), String(row, "uuid"));
        }

        public static DateTimeOffset? ParseTimestamp(JsonElement row) =>
            DateTimeOffset.TryParse(String(row, "timestamp"), CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out DateTimeOffset parsed)
                ? parsed
                : null;
    }

    private static bool IsTrue(JsonElement row, string name) =>
        row.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.True;

    private static string? String(JsonElement row, string name) =>
        row.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
