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
                Remove(Path.Combine(StartMenuDir, GuiLinkName));
                Remove(Path.Combine(StartupDir, WorkerLinkName));
                Remove(Path.Combine(DesktopDir, DesktopLinkName));
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

            // 图标缺失只警告不中止:没有图标的快捷方式仍然可用,
            // 但必须说出来 —— 否则用户只会看到"图标没了"却不知道为什么。
            if (!File.Exists(icon))
            {
                Console.Error.WriteLine($"警告:找不到图标 {icon},快捷方式将使用默认图标。");
            }

            CreateShortcut(
                Path.Combine(StartMenuDir, GuiLinkName), guiExe, string.Empty,
                Path.GetDirectoryName(guiExe) ?? baseDir, icon,
                "AI Resume 控制面:额度、续跑队列与完成通知");

            CreateShortcut(
                Path.Combine(StartupDir, WorkerLinkName), workerExe, string.Empty,
                Path.GetDirectoryName(workerExe) ?? baseDir, icon,
                "AI Resume 续跑引擎(后台):限额恢复后按队列顺序继续");

            // 桌面入口就地覆盖旧安装器留下的同名 .lnk —— 它指向 launcher.vbs(旧系统)。
            CreateShortcut(
                Path.Combine(DesktopDir, DesktopLinkName), guiExe, string.Empty,
                Path.GetDirectoryName(guiExe) ?? baseDir, icon,
                "AI Resume 控制面:额度、续跑队列与完成通知");

            Console.WriteLine($"已创建:{Path.Combine(StartMenuDir, GuiLinkName)}");
            Console.WriteLine($"已创建:{Path.Combine(StartupDir, WorkerLinkName)}(开机自启,续跑引擎)");
            Console.WriteLine($"已创建:{Path.Combine(DesktopDir, DesktopLinkName)}(覆盖旧入口)");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"快捷方式操作失败:{ex.Message}");
            return 1;
        }
    }

    private static string? ReadOption(string[] args, string name)
    {
        int i = Array.FindIndex(args, a => string.Equals(a, name, StringComparison.OrdinalIgnoreCase));
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }

    private static void Remove(string linkPath)
    {
        if (File.Exists(linkPath))
        {
            File.Delete(linkPath);
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
