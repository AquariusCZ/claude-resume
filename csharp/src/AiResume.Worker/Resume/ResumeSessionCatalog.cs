using System.Text.Json;
using AiResume.Core;
using AiResume.Worker.Probes;
using AiResume.Worker.Products;

namespace AiResume.Worker.Resume;

/// <summary>会话占用结论:idle / in-use(绑定会话在客户端打开)/ cwd-busy(同目录另一会话正在运行)/ unverified。</summary>
public sealed record SessionOccupancy(string State, string? Client = null);

/// <summary>续跑门禁读取的原生会话事实(会话尾部证据与客户端占用)。生产实现是 <see cref="ResumeSessionCatalog"/>。</summary>
public interface IProjectSessionInspector
{
    /// <summary>Claude 会话尾部证据;找不到或读不了时为 null。</summary>
    ClaudeSessionEvidence? ReadEvidence(ProjectRef project);

    SessionOccupancy Occupancy(ProjectRef project);

    /// <summary>
    /// 中断(<paramref name="since"/>)之后，同一项目目录里有没有**新开的**会话得到过真实应答——用户换了个会话把活接着干了。
    /// true = 有，不再自动续旧会话：旧上下文去改已经变化的工作树，可能重做或回退刚完成的工作;
    /// false = 核实过没有;null = 判断不了(目录读不了，或疑似新会话的文件读不出归属/开始时间),调用方按未核实等待。
    /// 默认实现回答「没有」。
    /// </summary>
    bool? ContinuedInAnotherSession(ProjectRef project, DateTimeOffset since) => false;
}

/// <summary>只读取原生会话头部/尾部元数据；保存精确 UUID，不恢复、不修改原生会话。</summary>
public sealed class ResumeSessionCatalog : IProjectSessionInspector
{
    private readonly string _claudeRoot;
    private readonly string _codexRoot;
    private readonly Func<int, bool?> _isAlive;
    private readonly Func<int, long?> _startTime;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> _transcripts = new(StringComparer.OrdinalIgnoreCase);
    public ResumeSessionCatalog(string? claudeRoot = null, string? codexRoot = null, Func<int, bool?>? isAlive = null,
        Func<int, long?>? startTime = null)
    {
        _claudeRoot = claudeRoot ?? Path.Combine(Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR") ??
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude"), "projects");
        _codexRoot = codexRoot ?? Path.Combine(CodexAuthProbe.ResolveCodexHome(), "sessions");
        _isAlive = isAlive ?? ProcessAlive;
        // 只注入了存活判定的测试替身不再去查真实进程的启动时间(同一 PID 在本机可能恰好存在)。
        _startTime = startTime ?? (isAlive is null ? ProcessStartTime : _ => null);
    }

    public IReadOnlyList<ProjectRef> Bind(string agent, IReadOnlyList<string> paths)
    {
        IReadOnlyDictionary<string, ProjectRef> found = BindAvailable(agent, paths);
        return paths.Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase).Select(path =>
            found.TryGetValue(path, out ProjectRef? project)
                ? project
                : throw new InvalidOperationException($"项目 {Path.GetFileName(path.TrimEnd('\\', '/'))} 没有可核实的 {agent} 会话，无法布防续跑"))
            .ToArray();
    }

    /// <summary>每个项目最近一次修改的原生会话;没有会话的项目不在结果里(预演用，不抛异常)。</summary>
    public IReadOnlyDictionary<string, ProjectRef> BindAvailable(string agent, IReadOnlyList<string> paths)
    {
        string root = agent == "codex" ? _codexRoot : _claudeRoot;
        var selected = paths.Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var latest = new Dictionary<string, (string Id, DateTime Time)>(StringComparer.OrdinalIgnoreCase);
        if (Directory.Exists(root))
            foreach (string file in Directory.EnumerateFiles(root, "*.jsonl", SearchOption.AllDirectories))
            {
                (string? id, string? cwd) = ReadMetadata(file, agent);
                if (!Guid.TryParseExact(id, "D", out _) || cwd is null) continue;
                string full;
                try { full = Path.GetFullPath(cwd); } catch (Exception) { continue; }
                if (!selected.Contains(full, StringComparer.OrdinalIgnoreCase)) continue;
                DateTime modified = File.GetLastWriteTimeUtc(file);
                if (!latest.TryGetValue(full, out var previous) || modified > previous.Time)
                    latest[full] = (id!, modified);
            }
        return selected.Where(latest.ContainsKey).ToDictionary(path => path,
            path => new ProjectRef { Path = path, Name = Path.GetFileName(path.TrimEnd('\\', '/')), SessionId = latest[path].Id },
            StringComparer.OrdinalIgnoreCase);
    }

    public bool Matches(string agent, ProjectRef project)
    {
        if (!Guid.TryParseExact(project.SessionId, "D", out _)) return false;
        string root = agent == "codex" ? _codexRoot : _claudeRoot;
        if (!Directory.Exists(root)) return false;
        foreach (string file in Directory.EnumerateFiles(root, "*" + project.SessionId + "*.jsonl", SearchOption.AllDirectories))
        {
            (string? id, string? cwd) = ReadMetadata(file, agent);
            if (id == project.SessionId && cwd is not null &&
                Path.GetFullPath(cwd).Equals(Path.GetFullPath(project.Path), StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    // Claude Code 2.1.251 的 sessions/<pid>.json 是原生进程登记，
    // 不把文件存在当成正在运行，也不读取 peer token 等不需要的字段。
    public bool? IsClaudeSessionIdle(string sessionId) =>
        Occupancy(new ProjectRef { SessionId = sessionId }).State switch
        {
            "idle" or "cwd-busy" => true,
            "in-use" => false,
            _ => null,
        };

    /// <summary>
    /// 原生会话登记(<c>sessions/&lt;pid&gt;.json</c>,2.1.309 字段:pid / sessionId / cwd / procStart / status / entrypoint)。
    /// 进程活着且不能证明是 PID 复用才算占用(<see cref="SameProcess"/>)——崩溃留下的登记遇上 PID 复用，不能被当成一直占用;
    /// 反过来，登记格式一变就判「进程已死」会把所有开着的会话当成空闲，所以只有启动时间晚于登记才算复用。
    /// 同目录另一个正在运行(status=busy)的会话也算占用：两个 agent 不能同时改同一个工作树。
    /// </summary>
    public SessionOccupancy Occupancy(ProjectRef project)
    {
        string registry = Path.Combine(Path.GetDirectoryName(_claudeRoot)!, "sessions");
        string? projectPath = NormalizeDirectory(project.Path);
        try
        {
            if (!Directory.Exists(registry)) return new("idle");
            SessionOccupancy result = new("idle");
            foreach (string path in Directory.EnumerateFiles(registry, "*.json"))
            {
                if (!int.TryParse(Path.GetFileNameWithoutExtension(path), out _)) continue;
                using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(path));
                JsonElement row = doc.RootElement;
                bool bound = row.TryGetProperty("sessionId", out JsonElement id) && id.ValueKind == JsonValueKind.String &&
                    id.GetString() == project.SessionId;
                bool sameDirectory = projectPath is not null && row.TryGetProperty("cwd", out JsonElement cwd) &&
                    cwd.ValueKind == JsonValueKind.String && NormalizeDirectory(cwd.GetString()) is { } other &&
                    other.Equals(projectPath, StringComparison.OrdinalIgnoreCase);
                if (!bound && !sameDirectory) continue;
                if (!row.TryGetProperty("pid", out JsonElement value) || !value.TryGetInt32(out int pid))
                    return new("unverified");
                bool? alive = _isAlive(pid);
                if (alive == true && _startTime(pid) is { } actual) alive = SameProcess(row, actual);
                if (alive == false) continue;
                string? client = row.TryGetProperty("entrypoint", out JsonElement entry) && entry.ValueKind == JsonValueKind.String
                    ? entry.GetString() : null;
                if (bound) return alive == true ? new("in-use", client) : new("unverified");
                bool busy = row.TryGetProperty("status", out JsonElement status) && status.ValueKind == JsonValueKind.String &&
                    status.GetString() == "busy";
                if (busy) result = alive == true ? new("cwd-busy", client) : new("unverified");
            }
            return result;
        }
        catch (Exception) { return new("unverified"); }
    }

    /// <summary>Claude 会话尾部证据。会话文件按 UUID 定位并缓存路径;读不到返回 null,由上层显示「未核实」。</summary>
    public ClaudeSessionEvidence? ReadEvidence(ProjectRef project)
    {
        if (!Guid.TryParseExact(project.SessionId, "D", out _)) return null;
        string? transcript = FindTranscript(project);
        return transcript is null ? null : ClaudeSessionEvidence.TryRead(transcript);
    }

    /// <summary>
    /// 只看绑定会话同一文件夹下的顶层会话文件(子代理不在顶层;旧版 <c>agent-*.jsonl</c> 只有子代理记录，读不出应答)。
    /// 按修改时间预筛。「中断之后新开」= 首条带时间戳的记录晚于中断，或文件本身在中断之后才创建——
    /// 分叉续写(fork)的会话会把旧记录复制过来，首条时间早于中断，但它确实是中断之后才开的。
    /// 中断之前就开着、之后继续干活的并行会话不算：那是用户本来的分工，仍按同目录占用(<c>cwd-busy</c>)规则等它空闲后续跑。
    /// 读不了目录、或中断后改过的会话文件读不出归属目录时回答「判断不了」(null),绝不当成「没人接着干」。
    /// </summary>
    public bool? ContinuedInAnotherSession(ProjectRef project, DateTimeOffset since)
    {
        string? transcript = Guid.TryParseExact(project.SessionId, "D", out _) ? FindTranscript(project) : null;
        string? folder = transcript is null ? null : Path.GetDirectoryName(transcript);
        string? owner = NormalizeDirectory(project.Path);
        if (folder is null || owner is null) return null;
        bool unknown = false;
        try
        {
            foreach (string file in Directory.EnumerateFiles(folder, "*.jsonl", SearchOption.TopDirectoryOnly))
            {
                if (string.Equals(file, transcript, StringComparison.OrdinalIgnoreCase) ||
                    !Guid.TryParseExact(Path.GetFileNameWithoutExtension(file), "D", out _) ||
                    File.GetLastWriteTimeUtc(file) <= since.UtcDateTime) continue;
                (_, string? cwd) = ReadMetadata(file, "claudecode");
                if (NormalizeDirectory(cwd) is not { } other)
                {
                    unknown = true;
                    continue;
                }

                if (!other.Equals(owner, StringComparison.OrdinalIgnoreCase)) continue;
                bool openedAfter = FirstTimestamp(file) > since || File.GetCreationTimeUtc(file) > since.UtcDateTime;
                if (openedAfter && ClaudeSessionEvidence.TryRead(file) is { LastReplyAtUtc: { } replied } && replied > since) return true;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        return unknown ? null : false;
    }

    /// <summary>会话文件开头第一条带时间戳的记录;找不到返回 null(此时不凭它认定会话是中断之后新开的)。</summary>
    private static DateTimeOffset? FirstTimestamp(string file) =>
        DateTimeOffset.TryParse(ProjectCatalog.ReadHeadString(file, "timestamp"), System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal,
            out DateTimeOffset at)
            ? at
            : null;

    private string? FindTranscript(ProjectRef project)
    {
        // 缓存键同时含会话与项目：缓存命中也必须是「这个项目的这个会话」,不能凭 UUID 借用别的项目的文件。
        string key = project.SessionId + "|" + NormalizeDirectory(project.Path);
        if (_transcripts.TryGetValue(key, out string? cached) && File.Exists(cached)) return cached;
        if (!Directory.Exists(_claudeRoot)) return null;
        try
        {
            foreach (string file in Directory.EnumerateFiles(_claudeRoot, project.SessionId + ".jsonl", SearchOption.AllDirectories))
            {
                (string? id, string? cwd) = ReadMetadata(file, "claudecode");
                if (id != project.SessionId || NormalizeDirectory(cwd) is not { } owner ||
                    !owner.Equals(NormalizeDirectory(project.Path), StringComparison.OrdinalIgnoreCase)) continue;
                _transcripts[key] = file;
                return file;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return null;
    }

    private static string? NormalizeDirectory(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        try { return Path.GetFullPath(path).TrimEnd('\\', '/'); }
        catch (Exception) { return null; }
    }

    /// <summary>
    /// 活着的这个 PID 还是不是登记里的那个 Claude 进程。true = 是;false = 能证明是 PID 复用;null = 判断不了。
    /// <para>只有「这个进程比登记的进程启动得晚」才证明复用:登记那一刻这个 PID 属于 Claude,
    /// 在那之前就已经启动、至今还活着的进程只能是它本身。<c>procStart</c>(2.1.309 是 FILETIME 字符串)
    /// 不在合理区间时退回 <c>startedAt</c>(毫秒时间戳,登记时刻);两者都用不上时按仍在占用处理。
    /// 比登记启动得还早却又对不上，说明字段语义变了，判断不了——绝不因此把正开着的会话当成空闲。</para>
    /// </summary>
    internal static bool? SameProcess(JsonElement row, long actualFileTime)
    {
        long slack = 2 * TimeSpan.TicksPerSecond;
        long earliest = new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc).ToFileTimeUtc();
        long latest = DateTime.UtcNow.AddDays(1).ToFileTimeUtc();
        if (row.TryGetProperty("procStart", out JsonElement start) && start.ValueKind == JsonValueKind.String &&
            long.TryParse(start.GetString(), out long expected) && expected >= earliest && expected <= latest)
        {
            if (Math.Abs(actualFileTime - expected) <= slack) return true;
            return actualFileTime > expected ? false : null;
        }

        if (row.TryGetProperty("startedAt", out JsonElement registered) && registered.ValueKind == JsonValueKind.Number &&
            registered.TryGetInt64(out long milliseconds) &&
            milliseconds >= DateTimeOffset.FromFileTime(earliest).ToUnixTimeMilliseconds() &&
            milliseconds <= DateTimeOffset.FromFileTime(latest).ToUnixTimeMilliseconds())
        {
            long registeredAt = DateTimeOffset.FromUnixTimeMilliseconds(milliseconds).UtcDateTime.ToFileTimeUtc();
            return actualFileTime <= registeredAt + slack;
        }

        return true;
    }

    private static long? ProcessStartTime(int pid)
    {
        try { using var process = System.Diagnostics.Process.GetProcessById(pid); return process.StartTime.ToFileTimeUtc(); }
        catch (Exception) { return null; }
    }

    private static bool? ProcessAlive(int pid)
    {
        if (pid <= 0) return null;
        try { using var process = System.Diagnostics.Process.GetProcessById(pid); return !process.HasExited; }
        catch (ArgumentException) { return false; }
        catch (Exception) { return null; }
    }

    private static (string? Id, string? Cwd) ReadMetadata(string file, string agent)
    {
        try
        {
            if (file.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Any(p => p.Equals("subagents", StringComparison.OrdinalIgnoreCase))) return (null, null);
            if (agent == "claudecode") return (Path.GetFileNameWithoutExtension(file), ProjectCatalog.ExtractCwd(file));
            // Codex 的 session_meta 首行带着完整的指令文本，可能远超 64 KiB:按行读，不截断。
            byte[]? line = ProjectCatalog.ReadHeadLines(file).FirstOrDefault();
            if (line is null) return (null, null);
            using JsonDocument doc = JsonDocument.Parse(line.AsMemory());
            JsonElement row = doc.RootElement;
            if (row.GetProperty("type").GetString() != "session_meta") return (null, null);
            JsonElement metadata = row.GetProperty("payload");
            if (!metadata.TryGetProperty("source", out JsonElement source) || source.ValueKind != JsonValueKind.String ||
                source.GetString() is not ("cli" or "vscode" or "exec")) return (null, null);
            return (metadata.GetProperty("id").GetString(), metadata.GetProperty("cwd").GetString());
        }
        catch (Exception) { return (null, null); }
    }
}
