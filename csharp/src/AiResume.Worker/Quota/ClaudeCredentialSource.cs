using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AiResume.Worker.Quota;

/// <summary>
/// 一份可用于 Claude OAuth 请求的凭据。<see cref="AccessToken"/> 只在调用栈内流动,
/// 绝不进日志、异常或前端应答;对外只暴露 <see cref="CredentialFingerprint"/> 与 <see cref="Source"/>。
/// <see cref="CredentialFingerprint"/> 是<b>账号</b>指纹;账号身份核实不了时为空串(绝不拿令牌顶替)。
/// </summary>
public sealed record ClaudeCredential(
    bool Ok,
    string? AccessToken,
    string CredentialFingerprint,
    string? FailureReason,
    string Source,
    string? Detail = null,
    DateTimeOffset? AccessTokenExpiresAtUtc = null)
{
    public static ClaudeCredential Missing(string reason, string source, string? detail = null) =>
        new(false, null, string.Empty, reason, source, detail);
}

/// <summary>Claude Code 登录状态的结论(不含实值),界面按它上色。</summary>
public enum ClaudeLoginVerdict
{
    /// <summary>已登录且访问令牌有效。</summary>
    Ok,

    /// <summary>已登录、仍可用，但登录本身(refresh token)将在 3 天内到期，需要用户重新 /login。</summary>
    LoginExpiring,

    /// <summary>登录仍有效，但访问令牌已过期：要等 Claude Code 下次运行时自己续期。</summary>
    TokenExpired,

    /// <summary>登录已到期，或已被 Claude Code 清空(刷新被服务端拒绝),必须重新 /login。</summary>
    LoginExpired,

    /// <summary>没有登录(登录文件不存在、没有访问令牌，或缺到期时间)。</summary>
    LoggedOut,

    /// <summary>Claude Code 已把登录改存进 Windows 凭据管理器，AI Resume 读不到。</summary>
    StoreMoved,

    /// <summary>登录文件在，但这次读不了或解析不了(可能正被改写):判断不了，绝不说成「未登录」。</summary>
    Unverified,
}

/// <summary>不含实值的登录状态，供控制面显示与到期提醒。</summary>
public sealed record ClaudeCredentialStatus(
    ClaudeLoginVerdict Verdict,
    DateTimeOffset? AccessTokenExpiresAtUtc,
    DateTimeOffset? LoginExpiresAtUtc,
    string? SubscriptionType,
    bool AccountVerified,
    string? Problem);

/// <summary>
/// Claude 订阅凭据的唯一解析入口：只读 Claude Code 自己的登录文件
/// (<c>%USERPROFILE%\.claude\.credentials.json</c>,或 <c>CLAUDE_CONFIG_DIR</c> 下的同名文件)。
///
/// 红线：只读、绝不刷新、绝不写回。Claude Code 的 refresh token 是一次性的，刷新必须在它自己的
/// <c>.oauth_refresh.lock</c> 锁内做重读 + 按 refreshToken 的 CAS 写回;外部进程刷新一次，Claude Code
/// 手里那份就作废，下次刷新得到 invalid_grant 后会清空登录(Claude Code 2.1.300 二进制核对,2026-10-30)。
/// 访问令牌约 8 小时一换，由正在运行的 Claude Code(会话或 Remote Control 桥)提前 5 分钟自己续期;
/// 登录本身(refreshTokenExpiresAt)是固定的约 30 天，刷新不会延长，只能由用户 /login 续期。
///
/// 2026-10 曾有第二条来源:AI Resume 自持的 <c>claude setup-token</c> 长期令牌。实测它的作用域只有
/// <c>user:inference</c>,额度端点一律 403(<c>oauth_scope_insufficient</c>),于 2026-10-30 删除(ADR-0006)。
///
/// <b>账号指纹</b>取 Claude Code 全局配置里 <c>oauthAccount</c> 的组织与账号 UUID(与 <c>claude auth status</c>
/// 的 orgId 同源)。凭据文件本身没有任何账号字段;此前退回「access token 的哈希」当指纹，令牌每 8 小时一换，
/// 续跑引擎就把同一个账号判成「供应商已改变」而永远等待(2026-10-30 体检根因 a,生产库同一账号 28 个指纹)。
/// 现在核实不了账号就如实返回空指纹(调用方据此等待),绝不拿令牌顶替。
/// </summary>
public sealed class ClaudeCredentialSource
{
    public const string SourceCli = "claude-cli";
    public const string SourceNone = "none";

    /// <summary>访问令牌剩余寿命低于此秒数视同过期，不发请求。</summary>
    private const long TokenExpiryGraceSeconds = 60;

    /// <summary>登录到期前多久开始提醒用户重新 /login(与 Claude Code 自己的提示阈值一致)。</summary>
    public static readonly TimeSpan LoginExpiryWarning = TimeSpan.FromDays(3);

    private readonly string _credentialsPath;
    private readonly string[] _globalConfigPaths;
    private readonly Func<string, string?> _env;
    private readonly Func<DateTimeOffset> _clock;
    private readonly object _accountLock = new();
    private readonly Dictionary<string, (long Length, long WriteTicks, AccountInfo Info)> _accountCache = new(StringComparer.OrdinalIgnoreCase);
    private string? _lastGoodMaterial;

    /// <param name="credentialsPath">Claude Code 登录文件;null 用默认路径。</param>
    /// <param name="globalConfigPath">
    /// Claude Code 全局配置(含 <c>oauthAccount</c>);null 时：显式给了 <paramref name="credentialsPath"/>
    /// 就取它同目录的 <c>.claude.json</c>(测试天然隔离),否则用默认路径(并交叉核对另一份全局配置)。
    /// </param>
    /// <param name="globalConfigPaths">显式给出要交叉核对的全部全局配置(优先于 <paramref name="globalConfigPath"/>)。</param>
    public ClaudeCredentialSource(
        string? credentialsPath = null,
        string? globalConfigPath = null,
        Func<DateTimeOffset>? clock = null,
        Func<string, string?>? environment = null,
        IReadOnlyList<string>? globalConfigPaths = null)
    {
        _env = environment ?? Environment.GetEnvironmentVariable;
        string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        _credentialsPath = credentialsPath ?? CredentialsPathFor(_env, userProfile);
        _globalConfigPaths = globalConfigPaths is { Count: > 0 }
            ? globalConfigPaths.ToArray()
            : globalConfigPath is not null
                ? [globalConfigPath]
                : credentialsPath is not null
                    ? [Path.Combine(Path.GetDirectoryName(Path.GetFullPath(credentialsPath))!, ".claude.json")]
                    : GlobalConfigPathsFor(_env, userProfile);
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    public static string DefaultCredentialsPath() => CredentialsPathFor(Environment.GetEnvironmentVariable,
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));

    public static string CredentialsPathFor(Func<string, string?> env, string userProfile) =>
        Path.Combine(ConfigRoot(env, userProfile), ".credentials.json");

    /// <summary>
    /// 与 Claude Code 的全局配置定位一致：<c>&lt;配置根&gt;/.config.json</c> 是非空文件就用它，否则是
    /// <c>CLAUDE_CONFIG_DIR</c>(或用户目录)下的 <c>.claude.json</c>。
    /// </summary>
    public static string GlobalConfigPathFor(Func<string, string?> env, string userProfile)
    {
        string modern = Path.Combine(ConfigRoot(env, userProfile), ".config.json");
        try
        {
            if (new FileInfo(modern) is { Exists: true, Length: > 0 })
            {
                return modern;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
        }

        return Path.Combine(env("CLAUDE_CONFIG_DIR") is { Length: > 0 } dir ? dir : userProfile, ".claude.json");
    }

    /// <summary>
    /// 主全局配置 + 另一份可能存在的全局配置。本机 CLI 用 <c>~/.claude.json</c>,而以
    /// <c>CLAUDE_CONFIG_DIR=~/.claude</c> 运行的 Claude Code(桌面端内嵌)写 <c>~/.claude/.claude.json</c>;
    /// 两者共用同一份凭据文件，切换账号后可能分叉，所以都要读来交叉核对。
    /// </summary>
    public static string[] GlobalConfigPathsFor(Func<string, string?> env, string userProfile)
    {
        string primary = GlobalConfigPathFor(env, userProfile);
        string secondary = Path.Combine(ConfigRoot(env, userProfile), ".claude.json");
        return string.Equals(Path.GetFullPath(primary), Path.GetFullPath(secondary), StringComparison.OrdinalIgnoreCase)
            ? [primary]
            : [primary, secondary];
    }

    private static string ConfigRoot(Func<string, string?> env, string userProfile) =>
        env("CLAUDE_CONFIG_DIR") is { Length: > 0 } dir ? dir : Path.Combine(userProfile, ".claude");

    public string CredentialsPath => _credentialsPath;

    /// <summary>读取当前登录。未登录、损坏、被清空或令牌到期时 Ok=false 并给出分类原因。</summary>
    public ClaudeCredential Read()
    {
        CredentialsFile? file = ReadFile(out string? unreadable);
        ClaudeAiOauth? oauth = file?.ClaudeAiOauth;
        // 账号身份只来自全局配置的 oauthAccount,与令牌在不在无关:登录失效时仍带上它,
        // 引擎才能把原因归为「凭据不可用(需要 /login)」而不是误报成「账号身份未核实」。
        string fingerprint = AccountMaterial() is { } material ? Fingerprint(material) : string.Empty;
        if (unreadable is not null)
        {
            // 文件在却读不出:多半是 Claude Code 正在改写它。判断不了，原因单列(unreadable),界面显示「未核实」而不是「未登录」。
            return new ClaudeCredential(false, null, fingerprint, "unreadable", SourceCli, unreadable);
        }

        if (oauth?.AccessToken is not { Length: > 0 } accessToken)
        {
            if (IsCleared(oauth))
            {
                return new ClaudeCredential(false, null, fingerprint, "login_expired", SourceCli,
                    "Claude Code 登录已失效(刷新被服务端拒绝后已清空):请在终端运行 claude 并执行 /login");
            }

            return CredentialStoreMoved(file)
                ? new ClaudeCredential(false, null, fingerprint, "credential_store_moved", SourceCli,
                    "Claude Code 已改用 Windows 凭据管理器保存登录,AI Resume 读不到额度凭据")
                : new ClaudeCredential(false, null, fingerprint, "no_credentials", SourceCli,
                    unreadable ?? "Claude Code 未登录：请在终端运行 claude 并执行 /login");
        }

        // Claude Code 总是写 expiresAt;缺了(或不是合理的毫秒时间戳)说明文件不是它写的或已损坏，保守按未登录处理。
        // 与 Describe 用同一个范围保护:越界值在这里抛异常，会从注册表一路冒到面板与在途续跑。
        if (FromMilliseconds(oauth.ExpiresAt) is not { } tokenExpires)
        {
            return new ClaudeCredential(false, null, fingerprint, "no_credentials", SourceCli,
                "Claude Code 登录文件缺少到期时间：请在终端运行 claude 并执行 /login");
        }

        DateTimeOffset now = _clock();
        if (FromMilliseconds(oauth.RefreshTokenExpiresAt) is { } loginExpires && loginExpires <= now)
        {
            return new ClaudeCredential(false, null, fingerprint, "login_expired", SourceCli,
                "Claude Code 登录已到期：请在终端运行 claude 并执行 /login");
        }

        if (tokenExpires - now < TimeSpan.FromSeconds(TokenExpiryGraceSeconds))
        {
            return new ClaudeCredential(false, null, fingerprint, "token_expired", SourceCli,
                "Claude Code 访问令牌已过期：打开一次 Claude Code(或保持 Remote Control 在线)即会自动续期");
        }

        return new ClaudeCredential(true, accessToken, fingerprint, null, SourceCli, null, tokenExpires);
    }

    /// <summary>不含实值的状态描述。</summary>
    public ClaudeCredentialStatus Describe()
    {
        CredentialsFile? file = ReadFile(out string? unreadable);
        ClaudeAiOauth? oauth = file?.ClaudeAiOauth;
        if (unreadable is not null)
        {
            return new ClaudeCredentialStatus(ClaudeLoginVerdict.Unverified, null, null, null, false, unreadable);
        }

        if (oauth?.AccessToken is not { Length: > 0 })
        {
            if (IsCleared(oauth))
            {
                return new ClaudeCredentialStatus(ClaudeLoginVerdict.LoginExpired, null, null, null, false,
                    "Claude Code 登录已失效：在终端运行 claude 并执行 /login");
            }

            return CredentialStoreMoved(file)
                ? new ClaudeCredentialStatus(ClaudeLoginVerdict.StoreMoved, null, null, null, false,
                    "Claude Code 已改用 Windows 凭据管理器保存登录,AI Resume 读不到额度凭据")
                : new ClaudeCredentialStatus(ClaudeLoginVerdict.LoggedOut, null, null, null, false,
                    "Claude Code 未登录：在终端运行 claude 并执行 /login");
        }

        DateTimeOffset now = _clock();
        DateTimeOffset? tokenExpires = FromMilliseconds(oauth.ExpiresAt);
        DateTimeOffset? loginExpires = FromMilliseconds(oauth.RefreshTokenExpiresAt);
        bool accountVerified = AccountMaterial() is not null;
        string? subscription = string.IsNullOrWhiteSpace(oauth.SubscriptionType) ? null : oauth.SubscriptionType.Trim();

        string? expiringNote = loginExpires is { } soon && soon > now && soon - now < LoginExpiryWarning
            ? $"Claude Code 登录将于 {Local(soon)} 到期，届时需运行 claude 并 /login"
            : null;
        ClaudeLoginVerdict verdict;
        string? problem;
        if (tokenExpires is null)
        {
            verdict = ClaudeLoginVerdict.LoggedOut;
            problem = "Claude Code 登录文件缺少到期时间：请运行 claude 并 /login";
        }
        else if (loginExpires is { } login && login <= now)
        {
            verdict = ClaudeLoginVerdict.LoginExpired;
            problem = $"Claude Code 登录已于 {Local(login)} 到期：请运行 claude 并 /login";
        }
        else if (tokenExpires.Value - now < TimeSpan.FromSeconds(TokenExpiryGraceSeconds))
        {
            // 令牌过期是可自愈的(Claude Code 一运行就续期),登录将到期是硬期限;两件事同时成立时都要说。
            verdict = ClaudeLoginVerdict.TokenExpired;
            problem = "访问令牌已过期，打开一次 Claude Code 即自动续期;续期前无法读取额度" +
                      (expiringNote is null ? string.Empty : ";" + expiringNote);
        }
        else
        {
            verdict = expiringNote is null ? ClaudeLoginVerdict.Ok : ClaudeLoginVerdict.LoginExpiring;
            problem = expiringNote;
        }

        if (!accountVerified && verdict is ClaudeLoginVerdict.Ok or ClaudeLoginVerdict.LoginExpiring)
        {
            problem = (problem is null ? string.Empty : problem + ";") +
                      "账号身份未核实(Claude Code 全局配置里没有一致的 oauthAccount):续跑会等待，打开一次 Claude Code 通常即可恢复";
        }

        return new ClaudeCredentialStatus(verdict, tokenExpires, loginExpires, subscription, accountVerified, problem);
    }

    /// <summary>
    /// 账号身份材料:<c>oauthAccount</c> 的组织与账号 UUID。
    /// 全局配置约 100 KB、被 Claude Code 频繁改写：按长度与写入时间缓存解析结果;
    /// 正被改写读不出时沿用本进程上一次成功的值;多份全局配置的组织不一致时视为未核实(null)。
    /// </summary>
    private string? AccountMaterial()
    {
        var materials = new List<string>();
        bool anyUnreadable = false;
        foreach (string path in _globalConfigPaths)
        {
            AccountInfo info = ReadAccount(path);
            if (info.Unreadable)
            {
                anyUnreadable = true;
            }
            else if (info.Material is { } material)
            {
                materials.Add(material);
            }
        }

        string? result = materials.Distinct(StringComparer.Ordinal).Count() switch
        {
            1 => materials[0],
            0 when anyUnreadable => Volatile.Read(ref _lastGoodMaterial),
            _ => null,
        };
        if (result is not null && materials.Count > 0)
        {
            Volatile.Write(ref _lastGoodMaterial, result);
        }

        return result;
    }

    private sealed record AccountInfo(string? Material, bool Unreadable);

    private AccountInfo ReadAccount(string path)
    {
        FileInfo info;
        try
        {
            info = new FileInfo(path);
            if (!info.Exists)
            {
                return new AccountInfo(null, false);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return new AccountInfo(null, true);
        }

        long length = info.Length;
        long ticks = info.LastWriteTimeUtc.Ticks;
        lock (_accountLock)
        {
            if (_accountCache.TryGetValue(path, out var cached) && cached.Length == length && cached.WriteTicks == ticks)
            {
                return cached.Info;
            }
        }

        AccountInfo result;
        try
        {
            // 该文件 projects 映射里有仅大小写不同的重复键(PowerShell ConvertFrom-Json 会因此失败);
            // JsonDocument 允许重复键，这里只按精确大小写取 oauthAccount。
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using JsonDocument doc = JsonDocument.Parse(stream);
            result = doc.RootElement.ValueKind == JsonValueKind.Object &&
                     doc.RootElement.TryGetProperty("oauthAccount", out JsonElement account) &&
                     account.ValueKind == JsonValueKind.Object &&
                     Text(account, "organizationUuid") is { } organization && Guid.TryParse(organization, out _)
                ? new AccountInfo("account:" + organization.Trim() + "/" + (Text(account, "accountUuid")?.Trim() ?? string.Empty), false)
                : new AccountInfo(null, false);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            // 正在被 Claude Code 改写或损坏：本轮当作读不到，不缓存失败。
            return new AccountInfo(null, true);
        }

        lock (_accountLock)
        {
            _accountCache[path] = (length, ticks, result);
        }

        return result;
    }

    /// <summary>
    /// Claude Code 刷新被服务端拒绝(invalid_grant)时会把 accessToken/refreshToken 置空、expiresAt 置 0;
    /// 这不是「从没登录」,而是登录已失效。
    /// </summary>
    private static bool IsCleared(ClaudeAiOauth? oauth) =>
        oauth is { AccessToken: "" or null, ExpiresAt: 0 };

    /// <summary>
    /// Claude Code 的 Windows 凭据管理器灰度(<c>CLAUDE_CODE_FORCE_WINDOWS_CREDMAN=1</c> 或全局配置
    /// <c>cachedGrowthBookFeatures.tengu_windows_credman=true</c>)一旦生效，首次写入成功后会删除
    /// <c>.credentials.json</c>。届时读不到文件不能说成「未登录」。
    /// </summary>
    private bool CredentialStoreMoved(CredentialsFile? file)
    {
        if (file?.ClaudeAiOauth is not null)
        {
            return false;
        }

        if (_env("CLAUDE_CODE_FORCE_WINDOWS_CREDMAN") == "1")
        {
            return true;
        }

        foreach (string path in _globalConfigPaths)
        {
            try
            {
                if (!File.Exists(path)) continue;
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using JsonDocument doc = JsonDocument.Parse(stream);
                if (doc.RootElement.ValueKind == JsonValueKind.Object &&
                    doc.RootElement.TryGetProperty("cachedGrowthBookFeatures", out JsonElement features) &&
                    features.ValueKind == JsonValueKind.Object &&
                    features.TryGetProperty("tengu_windows_credman", out JsonElement flag) &&
                    flag.ValueKind == JsonValueKind.True)
                {
                    return true;
                }
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
            {
            }
        }

        return false;
    }

    private CredentialsFile? ReadFile(out string? unreadable)
    {
        unreadable = null;
        try
        {
            if (!File.Exists(_credentialsPath))
            {
                return null;
            }

            // 共享读写/删除：不妨碍 Claude Code 在刷新时原子替换这份文件。
            using var stream = new FileStream(_credentialsPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return JsonSerializer.Deserialize<CredentialsFile>(stream);
        }
        catch (JsonException)
        {
            unreadable = "Claude Code 登录文件格式无法解析：请运行 claude 并 /login";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            unreadable = "Claude Code 登录文件无法读取";
        }

        return null;
    }

    private static DateTimeOffset? FromMilliseconds(long? ms) =>
        ms is > 0 and < 253402300799000 ? DateTimeOffset.FromUnixTimeMilliseconds(ms.Value) : null;

    private static string Local(DateTimeOffset at) => at.ToLocalTime().ToString("M-d HH:mm");

    private static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static string Fingerprint(string material) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(material)));

    /// <summary>凭据文件形状(2026-10-30 本机 Claude Code 2.1.300 录制，顶层只有 claudeAiOauth 与 mcpOAuth)。</summary>
    private sealed class CredentialsFile
    {
        [JsonPropertyName("claudeAiOauth")]
        public ClaudeAiOauth? ClaudeAiOauth { get; set; }
    }

    private sealed class ClaudeAiOauth
    {
        [JsonPropertyName("accessToken")]
        public string? AccessToken { get; set; }

        [JsonPropertyName("expiresAt")]
        public long? ExpiresAt { get; set; }

        [JsonPropertyName("refreshTokenExpiresAt")]
        public long? RefreshTokenExpiresAt { get; set; }

        [JsonPropertyName("subscriptionType")]
        public string? SubscriptionType { get; set; }
    }
}
