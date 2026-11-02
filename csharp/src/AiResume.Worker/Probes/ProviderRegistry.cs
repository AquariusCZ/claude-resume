using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AiResume.Worker.Quota;

namespace AiResume.Worker.Probes;

/// <summary>只在内存中持有凭据；界面只能取得 ProviderObservation。</summary>
public sealed class ProviderConnection
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string Agent { get; init; }
    public required string Kind { get; init; }
    public required string Identity { get; init; }

    /// <summary>凭据不可用时的稳定原因码(如 token_expired / login_expired / account_unverified),不含实值。</summary>
    public string? CredentialFailure { get; init; }

    /// <summary>
    /// 与 <see cref="Credentials"/> 里的访问令牌出自**同一次读取**的到期时间(只有 Claude 订阅行有)。
    /// 续跑前的寿命门槛必须看它:另读一次文件可能读到 Claude Code 刚换上的新令牌,而注入子进程的是旧的。
    /// </summary>
    public DateTimeOffset? AccessTokenExpiresAtUtc { get; init; }

    internal CodexProviderCredentials Credentials { get; init; } = null!;
    public override string ToString() => Name;
}

public sealed class ProviderRegistry
{
    private readonly Func<string, string?> _env;
    private readonly string _userProfile;
    private readonly Func<CodexProviderCredentials> _codex;
    private readonly Func<CodexProviderCredentials> _codexOfficial;
    private readonly ClaudeOAuthUsageProbe _oauth;

    /// <param name="userProfile">用户目录;测试注入临时目录，从而 Claude / OpenCode 的凭据全部落在隔离目录下。</param>
    /// <param name="codex">当前 <c>model_provider</c> 指向的连接(第三方静态 provider 才单独成行)。</param>
    /// <param name="codexOfficial">
    /// Codex 官方连接(auth.json 登录)。null 时读真机;测试若只注入 <paramref name="codex"/>,
    /// 官方连接默认视为未登录，不会碰真机 auth.json。
    /// </param>
    public ProviderRegistry(Func<string, string?>? environment = null, string? userProfile = null,
        Func<CodexProviderCredentials>? codex = null, ClaudeOAuthUsageProbe? oauth = null,
        Func<CodexProviderCredentials>? codexOfficial = null)
    {
        _env = environment ?? Environment.GetEnvironmentVariable;
        _userProfile = userProfile ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        _codex = codex ?? (() => CodexAuthProbe.ReadActiveProviderCredentials());
        _codexOfficial = codexOfficial ?? (codex is null
            ? () => CodexAuthProbe.ReadOfficialCredentials()
            : () => SimpleCredentials(null, null, "none", "Codex 未登录:请运行 codex login"));
        // 与额度路径(QuotaService 的默认 OAuth 探针)读同一组全局配置并交叉核对:只读主配置时,
        // 只用桌面端(它写 ~/.claude/.claude.json)的用户永远「账号未核实」,两份分叉时两条路径又各说各话。
        _oauth = oauth ?? new ClaudeOAuthUsageProbe(credentials: new ClaudeCredentialSource(
            ClaudeCredentialSource.CredentialsPathFor(_env, _userProfile),
            environment: _env,
            globalConfigPaths: ClaudeCredentialSource.GlobalConfigPathsFor(_env, _userProfile)));
    }

    /// <summary>Claude 登录解析器(不含实值),供控制面显示登录状态。</summary>
    public ClaudeCredentialSource ClaudeCredentials => _oauth.Credentials;

    public IReadOnlyList<ProviderConnection> Read()
    {
        var result = new List<ProviderConnection> { ClaudeSubscriptionRow() };

        Dictionary<string, string> settingsEnv;
        try { settingsEnv = ReadClaudeEnvironment(); }
        catch (Exception)
        {
            settingsEnv = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            result.Add(Create("claude-api", "Claude · 配置未核实", "claudecode", "anthropic-api",
                SimpleCredentials(null, null, "none", "Claude 配置无法读取")));
        }
        string? ReadEnv(string key) => settingsEnv.TryGetValue(key, out string? value) ? value : _env(key);
        string? endpoint = ReadEnv("ANTHROPIC_BASE_URL");
        string? bearer = ReadEnv("ANTHROPIC_AUTH_TOKEN");
        string? apiKey = !string.IsNullOrWhiteSpace(bearer) ? bearer : ReadEnv("ANTHROPIC_API_KEY");
        if (!result.Any(p => p.Id == "claude-api") &&
            (!string.IsNullOrWhiteSpace(endpoint) || !string.IsNullOrWhiteSpace(apiKey)))
        {
            string baseUrl = string.IsNullOrWhiteSpace(endpoint) ? "https://api.anthropic.com" : endpoint;
            bool deepSeek = IsDeepSeek(baseUrl);
            string name = deepSeek ? "Claude · DeepSeek" : "Claude · " + SafeHost(baseUrl);
            string source = !string.IsNullOrWhiteSpace(bearer) ? "anthropic-bearer" : "anthropic-key";
            result.Add(Create("claude-api", name, "claudecode", deepSeek ? "deepseek" : "anthropic-api",
                SimpleCredentials(baseUrl, apiKey, source, apiKey is null ? "缺少 API 凭据" : null)));
        }

        result.Add(CodexOfficialRow());

        // 第三方静态 provider 只有在自带凭据、且不是借用 ChatGPT 登录的本机桥接时才单独成行。
        try
        {
            CodexProviderCredentials codex = _codex();
            if (!codex.IsBuiltInOpenAi && !codex.RequiresOpenAiAuth && !IsLoopback(codex.BaseUrl) &&
                codex.ProviderId is { Length: > 0 })
            {
                result.Add(Create("codex-active", "Codex · " + codex.ProviderId, "codex", "openai-api", codex));
            }
        }
        catch (Exception)
        {
            // 第三方配置读不到不影响官方行;官方行已经如实说明登录状态。
        }

        // DeepSeek 只作「OpenCode 那个账户的余额」展示行，不是续跑目标:续跑只针对 Claude Code / Codex 原生会话,
        // 拿 OpenCode 的密钥去跑 Claude Code 会把一个客户端的凭据挪给另一个客户端用。
        try
        {
            OpenCodeProviderKey openCodeKey = OpenCodeCredentials.ReadDeepSeek(_env, _userProfile);
            if (openCodeKey.Source != OpenCodeCredentials.SourceNone)
                result.Add(Create(OpenCodeCredentials.ProviderId,
                    openCodeKey.Source == OpenCodeCredentials.SourceEnvironment ? "DeepSeek · 环境变量" : "DeepSeek · OpenCode",
                    "opencode", "deepseek",
                    SimpleCredentials("https://api.deepseek.com", openCodeKey.Ok ? openCodeKey.Key : null, openCodeKey.Source, openCodeKey.Problem)));
        }
        catch (Exception)
        {
            // OpenCode 配置读不到只影响这一行展示;不能让它拖垮 Claude/Codex 行(续跑的在途复核也走这里)。
        }

        return result;
    }

    /// <summary>
    /// 只取一行。续跑在途时每 500ms 复核一次执行计划(<c>IsCurrent</c> → <c>Find</c>):只读被问到的那一行,
    /// 不顺带把 OpenCode、Codex 第三方配置与 Claude settings 全读一遍——那些是别人的文件,读得越少越不妨碍它们自己写。
    /// </summary>
    public ProviderConnection? Find(string id) => id switch
    {
        "claude-subscription" => ClaudeSubscriptionRow(),
        "codex-official" => CodexOfficialRow(),
        _ => Read().SingleOrDefault(p => p.Id == id),
    };

    private ProviderConnection ClaudeSubscriptionRow()
    {
        ClaudeCredential token;
        try
        {
            token = _oauth.ReadAccessToken();
        }
        catch (Exception)
        {
            // 解析器本该把可预期的失败都归类;意外异常只降级这一行,不让整个面板与在途续跑跟着抛。
            return new ProviderConnection
            {
                Id = "claude-subscription", Name = "Claude · Anthropic 订阅", Agent = "claudecode", Kind = "claude-oauth",
                Credentials = SimpleCredentials("https://api.anthropic.com", null, "none", "Claude 登录状态无法读取"),
                CredentialFailure = "unreadable",
                Identity = string.Empty,
            };
        }

        string? claudeProblem = !token.Ok ? token.Detail ?? token.FailureReason
            : token.CredentialFingerprint.Length == 0
                ? "Claude 账号身份未核实:Claude Code 全局配置里没有一致的 oauthAccount,打开一次 Claude Code 通常即可恢复"
                : null;
        // 订阅行的身份只由账号(组织+账号 UUID)派生，令牌轮换不改变它;核实不了账号时身份为空，
        // 绝不退回包含令牌的连接指纹——那会让同一账号每 8 小时被判成「供应商已改变」(2026-10-30 体检根因 a)。
        return new ProviderConnection
        {
            Id = "claude-subscription", Name = "Claude · Anthropic 订阅", Agent = "claudecode", Kind = "claude-oauth",
            Credentials = SimpleCredentials("https://api.anthropic.com", token.AccessToken, "claude-oauth:" + token.Source,
                claudeProblem),
            CredentialFailure = token.Ok && token.CredentialFingerprint.Length == 0 ? "account_unverified" : token.FailureReason,
            AccessTokenExpiresAtUtc = token.AccessTokenExpiresAtUtc,
            Identity = token.CredentialFingerprint.Length > 0
                ? Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("claude-subscription:" + token.CredentialFingerprint)))
                : string.Empty,
        };
    }

    /// <summary>
    /// Codex 官方行只看 auth.json 的登录,不跟随 config.toml 的 model_provider:
    /// 本机桥接(如 codex-chatgpt-web)借用的就是同一份 ChatGPT 登录,额度也是同一份。
    /// </summary>
    private ProviderConnection CodexOfficialRow()
    {
        try
        {
            CodexProviderCredentials official = _codexOfficial();
            bool chatGpt = official.CredentialSource == "auth.json:chatgpt";
            bool officialApiKey = official.CredentialSource == "auth.json:apikey";
            string name = chatGpt ? "Codex · ChatGPT 官方" : officialApiKey ? "Codex · OpenAI API 官方" : "Codex · 官方(未登录)";
            return Create("codex-official", name, "codex", chatGpt ? "chatgpt" : "openai-api", official,
                chatGpt && official.AccountId is { Length: > 0 } ? official.AccountId : null);
        }
        catch (Exception)
        {
            return Create("codex-official", "Codex · 官方(未核实)", "codex", "chatgpt",
                SimpleCredentials(null, null, "none", "Codex 登录状态无法读取"));
        }
    }

    private Dictionary<string, string> ReadClaudeEnvironment()
    {
        string root = _env("CLAUDE_CONFIG_DIR") ?? Path.Combine(_userProfile, ".claude");
        string modernGlobal = Path.Combine(root, ".config.json");
        string global = File.Exists(modernGlobal) ? modernGlobal : Path.Combine(_env("CLAUDE_CONFIG_DIR") ?? _userProfile, ".claude.json");
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (string path in new[] { global, Path.Combine(root, "settings.json") })
        {
            if (!File.Exists(path)) continue;
            using JsonDocument document = JsonDocument.Parse(SharedFile.ReadAllText(path));
            if (document.RootElement.TryGetProperty("env", out JsonElement env) && env.ValueKind == JsonValueKind.Object)
                foreach (JsonProperty property in env.EnumerateObject())
                    if (property.Value.ValueKind == JsonValueKind.String)
                        values[property.Name] = property.Value.GetString()!;
        }
        return values;
    }

    private static ProviderConnection Create(string id, string name, string agent, string kind,
        CodexProviderCredentials credentials, string? accountIdentity = null) => new()
    {
        Id = id, Name = name, Agent = agent, Kind = kind, Credentials = credentials,
        Identity = accountIdentity is { Length: > 0 }
            ? Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(id + ":" + accountIdentity)))
            : CodexAuthProbe.CreateProviderIdentity(credentials with { Model = null }),
    };

    private static CodexProviderCredentials SimpleCredentials(string? endpoint, string? token, string source, string? problem) =>
        new(endpoint, token, null, "responses", new Dictionary<string, string>(), new Dictionary<string, string>(),
            source, problem, null, null, false, true);

    internal static bool IsDeepSeek(string? url) => Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) &&
        uri.Scheme == "https" && uri.IsDefaultPort && uri.UserInfo.Length == 0 && uri.Query.Length == 0 &&
        uri.AbsolutePath.TrimEnd('/') is "" or "/v1" or "/anthropic" && uri.Host.Equals("api.deepseek.com", StringComparison.OrdinalIgnoreCase);

    private static string SafeHost(string? url) => Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) ? uri.Host : "端点未核实";

    internal static bool IsLoopback(string? url) => !Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) || uri.IsLoopback ||
        uri.Host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase);
}
