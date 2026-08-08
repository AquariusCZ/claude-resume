using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AiResume.Core;

namespace AiResume.Worker.Products;

/// <summary>发现的项目条目。<paramref name="IsCustom"/> 为真表示来自用户手动添加(customProjects),
/// 而非会话扫描发现——这类条目没有"最近使用"时间,界面据此显示"手动添加"。</summary>
public sealed record ProjectEntry(string Name, string Path, DateTimeOffset LastWriteUtc, bool IsCustom = false);

/// <summary>
/// 项目发现(产品状态迁移 S5-A)。语义与现役 discoverProjects(feishu-runtime.js)对齐:
///
/// 1. 发现根(默认 ~/.claude/projects,测试可注入)下每个会话目录的最新 jsonl 头部
///    (前 64KiB/60 行)提取 "cwd" 字段,存在且未被排除则收录;
/// 2. 排除:hiddenProjects(全路径小写精确匹配)、生产 AppDir(%LOCALAPPDATA%\ClaudeResume)、
///    **本产品 shadow 根**(%LOCALAPPDATA%\ClaudeResumeShadow)、
///    系统 temp、Windows 系统根(^[a-z]:\windows);
/// 3. 按路径去重(保最新)、按最近使用排序;customProjects 追加(存在、未排除、未重复,
///    不参与重排);
/// 4. 3 秒缓存 + 配置指纹(hidden/custom/projectHome),指纹变化立即重算。
///
/// 只读会话元数据(不读生产 config.json、不写任何状态);测试经构造参数注入发现根与
/// temp/AppDir 边界,避免系统 temp 语义干扰断言。
/// </summary>
public sealed class ProjectCatalog
{
    private const int CacheMilliseconds = 3000;
    private const int ReadHeadBytes = 65536;
    private const int ScanLines = 60;

    private static readonly TimeSpan CacheWindow = TimeSpan.FromMilliseconds(CacheMilliseconds);

    private static readonly Regex WindowsRoot = new(@"^[a-z]:\\windows", RegexOptions.Compiled);

    /// <summary>
    /// 包管理器与应用安装目录(全小写、无尾部反斜杠)。这些位置下不会有用户项目,
    /// 但 AI 曾在其中运行过就会被发现逻辑当成项目。取不到的环境变量产出空串,由调用方跳过。
    /// </summary>
    private static readonly string[] InstallRoots = BuildInstallRoots();

    private static string[] BuildInstallRoots()
    {
        static string Norm(string? p) => string.IsNullOrWhiteSpace(p)
            ? string.Empty
            : p.TrimEnd('\\').ToLowerInvariant();

        string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

        return
        [
            // WinGet / UWP 包:每装一个包就多一个候选目录。
            Norm(string.IsNullOrEmpty(localAppData) ? null : Path.Combine(localAppData, "Microsoft", "WinGet")),
            Norm(string.IsNullOrEmpty(localAppData) ? null : Path.Combine(localAppData, "Packages")),
            Norm(Environment.GetEnvironmentVariable("ProgramFiles")),
            Norm(Environment.GetEnvironmentVariable("ProgramFiles(x86)")),
            Norm(Environment.GetEnvironmentVariable("ProgramData")),
        ];
    }

    private readonly Func<string?> _userProfilePath;
    private readonly string _tempDir;
    private readonly string _productionAppDir;
    private readonly string _shadowRoot;
    private readonly string? _indexPath;
    private readonly object _gate = new();
    private List<ProjectEntry>? _cache;
    private DateTimeOffset _cachedAt;
    private string? _cacheFingerprint;

    public ProjectCatalog(
        Func<string?>? userProfilePath = null,
        string? tempDir = null,
        string? productionAppDir = null,
        string? indexPath = null,
        string? shadowRoot = null)
    {
        _userProfilePath = userProfilePath ?? (() => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        _tempDir = string.IsNullOrWhiteSpace(tempDir) ? Path.GetTempPath() : tempDir;
        _productionAppDir = string.IsNullOrWhiteSpace(productionAppDir)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ClaudeResume")
            : productionAppDir;
        // shadow 根是本产品自己的状态目录,语义同 AppDir。额度探测把 claude 的工作目录
        // 设在这里,会在 ~/.claude/projects 下留下会话——不排除就会作为"项目"冒到队列里
        // (S7-C 实测踩到:ClaudeResumeShadow 一度排在续跑队列第一位)。
        _shadowRoot = string.IsNullOrWhiteSpace(shadowRoot) ? ShadowPaths.Root : shadowRoot;
        _indexPath = indexPath;
    }

    public List<ProjectEntry> Discover(ProductConfig config, string? discoveryRoot = null)
    {
        ArgumentNullException.ThrowIfNull(config);

        string fingerprint = Fingerprint(config);
        lock (_gate)
        {
            if (_cache is not null && DateTimeOffset.UtcNow - _cachedAt < CacheWindow && _cacheFingerprint == fingerprint)
            {
                return _cache;
            }
        }

        var discovered = new List<ProjectEntry>();
        string root = discoveryRoot ?? Path.Combine(_userProfilePath() ?? string.Empty, ".claude", "projects");
        ProjectIndex? index = null;
        try
        {
            if (_indexPath is not null)
            {
                index = ProjectIndex.Load(_indexPath);
            }
        }
        catch (Exception)
        {
            // 索引读取失败:静默降级为全量扫描,索引为 null 表示不启用。
            index = null;
        }

        try
        {
            if (Directory.Exists(root))
            {
                foreach (string sessionDir in Directory.EnumerateDirectories(root))
                {
                    DateTimeOffset dirWriteUtc;
                    try
                    {
                        dirWriteUtc = new DirectoryInfo(sessionDir).LastWriteTimeUtc;
                    }
                    catch (Exception)
                    {
                        // 目录信息不可读:跳过该目录。
                        continue;
                    }

                    string? jsonl;
                    string? cwd;
                    DateTimeOffset jsonlWriteUtc;

                    if (index is not null && index.TryGet(sessionDir, dirWriteUtc, out ProjectIndexEntry entry))
                    {
                        // 命中索引且目录未变:直接复用,零文件 I/O。
                        jsonl = entry.JsonlPath;
                        jsonlWriteUtc = entry.JsonlWriteUtc;
                        cwd = entry.Cwd;
                    }
                    else
                    {
                        // 未命中或目录已变:走原有全量路径。
                        jsonl = LatestJsonl(sessionDir);
                        if (jsonl is null)
                        {
                            // 无 jsonl:缓存空结果,避免每次重试。
                            if (index is not null)
                            {
                                index.Put(new ProjectIndexEntry(sessionDir, dirWriteUtc, null, DateTimeOffset.MinValue, null));
                            }
                            continue;
                        }

                        try
                        {
                            jsonlWriteUtc = File.GetLastWriteTimeUtc(jsonl);
                        }
                        catch (Exception)
                        {
                            jsonlWriteUtc = DateTimeOffset.MinValue;
                        }

                        cwd = ExtractCwd(jsonl);
                        if (index is not null)
                        {
                            // cwd 为 null 也要缓存,避免每次重试。
                            index.Put(new ProjectIndexEntry(sessionDir, dirWriteUtc, jsonl, jsonlWriteUtc, cwd));
                        }
                    }

                    if (string.IsNullOrWhiteSpace(cwd) || !Directory.Exists(cwd) || IsExcluded(cwd, config))
                    {
                        continue;
                    }

                    discovered.Add(new ProjectEntry(Path.GetFileName(cwd.TrimEnd('\\', '/')), cwd, jsonlWriteUtc));
                }
            }
        }
        catch (Exception)
        {
            // 发现根不可读/枚举失败:容错,继续 custom(现役 catch 后同样继续)。
        }

        // 索引清理与持久化(仅在启用索引时)。
        if (index is not null)
        {
            try
            {
                index.RemoveUnseen();
                index.SaveIfChanged(_indexPath!);
            }
            catch (Exception)
            {
                // 索引写失败不阻断发现结果。
            }
        }

        // 按路径去重(保最新),按最近使用排序。
        var byPath = new Dictionary<string, ProjectEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (ProjectEntry entry in discovered)
        {
            if (!byPath.TryGetValue(entry.Path, out ProjectEntry? existing) || entry.LastWriteUtc > existing.LastWriteUtc)
            {
                byPath[entry.Path] = entry;
            }
        }

        var list = byPath.Values
            .OrderByDescending(e => e.LastWriteUtc)
            .Select(e => e with { Name = Path.GetFileName(e.Path.TrimEnd('\\', '/')) })
            .ToList();

        // customProjects 追加(存在、未排除、未重复;不重排)。
        var seen = new HashSet<string>(list.Select(e => e.Path), StringComparer.OrdinalIgnoreCase);
        foreach (ProjectRef custom in config.CustomProjects)
        {
            if (string.IsNullOrWhiteSpace(custom.Path))
            {
                continue;
            }

            string customPath;
            try
            {
                customPath = Path.GetFullPath(custom.Path);
            }
            catch (Exception)
            {
                continue;
            }

            if (!Directory.Exists(customPath) || IsExcluded(customPath, config) || !seen.Add(customPath))
            {
                continue;
            }

            list.Add(new ProjectEntry(
                string.IsNullOrWhiteSpace(custom.Name) ? Path.GetFileName(customPath.TrimEnd('\\', '/')) : custom.Name,
                customPath,
                DateTimeOffset.MinValue,
                IsCustom: true));
        }

        lock (_gate)
        {
            _cache = list;
            _cachedAt = DateTimeOffset.UtcNow;
            _cacheFingerprint = fingerprint;
        }

        return list;
    }

    /// <summary>清除缓存(测试辅助)。</summary>
    public void ClearCache()
    {
        lock (_gate)
        {
            _cache = null;
            _cacheFingerprint = null;
        }
    }

    private bool IsExcluded(string cwd, ProductConfig config)
    {
        string lower;
        try
        {
            lower = Path.GetFullPath(cwd).TrimEnd('\\').ToLowerInvariant();
        }
        catch (Exception)
        {
            return true;
        }

        if (config.HiddenProjects.Any(h =>
                !string.IsNullOrWhiteSpace(h) &&
                SafeFullPath(h, out string hidden) &&
                hidden.TrimEnd('\\').ToLowerInvariant() == lower))
        {
            return true;
        }

        return IsReserved(cwd);
    }

    /// <summary>
    /// 该路径是否属于**内建保留区**(生产 AppDir、本产品 shadow 根、系统 temp、Windows 目录)。
    /// 与用户的 hiddenProjects 无关。
    ///
    /// 手动添加项目时先用它挡一道:保留区里的目录即使写进 customProjects,
    /// 发现阶段也会被过滤掉,只会表现为"添加了但没出现",不如当场告诉用户原因。
    /// </summary>
    public bool IsReserved(string path)
    {
        string lower;
        try
        {
            lower = Path.GetFullPath(path).TrimEnd('\\').ToLowerInvariant();
        }
        catch (Exception)
        {
            return true;
        }

        string appDir = _productionAppDir.TrimEnd('\\').ToLowerInvariant();
        if (lower == appDir || lower.StartsWith(appDir + "\\", StringComparison.Ordinal))
        {
            return true;
        }

        string shadow = _shadowRoot.TrimEnd('\\').ToLowerInvariant();
        if (lower == shadow || lower.StartsWith(shadow + "\\", StringComparison.Ordinal))
        {
            return true;
        }

        string temp = _tempDir.TrimEnd('\\').ToLowerInvariant();
        if (lower == temp || lower.StartsWith(temp + "\\", StringComparison.Ordinal))
        {
            return true;
        }

        // 包管理器与应用安装目录不是项目。
        //
        // 发现是按 AI 会话的 cwd 历史推的,所以只要在某个安装目录下跑过一次 AI,
        // 那个目录就会混进项目清单。实测:
        //   %LOCALAPPDATA%\Microsoft\WinGet\Packages\CodeZeno.ClaudeCodeUsageMonitor_…8wekyb3d8bbwe
        // 出现在续跑队列第 3 位。用户可以手动隐藏,但下一个装的包又会冒出来,
        // 治标不治本 —— 这类路径整体排除。
        foreach (string installRoot in InstallRoots)
        {
            if (installRoot.Length > 0 &&
                (lower == installRoot || lower.StartsWith(installRoot + "\\", StringComparison.Ordinal)))
            {
                return true;
            }
        }

        return WindowsRoot.IsMatch(lower);
    }

    private static bool SafeFullPath(string path, out string full)
    {
        try
        {
            full = Path.GetFullPath(path);
            return true;
        }
        catch (Exception)
        {
            full = string.Empty;
            return false;
        }
    }

    private static string? LatestJsonl(string sessionDir)
    {
        try
        {
            return Directory.EnumerateFiles(sessionDir, "*.jsonl")
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .FirstOrDefault();
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static string? ExtractCwd(string jsonlPath)
    {
        try
        {
            using var fs = new FileStream(jsonlPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            byte[] buffer = new byte[ReadHeadBytes];
            int read = fs.Read(buffer, 0, buffer.Length);
            string text = Encoding.UTF8.GetString(buffer, 0, read);
            string[] lines = text.Split('\n');
            int scanned = Math.Min(lines.Length, ScanLines);
            for (int i = 0; i < scanned; i++)
            {
                string line = lines[i];
                if (!line.Contains("\"cwd\"", StringComparison.Ordinal))
                {
                    continue;
                }

                try
                {
                    using JsonDocument doc = JsonDocument.Parse(line);
                    if (doc.RootElement.ValueKind == JsonValueKind.Object &&
                        doc.RootElement.TryGetProperty("cwd", out JsonElement cwd) &&
                        cwd.ValueKind == JsonValueKind.String)
                    {
                        return cwd.GetString();
                    }
                }
                catch (JsonException)
                {
                    // 单行损坏跳过,继续下一行。
                }
            }
        }
        catch (Exception)
        {
            // 文件不可读:返回 null(该会话目录跳过)。
        }

        return null;
    }

    private static string Fingerprint(ProductConfig config)
    {
        string hidden = string.Join("\u0002", config.HiddenProjects.Where(h => !string.IsNullOrWhiteSpace(h)).OrderBy(h => h, StringComparer.Ordinal));
        string custom = string.Join("\u0002", config.CustomProjects
            .Where(c => !string.IsNullOrWhiteSpace(c.Path))
            .Select(c => c.Name + "=" + c.Path)
            .OrderBy(s => s, StringComparer.Ordinal));
        string payload = hidden + "\u0001" + custom + "\u0001" + config.ProjectHome;
        byte[] hash = System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(payload));
        return Convert.ToHexString(hash)[..16];
    }
}