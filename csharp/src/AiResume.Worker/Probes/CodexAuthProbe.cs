using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Tomlyn;
using Tomlyn.Model;

namespace AiResume.Worker.Probes;

/// <summary>
/// Codex **真实可用性探测**:<c>GET {base_url}/models</c>,并按 Codex 的 provider
/// 认证选择规则决定是否带凭据。
///
/// 为什么需要它:`codex doctor` 的 route probe 是**不带凭据**的,401 只说明"路由需要认证",
/// 证明不了你的 key 有没有权限;而唯一此前能证明的办法 `codex exec` 要 10-12 秒、
/// **2.3 万 tokens**(实测),贵到不可能每次开窗都跑。
///
/// 带凭据打 /models 两边都占:**1.3 秒、0 token**,而且 200/401 的分野
/// 完全由凭据决定(实测:同一请求带 key → 200 并返回模型列表;去掉 key → 401 API_KEY_REQUIRED)。
/// 于是"每次探测都是真的"从奢侈变成默认。
///
/// **必须带正常 User-Agent。** 实测该端点在 Cloudflare 后面,
/// 用 .NET/Python 的默认 UA 一律 403 `error code: 1010`(客户端被封),
/// 带不带凭据都一样 —— 那会被误读成"认证失败",正是这个探测要避免的错。
///
/// 凭据只从本机 Codex 自己的文件读出来直接放进 Authorization 头,
/// **不落盘、不进日志、不进任何应答**。
/// </summary>
public enum CodexAuthOutcome
{
    /// <summary>带凭据请求成功——key 确实有权限。</summary>
    Authorized,

    /// <summary>
    /// 服务端接受了凭据，但最小推理没有得到成功响应。
    /// 这只能证明鉴权，不能证明任务可运行，因此界面必须保持非绿色。
    /// </summary>
    InferenceUnverified,

    /// <summary>
    /// 能列模型,但不允许推理。
    ///
    /// 这一档是 2026-08-08 第二轮审计逼出来的(A6):审计方架了个假端点,
    /// <c>{base_url}/models</c> 返 200、<c>{base_url}/responses</c> 返 403,
    /// 界面照样绿着说"凭据已验证"。
    /// 而列模型往往是**公开或低权限**的路由 —— 它证明的是"服务端认识这把 key",
    /// 不是"这把 key 能跑活儿"。真实场景里 sub2api 的额度用尽、
    /// 按模型授权收紧,表现正是这个组合。
    /// </summary>
    NoInference,

    /// <summary>凭据被拒。</summary>
    Rejected,

    /// <summary>余额不足或被限流。</summary>
    Limited,

    /// <summary>服务端异常(5xx)。</summary>
    ServerError,

    /// <summary>网络层失败(DNS/TCP/TLS/超时)。</summary>
    NetworkFailed,

    /// <summary>本机读不到 base_url 或 key,无法发起带凭据请求。</summary>
    NotConfigured,
}

public sealed record CodexAuthResult(
    CodexAuthOutcome Outcome,
    string Detail,
    string? Reason = null,
    bool UsedCredential = true);

public sealed record CodexProviderCredentials(
    string? BaseUrl,
    string? BearerToken,
    string? Model,
    string? WireApi,
    IReadOnlyDictionary<string, string> QueryParameters,
    IReadOnlyDictionary<string, string> RequestHeaders,
    string? CredentialSource,
    string? Problem,
    string? AccountId,
    string? ProviderId,
    bool IsBuiltInOpenAi,
    bool RequiresOpenAiAuth);

public static partial class CodexAuthProbe
{
    /// <summary>Cloudflare 会按 UA 拦默认的 HTTP 客户端,必须伪装成常规浏览器。</summary>
    public const string BrowserUserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36";

    private const int TimeoutSeconds = 20;
    private const int MaxInferenceResponseBytes = 64 * 1024;

    /// <summary>403 时为认出 CDN 拦截而读的最大响应体,够装下 Cloudflare 的 JSON 错误体与拦截页头部。</summary>
    private const int MaxBlockBodyBytes = 16 * 1024;

    /// <summary>
    /// 为一次已经解析完成的 provider 配置生成不含明文凭据的稳定身份。
    /// 同一次刷新里的所有 Codex 探针必须携带同一身份，避免切换 provider 时
    /// 把 A 的模型探测和 B 的余额拼成一个结论；凭据变化也会生成新身份，
    /// 因而不会复用旧 key 的最近余额。
    /// </summary>
    public static string CreateProviderIdentity(CodexProviderCredentials provider)
    {
        var material = new StringBuilder();
        AppendIdentityField(material, "provider", provider.ProviderId);
        AppendIdentityField(material, "base", provider.BaseUrl);
        AppendIdentityField(material, "model", provider.Model);
        AppendIdentityField(material, "wire", provider.WireApi);
        AppendIdentityField(material, "source", provider.CredentialSource);
        AppendIdentityField(material, "account", provider.AccountId);
        AppendIdentityField(material, "token", provider.BearerToken);
        AppendIdentityField(material, "requires_openai_auth", provider.RequiresOpenAiAuth ? "1" : "0");
        foreach ((string key, string value) in provider.QueryParameters.OrderBy(
                     pair => pair.Key,
                     StringComparer.Ordinal))
        {
            AppendIdentityField(material, "query:" + key, value);
        }

        foreach ((string key, string value) in provider.RequestHeaders.OrderBy(
                     pair => pair.Key,
                     StringComparer.OrdinalIgnoreCase))
        {
            AppendIdentityField(material, "header:" + key.ToLowerInvariant(), value);
        }

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(material.ToString())));
    }

    /// <summary>
    /// 从 <c>~/.codex/config.toml</c> 与 <c>~/.codex/auth.json</c> 解析出
    /// 活动 provider 的 base_url 与静态可读取凭据。**返回值含凭据,调用方不得回显。**
    /// 命令式 <c>auth</c> 由 Codex 维护刷新状态机,这里绝不执行。
    /// </summary>
    public static (string? BaseUrl, string? ApiKey) ReadActiveProvider(string? codexHome = null)
    {
        CodexProviderCredentials provider = ReadActiveProviderCredentials(codexHome);
        return (provider.BaseUrl, provider.BearerToken);
    }

    public static CodexProviderCredentials ReadActiveProviderCredentials(
        string? codexHome = null,
        Func<string, string?>? environmentVariable = null)
    {
        environmentVariable ??= Environment.GetEnvironmentVariable;
        string home = ResolveCodexHome(codexHome, environmentVariable);

        string configPath = Path.Combine(home, "config.toml");
        if (!TryReadProviderConfig(configPath, environmentVariable, out ProviderConfig config))
        {
            return MissingProvider("读不到 Codex 活动 provider 配置");
        }

        if (config.HasCommandAuth)
        {
            return new CodexProviderCredentials(
                config.BaseUrl,
                null,
                config.Model,
                config.WireApi,
                config.QueryParameters,
                config.RequestHeaders,
                "auth-command",
                "provider 使用命令式 auth,AI Resume 不执行该命令",
                null,
                config.ProviderId,
                config.IsBuiltInOpenAi,
                config.RequiresOpenAiAuth);
        }

        if (config.HasAwsAuth)
        {
            return MissingCredential(
                config,
                "aws",
                "provider 使用 AWS SigV4 认证,AI Resume 不把它误当成 Bearer 凭据");
        }

        // 上游 Codex 认证选择(锁定源码 model-provider-info/model-provider,2026-08-13):
        // provider 自有 env_key / experimental_bearer_token 优先;没有自有 Bearer 时,
        // 只有 requires_openai_auth=true 才使用 auth.json。其余 provider 按无认证处理,
        // 绝不能擅自把 OpenAI/ChatGPT 登录发往它的 base_url。
        if (!string.IsNullOrWhiteSpace(config.EnvKey))
        {
            string? token;
            try
            {
                token = environmentVariable(config.EnvKey);
            }
            catch (Exception ex) when (ex is ArgumentException or System.Security.SecurityException)
            {
                return MissingCredential(config, "env_key", $"环境变量 {config.EnvKey} 无法读取");
            }

            return CreateCredential(
                config,
                token,
                "env_key",
                $"环境变量 {config.EnvKey} 未设置",
                accountId: null);
        }

        if (!string.IsNullOrWhiteSpace(config.ExperimentalBearerToken))
        {
            return CreateCredential(
                config,
                config.ExperimentalBearerToken,
                "experimental_bearer_token",
                "provider 的 experimental_bearer_token 为空",
                accountId: null);
        }

        if (config.RequiresOpenAiAuth)
        {
            AuthFileCredential auth = ReadAuthFileCredential(Path.Combine(home, "auth.json"));
            if (config.IsBuiltInOpenAi && string.IsNullOrWhiteSpace(config.BaseUrl))
            {
                config = config with
                {
                    BaseUrl = string.Equals(auth.Source, "auth.json:chatgpt", StringComparison.Ordinal)
                        ? "https://chatgpt.com/backend-api/codex"
                        : "https://api.openai.com/v1",
                };
            }

            return string.IsNullOrWhiteSpace(auth.Token)
                ? MissingCredential(config, auth.Source, auth.Problem ?? "读不到 Codex auth.json 中的登录凭据")
                : CreateCredential(config, auth.Token, auth.Source, auth.Problem, auth.AccountId);
        }

        return new CodexProviderCredentials(
            config.BaseUrl,
            null,
            config.Model,
            config.WireApi,
            config.QueryParameters,
            config.RequestHeaders,
            "none",
            null,
            null,
            config.ProviderId,
            config.IsBuiltInOpenAi,
            config.RequiresOpenAiAuth);
    }

    private sealed record ProviderConfig(
        string ProviderId,
        string BaseUrl,
        string? Model,
        string WireApi,
        IReadOnlyDictionary<string, string> QueryParameters,
        IReadOnlyDictionary<string, string> RequestHeaders,
        string? EnvKey,
        string? ExperimentalBearerToken,
        bool HasCommandAuth,
        bool HasAwsAuth,
        bool IsBuiltInOpenAi,
        bool RequiresOpenAiAuth);

    private sealed record AuthFileCredential(
        string? Token,
        string? Source,
        string? Problem,
        string? AccountId);

    public static string ResolveCodexHome(
        string? explicitHome = null,
        Func<string, string?>? environmentVariable = null)
    {
        if (!string.IsNullOrWhiteSpace(explicitHome))
        {
            return explicitHome;
        }

        environmentVariable ??= Environment.GetEnvironmentVariable;
        string? aiResumeHome = ReadEnvironmentValue(environmentVariable, "AI_RESUME_CODEX_HOME");
        if (aiResumeHome is not null)
        {
            return aiResumeHome;
        }

        string? codexHome = ReadEnvironmentValue(environmentVariable, "CODEX_HOME");
        return codexHome ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex");
    }

    private static bool TryReadProviderConfig(
        string configPath,
        Func<string, string?> environmentVariable,
        out ProviderConfig config)
    {
        config = null!;
        if (!File.Exists(configPath))
        {
            config = BuiltInOpenAiConfig(model: null, baseUrl: null, environmentVariable);
            return true;
        }

        try
        {
            TomlTable root = TomlSerializer.Deserialize<TomlTable>(File.ReadAllText(configPath))
                ?? new TomlTable();
            TomlTable effective = root;
            string profileName = ReadTableString(root, "profile");
            if (profileName.Length > 0 &&
                root.TryGetValue("profiles", out object? rawProfiles) &&
                rawProfiles is TomlTable profiles &&
                profiles.TryGetValue(profileName, out object? rawProfile) &&
                rawProfile is TomlTable profile)
            {
                effective = profile;
            }

            string activeProvider = ReadTableString(effective, "model_provider");
            if (activeProvider.Length == 0)
            {
                activeProvider = ReadTableString(root, "model_provider");
            }

            if (activeProvider.Length == 0)
            {
                activeProvider = "openai";
            }

            string? model = ReadTableString(effective, "model") is { Length: > 0 } profileModel
                ? profileModel
                : ReadTableString(root, "model") is { Length: > 0 } rootModel ? rootModel : null;

            if (string.Equals(activeProvider, "openai", StringComparison.Ordinal))
            {
                string? builtInBaseUrl = ReadTableString(root, "openai_base_url") is { Length: > 0 } overrideUrl
                    ? overrideUrl
                    : null;
                config = BuiltInOpenAiConfig(model, builtInBaseUrl, environmentVariable);
                return true;
            }

            if (!root.TryGetValue("model_providers", out object? rawProviders) ||
                rawProviders is not TomlTable providers ||
                !providers.TryGetValue(activeProvider, out object? rawProvider) ||
                rawProvider is not TomlTable provider)
            {
                return false;
            }

            string baseUrl = ReadTableString(provider, "base_url");
            if (baseUrl.Length == 0)
            {
                return false;
            }

            if (!TryReadStringMap(provider, "query_params", out IReadOnlyDictionary<string, string> queryParameters) ||
                !TryReadStringMap(provider, "http_headers", out IReadOnlyDictionary<string, string> staticHeaders) ||
                !TryReadStringMap(provider, "env_http_headers", out IReadOnlyDictionary<string, string> envHeaders))
            {
                return false;
            }

            config = new ProviderConfig(
                activeProvider,
                baseUrl,
                model,
                ReadTableString(provider, "wire_api") is { Length: > 0 } wireApi ? wireApi : "responses",
                queryParameters,
                ResolveRequestHeaders(staticHeaders, envHeaders, environmentVariable),
                ReadTableString(provider, "env_key") is { Length: > 0 } envKey ? envKey : null,
                ReadTableString(provider, "experimental_bearer_token") is { Length: > 0 } bearer ? bearer : null,
                provider.TryGetValue("auth", out object? rawAuth) && rawAuth is TomlTable,
                provider.TryGetValue("aws", out object? rawAws) && rawAws is TomlTable,
                IsBuiltInOpenAi: false,
                RequiresOpenAiAuth: ReadTableBool(provider, "requires_openai_auth"));
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or TomlException)
        {
            return false;
        }
    }

    private static ProviderConfig BuiltInOpenAiConfig(
        string? model,
        string? baseUrl,
        Func<string, string?> environmentVariable) =>
        new(
            "openai",
            baseUrl ?? string.Empty,
            model,
            "responses",
            new Dictionary<string, string>(StringComparer.Ordinal),
            ResolveRequestHeaders(
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["OpenAI-Organization"] = "OPENAI_ORGANIZATION",
                    ["OpenAI-Project"] = "OPENAI_PROJECT",
                },
                environmentVariable),
            null,
            null,
            HasCommandAuth: false,
            HasAwsAuth: false,
            IsBuiltInOpenAi: true,
            RequiresOpenAiAuth: true);

    private static string ReadTableString(TomlTable table, string key) =>
        table.TryGetValue(key, out object? value) && value is string text ? text : string.Empty;

    private static bool ReadTableBool(TomlTable table, string key) =>
        table.TryGetValue(key, out object? value) && value is bool flag && flag;

    private static bool TryReadStringMap(
        TomlTable table,
        string key,
        out IReadOnlyDictionary<string, string> result)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        result = values;
        if (!table.TryGetValue(key, out object? raw))
        {
            return true;
        }

        if (raw is not TomlTable map)
        {
            return false;
        }

        foreach ((string name, object? value) in map)
        {
            if (value is not string text)
            {
                return false;
            }

            values[name] = text;
        }

        return true;
    }

    private static IReadOnlyDictionary<string, string> ResolveRequestHeaders(
        IReadOnlyDictionary<string, string> staticHeaders,
        IReadOnlyDictionary<string, string> environmentHeaders,
        Func<string, string?> environmentVariable)
    {
        var resolved = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach ((string name, string value) in staticHeaders)
        {
            if (IsSafeHeader(name, value))
            {
                resolved[name] = value;
            }
        }

        foreach ((string name, string variableName) in environmentHeaders)
        {
            string? value = ReadEnvironmentValue(environmentVariable, variableName);
            if (value is not null && IsSafeHeader(name, value))
            {
                resolved[name] = value;
            }
        }

        return resolved;
    }

    private static string? ReadEnvironmentValue(
        Func<string, string?> environmentVariable,
        string name)
    {
        try
        {
            string? value = environmentVariable(name);
            return string.IsNullOrWhiteSpace(value) ? null : value;
        }
        catch (Exception ex) when (ex is ArgumentException or System.Security.SecurityException)
        {
            return null;
        }
    }

    private static bool IsSafeHeader(string name, string value)
    {
        if (name.Length is 0 or > 256 || value.Length > 16_384)
        {
            return false;
        }

        foreach (char c in name)
        {
            bool token = char.IsAsciiLetterOrDigit(c) || c is '!' or '#' or '$' or '%' or '&' or '\'' or
                '*' or '+' or '-' or '.' or '^' or '_' or '`' or '|' or '~';
            if (!token)
            {
                return false;
            }
        }

        foreach (char c in value)
        {
            if ((char.IsControl(c) && c != '\t') || c == '\u007f')
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// 取顶层 <c>model = "…"</c>。最小推理探测要指定模型,而**必须用用户真正在跑的那个** ——
    /// 随便挑一个能跑的模型来证明"可以推理",证明的不是用户关心的那件事。
    /// 读不到返回 null,由调用方跳过推理探测并如实说明。
    /// </summary>
    public static string? TryReadModel(string configPath)
    {
        return TryReadProviderConfig(configPath, Environment.GetEnvironmentVariable, out ProviderConfig config)
            ? config.Model
            : null;
    }

    private static AuthFileCredential ReadAuthFileCredential(string authPath)
    {
        if (!File.Exists(authPath))
        {
            return new AuthFileCredential(null, "auth.json", "Codex auth.json 不存在或凭据保存在系统 keyring", null);
        }

        try
        {
            using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(authPath));
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                return new AuthFileCredential(null, "auth.json", "Codex auth.json 不是对象", null);
            }

            JsonElement root = doc.RootElement;
            string? authMode = ReadJsonString(root, "auth_mode");
            string? apiKey = ReadJsonString(doc.RootElement, "OPENAI_API_KEY");
            string? accessToken = null;
            string? accountId = null;
            if (root.TryGetProperty("tokens", out JsonElement tokens) &&
                tokens.ValueKind == JsonValueKind.Object)
            {
                accessToken = ReadJsonString(tokens, "access_token");
                accountId = ReadJsonString(tokens, "account_id");
            }

            string resolvedMode;
            if (authMode is not null)
            {
                resolvedMode = authMode;
            }
            else if (!string.IsNullOrWhiteSpace(ReadJsonString(root, "personal_access_token")))
            {
                resolvedMode = "personalAccessToken";
            }
            else if (root.TryGetProperty("bedrock_api_key", out JsonElement bedrock) &&
                     bedrock.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined))
            {
                resolvedMode = "bedrockApiKey";
            }
            else
            {
                resolvedMode = string.IsNullOrWhiteSpace(apiKey) ? "chatgpt" : "apikey";
            }

            return resolvedMode switch
            {
                "apikey" => new AuthFileCredential(
                    apiKey,
                    "auth.json:apikey",
                    string.IsNullOrWhiteSpace(apiKey) ? "auth_mode=apikey 但 OPENAI_API_KEY 为空" : null,
                    null),
                "chatgpt" or "chatgptAuthTokens" => new AuthFileCredential(
                    accessToken,
                    "auth.json:chatgpt",
                    string.IsNullOrWhiteSpace(accessToken) ? "auth_mode=chatgpt 但 tokens.access_token 为空" : null,
                    accountId),
                "headers" or "agentIdentity" or "personalAccessToken" or "bedrockApiKey" =>
                    new AuthFileCredential(
                        null,
                        "auth.json:" + resolvedMode,
                        $"Codex auth_mode={resolvedMode} 需要上游认证状态机,AI Resume 不自行复现",
                        null),
                _ => new AuthFileCredential(
                    null,
                    "auth.json",
                    $"Codex auth.json 含未知 auth_mode={resolvedMode}",
                    null),
            };
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return new AuthFileCredential(null, "auth.json", "Codex auth.json 无法读取或解析", null);
        }
    }

    private static string? ReadJsonString(JsonElement root, string property)
    {
        if (!root.TryGetProperty(property, out JsonElement value) || value.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        string? text = value.GetString();
        return string.IsNullOrWhiteSpace(text) ? null : text;
    }

    private static CodexProviderCredentials MissingProvider(string problem) =>
        new(
            null,
            null,
            null,
            null,
            new Dictionary<string, string>(StringComparer.Ordinal),
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
            null,
            problem,
            null,
            null,
            false,
            false);

    private static CodexProviderCredentials MissingCredential(
        ProviderConfig config,
        string? source,
        string problem) =>
        new(
            config.BaseUrl,
            null,
            config.Model,
            config.WireApi,
            config.QueryParameters,
            config.RequestHeaders,
            source,
            problem,
            null,
            config.ProviderId,
            config.IsBuiltInOpenAi,
            config.RequiresOpenAiAuth);

    private static CodexProviderCredentials CreateCredential(
        ProviderConfig config,
        string? token,
        string? source,
        string? missingProblem,
        string? accountId)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return MissingCredential(config, source, missingProblem ?? "Codex 凭据为空");
        }

        if (token.Length > 16_384 ||
            !AuthenticationHeaderValue.TryParse("Bearer " + token, out AuthenticationHeaderValue? parsed) ||
            !string.Equals(parsed.Scheme, "Bearer", StringComparison.OrdinalIgnoreCase))
        {
            return MissingCredential(config, source, "Codex 凭据含非法字符或长度异常");
        }

        string? safeAccountId = string.IsNullOrWhiteSpace(accountId)
            ? null
            : IsSafeHeaderValue(accountId, maxLength: 512, allowWhitespace: false) ? accountId : null;

        return new CodexProviderCredentials(
            config.BaseUrl,
            token,
            config.Model,
            config.WireApi,
            config.QueryParameters,
            config.RequestHeaders,
            source,
            null,
            safeAccountId,
            config.ProviderId,
            config.IsBuiltInOpenAi,
            config.RequiresOpenAiAuth);
    }

    private static bool IsSafeHeaderValue(string value, int maxLength, bool allowWhitespace)
    {
        if (value.Length == 0 || value.Length > maxLength)
        {
            return false;
        }

        foreach (char c in value)
        {
            if (char.IsControl(c) || (!allowWhitespace && char.IsWhiteSpace(c)))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>把 HTTP 状态码翻成结论。抽成纯函数是为了能不联网地断言映射。</summary>
    /// <summary>
    /// 认出 CDN 边缘拦截。
    ///
    /// Cloudflare 的 1xxx 拦截也走 403,但它拦的是**这个客户端**,不是**这把凭据**。
    /// 2026-08-13 用默认 UA 请求本机活动 provider 的 <c>/v1/usage</c>,录到的就是
    /// <c>{"status":403,"error_code":1010,"error_name":"browser_signature_banned",
    /// "cloudflare_error":true,"retryable":false,...}</c>;换成
    /// <see cref="BrowserUserAgent"/> 后同一把凭据立刻返回 200。
    /// 把它读成"凭据被拒"会让面板红着说一句能被证伪的假话。
    /// </summary>
    internal static bool LooksLikeCdnBlock(string? body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return false;
        }

        if (TryDetectCdnBlockJson(body, out bool blocked))
        {
            return blocked;
        }

        // 非 JSON(Cloudflare 的默认拦截页是 HTML)时必须两个标记同时出现:
        // 单独一个 "cloudflare" 或单独一个四位数,都可能出现在正常的业务错误文案里。
        return body.Contains("cloudflare", StringComparison.OrdinalIgnoreCase) &&
               CloudflareErrorCodePattern().IsMatch(body);
    }

    /// <summary>返回值表示"这段 body 是不是可解析 JSON(因而结论权威)",结论走 <paramref name="blocked"/>。</summary>
    private static bool TryDetectCdnBlockJson(string body, out bool blocked)
    {
        blocked = false;
        try
        {
            using JsonDocument doc = JsonDocument.Parse(body);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            JsonElement root = doc.RootElement;
            if (root.TryGetProperty("cloudflare_error", out JsonElement flag) &&
                flag.ValueKind == JsonValueKind.True)
            {
                blocked = true;
                return true;
            }

            // Cloudflare 的 1xxx 全族都是"边缘把客户端挡了"(1010 UA 封禁、1015 限速、
            // 1020 防火墙规则),没有一个是在说这把凭据不对。
            if (root.TryGetProperty("error_code", out JsonElement code) &&
                code.ValueKind == JsonValueKind.Number &&
                code.TryGetInt32(out int value) &&
                value is >= 1000 and <= 1999)
            {
                blocked = true;
                return true;
            }

            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    [GeneratedRegex(@"error\s*1\d{3}", RegexOptions.IgnoreCase)]
    private static partial Regex CloudflareErrorCodePattern();

    private static CodexAuthResult CdnBlocked(string what, bool usedCredential) =>
        new(CodexAuthOutcome.NetworkFailed,
            $"{what}被 CDN 边缘拦截(HTTP 403),不是凭据问题,可用性未核实",
            "cdn-blocked",
            usedCredential);

    public static CodexAuthResult Classify(
        HttpStatusCode status, bool usedCredential = true, string? body = null)
    {
        int code = (int)status;
        if (code is >= 200 and < 300)
        {
            return new CodexAuthResult(
                CodexAuthOutcome.Authorized,
                usedCredential ? "凭据有效" : "端点无需凭据且可访问",
                UsedCredential: usedCredential);
        }

        if (code is 401 or 403)
        {
            if (code == 403 && LooksLikeCdnBlock(body))
            {
                return CdnBlocked("列模型请求", usedCredential);
            }

            return new CodexAuthResult(
                CodexAuthOutcome.Rejected,
                usedCredential ? $"凭据被拒(HTTP {code})" : $"端点要求凭据(HTTP {code})",
                UsedCredential: usedCredential);
        }

        if (code is 402 or 429)
        {
            return new CodexAuthResult(
                CodexAuthOutcome.Limited,
                code == 402 ? "余额不足或需充值(HTTP 402)" : "被限流(HTTP 429)",
                "http-" + code,
                usedCredential);
        }

        if (code >= 500)
        {
            return new CodexAuthResult(
                CodexAuthOutcome.ServerError, $"服务端异常(HTTP {code})", UsedCredential: usedCredential);
        }

        // 4xx 里其余的既不是"授权没问题"也不是"授权被拒",不硬塞进任何一档。
        return new CodexAuthResult(
            CodexAuthOutcome.NetworkFailed, $"意外状态(HTTP {code})", UsedCredential: usedCredential);
    }

    /// <summary>
    /// 把最小推理请求的状态码翻成结论。**前提是 /models 已经 200** ——
    /// 也就是说凭据已被服务端接受,这一步只回答"接受之后允不允许干活"。
    ///
    /// 400/404/422 单独一档:那是**端点形状不支持**,不是权限问题。
    /// sub2api 各家路由不一,把"这家不认识 responses"读成"你没有推理权限",
    /// 会让一个好好的配置被标红 —— 误判和漏判一样是在骗人。
    /// </summary>
    public static CodexAuthResult ClassifyInference(
        HttpStatusCode status, bool usedCredential = true, string? body = null)
    {
        int code = (int)status;
        if (code is >= 200 and < 300)
        {
            return new CodexAuthResult(
                CodexAuthOutcome.Authorized,
                usedCredential ? "可用 · 凭据与推理已验证" : "可用 · 无需凭据且推理已验证",
                UsedCredential: usedCredential);
        }

        if (code is 401 or 403)
        {
            if (code == 403 && LooksLikeCdnBlock(body))
            {
                return CdnBlocked("推理请求", usedCredential);
            }

            return new CodexAuthResult(
                CodexAuthOutcome.NoInference,
                usedCredential
                    ? $"只能列模型,不允许推理(HTTP {code})—— 凭据本身被接受,但跑不了任务"
                    : $"只能列模型,推理路由要求凭据或拒绝访问(HTTP {code})",
                UsedCredential: usedCredential);
        }

        if (code is 402 or 429)
        {
            return new CodexAuthResult(
                CodexAuthOutcome.Limited,
                code == 402 ? "余额不足或需充值(HTTP 402)" : "被限流(HTTP 429)",
                "http-" + code,
                usedCredential);
        }

        if (code >= 500)
        {
            return new CodexAuthResult(
                CodexAuthOutcome.ServerError, $"服务端异常(HTTP {code})", UsedCredential: usedCredential);
        }

        // 端点不支持这种最小探测:凭据已经通过，但推理没有得到成功证据。
        return new CodexAuthResult(
            CodexAuthOutcome.InferenceUnverified,
            usedCredential
                ? $"凭据已验证 · 端点未接受最小推理探测(HTTP {code}),推理权限未核实"
                : $"端点可访问 · 未接受最小推理探测(HTTP {code}),推理能力未核实",
            UsedCredential: usedCredential);
    }

    /// <summary>
    /// Responses 网关可能在 HTTP 2xx 内返回 <c>status:"failed"</c>、
    /// <c>status:"cancelled"</c> 或 error envelope。只有 JSON 终态能证明
    /// 最小推理成功；空响应、HTML、损坏 JSON 和非终态都不能点绿。
    /// </summary>
    public static CodexAuthResult ClassifyInferenceResponse(
        HttpStatusCode status,
        string? responseBody,
        bool usedCredential = true)
    {
        if ((int)status is < 200 or >= 300)
        {
            return ClassifyInference(status, usedCredential);
        }

        if (string.IsNullOrWhiteSpace(responseBody))
        {
            return InferenceBodyUnverified("推理响应为空,推理权限未核实", usedCredential);
        }

        try
        {
            using JsonDocument doc = JsonDocument.Parse(responseBody);
            JsonElement root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return InferenceBodyUnverified("推理响应不是 JSON 对象,推理权限未核实", usedCredential);
            }

            if (root.TryGetProperty("error", out JsonElement error) && error.ValueKind != JsonValueKind.Null)
            {
                return new CodexAuthResult(
                    CodexAuthOutcome.NoInference,
                    "推理路由在成功状态码内返回错误,当前不能证明可运行任务",
                    "semantic-error",
                    usedCredential);
            }

            bool hasStatus = root.TryGetProperty("status", out JsonElement statusValue);
            string? terminalStatus = hasStatus && statusValue.ValueKind == JsonValueKind.String
                ? statusValue.GetString()
                : null;
            if (string.Equals(terminalStatus, "failed", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(terminalStatus, "cancelled", StringComparison.OrdinalIgnoreCase))
            {
                return new CodexAuthResult(
                    CodexAuthOutcome.NoInference,
                    "推理路由返回失败终态,当前不能运行任务",
                    "semantic-" + terminalStatus!.ToLowerInvariant(),
                    usedCredential);
            }

            bool hasOutput = root.TryGetProperty("output", out JsonElement output) &&
                             output.ValueKind == JsonValueKind.Array;
            if (string.Equals(terminalStatus, "completed", StringComparison.OrdinalIgnoreCase))
            {
                return hasOutput
                    ? InferenceAuthorized(usedCredential)
                    : InferenceBodyUnverified("推理完成响应缺少 output,推理权限未核实", usedCredential);
            }

            if (string.Equals(terminalStatus, "incomplete", StringComparison.OrdinalIgnoreCase))
            {
                string? reason = root.TryGetProperty("incomplete_details", out JsonElement details) &&
                                 details.ValueKind == JsonValueKind.Object &&
                                 details.TryGetProperty("reason", out JsonElement reasonValue) &&
                                 reasonValue.ValueKind == JsonValueKind.String
                    ? reasonValue.GetString()
                    : null;
                if (hasOutput && reason is "max_output_tokens" or "max_tokens")
                {
                    return InferenceAuthorized(usedCredential, outputLimited: true);
                }

                return InferenceBodyUnverified("推理响应未完整结束,推理权限未核实", usedCredential);
            }

            // 少数兼容网关在 HTTP 200 下完全省略 status，但仍返回标准 response envelope。
            // 只要 status 字段存在（包括 queued/in_progress/未知值/null/错误类型），
            // 就不能走这个兼容分支；HTTP 202 也只证明请求被接受，不证明已经完成。
            bool responseEnvelope = status == HttpStatusCode.OK &&
                                    !hasStatus &&
                                    hasOutput &&
                                    ((root.TryGetProperty("object", out JsonElement objectValue) &&
                                      objectValue.ValueKind == JsonValueKind.String &&
                                      string.Equals(objectValue.GetString(), "response", StringComparison.OrdinalIgnoreCase)) ||
                                     (root.TryGetProperty("id", out JsonElement idValue) &&
                                      idValue.ValueKind == JsonValueKind.String &&
                                      !string.IsNullOrWhiteSpace(idValue.GetString())));
            return responseEnvelope
                ? InferenceAuthorized(usedCredential)
                : InferenceBodyUnverified("推理响应缺少可验证终态,推理权限未核实", usedCredential);
        }
        catch (JsonException)
        {
            return InferenceBodyUnverified("推理响应无法解析,推理权限未核实", usedCredential);
        }
    }

    public static Task<CodexAuthResult> ProbeAsync(
        string? codexHome = null, HttpMessageHandler? handler = null, CancellationToken ct = default) =>
        ProbeAsync(codexHome, handler, Environment.GetEnvironmentVariable, verifyInference: true, ct);

    public static Task<CodexAuthResult> ProbeAsync(
        CodexProviderCredentials provider,
        HttpMessageHandler? handler = null,
        CancellationToken ct = default) =>
        ProbeAsync(provider, handler, verifyInference: true, ct);

    public static Task<CodexAuthResult> ProbeAsync(
        string? codexHome,
        HttpMessageHandler? handler,
        Func<string, string?> environmentVariable,
        CancellationToken ct = default) =>
        ProbeAsync(codexHome, handler, environmentVariable, verifyInference: true, ct);

    public static Task<CodexAuthResult> ProbeModelsAsync(
        string? codexHome = null, HttpMessageHandler? handler = null, CancellationToken ct = default) =>
        ProbeAsync(codexHome, handler, Environment.GetEnvironmentVariable, verifyInference: false, ct);

    public static Task<CodexAuthResult> ProbeModelsAsync(
        CodexProviderCredentials provider,
        HttpMessageHandler? handler = null,
        CancellationToken ct = default) =>
        ProbeAsync(provider, handler, verifyInference: false, ct);

    public static Task<CodexAuthResult> ProbeModelsAsync(
        string? codexHome,
        HttpMessageHandler? handler,
        Func<string, string?> environmentVariable,
        CancellationToken ct = default) =>
        ProbeAsync(codexHome, handler, environmentVariable, verifyInference: false, ct);

    private static async Task<CodexAuthResult> ProbeAsync(
        string? codexHome,
        HttpMessageHandler? handler,
        Func<string, string?> environmentVariable,
        bool verifyInference,
        CancellationToken ct)
    {
        CodexProviderCredentials provider = ReadActiveProviderCredentials(codexHome, environmentVariable);
        return await ProbeAsync(provider, handler, verifyInference, ct).ConfigureAwait(false);
    }

    private static async Task<CodexAuthResult> ProbeAsync(
        CodexProviderCredentials provider,
        HttpMessageHandler? handler,
        bool verifyInference,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(provider.BaseUrl) ||
            (provider.RequiresOpenAiAuth && string.IsNullOrWhiteSpace(provider.BearerToken)) ||
            (!string.IsNullOrWhiteSpace(provider.CredentialSource) &&
             provider.CredentialSource is not "none" &&
             string.IsNullOrWhiteSpace(provider.BearerToken)))
        {
            return new CodexAuthResult(
                CodexAuthOutcome.NotConfigured, provider.Problem ?? "读不到 Codex 的 base_url 或凭据");
        }

        using HttpClient client = CreateHttpClient(handler);
        client.Timeout = TimeSpan.FromSeconds(TimeoutSeconds);

        try
        {
            using var request = new HttpRequestMessage(
                HttpMethod.Get,
                BuildApiUrl(provider.BaseUrl, "models", provider.QueryParameters));
            AddRequestHeaders(request, provider);

            // 只要响应头就够判定;不读 body,避免把模型列表(以及任何回显)拉进内存。
            using HttpResponseMessage response = await client
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct)
                .ConfigureAwait(false);

            bool usedCredential = UsesConfiguredCredential(provider);

            // 只有 403 才值得读一眼 body:要把 CDN 边缘拦截和"凭据被拒"分开。
            // 其余状态维持原来的只读响应头,不把模型列表拉进内存。
            string? blockBody = response.StatusCode == HttpStatusCode.Forbidden
                ? await ReadLimitedBodyAsync(response.Content, MaxBlockBodyBytes, ct).ConfigureAwait(false)
                : null;
            CodexAuthResult models = Classify(response.StatusCode, usedCredential, blockBody);

            // 列模型没过就到此为止:后面那一步问的是"接受之后允不允许干活",
            // 前提不成立时问它没有意义,还白费一次请求。
            if (models.Outcome != CodexAuthOutcome.Authorized)
            {
                return models;
            }

            if (!verifyInference)
            {
                return new CodexAuthResult(
                    CodexAuthOutcome.InferenceUnverified,
                    usedCredential ? "凭据已验证 · 推理权限未核实" : "端点无需凭据 · 推理能力未核实",
                    UsedCredential: usedCredential);
            }

            // **列得出模型 ≠ 跑得动模型。** 这一步是审计 A6 补上的:
            // 列模型往往是低权限路由,真正决定"能不能干活"的是推理路由。
            return await ProbeInferenceAsync(
                    client, provider, ct)
                .ConfigureAwait(false);
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            return new CodexAuthResult(CodexAuthOutcome.NetworkFailed, "请求超时");
        }
        catch (HttpRequestException)
        {
            // DNS/TCP/TLS 失败是**本地失败**,不是授权失败 —— 两者混为一谈,
            // 断网时面板会红着说"认证被拒",把人往完全错误的方向带。
            return new CodexAuthResult(CodexAuthOutcome.NetworkFailed, "网络不可达");
        }
        catch (UriFormatException)
        {
            return new CodexAuthResult(CodexAuthOutcome.NotConfigured, "Codex base_url 格式无效");
        }
    }

    /// <summary>
    /// 最小推理探测:<c>max_output_tokens = 1</c>、一个字符的提示词。
    ///
    /// 这一步只在用户主动刷新时运行。相比此前的 <c>codex exec</c>,它只消耗个位数 token,
    /// 但仍属于真实推理请求,不能放进开窗、定时刷新或过期补刷链路。
    /// </summary>
    private static async Task<CodexAuthResult> ProbeInferenceAsync(
        HttpClient client, CodexProviderCredentials provider, CancellationToken ct)
    {
        bool usedCredential = UsesConfiguredCredential(provider);
        if (string.IsNullOrWhiteSpace(provider.Model))
        {
            // 不知道该用哪个模型就别猜一个。说清"验到哪一步"比给个漂亮结论重要。
            return new CodexAuthResult(
                CodexAuthOutcome.InferenceUnverified,
                !usedCredential
                    ? "端点可访问 · 配置里没写 model,推理能力未核实"
                    : "凭据已验证 · 配置里没写 model,推理权限未核实",
                UsedCredential: usedCredential);
        }

        if (!string.Equals(provider.WireApi, "responses", StringComparison.Ordinal))
        {
            return new CodexAuthResult(
                CodexAuthOutcome.InferenceUnverified,
                !usedCredential
                    ? $"端点可访问 · 不支持 wire_api={provider.WireApi},推理能力未核实"
                    : $"凭据已验证 · 不支持 wire_api={provider.WireApi},推理权限未核实",
                UsedCredential: usedCredential);
        }

        using var body = new StringContent(
            JsonSerializer.Serialize(new
            {
                model = provider.Model,
                max_output_tokens = 1,
                input = "1",
            }),
            System.Text.Encoding.UTF8,
            "application/json");
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(TimeoutSeconds));
        CancellationToken requestToken = timeoutCts.Token;

        try
        {
            using var request = new HttpRequestMessage(
                HttpMethod.Post,
                BuildApiUrl(provider.BaseUrl!, "responses", provider.QueryParameters)) { Content = body };
            AddRequestHeaders(request, provider);

            using HttpResponseMessage response = await client
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, requestToken)
                .ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                string? blockBody = response.StatusCode == HttpStatusCode.Forbidden
                    ? await ReadLimitedBodyAsync(response.Content, MaxBlockBodyBytes, requestToken)
                        .ConfigureAwait(false)
                    : null;
                return ClassifyInference(response.StatusCode, usedCredential, blockBody);
            }

            if (response.Content.Headers.ContentLength is > MaxInferenceResponseBytes)
            {
                return InferenceBodyUnverified("推理响应过大,推理权限未核实", usedCredential);
            }

            string? responseBody = await ReadLimitedBodyAsync(
                    response.Content,
                    MaxInferenceResponseBytes,
                    requestToken)
                .ConfigureAwait(false);
            return responseBody is null
                ? InferenceBodyUnverified("推理响应过大,推理权限未核实", usedCredential)
                : ClassifyInferenceResponse(response.StatusCode, responseBody, usedCredential);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // 凭据这一关已经过了,只是推理那一步没走完 —— 不能因此把凭据判成坏的。
            return new CodexAuthResult(
                CodexAuthOutcome.InferenceUnverified,
                !usedCredential
                    ? "端点可访问 · 推理探测超时,推理能力未核实"
                    : "凭据已验证 · 推理探测超时,推理权限未核实",
                UsedCredential: usedCredential);
        }
        catch (HttpRequestException)
        {
            // 措辞与其它降级分支保持一致:凡是没验到推理的,都必须出现"未核实"。
            // 说法不统一等于让用户逐句去猜哪句代表"验过了"。
            return new CodexAuthResult(
                CodexAuthOutcome.InferenceUnverified,
                !usedCredential
                    ? "端点可访问 · 推理网络不可达,推理能力未核实"
                    : "凭据已验证 · 网络不可达,推理权限未核实",
                UsedCredential: usedCredential);
        }
        catch (IOException)
        {
            return new CodexAuthResult(
                CodexAuthOutcome.InferenceUnverified,
                !usedCredential
                    ? "端点可访问 · 推理响应读取失败,推理能力未核实"
                    : "凭据已验证 · 推理响应读取失败,推理权限未核实",
                UsedCredential: usedCredential);
        }
        catch (UriFormatException)
        {
            return new CodexAuthResult(
                CodexAuthOutcome.InferenceUnverified,
                !usedCredential
                    ? "端点可访问 · Codex base_url 格式无效,推理能力未核实"
                    : "凭据已验证 · Codex base_url 格式无效,推理权限未核实",
                UsedCredential: usedCredential);
        }
    }

    public static string BuildApiUrl(string baseUrl, string relativePath) =>
        BuildApiUrlCore(baseUrl, relativePath, queryParameters: null, ensureV1: false);

    public static string BuildApiUrl(
        string baseUrl,
        string relativePath,
        IReadOnlyDictionary<string, string>? queryParameters) =>
        BuildApiUrlCore(baseUrl, relativePath, queryParameters, ensureV1: false);

    internal static string BuildV1ApiUrl(string baseUrl, string relativePath) =>
        BuildApiUrlCore(baseUrl, relativePath, queryParameters: null, ensureV1: true);

    internal static string BuildV1ApiUrl(
        string baseUrl,
        string relativePath,
        IReadOnlyDictionary<string, string>? queryParameters) =>
        BuildApiUrlCore(baseUrl, relativePath, queryParameters, ensureV1: true);

    private static string BuildApiUrlCore(
        string baseUrl,
        string relativePath,
        IReadOnlyDictionary<string, string>? queryParameters,
        bool ensureV1)
    {
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out Uri? uri) ||
            uri.Scheme is not ("http" or "https") ||
            !string.IsNullOrEmpty(uri.UserInfo) ||
            !string.IsNullOrEmpty(uri.Query) ||
            !string.IsNullOrEmpty(uri.Fragment) ||
            (uri.Scheme == Uri.UriSchemeHttp && !uri.IsLoopback))
        {
            throw new UriFormatException("Codex base_url 必须是 HTTPS 或 loopback HTTP,且不能含 userinfo/query/fragment");
        }

        var builder = new UriBuilder(uri);
        string path = builder.Path.TrimEnd('/');
        string cleanPath = relativePath.TrimStart('/');
        if (ensureV1 && !path.EndsWith("/v1", StringComparison.OrdinalIgnoreCase))
        {
            path += "/v1";
        }

        builder.Path = path + "/" + cleanPath;
        string result = builder.Uri.AbsoluteUri;
        if (queryParameters is { Count: > 0 })
        {
            result += "?" + string.Join("&", queryParameters.Select(pair =>
                Uri.EscapeDataString(pair.Key) + "=" + Uri.EscapeDataString(pair.Value)));
        }

        return result;
    }

    private static CodexAuthResult InferenceAuthorized(bool usedCredential, bool outputLimited = false) =>
        new(
            CodexAuthOutcome.Authorized,
            usedCredential
                ? outputLimited
                    ? "可用 · 凭据与推理已验证(探测输出达到 1 token 上限)"
                    : "可用 · 凭据与推理已验证"
                : outputLimited
                    ? "可用 · 无需凭据且推理已验证(探测输出达到 1 token 上限)"
                    : "可用 · 无需凭据且推理已验证",
            UsedCredential: usedCredential);

    private static CodexAuthResult InferenceBodyUnverified(string detail, bool usedCredential) =>
        new(
            CodexAuthOutcome.InferenceUnverified,
            usedCredential ? "凭据已验证 · " + detail : "端点可访问 · " + detail,
            UsedCredential: usedCredential);

    private static async Task<string?> ReadLimitedBodyAsync(
        HttpContent content,
        int maxBytes,
        CancellationToken ct)
    {
        await using Stream stream = await content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        byte[] chunk = new byte[8192];
        while (true)
        {
            int read = await stream.ReadAsync(chunk.AsMemory(0, chunk.Length), ct).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            if (buffer.Length + read > maxBytes)
            {
                return null;
            }

            buffer.Write(chunk, 0, read);
        }

        return Encoding.UTF8.GetString(buffer.GetBuffer(), 0, checked((int)buffer.Length));
    }

    private static void AppendIdentityField(StringBuilder builder, string name, string? value)
    {
        string safeValue = value ?? string.Empty;
        builder.Append(name.Length)
            .Append(':')
            .Append(name)
            .Append('=')
            .Append(safeValue.Length)
            .Append(':')
            .Append(safeValue)
            .Append(';');
    }

    private static HttpClient CreateHttpClient(HttpMessageHandler? handler)
    {
        if (handler is not null)
        {
            return new HttpClient(handler, disposeHandler: false);
        }

        return new HttpClient(new HttpClientHandler { AllowAutoRedirect = false });
    }

    internal static void AddRequestHeaders(HttpRequestMessage request, CodexProviderCredentials provider)
    {
        foreach ((string name, string value) in provider.RequestHeaders)
        {
            if (!request.Headers.TryAddWithoutValidation(name, value) && request.Content is not null)
            {
                request.Content.Headers.Remove(name);
                request.Content.Headers.TryAddWithoutValidation(name, value);
            }
        }

        if (!string.IsNullOrWhiteSpace(provider.BearerToken))
        {
            request.Headers.Remove("Authorization");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", provider.BearerToken);
        }
        request.Headers.Remove("User-Agent");
        request.Headers.TryAddWithoutValidation("User-Agent", BrowserUserAgent);
        request.Headers.Remove("Accept");
        request.Headers.TryAddWithoutValidation("Accept", "application/json");
        request.Headers.Remove("ChatGPT-Account-ID");
        if (!string.IsNullOrWhiteSpace(provider.AccountId))
        {
            request.Headers.TryAddWithoutValidation("ChatGPT-Account-ID", provider.AccountId);
        }
    }

    internal static bool UsesConfiguredCredential(CodexProviderCredentials provider)
    {
        if (!string.IsNullOrWhiteSpace(provider.BearerToken))
        {
            return true;
        }

        return provider.RequestHeaders.Keys.Any(name =>
            name.Equals("Authorization", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("Proxy-Authorization", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("Api-Key", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("X-Api-Key", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("X-Auth-Token", StringComparison.OrdinalIgnoreCase));
    }
}
