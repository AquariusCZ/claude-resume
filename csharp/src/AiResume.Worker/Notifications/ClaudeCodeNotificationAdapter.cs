using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AiResume.Worker.Notifications;

/// <summary>
/// Claude Code 通知适配器:管理 ~/.claude/settings.json 中 hooks.Stop 的合并写入/移除。
/// 所有权标记为命令中包含 <see cref="MarkerFileName"/> 的条目。
/// </summary>
public sealed class ClaudeCodeNotificationAdapter : INotificationAdapter
{
    /// <summary>所有权标记文件名,用于识别 AI Resume 写入的条目。</summary>
    public const string MarkerFileName = "AiResume.Hook.exe";

    private readonly string _settingsPath;

    /// <summary>
    /// 初始化适配器。
    /// </summary>
    /// <param name="settingsPath">settings.json 路径;默认 %USERPROFILE%\.claude\settings.json。</param>
    public ClaudeCodeNotificationAdapter(string? settingsPath = null)
    {
        _settingsPath = settingsPath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".claude", "settings.json");
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

            var json = File.ReadAllText(_settingsPath);
            using var doc = JsonDocument.Parse(json);
            var isEnabled = ContainsOwnEntry(doc.RootElement);
            return new NotificationProviderStatus(
                Kind, DisplayName, true, isEnabled, _settingsPath,
                isEnabled ? "已安装 AI Resume 通知钩子" : "未安装 AI Resume 通知钩子");
        }
        catch (Exception ex)
        {
            return new NotificationProviderStatus(
                Kind, DisplayName, true, false, _settingsPath,
                $"配置读取失败: {ex.Message}");
        }
    }

    /// <inheritdoc />
    public void Enable(string hookCommand)
    {
        if (string.IsNullOrWhiteSpace(hookCommand))
            throw new ArgumentException("hookCommand 不能为空", nameof(hookCommand));

        var dir = Path.GetDirectoryName(_settingsPath);
        if (string.IsNullOrEmpty(dir))
            throw new InvalidOperationException("无法确定配置目录路径");
        Directory.CreateDirectory(dir);

        JsonNode? root;
        if (File.Exists(_settingsPath))
        {
            try
            {
                var json = File.ReadAllText(_settingsPath);
                root = JsonNode.Parse(json);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"读取现有配置失败: {ex.Message}", ex);
            }
        }
        else
        {
            root = new JsonObject();
        }

        if (root is not JsonObject rootObj)
            throw new InvalidOperationException("配置文件根节点不是 JSON 对象");

        // 已存在我方条目则幂等返回
        if (ContainsOwnEntry(rootObj))
            return;

        // 确保 hooks 对象存在
        if (!rootObj.TryGetPropertyValue("hooks", out var hooksNode) || hooksNode is not JsonObject hooksObj)
        {
            hooksObj = new JsonObject();
            rootObj["hooks"] = hooksObj;
        }

        // 确保 Stop 数组存在
        if (!hooksObj.TryGetPropertyValue("Stop", out var stopNode) || stopNode is not JsonArray stopArray)
        {
            stopArray = new JsonArray();
            hooksObj["Stop"] = stopArray;
        }

        // 追加我方条目
        var entry = new JsonObject
        {
            ["matcher"] = "",
            ["hooks"] = new JsonArray
            {
                new JsonObject
                {
                    ["type"] = "command",
                    ["command"] = hookCommand,
                    ["timeout"] = 30
                }
            }
        };
        stopArray.Add(entry);

        AtomicWrite(rootObj);
    }

    /// <inheritdoc />
    public void Disable()
    {
        if (!File.Exists(_settingsPath))
            return;

        JsonNode? root;
        try
        {
            var json = File.ReadAllText(_settingsPath);
            root = JsonNode.Parse(json);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"读取现有配置失败: {ex.Message}", ex);
        }

        if (root is not JsonObject rootObj)
            return;

        if (!rootObj.TryGetPropertyValue("hooks", out var hooksNode) || hooksNode is not JsonObject hooksObj)
            return;

        if (!hooksObj.TryGetPropertyValue("Stop", out var stopNode) || stopNode is not JsonArray stopArray)
            return;

        bool changed = false;

        // 从后往前遍历,便于移除
        for (int i = stopArray.Count - 1; i >= 0; i--)
        {
            if (stopArray[i] is not JsonObject stopEntry)
                continue;

            if (!stopEntry.TryGetPropertyValue("hooks", out var entryHooksNode) || entryHooksNode is not JsonArray entryHooks)
                continue;

            // 移除我方 command 条目
            for (int j = entryHooks.Count - 1; j >= 0; j--)
            {
                if (entryHooks[j] is JsonObject hookObj &&
                    hookObj.TryGetPropertyValue("command", out var cmdNode) &&
                    cmdNode is JsonValue cmdValue &&
                    cmdValue.TryGetValue<string>(out var cmdStr) &&
                    cmdStr.Contains(MarkerFileName, StringComparison.OrdinalIgnoreCase))
                {
                    entryHooks.RemoveAt(j);
                    changed = true;
                }
            }

            // 若 hooks 数组为空则移除整个 Stop 分组
            if (entryHooks.Count == 0)
            {
                stopArray.RemoveAt(i);
                changed = true;
            }
        }

        if (!changed)
            return;

        // 若 Stop 数组为空则移除 Stop 键
        if (stopArray.Count == 0)
            hooksObj.Remove("Stop");

        // 若 hooks 对象为空则移除 hooks 键
        if (hooksObj.Count == 0)
            rootObj.Remove("hooks");

        AtomicWrite(rootObj);
    }

    /// <summary>
    /// 检查 JSON 根节点中是否包含我方条目。
    /// </summary>
    private static bool ContainsOwnEntry(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
            return false;

        if (!root.TryGetProperty("hooks", out var hooks) || hooks.ValueKind != JsonValueKind.Object)
            return false;

        if (!hooks.TryGetProperty("Stop", out var stop) || stop.ValueKind != JsonValueKind.Array)
            return false;

        foreach (var entry in stop.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Object)
                continue;

            if (!entry.TryGetProperty("hooks", out var entryHooks) || entryHooks.ValueKind != JsonValueKind.Array)
                continue;

            foreach (var hook in entryHooks.EnumerateArray())
            {
                if (hook.ValueKind != JsonValueKind.Object)
                    continue;

                if (hook.TryGetProperty("command", out var cmd) &&
                    cmd.ValueKind == JsonValueKind.String &&
                    cmd.GetString()?.Contains(MarkerFileName, StringComparison.OrdinalIgnoreCase) == true)
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>
    /// 检查 JsonObject 中是否包含我方条目。
    /// </summary>
    private static bool ContainsOwnEntry(JsonObject root)
    {
        if (!root.TryGetPropertyValue("hooks", out var hooksNode) || hooksNode is not JsonObject hooksObj)
            return false;

        if (!hooksObj.TryGetPropertyValue("Stop", out var stopNode) || stopNode is not JsonArray stopArray)
            return false;

        foreach (var entryNode in stopArray)
        {
            if (entryNode is not JsonObject entryObj)
                continue;

            if (!entryObj.TryGetPropertyValue("hooks", out var entryHooksNode) || entryHooksNode is not JsonArray entryHooks)
                continue;

            foreach (var hookNode in entryHooks)
            {
                if (hookNode is not JsonObject hookObj)
                    continue;

                if (hookObj.TryGetPropertyValue("command", out var cmdNode) &&
                    cmdNode is JsonValue cmdValue &&
                    cmdValue.TryGetValue<string>(out var cmdStr) &&
                    cmdStr.Contains(MarkerFileName, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>
    /// 原子写回:先备份原文件为 .bak,再写临时文件并替换。
    /// </summary>
    private void AtomicWrite(JsonNode root)
    {
        var dir = Path.GetDirectoryName(_settingsPath);
        if (string.IsNullOrEmpty(dir))
            throw new InvalidOperationException("无法确定配置目录路径");
        Directory.CreateDirectory(dir);

        // 备份原文件(覆盖式)
        if (File.Exists(_settingsPath))
        {
            var bakPath = _settingsPath + ".bak";
            File.Copy(_settingsPath, bakPath, true);
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
            // 清理临时文件,不留半成品
            try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { /* 忽略清理失败 */ }
            throw;
        }
    }
}