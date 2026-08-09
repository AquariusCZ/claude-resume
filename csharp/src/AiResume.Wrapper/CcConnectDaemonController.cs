using System.Diagnostics;
using System.Management;
using System.Net.Http.Headers;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Tomlyn;
using Tomlyn.Model;

namespace AiResume.Wrapper;

public sealed record CcConnectCommandResult(int ExitCode, string Output);

public enum CcConnectRestartRequestDisposition
{
    Accepted,
    Rejected,
    Unknown,
}

public sealed record CcConnectRestartRequestResult(
    CcConnectRestartRequestDisposition Disposition,
    int? StatusCode,
    string Output);

public enum CcConnectScheduledTaskState
{
    Running,
    Queued,
    Stopped,
    Disabled,
    Missing,
    Unknown,
}

public enum CcConnectLockState
{
    Found,
    Missing,
    Invalid,
    Unreadable,
}

public sealed record CcConnectLockResult(
    CcConnectLockState State,
    int? Pid,
    string? Error = null,
    DateTimeOffset? WrittenAt = null);

public sealed record CcConnectDaemonMetadata(string WorkDir, string BinaryPath, string LogPath);

public sealed record CcConnectScheduledTaskSnapshot(
    CcConnectScheduledTaskState State,
    DateTimeOffset? LastRunTime,
    string TaskPath,
    int ActionCount,
    string Execute,
    string Arguments,
    string UserId,
    string LogonType,
    string RunLevel,
    string ExecutionTimeLimit,
    bool DisallowStartIfOnBatteries,
    bool StopIfGoingOnBatteries,
    int RestartCount,
    string RestartInterval,
    string MultipleInstances,
    int TriggerCount,
    int EnabledTriggerCount,
    string TriggerType,
    string TriggerUserId,
    string TriggerInterval,
    string TriggerDuration,
    bool TriggerStopAtDurationEnd,
    string? Error = null);

public enum CcConnectTaskOwnership
{
    Owned,
    NotOwned,
    Unknown,
}

public sealed record CcConnectManagementSettings(bool Enabled, int Port, string Token);

public sealed record CcConnectRuntimeSnapshot(
    bool Reachable,
    int? LockPid,
    long UptimeSeconds,
    string Version,
    string Agent,
    string? Error = null,
    DateTimeOffset? LockWrittenAt = null);

public sealed record CcConnectDaemonRestartResult(
    bool Ok,
    string Message,
    int? PreviousPid,
    int? CurrentPid,
    string ExpectedAgent,
    string LogPath,
    bool ConfigWritten,
    string Phase);

/// <summary>
/// Activates a staged cc-connect config without opening or killing the Windows daemon process.
/// The upstream scheduled task runs as S4U, so an interactive GUI cannot safely inspect or terminate
/// its process handle. Instead this class uses cc-connect's authenticated in-process restart endpoint,
/// which performs Engine.Stop, closes agent sessions, releases the instance lock and starts the next
/// generation in the same security context. The scheduled-task watchdog is then re-armed and verified.
/// </summary>
public sealed class CcConnectDaemonController
{
    public const string SupportedVersion = "v1.4.1";

    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan DefaultReadinessTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan StableGenerationWindow = TimeSpan.FromMilliseconds(1000);

    private readonly Func<IReadOnlyList<string>, TimeSpan, CcConnectCommandResult> _runner;
    private readonly Func<CcConnectScheduledTaskSnapshot> _taskSnapshot;
    private readonly Func<int, DateTimeOffset?, CcConnectTaskOwnership> _taskOwnership;
    private readonly Func<CcConnectManagementSettings, string, string, CcConnectRuntimeSnapshot> _probeRuntime;
    private readonly Func<CcConnectManagementSettings, CcConnectRestartRequestResult> _requestRestart;
    private readonly Func<SingleConsumerGuard> _guardFactory;
    private readonly Func<DateTimeOffset> _clock;
    private readonly Action<TimeSpan> _delay;

    public CcConnectDaemonController(
        Func<IReadOnlyList<string>, TimeSpan, CcConnectCommandResult>? runner = null,
        Func<CcConnectScheduledTaskSnapshot>? taskSnapshot = null,
        Func<int, DateTimeOffset?, CcConnectTaskOwnership>? taskOwnership = null,
        Func<CcConnectManagementSettings, string, string, CcConnectRuntimeSnapshot>? probeRuntime = null,
        Func<CcConnectManagementSettings, CcConnectRestartRequestResult>? requestRestart = null,
        Func<SingleConsumerGuard>? guardFactory = null,
        Func<DateTimeOffset>? clock = null,
        Action<TimeSpan>? delay = null)
    {
        _runner = runner ?? RunDefault;
        _taskSnapshot = taskSnapshot ?? QueryScheduledTaskDefault;
        _taskOwnership = taskOwnership ?? GetTaskOwnershipDefault;
        _probeRuntime = probeRuntime ?? ProbeRuntimeDefault;
        _requestRestart = requestRestart ?? RequestRestartDefault;
        _guardFactory = guardFactory ?? SingleConsumerGuard.CreateDefault;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
        _delay = delay ?? Thread.Sleep;
    }

    public CcConnectDaemonRestartResult ActivateAndVerify(
        string configPath,
        string candidatePath,
        string projectName,
        string expectedAgent,
        Action commitConfiguration,
        Action rollbackConfiguration,
        TimeSpan? readinessTimeout = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(configPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(candidatePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(projectName);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedAgent);
        ArgumentNullException.ThrowIfNull(commitConfiguration);
        ArgumentNullException.ThrowIfNull(rollbackConfiguration);

        string fullConfigPath = Path.GetFullPath(configPath);
        CcConnectDaemonMetadata metadata;
        CcConnectManagementSettings settings;
        try
        {
            metadata = ReadMetadata(fullConfigPath);
            ValidateMetadata(fullConfigPath, metadata);
            settings = ReadManagementSettings(fullConfigPath);
            CcConnectManagementSettings candidateSettings = ReadManagementSettings(candidatePath);
            if (!settings.Enabled || settings.Port is < 1 or > 65535 || string.IsNullOrWhiteSpace(settings.Token))
            {
                throw new InvalidDataException("[management] 必须启用并配置有效端口与 token,才能安全触发 daemon 自重启");
            }

            if (candidateSettings != settings)
            {
                throw new InvalidDataException("候选配置改变了 management 端口、token 或 enabled;为避免重启后失去验证通道而拒绝提交");
            }
        }
        catch (Exception ex)
        {
            return Failed("preflight", "生产配置未改动:" + ex.Message,
                null, null, expectedAgent, string.Empty, false);
        }

        string lockPath = Path.Combine(metadata.WorkDir, ".config.toml.lock");
        CcConnectRuntimeSnapshot baseline = _probeRuntime(settings, lockPath, projectName);
        if (!baseline.Reachable || baseline.LockPid is null ||
            !baseline.Version.Equals(SupportedVersion, StringComparison.OrdinalIgnoreCase))
        {
            return Failed("preflight",
                "生产配置未改动:当前 cc-connect 管理 API、实例锁或版本无法可信核验。" + SafeError(baseline.Error),
                baseline.LockPid, null, expectedAgent, metadata.LogPath, false);
        }

        CcConnectScheduledTaskSnapshot initialTask = _taskSnapshot();
        try
        {
            ValidateScheduledTask(metadata, initialTask);
        }
        catch (Exception ex)
        {
            return Failed("preflight", "生产配置未改动:无法绑定 cc-connect 计划任务守护:" + ex.Message,
                baseline.LockPid, null, expectedAgent, metadata.LogPath, false);
        }

        if (initialTask.State != CcConnectScheduledTaskState.Running)
        {
            return Failed("preflight", $"生产配置未改动:cc-connect 计划任务状态为 {initialTask.State},当前运行实例不在可验证的守护状态。",
                baseline.LockPid, null, expectedAgent, metadata.LogPath, false);
        }

        CcConnectTaskOwnership baselineOwnership = _taskOwnership(
            baseline.LockPid.Value, initialTask.LastRunTime);
        if (baselineOwnership == CcConnectTaskOwnership.Unknown)
        {
            return Failed("preflight",
                "生产配置未改动:无法确认当前锁 PID 是否属于计划任务实例或其既有 watchdog;拒绝切换。",
                baseline.LockPid, null, expectedAgent, metadata.LogPath, false);
        }
        bool baselineOwnedByTask = baselineOwnership == CcConnectTaskOwnership.Owned;

        if (!CheckConsumers(baseline.LockPid.Value, out string? guardError))
        {
            return Failed("preflight", "生产配置未改动:" + guardError,
                baseline.LockPid, null, expectedAgent, metadata.LogPath, false);
        }

        // Recheck immediately before commit. This does not make external config writes impossible,
        // but it closes the process-admission window; the bridge separately verifies the file hash.
        if (!CheckConsumers(baseline.LockPid.Value, out guardError))
        {
            return Failed("precommit", "生产配置未改动:" + guardError,
                baseline.LockPid, null, expectedAgent, metadata.LogPath, false);
        }

        LogCursor cursor = CaptureLogCursor(metadata.LogPath);
        try
        {
            commitConfiguration();
        }
        catch (Exception ex)
        {
            return Failed("commit", "配置提交失败:" + ex.Message,
                baseline.LockPid, null, expectedAgent, metadata.LogPath, false);
        }

        DateTimeOffset restartRequestedAt = _clock();
        CcConnectRestartRequestResult restartRequest;
        try
        {
            restartRequest = _requestRestart(settings);
        }
        catch (Exception ex)
        {
            restartRequest = new CcConnectRestartRequestResult(
                CcConnectRestartRequestDisposition.Unknown, null, ex.Message);
        }

        // A client timeout is ambiguous: the server may already have queued RestartCh. Always
        // reconcile runtime state before deciding whether a rollback/recovery is needed.
        CcConnectDaemonRestartResult ready = WaitForNewGeneration(
            settings,
            metadata,
            lockPath,
            cursor,
            baseline,
            restartRequestedAt,
            projectName,
            expectedAgent,
            readinessTimeout ?? DefaultReadinessTimeout);
        if (!ready.Ok)
        {
            bool rolledBack = TryRollback(rollbackConfiguration, out string rollbackError);
            string recovery = RecoverAfterFailedRestart(settings, metadata, lockPath,
                baseline, initialTask, baselineOwnedByTask, projectName, rolledBack,
                readinessTimeout ?? DefaultReadinessTimeout);
            return ready with
            {
                ConfigWritten = !rolledBack,
                Message = ready.Message +
                    CommandDetail(restartRequest) +
                    (rolledBack ? " 已回滚旧配置。" : " 配置回滚失败:" + rollbackError) + recovery,
            };
        }

        if (!RearmAndVerifyTask(metadata, initialTask, baselineOwnedByTask, ready.CurrentPid,
                TimeSpan.FromSeconds(20), out CcConnectCommandResult rearm))
        {
            return Failed("rearm",
                "新 cc-connect 已加载配置,但 Windows 计划任务守护未恢复为 Running。" + CommandDetail(rearm),
                baseline.LockPid, ready.CurrentPid, expectedAgent, metadata.LogPath, true);
        }

        CcConnectRuntimeSnapshot finalRuntime = _probeRuntime(settings, lockPath, projectName);
        if (ready.CurrentPid is not int currentPid || !finalRuntime.Reachable ||
            finalRuntime.LockPid != currentPid ||
            !finalRuntime.Version.Equals(SupportedVersion, StringComparison.OrdinalIgnoreCase) ||
            !finalRuntime.Agent.Equals(expectedAgent, StringComparison.Ordinal))
        {
            return Failed("postflight",
                "计划任务复核后,新 cc-connect 运行态已变化或无法再次验证;拒绝报告成功。" +
                SafeError(finalRuntime.Error),
                baseline.LockPid, finalRuntime.LockPid, expectedAgent, metadata.LogPath, true);
        }

        if (!CheckConsumers(currentPid, out guardError))
        {
            return Failed("postflight", "新 cc-connect 已启动,但最终单消费者检查失败:" + guardError,
                baseline.LockPid, ready.CurrentPid, expectedAgent, metadata.LogPath, true);
        }

        return ready with
        {
            Message = ready.Message + " Windows 计划任务守护已验证为 Running。",
        };
    }

    private CcConnectDaemonRestartResult WaitForNewGeneration(
        CcConnectManagementSettings settings,
        CcConnectDaemonMetadata metadata,
        string lockPath,
        LogCursor cursor,
        CcConnectRuntimeSnapshot baseline,
        DateTimeOffset restartRequestedAt,
        string projectName,
        string expectedAgent,
        TimeSpan timeout)
    {
        DateTimeOffset deadline = _clock() + timeout;
        var observed = new StringBuilder();
        int? evidencePid = null;
        int? stablePid = null;
        DateTimeOffset? stableSince = null;
        CcConnectRuntimeSnapshot current = new(false, null, 0, string.Empty, string.Empty);

        while (_clock() <= deadline)
        {
            string appended = ReadNewLog(metadata.LogPath, ref cursor);
            current = _probeRuntime(settings, lockPath, projectName);
            int? currentEvidencePid = current.LockPid is int lockPid && lockPid != baseline.LockPid
                ? lockPid
                : null;
            if (currentEvidencePid != evidencePid)
            {
                observed.Clear();
                evidencePid = currentEvidencePid;
                stablePid = null;
                stableSince = null;
            }

            if (evidencePid is not null && appended.Length > 0)
            {
                observed.Append(appended);
                if (observed.Length > 512 * 1024)
                {
                    observed.Remove(0, observed.Length - 256 * 1024);
                }
            }

            DateTimeOffset estimatedStart = _clock().AddSeconds(-current.UptimeSeconds);
            bool generationChanged = current.Reachable && current.LockPid is int pid &&
                pid != baseline.LockPid &&
                current.Version.Equals(SupportedVersion, StringComparison.OrdinalIgnoreCase) &&
                estimatedStart >= restartRequestedAt.AddSeconds(-3) &&
                current.Agent.Equals(expectedAgent, StringComparison.Ordinal);
            if (!generationChanged)
            {
                stablePid = null;
                stableSince = null;
                _delay(PollInterval);
                continue;
            }

            if (stablePid != current.LockPid)
            {
                stablePid = current.LockPid;
                stableSince = _clock();
            }

            DateTimeOffset notBefore = current.LockWrittenAt is DateTimeOffset lockWrittenAt &&
                lockWrittenAt > restartRequestedAt
                    ? lockWrittenAt
                    : restartRequestedAt;
            string text = observed.ToString();
            bool configLoaded = ContainsLogLine(text, notBefore, "msg=\"config loaded\"",
                ("path", "config.toml"));
            bool agentLoaded = ContainsLogLine(text, notBefore, "engine started",
                ("project", projectName), ("agent", expectedAgent));
            bool feishuReady = ContainsLogLine(text, notBefore, "msg=\"platform ready\"",
                ("project", projectName), ("platform", "feishu"));
            bool running = ContainsLogLine(text, notBefore, "msg=\"cc-connect is running\"");
            bool stable = stableSince is not null && _clock() - stableSince.Value >= StableGenerationWindow;
            if (stable && configLoaded && agentLoaded && feishuReady && running)
            {
                return new CcConnectDaemonRestartResult(
                    true,
                    $"配置已提交,cc-connect 新进程换代已验证:agent={expectedAgent},Feishu 已就绪,PID={current.LockPid}。",
                    baseline.LockPid,
                    current.LockPid,
                    expectedAgent,
                    metadata.LogPath,
                    ConfigWritten: true,
                    Phase: "ready");
            }

            _delay(PollInterval);
        }

        return Failed("verify",
                "配置已提交,但未在同一稳定锁 PID 代次内同时验证到新启动时间、目标 agent 与本次启动日志。",
            baseline.LockPid, current.LockPid, expectedAgent, metadata.LogPath, true);
    }

    private bool CheckConsumers(int allowedPid, out string? error)
    {
        ConsumerGuardResult guard = _guardFactory().Check(
            feishuPlatformConfigured: true,
            allowedCcConnectPids: new HashSet<int> { allowedPid });
        error = guard.CanStart ? null : "单消费者检查未通过:" + guard.Reason;
        return guard.CanStart;
    }

    private bool RearmAndVerifyTask(
        CcConnectDaemonMetadata metadata,
        CcConnectScheduledTaskSnapshot baselineTask,
        bool baselineOwnedByTask,
        int? currentPid,
        TimeSpan timeout,
        out CcConnectCommandResult command)
    {
        DateTimeOffset deadline = _clock() + timeout;
        DateTimeOffset? stableSince = null;
        bool startRequested = false;
        bool observedStopped = false;
        command = new CcConnectCommandResult(0, string.Empty);
        while (_clock() <= deadline)
        {
            CcConnectScheduledTaskSnapshot task = _taskSnapshot();
            try
            {
                ValidateScheduledTask(metadata, task);
            }
            catch (Exception ex)
            {
                command = new CcConnectCommandResult(-1, ex.Message);
                return false;
            }

            if (task.State == CcConnectScheduledTaskState.Running)
            {
                bool newerInstance = observedStopped && startRequested &&
                    task.LastRunTime is not null &&
                    (baselineTask.LastRunTime is null || task.LastRunTime > baselineTask.LastRunTime);
                bool sameTaskOwnsNewProcess = currentPid is int pid &&
                    _taskOwnership(pid, task.LastRunTime) == CcConnectTaskOwnership.Owned;
                bool preexistingWatchdog = !baselineOwnedByTask &&
                    task.LastRunTime == baselineTask.LastRunTime;

                if (newerInstance || sameTaskOwnsNewProcess || preexistingWatchdog)
                {
                    stableSince ??= _clock();
                    if (_clock() - stableSince.Value >= StableGenerationWindow)
                    {
                        return true;
                    }
                }
                else
                {
                    stableSince = null;
                }
            }
            else if (task.State == CcConnectScheduledTaskState.Stopped)
            {
                stableSince = null;
                observedStopped = true;
                if (!startRequested)
                {
                    command = RunForReconciliation(new[] { "daemon", "start" }, CommandTimeout);
                    if (command.ExitCode != 0)
                    {
                        return false;
                    }
                    startRequested = true;
                }
            }
            else if (task.State == CcConnectScheduledTaskState.Queued)
            {
                stableSince = null;
            }
            else if (task.State is CcConnectScheduledTaskState.Disabled or
                CcConnectScheduledTaskState.Missing or CcConnectScheduledTaskState.Unknown)
            {
                return false;
            }

            _delay(PollInterval);
        }

        return false;
    }

    private string RecoverAfterFailedRestart(
        CcConnectManagementSettings settings,
        CcConnectDaemonMetadata metadata,
        string lockPath,
        CcConnectRuntimeSnapshot baseline,
        CcConnectScheduledTaskSnapshot baselineTask,
        bool baselineOwnedByTask,
        string projectName,
        bool rolledBack,
        TimeSpan timeout)
    {
        if (!rolledBack)
        {
            return string.Empty;
        }

        CcConnectRuntimeSnapshot current = _probeRuntime(settings, lockPath, projectName);
        if (current.Reachable && current.LockPid == baseline.LockPid &&
            current.Version.Equals(SupportedVersion, StringComparison.OrdinalIgnoreCase) &&
            current.Agent.Equals(baseline.Agent, StringComparison.Ordinal))
        {
            try
            {
                CcConnectScheduledTaskSnapshot task = _taskSnapshot();
                ValidateScheduledTask(metadata, task);
                CcConnectTaskOwnership ownership = current.LockPid is int pid
                    ? _taskOwnership(pid, task.LastRunTime)
                    : CcConnectTaskOwnership.Unknown;
                if (task.State == CcConnectScheduledTaskState.Running &&
                    ownership != CcConnectTaskOwnership.Unknown &&
                    current.LockPid is int allowedPid && CheckConsumers(allowedPid, out _))
                {
                    return " 旧进程未切换,磁盘配置、守护与单消费者状态均已恢复,无需再次重启。";
                }
            }
            catch (Exception)
            {
                // 继续走恢复重启;不能把无法验证的旧运行态描述成已恢复。
            }
        }

        LogCursor recoveryCursor = CaptureLogCursor(metadata.LogPath);
        DateTimeOffset recoveryRequestedAt = _clock();
        CcConnectRestartRequestResult request;
        try
        {
            request = current.Reachable
                ? _requestRestart(settings)
                : FromCommand(RunForReconciliation(new[] { "daemon", "start" }, CommandTimeout));
        }
        catch (Exception ex)
        {
            request = new CcConnectRestartRequestResult(
                CcConnectRestartRequestDisposition.Unknown, null, ex.Message);
        }

        CcConnectDaemonRestartResult recovered = WaitForNewGeneration(
            settings, metadata, lockPath, recoveryCursor, current.Reachable ? current : baseline,
            recoveryRequestedAt, projectName, baseline.Agent, timeout);
        if (!recovered.Ok)
        {
            return " 回滚后的运行态恢复未能验证。" + CommandDetail(request);
        }

        if (!RearmAndVerifyTask(metadata, baselineTask, baselineOwnedByTask, recovered.CurrentPid,
                TimeSpan.FromSeconds(20), out CcConnectCommandResult rearm))
        {
            return " 旧配置进程已恢复,但计划任务守护未能验证。" + CommandDetail(rearm);
        }

        CcConnectRuntimeSnapshot finalRuntime = _probeRuntime(settings, lockPath, projectName);
        int? recoveredPid = recovered.CurrentPid;
        bool runtimeRecovered = recoveredPid is not null && finalRuntime.Reachable &&
            finalRuntime.LockPid == recoveredPid &&
            finalRuntime.Version.Equals(SupportedVersion, StringComparison.OrdinalIgnoreCase) &&
            finalRuntime.Agent.Equals(baseline.Agent, StringComparison.Ordinal);
        string? guardError = null;
        if (!runtimeRecovered || !CheckConsumers(recoveredPid!.Value, out guardError))
        {
            return " 旧配置进程曾恢复,但最终运行态或单消费者复核失败:" +
                (guardError ?? finalRuntime.Error ?? "PID/agent 已变化");
        }

        return " 旧配置运行态与计划任务守护均已恢复。";
    }

    private static bool TryRollback(Action rollback, out string error)
    {
        try
        {
            rollback();
            error = string.Empty;
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    private CcConnectCommandResult RunForReconciliation(IReadOnlyList<string> arguments, TimeSpan timeout)
    {
        try
        {
            return _runner(arguments, timeout);
        }
        catch (Exception ex)
        {
            return new CcConnectCommandResult(-1, ex.Message);
        }
    }

    public static CcConnectDaemonMetadata ReadMetadata(string configPath)
    {
        string configDir = Path.GetDirectoryName(Path.GetFullPath(configPath))!;
        string metaPath = Path.Combine(configDir, "daemon.json");
        using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(metaPath));
        return new CcConnectDaemonMetadata(
            RequiredPath(doc.RootElement, "work_dir", metaPath),
            RequiredPath(doc.RootElement, "binary_path", metaPath),
            RequiredPath(doc.RootElement, "log_file", metaPath));
    }

    public static CcConnectManagementSettings ReadManagementSettings(string configPath)
    {
        TomlTable root = TomlSerializer.Deserialize<TomlTable>(File.ReadAllText(configPath))
            ?? new TomlTable();
        if (!root.TryGetValue("management", out object? raw) || raw is not TomlTable management)
        {
            return new CcConnectManagementSettings(false, 0, string.Empty);
        }

        bool enabled = management.TryGetValue("enabled", out object? rawEnabled) && rawEnabled is true;
        int port = management.TryGetValue("port", out object? rawPort) ? Convert.ToInt32(rawPort) : 9820;
        string token = management.TryGetValue("token", out object? rawToken) && rawToken is string text
            ? text
            : string.Empty;
        return new CcConnectManagementSettings(enabled, port, token);
    }

    private static void ValidateMetadata(string configPath, CcConnectDaemonMetadata metadata)
    {
        string expectedConfig = Path.GetFullPath(Path.Combine(metadata.WorkDir, "config.toml"));
        if (!PathsEqual(expectedConfig, configPath))
        {
            throw new InvalidDataException("daemon work_dir 与目标 config.toml 不一致");
        }

        if (!File.Exists(metadata.BinaryPath))
        {
            throw new FileNotFoundException("daemon binary_path 不存在", metadata.BinaryPath);
        }

        string? resolved = CcConnectConfigValidator.TryResolveExe();
        if (string.IsNullOrWhiteSpace(resolved) || !PathsEqual(resolved, metadata.BinaryPath))
        {
            throw new InvalidDataException("daemon binary_path 与当前 PATH 中的 cc-connect.exe 不一致");
        }
    }

    private static void ValidateScheduledTask(
        CcConnectDaemonMetadata metadata,
        CcConnectScheduledTaskSnapshot task)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("cc-connect 计划任务切换仅支持 Windows");
        }

        if (task.State is CcConnectScheduledTaskState.Missing or
            CcConnectScheduledTaskState.Disabled or CcConnectScheduledTaskState.Unknown)
        {
            throw new InvalidDataException(task.Error ?? $"计划任务状态为 {task.State}");
        }

        if (!task.TaskPath.Equals("\\", StringComparison.Ordinal))
        {
            throw new InvalidDataException("cc-connect 计划任务必须位于任务计划程序根路径且只能有一个同名定义");
        }

        if (task.ActionCount != 1 ||
            !task.Execute.Equals("powershell.exe", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("计划任务必须且只能有一个 powershell.exe action");
        }

        string scriptPath = Path.Combine(metadata.WorkDir, "cc-connect-daemon.ps1");
        string expectedArguments =
            $"-WindowStyle Hidden -NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"{scriptPath}\"";
        if (!task.Arguments.Equals(expectedArguments, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("计划任务 action 参数与固定上游 watchdog 不完全一致");
        }

        SecurityIdentifier? currentSid = WindowsIdentity.GetCurrent().User;
        SecurityIdentifier taskSid;
        try
        {
            taskSid = (SecurityIdentifier)new NTAccount(task.UserId)
                .Translate(typeof(SecurityIdentifier));
        }
        catch (Exception ex)
        {
            throw new InvalidDataException("计划任务账号无法解析为 SID", ex);
        }
        if (currentSid is null || !taskSid.Equals(currentSid))
        {
            throw new InvalidDataException("计划任务账号与当前 GUI 用户不一致");
        }

        if (!task.RunLevel.Equals("Limited", StringComparison.OrdinalIgnoreCase) ||
            !task.LogonType.Equals("S4U", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("计划任务 principal 必须是当前用户的 S4U + Limited token");
        }

        if (!task.ExecutionTimeLimit.Equals("PT0S", StringComparison.OrdinalIgnoreCase) ||
            task.DisallowStartIfOnBatteries || task.StopIfGoingOnBatteries ||
            task.RestartCount != 3 ||
            !task.RestartInterval.Equals("PT1M", StringComparison.OrdinalIgnoreCase) ||
            !task.MultipleInstances.Equals("IgnoreNew", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                "计划任务 settings 未满足 PT0S、不断电停止、3 次/1 分钟恢复与 IgnoreNew 的守护契约");
        }

        if (task.TriggerCount != 1 || task.EnabledTriggerCount != 1 ||
            !task.TriggerType.Equals("MSFT_TaskLogonTrigger", StringComparison.Ordinal) ||
            !task.TriggerInterval.Equals("PT5M", StringComparison.OrdinalIgnoreCase) ||
            task.TriggerDuration.Length != 0 || task.TriggerStopAtDurationEnd)
        {
            throw new InvalidDataException(
                "计划任务必须且只能有一个已启用的登录触发器,并按 PT5M 无限期重复");
        }

        SecurityIdentifier triggerSid;
        try
        {
            triggerSid = (SecurityIdentifier)new NTAccount(task.TriggerUserId)
                .Translate(typeof(SecurityIdentifier));
        }
        catch (Exception ex)
        {
            throw new InvalidDataException("计划任务触发器账号无法解析为 SID", ex);
        }
        if (currentSid is null || !triggerSid.Equals(currentSid))
        {
            throw new InvalidDataException("计划任务触发器账号与当前 GUI 用户不一致");
        }

        string script = File.ReadAllText(scriptPath).Replace("\r\n", "\n", StringComparison.Ordinal).TrimEnd('\n');
        string workDirLiteral = PowerShellSingleQuoted(metadata.WorkDir);
        string binaryLiteral = PowerShellSingleQuoted(metadata.BinaryPath);
        string expectedTail = string.Join("\n", new[]
        {
            $"Set-Location -LiteralPath {workDirLiteral}",
            "while ($true) {",
            $"  & {binaryLiteral}",
            "  $exitCode = $LASTEXITCODE",
            "  if ($exitCode -eq 0) { exit 0 }",
            "  Start-Sleep -Seconds 10",
            "}",
        });
        if (!script.EndsWith(expectedTail, StringComparison.Ordinal))
        {
            throw new InvalidDataException("cc-connect-daemon.ps1 与固定上游 watchdog 语义不一致");
        }

        string prefix = script[..^expectedTail.Length].TrimEnd('\n');
        string[] prefixLines = prefix.Split('\n');
        if (prefixLines.Length < 4 ||
            !prefixLines[0].Equals("$ErrorActionPreference = 'Stop'", StringComparison.Ordinal))
        {
            throw new InvalidDataException("cc-connect-daemon.ps1 前缀不是固定上游环境声明");
        }

        var environment = new Dictionary<string, string>(StringComparer.Ordinal);
        var envLine = new Regex("^\\$env:([A-Za-z_][A-Za-z0-9_]*) = '(?:[^']|'')*'$", RegexOptions.CultureInvariant);
        foreach (string line in prefixLines.Skip(1))
        {
            Match match = envLine.Match(line);
            if (!match.Success)
            {
                throw new InvalidDataException("cc-connect-daemon.ps1 含非环境赋值的额外前置命令");
            }
            string value = line[(line.IndexOf('=', StringComparison.Ordinal) + 1)..].Trim();
            environment[match.Groups[1].Value] = value[1..^1].Replace("''", "'", StringComparison.Ordinal);
        }

        if (!environment.ContainsKey("CC_LOG_FILE") || !environment.ContainsKey("CC_LOG_MAX_SIZE") ||
            !environment.ContainsKey("CC_LOG_MAX_BACKUPS"))
        {
            throw new InvalidDataException("cc-connect-daemon.ps1 缺少固定上游日志环境变量");
        }

        if (!environment.TryGetValue("AI_RESUME_INTERNAL_RUN", out string? internalRun) || internalRun != "1")
        {
            throw new InvalidDataException(
                "cc-connect-daemon.ps1 必须设置 AI_RESUME_INTERNAL_RUN=1,否则飞书任务会重复触发本地完成通知");
        }
    }

    private static string PowerShellSingleQuoted(string value) =>
        "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";

    private static string RequiredPath(JsonElement root, string property, string metaPath)
    {
        if (!root.TryGetProperty(property, out JsonElement value) ||
            value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(value.GetString()))
        {
            throw new InvalidDataException($"{metaPath} 缺少 {property}");
        }

        return Path.GetFullPath(value.GetString()!);
    }

    private static CcConnectRuntimeSnapshot ProbeRuntimeDefault(
        CcConnectManagementSettings settings,
        string lockPath,
        string projectName)
    {
        CcConnectLockResult lockResult = ReadLockDefault(lockPath);
        if (lockResult.State != CcConnectLockState.Found)
        {
            return new CcConnectRuntimeSnapshot(false, lockResult.Pid, 0, string.Empty, string.Empty,
                lockResult.Error ?? "实例锁不存在", lockResult.WrittenAt);
        }

        try
        {
            using HttpClient client = CreateManagementClient(settings);
            using JsonDocument status = GetJson(client, "status");
            using JsonDocument projects = GetJson(client, "projects");
            CcConnectLockResult lockAfter = ReadLockDefault(lockPath);
            if (lockAfter.State != CcConnectLockState.Found || lockAfter.Pid != lockResult.Pid)
            {
                return new CcConnectRuntimeSnapshot(false, lockAfter.Pid, 0, string.Empty, string.Empty,
                    "读取管理 API 期间实例锁 PID 发生变化", lockAfter.WrittenAt);
            }
            JsonElement statusData = status.RootElement.GetProperty("data");
            JsonElement projectArray = projects.RootElement.GetProperty("data").GetProperty("projects");
            string agent = string.Empty;
            foreach (JsonElement project in projectArray.EnumerateArray())
            {
                if (project.TryGetProperty("name", out JsonElement name) &&
                    name.GetString()?.Equals(projectName, StringComparison.Ordinal) == true)
                {
                    agent = project.TryGetProperty("agent_type", out JsonElement type)
                        ? type.GetString() ?? string.Empty
                        : string.Empty;
                    break;
                }
            }

            return new CcConnectRuntimeSnapshot(
                true,
                lockResult.Pid,
                statusData.GetProperty("uptime_seconds").GetInt64(),
                statusData.GetProperty("version").GetString() ?? string.Empty,
                agent,
                null,
                lockAfter.WrittenAt);
        }
        catch (Exception ex)
        {
            return new CcConnectRuntimeSnapshot(
                false, lockResult.Pid, 0, string.Empty, string.Empty, ex.Message, lockResult.WrittenAt);
        }
    }

    private static CcConnectRestartRequestResult RequestRestartDefault(CcConnectManagementSettings settings)
    {
        try
        {
            using HttpClient client = CreateManagementClient(settings);
            using var content = new StringContent("{}", Encoding.UTF8, "application/json");
            using HttpResponseMessage response = client.PostAsync("restart", content).GetAwaiter().GetResult();
            string body = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            CcConnectRestartRequestDisposition disposition = response.IsSuccessStatusCode
                ? CcConnectRestartRequestDisposition.Accepted
                : response.StatusCode == System.Net.HttpStatusCode.Conflict ||
                  (int)response.StatusCode >= 500
                    ? CcConnectRestartRequestDisposition.Unknown
                    : CcConnectRestartRequestDisposition.Rejected;
            return new CcConnectRestartRequestResult(disposition, (int)response.StatusCode, body);
        }
        catch (Exception ex)
        {
            return new CcConnectRestartRequestResult(
                CcConnectRestartRequestDisposition.Unknown, null, ex.Message);
        }
    }

    private static HttpClient CreateManagementClient(CcConnectManagementSettings settings)
    {
        var handler = new SocketsHttpHandler { UseProxy = false };
        var client = new HttpClient(handler)
        {
            BaseAddress = new Uri($"http://127.0.0.1:{settings.Port}/api/v1/"),
            Timeout = TimeSpan.FromSeconds(5),
        };
        if (settings.Token.Length > 0)
        {
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", settings.Token);
        }

        return client;
    }

    private static JsonDocument GetJson(HttpClient client, string path)
    {
        using HttpResponseMessage response = client.GetAsync(path).GetAwaiter().GetResult();
        string body = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
        response.EnsureSuccessStatusCode();
        return JsonDocument.Parse(body);
    }

    private static CcConnectLockResult ReadLockDefault(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return new CcConnectLockResult(CcConnectLockState.Missing, null, "实例锁不存在");
            }

            string text;
            using (var stream = new FileStream(
                       path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true))
            {
                text = reader.ReadToEnd().Trim();
            }
            return int.TryParse(text, out int pid) && pid > 0
                ? new CcConnectLockResult(
                    CcConnectLockState.Found,
                    pid,
                    WrittenAt: new DateTimeOffset(File.GetLastWriteTimeUtc(path), TimeSpan.Zero))
                : new CcConnectLockResult(CcConnectLockState.Invalid, null, "实例锁不是有效 PID");
        }
        catch (Exception ex)
        {
            return new CcConnectLockResult(CcConnectLockState.Unreadable, null, ex.Message);
        }
    }

    private static CcConnectScheduledTaskSnapshot QueryScheduledTaskDefault()
    {
        if (!OperatingSystem.IsWindows())
        {
            return new CcConnectScheduledTaskSnapshot(
                CcConnectScheduledTaskState.Unknown, null, string.Empty, 0, string.Empty, string.Empty,
                string.Empty, string.Empty, string.Empty, string.Empty, false, false, 0,
                string.Empty, string.Empty, 0, 0, string.Empty, string.Empty, string.Empty,
                string.Empty, false, "仅支持 Windows 计划任务");
        }

        const string script = "$tasks=@(Get-ScheduledTask -TaskName 'cc-connect' -ErrorAction SilentlyContinue);" +
            "if($tasks.Count -eq 0){[pscustomobject]@{state='Missing'}|ConvertTo-Json -Compress;exit};" +
            "if($tasks.Count -ne 1){[pscustomobject]@{state='Unknown';error='存在多个同名计划任务'}|ConvertTo-Json -Compress;exit};" +
            "$t=$tasks[0];" +
            "$i=Get-ScheduledTaskInfo -TaskName 'cc-connect';$a=@($t.Actions);$g=@($t.Triggers);" +
            "$eg=@($g|Where-Object{$_.Enabled});$first=if($g.Count -gt 0){$g[0]}else{$null};" +
            "$state=if(-not $t.Settings.Enabled){'Disabled'}else{$t.State.ToString()};" +
            "[pscustomobject]@{state=$state;lastRunTime=$i.LastRunTime.ToUniversalTime().ToString('o');taskPath=$t.TaskPath;" +
            "actionCount=$a.Count;execute=if($a.Count -gt 0){$a[0].Execute}else{''};" +
            "arguments=if($a.Count -gt 0){$a[0].Arguments}else{''};userId=$t.Principal.UserId;" +
            "logonType=$t.Principal.LogonType.ToString();runLevel=$t.Principal.RunLevel.ToString();" +
            "executionTimeLimit=[string]$t.Settings.ExecutionTimeLimit;" +
            "disallowStartIfOnBatteries=[bool]$t.Settings.DisallowStartIfOnBatteries;" +
            "stopIfGoingOnBatteries=[bool]$t.Settings.StopIfGoingOnBatteries;" +
            "restartCount=[int]$t.Settings.RestartCount;restartInterval=[string]$t.Settings.RestartInterval;" +
            "multipleInstances=$t.Settings.MultipleInstances.ToString();triggerCount=$g.Count;" +
            "enabledTriggerCount=$eg.Count;triggerType=if($null -ne $first){$first.CimClass.CimClassName}else{''};" +
            "triggerUserId=if($null -ne $first){[string]$first.UserId}else{''};" +
            "triggerInterval=if($null -ne $first){[string]$first.Repetition.Interval}else{''};" +
            "triggerDuration=if($null -ne $first){[string]$first.Repetition.Duration}else{''};" +
            "triggerStopAtDurationEnd=if($null -ne $first){[bool]$first.Repetition.StopAtDurationEnd}else{$false}}" +
            "|ConvertTo-Json -Compress";
        try
        {
            var psi = new ProcessStartInfo("powershell.exe")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            psi.ArgumentList.Add("-NoProfile");
            psi.ArgumentList.Add("-NonInteractive");
            psi.ArgumentList.Add("-Command");
            psi.ArgumentList.Add(script);
            using Process process = Process.Start(psi)!;
            string output = process.StandardOutput.ReadToEnd();
            if (!process.WaitForExit(5000))
            {
                try { process.Kill(entireProcessTree: true); } catch (Exception) { }
                return UnknownTask("读取 cc-connect 计划任务超时");
            }

            using JsonDocument doc = JsonDocument.Parse(output);
            JsonElement root = doc.RootElement;
            CcConnectScheduledTaskState state = root.GetProperty("state").GetString() switch
            {
                "Running" => CcConnectScheduledTaskState.Running,
                "Queued" => CcConnectScheduledTaskState.Queued,
                "Ready" => CcConnectScheduledTaskState.Stopped,
                "Disabled" => CcConnectScheduledTaskState.Disabled,
                "Missing" => CcConnectScheduledTaskState.Missing,
                _ => CcConnectScheduledTaskState.Unknown,
            };
            DateTimeOffset? lastRun = null;
            if (root.TryGetProperty("lastRunTime", out JsonElement rawLastRun) &&
                DateTimeOffset.TryParse(rawLastRun.GetString(), out DateTimeOffset parsed) &&
                parsed.Year > 2000)
            {
                lastRun = parsed;
            }

            return new CcConnectScheduledTaskSnapshot(
                state,
                lastRun,
                JsonString(root, "taskPath"),
                root.TryGetProperty("actionCount", out JsonElement count) ? count.GetInt32() : 0,
                JsonString(root, "execute"),
                JsonString(root, "arguments"),
                JsonString(root, "userId"),
                JsonString(root, "logonType"),
                JsonString(root, "runLevel"),
                JsonString(root, "executionTimeLimit"),
                JsonBool(root, "disallowStartIfOnBatteries"),
                JsonBool(root, "stopIfGoingOnBatteries"),
                JsonInt(root, "restartCount"),
                JsonString(root, "restartInterval"),
                JsonString(root, "multipleInstances"),
                JsonInt(root, "triggerCount"),
                JsonInt(root, "enabledTriggerCount"),
                JsonString(root, "triggerType"),
                JsonString(root, "triggerUserId"),
                JsonString(root, "triggerInterval"),
                JsonString(root, "triggerDuration"),
                JsonBool(root, "triggerStopAtDurationEnd"),
                JsonString(root, "error"));
        }
        catch (Exception ex)
        {
            return UnknownTask(ex.Message);
        }
    }

    private static CcConnectScheduledTaskSnapshot UnknownTask(string error) => new(
        CcConnectScheduledTaskState.Unknown, null, string.Empty, 0, string.Empty, string.Empty,
        string.Empty, string.Empty, string.Empty, string.Empty, false, false, 0,
        string.Empty, string.Empty, 0, 0, string.Empty, string.Empty, string.Empty,
        string.Empty, false, error);

    private static string JsonString(JsonElement root, string name) =>
        root.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;

    private static bool JsonBool(JsonElement root, string name) =>
        root.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.True;

    private static int JsonInt(JsonElement root, string name) =>
        root.TryGetProperty(name, out JsonElement value) && value.TryGetInt32(out int parsed)
            ? parsed
            : 0;

    private static CcConnectTaskOwnership GetTaskOwnershipDefault(int pid, DateTimeOffset? taskLastRunTime)
    {
        if (!OperatingSystem.IsWindows() || taskLastRunTime is null)
        {
            return CcConnectTaskOwnership.Unknown;
        }

        try
        {
            var parents = new Dictionary<int, int>();
            var taskRoots = new HashSet<int>();
            using var searcher = new ManagementObjectSearcher(
                "SELECT ProcessId, ParentProcessId, Name, CreationDate FROM Win32_Process");
            using ManagementObjectCollection processes = searcher.Get();
            foreach (ManagementBaseObject item in processes)
            {
                using (item)
                {
                    int processId = Convert.ToInt32(item["ProcessId"]);
                    parents[processId] = Convert.ToInt32(item["ParentProcessId"]);
                    string name = item["Name"] as string ?? string.Empty;
                    if (!name.Equals("powershell.exe", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    string? creation = item["CreationDate"] as string;
                    if (creation is null)
                    {
                        continue;
                    }

                    DateTime createdLocal = ManagementDateTimeConverter.ToDateTime(creation);
                    var created = new DateTimeOffset(createdLocal).ToUniversalTime();
                    if (Math.Abs((created - taskLastRunTime.Value.ToUniversalTime()).TotalSeconds) <= 5)
                    {
                        taskRoots.Add(processId);
                    }
                }
            }

            int current = pid;
            var visited = new HashSet<int>();
            while (current > 0 && visited.Add(current))
            {
                if (taskRoots.Contains(current))
                {
                    return CcConnectTaskOwnership.Owned;
                }
                if (!parents.TryGetValue(current, out current))
                {
                    break;
                }
            }

            return taskRoots.Count > 0
                ? CcConnectTaskOwnership.NotOwned
                : CcConnectTaskOwnership.Unknown;
        }
        catch (Exception)
        {
            return CcConnectTaskOwnership.Unknown;
        }
    }

    private static CcConnectCommandResult RunDefault(IReadOnlyList<string> arguments, TimeSpan timeout)
    {
        string exe = CcConnectConfigValidator.TryResolveExe()
            ?? throw new FileNotFoundException("本机找不到 cc-connect.exe。");
        var psi = new ProcessStartInfo(exe)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetTempPath(),
        };
        foreach (string argument in arguments)
        {
            psi.ArgumentList.Add(argument);
        }

        using Process process = Process.Start(psi)
            ?? throw new InvalidOperationException("cc-connect daemon 命令未能启动。");
        Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync();
        Task<string> stderrTask = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit((int)timeout.TotalMilliseconds))
        {
            try { process.Kill(entireProcessTree: true); } catch (Exception) { }
            throw new TimeoutException("cc-connect daemon 命令超时。");
        }

        return new CcConnectCommandResult(
            process.ExitCode,
            (stdoutTask.GetAwaiter().GetResult() + "\n" + stderrTask.GetAwaiter().GetResult()).Trim());
    }

    private static LogCursor CaptureLogCursor(string path)
    {
        try
        {
            var info = new FileInfo(path);
            return info.Exists ? new LogCursor(info.Length, info.CreationTimeUtc.Ticks) : new LogCursor(0, 0);
        }
        catch (Exception)
        {
            return new LogCursor(0, 0);
        }
    }

    private static string ReadNewLog(string path, ref LogCursor cursor)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            long creationTicks = File.GetCreationTimeUtc(path).Ticks;
            if (stream.Length < cursor.Offset ||
                (cursor.CreationTicks != 0 && creationTicks != cursor.CreationTicks))
            {
                cursor = new LogCursor(0, creationTicks);
            }

            stream.Seek(cursor.Offset, SeekOrigin.Begin);
            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, leaveOpen: true);
            string text = reader.ReadToEnd();
            cursor = new LogCursor(stream.Position, creationTicks);
            return text;
        }
        catch (Exception)
        {
            return string.Empty;
        }
    }

    private static bool ContainsLogLine(
        string text,
        DateTimeOffset notBefore,
        string marker,
        params (string Name, string Value)[] fields)
    {
        foreach (string line in text.Replace("\r\n", "\n").Split('\n'))
        {
            if (!line.Contains(marker, StringComparison.Ordinal) || !TryReadLogTime(line, out DateTimeOffset timestamp) ||
                timestamp < notBefore)
            {
                continue;
            }

            bool allFieldsMatch = fields.All(field =>
                line.Contains($" {field.Name}={field.Value}", StringComparison.Ordinal) ||
                line.Contains($" {field.Name}=\"{field.Value}\"", StringComparison.Ordinal));
            if (allFieldsMatch)
            {
                return true;
            }
        }

        return false;
    }

    private static bool TryReadLogTime(string line, out DateTimeOffset timestamp)
    {
        timestamp = default;
        int start = line.IndexOf("time=", StringComparison.Ordinal);
        if (start < 0) return false;
        start += 5;
        int end = line.IndexOf(' ', start);
        string value = (end < 0 ? line[start..] : line[start..end]).Trim('"');
        return DateTimeOffset.TryParse(value, out timestamp);
    }

    private static CcConnectDaemonRestartResult Failed(
        string phase,
        string message,
        int? previousPid,
        int? currentPid,
        string expectedAgent,
        string logPath,
        bool configWritten)
        => new(false, message, previousPid, currentPid, expectedAgent, logPath, configWritten, phase);

    private static string CommandDetail(CcConnectCommandResult result) =>
        result.ExitCode == 0 && string.IsNullOrWhiteSpace(result.Output)
            ? string.Empty
            : $" 命令结果(exit={result.ExitCode}):{FirstMeaningfulLine(result.Output)}";

    private static string CommandDetail(CcConnectRestartRequestResult result) =>
        $" 重启请求({result.Disposition}" +
        (result.StatusCode is int status ? $",HTTP {status}" : string.Empty) +
        $"):{FirstMeaningfulLine(result.Output)}";

    private static CcConnectRestartRequestResult FromCommand(CcConnectCommandResult result) => new(
        result.ExitCode == 0
            ? CcConnectRestartRequestDisposition.Accepted
            : CcConnectRestartRequestDisposition.Unknown,
        null,
        result.Output);

    private static string FirstMeaningfulLine(string? output)
    {
        foreach (string line in (output ?? string.Empty).Replace("\r\n", "\n").Split('\n'))
        {
            string trimmed = line.Trim();
            if (trimmed.Length > 0) return trimmed;
        }
        return "无输出";
    }

    private static string SafeError(string? error) => string.IsNullOrWhiteSpace(error) ? string.Empty : " " + error;

    private static bool PathsEqual(string left, string right) =>
        string.Equals(
            Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private readonly record struct LogCursor(long Offset, long CreationTicks);
}

public sealed class CcConnectApplyLock : IDisposable
{
    private readonly FileStream _stream;
    private readonly string _path;
    private bool _disposed;

    private CcConnectApplyLock(FileStream stream, string path)
    {
        _stream = stream;
        _path = path;
    }

    public static CcConnectApplyLock Acquire(string configPath)
    {
        string directory = Path.GetDirectoryName(Path.GetFullPath(configPath))!;
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, ".ai-resume-cutover.lock");
        try
        {
            return new CcConnectApplyLock(
                new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None), path);
        }
        catch (IOException ex)
        {
            throw new InvalidOperationException("另一个 AI Resume 窗口正在生成配置或重启 cc-connect。", ex);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _stream.Dispose();
        try { File.Delete(_path); } catch (Exception) { }
    }
}
