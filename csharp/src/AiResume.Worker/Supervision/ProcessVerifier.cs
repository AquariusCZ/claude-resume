using AiResume.Core.Contracts;

namespace AiResume.Worker.Supervision;

/// <summary>
/// 进程登记三态核验的全项目唯一判定函数(S5-D 由 ProcessSupervisor 提取,
/// 供恢复流程与对账器共用同一语义,禁止另写实现导致判定漂移)。
///
/// 结论四类(ProcessVerdict):
/// - Matched:PID 存在且启动时间 ±5s 容差内且命令签名一致 → 唯一允许终止的状态。
/// - Mismatched:PID 存在但特征明确不符(启动时间/签名)→ 只删登记,不终止。
/// - Gone:进程明确不存在(快照未命中)。
/// - Unverifiable:查询失败或特征不可得 → 一律 fail-closed 保留登记、不动作。
/// </summary>
public static class ProcessVerifier
{
    /// <summary>启动时间容差(±5 秒),覆盖时钟/文件时间精度误差。</summary>
    public const double StartTimeToleranceSeconds = 5.0;

    /// <summary>三态核验:matched 要求 PID 存在 + 启动时间 ±5s + 签名一致,特征缺一即不可核验。</summary>
    public static ProcessVerdict Verify(ProcessRegistryEntry entry, ProcessProbeResult probe)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(probe);

        if (probe.Liveness == ProcessLiveness.Gone)
        {
            return ProcessVerdict.Gone;
        }

        if (probe.Liveness == ProcessLiveness.Unknown)
        {
            return ProcessVerdict.Unverifiable;
        }

        // Alive:特征必须齐全,否则不可核验(fail-closed)。
        if (!probe.StartedAt.HasValue || string.IsNullOrEmpty(probe.ExePath))
        {
            return ProcessVerdict.Unverifiable;
        }

        bool timeOk = Math.Abs((probe.StartedAt.Value - entry.StartedAt).TotalSeconds) <= StartTimeToleranceSeconds;
        bool signatureOk = string.Equals(
            ProcessSignature.Compute(probe.ExePath), entry.CommandSignature, StringComparison.OrdinalIgnoreCase);
        return timeOk && signatureOk ? ProcessVerdict.Matched : ProcessVerdict.Mismatched;
    }
}
