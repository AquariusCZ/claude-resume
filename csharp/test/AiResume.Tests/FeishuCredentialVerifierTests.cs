using AiResume.Worker.Migration;
using Xunit;

namespace AiResume.Tests;

/// <summary>
/// 「已配置」到底能不能用(审计 A2)。
///
/// 原缺陷:界面依据只有「DPAPI 里有值」—— 只能证明用户填过。
/// 审计用一份错误凭据换 token,飞书返回 <c>code=10003</c>,界面照旧显示"已配置/已保存"。
///
/// 这个区别不是学术性的:secret 在开放平台被重置后本机这份就永久失效,
/// 而失效的表现是**机器人不理你** —— 和进程没起来、open_id 夹空格、钩子断链一模一样。
///
/// **不联网**:这一组全部打在纯函数 Classify 上,不发任何请求。
/// </summary>
public sealed class FeishuCredentialVerifierTests
{
    [Fact]
    public void 签发了token才算有效()
    {
        var r = FeishuCredentialVerifier.Classify(
            200, """{"code":0,"msg":"ok","tenant_access_token":"t-abc","expire":7200}""");

        Assert.Equal(FeishuCredentialVerdict.Valid, r.Verdict);
        Assert.True(r.Ok);
    }

    [Fact]
    public void HTTP200里的错误码不能读成成功()
    {
        // 飞书把业务错误码放在响应体里。只看 HTTP 状态会把 code=10003 读成通过 ——
        // 这正是审计 A2 观察到的那一条(HTTP 200 / code=10003)。
        var r = FeishuCredentialVerifier.Classify(
            200, """{"code":10003,"msg":"invalid app_id or app_secret"}""");

        Assert.Equal(FeishuCredentialVerdict.Rejected, r.Verdict);
        Assert.False(r.Ok);
        Assert.Equal(10003, r.Code);
    }

    [Fact]
    public void 说了ok却没给token也不算通过()
    {
        var r = FeishuCredentialVerifier.Classify(200, """{"code":0,"msg":"ok"}""");

        Assert.NotEqual(FeishuCredentialVerdict.Valid, r.Verdict);
        Assert.False(r.Ok);
    }

    [Fact]
    public void 被拒时要给出可查的原因()
    {
        var r = FeishuCredentialVerifier.Classify(400, """{"code":99991663,"msg":"app disabled"}""");

        Assert.Equal(FeishuCredentialVerdict.Rejected, r.Verdict);
        // 用户拿到 code 才能去开放平台对照;msg 与 code 都不是机密。
        Assert.Contains("99991663", r.Summary);
    }

    [Theory]
    [InlineData("")]
    [InlineData("<html>502 Bad Gateway</html>")]
    [InlineData("""{"unexpected":true}""")]
    public void 读不懂就说读不懂不猜结论(string body)
    {
        var r = FeishuCredentialVerifier.Classify(502, body);

        Assert.Equal(FeishuCredentialVerdict.Unreadable, r.Verdict);
        Assert.False(r.Ok);
    }

    [Fact]
    public void 网络失败绝不能判成凭据失效()
    {
        // 混为一谈会让用户去开放平台重置一个其实好好的 secret,
        // 而真正的问题(断网/代理)一直没被看见。
        var r = new FeishuVerifyResult(
            FeishuCredentialVerdict.NetworkFailed, null, "HttpRequestException",
            "请求没走通(HttpRequestException)。这是网络问题,不能据此判断凭据失效。");

        Assert.False(r.Ok);
        Assert.Contains("不能据此判断凭据失效", r.Summary);
    }

    [Fact]
    public void 只有Valid才是Ok()
    {
        foreach (FeishuCredentialVerdict v in Enum.GetValues<FeishuCredentialVerdict>())
        {
            var r = new FeishuVerifyResult(v, null, null, "");
            Assert.Equal(v == FeishuCredentialVerdict.Valid, r.Ok);
        }
    }
}
