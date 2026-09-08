using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;

namespace AiResume.Worker.Resume;

/// <summary>使用 Claude Code 上游 host-provider 固定功能，保留普通项目权限设置。</summary>
public static class ClaudeHostProvider
{
    private static readonly ConcurrentDictionary<string, bool> Capabilities = new();
    public const string Flag = "CLAUDE_CODE_PROVIDER_MANAGED_BY_HOST";

    public static void Validate(AgentCommand command, string project)
    {
        FileInfo info = new(command.Executable);
        string key = info.FullName + ":" + info.Length + ":" + info.LastWriteTimeUtc.Ticks;
        if (!Capabilities.GetOrAdd(key, _ => ContainsCapability(command.Executable)))
            throw new InvalidOperationException("当前 Claude Code 尚未核实供应商固定能力，请先更新客户端");
        string managed = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "ClaudeCode");
        string root = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR") ??
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude");
        if (File.Exists(Path.Combine(managed, "managed-settings.json")) ||
            Directory.Exists(Path.Combine(managed, "managed-settings.d")) ||
            File.Exists(Path.Combine(root, "remote-settings.json")) ||
            !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("CLAUDE_CODE_REMOTE_SETTINGS_PATH")))
            throw new InvalidOperationException("Claude 存在组织管理设置，当前不能核实供应商固定与组织策略是否兼容");
        foreach (RegistryKey hive in new[] { Registry.LocalMachine, Registry.CurrentUser })
        {
            using RegistryKey? policy = hive.OpenSubKey(@"SOFTWARE\Policies\ClaudeCode");
            if (policy?.GetValue("Settings") is not null)
                throw new InvalidOperationException("Claude 存在组织管理策略，当前不能核实供应商固定权限");
        }
        foreach (string path in ExecutionConfigLease.Paths("claudecode", project))
        {
            if (!File.Exists(path)) continue;
            using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(path));
            if (doc.RootElement.EnumerateObject().Any(p => p.Name is "forceLoginMethod" or "forceLoginOrgUUID" or "policyHelpers"))
                throw new InvalidOperationException("Claude 包含组织登录约束或动态策略，请通过原客户端续跑");
            if (doc.RootElement.TryGetProperty("env", out JsonElement env) && env.ValueKind == JsonValueKind.Object &&
                env.EnumerateObject().Any(p => IsTransport(p.Name) && p.Value.GetString() != Environment.GetEnvironmentVariable(p.Name)))
                throw new InvalidOperationException("Claude 设置包含独立的 TLS 或代理约束，当前无法同时固定供应商并保持该约束");
        }
        foreach (System.Collections.DictionaryEntry entry in Environment.GetEnvironmentVariables())
        {
            string name = entry.Key.ToString()!;
            string value = entry.Value?.ToString() ?? string.Empty;
            if (value.Length == 0 || value is "0" or "false") continue;
            if (name.StartsWith("CLAUDE_CODE_USE_", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("CREDENTIALS_FD", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("WIF", StringComparison.OrdinalIgnoreCase) ||
                name is "ANTHROPIC_UNIX_SOCKET" or "ANTHROPIC_CUSTOM_HEADERS" ||
                name.StartsWith("CLAUDE_CODE_HOST_", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Claude 使用尚不能固定的路由或认证模式，请通过原客户端续跑");
        }
    }

    private static bool IsTransport(string name) => name.Contains("PROXY", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("TLS", StringComparison.OrdinalIgnoreCase) || name.Contains("CERT", StringComparison.OrdinalIgnoreCase);

    private static bool ContainsCapability(string path)
    {
        byte[] marker = Encoding.ASCII.GetBytes(Flag);
        using FileStream stream = File.OpenRead(path);
        byte[] buffer = new byte[65536 + marker.Length];
        int retained = 0, count;
        while ((count = stream.Read(buffer, retained, 65536)) > 0)
        {
            int length = retained + count;
            if (buffer.AsSpan(0, length).IndexOf(marker) >= 0) return true;
            retained = Math.Min(marker.Length - 1, length);
            buffer.AsSpan(length - retained, retained).CopyTo(buffer);
        }
        return false;
    }
}
