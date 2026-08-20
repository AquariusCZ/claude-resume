using System.Collections.Concurrent;
using System.Diagnostics;
using AiResume.Core;
using AiResume.Core.Abstractions;
using AiResume.Core.Contracts;

namespace AiResume.Worker.Supervision;

/// <summary>
/// ProcessSupervisor(规格 §3.3;全项目安全关键包,每个决策都写注释):
///
/// 1. 先落盘占位登记再 spawn:首次登记失败 = internal 拒绝,进程绝不启动。Process.Start
///    不能 suspended create，Start→Assign 仍有极短崩溃窗口；这是现役明确记录的残余风险。
/// 2. spawn 后立即补全 child_pid/创建时间/签名并加入 Job。Assign 失败先保留精确 PID，
///    只有确认进程退出才删登记；杀不掉时继续由 Status/Cancel 持有并收敛。
/// 3. 本进程持有的 Job 是精确 RunId 所有权证据，状态与取消优先直接查询 Job，不依赖
///    registry 可读；没有本地 Job 时才按 ProcessVerifier 的 Matched/Mismatched/Unverifiable/Gone 语义处理。
/// 4. 正常终态和取消都以 Job 的 ActiveProcesses=0 为完整进程树证据。取消调用
///    TerminateJobObject 后保持句柄并轮询整棵树，不能只看外层 cmd PID。
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

            // 4) 入 Job。Process.Start 本身不支持 suspended create，这里仍存在极短的
            // Start→Assign 崩溃窗口；因此 Assign 失败时先补全精确 PID 登记，再终止并确认。
            // 若无法确认退出，必须继续把这次运行交给 Status/Cancel 收敛，绝不能删登记后失联。
            try
            {
                job.Assign(process);
            }
            catch (Exception)
            {
                CompleteRegistryBestEffort(request.RunId, process, placeholderSignature);
                if (TryKillProcessAndConfirm(process))
                {
                    SafeDeleteRegistry(request.RunId);
                    process.Dispose();
                    job.Dispose();
                    return new ProcessStartResult
                    {
                        RunId = request.RunId,
                        Started = false,
                        ErrorClass = ErrorClass.Internal,
                        ErrorCode = "assign_job_failed",
                    };
                }

                _jobs[request.RunId] = new JobEntry(job, process, isAssigned: false);
                return new ProcessStartResult
                {
                    RunId = request.RunId,
                    Started = true,
                    WrapperPid = Environment.ProcessId,
                    ChildPid = process.Id,
                    JobId = job.JobId,
                    ErrorClass = ErrorClass.Internal,
                    ErrorCode = "assign_job_failed_child_pending",
                };
            }

            // 5) spawn 后补全登记:真实创建时间、child_pid、实际 exe 签名。
            //    补全失败不阻塞启动(占位行 + Job 兜底);后续核验若特征不可得归 Unverifiable 保留。
            CompleteRegistryBestEffort(request.RunId, process, placeholderSignature);

            _jobs[request.RunId] = new JobEntry(job, process, isAssigned: true);
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
            if (_jobs.TryGetValue(runId, out JobEntry? ownedJob))
            {
                return InspectOwnedJob(runId, ownedJob);
            }

            ProcessRegistryEntry? entry = _registry.Get(runId);
            if (entry is null)
            {
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
            if (probe.Liveness == ProcessLiveness.Alive)
            {
                return new ProcessStatus
                {
                    RunId = runId,
                    Liveness = ProcessLiveness.Alive,
                    ChildPending = true,
                    ObservedAt = DateTimeOffset.UtcNow,
                    MonitorError = ProcessVerifier.Verify(entry, probe) == ProcessVerdict.Matched ? null : "registration_mismatch",
                };
            }

            return probe.Liveness == ProcessLiveness.Gone
                ? new ProcessStatus
                {
                    // 重启后没有本地 Job 句柄时仍保留登记，清理由 RecoverAsync 授权。
                    RunId = runId,
                    Liveness = ProcessLiveness.Gone,
                    ChildPending = false,
                    ObservedAt = DateTimeOffset.UtcNow,
                }
                : new ProcessStatus
                {
                    RunId = runId,
                    Liveness = ProcessLiveness.Unknown,
                    ChildPending = true,
                    ObservedAt = DateTimeOffset.UtcNow,
                    MonitorError = "probe_failed",
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
            if (_jobs.TryGetValue(runId, out JobEntry? ownedJob))
            {
                return await TerminateOwnedAsync(runId, ownedJob, cancellationToken).ConfigureAwait(false);
            }

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
                ProcessVerdict.Matched => await TerminateMatchedExternalAsync(runId, entry, cancellationToken).ConfigureAwait(false),
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

    private ProcessStatus InspectOwnedJob(RunId runId, JobEntry jobEntry)
    {
        if (!jobEntry.IsAssigned)
        {
            try
            {
                if (_probe.Probe(jobEntry.Process.Id).Liveness == ProcessLiveness.Gone)
                {
                    CleanupOwnedRun(runId, deleteRegistry: true);
                    return GoneStatus(runId);
                }
            }
            catch (Exception)
            {
                // 继续按未知处理，保留精确 Process 对象供 Cancel/Dispose 收敛。
            }

            return new ProcessStatus
            {
                RunId = runId,
                Liveness = ProcessLiveness.Unknown,
                ChildPending = true,
                ObservedAt = DateTimeOffset.UtcNow,
                MonitorError = "job_assignment_failed",
            };
        }

        try
        {
            if (jobEntry.Job.GetActiveProcessCount() == 0)
            {
                CleanupOwnedRun(runId, deleteRegistry: true);
                return GoneStatus(runId);
            }

            return new ProcessStatus
            {
                RunId = runId,
                Liveness = ProcessLiveness.Alive,
                ChildPending = true,
                ObservedAt = DateTimeOffset.UtcNow,
            };
        }
        catch (Exception)
        {
            return new ProcessStatus
            {
                RunId = runId,
                Liveness = ProcessLiveness.Unknown,
                ChildPending = true,
                ObservedAt = DateTimeOffset.UtcNow,
                MonitorError = "job_query_failed",
            };
        }
    }

    private async Task<ProcessStopResult> TerminateOwnedAsync(
        RunId runId,
        JobEntry jobEntry,
        CancellationToken cancellationToken)
    {
        if (!jobEntry.IsAssigned)
        {
            bool requested = TryKillProcessAndConfirm(jobEntry.Process);
            if (requested)
            {
                CleanupOwnedRun(runId, deleteRegistry: true);
                return new ProcessStopResult { RunId = runId, TerminateRequested = true, ChildPending = false };
            }

            return new ProcessStopResult { RunId = runId, TerminateRequested = true, ChildPending = true };
        }

        int activeCount;
        try
        {
            activeCount = jobEntry.Job.GetActiveProcessCount();
        }
        catch (Exception)
        {
            return new ProcessStopResult { RunId = runId, TerminateRequested = false, ChildPending = true };
        }

        if (activeCount == 0)
        {
            CleanupOwnedRun(runId, deleteRegistry: true);
            return new ProcessStopResult { RunId = runId, TerminateRequested = false, ChildPending = false };
        }

        try
        {
            jobEntry.Job.TerminateAll();
        }
        catch (Exception)
        {
            return new ProcessStopResult { RunId = runId, TerminateRequested = false, ChildPending = true };
        }

        for (int i = 0; i < CloseProbeMaxTries && !cancellationToken.IsCancellationRequested; i++)
        {
            try
            {
                if (jobEntry.Job.GetActiveProcessCount() == 0)
                {
                    CleanupOwnedRun(runId, deleteRegistry: true);
                    return new ProcessStopResult { RunId = runId, TerminateRequested = true, ChildPending = false };
                }
            }
            catch (Exception)
            {
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

        return new ProcessStopResult { RunId = runId, TerminateRequested = true, ChildPending = true };
    }

    private async Task<ProcessStopResult> TerminateMatchedExternalAsync(
        RunId runId,
        ProcessRegistryEntry entry,
        CancellationToken cancellationToken)
    {
        bool terminateRequested = false;
        try
        {
            using var process = Process.GetProcessById(entry.ChildPid!.Value);
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                terminateRequested = true;
            }
        }
        catch (ArgumentException)
        {
            SafeDeleteRegistry(runId);
            return new ProcessStopResult { RunId = runId, TerminateRequested = false, ChildPending = false };
        }
        catch (Exception)
        {
            return new ProcessStopResult { RunId = runId, TerminateRequested = terminateRequested, ChildPending = true };
        }

        for (int i = 0; i < CloseProbeMaxTries && !cancellationToken.IsCancellationRequested; i++)
        {
            if (_probe.Probe(entry.ChildPid!.Value).Liveness == ProcessLiveness.Gone)
            {
                SafeDeleteRegistry(runId);
                return new ProcessStopResult
                {
                    RunId = runId,
                    TerminateRequested = terminateRequested,
                    ChildPending = false,
                };
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

        return new ProcessStopResult
        {
            RunId = runId,
            TerminateRequested = terminateRequested,
            ChildPending = true,
        };
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

    private void CompleteRegistryBestEffort(RunId runId, Process process, string placeholderSignature)
    {
        DateTimeOffset startedAt = DateTimeOffset.UtcNow;
        string signature = placeholderSignature;
        try
        {
            ProcessProbeResult probe = _probe.Probe(process.Id);
            if (probe.Liveness == ProcessLiveness.Alive)
            {
                startedAt = probe.StartedAt ?? startedAt;
                if (!string.IsNullOrEmpty(probe.ExePath))
                {
                    signature = ProcessSignature.Compute(probe.ExePath);
                }
            }
        }
        catch (Exception)
        {
            // 仍使用当前时间与占位签名补全 PID；后续核验会按 Unverifiable fail-closed。
        }

        try
        {
            _registry.Complete(runId, process.Id, startedAt, signature);
        }
        catch (Exception)
        {
            // 补全失败:保留占位登记与本地 Job/Process 所有权。
        }
    }

    private void CleanupOwnedRun(RunId runId, bool deleteRegistry)
    {
        if (_jobs.TryRemove(runId, out JobEntry? jobEntry))
        {
            jobEntry.Dispose();
        }

        if (deleteRegistry)
        {
            SafeDeleteRegistry(runId);
        }
    }

    private static ProcessStatus GoneStatus(RunId runId) => new()
    {
        RunId = runId,
        Liveness = ProcessLiveness.Gone,
        ChildPending = false,
        ObservedAt = DateTimeOffset.UtcNow,
    };

    private static bool TryKillProcessAndConfirm(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit((int)CloseProbeInterval.TotalMilliseconds * CloseProbeMaxTries);
            }

            return process.HasExited;
        }
        catch (Exception)
        {
            try
            {
                return process.HasExited;
            }
            catch (Exception)
            {
                return false;
            }
        }
    }

    private sealed class JobEntry : IDisposable
    {
        public JobObject Job { get; }

        public Process Process { get; }

        public bool IsAssigned { get; }

        public JobEntry(JobObject job, Process process, bool isAssigned)
        {
            Job = job;
            Process = process;
            IsAssigned = isAssigned;
        }

        public void Dispose()
        {
            if (!IsAssigned)
            {
                TryKillProcessAndConfirm(Process);
            }

            Job.Dispose();
            Process.Dispose();
        }
    }
}
