using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using AiResume.Core;
using AiResume.Core.Abstractions;
using AiResume.Core.Contracts;
using AiResume.Worker.Resume;
using Xunit;

namespace AiResume.Tests;

/// <summary>
/// 假 supervisor:记录收到的请求与调用次数,在 StartAsync 时把注入的假输出写进重定向目标文件,
/// 并按注入的序列返回 Liveness。
/// </summary>
public sealed class FakeSupervisor : IProcessSupervisor
{
    private readonly string _outText;
    private readonly string _errText;
    private readonly Queue<ProcessLiveness> _livenessSequence;
    private readonly Queue<string?> _monitorErrors;
    private readonly bool _cancelChildPending;
    private readonly bool _throwOnCancel;
    private readonly bool _throwCanceledOnStart;
    private readonly string? _startErrorCode;

    public List<ProcessStartRequest> StartRequests { get; } = new();
    public int StartCallCount { get; private set; }
    public int StatusCallCount { get; private set; }
    public int CancelCallCount { get; private set; }

    public FakeSupervisor(
        string outText,
        string errText = "",
        IEnumerable<ProcessLiveness>? livenessSequence = null,
        IEnumerable<string?>? monitorErrors = null,
        bool cancelChildPending = false,
        bool throwOnCancel = false,
        bool throwCanceledOnStart = false,
        string? startErrorCode = null)
    {
        _outText = outText;
        _errText = errText;
        _livenessSequence = livenessSequence == null
            ? new Queue<ProcessLiveness>(new[] { ProcessLiveness.Gone })
            : new Queue<ProcessLiveness>(livenessSequence);
        _monitorErrors = monitorErrors == null
            ? new Queue<string?>()
            : new Queue<string?>(monitorErrors);
        _cancelChildPending = cancelChildPending;
        _throwOnCancel = throwOnCancel;
        _throwCanceledOnStart = throwCanceledOnStart;
        _startErrorCode = startErrorCode;
    }

    public Task<ProcessStartResult> StartAsync(ProcessStartRequest request, CancellationToken cancellationToken)
    {
        StartCallCount++;
        StartRequests.Add(request);
        if (_throwCanceledOnStart)
        {
            throw new OperationCanceledException(cancellationToken);
        }

        // 从 Arguments 中解析重定向目标路径:格式为 > "路径" 2> "路径"
        var outPath = ExtractRedirectPath(request.Arguments, ">");
        var errPath = ExtractRedirectPath(request.Arguments, "2>");

        if (outPath != null)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);
            File.WriteAllText(outPath, _outText);
        }

        if (errPath != null)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(errPath)!);
            File.WriteAllText(errPath, _errText);
        }

        return Task.FromResult(new ProcessStartResult
        {
            RunId = request.RunId,
            Started = true,
            WrapperPid = 1234,
            ChildPid = 5678,
            JobId = "job-1",
            ErrorClass = _startErrorCode is null ? null : ErrorClass.Internal,
            ErrorCode = _startErrorCode,
        });
    }

    public Task<ProcessStatus> StatusAsync(RunId runId, CancellationToken cancellationToken)
    {
        StatusCallCount++;
        var liveness = _livenessSequence.Count > 0 ? _livenessSequence.Dequeue() : ProcessLiveness.Gone;
        return Task.FromResult(new ProcessStatus
        {
            RunId = runId,
            Liveness = liveness,
            ChildPending = false,
            ObservedAt = DateTimeOffset.UtcNow,
            MonitorError = _monitorErrors.Count > 0 ? _monitorErrors.Dequeue() : null,
        });
    }

    public Task<ProcessStopResult> CancelAsync(RunId runId, CancellationToken cancellationToken)
    {
        CancelCallCount++;
        if (_throwOnCancel)
        {
            throw new InvalidOperationException("模拟取消失败");
        }

        return Task.FromResult(new ProcessStopResult
        {
            RunId = runId,
            TerminateRequested = true,
            ChildPending = _cancelChildPending,
        });
    }

    private static string? ExtractRedirectPath(string arguments, string redirectOperator)
    {
        // 匹配 > "路径" 或 2> "路径"
        var pattern = $@"(?:^|\s){Regex.Escape(redirectOperator)}\s+""([^""]+)""";
        var match = Regex.Match(arguments, pattern);
        return match.Success ? match.Groups[1].Value : null;
    }
}

/// <summary>
/// ClaudeResumeRunner 测试:覆盖规格 §5.1 的 12 条要求。
/// 所有测试使用假 supervisor 与临时目录,不启动真实 claude 进程。
/// </summary>
public sealed class ClaudeResumeRunnerTests : IDisposable
{
    private readonly string _tempRoot;
    private readonly string _projectDir;
    private readonly string _claudePath;
    private readonly ProjectRef _project;
    private readonly ProductConfig _config;

    public ClaudeResumeRunnerTests()
    {
        _tempRoot = TestTemp.NewDir("airesume-tests");
        Directory.CreateDirectory(_tempRoot);

        _projectDir = Path.Combine(_tempRoot, "project");
        Directory.CreateDirectory(_projectDir);

        _claudePath = Path.Combine(_tempRoot, "claude.exe");
        File.WriteAllText(_claudePath, "fake claude");

        _project = new ProjectRef { Name = "test-project", Path = _projectDir };
        _config = new ProductConfig
        {
            ResumePrompt = "continue",
            ResumeModel = "",
            SkipPermissions = false,
        };
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempRoot))
            {
                Directory.Delete(_tempRoot, recursive: true);
            }
        }
        catch
        {
            // 清理失败忽略
        }
    }

    private ClaudeResumeRunner CreateRunner(IProcessSupervisor supervisor, string? claudeCommand = null)
    {
        return new ClaudeResumeRunner(supervisor, claudeCommand ?? _claudePath);
    }

    [Fact]
    public async Task OutputContainsResultOk_ReturnsSuccess()
    {
        // 输出含 {"type":"result","is_error":false} → success
        var supervisor = new FakeSupervisor("{\"type\":\"result\",\"is_error\":false}");
        var runner = CreateRunner(supervisor);

        var result = await runner.RunAsync(_project, _config, CancellationToken.None);

        Assert.Equal("success", result.Status);
        Assert.True(result.ResultOk);
        Assert.False(result.Limited);
    }

    [Fact]
    public async Task OutputContainsBlockedAndResultOk_ReturnsSuccess()
    {
        // 普通对象里的 status=blocked 不是 rate_limit_event,不得误判成可重试限流。
        var supervisor = new FakeSupervisor(
            "{\"type\":\"result\",\"is_error\":false,\"status\":\"blocked\"}");
        var runner = CreateRunner(supervisor);

        var result = await runner.RunAsync(_project, _config, CancellationToken.None);

        Assert.Equal("success", result.Status);
        Assert.True(result.ResultOk);
        Assert.False(result.Limited);
    }

    [Fact]
    public async Task OutputContainsLimitedOnly_ReturnsLimited()
    {
        var supervisor = new FakeSupervisor(
            "{\"type\":\"rate_limit_event\",\"rate_limit_info\":{\"status\":\"limited\"}}");
        var runner = CreateRunner(supervisor);

        var result = await runner.RunAsync(_project, _config, CancellationToken.None);

        Assert.Equal("limited", result.Status);
        Assert.True(result.Limited);
        Assert.False(result.ResultOk);
    }

    [Fact]
    public async Task 普通对象StatusBlocked_不得误判为限流()
    {
        var supervisor = new FakeSupervisor("{\"type\":\"assistant\",\"status\":\"blocked\"}");
        var runner = CreateRunner(supervisor);

        ResumeRunResult result = await runner.RunAsync(_project, _config, CancellationToken.None);

        Assert.Equal("exit-null", result.Status);
        Assert.False(result.Limited);
    }

    [Fact]
    public async Task 只读工具后限流_仍允许等待恢复()
    {
        var supervisor = new FakeSupervisor(string.Join('\n',
            "{\"type\":\"assistant\",\"message\":{\"content\":[{\"type\":\"tool_use\",\"name\":\"Read\"}]}}",
            "{\"type\":\"rate_limit_event\",\"rate_limit_info\":{\"status\":\"blocked\"}}"));
        var runner = CreateRunner(supervisor);

        ResumeRunResult result = await runner.RunAsync(_project, _config, CancellationToken.None);

        Assert.Equal("limited", result.Status);
        Assert.True(result.Limited);
        Assert.False(result.SideEffectsStarted);
    }

    [Theory]
    [InlineData("Write")]
    [InlineData("Edit")]
    [InlineData("Bash")]
    [InlineData("UnknownTool")]
    public async Task 可能产生副作用的工具后限流_禁止自动重放(string toolName)
    {
        var supervisor = new FakeSupervisor(string.Join('\n',
            $"{{\"type\":\"assistant\",\"message\":{{\"content\":[{{\"type\":\"tool_use\",\"name\":\"{toolName}\"}}]}}}}",
            "{\"type\":\"rate_limit_event\",\"rate_limit_info\":{\"status\":\"blocked\"}}"));
        var runner = CreateRunner(supervisor);

        ResumeRunResult result = await runner.RunAsync(_project, _config, CancellationToken.None);

        Assert.Equal("limited-side-effects", result.Status);
        Assert.True(result.Limited);
        Assert.True(result.SideEffectsStarted);
        Assert.True(result.StopRound);
    }

    [Fact]
    public async Task 合法JSON数组行后限流_仍按未知协议副作用禁止自动重放()
    {
        var supervisor = new FakeSupervisor(string.Join('\n',
            "[{\"type\":\"tool_use\",\"name\":\"Write\"}]",
            "{\"type\":\"rate_limit_event\",\"rate_limit_info\":{\"status\":\"blocked\"}}"));
        var runner = CreateRunner(supervisor);

        ResumeRunResult result = await runner.RunAsync(_project, _config, CancellationToken.None);

        Assert.Equal("limited-side-effects", result.Status);
        Assert.True(result.Limited);
        Assert.True(result.SideEffectsStarted);
        Assert.True(result.StopRound);
    }

    [Fact]
    public async Task OutputContainsNeither_ReturnsExitShape()
    {
        // 两者都无 → exit-<N> 或 exit-null 形状
        var supervisor = new FakeSupervisor("some random output");
        var runner = CreateRunner(supervisor);

        var result = await runner.RunAsync(_project, _config, CancellationToken.None);

        Assert.StartsWith("exit-", result.Status);
    }

    [Fact]
    public async Task PromptContainsNewline_ReturnsPromptMultiline_AndStartNotCalled()
    {
        // config.ResumePrompt 含 \n → prompt-multiline,且 StartAsync 从未被调用
        var supervisor = new FakeSupervisor("{\"type\":\"result\",\"is_error\":false}");
        var runner = CreateRunner(supervisor);
        _config.ResumePrompt = "line1\nline2";

        var result = await runner.RunAsync(_project, _config, CancellationToken.None);

        Assert.Equal("prompt-multiline", result.Status);
        Assert.Equal(0, supervisor.StartCallCount);
    }

    [Fact]
    public async Task ProjectDirectoryMissing_ReturnsNoClaude_AndStartNotCalled()
    {
        // 项目目录不存在 → no-claude,且 StartAsync 未被调用
        var supervisor = new FakeSupervisor("{\"type\":\"result\",\"is_error\":false}");
        var runner = CreateRunner(supervisor);
        var missingProject = new ProjectRef { Name = "missing", Path = Path.Combine(_tempRoot, "does-not-exist") };

        var result = await runner.RunAsync(missingProject, _config, CancellationToken.None);

        Assert.Equal("no-claude", result.Status);
        Assert.Equal(0, supervisor.StartCallCount);
    }

    [Fact]
    public async Task StartFailedWithRegistryError_ReturnsRegistryError()
    {
        // StartAsync 返回 Started=false 且 ErrorCode 含 registry → registry-error
        var failingSupervisor = new FailingStartSupervisor("registry-error");
        var runner = CreateRunner(failingSupervisor);

        var result = await runner.RunAsync(_project, _config, CancellationToken.None);

        Assert.Equal("registry-error", result.Status);
    }

    [Fact]
    public async Task StartFailedWithOtherError_ReturnsLaunchError()
    {
        // 其它 ErrorCode → launch-error
        var failingSupervisor = new FailingStartSupervisor("some-other-error");
        var runner = CreateRunner(failingSupervisor);

        var result = await runner.RunAsync(_project, _config, CancellationToken.None);

        Assert.Equal("launch-error", result.Status);
    }

    [Fact]
    public async Task EnvironmentContainsInternalRunFlag()
    {
        // AI_RESUME_INTERNAL_RUN=1 必须出现在 ProcessStartRequest.Environment 中
        var supervisor = new FakeSupervisor("{\"type\":\"result\",\"is_error\":false}");
        var runner = CreateRunner(supervisor);

        await runner.RunAsync(_project, _config, CancellationToken.None);

        var request = Assert.Single(supervisor.StartRequests);
        Assert.NotNull(request.Environment);
        Assert.True(request.Environment.ContainsKey("AI_RESUME_INTERNAL_RUN"));
        Assert.Equal("1", request.Environment["AI_RESUME_INTERNAL_RUN"]);
    }

    [Fact]
    public async Task BeforeStartCallback在spawn前收到RunId()
    {
        var supervisor = new FakeSupervisor("{\"type\":\"result\",\"is_error\":false}");
        var runner = CreateRunner(supervisor);
        RunId? observed = null;

        ResumeRunResult result = await runner.RunAsync(
            _project,
            _config,
            CancellationToken.None,
            runId =>
            {
                Assert.Empty(supervisor.StartRequests);
                observed = runId;
                return true;
            });

        Assert.Equal("success", result.Status);
        Assert.NotNull(observed);
        Assert.Equal(Assert.Single(supervisor.StartRequests).RunId, observed.Value);
    }

    [Fact]
    public async Task BeforeStartCallback拒绝时_绝不spawn也不cancel()
    {
        var supervisor = new FakeSupervisor(
            "{\"type\":\"result\",\"is_error\":false}",
            livenessSequence: new[] { ProcessLiveness.Alive });
        var runner = CreateRunner(supervisor);

        ResumeRunResult result = await runner.RunAsync(
            _project,
            _config,
            CancellationToken.None,
            _ => false);

        Assert.Equal("stopped", result.Status);
        Assert.True(result.StopRound);
        Assert.Empty(supervisor.StartRequests);
        Assert.Equal(0, supervisor.CancelCallCount);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task BeforeStartCallback拒绝时_不受取消实现影响(bool childPending, bool throwOnCancel)
    {
        var supervisor = new FakeSupervisor(
            "{\"type\":\"result\",\"is_error\":false}",
            cancelChildPending: childPending,
            throwOnCancel: throwOnCancel);
        var runner = CreateRunner(supervisor);

        ResumeRunResult result = await runner.RunAsync(
            _project,
            _config,
            CancellationToken.None,
            _ => false);

        Assert.Equal("stopped", result.Status);
        Assert.True(result.StopRound);
        Assert.Null(result.RunId);
        Assert.Empty(supervisor.StartRequests);
        Assert.Equal(0, supervisor.CancelCallCount);
    }

    [Fact]
    public async Task StartAsync被取消时_必须终止本轮()
    {
        var supervisor = new FakeSupervisor(
            "",
            throwCanceledOnStart: true);
        var runner = CreateRunner(supervisor);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        ResumeRunResult result = await runner.RunAsync(_project, _config, cts.Token);

        Assert.Equal("stopped", result.Status);
        Assert.True(result.StopRound);
        Assert.Null(result.RunId);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public async Task SkipPermissionsFlag_ControlsArgument(bool skipPermissions, bool shouldContain)
    {
        // config.SkipPermissions=false 时参数不含 --dangerously-skip-permissions;为 true 时含
        var supervisor = new FakeSupervisor("{\"type\":\"result\",\"is_error\":false}");
        var runner = CreateRunner(supervisor);
        _config.SkipPermissions = skipPermissions;

        await runner.RunAsync(_project, _config, CancellationToken.None);

        var request = Assert.Single(supervisor.StartRequests);
        Assert.Equal(shouldContain, request.Arguments.Contains("--dangerously-skip-permissions"));
    }

    [Theory]
    [InlineData("", false)]
    [InlineData("claude-3-5-sonnet", true)]
    public async Task ResumeModel_ControlsArgument(string model, bool shouldContain)
    {
        // config.ResumeModel 为空白时参数不含 --model;非空时含
        var supervisor = new FakeSupervisor("{\"type\":\"result\",\"is_error\":false}");
        var runner = CreateRunner(supervisor);
        _config.ResumeModel = model;

        await runner.RunAsync(_project, _config, CancellationToken.None);

        var request = Assert.Single(supervisor.StartRequests);
        Assert.Equal(shouldContain, request.Arguments.Contains("--model"));
    }

    [Fact]
    public async Task UnknownLivenessThenGone_CompletesNormally()
    {
        // Liveness 先返回一次 Unknown 再返回 Gone → 仍能正常完成(不得把 Unknown 判成失败)
        var supervisor = new FakeSupervisor(
            "{\"type\":\"result\",\"is_error\":false}",
            livenessSequence: new[] { ProcessLiveness.Unknown, ProcessLiveness.Gone });
        var runner = CreateRunner(supervisor);

        var result = await runner.RunAsync(_project, _config, CancellationToken.None);

        Assert.Equal("success", result.Status);
    }

    [Fact]
    public async Task CancellationTriggered_ReturnsStopped_AndCancelCalled()
    {
        // 取消令牌在运行中触发 → stopped 且 CancelAsync 被调用
        var supervisor = new FakeSupervisor(
            "{\"type\":\"result\",\"is_error\":false}",
            livenessSequence: new[] { ProcessLiveness.Alive, ProcessLiveness.Alive, ProcessLiveness.Alive });
        var runner = CreateRunner(supervisor);
        using var cts = new CancellationTokenSource();

        // 启动后立即取消
        var task = runner.RunAsync(_project, _config, cts.Token);
        cts.Cancel();

        var result = await task;

        Assert.Equal("stopped", result.Status);
        Assert.True(result.StopRound);
        Assert.Equal(1, supervisor.CancelCallCount);
    }

    [Fact]
    public async Task ShouldContinue明确拒绝_会取消正在运行的精确Run()
    {
        var supervisor = new FakeSupervisor(
            "",
            livenessSequence: new[] { ProcessLiveness.Alive });
        var runner = CreateRunner(supervisor);

        ResumeRunResult result = await runner.RunAsync(
            _project,
            _config,
            CancellationToken.None,
            beforeStart: _ => true,
            shouldContinue: _ => false);

        Assert.Equal("stopped", result.Status);
        Assert.True(result.StopRound);
        Assert.Equal(1, supervisor.CancelCallCount);
        Assert.NotNull(result.RunId);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ShouldContinue无法明确授权_按FailClosed取消(bool throwError)
    {
        var supervisor = new FakeSupervisor(
            "",
            livenessSequence: new[] { ProcessLiveness.Alive });
        var runner = CreateRunner(supervisor);

        ResumeRunResult result = await runner.RunAsync(
            _project,
            _config,
            CancellationToken.None,
            beforeStart: _ => true,
            shouldContinue: _ => throwError
                ? throw new InvalidDataException("配置损坏")
                : null);

        Assert.Equal("stopped", result.Status);
        Assert.Equal(1, supervisor.CancelCallCount);
    }

    [Fact]
    public async Task 未知工具事件后限流_禁止自动重放()
    {
        var supervisor = new FakeSupervisor(string.Join('\n',
            "{\"type\":\"assistant\",\"message\":{\"content\":[{\"type\":\"server_tool_use\",\"name\":\"FutureTool\"}]}}",
            "{\"type\":\"rate_limit_event\",\"rate_limit_info\":{\"status\":\"blocked\"}}"));
        var runner = CreateRunner(supervisor);

        ResumeRunResult result = await runner.RunAsync(_project, _config, CancellationToken.None);

        Assert.Equal("limited-side-effects", result.Status);
        Assert.True(result.SideEffectsStarted);
    }

    [Fact]
    public async Task 截断工具事件后限流_禁止自动重放()
    {
        var supervisor = new FakeSupervisor(string.Join('\n',
            "{\"type\":\"assistant\",\"message\":{\"content\":[{\"type\":\"tool_",
            "{\"type\":\"rate_limit_event\",\"rate_limit_info\":{\"status\":\"blocked\"}}"));
        var runner = CreateRunner(supervisor);

        ResumeRunResult result = await runner.RunAsync(_project, _config, CancellationToken.None);

        Assert.Equal("limited-side-effects", result.Status);
        Assert.True(result.SideEffectsStarted);
    }

    [Fact]
    public async Task 工具名称出现前输出已截断_限流仍禁止自动重放()
    {
        var supervisor = new FakeSupervisor(string.Join('\n',
            "{\"type\":\"assistant\",\"message\":{\"content\":[{",
            "{\"type\":\"rate_limit_event\",\"rate_limit_info\":{\"status\":\"blocked\"}}"));
        var runner = CreateRunner(supervisor);

        ResumeRunResult result = await runner.RunAsync(_project, _config, CancellationToken.None);

        Assert.Equal("limited-side-effects", result.Status);
        Assert.True(result.SideEffectsStarted);
    }

    [Fact]
    public async Task Owned_job_remains_active_without_becoming_monitor_error()
    {
        var supervisor = new FakeSupervisor(
            "{\"type\":\"result\",\"is_error\":false}",
            livenessSequence: Enumerable.Repeat(ProcessLiveness.Alive, 5)
                .Append(ProcessLiveness.Gone));
        var runner = new ClaudeResumeRunner(
            supervisor,
            _claudePath,
            TimeSpan.FromMilliseconds(1),
            maxConsecutiveMonitorErrors: 2);

        ResumeRunResult result = await runner.RunAsync(_project, _config, CancellationToken.None);

        Assert.Equal("success", result.Status);
        Assert.Equal(0, supervisor.CancelCallCount);
        Assert.Equal(6, supervisor.StatusCallCount);
    }

    [Fact]
    public async Task 持续MonitorError_收敛到安全终态并取消()
    {
        var supervisor = new FakeSupervisor(
            "",
            livenessSequence: Enumerable.Repeat(ProcessLiveness.Unknown, 3),
            monitorErrors: Enumerable.Repeat<string?>("registry_incomplete", 3));
        var runner = new ClaudeResumeRunner(
            supervisor,
            _claudePath,
            TimeSpan.FromMilliseconds(1),
            maxConsecutiveMonitorErrors: 3);

        ResumeRunResult result = await runner.RunAsync(_project, _config, CancellationToken.None);

        Assert.Equal("monitor-error", result.Status);
        Assert.True(result.StopRound);
        Assert.Equal(1, supervisor.CancelCallCount);
    }

    [Fact]
    public async Task Gone伴随MonitorError_不得当作正常退出()
    {
        var supervisor = new FakeSupervisor(
            "{\"type\":\"result\",\"is_error\":false}",
            livenessSequence: Enumerable.Repeat(ProcessLiveness.Gone, 2),
            monitorErrors: Enumerable.Repeat<string?>("registration_mismatch", 2));
        var runner = new ClaudeResumeRunner(
            supervisor,
            _claudePath,
            TimeSpan.FromMilliseconds(1),
            maxConsecutiveMonitorErrors: 2);

        ResumeRunResult result = await runner.RunAsync(_project, _config, CancellationToken.None);

        Assert.Equal("monitor-error", result.Status);
        Assert.Equal(1, supervisor.CancelCallCount);
    }

    [Fact]
    public async Task 已启动但Supervisor返回错误时_立即取消而不进入监控等待()
    {
        var supervisor = new FakeSupervisor(
            "",
            startErrorCode: "assign_job_failed_child_pending");
        var runner = CreateRunner(supervisor);

        ResumeRunResult result = await runner.RunAsync(_project, _config, CancellationToken.None);

        Assert.Equal("launch-error", result.Status);
        Assert.True(result.StopRound);
        Assert.NotNull(result.RunId);
        Assert.Equal(1, supervisor.CancelCallCount);
        Assert.Equal(0, supervisor.StatusCallCount);
    }
}

/// <summary>
/// 模拟启动失败的假 supervisor。
/// </summary>
public sealed class FailingStartSupervisor : IProcessSupervisor
{
    private readonly string _errorCode;

    public FailingStartSupervisor(string errorCode)
    {
        _errorCode = errorCode;
    }

    public Task<ProcessStartResult> StartAsync(ProcessStartRequest request, CancellationToken cancellationToken)
    {
        return Task.FromResult(new ProcessStartResult
        {
            RunId = request.RunId,
            Started = false,
            WrapperPid = null,
            ChildPid = null,
            JobId = null,
            ErrorClass = ErrorClass.Internal,
            ErrorCode = _errorCode,
        });
    }

    public Task<ProcessStatus> StatusAsync(RunId runId, CancellationToken cancellationToken)
    {
        return Task.FromResult(new ProcessStatus
        {
            RunId = runId,
            Liveness = ProcessLiveness.Gone,
            ChildPending = false,
            ObservedAt = DateTimeOffset.UtcNow,
            MonitorError = null,
        });
    }

    public Task<ProcessStopResult> CancelAsync(RunId runId, CancellationToken cancellationToken)
    {
        return Task.FromResult(new ProcessStopResult
        {
            RunId = runId,
            TerminateRequested = true,
            ChildPending = false,
        });
    }
}
