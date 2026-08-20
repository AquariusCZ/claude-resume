using AiResume.Core;
using AiResume.Core.Abstractions;
using AiResume.Core.Contracts;

namespace AiResume.Worker.Fakes;

/// <summary>
/// FakeProviderAdapter:可编程脚本(按序产出 progress 事件、最终成功/失败/挂起),用于测试(S2-E)。
///
/// 脚本步语义:
/// - Progress:返回一次指标(LastOutputAt 更新),消费一步。
/// - SideEffect:置 SideEffectsStarted=true,消费一步(复刻 D-002 副作用活动语义)。
/// - Success:返回指标,消费一步;之后 StatusAsync 返回同一最终指标(不再消费)。
/// - Fail:抛 ProviderFailedException(ErrorClass/ErrorCode 可编程),消费一步。
/// - Hang:不消费队列,每次返回相同指标(模拟挂起;观察循环必须不判失败)。
///
/// StartAsync 可编程为启动拒绝(返回 Accepted=false + ErrorClass,对应编排器 failed 路径)。
/// 调用计数(StartCalls/CancelCalls/StatusCalls)供测试断言"绝无第二次 provider 调用"。
/// </summary>
public sealed class FakeProviderAdapter : IProviderAdapter
{
    public enum StepKind
    {
        Progress,
        SideEffect,
        Success,
        Fail,
        Hang,
    }

    public sealed record Step(StepKind Kind, ErrorClass? Error = null, string? ErrorCode = null)
    {
        public static Step ProgressStep() => new(StepKind.Progress);

        public static Step SideEffectStep() => new(StepKind.SideEffect);

        public static Step SuccessStep() => new(StepKind.Success);

        public static Step FailStep(ErrorClass errorClass, string errorCode) => new(StepKind.Fail, errorClass, errorCode);

        public static Step HangStep() => new(StepKind.Hang);
    }

    private readonly Queue<Step> _steps;
    private readonly bool _startRejected;
    private readonly ErrorClass? _startErrorClass;
    private readonly string? _startErrorCode;
    private int _statusCalls;

    public FakeProviderAdapter(IEnumerable<Step>? script = null, bool startRejected = false,
        ErrorClass? startErrorClass = null, string? startErrorCode = null)
    {
        _steps = new Queue<Step>(script ?? new[] { Step.SuccessStep() });
        _startRejected = startRejected;
        _startErrorClass = startErrorClass;
        _startErrorCode = startErrorCode;
    }

    public int StartCalls { get; private set; }

    public int CancelCalls { get; private set; }

    public int StatusCalls => _statusCalls;

    public bool MarkedSideEffects { get; private set; }

    public bool Completed { get; private set; }

    public Func<ProviderStartRequest, CancellationToken, Task<ProviderStartResult>>? StartHandler { get; set; }

    public Task<ProviderStartResult> StartAsync(ProviderStartRequest request, CancellationToken cancellationToken)
    {
        StartCalls++;
        if (StartHandler is not null)
        {
            return StartHandler(request, cancellationToken);
        }

        if (_startRejected)
        {
            return Task.FromResult(new ProviderStartResult
            {
                RunId = request.RunId,
                Accepted = false,
                ErrorClass = _startErrorClass ?? ErrorClass.Internal,
                ErrorCode = _startErrorCode ?? "fake_start_rejected",
            });
        }

        return Task.FromResult(new ProviderStartResult { RunId = request.RunId, Accepted = true });
    }

    public Task<ProviderStatus> StatusAsync(RunId runId, CancellationToken cancellationToken)
    {
        _statusCalls++;
        if (_steps.Count == 0)
        {
            // 脚本已耗尽:返回最终指标,不再消费(成功收尾)。
            return Task.FromResult(new ProviderStatus
            {
                RunId = runId,
                HeartbeatAt = DateTimeOffset.UtcNow,
                LastOutputAt = DateTimeOffset.UtcNow,
                OutputBytes = 1,
                SideEffectsStarted = MarkedSideEffects,
            });
        }

        Step step = _steps.Peek();
        if (step.Kind == StepKind.Hang)
        {
            // 挂起:不消费队列,每次返回相同指标(静默;观察循环不得判失败)。
            return Task.FromResult(new ProviderStatus
            {
                RunId = runId,
                HeartbeatAt = DateTimeOffset.UtcNow,
                OutputBytes = 0,
                SideEffectsStarted = MarkedSideEffects,
            });
        }

        _steps.Dequeue();
        switch (step.Kind)
        {
            case StepKind.Progress:
                return Task.FromResult(new ProviderStatus
                {
                    RunId = runId,
                    HeartbeatAt = DateTimeOffset.UtcNow,
                    LastOutputAt = DateTimeOffset.UtcNow,
                    OutputBytes = 1,
                    SideEffectsStarted = MarkedSideEffects,
                });
            case StepKind.SideEffect:
                MarkedSideEffects = true;
                return Task.FromResult(new ProviderStatus
                {
                    RunId = runId,
                    HeartbeatAt = DateTimeOffset.UtcNow,
                    LastOutputAt = DateTimeOffset.UtcNow,
                    SideEffectsStarted = true,
                });
            case StepKind.Success:
                Completed = true;
                return Task.FromResult(new ProviderStatus
                {
                    RunId = runId,
                    HeartbeatAt = DateTimeOffset.UtcNow,
                    LastOutputAt = DateTimeOffset.UtcNow,
                    OutputBytes = 1,
                    SideEffectsStarted = MarkedSideEffects,
                });
            case StepKind.Fail:
                Completed = true;
                throw new ProviderFailedException(
                    step.Error ?? ErrorClass.Internal,
                    step.ErrorCode ?? "fake_failed",
                    "FakeProvider 脚本到达 Fail 步。");
            default:
                throw new InvalidOperationException($"未知脚本步 {step.Kind}。");
        }
    }

    public Task<ProviderStopResult> CancelAsync(RunId runId, CancellationToken cancellationToken)
    {
        CancelCalls++;
        return Task.FromResult(new ProviderStopResult { RunId = runId, Stopped = true });
    }
}
