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
    /// 用户是否已用 <c>scripts/register-autostart.ps1</c> 把自启升级成了计划任务。
    ///
    /// 已升级时 install **不能再建 Startup 快捷方式** —— 两条链路都在,登录时会各拉起
    /// 一个 Worker,抢同一份 SQLite 与 Named Pipe。此前只能靠在末尾打一行"记得重跑脚本"
    /// 提醒用户,而提醒是会被忘记的。
    /// </summary>
    public static bool IsScheduledTaskRegistered(Func<string, bool>? fileExists = null) =>
        (fileExists ?? File.Exists)(ScheduledTaskDefinitionPath);

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
