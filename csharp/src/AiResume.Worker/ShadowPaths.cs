namespace AiResume.Worker;

/// <summary>
/// 状态目录解析(唯一来源)。
///
/// 默认 <c>%LOCALAPPDATA%\AI Resume\state</c>,可经环境变量
/// <c>AIRESUME_SHADOW_DIR</c> 覆盖(测试与并行运行用)。
/// 全部持久化——运行数据库、DPAPI 机密、日志、完成事件队列——都落在该目录下。
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

    private static string LocalAppData =>
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

    public static string DefaultRoot => Path.Combine(LocalAppData, ProductFolder, StateFolder);

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

    /// <summary>建状态根目录;进程入口处调用。v1 的 ClaudeResumeShadow 搬迁已于 2026-10 随 v1 退役一并删除。</summary>
    public static string EnsureRoot()
    {
        string root = Root;
        Directory.CreateDirectory(root);
        return root;
    }
}
