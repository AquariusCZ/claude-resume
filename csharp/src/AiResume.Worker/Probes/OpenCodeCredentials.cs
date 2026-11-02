using System.Text.Json;

namespace AiResume.Worker.Probes;

/// <summary>某个 OpenCode 供应商当前实际使用的密钥。<see cref="Key"/> 只在调用栈内流动，绝不进日志或界面。</summary>
public sealed record OpenCodeProviderKey(string? Key, string Source, string? Problem)
{
    public bool Ok => Problem is null && !string.IsNullOrWhiteSpace(Key);
}

/// <summary>
/// 只读解析 OpenCode 给 DeepSeek 用的密钥。
///
/// 为什么读 OpenCode 而不是 AI Resume 自己存一份：用户真正在用 DeepSeek 的地方是 OpenCode,
/// 面板要回答的是「OpenCode 那个账户还剩多少钱」。各存一份就会出现 2026-10-30 那种情况——
/// OpenCode 已换成公司密钥，面板还挂着个人密钥的余额。和 Claude、Codex 一样，
/// 凭据由上游客户端持有，这里只读、不写、不缓存到磁盘。
///
/// 解析顺序与 OpenCode 2.x 自己一致：合并后的配置里显式的 <c>provider.deepseek.options.apiKey</c>
/// (支持 <c>{env:VAR}</c> 与 <c>{file:路径}</c>)优先，其次是凭据库 <c>opencode.db</c> 的 credential 表;
/// 只有凭据库或这张表根本不存在(1.x 或尚未迁移)才看 <c>auth.json</c>;都没有时才回退旧的 <c>DEEPSEEK_API_KEY</c>
/// 环境变量，并在来源里如实标出。配置把 DeepSeek 指到非官方端点时不发余额请求——那把密钥未必属于 api.deepseek.com。
/// 端点按 OpenCode 的配置层合并后判断(全局 opencode.json → opencode.jsonc → OPENCODE_CONFIG → OPENCODE_CONFIG_CONTENT
/// → %ProgramData%\opencode 托管层),不只看写着密钥的那一层。所有文件都以共享读写方式打开，不妨碍 OpenCode 刷新自己的登录。
/// </summary>
public static class OpenCodeCredentials
{
    public const string ProviderId = "deepseek";
    public const string SourceConfig = "opencode-config";
    public const string SourceAuth = "opencode-auth";
    public const string SourceDatabase = "opencode-db";
    public const string SourceEnvironment = "env:" + DeepSeekProbe.ApiKeyEnvName;
    public const string SourceNone = "none";

    private static readonly JsonDocumentOptions JsoncOptions = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>OpenCode 的全局配置目录:<c>OPENCODE_CONFIG_DIR</c> 整体替换，否则 <c>XDG_CONFIG_HOME</c>(或 ~/.config)下的 opencode。</summary>
    public static string ConfigDirectory(Func<string, string?> env, string userProfile) =>
        env("OPENCODE_CONFIG_DIR") is { Length: > 0 } custom && !string.IsNullOrWhiteSpace(custom)
            ? custom.Trim()
            : Path.Combine(env("XDG_CONFIG_HOME") is { Length: > 0 } xdg ? xdg : Path.Combine(userProfile, ".config"), "opencode");

    public static string DataDirectory(Func<string, string?> env, string userProfile) =>
        Path.Combine(env("XDG_DATA_HOME") is { Length: > 0 } xdg ? xdg : Path.Combine(userProfile, ".local", "share"), "opencode");

    /// <summary>OpenCode 2.x 凭据库的位置:<c>OPENCODE_DB</c>(相对路径按数据目录解析),否则数据目录下的 opencode.db;内存库为 null。</summary>
    public static string? DatabasePath(Func<string, string?> env, string userProfile)
    {
        string dataDir = DataDirectory(env, userProfile);
        if (env("OPENCODE_DB") is not { Length: > 0 } custom || string.IsNullOrWhiteSpace(custom))
        {
            return Path.Combine(dataDir, "opencode.db");
        }

        return custom.Trim() == ":memory:" ? null : Path.GetFullPath(Path.Combine(dataDir, custom.Trim()));
    }

    public static OpenCodeProviderKey ReadDeepSeek(Func<string, string?> env, string userProfile)
    {
        // OpenCode 2.x 深度合并配置层:全局 opencode.json < opencode.jsonc < OPENCODE_CONFIG 文件 < OPENCODE_CONFIG_CONTENT
        // < %ProgramData%\opencode 托管层(最后合并、优先级最高)。1.x 的 config.json 2.x 不再读，这里也不读。
        // 密钥和 baseURL 可能写在不同的层:只看有密钥的那一层，会漏掉别的层把 DeepSeek 指到公司中转,
        // 把只属于中转的密钥发给 api.deepseek.com。所以先合并全部层，再判断端点。
        // 项目级 opencode.json 与组织远程配置读不到(不知道是哪个项目),文档里写明这一局限。
        string configDir = ConfigDirectory(env, userProfile);
        var layers = new List<(string Location, Func<string?> Read)>();
        void AddFile(string path) => layers.Add((path, () => File.Exists(path) ? SharedFile.ReadAllText(path) : null));
        AddFile(Path.Combine(configDir, "opencode.json"));
        AddFile(Path.Combine(configDir, "opencode.jsonc"));
        if (env("OPENCODE_CONFIG") is { Length: > 0 } custom && !string.IsNullOrWhiteSpace(custom))
        {
            AddFile(custom.Trim());
        }

        if (env("OPENCODE_CONFIG_CONTENT") is { Length: > 0 } content && !string.IsNullOrWhiteSpace(content))
        {
            // 内联配置里的相对 {file:} 引用按全局配置目录解析。
            layers.Add((Path.Combine(configDir, "OPENCODE_CONFIG_CONTENT"), () => content));
        }

        if (env("ProgramData") is { Length: > 0 } programData && !string.IsNullOrWhiteSpace(programData))
        {
            AddFile(Path.Combine(programData, "opencode", "opencode.json"));
            AddFile(Path.Combine(programData, "opencode", "opencode.jsonc"));
        }

        string? baseUrl = null;
        string? rawKey = null;
        string? keyLayer = null;
        foreach ((string location, Func<string?> read) in layers)
        {
            JsonElement? options;
            try
            {
                if (read() is not { } text) continue;
                options = DeepSeekOptions(text);
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                return new OpenCodeProviderKey(null, SourceConfig, "OpenCode 配置无法解析");
            }

            if (options is not { } found) continue;
            if (Text(found, "baseURL") is { Length: > 0 } layerBase) baseUrl = layerBase;
            if (Text(found, "apiKey") is { Length: > 0 } layerKey)
            {
                rawKey = layerKey;
                keyLayer = location;
            }
        }

        if (baseUrl is not null && !ProviderRegistry.IsDeepSeek(baseUrl))
        {
            return new OpenCodeProviderKey(null, SourceConfig, "OpenCode 的 DeepSeek 指向自定义端点，余额未核实");
        }

        if (rawKey is not null)
        {
            return ResolveConfiguredKey(rawKey, keyLayer!, env, userProfile);
        }

        // OpenCode 2.x 把登录凭据存在自己的 SQLite(opencode.db 的 credential 表);auth.json 只在 2.x 首次启动时
        // 导入一次，之后在 OpenCode 里换密钥、删密钥都只改数据库。只看 auth.json,换过或删过密钥后面板就会挂着旧账户的余额
        // ——正是 2026-10-30 那次「OpenCode 已换成公司密钥，面板还是个人密钥」。所以库和表在的时候以库为准:
        // 库里没有 DeepSeek 就是 OpenCode 没存，库读不了就是未核实;只有库或表根本不存在(1.x、尚未迁移)才看 auth.json。
        string? databasePath = DatabasePath(env, userProfile);
        OpenCodeProviderKey? stored = databasePath is null ? null : ReadDatabaseKey(databasePath);
        if (stored is { Problem: not null } or { Ok: true })
        {
            return stored;
        }

        string authPath = Path.Combine(DataDirectory(env, userProfile), "auth.json");
        if (stored is null && File.Exists(authPath))
        {
            try
            {
                using JsonDocument doc = JsonDocument.Parse(SharedFile.ReadAllText(authPath));
                if (doc.RootElement.ValueKind == JsonValueKind.Object &&
                    doc.RootElement.TryGetProperty(ProviderId, out JsonElement entry) && entry.ValueKind == JsonValueKind.Object &&
                    Text(entry, "type") == "api" && Text(entry, "key") is { Length: > 0 } key)
                {
                    return new OpenCodeProviderKey(key.Trim(), SourceAuth, null);
                }
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
            {
                return new OpenCodeProviderKey(null, SourceAuth, "OpenCode 登录文件无法读取");
            }
        }

        return env(DeepSeekProbe.ApiKeyEnvName) is { Length: > 0 } legacy && !string.IsNullOrWhiteSpace(legacy)
            ? new OpenCodeProviderKey(legacy.Trim(), SourceEnvironment, null)
            : new OpenCodeProviderKey(null, SourceNone, "OpenCode 未配置 DeepSeek");
    }

    /// <summary>
    /// OpenCode 2.x 凭据库里 DeepSeek 的密钥(只读打开、不建连接池、2 秒忙等;先取标为启用的，再取最近更新的)。
    /// 2.2.5 的记录形状:<c>credential(integration_id, value, active, time_updated)</c>,value 为 <c>{"type":"key","key":…}</c>。
    /// 返回 null = 库或 credential 表不存在(交给 auth.json);<see cref="SourceNone"/> 且无问题 = 库里没有 DeepSeek
    /// (OpenCode 没存，不得再拿 auth.json 里的旧密钥);带 Problem = 库在但读不了、记录认不出(未核实)。
    /// </summary>
    internal static OpenCodeProviderKey? ReadDatabaseKey(string databasePath)
    {
        if (!File.Exists(databasePath))
        {
            return null;
        }

        try
        {
            var builder = new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder
            {
                DataSource = databasePath,
                Mode = Microsoft.Data.Sqlite.SqliteOpenMode.ReadOnly,
                Pooling = false,
                DefaultTimeout = 2,
            };
            using var connection = new Microsoft.Data.Sqlite.SqliteConnection(builder.ToString());
            connection.Open();
            using (Microsoft.Data.Sqlite.SqliteCommand table = connection.CreateCommand())
            {
                table.CommandText = "select count(*) from sqlite_master where type = 'table' and name = 'credential'";
                if (Convert.ToInt64(table.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture) == 0)
                {
                    return null;
                }
            }

            using Microsoft.Data.Sqlite.SqliteCommand command = connection.CreateCommand();
            command.CommandText =
                "select value from credential where integration_id = $id order by coalesce(active, 0) desc, time_updated desc limit 1";
            command.Parameters.AddWithValue("$id", ProviderId);
            object? found = command.ExecuteScalar();
            if (found is null or DBNull)
            {
                return new OpenCodeProviderKey(null, SourceNone, null);
            }

            using JsonDocument doc = JsonDocument.Parse(found as string ?? string.Empty);
            return doc.RootElement.ValueKind == JsonValueKind.Object && Text(doc.RootElement, "key") is { Length: > 0 } key &&
                   !string.IsNullOrWhiteSpace(key)
                ? new OpenCodeProviderKey(key.Trim(), SourceDatabase, null)
                : new OpenCodeProviderKey(null, SourceDatabase, "OpenCode 凭据库里的 DeepSeek 凭据无法识别");
        }
        catch (Exception ex) when (ex is Microsoft.Data.Sqlite.SqliteException or JsonException or IOException or
                                       UnauthorizedAccessException or InvalidOperationException or FormatException or InvalidCastException)
        {
            return new OpenCodeProviderKey(null, SourceDatabase, "OpenCode 凭据库无法读取");
        }
    }

    /// <summary>这一层配置里的 <c>provider.deepseek.options</c>;没有这一段时为 null。解析失败抛出，由调用方降级。</summary>
    private static JsonElement? DeepSeekOptions(string text)
    {
        using JsonDocument doc = JsonDocument.Parse(text, JsoncOptions);
        return doc.RootElement.ValueKind == JsonValueKind.Object &&
               doc.RootElement.TryGetProperty("provider", out JsonElement providers) && providers.ValueKind == JsonValueKind.Object &&
               providers.TryGetProperty(ProviderId, out JsonElement provider) && provider.ValueKind == JsonValueKind.Object &&
               provider.TryGetProperty("options", out JsonElement found) && found.ValueKind == JsonValueKind.Object
            ? found.Clone()
            : null;
    }

    /// <summary>配置里的 apiKey:支持 <c>{env:VAR}</c> 与 <c>{file:路径}</c>(相对路径按它所在的配置文件解析)。</summary>
    private static OpenCodeProviderKey ResolveConfiguredKey(string raw, string layerPath, Func<string, string?> env, string userProfile)
    {
        if (raw.StartsWith("{env:", StringComparison.Ordinal) && raw.EndsWith('}'))
        {
            string name = raw[5..^1].Trim();
            return env(name) is { Length: > 0 } value && !string.IsNullOrWhiteSpace(value)
                ? new OpenCodeProviderKey(value.Trim(), SourceConfig, null)
                : new OpenCodeProviderKey(null, SourceConfig, $"OpenCode 引用的环境变量 {name} 未设置");
        }

        if (raw.StartsWith("{file:", StringComparison.Ordinal) && raw.EndsWith('}'))
        {
            try
            {
                string file = raw[6..^1].Trim();
                file = file.StartsWith('~') ? Path.Combine(userProfile, file[1..].TrimStart('/', '\\')) : file;
                file = Path.IsPathRooted(file) ? file : Path.Combine(Path.GetDirectoryName(layerPath)!, file);
                string value = SharedFile.ReadAllText(file).Trim();
                return value.Length > 0
                    ? new OpenCodeProviderKey(value, SourceConfig, null)
                    : new OpenCodeProviderKey(null, SourceConfig, "OpenCode 引用的密钥文件为空");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                return new OpenCodeProviderKey(null, SourceConfig, "OpenCode 引用的密钥文件无法读取");
            }
        }

        return new OpenCodeProviderKey(raw.Trim(), SourceConfig, null);
    }

    private static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
