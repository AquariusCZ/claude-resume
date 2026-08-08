using System;
using System.IO;
using System.Text;

namespace AiResume.Worker.Notifications;

/// <summary>
/// Cline 通知适配器。
/// Cline 的边界是 hooks 目录下的 TaskComplete.ps1 脚本文件(不是 JSON 配置)。
/// 通过 wrapper 脚本实现通知钩子,保留用户原有 hook 脚本。
/// </summary>
public sealed class ClineNotificationAdapter : INotificationAdapter
{
    /// <summary>hook 脚本文件名。</summary>
    public const string HookFileName = "TaskComplete.ps1";

    /// <summary>用户原脚本备份文件名。</summary>
    public const string PreviousFileName = "TaskComplete.ai-resume-previous.ps1";

    /// <summary>所有权标记,脚本首行注释包含此标记即视为 AI Resume 管理。</summary>
    public const string Marker = "AI Resume managed completion hook";

    private readonly string _hooksDirectory;

    /// <summary>
    /// 初始化适配器。
    /// </summary>
    /// <param name="hooksDirectory">hooks 目录;为 null 时使用默认路径 %USERPROFILE%\Documents\Cline\Hooks。</param>
    public ClineNotificationAdapter(string? hooksDirectory = null)
    {
        _hooksDirectory = hooksDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "Documents", "Cline", "Hooks");
    }

    /// <inheritdoc />
    public NotificationProviderKind Kind => NotificationProviderKind.Cline;

    /// <inheritdoc />
    public string DisplayName => "Cline";

    /// <inheritdoc />
    public NotificationProviderStatus Probe()
    {
        try
        {
            var installed = Directory.Exists(_hooksDirectory);
            var hookPath = Path.Combine(_hooksDirectory, HookFileName);
            var enabled = installed && File.Exists(hookPath) && FileContainsMarker(hookPath);

            return new NotificationProviderStatus(
                Kind,
                DisplayName,
                IsInstalled: installed,
                IsEnabled: enabled,
                ConfigPath: enabled ? hookPath : null,
                Detail: installed
                    ? (enabled ? "已安装 AI Resume 通知钩子" : "Cline hooks 目录存在,但未安装 AI Resume 通知钩子")
                    : "未检测到 Cline hooks 目录");
        }
        catch (Exception ex)
        {
            return new NotificationProviderStatus(
                Kind,
                DisplayName,
                IsInstalled: false,
                IsEnabled: false,
                ConfigPath: null,
                Detail: $"探测异常: {ex.Message}");
        }
    }

    /// <inheritdoc />
    public void Enable(string hookCommand)
    {
        try
        {
            Directory.CreateDirectory(_hooksDirectory);

            var hookPath = Path.Combine(_hooksDirectory, HookFileName);
            var previousPath = Path.Combine(_hooksDirectory, PreviousFileName);

            // 若已含标记,幂等空操作
            if (File.Exists(hookPath) && FileContainsMarker(hookPath))
            {
                return;
            }

            // 若存在用户原脚本且不含标记,先备份
            if (File.Exists(hookPath) && !FileContainsMarker(hookPath))
            {
                File.Copy(hookPath, previousPath, overwrite: true);
            }

            // 原子写入 wrapper 脚本
            var wrapperScript = BuildWrapperScript(hookCommand, previousPath);
            var tempPath = hookPath + ".tmp";
            File.WriteAllText(tempPath, wrapperScript, new UTF8Encoding(true));
            File.Move(tempPath, hookPath, overwrite: true);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"启用 Cline 通知失败: {ex.Message}", ex);
        }
    }

    /// <inheritdoc />
    public void Disable()
    {
        try
        {
            var hookPath = Path.Combine(_hooksDirectory, HookFileName);
            var previousPath = Path.Combine(_hooksDirectory, PreviousFileName);

            // 仅当脚本含标记时处理
            if (!File.Exists(hookPath) || !FileContainsMarker(hookPath))
            {
                return;
            }

            if (File.Exists(previousPath))
            {
                // 用备份还原
                File.Copy(previousPath, hookPath, overwrite: true);
                File.Delete(previousPath);
            }
            else
            {
                // 无备份则删除
                File.Delete(hookPath);
            }
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"停用 Cline 通知失败: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// 生成 PowerShell wrapper 脚本。
    /// </summary>
    /// <param name="hookCommand">AI Resume.Hook 可执行文件路径。</param>
    /// <param name="previousPath">用户原脚本路径;不存在时传 null 或空字符串。</param>
    /// <returns>wrapper 脚本内容(UTF-8 带 BOM,CRLF 换行)。</returns>
    public static string BuildWrapperScript(string hookCommand, string previousPath)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# {Marker}");
        sb.AppendLine("# 此脚本由 AI Resume 管理,请勿手动修改");
        sb.AppendLine();
        sb.AppendLine("$stdin = [Console]::In.ReadToEnd()");
        sb.AppendLine();

        if (!string.IsNullOrEmpty(previousPath) && File.Exists(previousPath))
        {
            var escapedPreviousPath = EscapePowerShellString(previousPath);
            var escapedHookCommand = EscapePowerShellString(hookCommand);

            sb.AppendLine($"$previousScript = '{escapedPreviousPath}'");
            sb.AppendLine("if (Test-Path $previousScript) {");
            sb.AppendLine("    $previousOutput = & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $previousScript 2>&1 | Out-String");
            sb.AppendLine("    $previousExitCode = $LASTEXITCODE");
            sb.AppendLine("    if ($previousExitCode -ne 0) {");
            sb.AppendLine("        Write-Output $previousOutput");
            sb.AppendLine("        exit $previousExitCode");
            sb.AppendLine("    }");
            sb.AppendLine("    $previousJson = $previousOutput | ConvertFrom-Json -ErrorAction SilentlyContinue");
            sb.AppendLine("    if ($previousJson -and $previousJson.cancel -eq $true) {");
            sb.AppendLine("        Write-Output $previousOutput");
            sb.AppendLine("        exit 0");
            sb.AppendLine("    }");
            sb.AppendLine("    try {");
            sb.AppendLine($"        & '{escapedHookCommand}' cline | Out-Null");
            sb.AppendLine("    } catch {");
            sb.AppendLine("        # 忽略我方处理器异常");
            sb.AppendLine("    }");
            sb.AppendLine("    if ([string]::IsNullOrWhiteSpace($previousOutput)) {");
            sb.AppendLine("        Write-Output '{\"cancel\":false}'");
            sb.AppendLine("    } else {");
            sb.AppendLine("        Write-Output $previousOutput");
            sb.AppendLine("    }");
            sb.AppendLine("} else {");
            sb.AppendLine("    try {");
            sb.AppendLine($"        & '{escapedHookCommand}' cline | Out-Null");
            sb.AppendLine("    } catch {");
            sb.AppendLine("        # 忽略我方处理器异常");
            sb.AppendLine("    }");
            sb.AppendLine("    Write-Output '{\"cancel\":false}'");
            sb.AppendLine("}");
        }
        else
        {
            var escapedHookCommand = EscapePowerShellString(hookCommand);
            sb.AppendLine("try {");
            sb.AppendLine($"    & '{escapedHookCommand}' cline | Out-Null");
            sb.AppendLine("} catch {");
            sb.AppendLine("    # 忽略我方处理器异常");
            sb.AppendLine("}");
            sb.AppendLine("Write-Output '{\"cancel\":false}'");
        }

        // 转换为 CRLF 换行
        var content = sb.ToString().Replace("\r\n", "\n").Replace("\n", "\r\n");
        return content;
    }

    /// <summary>
    /// 检查文件内容是否包含所有权标记。
    /// </summary>
    private static bool FileContainsMarker(string filePath)
    {
        try
        {
            var content = File.ReadAllText(filePath);
            return content.Contains(Marker, StringComparison.Ordinal);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// PowerShell 字符串转义:单引号重复。
    /// </summary>
    private static string EscapePowerShellString(string value)
    {
        return value.Replace("'", "''");
    }
}