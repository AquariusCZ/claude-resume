using AiResume.Gui;
using AiResume.Worker.Quota;
using Xunit;

namespace AiResume.Tests;

public sealed class ControlPlaneBridgeQuotaTests
{
    public static TheoryData<string, UsageSnapshot, bool, bool, bool, bool> GlobalQuotaCases => new()
    {
        {
            "scoped-only-43-81-100",
            Snapshot(43, 81, scopedPercent: 100),
            true, false, false, false
        },
        {
            "five-hour-full",
            Snapshot(100, 81),
            false, false, true, false
        },
        {
            "seven-day-full",
            Snapshot(43, 100),
            false, false, true, false
        },
        {
            "unknown-global-plus-scoped",
            Snapshot(43, 81, scopedPercent: 100, unattributed: true),
            true, false, true, true
        },
        {
            "partial-cli-failure",
            Snapshot(43, 81, unavailableReason: "网络异常,仅取得部分窗口信息"),
            false, false, false, false
        },
        {
            "carried-main-window",
            Snapshot(43, 81, carryFiveHour: true),
            false, true, false, false
        },
        {
            "missing-seven-day",
            Snapshot(43, null, includeSevenDay: false, scopedPercent: 100),
            false, false, false, false
        },
    };

    [Theory]
    [MemberData(nameof(GlobalQuotaCases))]
    public void Claude总额度分类执行关键状态矩阵(
        string _,
        UsageSnapshot snapshot,
        bool expectedCurrent,
        bool expectedCarried,
        bool expectedLimited,
        bool expectedUnattributed)
    {
        ControlPlaneBridge.QuotaGlobalFacts facts =
            ControlPlaneBridge.ClassifyGlobalQuota(snapshot);

        Assert.Equal(expectedCurrent, facts.HasCurrentData);
        Assert.Equal(expectedCarried, facts.HasCarried);
        Assert.Equal(expectedLimited, facts.LimitReached);
        Assert.Equal(expectedUnattributed, facts.UnattributedLimitReached);
    }

    private static UsageSnapshot Snapshot(
        int? fiveHourPercent,
        int? sevenDayPercent,
        int? scopedPercent = null,
        bool unattributed = false,
        bool carryFiveHour = false,
        bool includeSevenDay = true,
        string? unavailableReason = null)
    {
        var windows = new List<UsageWindow>
        {
            Window("five_hour", UsageWindow.FiveHourSeconds, fiveHourPercent, carryFiveHour),
        };
        if (includeSevenDay)
        {
            windows.Add(Window("seven_day", UsageWindow.SevenDaySeconds, sevenDayPercent));
        }
        if (scopedPercent is not null)
        {
            windows.Add(Window("weekly_scoped:Fable", UsageWindow.SevenDaySeconds, scopedPercent));
        }

        bool limitReached = unattributed || windows.Any(window => window.UsedPercent is >= 100);
        return new UsageSnapshot(
            "claudecode",
            DateTimeOffset.Parse("2026-08-21T08:00:00Z"),
            new[]
            {
                new UsageBucket("Usage", unavailableReason is null && !limitReached, limitReached, windows)
                {
                    UnattributedLimitReached = unattributed,
                },
            },
            unavailableReason);
    }

    private static UsageWindow Window(
        string name,
        int windowSeconds,
        int? usedPercent,
        bool carriedForward = false) => new(
        name,
        usedPercent is >= 100 ? "blocked" : "allowed",
        windowSeconds,
        DateTimeOffset.Parse("2026-08-24T08:00:00Z").ToUnixTimeSeconds(),
        null,
        usedPercent,
        carriedForward,
        name.StartsWith("weekly_scoped:", StringComparison.OrdinalIgnoreCase)
            ? "weekly_scoped:test"
            : null);
}
