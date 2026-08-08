using AiResume.Wrapper;
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
        string dir = Path.Combine(Path.GetTempPath(), "s10g-" + Guid.NewGuid().ToString("N"));
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
}