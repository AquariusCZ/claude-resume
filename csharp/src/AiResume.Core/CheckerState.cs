using System.Text.Json;

namespace AiResume.Core;

/// <summary>
/// S5-C 布防周期状态(契约)。语义对齐现役 Get-CcuState(checker.ps1 + lib.ps1):
/// phase=idle/waiting/resuming/done;cycleId 变化即周期失效;realReset 字段只取服务端
/// 精确值(禁止本地估算);sawLimited/limitedRefires 支撑限流节奏与误判死循环防护。
/// </summary>
public sealed class CheckerState
{
    public static JsonSerializerOptions JsonOptions { get; } = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    public const string PhaseIdle = "idle";
    public const string PhaseWaiting = "waiting";
    public const string PhaseResuming = "resuming";
    public const string PhaseDone = "done";
    public const string PhaseBlocked = "blocked";

    /// <summary>当前布防阶段:idle/waiting/resuming/done/blocked。</summary>
    public string Phase { get; set; } = PhaseIdle;

    /// <summary>布防周期 id(armCycleId 的 shadow 镜像);周期变化即失效。</summary>
    public string CycleId { get; set; } = string.Empty;

    /// <summary>本周期是否观测到过限流(决定恢复后是否触发续跑)。</summary>
    public bool SawLimited { get; set; }

    /// <summary>最近一次探测时间(节奏计算基线)。</summary>
    public DateTimeOffset? LastProbeUtc { get; set; }

    /// <summary>最近一次门禁结果的稳定原因码，不含异常正文或凭据。</summary>
    public string LastProbeReason { get; set; } = string.Empty;

    /// <summary>连续被误判限流计数(≥6 防死循环,标记 error 继续)。</summary>
    public int LimitedRefires { get; set; }

    /// <summary>
    /// 逐项目的「额度说可用、一跑就被限流」连续次数(path → 次数);这个项目成功一次即清零。
    /// 阈值按项目判断:一个项目反复误判,不能靠别的项目的成功把它清零后无限重试，也不能排在队首饿死别的项目。
    /// </summary>
    public Dictionary<string, int>? ProjectLimitedRefires { get; set; }

    /// <summary>本周期已经出现不可自动重放的结果;必须解除并重新布防后才能再次续跑。</summary>
    public bool ReplayBlocked { get; set; }

    /// <summary>服务端精确 5 小时重置时间(仅限流/接近限流时由探测下发)。</summary>
    public DateTimeOffset? RealFiveHourResetUtc { get; set; }

    /// <summary>服务端精确 7 天重置时间。</summary>
    public DateTimeOffset? RealSevenDayResetUtc { get; set; }

    /// <summary>最近一次读到真实重置时间的时间(诊断用)。</summary>
    public DateTimeOffset? RealResetProbedUtc { get; set; }

    /// <summary>服务端 5 小时窗口利用率(0..1)。</summary>
    public double? RealFiveHourUtil { get; set; }

    /// <summary>
    /// 逐项目结果(path → success/limited/running/continued-elsewhere/error/…)。
    /// limited = 待续跑;success 与 continued-elsewhere(已在客户端或手机上续上)都是完成态。
    /// </summary>
    public Dictionary<string, string>? ProjectStatus { get; set; }

    /// <summary>逐项目的当前判定说明(path → 原因码/窗口/预计时间),只用于界面与诊断，不参与阻断。</summary>
    public Dictionary<string, ProjectNote>? ProjectNotes { get; set; }

    /// <summary>
    /// AI Resume 已经发起过续跑的限额中断(path → 中断记录 uuid)。在 spawn 前与 running 一起落盘，
    /// 保证同一次中断最多续一次——即使续跑没能把会话尾部改掉，或 Worker 在运行中崩溃。
    /// </summary>
    public Dictionary<string, string>? ConsumedInterruptions { get; set; }

    /// <summary>当前续跑的精确 RunId;在 spawn 前与 running 状态一次性写入。</summary>
    public string ActiveRunId { get; set; } = string.Empty;

    /// <summary>与 <see cref="ActiveRunId"/> 绑定的项目路径。</summary>
    public string ActiveProjectPath { get; set; } = string.Empty;

    /// <summary>已请求终止但尚未确认退出的精确 RunId;跨布防周期保留。</summary>
    public string PendingCancellationRunId { get; set; } = string.Empty;

    /// <summary>待确认终止进程原属的项目路径。</summary>
    public string PendingCancellationProjectPath { get; set; } = string.Empty;

    /// <summary>待确认终止进程原属的布防周期。</summary>
    public string PendingCancellationCycleId { get; set; } = string.Empty;

    public static CheckerState CreateDefault() => new();
}

/// <summary>
/// 一个项目当前为什么在等、在等什么。原因码稳定(界面据此出文案),细节只含窗口名、状态码与时间，
/// 不含路径以外的任何用户数据。
/// </summary>
public sealed class ProjectNote
{
    /// <summary>
    /// not-interrupted / watching / ready / running / done / continued-elsewhere /
    /// quota:&lt;额度结论&gt; / session-in-use / cwd-busy / session-unverified / evidence-unverified /
    /// not-started:&lt;原因&gt;。
    /// </summary>
    public string Reason { get; set; } = string.Empty;

    public string? Detail { get; set; }

    /// <summary>预计解除阻挡的时间(服务端重置时间),unix 秒。</summary>
    public long? ResetAtUnix { get; set; }

    /// <summary>会话被限额打断的时间,unix 秒。</summary>
    public long? InterruptedAtUnix { get; set; }

    /// <summary>占用会话的客户端入口(claude-desktop / cli …)。</summary>
    public string? Client { get; set; }

    public long AtUnix { get; set; }
}
