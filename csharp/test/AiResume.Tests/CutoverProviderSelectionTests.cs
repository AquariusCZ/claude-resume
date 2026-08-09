using AiResume.Worker.Migration;
using Xunit;

namespace AiResume.Tests;

public sealed class CutoverProviderSelectionTests
{
    [Fact]
    public void 唯一兼容provider会成为活动选择并带出有效模型()
    {
        const string toml = """
            [[providers]]
            name = "chatpt"
            model = "fallback"
            agent_types = ["codex"]
            agent_models = { codex = "gpt-5.6" }

            [[providers]]
            name = "deepseek"
            model = "deepseek-chat"
            agent_types = ["claudecode"]
            """;

        var selection = CutoverConfigCommand.ResolveUnambiguousProviderSelection(
            toml, "codex", new[] { "chatpt" }, preserveAgentSelection: false);

        Assert.Equal(("chatpt", "gpt-5.6"), selection);
    }

    [Fact]
    public void 多个兼容provider时不静默猜选()
    {
        const string toml = """
            [[providers]]
            name = "a"
            model = "gpt-5.6"
            agent_types = ["codex"]

            [[providers]]
            name = "b"
            model = "gpt-5.6-terra"
            agent_types = ["codex"]
            """;

        var selection = CutoverConfigCommand.ResolveUnambiguousProviderSelection(
            toml, "codex", new[] { "a", "b" }, preserveAgentSelection: false);

        Assert.Equal((string.Empty, string.Empty), selection);
    }

    [Fact]
    public void 保留既有选择时生成器不再写第二份默认值()
    {
        const string toml = """
            [[providers]]
            name = "a"
            model = "gpt-5.6"
            agent_types = ["codex"]
            """;

        var selection = CutoverConfigCommand.ResolveUnambiguousProviderSelection(
            toml, "codex", new[] { "a" }, preserveAgentSelection: true);

        Assert.Equal((string.Empty, string.Empty), selection);
    }
}
