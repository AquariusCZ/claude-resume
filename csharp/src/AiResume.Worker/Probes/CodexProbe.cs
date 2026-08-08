using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AiResume.Worker.Probes;

/// <summary>
/// Codex 可用性探测。分两层:
/// - Shallow:只跑 <c>codex doctor --json</c>,不发模型请求(不烧额度)。
///   doctor **不验证授权**,所以 shallow 最好的结论只能是「可达,授权未验证」。
/// - Deep:先 shallow,shallow 明确失败就直接返回;否则再发一次最小请求
///   <c>codex exec OK</c> 验证真实可用性。
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
    bool DeepChecked);

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

    public CodexProbe(
        string? codexCommand = null,
        int timeoutSeconds = DefaultTimeoutSeconds,
        string? codexHome = null,
        HttpMessageHandler? authHandler = null)
    {
        _codexCommand = string.IsNullOrWhiteSpace(codexCommand) ? "codex" : codexCommand;
        _timeoutSeconds = timeoutSeconds > 0 ? timeoutSeconds : DefaultTimeoutSeconds;
        // 两个注入点只为测试:codexHome 让测试指向假配置目录,
        // authHandler 让测试不联网就能断言状态码映射。生产一律走默认。
        _codexHome = codexHome;
        _authHandler = authHandler;
    }

    /// <summary>
    /// 默认探测:<c>codex doctor --json</c> + **一次带凭据的 /v1/models 请求**。
    ///
    /// 后半段是关键 —— doctor 证明不了授权,而唯一此前能证明的办法 `codex exec`
    /// 要 10-12 秒、2.3 万 tokens,贵到不能每次开窗都跑,于是面板长期只能显示
    /// 「已就绪 · 未验证授权」。带凭据打 /v1/models 是 **1.3 秒、0 token** 的真实验证,
    /// 所以现在**每一次探测都是真的**,绿灯有据可依。
    ///
    /// 授权探测拿不出结论时(读不到配置、网络失败),**不冒充绿灯**,
    /// 退回 doctor 的「可达,授权未验证」。
    /// </summary>
    public async Task<CodexProbeResult> ProbeShallowAsync(CancellationToken ct = default)
    {
        CodexProbeResult doctor = await ProbeDoctorAsync(ct).ConfigureAwait(false);
        if (doctor.Readiness != CodexReadiness.Ok)
        {
            // doctor 已经给出明确的坏结论(没装/不可达/被限流),没必要再打一次网络。
            return doctor;
        }

        CodexAuthResult auth = await CodexAuthProbe
            .ProbeAsync(_codexHome, _authHandler, ct).ConfigureAwait(false);

        return auth.Outcome switch
        {
            // 带凭据请求成功 = 真的验证过了,给绿灯(DeepChecked=true)。
            // **文案直接用 auth.Detail**:探测已经区分了"凭据+推理都验过"和
            // "凭据验过但推理没核实"(端点不支持/没配 model),拼一句固定话会把这个区别抹掉。
            CodexAuthOutcome.Authorized =>
                new CodexProbeResult(CodexReadiness.Ok, "authorized", auth.Detail, true),
            // 能列模型、不能推理:凭据没坏,但**任务跑不了**,所以归 Auth(红)而不是 Ok。
            // 给绿灯等于告诉用户"随时可以跑",而它一跑就失败(审计 A6)。
            CodexAuthOutcome.NoInference =>
                new CodexProbeResult(CodexReadiness.Auth, "no-inference", auth.Detail, true),
            CodexAuthOutcome.Rejected =>
                new CodexProbeResult(CodexReadiness.Auth, "auth-rejected", auth.Detail, true),
            CodexAuthOutcome.Limited =>
                new CodexProbeResult(CodexReadiness.Limited, "http-429", auth.Detail, true),
            CodexAuthOutcome.ServerError =>
                new CodexProbeResult(CodexReadiness.Unreachable, "server-error", auth.Detail, true),
            // 读不到配置 / 网络失败:doctor 说可达,但授权没验成 —— 如实说,不给绿灯。
            _ => new CodexProbeResult(
                CodexReadiness.Ok, "unverified", "已装好并可达,未验证授权(" + auth.Detail + ")", false),
        };
    }

    /// <summary>只跑 codex doctor --json,不发任何模型请求。</summary>
    public async Task<CodexProbeResult> ProbeDoctorAsync(CancellationToken ct = default)
    {
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
        catch (OperationCanceledException)
        {
            // 调用方取消:杀进程树,归 Unknown(与契约对齐,不新增 cancelled 枚举)。
            TryKillTree(process);
            await WaitForExitQuietlyAsync(process).ConfigureAwait(false);
            return new CodexProbeResult(CodexReadiness.Unknown, "cancelled", "探测被取消", false);
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
            if (TryGetCheckStatus(checks, "installation", out string? installStatus) && installStatus == "error")
            {
                return new CodexProbeResult(CodexReadiness.NoCli, "install-error", "codex 安装异常", false);
            }

            // 这一条才是真的授权信号:doctor 判 error 时确实是凭据有问题。
            // (自定义 provider 不走 OpenAI 登录时,它会判 ok 并注明 "auth is not required"。)
            if (TryGetCheckStatus(checks, "auth.credentials", out string? authStatus) && authStatus == "error")
            {
                return new CodexProbeResult(CodexReadiness.Auth, "auth", "认证失败", false);
            }

            if (TryGetCheckStatus(checks, "network.provider_reachability", out string? networkStatus) && networkStatus == "error")
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

    /// <summary>先 shallow;shallow 明确失败就直接返回,否则再发一次最小请求。</summary>
    public async Task<CodexProbeResult> ProbeDeepAsync(CancellationToken ct = default)
    {
        // 先 shallow;Readiness != Ok 时直接返回它(不浪费一次真实请求)。
        CodexProbeResult shallow = await ProbeShallowAsync(ct).ConfigureAwait(false);
        if (shallow.Readiness != CodexReadiness.Ok)
        {
            return shallow;
        }

        // **必须给它一个一次性空目录当 cwd。**
        // `codex exec` 起的是一个真 agent,不是一次纯 HTTP 调用 —— 不指定工作目录就继承
        // 调用方的 cwd。探测是无人值守跑的(开窗、定时),让一个 agent 落在用户的仓库里
        // 是没有理由承担的风险。空目录里它无事可做,只能回一句话然后退出。
        string scratch = Path.Combine(Path.GetTempPath(), "airesume-codex-probe-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(scratch);

        try
        {
            return await ProbeDeepCoreAsync(scratch, ct).ConfigureAwait(false);
        }
        finally
        {
            try
            {
                Directory.Delete(scratch, recursive: true);
            }
            catch (Exception)
            {
                // 清不掉就留着:临时目录残留远好过在这里抛掉真正的探测结果。
            }
        }
    }

    private async Task<CodexProbeResult> ProbeDeepCoreAsync(string workingDirectory, CancellationToken ct)
    {
        // 起 codex exec OK,超时同上。
        using var process = new Process();
        process.StartInfo = new ProcessStartInfo
        {
            FileName = _codexCommand,
            // **--skip-git-repo-check 是必需的,不是可选优化。**
            // 没有它,codex 在非受信目录直接 exit 1 并打印
            // "Not inside a trusted directory and --skip-git-repo-check was not specified",
            // 探测于是恒返回「codex 执行异常」——绿灯永远点不亮,而且看着像 Codex 坏了。
            // 安装目录同样不是 git 仓库,所以这个参数在改用临时目录之前就已经是必需的。
            Arguments = "exec --skip-git-repo-check OK",
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        process.StartInfo.Environment[InternalRunEnvName] = "1";

        try
        {
            if (!process.Start())
            {
                return new CodexProbeResult(CodexReadiness.NoCli, "no-cli", "未安装 codex 命令", true);
            }
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or System.IO.FileNotFoundException)
        {
            return new CodexProbeResult(CodexReadiness.NoCli, "no-cli", "未安装 codex 命令", true);
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
            TryKillTree(process);
            await WaitForExitQuietlyAsync(process).ConfigureAwait(false);
            return new CodexProbeResult(CodexReadiness.Timeout, "timeout", "探测超时", true);
        }
        catch (OperationCanceledException)
        {
            TryKillTree(process);
            await WaitForExitQuietlyAsync(process).ConfigureAwait(false);
            return new CodexProbeResult(CodexReadiness.Unknown, "cancelled", "探测被取消", true);
        }

        string stdout = await stdoutTask.ConfigureAwait(false);
        string stderr = await stderrTask.ConfigureAwait(false);
        string combined = stdout + "\n" + stderr;
        string low = combined.ToLowerInvariant();

        // 退出码 0 → Ok。
        if (process.ExitCode == 0)
        {
            return new CodexProbeResult(CodexReadiness.Ok, "ok", "已装好并可用", true);
        }

        // 输出分类(大小写不敏感)。
        if (Regex.IsMatch(low, @"rate limit|quota|\b429\b|额度"))
        {
            return new CodexProbeResult(CodexReadiness.Limited, "limited", "额度受限", true);
        }

        if (Regex.IsMatch(low, @"\b401\b|unauthorized|invalid api key|authentication"))
        {
            return new CodexProbeResult(CodexReadiness.Auth, "auth", "认证失败", true);
        }

        // 否则 Unknown / exit-N。
        return new CodexProbeResult(
            CodexReadiness.Unknown,
            "exit-" + process.ExitCode.ToString(CultureInfo.InvariantCulture),
            "codex 执行异常",
            true);
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
