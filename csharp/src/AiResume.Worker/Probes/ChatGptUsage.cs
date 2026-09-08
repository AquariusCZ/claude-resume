using System.Text.Json;
using AiResume.Worker.Quota;

namespace AiResume.Worker.Probes;

/// <summary>Codex 0.148.0 backend-client /wham/usage 的账号窗口；不推断模型额度归属。</summary>
public static class ChatGptUsage
{
    public static UsageSnapshot Parse(string body)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(body);
            var buckets = new List<UsageBucket>();
            JsonElement root = doc.RootElement;
            if (root.TryGetProperty("rate_limit", out JsonElement main)) Add("ChatGPT", main, buckets);
            if (root.TryGetProperty("additional_rate_limits", out JsonElement extra) && extra.ValueKind == JsonValueKind.Array)
                foreach (JsonElement item in extra.EnumerateArray())
                    if (item.TryGetProperty("rate_limit", out JsonElement limit))
                        Add(item.TryGetProperty("limit_name", out JsonElement name) && name.ValueKind == JsonValueKind.String
                            ? name.GetString()! : "附加窗口", limit, buckets);
            bool spendBlocked = root.TryGetProperty("spend_control", out JsonElement spend) && spend.ValueKind == JsonValueKind.Object &&
                spend.TryGetProperty("reached", out JsonElement reached) && reached.ValueKind == JsonValueKind.True;
            bool typedBlock = root.TryGetProperty("rate_limit_reached_type", out JsonElement reason) && reason.ValueKind == JsonValueKind.Object;
            if (spendBlocked || typedBlock) buckets.Add(new("账号限额", false, true, Array.Empty<UsageWindow>()) { UnattributedLimitReached = true });
            return new("ChatGPT", DateTimeOffset.UtcNow, buckets, buckets.Count == 0 ? "订阅窗口未返回" : null);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException)
        {
            return UsageSnapshot.Unavailable("ChatGPT", DateTimeOffset.UtcNow, "订阅窗口格式未核实");
        }
    }

    private static void Add(string name, JsonElement limit, List<UsageBucket> buckets)
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
            long? reset = window.TryGetProperty("reset_at", out JsonElement resetValue) && resetValue.ValueKind == JsonValueKind.Number && resetValue.TryGetInt64(out long seconds) ? seconds : null;
            windows.Add(new(name + " · " + key, blocked ? "blocked" : allowed ? "allowed" : "unknown",
                Number(window, "limit_window_seconds") ?? 0, reset, Number(window, "reset_after_seconds"), percent));
        }
        buckets.Add(new(name, allowed, blocked || windows.Any(w => w.UsedPercent >= 100), windows));
    }

    private static int? Number(JsonElement parent, string key) => parent.TryGetProperty(key, out JsonElement value) &&
        value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out int result) ? result : null;
}
