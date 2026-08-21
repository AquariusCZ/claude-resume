using AiResume.Core;
using AiResume.Worker.Quota;
using Xunit;

namespace AiResume.Tests;

public sealed class QuotaResumeProbeTests
{
    [Fact]
    public async Task Fable满额时即使五小时窗口未满也禁止续跑()
    {
        DateTimeOffset now = DateTimeOffset.Parse("2026-08-20T19:02:20Z");
        UsageSnapshot snapshot = Snapshot(now, allowed: false,
            Window("five_hour", 95, now.AddHours(5)),
            Window("seven_day", 73, now.AddDays(4)),
            Window("weekly_scoped:Fable", 100, now.AddDays(4)));
        var probe = CreateProbe(now, _ => snapshot);

        ClaudeProbeResult result = await probe.ProbeAsync(
            "fable", "ignored", CancellationToken.None);

        Assert.True(result.IsLimited);
        Assert.False(result.Ready);
        Assert.Equal(0.95, result.FiveHourUtil);
        Assert.Equal(0.73, result.SevenDayUtil);
    }

    [Fact]
    public async Task 全部窗口实时可用时才允许续跑()
    {
        DateTimeOffset now = DateTimeOffset.Parse("2026-08-21T06:00:00Z");
        UsageSnapshot snapshot = Snapshot(now, allowed: true,
            Window("five_hour", 25, now.AddHours(4)),
            Window("seven_day", 79, now.AddDays(3)),
            Window("weekly_scoped:Fable", 20, now.AddDays(3)));
        var probe = CreateProbe(now, _ => snapshot);

        ClaudeProbeResult result = await probe.ProbeAsync(
            "fable", "ignored", CancellationToken.None);

        Assert.True(result.Ready);
        Assert.False(result.IsLimited);
        Assert.Equal("ok", result.Reason);
    }

    [Fact]
    public async Task 官方完整模型Id与同族Scoped可以配对()
    {
        DateTimeOffset now = DateTimeOffset.Parse("2026-08-21T06:00:00Z");
        UsageSnapshot snapshot = Snapshot(now, allowed: true,
            Window("five_hour", 25, now.AddHours(4)),
            Window("seven_day", 79, now.AddDays(3)),
            Window("weekly_scoped:Fable", 20, now.AddDays(3)));
        var probe = CreateProbe(now, _ => snapshot);

        ClaudeProbeResult result = await probe.ProbeAsync(
            "claude-fable-5-20260801", "ignored", CancellationToken.None);

        Assert.True(result.Ready);
    }

    [Theory]
    [InlineData("notfable")]
    [InlineData("x-fable-y")]
    [InlineData("claude-opus-sonnet-5")]
    public async Task 未知或含多个模型族的配置不能借FableScoped放行(string model)
    {
        DateTimeOffset now = DateTimeOffset.Parse("2026-08-21T06:00:00Z");
        UsageSnapshot snapshot = Snapshot(now, allowed: true,
            Window("five_hour", 25, now.AddHours(4)),
            Window("seven_day", 79, now.AddDays(3)),
            Window("weekly_scoped:Fable", 20, now.AddDays(3)));
        var probe = CreateProbe(now, _ => snapshot);

        ClaudeProbeResult result = await probe.ProbeAsync(model, "ignored");

        Assert.False(result.Ready);
        Assert.Equal("unknown", result.Reason);
    }

    [Theory]
    [InlineData("Fable Sonnet")]
    [InlineData("Fable 5 Opus")]
    [InlineData("claude-fable-sonnet")]
    public async Task 含多个模型族的Scoped显示名不能按首个模型放行(string scopeName)
    {
        DateTimeOffset now = DateTimeOffset.Parse("2026-08-21T06:00:00Z");
        UsageSnapshot snapshot = Snapshot(now, allowed: true,
            Window("five_hour", 25, now.AddHours(4)),
            Window("seven_day", 79, now.AddDays(3)),
            Window("weekly_scoped:" + scopeName, 20, now.AddDays(3)));
        var probe = CreateProbe(now, _ => snapshot);

        ClaudeProbeResult result = await probe.ProbeAsync("fable", "ignored");

        Assert.False(result.Ready);
        Assert.Equal("unknown", result.Reason);
    }

    [Fact]
    public async Task 缺少任一主窗口时不能证明续跑可用()
    {
        DateTimeOffset now = DateTimeOffset.Parse("2026-08-21T06:00:00Z");
        UsageSnapshot snapshot = Snapshot(now, allowed: true,
            Window("seven_day", 79, now.AddDays(3)),
            Window("weekly_scoped:Fable", 20, now.AddDays(3)));
        var probe = CreateProbe(now, _ => snapshot);

        ClaudeProbeResult result = await probe.ProbeAsync("fable", "ignored");

        Assert.False(result.Ready);
        Assert.Equal("unknown", result.Reason);
    }

    [Fact]
    public async Task 健康空库只有主窗口时不能证明未知原会话模型可用()
    {
        DateTimeOffset now = DateTimeOffset.Parse("2026-08-21T06:00:00Z");
        UsageSnapshot snapshot = Snapshot(now, allowed: true,
            Window("five_hour", 25, now.AddHours(4)),
            Window("seven_day", 79, now.AddDays(3)));
        var probe = CreateProbe(now, _ => snapshot);

        ClaudeProbeResult result = await probe.ProbeAsync("fable", "ignored");

        Assert.False(result.Ready);
        Assert.False(result.IsLimited);
        Assert.Equal("unknown", result.Reason);
    }

    [Fact]
    public async Task 只有其它模型Scoped时不能证明Fable可用()
    {
        DateTimeOffset now = DateTimeOffset.Parse("2026-08-21T06:00:00Z");
        UsageSnapshot snapshot = Snapshot(now, allowed: true,
            Window("five_hour", 25, now.AddHours(4)),
            Window("seven_day", 79, now.AddDays(3)),
            Window("weekly_scoped:Sonnet", 20, now.AddDays(3)));
        var probe = CreateProbe(now, _ => snapshot);

        ClaudeProbeResult result = await probe.ProbeAsync("claude-fable-5", "ignored");

        Assert.False(result.Ready);
        Assert.False(result.IsLimited);
        Assert.Equal("unknown", result.Reason);
    }

    [Fact]
    public async Task 其它模型Scoped满额不能伪造目标模型曾经限流()
    {
        DateTimeOffset now = DateTimeOffset.Parse("2026-08-21T06:00:00Z");
        UsageSnapshot snapshot = Snapshot(now, allowed: false,
            Window("five_hour", 25, now.AddHours(4)),
            Window("seven_day", 79, now.AddDays(3)),
            Window("weekly_scoped:Fable", 20, now.AddDays(3)),
            Window("weekly_scoped:Sonnet", 100, now.AddDays(3)));
        var probe = CreateProbe(now, _ => snapshot);

        ClaudeProbeResult result = await probe.ProbeAsync("fable", "ignored");

        Assert.False(result.Ready);
        Assert.False(result.IsLimited);
        Assert.Equal("unknown", result.Reason);
    }

    [Fact]
    public async Task 其它Scoped满额不能掩盖同时存在的未归因限流()
    {
        DateTimeOffset now = DateTimeOffset.Parse("2026-08-21T06:00:00Z");
        UsageSnapshot snapshot = Snapshot(now, allowed: false,
            Window("five_hour", 43, now.AddHours(4)),
            Window("seven_day", 81, now.AddDays(3)),
            Window("weekly_scoped:Fable", 20, now.AddDays(3)),
            Window("weekly_scoped:Sonnet", 100, now.AddDays(3)));
        UsageBucket bucket = Assert.Single(snapshot.Buckets);
        snapshot = snapshot with
        {
            Buckets = new[] { bucket with { UnattributedLimitReached = true } },
        };
        var probe = CreateProbe(now, _ => snapshot);

        ClaudeProbeResult result = await probe.ProbeAsync("fable", "ignored");

        Assert.True(result.IsLimited);
        Assert.False(result.Ready);
        Assert.Equal("limited", result.Reason);
    }

    [Fact]
    public async Task 继承模型未知时即使Fable窗口可用也不能授权续跑()
    {
        DateTimeOffset now = DateTimeOffset.Parse("2026-08-21T06:00:00Z");
        UsageSnapshot snapshot = Snapshot(now, allowed: true,
            Window("five_hour", 25, now.AddHours(4)),
            Window("seven_day", 79, now.AddDays(3)),
            Window("weekly_scoped:Fable", 20, now.AddDays(3)));
        var probe = CreateProbe(now, _ => snapshot);

        ClaudeProbeResult result = await probe.ProbeAsync(string.Empty, "ignored");

        Assert.False(result.Ready);
        Assert.False(result.IsLimited);
        Assert.Equal("unknown", result.Reason);
    }

    [Fact]
    public async Task 任一窗口只有reset没有百分比时不能证明续跑可用()
    {
        DateTimeOffset now = DateTimeOffset.Parse("2026-08-21T06:00:00Z");
        UsageSnapshot snapshot = Snapshot(now, allowed: true,
            Window("five_hour", 25, now.AddHours(4)),
            Window("seven_day", 79, now.AddDays(3)),
            new UsageWindow(
                "weekly_scoped:Fable",
                "allowed",
                UsageWindow.SevenDaySeconds,
                now.AddDays(3).ToUnixTimeSeconds(),
                null,
                null,
                Identity: "weekly_scoped:test"));
        var probe = CreateProbe(now, _ => snapshot);

        ClaudeProbeResult result = await probe.ProbeAsync("fable", "ignored");

        Assert.False(result.Ready);
        Assert.Equal("unknown", result.Reason);
    }

    [Fact]
    public async Task 窗口明确Blocked时不依赖Bucket汇总值也会失败关闭()
    {
        DateTimeOffset now = DateTimeOffset.Parse("2026-08-21T06:00:00Z");
        UsageSnapshot snapshot = Snapshot(now, allowed: true,
            Window("five_hour", 25, now.AddHours(4)),
            Window("seven_day", 79, now.AddDays(3)),
            Window("weekly_scoped:Fable", 20, now.AddDays(3)) with { Status = "blocked" });
        var probe = CreateProbe(now, _ => snapshot);

        ClaudeProbeResult result = await probe.ProbeAsync("fable", "ignored");

        Assert.True(result.IsLimited);
        Assert.False(result.Ready);
    }

    [Fact]
    public async Task 只有承接读数时保持等待而不启动续跑()
    {
        DateTimeOffset now = DateTimeOffset.Parse("2026-08-21T06:00:00Z");
        int oauthCalls = 0;
        var service = new QuotaService(
            probe: _ => Task.FromResult(new ClaudeProbeResult { Ready = true, Reason = "ok" }),
            clock: () => now,
            oauthProbe: _ => Task.FromResult(++oauthCalls == 1
                ? new OAuthUsageResult(
                    true,
                    Snapshot(now, allowed: true,
                        Window("five_hour", 25, now.AddHours(4)),
                        Window("seven_day", 79, now.AddDays(3)),
                        Window("weekly_scoped:Fable", 80, now.AddDays(3))),
                    null,
                    "account-a")
                : new OAuthUsageResult(false, null, "failed_local", "account-a")));
        var probe = new QuotaResumeProbe(service);

        Assert.True((await probe.ProbeAsync("fable", "ignored")).Ready);
        ClaudeProbeResult carried = await probe.ProbeAsync("fable", "ignored");

        Assert.False(carried.Ready);
        Assert.False(carried.IsLimited);
        Assert.Equal("unknown", carried.Reason);
    }

    [Fact]
    public async Task OAuth首次失败时Haiku成功和主窗口也不能证明Fable可用()
    {
        DateTimeOffset now = DateTimeOffset.Parse("2026-08-21T06:00:00Z");
        var service = new QuotaService(
            probe: _ => Task.FromResult(new ClaudeProbeResult
            {
                Ready = true,
                Reason = "ok",
                FiveHourResetUtc = now.AddHours(4),
                FiveHourUtil = 0.25,
                SevenDayResetUtc = now.AddDays(3),
                SevenDayUtil = 0.79,
            }),
            clock: () => now,
            oauthProbe: _ => Task.FromResult(
                new OAuthUsageResult(false, null, "failed_local", "account-a")));
        var probe = new QuotaResumeProbe(service);

        ClaudeProbeResult result = await probe.ProbeAsync("fable", "ignored");

        Assert.False(result.Ready);
        Assert.False(result.IsLimited);
        Assert.Equal("unknown", result.Reason);
    }

    [Fact]
    public async Task OAuth失败时Haiku限流也不能伪造Fable曾经限流()
    {
        DateTimeOffset now = DateTimeOffset.Parse("2026-08-21T06:00:00Z");
        var service = new QuotaService(
            probe: _ => Task.FromResult(new ClaudeProbeResult
            {
                Ready = false,
                Reason = "limited",
                FiveHourResetUtc = now.AddHours(4),
                FiveHourUtil = 1,
            }),
            clock: () => now,
            oauthProbe: _ => Task.FromResult(
                new OAuthUsageResult(false, null, "failed_local", "account-a")));
        var probe = new QuotaResumeProbe(service);

        ClaudeProbeResult result = await probe.ProbeAsync("fable", "ignored");

        Assert.False(result.Ready);
        Assert.False(result.IsLimited);
        Assert.Equal("unknown", result.Reason);
    }

    [Fact]
    public async Task 权威存储故障且本次OAuth省略Scoped时不得放行()
    {
        DateTimeOffset now = DateTimeOffset.Parse("2026-08-21T06:00:00Z");
        string directoryInsteadOfDatabase = TestTemp.NewDir("quota-resume-store-failure");
        var service = new QuotaService(
            probe: _ => Task.FromResult(new ClaudeProbeResult { Reason = "unexpected" }),
            clock: () => now,
            oauthProbe: _ => Task.FromResult(new OAuthUsageResult(
                true,
                Snapshot(now, allowed: true,
                    Window("five_hour", 25, now.AddHours(4)),
                    Window("seven_day", 79, now.AddDays(3))),
                null,
                "account-a")),
            authoritativeStore: new QuotaSnapshotStore(directoryInsteadOfDatabase));
        var probe = new QuotaResumeProbe(service);

        ClaudeProbeResult result = await probe.ProbeAsync("fable", "ignored");

        Assert.False(result.Ready);
        Assert.False(result.IsLimited);
        Assert.Equal("unknown", result.Reason);
        Assert.NotNull(service.StorageWarning);
    }

    [Fact]
    public async Task 每个状态机探测点都绕过GUI成功缓存()
    {
        DateTimeOffset now = DateTimeOffset.Parse("2026-08-21T06:00:00Z");
        int oauthCalls = 0;
        UsageSnapshot snapshot = Snapshot(now, allowed: true,
            Window("five_hour", 25, now.AddHours(4)));
        var probe = CreateProbe(now, _ =>
        {
            oauthCalls++;
            return snapshot;
        });

        await probe.ProbeAsync("fable", "ignored");
        await probe.ProbeAsync("fable", "ignored");

        Assert.Equal(2, oauthCalls);
    }

    private static QuotaResumeProbe CreateProbe(
        DateTimeOffset now,
        Func<CancellationToken, UsageSnapshot> snapshot)
    {
        var service = new QuotaService(
            probe: _ => Task.FromResult(new ClaudeProbeResult { Reason = "unexpected" }),
            clock: () => now,
            oauthProbe: cancellationToken => Task.FromResult(
                new OAuthUsageResult(true, snapshot(cancellationToken), null, "account-a")));
        return new QuotaResumeProbe(service);
    }

    private static UsageSnapshot Snapshot(
        DateTimeOffset capturedAt,
        bool allowed,
        params UsageWindow[] windows)
    {
        bool limited = windows.Any(window => window.UsedPercent is >= 100);
        return new UsageSnapshot(
            "claudecode",
            capturedAt,
            new[] { new UsageBucket("Usage", allowed, limited, windows) },
            null);
    }

    private static UsageWindow Window(
        string name,
        int usedPercent,
        DateTimeOffset reset) => new(
        name,
        usedPercent >= 100 ? "blocked" : "allowed",
        name.Equals("five_hour", StringComparison.OrdinalIgnoreCase)
            ? UsageWindow.FiveHourSeconds
            : UsageWindow.SevenDaySeconds,
        reset.ToUnixTimeSeconds(),
        null,
        usedPercent,
        Identity: name.StartsWith("weekly_scoped", StringComparison.OrdinalIgnoreCase)
            ? "weekly_scoped:test"
            : null);
}
