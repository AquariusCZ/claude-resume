using AiResume.Wrapper;
using Xunit;

namespace AiResume.Tests;

/// <summary>
/// 非飞书平台的原样保留(S10-S)。
///
/// 为什么必须钉住:`cc-connect weixin setup` 是**扫码**绑定的,绑定结果只落在
/// config.toml 的 [[projects.platforms]] 里。我们的生成器整块重写 [[projects]],
/// 一旦这里失效,用户每次点「生成 cc-connect 配置」就把上次扫的码作废一次——
/// 而且现象是静默的:配置照常生成、飞书照常能用,只有微信悄悄没了。
///
/// 同类事故已经发生过一次:整份重写把 [management] 抹掉,admin 页直接打不开。
/// </summary>
public sealed class CcConnectForeignPlatformTests
{
    private const string Toml = """
        [[projects]]
          name = "ai-resume"

          [projects.agent]
            type = "claudecode"

          [[projects.platforms]]
            type = "feishu"

            [projects.platforms.options]
              app_id = "cli_x"
              app_secret = "s"
              allow_from = "ou_1"

          [[projects.platforms]]
            type = "weixin"

            [projects.platforms.options]
              token = "eyJhbGciOi"
              api_url = "https://ilinkai.weixin.qq.com"
              allow_from = "wx_1"

        [management]
          port = 9820
        """;

    [Fact]
    public void 微信平台被原样保留而飞书块被丢弃()
    {
        IReadOnlyList<string> blocks = CcConnectConfigGenerator.ExtractForeignPlatforms(Toml);

        Assert.Single(blocks);
        Assert.Contains("type = \"weixin\"", blocks[0]);
        Assert.Contains("api_url", blocks[0]);
        // 飞书块由生成器自己产出,保留它会导致重复。
        Assert.DoesNotContain("feishu", blocks[0]);
        // [management] 属于另一区(由 ExtractNonProjectSections 负责),不能混进来。
        Assert.DoesNotContain("[management]", blocks[0]);
    }

    [Fact]
    public void 子表属于本块而下一个平台头结束本块()
    {
        IReadOnlyList<string> blocks = CcConnectConfigGenerator.ExtractForeignPlatforms(Toml);

        // options 子表必须跟着走,否则保留下来的是个没有凭据的空壳。
        Assert.Contains("[projects.platforms.options]", blocks[0]);
        // 但不能把 [management] 之后的内容吞进来。
        Assert.DoesNotContain("port = 9820", blocks[0]);
    }

    [Fact]
    public void 取不到类型的平台宁可多留不可丢()
    {
        const string noType = """
            [[projects]]
              [[projects.platforms]]
                [projects.platforms.options]
                  token = "abc"
            """;

        // 丢了等于用户白扫一次码;多留最多是一份冗余配置,cc-connect 自己会报错。
        Assert.Single(CcConnectConfigGenerator.ExtractForeignPlatforms(noType));
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    public void 空输入返回空列表且不抛(string? input)
        => Assert.Empty(CcConnectConfigGenerator.ExtractForeignPlatforms(input!));

    [Fact]
    public void 只有飞书时没有可保留的块()
    {
        const string onlyFeishu = """
            [[projects]]
              [[projects.platforms]]
                type = "feishu"
                [projects.platforms.options]
                  app_id = "cli_x"
            """;

        Assert.Empty(CcConnectConfigGenerator.ExtractForeignPlatforms(onlyFeishu));
    }

    [Fact]
    public void 平台类型值保留精确语义不得Trim后误认成Feishu()
    {
        const string spacedType = """
            [[projects]]
              [[projects.platforms]]
                type = " feishu "
                [projects.platforms.options]
                  token = "keep-me"
            """;

        IReadOnlyList<string> blocks = CcConnectConfigGenerator.ExtractForeignPlatforms(spacedType);

        Assert.Single(blocks);
        Assert.Contains("type = \" feishu \"", blocks[0], StringComparison.Ordinal);
        Assert.Contains("token = \"keep-me\"", blocks[0], StringComparison.Ordinal);
    }

    [Fact]
    public void 脱敏回显报数但绝不包含保留块内容()
    {
        // 值取一个**不与键名重叠**的串:写成 "secret" 会和 app_secret 这个键名撞,
        // 断言"输出里没有 secret"必然假红。
        var config = new CcConnectConfig(
            [new CcConnectProject("ai-resume", "claudecode", @"C:\w", AdminFrom: "ou_1")],
            new CcConnectPlatformOptions("cli_x", "S3CR3T-VALUE", "ou_1"));

        string sanitized = CcConnectConfigGenerator.RenderSanitized(config, foreignPlatformCount: 1);

        // 报数:不报的话用户以为微信被这次生成抹掉了,会再去扫一次码。
        Assert.Contains("另保留 1", sanitized);
        // **绝不回显内容**:微信平台 options 里的 token 是凭据。
        Assert.DoesNotContain("eyJhbGciOi", sanitized);
        Assert.DoesNotContain("ilinkai", sanitized);
        Assert.DoesNotContain("S3CR3T-VALUE", sanitized);
    }

    [Fact]
    public void 没有保留块时不追加计数行()
    {
        var config = new CcConnectConfig(
            [new CcConnectProject("ai-resume", "claudecode", @"C:\w", AdminFrom: "ou_1")],
            new CcConnectPlatformOptions("cli_x", "secret", "ou_1"));

        Assert.DoesNotContain("另保留", CcConnectConfigGenerator.RenderSanitized(config, 0));
    }

    [Fact]
    public void 写入后微信块仍在且飞书块被重写为新凭据()
    {
        string path = TestTemp.NewFile("ccx", ".toml");
        try
        {
            File.WriteAllText(path, Toml);

            var config = new CcConnectConfig(
                [new CcConnectProject("ai-resume", "claudecode", @"C:\w", AdminFrom: "ou_new")],
                new CcConnectPlatformOptions("cli_new", "secret_new", "ou_new"));

            CcConnectConfigGenerator.Write(path, config);
            string after = File.ReadAllText(path);

            // 这是本文件存在的理由:重写之后扫码绑的微信必须还在。
            Assert.Contains("type = \"weixin\"", after);
            Assert.Contains("eyJhbGciOi", after);
            // 飞书块换成新凭据,且只有一份。
            Assert.Contains("cli_new", after);
            Assert.DoesNotContain("cli_x", after);
            // [management] 由另一条路径保留,一并复核——它被抹掉过一次。
            Assert.Contains("[management]", after);
        }
        finally
        {
            try { File.Delete(path); } catch (IOException) { }
        }
    }
}
