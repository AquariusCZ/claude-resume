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
    string CredentialFingerprint = "");

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
    private readonly string? _userAgent;

    /// <summary>
    /// 构造探测器。
    /// </summary>
    /// <param name="httpClient">可注入的 HttpClient(测试用假 handler);为 null 时自建(超时 15 秒)。</param>
    /// <param name="credentialsPath">凭据文件路径;为 null 时用默认路径。</param>
    public ClaudeOAuthUsageProbe(
        HttpClient? httpClient = null,
        string? credentialsPath = null,
        string? userAgent = null)
    {
        _credentialsPath = credentialsPath ?? DefaultCredentialsPath;
        _userAgent = userAgent;

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
        string credentialFingerprint = string.Empty;
        try
        {
            // 1. 读凭据
            var tokenResult = ReadAccessToken();
            credentialFingerprint = tokenResult.CredentialFingerprint;
            if (!tokenResult.Ok)
            {
                return new OAuthUsageResult(
                    false, null, tokenResult.FailureReason, tokenResult.CredentialFingerprint);
            }

            // 2. 发请求
            using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.anthropic.com/api/oauth/usage");
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", tokenResult.AccessToken);
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
                return new OAuthUsageResult(false, null, "failed_local", tokenResult.CredentialFingerprint);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (TaskCanceledException)
            {
                // HttpClient 自身超时属于本地探测失败;调用方显式取消在上一分支传播。
                return new OAuthUsageResult(false, null, "failed_local", tokenResult.CredentialFingerprint);
            }
            catch (System.Net.Sockets.SocketException)
            {
                return new OAuthUsageResult(false, null, "failed_local", tokenResult.CredentialFingerprint);
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
                    return new OAuthUsageResult(false, null, reason, tokenResult.CredentialFingerprint);
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
                    return new OAuthUsageResult(
                        false, null, "malformed_response", tokenResult.CredentialFingerprint);
                }
                catch (NotSupportedException)
                {
                    return new OAuthUsageResult(
                        false, null, "malformed_response", tokenResult.CredentialFingerprint);
                }

                if (payload is null)
                {
                    return new OAuthUsageResult(
                        false, null, "malformed_response", tokenResult.CredentialFingerprint);
                }

                // 5. 映射到 UsageSnapshot
                var snapshot = MapToSnapshot(payload);
                return new OAuthUsageResult(true, snapshot, null, tokenResult.CredentialFingerprint);
            }
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

    /// <summary>读取凭据文件并提取 access token。失败时返回分类原因,不抛异常。</summary>
    private (bool Ok, string? AccessToken, string CredentialFingerprint, string? FailureReason) ReadAccessToken()
    {
        string json;
        try
        {
            if (!File.Exists(_credentialsPath))
            {
                return (false, null, string.Empty, "no_credentials");
            }

            json = File.ReadAllText(_credentialsPath);
        }
        catch (Exception)
        {
            // 读不到文件(权限、IO 等)一律降级。
            return (false, null, string.Empty, "no_credentials");
        }

        CredentialsFile? credentials;
        try
        {
            credentials = JsonSerializer.Deserialize<CredentialsFile>(json);
        }
        catch (JsonException)
        {
            return (false, null, string.Empty, "no_credentials");
        }

        if (credentials?.ClaudeAiOauth?.AccessToken is not { Length: > 0 } accessToken)
        {
            return (false, null, string.Empty, "no_credentials");
        }

        // organizationUuid 是账号级稳定身份,access token 轮换时不应让同一账号的
        // 最近权威快照凭空消失。旧版/异常凭据没有该字段时才退回 token 指纹。
        string identityMaterial = !string.IsNullOrWhiteSpace(credentials.OrganizationUuid)
            ? "organization:" + credentials.OrganizationUuid.Trim()
            : "token:" + accessToken;
        string credentialFingerprint = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(identityMaterial)));

        // token 剩余寿命低于 60 秒视同过期。
        if (credentials.ClaudeAiOauth.ExpiresAt is { } expiresAtMs)
        {
            long nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            long remainingMs = expiresAtMs - nowMs;
            if (remainingMs < TokenExpiryGraceSeconds * 1000)
            {
                return (false, null, credentialFingerprint, "token_expired");
            }
        }

        return (true, accessToken, credentialFingerprint, null);
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

    /// <summary>凭据文件形状。organizationUuid 只用于不可逆账号指纹。</summary>
    private sealed class CredentialsFile
    {
        [JsonPropertyName("claudeAiOauth")]
        public ClaudeAiOauth? ClaudeAiOauth { get; set; }

        [JsonPropertyName("organizationUuid")]
        public string? OrganizationUuid { get; set; }
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

    private static string ResolveClaudeCodeUserAgent()
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

    private sealed class ClaudeAiOauth
    {
        [JsonPropertyName("accessToken")]
        public string? AccessToken { get; set; }

        [JsonPropertyName("expiresAt")]
        public long? ExpiresAt { get; set; }
    }
}
