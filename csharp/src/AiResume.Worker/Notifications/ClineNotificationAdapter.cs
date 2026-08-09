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
                    : "未检测到 Cline hooks 目录",
                HookCommand: enabled ? FindHookCommand(hookPath) : null);
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

            // 若存在用户原脚本且不含标记,先备份
            if (File.Exists(hookPath) && !FileContainsMarker(hookPath))
            {
                File.Copy(hookPath, previousPath, overwrite: true);
            }

            // 我方 wrapper 也重新生成:安装目录或命令形状改变时必须原位刷新,
            // 不能依赖“先 Disable 再 Enable”这种会制造空窗的对账方式。
            var wrapperScript = BuildWrapperScript(hookCommand, previousPath);
            if (File.Exists(hookPath) &&
                string.Equals(File.ReadAllText(hookPath), wrapperScript, StringComparison.Ordinal))
            {
                return;
            }
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
            sb.AppendLine("    $previousErrorPath = [IO.Path]::GetTempFileName()");
            sb.AppendLine("    try {");
            sb.AppendLine("        $previousOutput = $stdin | & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $previousScript 2> $previousErrorPath | Out-String");
            sb.AppendLine("        $previousExitCode = $LASTEXITCODE");
            sb.AppendLine("        $previousError = if (Test-Path $previousErrorPath) { Get-Content -LiteralPath $previousErrorPath -Raw -ErrorAction SilentlyContinue } else { '' }");
            sb.AppendLine("    } finally {");
            sb.AppendLine("        Remove-Item -LiteralPath $previousErrorPath -Force -ErrorAction SilentlyContinue");
            sb.AppendLine("    }");
            sb.AppendLine("    if (-not [string]::IsNullOrWhiteSpace($previousError)) {");
            sb.AppendLine("        [Console]::Error.Write($previousError)");
            sb.AppendLine("    }");
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
            sb.AppendLine($"        $stdin | & '{escapedHookCommand}' cline | Out-Null");
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
            sb.AppendLine($"        $stdin | & '{escapedHookCommand}' cline | Out-Null");
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
            sb.AppendLine($"    $stdin | & '{escapedHookCommand}' cline | Out-Null");
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
            using var reader = new StreamReader(filePath, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            return string.Equals(reader.ReadLine(), $"# {Marker}", StringComparison.Ordinal);
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

    /// <summary>
    /// 从 wrapper 脚本里读回我方那条命令的原文;读不出返回 null。
    ///
    /// 脚本是本适配器自己写的,形状固定为 <c>&amp; '&lt;path&gt;' cline | Out-Null</c>,
    /// 所以这里按同一份契约反解即可。取它是为了让注册表核对
    /// "这个程序还在不在" —— Cline 的 wrapper 会把执行异常整个吞掉
    /// (catch 块里写着"忽略我方处理器异常"),文件没了也不会有任何报错。
    /// </summary>
    public static string? FindHookCommand(string wrapperPath)
    {
        try
        {
            return ParseHookCommand(File.ReadAllText(wrapperPath));
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>反解 wrapper 脚本里的调用行。与 <see cref="BuildWrapperScript"/> 互为逆操作。</summary>
    public static string? ParseHookCommand(string script)
    {
        var m = System.Text.RegularExpressions.Regex.Match(
            script, @"^\s*(?:\$stdin\s*\|\s*)?&\s*'(?<cmd>(?:[^']|'')*)'\s+cline\b",
            System.Text.RegularExpressions.RegexOptions.Multiline);

        // 单引号在 PowerShell 里靠翻倍转义,读回来要还原;
        // 不还原的话路径里带引号的用户会被误判成"文件不存在"。
        return m.Success ? m.Groups["cmd"].Value.Replace("''", "'") : null;
    }
}
