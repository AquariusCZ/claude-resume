using AiResume.Wrapper;
using Xunit;

namespace AiResume.Tests;

/// <summary>
/// agent、provider、model 三段必须对得上。
///
/// 2026-08-08 用户实测:把 agent 换成 codex、也重新生成了配置,回复却还是 Claude 的模型。
/// 原因是 <c>provider</c> 与 <c>model</c> 都是当初 agent=claudecode 时留下的,
/// **换 agent 不会重置它们** —— 而配置文件本身完全合法,cc-connect 也照常启动。
/// 又一个静默失败:三段里有两段还指着 Claude,界面上没有任何地方说得出这件事。
/// </summary>
public sealed class CcConnectAgentCoherenceTests
{
    /// <summary>照抄用户真机配置的形状(密钥换成假值)。</summary>
    private const string RealShape = """
        [[providers]]
          name = "chatpt-monthly"
          api_key = "sk-a"
          base_url = "https://relay.example.invalid"
          model = "gpt-5.6"
          agent_types = ["codex"]

        [[providers]]
          name = "deepseek"
          api_key = "sk-b"
          base_url = "https://api.deepseek.com/anthropic"

        [[projects]]
          name = "ai-resume"

          [projects.agent]
            type = "codex"
            provider_refs = ["chatpt-monthly", "deepseek"]

            [projects.agent.options]
              mode = "default"
              work_dir = "C:/work"

        provider = "deepseek"
        model = "opus"
        """;

    [Fact]
    public void 端点方言与agent对不上时点名()
    {
        var problems = CcConnectConfigValidator.CheckAgentCoherence(RealShape);

        // deepseek 那条 provider 的端点是 …/anthropic,只有 claudecode 说得通。
        Assert.Contains(problems, p => p.Contains("Anthropic 形状") && p.Contains("codex"));
    }

    [Fact]
    public void Claude别名留在非claudecode项目里要点名()
    {
        var problems = CcConnectConfigValidator.CheckAgentCoherence(RealShape);

        Assert.Contains(problems, p => p.Contains("opus") && p.Contains("换 agent 不会重置模型"));
    }

    [Fact]
    public void agent_types声明不含当前agent时点名()
    {
        string toml = RealShape.Replace("provider = \"deepseek\"", "provider = \"chatpt-monthly\"")
                               .Replace("type = \"codex\"", "type = \"claudecode\"");

        var problems = CcConnectConfigValidator.CheckAgentCoherence(toml);

        Assert.Contains(problems, p => p.Contains("chatpt-monthly") && p.Contains("对不上"));
    }

    [Fact]
    public void 三段一致时一条都不报()
    {
        // 误判比漏判更糟:告诉用户一份能用的配置坏了,他会去改一个没问题的东西。
        string toml = RealShape.Replace("type = \"codex\"", "type = \"claudecode\"")
                               .Replace("model = \"opus\"\n", "model = \"deepseek-chat\"\n");

        Assert.Empty(CcConnectConfigValidator.CheckAgentCoherence(toml));
    }

    [Fact]
    public void codex配chatgpt月付是对的组合()
    {
        string toml = RealShape.Replace("provider = \"deepseek\"", "provider = \"chatpt-monthly\"")
                               .Replace("model = \"opus\"", "model = \"gpt-5.6\"");

        Assert.Empty(CcConnectConfigValidator.CheckAgentCoherence(toml));
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("[log]\nlevel = \"info\"\n")]
    public void 看不出结论时一条都不报(string? toml)
    {
        Assert.Empty(CcConnectConfigValidator.CheckAgentCoherence(toml));
    }

    [Fact]
    public void speech段里的空provider不能顶掉项目里的()
    {
        // 真机实测:`[speech]` 段有一行 `provider = ""`,而它排在项目区之前。
        // 取全文第一个 provider 会拿到空串,整段 provider 判断被静默跳过、一条都不报——
        // **判据自己出这种错比不做检查更糟:它看起来在检查。**
        string toml = RealShape.Replace(
            "[[projects]]",
            "[speech]\n  enabled = false\n  provider = \"\"\n\n[[projects]]");

        var problems = CcConnectConfigValidator.CheckAgentCoherence(toml);

        Assert.Contains(problems, p => p.Contains("Anthropic 形状"));
    }

    [Fact]
    public void 平台块里的type不能被当成agent()
    {
        // [[projects.platforms]] 里也有 `type = "feishu"`,按段扫才不会混。
        string toml = RealShape + "\n\n  [[projects.platforms]]\n    type = \"feishu\"\n";

        Assert.Equal("codex", CcConnectConfigValidator.ReadProjectAgentTriple(toml).Agent);
    }

    [Fact]
    public void 三元组按段取到正确的值()
    {
        var (agent, provider, model) = CcConnectConfigValidator.ReadProjectAgentTriple(RealShape);

        Assert.Equal("codex", agent);
        Assert.Equal("deepseek", provider);
        Assert.Equal("opus", model);
    }

    [Fact]
    public void 没声明agent_types且端点看不出方言时不猜()
    {
        const string vague = """
            [[providers]]
              name = "relay"
              base_url = "https://relay.example.invalid/v1"

            [[projects]]
              [projects.agent]
                type = "codex"

            provider = "relay"
            model = "gpt-5.6"
            """;

        Assert.Empty(CcConnectConfigValidator.CheckAgentCoherence(vague));
    }
}
