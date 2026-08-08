using System.Globalization;
using System.Text.Json;

namespace AiResume.Worker.Notifications;

/// <summary>一次投递的结果。</summary>
public enum NotifyOutcome { Sent, Duplicate, Malformed, Failed, Skipped }

public sealed record NotifyItemResult(string EventId, NotifyOutcome Outcome, string? Detail);

public sealed record NotifySweepResult(
    int Total, int Sent, int Duplicate, int Malformed, int Failed, int Skipped,
    IReadOnlyList<NotifyItemResult> Items);

/// <summary>
/// 完成通知投递端(S12):消费 <c>completion-events</c> 队列,经 lark-cli 发飞书消息。
///
/// 为什么用 lark-cli 而不是自己发 HTTP(盘点结论,必须保留):
/// - 项目规则:新增飞书消息能力**必须优先调用官方 lark-cli**,不得手写同类 SDK 请求。
/// - 本机 lark-cli 已用**同一个飞书应用**授权(`auth status` 显示 bot 身份 ready)。
/// - 仓库已有 <see cref="AiResume.LarkCli.LarkCliInvoker"/> 封装(进程启动、超时、脱敏、信封解析),
///   **复用它**,不要新写进程调用。
/// - 实测命令形状(`--dry-run` 验证过,请求体为 POST /open-apis/im/v1/messages):
///   <c>lark-cli im +messages-send --as bot --user-id &lt;ou_...&gt; --text &lt;文本&gt;
///   --idempotency-key &lt;eventId&gt; --format json</c>
/// - <c>--idempotency-key</c> 最长 50 字符,服务端据此防重复投递;eventId 是 16 位十六进制,
///   正好可直接用。这是在本地去重之外的**第二道**保险。
///
/// 本类只负责队列扫描、去重、文件生命周期;实际投递通过注入的 <c>send</c> 委托完成,
/// 测试注入假实现,绝不触碰真实网络。
/// </summary>
public sealed class CompletionNotifier
{
    private static readonly TimeSpan DuplicateWindow = TimeSpan.FromDays(7);

    private readonly string _eventsDir;
    private readonly string _seenPath;
    private readonly Func<string, string, string, CancellationToken, Task<bool>> _send;
    private readonly Func<DateTimeOffset> _now;

    public CompletionNotifier(
        string eventsDir,
        string seenPath,
        Func<string, string, string, CancellationToken, Task<bool>> send,
        Func<DateTimeOffset>? now = null)
    {
        _eventsDir = eventsDir;
        _seenPath = seenPath;
        _send = send ?? throw new ArgumentNullException(nameof(send));
        _now = now ?? (() => DateTimeOffset.UtcNow);
    }

    /// <summary>扫一遍队列并投递。receiverOpenId 为空白时全部记 Skipped 且不删文件。</summary>
    public async Task<NotifySweepResult> SweepAsync(string receiverOpenId, CancellationToken ct = default)
    {
        var items = new List<NotifyItemResult>();
        int sent = 0, duplicate = 0, malformed = 0, failed = 0, skipped = 0;

        // 目录不存在时返回全 0,不创建目录、不抛。
        if (!Directory.Exists(_eventsDir))
        {
            return new NotifySweepResult(0, 0, 0, 0, 0, 0, items);
        }

        // 加载去重表;文件不存在/空/无法解析 → 空表,不得抛。
        Dictionary<string, DateTimeOffset> seen = LoadSeen();

        // 按文件名 Ordinal 升序处理,保证确定性。
        string[] files;
        try
        {
            files = Directory.GetFiles(_eventsDir, "*.json")
                .Select(Path.GetFileName)
                .Where(f => f is not null)
                .OrderBy(f => f!, StringComparer.Ordinal)
                .ToArray()!;
        }
        catch (IOException)
        {
            // 目录枚举失败:本轮无结果,不抛。
            return new NotifySweepResult(0, 0, 0, 0, 0, 0, items);
        }
        catch (UnauthorizedAccessException)
        {
            return new NotifySweepResult(0, 0, 0, 0, 0, 0, items);
        }

        bool seenChanged = false;

        foreach (string fileName in files)
        {
            ct.ThrowIfCancellationRequested();
            string fullPath = Path.Combine(_eventsDir, fileName);

            // 1. 读文件文本;读失败 → Failed,保留文件,下轮重试。
            string text;
            try
            {
                text = File.ReadAllText(fullPath);
            }
            catch (IOException ex)
            {
                items.Add(new NotifyItemResult(fileName, NotifyOutcome.Failed, ex.Message));
                failed++;
                continue;
            }
            catch (UnauthorizedAccessException ex)
            {
                items.Add(new NotifyItemResult(fileName, NotifyOutcome.Failed, ex.Message));
                failed++;
                continue;
            }

            // 2. 解析 JSON;失败或缺 eventId/cwd → Malformed,移入 malformed\。
            string? eventId;
            string? cwd;
            string? source;
            DateTimeOffset? atUtc;
            try
            {
                using var doc = JsonDocument.Parse(text);
                JsonElement root = doc.RootElement;
                eventId = root.TryGetProperty("eventId", out JsonElement e) ? e.GetString() : null;
                cwd = root.TryGetProperty("cwd", out JsonElement c) ? c.GetString() : null;
                source = root.TryGetProperty("source", out JsonElement s) ? s.GetString() : null;
                string? atStr = root.TryGetProperty("atUtc", out JsonElement a) ? a.GetString() : null;
                atUtc = ParseUtc(atStr);
            }
            catch (JsonException)
            {
                MoveToMalformed(fullPath, fileName);
                items.Add(new NotifyItemResult(fileName, NotifyOutcome.Malformed, "JSON 解析失败"));
                malformed++;
                continue;
            }

            if (string.IsNullOrWhiteSpace(eventId) || string.IsNullOrWhiteSpace(cwd))
            {
                MoveToMalformed(fullPath, fileName);
                items.Add(new NotifyItemResult(fileName, NotifyOutcome.Malformed, "缺少 eventId 或 cwd"));
                malformed++;
                continue;
            }

            // 3. 查去重表:eventId 已存在且记录时间在 7 天内 → Duplicate,删除文件。
            DateTimeOffset nowUtc = _now().ToUniversalTime();
            if (seen.TryGetValue(eventId, out DateTimeOffset recorded) &&
                nowUtc - recorded < DuplicateWindow)
            {
                TryDelete(fullPath);
                items.Add(new NotifyItemResult(eventId, NotifyOutcome.Duplicate, null));
                duplicate++;
                continue;
            }

            // 4. receiverOpenId 为空白 → Skipped,保留文件。
            if (string.IsNullOrWhiteSpace(receiverOpenId))
            {
                items.Add(new NotifyItemResult(eventId, NotifyOutcome.Skipped, null));
                skipped++;
                continue;
            }

            // 5. 投递。(变量名不能再叫 text —— 上面读文件时已占用。)
            string message = BuildText(cwd, source, atUtc, nowUtc);
            try
            {
                bool ok = await _send(receiverOpenId, message, eventId, ct);
                if (ok)
                {
                    seen[eventId] = nowUtc;
                    seenChanged = true;
                    TryDelete(fullPath);
                    items.Add(new NotifyItemResult(eventId, NotifyOutcome.Sent, null));
                    sent++;
                }
                else
                {
                    items.Add(new NotifyItemResult(eventId, NotifyOutcome.Failed, null));
                    failed++;
                }
            }
            catch (Exception ex)
            {
                // 不得让异常逃出 SweepAsync:一条坏事件不能中断整轮。
                items.Add(new NotifyItemResult(eventId, NotifyOutcome.Failed, ex.Message));
                failed++;
            }
        }

        // 剪枝:丢弃记录时间早于 now - 7 天的条目。
        DateTimeOffset cutoff = _now().ToUniversalTime() - DuplicateWindow;
        var stale = seen.Where(kv => kv.Value < cutoff).Select(kv => kv.Key).ToList();
        if (stale.Count > 0)
        {
            foreach (string key in stale)
            {
                seen.Remove(key);
            }

            seenChanged = true;
        }

        // 只有本轮有变化时才写盘,避免每轮无谓 IO。
        if (seenChanged)
        {
            WriteSeen(seen);
        }

        return new NotifySweepResult(
            items.Count, sent, duplicate, malformed, failed, skipped, items);
    }

    /// <summary>构造通知文本:✅ &lt;项目名&gt; 已完成 / &lt;source&gt; · &lt;本地时间 HH:mm&gt;。</summary>
    private static string BuildText(string cwd, string? source, DateTimeOffset? atUtc, DateTimeOffset nowUtc)
    {
        string projectName = Path.GetFileName(cwd.TrimEnd('\\', '/'));
        if (string.IsNullOrEmpty(projectName))
        {
            projectName = cwd;
        }

        DateTimeOffset localTime = atUtc?.ToLocalTime() ?? nowUtc.ToLocalTime();
        string sourceText = string.IsNullOrWhiteSpace(source) ? "unknown" : source;

        return $"✅ {projectName} 已完成\n{sourceText} · {localTime:HH:mm}";
    }

    /// <summary>解析 ISO8601 UTC;失败返回 null。</summary>
    private static DateTimeOffset? ParseUtc(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        // 用 InvariantCulture + AdjustToUniversal | AssumeUniversal,
        // 否则在非 UTC 时区的机器上七天边界会漂。
        if (DateTimeOffset.TryParse(
                value,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal,
                out DateTimeOffset result))
        {
            return result;
        }

        return null;
    }

    /// <summary>把坏事件移入 malformed\ 子目录;目标已存在则加 Guid 后缀。</summary>
    private void MoveToMalformed(string fullPath, string fileName)
    {
        try
        {
            string malformedDir = Path.Combine(_eventsDir, "malformed");
            Directory.CreateDirectory(malformedDir);
            string dest = Path.Combine(malformedDir, fileName);
            if (File.Exists(dest))
            {
                dest = Path.Combine(malformedDir, Path.GetFileNameWithoutExtension(fileName) + "-" + Guid.NewGuid().ToString("N") + Path.GetExtension(fileName));
            }

            File.Move(fullPath, dest);
        }
        catch (IOException)
        {
            // 移动失败:保留原文件,下轮再试。
        }
        catch (UnauthorizedAccessException)
        {
            // 同上。
        }
    }

    /// <summary>加载去重表;文件不存在/空/无法解析 → 空表,不抛。</summary>
    private Dictionary<string, DateTimeOffset> LoadSeen()
    {
        var result = new Dictionary<string, DateTimeOffset>(StringComparer.Ordinal);
        if (!File.Exists(_seenPath))
        {
            return result;
        }

        try
        {
            string json = File.ReadAllText(_seenPath);
            if (string.IsNullOrWhiteSpace(json))
            {
                return result;
            }

            var parsed = JsonSerializer.Deserialize<Dictionary<string, string>>(json);
            if (parsed is null)
            {
                return result;
            }

            foreach (KeyValuePair<string, string> kv in parsed)
            {
                if (DateTimeOffset.TryParse(
                        kv.Value,
                        CultureInfo.InvariantCulture,
                        DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal,
                        out DateTimeOffset ts))
                {
                    result[kv.Key] = ts;
                }
            }
        }
        catch (IOException)
        {
            // 读失败视同空表。
        }
        catch (UnauthorizedAccessException)
        {
            // 同上。
        }
        catch (JsonException)
        {
            // 损坏视同空表。
        }

        return result;
    }

    /// <summary>原子写去重表:临时文件 → Flush(true) → Move(overwrite)。</summary>
    private void WriteSeen(Dictionary<string, DateTimeOffset> seen)
    {
        try
        {
            string? dir = Path.GetDirectoryName(_seenPath);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            string tmpPath = _seenPath + ".tmp-" + Guid.NewGuid().ToString("N");
            try
            {
                var options = new JsonSerializerOptions { WriteIndented = true };
                string json = JsonSerializer.Serialize(
                    seen.ToDictionary(kv => kv.Key, kv => kv.Value.ToString("o", CultureInfo.InvariantCulture)),
                    options);

                using (var fs = new FileStream(tmpPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                using (var writer = new StreamWriter(fs, new System.Text.UTF8Encoding(false)))
                {
                    writer.Write(json);
                    writer.Flush();
                    fs.Flush(true); // 落盘,防断电半截文件。
                }

                File.Move(tmpPath, _seenPath, overwrite: true);
            }
            catch
            {
                try
                {
                    if (File.Exists(tmpPath))
                    {
                        File.Delete(tmpPath);
                    }
                }
                catch
                {
                    // 清理失败不掩盖原始异常。
                }

                throw;
            }
        }
        catch (IOException)
        {
            // 写失败:本轮去重表不落盘,下轮重试;不抛。
        }
        catch (UnauthorizedAccessException)
        {
            // 同上。
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
            // 删除失败:文件留在原地,下轮可能重试;不抛。
        }
        catch (UnauthorizedAccessException)
        {
            // 同上。
        }
    }
}
