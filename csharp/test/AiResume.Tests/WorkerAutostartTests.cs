using AiResume.Worker.Migration;
using Xunit;

namespace AiResume.Tests;

/// <summary>
/// 开机自启该指向谁。存在性判定是注入的,不依赖真实文件,也不动本机任何启动项。
/// </summary>
public sealed class WorkerAutostartTests
{
    private const string Worker = @"C:\Program Files\AI Resume\AiResume.Worker.exe";
    private const string Launcher = @"C:\Program Files\AI Resume\AiResume.Launcher.exe";

    [Fact]
    public void 有垫片时自启指向垫片且标记为无窗口()
    {
        WorkerAutostart.AutostartTarget target = WorkerAutostart.Resolve(
            Worker, path => string.Equals(path, Launcher, StringComparison.OrdinalIgnoreCase));

        Assert.Equal(Launcher, target.Target);
        Assert.True(target.Hidden);
    }

    [Fact]
    public void 没有垫片时退回直指Worker而不是干脆不装自启()
    {
        // 黑框难看,但**没有自启才是真故障**:限额恢复后不会有任何东西去续跑。
        WorkerAutostart.AutostartTarget target = WorkerAutostart.Resolve(Worker, _ => false);

        Assert.Equal(Worker, target.Target);
        Assert.False(target.Hidden);
    }

    [Fact]
    public void 垫片必须在Worker同目录下找而不是当前工作目录()
    {
        var probed = new List<string>();

        WorkerAutostart.Resolve(Worker, path => { probed.Add(path); return false; });

        Assert.Equal([Launcher], probed);
    }

    [Fact]
    public void 相对路径先规范化再判定()
    {
        string cwd = Directory.GetCurrentDirectory();
        var probed = new List<string>();

        WorkerAutostart.AutostartTarget target = WorkerAutostart.Resolve(
            "AiResume.Worker.exe", path => { probed.Add(path); return false; });

        Assert.Equal(Path.Combine(cwd, "AiResume.Worker.exe"), target.Target);
        Assert.Equal([Path.Combine(cwd, WorkerAutostart.LauncherFileName)], probed);
    }

    [Fact]
    public void 两种目标的描述必须能区分开()
    {
        string hidden = WorkerAutostart.Resolve(Worker, _ => true).Description;
        string visible = WorkerAutostart.Resolve(Worker, _ => false).Description;

        Assert.NotEqual(hidden, visible);
        Assert.Contains("无垫片", visible, StringComparison.Ordinal);
    }

    [Fact]
    public void 自启快捷方式沿用原文件名以便升级时就地覆盖()
    {
        // 换名字会在 Startup 里留下两个入口,登录时各拉起一个 Worker,
        // 抢同一份 SQLite 与 Named Pipe。
        Assert.Equal("AI Resume 续跑引擎.lnk", WorkerAutostart.StartupLinkName);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void 空的Worker路径直接拒绝(string workerExe)
    {
        Assert.Throws<ArgumentException>(() => WorkerAutostart.Resolve(workerExe));
    }

    private const string TaskNs = "http://schemas.microsoft.com/windows/2004/02/mit/task";

    private static string TaskXml(string command, string enabled = "true") => $"""
        <?xml version="1.0" encoding="UTF-16"?>
        <Task version="1.4" xmlns="{TaskNs}">
          <Settings><Enabled>{enabled}</Enabled><MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy></Settings>
          <Actions Context="Author">
            <Exec><Command>{command}</Command><WorkingDirectory>C:\Program Files\AI Resume</WorkingDirectory></Exec>
          </Actions>
        </Task>
        """;

    [Fact]
    public void 计划任务按根路径定义文件探知无需提权也无需起PowerShell()
    {
        string path = WorkerAutostart.ScheduledTaskDefinitionPath;

        Assert.EndsWith(Path.Combine("System32", "Tasks", "AI Resume 续跑引擎"), path, StringComparison.Ordinal);
        Assert.True(WorkerAutostart.IsScheduledTaskManagingAutostart(
            Worker,
            probed => string.Equals(probed, path, StringComparison.OrdinalIgnoreCase)
                ? TaskXml(Worker)
                : null));
    }

    [Theory]
    // 任务不存在。
    [InlineData(null)]
    // 被禁用的任务登录时不会跑。
    [InlineData("disabled")]
    // action 指向别的目录:卸载重装、或换 --target 装到别处之后就是这个样子。
    [InlineData("other-target")]
    // 定义文件损坏 / 不是合法 XML。
    [InlineData("garbage")]
    // 有任务但完全没有 Exec action。
    [InlineData("no-exec")]
    public void 任务存疑时一律当作没在管自启(string? shape)
    {
        // 宁可多建一个快捷方式(最坏是双启动,而全局互斥体会挡住第二个),
        // 也不能让机器一个自启入口都没有 —— 那是静默的零自启,还对外报成功。
        string? xml = shape switch
        {
            null => null,
            "disabled" => TaskXml(Worker, enabled: "false"),
            "other-target" => TaskXml(@"D:\Elsewhere\AiResume.Worker.exe"),
            "garbage" => "<Task><broken",
            _ => $"""<?xml version="1.0"?><Task xmlns="{TaskNs}"><Actions Context="Author" /></Task>""",
        };

        Assert.False(WorkerAutostart.IsScheduledTaskManagingAutostart(Worker, _ => xml));
    }

    [Fact]
    public void 任务命令带引号或环境变量也要认出来是同一个Worker()
    {
        string quoted = TaskXml("&quot;" + Worker + "&quot;");

        Assert.True(WorkerAutostart.IsScheduledTaskManagingAutostart(Worker, _ => quoted));
    }

    [Fact]
    public void 计划任务接管后必须主动删掉遗留的开机快捷方式()
    {
        string startup = TestTemp.NewDir("autostart-stale-link");
        string link = Path.Combine(startup, WorkerAutostart.StartupLinkName);
        File.WriteAllText(link, "stale");

        // 只"不创建"不够:上一次安装留下的那个还在,会与任务各拉起一个 Worker。
        Assert.True(WorkerAutostart.RemoveStartupShortcut(startup));
        Assert.False(File.Exists(link));
        Assert.True(WorkerAutostart.RemoveStartupShortcut(startup));
    }

    [Fact]
    public void 安装前用schtasks结束任务实例而不是按进程名猜杀()
    {
        // S4U 任务跑在会话 0,install 是会话 1 的非提权进程读不到它的 MainModule,
        // "只杀本目录进程"的判据会跳过它 → DLL 一直锁着 → 安装失败且回滚不完整。
        (string File, string Args)? invoked = null;

        bool ok = WorkerAutostart.StopScheduledTaskInstance(
            run: (file, args) => { invoked = (file, args); return (0, string.Empty, string.Empty); },
            log: _ => { });

        Assert.True(ok);
        Assert.Equal("schtasks.exe", invoked!.Value.File);
        Assert.Equal($"/End /TN \"{WorkerAutostart.TaskName}\"", invoked.Value.Args);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(-1)]
    public void 结束任务实例失败只返回false不抛出(int exitCode)
    {
        // 任务不存在或结束失败都不该中断安装:真正的问题会在复制阶段以文件锁暴露。
        Assert.False(WorkerAutostart.StopScheduledTaskInstance(
            run: (_, _) => (exitCode, string.Empty, "ERROR: task not found"),
            log: _ => { }));
        Assert.False(WorkerAutostart.StopScheduledTaskInstance(
            run: (_, _) => throw new System.ComponentModel.Win32Exception("schtasks missing"),
            log: _ => { }));
    }

    [Fact]
    public void 遗留快捷方式删不掉时明确警告而不是静默宣称已避免双启动()
    {
        var errors = new List<string>();

        bool ok = WorkerAutostart.RemoveStartupShortcut(
            @"C:\any",
            log: _ => { },
            logError: errors.Add,
            fileExists: _ => true,
            deleteFile: _ => throw new UnauthorizedAccessException("locked"));

        Assert.False(ok);
        Assert.Contains(errors, e => e.Contains("各拉起一个 Worker", StringComparison.Ordinal));
    }
}
