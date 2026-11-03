using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using AiResume.Core;
using AiResume.Worker.BudgetGate;
using AiResume.Worker.Products;
using AiResume.Worker.Quota;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AiResume.Tests;

/// <summary>
/// 子代理额度预检(ADR-0008)的控制面:settings.json 我方条目、模式意图与 config.json、OAuth 快照导出、证据汇总、清理。
/// 全部落在 TestTemp 里;不碰真实 ~/.claude 与 %LOCALAPPDATA%\AI Resume\state,不启动任何 AI 运行。
/// </summary>
public sealed class BudgetGateTests
{
    private const string Ours = @"C:\Install\AI Resume\mods\airesume-budget-gate";

    // ---------- settings.json 里的我方条目 ----------

    [Fact]
    public void 只增删我方条目_别人的条目与其它键原样保留_开发期旧条目被替换()
    {
        string settings = TestTemp.NewFile("bg-settings", ".json");
        File.WriteAllText(settings, """
            {
              "model": "opus",
              "hooks": { "Stop": [ { "hooks": [ { "type": "command", "command": "x.exe" } ] } ] },
              "env": {
                "OTHER": "keep-me",
                "CLAUDE_CODE_PLUGIN_DIRS": "C:\\Other\\mod-a;C:\\Dev\\budget-gate\\mod\\airesume-budget-gate"
              }
            }
            """);
        var dirs = new ClaudePluginDirs(settings);

        Assert.True(dirs.Apply(Ours, enable: true));
        JsonObject root = Read(settings);
        Assert.Equal("opus", root["model"]!.GetValue<string>());
        Assert.NotNull(root["hooks"]!["Stop"]);
        Assert.Equal("keep-me", root["env"]!["OTHER"]!.GetValue<string>());
        Assert.Equal(@"C:\Other\mod-a;" + Ours, root["env"]![ClaudePluginDirs.EnvKey]!.GetValue<string>());
        PluginDirsStatus status = dirs.Inspect(Ours);
        Assert.True(status.IsExact);
        Assert.Equal(new[] { @"C:\Other\mod-a" }, status.Others);
        Assert.False(dirs.Apply(Ours, enable: true), "已对齐时不再写文件");

        Assert.True(dirs.Apply(Ours, enable: false));
        root = Read(settings);
        Assert.Equal(@"C:\Other\mod-a", root["env"]![ClaudePluginDirs.EnvKey]!.GetValue<string>());
        Assert.Equal("keep-me", root["env"]!["OTHER"]!.GetValue<string>());
        Assert.False(dirs.Apply(Ours, enable: false));
        Assert.False(dirs.Inspect(Ours).IsExact);
    }

    [Fact]
    public void 移除后env空了连env一起删_settings不存在时新建只含我方键()
    {
        string settings = TestTemp.NewFile("bg-settings", ".json");
        File.WriteAllText(settings, $$"""{ "model": "x", "env": { "{{ClaudePluginDirs.EnvKey}}": "{{Ours.Replace("\\", "\\\\")}}" } }""");
        new ClaudePluginDirs(settings).Apply(Ours, enable: false);
        Assert.Null(Read(settings)["env"]);

        string fresh = Path.Combine(TestTemp.NewDir("bg-settings-dir"), "settings.json");
        new ClaudePluginDirs(fresh).Apply(Ours, enable: true);
        Assert.Equal(Ours, Read(fresh)["env"]![ClaudePluginDirs.EnvKey]!.GetValue<string>());
    }

    [Fact]
    public void 坏JSON不覆盖用户文件_读不出时现状未核实()
    {
        string settings = TestTemp.NewFile("bg-settings", ".json");
        File.WriteAllText(settings, "{ not json");
        var dirs = new ClaudePluginDirs(settings);
        Assert.ThrowsAny<Exception>(() => dirs.Apply(Ours, enable: true));
        Assert.Equal("{ not json", File.ReadAllText(settings));
        Assert.True(dirs.Inspect(Ours).Unverified);
    }

    [Fact]
    public void 非字符串值不让读取抛错_写回时由我方条目覆盖()
    {
        string settings = TestTemp.NewFile("bg-settings", ".json");
        File.WriteAllText(settings, $$"""{ "env": { "{{ClaudePluginDirs.EnvKey}}": 42 } }""");
        var dirs = new ClaudePluginDirs(settings);
        Assert.False(dirs.Inspect(Ours).Unverified);
        dirs.Apply(Ours, enable: true);
        Assert.True(dirs.Inspect(Ours).IsExact);
    }

    // ---------- 意图、config.json 与 install 对账 ----------

    private sealed record Rig(string Root, ProductConfigStore Store, BudgetGatePaths Paths, ClaudePluginDirs Env, BudgetGateControl Control, string Settings);

    /// <summary>没有任何托管策略来源:测试不读本机的 Program Files 与注册表。</summary>
    private static SideloadPolicy NoPolicy() => new(() => Array.Empty<string>(), () => Array.Empty<string?>());

    private static SideloadPolicy PolicyFile(string json)
    {
        string path = TestTemp.NewFile("bg-managed-settings", ".json");
        File.WriteAllText(path, json);
        return new SideloadPolicy(() => [path], () => Array.Empty<string?>());
    }

    private static Rig NewRig(bool withMod = true, SideloadPolicy? policy = null)
    {
        string root = TestTemp.NewDir("bg-rig");
        string modDir = Path.Combine(root, "install", "mods", BudgetGatePaths.ModName);
        if (withMod)
        {
            foreach (string file in BudgetGatePaths.RequiredModFiles)
            {
                string path = Path.Combine(modDir, file);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, file.EndsWith("plugin.json", StringComparison.Ordinal) ? """{ "name": "airesume-budget-gate", "version": "9.9.9" }""" : "// stub");
            }
        }
        string state = Path.Combine(root, "state");
        Directory.CreateDirectory(state);
        var store = new ProductConfigStore(state);
        var paths = new BudgetGatePaths(modDir, BudgetGatePaths.DataDirUnder(state));
        string settings = Path.Combine(root, "claude", "settings.json");
        var env = new ClaudePluginDirs(settings);
        return new Rig(root, store, paths, env, new BudgetGateControl(store, paths, env, policy ?? NoPolicy()), settings);
    }

    [Fact]
    public void 对账_旧配置补默认影子模式_写config并登记_读回一致()
    {
        Rig rig = NewRig();
        BudgetGateReconcile result = rig.Control.Reconcile(rig.Paths.ModDir);
        Assert.True(result.Ok, result.Detail);
        ProductConfig config = rig.Store.Load();
        Assert.Equal("shadow", config.BudgetGateMode);
        Assert.Equal(1, config.BudgetGateConfigVersion);
        JsonObject written = Read(rig.Paths.ConfigFile);
        Assert.Equal("shadow", written["mode"]!.GetValue<string>());
        Assert.Equal(1, written["version"]!.GetValue<int>());
        Assert.True(rig.Env.Inspect(rig.Paths.ModDir).IsExact);
        Assert.Equal("9.9.9", BudgetGateControl.ModVersion(rig.Paths.ModDir));

        // 再对账一次:意图与版本不变(install 重跑不制造新版本)
        Assert.True(rig.Control.Reconcile(rig.Paths.ModDir).Ok);
        Assert.Equal(1, rig.Store.Load().BudgetGateConfigVersion);
    }

    [Fact]
    public void 对账_保留用户意图_启用拦截不会被install改回()
    {
        Rig rig = NewRig();
        rig.Store.Update(c =>
        {
            c.BudgetGateMode = "enforce";
            c.BudgetGateConfigVersion = 5;
        });
        Assert.True(rig.Control.Reconcile(rig.Paths.ModDir).Ok);
        JsonObject written = Read(rig.Paths.ConfigFile);
        Assert.Equal("enforce", written["mode"]!.GetValue<string>());
        Assert.Equal(5, written["version"]!.GetValue<int>());
    }

    [Fact]
    public void 对账_mod文件不全时不登记并报失败()
    {
        Rig rig = NewRig(withMod: false);
        BudgetGateReconcile result = rig.Control.Reconcile(rig.Paths.ModDir);
        Assert.False(result.Ok);
        Assert.Contains("mod 文件不全", result.Detail, StringComparison.Ordinal);
        Assert.False(File.Exists(rig.Settings));
    }

    [Fact]
    public void 切换模式_版本递增_config跟上_未知模式拒绝_退役只删登记()
    {
        Rig rig = NewRig();
        Assert.True(rig.Control.Reconcile(rig.Paths.ModDir).Ok);
        Assert.Equal(2, rig.Control.SetMode("observe"));
        Assert.Equal("observe", Read(rig.Paths.ConfigFile)["mode"]!.GetValue<string>());
        Assert.Equal(2, rig.Control.ReadConfigVersion());
        Assert.Throws<ArgumentException>(() => rig.Control.SetMode("turbo"));
        Assert.Equal(2, rig.Store.Load().BudgetGateConfigVersion);

        Assert.True(rig.Control.Retire());
        Assert.False(rig.Env.Inspect(rig.Paths.ModDir).IsExact);
        Assert.Equal("observe", rig.Store.Load().BudgetGateMode);
    }

    [Fact]
    public void 对账_建好数据子目录与盐_重跑不换盐()
    {
        Rig rig = NewRig();
        Assert.True(rig.Control.Reconcile(rig.Paths.ModDir).Ok);
        Assert.True(Directory.Exists(rig.Paths.SessionsDir));
        Assert.True(Directory.Exists(rig.Paths.LogsDir));
        string salt = Path.Combine(rig.Paths.DataDir, "salt");
        string first = File.ReadAllText(salt);
        Assert.Matches("^[0-9a-f]{32}$", first);
        Assert.True(rig.Control.Reconcile(rig.Paths.ModDir).Ok);
        Assert.Equal(first, File.ReadAllText(salt));
    }

    [Fact]
    public void 已关闭即撤下登记_对账也不再登记_再切回影子重新登记()
    {
        Rig rig = NewRig();
        Assert.True(rig.Control.Reconcile(rig.Paths.ModDir).Ok);
        Assert.True(rig.Env.Inspect(rig.Paths.ModDir).IsExact);

        rig.Control.SetMode("off");
        Assert.Empty(rig.Env.Inspect(rig.Paths.ModDir).Ours);
        Assert.Equal("off", Read(rig.Paths.ConfigFile)["mode"]!.GetValue<string>());
        BudgetGateReconcile again = rig.Control.Reconcile(rig.Paths.ModDir);
        Assert.True(again.Ok, again.Detail);
        Assert.Empty(rig.Env.Inspect(rig.Paths.ModDir).Ours);

        rig.Control.SetMode("shadow");
        Assert.True(rig.Env.Inspect(rig.Paths.ModDir).IsExact);
    }

    [Theory]
    [InlineData("""{ "disableSideloadFlags": true }""")]
    [InlineData("{ not json")]
    public void 组织策略禁止旁加载或读不懂时_不登记_已有登记被撤下_对账报失败(string policyJson)
    {
        Rig rig = NewRig(policy: PolicyFile(policyJson));
        // 之前(策略生效前)已经登记过
        rig.Env.Apply(rig.Paths.ModDir, enable: true);
        BudgetGateReconcile result = rig.Control.Reconcile(rig.Paths.ModDir);
        Assert.False(result.Ok);
        Assert.Contains("不登记", result.Detail, StringComparison.Ordinal);
        Assert.Empty(rig.Env.Inspect(rig.Paths.ModDir).Ours);

        // Worker 的看护:策略生效后有人又登记了,下一拍撤下
        rig.Env.Apply(rig.Paths.ModDir, enable: true);
        Assert.True(rig.Control.EnforcePolicy());
        Assert.Empty(rig.Env.Inspect(rig.Paths.ModDir).Ours);
        Assert.False(rig.Control.EnforcePolicy(), "没有可撤的就不写文件");
    }

    [Fact]
    public void 托管策略判定_明确false与其它键不拦_注册表值同样生效()
    {
        Assert.True(PolicyFile("""{ "disableSideloadFlags": false, "permissions": {} }""").Check().CanRegister);
        Assert.True(NoPolicy().Check().CanRegister);
        SideloadVerdict registry = new SideloadPolicy(() => Array.Empty<string>(), () => [null, """{ "disableSideloadFlags": true }"""]).Check();
        Assert.True(registry.Blocked);
        Assert.False(registry.CanRegister);
        SideloadVerdict missingFile = new SideloadPolicy(() => [Path.Combine(TestTemp.NewPath("bg-none"), "managed-settings.json")], () => Array.Empty<string?>()).Check();
        Assert.True(missingFile.CanRegister, "策略文件不存在 = 没有策略");
    }

    [Fact]
    public void 退役_settings读不懂且不含我方条目时照常继续_含我方条目时抛出()
    {
        Rig rig = NewRig();
        Directory.CreateDirectory(Path.GetDirectoryName(rig.Settings)!);
        File.WriteAllText(rig.Settings, "{ broken");
        Assert.False(rig.Control.Retire());
        File.WriteAllText(rig.Settings, "{ \"env\": { \"CLAUDE_CODE_PLUGIN_DIRS\": \"C:\\\\x\\\\airesume-budget-gate\" ");
        Assert.ThrowsAny<Exception>(() => rig.Control.Retire());
    }

    [Fact]
    public void 看护补config只补不降_版本不低于意图时不动()
    {
        Rig rig = NewRig();
        Assert.True(rig.Control.Reconcile(rig.Paths.ModDir).Ok);
        // 界面刚切换到 v2(config 已写),但这个看护进程读到的意图仍是 v1:不得把 v1 写回去。
        File.WriteAllText(rig.Paths.ConfigFile, """{ "schema": 1, "version": 2, "mode": "observe" }""");
        Assert.False(rig.Control.HealConfig());
        Assert.Equal("observe", Read(rig.Paths.ConfigFile)["mode"]!.GetValue<string>());
        // 落后于意图才补
        File.WriteAllText(rig.Paths.ConfigFile, """{ "schema": 1, "version": 0, "mode": "observe" }""");
        Assert.True(rig.Control.HealConfig());
        Assert.Equal("shadow", Read(rig.Paths.ConfigFile)["mode"]!.GetValue<string>());
        Assert.False(rig.Control.HealConfig(), "已一致时不再写");
        // 版本相同、模式却不同(手改过或开发期残留):不是"降",按意图重写;install 对账同样
        File.WriteAllText(rig.Paths.ConfigFile, """{ "schema": 1, "version": 1, "mode": "enforce" }""");
        Assert.True(rig.Control.HealConfig());
        Assert.Equal("shadow", Read(rig.Paths.ConfigFile)["mode"]!.GetValue<string>());
        File.WriteAllText(rig.Paths.ConfigFile, """{ "schema": 1, "version": 1, "mode": "enforce" }""");
        Assert.True(rig.Control.Reconcile(rig.Paths.ModDir).Ok);
        Assert.Equal("shadow", Read(rig.Paths.ConfigFile)["mode"]!.GetValue<string>());
    }

    [Fact]
    public void 切换模式_config写不进去时意图恢复原样()
    {
        Rig rig = NewRig();
        Assert.True(rig.Control.Reconcile(rig.Paths.ModDir).Ok);
        // 把 config.json 的位置占成目录:写入必然失败
        File.Delete(rig.Paths.ConfigFile);
        Directory.CreateDirectory(rig.Paths.ConfigFile);
        Assert.ThrowsAny<Exception>(() => rig.Control.SetMode("enforce"));
        ProductConfig config = rig.Store.Load();
        Assert.Equal("shadow", config.BudgetGateMode);
        Assert.Equal(1, config.BudgetGateConfigVersion);
    }

    [Fact]
    public void config被删后看护按意图重写_清理过期日志与心跳()
    {
        Rig rig = NewRig();
        Assert.True(rig.Control.Reconcile(rig.Paths.ModDir).Ok);
        File.Delete(rig.Paths.ConfigFile);
        Directory.CreateDirectory(rig.Paths.LogsDir);
        Directory.CreateDirectory(rig.Paths.SessionsDir);
        string oldLog = Path.Combine(rig.Paths.LogsDir, "old.jsonl");
        string newLog = Path.Combine(rig.Paths.LogsDir, "new.jsonl");
        string oldBeat = Path.Combine(rig.Paths.SessionsDir, "old.json");
        string newBeat = Path.Combine(rig.Paths.SessionsDir, "new.json");
        string oldTemp = rig.Paths.ConfigFile + ".tmp-0123abcd";
        string newTemp = rig.Paths.SnapshotFile + ".tmp-4567ef01";
        foreach (string f in new[] { oldLog, newLog, oldBeat, newBeat, oldTemp, newTemp })
            File.WriteAllText(f, "{}");
        DateTimeOffset now = DateTimeOffset.UtcNow;
        File.SetLastWriteTimeUtc(oldLog, (now - TimeSpan.FromDays(31)).UtcDateTime);
        File.SetLastWriteTimeUtc(oldBeat, (now - TimeSpan.FromDays(8)).UtcDateTime);
        File.SetLastWriteTimeUtc(oldTemp, (now - TimeSpan.FromHours(2)).UtcDateTime);

        var janitor = new BudgetGateJanitor(rig.Control, NullLogger<BudgetGateJanitor>.Instance, () => now);
        Assert.Equal(3, janitor.SweepOnce());
        Assert.False(File.Exists(oldLog));
        Assert.False(File.Exists(oldBeat));
        Assert.False(File.Exists(oldTemp), "进程中途退出留下的半截临时文件");
        Assert.True(File.Exists(newLog));
        Assert.True(File.Exists(newBeat));
        Assert.True(File.Exists(newTemp), "可能正被别的进程写着的临时文件不碰");
        Assert.Equal(1, rig.Control.ReadConfigVersion());
        Assert.True(rig.Env.Inspect(rig.Paths.ModDir).IsExact, "策略允许时看护不动登记");
    }

    // ---------- OAuth 快照导出 ----------

    private static UsageSnapshot Snapshot(DateTimeOffset capturedAt, params UsageWindow[] windows) =>
        new UsageSnapshot(UsageSnapshotMapper.ProviderName, capturedAt, [new UsageBucket("claude", true, false, windows)], null)
        {
            EvidenceSource = UsageEvidenceSource.OAuth,
            CredentialFingerprint = "fp-SECRET-should-never-leak",
        };

    [Fact]
    public void 快照导出_不含凭据与账号标识_族按scope名识别_认不出的族为null()
    {
        string path = Path.Combine(TestTemp.NewDir("bg-snap"), "quota-snapshot.json");
        long reset = DateTimeOffset.UtcNow.AddHours(2).ToUnixTimeSeconds();
        UsageSnapshot snapshot = Snapshot(DateTimeOffset.UtcNow,
            new UsageWindow("five_hour", "allowed", UsageWindow.FiveHourSeconds, reset, null, 63),
            new UsageWindow("seven_day", "allowed", UsageWindow.SevenDaySeconds, reset + 400_000, null, 41, CarriedForward: true),
            new UsageWindow("weekly_scoped:Opus", "allowed", UsageWindow.SevenDaySeconds, reset + 400_000, null, 40, Identity: "weekly_scoped:abc123"),
            new UsageWindow("weekly_scoped:Claude Design", "allowed", UsageWindow.SevenDaySeconds, reset + 400_000, null, 99),
            new UsageWindow("weekly_scoped:Sonnet", "allowed", UsageWindow.SevenDaySeconds, reset + 400_000, null, null));

        Assert.True(BudgetGateSnapshotExporter.TryExport(snapshot, path, DateTimeOffset.UtcNow));
        string text = File.ReadAllText(path);
        Assert.DoesNotContain("SECRET", text, StringComparison.Ordinal);
        Assert.DoesNotContain("fingerprint", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("abc123", text, StringComparison.Ordinal);
        JsonObject root = Read(path);
        // 7 天窗口是承接值(同一周期更早的读数):不导出,免得 mod 拿偏低的旧值兜底、界面把它当成刚观测。
        Assert.Equal("five_hour", root["windows"]!.AsArray().Single()!["kind"]!.GetValue<string>());
        JsonArray scoped = root["scoped"]!.AsArray();
        Assert.Equal(2, scoped.Count);
        Assert.Equal("opus", scoped[0]!["family"]!.GetValue<string>());
        Assert.Null(scoped[1]!["family"]);
        IReadOnlyList<WindowReading> back = BudgetGateSnapshotExporter.ReadWindows(path);
        Assert.Equal(63, back.Single(w => w.Kind == "five_hour").PercentUsed);
    }

    [Fact]
    public void 快照导出_旧的不覆盖新的_非OAuth或无数据不导出()
    {
        string path = Path.Combine(TestTemp.NewDir("bg-snap"), "quota-snapshot.json");
        long reset = DateTimeOffset.UtcNow.AddHours(2).ToUnixTimeSeconds();
        DateTimeOffset t1 = DateTimeOffset.UtcNow.AddMinutes(-10);
        DateTimeOffset t2 = DateTimeOffset.UtcNow;
        Assert.True(BudgetGateSnapshotExporter.TryExport(Snapshot(t2, new UsageWindow("five_hour", "allowed", 18000, reset, null, 70)), path, t2));
        Assert.False(BudgetGateSnapshotExporter.TryExport(Snapshot(t1, new UsageWindow("five_hour", "allowed", 18000, reset, null, 50)), path, t2));
        Assert.Equal(70, BudgetGateSnapshotExporter.ReadWindows(path).Single().PercentUsed);

        string other = Path.Combine(TestTemp.NewDir("bg-snap"), "quota-snapshot.json");
        UsageSnapshot cli = Snapshot(t2, new UsageWindow("five_hour", "allowed", 18000, reset, null, 70)) with { EvidenceSource = UsageEvidenceSource.Cli };
        Assert.False(BudgetGateSnapshotExporter.TryExport(cli, other, t2));
        Assert.False(BudgetGateSnapshotExporter.TryExport(UsageSnapshot.Unavailable("claude", t2, "x") with { EvidenceSource = UsageEvidenceSource.OAuth }, other, t2));
        Assert.False(File.Exists(other));

        // 文件里的捕获时间在未来(时钟回拨后留下的坏值):不能让它永远挡住新读数
        string future = Path.Combine(TestTemp.NewDir("bg-snap"), "quota-snapshot.json");
        File.WriteAllText(future, $$"""{ "capturedAt": "{{t2.AddDays(1):O}}", "windows": [] }""");
        Assert.True(BudgetGateSnapshotExporter.TryExport(Snapshot(t2, new UsageWindow("five_hour", "allowed", 18000, reset, null, 55)), future, t2));
        Assert.Equal(55, BudgetGateSnapshotExporter.ReadWindows(future).Single().PercentUsed);
    }

    [Fact]
    public async Task 额度服务拿到OAuth读数时交给导出挂钩_挂钩抛错不影响主流程()
    {
        long reset = DateTimeOffset.UtcNow.AddHours(2).ToUnixTimeSeconds();
        UsageSnapshot fresh = new(UsageSnapshotMapper.ProviderName, DateTimeOffset.UtcNow,
            [new UsageBucket("claude", true, false, [new UsageWindow("five_hour", "allowed", 18000, reset, null, 66)])], null);
        UsageSnapshot? captured = null;
        var quota = new QuotaService(
            probe: _ => throw new InvalidOperationException("不得执行推理"),
            oauthProbe: _ => Task.FromResult(new OAuthUsageResult(true, fresh, null, "fp-1")),
            allowCliFallback: false,
            oauthSnapshotSink: s => captured = s);
        UsageSnapshot result = await quota.GetAsync(true, default);
        Assert.True(result.HasData);
        Assert.NotNull(captured);
        Assert.Equal(UsageEvidenceSource.OAuth, captured!.EvidenceSource);

        var throwing = new QuotaService(
            probe: _ => throw new InvalidOperationException("不得执行推理"),
            oauthProbe: _ => Task.FromResult(new OAuthUsageResult(true, fresh, null, "fp-1")),
            allowCliFallback: false,
            oauthSnapshotSink: _ => throw new IOException("磁盘满"));
        Assert.True((await throwing.GetAsync(true, default)).HasData);
    }

    // ---------- 证据汇总 ----------

    private const string ModVersion = "0.3.0";

    private static BudgetGateInputs Inputs(Rig rig, DateTimeOffset now, string mode = "shadow", int? version = 3,
        PluginDirsStatus? env = null, IReadOnlyList<WindowReading>? oauth = null, SideloadVerdict? policy = null, string? installed = ModVersion) =>
        new(rig.Paths, mode, version,
            env ?? new PluginDirsStatus(true, [rig.Paths.ModDir], [], "已登记"),
            policy ?? new SideloadVerdict(false, false, "托管策略未禁止旁加载"),
            true, string.Empty, installed, oauth ?? [], now);

    private static JsonObject Reading(string kind, double percent, DateTimeOffset resetsAt) =>
        new() { ["kind"] = kind, ["percentUsed"] = percent, ["resetsAt"] = resetsAt.ToString("O") };

    private static void WriteHeartbeat(Rig rig, string sessionId, DateTimeOffset beatAt, string mode, int? version, bool ended = false, string mod = ModVersion,
        JsonArray? readings = null, DateTimeOffset? readingsAt = null)
    {
        Directory.CreateDirectory(rig.Paths.SessionsDir);
        var beat = new JsonObject
        {
            ["schema"] = 1,
            ["mod"] = mod,
            ["engine"] = "2.1.309",
            ["sessionId"] = sessionId,
            ["nonce"] = "abc123",
            ["mode"] = mode,
            ["configVersion"] = version,
            ["beatAt"] = beatAt.ToString("O"),
            ["ended"] = ended,
            ["readings"] = readings ?? new JsonArray(
                Reading("five_hour", 64, beatAt.AddHours(2)),
                Reading("seven_day", 41, beatAt.AddDays(3))),
            ["readingsAt"] = (readingsAt ?? beatAt).ToString("O"),
            ["prices"] = new JsonArray(new JsonObject { ["window"] = "five_hour", ["model"] = "opus-5-5", ["period"] = "offpeak", ["k"] = 1.21, ["source"] = "measured", ["samples"] = 6, ["spread"] = 0.3 }),
            ["pending"] = new JsonArray(new JsonObject { ["seq"] = 7, ["runId"] = "wf_x", ["agentIndex"] = 4, ["type"] = "workflow-subagent", ["model"] = "opus-5-5", ["rule"] = "over-budget", ["deferUntil"] = beatAt.AddHours(2).ToString("O"), ["applied"] = false }),
        };
        File.WriteAllText(Path.Combine(rig.Paths.SessionsDir, sessionId + ".json"), beat.ToJsonString());
    }

    [Fact]
    public void 证据_没有心跳是等新会话_全部新鲜心跳一致才算生效_一部分跟上是部分生效_都没跟上是配置未跟上()
    {
        Rig rig = NewRig();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        Assert.Equal("waiting", BudgetGateEvidence.Build(Inputs(rig, now)).Status);

        WriteHeartbeat(rig, "s-old", now - TimeSpan.FromMinutes(10), "shadow", 3);
        Assert.Equal("waiting", BudgetGateEvidence.Build(Inputs(rig, now)).Status);
        WriteHeartbeat(rig, "s-ended", now - TimeSpan.FromSeconds(20), "shadow", 3, ended: true);
        Assert.Equal("waiting", BudgetGateEvidence.Build(Inputs(rig, now)).Status);

        WriteHeartbeat(rig, "s-stale", now - TimeSpan.FromSeconds(30), "shadow", 2);
        BudgetGateReport stale = BudgetGateEvidence.Build(Inputs(rig, now));
        Assert.Equal("stale", stale.Status);
        Assert.Contains(stale.Reasons, r => r.Contains("shadow/v2", StringComparison.Ordinal));

        WriteHeartbeat(rig, "s-ok", now - TimeSpan.FromSeconds(10), "shadow", 3);
        BudgetGateReport partial = BudgetGateEvidence.Build(Inputs(rig, now));
        Assert.Equal("partial", partial.Status);
        Assert.Equal(2, partial.FreshSessions);
        Assert.Contains(partial.Reasons, r => r.Contains("1 个会话还在用旧配置", StringComparison.Ordinal));

        // 旧配置的那个会话跟上之后才是「生效中」
        WriteHeartbeat(rig, "s-stale", now - TimeSpan.FromSeconds(5), "shadow", 3);
        BudgetGateReport ok = BudgetGateEvidence.Build(Inputs(rig, now, oauth: [new WindowReading("five_hour", 62, now.AddHours(2), now)]));
        Assert.Equal("effective", ok.Status);
        Assert.Empty(ok.Reasons);
        ReadingPair five = ok.Readings.Single(r => r.Kind == "five_hour");
        Assert.Equal(64, five.ModPercent);
        Assert.Equal(62, five.OAuthPercent);
        Assert.Equal(2, five.Delta);
        Assert.True(five.ResetMatches);
        Assert.Null(ok.Readings.Single(r => r.Kind == "seven_day").OAuthPercent);
        Assert.Equal("opus-5-5", ok.Prices.Single().Model);
        Assert.Equal(7, ok.Pending.First().Seq);
    }

    [Fact]
    public void 证据_settings未登记或mod不全是未生效且说原因_读不出settings是未核实()
    {
        Rig rig = NewRig();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        WriteHeartbeat(rig, "s-ok", now, "shadow", 3);
        BudgetGateReport broken = BudgetGateEvidence.Build(Inputs(rig, now, env: new PluginDirsStatus(false, [], [], "未登记")));
        Assert.Equal("broken", broken.Status);
        Assert.Contains(broken.Reasons, r => r.Contains("未登记", StringComparison.Ordinal));
        BudgetGateReport noConfig = BudgetGateEvidence.Build(Inputs(rig, now, version: null));
        Assert.Equal("broken", noConfig.Status);
        BudgetGateReport unknown = BudgetGateEvidence.Build(Inputs(rig, now, env: new PluginDirsStatus(false, [], [], "读取失败", Unverified: true)));
        Assert.Equal("unverified", unknown.Status);
        BudgetGateReport blocked = BudgetGateEvidence.Build(Inputs(rig, now, policy: new SideloadVerdict(true, false, "组织策略禁止旁加载 mod")));
        Assert.Equal("broken", blocked.Status);
        Assert.Contains(blocked.Reasons, r => r.Contains("组织策略", StringComparison.Ordinal));
    }

    [Fact]
    public void 证据_mod读数取最新周期里的最大值_空闲会话的旧值与过期周期不算()
    {
        // 真机实证(2026-11-02):空闲会话的 usage() 停在它最后一次请求时的旧值(44%),活跃会话已到 72%,OAuth 74%。
        Rig rig = NewRig();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        DateTimeOffset reset = now.AddMinutes(100);
        WriteHeartbeat(rig, "s-idle", now, "shadow", 3, readings: new JsonArray(Reading("five_hour", 44, reset), Reading("seven_day", 21, now.AddDays(6))));
        WriteHeartbeat(rig, "s-busy", now.AddSeconds(-20), "shadow", 3, readings: new JsonArray(Reading("five_hour", 72, reset.AddSeconds(-1)), Reading("seven_day", 24, now.AddDays(6))));
        WriteHeartbeat(rig, "s-lastcycle", now, "shadow", 3, readings: new JsonArray(Reading("five_hour", 95, now.AddHours(-1))));
        WriteHeartbeat(rig, "s-old", now.AddMinutes(-40), "shadow", 3, ended: true, readings: new JsonArray(Reading("five_hour", 99, reset)));

        BudgetGateReport report = BudgetGateEvidence.Build(Inputs(rig, now, oauth: [new WindowReading("five_hour", 74, reset, now)]));
        ReadingPair five = report.Readings.Single(r => r.Kind == "five_hour");
        Assert.Equal(72, five.ModPercent);
        Assert.Equal(-2, five.Delta);
        Assert.True(five.ResetMatches);
        Assert.Equal(24, report.Readings.Single(r => r.Kind == "seven_day").ModPercent);
    }

    [Fact]
    public void 证据_旧版mod的心跳不算生效_已关闭是disabled_仍登记时说明()
    {
        Rig rig = NewRig();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        WriteHeartbeat(rig, "s-oldmod", now, "shadow", 3, mod: "0.2.0");
        BudgetGateReport old = BudgetGateEvidence.Build(Inputs(rig, now));
        Assert.Equal("stale", old.Status);
        Assert.Contains(old.Reasons, r => r.Contains("mod 0.2.0", StringComparison.Ordinal) && r.Contains("重开会话", StringComparison.Ordinal));

        BudgetGateReport off = BudgetGateEvidence.Build(Inputs(rig, now, mode: "off", env: new PluginDirsStatus(false, [], [], "未登记")));
        Assert.Equal("disabled", off.Status);
        Assert.Empty(off.Reasons);
        BudgetGateReport offStillRegistered = BudgetGateEvidence.Build(Inputs(rig, now, mode: "off"));
        Assert.Equal("disabled", offStillRegistered.Status);
        Assert.Contains(offStillRegistered.Reasons, r => r.Contains("仍登记", StringComparison.Ordinal));
    }

    [Fact]
    public void 证据_同一行出现在两个文件里只算一次_超过7天的行不算_mod报错带出来()
    {
        Rig rig = NewRig();
        Directory.CreateDirectory(rig.Paths.LogsDir);
        DateTimeOffset now = DateTimeOffset.UtcNow;
        string row = $$"""{"ts":"{{now.AddMinutes(-5):O}}","sid":"s","ev":"decision","seq":1,"mode":"shadow","verdict":"allow","applied":false,"rule":"fits","model":"opus-5-5","type":"t","win":[]}""";
        string old = $$"""{"ts":"{{now.AddDays(-8):O}}","sid":"s","ev":"decision","seq":2,"mode":"shadow","verdict":"allow","applied":false,"rule":"fits","model":"opus-5-5","type":"t","win":[]}""";
        File.WriteAllText(Path.Combine(rig.Paths.LogsDir, "20261102T000000Z-aaaaaaaa-111111-0.jsonl"), row + "\n" + old + "\n");
        File.WriteAllText(Path.Combine(rig.Paths.LogsDir, "20261102T000000Z-aaaaaaaa-111111-1.jsonl"), row + "\n");
        WriteHeartbeat(rig, "s-err", now, "shadow", 3);
        JsonObject beat = Read(Path.Combine(rig.Paths.SessionsDir, "s-err.json"));
        beat["lastError"] = "beat: EACCES";
        beat["configProblems"] = new JsonArray("mode 不是已知模式");
        File.WriteAllText(Path.Combine(rig.Paths.SessionsDir, "s-err.json"), beat.ToJsonString());

        BudgetGateReport report = BudgetGateEvidence.Build(Inputs(rig, now));
        Assert.Single(report.Decisions);
        Assert.Equal(1, report.DecisionCounts["allow"]);
        Assert.Contains("beat: EACCES", report.LastErrors);
        Assert.Contains(report.LastErrors, e => e.StartsWith("config.json:", StringComparison.Ordinal));
        Assert.False(report.Truncated);
    }

    [Fact]
    public void 证据_真实影子日志汇总出判断与复盘_坏行跳过()
    {
        Rig rig = NewRig();
        Directory.CreateDirectory(rig.Paths.LogsDir);
        string fixture = File.ReadAllText(RepositoryFile("mods", "tests", "fixtures", "v2-shadow-workflow.jsonl"));
        string log = Path.Combine(rig.Paths.LogsDir, "20261102T171141Z-a0000004-e76d8e-0.jsonl");
        File.WriteAllText(log, fixture + "{\"ts\":\n not json\n");
        // 录制于 2026-11-02:把"现在"放在录制之后不久,日志的 7 天窗口不随测试运行的日期失效。
        DateTimeOffset recordedNow = DateTimeOffset.Parse("2026-11-02T18:00:00Z", System.Globalization.CultureInfo.InvariantCulture);
        File.SetLastWriteTimeUtc(log, recordedNow.UtcDateTime);
        BudgetGateReport report = BudgetGateEvidence.Build(Inputs(rig, recordedNow));
        Assert.Equal(5, report.Decisions.Count);
        Assert.Equal(2, report.DecisionCounts["allow"]);
        Assert.Equal(3, report.DecisionCounts["would-defer"]);
        Assert.False(report.DecisionCounts.ContainsKey("deferred"));
        Assert.True(report.Decisions[0].At >= report.Decisions[^1].At, "最新的在前");
        DecisionRow overBudget = report.Decisions.Single(d => d.Rule == "over-budget");
        Assert.Equal("five_hour", overBudget.Binding);
        Assert.NotNull(overBudget.Need);
        Assert.Equal(3, report.Coverage.Total);
        Assert.Equal(3, report.Coverage.Covered);
    }

    [Fact]
    public void 证据_避免的中断_影子里本来会推迟的agent后来被限额打断_启用后推迟期内窗口到过99()
    {
        Rig rig = NewRig();
        Directory.CreateDirectory(rig.Paths.LogsDir);
        DateTimeOffset t = DateTimeOffset.UtcNow.AddHours(-1);
        // 行的形状照抄 mod 实际写的日志(见 mods/tests/fixtures)。
        static string Row(DateTimeOffset at, object body)
        {
            JsonObject node = JsonSerializer.SerializeToNode(body)!.AsObject();
            node["ts"] = at.ToString("O");
            node["sid"] = "s";
            return node.ToJsonString();
        }

        object[] noWindows = [];
        string shadow = string.Join('\n',
            Row(t, new { ev = "decision", seq = 1, mode = "shadow", verdict = "defer", applied = false, rule = "over-budget", model = "opus-5-5", type = "t", binding = "five_hour", win = noWindows }),
            Row(t.AddSeconds(1), new { ev = "spawn", phase = "post", seq = 1, agentId = "agent-A", deny = (string?)null }),
            Row(t.AddMinutes(5), new { ev = "stop_failure", error = "rate_limit", agentId = "agent-A" }),
            Row(t, new { ev = "decision", seq = 2, mode = "shadow", verdict = "defer", applied = false, rule = "over-budget", model = "opus-5-5", type = "t", win = noWindows }),
            Row(t.AddSeconds(1), new { ev = "spawn", phase = "post", seq = 2, agentId = "agent-B", deny = (string?)null }),
            // 别的错误(不是限额拒绝)不算"被限额打断"
            Row(t.AddMinutes(6), new { ev = "stop_failure", error = "server_error", agentId = "agent-B" }),
            Row(t.AddMinutes(6), new { ev = "turn_end", agent = "agent-B", reason = "error", aborted = false }));
        File.WriteAllText(Path.Combine(rig.Paths.LogsDir, "20261102T000000Z-aaaaaaaa-111111-0.jsonl"), shadow);
        var wf = new { run = "wf_1", idx = 3 };
        string enforce = string.Join('\n',
            Row(t, new { ev = "decision", seq = 1, mode = "enforce", verdict = "defer", applied = true, rule = "over-budget", model = "opus-5-5", type = "t", binding = "five_hour", wf, until = t.AddHours(2).ToString("O"), win = noWindows }),
            // 同一个工作流 agent 被拒后再派、又被推迟:只算一次
            Row(t.AddMinutes(1), new { ev = "decision", seq = 2, mode = "enforce", verdict = "defer", applied = true, rule = "over-budget", model = "opus-5-5", type = "t", binding = "five_hour", wf, until = t.AddHours(2).ToString("O"), win = noWindows }),
            Row(t.AddMinutes(10), new { ev = "rl", src = "measure", rl = new[] { new { k = "five_hour", p = 99, r = (string?)null, d = 0 } } }),
            Row(t.AddMinutes(11), new { ev = "landing", agentId = "agent-C", windows = new[] { "five_hour" }, applied = true, tool = "Read", count = 1 }));
        File.WriteAllText(Path.Combine(rig.Paths.LogsDir, "20261102T000000Z-bbbbbbbb-222222-0.jsonl"), enforce);

        AvoidedStats avoided = BudgetGateEvidence.Build(Inputs(rig, DateTimeOffset.UtcNow)).Avoided;
        Assert.Equal(1, avoided.ShadowWouldAvoid);
        Assert.Equal(1, avoided.EnforceAvoided);
        Assert.Equal(1, avoided.LandingsApplied);
    }

    // ---------- 安装 payload 与 mod 核心 ----------

    [Fact]
    public void 安装payload带上预检mod_mod能被加载所需文件齐全()
    {
        // Worker 的 csproj 把 mods/airesume-budget-gate 作为输出内容;引用它的项目(含本测试)输出里都有,install 的清单与 SHA-256 冻结自动覆盖。
        string modDir = BudgetGatePaths.ModDirUnder(AppContext.BaseDirectory);
        Assert.Equal(string.Empty, BudgetGateControl.MissingModFiles(modDir));
        Assert.NotNull(BudgetGateControl.ModVersion(modDir));
        string hooks = File.ReadAllText(Path.Combine(modDir, "hooks", "hooks.json"));
        Assert.Contains("./register.ts", hooks, StringComparison.Ordinal);
    }

    [Fact]
    public async Task mod核心的Node测试全绿()
    {
        // 决策核心、在线校准、引擎与离线回放的测试;fixture 全部来自真实录制(2.1.309,临时目录、haiku 与 Desktop 会话)。
        string mods = Path.GetDirectoryName(RepositoryFile("mods", "tests", "engine.test.ts"))!;
        string[] tests = Directory.GetFiles(mods, "*.test.ts").OrderBy(f => f, StringComparer.Ordinal).ToArray();
        Assert.NotEmpty(tests);
        var start = new ProcessStartInfo("node")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            StandardOutputEncoding = System.Text.Encoding.UTF8,
            StandardErrorEncoding = System.Text.Encoding.UTF8,
            WorkingDirectory = mods,
        };
        start.ArgumentList.Add("--test");
        // 显式指定报告格式:默认格式随 Node 版本与是否接终端而变,下面按 spec 的汇总行断言。
        start.ArgumentList.Add("--test-reporter=spec");
        foreach (string test in tests)
            start.ArgumentList.Add(test);
        using Process process = Process.Start(start)!;
        Task<string> stdout = process.StandardOutput.ReadToEndAsync();
        Task<string> stderr = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(120));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            // 只结束本用例自己启动的这个 node(按进程对象,不按名称)。
            process.Kill(entireProcessTree: true);
            throw;
        }

        string output = await stdout + await stderr;
        Assert.True(process.ExitCode == 0, output);
        Assert.Contains("ℹ fail 0", output, StringComparison.Ordinal);
    }

    private static JsonObject Read(string path) => (JsonObject)JsonNode.Parse(File.ReadAllText(path))!;

    private static string RepositoryFile(params string[] parts)
    {
        DirectoryInfo? current = new(AppContext.BaseDirectory);
        while (current is not null)
        {
            string candidate = Path.Combine([current.FullName, .. parts]);
            if (File.Exists(candidate))
                return candidate;
            current = current.Parent;
        }

        throw new FileNotFoundException(string.Join('/', parts));
    }
}
