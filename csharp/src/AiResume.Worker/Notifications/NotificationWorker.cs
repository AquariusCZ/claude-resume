using AiResume.LarkCli;
using AiResume.Worker.Migration;

namespace AiResume.Worker.Notifications;

/// <summary>
/// 完成通知投递循环(S12)。
///
/// 钩子只负责把事件写进 <c>completion-events\</c>;在此之前**这个队列没有任何消费者**——
/// 旧系统由 feishu-agent.js 消费并发飞书消息,该模块 2026-08-07 退役后投递端一直空缺,
/// 表现为「任务跑完了但什么通知都没有」。本服务补上消费端。
///
/// 投递走**官方 lark-cli**(项目规则:新增飞书消息能力必须优先调用官方 CLI,
/// 不得手写同类 SDK 请求),并复用仓库已有的 <see cref="LarkCliInvoker"/> 封装
/// (进程启动、超时、输出脱敏、信封解析都在里面)。
/// </summary>
public sealed class NotificationWorker : BackgroundService
{
    /// <summary>
    /// 扫描间隔。通知是"人不在电脑前才需要"的东西,几十秒的延迟无所谓;
    /// 扫太勤只会在空队列上空转,还把 lark-cli 进程启动开销放大。
    /// </summary>
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(30);

    private readonly ILogger<NotificationWorker> _logger;
    private readonly CompletionNotifier _notifier;
    private readonly Func<string?> _recipient;
    private readonly TimeSpan _interval;

    public NotificationWorker(ILogger<NotificationWorker> logger)
        : this(logger, null, null)
    {
    }

    /// <param name="notifier">null 时用生产默认(shadow 队列 + lark-cli 投递)。</param>
    /// <param name="recipient">null 时从 DPAPI 的授权名单取第一个 open_id。</param>
    public NotificationWorker(
        ILogger<NotificationWorker> logger,
        CompletionNotifier? notifier,
        Func<string?>? recipient,
        TimeSpan? interval = null)
    {
        _logger = logger;
        _notifier = notifier ?? new CompletionNotifier(
            eventsDir: Path.Combine(ShadowPaths.Root, "completion-events"),
            seenPath: Path.Combine(ShadowPaths.Root, "completion-notify-seen.json"),
            send: SendViaLarkCliAsync);
        _recipient = recipient ?? ResolveOwnerOpenId;
        _interval = interval ?? Interval;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation(
            "worker.notify.started component={Component} intervalSeconds={IntervalSeconds}",
            "worker", _interval.TotalSeconds);

        int failedRounds = 0;
        TimeSpan nextDelay = _interval;

        // 启动后先立即扫一遍。安装/崩溃恢复前积压的完成事件不该再平白等 30 秒。
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

                // 空轮不记日志:每 30 秒一条"扫了 0 条"会把日志淹掉,
                // 真正出问题时反而找不到。
                if (r.Total > 0)
                {
                    _logger.LogInformation(
                        "worker.notify.sweep total={Total} sent={Sent} duplicate={Duplicate} malformed={Malformed} failed={Failed} skipped={Skipped}",
                        r.Total, r.Sent, r.Duplicate, r.Malformed, r.Failed, r.Skipped);

                    foreach (NotifyItemResult item in r.Items)
                    {
                        if (item.Outcome is NotifyOutcome.Failed or NotifyOutcome.Malformed)
                        {
                            _logger.LogWarning(
                                "worker.notify.item eventId={EventId} source={Source} outcome={Outcome} reason={Reason}",
                                item.EventId, item.Source ?? "unknown", item.Outcome, item.Detail ?? "unknown");
                        }
                        else
                        {
                            _logger.LogInformation(
                                "worker.notify.item eventId={EventId} source={Source} outcome={Outcome}",
                                item.EventId, item.Source ?? "unknown", item.Outcome);
                        }
                    }
                }

                failedRounds = r.Failed > 0 ? Math.Min(failedRounds + 1, 6) : 0;
                nextDelay = ComputeRetryDelay(_interval, failedRounds);
                if (failedRounds > 0)
                {
                    _logger.LogWarning(
                        "worker.notify.retry_backoff failedRounds={FailedRounds} nextSeconds={NextSeconds}",
                        failedRounds, nextDelay.TotalSeconds);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // 通知是辅助功能,**绝不能把 Worker 拖崩**——续跑编排才是这个进程的本职。
                _logger.LogWarning(ex, "worker.notify.sweep_failed");
            }

            try
            {
                await Task.Delay(nextDelay, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }

    /// <summary>连续失败按 2^n 退避,上限 15 分钟;成功轮立即恢复基础间隔。</summary>
    public static TimeSpan ComputeRetryDelay(TimeSpan baseInterval, int failedRounds)
    {
        if (baseInterval <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(baseInterval));
        }

        if (failedRounds <= 0)
        {
            return baseInterval;
        }

        double multiplier = Math.Pow(2, Math.Min(failedRounds - 1, 10));
        return TimeSpan.FromMilliseconds(Math.Min(
            baseInterval.TotalMilliseconds * multiplier,
            TimeSpan.FromMinutes(15).TotalMilliseconds));
    }

    /// <summary>
    /// 收件人取授权名单的第一个 open_id。
    ///
    /// 用 allow_from 而不是另设一个配置项:能给机器人发消息的人和该收到通知的人
    /// 本来就是同一个;另设一份只会漂开,还多一处要维护的安全边界。
    /// </summary>
    private static string? ResolveOwnerOpenId()
    {
        if (!new FeishuCredentialStore().TryLoad(out _, out _, out string allowFrom))
        {
            return null;
        }

        foreach (string part in allowFrom.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (part.StartsWith("ou_", StringComparison.Ordinal))
            {
                return part;
            }
        }

        return null;
    }

    /// <summary>
    /// 经 lark-cli 以 bot 身份发一条文本私信。
    ///
    /// <c>--idempotency-key</c> 传 eventId:lark-cli 会把它作为 uuid 交给飞书,
    /// 服务端据此拒绝重复投递。这是本地去重表之外的**第二道**保险——
    /// 万一去重表写不进磁盘,也不会把同一条通知重复推给用户。
    /// </summary>
    private static async Task<bool> SendViaLarkCliAsync(
        string receiverOpenId, string text, string idempotencyKey, CancellationToken ct)
    {
        // **lark-cli 在 Windows 上没有 .exe**,npm 只装了 shell 脚本 / .cmd / .ps1。
        // 直接 Process.Start("lark-cli") 在 UseShellExecute=false 下会失败:
        //   %1 不是有效的 Win32 应用程序
        // ——实测第一次真投递就栽在这儿(sweep failed=1)。
        // 所以 .cmd/.bat 必须经 cmd.exe /c 起,只有真 .exe 才能直接起。
        (string fileName, string[] wrapper) = ResolveLarkCli();
        var invoker = new LarkCliInvoker(fileName, wrapper, timeout: TimeSpan.FromSeconds(20));
        LarkCliResult result = await invoker.InvokeAsync(
        [
            "im", "+messages-send",
            "--as", "bot",
            "--user-id", receiverOpenId,
            "--text", text,
            "--idempotency-key", idempotencyKey,
            "--format", "json",
        ], ct);

        // 退出码为 0 还不够:lark-cli 对业务失败也可能返回 0 并在信封里标 ok=false。
        return result.ExitCode == 0 && result.Envelope?.Ok != false;
    }

    /// <summary>
    /// 定位 lark-cli 并决定怎么起它。
    /// 返回 (可执行文件, 包装参数):`.exe` 直接起;`.cmd`/`.bat` 经 <c>cmd.exe /c</c> 起。
    /// 都找不到时回落到裸名字——让 LarkCliInvoker 抛出它自己的"未安装"错误,
    /// 而不是在这里编一个更含糊的。
    /// </summary>
    private static (string FileName, string[] Wrapper) ResolveLarkCli()
    {
        foreach (string dir in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
                     .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            foreach (string ext in new[] { ".exe", ".cmd", ".bat" })
            {
                string candidate;
                try
                {
                    candidate = Path.Combine(dir, "lark-cli" + ext);
                }
                catch (ArgumentException)
                {
                    // PATH 里混进非法字符的条目,跳过而不是让整次解析失败。
                    continue;
                }

                if (!File.Exists(candidate))
                {
                    continue;
                }

                return ext == ".exe"
                    ? (candidate, [])
                    : ("cmd.exe", ["/c", candidate]);
            }
        }

        return ("lark-cli", []);
    }
}
