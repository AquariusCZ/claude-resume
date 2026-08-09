using System.Text.Json;
using System.Reflection;
using System.Text;
using AiResume.Wrapper;
using Xunit;

namespace AiResume.Tests;

public sealed class CcConnectDaemonControllerTests : IDisposable
{
    private readonly string _dir = TestTemp.NewDir("cc-daemon-controller");
    private readonly string _configPath;
    private readonly string _candidatePath;
    private readonly string _logPath;
    private readonly string _binaryPath;

    public CcConnectDaemonControllerTests()
    {
        Directory.CreateDirectory(_dir);
        _configPath = Path.Combine(_dir, "config.toml");
        _candidatePath = Path.Combine(_dir, "candidate.toml");
        _logPath = Path.Combine(_dir, "cc-connect.log");
        _binaryPath = CcConnectConfigValidator.TryResolveExe()!;
        WriteConfig(_configPath, "claudecode");
        WriteConfig(_candidatePath, "codex");
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
    }

    [Fact]
    public void 上游式写锁Flush并保持句柄时锁时间戳仍可见()
    {
        string lockPath = Path.Combine(_dir, ".timestamp.lock");
        DateTimeOffset started = DateTimeOffset.UtcNow;
        using var stream = new FileStream(
            lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read);
        stream.SetLength(0);
        byte[] pid = Encoding.ASCII.GetBytes("4242\n");
        stream.Write(pid);
        stream.Flush(flushToDisk: true);

        MethodInfo read = typeof(CcConnectDaemonController).GetMethod(
            "ReadLockDefault", BindingFlags.NonPublic | BindingFlags.Static)!;
        var result = Assert.IsType<CcConnectLockResult>(read.Invoke(null, new object[] { lockPath }));

        Assert.True(result.State == CcConnectLockState.Found, result.Error);
        Assert.Equal(4242, result.Pid);
        Assert.NotNull(result.WrittenAt);
        Assert.InRange(result.WrittenAt!.Value, started.AddSeconds(-1), DateTimeOffset.UtcNow.AddSeconds(1));
    }

    [Fact]
    public void 管理API重启后验证新PID目标agent日志与守护状态()
    {
        DateTimeOffset now = DateTimeOffset.Parse("2026-08-08T12:00:00Z");
        DateTimeOffset? newLockWrittenAt = null;
        bool restarted = false, committed = false, rolledBack = false, startRequested = false;
        int taskPollsAfterRestart = 0;
        DateTimeOffset oldTaskRun = now.AddHours(-1);
        CcConnectDaemonController controller = CreateController(
            runner: (args, _) =>
            {
                Assert.Equal(new[] { "daemon", "start" }, args);
                startRequested = true;
                return new CcConnectCommandResult(0, "ok");
            },
            taskSnapshot: () => !restarted
                ? TaskSnapshot(CcConnectScheduledTaskState.Running, oldTaskRun)
                : startRequested
                    ? TaskSnapshot(CcConnectScheduledTaskState.Running, now)
                    : ++taskPollsAfterRestart < 3
                        ? TaskSnapshot(CcConnectScheduledTaskState.Running, oldTaskRun)
                        : TaskSnapshot(CcConnectScheduledTaskState.Stopped, oldTaskRun),
            taskOwnership: (pid, _) => pid == 101
                ? CcConnectTaskOwnership.Owned
                : CcConnectTaskOwnership.NotOwned,
            probe: (_, _, _) => restarted
                ? new CcConnectRuntimeSnapshot(
                    true, 202, 0, "v1.4.1", "codex", LockWrittenAt: newLockWrittenAt)
                : new CcConnectRuntimeSnapshot(true, 101, 3600, "v1.4.1", "claudecode"),
            restart: _ =>
            {
                restarted = true;
                newLockWrittenAt = now;
                AppendReadyLog(now, "codex");
                return new CcConnectRestartRequestResult(
                    CcConnectRestartRequestDisposition.Accepted, 200, "accepted");
            },
            clock: () => now,
            delay: elapsed => now += elapsed);

        CcConnectDaemonRestartResult result = controller.ActivateAndVerify(
            _configPath, _candidatePath, "ai-resume", "codex",
            () => committed = true,
            () => rolledBack = true,
            TimeSpan.FromSeconds(3));

        Assert.True(result.Ok, result.Message);
        Assert.True(committed);
        Assert.False(rolledBack);
        Assert.Equal(101, result.PreviousPid);
        Assert.Equal(202, result.CurrentPid);
        Assert.Equal("ready", result.Phase);
        Assert.True(result.ConfigWritten);
        Assert.True(startRequested);
    }

    [Fact]
    public void 重启请求失败时回滚生产配置()
    {
        bool committed = false, rolledBack = false;
        CcConnectDaemonController controller = CreateController(
            probe: (_, _, _) => new CcConnectRuntimeSnapshot(true, 101, 100, "v1.4.1", "claudecode"),
            restart: _ => new CcConnectRestartRequestResult(
                CcConnectRestartRequestDisposition.Unknown, 503, "unavailable"));

        CcConnectDaemonRestartResult result = controller.ActivateAndVerify(
            _configPath, _candidatePath, "ai-resume", "codex",
            () => committed = true,
            () => rolledBack = true);

        Assert.False(result.Ok);
        Assert.True(committed);
        Assert.True(rolledBack);
        Assert.False(result.ConfigWritten);
        Assert.Equal("verify", result.Phase);
    }

    [Fact]
    public void 重启响应丢失但随后换代仍判成功且不回滚()
    {
        DateTimeOffset now = DateTimeOffset.Parse("2026-08-08T12:00:00Z");
        DateTimeOffset restartAt = now;
        bool restarted = false, rolledBack = false;
        CcConnectDaemonController controller = CreateController(
            probe: (_, _, _) => restarted
                ? new CcConnectRuntimeSnapshot(
                    true, 202, Math.Max(0, (long)(now - restartAt).TotalSeconds), "v1.4.1", "codex")
                : new CcConnectRuntimeSnapshot(true, 101, 0, "v1.4.1", "claudecode"),
            restart: _ =>
            {
                restartAt = now;
                restarted = true;
                AppendReadyLog(now, "codex");
                return new CcConnectRestartRequestResult(
                    CcConnectRestartRequestDisposition.Unknown, null, "connection closed");
            },
            clock: () => now,
            delay: elapsed => now += elapsed);

        CcConnectDaemonRestartResult result = controller.ActivateAndVerify(
            _configPath, _candidatePath, "ai-resume", "codex",
            () => { },
            () => rolledBack = true,
            TimeSpan.FromSeconds(3));

        Assert.True(result.Ok, result.Message);
        Assert.False(rolledBack);
        Assert.Equal(202, result.CurrentPid);
    }

    [Fact]
    public void 新代次未就绪时回滚并请求恢复旧配置()
    {
        DateTimeOffset now = DateTimeOffset.Parse("2026-08-08T12:00:00Z");
        int restartCalls = 0;
        bool rolledBack = false;
        CcConnectDaemonController controller = CreateController(
            probe: (_, _, _) => new CcConnectRuntimeSnapshot(true, 101, 100, "v1.4.1", "claudecode"),
            restart: _ =>
            {
                restartCalls++;
                return new CcConnectRestartRequestResult(
                    CcConnectRestartRequestDisposition.Accepted, 200, "accepted");
            },
            clock: () => now,
            delay: elapsed => now += elapsed);

        CcConnectDaemonRestartResult result = controller.ActivateAndVerify(
            _configPath, _candidatePath, "ai-resume", "codex",
            () => { },
            () => rolledBack = true,
            TimeSpan.FromSeconds(1));

        Assert.False(result.Ok);
        Assert.True(rolledBack);
        Assert.False(result.ConfigWritten);
        Assert.Equal("verify", result.Phase);
        Assert.Equal(1, restartCalls);
        Assert.Contains("回滚", result.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(CcConnectScheduledTaskState.Disabled)]
    [InlineData(CcConnectScheduledTaskState.Missing)]
    [InlineData(CcConnectScheduledTaskState.Unknown)]
    public void 无法守护的计划任务状态在提交前拒绝(CcConnectScheduledTaskState state)
    {
        bool committed = false;
        CcConnectDaemonController controller = CreateController(
            taskSnapshot: () => TaskSnapshot(state, DateTimeOffset.UtcNow),
            probe: (_, _, _) => new CcConnectRuntimeSnapshot(true, 101, 100, "v1.4.1", "claudecode"));

        CcConnectDaemonRestartResult result = controller.ActivateAndVerify(
            _configPath, _candidatePath, "ai-resume", "codex",
            () => committed = true,
            () => { });

        Assert.False(result.Ok);
        Assert.False(committed);
        Assert.Equal("preflight", result.Phase);
    }

    [Fact]
    public void 非锁定上游版本在提交前拒绝()
    {
        bool committed = false;
        CcConnectDaemonController controller = CreateController(
            probe: (_, _, _) => new CcConnectRuntimeSnapshot(true, 101, 100, "v1.4.2", "claudecode"));

        CcConnectDaemonRestartResult result = controller.ActivateAndVerify(
            _configPath, _candidatePath, "ai-resume", "codex",
            () => committed = true,
            () => { });

        Assert.False(result.Ok);
        Assert.False(committed);
        Assert.Equal("preflight", result.Phase);
    }

    [Fact]
    public void 计划任务进程归属无法核验时提交前失败关闭()
    {
        bool committed = false;
        CcConnectDaemonController controller = CreateController(
            taskOwnership: (_, _) => CcConnectTaskOwnership.Unknown);

        CcConnectDaemonRestartResult result = controller.ActivateAndVerify(
            _configPath, _candidatePath, "ai-resume", "codex",
            () => committed = true,
            () => { });

        Assert.False(result.Ok);
        Assert.False(committed);
        Assert.Equal("preflight", result.Phase);
        Assert.Contains("是否属于计划任务", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void 计划任务action不匹配daemon脚本时提交前拒绝()
    {
        bool committed = false;
        CcConnectDaemonController controller = CreateController(
            taskSnapshot: () => TaskSnapshot(
                CcConnectScheduledTaskState.Running,
                DateTimeOffset.Parse("2026-08-08T11:00:00Z")) with
            {
                Arguments = "-File C:\\unexpected.ps1",
            });

        CcConnectDaemonRestartResult result = controller.ActivateAndVerify(
            _configPath, _candidatePath, "ai-resume", "codex",
            () => committed = true,
            () => { });

        Assert.False(result.Ok);
        Assert.False(committed);
        Assert.Equal("preflight", result.Phase);
        Assert.Contains("action", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void 计划任务action尾部追加命令也不能伪装成合法watchdog()
    {
        bool committed = false;
        CcConnectScheduledTaskSnapshot snapshot = TaskSnapshot(
            CcConnectScheduledTaskState.Running,
            DateTimeOffset.Parse("2026-08-08T11:00:00Z"));
        CcConnectDaemonController controller = CreateController(
            taskSnapshot: () => snapshot with { Arguments = snapshot.Arguments + " -Command whoami" });

        CcConnectDaemonRestartResult result = controller.ActivateAndVerify(
            _configPath, _candidatePath, "ai-resume", "codex",
            () => committed = true,
            () => { });

        Assert.False(result.Ok);
        Assert.False(committed);
        Assert.Equal("preflight", result.Phase);
    }

    [Fact]
    public void watchdog脚本含额外注释或命令时提交前拒绝()
    {
        string scriptPath = Path.Combine(_dir, "cc-connect-daemon.ps1");
        string script = File.ReadAllText(scriptPath);
        File.WriteAllText(scriptPath, script.Replace(
            "$env:CC_LOG_FILE",
            "# unexpected\n$env:CC_LOG_FILE",
            StringComparison.Ordinal));
        bool committed = false;
        CcConnectDaemonController controller = CreateController();

        CcConnectDaemonRestartResult result = controller.ActivateAndVerify(
            _configPath, _candidatePath, "ai-resume", "codex",
            () => committed = true,
            () => { });

        Assert.False(result.Ok);
        Assert.False(committed);
        Assert.Equal("preflight", result.Phase);
        Assert.Contains("额外", result.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("path")]
    [InlineData("principal")]
    [InlineData("timeout")]
    [InlineData("battery-start")]
    [InlineData("battery-stop")]
    [InlineData("restart-count")]
    [InlineData("restart-interval")]
    [InlineData("multiple")]
    public void 计划任务关键守护设置退化时提交前失败关闭(string field)
    {
        CcConnectScheduledTaskSnapshot snapshot = TaskSnapshot(
            CcConnectScheduledTaskState.Running,
            DateTimeOffset.Parse("2026-08-08T11:00:00Z"));
        snapshot = field switch
        {
            "path" => snapshot with { TaskPath = "\\Other\\" },
            "principal" => snapshot with { UserId = "NT AUTHORITY\\SYSTEM" },
            "timeout" => snapshot with { ExecutionTimeLimit = "PT72H" },
            "battery-start" => snapshot with { DisallowStartIfOnBatteries = true },
            "battery-stop" => snapshot with { StopIfGoingOnBatteries = true },
            "restart-count" => snapshot with { RestartCount = 0 },
            "restart-interval" => snapshot with { RestartInterval = "PT5M" },
            "multiple" => snapshot with { MultipleInstances = "Parallel" },
            _ => throw new ArgumentOutOfRangeException(nameof(field)),
        };
        bool committed = false;
        CcConnectDaemonController controller = CreateController(taskSnapshot: () => snapshot);

        CcConnectDaemonRestartResult result = controller.ActivateAndVerify(
            _configPath, _candidatePath, "ai-resume", "codex",
            () => committed = true,
            () => { });

        Assert.False(result.Ok);
        Assert.False(committed);
        Assert.Equal("preflight", result.Phase);
    }

    [Fact]
    public void watchdog缺少内部运行标记时提交前拒绝避免重复通知()
    {
        string scriptPath = Path.Combine(_dir, "cc-connect-daemon.ps1");
        string script = string.Join(Environment.NewLine,
            File.ReadAllLines(scriptPath).Where(line =>
                !line.Equals("$env:AI_RESUME_INTERNAL_RUN = '1'", StringComparison.Ordinal)));
        File.WriteAllText(scriptPath, script);
        bool committed = false;
        CcConnectDaemonController controller = CreateController();

        CcConnectDaemonRestartResult result = controller.ActivateAndVerify(
            _configPath, _candidatePath, "ai-resume", "codex",
            () => committed = true,
            () => { });

        Assert.False(result.Ok);
        Assert.False(committed);
        Assert.Equal("preflight", result.Phase);
        Assert.Contains("AI_RESUME_INTERNAL_RUN=1", result.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("duplicate")]
    [InlineData("disabled")]
    [InlineData("type")]
    [InlineData("user")]
    [InlineData("interval")]
    [InlineData("duration")]
    [InlineData("stop")]
    public void 重复触发器或非无限五分钟重复契约时提交前拒绝(string field)
    {
        CcConnectScheduledTaskSnapshot snapshot = TaskSnapshot(
            CcConnectScheduledTaskState.Running,
            DateTimeOffset.Parse("2026-08-08T11:00:00Z"));
        snapshot = field switch
        {
            "duplicate" => snapshot with { TriggerCount = 2, EnabledTriggerCount = 2 },
            "disabled" => snapshot with { EnabledTriggerCount = 0 },
            "type" => snapshot with { TriggerType = "MSFT_TaskTimeTrigger" },
            "user" => snapshot with { TriggerUserId = "NT AUTHORITY\\SYSTEM" },
            "interval" => snapshot with { TriggerInterval = "PT10M" },
            "duration" => snapshot with { TriggerDuration = "P1D" },
            "stop" => snapshot with { TriggerStopAtDurationEnd = true },
            _ => throw new ArgumentOutOfRangeException(nameof(field)),
        };
        bool committed = false;
        CcConnectDaemonController controller = CreateController(taskSnapshot: () => snapshot);

        CcConnectDaemonRestartResult result = controller.ActivateAndVerify(
            _configPath, _candidatePath, "ai-resume", "codex",
            () => committed = true,
            () => { });

        Assert.False(result.Ok);
        Assert.False(committed);
        Assert.Equal("preflight", result.Phase);
    }

    [Fact]
    public void 不同新PID的日志证据不能拼接成功()
    {
        DateTimeOffset now = DateTimeOffset.Parse("2026-08-08T12:00:00Z");
        int probes = 0;
        bool restarted = false, rolledBack = false;
        CcConnectDaemonController controller = CreateController(
            probe: (_, _, _) => !restarted
                ? new CcConnectRuntimeSnapshot(true, 101, 100, "v1.4.1", "claudecode")
                : ++probes < 3
                    ? new CcConnectRuntimeSnapshot(true, 202, 0, "v1.4.1", "codex")
                    : new CcConnectRuntimeSnapshot(true, 303, 0, "v1.4.1", "codex"),
            restart: _ =>
            {
                restarted = true;
                File.AppendAllText(_logPath,
                    $"time={now:O} level=INFO msg=\"config loaded\" path=config.toml\n" +
                    $"time={now:O} level=INFO msg=\"engine started\" project=ai-resume agent=codex\n");
                return new CcConnectRestartRequestResult(
                    CcConnectRestartRequestDisposition.Accepted, 200, "accepted");
            },
            clock: () => now,
            delay: elapsed =>
            {
                now += elapsed;
                if (probes == 3)
                {
                    File.AppendAllText(_logPath,
                        $"time={now:O} level=INFO msg=\"platform ready\" project=ai-resume platform=feishu\n" +
                        $"time={now:O} level=INFO msg=\"cc-connect is running\"\n");
                    probes++;
                }
            });

        CcConnectDaemonRestartResult result = controller.ActivateAndVerify(
            _configPath, _candidatePath, "ai-resume", "codex",
            () => { },
            () => rolledBack = true,
            TimeSpan.FromSeconds(2));

        Assert.False(result.Ok);
        Assert.True(rolledBack);
        Assert.Equal("verify", result.Phase);
    }

    [Fact]
    public void 候选PID短暂不可达后换PID也不能拼接前代日志()
    {
        DateTimeOffset now = DateTimeOffset.Parse("2026-08-08T12:00:00Z");
        bool restarted = false, rolledBack = false, appendedSecond = false;
        int postRestartProbes = 0;
        CcConnectDaemonController controller = CreateController(
            probe: (_, _, _) =>
            {
                if (!restarted)
                {
                    return new CcConnectRuntimeSnapshot(true, 101, 100, "v1.4.1", "claudecode");
                }

                postRestartProbes++;
                if (postRestartProbes == 1)
                {
                    return new CcConnectRuntimeSnapshot(true, 202, 0, "v1.4.1", "codex");
                }
                if (postRestartProbes == 2)
                {
                    return new CcConnectRuntimeSnapshot(false, 202, 0, string.Empty, string.Empty, "starting");
                }
                if (!appendedSecond)
                {
                    appendedSecond = true;
                    File.AppendAllText(_logPath,
                        $"time={now:O} level=INFO msg=\"platform ready\" project=ai-resume platform=feishu\n" +
                        $"time={now:O} level=INFO msg=\"cc-connect is running\"\n");
                }
                return new CcConnectRuntimeSnapshot(true, 303, 0, "v1.4.1", "codex");
            },
            restart: _ =>
            {
                restarted = true;
                File.AppendAllText(_logPath,
                    $"time={now:O} level=INFO msg=\"config loaded\" path=config.toml\n" +
                    $"time={now:O} level=INFO msg=\"engine started\" project=ai-resume agent=codex\n");
                return new CcConnectRestartRequestResult(
                    CcConnectRestartRequestDisposition.Accepted, 200, "accepted");
            },
            clock: () => now,
            delay: elapsed => now += elapsed);

        CcConnectDaemonRestartResult result = controller.ActivateAndVerify(
            _configPath, _candidatePath, "ai-resume", "codex",
            () => { },
            () => rolledBack = true,
            TimeSpan.FromSeconds(2));

        Assert.False(result.Ok);
        Assert.True(rolledBack);
        Assert.Equal("verify", result.Phase);
    }

    [Fact]
    public void 重启后任务定义被替换时不得报告成功()
    {
        DateTimeOffset now = DateTimeOffset.Parse("2026-08-08T12:00:00Z");
        bool restarted = false;
        CcConnectScheduledTaskSnapshot valid = TaskSnapshot(
            CcConnectScheduledTaskState.Running, now.AddHours(-1));
        CcConnectDaemonController controller = CreateController(
            taskSnapshot: () => restarted ? valid with { RestartCount = 0 } : valid,
            probe: (_, _, _) => restarted
                ? new CcConnectRuntimeSnapshot(true, 202, 0, "v1.4.1", "codex")
                : new CcConnectRuntimeSnapshot(true, 101, 100, "v1.4.1", "claudecode"),
            restart: _ =>
            {
                restarted = true;
                AppendReadyLog(now, "codex");
                return new CcConnectRestartRequestResult(
                    CcConnectRestartRequestDisposition.Accepted, 200, "accepted");
            },
            clock: () => now,
            delay: elapsed => now += elapsed);

        CcConnectDaemonRestartResult result = controller.ActivateAndVerify(
            _configPath, _candidatePath, "ai-resume", "codex",
            () => { },
            () => { },
            TimeSpan.FromSeconds(3));

        Assert.False(result.Ok);
        Assert.Equal("rearm", result.Phase);
    }

    [Fact]
    public void Stopped任务定义被替换时必须先拒绝且不得启动()
    {
        DateTimeOffset now = DateTimeOffset.Parse("2026-08-08T12:00:00Z");
        bool restarted = false, startRequested = false;
        CcConnectScheduledTaskSnapshot baselineTask = TaskSnapshot(
            CcConnectScheduledTaskState.Running, now.AddHours(-1));
        CcConnectDaemonController controller = CreateController(
            runner: (_, _) =>
            {
                startRequested = true;
                return new CcConnectCommandResult(0, "unexpected");
            },
            taskSnapshot: () => !restarted
                ? baselineTask
                : baselineTask with
                {
                    State = CcConnectScheduledTaskState.Stopped,
                    Arguments = "-File C:\\malicious.ps1",
                },
            probe: (_, _, _) => restarted
                ? new CcConnectRuntimeSnapshot(true, 202, 0, "v1.4.1", "codex")
                : new CcConnectRuntimeSnapshot(true, 101, 100, "v1.4.1", "claudecode"),
            restart: _ =>
            {
                restarted = true;
                AppendReadyLog(now, "codex");
                return new CcConnectRestartRequestResult(
                    CcConnectRestartRequestDisposition.Accepted, 200, "accepted");
            },
            clock: () => now,
            delay: elapsed => now += elapsed);

        CcConnectDaemonRestartResult result = controller.ActivateAndVerify(
            _configPath, _candidatePath, "ai-resume", "codex",
            () => { },
            () => { },
            TimeSpan.FromSeconds(3));

        Assert.False(result.Ok);
        Assert.Equal("rearm", result.Phase);
        Assert.False(startRequested);
    }

    [Fact]
    public void 同一日志读取块中的前代标记不得越过当前锁写入时间()
    {
        DateTimeOffset now = DateTimeOffset.Parse("2026-08-08T12:00:00Z");
        DateTimeOffset currentLockWrittenAt = now.AddMilliseconds(500);
        bool restarted = false, rolledBack = false;
        CcConnectDaemonController controller = CreateController(
            probe: (_, _, _) => restarted
                ? new CcConnectRuntimeSnapshot(
                    true, 303, 0, "v1.4.1", "codex", LockWrittenAt: currentLockWrittenAt)
                : new CcConnectRuntimeSnapshot(true, 101, 100, "v1.4.1", "claudecode"),
            restart: _ =>
            {
                restarted = true;
                File.AppendAllText(_logPath,
                    $"time={now:O} level=INFO msg=\"config loaded\" path=config.toml\n" +
                    $"time={now:O} level=INFO msg=\"engine started\" project=ai-resume agent=codex\n" +
                    $"time={now.AddSeconds(1):O} level=INFO msg=\"platform ready\" project=ai-resume platform=feishu\n" +
                    $"time={now.AddSeconds(1):O} level=INFO msg=\"cc-connect is running\"\n");
                return new CcConnectRestartRequestResult(
                    CcConnectRestartRequestDisposition.Accepted, 200, "accepted");
            },
            clock: () => now,
            delay: elapsed => now += elapsed);

        CcConnectDaemonRestartResult result = controller.ActivateAndVerify(
            _configPath, _candidatePath, "ai-resume", "codex",
            () => { },
            () => rolledBack = true,
            TimeSpan.FromSeconds(2));

        Assert.False(result.Ok);
        Assert.True(rolledBack);
        Assert.Equal("verify", result.Phase);
    }

    [Fact]
    public void 任务Queued过渡可等待但必须最终稳定Running()
    {
        DateTimeOffset now = DateTimeOffset.Parse("2026-08-08T12:00:00Z");
        DateTimeOffset oldRun = now.AddHours(-1);
        bool restarted = false;
        int queuedPolls = 0;
        CcConnectDaemonController controller = CreateController(
            taskSnapshot: () => !restarted
                ? TaskSnapshot(CcConnectScheduledTaskState.Running, oldRun)
                : ++queuedPolls <= 2
                    ? TaskSnapshot(CcConnectScheduledTaskState.Queued, oldRun)
                    : TaskSnapshot(CcConnectScheduledTaskState.Running, oldRun),
            probe: (_, _, _) => restarted
                ? new CcConnectRuntimeSnapshot(true, 202, 0, "v1.4.1", "codex")
                : new CcConnectRuntimeSnapshot(true, 101, 100, "v1.4.1", "claudecode"),
            restart: _ =>
            {
                restarted = true;
                AppendReadyLog(now, "codex");
                return new CcConnectRestartRequestResult(
                    CcConnectRestartRequestDisposition.Accepted, 200, "accepted");
            },
            clock: () => now,
            delay: elapsed => now += elapsed);

        CcConnectDaemonRestartResult result = controller.ActivateAndVerify(
            _configPath, _candidatePath, "ai-resume", "codex",
            () => { },
            () => { },
            TimeSpan.FromSeconds(3));

        Assert.True(result.Ok, result.Message);
        Assert.True(queuedPolls > 2);
    }

    [Fact]
    public void 守护复核后新进程消失时最终态必须失败()
    {
        DateTimeOffset now = DateTimeOffset.Parse("2026-08-08T12:00:00Z");
        DateTimeOffset oldRun = now.AddHours(-1);
        bool restarted = false, rearmObserved = false;
        CcConnectDaemonController controller = CreateController(
            taskSnapshot: () =>
            {
                if (restarted) rearmObserved = true;
                return TaskSnapshot(CcConnectScheduledTaskState.Running, oldRun);
            },
            probe: (_, _, _) => !restarted
                ? new CcConnectRuntimeSnapshot(true, 101, 100, "v1.4.1", "claudecode")
                : rearmObserved
                    ? new CcConnectRuntimeSnapshot(false, null, 0, string.Empty, string.Empty, "gone")
                    : new CcConnectRuntimeSnapshot(true, 202, 0, "v1.4.1", "codex"),
            restart: _ =>
            {
                restarted = true;
                AppendReadyLog(now, "codex");
                return new CcConnectRestartRequestResult(
                    CcConnectRestartRequestDisposition.Accepted, 200, "accepted");
            },
            clock: () => now,
            delay: elapsed => now += elapsed);

        CcConnectDaemonRestartResult result = controller.ActivateAndVerify(
            _configPath, _candidatePath, "ai-resume", "codex",
            () => { },
            () => { },
            TimeSpan.FromSeconds(3));

        Assert.False(result.Ok);
        Assert.Equal("postflight", result.Phase);
    }

    [Fact]
    public void 新配置验证失败后能恢复旧配置的新进程与守护()
    {
        DateTimeOffset now = DateTimeOffset.Parse("2026-08-08T12:00:00Z");
        int restartCalls = 0;
        bool rolledBack = false;
        CcConnectDaemonController controller = CreateController(
            probe: (_, _, _) => restartCalls switch
            {
                0 => new CcConnectRuntimeSnapshot(true, 101, 100, "v1.4.1", "claudecode"),
                1 => new CcConnectRuntimeSnapshot(true, 202, 0, "v1.4.1", "claudecode"),
                _ => new CcConnectRuntimeSnapshot(true, 303, 0, "v1.4.1", "claudecode"),
            },
            restart: _ =>
            {
                restartCalls++;
                if (restartCalls == 2)
                {
                    AppendReadyLog(now, "claudecode");
                }
                return new CcConnectRestartRequestResult(
                    CcConnectRestartRequestDisposition.Accepted, 200, "accepted");
            },
            clock: () => now,
            delay: elapsed => now += elapsed);

        CcConnectDaemonRestartResult result = controller.ActivateAndVerify(
            _configPath, _candidatePath, "ai-resume", "codex",
            () => { },
            () => rolledBack = true,
            TimeSpan.FromSeconds(1));

        Assert.False(result.Ok);
        Assert.True(rolledBack);
        Assert.False(result.ConfigWritten);
        Assert.Equal(2, restartCalls);
        Assert.Contains("旧配置运行态与计划任务守护均已恢复", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void management设置变化或无token时提交前拒绝()
    {
        WriteConfig(_candidatePath, "codex", port: 9821);
        bool committed = false;
        CcConnectDaemonController controller = CreateController();

        CcConnectDaemonRestartResult result = controller.ActivateAndVerify(
            _configPath, _candidatePath, "ai-resume", "codex",
            () => committed = true,
            () => { });

        Assert.False(result.Ok);
        Assert.False(committed);
        Assert.Equal("preflight", result.Phase);
    }

    [Fact]
    public void 额外ccconnect消费者在提交前拒绝()
    {
        bool committed = false;
        var lister = new FakeLister(new[]
        {
            new RunningProcessInfo(101, "cc-connect", "cc-connect"),
            new RunningProcessInfo(303, "cc-connect", "cc-connect"),
        });
        CcConnectDaemonController controller = CreateController(
            probe: (_, _, _) => new CcConnectRuntimeSnapshot(true, 101, 100, "v1.4.1", "claudecode"),
            guardFactory: () => new SingleConsumerGuard(lister, 999));

        CcConnectDaemonRestartResult result = controller.ActivateAndVerify(
            _configPath, _candidatePath, "ai-resume", "codex",
            () => committed = true,
            () => { });

        Assert.False(result.Ok);
        Assert.False(committed);
        Assert.Contains("单消费者", result.Message, StringComparison.Ordinal);
    }

    private CcConnectDaemonController CreateController(
        Func<IReadOnlyList<string>, TimeSpan, CcConnectCommandResult>? runner = null,
        Func<CcConnectScheduledTaskSnapshot>? taskSnapshot = null,
        Func<int, DateTimeOffset?, CcConnectTaskOwnership>? taskOwnership = null,
        Func<CcConnectManagementSettings, string, string, CcConnectRuntimeSnapshot>? probe = null,
        Func<CcConnectManagementSettings, CcConnectRestartRequestResult>? restart = null,
        Func<SingleConsumerGuard>? guardFactory = null,
        Func<DateTimeOffset>? clock = null,
        Action<TimeSpan>? delay = null)
    {
        DateTimeOffset fallbackNow = DateTimeOffset.Parse("2026-08-08T12:00:00Z");
        clock ??= () => fallbackNow;
        delay ??= elapsed => fallbackNow += elapsed;
        return new CcConnectDaemonController(
            runner: runner ?? ((_, _) => new CcConnectCommandResult(0, "ok")),
            taskSnapshot: taskSnapshot ?? (() => TaskSnapshot(CcConnectScheduledTaskState.Running, DateTimeOffset.Parse("2026-08-08T11:00:00Z"))),
            taskOwnership: taskOwnership ?? ((_, _) => CcConnectTaskOwnership.NotOwned),
            probeRuntime: probe ?? ((_, _, _) => new CcConnectRuntimeSnapshot(true, 101, 100, "v1.4.1", "claudecode")),
            requestRestart: restart ?? (_ => new CcConnectRestartRequestResult(
                CcConnectRestartRequestDisposition.Accepted, 200, "accepted")),
            guardFactory: guardFactory ?? (() => new SingleConsumerGuard(new FakeLister(Array.Empty<RunningProcessInfo>()), 999)),
            clock: clock,
            delay: delay);
    }

    private CcConnectScheduledTaskSnapshot TaskSnapshot(
        CcConnectScheduledTaskState state,
        DateTimeOffset? lastRunTime) => new(
        state,
        lastRunTime,
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
        false);

    private void WriteConfig(string path, string agent, int port = 9820)
    {
        File.WriteAllText(path, $$"""
            [management]
            enabled = true
            port = {{port}}
            token = "test-token"

            [[projects]]
            name = "ai-resume"
            [projects.agent]
            type = "{{agent}}"
            """);
    }

    private void AppendReadyLog(DateTimeOffset timestamp, string agent)
    {
        File.AppendAllText(_logPath,
            $"time={timestamp:O} level=INFO msg=\"config loaded\" path=config.toml\n" +
            $"time={timestamp:O} level=INFO msg=\"engine started\" project=ai-resume agent={agent}\n" +
            $"time={timestamp:O} level=INFO msg=\"platform ready\" project=ai-resume platform=feishu\n" +
            $"time={timestamp:O} level=INFO msg=\"cc-connect is running\"\n");
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (Exception) { }
    }

    private sealed class FakeLister : IRunningProcessLister
    {
        private readonly IReadOnlyList<RunningProcessInfo> _items;
        public FakeLister(IReadOnlyList<RunningProcessInfo> items) => _items = items;
        public bool ProvidesCommandLine => true;
        public IReadOnlyList<RunningProcessInfo> List() => _items;
    }
}
