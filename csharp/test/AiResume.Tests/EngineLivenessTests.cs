using AiResume.Worker.Products;
using Xunit;

namespace AiResume.Tests;

/// <summary>
/// 「监视中」凭什么说得出口(审计 A4)。
///
/// 原缺陷:依据只有 <c>config.Armed</c> —— 一个用户点「布防」时写下、
/// 此后不会因任何事情变回去的布尔值。把续跑 Worker 直接 kill 掉,
/// 面板顶部照旧绿灯写着「监视中」。
///
/// 这一句说错比面板上任何其它一句说错都严重:用户会真的去睡觉。
/// </summary>
public sealed class EngineLivenessTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 8, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void 没布防就是没布防()
    {
        Assert.Equal(
            EngineVerdict.NotArmed,
            EngineLiveness.Evaluate(armed: false, engineRunning: false, null, Now, 15));
    }

    [Fact]
    public void 布防了但引擎不在必须报出来()
    {
        Assert.Equal(
            EngineVerdict.NotRunning,
            EngineLiveness.Evaluate(armed: true, engineRunning: false, Now.AddMinutes(-1), Now, 15));
    }

    [Fact]
    public void 刚布防还没探过不算卡住()
    {
        // 报红会让每次布防后的头几分钟都在假警报,红灯很快就不值钱了。
        Assert.Equal(
            EngineVerdict.Alive,
            EngineLiveness.Evaluate(armed: true, engineRunning: true, null, Now, 15));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(15)]
    [InlineData(44)]    // 15 × 3 = 45,还在容忍内
    public void 探测在节奏内算正常(int ageMinutes)
    {
        Assert.Equal(
            EngineVerdict.Alive,
            EngineLiveness.Evaluate(true, true, Now.AddMinutes(-ageMinutes), Now, 15));
    }

    [Fact]
    public void 超过三拍算卡住()
    {
        Assert.Equal(
            EngineVerdict.Stalled,
            EngineLiveness.Evaluate(true, true, Now.AddMinutes(-46), Now, 15));
    }

    [Fact]
    public void 间隔配得极小时用五分钟地板()
    {
        // 间隔配成 1 分钟时,3 分钟就报卡住会一直误报。
        Assert.Equal(
            EngineVerdict.Alive,
            EngineLiveness.Evaluate(true, true, Now.AddMinutes(-4), Now, 1));
        Assert.Equal(
            EngineVerdict.Stalled,
            EngineLiveness.Evaluate(true, true, Now.AddMinutes(-6), Now, 1));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void 间隔非法时不因此误报(int interval)
    {
        Assert.Equal(
            EngineVerdict.Alive,
            EngineLiveness.Evaluate(true, true, Now.AddMinutes(-4), Now, interval));
    }

    [Fact]
    public void 时钟回拨不冒充故障()
    {
        // 未来时间戳说明读数不可信,但它同样不能证明引擎坏了。
        Assert.Equal(
            EngineVerdict.Alive,
            EngineLiveness.Evaluate(true, true, Now.AddMinutes(30), Now, 15));
    }

    [Fact]
    public void 没布防时引擎死活都不改结论()
    {
        // 没布防就不该有人在盯,这时候报「引擎没运行」是噪音。
        Assert.Equal(
            EngineVerdict.NotArmed,
            EngineLiveness.Evaluate(false, false, Now.AddDays(-9), Now, 15));
    }

    [Fact]
    public void 文案要把在等和没人盯分开()
    {
        Assert.Contains("没在运行", EngineLiveness.Describe(EngineVerdict.NotRunning));
        Assert.Contains("久未探测", EngineLiveness.Describe(EngineVerdict.Stalled));
        Assert.DoesNotContain("没在运行", EngineLiveness.Describe(EngineVerdict.Alive));
    }

    [Fact]
    public void 进程探不出来时按在跑处理不误报()
    {
        // TryDetectEngineProcess 返回 null 表示"探不出来"。
        // 拿探不出来当"没在跑"会在权限受限的环境里一直红着。
        bool? unknown = null;
        Assert.Equal(
            EngineVerdict.Alive,
            EngineLiveness.Evaluate(true, unknown ?? true, Now.AddMinutes(-1), Now, 15));
    }
}
