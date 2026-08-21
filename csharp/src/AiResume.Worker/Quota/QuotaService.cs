using AiResume.Core;
using AiResume.Worker.Probes;

namespace AiResume.Worker.Quota;

/// <summary>
/// 额度快照的取数与缓存。GUI 潮汐轴与后续的续跑编排共用这一个入口。
///
/// 缓存策略沿用现役 GUI 约定:成功 5 分钟,失败只负缓存 30 秒——
/// 失败短缓存是为了让"刚登录/刚恢复网络"能很快反映出来,而不是被一个陈旧失败挡住。
///
/// **单航班(single-flight)**:一次探测要拉起 claude 进程、实测约 7 秒。
/// 并发请求经同一把锁串行,后到者直接复用先到者刚写入的快照,
/// 避免"打开窗口 + 手动刷新"同时起两个 claude 进程。
/// </summary>
public sealed class QuotaService
{
    private static readonly TimeSpan SuccessTtl = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan FailureTtl = TimeSpan.FromSeconds(30);

    private readonly Func<CancellationToken, Task<ClaudeProbeResult>> _probe;
    private readonly Func<CancellationToken, Task<OAuthUsageResult>>? _oauthProbe;
    private readonly Func<DateTimeOffset> _clock;
    private readonly QuotaSnapshotStore? _authoritativeStore;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private UsageSnapshot? _cached;
    private DateTimeOffset _cachedAt;
    private UsageSnapshot? _lastAuthoritative;
    private string _lastAuthoritativeFingerprint = string.Empty;
    private string? _storageWarning;

    public QuotaService(
        Func<CancellationToken, Task<ClaudeProbeResult>>? probe = null,
        Func<DateTimeOffset>? clock = null,
        Func<CancellationToken, Task<OAuthUsageResult>>? oauthProbe = null,
        QuotaSnapshotStore? authoritativeStore = null)
    {
        _probe = probe ?? DefaultProbeAsync;
        // 注入了自定义 probe 的调用方(测试)默认不启用 OAuth 主路径,
        // 否则测试会意外走真实凭据与真实网络。
        _oauthProbe = oauthProbe ?? (probe is null ? DefaultOAuthProbeAsync : null);
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
        _authoritativeStore = authoritativeStore ??
            (probe is null && oauthProbe is null ? new QuotaSnapshotStore(ShadowPaths.RunDatabasePath) : null);
    }

    /// <summary>额度实时结果可用但跨窗口持久化退化时的脱敏诊断。</summary>
    public string? StorageWarning => Volatile.Read(ref _storageWarning);

    /// <summary>
    /// 取一份额度快照。<paramref name="forceRefresh"/> 为 true 时忽略缓存重新探测。
    /// 探测失败不抛异常,而是返回带 <c>UnavailableReason</c> 的快照——
    /// 红线:失败时额度区如实显示不可用,不得回退成"空闲"。
    /// </summary>
    public async Task<UsageSnapshot> GetAsync(bool forceRefresh, CancellationToken cancellationToken)
    {
        if (!forceRefresh && TryGetFresh(out UsageSnapshot? fresh))
        {
            return fresh!;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // 二次检查:等锁期间可能已有人写入新快照,此时不必再探测一次。
            if (!forceRefresh && TryGetFresh(out UsageSnapshot? afterWait))
            {
                return afterWait!;
            }

            DateTimeOffset now = _clock();
            UsageSnapshot snapshot;
            try
            {
                // 主路径:官方 oauth/usage 接口。毫秒级、两个窗口都权威下发。
                // 失败(无凭据/token 过期/网络)才降级到子进程探测——后者约 7 秒,
                // 且 five_hour 只偶发下发,所以只当兜底,不当首选。
                OAuthUsageResult oauth = await TryOAuthAsync(cancellationToken).ConfigureAwait(false);
                if (oauth is { Ok: true, Snapshot: { HasData: true } viaOAuth })
                {
                    viaOAuth = viaOAuth with { EvidenceSource = UsageEvidenceSource.OAuth };
                    if (_authoritativeStore is not null && oauth.CredentialFingerprint.Length > 0 &&
                        _authoritativeStore.TryUpdate(
                            UsageSnapshotMapper.ProviderName,
                            oauth.CredentialFingerprint,
                            previous => MergeSparseObservation(viaOAuth, previous, now),
                            out UsageSnapshot? persistedMerged))
                    {
                        snapshot = persistedMerged!;
                        Volatile.Write(ref _storageWarning, null);
                    }
                    else
                    {
                        UsageSnapshot? previous = ResolveAuthoritative(oauth.CredentialFingerprint);
                        snapshot = MergeSparseObservation(viaOAuth, previous, now);
                        if (_authoritativeStore is not null)
                        {
                            Volatile.Write(ref _storageWarning,
                                _authoritativeStore.LastFailure ?? "额度快照写入失败");
                        }
                    }
                    // oauth/usage 与 CLI rate_limit_event 都是部分观测:某次没出现某个
                    // window/percent 不代表它被撤销。保存合并后的账号级最新证据,
                    // 但任何承接值都受原服务端 resetAt 约束。
                    _lastAuthoritative = snapshot;
                    _lastAuthoritativeFingerprint = oauth.CredentialFingerprint;
                }
                else
                {
                    ClaudeProbeResult result = await _probe(cancellationToken).ConfigureAwait(false);
                    UsageSnapshot fallback = UsageSnapshotMapper.FromProbe(result, now) with
                    {
                        EvidenceSource = UsageEvidenceSource.Cli,
                    };
                    UsageSnapshot? authoritative = ResolveAuthoritative(oauth.CredentialFingerprint);
                    snapshot = MergeSparseObservation(fallback, authoritative, now);
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                // 探测器已把可预期失败归类到 Reason;走到这里是意外异常,同样如实呈现而非静默成功。
                snapshot = UsageSnapshot.Unavailable(
                    UsageSnapshotMapper.ProviderName, now, "探测异常:" + ex.Message);
            }

            _cached = snapshot;
            _cachedAt = now;
            return snapshot;
        }
        finally
        {
            _gate.Release();
        }
    }

    private bool TryGetFresh(out UsageSnapshot? snapshot)
    {
        UsageSnapshot? cached = _cached;
        if (cached is null)
        {
            snapshot = null;
            return false;
        }

        // 解析到部分窗口不代表探测成功。失败路径会在 UnavailableReason 中保留
        // 分类说明,这类结果必须走 30 秒负缓存,让登录/网络恢复尽快生效。
        TimeSpan ttl = cached.HasData && cached.UnavailableReason is null
            ? SuccessTtl
            : FailureTtl;
        if (_clock() - _cachedAt < ttl)
        {
            snapshot = cached;
            return true;
        }

        snapshot = null;
        return false;
    }

    /// <summary>
    /// 试走 OAuth 主路径。返回 null 表示"这条路不通,请降级",
    /// **不把失败原因当成快照返回**——否则用户会看到"不可用"而实际上子进程探测还能拿到数据。
    /// </summary>
    private async Task<OAuthUsageResult> TryOAuthAsync(CancellationToken cancellationToken)
    {
        if (_oauthProbe is null)
        {
            return new OAuthUsageResult(false, null, "oauth_disabled");
        }

        try
        {
            return await _oauthProbe(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            // 主路径的任何意外都只意味着"降级",不该冒泡成整体失败。
            return new OAuthUsageResult(false, null, "failed_local");
        }
    }

    private UsageSnapshot? ResolveAuthoritative(string credentialFingerprint)
    {
        if (credentialFingerprint.Length == 0)
        {
            return null;
        }

        UsageSnapshot? memory = _lastAuthoritativeFingerprint.Equals(
            credentialFingerprint, StringComparison.Ordinal)
            ? _lastAuthoritative
            : null;
        // 每次降级前重新读,让另一个 GUI/Worker 进程刚写入的更新快照可见。
        // Load 内部容错;存储损坏时仍退回当前进程的有效内存基线。
        UsageSnapshot? persisted = _authoritativeStore?.Load(
            UsageSnapshotMapper.ProviderName, credentialFingerprint);
        Volatile.Write(ref _storageWarning, _authoritativeStore?.LastFailure);
        UsageSnapshot? newest = memory is null ||
                                (persisted is not null && persisted.CapturedAt >= memory.CapturedAt)
            ? persisted
            : memory;
        if (newest is not null)
        {
            _lastAuthoritative = newest;
            _lastAuthoritativeFingerprint = credentialFingerprint;
        }

        return newest;
    }

    /// <summary>
    /// 把当前的部分观测与同账号最近一次服务端证据合并。
    ///
    /// 第一性约束:
    /// 1. 缺字段/缺窗口不是 tombstone,不得清空仍在同一 resetAt 周期内的真值;
    /// 2. 当前明确值永远优先,窗口 resetAt 改变即视为新一代,不得承接旧百分比;
    /// 3. 只有带未来 resetAt 的旧值可承接,到点立即失效;
    /// 4. 调用方已按不可逆账号指纹隔离,本函数不跨账号猜测。
    /// </summary>
    public static UsageSnapshot MergeSparseObservation(
        UsageSnapshot observation,
        UsageSnapshot? previous,
        DateTimeOffset now)
    {
        try
        {
            if (previous is null)
            {
                return observation;
            }

            long nowUnix = now.ToUnixTimeSeconds();
            bool observationIsOlder = observation.CapturedAt < previous.CapturedAt;
            Dictionary<string, UsageWindow> allPriorByIdentity = previous.Buckets
                .SelectMany(bucket => bucket.Windows)
                .Where(window => window.ResetAtUnix is null || window.ResetAtUnix > nowUnix)
                .GroupBy(WindowIdentity, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.Last(), StringComparer.OrdinalIgnoreCase);
            Dictionary<string, UsageWindow> carryablePriorByIdentity = allPriorByIdentity
                .Where(pair => pair.Value.ResetAtUnix is { } reset && reset > nowUnix)
                .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
            if (carryablePriorByIdentity.Count == 0 && (!observationIsOlder || allPriorByIdentity.Count == 0))
            {
                return WithMonotonicCaptureTime(observation, previous);
            }

            UsageBucket? observedBucket = observation.Buckets.FirstOrDefault();
            UsageWindow[] observedWindows = observedBucket?.Windows.ToArray() ?? Array.Empty<UsageWindow>();
            var merged = new List<UsageWindow>();
            var matchedPrior = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (UsageWindow current in observedWindows)
            {
                string currentIdentity = WindowIdentity(current);
                UsageWindow? prior = observationIsOlder
                    ? allPriorByIdentity.GetValueOrDefault(currentIdentity)
                    : carryablePriorByIdentity.GetValueOrDefault(currentIdentity);
                if (prior is null)
                {
                    merged.Add(current);
                    continue;
                }

                matchedPrior.Add(WindowIdentity(prior));
                if (observationIsOlder && current.ResetAtUnix is null)
                {
                    // percent-only 旧观测无法证明自己属于 prior 的新 reset 代次。
                    // 宁可保留较新窗口,也不能把旧 100% 吸进新周期。
                    merged.Add(CarryWindow(prior, nowUnix));
                }
                else if (IsSameWindowGeneration(current, prior))
                {
                    merged.Add(MergeWindow(current, prior, nowUnix));
                }
                else if (observationIsOlder)
                {
                    // 延迟进程的旧 reset 不能覆盖另一个进程已经提交的新一代窗口。
                    merged.Add(CarryWindow(prior, nowUnix));
                }
                else
                {
                    merged.Add(current);
                }
            }

            IEnumerable<UsageWindow> unmatchedPrior = observationIsOlder
                ? allPriorByIdentity.Values
                : carryablePriorByIdentity.Values;
            foreach (UsageWindow prior in unmatchedPrior.Where(window =>
                         !matchedPrior.Contains(WindowIdentity(window)) &&
                         !HasStableScopedReplacement(window, observedWindows)))
            {
                merged.Add(CarryWindow(prior, nowUnix));
            }

            if (merged.Count == 0)
            {
                return WithMonotonicCaptureTime(observation, previous);
            }

            // 旧观测的 aggregate 限流位与旧 reset 属于同一时间边界。窗口已拒绝倒写时,
            // 不能再让这个旧布尔值把新的未限流代次重新标成 limited。
            // 未归因限流没有可绑定的 reset 窗口，只能按快照时间排序。较旧观测
            // 不能清掉较新快照已经确认的限流；较新的明确观测才有权解除它。
            bool unattributedLimitReached = observationIsOlder
                ? HasUnattributedLimit(previous.Buckets.FirstOrDefault())
                : HasUnattributedLimit(observedBucket);
            bool limitReached = unattributedLimitReached || merged.Any(window =>
                window.Status.Equals("blocked", StringComparison.OrdinalIgnoreCase) ||
                window.UsedPercent is >= 100);
            UsageBucket mergedBucket = (observedBucket ?? new UsageBucket(
                "Usage", Allowed: false, LimitReached: limitReached, Windows: Array.Empty<UsageWindow>())) with
            {
                // 纯历史读数只用于连续展示/安全等待,不能冒充一次实时成功。
                Allowed = observation.HasData && !limitReached &&
                          !merged.Any(window => window.CarriedForward) &&
                          (observedBucket?.Allowed ?? false),
                LimitReached = limitReached,
                UnattributedLimitReached = unattributedLimitReached,
                Windows = merged,
            };

            UsageBucket[] buckets = observation.Buckets.Count == 0
                ? new[] { mergedBucket }
                : observation.Buckets.ToArray();
            if (observation.Buckets.Count > 0)
            {
                buckets[0] = mergedBucket;
            }
            return WithMonotonicCaptureTime(observation with
            {
                Buckets = buckets,
            }, previous);
        }
        catch (Exception)
        {
            // 历史快照只是补充。任何结构异常都不得覆盖本次观测。
            return WithMonotonicCaptureTime(observation, previous!);
        }
    }

    private static UsageSnapshot WithMonotonicCaptureTime(
        UsageSnapshot snapshot,
        UsageSnapshot previous)
    {
        if (snapshot.CapturedAt >= previous.CapturedAt)
        {
            return snapshot;
        }

        // 所有提前返回与异常降级都经过这里。即使双方都没有窗口，较旧观测
        // 也不能清掉较新快照的未归因限流事实。
        if (!HasUnattributedLimit(previous.Buckets.FirstOrDefault()))
        {
            return snapshot with { CapturedAt = previous.CapturedAt };
        }

        UsageBucket? observedBucket = snapshot.Buckets.FirstOrDefault();
        UsageBucket preservedBucket = (observedBucket ?? new UsageBucket(
            "Usage", Allowed: false, LimitReached: true, Windows: Array.Empty<UsageWindow>())) with
        {
            Allowed = false,
            LimitReached = true,
            UnattributedLimitReached = true,
        };
        UsageBucket[] buckets = snapshot.Buckets.Count == 0
            ? new[] { preservedBucket }
            : snapshot.Buckets.ToArray();
        if (snapshot.Buckets.Count > 0)
        {
            buckets[0] = preservedBucket;
        }

        return snapshot with
        {
            CapturedAt = previous.CapturedAt,
            Buckets = buckets,
        };
    }

    private static bool IsSameWindowGeneration(UsageWindow current, UsageWindow prior) =>
        current.ResetAtUnix is null || current.ResetAtUnix == prior.ResetAtUnix;

    private static string WindowIdentity(UsageWindow window) =>
        string.IsNullOrEmpty(window.Identity) ? "name:" + window.Name : "id:" + window.Identity;

    private static bool HasUnattributedLimit(UsageBucket? bucket) =>
        bucket?.UnattributedLimitReached == true ||
        (bucket?.LimitReached == true && !bucket.Windows.Any(window =>
            window.Status.Equals("blocked", StringComparison.OrdinalIgnoreCase) ||
            window.UsedPercent is >= 100));

    private static bool HasStableScopedReplacement(UsageWindow prior, IReadOnlyList<UsageWindow> observed) =>
        string.IsNullOrEmpty(prior.Identity) &&
        prior.Name.StartsWith("weekly_scoped", StringComparison.OrdinalIgnoreCase) &&
        observed.Any(current =>
            !string.IsNullOrEmpty(current.Identity) &&
            current.Name.StartsWith("weekly_scoped", StringComparison.OrdinalIgnoreCase) &&
            (current.Name.Equals(prior.Name, StringComparison.OrdinalIgnoreCase) ||
             current.Name.StartsWith(prior.Name + "#", StringComparison.OrdinalIgnoreCase)));

    private static UsageWindow MergeWindow(UsageWindow current, UsageWindow prior, long nowUnix)
    {
        // 固定 reset 代次内“已用百分比”应单调不减。跨进程晚提交、调度暂停或
        // 本机时钟回拨都可能让较旧的 99% 在新的 100% 之后到达;若让它显式覆盖,
        // 会错误解除限流。reset 换代由外层 IsSameWindowGeneration 隔离。
        int? used = current.UsedPercent is { } currentUsed && prior.UsedPercent is { } priorUsed
            ? Math.Max(currentUsed, priorUsed)
            : current.UsedPercent ?? prior.UsedPercent;
        long? reset = current.ResetAtUnix ?? prior.ResetAtUnix;
        bool retainedHigherPriorPercent = prior.UsedPercent is { } previousUsed &&
                                          (current.UsedPercent is null || previousUsed > current.UsedPercent);
        bool carried = current.CarriedForward ||
                       (current.UsedPercent is null && prior.UsedPercent is not null) ||
                       (current.ResetAtUnix is null && prior.ResetAtUnix is not null) ||
                       retainedHigherPriorPercent;
        string status = used is >= 100
            ? "blocked"
            : current.Status.Length > 0 ? current.Status : prior.Status;
        int? resetAfter = reset is { } unix
            ? (int)Math.Min(Math.Max(0, unix - nowUnix), int.MaxValue)
            : current.ResetAfterSeconds;
        return current with
        {
            Status = status,
            ResetAtUnix = reset,
            ResetAfterSeconds = resetAfter,
            UsedPercent = used,
            CarriedForward = carried,
        };
    }

    private static UsageWindow CarryWindow(UsageWindow prior, long nowUnix) => prior with
    {
        ResetAfterSeconds = prior.ResetAtUnix is { } reset
            ? (int)Math.Min(Math.Max(0, reset - nowUnix), int.MaxValue)
            : prior.ResetAfterSeconds,
        CarriedForward = true,
    };

    private static async Task<OAuthUsageResult> DefaultOAuthProbeAsync(CancellationToken cancellationToken)
        => await new ClaudeOAuthUsageProbe().TryFetchAsync(cancellationToken).ConfigureAwait(false);

    /// <summary>
    /// 默认探测:工作目录固定为 shadow 根,让探测会话落进一个已知的 .claude/projects 目录,
    /// 不污染项目发现结果(与现役 Test-ClaudeReady 的做法一致)。
    /// </summary>
    private static async Task<ClaudeProbeResult> DefaultProbeAsync(CancellationToken cancellationToken)
    {
        string workDir = ShadowPaths.Root;
        Directory.CreateDirectory(workDir);
        var probe = new ClaudeCodeProbe();
        return await probe.ProbeAsync("haiku", workDir, cancellationToken).ConfigureAwait(false);
    }
}
