using AiResume.Worker.Notifications;
using AiResume.Worker.Products;

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
    /// <summary>安装目标。与旧系统同层级,便于用户按同一心智找它。</summary>
    public static string DefaultTarget => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AI Resume");

    /// <summary>需要装进目标目录的项目(输出目录里的全部文件合并到同一层)。</summary>
    private static readonly string[] Projects = ["AiResume.Gui", "AiResume.Worker", "AiResume.Hook"];

    public static int Run(string[] args)
    {
        bool uninstall = args.Any(a => string.Equals(a, "uninstall", StringComparison.OrdinalIgnoreCase));
        string target = ReadOption(args, "--target") ?? DefaultTarget;

        try
        {
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
        IReadOnlyList<string> sources = ResolveSources(ReadOption(args, "--from"));
        if (sources.Count == 0)
        {
            Console.Error.WriteLine("找不到任何构建产物。请先 dotnet build,或用 --from 指定 src 根目录。");
            return 1;
        }

        // 目标目录里的旧副本正在跑时文件被锁,复制会中途失败并留下半套产物。
        // **只停目标目录里的**,不碰用户从仓库跑的开发实例。
        StopRunningIn(target);

        Directory.CreateDirectory(target);
        int files = 0;
        foreach (string src in sources)
        {
            files += CopyTree(src, target);
        }

        Console.WriteLine($"已安装 {files} 个文件到 {target}");

        string guiExe = Path.Combine(target, "AiResume.Gui.exe");
        string workerExe = Path.Combine(target, "AiResume.Worker.exe");
        string hookExe = Path.Combine(target, HookExecutable.FileName);

        if (!File.Exists(guiExe) || !File.Exists(workerExe))
        {
            Console.Error.WriteLine("安装目录里缺少 GUI 或 Worker,拒绝继续创建入口(否则会造出指向空气的快捷方式)。");
            return 1;
        }

        // 快捷方式复用 ShortcutCommand:它已经处理了桌面/开始菜单/启动项三处,
        // 这里只是把目标从 bin 换成安装目录。
        // 图标显式传安装目录里的那份:不传的话 ShortcutCommand 会按 guiExe 的目录推,
        // 虽然结果相同,但显式写出来才不会在将来某次重构里又漂回"跑安装的那个目录"。
        int rc = ShortcutCommand.Run([
            "shortcuts", "--gui", guiExe, "--worker", workerExe,
            "--icon", Path.Combine(target, "icon.ico")]);
        if (rc != 0)
        {
            return rc;
        }

        bool hooksOk = ReconcileHooks(hookExe);

        Console.WriteLine();
        Console.WriteLine("入口已全部指向安装目录,与仓库路径脱钩(改名/清 bin/换分支都不再影响)。");
        Console.WriteLine("改动代码后需重新运行 install 才生效。");

        // 通知源没对齐就**不要报告成功**。退出码 0 加一句"入口已全部指向安装目录"
        // 正是审计 B3 里那条骗人的输出:命令说成功,五个通知源全是关的。
        if (!hooksOk)
        {
            Console.Error.WriteLine("注意:部分通知源未能启用(见上方警告),完成通知可能收不到。");
            return 2;
        }

        return 0;
    }

    private static int Uninstall(string target)
    {
        StopRunningIn(target);
        ShortcutCommand.Run(["shortcuts", "uninstall"]);

        // 钩子必须**逐个源关掉**而不是删配置文件:用户 ~/.claude/settings.json 里
        // 还有他自己的钩子和一堆无关设置,整份删等于毁掉用户配置。
        var registry = new NotificationRegistry();
        IReadOnlyList<NotificationProviderStatus> before = registry.ProbeAll();

        // **关掉之前先把"本来开着哪几个"记下来。** 关完再问就永远问不出来了——
        // 重装恢复不了通知源这个缺陷(审计 B3)的根就在这里:
        // 卸载把现状清空,而现状是当时唯一的依据。
        SaveIntent(NotifyIntent.FromProbe(before));

        foreach (NotificationProviderStatus s in before)
        {
            if (s.IsEnabled)
            {
                registry.SetEnabled(s.Kind, false, string.Empty);
                Console.WriteLine($"已关闭通知源 {s.Kind}");
            }
        }

        if (Directory.Exists(target))
        {
            // **状态子目录必须留下。** 里面是 DPAPI 加密的飞书凭据、运行数据库和
            // 完成事件队列 —— 整树删掉,重装之后要重新填 app secret,而卸载的人
            // 想删的只是程序本身。要连状态一起清,得显式再删一次 state\。
            string state = Path.Combine(target, ShadowPaths.StateFolder);
            bool keepState = Directory.Exists(state);

            foreach (string entry in Directory.EnumerateFileSystemEntries(target))
            {
                if (keepState && string.Equals(
                        Path.GetFileName(entry), ShadowPaths.StateFolder, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (Directory.Exists(entry))
                {
                    Directory.Delete(entry, recursive: true);
                }
                else
                {
                    File.Delete(entry);
                }
            }

            if (keepState)
            {
                Console.WriteLine($"已删除程序文件,保留状态目录 {state}");
                Console.WriteLine("(内含加密的飞书凭据与运行记录;确实要清空请手动删除该目录。)");
            }
            else
            {
                Directory.Delete(target, recursive: true);
                Console.WriteLine($"已删除 {target}");
            }
        }

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
                // 先关后开:适配器按"命令里含我们的文件名"识别自己的条目,
                // 直接再开一次会留下两条(旧路径一条、新路径一条)。
                registry.SetEnabled(kind, false, string.Empty);
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

        // 只记真正写成功的:把失败的也记进意图,下次安装会再试一遍失败的动作,
        // 而界面读的是探测结果,两边会长期对不上。
        SaveIntent(done.Select(k => k.ToString()).ToList());

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

    private static int CopyTree(string sourceDir, string targetDir)
    {
        int count = 0;
        foreach (string file in Directory.EnumerateFiles(sourceDir, "*", SearchOption.AllDirectories))
        {
            string rel = Path.GetRelativePath(sourceDir, file);
            string dest = Path.Combine(targetDir, rel);
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

    /// <summary>只终止**从目标目录运行**的实例;用户在仓库里跑的开发实例不受影响。</summary>
    private static void StopRunningIn(string target)
    {
        string normalized = Path.GetFullPath(target).TrimEnd('\\');
        foreach (string name in new[] { "AiResume.Gui", "AiResume.Worker" })
        {
            foreach (System.Diagnostics.Process p in System.Diagnostics.Process.GetProcessesByName(name))
            {
                try
                {
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
