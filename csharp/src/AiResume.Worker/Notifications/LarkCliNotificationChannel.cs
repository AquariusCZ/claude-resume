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

    /// <summary>env(LocalMachine 副本换令牌后注入)/ keychain(lark-cli 自己的钥匙串)。</summary>
    public string? CredentialSource { get; init; }

    /// <summary>缓存的令牌先被飞书判无效、作废重换后又试了一次(这是第二次的结果)。</summary>
    public bool TokenRetried { get; init; }

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

    /// <summary>
    /// 换 tenant_access_token 失败 → 投递决策(ADR-0007)。这一步在启动 lark-cli 之前，消息根本没发出去:
    /// 一律通道级、结果明确(不 ambiguous)。飞书以已知凭据类错误码拒绝(10014 app_id 不存在或 App Secret 不对、
    /// 10003 参数缺失、10013 应用状态异常…)是凭据问题，设置一变即解除;网络、超时、5xx、限流按各自退避重试;
    /// 应答不是预期结果(未识别的错误码、代理或网关拦截页)单独成类，短退避重试，不冒充「凭据不对」或「要升级」。
    /// ErrorType 固定为 tenant_token,ErrorSubtype 形如 <c>server_error:http_502@direct</c>,区分换令牌失败与发消息失败。
    /// </summary>
    public static NotifySendResult ClassifyTokenExchange(TenantTokenFailure failure)
    {
        NotifySendResult result = failure.Kind switch
        {
            TenantTokenFailureKind.Rejected => NotifySendResult.ChannelFailure("auth_config"),
            TenantTokenFailureKind.RateLimited => NotifySendResult.ChannelFailure("rate_limit", retryAfter: failure.RetryAfterSeconds),
            TenantTokenFailureKind.Timeout => NotifySendResult.ChannelFailure("network_timeout"),
            TenantTokenFailureKind.Network or TenantTokenFailureKind.ServerError => NotifySendResult.ChannelFailure("network"),
            _ => NotifySendResult.ChannelFailure("token_unexpected"),
        };
        string subtype = failure.Kind switch
        {
            TenantTokenFailureKind.ServerError => "server_error",
            TenantTokenFailureKind.RateLimited => "rate_limited",
            _ => failure.Kind.ToString().ToLowerInvariant(),
        };
        if (failure.HttpStatus is { } status and not 200)
        {
            subtype += ":http_" + status.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        if (!string.IsNullOrEmpty(failure.Detail))
        {
            subtype += ":" + failure.Detail;
        }

        if (!string.IsNullOrEmpty(failure.Route))
        {
            subtype += "@" + failure.Route;
        }

        return result with
        {
            ErrorType = "tenant_token",
            ErrorSubtype = subtype,
            ErrorCode = failure.Code,
            LogId = failure.LogId,
            DurationMs = failure.DurationMs ?? 0,
        };
    }
}

/// <summary>
/// 经官方 lark-cli 以 bot 身份发飞书私信的通道。
/// <list type="bullet">
/// <item>**直接启动 lark-cli.exe,绝不经 cmd.exe**:npm 垫片走 <c>cmd.exe /c</c> 时,多行 <c>--text</c> 在第一个换行处
///   被截断，后面的 <c>--idempotency-key</c> 从未送达(2026-10-30 复现;v1 时代同一教训见 LESSONS)。</item>
/// <item>凭据优先用 <see cref="FeishuBotCredentials"/> 的 LocalMachine 副本：由 <see cref="FeishuTenantToken"/>
///   换成 tenant_access_token,只把 App ID + 令牌经官方环境变量注入(lark-cli 的 env 来源不会拿 App Secret 去换，
///   ADR-0007),并让 lark-cli 用 AI Resume 自有的空配置目录——从结构上就碰不到用户配置里引用的 CurrentUser 钥匙串。
///   没有副本才退回 lark-cli 自己的钥匙串(S4U 下不可靠，日志与界面如实标出 credentialSource)。</item>
/// <item>错误信封从 stderr 解析，按 <see cref="NotifyClassifier"/> 分条目级/通道级。</item>
/// </list>
/// </summary>
public sealed class LarkCliNotificationChannel
{
    private const string CredentialUnreadable = "AI Resume 保存的机器人凭据副本读不出(已损坏、正被占用，或换过机器、重装过系统),请在「飞书通知设置」重新保存";

    /// <summary>
    /// 子进程里一律先删掉的继承变量:凭据与身份相关的 lark-cli 环境变量只能由这里决定，用户环境里残留的值不得改变
    /// 这次调用实际用的凭据(界面与健康文件写的凭据来源才是真的),也不得把令牌带去别处(AUTH_PROXY 语义未核实)。
    /// </summary>
    private static readonly string[] CredentialVariables =
    [
        "LARKSUITE_CLI_APP_ID",
        "LARKSUITE_CLI_APP_SECRET",
        "LARKSUITE_CLI_TENANT_ACCESS_TOKEN",
        "LARKSUITE_CLI_USER_ACCESS_TOKEN",
        "LARKSUITE_CLI_DEFAULT_AS",
        "LARKSUITE_CLI_AUTH_PROXY",
    ];

    private readonly Func<string?> _resolveExecutable;
    private readonly FeishuBotCredentials _credentials;
    private readonly FeishuTenantToken _tokens;
    private readonly TimeSpan _timeout;
    private readonly string _cliConfigDirectory;

    /// <param name="tokens">换令牌的客户端;null 时用生产默认(真实请求飞书)。测试注入带桩处理器的实例。</param>
    /// <param name="cliConfigDirectory">副本路径下 lark-cli 用的 AI Resume 自有配置目录;null 时为 state\lark-cli。</param>
    public LarkCliNotificationChannel(FeishuBotCredentials? credentials = null, Func<string?>? resolveExecutable = null, TimeSpan? timeout = null,
        FeishuTenantToken? tokens = null, string? cliConfigDirectory = null)
    {
        _credentials = credentials ?? new FeishuBotCredentials();
        _resolveExecutable = resolveExecutable ?? (() => ResolveExecutable(Environment.GetEnvironmentVariable("PATH"),
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData)));
        _timeout = timeout ?? TimeSpan.FromSeconds(20);
        _tokens = tokens ?? new FeishuTenantToken();
        _cliConfigDirectory = cliConfigDirectory ?? Path.Combine(ShadowPaths.Root, "lark-cli");
    }

    /// <summary>
    /// 定位真正的 lark-cli.exe,**不回退 cmd.exe**;找不到返回 null(通道级 launch_failed)。
    /// 顺序:先用户自己的 npm 全局包 %APPDATA%\npm\node_modules\@larksuite\cli\bin\lark-cli.exe(官方安装位置),
    /// 再按 PATH 找 npm 垫片旁的包内二进制或独立的 lark-cli.exe。只认绝对路径条目:相对条目会按 Worker 的
    /// 当前目录解析,而这个进程会把机器人访问令牌放进子进程环境,不能交给来路不明的同名程序。
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

        BotCredential credential = await ResolveCredentialAsync(ct).ConfigureAwait(false);
        if (credential.Refused is { } refused)
        {
            // 副本读不出或换不到令牌：不启动 lark-cli,也绝不退回把 App Secret 放进环境变量。
            return refused;
        }

        NotifySendResult result = await SendOnceAsync(executable, credential, receiverOpenId, text, idempotencyKey, ct).ConfigureAwait(false);
        if (credential.Token is { FromCache: true } && IsTokenRejected(result))
        {
            // 缓存的令牌被飞书判无效(提前作废、时钟偏差):作废后重换一次再发。令牌被拒时消息没有发出，
            // 而且重试沿用同一个幂等键，飞书一小时内按 uuid 去重。
            _tokens.Invalidate();
            credential = await ResolveCredentialAsync(ct).ConfigureAwait(false);
            result = credential.Refused ??
                     await SendOnceAsync(executable, credential, receiverOpenId, text, idempotencyKey, ct).ConfigureAwait(false);
            result = result with { TokenRetried = true };
        }

        return result;
    }

    /// <summary>飞书判令牌无效(不含 lark-cli 本地没读到令牌的 token_missing:那是契约问题，重换也没用)。</summary>
    private static bool IsTokenRejected(NotifySendResult result) =>
        !result.Sent && result.ErrorSubtype != "token_missing" &&
        (result.ErrorType == "authentication" || result.ErrorCode is 99991663 or 99991664 or 99991665);

    /// <summary>
    /// 副本路径注入了令牌，lark-cli 却说本地没有令牌(token_missing):它不再读这个环境变量了(版本不兼容),
    /// 不是凭据问题——按 lark-cli 契约变化处理，不提示用户重新保存凭据。
    /// </summary>
    private static NotifySendResult WithTokenContract(NotifySendResult result, BotCredential credential) =>
        credential.Token is not null && !result.Sent && result.ErrorSubtype == "token_missing"
            ? result with { Class = "cli_contract", Scope = NotifyScope.Channel, Permanent = false }
            : result;

    private async Task<NotifySendResult> SendOnceAsync(string executable, BotCredential credential, string receiverOpenId, string text,
        string idempotencyKey, CancellationToken ct)
    {
        string source = credential.Source;
        var invoker = new LarkCliInvoker(executable, timeout: _timeout, environment: credential.Environment);
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
            return WithTokenContract(NotifyClassifier.Classify(result.ExitCode, success, error ?? success), credential) with
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
    /// 诊断通道(在线，只读),用与投递相同的凭据来源。有机器范围副本：先真实换令牌、以令牌经 lark-cli 调一次机器人接口
    /// (<see cref="VerifyAsync"/>,只有这两步决定 BotReady 与 Problem),再附 <c>lark-cli doctor</c>(用户的 lark-cli 配置 +
    /// 注入的令牌，看版本与端点;它的问题只作附注检查项);没换到令牌就不跑 doctor。没有副本：只跑 doctor(钥匙串)。
    /// 只回检查项名称/状态/说明，不含凭据。
    /// </summary>
    public async Task<LarkDoctorReport> DoctorAsync(CancellationToken ct)
    {
        string? executable = _resolveExecutable();
        if (executable is null)
        {
            return LarkDoctorReport.Unavailable("找不到 lark-cli.exe(npm i -g @larksuite/cli 后重试)");
        }

        if (!_credentials.TryLoad(out string appId, out string appSecret))
        {
            return _credentials.Exists
                ? new LarkDoctorReport(false, false, "env", [new LarkDoctorCheck("credential_copy", "fail", CredentialUnreadable)], CredentialUnreadable)
                : await RunDoctorAsync(executable, KeychainEnvironment(), "keychain", ct).ConfigureAwait(false);
        }

        BotVerification verification = await VerifyCoreAsync(executable, appId, appSecret, ct).ConfigureAwait(false);
        if (verification.Environment is null)
        {
            return new LarkDoctorReport(false, false, "env", verification.Checks, verification.Problem);
        }

        // doctor 看的是用户自己的 lark-cli 配置(去掉副本路径的私有配置目录),注入的令牌照旧优先于钥匙串。
        Dictionary<string, string?> doctorEnvironment = new(verification.Environment, StringComparer.OrdinalIgnoreCase);
        doctorEnvironment.Remove("LARKSUITE_CLI_CONFIG_DIR");
        LarkDoctorReport doctor = await RunDoctorAsync(executable, doctorEnvironment, "env", ct).ConfigureAwait(false);
        List<LarkDoctorCheck> checks = [.. verification.Checks, .. doctor.Checks];
        if (doctor.Problem is not null)
        {
            checks.Add(new LarkDoctorCheck("doctor", "warn", doctor.Problem));
        }

        return new LarkDoctorReport(verification.Ok && doctor.Ok, verification.Ok, "env", checks, verification.Problem);
    }

    /// <summary>
    /// 核验一组机器人凭据(保存前、导入前):真实向飞书换 tenant_access_token,再以这个令牌经 lark-cli 调一次只读的
    /// 机器人信息接口(<c>GET /open-apis/bot/v3/info</c>)。两步都过才算飞书接受;令牌不缓存、不落盘。
    /// </summary>
    public async Task<BotVerification> VerifyAsync(string appId, string appSecret, CancellationToken ct)
    {
        string? executable = _resolveExecutable();
        if (executable is null)
        {
            const string missing = "找不到 lark-cli.exe(npm i -g @larksuite/cli 后重试)";
            return new BotVerification(false, missing, [new LarkDoctorCheck("lark_cli", "fail", missing)]);
        }

        return await VerifyCoreAsync(executable, appId, appSecret, ct).ConfigureAwait(false);
    }

    private async Task<BotVerification> VerifyCoreAsync(string executable, string appId, string appSecret, CancellationToken ct)
    {
        TenantTokenOutcome token = await _tokens.ExchangeAsync(appId, appSecret, ct).ConfigureAwait(false);
        if (!token.Ok)
        {
            string refused = DescribeTokenFailure(token.Failure!);
            return new BotVerification(false, refused, [new LarkDoctorCheck("tenant_token", "fail", refused)]);
        }

        var tokenCheck = new LarkDoctorCheck("tenant_token", "pass", "飞书已发放 tenant_access_token(App ID 与 App Secret 有效)");
        Dictionary<string, string?> environment = TokenEnvironment(appId, token.Token!, PrepareConfigDirectory());
        NotifySendResult bot = await BotInfoAsync(executable, environment, ct).ConfigureAwait(false);
        if (!bot.Sent)
        {
            string problem = DescribeBotFailure(bot);
            return new BotVerification(false, problem, [tokenCheck, new LarkDoctorCheck("bot_api", "fail", problem)]) { Environment = environment };
        }

        return new BotVerification(true, null, [tokenCheck, new LarkDoctorCheck("bot_api", "pass", "以该令牌经 lark-cli 调用机器人信息接口成功，机器人已启用")])
        {
            Environment = environment,
        };
    }

    /// <summary>
    /// 以注入的令牌调一次只读的机器人信息接口。退出码 0 才算调通;应答里读得到 <c>bot.activate_status</c> 且不是 2(启用)时
    /// 判机器人未启用——停用或待启用的机器人发不了消息。只读这一个字段，其余应答(机器人名称、open_id)不记录。
    /// </summary>
    private static async Task<NotifySendResult> BotInfoAsync(string executable, Dictionary<string, string?> environment, CancellationToken ct)
    {
        try
        {
            LarkCliResult result = await new LarkCliInvoker(executable, timeout: TimeSpan.FromSeconds(20), environment: environment)
                .InvokeAsync(["api", "GET", "/open-apis/bot/v3/info", "--as", "bot"], ct, requireEnvelope: false).ConfigureAwait(false);
            using LarkEnvelope? output = result.Envelope;
            using LarkEnvelope? error = ReferenceEquals(result.ErrorEnvelope, result.Envelope) ? null : result.ErrorEnvelope;
            if (result.ExitCode != 0)
            {
                return NotifyClassifier.Classify(result.ExitCode, null, error ?? output);
            }

            return BotActivateStatus(result.Stdout) is { } status and not 2
                ? NotifySendResult.ChannelFailure("bot_inactive") with { ErrorSubtype = "activate_status_" + status }
                : NotifySendResult.Delivered();
        }
        catch (LarkCliException ex) when (ex.Kind == LarkCliFailureKind.Timeout)
        {
            return NotifySendResult.ChannelFailure("network_timeout");
        }
        catch (LarkCliException ex) when (ex.Kind == LarkCliFailureKind.HighRiskConfirmationRequired)
        {
            return NotifySendResult.ChannelFailure("confirmation_required");
        }
        catch (LarkCliException)
        {
            return NotifySendResult.ItemFailure("invalid_output");
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or IOException)
        {
            return NotifySendResult.ChannelFailure("launch_failed");
        }
    }

    /// <summary>bot/v3/info 应答里的 activate_status(官方形状在顶层 bot 下;也认 data 外壳);读不到为 null。</summary>
    private static int? BotActivateStatus(string stdout)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(stdout);
            JsonElement root = doc.RootElement;
            JsonElement? bot = root.ValueKind != JsonValueKind.Object ? null
                : root.TryGetProperty("bot", out JsonElement top) ? top
                : root.TryGetProperty("data", out JsonElement data) && data.ValueKind == JsonValueKind.Object
                    ? data.TryGetProperty("bot", out JsonElement nested) ? nested : data
                    : null;
            return bot is { ValueKind: JsonValueKind.Object } value && value.TryGetProperty("activate_status", out JsonElement status) &&
                   status.ValueKind == JsonValueKind.Number && status.TryGetInt32(out int number)
                ? number
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string DescribeTokenFailure(TenantTokenFailure failure)
    {
        string codes = Codes(failure.HttpStatus, failure.Code, failure.Message, failure.LogId);
        string path = string.Join(",", new[] { failure.Detail, failure.Route is { } route ? "路径 " + route : null }.Where(part => !string.IsNullOrEmpty(part)));
        return failure.Kind switch
        {
            TenantTokenFailureKind.Rejected => $"飞书拒绝了这组凭据或应用配置({codes}):核对 App ID 与 App Secret,并确认应用已启用",
            TenantTokenFailureKind.RateLimited => $"换取令牌时被飞书限流({codes}),稍后重试",
            TenantTokenFailureKind.Timeout => $"向飞书换取令牌超时({path}),稍后重试",
            TenantTokenFailureKind.Network => $"连不上飞书开放平台(换取令牌时网络出错:{path}),稍后重试",
            TenantTokenFailureKind.ServerError => $"飞书开放平台暂时出错({codes}),稍后重试",
            _ => $"向飞书换取令牌的应答不是预期结果({string.Join(",", new[] { codes, path }.Where(part => part.Length > 0))}):可能被代理或网关拦截，也可能是未识别的错误码;稍后重试",
        };
    }

    private static string DescribeBotFailure(NotifySendResult failure)
    {
        string what = failure.Class switch
        {
            "bot_inactive" => "换到了令牌，但机器人没有启用(" + failure.ErrorSubtype + "):在开放平台启用机器人能力并发布版本",
            "cli_contract" or "auth_config" when failure.ErrorSubtype == "token_missing" => "lark-cli 没有读到注入的令牌(lark-cli 版本可能不兼容)",
            "permission_or_recipient" => "换到了令牌，但机器人接口被拒：应用可能没有启用机器人能力或缺少权限",
            "auth_config" => "换到了令牌，但飞书不接受它",
            "network" or "network_timeout" => "换到了令牌，但 lark-cli 连不上飞书",
            "launch_failed" => "找不到或无法启动 lark-cli.exe",
            "confirmation_required" => "lark-cli 要求人工确认，无法代为核验",
            _ => "换到了令牌，但 lark-cli 调用机器人接口失败",
        };
        string codes = Codes(null, failure.ErrorCode, failure.ExitCode is { } exit ? "退出码 " + exit : null, failure.LogId);
        return codes.Length == 0 ? what : $"{what}({codes})";
    }

    private static string Codes(int? httpStatus, long? code, string? message, string? logId) => string.Join(",", new[]
    {
        httpStatus is { } status and not 200 ? "HTTP " + status : null,
        code is { } value ? "错误码 " + value : null,
        message,
        logId is { } id ? "log_id " + id : null,
    }.Where(part => !string.IsNullOrEmpty(part)));

    private static async Task<LarkDoctorReport> RunDoctorAsync(string executable, Dictionary<string, string?> environment, string source,
        CancellationToken ct)
    {
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
            LarkCliResult result = await new LarkCliInvoker(executable, timeout: TimeSpan.FromSeconds(20), environment: KeychainEnvironment())
                .InvokeAsync(["auth", "list", "--json"], ct, requireEnvelope: false).ConfigureAwait(false);
            return result.ExitCode == 0 ? LarkUserCandidate.Parse(result.Stdout) : [];
        }
        catch (Exception ex) when (ex is LarkCliException or Win32Exception or InvalidOperationException or IOException)
        {
            return [];
        }
    }

    /// <summary>
    /// 这一次调用 lark-cli 用哪份凭据。有副本：换令牌后只注入 App ID + 令牌;没有副本：交给 lark-cli 自己的钥匙串。
    /// 副本在却读不出、或换不到令牌，返回分类好的失败(<see cref="BotCredential.Refused"/>),调用方不得启动 lark-cli。
    /// </summary>
    private async Task<BotCredential> ResolveCredentialAsync(CancellationToken ct)
    {
        if (!_credentials.TryLoad(out string appId, out string appSecret))
        {
            if (_credentials.Exists)
            {
                // 文件还在却读不出：坏了或正被占用，不能静默改用另一份凭据。
                return BotCredential.Refuse(NotifySendResult.ChannelFailure("credential_unreadable") with { CredentialSource = "env" });
            }

            // 副本被清除了：按钥匙串;之前换来的令牌不再用。
            _tokens.Invalidate();
            return new BotCredential(KeychainEnvironment(), "keychain", null, null);
        }

        TenantTokenOutcome token = await _tokens.GetAsync(appId, appSecret, ct).ConfigureAwait(false);
        return token.Ok
            ? new BotCredential(TokenEnvironment(appId, token.Token!, PrepareConfigDirectory()), "env", token, null)
            : BotCredential.Refuse(NotifyClassifier.ClassifyTokenExchange(token.Failure!) with { CredentialSource = "env" });
    }

    /// <summary>
    /// 副本路径的子进程环境:lark-cli 的 env 来源只看到 App ID + 现成的 bot 令牌。继承来的凭据类变量(App Secret、用户令牌…)
    /// 一律先删掉(值为 null 表示删除),绝不把 App Secret 交给子进程。<paramref name="configDirectory"/> 给出时让 lark-cli
    /// 用这个 AI Resume 自有的空配置目录:用户配置里引用的 CurrentUser 钥匙串从结构上就碰不到。
    /// </summary>
    public static Dictionary<string, string?> TokenEnvironment(string appId, string tenantAccessToken, string? configDirectory = null)
    {
        Dictionary<string, string?> environment = KeychainEnvironment();
        environment["LARKSUITE_CLI_APP_ID"] = appId;
        environment["LARKSUITE_CLI_TENANT_ACCESS_TOKEN"] = tenantAccessToken;
        environment["LARKSUITE_CLI_BRAND"] = "feishu";
        environment["LARKSUITE_CLI_STRICT_MODE"] = "bot";
        if (configDirectory is not null)
        {
            environment["LARKSUITE_CLI_CONFIG_DIR"] = configDirectory;
        }

        return environment;
    }

    /// <summary>钥匙串路径(以及 auth list):先删掉继承来的凭据类变量，让 lark-cli 真的用它自己的配置与钥匙串。</summary>
    public static Dictionary<string, string?> KeychainEnvironment()
    {
        Dictionary<string, string?> environment = NotifierSilence();
        foreach (string name in CredentialVariables)
        {
            environment[name] = null;
        }

        return environment;
    }

    /// <summary>副本路径 lark-cli 的私有配置目录;建不出来就返回 null(退回默认配置目录，注入的令牌照样优先)。</summary>
    private string? PrepareConfigDirectory()
    {
        try
        {
            Directory.CreateDirectory(_cliConfigDirectory);
            return _cliConfigDirectory;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    /// <param name="Environment">交给 lark-cli 子进程的环境;Refused 时为 null。</param>
    /// <param name="Token">副本路径换到的令牌;钥匙串路径为 null。</param>
    /// <param name="Refused">副本读不出或换不到令牌时的分类结果。</param>
    private sealed record BotCredential(Dictionary<string, string?>? Environment, string Source, TenantTokenOutcome? Token, NotifySendResult? Refused)
    {
        public static BotCredential Refuse(NotifySendResult failure) => new(null, "env", null, failure);
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

/// <summary>一组机器人凭据的核验结论(真实换令牌 + 一次只读机器人接口调用)。只含检查项与说明，不含凭据。</summary>
public sealed record BotVerification(bool Ok, string? Problem, IReadOnlyList<LarkDoctorCheck> Checks)
{
    /// <summary>换到令牌后的子进程环境，只供同一次诊断里的 doctor 复用，不外传。</summary>
    internal Dictionary<string, string?>? Environment { get; init; }
}

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
