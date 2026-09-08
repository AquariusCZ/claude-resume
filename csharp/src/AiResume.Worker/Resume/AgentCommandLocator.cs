namespace AiResume.Worker.Resume;

public sealed record AgentCommand(string Executable, IReadOnlyList<string> Prefix);

public static class AgentCommandLocator
{
    public static AgentCommand? Find(string agent)
    {
        string name = agent switch { "claudecode" => "claude", "codex" => "codex", _ => string.Empty };
        if (name.Length == 0) return null;
        foreach (string raw in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator))
        {
            string directory = raw.Trim('"');
            try
            {
                string executable = Path.Combine(directory, name + ".exe");
                if (Path.IsPathFullyQualified(executable) && File.Exists(executable)) return new(executable, Array.Empty<string>());
                // npm 的官方入口本来就是 node + bin/codex.js；薄调用同一入口，避免 cmd 转义。
                string script = Path.Combine(directory, "node_modules", "@openai", "codex", "bin", "codex.js");
                if (agent == "codex" && File.Exists(Path.Combine(directory, "codex.cmd")) && File.Exists(script))
                {
                    string? node = FindNode();
                    if (node is not null) return new(node, new[] { script });
                }
            }
            catch (Exception) { /* PATH 中的无效目录不作为执行器。 */ }
        }
        return null;
    }

    public static string? CodexVersion()
    {
        AgentCommand? command = Find("codex");
        if (command is null) return null;
        if (command.Prefix.Count == 1)
        {
            string package = Path.Combine(Path.GetDirectoryName(command.Prefix[0])!, "..", "package.json");
            using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(package));
            string? version = doc.RootElement.GetProperty("version").GetString();
            return version is { Length: > 0 and < 40 } && version.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-') ? version.Split('-')[0] : null;
        }
        string? nativeVersion = System.Diagnostics.FileVersionInfo.GetVersionInfo(command.Executable).ProductVersion;
        return nativeVersion is { Length: > 0 and < 40 } && nativeVersion.All(c => char.IsAsciiDigit(c) || c == '.') ? nativeVersion : null;
    }

    private static string? FindNode()
    {
        foreach (string directory in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator))
        {
            try
            {
                string executable = Path.Combine(directory.Trim('"'), "node.exe");
                if (Path.IsPathFullyQualified(executable) && File.Exists(executable)) return executable;
            }
            catch (Exception) { }
        }
        return null;
    }
}
