using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AiResume.Worker.Probes;

/// <summary>
/// Codex 可用性探测。分两层:
/// - Shallow:<c>codex doctor --json</c> + 带凭据 <c>/models</c>,零 token。
/// - Deep:同一链路再发且只发一次 <c>max_output_tokens=1</c> 的最小 HTTP 推理请求。
///
/// **2026-08-07 修正:route probe 的 401/403 不是认证失败。**
/// 原实现把 <c>provider_reachability.details</c> 里出现的 401/403 一律判成"认证被拒",
/// 结果面板长期红着,而用户的 key 完全正常。实测该 check 的原文是:
/// <code>
/// "status": "ok",
/// "summary": "active provider endpoints are reachable over HTTP",
/// "details": {
///   "OpenAI API base URL":    "https://… reachable (HTTP 200)",
///   "OpenAI API route probe": "https://…/models route exists (HTTP 401)",
///   "reachability mode":      "provider auth"
/// }
/// </code>
/// 这是一次**不带凭据**的连通性探测:401 恰恰证明"路由在、且需要认证"
/// ——doctor 自己的措辞就是 "route exists",并据此判 ok。用它推断授权状态是读错了语义。
/// 所以这里只从 details 里捞 doctor **没有**用 status 表达出来的信号(429 与 5xx)。
/// </summary>
public enum CodexReadiness
{
    Ok,
    Limited,
    Auth,
    Unreachable,
    NoCli,
    Timeout,
    Unknown
}

public sealed record CodexProbeResult(
    CodexReadiness Readiness,
    string Reason,
    string? Summary,
    bool DeepChecked,
    string? ProviderIdentity = null);

public sealed class CodexProbe
{
    private const int DefaultTimeoutSeconds = 60;
    private const int KillGraceSeconds = 5;
    private const string InternalRunEnvName = "AI_RESUME_INTERNAL_RUN";

    private static readonly Regex HttpStatusCodeRegex = new(@"HTTP (\d{3})", RegexOptions.Compiled);

    private readonly string _codexCommand;
    private readonly int _timeoutSeconds;
    private readonly string? _codexHome;
    private readonly HttpMessageHandler? _authHandler;
    private readonly Func<CancellationToken, Task<CodexProbeResult>>? _doctorProbe;
    private readonly Func<string, string?> _environmentVariable;
    private readonly Action<ProcessStartInfo>? _doctorStartObserver;

    public CodexProbe(
        string? codexCommand = null,
        int timeoutSeconds = DefaultTimeoutSeconds,
        string? codexHome = null,
        HttpMessageHandler? authHandler = null,
        Func<CancellationToken, Task<CodexProbeResult>>? doctorProbe = null,
        Func<string, string?>? environmentVariable = null,
        Action<ProcessStartInfo>? doctorStartObserver = null)
    {
        _codexCommand = string.IsNullOrWhiteSpace(codexCommand) ? "codex" : codexCommand;
        _timeoutSeconds = timeoutSeconds > 0 ? timeoutSeconds : DefaultTimeoutSeconds;
        _environmentVariable = environmentVariable ?? Environment.GetEnvironmentVariable;
        // 两个注入点只为测试:codexHome 让测试指向假配置目录,
        // authHandler 让测试不联网就能断言状态码映射。生产一律走默认。
        _codexHome = CodexAuthProbe.ResolveCodexHome(codexHome, _environmentVariable);
        _authHandler = authHandler;
        _doctorProbe = doctorProbe;
        _doctorStartObserver = doctorStartObserver;
    }

    /// <summary>
    /// 默认探测:<c>codex doctor --json</c> + **一次带凭据的 /models 请求**。
    ///
    /// 后半段是关键 —— doctor 证明不了授权。带凭据打 /models 是零 token 的真实鉴权验证,
    /// 但仍不证明当前模型能完成推理,所以 shallow 结果保持灰色。
    ///
    /// 授权探测拿不出结论时(读不到配置、网络失败),**不冒充绿灯**,
    /// 退回 doctor 的「可达,授权未验证」。
    /// </summary>
    public Task<CodexProbeResult> ProbeShallowAsync(CancellationToken ct = default)
    {
        CodexProviderCredentials provider = CodexAuthProbe.ReadActiveProviderCredentials(
            _codexHome,
            _environmentVariable);
        return ProbeShallowAsync(provider, ct);
    }

    public async Task<CodexProbeResult> ProbeShallowAsync(
        CodexProviderCredentials provider,
        CancellationToken ct = default)
    {
        string providerIdentity = CodexAuthProbe.CreateProviderIdentity(provider);
        CodexProbeResult doctor = await ProbeDoctorAsync(ct).ConfigureAwait(false);
        if (doctor.Readiness != CodexReadiness.Ok)
        {
            // doctor 已经给出明确的坏结论(没装/不可达/被限流),没必要再打一次网络。
            return doctor with { ProviderIdentity = providerIdentity };
        }

        CodexAuthResult auth = await CodexAuthProbe
            .ProbeModelsAsync(provider, _authHandler, ct).ConfigureAwait(false);

        return FromAuthResult(auth) with { ProviderIdentity = providerIdentity };
    }

    public static CodexProbeResult FromAuthResult(CodexAuthResult auth)
    {
        return auth.Outcome switch
        {
            // 只有最小推理真实成功才是可用证据，允许给绿灯。
            CodexAuthOutcome.Authorized =>
                new CodexProbeResult(CodexReadiness.Ok, "authorized", auth.Detail, true),
            // 鉴权成功但推理没有核实，不把凭据说成坏的，也绝不冒充绿色可用。
            CodexAuthOutcome.InferenceUnverified =>
                new CodexProbeResult(CodexReadiness.Ok, "inference-unverified", auth.Detail, false),
            // 能列模型、不能推理:凭据没坏,但**任务跑不了**,所以归 Auth(红)而不是 Ok。
            // 给绿灯等于告诉用户"随时可以跑",而它一跑就失败(审计 A6)。
            CodexAuthOutcome.NoInference =>
                new CodexProbeResult(CodexReadiness.Auth, "no-inference", auth.Detail, true),
            CodexAuthOutcome.Rejected =>
                new CodexProbeResult(
                    CodexReadiness.Auth,
                    auth.UsedCredential ? "auth-rejected" : "credential-required",
                    auth.Detail,
                    true),
            CodexAuthOutcome.Limited =>
                new CodexProbeResult(
                    CodexReadiness.Limited,
                    auth.Reason is "http-402" or "http-429" ? auth.Reason : "limited",
                    auth.Detail,
                    true),
            CodexAuthOutcome.ServerError =>
                new CodexProbeResult(CodexReadiness.Unreachable, "server-error", auth.Detail, true),
            // doctor 通过,但 HTTP 探针没拿到结论:只陈述 doctor 证据,不给绿灯。
            _ => new CodexProbeResult(
                CodexReadiness.Ok, "unverified", "Codex doctor 通过,HTTP 可用性未验证(" + auth.Detail + ")", false),
        };
    }

    /// <summary>只跑 codex doctor --json,不发任何模型请求。</summary>
    public async Task<CodexProbeResult> ProbeDoctorAsync(CancellationToken ct = default)
    {
        if (_doctorProbe is not null)
        {
            return await _doctorProbe(ct).ConfigureAwait(false);
        }

        // 起进程 codex doctor --json,捕获 stdout。
        // 必须异步读流:先 WaitForExit 再读会死锁(输出超过管道缓冲)。
        using var process = new Process();
        process.StartInfo = new ProcessStartInfo
        {
            FileName = _codexCommand,
            Arguments = "doctor --json",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        process.StartInfo.Environment[InternalRunEnvName] = "1";
        process.StartInfo.Environment["CODEX_HOME"] = _codexHome;
        _doctorStartObserver?.Invoke(process.StartInfo);

        try
        {
            if (!process.Start())
            {
                return new CodexProbeResult(CodexReadiness.NoCli, "no-cli", "未安装 codex 命令", false);
            }
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or System.IO.FileNotFoundException)
        {
            // 进程起不来(找不到文件/不是可执行文件)。
            return new CodexProbeResult(CodexReadiness.NoCli, "no-cli", "未安装 codex 命令", false);
        }

        // 异步读两个流,避免管道缓冲死锁。
        var stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
        var stderrTask = process.StandardError.ReadToEndAsync(ct);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(_timeoutSeconds));
        try
        {
            await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // 超时:杀进程树,返回 Timeout。
            TryKillTree(process);
            await WaitForExitQuietlyAsync(process).ConfigureAwait(false);
            return new CodexProbeResult(CodexReadiness.Timeout, "timeout", "探测超时", false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // 调用方取消:杀进程树后把取消继续传给 GUI/RPC 调用链。
            TryKillTree(process);
            await WaitForExitQuietlyAsync(process).ConfigureAwait(false);
            throw;
        }

        string stdout = await stdoutTask.ConfigureAwait(false);
        _ = await stderrTask.ConfigureAwait(false);   // 必须读干净,否则管道满了子进程会卡住

        return ClassifyDoctorJson(stdout);
    }

    /// <summary>
    /// 把 <c>codex doctor --json</c> 的 stdout 判成一个 shallow 结论。
    /// 抽成纯函数是为了能直接喂真实 doctor 输出做断言——探测本身要起子进程,测不了。
    /// </summary>
    public static CodexProbeResult ClassifyDoctorJson(string stdout)
    {
        // stdout 解析不出 JSON → Unknown。
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(stdout);
        }
        catch (JsonException)
        {
            return new CodexProbeResult(CodexReadiness.Unknown, "malformed", "无法解析 codex doctor 输出", false);
        }

        using (doc)
        {
            if (doc.RootElement.ValueKind != JsonValueKind.Object ||
                !doc.RootElement.TryGetProperty("checks", out JsonElement checks))
            {
                return new CodexProbeResult(CodexReadiness.Unknown, "malformed", "codex doctor 输出缺少 checks", false);
            }

            // 读关键 check 的 status。
            if (TryGetCheckStatus(checks, "installation", out string? installStatus) && IsFailureStatus(installStatus))
            {
                return new CodexProbeResult(CodexReadiness.NoCli, "install-error", "codex 安装异常", false);
            }

            if (TryGetCheckFailure(checks, "config.load", out string? configFailure))
            {
                return new CodexProbeResult(
                    CodexReadiness.Unknown,
                    "config-error",
                    DescribeConfigFailure(configFailure),
                    false);
            }

            // 这一条才是真的授权信号:doctor 判 error 时确实是凭据有问题。
            // (自定义 provider 不走 OpenAI 登录时,它会判 ok 并注明 "auth is not required"。)
            if (TryGetCheckStatus(checks, "auth.credentials", out string? authStatus) && IsFailureStatus(authStatus))
            {
                return new CodexProbeResult(CodexReadiness.Auth, "auth", "认证失败", false);
            }

            if (TryGetCheckStatus(checks, "network.provider_reachability", out string? networkStatus) && IsFailureStatus(networkStatus))
            {
                return new CodexProbeResult(CodexReadiness.Unreachable, "unreachable", "网络不可达", false);
            }

            // 扫 details 里的 HTTP 状态码,但**只捞 doctor 没用 status 表达出来的**:
            // 429(被限流)和 5xx(网关自己坏了)。
            // 401/403 在这里是**预期结果**——doctor 的 route probe 不带凭据,
            // 它的原话就是 "route exists (HTTP 401)",拿它判"认证被拒"是读错语义。
            if (TryGetCheckDetails(checks, "network.provider_reachability", out JsonElement details))
            {
                foreach (JsonProperty prop in details.EnumerateObject())
                {
                    if (prop.Value.ValueKind != JsonValueKind.String)
                    {
                        continue;
                    }

                    string value = prop.Value.GetString() ?? string.Empty;
                    foreach (Match match in HttpStatusCodeRegex.Matches(value))
                    {
                        string code = match.Groups[1].Value;
                        if (code == "429")
                        {
                            return new CodexProbeResult(CodexReadiness.Limited, "http-429", "被限流(HTTP 429)", false);
                        }

                        if (code.Length == 3 && code[0] == '5')
                        {
                            return new CodexProbeResult(
                                CodexReadiness.Unreachable, "http-" + code, $"服务端异常(HTTP {code})", false);
                        }
                    }
                }
            }

            // 到这里只证明「装好了、配置能读、端点可达」。**授权仍未验证**,
            // 所以桥接层把它渲染成灰色「已就绪 · 未验证授权」,不给绿灯。
            return new CodexProbeResult(CodexReadiness.Ok, "ok", "已装好并可达,未验证授权", false);
        }
    }

    /// <summary>先 doctor;明确失败就返回,否则执行一次完整 HTTP 鉴权+最小推理链路。</summary>
    public Task<CodexProbeResult> ProbeDeepAsync(CancellationToken ct = default)
    {
        CodexProviderCredentials provider = CodexAuthProbe.ReadActiveProviderCredentials(
            _codexHome,
            _environmentVariable);
        return ProbeDeepAsync(provider, ct);
    }

    public async Task<CodexProbeResult> ProbeDeepAsync(
        CodexProviderCredentials provider,
        CancellationToken ct = default)
    {
        string providerIdentity = CodexAuthProbe.CreateProviderIdentity(provider);
        CodexProbeResult doctor = await ProbeDoctorAsync(ct).ConfigureAwait(false);
        if (doctor.Readiness != CodexReadiness.Ok)
        {
            return doctor with { ProviderIdentity = providerIdentity };
        }

        CodexAuthResult auth = await CodexAuthProbe
            .ProbeAsync(provider, _authHandler, ct).ConfigureAwait(false);
        return FromAuthResult(auth) with { ProviderIdentity = providerIdentity };
    }

    private static bool TryGetCheckStatus(JsonElement checks, string id, out string? status)
    {
        status = null;
        if (checks.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        if (!checks.TryGetProperty(id, out JsonElement check) || check.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        if (!check.TryGetProperty("status", out JsonElement statusEl) || statusEl.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        status = statusEl.GetString();
        return status != null;
    }

    private static bool TryGetCheckFailure(JsonElement checks, string id, out string? failure)
    {
        failure = null;
        if (!TryGetCheckStatus(checks, id, out string? status) ||
            !IsFailureStatus(status))
        {
            return false;
        }

        if (checks.TryGetProperty(id, out JsonElement check) && check.ValueKind == JsonValueKind.Object)
        {
            if (check.TryGetProperty("summary", out JsonElement summary) && summary.ValueKind == JsonValueKind.String)
            {
                failure = summary.GetString();
            }

            if (check.TryGetProperty("notes", out JsonElement notes) && notes.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement item in notes.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.String)
                    {
                        continue;
                    }

                    string? note = item.GetString();
                    if (!string.IsNullOrWhiteSpace(note))
                    {
                        failure = note;
                        break;
                    }
                }
            }
        }

        return true;
    }

    private static bool IsFailureStatus(string? status) =>
        string.Equals(status, "error", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(status, "fail", StringComparison.OrdinalIgnoreCase);

    private static string DescribeConfigFailure(string? failure)
    {
        if (string.IsNullOrWhiteSpace(failure))
        {
            return "Codex 配置无法加载";
        }

        if (Regex.IsMatch(failure, @"\bduplicate\s+(?:key|field)\b", RegexOptions.IgnoreCase) ||
            failure.Contains("重复", StringComparison.Ordinal))
        {
            return "Codex 配置无法加载:存在重复键";
        }

        if (Regex.IsMatch(failure, @"\bparse|invalid\s+toml|toml.*(?:error|invalid)\b", RegexOptions.IgnoreCase) ||
            failure.Contains("语法", StringComparison.Ordinal))
        {
            return "Codex 配置无法加载:TOML 语法错误";
        }

        if (Regex.IsMatch(failure, @"permission|access\s+denied|unauthorized", RegexOptions.IgnoreCase) ||
            failure.Contains("权限", StringComparison.Ordinal))
        {
            return "Codex 配置无法加载:无读取权限";
        }

        return "Codex 配置无法加载:详情请运行 codex doctor --json";
    }

    private static bool TryGetCheckDetails(JsonElement checks, string id, out JsonElement details)
    {
        details = default;
        if (checks.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        if (!checks.TryGetProperty(id, out JsonElement check) || check.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        if (!check.TryGetProperty("details", out details) || details.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        return true;
    }

    private static void TryKillTree(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception)
        {
            // 尽力终止;终止失败留给下次观察核验。
        }
    }

    private static async Task WaitForExitQuietlyAsync(Process process)
    {
        try
        {
            await process.WaitForExitAsync(CancellationToken.None)
                .WaitAsync(TimeSpan.FromSeconds(KillGraceSeconds))
                .ConfigureAwait(false);
        }
        catch (Exception)
        {
            // 尽力等待;不掩盖分类结果。
        }
    }
}
