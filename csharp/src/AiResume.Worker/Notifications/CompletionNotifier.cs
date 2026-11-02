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
    private readonly Func<string, string, string, CancellationToken, Task<NotifySendResult>> _send;
    private readonly Func<DateTimeOffset> _now;
    private readonly Func<string, string[]> _enumerateFiles;
    private readonly Func<string, string, bool> _moveToMalformed;
    private readonly Func<string> _channelFingerprint;
    private readonly TimeSpan _sendSpacing;
    private readonly Func<string?, bool> _sourceEnabled;

    /// <param name="sourceEnabled">
    /// 事件来源是否仍是用户想要的通知源(null = 全部放行)。关掉的源仍可能有残留链路在写事件
    /// (例如 Codex <c>config.toml</c> 里旧版 <c>notify</c> 链,那是共享键，按 ADR-0005 只提示不改写),这里统一丢弃。
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
        Func<string?, bool>? sourceEnabled = null)
    {
        _eventsDir = eventsDir;
        _seenPath = seenPath;
        string stateDirectory = Path.GetDirectoryName(seenPath) ?? eventsDir;
        _statePath = Path.Combine(stateDirectory, "completion-notify-state.json");
        _healthPath = Path.Combine(stateDirectory, "completion-notify-health.json");
        // 每个 Claude 会话最近一次已送达的完成时间:更早的「后台任务未完」完成事件据此作废(它要等的正是那一条)。
        _sessionsPath = Path.Combine(stateDirectory, "completion-notify-sessions.json");
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

        bool blocked = health.BlockedUntilUtc > nowUtc &&
                       string.Equals(health.BlockFingerprint ?? string.Empty, SafeFingerprint(), StringComparison.Ordinal);
        bool sentOne = false;

        foreach (QueuedEvent item in queue)
        {
            ct.ThrowIfCancellationRequested();

            // 3. 七天内已送达 → 删除。
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

            // 3c. 发生超过 24 小时还没送出去(缺收件人、通道长期熔断…):不再补发,进死信。
            //     恢复后一次冲刷一串旧「已完成」只会误导——文案只有 HH:mm,看起来像刚发生。
            if (item.AtUtc is { } occurred && nowUtc - occurred > StaleAfter)
            {
                DeadOrBackoff(item, NotifySendResult.ItemFailure("stale"), attempts, nowUtc, items, "stale", ref stateChanged);
                failed++;
                continue;
            }

            // 4. 同一 Claude Code 会话更新的完成替代旧的:连续 Stop 只报最后一次。普通完成只在 2 分钟内合并;
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

            // 5. 收件人为空 → 保留文件。
            if (string.IsNullOrWhiteSpace(receiverOpenId))
            {
                items.Add(new NotifyItemResult(item.EventId, NotifyOutcome.Skipped, "recipient_missing", item.Source));
                skipped++;
                continue;
            }

            // 6. 稳定期与后台任务暂缓:先等更新的完成或真正的结束。
            TimeSpan age = item.AtUtc is { } at ? nowUtc - at : TimeSpan.MaxValue;
            if (item.IsClaudeCompletion && age < StabilityWindow)
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

            // 7. 通道熔断中 / 本条还在退避 → 这轮不碰。
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
                DeadOrBackoff(item, NotifySendResult.ItemFailure("outcome_unknown"), attempts, nowUtc, items, "outcome_unknown", ref stateChanged);
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
            string message = BuildText(item, nowUtc);
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
                health.SuccessFingerprint = SafeFingerprint();
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
            health.LastErrorCode = result.ErrorCode;
            health.LastLogId = result.LogId;

            // 参数类永久失败:30 分钟内另一条事件也命中同一类(跨轮记账——常态下每轮只有一条事件),
            // 多半是 CLI 契约变了,继续把每条都送进死信只会全军覆没 → 升级为通道熔断。
            if (result.Permanent)
            {
                // 已经判过「CLI 契约变了」、之后既没成功过(成功会清掉熔断)也没换设置:同类永久失败继续按通道熔断,
                // 不再每 30 分钟放一条进死信——那只是把「全部进死信」放慢了。超过 24 小时的由 stale 兜底。
                bool contractBroken = health.State == "blocked" && health.Reason == "cli_contract" &&
                                      string.Equals(health.BlockFingerprint ?? string.Empty, SafeFingerprint(), StringComparison.Ordinal);
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
                Block(health, result, nowUtc);
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
                DeadOrBackoff(item, result, attempts, nowUtc, items, result.Class, ref stateChanged);
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
                DeadOrBackoff(item, result, attempts, nowUtc, items, "retry_exhausted:" + result.Class, ref stateChanged);
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
        };
    }

    /// <summary>条目级重试间隔:30s×2^(n-1),上限 15 分钟,±20% 抖动(避免同一时刻一起重试)。</summary>
    public static TimeSpan ItemBackoff(int attempts)
    {
        double seconds = Math.Min(30 * Math.Pow(2, Math.Max(0, attempts - 1)), 15 * 60);
        return TimeSpan.FromSeconds(seconds * (0.8 + Random.Shared.NextDouble() * 0.4));
    }

    /// <summary>通道级熔断时长:按类别，连续失败加倍。凭据/权限类等人处理，设置一变即解除。</summary>
    public static TimeSpan ChannelBackoff(string @class, int consecutive, int? retryAfterSeconds)
    {
        int n = Math.Max(1, consecutive);
        return @class switch
        {
            "keychain_unavailable" => TimeSpan.FromSeconds(Math.Min(60 * Math.Pow(2, n - 1), 600)),
            "rate_limit" => TimeSpan.FromSeconds(retryAfterSeconds ?? Math.Min(Math.Pow(2, n), 60)),
            "network" or "network_timeout" => TimeSpan.FromSeconds(Math.Min(30 * Math.Pow(2, n - 1), 300)),
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

    private void Block(DeliveryHealth health, NotifySendResult result, DateTimeOffset nowUtc)
    {
        bool sameReason = health.State == "blocked" && health.Reason == result.Class;
        health.ConsecutiveChannelFailures = sameReason ? health.ConsecutiveChannelFailures + 1 : 1;
        health.State = "blocked";
        health.Reason = result.Class;
        health.BlockedSinceUtc = sameReason ? health.BlockedSinceUtc ?? nowUtc : nowUtc;
        health.BlockedUntilUtc = nowUtc + ChannelBackoff(result.Class, health.ConsecutiveChannelFailures, result.RetryAfterSeconds);
        health.BlockFingerprint = SafeFingerprint();
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

    /// <summary>
    /// 构造通知文本：第一行说发生了什么，第二行「来源 · 时间」,需要人动手时第三行写清怎么做。
    /// 决策类用 ⏳ 打头并写明**在等什么**:完成说的是"结束了",决策说的是"停在这儿等你",紧迫程度完全不同。
    /// </summary>
    internal static string BuildText(QueuedEvent item, DateTimeOffset nowUtc)
    {
        string projectName = string.IsNullOrWhiteSpace(item.Cwd) ? string.Empty : Path.GetFileName(item.Cwd.TrimEnd('\\', '/'));
        if (string.IsNullOrEmpty(projectName))
        {
            projectName = item.Cwd ?? string.Empty;
        }

        DateTimeOffset localTime = item.AtUtc?.ToLocalTime() ?? nowUtc.ToLocalTime();
        string? model = ModelLabel(item.Model);
        string meta = SourceDisplayName(item.Source) + (model is null ? string.Empty : " · " + model) + $" · {localTime:HH:mm}" +
                      (item.Bridged ? " · 远程已连接" : string.Empty);

        (string heading, string? advice) = item switch
        {
            { Smoke: true } => ($"🧪 {projectName} 通知冒烟通过", null),
            { Kind: "decision" } => ($"⏳ {projectName} {DecisionLabel(item.NotificationType)}", null),
            { Kind: "failure" } => FailureText(projectName, item.Error),
            { Kind: "resume" } => ResumeText(projectName, item.NotificationType, item.Detail),
            _ => ($"✅ {projectName} 已完成", null),
        };
        return advice is null ? $"{heading.Trim()}\n{meta}" : $"{heading.Trim()}\n{meta}\n{advice}";
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
            "quota_auto_resume_stale" => "额度恢复后没能自动继续,等你处理",
            "quota_auto_resume_disabled" => "被用量上限打断,自动继续未开启",
            _ => "等你决定",
        };

    /// <summary>Claude Code StopFailure 的 error 枚举(官方 hooks 文档)→ 中断原因。</summary>
    private static (string Heading, string? Advice) FailureText(string project, string? error) => error switch
    {
        "rate_limit" => ($"⏸️ {project} 因用量上限中断", "额度恢复后 AI Resume 会按布防自动续跑;会话开着时也可在原会话或手机远程里继续"),
        "authentication_failed" => ($"⚠️ {project} 登录失效而中断", "在终端运行 claude 并执行 /login"),
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
        "resume-failed" => ($"⚠️ {project} 自动续跑未确认完成" + (detail is null ? string.Empty : $"({detail})"),
            "打开会话检查结果;本轮布防已停止自动重放"),
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
            "execution-unverified" => "(执行条件核实不了)",
            _ => string.Empty,
        }), "打开控制面查看这一行的原因;修好后会自动重试"),
        "provider-changed" => ($"⚠️ {project} 的 Claude 账号与布防时不同",
            "换了账号,或刚从 v2.1 升级(账号身份的算法变了):确认账号后解除并重新布防"),
        "login-expiring" => ("⚠️ Claude Code 登录即将到期" + (detail is null ? string.Empty : $"({detail})"),
            "在终端运行 claude 并执行 /login,否则到期后读不到额度、无法自动续跑"),
        "login-expired" => ("⚠️ Claude Code 登录已过期", "在终端运行 claude 并执行 /login"),
        _ => ($"ℹ️ {project} {type}", null),
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
    /// 移入死信;移动失败(文件被占用等)时保留这一条的退避状态、15 分钟后再试,
    /// 而不是删掉状态后每 15 秒原样重发一次。
    /// </summary>
    private void DeadOrBackoff(QueuedEvent item, NotifySendResult result, Dictionary<string, AttemptState> attempts,
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
            return;
        }

        attempt ??= new AttemptState { FirstAttemptUtc = nowUtc };
        attempt.NextAttemptUtc = nowUtc + TimeSpan.FromMinutes(15);
        attempts[item.EventId] = attempt;
        stateChanged = true;
        items.Add(new NotifyItemResult(item.EventId, NotifyOutcome.Failed, "dead_move_failed:" + detail, item.Source)
        {
            Attempt = result, Attempts = attempt.Attempts,
        });
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
}

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
    string? Detail)
{
    /// <summary>Claude Code 的完成类事件(Stop):受稳定期、同会话合并与后台暂缓约束。</summary>
    public bool IsClaudeCompletion =>
        Source == "claudecode" && Kind is null or "completion" && !Smoke && !string.IsNullOrEmpty(SessionId);

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
            Text(root, "detail"));
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
