using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace AiResume.Worker.Probes;

/// <summary>
/// Codex 当前 OpenAI-compatible provider 的余额探测。
///
/// 上游盘点(2026-08-13):CC Switch 对第三方 provider 不走 cc-connect 管理 API,
/// 而是在 provider 上保存 usage_script。当前 Sub2API 脚本是:
/// <c>GET {{baseUrl}}/v1/usage</c>,Bearer 鉴权,再从 <c>remaining</c>、
/// <c>quota.remaining</c> 或 <c>balance</c> 提取余额,默认单位 USD。
///
/// 这里只做该常见形状的薄封装:读 Codex 自己的活动 provider 配置与 auth.json,
/// 不读取 CC Switch 数据库,也不写任何配置或凭据。
/// </summary>
public sealed record CodexBalanceResult(
    ProviderReadiness Readiness,
    string Reason,
    string? Summary,
    decimal? Remaining,
    string? Unit,
    string? ProviderIdentity = null,
    bool IsStale = false);

public sealed class CodexBalanceProbe
{
    private const int MaxResponseBytes = 64 * 1024;
    private static readonly TimeSpan DefaultRetryDelay = TimeSpan.FromMilliseconds(1500);
    private static readonly TimeSpan DefaultKeepLastGood = TimeSpan.FromMinutes(10);

    private readonly string? _codexHome;
    private readonly HttpMessageHandler? _handler;
    private readonly TimeSpan _timeout;
    private readonly TimeSpan _retryDelay;
    private readonly TimeSpan _keepLastGood;
    private readonly Func<string, string?> _environmentVariable;
    private readonly Func<DateTimeOffset> _clock;
    private readonly SemaphoreSlim _probeGate = new(1, 1);
    private readonly object _lastGoodGate = new();
    private readonly Dictionary<string, LastGoodBalance> _lastGood = new(StringComparer.Ordinal);

    private sealed record LastGoodBalance(CodexBalanceResult Result, DateTimeOffset ObservedAt);

    public CodexBalanceProbe(
        string? codexHome = null,
        HttpMessageHandler? handler = null,
        TimeSpan? timeout = null,
        Func<string, string?>? environmentVariable = null,
        TimeSpan? retryDelay = null,
        TimeSpan? keepLastGood = null,
        Func<DateTimeOffset>? clock = null)
    {
        _environmentVariable = environmentVariable ?? Environment.GetEnvironmentVariable;
        _codexHome = CodexAuthProbe.ResolveCodexHome(codexHome, _environmentVariable);
        _handler = handler;
        _timeout = timeout ?? TimeSpan.FromSeconds(10);
        _retryDelay = retryDelay ?? DefaultRetryDelay;
        _keepLastGood = keepLastGood ?? DefaultKeepLastGood;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    public Task<CodexBalanceResult> ProbeAsync(CancellationToken ct = default)
    {
        CodexProviderCredentials provider = CodexAuthProbe.ReadActiveProviderCredentials(
            _codexHome,
            _environmentVariable);
        return ProbeAsync(provider, ct);
    }

    public async Task<CodexBalanceResult> ProbeAsync(
        CodexProviderCredentials provider,
        CancellationToken ct = default)
    {
        await _probeGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await ProbeSerializedAsync(provider, ct).ConfigureAwait(false);
        }
        finally
        {
            _probeGate.Release();
        }
    }

    private async Task<CodexBalanceResult> ProbeSerializedAsync(
        CodexProviderCredentials provider,
        CancellationToken ct)
    {
        string providerIdentity = CodexAuthProbe.CreateProviderIdentity(provider);
        if (IsOfficialOpenAiEndpoint(provider.BaseUrl))
        {
            return ResolveDisplayResult(new CodexBalanceResult(
                ProviderReadiness.Unknown,
                "official-provider",
                "官方 ChatGPT/OpenAI 不提供第三方余额接口",
                null,
                null,
                providerIdentity));
        }

        if (string.Equals(provider.CredentialSource, "auth.json:chatgpt", StringComparison.Ordinal))
        {
            return ResolveDisplayResult(new CodexBalanceResult(
                ProviderReadiness.Unknown,
                "oauth-not-supported",
                "ChatGPT 登录令牌不用于第三方余额接口",
                null,
                null,
                providerIdentity));
        }

        if (string.IsNullOrWhiteSpace(provider.BaseUrl) ||
            (provider.RequiresOpenAiAuth && string.IsNullOrWhiteSpace(provider.BearerToken)) ||
            (!string.IsNullOrWhiteSpace(provider.CredentialSource) &&
             provider.CredentialSource is not "none" &&
             string.IsNullOrWhiteSpace(provider.BearerToken)))
        {
            return ResolveDisplayResult(new CodexBalanceResult(
                ProviderReadiness.NoCredential,
                "no-config",
                provider.Problem ?? "读不到 Codex 的 base_url 或凭据",
                null,
                null,
                providerIdentity));
        }

        using HttpClient http = CreateHttpClient(_handler);
        http.Timeout = Timeout.InfiniteTimeSpan;

        CodexBalanceResult result = await ProbeOnceAsync(
                http,
                provider,
                providerIdentity,
                ct)
            .ConfigureAwait(false);
        if (ShouldRetry(result))
        {
            if (_retryDelay > TimeSpan.Zero)
            {
                await Task.Delay(_retryDelay, ct).ConfigureAwait(false);
            }

            result = await ProbeOnceAsync(http, provider, providerIdentity, ct).ConfigureAwait(false);
        }

        return ResolveDisplayResult(result);
    }

    private async Task<CodexBalanceResult> ProbeOnceAsync(
        HttpClient http,
        CodexProviderCredentials provider,
        string providerIdentity,
        CancellationToken ct)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(_timeout);
        CancellationToken requestToken = timeoutCts.Token;

        try
        {
            using var req = new HttpRequestMessage(
                HttpMethod.Get,
                BuildUsageUrl(provider.BaseUrl!, provider.QueryParameters));
            CodexAuthProbe.AddRequestHeaders(req, provider);
            bool usedCredential = CodexAuthProbe.UsesConfiguredCredential(provider);

            using HttpResponseMessage resp = await http
                .SendAsync(req, HttpCompletionOption.ResponseHeadersRead, requestToken)
                .ConfigureAwait(false);

            if (resp.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                // Cloudflare 的 1xxx 拦截同样走 403,但它拦的是客户端而不是凭据。
                // 不分开就会红着说"余额接口拒绝凭据",而换个 UA 同一把凭据就能 200。
                if (resp.StatusCode == HttpStatusCode.Forbidden &&
                    CodexAuthProbe.LooksLikeCdnBlock(
                        await ReadBodyAsync(resp.Content, requestToken).ConfigureAwait(false)))
                {
                    return new CodexBalanceResult(
                        ProviderReadiness.Unknown,
                        "cdn-blocked",
                        "余额接口被 CDN 拦截(非凭据问题)",
                        null,
                        null,
                        providerIdentity);
                }

                return new CodexBalanceResult(
                    ProviderReadiness.Auth,
                    "http-" + (int)resp.StatusCode,
                    usedCredential ? "余额接口拒绝凭据" : "余额接口需要凭据",
                    null,
                    null,
                    providerIdentity);
            }

            if ((int)resp.StatusCode == 402)
            {
                return new CodexBalanceResult(
                    ProviderReadiness.Insufficient,
                    "http-402",
                    "余额不足或需充值",
                    null,
                    null,
                    providerIdentity);
            }

            if ((int)resp.StatusCode == 429)
            {
                // 限流是"这次没问出来",不是"余额不够"。判成 Insufficient 会让面板红着说错话,
                // 而 IsTransient 又把 http-429 当瞬时失败去接 last-good —— 同一个状态码不能
                // 一边是确定性红色、一边又有资格续用旧余额,两套语义必须一致。
                return new CodexBalanceResult(
                    ProviderReadiness.Unknown,
                    "http-429",
                    "余额接口被限流",
                    null,
                    null,
                    providerIdentity);
            }

            if (resp.StatusCode == HttpStatusCode.NotFound)
            {
                return new CodexBalanceResult(
                    ProviderReadiness.Unknown,
                    "not-supported",
                    "余额接口不可用",
                    null,
                    null,
                    providerIdentity);
            }

            if (!resp.IsSuccessStatusCode)
            {
                return new CodexBalanceResult(
                    (int)resp.StatusCode >= 500 ? ProviderReadiness.Unreachable : ProviderReadiness.Unknown,
                    "http-" + (int)resp.StatusCode,
                    $"余额接口返回 {(int)resp.StatusCode}",
                    null,
                    null,
                    providerIdentity);
            }

            if (resp.Content.Headers.ContentLength is > MaxResponseBytes)
            {
                return TooLarge() with { ProviderIdentity = providerIdentity };
            }

            string? body = await ReadBodyAsync(resp.Content, requestToken).ConfigureAwait(false);
            if (body is null)
            {
                return TooLarge() with { ProviderIdentity = providerIdentity };
            }

            return Parse(body) with { ProviderIdentity = providerIdentity };
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new CodexBalanceResult(
                ProviderReadiness.Timeout, "timeout", "余额探测超时", null, null, providerIdentity);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (HttpRequestException)
        {
            return new CodexBalanceResult(
                ProviderReadiness.Unreachable, "unreachable", "余额接口网络不可达", null, null, providerIdentity);
        }
        catch (IOException)
        {
            return new CodexBalanceResult(
                ProviderReadiness.Unreachable, "read-failed", "余额响应读取失败", null, null, providerIdentity);
        }
        catch (UriFormatException)
        {
            return new CodexBalanceResult(
                ProviderReadiness.NoCredential, "invalid-url", "Codex base_url 格式无效", null, null, providerIdentity);
        }
    }

    public static CodexBalanceResult Parse(string body)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(body);
            JsonElement root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return Malformed();
            }

            if (!TryReadValidity(root, out bool valid))
            {
                return new CodexBalanceResult(
                    ProviderReadiness.Unknown,
                    "invalid-validity",
                    "账户有效性字段类型无效",
                    null,
                    null);
            }

            if (!TryReadRemaining(root, out decimal? remaining))
            {
                return new CodexBalanceResult(
                    ProviderReadiness.Unknown,
                    "invalid-balance-type",
                    "余额字段必须是数字",
                    null,
                    null);
            }

            if (!TryReadUnit(root, out string? rawUnit))
            {
                return new CodexBalanceResult(
                    ProviderReadiness.Unknown,
                    "invalid-unit",
                    "余额单位必须是字符串",
                    null,
                    null);
            }

            if (!TryNormalizeUnit(rawUnit, out string unit))
            {
                return new CodexBalanceResult(
                    ProviderReadiness.Unknown,
                    "invalid-unit",
                    "余额单位无效",
                    null,
                    null);
            }

            if (!valid)
            {
                return new CodexBalanceResult(
                    ProviderReadiness.Insufficient,
                    "invalid",
                    remaining is null ? "账户不可用" : $"账户不可用(余额 {FormatAmount(remaining.Value, unit)})",
                    remaining,
                    unit);
            }

            if (remaining is null)
            {
                return new CodexBalanceResult(
                    ProviderReadiness.Unknown,
                    "no-balance",
                    "余额未报告",
                    null,
                    unit);
            }

            if (remaining.Value < 0)
            {
                return new CodexBalanceResult(
                    ProviderReadiness.Unknown,
                    "invalid-balance",
                    "余额数值无效",
                    null,
                    unit);
            }

            return new CodexBalanceResult(
                remaining.Value == 0 ? ProviderReadiness.Insufficient : ProviderReadiness.Ok,
                remaining.Value == 0 ? "empty" : "ok",
                "余额 " + FormatAmount(remaining.Value, unit),
                remaining,
                unit);
        }
        catch (JsonException)
        {
            return Malformed();
        }
    }

    private CodexBalanceResult ResolveDisplayResult(CodexBalanceResult current)
    {
        if (current.ProviderIdentity is not { Length: > 0 } identity)
        {
            return current;
        }

        DateTimeOffset now = _clock();
        lock (_lastGoodGate)
        {
            foreach (string expired in _lastGood
                         .Where(pair => now - pair.Value.ObservedAt >= _keepLastGood)
                         .Select(pair => pair.Key)
                         .ToArray())
            {
                _lastGood.Remove(expired);
            }

            if (current.Readiness == ProviderReadiness.Ok && current.Remaining is > 0)
            {
                CodexBalanceResult fresh = current with { IsStale = false };
                _lastGood[identity] = new LastGoodBalance(fresh, now);
                return fresh;
            }

            if (IsTransient(current))
            {
                if (_lastGood.TryGetValue(identity, out LastGoodBalance? lastGood) &&
                    now - lastGood.ObservedAt < _keepLastGood)
                {
                    string amount = FormatAmount(lastGood.Result.Remaining!.Value, lastGood.Result.Unit);
                    string failure = string.IsNullOrWhiteSpace(current.Summary) ? "本次探测失败" : current.Summary;
                    return lastGood.Result with
                    {
                        Reason = "stale-" + current.Reason,
                        Summary = $"最近余额 {amount}；{failure}",
                        ProviderIdentity = identity,
                        IsStale = true,
                    };
                }

                return current;
            }

            // 鉴权、余额为零、账户失效、确定性 4xx 或解析错误都会让旧快照失信。
            _lastGood.Remove(identity);
            return current;
        }
    }

    private static bool ShouldRetry(CodexBalanceResult result) =>
        result.Reason is "timeout" or "unreachable" or "read-failed";

    private static bool IsTransient(CodexBalanceResult result) =>
        result.Readiness is ProviderReadiness.Timeout or ProviderReadiness.Unreachable ||
        result.Reason is "http-429" or "cdn-blocked" ||
        result.Reason.Length == 8 &&
        result.Reason.StartsWith("http-5", StringComparison.Ordinal) &&
        int.TryParse(result.Reason.AsSpan(5), out int status) &&
        status is >= 500 and <= 599;

    public static string FormatAmount(decimal value, string? unit)
    {
        string normalized = TryNormalizeUnit(unit, out string safeUnit) ? safeUnit : "USD";
        string amount = value.ToString("0.##", CultureInfo.InvariantCulture);
        return normalized.ToUpperInvariant() switch
        {
            "CNY" or "RMB" => "¥" + amount,
            "USD" => amount + " USD",
            _ => amount + " " + normalized,
        };
    }

    public static string BuildUsageUrl(string baseUrl)
        => CodexAuthProbe.BuildV1ApiUrl(baseUrl, "usage");

    internal static string BuildUsageUrl(
        string baseUrl,
        IReadOnlyDictionary<string, string>? queryParameters)
        => CodexAuthProbe.BuildV1ApiUrl(baseUrl, "usage", queryParameters);

    private static CodexBalanceResult Malformed() =>
        new(ProviderReadiness.Unknown, "malformed", "余额响应无法解析", null, null);

    private static CodexBalanceResult TooLarge() =>
        new(ProviderReadiness.Unknown, "response-too-large", "余额响应过大", null, null);

    private static bool TryReadValidity(JsonElement root, out bool valid)
    {
        foreach (string property in new[] { "is_active", "isValid" })
        {
            if (!root.TryGetProperty(property, out JsonElement value) ||
                value.ValueKind == JsonValueKind.Null)
            {
                continue;
            }

            if (value.ValueKind is JsonValueKind.True or JsonValueKind.False)
            {
                valid = value.GetBoolean();
                return true;
            }

            valid = false;
            return false;
        }

        valid = true;
        return true;
    }

    private static bool TryReadRemaining(JsonElement root, out decimal? remaining)
    {
        if (TryReadNumberCandidate(root, "remaining", out remaining, out bool invalid))
        {
            return true;
        }

        if (invalid)
        {
            return false;
        }

        if (root.TryGetProperty("quota", out JsonElement quota) && quota.ValueKind == JsonValueKind.Object)
        {
            if (TryReadNumberCandidate(quota, "remaining", out remaining, out invalid))
            {
                return true;
            }

            if (invalid)
            {
                return false;
            }
        }

        if (TryReadNumberCandidate(root, "balance", out remaining, out invalid))
        {
            return true;
        }

        if (invalid)
        {
            return false;
        }

        remaining = null;
        return true;
    }

    private static bool TryReadNumberCandidate(
        JsonElement root,
        string property,
        out decimal? value,
        out bool invalid)
    {
        value = null;
        invalid = false;
        if (!root.TryGetProperty(property, out JsonElement element) ||
            element.ValueKind == JsonValueKind.Null)
        {
            return false;
        }

        if (element.ValueKind == JsonValueKind.Number && element.TryGetDecimal(out decimal number))
        {
            value = number;
            return true;
        }

        invalid = true;
        return false;
    }

    private static bool TryReadUnit(JsonElement root, out string? unit)
    {
        if (TryReadStringCandidate(root, "unit", out unit, out bool invalid))
        {
            return true;
        }

        if (invalid)
        {
            return false;
        }

        if (root.TryGetProperty("quota", out JsonElement quota) && quota.ValueKind == JsonValueKind.Object)
        {
            if (TryReadStringCandidate(quota, "unit", out unit, out invalid))
            {
                return true;
            }

            if (invalid)
            {
                return false;
            }
        }

        unit = null;
        return true;
    }

    private static bool TryReadStringCandidate(
        JsonElement root,
        string property,
        out string? value,
        out bool invalid)
    {
        value = null;
        invalid = false;
        if (!root.TryGetProperty(property, out JsonElement element) ||
            element.ValueKind == JsonValueKind.Null)
        {
            return false;
        }

        if (element.ValueKind == JsonValueKind.String)
        {
            value = element.GetString();
            return true;
        }

        invalid = true;
        return false;
    }

    private static bool TryNormalizeUnit(string? unit, out string normalized)
    {
        normalized = string.IsNullOrWhiteSpace(unit) ? "USD" : unit.Trim();
        if (normalized.Length is 0 or > 16)
        {
            return false;
        }

        foreach (char c in normalized)
        {
            if (char.IsControl(c))
            {
                return false;
            }
        }

        return true;
    }

    private static HttpClient CreateHttpClient(HttpMessageHandler? handler) =>
        handler is null
            ? new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
            : new HttpClient(handler, disposeHandler: false);

    private static bool IsOfficialOpenAiEndpoint(string? baseUrl)
    {
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out Uri? uri) ||
            uri.Scheme != Uri.UriSchemeHttps)
        {
            return false;
        }

        return string.Equals(uri.IdnHost, "api.openai.com", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(uri.IdnHost, "chatgpt.com", StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<string?> ReadBodyAsync(HttpContent content, CancellationToken ct)
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

            if (buffer.Length + read > MaxResponseBytes)
            {
                return null;
            }

            buffer.Write(chunk, 0, read);
        }

        return Encoding.UTF8.GetString(buffer.GetBuffer(), 0, checked((int)buffer.Length));
    }

}
