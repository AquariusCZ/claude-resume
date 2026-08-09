using AiResume.Wrapper;
using Tomlyn;
using Tomlyn.Model;
using Xunit;

namespace AiResume.Tests;

/// <summary>
/// S10-G cc-connect 配置合并写入回归测试。
///
/// 背景(2026-08-06 生产切换实测):原实现 Write 是整份覆盖,只写 [[projects]],
/// 把用户已启用的 [management] 段(http://localhost:9820 的 Web 管理台)一并抹掉,
/// admin 页直接打不开。这份配置不归我们独占——我们只拥有项目清单那一部分,
/// 其余是 cc-connect 与用户的。因此 Write 改为合并写入:
///   1. 目标文件已存在时,原样保留除 [[projects]] 及其子表之外的**全部内容**;
///   2. 然后在末尾追加本次生成的 [[projects]] 段;
///   3. 目标文件不存在时,行为不变(只写生成内容)。
///
/// 实现刻意做行级切分而非解析后重序列化:一旦解析再序列化,用户的注释、
/// 段落顺序和格式就全没了——而这份文件里的注释正是 cc-connect 自带的使用说明。
/// 因此本测试全部用字符串包含/计数断言,不做真实 TOML 解析。
/// </summary>
public sealed class CcConnectConfigPreserveTests : IDisposable
{
    private readonly List<string> _dirs = new();

    public void Dispose()
    {
        foreach (string dir in _dirs)
        {
            try
            {
                Directory.Delete(dir, recursive: true);
            }
            catch
            {
                // 清理失败不掩盖断言结果。
            }
        }
    }

    // ---- 工具 ----

    private string NewDir()
    {
        string dir = TestTemp.NewDir("s10g");
        Directory.CreateDirectory(dir);
        _dirs.Add(dir);
        return dir;
    }

    private static CcConnectConfig SampleConfig() => new(
        Projects: new List<CcConnectProject>
        {
            new("pilot", "claudecode", @"C:\temp\pilot"),
        },
        Feishu: new CcConnectPlatformOptions("cli_fake_test_app", "fake-secret", "ou_fake_owner"));

    private static CcConnectConfig TwoProjectConfig() => new(
        Projects: new List<CcConnectProject>
        {
            new("alpha", "claudecode", @"C:\temp\alpha"),
            new("beta", "codex", @"C:\temp\beta"),
        },
        Feishu: new CcConnectPlatformOptions("cli_fake_test_app", "fake-secret", "ou_fake_owner"));

    private static int CountOccurrences(string text, string needle)
    {
        int count = 0;
        int index = 0;
        while ((index = text.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += needle.Length;
        }

        return count;
    }

    // ---- 用例 1:保留 management 段 ----

    /// <summary>
    /// 核心回归:旧文件含 [management] 段(9820 端口 Web 管理台),Write 后必须原样保留。
    /// 2026-08-06 切换时整份覆盖把这段抹掉,admin 页直接打不开。
    /// </summary>
    [Fact]
    public void Write_preserves_management_section()
    {
        string dir = NewDir();
        string path = Path.Combine(dir, "config.toml");
        const string existing = """
            [management]
            enabled = true
            port = 9820
            token = "T"
            """;
        File.WriteAllText(path, existing);

        CcConnectConfigGenerator.Write(path, SampleConfig());
        string result = File.ReadAllText(path);

        // 管理台三行必须逐字保留。
        Assert.Contains("[management]", result, StringComparison.Ordinal);
        Assert.Contains("enabled = true", result, StringComparison.Ordinal);
        Assert.Contains("port = 9820", result, StringComparison.Ordinal);
        Assert.Contains("token = \"T\"", result, StringComparison.Ordinal);
        // 同时追加了项目段。
        Assert.Contains("[[projects]]", result, StringComparison.Ordinal);
    }

    // ---- 用例 2:保留顶层键与注释 ----

    /// <summary>
    /// 注释必须逐字保留——实现刻意做行级切分而非解析后重序列化,
    /// 一旦解析再序列化,用户的注释、段落顺序和格式就全没了。
    /// </summary>
    [Fact]
    public void Write_preserves_top_level_keys_and_comments_verbatim()
    {
        string dir = NewDir();
        string path = Path.Combine(dir, "config.toml");
        const string existing = """
            # 我的注释
            data_dir = "x"
            """;
        File.WriteAllText(path, existing);

        CcConnectConfigGenerator.Write(path, SampleConfig());
        string result = File.ReadAllText(path);

        // 注释逐字保留(含空格与标点)。
        Assert.Contains("# 我的注释", result, StringComparison.Ordinal);
        // 顶层键保留。
        Assert.Contains("data_dir = \"x\"", result, StringComparison.Ordinal);
        // 项目段追加在末尾。
        Assert.Contains("[[projects]]", result, StringComparison.Ordinal);
    }

    // ---- 用例 3:保留 projects 之后出现的段 ----

    /// <summary>
    /// 旧文件顺序是 [[projects]] 在前、[log] 在后。
    /// 剔除逻辑必须在遇到下一个非 projects 表头时正确退出项目区域,
    /// 否则 [log] 会被误删。
    /// </summary>
    [Fact]
    public void Write_preserves_sections_after_projects()
    {
        string dir = NewDir();
        string path = Path.Combine(dir, "config.toml");
        const string existing = """
            [[projects]]
            name = "OLD"

            [log]
            level = "debug"
            """;
        File.WriteAllText(path, existing);

        CcConnectConfigGenerator.Write(path, SampleConfig());
        string result = File.ReadAllText(path);

        // [log] 段必须保留。
        Assert.Contains("[log]", result, StringComparison.Ordinal);
        Assert.Contains("level = \"debug\"", result, StringComparison.Ordinal);
        // 新项目段追加。
        Assert.Contains("[[projects]]", result, StringComparison.Ordinal);
    }

    // ---- 用例 4:旧的 projects 段被剔除不残留 ----

    /// <summary>
    /// 旧文件含 [[projects]] + name = "OLD" + [projects.agent],
    /// 新配置只有 NEW 项目;断言结果不含 "OLD",且 [[projects]] 只出现新配置的个数。
    /// </summary>
    [Fact]
    public void Write_removes_old_projects_section()
    {
        string dir = NewDir();
        string path = Path.Combine(dir, "config.toml");
        const string existing = """
            [[projects]]
            name = "OLD"
            [projects.agent]
            type = "claudecode"
            """;
        File.WriteAllText(path, existing);

        CcConnectConfigGenerator.Write(path, SampleConfig());
        string result = File.ReadAllText(path);

        // 旧项目名不残留。
        Assert.DoesNotContain("OLD", result, StringComparison.Ordinal);
        // [[projects]] 只出现新配置的个数(1 个)。
        Assert.Equal(1, CountOccurrences(result, "[[projects]]"));
        // 新项目名出现。
        Assert.Contains("name = \"pilot\"", result, StringComparison.Ordinal);
    }

    // ---- 用例 5:旧的 projects 子表也被剔除 ----

    /// <summary>
    /// 旧文件含 [projects.agent.options] 与 [[projects.platforms]];
    /// 这些旧内容(用独特标记串 work_dir = "C:\OLD")不得残留。
    /// </summary>
    [Fact]
    public void Write_removes_old_projects_subtables()
    {
        string dir = NewDir();
        string path = Path.Combine(dir, "config.toml");
        const string existing = """
            [[projects]]
            name = "OLD"
            [projects.agent.options]
            work_dir = "C:\OLD"
            [[projects.platforms]]
            type = "feishu"
            """;
        File.WriteAllText(path, existing);

        CcConnectConfigGenerator.Write(path, SampleConfig());
        string result = File.ReadAllText(path);

        // 旧子表内容不残留。
        Assert.DoesNotContain("C:\\OLD", result, StringComparison.Ordinal);
        Assert.DoesNotContain("OLD", result, StringComparison.Ordinal);
        // 新配置的子表形状完整。
        Assert.Contains("[projects.agent.options]", result, StringComparison.Ordinal);
        Assert.Contains("[[projects.platforms]]", result, StringComparison.Ordinal);
    }

    [Fact]
    public void 合法尾注释和引号式项目表头不会丢失用户资产()
    {
        string path = Path.Combine(NewDir(), "config.toml");
        File.WriteAllText(path, """
            [["projects"]] # user project
            "name" = "pilot"
            custom_project = "keep-project"

            ["projects"."agent"] # user agent
            "type" = "claudecode"
            custom_agent = "keep-agent"

            ["projects"."agent"."options"] # user options
            "mode" = "default"
            "work_dir" = "C:\\old"
            custom_option = "keep-option"

            [["projects"."platforms"]] # user platform
            "type" = "weixin"
            ["projects"."platforms"."options"] # credential-bearing options
            token = "keep-weixin-token"

              [log]
              level = "debug"
            """);

        CcConnectConfigGenerator.Write(path, SampleConfig());
        string result = File.ReadAllText(path);
        TomlTable root = TomlSerializer.Deserialize<TomlTable>(result)!;
        TomlTable project = Assert.IsType<TomlTable>(Assert.IsType<TomlTableArray>(root["projects"])[0]);
        TomlTable agent = Assert.IsType<TomlTable>(project["agent"]);
        TomlTable options = Assert.IsType<TomlTable>(agent["options"]);
        TomlTableArray platforms = Assert.IsType<TomlTableArray>(project["platforms"]);
        TomlTable weixin = Assert.Single(platforms.OfType<TomlTable>(), platform =>
            string.Equals(platform["type"] as string, "weixin", StringComparison.Ordinal));

        Assert.Equal("keep-project", project["custom_project"]);
        Assert.Equal("keep-agent", agent["custom_agent"]);
        Assert.Equal("keep-option", options["custom_option"]);
        Assert.Equal("keep-weixin-token", Assert.IsType<TomlTable>(weixin["options"])["token"]);
        Assert.Contains("level = \"debug\"", result, StringComparison.Ordinal);
    }

    // ---- 用例 6:重复写入不累积 ----

    /// <summary>
    /// 连续 Write 三次:[[projects]] 数量恒等于项目数、
    /// [management] 只出现一次、生成标记行只出现一次。
    /// 防止逐轮累积生成标记与项目段。
    /// </summary>
    [Fact]
    public void Write_repeatedly_does_not_accumulate()
    {
        string dir = NewDir();
        string path = Path.Combine(dir, "config.toml");
        const string existing = """
            [management]
            enabled = true
            port = 9820
            """;
        File.WriteAllText(path, existing);

        CcConnectConfig config = TwoProjectConfig();
        CcConnectConfigGenerator.Write(path, config);
        CcConnectConfigGenerator.Write(path, config);
        CcConnectConfigGenerator.Write(path, config);
        string result = File.ReadAllText(path);

        // [[projects]] 数量恒等于项目数(2)。
        Assert.Equal(2, CountOccurrences(result, "[[projects]]"));
        // [management] 只出现一次。
        Assert.Equal(1, CountOccurrences(result, "[management]"));
        // 生成标记行只出现一次。
        Assert.Equal(1, CountOccurrences(result, "# Generated by AiResume.Wrapper (S6-A). Deterministic; do not edit by hand."));
    }

    // ---- 用例 7:目标文件不存在时行为不变 ----

    /// <summary>
    /// 空目录直写,断言内容等于 Render(config)。
    /// </summary>
    [Fact]
    public void Write_to_missing_file_matches_render()
    {
        string dir = NewDir();
        string path = Path.Combine(dir, "config.toml");

        CcConnectConfig config = SampleConfig();
        CcConnectConfigGenerator.Write(path, config);
        string result = File.ReadAllText(path);

        Assert.Equal(CcConnectConfigGenerator.Render(config), result);
    }

    // ---- 用例 8:旧文件为空或全空白 ----

    /// <summary>
    /// 空文件或全空白文件:不抛异常,结果含 [[projects]]。
    /// </summary>
    [Fact]
    public void Write_with_empty_or_whitespace_existing_file()
    {
        string dir = NewDir();
        string path = Path.Combine(dir, "config.toml");
        File.WriteAllText(path, "");

        CcConnectConfigGenerator.Write(path, SampleConfig());
        string result = File.ReadAllText(path);
        Assert.Contains("[[projects]]", result, StringComparison.Ordinal);

        // 全空白文件。
        File.WriteAllText(path, "   \n  \n\t\n");
        CcConnectConfigGenerator.Write(path, SampleConfig());
        result = File.ReadAllText(path);
        Assert.Contains("[[projects]]", result, StringComparison.Ordinal);
    }

    // ---- 用例 9:写入是原子的且不留 tmp ----

    /// <summary>
    /// Write 后断言目录下没有 *.tmp-* 残留。
    /// </summary>
    [Fact]
    public void Write_is_atomic_and_leaves_no_tmp()
    {
        string dir = NewDir();
        string path = Path.Combine(dir, "config.toml");
        const string existing = """
            [management]
            enabled = true
            """;
        File.WriteAllText(path, existing);

        CcConnectConfigGenerator.Write(path, SampleConfig());
        string result = File.ReadAllText(path);

        // 内容完整。
        Assert.Contains("[management]", result, StringComparison.Ordinal);
        Assert.Contains("[[projects]]", result, StringComparison.Ordinal);
        // 无 tmp 残留。
        Assert.Empty(Directory.EnumerateFiles(dir, "*.tmp-*"));
    }

    // ---- 用例 10:allow_from 为空时拒绝写入 ----

    /// <summary>
    /// fail-closed:空 allow_from 会让 cc-connect 放行所有人。
    /// 宁可拒绝生成配置,也不产出一份"任何人都能驱动 Claude Code 改你项目"的配置。
    /// 且目标文件保持原内容不变——不能写半截。
    /// </summary>
    [Fact]
    public void Write_rejects_empty_allow_from_and_preserves_existing()
    {
        string dir = NewDir();
        string path = Path.Combine(dir, "config.toml");
        const string existing = """
            [management]
            enabled = true
            port = 9820
            """;
        File.WriteAllText(path, existing);

        var badConfig = new CcConnectConfig(
            Projects: new List<CcConnectProject>
            {
                new("pilot", "claudecode", @"C:\temp\pilot"),
            },
            Feishu: new CcConnectPlatformOptions("cli_fake_test_app", "fake-secret", ""));

        Assert.Throws<ArgumentException>(() => CcConnectConfigGenerator.Write(path, badConfig));

        // 目标文件保持原内容不变(fail-closed 不能写半截)。
        string result = File.ReadAllText(path);
        Assert.Equal(existing, result);
        Assert.DoesNotContain("[[projects]]", result, StringComparison.Ordinal);
    }

    [Fact]
    public void Write_validates_candidate_before_replacing_existing_file()
    {
        string dir = NewDir();
        string path = Path.Combine(dir, "config.toml");
        const string existing = "[management]\nenabled = true\n";
        File.WriteAllText(path, existing);

        InvalidDataException error = Assert.Throws<InvalidDataException>(() =>
            CcConnectConfigGenerator.Write(
                path,
                SampleConfig(),
                validateCandidate: _ => "上游解析器拒绝候选配置"));

        Assert.Contains("上游解析器拒绝", error.Message, StringComparison.Ordinal);
        Assert.Equal(existing, File.ReadAllText(path));
        Assert.Empty(Directory.EnumerateFiles(dir, "*.tmp-*"));
    }

    [Fact]
    public void 第三方relay只补它明确配置的默认模型()
    {
        string dir = NewDir();
        string path = Path.Combine(dir, "config.toml");
        File.WriteAllText(path, """
            [[providers]]
              name = "chatpt-monthly"
              api_key = "secret"
              base_url = "https://router.example/v1"
              model = "gpt-5.6"
              agent_types = ["codex"]

            [[providers]]
              name = "deepseek"
              api_key = "secret"
              base_url = "https://api.deepseek.com/anthropic"
              model = "deepseek-v4"
            """);

        CcConnectConfig sample = SampleConfig() with
        {
            Projects = new[]
            {
                SampleConfig().Projects[0] with
                {
                    Agent = "codex",
                    ProviderRefs = new[] { "chatpt-monthly" },
                },
            },
        };

        CcConnectConfigGenerator.Write(path, sample);
        string result = File.ReadAllText(path);

        Assert.Contains("name = \"chatpt-monthly\"", result, StringComparison.Ordinal);
        Assert.Contains("[[providers.agent_model_lists.codex]]", result, StringComparison.Ordinal);
        Assert.Contains("model = \"gpt-5.6\"", result, StringComparison.Ordinal);
        Assert.DoesNotContain("model = \"gpt-5.6-sol\"", result, StringComparison.Ordinal);
        Assert.DoesNotContain("model = \"gpt-5.6-terra\"", result, StringComparison.Ordinal);
        Assert.DoesNotContain("model = \"gpt-5.6-luna\"", result, StringComparison.Ordinal);
        Assert.Contains("alias = \"[AI Resume] GPT-5.6（当前默认）\"", result, StringComparison.Ordinal);
        Assert.Equal(1, CountOccurrences(result, "[[providers.agent_model_lists.codex]]"));
    }

    [Fact]
    public void 当前Codex模型列表连续生成保持四项且字节幂等()
    {
        string path = Path.Combine(NewDir(), "config.toml");
        File.WriteAllText(path, """
            [[providers]]
              name = "router"
              model = "gpt-5.6"
            """);
        CcConnectConfig sample = SampleConfig() with
        {
            Projects = new[] { SampleConfig().Projects[0] with { Agent = "codex", ProviderRefs = new[] { "router" } } },
        };

        CcConnectConfigGenerator.Write(path, sample);
        byte[] first = File.ReadAllBytes(path);
        CcConnectConfigGenerator.Write(path, sample);
        byte[] second = File.ReadAllBytes(path);

        Assert.Equal(first, second);
        Assert.Equal(4, CountOccurrences(File.ReadAllText(path), "[[providers.agent_model_lists.codex]]"));
    }

    [Fact]
    public void provider尾注释仍能为官方端点补当前Codex模型()
    {
        string path = Path.Combine(NewDir(), "config.toml");
        File.WriteAllText(path, """
            [[providers]] # official OpenAI
              name = "openai"
              base_url = "https://api.openai.com/v1"
              model = "gpt-5.6"
            """);
        CcConnectConfig sample = SampleConfig() with
        {
            Projects = new[]
            {
                SampleConfig().Projects[0] with { Agent = "codex", ProviderRefs = new[] { "openai" } },
            },
        };

        CcConnectConfigGenerator.Write(path, sample);
        string result = File.ReadAllText(path);

        Assert.Equal(4, CountOccurrences(result, "[[providers.agent_model_lists.codex]]"));
        Assert.Contains("alias = \"[AI Resume] GPT-5.6 Sol（旗舰）\"", result, StringComparison.Ordinal);
    }

    [Fact]
    public void 无所有权标记的用户单项列表原样保留()
    {
        string path = Path.Combine(NewDir(), "config.toml");
        File.WriteAllText(path, """
            [[providers]]
              name = "router"
              model = "gpt-5.6"

              [[providers.agent_model_lists.codex]]
                model = "gpt-5.6"
            """);
        CcConnectConfig sample = SampleConfig() with
        {
            Projects = new[] { SampleConfig().Projects[0] with { Agent = "codex", ProviderRefs = new[] { "router" } } },
        };

        CcConnectConfigGenerator.Write(path, sample);
        string result = File.ReadAllText(path);

        Assert.Equal(1, CountOccurrences(result, "[[providers.agent_model_lists.codex]]"));
        Assert.DoesNotContain("[AI Resume]", result, StringComparison.Ordinal);
        Assert.DoesNotContain("model = \"gpt-5.6-sol\"", result, StringComparison.Ordinal);
    }

    [Fact]
    public void 上游CRUD重编码剥掉注释后仍按语义alias识别并刷新目录()
    {
        string path = Path.Combine(NewDir(), "config.toml");
        File.WriteAllText(path, """
            [[providers]]
              name = "openai"
              base_url = "https://api.openai.com/v1"
              model = "gpt-5.6"
            """);
        CcConnectConfig sample = SampleConfig() with
        {
            Projects = new[]
            {
                SampleConfig().Projects[0] with { Agent = "codex", ProviderRefs = new[] { "openai" } },
            },
        };
        CcConnectConfigGenerator.Write(path, sample);

        TomlTable reencoded = TomlSerializer.Deserialize<TomlTable>(File.ReadAllText(path))!;
        TomlTable provider = Assert.IsType<TomlTable>(
            Assert.IsType<TomlTableArray>(reencoded["providers"])[0]);
        provider["model"] = "gpt-5.6-luna";
        File.WriteAllText(path, TomlSerializer.Serialize(reencoded));

        CcConnectConfigGenerator.Write(path, sample);
        string result = File.ReadAllText(path);

        Assert.Equal(3, CountOccurrences(result, "[[providers.agent_model_lists.codex]]"));
        Assert.DoesNotContain("alias = \"[AI Resume] GPT-5.6（当前默认）\"", result, StringComparison.Ordinal);
        Assert.Contains("alias = \"[AI Resume] GPT-5.6 Luna（经济高吞吐）\"", result, StringComparison.Ordinal);
    }

    [Fact]
    public void agentModels覆盖值用于补当前agent菜单且其它agent列表不阻塞()
    {
        string path = Path.Combine(NewDir(), "config.toml");
        File.WriteAllText(path, """
            [[providers]]
              name = "router"
              model = "claude-default"
              agent_models = { codex = "gpt-custom" }

              [[providers.agent_model_lists.claudecode]]
                model = "sonnet"
            """);
        CcConnectConfig sample = SampleConfig() with
        {
            Projects = new[]
            {
                SampleConfig().Projects[0] with
                {
                    Agent = "codex",
                    ProviderRefs = new[] { "router" },
                },
            },
        };

        CcConnectConfigGenerator.Write(path, sample);
        string result = File.ReadAllText(path);

        Assert.Contains("[[providers.agent_model_lists.codex]]", result, StringComparison.Ordinal);
        Assert.Contains("model = \"gpt-custom\"", result, StringComparison.Ordinal);
        Assert.Contains("[[providers.agent_model_lists.claudecode]]", result, StringComparison.Ordinal);
    }

    [Fact]
    public void 用户自定义当前agent模型列表原样保留且不追加生成块()
    {
        string path = Path.Combine(NewDir(), "config.toml");
        File.WriteAllText(path, """
            [[providers]]
              name = "router"
              model = "gpt-default"

              [[providers.agent_model_lists.codex]]
                model = "gpt-user"
                alias = "User Choice"
            """);
        CcConnectConfig sample = SampleConfig() with
        {
            Projects = new[] { SampleConfig().Projects[0] with { Agent = "codex", ProviderRefs = new[] { "router" } } },
        };

        CcConnectConfigGenerator.Write(path, sample);
        string result = File.ReadAllText(path);

        Assert.Contains("alias = \"User Choice\"", result, StringComparison.Ordinal);
        Assert.DoesNotContain("Generated by AI Resume from the provider", result, StringComparison.Ordinal);
        Assert.Equal(1, CountOccurrences(result, "[[providers.agent_model_lists.codex]]"));
    }

    [Fact]
    public void AIResume生成的模型列表会随有效默认模型刷新()
    {
        string path = Path.Combine(NewDir(), "config.toml");
        File.WriteAllText(path, """
            [[providers]]
              name = "router"
              model = "gpt-old"
            """);
        CcConnectConfig sample = SampleConfig() with
        {
            Projects = new[] { SampleConfig().Projects[0] with { Agent = "codex", ProviderRefs = new[] { "router" } } },
        };
        CcConnectConfigGenerator.Write(path, sample);

        string first = File.ReadAllText(path);
        int scalar = first.IndexOf("model = \"gpt-old\"", StringComparison.Ordinal);
        Assert.True(scalar >= 0);
        string changed = first.Remove(scalar, "model = \"gpt-old\"".Length)
            .Insert(scalar, "model = \"gpt-new\"");
        File.WriteAllText(path, changed);

        CcConnectConfigGenerator.Write(path, sample);
        string result = File.ReadAllText(path);

        Assert.Contains("model = \"gpt-new\"", result, StringComparison.Ordinal);
        Assert.DoesNotContain("model = \"gpt-old\"", result, StringComparison.Ordinal);
        Assert.Equal(1, CountOccurrences(result, "[[providers.agent_model_lists.codex]]"));
    }

    [Fact]
    public void 项目级字段agentOptions与inlineProvider按原层级保留()
    {
        string path = Path.Combine(NewDir(), "config.toml");
        File.WriteAllText(path, """
            [[projects]]
            name = 'ai-resume'
            disabled_commands = ["restart"]

            [projects.agent]
            type = "codex"
            custom_agent_flag = true

            [projects.agent.options]
            mode = "default"
            work_dir = "C:\\old"
            provider = "router"
            model = "gpt-user"
            custom_option = "keep"

            [[projects.agent.providers]]
            name = "inline-local"
            base_url = "https://inline.example/v1"

            [projects.display]
            mode = "quiet"

            [[projects.platforms]]
            type = "feishu"
            [projects.platforms.options]
            app_id = "old"
            app_secret = "old"
            allow_from = "old"
            """);

        CcConnectConfig config = SampleConfig() with
        {
            Projects = new[]
            {
                SampleConfig().Projects[0] with
                {
                    Name = "ai-resume",
                    Agent = "codex",
                    WorkDir = @"C:\new",
                },
            },
        };
        CcConnectConfigGenerator.Write(path, config, preserveAgentSelection: true);

        TomlTable root = TomlSerializer.Deserialize<TomlTable>(File.ReadAllText(path))!;
        TomlTable project = Assert.IsType<TomlTable>(Assert.IsType<TomlTableArray>(root["projects"])[0]);
        TomlTable agent = Assert.IsType<TomlTable>(project["agent"]);
        TomlTable options = Assert.IsType<TomlTable>(agent["options"]);

        Assert.True(project.ContainsKey("disabled_commands"));
        Assert.False(options.ContainsKey("disabled_commands"));
        Assert.Equal(true, agent["custom_agent_flag"]);
        Assert.Equal("router", options["provider"]);
        Assert.Equal("gpt-user", options["model"]);
        Assert.Equal("keep", options["custom_option"]);
        Assert.Single(Assert.IsType<TomlTableArray>(agent["providers"]));
        Assert.Equal("quiet", Assert.IsType<TomlTable>(project["display"])["mode"]);
    }

    [Fact]
    public void 唯一兼容provider选择写入agentOptions而不是项目顶层()
    {
        CcConnectConfig config = SampleConfig() with
        {
            Projects = new[]
            {
                SampleConfig().Projects[0] with
                {
                    Agent = "codex",
                    SelectedProvider = "router",
                    SelectedModel = "gpt-5.6",
                },
            },
        };

        string result = CcConnectConfigGenerator.Render(config);
        int options = result.IndexOf("[projects.agent.options]", StringComparison.Ordinal);
        int platform = result.IndexOf("[[projects.platforms]]", StringComparison.Ordinal);
        string optionBlock = result[options..platform];

        Assert.Contains("provider = \"router\"", optionBlock, StringComparison.Ordinal);
        Assert.Contains("model = \"gpt-5.6\"", optionBlock, StringComparison.Ordinal);
        Assert.DoesNotContain("provider = \"router\"", result[..options], StringComparison.Ordinal);
    }

    [Fact]
    public void 连续生成字节幂等且内联模型列表不被扩展成非法TOML()
    {
        string path = Path.Combine(NewDir(), "config.toml");
        File.WriteAllText(path, """
            [[providers]]
            name = "router"
            model = "gpt-default"
            agent_model_lists = { codex = [{ model = "gpt-user" }] }
            """);
        CcConnectConfig config = SampleConfig() with
        {
            Projects = new[] { SampleConfig().Projects[0] with { Agent = "codex", ProviderRefs = new[] { "router" } } },
        };

        CcConnectConfigGenerator.Write(path, config);
        byte[] first = File.ReadAllBytes(path);
        CcConnectConfigGenerator.Write(path, config);
        byte[] second = File.ReadAllBytes(path);

        Assert.Equal(first, second);
        Assert.Equal(1, CountOccurrences(File.ReadAllText(path), "agent_model_lists"));
        Assert.Single(CcConnectProviderCatalog.Parse(File.ReadAllText(path)).Providers[0].EffectiveModels("codex"));
    }

    [Fact]
    public void 引号式内联模型列表同样封闭且不会追加Codex子表()
    {
        string path = Path.Combine(NewDir(), "config.toml");
        File.WriteAllText(path, """
            [[providers]]
            name = "router"
            model = "gpt-default"
            "agent_model_lists" = { claudecode = [{ model = "sonnet" }] }
            """);
        CcConnectConfig config = SampleConfig() with
        {
            Projects = new[] { SampleConfig().Projects[0] with { Agent = "codex", ProviderRefs = new[] { "router" } } },
        };

        CcConnectConfigGenerator.Write(path, config);

        string result = File.ReadAllText(path);
        Assert.Equal(1, CountOccurrences(result, "agent_model_lists"));
        Assert.DoesNotContain("[[providers.agent_model_lists.codex]]", result, StringComparison.Ordinal);
        Assert.Empty(CcConnectProviderCatalog.Parse(result).Providers[0].EffectiveModels("codex"));
    }

    [Theory]
    [InlineData("models = []")]
    [InlineData("agent_model_lists = { codex = [] }")]
    public void 用户显式空模型列表不被自动补全(string explicitList)
    {
        string path = Path.Combine(NewDir(), "config.toml");
        File.WriteAllText(path, $$"""
            [[providers]]
            name = "router"
            model = "gpt-default"
            {{explicitList}}
            """);
        CcConnectConfig config = SampleConfig() with
        {
            Projects = new[]
            {
                SampleConfig().Projects[0] with
                {
                    Agent = "codex",
                    ProviderRefs = new[] { "router" },
                },
            },
        };

        CcConnectConfigGenerator.Write(path, config);

        string result = File.ReadAllText(path);
        Assert.DoesNotContain("# AI Resume generated model list: begin", result, StringComparison.Ordinal);
    }

    [Fact]
    public void 切换agent时存在内联provider必须失败关闭而不是静默带入()
    {
        string path = Path.Combine(NewDir(), "config.toml");
        File.WriteAllText(path, """
            [[projects]]
            name = "ai-resume"
            [projects.agent]
            type = "claudecode"

            [[projects.agent.providers]]
            name = "deepseek-inline"
            base_url = "https://api.example/anthropic"
            """);
        CcConnectConfig config = SampleConfig() with
        {
            Projects = new[]
            {
                SampleConfig().Projects[0] with { Name = "ai-resume", Agent = "codex" },
            },
        };

        InvalidDataException error = Assert.Throws<InvalidDataException>(() =>
            CcConnectConfigGenerator.Write(
                path,
                config,
                preserveAgentSelection: false,
                preserveInlineAgentProviders: false));

        Assert.Contains("内联 provider", error.Message, StringComparison.Ordinal);
        Assert.Contains("claudecode", File.ReadAllText(path), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("[[projects.agent.\"providers\"]]")]
    [InlineData("[[projects.\"agent\".providers]]")]
    public void 切换agent时合法引号式内联provider同样失败关闭(string providerHeader)
    {
        string path = Path.Combine(NewDir(), "config.toml");
        File.WriteAllText(path, $$"""
            [[projects]]
            name = "ai-resume"
            [projects.agent]
            type = "claudecode"

            {{providerHeader}}
            name = "deepseek-inline"
            base_url = "https://api.example/anthropic"
            """);
        CcConnectConfig config = SampleConfig() with
        {
            Projects = new[]
            {
                SampleConfig().Projects[0] with { Name = "ai-resume", Agent = "codex" },
            },
        };

        InvalidDataException error = Assert.Throws<InvalidDataException>(() =>
            CcConnectConfigGenerator.Write(
                path,
                config,
                preserveAgentSelection: false,
                preserveInlineAgentProviders: false));

        Assert.Contains("内联 provider", error.Message, StringComparison.Ordinal);
        Assert.Contains("claudecode", File.ReadAllText(path), StringComparison.Ordinal);
    }

    [Fact]
    public void 截断的生成标记不删除后续用户配置()
    {
        string path = Path.Combine(NewDir(), "config.toml");
        File.WriteAllText(path, """
            [[providers]]
            name = "router"
            model = "gpt-default"
            # AI Resume generated model list: begin

            [management]
            enabled = true
            port = 9820
            token = "keep-me"
            """);
        CcConnectConfig config = SampleConfig() with
        {
            Projects = new[] { SampleConfig().Projects[0] with { Agent = "codex", ProviderRefs = new[] { "router" } } },
        };

        CcConnectConfigGenerator.Write(path, config);

        string result = File.ReadAllText(path);
        Assert.Contains("token = \"keep-me\"", result, StringComparison.Ordinal);
        Assert.Contains("# AI Resume generated model list: begin", result, StringComparison.Ordinal);
    }

    [Fact]
    public void 生成配置把内部运行标记合并进已有嵌套env且保留用户变量()
    {
        string path = Path.Combine(NewDir(), "config.toml");
        File.WriteAllText(path, """
            [[projects]]
            name = "pilot"
            [projects.agent]
            type = "claudecode"
            [projects.agent.options]
            mode = "default"
            work_dir = "C:\\old"
            [projects.agent.options.env]
            AI_RESUME_INTERNAL_RUN = "wrong"
            CUSTOM_FLAG = "keep"
            "KEY-WITH-DASH" = "also-keep"
            """);

        CcConnectConfigGenerator.Write(path, SampleConfig());

        string result = File.ReadAllText(path);
        Assert.Equal(1, CountOccurrences(result, "[projects.agent.options.env]"));
        Assert.Equal(1, CountOccurrences(result, "AI_RESUME_INTERNAL_RUN = \"1\""));
        Assert.Contains("\"CUSTOM_FLAG\" = \"keep\"", result, StringComparison.Ordinal);
        Assert.Contains("\"KEY-WITH-DASH\" = \"also-keep\"", result, StringComparison.Ordinal);
        Assert.DoesNotContain("AI_RESUME_INTERNAL_RUN = \"wrong\"", result, StringComparison.Ordinal);
        Assert.NotNull(TomlSerializer.Deserialize<TomlTable>(result));
    }

    [Fact]
    public void 生成配置把内联env规范为单一表且不丢用户变量()
    {
        string path = Path.Combine(NewDir(), "config.toml");
        File.WriteAllText(path, """
            [[projects]]
            name = "pilot"
            [projects.agent]
            type = "codex"
            [projects.agent.options]
            mode = "default"
            work_dir = "C:\\old"
            env = { CUSTOM_FLAG = "keep", AI_RESUME_INTERNAL_RUN = "0" }
            """);

        CcConnectConfigGenerator.Write(path, SampleConfig());

        string result = File.ReadAllText(path);
        Assert.DoesNotContain("env = {", result, StringComparison.Ordinal);
        Assert.Equal(1, CountOccurrences(result, "[projects.agent.options.env]"));
        Assert.Contains("AI_RESUME_INTERNAL_RUN = \"1\"", result, StringComparison.Ordinal);
        Assert.Contains("\"CUSTOM_FLAG\" = \"keep\"", result, StringComparison.Ordinal);
        Assert.NotNull(TomlSerializer.Deserialize<TomlTable>(result));
    }
}
