using System.Text;
using System.Text.Json;
using AiResume.Core;
using AiResume.Core.Abstractions;
using AiResume.Core.Contracts;
using AiResume.Worker.Probes;

namespace AiResume.Worker.Resume;

/// <summary>
/// 续跑运行结果。Status 取值与现役 <c>Invoke-ClaudeResume</c> 一致:
/// success / limited / limited-side-effects / stopped / cancel-pending / no-claude /
/// prompt-multiline / launch-error / registry-error / monitor-error / exit-&lt;N&gt; / exit-null。
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

    /// <summary>进程成功登记后的精确标识;spawn 前门禁或启动失败时为空。</summary>
    public RunId? RunId { get; init; }

    /// <summary>当前项目结束后必须终止本轮,避免未确认退出的进程与下一项目并发。</summary>
    public bool StopRound { get; init; }

    /// <summary>本次运行已经出现写入、命令或未知工具活动;一旦为 true 就禁止自动重放。</summary>
    public bool SideEffectsStarted { get; init; }
}

/// <summary>续跑运行器最小接口(供 ResumeEngine 依赖注入与测试替身)。</summary>
public interface IClaudeResumeRunner
{
    Task<ResumeRunResult> RunAsync(
        ProjectRef project,
        ProductConfig config,
        CancellationToken cancellationToken,
        Func<RunId, bool>? beforeStart = null,
        Func<RunId, bool?>? shouldContinue = null);
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
    private const int RescanDelayMs = 300;

    /// <summary>连续监控异常上限(× 500ms = 10 秒)。超过即归 monitor-error,避免引擎被永久挂住。</summary>
    private const int MaxConsecutiveMonitorErrors = 20;

    private static readonly HashSet<string> ReadOnlyTools = new(StringComparer.OrdinalIgnoreCase)
    {
        "Read",
        "Glob",
        "Grep",
        "WebFetch",
        "WebSearch",
        "NotebookRead",
    };

    private static readonly HashSet<string> LimitedStatuses = new(StringComparer.OrdinalIgnoreCase)
    {
        "blocked",
        "rejected",
        "limited",
        "exceeded",
    };

    private readonly IProcessSupervisor _supervisor;
    private readonly string _claudeCommand;
    private readonly TimeSpan _pollInterval;
    private readonly int _maxConsecutiveMonitorErrors;

    public ClaudeResumeRunner(
        IProcessSupervisor supervisor,
        string? claudeCommand = null,
        TimeSpan? pollInterval = null,
        int maxConsecutiveMonitorErrors = MaxConsecutiveMonitorErrors)
    {
        _supervisor = supervisor ?? throw new ArgumentNullException(nameof(supervisor));
        _claudeCommand = string.IsNullOrWhiteSpace(claudeCommand) ? "claude" : claudeCommand;
        _pollInterval = pollInterval is { } interval && interval > TimeSpan.Zero
            ? interval
            : TimeSpan.FromMilliseconds(500);
        _maxConsecutiveMonitorErrors = maxConsecutiveMonitorErrors > 0
            ? maxConsecutiveMonitorErrors
            : MaxConsecutiveMonitorErrors;
    }

    /// <summary>
    /// 执行一次续跑。前置校验任一不过立即返回且**绝不 spawn**;
    /// 运行期不设客户端总时限(RunContract:续跑无总时限)。
    /// </summary>
    public async Task<ResumeRunResult> RunAsync(
        ProjectRef project,
        ProductConfig config,
        CancellationToken cancellationToken,
        Func<RunId, bool>? beforeStart = null,
        Func<RunId, bool?>? shouldContinue = null)
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
            // product_state 必须先持久化本次 RunId,再交给 ProcessSupervisor 登记并 spawn。
            // 否则 Worker 在 spawn 成功到回调落盘之间崩溃时,新 Worker 无法识别这次运行,
            // 可能自动重放已经产生过副作用的项目。
            if (beforeStart is not null)
            {
                bool accepted;
                try
                {
                    accepted = beforeStart(runId);
                }
                catch (Exception)
                {
                    accepted = false;
                }

                if (!accepted)
                {
                    return result with { Status = "stopped", StopRound = true };
                }
            }

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
                return result with { Status = "stopped", StopRound = true };
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

            result = result with { RunId = runId };

            // 进程确实启动、但未能加入 Job 时，Supervisor 会保留精确 PID/Process 供取消收敛。
            // 这不是可继续运行的“带警告成功”：必须立即终止，不能先经过默认 10 秒监控窗口。
            if (!string.IsNullOrEmpty(startResult.ErrorCode))
            {
                bool stopped = await TryCancelAsync(runId).ConfigureAwait(false);
                return result with
                {
                    Status = stopped ? "launch-error" : "cancel-pending",
                    StopRound = true,
                };
            }

            string? terminalStatus = await WaitForExitAsync(
                runId,
                cancellationToken,
                shouldContinue).ConfigureAwait(false);
            if (terminalStatus is not null)
            {
                return result with
                {
                    Status = terminalStatus,
                    StopRound = terminalStatus is "stopped" or "monitor-error" or "cancel-pending",
                };
            }

            // 权威重扫:流式输出会把一行拆成多个 chunk 落盘,只有读全文才能可靠判定(现役已踩)。
            // 用 None:进程已结束,此处再被取消会丢掉已经拿到的结论。
            await Task.Delay(RescanDelayMs, CancellationToken.None).ConfigureAwait(false);

            long outputBytes = FileLength(tmpOut) + FileLength(tmpErr);
            string blob = SafeRead(tmpOut) + "\n" + SafeRead(tmpErr);

            var evidence = new NdjsonEvidence();
            foreach (string line in blob.Split('\n', '\r'))
            {
                if (line.Length == 0)
                {
                    continue;
                }

                InspectNdjsonLine(line, evidence);
            }

            // 判定顺序:ResultOk 必须压过 Limited。一次成功的运行可能在正文里*谈论*限流,
            // 而真被限流的运行永远不会以 is_error:false 收尾;顺序反了会把成功误判成限流,
            // 进而错误地把整轮续跑打回等待。
            // ProcessStatus 不提供退出码,兜底一律 exit-null(不得据退出码判成功)。
            string status = evidence.ResultOk
                ? "success"
                : evidence.Limited
                    ? evidence.SideEffectsStarted ? "limited-side-effects" : "limited"
                    : "exit-null";

            return result with
            {
                Status = status,
                ResultOk = evidence.ResultOk,
                Limited = evidence.Limited,
                SideEffectsStarted = evidence.SideEffectsStarted,
                StopRound = status == "limited-side-effects",
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
    private async Task<string?> WaitForExitAsync(
        RunId runId,
        CancellationToken cancellationToken,
        Func<RunId, bool?>? shouldContinue)
    {
        int consecutiveMonitorErrors = 0;

        while (true)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return await TryCancelAsync(runId).ConfigureAwait(false) ? "stopped" : "cancel-pending";
            }

            if (shouldContinue is not null)
            {
                bool? allowed;
                try
                {
                    allowed = shouldContinue(runId);
                }
                catch (Exception)
                {
                    allowed = null;
                }

                if (allowed != true)
                {
                    return await TryCancelAsync(runId).ConfigureAwait(false) ? "stopped" : "cancel-pending";
                }
            }

            ProcessStatus status;
            try
            {
                status = await _supervisor.StatusAsync(runId, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return await TryCancelAsync(runId).ConfigureAwait(false) ? "stopped" : "cancel-pending";
            }
            catch (Exception)
            {
                // 监控异常本身不是任务失败,但也不能无限等下去把引擎挂死:
                // 连续失败超过上限归 monitor-error(RunContract 的 failed_local 类)。
                if (++consecutiveMonitorErrors >= _maxConsecutiveMonitorErrors)
                {
                    return await TryCancelAsync(runId).ConfigureAwait(false)
                        ? "monitor-error"
                        : "cancel-pending";
                }

                await DelayAsync(cancellationToken).ConfigureAwait(false);
                continue;
            }

            if (!string.IsNullOrWhiteSpace(status.MonitorError))
            {
                if (++consecutiveMonitorErrors >= _maxConsecutiveMonitorErrors)
                {
                    return await TryCancelAsync(runId).ConfigureAwait(false)
                        ? "monitor-error"
                        : "cancel-pending";
                }
            }
            else
            {
                consecutiveMonitorErrors = 0;
            }

            if (status.Liveness == ProcessLiveness.Gone &&
                string.IsNullOrWhiteSpace(status.MonitorError))
            {
                return null;
            }

            // Alive 与 Unknown 都继续等待:Unknown 是"核验不了",不是"失败"(fail-closed)。
            await DelayAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task DelayAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(_pollInterval, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // 取消由下一轮循环开头统一处理成 stopped。
        }
    }

    private static void InspectNdjsonLine(string line, NdjsonEvidence evidence)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(line);
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                // Claude stream-json 的每个非空 NDJSON 行都应是对象。数组/标量即使语法合法，
                // 也不在已知协议内，无法证明其中没有工具活动，按 fail-closed 记为可能有副作用。
                evidence.SideEffectsStarted = true;
                return;
            }

            if (root.TryGetProperty("type", out JsonElement typeElement) &&
                typeElement.ValueKind == JsonValueKind.String)
            {
                string? type = typeElement.GetString();
                if (string.Equals(type, "result", StringComparison.Ordinal) &&
                    root.TryGetProperty("is_error", out JsonElement isError) &&
                    isError.ValueKind == JsonValueKind.False)
                {
                    evidence.ResultOk = true;
                }

                if (string.Equals(type, "rate_limit_event", StringComparison.Ordinal) &&
                    root.TryGetProperty("rate_limit_info", out JsonElement info) &&
                    info.ValueKind == JsonValueKind.Object &&
                    info.TryGetProperty("status", out JsonElement status) &&
                    status.ValueKind == JsonValueKind.String &&
                    LimitedStatuses.Contains(status.GetString() ?? string.Empty))
                {
                    evidence.Limited = true;
                }
            }

            if (ContainsSideEffectingTool(root, evidence))
            {
                evidence.SideEffectsStarted = true;
            }
        }
        catch (JsonException)
        {
            // Claude 的流式契约要求每个非空行都是完整 JSON。任何损坏/截断行都无法证明
            // 它不是工具活动，按 RunContract fail-closed 视为可能已有副作用。
            evidence.SideEffectsStarted = true;
        }
    }

    private static bool ContainsSideEffectingTool(JsonElement element, NdjsonEvidence evidence)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                if (element.TryGetProperty("type", out JsonElement type) &&
                    type.ValueKind == JsonValueKind.String)
                {
                    string activityType = type.GetString() ?? string.Empty;
                    if (string.Equals(activityType, "tool_use", StringComparison.OrdinalIgnoreCase))
                    {
                        evidence.ToolUseObserved = true;
                        if (!element.TryGetProperty("name", out JsonElement name) ||
                            name.ValueKind != JsonValueKind.String ||
                            !ReadOnlyTools.Contains(name.GetString() ?? string.Empty))
                        {
                            return true;
                        }
                    }
                    else if (activityType.Contains("tool", StringComparison.OrdinalIgnoreCase) &&
                        !IsKnownReadOnlyToolResult(activityType, evidence.ToolUseObserved))
                    {
                        // 新版/未知工具事件在没有明确只读语义时必须 fail-closed。
                        return true;
                    }
                }

                foreach (JsonProperty property in element.EnumerateObject())
                {
                    if (ContainsSideEffectingTool(property.Value, evidence))
                    {
                        return true;
                    }
                }

                return false;

            case JsonValueKind.Array:
                foreach (JsonElement item in element.EnumerateArray())
                {
                    if (ContainsSideEffectingTool(item, evidence))
                    {
                        return true;
                    }
                }

                return false;

            default:
                return false;
        }
    }

    private static bool IsKnownReadOnlyToolResult(string activityType, bool toolUseObserved) =>
        (string.Equals(activityType, "tool_result", StringComparison.OrdinalIgnoreCase) && toolUseObserved) ||
        string.Equals(activityType, "web_search_tool_result", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(activityType, "web_fetch_tool_result", StringComparison.OrdinalIgnoreCase);

    private sealed class NdjsonEvidence
    {
        public bool ResultOk { get; set; }

        public bool Limited { get; set; }

        public bool SideEffectsStarted { get; set; }

        public bool ToolUseObserved { get; set; }
    }

    private async Task<bool> TryCancelAsync(RunId runId)
    {
        try
        {
            ProcessStopResult stop = await _supervisor.CancelAsync(runId, CancellationToken.None).ConfigureAwait(false);
            return !stop.ChildPending;
        }
        catch (Exception)
        {
            // 无法确认退出时必须保留 pending 语义,禁止继续下一项目。
            return false;
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
