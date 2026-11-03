using System.Text.Json;
using AiResume.Worker.Products;
using Xunit;

namespace AiResume.Tests;

/// <summary>
/// 用户认得出的项目名(<see cref="ProjectNames"/>):控制面续跑队列与飞书通知共用的临时工作区判据、会话名读法与清洗。
/// 只用 <see cref="TestTemp"/> 下的合成目录,不碰真实 ~/.claude。
/// </summary>
public sealed class ProjectNamesTests
{
    private const string Session = "11111111-1111-1111-1111-111111111111";

    [Theory]
    // 桌面端 MSIX 版与旧版两处临时工作区根,正反斜杠、末尾分隔符都认。
    [InlineData(@"C:\Users\u\AppData\Local\Claude-Data\claude_x\scratch-workspaces\g1\g2\scratch-2026-10-28-f22194", true)]
    [InlineData(@"C:\Users\u\AppData\Roaming\Claude\scratch-workspaces\g1\g2\scratch-2026-09-16-93ed86", true)]
    [InlineData(@"C:/Users/u/AppData/Roaming/Claude/scratch-workspaces/g1/g2/scratch-2026-10-13-A8882E/", true)]
    [InlineData(@"C:\Users\u\AppData\Roaming\Claude\scratch-workspaces\g1\g2\scratch-2026-10-13-926795\", true)]
    // 两样都要:用户自建的同名目录、不在 scratch-workspaces 下的、编号不是十六进制的都不算。
    [InlineData(@"C:\work\scratch-workspaces\notes", false)]
    [InlineData(@"C:\Users\u\scratch-2026-10-29-03a48e", false)]
    [InlineData(@"C:\Users\u\scratch-workspaces-old\g\scratch-2026-10-29-03a48e", false)]
    [InlineData(@"C:\Users\u\AppData\Roaming\Claude\scratch-workspaces\g1\g2\scratch-2026-10-29-zzzzzz", false)]
    [InlineData(@"C:\Users\u\AppData\Roaming\Claude\scratch-workspaces\g1\g2\scratch-2026-10-29-03a", false)]
    [InlineData(@"C:\Users\u\AppData\Roaming\Claude\scratch-workspaces\g1\scratch-2026-10-29-03a48e\src", false)]
    [InlineData("", false)]
    [InlineData("   ", false)]
    [InlineData(null, false)]
    public void 临时工作区判据_末级形如scratch日期编号_上级又有scratch_workspaces(string? path, bool expected) =>
        Assert.Equal(expected, ProjectNames.IsScratchWorkspace(path));

    [Fact]
    public void 清洗会话名_换行与控制字符换成空格_连续空白并成一个()
    {
        Assert.Equal("第一行 第二行 第三行", ProjectNames.Clean("第一行\n第二行\u2028第三行"));
        Assert.Equal("a b c", ProjectNames.Clean("  a\t\tb\r\n\u2029c \u0007 "));
        Assert.Equal(string.Empty, ProjectNames.Clean("\n\t "));
    }

    [Fact]
    public void 清洗会话名_去掉方向控制与零宽空格_保留组成表情的连接符()
    {
        // U+202E 会把同一行后面的文字(状态灯里的等待原因、飞书里的后半句)倒过来显示。
        Assert.Equal("abc 等额度", ProjectNames.Clean("abc‮ 等额度"));
        Assert.Equal("ab", ProjectNames.Clean("a⁦‏b⁩﻿"));
        Assert.Equal(string.Empty, ProjectNames.Clean("​​"));
        string family = "\U0001F468‍\U0001F469‍\U0001F467";
        Assert.Equal(family, ProjectNames.Clean(family));
    }

    [Fact]
    public void 标题离文件尾超过64KiB也能找到_超过1MiB就不再往前读()
    {
        string root = TestTemp.NewDir("project-names-window");
        string folder = Path.Combine(root, "projects", "C--x");
        Directory.CreateDirectory(folder);
        string transcript = Path.Combine(folder, Session + ".jsonl");
        string title = """{"type":"custom-title","customTitle":"远处的标题","sessionId":"11111111-1111-1111-1111-111111111111"}""" + "\n";
        string filler = """{"type":"assistant","message":{"role":"assistant","content":"x"}}""" + "\n";

        File.WriteAllText(transcript, title + string.Concat(Enumerable.Repeat(filler, 200 * 1024 / filler.Length)));
        Assert.Equal("远处的标题", ProjectNames.SessionName(Session, transcript));

        File.WriteAllText(transcript, title + string.Concat(Enumerable.Repeat(filler, 1100 * 1024 / filler.Length)));
        Assert.Null(ProjectNames.SessionName(Session, transcript));
    }

    [Fact]
    public void 登记一次读成表_与逐个查找结果一致()
    {
        string root = TestTemp.NewDir("project-names-registry");
        string sessions = Path.Combine(root, "sessions");
        Directory.CreateDirectory(sessions);
        File.WriteAllText(Path.Combine(sessions, "1.json"), JsonSerializer.Serialize(new { pid = 1, sessionId = Session, name = "  整理发布说明 " }));
        File.WriteAllText(Path.Combine(sessions, "2.json"), "{ broken");
        File.WriteAllText(Path.Combine(sessions, "3.json"),
            JsonSerializer.Serialize(new { pid = 3, sessionId = "22222222-2222-2222-2222-222222222222", name = "   " }));

        IReadOnlyDictionary<string, string> names = ProjectNames.RegistryNames(root);

        Assert.Equal(new KeyValuePair<string, string>(Session, "整理发布说明"), Assert.Single(names));
        Assert.Equal("整理发布说明", ProjectNames.SessionName(Session, null, root));
        Assert.Equal("整理发布说明", ProjectNames.SessionName(Session, null, registry: names));
        Assert.Null(ProjectNames.SessionName("22222222-2222-2222-2222-222222222222", null, registry: names));
        Assert.Empty(ProjectNames.RegistryNames(Path.Combine(root, "missing")));
    }

    [Fact]
    public void 会话名_开着时以登记为准_关了退回转录尾部最近的标题_配置根从转录路径反推()
    {
        string root = TestTemp.NewDir("project-names");
        string folder = Path.Combine(root, "projects", "C--scratch");
        Directory.CreateDirectory(folder);
        string transcript = Path.Combine(folder, Session + ".jsonl");
        File.WriteAllText(transcript,
            """{"type":"user","cwd":"C:\\x","sessionId":"11111111-1111-1111-1111-111111111111"}""" + "\n" +
            """{"type":"custom-title","customTitle":"旧标题","sessionId":"11111111-1111-1111-1111-111111111111"}""" + "\n" +
            """{"type":"custom-title","customTitle":"别的会话的标题","sessionId":"22222222-2222-2222-2222-222222222222"}""" + "\n" +
            """{"type":"custom-title","customTitle":"整理发布说明","sessionId":"11111111-1111-1111-1111-111111111111"}""" + "\n" +
            """{"type":"custom-title","customTitle":"后来的另一个会话","sessionId":"22222222-2222-2222-2222-222222222222"}""" + "\n" +
            """{"type":"custom-ti""");

        // 只认这个会话自己的标题;末尾正在追加的半行跳过。
        Assert.Equal("整理发布说明", ProjectNames.SessionName(Session, transcript));

        // 会话开着:进程登记(配置根\sessions\<pid>.json)里的名字优先;坏掉的登记跳过。
        Directory.CreateDirectory(Path.Combine(root, "sessions"));
        File.WriteAllText(Path.Combine(root, "sessions", "100.json"), "{ not json");
        File.WriteAllText(Path.Combine(root, "sessions", "41440.json"),
            JsonSerializer.Serialize(new { pid = 41440, sessionId = Session, name = "通知文案重做", entrypoint = "claude-desktop" }));
        Assert.Equal("通知文案重做", ProjectNames.SessionName(Session, transcript));

        // 转录路径形状不对时用调用方给的配置根找登记;都没有就只看转录。
        string elsewhere = TestTemp.NewDir("project-names-loose");
        string loose = Path.Combine(elsewhere, Session + ".jsonl");
        File.Copy(transcript, loose);
        Assert.Equal("整理发布说明", ProjectNames.SessionName(Session, loose));
        Assert.Equal("通知文案重做", ProjectNames.SessionName(Session, loose, root));

        Assert.Null(ProjectNames.SessionName(null, transcript));
        Assert.Null(ProjectNames.SessionName("33333333-3333-3333-3333-333333333333", transcript));
        Assert.Null(ProjectNames.SessionName(Session, Path.Combine(elsewhere, "missing.jsonl")));
        Assert.Equal(root, ProjectNames.ClaudeRootOf(transcript));
        Assert.Null(ProjectNames.ClaudeRootOf(loose));
    }
}
