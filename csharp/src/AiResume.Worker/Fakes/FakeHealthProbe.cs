using AiResume.Core;
using AiResume.Core.Abstractions;
using AiResume.Core.Contracts;

namespace AiResume.Worker.Fakes;

/// <summary>
/// FakeHealthProbe:固定返回注入的健康状态(S2-E 骨架;真实探测逻辑属 Stage 5)。
/// 契约要点(不设客户端总时限、静默不判失败、childPending 禁探测)由注入的
/// TaskKind=probe 任务驱动编排器执行,本类只按注入值应答。
/// </summary>
public sealed class FakeHealthProbe : IHealthProbe
{
    private readonly ITaskOrchestrator _orchestrator;

    public FakeHealthProbe(ITaskOrchestrator orchestrator)
    {
        _orchestrator = orchestrator;
    }

    public Task<StartResponse> StartAsync(StartRequest request, CancellationToken cancellationToken)
    {
        if (request.TaskKind != TaskKind.Probe)
        {
            throw new ArgumentException("健康探测只允许 TaskKind.Probe。", nameof(request));
        }

        return _orchestrator.StartAsync(request, cancellationToken);
    }

    public Task<RunSnapshot> StatusAsync(RunId runId, CancellationToken cancellationToken) =>
        _orchestrator.StatusAsync(runId, cancellationToken);

    public Task<CancelResponse> CancelAsync(CancelRequest request, CancellationToken cancellationToken) =>
        _orchestrator.CancelAsync(request, cancellationToken);
}
