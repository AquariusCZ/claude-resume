using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AiResume.Core;
using AiResume.Storage;
using AiResume.Worker.Probes;
using AiResume.Worker.Products;
using AiResume.Worker.Quota;
using AiResume.Worker.Resume;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AiResume.Tests;

/// <summary>
/// ResumeEngine 单拍驱动测试。用假探测器/假运行器 + 固定可推进时钟,
/// 手动驱动 RunOnceAsync 验证 §5.2 的 10 条行为。不启动真实进程、不触碰真实 shadow 根。
/// </summary>
[Collection(SqliteCollection.Name)]
public sealed class ResumeEngineTests : IDisposable
{
    private readonly string _shadowRoot;
    private readonly string _stateDbPath;
    private readonly ProductConfigStore _configStore;
    private readonly ProductStateStore _stateStore;
    private readonly FakeProbe _probe;
    private readonly FakeRunner _runner;
    private readonly FakeProcessSupervisor _supervisor;
    private readonly CheckerCycle _cycle;
    private readonly ResumeEngine _engine;
    private readonly DateTimeOffset _now;
    private DateTimeOffset _clock;
    private bool? _activeRunEvidence = false;

    public ResumeEngineTests()
    {
        // 每个测试独立临时目录,避免相互污染。
        _shadowRoot = TestTemp.NewDir("airesume-tests");
        Directory.CreateDirectory(_shadowRoot);
        // ProductStateStore 落 SQLite 而非 JSON,且**必须先迁移建表**——
        // 不建表时 Save 会抛 no such table: product_state(隐含前置条件)。
        _stateDbPath = Path.Combine(_shadowRoot, "shadow.db");
        StorageDatabase.Migrate(_stateDbPath);

        _configStore = new ProductConfigStore(_shadowRoot);
        _stateStore = new ProductStateStore(_stateDbPath);

        _now = new DateTimeOffset(2026, 8, 6, 12, 0, 0, TimeSpan.Zero);
        _clock = _now;
        _cycle = new CheckerCycle(_stateStore, () => _clock);

        _probe = new FakeProbe();
        _runner = new FakeRunner();
        _supervisor = new FakeProcessSupervisor();
        _engine = CreateEngine(_probe);
    }

    private ResumeEngine CreateEngine(IClaudeUsageProbe probe) => new(
            _configStore,
            _stateStore,
            _cycle,
            probe,
            _runner,
            _supervisor,
            NullLogger<ResumeEngine>.Instance,
            TimeSpan.FromSeconds(30),
            _ => _activeRunEvidence);

    public void Dispose()
    {
        _engine.Dispose();
        try
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (Directory.Exists(_shadowRoot))
            {
                Directory.Delete(_shadowRoot, recursive: true);
            }
        }
        catch
        {
            // 清理失败忽略,不影响测试结果。
        }
    }

    /// <summary>构造已布防配置:Enabled=true、Armed=true、ArmCycleId 非空。</summary>
    private static ProductConfig ArmedConfig(string cycleId = "cycle-1", bool continuous = false)
    {
        var config = ProductConfig.CreateDefault();
        config.Enabled = true;
        config.Armed = true;
        config.ArmCycleId = cycleId;
        config.Continuous = continuous;
        config.Selected = new List<ProjectRef>
        {
            new() { Name = "proj-a", Path = @"C:\proj\a" },
            new() { Name = "proj-b", Path = @"C:\proj\b" },
            new() { Name = "proj-c", Path = @"C:\proj\c" },
        };
        return config;
    }

    private static UsageSnapshot QuotaSnapshot(
        DateTimeOffset capturedAt,
        bool allowed,
        params UsageWindow[] windows)
    {
        bool limited = windows.Any(window => window.UsedPercent is >= 100);
        return new UsageSnapshot(
            "claudecode",
            capturedAt,
            new[] { new UsageBucket("Usage", allowed, limited, windows) },
            null);
    }

    private static UsageWindow QuotaWindow(
        string name,
        int usedPercent,
        DateTimeOffset reset) => new(
        name,
        usedPercent >= 100 ? "blocked" : "allowed",
        name.Equals("five_hour", StringComparison.OrdinalIgnoreCase)
            ? UsageWindow.FiveHourSeconds
            : UsageWindow.SevenDaySeconds,
        reset.ToUnixTimeSeconds(),
        null,
        usedPercent,
        Identity: name.StartsWith("weekly_scoped", StringComparison.OrdinalIgnoreCase)
            ? "weekly_scoped:test"
            : null);

    /// <summary>把 state 的 LastProbeUtc 设为"刚刚",使 ShouldProbe 返回 false。</summary>
    private void SetLastProbeJustNow(CheckerState state)
    {
        state.LastProbeUtc = _clock;
        _stateStore.Save(state);
    }

    /// <summary>推进时钟使 ShouldProbe 必然返回 true(超过 15 分钟间隔)。</summary>
    private void AdvancePastProbeInterval()
    {
        _clock = _clock.AddMinutes(16);
    }

    [Fact]
    public async Task 未布防_不探测()
    {
        var config = ArmedConfig();
        config.Armed = false;
        _configStore.Save(config);
        _stateStore.Save(CheckerState.CreateDefault());

        await _engine.RunOnceAsync(CancellationToken.None);

        Assert.Equal(0, _probe.CallCount);
        Assert.Equal(0, _runner.CallCount);
    }

    [Fact]
    public async Task 布防但刚探测过_不探测()
    {
        var config = ArmedConfig();
        _configStore.Save(config);
        var state = CheckerState.CreateDefault();
        state.CycleId = config.ArmCycleId;
        SetLastProbeJustNow(state);

        await _engine.RunOnceAsync(CancellationToken.None);

        Assert.Equal(0, _probe.CallCount);
        Assert.Equal(0, _runner.CallCount);
    }

    [Fact]
    public async Task 探测limited_进入等待且不续跑()
    {
        var config = ArmedConfig();
        _configStore.Save(config);
        var state = CheckerState.CreateDefault();
        state.CycleId = config.ArmCycleId;
        _stateStore.Save(state);

        _probe.Results.Enqueue(new ClaudeProbeResult { Ready = false, Reason = "limited" });

        await _engine.RunOnceAsync(CancellationToken.None);

        var saved = _stateStore.Load();
        Assert.True(saved.SawLimited);
        Assert.Equal(CheckerState.PhaseWaiting, saved.Phase);
        Assert.Equal(0, _runner.CallCount);
    }

    [Fact]
    public async Task 未观测到限流时探测ready_保持布防不续跑()
    {
        var config = ArmedConfig();
        _configStore.Save(config);
        var state = CheckerState.CreateDefault();
        state.CycleId = config.ArmCycleId;
        _stateStore.Save(state);

        _probe.Results.Enqueue(new ClaudeProbeResult { Ready = true, Reason = "ok" });

        await _engine.RunOnceAsync(CancellationToken.None);

        var saved = _stateStore.Load();
        Assert.False(saved.SawLimited);
        Assert.Equal(CheckerState.PhaseWaiting, saved.Phase);
        Assert.Equal(0, _runner.CallCount);
        // 布防保持。
        var freshConfig = _configStore.Load();
        Assert.True(freshConfig.Armed);
        Assert.Equal(config.ArmCycleId, freshConfig.ArmCycleId);
    }

    [Fact]
    public async Task 观测到限流后再探测ready_按顺序触发续跑()
    {
        var config = ArmedConfig();
        _configStore.Save(config);
        var state = CheckerState.CreateDefault();
        state.CycleId = config.ArmCycleId;
        state.SawLimited = true;
        state.Phase = CheckerState.PhaseWaiting;
        _stateStore.Save(state);

        _probe.Results.Enqueue(new ClaudeProbeResult { Ready = true, Reason = "ok" });
        _runner.Results["success"] = new ResumeRunResult { Status = "success" };

        await _engine.RunOnceAsync(CancellationToken.None);

        Assert.Equal(3, _runner.CallCount);
        Assert.Equal(
            config.Selected.Select(p => p.Path).ToList(),
            _runner.CalledPaths);
    }

    [Fact]
    public async Task 完整额度门禁发现Fable满额时不会调用Runner()
    {
        ProductConfig config = ArmedConfig();
        config.ResumeModel = "fable";
        _configStore.Save(config);
        var state = CheckerState.CreateDefault();
        state.CycleId = config.ArmCycleId;
        state.SawLimited = true;
        state.Phase = CheckerState.PhaseWaiting;
        _stateStore.Save(state);
        var quota = new QuotaService(
            probe: _ => Task.FromResult(new ClaudeProbeResult { Reason = "unexpected" }),
            clock: () => _clock,
            oauthProbe: _ => Task.FromResult(new OAuthUsageResult(
                true,
                QuotaSnapshot(_clock, allowed: false,
                    QuotaWindow("five_hour", 95, _clock.AddHours(5)),
                    QuotaWindow("seven_day", 73, _clock.AddDays(4)),
                    QuotaWindow("weekly_scoped:Fable", 100, _clock.AddDays(4))),
                null,
                "account-a")));
        using ResumeEngine engine = CreateEngine(new QuotaResumeProbe(quota));

        await engine.RunOnceAsync(CancellationToken.None);

        Assert.Equal(0, _runner.CallCount);
        CheckerState saved = _stateStore.Load();
        Assert.True(saved.SawLimited);
        Assert.Equal(CheckerState.PhaseWaiting, saved.Phase);
    }

    [Fact]
    public async Task 未配置续跑模型时即使Fable实时可用也不会调用Runner()
    {
        ProductConfig config = ArmedConfig();
        config.ResumeModel = string.Empty;
        _configStore.Save(config);
        CheckerState state = CheckerState.CreateDefault();
        state.CycleId = config.ArmCycleId;
        state.SawLimited = true;
        state.Phase = CheckerState.PhaseWaiting;
        _stateStore.Save(state);
        var quota = new QuotaService(
            probe: _ => Task.FromResult(new ClaudeProbeResult { Reason = "unexpected" }),
            clock: () => _clock,
            oauthProbe: _ => Task.FromResult(new OAuthUsageResult(
                true,
                QuotaSnapshot(_clock, allowed: true,
                    QuotaWindow("five_hour", 25, _clock.AddHours(4)),
                    QuotaWindow("seven_day", 79, _clock.AddDays(3)),
                    QuotaWindow("weekly_scoped:Fable", 20, _clock.AddDays(3))),
                null,
                "account-a")));
        using ResumeEngine engine = CreateEngine(new QuotaResumeProbe(quota));

        await engine.RunOnceAsync(CancellationToken.None);

        Assert.Equal(0, _runner.CallCount);
        Assert.Equal(CheckerState.PhaseWaiting, _stateStore.Load().Phase);
    }

    [Fact]
    public async Task 额度门禁与Runner使用同一个显式续跑模型()
    {
        ProductConfig config = ArmedConfig();
        config.ProbeModel = "haiku";
        config.ResumeModel = "fable";
        config.Selected = new List<ProjectRef> { config.Selected[0] };
        _configStore.Save(config);
        var state = CheckerState.CreateDefault();
        state.CycleId = config.ArmCycleId;
        state.SawLimited = true;
        state.Phase = CheckerState.PhaseWaiting;
        _stateStore.Save(state);
        _probe.Results.Enqueue(new ClaudeProbeResult { Ready = true, Reason = "ok" });

        await _engine.RunOnceAsync(CancellationToken.None);

        Assert.Equal(new[] { "fable" }, _probe.Models);
        Assert.Equal(new[] { "fable" }, _runner.ResumeModels);
    }

    [Fact]
    public async Task 初次额度探测期间模型变化_旧证据作废并针对新模型重探()
    {
        ProductConfig config = ArmedConfig();
        config.ResumeModel = "fable";
        config.Selected = new List<ProjectRef> { config.Selected[0] };
        _configStore.Save(config);
        CheckerState state = CheckerState.CreateDefault();
        state.CycleId = config.ArmCycleId;
        state.SawLimited = true;
        state.Phase = CheckerState.PhaseWaiting;
        _stateStore.Save(state);
        _probe.Results.Enqueue(new ClaudeProbeResult { Ready = true, Reason = "ok" });
        _probe.Results.Enqueue(new ClaudeProbeResult { Ready = true, Reason = "ok" });
        _probe.AfterProbe = (_, call) =>
        {
            if (call == 1)
            {
                _configStore.Update(latest => latest.ResumeModel = "opus");
            }
        };

        await _engine.RunOnceAsync(CancellationToken.None);

        Assert.Equal(new[] { "fable", "opus" }, _probe.Models);
        Assert.Equal(new[] { "opus" }, _runner.ResumeModels);
        Assert.Equal(1, _runner.CallCount);
    }

    [Fact]
    public async Task 后续项目额度复核期间模型变化_旧证据作废且不启动错模型()
    {
        ProductConfig config = ArmedConfig();
        config.ResumeModel = "fable";
        config.Selected = config.Selected.Take(2).ToList();
        _configStore.Save(config);
        CheckerState state = CheckerState.CreateDefault();
        state.CycleId = config.ArmCycleId;
        state.SawLimited = true;
        state.Phase = CheckerState.PhaseWaiting;
        _stateStore.Save(state);
        _probe.Results.Enqueue(new ClaudeProbeResult { Ready = true, Reason = "ok" });
        _probe.Results.Enqueue(new ClaudeProbeResult { Ready = true, Reason = "ok" });
        _probe.Results.Enqueue(new ClaudeProbeResult { Ready = true, Reason = "ok" });
        _probe.AfterProbe = (_, call) =>
        {
            if (call == 2)
            {
                _configStore.Update(latest => latest.ResumeModel = "opus");
            }
        };

        await _engine.RunOnceAsync(CancellationToken.None);

        Assert.Equal(new[] { "fable", "fable", "opus" }, _probe.Models);
        Assert.Equal(new[] { "fable", "opus" }, _runner.ResumeModels);
        Assert.Equal(2, _runner.CallCount);
    }

    [Fact]
    public async Task 最终启动门禁前模型变化_不spawn旧授权并重新取证()
    {
        ProductConfig config = ArmedConfig();
        config.ResumeModel = "fable";
        config.Selected = new List<ProjectRef> { config.Selected[0] };
        _configStore.Save(config);
        CheckerState state = CheckerState.CreateDefault();
        state.CycleId = config.ArmCycleId;
        state.SawLimited = true;
        state.Phase = CheckerState.PhaseWaiting;
        _stateStore.Save(state);
        _probe.Results.Enqueue(new ClaudeProbeResult { Ready = true, Reason = "ok" });
        _probe.Results.Enqueue(new ClaudeProbeResult { Ready = true, Reason = "ok" });
        bool changed = false;
        _runner.BeforeStart = _ =>
        {
            if (!changed)
            {
                changed = true;
                _configStore.Update(latest => latest.ResumeModel = "opus");
            }
        };

        await _engine.RunOnceAsync(CancellationToken.None);

        Assert.True(changed);
        Assert.Equal(new[] { "fable", "opus" }, _probe.Models);
        Assert.Equal(new[] { "opus" }, _runner.ResumeModels);
        Assert.Equal(1, _runner.CallCount);
        Assert.False(_stateStore.Load().ReplayBlocked);
    }

    [Fact]
    public async Task 最终启动门禁前解除布防_不spawn也不把旧周期写成失败()
    {
        ProductConfig config = ArmedConfig();
        config.ResumeModel = "fable";
        config.Selected = new List<ProjectRef> { config.Selected[0] };
        _configStore.Save(config);
        CheckerState state = CheckerState.CreateDefault();
        state.CycleId = config.ArmCycleId;
        state.SawLimited = true;
        state.Phase = CheckerState.PhaseWaiting;
        _stateStore.Save(state);
        _probe.Results.Enqueue(new ClaudeProbeResult { Ready = true, Reason = "ok" });
        _runner.BeforeStart = _ => _configStore.Update(latest =>
        {
            latest.Armed = false;
            latest.ArmCycleId = string.Empty;
        });

        await _engine.RunOnceAsync(CancellationToken.None);

        Assert.Equal(0, _runner.CallCount);
        Assert.False(_configStore.Load().Armed);
        Assert.False(_stateStore.Load().ProjectStatus?.ContainsKey(config.Selected[0].Path) == true);
    }

    [Fact]
    public async Task Worker在spawn前取消_撤销精确预登记且不锁死周期()
    {
        ProductConfig config = ArmedConfig();
        config.ResumeModel = "fable";
        config.Selected = new List<ProjectRef> { config.Selected[0] };
        _configStore.Save(config);
        CheckerState state = CheckerState.CreateDefault();
        state.CycleId = config.ArmCycleId;
        state.SawLimited = true;
        state.Phase = CheckerState.PhaseWaiting;
        _stateStore.Save(state);
        _probe.Results.Enqueue(new ClaudeProbeResult { Ready = true, Reason = "ok" });
        _runner.CancelBeforeSpawn = true;

        await _engine.RunOnceAsync(CancellationToken.None);

        ProductConfig persistedConfig = _configStore.Load();
        CheckerState persistedState = _stateStore.Load();
        Assert.True(persistedConfig.Armed);
        Assert.Equal(CheckerState.PhaseWaiting, persistedState.Phase);
        Assert.True(persistedState.SawLimited);
        Assert.False(persistedState.ReplayBlocked);
        Assert.Empty(persistedState.ActiveRunId);
        Assert.Empty(persistedState.ActiveProjectPath);
        Assert.False(persistedState.ProjectStatus?.ContainsKey(config.Selected[0].Path) == true);
        Assert.Equal(0, _runner.CallCount);
    }

    [Fact]
    public async Task OAuth失败时Haiku就绪也不会调用Runner()
    {
        ProductConfig config = ArmedConfig();
        config.ResumeModel = "fable";
        _configStore.Save(config);
        var state = CheckerState.CreateDefault();
        state.CycleId = config.ArmCycleId;
        state.SawLimited = true;
        state.Phase = CheckerState.PhaseWaiting;
        _stateStore.Save(state);
        var quota = new QuotaService(
            probe: _ => Task.FromResult(new ClaudeProbeResult
            {
                Ready = true,
                Reason = "ok",
                FiveHourResetUtc = _clock.AddHours(5),
                FiveHourUtil = 0.25,
                SevenDayResetUtc = _clock.AddDays(4),
                SevenDayUtil = 0.73,
            }),
            clock: () => _clock,
            oauthProbe: _ => Task.FromResult(
                new OAuthUsageResult(false, null, "failed_local", "account-a")));
        using ResumeEngine engine = CreateEngine(new QuotaResumeProbe(quota));

        await engine.RunOnceAsync(CancellationToken.None);

        Assert.Equal(0, _runner.CallCount);
        Assert.Equal(CheckerState.PhaseWaiting, _stateStore.Load().Phase);
    }

    [Fact]
    public async Task Fable新重置代次实时可用后只启动一次续跑()
    {
        ProductConfig config = ArmedConfig();
        config.ResumeModel = "fable";
        config.Selected = new List<ProjectRef> { config.Selected[0] };
        _configStore.Save(config);
        var state = CheckerState.CreateDefault();
        state.CycleId = config.ArmCycleId;
        _stateStore.Save(state);
        int oauthCalls = 0;
        DateTimeOffset firstFiveHourReset = _clock.AddMinutes(3);
        DateTimeOffset nextFiveHourReset = _clock.AddHours(6);
        DateTimeOffset firstFableReset = _clock.AddDays(4);
        DateTimeOffset nextFableReset = _clock.AddDays(7);
        var quota = new QuotaService(
            probe: _ => Task.FromResult(new ClaudeProbeResult { Reason = "unexpected" }),
            clock: () => _clock,
            oauthProbe: _ =>
            {
                oauthCalls++;
                bool fiveHourBlocked = oauthCalls == 1;
                bool fableBlocked = oauthCalls <= 2;
                return Task.FromResult(new OAuthUsageResult(
                    true,
                    QuotaSnapshot(_clock, allowed: !fableBlocked,
                        QuotaWindow("five_hour", fiveHourBlocked ? 100 : oauthCalls == 2 ? 0 : 5,
                            fiveHourBlocked ? firstFiveHourReset : nextFiveHourReset),
                        QuotaWindow("seven_day", 73, _clock.AddDays(4)),
                        QuotaWindow("weekly_scoped:Fable", fableBlocked ? 100 : 5,
                            fableBlocked ? firstFableReset : nextFableReset)),
                    null,
                    "account-a"));
            });
        using ResumeEngine engine = CreateEngine(new QuotaResumeProbe(quota));

        await engine.RunOnceAsync(CancellationToken.None);
        Assert.Equal(0, _runner.CallCount);

        _clock = _clock.AddMinutes(4);
        await engine.RunOnceAsync(CancellationToken.None);
        Assert.Equal(0, _runner.CallCount);
        Assert.Equal(2, oauthCalls);

        _clock = _clock.AddMinutes(4);
        await engine.RunOnceAsync(CancellationToken.None);

        Assert.Equal(3, oauthCalls);
        Assert.Equal(1, _runner.CallCount);
        Assert.False(_configStore.Load().Armed);
        Assert.Equal(CheckerState.PhaseDone, _stateStore.Load().Phase);
    }

    [Fact]
    public async Task 第一项目成功后Fable额度收紧_第二项目不会启动()
    {
        ProductConfig config = ArmedConfig();
        config.ResumeModel = "fable";
        config.Selected = config.Selected.Take(2).ToList();
        _configStore.Save(config);
        var state = CheckerState.CreateDefault();
        state.CycleId = config.ArmCycleId;
        state.SawLimited = true;
        state.Phase = CheckerState.PhaseWaiting;
        _stateStore.Save(state);
        int oauthCalls = 0;
        var quota = new QuotaService(
            probe: _ => Task.FromResult(new ClaudeProbeResult { Reason = "unexpected" }),
            clock: () => _clock,
            oauthProbe: _ =>
            {
                oauthCalls++;
                bool fableBlocked = oauthCalls == 2;
                return Task.FromResult(new OAuthUsageResult(
                    true,
                    QuotaSnapshot(_clock, allowed: !fableBlocked,
                        QuotaWindow("five_hour", 25, _clock.AddHours(5)),
                        QuotaWindow("seven_day", 73, _clock.AddDays(4)),
                        QuotaWindow("weekly_scoped:Fable", fableBlocked ? 100 : 80,
                            _clock.AddDays(4))),
                    null,
                    "account-a"));
            });
        using ResumeEngine engine = CreateEngine(new QuotaResumeProbe(quota));

        await engine.RunOnceAsync(CancellationToken.None);

        Assert.Equal(new[] { config.Selected[0].Path }, _runner.CalledPaths);
        Assert.Equal(2, oauthCalls);
        CheckerState waiting = _stateStore.Load();
        Assert.Equal(CheckerState.PhaseWaiting, waiting.Phase);
        Assert.Equal("success", waiting.ProjectStatus![config.Selected[0].Path]);
        Assert.False(waiting.ProjectStatus.ContainsKey(config.Selected[1].Path));
        Assert.True(_configStore.Load().Armed);
    }

    [Fact]
    public async Task 每个项目开始执行前先持久化running状态()
    {
        var config = ArmedConfig();
        _configStore.Save(config);
        var state = CheckerState.CreateDefault();
        state.CycleId = config.ArmCycleId;
        state.SawLimited = true;
        state.Phase = CheckerState.PhaseWaiting;
        state.ProjectStatus = new Dictionary<string, string>
        {
            [config.Selected[0].Path] = "limited",
        };
        _stateStore.Save(state);

        _probe.Results.Enqueue(new ClaudeProbeResult { Ready = true, Reason = "ok" });
        var observed = new List<string?>();
        _runner.BeforeRun = () =>
        {
            string path = config.Selected[_runner.CallCount].Path;
            observed.Add(_stateStore.Load().ProjectStatus?.GetValueOrDefault(path));
        };

        await _engine.RunOnceAsync(CancellationToken.None);

        Assert.Equal(new[] { "running", "running", "running" }, observed);
    }

    [Fact]
    public async Task 进程登记后持久化精确RunId且项目结束时清理()
    {
        var config = ArmedConfig();
        _configStore.Save(config);
        var state = CheckerState.CreateDefault();
        state.CycleId = config.ArmCycleId;
        state.SawLimited = true;
        state.Phase = CheckerState.PhaseWaiting;
        _stateStore.Save(state);

        _probe.Results.Enqueue(new ClaudeProbeResult { Ready = true, Reason = "ok" });
        var observed = new List<(string Path, string RunId, string Status)>();
        _runner.AfterStarted = runId =>
        {
            CheckerState saved = _stateStore.Load();
            observed.Add((
                saved.ActiveProjectPath,
                saved.ActiveRunId,
                saved.ProjectStatus?.GetValueOrDefault(saved.ActiveProjectPath) ?? string.Empty));
            Assert.Equal(runId.ToString(), saved.ActiveRunId);
        };

        await _engine.RunOnceAsync(CancellationToken.None);

        Assert.Equal(config.Selected.Select(p => p.Path), observed.Select(x => x.Path));
        Assert.All(observed, x => Assert.Equal("running", x.Status));
        Assert.All(observed, x => Assert.False(string.IsNullOrEmpty(x.RunId)));
        CheckerState completed = _stateStore.Load();
        Assert.Equal(string.Empty, completed.ActiveRunId);
        Assert.Equal(string.Empty, completed.ActiveProjectPath);
    }

    [Fact]
    public async Task 某项目返回limited_本轮中止后续不执行()
    {
        var config = ArmedConfig();
        _configStore.Save(config);
        var state = CheckerState.CreateDefault();
        state.CycleId = config.ArmCycleId;
        state.SawLimited = true;
        state.Phase = CheckerState.PhaseWaiting;
        _stateStore.Save(state);

        _probe.Results.Enqueue(new ClaudeProbeResult { Ready = true, Reason = "ok" });
        _runner.Results["success"] = new ResumeRunResult { Status = "success" };
        _runner.Results["limited"] = new ResumeRunResult { Status = "limited" };
        // 第二个项目返回 limited。
        _runner.ResultByPath[config.Selected[1].Path] = "limited";

        await _engine.RunOnceAsync(CancellationToken.None);

        Assert.Equal(2, _runner.CallCount);
        Assert.Equal(
            new[] { config.Selected[0].Path, config.Selected[1].Path },
            _runner.CalledPaths);
    }

    [Fact]
    public async Task 子进程退出未确认时_本轮中止且不启动后续项目()
    {
        var config = ArmedConfig();
        _configStore.Save(config);
        var state = CheckerState.CreateDefault();
        state.CycleId = config.ArmCycleId;
        state.SawLimited = true;
        state.Phase = CheckerState.PhaseWaiting;
        _stateStore.Save(state);

        _probe.Results.Enqueue(new ClaudeProbeResult { Ready = true, Reason = "ok" });
        _runner.Results["cancel-pending"] = new ResumeRunResult
        {
            Status = "cancel-pending",
            StopRound = true,
        };
        _runner.ResultByPath[config.Selected[0].Path] = "cancel-pending";

        await _engine.RunOnceAsync(CancellationToken.None);

        Assert.Equal(1, _runner.CallCount);
        CheckerState saved = _stateStore.Load();
        Assert.Equal("cancel-pending", saved.ProjectStatus![config.Selected[0].Path]);
        Assert.False(string.IsNullOrEmpty(saved.ActiveRunId));
        Assert.Equal(config.Selected[0].Path, saved.ActiveProjectPath);
    }

    [Fact]
    public async Task 解除时终止未确认_重新布防也必须等旧RunId消失()
    {
        var config = ArmedConfig();
        _configStore.Save(config);
        var state = CheckerState.CreateDefault();
        state.CycleId = config.ArmCycleId;
        state.SawLimited = true;
        state.Phase = CheckerState.PhaseWaiting;
        _stateStore.Save(state);

        _probe.Results.Enqueue(new ClaudeProbeResult { Ready = true, Reason = "ok" });
        _runner.ResultByPath[config.Selected[0].Path] = "cancel-pending";
        _runner.Results["cancel-pending"] = new ResumeRunResult
        {
            Status = "cancel-pending",
            StopRound = true,
        };
        _runner.BeforeRun = () => _configStore.Update(latest =>
        {
            latest.Armed = false;
            latest.ArmCycleId = string.Empty;
        });

        await _engine.RunOnceAsync(CancellationToken.None);

        CheckerState pending = _stateStore.Load();
        Assert.False(string.IsNullOrEmpty(pending.PendingCancellationRunId));
        Assert.Equal(config.Selected[0].Path, pending.PendingCancellationProjectPath);
        Assert.Equal(config.ArmCycleId, pending.PendingCancellationCycleId);

        _configStore.Update(latest =>
        {
            latest.Armed = true;
            latest.ArmCycleId = "cycle-2";
        });
        _activeRunEvidence = true;
        _supervisor.CancelChildPending = true;
        _clock = _clock.AddMinutes(30);

        await _engine.RunOnceAsync(CancellationToken.None);

        Assert.Equal(1, _probe.CallCount);
        Assert.Equal(1, _runner.CallCount);
        Assert.Equal(pending.PendingCancellationRunId, _stateStore.Load().PendingCancellationRunId);
        Assert.Equal(1, _supervisor.CancelCalls);
    }

    [Fact]
    public async Task 已解除布防后待终止进程消失_本拍仍清理待确认记录()
    {
        _configStore.Save(new ProductConfig
        {
            Enabled = true,
            Armed = false,
        });
        var state = CheckerState.CreateDefault();
        state.CycleId = "old-cycle";
        state.Phase = CheckerState.PhaseResuming;
        state.ActiveRunId = "12345678-1234-1234-1234-1234567890ab";
        state.ActiveProjectPath = @"C:\proj\alpha";
        state.PendingCancellationRunId = state.ActiveRunId;
        state.PendingCancellationProjectPath = state.ActiveProjectPath;
        state.PendingCancellationCycleId = state.CycleId;
        state.ProjectStatus = new Dictionary<string, string>
        {
            [state.ActiveProjectPath] = "cancel-pending",
        };
        _stateStore.Save(state);
        _activeRunEvidence = false;

        await _engine.RunOnceAsync(CancellationToken.None);

        CheckerState cleared = _stateStore.Load();
        Assert.Equal(string.Empty, cleared.PendingCancellationRunId);
        Assert.Equal(string.Empty, cleared.PendingCancellationProjectPath);
        Assert.Equal(string.Empty, cleared.PendingCancellationCycleId);
        Assert.Equal(string.Empty, cleared.ActiveRunId);
        Assert.Equal(string.Empty, cleared.ActiveProjectPath);
        Assert.Equal("stopped", cleared.ProjectStatus![@"C:\proj\alpha"]);
        Assert.Equal(0, _probe.CallCount);
        Assert.Equal(0, _runner.CallCount);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(null)]
    public async Task 已持久化ActiveRun仍存活或不可核验_不得开始新续跑(bool? evidence)
    {
        var config = ArmedConfig();
        _configStore.Save(config);
        var state = CheckerState.CreateDefault();
        state.CycleId = config.ArmCycleId;
        state.Phase = CheckerState.PhaseResuming;
        state.ActiveRunId = "12345678-1234-1234-1234-1234567890ab";
        state.ActiveProjectPath = config.Selected[0].Path;
        state.ProjectStatus = new Dictionary<string, string>
        {
            [state.ActiveProjectPath] = "running",
        };
        _stateStore.Save(state);
        _activeRunEvidence = evidence;

        await _engine.RunOnceAsync(CancellationToken.None);

        Assert.Equal(0, _probe.CallCount);
        Assert.Equal(0, _runner.CallCount);
        Assert.Equal(state.ActiveRunId, _stateStore.Load().ActiveRunId);
        Assert.Equal(0, _supervisor.CancelCalls);
    }

    [Fact]
    public async Task 重启后ActiveRun遇到解除布防_按精确RunId终止并标记stopped()
    {
        _configStore.Save(new ProductConfig { Enabled = true, Armed = false });
        var state = CheckerState.CreateDefault();
        state.CycleId = "old-cycle";
        state.Phase = CheckerState.PhaseResuming;
        state.ActiveRunId = "12345678-1234-1234-1234-1234567890ab";
        state.ActiveProjectPath = @"C:\proj\alpha";
        state.ProjectStatus = new Dictionary<string, string>
        {
            [state.ActiveProjectPath] = "running",
        };
        _stateStore.Save(state);
        _activeRunEvidence = true;

        await _engine.RunOnceAsync(CancellationToken.None);

        CheckerState stopped = _stateStore.Load();
        Assert.Equal(1, _supervisor.CancelCalls);
        Assert.Equal(string.Empty, stopped.ActiveRunId);
        Assert.Equal(string.Empty, stopped.PendingCancellationRunId);
        Assert.Equal("stopped", stopped.ProjectStatus![state.ActiveProjectPath]);
        Assert.Equal(CheckerState.PhaseBlocked, stopped.Phase);
        Assert.True(stopped.ReplayBlocked);
    }

    [Fact]
    public async Task 重启后ActiveRun对应项目被移除_持续重试终止直到整树退出()
    {
        ProductConfig config = ArmedConfig();
        string removedPath = config.Selected[0].Path;
        config.Selected.RemoveAt(0);
        _configStore.Save(config);
        var state = CheckerState.CreateDefault();
        state.CycleId = config.ArmCycleId;
        state.Phase = CheckerState.PhaseResuming;
        state.ActiveRunId = "12345678-1234-1234-1234-1234567890ab";
        state.ActiveProjectPath = removedPath;
        state.ProjectStatus = new Dictionary<string, string>
        {
            [removedPath] = "running",
        };
        _stateStore.Save(state);
        _activeRunEvidence = true;
        _supervisor.CancelChildPending = true;

        await _engine.RunOnceAsync(CancellationToken.None);
        await _engine.RunOnceAsync(CancellationToken.None);

        CheckerState pending = _stateStore.Load();
        Assert.Equal(2, _supervisor.CancelCalls);
        Assert.Equal(state.ActiveRunId, pending.ActiveRunId);
        Assert.Equal(state.ActiveRunId, pending.PendingCancellationRunId);
        Assert.Equal("cancel-pending", pending.ProjectStatus![removedPath]);

        _supervisor.CancelChildPending = false;
        await _engine.RunOnceAsync(CancellationToken.None);

        CheckerState stopped = _stateStore.Load();
        Assert.Equal(3, _supervisor.CancelCalls);
        Assert.Equal(string.Empty, stopped.ActiveRunId);
        Assert.Equal(string.Empty, stopped.PendingCancellationRunId);
        Assert.Equal("stopped", stopped.ProjectStatus![removedPath]);
    }

    [Fact]
    public async Task 生产路径通过ProcessSupervisor核验ActiveRun而非外层PID()
    {
        ProductConfig config = ArmedConfig();
        _configStore.Save(config);
        var state = CheckerState.CreateDefault();
        state.CycleId = config.ArmCycleId;
        state.Phase = CheckerState.PhaseResuming;
        state.ActiveRunId = "12345678-1234-1234-1234-1234567890ab";
        state.ActiveProjectPath = config.Selected[0].Path;
        state.ProjectStatus = new Dictionary<string, string>
        {
            [state.ActiveProjectPath] = "running",
        };
        _stateStore.Save(state);
        var supervisor = new FakeProcessSupervisor
        {
            StatusProvider = () => new AiResume.Core.Contracts.ProcessStatus
            {
                Liveness = AiResume.Core.Contracts.ProcessLiveness.Alive,
                ChildPending = true,
            },
        };
        using var engine = new ResumeEngine(
            _configStore,
            _stateStore,
            _cycle,
            _probe,
            _runner,
            supervisor,
            NullLogger<ResumeEngine>.Instance,
            TimeSpan.FromSeconds(30),
            activeRunDetector: null);

        await engine.RunOnceAsync(CancellationToken.None);

        Assert.Equal(1, supervisor.StatusCalls);
        Assert.Equal(0, _probe.CallCount);
        Assert.Equal(0, _runner.CallCount);
        Assert.Equal(state.ActiveRunId, _stateStore.Load().ActiveRunId);
        Assert.Equal(0, supervisor.CancelCalls);
    }

    [Fact]
    public async Task 已持久化ActiveRun确认消失_未布防也会清理并标记未确认完成()
    {
        _configStore.Save(new ProductConfig { Enabled = true, Armed = false });
        var state = CheckerState.CreateDefault();
        state.CycleId = "old-cycle";
        state.Phase = CheckerState.PhaseResuming;
        state.ActiveRunId = "12345678-1234-1234-1234-1234567890ab";
        state.ActiveProjectPath = @"C:\proj\alpha";
        state.ProjectStatus = new Dictionary<string, string>
        {
            [state.ActiveProjectPath] = "running",
        };
        _stateStore.Save(state);
        _activeRunEvidence = false;

        await _engine.RunOnceAsync(CancellationToken.None);

        CheckerState cleared = _stateStore.Load();
        Assert.Equal(string.Empty, cleared.ActiveRunId);
        Assert.Equal(string.Empty, cleared.ActiveProjectPath);
        Assert.Equal("exit-null", cleared.ProjectStatus![@"C:\proj\alpha"]);
        Assert.Equal(CheckerState.PhaseBlocked, cleared.Phase);
        Assert.True(cleared.ReplayBlocked);
        Assert.Equal(0, _probe.CallCount);
        Assert.Equal(0, _runner.CallCount);
    }

    [Fact]
    public async Task 已持久化ActiveRun确认消失_当前周期锁住且后续拍不重跑()
    {
        ProductConfig config = ArmedConfig();
        _configStore.Save(config);
        var state = CheckerState.CreateDefault();
        state.CycleId = config.ArmCycleId;
        state.Phase = CheckerState.PhaseResuming;
        state.SawLimited = true;
        state.ActiveRunId = "12345678-1234-1234-1234-1234567890ab";
        state.ActiveProjectPath = config.Selected[0].Path;
        state.ProjectStatus = new Dictionary<string, string>
        {
            [state.ActiveProjectPath] = "running",
        };
        _stateStore.Save(state);
        _activeRunEvidence = false;

        await _engine.RunOnceAsync(CancellationToken.None);
        _clock = _clock.AddMinutes(30);
        _probe.Results.Enqueue(new ClaudeProbeResult { Ready = true, Reason = "ok" });
        await _engine.RunOnceAsync(CancellationToken.None);

        CheckerState blocked = _stateStore.Load();
        Assert.Equal("exit-null", blocked.ProjectStatus![config.Selected[0].Path]);
        Assert.Equal(CheckerState.PhaseBlocked, blocked.Phase);
        Assert.True(blocked.ReplayBlocked);
        Assert.Equal(0, _probe.CallCount);
        Assert.Equal(0, _runner.CallCount);
    }

    [Fact]
    public async Task 待终止Run确认消失_当前周期标记stopped且后续拍不重跑()
    {
        ProductConfig config = ArmedConfig();
        _configStore.Save(config);
        var state = CheckerState.CreateDefault();
        state.CycleId = config.ArmCycleId;
        state.Phase = CheckerState.PhaseResuming;
        state.SawLimited = true;
        state.ActiveRunId = "12345678-1234-1234-1234-1234567890ab";
        state.ActiveProjectPath = config.Selected[0].Path;
        state.PendingCancellationRunId = state.ActiveRunId;
        state.PendingCancellationProjectPath = state.ActiveProjectPath;
        state.PendingCancellationCycleId = state.CycleId;
        state.ProjectStatus = new Dictionary<string, string>
        {
            [state.ActiveProjectPath] = "cancel-pending",
        };
        _stateStore.Save(state);
        _activeRunEvidence = false;

        await _engine.RunOnceAsync(CancellationToken.None);
        _clock = _clock.AddMinutes(30);
        _probe.Results.Enqueue(new ClaudeProbeResult { Ready = true, Reason = "ok" });
        await _engine.RunOnceAsync(CancellationToken.None);

        CheckerState blocked = _stateStore.Load();
        Assert.Equal("stopped", blocked.ProjectStatus![config.Selected[0].Path]);
        Assert.Equal(CheckerState.PhaseBlocked, blocked.Phase);
        Assert.True(blocked.ReplayBlocked);
        Assert.Equal(0, _probe.CallCount);
        Assert.Equal(0, _runner.CallCount);
    }

    [Fact]
    public async Task 状态JSON损坏时_整拍失败关闭且不探测不续跑()
    {
        ProductConfig config = ArmedConfig();
        _configStore.Save(config);
        const string corrupted = "{\"phase\":\"resuming\",\"activeRunId\":\"unterminated";
        using (var connection = StorageDatabase.Open(_stateDbPath))
        {
            StorageDatabase.Execute(connection, """
                INSERT INTO product_state(id, state_json, updated_at) VALUES (1, $json, $now)
                ON CONFLICT(id) DO UPDATE SET state_json = $json, updated_at = $now;
                """, null, ("$json", corrupted), ("$now", DateTimeOffset.UtcNow.ToString("o")));
        }

        await _engine.RunOnceAsync(CancellationToken.None);

        Assert.Equal(0, _probe.CallCount);
        Assert.Equal(0, _runner.CallCount);
        Assert.True(_configStore.Load().Armed);
        Assert.ThrowsAny<Exception>(() => _stateStore.LoadStrict());

        using (var connection = StorageDatabase.Open(_stateDbPath))
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT state_json FROM product_state WHERE id = 1;";
            Assert.Equal(corrupted, command.ExecuteScalar() as string);
        }

        var repaired = CheckerState.CreateDefault();
        repaired.CycleId = config.ArmCycleId;
        repaired.Phase = CheckerState.PhaseWaiting;
        _stateStore.Save(repaired);

        await _engine.RunOnceAsync(CancellationToken.None);

        Assert.Equal(1, _probe.CallCount);
        Assert.Equal(0, _runner.CallCount);
    }

    [Fact]
    public async Task monitorError安全中止后_锁住本周期且后续拍不重试()
    {
        var config = ArmedConfig();
        _configStore.Save(config);
        var state = CheckerState.CreateDefault();
        state.CycleId = config.ArmCycleId;
        state.SawLimited = true;
        state.Phase = CheckerState.PhaseWaiting;
        _stateStore.Save(state);

        _probe.Results.Enqueue(new ClaudeProbeResult { Ready = true, Reason = "ok" });
        _runner.ResultByPath[config.Selected[0].Path] = "monitor-error";
        _runner.Results["monitor-error"] = new ResumeRunResult
        {
            Status = "monitor-error",
            StopRound = true,
        };

        await _engine.RunOnceAsync(CancellationToken.None);

        CheckerState stopped = _stateStore.Load();
        Assert.Equal(1, _runner.CallCount);
        Assert.Equal(CheckerState.PhaseBlocked, stopped.Phase);
        Assert.True(stopped.ReplayBlocked);
        Assert.Equal("monitor-error", stopped.ProjectStatus![config.Selected[0].Path]);
        Assert.Equal(string.Empty, stopped.ActiveRunId);
        Assert.Equal(string.Empty, stopped.ActiveProjectPath);
        Assert.True(_configStore.Load().Armed);

        _clock = _clock.AddMinutes(30);
        _probe.Results.Enqueue(new ClaudeProbeResult { Ready = true, Reason = "ok" });
        await _engine.RunOnceAsync(CancellationToken.None);

        Assert.Equal(1, _probe.CallCount);
        Assert.Equal(1, _runner.CallCount);
    }

    [Fact]
    public async Task 已发生副作用后限流_锁住本周期且下一拍不重跑()
    {
        var config = ArmedConfig();
        _configStore.Save(config);
        var state = CheckerState.CreateDefault();
        state.CycleId = config.ArmCycleId;
        state.SawLimited = true;
        state.Phase = CheckerState.PhaseWaiting;
        _stateStore.Save(state);

        _probe.Results.Enqueue(new ClaudeProbeResult { Ready = true, Reason = "ok" });
        _runner.ResultByPath[config.Selected[0].Path] = "limited-side-effects";
        _runner.Results["limited-side-effects"] = new ResumeRunResult
        {
            Status = "limited-side-effects",
            SideEffectsStarted = true,
            StopRound = true,
        };

        await _engine.RunOnceAsync(CancellationToken.None);

        CheckerState blocked = _stateStore.Load();
        Assert.Equal(1, _runner.CallCount);
        Assert.Equal(CheckerState.PhaseBlocked, blocked.Phase);
        Assert.True(blocked.ReplayBlocked);
        Assert.Equal("limited-side-effects", blocked.ProjectStatus![config.Selected[0].Path]);

        _clock = _clock.AddMinutes(30);
        _probe.Results.Enqueue(new ClaudeProbeResult { Ready = true, Reason = "ok" });
        await _engine.RunOnceAsync(CancellationToken.None);

        Assert.Equal(1, _probe.CallCount);
        Assert.Equal(1, _runner.CallCount);
    }

    [Fact]
    public async Task 一轮全部成功且非连续_解除布防清空周期()
    {
        var config = ArmedConfig(continuous: false);
        _configStore.Save(config);
        var state = CheckerState.CreateDefault();
        state.CycleId = config.ArmCycleId;
        state.SawLimited = true;
        state.Phase = CheckerState.PhaseWaiting;
        _stateStore.Save(state);

        _probe.Results.Enqueue(new ClaudeProbeResult { Ready = true, Reason = "ok" });
        _runner.Results["success"] = new ResumeRunResult { Status = "success" };

        await _engine.RunOnceAsync(CancellationToken.None);

        var freshConfig = _configStore.Load();
        Assert.False(freshConfig.Armed);
        Assert.Equal(string.Empty, freshConfig.ArmCycleId);
    }

    [Fact]
    public async Task 一次性周期已Done但仍Armed_重启后只补解除不探测不续跑()
    {
        var config = ArmedConfig(continuous: false);
        _configStore.Save(config);
        var state = CheckerState.CreateDefault();
        state.CycleId = config.ArmCycleId;
        state.Phase = CheckerState.PhaseDone;
        state.ProjectStatus = config.Selected.ToDictionary(p => p.Path, _ => "success");
        _stateStore.Save(state);

        await _engine.RunOnceAsync(CancellationToken.None);

        ProductConfig recovered = _configStore.Load();
        Assert.False(recovered.Armed);
        Assert.Equal(string.Empty, recovered.ArmCycleId);
        Assert.Equal(0, _probe.CallCount);
        Assert.Equal(0, _runner.CallCount);
        Assert.Equal(CheckerState.PhaseDone, _stateStore.Load().Phase);
    }

    [Fact]
    public async Task 一轮全部成功且连续_保持布防()
    {
        var config = ArmedConfig(continuous: true);
        _configStore.Save(config);
        var state = CheckerState.CreateDefault();
        state.CycleId = config.ArmCycleId;
        state.SawLimited = true;
        state.Phase = CheckerState.PhaseWaiting;
        _stateStore.Save(state);

        _probe.Results.Enqueue(new ClaudeProbeResult { Ready = true, Reason = "ok" });
        _runner.Results["success"] = new ResumeRunResult { Status = "success" };

        await _engine.RunOnceAsync(CancellationToken.None);

        var freshConfig = _configStore.Load();
        Assert.True(freshConfig.Armed);
        Assert.Equal(config.ArmCycleId, freshConfig.ArmCycleId);
    }

    [Fact]
    public async Task 当前周期已成功的项目_额度再次恢复时不会重复续跑()
    {
        ProductConfig config = ArmedConfig();
        _configStore.Save(config);
        var state = CheckerState.CreateDefault();
        state.CycleId = config.ArmCycleId;
        state.SawLimited = true;
        state.Phase = CheckerState.PhaseWaiting;
        state.ProjectStatus = new Dictionary<string, string>
        {
            [config.Selected[0].Path] = "success",
            [config.Selected[1].Path] = "limited",
        };
        _stateStore.Save(state);

        _probe.Results.Enqueue(new ClaudeProbeResult { Ready = true, Reason = "ok" });
        _runner.Results["success"] = new ResumeRunResult { Status = "success" };

        await _engine.RunOnceAsync(CancellationToken.None);

        Assert.Equal(
            config.Selected.Skip(1).Select(p => p.Path),
            _runner.CalledPaths);
    }

    [Fact]
    public async Task 阻断周期解除后重新布防_新周期可再次续跑()
    {
        ProductConfig oldConfig = ArmedConfig();
        _configStore.Save(oldConfig);
        var blocked = CheckerState.CreateDefault();
        blocked.CycleId = oldConfig.ArmCycleId;
        blocked.Phase = CheckerState.PhaseBlocked;
        blocked.ReplayBlocked = true;
        blocked.ProjectStatus = new Dictionary<string, string>
        {
            [oldConfig.Selected[0].Path] = "monitor-error",
        };
        _stateStore.Save(blocked);

        _configStore.Update(config =>
        {
            config.Armed = false;
            config.ArmCycleId = string.Empty;
        });
        _configStore.Update(config =>
        {
            config.Armed = true;
            config.ArmCycleId = "cycle-2";
        });

        _probe.Results.Enqueue(new ClaudeProbeResult { Ready = false, Reason = "limited" });
        await _engine.RunOnceAsync(CancellationToken.None);
        _clock = _clock.AddMinutes(4);
        _probe.Results.Enqueue(new ClaudeProbeResult { Ready = true, Reason = "ok" });
        await _engine.RunOnceAsync(CancellationToken.None);

        Assert.Equal(oldConfig.Selected.Count, _runner.CallCount);
        Assert.False(_configStore.Load().Armed);
    }

    [Fact]
    public async Task 续跑途中解除布防_剩余项目不再执行()
    {
        var config = ArmedConfig();
        _configStore.Save(config);
        var state = CheckerState.CreateDefault();
        state.CycleId = config.ArmCycleId;
        state.SawLimited = true;
        state.Phase = CheckerState.PhaseWaiting;
        _stateStore.Save(state);

        _probe.Results.Enqueue(new ClaudeProbeResult { Ready = true, Reason = "ok" });
        _runner.Results["success"] = new ResumeRunResult { Status = "success" };

        // 第一个项目**执行期间**把配置改为解除布防。
        // 注意 BeforeRun 在记账之前触发,所以第一次调用时 CallCount 仍为 0;
        // 若写成 == 1,解除会发生在第二个项目已进入 RunAsync 之后,引擎届时已无从拦截。
        _runner.BeforeRun = () =>
        {
            if (_runner.CallCount == 0)
            {
                var fresh = _configStore.Load();
                fresh.Armed = false;
                fresh.ArmCycleId = string.Empty;
                _configStore.Save(fresh);
            }
        };

        await _engine.RunOnceAsync(CancellationToken.None);

        Assert.Equal(1, _runner.CallCount);
        Assert.Equal(new[] { config.Selected[0].Path }, _runner.CalledPaths);
        Assert.True(_runner.ShouldContinueCallCount > 0);
        Assert.Equal("stopped", _stateStore.Load().ProjectStatus![config.Selected[0].Path]);
    }

    [Fact]
    public async Task 续跑途中移除尚未执行项目_该项目不会启动()
    {
        var config = ArmedConfig();
        _configStore.Save(config);
        var state = CheckerState.CreateDefault();
        state.CycleId = config.ArmCycleId;
        state.SawLimited = true;
        state.Phase = CheckerState.PhaseWaiting;
        _stateStore.Save(state);

        _probe.Results.Enqueue(new ClaudeProbeResult { Ready = true, Reason = "ok" });
        string removedPath = config.Selected[1].Path;
        _runner.BeforeRun = () =>
        {
            if (_runner.CallCount == 0)
            {
                _configStore.Update(fresh =>
                    fresh.Selected.RemoveAll(p =>
                        string.Equals(p.Path, removedPath, StringComparison.OrdinalIgnoreCase)));
            }
        };

        await _engine.RunOnceAsync(CancellationToken.None);

        Assert.DoesNotContain(removedPath, _runner.CalledPaths, StringComparer.OrdinalIgnoreCase);
        Assert.Equal(
            new[] { config.Selected[0].Path, config.Selected[2].Path },
            _runner.CalledPaths);
    }

    [Fact]
    public async Task 额度复核后最终启动门禁前移除项目_未启动项目不会锁死周期()
    {
        var config = ArmedConfig();
        _configStore.Save(config);
        var state = CheckerState.CreateDefault();
        state.CycleId = config.ArmCycleId;
        state.SawLimited = true;
        state.Phase = CheckerState.PhaseWaiting;
        _stateStore.Save(state);

        _probe.Results.Enqueue(new ClaudeProbeResult { Ready = true, Reason = "ok" });
        string removedPath = config.Selected[0].Path;
        bool removed = false;
        _runner.BeforeStart = project =>
        {
            if (!removed && string.Equals(project.Path, removedPath, StringComparison.OrdinalIgnoreCase))
            {
                removed = true;
                _configStore.Update(fresh =>
                    fresh.Selected.RemoveAll(selected =>
                        string.Equals(selected.Path, removedPath, StringComparison.OrdinalIgnoreCase)));
            }
        };

        await _engine.RunOnceAsync(CancellationToken.None);

        Assert.True(removed);
        Assert.Equal(
            new[] { config.Selected[1].Path, config.Selected[2].Path },
            _runner.CalledPaths);
        CheckerState completed = _stateStore.Load();
        Assert.False(completed.ReplayBlocked);
        Assert.Equal(CheckerState.PhaseDone, completed.Phase);
        Assert.False(completed.ProjectStatus!.ContainsKey(removedPath));
        Assert.False(_configStore.Load().Armed);
    }

    [Fact]
    public async Task 续跑途中新增项目_必须纳入当前轮次后才能完成()
    {
        var config = ArmedConfig();
        _configStore.Save(config);
        var state = CheckerState.CreateDefault();
        state.CycleId = config.ArmCycleId;
        state.SawLimited = true;
        state.Phase = CheckerState.PhaseWaiting;
        _stateStore.Save(state);

        var added = new ProjectRef
        {
            Name = "added-during-run",
            Path = Path.Combine(_shadowRoot, "added-during-run"),
        };
        Directory.CreateDirectory(added.Path);
        _probe.Results.Enqueue(new ClaudeProbeResult { Ready = true, Reason = "ok" });
        _runner.BeforeRun = () =>
        {
            if (_runner.CallCount == 0)
            {
                _configStore.Update(fresh => fresh.Selected.Insert(1, added));
            }
        };

        await _engine.RunOnceAsync(CancellationToken.None);

        Assert.Equal(
            new[]
            {
                config.Selected[0].Path,
                added.Path,
                config.Selected[1].Path,
                config.Selected[2].Path,
            },
            _runner.CalledPaths);
        ProductConfig completedConfig = _configStore.Load();
        Assert.False(completedConfig.Armed);
        CheckerState completedState = _stateStore.Load();
        Assert.Equal(CheckerState.PhaseDone, completedState.Phase);
        Assert.Equal("success", completedState.ProjectStatus![added.Path]);
    }

    [Fact]
    public async Task Runner返回limited但声明已有副作用_引擎独立阻断重放()
    {
        var config = ArmedConfig();
        _configStore.Save(config);
        var state = CheckerState.CreateDefault();
        state.CycleId = config.ArmCycleId;
        state.SawLimited = true;
        state.Phase = CheckerState.PhaseWaiting;
        _stateStore.Save(state);

        _probe.Results.Enqueue(new ClaudeProbeResult { Ready = true, Reason = "ok" });
        _runner.ResultByPath[config.Selected[0].Path] = "limited";
        _runner.Results["limited"] = new ResumeRunResult
        {
            Status = "limited",
            Limited = true,
            SideEffectsStarted = true,
        };

        await _engine.RunOnceAsync(CancellationToken.None);

        CheckerState blocked = _stateStore.Load();
        Assert.True(blocked.ReplayBlocked);
        Assert.Equal(CheckerState.PhaseBlocked, blocked.Phase);
        Assert.Equal("limited-side-effects", blocked.ProjectStatus![config.Selected[0].Path]);
        Assert.Equal(1, _runner.CallCount);
    }

    [Fact]
    public async Task 假probe抛异常_单拍吞掉引擎不退出()
    {
        var config = ArmedConfig();
        _configStore.Save(config);
        var state = CheckerState.CreateDefault();
        state.CycleId = config.ArmCycleId;
        // 必须先观测到过限流,第二拍的 ready 才会触发续跑("布防先于限流"语义)。
        state.SawLimited = true;
        state.Phase = CheckerState.PhaseWaiting;
        _stateStore.Save(state);

        _probe.ThrowOnNext = true;

        // 第一拍:探测抛异常,必须被吞掉且不改变任何周期状态。
        await _engine.RunOnceAsync(CancellationToken.None);
        Assert.Equal(1, _probe.CallCount);
        Assert.Equal(0, _runner.CallCount);

        // 第二拍:MarkProbeAttempt 已把 LastProbeUtc 设成第一拍时刻,
        // 不推进时钟的话 ShouldProbe 会直接返回 false,第二拍将空转。
        _clock = _clock.AddMinutes(30);
        _probe.Results.Enqueue(new ClaudeProbeResult { Ready = true, Reason = "ok" });
        _runner.Results["success"] = new ResumeRunResult { Status = "success" };
        await _engine.RunOnceAsync(CancellationToken.None);

        // 引擎在异常后仍然正常工作:又探测一次,并按队列跑完三个项目。
        Assert.Equal(4, _probe.CallCount);
        Assert.Equal(3, _runner.CallCount);
    }

    /// <summary>假探测器:按注入序列返回结果,记录调用次数,可配置抛异常。</summary>
    private sealed class FakeProbe : IClaudeUsageProbe
    {
        public Queue<ClaudeProbeResult> Results { get; } = new();
        public List<string> Models { get; } = new();
        public int CallCount { get; private set; }
        public bool ThrowOnNext { get; set; }
        public Action<string, int>? AfterProbe { get; set; }
        private ClaudeProbeResult _lastResult = new() { Ready = false, Reason = "unknown" };

        public Task<ClaudeProbeResult> ProbeAsync(string model, string workingDirectory, CancellationToken cancellationToken)
        {
            CallCount++;
            Models.Add(model);
            if (ThrowOnNext)
            {
                ThrowOnNext = false;
                throw new InvalidOperationException("模拟探测异常");
            }

            if (Results.Count > 0)
            {
                _lastResult = Results.Dequeue();
            }
            AfterProbe?.Invoke(model, CallCount);
            return Task.FromResult(_lastResult);
        }
    }

    /// <summary>假运行器:记录调用顺序,按项目路径或状态返回注入结果。</summary>
    private sealed class FakeRunner : IClaudeResumeRunner
    {
        public List<string> CalledPaths { get; } = new();
        public List<string> ResumeModels { get; } = new();
        public int CallCount => CalledPaths.Count;
        public Dictionary<string, ResumeRunResult> Results { get; } = new();
        public Dictionary<string, string> ResultByPath { get; } = new();
        public Action<ProjectRef>? BeforeStart { get; set; }
        public Action? BeforeRun { get; set; }
        public Action<RunId>? AfterStarted { get; set; }
        public bool CancelBeforeSpawn { get; set; }
        public int ShouldContinueCallCount { get; private set; }

        public Task<ResumeRunResult> RunAsync(
            ProjectRef project,
            ProductConfig config,
            CancellationToken cancellationToken,
            Func<RunId, bool>? beforeStart = null,
            Func<RunId, bool?>? shouldContinue = null)
        {
            RunId runId = RunId.New();
            BeforeStart?.Invoke(project);
            if (beforeStart is not null && !beforeStart(runId))
            {
                return Task.FromResult(new ResumeRunResult { Status = "stopped", StopRound = true });
            }

            if (CancelBeforeSpawn)
            {
                return Task.FromResult(new ResumeRunResult
                {
                    Status = "stopped",
                    StopRound = true,
                    PreparedRunId = runId,
                    StartCancelledBeforeSpawn = true,
                });
            }

            BeforeRun?.Invoke();
            CalledPaths.Add(project.Path);
            ResumeModels.Add(config.ResumeModel);
            AfterStarted?.Invoke(runId);

            string status;
            if (ResultByPath.TryGetValue(project.Path, out var pathStatus))
            {
                status = pathStatus;
            }
            else
            {
                status = "success";
            }

            if (shouldContinue is not null)
            {
                ShouldContinueCallCount++;
                if (shouldContinue(runId) == false)
                {
                    if (status == "cancel-pending" &&
                        Results.TryGetValue(status, out var cancelledResult))
                    {
                        return Task.FromResult(cancelledResult with
                        {
                            RunId = cancelledResult.RunId ?? runId,
                        });
                    }

                    return Task.FromResult(new ResumeRunResult
                    {
                        Status = "stopped",
                        StopRound = true,
                        RunId = runId,
                    });
                }
            }

            if (Results.TryGetValue(status, out var result))
            {
                return Task.FromResult(result with { RunId = result.RunId ?? runId });
            }

            return Task.FromResult(new ResumeRunResult { Status = status, RunId = runId });
        }
    }
}
