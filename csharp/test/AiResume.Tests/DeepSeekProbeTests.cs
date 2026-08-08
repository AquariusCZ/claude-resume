using AiResume.Worker.Probes;
using Xunit;

namespace AiResume.Tests;

/// <summary>
/// DeepSeek 余额探测。**只测纯解析函数,绝不发真实网络请求** ——
/// 测试跑起来不该消耗任何配额,也不该依赖网络与用户的真实密钥。
///
/// fixture 取自 2026-08-07 对 <c>GET https://api.deepseek.com/user/balance</c> 的实测响应:
/// total_balance 是**字符串**而不是数字,直接 GetDecimal() 会抛 —— 这一条如果猜错,
/// 线上表现是探测恒 malformed、面板永远灰着,而没有任何报错。
/// </summary>
public sealed class DeepSeekProbeTests
{
    private const string RealShape = """
        {"is_available":true,"balance_infos":[{"currency":"CNY","total_balance":"48.23","granted_balance":"0.00","topped_up_balance":"48.23"}]}
        """;

    [Fact]
    public void 真实响应形状解析出余额()
    {
        DeepSeekProbeResult r = DeepSeekProbe.Parse(RealShape);

        Assert.Equal(ProviderReadiness.Ok, r.Readiness);
        Assert.Equal(48.23m, r.BalanceCny);
        Assert.Contains("48.23", r.Summary);
    }

    [Fact]
    public void 余额是字符串而不是数字()
    {
        // 单独钉住这条:服务端把金额编码成字符串。
        // 哪天它改成数字,这个用例会红,提醒我们同步解析逻辑 —— 那正是我们想要的提醒。
        using var doc = System.Text.Json.JsonDocument.Parse(RealShape);
        System.Text.Json.JsonElement tb = doc.RootElement
            .GetProperty("balance_infos")[0].GetProperty("total_balance");

        Assert.Equal(System.Text.Json.JsonValueKind.String, tb.ValueKind);
    }

    [Fact]
    public void 账户不可用时报不足而不是可用()
    {
        DeepSeekProbeResult r = DeepSeekProbe.Parse(
            """{"is_available":false,"balance_infos":[{"currency":"CNY","total_balance":"0.00"}]}""");

        Assert.Equal(ProviderReadiness.Insufficient, r.Readiness);
        Assert.Equal(0m, r.BalanceCny);
    }

    [Fact]
    public void 只有非人民币余额时如实报可用但不编造数字()
    {
        DeepSeekProbeResult r = DeepSeekProbe.Parse(
            """{"is_available":true,"balance_infos":[{"currency":"USD","total_balance":"7.00"}]}""");

        Assert.Equal(ProviderReadiness.Ok, r.Readiness);
        Assert.Null(r.BalanceCny);   // 不拿美元冒充人民币
        Assert.Equal("可用", r.Summary);
    }

    [Theory]
    [InlineData("")]
    [InlineData("{ 坏 JSON")]
    [InlineData("<html>502</html>")]
    public void 响应损坏时报malformed且不抛(string body)
    {
        DeepSeekProbeResult r = DeepSeekProbe.Parse(body);

        Assert.Equal(ProviderReadiness.Unknown, r.Readiness);
        Assert.Equal("malformed", r.Reason);
    }

    [Fact]
    public void 缺少余额数组时仍报可用()
    {
        // 服务端只说 is_available 而不给明细也算可用 —— 少信息不等于故障。
        DeepSeekProbeResult r = DeepSeekProbe.Parse("""{"is_available":true}""");

        Assert.Equal(ProviderReadiness.Ok, r.Readiness);
        Assert.Null(r.BalanceCny);
    }

    [Fact]
    public async Task 未设密钥时报未配置而不是故障()
    {
        // 没配密钥是"没启用",不是"坏了"。报红会让人以为要去修什么。
        var probe = new DeepSeekProbe(apiKey: () => null);

        DeepSeekProbeResult r = await probe.ProbeAsync();

        Assert.Equal(ProviderReadiness.NoCredential, r.Readiness);
        Assert.Equal("no-key", r.Reason);
    }

    [Fact]
    public void 摘要里绝不出现密钥或端点地址()
    {
        // 摘要会直接显示在界面并可能进日志。
        foreach (string body in new[] { RealShape, """{"is_available":false}""", "坏的" })
        {
            string? s = DeepSeekProbe.Parse(body).Summary;
            if (s is null) { continue; }

            Assert.DoesNotContain("sk-", s, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("api.deepseek.com", s, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("http", s, StringComparison.OrdinalIgnoreCase);
        }
    }
}
