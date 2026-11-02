namespace AiResume.Core;

/// <summary>
/// S5-B Claude 限额探测结果(契约)。语义对齐现役 Test-ClaudeReady(checker.ps1):
/// ready=true 仅当服务端确认可用;reason 携带判定分类;resetsAt/utilization 只取服务端
/// 精确值,禁止本地估算(resetsAt 仅在 blocked 或利用率越过 ~0.75 时由服务端下发)。
/// 输出文本脱敏后丢弃,本对象只保留结构化结果与字节计数。
/// </summary>
public sealed record ClaudeProbeResult
{
    /// <summary>服务端确认可用为 true(唯一成功信号)。</summary>
    public bool Ready { get; init; }

    /// <summary>判定分类:ok/limited/auth/billing/model_unavailable/transient/no-claude/
    /// timeout/spawn-failed/cancelled/exit-N/unknown。</summary>
    public string Reason { get; init; } = "unknown";

    /// <summary>服务端精确 5 小时窗口重置时间(unix 秒),仅限流/接近限流时下发。</summary>
    public DateTimeOffset? FiveHourResetUtc { get; init; }

    /// <summary>服务端精确 7 天窗口重置时间(unix 秒)。</summary>
    public DateTimeOffset? SevenDayResetUtc { get; init; }

    /// <summary>服务端 5 小时窗口利用率(0..1)。</summary>
    public double? FiveHourUtil { get; init; }

    /// <summary>服务端 7 天窗口利用率(0..1)。</summary>
    public double? SevenDayUtil { get; init; }

    /// <summary>挡住目标模型的窗口里最晚的重置时间(服务端值);未受限或未知时为空。</summary>
    public DateTimeOffset? BlockingResetUtc { get; init; }

    /// <summary>探测输出字节数(stdout+stderr,含重定向包装开销)。</summary>
    public long OutputBytes { get; init; }

    /// <summary>未就绪/限流的具体窗口或原因(如 <c>weekly_scoped:Fable:carried</c>),只含窗口名与状态码，供日志与界面说明。</summary>
    public string? Detail { get; init; }

    /// <summary>
    /// 逐项目准入事实(按 Selected 顺序)。非 null 时状态机逐项目判定，顶层字段只作兼容汇总;
    /// null 表示旧的整体语义(测试替身、未绑定目标)。
    /// </summary>
    public IReadOnlyList<ProjectAdmission>? Projects { get; init; }

    public bool IsLimited => Reason == "limited";

    public bool IsOk => Ready;
}
