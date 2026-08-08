using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AiResume.Worker.Quota;

/// <summary>oauth/usage 的取数结果。Failed 时 Snapshot 为 null。</summary>
public sealed record OAuthUsageResult(bool Ok, UsageSnapshot? Snapshot, string? FailureReason);

/// <summary>
/// 通过 Claude Code 官方 OAuth usage 端点取配额。
///
/// 上游:GET https://api.anthropic.com/api/oauth/usage,凭据为 Claude Code 已有的
/// OAuth access token(Windows 上位于 %USERPROFILE%\.claude\.credentials.json)。
///
/// 红线(照抄参考实现约束):
/// 1. 只读 token,绝不刷新、绝不写回凭据文件——续期由 Claude Code 自己完成;
/// 2. token 不进日志、不进异常消息、不进任何返回值;
/// 3. token 剩余寿命低于 60 秒视同过期 → 不发请求,直接降级;
/// 4. 凭据文件读不到 / JSON 损坏 / 无 claudeAiOauth.accessToken → 降级,不抛;
/// 5. 降级目标是现有的 ClaudeCodeProbe(PTY/子进程探测),不删除它。
/// </summary>
public sealed class ClaudeOAuthUsageProbe
{
    /// <summary>token 剩余寿命低于此秒数视同过期,不发请求。</summary>
    private const long TokenExpiryGraceSeconds = 60;

    /// <summary>默认凭据文件路径: %USERPROFILE%\.claude\.credentials.json。</summary>
    private static readonly string DefaultCredentialsPath =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", ".credentials.json");

    private readonly HttpClient _httpClient;
    private readonly string _credentialsPath;

    /// <summary>
    /// 构造探测器。
    /// </summary>
    /// <param name="httpClient">可注入的 HttpClient(测试用假 handler);为 null 时自建(超时 15 秒)。</param>
    /// <param name="credentialsPath">凭据文件路径;为 null 时用默认路径。</param>
    public ClaudeOAuthUsageProbe(HttpClient? httpClient = null, string? credentialsPath = null)
    {
        _credentialsPath = credentialsPath ?? DefaultCredentialsPath;

        if (httpClient is null)
        {
            _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        }
        else
        {
            // **绝不 dispose 注入进来的 HttpClient**:所有权在调用方。
            _httpClient = httpClient;
        }
    }

    /// <summary>
    /// 尝试从 oauth/usage 端点取配额。
    /// 任何失败都返回 Ok == false 与分类后的 FailureReason,绝不抛异常。
    /// </summary>
    public async Task<OAuthUsageResult> TryFetchAsync(CancellationToken cancellationToken)
    {
        try
        {
            // 1. 读凭据
            var tokenResult = ReadAccessToken();
            if (!tokenResult.Ok)
            {
                return new OAuthUsageResult(false, null, tokenResult.FailureReason);
            }

            // 2. 发请求
            using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.anthropic.com/api/oauth/usage");
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", tokenResult.AccessToken);

            HttpResponseMessage response;
            try
            {
                response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
            }
            catch (HttpRequestException)
            {
                return new OAuthUsageResult(false, null, "failed_local");
            }
            catch (TaskCanceledException)
            {
                // 超时或取消。取消时也归为 failed_local(调用方可通过 cancellationToken 区分)。
                return new OAuthUsageResult(false, null, "failed_local");
            }
            catch (System.Net.Sockets.SocketException)
            {
                return new OAuthUsageResult(false, null, "failed_local");
            }

            using (response)
            {
                // 3. 分类非 2xx
                if (!response.IsSuccessStatusCode)
                {
                    int status = (int)response.StatusCode;
                    string reason = status switch
                    {
                        401 or 403 => $"token_rejected_{status}",
                        408 or 504 => "gateway_timeout",
                        _ => $"http_{status}",
                    };
                    return new OAuthUsageResult(false, null, reason);
                }

                // 4. 解析 JSON
                OAuthUsageResponse? payload;
                try
                {
                    payload = await response.Content.ReadFromJsonAsync<OAuthUsageResponse>(
                        new JsonSerializerOptions { PropertyNameCaseInsensitive = true },
                        cancellationToken).ConfigureAwait(false);
                }
                catch (JsonException)
                {
                    return new OAuthUsageResult(false, null, "malformed_response");
                }
                catch (NotSupportedException)
                {
                    return new OAuthUsageResult(false, null, "malformed_response");
                }

                if (payload is null)
                {
                    return new OAuthUsageResult(false, null, "malformed_response");
                }

                // 5. 映射到 UsageSnapshot
                var snapshot = MapToSnapshot(payload);
                return new OAuthUsageResult(true, snapshot, null);
            }
        }
        catch (Exception)
        {
            // 兜底:任何未预期异常都归为 failed_local,绝不外泄 token 或响应正文。
            return new OAuthUsageResult(false, null, "failed_local");
        }
    }

    /// <summary>读取凭据文件并提取 access token。失败时返回分类原因,不抛异常。</summary>
    private (bool Ok, string? AccessToken, string? FailureReason) ReadAccessToken()
    {
        string json;
        try
        {
            if (!File.Exists(_credentialsPath))
            {
                return (false, null, "no_credentials");
            }

            json = File.ReadAllText(_credentialsPath);
        }
        catch (Exception)
        {
            // 读不到文件(权限、IO 等)一律降级。
            return (false, null, "no_credentials");
        }

        CredentialsFile? credentials;
        try
        {
            credentials = JsonSerializer.Deserialize<CredentialsFile>(json);
        }
        catch (JsonException)
        {
            return (false, null, "no_credentials");
        }

        if (credentials?.ClaudeAiOauth?.AccessToken is not { Length: > 0 } accessToken)
        {
            return (false, null, "no_credentials");
        }

        // token 剩余寿命低于 60 秒视同过期。
        if (credentials.ClaudeAiOauth.ExpiresAt is { } expiresAtMs)
        {
            long nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            long remainingMs = expiresAtMs - nowMs;
            if (remainingMs < TokenExpiryGraceSeconds * 1000)
            {
                return (false, null, "token_expired");
            }
        }

        return (true, accessToken, null);
    }

    /// <summary>
    /// 把 oauth/usage 响应映射成 UsageSnapshot。
    /// 两个主窗口(five_hour / seven_day)用于**显示**;
    /// 是否被限流则由 <c>limits</c> 数组决定 —— 见 <see cref="IsLimitReached"/>。
    /// </summary>
    private static UsageSnapshot MapToSnapshot(OAuthUsageResponse payload)
    {
        var now = DateTimeOffset.UtcNow;
        var windows = new List<UsageWindow>(2);

        AppendWindow(windows, "five_hour", UsageWindow.FiveHourSeconds, payload.FiveHour);
        AppendWindow(windows, "seven_day", UsageWindow.SevenDaySeconds, payload.SevenDay);

        bool limitReached = IsLimitReached(
            windows.Select(w => w.UsedPercent).ToList(),
            payload.Limits?.Select(l => l.Percent).ToList() ?? (IReadOnlyList<double?>)Array.Empty<double?>());
        var bucket = new UsageBucket("Usage", !limitReached, limitReached, windows);

        string? unavailable = windows.Count > 0 ? null : "oauth/usage 未返回任何限额窗口";
        return new UsageSnapshot("claudecode", now, new[] { bucket }, unavailable);
    }

    private static void AppendWindow(List<UsageWindow> windows, string name, int windowSeconds, OAuthWindow? window)
    {
        if (window is null)
        {
            return;
        }

        // utilization 缺失/为 null → UsedPercent 为 null(表示"未报告"),绝不当 0。
        int? usedPercent = null;
        if (window.Utilization is { } util)
        {
            usedPercent = Math.Clamp((int)Math.Round(util), 0, 100);
        }

        // resets_at 可能是 ISO 8601 字符串,也可能是 epoch 数字(秒)。
        long? resetAtUnix = ParseResetAt(window.ResetsAt);

        int? resetAfterSeconds = null;
        if (resetAtUnix is { } unix)
        {
            long delta = unix - DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            resetAfterSeconds = delta <= 0 ? 0 : (int)Math.Min(delta, int.MaxValue);
        }

        string status = usedPercent is { } p && p >= 100 ? "blocked" : "allowed";

        windows.Add(new UsageWindow(
            name,
            status,
            windowSeconds,
            resetAtUnix,
            resetAfterSeconds,
            usedPercent));
    }

    /// <summary>
    /// 判断是否已被限流。**任意一条限额打满就算限流**,不只是两个主窗口。
    ///
    /// 2026-08-08 审计实测的反例:seven_day 93%(面板显示"正常、可运行"),
    /// 而同一时刻 <c>weekly_scoped</c> 已是 100%,真实 Fable 任务直接被拒。
    /// 这个产品存在的全部理由就是"知道什么时候被限流" ——
    /// **漏判一条已经打满的限额,比多等一会儿严重得多**,所以这里取最保守的读法。
    ///
    /// 只认 percent >= 100,不认 <c>severity = "critical"</c>:
    /// 实测 93% 也标 critical,那是"快满了"的预警,不是"已经不能跑"。
    /// 拿它当限流会让引擎在还能跑的时候白等。
    /// </summary>
    /// <param name="windowPercents">两个主窗口的 used percent(null = 未报告)。</param>
    /// <param name="limitPercents">
    /// <c>limits</c> 数组里每条的 percent(null = 该条未报告,跳过而不是当 0 或 100)。
    /// </param>
    public static bool IsLimitReached(
        IReadOnlyList<int?> windowPercents, IReadOnlyList<double?> limitPercents)
    {
        foreach (int? p in windowPercents)
        {
            if (p is { } v && v >= 100)
            {
                return true;
            }
        }

        foreach (double? p in limitPercents)
        {
            if (p is { } v && v >= 100)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>解析 resets_at:支持 ISO 8601 字符串与 epoch 秒数字。</summary>
    private static long? ParseResetAt(object? resetsAt)
    {
        return resetsAt switch
        {
            null => null,
            JsonElement { ValueKind: JsonValueKind.Number } number when number.TryGetInt64(out long epoch) => epoch,
            JsonElement { ValueKind: JsonValueKind.String } str => ParseIso8601(str.GetString()),
            string s => ParseIso8601(s),
            _ => null,
        };
    }

    private static long? ParseIso8601(string? s)
    {
        if (string.IsNullOrWhiteSpace(s))
        {
            return null;
        }

        if (DateTimeOffset.TryParse(s, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AssumeUniversal, out var dto))
        {
            return dto.ToUnixTimeSeconds();
        }

        return null;
    }

    /// <summary>oauth/usage 响应中单个窗口的形状。</summary>
    private sealed class OAuthWindow
    {
        [JsonPropertyName("utilization")]
        public double? Utilization { get; set; }

        [JsonPropertyName("resets_at")]
        public object? ResetsAt { get; set; }
    }

    /// <summary>
    /// <c>limits</c> 数组里的一条。**这是判断"到底还能不能跑"的权威来源。**
    ///
    /// 服务端除了 five_hour / seven_day 两个总窗口,还会下发**按模型限定**的额度
    /// (<c>kind = "weekly_scoped"</c>,带 <c>scope.model</c>)。两者可以差得很远。
    /// </summary>
    internal sealed class OAuthLimit
    {
        [JsonPropertyName("kind")]
        public string? Kind { get; set; }

        [JsonPropertyName("percent")]
        public double? Percent { get; set; }

        [JsonPropertyName("severity")]
        public string? Severity { get; set; }

        [JsonPropertyName("resets_at")]
        public object? ResetsAt { get; set; }

        [JsonPropertyName("scope")]
        public JsonElement? Scope { get; set; }
    }

    /// <summary>oauth/usage 响应整体形状。只映射本轮需要的字段。</summary>
    private sealed class OAuthUsageResponse
    {
        [JsonPropertyName("five_hour")]
        public OAuthWindow? FiveHour { get; set; }

        [JsonPropertyName("seven_day")]
        public OAuthWindow? SevenDay { get; set; }

        /// <summary>
        /// **2026-08-08 审计实测:只看 five_hour / seven_day 会漏判限流。**
        /// 当时 seven_day 报 93%、面板显示"正常、可运行",而同一时刻真实 Fable 任务
        /// 直接返回 "You've hit your limit"。原因在这个数组里:
        /// <code>
        /// {"kind":"session",       "percent":4,   "severity":"normal"}
        /// {"kind":"weekly_all",    "percent":93,  "severity":"critical"}
        /// {"kind":"weekly_scoped", "percent":100, "severity":"critical", "scope":{"model":…}}
        /// </code>
        /// **weekly_scoped 已经 100%** —— 按模型限定的额度耗尽了,而总额度还有 7%。
        /// 这个产品存在的全部理由就是"知道什么时候被限流",漏掉一条已经打满的限额
        /// 是最不能接受的一类错误。
        /// </summary>
        [JsonPropertyName("limits")]
        public List<OAuthLimit>? Limits { get; set; }
    }

    /// <summary>凭据文件形状。只读取 claudeAiOauth.accessToken 与 expiresAt。</summary>
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
    }
}