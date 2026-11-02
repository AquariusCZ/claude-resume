using AiResume.Core;
using AiResume.Storage;

namespace AiResume.Worker.Products;

/// <summary>取证后的决策:保持监视、开始续跑，或(一次性周期全部项目已完成时)提交完成。</summary>
public enum ProbeDecision
{
    KeepWatching,
    StartResuming,
    CompleteCycle,
}

/// <summary>续跑单项目完成后的下一步(对齐现役 checker.ps1 分支)。</summary>
public enum ProjectOutcome
{
    /// <summary>正常更新(成功/普通失败),继续下一项目。</summary>
    Continue,

    /// <summary>续跑中被判限流,回到等待(停止本轮)。</summary>
    BackToWaiting,

    /// <summary>refire ≥6(误判死循环防护),该项目标记 error,继续其余项目。</summary>
    MarkedError,

    /// <summary>布防周期已变化,停止本轮且不写状态。</summary>
    CycleSuperseded,

    /// <summary>项目出现不可自动重放的结果,停止本轮并等待用户重新布防。</summary>
    Blocked,
}

/// <summary>Complete 的完成语义(对齐现役 Complete-CcuCycle)。</summary>
public enum CycleCompletionKind
{
    /// <summary>一次性完成 → 解除布防(现役会写生产 config;shadow 阶段只返回语义)。</summary>
    Disarmed,

    /// <summary>连续模式 → 保持布防,等待下一轮。</summary>
    Continuous,

    /// <summary>周期已变化/未启用 → 本周期作废。</summary>
    Superseded,

    /// <summary>本周期出现失败、停止或未确认结果;保持布防和结果可见,禁止自动重放。</summary>
    Blocked,
}

/// <summary>
/// S5-C 布防周期状态机(shadow,纯逻辑;注入时钟/存储)。语义对齐现役 checker.ps1 主循环:
/// cycleId = config.armCycleId,周期变化即失效;phase=idle/waiting/resuming/done;
/// 探测节奏:可用 → probeIntervalMinutes(默认 15,≥2 校验),限流 → 4 分钟;
/// realReset 字段只取服务端精确值(仅覆盖,不清零);sawLimited/limitedRefires(≥6 防死循环);
/// 未限流过就可用 → 保持布防继续监视(不误解除)。每次状态写前校验周期活跃,失效不写。
/// shadow 模式零生产写入:Complete 只判定语义,解除布防的 config 写由现役完成。
/// </summary>
public sealed class CheckerCycle
{
    public const int LimitedCadenceMinutes = 4;
    public const int MaxLimitedRefires = 6;
    public const int DefaultProbeIntervalMinutes = 15;

    private readonly ProductStateStore _store;
    private readonly Func<DateTimeOffset> _clock;

    public CheckerCycle(ProductStateStore store, Func<DateTimeOffset>? clock = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    /// <summary>周期活跃校验:config 启用且 armCycleId 与给定 cycleId 一致。</summary>
    public bool TestCycleActive(ProductConfig config, string cycleId)
    {
        ArgumentNullException.ThrowIfNull(config);
        return config.Enabled && !string.IsNullOrEmpty(config.ArmCycleId) && config.ArmCycleId == cycleId;
    }

    /// <summary>
    /// 节奏判定:距上次探测 ≥ (sawLimited ? 4 分钟 : probeIntervalMinutes);
    /// probeIntervalMinutes 默认 15 且 ≥2 校验(对齐现役);从未探测过 → 立即。
    /// </summary>
    public bool ShouldProbe(ProductConfig config, CheckerState state)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(state);

        if (RequiresExplicitRearm(state))
        {
            return false;
        }

        int interval = DefaultProbeIntervalMinutes;
        if (config.ProbeIntervalMinutes >= 2)
        {
            interval = config.ProbeIntervalMinutes;
        }

        double minGapMinutes = state.SawLimited ? LimitedCadenceMinutes : interval;
        if (state.LastProbeUtc is null)
        {
            return true;
        }

        return (_clock() - state.LastProbeUtc.Value).TotalMinutes >= minGapMinutes;
    }

    /// <summary>
    /// 周期初始化(对齐 Initialize-CcuCycleState):state 落后于 config 周期时重置
    /// 周期字段并持久化;已对齐(幂等)或 config 未启用/无周期 → false。
    /// </summary>
    public bool Initialize(ProductConfig config, CheckerState state)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(state);

        if (!config.Enabled || string.IsNullOrEmpty(config.ArmCycleId))
        {
            return false;
        }

        if (state.CycleId == config.ArmCycleId)
        {
            return true; // 已初始化,幂等。
        }

        if (!TestCycleActive(config, config.ArmCycleId))
        {
            return false;
        }

        state.CycleId = config.ArmCycleId;
        state.Phase = CheckerState.PhaseWaiting;
        state.ProjectStatus = new Dictionary<string, string>();
        state.ProjectNotes = new Dictionary<string, ProjectNote>();
        state.ConsumedInterruptions = new Dictionary<string, string>();
        state.SawLimited = false;
        state.LastProbeReason = string.Empty;
        state.LimitedRefires = 0;
        state.ProjectLimitedRefires = null;
        state.ReplayBlocked = false;
        ClearActiveRun(state);
        SaveChecked(config, state);
        return true;
    }

    /// <summary>探测前打点:lastProbeUtc=now 并持久化;周期失效 → false(不写、不改)。</summary>
    public bool MarkProbeAttempt(ProductConfig config, CheckerState state)
    {
        ArgumentNullException.ThrowIfNull(state);

        if (!TestCycleActive(config, state.CycleId))
        {
            return false;
        }

        state.LastProbeUtc = _clock();
        return SaveChecked(config, state);
    }

    /// <summary>探测结果 limited:新限流清上一周期逐项目结果与 refire 计数;
    /// sawLimited=true,phase=waiting;realReset 仅覆盖服务端下发值。周期失效 → false。</summary>
    public bool OnLimited(ProductConfig config, CheckerState state, ClaudeProbeResult probe)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(probe);

        if (!TestCycleActive(config, state.CycleId))
        {
            return false;
        }

        state.LastProbeReason = "limited";
        if (RequiresExplicitRearm(state))
        {
            state.ReplayBlocked = true;
            state.Phase = CheckerState.PhaseBlocked;
            ApplyRealReset(state, probe);
            return SaveChecked(config, state);
        }

        if (!state.SawLimited)
        {
            state.ProjectStatus = new Dictionary<string, string>();
            state.LimitedRefires = 0;
            state.ProjectLimitedRefires = null;
        }

        state.SawLimited = true;
        state.Phase = CheckerState.PhaseWaiting;
        ClearActiveRun(state);
        ApplyRealReset(state, probe);
        return SaveChecked(config, state);
    }

    /// <summary>
    /// 探测结果 ready:未观测到过限流 → 保持布防继续监视(不解除、不续跑,现役"布防先于限流"流程);
    /// 观测到过限流 → 恢复触发,phase=resuming,返回 StartResuming。周期失效 → KeepWatching。
    /// </summary>
    public ProbeDecision OnReady(ProductConfig config, CheckerState state, ClaudeProbeResult probe)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(probe);

        if (!TestCycleActive(config, state.CycleId))
        {
            return ProbeDecision.KeepWatching;
        }

        state.LastProbeReason = "ok";
        ApplyRealReset(state, probe);

        if (RequiresExplicitRearm(state))
        {
            state.ReplayBlocked = true;
            state.Phase = CheckerState.PhaseBlocked;
            SaveChecked(config, state);
            return ProbeDecision.KeepWatching;
        }

        if (!state.SawLimited)
        {
            state.Phase = CheckerState.PhaseWaiting;
            ClearActiveRun(state);
            SaveChecked(config, state);
            return ProbeDecision.KeepWatching;
        }

        state.Phase = CheckerState.PhaseResuming;
        ClearActiveRun(state);
        return SaveChecked(config, state) ? ProbeDecision.StartResuming : ProbeDecision.KeepWatching;
    }

    /// <summary>探测结果未就绪(非 limited):phase=waiting,下次重试(fail-closed 不误触发)。</summary>
    public bool OnNotReady(ProductConfig config, CheckerState state, ClaudeProbeResult probe)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(probe);

        if (!TestCycleActive(config, state.CycleId))
        {
            return false;
        }

        // 原因码原样保留(限长、只留安全字符):压成 unknown 会让日志与界面都说不清卡在哪(2026-10-20 实证)。
        state.LastProbeReason = SanitizeReason(probe.Reason);
        if (RequiresExplicitRearm(state))
        {
            state.ReplayBlocked = true;
            state.Phase = CheckerState.PhaseBlocked;
        }
        else
        {
            state.Phase = CheckerState.PhaseWaiting;
        }
        ClearActiveRun(state);
        return SaveChecked(config, state);
    }

    /// <summary>
    /// 逐项目取证结果的统一入口(<see cref="ClaudeProbeResult.Projects"/> 非 null);为 null 时退回旧的整体语义。
    /// <list type="bullet">
    /// <item>待续跑(status=limited):会话尾部是 AI Resume 尚未续过的限额中断;没有会话证据的项目(Codex)
    ///   沿用旧语义——观测到它的模型受限。</item>
    /// <item>曾待续跑、而会话尾部已有新应答 → continued-elsewhere:用户在客户端或手机上续上了，算完成。</item>
    /// <item>连续模式下已完成的项目再次被打断会重新入队;一次性模式每个项目最多续一次。</item>
    /// <item>存在「待续跑且三维都放行」的项目 → StartResuming;一次性周期全部项目完成 → CompleteCycle。</item>
    /// </list>
    /// 从未被打断的项目让一次性周期保持监视：宁可多守一会儿，也不能在它稍后被打断时已经解除布防。
    /// </summary>
    public ProbeDecision OnProbe(ProductConfig config, CheckerState state, ClaudeProbeResult probe)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(probe);

        if (probe.Projects is not { } projects)
        {
            if (probe.IsLimited)
            {
                OnLimited(config, state, probe);
                return ProbeDecision.KeepWatching;
            }

            if (probe.Ready)
            {
                return OnReady(config, state, probe);
            }

            OnNotReady(config, state, probe);
            return ProbeDecision.KeepWatching;
        }

        if (!TestCycleActive(config, state.CycleId))
        {
            return ProbeDecision.KeepWatching;
        }

        ApplyRealReset(state, probe);
        if (RequiresExplicitRearm(state))
        {
            state.LastProbeReason = "blocked";
            state.ReplayBlocked = true;
            state.Phase = CheckerState.PhaseBlocked;
            SaveChecked(config, state);
            return ProbeDecision.KeepWatching;
        }

        state.ProjectStatus ??= new Dictionary<string, string>();
        var notes = new Dictionary<string, ProjectNote>();
        long now = _clock().ToUnixTimeSeconds();
        bool anyPending = false;
        bool anyStartable = false;
        bool allDone = config.Selected.Count > 0;
        var waitingReasons = new List<string>();
        foreach (ProjectRef project in config.Selected)
        {
            ProjectAdmission? admission = projects.FirstOrDefault(item => ProjectPaths.Equal(item.Path, project.Path));
            string key = ProjectPaths.KeyFor(state.ProjectStatus, project.Path);
            string? status = state.ProjectStatus.TryGetValue(key, out string? current) ? current : null;
            bool done = status is "success" or "continued-elsewhere";
            if (admission is not null)
            {
                if (admission.Evidence == "superseded" && !string.IsNullOrEmpty(admission.InterruptionId))
                {
                    // 换会话接着干是这次中断的结局：记为已处理。之后那个新会话被删、或某一拍读不到时，
                    // 旧会话的尾部仍是这条中断，也不能回头拿旧上下文去续(连续模式下已完成的项目会重新入队)。
                    ConsumeInterruption(state, project.Path, admission.InterruptionId);
                }

                string? consumed = ProjectPaths.Find(state.ConsumedInterruptions, project.Path);
                bool pendingNow = admission.Evidence == "interrupted"
                    ? !string.Equals(admission.InterruptionId, consumed, StringComparison.Ordinal)
                    : admission.Evidence == "none" && admission.Quota == "limited";
                if (pendingNow && (!done || config.Continuous))
                {
                    status = "limited";
                    done = false;
                    state.ProjectStatus[key] = status;
                }
                else if (status == "limited" && admission.Evidence is "clear" or "superseded")
                {
                    status = "continued-elsewhere";
                    done = true;
                    state.ProjectStatus[key] = status;
                }
            }

            bool pending = status == "limited";
            anyPending |= pending;
            anyStartable |= pending && admission?.Admissible == true;
            allDone &= done;
            ProjectNote note = Describe(admission, status, now);
            if (note.Reason == "ready" &&
                ProjectPaths.Find(state.ProjectNotes, project.Path) is { } previous &&
                previous.Reason.StartsWith("not-started:", StringComparison.Ordinal))
            {
                // 上次没能启动的原因在下一次真正尝试前一直有效;额度放行不等于那个原因已经消失。
                note = previous;
            }

            notes[key] = note;
            if (pending && note.Reason != "ready")
            {
                waitingReasons.Add(note.Reason);
            }
        }

        state.ProjectNotes = notes;
        if (anyPending && !state.SawLimited)
        {
            state.LimitedRefires = 0;
            state.ProjectLimitedRefires = null;
        }

        state.SawLimited = anyPending;
        state.LastProbeReason = anyStartable ? "ok"
            : anyPending ? (waitingReasons.FirstOrDefault() ?? "unknown")
            : "watching";
        state.Phase = anyStartable ? CheckerState.PhaseResuming : CheckerState.PhaseWaiting;
        ClearActiveRun(state);
        if (!SaveChecked(config, state))
        {
            return ProbeDecision.KeepWatching;
        }

        return anyStartable ? ProbeDecision.StartResuming
            : allDone && !config.Continuous ? ProbeDecision.CompleteCycle
            : ProbeDecision.KeepWatching;
    }

    /// <summary>本轮没有可启动的项目了:相位回到等待(原因已逐项目记下)。周期失效或已锁存时不写。</summary>
    public bool EndRound(ProductConfig config, CheckerState state)
    {
        ArgumentNullException.ThrowIfNull(state);

        if (!TestCycleActive(config, state.CycleId) || state.Phase != CheckerState.PhaseResuming || RequiresExplicitRearm(state))
        {
            return false;
        }

        state.Phase = CheckerState.PhaseWaiting;
        ClearActiveRun(state);
        return SaveChecked(config, state);
    }

    /// <summary>记录一个项目这次没能启动的原因(未启动不是失败：不锁周期，下一拍重新评估)。</summary>
    public bool NoteProject(ProductConfig config, CheckerState state, string path, string reason, string? detail = null)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(path);

        if (!TestCycleActive(config, state.CycleId))
        {
            return false;
        }

        state.ProjectNotes ??= new Dictionary<string, ProjectNote>();
        string key = ProjectPaths.KeyFor(state.ProjectStatus, path);
        ProjectNote? previous = ProjectPaths.Find(state.ProjectNotes, path);
        state.ProjectNotes[key] = new ProjectNote
        {
            Reason = reason,
            Detail = detail is { Length: > 160 } ? detail[..160] : detail,
            ResetAtUnix = previous?.ResetAtUnix,
            InterruptedAtUnix = previous?.InterruptedAtUnix,
            Client = previous?.Client,
            AtUnix = _clock().ToUnixTimeSeconds(),
        };
        return SaveChecked(config, state);
    }

    /// <summary>
    /// 在 spawn 前把 running、项目路径与精确 RunId 一次性持久化;同一次写入里记下本次要续的限额中断，
    /// 保证同一次中断最多续一次(即使 Worker 在运行中崩溃)。
    /// </summary>
    public bool PrepareActiveRun(ProductConfig config, CheckerState state, string path, RunId runId, string? interruptionId = null)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(path);

        if (!TestCycleActive(config, state.CycleId) || RequiresExplicitRearm(state))
        {
            return false;
        }

        state.ProjectStatus ??= new Dictionary<string, string>();
        state.ProjectStatus[ProjectPaths.KeyFor(state.ProjectStatus, path)] = "running";
        if (!string.IsNullOrEmpty(interruptionId))
        {
            ConsumeInterruption(state, path, interruptionId);
        }

        state.ProjectNotes ??= new Dictionary<string, ProjectNote>();
        state.ProjectNotes[ProjectPaths.KeyFor(state.ProjectStatus, path)] = new ProjectNote
        {
            Reason = "running",
            AtUnix = _clock().ToUnixTimeSeconds(),
        };
        state.ActiveProjectPath = path;
        state.ActiveRunId = runId.ToString();
        return SaveChecked(config, state);
    }

    /// <summary>
    /// supervisor 明确在任何登记/spawn 前取消时，撤销同一个 RunId 的 product_state
    /// 预登记。精确 id、项目或 running 状态任一不匹配都拒绝回滚。
    /// </summary>
    public bool RollbackPreparedRun(
        ProductConfig config,
        CheckerState state,
        string path,
        RunId runId,
        string? interruptionId = null)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(path);

        string key = ProjectPaths.KeyFor(state.ProjectStatus, path);
        if (!TestCycleActive(config, state.CycleId) ||
            !string.Equals(state.ActiveRunId, runId.ToString(), StringComparison.Ordinal) ||
            !ProjectPaths.Equal(state.ActiveProjectPath, path) ||
            state.ProjectStatus is null ||
            !state.ProjectStatus.TryGetValue(key, out string? status) ||
            !string.Equals(status, "running", StringComparison.Ordinal))
        {
            return false;
        }

        // 没有 spawn 就没有副作用：项目回到待续跑，本次中断也没有被消费。
        state.ProjectStatus[key] = "limited";
        if (!string.IsNullOrEmpty(interruptionId) && state.ConsumedInterruptions is { } consumed)
        {
            string consumedKey = ProjectPaths.KeyFor(consumed, path);
            if (consumed.TryGetValue(consumedKey, out string? marked) && marked == interruptionId)
            {
                consumed.Remove(consumedKey);
            }
        }

        state.Phase = CheckerState.PhaseWaiting;
        ClearActiveRun(state);
        return SaveChecked(config, state);
    }

    /// <summary>同一次限额中断(uuid)只处理一次：续过，或用户已在别处接着干。只改内存中的 state,由调用方一并保存。</summary>
    private static void ConsumeInterruption(CheckerState state, string path, string interruptionId)
    {
        state.ConsumedInterruptions ??= new Dictionary<string, string>();
        state.ConsumedInterruptions[ProjectPaths.KeyFor(state.ConsumedInterruptions, path)] = interruptionId;
    }

    /// <summary>把升级前遗留的失败/停止/running 等状态锁存为当前周期阻断。</summary>
    public bool LatchReplayBlock(ProductConfig config, CheckerState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (!TestCycleActive(config, state.CycleId) || !RequiresExplicitRearm(state))
        {
            return false;
        }

        state.ReplayBlocked = true;
        state.Phase = CheckerState.PhaseBlocked;
        ClearActiveRun(state);
        return SaveChecked(config, state);
    }

    /// <summary>
    /// 续跑单项目完成:更新 pstat;status=limited 时按项目计 refire(同一项目连续 ≥6 次 → 该项目标记 error、
    /// 周期锁存,防误判死循环;否则这个项目回等待,引擎本轮跳过它继续别的项目)。周期失效 → CycleSuperseded(不写)。
    /// continued-elsewhere 带上 <paramref name="interruptionId"/> 时把那次中断记为已处理(同一次写入)。
    /// </summary>
    public ProjectOutcome ApplyProjectResult(
        ProductConfig config,
        CheckerState state,
        string path,
        string status,
        bool stopRound = false,
        string? interruptionId = null)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(status);

        if (!TestCycleActive(config, state.CycleId))
        {
            return ProjectOutcome.CycleSuperseded;
        }

        state.ProjectStatus ??= new Dictionary<string, string>();
        string key = ProjectPaths.KeyFor(state.ProjectStatus, path);
        state.ProjectStatus[key] = status;
        if (status == "continued-elsewhere" && !string.IsNullOrEmpty(interruptionId))
        {
            ConsumeInterruption(state, path, interruptionId);
        }
        state.ProjectNotes ??= new Dictionary<string, ProjectNote>();
        state.ProjectNotes[key] = new ProjectNote
        {
            Reason = status switch
            {
                "success" => "done",
                "continued-elsewhere" => "continued-elsewhere",
                "limited" => "quota:limited",
                "running" => "running",
                _ => "blocked:" + status,
            },
            AtUnix = _clock().ToUnixTimeSeconds(),
        };
        if (status is not "success" and not "limited" and not "running" and not "continued-elsewhere")
        {
            state.ReplayBlocked = true;
            state.Phase = CheckerState.PhaseBlocked;
        }

        if (status == "success")
        {
            // 连续误判计数只数「额度说可用、一跑就被限流」的连续次数;成功一次即清零(逐项目的只清这个项目)。
            state.LimitedRefires = 0;
            if (state.ProjectLimitedRefires is { } refires && ProjectPaths.KeyFor(refires, path) is { } refireKey)
            {
                refires.Remove(refireKey);
            }
        }
        if (!string.Equals(status, "running", StringComparison.Ordinal) &&
            !string.Equals(status, "cancel-pending", StringComparison.Ordinal) &&
            string.Equals(state.ActiveProjectPath, path, StringComparison.OrdinalIgnoreCase))
        {
            ClearActiveRun(state);
        }
        if (stopRound)
        {
            state.ReplayBlocked = true;
            state.Phase = CheckerState.PhaseBlocked;
        }
        if (status != "limited")
        {
            SaveChecked(config, state);
            return state.ReplayBlocked ? ProjectOutcome.Blocked : ProjectOutcome.Continue;
        }

        state.LimitedRefires++;
        // 升级前的状态只有全周期计数:第一次按项目记时用它作这个项目的起点,升级不会让在途的误判循环重新数起。
        bool legacyCounter = state.ProjectLimitedRefires is null;
        state.ProjectLimitedRefires ??= new Dictionary<string, int>();
        string projectRefireKey = ProjectPaths.KeyFor(state.ProjectLimitedRefires, path);
        int projectRefires = (state.ProjectLimitedRefires.TryGetValue(projectRefireKey, out int counted) ? counted
            : legacyCounter ? Math.Max(0, state.LimitedRefires - 1) : 0) + 1;
        state.ProjectLimitedRefires[projectRefireKey] = projectRefires;
        if (projectRefires >= MaxLimitedRefires)
        {
            // 真实账户级限流等待约 5 小时/次,快速增长的 refire 说明是误判 → 标记 error 继续。
            state.ProjectStatus[key] = "error";
            state.ProjectNotes[key].Reason = "blocked:repeated-limit";
            state.ReplayBlocked = true;
            state.Phase = CheckerState.PhaseBlocked;
            SaveChecked(config, state);
            return ProjectOutcome.Blocked;
        }

        state.SawLimited = true;
        state.Phase = CheckerState.PhaseWaiting;
        SaveChecked(config, state);
        return ProjectOutcome.BackToWaiting;
    }

    /// <summary>
    /// 完成语义(对齐 Complete-CcuCycle):周期失效/未启用 → Superseded;连续模式 → Continuous;
    /// 否则 → Disarmed(一次性完成,现役会解除生产布防;shadow 阶段只返回语义,不写生产)。
    /// 周期有效时先收尾 state(phase=done、sawLimited=false、limitedRefires=0)并持久化。
    /// </summary>
    public CycleCompletionKind Complete(ProductConfig config, CheckerState state)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(state);

        if (!TestCycleActive(config, state.CycleId))
        {
            return CycleCompletionKind.Superseded;
        }

        if (RequiresExplicitRearm(state))
        {
            state.ReplayBlocked = true;
            state.Phase = CheckerState.PhaseBlocked;
            ClearActiveRun(state);
            SaveChecked(config, state);
            return CycleCompletionKind.Blocked;
        }

        state.Phase = CheckerState.PhaseDone;
        state.SawLimited = false;
        state.LimitedRefires = 0;
        state.ProjectLimitedRefires = null;
        state.ReplayBlocked = false;
        ClearActiveRun(state);
        SaveChecked(config, state);

        if (config.Continuous)
        {
            return CycleCompletionKind.Continuous;
        }

        return CycleCompletionKind.Disarmed;
    }

    /// <summary>realReset 字段只覆盖服务端下发值(低利用率探测不清零好值)。</summary>
    private void ApplyRealReset(CheckerState state, ClaudeProbeResult probe)
    {
        if (probe.FiveHourResetUtc is not null)
        {
            state.RealFiveHourResetUtc = probe.FiveHourResetUtc;
            state.RealResetProbedUtc = _clock();
            if (probe.FiveHourUtil is not null)
            {
                state.RealFiveHourUtil = probe.FiveHourUtil;
            }
        }

        if (probe.SevenDayResetUtc is not null)
        {
            state.RealSevenDayResetUtc = probe.SevenDayResetUtc;
            state.RealResetProbedUtc = _clock();
        }
    }

    private static void ClearActiveRun(CheckerState state)
    {
        state.ActiveRunId = string.Empty;
        state.ActiveProjectPath = string.Empty;
    }

    private static bool RequiresExplicitRearm(CheckerState state) =>
        state.ReplayBlocked ||
        state.ProjectStatus?.Values.Any(status => status is not "success" and not "limited" and not "continued-elsewhere") == true;

    private static string SanitizeReason(string? reason)
    {
        if (string.IsNullOrWhiteSpace(reason))
        {
            return "unknown";
        }

        string safe = new(reason.Where(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or ':' or '.').Take(64).ToArray());
        return safe.Length == 0 ? "unknown" : safe;
    }

    /// <summary>把一个项目的准入事实与当前状态压成界面可读的原因码。</summary>
    private static ProjectNote Describe(ProjectAdmission? admission, string? status, long now)
    {
        var note = new ProjectNote
        {
            AtUnix = now,
            InterruptedAtUnix = admission?.InterruptedAtUtc?.ToUnixTimeSeconds(),
            Client = admission?.SessionClient,
        };
        if (status is "success" or "continued-elsewhere" or "running")
        {
            note.Reason = status == "success" ? "done" : status;
            // 区分「这个会话自己被续上」与「同目录新开的会话接着干了」,界面说法不同。
            note.Detail = status == "continued-elsewhere" && admission?.Evidence == "superseded" ? "other-session" : null;
            return note;
        }

        if (admission is null)
        {
            note.Reason = "unevaluated";
            return note;
        }

        if (status == "limited")
        {
            if (admission.Quota != "ok")
            {
                note.Reason = "quota:" + admission.Quota;
                note.Detail = admission.QuotaDetail;
                note.ResetAtUnix = (admission.QuotaResetUtc ?? admission.LimitResetsAtUtc)?.ToUnixTimeSeconds();
            }
            else
            {
                note.Reason = admission.Session switch
                {
                    "in-use" => "session-in-use",
                    "cwd-busy" => "cwd-busy",
                    "unverified" => "session-unverified",
                    _ => admission.Evidence == "unverified" ? "evidence-unverified" : "ready",
                };
            }

            return note;
        }

        note.Reason = admission.Evidence switch
        {
            "clear" => "not-interrupted",
            "unverified" => "evidence-unverified",
            _ => "watching",
        };
        return note;
    }

    /// <summary>周期活跃才持久化;失效返回 false 且不写(对齐现役 Save-ThisCycleState)。</summary>
    private bool SaveChecked(ProductConfig config, CheckerState state)
    {
        if (!TestCycleActive(config, state.CycleId))
        {
            return false;
        }

        _store.Save(state);
        return true;
    }
}
