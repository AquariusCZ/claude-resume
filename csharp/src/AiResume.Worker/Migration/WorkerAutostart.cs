using System.Xml;
using System.Xml.Linq;

namespace AiResume.Worker.Migration;

/// <summary>
/// 决定**开机自启的 .lnk 该指向谁**。
///
/// 续跑引擎(Worker)是控制台程序,而且必须留在这个形态 ——
/// <c>install</c> / <c>notify</c> / <c>feishu-check</c> 都靠 stdout 说话,
/// 改成 WinExe 会让这些 CLI 变哑。但控制台程序被 Explorer 从 Startup 的 .lnk
/// 拉起时必然分到一个控制台窗口,于是每次开机弹黑框
/// (<c>.lnk</c> 只有 Normal/Minimized/Maximized,**没有 Hidden 档**,
/// 最小化仍然会闪一下)。
///
/// **为什么不用计划任务**:S4U 计划任务能让进程不在交互桌面上跑,本来是更好的答案,
/// 但 2026-08-13 在目标机器上实测,非提权进程注册计划任务一律
/// <c>0x80070005 拒绝访问</c>(root/子目录 × S4U/Interactive 四种组合全试过,
/// 关掉沙箱结果一致,<c>elevated=False</c>)。install 是非提权跑的,
/// 为一个自启入口去弹 UAC 不值得。
///
/// 所以自启指向 <c>AiResume.Launcher.exe</c>(WinExe 垫片),由它用
/// <c>CreateNoWindow</c> 拉起 Worker。垫片缺席时退回直接指向 Worker ——
/// 会弹黑框,但**有自启比没自启重要**:没有它,限额恢复后不会有任何东西去续跑。
/// </summary>
public static class WorkerAutostart
{
    public const string LauncherFileName = "AiResume.Launcher.exe";

    /// <summary>开机自启快捷方式的文件名。沿用旧名字,升级时就地覆盖,不会留下两个入口。</summary>
    public const string StartupLinkName = "AI Resume 续跑引擎.lnk";

    /// <param name="Target">.lnk 应该指向的可执行文件。</param>
    /// <param name="Hidden">true = 经垫片启动,登录时无窗口;false = 直指 Worker,会弹控制台窗口。</param>
    public sealed record AutostartTarget(string Target, bool Hidden)
    {
        public string Description => Hidden
            ? "AI Resume 续跑引擎(后台):限额恢复后按队列顺序继续"
            : "AI Resume 续跑引擎(后台,无垫片):限额恢复后按队列顺序继续";
    }

    /// <summary>
    /// 根路径计划任务的定义文件。Task Scheduler 把根路径任务落成
    /// <c>%WINDIR%\System32\Tasks\&lt;任务名&gt;</c> 这么一个文件,**非提权可读**
    /// (2026-08-14 实测),所以 install 不必起 PowerShell 就能廉价探知它在不在。
    /// </summary>
    public static string ScheduledTaskDefinitionPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.Windows),
        "System32", "Tasks", "AI Resume 续跑引擎");

    /// <summary>
    /// 计划任务**是否真的在管这台机器的自启**。
    ///
    /// 已升级时 install 不能再建 Startup 快捷方式 —— 两条链路都在,登录时会各拉起
    /// 一个 Worker,抢同一份 SQLite 与 Named Pipe。
    ///
    /// **但"文件在"不等于"在管"。** 只看存在性会踩这个坑:注册过任务之后卸载重装、
    /// 或改用 <c>--target</c> 装到别的目录,任务的 action 指向的 Worker 已经不存在,
    /// 而 install 仍判定"已由计划任务接管"、不建快捷方式并返回 0 ——
    /// 结果是**零自启,却对外报成功**。任务被禁用也是同样的下场。
    ///
    /// 所以这里读任务定义 XML(根路径任务落成 <c>%WINDIR%\System32\Tasks\&lt;名&gt;</c>,
    /// 非提权可读),逐项核验:能解析、未被禁用、且至少一个 action 指向**本次安装目录**
    /// 的 Worker。任何一项存疑都返回 false —— 宁可多一个快捷方式(最坏是双启动,
    /// 而全局互斥体会挡住第二个),也不能让机器一个自启入口都没有。
    /// </summary>
    public static bool IsScheduledTaskManagingAutostart(
        string workerExe,
        Func<string, string?>? readDefinition = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workerExe);
        readDefinition ??= DefaultReadDefinition;

        string? xml = readDefinition(ScheduledTaskDefinitionPath);
        if (string.IsNullOrWhiteSpace(xml))
        {
            return false;
        }

        XDocument document;
        try
        {
            document = XDocument.Parse(xml);
        }
        catch (XmlException)
        {
            return false;
        }

        XNamespace ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";

        // <Settings><Enabled>false</Enabled> 的任务不会在登录时跑。
        string? enabled = document.Root?.Element(ns + "Settings")?.Element(ns + "Enabled")?.Value;
        if (string.Equals(enabled?.Trim(), "false", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        string expected = NormalizeExecutable(workerExe);
        return document.Descendants(ns + "Exec")
            .Select(exec => exec.Element(ns + "Command")?.Value)
            .Any(command => !string.IsNullOrWhiteSpace(command) &&
                            string.Equals(
                                NormalizeExecutable(command!),
                                expected,
                                StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>任务 XML 里的 Command 可能带引号,也可能含 %VAR%。</summary>
    private static string NormalizeExecutable(string value)
    {
        string trimmed = Environment.ExpandEnvironmentVariables(value.Trim()).Trim('"');
        try
        {
            return Path.GetFullPath(trimmed);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return trimmed;
        }
    }

    private static string? DefaultReadDefinition(string path)
    {
        try
        {
            return File.Exists(path) ? File.ReadAllText(path) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 读不到就当"没在管":宁可多建一个快捷方式,也不能让机器没有自启入口。
            return null;
        }
    }

    /// <summary>
    /// 结束计划任务当前的运行实例。**安装前必须先做这一步。**
    ///
    /// 2026-08-14 实测的坑:任务用 S4U 跑在会话 0,而 install 是会话 1 的非提权进程,
    /// 读不到它的 <c>MainModule.FileName</c> —— <c>StopRunningIn</c> 那个"只杀本目录进程"
    /// 的保守判据于是直接跳过它,Worker 从没被停过,DLL 一直锁着,
    /// 安装失败并进入**不完整回滚**("请勿把当前安装视为可用")。
    /// 而任务的失败自动重启还会把它再拉回来,和安装器反复抢文件。
    ///
    /// <c>schtasks /End</c> 由任务属主执行,**不需要提权**(实测),
    /// 且比按进程名猜杀安全:结束的是这个任务自己的实例。
    /// 任务不存在或结束失败都只警告 —— 真正的失败会在后续复制阶段以文件锁的形式暴露。
    /// </summary>
    public static bool StopScheduledTaskInstance(
        Func<string, string, (int ExitCode, string Output, string Error)>? run = null,
        Action<string>? log = null)
    {
        run ??= RunProcess;
        try
        {
            (int exitCode, _, _) = run("schtasks.exe", $"/End /TN \"{TaskName}\"");
            if (exitCode == 0)
            {
                log?.Invoke($"已结束计划任务的运行实例:{TaskName}(释放安装目录文件锁)");
                return true;
            }

            return false;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>计划任务名。与 <c>scripts/register-autostart.ps1</c> 保持一致。</summary>
    public const string TaskName = "AI Resume 续跑引擎";

    private static (int ExitCode, string Output, string Error) RunProcess(string fileName, string arguments)
    {
        var psi = new System.Diagnostics.ProcessStartInfo(fileName)
        {
            Arguments = arguments,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        using System.Diagnostics.Process process = System.Diagnostics.Process.Start(psi)!;
        string output = process.StandardOutput.ReadToEnd();
        string error = process.StandardError.ReadToEnd();
        if (!process.WaitForExit(15000))
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (Exception)
            {
            }

            return (-1, output, "schtasks 执行超时");
        }

        return (process.ExitCode, output, error);
    }

    /// <summary>
    /// 移除 Startup 里的自启快捷方式。计划任务接管后必须**主动删掉**它 ——
    /// 只"不创建"不够:上一次安装留下的那个还在,两条链路会各拉起一个 Worker。
    /// </summary>
    public static bool RemoveStartupShortcut(
        string startupDir,
        Action<string>? log = null,
        Action<string>? logError = null,
        Func<string, bool>? fileExists = null,
        Action<string>? deleteFile = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(startupDir);
        fileExists ??= File.Exists;
        deleteFile ??= File.Delete;

        string link = Path.Combine(startupDir, StartupLinkName);
        if (!fileExists(link))
        {
            return true;
        }

        try
        {
            deleteFile(link);
            log?.Invoke($"已移除开机快捷方式(自启改由计划任务负责):{link}");
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logError?.Invoke(
                $"警告:开机快捷方式删除失败,它与计划任务并存会在登录时各拉起一个 Worker:{link}({ex.Message})");
            return false;
        }
    }

    /// <summary>
    /// 解析自启目标。**纯函数 + 注入的存在性判定**,便于断言退化路径而不依赖真实文件。
    /// </summary>
    public static AutostartTarget Resolve(
        string workerExe,
        Func<string, bool>? fileExists = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workerExe);
        fileExists ??= File.Exists;

        string full = Path.GetFullPath(workerExe);
        string launcher = Path.Combine(Path.GetDirectoryName(full) ?? string.Empty, LauncherFileName);

        return fileExists(launcher)
            ? new AutostartTarget(launcher, Hidden: true)
            : new AutostartTarget(full, Hidden: false);
    }
}
