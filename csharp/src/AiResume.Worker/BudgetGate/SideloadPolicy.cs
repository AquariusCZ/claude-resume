using System.Text.Json;
using Microsoft.Win32;

namespace AiResume.Worker.BudgetGate;

/// <summary>
/// 组织托管策略是否禁止旁加载 mod(<c>disableSideloadFlags</c>)。
///
/// Claude Code 启动时只要 <c>CLAUDE_CODE_PLUGIN_DIRS</c> 非空就把它记为旁加载来源;托管策略里
/// <c>disableSideloadFlags === true</c> 时直接报错退出(本机二进制实证)。所以这条策略生效时,我方在 settings.json 的
/// 登记会让**所有**新会话——终端、Desktop、AI Resume 自己的续跑——一启动就退出。登记前、以及 Worker 每小时都要查:
/// 命中就不登记并撤下;有策略来源读不出时同样按"不能确认"处理——宁可 mod 不生效,也不能让 Claude Code 起不来。
///
/// 来源与 Claude Code 一致:<c>%ProgramFiles%\ClaudeCode\managed-settings.json</c>、<c>managed-settings.d\*.json</c>、
/// 配置根下的 <c>remote-settings.json</c>、<c>CLAUDE_CODE_REMOTE_SETTINGS_PATH</c> 指向的文件、注册表
/// <c>HKLM/HKCU\SOFTWARE\Policies\ClaudeCode</c> 的 <c>Settings</c>(JSON 字符串)。
/// </summary>
public sealed class SideloadPolicy
{
    private readonly Func<IEnumerable<string>> _files;
    private readonly Func<IEnumerable<string?>> _registryValues;

    public SideloadPolicy(Func<IEnumerable<string>>? files = null, Func<IEnumerable<string?>>? registryValues = null)
    {
        _files = files ?? DefaultFiles;
        _registryValues = registryValues ?? DefaultRegistryValues;
    }

    public SideloadVerdict Check()
    {
        try
        {
            foreach (string path in _files())
            {
                if (!File.Exists(path))
                    continue;
                string text;
                try
                {
                    using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    using var reader = new StreamReader(fs);
                    text = reader.ReadToEnd();
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    return new SideloadVerdict(false, true, $"读不出托管设置 {Path.GetFileName(path)}:{ex.GetType().Name}");
                }

                SideloadVerdict? verdict = Judge(text, Path.GetFileName(path));
                if (verdict is not null)
                    return verdict;
            }

            foreach (string? value in _registryValues())
            {
                if (string.IsNullOrWhiteSpace(value))
                    continue;
                SideloadVerdict? verdict = Judge(value, @"注册表 Policies\ClaudeCode");
                if (verdict is not null)
                    return verdict;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return new SideloadVerdict(false, true, $"读不出托管策略:{ex.GetType().Name}");
        }

        return new SideloadVerdict(false, false, "托管策略未禁止旁加载");
    }

    /// <summary>命中禁止或读不懂返回结论;明确未禁止返回 null(继续看下一个来源)。</summary>
    private static SideloadVerdict? Judge(string json, string source)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(json, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            if (doc.RootElement.ValueKind == JsonValueKind.Object &&
                doc.RootElement.TryGetProperty("disableSideloadFlags", out JsonElement flag) &&
                flag.ValueKind == JsonValueKind.True)
            {
                return new SideloadVerdict(true, false, $"组织策略禁止旁加载 mod(disableSideloadFlags,来源:{source})");
            }

            return null;
        }
        catch (JsonException)
        {
            return new SideloadVerdict(false, true, $"托管设置不是合法 JSON(来源:{source}),无法确认是否禁止旁加载");
        }
    }

    private static IEnumerable<string> DefaultFiles()
    {
        string managed = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "ClaudeCode");
        yield return Path.Combine(managed, "managed-settings.json");
        string dropIns = Path.Combine(managed, "managed-settings.d");
        if (Directory.Exists(dropIns))
        {
            foreach (string file in Directory.EnumerateFiles(dropIns, "*.json").OrderBy(f => f, StringComparer.Ordinal))
                yield return file;
        }

        string? configDir = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR");
        string root = !string.IsNullOrWhiteSpace(configDir)
            ? configDir
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude");
        yield return Path.Combine(root, "remote-settings.json");
        string? remote = Environment.GetEnvironmentVariable("CLAUDE_CODE_REMOTE_SETTINGS_PATH");
        if (!string.IsNullOrWhiteSpace(remote))
            yield return remote;
    }

    private static IEnumerable<string?> DefaultRegistryValues()
    {
        foreach (RegistryKey hive in new[] { Registry.LocalMachine, Registry.CurrentUser })
        {
            using RegistryKey? policy = hive.OpenSubKey(@"SOFTWARE\Policies\ClaudeCode");
            yield return policy?.GetValue("Settings") as string;
        }
    }
}

/// <param name="Blocked">策略明确禁止旁加载。</param>
/// <param name="Unknown">有策略来源读不出或读不懂:是否禁止不能确认。</param>
public sealed record SideloadVerdict(bool Blocked, bool Unknown, string Detail)
{
    /// <summary>可以登记:明确没有禁止。</summary>
    public bool CanRegister => !Blocked && !Unknown;
}
