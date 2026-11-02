using AiResume.Core;
using AiResume.Storage;
using AiResume.Worker.Probes;
using AiResume.Worker.Products;
using AiResume.Worker.Resume;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AiResume.Tests;

/// <summary>
/// 逐项目续跑调度(ADR-0006):每个项目按自己会话的限额中断判定是否待续跑，按自己的模型取额度证据，
/// 会话占用只挡自己;没能启动不锁周期;最终门禁发现已在别处续上就不再 spawn。
/// 全部用假探测/假运行器/假会话检查器驱动，不启动进程、不读真实会话目录。
/// </summary>
[Collection(SqliteCollection.Name)]
public sealed class ResumeSchedulerTests : IDisposable
{
    private const string Opus = "claude-opus-5-5";
    private const string Fable = "claude-fable-5-1";

    private readonly string _root;
    private readonly ProductConfigStore _configStore;
    private readonly ProductStateStore _stateStore;
    private readonly World _world = new();
    private readonly ScriptedProbe _probe;
    private readonly RecordingRunner _runner;
    private readonly RecordingNotices _notices = new();
    private readonly ResumeEngine _engine;
    private DateTimeOffset _clock = new(2026, 10, 30, 8, 0, 0, TimeSpan.Zero);

    public ResumeSchedulerTests()
    {
        _root = TestTemp.NewDir("resume-scheduler");
        string database = Path.Combine(_root, "runs.db");
        StorageDatabase.Migrate(database);
        _configStore = new ProductConfigStore(_root);
        _stateStore = new ProductStateStore(database);
        _probe = new ScriptedProbe(_world);
        _runner = new RecordingRunner();
        _engine = new ResumeEngine(_configStore, _stateStore, new CheckerCycle(_stateStore, () => _clock), _probe, _runner,
            new FakeProcessSupervisor(), NullLogger<ResumeEngine>.Instance, TimeSpan.FromSeconds(30), _ => false,
            _world, _notices, () => _clock);
    }

    public void Dispose()
    {
        _engine.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
    }

    [Fact]
    public async Task Fable周额度满只挡Fable项目_Opus项目先续_恢复后再续Fable并解除布防()
    {
        Arm(Project("a", Fable), Project("b", Opus));
        _world.Interrupt("a", "u-a");
        _world.Interrupt("b", "u-b");
        _world.Quota[Fable] = "limited";
        _world.Quota[Opus] = "ok";

        await _engine.RunOnceAsync(default);

        Assert.Equal(new[] { P("b") }, _runner.Paths);
        Assert.Equal(new[] { Opus }, _runner.Models);
        CheckerState state = _stateStore.Load();
        Assert.Equal("success", state.ProjectStatus![P("b")]);
        Assert.Equal("limited", state.ProjectStatus[P("a")]);
        Assert.Equal("quota:limited", state.ProjectNotes![P("a")].Reason);
        Assert.True(_configStore.Load().Armed);

        _world.Quota[Fable] = "ok";
        _clock = _clock.AddMinutes(5);
        await _engine.RunOnceAsync(default);

        Assert.Equal(new[] { P("b"), P("a") }, _runner.Paths);
        Assert.Equal(new[] { Opus, Fable }, _runner.Models);
        Assert.False(_configStore.Load().Armed);
    }

    [Fact]
    public async Task 同模型两个项目串行_第二个启动前重新取证_额度被用完就回到等待()
    {
        Arm(Project("a", Opus), Project("b", Opus));
        _world.Interrupt("a", "u-a");
        _world.Interrupt("b", "u-b");
        _world.Quota[Opus] = "ok";
        _runner.OnRun = project => _world.Quota[Opus] = "limited";

        await _engine.RunOnceAsync(default);

        Assert.Equal(new[] { P("a") }, _runner.Paths);
        Assert.Equal(2, _probe.CallCount);
        CheckerState state = _stateStore.Load();
        Assert.Equal("limited", state.ProjectStatus![P("b")]);
        Assert.Equal("quota:limited", state.ProjectNotes![P("b")].Reason);
        Assert.False(state.ReplayBlocked);
    }

    [Fact]
    public async Task 会话开在客户端只跳过该项目_其它项目照跑_宽限期后提醒一次()
    {
        Arm(Project("a", Opus), Project("b", Opus));
        _world.Interrupt("a", "u-a", resetsAt: _clock.AddMinutes(-30));
        _world.Interrupt("b", "u-b");
        _world.Session[P("a")] = "in-use";
        _world.Quota[Opus] = "ok";

        await _engine.RunOnceAsync(default);

        Assert.Equal(new[] { P("b") }, _runner.Paths);
        CheckerState state = _stateStore.Load();
        Assert.False(state.ReplayBlocked);
        Assert.Equal("limited", state.ProjectStatus![P("a")]);
        Assert.Equal("session-in-use", state.ProjectNotes![P("a")].Reason);
        Assert.True(_configStore.Load().Armed);
        ResumeNotice notice = Assert.Single(_notices.Items, n => n.Type == "session-open");
        Assert.Equal(P("a"), notice.ProjectPath);

        _clock = _clock.AddMinutes(5);
        await _engine.RunOnceAsync(default);
        Assert.Single(_notices.Items, n => n.Type == "session-open");
    }

    [Fact]
    public async Task 额度刚恢复时会话仍开着_先给原生自动继续留宽限期不提醒()
    {
        Arm(Project("a", Opus));
        _world.Interrupt("a", "u-a", resetsAt: _clock.AddMinutes(-2));
        _world.Session[P("a")] = "in-use";
        _world.Quota[Opus] = "ok";

        await _engine.RunOnceAsync(default);

        Assert.Empty(_runner.Paths);
        Assert.DoesNotContain(_notices.Items, n => n.Type == "session-open");
    }

    [Fact]
    public async Task 最终门禁发现会话已在别处续上_不spawn且记为已在客户端继续()
    {
        Arm(Project("a", Opus));
        _world.Interrupt("a", "u-a");
        _world.Quota[Opus] = "ok";
        _runner.BeforeGate = project => _world.Clear(project.Path, "u-a-user-reply");

        await _engine.RunOnceAsync(default);

        Assert.Empty(_runner.Paths);
        CheckerState state = _stateStore.Load();
        Assert.Equal("continued-elsewhere", state.ProjectStatus![P("a")]);
        Assert.False(state.ReplayBlocked);

        _clock = _clock.AddMinutes(5);
        await _engine.RunOnceAsync(default);
        Assert.False(_configStore.Load().Armed);
    }

    [Fact]
    public async Task 最终门禁发现尾部换成另一条中断_不spawn也不锁周期()
    {
        Arm(Project("a", Opus));
        _world.Interrupt("a", "u-a");
        _world.Quota[Opus] = "ok";
        _runner.BeforeGate = project => _world.Interrupt("a", "u-a-2");

        await _engine.RunOnceAsync(default);

        Assert.Empty(_runner.Paths);
        CheckerState state = _stateStore.Load();
        Assert.False(state.ReplayBlocked);
        Assert.Equal("not-started:evidence-changed", state.ProjectNotes![P("a")].Reason);

        _runner.BeforeGate = null;
        _clock = _clock.AddMinutes(5);
        await _engine.RunOnceAsync(default);
        Assert.Equal(new[] { P("a") }, _runner.Paths);
    }

    [Fact]
    public async Task 同一次中断只续一次_续跑后会话尾部未变也不重复续跑()
    {
        Arm(continuous: true, Project("a", Opus));
        _world.Interrupt("a", "u-a");
        _world.Quota[Opus] = "ok";

        await _engine.RunOnceAsync(default);
        _clock = _clock.AddMinutes(20);
        await _engine.RunOnceAsync(default);

        Assert.Equal(new[] { P("a") }, _runner.Paths);
        CheckerState state = _stateStore.Load();
        Assert.Equal("u-a", state.ConsumedInterruptions![P("a")]);
        Assert.Equal("success", state.ProjectStatus![P("a")]);
    }

    [Fact]
    public async Task 连续模式下完成后再次被打断会重新入队()
    {
        Arm(continuous: true, Project("a", Opus));
        _world.Interrupt("a", "u-1");
        _world.Quota[Opus] = "ok";
        _runner.OnRun = project => _world.Clear(project.Path, "reply-1");

        await _engine.RunOnceAsync(default);
        _world.Interrupt("a", "u-2");
        _clock = _clock.AddMinutes(20);
        await _engine.RunOnceAsync(default);

        Assert.Equal(new[] { P("a"), P("a") }, _runner.Paths);
        Assert.True(_configStore.Load().Armed);
    }

    [Fact]
    public async Task 运行器报告会话占用或令牌将过期时只记原因_不锁周期且继续下一个项目()
    {
        Arm(Project("a", Opus), Project("b", Opus));
        _world.Interrupt("a", "u-a");
        _world.Interrupt("b", "u-b");
        _world.Quota[Opus] = "ok";
        _runner.NotStartedByPath[P("a")] = "token-expiring";

        await _engine.RunOnceAsync(default);

        Assert.Equal(new[] { P("b") }, _runner.Paths);
        CheckerState state = _stateStore.Load();
        Assert.False(state.ReplayBlocked);
        Assert.Equal("limited", state.ProjectStatus![P("a")]);
        Assert.Equal("not-started:token-expiring", state.ProjectNotes![P("a")].Reason);
        Assert.Empty(state.ActiveRunId);
    }

    [Fact]
    public async Task 未被限额打断的项目不续跑_一次性周期保持监视()
    {
        Arm(Project("a", Opus));
        _world.Clear(P("a"), "normal-reply");
        _world.Quota[Opus] = "ok";

        await _engine.RunOnceAsync(default);

        Assert.Empty(_runner.Paths);
        Assert.True(_configStore.Load().Armed);
        Assert.Equal("not-interrupted", _stateStore.Load().ProjectNotes![P("a")].Reason);
    }

    [Fact]
    public async Task 会话记录读不出时待续跑项目只等待_不放行也不锁周期()
    {
        Arm(Project("a", Opus));
        _world.Interrupt("a", "u-a");
        _world.Quota[Opus] = "limited";
        await _engine.RunOnceAsync(default);

        _world.Evidence.Remove(P("a"));
        _world.Quota[Opus] = "ok";
        _clock = _clock.AddMinutes(5);
        await _engine.RunOnceAsync(default);

        Assert.Empty(_runner.Paths);
        CheckerState state = _stateStore.Load();
        Assert.Equal("limited", state.ProjectStatus![P("a")]);
        Assert.Equal("evidence-unverified", state.ProjectNotes![P("a")].Reason);
        Assert.False(state.ReplayBlocked);
    }

    [Fact]
    public async Task Codex项目没有会话证据时沿用模型受限后恢复才续跑的语义()
    {
        Arm(Project("a", "gpt-5.6-sol"));
        ProductConfig config = _configStore.Load();
        config.ResumeTarget = config.ResumeTarget! with { Agent = "codex", ModelId = "gpt-5.6-sol" };
        config.ResumeModel = "gpt-5.6-sol";
        _configStore.Save(config);
        _world.Codex = true;
        _world.Quota["gpt-5.6-sol"] = "ok";

        await _engine.RunOnceAsync(default);
        Assert.Empty(_runner.Paths);

        _world.Quota["gpt-5.6-sol"] = "limited";
        _clock = _clock.AddMinutes(20);
        await _engine.RunOnceAsync(default);
        Assert.Empty(_runner.Paths);

        _world.Quota["gpt-5.6-sol"] = "ok";
        _clock = _clock.AddMinutes(5);
        await _engine.RunOnceAsync(default);
        Assert.Equal(new[] { P("a") }, _runner.Paths);
    }

    [Fact]
    public async Task 续跑开始与结束都写一次提醒_限流结果不打扰()
    {
        Arm(Project("a", Opus));
        _world.Interrupt("a", "u-a");
        _world.Quota[Opus] = "ok";

        await _engine.RunOnceAsync(default);

        Assert.Contains(_notices.Items, n => n.Type == "resume-started" && n.ProjectPath == P("a") && n.ModelId == Opus);
        Assert.Contains(_notices.Items, n => n.Type == "resume-succeeded" && n.ProjectPath == P("a"));
    }

    [Fact]
    public async Task 一个项目一跑就被限流不饿死别的项目_误判计数按项目算()
    {
        // a 排在队首,额度说可用、一跑就被限流;b 是另一个模型。旧行为:a 每轮第一个被选中、限流后整轮结束,b 永远轮不到。
        Arm(Project("a", Opus), Project("b", Fable));
        _world.Interrupt("a", "u-a");
        _world.Interrupt("b", "u-b");
        _world.Quota[Opus] = "ok";
        _world.Quota[Fable] = "ok";
        _runner.StatusByPath[P("a")] = "limited";

        await _engine.RunOnceAsync(default);

        Assert.Equal(new[] { P("a"), P("b") }, _runner.Paths);
        CheckerState state = _stateStore.Load();
        Assert.Equal("limited", state.ProjectStatus![P("a")]);
        Assert.Equal("success", state.ProjectStatus[P("b")]);
        Assert.Equal(1, state.ProjectLimitedRefires![P("a")]);
        Assert.False(state.ReplayBlocked);
    }

    [Fact]
    public async Task 最终门禁发现同目录新开的会话已接着干_不再续旧会话并说明是别的会话()
    {
        Arm(Project("a", Opus));
        _world.Interrupt("a", "u-a");
        _world.Quota[Opus] = "ok";
        _world.TakenOver.Add(P("a"));

        await _engine.RunOnceAsync(default);

        Assert.Empty(_runner.Paths);
        CheckerState state = _stateStore.Load();
        Assert.Equal("continued-elsewhere", state.ProjectStatus![P("a")]);
        Assert.Equal("other-session", state.ProjectNotes![P("a")].Detail);
    }

    [Fact]
    public async Task 续跑提醒写入失败不吞掉_下一拍重写()
    {
        Arm(Project("a", Opus));
        _world.Interrupt("a", "u-a");
        _world.Quota[Opus] = "credentials-unavailable";
        _notices.FailNext = 1;

        await _engine.RunOnceAsync(default);
        Assert.DoesNotContain(_notices.Items, n => n.Type == "credentials-unavailable");

        _clock = _clock.AddMinutes(5);
        await _engine.RunOnceAsync(default);
        Assert.Contains(_notices.Items, n => n.Type == "credentials-unavailable");
    }

    [Fact]
    public async Task 同一个不会自己好的原因连续三次没能启动_发一次飞书提醒()
    {
        Arm(Project("a", Opus));
        _world.Interrupt("a", "u-a");
        _world.Quota[Opus] = "ok";
        _runner.NotStartedByPath[P("a")] = "no-claude";

        for (int i = 0; i < 4; i++)
        {
            await _engine.RunOnceAsync(default);
            _clock = _clock.AddMinutes(5);
        }

        ResumeNotice notice = Assert.Single(_notices.Items, n => n.Type == "resume-not-started");
        Assert.Equal("no-claude", notice.Detail);
        Assert.Equal(P("a"), notice.ProjectPath);
        Assert.True(_configStore.Load().Armed);
    }

    [Fact]
    public async Task 第三次未启动提醒写入失败_第四次补发且只发一次()
    {
        Arm(Project("a", Opus));
        _world.Interrupt("a", "u-a");
        _world.Quota[Opus] = "ok";
        _runner.NotStartedByPath[P("a")] = "no-claude";

        for (int i = 0; i < 5; i++)
        {
            _notices.FailNext = i == 2 ? 1 : 0;
            await _engine.RunOnceAsync(default);
            _clock = _clock.AddMinutes(5);
        }

        Assert.Single(_notices.Items, n => n.Type == "resume-not-started");
    }

    [Fact]
    public async Task 已开始续跑提醒写入失败_监控循环下次询问时补发()
    {
        Arm(Project("a", Opus));
        _world.Interrupt("a", "u-a");
        _world.Quota[Opus] = "ok";
        _runner.ContinueChecks = 3;
        _notices.FailNext = 1;

        await _engine.RunOnceAsync(default);

        Assert.Single(_notices.Items, n => n.Type == "resume-started");
        Assert.Contains(_notices.Items, n => n.Type == "resume-succeeded");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task 取证时发现已在别的会话接着干_记为别处继续_那个会话之后消失也不回头续(bool continuous)
    {
        Arm(continuous, Project("a", Opus));
        _world.Interrupt("a", "u-a");
        _world.Quota[Opus] = "limited";
        _world.ProbeChecksTakeover = true;
        await _engine.RunOnceAsync(default);
        Assert.Equal("limited", _stateStore.Load().ProjectStatus![P("a")]);

        // 额度恢复前，用户在同目录新开会话接着干了。
        _world.TakenOver.Add(P("a"));
        _world.Quota[Opus] = "ok";
        _clock = _clock.AddMinutes(5);
        await _engine.RunOnceAsync(default);

        CheckerState state = _stateStore.Load();
        Assert.Empty(_runner.Paths);
        Assert.Equal("continued-elsewhere", state.ProjectStatus![P("a")]);
        Assert.Equal("other-session", state.ProjectNotes![P("a")].Detail);
        Assert.Equal("u-a", state.ConsumedInterruptions![P("a")]);

        // 新会话被删(或某一拍读不到):旧会话尾部仍是那条中断，但它已经处理过，不能拿旧上下文去续。
        _world.TakenOver.Remove(P("a"));
        _clock = _clock.AddMinutes(5);
        await _engine.RunOnceAsync(default);
        Assert.Empty(_runner.Paths);
        if (continuous)
        {
            Assert.Equal("continued-elsewhere", _stateStore.Load().ProjectStatus![P("a")]);
        }
    }

    [Fact]
    public async Task 判断不了有没有别的会话接着干时只等待_取证与最终门禁都不放行()
    {
        Arm(Project("a", Opus));
        _world.Interrupt("a", "u-a");
        _world.Quota[Opus] = "limited";
        _world.ProbeChecksTakeover = true;
        await _engine.RunOnceAsync(default);

        _world.TakeoverUnknown.Add(P("a"));
        _world.Quota[Opus] = "ok";
        _clock = _clock.AddMinutes(5);
        await _engine.RunOnceAsync(default);
        Assert.Empty(_runner.Paths);
        Assert.Equal("evidence-unverified", _stateStore.Load().ProjectNotes![P("a")].Reason);

        // 取证那一刻还能判断，最终门禁那一刻判断不了:同样不启动、不锁周期。
        _world.ProbeChecksTakeover = false;
        _clock = _clock.AddMinutes(5);
        await _engine.RunOnceAsync(default);
        CheckerState state = _stateStore.Load();
        Assert.Empty(_runner.Paths);
        Assert.Equal("limited", state.ProjectStatus![P("a")]);
        Assert.Equal("not-started:evidence-unverified", state.ProjectNotes![P("a")].Reason);
        Assert.False(state.ReplayBlocked);
    }

    [Fact]
    public async Task 一跑就被限流时同模型的其它项目本轮一并跳过_别的模型照常()
    {
        Arm(Project("a", Opus), Project("b", Opus), Project("c", Fable));
        _world.Interrupt("a", "u-a");
        _world.Interrupt("b", "u-b");
        _world.Interrupt("c", "u-c");
        _world.Quota[Opus] = "ok";
        _world.Quota[Fable] = "ok";
        _runner.StatusByPath[P("a")] = "limited";

        await _engine.RunOnceAsync(default);

        Assert.Equal(new[] { P("a"), P("c") }, _runner.Paths);
        CheckerState state = _stateStore.Load();
        Assert.Equal("limited", state.ProjectStatus![P("b")]);
        Assert.False(state.ProjectLimitedRefires!.ContainsKey(P("b")));
    }

    private static string P(string name) => @"C:\proj\" + name;

    private static ProjectRef Project(string name, string model) => new()
    {
        Name = name, Path = P(name), SessionId = "00000000-0000-4000-8000-00000000000" + (name[0] - 'a'),
        ModelId = model, ModelName = model, ModelSource = "session",
    };

    private void Arm(params ProjectRef[] projects) => Arm(false, projects);

    private void Arm(bool continuous, params ProjectRef[] projects)
    {
        var config = ProductConfig.CreateDefault();
        config.Enabled = true;
        config.Armed = true;
        config.ArmCycleId = "cycle-" + Guid.NewGuid().ToString("N");
        config.Continuous = continuous;
        config.Selected = projects.ToList();
        config.ResumeTarget = new ResumeTarget
        {
            ProviderId = "claude-subscription", ProviderIdentity = "account-test", Agent = "claudecode",
            ModelId = Opus, ModelName = Opus, BoundAtUtc = _clock,
        };
        config.ResumeModel = Opus;
        _configStore.Save(config);
    }

    /// <summary>假的外部世界：每个模型的额度、每个会话的尾部证据与占用。探测、最终门禁与运行器看到同一份事实。</summary>
    private sealed class World : IProjectSessionInspector
    {
        public Dictionary<string, string> Quota { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, ClaudeSessionEvidence> Evidence { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, string> Session { get; } = new(StringComparer.OrdinalIgnoreCase);
        public bool Codex { get; set; }

        public void Interrupt(string name, string uuid, DateTimeOffset? resetsAt = null) =>
            Evidence[P(name)] = new ClaudeSessionEvidence("model", true, "five_hour", resetsAt,
                new DateTimeOffset(2026, 10, 30, 6, 0, 0, TimeSpan.Zero), uuid, "bypassPermissions");

        public void Clear(string path, string uuid) =>
            Evidence[path] = new ClaudeSessionEvidence("model", false, null, null, DateTimeOffset.UtcNow, uuid, "bypassPermissions");

        public ClaudeSessionEvidence? ReadEvidence(ProjectRef project) =>
            Evidence.TryGetValue(project.Path, out ClaudeSessionEvidence? evidence) ? evidence : null;

        public SessionOccupancy Occupancy(ProjectRef project) =>
            new(Session.TryGetValue(project.Path, out string? state) ? state : "idle", "claude-desktop");

        /// <summary>中断之后同目录新开的会话已经接着干了的项目。</summary>
        public HashSet<string> TakenOver { get; } = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>判断不了有没有新会话接着干的项目(目录读不了、疑似新会话读不出归属)。</summary>
        public HashSet<string> TakeoverUnknown { get; } = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>取证时也看「换会话接着干」(真实 ProviderResumeProbe 的行为);默认只有最终门禁看。</summary>
        public bool ProbeChecksTakeover { get; set; }

        public bool? ContinuedInAnotherSession(ProjectRef project, DateTimeOffset since) =>
            TakenOver.Contains(project.Path) ? true : TakeoverUnknown.Contains(project.Path) ? null : false;
    }

    private sealed class ScriptedProbe(World world) : IClaudeUsageProbe
    {
        public int CallCount { get; private set; }

        public Task<ClaudeProbeResult> ProbeAsync(string model, string workingDirectory, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<ClaudeProbeResult> ProbeAsync(ProductConfig config, string workingDirectory, CancellationToken cancellationToken = default)
        {
            CallCount++;
            var projects = config.Selected.Select(project =>
            {
                string model = ResumeTarget.EffectiveModel(project, config.ResumeTarget);
                ClaudeSessionEvidence? evidence = world.Codex ? null : world.ReadEvidence(project);
                bool limitRecord = evidence?.InterruptedByLimit == true;
                bool? continued = limitRecord && world.ProbeChecksTakeover
                    ? world.ContinuedInAnotherSession(project, evidence!.LastAssistantAtUtc ?? default)
                    : false;
                bool interrupted = limitRecord && continued == false;
                return new ProjectAdmission
                {
                    Path = project.Path,
                    ModelId = model,
                    Quota = world.Quota.TryGetValue(model, out string? quota) ? quota : "unknown",
                    Session = world.Codex ? "n/a" : world.Occupancy(project).State,
                    SessionClient = "claude-desktop",
                    Evidence = world.Codex ? "none" : evidence is null || continued is null ? "unverified"
                        : continued == true ? "superseded" : interrupted ? "interrupted" : "clear",
                    InterruptionId = limitRecord && continued is not null ? evidence!.LastAssistantUuid : null,
                    InterruptedAtUtc = interrupted ? evidence!.LastAssistantAtUtc : null,
                    LimitResetsAtUtc = interrupted ? evidence!.LimitResetsAtUtc : null,
                };
            }).ToArray();
            bool any = projects.Any(p => p.Admissible);
            return Task.FromResult(new ClaudeProbeResult { Ready = any, Reason = any ? "ok" : "unknown", Projects = projects });
        }
    }

    private sealed class RecordingRunner : IClaudeResumeRunner
    {
        public List<string> Paths { get; } = new();
        public List<string> Models { get; } = new();
        public Dictionary<string, string> NotStartedByPath { get; } = new(StringComparer.OrdinalIgnoreCase);
        /// <summary>已经 spawn 后返回的状态(默认 success);例如 limited = 额度说可用、一跑就被限流。</summary>
        public Dictionary<string, string> StatusByPath { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Action<ProjectRef>? BeforeGate { get; set; }
        public Action<ProjectRef>? OnRun { get; set; }
        /// <summary>监控循环询问 shouldContinue 的次数(真实运行器每隔几秒问一次)。</summary>
        public int ContinueChecks { get; set; } = 1;

        public Task<ResumeRunResult> RunAsync(ProjectRef project, ProductConfig config, CancellationToken cancellationToken,
            Func<RunId, bool>? beforeStart = null, Func<RunId, bool?>? shouldContinue = null)
        {
            if (NotStartedByPath.TryGetValue(project.Path, out string? reason))
                return Task.FromResult(new ResumeRunResult { ProjectPath = project.Path, Status = reason, NotStarted = true, NotStartedDetail = reason });
            BeforeGate?.Invoke(project);
            RunId runId = RunId.New();
            if (beforeStart is not null && !beforeStart(runId))
                return Task.FromResult(new ResumeRunResult { ProjectPath = project.Path, Status = "stopped", StopRound = true, NotStarted = true });
            Paths.Add(project.Path);
            Models.Add(ResumeTarget.EffectiveModel(project, config.ResumeTarget));
            for (int check = 0; check < ContinueChecks; check++)
                shouldContinue?.Invoke(runId);
            OnRun?.Invoke(project);
            string status = StatusByPath.TryGetValue(project.Path, out string? scripted) ? scripted : "success";
            return Task.FromResult(new ResumeRunResult { ProjectPath = project.Path, Status = status, ResultOk = status == "success", RunId = runId });
        }
    }

    private sealed class RecordingNotices : IResumeNotices
    {
        public List<ResumeNotice> Items { get; } = new();

        /// <summary>接下来这么多次写入抛 IOException(杀软占用、磁盘满)。</summary>
        public int FailNext { get; set; }

        public void Post(ResumeNotice notice)
        {
            if (FailNext > 0)
            {
                FailNext--;
                throw new IOException("模拟写入失败");
            }

            Items.Add(notice);
        }
    }
}
