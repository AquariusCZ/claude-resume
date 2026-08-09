using System.Text.Json;
using AiResume.Gui;
using AiResume.Storage;
using AiResume.Worker.Migration;
using AiResume.Worker.Products;
using AiResume.Wrapper;
using Microsoft.Data.Sqlite;
using Xunit;

namespace AiResume.Tests;

[Collection(SqliteCollection.Name)]
public sealed class ControlPlaneBridgeCutoverTests : IDisposable
{
    private readonly string _dir = TestTemp.NewDir("bridge-cutover");
    private readonly string _configPath;
    private readonly string _logPath;
    private readonly string _dbPath;
    private readonly string _binaryPath;

    public ControlPlaneBridgeCutoverTests()
    {
        Directory.CreateDirectory(_dir);
        _configPath = Path.Combine(_dir, "config.toml");
        _logPath = Path.Combine(_dir, "cc-connect.log");
        _dbPath = Path.Combine(_dir, "state.db");
        _binaryPath = CcConnectConfigValidator.TryResolveExe()!;
        File.WriteAllText(_configPath, Config("claudecode"));
        File.WriteAllText(_logPath, string.Empty);
        File.WriteAllText(Path.Combine(_dir, "daemon.json"), JsonSerializer.Serialize(new
        {
            work_dir = _dir,
            binary_path = _binaryPath,
            log_file = _logPath,
        }));
        File.WriteAllText(Path.Combine(_dir, "cc-connect-daemon.ps1"), $$"""
            $ErrorActionPreference = 'Stop'
            $env:CC_LOG_FILE = '{{_logPath.Replace("'", "''", StringComparison.Ordinal)}}'
            $env:CC_LOG_MAX_SIZE = '10485760'
            $env:CC_LOG_MAX_BACKUPS = '3'
            $env:AI_RESUME_INTERNAL_RUN = '1'
            Set-Location -LiteralPath '{{_dir.Replace("'", "''", StringComparison.Ordinal)}}'
            while ($true) {
              & '{{_binaryPath.Replace("'", "''", StringComparison.Ordinal)}}'
              $exitCode = $LASTEXITCODE
              if ($exitCode -eq 0) { exit 0 }
              Start-Sleep -Seconds 10
            }
            """);
        StorageDatabase.Migrate(_dbPath);
    }

    [Fact]
    public async Task 候选配置提交后经管理API重启并回传阶段状态()
    {
        DateTimeOffset now = DateTimeOffset.Parse("2026-08-08T12:00:00Z");
        bool restarted = false;
        ControlPlaneBridge bridge = CreateBridge(() => CreateController(
            probe: (_, _, _) => restarted
                ? new CcConnectRuntimeSnapshot(true, 202, 0, "v1.4.1", "codex")
                : new CcConnectRuntimeSnapshot(true, 101, 100, "v1.4.1", "claudecode"),
            restart: _ =>
            {
                restarted = true;
                AppendReadyLog(now);
                return new CcConnectRestartRequestResult(
                    CcConnectRestartRequestDisposition.Accepted, 200, "accepted");
            },
            clock: () => now,
            delay: elapsed => now += elapsed));

        string response = await bridge.HandleAsync(
            "{\"type\":\"cutover.generate\",\"id\":\"1\"}", CancellationToken.None);
        JsonElement payload = Payload(response);

        Assert.True(payload.GetProperty("ok").GetBoolean(), payload.GetProperty("message").GetString());
        Assert.True(payload.GetProperty("configWritten").GetBoolean());
        Assert.True(payload.GetProperty("restartVerified").GetBoolean());
        Assert.Equal("ready", payload.GetProperty("phase").GetString());
        Assert.Contains("type = \"codex\"", File.ReadAllText(_configPath), StringComparison.Ordinal);
        Assert.Empty(Directory.EnumerateFiles(_dir, ".config.toml.ai-resume-candidate-*"));
        Assert.False(bridge.IsCutoverInProgress);
    }

    [Fact]
    public async Task 候选校验后生产配置被外部修改时拒绝覆盖()
    {
        bool changed = false;
        ControlPlaneBridge bridge = CreateBridge(() => CreateController(
            probe: (_, _, _) =>
            {
                if (!changed)
                {
                    File.WriteAllText(_configPath, "# external");
                    changed = true;
                }
                return new CcConnectRuntimeSnapshot(true, 101, 100, "v1.4.1", "claudecode");
            }));

        string response = await bridge.HandleAsync(
            "{\"type\":\"cutover.generate\",\"id\":\"2\"}", CancellationToken.None);
        JsonElement payload = Payload(response);

        Assert.False(payload.GetProperty("ok").GetBoolean());
        Assert.False(payload.GetProperty("configWritten").GetBoolean());
        Assert.Equal("commit", payload.GetProperty("phase").GetString());
        Assert.Equal("# external", File.ReadAllText(_configPath));
    }

    [Fact]
    public async Task 重启请求失败时精确恢复原配置字节()
    {
        byte[] original = File.ReadAllBytes(_configPath);
        ControlPlaneBridge bridge = CreateBridge(() => CreateController(
            restart: _ => new CcConnectRestartRequestResult(
                CcConnectRestartRequestDisposition.Unknown, 503, "unavailable")));

        string response = await bridge.HandleAsync(
            "{\"type\":\"cutover.generate\",\"id\":\"3\"}", CancellationToken.None);
        JsonElement payload = Payload(response);

        Assert.False(payload.GetProperty("ok").GetBoolean());
        Assert.False(payload.GetProperty("configWritten").GetBoolean());
        Assert.Equal("verify", payload.GetProperty("phase").GetString());
        Assert.Equal(original, File.ReadAllBytes(_configPath));
    }

    private ControlPlaneBridge CreateBridge(Func<CcConnectDaemonController> controllerFactory)
    {
        string storeDir = Path.Combine(_dir, "store");
        Directory.CreateDirectory(storeDir);
        return new ControlPlaneBridge(
            configStore: new ProductConfigStore(storeDir),
            stateStore: new ProductStateStore(_dbPath),
            daemonControllerFactory: controllerFactory,
            cutoverConfigPath: _configPath,
            cutoverGenerate: path =>
            {
                string candidate = Config("codex");
                File.WriteAllText(path, candidate);
                return new CutoverConfigCommand.GenerateResult(true, "ok", 1, candidate, path);
            });
    }

    private CcConnectDaemonController CreateController(
        Func<CcConnectManagementSettings, string, string, CcConnectRuntimeSnapshot>? probe = null,
        Func<CcConnectManagementSettings, CcConnectRestartRequestResult>? restart = null,
        Func<DateTimeOffset>? clock = null,
        Action<TimeSpan>? delay = null)
    {
        DateTimeOffset fallbackNow = DateTimeOffset.Parse("2026-08-08T12:00:00Z");
        clock ??= () => fallbackNow;
        delay ??= elapsed => fallbackNow += elapsed;
        return new CcConnectDaemonController(
            runner: (_, _) => new CcConnectCommandResult(0, "ok"),
            taskSnapshot: () => new CcConnectScheduledTaskSnapshot(
                CcConnectScheduledTaskState.Running,
                DateTimeOffset.Parse("2026-08-08T11:00:00Z"),
                "\\",
                1,
                "powershell.exe",
                $"-WindowStyle Hidden -NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"{Path.Combine(_dir, "cc-connect-daemon.ps1")}\"",
                Environment.UserName,
                "S4U",
                "Limited",
                "PT0S",
                false,
                false,
                3,
                "PT1M",
                "IgnoreNew",
                1,
                1,
                "MSFT_TaskLogonTrigger",
                Environment.UserName,
                "PT5M",
                string.Empty,
                false),
            taskOwnership: (_, _) => CcConnectTaskOwnership.NotOwned,
            probeRuntime: probe ?? ((_, _, _) => new CcConnectRuntimeSnapshot(true, 101, 100, "v1.4.1", "claudecode")),
            requestRestart: restart ?? (_ => new CcConnectRestartRequestResult(
                CcConnectRestartRequestDisposition.Accepted, 200, "accepted")),
            guardFactory: () => new SingleConsumerGuard(new EmptyLister(), 999),
            clock: clock,
            delay: delay);
    }

    private static string Config(string agent) => $$"""
        [management]
        enabled = true
        port = 9820
        token = "test-token"

        [[projects]]
        name = "ai-resume"
        [projects.agent]
        type = "{{agent}}"
        """;

    private void AppendReadyLog(DateTimeOffset timestamp)
    {
        File.AppendAllText(_logPath,
            $"time={timestamp:O} level=INFO msg=\"config loaded\" path=config.toml\n" +
            $"time={timestamp:O} level=INFO msg=\"platform ready\" project=ai-resume platform=feishu\n" +
            $"time={timestamp:O} level=INFO msg=\"engine started\" project=ai-resume agent=codex\n" +
            $"time={timestamp:O} level=INFO msg=\"cc-connect is running\"\n");
    }

    private static JsonElement Payload(string response)
    {
        using JsonDocument doc = JsonDocument.Parse(response);
        if (doc.RootElement.TryGetProperty("error", out JsonElement error) && error.ValueKind == JsonValueKind.String)
        {
            Assert.Fail(error.GetString());
        }
        return doc.RootElement.GetProperty("payload").Clone();
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch (Exception) { }
    }

    private sealed class EmptyLister : IRunningProcessLister
    {
        public bool ProvidesCommandLine => true;
        public IReadOnlyList<RunningProcessInfo> List() => Array.Empty<RunningProcessInfo>();
    }
}
