using AiResume.Core;
using AiResume.Worker.Quota;
using Xunit;

namespace AiResume.Tests;

public sealed class QuotaServiceTests
{
    private const string AccountA = "account-a";
    private const string AccountB = "account-b";

    [Fact]
    public async Task OAuth降级到CLI时在重置前承接模型额度()
    {
        DateTimeOffset now = DateTimeOffset.Parse("2026-08-09T12:00:00Z");
        int oauthCalls = 0;
        var service = new QuotaService(
            probe: _ => Task.FromResult(FallbackProbe(now)),
            clock: () => now,
            oauthProbe: _ => Task.FromResult(++oauthCalls == 1
                ? new OAuthUsageResult(true, Authoritative(now, includeScoped: true), null, AccountA)
                : new OAuthUsageResult(false, null, "failed_local", AccountA)));

        UsageSnapshot first = await service.GetAsync(forceRefresh: true, CancellationToken.None);
        Assert.False(Scoped(first).CarriedForward);

        UsageSnapshot fallback = await service.GetAsync(forceRefresh: true, CancellationToken.None);
        UsageWindow scoped = Scoped(fallback);

        Assert.True(scoped.CarriedForward);
        Assert.Equal(100, scoped.UsedPercent);
        Assert.Equal(3600, scoped.ResetAfterSeconds);
        Assert.True(Assert.Single(fallback.Buckets).LimitReached);
        Assert.False(Assert.Single(fallback.Buckets).Allowed);
    }

    [Fact]
    public async Task 已过重置时间的模型额度不会继续承接()
    {
        DateTimeOffset now = DateTimeOffset.Parse("2026-08-09T12:00:00Z");
        int oauthCalls = 0;
        var service = new QuotaService(
            probe: _ => Task.FromResult(FallbackProbe(now)),
            clock: () => now,
            oauthProbe: _ => Task.FromResult(++oauthCalls == 1
                ? new OAuthUsageResult(true, Authoritative(now, includeScoped: true), null, AccountA)
                : new OAuthUsageResult(false, null, "failed_local", AccountA)));

        await service.GetAsync(forceRefresh: true, CancellationToken.None);
        now = now.AddHours(2);

        UsageSnapshot fallback = await service.GetAsync(forceRefresh: true, CancellationToken.None);

        Assert.DoesNotContain(
            Assert.Single(fallback.Buckets).Windows,
            window => window.Name.StartsWith("weekly_scoped", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task 新OAuth稀疏快照不清除同一重置周期的模型额度()
    {
        DateTimeOffset now = DateTimeOffset.Parse("2026-08-09T12:00:00Z");
        var oauthResults = new Queue<OAuthUsageResult>(new[]
        {
            new OAuthUsageResult(true, Authoritative(now, includeScoped: true), null, AccountA),
            new OAuthUsageResult(true, Authoritative(now, includeScoped: false), null, AccountA),
            new OAuthUsageResult(false, null, "failed_local", AccountA),
        });
        var service = new QuotaService(
            probe: _ => Task.FromResult(FallbackProbe(now)),
            clock: () => now,
            oauthProbe: _ => Task.FromResult(oauthResults.Dequeue()));

        await service.GetAsync(forceRefresh: true, CancellationToken.None);
        UsageSnapshot withoutScoped = await service.GetAsync(forceRefresh: true, CancellationToken.None);
        UsageSnapshot fallback = await service.GetAsync(forceRefresh: true, CancellationToken.None);

        Assert.True(Scoped(withoutScoped).CarriedForward);
        Assert.True(Scoped(fallback).CarriedForward);
        Assert.Equal(100, Scoped(fallback).UsedPercent);
    }

    [Fact]
    public async Task 实时探测无窗口时保留最近读数但不得冒充实时可用()
    {
        DateTimeOffset now = DateTimeOffset.Parse("2026-08-09T12:00:00Z");
        int oauthCalls = 0;
        var service = new QuotaService(
            probe: _ => Task.FromResult(new ClaudeProbeResult { Ready = false, Reason = "transient" }),
            clock: () => now,
            oauthProbe: _ => Task.FromResult(++oauthCalls == 1
                ? new OAuthUsageResult(true, Authoritative(now, includeScoped: true), null, AccountA)
                : new OAuthUsageResult(false, null, "failed_local", AccountA)));

        await service.GetAsync(forceRefresh: true, CancellationToken.None);
        UsageSnapshot fallback = await service.GetAsync(forceRefresh: true, CancellationToken.None);

        Assert.True(fallback.HasData);
        Assert.True(Scoped(fallback).CarriedForward);
        Assert.False(Assert.Single(fallback.Buckets).Allowed);
    }

    [Fact]
    public async Task 权威模型额度跨QuotaService实例按重置时间承接()
    {
        DateTimeOffset now = DateTimeOffset.Parse("2026-08-09T12:00:00Z");
        string databasePath = TestTemp.NewFile("quota-snapshot", ".db");
        var store = new QuotaSnapshotStore(databasePath);
        var reader = new QuotaService(
            probe: _ => Task.FromResult(FallbackProbe(now)),
            clock: () => now,
            oauthProbe: _ => Task.FromResult(
                new OAuthUsageResult(false, null, "failed_local", AccountA)),
            authoritativeStore: new QuotaSnapshotStore(databasePath));
        var writer = new QuotaService(
            probe: _ => Task.FromResult(FallbackProbe(now)),
            clock: () => now,
            oauthProbe: _ => Task.FromResult(
                new OAuthUsageResult(true, Authoritative(now, includeScoped: true), null, AccountA)),
            authoritativeStore: store);
        await writer.GetAsync(forceRefresh: true, CancellationToken.None);

        UsageWindow scoped = Scoped(await reader.GetAsync(forceRefresh: true, CancellationToken.None));

        Assert.True(scoped.CarriedForward);
        Assert.Equal(100, scoped.UsedPercent);
    }

    [Fact]
    public async Task 不同凭据指纹不会承接上一账号的模型额度()
    {
        DateTimeOffset now = DateTimeOffset.Parse("2026-08-09T12:00:00Z");
        string databasePath = TestTemp.NewFile("quota-account", ".db");
        var writer = new QuotaService(
            probe: _ => Task.FromResult(FallbackProbe(now)),
            clock: () => now,
            oauthProbe: _ => Task.FromResult(
                new OAuthUsageResult(true, Authoritative(now, includeScoped: true), null, AccountA)),
            authoritativeStore: new QuotaSnapshotStore(databasePath));
        await writer.GetAsync(forceRefresh: true, CancellationToken.None);

        var otherAccount = new QuotaService(
            probe: _ => Task.FromResult(FallbackProbe(now)),
            clock: () => now,
            oauthProbe: _ => Task.FromResult(
                new OAuthUsageResult(false, null, "failed_local", AccountB)),
            authoritativeStore: new QuotaSnapshotStore(databasePath));
        UsageSnapshot fallback = await otherAccount.GetAsync(forceRefresh: true, CancellationToken.None);

        Assert.DoesNotContain(
            Assert.Single(fallback.Buckets).Windows,
            window => window.Name.StartsWith("weekly_scoped", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task 另一实例同CapturedAt写入稀疏OAuth后不会擦除Fable()
    {
        DateTimeOffset now = DateTimeOffset.Parse("2026-08-09T12:00:00Z");
        string databasePath = TestTemp.NewFile("quota-cross-instance-clear", ".db");
        var initialWriter = new QuotaService(
            probe: _ => Task.FromResult(FallbackProbe(now)),
            clock: () => now,
            oauthProbe: _ => Task.FromResult(
                new OAuthUsageResult(true, Authoritative(now, includeScoped: true), null, AccountA)),
            authoritativeStore: new QuotaSnapshotStore(databasePath));
        await initialWriter.GetAsync(forceRefresh: true, CancellationToken.None);

        var staleWindow = new QuotaService(
            probe: _ => Task.FromResult(FallbackProbe(now)),
            clock: () => now,
            oauthProbe: _ => Task.FromResult(
                new OAuthUsageResult(false, null, "failed_local", AccountA)),
            authoritativeStore: new QuotaSnapshotStore(databasePath));
        Assert.True(Scoped(await staleWindow.GetAsync(forceRefresh: true, CancellationToken.None)).CarriedForward);

        var clearer = new QuotaService(
            probe: _ => Task.FromResult(FallbackProbe(now)),
            clock: () => now,
            oauthProbe: _ => Task.FromResult(
                new OAuthUsageResult(true, Authoritative(now, includeScoped: false), null, AccountA)),
            authoritativeStore: new QuotaSnapshotStore(databasePath));
        await clearer.GetAsync(forceRefresh: true, CancellationToken.None);
        now = now.AddMinutes(1);

        UsageSnapshot fallback = await staleWindow.GetAsync(forceRefresh: true, CancellationToken.None);

        Assert.True(Scoped(fallback).CarriedForward);
        Assert.Equal(100, Scoped(fallback).UsedPercent);
    }

    [Fact]
    public async Task 主窗口同一ResetAt缺百分比时承接而换代后不承接()
    {
        DateTimeOffset now = DateTimeOffset.Parse("2026-08-09T12:00:00Z");
        long firstReset = now.AddDays(1).ToUnixTimeSeconds();
        long nextReset = now.AddDays(2).ToUnixTimeSeconds();
        var results = new Queue<OAuthUsageResult>(new[]
        {
            new OAuthUsageResult(true, MainWindow(now, firstReset, 73), null, AccountA),
            new OAuthUsageResult(true, MainWindow(now, firstReset, null), null, AccountA),
            new OAuthUsageResult(true, MainWindow(now, nextReset, null), null, AccountA),
        });
        var service = new QuotaService(
            probe: _ => Task.FromResult(FallbackProbe(now)),
            clock: () => now,
            oauthProbe: _ => Task.FromResult(results.Dequeue()));

        await service.GetAsync(forceRefresh: true, CancellationToken.None);
        UsageWindow sameWindow = Assert.Single(Assert.Single(
            (await service.GetAsync(forceRefresh: true, CancellationToken.None)).Buckets).Windows);
        UsageWindow nextWindow = Assert.Single(Assert.Single(
            (await service.GetAsync(forceRefresh: true, CancellationToken.None)).Buckets).Windows);

        Assert.Equal(73, sameWindow.UsedPercent);
        Assert.True(sameWindow.CarriedForward);
        Assert.Null(nextWindow.UsedPercent);
        Assert.False(nextWindow.CarriedForward);
    }

    [Fact]
    public void 同一Reset较旧低读数不会覆盖较新满额读数()
    {
        DateTimeOffset newerTime = DateTimeOffset.Parse("2026-08-09T12:01:00Z");
        DateTimeOffset olderTime = newerTime.AddMinutes(-1);
        long resetAt = newerTime.AddDays(1).ToUnixTimeSeconds();
        UsageSnapshot newer = MainWindow(newerTime, resetAt, 100);
        UsageSnapshot older = MainWindow(olderTime, resetAt, 99);

        UsageSnapshot merged = QuotaService.MergeSparseObservation(older, newer, newerTime);
        UsageWindow window = Assert.Single(Assert.Single(merged.Buckets).Windows);

        Assert.Equal(100, window.UsedPercent);
        Assert.Equal("blocked", window.Status);
        Assert.True(window.CarriedForward);
        Assert.Equal(newerTime, merged.CapturedAt);
    }

    [Fact]
    public void 较旧观测的旧Reset不会覆盖较新观测的新Reset()
    {
        DateTimeOffset newerTime = DateTimeOffset.Parse("2026-08-09T12:01:00Z");
        DateTimeOffset olderTime = newerTime.AddMinutes(-1);
        long oldReset = newerTime.AddHours(1).ToUnixTimeSeconds();
        long newReset = newerTime.AddDays(1).ToUnixTimeSeconds();
        UsageSnapshot newer = MainWindow(newerTime, newReset, 12);
        UsageSnapshot older = MainWindow(olderTime, oldReset, 100);

        UsageSnapshot merged = QuotaService.MergeSparseObservation(older, newer, newerTime);
        UsageBucket bucket = Assert.Single(merged.Buckets);
        UsageWindow window = Assert.Single(bucket.Windows);

        Assert.Equal(newReset, window.ResetAtUnix);
        Assert.Equal(12, window.UsedPercent);
        Assert.True(window.CarriedForward);
        Assert.False(bucket.LimitReached);
        Assert.False(bucket.Allowed);
        Assert.Equal(newerTime, merged.CapturedAt);
    }

    [Fact]
    public void 早期无Identity同名快照不会同时污染多个新Scope()
    {
        DateTimeOffset now = DateTimeOffset.Parse("2026-08-09T12:00:00Z");
        long reset = now.AddDays(1).ToUnixTimeSeconds();
        UsageSnapshot legacy = new(
            "claudecode", now.AddMinutes(-1),
            new[]
            {
                new UsageBucket("Usage", false, true, new[]
                {
                    new UsageWindow("weekly_scoped:Fable", "blocked", UsageWindow.SevenDaySeconds,
                        reset, 86400, 100),
                }),
            }, null);
        UsageSnapshot current = new(
            "claudecode", now,
            new[]
            {
                new UsageBucket("Usage", true, false, new[]
                {
                    new UsageWindow("weekly_scoped:Fable", "allowed", UsageWindow.SevenDaySeconds,
                        reset, 86400, 40, Identity: "scope-a"),
                    new UsageWindow("weekly_scoped:Fable", "allowed", UsageWindow.SevenDaySeconds,
                        reset, 86400, 50, Identity: "scope-b"),
                }),
            }, null);

        UsageSnapshot merged = QuotaService.MergeSparseObservation(current, legacy, now);
        UsageBucket bucket = Assert.Single(merged.Buckets);

        Assert.Equal(new int?[] { 40, 50 },
            bucket.Windows.Select(window => window.UsedPercent).Order().ToArray());
        Assert.All(bucket.Windows, window => Assert.False(window.CarriedForward));
        Assert.False(bucket.LimitReached);
        Assert.True(bucket.Allowed);
    }

    [Fact]
    public void 较旧PercentOnly观测不会污染较新Reset代次()
    {
        DateTimeOffset newerTime = DateTimeOffset.Parse("2026-08-09T12:01:00Z");
        DateTimeOffset olderTime = newerTime.AddMinutes(-1);
        long newReset = newerTime.AddDays(1).ToUnixTimeSeconds();
        UsageSnapshot newer = MainWindow(newerTime, newReset, 12);
        UsageSnapshot older = new(
            "claudecode", olderTime,
            new[]
            {
                new UsageBucket("Usage", false, true, new[]
                {
                    new UsageWindow("seven_day", "blocked", UsageWindow.SevenDaySeconds,
                        null, null, 100),
                }),
            }, null);

        UsageSnapshot merged = QuotaService.MergeSparseObservation(older, newer, newerTime);
        UsageBucket bucket = Assert.Single(merged.Buckets);
        UsageWindow window = Assert.Single(bucket.Windows);

        Assert.Equal(newReset, window.ResetAtUnix);
        Assert.Equal(12, window.UsedPercent);
        Assert.True(window.CarriedForward);
        Assert.False(bucket.LimitReached);
        Assert.False(bucket.Allowed);
    }

    [Fact]
    public void 被拒绝的较旧未满观测也不能让承接结果Allowed()
    {
        DateTimeOffset newerTime = DateTimeOffset.Parse("2026-08-09T12:01:00Z");
        DateTimeOffset olderTime = newerTime.AddMinutes(-1);
        long oldReset = newerTime.AddHours(1).ToUnixTimeSeconds();
        long newReset = newerTime.AddDays(1).ToUnixTimeSeconds();
        UsageSnapshot newer = MainWindow(newerTime, newReset, 12);
        UsageSnapshot older = MainWindow(olderTime, oldReset, 99);

        UsageSnapshot merged = QuotaService.MergeSparseObservation(older, newer, newerTime);
        UsageBucket bucket = Assert.Single(merged.Buckets);

        Assert.Equal(newReset, Assert.Single(bucket.Windows).ResetAtUnix);
        Assert.True(Assert.Single(bucket.Windows).CarriedForward);
        Assert.False(bucket.LimitReached);
        Assert.False(bucket.Allowed);
    }

    [Fact]
    public void 较旧稀疏观测省略窗口时不会删除较新Resetless窗口()
    {
        DateTimeOffset newerTime = DateTimeOffset.Parse("2026-08-09T12:01:00Z");
        DateTimeOffset olderTime = newerTime.AddMinutes(-1);
        long weeklyReset = newerTime.AddDays(1).ToUnixTimeSeconds();
        UsageSnapshot newer = new(
            "claudecode", newerTime,
            new[]
            {
                new UsageBucket("Usage", true, false, new[]
                {
                    new UsageWindow("five_hour", "allowed", UsageWindow.FiveHourSeconds,
                        null, null, 80),
                    new UsageWindow("seven_day", "allowed", UsageWindow.SevenDaySeconds,
                        weeklyReset, 86400, 30),
                }),
            }, null);
        UsageSnapshot older = new(
            "claudecode", olderTime,
            new[]
            {
                new UsageBucket("Usage", true, false, new[]
                {
                    new UsageWindow("seven_day", "allowed", UsageWindow.SevenDaySeconds,
                        weeklyReset, 86400, 20),
                }),
            }, null);

        UsageSnapshot merged = QuotaService.MergeSparseObservation(older, newer, newerTime);
        UsageBucket bucket = Assert.Single(merged.Buckets);
        UsageWindow fiveHour = Assert.Single(bucket.Windows, window => window.Name == "five_hour");

        Assert.Equal(80, fiveHour.UsedPercent);
        Assert.Null(fiveHour.ResetAtUnix);
        Assert.True(fiveHour.CarriedForward);
        Assert.False(bucket.Allowed);
    }

    [Fact]
    public void 早期无IdentityScoped不会按唯一同名猜成另一个Scope()
    {
        DateTimeOffset now = DateTimeOffset.Parse("2026-08-09T12:00:00Z");
        long reset = now.AddDays(1).ToUnixTimeSeconds();
        UsageSnapshot legacy = new(
            "claudecode", now.AddMinutes(-1),
            new[]
            {
                new UsageBucket("Usage", false, true, new[]
                {
                    new UsageWindow("weekly_scoped:Fable", "blocked", UsageWindow.SevenDaySeconds,
                        reset, 86400, 100),
                }),
            }, null);
        UsageSnapshot current = new(
            "claudecode", now,
            new[]
            {
                new UsageBucket("Usage", true, false, new[]
                {
                    new UsageWindow("weekly_scoped:Fable", "allowed", UsageWindow.SevenDaySeconds,
                        reset, 86400, null, Identity: "different-scope"),
                }),
            }, null);

        UsageSnapshot merged = QuotaService.MergeSparseObservation(current, legacy, now);
        UsageBucket bucket = Assert.Single(merged.Buckets);
        UsageWindow window = Assert.Single(bucket.Windows);

        Assert.Null(window.UsedPercent);
        Assert.Equal("different-scope", window.Identity);
        Assert.False(window.CarriedForward);
        Assert.False(bucket.LimitReached);
    }

    [Fact]
    public async Task 部分失败快照只负缓存三十秒()
    {
        DateTimeOffset now = DateTimeOffset.Parse("2026-08-09T12:00:00Z");
        int probeCalls = 0;
        var service = new QuotaService(
            probe: _ =>
            {
                probeCalls++;
                return Task.FromResult(new ClaudeProbeResult
                {
                    Ready = false,
                    Reason = "transient",
                    SevenDayResetUtc = now.AddDays(1),
                });
            },
            clock: () => now,
            oauthProbe: _ => Task.FromResult(
                new OAuthUsageResult(false, null, "failed_local", AccountA)));

        UsageSnapshot first = await service.GetAsync(forceRefresh: true, CancellationToken.None);
        Assert.True(first.HasData);
        Assert.False(Assert.Single(first.Buckets).Allowed);
        Assert.NotNull(first.UnavailableReason);

        now = now.AddSeconds(20);
        await service.GetAsync(forceRefresh: false, CancellationToken.None);
        Assert.Equal(1, probeCalls);

        now = now.AddSeconds(11);
        await service.GetAsync(forceRefresh: false, CancellationToken.None);
        Assert.Equal(2, probeCalls);
    }

    private static UsageSnapshot Authoritative(DateTimeOffset now, bool includeScoped)
    {
        var windows = new List<UsageWindow>
        {
            new("seven_day", "allowed", UsageWindow.SevenDaySeconds,
                now.AddDays(1).ToUnixTimeSeconds(), 86400, 50),
        };
        if (includeScoped)
        {
            windows.Add(new UsageWindow(
                "weekly_scoped:Fable", "blocked", UsageWindow.SevenDaySeconds,
                now.AddHours(1).ToUnixTimeSeconds(), 3600, 100));
        }

        bool limited = includeScoped;
        return new UsageSnapshot(
            "claudecode",
            now,
            new[] { new UsageBucket("Usage", !limited, limited, windows) },
            null);
    }

    private static UsageSnapshot MainWindow(DateTimeOffset now, long resetAt, int? used) => new(
        "claudecode",
        now,
        new[]
        {
            new UsageBucket("Usage", used is not >= 100, used is >= 100, new[]
            {
                new UsageWindow(
                    "seven_day", used is >= 100 ? "blocked" : "allowed",
                    UsageWindow.SevenDaySeconds, resetAt,
                    (int)Math.Max(0, resetAt - now.ToUnixTimeSeconds()), used),
            }),
        },
        null);

    private static ClaudeProbeResult FallbackProbe(DateTimeOffset now) => new()
    {
        Ready = true,
        Reason = "ok",
        SevenDayResetUtc = now.AddDays(1),
        SevenDayUtil = 0.5,
    };

    private static UsageWindow Scoped(UsageSnapshot snapshot) =>
        Assert.Single(Assert.Single(snapshot.Buckets).Windows, window =>
            window.Name.StartsWith("weekly_scoped", StringComparison.OrdinalIgnoreCase));
}
