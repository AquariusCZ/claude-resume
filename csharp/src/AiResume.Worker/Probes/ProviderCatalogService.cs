using System.Net;
using System.Text.Json;
using AiResume.Core;
using AiResume.Worker.Quota;
using AiResume.Worker.Resume;

namespace AiResume.Worker.Probes;

public sealed record ProviderObservation(
    string Id, string Name, string Agent, string Kind, string Identity,
    ProviderModelCatalog Catalog, decimal? Balance, string? Currency,
    bool QuotaKnown, bool Limited, string? Problem, UsageSnapshot? Usage = null)
{
    /// <summary>凭据来源标签(不含实值),如 claude-oauth:ai-resume-token / auth.json:chatgpt。</summary>
    public string? CredentialSource { get; init; }

    public object ToPayload() => new
    {
        id = Id, name = Name, agent = Agent, kind = Kind, identity = Identity,
        credentialSource = CredentialSource,
        models = Catalog.Models.Select(m => new { id = m.Id, name = m.Name }),
        modelsVerified = Catalog.Verified, source = Catalog.Source,
        observedAtUtc = Catalog.ObservedAtUtc, balance = Balance, currency = Currency,
        quotaKnown = QuotaKnown, limited = Limited, problem = Problem ?? Catalog.Problem,
        // 模型可用性窗口只在「不可用」时露出:列出全部模型只会把侧栏填满无意义的绿行。
        windows = Usage?.Buckets.SelectMany(b => b.Windows)
            .Where(w => !w.Name.StartsWith(ChatGptUsage.ModelWindowPrefix, StringComparison.Ordinal) || w.Status == "blocked")
            .Select(w => new
            {
                name = w.Name, usedPercent = w.UsedPercent, resetAtUnix = w.ResetAtUnix,
                windowSeconds = w.WindowSeconds, carriedForward = w.CarriedForward, status = w.Status,
            }),
        resumeNote = Kind == "chatgpt"
            ? "账号窗口与模型可用性来自 ChatGPT 官方接口；账号未限额且目标模型可用时自动续跑"
            : Kind == "deepseek"
                ? "本周期固定调用 ID；供应商可能更新同一 ID 的后端版本"
                : "布防时固定供应商与调用 ID，下次布防重新选择",
    };
}

/// <summary>
/// 供应商目录与计费证据的薄聚合。刷新不写 agent 配置、不调用推理、不创建会话。
/// 模型列表、余额与订阅窗口始终来自同一个连接快照。
/// </summary>
public sealed class ProviderCatalogService
{
    private readonly ProviderRegistry _registry;
    private readonly HttpMessageHandler? _handler;
    private readonly QuotaService _quota;
    private readonly QuotaResumeProbe _quotaProbe;
    private readonly Func<ProviderConnection, CancellationToken, Task<ProviderObservation>>? _observe;

    public ProviderCatalogService(ProviderRegistry? registry = null, HttpMessageHandler? handler = null,
        Func<CancellationToken, Task<OAuthUsageResult>>? oauth = null,
        Func<ProviderConnection, CancellationToken, Task<ProviderObservation>>? observe = null,
        QuotaService? quota = null, QuotaResumeProbe? quotaProbe = null)
    {
        _registry = registry ?? new ProviderRegistry();
        _handler = handler;
        _quota = quota ?? (oauth is null ? new QuotaService() : new QuotaService(probe: _ => Task.FromResult(new ClaudeProbeResult()), oauthProbe: oauth));
        _quotaProbe = quotaProbe ?? new QuotaResumeProbe(_quota);
        _observe = observe;
    }

    public ProviderRegistry Registry => _registry;

    public async Task<IReadOnlyList<ProviderObservation>> ReadAsync(CancellationToken ct)
    {
        IReadOnlyList<ProviderConnection> connections = _registry.Read();
        return await Task.WhenAll(connections.Select(p => ObserveAsync(p, ct))).ConfigureAwait(false);
    }

    public async Task<ProviderObservation> ObserveAsync(ProviderConnection connection, CancellationToken ct)
    {
        try
        {
            ProviderObservation observation = _observe is not null
                ? await _observe(connection, ct).ConfigureAwait(false)
                : await ObserveCoreAsync(connection, ct).ConfigureAwait(false);
            ProviderConnection? current = _registry.Find(connection.Id);
            if (current?.Identity != connection.Identity)
                return Unavailable(connection, "查询期间供应商或账号已改变，请刷新");
            return observation with { CredentialSource = connection.Credentials.CredentialSource };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception) { return Unavailable(connection, "供应商数据暂时无法读取"); }
    }

    public async Task<ResumeTarget> BindAsync(string providerId, string modelId, CancellationToken ct)
    {
        ProviderConnection connection = _registry.Find(providerId)
            ?? throw new InvalidOperationException("供应商已改变，请刷新后重新选择");
        ProviderObservation observation = await ObserveAsync(connection, ct).ConfigureAwait(false);
        ProviderModel? model = observation.Catalog.Models.SingleOrDefault(m => m.Id == modelId);
        if (!observation.Catalog.Verified || model is null || !ProviderModels.IsSafeId(modelId))
            throw new InvalidOperationException("无法从当前供应商目录确认所选模型，请刷新后重试");
        return new ResumeTarget
        {
            ProviderId = connection.Id, ProviderIdentity = connection.Identity,
            ProviderName = connection.Name, Agent = connection.Agent, ModelId = model.Id,
            ModelName = model.Name, BoundAtUtc = DateTimeOffset.UtcNow,
        };
    }

    public async Task<ClaudeProbeResult> CheckAsync(ResumeTarget target, CancellationToken ct)
    {
        ProviderConnection? connection = _registry.Find(target.ProviderId);
        if (!target.IsValid || connection?.Identity != target.ProviderIdentity || connection.Agent != target.Agent)
            return new ClaudeProbeResult { Reason = "provider-changed" };
        ProviderObservation observation = await ObserveAsync(connection, ct).ConfigureAwait(false);
        if (!observation.Catalog.Verified || !observation.Catalog.Models.Any(m => m.Id == target.ModelId))
            return new ClaudeProbeResult { Reason = "model-unverified" };
        if (connection.Kind == "claude-oauth" && observation.Usage is { } snapshot)
        {
            return _quotaProbe.Evaluate(snapshot, target.ModelId, _quota.StorageWarning);
        }
        if (connection.Kind == "chatgpt")
        {
            return observation.Usage is { } chatGpt
                ? EvaluateChatGpt(chatGpt, target.ModelId)
                : new ClaudeProbeResult();
        }
        if (!observation.QuotaKnown) return new ClaudeProbeResult();
        return new ClaudeProbeResult
        {
            Ready = !observation.Limited && observation.Balance > 0,
            Reason = observation.Limited ? "limited" : observation.Balance > 0 ? "ok" : "unknown",
        };
    }

    /// <summary>
    /// Codex 官方(ChatGPT 登录)的续跑放行规则,保守优先:
    /// <list type="bullet">
    /// <item>账号任一窗口/附加限额打满、spend 限制触发、或目标模型 <c>model_usage.available=false</c> → limited;</item>
    /// <item>账号窗口全部 allowed 且百分比 &lt; 100、无未归因限流、目标模型未被列为不可用 → ok;</item>
    /// <item>没有数据、格式未核实或任一窗口缺百分比 → unknown(等待,不猜)。</item>
    /// </list>
    /// 服务端没有在 model_usage 里列出目标模型时不视为不可用:列表只枚举有过记录的模型。
    /// </summary>
    public static ClaudeProbeResult EvaluateChatGpt(UsageSnapshot usage, string modelId)
    {
        if (!usage.HasData || usage.UnavailableReason is not null) return new ClaudeProbeResult();
        UsageBucket? account = usage.Buckets.FirstOrDefault(b => b.Name == ChatGptUsage.AccountBucket);
        if (account is null || account.Windows.Count == 0) return new ClaudeProbeResult();
        UsageWindow? model = ChatGptUsage.FindModelWindow(usage, modelId);
        UsageWindow? primary = account.Windows.FirstOrDefault(w => w.Name.StartsWith("primary", StringComparison.Ordinal));
        UsageWindow? secondary = account.Windows.FirstOrDefault(w => w.Name.StartsWith("secondary", StringComparison.Ordinal));
        bool limited = usage.Buckets.Any(b => b.LimitReached || b.UnattributedLimitReached) ||
                       account.Windows.Any(w => w.Status == "blocked") ||
                       model?.Status == "blocked";
        bool ready = !limited && account.Allowed &&
                     account.Windows.All(w => w.Status == "allowed" && w.UsedPercent is >= 0 and < 100) &&
                     (model is null || model.Status == "allowed");
        return new ClaudeProbeResult
        {
            Ready = ready,
            Reason = limited ? "limited" : ready ? "ok" : "unknown",
            FiveHourResetUtc = primary?.ResetAtUnix is { } p ? DateTimeOffset.FromUnixTimeSeconds(p) : null,
            SevenDayResetUtc = secondary?.ResetAtUnix is { } s ? DateTimeOffset.FromUnixTimeSeconds(s) : null,
            FiveHourUtil = primary?.UsedPercent is { } pu ? pu / 100d : null,
            SevenDayUtil = secondary?.UsedPercent is { } su ? su / 100d : null,
        };
    }

    private async Task<ProviderObservation> ObserveCoreAsync(ProviderConnection connection, CancellationToken ct)
    {
        CodexProviderCredentials credentials = connection.Credentials;
        if (credentials.Problem is not null || (connection.Agent != "codex" && string.IsNullOrEmpty(credentials.BearerToken)))
            return Unavailable(connection, credentials.Problem ?? "凭据未就绪，请先在对应客户端配置供应商");

        ProviderModelCatalog catalog;
        try
        {
        if (connection.Agent == "codex" && connection.Kind != "chatgpt")
        {
            CodexAuthResult auth = await CodexAuthProbe.ProbeModelsAsync(credentials, _handler, ct).ConfigureAwait(false);
            catalog = auth.Catalog ?? ProviderModelCatalog.Unavailable(auth.Detail);
            if (auth.Outcome is CodexAuthOutcome.Rejected or CodexAuthOutcome.NotConfigured)
                return Unavailable(connection, "供应商未接受当前凭据");
        }
        else
        {
            string modelsUrl = connection.Kind switch
            {
                "deepseek" => "https://api.deepseek.com/models",
                "chatgpt" => "https://chatgpt.com/backend-api/codex/models?client_version=" + Uri.EscapeDataString(AgentCommandLocator.CodexVersion() ?? throw new InvalidOperationException("Codex 版本未核实")),
                _ => CodexAuthProbe.BuildV1ApiUrl(credentials.BaseUrl!, "models", new Dictionary<string, string> { ["limit"] = "1000" }),
            };
            catalog = connection.Kind == "chatgpt"
                ? ParseChatGptModels(await GetAsync(modelsUrl, connection, ct).ConfigureAwait(false))
                : await ReadCatalogPagesAsync(modelsUrl, connection, ct).ConfigureAwait(false);
        }

        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) { catalog = ProviderModelCatalog.Unavailable(DescribeMetadataFailure(ex, "模型目录")); }

        if (connection.Kind == "claude-oauth")
        {
            UsageSnapshot snapshot = await _quota.GetAsync(true, ct).ConfigureAwait(false);
            // 目录查询和额度查询分别读认证；账号指纹必须再次与连接快照配对。
            string expected = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(connection.Id + ":" + snapshot.CredentialFingerprint)));
            if (snapshot.EvidenceSource != UsageEvidenceSource.OAuth || expected != connection.Identity)
                return new(connection.Id, connection.Name, connection.Agent, connection.Kind, connection.Identity,
                    catalog, null, null, false, false, "Claude 订阅额度未核实");
            return new(connection.Id, connection.Name, connection.Agent, connection.Kind, connection.Identity,
                catalog, null, null, snapshot.HasData && snapshot.Buckets.SelectMany(b => b.Windows).All(w => !w.CarriedForward && w.UsedPercent is not null),
                snapshot.Buckets.Any(b => b.UnattributedLimitReached || b.Windows.Any(w => !w.CarriedForward && (w.UsedPercent >= 100 || w.Status == "blocked"))),
                _quota.StorageWarning ?? snapshot.UnavailableReason, snapshot);
        }
        if (connection.Kind == "deepseek")
        {
            DeepSeekProbeResult balance = await new DeepSeekProbe(() => credentials.BearerToken, _handler)
                .ProbeAsync(ct).ConfigureAwait(false);
            bool known = (balance.Readiness is ProviderReadiness.Ok or ProviderReadiness.Insufficient) && balance.BalanceCny is not null;
            return new(connection.Id, connection.Name, connection.Agent, connection.Kind, connection.Identity,
                catalog, balance.BalanceCny, "CNY", known,
                balance.Readiness == ProviderReadiness.Insufficient || balance.BalanceCny <= 0,
                known ? null : balance.Summary);
        }
        if (connection.Kind == "chatgpt")
        {
            UsageSnapshot snapshot;
            try { snapshot = ChatGptUsage.Parse(await GetAsync("https://chatgpt.com/backend-api/wham/usage", connection, ct).ConfigureAwait(false)); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) { snapshot = UsageSnapshot.Unavailable("ChatGPT", DateTimeOffset.UtcNow, DescribeMetadataFailure(ex, "订阅窗口")); }
            // 行级 limited 只看账号级证据;某个没在用的模型不可用不该把整行标成限额,
            // 目标模型的可用性由 EvaluateChatGpt 在布防目标上单独判断。
            return new(connection.Id, connection.Name, connection.Agent, connection.Kind, connection.Identity,
                catalog, null, null, snapshot.HasData,
                snapshot.Buckets.Any(b => b.LimitReached || b.UnattributedLimitReached),
                snapshot.UnavailableReason, snapshot);
        }

        CodexBalanceResult usage = await new CodexBalanceProbe(handler: _handler).ProbeAsync(credentials, ct).ConfigureAwait(false);
        bool quotaKnown = !usage.IsStale && usage.Remaining is not null &&
                          usage.Readiness is ProviderReadiness.Ok or ProviderReadiness.Insufficient;
        return new(connection.Id, connection.Name, connection.Agent, connection.Kind, connection.Identity,
            catalog, usage.Remaining, usage.Unit, quotaKnown,
            usage.Readiness == ProviderReadiness.Insufficient || usage.Remaining <= 0,
            quotaKnown ? null : usage.Summary);
    }

    private async Task<ProviderModelCatalog> ReadCatalogPagesAsync(string url, ProviderConnection connection, CancellationToken ct)
    {
        var models = new List<ProviderModel>();
        var cursors = new HashSet<string>(StringComparer.Ordinal);
        for (int page = 0; page < 20; page++)
        {
            string body = await GetAsync(url, connection, ct).ConfigureAwait(false);
            ProviderModelCatalog catalog = ProviderModels.Parse(body);
            if (!catalog.Verified && catalog.Problem != "供应商目录尚有后续分页，暂不能布防") return catalog;
            models.AddRange(catalog.Models);
            if (models.Count > ProviderModels.MaxModels) return ProviderModelCatalog.Unavailable("供应商目录超过模型数量限制");
            if (catalog.Verified) return catalog with { Models = models.DistinctBy(m => m.Id, StringComparer.Ordinal).ToArray() };
            using JsonDocument doc = JsonDocument.Parse(body);
            if (!doc.RootElement.TryGetProperty("last_id", out JsonElement cursor) || cursor.ValueKind != JsonValueKind.String ||
                cursor.GetString() is not { } next || !ProviderModels.IsSafeId(next) || !cursors.Add(next))
                return ProviderModelCatalog.Unavailable("供应商目录分页游标无法核实");
            url = CodexAuthProbe.BuildV1ApiUrl(connection.Credentials.BaseUrl!, "models",
                new Dictionary<string, string> { ["limit"] = "1000", ["after_id"] = next });
        }
        return ProviderModelCatalog.Unavailable("供应商目录分页未完成");
    }

    private async Task<string> GetAsync(string url, ProviderConnection connection, CancellationToken ct)
    {
        using HttpClient client = _handler is null
            ? new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
            : new HttpClient(_handler, disposeHandler: false);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (connection.Credentials.CredentialSource == "anthropic-key")
            request.Headers.TryAddWithoutValidation("x-api-key", connection.Credentials.BearerToken);
        else CodexAuthProbe.AddRequestHeaders(request, connection.Credentials);
        if (connection.Kind == "chatgpt")
        {
            request.Headers.Remove("User-Agent");
            request.Headers.TryAddWithoutValidation("User-Agent", "codex_cli_rs/" + (AgentCommandLocator.CodexVersion() ?? "unknown"));
            request.Headers.TryAddWithoutValidation("originator", "codex_cli_rs");
        }
        if (connection.Agent == "claudecode")
        {
            request.Headers.TryAddWithoutValidation("anthropic-version", "2023-06-01");
            request.Headers.Remove("User-Agent");
            request.Headers.TryAddWithoutValidation("User-Agent", ClaudeOAuthUsageProbe.ResolveClaudeCodeUserAgent());
            if (connection.Kind == "claude-oauth") request.Headers.TryAddWithoutValidation("anthropic-beta", "oauth-2025-04-20");
        }
        using HttpResponseMessage response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
        if (response.StatusCode != HttpStatusCode.OK) throw new HttpRequestException("供应商元数据请求失败", null, response.StatusCode);
        return await ProviderModels.ReadBodyAsync(response.Content, timeout.Token).ConfigureAwait(false);
    }

    /// <summary>
    /// 元数据请求失败的可行动文案。登录失效、限流、版本未核实、超时与网络分别说清,
    /// 不合并成一句「暂时无法读取」——那句话用户没法据此做任何事。
    /// </summary>
    public static string DescribeMetadataFailure(Exception ex, string what) => ex switch
    {
        HttpRequestException { StatusCode: HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden } rejected =>
            what + "被拒(HTTP " + (int)rejected.StatusCode.GetValueOrDefault() + "):登录已失效或无权限,请在对应客户端重新登录",
        HttpRequestException { StatusCode: HttpStatusCode.TooManyRequests } => what + "被限流(HTTP 429),稍后自动重试",
        HttpRequestException { StatusCode: { } code } => what + "请求失败(HTTP " + (int)code + ")",
        HttpRequestException => what + "网络请求失败",
        OperationCanceledException => what + "请求超时",
        InvalidOperationException version when version.Message.Contains("版本", StringComparison.Ordinal) => what + "未读取:" + version.Message,
        JsonException => what + "格式未核实",
        _ => what + "暂时无法读取",
    };

    /// <summary>ChatGPT <c>/backend-api/codex/models</c>:只收 <c>visibility=list</c> 的 slug;形状以 2026-10-12 本机录制为准。</summary>
    public static ProviderModelCatalog ParseChatGptModels(string body)
    {
        using JsonDocument doc = JsonDocument.Parse(body);
        if (!doc.RootElement.TryGetProperty("models", out JsonElement models) || models.ValueKind != JsonValueKind.Array)
            return ProviderModelCatalog.Unavailable("Codex 模型目录格式未核实");
        var entries = models.EnumerateArray().Where(m => m.ValueKind == JsonValueKind.Object &&
                m.TryGetProperty("visibility", out JsonElement visibility) && visibility.ValueKind == JsonValueKind.String && visibility.GetString() == "list" &&
                m.TryGetProperty("slug", out JsonElement slug) && slug.ValueKind == JsonValueKind.String)
            .Select(m => new { id = m.GetProperty("slug").GetString(), display_name = m.TryGetProperty("display_name", out JsonElement name) ? name.GetString() : null });
        return ProviderModels.Parse(JsonSerializer.Serialize(new { data = entries }));
    }

    private static ProviderObservation Unavailable(ProviderConnection p, string problem) =>
        new(p.Id, p.Name, p.Agent, p.Kind, p.Identity, ProviderModelCatalog.Unavailable(problem), null, null, false, false, problem)
        {
            CredentialSource = p.Credentials.CredentialSource,
        };
}
