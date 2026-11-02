using AiResume.Worker.Notifications;
using Xunit;

namespace AiResume.Tests;

/// <summary>
/// 飞书通知收件人的空白规整与校验。**2026-08-08 真事故的回归。**
///
/// 当时用户填进来的 open_id 中间夹了一个空格(比标准长度多 1 个字符),而只做了 <c>.Trim()</c>
/// —— 去不掉中间的。后果是白名单永远匹配不上，每条飞书消息都被静默丢弃，
/// 日志里一行报错都没有、所有健康检查全绿。现在同一个值决定通知发给谁，同样的错会让通知静默发丢。
/// (夹具一律用合成 open_id,不放真实标识。)
/// </summary>
public sealed class NotifyRecipientTests
{
    private const string Good = "ou_0123456789abcdef0123456789abcdef";

    [Fact]
    public void 中间夹空格的open_id被修好()
    {
        string bad = "ou_0123456789abcdef0 123456789abcdef";
        Assert.Equal(Good.Length + 1, bad.Length);

        Assert.Equal(Good, NotifyRecipient.Normalize(bad));
        Assert.True(NotifyRecipient.IsValid(bad));
    }

    [Theory]
    [InlineData(" ou_abc ", "ou_abc")]                       // 首尾
    [InlineData("ou_a\tb", "ou_ab")]                         // 制表符
    [InlineData("ou_a b", "ou_ab")]                     // 不换行空格(粘贴常见)
    [InlineData("ou_a\r\nb", "ou_ab")]                       // 换行
    [InlineData("ou_a​b", "ou_ab")]                     // 零宽空格
    public void 各种空白都剥掉(string input, string expected)
    {
        Assert.Equal(expected, NotifyRecipient.Normalize(input));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void 规整后为空要能被识别出来(string? input)
    {
        Assert.Equal(string.Empty, NotifyRecipient.Normalize(input));
        Assert.False(NotifyRecipient.IsValid(input));
    }

    [Theory]
    [InlineData("ou_")]                       // 只有前缀
    [InlineData("on_0123456789abcdef")]       // union_id 不是 open_id
    [InlineData("ou_a,ou_b")]                 // 只能有一个收件人
    [InlineData("ou_a-b")]                    // 非字母数字
    public void 不是单个open_id时判为无效(string input)
    {
        Assert.False(NotifyRecipient.IsValid(input));
    }

    [Fact]
    public void 正常值原样返回()
    {
        Assert.Equal(Good, NotifyRecipient.Normalize(Good));
        Assert.True(NotifyRecipient.IsValid(Good));
    }
}
