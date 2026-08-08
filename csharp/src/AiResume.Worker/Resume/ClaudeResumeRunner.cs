using System.Text;
using System.Text.RegularExpressions;
using AiResume.Core;
using AiResume.Core.Abstractions;
using AiResume.Core.Contracts;
using AiResume.Worker.Probes;

namespace AiResume.Worker.Resume;

/// <summary>
/// 续跑运行结果。Status 取值与现役 <c>Invoke-ClaudeResume</c> 一致:
/// success / limited / stopped / no-claude / prompt-multiline / launch-error /
/// registry-error / monitor-error / exit-&lt;N&gt; / exit-null。
/// </summary>
public sealed record ResumeRunResult
{
    public string ProjectPath { get; init; } = string.Empty;

    /// <summary>最终判定。这是调用方唯一该据以决策的字段。</summary>
    public string Status { get; init; } = "error";

    public int? ExitCode { get; init; }

    /// <summary>
    /// **观察事实,不是判定**:输出中出现过限流状态标记。
    /// 一次成功的运行也可能在正文里谈论限流,此时 <see cref="Limited"/> 为 true 而
    /// <see cref="Status"/> 为 success。诊断用,不得据此推断结论。
    /// </summary>
    public bool Limited { get; init; }

    /// <summary>**观察事实**:出现过 <c>result</c> 且 <c>is_error:false</c> 的行。</summary>
    public bool ResultOk { get; init; }

    public long OutputBytes { get; init; }
}

/// <summary>续跑运行器最小接口(供 ResumeEngine 依赖注入与测试替身)。</summary>
public interface IClaudeResumeRunner
{
    Task<ResumeRunResult> RunAsync(ProjectRef project, ProductConfig config, CancellationToken cancellationToken);
}

/// <summary>
/// Claude 续跑运行器(移植自现役 <c>src/lib.ps1</c> 的 <c>Invoke-ClaudeResume</c>)。
///
/// 经 <see cref="IProcessSupervisor"/> 启动 —— **不得裸 Process.Start**:
/// 监督器负责"先落盘登记后 spawn"、Job Object kill-on-close 与崩溃恢复,
/// 这些是无总时限长任务的进程边界红线。
/// 输出经 cmd 重定向到临时文件(监督器不做输出重定向),进程结束后做全文权威重扫。
/// 全流程不向调用方抛异常(取消除外)。
/// </summary>
public sealed class ClaudeResumeRunner : IClaudeResumeRunner
{
    private const int PollIntervalMs = 500;
    private const int RescanDelayMs = 300;

    /// <summary>连续监控异常上限(× 500ms = 10 秒)。超过即归 monitor-error,避免引擎被永久挂住。</summary>
    private const int MaxConsecutiveMonitorErrors = 20;

    // 逐行判定,两个模式各自独立匹配。
    // **不可合并成一个跨字段正则**:真实 result 行形如
    // {"type":"result",...,"usage":{...},"is_error":false},中间隔着嵌套对象,
    // 任何 [^}]* 之类的连接写法都跨不过去,会把成功的运行误判成 exit-null。
    private static readonly Regex ResultLine = new("\"type\"\\s*:\\s*\"result\"", RegexOptions.Compiled);
    private static readonly Regex NotErrorLine = new("\"is_error\"\\s*:\\s*false", RegexOptions.Compiled);
    private static readonly Regex LimitedLine = new(
        "\"status\"\\s*:\\s*\"(blocked|rejected|limited|exceeded)\"", RegexOptions.Compiled);

    private readonly IProcessSupervisor _supervisor;
    private readonly string _claudeCommand;

    public ClaudeResumeRunner(IProcessSupervisor supervisor, string? claudeCommand = null)
    {
        _supervisor = supervisor ?? throw new ArgumentNullException(nameof(supervisor));
        _claudeCommand = string.IsNullOrWhiteSpace(claudeCommand) ? "claude" : claudeCommand;
    }

    /// <summary>
    /// 执行一次续跑。前置校验任一不过立即返回且**绝不 spawn**;
    /// 运行期不设客户端总时限(RunContract:续跑无总时限)。
    /// </summary>
    public async Task<ResumeRunResult> RunAsync(ProjectRef project, ProductConfig config, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(config);

        string projectPath = project.Path ?? string.Empty;
        var result = new ResumeRunResult { ProjectPath = projectPath };

        if (Path.IsPathRooted(_claudeCommand) && !File.Exists(_claudeCommand))
        {
            return result with { Status = "no-claude" };
        }

        if (!Directory.Exists(projectPath))
        {
            return result with { Status = "no-claude" };
        }

        // 换行必须显式失败:cmd /c 会在首个换行处截断 -p 参数(见 docs/LESSONS.md),
        // 静默截断等于跑了一个与用户意图不同的提示词。
        if (config.ResumePrompt.Contains('\r') || config.ResumePrompt.Contains('\n'))
        {
            return result with { Status = "prompt-multiline" };
        }

        string tmpOut = Path.Combine(Path.GetTempPath(), "ccu-resume-" + Guid.NewGuid().ToString("N") + ".out");
        string tmpErr = Path.Combine(Path.GetTempPath(), "ccu-resume-" + Guid.NewGuid().ToString("N") + ".err");
        RunId runId = RunId.New();

        try
        {
            var request = new ProcessStartRequest
            {
                RunId = runId,
                FileName = "cmd.exe",
                Arguments = BuildArguments(config, tmpOut, tmpErr),
                WorkingDirectory = projectPath,
                // 红线:AI Resume 自己启动的进程必须带内部标记,否则 Claude Code 的 Stop hook
                // 会被 AiResume.Hook 当成用户任务完成,每次续跑伪造一条通知。
                Environment = new Dictionary<string, string?>
                {
                    [ClaudeCodeProbe.InternalRunEnvName] = "1",
                },
                CommandSignature = "cmd.exe",
            };

            ProcessStartResult startResult;
            try
            {
                startResult = await _supervisor.StartAsync(request, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return result with { Status = "stopped" };
            }
            catch (Exception)
            {
                return result with { Status = "launch-error" };
            }

            if (!startResult.Started)
            {
                string errorCode = startResult.ErrorCode ?? string.Empty;
                return result with
                {
                    Status = errorCode.Contains("registry", StringComparison.OrdinalIgnoreCase)
                        ? "registry-error"
                        : "launch-error",
                };
            }

            string? terminalStatus = await WaitForExitAsync(runId, cancellationToken).ConfigureAwait(false);
            if (terminalStatus is not null)
            {
                return result with { Status = terminalStatus };
            }

            // 权威重扫:流式输出会把一行拆成多个 chunk 落盘,只有读全文才能可靠判定(现役已踩)。
            // 用 None:进程已结束,此处再被取消会丢掉已经拿到的结论。
            await Task.Delay(RescanDelayMs, CancellationToken.None).ConfigureAwait(false);

            long outputBytes = FileLength(tmpOut) + FileLength(tmpErr);
            string blob = SafeRead(tmpOut) + "\n" + SafeRead(tmpErr);

            bool resultOk = false;
            bool limited = false;
            foreach (string line in blob.Split('\n', '\r'))
            {
                if (line.Length == 0)
                {
                    continue;
                }

                if (!resultOk && ResultLine.IsMatch(line) && NotErrorLine.IsMatch(line))
                {
                    resultOk = true;
                }

                if (!limited && LimitedLine.IsMatch(line))
                {
                    limited = true;
                }
            }

            // 判定顺序:ResultOk 必须压过 Limited。一次成功的运行可能在正文里*谈论*限流,
            // 而真被限流的运行永远不会以 is_error:false 收尾;顺序反了会把成功误判成限流,
            // 进而错误地把整轮续跑打回等待。
            // ProcessStatus 不提供退出码,兜底一律 exit-null(不得据退出码判成功)。
            string status = resultOk ? "success" : limited ? "limited" : "exit-null";

            return result with
            {
                Status = status,
                ResultOk = resultOk,
                Limited = limited,
                OutputBytes = outputBytes,
            };
        }
        catch (Exception)
        {
            return result with { Status = "launch-error" };
        }
        finally
        {
            TryDelete(tmpOut);
            TryDelete(tmpErr);
        }
    }

    /// <summary>
    /// 等待进程结束。返回 <c>null</c> 表示正常退出(交由调用方做重扫判定);
    /// 返回非 null 表示提前终态(stopped / monitor-error)。
    /// </summary>
    private async Task<string?> WaitForExitAsync(RunId runId, CancellationToken cancellationToken)
    {
        int consecutiveMonitorErrors = 0;

        while (true)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                await TryCancelAsync(runId).ConfigureAwait(false);
                return "stopped";
            }

            ProcessStatus status;
            try
            {
                status = await _supervisor.StatusAsync(runId, cancellationToken).ConfigureAwait(false);
                consecutiveMonitorErrors = 0;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                await TryCancelAsync(runId).ConfigureAwait(false);
                return "stopped";
            }
            catch (Exception)
            {
                // 监控异常本身不是任务失败,但也不能无限等下去把引擎挂死:
                // 连续失败超过上限归 monitor-error(RunContract 的 failed_local 类)。
                if (++consecutiveMonitorErrors >= MaxConsecutiveMonitorErrors)
                {
                    return "monitor-error";
                }

                await DelayAsync(cancellationToken).ConfigureAwait(false);
                continue;
            }

            if (status.Liveness == ProcessLiveness.Gone)
            {
                return null;
            }

            // Alive 与 Unknown 都继续等待:Unknown 是"核验不了",不是"失败"(fail-closed)。
            await DelayAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task DelayAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(PollIntervalMs, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // 取消由下一轮循环开头统一处理成 stopped。
        }
    }

    private async Task TryCancelAsync(RunId runId)
    {
        try
        {
            await _supervisor.CancelAsync(runId, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // 终止失败不掩盖 stopped 语义;残留由监督器的观察/恢复流程核验。
        }
    }

    /// <summary>
    /// cmd /c 引号规则(照抄 <c>ClaudeCodeProbe.BuildArguments</c>):
    /// 整条命令首尾引号包裹,内层命令自带引号;重定向符号位于引号外。
    /// </summary>
    private string BuildArguments(ProductConfig config, string tmpOut, string tmpErr)
    {
        var sb = new StringBuilder();
        sb.Append("/c \"\"").Append(_claudeCommand).Append("\" --continue -p \"")
          .Append(config.ResumePrompt).Append("\" --output-format stream-json --verbose");

        if (!string.IsNullOrWhiteSpace(config.ResumeModel))
        {
            sb.Append(" --model \"").Append(config.ResumeModel).Append('"');
        }

        if (config.SkipPermissions)
        {
            sb.Append(" --dangerously-skip-permissions");
        }

        sb.Append(" > \"").Append(tmpOut).Append("\" 2> \"").Append(tmpErr).Append("\"\"");
        return sb.ToString();
    }

    private static string SafeRead(string path)
    {
        try
        {
            // FileShare.ReadWrite:claude 可能仍持有句柄。
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            return reader.ReadToEnd();
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
