using System;
using System.Linq;
using AiResume.Worker.Notifications;

namespace AiResume.Worker.Migration;

/// <summary>
/// <c>AiResume.Worker.exe notify</c>:列出、启用或停用 AI 编程工具的通知适配器。
///
/// 为什么放在 Migration 命名空间:它和 FeishuCheckCommand 一样是运维诊断/迁移工具,
/// 不参与业务主流程,且需要直接操作注册表与适配器。
/// </summary>
public static class NotifyCommand
{
    /// <summary>
    /// 执行 notify 子命令。
    /// </summary>
    /// <param name="args">子命令参数:list / enable / disable 及可选 kind。</param>
    /// <returns>0 表示成功,1 表示失败(找不到适配器或操作异常)。</returns>
    public static int Run(string[] args)
    {
        // 无子命令时默认 list,与规格一致。
        // args 是 Program.cs 传下来的**完整**命令行,args[0] 是 "notify" 本身,
        // 子命令从 args[1] 起(与 cutover-config 等既有命令的约定一致)。
        string sub = args.Length > 1 ? args[1] : "list";

        switch (sub.ToLowerInvariant())
        {
            case "list":
                return ListAll();

            case "enable":
            case "disable":
                // kind 缺失时按"找不到"处理,由 SetEnabled 内部抛异常,
                // 但这里先取出来,避免索引越界。
                string? kindArg = args.Length > 2 ? args[2] : null;
                return SetEnabled(sub == "enable", kindArg);

            default:
                // 未知子命令:打印可用列表并返回 1,与"找不到"行为一致。
                Console.Error.WriteLine($"未知子命令: {sub}");
                PrintAvailableKinds();
                return 1;
        }
    }

    /// <summary>
    /// 列出全部适配器的探测状态。
    /// </summary>
    private static int ListAll()
    {
        var registry = new NotificationRegistry();
        var statuses = registry.ProbeAll();

        // 按 Kind 排序输出,保证多次运行输出顺序稳定,便于 diff 对比。
        // **单列「可送达」**:已启用只说明配置里有这条命令,说明不了它跑得起来。
        // 审计里把 hook 可执行文件挪走后,这一行照旧打印"已启用=True"(2026-08-08 A1)。
        foreach (var status in statuses.OrderBy(s => s.Kind))
        {
            string deliverable = status.IsEnabled ? (status.HookBroken ? "False(钩子断链)" : "True") : "-";
            Console.WriteLine(
                $"{status.Kind}  {status.DisplayName}  " +
                $"已安装={status.IsInstalled}  已启用={status.IsEnabled}  " +
                $"可送达={deliverable}  {status.Detail}");
        }

        // 断链是需要动手的状态,不能只混在一行文字里:退出码得让脚本也看得见。
        return statuses.Any(s => s.IsEnabled && s.HookBroken) ? 2 : 0;
    }

    /// <summary>
    /// 启用或停用指定 Kind 的适配器。
    /// </summary>
    /// <param name="enable">true 为启用,false 为停用。</param>
    /// <param name="kindArg">Kind 的字符串表示,大小写不敏感。</param>
    private static int SetEnabled(bool enable, string? kindArg)
    {
        // 解析 Kind;解析失败(含 null)时打印可用列表并返回 1。
        if (!TryParseKind(kindArg, out var kind))
        {
            Console.Error.WriteLine($"未知或缺失的 Kind: {kindArg ?? "(空)"}");
            PrintAvailableKinds();
            return 1;
        }

        // **写进用户配置的必须是绝对路径**,不能是裸文件名。
        // 实测:裸 "AiResume.Hook.exe" 依赖它在 PATH 上,而它不在——
        // 钩子照样被写进 ~/.claude/settings.json,界面显示"已启用",探测也报"已安装",
        // 但每次任务结束时命令根本执行不了,事件队列永远是空的。失败完全静默。
        // 找不到就拒绝启用:宁可报错,也不要留一个假装已启用的坏钩子。
        string hookCommand = string.Empty;
        if (enable)
        {
            string? resolved = HookExecutable.TryResolve();
            if (resolved is null)
            {
                Console.Error.WriteLine(
                    $"找不到 {HookExecutable.FileName},拒绝启用——写进配置的钩子会指向不存在的程序,");
                Console.Error.WriteLine("表现为「已启用但永远收不到通知」。请先构建 AiResume.Hook 项目。");
                return 1;
            }

            hookCommand = resolved;
        }

        var registry = new NotificationRegistry();
        try
        {
            registry.SetEnabled(kind, enable, hookCommand);
            // 记下意图:重装时靠它恢复。不记的话卸载一次现状就没了(审计 B3)。
            RecordIntent(kind, enable);
            Console.WriteLine($"已{(enable ? "启用" : "停用")} {kind}");
            return 0;
        }
        catch (Exception ex)
        {
            // 只打印异常消息,不打印堆栈,避免泄露内部路径等敏感信息。
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
    }

    /// <summary>
    /// 把开关动作记进持久化意图。**只改这一个字段**(锁内读-改-写):
    /// 配置同时被 GUI 与续跑引擎写,整体写回旧快照会互相覆盖。
    /// 记不下来不影响本次开关是否生效,所以只警告不失败。
    /// </summary>
    private static void RecordIntent(NotificationProviderKind kind, bool enable)
    {
        try
        {
            var store = new Products.ProductConfigStore(ShadowPaths.EnsureRoot());
            store.Update(c => c.NotifySources = NotifyIntent.Toggle(c.NotifySources, kind, enable));
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"警告:通知源意图未能保存({ex.Message});重装后可能需要手动重开。");
        }
    }

    /// <summary>
    /// 按大小写不敏感解析 Kind 字符串。
    /// </summary>
    private static bool TryParseKind(string? text, out NotificationProviderKind kind)
    {
        kind = default;
        if (string.IsNullOrWhiteSpace(text))
            return false;

        // 枚举解析默认大小写不敏感,但显式指定更清晰。
        return Enum.TryParse(text, ignoreCase: true, out kind)
               && Enum.IsDefined(typeof(NotificationProviderKind), kind);
    }

    /// <summary>
    /// 打印所有可用 Kind 列表到标准错误。
    /// </summary>
    private static void PrintAvailableKinds()
    {
        Console.Error.WriteLine("可用 Kind:");
        foreach (var name in Enum.GetNames<NotificationProviderKind>())
        {
            Console.Error.WriteLine($"  {name}");
        }
    }
}
