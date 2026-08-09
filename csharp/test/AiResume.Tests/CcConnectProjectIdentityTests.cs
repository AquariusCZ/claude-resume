using AiResume.Core;
using AiResume.Worker.Migration;
using AiResume.Worker.Products;
using AiResume.Wrapper;
using Xunit;

namespace AiResume.Tests;

/// <summary>
/// S10-N cc-connect 项目身份固定化 + work_dir 确定性解析测试。
///
/// 背景(2026-08-06 生产现场):项目名曾从 claude-resume-migration 漂成 _smoke-cutover,
/// 导致 provider list --project 报 project not found;shadow 配置的 selected 数组实际为空,
/// work_dir 回落到 ProjectCatalog.Discover() 的第一项,而 Discover() 按目录 mtime 排序,
/// 于是"哪个目录最后被碰过,机器人默认就进哪个目录"。
///
/// 本测试只测两个纯函数:ResolveWorkDir 与 TryReadExistingWorkDir。
/// **绝不调用 Generate**——它会读真实用户环境的 DPAPI 凭据。
/// </summary>
public sealed class CcConnectProjectIdentityTests
{
    // ---- provider_refs 按 agent 类型过滤 ----
    //
    // chatpt-月付 声明 agent_types = ["codex"],被 claudecode 项目引用时
    // 在飞书里表现为 /provider switch 列得出来却切不过去。

    private const string ProvidersToml = """
        [[providers]]
          name = "chatpt-月付"
          agent_types = ["codex"]

        [[providers]]
          name = "deepseek"
          base_url = "https://api.deepseek.com/anthropic"

        [[providers]]
          name = "both"
          agent_types = ["codex", "claudecode"]
        """;

    [Fact]
    public void 只引用适用于本agent的全局服务商()
    {
        IReadOnlyList<string> refs = CcConnectConfigGenerator.ReadGlobalProviderNames(
            ProvidersToml, "claudecode");

        // deepseek 是 Anthropic 端点,只适用于 claudecode;both 显式包含 claudecode。
        Assert.Equal(new[] { "deepseek", "both" }, refs);
    }

    [Fact]
    public void 换成codex时codex专属服务商回到列表里()
    {
        IReadOnlyList<string> refs = CcConnectConfigGenerator.ReadGlobalProviderNames(
            ProvidersToml, "codex");

        Assert.Equal(new[] { "chatpt-月付", "both" }, refs);
    }

    [Fact]
    public void 不传agent类型时不过滤保持旧行为()
    {
        IReadOnlyList<string> refs = CcConnectConfigGenerator.ReadGlobalProviderNames(ProvidersToml);

        Assert.Equal(new[] { "chatpt-月付", "deepseek", "both" }, refs);
    }

    [Fact]
    public void anthropic默认端点有codex覆盖时仍可给codex引用()
    {
        const string toml = """
            [[providers]]
              name = "router"
              base_url = "https://router.example/anthropic"

              [providers.endpoints]
                codex = "https://router.example/v1"
            """;

        IReadOnlyList<string> refs = CcConnectConfigGenerator.ReadGlobalProviderNames(toml, "codex");

        Assert.Equal(new[] { "router" }, refs);
    }

    [Fact]
    public void 默认agent类型是claudecode且在白名单内()
    {
        // agent 类型现在由用户在控制面选(S10-R),但默认值必须仍是 claudecode——
        // 它决定了 provider_refs 的过滤依据,漂开就等于过滤失效。
        Assert.Equal("claudecode", CutoverConfigCommand.DefaultAgentType);
        Assert.Contains(CcConnectAgents.Supported, a => a.Id == CutoverConfigCommand.DefaultAgentType);
    }

    [Fact]
    public void 已安装探测对本机真实存在的CLI为真()
    {
        // claude 是本项目运行的前提(续跑靠它),不装它整个产品没有意义。
        // 这条同时钉住 PATH 解析本身能工作——探测恒 false 会把全部选项灰掉。
        Assert.True(CcConnectAgents.IsInstalled("claudecode"));
    }

    [Fact]
    public void 搜索路径里没有时探测为假()
    {
        // 用空目录当搜索路径,而不是挑一个"本机大概没装"的 agent ——
        // 后者在用户哪天真装了它之后会假红。
        string empty = TestTemp.NewDir("airesume-nopath");
        Directory.CreateDirectory(empty);
        try
        {
            foreach ((string id, _) in CcConnectAgents.Supported)
            {
                Assert.False(CcConnectAgents.IsInstalled(id, empty), $"{id} 在空路径下不应判为已安装");
            }
        }
        finally
        {
            try { Directory.Delete(empty); } catch (IOException) { }
        }
    }

    [Fact]
    public void 搜索路径里有同名可执行文件时探测为真()
    {
        // 反向:放一个假的 claude.exe 进去,探测必须认出来。
        // 只测"能不能解析",不测它是不是真的 Claude Code——那不是这层的职责。
        string dir = TestTemp.NewDir("airesume-path");
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "claude.exe"), string.Empty);
            Assert.True(CcConnectAgents.IsInstalled("claudecode", dir));
            Assert.False(CcConnectAgents.IsInstalled("codex", dir));
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
        }
    }

    [Theory]
    [InlineData(null, "claudecode")]
    [InlineData("", "claudecode")]
    [InlineData("  ", "claudecode")]
    [InlineData("不存在的agent", "claudecode")]
    [InlineData("CODEX", "codex")]
    [InlineData("codex", "codex")]
    public void 非法agent一律回落到默认值(string? input, string expected)
    {
        // 白名单外的值写进 config.toml 会让 cc-connect **启动失败**——
        // 那时飞书机器人整个失联,比回落到默认严重得多。
        Assert.Equal(expected, CcConnectAgents.Normalize(input));
    }

    // ---- ResolveWorkDir 用例 ----

    [Fact]
    public void 显式指定的工作目录优先级最高()
    {
        var selected = new List<ProjectRef> { new() { Name = "sel", Path = @"C:\sel" } };
        var discovered = new List<ProjectEntry> { new("disc", @"C:\disc", DateTimeOffset.MinValue) };

        string? result = CutoverConfigCommand.ResolveWorkDir(
            @"C:\explicit",
            @"C:\existing",
            selected,
            discovered,
            _ => true);

        Assert.Equal(@"C:\explicit", result);
    }

    [Fact]
    public void 显式目录不存在也照样返回()
    {
        string? result = CutoverConfigCommand.ResolveWorkDir(
            @"C:\explicit",
            @"C:\existing",
            new List<ProjectRef> { new() { Name = "sel", Path = @"C:\sel" } },
            new List<ProjectEntry> { new("disc", @"C:\disc", DateTimeOffset.MinValue) },
            _ => false);

        Assert.Equal(@"C:\explicit", result);
    }

    [Fact]
    public void 既有配置目录仍存在时被保留()
    {
        string? result = CutoverConfigCommand.ResolveWorkDir(
            null,
            @"C:\existing",
            new List<ProjectRef> { new() { Name = "sel", Path = @"C:\sel" } },
            new List<ProjectEntry> { new("disc", @"C:\disc", DateTimeOffset.MinValue) },
            _ => true);

        Assert.Equal(@"C:\existing", result);
    }

    [Fact]
    public void 既有配置目录已被删除时继续往下找()
    {
        string? result = CutoverConfigCommand.ResolveWorkDir(
            null,
            @"C:\existing",
            new List<ProjectRef> { new() { Name = "sel", Path = @"C:\sel" } },
            new List<ProjectEntry> { new("disc", @"C:\disc", DateTimeOffset.MinValue) },
            path => path != @"C:\existing");

        Assert.Equal(@"C:\sel", result);
    }

    [Fact]
    public void 既有配置为空时用已布防项目()
    {
        string? result = CutoverConfigCommand.ResolveWorkDir(
            null,
            "   ",
            new List<ProjectRef>
            {
                new() { Name = "a", Path = @"C:\a" },
                new() { Name = "b", Path = @"C:\b" },
            },
            new List<ProjectEntry> { new("disc", @"C:\disc", DateTimeOffset.MinValue) },
            _ => true);

        Assert.Equal(@"C:\a", result);
    }

    [Fact]
    public void 已布防项目里跳过路径为空的条目()
    {
        string? result = CutoverConfigCommand.ResolveWorkDir(
            null,
            null,
            new List<ProjectRef>
            {
                new() { Name = "a", Path = "" },
                new() { Name = "b", Path = @"C:\b" },
            },
            new List<ProjectEntry> { new("disc", @"C:\disc", DateTimeOffset.MinValue) },
            _ => true);

        Assert.Equal(@"C:\b", result);
    }

    [Fact]
    public void 回落到发现结果时按名字排序而不是入参顺序()
    {
        // 模拟 mtime 序:zeta 在前、alpha 在后。期望按名字 Ordinal 升序取 alpha。
        var discovered = new List<ProjectEntry>
        {
            new("zeta", @"C:\z", DateTimeOffset.MinValue),
            new("alpha", @"C:\a", DateTimeOffset.MinValue),
        };

        string? result = CutoverConfigCommand.ResolveWorkDir(
            null,
            null,
            new List<ProjectRef>(),
            discovered,
            _ => true);

        Assert.Equal(@"C:\a", result);
    }

    [Fact]
    public void 全部候选落空时返回null()
    {
        string? result = CutoverConfigCommand.ResolveWorkDir(
            null,
            null,
            new List<ProjectRef>(),
            new List<ProjectEntry>(),
            _ => true);

        Assert.Null(result);
    }

    [Fact]
    public void 入参为null不抛异常()
    {
        string? result = CutoverConfigCommand.ResolveWorkDir(null, null, null, null, null);

        Assert.Null(result);
    }

    [Fact]
    public void 项目名是固定常量()
    {
        Assert.Equal("ai-resume", CutoverConfigCommand.ProjectName);
    }

    // ---- TryReadExistingWorkDir 用例 ----

    [Fact]
    public void 能从嵌套表里读回工作目录并还原反斜杠()
    {
        const string toml = """
            [projects.agent.options]
            mode = "default"
            work_dir = "C:\\Users\\me\\proj"
            """;

        string? result = CcConnectConfigGenerator.TryReadExistingWorkDir(toml);

        Assert.Equal(@"C:\Users\me\proj", result);
    }

    [Fact]
    public void 不读别的表里的同名键()
    {
        const string toml = """
            [projects.agent.options]
            mode = "default"

            [[projects.platforms]]
            type = "feishu"
            work_dir = "C:\\wrong"
            """;

        string? result = CcConnectConfigGenerator.TryReadExistingWorkDir(toml);

        Assert.Null(result);
    }

    [Fact]
    public void 空串与空白值视同没有()
    {
        const string toml = """
            [projects.agent.options]
            work_dir = ""
            """;

        string? result = CcConnectConfigGenerator.TryReadExistingWorkDir(toml);

        Assert.Null(result);
    }

    [Fact]
    public void 输入为空时返回null()
    {
        Assert.Null(CcConnectConfigGenerator.TryReadExistingWorkDir(""));
        Assert.Null(CcConnectConfigGenerator.TryReadExistingWorkDir(null!));
    }
}
