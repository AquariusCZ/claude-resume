using System.Text;
using System.Text.Json;
using AiResume.Core;
using AiResume.Worker.Probes;
using AiResume.Worker.Products;

namespace AiResume.Worker.Resume;

/// <summary>只读取原生会话头部元数据；保存精确 UUID，不恢复、不修改原生会话。</summary>
public sealed class ResumeSessionCatalog
{
    private readonly string _claudeRoot;
    private readonly string _codexRoot;
    private readonly Func<int, bool?> _isAlive;
    public ResumeSessionCatalog(string? claudeRoot = null, string? codexRoot = null, Func<int, bool?>? isAlive = null)
    {
        _claudeRoot = claudeRoot ?? Path.Combine(Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR") ??
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude"), "projects");
        _codexRoot = codexRoot ?? Path.Combine(CodexAuthProbe.ResolveCodexHome(), "sessions");
        _isAlive = isAlive ?? ProcessAlive;
    }

    public IReadOnlyList<ProjectRef> Bind(string agent, IReadOnlyList<string> paths)
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
        return selected.Select(path =>
        {
            if (!latest.TryGetValue(path, out var session))
                throw new InvalidOperationException($"项目 {Path.GetFileName(path)} 没有可核实的 {agent} 会话，无法布防续跑");
            return new ProjectRef { Path = path, Name = Path.GetFileName(path.TrimEnd('\\', '/')), SessionId = session.Id };
        }).ToArray();
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
    public bool? IsClaudeSessionIdle(string sessionId)
    {
        string registry = Path.Combine(Path.GetDirectoryName(_claudeRoot)!, "sessions");
        try
        {
            if (!Directory.Exists(registry)) return true;
            foreach (string path in Directory.EnumerateFiles(registry, "*.json"))
            {
                if (!int.TryParse(Path.GetFileNameWithoutExtension(path), out _)) continue;
                using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(path));
                if (!doc.RootElement.TryGetProperty("sessionId", out JsonElement id) || id.GetString() != sessionId) continue;
                if (!doc.RootElement.TryGetProperty("pid", out JsonElement value) || !value.TryGetInt32(out int pid)) return null;
                bool? alive = _isAlive(pid);
                if (alive != false) return alive == true ? false : null;
            }
            return true;
        }
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
            using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            byte[] buffer = new byte[65536];
            int count = stream.Read(buffer);
            string line = Encoding.UTF8.GetString(buffer, 0, count).Split('\n')[0];
            using JsonDocument doc = JsonDocument.Parse(line);
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
