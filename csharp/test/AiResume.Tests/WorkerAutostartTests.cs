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

    [Fact]
    public void 计划任务按根路径定义文件探知无需提权也无需起PowerShell()
    {
        string path = WorkerAutostart.ScheduledTaskDefinitionPath;

        Assert.EndsWith(Path.Combine("System32", "Tasks", "AI Resume 续跑引擎"), path, StringComparison.Ordinal);
        Assert.True(WorkerAutostart.IsScheduledTaskRegistered(
            probed => string.Equals(probed, path, StringComparison.OrdinalIgnoreCase)));
        Assert.False(WorkerAutostart.IsScheduledTaskRegistered(_ => false));
    }
}
