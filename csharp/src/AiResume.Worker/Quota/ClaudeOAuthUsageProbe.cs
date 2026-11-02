using System.Diagnostics;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AiResume.Worker.Quota;

/// <summary>oauth/usage 的取数结果。Failed 时 Snapshot 为 null。</summary>
public sealed record OAuthUsageResult(
    bool Ok,
    UsageSnapshot? Snapshot,
    string? FailureReason,
    string CredentialFingerprint = "",
    string CredentialSource = "",
    string? Note = null);

/// <summary>
/// 通过 Claude Code 官方 OAuth usage 端点取配额。
///
/// 上游:GET https://api.anthropic.com/api/oauth/usage。凭据由 <see cref="ClaudeCredentialSource"/>
/// 只读解析 Claude Code 自己的登录文件。
///
/// 红线:
/// 1. 只读 token,绝不刷新、绝不写回 Claude Code 的凭据文件——续期由 Claude Code 自己完成;
/// 2. token 不进日志、不进异常消息、不进任何返回值;
/// 3. token 剩余寿命低于 60 秒视同过期 → 不发请求，直接降级;
/// 4. 凭据读不到 / JSON 损坏 / 无 access token → 降级，不抛。
/// </summary>
public sealed class ClaudeOAuthUsageProbe
{
    private readonly HttpClient _httpClient;
    private readonly ClaudeCredentialSource _credentials;
    private readonly string? _userAgent;

    /// <summary>
    /// 构造探测器。
    /// </summary>
    /// <param name="httpClient">可注入的 HttpClient(测试用假 handler);为 null 时自建(超时 15 秒)。</param>
    /// <param name="credentialsPath">Claude CLI 凭据文件路径;为 null 时用默认路径。</param>
    /// <param name="userAgent">显式 User-Agent;为 null 时按本机 Claude Code 版本解析。</param>
    /// <param name="credentials">凭据解析器;null 时按 <paramref name="credentialsPath"/> 构造(测试天然隔离)。</param>
    public ClaudeOAuthUsageProbe(
        HttpClient? httpClient = null,
        string? credentialsPath = null,
        string? userAgent = null,
        ClaudeCredentialSource? credentials = null)
    {
        _credentials = credentials ?? new ClaudeCredentialSource(credentialsPath);
        _userAgent = userAgent;

        if (httpClient is null)
        {
            _httpClient = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(15) };
        }
        else
        {
            // **绝不 dispose 注入进来的 HttpClient**:所有权在调用方。
            _httpClient = httpClient;
        }
    }

    /// <summary>凭据解析器(不含实值),供控制面显示登录状态。</summary>
    public ClaudeCredentialSource Credentials => _credentials;

    /// <summary>
    /// 尝试从 oauth/usage 端点取配额。
    /// 任何失败都返回 Ok == false 与分类后的 FailureReason,绝不抛异常。
    /// </summary>
    public async Task<OAuthUsageResult> TryFetchAsync(CancellationToken cancellationToken)
    {
        string credentialFingerprint = string.Empty;
        try
        {
            ClaudeCredential credential = _credentials.Read();
            credentialFingerprint = credential.CredentialFingerprint;
            if (!credential.Ok)
            {
                return new OAuthUsageResult(
                    false, null, credential.FailureReason, credential.CredentialFingerprint, credential.Source, credential.Detail);
            }

            return await FetchWithAsync(credential, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            // 兜底:任何未预期异常都归为 failed_local,绝不外泄 token 或响应正文。
            return new OAuthUsageResult(false, null, "failed_local", credentialFingerprint);
        }
    }

    /// <summary>Anthropic 的错误响应形状:<c>{"type":"error","error":{"type":…,"message":…}}</c>。其它正文(HTML、空)不算。</summary>
    internal static bool LooksLikeAnthropicError(string body)
    {
        if (string.IsNullOrWhiteSpace(body)) return false;
        try
        {
            using JsonDocument doc = JsonDocument.Parse(body);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return false;
            bool typed = doc.RootElement.TryGetProperty("type", out JsonElement type) &&
                         type.ValueKind == JsonValueKind.String && type.GetString() == "error";
            bool nested = doc.RootElement.TryGetProperty("error", out JsonElement error) && error.ValueKind == JsonValueKind.Object;
            return typed || nested;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>最多读 8 KB 正文：只用来判断错误形状，不保存、不记日志。</summary>
    private static async Task<string> ReadBoundedAsync(HttpContent content, CancellationToken cancellationToken)
    {
        try
        {
            using Stream stream = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            byte[] buffer = new byte[8192];
            int total = 0;
            while (total < buffer.Length)
            {
                int read = await stream.ReadAsync(buffer.AsMemory(total, buffer.Length - total), cancellationToken).ConfigureAwait(false);
                if (read <= 0) break;
                total += read;
            }

            return Encoding.UTF8.GetString(buffer, 0, total);
        }
        catch (Exception ex) when (ex is IOException or HttpRequestException or ObjectDisposedException)
        {
            return string.Empty;
        }
    }

    private async Task<OAuthUsageResult> FetchWithAsync(ClaudeCredential credential, CancellationToken cancellationToken)
    {
        string fingerprint = credential.CredentialFingerprint;
        string source = credential.Source;
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.anthropic.com/api/oauth/usage");
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", credential.AccessToken);
        request.Headers.Accept.Add(new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.TryAddWithoutValidation("anthropic-beta", "oauth-2025-04-20");
        request.Headers.TryAddWithoutValidation("anthropic-version", "2023-06-01");
        // 该端点会把普通 HttpClient UA 放进更激进的 429 桶。Claude Code 自身及
        // 成熟的额度工具都使用 claude-code/<version>;版本从本机二进制只读解析,
        // 取不到时退回兼容基线。解析发生在后台请求路径,不阻塞 WPF 首帧。
        request.Headers.TryAddWithoutValidation("User-Agent", _userAgent ?? ResolveClaudeCodeUserAgent());

        HttpResponseMessage response;
        try
        {
            response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException)
        {
            return new OAuthUsageResult(false, null, "failed_local", fingerprint, source);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (TaskCanceledException)
        {
            // HttpClient 自身超时属于本地探测失败;调用方显式取消在上一分支传播。
            return new OAuthUsageResult(false, null, "failed_local", fingerprint, source);
        }
        catch (System.Net.Sockets.SocketException)
        {
            return new OAuthUsageResult(false, null, "failed_local", fingerprint, source);
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                int status = (int)response.StatusCode;
                // 401 一定是凭据问题;403 还可能来自 CDN/网关/代理拦截，只有 Anthropic 自己的结构化错误体
                // 才算「登录无权读取额度」——否则会把网络问题误报成要用户重新 /login。
                string reason = status switch
                {
                    401 => "token_rejected_401",
                    403 => LooksLikeAnthropicError(await ReadBoundedAsync(response.Content, cancellationToken).ConfigureAwait(false))
                        ? "token_rejected_403"
                        : "http_403_gateway",
                    408 or 504 => "gateway_timeout",
                    _ => $"http_{status}",
                };
                return new OAuthUsageResult(false, null, reason, fingerprint, source);
            }

            OAuthUsageResponse? payload;
            try
            {
                payload = await response.Content.ReadFromJsonAsync<OAuthUsageResponse>(
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true },
                    cancellationToken).ConfigureAwait(false);
            }
            catch (JsonException)
            {
                return new OAuthUsageResult(false, null, "malformed_response", fingerprint, source);
            }
            catch (NotSupportedException)
            {
                return new OAuthUsageResult(false, null, "malformed_response", fingerprint, source);
            }

            if (payload is null)
            {
                return new OAuthUsageResult(false, null, "malformed_response", fingerprint, source);
            }

            return new OAuthUsageResult(true, MapToSnapshot(payload), null, fingerprint, source);
        }
    }

    /// <summary>当前会被用于请求的凭据。失败时返回分类原因,不抛异常;实值只在调用栈内流动。</summary>
    public ClaudeCredential ReadAccessToken() => _credentials.Read();

    /// <summary>
    /// 把 oauth/usage 响应映射成 UsageSnapshot。
    /// 两个主窗口(five_hour / seven_day)用于**显示**;
    /// 是否被限流则由 <c>limits</c> 数组决定 —— 见 <see cref="IsLimitReached"/>。
    /// </summary>
    private static UsageSnapshot MapToSnapshot(OAuthUsageResponse payload)
    {
        var now = DateTimeOffset.UtcNow;
        var windows = new List<UsageWindow>(2);

        OAuthLimit? session = FindLimit(payload.Limits, "session");
        OAuthLimit? weekly = FindLimit(payload.Limits, "weekly_all");
        AppendWindow(windows, "five_hour", UsageWindow.FiveHourSeconds, payload.FiveHour, session, now);
        AppendWindow(windows, "seven_day", UsageWindow.SevenDaySeconds, payload.SevenDay, weekly, now);

        // **按模型限定的额度也要露出来。** 光有两个总窗口的话,面板会出现
        // "7 天还剩 7%、显示正常,而 Fable 已经一个 token 都发不出去"这种自相矛盾
        // (2026-08-08 审计实测)。把 weekly_scoped 也做成一个窗口,
        // 用户才看得见"到底是哪一条打满了"。
        AppendScopedWindows(windows, payload.Limits, now);

        // 限流结论必须来自规范化后的逻辑窗口。直接扫描原始 limits 会让同一 scope
        // 的旧 reset/100% 在已被新 reset/0% 取代后继续污染 bucket。未知 kind 没有
        // 对应窗口,仍保守保留其明确 100% 结论。
        bool unknownLimitReached = payload.Limits?.Any(limit =>
            !IsKnownLimitKind(limit.Kind) && limit.Percent is >= 100) == true;
        bool limitReached = windows.Any(window => window.UsedPercent is >= 100) || unknownLimitReached;
        var bucket = new UsageBucket("Usage", !limitReached, limitReached, windows)
        {
            UnattributedLimitReached = unknownLimitReached,
        };

        string? unavailable = windows.Count > 0 ? null : "oauth/usage 未返回任何限额窗口";
        return new UsageSnapshot("claudecode", now, new[] { bucket }, unavailable);
    }

    private static void AppendWindow(
        List<UsageWindow> windows,
        string name,
        int windowSeconds,
        OAuthWindow? window,
        OAuthLimit? modernLimit,
        DateTimeOffset now)
    {
        if (window is null && modernLimit is null)
        {
            return;
        }

        // 现代 limits 是 Claude Code 当前 /usage 的主形状;旧顶层窗口继续兼容。
        // 两边都缺 percent 才是"未报告",绝不当 0。
        int? usedPercent = null;
        if (modernLimit?.Percent is { } modernPercent)
        {
            usedPercent = Math.Clamp((int)Math.Round(modernPercent), 0, 100);
        }
        else if (window?.Utilization is { } legacyUtil)
        {
            usedPercent = Math.Clamp((int)Math.Round(legacyUtil), 0, 100);
        }

        // resets_at 可能是 ISO 8601 字符串,也可能是 epoch 数字(秒)。
        long? resetAtUnix = ParseResetAt(modernLimit?.ResetsAt) ?? ParseResetAt(window?.ResetsAt);

        // 现代/legacy 空对象不是一个“0% 的窗口”,也不是健康证据。
        if (usedPercent is null && resetAtUnix is null)
        {
            return;
        }

        int? resetAfterSeconds = null;
        if (resetAtUnix is { } unix)
        {
            long delta = unix - now.ToUnixTimeSeconds();
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
    /// 把 <c>limits</c> 里所有 <c>kind = "weekly_scoped"</c> 条目做成窗口。
    /// 不得只取第一条:服务端可以同时给多个模型限额,真正打满的可能在后面。
    /// </summary>
    private static void AppendScopedWindows(
        List<UsageWindow> windows,
        List<OAuthLimit>? limits,
        DateTimeOffset now)
    {
        if (limits is null)
        {
            return;
        }

        var candidates = new Dictionary<string, ScopedWindowCandidate>(StringComparer.Ordinal);
        foreach (OAuthLimit scoped in limits.Where(limit =>
                     string.Equals(limit.Kind, "weekly_scoped", StringComparison.OrdinalIgnoreCase)))
        {
            int? used = scoped.Percent is { } percent
                ? Math.Clamp((int)Math.Round(percent), 0, 100)
                : null;
            long? resetAtUnix = ParseResetAt(scoped.ResetsAt);
            int? resetAfterSeconds = null;
            if (resetAtUnix is { } unix)
            {
                long delta = unix - now.ToUnixTimeSeconds();
                resetAfterSeconds = delta <= 0 ? 0 : (int)Math.Min(delta, int.MaxValue);
            }

            if (used is null && resetAtUnix is null)
            {
                continue;
            }

            string model = ReadScopeModel(scoped.Scope);
            string baseName = model.Length > 0 ? "weekly_scoped:" + model : "weekly_scoped:未命名模型";
            string identity = BuildScopeIdentity(scoped.Scope);
            var candidate = new ScopedWindowCandidate(baseName, identity, used, resetAtUnix, resetAfterSeconds);
            if (candidates.TryGetValue(identity, out ScopedWindowCandidate? existing))
            {
                // 完全相同的 scope 是同一逻辑额度。若服务端重复下发,同 reset 取更高
                // 用量;reset 不同取更新的代次,避免重复行和顺序依赖。
                candidate = MergeDuplicateScope(existing, candidate);
            }
            candidates[identity] = candidate;
        }

        Dictionary<string, int> duplicateNames = candidates.Values
            .GroupBy(candidate => candidate.BaseName, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.OrdinalIgnoreCase);
        foreach (ScopedWindowCandidate candidate in candidates.Values)
        {
            string name = duplicateNames[candidate.BaseName] == 1
                ? candidate.BaseName
                : candidate.BaseName + "#" + candidate.Identity[^6..];
            windows.Add(new UsageWindow(
                name,
                candidate.UsedPercent is >= 100 ? "blocked" : "allowed",
                UsageWindow.SevenDaySeconds,
                candidate.ResetAtUnix,
                candidate.ResetAfterSeconds,
                candidate.UsedPercent,
                Identity: candidate.Identity));
        }
    }

    private static ScopedWindowCandidate MergeDuplicateScope(
        ScopedWindowCandidate existing,
        ScopedWindowCandidate candidate)
    {
        if (existing.ResetAtUnix != candidate.ResetAtUnix)
        {
            return candidate.ResetAtUnix is not null &&
                   (existing.ResetAtUnix is null || candidate.ResetAtUnix > existing.ResetAtUnix)
                ? candidate
                : existing;
        }

        int? used = existing.UsedPercent is { } first && candidate.UsedPercent is { } second
            ? Math.Max(first, second)
            : existing.UsedPercent ?? candidate.UsedPercent;
        int? resetAfter = existing.ResetAfterSeconds is { } existingAfter &&
                          candidate.ResetAfterSeconds is { } candidateAfter
            ? Math.Min(existingAfter, candidateAfter)
            : existing.ResetAfterSeconds ?? candidate.ResetAfterSeconds;
        return existing with
        {
            UsedPercent = used,
            ResetAfterSeconds = resetAfter,
        };
    }

    private static string BuildScopeIdentity(JsonElement? scope)
    {
        if (scope is not { } value || value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return "weekly_scoped:" + Convert.ToHexString(SHA256.HashData("null"u8));
        }

        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            WriteCanonicalJson(writer, value);
        }
        return "weekly_scoped:" + Convert.ToHexString(SHA256.HashData(stream.ToArray()));
    }

    private static void WriteCanonicalJson(Utf8JsonWriter writer, JsonElement value)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (JsonProperty property in value.EnumerateObject().OrderBy(
                             property => property.Name, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(property.Name);
                    WriteCanonicalJson(writer, property.Value);
                }
                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (JsonElement item in value.EnumerateArray())
                {
                    WriteCanonicalJson(writer, item);
                }
                writer.WriteEndArray();
                break;
            default:
                value.WriteTo(writer);
                break;
        }
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

    /// <summary>
    /// 从 <c>scope</c> 取模型显示名。实测形状:
    /// <c>{"model":{"id":null,"display_name":"Fable"},"surface":null}</c>。
    /// 取不到就返回空串,由调用方退回不带模型名的通用写法 —— 不猜。
    /// </summary>
    public static string ReadScopeModel(JsonElement? scope)
    {
        if (scope is not { ValueKind: JsonValueKind.Object } s ||
            !s.TryGetProperty("model", out JsonElement m) ||
            m.ValueKind != JsonValueKind.Object ||
            !m.TryGetProperty("display_name", out JsonElement d) ||
            d.ValueKind != JsonValueKind.String)
        {
            return string.Empty;
        }

        string? name = d.GetString();
        // 冒号是我们拿来拼窗口名的分隔符,出现在模型名里会把解析弄乱。
        return string.IsNullOrWhiteSpace(name) ? string.Empty : name.Replace(":", string.Empty).Trim();
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

    private sealed record ScopedWindowCandidate(
        string BaseName,
        string Identity,
        int? UsedPercent,
        long? ResetAtUnix,
        int? ResetAfterSeconds);

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

    private static OAuthLimit? FindLimit(List<OAuthLimit>? limits, string kind)
    {
        OAuthLimit? selected = null;
        foreach (OAuthLimit candidate in limits?.Where(limit =>
                     string.Equals(limit.Kind, kind, StringComparison.OrdinalIgnoreCase)) ??
                 Enumerable.Empty<OAuthLimit>())
        {
            if (selected is null)
            {
                selected = candidate;
                continue;
            }

            long? selectedReset = ParseResetAt(selected.ResetsAt);
            long? candidateReset = ParseResetAt(candidate.ResetsAt);
            if (selectedReset != candidateReset)
            {
                if (candidateReset is not null &&
                    (selectedReset is null || candidateReset > selectedReset))
                {
                    selected = candidate;
                }
                continue;
            }

            if (candidate.Percent is { } candidatePercent &&
                (selected.Percent is null || candidatePercent > selected.Percent))
            {
                selected = candidate;
            }
        }
        return selected;
    }

    private static bool IsKnownLimitKind(string? kind) =>
        string.Equals(kind, "session", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(kind, "weekly_all", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(kind, "weekly_scoped", StringComparison.OrdinalIgnoreCase);

    internal static string ResolveClaudeCodeUserAgent()
    {
        try
        {
            var candidates = new List<string>
            {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "bin", "claude.exe"),
            };
            string? path = Environment.GetEnvironmentVariable("PATH");
            if (!string.IsNullOrEmpty(path))
            {
                candidates.AddRange(path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
                    .Select(directory => Path.Combine(directory.Trim(), "claude.exe")));
            }

            string? executable = candidates.FirstOrDefault(File.Exists);
            string? rawVersion = executable is null
                ? null
                : FileVersionInfo.GetVersionInfo(executable).ProductVersion;
            if (Version.TryParse(rawVersion, out Version? version))
            {
                int build = Math.Max(0, version.Build);
                return $"claude-code/{version.Major}.{version.Minor}.{build}";
            }
        }
        catch (Exception)
        {
            // UA 解析只是反限流兼容信息;失败不应阻断额度请求。
        }

        return "claude-code/2.0.0";
    }

}
