using AiResume.Worker.Probes;

namespace AiResume.Worker.Resume;

/// <summary>运行期间对已有连接配置持只读共享句柄，阻止并发改写/替换。</summary>
public sealed class ExecutionConfigLease : IDisposable
{
    private readonly List<FileStream> _files = new();
    private readonly Dictionary<string, bool> _existence = new(StringComparer.OrdinalIgnoreCase);

    public bool Holds(string path) => _files.Any(file => Path.GetFullPath(file.Name).Equals(Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase));
    public bool IsCurrent => _existence.All(pair => File.Exists(pair.Key) == pair.Value);

    public static ExecutionConfigLease Acquire(string agent, string project) => Acquire(Paths(agent, project).Where(path =>
        !(agent == "claudecode" && Path.GetFileName(path) is ".claude.json" or ".config.json")));

    public static ExecutionConfigLease Acquire(IEnumerable<string> paths)
    {
        var lease = new ExecutionConfigLease();
        try
        {
            foreach (string path in paths.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                bool exists = File.Exists(path);
                lease._existence[Path.GetFullPath(path)] = exists;
                if (exists) lease._files.Add(new(path, FileMode.Open, FileAccess.Read, FileShare.Read));
            }
            return lease;
        }
        catch { lease.Dispose(); throw; }
    }

    public static IEnumerable<string> Paths(string agent, string project)
    {
        if (agent == "claudecode")
        {
            string user = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            string root = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR") ?? Path.Combine(user, ".claude");
            yield return Path.Combine(root, ".config.json");
            yield return Path.Combine(Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR") ?? user, ".claude.json");
            yield return Path.Combine(root, "settings.json");
            yield return Path.Combine(root, "settings.local.json");
            yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "ClaudeCode", "managed-settings.json");
            yield return Path.Combine(project, ".claude", "settings.json");
            yield return Path.Combine(project, ".claude", "settings.local.json");
        }
        else
        {
            string root = CodexAuthProbe.ResolveCodexHome();
            yield return Path.Combine(root, "config.toml");
            yield return Path.Combine(root, "managed_config.toml");
            yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "OpenAI", "Codex", "config.toml");
            for (DirectoryInfo? directory = new(Path.GetFullPath(project)); directory is not null; directory = directory.Parent)
                yield return Path.Combine(directory.FullName, ".codex", "config.toml");
        }
    }

    public void Dispose()
    {
        foreach (FileStream file in _files) file.Dispose();
        _files.Clear();
    }
}
