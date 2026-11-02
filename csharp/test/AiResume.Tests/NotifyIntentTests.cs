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

    [Fact]
    public void 拨开关先记意图_改客户端失败就把这一项的意图恢复原样()
    {
        // v2.2 起意图也是投递端的来源过滤:界面显示开着、意图却没记上,这个来源的事件会被全部丢弃。
        var store = new AiResume.Worker.Products.ProductConfigStore(TestTemp.NewDir("notify-intent-apply"));
        store.Update(c => c.NotifySources = new List<string> { "ClaudeCode" });

        Assert.Throws<IOException>(() => NotifyIntent.Apply(store, NotificationProviderKind.OpenCode, true,
            () => throw new IOException("插件目录被占用")));
        var restored = store.Load();
        Assert.Equal(new[] { "ClaudeCode" }, restored.NotifySources);
        Assert.False(restored.NotifyIntentRecorded);

        bool changed = false;
        NotifyIntent.Apply(store, NotificationProviderKind.OpenCode, true, () => changed = true);
        var applied = store.Load();
        Assert.True(changed);
        Assert.Equal(new[] { "ClaudeCode", "OpenCode" }, applied.NotifySources);
        Assert.True(applied.NotifyIntentRecorded);
    }

    [Fact]
    public void 改客户端时部分生效后才失败_意图跟着实际状态走_证明没生效才恢复()
    {
        var store = new AiResume.Worker.Products.ProductConfigStore(TestTemp.NewDir("notify-intent-partial"));
        store.Update(c => c.NotifySources = new List<string> { "ClaudeCode" });

        // 新插件已经写好，清理旧单文件时失败:插件在跑，意图不能被改回「关」(否则投递端把它的事件全丢掉)。
        Assert.Throws<IOException>(() => NotifyIntent.Apply(store, NotificationProviderKind.OpenCode, true,
            () => throw new IOException("旧插件文件被占用"), () => true));
        Assert.Equal(new[] { "ClaudeCode", "OpenCode" }, store.Load().NotifySources);
        Assert.True(store.Load().NotifyIntentRecorded);

        // 探不出实际状态:保留这次的选择。
        Assert.Throws<IOException>(() => NotifyIntent.Apply(store, NotificationProviderKind.Codex, true,
            () => throw new IOException("hooks.json 被占用"), () => null));
        Assert.Contains("Codex", store.Load().NotifySources);

        // 探测证明根本没生效:恢复原意图。
        Assert.Throws<IOException>(() => NotifyIntent.Apply(store, NotificationProviderKind.Codex, false,
            () => throw new IOException("hooks.json 被占用"), () => true));
        Assert.Contains("Codex", store.Load().NotifySources);
    }

    [Fact]
    public void 探测失败的源不进对账目标_也不从意图里抹掉()
    {
        var probed = new[]
        {
            S(NotificationProviderKind.ClaudeCode, true, true),
            new NotificationProviderStatus(NotificationProviderKind.OpenCode, "OpenCode", false, false, null, "探测异常", Unverified: true),
        };
        string[] recorded = { "ClaudeCode", "OpenCode" };

        Assert.Equal(new[] { NotificationProviderKind.ClaudeCode }, NotifyIntent.Targets(recorded, probed));
        Assert.Equal(new[] { NotificationProviderKind.OpenCode }, NotifyIntent.Unverified(recorded, probed));
        Assert.Equal(new[] { "ClaudeCode", "OpenCode" }, NotifyIntent.FromProbe(probed, recorded));
        Assert.Equal(new[] { "ClaudeCode" }, NotifyIntent.FromProbe(probed));
    }
}
