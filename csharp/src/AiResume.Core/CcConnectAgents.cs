namespace AiResume.Core;

/// <summary>
/// cc-connect 支持的 agent 类型白名单。cc-connect 一个项目死绑一个 agent,
/// 运行时切不了,所以由 AI Resume 记住选择、写进生成的 config.toml,重启后生效。
/// 白名单外的值写进 config.toml 会让 cc-connect 启动失败,因此所有入口都必须先 Normalize。
/// </summary>
public static class CcConnectAgents
{
    public const string Default = "claudecode";

    /// <summary>键=cc-connect 的 agent 标识,值=界面显示名。顺序即界面顺序。</summary>
    public static IReadOnlyList<(string Id, string Display)> Supported { get; } = new[]
    {
        ("claudecode", "Claude Code"),
        ("codex", "Codex"),
        ("cursor", "Cursor"),
        ("gemini", "Gemini CLI"),
        ("qoder", "Qoder CLI"),
        ("opencode", "OpenCode"),
    };

    /// <summary>
    /// 每个 agent 对应的 CLI 可执行文件候选名。**任一命中即视为已安装**。
    ///
    /// 之所以是候选**列表**而不是单个名字:各家改过命令名(Cursor 就有
    /// <c>cursor</c> 与 <c>cursor-agent</c> 两种叫法),写死一个会把装了的判成没装,
    /// 界面上直接置灰、用户根本选不了。多列几个的代价只是多几次 PATH 查找。
    ///
    /// 这里**不猜 cc-connect 内部到底 exec 哪一个**——二进制里的字符串是 agent
    /// 类型名,推不出可执行文件名。本表的用途仅限「界面提示装没装」,
    /// 判断错了最坏是提示不准,不会写出非法配置(那由 Normalize 兜底)。
    /// </summary>
    private static readonly Dictionary<string, string[]> Executables = new(StringComparer.OrdinalIgnoreCase)
    {
        ["claudecode"] = ["claude", "claude-code"],
        ["codex"] = ["codex"],
        ["cursor"] = ["cursor-agent", "cursor"],
        ["gemini"] = ["gemini"],
        ["qoder"] = ["qoder"],
        ["opencode"] = ["opencode"],
    };

    /// <summary>
    /// 该 agent 的 CLI 是否能在 PATH 上解析到。解析不到时选它会让 cc-connect 起不来。
    /// </summary>
    /// <param name="searchPath">
    /// 搜索路径,为 null 时用环境变量 PATH。**只为可测性存在**:
    /// 若直接依赖真实 PATH,"未安装应返回 false"这条只能挑一个"本机大概没装"的
    /// agent 来断言,而用户哪天真装了它,测试就假红。
    /// </param>
    public static bool IsInstalled(string? id, string? searchPath = null)
    {
        string normalized = Normalize(id);
        if (!Executables.TryGetValue(normalized, out string[]? names))
        {
            return false;
        }

        foreach (string name in names)
        {
            if (ResolveOnPath(name, searchPath) is not null)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>在给定搜索路径上按 PATHEXT 解析命令;找不到返回 null。</summary>
    private static string? ResolveOnPath(string command, string? searchPath)
    {
        string[] exts = (Environment.GetEnvironmentVariable("PATHEXT") ?? ".EXE;.CMD;.BAT")
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        foreach (string dir in (searchPath ?? Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
                     .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            string basePath;
            try
            {
                basePath = Path.Combine(dir, command);
            }
            catch (ArgumentException)
            {
                // PATH 里混进了非法字符的条目(实测存在),跳过而不是让整次探测失败。
                continue;
            }

            if (File.Exists(basePath))
            {
                return basePath;
            }

            foreach (string ext in exts)
            {
                string candidate = basePath + ext;
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
        }

        return null;
    }

    /// <summary>不在白名单内一律回落到 Default。写进 config.toml 的非法值会让 cc-connect 启动失败。</summary>
    public static string Normalize(string? id)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return Default;
        }

        foreach ((string supportedId, _) in Supported)
        {
            if (string.Equals(id, supportedId, StringComparison.OrdinalIgnoreCase))
            {
                return supportedId;
            }
        }

        return Default;
    }
}
