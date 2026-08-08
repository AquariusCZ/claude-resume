using AiResume.Worker.Quota;
using Xunit;

namespace AiResume.Tests;

/// <summary>
/// 按模型限定的额度(<c>weekly_scoped</c>)必须参与限流判定。
///
/// **2026-08-08 真机审计发现的产品级缺陷。** 当时同一时刻:
/// - `oauth/usage` 的 seven_day = 93%,面板显示「正常」、`Allowed=true`;
/// - 而真实 Fable 任务直接返回 "You've hit your limit · resets Aug 10, 7am"。
///
/// 原因是响应里还有一个我们完全没解析的 `limits` 数组:
/// <code>
/// {"kind":"session",       "percent":4,   "severity":"normal"}
/// {"kind":"weekly_all",    "percent":93,  "severity":"critical"}
/// {"kind":"weekly_scoped", "percent":100, "severity":"critical", "scope":{"model":…}}
/// </code>
/// **weekly_scoped 已经打满**,总额度却还剩 7%。
///
/// 这个产品存在的全部理由就是"知道什么时候被限流" ——
/// 漏判一条已经打满的限额,是最不能接受的一类错误。
/// </summary>
public sealed class ScopedLimitTests
{
    private static readonly double?[] NoLimits = Array.Empty<double?>();

    [Fact]
    public void 总额度没满但按模型额度打满时判为限流()
    {
        // 这就是 2026-08-08 那一刻的真实数据。
        int?[] windows = [4, 93];
        double?[] limits = [4, 93, 100];   // session / weekly_all / weekly_scoped

        Assert.True(ClaudeOAuthUsageProbe.IsLimitReached(windows, limits));
    }

    [Fact]
    public void 只看两个主窗口会漏判()
    {
        // 钉住"为什么必须解析 limits":光看窗口是 false,
        // 那正是当时面板显示「正常」而 Fable 已经跑不动的原因。
        int?[] windows = [4, 93];

        Assert.False(ClaudeOAuthUsageProbe.IsLimitReached(windows, NoLimits));
    }

    [Fact]
    public void 九十三不算限流()
    {
        // 实测 93% 的 severity 也是 critical —— 那是"快满了"的预警,不是"已经不能跑"。
        // 拿 severity 当判据,引擎会在还能跑的时候白等到窗口重置。
        int?[] windows = [93];
        double?[] limits = [93];

        Assert.False(ClaudeOAuthUsageProbe.IsLimitReached(windows, limits));
    }

    [Fact]
    public void 主窗口自己打满仍然算限流()
    {
        int?[] windows = [100];

        Assert.True(ClaudeOAuthUsageProbe.IsLimitReached(windows, NoLimits));
    }

    [Theory]
    [InlineData(99.4)]
    [InlineData(0)]
    public void 未满的limits不影响判定(double percent)
    {
        int?[] windows = [50];
        double?[] limits = [percent];

        Assert.False(ClaudeOAuthUsageProbe.IsLimitReached(windows, limits));
    }

    [Fact]
    public void percent缺失的条目被跳过而不是当成0或100()
    {
        int?[] windows = [50];
        double?[] limits = [null];

        // 拿不准就不判限流:误判限流会让引擎白等,而下一拍探测会拿到真实值。
        Assert.False(ClaudeOAuthUsageProbe.IsLimitReached(windows, limits));
    }

    [Fact]
    public void 窗口未报告也不影响limits的判定()
    {
        int?[] windows = [null, null];
        double?[] limits = [100];

        Assert.True(ClaudeOAuthUsageProbe.IsLimitReached(windows, limits));
    }

    [Fact]
    public void 两者都空时不判限流()
    {
        Assert.False(ClaudeOAuthUsageProbe.IsLimitReached(Array.Empty<int?>(), NoLimits));
    }
}
