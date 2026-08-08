namespace AiResume.Tests;

/// <summary>
/// 测试用临时目录的唯一出口。
///
/// 起因是 2026-08-08 在 <c>%TEMP%</c> 里数出来的账:
/// <c>claude-oauth-tests-*</c> 1171 个、<c>s5d-powerloss-*</c> 227 个、
/// <c>s10o-fault-*</c> 53 个、<c>s5c-*</c> 48 个 —— 合计 1499 个目录,
/// 全部是测试直接往 <see cref="Path.GetTempPath"/> 底下建、建完不收的。
/// 每跑一次测试就多几十个,**用户打开 %TEMP% 看到的是一屏我们的垃圾**。
///
/// 这和探测足迹是同一类毛病(见 docs/LESSONS.md 第十节):
/// 干活的时候只想着自己要什么,没想过在别人家里留下了什么。
///
/// 三条约束:
/// <list type="number">
/// <item>所有临时目录建在**一个** session 根下,收的时候收一次就干净;</item>
/// <item>进程退出时删自己那个根 —— 正常结束的运行不留任何东西;</item>
/// <item>启动时扫掉**上次崩溃**留下的旧根(按时间,且只认我们自己的目录名)。
///       只靠 ProcessExit 是不够的:测试被 Ctrl+C 或宿主崩掉时它不会跑。</item>
/// </list>
/// </summary>
internal static class TestTemp
{
    /// <summary>我们自己的地盘。只有这个名字下面的东西才允许被扫。</summary>
    private const string Container = "airesume-tests";

    /// <summary>比这个还旧的 session 根视为上次残留。留足一次长测试的时间。</summary>
    private static readonly TimeSpan StaleAfter = TimeSpan.FromHours(6);

    private static readonly string Root = Path.Combine(
        Path.GetTempPath(), Container, $"{Environment.ProcessId:x8}-{Guid.NewGuid():N}");

    static TestTemp()
    {
        Directory.CreateDirectory(Root);
        AppDomain.CurrentDomain.ProcessExit += (_, _) => TryDeleteTree(Root);
        SweepStale();
    }

    /// <summary>建一个本次运行专属的目录。<paramref name="prefix"/> 只用于人读,不参与清理判定。</summary>
    public static string NewDir(string prefix)
    {
        string dir = Path.Combine(Root, prefix + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>建一个本次运行专属的文件路径(不创建文件)。</summary>
    public static string NewFile(string prefix, string extension)
        => Path.Combine(Root, prefix + "-" + Guid.NewGuid().ToString("N") + extension);

    /// <summary>
    /// 只给路径,**不创建**。用于"这个目录不存在"本身就是被测前提的场景
    /// (例如 Cline 适配器在 hooks 目录不存在时应报未安装)。
    /// 和 <see cref="NewDir"/> 分开是必须的:用 NewDir 会把前提创没了,
    /// 测试照样绿,但它已经不再测那件事。
    /// </summary>
    public static string NewPath(string prefix)
        => Path.Combine(Root, prefix + "-" + Guid.NewGuid().ToString("N"));

    /// <summary>
    /// 扫掉上次崩溃留下的 session 根。
    ///
    /// **只删我们自己容器下、且明显够旧的**:名字对不上、时间读不出来、
    /// 或者可能是另一个正在跑的测试进程建的,一律跳过。
    /// 清理逻辑宁可残留也不能误删 —— 这条在本项目已经写进红线。
    /// </summary>
    private static void SweepStale()
    {
        string container = Path.Combine(Path.GetTempPath(), Container);
        if (!Directory.Exists(container))
        {
            return;
        }

        DateTime cutoff = DateTime.UtcNow - StaleAfter;
        foreach (string dir in SafeEnumerate(container))
        {
            if (string.Equals(dir, Root, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            try
            {
                if (Directory.GetLastWriteTimeUtc(dir) < cutoff)
                {
                    TryDeleteTree(dir);
                }
            }
            catch (Exception)
            {
                // 读不到时间就不动它。
            }
        }
    }

    private static string[] SafeEnumerate(string path)
    {
        try
        {
            return Directory.GetDirectories(path);
        }
        catch (Exception)
        {
            return Array.Empty<string>();
        }
    }

    private static void TryDeleteTree(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (Exception)
        {
            // 有文件被占着删不掉:留给下一次 SweepStale,不因清理失败让测试失败。
        }
    }
}
