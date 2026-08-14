using System.Runtime.InteropServices;

namespace AiResume.Worker.Migration;

/// <summary>
/// <c>AiResume.Worker.exe shortcuts [install|uninstall]</c>:
/// 创建/移除开始菜单与开机自启动的快捷方式。
///
/// **为什么必须有开机自启**:续跑编排(<c>ResumeEngine</c>)跑在 Worker 进程里。
/// 它是 AI Resume 唯一不可替代的核心(ADR-0003 §2.2),但在此之前**没有任何启动入口**
/// ——装了也不会自己跑,限额恢复后不会续跑。GUI 只是控制面,关掉窗口不该停掉引擎,
/// 所以两者必须分别有入口。
///
/// 快捷方式经 WScript.Shell COM 创建(与现役 install.ps1 同路子,不引入新依赖)。
/// **只创建/删除本产品自己的两个 .lnk**,绝不触碰现役 ClaudeResume* 的快捷方式
/// ——那是回滚路径,切换观察期结束前必须留着。
/// </summary>
public static class ShortcutCommand
{
    private const string GuiLinkName = "AI Resume 控制面.lnk";
    private const string WorkerLinkName = "AI Resume 续跑引擎.lnk";

    private static string StartMenuDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Microsoft", "Windows", "Start Menu", "Programs");

    private static string StartupDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Microsoft", "Windows", "Start Menu", "Programs", "Startup");

    private static string DesktopDir =>
        Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);

    /// <summary>
    /// 桌面上那个入口的名字。**沿用现役安装器用过的名字**,这样它会被就地覆盖,
    /// 而不是在桌面上多出一个同义图标、让人继续点到旧系统。
    /// 2026-08-06 实测:桌面「AI Resume」一直指向
    /// <c>wscript.exe "…\ClaudeResume\launcher.vbs"</c>,所以点开的始终是旧软件。
    /// </summary>
    private const string DesktopLinkName = "AI Resume.lnk";

    public static int Run(string[] args)
    {
        bool uninstall = args.Any(a => string.Equals(a, "uninstall", StringComparison.OrdinalIgnoreCase));

        try
        {
            if (uninstall)
            {
                // Startup 里的 .lnk 是旧版入口,现役自启走计划任务;两个都要清。
                RemoveShortcutsTransaction(
                [
                    Path.Combine(StartMenuDir, GuiLinkName),
                    Path.Combine(StartupDir, WorkerLinkName),
                    Path.Combine(DesktopDir, DesktopLinkName),
                ]);
                Console.WriteLine("已移除 AI Resume 的快捷方式。");
                return 0;
            }

            string baseDir = AppContext.BaseDirectory;
            // 发布产物里 GUI 与 Worker 同目录;开发构建下各在各自的 bin 里,
            // 所以允许 --gui 显式指定,否则装出来的快捷方式会指向不存在的文件。
            string guiExe = ReadOption(args, "--gui") ?? Path.Combine(baseDir, "AiResume.Gui.exe");
            string workerExe = ReadOption(args, "--worker") ?? Path.Combine(baseDir, "AiResume.Worker.exe");

            // **图标必须跟着目标走,不能跟着"谁在跑安装命令"走。**
            // 原来固定取 AppContext.BaseDirectory —— 从仓库 bin 跑 install 时,
            // TargetPath 因为显式传了 --gui 而正确指向安装目录,图标却留在了仓库 bin。
            // 仓库一改名,三个快捷方式的图标就全变成空白(2026-08-07 实测),
            // 而且失败是静默的:快捷方式照常能点开,只是没图标。
            string icon = ReadOption(args, "--icon")
                ?? Path.Combine(Path.GetDirectoryName(guiExe) ?? baseDir, "icon.ico");

            // GUI 与 Worker 是分开构建的:同目录下找不到 GUI 时明确报错,
            // 不要默默创建一个指向不存在文件的快捷方式。
            if (!File.Exists(guiExe))
            {
                Console.Error.WriteLine($"找不到控制面可执行文件:{guiExe}");
                return 1;
            }
            if (!File.Exists(workerExe))
            {
                Console.Error.WriteLine($"找不到续跑引擎可执行文件:{workerExe}");
                return 1;
            }

            // 图标缺失只警告不中止:没有图标的快捷方式仍然可用,
            // 但必须说出来 —— 否则用户只会看到"图标没了"却不知道为什么。
            if (!File.Exists(icon))
            {
                Console.Error.WriteLine($"警告:找不到图标 {icon},快捷方式将使用默认图标。");
            }

            // 计划任务是否**真的在管**自启:不光要文件在,还要没被禁用、且 action 指向
            // 本次安装目录的 Worker。只看存在性会让"卸载重装 / 换 --target"之后
            // 留下的失效任务永久压掉唯一的自启入口,而 install 照样返回 0。
            bool managedByTask = WorkerAutostart.IsScheduledTaskManagingAutostart(workerExe);

            // 开机自启指向 WinExe 垫片(无窗口拉起 Worker);垫片缺席时直指 Worker,
            // 会弹控制台窗口 —— 但没有自启才是真故障,不能因此不装。
            WorkerAutostart.AutostartTarget autostart = WorkerAutostart.Resolve(workerExe);
            if (!autostart.Hidden && !managedByTask)
            {
                Console.Error.WriteLine(
                    $"警告:找不到 {WorkerAutostart.LauncherFileName},开机自启将直接启动控制台程序 —— " +
                    "登录时会短暂弹出控制台窗口。");
            }

            var shortcuts = new List<ShortcutSpec>
            {
                new ShortcutSpec(
                    Path.Combine(StartMenuDir, GuiLinkName), guiExe,
                    Path.GetDirectoryName(guiExe) ?? baseDir, icon,
                    "AI Resume 控制面:额度、续跑队列与完成通知"),
                // **登录自启不再放这里。** Worker 是控制台程序,Explorer 启动 Startup 里的
                // .lnk 会给它配一个控制台窗口,每次开机弹黑框。自启改由
                // WorkerAutostart 的 S4U 计划任务负责(隐藏运行),旧 .lnk 在下面删除。
                // 桌面入口就地覆盖旧安装器留下的同名 .lnk —— 它指向 launcher.vbs(旧系统)。
                new ShortcutSpec(
                    Path.Combine(DesktopDir, DesktopLinkName), guiExe,
                    Path.GetDirectoryName(guiExe) ?? baseDir, icon,
                    "AI Resume 控制面:额度、续跑队列与完成通知"),
            };
            // 已升级成计划任务时不能再建快捷方式:两条链路都在,登录时会各拉起一个 Worker。
            if (!managedByTask)
            {
                shortcuts.Add(new ShortcutSpec(
                    Path.Combine(StartupDir, WorkerLinkName), autostart.Target,
                    Path.GetDirectoryName(workerExe) ?? baseDir, icon,
                    autostart.Description));
            }

            var staged = new List<(string Staged, string Destination)>();
            try
            {
                foreach (ShortcutSpec shortcut in shortcuts)
                {
                    string directory = Path.GetDirectoryName(shortcut.Destination)!;
                    Directory.CreateDirectory(directory);
                    string temp = Path.Combine(
                        directory,
                        "." + Path.GetFileNameWithoutExtension(shortcut.Destination) + "." +
                        Guid.NewGuid().ToString("N") + ".new.lnk");
                    CreateShortcut(
                        temp, shortcut.Target, string.Empty, shortcut.WorkDir, shortcut.Icon, shortcut.Description);
                    staged.Add((temp, shortcut.Destination));
                }

                CommitStagedShortcuts(staged);
            }
            finally
            {
                foreach ((string temp, _) in staged)
                {
                    TryDelete(temp);
                }
            }

            Console.WriteLine($"已创建:{Path.Combine(StartMenuDir, GuiLinkName)}");
            Console.WriteLine($"已创建:{Path.Combine(DesktopDir, DesktopLinkName)}(覆盖旧入口)");
            if (managedByTask)
            {
                // **只"不创建"不够。** 上一次安装留下的那个 .lnk 还在,它与计划任务并存,
                // 登录时会各拉起一个 Worker —— 此前这里却打印"已避免双启动"。
                WorkerAutostart.RemoveStartupShortcut(
                    StartupDir, Console.WriteLine, Console.Error.WriteLine);
                Console.WriteLine("开机自启已由计划任务接管,未创建开机快捷方式(避免双启动)。");
            }
            else
            {
                Console.WriteLine(
                    $"已创建:{Path.Combine(StartupDir, WorkerLinkName)}(开机自启,续跑引擎" +
                    (autostart.Hidden ? ",无窗口)" : ",会弹控制台窗口)"));
            }

            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"快捷方式操作失败:{ex.Message}");
            return 1;
        }
    }

    private sealed record ShortcutSpec(
        string Destination,
        string Target,
        string WorkDir,
        string Icon,
        string Description);

    /// <summary>
    /// 三个入口作为一个事务提交。任一步失败时恢复原文件或删除本轮新建文件，
    /// 避免安装运行时回滚后留下指向已删除 exe 的半套快捷方式。
    /// </summary>
    public static void CommitStagedShortcuts(
        IReadOnlyList<(string Staged, string Destination)> staged,
        Action<string, string, bool>? moveFile = null,
        Action<string, string, bool>? copyFile = null,
        Action<string>? deleteFile = null)
    {
        ArgumentNullException.ThrowIfNull(staged);
        moveFile ??= (source, destination, overwrite) => File.Move(source, destination, overwrite);
        copyFile ??= (source, destination, overwrite) => File.Copy(source, destination, overwrite);
        deleteFile ??= File.Delete;
        string operationId = Guid.NewGuid().ToString("N");
        var snapshots = new List<(string Destination, string Backup, bool Existed)>();
        bool preserveBackups = false;

        try
        {
            foreach ((string _, string destination) in staged)
            {
                string backup = destination + ".airesume-backup-" + operationId;
                bool existed = File.Exists(destination);
                if (existed)
                {
                    copyFile(destination, backup, false);
                }
                snapshots.Add((destination, backup, existed));
            }

            foreach ((string source, string destination) in staged)
            {
                moveFile(source, destination, true);
            }
        }
        catch (Exception commitError)
        {
            var rollbackErrors = new List<Exception>();
            foreach ((string destination, string backup, bool existed) in snapshots.AsEnumerable().Reverse())
            {
                try
                {
                    if (existed)
                    {
                        copyFile(backup, destination, true);
                    }
                    else if (File.Exists(destination))
                    {
                        deleteFile(destination);
                    }
                }
                catch (Exception rollbackError) when (rollbackError is IOException or UnauthorizedAccessException)
                {
                    rollbackErrors.Add(rollbackError);
                }
            }

            if (rollbackErrors.Count > 0)
            {
                preserveBackups = true;
                throw new InvalidOperationException(
                    BuildIncompleteRollbackMessage("快捷方式提交失败且回滚不完整", snapshots),
                    new AggregateException(new[] { commitError }.Concat(rollbackErrors)));
            }
            throw;
        }
        finally
        {
            if (!preserveBackups)
            {
                foreach ((string _, string backup, _) in snapshots)
                {
                    TryDelete(backup);
                }
            }
        }
    }

    /// <summary>
    /// 三个入口作为一个事务移除。删除失败时恢复原入口；若恢复也失败，
    /// 保留同目录备份并把恢复路径放进异常，卸载器据此停止删除运行时。
    /// </summary>
    public static void RemoveShortcutsTransaction(
        IReadOnlyList<string> destinations,
        Action<string, string, bool>? copyFile = null,
        Action<string>? deleteFile = null)
    {
        ArgumentNullException.ThrowIfNull(destinations);
        copyFile ??= (source, destination, overwrite) => File.Copy(source, destination, overwrite);
        deleteFile ??= File.Delete;
        string operationId = Guid.NewGuid().ToString("N");
        var snapshots = new List<(string Destination, string Backup, bool Existed)>();
        bool preserveBackups = false;

        try
        {
            foreach (string destination in destinations)
            {
                string backup = destination + ".airesume-backup-" + operationId;
                bool existed = File.Exists(destination);
                if (existed)
                {
                    copyFile(destination, backup, false);
                }
                snapshots.Add((destination, backup, existed));
            }

            foreach ((string destination, _, bool existed) in snapshots)
            {
                if (existed)
                {
                    deleteFile(destination);
                }
            }
        }
        catch (Exception removalError)
        {
            var rollbackErrors = new List<Exception>();
            foreach ((string destination, string backup, bool existed) in snapshots.AsEnumerable().Reverse())
            {
                if (!existed)
                {
                    continue;
                }

                try
                {
                    copyFile(backup, destination, true);
                }
                catch (Exception rollbackError) when (rollbackError is IOException or UnauthorizedAccessException)
                {
                    rollbackErrors.Add(rollbackError);
                }
            }

            if (rollbackErrors.Count > 0)
            {
                preserveBackups = true;
                throw new InvalidOperationException(
                    BuildIncompleteRollbackMessage("快捷方式删除失败且回滚不完整", snapshots),
                    new AggregateException(new[] { removalError }.Concat(rollbackErrors)));
            }
            throw;
        }
        finally
        {
            if (!preserveBackups)
            {
                foreach ((string _, string backup, _) in snapshots)
                {
                    TryDelete(backup);
                }
            }
        }
    }

    private static string BuildIncompleteRollbackMessage(
        string prefix,
        IReadOnlyList<(string Destination, string Backup, bool Existed)> snapshots)
    {
        string[] recovery = snapshots
            .Where(s => s.Existed && File.Exists(s.Backup))
            .Select(s => Path.GetFullPath(s.Backup))
            .ToArray();
        string material = recovery.Length > 0
            ? string.Join("; ", recovery)
            : string.Join("; ", snapshots.Select(s => Path.GetFullPath(s.Destination)));
        return $"{prefix};恢复材料:{material}";
    }

    private static string? ReadOption(string[] args, string name)
    {
        int i = Array.FindIndex(args, a => string.Equals(a, name, StringComparison.OrdinalIgnoreCase));
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>经 WScript.Shell COM 创建 .lnk(Windows 内建,不引入新依赖)。</summary>
    private static void CreateShortcut(
        string linkPath, string target, string arguments, string workDir, string iconPath, string description)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(linkPath)!);

        Type? shellType = Type.GetTypeFromProgID("WScript.Shell");
        if (shellType is null)
        {
            throw new PlatformNotSupportedException("本机没有 WScript.Shell,无法创建快捷方式。");
        }

        object? shell = Activator.CreateInstance(shellType);
        if (shell is null)
        {
            throw new PlatformNotSupportedException("无法创建 WScript.Shell 实例。");
        }

        try
        {
            object? link = shellType.InvokeMember(
                "CreateShortcut", System.Reflection.BindingFlags.InvokeMethod, null, shell, new object[] { linkPath });
            if (link is null)
            {
                throw new InvalidOperationException("CreateShortcut 返回空。");
            }

            Type linkType = link.GetType();
            void Set(string name, string value) => linkType.InvokeMember(
                name, System.Reflection.BindingFlags.SetProperty, null, link, new object[] { value });

            Set("TargetPath", target);
            Set("Arguments", arguments);
            Set("WorkingDirectory", workDir);
            Set("Description", description);
            if (File.Exists(iconPath))
            {
                Set("IconLocation", iconPath + ",0");
            }

            linkType.InvokeMember("Save", System.Reflection.BindingFlags.InvokeMethod, null, link, null);
            Marshal.ReleaseComObject(link);
        }
        finally
        {
            Marshal.ReleaseComObject(shell);
        }
    }
}
