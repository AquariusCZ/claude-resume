using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using AiResume.Core;

namespace AiResume.Worker.Probes;

/// <summary>
/// S5-B Claude 限额探测(真实最小调用,与现役 Test-ClaudeReady 相同命令):
/// <c>claude -p ready --model &lt;model&gt; --max-turns 1 --output-format stream-json --verbose</c>,
/// 经 cmd /c 启动、输出重定向到临时文件(避免管道缓冲死锁),读取后解析即弃。
///
/// 判定顺序(与现役一致,最权威优先):
/// 1. 服务端 status=blocked/rejected/limited/exceeded → limited;
/// 2. result 行 is_error=false → ok(成功信号,必须压过模糊文本匹配);
/// 3. 模糊文本分类(limited/auth/billing/model_unavailable/transient/no-claude);
/// 4. exit code(最后手段):0 → ok,否则 exit-N。
/// rate_limit_info(扁平 JSON 段)解析服务端精确 resetsAt(5 小时/7 天)与 utilization。
///
/// 错误分类(规格 §4 S5-B):no-claude/spawn 失败/timeout/网络类 → 本地类 reason;
/// 服务端结构化 → 服务端类 reason。探测不设客户端总时限;timeout 为探测固有防护
/// (现役 TimeoutSec=90)。输出文本解析后即删除,不落日志;结果只含结构化字段与字节计数。
/// </summary>
public sealed class ClaudeCodeProbe : IClaudeUsageProbe
{
    private const int DefaultTimeoutSeconds = 90;
    private const int KillGraceSeconds = 5;

    /// <summary>内部运行标记:AI Resume 自己启动的进程带上它,完成通知 hook 据此抑制。</summary>
    internal const string InternalRunEnvName = "AI_RESUME_INTERNAL_RUN";

    private static readonly Regex RateLimitInfo = new(@"""rate_limit_info""\s*:\s*\{[^}]*\}", RegexOptions.Compiled);
    private static readonly Regex StatusBlocked = new(@"""status""\s*:\s*""(blocked|rejected|limited|exceeded)""", RegexOptions.Compiled);

    private readonly string _claudeCommand;
    private readonly int _timeoutSeconds;

    public ClaudeCodeProbe(string? claudeCommand = null, int timeoutSeconds = DefaultTimeoutSeconds)
    {
        _claudeCommand = string.IsNullOrWhiteSpace(claudeCommand) ? "claude" : claudeCommand;
        _timeoutSeconds = timeoutSeconds > 0 ? timeoutSeconds : DefaultTimeoutSeconds;
    }

    public string ClaudeCommand => _claudeCommand;

    /// <summary>执行一次探测;调用方取消时终止进程树并返回 cancelled 类 reason。</summary>
    public async Task<ClaudeProbeResult> ProbeAsync(string model, string workingDirectory, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(workingDirectory);

        // no-claude 预检:绝对路径不存在直接失败(避免 cmd 间接错误噪音)。
        if (Path.IsPathRooted(_claudeCommand) && !File.Exists(_claudeCommand))
        {
            return new ClaudeProbeResult { Reason = "no-claude" };
        }

        // 清扫上一次崩溃/强杀留下的陈旧临时文件。
        // finally 里的删除只覆盖正常路径;宿主被硬杀(例如用户在探测中途关掉窗口、
        // 或截图自测走 Application.Shutdown)时进程直接消失,文件会永久留在系统 temp。
        SweepStaleTempFiles();

        string tmpOut = Path.Combine(Path.GetTempPath(), "ccu-probe-" + Guid.NewGuid().ToString("N") + ".out");
        string tmpErr = Path.Combine(Path.GetTempPath(), "ccu-probe-" + Guid.NewGuid().ToString("N") + ".err");
        try
        {
            using var process = new Process();
            process.StartInfo = new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = BuildArguments(model, tmpOut, tmpErr),
                WorkingDirectory = workingDirectory,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            // 探测本身会拉起 claude,其 Stop hook 会被 AiResume.Hook 接住并当成"任务完成"。
            // 打上内部运行标记,让 hook 侧的抑制闸门(Program.ShouldSuppress)识别并丢弃,
            // 否则每探测一次就伪造一条完成通知。
            process.StartInfo.Environment[InternalRunEnvName] = "1";

            try
            {
                if (!process.Start())
                {
                    return new ClaudeProbeResult { Reason = "spawn-failed" };
                }
            }
            catch (Exception ex)
            {
                // spawn 失败(本地):文本分类对齐现役 catch 分支;无法分类时归 spawn-failed。
                string classified = ClassifyText(ex.Message);
                return new ClaudeProbeResult
                {
                    Reason = classified == "unknown" ? "spawn-failed" : classified,
                };
            }

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(_timeoutSeconds));
            try
            {
                await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // 探测超时(本地防护,与现役 TimeoutSec 相同):终止进程树后归 timeout。
                TryKillTree(process);
                await WaitForExitQuietlyAsync(process).ConfigureAwait(false);
                return new ClaudeProbeResult { Reason = "timeout" };
            }
            catch (OperationCanceledException)
            {
                // 调用方取消:终止进程树,归 cancelled(探测不设总时限,取消即停)。
                TryKillTree(process);
                await WaitForExitQuietlyAsync(process).ConfigureAwait(false);
                return new ClaudeProbeResult { Reason = "cancelled" };
            }

            string blob = SafeRead(tmpOut) + "\n" + SafeRead(tmpErr);
            long bytes = FileLength(tmpOut) + FileLength(tmpErr);
            return Classify(blob, process.ExitCode, bytes);
        }
        finally
        {
            TryDelete(tmpOut);
            TryDelete(tmpErr);
        }
    }

    /// <summary>cmd /c 引号规则:整条命令首尾引号包裹,内层命令自带引号;重定向符号位于引号外。</summary>
    private string BuildArguments(string model, string tmpOut, string tmpErr)
    {
        return "/c \"\"" + _claudeCommand + "\" -p ready --model \"" + model +
               "\" --max-turns 1 --output-format stream-json --verbose > \"" + tmpOut + "\" 2> \"" + tmpErr + "\"\"";
    }

    private static ClaudeProbeResult Classify(string blob, int exitCode, long bytes)
    {
        DateTimeOffset? fiveHourReset = null;
        DateTimeOffset? sevenDayReset = null;
        double? fiveHourUtil = null;
        double? sevenDayUtil = null;

        foreach (Match match in RateLimitInfo.Matches(blob))
        {
            string segment = match.Value;
            string type = Regex.Match(segment, "\"rateLimitType\"\\s*:\\s*\"([^\"]+)\"").Groups[1].Value;
            string resetsAt = Regex.Match(segment, "\"resetsAt\"\\s*:\\s*(\\d+)").Groups[1].Value;
            string utilization = Regex.Match(segment, "\"utilization\"\\s*:\\s*([0-9.]+)").Groups[1].Value;
            if (long.TryParse(resetsAt, NumberStyles.Integer, CultureInfo.InvariantCulture, out long unix))
            {
                DateTimeOffset resetUtc = DateTimeOffset.FromUnixTimeSeconds(unix);
                double? util = double.TryParse(utilization, NumberStyles.Float, CultureInfo.InvariantCulture, out double u)
                    ? u
                    : null;
                if (type == "five_hour")
                {
                    fiveHourReset = resetUtc;
                    fiveHourUtil = util;
                }
                else if (type == "seven_day")
                {
                    sevenDayReset = resetUtc;
                    sevenDayUtil = util;
                }
            }
        }

        string reason;
        if (StatusBlocked.IsMatch(blob))
        {
            reason = "limited";
        }
        else
        {
            reason = "unknown";
            foreach (string line in blob.Split('\n'))
            {
                // result 行 is_error=false 是成功信号(压过模糊文本,避免成功 run 内的接近限流警告误读)。
                if (Regex.IsMatch(line, "\"type\"\\s*:\\s*\"result\"") &&
                    Regex.IsMatch(line, "\"is_error\"\\s*:\\s*false"))
                {
                    reason = "ok";
                    break;
                }
            }

            if (reason == "unknown")
            {
                reason = ClassifyText(blob);
                if (reason == "unknown")
                {
                    reason = exitCode == 0 ? "ok" : "exit-" + exitCode.ToString(CultureInfo.InvariantCulture);
                }
            }
        }

        return new ClaudeProbeResult
        {
            Ready = reason == "ok",
            Reason = reason,
            FiveHourResetUtc = fiveHourReset,
            SevenDayResetUtc = sevenDayReset,
            FiveHourUtil = fiveHourUtil,
            SevenDayUtil = sevenDayUtil,
            OutputBytes = bytes,
        };
    }

    /// <summary>模糊文本分类(移植现役 Get-ClaudeProbeFailureReason 的规则集)。</summary>
    private static string ClassifyText(string text)
    {
        string low = text.ToLowerInvariant();
        if (Regex.IsMatch(low, @"usage limit|rate.?limit|limit reached|5-hour limit|weekly limit|too many requests|resets at|quota exceeded|429"))
        {
            return "limited";
        }

        if (Regex.IsMatch(low, @"not logged in|please run /login|login required|unauthori[sz]ed|authentication|invalid api key|invalid.*auth|api key.*missing|\b401\b|\b403\b"))
        {
            return "auth";
        }

        if (Regex.IsMatch(low, @"subscription.*(expired|required|inactive)|billing|payment required|insufficient (credit|balance)|credit balance|plan expired"))
        {
            return "billing";
        }

        if (Regex.IsMatch(low, @"model.*(not found|unavailable|unsupported)|unknown model|模型.*不可用"))
        {
            return "model_unavailable";
        }

        if (Regex.IsMatch(low, @"timed? ?out|timeout|econn|socket|tls|dns|network|connection (reset|refused|failed)|\b502\b|\b503\b|\b504\b|server overloaded|temporar"))
        {
            return "transient";
        }

        if (Regex.IsMatch(low, @"enoent|not recognized|command not found|系统找不到指定的文件|启动.*失败"))
        {
            return "no-claude";
        }

        return "unknown";
    }

    /// <summary>
    /// 删除超过 1 小时的探测临时文件。1 小时远大于探测自身的 90 秒上限,
    /// 因此绝不会误删正在进行的探测(哪怕是同机另一个 AI Resume 进程的)。
    /// 全程尽力而为:任何异常都吞掉,清扫失败不能影响本次探测。
    /// </summary>
    private static void SweepStaleTempFiles()
    {
        try
        {
            DateTime cutoff = DateTime.UtcNow - TimeSpan.FromHours(1);
            foreach (string pattern in new[] { "ccu-probe-*.out", "ccu-probe-*.err" })
            {
                foreach (string path in Directory.EnumerateFiles(Path.GetTempPath(), pattern))
                {
                    try
                    {
                        if (File.GetLastWriteTimeUtc(path) < cutoff)
                        {
                            File.Delete(path);
                        }
                    }
                    catch (Exception)
                    {
                        // 单个文件删不掉(被占用/权限)不影响其余。
                    }
                }
            }
        }
        catch (Exception)
        {
            // 目录枚举失败也不影响探测本身。
        }
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

    private static string SafeRead(string path)
    {
        try
        {
            return File.ReadAllText(path, Encoding.UTF8);
        }
        catch (Exception)
        {
            return string.Empty;
        }
    }

    private static long FileLength(string path)
    {
        try
        {
            return File.Exists(path) ? new FileInfo(path).Length : 0;
        }
        catch (Exception)
        {
            return 0;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception)
        {
            // 临时文件清理失败可安全忽略(系统 temp)。
        }
    }
}
