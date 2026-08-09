using System.Net;
using AiResume.Worker.Probes;
using Xunit;

namespace AiResume.Tests;

/// <summary>
/// Codex 真实授权探测。**不联网、不读用户真实的 ~/.codex**——
/// HTTP 层用假 handler,配置层用临时目录里的假 config.toml / auth.json。
///
/// 这个探测存在的理由(2026-08-08 实测):
/// - `codex exec` 能验证授权,但 10-12 秒、**23,220 tokens**;
/// - 带凭据 GET /v1/models **1.3 秒、0 token**,且 200/401 完全由凭据决定
///   (同一请求带 key → 200 返回模型列表;去掉 key → 401 API_KEY_REQUIRED)。
/// 于是"每次探测都是真的"才成立。
/// </summary>
public sealed class CodexAuthProbeTests : IDisposable
{
    private readonly List<string> _dirs = new();

    public void Dispose()
    {
        foreach (string d in _dirs)
        {
            try
            {
                Directory.Delete(d, recursive: true);
            }
            catch (IOException)
            {
                // 清不掉就留给系统 temp 回收,不影响判定。
            }
        }
    }

    private string NewCodexHome(string? configToml, string? authJson)
    {
        string dir = TestTemp.NewDir("codexhome");
        Directory.CreateDirectory(dir);
        _dirs.Add(dir);
        if (configToml is not null)
        {
            File.WriteAllText(Path.Combine(dir, "config.toml"), configToml);
        }

        if (authJson is not null)
        {
            File.WriteAllText(Path.Combine(dir, "auth.json"), authJson);
        }

        return dir;
    }

    /// <summary>照抄本机 config.toml 的真实形态:顶层键在前,provider 段在后。</summary>
    private const string RealisticConfig = """
        model_provider = "OpenAI"
        model = "gpt-5.6-sol"
        model_reasoning_effort = "xhigh"

        [model_providers.OpenAI]
        name = "OpenAI"
        base_url = "https://relay.example.invalid"
        wire_api = "responses"
        requires_openai_auth = false

        [model_providers.deepseek]
        name = "deepseek"
        base_url = "https://api.deepseek.example/"
        """;

    [Fact]
    public void 读出活动provider的base_url与凭据()
    {
        string home = NewCodexHome(RealisticConfig, """{"OPENAI_API_KEY":"sk-test-not-a-real-key"}""");

        (string? baseUrl, string? apiKey) = CodexAuthProbe.ReadActiveProvider(home);

        // 必须取 model_provider 指向的那一段,而不是文件里的第一个/最后一个 provider。
        Assert.Equal("https://relay.example.invalid", baseUrl);
        Assert.Equal("sk-test-not-a-real-key", apiKey);
    }

    [Fact]
    public void 单引号字符串同样认()
    {
        string home = NewCodexHome(
            "model_provider = 'X'\n[model_providers.X]\nbase_url = 'https://x.example'\n",
            """{"OPENAI_API_KEY":"k"}""");

        Assert.Equal("https://x.example", CodexAuthProbe.ReadActiveProvider(home).BaseUrl);
    }

    [Theory]
    [InlineData(null, """{"OPENAI_API_KEY":"k"}""")]              // 没有 config.toml
    [InlineData("model_provider = \"OpenAI\"\n", null)]           // 没有 auth.json
    [InlineData("model = \"x\"\n", """{"OPENAI_API_KEY":"k"}""")] // 没写 model_provider
    [InlineData("model_provider = \"Nope\"\n[model_providers.Other]\nbase_url = \"https://o\"\n",
                """{"OPENAI_API_KEY":"k"}""")]                     // 指向不存在的段
    public void 读不全就返回null而不是猜(string? cfg, string? auth)
    {
        string home = NewCodexHome(cfg, auth);

        (string? baseUrl, string? apiKey) = CodexAuthProbe.ReadActiveProvider(home);

        Assert.True(baseUrl is null || apiKey is null);
    }

    [Fact]
    public async Task 读不到配置时明确报NotConfigured而不是认证失败()
    {
        string home = NewCodexHome(null, null);

        CodexAuthResult r = await CodexAuthProbe.ProbeAsync(home);

        // 关键:缺配置 ≠ 凭据被拒。混为一谈会让人去查一个根本没问题的 key。
        Assert.Equal(CodexAuthOutcome.NotConfigured, r.Outcome);
    }

    [Theory]
    [InlineData(HttpStatusCode.OK, CodexAuthOutcome.Authorized)]
    [InlineData(HttpStatusCode.NoContent, CodexAuthOutcome.Authorized)]
    [InlineData(HttpStatusCode.Unauthorized, CodexAuthOutcome.Rejected)]
    [InlineData(HttpStatusCode.Forbidden, CodexAuthOutcome.Rejected)]
    [InlineData(HttpStatusCode.TooManyRequests, CodexAuthOutcome.Limited)]
    [InlineData(HttpStatusCode.BadGateway, CodexAuthOutcome.ServerError)]
    [InlineData(HttpStatusCode.ServiceUnavailable, CodexAuthOutcome.ServerError)]
    [InlineData(HttpStatusCode.NotFound, CodexAuthOutcome.NetworkFailed)]
    public void 状态码映射(HttpStatusCode status, CodexAuthOutcome expected)
    {
        Assert.Equal(expected, CodexAuthProbe.Classify(status).Outcome);
    }

    [Fact]
    public async Task 带凭据请求成功判Authorized()
    {
        string home = NewCodexHome(RealisticConfig, """{"OPENAI_API_KEY":"sk-test"}""");
        var handler = new CapturingHandler(HttpStatusCode.OK);

        CodexAuthResult r = await CodexAuthProbe.ProbeAsync(home, handler);

        Assert.Equal(CodexAuthOutcome.Authorized, r.Outcome);
        Assert.Equal("https://relay.example.invalid/v1/models", handler.Uri);
        Assert.Equal("Bearer sk-test", handler.Authorization);
        // Cloudflare 会按 UA 拦默认 HTTP 客户端(实测 403 error code: 1010),
        // 那会被读成"认证失败"——正是这个探测要避免的错。UA 必须带。
        Assert.Contains("Mozilla/5.0", handler.UserAgent);
    }

    [Fact]
    public async Task 列得出模型还要再问一句跑不跑得动()
    {
        string home = NewCodexHome(RealisticConfig, """{"OPENAI_API_KEY":"sk-test"}""");
        var handler = new CapturingHandler(HttpStatusCode.OK, HttpStatusCode.OK);

        CodexAuthResult r = await CodexAuthProbe.ProbeAsync(home, handler);

        Assert.Equal(CodexAuthOutcome.Authorized, r.Outcome);
        Assert.Equal(
            new[] { "https://relay.example.invalid/v1/models", "https://relay.example.invalid/v1/chat/completions" },
            handler.Uris);
        Assert.Equal(new[] { "GET", "POST" }, handler.Methods);
        // 用**用户配置里那个模型**,而不是随便挑一个能跑的。
        Assert.Contains("gpt-5.6-sol", handler.Bodies[1]);
        // 每次探测都要真发,所以必须便宜到可以忽略:1 个 token。
        Assert.Contains("\"max_tokens\":1", handler.Bodies[1]);
        Assert.Contains("推理", r.Detail);
    }

    [Fact]
    public async Task 能列模型但不允许推理不得判成可用()
    {
        // 审计 A6 的原始注入条件:/v1/models 返 200,推理路由返 403。
        // 原来这种组合界面绿着写"凭据已验证",而任务一跑就失败。
        string home = NewCodexHome(RealisticConfig, """{"OPENAI_API_KEY":"sk-test"}""");
        var handler = new CapturingHandler(HttpStatusCode.OK, HttpStatusCode.Forbidden);

        CodexAuthResult r = await CodexAuthProbe.ProbeAsync(home, handler);

        Assert.Equal(CodexAuthOutcome.NoInference, r.Outcome);
        Assert.Contains("不允许推理", r.Detail);
    }

    [Fact]
    public async Task 列模型没过就不再打推理那一枪()
    {
        string home = NewCodexHome(RealisticConfig, """{"OPENAI_API_KEY":"sk-test"}""");
        var handler = new CapturingHandler(HttpStatusCode.Unauthorized);

        CodexAuthResult r = await CodexAuthProbe.ProbeAsync(home, handler);

        Assert.Equal(CodexAuthOutcome.Rejected, r.Outcome);
        // 前提不成立时再问"允不允许干活"没有意义,还白费一次请求。
        Assert.Single(handler.Uris);
    }

    [Fact]
    public async Task 没写model时不猜一个模型去探()
    {
        string home = NewCodexHome(
            "model_provider = \"X\"\n[model_providers.X]\nbase_url = \"https://x.example\"\n",
            """{"OPENAI_API_KEY":"k"}""");
        var handler = new CapturingHandler(HttpStatusCode.OK);

        CodexAuthResult r = await CodexAuthProbe.ProbeAsync(home, handler);

        Assert.Equal(CodexAuthOutcome.InferenceUnverified, r.Outcome);
        Assert.Single(handler.Uris);
        // 说清"验到哪一步"比给一个漂亮但没依据的结论重要。
        Assert.Contains("未核实", r.Detail);
    }

    [Theory]
    [InlineData(HttpStatusCode.OK, CodexAuthOutcome.Authorized)]
    [InlineData(HttpStatusCode.Unauthorized, CodexAuthOutcome.NoInference)]
    [InlineData(HttpStatusCode.Forbidden, CodexAuthOutcome.NoInference)]
    [InlineData(HttpStatusCode.TooManyRequests, CodexAuthOutcome.Limited)]
    [InlineData(HttpStatusCode.InternalServerError, CodexAuthOutcome.ServerError)]
    // 端点形状不支持 ≠ 没有推理权限。sub2api 各家路由不一,
    // 把"这家不认识 chat/completions"标成红,是把好配置误判成坏的。
    [InlineData(HttpStatusCode.NotFound, CodexAuthOutcome.InferenceUnverified)]
    [InlineData(HttpStatusCode.BadRequest, CodexAuthOutcome.InferenceUnverified)]
    public void 推理状态码映射(HttpStatusCode status, CodexAuthOutcome expected)
    {
        Assert.Equal(expected, CodexAuthProbe.ClassifyInference(status).Outcome);
    }

    [Fact]
    public async Task 推理那一枪打不通不得反过来否定凭据()
    {
        string home = NewCodexHome(RealisticConfig, """{"OPENAI_API_KEY":"sk-test"}""");
        var handler = new SequenceHandler(
            _ => new HttpResponseMessage(HttpStatusCode.OK),
            _ => throw new HttpRequestException("connection reset"));

        CodexAuthResult r = await CodexAuthProbe.ProbeAsync(home, handler);

        // 凭据那一关已经过了。因为第二枪没打通就把凭据判成坏的,
        // 会让人去换一把其实没问题的 key。
        Assert.Equal(CodexAuthOutcome.InferenceUnverified, r.Outcome);
        Assert.Contains("未核实", r.Detail);
    }

    private sealed class SequenceHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage>[] _steps;
        private int _i;

        public SequenceHandler(params Func<HttpRequestMessage, HttpResponseMessage>[] steps) => _steps = steps;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Func<HttpRequestMessage, HttpResponseMessage> step = _steps[Math.Min(_i++, _steps.Length - 1)];
            try
            {
                return Task.FromResult(step(request));
            }
            catch (Exception ex)
            {
                return Task.FromException<HttpResponseMessage>(ex);
            }
        }
    }

    [Fact]
    public async Task 网络失败不判成认证失败()
    {
        string home = NewCodexHome(RealisticConfig, """{"OPENAI_API_KEY":"sk-test"}""");
        var handler = new ThrowingHandler(new HttpRequestException("no such host"));

        CodexAuthResult r = await CodexAuthProbe.ProbeAsync(home, handler);

        // DNS/TCP/TLS 失败是本地失败。断网时面板红着说"认证被拒",
        // 会把排查方向带到完全错误的地方。
        Assert.Equal(CodexAuthOutcome.NetworkFailed, r.Outcome);
    }

    /// <summary>
    /// 记录**每一次**请求。探测现在是两步(列模型 → 最小推理),
    /// 只记最后一次会让"到底打了哪几个端点"无从断言。
    /// </summary>
    private sealed class CapturingHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _first;
        private readonly HttpStatusCode? _second;
        private int _count;

        /// <param name="first">/v1/models 的状态码。</param>
        /// <param name="second">最小推理请求的状态码;null 表示与 first 相同。</param>
        public CapturingHandler(HttpStatusCode first, HttpStatusCode? second = null)
        {
            _first = first;
            _second = second;
        }

        public List<string> Uris { get; } = new();

        public List<string> Methods { get; } = new();

        public List<string> Bodies { get; } = new();

        public string? Uri => Uris.Count > 0 ? Uris[0] : null;

        public string? Authorization { get; private set; }

        public string? UserAgent { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Uris.Add(request.RequestUri?.ToString() ?? string.Empty);
            Methods.Add(request.Method.Method);
            Bodies.Add(request.Content is null
                ? string.Empty
                : request.Content.ReadAsStringAsync(ct).GetAwaiter().GetResult());
            Authorization = request.Headers.TryGetValues("Authorization", out IEnumerable<string>? a)
                ? string.Join(",", a) : null;
            UserAgent = request.Headers.TryGetValues("User-Agent", out IEnumerable<string>? u)
                ? string.Join(" ", u) : null;

            HttpStatusCode status = _count++ == 0 ? _first : (_second ?? _first);
            return Task.FromResult(new HttpResponseMessage(status));
        }
    }

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        private readonly Exception _ex;

        public ThrowingHandler(Exception ex) => _ex = ex;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => Task.FromException<HttpResponseMessage>(_ex);
    }
}
