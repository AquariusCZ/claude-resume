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
            var isEnabled = File.Exists(pluginPath);

            string? detail = null;
            if (!isInstalled)
            {
                detail = $"OpenCode 配置目录不存在: {configDirectory}";
            }
            else if (isEnabled)
            {
                detail = $"插件文件已存在: {pluginPath}";
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
                detail);
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

                // 内容不同,先备份再覆盖
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
            if (File.Exists(pluginPath))
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
        sb.AppendLine("// AI Resume 通知插件 - 由 AI Resume 自动生成,请勿手动修改");
        sb.AppendLine("// 监听 session.idle 事件,在 agent 完成响应时触发通知");
        sb.AppendLine();
        sb.AppendLine("export const AiResumeNotify = async ({ project, directory }) => {");
        sb.AppendLine("  return {");
        sb.AppendLine("    event: async ({ event }) => {");
        sb.AppendLine("      try {");
        sb.AppendLine("        // 仅在 session.idle 事件时触发");
        sb.AppendLine("        if (event.type !== \"session.idle\") {");
        sb.AppendLine("          return;");
        sb.AppendLine("        }");
        sb.AppendLine();
        sb.AppendLine("        // 确定项目目录:优先使用事件提供的目录,其次使用插件注入的目录");
        sb.AppendLine("        const targetDir = directory || project?.directory || process.cwd();");
        sb.AppendLine();
        sb.AppendLine("        // 通过 Bun 的 $ 执行通知命令,传入项目目录");
        sb.AppendLine("        const cmd = " + QuoteForTs(hookCommand) + ";");
        sb.AppendLine("        await Bun.$`" + "${cmd}" + " ${targetDir}`.quiet();");
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