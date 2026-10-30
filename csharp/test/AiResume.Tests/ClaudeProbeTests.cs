using System.Text;
using AiResume.Core;
using AiResume.Core.Abstractions;
using AiResume.Core.Contracts;
using AiResume.Worker.Fakes;
using AiResume.Worker.Probes;
using Xunit;

namespace AiResume.Tests;

/// <summary>
/// S5-B Claude 限额探测测试:假 claude 脚本模拟 blocked/可用/无 rate_limit/崩溃/超时/
/// 乱码输出,验证五态解析与分类;不真跑 AI(红线:不对真实项目/会话启动 AI)。
/// </summary>
public sealed class ClaudeProbeTests : IDisposable
{
    private readonly string _root = CreateTempRoot();
    private readonly string _cwd;

    public ClaudeProbeTests()
    {
        _cwd = Path.Combine(_root, "workdir");
        Directory.CreateDirectory(_cwd);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (Exception)
        {
            // 清理失败不掩盖断言结果。
        }
    }

    private static string CreateTempRoot()
    {
        string dir = TestTemp.NewDir("s5b");
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>系统 temp 下当前所有探测临时文件的快照(用于只对本次新增做断言)。</summary>
    private static HashSet<string> SnapshotProbeTempFiles() =>
        Directory.GetFiles(Path.GetTempPath(), "ccu-probe-*.out")
            .Concat(Directory.GetFiles(Path.GetTempPath(), "ccu-probe-*.err"))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>构造假 claude 脚本(.cmd,echo 原样输出 JSON)。</summary>
    private string WriteScript(string body)
    {
        string dir = Path.Combine(_root, "scripts-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, "fake-claude.cmd");
        File.WriteAllText(path, body, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        return path;
    }

    private async Task<ClaudeProbeResult> ProbeAsync(string script, int timeoutSeconds = 10)
    {
        var probe = new ClaudeCodeProbe(claudeCommand: script, timeoutSeconds: timeoutSeconds);
        return await probe.ProbeAsync("haiku", _cwd, CancellationToken.None);
    }

    // ---- 五态解析 ----

    [Fact]
    public async Task Blocked_rate_limit_event_parses_exact_reset_and_utilization()
    {
        string script = WriteScript(
            "@echo off\r\n" +
            "echo {\"type\":\"rate_limit_event\",\"rate_limit_info\":{\"status\":\"blocked\",\"resetsAt\":1754294400,\"rateLimitType\":\"five_hour\",\"utilization\":0.87,\"other\":\"x\"}}\r\n" +
            "exit /b 0\r\n");

        ClaudeProbeResult result = await ProbeAsync(script);

        Assert.False(result.Ready);
        Assert.Equal("limited", result.Reason);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1754294400), result.FiveHourResetUtc);
        Assert.Equal(0.87, result.FiveHourUtil);
        Assert.Null(result.SevenDayResetUtc);
    }

    [Fact]
    public async Task Seven_day_and_five_hour_events_both_parsed()
    {
        string script = WriteScript(
            "@echo off\r\n" +
            "echo {\"type\":\"rate_limit_event\",\"rate_limit_info\":{\"status\":\"blocked\",\"resetsAt\":1754294400,\"rateLimitType\":\"five_hour\",\"utilization\":0.9}}\r\n" +
            "echo {\"type\":\"rate_limit_event\",\"rate_limit_info\":{\"status\":\"blocked\",\"resetsAt\":1754812800,\"rateLimitType\":\"seven_day\",\"utilization\":0.4}}\r\n" +
            "exit /b 0\r\n");

        ClaudeProbeResult result = await ProbeAsync(script);

        Assert.Equal("limited", result.Reason);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1754294400), result.FiveHourResetUtc);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1754812800), result.SevenDayResetUtc);
        Assert.Equal(0.9, result.FiveHourUtil);
        Assert.Equal(0.4, result.SevenDayUtil);
    }

    [Fact]
    public async Task Result_line_is_error_false_means_ok()
    {
        string script = WriteScript(
            "@echo off\r\n" +
            "echo {\"type\":\"result\",\"subtype\":\"success\",\"is_error\":false,\"duration_ms\":120}\r\n" +
            "exit /b 0\r\n");

        ClaudeProbeResult result = await ProbeAsync(script);

        Assert.True(result.Ready);
        Assert.Equal("ok", result.Reason);
    }

    [Fact]
    public async Task Ok_without_rate_limit_when_exit_zero()
    {
        string script = WriteScript(
            "@echo off\r\n" +
            "echo hello from ready check\r\n" +
            "exit /b 0\r\n");

        ClaudeProbeResult result = await ProbeAsync(script);

        // 无 rate_limit_info、无 result 行、exit 0 → 现役最后手段:ready。
        Assert.True(result.Ready);
        Assert.Equal("ok", result.Reason);
        Assert.Null(result.FiveHourResetUtc);
    }

    [Fact]
    public async Task Fuzzy_limit_text_classifies_limited_even_on_error_exit()
    {
        string script = WriteScript(
            "@echo off\r\n" +
            "echo we hit the usage limit for 5-hour window\r\n" +
            "exit /b 1\r\n");

        ClaudeProbeResult result = await ProbeAsync(script);

        Assert.False(result.Ready);
        Assert.Equal("limited", result.Reason);
    }

    [Fact]
    public async Task Network_text_classifies_transient()
    {
        string script = WriteScript(
            "@echo off\r\n" +
            "echo connection reset by peer\r\n" +
            "exit /b 1\r\n");

        ClaudeProbeResult result = await ProbeAsync(script);

        Assert.False(result.Ready);
        Assert.Equal("transient", result.Reason);
    }

    [Fact]
    public async Task Missing_command_returns_no_claude()
    {
        string missing = Path.Combine(_root, "no-such-claude.cmd");
        var probe = new ClaudeCodeProbe(claudeCommand: missing, timeoutSeconds: 5);
        ClaudeProbeResult result = await probe.ProbeAsync("haiku", _cwd);

        Assert.False(result.Ready);
        Assert.Equal("no-claude", result.Reason);
    }

    [Fact]
    public async Task Unknown_exit_code_reports_exit_N()
    {
        string script = WriteScript(
            "@echo off\r\n" +
            "echo \uFFFF weird garbage \u0001\u0002\r\n" +
            "exit /b 42\r\n");

        ClaudeProbeResult result = await ProbeAsync(script);

        Assert.False(result.Ready);
        Assert.Equal("exit-42", result.Reason);
    }

    [Fact]
    public async Task Timeout_kills_tree_and_reports_timeout()
    {
        string script = WriteScript(
            "@echo off\r\n" +
            "ping -n 8 127.0.0.1 > NUL\r\n" +
            "echo {\"type\":\"result\",\"is_error\":false}\r\n" +
            "exit /b 0\r\n");

        // 探测超时 500ms,脚本要跑约 7 秒 → 必须被终止并归 timeout(不得等脚本自然结束)。
        var sw = System.Diagnostics.Stopwatch.StartNew();
        ClaudeProbeResult result = await ProbeAsync(script, timeoutSeconds: 1);
        sw.Stop();

        Assert.False(result.Ready);
        Assert.Equal("timeout", result.Reason);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(6), $"探测应被终止,实际耗时 {sw.Elapsed}");
    }

    [Fact]
    public async Task Cancellation_returns_cancelled_reason()
    {
        string script = WriteScript(
            "@echo off\r\n" +
            "ping -n 8 127.0.0.1 > NUL\r\n" +
            "echo {\"type\":\"result\",\"is_error\":false}\r\n" +
            "exit /b 0\r\n");

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
        var probe = new ClaudeCodeProbe(claudeCommand: script, timeoutSeconds: 10);
        ClaudeProbeResult result = await probe.ProbeAsync("haiku", _cwd, cts.Token);

        Assert.False(result.Ready);
        Assert.Equal("cancelled", result.Reason);
    }

    [Fact]
    public async Task Output_bytes_counted_but_text_not_retained()
    {
        string script = WriteScript(
            "@echo off\r\n" +
            "echo {\"type\":\"result\",\"is_error\":false,\"secret\":\"sk-live-value\"}\r\n" +
            "exit /b 0\r\n");

        // 只关心**本次探测**留下的文件。
        // 原实现直接断言系统 temp 下不存在任何 ccu-probe-*,那是共享目录:
        // 同机上任何一次被中途终止的探测(例如关窗时正在跑的 GUI 探测)都会让它误报。
        HashSet<string> before = SnapshotProbeTempFiles();

        ClaudeProbeResult result = await ProbeAsync(script);

        Assert.True(result.Ready);
        Assert.True(result.OutputBytes > 0, "输出字节数应为正(含 JSON 行)。");

        // 结果对象不含原始输出文本(编译期类型即保证,无 OutputText 字段)。
        string[] leaked = SnapshotProbeTempFiles().Except(before).ToArray();
        Assert.Empty(leaked);
    }

}
