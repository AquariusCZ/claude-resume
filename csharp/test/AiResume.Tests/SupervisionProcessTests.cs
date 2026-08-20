using AiResume.Core;
using AiResume.Core.Contracts;
using AiResume.Storage;
using AiResume.Worker.Supervision;
using Microsoft.Data.Sqlite;
using System.Diagnostics;
using Xunit;

namespace AiResume.Tests;

/// <summary>
/// S2-D ProcessSupervisor 真进程测试(安全关键,工作单 §S2-D):
/// 先登记后 spawn、树杀 0 残留、mismatched 拒绝终止、完整 Job 终态核验、
/// RecoverAsync 清理、首次登记失败 internal 拒绝。
/// 命令使用工作单指定的无害长命令(cmd + ping 回环),测试自身 finally 兜底清理全部残留进程。
/// </summary>
[Collection(SqliteCollection.Name)]
public sealed class SupervisionProcessTests : IDisposable
{
    private const string LongPing = "/c ping -n 30 127.0.0.1 > NUL";

    private readonly string _dir;
    private readonly string _dbPath;
    private readonly List<int> _spawnedPids = new();
    private readonly NativeProcessProbe _probe = new();

    public SupervisionProcessTests()
    {
        _dir = TestTemp.NewDir("airesume-supervision-tests");
        Directory.CreateDirectory(_dir);
        _dbPath = Path.Combine(_dir, "supervision.db");
        StorageDatabase.Migrate(_dbPath);
    }

    public void Dispose()
    {
        // 兜底:任何残留测试进程一律整树终止(finally 语义,工作单要求)。
        foreach (int pid in _spawnedPids)
        {
            try
            {
                using var p = Process.GetProcessById(pid);
                if (!p.HasExited)
                {
                    p.Kill(entireProcessTree: true);
                    p.WaitForExit(3000);
                }
            }
            catch
            {
                // 进程已不存在,无需清理。
            }
        }

        SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch
        {
            // 测试临时目录残留可容忍。
        }
    }

    private ProcessStartRequest NewRequest(RunId runId, string arguments) => new()
    {
        RunId = runId,
        FileName = "cmd.exe",
        Arguments = arguments,
    };

    private void Track(params int[] pids)
    {
        foreach (int pid in pids)
        {
            _spawnedPids.Add(pid);
        }
    }

    private int FindGrandchildPid(int childPid, int timeoutMs = 8000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            foreach (ProcessSnapshotEntry entry in _probe.EnumerateAll())
            {
                if (entry.ParentPid == childPid)
                {
                    return entry.Pid;
                }
            }

            Thread.Sleep(100);
        }

        return -1;
    }

    private int NonexistentPid()
    {
        var existing = new HashSet<int>(_probe.EnumerateAll().Select(e => e.Pid));
        int pid = 100_000;
        while (existing.Contains(pid))
        {
            pid++;
        }

        return pid;
    }

    [Fact]
    public async Task Start_registers_before_spawn_and_completes_fields()
    {
        using var supervisor = new ProcessSupervisor(_dbPath);
        RunId runId = RunId.New();
        ProcessStartResult result = await supervisor.StartAsync(NewRequest(runId, LongPing), CancellationToken.None);

        Assert.True(result.Started);
        Assert.NotNull(result.ChildPid);
        Assert.False(string.IsNullOrEmpty(result.JobId));
        Track(result.ChildPid!.Value);

        // 登记必须已补全:child_pid 一致、签名按实际 exe 文件名、启动时间在 ±5s 容差内。
        using (var connection = StorageDatabase.Open(_dbPath))
        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = """
                SELECT child_pid, job_id, started_at, command_signature
                FROM process_registry WHERE run_id = $run_id;
                """;
            cmd.Parameters.AddWithValue("$run_id", runId.ToString());
            using var reader = cmd.ExecuteReader();
            Assert.True(reader.Read(), "Start 后登记必须存在。");
            Assert.Equal(result.ChildPid!.Value, reader.GetInt32(0));
            Assert.Equal(result.JobId, reader.GetString(1));

            DateTimeOffset registered = DateTimeOffset.Parse(reader.GetString(2));
            ProcessProbeResult probe = _probe.Probe(result.ChildPid!.Value);
            Assert.Equal(ProcessLiveness.Alive, probe.Liveness);
            Assert.NotNull(probe.StartedAt);
            Assert.True(Math.Abs((probe.StartedAt.Value - registered).TotalSeconds) <= 5,
                "登记启动时间必须与进程真实创建时间吻合(±5s)。");
            Assert.Equal(ProcessSignature.Compute(probe.ExePath!), reader.GetString(3));
        }
    }

    [Fact]
    public async Task Cancel_matched_kills_process_tree_with_zero_residue()
    {
        using var supervisor = new ProcessSupervisor(_dbPath);
        RunId runId = RunId.New();
        // 造孙进程:start /b 后台启动孙 ping,自身再跑前台 ping。
        ProcessStartResult result = await supervisor.StartAsync(
            NewRequest(runId, "/c start /b ping -n 60 127.0.0.1 > NUL & ping -n 30 127.0.0.1 > NUL"),
            CancellationToken.None);
        Assert.True(result.Started);
        Track(result.ChildPid!.Value);

        int grandchildPid = FindGrandchildPid(result.ChildPid!.Value);
        Assert.True(grandchildPid > 0, "必须能找到孙进程(树杀测试前提)。");
        Track(grandchildPid);

        ProcessStopResult cancel = await supervisor.CancelAsync(runId, CancellationToken.None);
        Assert.True(cancel.TerminateRequested);
        Assert.False(cancel.ChildPending, "Job 关闭后进程树应在宽限期内确认退出。");

        // 0 残留:主进程与孙进程都必须 gone;登记已删。
        var after = _probe.EnumerateAll();
        Assert.DoesNotContain(after, e => e.Pid == result.ChildPid!.Value);
        Assert.DoesNotContain(after, e => e.Pid == grandchildPid);
        using (var connection = StorageDatabase.Open(_dbPath))
        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = "SELECT COUNT(*) FROM process_registry WHERE run_id = $run_id;";
            cmd.Parameters.AddWithValue("$run_id", runId.ToString());
            Assert.Equal(0L, (long)cmd.ExecuteScalar()!);
        }
    }

    [Fact]
    public async Task Cancel_mismatched_registration_is_removed_without_killing_process()
    {
        RunId runId = RunId.New();
        using var process = Process.Start(new ProcessStartInfo("cmd.exe", LongPing)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
        }) ?? throw new InvalidOperationException("测试进程启动失败。 ");
        Track(process.Id);
        ProcessProbeResult actual = _probe.Probe(process.Id);
        Assert.Equal(ProcessLiveness.Alive, actual.Liveness);

        // 伪造一个不属于当前 supervisor 的登记，且启动时间明确不符。
        using (var connection = StorageDatabase.Open(_dbPath))
        {
            StorageDatabase.Execute(connection, """
                INSERT INTO process_registry (run_id, parent_pid, child_pid, job_id, started_at, command_signature, updated_at)
                VALUES ($run_id, $parent, $child, 'job-external', $started_at, $signature, $updated_at);
                """, null,
                ("$run_id", runId.ToString()),
                ("$parent", Environment.ProcessId),
                ("$child", process.Id),
                ("$started_at", (actual.StartedAt ?? DateTimeOffset.UtcNow).AddMinutes(-10).ToString("o")),
                ("$signature", ProcessSignature.Compute(actual.ExePath ?? "cmd.exe")),
                ("$updated_at", DateTimeOffset.UtcNow.ToString("o")));
        }

        using var supervisor = new ProcessSupervisor(_dbPath);
        ProcessStopResult cancel = await supervisor.CancelAsync(runId, CancellationToken.None);
        Assert.False(cancel.TerminateRequested, "mismatched 禁止终止。");
        Assert.False(cancel.ChildPending);

        // 进程必须仍然存活(未被误杀);登记已删(只删登记)。
        ProcessProbeResult alive = _probe.Probe(process.Id);
        Assert.Equal(ProcessLiveness.Alive, alive.Liveness);
        using (var connection = StorageDatabase.Open(_dbPath))
        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = "SELECT COUNT(*) FROM process_registry WHERE run_id = $run_id;";
            cmd.Parameters.AddWithValue("$run_id", runId.ToString());
            Assert.Equal(0L, (long)cmd.ExecuteScalar()!);
        }
    }

    [Fact]
    public async Task Cancel_owned_job_uses_exact_in_memory_ownership_when_registry_is_tampered()
    {
        using var supervisor = new ProcessSupervisor(_dbPath);
        RunId runId = RunId.New();
        ProcessStartResult result = await supervisor.StartAsync(NewRequest(runId, LongPing), CancellationToken.None);
        Assert.True(result.Started);
        Track(result.ChildPid!.Value);

        using (var connection = StorageDatabase.Open(_dbPath))
        {
            StorageDatabase.Execute(connection,
                "UPDATE process_registry SET started_at = $started_at WHERE run_id = $run_id;",
                null,
                ("$started_at", DateTimeOffset.UtcNow.AddMinutes(-10).ToString("o")),
                ("$run_id", runId.ToString()));
        }

        ProcessStopResult cancel = await supervisor.CancelAsync(runId, CancellationToken.None);
        Assert.True(cancel.TerminateRequested);
        Assert.False(cancel.ChildPending);
        await WaitGoneAsync(result.ChildPid.Value);
        Assert.Null(GetRegistryRow(runId));
    }

    [Fact]
    public async Task Cancel_gone_registration_is_kept_fail_closed()
    {
        using var supervisor = new ProcessSupervisor(_dbPath);
        RunId runId = RunId.New();
        int pid = NonexistentPid();

        // 写入合法结构的登记,child_pid 指向不存在的 PID(模拟损坏/过期登记)。
        using (var connection = StorageDatabase.Open(_dbPath))
        {
            StorageDatabase.Execute(connection, """
                INSERT INTO process_registry (run_id, parent_pid, child_pid, job_id, started_at, command_signature, updated_at)
                VALUES ($run_id, $parent, $child, 'job-fake', $started_at, $signature, $updated_at);
                """, null,
                ("$run_id", runId.ToString()),
                ("$parent", Environment.ProcessId),
                ("$child", pid),
                ("$started_at", DateTimeOffset.UtcNow.ToString("o")),
                ("$signature", ProcessSignature.Compute("cmd.exe")),
                ("$updated_at", DateTimeOffset.UtcNow.ToString("o")));
        }

        // 运行期 Gone:不终止、不删登记(fail-closed,清理只授权 RecoverAsync)。
        ProcessStopResult cancel = await supervisor.CancelAsync(runId, CancellationToken.None);
        Assert.False(cancel.TerminateRequested);
        Assert.False(cancel.ChildPending);
        Assert.NotNull(GetRegistryRow(runId));
    }

    [Fact]
    public async Task Status_reports_liveness_and_cleans_owned_job_after_exit()
    {
        using var supervisor = new ProcessSupervisor(_dbPath);
        RunId runId = RunId.New();
        ProcessStartResult result = await supervisor.StartAsync(NewRequest(runId, LongPing), CancellationToken.None);
        Assert.True(result.Started);
        Track(result.ChildPid!.Value);

        ProcessStatus running = await supervisor.StatusAsync(runId, CancellationToken.None);
        Assert.Equal(ProcessLiveness.Alive, running.Liveness);
        Assert.True(running.ChildPending);

        // 外部杀死进程(模拟崩溃/异常退出)。
        using (var p = Process.GetProcessById(result.ChildPid!.Value))
        {
            p.Kill(entireProcessTree: true);
            p.WaitForExit(3000);
        }

        ProcessStatus gone = await supervisor.StatusAsync(runId, CancellationToken.None);
        Assert.Equal(ProcessLiveness.Gone, gone.Liveness);
        Assert.False(gone.ChildPending);
        // 本进程仍持有精确 Job 且 ActiveProcesses=0，正常终态应释放句柄并清登记。
        Assert.Null(GetRegistryRow(runId));
    }

    [Fact]
    public async Task Status_wrapper_gone_but_job_descendant_active_remains_pending()
    {
        using var supervisor = new ProcessSupervisor(_dbPath);
        RunId runId = RunId.New();
        ProcessStartResult result = await supervisor.StartAsync(
            NewRequest(runId, "/c start /b ping -n 30 127.0.0.1 > NUL & exit /b 0"),
            CancellationToken.None);
        Assert.True(result.Started);
        Track(result.ChildPid!.Value);

        int grandchildPid = FindGrandchildPid(result.ChildPid.Value);
        Assert.True(grandchildPid > 0, "必须先观察到后台后代进程。 ");
        Track(grandchildPid);
        await WaitGoneAsync(result.ChildPid.Value);

        ProcessStatus status = await supervisor.StatusAsync(runId, CancellationToken.None);
        Assert.Equal(ProcessLiveness.Alive, status.Liveness);
        Assert.True(status.ChildPending);
        Assert.Null(status.MonitorError);

        ProcessStopResult cancel = await supervisor.CancelAsync(runId, CancellationToken.None);
        Assert.True(cancel.TerminateRequested);
        Assert.False(cancel.ChildPending);
        await WaitGoneAsync(grandchildPid);
        Assert.Null(GetRegistryRow(runId));
    }

    [Fact]
    public async Task Missing_registry_still_uses_owned_job_for_status_and_cancel()
    {
        using var supervisor = new ProcessSupervisor(_dbPath);
        RunId runId = RunId.New();
        ProcessStartResult result = await supervisor.StartAsync(NewRequest(runId, LongPing), CancellationToken.None);
        Assert.True(result.Started);
        Track(result.ChildPid!.Value);

        using (var connection = StorageDatabase.Open(_dbPath))
        {
            StorageDatabase.Execute(connection,
                "DELETE FROM process_registry WHERE run_id = $run_id;",
                null,
                ("$run_id", runId.ToString()));
        }

        ProcessStatus status = await supervisor.StatusAsync(runId, CancellationToken.None);
        Assert.Equal(ProcessLiveness.Alive, status.Liveness);
        Assert.True(status.ChildPending);
        Assert.Null(status.MonitorError);

        ProcessStopResult cancel = await supervisor.CancelAsync(runId, CancellationToken.None);
        Assert.True(cancel.TerminateRequested);
        Assert.False(cancel.ChildPending);
        await WaitGoneAsync(result.ChildPid.Value);
    }

    [Fact]
    public async Task Incomplete_registry_still_uses_owned_job_for_cancel()
    {
        using var supervisor = new ProcessSupervisor(_dbPath);
        RunId runId = RunId.New();
        ProcessStartResult result = await supervisor.StartAsync(NewRequest(runId, LongPing), CancellationToken.None);
        Assert.True(result.Started);
        Track(result.ChildPid!.Value);

        using (var connection = StorageDatabase.Open(_dbPath))
        {
            StorageDatabase.Execute(connection,
                "UPDATE process_registry SET child_pid = NULL WHERE run_id = $run_id;",
                null,
                ("$run_id", runId.ToString()));
        }

        ProcessStopResult cancel = await supervisor.CancelAsync(runId, CancellationToken.None);
        Assert.True(cancel.TerminateRequested);
        Assert.False(cancel.ChildPending);
        await WaitGoneAsync(result.ChildPid.Value);
        Assert.Null(GetRegistryRow(runId));
    }

    [Fact]
    public async Task Registry_read_failure_does_not_block_owned_job_status_or_cancel()
    {
        var registry = new SwitchableProcessRegistry(_dbPath);
        using var supervisor = new ProcessSupervisor(_dbPath, registry: registry);
        RunId runId = RunId.New();
        ProcessStartResult result = await supervisor.StartAsync(NewRequest(runId, LongPing), CancellationToken.None);
        Assert.True(result.Started);
        Track(result.ChildPid!.Value);

        registry.ThrowOnRead = true;
        ProcessStatus status = await supervisor.StatusAsync(runId, CancellationToken.None);
        Assert.Equal(ProcessLiveness.Alive, status.Liveness);
        Assert.True(status.ChildPending);
        Assert.Null(status.MonitorError);

        ProcessStopResult cancel = await supervisor.CancelAsync(runId, CancellationToken.None);
        Assert.True(cancel.TerminateRequested);
        Assert.False(cancel.ChildPending);
        await WaitGoneAsync(result.ChildPid.Value);
    }

    [Fact]
    public async Task Unverifiable_registry_probe_still_uses_owned_job_for_cancel()
    {
        var probe = new SwitchableProcessProbe();
        using var supervisor = new ProcessSupervisor(_dbPath, probe);
        RunId runId = RunId.New();
        ProcessStartResult result = await supervisor.StartAsync(NewRequest(runId, LongPing), CancellationToken.None);
        Assert.True(result.Started);
        Track(result.ChildPid!.Value);

        probe.ReturnUnknown = true;
        ProcessStopResult cancel = await supervisor.CancelAsync(runId, CancellationToken.None);

        Assert.True(cancel.TerminateRequested);
        Assert.False(cancel.ChildPending);
        await WaitGoneAsync(result.ChildPid.Value);
        Assert.Null(GetRegistryRow(runId));
    }

    [Fact]
    public async Task RecoverAsync_cleans_gone_registration_and_reports()
    {
        using var supervisor = new ProcessSupervisor(_dbPath);
        RunId runId = RunId.New();
        ProcessStartResult result = await supervisor.StartAsync(NewRequest(runId, LongPing), CancellationToken.None);
        Assert.True(result.Started);
        Track(result.ChildPid!.Value);

        // 外部杀死进程 → 模拟宿主崩溃后进程已随 Job 死亡。
        using (var p = Process.GetProcessById(result.ChildPid!.Value))
        {
            p.Kill(entireProcessTree: true);
            p.WaitForExit(3000);
        }

        await WaitGoneAsync(result.ChildPid!.Value);

        RecoveryReport report = await supervisor.RecoverAsync(CancellationToken.None);
        RecoveryReportItem item = Assert.Single(report.Items);
        Assert.Equal(runId, item.RunId);
        Assert.Equal(ProcessVerdict.Gone, item.Verdict);
        Assert.Equal(RecoveryAction.RemoveRegistry, item.Action);
        Assert.Null(GetRegistryRow(runId));
    }

    [Fact]
    public async Task Start_fails_internal_when_first_registry_write_fails()
    {
        // 破坏 db 文件:删掉数据库并建同名目录 → 登记写入必然失败 → Start 必须 internal 拒绝且不 spawn。
        SqliteConnection.ClearAllPools();
        foreach (string suffix in new[] { string.Empty, "-wal", "-shm" })
        {
            string path = _dbPath + suffix;
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }

        Directory.CreateDirectory(_dbPath);

        using var supervisor = new ProcessSupervisor(_dbPath);
        ProcessStartResult result = await supervisor.StartAsync(NewRequest(RunId.New(), LongPing), CancellationToken.None);
        Assert.False(result.Started);
        Assert.Equal(ErrorClass.Internal, result.ErrorClass);
        Assert.Equal("registry_write_failed", result.ErrorCode);
    }

    private async Task WaitGoneAsync(int pid)
    {
        for (int i = 0; i < 30; i++)
        {
            if (_probe.Probe(pid).Liveness == ProcessLiveness.Gone)
            {
                return;
            }

            await Task.Delay(100).ConfigureAwait(false);
        }

        throw new InvalidOperationException($"进程 {pid} 在等待期内未退出。");
    }

    /// <summary>只读辅助:返回登记行是否存在(不触碰 supervisor 内部)。</summary>
    private ProcessRegistryEntry? GetRegistryRow(RunId runId)
    {
        var registry = new SqliteProcessRegistry(_dbPath);
        return registry.Get(runId);
    }

    private sealed class SwitchableProcessProbe : IProcessProbe
    {
        private readonly NativeProcessProbe _inner = new();

        public bool ReturnUnknown { get; set; }

        public ProcessProbeResult Probe(int pid) => ReturnUnknown
            ? new ProcessProbeResult(ProcessLiveness.Unknown, null, null)
            : _inner.Probe(pid);

        public IReadOnlyList<ProcessSnapshotEntry> EnumerateAll() => _inner.EnumerateAll();
    }

    private sealed class SwitchableProcessRegistry : IProcessRegistry
    {
        private readonly SqliteProcessRegistry _inner;

        public SwitchableProcessRegistry(string databasePath)
        {
            _inner = new SqliteProcessRegistry(databasePath);
        }

        public bool ThrowOnRead { get; set; }

        public void InsertPlaceholder(RunId runId, int parentPid, string jobId, string commandSignature) =>
            _inner.InsertPlaceholder(runId, parentPid, jobId, commandSignature);

        public void Complete(RunId runId, int childPid, DateTimeOffset startedAt, string commandSignature) =>
            _inner.Complete(runId, childPid, startedAt, commandSignature);

        public ProcessRegistryEntry? Get(RunId runId) => ThrowOnRead
            ? throw new InvalidDataException("registry read failed")
            : _inner.Get(runId);

        public void Delete(RunId runId) => _inner.Delete(runId);

        public IReadOnlyList<ProcessRegistryEntry> EnumerateAll() => ThrowOnRead
            ? throw new InvalidDataException("registry read failed")
            : _inner.EnumerateAll();
    }
}
