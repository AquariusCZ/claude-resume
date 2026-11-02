using System.Net;
using System.Text.Json;
using AiResume.Core;
using AiResume.Worker.Probes;
using AiResume.Worker.Quota;
using AiResume.Worker.Resume;
using Xunit;

namespace AiResume.Tests;

public sealed class ProviderBoundResumeTests
{
    // 2026-09-07 官方 GET /v1/models?limit=1000 HTTP 200 的白名单录制字段；无凭据。
    private const string AnthropicModels = """
        {"data":[{"id":"claude-fable-5-1","display_name":"Claude Fable 5.1","type":"model"},
          {"id":"claude-opus-5","display_name":"Claude Opus 5","type":"model"},
          {"id":"claude-fable-5","display_name":"Claude Fable 5","type":"model"}],"has_more":false}
        """;

    [Theory]
    [InlineData("Fable 5.1", "claude-fable-5-1", true)]
    [InlineData("Fable 5", "claude-fable-5-1", false)]
    [InlineData("Fable", "claude-fable-5-1", true)]
    [InlineData("Fable 5..1", "claude-fable-5-1", false)]
    public void 带版本的额度范围必须与实际版本对应(string scope, string model, bool expected)
        => Assert.Equal(expected, ClaudeModelFamilies.ScopeMatchesModel(scope, model));

    [Fact]
    public void 官方录制目录保留小版本与旧版本的不同调用ID()
    {
        ProviderModelCatalog result = ProviderModels.Parse(AnthropicModels);
        Assert.True(result.Verified);
        Assert.Equal("claude-fable-5-1", result.Models[0].Id);
        Assert.Equal("Claude Fable 5.1", result.Models[0].Name);
        Assert.Contains(result.Models, m => m.Id == "claude-fable-5");
    }

    [Theory]
    [InlineData("{\"data\":{},\"has_more\":false}")]
    [InlineData("{\"data\":[{\"id\":\"model & command\"}]}")]
    [InlineData("{\"data\":[],\"has_more\":true}")]
    public void 损坏或不完整目录不得授权布防(string body) => Assert.False(ProviderModels.Parse(body).Verified);

    [Fact]
    public async Task 布防后目录升级不改写目标且新布防采用新ID()
    {
        string root = TestTemp.NewDir("provider-binding");
        string model = "relay-model-1";
        string token = "test-only-token";
        ProviderRegistry registry = Registry(root, () => token);
        var service = new ProviderCatalogService(registry, observe: (p, _) => Task.FromResult(Observation(p, model, 10)));
        ResumeTarget bound = await service.BindAsync("codex-active", model, default);
        model = "relay-model-2";
        Assert.Equal("relay-model-1", bound.ModelId);
        Assert.False((await service.CheckAsync(bound, default)).Ready);
        ResumeTarget next = await service.BindAsync("codex-active", model, default);
        Assert.Equal("relay-model-2", next.ModelId);
        Assert.True((await service.CheckAsync(next, default)).Ready);
        token = "different-test-token";
        Assert.Equal("provider-changed", (await service.CheckAsync(next, default)).Reason);
    }

    [Fact]
    public async Task 余额从零到正才建立供应商恢复证据()
    {
        string root = TestTemp.NewDir("provider-balance");
        decimal? balance = 0;
        var service = new ProviderCatalogService(Registry(root, () => "test-key"),
            observe: (p, _) => Task.FromResult(Observation(p, "relay-model", balance)));
        ResumeTarget target = await service.BindAsync("codex-active", "relay-model", default);
        Assert.True((await service.CheckAsync(target, default)).IsLimited);
        balance = null;
        Assert.False((await service.CheckAsync(target, default)).Ready);
        balance = 1;
        Assert.True((await service.CheckAsync(target, default)).Ready);
    }

    [Fact]
    public async Task DeepSeek余额行只展示不能作为续跑目标()
    {
        var service = new ProviderCatalogService(Registry(TestTemp.NewDir("provider-deepseek"), () => "test-key"),
            observe: (p, _) => Task.FromResult(Observation(p, "deepseek-v4-flash", 5)));
        ProviderConnection deepseek = service.Registry.Find("deepseek")!;
        Assert.Equal("opencode", deepseek.Agent);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => service.BindAsync("deepseek", "deepseek-v4-flash", default));
        Assert.Contains("只显示余额", error.Message, StringComparison.Ordinal);
        ProviderObservation row = (await service.ReadAsync(default)).Single(p => p.Id == "deepseek");
        Assert.False(JsonDocument.Parse(JsonSerializer.Serialize(row.ToPayload())).RootElement.GetProperty("resumable").GetBoolean());
    }

    [Fact]
    public async Task 单供应商异常不会拖垮其他目录且不泄露异常正文()
    {
        var service = new ProviderCatalogService(Registry(TestTemp.NewDir("provider-isolation"), () => "test-key"),
            observe: (p, _) => p.Id == "codex-active"
                ? throw new IOException("secret-must-not-escape")
                : Task.FromResult(Observation(p, "model", 1)));
        IReadOnlyList<ProviderObservation> rows = await service.ReadAsync(default);
        Assert.True(rows.Single(p => p.Id == "deepseek").Catalog.Verified);
        Assert.False(rows.Single(p => p.Id == "codex-active").Catalog.Verified);
        Assert.DoesNotContain("secret-must-not-escape", JsonSerializer.Serialize(rows.Select(p => p.ToPayload())), StringComparison.Ordinal);
    }

    [Fact]
    public async Task 旧配置没有绑定目标不能复用历史模型授权()
    {
        var service = new ProviderCatalogService(Registry(TestTemp.NewDir("provider-legacy"), () => "test-key"),
            observe: (_, _) => throw new InvalidOperationException("不得请求"));
        var config = ProductConfig.CreateDefault();
        config.ResumeModel = "fable";
        Assert.Equal("target-unbound", (await new ProviderResumeProbe(service).ProbeAsync(config, "", default)).Reason);
    }

    [Fact]
    public void Codex仅完整匹配会话的结束事件证明成功()
    {
        const string id = "bd42e9fd-0c4b-4b5c-8058-3c31ac905338";
        var evidence = new CodexResumeEvidence();
        evidence.Inspect("{\"type\":\"thread.started\",\"thread_id\":\"" + id + "\"}", id);
        evidence.Inspect("{\"type\":\"turn.started\"}", id);
        evidence.Inspect("{\"type\":\"item.started\",\"item\":{\"type\":\"command_execution\"}}", id);
        Assert.True(evidence.SideEffects);
        Assert.False(evidence.Success);
        evidence.Inspect("{\"type\":\"turn.completed\",\"usage\":{\"input_tokens\":1,\"cached_input_tokens\":0,\"output_tokens\":1}}", id);
        Assert.True(evidence.Success);
        evidence.Inspect("{\"type\":\"error\",\"message\":\"rate limit\"}", id);
        Assert.False(evidence.Success);
    }

    [Theory]
    [InlineData("not-json")]
    [InlineData("{\"type\":\"item.completed\",\"item\":{\"type\":\"error\",\"message\":\"rerouted\"}}")]
    [InlineData("{\"type\":\"new-event\"}")]
    public void Codex未知或重路由事件必须阻断完成与重放(string line)
    {
        var evidence = new CodexResumeEvidence();
        evidence.Inspect(line, "test-session");
        evidence.Inspect("{\"type\":\"turn.completed\"}", "test-session");
        Assert.True(evidence.Failed);
        Assert.False(evidence.Success);
    }

    [Fact]
    public void 精确会话选择排除Claude子代理且不会随最新会话漂移()
    {
        string root = TestTemp.NewDir("resume-sessions");
        string project = TestTemp.NewDir("resume-project");
        string parent = "bd42e9fd-0c4b-4b5c-8058-3c31ac905338";
        File.WriteAllText(Path.Combine(root, parent + ".jsonl"), JsonSerializer.Serialize(new { type = "user", cwd = project }));
        string subs = Path.Combine(root, "subagents");
        Directory.CreateDirectory(subs);
        File.WriteAllText(Path.Combine(subs, Guid.NewGuid() + ".jsonl"), JsonSerializer.Serialize(new { type = "user", cwd = project }));
        var catalog = new ResumeSessionCatalog(root, TestTemp.NewPath("no-codex"));
        ProjectRef selected = Assert.Single(catalog.Bind("claudecode", new[] { project }));
        Assert.Equal(parent, selected.SessionId);
        File.WriteAllText(Path.Combine(root, Guid.NewGuid() + ".jsonl"), JsonSerializer.Serialize(new { type = "user", cwd = project }));
        Assert.True(catalog.Matches("claudecode", selected));
        selected.Path = TestTemp.NewDir("other-project");
        Assert.False(catalog.Matches("claudecode", selected));
    }

    [Fact]
    public async Task 显式清空Bearer后使用APIKey且Claude请求仅有一个UA()
    {
        string root = TestTemp.NewDir("provider-http");
        string claude = Path.Combine(root, ".claude");
        Directory.CreateDirectory(claude);
        File.WriteAllText(Path.Combine(claude, "settings.json"),
            """{"env":{"ANTHROPIC_AUTH_TOKEN":"","ANTHROPIC_API_KEY":"test-only-api-key","ANTHROPIC_BASE_URL":"https://test.invalid"}}""");
        ProviderRegistry registry = Registry(root, () => "test-deepseek");
        var handler = new RecordingHandler(request =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("models", StringComparison.Ordinal))
            {
                Assert.Equal("test-only-api-key", Assert.Single(request.Headers.GetValues("x-api-key")));
                string ua = Assert.Single(request.Headers.GetValues("User-Agent"));
                Assert.StartsWith("claude-code/", ua, StringComparison.Ordinal);
                Assert.DoesNotContain("Mozilla", ua, StringComparison.Ordinal);
                return new(HttpStatusCode.OK) { Content = new StringContent(AnthropicModels) };
            }
            return new(HttpStatusCode.NotFound) { Content = new StringContent("{}") };
        });
        var service = new ProviderCatalogService(registry, handler);
        ProviderObservation result = await service.ObserveAsync(registry.Find("claude-api")!, default);
        Assert.True(result.Catalog.Verified);
        Assert.True(handler.ModelRequested);
    }

    private sealed class RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> response) : HttpMessageHandler
    {
        public bool ModelRequested { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            ModelRequested |= request.RequestUri!.AbsolutePath.EndsWith("models", StringComparison.Ordinal);
            return Task.FromResult(response(request));
        }
    }

    [Fact]
    public async Task 元数据刷新失败不得降级为AI推理()
    {
        int inferenceCalls = 0;
        var quota = new QuotaService(probe: _ =>
        {
            inferenceCalls++;
            throw new InvalidOperationException("不得执行推理");
        }, oauthProbe: _ => Task.FromResult(new OAuthUsageResult(false, null, "network_error")), allowCliFallback: false);
        UsageSnapshot result = await quota.GetAsync(true, default);
        Assert.Equal(0, inferenceCalls);
        Assert.False(result.HasData);
        Assert.NotNull(result.UnavailableReason);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(null, null)]
    public void Claude原生登记仅在进程确认结束后允许续跑(bool? processAlive, bool? expectedIdle)
    {
        string root = TestTemp.NewDir("session-ownership");
        string registry = Path.Combine(root, "sessions");
        Directory.CreateDirectory(registry);
        string session = Guid.NewGuid().ToString();
        File.WriteAllText(Path.Combine(registry, "123.json"), JsonSerializer.Serialize(new { pid = 123, sessionId = session }));
        var sessions = new ResumeSessionCatalog(Path.Combine(root, "projects"), Path.Combine(root, "codex"), _ => processAlive);
        Assert.Equal(expectedIdle, sessions.IsClaudeSessionIdle(session));
        Assert.True(sessions.IsClaudeSessionIdle(Guid.NewGuid().ToString()));
    }

    private static ProviderRegistry Registry(string root, Func<string> key) => new(
        environment: name => name == "DEEPSEEK_API_KEY" ? key() : null, userProfile: root,
        codex: () => new("https://test.invalid/v1", key(), "model", "responses", new Dictionary<string, string>(),
            new Dictionary<string, string>(), "test", null, null, "test", false, false));

    private static ProviderObservation Observation(ProviderConnection p, string model, decimal? balance) =>
        new(p.Id, p.Name, p.Agent, p.Kind, p.Identity,
            new(new[] { new ProviderModel(model, model) }, true, "隔离测试观测", DateTimeOffset.UtcNow),
            balance, "CNY", balance is not null, balance <= 0, null);
}
