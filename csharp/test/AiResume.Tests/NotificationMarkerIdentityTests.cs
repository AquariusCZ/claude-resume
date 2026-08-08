using AiResume.Worker.Notifications;
using Xunit;

namespace AiResume.Tests;

/// <summary>
/// 通知适配器的**身份判定**必须自洽:用来识别「这条是我们写的」的标记,
/// 必须能在 Enable 实际写进配置的命令里找到。
///
/// 2026-08-07 实测事故:QoderNotificationAdapter 的标记是
/// <c>airesume-completion-hook.cmd</c>(早期批处理包装脚本的名字),
/// 而 Enable 写进去的是 <c>AiResume.Hook.exe</c>。两者对不上的后果不是"少个功能",
/// 而是**判定彻底失效**:
///
/// - Probe 永远报「未安装 AI Resume 通知钩子」;
/// - 界面开关因此永远显示"关",用户点它 → 调 Enable → **再追加一条** → 重新渲染仍是"关";
/// - 于是每点一次多一条。用户的 ~/.qoder/settings.json 里累积到了 **14 条**;
/// - Disable 也认不出这些条目,清不掉;
/// - 安装层的「重指」同样认不出,旧路径的条目全部留存。
///
/// 这类缺陷不会报错、不会红,只会让人觉得"这个按钮坏了"。所以必须用测试钉住。
/// </summary>
public sealed class NotificationMarkerIdentityTests : IDisposable
{
    private readonly string _root = TestTemp.NewDir("marker");

    public NotificationMarkerIdentityTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    /// <summary>典型的 hook 命令:安装层写进去的就是这个形状(绝对路径 + 可执行文件名)。</summary>
    private string HookCommand => Path.Combine(_root, "bin", HookExecutable.FileName);

    [Fact]
    public void Qoder的标记能在它自己写入的命令里找到()
    {
        string settings = Path.Combine(_root, "qoder-settings.json");
        File.WriteAllText(settings, "{}");

        var adapter = new QoderNotificationAdapter(settings);
        adapter.Enable(HookCommand);

        // 判定自洽的最小充分条件:写完之后自己认得出来。
        Assert.True(adapter.Probe().IsEnabled, "Enable 之后 Probe 必须报已启用");
        Assert.Contains(QoderNotificationAdapter.MarkerFileName, File.ReadAllText(settings), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Qoder重复启用不会追加重复条目()
    {
        // 这条直接对应实测现场:标记对不上时,连点 N 次就留下 N 条。
        string settings = Path.Combine(_root, "qoder-dup.json");
        File.WriteAllText(settings, "{}");

        var adapter = new QoderNotificationAdapter(settings);
        adapter.Enable(HookCommand);
        adapter.Enable(HookCommand);
        adapter.Enable(HookCommand);

        int occurrences = CountOccurrences(File.ReadAllText(settings), QoderNotificationAdapter.MarkerFileName);
        Assert.Equal(1, occurrences);
    }

    [Fact]
    public void Qoder停用能清掉自己写的条目()
    {
        string settings = Path.Combine(_root, "qoder-off.json");
        File.WriteAllText(settings, "{}");

        var adapter = new QoderNotificationAdapter(settings);
        adapter.Enable(HookCommand);
        adapter.Disable();

        Assert.False(adapter.Probe().IsEnabled);
        Assert.DoesNotContain(QoderNotificationAdapter.MarkerFileName, File.ReadAllText(settings), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ClaudeCode的标记同样自洽()
    {
        // 对照组:这个适配器一直是对的,一并钉住,防止哪天被"统一"成错的那个。
        string settings = Path.Combine(_root, "claude-settings.json");
        File.WriteAllText(settings, "{}");

        var adapter = new ClaudeCodeNotificationAdapter(settings);
        adapter.Enable(HookCommand);

        Assert.True(adapter.Probe().IsEnabled);
        Assert.Contains(ClaudeCodeNotificationAdapter.MarkerFileName, File.ReadAllText(settings), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void 各适配器的标记都出现在安装层写入的命令里()
    {
        // 安装层统一把 hook 命令写成「…\AiResume.Hook.exe」。
        // 凡是靠"命令里含某文件名"做身份判定的适配器,标记都必须能在这个命令里找到,
        // 否则它写完就认不得自己。
        string installed = Path.Combine(_root, "AI Resume", HookExecutable.FileName);

        Assert.Contains(ClaudeCodeNotificationAdapter.MarkerFileName, installed, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(QoderNotificationAdapter.MarkerFileName, installed, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(CodexNotificationAdapter.MarkerFileName, installed, StringComparison.OrdinalIgnoreCase);
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        int count = 0, i = 0;
        while ((i = haystack.IndexOf(needle, i, StringComparison.OrdinalIgnoreCase)) >= 0)
        {
            count++;
            i += needle.Length;
        }

        return count;
    }
}
