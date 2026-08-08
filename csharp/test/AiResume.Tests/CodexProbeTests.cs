using AiResume.Worker.Probes;
using Xunit;

namespace AiResume.Tests;

/// <summary>
/// Codex shallow 探测的判定。**只测纯解析函数,不起 codex 进程、不发任何请求**。
///
/// 这个文件的存在本身就是一次事故复盘:原实现把
/// <c>network.provider_reachability.details</c> 里出现的 401 一律判成「认证被拒」,
/// 面板因此长期红着,而用户的 key 完全正常。下面第一个 fixture 是
/// 2026-08-07 从本机 <c>codex doctor --json</c> 原样抄来的输出 ——
/// 它同时含 HTTP 200 和 HTTP 401,而 doctor 自己判 ok。
/// </summary>
public sealed class CodexProbeTests
{
    /// <summary>
    /// 本机实测输出(自定义 provider,不走 OpenAI 登录)。
    /// 注意 route probe 的原文是 "route exists (HTTP 401)" —— 那是一次**不带凭据**的
    /// 连通性探测,401 恰恰证明路由在且需要认证。
    /// </summary>
    private const string RealDoctorOutput = """
        {
          "schemaVersion": 1,
          "overallStatus": "ok",
          "checks": {
            "installation": { "id": "installation", "status": "ok", "summary": "codex is installed" },
            "auth.credentials": {
              "id": "auth.credentials",
              "status": "ok",
              "summary": "OpenAI auth is not required for the active model provider",
              "details": {
                "auth storage mode": "File",
                "model provider requires OpenAI auth": "false"
              }
            },
            "network.provider_reachability": {
              "id": "network.provider_reachability",
              "status": "ok",
              "summary": "active provider endpoints are reachable over HTTP",
              "details": {
                "OpenAI API base URL": "https://relay.example.invalid reachable (HTTP 200)",
                "OpenAI API route probe": "https://relay.example.invalid/models route exists (HTTP 401)",
                "reachability mode": "provider auth"
              }
            }
          }
        }
        """;

    [Fact]
    public void 未认证路由探测的401不判成认证失败()
    {
        CodexProbeResult r = CodexProbe.ClassifyDoctorJson(RealDoctorOutput);

        // 曾经这里返回 Auth / "认证被拒(HTTP 401)"。那是把 doctor 的语义读反了。
        Assert.Equal(CodexReadiness.Ok, r.Readiness);
        Assert.Equal("ok", r.Reason);
        Assert.False(r.DeepChecked);
    }

    [Fact]
    public void shallow不声称授权已验证()
    {
        // shallow 最好的结论只能是「可达」。绿灯必须来自 deep 的真实请求 ——
        // 项目铁律:"配了 key""装好了"都不算可用。
        CodexProbeResult r = CodexProbe.ClassifyDoctorJson(RealDoctorOutput);

        Assert.False(r.DeepChecked);
        Assert.Contains("未验证授权", r.Summary);
    }

    [Fact]
    public void 路由探测403同样不判成认证失败()
    {
        string json = RealDoctorOutput.Replace("HTTP 401", "HTTP 403");

        Assert.Equal(CodexReadiness.Ok, CodexProbe.ClassifyDoctorJson(json).Readiness);
    }

    [Fact]
    public void auth检查报error才判认证失败()
    {
        // 这一条是**真的**授权信号:doctor 自己把 auth.credentials 判成 error。
        // 注意 details 里照样留着 401 —— 判定必须来自 status,而不是那个 401。
        string json = """
            {"checks":{
               "auth.credentials":{"status":"error","summary":"missing credentials"},
               "network.provider_reachability":{"status":"ok",
                 "details":{"OpenAI API route probe":"https://x/models route exists (HTTP 401)"}}}}
            """;

        CodexProbeResult r = CodexProbe.ClassifyDoctorJson(json);

        Assert.Equal(CodexReadiness.Auth, r.Readiness);
        Assert.Equal("auth", r.Reason);
    }

    [Fact]
    public void 限流429仍要判出来()
    {
        // 429 是 doctor 没有用 status 表达出来的信号,必须自己从 details 里捞。
        string json = RealDoctorOutput.Replace("HTTP 401", "HTTP 429");

        CodexProbeResult r = CodexProbe.ClassifyDoctorJson(json);

        Assert.Equal(CodexReadiness.Limited, r.Readiness);
        Assert.Equal("http-429", r.Reason);
    }

    [Theory]
    [InlineData("500")]
    [InlineData("502")]
    [InlineData("503")]
    public void 网关5xx判成不可达(string code)
    {
        string json = RealDoctorOutput.Replace("HTTP 401", "HTTP " + code);

        CodexProbeResult r = CodexProbe.ClassifyDoctorJson(json);

        Assert.Equal(CodexReadiness.Unreachable, r.Readiness);
        Assert.Equal("http-" + code, r.Reason);
    }

    [Fact]
    public void reachability报error判成不可达()
    {
        string json = """
            {"checks":{"network.provider_reachability":{"status":"error","summary":"dns failure"}}}
            """;

        Assert.Equal(CodexReadiness.Unreachable, CodexProbe.ClassifyDoctorJson(json).Readiness);
    }

    [Fact]
    public void 安装检查报error判成未安装()
    {
        string json = """
            {"checks":{"installation":{"status":"error","summary":"codex not on PATH"}}}
            """;

        Assert.Equal(CodexReadiness.NoCli, CodexProbe.ClassifyDoctorJson(json).Readiness);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json at all")]
    [InlineData("{}")]
    [InlineData("[1,2,3]")]
    [InlineData("""{"schemaVersion":1}""")]
    public void 输出不可解析或缺checks判Unknown(string stdout)
    {
        CodexProbeResult r = CodexProbe.ClassifyDoctorJson(stdout);

        // 拿不准就是 Unknown(灰),绝不猜一个绿灯或红灯出来。
        Assert.Equal(CodexReadiness.Unknown, r.Readiness);
        Assert.Equal("malformed", r.Reason);
    }

    [Fact]
    public void details缺失时仍给出可达结论()
    {
        string json = """
            {"checks":{"installation":{"status":"ok"},
                       "network.provider_reachability":{"status":"ok"}}}
            """;

        Assert.Equal(CodexReadiness.Ok, CodexProbe.ClassifyDoctorJson(json).Readiness);
    }
}
