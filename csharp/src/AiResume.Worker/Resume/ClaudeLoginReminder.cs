using System.Globalization;
using AiResume.Worker.Products;
using AiResume.Worker.Quota;

namespace AiResume.Worker.Resume;

/// <summary>
/// Claude Code 登录到期提醒。v2.2 起额度只靠 Claude Code 自己的登录读取(ADR-0006),而它的 refresh token 有固定寿命
/// (本机约 30 天,<c>refreshTokenExpiresAt</c>),到期后 AI Resume 读不到额度、也就无法自动续跑——这件事只能由人
/// 运行 <c>claude</c> 并 <c>/login</c> 解决。所以在剩 3 天、2 天、1 天、12 小时与已过期时各发一次飞书提醒。
/// 只读凭据文件，不刷新、不写回;没设置通知收件人时不写事件(免得设置后一次补发一串过期提醒)。
/// </summary>
public sealed class ClaudeLoginReminder : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(30);

    private static readonly (string Key, TimeSpan Remaining)[] Thresholds =
    [
        ("3d", TimeSpan.FromDays(3)),
        ("2d", TimeSpan.FromDays(2)),
        ("1d", TimeSpan.FromDays(1)),
        ("12h", TimeSpan.FromHours(12)),
    ];

    private readonly Func<ClaudeCredentialStatus> _describe;
    private readonly IResumeNotices _notices;
    private readonly Func<bool> _recipientConfigured;
    private readonly Func<DateTimeOffset> _clock;
    private readonly ILogger<ClaudeLoginReminder> _logger;
    private readonly HashSet<string> _posted = new(StringComparer.Ordinal);

    public ClaudeLoginReminder(IResumeNotices notices, ILogger<ClaudeLoginReminder> logger, ProductConfigStore configStore)
        : this(() => new ClaudeCredentialSource().Describe(), notices, logger,
            () => !string.IsNullOrWhiteSpace(configStore.Load().NotifyRecipientOpenId))
    {
    }

    public ClaudeLoginReminder(
        Func<ClaudeCredentialStatus> describe,
        IResumeNotices notices,
        ILogger<ClaudeLoginReminder> logger,
        Func<bool> recipientConfigured,
        Func<DateTimeOffset>? clock = null)
    {
        _describe = describe;
        _notices = notices;
        _logger = logger;
        _recipientConfigured = recipientConfigured;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                CheckOnce();
            }
            catch (Exception ex)
            {
                _logger.LogWarning("claude.login.reminder.error error={Error}", ex.GetType().Name);
            }

            try
            {
                await Task.Delay(Interval, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }

    /// <summary>检查一次;返回本次写入的提醒类型(测试与日志用)。</summary>
    public string? CheckOnce()
    {
        ClaudeCredentialStatus status = _describe();
        if (!_recipientConfigured())
        {
            return null;
        }

        if (status.LoginExpiresAtUtc is not { } expiresAt)
        {
            // 刷新被拒后 Claude Code 会把登录清空,到期时间也随之消失:这正是最需要 /login 的终态,每天提醒一次直到恢复。
            // (LoggedOut 也可能是一次读文件失败,不据此提醒。)
            if (status.Verdict != ClaudeLoginVerdict.LoginExpired)
            {
                return null;
            }

            string lostKey = "login|cleared|" + _clock().ToLocalTime().ToString("yyyyMMdd", CultureInfo.InvariantCulture);
            if (!_posted.Add(lostKey))
            {
                return null;
            }

            PostOrForget(new ResumeNotice("login-expired", string.Empty, lostKey, null, null));
            _logger.LogInformation("claude.login.reminder type={Type} threshold={Threshold}", "login-expired", "cleared");
            return "login-expired";
        }

        TimeSpan remaining = expiresAt - _clock();
        string detail = expiresAt.ToLocalTime().ToString("MM-dd HH:mm", CultureInfo.InvariantCulture) + " 到期";
        string? key = remaining <= TimeSpan.Zero
            ? "expired"
            : Thresholds.Where(threshold => remaining <= threshold.Remaining).Select(threshold => threshold.Key).LastOrDefault();
        if (key is null)
        {
            return null;
        }

        string type = key == "expired" ? "login-expired" : "login-expiring";
        string dedupe = "login|" + expiresAt.ToUnixTimeSeconds() + "|" + key;
        if (!_posted.Add(dedupe))
        {
            return null;
        }

        PostOrForget(new ResumeNotice(type, string.Empty, dedupe, null, detail));
        _logger.LogInformation("claude.login.reminder type={Type} threshold={Threshold}", type, key);
        return type;
    }

    /// <summary>写进队列失败(杀软占用、磁盘满)时撤回去重键再抛出:下一次检查还能重写,不能一次失败就永远不提醒。</summary>
    private void PostOrForget(ResumeNotice notice)
    {
        try
        {
            _notices.Post(notice);
        }
        catch
        {
            _posted.Remove(notice.DedupeKey);
            throw;
        }
    }
}
