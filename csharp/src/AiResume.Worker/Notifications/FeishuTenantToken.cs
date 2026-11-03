using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AiResume.Worker.Notifications;

/// <summary>换 tenant_access_token 失败的性质。只描述性质，不含任何凭据。</summary>
public enum TenantTokenFailureKind
{
    /// <summary>DNS/TCP/TLS/代理隧道/连接中断:请求可能根本没到飞书。</summary>
    Network,

    /// <summary>超时没有应答。</summary>
    Timeout,

    /// <summary>HTTP 429 或 code 99991400。</summary>
    RateLimited,

    /// <summary>HTTP 5xx。</summary>
    ServerError,

    /// <summary>
    /// 飞书明确拒绝了凭据或应用配置:只认已知的凭据类错误码(10003 参数缺失、10012、10013 应用状态异常、
    /// 10014 app_id 不存在或 App Secret 不对)。
    /// </summary>
    Rejected,

    /// <summary>
    /// 应答不是预期结果：不是 JSON(网关、代理拦截页)、HTTP 状态不对、未识别的非零 code、9499 Bad Request、
    /// code 0 却没有合格的令牌，或读应答时出了意料之外的错。
    /// </summary>
    Unexpected,
}

/// <param name="Message">飞书的 msg(如 <c>app id not exists</c>),遮蔽 app_id 并截短;不含凭据。</param>
/// <param name="Detail">细分原因(<see cref="HttpRequestError"/> 名、异常类型名);不含凭据。</param>
/// <param name="Route">这次请求的网络路径:direct / env_proxy。</param>
public sealed record TenantTokenFailure(
    TenantTokenFailureKind Kind,
    int? HttpStatus = null,
    long? Code = null,
    string? Message = null,
    string? LogId = null,
    int? RetryAfterSeconds = null,
    string? Detail = null,
    string? Route = null,
    long? DurationMs = null);

/// <summary>一次取令牌的结果。令牌只交给 lark-cli 子进程的环境变量;<see cref="ToString"/> 不含令牌。</summary>
public sealed class TenantTokenOutcome
{
    private TenantTokenOutcome(string? token, DateTimeOffset expiresAtUtc, TenantTokenFailure? failure, bool fromCache)
    {
        Token = token;
        ExpiresAtUtc = expiresAtUtc;
        Failure = failure;
        FromCache = fromCache;
    }

    public string? Token { get; }

    public DateTimeOffset ExpiresAtUtc { get; }

    public TenantTokenFailure? Failure { get; }

    /// <summary>来自进程内缓存，而不是这一次刚换的。lark-cli 说它失效时值得作废重换一次。</summary>
    public bool FromCache { get; }

    public bool Ok => Token is not null;

    public static TenantTokenOutcome Issued(string token, DateTimeOffset expiresAtUtc) => new(token, expiresAtUtc, null, false);

    public static TenantTokenOutcome Failed(TenantTokenFailure failure) => new(null, default, failure, false);

    internal TenantTokenOutcome AsCached() => new(Token, ExpiresAtUtc, null, true);

    internal TenantTokenOutcome WithFailure(TenantTokenFailure failure) => new(null, default, failure, false);

    public override string ToString() => Ok
        ? "tenant_access_token(expires " + ExpiresAtUtc.ToString("o", CultureInfo.InvariantCulture) + ")"
        : "tenant_access_token_failed(" + Failure + ")";
}

/// <summary>
/// 用 AI Resume 机器范围副本里的 App ID / App Secret 向飞书换 tenant_access_token(ADR-0007)。
///
/// **为什么自己换**:lark-cli 1.0.81 的 env 凭据来源只接受现成的令牌(<c>LARKSUITE_CLI_TENANT_ACCESS_TOKEN</c>),
/// 给它 <c>LARKSUITE_CLI_APP_SECRET</c> 不会拿去换，必报 <c>no tat available from credential source "env"</c>
/// (2026-11-02 用假值实验查证);钥匙串那条路在 S4U 会话 0 下又间歇解不开。所以这里只做「换令牌」这一步，
/// 发消息、体检等其余所有操作仍走 lark-cli。
///
/// 边界：只换令牌、只用于 bot、只 feishu 品牌;令牌只在内存里按过期时间缓存，不落盘、不进日志/RPC/argv;
/// App Secret 只出现在发往官方端点的请求体里。不跟随重定向(307/308 会带着请求体重发到别处)。
/// 代理只认 HTTPS_PROXY(及 NO_PROXY),与 Go net/http 的 ProxyFromEnvironment 同一规则;不读 ALL_PROXY,
/// 也不用 WinINet 系统代理——本机代理客户端停了而系统代理残留时,lark-cli 照样直连得上飞书。
/// HTTPS_PROXY 解析不出就直连(而不是落回 .NET 的默认代理)。lark-cli 自己配置文件里的代理设置这里不跟随。
///
/// 端点与应答形状(官方文档;错误应答为 2026-11-02 本机用假值录制):
/// <c>POST https://open.feishu.cn/open-apis/auth/v3/tenant_access_token/internal</c>,
/// 成功 <c>{"code":0,"msg":"ok","tenant_access_token":"t-…","expire":7200}</c>——字段在顶层，没有 data 外壳,expire 单位秒;
/// app_id 不存在 HTTP 200 <c>{"code":10014,"msg":"app id not exists"}</c>;缺参数 HTTP 200 <c>{"code":10003,"data":{},"msg":"invalid param"}</c>;
/// 请求体不是 JSON HTTP 400 <c>{"code":9499,"msg":"Bad Request","error":{"log_id":…}}</c>。log_id 在响应头 <c>X-Tt-Logid</c>。
/// 有效期最长 2 小时;剩余不足 30 分钟时再换会拿到新令牌(新旧并存),否则返回原令牌。
/// </summary>
public sealed class FeishuTenantToken
{
    public const string Endpoint = "https://open.feishu.cn/open-apis/auth/v3/tenant_access_token/internal";

    private const string Host = "open.feishu.cn";

    /// <summary>缓存的令牌至少还剩这么久才复用:单次 lark-cli 调用不超过 30 秒，留足余量。</summary>
    public static readonly TimeSpan MinRemaining = TimeSpan.FromMinutes(5);

    /// <summary>飞书拒绝凭据或应用配置的已知错误码;其余非零 code 不冒充「凭据不对」。</summary>
    private static readonly HashSet<long> CredentialCodes = [10003, 10012, 10013, 10014];

    private static readonly TimeSpan MaxLifetime = TimeSpan.FromHours(2);

    private readonly HttpMessageHandler? _handler;
    private readonly Func<DateTimeOffset> _now;
    private readonly TimeSpan _timeout;
    private readonly object _sync = new();
    private byte[]? _cacheKey;
    private TenantTokenOutcome? _cached;

    /// <param name="handler">HTTP 处理器;测试注入桩，**绝不真调网络**。</param>
    public FeishuTenantToken(HttpMessageHandler? handler = null, Func<DateTimeOffset>? now = null, TimeSpan? timeout = null)
    {
        _handler = handler;
        _now = now ?? (() => DateTimeOffset.UtcNow);
        _timeout = timeout ?? TimeSpan.FromSeconds(15);
    }

    /// <summary>取可用令牌：同一组凭据、剩余不少于 <see cref="MinRemaining"/> 就复用缓存，否则向飞书换。</summary>
    public async Task<TenantTokenOutcome> GetAsync(string appId, string appSecret, CancellationToken ct)
    {
        byte[] key = CacheKey(appId, appSecret);
        lock (_sync)
        {
            if (_cached is { } cached && _cacheKey is not null && CryptographicOperations.FixedTimeEquals(_cacheKey, key) &&
                cached.ExpiresAtUtc - _now() >= MinRemaining)
            {
                return cached.AsCached();
            }
        }

        TenantTokenOutcome outcome = await ExchangeAsync(appId, appSecret, ct).ConfigureAwait(false);
        lock (_sync)
        {
            _cacheKey = outcome.Ok ? key : null;
            _cached = outcome.Ok ? outcome : null;
        }

        return outcome;
    }

    /// <summary>丢掉缓存的令牌(lark-cli 说它失效了、或副本已被清除),下一次重新换。</summary>
    public void Invalidate()
    {
        lock (_sync)
        {
            _cacheKey = null;
            _cached = null;
        }
    }

    /// <summary>不看缓存，真实向飞书换一次(保存前核验与诊断用)。除调用方取消外不抛异常。</summary>
    public async Task<TenantTokenOutcome> ExchangeAsync(string appId, string appSecret, CancellationToken ct)
    {
        (Uri? proxy, string route) = ResolveProxy(Environment.GetEnvironmentVariable);
        using HttpClient http = _handler is null ? new HttpClient(CreateHandler(proxy)) : new HttpClient(_handler, disposeHandler: false);
        http.Timeout = _timeout;
        http.MaxResponseContentBufferSize = 64 * 1024;
        string body = JsonSerializer.Serialize(new Dictionary<string, string> { ["app_id"] = appId, ["app_secret"] = appSecret });
        var stopwatch = Stopwatch.StartNew();
        TenantTokenOutcome outcome;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, Endpoint)
            {
                // 官方要求 Content-Type: application/json; charset=utf-8。
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            };
            using HttpResponseMessage response = await http.SendAsync(request, ct).ConfigureAwait(false);
            string? logId = response.Headers.TryGetValues("X-Tt-Logid", out IEnumerable<string>? values) ? values.FirstOrDefault() : null;
            int? retryAfter = response.Headers.RetryAfter?.Delta is { } delta ? (int)Math.Ceiling(delta.TotalSeconds) : null;
            string text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            outcome = Parse((int)response.StatusCode, text, logId, _now(), retryAfter);
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            // HttpClient 的超时也走 TaskCanceledException;调用方主动取消照常抛出。
            outcome = TenantTokenOutcome.Failed(new TenantTokenFailure(TenantTokenFailureKind.Timeout));
        }
        catch (HttpRequestException ex)
        {
            outcome = TenantTokenOutcome.Failed(new TenantTokenFailure(TenantTokenFailureKind.Network, Detail: ex.HttpRequestError.ToString()));
        }
        catch (IOException ex)
        {
            outcome = TenantTokenOutcome.Failed(new TenantTokenFailure(TenantTokenFailureKind.Network, Detail: ex.GetType().Name));
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            // 应答 charset 不认识、代理 scheme 不支持之类:仍归通道级的「应答异常」,不让异常逃到投递循环变成逐条失败。
            outcome = TenantTokenOutcome.Failed(new TenantTokenFailure(TenantTokenFailureKind.Unexpected, Detail: ex.GetType().Name));
        }

        if (outcome.Failure is { } failure)
        {
            // 纵深防御：飞书的 msg 万一回显了 App Secret,也不让它进说明、日志或界面。
            string? message = failure.Message is { } text && appSecret.Length > 0
                ? text.Replace(appSecret, "[REDACTED]", StringComparison.Ordinal)
                : failure.Message;
            outcome = outcome.WithFailure(failure with { Message = message, Route = route, DurationMs = stopwatch.ElapsedMilliseconds });
        }

        return outcome;
    }

    /// <summary>
    /// 解析换令牌应答。形状不对一律判 <see cref="TenantTokenFailureKind.Unexpected"/>,不抛。
    /// </summary>
    /// <remarks>仅为可测性公开：测试按录制形状直接断言(网络那半段一律注入桩)。</remarks>
    public static TenantTokenOutcome Parse(int httpStatus, string body, string? logId, DateTimeOffset nowUtc, int? retryAfterSeconds = null)
    {
        long? code = null;
        string? message = null;
        string? token = null;
        long? expire = null;
        try
        {
            using JsonDocument doc = JsonDocument.Parse(body);
            JsonElement root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.Object)
            {
                code = Number(root, "code");
                message = Text(root, "msg");
                token = Text(root, "tenant_access_token");
                expire = Number(root, "expire");
                if (logId is null && root.TryGetProperty("error", out JsonElement error) && error.ValueKind == JsonValueKind.Object)
                {
                    logId = Text(error, "log_id");
                }
            }
        }
        catch (JsonException)
        {
            // 不是 JSON:下面按 HTTP 状态归类。
        }

        TenantTokenOutcome Fail(TenantTokenFailureKind kind) =>
            TenantTokenOutcome.Failed(new TenantTokenFailure(kind, httpStatus, code, Shorten(message), logId, retryAfterSeconds));

        if (httpStatus == 429 || code == 99991400)
        {
            return Fail(TenantTokenFailureKind.RateLimited);
        }

        if (httpStatus >= 500)
        {
            return Fail(TenantTokenFailureKind.ServerError);
        }

        if (code is { } nonZero && nonZero != 0)
        {
            // 9499 是网关的 Bad Request(请求体本身不合法);未识别的错误码不冒充「凭据不对」。
            return Fail(CredentialCodes.Contains(nonZero) ? TenantTokenFailureKind.Rejected : TenantTokenFailureKind.Unexpected);
        }

        if (code is null || httpStatus is < 200 or > 299 || !IsWellFormedToken(token) || expire is not > 0)
        {
            return Fail(TenantTokenFailureKind.Unexpected);
        }

        TimeSpan lifetime = TimeSpan.FromSeconds(Math.Min(expire.Value, (long)MaxLifetime.TotalSeconds));
        return TenantTokenOutcome.Issued(token!, nowUtc + lifetime);
    }

    /// <summary>
    /// 换令牌请求走哪条网络路径，与 Go net/http 的 ProxyFromEnvironment 同一规则：只看 HTTPS_PROXY(没写 scheme 按 http://),
    /// NO_PROXY 命中 open.feishu.cn(<c>*</c>、<c>feishu.cn</c>、<c>.feishu.cn</c>…)就直连;解析不出也直连。返回(代理, 路径说明)。
    /// </summary>
    public static (Uri? Proxy, string Route) ResolveProxy(Func<string, string?> environment)
    {
        string? raw = environment("HTTPS_PROXY")?.Trim();
        if (string.IsNullOrEmpty(raw))
        {
            return (null, "direct");
        }

        if (Bypassed(environment("NO_PROXY")))
        {
            return (null, "direct(no_proxy)");
        }

        string candidate = raw.Contains("://", StringComparison.Ordinal) ? raw : "http://" + raw;
        if (!Uri.TryCreate(candidate, UriKind.Absolute, out Uri? proxy) || string.IsNullOrEmpty(proxy.Host))
        {
            return (null, "direct(proxy_unparsable)");
        }

        // .NET 的 SocketsHttpHandler 支持这几种;socks5h 等不支持的就直连并如实标出，不抛到发送那一步。
        return proxy.Scheme is "http" or "https" or "socks5" or "socks4" or "socks4a"
            ? (proxy, "env_proxy")
            : (null, "direct(proxy_unsupported)");
    }

    /// <summary>给 <c>notify check</c> 看的路径说明;代理地址去掉用户名口令。</summary>
    public static string DescribeRoute(Func<string, string?> environment)
    {
        (Uri? proxy, string route) = ResolveProxy(environment);
        return proxy is null ? route : $"{route} {proxy.Scheme}://{proxy.Host}:{proxy.Port}";
    }

    /// <summary>生产用的处理器(公开只为测试断言它不跟随重定向、代理只认 HTTPS_PROXY)。</summary>
    public static HttpMessageHandler CreateHandler(Uri? proxy) => new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        // 显式给出代理(或明确不用):不落回 HttpClient.DefaultProxy,它在 Windows 上会改用 WinINet 系统代理。
        UseProxy = proxy is not null,
        Proxy = proxy is null ? null : new WebProxy(proxy),
    };

    private static bool Bypassed(string? noProxy)
    {
        foreach (string raw in (noProxy ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            string entry = raw.ToLowerInvariant();
            int colon = entry.LastIndexOf(':');
            if (colon > 0 && !entry.Contains(']', StringComparison.Ordinal))
            {
                entry = entry[..colon];
            }

            if (entry == "*")
            {
                return true;
            }

            // Go 的规则:「.y.com」只匹配子域;「y.com」匹配自身与子域。
            bool matches = entry.StartsWith('.')
                ? Host.EndsWith(entry, StringComparison.Ordinal)
                : Host == entry || Host.EndsWith("." + entry, StringComparison.Ordinal);
            if (matches)
            {
                return true;
            }
        }

        return false;
    }

    // 令牌要进子进程环境块，也会进输出脱敏的全文替换集合:只收可见 ASCII、长度合理的值(过短的值会把正常输出替换坏)。
    private static bool IsWellFormedToken(string? token) =>
        token is { Length: >= 16 and <= 512 } && token.All(c => c is > ' ' and <= '~');

    private static byte[] CacheKey(string appId, string appSecret) =>
        SHA256.HashData(Encoding.UTF8.GetBytes(appId + "\n" + appSecret));

    private static string? Shorten(string? message)
    {
        string? masked = LarkDoctorReport.MaskIdentifiers(message);
        return masked is { Length: > 120 } ? masked[..120] + "…" : masked;
    }

    private static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static long? Number(JsonElement element, string name) =>
        element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out long number)
            ? number
            : null;
}
