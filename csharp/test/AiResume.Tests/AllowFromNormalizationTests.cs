using AiResume.Worker.Migration;
using Xunit;

namespace AiResume.Tests;

/// <summary>
/// allow_from 的空白规整。**2026-08-08 真事故的回归。**
///
/// 用户填进来的 open_id 中间夹了一个空格
/// (<c>ou_160a7866f6c507d6c 508896eadda3c34</c>,36 字符而不是 35),
/// 而当时只做了 <c>.Trim()</c> —— 去不掉中间的。
///
/// 后果:cc-connect 拿着一个永远匹配不上的白名单跑,
/// **每一条飞书消息都被静默丢弃**。日志里连一行 "message received" 都没有;
/// 进程在、平台 ready、管理 API 200,所有健康检查全绿。
/// 用户看到的只有"机器人不理我",而且完全无从查起 ——
/// 这类"全绿但不工作"的故障比崩溃危险得多。
/// </summary>
public sealed class AllowFromNormalizationTests
{
    [Fact]
    public void 中间夹空格的open_id被修好()
    {
        string bad = "ou_160a7866f6c507d6c 508896eadda3c34";
        Assert.Equal(36, bad.Length);

        string fixedUp = FeishuCredentialStore.NormalizeAllowFrom(bad);

        Assert.Equal("ou_160a7866f6c507d6c508896eadda3c34", fixedUp);
        Assert.Equal(35, fixedUp.Length);   // open_id 的标准长度
    }

    [Theory]
    [InlineData(" ou_abc ", "ou_abc")]                       // 首尾
    [InlineData("ou_a\tb", "ou_ab")]                         // 制表符
    [InlineData("ou_a b", "ou_ab")]                     // 不换行空格(粘贴常见)
    [InlineData("ou_a\r\nb", "ou_ab")]                       // 换行
    public void 各种空白都剥掉(string input, string expected)
    {
        Assert.Equal(expected, FeishuCredentialStore.NormalizeAllowFrom(input));
    }

    [Theory]
    [InlineData("ou_a, ou_b", "ou_a,ou_b")]                  // 逗号后的空格
    [InlineData("ou_a,,ou_b", "ou_a,ou_b")]                  // 空项
    [InlineData("ou_a, ,ou_b", "ou_a,ou_b")]                 // 只有空白的项
    [InlineData(" ou_a , ou_b ", "ou_a,ou_b")]
    public void 多个id的分隔也规整(string input, string expected)
    {
        Assert.Equal(expected, FeishuCredentialStore.NormalizeAllowFrom(input));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(" , , ")]
    public void 规整后为空要能被识别出来(string? input)
    {
        // 空 allow_from 会让 cc-connect **放行所有飞书用户** ——
        // 所以"规整之后变成空"必须能被上层判出来并 fail-closed,
        // 不能悄悄写一个空字符串进配置。
        Assert.Equal(string.Empty, FeishuCredentialStore.NormalizeAllowFrom(input));
    }

    [Fact]
    public void 正常值原样返回()
    {
        const string good = "ou_160a7866f6c507d6c508896eadda3c34";

        Assert.Equal(good, FeishuCredentialStore.NormalizeAllowFrom(good));
    }
}
