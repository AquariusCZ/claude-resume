using System.Collections.Concurrent;
using AiResume.Core;
using AiResume.Core.Abstractions;
using AiResume.Core.Contracts;
using AiResume.Worker.Fakes;

namespace AiResume.Worker.Probes;

/// <summary>
/// S5-B ClaudeCodeProviderAdapter(IProviderAdapter):probe taskKind 专用路径。
///
/// 判别:ProviderStartRequest 无 TaskKind 字段(冻结接口不改),probe 任务以
/// ProfileId="probe" 标记(与 S5-D 测试钩子、S5-C CheckerCycle 约定);其余 profile 显式拒绝。
///
/// probe 的 Start 不阻塞调用方:后台执行探测,Status 轮询结果;探测进行中返回静默指标
/// (观察循环不得判失败,静默不触发状态变更)。结果分类(规格 §4 S5-B):
/// 服务端结构化(limited/billing/auth/model_unavailable) → FailedProvider;
/// 本地类(no-claude/spawn-failed/timeout/transient/exit-*/unknown) → FailedLocal(经 Internal)。
/// 为 Stage 6 正式任务预留 Start/Status/Cancel 骨架(非 probe 一律拒绝,不假装支持)。
/// </summary>
public sealed class ClaudeCodeProviderAdapter : IProviderAdapter
{
    public const string ProbeProfileId = "probe";

    private readonly ClaudeCodeProbe _probe;
    private readonly ConcurrentDictionary<RunId, Task> _pending = new();
    private readonly ConcurrentDictionary<RunId, ClaudeProbeResult> _results = new();

    public ClaudeCodeProviderAdapter(ClaudeCodeProbe probe)
    {
        _probe = probe ?? throw new ArgumentNullException(nameof(probe));
    }

    public Task<ProviderStartResult> StartAsync(ProviderStartRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (request.ProfileId != ProbeProfileId)
        {
            // 非 probe 显式拒绝(Stage 6 才支持正式任务)。
            return Task.FromResult(new ProviderStartResult
            {
                RunId = request.RunId,
                Accepted = false,
                ErrorClass = ErrorClass.Config,
                ErrorCode = "probe_only_adapter",
            });
        }

        string model = string.IsNullOrWhiteSpace(request.Model) ? "haiku" : request.Model;
        string cwd = string.IsNullOrWhiteSpace(request.Cwd) ? Path.GetTempPath() : request.Cwd;

        var runTask = Task.Run(async () =>
        {
            ClaudeProbeResult result = await _probe.ProbeAsync(model, cwd, cancellationToken).ConfigureAwait(false);
            _results[request.RunId] = result;
            return result;
        }, CancellationToken.None);
        _pending[request.RunId] = runTask;
        _ = runTask.ContinueWith(
            _ => _pending.TryRemove(request.RunId, out Task? _),
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

        return Task.FromResult(new ProviderStartResult { RunId = request.RunId, Accepted = true });
    }

    public Task<ProviderStatus> StatusAsync(RunId runId, CancellationToken cancellationToken)
    {
        if (_results.TryGetValue(runId, out ClaudeProbeResult? result))
        {
            if (result.IsOk)
            {
                return Task.FromResult(new ProviderStatus
                {
                    RunId = runId,
                    HeartbeatAt = DateTimeOffset.UtcNow,
                    LastOutputAt = DateTimeOffset.UtcNow,
                    OutputBytes = result.OutputBytes,
                    SideEffectsStarted = false,
                });
            }

            throw new ProviderFailedException(
                ToErrorClass(result.Reason),
                "probe_" + result.Reason,
                "Claude 限额探测未就绪:" + result.Reason + "。");
        }

        if (_pending.ContainsKey(runId))
        {
            // 探测进行中:静默指标(绝不判失败,观察循环继续等待)。
            return Task.FromResult(new ProviderStatus
            {
                RunId = runId,
                HeartbeatAt = DateTimeOffset.UtcNow,
                OutputBytes = 0,
                SideEffectsStarted = false,
            });
        }

        throw new ProviderFailedException(ErrorClass.Internal, "probe_result_missing", "探测结果缺失(从未启动或已被清理)。");
    }

    public Task<ProviderStopResult> CancelAsync(RunId runId, CancellationToken cancellationToken)
    {
        _pending.TryRemove(runId, out _);
        _results.TryRemove(runId, out _);
        return Task.FromResult(new ProviderStopResult { RunId = runId, Stopped = true });
    }

    /// <summary>reason → ErrorClass(编排器按 ErrorClass 归 failed_provider/failed_local)。</summary>
    private static ErrorClass ToErrorClass(string reason) => reason switch
    {
        // 服务端结构化 → failed_provider。
        "limited" or "billing" => ErrorClass.Quota,
        "auth" => ErrorClass.Auth,
        "model_unavailable" => ErrorClass.ModelUnavailable,
        // 本地类(no-claude/spawn-failed/timeout/transient/exit-*/unknown)→ failed_local。
        _ => ErrorClass.Internal,
    };
}
