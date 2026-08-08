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

    public List<ProcessStartRequest> StartRequests { get; } = new();
    public int StartCallCount { get; private set; }
    public int CancelCallCount { get; private set; }

    public FakeSupervisor(string outText, string errText = "", IEnumerable<ProcessLiveness>? livenessSequence = null)
    {
        _outText = outText;
        _errText = errText;
        _livenessSequence = livenessSequence == null
            ? new Queue<ProcessLiveness>(new[] { ProcessLiveness.Gone })
            : new Queue<ProcessLiveness>(livenessSequence);
    }

    public Task<ProcessStartResult> StartAsync(ProcessStartRequest request, CancellationToken cancellationToken)
    {
        StartCallCount++;
        StartRequests.Add(request);

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
            ErrorClass = null,
            ErrorCode = null,
        });
    }

    public Task<ProcessStatus> StatusAsync(RunId runId, CancellationToken cancellationToken)
    {
        var liveness = _livenessSequence.Count > 0 ? _livenessSequence.Dequeue() : ProcessLiveness.Gone;
        return Task.FromResult(new ProcessStatus
        {
            RunId = runId,
            Liveness = liveness,
            ChildPending = false,
            ObservedAt = DateTimeOffset.UtcNow,
            MonitorError = null,
        });
    }

    public Task<ProcessStopResult> CancelAsync(RunId runId, CancellationToken cancellationToken)
    {
        CancelCallCount++;
        return Task.FromResult(new ProcessStopResult
        {
            RunId = runId,
            TerminateRequested = true,
            ChildPending = false,
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
        // 顺序红线:ResultOk 必须压过 Limited。
        // 一次成功的运行可能在正文里谈论限流;而真被限流的运行永远不会以 is_error:false 收尾。
        // 因此同时含 "status":"blocked" 与 "is_error":false 时必须判 success,而不是 limited。
        var supervisor = new FakeSupervisor(
            "{\"type\":\"result\",\"is_error\":false,\"status\":\"blocked\"}");
        var runner = CreateRunner(supervisor);

        var result = await runner.RunAsync(_project, _config, CancellationToken.None);

        Assert.Equal("success", result.Status);
        Assert.True(result.ResultOk);
        // Limited 记录的是"限流标记出现过"这个观察事实,不是最终判定;
        // 标记确实出现了所以为 true,而最终判定由 Status 给出。两者语义不同,不可互相推断。
        Assert.True(result.Limited);
    }

    [Fact]
    public async Task OutputContainsLimitedOnly_ReturnsLimited()
    {
        // 只含 "status":"limited" → limited
        var supervisor = new FakeSupervisor("{\"status\":\"limited\"}");
        var runner = CreateRunner(supervisor);

        var result = await runner.RunAsync(_project, _config, CancellationToken.None);

        Assert.Equal("limited", result.Status);
        Assert.True(result.Limited);
        Assert.False(result.ResultOk);
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
        Assert.Equal(1, supervisor.CancelCallCount);
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