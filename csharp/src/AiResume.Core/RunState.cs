namespace AiResume.Core;

/// <summary>
/// 权威运行状态。合法迁移表见 <see cref="RunStateMachine"/>,
/// 语义唯一真身为 docs/RUN-CONTRACT.md 第 4 节。
/// </summary>
public enum RunState
{
    Queued,
    Starting,
    Running,
    Succeeded,
    FailedProvider,
    FailedLocal,
    Cancelled,
}

/// <summary>
/// RunState 与传输线格式(枚举字符串)之间的稳定映射,以及状态机迁移表。
/// </summary>
public static class RunStateMachine
{
    private static readonly Dictionary<RunState, RunState[]> LegalTransitions = new()
    {
        [RunState.Queued] = new[] { RunState.Starting, RunState.Cancelled },
        [RunState.Starting] = new[] { RunState.Running, RunState.FailedProvider, RunState.FailedLocal, RunState.Cancelled },
        [RunState.Running] = new[] { RunState.Succeeded, RunState.FailedProvider, RunState.FailedLocal, RunState.Cancelled },
        [RunState.Succeeded] = Array.Empty<RunState>(),
        [RunState.FailedProvider] = Array.Empty<RunState>(),
        [RunState.FailedLocal] = Array.Empty<RunState>(),
        [RunState.Cancelled] = Array.Empty<RunState>(),
    };

    /// <summary>返回不可变迁移表(测试与工具使用)。</summary>
    public static IReadOnlyDictionary<RunState, RunState[]> Transitions =>
        LegalTransitions.ToDictionary(kv => kv.Key, kv => (RunState[])kv.Value.Clone());

    public static bool CanTransition(RunState from, RunState to) =>
        LegalTransitions.TryGetValue(from, out var targets) && Array.IndexOf(targets, to) >= 0;

    public static bool IsTerminal(RunState state) =>
        state is RunState.Succeeded or RunState.FailedProvider or RunState.FailedLocal or RunState.Cancelled;

    /// <summary>仅在合法时推进状态;失败时保持原状态并返回 false。</summary>
    public static bool TryTransition(ref RunState current, RunState next)
    {
        if (!CanTransition(current, next))
        {
            return false;
        }

        current = next;
        return true;
    }

    public static string ToWireCode(this RunState state) => state switch
    {
        RunState.Queued => "queued",
        RunState.Starting => "starting",
        RunState.Running => "running",
        RunState.Succeeded => "succeeded",
        RunState.FailedProvider => "failed_provider",
        RunState.FailedLocal => "failed_local",
        RunState.Cancelled => "cancelled",
        _ => throw new ArgumentOutOfRangeException(nameof(state), state, "未知 RunState。"),
    };

    public static bool TryFromWireCode(string? code, out RunState state)
    {
        switch (code)
        {
            case "queued": state = RunState.Queued; return true;
            case "starting": state = RunState.Starting; return true;
            case "running": state = RunState.Running; return true;
            case "succeeded": state = RunState.Succeeded; return true;
            case "failed_provider": state = RunState.FailedProvider; return true;
            case "failed_local": state = RunState.FailedLocal; return true;
            case "cancelled": state = RunState.Cancelled; return true;
            default: state = default; return false;
        }
    }
}
