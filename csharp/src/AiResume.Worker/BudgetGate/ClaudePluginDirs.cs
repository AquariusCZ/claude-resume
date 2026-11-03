using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AiResume.Worker.BudgetGate;

/// <summary>
/// <c>~/.claude/settings.json</c> 里 <c>env.CLAUDE_CODE_PLUGIN_DIRS</c> 的我方条目(ADR-0008 §F)。
///
/// 这个键是多值的(Windows 用 <c>;</c> 分隔),别人也可能往里放自己的 mod 目录,所以只增删我方条目:
/// 末级目录名等于 <see cref="BudgetGatePaths.ModName"/> 的都算我方——开发期指向开发目录的条目在首次 install 时被替换成安装目录。
/// 读改写与 <c>ClaudeCodeNotificationAdapter</c> 同一套:整树解析、只动这一个键、临时文件 + 原子替换、改前留 <c>.bak</c>;
/// 写前重读,和读到时不一致就放弃这次(别的进程刚写过),由下一次对账再来。
/// Claude Code 只在会话启动时读这个键,改动只影响新启动的会话。
/// </summary>
public sealed class ClaudePluginDirs
{
    public const string EnvKey = "CLAUDE_CODE_PLUGIN_DIRS";
    private const char Separator = ';';

    private readonly string _settingsPath;

    public ClaudePluginDirs(string? settingsPath = null)
    {
        _settingsPath = settingsPath ?? DefaultSettingsPath();
    }

    public string SettingsPath => _settingsPath;

    /// <summary>与 Claude Code 一致:<c>%CLAUDE_CONFIG_DIR%</c>(设置了时)否则 <c>%USERPROFILE%\.claude</c> 下的 settings.json。</summary>
    public static string DefaultSettingsPath()
    {
        string? configDir = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR");
        string root = !string.IsNullOrWhiteSpace(configDir)
            ? configDir
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude");
        return Path.Combine(root, "settings.json");
    }

    /// <summary>这条路径算不算我方条目:末级目录名是 mod 名(大小写不敏感,去掉尾部分隔符)。</summary>
    public static bool IsOurs(string entry)
    {
        string trimmed = entry.Trim().TrimEnd('\\', '/');
        return string.Equals(Path.GetFileName(trimmed), BudgetGatePaths.ModName, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>现状:我方条目有几条、是否恰好一条且指向 <paramref name="modDir"/>、读不读得出。</summary>
    public PluginDirsStatus Inspect(string modDir)
    {
        try
        {
            if (!File.Exists(_settingsPath))
                return new PluginDirsStatus(false, Array.Empty<string>(), Array.Empty<string>(), "settings.json 不存在");
            if (JsonNode.Parse(ReadText()) is not JsonObject root)
                return new PluginDirsStatus(false, Array.Empty<string>(), Array.Empty<string>(), "settings.json 根节点不是 JSON 对象", Unverified: true);
            string[] entries = Split(root);
            string[] ours = entries.Where(IsOurs).ToArray();
            bool exact = ours.Length == 1 && SamePath(ours[0], modDir);
            return new PluginDirsStatus(exact, ours, entries.Where(e => !IsOurs(e)).ToArray(),
                exact ? "已登记" : ours.Length == 0 ? "未登记" : "登记的路径不是安装目录");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return new PluginDirsStatus(false, Array.Empty<string>(), Array.Empty<string>(), $"settings.json 读取失败:{ex.Message}", Unverified: true);
        }
    }

    /// <summary>
    /// 让我方条目恰好是 <paramref name="modDir"/>(<paramref name="enable"/> 为 false 时一条不留),别人的条目原样保留、顺序不变。
    /// 返回是否改了文件;写前文件被别人改过时抛 <see cref="IOException"/>,调用方按对账失败处理、下次再来。
    /// </summary>
    public bool Apply(string modDir, bool enable)
    {
        string? dir = Path.GetDirectoryName(_settingsPath);
        if (string.IsNullOrEmpty(dir))
            throw new InvalidOperationException("无法确定 settings.json 所在目录");

        string original = File.Exists(_settingsPath) ? ReadText() : string.Empty;
        JsonObject root = original.Length == 0
            ? new JsonObject()
            : JsonNode.Parse(original) as JsonObject ?? throw new InvalidOperationException("settings.json 根节点不是 JSON 对象");

        string[] entries = Split(root);
        List<string> next = entries.Where(e => !IsOurs(e)).ToList();
        if (enable)
            next.Add(Path.GetFullPath(modDir));
        if (next.SequenceEqual(entries, StringComparer.Ordinal))
            return false;

        JsonObject env = root["env"] as JsonObject ?? new JsonObject();
        if (next.Count > 0)
            env[EnvKey] = string.Join(Separator, next);
        else
            env.Remove(EnvKey);
        if (env.Count > 0)
            root["env"] = env;
        else
            root.Remove("env");

        Directory.CreateDirectory(dir);
        // 临时文件名唯一:通知适配器用固定的 settings.json.tmp,两边并发时不能互相 Move 走对方的内容。
        string tempPath = $"{_settingsPath}.budget-gate-{Guid.NewGuid():N}.tmp";
        try
        {
            string json = root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
            using (var fs = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
            using (var writer = new StreamWriter(fs, new UTF8Encoding(false)))
            {
                writer.Write(json);
                writer.Flush();
                fs.Flush(flushToDisk: true);
            }

            // 落盘之后、替换之前再读一次:读改写期间被别的进程(Claude Code、通知开关、install)改过就放弃这次。
            string current = File.Exists(_settingsPath) ? ReadText() : string.Empty;
            if (!string.Equals(current, original, StringComparison.Ordinal))
                throw new IOException("settings.json 在改写期间被别的进程改过,这次没有写入");
            if (File.Exists(_settingsPath))
                File.Copy(_settingsPath, _settingsPath + ".bak", overwrite: true);
            File.Move(tempPath, _settingsPath, overwrite: true);
        }
        catch
        {
            try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { /* 忽略清理失败 */ }
            throw;
        }

        return true;
    }

    /// <summary>settings.json 的原文里有没有这段文字(读不懂 JSON 时判断"有没有我方条目"用);文件不在时为 false。</summary>
    public bool RawContains(string text)
    {
        try
        {
            return File.Exists(_settingsPath) && ReadText().Contains(text, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return true;
        }
    }

    private static string[] Split(JsonObject root)
    {
        // 别人手写成非字符串时不认它:当作没有条目,写回时由我方条目覆盖成字符串(值本来就无法被 Claude Code 使用)。
        string raw = (root["env"] as JsonObject)?[EnvKey] is JsonValue value && value.TryGetValue(out string? text) ? text : string.Empty;
        return raw.Split(Separator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    private static bool SamePath(string a, string b)
    {
        static string Norm(string p) => Path.GetFullPath(p.Trim()).TrimEnd('\\', '/');
        return string.Equals(Norm(a), Norm(b), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>与 Claude Code 自己的写法共存:共享读写删,去 BOM。</summary>
    private string ReadText()
    {
        using var fs = new FileStream(_settingsPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(fs, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }
}

/// <param name="IsExact">我方条目恰好一条且指向安装目录。</param>
/// <param name="Ours">我方条目(可能指向旧的开发目录)。</param>
/// <param name="Others">别人的条目,只读展示。</param>
/// <param name="Unverified">settings.json 读不出,开没开不知道。</param>
public sealed record PluginDirsStatus(bool IsExact, IReadOnlyList<string> Ours, IReadOnlyList<string> Others, string Detail, bool Unverified = false);
