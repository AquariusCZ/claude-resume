using AiResume.Core;
using AiResume.Storage;

namespace AiResume.Worker.Products;

/// <summary>OnReady 的决策:保持监视(未限流过/周期失效)或开始续跑。</summary>
public enum ProbeDecision
{
    KeepWatching,
    StartResuming,
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
        state.SawLimited = false;
        state.LimitedRefires = 0;
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

        if (!state.SawLimited)
        {
            state.ProjectStatus = new Dictionary<string, string>();
            state.LimitedRefires = 0;
        }

        state.SawLimited = true;
        state.Phase = CheckerState.PhaseWaiting;
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

        ApplyRealReset(state, probe);

        if (!state.SawLimited)
        {
            state.Phase = CheckerState.PhaseWaiting;
            SaveChecked(config, state);
            return ProbeDecision.KeepWatching;
        }

        state.Phase = CheckerState.PhaseResuming;
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

        state.Phase = CheckerState.PhaseWaiting;
        return SaveChecked(config, state);
    }

    /// <summary>
    /// 续跑单项目完成:更新 pstat;status=limited 时 refire 计数(≥6 → 该项目标记 error 继续,
    /// 防误判死循环;否则回等待)。周期失效 → CycleSuperseded(不写)。
    /// </summary>
    public ProjectOutcome ApplyProjectResult(ProductConfig config, CheckerState state, string path, string status)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(status);

        if (!TestCycleActive(config, state.CycleId))
        {
            return ProjectOutcome.CycleSuperseded;
        }

        state.ProjectStatus ??= new Dictionary<string, string>();
        state.ProjectStatus[path] = status;
        SaveChecked(config, state);

        if (status != "limited")
        {
            return ProjectOutcome.Continue;
        }

        state.LimitedRefires++;
        if (state.LimitedRefires >= MaxLimitedRefires)
        {
            // 真实账户级限流等待约 5 小时/次,快速增长的 refire 说明是误判 → 标记 error 继续。
            state.ProjectStatus[path] = "error";
            SaveChecked(config, state);
            return ProjectOutcome.MarkedError;
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

        state.Phase = CheckerState.PhaseDone;
        state.SawLimited = false;
        state.LimitedRefires = 0;
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
