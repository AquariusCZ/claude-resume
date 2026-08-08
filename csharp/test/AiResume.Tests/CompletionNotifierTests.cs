using System.Globalization;
using AiResume.Worker.Notifications;
using Xunit;

namespace AiResume.Tests;

/// <summary>
/// S12 完成通知投递端测试。
///
/// 为什么全部用假 send:测试绝不能调用真实 lark-cli 或真实网络。
/// 为什么全部用系统 temp 下的新建目录:测试绝不能触碰 %LOCALAPPDATA%。
/// 时间比较一律用 UTC;解析用 InvariantCulture + AdjustToUniversal | AssumeUniversal。
/// </summary>
public sealed class CompletionNotifierTests
{
    private static readonly DateTimeOffset FixedNow = new DateTimeOffset(2026, 8, 7, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task 正常事件被投递并删除文件()
    {
        string dir = CreateTempDir();
        try
        {
            string eventId = "aaaaaaaaaaaaaaaa";
            string file = Path.Combine(dir, "events", "a.json");
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllText(file, EventJson(eventId, @"C:\x\my-proj", "claudecode"));

            int sendCalls = 0;
            var notifier = new CompletionNotifier(
                Path.Combine(dir, "events"),
                Path.Combine(dir, "seen.json"),
                (_, _, _, _) => { sendCalls++; return Task.FromResult(true); },
                () => FixedNow);

            NotifySweepResult result = await notifier.SweepAsync("ou_123");

            Assert.Equal(1, result.Sent);
            Assert.Equal(1, result.Total);
            Assert.False(File.Exists(file));
            Assert.Equal(1, sendCalls);
        }
        finally
        {
            Cleanup(dir);
        }
    }

    [Fact]
    public async Task 幂等键就是事件ID()
    {
        string dir = CreateTempDir();
        try
        {
            string eventId = "bbbbbbbbbbbbbbbb";
            string file = Path.Combine(dir, "events", "a.json");
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllText(file, EventJson(eventId, @"C:\x\my-proj", "claudecode"));

            string? receivedKey = null;
            var notifier = new CompletionNotifier(
                Path.Combine(dir, "events"),
                Path.Combine(dir, "seen.json"),
                (_, _, key, _) => { receivedKey = key; return Task.FromResult(true); },
                () => FixedNow);

            await notifier.SweepAsync("ou_123");

            // 幂等键直接传 eventId,不加前缀——lark-cli 限 50 字符,加前缀有溢出风险。
            Assert.Equal(eventId, receivedKey);
        }
        finally
        {
            Cleanup(dir);
        }
    }

    [Fact]
    public async Task 通知文本含项目名与来源()
    {
        string dir = CreateTempDir();
        try
        {
            string file = Path.Combine(dir, "events", "a.json");
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllText(file, EventJson("cccccccccccccccc", @"C:\x\my-proj", "claudecode"));

            string? receivedText = null;
            var notifier = new CompletionNotifier(
                Path.Combine(dir, "events"),
                Path.Combine(dir, "seen.json"),
                (_, text, _, _) => { receivedText = text; return Task.FromResult(true); },
                () => FixedNow);

            await notifier.SweepAsync("ou_123");

            Assert.NotNull(receivedText);
            Assert.Contains("my-proj", receivedText);
            Assert.Contains("claudecode", receivedText);
        }
        finally
        {
            Cleanup(dir);
        }
    }

    [Fact]
    public async Task 七天内重复事件不再投递且删除文件()
    {
        string dir = CreateTempDir();
        try
        {
            string eventId = "dddddddddddddddd";
            string file = Path.Combine(dir, "events", "a.json");
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllText(file, EventJson(eventId, @"C:\x\my-proj", "claudecode"));

            // 去重表里已有该 eventId,记录时间 = now - 1 天。
            string seenPath = Path.Combine(dir, "seen.json");
            File.WriteAllText(seenPath, $"{{\"{eventId}\":\"{(FixedNow - TimeSpan.FromDays(1)).ToString("o", CultureInfo.InvariantCulture)}\"}}");

            int sendCalls = 0;
            var notifier = new CompletionNotifier(
                Path.Combine(dir, "events"),
                seenPath,
                (_, _, _, _) => { sendCalls++; return Task.FromResult(true); },
                () => FixedNow);

            NotifySweepResult result = await notifier.SweepAsync("ou_123");

            Assert.Equal(1, result.Duplicate);
            Assert.Equal(0, sendCalls);
            Assert.False(File.Exists(file));
        }
        finally
        {
            Cleanup(dir);
        }
    }

    [Fact]
    public async Task 超过七天的记录不再算重复()
    {
        string dir = CreateTempDir();
        try
        {
            string eventId = "eeeeeeeeeeeeeeee";
            string file = Path.Combine(dir, "events", "a.json");
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllText(file, EventJson(eventId, @"C:\x\my-proj", "claudecode"));

            // 记录时间 = now - 8 天 → 已过期,不算重复。
            string seenPath = Path.Combine(dir, "seen.json");
            File.WriteAllText(seenPath, $"{{\"{eventId}\":\"{(FixedNow - TimeSpan.FromDays(8)).ToString("o", CultureInfo.InvariantCulture)}\"}}");

            int sendCalls = 0;
            var notifier = new CompletionNotifier(
                Path.Combine(dir, "events"),
                seenPath,
                (_, _, _, _) => { sendCalls++; return Task.FromResult(true); },
                () => FixedNow);

            NotifySweepResult result = await notifier.SweepAsync("ou_123");

            Assert.Equal(1, result.Sent);
            Assert.Equal(1, sendCalls);
            Assert.False(File.Exists(file));
        }
        finally
        {
            Cleanup(dir);
        }
    }

    [Fact]
    public async Task 投递失败保留文件下轮重试()
    {
        string dir = CreateTempDir();
        try
        {
            string eventId = "ffffffffffffffff";
            string file = Path.Combine(dir, "events", "a.json");
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllText(file, EventJson(eventId, @"C:\x\my-proj", "claudecode"));

            string seenPath = Path.Combine(dir, "seen.json");
            var notifier = new CompletionNotifier(
                Path.Combine(dir, "events"),
                seenPath,
                (_, _, _, _) => Task.FromResult(false),
                () => FixedNow);

            NotifySweepResult result = await notifier.SweepAsync("ou_123");

            Assert.Equal(1, result.Failed);
            Assert.True(File.Exists(file));
            Assert.False(File.Exists(seenPath)); // 去重表未写入。
        }
        finally
        {
            Cleanup(dir);
        }
    }

    [Fact]
    public async Task 投递抛异常不中断整轮()
    {
        string dir = CreateTempDir();
        try
        {
            string eventsDir = Path.Combine(dir, "events");
            Directory.CreateDirectory(eventsDir);
            File.WriteAllText(Path.Combine(eventsDir, "a.json"), EventJson("1111111111111111", @"C:\x\a", "claudecode"));
            File.WriteAllText(Path.Combine(eventsDir, "b.json"), EventJson("2222222222222222", @"C:\x\b", "claudecode"));
            File.WriteAllText(Path.Combine(eventsDir, "c.json"), EventJson("3333333333333333", @"C:\x\c", "claudecode"));

            var notifier = new CompletionNotifier(
                eventsDir,
                Path.Combine(dir, "seen.json"),
                (_, _, key, _) =>
                {
                    // 第二条抛异常。
                    if (key == "2222222222222222")
                    {
                        throw new InvalidOperationException("boom");
                    }

                    return Task.FromResult(true);
                },
                () => FixedNow);

            // 方法本身不抛。
            NotifySweepResult result = await notifier.SweepAsync("ou_123");

            Assert.Equal(3, result.Total);
            Assert.Equal(2, result.Sent);
            Assert.Equal(1, result.Failed);
        }
        finally
        {
            Cleanup(dir);
        }
    }

    [Fact]
    public async Task 坏JSON被移入malformed且不重试()
    {
        string dir = CreateTempDir();
        try
        {
            string eventsDir = Path.Combine(dir, "events");
            Directory.CreateDirectory(eventsDir);
            string file = Path.Combine(eventsDir, "bad.json");
            File.WriteAllText(file, "{ 坏");

            var notifier = new CompletionNotifier(
                eventsDir,
                Path.Combine(dir, "seen.json"),
                (_, _, _, _) => Task.FromResult(true),
                () => FixedNow);

            NotifySweepResult result = await notifier.SweepAsync("ou_123");

            Assert.Equal(1, result.Malformed);
            Assert.False(File.Exists(file));
            Assert.True(File.Exists(Path.Combine(eventsDir, "malformed", "bad.json")));
        }
        finally
        {
            Cleanup(dir);
        }
    }

    [Fact]
    public async Task 缺少eventId或cwd视为坏事件()
    {
        string dir = CreateTempDir();
        try
        {
            string eventsDir = Path.Combine(dir, "events");
            Directory.CreateDirectory(eventsDir);
            File.WriteAllText(Path.Combine(eventsDir, "no-id.json"), "{\"cwd\":\"C:\\\\x\",\"source\":\"claudecode\"}");
            File.WriteAllText(Path.Combine(eventsDir, "no-cwd.json"), "{\"eventId\":\"4444444444444444\",\"source\":\"claudecode\"}");

            var notifier = new CompletionNotifier(
                eventsDir,
                Path.Combine(dir, "seen.json"),
                (_, _, _, _) => Task.FromResult(true),
                () => FixedNow);

            NotifySweepResult result = await notifier.SweepAsync("ou_123");

            Assert.Equal(2, result.Malformed);
            Assert.False(File.Exists(Path.Combine(eventsDir, "no-id.json")));
            Assert.False(File.Exists(Path.Combine(eventsDir, "no-cwd.json")));
            Assert.True(File.Exists(Path.Combine(eventsDir, "malformed", "no-id.json")));
            Assert.True(File.Exists(Path.Combine(eventsDir, "malformed", "no-cwd.json")));
        }
        finally
        {
            Cleanup(dir);
        }
    }

    [Fact]
    public async Task 收件人为空时跳过且保留文件()
    {
        string dir = CreateTempDir();
        try
        {
            string file = Path.Combine(dir, "events", "a.json");
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllText(file, EventJson("5555555555555555", @"C:\x\my-proj", "claudecode"));

            int sendCalls = 0;
            var notifier = new CompletionNotifier(
                Path.Combine(dir, "events"),
                Path.Combine(dir, "seen.json"),
                (_, _, _, _) => { sendCalls++; return Task.FromResult(true); },
                () => FixedNow);

            NotifySweepResult result = await notifier.SweepAsync("");

            Assert.Equal(1, result.Skipped);
            Assert.True(File.Exists(file));
            Assert.Equal(0, sendCalls);
        }
        finally
        {
            Cleanup(dir);
        }
    }

    [Fact]
    public async Task 目录不存在时返回全零不抛()
    {
        string dir = CreateTempDir();
        try
        {
            var notifier = new CompletionNotifier(
                Path.Combine(dir, "nonexistent"),
                Path.Combine(dir, "seen.json"),
                (_, _, _, _) => Task.FromResult(true),
                () => FixedNow);

            NotifySweepResult result = await notifier.SweepAsync("ou_123");

            Assert.Equal(0, result.Total);
            Assert.Equal(0, result.Sent);
            Assert.Equal(0, result.Duplicate);
            Assert.Equal(0, result.Malformed);
            Assert.Equal(0, result.Failed);
            Assert.Equal(0, result.Skipped);
        }
        finally
        {
            Cleanup(dir);
        }
    }

    [Fact]
    public async Task 按文件名有序处理()
    {
        string dir = CreateTempDir();
        try
        {
            string eventsDir = Path.Combine(dir, "events");
            Directory.CreateDirectory(eventsDir);
            // 故意乱序写入;文件名 Ordinal 升序应为 a.json, b.json, c.json。
            File.WriteAllText(Path.Combine(eventsDir, "c.json"), EventJson("3333333333333333", @"C:\x\c", "claudecode"));
            File.WriteAllText(Path.Combine(eventsDir, "a.json"), EventJson("1111111111111111", @"C:\x\a", "claudecode"));
            File.WriteAllText(Path.Combine(eventsDir, "b.json"), EventJson("2222222222222222", @"C:\x\b", "claudecode"));

            var received = new List<string>();
            var notifier = new CompletionNotifier(
                eventsDir,
                Path.Combine(dir, "seen.json"),
                (_, _, key, _) => { received.Add(key); return Task.FromResult(true); },
                () => FixedNow);

            await notifier.SweepAsync("ou_123");

            Assert.Equal(["1111111111111111", "2222222222222222", "3333333333333333"], received);
        }
        finally
        {
            Cleanup(dir);
        }
    }

    [Fact]
    public async Task 去重表损坏时视作空表()
    {
        string dir = CreateTempDir();
        try
        {
            string file = Path.Combine(dir, "events", "a.json");
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllText(file, EventJson("6666666666666666", @"C:\x\my-proj", "claudecode"));

            string seenPath = Path.Combine(dir, "seen.json");
            File.WriteAllText(seenPath, "不是JSON");

            int sendCalls = 0;
            var notifier = new CompletionNotifier(
                Path.Combine(dir, "events"),
                seenPath,
                (_, _, _, _) => { sendCalls++; return Task.FromResult(true); },
                () => FixedNow);

            NotifySweepResult result = await notifier.SweepAsync("ou_123");

            Assert.Equal(1, result.Sent);
            Assert.Equal(1, sendCalls);
        }
        finally
        {
            Cleanup(dir);
        }
    }

    [Fact]
    public async Task 计数与明细一致()
    {
        string dir = CreateTempDir();
        try
        {
            string eventsDir = Path.Combine(dir, "events");
            Directory.CreateDirectory(eventsDir);

            // Sent
            File.WriteAllText(Path.Combine(eventsDir, "a.json"), EventJson("aaaaaaaaaaaaaaaa", @"C:\x\a", "claudecode"));
            // Duplicate
            string dupId = "bbbbbbbbbbbbbbbb";
            File.WriteAllText(Path.Combine(eventsDir, "b.json"), EventJson(dupId, @"C:\x\b", "claudecode"));
            // Malformed
            File.WriteAllText(Path.Combine(eventsDir, "c.json"), "{ 坏");
            // Failed
            File.WriteAllText(Path.Combine(eventsDir, "d.json"), EventJson("dddddddddddddddd", @"C:\x\d", "claudecode"));

            string seenPath = Path.Combine(dir, "seen.json");
            File.WriteAllText(seenPath, $"{{\"{dupId}\":\"{(FixedNow - TimeSpan.FromDays(1)).ToString("o", CultureInfo.InvariantCulture)}\"}}");

            var notifier = new CompletionNotifier(
                eventsDir,
                seenPath,
                (_, _, key, _) => Task.FromResult(key != "dddddddddddddddd"), // d 失败
                () => FixedNow);

            NotifySweepResult result = await notifier.SweepAsync("ou_123");

            Assert.Equal(4, result.Total);
            Assert.Equal(1, result.Sent);
            Assert.Equal(1, result.Duplicate);
            Assert.Equal(1, result.Malformed);
            Assert.Equal(1, result.Failed);
            Assert.Equal(0, result.Skipped);
            Assert.Equal(4, result.Items.Count);
            Assert.Equal(result.Total, result.Items.Count);
        }
        finally
        {
            Cleanup(dir);
        }
    }

    private static string EventJson(string eventId, string cwd, string source)
    {
        return $"{{\"eventId\":\"{eventId}\",\"source\":\"{source}\",\"sessionId\":\"s\",\"cwd\":\"{cwd.Replace("\\", "\\\\")}\",\"transcriptPath\":null,\"atUtc\":\"2026-08-07T05:48:26.0095149Z\"}}";
    }

    private static string CreateTempDir()
    {
        return TestTemp.NewDir("s12-test");
    }

    private static void Cleanup(string dir)
    {
        try
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
        catch (IOException)
        {
            // 清理失败不掩盖测试结果。
        }
        catch (UnauthorizedAccessException)
        {
            // 同上。
        }
    }
}
