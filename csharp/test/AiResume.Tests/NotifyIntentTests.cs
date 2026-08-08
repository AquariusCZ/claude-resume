using AiResume.Worker.Notifications;
using Xunit;

namespace AiResume.Tests;

/// <summary>
/// 意图与现状的对账(审计 B3)。
///
/// 原缺陷:<c>install → uninstall → install</c>,第二次安装退出码 0、
/// 打印"入口已全部指向安装目录",而五个通知源全是关的。
/// 因为重指那一步只认现状,而卸载上一步刚把现状清空 —— 循环体一次都没进。
/// </summary>
public sealed class NotifyIntentTests
{
    private static NotificationProviderStatus S(
        NotificationProviderKind kind, bool installed, bool enabled) =>
        new(kind, kind.ToString(), installed, enabled, null, null);

    [Fact]
    public void 卸载后现状为空也要按意图恢复()
    {
        // 这就是 B3 的原始场景。
        var probed = new[]
        {
            S(NotificationProviderKind.ClaudeCode, installed: true, enabled: false),
            S(NotificationProviderKind.Codex, installed: true, enabled: false),
        };

        var targets = NotifyIntent.Targets(new[] { "ClaudeCode", "Codex" }, probed);

        Assert.Equal(
            new[] { NotificationProviderKind.ClaudeCode, NotificationProviderKind.Codex }, targets);
    }

    [Fact]
    public void 首次安装没有意图时不得把已开着的关掉()
    {
        // 从旧版升上来的机器没有 notifySources 字段。只按意图算会把人家开着的静默关掉。
        var probed = new[] { S(NotificationProviderKind.Cline, installed: true, enabled: true) };

        Assert.Equal(new[] { NotificationProviderKind.Cline }, NotifyIntent.Targets(null, probed));
    }

    [Fact]
    public void 本机已卸载该工具时不替它重建配置()
    {
        var probed = new[] { S(NotificationProviderKind.Qoder, installed: false, enabled: false) };

        Assert.Empty(NotifyIntent.Targets(new[] { "Qoder" }, probed));
    }

    [Fact]
    public void 已开着且健康的也要进目标以便重指()
    {
        // 安装的职责之一是把钩子从仓库 bin 重指到安装目录。
        // 只补缺失的会让它继续指着 bin —— 清一次 bin 就断,而且断得没有任何提示。
        var probed = new[] { S(NotificationProviderKind.ClaudeCode, installed: true, enabled: true) };

        Assert.Equal(
            new[] { NotificationProviderKind.ClaudeCode },
            NotifyIntent.Targets(Array.Empty<string>(), probed));
    }

    [Fact]
    public void 认不出的名字被跳过而不是让安装失败()
    {
        var probed = new[] { S(NotificationProviderKind.Codex, installed: true, enabled: false) };

        var targets = NotifyIntent.Targets(new[] { "Codex", "Cursor", "", "   " }, probed);

        Assert.Equal(new[] { NotificationProviderKind.Codex }, targets);
    }

    [Fact]
    public void 名字大小写不敏感()
    {
        var probed = new[] { S(NotificationProviderKind.OpenCode, installed: true, enabled: false) };

        Assert.Equal(
            new[] { NotificationProviderKind.OpenCode }, NotifyIntent.Targets(new[] { "opencode" }, probed));
    }

    [Fact]
    public void 输出按枚举序稳定()
    {
        var probed = new[]
        {
            S(NotificationProviderKind.OpenCode, true, false),
            S(NotificationProviderKind.ClaudeCode, true, false),
            S(NotificationProviderKind.Cline, true, false),
        };

        var targets = NotifyIntent.Targets(new[] { "OpenCode", "Cline", "ClaudeCode" }, probed);

        Assert.Equal(
            new[]
            {
                NotificationProviderKind.ClaudeCode,
                NotificationProviderKind.Cline,
                NotificationProviderKind.OpenCode,
            },
            targets);
    }

    [Fact]
    public void 从探测折出意图只取已启用的()
    {
        var probed = new[]
        {
            S(NotificationProviderKind.ClaudeCode, true, true),
            S(NotificationProviderKind.Codex, true, false),
            S(NotificationProviderKind.Cline, false, false),
        };

        Assert.Equal(new[] { "ClaudeCode" }, NotifyIntent.FromProbe(probed));
    }

    [Fact]
    public void 拨开关只动那一项()
    {
        var after = NotifyIntent.Toggle(new[] { "ClaudeCode", "Codex" }, NotificationProviderKind.Cline, true);
        Assert.Equal(new[] { "ClaudeCode", "Codex", "Cline" }, after);

        var off = NotifyIntent.Toggle(after, NotificationProviderKind.Codex, false);
        Assert.Equal(new[] { "ClaudeCode", "Cline" }, off);
    }

    [Fact]
    public void 重复开同一项不会记两遍()
    {
        var once = NotifyIntent.Toggle(new[] { "Codex" }, NotificationProviderKind.Codex, true);

        Assert.Equal(new[] { "Codex" }, once);
    }

    [Fact]
    public void 关一个没开过的项不报错()
    {
        Assert.Empty(NotifyIntent.Toggle(null, NotificationProviderKind.Qoder, false));
    }
}
