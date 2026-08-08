namespace AiResume.Core.Events;

/// <summary>
/// 事件信封 v1 强类型。字段对齐 docs/EVENT-CONTRACTS.md(Stage 2 先落 v1 骨架,
/// 目标 v2 演进在后续工作包);deadline_ms 仅作兼容字段,恒为 0,绝不构成总时限。
/// </summary>
public sealed record EventEnvelopeV1
{
    public const string EnvelopeVersionValue = "1";

    public string EnvelopeVersion => EnvelopeVersionValue;

    /// <summary>本记录唯一 ID,生成后不可变。</summary>
    public Guid EventId { get; init; }

    /// <summary>命令/事件类型(如 run.start.requested / run.state_changed)。</summary>
    public string Type { get; init; } = string.Empty;

    /// <summary>来源:worker|gui|cc-connect|lark-cli|hook-*|feishu。</summary>
    public string Source { get; init; } = string.Empty;

    /// <summary>Unix 毫秒 UTC。</summary>
    public long Ts { get; init; }

    public string IdempotencyKey { get; init; } = string.Empty;

    /// <summary>类型化内容;禁止机密与完整命令行。</summary>
    public object? Payload { get; init; }

    public Guid? RunId { get; init; }

    /// <summary>run 内事件序号;命令可为 0。</summary>
    public long Seq { get; init; }

    public string? Actor { get; init; }
    public string? CorrelationId { get; init; }
    public string? CausationId { get; init; }
    public int Attempt { get; init; }

    /// <summary>兼容字段,恒 0;任何实现不得读取为运行时限。</summary>
    public long DeadlineMs => 0;
}
