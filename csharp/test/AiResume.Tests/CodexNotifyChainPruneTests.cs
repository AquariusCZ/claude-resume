using System.Text.Json;
using AiResume.Worker.Notifications;
using Xunit;

namespace AiResume.Tests;

/// <summary>
/// notify 链剪枝。**2026-08-08 真实事故的回归。**
///
/// 安装目录改名成含空格的 <c>AI Resume</c> 之后,写进 notify 的钩子路径在空格处
/// 断成 <c>C:\Users\…\AppData\Local\AI</c>。断掉的条目**不含标记** AiResume.Hook.exe,
/// 于是 <c>IsOwnCommand</c> 认不出它是我们自己写的,**每次启用都再套一层而不是替换** ——
/// 套到第 8 层撞上 MaxChainDepth,适配器从此拒绝处理,
/// 界面只报「notify 链深度超过上限」。用户看到的是「Codex 通知打不开了」,
/// 而根因在七层之外。实测那一行长 9909 字符。
/// </summary>
public sealed class CodexNotifyChainPruneTests
{
    private const string Alive = @"C:\real\node.exe";
    private const string Dead = @"C:\Users\x\AppData\Local\AI";

    private static Func<string, bool> Exists(params string[] alive)
        => p => alive.Contains(p, StringComparer.OrdinalIgnoreCase);

    private static string[] Wrap(string exe, string[] inner)
        => [exe, "codex", "--previous-notify", JsonSerializer.Serialize(inner)];

    [Fact]
    public void 最外层活着就原样返回()
    {
        string[] arr = [Alive, "turn-ended"];

        Assert.Equal(arr, CodexNotificationAdapter.PruneDeadLinks(arr, Exists(Alive)));
    }

    [Fact]
    public void 剪掉断路径那层露出里面活着的命令()
    {
        string[] arr = Wrap(Dead, [Alive, "turn-ended"]);

        string[] pruned = CodexNotificationAdapter.PruneDeadLinks(arr, Exists(Alive));

        Assert.Equal([Alive, "turn-ended"], pruned);
    }

    [Fact]
    public void 连续七层断路径全部剪掉()
    {
        // 这就是用户机器上的真实形态:7 层同样的断路径,最内层是已删除的 v1 脚本。
        string[] arr = [Alive, "turn-ended"];
        for (int i = 0; i < 7; i++)
        {
            arr = Wrap(Dead, arr);
        }

        string[] pruned = CodexNotificationAdapter.PruneDeadLinks(arr, Exists(Alive));

        Assert.Equal([Alive, "turn-ended"], pruned);
    }

    [Fact]
    public void 最内层不是我方形状时保留它()
    {
        // 内层是 v1 的 [node, script, "codex"] —— 不是我方包装形状,
        // 即使文件不在也**保留**:那是用户配置,不是我们写坏的。
        string[] inner = [@"C:\gone\node.exe", @"C:\gone\v1.js", "codex"];
        string[] arr = Wrap(Dead, inner);

        Assert.Equal(inner, CodexNotificationAdapter.PruneDeadLinks(arr, Exists()));
    }

    [Fact]
    public void 文件不在但不是我方形状时一律保留()
    {
        // **这一条是安全阀**:用户的 notify 可能指向网络盘或暂时没装的程序。
        // 只凭"文件不存在"就删,等于替用户丢掉他自己的配置。
        string[] arr = [@"C:\net-share\his-tool.exe", "--flag"];

        Assert.Equal(arr, CodexNotificationAdapter.PruneDeadLinks(arr, Exists()));
    }

    [Theory]
    [InlineData("node")]
    [InlineData("python")]
    [InlineData("pwsh")]
    public void PATH上的裸命令一律保留(string bare)
    {
        // File.Exists 判不了裸命令。宁可留一条可能没用的,
        // 也不要误删用户自己配的 notify —— 那是他的配置,不是我们的。
        string[] arr = [bare, "hook.js"];

        Assert.Equal(arr, CodexNotificationAdapter.PruneDeadLinks(arr, Exists()));
    }

    [Fact]
    public void 内层JSON解不开就停下不猜()
    {
        string[] arr = [Dead, "codex", "--previous-notify", "{不是数组}"];

        Assert.Empty(CodexNotificationAdapter.PruneDeadLinks(arr, Exists(Alive)));
    }

    [Fact]
    public void 断路径但没有previous时保留()
    {
        // 不是完整的我方包装形状(没有 --previous-notify),不动它。
        string[] arr = [Dead, "codex"];

        Assert.Equal(arr, CodexNotificationAdapter.PruneDeadLinks(arr, Exists(Alive)));
    }

    [Fact]
    public void 空数组原样返回()
    {
        Assert.Empty(CodexNotificationAdapter.PruneDeadLinks([], Exists(Alive)));
    }

    // ── 路径截断:整个事故的源头 ──────────────────────────────────

    [Theory]
    // **这一条就是事故本身**:安装目录含空格,原来的 Split(' ')[0] 把它截成 …\Local\AI
    [InlineData(@"C:\Users\x\AppData\Local\AI Resume\AiResume.Hook.exe codex",
                @"C:\Users\x\AppData\Local\AI Resume\AiResume.Hook.exe")]
    [InlineData(@"C:\Users\x\AppData\Local\AI Resume\AiResume.Hook.exe",
                @"C:\Users\x\AppData\Local\AI Resume\AiResume.Hook.exe")]
    [InlineData(@"C:\tools\AiResume.Hook.exe codex", @"C:\tools\AiResume.Hook.exe")]
    [InlineData(@"C:\tools\AiResume.Hook.exe", @"C:\tools\AiResume.Hook.exe")]
    [InlineData(@"  C:\a b\hook.exe  codex  ", @"C:\a b\hook.exe")]
    public void 按exe边界切而不是按空格(string input, string expected)
    {
        Assert.Equal(expected, CodexNotificationAdapter.ExtractHookExe(input));
    }

    [Fact]
    public void 没有exe后缀时整串返回()
    {
        // node 脚本形式:没有 .exe 可切,整串交给上层原样处理,不猜。
        const string cmd = "node /opt/hook.js";

        Assert.Equal(cmd, CodexNotificationAdapter.ExtractHookExe(cmd));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void 空命令拒绝(string? cmd)
    {
        Assert.Throws<ArgumentException>(() => CodexNotificationAdapter.ExtractHookExe(cmd));
    }
}
