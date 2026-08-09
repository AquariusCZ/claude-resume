using AiResume.Wrapper;
using Xunit;

namespace AiResume.Tests;

public sealed class CcConnectProviderCatalogTests
{
    [Fact]
    public void 错误宣称必须new的遗留会话探针不进入正式Wrapper公共API()
    {
        Assert.Null(typeof(CcConnectProviderCatalog).Assembly.GetType(
            "AiResume.Wrapper.CcConnectSessionAgent", throwOnError: false));
    }

    [Fact]
    public void 结构化解析支持单引号多行agentTypes与inlineEndpoints()
    {
        const string toml = """
            [[providers]]
            name = 'router'
            base_url = 'https://router.example/anthropic'
            model = 'claude-default'
            agent_types = [
              'claudecode',
              'codex',
            ]
            endpoints = { codex = 'https://router.example/v1' }
            agent_models = { codex = 'gpt-custom' }
            """;

        CcConnectProviderDescriptor provider = Assert.Single(CcConnectProviderCatalog.Parse(toml).Providers);

        Assert.True(provider.SupportsAgent("codex"));
        Assert.Equal("https://router.example/v1", provider.Endpoints["codex"]);
        Assert.Equal("gpt-custom", provider.EffectiveModel("codex"));
    }

    [Fact]
    public void 重复名称遵循上游lastWins()
    {
        const string toml = """
            [[providers]]
            name = "same"
            base_url = "https://first.example/v1"
            agent_types = ["codex"]

            [[providers]]
            name = "same"
            base_url = "https://second.example/anthropic"
            agent_types = ["claudecode"]
            """;

        CcConnectProviderCatalog catalog = CcConnectProviderCatalog.Parse(toml);

        CcConnectProviderDescriptor provider = Assert.Single(catalog.Providers);
        Assert.Equal("https://second.example/anthropic", provider.BaseUrl);
        Assert.False(provider.SupportsAgent("codex"));
        Assert.True(provider.SupportsAgent("claudecode"));
    }

    [Fact]
    public void agent专属模型列表优先于全局列表()
    {
        const string toml = """
            [[providers]]
            name = "router"
            model = "default"

              [[providers.models]]
              model = "global-model"

              [[providers.agent_model_lists.codex]]
              model = "gpt-custom"
              alias = "Custom GPT"
            """;

        CcConnectProviderDescriptor provider = Assert.Single(CcConnectProviderCatalog.Parse(toml).Providers);

        Assert.Equal("gpt-custom", Assert.Single(provider.EffectiveModels("codex")).Model);
        Assert.Equal("global-model", Assert.Single(provider.EffectiveModels("claudecode")).Model);
    }

    [Fact]
    public void validator与provider筛选共用endpointOverride语义()
    {
        const string toml = """
            [[providers]]
            name = "router"
            base_url = "https://router.example/anthropic"
            endpoints = { codex = "https://router.example/v1" }

            [[projects]]
            name = "ai-resume"
            [projects.agent]
            type = "codex"
            [projects.agent.options]
            provider = "router"
            """;

        Assert.Equal(new[] { "router" }, CcConnectConfigGenerator.ReadGlobalProviderNames(toml, "codex"));
        Assert.Empty(CcConnectConfigValidator.CheckAgentCoherence(toml, "ai-resume"));
    }

    [Fact]
    public void 多项目读取只选择指定项目()
    {
        const string toml = """
            [[projects]]
            name = "other"
            [projects.agent]
            type = "claudecode"
            [projects.agent.options]
            provider = "deepseek"
            model = "opus"

            [[projects]]
            name = "ai-resume"
            [projects.agent]
            type = "codex"
            [projects.agent.options]
            provider = "chatpt"
            model = "gpt-5.6"
            work_dir = "C:\\work"
            """;

        var triple = CcConnectConfigValidator.ReadProjectAgentTriple(toml, "ai-resume");

        Assert.Equal(("codex", "chatpt", "gpt-5.6"), triple);
        Assert.Equal(@"C:\work", CcConnectConfigGenerator.TryReadExistingWorkDir(toml, "ai-resume"));
    }

    [Fact]
    public void provider与agent映射按上游规则严格区分大小写()
    {
        const string toml = """
            [[providers]]
            name = "Router"
            agent_types = ["codex"]
            endpoints = { Codex = "https://wrong.example/v1" }

            [[providers]]
            name = "router"
            agent_types = ["Codex"]
            """;

        CcConnectProviderCatalog catalog = CcConnectProviderCatalog.Parse(toml);

        Assert.Equal(2, catalog.Providers.Count);
        Assert.NotNull(catalog.Find("Router"));
        Assert.NotNull(catalog.Find("router"));
        Assert.Null(catalog.Find("ROUTER"));
        Assert.True(catalog.Find("Router")!.SupportsAgent("codex"));
        Assert.False(catalog.Find("router")!.SupportsAgent("codex"));
        Assert.False(catalog.Find("Router")!.Endpoints.ContainsKey("codex"));
    }

    [Fact]
    public void 内联models数组会成为真实候选而不是触发单项补全()
    {
        const string toml = """
            [[providers]]
            name = "router"
            model = "default"
            models = [{ model = "a", alias = "A" }, { model = "b" }]
            """;

        CcConnectProviderDescriptor provider = Assert.Single(CcConnectProviderCatalog.Parse(toml).Providers);

        Assert.Equal(new[] { "a", "b" }, provider.Models.Select(model => model.Model));
        Assert.Equal("A", provider.Models[0].Alias);
    }

    [Fact]
    public void 内联agentModelLists数组按agent解析()
    {
        const string toml = """
            [[providers]]
            name = "router"
            model = "default"
            agent_model_lists = { codex = [{ model = "gpt-a" }, { model = "gpt-b" }] }
            """;

        CcConnectProviderDescriptor provider = Assert.Single(CcConnectProviderCatalog.Parse(toml).Providers);

        Assert.Equal(new[] { "gpt-a", "gpt-b" },
            provider.EffectiveModels("codex").Select(model => model.Model));
        Assert.Empty(provider.EffectiveModels("claudecode"));
        Assert.True(provider.HasAgentModelListsDefinition);
    }

    [Fact]
    public void 精确选择第一个同名项目且只读agentOptions的providerModel()
    {
        const string toml = """
            [[projects]]
            name = "AI-Resume"
            [projects.agent]
            type = "claudecode"

            [[projects]]
            name = "ai-resume"
            provider = "ignored-top-level"
            model = "ignored-top-level"
            [projects.agent]
            type = "codex"
            [projects.agent.options]
            provider = "first"
            model = "gpt-first"

            [[projects]]
            name = "ai-resume"
            [projects.agent]
            type = "claudecode"
            [projects.agent.options]
            provider = "second"
            model = "opus"
            """;

        Assert.Equal(("codex", "first", "gpt-first"),
            CcConnectConfigValidator.ReadProjectAgentTriple(toml, "ai-resume"));
        Assert.Equal(("claudecode", "", ""),
            CcConnectConfigValidator.ReadProjectAgentTriple(toml, "AI-Resume"));
    }

    [Fact]
    public void TOML字符串保留原值且空覆盖遵循上游非空判定()
    {
        const string toml = """
            [[providers]]
            name = "router "
            base_url = "https://base.example/v1"
            agent_types = [" codex"]
            endpoints = { codex = "", other = " " }
            agent_models = { codex = "", other = " " }
            models = [{ model = "", alias = " empty " }]
            agent_model_lists = { codex = [] }
            """;

        CcConnectProviderDescriptor provider = Assert.Single(CcConnectProviderCatalog.Parse(toml).Providers);

        Assert.Equal("router ", provider.Name);
        Assert.Null(CcConnectProviderCatalog.Parse(toml).Find("router"));
        Assert.False(provider.SupportsAgent("codex"));
        Assert.True(provider.SupportsAgent(" codex"));
        Assert.Equal("https://base.example/v1", provider.BaseUrl);
        Assert.Equal("", provider.EffectiveModel("codex"));
        Assert.Equal(" ", provider.EffectiveModel("other"));
        Assert.Equal("", Assert.Single(provider.EffectiveModels("codex")).Model);
        Assert.True(provider.HasModelsDefinition);
        Assert.Equal("", Assert.Single(provider.Models).Model);
        Assert.Equal(" empty ", provider.Models[0].Alias);
    }

    [Fact]
    public void 项目选择字符串不裁剪以免匹配到另一个provider()
    {
        const string toml = """
            [[projects]]
            name = "ai-resume"
            [projects.agent]
            type = "codex "
            [projects.agent.options]
            provider = "router "
            model = " gpt"
            """;

        Assert.Equal(("codex ", "router ", " gpt"),
            CcConnectConfigValidator.ReadProjectAgentTriple(toml, "ai-resume"));
    }
}
