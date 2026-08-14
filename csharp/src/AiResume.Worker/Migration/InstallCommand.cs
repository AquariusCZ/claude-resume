using AiResume.Worker.Notifications;
using AiResume.Worker.Products;
using AiResume.Ipc;
using Microsoft.Win32.SafeHandles;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace AiResume.Worker.Migration;

/// <summary>
/// <c>AiResume.Worker.exe install [--target &lt;dir&gt;] [--from &lt;buildRoot&gt;]</c>
/// 与 <c>uninstall</c>。
///
/// **为什么必须有这一层**:此前桌面快捷方式、开始菜单、开机自启和 Claude Code 的
/// Stop 钩子全都指向 <c>…\csharp\src\AiResume.Gui\bin\Debug\net10.0-windows\…</c>
/// ——开发构建目录。清一次 bin、换个分支重新构建、或者把仓库目录改个名,
/// 这些入口就全断了。其中 Stop 钩子断得**没有任何报错**:界面照样显示"已启用",
/// 只是通知永远不到(2026-08-07 已因同类问题排查过一次)。
///
/// 旧系统当年是对的——产物装在 <c>%LOCALAPPDATA%\ClaudeResume\</c>,
/// 所有入口指向那里,仓库怎么动都不影响。迁移时把这一层丢了,现在补回来。
///
/// 安装后仓库只是源码;运行的是安装目录里的副本。改动代码后要重新 <c>install</c> 才生效。
/// </summary>
public static class InstallCommand
{
    public sealed record ActivationResult(bool WorkerReady, int ShortcutExitCode, bool HooksOk);
    private const string OwnershipMarkerName = ".ai-resume-install-root";
    private const string OwnershipMarkerContent = "AI Resume v2 install root\n";
    private const string PayloadManifestName = ".ai-resume-install-manifest";
    private const string PreservedRootMarkerName = ".ai-resume-preserved-root";
    private const string PreservedRootMarkerContent = "AI Resume v2 preserved root\n";
    private const string UninstallHelperPrefix = ".airesume-uninstall-";
    private const int InstallLockTimeoutSeconds = 120;
    private const int InstallHandoffTimeoutSeconds = 15;
    private const uint FileReadAttributes = 0x0080;
    private const uint FileShareRead = 0x00000001;
    private const uint FileShareWrite = 0x00000002;
    private const uint FileShareDelete = 0x00000004;
    private const uint OpenExisting = 3;
    private const uint FileFlagBackupSemantics = 0x02000000;
    private const uint VolumeNameNt = 0x00000002;

    /// <summary>安装目标。与旧系统同层级,便于用户按同一心智找它。</summary>
    public static string DefaultTarget => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AI Resume");

    /// <summary>需要装进目标目录的项目(输出目录里的全部文件合并到同一层)。</summary>
    // Launcher 是开机自启的无窗口垫片(见 WorkerAutostart),必须随产物一起装进去,
    // 否则自启会退化成直接拉起控制台程序、每次登录弹黑框。
    private static readonly string[] Projects =
        ["AiResume.Gui", "AiResume.Worker", "AiResume.Hook", "AiResume.Launcher"];

    public static int Run(string[] args)
    {
        bool uninstall = args.Any(a => string.Equals(a, "uninstall", StringComparison.OrdinalIgnoreCase));
        string target = ReadOption(args, "--target") ?? DefaultTarget;

        try
        {
            if (uninstall && args.Any(a => string.Equals(a, "--helper", StringComparison.OrdinalIgnoreCase)))
            {
                return RunUninstallHelper(args, target);
            }
            return uninstall ? Uninstall(target) : Install(args, target);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"失败:{ex.Message}");
            return 1;
        }
    }

    private static int Install(string[] args, string target)
    {
        using IDisposable operationLease = AcquireOperationLease(target);
        IReadOnlyList<string> sources = ResolveSources(ReadOption(args, "--from"));
        if (sources.Count == 0)
        {
            Console.Error.WriteLine("找不到任何构建产物。请先 dotnet build,或用 --from 指定 src 根目录。");
            return 1;
        }
        string fullTarget = ValidateInstallTarget(target, EnumeratePayloadRelativeFiles(sources));

        string parent = Directory.GetParent(fullTarget)?.FullName
                        ?? throw new InvalidOperationException($"安装目录没有可用父目录: {fullTarget}");
        string operationId = Guid.NewGuid().ToString("N");
        string stage = Path.Combine(parent, ".airesume-install-" + operationId);
        string backup = Path.Combine(parent, ".airesume-backup-" + operationId);
        bool runtimeTouched = false;
        bool preserveRecoveryArtifacts = false;
        IReadOnlyList<string> obsoletePayload = Array.Empty<string>();
        bool hadWorker = IsRunningIn(fullTarget, "AiResume.Worker");
        string guiExe = Path.Combine(fullTarget, "AiResume.Gui.exe");
        string workerExe = Path.Combine(fullTarget, "AiResume.Worker.exe");
        string hookExe = Path.Combine(fullTarget, HookExecutable.FileName);

        try
        {
            Directory.CreateDirectory(stage);
            int files = 0;
            foreach (string src in sources)
            {
                files += CopyTree(src, stage);
            }
            WritePayloadManifest(stage);
            WriteOwnershipMarker(stage);

            ValidateStagedRuntime(stage);
            Directory.CreateDirectory(backup);
            obsoletePayload = FindObsoletePayload(fullTarget, stage);
            BackupPayload(stage, fullTarget, backup, obsoletePayload);
            IReadOnlyDictionary<string, string> stagedHashes = CapturePayloadHashes(stage);

            // 源文件读取、staging 和旧版备份都完成后才停服务。
            // 这样磁盘/权限/构建产物错误不会先制造通知消费者停机。
            StopRunningIn(fullTarget);
            Directory.CreateDirectory(fullTarget);
            runtimeTouched = true;
            CopyTree(stage, fullTarget, rejectReparse: true, stagedHashes.Keys);
            VerifyPayloadHashes(fullTarget, stagedHashes);
            DeleteObsoletePayload(fullTarget, obsoletePayload);

            Console.WriteLine($"已安装 {files} 个文件到 {fullTarget}");

            if (!File.Exists(guiExe) || !File.Exists(workerExe) || !File.Exists(hookExe))
            {
                throw new InvalidOperationException("安装目录里缺少 GUI、Worker 或 Hook,拒绝继续创建入口。");
            }

            ActivationResult activation = ActivateInstalledVersion(
                () => StartInstalledWorker(workerExe, fullTarget),
                () => ShortcutCommand.Run([
                    "shortcuts", "--gui", guiExe, "--worker", workerExe,
                    "--icon", Path.Combine(fullTarget, "icon.ico")]),
                () => ReconcileHooks(hookExe));
            if (!activation.WorkerReady)
            {
                Console.Error.WriteLine("注意:后台 Worker 未通过 Named Pipe 就绪核验;正在恢复旧运行版本。");
                if (!RollbackRuntime(stage, backup, fullTarget, hadWorker, obsoletePayload))
                {
                    preserveRecoveryArtifacts = true;
                    return 4;
                }
                runtimeTouched = false;
                return 3;
            }

            // 外部入口只能在新 Worker 真实就绪后提交。否则运行文件虽然回滚了,
            // 快捷方式与用户级 Hook 却会继续指向已经删除的新版本。
            int rc = activation.ShortcutExitCode;
            if (rc != 0)
            {
                if (!RollbackRuntime(stage, backup, fullTarget, hadWorker, obsoletePayload))
                {
                    preserveRecoveryArtifacts = true;
                    return 4;
                }
                runtimeTouched = false;
                return rc;
            }

            bool hooksOk = activation.HooksOk;
            File.Delete(Path.Combine(fullTarget, PreservedRootMarkerName));

            Console.WriteLine();
            Console.WriteLine("入口已全部指向安装目录,与仓库路径脱钩(改名/清 bin/换分支都不再影响)。");
            Console.WriteLine("改动代码后需重新运行 install 才生效。");

            // 通知源没对齐就**不要报告成功**。退出码 0 加一句"入口已全部指向安装目录"
            // 正是审计 B3 里那条骗人的输出:命令说成功,五个通知源全是关的。
            if (!hooksOk)
            {
                Console.Error.WriteLine("注意:部分通知源未能启用(见上方警告),完成通知可能收不到。");
            }

            if (!hooksOk)
            {
                return 2;
            }

            return 0;
        }
        catch
        {
            if (runtimeTouched)
            {
                preserveRecoveryArtifacts = !RollbackRuntime(
                    stage, backup, fullTarget, hadWorker, obsoletePayload);
            }
            throw;
        }
        finally
        {
            if (preserveRecoveryArtifacts)
            {
                Console.Error.WriteLine($"警告:回滚未完整成功,已保留恢复材料: {stage} ; {backup}");
            }
            else
            {
                TryDeleteOperationDirectory(stage, parent, ".airesume-install-");
                TryDeleteOperationDirectory(backup, parent, ".airesume-backup-");
            }
        }
    }

    /// <summary>按物理规范目标目录串行化完整安装/卸载事务。</summary>
    public static IDisposable AcquireOperationLease(string target, TimeSpan? timeout = null)
    {
        OperationLockNames names = GetOperationLockNames(target);
        TimeSpan wait = NormalizeOperationLockTimeout(timeout);
        using MutexLease gate = AcquireMutex(
            names.Gate,
            wait,
            "等待同一安装目录的事务门闩超时，未修改运行文件。");
        return AcquireMutex(
            names.Operation,
            wait,
            "等待同一安装目录的既有安装或卸载事务超时，未修改运行文件。");
    }

    /// <summary>父卸载进程持有门闩和事务锁，直到临时 Worker 已准备好接管。</summary>
    public static OperationHandoffLease AcquireOperationHandoffLease(
        string target,
        TimeSpan? timeout = null)
    {
        OperationLockNames names = GetOperationLockNames(target);
        TimeSpan wait = NormalizeOperationLockTimeout(timeout);
        MutexLease gate = AcquireMutex(
            names.Gate,
            wait,
            "等待同一安装目录的事务门闩超时，未启动卸载。");
        try
        {
            MutexLease operation = AcquireMutex(
                names.Operation,
                wait,
                "等待同一安装目录的既有安装或卸载事务超时，未启动卸载。");
            return new OperationHandoffLease(gate, operation);
        }
        catch
        {
            gate.Dispose();
            throw;
        }
    }

    /// <summary>卸载 helper 在父进程仍持有门闩时直接接管事务锁。</summary>
    public static IDisposable AcquireTransferredOperationLease(
        string target,
        TimeSpan? timeout = null)
    {
        OperationLockNames names = GetOperationLockNames(target);
        return AcquireMutex(
            names.Operation,
            NormalizeOperationLockTimeout(timeout),
            "等待父进程移交安装目录事务锁超时，未执行卸载。");
    }

    private static TimeSpan NormalizeOperationLockTimeout(TimeSpan? timeout)
    {
        TimeSpan result = timeout ?? TimeSpan.FromSeconds(InstallLockTimeoutSeconds);
        if (result <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout));
        }

        return result;
    }

    private static MutexLease AcquireMutex(string name, TimeSpan timeout, string timeoutMessage)
    {
        var mutex = new Mutex(initiallyOwned: false, name);
        bool acquired;
        try
        {
            acquired = mutex.WaitOne(timeout);
        }
        catch (AbandonedMutexException)
        {
            acquired = true;
        }

        if (!acquired)
        {
            mutex.Dispose();
            throw new TimeoutException(timeoutMessage);
        }

        return new MutexLease(mutex);
    }

    private static OperationLockNames GetOperationLockNames(string target)
    {
        string canonicalTarget = GetCanonicalOperationTarget(target);
        string targetHash = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(canonicalTarget)));
        return new OperationLockNames(
            @"Global\AIResume.InstallGate." + targetHash,
            @"Global\AIResume.Install." + targetHash);
    }

    private static string GetCanonicalOperationTarget(string target)
    {
        string full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(target));
        string existing = full;
        var missingSegments = new Stack<string>();
        while (!Directory.Exists(existing))
        {
            string segment = Path.GetFileName(existing);
            DirectoryInfo? parent = Directory.GetParent(existing);
            if (segment.Length == 0 || parent is null)
            {
                throw new DirectoryNotFoundException($"无法确定安装锁的现有父目录:{full}");
            }

            missingSegments.Push(segment);
            existing = parent.FullName;
        }

        string canonical = GetFinalDirectoryPath(existing).TrimEnd('\\');
        while (missingSegments.TryPop(out string? segment))
        {
            canonical += "\\" + segment;
        }

        return canonical.ToUpperInvariant();
    }

    private static string GetFinalDirectoryPath(string path)
    {
        using SafeFileHandle handle = CreateFileW(
            path,
            FileReadAttributes,
            FileShareRead | FileShareWrite | FileShareDelete,
            IntPtr.Zero,
            OpenExisting,
            FileFlagBackupSemantics,
            IntPtr.Zero);
        if (handle.IsInvalid)
        {
            throw new IOException(
                $"无法打开安装目录以建立唯一事务锁:{path}",
                Marshal.GetExceptionForHR(Marshal.GetHRForLastWin32Error()));
        }

        var buffer = new StringBuilder(512);
        uint length = GetFinalPathNameByHandleW(handle, buffer, (uint)buffer.Capacity, VolumeNameNt);
        if (length >= buffer.Capacity)
        {
            buffer.EnsureCapacity(checked((int)length + 1));
            length = GetFinalPathNameByHandleW(handle, buffer, (uint)buffer.Capacity, VolumeNameNt);
        }

        if (length == 0 || length >= buffer.Capacity)
        {
            throw new IOException(
                $"无法规范化安装目录事务锁路径:{path}",
                Marshal.GetExceptionForHR(Marshal.GetHRForLastWin32Error()));
        }

        return buffer.ToString();
    }

    public sealed class OperationHandoffLease : IDisposable
    {
        private MutexLease? _gate;
        private MutexLease? _operation;

        internal OperationHandoffLease(MutexLease gate, MutexLease operation)
        {
            _gate = gate;
            _operation = operation;
        }

        public void ReleaseOperation() =>
            Interlocked.Exchange(ref _operation, null)?.Dispose();

        public void ReleaseGate() =>
            Interlocked.Exchange(ref _gate, null)?.Dispose();

        public void Dispose()
        {
            ReleaseOperation();
            ReleaseGate();
        }
    }

    internal sealed class MutexLease : IDisposable
    {
        private Mutex? _mutex;

        public MutexLease(Mutex mutex) => _mutex = mutex;

        public void Dispose()
        {
            Mutex? mutex = Interlocked.Exchange(ref _mutex, null);
            if (mutex is null)
            {
                return;
            }

            try
            {
                mutex.ReleaseMutex();
            }
            finally
            {
                mutex.Dispose();
            }
        }
    }

    private sealed record OperationLockNames(string Gate, string Operation);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(
        string fileName,
        uint desiredAccess,
        uint shareMode,
        IntPtr securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        IntPtr templateFile);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandleW(
        SafeFileHandle file,
        StringBuilder filePath,
        uint filePathLength,
        uint flags);

    /// <summary>
    /// 新运行时的提交屏障:Worker 未通过进程身份绑定的 Named Pipe 核验前,
    /// 不允许修改桌面/启动项快捷方式或任何用户级通知 Hook。
    /// </summary>
    public static ActivationResult ActivateInstalledVersion(
        Func<bool> startWorker,
        Func<int> installShortcuts,
        Func<bool> reconcileHooks)
    {
        ArgumentNullException.ThrowIfNull(startWorker);
        ArgumentNullException.ThrowIfNull(installShortcuts);
        ArgumentNullException.ThrowIfNull(reconcileHooks);

        if (!startWorker())
        {
            return new ActivationResult(false, -1, false);
        }

        int shortcutExitCode = installShortcuts();
        if (shortcutExitCode != 0)
        {
            return new ActivationResult(true, shortcutExitCode, false);
        }

        return new ActivationResult(true, 0, reconcileHooks());
    }

    /// <summary>
    /// 安装会先停止旧副本以释放文件锁,所以复制完成后必须立即启动新 Worker。
    /// 只创建开机启动快捷方式意味着本次登录余下时间都没有通知消费者。
    /// </summary>
    public static bool StartInstalledWorker(
        string workerExe,
        string workingDirectory,
        Func<ProcessStartInfo, bool>? startAndProbe = null)
    {
        if (!File.Exists(workerExe))
        {
            Console.Error.WriteLine($"警告:找不到 Worker {workerExe}");
            return false;
        }

        var psi = new ProcessStartInfo(workerExe)
        {
            WorkingDirectory = workingDirectory,
            // install 常由 GUI、测试或 shell 通过重定向管道调用。直接 CreateProcess
            // 会让常驻 Worker 继承这些句柄,调用方即使看到 install 退出也等不到 EOF。
            UseShellExecute = true,
            WindowStyle = ProcessWindowStyle.Hidden,
        };

        try
        {
            bool ok = startAndProbe?.Invoke(psi) ?? StartAndProbe(psi);
            Console.WriteLine(ok ? "后台 Worker 已立即启动" : "后台 Worker 启动后立即退出");
            return ok;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"警告:启动后台 Worker 失败({ex.Message})");
            return false;
        }
    }

    private static bool StartAndProbe(ProcessStartInfo psi)
    {
        // 若启动前已有 Worker 响应,新进程即使短暂存活也不能证明它拿到了单实例互斥体。
        if (CanPingWorker(TimeSpan.FromMilliseconds(250)))
        {
            return false;
        }

        using Process? process = Process.Start(psi);
        if (process is null)
        {
            return false;
        }

        return WaitForWorkerReady(
            () => process.HasExited,
            () => CanPingWorker(TimeSpan.FromMilliseconds(300), process.Id),
            maxAttempts: 30,
            pause: () => Thread.Sleep(100));
    }

    /// <summary>等待 Worker 同时满足“启动进程仍存活”和“Named Pipe 返回 pong”。</summary>
    public static bool WaitForWorkerReady(
        Func<bool> hasExited,
        Func<bool> pipeReady,
        int maxAttempts,
        Action? pause = null)
    {
        ArgumentNullException.ThrowIfNull(hasExited);
        ArgumentNullException.ThrowIfNull(pipeReady);
        if (maxAttempts <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxAttempts));
        }

        for (int attempt = 0; attempt < maxAttempts; attempt++)
        {
            if (hasExited())
            {
                return false;
            }

            if (pipeReady() && !hasExited())
            {
                return true;
            }

            pause?.Invoke();
        }

        return false;
    }

    private static bool CanPingWorker(TimeSpan timeout, int? expectedProcessId = null)
    {
        try
        {
            using var client = new PipeClient(PipeNaming.CurrentUserPipeName, timeout);
            WorkerPingInfo? ping = client.PingIdentityAsync(CancellationToken.None).GetAwaiter().GetResult();
            return MatchesWorkerIdentity(ping, expectedProcessId);
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or UnauthorizedAccessException or OperationCanceledException)
        {
            return false;
        }
    }

    public static bool MatchesWorkerIdentity(WorkerPingInfo? ping, int? expectedProcessId)
        => ping is not null &&
           string.Equals(ping.Version, PipeProtocol.Version, StringComparison.Ordinal) &&
           (!expectedProcessId.HasValue || ping.ProcessId == expectedProcessId.Value);

    private static int Uninstall(string target)
    {
        string? currentExecutable = Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule?.FileName;
        string installedWorker = Path.GetFullPath(Path.Combine(Path.GetFullPath(target), "AiResume.Worker.exe"));
        if (currentExecutable is not null && string.Equals(
                Path.GetFullPath(currentExecutable), installedWorker, StringComparison.OrdinalIgnoreCase))
        {
            using OperationHandoffLease handoff = AcquireOperationHandoffLease(target);
            target = ValidateUninstallTarget(target);
            return LaunchUninstallHelperAndWait(target, handoff);
        }

        using IDisposable operationLease = AcquireOperationLease(target);
        target = ValidateUninstallTarget(target);
        return UninstallCore(target, protectedProcessId: null, deferPayloadDeletion: false);
    }

    private static int LaunchUninstallHelperAndWait(string target, OperationHandoffLease handoff)
    {
        string helperRoot = Path.Combine(
            Path.GetTempPath(), UninstallHelperPrefix + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(helperRoot);
        StageUninstallHelper(target, helperRoot);
        string signalPath = Path.Combine(helperRoot, "uninstall-result.txt");
        string helperExe = Path.Combine(helperRoot, "AiResume.Worker.exe");
        string handoffEventName = @"Global\AIResume.InstallHandoff." + Guid.NewGuid().ToString("N");
        using var handoffAcquired = new EventWaitHandle(
            initialState: false,
            EventResetMode.ManualReset,
            handoffEventName,
            out _);
        var psi = new ProcessStartInfo(helperExe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            WorkingDirectory = helperRoot,
        };
        foreach (string argument in new[]
        {
            "uninstall", "--helper", "--target", target,
            "--parent-pid", Environment.ProcessId.ToString(),
            "--signal", signalPath,
            "--helper-root", helperRoot,
            "--operation-handoff", handoffEventName,
        })
        {
            psi.ArgumentList.Add(argument);
        }

        using Process helper = Process.Start(psi)
            ?? throw new InvalidOperationException("无法启动临时卸载 Worker");
        handoff.ReleaseOperation();
        WaitForHelperOperationHandoff(helper, handoffAcquired, signalPath, helperRoot);
        handoff.ReleaseGate();
        return WaitForUninstallHelperResult(helper, signalPath, helperRoot, TimeSpan.FromSeconds(60));
    }

    private static void WaitForHelperOperationHandoff(
        Process helper,
        EventWaitHandle handoffAcquired,
        string signalPath,
        string helperRoot)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(InstallHandoffTimeoutSeconds);
        while (DateTime.UtcNow < deadline)
        {
            if (handoffAcquired.WaitOne(TimeSpan.FromMilliseconds(100)))
            {
                return;
            }

            if (helper.HasExited || File.Exists(signalPath))
            {
                throw new InvalidOperationException(
                    $"临时卸载 Worker 未接管安装目录事务锁；恢复材料保留在 {helperRoot}");
            }
        }

        StopUninstallHelperBeforeReturn(helper, helperRoot);
        throw new TimeoutException(
            $"临时卸载 Worker 未在 {InstallHandoffTimeoutSeconds} 秒内接管安装目录事务锁；" +
            $"恢复材料保留在 {helperRoot}");
    }

    /// <summary>
    /// 等待临时卸载进程给出事务结果。父进程不能清理 helper 目录：无信号或坏信号时，
    /// <c>retired</c> 可能正是唯一可恢复副本；安全清理由 helper 在明确提交或完整回滚后安排。
    /// </summary>
    public static int WaitForUninstallHelperResult(
        Process helper,
        string signalPath,
        string helperRoot,
        TimeSpan timeout)
    {
        DateTime deadline = DateTime.UtcNow.Add(timeout);
        while (DateTime.UtcNow < deadline)
        {
            if (File.Exists(signalPath))
            {
                string[] result;
                try
                {
                    result = File.ReadAllLines(signalPath);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    StopUninstallHelperBeforeReturn(helper, helperRoot);
                    throw new InvalidDataException(
                        $"临时卸载 Worker 结果无法读取；恢复材料保留在 {helperRoot}", ex);
                }
                if (result.Length > 0 && int.TryParse(result[0], out int exitCode))
                {
                    if (result.Length > 1 && result[1].Length > 0)
                    {
                        (exitCode == 0 ? Console.Out : Console.Error).WriteLine(result[1]);
                    }
                    return exitCode;
                }
                StopUninstallHelperBeforeReturn(helper, helperRoot);
                throw new InvalidDataException($"临时卸载 Worker 返回了无效结果；恢复材料保留在 {helperRoot}");
            }
            if (helper.HasExited)
            {
                throw new InvalidOperationException(
                    $"临时卸载 Worker 提前退出({helper.ExitCode})；恢复材料保留在 {helperRoot}");
            }
            Thread.Sleep(100);
        }

        StopUninstallHelperBeforeReturn(helper, helperRoot);
        Console.Error.WriteLine($"临时卸载 Worker 60 秒内未返回；恢复材料保留在 {helperRoot}");
        return 4;
    }

    private static void StopUninstallHelperBeforeReturn(Process helper, string helperRoot)
    {
        if (helper.HasExited)
        {
            return;
        }

        try
        {
            helper.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException) when (helper.HasExited)
        {
            return;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(
                $"临时卸载 Worker 终止请求失败，将等待其自然退出以保护安装目录:{ex.Message};恢复材料:{helperRoot}");
        }

        // 不设超时：在确认 helper 退出之前返回，会释放安装目录给并发重装，
        // 而旧 helper 随后仍可能退役新 payload。宁可等待，也不能破坏所有权边界。
        helper.WaitForExit();
    }

    public static void StageUninstallHelper(string target, string helperRoot)
    {
        IReadOnlyList<string> payload = ReadPayloadManifest(target);
        foreach (string relative in payload)
        {
            string source = ResolvePayloadPath(target, relative);
            if (!File.Exists(source))
            {
                continue;
            }
            string destination = ResolvePayloadPath(helperRoot, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(source, destination, overwrite: false);
        }

        ValidateStagedRuntime(helperRoot);
    }

    private static int RunUninstallHelper(string[] args, string target)
    {
        string helperRoot = ValidateHelperRoot(AppContext.BaseDirectory);
        string signalPath = Path.Combine(helperRoot, "uninstall-result.txt");
        int? parentProcessId = null;
        int result = 1;
        string detail = "临时卸载未完成";
        bool preserveHelperRoot = false;
        IDisposable? operationLease = null;
        try
        {
            if (!int.TryParse(ReadOption(args, "--parent-pid"), out int parsedParent) || parsedParent <= 0)
            {
                throw new InvalidDataException("临时卸载缺少有效 parent pid");
            }
            parentProcessId = parsedParent;
            string requestedSignal = ReadOption(args, "--signal")
                ?? throw new InvalidDataException("临时卸载缺少结果路径");
            string requestedRoot = ReadOption(args, "--helper-root")
                ?? throw new InvalidDataException("临时卸载缺少 helper root");
            string handoffEventName = ReadOption(args, "--operation-handoff")
                ?? throw new InvalidDataException("临时卸载缺少事务锁交接事件");
            if (!string.Equals(ValidateHelperRoot(requestedRoot), helperRoot, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(Path.GetFullPath(requestedSignal), signalPath, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("临时卸载参数与实际 helper 路径不一致");
            }

            using EventWaitHandle handoffAcquired = EventWaitHandle.OpenExisting(handoffEventName);
            operationLease = AcquireTransferredOperationLease(
                target,
                TimeSpan.FromSeconds(InstallHandoffTimeoutSeconds));
            handoffAcquired.Set();
            target = ValidateUninstallTarget(target);

            result = UninstallCore(target, parsedParent, deferPayloadDeletion: true);
            if (result == 0)
            {
                RetireInstalledPayload(target, helperRoot);
                detail = "卸载完成；状态与未知文件已保留。";
            }
            else
            {
                detail = "快捷方式或通知源未能完整处理。";
            }
        }
        catch (PreserveUninstallHelperException ex)
        {
            preserveHelperRoot = true;
            result = 4;
            detail = ex.Message + $"；恢复材料:{helperRoot}";
        }
        catch (Exception ex)
        {
            result = 1;
            detail = "临时卸载失败:" + ex.Message;
        }
        finally
        {
            try
            {
                WriteUninstallSignal(signalPath, result, detail);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"临时卸载结果写入失败:{ex.Message};恢复材料:{helperRoot}");
                preserveHelperRoot = true;
            }

            operationLease?.Dispose();

            if (!preserveHelperRoot)
            {
                ScheduleHelperDirectoryCleanup(helperRoot, parentProcessId);
            }
        }
        return result;
    }

    private static int UninstallCore(string target, int? protectedProcessId, bool deferPayloadDeletion)
    {
        var registry = new NotificationRegistry();
        IReadOnlyList<NotificationProviderStatus> before = registry.ProbeAll();
        SaveIntent(NotifyIntent.FromProbe(before));
        int preamble = RunUninstallPreamble(
            target,
            () => ShortcutCommand.Run(["shortcuts", "uninstall"]),
            path => StopRunningIn(path, protectedProcessId));
        if (preamble != 0)
        {
            return preamble;
        }

        foreach (NotificationProviderStatus s in before)
        {
            if (s.IsEnabled)
            {
                registry.SetEnabled(s.Kind, false, string.Empty);
                Console.WriteLine($"已关闭通知源 {s.Kind}");
            }
        }

        if (!deferPayloadDeletion && Directory.Exists(target))
        {
            IReadOnlyList<string> installedPayload = ReadPayloadManifest(target);
            foreach (string relative in installedPayload)
            {
                string file = ResolvePayloadPath(target, relative);
                if (File.Exists(file))
                {
                    File.Delete(file);
                }
            }
            File.Delete(Path.Combine(target, PayloadManifestName));
            File.Delete(Path.Combine(target, OwnershipMarkerName));
            DeleteEmptyPayloadDirectories(target, installedPayload);
            ReportUninstallResult(target);
        }

        return 0;
    }

    private static void RetireInstalledPayload(string target, string helperRoot)
    {
        IReadOnlyList<string> installedPayload = ReadPayloadManifest(target);
        string retiredRoot = Path.Combine(helperRoot, "retired");
        Directory.CreateDirectory(retiredRoot);
        bool preservedMarkerExisted = File.Exists(Path.Combine(target, PreservedRootMarkerName));
        var moved = new List<(string Source, string Destination)>();
        try
        {
            IEnumerable<string> orderedPayload = installedPayload.OrderBy(relative =>
            {
                string normalized = NormalizeRelativePath(relative);
                if (normalized.Equals("AiResume.Worker.exe", StringComparison.OrdinalIgnoreCase)) return 2;
                if (normalized.Equals("AiResume.Worker.dll", StringComparison.OrdinalIgnoreCase)) return 1;
                return 0;
            });
            foreach (string relative in orderedPayload)
            {
                string source = ResolvePayloadPath(target, relative);
                if (!File.Exists(source))
                {
                    continue;
                }
                string destination = ResolvePayloadPath(retiredRoot, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                File.Move(source, destination, overwrite: false);
                moved.Add((source, destination));
            }

            MoveMetadataToRetired(target, retiredRoot, PayloadManifestName, moved);
            MoveMetadataToRetired(target, retiredRoot, OwnershipMarkerName, moved);
            DeleteEmptyPayloadDirectories(target, installedPayload);
            ReportUninstallResult(target);
        }
        catch (Exception retirementError)
        {
            var rollbackErrors = new List<Exception>();
            if (!preservedMarkerExisted && HasPreservedRootMarker(target))
            {
                try
                {
                    File.Delete(Path.Combine(target, PreservedRootMarkerName));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    rollbackErrors.Add(ex);
                }
            }
            foreach ((string source, string destination) in moved.AsEnumerable().Reverse())
            {
                try
                {
                    if (!File.Exists(destination))
                    {
                        continue;
                    }
                    if (File.Exists(source))
                    {
                        throw new IOException($"回滚目标已被占用:{source}");
                    }
                    Directory.CreateDirectory(Path.GetDirectoryName(source)!);
                    File.Move(destination, source, overwrite: false);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    rollbackErrors.Add(ex);
                }
            }

            if (rollbackErrors.Count > 0)
            {
                throw new PreserveUninstallHelperException(
                    "卸载文件退役失败且回滚不完整",
                    new AggregateException(new[] { retirementError }.Concat(rollbackErrors)));
            }
            throw;
        }
    }

    private static void MoveMetadataToRetired(
        string target,
        string retiredRoot,
        string name,
        ICollection<(string Source, string Destination)> moved)
    {
        string source = Path.Combine(target, name);
        if (!File.Exists(source))
        {
            return;
        }
        string destination = ResolvePayloadPath(retiredRoot, name);
        File.Move(source, destination, overwrite: false);
        moved.Add((source, destination));
    }

    private static void ReportUninstallResult(string target)
    {
        if (!Directory.Exists(target))
        {
            return;
        }
        string state = Path.Combine(target, ShadowPaths.StateFolder);
        string[] remainingBeforeMarker = Directory.EnumerateFileSystemEntries(target).ToArray();
        bool hasUnknown = remainingBeforeMarker.Any(entry => !string.Equals(
            Path.GetFileName(entry), ShadowPaths.StateFolder, StringComparison.OrdinalIgnoreCase));
        if (hasUnknown)
        {
            WritePreservedRootMarker(target);
        }
        string[] remaining = Directory.EnumerateFileSystemEntries(target).ToArray();
        if (Directory.Exists(state))
        {
            Console.WriteLine($"已删除程序文件,保留状态目录 {state}");
            Console.WriteLine("(内含加密的飞书凭据与运行记录;确实要清空请手动删除该目录。)");
        }
        if (hasUnknown)
        {
            Console.WriteLine($"安装目录中存在不属于 payload 清单的文件,已原样保留:{target}");
        }
        else if (remaining.Length == 0)
        {
            Directory.Delete(target);
            Console.WriteLine($"已删除 {target}");
        }
    }

    private static void WriteUninstallSignal(string signalPath, int result, string detail)
    {
        string temporary = signalPath + ".tmp";
        File.WriteAllLines(temporary, [result.ToString(), detail]);
        File.Move(temporary, signalPath, overwrite: true);
    }

    private static string ValidateHelperRoot(string helperRoot)
    {
        string tempRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath()));
        string full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(helperRoot));
        if (!string.Equals(Directory.GetParent(full)?.FullName, tempRoot, StringComparison.OrdinalIgnoreCase) ||
            !Path.GetFileName(full).StartsWith(UninstallHelperPrefix, StringComparison.Ordinal))
        {
            throw new InvalidDataException("临时卸载目录不属于系统 temp");
        }
        return full;
    }

    private static void ScheduleHelperDirectoryCleanup(string helperRoot, int? parentProcessId)
    {
        helperRoot = ValidateHelperRoot(helperRoot);

        try
        {
            string shell = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.System),
                "WindowsPowerShell", "v1.0", "powershell.exe");
            if (!File.Exists(shell))
            {
                throw new FileNotFoundException("找不到 Windows PowerShell", shell);
            }

            string escaped = helperRoot.Replace("'", "''", StringComparison.Ordinal);
            string command = "$ErrorActionPreference='SilentlyContinue';" +
                             $"Wait-Process -Id {Environment.ProcessId};" +
                             (parentProcessId is { } parent ? $"Wait-Process -Id {parent};" : string.Empty) +
                             $"for($i=0;$i -lt 50;$i++){{try{{Remove-Item -LiteralPath '{escaped}' -Recurse -Force -ErrorAction Stop;break}}catch{{Start-Sleep -Milliseconds 100}}}}";
            string encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(command));
            var psi = new ProcessStartInfo(shell)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                WorkingDirectory = Path.GetTempPath(),
            };
            psi.ArgumentList.Add("-NoProfile");
            psi.ArgumentList.Add("-NonInteractive");
            psi.ArgumentList.Add("-EncodedCommand");
            psi.ArgumentList.Add(encoded);
            Process.Start(psi)?.Dispose();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"警告:临时卸载目录未能安排清理:{helperRoot} ({ex.Message})");
        }
    }

    private sealed class PreserveUninstallHelperException : Exception
    {
        public PreserveUninstallHelperException(string message, Exception innerException)
            : base(message, innerException)
        {
        }
    }

    /// <summary>
    /// 安装目标可以自定义，但不得复用宽目录或借 reparse point 重定向到其它树。
    /// marker 引入前的 v2 目录只有在每个非状态文件都属于本次 payload 时才能升级；
    /// 仅保留 state 的卸载目录也可重装。
    /// </summary>
    public static string ValidateInstallTarget(
        string target,
        IReadOnlyCollection<string>? recognizedPayloadRelativePaths = null)
    {
        string full = NormalizeAndValidateTarget(target);
        if (!Directory.Exists(full))
        {
            return full;
        }

        if (HasOwnershipMarker(full) ||
            HasPreservedRootMarker(full) ||
            IsRecognizedRuntime(full, recognizedPayloadRelativePaths) ||
            ContainsOnlyPreservedState(full) ||
            !Directory.EnumerateFileSystemEntries(full).Any())
        {
            return full;
        }

        throw new InvalidOperationException(
            $"安装目录不是空目录、保留状态目录或可证明的 AI Resume 运行时:{full}");
    }

    /// <summary>卸载是破坏性操作，必须有当前安装器写入的不可含糊 marker。</summary>
    public static string ValidateUninstallTarget(string target)
    {
        string full = NormalizeAndValidateTarget(target);
        if (!Directory.Exists(full) || !HasOwnershipMarker(full) || !HasValidPayloadManifest(full))
        {
            throw new InvalidOperationException(
                $"拒绝卸载缺少精确所有权标记或安装清单的目录:{full}");
        }
        return full;
    }

    public static void WriteOwnershipMarker(string target)
    {
        Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(target, OwnershipMarkerName), OwnershipMarkerContent);
    }

    public static void WritePreservedRootMarker(string target)
    {
        Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(target, PreservedRootMarkerName), PreservedRootMarkerContent);
    }

    public static void WritePayloadManifest(string target)
    {
        Directory.CreateDirectory(target);
        string manifestPath = Path.Combine(target, PayloadManifestName);
        string ownershipMarkerPath = Path.Combine(target, OwnershipMarkerName);
        string[] relativeFiles = Directory.EnumerateFiles(target, "*", SearchOption.AllDirectories)
            .Where(path => !string.Equals(path, manifestPath, StringComparison.OrdinalIgnoreCase) &&
                           !string.Equals(path, ownershipMarkerPath, StringComparison.OrdinalIgnoreCase))
            .Select(path => NormalizeRelativePath(Path.GetRelativePath(target, path)))
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        File.WriteAllLines(manifestPath, relativeFiles);
    }

    private static bool HasOwnershipMarker(string target)
    {
        string marker = Path.Combine(target, OwnershipMarkerName);
        try
        {
            return File.Exists(marker) &&
                   string.Equals(File.ReadAllText(marker), OwnershipMarkerContent, StringComparison.Ordinal);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static bool HasPreservedRootMarker(string target)
    {
        string marker = Path.Combine(target, PreservedRootMarkerName);
        try
        {
            return File.Exists(marker) &&
                   string.Equals(File.ReadAllText(marker), PreservedRootMarkerContent, StringComparison.Ordinal);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static bool HasValidPayloadManifest(string target)
    {
        try
        {
            _ = ReadPayloadManifest(target);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return false;
        }
    }

    private static bool IsRecognizedRuntime(
        string target,
        IReadOnlyCollection<string>? recognizedPayloadRelativePaths)
    {
        if (recognizedPayloadRelativePaths is null ||
            !File.Exists(Path.Combine(target, "AiResume.Gui.exe")) ||
            !File.Exists(Path.Combine(target, "AiResume.Worker.exe")) ||
            !File.Exists(Path.Combine(target, HookExecutable.FileName)))
        {
            return false;
        }

        var recognized = new HashSet<string>(
            recognizedPayloadRelativePaths.Select(NormalizeRelativePath),
            StringComparer.OrdinalIgnoreCase);
        foreach (string entry in Directory.EnumerateFileSystemEntries(target, "*", SearchOption.AllDirectories))
        {
            string relative = Path.GetRelativePath(target, entry);
            string firstSegment = relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)[0];
            if (string.Equals(firstSegment, ShadowPaths.StateFolder, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (Directory.Exists(entry))
            {
                if (!Directory.EnumerateFileSystemEntries(entry).Any())
                {
                    return false;
                }
                continue;
            }

            if (!recognized.Contains(NormalizeRelativePath(relative)))
            {
                return false;
            }
        }

        return true;
    }

    private static bool ContainsOnlyPreservedState(string target)
    {
        string[] entries = Directory.EnumerateFileSystemEntries(target).ToArray();
        return entries.Length == 1 &&
               Directory.Exists(entries[0]) &&
               string.Equals(Path.GetFileName(entries[0]), ShadowPaths.StateFolder, StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeAndValidateTarget(string target)
    {
        if (string.IsNullOrWhiteSpace(target))
        {
            throw new ArgumentException("安装目录不能为空", nameof(target));
        }

        string full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(target));
        string? root = Path.GetPathRoot(full);
        if (root is null ||
            string.Equals(full, Path.TrimEndingDirectorySeparator(root), StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"拒绝把卷根目录作为安装目标:{full}");
        }

        string[] protectedRoots =
        [
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            Environment.GetFolderPath(Environment.SpecialFolder.System),
        ];
        string canonicalFull = GetCanonicalOperationTarget(full);
        if (protectedRoots
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(p => GetCanonicalOperationTarget(
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(p))))
            .Any(p => string.Equals(canonicalFull, p, StringComparison.Ordinal)))
        {
            throw new InvalidOperationException($"拒绝把系统或用户根目录作为安装目标:{full}");
        }

        for (string? current = Directory.Exists(full) ? full : Directory.GetParent(full)?.FullName;
             current is not null;
             current = Directory.GetParent(current)?.FullName)
        {
            if (!Directory.Exists(current))
            {
                continue;
            }
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidOperationException($"安装目标路径包含 reparse point，拒绝继续:{current}");
            }
        }

        return full;
    }

    /// <summary>
    /// 卸载必须先完整移除外部入口，再停止进程和删除运行时。
    /// 仅为可测性公开：入口事务失败时必须保持程序仍可运行，不能留下死快捷方式。
    /// </summary>
    public static int RunUninstallPreamble(
        string target,
        Func<int> removeShortcuts,
        Action<string> stopRunning)
    {
        ArgumentNullException.ThrowIfNull(removeShortcuts);
        ArgumentNullException.ThrowIfNull(stopRunning);
        int shortcutResult = removeShortcuts();
        if (shortcutResult != 0)
        {
            Console.Error.WriteLine("快捷方式未能完整移除，已停止卸载；程序文件与 Worker 保持不变。");
            return shortcutResult;
        }

        stopRunning(target);
        return 0;
    }

    /// <summary>
    /// 把通知源对账到安装目录里的 hook。
    ///
    /// 「对账」而不是「重指」:原来的判据是"当前已启用的才重指",
    /// 于是 <c>uninstall</c> 之后再 <c>install</c>,现状是空的、循环体一次都没进,
    /// 命令照样退出码 0 并打印"入口已全部指向安装目录" —— 而五个源全是关的
    /// (2026-08-08 审计 B3)。现在的依据是**意图 ∪ 现状**,见 <see cref="NotifyIntent.Targets"/>。
    ///
    /// 单个源写失败不终止其余:一个装坏的 Qoder 配置不该连累另外四个。
    /// 但失败必须被数出来并影响退出码,否则又是一次"命令说成功、事情没做成"。
    /// </summary>
    private static bool ReconcileHooks(string hookExe)
    {
        if (!File.Exists(hookExe))
        {
            Console.Error.WriteLine($"警告:安装目录里没有 {HookExecutable.FileName},通知钩子未对账。");
            return false;
        }

        var registry = new NotificationRegistry();
        IReadOnlyList<NotificationProviderStatus> probed = registry.ProbeAll();
        List<NotificationProviderKind> targets = NotifyIntent.Targets(LoadIntent(), probed);

        var done = new List<NotificationProviderKind>();
        bool allOk = true;
        foreach (NotificationProviderKind kind in targets)
        {
            try
            {
                // 各适配器必须原位刷新自己拥有的条目。先关后开会在第二步失败时
                // 拆掉原本工作的 hook,因此安装对账不再主动制造禁用空窗。
                registry.SetEnabled(kind, true, hookExe);
                done.Add(kind);
                Console.WriteLine($"通知源 {kind} 已指向安装目录");
            }
            catch (Exception ex)
            {
                allOk = false;
                Console.Error.WriteLine($"警告:通知源 {kind} 启用失败({ex.Message});其余源继续。");
            }
        }

        if (targets.Count == 0)
        {
            Console.WriteLine("没有需要启用的通知源(此前也没开过)。");
        }
        else if (done.Contains(NotificationProviderKind.Codex))
        {
            Console.WriteLine("Codex 通知配置已写入;若客户端此前已在运行,需重启 Codex 后加载。");
        }

        // 意图代表用户想要的目标,不是本轮偶然成功的子集。失败项必须保留,
        // 否则一次权限/磁盘错误会让后续安装永远不再重试。
        SaveIntent(targets.Select(k => k.ToString()).ToList());

        // 核对到底:写完再探一次,确认配置里真的有了。写成功不等于探得到——
        // 本项目已经三次栽在"只看自己写了什么,不回头看世界变没变"上。
        var after = new NotificationRegistry().ProbeAll()
            .Where(s => s.IsEnabled && !s.HookBroken).Select(s => s.Kind).ToHashSet();
        foreach (NotificationProviderKind kind in done)
        {
            if (!after.Contains(kind))
            {
                allOk = false;
                Console.Error.WriteLine($"警告:通知源 {kind} 写入后复查未通过,通知可能收不到。");
            }
        }

        return allOk;
    }

    /// <summary>读回持久化的通知意图;读不出来当作空(不阻断安装)。</summary>
    private static List<string> LoadIntent()
    {
        try
        {
            return new ProductConfigStore(ShadowPaths.EnsureRoot()).Load().NotifySources;
        }
        catch (Exception)
        {
            return new List<string>();
        }
    }

    /// <summary>
    /// 写回通知意图。**只改这一个字段**:配置同时被 GUI 与续跑引擎写,
    /// 锁外读旧快照整体写回会互相覆盖(本项目有过这个事故)。
    /// </summary>
    private static void SaveIntent(List<string> sources)
    {
        try
        {
            new ProductConfigStore(ShadowPaths.EnsureRoot()).Update(c => c.NotifySources = sources);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"警告:通知源意图未能保存({ex.Message});重装后可能需要手动重开。");
        }
    }

    /// <summary>
    /// 定位三个项目的构建输出。
    /// <paramref name="from"/> 为 src 根目录;为 null 时从当前程序位置往上推
    /// (…\src\&lt;Project&gt;\bin\&lt;Cfg&gt;\&lt;Tfm&gt;\ → 上溯四层得到 src)。
    /// </summary>
    private static IReadOnlyList<string> ResolveSources(string? from)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        string tfm = dir.Name;
        string cfg = dir.Parent?.Name ?? "Debug";
        string srcRoot = from ?? dir.Parent?.Parent?.Parent?.Parent?.FullName ?? string.Empty;

        var found = new List<string>();
        foreach (string project in Projects)
        {
            string candidate = Path.Combine(srcRoot, project, "bin", cfg, tfm);
            if (Directory.Exists(candidate))
            {
                found.Add(candidate);
                continue;
            }

            // Hook 与 Worker 的 TFM 可能与 GUI 不同(net10.0 vs net10.0-windows),
            // 找不到精确匹配时退一步在 bin\<Cfg>\ 下取唯一子目录。
            string cfgDir = Path.Combine(srcRoot, project, "bin", cfg);
            if (!Directory.Exists(cfgDir))
            {
                continue;
            }

            string[] tfms = Directory.GetDirectories(cfgDir);
            if (tfms.Length == 1)
            {
                found.Add(tfms[0]);
            }
        }

        return found;
    }

    private static IReadOnlyCollection<string> EnumeratePayloadRelativeFiles(IEnumerable<string> sources)
    {
        var relativePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string source in sources)
        {
            foreach (string file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
            {
                relativePaths.Add(NormalizeRelativePath(Path.GetRelativePath(source, file)));
            }
        }
        return relativePaths;
    }

    private static string NormalizeRelativePath(string relativePath) =>
        relativePath.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);

    private static IReadOnlyList<string> ReadPayloadManifest(string root)
    {
        string manifestPath = Path.Combine(root, PayloadManifestName);
        if (!File.Exists(manifestPath))
        {
            throw new InvalidDataException("缺少安装 payload 清单");
        }

        var result = new List<string>();
        foreach (string raw in File.ReadAllLines(manifestPath))
        {
            string relative = NormalizeRelativePath(raw.Trim());
            if (relative.Length == 0 || Path.IsPathRooted(relative) ||
                relative.Split(Path.DirectorySeparatorChar).Any(segment => segment is "." or ".."))
            {
                throw new InvalidDataException("安装 payload 清单包含不安全路径");
            }

            _ = ResolvePayloadPath(root, relative);
            result.Add(relative);
        }

        return result.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public static IReadOnlyList<string> FindObsoletePayload(string target, string stage)
    {
        if (!Directory.Exists(target) || !File.Exists(Path.Combine(target, PayloadManifestName)))
        {
            return Array.Empty<string>();
        }

        var next = new HashSet<string>(ReadPayloadManifest(stage), StringComparer.OrdinalIgnoreCase);
        return ReadPayloadManifest(target)
            .Where(relative => !next.Contains(relative))
            .ToArray();
    }

    public static void DeleteObsoletePayload(string target, IEnumerable<string> obsoletePayload)
    {
        foreach (string relative in obsoletePayload)
        {
            string path = ResolvePayloadPath(target, relative);
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        DeleteEmptyPayloadDirectories(target, obsoletePayload);
    }

    public static void DeleteEmptyPayloadDirectories(string target, IEnumerable<string> relativeFiles)
    {
        var relativeDirectories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string relativeFile in relativeFiles)
        {
            string? directory = Path.GetDirectoryName(relativeFile);
            while (!string.IsNullOrEmpty(directory))
            {
                relativeDirectories.Add(NormalizeRelativePath(directory));
                directory = Path.GetDirectoryName(directory);
            }
        }

        foreach (string relativeDirectory in relativeDirectories.OrderByDescending(path => path.Length))
        {
            string firstSegment = relativeDirectory.Split(Path.DirectorySeparatorChar)[0];
            if (string.Equals(firstSegment, ShadowPaths.StateFolder, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            string directory = ResolvePayloadPath(target, relativeDirectory);
            if (Directory.Exists(directory) && !Directory.EnumerateFileSystemEntries(directory).Any())
            {
                Directory.Delete(directory);
            }
        }
    }

    private static string ResolvePayloadPath(string root, string relative)
    {
        string fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        string resolved = Path.GetFullPath(Path.Combine(fullRoot, relative));
        string prefix = fullRoot + Path.DirectorySeparatorChar;
        if (!resolved.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("安装 payload 清单路径越界");
        }

        string current = fullRoot;
        foreach (string segment in NormalizeRelativePath(relative).Split(Path.DirectorySeparatorChar))
        {
            current = Path.Combine(current, segment);
            if ((Directory.Exists(current) || File.Exists(current)) &&
                (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidDataException($"安装 payload 路径包含 reparse point:{relative}");
            }
        }
        return resolved;
    }

    private static int CopyTree(
        string sourceDir,
        string targetDir,
        bool rejectReparse = false,
        IEnumerable<string>? relativeFiles = null)
    {
        int count = 0;
        IEnumerable<(string File, string Relative)> files = relativeFiles is null
            ? Directory.EnumerateFiles(sourceDir, "*", SearchOption.AllDirectories)
                .Select(file => (file, NormalizeRelativePath(Path.GetRelativePath(sourceDir, file))))
            : relativeFiles.Select(relative =>
                (ResolvePayloadPath(sourceDir, relative), NormalizeRelativePath(relative)));

        foreach ((string file, string rel) in files)
        {
            string dest = rejectReparse
                ? ResolvePayloadPath(targetDir, rel)
                : Path.Combine(targetDir, rel);
            string? destDir = Path.GetDirectoryName(dest);
            if (!string.IsNullOrEmpty(destDir))
            {
                Directory.CreateDirectory(destDir);
            }

            File.Copy(file, dest, overwrite: true);
            count++;
        }

        return count;
    }

    /// <summary>冻结 staging 的逐文件摘要,供停服务后的提交阶段核对精确字节。</summary>
    public static IReadOnlyDictionary<string, string> CapturePayloadHashes(string root)
    {
        var hashes = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (string file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            string relative = NormalizeRelativePath(Path.GetRelativePath(root, file));
            using FileStream stream = new(file, FileMode.Open, FileAccess.Read, FileShare.Read);
            hashes.Add(relative, Convert.ToHexString(SHA256.HashData(stream)));
        }

        return hashes;
    }

    /// <summary>确认目标中的每个提交文件仍与 staging 快照逐字节一致。</summary>
    public static void VerifyPayloadHashes(
        string root,
        IReadOnlyDictionary<string, string> expectedHashes)
    {
        foreach ((string relative, string expectedHash) in expectedHashes)
        {
            string file = ResolvePayloadPath(root, relative);
            if (!File.Exists(file))
            {
                throw new InvalidDataException($"安装提交缺少 payload 文件:{relative}");
            }

            using FileStream stream = new(file, FileMode.Open, FileAccess.Read, FileShare.Read);
            string actualHash = Convert.ToHexString(SHA256.HashData(stream));
            if (!actualHash.Equals(expectedHash, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException($"安装提交 payload 校验失败:{relative}");
            }
        }
    }

    private static void ValidateStagedRuntime(string stage)
    {
        foreach (string required in new[] { "AiResume.Gui.exe", "AiResume.Worker.exe", HookExecutable.FileName })
        {
            string path = Path.Combine(stage, required);
            if (!File.Exists(path) || new FileInfo(path).Length == 0)
            {
                throw new InvalidOperationException($"staging 缺少有效运行文件: {required}");
            }
        }
    }

    /// <summary>只备份本次 payload 会覆盖的旧文件;state 与其它用户数据完全不在集合中。</summary>
    private static void BackupPayload(
        string stage,
        string target,
        string backup,
        IEnumerable<string> obsoletePayload)
    {
        foreach (string stagedFile in Directory.EnumerateFiles(stage, "*", SearchOption.AllDirectories))
        {
            string rel = Path.GetRelativePath(stage, stagedFile);
            string current = ResolvePayloadPath(target, rel);
            if (!File.Exists(current))
            {
                continue;
            }

            string backupFile = Path.Combine(backup, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(backupFile)!);
            File.Copy(current, backupFile, overwrite: true);
        }

        foreach (string relative in obsoletePayload)
        {
            string current = ResolvePayloadPath(target, relative);
            if (!File.Exists(current))
            {
                continue;
            }
            string backupFile = Path.Combine(backup, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(backupFile)!);
            File.Copy(current, backupFile, overwrite: true);
        }
    }

    public static bool RollbackRuntime(
        string stage,
        string backup,
        string target,
        bool restartWorker,
        IEnumerable<string> obsoletePayload,
        Action<string>? stopRunning = null,
        Func<string, string, string>? resolvePayloadPath = null)
    {
        stopRunning ??= path => StopRunningIn(path);
        resolvePayloadPath ??= ResolvePayloadPath;
        try
        {
            Console.Error.WriteLine("正在回滚安装目录里的运行文件...");
            stopRunning(target);
            bool restored = true;
            string[] stagedFiles = Directory.EnumerateFiles(stage, "*", SearchOption.AllDirectories).ToArray();
            foreach (string stagedFile in stagedFiles)
            {
                string rel = Path.GetRelativePath(stage, stagedFile);
                try
                {
                    string current = resolvePayloadPath(target, rel);
                    string old = Path.Combine(backup, rel);
                    if (File.Exists(old))
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(current)!);
                        File.Copy(old, current, overwrite: true);
                    }
                    else if (File.Exists(current))
                    {
                        File.Delete(current);
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
                {
                    restored = false;
                    Console.Error.WriteLine($"警告:回滚文件失败 {rel} ({ex.Message})");
                }
            }

            foreach (string relative in obsoletePayload)
            {
                try
                {
                    string old = Path.Combine(backup, relative);
                    string current = resolvePayloadPath(target, relative);
                    if (File.Exists(old))
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(current)!);
                        File.Copy(old, current, overwrite: true);
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
                {
                    restored = false;
                    Console.Error.WriteLine($"警告:回滚过期 payload 失败 {relative} ({ex.Message})");
                }
            }

            if (restored)
            {
                DeleteEmptyPayloadDirectories(
                    target, stagedFiles.Select(file => Path.GetRelativePath(stage, file)));
            }

            if (!restored)
            {
                Console.Error.WriteLine("警告:运行文件未能完整回滚,请勿把当前安装视为可用。");
                return false;
            }

            Console.Error.WriteLine("旧运行文件已恢复。");
            string oldWorker = Path.Combine(target, "AiResume.Worker.exe");
            if (restartWorker && File.Exists(oldWorker) && !StartInstalledWorker(oldWorker, target))
            {
                Console.Error.WriteLine("警告:旧 Worker 文件已恢复,但未能重新通过就绪核验。");
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"警告:回滚过程异常({ex.Message});已保留 staging/backup 恢复材料。");
            return false;
        }
    }

    private static void TryDeleteOperationDirectory(string path, string expectedParent, string prefix)
    {
        try
        {
            string full = Path.GetFullPath(path);
            string? parent = Directory.GetParent(full)?.FullName;
            if (!string.Equals(parent, Path.GetFullPath(expectedParent), StringComparison.OrdinalIgnoreCase) ||
                !Path.GetFileName(full).StartsWith(prefix, StringComparison.Ordinal))
            {
                Console.Error.WriteLine($"警告:拒绝清理无法确认的安装临时目录 {full}");
                return;
            }

            if (Directory.Exists(full))
            {
                Directory.Delete(full, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"警告:安装临时目录清理失败({ex.Message})");
        }
    }

    private static bool IsRunningIn(string target, string processName)
    {
        string normalized = Path.GetFullPath(target).TrimEnd('\\');
        foreach (Process p in Process.GetProcessesByName(processName))
        {
            try
            {
                string? path = p.MainModule?.FileName;
                if (path is not null && path.StartsWith(normalized + "\\", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            catch (Exception)
            {
            }
            finally
            {
                p.Dispose();
            }
        }

        return false;
    }

    /// <summary>
    /// 等到文件不再被别的进程独占为止。超时不抛异常 —— 让后续复制阶段以真实的
    /// 文件锁错误失败,那里有完整的备份与回滚,比在这里提前中断更安全。
    /// </summary>
    internal static bool WaitForFileUnlocked(
        string path,
        TimeSpan timeout,
        Func<string, bool>? isUnlocked = null,
        Action<int>? sleep = null)
    {
        isUnlocked ??= DefaultIsUnlocked;
        sleep ??= Thread.Sleep;

        int attempts = Math.Max(1, (int)(timeout.TotalMilliseconds / 250));
        for (int i = 0; i < attempts; i++)
        {
            if (isUnlocked(path))
            {
                return true;
            }

            sleep(250);
        }

        return isUnlocked(path);
    }

    private static bool DefaultIsUnlocked(string path)
    {
        if (!File.Exists(path))
        {
            return true;
        }

        try
        {
            using FileStream stream = File.Open(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            return true;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            // 权限问题不是"还锁着",继续等也没用。
            return true;
        }
    }

    /// <summary>只终止**从目标目录运行**的实例;用户在仓库里跑的开发实例不受影响。</summary>
    private static void StopRunningIn(string target, int? protectedProcessId = null)
    {
        // 先结束计划任务的运行实例。它用 S4U 跑在会话 0,而 install 是会话 1 的非提权
        // 进程,读不到那个进程的 MainModule —— 下面"只杀本目录进程"的保守判据会直接
        // 跳过它,于是 DLL 一直锁着,安装失败并进入不完整回滚(2026-08-14 实测)。
        string taskWorker = Path.Combine(Path.GetFullPath(target), "AiResume.Worker.exe");
        if (WorkerAutostart.IsScheduledTaskManagingAutostart(taskWorker) &&
            WorkerAutostart.StopScheduledTaskInstance(log: Console.WriteLine))
        {
            // **/End 是异步的。** 它只是给任务发停止信号,进程还要一会儿才退。
            // 不等就继续复制,文件仍然锁着 —— 表现和完全没停一模一样(实测)。
            // 直接以"能不能独占打开 Worker.exe"为判据:这正是复制阶段需要的那个条件。
            WaitForFileUnlocked(taskWorker, TimeSpan.FromSeconds(20));
        }

        string normalized = Path.GetFullPath(target).TrimEnd('\\');
        foreach (string name in new[] { "AiResume.Gui", "AiResume.Worker" })
        {
            foreach (System.Diagnostics.Process p in System.Diagnostics.Process.GetProcessesByName(name))
            {
                try
                {
                    if (p.Id == Environment.ProcessId || p.Id == protectedProcessId)
                    {
                        continue;
                    }
                    string? path = p.MainModule?.FileName;
                    if (path is not null &&
                        path.StartsWith(normalized + "\\", StringComparison.OrdinalIgnoreCase))
                    {
                        p.Kill(entireProcessTree: true);
                        p.WaitForExit(5000);
                        Console.WriteLine($"已停止安装目录里的 {name}({p.Id})");
                    }
                }
                catch (Exception)
                {
                    // 拿不到 MainModule(权限/已退出)时跳过:宁可复制失败报错,
                    // 也不要凭进程名乱杀。
                }
                finally
                {
                    p.Dispose();
                }
            }
        }
    }


    private static string? ReadOption(string[] args, string name)
    {
        int i = Array.FindIndex(args, a => string.Equals(a, name, StringComparison.OrdinalIgnoreCase));
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }
}
