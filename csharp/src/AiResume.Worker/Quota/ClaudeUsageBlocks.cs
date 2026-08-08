using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace AiResume.Worker.Quota;

/// <summary>
/// 表示一个 5 小时滚动窗口内的用量统计块。
/// </summary>
public sealed record UsageBlock(
    DateTimeOffset StartUtc,
    DateTimeOffset EndUtc,
    long InputTokens,
    long OutputTokens,
    long CacheCreationTokens,
    long CacheReadTokens,
    int MessageCount,
    DateTimeOffset LastActivityUtc)
{
    /// <summary>
    /// 该块内所有 token 的总和。
    /// </summary>
    public long TotalTokens => InputTokens + OutputTokens + CacheCreationTokens + CacheReadTokens;
}

/// <summary>
/// 从本地 Claude Code 会话文件中计算 5 小时滚动窗口用量。
/// </summary>
public static class ClaudeUsageBlocks
{
    /// <summary>
    /// 滚动窗口长度（小时）。
    /// </summary>
    public const int BlockHours = 5;

    /// <summary>
    /// 查找当前活动块。若没有活动块则返回 null。
    /// </summary>
    /// <param name="projectsRoot">~/.claude/projects 目录路径。</param>
    /// <param name="now">当前时间（UTC）。</param>
    /// <returns>活动块，或 null。</returns>
    public static UsageBlock? FindActiveBlock(string projectsRoot, DateTimeOffset now)
    {
        if (!Directory.Exists(projectsRoot))
        {
            return null;
        }

        // 性能裁剪：只读取最近 12 小时内修改过的文件。
        // 活动块最多覆盖最近 5 小时，12 小时留足时钟偏斜与跨块边界余量。
        // 用户机器上有 2500 多个 jsonl 合计 600MB 以上，全读会让 GUI 卡住。
        var cutoff = now.UtcDateTime.AddHours(-12);
        var files = EnumerateJsonlFiles(projectsRoot)
            .Where(f => RecentlyWritten(f, cutoff))
            .ToList();

        var records = new List<(DateTimeOffset Timestamp, string DedupKey, long Input, long Output, long CacheCreation, long CacheRead)>();

        foreach (var file in files)
        {
            try
            {
                using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var reader = new StreamReader(stream);
                string? line;
                while ((line = reader.ReadLine()) != null)
                {
                    if (string.IsNullOrWhiteSpace(line))
                    {
                        continue;
                    }

                    try
                    {
                        using var doc = JsonDocument.Parse(line);
                        var root = doc.RootElement;

                        if (!root.TryGetProperty("timestamp", out var tsElem) ||
                            !root.TryGetProperty("message", out var msgElem) ||
                            !msgElem.TryGetProperty("usage", out var usageElem))
                        {
                            continue;
                        }

                        if (!DateTimeOffset.TryParse(
                                tsElem.GetString(),
                                CultureInfo.InvariantCulture,
                                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal,
                                out var timestamp))
                        {
                            continue;
                        }

                        // 只收集 assistant 消息（带 usage 的即为 assistant）。
                        if (!msgElem.TryGetProperty("role", out var roleElem) ||
                            roleElem.GetString() != "assistant")
                        {
                            continue;
                        }

                        // 去重键：message.id + requestId；两者都缺失时用 uuid；都没有则跳过。
                        string? dedupKey = null;
                        if (msgElem.TryGetProperty("id", out var idElem) &&
                            root.TryGetProperty("requestId", out var reqElem))
                        {
                            dedupKey = idElem.GetString() + "|" + reqElem.GetString();
                        }
                        else if (root.TryGetProperty("uuid", out var uuidElem))
                        {
                            dedupKey = uuidElem.GetString();
                        }

                        if (string.IsNullOrEmpty(dedupKey))
                        {
                            continue;
                        }

                        // 用 TryGetInt64 而非 GetInt64:字段存在但值为 null 或字符串时,
                        // GetInt64 抛的是 InvalidOperationException,它不在下面的 catch 里,
                        // 会一路逃出方法、违反"不向调用方抛异常"的约定。
                        records.Add((
                            timestamp,
                            dedupKey,
                            ReadTokens(usageElem, "input_tokens"),
                            ReadTokens(usageElem, "output_tokens"),
                            ReadTokens(usageElem, "cache_creation_input_tokens"),
                            ReadTokens(usageElem, "cache_read_input_tokens")));
                    }
                    catch (JsonException)
                    {
                        // 单行解析失败静默跳过，绝不抛异常。
                    }
                    catch (FormatException)
                    {
                        // 数值解析失败静默跳过。
                    }
                }
            }
            catch (IOException)
            {
                // 文件读取失败跳过该文件。
            }
            catch (UnauthorizedAccessException)
            {
                // 无权限访问跳过该文件。
            }
        }

        if (records.Count == 0)
        {
            return null;
        }

        // 去重：同一条消息可能出现在多个文件里。
        var distinct = new Dictionary<string, (DateTimeOffset Timestamp, long Input, long Output, long CacheCreation, long CacheRead)>();
        foreach (var r in records)
        {
            // 若重复，保留时间戳最早的一条（通常为原始记录）。
            if (!distinct.TryGetValue(r.DedupKey, out var existing) || r.Timestamp < existing.Timestamp)
            {
                distinct[r.DedupKey] = (r.Timestamp, r.Input, r.Output, r.CacheCreation, r.CacheRead);
            }
        }

        var sorted = distinct.Values
            .OrderBy(x => x.Timestamp)
            .ToList();

        // 分块。
        var blocks = new List<(DateTimeOffset Start, List<(DateTimeOffset Timestamp, long Input, long Output, long CacheCreation, long CacheRead)> Items)>();

        DateTimeOffset? blockStart = null;
        DateTimeOffset? prevTimestamp = null;
        List<(DateTimeOffset, long, long, long, long)>? currentItems = null;

        foreach (var item in sorted)
        {
            var ts = item.Timestamp;
            if (blockStart == null)
            {
                // 第一条记录的时间向下取整到整点（UTC）作为 blockStart。
                blockStart = new DateTimeOffset(ts.Year, ts.Month, ts.Day, ts.Hour, 0, 0, TimeSpan.Zero);
                currentItems = new List<(DateTimeOffset, long, long, long, long)>();
            }

            bool belongsToCurrent = false;
            if (currentItems != null && blockStart.HasValue && prevTimestamp.HasValue)
            {
                // 两个条件都满足才属于当前块：
                // 1. 该记录时间减 blockStart 小于 5 小时
                // 2. 该记录时间减上一条记录时间 小于 5 小时
                if ((ts - blockStart.Value).TotalHours < BlockHours &&
                    (ts - prevTimestamp.Value).TotalHours < BlockHours)
                {
                    belongsToCurrent = true;
                }
            }

            if (!belongsToCurrent && currentItems != null && currentItems.Count > 0)
            {
                // 开启新块。
                blocks.Add((blockStart!.Value, currentItems));
                blockStart = new DateTimeOffset(ts.Year, ts.Month, ts.Day, ts.Hour, 0, 0, TimeSpan.Zero);
                currentItems = new List<(DateTimeOffset, long, long, long, long)>();
            }

            currentItems!.Add((ts, item.Input, item.Output, item.CacheCreation, item.CacheRead));
            prevTimestamp = ts;
        }

        if (currentItems != null && currentItems.Count > 0 && blockStart.HasValue)
        {
            blocks.Add((blockStart.Value, currentItems));
        }

        // 活动块 = 最后一个满足 blockStart + 5 小时 晚于 now 的块。
        UsageBlock? active = null;
        foreach (var block in blocks)
        {
            var end = block.Start.AddHours(BlockHours);
            if (end > now)
            {
                active = new UsageBlock(
                    block.Start,
                    end,
                    block.Items.Sum(x => x.Item2),
                    block.Items.Sum(x => x.Item3),
                    block.Items.Sum(x => x.Item4),
                    block.Items.Sum(x => x.Item5),
                    block.Items.Count,
                    block.Items.Max(x => x.Item1));
            }
        }

        return active;
    }

    /// <summary>读取一个 token 计数字段;缺失或不是整数一律按 0,绝不抛异常。</summary>
    private static long ReadTokens(JsonElement usage, string name) =>
        usage.TryGetProperty(name, out JsonElement el) && el.TryGetInt64(out long v) ? v : 0;

    /// <summary>
    /// 文件是否在裁剪窗口内。取 mtime 可能失败(枚举与取值之间文件被删/被独占),
    /// 此时保守返回 false —— 少读一个文件远好过让整次计算抛异常。
    /// </summary>
    private static bool RecentlyWritten(string path, DateTime cutoffUtc)
    {
        try
        {
            return File.GetLastWriteTimeUtc(path) > cutoffUtc;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// 递归枚举所有 .jsonl 文件。
    /// **必须在 try 内把枚举结果materialize**:Directory.EnumerateFiles 是惰性的,
    /// 若只在 try 里拿到迭代器,真正的 IO 异常会在下面的 foreach 中抛出、逃出 catch。
    /// </summary>
    private static IEnumerable<string> EnumerateJsonlFiles(string root)
    {
        var stack = new Stack<string>();
        stack.Push(root);

        while (stack.Count > 0)
        {
            var dir = stack.Pop();
            List<string> subDirs;
            List<string> files;
            try
            {
                subDirs = Directory.EnumerateDirectories(dir).ToList();
                files = Directory.EnumerateFiles(dir, "*.jsonl").ToList();
            }
            catch (Exception)
            {
                // 无权限/已删除/路径过长等一律跳过该目录。
                continue;
            }

            foreach (var sub in subDirs)
            {
                stack.Push(sub);
            }

            foreach (var f in files)
            {
                yield return f;
            }
        }
    }
}