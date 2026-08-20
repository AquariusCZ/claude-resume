using AiResume.Core;
using AiResume.Worker.Supervision;

namespace AiResume.Worker.Products;

/// <summary>续跑引擎此刻的可信状态。顺序即严重程度,越靠后越需要动手。</summary>
public enum EngineVerdict
{
    /// <summary>没布防。引擎不该在盯,也就无所谓死活。</summary>
    NotArmed,

    /// <summary>布防中且引擎确实在跑,探测节奏正常。</summary>
    Alive,

    /// <summary>布防中,引擎进程在,但很久没探过额度了 —— 卡住了,不是在等。</summary>
    Stalled,

    /// <summary>布防中,而引擎进程根本不在。**配置说在盯,实际没人盯。**</summary>
    NotRunning,

    /// <summary>状态声称项目在续跑,但精确 RunId 对应的进程已不存在或不匹配。</summary>
    RunMissing,

    /// <summary>状态声称项目在续跑,但精确 RunId 的进程身份暂时无法核验。</summary>
    RunUnverified,

    /// <summary>非当前周期仍留有精确 RunId,且核验表明对应进程仍在运行。</summary>
    RunActive,

    /// <summary>布防周期状态无法从持久化层可靠读取,当前结论未知。</summary>
    StateUnverified,

    /// <summary>已请求终止精确进程,但核验表明它仍然存活。</summary>
    CancelPending,
}

/// <summary>
/// 「监视中」这三个字凭什么说得出口。
///
/// 原来的依据只有 <c>config.Armed</c> —— 一个布尔值,由用户点「布防」时写下,
/// 此后不会因为任何事情变回去。2026-08-08 第二轮审计把续跑 Worker 直接 kill 掉,
/// 面板顶部照旧绿灯写着「监视中」(A4)。**配置记录的是意图,不是事实**:
/// 它只能证明用户表达过"我要它盯着",证明不了现在真有人在盯。
///
/// 布防这件事对用户的全部价值就是"我睡觉时它替我盯着"。这一句说错,
/// 比面板上任何其它一句说错都严重 —— 用户会真的去睡。
/// </summary>
public static class EngineLiveness
{
    /// <summary>
    /// 判定引擎状态。纯函数,不碰进程也不碰时钟,便于把每一条分支都测到。
    /// </summary>
    /// <param name="armed">配置里的布防意图。</param>
    /// <param name="engineRunning">是否存在续跑引擎进程(由调用方探)。</param>
    /// <param name="lastProbeUtc">最近一次额度探测时间;从未探过为 null。</param>
    /// <param name="nowUtc">当前时刻。</param>
    /// <param name="probeIntervalMinutes">配置的探测间隔,用来推算"多久算太久"。</param>
    /// <param name="workInProgress">是否正在执行续跑。续跑期间本来就不会再次探额度。</param>
    public static EngineVerdict Evaluate(
        bool armed,
        bool engineRunning,
        DateTimeOffset? lastProbeUtc,
        DateTimeOffset nowUtc,
        int probeIntervalMinutes,
        bool workInProgress = false)
    {
        if (!armed)
        {
            return EngineVerdict.NotArmed;
        }

        if (!engineRunning)
        {
            return EngineVerdict.NotRunning;
        }

        // 续跑任务可以执行很久,而且 RunContract 明确不以静默时长判失败。
        // 此时 LastProbeUtc 停在触发续跑的那一拍是正常现象,不能据此报"引擎无响应"。
        if (workInProgress)
        {
            return EngineVerdict.Alive;
        }

        // 从未探过**不算卡住**:刚布防、引擎还没走到下一拍是正常的,
        // 这时候报红会让每次布防后的头几分钟都在假警报,红灯很快就不值钱了。
        if (lastProbeUtc is null)
        {
            return EngineVerdict.Alive;
        }

        // 容忍三拍。一拍太紧(一次探测慢一点就误报),
        // 地板 5 分钟是为了防住有人把间隔配成 1 分钟。
        double toleranceMinutes = Math.Max(5, Math.Max(1, probeIntervalMinutes) * 3);
        double ageMinutes = (nowUtc - lastProbeUtc.Value).TotalMinutes;

        // 时钟回拨会让 age 变成负数。负数不是"很新",是"读数不可信" ——
        // 但它同样不能证明引擎坏了,所以归 Alive 而不是 Stalled。
        return ageMinutes > toleranceMinutes ? EngineVerdict.Stalled : EngineVerdict.Alive;
    }

    /// <summary>
    /// 本机是否有续跑引擎进程在跑。
    ///
    /// 按进程名匹配,并排除当前进程自己(GUI 里调用时进程名不同,
    /// 但 Worker 内部自查时会撞上)。拿不到进程列表时返回 <c>null</c> 表示
    /// **探不出来** —— 交给调用方按未知处理,不要当成"没在跑"去报红。
    /// </summary>
    public static bool? TryDetectEngineProcess(string processName = "AiResume.Worker")
    {
        try
        {
            int self = Environment.ProcessId;
            using var _ = (IDisposable?)null;
            System.Diagnostics.Process[] all = System.Diagnostics.Process.GetProcessesByName(processName);
            try
            {
                return all.Any(p => p.Id != self);
            }
            finally
            {
                foreach (System.Diagnostics.Process p in all)
                {
                    p.Dispose();
                }
            }
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>给界面的一句话。措辞刻意区分"在等"和"没人在盯"——前者正常,后者要动手。</summary>
    public static string Describe(EngineVerdict verdict) => verdict switch
    {
        EngineVerdict.NotArmed => "未布防",
        EngineVerdict.Alive => "引擎在跑",
        EngineVerdict.Stalled => "引擎进程在,但久未探测额度",
        EngineVerdict.NotRunning => "已布防,但续跑引擎没在运行",
        EngineVerdict.RunMissing => "续跑状态仍在,但对应进程已不存在",
        EngineVerdict.RunUnverified => "续跑进程状态暂时无法核验",
        EngineVerdict.RunActive => "已登记的续跑进程仍在运行",
        EngineVerdict.StateUnverified => "布防状态暂时无法核实",
        EngineVerdict.CancelPending => "已请求终止续跑进程,但它仍在运行",
        _ => verdict.ToString(),
    };

    /// <summary>从状态与配置一步到位地判定(GUI 用)。进程探测不出来时按"在跑"处理,不误报。</summary>
    public static EngineVerdict Evaluate(
        ProductConfig config, CheckerState state, DateTimeOffset nowUtc, bool workInProgress = false)
    {
        bool currentCycle = config.Armed &&
            !string.IsNullOrEmpty(config.ArmCycleId) &&
            string.Equals(config.ArmCycleId, state.CycleId, StringComparison.Ordinal);

        return Evaluate(
            config.Armed,
            TryDetectEngineProcess() ?? true,
            currentCycle ? state.LastProbeUtc : null,
            nowUtc,
            config.ProbeIntervalMinutes,
            currentCycle && workInProgress);
    }

    /// <summary>
    /// 只把登记身份匹配且仍存活的 AI Resume 外层进程当作活动执行证据。
    /// GUI 不持有 Worker 的 Job Object；登记仍在但外层 PID 消失或复用时，无法据此证明
    /// Job 内后代已经结束，必须返回 null。只有登记本身不存在才返回 false。
    /// </summary>
    public static bool? TryDetectActiveOwnedProcess(string databasePath, string runId)
    {
        try
        {
            var registry = new SqliteProcessRegistry(databasePath);
            var probe = new NativeProcessProbe();
            return TryDetectActiveOwnedProcess(RunId.FromString(runId), registry, probe);
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>可注入版本:只核验指定 RunId,其它登记进程绝不构成当前续跑证据。</summary>
    public static bool? TryDetectActiveOwnedProcess(
        RunId runId,
        IProcessRegistry registry,
        IProcessProbe probe)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(probe);

        ProcessRegistryEntry? entry = registry.Get(runId);
        if (entry is null)
        {
            return false;
        }

        if (entry.ChildPid is null)
        {
            return null;
        }

        return ProcessVerifier.Verify(entry, probe.Probe(entry.ChildPid.Value)) switch
        {
            ProcessVerdict.Matched => true,
            _ => null,
        };
    }
}
