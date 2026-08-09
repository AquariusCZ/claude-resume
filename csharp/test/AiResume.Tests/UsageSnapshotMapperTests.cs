using AiResume.Core;
using AiResume.Worker.Quota;
using Xunit;

namespace AiResume.Tests;

/// <summary>
/// UsageSnapshotMapper.FromProbe 的测试。
/// 覆盖:窗口映射、utilization 钳制、ResetAfterSeconds 截断、LimitReached 传播、
/// 窗口排序、DerivedWindowStart 推导、null 入参抛异常。
/// </summary>
public sealed class UsageSnapshotMapperTests
{
    // 固定时刻,避免 DateTimeOffset.UtcNow 导致 ResetAfterSeconds 断言随机失败。
    private static readonly DateTimeOffset FixedNow = DateTimeOffset.FromUnixTimeSeconds(1786020000);

    // 固定 reset 时刻:比 FixedNow 晚 7800 秒(2 小时 10 分)。
    private static readonly DateTimeOffset FixedReset = DateTimeOffset.FromUnixTimeSeconds(1786027800);

    [Fact]
    public void OnlyFiveHourReset_NoUtil_WindowExists_UsedPercentNull()
    {
        var result = new ClaudeProbeResult
        {
            Ready = true,
            Reason = "ok",
            FiveHourResetUtc = FixedReset,
        };

        var snapshot = UsageSnapshotMapper.FromProbe(result, FixedNow);

        Assert.True(snapshot.HasData);
        Assert.Single(snapshot.Buckets);
        Assert.Equal("Usage", snapshot.Buckets[0].Name);
        Assert.Single(snapshot.Buckets[0].Windows);
        var window = snapshot.Buckets[0].Windows[0];
        Assert.Equal("five_hour", window.Name);
        Assert.Equal(FixedReset.ToUnixTimeSeconds(), window.ResetAtUnix);
        // 未报告 utilization 时 UsedPercent 必须为 null,不得当成 0。
        Assert.Null(window.UsedPercent);
        Assert.Equal(UsageWindow.FiveHourSeconds, window.WindowSeconds);
    }

    [Fact]
    public void Utilization_087_MapsTo87()
    {
        var result = new ClaudeProbeResult
        {
            Ready = true,
            Reason = "ok",
            FiveHourResetUtc = FixedReset,
            FiveHourUtil = 0.87,
        };

        var snapshot = UsageSnapshotMapper.FromProbe(result, FixedNow);

        var window = snapshot.Buckets[0].Windows.Single(w => w.Name == "five_hour");
        Assert.Equal(87, window.UsedPercent);
    }

    [Theory]
    [InlineData(1.5, 100)]
    [InlineData(-0.2, 0)]
    public void Utilization_OutOfRange_Clamped(double utilization, int expected)
    {
        var result = new ClaudeProbeResult
        {
            Ready = true,
            Reason = "ok",
            FiveHourResetUtc = FixedReset,
            FiveHourUtil = utilization,
        };

        var snapshot = UsageSnapshotMapper.FromProbe(result, FixedNow);

        var window = snapshot.Buckets[0].Windows.Single(w => w.Name == "five_hour");
        Assert.Equal(expected, window.UsedPercent);
    }

    [Fact]
    public void ResetBeforeNow_ResetAfterSeconds_Zero_NotNegative()
    {
        var pastReset = DateTimeOffset.FromUnixTimeSeconds(FixedNow.ToUnixTimeSeconds() - 3600);
        var result = new ClaudeProbeResult
        {
            Ready = true,
            Reason = "ok",
            FiveHourResetUtc = pastReset,
        };

        var snapshot = UsageSnapshotMapper.FromProbe(result, FixedNow);

        var window = snapshot.Buckets[0].Windows.Single(w => w.Name == "five_hour");
        Assert.Equal(0, window.ResetAfterSeconds);
        Assert.True(window.ResetAfterSeconds >= 0);
    }

    [Fact]
    public void NoReset_NoUtil_NoWindows_HasDataFalse_UnavailableReasonNotNull()
    {
        var result = new ClaudeProbeResult
        {
            Ready = true,
            Reason = "ok",
        };

        var snapshot = UsageSnapshotMapper.FromProbe(result, FixedNow);

        Assert.False(snapshot.HasData);
        Assert.NotNull(snapshot.UnavailableReason);
        Assert.NotEmpty(snapshot.UnavailableReason);
        // 窗口为空时仍返回 bucket,但 HasData 为 false。
        Assert.Single(snapshot.Buckets);
        Assert.Empty(snapshot.Buckets[0].Windows);
    }

    [Fact]
    public void ReasonLimited_LimitReachedTrue_AllowedFalse()
    {
        var result = new ClaudeProbeResult
        {
            Ready = false,
            Reason = "limited",
            FiveHourResetUtc = FixedReset,
        };

        var snapshot = UsageSnapshotMapper.FromProbe(result, FixedNow);

        Assert.True(snapshot.Buckets[0].LimitReached);
        Assert.False(snapshot.Buckets[0].Allowed);
    }

    [Fact]
    public void ReasonOk_WithWindow_LimitReachedFalse_StatusAllowed()
    {
        var result = new ClaudeProbeResult
        {
            Ready = true,
            Reason = "ok",
            FiveHourResetUtc = FixedReset,
        };

        var snapshot = UsageSnapshotMapper.FromProbe(result, FixedNow);

        Assert.False(snapshot.Buckets[0].LimitReached);
        Assert.True(snapshot.Buckets[0].Allowed);
        var window = snapshot.Buckets[0].Windows.Single(w => w.Name == "five_hour");
        Assert.Equal("allowed", window.Status);
    }

    [Fact]
    public void ReasonLimited_OnlyGlobalSignal_DoesNotMarkSpecificWindowBlocked()
    {
        var result = new ClaudeProbeResult
        {
            Ready = false,
            Reason = "limited",
            FiveHourResetUtc = FixedReset,
        };

        var snapshot = UsageSnapshotMapper.FromProbe(result, FixedNow);

        var window = snapshot.Buckets[0].Windows.Single(w => w.Name == "five_hour");
        Assert.Equal(string.Empty, window.Status);
        Assert.True(snapshot.Buckets[0].LimitReached);
    }

    [Fact]
    public void TransientFailure_WithPartialWindow_IsNotAllowedOrHealthy()
    {
        var result = new ClaudeProbeResult
        {
            Ready = false,
            Reason = "transient",
            FiveHourResetUtc = FixedReset,
        };

        var snapshot = UsageSnapshotMapper.FromProbe(result, FixedNow);

        Assert.True(snapshot.HasData);
        Assert.False(Assert.Single(snapshot.Buckets).Allowed);
        Assert.False(Assert.Single(snapshot.Buckets).LimitReached);
        Assert.Contains("仅取得部分窗口信息", snapshot.UnavailableReason, StringComparison.Ordinal);
    }

    [Fact]
    public void WindowUtilization100_MarksThatWindowBlocked()
    {
        var result = new ClaudeProbeResult
        {
            Ready = false,
            Reason = "limited",
            FiveHourResetUtc = FixedReset,
            FiveHourUtil = 1.0,
        };

        var snapshot = UsageSnapshotMapper.FromProbe(result, FixedNow);

        var window = snapshot.Buckets[0].Windows.Single(w => w.Name == "five_hour");
        Assert.Equal("blocked", window.Status);
        Assert.Equal(100, window.UsedPercent);
    }

    [Fact]
    public void ReasonNoClaude_NoWindows_UnavailableReasonContainsClaudeCli()
    {
        var result = new ClaudeProbeResult
        {
            Ready = false,
            Reason = "no-claude",
        };

        var snapshot = UsageSnapshotMapper.FromProbe(result, FixedNow);

        Assert.False(snapshot.HasData);
        Assert.NotNull(snapshot.UnavailableReason);
        Assert.Contains("claude CLI", snapshot.UnavailableReason);
    }

    [Fact]
    public void BothWindows_FiveHourFirst()
    {
        var result = new ClaudeProbeResult
        {
            Ready = true,
            Reason = "ok",
            FiveHourResetUtc = FixedReset,
            SevenDayResetUtc = DateTimeOffset.FromUnixTimeSeconds(FixedReset.ToUnixTimeSeconds() + 604800),
        };

        var snapshot = UsageSnapshotMapper.FromProbe(result, FixedNow);

        Assert.Equal(2, snapshot.Buckets[0].Windows.Count);
        Assert.Equal("five_hour", snapshot.Buckets[0].Windows[0].Name);
        Assert.Equal("seven_day", snapshot.Buckets[0].Windows[1].Name);
    }

    [Fact]
    public void DerivedWindowStart_ResetMinus18000()
    {
        var result = new ClaudeProbeResult
        {
            Ready = true,
            Reason = "ok",
            FiveHourResetUtc = FixedReset,
        };

        var snapshot = UsageSnapshotMapper.FromProbe(result, FixedNow);

        var window = snapshot.Buckets[0].Windows.Single(w => w.Name == "five_hour");
        var expected = DateTimeOffset.FromUnixTimeSeconds(FixedReset.ToUnixTimeSeconds() - UsageWindow.FiveHourSeconds);
        Assert.Equal(expected, window.DerivedWindowStart);
    }

    [Fact]
    public void NullResult_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() => UsageSnapshotMapper.FromProbe(null!, FixedNow));
    }
}
