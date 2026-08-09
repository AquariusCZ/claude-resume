using AiResume.Wrapper;
using Xunit;

namespace AiResume.Tests;

/// <summary>
/// 同一 agent 下「生成 cc-connect 配置」不得抹掉用户在聊天里切好的 provider 与模型;
/// 切换 agent 时则必须清掉,避免把 opus/deepseek 等旧选择带给 Codex。
///
/// 2026-08-08 在真机配置里看到的形状:用户跑过 <c>/provider switch deepseek</c> 之后,
/// cc-connect 把结果写回项目区(顶格,紧跟在 <c>[projects.agent.options]</c> 之后):
/// <code>
/// provider = "deepseek"
/// model = "opus"
/// </code>
/// 而生成器把整个项目区都当成自己的、整块重写 —— 于是用户下一次点「生成配置」,
/// **他刚切的 provider 和模型就没了,而且没有任何提示。**
///
/// 这与「保留 [management]」「保留扫码绑定的微信平台块」是同一类错:
/// 这份配置不归我们独占,我们只拥有项目清单那一部分。
/// </summary>
public sealed class CcConnectProjectExtraKeysTests
{
    private const string RealShape = """
        [[providers]]
          name = "deepseek"
          api_key = "sk-x"

        [[projects]]
          name = "ai-resume"
          admin_from = "ou_x"

          [projects.agent]
            type = "claudecode"
            provider_refs = ["deepseek"]

            [projects.agent.options]
              mode = "default"
              work_dir = "C:\\work"

        provider = "deepseek"

        model = "opus"

          [[projects.platforms]]
            type = "feishu"

            [projects.platforms.options]
              app_id = "cli_x"
              app_secret = "s"
              allow_from = "ou_x"

        [log]
          level = "info"
        """;

    [Fact]
    public void 捞回用户切过的provider与模型()
    {
        var extras = CcConnectConfigGenerator.ExtractProjectExtraKeys(RealShape);

        Assert.Equal(new[] { "provider = \"deepseek\"", "model = \"opus\"" }, extras);
    }

    [Fact]
    public void 我们自己生成的键不重复捞()
    {
        // name/type/mode/work_dir/provider_refs 都由 RenderCore 写出;
        // 再捞一遍会在文件里出现两次同名键,TOML 下行为未定义。
        var extras = CcConnectConfigGenerator.ExtractProjectExtraKeys(RealShape);

        foreach (string owned in new[] { "name", "type", "mode", "work_dir", "provider_refs", "admin_from" })
        {
            Assert.DoesNotContain(extras, e => e.StartsWith(owned + " ", StringComparison.Ordinal));
        }
    }

    [Fact]
    public void 平台块里的键不在这里收()
    {
        // app_id/app_secret/allow_from 由 ExtractForeignPlatforms 与 RenderCore 分别负责,
        // 这里再收一份会把飞书凭据复制到项目顶层。
        var extras = CcConnectConfigGenerator.ExtractProjectExtraKeys(RealShape);

        Assert.DoesNotContain(extras, e => e.Contains("app_secret", StringComparison.Ordinal));
        Assert.DoesNotContain(extras, e => e.Contains("app_id", StringComparison.Ordinal));
    }

    [Fact]
    public void 项目区之外的键一概不碰()
    {
        // [log] 与 [[providers]] 归 ExtractNonProjectSections 管,这里收了就会重复出现。
        var extras = CcConnectConfigGenerator.ExtractProjectExtraKeys(RealShape);

        Assert.DoesNotContain(extras, e => e.StartsWith("level", StringComparison.Ordinal));
        Assert.DoesNotContain(extras, e => e.StartsWith("api_key", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("[log]\nlevel = \"info\"\n")]
    public void 没有项目区时返回空(string? toml)
    {
        Assert.Empty(CcConnectConfigGenerator.ExtractProjectExtraKeys(toml!));
    }

    [Fact]
    public void 注释与空行不当成键()
    {
        const string toml = """
            [[projects]]
              name = "p"
              # provider = "被注释掉的不算"

              [projects.agent]
                type = "claudecode"

            thinking = "disabled"
            """;

        Assert.Equal(new[] { "thinking = \"disabled\"" }, CcConnectConfigGenerator.ExtractProjectExtraKeys(toml));
    }

    [Fact]
    public void 重复出现的同一行只保留一条()
    {
        const string toml = """
            [[projects]]
              name = "p"
              [projects.agent]
                type = "claudecode"
            provider = "deepseek"
            provider = "deepseek"
            """;

        Assert.Single(CcConnectConfigGenerator.ExtractProjectExtraKeys(toml));
    }

    [Fact]
    public void 写回之后这两行还在()
    {
        string path = TestTemp.NewFile("ccgen", ".toml");
        File.WriteAllText(path, RealShape);

        CcConnectConfigGenerator.Write(path, new CcConnectConfig(
            new[]
            {
                new CcConnectProject(
                    Name: "ai-resume", WorkDir: @"C:\work", Agent: "claudecode",
                    Mode: "default", AdminFrom: "ou_x", ProviderRefs: new[] { "deepseek" }),
            },
            new CcConnectPlatformOptions("cli_x", "s", "ou_x")));

        string after = File.ReadAllText(path);

        Assert.Contains("provider = \"deepseek\"", after, StringComparison.Ordinal);
        Assert.Contains("model = \"opus\"", after, StringComparison.Ordinal);
        // 顺带确认没把它们复制成两份。
        Assert.Equal(1, CountOccurrences(after, "provider = \"deepseek\""));
    }

    [Fact]
    public void 切换agent时只清provider与model并保留其它扩展键()
    {
        string toml = RealShape.Replace(
            "model = \"opus\"",
            "model = \"opus\"\nthinking = \"disabled\"");

        var extras = CcConnectConfigGenerator.ExtractProjectExtraKeys(
            toml, preserveAgentSelection: false);

        Assert.DoesNotContain(extras, e => e.StartsWith("provider ", StringComparison.Ordinal));
        Assert.DoesNotContain(extras, e => e.StartsWith("model ", StringComparison.Ordinal));
        Assert.Contains("thinking = \"disabled\"", extras);
    }

    [Fact]
    public void 多项目配置只捞指定项目的扩展键()
    {
        const string toml = """
            [[projects]]
            name = "other"
            provider = "other-provider"
            model = "other-model"
            [projects.agent]
            type = "claudecode"

            [[projects]]
            name = "ai-resume"
            provider = "chatpt"
            model = "gpt-5.6"
            thinking = "medium"
            [projects.agent]
            type = "codex"
            """;

        IReadOnlyList<string> extras = CcConnectConfigGenerator.ExtractProjectExtraKeys(
            toml, preserveAgentSelection: true, projectName: "ai-resume");

        Assert.Contains("provider = \"chatpt\"", extras);
        Assert.Contains("model = \"gpt-5.6\"", extras);
        Assert.Contains("thinking = \"medium\"", extras);
        Assert.DoesNotContain(extras, line => line.Contains("other-", StringComparison.Ordinal));
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        int n = 0, i = 0;
        while ((i = haystack.IndexOf(needle, i, StringComparison.Ordinal)) >= 0)
        {
            n++;
            i += needle.Length;
        }

        return n;
    }
}
