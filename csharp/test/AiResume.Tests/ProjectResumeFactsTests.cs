using System.Text.Json;
using AiResume.Core;
using AiResume.Worker.Probes;
using AiResume.Worker.Quota;
using AiResume.Worker.Resume;
using Xunit;

namespace AiResume.Tests;

/// <summary>
/// 逐项目续跑依赖的底层事实：会话占用(procStart 防 PID 复用、同目录正在运行)、会话文件定位、
/// 额度承接规则、逐项目模型选择、一次取证服务多个模型、续跑提醒写入去重。
/// 会话登记与会话文件都写在 TestTemp 临时目录，按 2.1.309 真实字段形状(值为合成数据)。
/// </summary>
public sealed class ProjectResumeFactsTests
{
    private const string Session = "11111111-2222-4333-8444-555555555555";

    [Fact]
    public void 绑定会话进程活着且启动时间一致才算占用_PID被复用时视为空闲()
    {
        string root = TestTemp.NewDir("occupancy");
        string project = TestTemp.NewDir("occupancy-project");
        WriteRegistry(root, 4242, Session, project, procStart: "134378287473914826", status: "idle");
        // 复用:活着的进程比登记的那个 Claude 进程晚一小时启动。
        var reused = new ResumeSessionCatalog(Path.Combine(root, "projects"), TestTemp.NewPath("codex"), _ => true,
            _ => 134378287473914826 + TimeSpan.TicksPerHour);
        var same = new ResumeSessionCatalog(Path.Combine(root, "projects"), TestTemp.NewPath("codex"), _ => true, _ => 134378287473914826);
        // 比登记的启动时间还早:不可能是复用，是字段语义变了,判断不了——不能当成空闲。
        var contradictory = new ResumeSessionCatalog(Path.Combine(root, "projects"), TestTemp.NewPath("codex"), _ => true,
            _ => 134378287473914826 - TimeSpan.TicksPerHour);

        Assert.Equal("idle", reused.Occupancy(new ProjectRef { Path = project, SessionId = Session }).State);
        SessionOccupancy occupied = same.Occupancy(new ProjectRef { Path = project, SessionId = Session });
        Assert.Equal("in-use", occupied.State);
        Assert.Equal("claude-desktop", occupied.Client);
        Assert.Equal("unverified", contradictory.Occupancy(new ProjectRef { Path = project, SessionId = Session }).State);
    }

    [Fact]
    public void procStart格式变了也不会把开着的会话当成空闲()
    {
        string root = TestTemp.NewDir("occupancy-drift");
        string project = TestTemp.NewDir("occupancy-drift-project");
        // 假如上游把 procStart 改成毫秒时间戳:不是合理的 FILETIME,退回 startedAt(登记时刻,1761800000000 ms)判断。
        WriteRegistry(root, 4343, Session, project, procStart: "1761799990000", status: "idle");
        long registeredAt = DateTimeOffset.FromUnixTimeMilliseconds(1761800000000).UtcDateTime.ToFileTimeUtc();
        var startedBefore = new ResumeSessionCatalog(Path.Combine(root, "projects"), TestTemp.NewPath("codex"), _ => true,
            _ => registeredAt - 10 * TimeSpan.TicksPerSecond);
        var startedAfter = new ResumeSessionCatalog(Path.Combine(root, "projects"), TestTemp.NewPath("codex"), _ => true,
            _ => registeredAt + TimeSpan.TicksPerHour);

        Assert.Equal("in-use", startedBefore.Occupancy(new ProjectRef { Path = project, SessionId = Session }).State);
        Assert.Equal("idle", startedAfter.Occupancy(new ProjectRef { Path = project, SessionId = Session }).State);
    }

    [Fact]
    public void 同目录另一个正在运行的会话也算占用_空闲的不算()
    {
        string root = TestTemp.NewDir("occupancy-cwd");
        string project = TestTemp.NewDir("occupancy-cwd-project");
        WriteRegistry(root, 5151, Guid.NewGuid().ToString(), project, procStart: null, status: "busy");
        var busy = new ResumeSessionCatalog(Path.Combine(root, "projects"), TestTemp.NewPath("codex"), _ => true);
        Assert.Equal("cwd-busy", busy.Occupancy(new ProjectRef { Path = project, SessionId = Session }).State);

        WriteRegistry(root, 5151, Guid.NewGuid().ToString(), project, procStart: null, status: "idle");
        Assert.Equal("idle", busy.Occupancy(new ProjectRef { Path = project, SessionId = Session }).State);
        Assert.Equal("idle", busy.Occupancy(new ProjectRef { Path = TestTemp.NewDir("other"), SessionId = Session }).State);
    }

    [Fact]
    public void 登记缺PID或存活无法判定时占用状态未核实()
    {
        string root = TestTemp.NewDir("occupancy-unknown");
        string project = TestTemp.NewDir("occupancy-unknown-project");
        WriteRegistry(root, 6262, Session, project, procStart: null, status: "idle");
        var unknown = new ResumeSessionCatalog(Path.Combine(root, "projects"), TestTemp.NewPath("codex"), _ => null);
        Assert.Equal("unverified", unknown.Occupancy(new ProjectRef { Path = project, SessionId = Session }).State);
        Assert.Null(unknown.IsClaudeSessionIdle(Session));
    }

    [Fact]
    public void 按会话UUID定位会话文件并读出限额中断证据_归属目录不符时不读()
    {
        string root = TestTemp.NewDir("transcript");
        string project = TestTemp.NewDir("transcript-project");
        string folder = Path.Combine(root, "projects", "C--encoded-project");
        Directory.CreateDirectory(folder);
        File.WriteAllLines(Path.Combine(folder, Session + ".jsonl"), new[]
        {
            JsonSerializer.Serialize(new { type = "user", cwd = project, sessionId = Session, permissionMode = "bypassPermissions", uuid = "u1" }),
            JsonSerializer.Serialize(new { type = "assistant", cwd = project, uuid = "a1", timestamp = "2026-10-29T23:10:00Z", message = new { model = "claude-opus-5-5" } }),
            JsonSerializer.Serialize(new { type = "assistant", cwd = project, uuid = "rl-1", timestamp = "2026-10-29T23:12:00Z", isApiErrorMessage = true, error = "rate_limit",
                quotaLimits = new { rateLimitType = "five_hour", resetsAt = 1761810000 }, message = new { model = "<synthetic>" } }),
        });
        var catalog = new ResumeSessionCatalog(Path.Combine(root, "projects"), TestTemp.NewPath("codex"), _ => false);

        ClaudeSessionEvidence evidence = catalog.ReadEvidence(new ProjectRef { Path = project, SessionId = Session })!;
        Assert.True(evidence.InterruptedByLimit);
        Assert.Equal("rl-1", evidence.LastAssistantUuid);
        Assert.Equal("claude-opus-5-5", evidence.LastModel);
        Assert.Null(catalog.ReadEvidence(new ProjectRef { Path = TestTemp.NewDir("not-owner"), SessionId = Session }));
    }

    [Fact]
    public void 中断之后同目录新开的会话有真实应答才算接着干了_早就开着的并行会话不算()
    {
        string root = TestTemp.NewDir("takeover");
        string project = TestTemp.NewDir("takeover-project");
        string folder = Path.Combine(root, "projects", "C--encoded-project");
        Directory.CreateDirectory(folder);
        DateTimeOffset interruptedAt = DateTimeOffset.Parse("2026-10-29T23:12:00Z");
        string Line(object row) => JsonSerializer.Serialize(row);
        // 文件创建时间照真实情况给:会话文件在它第一条记录那一刻创建。
        void Write(string id, DateTimeOffset createdAt, params object[] rows)
        {
            string file = Path.Combine(folder, id + ".jsonl");
            File.WriteAllLines(file, rows.Select(Line));
            File.SetCreationTimeUtc(file, createdAt.UtcDateTime);
            File.SetLastWriteTimeUtc(file, interruptedAt.UtcDateTime.AddHours(2));
        }

        var catalog = new ResumeSessionCatalog(Path.Combine(root, "projects"), TestTemp.NewPath("codex"), _ => false);
        var bound = new ProjectRef { Path = project, SessionId = Session };
        Write(Session, DateTimeOffset.Parse("2026-10-29T23:00:00Z"),
            new { type = "user", cwd = project, sessionId = Session, uuid = "u1", timestamp = "2026-10-29T23:00:00Z" },
            new { type = "assistant", cwd = project, uuid = "rl-1", timestamp = "2026-10-29T23:12:00Z", isApiErrorMessage = true, error = "rate_limit", message = new { model = "<synthetic>" } });

        // 中断之前就开着的并行会话,中断之后还在干活:是用户本来的分工,不算接管。
        string parallel = "22222222-2222-4222-8222-222222222222";
        Write(parallel, DateTimeOffset.Parse("2026-10-29T22:00:00Z"),
            new { type = "user", cwd = project, sessionId = parallel, uuid = "p1", timestamp = "2026-10-29T22:00:00Z" },
            new { type = "assistant", cwd = project, uuid = "p2", timestamp = "2026-10-30T00:30:00Z", message = new { model = "claude-sonnet-4-5" } });
        Assert.False(catalog.ContinuedInAnotherSession(bound, interruptedAt));

        // 中断之后新开、只有合成错误(没真正跑起来):不算。
        string failed = "33333333-3333-4333-8333-333333333333";
        Write(failed, DateTimeOffset.Parse("2026-10-30T00:00:00Z"),
            new { type = "user", cwd = project, sessionId = failed, uuid = "f1", timestamp = "2026-10-30T00:00:00Z" },
            new { type = "assistant", cwd = project, uuid = "f2", timestamp = "2026-10-30T00:00:05Z", isApiErrorMessage = true, error = "server_error", message = new { model = "<synthetic>" } });
        Assert.False(catalog.ContinuedInAnotherSession(bound, interruptedAt));

        // 中断之后新开、有真实应答:用户换会话接着干了。
        string takeover = "44444444-4444-4444-8444-444444444444";
        Write(takeover, DateTimeOffset.Parse("2026-10-30T00:10:00Z"),
            new { type = "user", cwd = project, sessionId = takeover, uuid = "t1", timestamp = "2026-10-30T00:10:00Z" },
            new { type = "assistant", cwd = project, uuid = "t2", timestamp = "2026-10-30T00:10:30Z", message = new { model = "claude-opus-5-5" } });
        Assert.True(catalog.ContinuedInAnotherSession(bound, interruptedAt));
    }

    [Fact]
    public void 分叉续写和首条提问贴了大图的新会话都算接着干了_读不出归属的新文件判断不了()
    {
        string root = TestTemp.NewDir("takeover-edge");
        string project = TestTemp.NewDir("takeover-edge-project");
        string folder = Path.Combine(root, "projects", "C--encoded-project");
        Directory.CreateDirectory(folder);
        DateTimeOffset interruptedAt = DateTimeOffset.Parse("2026-10-29T23:12:00Z");
        void Write(string id, DateTimeOffset createdAt, params string[] lines)
        {
            string file = Path.Combine(folder, id + ".jsonl");
            File.WriteAllLines(file, lines);
            File.SetCreationTimeUtc(file, createdAt.UtcDateTime);
            File.SetLastWriteTimeUtc(file, interruptedAt.UtcDateTime.AddHours(2));
        }

        var bound = new ProjectRef { Path = project, SessionId = Session };
        // 绑定会话的首条提问贴了一张图(base64 内联,单行远超 64 KiB),message 排在 cwd 之前——照录制形状的字段顺序。
        string image = new('A', 300_000);
        Write(Session, DateTimeOffset.Parse("2026-10-29T23:00:00Z"),
            JsonSerializer.Serialize(new { type = "user", message = new { role = "user", content = new object[] { new { type = "image", source = new { type = "base64", data = image } } } },
                cwd = project, sessionId = Session, uuid = "u1", timestamp = "2026-10-29T23:00:00Z" }),
            JsonSerializer.Serialize(new { type = "assistant", cwd = project, uuid = "rl-1", timestamp = "2026-10-29T23:12:00Z", isApiErrorMessage = true, error = "rate_limit", message = new { model = "<synthetic>" } }));
        var catalog = new ResumeSessionCatalog(Path.Combine(root, "projects"), TestTemp.NewPath("codex"), _ => false);

        Assert.Equal("rl-1", catalog.ReadEvidence(bound)?.LastAssistantUuid);
        Assert.Equal(Session, catalog.BindAvailable("claudecode", new[] { project })[Path.GetFullPath(project)].SessionId);
        Assert.False(catalog.ContinuedInAnotherSession(bound, interruptedAt));

        // 分叉续写:新文件在中断之后创建，开头复制了旧记录(首条时间早于中断),之后有真实应答。
        string fork = "55555555-5555-4555-8555-555555555555";
        Write(fork, DateTimeOffset.Parse("2026-10-30T00:05:00Z"),
            JsonSerializer.Serialize(new { type = "user", cwd = project, sessionId = fork, uuid = "u1", timestamp = "2026-10-29T23:00:00Z" }),
            JsonSerializer.Serialize(new { type = "assistant", cwd = project, uuid = "k2", timestamp = "2026-10-30T00:06:00Z", message = new { model = "claude-opus-5-5" } }));
        Assert.True(catalog.ContinuedInAnotherSession(bound, interruptedAt));
        File.Delete(Path.Combine(folder, fork + ".jsonl"));

        // 中断之后改过、按 UUID 命名、却读不出归属目录的会话文件:可能就是接着干的那个，判断不了。
        string unreadable = "66666666-6666-4666-8666-666666666666";
        Write(unreadable, DateTimeOffset.Parse("2026-10-30T00:05:00Z"), "{\"type\":\"user\",\"message\":");
        Assert.Null(catalog.ContinuedInAnotherSession(bound, interruptedAt));
    }

    [Fact]
    public void 单个项目的执行身份不受队列里别的项目增删和改模型影响()
    {
        var config = ProductConfig.CreateDefault();
        config.ResumeTarget = new ResumeTarget { ProviderId = "claude-subscription", ProviderIdentity = "acct", Agent = "claudecode", ModelId = "claude-opus-5-5" };
        config.ResumeModel = "claude-opus-5-5";
        config.Selected = new List<ProjectRef>
        {
            new() { Path = @"C:\demo\a", SessionId = Session, ModelId = "claude-opus-5-5" },
            new() { Path = @"C:\demo\b", SessionId = Session, ModelId = "claude-fable-5-1" },
        };
        string before = ResumeTarget.ProjectIdentity(config, @"C:\demo\a");

        config.Selected.RemoveAt(1);
        Assert.Equal(before, ResumeTarget.ProjectIdentity(config, @"c:\DEMO\a\"));
        config.Selected.Add(new ProjectRef { Path = @"C:\demo\c", SessionId = Session, ModelId = "claude-fable-5-1" });
        Assert.Equal(before, ResumeTarget.ProjectIdentity(config, @"C:\demo\a"));

        config.Selected[0].ModelId = "claude-fable-5-1";
        Assert.NotEqual(before, ResumeTarget.ProjectIdentity(config, @"C:\demo\a"));
        Assert.Equal(string.Empty, ResumeTarget.ProjectIdentity(config, @"C:\demo\gone"));
    }

    [Fact]
    public void 相关Scoped被本次响应省略且未满时不再阻塞_满额的承接值仍判受限()
    {
        DateTimeOffset now = DateTimeOffset.Parse("2026-10-30T08:00:00Z");
        var probe = new QuotaResumeProbe(new QuotaService(probe: _ => Task.FromResult(new ClaudeProbeResult())));
        UsageSnapshot carriedOpen = Snapshot(Window("five_hour", 20, now.AddHours(3)), Window("seven_day", 50, now.AddDays(3)),
            Window("weekly_scoped:Fable", 60, now.AddDays(3)) with { CarriedForward = true });
        UsageSnapshot carriedFull = Snapshot(Window("five_hour", 20, now.AddHours(3)), Window("seven_day", 50, now.AddDays(3)),
            Window("weekly_scoped:Fable", 100, now.AddDays(2)) with { CarriedForward = true });

        Assert.True(probe.Evaluate(carriedOpen, "claude-fable-5-1").Ready);
        ClaudeProbeResult limited = probe.Evaluate(carriedFull, "claude-fable-5-1");
        Assert.True(limited.IsLimited);
        Assert.Equal(now.AddDays(2).ToUnixTimeSeconds(), limited.BlockingResetUtc?.ToUnixTimeSeconds());
        Assert.True(probe.Evaluate(carriedFull, "claude-opus-5-5").Ready);
    }

    [Fact]
    public void 挡住目标的窗口取最晚的重置时间()
    {
        DateTimeOffset now = DateTimeOffset.Parse("2026-10-30T08:00:00Z");
        var probe = new QuotaResumeProbe(new QuotaService(probe: _ => Task.FromResult(new ClaudeProbeResult())));
        ClaudeProbeResult result = probe.Evaluate(
            Snapshot(Window("five_hour", 100, now.AddHours(2)), Window("seven_day", 100, now.AddDays(4))), "claude-opus-5-5");

        Assert.True(result.IsLimited);
        Assert.Equal(now.AddDays(4).ToUnixTimeSeconds(), result.BlockingResetUtc?.ToUnixTimeSeconds());
        Assert.Equal("five_hour:blocked,seven_day:blocked", result.Detail);
    }

    [Fact]
    public void 项目模型选择_显式选择必须在目录_未选时跟随会话_不在目录退回默认()
    {
        var catalog = new ProviderModelCatalog(new[] { new ProviderModel("claude-opus-5-5", "Claude Opus 5.5"), new ProviderModel("claude-fable-5-1", "Claude Fable 5.1") },
            true, "测试目录", DateTimeOffset.UtcNow);
        var target = new ResumeTarget { ModelId = "claude-opus-5-5", ModelName = "Claude Opus 5.5" };

        Assert.Equal(("claude-fable-5-1", "user"), Pick(ResumeBindingService.ChooseModel("a", "claude-fable-5-1", "claude-opus-5-5", catalog, target)));
        Assert.Equal(("claude-fable-5-1", "session"), Pick(ResumeBindingService.ChooseModel("a", null, "claude-fable-5-1", catalog, target)));
        Assert.Equal(("claude-opus-5-5", "default"), Pick(ResumeBindingService.ChooseModel("a", "", "claude-retired-4", catalog, target)));
        Assert.Equal(("claude-opus-5-5", "default"), Pick(ResumeBindingService.ChooseModel("a", null, null, catalog, target)));
        Assert.Throws<InvalidOperationException>(() => ResumeBindingService.ChooseModel("a", "claude-unknown-9", null, catalog, target));

        static (string, string) Pick((ProviderModel Model, string Source) choice) => (choice.Model.Id, choice.Source);
    }

    [Fact]
    public void 会话模型认不出模型族时只说这一个原因_不再多一句不在目录()
    {
        var catalog = new ProviderModelCatalog(new[] { new ProviderModel("claude-opus-5-5", "Claude Opus 5.5"), new ProviderModel("mystery-model-1", "Mystery 1") },
            true, "测试目录", DateTimeOffset.UtcNow);
        var target = new ResumeTarget { ProviderId = "claude-subscription", ModelId = "claude-opus-5-5", ModelName = "Claude Opus 5.5" };

        var warnings = new List<string>();
        (ProviderModel model, string source) = ResumeBindingService.ResolveProjectModel("a", null, "mystery-model-1", catalog, target, warnings);
        Assert.Equal(("claude-opus-5-5", "default"), (model.Id, source));
        string warning = Assert.Single(warnings);
        Assert.Contains("认不出模型族", warning);

        // 会话模型真的不在目录:只说「不在当前目录」。
        warnings.Clear();
        ResumeBindingService.ResolveProjectModel("a", null, "claude-retired-4", catalog, target, warnings);
        Assert.Contains("不在当前目录", Assert.Single(warnings));

        // 手选的认不出模型族:拒绝布防。
        Assert.Throws<InvalidOperationException>(() =>
            ResumeBindingService.ResolveProjectModel("a", "mystery-model-1", null, catalog, target, new List<string>()));
    }

    [Fact]
    public async Task 多个项目多个模型只做一次供应商观测_逐项目给出额度结论()
    {
        int observations = 0;
        var registry = new ProviderRegistry(environment: _ => null, userProfile: TestTemp.NewDir("facts-profile"),
            codex: () => new("https://test.invalid/v1", "test-only-token", "m1", "responses", new Dictionary<string, string>(),
                new Dictionary<string, string>(), "test", null, null, "relay", false, false));
        var service = new ProviderCatalogService(registry, observe: (p, _) =>
        {
            observations++;
            return Task.FromResult(new ProviderObservation(p.Id, p.Name, p.Agent, p.Kind, p.Identity,
                new(new[] { new ProviderModel("m1", "m1"), new ProviderModel("m2", "m2") }, true, "测试", DateTimeOffset.UtcNow),
                5, "USD", true, false, null));
        });
        ProviderConnection connection = registry.Find("codex-active")!;
        var config = ProductConfig.CreateDefault();
        config.ResumeTarget = new ResumeTarget { ProviderId = connection.Id, ProviderIdentity = connection.Identity, Agent = "codex", ModelId = "m1", ModelName = "m1" };
        config.ResumeModel = "m1";
        config.Selected = new List<ProjectRef>
        {
            new() { Path = @"C:\demo\a", SessionId = Session, ModelId = "m1" },
            new() { Path = @"C:\demo\b", SessionId = Session, ModelId = "m2" },
            new() { Path = @"C:\demo\c", SessionId = Session, ModelId = "m3" },
            new() { Path = @"C:\demo\d", SessionId = Session },
        };
        var probe = new ProviderResumeProbe(service, new ResumeSessionCatalog(TestTemp.NewPath("claude"), TestTemp.NewPath("codex")));

        ClaudeProbeResult result = await probe.ProbeAsync(config, "", default);

        Assert.Equal(1, observations);
        Assert.Equal(new[] { "ok", "ok", "model-unverified", "ok" }, result.Projects!.Select(p => p.Quota));
        // 目录读出来了、模型确实不在 ≠ 目录这次没读出来:界面据此说「不在当前目录」还是「模型目录未核实」。
        Assert.Equal("absent", result.Projects![2].QuotaDetail);
        Assert.Equal(new[] { "m1", "m2", "m3", "m1" }, result.Projects!.Select(p => p.ModelId));
        Assert.All(result.Projects!, p => Assert.Equal("none", p.Evidence));
        Assert.All(result.Projects!, p => Assert.Equal("n/a", p.Session));

        var unverified = new ProviderCatalogService(registry, observe: (p, _) => Task.FromResult(new ProviderObservation(p.Id, p.Name, p.Agent, p.Kind, p.Identity,
            ProviderModelCatalog.Unavailable("HTTP 429"), null, null, false, false, null)));
        ClaudeProbeResult unknown = await new ProviderResumeProbe(unverified, new ResumeSessionCatalog(TestTemp.NewPath("claude"), TestTemp.NewPath("codex")))
            .ProbeAsync(config, "", default);
        Assert.All(unknown.Projects!, p => Assert.Equal(("model-unverified", "catalog-unverified"), (p.Quota, p.QuotaDetail)));
    }

    [Fact]
    public void 续跑提醒按稳定键去重_写入完成通知队列的标准字段()
    {
        string events = TestTemp.NewPath("resume-events");
        var writer = new ResumeNoticeWriter(events, () => DateTimeOffset.Parse("2026-10-30T08:00:00Z"));
        var notice = new ResumeNotice("session-open", @"C:\demo\ledger-api", "cycle|path|u1|session-open", "claude-opus-5-5", "claude-desktop");

        writer.Post(notice);
        writer.Post(notice);

        string file = Assert.Single(Directory.GetFiles(events, "*.json"));
        Assert.Equal(ResumeNoticeWriter.EventIdFor(notice) + ".json", Path.GetFileName(file));
        using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(file));
        JsonElement root = doc.RootElement;
        Assert.Equal("ai-resume", root.GetProperty("source").GetString());
        Assert.Equal("resume", root.GetProperty("kind").GetString());
        Assert.Equal("session-open", root.GetProperty("notificationType").GetString());
        Assert.Equal(@"C:\demo\ledger-api", root.GetProperty("cwd").GetString());
        Assert.Equal(16, root.GetProperty("eventId").GetString()!.Length);
        Assert.NotEqual(ResumeNoticeWriter.EventIdFor(notice), ResumeNoticeWriter.EventIdFor(notice with { DedupeKey = "cycle|path|u2|session-open" }));
    }

    private static void WriteRegistry(string root, int pid, string sessionId, string cwd, string? procStart, string status)
    {
        string registry = Path.Combine(root, "sessions");
        Directory.CreateDirectory(registry);
        var row = new Dictionary<string, object?>
        {
            ["pid"] = pid, ["sessionId"] = sessionId, ["cwd"] = cwd, ["startedAt"] = 1761800000000, ["version"] = "2.1.309",
            ["kind"] = "interactive", ["entrypoint"] = "claude-desktop", ["status"] = status,
        };
        if (procStart is not null) row["procStart"] = procStart;
        File.WriteAllText(Path.Combine(registry, pid + ".json"), JsonSerializer.Serialize(row));
    }

    private static UsageSnapshot Snapshot(params UsageWindow[] windows) => new(
        "claudecode", DateTimeOffset.UtcNow,
        new[] { new UsageBucket("Usage", !windows.Any(w => w.UsedPercent >= 100), windows.Any(w => w.UsedPercent >= 100), windows) },
        null) { EvidenceSource = UsageEvidenceSource.OAuth };

    private static UsageWindow Window(string name, int usedPercent, DateTimeOffset reset) => new(
        name, usedPercent >= 100 ? "blocked" : "allowed",
        name == "five_hour" ? UsageWindow.FiveHourSeconds : UsageWindow.SevenDaySeconds,
        reset.ToUnixTimeSeconds(), null, usedPercent);
}
