using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace AiResume.Worker.Probes;

/// <summary>探测判定分类。与 <see cref="CodexProbe"/> 的语义对齐。</summary>
public enum ProviderReadiness
{
    Ok,
    NoCredential,
    Auth,
    Insufficient,
    Unreachable,
    Timeout,
    Unknown,
}

/// <summary>
/// DeepSeek 探测结果。<see cref="Summary"/> 可直接显示,**不含密钥、不含 URL**。
/// </summary>
public sealed record DeepSeekProbeResult(
    ProviderReadiness Readiness,
    string Reason,
    string? Summary,
    decimal? BalanceCny);

/// <summary>
/// DeepSeek 可用性探测:查余额。
///
/// **为什么查余额而不是发一次最小对话请求**:DeepSeek 是按量计费、没有窗口概念,
/// 「还剩多少」对它就是余额;而且余额接口**不消耗任何 token**,可以随面板刷新随便调,
/// 发对话请求则每次都要花钱。同样是"真实请求成功"才给绿灯,这条更便宜。
///
/// 端点形状为 2026-08-07 实测:
/// <c>GET https://api.deepseek.com/user/balance</c>,Bearer 鉴权,返回
/// <c>{"is_available":true,"balance_infos":[{"currency":"CNY","total_balance":"48.23",…}]}</c>。
///
/// **密钥只从环境变量读**,不落仓库、不进日志、不回显给界面。
/// 刻意不去读 cc-connect 的 config.toml:那是它的资产,跨进程掏别人的凭据文件既越界、
/// 又会在对方改格式时静默失效。
/// </summary>
public sealed class DeepSeekProbe
{
    public const string ApiKeyEnvName = "DEEPSEEK_API_KEY";

    private const string BalanceUrl = "https://api.deepseek.com/user/balance";

    private readonly Func<string?> _apiKey;
    private readonly HttpMessageHandler? _handler;
    private readonly TimeSpan _timeout;

    /// <param name="apiKey">密钥提供者;默认读环境变量。测试注入假值,**绝不真调网络**。</param>
    /// <param name="handler">HTTP 处理器;测试注入桩。</param>
    public DeepSeekProbe(Func<string?>? apiKey = null, HttpMessageHandler? handler = null, TimeSpan? timeout = null)
    {
        _apiKey = apiKey ?? (() => Environment.GetEnvironmentVariable(ApiKeyEnvName));
        _handler = handler;
        _timeout = timeout ?? TimeSpan.FromSeconds(15);
    }

    public async Task<DeepSeekProbeResult> ProbeAsync(CancellationToken ct = default)
    {
        string? key = _apiKey();
        if (string.IsNullOrWhiteSpace(key))
        {
            // 没配密钥不是故障,是"没启用"。灰灯,不报红。
            return new DeepSeekProbeResult(ProviderReadiness.NoCredential, "no-key",
                $"未设置 {ApiKeyEnvName}", null);
        }

        using HttpClient http = _handler is null ? new HttpClient() : new HttpClient(_handler, disposeHandler: false);
        http.Timeout = _timeout;

        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, BalanceUrl);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);

            using HttpResponseMessage resp = await http.SendAsync(req, ct).ConfigureAwait(false);

            if (resp.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                return new DeepSeekProbeResult(ProviderReadiness.Auth, "http-" + (int)resp.StatusCode,
                    "密钥被拒绝", null);
            }

            if (!resp.IsSuccessStatusCode)
            {
                return new DeepSeekProbeResult(ProviderReadiness.Unknown, "http-" + (int)resp.StatusCode,
                    $"服务端返回 {(int)resp.StatusCode}", null);
            }

            string body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            return Parse(body);
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            // HttpClient 的超时也走 TaskCanceledException;调用方主动取消要区分开。
            return new DeepSeekProbeResult(ProviderReadiness.Timeout, "timeout", "探测超时", null);
        }
        catch (OperationCanceledException)
        {
            return new DeepSeekProbeResult(ProviderReadiness.Unknown, "cancelled", null, null);
        }
        catch (HttpRequestException)
        {
            // DNS/TCP/TLS 一律归本地网络问题,不冒充"服务端故障"。
            return new DeepSeekProbeResult(ProviderReadiness.Unreachable, "unreachable", "网络不可达", null);
        }
    }

    /// <summary>
    /// 解析余额响应。形状变了也不能抛——探测失败可以,拖崩调用方不行。
    /// </summary>
    /// <remarks>仅为可测性公开:测试项目未配 InternalsVisibleTo,而这是唯一值得测的地方
    /// (网络那半段一律注入桩,测试绝不真调 DeepSeek)。</remarks>
    public static DeepSeekProbeResult Parse(string body)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(body);
            JsonElement root = doc.RootElement;

            bool available = !root.TryGetProperty("is_available", out JsonElement av)
                             || av.ValueKind != JsonValueKind.False;

            decimal? cny = null;
            if (root.TryGetProperty("balance_infos", out JsonElement infos) &&
                infos.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement info in infos.EnumerateArray())
                {
                    if (!info.TryGetProperty("currency", out JsonElement cur) ||
                        !string.Equals(cur.GetString(), "CNY", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    // total_balance 是**字符串**("48.23"),不是数字 —— 实测如此。
                    // 直接 GetDecimal() 会抛。
                    if (info.TryGetProperty("total_balance", out JsonElement tb) &&
                        decimal.TryParse(tb.GetString(), NumberStyles.Number, CultureInfo.InvariantCulture, out decimal v))
                    {
                        cny = v;
                    }

                    break;
                }
            }

            if (!available)
            {
                return new DeepSeekProbeResult(ProviderReadiness.Insufficient, "unavailable",
                    cny is null ? "账户不可用" : $"账户不可用(余额 ¥{cny:0.##})", cny);
            }

            if (cny is null)
            {
                // 服务端说可用但没给 CNY 余额:如实报"可用",别编一个数字出来。
                return new DeepSeekProbeResult(ProviderReadiness.Ok, "ok", "可用", null);
            }

            return new DeepSeekProbeResult(ProviderReadiness.Ok, "ok", $"余额 ¥{cny:0.##}", cny);
        }
        catch (JsonException)
        {
            return new DeepSeekProbeResult(ProviderReadiness.Unknown, "malformed", "响应无法解析", null);
        }
    }
}
