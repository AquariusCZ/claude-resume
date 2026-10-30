using System.Text.Json;
using AiResume.Core;
using AiResume.Worker.Migration;
using AiResume.Worker.Probes;
using AiResume.Worker.Quota;
using AiResume.Worker.Resume;
using AiResume.Wrapper;
using Xunit;

namespace AiResume.Tests;

/// <summary>
/// Codex 官方供应商(B1):面板行、续跑放行规则、执行计划与 cc-connect 配置生成。
/// 真实响应形状来自 2026-10-12 本机 wham/usage 录制(账号/邮箱字段已脱敏)。
/// </summary>
public sealed class CodexOfficialProviderTests
{
    /// <summary>prolite 计划的真实响应,仅把个人标识替换为占位。</summary>
    private const string RecordedUsage = """
        {"user_id":"user-x","account_id":"acct-x","email":"x@example.invalid","plan_type":"prolite",
         "rate_limit":{"allowed":true,"limit_reached":false,
           "primary_window":{"used_percent":0,"limit_window_seconds":604800,"reset_after_seconds":604800,"reset_at":1792411217},
           "secondary_window":null},
         "code_review_rate_limit":null,"additional_rate_limits":null,
         "model_usage":{"gpt-6-astra":{"available":true,"available_at":null,"credits_would_enable":false}},
         "credits":{"has_credits":false,"unlimited":false,"overage_limit_reached":false,"balance":"0","approx_local_messages":[0,0],"approx_cloud_messages":[0,0]},
         "spend_control":{"reached":false,"individual_limit":null},
         "rate_limit_reached_type":null,"promo":null,
         "rate_limit_reset_credits":{"available_count":1,"applicable_available_count":0}}
        """;

    [Fact]
    public void 真实wham响应解析出账号窗口与模型可用性()
    {
        UsageSnapshot snapshot = ChatGptUsage.Parse(RecordedUsage);

        Assert.True(snapshot.HasData);
        Assert.Null(snapshot.UnavailableReason);
        UsageBucket account = Assert.Single(snapshot.Buckets, b => b.Name == ChatGptUsage.AccountBucket);
        Assert.True(account.Allowed);
        Assert.False(account.LimitReached);
        UsageWindow primary = Assert.Single(account.Windows);
        Assert.Equal("primary · 7D", primary.Name);
        Assert.Equal(0, primary.UsedPercent);
        Assert.Equal(1792411217, primary.ResetAtUnix);
        Assert.Equal(604800, primary.WindowSeconds);
        Assert.Equal("allowed", primary.Status);
        UsageWindow model = ChatGptUsage.FindModelWindow(snapshot, "gpt-6-astra")!;
        Assert.Equal("allowed", model.Status);
        Assert.Null(ChatGptUsage.FindModelWindow(snapshot, "gpt-5.6-sol"));
    }

    [Fact]
    public void 放行规则_账号未限额且目标模型可用才ready()
    {
        UsageSnapshot ok = ChatGptUsage.Parse(RecordedUsage);
        Assert.Equal("ok", ProviderCatalogService.EvaluateChatGpt(ok, "gpt-6-astra").Reason);
        // 未列出的模型不视为不可用:列表只枚举有过记录的模型。
        Assert.True(ProviderCatalogService.EvaluateChatGpt(ok, "gpt-5.6-sol").Ready);

        UsageSnapshot modelBlocked = ChatGptUsage.Parse(RecordedUsage.Replace("\"available\":true", "\"available\":false"));
        ClaudeProbeResult blocked = ProviderCatalogService.EvaluateChatGpt(modelBlocked, "gpt-6-astra");
        Assert.False(blocked.Ready);
        Assert.Equal("limited", blocked.Reason);
        Assert.True(ProviderCatalogService.EvaluateChatGpt(modelBlocked, "gpt-5.6-sol").Ready);

        UsageSnapshot accountLimited = ChatGptUsage.Parse(RecordedUsage
            .Replace("\"allowed\":true,\"limit_reached\":false", "\"allowed\":false,\"limit_reached\":true")
            .Replace("\"used_percent\":0", "\"used_percent\":100"));
        Assert.Equal("limited", ProviderCatalogService.EvaluateChatGpt(accountLimited, "gpt-5.6-sol").Reason);

        UsageSnapshot spend = ChatGptUsage.Parse(RecordedUsage.Replace("\"reached\":false", "\"reached\":true"));
        Assert.Equal("limited", ProviderCatalogService.EvaluateChatGpt(spend, "gpt-5.6-sol").Reason);

        Assert.Equal("unknown", ProviderCatalogService.EvaluateChatGpt(ChatGptUsage.Parse("{}"), "gpt-5.6-sol").Reason);
        Assert.Equal("unknown", ProviderCatalogService.EvaluateChatGpt(ChatGptUsage.Parse("not json"), "gpt-5.6-sol").Reason);
        UsageSnapshot noPercent = ChatGptUsage.Parse(RecordedUsage.Replace("\"used_percent\":0,", string.Empty));
        Assert.Equal("unknown", ProviderCatalogService.EvaluateChatGpt(noPercent, "gpt-5.6-sol").Reason);
    }

    [Fact]
    public void 注册表官方行跟随authJson而非configToml的桥接provider()
    {
        string root = TestTemp.NewDir("codex-official-registry");
        var bridge = new CodexProviderCredentials("http://127.0.0.1:17841/v1", "chatgpt-token", "gpt-6-astra", "responses",
            new Dictionary<string, string>(), new Dictionary<string, string>(), "auth.json:chatgpt", null, "acct", "codex_web_gpt", false, true);
        var official = new CodexProviderCredentials("https://chatgpt.com/backend-api/codex", "chatgpt-token", "gpt-6-astra", "responses",
            new Dictionary<string, string>(), new Dictionary<string, string>(), "auth.json:chatgpt", null, "acct", "openai", true, true);
        var registry = new ProviderRegistry(_ => null, root, codex: () => bridge, codexOfficial: () => official);

        IReadOnlyList<ProviderConnection> rows = registry.Read();

        ProviderConnection row = Assert.Single(rows, r => r.Agent == "codex");
        Assert.Equal("codex-official", row.Id);
        Assert.Equal("chatgpt", row.Kind);
        Assert.Equal("Codex · ChatGPT 官方", row.Name);
        Assert.DoesNotContain(rows, r => r.Id == "codex-active");
    }

    [Fact]
    public void 自带凭据的远端第三方provider仍单独成行而本机桥接不成行()
    {
        string root = TestTemp.NewDir("codex-thirdparty-registry");
        var relay = new CodexProviderCredentials("https://relay.example.invalid/v1", "relay-key", "m", "responses",
            new Dictionary<string, string>(), new Dictionary<string, string>(), "env_key", null, null, "relay", false, false);
        var unauthenticated = new CodexProviderCredentials("https://chatgpt.com/backend-api/codex", null, null, "responses",
            new Dictionary<string, string>(), new Dictionary<string, string>(), "auth.json", "Codex 未登录", null, "openai", true, true);
        var registry = new ProviderRegistry(_ => null, root, codex: () => relay, codexOfficial: () => unauthenticated);

        IReadOnlyList<ProviderConnection> rows = registry.Read();

        Assert.Contains(rows, r => r.Id == "codex-active" && r.Name == "Codex · relay");
        ProviderConnection officialRow = Assert.Single(rows, r => r.Id == "codex-official");
        Assert.Equal("Codex · 官方(未登录)", officialRow.Name);
    }

    /// <summary>2026-10-12 本机只读录制的 <c>/backend-api/codex/models?client_version=0.148.0</c>(HTTP 200,6 个模型),
    /// 每个模型只保留与解析相关及少量真实字段,值未改动;slug 不是秘密。</summary>
    private const string RecordedModels = """
        {"models":[{"slug":"gpt-reserve","display_name":"GPT-Reserve","visibility":"hide","priority":4,"supported_in_api":true,"context_window":272000,"minimal_client_version":"0.144.0","model_specialty":null},{"slug":"gpt-5.6-sol","display_name":"GPT-5.6-Sol","visibility":"list","priority":5,"supported_in_api":true,"context_window":272000,"minimal_client_version":"0.144.0","model_specialty":null},{"slug":"gpt-5.6-terra","display_name":"GPT-5.6-Terra","visibility":"list","priority":8,"supported_in_api":true,"context_window":272000,"minimal_client_version":"0.144.0","model_specialty":null},{"slug":"gpt-5.6-luna","display_name":"GPT-5.6-Luna","visibility":"list","priority":9,"supported_in_api":true,"context_window":272000,"minimal_client_version":"0.144.0","model_specialty":null},{"slug":"gpt-5.5","display_name":"GPT-5.5","visibility":"hide","priority":13,"supported_in_api":true,"context_window":272000,"minimal_client_version":"0.124.0","model_specialty":null},{"slug":"codex-auto-review","display_name":"Codex Auto Review","visibility":"hide","priority":43,"supported_in_api":true,"context_window":272000,"minimal_client_version":"0.98.0","model_specialty":null}]}
        """;

    [Fact]
    public void 真实models目录只收visibility为list的slug()
    {
        ProviderModelCatalog catalog = ProviderCatalogService.ParseChatGptModels(RecordedModels);

        Assert.True(catalog.Verified, catalog.Problem);
        Assert.Equal(new[] { "gpt-5.6-sol", "gpt-5.6-terra", "gpt-5.6-luna" }, catalog.Models.Select(m => m.Id).ToArray());
        // hide 的模型(gpt-reserve / gpt-5.5 / codex-auto-review)不进选择器。
        Assert.DoesNotContain(catalog.Models, m => m.Id == "gpt-reserve" || m.Id == "codex-auto-review");
        Assert.False(ProviderCatalogService.ParseChatGptModels("{\"foo\":1}").Verified);
    }

    [Fact]
    public void 小数百分比_次级窗口_附加限额与reached_type都按保守语义解析()
    {
        string body = RecordedUsage
            .Replace("\"used_percent\":0,", "\"used_percent\":12.5,")
            .Replace("\"secondary_window\":null", "\"secondary_window\":{\"used_percent\":99.6,\"limit_window_seconds\":18000,\"reset_after_seconds\":100,\"reset_at\":1792411217}")
            .Replace("\"additional_rate_limits\":null", "\"additional_rate_limits\":[{\"limit_name\":\"code_review\",\"rate_limit\":{\"allowed\":false,\"limit_reached\":true,\"primary_window\":{\"used_percent\":100,\"limit_window_seconds\":604800,\"reset_after_seconds\":1,\"reset_at\":1792411217},\"secondary_window\":null}}]")
            .Replace("\"rate_limit_reached_type\":null", "\"rate_limit_reached_type\":\"primary_window\"");

        UsageSnapshot snapshot = ChatGptUsage.Parse(body);

        UsageBucket account = Assert.Single(snapshot.Buckets, b => b.Name == ChatGptUsage.AccountBucket);
        // 上游 used_percent 是 f64:12.5 四舍五入成 13,而不是「未报告」。
        Assert.Equal(13, account.Windows.Single(w => w.Name.StartsWith("primary", StringComparison.Ordinal)).UsedPercent);
        UsageWindow secondary = account.Windows.Single(w => w.Name.StartsWith("secondary", StringComparison.Ordinal));
        Assert.Equal(100, secondary.UsedPercent);
        Assert.Equal("blocked", secondary.Status);
        Assert.True(Assert.Single(snapshot.Buckets, b => b.Name == "code_review").LimitReached);
        Assert.Contains(snapshot.Buckets, b => b.UnattributedLimitReached);
        Assert.Equal("limited", ProviderCatalogService.EvaluateChatGpt(snapshot, "gpt-6-astra").Reason);

        // 只是百分比带小数、其余正常:必须 ready,不能因整数解析失败而永远 unknown。
        UsageSnapshot decimalOnly = ChatGptUsage.Parse(RecordedUsage.Replace("\"used_percent\":0,", "\"used_percent\":12.5,"));
        Assert.Equal("ok", ProviderCatalogService.EvaluateChatGpt(decimalOnly, "gpt-6-astra").Reason);
        // rate_limit_reached_type 非 null 即视为限流,不管形状。
        UsageSnapshot typed = ChatGptUsage.Parse(RecordedUsage.Replace("\"rate_limit_reached_type\":null", "\"rate_limit_reached_type\":{\"kind\":\"x\"}"));
        Assert.Equal("limited", ProviderCatalogService.EvaluateChatGpt(typed, "gpt-6-astra").Reason);
    }

    [Fact]
    public void 元数据失败文案区分登录失效_限流_版本与超时()
    {
        Assert.Contains("重新登录", ProviderCatalogService.DescribeMetadataFailure(new HttpRequestException("x", null, System.Net.HttpStatusCode.Unauthorized), "模型目录"), StringComparison.Ordinal);
        Assert.Contains("429", ProviderCatalogService.DescribeMetadataFailure(new HttpRequestException("x", null, System.Net.HttpStatusCode.TooManyRequests), "订阅窗口"), StringComparison.Ordinal);
        Assert.Contains("版本", ProviderCatalogService.DescribeMetadataFailure(new InvalidOperationException("Codex 版本未核实"), "模型目录"), StringComparison.Ordinal);
        Assert.Contains("超时", ProviderCatalogService.DescribeMetadataFailure(new OperationCanceledException(), "订阅窗口"), StringComparison.Ordinal);
        Assert.Equal("模型目录暂时无法读取", ProviderCatalogService.DescribeMetadataFailure(new Exception("?"), "模型目录"));
    }

    [Fact]
    public void 执行计划以官方provider固定端点且不经环境传递凭据()
    {
        string root = TestTemp.NewDir("codex-official-plan");
        string project = TestTemp.NewDir("codex-official-project");
        string session = Guid.NewGuid().ToString("D");
        string sessions = Path.Combine(root, "sessions");
        Directory.CreateDirectory(sessions);
        File.WriteAllText(Path.Combine(sessions, "rollout-" + session + ".jsonl"), JsonSerializer.Serialize(
            new { type = "session_meta", payload = new { id = session, cwd = project, source = "vscode" } }));
        string configPath = Path.Combine(root, "config.toml");
        File.WriteAllText(configPath, "model_provider = \"codex_web_gpt\"\nopenai_base_url = \"http://127.0.0.1:17841/v1\"\n[model_providers.codex_web_gpt]\nbase_url = \"http://127.0.0.1:17841/v1\"\nrequires_openai_auth = true\n");
        string managedPath = Path.Combine(root, "managed_config.toml");
        File.WriteAllText(managedPath, "# managed\n");
        var official = new CodexProviderCredentials("https://chatgpt.com/backend-api/codex", "chatgpt-secret-token", "gpt-5.6-sol", "responses",
            new Dictionary<string, string>(), new Dictionary<string, string>(), "auth.json:chatgpt", null, "acct", "openai", true, true);
        var registry = new ProviderRegistry(_ => null, root, codex: () => official, codexOfficial: () => official);
        ProviderConnection connection = registry.Find("codex-official")!;
        var config = ProductConfig.CreateDefault();
        config.ResumeModel = "gpt-5.6-sol";
        config.ResumeTarget = new() { ProviderId = connection.Id, ProviderIdentity = connection.Identity, Agent = "codex", ModelId = "gpt-5.6-sol" };
        var planner = new ProviderExecutionPlan(registry, new ResumeSessionCatalog(root, sessions),
            _ => new AgentCommand(Path.Combine(root, "never-run.exe"), Array.Empty<string>()),
            (_, _) => new[] { configPath, managedPath }, root, () => true);

        using ResumeExecutionPlan plan = planner.Create(new ProjectRef { Path = project, SessionId = session }, config);

        string joined = string.Join(" ", plan.Arguments);
        Assert.Contains("model_provider=\"openai\"", plan.Arguments);
        Assert.Contains("openai_base_url=\"https://chatgpt.com/backend-api/codex\"", plan.Arguments);
        Assert.DoesNotContain("chatgpt-secret-token", joined, StringComparison.Ordinal);
        Assert.DoesNotContain("AI_RESUME_PROVIDER_KEY", plan.Environment.Keys);
        Assert.DoesNotContain(plan.Environment.Values, v => v == "chatgpt-secret-token");
        Assert.Equal(root, plan.Environment["CODEX_HOME"]);
        // 本机桥接常用 OPENAI_BASE_URL 环境把官方端点改到 127.0.0.1:子进程环境必须钉成同一官方地址。
        Assert.Equal("https://chatgpt.com/backend-api/codex", plan.Environment["OPENAI_BASE_URL"]);
        Assert.Contains("resume", plan.Arguments);
        Assert.Contains(session, plan.Arguments);
    }

    [Fact]
    public void 未登录的官方连接拒绝生成执行计划()
    {
        string root = TestTemp.NewDir("codex-official-nologin");
        var missing = new CodexProviderCredentials("https://chatgpt.com/backend-api/codex", null, null, "responses",
            new Dictionary<string, string>(), new Dictionary<string, string>(), "auth.json", "Codex 未登录", null, "openai", true, true);
        var registry = new ProviderRegistry(_ => null, root, codex: () => missing, codexOfficial: () => missing);
        ProviderConnection connection = registry.Find("codex-official")!;
        var config = ProductConfig.CreateDefault();
        config.ResumeModel = "gpt-5.6-sol";
        config.ResumeTarget = new() { ProviderId = connection.Id, ProviderIdentity = connection.Identity, Agent = "codex", ModelId = "gpt-5.6-sol" };
        var planner = new ProviderExecutionPlan(registry, new ResumeSessionCatalog(root, Path.Combine(root, "sessions")),
            _ => new AgentCommand(Path.Combine(root, "never-run.exe"), Array.Empty<string>()),
            (_, _) => Array.Empty<string>(), root, () => true);

        Assert.Throws<InvalidOperationException>(() => planner.Create(
            new ProjectRef { Path = root, SessionId = Guid.NewGuid().ToString("D") }, config));
    }

    [Fact]
    public void 主配置活动profile指定model_provider时拒绝固定供应商()
    {
        string root = TestTemp.NewDir("codex-official-profile");
        string project = TestTemp.NewDir("codex-official-profile-project");
        string session = Guid.NewGuid().ToString("D");
        string sessions = Path.Combine(root, "sessions");
        Directory.CreateDirectory(sessions);
        File.WriteAllText(Path.Combine(sessions, "rollout-" + session + ".jsonl"), JsonSerializer.Serialize(
            new { type = "session_meta", payload = new { id = session, cwd = project, source = "vscode" } }));
        string configPath = Path.Combine(root, "config.toml");
        // 固定上游 0.148.0:config_profile.model_provider.or(cfg.model_provider),profile 会压过 -c 写进根表的值。
        File.WriteAllText(configPath, "profile = \"work\"\n[profiles.work]\nmodel_provider = \"codex_web_gpt\"\n[model_providers.codex_web_gpt]\nbase_url = \"http://127.0.0.1:17841/v1\"\n");
        File.WriteAllText(Path.Combine(root, "managed_config.toml"), "# managed\n");
        var official = new CodexProviderCredentials("https://chatgpt.com/backend-api/codex", "chatgpt-secret-token", "gpt-5.6-sol", "responses",
            new Dictionary<string, string>(), new Dictionary<string, string>(), "auth.json:chatgpt", null, "acct", "openai", true, true);
        var registry = new ProviderRegistry(_ => null, root, codex: () => official, codexOfficial: () => official);
        ProviderConnection connection = registry.Find("codex-official")!;
        var config = ProductConfig.CreateDefault();
        config.ResumeModel = "gpt-5.6-sol";
        config.ResumeTarget = new() { ProviderId = connection.Id, ProviderIdentity = connection.Identity, Agent = "codex", ModelId = "gpt-5.6-sol" };
        var planner = new ProviderExecutionPlan(registry, new ResumeSessionCatalog(root, sessions),
            _ => new AgentCommand(Path.Combine(root, "never-run.exe"), Array.Empty<string>()),
            (_, _) => new[] { configPath, Path.Combine(root, "managed_config.toml") }, root, () => true);

        var ex = Assert.Throws<InvalidOperationException>(() => planner.Create(new ProjectRef { Path = project, SessionId = session }, config));
        Assert.Contains("profile", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void 未登录时OfficialCodexCommand拒绝猜测端点()
    {
        string emptyHome = TestTemp.NewDir("codex-official-cmd-nologin");
        var ex = Assert.Throws<InvalidOperationException>(() => CutoverConfigCommand.OfficialCodexCommand(null, emptyHome));
        Assert.Contains("codex login", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void claudecode用户自写的cmd在重新生成时原样保留()
    {
        string dir = TestTemp.NewDir("codex-official-user-cmd");
        string path = Path.Combine(dir, "config.toml");
        var config = new CcConnectConfig(
            new[] { new CcConnectProject("ai-resume", "claudecode", @"C:\w", AdminFrom: "ou_1") },
            new CcConnectPlatformOptions("cli_x", "secret", "ou_1"));
        CcConnectConfigGenerator.Write(path, config);
        string written = File.ReadAllText(path);
        Assert.Contains("      work_dir = \"C:\\\\w\"", written, StringComparison.Ordinal);
        // 用户手工给 claudecode 指定 shim。
        File.WriteAllText(path, written.Replace("      work_dir = \"C:\\\\w\"", "      work_dir = \"C:\\\\w\"\n      cmd = \"C:\\\\tools\\\\claude.cmd\""));

        CcConnectConfigGenerator.Write(path, config);

        string again = File.ReadAllText(path);
        var root = Tomlyn.TomlSerializer.Deserialize<Tomlyn.Model.TomlTable>(again)!;
        var projects = (Tomlyn.Model.TomlTableArray)root["projects"];
        var options = (Tomlyn.Model.TomlTable)((Tomlyn.Model.TomlTable)((Tomlyn.Model.TomlTable)projects[0])["agent"])["options"];
        Assert.Equal(@"C:\tools\claude.cmd", options["cmd"]);
        Assert.Equal(1, again.Split("cmd = ").Length - 1);
    }

    [Fact]
    public void 生成器删除只服务codex的provider并写入官方cmd()
    {
        string existing = """
            [[providers]]
              name = "deepseek"
              api_key = "k1"
              base_url = "https://api.deepseek.com/anthropic"
              model = "deepseek-chat"

            [[providers]]
              name = "sub2api"
              api_key = "k2"
              base_url = "https://sub2api.example.invalid"
              model = "gpt-5.6-sol"
              agent_types = ["codex"]

              [[providers.models]]
                model = "gpt-5.6-sol"
                alias = "[AI Resume] GPT-5.6 Sol"

              [providers.codex]
                wire_api = "responses"

            [[providers]]
              name = "both"
              api_key = "k3"
              base_url = "https://both.example.invalid/v1"
              agent_types = ["codex", "claudecode"]

            [management]
              enabled = true
              port = 9820
              token = "t"
            """;

        string cleaned = CcConnectConfigGenerator.RemoveCodexOnlyProviders(existing, out IReadOnlyList<string> removed);

        Assert.Equal(new[] { "sub2api" }, removed);
        Assert.DoesNotContain("sub2api", cleaned, StringComparison.Ordinal);
        Assert.DoesNotContain("[AI Resume] GPT-5.6 Sol", cleaned, StringComparison.Ordinal);
        Assert.Contains("name = \"deepseek\"", cleaned, StringComparison.Ordinal);
        Assert.Contains("name = \"both\"", cleaned, StringComparison.Ordinal);
        Assert.Contains("[management]", cleaned, StringComparison.Ordinal);
        Assert.Equal(cleaned, CcConnectConfigGenerator.RemoveCodexOnlyProviders(cleaned, out IReadOnlyList<string> again));
        Assert.Empty(again);

        string cmd = CutoverConfigCommand.OfficialCodexCommand("https://chatgpt.com/backend-api/codex");
        Assert.Equal("codex -c model_provider=\"openai\" -c openai_base_url=\"https://chatgpt.com/backend-api/codex\"", cmd);
        Assert.Throws<InvalidOperationException>(() => CutoverConfigCommand.OfficialCodexCommand("https://x y"));

        var config = new CcConnectConfig(
            new[] { new CcConnectProject("ai-resume", "codex", @"C:\w", AdminFrom: "ou_1", Cmd: cmd) },
            new CcConnectPlatformOptions("cli_x", "secret", "ou_1"));
        string rendered = CcConnectConfigGenerator.RenderSanitized(config);
        Assert.Contains("cmd = \"codex -c model_provider=\\\"openai\\\" -c openai_base_url=\\\"https://chatgpt.com/backend-api/codex\\\"\"", rendered, StringComparison.Ordinal);
        Assert.DoesNotContain("provider_refs", rendered, StringComparison.Ordinal);
    }

    [Fact]
    public void Write带removeCodexOnlyProviders时产出可被Tomlyn解析且cmd落在agentOptions()
    {
        string dir = TestTemp.NewDir("codex-official-write");
        string path = Path.Combine(dir, "config.toml");
        File.WriteAllText(path, """
            [[providers]]
              name = "sub2api"
              api_key = "k2"
              base_url = "https://sub2api.example.invalid"
              agent_types = ["codex"]

            [management]
              enabled = true
              port = 9820
              token = "t"
            """);
        string cmd = CutoverConfigCommand.OfficialCodexCommand("https://chatgpt.com/backend-api/codex");
        var config = new CcConnectConfig(
            new[] { new CcConnectProject("ai-resume", "codex", @"C:\w", AdminFrom: "ou_1", Cmd: cmd) },
            new CcConnectPlatformOptions("cli_x", "secret", "ou_1"));

        CcConnectConfigGenerator.Write(path, config, removeCodexOnlyProviders: true);

        string written = File.ReadAllText(path);
        var root = Tomlyn.TomlSerializer.Deserialize<Tomlyn.Model.TomlTable>(written)!;
        Assert.False(root.ContainsKey("providers"));
        var projects = (Tomlyn.Model.TomlTableArray)root["projects"];
        var options = (Tomlyn.Model.TomlTable)((Tomlyn.Model.TomlTable)((Tomlyn.Model.TomlTable)projects[0])["agent"])["options"];
        Assert.Equal(cmd, options["cmd"]);
        Assert.Equal(9820L, ((Tomlyn.Model.TomlTable)root["management"])["port"]);
        // 字段归我方所有:再次写入不会堆出第二行 cmd。
        CcConnectConfigGenerator.Write(path, config, removeCodexOnlyProviders: true);
        Assert.Equal(1, File.ReadAllText(path).Split("cmd = ").Length - 1);
    }
}
