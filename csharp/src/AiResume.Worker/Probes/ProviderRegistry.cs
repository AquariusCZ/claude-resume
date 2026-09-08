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
    private readonly ClaudeOAuthUsageProbe _oauth;

    public ProviderRegistry(Func<string, string?>? environment = null, string? userProfile = null,
        Func<CodexProviderCredentials>? codex = null, ClaudeOAuthUsageProbe? oauth = null)
    {
        _env = environment ?? Environment.GetEnvironmentVariable;
        _userProfile = userProfile ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        _codex = codex ?? (() => CodexAuthProbe.ReadActiveProviderCredentials());
        _oauth = oauth ?? new ClaudeOAuthUsageProbe(credentialsPath: Path.Combine(_env("CLAUDE_CONFIG_DIR") ?? Path.Combine(_userProfile, ".claude"), ".credentials.json"));
    }

    public IReadOnlyList<ProviderConnection> Read()
    {
        var result = new List<ProviderConnection>();
        var token = _oauth.ReadAccessToken();
        result.Add(Create("claude-subscription", "Claude · Anthropic 订阅", "claudecode", "claude-oauth",
            SimpleCredentials("https://api.anthropic.com", token.AccessToken, "claude-oauth", token.FailureReason),
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

        try
        {
            CodexProviderCredentials codex = _codex();
            bool chatGpt = codex.CredentialSource == "auth.json:chatgpt" && codex.IsBuiltInOpenAi &&
                Uri.TryCreate(codex.BaseUrl, UriKind.Absolute, out Uri? codexUri) &&
                codexUri.Scheme == "https" && codexUri.Host == "chatgpt.com" && codexUri.IsDefaultPort;
            string name = chatGpt ? "Codex · ChatGPT" : "Codex · " + (codex.ProviderId ?? SafeHost(codex.BaseUrl));
            result.Add(Create("codex-active", name, "codex", chatGpt ? "chatgpt" : "openai-api", codex,
                chatGpt && codex.AccountId is { Length: > 0 } ? codex.AccountId : null));
        }
        catch (Exception)
        {
            result.Add(Create("codex-active", "Codex · 未配置", "codex", "openai-api",
                SimpleCredentials(null, null, "none", "供应商配置无法读取")));
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
}
