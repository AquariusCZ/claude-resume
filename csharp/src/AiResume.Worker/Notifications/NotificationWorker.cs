using AiResume.Worker.Products;

namespace AiResume.Worker.Notifications;

/// <summary>
/// 完成通知投递循环(S12)。
///
/// 钩子与续跑引擎只负责把事件写进 <c>completion-events\</c>;本服务是唯一消费者，经官方 lark-cli 发飞书消息
/// (项目规则：新增飞书消息能力必须优先调用官方 CLI,不得手写同类 SDK 请求)。
/// 调度(单条退避、通道熔断、死信、稳定期)全部在 <see cref="CompletionNotifier"/>;本类只负责节拍与日志。
/// </summary>
public sealed class NotificationWorker : BackgroundService
{
    /// <summary>
    /// 扫描间隔。通知是"人不在电脑前才需要"的东西,十几秒的延迟无所谓;
    /// 失败不再拉长整个循环(旧版一条失败会把所有新通知推迟到 15 分钟一轮),退避只作用于出错的那一条或熔断中的通道。
    /// </summary>
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(15);

    private readonly ILogger<NotificationWorker> _logger;
    private readonly CompletionNotifier _notifier;
    private readonly Func<string?> _recipient;
    private readonly TimeSpan _interval;
    private string? _lastChannelState;

    public NotificationWorker(ILogger<NotificationWorker> logger)
        : this(logger, null, null)
    {
    }

    /// <param name="notifier">null 时用生产默认(shadow 队列 + lark-cli 投递)。</param>
    /// <param name="recipient">null 时从产品配置的明文收件人读取。</param>
    public NotificationWorker(
        ILogger<NotificationWorker> logger,
        CompletionNotifier? notifier,
        Func<string?>? recipient,
        TimeSpan? interval = null)
    {
        _logger = logger;
        _recipient = recipient ?? ResolveOwnerOpenId;
        if (notifier is null)
        {
            var credentials = new FeishuBotCredentials();
            var channel = new LarkCliNotificationChannel(credentials);
            var configStore = new ProductConfigStore(ShadowPaths.Root);
            notifier = new CompletionNotifier(
                eventsDir: Path.Combine(ShadowPaths.Root, "completion-events"),
                seenPath: Path.Combine(ShadowPaths.Root, "completion-notify-seen.json"),
                send: channel.SendAsync,
                // 凭据、收件人或 lark-cli 位置一变(用户刚在界面里改过、刚装好 lark-cli)就立即解除熔断重试，不等 30 分钟。
                channelFingerprint: () => NotifyFingerprint.Compute(credentials, _recipient(),
                    LarkCliNotificationChannel.ResolveExecutable(Environment.GetEnvironmentVariable("PATH"),
                        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData))),
                // 用户关掉的通知源不发(只在有事件时才读配置)。
                sourceEnabled: source =>
                {
                    AiResume.Core.ProductConfig config = configStore.Load();
                    return CompletionNotifier.IsSourceEnabled(source, config.NotifySources, config.NotifyIntentRecorded);
                },
                // Claude Code 事件定稿前核对:会话落没落盘、限额周期、会话名、布防快照(只读)。
                facts: new NotifyFacts());
            _logger.LogInformation(
                "worker.notify.channel_init larkCli={LarkCli} credentialSource={CredentialSource}",
                LarkCliNotificationChannel.ResolveExecutable(Environment.GetEnvironmentVariable("PATH"),
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData)) is null ? "missing" : "exe",
                channel.CredentialSource);
        }

        _notifier = notifier;
        _interval = interval ?? Interval;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation(
            "worker.notify.started component={Component} intervalSeconds={IntervalSeconds}",
            "worker", _interval.TotalSeconds);

        // 启动后先立即扫一遍。安装/崩溃恢复前积压的完成事件不该再平白等一个间隔。
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                string receiver = _recipient() ?? string.Empty;
                NotifySweepResult r = await _notifier.SweepAsync(receiver, stoppingToken);

                foreach (string diagnostic in r.Diagnostics)
                {
                    _logger.LogWarning("worker.notify.diagnostic code={Code}", diagnostic);
                }

                // 只记真正尝试过或状态有变的条目;稳定期/退避/熔断中的 Deferred 不逐条刷屏。
                foreach (NotifyItemResult item in r.Items.Where(item => item.Outcome is not (NotifyOutcome.Deferred or NotifyOutcome.Skipped)))
                {
                    NotifySendResult? attempt = item.Attempt;
                    if (item.Outcome is NotifyOutcome.Sent or NotifyOutcome.Duplicate or NotifyOutcome.Superseded or NotifyOutcome.Suppressed)
                    {
                        // detail 是稳定原因码(session_not_persisted / same_limit_cycle / merged / source_disabled …),不含正文。
                        _logger.LogInformation(
                            "worker.notify.item eventId={EventId} source={Source} outcome={Outcome} detail={Detail} durationMs={DurationMs} credentialSource={CredentialSource}",
                            item.EventId, item.Source ?? "unknown", item.Outcome, item.Detail, attempt?.DurationMs, attempt?.CredentialSource);
                    }
                    else
                    {
                        // errType=tenant_token 是换令牌这一步失败(lark-cli 没启动);tokenRetried=True 表示缓存的令牌先被飞书判无效、
                        // 作废重换后又试了一次,这里记的是第二次的结果。
                        _logger.LogWarning(
                            "worker.notify.item eventId={EventId} source={Source} outcome={Outcome} class={Class} scope={Scope} exit={Exit} errType={ErrType} errSubtype={ErrSubtype} errCode={ErrCode} logId={LogId} ambiguous={Ambiguous} attempts={Attempts} durationMs={DurationMs} credentialSource={CredentialSource} tokenRetried={TokenRetried}",
                            item.EventId, item.Source ?? "unknown", item.Outcome, item.Detail ?? "unknown", attempt?.Scope,
                            attempt?.ExitCode, attempt?.ErrorType, attempt?.ErrorSubtype, attempt?.ErrorCode, attempt?.LogId,
                            attempt?.Ambiguous, item.Attempts, attempt?.DurationMs, attempt?.CredentialSource, attempt?.TokenRetried);
                    }
                }

                // 通道状态只在变化时记一条:熔断几小时不该每 15 秒打印一次。
                string channelState = r.ChannelBlockedUntilUtc is null ? "healthy" : "blocked";
                if (!string.Equals(channelState, _lastChannelState, StringComparison.Ordinal))
                {
                    _logger.Log(channelState == "blocked" ? LogLevel.Warning : LogLevel.Information,
                        "worker.notify.channel state={State} reason={Reason} until={Until} backlog={Backlog}",
                        channelState, r.ChannelReason, r.ChannelBlockedUntilUtc, r.Deferred + r.Failed);
                    _lastChannelState = channelState;
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // 通知是辅助功能,**绝不能把 Worker 拖崩**——续跑编排才是这个进程的本职。
                _logger.LogWarning("worker.notify.sweep_failed error={Error}", ex.GetType().Name);
            }

            try
            {
                await Task.Delay(_interval, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }

    /// <summary>
    /// 收件人只读明文产品配置 <see cref="AiResume.Core.ProductConfig.NotifyRecipientOpenId"/>。
    /// Worker 以 S4U 跑在会话 0,用户范围 DPAPI 在那里会间歇失败数小时(2026-09/10 日志里
    /// <c>CryptUnprotectData</c> 一天失败七百多次),发送本身走 lark-cli 的 bot 身份，不需要任何密文。
    /// 没设置时整轮记 recipient_missing,控制面「飞书通知」标红。
    /// </summary>
    private static string? ResolveOwnerOpenId() =>
        FirstOpenId(new ProductConfigStore(ShadowPaths.Root).Load().NotifyRecipientOpenId);

    /// <summary>逗号分隔名单里第一个 <c>ou_</c> 开头的 open_id;没有则 null。</summary>
    public static string? FirstOpenId(string? recipients)
    {
        foreach (string part in (recipients ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (part.StartsWith("ou_", StringComparison.Ordinal))
            {
                return part;
            }
        }

        return null;
    }
}
