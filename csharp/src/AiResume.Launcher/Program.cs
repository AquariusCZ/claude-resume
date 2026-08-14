using System.Diagnostics;

namespace AiResume.Launcher;

/// <summary>
/// 开机自启的**无窗口启动垫片**。唯一职责:把同目录的 AiResume.Worker.exe
/// 以无控制台窗口的方式拉起来,然后立刻退出。
///
/// **为什么需要它**:续跑引擎(Worker)必须留在控制台程序形态 ——
/// <c>install</c> / <c>notify</c> / <c>feishu-check</c> 都靠 stdout 说话,
/// 改成 WinExe 会让这些 CLI 变哑。但控制台程序被 Explorer 从 Startup 的 .lnk
/// 拉起时必然分到一个控制台窗口,于是每次开机弹黑框。
///
/// 计划任务(S4U,进程不在交互桌面上跑)本来是更好的答案,但 2026-08-13 在目标机器
/// 上实测:非提权进程注册计划任务一律 <c>0x80070005 拒绝访问</c>
/// (root/子目录 × S4U/Interactive 四种组合全试过,沙箱内外一致)。
/// 让 install 为一个自启入口去弹 UAC 不值得,所以退回这个 20 行的垫片 ——
/// 它不需要任何特权。
/// </summary>
internal static class Program
{
    private const string WorkerFileName = "AiResume.Worker.exe";

    [STAThread]
    private static int Main()
    {
        string baseDir = AppContext.BaseDirectory;
        string worker = Path.Combine(baseDir, WorkerFileName);

        try
        {
            if (!File.Exists(worker))
            {
                Log($"找不到续跑引擎:{worker}");
                return 1;
            }

            // 已经有一个在跑就不要再拉一个:两个 Worker 会抢同一份 SQLite 与 Named Pipe。
            // 登录时正常不会有,但用户手动开过、或上一次会话没退干净时会有。
            if (IsWorkerAlreadyRunning(worker))
            {
                return 0;
            }

            // UseShellExecute=false + CreateNoWindow=true:子进程不分配控制台窗口。
            // 这正是 InstallCommand 立即启动 Worker 时用的同一套语义。
            var psi = new ProcessStartInfo(worker)
            {
                WorkingDirectory = baseDir,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using Process? started = Process.Start(psi);
            if (started is null)
            {
                Log("续跑引擎启动失败:Process.Start 返回 null");
                return 1;
            }

            return 0;
        }
        catch (Exception ex)
        {
            // 开机失败是**看不见的失败** —— 没有窗口、没有终端,不留下痕迹就等于没发生过。
            Log($"续跑引擎启动异常:{ex.GetType().Name}: {ex.Message}");
            return 1;
        }
    }

    private static bool IsWorkerAlreadyRunning(string workerPath)
    {
        foreach (Process process in Process.GetProcessesByName(
                     Path.GetFileNameWithoutExtension(WorkerFileName)))
        {
            using (process)
            {
                try
                {
                    // 只认同一个安装目录里的那个:仓库 bin 里跑着的开发构建不算数。
                    if (string.Equals(
                            process.MainModule?.FileName, workerPath, StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }
                }
                catch (Exception)
                {
                    // 读不到模块路径(权限/已退出)时不作数:宁可多拉一次,
                    // 也好过因为一次读取失败让开机自启彻底不发生。
                }
            }
        }

        return false;
    }

    /// <summary>尽力而为地留一行痕迹;日志本身失败绝不影响启动结果。</summary>
    private static void Log(string message)
    {
        try
        {
            string dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "AI Resume", "state", "logs");
            Directory.CreateDirectory(dir);
            File.AppendAllText(
                Path.Combine(dir, "launcher.log"),
                $"{DateTimeOffset.Now:O} {message}{Environment.NewLine}");
        }
        catch (Exception)
        {
        }
    }
}
