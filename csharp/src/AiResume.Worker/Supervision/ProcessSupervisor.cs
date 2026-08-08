using System.Collections.Concurrent;
using System.Diagnostics;
using AiResume.Core;
using AiResume.Core.Abstractions;
using AiResume.Core.Contracts;

namespace AiResume.Worker.Supervision;

/// <summary>
/// ProcessSupervisor(规格 §3.3;全项目安全关键包,每个决策都写注释):
///
/// 1. 先落盘登记后 spawn:占位登记(spawn 前事务提交成功)保证任何崩溃窗口内"进程必先有登记",
///    恢复流程不丢孤儿;首次登记失败 = internal 拒绝,进程绝不启动。
/// 2. 占位行 child_pid/真实启动时间未知(spawn 后才能取得),spawn 后立即 Complete 补全;
///    补全失败不阻塞启动 —— Job Object 句柄随宿主存活,崩溃时 kill-on-close 兜底杀整树。
/// 3. 核验四类(ProcessVerdict):只有 Matched 可终止;Mismatched 只删登记不终止;
///    Unverifiable(查询失败/特征缺失)一律 fail-closed 保留;Gone(明确不存在)运行期保留登记
///    (无法区分"已退出"与"损坏登记",防误清),清理只授权给 RecoverAsync(恢复流程是宿主
///    崩溃后唯一知道"所有 Job 子进程理应已死"的时机)。
/// 4. 终止优先关闭 Job 句柄(kill-on-close 杀整棵进程树),再以宽限期轮询确认主进程 gone;
///    未确认退出前返回 childPending=true 且保留登记(S2-E 观察循环继续核验)。
/// 5. 无总时限语义:等待退出使用固定 3 秒终止宽限期,不是任务总时限;宽限期后照常返回。
/// </summary>
public sealed class ProcessSupervisor : IProcessSupervisor, IDisposable
{
    /// <summary>终止确认宽限期:单次 300ms,最多 10 次(共 3s)。仅用于确认退出,不设任务总时限。</summary>
    private static readonly TimeSpan CloseProbeInterval = TimeSpan.FromMilliseconds(300);

    private const int CloseProbeMaxTries = 10;

    private readonly IProcessRegistry _registry;
    private readonly IProcessProbe _probe;
    private readonly ConcurrentDictionary<RunId, JobEntry> _jobs = new();
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _disposed;

    public ProcessSupervisor(string registryDatabasePath, IProcessProbe? probe = null, IProcessRegistry? registry = null)
    {
        _registry = registry ?? new SqliteProcessRegistry(registryDatabasePath);
        _probe = probe ?? new NativeProcessProbe();
    }

    public async Task<ProcessStartResult> StartAsync(ProcessStartRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(request);
        if (request.RunId == default)
        {
            throw new ArgumentException("RunId 不能为空。", nameof(request));
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // 1) 先建 Job(kill-on-close):句柄先于进程存在,崩溃兜底从这一时刻起生效。
            JobObject job;
            try
            {
                job = new JobObject();
            }
            catch (Exception)
            {
                return new ProcessStartResult
                {
                    RunId = request.RunId,
                    Started = false,
                    ErrorClass = ErrorClass.Internal,
                    ErrorCode = "job_create_failed",
                };
            }

            // 2) 先落盘占位登记;失败 = internal 拒绝,绝不 spawn(安全关键顺序)。
            //    签名在 Complete 时按进程实际 exe 名重写,占位值仅保证表结构完整。
            string placeholderSignature = ProcessSignature.Compute(request.FileName);
            try
            {
                _registry.InsertPlaceholder(request.RunId, Environment.ProcessId, job.JobId, placeholderSignature);
            }
            catch (Exception)
            {
                job.Dispose();
                return new ProcessStartResult
                {
                    RunId = request.RunId,
                    Started = false,
                    ErrorClass = ErrorClass.Internal,
                    ErrorCode = "registry_write_failed",
                };
            }

            // 3) spawn。
            Process process;
            try
            {
                var psi = new ProcessStartInfo(request.FileName, request.Arguments)
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WorkingDirectory = string.IsNullOrEmpty(request.WorkingDirectory) ? Path.GetTempPath() : request.WorkingDirectory,
                };
                if (request.Environment is not null)
                {
                    foreach (var pair in request.Environment)
                    {
                        psi.Environment[pair.Key] = pair.Value ?? string.Empty;
                    }
                }

                process = Process.Start(psi) ?? throw new InvalidOperationException("Process.Start 返回 null。");
            }
            catch (Exception)
            {
                // 失败路径清理:自建占位登记必须删除(D-009 语义)。
                SafeDeleteRegistry(request.RunId);
                job.Dispose();
                return new ProcessStartResult
                {
                    RunId = request.RunId,
                    Started = false,
                    ErrorClass = ErrorClass.Internal,
                    ErrorCode = "spawn_failed",
                };
            }

            // 4) 入 Job;失败必须显式杀进程 + 删登记 + 关闭句柄,不允许留下无登记进程。
            try
            {
                job.Assign(process);
            }
            catch (Exception)
            {
                SafeKillProcess(process);
                SafeDeleteRegistry(request.RunId);
                job.Dispose();
                return new ProcessStartResult
                {
                    RunId = request.RunId,
                    Started = false,
                    ErrorClass = ErrorClass.Internal,
                    ErrorCode = "assign_job_failed",
                };
            }

            // 5) spawn 后补全登记:真实创建时间、child_pid、实际 exe 签名。
            //    补全失败不阻塞启动(占位行 + Job 兜底);后续核验若特征不可得归 Unverifiable 保留。
            DateTimeOffset startedAt = DateTimeOffset.UtcNow;
            string signature = placeholderSignature;
            ProcessProbeResult probe = _probe.Probe(process.Id);
            if (probe.Liveness == ProcessLiveness.Alive)
            {
                if (probe.StartedAt.HasValue)
                {
                    startedAt = probe.StartedAt.Value;
                }

                if (!string.IsNullOrEmpty(probe.ExePath))
                {
                    signature = ProcessSignature.Compute(probe.ExePath);
                }
            }

            try
            {
                _registry.Complete(request.RunId, process.Id, startedAt, signature);
            }
            catch (Exception)
            {
                // 补全失败:保留占位登记,恢复流程按特征核验处置。
            }

            _jobs[request.RunId] = new JobEntry(job, process);
            return new ProcessStartResult
            {
                RunId = request.RunId,
                Started = true,
                WrapperPid = Environment.ProcessId,
                ChildPid = process.Id,
                JobId = job.JobId,
            };
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<ProcessStatus> StatusAsync(RunId runId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ProcessRegistryEntry? entry = _registry.Get(runId);
            if (entry is null)
            {
                // 未登记 = 从未 spawn 或已清理:无进程。
                return new ProcessStatus { RunId = runId, Liveness = ProcessLiveness.Gone, ChildPending = false, ObservedAt = DateTimeOffset.UtcNow };
            }

            if (entry.ChildPid is null)
            {
                // 占位未补全:无法核验,保守视为未知(可能有进程,Job 兜底中)。
                return new ProcessStatus
                {
                    RunId = runId,
                    Liveness = ProcessLiveness.Unknown,
                    ChildPending = true,
                    ObservedAt = DateTimeOffset.UtcNow,
                    MonitorError = "registry_incomplete",
                };
            }

            ProcessProbeResult probe = _probe.Probe(entry.ChildPid.Value);
            return probe.Liveness switch
            {
                ProcessLiveness.Alive => new ProcessStatus
                {
                    RunId = runId,
                    Liveness = ProcessLiveness.Alive,
                    ChildPending = true,
                    ObservedAt = DateTimeOffset.UtcNow,
                    MonitorError = ProcessVerifier.Verify(entry, probe) == ProcessVerdict.Matched ? null : "registration_mismatch",
                },
                ProcessLiveness.Gone => new ProcessStatus
                {
                    // 运行期 Gone:保留登记(fail-closed),清理授权给 RecoverAsync。
                    RunId = runId,
                    Liveness = ProcessLiveness.Gone,
                    ChildPending = false,
                    ObservedAt = DateTimeOffset.UtcNow,
                },
                _ => new ProcessStatus
                {
                    RunId = runId,
                    Liveness = ProcessLiveness.Unknown,
                    ChildPending = true,
                    ObservedAt = DateTimeOffset.UtcNow,
                    MonitorError = "probe_failed",
                },
            };
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<ProcessStopResult> CancelAsync(RunId runId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ProcessRegistryEntry? entry = _registry.Get(runId);
            if (entry is null)
            {
                return new ProcessStopResult { RunId = runId, TerminateRequested = false, ChildPending = false };
            }

            if (entry.ChildPid is null)
            {
                // 占位未补全:无 PID 可终止,保留登记(fail-closed)。
                return new ProcessStopResult { RunId = runId, TerminateRequested = false, ChildPending = true };
            }

            ProcessProbeResult probe = _probe.Probe(entry.ChildPid.Value);
            return ProcessVerifier.Verify(entry, probe) switch
            {
                ProcessVerdict.Matched => await TerminateMatchedAsync(runId, entry, cancellationToken).ConfigureAwait(false),
                ProcessVerdict.Mismatched => HandleMismatched(runId),
                ProcessVerdict.Gone => new ProcessStopResult { RunId = runId, TerminateRequested = false, ChildPending = false },
                _ => new ProcessStopResult { RunId = runId, TerminateRequested = false, ChildPending = true },
            };
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// 崩溃恢复:重启后遍历登记表逐项核验处置,产出结构化报告。
    /// 这是唯一授权清理 Gone 登记的时机:宿主曾崩溃,Job 子进程理应全部死亡,
    /// 快照未命中即登记过期;Unverifiable 仍 fail-closed 保留。
    /// </summary>
    public async Task<RecoveryReport> RecoverAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var items = new List<RecoveryReportItem>();
            foreach (ProcessRegistryEntry entry in _registry.EnumerateAll())
            {
                cancellationToken.ThrowIfCancellationRequested();
                ProcessVerdict verdict;
                RecoveryAction action;
                if (entry.ChildPid is null)
                {
                    // 占位未补全:无法核验,保留(fail-closed)。
                    verdict = ProcessVerdict.Unverifiable;
                    action = RecoveryAction.KeepFailClosed;
                }
                else
                {
                    ProcessProbeResult probe = _probe.Probe(entry.ChildPid.Value);
                    verdict = ProcessVerifier.Verify(entry, probe);
                    action = verdict switch
                    {
                        ProcessVerdict.Matched => RecoveryAction.Keep,
                        ProcessVerdict.Unverifiable => RecoveryAction.KeepFailClosed,
                        _ => RecoveryAction.RemoveRegistry,
                    };
                }

                if (action == RecoveryAction.RemoveRegistry)
                {
                    SafeDeleteRegistry(entry.RunId);
                }

                items.Add(new RecoveryReportItem(entry.RunId, verdict, action));
            }

            return new RecoveryReport(items);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>宿主退出兜底:关闭全部 Job 句柄 → kill-on-close 终止所有被监督进程树。</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        foreach (JobEntry entry in _jobs.Values)
        {
            entry.Dispose();
        }

        _jobs.Clear();
        _gate.Dispose();
    }

    private async Task<ProcessStopResult> TerminateMatchedAsync(RunId runId, ProcessRegistryEntry entry, CancellationToken cancellationToken)
    {
        _jobs.TryRemove(runId, out JobEntry? jobEntry);

        // 1) 关闭 Job 句柄 → kill-on-close 终止整棵进程树(终止的优先手段)。
        jobEntry?.Job.CloseAndKill();

        // 2) 宽限期轮询确认主进程确实退出;未确认前返回 childPending=true 且保留登记。
        bool confirmedGone = false;
        for (int i = 0; i < CloseProbeMaxTries && !cancellationToken.IsCancellationRequested; i++)
        {
            if (_probe.Probe(entry.ChildPid!.Value).Liveness == ProcessLiveness.Gone)
            {
                confirmedGone = true;
                break;
            }

            try
            {
                await Task.Delay(CloseProbeInterval, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        if (confirmedGone)
        {
            SafeDeleteRegistry(runId);
            jobEntry?.Process.Dispose();
            return new ProcessStopResult { RunId = runId, TerminateRequested = true, ChildPending = false };
        }

        // 未确认退出:登记保留,运行键不释放;S2-E 观察循环继续核验。
        return new ProcessStopResult { RunId = runId, TerminateRequested = true, ChildPending = true };
    }

    /// <summary>mismatched:PID 存在但特征不符 —— 不是我们的进程,只删登记,绝不终止。</summary>
    private ProcessStopResult HandleMismatched(RunId runId)
    {
        SafeDeleteRegistry(runId);
        return new ProcessStopResult { RunId = runId, TerminateRequested = false, ChildPending = false };
    }

    private void SafeDeleteRegistry(RunId runId)
    {
        try
        {
            _registry.Delete(runId);
        }
        catch (Exception)
        {
            // 清理失败不掩盖主流程;登记残留由 RecoverAsync 兜底。
        }
    }

    private static void SafeKillProcess(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit((int)CloseProbeInterval.TotalMilliseconds * CloseProbeMaxTries);
            }
        }
        catch (Exception)
        {
            // 杀失败时 Job 句柄仍会随宿主 Dispose 兜底。
        }
    }

    private sealed class JobEntry : IDisposable
    {
        public JobObject Job { get; }

        public Process Process { get; }

        public JobEntry(JobObject job, Process process)
        {
            Job = job;
            Process = process;
        }

        public void Dispose()
        {
            Job.Dispose();
            Process.Dispose();
        }
    }
}
