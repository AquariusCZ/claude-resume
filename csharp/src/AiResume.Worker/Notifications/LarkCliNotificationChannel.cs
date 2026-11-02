using System.ComponentModel;
using System.Text.Json;
using System.Text.RegularExpressions;
using AiResume.LarkCli;

namespace AiResume.Worker.Notifications;

/// <summary>一次投递失败的作用范围：只影响这一条(Item),还是整个通道都发不出去(Channel,熔断)。</summary>
public enum NotifyScope
{
    Item,
    Channel,
}

/// <summary>
/// 一次投递尝试的结构化结果。只含状态码、错误类别与飞书 log_id,不含正文、收件人或凭据。
/// </summary>
public sealed record NotifySendResult(bool Sent, string Class)
{
    public NotifyScope Scope { get; init; } = NotifyScope.Item;

    /// <summary>条目级且不可重试(参数/策略错误):移入死信。</summary>
    public bool Permanent { get; init; }

    public int? RetryAfterSeconds { get; init; }

    /// <summary>结果不明(超时/传输中断):可能已送达，重试必须沿用同一个幂等键。</summary>
    public bool Ambiguous { get; init; }

    public int? ExitCode { get; init; }

    public string? ErrorType { get; init; }

    public string? ErrorSubtype { get; init; }

    public long? ErrorCode { get; init; }

    public string? LogId { get; init; }

    public long DurationMs { get; init; }

    public string? MessageId { get; init; }

    /// <summary>env(LocalMachine 副本注入)/ keychain(lark-cli 自己的钥匙串)。</summary>
    public string? CredentialSource { get; init; }

    public static NotifySendResult Delivered(string? messageId = null) => new(true, "sent") { MessageId = messageId };

    public static NotifySendResult ChannelFailure(string @class, bool ambiguous = false, int? retryAfter = null) =>
        new(false, @class) { Scope = NotifyScope.Channel, Ambiguous = ambiguous, RetryAfterSeconds = retryAfter };

    public static NotifySendResult ItemFailure(string @class, bool permanent = false) =>
        new(false, @class) { Permanent = permanent };
}

/// <summary>
/// lark-cli 1.0.81 的错误契约 → 投递决策(官方 errs/ERROR_CONTRACT.md 与本机录制形状):
/// 退出码 validation=2,authentication/authorization/config=3,network=4,api=1,policy=6,internal=5,confirmation=10。
/// </summary>
public static class NotifyClassifier
{
    public static NotifySendResult Classify(int exitCode, LarkEnvelope? success, LarkEnvelope? error)
    {
        if (exitCode == 0 && success?.Ok == true)
        {
            return NotifySendResult.Delivered(success.MessageId);
        }

        string? type = error?.ErrorType;
        string? subtype = error?.ErrorSubtype;
        long? code = error?.ErrorCode;
        NotifySendResult result;
        if (exitCode == 10 || type == "confirmation")
        {
            // 发消息不该需要确认;真出现了就停下等人，绝不自动 --yes。
            result = NotifySendResult.ChannelFailure("confirmation_required");
        }
        else if (type == "api" && subtype == "unknown" &&
                 (error?.ErrorMessage?.StartsWith("keychain ", StringComparison.Ordinal) ?? false))
        {
            // S4U 下 lark-cli 读不了自己的 DPAPI 钥匙串(message 不是稳定契约，只作兜底识别)。
            result = NotifySendResult.ChannelFailure("keychain_unavailable");
        }
        else if (subtype == "rate_limit" || code is 230020 or 99991400)
        {
            result = NotifySendResult.ChannelFailure("rate_limit", retryAfter: error?.RetryAfterSeconds);
        }
        else if (type == "network" || exitCode == 4)
        {
            result = NotifySendResult.ChannelFailure("network", ambiguous: subtype is "timeout" or "transport" or "server_error");
        }
        else if (type is "authentication" or "config" || code is 99991663 or 99991664 or 99991665 or 10014 or 10003)
        {
            // 10003 是换 tenant_access_token 时凭据无效(开放平台重置过 App Secret 等),不是发消息的参数错误:
            // 按通道级处理，设置一变即解除;当成永久失败会进死信、还会被误升级成「CLI 契约变了」。
            result = NotifySendResult.ChannelFailure("auth_config");
        }
        else if (type == "authorization" || code is 230013 or 230053 or 230006 or 99991672)
        {
            result = NotifySendResult.ChannelFailure("permission_or_recipient");
        }
        else if (type is "validation" or "policy" || code is 230001 or 99992402)
        {
            result = NotifySendResult.ItemFailure("invalid_message", permanent: true);
        }
        else
        {
            result = NotifySendResult.ItemFailure("unknown");
        }

        return result with
        {
            ExitCode = exitCode,
            ErrorType = type,
            ErrorSubtype = subtype,
            ErrorCode = code,
            LogId = error?.LogId,
        };
    }
}

/// <summary>
/// 经官方 lark-cli 以 bot 身份发飞书私信的通道。
/// <list type="bullet">
/// <item>**直接启动 lark-cli.exe,绝不经 cmd.exe**:npm 垫片走 <c>cmd.exe /c</c> 时,多行 <c>--text</c> 在第一个换行处
///   被截断，后面的 <c>--idempotency-key</c> 从未送达(2026-10-30 复现;v1 时代同一教训见 LESSONS)。</item>
/// <item>凭据优先用 <see cref="FeishuBotCredentials"/> 的 LocalMachine 副本，经官方环境变量注入;没有副本才退回
///   lark-cli 自己的钥匙串(S4U 下不可靠，日志与界面如实标出 credentialSource)。</item>
/// <item>错误信封从 stderr 解析，按 <see cref="NotifyClassifier"/> 分条目级/通道级。</item>
/// </list>
/// </summary>
public sealed class LarkCliNotificationChannel
{
    private readonly Func<string?> _resolveExecutable;
    private readonly FeishuBotCredentials _credentials;
    private readonly TimeSpan _timeout;

    public LarkCliNotificationChannel(FeishuBotCredentials? credentials = null, Func<string?>? resolveExecutable = null, TimeSpan? timeout = null)
    {
        _credentials = credentials ?? new FeishuBotCredentials();
        _resolveExecutable = resolveExecutable ?? (() => ResolveExecutable(Environment.GetEnvironmentVariable("PATH"),
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData)));
        _timeout = timeout ?? TimeSpan.FromSeconds(20);
    }

    /// <summary>
    /// 定位真正的 lark-cli.exe,**不回退 cmd.exe**;找不到返回 null(通道级 launch_failed)。
    /// 顺序:先用户自己的 npm 全局包 %APPDATA%\npm\node_modules\@larksuite\cli\bin\lark-cli.exe(官方安装位置),
    /// 再按 PATH 找 npm 垫片旁的包内二进制或独立的 lark-cli.exe。只认绝对路径条目:相对条目会按 Worker 的
    /// 当前目录解析,而这个进程会把机器人 App Secret 放进子进程环境,不能交给来路不明的同名程序。
    /// </summary>
    public static string? ResolveExecutable(string? pathEnvironment, string? applicationData)
    {
        IEnumerable<string> directories = (pathEnvironment ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(directory => directory.Trim('"'));
        if (!string.IsNullOrEmpty(applicationData))
        {
            directories = directories.Prepend(Path.Combine(applicationData, "npm"));
        }

        foreach (string directory in directories)
        {
            try
            {
                if (!Path.IsPathFullyQualified(directory))
                {
                    continue;
                }

                string packaged = Path.Combine(directory, "node_modules", "@larksuite", "cli", "bin", "lark-cli.exe");
                if (File.Exists(packaged))
                {
                    return packaged;
                }

                string direct = Path.Combine(directory, "lark-cli.exe");
                if (File.Exists(direct))
                {
                    return direct;
                }
            }
            catch (ArgumentException)
            {
                // PATH 里混进非法字符的条目，跳过。
            }
        }

        return null;
    }

    /// <summary>当前会用哪份凭据(只说来源，不读出实值)。</summary>
    public string CredentialSource => _credentials.Exists ? "env" : "keychain";

    public async Task<NotifySendResult> SendAsync(string receiverOpenId, string text, string idempotencyKey, CancellationToken ct)
    {
        string? executable = _resolveExecutable();
        if (executable is null)
        {
            return NotifySendResult.ChannelFailure("launch_failed") with { CredentialSource = CredentialSource };
        }

        (Dictionary<string, string?> environment, string source) = BuildEnvironment();
        var invoker = new LarkCliInvoker(executable, timeout: _timeout, environment: environment);
        try
        {
            LarkCliResult result = await invoker.InvokeAsync(
            [
                "im", "+messages-send",
                "--as", "bot",
                "--user-id", receiverOpenId,
                "--text", text,
                "--idempotency-key", idempotencyKey,
                "--format", "json",
            ], ct).ConfigureAwait(false);
            using LarkEnvelope? success = result.Envelope;
            using LarkEnvelope? error = ReferenceEquals(result.ErrorEnvelope, result.Envelope) ? null : result.ErrorEnvelope;
            return NotifyClassifier.Classify(result.ExitCode, success, error ?? success) with
            {
                DurationMs = (long)result.Duration.TotalMilliseconds,
                CredentialSource = source,
            };
        }
        catch (LarkCliException ex) when (ex.Kind == LarkCliFailureKind.Timeout)
        {
            // 超时结果不明：可能已送达，重试沿用同一幂等键(飞书 uuid 一小时内去重)。
            return NotifySendResult.ChannelFailure("network_timeout", ambiguous: true) with { CredentialSource = source };
        }
        catch (LarkCliException ex) when (ex.Kind == LarkCliFailureKind.HighRiskConfirmationRequired)
        {
            return NotifySendResult.ChannelFailure("confirmation_required") with { ExitCode = 10, CredentialSource = source };
        }
        catch (LarkCliException ex) when (ex.Kind == LarkCliFailureKind.InvalidOutput)
        {
            // 退出码 0、但 stdout 不是单个 JSON 信封(新版本多打一行提示之类):lark-cli 用 0 表示发送成功,按已送达处理。
            // 当成失败重试是错的——重试会持续到飞书 uuid 一小时去重窗口之外,真的重复送达。
            return NotifySendResult.Delivered() with { Class = "sent_unverified", ExitCode = 0, CredentialSource = source };
        }
        catch (LarkCliException)
        {
            return NotifySendResult.ItemFailure("invalid_output") with { Ambiguous = true, CredentialSource = source };
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or IOException)
        {
            return NotifySendResult.ChannelFailure("launch_failed") with { CredentialSource = source };
        }
    }

    /// <summary>
    /// <c>lark-cli doctor</c>(在线，Risk: read):用与投递相同的凭据来源核对 bot 身份能否换到 token、端点是否可达。
    /// 这是「凭据有效」的外部证据;只回检查项名称/状态/说明，不含凭据。
    /// </summary>
    public async Task<LarkDoctorReport> DoctorAsync(CancellationToken ct, string? appId = null, string? appSecret = null)
    {
        string? executable = _resolveExecutable();
        if (executable is null)
        {
            return LarkDoctorReport.Unavailable("找不到 lark-cli.exe(npm i -g @larksuite/cli 后重试)");
        }

        (Dictionary<string, string?> environment, string source) = appId is not null && appSecret is not null
            ? (CredentialEnvironment(appId, appSecret), "env")
            : BuildEnvironment();
        try
        {
            LarkCliResult result = await new LarkCliInvoker(executable, timeout: TimeSpan.FromSeconds(30), environment: environment)
                .InvokeAsync(["doctor"], ct, requireEnvelope: false).ConfigureAwait(false);
            LarkDoctorReport report = LarkDoctorReport.Parse(result.Stdout, source);
            // stdout 不是 doctor 报告、而 stderr 有错误信封(配置损坏、参数被拒…):把真实原因说出来,而不是只说「格式未核实」。
            if (report.Checks.Count == 0 && result.ErrorEnvelope is { } envelope)
            {
                using (envelope)
                {
                    string message = LarkDoctorReport.MaskIdentifiers(envelope.ErrorMessage) ?? envelope.ErrorType ?? "未知错误";
                    return LarkDoctorReport.Unavailable($"lark-cli doctor 报错(退出码 {result.ExitCode}):{message}");
                }
            }

            return report;
        }
        catch (Exception ex) when (ex is LarkCliException or Win32Exception or InvalidOperationException or IOException)
        {
            return LarkDoctorReport.Unavailable("lark-cli doctor 未能完成(" + ex.GetType().Name + ")");
        }
    }

    /// <summary>
    /// <c>lark-cli auth list --json</c>:同一应用下已登录用户的 open_id(按应用隔离，跨应用错配会让投递失败)。
    /// 只读，不刷新令牌。
    /// </summary>
    public async Task<IReadOnlyList<LarkUserCandidate>> ListUsersAsync(CancellationToken ct)
    {
        string? executable = _resolveExecutable();
        if (executable is null)
        {
            return [];
        }

        try
        {
            LarkCliResult result = await new LarkCliInvoker(executable, timeout: TimeSpan.FromSeconds(20), environment: NotifierSilence())
                .InvokeAsync(["auth", "list", "--json"], ct, requireEnvelope: false).ConfigureAwait(false);
            return result.ExitCode == 0 ? LarkUserCandidate.Parse(result.Stdout) : [];
        }
        catch (Exception ex) when (ex is LarkCliException or Win32Exception or InvalidOperationException or IOException)
        {
            return [];
        }
    }

    private (Dictionary<string, string?> Environment, string Source) BuildEnvironment() =>
        _credentials.TryLoad(out string appId, out string appSecret)
            ? (CredentialEnvironment(appId, appSecret), "env")
            : (NotifierSilence(), "keychain");

    private static Dictionary<string, string?> CredentialEnvironment(string appId, string appSecret)
    {
        Dictionary<string, string?> environment = NotifierSilence();
        environment["LARKSUITE_CLI_APP_ID"] = appId;
        environment["LARKSUITE_CLI_APP_SECRET"] = appSecret;
        environment["LARKSUITE_CLI_BRAND"] = "feishu";
        environment["LARKSUITE_CLI_STRICT_MODE"] = "bot";
        return environment;
    }

    /// <summary>官方 lark-shared skill 对机器调用的建议：关掉升级与 skills 提示，stdout/stderr 只剩信封。</summary>
    private static Dictionary<string, string?> NotifierSilence() => new(StringComparer.OrdinalIgnoreCase)
    {
        ["LARKSUITE_CLI_NO_UPDATE_NOTIFIER"] = "1",
        ["LARKSUITE_CLI_NO_SKILLS_NOTIFIER"] = "1",
    };
}

/// <summary><c>lark-cli doctor</c> 的检查结果(1.0.81 录制形状:{checks:[{name,status,message,hint?}],ok,workspace})。</summary>
public sealed record LarkDoctorReport(bool Ok, bool BotReady, string CredentialSource, IReadOnlyList<LarkDoctorCheck> Checks, string? Problem)
{
    public static LarkDoctorReport Unavailable(string problem) => new(false, false, "none", [], problem);

    public static LarkDoctorReport Parse(string stdout, string credentialSource)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(stdout);
            JsonElement root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("checks", out JsonElement checks) ||
                checks.ValueKind != JsonValueKind.Array)
            {
                return Unavailable("lark-cli doctor 输出格式未核实");
            }

            var items = checks.EnumerateArray()
                .Where(check => check.ValueKind == JsonValueKind.Object)
                .Select(check => new LarkDoctorCheck(Text(check, "name") ?? "?", Text(check, "status") ?? "?", MaskIdentifiers(Text(check, "message"))))
                .ToArray();
            bool ok = root.TryGetProperty("ok", out JsonElement okElement) && okElement.ValueKind == JsonValueKind.True;
            bool botReady = items.Any(check => check.Name == "bot_identity" && check.Status == "pass");
            return new LarkDoctorReport(ok, botReady, credentialSource, items, null);
        }
        catch (JsonException)
        {
            return Unavailable("lark-cli doctor 输出格式未核实");
        }
    }

    private static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static readonly Regex AppIdPattern = new(@"\bcli_[A-Za-z0-9]{4,}\b", RegexOptions.CultureInvariant);
    private static readonly Regex OpenIdPattern = new(@"\bou_[A-Za-z0-9]{8,}\b", RegexOptions.CultureInvariant);

    /// <summary>
    /// doctor 的说明文字会原样带出完整 app_id(如 <c>app: cli_… (feishu)</c>);界面与 CLI 只显示遮蔽后的标识,
    /// 与「飞书通知设置」里的写法一致。
    /// </summary>
    internal static string? MaskIdentifiers(string? message) => message is null
        ? null
        : OpenIdPattern.Replace(
            AppIdPattern.Replace(message, match => FeishuBotCredentials.Mask(match.Value)),
            match => NotifyControlPlane.MaskOpenId(match.Value));
}

public sealed record LarkDoctorCheck(string Name, string Status, string? Message);

/// <summary><c>lark-cli auth list --json</c> 的一项(1.0.81 形状:[{appId,tokenStatus,userName,userOpenId}])。</summary>
public sealed record LarkUserCandidate(string AppId, string UserOpenId, string? UserName, string? TokenStatus)
{
    public static IReadOnlyList<LarkUserCandidate> Parse(string stdout)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(stdout);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
            {
                return [];
            }

            return doc.RootElement.EnumerateArray()
                .Where(item => item.ValueKind == JsonValueKind.Object)
                .Select(item => new LarkUserCandidate(
                    Text(item, "appId") ?? string.Empty,
                    NotifyRecipient.Normalize(Text(item, "userOpenId")),
                    Text(item, "userName"),
                    Text(item, "tokenStatus")))
                .Where(candidate => NotifyRecipient.IsValid(candidate.UserOpenId))
                .ToArray();
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
