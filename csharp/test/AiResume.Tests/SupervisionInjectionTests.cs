using AiResume.Core;
using AiResume.Core.Contracts;
using AiResume.Worker.Supervision;
using Xunit;

namespace AiResume.Tests;

/// <summary>
/// S2-D ProcessSupervisor 注入测试:用 FakeProbe/FakeRegistry 覆盖
/// "查询失败 → unverifiable → fail-closed 保留"与"首次登记失败 → internal 拒绝"路径
/// (真进程无法稳定构造这两条路径)。
/// </summary>
public sealed class SupervisionInjectionTests : IDisposable
{
    private readonly string _dir;

    public SupervisionInjectionTests()
    {
        _dir = TestTemp.NewDir("airesume-supervision-inject");
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch
        {
            // 测试临时目录残留可容忍。
        }
    }

    [Fact]
    public async Task Cancel_unverifiable_keeps_registry_and_does_not_terminate()
    {
        RunId runId = RunId.New();
        var registry = new MemoryRegistry();
        registry.Add(new ProcessRegistryEntry(
            runId, Environment.ProcessId, 12345, "job-x", DateTimeOffset.UtcNow,
            ProcessSignature.Compute("cmd.exe"), DateTimeOffset.UtcNow));

        using var supervisor = new ProcessSupervisor(_dir, new FixedProbe(ProcessLiveness.Unknown), registry);

        ProcessStopResult cancel = await supervisor.CancelAsync(runId, CancellationToken.None);
        Assert.False(cancel.TerminateRequested, "unverifiable 禁止终止。");
        Assert.True(cancel.ChildPending, "不可核验时保守视为进程可能仍存在。");
        Assert.True(registry.Contains(runId), "unverifiable 必须保留登记(fail-closed)。");
    }

    [Fact]
    public async Task Status_unverifiable_reports_unknown_without_mutation()
    {
        RunId runId = RunId.New();
        var registry = new MemoryRegistry();
        registry.Add(new ProcessRegistryEntry(
            runId, Environment.ProcessId, 12345, "job-x", DateTimeOffset.UtcNow,
            ProcessSignature.Compute("cmd.exe"), DateTimeOffset.UtcNow));

        using var supervisor = new ProcessSupervisor(_dir, new FixedProbe(ProcessLiveness.Unknown), registry);

        ProcessStatus status = await supervisor.StatusAsync(runId, CancellationToken.None);
        Assert.Equal(ProcessLiveness.Unknown, status.Liveness);
        Assert.True(status.ChildPending);
        Assert.True(registry.Contains(runId), "Status 不得清理登记。");
    }

    [Fact]
    public async Task RecoverAsync_keeps_unverifiable_fail_closed_and_reports()
    {
        RunId runId = RunId.New();
        var registry = new MemoryRegistry();
        registry.Add(new ProcessRegistryEntry(
            runId, Environment.ProcessId, 12345, "job-x", DateTimeOffset.UtcNow,
            ProcessSignature.Compute("cmd.exe"), DateTimeOffset.UtcNow));

        using var supervisor = new ProcessSupervisor(_dir, new FixedProbe(ProcessLiveness.Unknown), registry);

        RecoveryReport report = await supervisor.RecoverAsync(CancellationToken.None);
        RecoveryReportItem item = Assert.Single(report.Items);
        Assert.Equal(ProcessVerdict.Unverifiable, item.Verdict);
        Assert.Equal(RecoveryAction.KeepFailClosed, item.Action);
        Assert.True(registry.Contains(runId), "unverifiable 恢复时必须保留登记。");
    }

    [Fact]
    public async Task Start_rejects_internal_when_placeholder_write_fails()
    {
        var registry = new MemoryRegistry(failInsert: true);
        using var supervisor = new ProcessSupervisor(_dir, new FixedProbe(ProcessLiveness.Alive), registry);

        ProcessStartResult result = await supervisor.StartAsync(new ProcessStartRequest
        {
            RunId = RunId.New(),
            FileName = "cmd.exe",
            Arguments = "/c ping -n 2 127.0.0.1 > NUL",
        }, CancellationToken.None);

        Assert.False(result.Started);
        Assert.Equal(ErrorClass.Internal, result.ErrorClass);
        Assert.Equal("registry_write_failed", result.ErrorCode);
        Assert.True(registry.DeleteCalls == 0, "登记失败后不应有删除调用(占位从未写入)。");
    }

    /// <summary>固定返回指定 Liveness 的探针。</summary>
    private sealed class FixedProbe : IProcessProbe
    {
        private readonly ProcessLiveness _liveness;

        public FixedProbe(ProcessLiveness liveness)
        {
            _liveness = liveness;
        }

        public ProcessProbeResult Probe(int pid) => new(_liveness, null, null);

        public IReadOnlyList<ProcessSnapshotEntry> EnumerateAll() => Array.Empty<ProcessSnapshotEntry>();
    }

    /// <summary>内存 registry:可控抛错、可断言调用。</summary>
    private sealed class MemoryRegistry : IProcessRegistry
    {
        private readonly bool _failInsert;
        private readonly Dictionary<string, ProcessRegistryEntry> _rows = new();

        public int DeleteCalls { get; private set; }

        public MemoryRegistry(bool failInsert = false)
        {
            _failInsert = failInsert;
        }

        public void Add(ProcessRegistryEntry entry)
        {
            _rows[entry.RunId.ToString()] = entry;
        }

        public bool Contains(RunId runId) => _rows.ContainsKey(runId.ToString());

        public void InsertPlaceholder(RunId runId, int parentPid, string jobId, string commandSignature)
        {
            if (_failInsert)
            {
                throw new InvalidOperationException("fake registry write failure");
            }

            _rows[runId.ToString()] = new ProcessRegistryEntry(
                runId, parentPid, null, jobId, DateTimeOffset.UtcNow, commandSignature, DateTimeOffset.UtcNow);
        }

        public void Complete(RunId runId, int childPid, DateTimeOffset startedAt, string commandSignature)
        {
            _rows[runId.ToString()] = new ProcessRegistryEntry(
                runId, Environment.ProcessId, childPid, "job-x", startedAt, commandSignature, DateTimeOffset.UtcNow);
        }

        public ProcessRegistryEntry? Get(RunId runId)
        {
            return _rows.TryGetValue(runId.ToString(), out ProcessRegistryEntry? entry) ? entry : null;
        }

        public void Delete(RunId runId)
        {
            DeleteCalls++;
            _rows.Remove(runId.ToString());
        }

        public IReadOnlyList<ProcessRegistryEntry> EnumerateAll() => _rows.Values.ToList();
    }
}
