using AiResume.Core;
using AiResume.Core.Contracts;
using AiResume.Storage;
using AiResume.Worker.Supervision;
using Xunit;

namespace AiResume.Tests;

/// <summary>
/// S5-D 对账器测试:三方对账(非 terminal run ↔ registry ↔ 进程 liveness)、
/// runKey 规范形复验(D-011)、registry 完整性(孤儿/占位)、
/// PID 复用(Mismatched 不终止)与探测失败(Unverifiable fail-closed)的对账视角补强。
/// 注入假探针/内存 registry,不触碰真实进程与生产状态。
/// </summary>
[Collection(SqliteCollection.Name)]
public sealed class ReconcilerTests : IDisposable
{
    private readonly string _dbPath;
    private readonly string _dir;

    public ReconcilerTests()
    {
        _dir = TestTemp.NewDir("s5d-reconciler");
        Directory.CreateDirectory(_dir);
        _dbPath = Path.Combine(_dir, "shadow.db");
        StorageDatabase.Migrate(_dbPath);
    }

    public void Dispose()
    {
        try
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            Directory.Delete(_dir, recursive: true);
        }
        catch
        {
            // 临时目录残留可容忍。
        }
    }

    // ---- 工具 ----

    /// <summary>固定 GUID 便于断言(run_id 存储/解析均要求 GUID 规范形)。</summary>
    private const string R1 = "11111111-1111-1111-1111-111111111111";
    private const string RNone = "22222222-2222-2222-2222-222222222222";

    /// <summary>直接插入 run 行(测试自由控制 run_key/state,不依赖 StartRequest 契约路径)。</summary>
    private void InsertRun(string runId, string runKey, string taskKind, string state)
    {
        using var connection = StorageDatabase.Open(_dbPath);
        string now = DateTimeOffset.UtcNow.ToString("o");
        StorageDatabase.Execute(connection, """
            INSERT INTO runs (run_id, request_id, run_key, task_kind, profile_id, input_ref,
                fallback_policy, state, state_version, seq, queued_at, created_at, updated_at)
            VALUES ($run_id, $request_id, $run_key, $task_kind, 'probe', 't',
                'none', $state, 3, 3, $now, $now, $now);
            """, null,
            ("$run_id", runId),
            ("$request_id", Guid.NewGuid().ToString("D")),
            ("$run_key", runKey),
            ("$task_kind", taskKind),
            ("$state", state),
            ("$now", now));
    }

    /// <summary>规范形 runKey(等价 RunKey.Create 输出)。</summary>
    private static string CanonicalKey(string taskKind = "probe", string path = @"C:\repo\a", string openId = "ou_1") =>
        $"{taskKind}|{path.ToLowerInvariant().Replace('/', '\\').TrimEnd('\\')}|{openId}";

    private static ProcessRegistryEntry RegistryEntry(string runId, int? childPid,
        DateTimeOffset? startedAt = null, string? signature = null) => new(
        RunId.FromString(runId),
        Environment.ProcessId,
        childPid,
        "job-x",
        startedAt ?? DateTimeOffset.UtcNow,
        signature ?? ProcessSignature.Compute("cmd.exe"),
        DateTimeOffset.UtcNow);

    private Reconciler NewReconciler(IProcessProbe probe, IProcessRegistry registry) => new(_dbPath, probe, registry);

    /// <summary>固定返回 Alive + 指定启动时间/exe 的探针。</summary>
    private sealed class FixedProbe : IProcessProbe
    {
        private readonly ProcessLiveness _liveness;
        private readonly DateTimeOffset? _startedAt;
        private readonly string? _exePath;

        public FixedProbe(ProcessLiveness liveness, DateTimeOffset? startedAt = null, string? exePath = null)
        {
            _liveness = liveness;
            _startedAt = startedAt;
            _exePath = exePath;
        }

        public ProcessProbeResult Probe(int pid) => new(_liveness, _startedAt, _exePath);

        public IReadOnlyList<ProcessSnapshotEntry> EnumerateAll() => Array.Empty<ProcessSnapshotEntry>();
    }

    private sealed class MemoryRegistry : IProcessRegistry
    {
        private readonly Dictionary<string, ProcessRegistryEntry> _rows = new();

        public void Add(ProcessRegistryEntry entry) => _rows[entry.RunId.ToString()] = entry;

        public IReadOnlyList<ProcessRegistryEntry> EnumerateAll() => _rows.Values.ToList();

        public void InsertPlaceholder(RunId runId, int parentPid, string jobId, string commandSignature) =>
            throw new NotSupportedException("对账测试不需要写路径。");

        public void Complete(RunId runId, int childPid, DateTimeOffset startedAt, string commandSignature) =>
            throw new NotSupportedException("对账测试不需要写路径。");

        public ProcessRegistryEntry? Get(RunId runId) =>
            _rows.TryGetValue(runId.ToString(), out ProcessRegistryEntry? entry) ? entry : null;

        public void Delete(RunId runId) => throw new NotSupportedException("对账测试不需要写路径。");
    }

    // ---- 三方一致 ----

    [Fact]
    public void Consistent_when_active_run_matches_live_process()
    {
        InsertRun(R1, CanonicalKey(), "probe", "running");
        var registry = new MemoryRegistry();
        var startedAt = DateTimeOffset.UtcNow.AddMinutes(-1);
        registry.Add(RegistryEntry(R1, 4242, startedAt));

        ReconcileReport report = NewReconciler(
            new FixedProbe(ProcessLiveness.Alive, startedAt, "cmd.exe"), registry).Reconcile();

        Assert.Equal("consistent", report.Status);
        ReconcileRunItem item = Assert.Single(report.Runs);
        Assert.Equal(ReconcileVerdict.Matched, item.Verdict);
        Assert.True(item.ProcessAlive);
        Assert.True(item.RunKeyCanonical);
        Assert.Empty(report.Orphans);
    }

    // ---- 无登记 ----

    [Fact]
    public void Running_without_registry_is_inconsistent()
    {
        InsertRun(R1, CanonicalKey(), "probe", "running");
        var registry = new MemoryRegistry();

        ReconcileReport report = NewReconciler(new FixedProbe(ProcessLiveness.Alive), registry).Reconcile();

        Assert.Equal("inconsistent", report.Status);
        ReconcileRunItem item = Assert.Single(report.Runs);
        Assert.Equal(ReconcileVerdict.NotRegistered, item.Verdict);
        Assert.Contains("running 无登记", item.Note);
    }

    [Fact]
    public void Queued_without_registry_is_normal_pre_spawn()
    {
        InsertRun(R1, CanonicalKey(), "probe", "queued");
        var registry = new MemoryRegistry();

        ReconcileReport report = NewReconciler(new FixedProbe(ProcessLiveness.Alive), registry).Reconcile();

        Assert.Equal("consistent", report.Status);
        Assert.Equal(ReconcileVerdict.NotRegistered, Assert.Single(report.Runs).Verdict);
    }

    // ---- runKey 规范形复验(D-011)----

    [Theory]
    [InlineData("probe|c:\\repo\\a|ou_1", false)]            // 规范
    [InlineData("probe|C:\\Repo\\A|ou_1", true)]             // 路径大小写未归一
    [InlineData("probe|c:/repo/a|ou_1", true)]               // 分隔符未统一
    [InlineData("probe|c:\\repo\\a\\|ou_1", true)]           // 尾分隔符未去除
    [InlineData("probe|c:\\repo\\a", true)]                  // 段数不足
    [InlineData("query|c:\\repo\\a|ou_1", false)]            // 其他合法 kind
    [InlineData("unknown|c:\\repo\\a|ou_1", true)]           // 未知 kind
    [InlineData("probe||ou_1", true)]                        // 空路径段
    public void Run_key_canonical_form_detected(string runKey, bool expectInvalid)
    {
        InsertRun(R1, runKey, "probe", "running");
        var registry = new MemoryRegistry();

        ReconcileReport report = NewReconciler(new FixedProbe(ProcessLiveness.Alive), registry).Reconcile();

        ReconcileRunItem item = Assert.Single(report.Runs);
        Assert.Equal(!expectInvalid, item.RunKeyCanonical);
        if (expectInvalid)
        {
            Assert.Equal("inconsistent", report.Status);
            Assert.Equal(1, report.RunKeyInvalidCount);
            Assert.NotNull(item.RunKeyIssue);
        }
    }

    // ---- 占位与孤儿 ----

    [Fact]
    public void Placeholder_registry_marks_attention()
    {
        InsertRun(R1, CanonicalKey(), "probe", "running");
        var registry = new MemoryRegistry();
        registry.Add(RegistryEntry(R1, null)); // child_pid 未知。

        ReconcileReport report = NewReconciler(new FixedProbe(ProcessLiveness.Alive), registry).Reconcile();

        Assert.Equal("attention", report.Status);
        Assert.Equal(ReconcileVerdict.Placeholder, Assert.Single(report.Runs).Verdict);
        Assert.Equal(1, report.RegistryPlaceholderCount);
    }

    [Fact]
    public void Orphan_registry_reported_when_run_absent()
    {
        var registry = new MemoryRegistry();
        registry.Add(RegistryEntry(RNone, 4242));

        ReconcileReport report = NewReconciler(new FixedProbe(ProcessLiveness.Alive), registry).Reconcile();

        Assert.Equal("attention", report.Status);
        OrphanRegistryItem orphan = Assert.Single(report.Orphans);
        Assert.Equal(RNone, orphan.RunId);
        Assert.Contains("孤儿登记", orphan.Note);
        Assert.Empty(report.Runs);
    }

    [Fact]
    public void Orphan_registry_reported_when_run_terminal()
    {
        InsertRun(R1, CanonicalKey(), "probe", "succeeded");
        var registry = new MemoryRegistry();
        registry.Add(RegistryEntry(R1, 4242));

        ReconcileReport report = NewReconciler(new FixedProbe(ProcessLiveness.Alive), registry).Reconcile();

        Assert.Equal("attention", report.Status);
        OrphanRegistryItem orphan = Assert.Single(report.Orphans);
        Assert.Contains("terminal", orphan.Note);
    }

    // ---- PID 复用(Mismatched 不终止)与探测失败(fail-closed)----

    [Fact]
    public void Pid_reuse_mismatched_is_reported_not_actionable()
    {
        InsertRun(R1, CanonicalKey(), "probe", "running");
        var registry = new MemoryRegistry();
        var startedAt = DateTimeOffset.UtcNow.AddMinutes(-10); // 与进程真实启动时间明显不符。
        registry.Add(RegistryEntry(R1, 4242, startedAt));

        ReconcileReport report = NewReconciler(
            new FixedProbe(ProcessLiveness.Alive, DateTimeOffset.UtcNow, "cmd.exe"), registry).Reconcile();

        Assert.Equal("attention", report.Status);
        Assert.Equal(ReconcileVerdict.Mismatched, Assert.Single(report.Runs).Verdict);
        Assert.Contains("禁止终止", Assert.Single(report.Runs).Note);
    }

    [Fact]
    public void Probe_unknown_is_unverifiable_fail_closed()
    {
        InsertRun(R1, CanonicalKey(), "probe", "running");
        var registry = new MemoryRegistry();
        registry.Add(RegistryEntry(R1, 4242));

        ReconcileReport report = NewReconciler(
            new FixedProbe(ProcessLiveness.Unknown), registry).Reconcile();

        Assert.Equal("attention", report.Status);
        ReconcileRunItem item = Assert.Single(report.Runs);
        Assert.Equal(ReconcileVerdict.Unverifiable, item.Verdict);
        Assert.Contains("fail-closed", item.Note);
    }

    [Fact]
    public void Gone_process_is_attention_pending_recovery()
    {
        InsertRun(R1, CanonicalKey(), "probe", "running");
        var registry = new MemoryRegistry();
        registry.Add(RegistryEntry(R1, 4242));

        ReconcileReport report = NewReconciler(
            new FixedProbe(ProcessLiveness.Gone), registry).Reconcile();

        Assert.Equal("attention", report.Status);
        ReconcileRunItem item = Assert.Single(report.Runs);
        Assert.Equal(ReconcileVerdict.Gone, item.Verdict);
        Assert.False(item.ProcessAlive);
        Assert.Contains("恢复流程处置", item.Note);
    }

    // ---- JSON 输出形状 ----

    [Fact]
    public void ToJson_outputs_parseable_camel_case_shape()
    {
        InsertRun(R1, CanonicalKey(), "probe", "running");
        var registry = new MemoryRegistry();
        var startedAt = DateTimeOffset.UtcNow.AddMinutes(-1);
        registry.Add(RegistryEntry(R1, 4242, startedAt));

        ReconcileReport report = NewReconciler(
            new FixedProbe(ProcessLiveness.Alive, startedAt, "cmd.exe"), registry).Reconcile();

        using var doc = System.Text.Json.JsonDocument.Parse(report.ToJson());
        System.Text.Json.JsonElement root = doc.RootElement;
        Assert.Equal("consistent", root.GetProperty("status").GetString());
        Assert.Equal(1, root.GetProperty("activeRunCount").GetInt32());
        Assert.Equal(0, root.GetProperty("orphanRegistryCount").GetInt32());
        System.Text.Json.JsonElement first = root.GetProperty("runs")[0];
        Assert.Equal(R1, first.GetProperty("runId").GetString());
        Assert.Equal("Matched", first.GetProperty("verdict").GetString());
        Assert.True(first.GetProperty("runKeyCanonical").GetBoolean());
        Assert.True(root.GetProperty("generatedAt").GetString() is not null);
        Assert.True(root.GetProperty("databasePath").GetString() is not null);
    }
}
