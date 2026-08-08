namespace AiResume.Worker;

/// <summary>
/// Stage 2 shadow 目录解析(唯一来源):
/// 默认 %LOCALAPPDATA%\ClaudeResumeShadow,可经环境变量 AIRESUME_SHADOW_DIR 覆盖。
/// 全部持久化(数据库/机密/日志)落在该目录下,绝不触碰生产 AppDir。
/// </summary>
public static class ShadowPaths
{
    public const string EnvOverride = "AIRESUME_SHADOW_DIR";
    public const string DefaultRelative = "ClaudeResumeShadow";

    public static string Root
    {
        get
        {
            string? env = Environment.GetEnvironmentVariable(EnvOverride);
            string root = string.IsNullOrWhiteSpace(env)
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), DefaultRelative)
                : env;
            return root;
        }
    }

    public static string RunDatabasePath => Path.Combine(Root, "runs.db");

    /// <summary>结构化日志目录(按日滚动文件)。</summary>
    public static string LogsDirectory => Path.Combine(Root, "logs");

    /// <summary>DPAPI 机密目录(DpapiSecretStore 的 root 参数)。</summary>
    public static string SecretsRoot => Root;
}
