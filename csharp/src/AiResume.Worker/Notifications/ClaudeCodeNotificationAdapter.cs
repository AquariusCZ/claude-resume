using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AiResume.Worker.Notifications;

/// <summary>
/// Claude Code 通知适配器：管理 <c>~/.claude/settings.json</c> 里 AI Resume 自己的三组 hooks。
///
/// 按官方 hooks 文档(2026-10-30 核对,本机 CLI 2.1.300 / VS Code 2.1.280 / Desktop 2.1.309):
/// <list type="bullet">
/// <item><b>Stop</b>:回合正常结束 → 「已完成」。官方说明 Stop 不支持 matcher,所以不写。</item>
/// <item><b>StopFailure</b>(2.1.78+):回合因 API 错误结束时 Stop <i>不</i>触发，改发它——用量上限、登录失效、
///   账单问题此前完全没有通知。不写 matcher = 接收全部 error。</item>
/// <item><b>Notification</b>:只订阅“真的在等你”的几种 notification_type。</item>
/// </list>
/// 一律写成官方推荐的 <b>exec form</b>(<c>command</c> 只放 exe 绝对路径,<c>args</c> 放参数,2.1.139+):
/// 不经过 shell。旧版写的是带引号的整条 shell 命令，Claude Code 回退到 PowerShell 时会 ParserError(本机复现过)。
///
/// 所有权：可执行文件名精确等于 <see cref="MarkerFileName"/>,且参数只能是 <c>claudecode</c> 加我方 <c>--kind=</c> 开关;
/// 旧的 shell form 照样认领，以便迁移。只增删我方条目，用户自己的 hook 与分组原样保留——
/// 我方条目若混在用户的分组里，会被移出去单独成组，而不是改写那一组的 matcher。
/// </summary>
public sealed class ClaudeCodeNotificationAdapter : INotificationAdapter
{
    /// <summary>所有权标记文件名，用于识别 AI Resume 写入的条目。</summary>
    public const string MarkerFileName = "AiResume.Hook.exe";

    private const string Source = "claudecode";

    /// <summary>
    /// “在等你”的类型。2026-10-30 起补上 <c>quota_auto_resume_stale</c>(额度已重置但电脑睡过头，等你按回车)
    /// 与 <c>quota_auto_resume_disabled</c>(不会自己继续，需要你处理)。
    /// 仍不订阅 idle_prompt(回合结束 60 秒提醒，与 Stop 重复)、auth_success、agent_completed 等。
    /// </summary>
    public const string DecisionMatcher =
        "permission_prompt|worker_permission_prompt|elicitation_dialog|elicitation_url_dialog|agent_needs_input|quota_auto_resume_stale|quota_auto_resume_disabled";

    /// <summary>一组受管 hook:事件名、我方 kind 开关(null = 完成)、matcher(null = 不写)。</summary>
    private sealed record ManagedGroup(string Event, string? KindSwitch, string? Matcher, string Label);

    private static readonly ManagedGroup[] Groups =
    [
        new("Stop", null, null, "完成"),
        new("StopFailure", "--kind=failure", null, "中断"),
        new("Notification", "--kind=decision", DecisionMatcher, "等你"),
    ];

    private readonly string _settingsPath;

    /// <param name="settingsPath">
    /// settings.json 路径;默认与 Claude Code 一致:<c>%CLAUDE_CONFIG_DIR%</c>(设置了时)否则 <c>%USERPROFILE%\.claude</c>
    /// 下的 settings.json——写到 Claude Code 不读的位置,界面却说「已安装」,就是一句无法证伪的假话。
    /// </param>
    public ClaudeCodeNotificationAdapter(string? settingsPath = null)
    {
        _settingsPath = settingsPath ?? Path.Combine(
            Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR") is { Length: > 0 } configDir
                ? configDir
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude"),
            "settings.json");
    }

    /// <inheritdoc />
    public NotificationProviderKind Kind => NotificationProviderKind.ClaudeCode;

    /// <inheritdoc />
    public string DisplayName => "Claude Code";

    /// <inheritdoc />
    public NotificationProviderStatus Probe()
    {
        try
        {
            var dir = Path.GetDirectoryName(_settingsPath);
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
            {
                return new NotificationProviderStatus(
                    Kind, DisplayName, false, false, null, "Claude Code 配置目录不存在");
            }

            if (!File.Exists(_settingsPath))
            {
                return new NotificationProviderStatus(
                    Kind, DisplayName, true, false, _settingsPath, "配置文件不存在");
            }

            if (JsonNode.Parse(File.ReadAllText(_settingsPath)) is not JsonObject root)
            {
                return new NotificationProviderStatus(
                    Kind, DisplayName, true, false, _settingsPath, "配置文件根节点不是 JSON 对象");
            }

            OwnEntry[] own = FindOwnEntries(root).ToArray();
            if (own.Length == 0)
            {
                return new NotificationProviderStatus(
                    Kind, DisplayName, true, false, _settingsPath, "未安装 AI Resume 通知 hook");
            }

            // 「已启用」必须三组都在且形状正确:只剩 Stop 时，中断与「等你」两类通知永远不会到。
            string[] drift = Groups
                .Where(group => !own.Any(entry => IsCanonical(entry, group)))
                .Select(group => group.Label)
                .ToArray();
            string hookCommand = own[0].Command;
            return drift.Length == 0
                ? new NotificationProviderStatus(Kind, DisplayName, true, true, _settingsPath,
                    "已安装 AI Resume 通知 hook(完成 / 中断 / 等你)", HookCommand: hookCommand)
                : new NotificationProviderStatus(Kind, DisplayName, true, true, _settingsPath,
                    $"hook 不完整或为旧格式(缺:{string.Join("、", drift)});关掉再打开本开关或重新安装即可修复",
                    HookCommand: hookCommand, HookBroken: true);
        }
        catch (Exception ex)
        {
            return new NotificationProviderStatus(
                Kind, DisplayName, true, false, _settingsPath,
                $"配置读取失败: {ex.Message}", Unverified: true);
        }
    }

    /// <inheritdoc />
    public void Enable(string hookCommand)
    {
        string exe = HookCommand.ExtractExecutable(hookCommand) ??
                     throw new ArgumentException("hookCommand 不能为空", nameof(hookCommand));
        if (exe.Contains('"') || !Path.IsPathFullyQualified(exe))
            throw new ArgumentException("hook 可执行文件必须是不含引号的绝对路径", nameof(hookCommand));

        var dir = Path.GetDirectoryName(_settingsPath);
        if (string.IsNullOrEmpty(dir))
            throw new InvalidOperationException("无法确定配置目录路径");
        Directory.CreateDirectory(dir);

        JsonObject root = ReadRoot() ?? new JsonObject();
        OwnEntry[] own = FindOwnEntries(root).ToArray();

        // 已经恰好是三组规范条目、且没有多余的我方条目:幂等空操作，不改写文件、不调整顺序。
        bool canonical = own.Length == Groups.Length &&
                         Groups.All(group => own.Count(entry => IsCanonical(entry, group, exe)) == 1);
        if (canonical)
        {
            return;
        }

        if (root["hooks"] is not JsonObject hooks)
        {
            hooks = new JsonObject();
            root["hooks"] = hooks;
        }

        RemoveOwnEntries(hooks);
        foreach (ManagedGroup group in Groups)
        {
            if (hooks[group.Event] is not JsonArray array)
            {
                array = new JsonArray();
                hooks[group.Event] = array;
            }

            var args = new JsonArray { Source };
            if (group.KindSwitch is not null)
            {
                args.Add(group.KindSwitch);
            }

            var entry = new JsonObject();
            if (group.Matcher is not null)
            {
                entry["matcher"] = group.Matcher;
            }

            entry["hooks"] = new JsonArray
            {
                new JsonObject
                {
                    ["type"] = "command",
                    ["command"] = exe,
                    ["args"] = args,
                    ["timeout"] = 30,
                },
            };
            array.Add(entry);
        }

        AtomicWrite(root);
    }

    /// <inheritdoc />
    public void Disable()
    {
        if (!File.Exists(_settingsPath))
            return;

        JsonObject? root = ReadRoot();
        if (root?["hooks"] is not JsonObject hooks)
            return;

        if (!RemoveOwnEntries(hooks))
            return;

        if (hooks.Count == 0)
            root.Remove("hooks");

        AtomicWrite(root);
    }

    private JsonObject? ReadRoot()
    {
        if (!File.Exists(_settingsPath))
            return null;

        try
        {
            return JsonNode.Parse(File.ReadAllText(_settingsPath)) as JsonObject
                   ?? throw new InvalidOperationException("配置文件根节点不是 JSON 对象");
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"读取现有配置失败: {ex.Message}", ex);
        }
    }

    /// <summary>我方的一条 hook:所在事件、所在分组、命令原文、kind 开关、是否 exec form、分组里是否还有别人的 hook。</summary>
    private sealed record OwnEntry(string Event, JsonObject Group, JsonObject Hook, string Command,
        string? KindSwitch, bool ExecForm, bool SharedGroup);

    private static IEnumerable<OwnEntry> FindOwnEntries(JsonObject root)
    {
        if (root["hooks"] is not JsonObject hooks)
            yield break;

        foreach ((string eventName, JsonNode? eventNode) in hooks)
        {
            if (eventNode is not JsonArray groups)
                continue;

            foreach (JsonObject group in groups.OfType<JsonObject>())
            {
                if (group["hooks"] is not JsonArray entries)
                    continue;

                foreach (JsonObject hook in entries.OfType<JsonObject>())
                {
                    if (TryParseOwn(hook, out string? command, out string? kindSwitch, out bool execForm))
                    {
                        yield return new OwnEntry(eventName, group, hook, command!, kindSwitch, execForm, entries.Count > 1);
                    }
                }
            }
        }
    }

    /// <summary>
    /// 是否我方条目。exec form:command 的文件名是标记 exe,args 是 <c>["claudecode", 我方开关…]</c>;
    /// 旧 shell form:没有 args,由 <see cref="HookCommand.IsManaged"/> 判定整条命令。
    /// </summary>
    private static bool TryParseOwn(JsonObject hook, out string? command, out string? kindSwitch, out bool execForm)
    {
        command = null;
        kindSwitch = null;
        execForm = false;
        if (hook["command"] is not JsonValue commandValue || !commandValue.TryGetValue(out string? text) ||
            string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        if (hook.ContainsKey("args"))
        {
            if (hook["args"] is not JsonArray args || args.Count == 0 ||
                args.Any(arg => arg is not JsonValue value || !value.TryGetValue(out string? _)) ||
                !string.Equals(Path.GetFileName(HookCommand.ExtractExecutable(text) ?? string.Empty), MarkerFileName,
                    StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            string[] values = args.Select(arg => arg!.GetValue<string>()).ToArray();
            if (!string.Equals(values[0], Source, StringComparison.OrdinalIgnoreCase) ||
                !values.Skip(1).All(value => value.StartsWith("--kind=", StringComparison.Ordinal)))
            {
                return false;
            }

            command = text;
            kindSwitch = values.Skip(1).LastOrDefault();
            execForm = true;
            return true;
        }

        if (!HookCommand.IsManaged(text, MarkerFileName, Source))
        {
            return false;
        }

        command = text;
        kindSwitch = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .LastOrDefault(token => token.StartsWith("--kind=", StringComparison.Ordinal));
        return true;
    }

    private static bool IsCanonical(OwnEntry entry, ManagedGroup group, string? exe = null) =>
        entry.ExecForm && !entry.SharedGroup &&
        string.Equals(entry.Event, group.Event, StringComparison.Ordinal) &&
        string.Equals(entry.KindSwitch, group.KindSwitch, StringComparison.Ordinal) &&
        (exe is null || string.Equals(entry.Command, exe, StringComparison.OrdinalIgnoreCase)) &&
        string.Equals(entry.Group["matcher"] is JsonValue matcher && matcher.TryGetValue(out string? text) ? text : null,
            group.Matcher, StringComparison.Ordinal) &&
        entry.Hook["args"] is JsonArray args && args.Count == (group.KindSwitch is null ? 1 : 2);

    /// <summary>从所有事件里移除我方条目，空分组与空事件键一并移除;用户自己的 hook 原样保留。</summary>
    private static bool RemoveOwnEntries(JsonObject hooks)
    {
        bool changed = false;
        foreach (string eventName in hooks.Select(pair => pair.Key).ToArray())
        {
            if (hooks[eventName] is not JsonArray groups)
                continue;

            for (int i = groups.Count - 1; i >= 0; i--)
            {
                if (groups[i] is not JsonObject group || group["hooks"] is not JsonArray entries)
                    continue;

                for (int j = entries.Count - 1; j >= 0; j--)
                {
                    if (entries[j] is JsonObject hook && TryParseOwn(hook, out _, out _, out _))
                    {
                        entries.RemoveAt(j);
                        changed = true;
                    }
                }

                if (entries.Count == 0)
                {
                    groups.RemoveAt(i);
                    changed = true;
                }
            }

            if (groups.Count == 0)
                hooks.Remove(eventName);
        }

        return changed;
    }

    /// <summary>原子写回：先备份原文件为 .bak,再写临时文件并替换。</summary>
    private void AtomicWrite(JsonNode root)
    {
        var dir = Path.GetDirectoryName(_settingsPath);
        if (string.IsNullOrEmpty(dir))
            throw new InvalidOperationException("无法确定配置目录路径");
        Directory.CreateDirectory(dir);

        if (File.Exists(_settingsPath))
        {
            File.Copy(_settingsPath, _settingsPath + ".bak", true);
        }

        var tempPath = _settingsPath + ".tmp";
        try
        {
            var json = root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
            using (var fs = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
            using (var writer = new StreamWriter(fs, new UTF8Encoding(false)))
            {
                writer.Write(json);
                writer.Flush();
                fs.Flush(flushToDisk: true);
            }

            File.Move(tempPath, _settingsPath, overwrite: true);
        }
        catch
        {
            try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { /* 忽略清理失败 */ }
            throw;
        }
    }
}
