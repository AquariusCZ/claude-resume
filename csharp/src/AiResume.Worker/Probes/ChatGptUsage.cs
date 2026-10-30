using System.Globalization;
using System.Text.Json;
using AiResume.Worker.Quota;

namespace AiResume.Worker.Probes;

/// <summary>
/// ChatGPT 官方 <c>/backend-api/wham/usage</c> 的解析。
///
/// 2026-10-12 本机真实录制(prolite 计划)的形状:
/// <code>
/// rate_limit: { allowed, limit_reached, primary_window{used_percent,limit_window_seconds,reset_after_seconds,reset_at}, secondary_window:null }
/// additional_rate_limits: null | [{limit_name, rate_limit{...}}]
/// model_usage: { "&lt;slug&gt;": { available, available_at, credits_would_enable } }
/// spend_control: { reached, individual_limit }
/// rate_limit_reached_type: null | 任何非 null 值(形状未录制,保守地一律视为账号级限流)
/// </code>
/// 账号窗口进 <see cref="AccountBucket"/>;<c>model_usage</c> 逐模型进 <see cref="ModelBucket"/>,
/// 窗口名 <c>model:&lt;slug&gt;</c>,status 只有 allowed/blocked —— 它就是「这个模型此刻能不能跑」,
/// 也是自动续跑能把账号窗口对应到目标模型的 join key(ADR-0004 当时的上游缺口已由服务端补齐)。
/// </summary>
public static class ChatGptUsage
{
    public const string ProviderName = "ChatGPT";
    public const string AccountBucket = "ChatGPT";
    public const string ModelBucket = "model_usage";
    public const string ModelWindowPrefix = "model:";

    public static UsageSnapshot Parse(string body)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        try
        {
            using JsonDocument doc = JsonDocument.Parse(body);
            JsonElement root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return UsageSnapshot.Unavailable(ProviderName, now, "订阅窗口格式未核实");
            }

            var buckets = new List<UsageBucket>();
            if (root.TryGetProperty("rate_limit", out JsonElement main)) Add(AccountBucket, main, buckets, now);
            if (root.TryGetProperty("additional_rate_limits", out JsonElement extra) && extra.ValueKind == JsonValueKind.Array)
                foreach (JsonElement item in extra.EnumerateArray())
                    if (item.TryGetProperty("rate_limit", out JsonElement limit))
                        Add(item.TryGetProperty("limit_name", out JsonElement name) && name.ValueKind == JsonValueKind.String
                            ? name.GetString()! : "附加窗口", limit, buckets, now);
            bool spendBlocked = root.TryGetProperty("spend_control", out JsonElement spend) && spend.ValueKind == JsonValueKind.Object &&
                spend.TryGetProperty("reached", out JsonElement reached) && reached.ValueKind == JsonValueKind.True;
            bool typedBlock = root.TryGetProperty("rate_limit_reached_type", out JsonElement reason) &&
                reason.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined);
            if (spendBlocked || typedBlock) buckets.Add(new("账号限额", false, true, Array.Empty<UsageWindow>()) { UnattributedLimitReached = true });
            if (root.TryGetProperty("model_usage", out JsonElement models) && models.ValueKind == JsonValueKind.Object)
            {
                var windows = new List<UsageWindow>();
                foreach (JsonProperty model in models.EnumerateObject())
                {
                    if (model.Value.ValueKind != JsonValueKind.Object || !ProviderModels.IsSafeId(model.Name)) continue;
                    bool available = model.Value.TryGetProperty("available", out JsonElement flag) && flag.ValueKind == JsonValueKind.True;
                    long? availableAt = model.Value.TryGetProperty("available_at", out JsonElement at) ? ParseInstant(at) : null;
                    int? after = availableAt is { } unix ? (int)Math.Min(Math.Max(0, unix - now.ToUnixTimeSeconds()), int.MaxValue) : null;
                    windows.Add(new UsageWindow(ModelWindowPrefix + model.Name, available ? "allowed" : "blocked", 0, availableAt, after, null));
                }
                // 模型可用性是目标模型的门禁,不是账号级限流:Allowed/LimitReached 保持中性,
                // 由 EvaluateChatGpt 按目标模型逐一判断,避免「某个没在用的模型不可用」把整行标成限额。
                if (windows.Count > 0) buckets.Add(new(ModelBucket, true, false, windows));
            }
            return new(ProviderName, now, buckets, buckets.Count == 0 ? "订阅窗口未返回" : null);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException)
        {
            return UsageSnapshot.Unavailable(ProviderName, now, "订阅窗口格式未核实");
        }
    }

    /// <summary>目标模型在 <c>model_usage</c> 里的可用性窗口;服务端没有列出该模型时为 null。</summary>
    public static UsageWindow? FindModelWindow(UsageSnapshot snapshot, string modelId) =>
        snapshot.Buckets.Where(b => b.Name == ModelBucket).SelectMany(b => b.Windows)
            .FirstOrDefault(w => w.Name.Equals(ModelWindowPrefix + modelId, StringComparison.Ordinal));

    /// <summary>把窗口长度翻成人看的标签:7D / 5H / 12H。</summary>
    public static string DescribeWindow(int seconds) => seconds switch
    {
        >= 7 * 24 * 3600 - 3600 => "7D",
        >= 3600 => (seconds / 3600).ToString(CultureInfo.InvariantCulture) + "H",
        > 0 => seconds.ToString(CultureInfo.InvariantCulture) + "s",
        _ => "窗口",
    };

    private static void Add(string name, JsonElement limit, List<UsageBucket> buckets, DateTimeOffset now)
    {
        if (limit.ValueKind != JsonValueKind.Object) return;
        var windows = new List<UsageWindow>();
        bool blocked = limit.TryGetProperty("limit_reached", out JsonElement reached) && reached.ValueKind == JsonValueKind.True;
        bool allowed = limit.TryGetProperty("allowed", out JsonElement available) && available.ValueKind == JsonValueKind.True;
        foreach (string key in new[] { "primary_window", "secondary_window" })
        {
            if (!limit.TryGetProperty(key, out JsonElement window) || window.ValueKind != JsonValueKind.Object) continue;
            int? percent = Number(window, "used_percent");
            if (percent is < 0 or > 100) percent = null;
            long? reset = window.TryGetProperty("reset_at", out JsonElement resetValue) ? ParseInstant(resetValue) : null;
            int seconds = Number(window, "limit_window_seconds") ?? 0;
            string label = (key == "primary_window" ? "primary" : "secondary") + " · " + DescribeWindow(seconds);
            windows.Add(new(name == AccountBucket ? label : name + " · " + label,
                blocked || percent >= 100 ? "blocked" : allowed ? "allowed" : "unknown",
                seconds, reset, Number(window, "reset_after_seconds"), percent));
        }
        buckets.Add(new(name, allowed && !blocked, blocked || windows.Any(w => w.UsedPercent >= 100), windows));
    }

    /// <summary>
    /// JSON 数字 → 整数。上游 <c>used_percent</c> 是 f64(录制里恰好是整数 0),小数按四舍五入,
    /// 不能因为服务端给了 12.5 就把窗口当成「未报告」——那会让放行永远停在 unknown。超出 int 的值当未报告。
    /// </summary>
    private static int? Number(JsonElement parent, string key)
    {
        if (!parent.TryGetProperty(key, out JsonElement value) || value.ValueKind != JsonValueKind.Number) return null;
        if (value.TryGetInt32(out int exact)) return exact;
        if (!value.TryGetDouble(out double raw) || double.IsNaN(raw) || double.IsInfinity(raw)) return null;
        double rounded = Math.Round(raw, MidpointRounding.AwayFromZero);
        return rounded is >= int.MinValue and <= int.MaxValue ? (int)rounded : null;
    }

    /// <summary>epoch 秒数字或 ISO 8601 字符串;其它形状一律当未报告。</summary>
    private static long? ParseInstant(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Number when value.TryGetInt64(out long seconds) => seconds,
        JsonValueKind.String when DateTimeOffset.TryParse(value.GetString(), CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal, out DateTimeOffset parsed) => parsed.ToUnixTimeSeconds(),
        _ => null,
    };
}
