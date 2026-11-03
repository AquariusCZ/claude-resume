using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using AiResume.Worker.Products;

namespace AiResume.Worker.Notifications;

/// <summary>飞书通知通道的可证伪状态。每一项肯定都来自外部证据(文件、真实投递结果、lark-cli 体检)。</summary>
/// <param name="SettingsVerified">
/// 最近一次成功送达时的设置指纹与现在是否一致;null = 还没有成功记录。false = 成功之后改过凭据、收件人或 lark-cli,
/// 那次成功不能再证明现在可送达。
/// </param>
/// <param name="CredentialUnreadable">
/// 机器范围副本文件在，却解不开或格式坏了(换过机器、损坏、正被占用)。投递此时会拒发、不会改用钥匙串，
/// 界面必须显红，不能凭历史成功显示可送达。
/// </param>
public sealed record NotifyChannelStatus(
    bool LarkCliFound,
    string CredentialSource,
    string? AppIdMasked,
    bool LegacyCredentialAvailable,
    bool RecipientSet,
    string? RecipientMasked,
    DeliveryHealth? Health,
    bool? SettingsVerified = null,
    bool CredentialUnreadable = false);

/// <summary>「识别」收件人的结果:是否已按已保存机器人凭据的应用过滤。</summary>
public sealed record RecipientCandidates(IReadOnlyList<LarkUserCandidate> Users, bool FilteredByApp);

/// <summary>
/// 投递通道的设置指纹:机器人凭据副本的写入时间 + 收件人 + lark-cli 位置与写入时间(原地升级也算变化),取 SHA-256 前 16 位。
/// Worker 用它判断熔断是否该立即解除,控制面用它判断最近一次成功是否还作数;只用于比较,不含可读标识。
/// </summary>
public static class NotifyFingerprint
{
    public static string Compute(FeishuBotCredentials credentials, string? recipient, string? larkCliPath)
    {
        string material = WrittenTicks(credentials.SecretPath) + "|" + NotifyRecipient.Normalize(recipient) + "|" + (larkCliPath ?? string.Empty) +
                          "|" + (larkCliPath is null ? "0" : WrittenTicks(larkCliPath));
        return Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(material)))[..16];
    }

    private static string WrittenTicks(string path)
    {
        try
        {
            return (File.Exists(path) ? File.GetLastWriteTimeUtc(path) : DateTime.MinValue).Ticks.ToString(CultureInfo.InvariantCulture);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return "0";
        }
    }
}

/// <summary>
/// 「完成通知 → 飞书」控制面：GUI 与 <c>notify check/test</c> 共用。
/// 凭据只在这里经真实换令牌 + 一次只读机器人接口调用核验后才落盘(ADR-0007);收件人只接受规整后的 open_id。
/// </summary>
public sealed class NotifyControlPlane
{
    private readonly ProductConfigStore _configStore;
    private readonly string _stateRoot;
    private readonly FeishuBotCredentials _credentials;
    private readonly LarkCliNotificationChannel _channel;
    private readonly Func<string?> _resolveExecutable;

    /// <param name="tokens">换令牌的客户端;null 时真实请求飞书。测试注入带桩处理器的实例。</param>
    public NotifyControlPlane(
        ProductConfigStore? configStore = null,
        string? stateRoot = null,
        FeishuBotCredentials? credentials = null,
        Func<string?>? resolveExecutable = null,
        FeishuTenantToken? tokens = null)
    {
        _stateRoot = stateRoot ?? ShadowPaths.Root;
        _configStore = configStore ?? new ProductConfigStore(_stateRoot);
        _credentials = credentials ?? new FeishuBotCredentials(_stateRoot);
        _resolveExecutable = resolveExecutable ?? (() => LarkCliNotificationChannel.ResolveExecutable(
            Environment.GetEnvironmentVariable("PATH"), Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData)));
        _channel = new LarkCliNotificationChannel(_credentials, _resolveExecutable, tokens: tokens,
            cliConfigDirectory: Path.Combine(_stateRoot, "lark-cli"));
    }

    public string EventsDirectory => Path.Combine(_stateRoot, "completion-events");

    public string HealthPath => Path.Combine(_stateRoot, "completion-notify-health.json");

    /// <summary>这一条事件是否已由 Worker 真实送达(在去重表里,且送达时间不早于 <paramref name="since"/>)。</summary>
    public bool WasDelivered(string eventId, DateTimeOffset since)
    {
        try
        {
            string seenPath = Path.Combine(_stateRoot, "completion-notify-seen.json");
            if (!File.Exists(seenPath))
            {
                return false;
            }

            Dictionary<string, string>? seen = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(seenPath));
            return seen is not null && seen.TryGetValue(eventId, out string? at) &&
                   DateTimeOffset.TryParse(at, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out DateTimeOffset deliveredAt) &&
                   deliveredAt >= since.AddSeconds(-1);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return false;
        }
    }

    /// <summary>
    /// 一条测试通知的结局，只看这一条事件:delivered(进了已送达表)/ dead(进了死信)/
    /// failed(这一条真实尝试过、得到确定的条目级失败，原因见 <see cref="TestFailure"/>)/
    /// blocked(入队之后 Worker 看过、通道此刻仍在熔断)/ pending(还在排队)。GUI「发送测试」与 <c>notify test</c> 共用。
    /// 熔断只认此刻仍然有效的:到期了下一轮就会去试这一条，设置一改(指纹变了)Worker 也会立即解除，都不算 blocked。
    /// </summary>
    public string TestOutcome(string eventId, DateTimeOffset since)
    {
        RequireEventId(eventId);
        if (WasDelivered(eventId, since)) return "delivered";
        if (File.Exists(Path.Combine(EventsDirectory, "dead", eventId + ".json"))) return "dead";
        if (TestFailure(eventId) is not null) return "failed";
        DeliveryHealth? health = DeliveryHealth.TryLoad(HealthPath);
        bool blockedNow = health is { State: "blocked" } && health.UpdatedAtUtc >= since &&
                          health.BlockedUntilUtc > DateTimeOffset.UtcNow &&
                          string.Equals(health.BlockFingerprint ?? string.Empty, CurrentFingerprint(), StringComparison.Ordinal);
        return blockedNow ? "blocked" : "pending";
    }

    /// <summary>
    /// 测试通知这一条自己的确定失败原因(类别、错误码、log_id);还没尝试过、或结果不明(可能已送达，Worker 会在
    /// 飞书去重窗口内重试)时为 null。只含类别与编号，不含正文与收件人。
    /// </summary>
    public string? TestFailure(string eventId)
    {
        RequireEventId(eventId);
        try
        {
            string statePath = Path.Combine(_stateRoot, "completion-notify-state.json");
            if (!File.Exists(statePath))
            {
                return null;
            }

            Dictionary<string, CompletionNotifier.AttemptState>? attempts =
                JsonSerializer.Deserialize<Dictionary<string, CompletionNotifier.AttemptState>>(File.ReadAllText(statePath), CompletionNotifier.StateJson);
            return attempts is not null && attempts.TryGetValue(eventId, out CompletionNotifier.AttemptState? attempt) &&
                   attempt.Attempts > 0 && attempt.AmbiguousSinceUtc is null
                ? $"{attempt.LastClass ?? "unknown"}(错误码 {attempt.LastErrorCode?.ToString(CultureInfo.InvariantCulture) ?? "-"},log_id {attempt.LastLogId ?? "-"})"
                : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    private string CurrentFingerprint()
    {
        try
        {
            string recipient = NotifyRecipient.Normalize(_configStore.Load().NotifyRecipientOpenId);
            return NotifyFingerprint.Compute(_credentials, recipient, _resolveExecutable());
        }
        catch (Exception)
        {
            return string.Empty;
        }
    }

    /// <summary>撤回还没送出的测试通知,免得通道恢复后补发一条过时的「冒烟通过」。撤不回(被占用)就留着。</summary>
    public bool WithdrawTest(string eventId)
    {
        RequireEventId(eventId);
        try
        {
            string path = Path.Combine(EventsDirectory, eventId + ".json");
            if (!File.Exists(path)) return false;
            File.Delete(path);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    // 事件 ID 来自 RPC:只接受 EnqueueTest 生成的 16 位十六进制,不让它拼出队列目录以外的路径。
    private static void RequireEventId(string eventId)
    {
        if (eventId is not { Length: 16 } || !eventId.All(Uri.IsHexDigit))
        {
            throw new ArgumentException("测试通知的事件 ID 格式不对。");
        }
    }

    public NotifyChannelStatus Describe()
    {
        string recipient = NotifyRecipient.Normalize(_configStore.Load().NotifyRecipientOpenId);
        bool recipientSet = NotifyRecipient.IsValid(recipient);
        string? executable = _resolveExecutable();
        DeliveryHealth? health = DeliveryHealth.TryLoad(HealthPath);
        bool? settingsVerified = health?.SuccessFingerprint is { Length: > 0 } successFingerprint
            ? string.Equals(successFingerprint, NotifyFingerprint.Compute(_credentials, recipient, executable), StringComparison.Ordinal)
            : null;
        bool copyExists = _credentials.Exists;
        string? appIdMasked = copyExists ? _credentials.MaskedAppId() : null;
        return new NotifyChannelStatus(
            executable is not null,
            copyExists ? "env" : "keychain",
            appIdMasked,
            !copyExists && _credentials.LegacyExists,
            recipientSet,
            recipientSet ? MaskOpenId(recipient) : null,
            health,
            settingsVerified,
            // 读不出时再确认文件还在(刚被清除不算坏)。
            CredentialUnreadable: copyExists && appIdMasked is null && _credentials.Exists);
    }

    /// <summary>
    /// 诊断通道(在线，只读),用投递实际会用的凭据来源：有副本时真实换令牌并调一次机器人接口，再附 lark-cli doctor;
    /// 没有副本时只跑 doctor(钥匙串)。
    /// </summary>
    public Task<LarkDoctorReport> CheckAsync(CancellationToken ct) => _channel.DoctorAsync(ct);

    /// <summary>
    /// 保存机器人凭据：先用这组凭据真实向飞书换 tenant_access_token,再以令牌经 lark-cli 调一次只读机器人接口，
    /// 两步都过才写入(LocalMachine DPAPI)。
    /// </summary>
    public async Task<NotifyChannelStatus> SetCredentialAsync(string appId, string appSecret, CancellationToken ct)
    {
        appId = (appId ?? string.Empty).Trim();
        appSecret = (appSecret ?? string.Empty).Trim();
        if (!FeishuBotCredentials.IsValidAppId(appId))
        {
            throw new ArgumentException("飞书 App ID 格式不对(应为 cli_ 开头)。");
        }

        if (appSecret.Length < 8)
        {
            throw new ArgumentException("请填写飞书 App Secret。");
        }

        BotVerification verification = await _channel.VerifyAsync(appId, appSecret, ct).ConfigureAwait(false);
        if (!verification.Ok)
        {
            throw new InvalidOperationException("凭据未保存:" + verification.Problem);
        }

        _credentials.Save(appId, appSecret);
        return Describe();
    }

    /// <summary>
    /// 从 v2.1「飞书接入」保存的旧凭据导入(CurrentUser DPAPI,只能在交互会话里解密)。
    /// 同样先真实换令牌并调一次机器人接口，通过才写机器范围副本;旧文件原样保留，由用户决定是否清除。
    /// </summary>
    public async Task<NotifyChannelStatus> ImportLegacyAsync(CancellationToken ct)
    {
        if (!_credentials.TryLoadLegacy(out string appId, out string appSecret))
        {
            throw new InvalidOperationException("没有可导入的旧版飞书凭据(或当前 Windows 用户解不开它)。");
        }

        BotVerification verification = await _channel.VerifyAsync(appId, appSecret, ct).ConfigureAwait(false);
        if (!verification.Ok)
        {
            throw new InvalidOperationException("旧版飞书凭据未导入:" + verification.Problem + "。请在下方重新填写。");
        }

        _credentials.Save(appId, appSecret);
        return Describe();
    }

    public NotifyChannelStatus ClearCredential()
    {
        _credentials.Delete();
        return Describe();
    }

    public NotifyChannelStatus SetRecipient(string? openId)
    {
        string normalized = NotifyRecipient.Normalize(openId);
        if (!NotifyRecipient.IsValid(normalized))
        {
            throw new ArgumentException("收件人应为 ou_ 开头的 open_id(可点「识别」从 lark-cli 读取)。");
        }

        _configStore.Update(config => config.NotifyRecipientOpenId = normalized);
        return Describe();
    }

    /// <summary>
    /// lark-cli 里已登录的用户。open_id 按应用隔离:保存过机器人凭据副本时,只返回与它同一应用的用户
    /// (比对只在本进程内做,不回传 app_id 实值),否则投递会因跨应用 open_id 失败。
    /// </summary>
    public async Task<RecipientCandidates> DetectRecipientsAsync(CancellationToken ct)
    {
        IReadOnlyList<LarkUserCandidate> users = await _channel.ListUsersAsync(ct).ConfigureAwait(false);
        if (!_credentials.TryLoad(out string appId, out _))
        {
            if (_credentials.Exists)
            {
                // 副本在却读不出：没法按应用过滤，不能把别的应用下的 open_id 当候选递出去。
                throw new InvalidOperationException("AI Resume 保存的机器人凭据副本读不出,无法按应用筛选收件人;请先重新保存机器人凭据。");
            }

            return new RecipientCandidates(users, false);
        }

        return new RecipientCandidates(
            users.Where(user => string.Equals(user.AppId, appId, StringComparison.Ordinal)).ToArray(), true);
    }

    /// <summary>
    /// 往队列里放一条冒烟事件，走完 队列 → Worker → lark-cli → 飞书 全链路(而不是在界面进程里直接发)。
    /// 返回事件 ID,界面据投递健康文件确认结果。
    /// </summary>
    public string EnqueueTest()
    {
        string eventId = Convert.ToHexString(RandomNumberGenerator.GetBytes(8)).ToLowerInvariant();
        Directory.CreateDirectory(EventsDirectory);
        string target = Path.Combine(EventsDirectory, eventId + ".json");
        string temp = Path.Combine(EventsDirectory, eventId + ".tmp-" + Guid.NewGuid().ToString("N"));
        File.WriteAllText(temp, JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["version"] = 1,
            ["eventId"] = eventId,
            ["source"] = "ai-resume",
            ["kind"] = "resume",
            ["notificationType"] = "test",
            ["smoke"] = true,
            ["cwd"] = "AI Resume",
            ["atUtc"] = DateTimeOffset.UtcNow.ToString("o", CultureInfo.InvariantCulture),
        }));
        File.Move(temp, target, overwrite: false);
        return eventId;
    }

    public static string MaskOpenId(string openId) =>
        openId.Length <= 10 ? openId[..Math.Min(3, openId.Length)] + "…" : openId[..7] + "…" + openId[^4..];
}
