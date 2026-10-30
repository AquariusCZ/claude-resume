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

    /// <param name="secretsRoot">
    /// AI Resume 自持长期令牌的 DPAPI 根目录。null 时:注入了 <paramref name="userProfile"/>(测试)
    /// 就落在它下面的隔离目录,否则用生产状态目录。测试因此永远碰不到真机令牌。
    /// </param>
    /// <param name="codex">当前 <c>model_provider</c> 指向的连接(第三方静态 provider 才单独成行)。</param>
    /// <param name="codexOfficial">
    /// Codex 官方连接(auth.json 登录)。null 时读真机;测试若只注入 <paramref name="codex"/>,
    /// 官方连接默认视为未登录,不会碰真机 auth.json。
    /// </param>
    public ProviderRegistry(Func<string, string?>? environment = null, string? userProfile = null,
        Func<CodexProviderCredentials>? codex = null, ClaudeOAuthUsageProbe? oauth = null,
        string? secretsRoot = null, Func<CodexProviderCredentials>? codexOfficial = null)
    {
        _env = environment ?? Environment.GetEnvironmentVariable;
        _userProfile = userProfile ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        _codex = codex ?? (() => CodexAuthProbe.ReadActiveProviderCredentials());
        _codexOfficial = codexOfficial ?? (codex is null
            ? () => CodexAuthProbe.ReadOfficialCredentials()
            : () => SimpleCredentials(null, null, "none", "Codex 未登录:请运行 codex login"));
        _oauth = oauth ?? new ClaudeOAuthUsageProbe(
            credentialsPath: Path.Combine(_env("CLAUDE_CONFIG_DIR") ?? Path.Combine(_userProfile, ".claude"), ".credentials.json"),
            secretsRoot: secretsRoot ?? (userProfile is null ? ShadowPaths.SecretsRoot : Path.Combine(userProfile, ".ai-resume-secrets")));
    }

    /// <summary>Claude 凭据解析器(不含实值),供控制面显示与保存长期令牌。</summary>
    public ClaudeCredentialSource ClaudeCredentials => _oauth.Credentials;

    public IReadOnlyList<ProviderConnection> Read()
    {
        var result = new List<ProviderConnection>();
        ClaudeCredential token = _oauth.ReadAccessToken();
        result.Add(Create("claude-subscription", "Claude · Anthropic 订阅", "claudecode", "claude-oauth",
            SimpleCredentials("https://api.anthropic.com", token.AccessToken, "claude-oauth:" + token.Source,
                token.Ok ? null : token.Detail ?? token.FailureReason),
            token.CredentialFingerprint));

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

        // Codex 官方行只看 auth.json 的登录,不跟随 config.toml 的 model_provider:
        // 本机桥接(如 codex-chatgpt-web)借用的就是同一份 ChatGPT 登录,额度也是同一份。
        try
        {
            CodexProviderCredentials official = _codexOfficial();
            bool chatGpt = official.CredentialSource == "auth.json:chatgpt";
            bool officialApiKey = official.CredentialSource == "auth.json:apikey";
            string name = chatGpt ? "Codex · ChatGPT 官方" : officialApiKey ? "Codex · OpenAI API 官方" : "Codex · 官方(未登录)";
            result.Add(Create("codex-official", name, "codex", chatGpt ? "chatgpt" : "openai-api", official,
                chatGpt && official.AccountId is { Length: > 0 } ? official.AccountId : null));
        }
        catch (Exception)
        {
            result.Add(Create("codex-official", "Codex · 官方(未核实)", "codex", "chatgpt",
                SimpleCredentials(null, null, "none", "Codex 登录状态无法读取")));
        }

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

        string? deepSeekKey = _env(DeepSeekProbe.ApiKeyEnvName);
        if (!string.IsNullOrWhiteSpace(deepSeekKey))
            result.Add(Create("deepseek", "DeepSeek · 官方 API", "claudecode", "deepseek",
                SimpleCredentials("https://api.deepseek.com/anthropic", deepSeekKey, "anthropic-bearer", null)));
        return result;
    }

    public ProviderConnection? Find(string id) => Read().SingleOrDefault(p => p.Id == id);

    private Dictionary<string, string> ReadClaudeEnvironment()
    {
        string root = _env("CLAUDE_CONFIG_DIR") ?? Path.Combine(_userProfile, ".claude");
        string modernGlobal = Path.Combine(root, ".config.json");
        string global = File.Exists(modernGlobal) ? modernGlobal : Path.Combine(_env("CLAUDE_CONFIG_DIR") ?? _userProfile, ".claude.json");
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (string path in new[] { global, Path.Combine(root, "settings.json") })
        {
            if (!File.Exists(path)) continue;
            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path));
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
