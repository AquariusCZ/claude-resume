using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AiResume.Worker.Products;

/// <summary>
/// 用户认得出的项目名:控制面续跑队列与飞书通知共用的判据与会话名读法。
/// Claude 桌面端的临时工作区(<c>scratch-workspaces\…\scratch-YYYY-MM-DD-xxxxxx</c>)目录名对人没有意义,
/// 改用会话名(桌面端侧栏标题、<c>/rename</c>),没有就写「临时会话」——这条规则各自写在消息文案
/// (<c>CompletionNotifier.ProjectDisplayName</c>)与页面视图模型(<c>AR.projectLabel</c>)里,判据与读法只在这里。
/// 只读 Claude Code 公开落盘的产物,不写任何文件。会话名来自对话内容(自动标题取自首条提问),
/// 只进消息与界面,不进日志。
/// </summary>
public static class ProjectNames
{
    /// <summary>
    /// 转录尾部找标题的窗口:先 64 KiB,找不到再扩到 1 MiB。Claude Code 会反复补写 custom-title,
    /// 最近一条通常离文件尾只有几 KB(2026-11 本机实测不超过 18 KB);一次列很多临时工作区时不必每份都读 1 MiB。
    /// </summary>
    private static readonly int[] TitleWindows = [64 * 1024, 1 << 20];

    private static readonly Regex ScratchFolder = new(@"^scratch-\d{4}-\d{2}-\d{2}-[0-9a-f]{4,}$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <summary>末级目录形如 <c>scratch-YYYY-MM-DD-xxxxxx</c>、且上级有 <c>scratch-workspaces</c>(两样都要,用户自建的同名目录不算)。</summary>
    public static bool IsScratchWorkspace(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        string normalized = path.Replace('/', '\\').TrimEnd('\\');
        return ScratchFolder.IsMatch(Path.GetFileName(normalized)) &&
               normalized.Contains(@"\scratch-workspaces\", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 会话名:先找进程登记(<c>sessions\&lt;pid&gt;.json</c> 的 <c>name</c>,会话开着时才有),
    /// 会话已关时退回转录尾部最近一条 <c>custom-title</c>。登记所在的配置根优先从转录路径反推,
    /// 反推不出用 <paramref name="claudeRoot"/>,两者都没有就只看转录。没有或读不了 → null(未清洗,见 <see cref="Clean"/>)。
    /// <paramref name="registry"/> 给出时用这份已读好的登记(<see cref="RegistryNames"/>,须是同一个配置根的),不再扫登记目录。
    /// </summary>
    public static string? SessionName(string? sessionId, string? transcriptPath, string? claudeRoot = null,
        IReadOnlyDictionary<string, string>? registry = null)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return null;
        }

        string? registered = registry is not null
            ? registry.GetValueOrDefault(sessionId)
            : (ClaudeRootOf(transcriptPath) ?? claudeRoot) is { } root ? RegistryName(sessionId, root) : null;
        return registered ?? (string.IsNullOrWhiteSpace(transcriptPath) ? null : TranscriptTitle(transcriptPath, sessionId));
    }

    /// <summary>
    /// 一个配置根下进程登记里的全部会话名(会话 ID → name),一次读完;一次列很多临时工作区时用它,免得每个都把登记目录扫一遍。
    /// 同一会话有多份登记时取先列到的那份(与逐个查找一致);读不了的登记跳过。
    /// </summary>
    public static IReadOnlyDictionary<string, string> RegistryNames(string claudeRoot)
    {
        var names = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach ((string sessionId, string name) in RegistryRows(claudeRoot))
        {
            names.TryAdd(sessionId, name);
        }

        return names;
    }

    /// <summary>
    /// 会话名清洗:换行与控制字符换成空格,连续空白并成一个;方向控制符与零宽空格直接去掉。自动标题来自对话内容,
    /// 不能在飞书里伪造出单独的一行,也不能在界面上撑出第二行。清洗后可能是空串,调用方按「没有名字」处理。
    /// </summary>
    public static string Clean(string value) =>
        Regex.Replace(
            new string(value.Where(c => !IsInvisibleControl(c)).Select(c => char.IsControl(c) ||
                char.GetUnicodeCategory(c) is UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator ? ' ' : c).ToArray()),
            @"\s{2,}", " ").Trim();

    /// <summary>
    /// 看不见却会改显示的格式字符:方向覆盖(U+202E 会把同一行后面的文字倒过来显示)、方向标记与隔离、零宽空格、BOM。
    /// 组成表情与连字的 ZWJ / ZWNJ 保留。
    /// </summary>
    private static bool IsInvisibleControl(char c) =>
        c is '​' or '‎' or '‏' or '؜' or '﻿' or (>= '‪' and <= '‮') or (>= '⁦' and <= '⁩');

    /// <summary>转录路径 <c>&lt;根&gt;\projects\&lt;目录&gt;\&lt;会话&gt;.jsonl</c> → 根;形状不对返回 null。</summary>
    public static string? ClaudeRootOf(string? transcriptPath)
    {
        try
        {
            string? projects = string.IsNullOrWhiteSpace(transcriptPath)
                ? null
                : Path.GetDirectoryName(Path.GetDirectoryName(Path.GetFullPath(transcriptPath)));
            return projects is not null && string.Equals(Path.GetFileName(projects), "projects", StringComparison.OrdinalIgnoreCase)
                ? Path.GetDirectoryName(projects)
                : null;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or System.Security.SecurityException or IOException)
        {
            return null;
        }
    }

    private static string? RegistryName(string sessionId, string claudeRoot)
    {
        foreach ((string id, string name) in RegistryRows(claudeRoot))
        {
            if (string.Equals(id, sessionId, StringComparison.Ordinal))
            {
                return name;
            }
        }

        return null;
    }

    /// <summary>进程登记里带名字的每一行(会话 ID, 去掉首尾空白的 name);目录不在或读不了就是空的。</summary>
    private static IEnumerable<(string SessionId, string Name)> RegistryRows(string claudeRoot)
    {
        foreach (string path in RegistryFiles(claudeRoot))
        {
            (string, string)? row = null;
            try
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using JsonDocument doc = JsonDocument.Parse(stream);
                JsonElement root = doc.RootElement;
                if (root.ValueKind == JsonValueKind.Object &&
                    root.TryGetProperty("sessionId", out JsonElement id) && id.ValueKind == JsonValueKind.String &&
                    root.TryGetProperty("name", out JsonElement name) && name.ValueKind == JsonValueKind.String &&
                    !string.IsNullOrWhiteSpace(name.GetString()))
                {
                    row = (id.GetString()!, name.GetString()!.Trim());
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                // 正在被改写或已删除的登记:跳过这一个。
            }

            if (row is { } found)
            {
                yield return found;
            }
        }
    }

    private static string[] RegistryFiles(string claudeRoot)
    {
        try
        {
            string registry = Path.Combine(claudeRoot, "sessions");
            return Directory.Exists(registry) ? Directory.GetFiles(registry, "*.json") : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return [];
        }
    }

    /// <summary>转录尾部最近一条 <c>{"type":"custom-title","customTitle":…,"sessionId":…}</c>;只读这一种元数据行。</summary>
    private static string? TranscriptTitle(string transcriptPath, string sessionId)
    {
        try
        {
            using var stream = new FileStream(transcriptPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            // 长度只取一次:读的时候文件可能还在追加,也可能被截短(两次取长度会算出负数的缓冲区)。
            long length = stream.Length;
            foreach (int window in TitleWindows)
            {
                long start = Math.Max(0, length - window);
                string? title = LastTitle(stream, start, length, sessionId);
                if (title is not null || start == 0)
                {
                    return title;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
        }

        return null;
    }

    /// <summary>
    /// [<paramref name="start"/>, <paramref name="end"/>) 里从后往前第一条属于这个会话的标题。
    /// 窗口不从文件头开始时第一行可能只读到半截,跳过;文件此刻变短了就只读到哪算哪。
    /// </summary>
    private static string? LastTitle(FileStream stream, long start, long end, string sessionId)
    {
        stream.Seek(start, SeekOrigin.Begin);
        var buffer = new byte[end - start];
        int total = 0;
        while (total < buffer.Length)
        {
            int read = stream.Read(buffer, total, buffer.Length - total);
            if (read == 0)
            {
                break;
            }

            total += read;
        }

        string[] lines = Encoding.UTF8.GetString(buffer, 0, total).Split('\n');
        for (int i = lines.Length - 1; i >= (start > 0 ? 1 : 0); i--)
        {
            if (!lines[i].Contains("\"custom-title\"", StringComparison.Ordinal))
            {
                continue;
            }

            try
            {
                using JsonDocument doc = JsonDocument.Parse(lines[i]);
                JsonElement row = doc.RootElement;
                if (row.ValueKind == JsonValueKind.Object &&
                    row.TryGetProperty("type", out JsonElement type) && type.ValueKind == JsonValueKind.String &&
                    type.GetString() == "custom-title" &&
                    (!row.TryGetProperty("sessionId", out JsonElement id) || id.ValueKind != JsonValueKind.String ||
                     string.Equals(id.GetString(), sessionId, StringComparison.Ordinal)) &&
                    row.TryGetProperty("customTitle", out JsonElement title) && title.ValueKind == JsonValueKind.String &&
                    !string.IsNullOrWhiteSpace(title.GetString()))
                {
                    return title.GetString()!.Trim();
                }
            }
            catch (JsonException)
            {
                // 正在追加的半行:继续往前找。
            }
        }

        return null;
    }
}
