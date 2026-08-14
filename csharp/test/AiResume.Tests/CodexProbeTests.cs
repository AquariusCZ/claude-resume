using System.Net;
using System.Diagnostics;
using System.Text.Json;
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
    private static readonly CodexProbeResult DoctorOk =
        new(CodexReadiness.Ok, "ok", "已装好并可达,未验证授权", false);

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

    [Fact]
    public void doctor匿名路由的402不能解释成用户余额不足()
    {
        string json = RealDoctorOutput.Replace("HTTP 401", "HTTP 402");

        CodexProbeResult r = CodexProbe.ClassifyDoctorJson(json);

        Assert.Equal(CodexReadiness.Ok, r.Readiness);
        Assert.Equal("ok", r.Reason);
        Assert.DoesNotContain("余额", r.Summary, StringComparison.Ordinal);
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

    [Fact]
    public void 配置加载失败不得继续探测或显示可达()
    {
        string json = """
            {"checks":{
              "config.load":{"status":"fail","summary":"config could not be loaded","notes":["duplicate key notify"]},
              "installation":{"status":"ok"},
              "network.provider_reachability":{"status":"ok"}
            }}
            """;

        CodexProbeResult result = CodexProbe.ClassifyDoctorJson(json);

        Assert.Equal(CodexReadiness.Unknown, result.Readiness);
        Assert.Equal("config-error", result.Reason);
        Assert.Equal("Codex 配置无法加载:存在重复键", result.Summary);
    }

    [Theory]
    [InlineData("installation", "fail", CodexReadiness.NoCli, "install-error")]
    [InlineData("auth.credentials", "fail", CodexReadiness.Auth, "auth")]
    [InlineData("network.provider_reachability", "fail", CodexReadiness.Unreachable, "unreachable")]
    public void doctor关键检查同时识别fail和error(
        string checkId, string status, CodexReadiness readiness, string reason)
    {
        string json = JsonSerializer.Serialize(new
        {
            checks = new Dictionary<string, object>
            {
                [checkId] = new { status },
            },
        });

        CodexProbeResult result = CodexProbe.ClassifyDoctorJson(json);

        Assert.Equal(readiness, result.Readiness);
        Assert.Equal(reason, result.Reason);
    }

    [Fact]
    public void 配置失败的空Notes数组不会让分类器抛异常()
    {
        string json = """
            {"checks":{"config.load":{"status":"fail","summary":"config could not be loaded","notes":[]}}}
            """;

        CodexProbeResult result = CodexProbe.ClassifyDoctorJson(json);

        Assert.Equal("config-error", result.Reason);
        Assert.Equal("Codex 配置无法加载:详情请运行 codex doctor --json", result.Summary);
    }

    [Fact]
    public void 配置失败详情不回显路径端点或令牌形状()
    {
        string json = """
            {"checks":{"config.load":{"status":"fail","notes":["invalid TOML at C:\\\\Users\\\\alice\\\\.codex\\\\config.toml https://relay.example sk-secret-token-123456"]}}}
            """;

        CodexProbeResult result = CodexProbe.ClassifyDoctorJson(json);

        Assert.Equal("Codex 配置无法加载:TOML 语法错误", result.Summary);
        Assert.DoesNotContain("alice", result.Summary, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("relay.example", result.Summary, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("sk-secret", result.Summary, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void HTTP探针失败时只陈述Doctor证据不声称可达()
    {
        CodexProbeResult result = CodexProbe.FromAuthResult(
            new CodexAuthResult(CodexAuthOutcome.NetworkFailed, "网络不可达"));

        Assert.Equal(CodexReadiness.Ok, result.Readiness);
        Assert.Equal("unverified", result.Reason);
        Assert.Contains("doctor 通过", result.Summary, StringComparison.Ordinal);
        Assert.DoesNotContain("已装好并可达", result.Summary, StringComparison.Ordinal);
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

    [Fact]
    public void 鉴权通过但推理未核实不得成为绿色可用()
    {
        CodexProbeResult result = CodexProbe.FromAuthResult(new CodexAuthResult(
            CodexAuthOutcome.InferenceUnverified,
            "凭据已验证,推理权限未核实"));

        Assert.Equal(CodexReadiness.Ok, result.Readiness);
        Assert.False(result.DeepChecked);
        Assert.Equal("inference-unverified", result.Reason);
    }

    [Theory]
    [InlineData(HttpStatusCode.PaymentRequired, "http-402")]
    [InlineData(HttpStatusCode.TooManyRequests, "http-429")]
    public void 额度状态保留具体HTTP原因(HttpStatusCode status, string reason)
    {
        CodexProbeResult result = CodexProbe.FromAuthResult(CodexAuthProbe.Classify(status));

        Assert.Equal(CodexReadiness.Limited, result.Readiness);
        Assert.Equal(reason, result.Reason);
    }

    [Fact]
    public async Task shallow只发一次models且不会启动codexExec()
    {
        string home = NewCodexHome();
        var handler = new RequestCountingHandler(HttpStatusCode.OK);
        var probe = new CodexProbe(
            codexCommand: "definitely-not-a-real-codex-command",
            codexHome: home,
            authHandler: handler,
            doctorProbe: _ => Task.FromResult(DoctorOk));

        CodexProbeResult result = await probe.ProbeShallowAsync();

        Assert.Equal("inference-unverified", result.Reason);
        Assert.False(result.DeepChecked);
        Assert.Equal(new[] { "GET https://relay.example.invalid/models" }, handler.Requests);
    }

    [Fact]
    public async Task deep只发models加一次responses且不会启动codexExec()
    {
        string home = NewCodexHome();
        var handler = new RequestCountingHandler(HttpStatusCode.OK, HttpStatusCode.OK);
        var probe = new CodexProbe(
            codexCommand: "definitely-not-a-real-codex-command",
            codexHome: home,
            authHandler: handler,
            doctorProbe: _ => Task.FromResult(DoctorOk));

        CodexProbeResult result = await probe.ProbeDeepAsync();

        Assert.Equal(CodexReadiness.Ok, result.Readiness);
        Assert.True(result.DeepChecked);
        Assert.Equal(
            new[]
            {
                "GET https://relay.example.invalid/models",
                "POST https://relay.example.invalid/responses",
            },
            handler.Requests);
        Assert.Contains("\"max_output_tokens\":1", handler.Bodies[1]);
    }

    [Fact]
    public async Task Doctor与Http探针使用同一个环境解析出的CodexHome()
    {
        string home = NewCodexHome();
        ProcessStartInfo? observedStart = null;
        static string? EnvironmentFor(string expectedHome, string name) => name switch
        {
            "AI_RESUME_CODEX_HOME" => expectedHome,
            "CODEX_HOME" => @"C:\must-not-win",
            _ => null,
        };

        var doctorOnly = new CodexProbe(
            codexCommand: "definitely-not-a-real-codex-command",
            environmentVariable: name => EnvironmentFor(home, name),
            doctorStartObserver: start => observedStart = start);

        CodexProbeResult doctor = await doctorOnly.ProbeDoctorAsync();

        Assert.Equal(CodexReadiness.NoCli, doctor.Readiness);
        Assert.NotNull(observedStart);
        Assert.Equal(home, observedStart!.Environment["CODEX_HOME"]);

        var handler = new RequestCountingHandler(HttpStatusCode.OK);
        var shallow = new CodexProbe(
            codexHome: null,
            authHandler: handler,
            doctorProbe: _ => Task.FromResult(DoctorOk),
            environmentVariable: name => EnvironmentFor(home, name));

        CodexProbeResult result = await shallow.ProbeShallowAsync();

        Assert.Equal("inference-unverified", result.Reason);
        Assert.Equal(new[] { "GET https://relay.example.invalid/models" }, handler.Requests);
    }

    private static string NewCodexHome()
    {
        string home = TestTemp.NewDir("codex-probe-home");
        File.WriteAllText(
            Path.Combine(home, "config.toml"),
            """
            model_provider = "relay"
            model = "gpt-5.6-sol"

            [model_providers.relay]
            base_url = "https://relay.example.invalid"
            wire_api = "responses"
            """);
        File.WriteAllText(Path.Combine(home, "auth.json"), """{"OPENAI_API_KEY":"sk-test"}""");
        return home;
    }

    private sealed class RequestCountingHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode[] _statuses;
        private int _index;

        public RequestCountingHandler(params HttpStatusCode[] statuses) => _statuses = statuses;

        public List<string> Requests { get; } = new();

        public List<string> Bodies { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add($"{request.Method.Method} {request.RequestUri}");
            Bodies.Add(request.Content is null
                ? string.Empty
                : request.Content.ReadAsStringAsync(ct).GetAwaiter().GetResult());
            HttpStatusCode status = _statuses[Math.Min(_index++, _statuses.Length - 1)];
            var response = new HttpResponseMessage(status);
            if (request.Method == HttpMethod.Post && response.IsSuccessStatusCode)
            {
                response.Content = new StringContent(
                    """{"id":"resp_test","object":"response","status":"completed","output":[]}""");
            }

            return Task.FromResult(response);
        }
    }
}
