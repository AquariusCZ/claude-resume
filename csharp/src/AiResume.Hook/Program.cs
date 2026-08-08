using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AiResume.Hook;

/// <summary>
/// AiResume.Hook 处理器可执行文件。
/// 由各 provider 的 hook 配置指向,从 stdin 读取事件负载(JSON),
/// 经抑制判定、字段提取、稳定事件 ID 计算后,幂等写入事件队列。
/// 绝不阻断宿主:任何异常均以 exit code 0 结束,stdout 保持干净。
/// </summary>
public static class Program
{
    /// <summary>
    /// 抑制判定:stop_hook_active 为 true、AI_RESUME_INTERNAL_RUN=1、
    /// stdin 为空或非法 JSON 时返回 true(不产出事件)。
    /// </summary>
    public static bool ShouldSuppress(string? stdinJson, IDictionary<string, string?> env)
    {
        // 内部运行抑制(最高优先级)
        if (env != null &&
            env.TryGetValue("AI_RESUME_INTERNAL_RUN", out var internalRun) &&
            string.Equals(internalRun, "1", StringComparison.Ordinal))
        {
            return true;
        }

        // stdin 为空或非法 JSON 时抑制
        if (string.IsNullOrWhiteSpace(stdinJson))
        {
            return true;
        }

        try
        {
            using var doc = JsonDocument.Parse(stdinJson);
            if (doc.RootElement.ValueKind == JsonValueKind.Object &&
                doc.RootElement.TryGetProperty("stop_hook_active", out var stopHook) &&
                stopHook.ValueKind == JsonValueKind.True)
            {
                return true;
            }
        }
        catch (JsonException)
        {
            return true;
        }

        return false;
    }

    /// <summary>
    /// 计算稳定事件 ID:对 source、sessionId、cwd、transcriptPath 最后写入时间
    /// 四项用竖线连接后取 SHA256 前 16 位十六进制。
    /// transcript 文件不存在时该分量记为空串。
    /// </summary>
    public static string ComputeEventId(string source, string? sessionId, string? cwd, string? transcriptPath)
    {
        string transcriptTime = string.Empty;
        if (!string.IsNullOrWhiteSpace(transcriptPath) && File.Exists(transcriptPath))
        {
            try
            {
                transcriptTime = File.GetLastWriteTimeUtc(transcriptPath)
                    .ToString("o", System.Globalization.CultureInfo.InvariantCulture);
            }
            catch (IOException)
            {
                // 文件可能被占用或已删除,记为空串
            }
            catch (UnauthorizedAccessException)
            {
                // 无权限访问,记为空串
            }
        }

        string raw = string.Join("|", source ?? string.Empty, sessionId ?? string.Empty, cwd ?? string.Empty, transcriptTime);
        byte[] hashBytes = SHA256.HashData(Encoding.UTF8.GetBytes(raw));
        var sb = new StringBuilder(16);
        for (int i = 0; i < 8; i++)
        {
            sb.Append(hashBytes[i].ToString("x2"));
        }
        return sb.ToString();
    }

    /// <summary>
    /// 尝试写入事件文件。先做抑制判定;被抑制返回 false。
    /// 提取字段、计算事件 ID;同 ID 文件已存在则返回 false(幂等)。
    /// 否则以临时文件 + 原子替换方式写入。
    /// </summary>
    public static bool TryWriteEvent(string eventsDirectory, string source, string? stdinJson,
                                     IDictionary<string, string?> env, out string eventId)
    {
        eventId = string.Empty;

        // 抑制判定
        if (ShouldSuppress(stdinJson, env))
        {
            return false;
        }

        // 提取字段
        string? sessionId = null;
        string? cwd = null;
        string? transcriptPath = null;

        try
        {
            using var doc = JsonDocument.Parse(stdinJson!);
            var root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.Object)
            {
                sessionId = GetStringProperty(root, "session_id") ?? GetStringProperty(root, "sessionId");
                cwd = GetStringProperty(root, "cwd");
                transcriptPath = GetStringProperty(root, "transcript_path") ?? GetStringProperty(root, "transcriptPath");
            }
        }
        catch (JsonException)
        {
            // 已由 ShouldSuppress 处理,此处不会到达
        }

        // 环境变量回退
        if (string.IsNullOrEmpty(sessionId) && env != null)
        {
            env.TryGetValue("QODER_SESSION_ID", out sessionId);
        }
        if (string.IsNullOrEmpty(cwd) && env != null)
        {
            env.TryGetValue("QODER_CWD", out cwd);
        }

        // 计算事件 ID
        eventId = ComputeEventId(source, sessionId, cwd, transcriptPath);
        if (string.IsNullOrEmpty(eventId))
        {
            return false;
        }

        // 目标文件路径
        string targetPath = Path.Combine(eventsDirectory, eventId + ".json");

        // 幂等:同 ID 已存在则不重写
        if (File.Exists(targetPath))
        {
            return false;
        }

        // 构造事件对象
        var eventPayload = new Dictionary<string, object?>
        {
            ["eventId"] = eventId,
            ["source"] = source,
            ["sessionId"] = sessionId,
            ["cwd"] = cwd,
            ["transcriptPath"] = transcriptPath,
            ["atUtc"] = DateTime.UtcNow.ToString("o", System.Globalization.CultureInfo.InvariantCulture)
        };

        try
        {
            // 确保目录存在
            Directory.CreateDirectory(eventsDirectory);

            // 临时文件 + 原子替换
            string tempPath = Path.Combine(eventsDirectory, eventId + ".tmp" + Guid.NewGuid().ToString("N"));
            string json = JsonSerializer.Serialize(eventPayload, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(tempPath, json, new UTF8Encoding(false));
            File.Move(tempPath, targetPath, overwrite: false);
            return true;
        }
        catch (IOException)
        {
            // 并发写入时目标可能已存在,视为幂等成功
            return File.Exists(targetPath);
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// 入口:读取 stdin 与环境变量,调用核心逻辑。
    /// 任何异常均以 exit code 0 结束,绝不阻断宿主;stdout 全程无输出。
    /// </summary>
    public static int Main(string[] args)
    {
        try
        {
            // 读取 source(缺失用 unknown)
            string source = args.Length > 0 ? args[0] : "unknown";

            // 读取 stdin
            string? stdinJson = Console.In.ReadToEnd();

            // 环境变量快照
            var env = new Dictionary<string, string?>();
            foreach (System.Collections.DictionaryEntry entry in Environment.GetEnvironmentVariables())
            {
                env[entry.Key?.ToString() ?? string.Empty] = entry.Value?.ToString();
            }

            // 事件目录
            string eventsDirectory = Path.Combine(AiResume.Worker.ShadowPaths.Root, "completion-events");

            // 写入事件
            TryWriteEvent(eventsDirectory, source, stdinJson, env, out _);

            return 0;
        }
        catch (Exception ex)
        {
            // 诊断信息只写 stderr,绝不写 stdout
            Console.Error.WriteLine($"AiResume.Hook error: {ex.Message}");
            return 0;
        }
    }

    /// <summary>
    /// 从 JSON 对象中安全读取字符串属性,不存在或类型不符时返回 null。
    /// </summary>
    private static string? GetStringProperty(JsonElement root, string propertyName)
    {
        if (root.TryGetProperty(propertyName, out var element) &&
            element.ValueKind == JsonValueKind.String)
        {
            return element.GetString();
        }
        return null;
    }
}