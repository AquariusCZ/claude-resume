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

        UsageWindow[] windows = snapshot.Buckets
            .SelectMany(bucket => bucket.Windows)
            .ToArray();
        UsageWindow? fiveHour = FindWindow(windows, "five_hour");
        UsageWindow? sevenDay = FindWindow(windows, "seven_day");
        UsageWindow[] scopedWindows = windows
            .Where(IsScopedWindow)
            .ToArray();
        UsageWindow[] targetScopedWindows = scopedWindows
            .Where(window => ScopeMatchesModel(window, model))
            .ToArray();
        bool targetModelValid = ClaudeModelFamilies.TryNormalizeConfiguredModel(model, out _);
        bool relevantWindowBlocked = windows.Any(window =>
            (!IsScopedWindow(window) || ScopeMatchesModel(window, model)) &&
            IsBlocked(window));
        bool unattributedAggregateBlock = snapshot.Buckets.Any(bucket =>
            bucket.UnattributedLimitReached ||
            (bucket.LimitReached && !bucket.Windows.Any(IsBlocked)));
        // CLI 探针可能只证明 Haiku 或另一个模型被限，不能替目标模型建立 SawLimited。
        // 同理，另一个模型的 scoped=100 只能让当前证据保持 Unknown，不能制造一次
        // “目标模型已限流 -> 后续恢复”的虚假状态迁移。
        bool limited = snapshot.EvidenceSource == UsageEvidenceSource.OAuth &&
                       targetModelValid &&
                       (relevantWindowBlocked || unattributedAggregateBlock);
        bool ready = snapshot.HasData &&
                     snapshot.EvidenceSource == UsageEvidenceSource.OAuth &&
                     _quota.StorageWarning is null &&
                     snapshot.UnavailableReason is null &&
                     snapshot.Buckets.Count > 0 &&
                     snapshot.Buckets.All(bucket => bucket.Allowed && !bucket.LimitReached) &&
                     !windows.Any(IsBlocked) &&
                     HasCurrentUtilization(fiveHour) &&
                     HasCurrentUtilization(sevenDay) &&
                     targetModelValid &&
                     targetScopedWindows.Any(HasCurrentUtilization) &&
                     windows.All(HasCurrentUtilization);

        if (limited)
        {
            string blocked = string.Join(",", windows
                .Where(IsBlocked)
                .Select(window => $"{window.Name}:{window.UsedPercent?.ToString() ?? "unknown"}%@{window.ResetAtUnix?.ToString() ?? "unknown"}"));
            _logger.LogInformation(
                "resume.quota.blocked 模型绑定额度门禁仍有限额未恢复,limits={Limits}",
                blocked.Length == 0 ? "aggregate" : blocked);
        }
        else if (!ready)
        {
            _logger.LogWarning(
                "resume.quota.unverified 模型绑定额度门禁没有取得目标模型的实时 OAuth 可用证据,source={Source},storageVerified={StorageVerified},targetModelValid={TargetModelValid},targetScopeCount={TargetScopeCount},hasData={HasData},bucketCount={BucketCount},scopedCount={ScopedCount},carriedCount={CarriedCount}",
                snapshot.EvidenceSource,
                _quota.StorageWarning is null,
                targetModelValid,
                targetScopedWindows.Length,
                snapshot.HasData,
                snapshot.Buckets.Count,
                scopedWindows.Length,
                windows.Count(window => window.CarriedForward));
        }

        return new ClaudeProbeResult
        {
            Ready = ready,
            Reason = limited ? "limited" : ready ? "ok" : "unknown",
            FiveHourResetUtc = ToReset(fiveHour),
            SevenDayResetUtc = ToReset(sevenDay),
            FiveHourUtil = ToUtilization(fiveHour),
            SevenDayUtil = ToUtilization(sevenDay),
        };
    }

    private static bool IsBlocked(UsageWindow window) =>
        window.UsedPercent is >= 100 ||
        window.Status.Equals("blocked", StringComparison.OrdinalIgnoreCase);

    private static bool IsScopedWindow(UsageWindow window) =>
        window.Name.StartsWith("weekly_scoped:", StringComparison.OrdinalIgnoreCase);

    private static bool ScopeMatchesModel(UsageWindow window, string model)
    {
        if (!IsScopedWindow(window) || string.IsNullOrWhiteSpace(model))
        {
            return false;
        }

        string displayName = window.Name[(window.Name.IndexOf(':') + 1)..];
        return ClaudeModelFamilies.TryNormalizeScopeName(displayName, out string scopedFamily) &&
            ClaudeModelFamilies.TryNormalizeConfiguredModel(model, out string configuredFamily) &&
            string.Equals(scopedFamily, configuredFamily, StringComparison.Ordinal);
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
