using System.Text.Json;
using AiResume.Core;
using AiResume.Worker.Probes;
using Tomlyn;
using Tomlyn.Model;

namespace AiResume.Worker.Resume;

public sealed record ResumeExecutionPlan(AgentCommand Command, IReadOnlyList<string> Arguments,
    IReadOnlyDictionary<string, string?> Environment, string Protocol, IDisposable? ConfigurationLease = null) : IDisposable
{
    public void Dispose() => ConfigurationLease?.Dispose();
}

/// <summary>布防只核验执行条件，不检查会话占用;启动前才要求会话空闲、令牌够用。</summary>
public enum ExecutionPurpose
{
    Arm,
    Spawn,
}

/// <summary>
/// 暂时不能启动、但不是执行失败：会话在客户端打开、同目录另一会话在跑、登录令牌即将过期。
/// 不产生 RunId,不锁周期，下一拍重新评估。
/// </summary>
public sealed class ResumeDeferredException(string reason, string message) : InvalidOperationException(message)
{
    /// <summary>session-in-use / cwd-busy / session-unverified / token-expiring。</summary>
    public string Reason { get; } = reason;
}

public interface IProviderExecutionPlan
{
    ResumeExecutionPlan Create(ProjectRef project, ProductConfig config, ExecutionPurpose purpose = ExecutionPurpose.Spawn);
    bool IsCurrent(ResumeTarget target);
}

public sealed class ProviderExecutionPlan : IProviderExecutionPlan
{
    private readonly ProviderRegistry _registry;
    private readonly ResumeSessionCatalog _sessions;
    private readonly Func<string, AgentCommand?> _command;
    private readonly Func<string, string, IEnumerable<string>> _configurationPaths;
    private readonly string _codexHome;
    private readonly Func<bool> _codexWriterLockVerified;
    private readonly Func<DateTimeOffset> _clock;

    /// <summary>
    /// 续跑子进程拿到的是启动那一刻的 access token(环境变量里没有 refresh token,子进程无法自行续期)。
    /// 剩余寿命不足这个值就先不启动，等 Claude Code 自己刷新：一次续跑至少能跑这么久而不中途 401。
    /// </summary>
    public static readonly TimeSpan MinimumTokenLifetime = TimeSpan.FromMinutes(45);

    public ProviderExecutionPlan(ProviderRegistry registry, ResumeSessionCatalog? sessions = null,
        Func<string, AgentCommand?>? command = null,
        Func<string, string, IEnumerable<string>>? configurationPaths = null, string? codexHome = null,
        Func<bool>? codexWriterLockVerified = null, Func<DateTimeOffset>? clock = null)
    {
        _registry = registry;
        _sessions = sessions ?? new ResumeSessionCatalog();
        _command = command ?? AgentCommandLocator.Find;
        _configurationPaths = configurationPaths ?? ExecutionConfigLease.Paths;
        _codexHome = codexHome ?? CodexAuthProbe.ResolveCodexHome();
        _codexWriterLockVerified = codexWriterLockVerified ?? (() => AgentCommandLocator.CodexVersion() == "0.148.0");
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    public ResumeExecutionPlan Create(ProjectRef project, ProductConfig config, ExecutionPurpose purpose = ExecutionPurpose.Spawn)
    {
        string agent = config.ResumeTarget?.Agent ?? "claudecode";
        var lease = ExecutionConfigLease.Acquire(_configurationPaths(agent, project.Path).Where(path =>
            !(agent == "claudecode" && Path.GetFileName(path) is ".claude.json" or ".config.json")));
        try
        {
            ResumeExecutionPlan plan = CreateCore(project, config, lease, purpose);
            if (!lease.IsCurrent) throw new InvalidOperationException("配置在验证期间发生变化，请重新布防");
            return plan with { ConfigurationLease = lease };
        }
        catch { lease.Dispose(); throw; }
    }

    private ResumeExecutionPlan CreateCore(ProjectRef project, ProductConfig config, ExecutionConfigLease lease, ExecutionPurpose purpose)
    {
        ResumeTarget target = config.ResumeTarget ?? throw new InvalidOperationException("需要重新选择供应商并布防");
        ProviderConnection? connection = _registry.Find(target.ProviderId);
        if (!target.IsValid || target.ModelId != config.ResumeModel ||
            connection?.Identity != target.ProviderIdentity || connection.Agent != target.Agent)
            throw new InvalidOperationException("供应商或账号已改变，需要重新布防");
        string model = ResumeTarget.EffectiveModel(project, target);
        if (!ProviderModels.IsSafeId(model)) throw new InvalidOperationException("项目的续跑模型无效，请重新布防");
        if (!_sessions.Matches(target.Agent, project)) throw new InvalidOperationException("原生会话归属无法核实");
        AgentCommand command = _command(target.Agent) ?? throw new InvalidOperationException(target.Agent == "codex" ? "找不到 Codex 程序" : "找不到 Claude Code 程序");
        var arguments = new List<string>(command.Prefix);
        var environment = new Dictionary<string, string?> { ["AI_RESUME_INTERNAL_RUN"] = "1" };
        if (target.Agent == "claudecode")
        {
            if (purpose == ExecutionPurpose.Spawn)
            {
                SessionOccupancy occupancy = _sessions.Occupancy(project);
                if (occupancy.State != "idle")
                    throw occupancy.State switch
                    {
                        "in-use" => new ResumeDeferredException("session-in-use", "原生 Claude 会话正在客户端打开"),
                        "cwd-busy" => new ResumeDeferredException("cwd-busy", "同目录另一个 Claude 会话正在运行"),
                        _ => new ResumeDeferredException("session-unverified", "原生 Claude 会话占用状态未核实"),
                    };
                // 到期时间与要注入的令牌出自同一次读取(connection),不另读文件:两次读之间 Claude Code 可能刚换了令牌。
                if (connection.Kind == "claude-oauth" &&
                    (connection.AccessTokenExpiresAtUtc is not { } expiry ||
                     expiry - _clock() < MinimumTokenLifetime))
                    throw new ResumeDeferredException("token-expiring", "Claude 登录令牌即将过期，等 Claude Code 自动续期后再续跑");
            }
            ClaudeHostProvider.Validate(command, project.Path);
            environment[ClaudeHostProvider.Flag] = "1";
            environment["ANTHROPIC_BASE_URL"] = connection.Credentials.BaseUrl;
            environment["ANTHROPIC_API_KEY"] = connection.Credentials.CredentialSource == "anthropic-key" ? connection.Credentials.BearerToken : string.Empty;
            environment["ANTHROPIC_AUTH_TOKEN"] = connection.Kind == "claude-oauth" || connection.Credentials.CredentialSource == "anthropic-key"
                ? string.Empty : connection.Credentials.BearerToken;
            environment["CLAUDE_CODE_OAUTH_TOKEN"] = connection.Kind == "claude-oauth" ? connection.Credentials.BearerToken : string.Empty;
            arguments.AddRange(new[] { "--resume", project.SessionId, "-p", config.ResumePrompt,
                "--model", model, "--output-format", "stream-json", "--verbose" });
            if (config.SkipPermissions) arguments.Add("--dangerously-skip-permissions");
        }
        else
        {
            environment["CODEX_HOME"] = _codexHome;
            if (!_codexWriterLockVerified()) throw new InvalidOperationException("当前 Codex 的原生会话独占锁尚未核实，请使用原客户端续跑");
            CodexProviderCredentials credentials = connection.Credentials;
            if (credentials.QueryParameters.Count > 0 || credentials.WireApi != "responses")
                throw new InvalidOperationException("当前供应商协议或查询参数无法固定，请在客户端调整后重新布防");
            if (!lease.Holds(Path.Combine(_codexHome, "managed_config.toml")))
                throw new InvalidOperationException("Codex 管理配置的存在性无法锁定，当前只读取模型与额度，请使用原客户端续跑");
            void Override(string key, string value) => arguments.AddRange(new[] { "-c", key + "=" + value });
            if (credentials.IsBuiltInOpenAi)
            {
                // 官方连接:固定内置 openai provider,并把 openai_base_url 钉到该认证方式的官方默认端点
                // (固定上游 0.148.0:base_url 为空时 ChatGPT 登录走 chatgpt.com/backend-api/codex,API key 走 api.openai.com/v1;
                // 用户 config.toml 若被本机桥接改写了 openai_base_url,这里显式覆盖才是真正的官方)。
                // 凭据由 Codex 自己从 auth.json 读取,不经命令行、环境或任何 AI Resume 文件。
                if (credentials.CredentialSource is not ("auth.json:chatgpt" or "auth.json:apikey") ||
                    string.IsNullOrWhiteSpace(credentials.BaseUrl))
                    throw new InvalidOperationException("Codex 未登录或登录方式不受支持，无法以官方供应商续跑");
                ValidateCodexLayers("openai", project.Path);
                Override("model_provider", "\"openai\"");
                Override("openai_base_url", JsonSerializer.Serialize(credentials.BaseUrl));
                // 本机桥接类工具常用 OPENAI_BASE_URL 环境变量把官方端点改到 127.0.0.1;子进程继承 Worker 环境,
                // 这里把它钉成同一个官方端点,配置与环境两条路径给出的都是官方地址。
                environment["OPENAI_BASE_URL"] = credentials.BaseUrl;
            }
            else
            {
                // 第三方静态 provider:使用本次内存连接固定原 provider，凭据只通过环境传递。
                // 命令行覆盖优先于 profile 和项目配置，保留其沙箱、审批、hooks 等约束。
                string provider = credentials.ProviderId ?? throw new InvalidOperationException("供应商 ID 未核实");
                if (provider is "openai" or "amazon-bedrock" or "amazon-bedrock-streaming" ||
                    !provider.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-'))
                    throw new InvalidOperationException("当前供应商连接无法固定，额度读取仍可使用");
                ValidateCodexLayers(provider, project.Path);
                Override("model_provider", JsonSerializer.Serialize(provider));
                Override("model_providers." + provider + ".name", JsonSerializer.Serialize(connection.Name));
                Override("model_providers." + provider + ".base_url", JsonSerializer.Serialize(credentials.BaseUrl));
                Override("model_providers." + provider + ".wire_api", "\"responses\"");
                Override("model_providers." + provider + ".requires_openai_auth", "false");
                if (!string.IsNullOrEmpty(credentials.BearerToken))
                {
                    Override("model_providers." + provider + ".env_key", "\"AI_RESUME_PROVIDER_KEY\"");
                    environment["AI_RESUME_PROVIDER_KEY"] = credentials.BearerToken;
                }
                if (credentials.RequestHeaders.Keys.Distinct(StringComparer.OrdinalIgnoreCase).Count() != credentials.RequestHeaders.Count ||
                    credentials.RequestHeaders.Values.Any(string.IsNullOrWhiteSpace))
                    throw new InvalidOperationException("供应商请求头为空或存在大小写冲突，无法固定");
                var headerVariables = new List<string>();
                int index = 0;
                foreach (var header in credentials.RequestHeaders)
                {
                    string variable = "AI_RESUME_HEADER_" + index++;
                    environment[variable] = header.Value;
                    headerVariables.Add(JsonSerializer.Serialize(header.Key) + "=" + JsonSerializer.Serialize(variable));
                }
                Override("model_providers." + provider + ".env_http_headers", "{" + string.Join(",", headerVariables) + "}");
            }
            arguments.AddRange(new[] { "exec", "resume", project.SessionId, config.ResumePrompt,
                "--model", model, "--json", "--skip-git-repo-check" });
        }
        return new(command, arguments, environment, target.Agent);
    }

    public bool IsCurrent(ResumeTarget target) => _registry.Find(target.ProviderId)?.Identity == target.ProviderIdentity;

    private void ValidateCodexLayers(string provider, string project)
    {
        string global = Path.GetFullPath(Path.Combine(_codexHome, "config.toml"));
        foreach (string path in _configurationPaths("codex", project).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!File.Exists(path)) continue;
            TomlTable root = TomlSerializer.Deserialize<TomlTable>(File.ReadAllText(path)) ?? throw new InvalidDataException("Codex 配置无法读取");
            bool primary = Path.GetFullPath(path).Equals(global, StringComparison.OrdinalIgnoreCase);
            if (!primary && root.Keys.Any(k => k is "model" or "model_provider" or "model_providers" or "profile" or "profiles" or "openai_base_url"))
                throw new InvalidOperationException("Codex 项目或管理配置覆盖模型连接，无法固定供应商");
            if (root.ContainsKey("active_profile") || root.ContainsKey("profile_v2"))
                throw new InvalidOperationException("Codex 配置版本尚无法核实，请使用客户端续跑");
            // 固定上游 0.148.0:model_provider 取 config_profile.model_provider.or(cfg.model_provider),
            // 而 -c 只改根表;主配置的活动 profile 若自带 model_provider,会静默压过命令行固定值。
            if (primary && root.TryGetValue("profile", out object? activeProfile) && activeProfile is string profileName &&
                root.TryGetValue("profiles", out object? profiles) && profiles is TomlTable profileTable &&
                profileTable.TryGetValue(profileName, out object? selected) && selected is TomlTable selectedProfile &&
                selectedProfile.ContainsKey("model_provider"))
                throw new InvalidOperationException("Codex 主配置的活动 profile 指定了 model_provider,会压过命令行固定值,无法固定供应商");
            if (root.ContainsKey("experimental_thread_store"))
                throw new InvalidOperationException("Codex 自定义会话存储的独占锁尚未核实");
            if (primary && root.TryGetValue("model_providers", out object? providers) && providers is TomlTable map &&
                map.TryGetValue(provider, out object? configured) && configured is TomlTable fields)
            {
                string[] allowed = { "name", "base_url", "wire_api", "env_key", "env_key_instructions", "requires_openai_auth", "http_headers", "env_http_headers", "query_params", "request_max_retries", "stream_max_retries", "stream_idle_timeout_ms", "supports_websockets" };
                if (fields.Keys.Any(k => !allowed.Contains(k, StringComparer.Ordinal)))
                    throw new InvalidOperationException("Codex 供应商含有尚不能固定的认证或连接配置");
            }
        }
    }

}
