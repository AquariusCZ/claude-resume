using AiResume.Core;

namespace AiResume.Worker.Notifications;

/// <summary>
/// 「用户想开哪几个通知源」与「现在实际开着哪几个」之间的对账。
///
/// 分开这两件事是 2026-08-08 审计 B3 的直接教训:
/// <c>install → uninstall → install</c> 的第二次安装退出码 0、日志写着
/// "入口已全部指向安装目录",而五个通知源全是关的。
/// 原因是重指那一步只认现状——<c>ProbeAll()</c> 里 <c>IsEnabled</c> 的才重指——
/// 而卸载上一步刚把它们全关了。**现状被自己的上一步清空,循环体一次都没进。**
///
/// 现状永远只能回答"此刻是什么样",回答不了"本该是什么样"。
/// 后者必须自己记下来(<see cref="ProductConfig.NotifySources"/>),这个类负责两者的换算。
/// </summary>
public static class NotifyIntent
{
    /// <summary>把探测结果里**已启用**的那些,折成可持久化的意图名单。</summary>
    public static List<string> FromProbe(IEnumerable<NotificationProviderStatus> probed)
        => probed.Where(s => s.IsEnabled)
                 .Select(s => s.Kind.ToString())
                 .Distinct(StringComparer.OrdinalIgnoreCase)
                 .ToList();

    /// <summary>把名字解析回枚举,丢掉无法识别的;顺序去重,按枚举序稳定输出。</summary>
    public static List<NotificationProviderKind> Parse(IEnumerable<string>? names)
    {
        var set = new HashSet<NotificationProviderKind>();
        foreach (string n in names ?? Enumerable.Empty<string>())
        {
            // 手改坏的配置不该让安装整个失败:认不出的名字直接跳过。
            if (Enum.TryParse<NotificationProviderKind>(n?.Trim(), ignoreCase: true, out var k))
            {
                set.Add(k);
            }
        }

        return set.OrderBy(k => (int)k).ToList();
    }

    /// <summary>
    /// 安装时该把哪几个源(重新)写进去。
    ///
    /// 目标 = <b>记下来的意图</b> ∪ <b>此刻仍开着的</b>,再 ∩ <b>本机装了这个工具的</b>。
    ///
    /// 三项各有理由:
    /// <list type="bullet">
    /// <item>并意图:卸载后现状是空的,只有意图能说出该恢复什么;</item>
    /// <item>并现状:首次安装没有意图(或用户是从旧版升上来的),
    ///       不并进来会把人家本来开着的通知悄悄关掉;</item>
    /// <item>交"已安装":工具已经从本机卸载时,不该替它重建一份配置目录。</item>
    /// </list>
    ///
    /// 注意目标里**包含已经开着且健康的那些** —— 安装要把它们重指到新的安装目录,
    /// 不是只补缺失的。跳过它们会让钩子继续指着仓库 bin,清一次 bin 就断,而且断得没有任何提示。
    /// </summary>
    public static List<NotificationProviderKind> Targets(
        IEnumerable<string>? recorded,
        IReadOnlyList<NotificationProviderStatus> probed)
    {
        var installed = probed.Where(s => s.IsInstalled).Select(s => s.Kind).ToHashSet();

        var want = new HashSet<NotificationProviderKind>(Parse(recorded));
        foreach (NotificationProviderStatus s in probed)
        {
            if (s.IsEnabled)
            {
                want.Add(s.Kind);
            }
        }

        want.IntersectWith(installed);
        return want.OrderBy(k => (int)k).ToList();
    }

    /// <summary>用户拨动某个开关后的新意图名单。保持稳定顺序,便于配置文件 diff。</summary>
    public static List<string> Toggle(
        IEnumerable<string>? recorded, NotificationProviderKind kind, bool enabled)
    {
        var set = new HashSet<NotificationProviderKind>(Parse(recorded));
        if (enabled)
        {
            set.Add(kind);
        }
        else
        {
            set.Remove(kind);
        }

        return set.OrderBy(k => (int)k).Select(k => k.ToString()).ToList();
    }
}
