using AiResume.Core;
using AiResume.Storage;
using AiResume.Worker.Products;
using Xunit;

namespace AiResume.Tests;

/// <summary>
/// S5-C 布防周期状态机测试:全路径/周期隔离/节奏/refire 防护/完成语义/持久化 round-trip。
/// 注入假时钟与临时 SQLite,不触碰生产状态。
/// </summary>
[Collection(SqliteCollection.Name)]
public sealed class CheckerCycleTests : IDisposable
{
    private readonly string _dbPath;
    private DateTimeOffset _now = new(2026, 8, 5, 0, 0, 0, TimeSpan.Zero);

    public CheckerCycleTests()
    {
        string dir = TestTemp.NewDir("s5c");
        Directory.CreateDirectory(dir);
        _dbPath = Path.Combine(dir, "shadow.db");
        StorageDatabase.Migrate(_dbPath);
    }

    public void Dispose()
    {
        try
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            Directory.Delete(Path.GetDirectoryName(_dbPath)!, recursive: true);
        }
        catch (Exception)
        {
            // 清理失败不掩盖断言结果。
        }
    }

    private CheckerCycle NewCycle() => new(new ProductStateStore(_dbPath), () => _now);

    private void Advance(TimeSpan span) => _now += span;

    private static ProductConfig Config(Action<ProductConfig>? mutate = null)
    {
        var cfg = ProductConfig.CreateDefault();
        cfg.Enabled = true;
        cfg.Armed = true;
        cfg.ArmCycleId = "cycle-1";
        mutate?.Invoke(cfg);
        return cfg;
    }

    private static ClaudeProbeResult Probe(string reason, DateTimeOffset? fiveHourReset = null, double? util = null) => new()
    {
        Ready = reason == "ok",
        Reason = reason,
        FiveHourResetUtc = fiveHourReset,
        FiveHourUtil = util,
    };

    // ---- 节奏 ----

    [Fact]
    public void ShouldProbe_immediate_when_never_probed()
    {
        var cycle = NewCycle();
        Assert.True(cycle.ShouldProbe(Config(), CheckerState.CreateDefault()));
    }

    [Fact]
    public void ShouldProbe_respects_usable_cadence_from_config()
    {
        var cycle = NewCycle();
        var state = CheckerState.CreateDefault();
        state.CycleId = "cycle-1";
        state.LastProbeUtc = _now;

        // probeIntervalMinutes=7:6 分钟前 → 不探;7 分钟前 → 探。
        Advance(TimeSpan.FromMinutes(6));
        Assert.False(cycle.ShouldProbe(Config(c => c.ProbeIntervalMinutes = 7), state));
        Advance(TimeSpan.FromMinutes(1));
        Assert.True(cycle.ShouldProbe(Config(c => c.ProbeIntervalMinutes = 7), state));
    }

    [Fact]
    public void ShouldProbe_falls_back_to_default_15_when_interval_below_2()
    {
        var cycle = NewCycle();
        var state = CheckerState.CreateDefault();
        state.CycleId = "cycle-1";
        state.LastProbeUtc = _now;

        // probeIntervalMinutes=1(低于 2)→ 回落默认 15;10 分钟前不探,16 分钟前探。
        Advance(TimeSpan.FromMinutes(10));
        Assert.False(cycle.ShouldProbe(Config(c => c.ProbeIntervalMinutes = 1), state));
        Advance(TimeSpan.FromMinutes(6));
        Assert.True(cycle.ShouldProbe(Config(c => c.ProbeIntervalMinutes = 1), state));
    }

    [Fact]
    public void ShouldProbe_tightens_to_4_minutes_while_limited()
    {
        var cycle = NewCycle();
        var state = CheckerState.CreateDefault();
        state.CycleId = "cycle-1";
        state.SawLimited = true;
        state.LastProbeUtc = _now;

        Advance(TimeSpan.FromMinutes(3));
        Assert.False(cycle.ShouldProbe(Config(), state));
        Advance(TimeSpan.FromMinutes(1));
        Assert.True(cycle.ShouldProbe(Config(), state));
    }

    // ---- 周期隔离 ----

    [Fact]
    public void Cycle_change_invalidates_operations()
    {
        var cycle = NewCycle();
        var config = Config();
        var state = CheckerState.CreateDefault();
        state.CycleId = "cycle-1";
        Assert.True(cycle.TestCycleActive(config, "cycle-1"));

        // 周期变化:config.armCycleId 改了 → 旧 state.cycleId 失效。
        config.ArmCycleId = "cycle-2";
        Assert.False(cycle.TestCycleActive(config, state.CycleId));

        // 失效周期上操作:不写状态(OnLimited 返回 false,state 不变)。
        state.SawLimited = false;
        Assert.False(cycle.OnLimited(config, state, Probe("limited")));
        Assert.False(state.SawLimited);

        // 解除布防:config.enabled=false → 失效。
        config.ArmCycleId = "cycle-1";
        config.Enabled = false;
        Assert.False(cycle.TestCycleActive(config, state.CycleId));
    }

    [Fact]
    public void Initialize_resets_cycle_fields_when_cycle_advanced()
    {
        var cycle = NewCycle();
        var config = Config();
        var state = CheckerState.CreateDefault();
        state.CycleId = "old-cycle";
        state.Phase = CheckerState.PhaseResuming;
        state.SawLimited = true;
        state.LimitedRefires = 3;
        state.ProjectStatus = new Dictionary<string, string> { ["C:\\Repo\\A"] = "success" };
        state.ActiveRunId = RunId.New().ToString();
        state.ActiveProjectPath = "C:\\Repo\\A";

        Assert.True(cycle.Initialize(config, state));

        Assert.Equal("cycle-1", state.CycleId);
        Assert.Equal(CheckerState.PhaseWaiting, state.Phase);
        Assert.False(state.SawLimited);
        Assert.Equal(0, state.LimitedRefires);
        Assert.NotNull(state.ProjectStatus);
        Assert.Empty(state.ProjectStatus);
        Assert.Equal(string.Empty, state.ActiveRunId);
        Assert.Equal(string.Empty, state.ActiveProjectPath);

        // 幂等:已对齐 → true 不重置。
        Assert.True(cycle.Initialize(config, state));
    }

    [Fact]
    public void Initialize_returns_false_when_config_disabled_or_no_cycle()
    {
        var cycle = NewCycle();
        Assert.False(cycle.Initialize(Config(c => c.Enabled = false), CheckerState.CreateDefault()));
        Assert.False(cycle.Initialize(Config(c => c.ArmCycleId = string.Empty), CheckerState.CreateDefault()));
    }

    // ---- 探测分支 ----

    [Fact]
    public void OnLimited_sets_waiting_and_clears_prior_project_results_on_first_limit()
    {
        var cycle = NewCycle();
        var config = Config();
        var state = CheckerState.CreateDefault();
        state.CycleId = "cycle-1";
        state.ProjectStatus = new Dictionary<string, string> { ["C:\\Repo\\A"] = "success" };
        state.LimitedRefires = 2;

        Assert.True(cycle.OnLimited(config, state, Probe("limited")));

        Assert.True(state.SawLimited);
        Assert.Equal(CheckerState.PhaseWaiting, state.Phase);
        Assert.Equal(0, state.LimitedRefires);
        Assert.Empty(state.ProjectStatus!);
    }

    [Fact]
    public void OnLimited_preserves_project_results_on_repeat_limit()
    {
        var cycle = NewCycle();
        var config = Config();
        var state = CheckerState.CreateDefault();
        state.CycleId = "cycle-1";
        state.SawLimited = true;
        state.LimitedRefires = 4;
        state.ProjectStatus = new Dictionary<string, string> { ["C:\\Repo\\A"] = "limited" };

        Assert.True(cycle.OnLimited(config, state, Probe("limited")));

        // 没有 reset 代次变化证据时必须保留累计值，避免 limited/ready 振荡绕过熔断。
        Assert.Single(state.ProjectStatus!);
        Assert.Equal(4, state.LimitedRefires);
    }

    [Fact]
    public void 同一周期Limited与误放行交替时仍会触发Refire熔断()
    {
        var cycle = NewCycle();
        var config = Config();
        var state = CheckerState.CreateDefault();
        state.CycleId = "cycle-1";
        state.SawLimited = true;
        state.Phase = CheckerState.PhaseWaiting;
        state.LimitedRefires = 4;
        state.ProjectStatus = new Dictionary<string, string> { ["C:\\Repo\\A"] = "limited" };

        Assert.True(cycle.OnLimited(config, state, Probe("limited")));
        Assert.Equal(
            ProjectOutcome.BackToWaiting,
            cycle.ApplyProjectResult(config, state, "C:\\Repo\\A", "limited"));
        Assert.True(cycle.OnLimited(config, state, Probe("limited")));
        Assert.Equal(
            ProjectOutcome.Blocked,
            cycle.ApplyProjectResult(config, state, "C:\\Repo\\A", "limited"));

        Assert.Equal(6, state.LimitedRefires);
        Assert.True(state.ReplayBlocked);
        Assert.Equal(CheckerState.PhaseBlocked, state.Phase);
    }

    [Fact]
    public void OnLimited_persists_exact_server_reset_without_clearing_on_low_utilization()
    {
        var cycle = NewCycle();
        var config = Config();
        var state = CheckerState.CreateDefault();
        state.CycleId = "cycle-1";
        var reset = new DateTimeOffset(2026, 8, 5, 5, 0, 0, TimeSpan.Zero);

        Assert.True(cycle.OnLimited(config, state, Probe("limited", reset, 0.87)));
        Assert.Equal(reset, state.RealFiveHourResetUtc);
        Assert.Equal(0.87, state.RealFiveHourUtil);
        Assert.Equal(_now, state.RealResetProbedUtc);

        // 后续探测未带服务端值:好值保留(只覆盖,不清零)。
        Advance(TimeSpan.FromMinutes(4));
        Assert.True(cycle.OnLimited(config, state, Probe("limited")));
        Assert.Equal(reset, state.RealFiveHourResetUtc);
        Assert.Equal(0.87, state.RealFiveHourUtil);
    }

    [Fact]
    public void OnReady_keeps_watching_when_limit_never_seen()
    {
        var cycle = NewCycle();
        var config = Config();
        var state = CheckerState.CreateDefault();
        state.CycleId = "cycle-1";

        ProbeDecision decision = cycle.OnReady(config, state, Probe("ok"));

        Assert.Equal(ProbeDecision.KeepWatching, decision);
        Assert.Equal(CheckerState.PhaseWaiting, state.Phase);
        Assert.False(state.SawLimited);
    }

    [Fact]
    public void OnReady_starts_resuming_after_limit_seen()
    {
        var cycle = NewCycle();
        var config = Config();
        var state = CheckerState.CreateDefault();
        state.CycleId = "cycle-1";
        state.SawLimited = true;
        state.ProjectStatus = new Dictionary<string, string>();

        ProbeDecision decision = cycle.OnReady(config, state, Probe("ok"));

        Assert.Equal(ProbeDecision.StartResuming, decision);
        Assert.Equal(CheckerState.PhaseResuming, state.Phase);
    }

    [Fact]
    public void OnNotReady_waits_fail_closed()
    {
        var cycle = NewCycle();
        var config = Config();
        var state = CheckerState.CreateDefault();
        state.CycleId = "cycle-1";
        state.SawLimited = true;

        Assert.True(cycle.OnNotReady(config, state, Probe("transient")));

        Assert.Equal(CheckerState.PhaseWaiting, state.Phase);
        // 未就绪绝不触发续跑(fail-closed)。
    }

    [Fact]
    public void OnReady_and_OnLimited_return_false_when_cycle_superseded()
    {
        var cycle = NewCycle();
        var config = Config();
        var state = CheckerState.CreateDefault();
        state.CycleId = "cycle-1";
        config.ArmCycleId = "cycle-2"; // 周期已变化。

        Assert.False(cycle.OnLimited(config, state, Probe("limited")));
        Assert.Equal(ProbeDecision.KeepWatching, cycle.OnReady(config, state, Probe("ok")));
        Assert.False(cycle.OnNotReady(config, state, Probe("transient")));
    }

    // ---- refire 防护 ----

    [Fact]
    public void ApplyProjectResult_updates_status_and_continues_on_success()
    {
        var cycle = NewCycle();
        var config = Config();
        var state = CheckerState.CreateDefault();
        state.CycleId = "cycle-1";
        state.SawLimited = true;
        state.Phase = CheckerState.PhaseResuming;

        ProjectOutcome outcome = cycle.ApplyProjectResult(config, state, "C:\\Repo\\A", "success");

        Assert.Equal(ProjectOutcome.Continue, outcome);
        Assert.Equal("success", state.ProjectStatus!["C:\\Repo\\A"]);
        Assert.Equal(CheckerState.PhaseResuming, state.Phase);
    }

    [Fact]
    public void Running项目与精确RunId在spawn前一次持久化且终态会清理()
    {
        var cycle = NewCycle();
        var config = Config();
        var state = CheckerState.CreateDefault();
        state.CycleId = "cycle-1";
        state.SawLimited = true;
        state.Phase = CheckerState.PhaseResuming;
        RunId runId = RunId.New();

        Assert.True(cycle.PrepareActiveRun(config, state, "C:\\Repo\\A", runId));
        Assert.Equal("running", state.ProjectStatus!["C:\\Repo\\A"]);
        Assert.Equal("C:\\Repo\\A", state.ActiveProjectPath);
        Assert.Equal(runId.ToString(), state.ActiveRunId);
        CheckerState persisted = new ProductStateStore(_dbPath).Load();
        Assert.Equal(runId.ToString(), persisted.ActiveRunId);
        Assert.Equal("C:\\Repo\\A", persisted.ActiveProjectPath);

        Assert.Equal(
            ProjectOutcome.Continue,
            cycle.ApplyProjectResult(config, state, "C:\\Repo\\A", "success"));
        Assert.Equal(string.Empty, state.ActiveRunId);
        Assert.Equal(string.Empty, state.ActiveProjectPath);
    }

    [Fact]
    public void Spawn前取消只撤销精确RunId对应的预登记()
    {
        var cycle = NewCycle();
        var config = Config();
        var state = CheckerState.CreateDefault();
        state.CycleId = "cycle-1";
        state.SawLimited = true;
        state.Phase = CheckerState.PhaseResuming;
        RunId runId = RunId.New();

        Assert.True(cycle.PrepareActiveRun(config, state, "C:\\Repo\\A", runId));
        Assert.True(cycle.RollbackPreparedRun(config, state, "C:\\Repo\\A", runId));

        Assert.Equal(CheckerState.PhaseWaiting, state.Phase);
        Assert.Empty(state.ActiveRunId);
        Assert.Empty(state.ActiveProjectPath);
        Assert.False(state.ProjectStatus!.ContainsKey("C:\\Repo\\A"));
        CheckerState persisted = new ProductStateStore(_dbPath).Load();
        Assert.Empty(persisted.ActiveRunId);
        Assert.False(persisted.ProjectStatus!.ContainsKey("C:\\Repo\\A"));
    }

    [Fact]
    public void Spawn前取消的RunId不匹配时保持失败关闭()
    {
        var cycle = NewCycle();
        var config = Config();
        var state = CheckerState.CreateDefault();
        state.CycleId = "cycle-1";
        state.SawLimited = true;
        state.Phase = CheckerState.PhaseResuming;
        RunId activeRunId = RunId.New();

        Assert.True(cycle.PrepareActiveRun(config, state, "C:\\Repo\\A", activeRunId));
        Assert.False(cycle.RollbackPreparedRun(config, state, "C:\\Repo\\A", RunId.New()));

        Assert.Equal(activeRunId.ToString(), state.ActiveRunId);
        Assert.Equal("C:\\Repo\\A", state.ActiveProjectPath);
        Assert.Equal("running", state.ProjectStatus!["C:\\Repo\\A"]);
        CheckerState persisted = new ProductStateStore(_dbPath).Load();
        Assert.Equal(activeRunId.ToString(), persisted.ActiveRunId);
        Assert.Equal("running", persisted.ProjectStatus!["C:\\Repo\\A"]);
    }

    [Fact]
    public void 终止待确认时保留精确RunId供后续核验()
    {
        var cycle = NewCycle();
        var config = Config();
        var state = CheckerState.CreateDefault();
        state.CycleId = "cycle-1";
        state.SawLimited = true;
        state.Phase = CheckerState.PhaseResuming;
        RunId runId = RunId.New();

        Assert.True(cycle.PrepareActiveRun(config, state, "C:\\Repo\\A", runId));
        Assert.Equal(
            ProjectOutcome.Blocked,
            cycle.ApplyProjectResult(config, state, "C:\\Repo\\A", "cancel-pending"));

        Assert.Equal("cancel-pending", state.ProjectStatus!["C:\\Repo\\A"]);
        Assert.Equal(CheckerState.PhaseBlocked, state.Phase);
        Assert.True(state.ReplayBlocked);
        Assert.Equal(runId.ToString(), state.ActiveRunId);
        Assert.Equal("C:\\Repo\\A", state.ActiveProjectPath);
    }

    [Fact]
    public void 安全中止本轮时清理活动Run并回到waiting()
    {
        var cycle = NewCycle();
        var config = Config();
        var state = CheckerState.CreateDefault();
        state.CycleId = "cycle-1";
        state.SawLimited = true;
        state.Phase = CheckerState.PhaseResuming;
        RunId runId = RunId.New();

        Assert.True(cycle.PrepareActiveRun(config, state, "C:\\Repo\\A", runId));

        Assert.Equal(
            ProjectOutcome.Blocked,
            cycle.ApplyProjectResult(config, state, "C:\\Repo\\A", "monitor-error", stopRound: true));
        Assert.Equal(CheckerState.PhaseBlocked, state.Phase);
        Assert.True(state.ReplayBlocked);
        Assert.Equal("monitor-error", state.ProjectStatus!["C:\\Repo\\A"]);
        Assert.Equal(string.Empty, state.ActiveRunId);
        Assert.Equal(string.Empty, state.ActiveProjectPath);
    }

    [Fact]
    public void ApplyProjectResult_returns_to_waiting_before_refire_cap()
    {
        var cycle = NewCycle();
        var config = Config();
        var state = CheckerState.CreateDefault();
        state.CycleId = "cycle-1";
        state.SawLimited = true;
        state.Phase = CheckerState.PhaseResuming;
        state.LimitedRefires = 4;

        ProjectOutcome outcome = cycle.ApplyProjectResult(config, state, "C:\\Repo\\A", "limited");

        Assert.Equal(ProjectOutcome.BackToWaiting, outcome);
        Assert.Equal(5, state.LimitedRefires);
        Assert.Equal(CheckerState.PhaseWaiting, state.Phase);
        Assert.True(state.SawLimited);
        Assert.Equal("limited", state.ProjectStatus!["C:\\Repo\\A"]);
        CheckerState persisted = new ProductStateStore(_dbPath).Load();
        Assert.Equal(5, persisted.LimitedRefires);
        Assert.Equal("limited", persisted.ProjectStatus!["C:\\Repo\\A"]);
    }

    [Fact]
    public void ApplyProjectResult_marks_error_after_six_refires()
    {
        var cycle = NewCycle();
        var config = Config();
        var state = CheckerState.CreateDefault();
        state.CycleId = "cycle-1";
        state.SawLimited = true;
        state.Phase = CheckerState.PhaseResuming;
        state.LimitedRefires = 5;

        ProjectOutcome outcome = cycle.ApplyProjectResult(config, state, "C:\\Repo\\A", "limited");

        Assert.Equal(ProjectOutcome.Blocked, outcome);
        Assert.Equal(6, state.LimitedRefires);
        Assert.Equal("error", state.ProjectStatus!["C:\\Repo\\A"]);
        Assert.Equal(CheckerState.PhaseBlocked, state.Phase);
        Assert.True(state.ReplayBlocked);
    }

    [Fact]
    public void ApplyProjectResult_superseded_does_not_write()
    {
        var cycle = NewCycle();
        var config = Config();
        var state = CheckerState.CreateDefault();
        state.CycleId = "cycle-1";
        config.ArmCycleId = "cycle-2";

        ProjectOutcome outcome = cycle.ApplyProjectResult(config, state, "C:\\Repo\\A", "success");

        Assert.Equal(ProjectOutcome.CycleSuperseded, outcome);
        Assert.Null(state.ProjectStatus);
    }

    // ---- 完成语义 ----

    [Fact]
    public void Complete_disarms_after_one_shot()
    {
        var cycle = NewCycle();
        var config = Config();
        var state = CheckerState.CreateDefault();
        state.CycleId = "cycle-1";
        state.SawLimited = true;

        CycleCompletionKind kind = cycle.Complete(config, state);

        Assert.Equal(CycleCompletionKind.Disarmed, kind);
        Assert.Equal(CheckerState.PhaseDone, state.Phase);
        Assert.False(state.SawLimited);
        Assert.Equal(0, state.LimitedRefires);
    }

    [Fact]
    public void Complete_keeps_continuous_mode()
    {
        var cycle = NewCycle();
        var config = Config(c => c.Continuous = true);
        var state = CheckerState.CreateDefault();
        state.CycleId = "cycle-1";

        Assert.Equal(CycleCompletionKind.Continuous, cycle.Complete(config, state));
        Assert.Equal(CheckerState.PhaseDone, state.Phase);
    }

    [Fact]
    public void Complete_superseded_when_cycle_changed()
    {
        var cycle = NewCycle();
        var config = Config();
        var state = CheckerState.CreateDefault();
        state.CycleId = "cycle-1";
        config.ArmCycleId = "cycle-2";

        Assert.Equal(CycleCompletionKind.Superseded, cycle.Complete(config, state));
        Assert.NotEqual(CheckerState.PhaseDone, state.Phase); // 失效不写收尾。
    }

    [Fact]
    public void 失败状态会阻断ready与limited且保留项目结果()
    {
        var cycle = NewCycle();
        var config = Config();
        var state = CheckerState.CreateDefault();
        state.CycleId = "cycle-1";
        state.SawLimited = true;
        state.Phase = CheckerState.PhaseWaiting;
        state.ProjectStatus = new Dictionary<string, string>
        {
            ["C:\\Repo\\A"] = "monitor-error",
        };

        Assert.Equal(ProbeDecision.KeepWatching, cycle.OnReady(config, state, Probe("ready")));
        Assert.True(state.ReplayBlocked);
        Assert.Equal(CheckerState.PhaseBlocked, state.Phase);
        Assert.Equal("monitor-error", state.ProjectStatus["C:\\Repo\\A"]);

        Assert.True(cycle.OnLimited(config, state, Probe("limited")));
        Assert.Equal(CheckerState.PhaseBlocked, state.Phase);
        Assert.Equal("monitor-error", state.ProjectStatus["C:\\Repo\\A"]);
    }

    [Fact]
    public void 升级前失败状态会锁存且新周期初始化后才解除()
    {
        var cycle = NewCycle();
        var config = Config();
        var state = CheckerState.CreateDefault();
        state.CycleId = "cycle-1";
        state.Phase = CheckerState.PhaseWaiting;
        state.ProjectStatus = new Dictionary<string, string>
        {
            ["C:\\Repo\\A"] = "exit-null",
        };

        Assert.True(cycle.LatchReplayBlock(config, state));
        Assert.True(state.ReplayBlocked);
        Assert.Equal(CheckerState.PhaseBlocked, state.Phase);
        Assert.False(cycle.ShouldProbe(config, state));

        config.ArmCycleId = "cycle-2";
        Assert.True(cycle.Initialize(config, state));
        Assert.False(state.ReplayBlocked);
        Assert.Equal(CheckerState.PhaseWaiting, state.Phase);
        Assert.Empty(state.ProjectStatus!);
    }

    [Fact]
    public void Complete遇到阻断状态时保持当前周期可见()
    {
        var cycle = NewCycle();
        var config = Config();
        var state = CheckerState.CreateDefault();
        state.CycleId = "cycle-1";
        state.ReplayBlocked = true;
        state.ProjectStatus = new Dictionary<string, string>
        {
            ["C:\\Repo\\A"] = "stopped",
        };

        Assert.Equal(CycleCompletionKind.Blocked, cycle.Complete(config, state));
        Assert.Equal(CheckerState.PhaseBlocked, state.Phase);
        Assert.True(state.ReplayBlocked);
        Assert.Equal("stopped", state.ProjectStatus["C:\\Repo\\A"]);
    }

    // ---- 持久化 round-trip ----

    [Fact]
    public void State_persists_round_trip_through_store()
    {
        var cycle = NewCycle();
        var config = Config();
        var state = CheckerState.CreateDefault();
        state.CycleId = "cycle-1";
        Assert.True(cycle.MarkProbeAttempt(config, state));
        Assert.True(cycle.OnLimited(config, state, Probe("limited",
            new DateTimeOffset(2026, 8, 5, 5, 0, 0, TimeSpan.Zero), 0.87)));
        Assert.True(cycle.ApplyProjectResult(config, state, "C:\\Repo\\A", "limited") is ProjectOutcome.BackToWaiting or ProjectOutcome.MarkedError);

        // 新 store 实例(模拟重启)读取完整状态。
        var fresh = new ProductStateStore(_dbPath).Load();

        Assert.Equal("cycle-1", fresh.CycleId);
        Assert.True(fresh.SawLimited);
        Assert.NotNull(fresh.LastProbeUtc);
        Assert.Equal(CheckerState.PhaseWaiting, fresh.Phase);
        Assert.Equal(new DateTimeOffset(2026, 8, 5, 5, 0, 0, TimeSpan.Zero), fresh.RealFiveHourResetUtc);
        Assert.Equal(0.87, fresh.RealFiveHourUtil);
        Assert.Equal("limited", fresh.ProjectStatus!["C:\\Repo\\A"]);
    }

    [Fact]
    public void Store_loads_default_when_table_missing_or_empty()
    {
        // 全新数据库(未迁移):Load 容错回默认。
        string dir = TestTemp.NewDir("s5c");
        Directory.CreateDirectory(dir);
        try
        {
            var store = new ProductStateStore(Path.Combine(dir, "empty.db"));
            CheckerState state = store.Load();
            Assert.Equal(CheckerState.PhaseIdle, state.Phase);
            Assert.Null(state.ProjectStatus);
        }
        finally
        {
            // Microsoft.Data.Sqlite 连接池可能持有文件句柄,先清池再删目录。
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Store_Update在事务内合并待终止字段且保留周期状态()
    {
        var store = new ProductStateStore(_dbPath);
        var state = CheckerState.CreateDefault();
        state.CycleId = "cycle-1";
        state.Phase = CheckerState.PhaseResuming;
        state.ProjectStatus = new Dictionary<string, string> { ["C:\\Repo\\A"] = "running" };
        store.Save(state);

        store.Update(latest =>
        {
            latest.PendingCancellationRunId = "12345678-1234-1234-1234-1234567890ab";
            latest.PendingCancellationProjectPath = "C:\\Repo\\A";
            latest.PendingCancellationCycleId = "cycle-1";
        });

        CheckerState saved = store.Load();
        Assert.Equal("cycle-1", saved.CycleId);
        Assert.Equal(CheckerState.PhaseResuming, saved.Phase);
        Assert.Equal("running", saved.ProjectStatus!["C:\\Repo\\A"]);
        Assert.Equal("12345678-1234-1234-1234-1234567890ab", saved.PendingCancellationRunId);
    }
}
