namespace AiResume.Worker;

/// <summary>
/// 状态目录解析(唯一来源)。
///
/// 默认 <c>%LOCALAPPDATA%\AI Resume\state</c>,可经环境变量
/// <c>AIRESUME_SHADOW_DIR</c> 覆盖(测试与并行运行用)。
/// 全部持久化——运行数据库、DPAPI 机密、日志、完成事件队列——都落在该目录下。
///
/// **为什么从 ClaudeResumeShadow 搬过来**:那个名字是 Stage 2 影子运行期留下的,
/// 当时要和现役 v1 的 <c>ClaudeResume</c> 目录并存才刻意叫 "Shadow"。
/// v1 退役之后它既不影子也不属于 ClaudeResume,只会让人以为是旧系统残留而误删——
/// 里面装着 DPAPI 加密的飞书凭据,删掉要重新填 app secret。
///
/// **为什么是子目录 state\ 而不是安装目录本身**:<c>install</c> 会往安装目录复制
/// 271 个文件、<c>uninstall</c> 会整树删除它。状态和二进制混在一层,
/// 一次卸载就把凭据和运行记录一起带走了。分成子目录后,卸载显式跳过 state\。
/// </summary>
public static class ShadowPaths
{
    public const string EnvOverride = "AIRESUME_SHADOW_DIR";

    /// <summary>安装目录名;与 <c>InstallCommand.DefaultTarget</c> 保持一致。</summary>
    public const string ProductFolder = "AI Resume";

    /// <summary>状态子目录名。<c>uninstall</c> 按这个名字跳过它。</summary>
    public const string StateFolder = "state";

    /// <summary>迁移前的旧位置;<see cref="TryMigrateLegacy"/> 会把内容搬过来。</summary>
    public const string LegacyRelative = "ClaudeResumeShadow";

    private static string LocalAppData =>
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

    public static string DefaultRoot => Path.Combine(LocalAppData, ProductFolder, StateFolder);

    public static string LegacyRoot => Path.Combine(LocalAppData, LegacyRelative);

    public static string Root
    {
        get
        {
            string? env = Environment.GetEnvironmentVariable(EnvOverride);
            return string.IsNullOrWhiteSpace(env) ? DefaultRoot : env;
        }
    }

    public static string RunDatabasePath => Path.Combine(Root, "runs.db");

    /// <summary>结构化日志目录(按日滚动文件)。</summary>
    public static string LogsDirectory => Path.Combine(Root, "logs");

    /// <summary>DPAPI 机密目录(DpapiSecretStore 的 root 参数)。</summary>
    public static string SecretsRoot => Root;

    /// <summary>true 表示状态根被 <c>AIRESUME_SHADOW_DIR</c> 显式指定。</summary>
    public static bool IsOverridden =>
        !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(EnvOverride));

    /// <summary>
    /// 建目录;**只有在使用默认位置时**才把旧 <c>ClaudeResumeShadow</c> 搬过来。
    /// 进程入口处调用。迁移失败不抛——搬不动最多是状态还在老地方,不该让程序起不来。
    ///
    /// **为什么必须判 IsOverridden(2026-08-08 真踩过):**
    /// <c>AIRESUME_SHADOW_DIR</c> 被测试用来把 Worker 子进程隔离到临时目录。
    /// 早先这里无条件迁移,而 legacyRoot 取的是**真实**的 %LOCALAPPDATA%\ClaudeResumeShadow
    /// —— 于是一个隔离测试把用户的生产状态(含 DPAPI 加密的飞书凭据)搬进了自己的临时目录,
    /// 测试收尾时一并删掉。这正是仓库测试红线要防的那类事故,而漏洞开在被测代码这一侧。
    /// 显式指定了状态根,就说明调用方自己决定状态放哪,这里没有任何东西该被"迁移"过去。
    /// </summary>
    public static string EnsureRoot()
    {
        string root = Root;
        Directory.CreateDirectory(root);

        if (IsOverridden)
        {
            return root;
        }

        try
        {
            TryMigrateLegacy(newRoot: root);
        }
        catch (Exception)
        {
            // 迁移是尽力而为;下次启动再试。
        }

        return root;
    }

    /// <summary>
    /// 把旧 <c>ClaudeResumeShadow</c> 的内容搬到新位置。返回搬动的条目数(0 表示无事可做)。
    ///
    /// 规则,按"宁可不搬也不覆盖"来定:
    /// - 新目录里已存在同名项时**跳过**,绝不覆盖——新的才是现役,旧的是历史;
    /// - 逐项移动而不是整目录改名:整体改名在目标已存在时直接失败,搬不动一半也搬不动另一半;
    /// - 搬完不删旧目录,只在空了的时候删——留一个空壳比误删有内容的目录安全。
    ///
    /// **DPAPI 机密可以随文件移动**:它按当前用户加密,换个路径仍然解得开。
    /// </summary>
    public static int TryMigrateLegacy(string? legacyRoot = null, string? newRoot = null)
    {
        string from = legacyRoot ?? LegacyRoot;
        string to = newRoot ?? Root;

        if (!Directory.Exists(from) || string.Equals(
                Path.GetFullPath(from).TrimEnd('\\'),
                Path.GetFullPath(to).TrimEnd('\\'),
                StringComparison.OrdinalIgnoreCase))
        {
            return 0;
        }

        Directory.CreateDirectory(to);
        int moved = 0;

        foreach (string entry in Directory.EnumerateFileSystemEntries(from))
        {
            string name = Path.GetFileName(entry);
            string target = Path.Combine(to, name);
            if (File.Exists(target) || Directory.Exists(target))
            {
                continue;   // 新位置已有同名项:现役优先,不覆盖。
            }

            try
            {
                if (Directory.Exists(entry))
                {
                    Directory.Move(entry, target);
                }
                else
                {
                    File.Move(entry, target, overwrite: false);
                }

                moved++;
            }
            catch (IOException)
            {
                // 被占用(例如 runs.db 正开着)就留在原地,下次启动再搬。
                // 迁移失败绝不能阻断启动。
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        try
        {
            if (!Directory.EnumerateFileSystemEntries(from).Any())
            {
                Directory.Delete(from);
            }
        }
        catch (IOException)
        {
        }

        return moved;
    }
}
