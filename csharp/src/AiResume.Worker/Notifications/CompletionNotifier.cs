using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AiResume.Worker.Notifications;

/// <summary>一次投递的结果。Suppressed = 来源已被用户关掉，事件直接丢弃不发。</summary>
public enum NotifyOutcome { Sent, Duplicate, Malformed, Failed, Skipped, Deferred, Superseded, Dead, Suppressed }

public sealed record NotifyItemResult(
    string EventId,
    NotifyOutcome Outcome,
    string? Detail,
    string? Source = null)
{
    /// <summary>失败时的结构化原因(类别/错误码/log_id/退出码/耗时/尝试次数),不含正文与收件人。</summary>
    public NotifySendResult? Attempt { get; init; }

    public int Attempts { get; init; }
}

public sealed record NotifySweepResult(
    int Total, int Sent, int Duplicate, int Malformed, int Failed, int Skipped,
    IReadOnlyList<NotifyItemResult> Items)
{
    public IReadOnlyList<string> Diagnostics { get; init; } = [];

    /// <summary>本轮按计划没有尝试的条目(稳定期、单条退避、通道熔断中)。</summary>
    public int Deferred { get; init; }

    /// <summary>通道是否处于熔断(凭据、权限、网络等整体故障):调用方据此决定下次唤醒，而不是逐条撞墙。</summary>
    public DateTimeOffset? ChannelBlockedUntilUtc { get; init; }

    /// <summary>熔断原因(失败类别);未熔断为 null。</summary>
    public string? ChannelReason { get; init; }
}

/// <summary>
/// 投递通道健康状态(Worker 单写，GUI 只读)。界面上「飞书通知可送达」的每一句肯定都必须来自这里的真实投递证据。
/// </summary>
public sealed class DeliveryHealth
{
    /// <summary>healthy / blocked / unknown(还没投递过)。</summary>
    public string State { get; set; } = "unknown";

    public string? Reason { get; set; }

    public DateTimeOffset? BlockedSinceUtc { get; set; }

    public DateTimeOffset? BlockedUntilUtc { get; set; }

    public int ConsecutiveChannelFailures { get; set; }

    /// <summary>熔断时的凭据/收件人指纹;它变了(用户刚改过设置)就立即解除熔断重试。</summary>
    public string? BlockFingerprint { get; set; }

    public DateTimeOffset? LastSuccessUtc { get; set; }

    public DateTimeOffset? LastAttemptUtc { get; set; }

    public string? LastErrorClass { get; set; }

    /// <summary>失败来自哪一段:tenant_token = 换令牌(lark-cli 没启动),其余为 lark-cli 信封的 type。</summary>
    public string? LastErrorType { get; set; }

    /// <summary>失败细类(如 <c>server_error:http_502@direct</c>、<c>token_invalid</c>);不含凭据。</summary>
    public string? LastErrorSubtype { get; set; }

    public long? LastErrorCode { get; set; }

    public string? LastLogId { get; set; }

    public int Backlog { get; set; }

    public int Dead { get; set; }

    public string? CredentialSource { get; set; }

    /// <summary>
    /// 最近一次真实尝试是否成功(null = 还没尝试过)。条目级失败(收件人、权限、未知错误码…)也置 false:
    /// 历史上的成功不能让界面在之后的失败里继续显示「可送达」。
    /// </summary>
    public bool? LastAttemptOk { get; set; }

    /// <summary>最近一次成功送达时的凭据/收件人指纹(哈希)。与当前指纹不同 = 成功之后设置改过,需要重新核实。</summary>
    public string? SuccessFingerprint { get; set; }

    /// <summary>跨轮的永久失败记录:30 分钟内另一条事件再命中同一类 → 多半是 CLI 契约变了,升级为通道熔断。</summary>
    public string? PermanentClass { get; set; }

    public DateTimeOffset? PermanentSinceUtc { get; set; }

    public string? PermanentEventId { get; set; }

    public DateTimeOffset UpdatedAtUtc { get; set; }

    public static DeliveryHealth? TryLoad(string path)
    {
        try
        {
            return File.Exists(path)
                ? JsonSerializer.Deserialize<DeliveryHealth>(File.ReadAllText(path), CompletionNotifier.StateJson)
                : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }
}

/// <summary>
/// 完成通知投递端(S12):消费 <c>completion-events</c> 队列,经 lark-cli 发飞书消息。
///
/// 为什么用 lark-cli 而不是自己发 HTTP(盘点结论,必须保留):
/// - 项目规则:新增飞书消息能力**必须优先调用官方 lark-cli**,不得手写同类 SDK 请求。
/// - 本机 lark-cli 已用**同一个飞书应用**授权;投递通道见 <see cref="LarkCliNotificationChannel"/>。
/// - <c>--idempotency-key</c> 最长 50 字符,服务端据此在一小时内去重;eventId 是 16 位十六进制,正好可直接用。
///   这是本地去重之外的第二道保险(2026-10-30 之前经 cmd.exe 被截断从未送达，已改为直接启动 exe)。
///
/// 调度(2026-10-30 体检后重写，旧版一条失败会把所有新通知推迟到 15 分钟一轮):
/// <list type="bullet">
/// <item>按事件发生时间(atUtc)从旧到新处理;</item>
/// <item>条目级失败只退避这一条(30s×2^n,上限 15 分钟,24 小时后进死信),不拖累新事件;</item>
/// <item>通道级失败(钥匙串/凭据/权限/网络/频控)立刻熔断本轮，按类别退避，不再逐条撞墙;设置一变即解除;</item>
/// <item>参数类永久失败移入 <c>dead\</c>,但 30 分钟内(跨轮记账)另一条事件命中同类就升级为通道级(防 CLI 升级把全部通知送进死信);</item>
/// <item>发生超过 24 小时还没送出的事件不再补发、进死信;结果不明(可能已送达)的重发只在飞书一小时去重窗口内做(55 分钟封顶);</item>
/// <item>Claude Code 完成类事件有 20 秒稳定期：同一会话更新的完成会替代旧的(/goal 续跑等连续 Stop 只报最后一次);
///   后台任务还在跑时最多暂缓 30 分钟，等真正结束那一条。</item>
/// <item>会话没落盘(稳定期后转录文件仍不存在)的 Claude Code 事件一律不发:谁都打不开、续不了,只可能是程序拉起的一次性调用
///   (2026-11 实证:第三方用量监视器每 8 小时以 <c>--no-session-persistence</c> 拉起 claude 刷新令牌,周额度用完的两天里每次都报「中断」)。</item>
/// <item>用量上限中断(StopFailure rate_limit 与原生自动继续放弃的 <c>quota_auto_resume_disabled</c>)按限额周期
///   (转录里服务端给的 <c>rateLimitType + resetsAt</c>,读不到时按会话、5 小时)只提醒一次:同时被打断的会话合成一条,
///   之后同周期的直接丢弃;文案按投递那一刻的布防快照说清会不会自动续跑、何时重置。</item>
/// </list>
/// 本类只负责队列扫描、去重、退避与文件生命周期;实际投递通过注入的 <c>send</c> 委托完成,
/// 测试注入假实现,绝不触碰真实网络。
/// </summary>
public sealed class CompletionNotifier
{
    public static readonly TimeSpan StabilityWindow = TimeSpan.FromSeconds(20);
    public static readonly TimeSpan BackgroundBusyHold = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan DuplicateWindow = TimeSpan.FromDays(7);
    private static readonly TimeSpan SupersedeWindow = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan DeadAfter = TimeSpan.FromHours(24);

    /// <summary>事件发生超过这么久还没送出去(缺收件人、通道长期熔断)就不再补发,免得恢复后一次冲刷一串旧消息。</summary>
    public static readonly TimeSpan StaleAfter = TimeSpan.FromHours(24);

    /// <summary>
    /// 结果不明(可能已送达)的重发只在飞书 uuid 去重窗口(1 小时)之内做;从第一次不明开始超过这个时长就停,
    /// 否则再发就可能真的重复送达。
    /// </summary>
    public static readonly TimeSpan AmbiguousCap = TimeSpan.FromMinutes(55);

    private static readonly TimeSpan PermanentWindow = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan SessionLedgerKeep = TimeSpan.FromDays(2);

    /// <summary>同一限额周期的中断最多攒这么久就发:最新一条满稳定期,或最早一条满 2 分钟(陆续撞墙的会话不无限推迟)。</summary>
    private static readonly TimeSpan LimitGroupCap = TimeSpan.FromMinutes(2);

    /// <summary>读不到重置时间时按会话记账的时长:最短的限额窗口(5 小时)。</summary>
    private static readonly TimeSpan SessionLimitKeep = TimeSpan.FromHours(5);

    /// <summary>按重置时间记账时额外多留的余量(时钟偏差、重置后才到的迟到事件)。</summary>
    private static readonly TimeSpan LimitCycleSlack = TimeSpan.FromHours(1);

    internal static readonly JsonSerializerOptions StateJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly string _eventsDir;
    private readonly string _seenPath;
    private readonly string _statePath;
    private readonly string _healthPath;
    private readonly string _sessionsPath;
    private readonly string _limitsPath;
    private readonly Func<string, string, string, CancellationToken, Task<NotifySendResult>> _send;
    private readonly Func<DateTimeOffset> _now;
    private readonly Func<string, string[]> _enumerateFiles;
    private readonly Func<string, string, bool> _moveToMalformed;
    private readonly Func<string> _channelFingerprint;
    private readonly TimeSpan _sendSpacing;
    private readonly Func<string?, bool> _sourceEnabled;
    private readonly INotifyFacts? _facts;

    /// <param name="sourceEnabled">
    /// 事件来源是否仍是用户想要的通知源(null = 全部放行)。关掉的源仍可能有残留链路在写事件
    /// (例如 Codex <c>config.toml</c> 里旧版 <c>notify</c> 链,那是共享键，按 ADR-0005 只提示不改写),这里统一丢弃。
    /// </param>
    /// <param name="facts">
    /// Claude Code 通知定稿前核对的外部事实(转录在不在、限额周期、会话名、布防快照)。null = 一律「不知道」:
    /// 不过滤不落盘的会话,限额按会话记账,续跑句写「布防状态未核实」。
    /// </param>
    public CompletionNotifier(
        string eventsDir,
        string seenPath,
        Func<string, string, string, CancellationToken, Task<NotifySendResult>> send,
        Func<DateTimeOffset>? now = null,
        Func<string, string[]>? enumerateFiles = null,
        Func<string, string, bool>? moveToMalformed = null,
        Func<string>? channelFingerprint = null,
        TimeSpan? sendSpacing = null,
        Func<string?, bool>? sourceEnabled = null,
        INotifyFacts? facts = null)
    {
        _eventsDir = eventsDir;
        _seenPath = seenPath;
        string stateDirectory = Path.GetDirectoryName(seenPath) ?? eventsDir;
        _statePath = Path.Combine(stateDirectory, "completion-notify-state.json");
        _healthPath = Path.Combine(stateDirectory, "completion-notify-health.json");
        // 每个 Claude 会话最近一次已送达的完成时间:更早的「后台任务未完」完成事件据此作废(它要等的正是那一条)。
        _sessionsPath = Path.Combine(stateDirectory, "completion-notify-sessions.json");
        // 已提醒过的限额周期(周期键 → 失效时间):同一周期之后的中断不再发。
        _limitsPath = Path.Combine(stateDirectory, "completion-notify-limits.json");
        _facts = facts;
        _send = send ?? throw new ArgumentNullException(nameof(send));
        _now = now ?? (() => DateTimeOffset.UtcNow);
        _enumerateFiles = enumerateFiles ?? (path => Directory.GetFiles(path, "*.json"));
        _moveToMalformed = moveToMalformed ?? ((full, name) => TryMoveTo(full, name, "malformed"));
        _channelFingerprint = channelFingerprint ?? (() => string.Empty);
        // 飞书对同一用户 5 QPS;积压冲刷时两次发送之间留一点间隔。测试传 0。
        _sendSpacing = sendSpacing ?? TimeSpan.FromMilliseconds(300);
        _sourceEnabled = sourceEnabled ?? (_ => true);
    }

    /// <summary>
    /// 按持久化的通知意图(<c>ProductConfig.NotifySources</c>,如 ClaudeCode / OpenCode)判断事件来源是否放行。
    /// AI Resume 自己的续跑与登录事件(source=ai-resume)总是放行。意图分三态:从未记录过(旧配置,列表为空且
    /// <paramref name="intentRecorded"/> 为 false)时不过滤;记录过——哪怕是用户把通知源全关了的空列表——就只放行列表里的。
    /// 事件来源与通知源名只差大小写(claudecode ↔ ClaudeCode)。
    /// </summary>
    public static bool IsSourceEnabled(string? source, IReadOnlyCollection<string>? intendedSources, bool intentRecorded = false)
    {
        if (string.Equals(source, "ai-resume", StringComparison.Ordinal))
        {
            return true;
        }

        if (!intentRecorded && (intendedSources is null || intendedSources.Count == 0))
        {
            return true;
        }

        return source is { Length: > 0 } && intendedSources is not null &&
               intendedSources.Contains(source, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>兼容只回答「成没成」的投递委托:false 当作可重试的条目级失败。</summary>
    public CompletionNotifier(
        string eventsDir,
        string seenPath,
        Func<string, string, string, CancellationToken, Task<bool>> send,
        Func<DateTimeOffset>? now = null,
        Func<string, string[]>? enumerateFiles = null,
        Func<string, string, bool>? moveToMalformed = null)
        : this(eventsDir, seenPath,
            async (receiver, text, key, ct) => await send(receiver, text, key, ct).ConfigureAwait(false)
                ? NotifySendResult.Delivered()
                : NotifySendResult.ItemFailure("send_rejected"),
            now, enumerateFiles, moveToMalformed, sendSpacing: TimeSpan.Zero)
    {
        ArgumentNullException.ThrowIfNull(send);
    }

    public string HealthPath => _healthPath;

    /// <summary>扫一遍队列并投递。receiverOpenId 为空白时全部记 Skipped 且不删文件。</summary>
    public async Task<NotifySweepResult> SweepAsync(string receiverOpenId, CancellationToken ct = default)
    {
        var items = new List<NotifyItemResult>();
        var diagnostics = new List<string>();
        int sent = 0, duplicate = 0, malformed = 0, failed = 0, skipped = 0, deferred = 0;

        // 加载去重表;文件不存在/空/无法解析 → 空表,不得抛。
        Dictionary<string, DateTimeOffset> seen = LoadSeen(out string? seenDiagnostic);
        if (seenDiagnostic is not null)
        {
            diagnostics.Add(seenDiagnostic);
        }

        Dictionary<string, AttemptState> attempts = LoadState();
        DeliveryHealth health = DeliveryHealth.TryLoad(_healthPath) ?? new DeliveryHealth();
        Dictionary<string, DateTimeOffset> lastSentCompletion = LoadSessions();
        bool sessionsChanged = false;

        string[] files;
        try
        {
            files = _enumerateFiles(_eventsDir)
                .Select(Path.GetFileName)
                .Where(f => f is not null)
                .OrderBy(f => f!, StringComparer.Ordinal)
                .ToArray()!;
        }
        catch (DirectoryNotFoundException)
        {
            return new NotifySweepResult(0, 0, 0, 0, 0, 0, items) { Diagnostics = diagnostics };
        }
        catch (IOException)
        {
            const string code = "queue_enumeration_io";
            diagnostics.Add(code);
            items.Add(new NotifyItemResult("queue", NotifyOutcome.Failed, code, "worker"));
            return new NotifySweepResult(1, 0, 0, 0, 1, 0, items) { Diagnostics = diagnostics };
        }
        catch (UnauthorizedAccessException)
        {
            const string code = "queue_enumeration_denied";
            diagnostics.Add(code);
            items.Add(new NotifyItemResult("queue", NotifyOutcome.Failed, code, "worker"));
            return new NotifySweepResult(1, 0, 0, 0, 1, 0, items) { Diagnostics = diagnostics };
        }

        bool seenChanged = false;
        bool stateChanged = false;
        DateTimeOffset nowUtc = _now().ToUniversalTime();

        // 1. 读入并校验全部事件;坏事件隔离进 malformed\,读失败的留待下轮。
        var queue = new List<QueuedEvent>();
        foreach (string fileName in files)
        {
            ct.ThrowIfCancellationRequested();
            string fullPath = Path.Combine(_eventsDir, fileName);
            string text;
            try
            {
                text = File.ReadAllText(fullPath);
            }
            catch (IOException)
            {
                items.Add(new NotifyItemResult(fileName, NotifyOutcome.Failed, "read_io"));
                failed++;
                continue;
            }
            catch (UnauthorizedAccessException)
            {
                items.Add(new NotifyItemResult(fileName, NotifyOutcome.Failed, "read_denied"));
                failed++;
                continue;
            }

            QueuedEvent? parsed;
            try
            {
                parsed = QueuedEvent.Parse(fileName, fullPath, text);
            }
            catch (Exception ex) when (ex is JsonException or InvalidDataException)
            {
                if (_moveToMalformed(fullPath, fileName))
                {
                    items.Add(new NotifyItemResult(fileName, NotifyOutcome.Malformed, "json_invalid"));
                    malformed++;
                }
                else
                {
                    items.Add(new NotifyItemResult(fileName, NotifyOutcome.Failed, "malformed_move_failed:json_invalid"));
                    failed++;
                }

                continue;
            }

            if (parsed is null)
            {
                string? source = QueuedEvent.PeekSource(text);
                if (_moveToMalformed(fullPath, fileName))
                {
                    items.Add(new NotifyItemResult(fileName, NotifyOutcome.Malformed, "required_field_missing", source));
                    malformed++;
                }
                else
                {
                    items.Add(new NotifyItemResult(fileName, NotifyOutcome.Failed, "malformed_move_failed:required_field_missing", source));
                    failed++;
                }

                continue;
            }

            queue.Add(parsed);
        }

        // 2. 按事件发生时间从旧到新;时间缺失的排最后，同时刻按文件名保证确定性。
        queue = queue
            .OrderBy(item => item.AtUtc ?? DateTimeOffset.MaxValue)
            .ThenBy(item => item.FileName, StringComparer.Ordinal)
            .ToList();

        Dictionary<string, DateTimeOffset> limitCycles = LoadLimitCycles();
        bool limitCyclesChanged = false;
        var transcripts = new Dictionary<string, bool?>(StringComparer.OrdinalIgnoreCase);
        var pending = new List<QueuedEvent>();

        // 3. 先剔除一定不发的:已送达、来源已关、过期、会话没落盘。
        foreach (QueuedEvent item in queue)
        {
            ct.ThrowIfCancellationRequested();

            // 3a. 七天内已送达 → 删除。
            if (seen.TryGetValue(item.EventId, out DateTimeOffset recorded) && nowUtc - recorded < DuplicateWindow)
            {
                TryDelete(item.FullPath);
                attempts.Remove(item.EventId);
                items.Add(new NotifyItemResult(item.EventId, NotifyOutcome.Duplicate, null, item.Source));
                duplicate++;
                continue;
            }

            // 3b. 来源已被用户关掉(残留的旧链路仍可能写事件)→ 不发，直接丢弃。
            if (!_sourceEnabled(item.Source))
            {
                TryDelete(item.FullPath);
                stateChanged |= attempts.Remove(item.EventId);
                items.Add(new NotifyItemResult(item.EventId, NotifyOutcome.Suppressed, "source_disabled", item.Source));
                continue;
            }

            // 3c. 会话没落盘的 Claude Code 事件不发(完成、中断、等你都一样):没有转录的会话谁都打不开、续不了。
            //     转录是异步写的,先等过稳定期再下结论;判断不了(权限、陌生路径、旧事件没带路径)照常发。
            //     排在过期判定之前:本该静默丢弃的事件,Worker 停机一天后也不该进死信、算进「未送达」。
            if (item.IsClaudeCode && item.TranscriptPath is { Length: > 0 } transcript)
            {
                if (!transcripts.TryGetValue(transcript, out bool? persisted))
                {
                    persisted = ReadFact(facts => facts.TranscriptExists(transcript));
                    transcripts[transcript] = persisted;
                }

                if (persisted == false)
                {
                    if (AgeOf(item, nowUtc) < StabilityWindow)
                    {
                        items.Add(new NotifyItemResult(item.EventId, NotifyOutcome.Deferred, "settling", item.Source));
                        deferred++;
                        continue;
                    }

                    TryDelete(item.FullPath);
                    stateChanged |= attempts.Remove(item.EventId);
                    items.Add(new NotifyItemResult(item.EventId, NotifyOutcome.Suppressed, "session_not_persisted", item.Source));
                    continue;
                }
            }

            // 3d. 发生超过 24 小时还没送出去(缺收件人、通道长期熔断…):不再补发,进死信。
            //     恢复后一次冲刷一串旧「已完成」只会误导——文案只有 HH:mm,看起来像刚发生。
            //     用量上限中断先过限额周期记账(同周期的直接丢弃),过期判定放到第 4 步。
            if (!item.IsLimitInterruption && item.AtUtc is { } occurred && nowUtc - occurred > StaleAfter)
            {
                DeadOrBackoff(item, NotifySendResult.ItemFailure("stale"), attempts, nowUtc, items, "stale", ref stateChanged);
                failed++;
                continue;
            }

            pending.Add(item);
        }

        // 4. 用量上限中断按限额周期分组:同一会话同一周期只提醒一次,同一周期里同时待发的会话合成一条。
        //    同周期之后这个会话的每一次(手动重试、原生自动继续撞墙)都不带新信息;别的会话撞上同一周期照样提醒。
        var limitFacts = new Dictionary<string, LimitFacts?>(StringComparer.OrdinalIgnoreCase);
        var groupOf = new Dictionary<string, LimitGroup>(StringComparer.Ordinal);
        var groups = new Dictionary<string, LimitGroup>(StringComparer.Ordinal);
        // 结果不明(可能已经送达)又过期作废的那条所在的周期:同周期其余几条可能就在那条消息里,不再另发。
        var announcedCycles = new HashSet<string>(StringComparer.Ordinal);
        bool limitSettling = false;
        foreach (QueuedEvent item in pending.Where(item => item.IsLimitInterruption).ToList())
        {
            // 刚到的先不判:新的限流记录可能还没写进转录,这时读到的是上一次中断,会被误判成「这一周期已提醒过」。
            if (AgeOf(item, nowUtc) < StabilityWindow)
            {
                items.Add(new NotifyItemResult(item.EventId, NotifyOutcome.Deferred, "settling", item.Source));
                deferred++;
                pending.Remove(item);
                limitSettling = true;
                continue;
            }

            LimitFacts? facts = null;
            if (item.TranscriptPath is { Length: > 0 } transcript && !limitFacts.TryGetValue(transcript, out facts))
            {
                facts = ReadFact(reader => reader.ReadLimit(transcript));
                limitFacts[transcript] = facts;
            }

            // 事件发生时这份限额已经过了重置时刻:转录尾部是更早那次中断(这次没写新的限流记录),不能拿它定周期、说重置时间。
            if (facts?.ResetsAtUtc is { } resets && item.AtUtc is { } happened && resets <= happened)
            {
                facts = null;
            }

            string cycle = LimitCycleKey(item, facts);
            string ledgerKey = cycle + "#" + (item.SessionId ?? item.EventId);
            if ((limitCycles.TryGetValue(ledgerKey, out DateTimeOffset until) && until > nowUtc) || announcedCycles.Contains(cycle))
            {
                if (announcedCycles.Contains(cycle))
                {
                    limitCycles[ledgerKey] = CycleExpiry(facts, item.AtUtc ?? nowUtc, nowUtc);
                    limitCyclesChanged = true;
                }

                TryDelete(item.FullPath);
                stateChanged |= attempts.Remove(item.EventId);
                items.Add(new NotifyItemResult(item.EventId, NotifyOutcome.Suppressed, "same_limit_cycle", item.Source));
                pending.Remove(item);
                continue;
            }

            // 发生超过 24 小时还没送出去:不补发。它要是有过结果不明的尝试(可能已经送达),这个周期按已提醒记账。
            if (item.AtUtc is { } occurred && nowUtc - occurred > StaleAfter)
            {
                if (attempts.TryGetValue(item.EventId, out AttemptState? tried) && tried.AmbiguousSinceUtc is not null)
                {
                    announcedCycles.Add(cycle);
                    limitCycles[ledgerKey] = CycleExpiry(facts, occurred, nowUtc);
                    limitCyclesChanged = true;
                }

                DeadOrBackoff(item, NotifySendResult.ItemFailure("stale"), attempts, nowUtc, items, "stale", ref stateChanged);
                failed++;
                pending.Remove(item);
                continue;
            }

            if (!groups.TryGetValue(cycle, out LimitGroup? group))
            {
                group = new LimitGroup(cycle);
                groups[cycle] = group;
            }

            group.Add(item, facts, ledgerKey);
            groupOf[item.EventId] = group;
        }

        bool blocked = health.BlockedUntilUtc > nowUtc &&
                       string.Equals(health.BlockFingerprint ?? string.Empty, SafeFingerprint(), StringComparison.Ordinal);
        bool sentOne = false;
        ArmSnapshot? arm = null;
        bool armRead = false;
        // 已随同组消息送达、或随代表那条一起进了死信的限额事件。
        var settled = new HashSet<string>(StringComparer.Ordinal);

        foreach (QueuedEvent item in pending)
        {
            ct.ThrowIfCancellationRequested();
            if (settled.Contains(item.EventId))
            {
                continue;
            }

            // 同一 eventId 的另一份文件这一轮刚送达过(写入方都以 eventId 命名,正常不会出现):按已送达删掉。
            if (seen.TryGetValue(item.EventId, out DateTimeOffset sentEarlier) && nowUtc - sentEarlier < DuplicateWindow)
            {
                TryDelete(item.FullPath);
                stateChanged |= attempts.Remove(item.EventId);
                items.Add(new NotifyItemResult(item.EventId, NotifyOutcome.Duplicate, null, item.Source));
                duplicate++;
                continue;
            }

            groupOf.TryGetValue(item.EventId, out LimitGroup? group);

            // 5. 同一 Claude Code 会话更新的完成替代旧的:连续 Stop 只报最后一次。普通完成只在 2 分钟内合并;
            //    「后台任务未完」的完成等的正是之后那一条,所以同会话任何更新的完成——哪怕已经送出——都让它作废。
            bool superseded = item.IsClaudeCompletion && (
                queue.Any(other => !ReferenceEquals(other, item) && other.IsClaudeCompletion && other.SessionId == item.SessionId &&
                    other.AtUtc > item.AtUtc && (item.BackgroundBusy || other.AtUtc - item.AtUtc <= SupersedeWindow)) ||
                (item.BackgroundBusy && item.AtUtc is { } busyAt && item.SessionId is { } busySession &&
                    lastSentCompletion.TryGetValue(busySession, out DateTimeOffset laterSent) && laterSent > busyAt));
            if (superseded)
            {
                TryDelete(item.FullPath);
                stateChanged |= attempts.Remove(item.EventId);
                items.Add(new NotifyItemResult(item.EventId, NotifyOutcome.Superseded, "newer_completion", item.Source));
                continue;
            }

            // 6. 收件人为空 → 保留文件。
            if (string.IsNullOrWhiteSpace(receiverOpenId))
            {
                items.Add(new NotifyItemResult(item.EventId, NotifyOutcome.Skipped, "recipient_missing", item.Source));
                skipped++;
                continue;
            }

            // 7. 稳定期与后台任务暂缓:先等更新的完成或真正的结束。限额中断整组只由最早那一条代表发送;
            //    还有刚到、尚未分组的中断时先等它满稳定期(陆续撞墙的会话合进同一条),最早一条满 2 分钟则不再等。
            TimeSpan age = AgeOf(item, nowUtc);
            if (group is not null)
            {
                if (!ReferenceEquals(group.Lead, item))
                {
                    items.Add(new NotifyItemResult(item.EventId, NotifyOutcome.Deferred, "limit_group", item.Source));
                    deferred++;
                    continue;
                }

                if (!group.Ready(nowUtc, limitSettling))
                {
                    items.Add(new NotifyItemResult(item.EventId, NotifyOutcome.Deferred, "settling", item.Source));
                    deferred++;
                    continue;
                }
            }
            else if (item.IsClaudeCompletion && age < StabilityWindow)
            {
                items.Add(new NotifyItemResult(item.EventId, NotifyOutcome.Deferred, "settling", item.Source));
                deferred++;
                continue;
            }

            if (item.IsClaudeCompletion && item.BackgroundBusy && age < BackgroundBusyHold)
            {
                items.Add(new NotifyItemResult(item.EventId, NotifyOutcome.Deferred, "background_busy", item.Source));
                deferred++;
                continue;
            }

            // 8. 通道熔断中 / 本条还在退避 → 这轮不碰。
            if (blocked)
            {
                items.Add(new NotifyItemResult(item.EventId, NotifyOutcome.Deferred, "channel_blocked:" + health.Reason, item.Source));
                deferred++;
                continue;
            }

            attempts.TryGetValue(item.EventId, out AttemptState? attempt);
            if (attempt?.AmbiguousSinceUtc is { } ambiguousSince && nowUtc - ambiguousSince > AmbiguousCap)
            {
                // 结果不明(可能已送达)的重发只在飞书 uuid 一小时去重窗口内做;再往后发就可能真的重复送达。
                NotifySendResult unknown = NotifySendResult.ItemFailure("outcome_unknown");
                if (DeadOrBackoff(item, unknown, attempts, nowUtc, items, "outcome_unknown", ref stateChanged) && group is not null)
                {
                    // 那条消息可能已经送到:同组按已提醒记账,之后同周期的不再另发。
                    BuryGroup(group, unknown, "outcome_unknown", items, attempts, settled, ref stateChanged);
                    RecordCycle(group, limitCycles, nowUtc);
                    limitCyclesChanged = true;
                }

                failed++;
                continue;
            }

            if (attempt?.NextAttemptUtc > nowUtc)
            {
                items.Add(new NotifyItemResult(item.EventId, NotifyOutcome.Deferred, "backoff", item.Source) { Attempts = attempt.Attempts });
                deferred++;
                continue;
            }

            if (sentOne && _sendSpacing > TimeSpan.Zero)
            {
                await Task.Delay(_sendSpacing, ct).ConfigureAwait(false);
            }

            // 这一轮开头就把事件读进了内存:发送前再确认文件还在，并在发送期间占住它(允许读，不允许删)。
            // 撤回测试通知与发送由此互斥——先撤回的不再发;正在发的撤不回，撤回方拿到「撤回失败」,如实提示可能仍会送达。
            FileStream? claim = TryClaim(item.FullPath, out bool withdrawn);
            if (withdrawn)
            {
                stateChanged |= attempts.Remove(item.EventId);
                items.Add(new NotifyItemResult(item.EventId, NotifyOutcome.Suppressed, "withdrawn", item.Source));
                continue;
            }

            sentOne = true;
            string message;
            if (group is not null)
            {
                if (!armRead)
                {
                    arm = ReadFact(facts => facts.ReadArm());
                    armRead = true;
                }

                message = BuildLimitText(group.Sessions(SessionNameOf), group.Window, arm, nowUtc);
            }
            else
            {
                message = BuildText(item, nowUtc, SessionNameOf(item));
            }

            // 发送前记下设置指纹：一次发送(含换令牌与令牌失效重试)可能持续几十秒，期间用户改了设置，
            // 这次的成功或熔断都属于旧设置——不能让新设置凭这次结果点绿，也不能让它被这次失败熔断。
            string attemptFingerprint = SafeFingerprint();
            NotifySendResult result;
            try
            {
                result = await _send(receiverOpenId, message, item.EventId, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception)
            {
                // 不得让异常逃出 SweepAsync:一条坏事件不能中断整轮。
                result = NotifySendResult.ItemFailure("send_exception");
            }
            finally
            {
                claim?.Dispose();
            }

            health.LastAttemptUtc = nowUtc;
            health.LastAttemptOk = result.Sent;
            if (result.CredentialSource is not null)
            {
                health.CredentialSource = result.CredentialSource;
            }

            if (result.Sent)
            {
                seen[item.EventId] = nowUtc;
                if (group is not null)
                {
                    // 同组其余几条已经写进这条消息:一并记已送达;这个限额周期记账,之后同周期的不再发。
                    foreach (QueuedEvent member in group.Members.Where(member => !ReferenceEquals(member, item)))
                    {
                        seen[member.EventId] = nowUtc;
                        stateChanged |= attempts.Remove(member.EventId);
                        TryDelete(member.FullPath);
                        settled.Add(member.EventId);
                        items.Add(new NotifyItemResult(member.EventId, NotifyOutcome.Superseded, "merged", member.Source));
                    }

                    RecordCycle(group, limitCycles, nowUtc);
                    limitCyclesChanged = WriteJson(_limitsPath, limitCycles) is not null;
                }

                // 送达一条就落盘一次已送达表:积压冲刷可能持续好几分钟，等整轮结束才写，
                // 「发送测试」/ notify test 在这期间看不到这条已经送达，会误判超时并撤回。
                if (WriteSeen(seen) is { } sentDiagnostic)
                {
                    diagnostics.Add(sentDiagnostic);
                    seenChanged = true;
                }

                stateChanged |= attempts.Remove(item.EventId);
                TryDelete(item.FullPath);
                MarkHealthy(health, nowUtc);
                health.SuccessFingerprint = attemptFingerprint;
                if (item.IsClaudeCompletion && item.SessionId is { } sentSession && item.AtUtc is { } sentAt &&
                    (!lastSentCompletion.TryGetValue(sentSession, out DateTimeOffset previous) || sentAt > previous))
                {
                    lastSentCompletion[sentSession] = sentAt;
                    sessionsChanged = true;
                }

                items.Add(new NotifyItemResult(item.EventId, NotifyOutcome.Sent, null, item.Source) { Attempt = result });
                sent++;
                continue;
            }

            health.LastErrorClass = result.Class;
            health.LastErrorType = result.ErrorType;
            health.LastErrorSubtype = result.ErrorSubtype;
            health.LastErrorCode = result.ErrorCode;
            health.LastLogId = result.LogId;

            // 参数类永久失败:30 分钟内另一条事件也命中同一类(跨轮记账——常态下每轮只有一条事件),
            // 多半是 CLI 契约变了,继续把每条都送进死信只会全军覆没 → 升级为通道熔断。
            if (result.Permanent)
            {
                // 已经判过「CLI 契约变了」、之后既没成功过(成功会清掉熔断)也没换设置:同类永久失败继续按通道熔断,
                // 不再每 30 分钟放一条进死信——那只是把「全部进死信」放慢了。超过 24 小时的由 stale 兜底。
                bool contractBroken = health.State == "blocked" && health.Reason == "cli_contract" &&
                                      string.Equals(health.BlockFingerprint ?? string.Empty, attemptFingerprint, StringComparison.Ordinal);
                bool repeated = contractBroken ||
                                (health.PermanentClass == result.Class && health.PermanentSinceUtc is { } since &&
                                 nowUtc - since <= PermanentWindow && health.PermanentEventId != item.EventId);
                if (repeated)
                {
                    result = result with { Scope = NotifyScope.Channel, Permanent = false, Class = "cli_contract" };
                    health.PermanentClass = null;
                    health.PermanentSinceUtc = null;
                    health.PermanentEventId = null;
                }
                else
                {
                    health.PermanentClass = result.Class;
                    health.PermanentSinceUtc = nowUtc;
                    health.PermanentEventId = item.EventId;
                }
            }

            if (result.Scope == NotifyScope.Channel)
            {
                Block(health, result, nowUtc, attemptFingerprint);
                blocked = true;
                if (result.Ambiguous)
                {
                    // 通道级的不明结果(超时、连接中断)同样可能已送达:记下首次不明时间,超出 uuid 窗口后不再重发这一条。
                    attempt ??= new AttemptState { FirstAttemptUtc = nowUtc };
                    attempt.Attempts++;
                    attempt.LastClass = result.Class;
                    attempt.AmbiguousSinceUtc ??= nowUtc;
                    attempts[item.EventId] = attempt;
                    stateChanged = true;
                }

                items.Add(new NotifyItemResult(item.EventId, NotifyOutcome.Failed, result.Class, item.Source)
                {
                    Attempt = result, Attempts = attempt?.Attempts ?? 0,
                });
                failed++;
                continue;
            }

            if (result.Permanent)
            {
                if (DeadOrBackoff(item, result, attempts, nowUtc, items, result.Class, ref stateChanged) && group is not null)
                {
                    BuryGroup(group, result, result.Class, items, attempts, settled, ref stateChanged);
                }

                failed++;
                continue;
            }

            attempt ??= new AttemptState { FirstAttemptUtc = nowUtc };
            attempt.Attempts++;
            attempt.LastClass = result.Class;
            attempt.LastErrorCode = result.ErrorCode;
            attempt.LastLogId = result.LogId;
            if (result.Ambiguous)
            {
                attempt.AmbiguousSinceUtc ??= nowUtc;
            }

            if (nowUtc - attempt.FirstAttemptUtc > DeadAfter)
            {
                attempts[item.EventId] = attempt;
                string exhausted = "retry_exhausted:" + result.Class;
                if (DeadOrBackoff(item, result, attempts, nowUtc, items, exhausted, ref stateChanged) && group is not null)
                {
                    BuryGroup(group, result, exhausted, items, attempts, settled, ref stateChanged);
                }

                failed++;
                continue;
            }

            attempt.NextAttemptUtc = nowUtc + ItemBackoff(attempt.Attempts);
            attempts[item.EventId] = attempt;
            stateChanged = true;
            items.Add(new NotifyItemResult(item.EventId, NotifyOutcome.Failed, result.Class, item.Source)
            {
                Attempt = result, Attempts = attempt.Attempts,
            });
            failed++;
        }

        // 剪枝:丢弃记录时间早于 now - 7 天的去重条目，以及文件已经不在的退避状态。
        DateTimeOffset cutoff = nowUtc - DuplicateWindow;
        foreach (string key in seen.Where(kv => kv.Value < cutoff).Select(kv => kv.Key).ToList())
        {
            seen.Remove(key);
            seenChanged = true;
        }

        var live = queue.Select(item => item.EventId).ToHashSet(StringComparer.Ordinal);
        foreach (string key in attempts.Keys.Where(key => !live.Contains(key)).ToList())
        {
            attempts.Remove(key);
            stateChanged = true;
        }

        if (seenChanged && WriteSeen(seen) is { } writeDiagnostic)
        {
            diagnostics.Add(writeDiagnostic);
        }

        if (stateChanged && WriteJson(_statePath, attempts) is { } stateDiagnostic)
        {
            diagnostics.Add(stateDiagnostic.Replace("seen_", "state_", StringComparison.Ordinal));
        }

        foreach (string session in lastSentCompletion.Where(kv => nowUtc - kv.Value > SessionLedgerKeep).Select(kv => kv.Key).ToList())
        {
            lastSentCompletion.Remove(session);
            sessionsChanged = true;
        }

        if (sessionsChanged && WriteJson(_sessionsPath, lastSentCompletion) is { } sessionsDiagnostic)
        {
            diagnostics.Add(sessionsDiagnostic.Replace("seen_", "sessions_", StringComparison.Ordinal));
        }

        foreach (string key in limitCycles.Where(kv => kv.Value <= nowUtc).Select(kv => kv.Key).ToList())
        {
            limitCycles.Remove(key);
            limitCyclesChanged = true;
        }

        if (limitCyclesChanged && WriteJson(_limitsPath, limitCycles) is { } limitsDiagnostic)
        {
            diagnostics.Add(limitsDiagnostic.Replace("seen_", "limits_", StringComparison.Ordinal));
        }

        health.Backlog = items.Count(item => item.Outcome is NotifyOutcome.Deferred or NotifyOutcome.Failed or NotifyOutcome.Skipped);
        health.Dead = CountDead();
        // 熔断到期但这轮没有可探测的条目时保持 blocked:直到下一次真实尝试给出结论，界面不得提前转绿。
        health.UpdatedAtUtc = nowUtc;
        WriteJson(_healthPath, health);

        return new NotifySweepResult(items.Count, sent, duplicate, malformed, failed, skipped, items)
        {
            Diagnostics = diagnostics,
            Deferred = deferred,
            ChannelBlockedUntilUtc = blocked ? health.BlockedUntilUtc : null,
            ChannelReason = blocked ? health.Reason : null,
        };
    }

    /// <summary>条目级重试间隔:30s×2^(n-1),上限 15 分钟,±20% 抖动(避免同一时刻一起重试)。</summary>
    public static TimeSpan ItemBackoff(int attempts)
    {
        double seconds = Math.Min(30 * Math.Pow(2, Math.Max(0, attempts - 1)), 15 * 60);
        return TimeSpan.FromSeconds(seconds * (0.8 + Random.Shared.NextDouble() * 0.4));
    }

    /// <summary>
    /// 通道级熔断时长:按类别，连续失败加倍。凭据/权限类等人处理，设置一变即解除。副本读不出(可能只是正被占用)
    /// 与钥匙串一样短退避;换令牌应答异常(可能是代理或网关一时拦截)与网络一样短退避——重试只是一次本地解密或一个小请求。
    /// </summary>
    public static TimeSpan ChannelBackoff(string @class, int consecutive, int? retryAfterSeconds)
    {
        int n = Math.Max(1, consecutive);
        return @class switch
        {
            "keychain_unavailable" or "credential_unreadable" => TimeSpan.FromSeconds(Math.Min(60 * Math.Pow(2, n - 1), 600)),
            "rate_limit" => TimeSpan.FromSeconds(retryAfterSeconds ?? Math.Min(Math.Pow(2, n), 60)),
            "network" or "network_timeout" or "token_unexpected" => TimeSpan.FromSeconds(Math.Min(30 * Math.Pow(2, n - 1), 300)),
            _ => TimeSpan.FromMinutes(30),
        };
    }

    private string? WriteSeen(Dictionary<string, DateTimeOffset> seen) =>
        WriteJson(_seenPath, seen.ToDictionary(kv => kv.Key, kv => kv.Value.ToString("o", CultureInfo.InvariantCulture)));

    /// <summary>
    /// 占住一条待发事件(允许读，不允许删)。文件已经不在(被撤回，或别处处理掉了)→ <paramref name="gone"/>;
    /// 被别人占着等其它原因打不开 → 不占，照常发送(与不加占用时的行为一致)。
    /// </summary>
    private static FileStream? TryClaim(string path, out bool gone)
    {
        gone = false;
        try
        {
            return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            gone = true;
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static void Block(DeliveryHealth health, NotifySendResult result, DateTimeOffset nowUtc, string fingerprint)
    {
        bool sameReason = health.State == "blocked" && health.Reason == result.Class;
        health.ConsecutiveChannelFailures = sameReason ? health.ConsecutiveChannelFailures + 1 : 1;
        health.State = "blocked";
        health.Reason = result.Class;
        health.BlockedSinceUtc = sameReason ? health.BlockedSinceUtc ?? nowUtc : nowUtc;
        health.BlockedUntilUtc = nowUtc + ChannelBackoff(result.Class, health.ConsecutiveChannelFailures, result.RetryAfterSeconds);
        health.BlockFingerprint = fingerprint;
    }

    private static void MarkHealthy(DeliveryHealth health, DateTimeOffset nowUtc)
    {
        health.State = "healthy";
        health.Reason = null;
        health.BlockedSinceUtc = null;
        health.BlockedUntilUtc = null;
        health.ConsecutiveChannelFailures = 0;
        health.BlockFingerprint = null;
        health.LastSuccessUtc = nowUtc;
        health.LastErrorClass = null;
        health.LastErrorType = null;
        health.LastErrorSubtype = null;
        health.LastErrorCode = null;
        health.LastLogId = null;
        health.PermanentClass = null;
        health.PermanentSinceUtc = null;
        health.PermanentEventId = null;
    }

    private string SafeFingerprint()
    {
        try
        {
            return _channelFingerprint() ?? string.Empty;
        }
        catch (Exception)
        {
            return string.Empty;
        }
    }

    private static TimeSpan AgeOf(QueuedEvent item, DateTimeOffset nowUtc) =>
        item.AtUtc is { } at ? nowUtc - at : TimeSpan.MaxValue;

    /// <summary>读一项外部事实;没注入或读的时候抛了都按「不知道」——事实读不了不能让一条通知卡住整轮。</summary>
    private T? ReadFact<T>(Func<INotifyFacts, T?> read)
    {
        if (_facts is null)
        {
            return default;
        }

        try
        {
            return read(_facts);
        }
        catch (Exception)
        {
            return default;
        }
    }

    private string? SessionNameOf(QueuedEvent item) =>
        item.IsClaudeCode ? ReadFact(facts => facts.SessionName(item.SessionId, item.TranscriptPath)) : null;

    /// <summary>
    /// 限额周期键:转录里服务端给的窗口与重置时间(账号级,同一账号的会话一致),再加上配置根——
    /// 两个账号(两个 <c>CLAUDE_CONFIG_DIR</c>)可能恰好在同一个整点重置。读不到时退回按会话,5 小时内只提醒一次。
    /// </summary>
    internal static string LimitCycleKey(QueuedEvent item, LimitFacts? facts) =>
        facts?.ResetsAtUtc is { } resetsAt
            ? "rate_limit|" + ConfigRootTag(item.TranscriptPath) + "|" + (facts.LimitType ?? "unknown") + "|" +
              resetsAt.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture)
            : "session|" + (item.SessionId ?? item.EventId);

    /// <summary>转录所在配置根的短指纹(规范化路径的 SHA-256 前 8 位),记账文件里不留路径。</summary>
    private static string ConfigRootTag(string? transcriptPath) =>
        NotifyFacts.ClaudeRootOf(transcriptPath) is { } root
            ? Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(root.TrimEnd('\\', '/').ToLowerInvariant())))[..8]
            : "unknown";

    /// <summary>
    /// 构造通知文本：第一行说发生了什么，第二行「来源 · 模型 · 时间 · 「会话名」」,需要人动手时第三行写清怎么做。
    /// 决策类用 ⏳ 打头并写明**在等什么**:完成说的是"结束了",决策说的是"停在这儿等你",紧迫程度完全不同。
    /// </summary>
    internal static string BuildText(QueuedEvent item, DateTimeOffset nowUtc, string? sessionName = null)
    {
        string projectName = ProjectDisplayName(item.Cwd, sessionName);
        DateTimeOffset localTime = item.AtUtc?.ToLocalTime() ?? nowUtc.ToLocalTime();
        string meta = MetaLine(item.Source, item.Model, localTime, SessionLabel(projectName, sessionName), item.Bridged);

        (string heading, string? advice) = item switch
        {
            // 测试通知只走「队列 → 后台服务 → lark-cli → 飞书」这一段,不经过任何客户端的 hook。
            { Smoke: true } => ($"🧪 {projectName} 测试通知", "能收到这条,说明飞书投递这一段是通的"),
            { Kind: "decision" } => ($"⏳ {projectName} {DecisionLabel(item.NotificationType)}", null),
            { Kind: "failure" } => FailureText(projectName, item.Error),
            { Kind: "resume" } => ResumeText(projectName, item.NotificationType, item.Detail),
            _ => ($"✅ {projectName} 已完成", null),
        };
        return advice is null ? $"{heading.Trim()}\n{meta}" : $"{heading.Trim()}\n{meta}\n{advice}";
    }

    /// <summary>
    /// 一个限额周期一条消息:哪些会话停了、哪个额度用完、预计何时重置、AI Resume 会不会续跑它们。
    /// 续跑句只按投递那一刻的布防快照说;快照读不了就说「未核实」,绝不沿用「会按布防自动续跑」这种不看状态的套话。
    /// </summary>
    internal static string BuildLimitText(IReadOnlyList<LimitSession> sessions, LimitFacts? window, ArmSnapshot? arm, DateTimeOffset nowUtc)
    {
        LimitSession lead = sessions[0];
        DateTimeOffset localTime = lead.Event.AtUtc?.ToLocalTime() ?? nowUtc.ToLocalTime();
        // 只有转录里有服务端给的额度窗口与重置时间,才说得出「用量上限」;否则只知道撞了限额(也可能是 API 的速率限制)。
        string what = window?.ResetsAtUtc is null ? "被限额打断" : "被用量上限打断";
        var lines = new List<string>();
        if (sessions.Count == 1)
        {
            string name = ProjectDisplayName(lead.Event.Cwd, lead.SessionName);
            lines.Add($"⏸️ {name} {what}");
            lines.Add(MetaLine(lead.Event.Source, lead.Model, localTime, SessionLabel(name, lead.SessionName), lead.Bridged));
        }
        else
        {
            string[] names = sessions.Select(session => ProjectDisplayName(session.Event.Cwd, session.SessionName))
                .Distinct(StringComparer.Ordinal).ToArray();
            lines.Add($"⏸️ {sessions.Count} 个会话{what}:" + string.Join("、", names.Take(3)) + (names.Length > 3 ? " 等" : string.Empty));
            string? model = sessions.Select(session => ModelLabel(session.Model)).Distinct(StringComparer.Ordinal).Count() == 1 ? lead.Model : null;
            lines.Add(MetaLine(lead.Event.Source, model, localTime, null, false));
        }

        lines.Add(ResetLine(window, nowUtc));

        ResumeOutlook[] outlooks = sessions.Select(session => OutlookFor(session.Event, arm)).ToArray();
        bool sameOutlook = outlooks.Distinct().Count() == 1;
        bool sameClaude = sessions.Select(session => session.ClaudeGaveUp).Distinct().Count() == 1;
        if (sameOutlook && sameClaude)
        {
            lines.Add(ResumeSentence(outlooks[0], lead.ClaudeGaveUp, sessions.Count > 1));
        }
        else
        {
            for (int i = 0; i < sessions.Count; i++)
            {
                string name = ProjectDisplayName(sessions[i].Event.Cwd, sessions[i].SessionName);
                lines.Add($"· {name}{SessionSuffix(name, sessions[i].SessionName)}:" + ResumeShort(outlooks[i], sessions[i].ClaudeGaveUp));
            }

            if (outlooks.Any(outlook => outlook is ResumeOutlook.NotArmed or ResumeOutlook.NotInScope or ResumeOutlook.OtherSession or
                    ResumeOutlook.Handled))
            {
                lines.Add("不会自动续跑的会话,额度恢复后在原会话或手机远程里继续");
            }
        }

        return string.Join("\n", lines);
    }

    /// <summary>这个会话额度恢复后会不会被 AI Resume 续跑。只看布防绑定的精确会话 UUID:同项目的其它会话不续(ADR-0006)。</summary>
    internal enum ResumeOutlook { Unknown, NotArmed, Stopped, NeedsRearm, LoginHeld, NotInScope, OtherSession, Handled, WillResume }

    internal static ResumeOutlook OutlookFor(QueuedEvent item, ArmSnapshot? arm)
    {
        if (arm is null || string.IsNullOrWhiteSpace(item.SessionId))
        {
            return ResumeOutlook.Unknown;
        }

        if (!arm.Armed)
        {
            return ResumeOutlook.NotArmed;
        }

        if (arm.Stopped)
        {
            return ResumeOutlook.Stopped;
        }

        ArmedProject? bound = arm.Projects.FirstOrDefault(project =>
            string.Equals(project.SessionId, item.SessionId, StringComparison.OrdinalIgnoreCase));
        if (bound is not null)
        {
            // 「会续跑」是飞书里唯一一句肯定的承诺:引擎此刻已知会一直等下去(要重新布防、登录失效、监视卡住)就不能这么说。
            return arm.Hold == ArmSnapshot.HoldStalled ? ResumeOutlook.Unknown
                : arm.Hold == ArmSnapshot.HoldRearm || bound.NeedsRearm ? ResumeOutlook.NeedsRearm
                : bound.Handled && !arm.Continuous ? ResumeOutlook.Handled
                : arm.Hold == ArmSnapshot.HoldLogin ? ResumeOutlook.LoginHeld
                : ResumeOutlook.WillResume;
        }

        ArmedProject[] sameProject = arm.Projects
            .Where(project => AiResume.Worker.Products.ProjectPaths.Equal(project.Path, item.Cwd))
            .ToArray();
        // 旧配置的项目没有绑定会话:引擎认不出哪个会话该续,重新布防才会绑定。
        return sameProject.Any(project => string.IsNullOrWhiteSpace(project.SessionId)) ? ResumeOutlook.NeedsRearm
            : sameProject.Length > 0 ? ResumeOutlook.OtherSession
            : ResumeOutlook.NotInScope;
    }

    private static string ResumeSentence(ResumeOutlook outlook, bool claudeGaveUp, bool plural)
    {
        string them = plural ? "它们" : "它";
        string claude = claudeGaveUp ? ";Claude Code 也不会自己继续" : string.Empty;
        const string manual = ";额度恢复后在原会话或手机远程里继续";
        return outlook switch
        {
            ResumeOutlook.NotArmed => "AI Resume 未布防,不会自动续跑" + claude + manual,
            ResumeOutlook.Stopped => "AI Resume 本轮布防已停止,不会再自动续跑" + claude + ";检查后在控制面解除并重新布防",
            ResumeOutlook.NeedsRearm => $"AI Resume 要重新布防才会续跑{them}" + claude + ";在控制面解除并重新布防",
            ResumeOutlook.LoginHeld => $"已布防,但 AI Resume 读不到 Claude 登录,恢复前不会续跑{them}" + claude + ";在终端运行 claude 并执行 /login",
            ResumeOutlook.NotInScope => "不在 AI Resume 布防范围,不会自动续跑" + claude + manual,
            ResumeOutlook.OtherSession => (plural
                ? "AI Resume 布防的是它们所在项目的另一个会话,不会续跑它们"
                : "AI Resume 布防的是这个项目的另一个会话,不会续跑这一个") + claude + manual,
            ResumeOutlook.Handled => $"一次性布防这一轮已处理过{them},AI Resume 不会再续跑" + claude + manual,
            // 会话开在客户端时 AI Resume 一律不续跑(交给客户端),关掉才续——这句只说 AI Resume 自己会做什么,
            // 不替 Claude Code 承诺它会不会自己接上(Remote Control 会话、跨模型族的周额度它都不等)。
            ResumeOutlook.WillResume => $"已布防:额度恢复后 AI Resume 会续跑{them}" +
                (claudeGaveUp ? ";Claude Code 不会自己继续,会话还开着的话要先关掉" : ";会话还开着的话 AI Resume 不插手,关掉后才续"),
            _ => "AI Resume 布防状态未核实,不确定会不会自动续跑" + claude,
        };
    }

    private static string ResumeShort(ResumeOutlook outlook, bool claudeGaveUp) => outlook switch
    {
        ResumeOutlook.NotArmed => "未布防,不会自动续跑",
        ResumeOutlook.Stopped => "本轮布防已停止,不会再自动续跑",
        ResumeOutlook.NeedsRearm => "要重新布防才会续跑",
        ResumeOutlook.LoginHeld => "Claude 登录失效,恢复前不会续跑",
        ResumeOutlook.NotInScope => "不在布防范围,不会自动续跑",
        ResumeOutlook.OtherSession => "布防的是同项目另一个会话,不会续跑它",
        ResumeOutlook.Handled => "这一轮已处理过,不会再自动续跑",
        ResumeOutlook.WillResume => "额度恢复后会自动续跑(开着的话关掉后才续)",
        _ => "布防状态未核实",
    } + (!claudeGaveUp ? string.Empty
        : outlook == ResumeOutlook.WillResume ? ";Claude Code 不会自己继续"
        : ";Claude Code 也不会自己继续");

    /// <summary>哪个额度用完、服务端说何时重置;读不到就直说未核实,不用本地估算,也不断言是用量上限。</summary>
    private static string ResetLine(LimitFacts? window, DateTimeOffset nowUtc)
    {
        if (window?.ResetsAtUtc is not { } resetsAt)
        {
            return "是哪个额度、何时恢复未核实";
        }

        string label = window.LimitType?.Trim().ToLowerInvariant() switch
        {
            "five_hour" => "5 小时额度",
            "seven_day" => "周额度",
            "seven_day_opus" => "Opus 周额度",
            "seven_day_sonnet" => "Sonnet 周额度",
            _ => "用量额度",
        };
        string when = LocalMoment(resetsAt, nowUtc);
        return resetsAt > nowUtc ? $"{label}已用完,预计 {when} 重置" : $"{label}已在 {when} 重置";
    }

    /// <summary>当天只写时刻,明天写「明天」,更远写月/日。</summary>
    private static string LocalMoment(DateTimeOffset utc, DateTimeOffset nowUtc)
    {
        DateTimeOffset local = utc.ToLocalTime();
        DateTime today = nowUtc.ToLocalTime().Date;
        return local.Date == today ? $"{local:HH:mm}"
            : local.Date == today.AddDays(1) ? $"明天 {local:HH:mm}"
            : $"{local.Month}/{local.Day} {local:HH:mm}";
    }

    private static string MetaLine(string? source, string? model, DateTimeOffset localTime, string? sessionLabel, bool bridged)
    {
        string? label = ModelLabel(model);
        return SourceDisplayName(source) + (label is null ? string.Empty : " · " + label) + $" · {localTime:HH:mm}" +
               (sessionLabel is null ? string.Empty : " · " + sessionLabel) + (bridged ? " · 远程已连接" : string.Empty);
    }

    /// <summary>
    /// 用户认得出的项目名:工作目录末级。Claude 桌面端的临时工作区(<c>scratch-workspaces\…\scratch-YYYY-MM-DD-xxxxxx</c>)
    /// 目录名对人没有意义,改用会话名,没有就写「临时会话」。判据与控制面续跑队列共用
    /// <see cref="AiResume.Worker.Products.ProjectNames.IsScratchWorkspace"/>。
    /// </summary>
    internal static string ProjectDisplayName(string? cwd, string? sessionName)
    {
        if (AiResume.Worker.Products.ProjectNames.IsScratchWorkspace(cwd))
        {
            // 清洗后为空(名字只有零宽或方向控制字符)同样算没有名字。
            string name = string.IsNullOrWhiteSpace(sessionName) ? string.Empty : Shorten(sessionName);
            return name.Length == 0 ? "临时会话" : name;
        }

        string folder = string.IsNullOrWhiteSpace(cwd) ? string.Empty : Path.GetFileName(cwd.TrimEnd('\\', '/'));
        return string.IsNullOrEmpty(folder) ? cwd ?? string.Empty : folder;
    }

    /// <summary>第二行的「会话名」:项目名已经就是会话名(临时工作区)时不重复。</summary>
    private static string? SessionLabel(string projectName, string? sessionName)
    {
        string name = string.IsNullOrWhiteSpace(sessionName) ? string.Empty : Shorten(sessionName);
        return name.Length == 0 || string.Equals(name, projectName, StringComparison.Ordinal) ? null : "「" + name + "」";
    }

    private static string SessionSuffix(string projectName, string? sessionName) => SessionLabel(projectName, sessionName) ?? string.Empty;

    /// <summary>
    /// 会话名清洗后截到 20 个字符(按文本元素,不切断表情或代理对)。自动标题来自对话内容,
    /// 换行与控制字符换成空格(<see cref="AiResume.Worker.Products.ProjectNames.Clean"/>),免得在飞书里伪造出单独的一行。
    /// </summary>
    private static string Shorten(string value)
    {
        string cleaned = AiResume.Worker.Products.ProjectNames.Clean(value);
        var info = new StringInfo(cleaned);
        return info.LengthInTextElements <= 20 ? cleaned : info.SubstringByTextElements(0, 19) + "…";
    }

    /// <summary>
    /// 事件类型 → 一句人话。上游的 notification_type 只在 payload 里出现时才有值;
    /// 认不出来就说「等你决定」,绝不编一个更具体的说法。
    /// </summary>
    private static string DecisionLabel(string? notificationType)
        => notificationType?.Trim().ToLowerInvariant() switch
        {
            "agent_needs_input" => "等你输入",
            "elicitation_dialog" or "elicitation_url_dialog" => "等你确认",
            "permission_prompt" => "等你授权",
            // agent team 里的协作 agent 卡在工具或网络授权上:整件事停在那儿。
            "worker_permission_prompt" => "协作 agent 等你授权",
            "idle_prompt" => "在等你回来",
            "execution_failed" => "运行失败,等你处理",
            // Claude Code 原生自动继续:额度已重置但没接上(原文 press enter to continue)。
            "quota_auto_resume_stale" => "额度已恢复,按回车即可继续",
            // Claude Code 的这类事件都并入用量上限通知(BuildLimitText);这里只兜底别的来源。
            "quota_auto_resume_disabled" => "被用量上限打断,不会自己继续",
            _ => "等你决定",
        };

    /// <summary>Claude Code StopFailure 的 error 枚举(官方 hooks 文档)→ 中断原因。rate_limit 走 <see cref="BuildLimitText"/>。</summary>
    private static (string Heading, string? Advice) FailureText(string project, string? error) => error switch
    {
        "rate_limit" => ($"⏸️ {project} 被用量上限打断", null),
        "authentication_failed" => ($"⚠️ {project} 因登录失效中断", "在终端运行 claude 并执行 /login"),
        "billing_error" => ($"⚠️ {project} 因账单问题中断", null),
        "oauth_org_not_allowed" or "account_on_hold" => ($"⚠️ {project} 因账号受限中断", null),
        "server_error" or "overloaded" or "api_error" => ($"⚠️ {project} 因服务端错误中断", null),
        _ => ($"⚠️ {project} 运行中断", null),
    };

    /// <summary>AI Resume 续跑引擎自己的提醒(source=ai-resume,kind=resume)。</summary>
    private static (string Heading, string? Advice) ResumeText(string project, string? type, string? detail) => type switch
    {
        "session-open" => ($"⏳ {project} 额度已恢复,会话仍开在客户端",
            "在原会话或手机远程里直接继续;关闭会话后 AI Resume 会自动续跑"),
        "resume-started" => ($"▶️ {project} 已开始自动续跑", null),
        "resume-succeeded" => ($"✅ {project} 自动续跑完成", null),
        // stopped:续跑中解除或重新布防、周期作废,引擎把进程停了——这是用户(或配置变化)让它停的,不说「锁住」。
        "resume-failed" when detail == "stopped" => ($"⏹️ {project} 自动续跑已中止", "打开会话检查结果;需要时重新布防"),
        "resume-failed" when detail == "cancel-pending" => ($"⚠️ {project} 自动续跑正在终止,进程还没退出",
            "确认进程退出前不会启动新的续跑"),
        "resume-failed" => ($"⚠️ {project} 自动续跑未确认完成" +
            (detail is null || RunStatusText(detail) is "未确认完成" ? string.Empty : $"({RunStatusText(detail)})"),
            "打开会话检查结果;这一轮不会再自动续跑,检查后解除并重新布防"),
        // 读不到额度的原因各不相同,建议必须跟着原因走:令牌过期等续期就好,登录失效只能 /login,凭据挪走了打开 Claude Code 也没用。
        "credentials-unavailable" => detail switch
        {
            "token_expired" => ("⚠️ Claude 访问令牌已过期,AI Resume 暂时读不到额度", "打开一次 Claude Code(或保持手机远程连接)即可自动续期"),
            "login_expired" or "no_credentials" => ("⚠️ Claude Code 登录已失效,AI Resume 读不到额度", "在终端运行 claude 并执行 /login"),
            "credential_store_moved" => ("⚠️ Claude Code 把登录挪进了 Windows 凭据管理器", "AI Resume 读不到它,额度无法核实,自动续跑会一直等待"),
            "account_unverified" => ("⚠️ Claude 账号身份未核实,自动续跑在等待", "打开一次 Claude Code,让它写入账号信息"),
            _ => ("⚠️ Claude 登录状态未核实,AI Resume 读不到额度", "在控制面「Claude 登录」查看原因"),
        },
        "resume-not-started" => ($"⚠️ {project} 一直没能开始自动续跑" + (detail switch
        {
            "no-claude" => "(找不到 Claude Code 或项目目录)",
            "execution-unverified" => "(执行条件未核实)",
            _ => string.Empty,
        }), "打开控制面查看这一行的原因;修好后会自动重试"),
        "provider-changed" => ($"⚠️ {project} 的 Claude 账号与布防时不同",
            "换了账号,或刚从 v2.1 升级(账号身份的算法变了):确认账号后解除并重新布防"),
        "login-expiring" => ("⚠️ Claude Code 登录即将到期" + (detail is null ? string.Empty : $"({detail})"),
            "在终端运行 claude 并执行 /login,否则到期后读不到额度、无法自动续跑"),
        "login-expired" => ("⚠️ Claude Code 登录已过期", "在终端运行 claude 并执行 /login"),
        _ => ($"ℹ️ {project} {type}", null),
    };

    /// <summary>续跑结果的状态码 → 与界面队列行同一套说法(<c>ControlPlaneBridge.DescribeProjectStatus</c>),飞书里不露原始码。</summary>
    private static string RunStatusText(string status) => status switch
    {
        "exit-null" => "未确认完成",
        "limited-side-effects" => "撞限前已有操作",
        "error" => "失败",
        "no-claude" => "找不到程序或目录",
        "prompt-multiline" => "续跑提示词无效",
        "launch-error" => "启动失败",
        "configuration-changed" => "配置已改变",
        "execution-unverified" => "执行条件未核实",
        "registry-error" => "进程登记失败",
        "monitor-error" => "进程监控失败",
        "run-unverified" => "状态未核实",
        _ when status.StartsWith("exit-", StringComparison.Ordinal) => "进程异常退出",
        _ => "未知异常",
    };

    private static string SourceDisplayName(string? source)
        => source?.Trim().ToLowerInvariant() switch
        {
            "claude" or "claude-code" or "claudecode" => "Claude Code",
            "codex" => "Codex",
            "cline" => "Cline",
            "qoder" => "Qoder",
            "opencode" => "OpenCode",
            "ai-resume" => "AI Resume",
            _ => "未知客户端",
        };

    /// <summary><c>claude-opus-5-5</c> → <c>Opus 5.5</c>;其它模型 ID 原样。</summary>
    public static string? ModelLabel(string? modelId)
    {
        if (string.IsNullOrWhiteSpace(modelId))
        {
            return null;
        }

        if (!modelId.StartsWith("claude-", StringComparison.Ordinal))
        {
            return modelId;
        }

        string[] parts = modelId["claude-".Length..].Split('-', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0 || parts[0].Length == 0)
        {
            return modelId;
        }

        string family = char.ToUpperInvariant(parts[0][0]) + parts[0][1..];
        string[] version = parts.Skip(1).TakeWhile(part => part.Length <= 2 && part.All(char.IsAsciiDigit)).ToArray();
        return version.Length == 0 ? family : family + " " + string.Join(".", version);
    }

    /// <summary>
    /// 移入死信(返回 true);移动失败(文件被占用等)时保留这一条的退避状态、15 分钟后再试,
    /// 而不是删掉状态后每 15 秒原样重发一次。
    /// </summary>
    private bool DeadOrBackoff(QueuedEvent item, NotifySendResult result, Dictionary<string, AttemptState> attempts,
        DateTimeOffset nowUtc, List<NotifyItemResult> items, string detail, ref bool stateChanged)
    {
        attempts.TryGetValue(item.EventId, out AttemptState? attempt);
        if (MoveToDead(item, result))
        {
            stateChanged |= attempts.Remove(item.EventId);
            items.Add(new NotifyItemResult(item.EventId, NotifyOutcome.Dead, detail, item.Source)
            {
                Attempt = result, Attempts = attempt?.Attempts ?? 0,
            });
            return true;
        }

        attempt ??= new AttemptState { FirstAttemptUtc = nowUtc };
        attempt.NextAttemptUtc = nowUtc + TimeSpan.FromMinutes(15);
        attempts[item.EventId] = attempt;
        stateChanged = true;
        items.Add(new NotifyItemResult(item.EventId, NotifyOutcome.Failed, "dead_move_failed:" + detail, item.Source)
        {
            Attempt = result, Attempts = attempt.Attempts,
        });
        return false;
    }

    /// <summary>
    /// 代表限额组的那条刚进了死信:同组其余几条说的是同一件事,一起进死信,不再轮流换一条重发。
    /// 移动失败的留在队列里,下一轮重新分组。
    /// </summary>
    private void BuryGroup(LimitGroup group, NotifySendResult result, string detail, List<NotifyItemResult> items,
        Dictionary<string, AttemptState> attempts, HashSet<string> settled, ref bool stateChanged)
    {
        foreach (QueuedEvent member in group.Members.Where(member => !ReferenceEquals(member, group.Lead)))
        {
            if (MoveToDead(member, result))
            {
                stateChanged |= attempts.Remove(member.EventId);
                settled.Add(member.EventId);
                items.Add(new NotifyItemResult(member.EventId, NotifyOutcome.Dead, detail, member.Source) { Attempt = result });
            }
        }
    }

    /// <summary>这一组提到的每个会话都按这个限额周期记账(同一会话的 StopFailure 与原生自动继续放弃共用一条)。</summary>
    private static void RecordCycle(LimitGroup group, Dictionary<string, DateTimeOffset> limitCycles, DateTimeOffset nowUtc)
    {
        DateTimeOffset expires = CycleExpiry(group.Window, group.Lead.AtUtc ?? nowUtc, nowUtc);
        foreach (string ledgerKey in group.LedgerKeys)
        {
            if (!limitCycles.TryGetValue(ledgerKey, out DateTimeOffset existing) || existing < expires)
            {
                limitCycles[ledgerKey] = expires;
            }
        }
    }

    /// <summary>
    /// 周期记账的失效时间:按服务端重置时间的,到重置后再留一小时(时钟偏差、迟到事件);按会话的,从中断发生起 5 小时
    /// (通道熔断几小时后才送出也不顺延)。重置时间离谱(超出一个月)时退回按会话,免得日期运算溢出。
    /// </summary>
    private static DateTimeOffset CycleExpiry(LimitFacts? window, DateTimeOffset occurredUtc, DateTimeOffset nowUtc)
    {
        if (window?.ResetsAtUtc is { } resetsAt && resetsAt < nowUtc.AddDays(31))
        {
            return (resetsAt > nowUtc ? resetsAt : nowUtc) + LimitCycleSlack;
        }

        return (occurredUtc > nowUtc ? nowUtc : occurredUtc) + SessionLimitKeep;
    }

    private bool MoveToDead(QueuedEvent item, NotifySendResult result)
    {
        if (!TryMoveTo(item.FullPath, item.FileName, "dead"))
        {
            return false;
        }

        try
        {
            string errorPath = Path.Combine(_eventsDir, "dead", Path.GetFileNameWithoutExtension(item.FileName) + ".error.txt");
            File.WriteAllText(errorPath, JsonSerializer.Serialize(new
            {
                eventId = item.EventId, result.Class, result.ExitCode, result.ErrorType, result.ErrorSubtype,
                result.ErrorCode, result.LogId, atUtc = DateTimeOffset.UtcNow,
            }));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 死信说明写不进去不影响死信本身。
        }

        return true;
    }

    private Dictionary<string, DateTimeOffset> LoadSessions()
    {
        try
        {
            return File.Exists(_sessionsPath)
                ? JsonSerializer.Deserialize<Dictionary<string, DateTimeOffset>>(File.ReadAllText(_sessionsPath), StateJson)
                  ?? new Dictionary<string, DateTimeOffset>(StringComparer.Ordinal)
                : new Dictionary<string, DateTimeOffset>(StringComparer.Ordinal);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return new Dictionary<string, DateTimeOffset>(StringComparer.Ordinal);
        }
    }

    /// <summary>已提醒过的限额周期;读不了按空表处理(最坏多发一条,不会漏发)。</summary>
    private Dictionary<string, DateTimeOffset> LoadLimitCycles()
    {
        try
        {
            return File.Exists(_limitsPath)
                ? new Dictionary<string, DateTimeOffset>(
                    JsonSerializer.Deserialize<Dictionary<string, DateTimeOffset>>(File.ReadAllText(_limitsPath), StateJson)
                    ?? new Dictionary<string, DateTimeOffset>(), StringComparer.Ordinal)
                : new Dictionary<string, DateTimeOffset>(StringComparer.Ordinal);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return new Dictionary<string, DateTimeOffset>(StringComparer.Ordinal);
        }
    }

    private int CountDead()
    {
        try
        {
            string dead = Path.Combine(_eventsDir, "dead");
            return Directory.Exists(dead) ? Directory.GetFiles(dead, "*.json").Length : 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }

    /// <summary>把事件移入子目录(malformed / dead);目标已存在则加 Guid 后缀。</summary>
    private bool TryMoveTo(string fullPath, string fileName, string folder)
    {
        try
        {
            string directory = Path.Combine(_eventsDir, folder);
            Directory.CreateDirectory(directory);
            string dest = Path.Combine(directory, fileName);
            if (File.Exists(dest))
            {
                dest = Path.Combine(directory, Path.GetFileNameWithoutExtension(fileName) + "-" + Guid.NewGuid().ToString("N") + Path.GetExtension(fileName));
            }

            File.Move(fullPath, dest);
            return true;
        }
        catch (IOException)
        {
            // 移动失败:保留原文件,下轮再试。
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    private Dictionary<string, AttemptState> LoadState()
    {
        try
        {
            return File.Exists(_statePath)
                ? JsonSerializer.Deserialize<Dictionary<string, AttemptState>>(File.ReadAllText(_statePath), StateJson)
                  ?? new Dictionary<string, AttemptState>(StringComparer.Ordinal)
                : new Dictionary<string, AttemptState>(StringComparer.Ordinal);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return new Dictionary<string, AttemptState>(StringComparer.Ordinal);
        }
    }

    /// <summary>加载去重表;文件不存在/空/无法解析 → 空表,不抛。</summary>
    private Dictionary<string, DateTimeOffset> LoadSeen(out string? diagnostic)
    {
        diagnostic = null;
        var result = new Dictionary<string, DateTimeOffset>(StringComparer.Ordinal);
        if (!File.Exists(_seenPath))
        {
            return result;
        }

        try
        {
            string json = File.ReadAllText(_seenPath);
            if (string.IsNullOrWhiteSpace(json))
            {
                return result;
            }

            var parsed = JsonSerializer.Deserialize<Dictionary<string, string>>(json);
            if (parsed is null)
            {
                diagnostic = "seen_read_invalid";
                return result;
            }

            bool invalidEntry = false;
            foreach (KeyValuePair<string, string> kv in parsed)
            {
                if (DateTimeOffset.TryParse(
                        kv.Value,
                        CultureInfo.InvariantCulture,
                        DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal,
                        out DateTimeOffset ts))
                {
                    result[kv.Key] = ts;
                }
                else
                {
                    invalidEntry = true;
                }
            }
            if (invalidEntry)
            {
                diagnostic = "seen_read_invalid";
            }
        }
        catch (IOException)
        {
            diagnostic = "seen_read_io";
        }
        catch (UnauthorizedAccessException)
        {
            diagnostic = "seen_read_denied";
        }
        catch (JsonException)
        {
            diagnostic = "seen_read_invalid";
        }

        return result;
    }

    /// <summary>原子写 JSON:临时文件 → Flush(true) → Move(overwrite)。返回诊断码或 null。</summary>
    private static string? WriteJson<T>(string path, T value)
    {
        try
        {
            string? dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            string tmpPath = path + ".tmp-" + Guid.NewGuid().ToString("N");
            try
            {
                string json = JsonSerializer.Serialize(value, StateJson);
                using (var fs = new FileStream(tmpPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                using (var writer = new StreamWriter(fs, new System.Text.UTF8Encoding(false)))
                {
                    writer.Write(json);
                    writer.Flush();
                    fs.Flush(true); // 落盘,防断电半截文件。
                }

                File.Move(tmpPath, path, overwrite: true);
            }
            catch
            {
                try
                {
                    if (File.Exists(tmpPath))
                    {
                        File.Delete(tmpPath);
                    }
                }
                catch
                {
                    // 清理失败不掩盖原始异常。
                }

                throw;
            }

            return null;
        }
        catch (IOException)
        {
            return "seen_write_io";
        }
        catch (UnauthorizedAccessException)
        {
            return "seen_write_denied";
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
            // 删除失败:文件留在原地,下轮可能重试;不抛。
        }
        catch (UnauthorizedAccessException)
        {
            // 同上。
        }
    }

    /// <summary>单条事件的退避状态(Worker 单写，跨重启保留)。</summary>
    internal sealed class AttemptState
    {
        public int Attempts { get; set; }

        public DateTimeOffset FirstAttemptUtc { get; set; }

        public DateTimeOffset? NextAttemptUtc { get; set; }

        public DateTimeOffset? AmbiguousSinceUtc { get; set; }

        public string? LastClass { get; set; }

        public long? LastErrorCode { get; set; }

        public string? LastLogId { get; set; }
    }

    /// <summary>同一限额周期里待发的中断事件,按发生时间排列;最早一条代表整组发送与退避,送达后整组记已送达。</summary>
    private sealed class LimitGroup(string key)
    {
        private readonly Dictionary<string, LimitFacts?> _facts = new(StringComparer.Ordinal);

        public string Key { get; } = key;

        public List<QueuedEvent> Members { get; } = [];

        /// <summary>组里每个会话的周期记账键(周期键 + 会话)。</summary>
        public HashSet<string> LedgerKeys { get; } = new(StringComparer.Ordinal);

        public QueuedEvent Lead => Members[0];

        /// <summary>撞的是哪个窗口、何时重置:组里第一条读得到重置时间的证据(按周期分的组里各条一致)。</summary>
        public LimitFacts? Window =>
            Members.Select(member => _facts.GetValueOrDefault(member.EventId)).FirstOrDefault(facts => facts?.ResetsAtUtc is not null);

        public void Add(QueuedEvent item, LimitFacts? facts, string ledgerKey)
        {
            Members.Add(item);
            _facts[item.EventId] = facts;
            LedgerKeys.Add(ledgerKey);
        }

        /// <summary>
        /// 组里各条都已满稳定期(没满的不分组);还有刚到、尚未分组的中断时先等它,最早一条满 2 分钟就不再等。
        /// </summary>
        public bool Ready(DateTimeOffset nowUtc, bool othersSettling) =>
            !othersSettling || AgeOf(Lead, nowUtc) >= LimitGroupCap;

        /// <summary>按会话归并(同一会话的 StopFailure 与 quota_auto_resume_disabled 是同一次中断),保持发生顺序。</summary>
        public IReadOnlyList<LimitSession> Sessions(Func<QueuedEvent, string?> sessionName) =>
            Members.GroupBy(member => member.SessionId ?? member.EventId, StringComparer.OrdinalIgnoreCase)
                .Select(session =>
                {
                    QueuedEvent first = session.First();
                    string? model = session.Select(member => _facts.GetValueOrDefault(member.EventId)?.Model)
                        .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? first.Model;
                    return new LimitSession(first, sessionName(first), model,
                        session.Any(member => member.IsAutoResumeDisabled), session.Any(member => member.Bridged));
                })
                .ToArray();
    }
}

/// <summary>限额通知里的一个会话:代表事件、会话名、最近用的模型、Claude Code 是否已表示不会自己继续、是否连着手机远程。</summary>
internal sealed record LimitSession(QueuedEvent Event, string? SessionName, string? Model, bool ClaudeGaveUp, bool Bridged);

/// <summary>队列里的一条事件(钩子或续跑引擎写入的同一格式)。</summary>
internal sealed record QueuedEvent(
    string FileName,
    string FullPath,
    string EventId,
    string? Cwd,
    string? Source,
    DateTimeOffset? AtUtc,
    bool Smoke,
    string? Kind,
    string? NotificationType,
    string? SessionId,
    bool BackgroundBusy,
    bool Bridged,
    string? Error,
    string? Model,
    string? Detail,
    string? TranscriptPath = null)
{
    /// <summary>Claude Code 的完成类事件(Stop):受稳定期、同会话合并与后台暂缓约束。</summary>
    public bool IsClaudeCompletion =>
        Source == "claudecode" && Kind is null or "completion" && !Smoke && !string.IsNullOrEmpty(SessionId);

    /// <summary>Claude Code hook 写的真实事件(不含冒烟)。</summary>
    public bool IsClaudeCode => Source == "claudecode" && !Smoke;

    /// <summary>原生自动继续放弃:限额打断后 Claude Code 不会自己继续(设置关了、重置超过 24 小时、反复撞墙后放弃…)。</summary>
    public bool IsAutoResumeDisabled =>
        IsClaudeCode && Kind == "decision" &&
        string.Equals(NotificationType?.Trim(), "quota_auto_resume_disabled", StringComparison.OrdinalIgnoreCase);

    /// <summary>用量上限中断:StopFailure 的 rate_limit,以及同一次中断的 quota_auto_resume_disabled。</summary>
    public bool IsLimitInterruption =>
        IsAutoResumeDisabled || (IsClaudeCode && Kind == "failure" && Error == "rate_limit");

    /// <summary>解析事件;缺 eventId(或除 AI Resume 自身提醒外缺 cwd)返回 null;类型错误抛 InvalidDataException。</summary>
    public static QueuedEvent? Parse(string fileName, string fullPath, string text)
    {
        using var doc = JsonDocument.Parse(text);
        JsonElement root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException("通知事件根节点必须是对象");
        }

        string? eventId = Text(root, "eventId");
        string? cwd = Text(root, "cwd");
        string? source = Text(root, "source");
        if (string.IsNullOrWhiteSpace(eventId) || (string.IsNullOrWhiteSpace(cwd) && source != "ai-resume"))
        {
            return null;
        }

        return new QueuedEvent(
            fileName,
            fullPath,
            eventId,
            cwd,
            source,
            ParseUtc(Text(root, "atUtc")),
            Flag(root, "smoke"),
            Text(root, "kind"),
            Text(root, "notificationType"),
            Text(root, "sessionId"),
            Flag(root, "backgroundBusy"),
            Flag(root, "bridged"),
            Text(root, "error"),
            Text(root, "model"),
            Text(root, "detail"),
            // 2026-11-02 起才读的字段:形状不对只当没带,不能让整条事件因此进 malformed。
            root.TryGetProperty("transcriptPath", out JsonElement transcriptPath) && transcriptPath.ValueKind == JsonValueKind.String
                ? transcriptPath.GetString()
                : null);
    }

    public static string? PeekSource(string text)
    {
        try
        {
            using var doc = JsonDocument.Parse(text);
            return doc.RootElement.ValueKind == JsonValueKind.Object &&
                   doc.RootElement.TryGetProperty("source", out JsonElement value) && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? Text(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out JsonElement value) || value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (value.ValueKind != JsonValueKind.String)
        {
            throw new InvalidDataException($"通知字段 {propertyName} 必须是字符串");
        }

        return value.GetString();
    }

    private static bool Flag(JsonElement root, string propertyName) =>
        root.TryGetProperty(propertyName, out JsonElement value) && value.ValueKind == JsonValueKind.True;

    /// <summary>解析 ISO8601 UTC;失败返回 null。用 InvariantCulture + AdjustToUniversal,否则非 UTC 时区的七天边界会漂。</summary>
    private static DateTimeOffset? ParseUtc(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return DateTimeOffset.TryParse(
            value,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal,
            out DateTimeOffset result)
            ? result
            : null;
    }
}
