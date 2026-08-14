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
/// - 带凭据 GET /models **1.3 秒、0 token**,且 200/401 完全由凭据决定
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
        requires_openai_auth = true

        [model_providers.deepseek]
        name = "deepseek"
        base_url = "https://api.deepseek.example/"
        """;

    [Fact]
    public void CodexHome按显式参数再AiResume环境再Codex环境解析()
    {
        static string? Env(string name) => name switch
        {
            "AI_RESUME_CODEX_HOME" => @"C:\ai-resume-codex",
            "CODEX_HOME" => @"C:\codex-home",
            _ => null,
        };

        Assert.Equal(@"C:\explicit", CodexAuthProbe.ResolveCodexHome(@"C:\explicit", Env));
        Assert.Equal(@"C:\ai-resume-codex", CodexAuthProbe.ResolveCodexHome(null, Env));
        Assert.Equal(
            @"C:\codex-home",
            CodexAuthProbe.ResolveCodexHome(null, name => name == "CODEX_HOME" ? @"C:\codex-home" : null));
    }

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

    [Fact]
    public void 尾注释与引号式provider表头同样认()
    {
        string home = NewCodexHome(
            """
            model_provider = "Sub2API" # active provider

            [model_providers."Sub2API"]
            base_url = "https://x.example/v1" # endpoint
            """,
            """{"OPENAI_API_KEY":"k"}""");

        Assert.Equal("https://x.example/v1", CodexAuthProbe.ReadActiveProvider(home).BaseUrl);
    }

    [Theory]
    [InlineData("model_provider = \"OpenAI\"\n", null)]           // 没有 auth.json
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
    [InlineData(HttpStatusCode.PaymentRequired, CodexAuthOutcome.Limited)]
    [InlineData(HttpStatusCode.TooManyRequests, CodexAuthOutcome.Limited)]
    [InlineData(HttpStatusCode.BadGateway, CodexAuthOutcome.ServerError)]
    [InlineData(HttpStatusCode.ServiceUnavailable, CodexAuthOutcome.ServerError)]
    [InlineData(HttpStatusCode.NotFound, CodexAuthOutcome.NetworkFailed)]
    public void 状态码映射(HttpStatusCode status, CodexAuthOutcome expected)
    {
        Assert.Equal(expected, CodexAuthProbe.Classify(status).Outcome);
    }

    /// <summary>
    /// 2026-08-13 用默认 UA 请求本机活动 provider 录到的真实 403 体,域名与 ray_id 换成合成值。
    /// 换成探针的浏览器 UA 后同一把凭据立刻 200 —— 拦的是客户端,不是凭据。
    /// </summary>
    private const string CloudflareBlockBody = """
        {"title":"Error 1010: Access denied","status":403,
         "detail":"The site owner has blocked access based on your browser's signature.",
         "error_code":1010,"error_name":"browser_signature_banned",
         "error_category":"access_denied","zone":"relay.example.invalid",
         "cloudflare_error":true,"retryable":false}
        """;

    [Fact]
    public void CDN拦截的403判成未核实而不是凭据被拒()
    {
        CodexAuthResult models = CodexAuthProbe.Classify(
            HttpStatusCode.Forbidden, usedCredential: true, body: CloudflareBlockBody);
        CodexAuthResult inference = CodexAuthProbe.ClassifyInference(
            HttpStatusCode.Forbidden, usedCredential: true, body: CloudflareBlockBody);

        Assert.Equal(CodexAuthOutcome.NetworkFailed, models.Outcome);
        Assert.Equal("cdn-blocked", models.Reason);
        Assert.Equal(CodexAuthOutcome.NetworkFailed, inference.Outcome);
        Assert.Equal("cdn-blocked", inference.Reason);
    }

    [Theory]
    // 401 一律是凭据问题:Cloudflare 的 1xxx 只走 403,不能因为体里有关键字就放过 401。
    [InlineData(HttpStatusCode.Unauthorized, """{"error_code":1010,"cloudflare_error":true}""")]
    [InlineData(HttpStatusCode.Forbidden, """{"error":{"message":"invalid api key"}}""")]
    [InlineData(HttpStatusCode.Forbidden, "")]
    [InlineData(HttpStatusCode.Forbidden, "cloudflare 只有一个标记不足以判定")]
    [InlineData(HttpStatusCode.Forbidden, """{"error_code":403,"message":"forbidden"}""")]
    public void 非CDN拦截的拒绝仍判凭据被拒(HttpStatusCode status, string body)
    {
        Assert.Equal(
            CodexAuthOutcome.Rejected,
            CodexAuthProbe.Classify(status, usedCredential: true, body: body).Outcome);
    }

    [Fact]
    public void 非JSON的CDN拦截页需要两个标记同时出现()
    {
        const string page =
            "<html><body><h1>Error 1010</h1><p>Ray ID: abc</p>" +
            "<div>Performance &amp; security by Cloudflare</div></body></html>";

        Assert.Equal(
            CodexAuthOutcome.NetworkFailed,
            CodexAuthProbe.Classify(HttpStatusCode.Forbidden, body: page).Outcome);
    }

    [Fact]
    public async Task 带凭据请求成功判Authorized()
    {
        string home = NewCodexHome(RealisticConfig, """{"OPENAI_API_KEY":"sk-test"}""");
        var handler = new CapturingHandler(HttpStatusCode.OK);

        CodexAuthResult r = await CodexAuthProbe.ProbeAsync(home, handler);

        Assert.Equal(CodexAuthOutcome.Authorized, r.Outcome);
        Assert.Equal("https://relay.example.invalid/models", handler.Uri);
        Assert.Equal("Bearer sk-test", handler.Authorization);
        // Cloudflare 会按 UA 拦默认 HTTP 客户端(实测 403 error code: 1010),
        // 那会被读成"认证失败"——正是这个探测要避免的错。UA 必须带。
        Assert.Contains("Mozilla/5.0", handler.UserAgent);
    }

    [Fact]
    public void 没有配置文件时按Codex默认内置OpenAi解析()
    {
        string home = NewCodexHome(null, """{"OPENAI_API_KEY":"k"}""");

        CodexProviderCredentials provider = CodexAuthProbe.ReadActiveProviderCredentials(home);

        Assert.True(provider.IsBuiltInOpenAi);
        Assert.Equal("openai", provider.ProviderId);
        Assert.Equal("https://api.openai.com/v1", provider.BaseUrl);
    }

    [Fact]
    public async Task baseUrl已含v1时不重复拼接()
    {
        string home = NewCodexHome(
            RealisticConfig.Replace("https://relay.example.invalid", "https://relay.example.invalid/v1"),
            """{"OPENAI_API_KEY":"sk-test"}""");
        var handler = new CapturingHandler(HttpStatusCode.OK, HttpStatusCode.OK);

        CodexAuthResult r = await CodexAuthProbe.ProbeAsync(home, handler);

        Assert.Equal(CodexAuthOutcome.Authorized, r.Outcome);
        Assert.Equal(
            new[] { "https://relay.example.invalid/v1/models", "https://relay.example.invalid/v1/responses" },
            handler.Uris);
    }

    [Fact]
    public async Task ChatGPTBackend路径不插入v1()
    {
        string home = NewCodexHome(
            RealisticConfig.Replace(
                "https://relay.example.invalid",
                "https://chatgpt.com/backend-api/codex"),
            """{"auth_mode":"chatgpt","tokens":{"access_token":"chatgpt-access","account_id":"acct-1"}}""");
        var handler = new CapturingHandler(HttpStatusCode.OK, HttpStatusCode.OK);

        CodexAuthResult result = await CodexAuthProbe.ProbeAsync(home, handler);

        Assert.Equal(CodexAuthOutcome.Authorized, result.Outcome);
        Assert.Equal(
            new[]
            {
                "https://chatgpt.com/backend-api/codex/models",
                "https://chatgpt.com/backend-api/codex/responses",
            },
            handler.Uris);
    }

    [Fact]
    public async Task 内置OpenAi无需显式provider表即可按AuthMode解析端点()
    {
        string home = NewCodexHome(
            "model = \"gpt-5.5\"\n",
            """{"auth_mode":"chatgpt","tokens":{"access_token":"chatgpt-access","account_id":"acct-1"}}""");
        var handler = new CapturingHandler(HttpStatusCode.OK, HttpStatusCode.OK);

        CodexAuthResult result = await CodexAuthProbe.ProbeAsync(home, handler);

        Assert.Equal(CodexAuthOutcome.Authorized, result.Outcome);
        Assert.Equal(
            new[]
            {
                "https://chatgpt.com/backend-api/codex/models",
                "https://chatgpt.com/backend-api/codex/responses",
            },
            handler.Uris);
    }

    [Fact]
    public async Task baseUrl格式无效时返回未配置而不是抛到GUI()
    {
        string home = NewCodexHome(
            "model_provider = \"X\"\n[model_providers.X]\nbase_url = \"not a url\"\n",
            """{"OPENAI_API_KEY":"k"}""");

        CodexAuthResult r = await CodexAuthProbe.ProbeAsync(home);

        Assert.Equal(CodexAuthOutcome.NotConfigured, r.Outcome);
        Assert.Contains("格式无效", r.Detail);
    }

    [Fact]
    public async Task 列得出模型还要再问一句跑不跑得动()
    {
        string home = NewCodexHome(RealisticConfig, """{"OPENAI_API_KEY":"sk-test"}""");
        var handler = new CapturingHandler(HttpStatusCode.OK, HttpStatusCode.OK);

        CodexAuthResult r = await CodexAuthProbe.ProbeAsync(home, handler);

        Assert.Equal(CodexAuthOutcome.Authorized, r.Outcome);
        Assert.Equal(
            new[] { "https://relay.example.invalid/models", "https://relay.example.invalid/responses" },
            handler.Uris);
        Assert.Equal(new[] { "GET", "POST" }, handler.Methods);
        // 用**用户配置里那个模型**,而不是随便挑一个能跑的。
        Assert.Contains("gpt-5.6-sol", handler.Bodies[1]);
        // 每次探测都要真发,所以必须便宜到可以忽略:1 个 token。
        Assert.Contains("\"max_output_tokens\":1", handler.Bodies[1]);
        Assert.Contains("\"input\":\"1\"", handler.Bodies[1]);
        Assert.Contains("推理", r.Detail);
    }

    [Fact]
    public async Task 查询参数按RFC3986逐个转义而不是拼裸字符串()
    {
        // 之前只有一个 api-version=2026-01-01 这种纯 ASCII 用例过了 ——
        // 而真正会出事的是 & = 空格 + / 和非 ASCII:拼裸串会让一个参数把后面的
        // 参数"注入"掉,provider 收到的是另一组条件,而我们以为问的是这一组。
        string home = NewCodexHome(
            """
            model_provider = "relay"
            model = "gpt-test"

            [model_providers.relay]
            base_url = "https://relay.example.invalid/v1"
            wire_api = "responses"
            requires_openai_auth = true
            query_params = { "a&b=c" = "x y+z/w", tenant = "研发&测试", empty = "" }
            """,
            """{"auth_mode":"apikey","OPENAI_API_KEY":"sk-real-probe-key"}""");
        var handler = new CapturingHandler(HttpStatusCode.OK, HttpStatusCode.OK);

        CodexAuthResult result = await CodexAuthProbe.ProbeAsync(home, handler, _ => null);

        Assert.Equal(CodexAuthOutcome.Authorized, result.Outcome);
        string uri = handler.Uris[0];
        // 键里的 & 和 = 必须被转义,否则它会被读成"新参数"和"赋值"。
        Assert.Contains("a%26b%3Dc=x%20y%2Bz%2Fw", uri, StringComparison.Ordinal);
        // 非 ASCII 走 UTF-8 百分号编码,不是原样塞进 URL。
        Assert.Contains("tenant=%E7%A0%94%E5%8F%91%26%E6%B5%8B%E8%AF%95", uri, StringComparison.Ordinal);
        // 空值保留为 key=,不能整条丢掉:有些 relay 用空值表达"该开关存在"。
        Assert.Contains("empty=", uri, StringComparison.Ordinal);
        Assert.DoesNotContain("研发", uri, StringComparison.Ordinal);
    }

    [Theory]
    // 上游 Codex 的 query_params / http_headers 都是 HashMap<String, String>,
    // 非字符串值在 Codex 自己那里就反序列化失败。我们跟着失败关闭,而不是
    // 悄悄丢掉那个参数 —— 丢掉会让请求带着一组不完整的条件发出去。
    [InlineData("""query_params = { version = 2 }""")]
    [InlineData("""query_params = { flag = true }""")]
    [InlineData("""http_headers = { X-Count = 3 }""")]
    [InlineData("""env_http_headers = { X-Env = 7 }""")]
    [InlineData("""query_params = "not-a-table" """)]
    public void 非字符串的映射值让整份provider配置失败关闭(string line)
    {
        string home = NewCodexHome(
            $"""
            model_provider = "relay"
            model = "gpt-test"

            [model_providers.relay]
            base_url = "https://relay.example.invalid/v1"
            requires_openai_auth = true
            {line}
            """,
            """{"auth_mode":"apikey","OPENAI_API_KEY":"sk-real-probe-key"}""");

        CodexProviderCredentials provider = CodexAuthProbe.ReadActiveProviderCredentials(home);

        Assert.Null(provider.BaseUrl);
        Assert.False(string.IsNullOrWhiteSpace(provider.Problem));
    }

    [Fact]
    public void 请求头拒绝换行与非法名字符以挡住头注入()
    {
        string home = NewCodexHome(
            """
            model_provider = "relay"
            model = "gpt-test"

            [model_providers.relay]
            base_url = "https://relay.example.invalid/v1"
            requires_openai_auth = true
            http_headers = { "X-Good" = "fine", "X-Bad" = "a\nX-Injected: 1", "Bad Name" = "v" }
            """,
            """{"auth_mode":"apikey","OPENAI_API_KEY":"sk-real-probe-key"}""");

        CodexProviderCredentials provider = CodexAuthProbe.ReadActiveProviderCredentials(home);

        Assert.True(provider.RequestHeaders.ContainsKey("X-Good"));
        Assert.False(provider.RequestHeaders.ContainsKey("X-Bad"));
        Assert.False(provider.RequestHeaders.ContainsKey("Bad Name"));
    }

    [Fact]
    public void 环境变量未设置时整条头省略而不是发一个空头()
    {
        // 发空头会被 relay 读成"提供了但是空的",最典型的后果是把
        // 401 的原因从"没带凭据"变成"凭据无效",诊断方向直接跑偏。
        string home = NewCodexHome(
            """
            model_provider = "relay"
            model = "gpt-test"

            [model_providers.relay]
            base_url = "https://relay.example.invalid/v1"
            requires_openai_auth = true
            env_http_headers = { X-Missing = "NOT_SET_ANYWHERE", X-Blank = "SET_BUT_BLANK" }
            """,
            """{"auth_mode":"apikey","OPENAI_API_KEY":"sk-real-probe-key"}""");

        CodexProviderCredentials provider = CodexAuthProbe.ReadActiveProviderCredentials(
            home,
            name => name == "SET_BUT_BLANK" ? "   " : null);

        Assert.False(provider.RequestHeaders.ContainsKey("X-Missing"));
        Assert.False(provider.RequestHeaders.ContainsKey("X-Blank"));
    }

    [Fact]
    public void 能力字段缺省时取上游默认而不是留空()
    {
        // wire_api 缺省 -> responses;requires_openai_auth 缺省 -> false
        // (于是不把 auth.json 的 OpenAI 登录发给第三方 base_url)。
        string home = NewCodexHome(
            """
            model_provider = "relay"
            model = "gpt-test"

            [model_providers.relay]
            base_url = "https://relay.example.invalid/v1"
            """,
            """{"auth_mode":"apikey","OPENAI_API_KEY":"sk-real-probe-key"}""");

        CodexProviderCredentials provider = CodexAuthProbe.ReadActiveProviderCredentials(home);

        Assert.Equal("responses", provider.WireApi);
        Assert.False(provider.RequiresOpenAiAuth);
        Assert.True(string.IsNullOrWhiteSpace(provider.BearerToken));
    }

    [Fact]
    public async Task Provider查询参数和自定义请求头复现到Models与Responses()
    {
        string home = NewCodexHome(
            """
            model_provider = "relay"
            model = "gpt-test"

            [model_providers.relay]
            base_url = "https://relay.example.invalid/v1"
            wire_api = "responses"
            requires_openai_auth = true
            query_params = { api-version = "2026-01-01" }
            http_headers = { X-Static = "static", Authorization = "Bearer must-be-overridden" }
            env_http_headers = { X-Env = "RELAY_HEADER", X-Static = "RELAY_OVERRIDE" }
            """,
            """{"auth_mode":"apikey","OPENAI_API_KEY":"sk-real-probe-key"}""");
        var handler = new CapturingHandler(HttpStatusCode.OK, HttpStatusCode.OK);

        CodexAuthResult result = await CodexAuthProbe.ProbeAsync(
            home,
            handler,
            name => name switch
            {
                "RELAY_HEADER" => "from-environment",
                "RELAY_OVERRIDE" => "environment-wins",
                _ => null,
            });

        Assert.Equal(CodexAuthOutcome.Authorized, result.Outcome);
        Assert.Equal(
            new[]
            {
                "https://relay.example.invalid/v1/models?api-version=2026-01-01",
                "https://relay.example.invalid/v1/responses?api-version=2026-01-01",
            },
            handler.Uris);
        Assert.Equal("Bearer sk-real-probe-key", handler.Authorization);
        Assert.Equal("environment-wins", handler.Header("X-Static"));
        Assert.Equal("from-environment", handler.Header("X-Env"));
    }

    [Fact]
    public void Provider查询参数的键和值分别按Uri规则编码()
    {
        var query = new Dictionary<string, string>
        {
            ["tenant key&"] = "a=b + 100% 中文",
        };

        string url = CodexAuthProbe.BuildApiUrl(
            "https://relay.example.invalid/v1",
            "models",
            query);

        Assert.Equal(
            "https://relay.example.invalid/v1/models?tenant%20key%26=a%3Db%20%2B%20100%25%20%E4%B8%AD%E6%96%87",
            url);
    }

    [Fact]
    public async Task 无OpenAiAuth的Provider可用自定义Authorization且拒绝时不说匿名端点()
    {
        string home = NewCodexHome(
            """
            model_provider = "relay"
            model = "gpt-test"

            [model_providers.relay]
            base_url = "https://relay.example.invalid"
            requires_openai_auth = false
            http_headers = { Authorization = "Bearer relay-header-token" }
            """,
            authJson: null);
        var handler = new CapturingHandler(HttpStatusCode.Unauthorized);

        CodexAuthResult result = await CodexAuthProbe.ProbeAsync(home, handler);

        Assert.Equal(CodexAuthOutcome.Rejected, result.Outcome);
        Assert.True(result.UsedCredential);
        Assert.Contains("凭据被拒", result.Detail, StringComparison.Ordinal);
        Assert.Equal("Bearer relay-header-token", handler.Authorization);
    }

    [Fact]
    public void envKey优先于authJson且不回退到错误凭据()
    {
        string home = NewCodexHome(
            RealisticConfig.Replace(
                "requires_openai_auth = true",
                "requires_openai_auth = false\nenv_key = \"CORP_TOKEN\""),
            """{"auth_mode":"apikey","OPENAI_API_KEY":"sk-auth-file"}""");

        CodexProviderCredentials credentials = CodexAuthProbe.ReadActiveProviderCredentials(
            home,
            name => name == "CORP_TOKEN" ? "sk-from-env" : null);

        Assert.Equal("sk-from-env", credentials.BearerToken);
        Assert.Equal("env_key", credentials.CredentialSource);
    }

    [Fact]
    public void experimentalBearer优先于authJson()
    {
        string home = NewCodexHome(
            RealisticConfig.Replace(
                "requires_openai_auth = true",
                "requires_openai_auth = false\nexperimental_bearer_token = \"static-provider-token\""),
            """{"auth_mode":"apikey","OPENAI_API_KEY":"sk-auth-file"}""");

        CodexProviderCredentials credentials = CodexAuthProbe.ReadActiveProviderCredentials(home);

        Assert.Equal("static-provider-token", credentials.BearerToken);
        Assert.Equal("experimental_bearer_token", credentials.CredentialSource);
    }

    [Theory]
    [InlineData("apikey", "sk-api", null, "sk-api", "auth.json:apikey")]
    [InlineData("chatgpt", "sk-api", "chatgpt-access", "chatgpt-access", "auth.json:chatgpt")]
    [InlineData(null, "sk-api", "chatgpt-access", "sk-api", "auth.json:apikey")]
    [InlineData(null, null, "chatgpt-access", "chatgpt-access", "auth.json:chatgpt")]
    public void authMode按Codex上游规则选择凭据(
        string? authMode,
        string? apiKey,
        string? accessToken,
        string expectedToken,
        string expectedSource)
    {
        string authJson = System.Text.Json.JsonSerializer.Serialize(new
        {
            auth_mode = authMode,
            OPENAI_API_KEY = apiKey,
            tokens = accessToken is null ? null : new { access_token = accessToken, account_id = "acct-1" },
        });
        string home = NewCodexHome(RealisticConfig, authJson);

        CodexProviderCredentials credentials = CodexAuthProbe.ReadActiveProviderCredentials(home);

        Assert.Equal(expectedToken, credentials.BearerToken);
        Assert.Equal(expectedSource, credentials.CredentialSource);
        Assert.Equal(expectedSource == "auth.json:chatgpt" ? "acct-1" : null, credentials.AccountId);
    }

    [Fact]
    public async Task ChatGPT凭据携带账户头且不泄露到账户摘要()
    {
        string home = NewCodexHome(
            RealisticConfig,
            """{"auth_mode":"chatgpt","tokens":{"access_token":"chatgpt-access","account_id":"acct-123"}}""");
        var handler = new CapturingHandler(HttpStatusCode.OK, HttpStatusCode.OK);

        CodexAuthResult result = await CodexAuthProbe.ProbeAsync(home, handler);

        Assert.Equal(CodexAuthOutcome.Authorized, result.Outcome);
        Assert.Equal("acct-123", handler.AccountId);
        Assert.DoesNotContain("chatgpt-access", result.Detail, StringComparison.Ordinal);
        Assert.DoesNotContain("acct-123", result.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 未启用OpenAi认证的自定义Provider不读取AuthJson也不发送授权头()
    {
        string home = NewCodexHome(
            RealisticConfig.Replace("requires_openai_auth = true", "requires_openai_auth = false"),
            """{"auth_mode":"chatgpt","tokens":{"access_token":"must-not-leak","account_id":"acct-123"}}""");
        var handler = new CapturingHandler(HttpStatusCode.OK, HttpStatusCode.OK);

        CodexProviderCredentials credentials = CodexAuthProbe.ReadActiveProviderCredentials(home);
        CodexAuthResult result = await CodexAuthProbe.ProbeAsync(home, handler);

        Assert.Null(credentials.BearerToken);
        Assert.Equal("none", credentials.CredentialSource);
        Assert.Equal(CodexAuthOutcome.Authorized, result.Outcome);
        Assert.False(result.UsedCredential);
        Assert.Null(handler.Authorization);
        Assert.Null(handler.AccountId);
        Assert.DoesNotContain("must-not-leak", string.Join("\n", handler.Bodies), StringComparison.Ordinal);
    }

    [Fact]
    public async Task 无认证Provider遇到401说明端点要求凭据而不是凭据被拒()
    {
        string home = NewCodexHome(
            RealisticConfig.Replace("requires_openai_auth = true", "requires_openai_auth = false"),
            """{"OPENAI_API_KEY":"must-not-leak"}""");
        var handler = new CapturingHandler(HttpStatusCode.Unauthorized);

        CodexAuthResult result = await CodexAuthProbe.ProbeAsync(home, handler);

        Assert.Equal(CodexAuthOutcome.Rejected, result.Outcome);
        Assert.False(result.UsedCredential);
        Assert.Contains("要求凭据", result.Detail, StringComparison.Ordinal);
        Assert.Null(handler.Authorization);
    }

    [Theory]
    [InlineData("auth", "provider 使用命令式 auth")]
    [InlineData("aws", "AWS SigV4")]
    public void 需要上游状态机的provider认证不自行执行(string kind, string expectedProblem)
    {
        string providerBlock = kind == "auth"
            ? "[model_providers.OpenAI.auth]\ncommand = \"definitely-not-a-real-command\""
            : "[model_providers.OpenAI.aws]\nprofile = \"default\"";
        string home = NewCodexHome(
            RealisticConfig + "\n" + providerBlock + "\n",
            """{"auth_mode":"apikey","OPENAI_API_KEY":"sk-auth-file"}""");

        CodexProviderCredentials credentials = CodexAuthProbe.ReadActiveProviderCredentials(home);

        Assert.Null(credentials.BearerToken);
        Assert.Contains(expectedProblem, credentials.Problem);
    }

    [Fact]
    public void profile覆盖活动provider与模型()
    {
        string home = NewCodexHome(
            """
            profile = "work"
            model_provider = "A"
            model = "root-model"

            [profiles.work]
            model_provider = "B"
            model = "profile-model"

            [model_providers.A]
            base_url = "https://a.example"

            [model_providers.B]
            base_url = "https://b.example"
            wire_api = "responses"
            """,
            """{"OPENAI_API_KEY":"k"}""");

        CodexProviderCredentials credentials = CodexAuthProbe.ReadActiveProviderCredentials(home);

        Assert.Equal("https://b.example", credentials.BaseUrl);
        Assert.Equal("profile-model", credentials.Model);
    }

    [Theory]
    [InlineData("http://relay.example")]
    [InlineData("https://user:pass@relay.example")]
    [InlineData("https://relay.example?token=x")]
    [InlineData("https://relay.example#fragment")]
    [InlineData("file:///C:/temp")]
    public async Task 不安全baseUrl失败关闭且不发请求(string baseUrl)
    {
        string home = NewCodexHome(
            RealisticConfig.Replace("https://relay.example.invalid", baseUrl),
            """{"OPENAI_API_KEY":"k"}""");
        var handler = new CapturingHandler(HttpStatusCode.OK);

        CodexAuthResult result = await CodexAuthProbe.ProbeAsync(home, handler);

        Assert.Equal(CodexAuthOutcome.NotConfigured, result.Outcome);
        Assert.Empty(handler.Uris);
    }

    [Fact]
    public async Task 非Responses协议只验models不猜请求形状()
    {
        string home = NewCodexHome(
            RealisticConfig.Replace("wire_api = \"responses\"", "wire_api = \"legacy-chat\""),
            """{"OPENAI_API_KEY":"k"}""");
        var handler = new CapturingHandler(HttpStatusCode.OK);

        CodexAuthResult result = await CodexAuthProbe.ProbeAsync(home, handler);

        Assert.Equal(CodexAuthOutcome.InferenceUnverified, result.Outcome);
        Assert.Single(handler.Uris);
        Assert.Contains("wire_api", result.Detail);
    }

    [Fact]
    public async Task 能列模型但不允许推理不得判成可用()
    {
        // 审计 A6 的原始注入条件:{base_url}/models 返 200,推理路由返 403。
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
    [InlineData(HttpStatusCode.PaymentRequired, CodexAuthOutcome.Limited)]
    [InlineData(HttpStatusCode.TooManyRequests, CodexAuthOutcome.Limited)]
    [InlineData(HttpStatusCode.InternalServerError, CodexAuthOutcome.ServerError)]
    // 端点形状不支持 ≠ 没有推理权限。sub2api 各家路由不一,
    // 把"这家不认识 responses"标成红,是把好配置误判成坏的。
    [InlineData(HttpStatusCode.NotFound, CodexAuthOutcome.InferenceUnverified)]
    [InlineData(HttpStatusCode.BadRequest, CodexAuthOutcome.InferenceUnverified)]
    public void 推理状态码映射(HttpStatusCode status, CodexAuthOutcome expected)
    {
        Assert.Equal(expected, CodexAuthProbe.ClassifyInference(status).Outcome);
    }

    [Theory]
    [InlineData("""{"id":"resp_1","object":"response","status":"completed","output":[]}""")]
    [InlineData("""{"id":"resp_1","status":"incomplete","incomplete_details":{"reason":"max_output_tokens"},"output":[]}""")]
    [InlineData("""{"id":"resp_1","object":"response","output":[]}""")]
    public void Responses有效终态才能作为推理成功证据(string body)
    {
        CodexAuthResult result = CodexAuthProbe.ClassifyInferenceResponse(HttpStatusCode.OK, body);

        Assert.Equal(CodexAuthOutcome.Authorized, result.Outcome);
    }

    [Theory]
    [InlineData("")]
    [InlineData("<html>ok</html>")]
    [InlineData("{ broken")]
    [InlineData("{}")]
    [InlineData("""{"status":"in_progress","output":[]}""")]
    public void 空响应非Json和非终态不能因Http200点绿(string body)
    {
        CodexAuthResult result = CodexAuthProbe.ClassifyInferenceResponse(HttpStatusCode.OK, body);

        Assert.NotEqual(CodexAuthOutcome.Authorized, result.Outcome);
    }

    [Theory]
    // 非终态:请求被收下了,但还没跑完 —— 证明不了"这把 key 能干活"。
    [InlineData("""{"id":"resp_1","object":"response","status":"queued","output":[]}""")]
    [InlineData("""{"id":"resp_1","object":"response","status":"in_progress","output":[]}""")]
    // 未知/非字符串 status:读不懂就不许点绿,更不许因为"看着像 envelope"而走兼容分支。
    [InlineData("""{"id":"resp_1","object":"response","status":"weird_new_state","output":[]}""")]
    [InlineData("""{"id":"resp_1","object":"response","status":null,"output":[]}""")]
    [InlineData("""{"id":"resp_1","object":"response","status":7,"output":[]}""")]
    // completed 但没有 output:没有产出就没有"跑得动"的证据。
    [InlineData("""{"id":"resp_1","object":"response","status":"completed"}""")]
    // incomplete 的原因不是长度截断,或截断了却没有 output —— 都不算成功。
    [InlineData("""{"id":"resp_1","status":"incomplete","incomplete_details":{"reason":"content_filter"},"output":[]}""")]
    [InlineData("""{"id":"resp_1","status":"incomplete","incomplete_details":{"reason":"max_output_tokens"}}""")]
    // 既没有 object=response 也没有 id 的裸对象,不构成 envelope。
    [InlineData("""{"object":"chat.completion","output":[]}""")]
    [InlineData("""{"output":[]}""")]
    public void 非终态与读不懂的终态一律不点绿(string body)
    {
        CodexAuthResult result = CodexAuthProbe.ClassifyInferenceResponse(HttpStatusCode.OK, body);

        Assert.Equal(CodexAuthOutcome.InferenceUnverified, result.Outcome);
    }

    [Fact]
    public void HTTP202只证明请求被收下不证明推理完成()
    {
        // 202 + 一份看着完整的 envelope 是最容易骗过判定的组合:
        // 缺 status 的兼容分支必须锁死在 200,否则"已接受"会被读成"已完成"。
        const string envelope = """{"id":"resp_1","object":"response","output":[]}""";

        Assert.Equal(
            CodexAuthOutcome.Authorized,
            CodexAuthProbe.ClassifyInferenceResponse(HttpStatusCode.OK, envelope).Outcome);
        Assert.Equal(
            CodexAuthOutcome.InferenceUnverified,
            CodexAuthProbe.ClassifyInferenceResponse(HttpStatusCode.Accepted, envelope).Outcome);
    }

    [Fact]
    public void error为null不算错误也不因此点绿()
    {
        // null 是"没有错误",不是"有个错误对象"。误判成错误会把好 provider 标红。
        CodexAuthResult completed = CodexAuthProbe.ClassifyInferenceResponse(
            HttpStatusCode.OK,
            """{"id":"resp_1","object":"response","status":"completed","error":null,"output":[]}""");
        CodexAuthResult queued = CodexAuthProbe.ClassifyInferenceResponse(
            HttpStatusCode.OK,
            """{"id":"resp_1","object":"response","status":"queued","error":null,"output":[]}""");

        Assert.Equal(CodexAuthOutcome.Authorized, completed.Outcome);
        Assert.Equal(CodexAuthOutcome.InferenceUnverified, queued.Outcome);
    }

    [Fact]
    public void output必须是数组而不是随便什么真值()
    {
        foreach (string body in new[]
                 {
                     """{"id":"resp_1","object":"response","status":"completed","output":"done"}""",
                     """{"id":"resp_1","object":"response","status":"completed","output":{}}""",
                     """{"id":"resp_1","object":"response","output":"done"}""",
                 })
        {
            Assert.Equal(
                CodexAuthOutcome.InferenceUnverified,
                CodexAuthProbe.ClassifyInferenceResponse(HttpStatusCode.OK, body).Outcome);
        }
    }

    [Theory]
    [InlineData("""{"status":"failed","error":{"message":"backend failed"},"output":[]}""")]
    [InlineData("""{"status":"cancelled","output":[]}""")]
    [InlineData("""{"error":{"type":"rate_limit_error"}}""")]
    public void Http200内的语义失败不能点绿(string body)
    {
        CodexAuthResult result = CodexAuthProbe.ClassifyInferenceResponse(HttpStatusCode.OK, body);

        Assert.Equal(CodexAuthOutcome.NoInference, result.Outcome);
    }

    [Fact]
    public async Task 深探针实际读取Responses响应体而不是只看Http状态码()
    {
        string home = NewCodexHome(RealisticConfig, """{"OPENAI_API_KEY":"sk-test"}""");
        var handler = new SequenceHandler(
            _ => new HttpResponseMessage(HttpStatusCode.OK),
            _ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"status":"failed","error":{"message":"hidden"},"output":[]}"""),
            });

        CodexAuthResult result = await CodexAuthProbe.ProbeAsync(home, handler);

        Assert.Equal(CodexAuthOutcome.NoInference, result.Outcome);
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

        /// <param name="first">{base_url}/models 的状态码。</param>
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

        public string? AccountId { get; private set; }

        private Dictionary<string, string> LastHeaders { get; } = new(StringComparer.OrdinalIgnoreCase);

        public string? Header(string name) => LastHeaders.TryGetValue(name, out string? value) ? value : null;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            // **必须记 AbsoluteUri 而不是 ToString()。** Uri.ToString() 会把百分号编码
            // 反解成"给人看"的形式,拿它断言等于放过所有转义缺陷;上线走的是 AbsoluteUri。
            Uris.Add(request.RequestUri?.AbsoluteUri ?? string.Empty);
            Methods.Add(request.Method.Method);
            Bodies.Add(request.Content is null
                ? string.Empty
                : request.Content.ReadAsStringAsync(ct).GetAwaiter().GetResult());
            Authorization = request.Headers.TryGetValues("Authorization", out IEnumerable<string>? a)
                ? string.Join(",", a) : null;
            UserAgent = request.Headers.TryGetValues("User-Agent", out IEnumerable<string>? u)
                ? string.Join(" ", u) : null;
            AccountId = request.Headers.TryGetValues("ChatGPT-Account-ID", out IEnumerable<string>? account)
                ? string.Join(",", account) : null;
            LastHeaders.Clear();
            foreach ((string name, IEnumerable<string> values) in request.Headers)
            {
                LastHeaders[name] = string.Join(",", values);
            }

            HttpStatusCode status = _count++ == 0 ? _first : (_second ?? _first);
            var response = new HttpResponseMessage(status);
            if (request.Method == HttpMethod.Post && response.IsSuccessStatusCode)
            {
                response.Content = new StringContent(
                    """{"id":"resp_test","object":"response","status":"completed","output":[]}""");
            }

            return Task.FromResult(response);
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
