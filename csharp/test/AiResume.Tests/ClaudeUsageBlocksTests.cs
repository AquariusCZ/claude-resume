using System;
using System.IO;
using System.Text.Json;
using Xunit;

namespace AiResume.Tests;

public class ClaudeUsageBlocksTests : IDisposable
{
    private readonly string _tempRoot;
    private readonly DateTimeOffset _now = DateTimeOffset.Parse("2026-08-06T09:30:00Z");

    public ClaudeUsageBlocksTests()
    {
        _tempRoot = TestTemp.NewDir("airesume-tests");
        Directory.CreateDirectory(_tempRoot);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempRoot))
        {
            Directory.Delete(_tempRoot, recursive: true);
        }
    }

    private string WriteJsonl(string relativePath, string content, DateTimeOffset? lastWriteTime = null)
    {
        var fullPath = Path.Combine(_tempRoot, relativePath);
        var dir = Path.GetDirectoryName(fullPath)!;
        Directory.CreateDirectory(dir);
        File.WriteAllText(fullPath, content);
        // SetLastWriteTimeUtc 收的是 DateTime 而非 DateTimeOffset,须显式取 UtcDateTime。
        File.SetLastWriteTimeUtc(fullPath, (lastWriteTime ?? _now).UtcDateTime);
        return fullPath;
    }

    private static string BuildLine(
        string timestamp,
        string type = "assistant",
        string sessionId = "s1",
        string requestId = "r1",
        string messageId = "m1",
        long inputTokens = 10,
        long outputTokens = 20,
        long cacheCreationTokens = 5,
        long cacheReadTokens = 7,
        bool includeUsage = true,
        bool includeCacheFields = true)
    {
        // role 必须跟随 type:真实数据里 user 消息的 message.role 就是 "user" 且不带 usage,
        // 只改顶层 type 而把 role 钉死成 assistant 会造出现实中不存在的行,断言也就失去意义。
        var message = new Dictionary<string, object>
        {
            ["id"] = messageId,
            ["model"] = "claude-opus-5",
            ["role"] = type
        };

        if (includeUsage)
        {
            var usage = new Dictionary<string, object>
            {
                ["input_tokens"] = inputTokens,
                ["output_tokens"] = outputTokens
            };
            if (includeCacheFields)
            {
                usage["cache_creation_input_tokens"] = cacheCreationTokens;
                usage["cache_read_input_tokens"] = cacheReadTokens;
            }
            message["usage"] = usage;
        }

        var line = new Dictionary<string, object>
        {
            ["timestamp"] = timestamp,
            ["type"] = type,
            ["sessionId"] = sessionId,
            ["requestId"] = requestId,
            ["uuid"] = "u1",
            ["message"] = message
        };

        return JsonSerializer.Serialize(line);
    }

    [Fact]
    public void 目录不存在_返回null()
    {
        var missingDir = Path.Combine(_tempRoot, "does-not-exist");
        var result = AiResume.Worker.Quota.ClaudeUsageBlocks.FindActiveBlock(missingDir, _now);
        Assert.Null(result);
    }

    [Fact]
    public void 空目录_返回null()
    {
        var result = AiResume.Worker.Quota.ClaudeUsageBlocks.FindActiveBlock(_tempRoot, _now);
        Assert.Null(result);
    }

    [Fact]
    public void 单条记录在活动窗口内_块属性正确()
    {
        var line = BuildLine("2026-08-06T07:23:00Z");
        WriteJsonl("single.jsonl", line + "\n");

        var result = AiResume.Worker.Quota.ClaudeUsageBlocks.FindActiveBlock(_tempRoot, _now);

        Assert.NotNull(result);
        Assert.Equal(DateTimeOffset.Parse("2026-08-06T07:00:00Z"), result.StartUtc);
        Assert.Equal(DateTimeOffset.Parse("2026-08-06T12:00:00Z"), result.EndUtc);
        Assert.Equal(10, result.InputTokens);
        Assert.Equal(20, result.OutputTokens);
        Assert.Equal(5, result.CacheCreationTokens);
        Assert.Equal(7, result.CacheReadTokens);
        Assert.Equal(1, result.MessageCount);
        Assert.Equal(42, result.TotalTokens);
        Assert.Equal(DateTimeOffset.Parse("2026-08-06T07:23:00Z"), result.LastActivityUtc);
    }

    [Fact]
    public void 同一块内多条记录_累加正确()
    {
        var line1 = BuildLine("2026-08-06T07:10:00Z", requestId: "r1", messageId: "m1", inputTokens: 10, outputTokens: 20, cacheCreationTokens: 5, cacheReadTokens: 7);
        var line2 = BuildLine("2026-08-06T08:15:00Z", requestId: "r2", messageId: "m2", inputTokens: 30, outputTokens: 40, cacheCreationTokens: 15, cacheReadTokens: 17);
        WriteJsonl("multi.jsonl", line1 + "\n" + line2 + "\n");

        var result = AiResume.Worker.Quota.ClaudeUsageBlocks.FindActiveBlock(_tempRoot, _now);

        Assert.NotNull(result);
        Assert.Equal(DateTimeOffset.Parse("2026-08-06T07:00:00Z"), result.StartUtc);
        Assert.Equal(40, result.InputTokens);
        Assert.Equal(60, result.OutputTokens);
        Assert.Equal(20, result.CacheCreationTokens);
        Assert.Equal(24, result.CacheReadTokens);
        Assert.Equal(2, result.MessageCount);
        Assert.Equal(144, result.TotalTokens);
        Assert.Equal(DateTimeOffset.Parse("2026-08-06T08:15:00Z"), result.LastActivityUtc);
    }

    [Fact]
    public void 两条记录间隔超过5小时_分成两块_返回活动块()
    {
        var line1 = BuildLine("2026-08-06T01:00:00Z", requestId: "r1", messageId: "m1");
        var line2 = BuildLine("2026-08-06T07:30:00Z", requestId: "r2", messageId: "m2");
        WriteJsonl("gap.jsonl", line1 + "\n" + line2 + "\n");

        var result = AiResume.Worker.Quota.ClaudeUsageBlocks.FindActiveBlock(_tempRoot, _now);

        Assert.NotNull(result);
        Assert.Equal(DateTimeOffset.Parse("2026-08-06T07:00:00Z"), result.StartUtc);
        Assert.Equal(1, result.MessageCount);
        Assert.Equal(DateTimeOffset.Parse("2026-08-06T07:30:00Z"), result.LastActivityUtc);
    }

    [Fact]
    public void 记录距blockStart超过5小时_即使与上一条间隔不足5小时_开启新块()
    {
        var line1 = BuildLine("2026-08-06T01:00:00Z", requestId: "r1", messageId: "m1");
        var line2 = BuildLine("2026-08-06T06:30:00Z", requestId: "r2", messageId: "m2");
        WriteJsonl("blockstart.jsonl", line1 + "\n" + line2 + "\n");

        var result = AiResume.Worker.Quota.ClaudeUsageBlocks.FindActiveBlock(_tempRoot, _now);

        Assert.NotNull(result);
        Assert.Equal(DateTimeOffset.Parse("2026-08-06T06:00:00Z"), result.StartUtc);
        Assert.Equal(1, result.MessageCount);
        Assert.Equal(DateTimeOffset.Parse("2026-08-06T06:30:00Z"), result.LastActivityUtc);
    }

    [Fact]
    public void 同一条消息在两个文件_去重()
    {
        var line = BuildLine("2026-08-06T07:30:00Z", requestId: "r1", messageId: "m1");
        WriteJsonl("file1.jsonl", line + "\n");
        WriteJsonl("file2.jsonl", line + "\n");

        var result = AiResume.Worker.Quota.ClaudeUsageBlocks.FindActiveBlock(_tempRoot, _now);

        Assert.NotNull(result);
        Assert.Equal(10, result.InputTokens);
        Assert.Equal(20, result.OutputTokens);
        Assert.Equal(5, result.CacheCreationTokens);
        Assert.Equal(7, result.CacheReadTokens);
        Assert.Equal(1, result.MessageCount);
        Assert.Equal(42, result.TotalTokens);
    }

    [Fact]
    public void 只有user消息或缺usage_不计入_返回null()
    {
        var userLine = BuildLine("2026-08-06T07:30:00Z", type: "user", requestId: "r1", messageId: "m1");
        var noUsageLine = BuildLine("2026-08-06T07:31:00Z", requestId: "r2", messageId: "m2", includeUsage: false);
        WriteJsonl("ignored.jsonl", userLine + "\n" + noUsageLine + "\n");

        var result = AiResume.Worker.Quota.ClaudeUsageBlocks.FindActiveBlock(_tempRoot, _now);

        Assert.Null(result);
    }

    [Fact]
    public void 非JSON行空行截断半行_静默跳过_其余行正确()
    {
        var validLine = BuildLine("2026-08-06T07:30:00Z", requestId: "r1", messageId: "m1");
        var content = "not-json\n\n" + validLine + "\n{\"truncated\": true\n";
        WriteJsonl("dirty.jsonl", content);

        var result = AiResume.Worker.Quota.ClaudeUsageBlocks.FindActiveBlock(_tempRoot, _now);

        Assert.NotNull(result);
        Assert.Equal(1, result.MessageCount);
        Assert.Equal(10, result.InputTokens);
        Assert.Equal(20, result.OutputTokens);
        Assert.Equal(5, result.CacheCreationTokens);
        Assert.Equal(7, result.CacheReadTokens);
    }

    [Fact]
    public void 所有记录早于now减5小时_返回null()
    {
        var line = BuildLine("2026-08-06T03:00:00Z", requestId: "r1", messageId: "m1");
        WriteJsonl("expired.jsonl", line + "\n");

        var result = AiResume.Worker.Quota.ClaudeUsageBlocks.FindActiveBlock(_tempRoot, _now);

        Assert.Null(result);
    }

    [Fact]
    public void 缺cache字段_按0处理()
    {
        var line = BuildLine("2026-08-06T07:30:00Z", requestId: "r1", messageId: "m1", includeCacheFields: false);
        WriteJsonl("nocache.jsonl", line + "\n");

        var result = AiResume.Worker.Quota.ClaudeUsageBlocks.FindActiveBlock(_tempRoot, _now);

        Assert.NotNull(result);
        Assert.Equal(10, result.InputTokens);
        Assert.Equal(20, result.OutputTokens);
        Assert.Equal(0, result.CacheCreationTokens);
        Assert.Equal(0, result.CacheReadTokens);
        Assert.Equal(30, result.TotalTokens);
    }

    [Fact]
    public void 递归子目录中的jsonl被扫描()
    {
        var line = BuildLine("2026-08-06T07:30:00Z", requestId: "r1", messageId: "m1");
        WriteJsonl(Path.Combine("sub", "nested", "deep.jsonl"), line + "\n");

        var result = AiResume.Worker.Quota.ClaudeUsageBlocks.FindActiveBlock(_tempRoot, _now);

        Assert.NotNull(result);
        Assert.Equal(1, result.MessageCount);
        Assert.Equal(10, result.InputTokens);
    }
}