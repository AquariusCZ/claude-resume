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
    private readonly SemaphoreSlim _gate = new(1, 1);

    private UsageSnapshot? _cached;
    private DateTimeOffset _cachedAt;

    public QuotaService(
        Func<CancellationToken, Task<ClaudeProbeResult>>? probe = null,
        Func<DateTimeOffset>? clock = null,
        Func<CancellationToken, Task<OAuthUsageResult>>? oauthProbe = null)
    {
        _probe = probe ?? DefaultProbeAsync;
        // 注入了自定义 probe 的调用方(测试)默认不启用 OAuth 主路径,
        // 否则测试会意外走真实凭据与真实网络。
        _oauthProbe = oauthProbe ?? (probe is null ? DefaultOAuthProbeAsync : null);
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

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
                UsageSnapshot? viaOAuth = await TryOAuthAsync(cancellationToken).ConfigureAwait(false);
                if (viaOAuth is not null)
                {
                    snapshot = viaOAuth;
                }
                else
                {
                    ClaudeProbeResult result = await _probe(cancellationToken).ConfigureAwait(false);
                    snapshot = UsageSnapshotMapper.FromProbe(result, now);
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

        TimeSpan ttl = cached.HasData ? SuccessTtl : FailureTtl;
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
    private async Task<UsageSnapshot?> TryOAuthAsync(CancellationToken cancellationToken)
    {
        if (_oauthProbe is null)
        {
            return null;
        }

        try
        {
            OAuthUsageResult result = await _oauthProbe(cancellationToken).ConfigureAwait(false);
            return result is { Ok: true, Snapshot: { HasData: true } } ? result.Snapshot : null;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            // 主路径的任何意外都只意味着"降级",不该冒泡成整体失败。
            return null;
        }
    }

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
