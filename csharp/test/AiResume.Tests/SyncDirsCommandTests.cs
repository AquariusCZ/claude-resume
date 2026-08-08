using AiResume.Worker.Migration;
using Xunit;

namespace AiResume.Tests;

/// <summary>
/// S10-P sync-dirs 纯函数回归测试。
///
/// 只测 BuildDirList 与 MergeJson 两个纯函数,绝不调用 Run(它会读真实用户环境的
/// shadow 配置并扫真实磁盘)。directoryExists 一律注入假实现,不触碰真实文件系统。
/// </summary>
public sealed class SyncDirsCommandTests
{
    // ---- BuildDirList 用例 ----

    /// <summary>
    /// 当前目录在候选里时被移到第 0 位,其余保持相对顺序。
    /// </summary>
    [Fact]
    public void 当前目录被置顶()
    {
        var result = SyncDirsCommand.BuildDirList(
            new[] { @"C:\A", @"C:\B", @"C:\C" },
            null,
            @"C:\C",
            _ => true);

        Assert.Equal(new[] { @"C:\C", @"C:\A", @"C:\B" }, result);
    }

    /// <summary>
    /// 当前目录不在候选里但存在且未隐藏时,插入到最前。
    /// </summary>
    [Fact]
    public void 当前目录不在候选里时被插到最前()
    {
        var result = SyncDirsCommand.BuildDirList(
            new[] { @"C:\A", @"C:\B" },
            null,
            @"C:\Z",
            _ => true);

        Assert.Equal(new[] { @"C:\Z", @"C:\A", @"C:\B" }, result);
    }

    /// <summary>
    /// 当前目录不存在时既不置顶也不插入,照常忽略。
    /// </summary>
    [Fact]
    public void 当前目录不存在时既不置顶也不插入()
    {
        var result = SyncDirsCommand.BuildDirList(
            new[] { @"C:\A", @"C:\B" },
            null,
            @"C:\Z",
            path => path != @"C:\Z");

        Assert.Equal(new[] { @"C:\A", @"C:\B" }, result);
    }

    /// <summary>
    /// 当前目录被隐藏时不插入,结果不含它。
    /// </summary>
    [Fact]
    public void 当前目录被隐藏时不插入()
    {
        var result = SyncDirsCommand.BuildDirList(
            new[] { @"C:\A", @"C:\B" },
            new[] { @"C:\Z" },
            @"C:\Z",
            _ => true);

        Assert.Equal(new[] { @"C:\A", @"C:\B" }, result);
    }

    /// <summary>
    /// 隐藏项被排除,且比较忽略大小写与尾部反斜杠。
    /// 现场路径大小写不一致,实测同一目录既出现过 C:\Users\... 也出现过 c:\Users\...。
    /// </summary>
    [Fact]
    public void 隐藏项被排除且忽略大小写与尾部反斜杠()
    {
        var result = SyncDirsCommand.BuildDirList(
            new[] { @"C:\X\", @"C:\Y" },
            new[] { @"c:\x" },
            null,
            _ => true);

        Assert.Equal(new[] { @"C:\Y" }, result);
    }

    /// <summary>
    /// 不存在的目录被排除,directoryExists 只对部分路径返回 true。
    /// </summary>
    [Fact]
    public void 不存在的目录被排除()
    {
        var result = SyncDirsCommand.BuildDirList(
            new[] { @"C:\A", @"C:\B", @"C:\C" },
            null,
            null,
            path => path != @"C:\B");

        Assert.Equal(new[] { @"C:\A", @"C:\C" }, result);
    }

    /// <summary>
    /// 去重保留首次出现的原始写法,忽略大小写与尾部反斜杠。
    /// </summary>
    [Fact]
    public void 去重保留首次出现的原始写法()
    {
        var result = SyncDirsCommand.BuildDirList(
            new[] { @"C:\A", @"c:\a\" },
            null,
            null,
            _ => true);

        Assert.Equal(new[] { @"C:\A" }, result);
    }

    /// <summary>
    /// 无 current 时保持候选的相对顺序,不排序。
    /// </summary>
    [Fact]
    public void 保持候选的相对顺序()
    {
        var result = SyncDirsCommand.BuildDirList(
            new[] { @"C:\C", @"C:\A", @"C:\B" },
            null,
            null,
            _ => true);

        Assert.Equal(new[] { @"C:\C", @"C:\A", @"C:\B" }, result);
    }

    /// <summary>
    /// 空白项被丢弃,包括空串与纯空白串。
    /// </summary>
    [Fact]
    public void 空白项被丢弃()
    {
        var result = SyncDirsCommand.BuildDirList(
            new[] { "", "   ", @"C:\A" },
            null,
            null,
            _ => true);

        Assert.Equal(new[] { @"C:\A" }, result);
    }

    /// <summary>
    /// 四个入参全 null 不抛异常,返回空集合。
    /// </summary>
    [Fact]
    public void 入参为null不抛异常()
    {
        var result = SyncDirsCommand.BuildDirList(null, null, null, null);

        Assert.Empty(result);
    }

    /// <summary>
    /// directoryExists 为 null 时视同全部存在,候选原样保留。
    /// </summary>
    [Fact]
    public void directoryExists为null时视同全部存在()
    {
        var result = SyncDirsCommand.BuildDirList(
            new[] { @"C:\A", @"C:\B" },
            null,
            null,
            null);

        Assert.Equal(new[] { @"C:\A", @"C:\B" }, result);
    }

    // ---- MergeJson 用例 ----

    /// <summary>
    /// 核心回归:其它项目的条目必须原样保留,只替换本项目键。
    /// 这条钉住"不许整份覆盖"——本项目已经因为整份重写把 [management] 抹掉出过事故。
    /// </summary>
    [Fact]
    public void 其它项目的条目被原样保留()
    {
        // fixture 里的反斜杠必须按 **JSON** 规则转义成 `\\`。
        // 原写法是 `C:\O` —— `\O` 在 JSON 里是非法转义,解析直接抛,
        // MergeJson 回落成空对象,于是 other 被丢,看起来像实现有 bug,其实是 fixture 坏了。
        const string existing = @"{""other"":[""C:\\O""],""ai-resume"":[""C:\\Old""]}";

        string result = SyncDirsCommand.MergeJson(existing, "ai-resume", new[] { @"C:\New" });

        // 断言解析后的**值**,不断言序列化文本:后者要和转义规则较劲,一改格式就假红假绿。
        Dictionary<string, List<string>> parsed = Parse(result);

        Assert.Equal(new[] { @"C:\O" }, parsed["other"]);
        Assert.Equal(new[] { @"C:\New" }, parsed["ai-resume"]);
    }

    private static Dictionary<string, List<string>> Parse(string json)
        => System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, List<string>>>(json)!;

    /// <summary>
    /// 既有为空或无法解析时当空对象处理,三次都应产出只含本项目键的合法 JSON,且不抛异常。
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("{ 坏 JSON")]
    public void 既有为空或无法解析时当空对象处理(string? existing)
    {
        string result = SyncDirsCommand.MergeJson(existing, "ai-resume", new[] { @"C:\A" });

        Dictionary<string, List<string>> parsed = Parse(result);

        Assert.Equal(new[] { @"C:\A" }, parsed["ai-resume"]);
        Assert.Single(parsed);
    }

    /// <summary>
    /// 空目录列表写成空数组而不是删键,也不是 null。
    /// </summary>
    [Fact]
    public void 空目录列表写成空数组而不是删键()
    {
        string result = SyncDirsCommand.MergeJson(null, "ai-resume", Array.Empty<string>());

        Assert.Contains(@"""ai-resume"": []", result, StringComparison.Ordinal);
    }

    /// <summary>
    /// 中文路径不被转义成 unicode 转义序列——项目路径里有中文目录名
    /// (如 调研报告与PPT),转成 \uXXXX 虽然合法但人不可读。
    /// </summary>
    [Fact]
    public void 中文路径不被转义成unicode转义序列()
    {
        string result = SyncDirsCommand.MergeJson(null, "ai-resume", new[] { @"C:\调研报告\项目" });

        Assert.Contains("调研报告", result, StringComparison.Ordinal);
        Assert.DoesNotContain(@"\u8c03\u7814", result, StringComparison.Ordinal);
    }
}
