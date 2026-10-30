using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AiResume.Secrets;

namespace AiResume.Worker.Quota;

/// <summary>
/// 一份可用于 Claude OAuth 请求的凭据。<see cref="AccessToken"/> 只在调用栈内流动,
/// 绝不进日志、异常或前端应答;对外只暴露 <see cref="CredentialFingerprint"/> 与 <see cref="Source"/>。
/// </summary>
public sealed record ClaudeCredential(
    bool Ok,
    string? AccessToken,
    string CredentialFingerprint,
    string? FailureReason,
    string Source,
    string? Detail = null,
    long? FileLength = null,
    long? FileWriteTicks = null)
{
    public static ClaudeCredential Missing(string reason, string source, string? detail = null) =>
        new(false, null, string.Empty, reason, source, detail);
}

/// <summary>
/// 不含实值的凭据状态,供控制面显示「现在靠哪份凭据在跑」。
/// </summary>
public sealed record ClaudeCredentialStatus(
    bool HasStoredToken,
    DateTimeOffset? StoredTokenSavedAtUtc,
    bool CliLoggedIn,
    DateTimeOffset? CliExpiresAtUtc,
    string? CliProblem,
    string ActiveSource,
    string? Problem,
    string? StoredRejectedReason = null,
    string? StoredProblem = null);

/// <summary>
/// Claude 订阅凭据的唯一解析入口。两条来源,按顺序:
///
/// 1. <b>AI Resume 自持的长期令牌</b>(用户运行 <c>claude setup-token</c> 后粘贴进控制面)。
///    这是 Claude Code 官方为无人值守场景提供的一年期 OAuth 令牌,不参与 Claude Code 的
///    刷新状态机,所以不会和它争用 refresh token。以机器范围 DPAPI 落在状态目录,
///    原因见 <see cref="DpapiSecretStore.SaveAsync(string, byte[], DpapiScope, CancellationToken)"/>。
/// 2. <b>Claude CLI 登录文件</b> <c>~/.claude/.credentials.json</c>。它由 Claude Code 自己维护,
///    AI Resume 只读、绝不刷新、绝不写回;到期即失效。2026-09-30 本机正是这份登录到期,
///    让额度链路静默停摆了一个月——这就是第 1 条存在的理由。
///
/// 两条都没有时如实返回 <c>no_credentials</c>,由界面告诉用户该做什么。
/// </summary>
public sealed class ClaudeCredentialSource
{
    /// <summary>DPAPI 文件引用名。</summary>
    public const string StoredTokenRef = "claude-oauth-token";

    public const string SourceStored = "ai-resume-token";
    public const string SourceCli = "claude-cli";
    public const string SourceNone = "none";

    /// <summary>CLI access token 剩余寿命低于此秒数视同过期,不发请求。</summary>
    private const long TokenExpiryGraceSeconds = 60;

    private readonly string _credentialsPath;
    private readonly DpapiSecretStore _store;
    private readonly string _storedTokenPath;
    private readonly Func<DateTimeOffset> _clock;
    private readonly object _cacheLock = new();
    private (long Length, long WriteTicks, string Token)? _storedCache;

    /// <param name="credentialsPath">Claude CLI 登录文件;null 用默认 <c>~/.claude/.credentials.json</c>。</param>
    /// <param name="secretsRoot">
    /// DPAPI 根目录;null 时用生产状态目录。**测试必须显式传临时目录**,否则会读到真机令牌。
    /// </param>
    public ClaudeCredentialSource(
        string? credentialsPath = null,
        string? secretsRoot = null,
        Func<DateTimeOffset>? clock = null)
    {
        _credentialsPath = credentialsPath ?? DefaultCredentialsPath();
        string root = secretsRoot ?? ShadowPaths.SecretsRoot;
        _store = new DpapiSecretStore(root);
        _storedTokenPath = Path.Combine(root, "secrets", StoredTokenRef + ".bin");
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    public static string DefaultCredentialsPath() => Path.Combine(
        Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR") ??
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude"),
        ".credentials.json");

    public string CredentialsPath => _credentialsPath;

    public bool HasStoredToken => File.Exists(_storedTokenPath);

    /// <summary>
    /// 保存长期令牌。只做形状校验,不联网验证——验证在下一次额度请求里真实发生,
    /// 界面据其结果显示「令牌被拒」而不是在这里假装核过。
    /// </summary>
    public void SaveToken(string token)
    {
        string trimmed = token?.Trim() ?? string.Empty;
        if (trimmed.Length is < 20 or > 8192)
        {
            throw new ArgumentException("令牌长度异常,请粘贴 claude setup-token 输出的完整令牌。");
        }

        foreach (char c in trimmed)
        {
            if (c <= ' ' || c >= '\u007f')
            {
                throw new ArgumentException("令牌含有空白或非 ASCII 字符,请只粘贴令牌本身。");
            }
        }

        byte[] plaintext = Encoding.UTF8.GetBytes(trimmed);
        try
        {
            _store.SaveAsync(StoredTokenRef, plaintext, DpapiScope.LocalMachine, CancellationToken.None)
                .GetAwaiter().GetResult();
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }

        lock (_cacheLock)
        {
            _storedCache = null;
        }

        TryDeleteRejectedMarker();
    }

    public void ClearToken()
    {
        try
        {
            _store.DeleteAsync(StoredTokenRef, CancellationToken.None).GetAwaiter().GetResult();
        }
        catch (KeyNotFoundException)
        {
        }

        lock (_cacheLock)
        {
            _storedCache = null;
        }

        TryDeleteRejectedMarker();
    }

    private void TryDeleteRejectedMarker()
    {
        try
        {
            if (File.Exists(RejectedMarkerPath))
            {
                File.Delete(RejectedMarkerPath);
            }
        }
        catch (Exception)
        {
        }
    }

    /// <summary>被拒标记的有效期:到期后允许再试一次长期令牌,仍被拒会重新标记。网关侧的误判因此能自愈。</summary>
    public static readonly TimeSpan RejectedMarkerTtl = TimeSpan.FromHours(1);

    /// <summary>
    /// 记录「<paramref name="rejected"/> 这份长期令牌被 usage 端点拒绝」。记录绑定被拒令牌读取时的文件长度与写入时间:
    /// 请求在途期间用户换了令牌(文件签名已变),就不写——否则新令牌从未被试过就被跳过。
    /// 用户重新保存令牌后签名变化,记录自动失效。写入失败不抛:最坏是下次再试一次被拒的令牌。
    /// </summary>
    public void MarkStoredRejected(string reason, ClaudeCredential rejected)
    {
        if (rejected.Source != SourceStored || rejected.FileLength is null || rejected.FileWriteTicks is null)
        {
            return;
        }

        try
        {
            FileInfo info = new(_storedTokenPath);
            if (!info.Exists || info.Length != rejected.FileLength || info.LastWriteTimeUtc.Ticks != rejected.FileWriteTicks)
            {
                return;
            }

            File.WriteAllText(RejectedMarkerPath, JsonSerializer.Serialize(new RejectedMarker(
                info.Length, info.LastWriteTimeUtc.Ticks, reason, _clock())));
        }
        catch (Exception)
        {
        }
    }

    /// <summary>当前长期令牌是否已被端点拒绝过(且之后没有重新保存)。</summary>
    public string? StoredRejectedReason
    {
        get
        {
            try
            {
                FileInfo info = new(_storedTokenPath);
                if (!info.Exists || !File.Exists(RejectedMarkerPath))
                {
                    return null;
                }

                RejectedMarker? marker = JsonSerializer.Deserialize<RejectedMarker>(File.ReadAllText(RejectedMarkerPath));
                return marker is not null && marker.Length == info.Length && marker.WriteTicks == info.LastWriteTimeUtc.Ticks &&
                       _clock() - marker.AtUtc < RejectedMarkerTtl
                    ? marker.Reason
                    : null;
            }
            catch (Exception)
            {
                return null;
            }
        }
    }

    private string RejectedMarkerPath => _storedTokenPath + ".rejected.json";

    private sealed record RejectedMarker(long Length, long WriteTicks, string Reason, DateTimeOffset AtUtc);

    /// <summary>按优先级解析:自持令牌 → CLI 登录。被端点拒绝过的自持令牌跳过。</summary>
    public ClaudeCredential Read()
    {
        ClaudeCredential stored = ReadStored();
        if (stored.Ok && StoredRejectedReason is null)
        {
            return stored;
        }

        ClaudeCredential cli = ReadCli();
        if (cli.Ok)
        {
            return cli;
        }

        // CLI 登录到期时仍带上它的账号指纹:同账号未过期的历史读数要按承接规则继续展示,
        // 指纹一旦清空,面板会把"刚刚还在的最近读数"当成另一个账号而清掉。
        return new ClaudeCredential(
            false,
            null,
            cli.CredentialFingerprint,
            cli.FailureReason ?? "no_credentials",
            SourceNone,
            DescribeMissing(stored, cli));
    }

    /// <summary>读取 AI Resume 自持的长期令牌;没有保存过时 Ok=false。</summary>
    public ClaudeCredential ReadStored()
    {
        FileInfo info = new(_storedTokenPath);
        if (!info.Exists)
        {
            return ClaudeCredential.Missing("no_stored_token", SourceStored);
        }

        long length = info.Length;
        long ticks = info.LastWriteTimeUtc.Ticks;
        lock (_cacheLock)
        {
            if (_storedCache is { } cached && cached.Length == length && cached.WriteTicks == ticks)
            {
                return Success(cached.Token, SourceStored, length, ticks);
            }
        }

        byte[]? plaintext = null;
        try
        {
            plaintext = _store.LoadAsync(StoredTokenRef, CancellationToken.None).GetAwaiter().GetResult();
            string token = Encoding.UTF8.GetString(plaintext).Trim();
            if (token.Length == 0)
            {
                return ClaudeCredential.Missing("stored_token_empty", SourceStored, "已保存的长期令牌为空");
            }

            lock (_cacheLock)
            {
                _storedCache = (length, ticks, token);
            }

            return Success(token, SourceStored, length, ticks);
        }
        catch (KeyNotFoundException)
        {
            // Exists 与 Load 之间被 GUI 清除:就是「没有令牌」,不是故障。
            return ClaudeCredential.Missing("no_stored_token", SourceStored);
        }
        catch (Exception ex) when (ex is CryptographicException or System.ComponentModel.Win32Exception
                                       or IOException or UnauthorizedAccessException)
        {
            // 解密失败(机器范围密文仍可能因 DPAPI 服务异常而暂时失败):
            // 若本进程此前成功解过同一份文件,继续用内存里的那份,不让一次抖动把额度链路打断。
            lock (_cacheLock)
            {
                if (_storedCache is { } cached && cached.Length == length && cached.WriteTicks == ticks)
                {
                    return Success(cached.Token, SourceStored, length, ticks);
                }
            }

            return ClaudeCredential.Missing("stored_token_unreadable", SourceStored, "已保存的长期令牌无法解密");
        }
        finally
        {
            if (plaintext is not null)
            {
                CryptographicOperations.ZeroMemory(plaintext);
            }
        }
    }

    /// <summary>读取 Claude CLI 登录文件;未登录、损坏或到期时 Ok=false 并给出分类原因。</summary>
    public ClaudeCredential ReadCli()
    {
        string json;
        try
        {
            if (!File.Exists(_credentialsPath))
            {
                return ClaudeCredential.Missing("no_credentials", SourceCli, "Claude CLI 未登录(没有凭据文件)");
            }

            json = File.ReadAllText(_credentialsPath);
        }
        catch (Exception)
        {
            return ClaudeCredential.Missing("no_credentials", SourceCli, "Claude CLI 凭据文件无法读取");
        }

        CredentialsFile? credentials;
        try
        {
            credentials = JsonSerializer.Deserialize<CredentialsFile>(json);
        }
        catch (JsonException)
        {
            return ClaudeCredential.Missing("no_credentials", SourceCli, "Claude CLI 凭据文件格式无法解析");
        }

        if (credentials?.ClaudeAiOauth?.AccessToken is not { Length: > 0 } accessToken)
        {
            return ClaudeCredential.Missing("no_credentials", SourceCli, "Claude CLI 未登录");
        }

        // organizationUuid 是账号级稳定身份,access token 轮换时不应让同一账号的
        // 最近权威快照凭空消失。旧版/异常凭据没有该字段时才退回 token 指纹。
        string identityMaterial = !string.IsNullOrWhiteSpace(credentials.OrganizationUuid)
            ? "organization:" + credentials.OrganizationUuid.Trim()
            : "token:" + accessToken;
        string fingerprint = Fingerprint(identityMaterial);

        // Claude Code 总是写 expiresAt;缺了说明文件不是它写的或已损坏,保守按未登录处理,
        // 不拿一个寿命未知的 token 去撞 401 再把文案指向「更换长期令牌」。
        if (credentials.ClaudeAiOauth.ExpiresAt is not { } expiresAtMs)
        {
            return new ClaudeCredential(false, null, fingerprint, "no_credentials", SourceCli,
                "Claude CLI 凭据缺少到期时间,请在终端运行 claude 并执行 /login");
        }

        long remainingMs = expiresAtMs - _clock().ToUnixTimeMilliseconds();
        if (remainingMs < TokenExpiryGraceSeconds * 1000)
        {
            return new ClaudeCredential(false, null, fingerprint, "token_expired", SourceCli,
                "Claude CLI 登录已过期,请在终端运行 claude 并执行 /login");
        }

        return new ClaudeCredential(true, accessToken, fingerprint, null, SourceCli);
    }

    /// <summary>不含实值的状态描述。</summary>
    public ClaudeCredentialStatus Describe()
    {
        ClaudeCredential stored = ReadStored();
        ClaudeCredential cli = ReadCli();
        DateTimeOffset? savedAt = null;
        if (HasStoredToken)
        {
            try
            {
                savedAt = new DateTimeOffset(File.GetLastWriteTimeUtc(_storedTokenPath), TimeSpan.Zero);
            }
            catch (Exception)
            {
            }
        }

        DateTimeOffset? cliExpires = TryReadCliExpiry();
        string? rejected = StoredRejectedReason;
        // 文件在但解不开/为空:这不是「没保存」,是故障,必须单独说出来,否则侧栏会把一份读不出的令牌画成琥珀「已保存」。
        string? storedProblem = HasStoredToken && !stored.Ok ? stored.Detail ?? "已保存的长期令牌不可用" : null;
        string active = stored.Ok && rejected is null ? SourceStored : cli.Ok ? SourceCli : SourceNone;
        string? problem = active == SourceNone
            ? DescribeMissing(stored, cli)
            : rejected is not null ? "AI Resume 长期令牌被 usage 端点拒绝(" + rejected + "),当前使用 Claude CLI 登录"
            : storedProblem is not null ? storedProblem + ",当前使用 Claude CLI 登录;请重新粘贴令牌" : null;
        return new ClaudeCredentialStatus(
            HasStoredToken,
            savedAt,
            cli.Ok,
            cliExpires,
            cli.Ok ? null : cli.Detail,
            active,
            problem,
            rejected,
            storedProblem);
    }

    private DateTimeOffset? TryReadCliExpiry()
    {
        try
        {
            if (!File.Exists(_credentialsPath))
            {
                return null;
            }

            CredentialsFile? credentials = JsonSerializer.Deserialize<CredentialsFile>(File.ReadAllText(_credentialsPath));
            return credentials?.ClaudeAiOauth?.ExpiresAt is { } ms && ms > 0
                ? DateTimeOffset.FromUnixTimeMilliseconds(ms)
                : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private string DescribeMissing(ClaudeCredential stored, ClaudeCredential cli)
    {
        string storedText = stored.FailureReason == "no_stored_token"
            ? "未保存 AI Resume 长期令牌"
            : stored.Ok && StoredRejectedReason is { } rejected
                ? "已保存的长期令牌被 usage 端点拒绝(" + rejected + ")"
                : stored.Detail ?? "长期令牌不可用";
        string cliText = cli.Detail ?? "Claude CLI 未登录";
        return $"{cliText};{storedText}。请在终端运行 claude 并 /login,或运行 claude setup-token 后把令牌粘贴到控制面。";
    }

    private static ClaudeCredential Success(string token, string source, long? length = null, long? ticks = null) =>
        new(true, token, Fingerprint("token:" + token), null, source, null, length, ticks);

    private static string Fingerprint(string material) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(material)));

    /// <summary>凭据文件形状。organizationUuid 只用于不可逆账号指纹。</summary>
    private sealed class CredentialsFile
    {
        [JsonPropertyName("claudeAiOauth")]
        public ClaudeAiOauth? ClaudeAiOauth { get; set; }

        [JsonPropertyName("organizationUuid")]
        public string? OrganizationUuid { get; set; }
    }

    private sealed class ClaudeAiOauth
    {
        [JsonPropertyName("accessToken")]
        public string? AccessToken { get; set; }

        [JsonPropertyName("expiresAt")]
        public long? ExpiresAt { get; set; }
    }
}
