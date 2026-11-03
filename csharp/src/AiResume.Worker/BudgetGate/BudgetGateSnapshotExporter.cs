using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AiResume.Core;
using AiResume.Worker.Quota;

namespace AiResume.Worker.BudgetGate;

/// <summary>
/// 把合并后的 OAuth 额度快照导出成预检 mod 读的只读文件 <c>quota-snapshot.json</c>(ADR-0008 §C)。
///
/// mod 自己看得到 5 小时与 7 天,看不到按模型族的周额度(weekly_scoped),这份文件补上后者,并在 mod 还没拿到读数时兜底 5h/7d。
/// **只挑字段导出**:直接序列化 <see cref="UsageSnapshot"/> 会带出账号指纹,这里一个凭据或账号标识都不写。
/// GUI 与 Worker 两个进程都会在拿到真实 OAuth 读数时调用:临时文件 + 原子替换,并按捕获时间单调——旧的不覆盖新的。
/// 导出失败只返回 false,绝不影响额度主流程。
/// </summary>
public static class BudgetGateSnapshotExporter
{
    public static bool TryExport(UsageSnapshot snapshot, string path, DateTimeOffset now)
    {
        try
        {
            if (snapshot.EvidenceSource != UsageEvidenceSource.OAuth || !snapshot.HasData)
                return false;
            // 旧的不覆盖新的;但文件里的捕获时间若在未来(时钟回拨后留下的),视为坏值,照常覆盖。
            if (ReadCapturedAt(path) is { } existing && existing >= snapshot.CapturedAt && existing <= now.AddMinutes(5))
                return false;

            var windows = new JsonArray();
            var scoped = new JsonArray();
            foreach (UsageWindow w in snapshot.Buckets.SelectMany(b => b.Windows))
            {
                if (w.UsedPercent is null)
                    continue;
                string? reset = w.ResetAtUnix is { } unix ? DateTimeOffset.FromUnixTimeSeconds(unix).ToString("O", CultureInfo.InvariantCulture) : null;
                string observed = snapshot.CapturedAt.ToString("O", CultureInfo.InvariantCulture);
                if (w.Name is "five_hour" or "seven_day")
                {
                    // 承接值是同一周期里更早的服务端读数:mod 拿它兜底会偏低(少拦),界面对表也会把它当成"刚观测"。不导出。
                    if (w.CarriedForward)
                        continue;
                    windows.Add(new JsonObject
                    {
                        ["kind"] = w.Name,
                        ["percentUsed"] = w.UsedPercent,
                        ["resetsAt"] = reset,
                        ["observedAt"] = observed,
                        ["carried"] = w.CarriedForward,
                    });
                }
                else if (w.Name.StartsWith("weekly_scoped:", StringComparison.Ordinal))
                {
                    string display = w.Name["weekly_scoped:".Length..];
                    scoped.Add(new JsonObject
                    {
                        // 认不出族的窗口 family 为 null:mod 不拿它做判断(被动发现兜底),只供界面展示。
                        ["family"] = ClaudeModelFamilies.TryNormalizeScopeName(display, out string family) ? family : null,
                        ["name"] = display,
                        ["percentUsed"] = w.UsedPercent,
                        ["resetsAt"] = reset,
                        ["observedAt"] = observed,
                        ["carried"] = w.CarriedForward,
                    });
                }
            }

            var root = new JsonObject
            {
                ["schema"] = 1,
                ["exportedAt"] = now.ToString("O", CultureInfo.InvariantCulture),
                ["capturedAt"] = snapshot.CapturedAt.ToString("O", CultureInfo.InvariantCulture),
                ["windows"] = windows,
                ["scoped"] = scoped,
            };
            string? dir = Path.GetDirectoryName(path);
            if (string.IsNullOrEmpty(dir))
                return false;
            Directory.CreateDirectory(dir);
            string temp = path + ".tmp-" + Guid.NewGuid().ToString("N");
            try
            {
                File.WriteAllText(temp, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false));
                File.Move(temp, path, overwrite: true);
            }
            catch
            {
                try { if (File.Exists(temp)) File.Delete(temp); } catch { /* 忽略清理失败 */ }
                throw;
            }

            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
        {
            return false;
        }
    }

    private static DateTimeOffset? ReadCapturedAt(string path)
    {
        try
        {
            if (!File.Exists(path))
                return null;
            string? text = JsonNode.Parse(BudgetGateControl.ReadShared(path))?["capturedAt"]?.GetValue<string>();
            return DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out DateTimeOffset t) ? t : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>界面对表用:读回导出文件里的 5h/7d(只读;读不出返回空)。</summary>
    public static IReadOnlyList<WindowReading> ReadWindows(string path)
    {
        try
        {
            if (!File.Exists(path) || JsonNode.Parse(BudgetGateControl.ReadShared(path))?["windows"] is not JsonArray arr)
                return Array.Empty<WindowReading>();
            var list = new List<WindowReading>();
            foreach (JsonNode? n in arr)
            {
                if (n?["kind"]?.GetValue<string>() is not { } kind || n["percentUsed"] is not JsonValue p || !p.TryGetValue(out double percent))
                    continue;
                list.Add(new WindowReading(kind, percent, Parse(n["resetsAt"]), Parse(n["observedAt"])));
            }

            return list;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
        {
            return Array.Empty<WindowReading>();
        }

        static DateTimeOffset? Parse(JsonNode? node) =>
            node is JsonValue v && v.TryGetValue(out string? s) &&
            DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out DateTimeOffset t) ? t : null;
    }
}
