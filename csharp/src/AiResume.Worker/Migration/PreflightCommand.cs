using AiResume.Wrapper;

namespace AiResume.Worker.Migration;

/// <summary>
/// <c>AiResume.Worker.exe preflight</c>(S10):生产切换前的单消费者核验。
///
/// 对应切换流程里的「确认唯一消费者」一步。**它只读、只报告,不停止任何进程**——
/// 杀掉别人的进程必须是人的决定,不是一条预检命令的副作用。
///
/// 退出码:0 = 可以启动;1 = 存在冲突或无法核验(fail-closed)。
/// </summary>
public static class PreflightCommand
{
    public static int Run()
    {
        SingleConsumerGuard guard = SingleConsumerGuard.CreateDefault();

        // 传 true:预检的前提就是"我们打算接飞书"。传 false 会让守卫直接放行,
        // 那样预检就退化成一句空话。
        ConsumerGuardResult result = guard.Check(feishuPlatformConfigured: true);

        Console.WriteLine($"单消费者预检:{result.Verdict}");
        if (!string.IsNullOrEmpty(result.Reason))
        {
            Console.WriteLine($"  {result.Reason}");
        }

        foreach (ConflictingProcess conflict in result.Conflicts)
        {
            // Detail 只有进程名与固定文案,绝不含原始命令行(可能带飞书 app_secret)。
            Console.WriteLine($"  冲突 {conflict.Kind} PID={conflict.Pid} — {conflict.Detail}");
        }

        // **切换完成后的正常状态也会命中守卫**:守卫的语义是"启动前确认没有别人",
        // 而切换后本来就有一个 cc-connect 在跑。把这种情况单独说清楚,
        // 否则读输出的人(或执行冒烟的 AI)会把"运行正常"误判成"切换失败"。
        bool onlyCcConnect = !result.CanStart
            && result.Conflicts.Count > 0
            && result.Conflicts.All(c => string.Equals(c.Kind, "cc-connect", StringComparison.OrdinalIgnoreCase));

        if (result.CanStart)
        {
            Console.WriteLine("结论:本机没有飞书事件消费者,可以启动 cc-connect。");
            return 0;
        }

        if (onlyCcConnect)
        {
            Console.WriteLine(
                "结论:唯一的消费者是 cc-connect 本身——**这是切换完成后的正常状态**。" +
                "没有现役 node agent 残留。若要重启 cc-connect,先停止上面列出的进程。");
            // 退出码仍为 1:本命令回答的是"现在能不能再启一个",答案确实是不能。
            // 冒烟计划据此判定时看的是这段结论,不是裸退出码。
            return 1;
        }

        Console.WriteLine("结论:**不可启动**。飞书长连接是集群模式,事件会被随机截走。");
        return 1;
    }
}
