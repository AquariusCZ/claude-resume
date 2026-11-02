using AiResume.Core;
using AiResume.Worker.Probes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AiResume.Worker.Quota;

/// <summary>
/// 把 <see cref="QuotaService"/> 的额度观测适配为续跑状态机使用的探测契约。
/// 续跑必须经过 OAuth 主路径及其 scoped 窗口门禁，不能再用某个探测模型的
/// 单次成功代替原会话所需额度已经恢复。
/// </summary>
public sealed class QuotaResumeProbe : IClaudeUsageProbe
{
    private readonly QuotaService _quota;
    private readonly ILogger<QuotaResumeProbe> _logger;

    public QuotaResumeProbe(
        QuotaService quota,
        ILogger<QuotaResumeProbe>? logger = null)
    {
        _quota = quota ?? throw new ArgumentNullException(nameof(quota));
        _logger = logger ?? NullLogger<QuotaResumeProbe>.Instance;
    }

    public async Task<ClaudeProbeResult> ProbeAsync(
        string model,
        string workingDirectory,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(workingDirectory);

        // CheckerCycle 已负责 4/15 分钟节奏；每次到点都必须取当前服务端证据，
        // 不能让 QuotaService 的 5 分钟 GUI 缓存跨过 reset 后的续跑判定。
        UsageSnapshot snapshot = await _quota
            .GetAsync(forceRefresh: true, cancellationToken)
            .ConfigureAwait(false);

        return Evaluate(snapshot, model, _quota.StorageWarning);
    }

    /// <summary>
    /// 针对一个目标模型评估额度证据。规则(ADR-0006):
    /// <list type="bullet">
    /// <item>相关窗口 = 两个主窗口(five_hour / seven_day 等非 scoped 窗口)
    ///   + 与目标<b>同一模型族</b>的 scoped(任何版本，同族不同版本也算，宁可多等)
    ///   + <b>认不出模型族</b>的 scoped(不知道它管不管目标，就当它管)。</item>
    /// <item>其它已知模型族的 scoped 与目标无关：Fable 周额度打满不挡 Opus 项目，反之亦然。</item>
    /// <item>一次成功的 OAuth 响应里 <c>limits</c> 列出的就是账号全部模型级上限;目标模型没有专属窗口，
    ///   就由主窗口决定(2026-10-30 体检根因 b:原规则要求目标必须有自己的 scoped 窗口，Opus 永远放行不了)。</item>
    /// <item>任一相关窗口打满或未归因限流 → limited;相关窗口全部来自本次实时观测且 0 ≤ 百分比 &lt; 100、
    ///   两个主窗口都在、无存储故障 → ok;其余(承接读数、缺百分比、模型非法、非 OAuth 证据)→ unknown。</item>
    /// </list>
    /// </summary>
    public ClaudeProbeResult Evaluate(UsageSnapshot snapshot, string model, string? storageWarning = null)
    {
        UsageWindow[] windows = snapshot.Buckets
            .SelectMany(bucket => bucket.Windows)
            .ToArray();
        UsageWindow? fiveHour = FindWindow(windows, "five_hour");
        UsageWindow? sevenDay = FindWindow(windows, "seven_day");
        bool targetModelValid = ClaudeModelFamilies.TryNormalizeConfiguredModel(model, out string family);
        UsageWindow[] relevant = windows
            .Where(window => !IsScopedWindow(window) || ScopeRelevant(window, family))
            .ToArray();
        UsageWindow[] blockedRelevant = relevant.Where(IsBlocked).ToArray();
        bool unattributedAggregateBlock = snapshot.Buckets.Any(bucket =>
            bucket.UnattributedLimitReached ||
            (bucket.LimitReached && !bucket.Windows.Any(IsBlocked)));
        bool oauth = snapshot.EvidenceSource == UsageEvidenceSource.OAuth;
        // CLI 探针可能只证明 Haiku 或另一个模型被限，不能替目标模型建立限流事实;
        // 另一个模型族的 scoped=100 与目标无关，同样不能制造“目标模型已限流”。
        bool limited = oauth && targetModelValid && (blockedRelevant.Length > 0 || unattributedAggregateBlock);

        string? notReady = !oauth ? "evidence-not-oauth"
            : !snapshot.HasData || snapshot.Buckets.Count == 0 ? "no-data"
            : snapshot.UnavailableReason is not null ? "unavailable"
            : storageWarning is not null ? "storage"
            : !targetModelValid ? "model-invalid"
            : !HasCurrentUtilization(fiveHour) ? Describe(fiveHour, "five_hour")
            : !HasCurrentUtilization(sevenDay) ? Describe(sevenDay, "seven_day")
            // 相关 scoped 被本次响应省略(承接读数)且未满时不再阻塞：一次成功响应的 limits 就是全部上限，
            // 省略说明它此刻不构成约束;承接值满额仍经 blockedRelevant 判为 limited。
            : relevant.FirstOrDefault(window => IsScopedWindow(window)
                ? !window.CarriedForward && window.UsedPercent is null
                : !HasCurrentUtilization(window)) is { } stale ? Describe(stale, stale.Name)
            : unattributedAggregateBlock ? "unattributed"
            : null;
        bool ready = !limited && notReady is null;
        string? detail = limited
            ? (blockedRelevant.Length == 0 ? "unattributed" : string.Join(",", blockedRelevant.Select(window => window.Name + ":blocked")))
            : notReady;

        if (limited)
        {
            string blocked = string.Join(",", blockedRelevant
                .Select(window => $"{window.Name}:{window.UsedPercent?.ToString() ?? "unknown"}%@{window.ResetAtUnix?.ToString() ?? "unknown"}"));
            _logger.LogInformation(
                "resume.quota.blocked 目标模型的相关额度仍未恢复,model={Model},limits={Limits}",
                model, blocked.Length == 0 ? "aggregate" : blocked);
        }
        else if (!ready)
        {
            _logger.LogWarning(
                "resume.quota.unverified 没有取得目标模型的实时可用证据,model={Model},detail={Detail},source={Source},carriedCount={CarriedCount}",
                model, detail, snapshot.EvidenceSource, windows.Count(window => window.CarriedForward));
        }

        return new ClaudeProbeResult
        {
            Ready = ready,
            Reason = limited ? "limited" : ready ? "ok" : "unknown",
            Detail = detail,
            FiveHourResetUtc = ToReset(fiveHour),
            SevenDayResetUtc = ToReset(sevenDay),
            FiveHourUtil = ToUtilization(fiveHour),
            SevenDayUtil = ToUtilization(sevenDay),
            // 要等所有挡住它的窗口都重置：取其中最晚的那个。
            BlockingResetUtc = limited
                ? blockedRelevant.Select(ToReset).Where(reset => reset is not null).Max()
                : null,
        };
    }

    private static string Describe(UsageWindow? window, string name) => window switch
    {
        null => name + ":missing",
        { CarriedForward: true } => name + ":carried",
        { UsedPercent: null } => name + ":no-percent",
        _ => name + ":blocked",
    };

    private static bool IsBlocked(UsageWindow window) =>
        window.UsedPercent is >= 100 ||
        window.Status.Equals("blocked", StringComparison.OrdinalIgnoreCase);

    private static bool IsScopedWindow(UsageWindow window) =>
        window.Name.StartsWith("weekly_scoped:", StringComparison.OrdinalIgnoreCase);

    /// <summary>scoped 窗口是否可能约束目标模型族:同族(任何版本)或认不出模型族时都算。</summary>
    private static bool ScopeRelevant(UsageWindow window, string targetFamily)
    {
        string displayName = window.Name[(window.Name.IndexOf(':') + 1)..];
        return !ClaudeModelFamilies.TryNormalizeScopeName(displayName, out string scopeFamily) ||
               targetFamily.Length == 0 ||
               scopeFamily == targetFamily;
    }

    private static bool HasCurrentUtilization(UsageWindow? window) =>
        window is { CarriedForward: false, UsedPercent: >= 0 and < 100 };

    private static UsageWindow? FindWindow(IEnumerable<UsageWindow> windows, string name) =>
        windows.FirstOrDefault(window => window.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    private static DateTimeOffset? ToReset(UsageWindow? window) =>
        window?.ResetAtUnix is { } reset ? DateTimeOffset.FromUnixTimeSeconds(reset) : null;

    private static double? ToUtilization(UsageWindow? window) =>
        window?.UsedPercent is { } percent ? percent / 100d : null;
}
