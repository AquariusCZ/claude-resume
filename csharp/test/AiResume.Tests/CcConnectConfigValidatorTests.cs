using AiResume.Wrapper;
using Xunit;

namespace AiResume.Tests;

/// <summary>
/// 「cc-connect 配置已生成」凭什么这么说(审计 A3)。
///
/// 原缺陷:依据只有 <c>File.Exists</c> 与"写入没抛异常"——两者都只证明我们做完了自己那步。
/// 配置被改坏之后界面照旧显示"配置已生成"。
///
/// **不联网、不起 cc-connect**:语法那一关注入假 runner,语义那一关是纯函数。
/// </summary>
public sealed class CcConnectConfigValidatorTests : IDisposable
{
    private readonly List<string> _files = new();

    public void Dispose()
    {
        foreach (string f in _files)
        {
            try
            {
                File.Delete(f);
            }
            catch (IOException)
            {
            }
        }
    }

    private const string GoodToml = """
        [[projects]]
          name = "ai-resume"

          [projects.agent]
            type = "claudecode"

            [projects.agent.options]
              mode = "acceptEdits"
              work_dir = "C:/work"

          [[projects.platforms]]
            type = "feishu"

            [projects.platforms.options]
              app_id = "cli_demo"
              app_secret = "s3cr3t"
              allow_from = "ou_demo"
        """;

    private string Write(string content)
    {
        string p = Path.Combine(Path.GetTempPath(), "airesume-cfgtest-" + Guid.NewGuid().ToString("N") + ".toml");
        File.WriteAllText(p, content);
        _files.Add(p);
        return p;
    }

    [Fact]
    public void 文件不在报missing而不是invalid()
    {
        var r = CcConnectConfigValidator.CheckFile(
            Path.Combine(Path.GetTempPath(), "airesume-not-here-" + Guid.NewGuid().ToString("N") + ".toml"));

        Assert.Equal(CcConnectConfigState.Missing, r.State);
    }

    [Fact]
    public void 解析器接受且语义完整才算ok()
    {
        var r = CcConnectConfigValidator.CheckFile(Write(GoodToml), _ => (0, "Formatted x"));

        Assert.Equal(CcConnectConfigState.Ok, r.State);
        Assert.Empty(r.Problems);
    }

    [Fact]
    public void 解析器拒绝时原样转出它的错误()
    {
        // cc-connect 的错误信息本身很准确(实测:toml: line 2: expected '.' or ']' …),
        // 转述会丢掉行号,而行号正是用户唯一能用的线索。
        var r = CcConnectConfigValidator.CheckFile(
            Write(GoodToml),
            _ => (1, "Error formatting config: invalid TOML: toml: line 2: expected '.' or ']' to end table name"));

        Assert.Equal(CcConnectConfigState.Invalid, r.State);
        Assert.Contains(r.Problems, p => p.Contains("line 2"));
    }

    [Fact]
    public void 校验的是副本原文件一个字节都不能动()
    {
        // config format 会重写文件,而这份配置里有用户手工维护的段落与注释
        // ([management]、[log]、扫码绑定的微信平台……)。
        string path = Write(GoodToml);
        string before = File.ReadAllText(path);
        string? seen = null;

        CcConnectConfigValidator.CheckFile(path, p => { seen = p; return (0, ""); });

        Assert.NotNull(seen);
        Assert.NotEqual(path, seen);
        Assert.Equal(before, File.ReadAllText(path));
        Assert.False(File.Exists(seen!));   // 副本用完即删,不留垃圾
    }

    [Fact]
    public void 语法过了但缺项目仍然算坏()
    {
        var r = CcConnectConfigValidator.CheckFile(
            Write("[log]\nlevel = \"info\"\n"), _ => (0, ""));

        Assert.Equal(CcConnectConfigState.Invalid, r.State);
        Assert.Contains(r.Problems, p => p.Contains("[[projects]]"));
    }

    [Fact]
    public void allow_from为空要说出后果()
    {
        var problems = CcConnectConfigValidator.CheckSemantics(GoodToml.Replace("\"ou_demo\"", "\"\""));

        // 这是安全问题,不是配置瑕疵。措辞必须让人立刻明白代价。
        Assert.Contains(problems, p => p.Contains("放行所有飞书用户"));
    }

    [Theory]
    [InlineData("app_id")]
    [InlineData("app_secret")]
    [InlineData("allow_from")]
    public void 缺键要逐条点名(string key)
    {
        string toml = string.Join("\n",
            GoodToml.Split('\n').Where(l => !l.TrimStart().StartsWith(key + " ")));

        Assert.Contains(CcConnectConfigValidator.CheckSemantics(toml), p => p.Contains(key));
    }

    [Fact]
    public void 完整配置的语义检查不该报任何问题()
    {
        // 误判比漏判更糟:告诉用户一份能用的配置坏了,他会去改一个没问题的东西。
        Assert.Empty(CcConnectConfigValidator.CheckSemantics(GoodToml));
    }

    [Fact]
    public void 找不到cc_connect时报unknown而不是ok()
    {
        // 「核对不了」不是「没问题」。
        var r = CcConnectConfigValidator.CheckFile(
            Write(GoodToml),
            _ => throw new FileNotFoundException("cc-connect not found"));

        Assert.Equal(CcConnectConfigState.Unknown, r.State);
    }

    [Fact]
    public void 取第一条有内容的错误行()
    {
        Assert.Equal(
            "Error formatting config: invalid TOML",
            CcConnectConfigValidator.FirstMeaningfulLine("\n\n  Error formatting config: invalid TOML\nmore\n"));
    }

    [Fact]
    public void 没有输出时也要给一句可读的话()
    {
        Assert.Contains("拒绝加载", CcConnectConfigValidator.FirstMeaningfulLine(""));
    }

    [Fact]
    public void 读键值区分不存在与空串()
    {
        Assert.Null(CcConnectConfigValidator.ReadFirstStringValue("a = \"1\"", "b"));
        Assert.Equal("", CcConnectConfigValidator.ReadFirstStringValue("  b = \"\"", "b"));
        Assert.Equal("v", CcConnectConfigValidator.ReadFirstStringValue("  b = \"v\"", "b"));
    }
}
