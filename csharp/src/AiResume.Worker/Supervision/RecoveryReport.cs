using AiResume.Core;

namespace AiResume.Worker.Supervision;

/// <summary>
/// 进程核验四类结论:
/// - Matched:PID 存在且启动时间 ±5s 容差内且命令签名一致 → 唯一允许终止的状态。
/// - Mismatched:PID 存在但特征明确不符(启动时间/签名) → 只删登记,不终止。
/// - Gone:进程明确不存在(快照未命中)。
/// - Unverifiable:查询失败或特征不可得 → 一律 fail-closed 保留登记、不动作。
/// </summary>
public enum ProcessVerdict
{
    Matched,
    Mismatched,
    Gone,
    Unverifiable,
}

/// <summary>恢复处置动作。只有 RemoveRegistry 会写登记表。</summary>
public enum RecoveryAction
{
    /// <summary>matched:进程存活且特征一致,保留登记继续监督。</summary>
    Keep,

    /// <summary>unverifiable:不可核验,保留登记不动作(fail-closed,防误清/误杀)。</summary>
    KeepFailClosed,

    /// <summary>gone / mismatched:登记过期或损坏,恢复流程授权清理。</summary>
    RemoveRegistry,
}

/// <summary>崩溃恢复报告单条:每项含 run_id、verdict、action。</summary>
public sealed record RecoveryReportItem(RunId RunId, ProcessVerdict Verdict, RecoveryAction Action);

/// <summary>崩溃恢复结构化报告。</summary>
public sealed record RecoveryReport(IReadOnlyList<RecoveryReportItem> Items);
