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

            case "check":
                return Check();

            case "test":
                return Test();

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
            // 意图先落盘、再改客户端配置:它是重装时恢复的依据(审计 B3),也是投递端的来源过滤。
            // 记不下来就不改开关,改客户端失败就把意图恢复原样(NotifyIntent.Apply)。
            var store = new Products.ProductConfigStore(ShadowPaths.EnsureRoot());
            NotifyIntent.Apply(store, kind, enable, () => registry.SetEnabled(kind, enable, hookCommand),
                () => registry.ProbeEnabled(kind));
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
    /// <c>notify check</c>:飞书通知通道体检。lark-cli 是否找得到、凭据来源(机器范围副本 env / lark-cli 钥匙串)、
    /// 收件人、Worker 最近的真实投递结论，再跑一次 <c>lark-cli doctor</c>(在线，只读)。全部就绪返回 0,否则 2。
    /// </summary>
    private static int Check()
    {
        var control = new NotifyControlPlane();
        NotifyChannelStatus status = control.Describe();
        Console.WriteLine($"lark-cli: {(status.LarkCliFound ? "找到" : "未找到(npm i -g @larksuite/cli)")}");
        Console.WriteLine($"机器人凭据: {(status.CredentialSource == "env" ? "AI Resume 机器范围副本 " + status.AppIdMasked : "lark-cli 钥匙串(后台计划任务下不可靠)")}");
        Console.WriteLine($"收件人: {(status.RecipientSet ? status.RecipientMasked : "未设置")}");
        if (status.Health is { } health)
        {
            string lastAttempt = health.LastAttemptOk switch { true => "成功", false => "失败(" + (health.LastErrorClass ?? "?") + ")", null => "-" };
            Console.WriteLine($"最近投递: 状态={health.State} 原因={health.Reason ?? "-"} 最近一次尝试={lastAttempt} 最近成功={health.LastSuccessUtc?.ToLocalTime():MM-dd HH:mm} 积压={health.Backlog} 死信={health.Dead}");
            if (status.SettingsVerified == false)
            {
                Console.WriteLine("  注意:最近一次成功之后改过机器人凭据、收件人或 lark-cli,那次成功不再作数;运行 notify test 重新核实。");
            }
        }
        else
        {
            Console.WriteLine("最近投递: 未核实(Worker 还没有写出投递健康记录)");
        }

        LarkDoctorReport report = control.CheckAsync(CancellationToken.None).GetAwaiter().GetResult();
        foreach (LarkDoctorCheck check in report.Checks)
        {
            Console.WriteLine($"  doctor {check.Name}: {check.Status} {check.Message}");
        }

        if (report.Problem is not null)
        {
            Console.WriteLine($"  doctor: {report.Problem}");
        }

        // 最近一次真实投递失败(条目级,如收件人跨应用)时不报就绪:doctor 只证明机器人身份,不证明收件人可达。
        bool ready = status.LarkCliFound && status.RecipientSet && report.BotReady && status.Health?.State != "blocked" &&
                     status.Health?.LastAttemptOk != false;
        Console.WriteLine(ready ? "结论: 飞书通知通道就绪" : "结论: 飞书通知通道未就绪(见上)");
        return ready ? 0 : 2;
    }

    /// <summary>
    /// <c>notify test</c>:往队列放一条冒烟事件，等后台 Worker 经 lark-cli 真实投递(最多 90 秒)。
    /// 走的是与任务完成通知完全相同的链路，送达返回 0,否则 2。
    /// </summary>
    private static int Test()
    {
        var control = new NotifyControlPlane();
        DateTimeOffset started = DateTimeOffset.UtcNow;
        string eventId = control.EnqueueTest();
        Console.WriteLine($"已入队测试通知 {eventId},等待后台 Worker 投递…");
        for (int i = 0; i < 90; i++)
        {
            Thread.Sleep(TimeSpan.FromSeconds(1));
            // 只认这一条冒烟事件自己的结局:它进了去重表才算送达(别的事件同时成功不算)。与 GUI「发送测试」同一判据。
            string outcome = control.TestOutcome(eventId, started);
            if (outcome == "delivered")
            {
                Console.WriteLine("已送达飞书(请在手机上确认收到「AI Resume 通知冒烟通过」)。");
                return 0;
            }

            DeliveryHealth? health = DeliveryHealth.TryLoad(control.HealthPath);
            if (outcome == "dead")
            {
                Console.Error.WriteLine($"测试通知没能送达,已移入死信:{health?.LastErrorClass ?? "-"}(错误码 {health?.LastErrorCode?.ToString() ?? "-"},log_id {health?.LastLogId ?? "-"})。运行 notify check 查看原因。");
                return 2;
            }

            if (outcome == "failed")
            {
                string? failure = control.TestFailure(eventId);
                Console.Error.WriteLine($"测试通知投递失败:{failure ?? "-"}。{WithdrawText(control, eventId, started)}运行 notify check 查看原因。");
                return 2;
            }

            if (outcome == "blocked")
            {
                Console.Error.WriteLine($"投递通道熔断:{health?.Reason}(错误码 {health?.LastErrorCode?.ToString() ?? "-"},log_id {health?.LastLogId ?? "-"})。{WithdrawText(control, eventId, started)}运行 notify check 查看原因。");
                return 2;
            }
        }

        // 没送出去的冒烟事件撤回,免得通道恢复后补发一条过时的「冒烟通过」。
        string withdrawn = WithdrawText(control, eventId, started);
        if (withdrawn.StartsWith("已送达", StringComparison.Ordinal))
        {
            Console.WriteLine("已送达飞书(请在手机上确认收到「AI Resume 通知冒烟通过」)。");
            return 0;
        }

        Console.Error.WriteLine($"90 秒内没有确认送达。{withdrawn}确认后台 Worker 在运行(开机自启计划任务),并运行 notify check。");
        return 2;
    }

    /// <summary>
    /// 撤回测试通知并如实说明结果:撤回成功才说已撤回;撤不回(Worker 正在发它)时再看一眼结局——
    /// 已送达就说已送达，否则提示可能仍会送达。
    /// </summary>
    private static string WithdrawText(NotifyControlPlane control, string eventId, DateTimeOffset started)
    {
        if (control.WithdrawTest(eventId))
        {
            return "测试通知已撤回。";
        }

        Thread.Sleep(TimeSpan.FromSeconds(3));
        return control.TestOutcome(eventId, started) switch
        {
            "delivered" => "已送达(撤回时 Worker 正在发送这一条)。",
            "dead" => "测试通知已移入死信。",
            _ => "撤回未确认:Worker 可能正在发送这一条,稍后仍可能收到。",
        };
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
