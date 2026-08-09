using System;
using System.IO;
using System.Text;

namespace AiResume.Worker.Notifications;

/// <summary>
/// OpenCode 通知适配器。
/// 在 ~/.config/opencode/plugins/ 下写入独立插件文件 airesume-notify.ts,
/// 监听 session.idle 事件,在 agent 完成响应时触发通知命令。
/// 不修改用户任何既有插件文件,Disable 仅删除我方插件文件。
/// </summary>
public sealed class OpenCodeNotificationAdapter : INotificationAdapter
{
    /// <summary>我方插件文件名,用于识别所有权。</summary>
    public const string PluginFileName = "airesume-notify.ts";

    /// <summary>文件内容中的稳定所有权标记。文件名相同不能证明文件属于 AI Resume。</summary>
    public const string ManagedMarker = "// AI Resume managed OpenCode notification plugin";

    /// <summary>2026-08-09 之前生成文件的精确首行,仅用于一次性原位升级。</summary>
    public const string LegacyManagedMarker = "// AI Resume 通知插件 - 由 AI Resume 自动生成,请勿手动修改";

    private readonly string _pluginsDirectory;

    /// <summary>
    /// 初始化适配器。
    /// </summary>
    /// <param name="pluginsDirectory">插件目录路径;默认 %USERPROFILE%\.config\opencode\plugins。</param>
    public OpenCodeNotificationAdapter(string? pluginsDirectory = null)
    {
        _pluginsDirectory = pluginsDirectory ?? GetDefaultPluginsDirectory();
    }

    /// <inheritdoc />
    public NotificationProviderKind Kind => NotificationProviderKind.OpenCode;

    /// <inheritdoc />
    public string DisplayName => "OpenCode";

    /// <inheritdoc />
    public NotificationProviderStatus Probe()
    {
        try
        {
            // 插件目录的父目录(~/.config/opencode)存在即视为已安装
            var configDirectory = Path.GetDirectoryName(_pluginsDirectory);
            var isInstalled = !string.IsNullOrEmpty(configDirectory) && Directory.Exists(configDirectory);
            var pluginPath = Path.Combine(_pluginsDirectory, PluginFileName);
            bool pluginExists = File.Exists(pluginPath);
            bool isEnabled = pluginExists && IsManagedPlugin(pluginPath);

            string? detail = null;
            if (!isInstalled)
            {
                detail = $"OpenCode 配置目录不存在: {configDirectory}";
            }
            else if (isEnabled)
            {
                detail = $"已安装 AI Resume 通知插件: {pluginPath}";
            }
            else if (pluginExists)
            {
                detail = $"同名插件属于用户或其他工具,未接管: {pluginPath}";
            }
            else
            {
                detail = "插件文件未安装";
            }

            return new NotificationProviderStatus(
                Kind,
                DisplayName,
                isInstalled,
                isEnabled,
                isInstalled ? configDirectory : null,
                detail,
                HookCommand: isEnabled ? FindHookCommand(pluginPath) : null);
        }
        catch (Exception ex)
        {
            return new NotificationProviderStatus(
                Kind,
                DisplayName,
                false,
                false,
                null,
                $"探测失败: {ex.Message}");
        }
    }

    /// <inheritdoc />
    public void Enable(string hookCommand)
    {
        try
        {
            // 确保插件目录存在
            Directory.CreateDirectory(_pluginsDirectory);

            var pluginPath = Path.Combine(_pluginsDirectory, PluginFileName);
            var source = BuildPluginSource(hookCommand);

            if (File.Exists(pluginPath))
            {
                var existing = File.ReadAllText(pluginPath, Encoding.UTF8);
                if (string.Equals(existing, source, StringComparison.Ordinal))
                {
                    // 内容一致,幂等空操作
                    return;
                }

                if (!IsManagedSource(existing))
                {
                    throw new InvalidOperationException(
                        $"同名插件已存在且不属于 AI Resume,拒绝覆盖: {pluginPath}");
                }

                // 只有已证实属于我方的旧版本才允许备份并刷新。
                var backupPath = pluginPath + ".bak";
                File.Copy(pluginPath, backupPath, overwrite: true);
            }

            // 原子写入:临时文件 + flush + 替换
            var tempPath = pluginPath + ".tmp" + Guid.NewGuid().ToString("N");
            try
            {
                using (var fs = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                using (var writer = new StreamWriter(fs, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)))
                {
                    writer.Write(source);
                    writer.Flush();
                    fs.Flush(flushToDisk: true);
                }

                File.Move(tempPath, pluginPath, overwrite: true);
            }
            catch
            {
                // 清理临时文件,不留半成品
                try
                {
                    if (File.Exists(tempPath))
                    {
                        File.Delete(tempPath);
                    }
                }
                catch
                {
                    // 忽略清理失败
                }

                throw;
            }
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"启用 OpenCode 通知插件失败: {ex.Message}", ex);
        }
    }

    /// <inheritdoc />
    public void Disable()
    {
        try
        {
            var pluginPath = Path.Combine(_pluginsDirectory, PluginFileName);
            if (File.Exists(pluginPath) && IsManagedPlugin(pluginPath))
            {
                File.Delete(pluginPath);
            }
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"停用 OpenCode 通知插件失败: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// 生成 OpenCode 插件 TypeScript 源码。
    /// 插件导出 async 函数,返回 hooks 对象;仅在 session.idle 事件时触发通知命令。
    /// 任何异常都吞掉,不影响 OpenCode 主流程。
    /// </summary>
    /// <param name="hookCommand">要执行的命令。</param>
    /// <returns>TypeScript 插件源码。</returns>
    public static string BuildPluginSource(string hookCommand)
    {
        // 注意:逐行拼接,确保 C# 字符串中的引号转义正确
        var sb = new StringBuilder();
        sb.AppendLine(ManagedMarker);
        sb.AppendLine("// 由 AI Resume 自动生成,请勿手动修改");
        sb.AppendLine("// 监听 session.idle 事件,在 agent 完成响应时触发通知");
        sb.AppendLine();
        sb.AppendLine("export const AiResumeNotify = async ({ client, project, directory, worktree }) => {");
        sb.AppendLine("  return {");
        sb.AppendLine("    event: async ({ event }) => {");
        sb.AppendLine("      try {");
        sb.AppendLine("        // 仅在 session.idle 事件时触发");
        sb.AppendLine("        if (event.type !== \"session.idle\") {");
        sb.AppendLine("          return;");
        sb.AppendLine("        }");
        sb.AppendLine();
        sb.AppendLine("        const sessionId = event.properties?.sessionID || \"\";");
        sb.AppendLine("        if (!sessionId) return;");
        sb.AppendLine();
        sb.AppendLine("        // Task 工具会创建带 parentID 的子 session,它们也会发布 session.idle。");
        sb.AppendLine("        // 只有查证为顶层 session 才能代表整个用户任务完成;查询失败时 fail-closed。");
        sb.AppendLine("        const targetDir = directory || worktree || project?.directory || process.cwd();");
        sb.AppendLine("        const unwrapSession = (response) => response?.data || (response?.id ? response : null);");
        sb.AppendLine("        let session = null;");
        sb.AppendLine("        try {");
        sb.AppendLine("          session = unwrapSession(await client.session.get({");
        sb.AppendLine("            path: { id: sessionId }, query: { directory: targetDir }");
        sb.AppendLine("          }));");
        sb.AppendLine("        } catch {}");
        sb.AppendLine("        if (!session) {");
        sb.AppendLine("          try {");
        sb.AppendLine("            session = unwrapSession(await client.session.get({ sessionID: sessionId, directory: targetDir }));");
        sb.AppendLine("          } catch {}");
        sb.AppendLine("        }");
        sb.AppendLine("        if (!session || session.parentID) return;");
        sb.AppendLine();
        sb.AppendLine("        const timestamp = new Date().toISOString();");
        sb.AppendLine("        const payload = JSON.stringify({");
        sb.AppendLine("          hook_event_name: \"session.idle\",");
        sb.AppendLine("          session_id: sessionId,");
        sb.AppendLine("          cwd: targetDir,");
        sb.AppendLine("          event_id: `session.idle:${sessionId}:${timestamp}`,");
        sb.AppendLine("          timestamp");
        sb.AppendLine("        });");
        sb.AppendLine();
        sb.AppendLine("        // Bun.spawn 使用 argv 数组,路径含空格时不会被 shell 再拆分;JSON 经 stdin 传给统一 Hook。");
        sb.AppendLine("        const cmd = " + QuoteForTs(hookCommand) + ";");
        sb.AppendLine("        const child = Bun.spawn([cmd, \"opencode\"], {");
        sb.AppendLine("          stdin: new TextEncoder().encode(payload),");
        sb.AppendLine("          stdout: \"ignore\",");
        sb.AppendLine("          stderr: \"ignore\"");
        sb.AppendLine("        });");
        sb.AppendLine("        await child.exited;");
        sb.AppendLine("      } catch (err) {");
        sb.AppendLine("        // 吞掉所有异常,不影响 OpenCode 主流程");
        sb.AppendLine("        console.error(\"[airesume-notify] 通知执行失败:\", err);");
        sb.AppendLine("      }");
        sb.AppendLine("    }");
        sb.AppendLine("  };");
        sb.AppendLine("};");
        sb.AppendLine();
        sb.AppendLine("export default AiResumeNotify;");
        return sb.ToString();
    }

    /// <summary>
    /// 获取默认插件目录路径。
    /// </summary>
    private static string GetDefaultPluginsDirectory()
    {
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(userProfile, ".config", "opencode", "plugins");
    }

    /// <summary>
    /// 从插件源码里读回我方那条命令的原文;读不出返回 null。
    ///
    /// 插件是本适配器自己写的(<c>const cmd = "…";</c>),按同一份契约反解即可。
    /// 取它是为了核对"这个程序还在不在" —— 插件的 catch 块把执行异常整个吞掉,
    /// 文件没了 OpenCode 侧不会有任何可见迹象。
    /// </summary>
    public static string? FindHookCommand(string pluginPath)
    {
        try
        {
            return ParseHookCommand(File.ReadAllText(pluginPath, Encoding.UTF8));
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>反解插件源码里的命令常量。与 <see cref="QuoteForTs"/> 互为逆操作。</summary>
    public static string? ParseHookCommand(string source)
    {
        var m = System.Text.RegularExpressions.Regex.Match(
            source, @"const\s+cmd\s*=\s*""(?<cmd>(?:[^""\\]|\\.)*)""\s*;");
        if (!m.Success)
        {
            return null;
        }

        // QuoteForTs 转义了 \ " \r \n,这里按相同顺序的逆序还原。
        return m.Groups["cmd"].Value
            .Replace("\\r", "\r")
            .Replace("\\n", "\n")
            .Replace("\\\"", "\"")
            .Replace("\\\\", "\\");
    }

    private static bool IsManagedPlugin(string pluginPath)
    {
        try
        {
            return IsManagedSource(File.ReadAllText(pluginPath, Encoding.UTF8));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static bool IsManagedSource(string source)
    {
        using var reader = new StringReader(source);
        string? firstLine = reader.ReadLine();
        bool current = string.Equals(firstLine, ManagedMarker, StringComparison.Ordinal);
        bool legacy = string.Equals(firstLine, LegacyManagedMarker, StringComparison.Ordinal);
        if (!current && !legacy)
        {
            return false;
        }

        // 文件名和首行仍不够:再核对我方生成器的稳定结构,避免用户文档或字符串常量碰撞。
        bool sharedShape = source.Contains("export const AiResumeNotify", StringComparison.Ordinal) &&
                           source.Contains("event.type !== \"session.idle\"", StringComparison.Ordinal) &&
                           source.Contains("const cmd = ", StringComparison.Ordinal);
        if (!sharedShape)
        {
            return false;
        }

        return current
            ? source.Contains("hook_event_name: \"session.idle\"", StringComparison.Ordinal) &&
              source.Contains("new TextEncoder().encode(payload)", StringComparison.Ordinal) &&
              source.Contains("Bun.spawn([cmd, \"opencode\"]", StringComparison.Ordinal)
            : source.Contains("export const AiResumeNotify = async ({ project, directory })", StringComparison.Ordinal) &&
              source.Contains("await Bun.$`${cmd} ${targetDir}`.quiet();", StringComparison.Ordinal);
    }

    /// <summary>
    /// 将 C# 字符串转义为 TypeScript 字符串字面量。
    /// </summary>
    private static string QuoteForTs(string value)
    {
        // 转义反斜杠、双引号和换行符
        var escaped = value
            .Replace("\\", "\\\\")
            .Replace("\"", "\\\"")
            .Replace("\r", "\\r")
            .Replace("\n", "\\n");
        return "\"" + escaped + "\"";
    }
}
