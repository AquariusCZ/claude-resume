namespace AiResume.Core.Contracts;

/// <summary>Start 幂等/并发拒绝原因。None 表示成功接纳。</summary>
public enum ConflictKind
{
    None,
    IdempotencyConflict,
    RunKeyBusy,
}

/// <summary>fallbackPolicy 取值。provider_explicit_once 是唯一允许自动 fallback 的策略。</summary>
public enum FallbackPolicy
{
    None,
    ProviderExplicitOnce,
}

/// <summary>Cancel reason;不存在 timeout reason(ADR-0002)。</summary>
public enum CancelReason
{
    UserStop,
    Disarm,
    Replaced,
    Shutdown,
}

/// <summary>进程存活三态;unknown 不等于 gone,禁止仅凭 PID 猜测。</summary>
public enum ProcessLiveness
{
    Alive,
    Gone,
    Unknown,
}
