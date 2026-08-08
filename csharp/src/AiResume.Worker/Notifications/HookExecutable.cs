namespace AiResume.Worker.Notifications;

/// <summary>
/// 定位 <c>AiResume.Hook.exe</c> 的真实路径。
///
/// 存在的理由是一次实测事故:完成通知的 Stop hook 曾被写成裸文件名
/// <c>AiResume.Hook.exe</c>,依赖它在 PATH 上——而它不在。于是钩子被写进用户的
/// <c>~/.claude/settings.json</c>、界面显示"已启用"、探测也报"已安装",
/// 但每次任务结束时命令根本执行不了,事件队列永远是空的。
/// **失败是完全静默的**:除非有人去数事件文件,否则看不出来。
///
/// 因此这里有两条纪律:
/// 1. 写进用户配置的必须是**绝对路径**;
/// 2. 找不到就返回 null,由调用方**拒绝启用并说明原因**——
///    宁可让人看到"启用失败",也不要留一个假装已启用的坏钩子。
/// </summary>
public static class HookExecutable
{
    public const string FileName = "AiResume.Hook.exe";

    /// <summary>返回 hook 可执行文件的绝对路径;找不到返回 null。</summary>
    public static string? TryResolve()
    {
        foreach (string candidate in Candidates())
        {
            if (!string.IsNullOrWhiteSpace(candidate) && File.Exists(candidate))
            {
                return Path.GetFullPath(candidate);
            }
        }

        return null;
    }

    private static IEnumerable<string> Candidates()
    {
        // 1. 与当前程序同目录——正式安装后应当是这一条命中。
        yield return Path.Combine(AppContext.BaseDirectory, FileName);

        // 2. 开发布局:各项目各自输出到 src/<Project>/bin/<Cfg>/<Tfm>/,
        //    Hook 不会被复制到 Worker/GUI 的输出目录里。从当前输出目录往上找到 src/,
        //    再进 AiResume.Hook 的同名配置目录。
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        string? tfm = dir.Name;                 // net10.0-windows
        string? cfg = dir.Parent?.Name;         // Debug / Release
        DirectoryInfo? srcRoot = dir.Parent?.Parent?.Parent?.Parent; // …/csharp/src
        if (tfm is not null && cfg is not null && srcRoot is not null)
        {
            yield return Path.Combine(srcRoot.FullName, "AiResume.Hook", "bin", cfg, tfm, FileName);
        }

        // 3. PATH 兜底:装过全局工具时可能命中。放最后,因为它最不可控。
        string? pathVar = Environment.GetEnvironmentVariable("PATH");
        if (!string.IsNullOrEmpty(pathVar))
        {
            foreach (string p in pathVar.Split(Path.PathSeparator))
            {
                if (!string.IsNullOrWhiteSpace(p))
                {
                    yield return Path.Combine(p.Trim(), FileName);
                }
            }
        }
    }
}
