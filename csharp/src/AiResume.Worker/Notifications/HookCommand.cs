namespace AiResume.Worker.Notifications;

/// <summary>生成/解析写入 shell 型 hook 配置的命令。</summary>
public static class HookCommand
{
    /// <summary>
    /// Windows 的 Claude Code/Qoder command 字段是一整条 shell 命令。
    /// 可执行文件路径必须加引号,来源参数由适配器固定追加。
    /// </summary>
    public static string Format(string executable, string source)
    {
        string exe = ExtractExecutable(executable) ??
                     throw new ArgumentException("hook 可执行文件路径为空或无效", nameof(executable));
        if (exe.Contains('"'))
        {
            throw new ArgumentException("hook 可执行文件路径不能包含双引号", nameof(executable));
        }
        if (string.IsNullOrWhiteSpace(source) || source.Any(char.IsWhiteSpace))
        {
            throw new ArgumentException("通知源参数为空或含空白", nameof(source));
        }

        return $"\"{exe}\" {source}";
    }

    /// <summary>从引号命令或裸路径中提取第一个 .exe 路径。</summary>
    public static string? ExtractExecutable(string? command)
    {
        string value = command?.Trim() ?? string.Empty;
        if (value.Length == 0)
        {
            return null;
        }

        if (value[0] == '"')
        {
            int closing = value.IndexOf('"', 1);
            if (closing > 1)
            {
                return value[1..closing];
            }
        }

        int exeEnd = value.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
        return exeEnd < 0 ? null : value[..(exeEnd + 4)].Trim().Trim('"');
    }

    /// <summary>
    /// 只把“首个可执行文件名精确等于 marker，且其后没有参数或只有指定 source”
    /// 的命令视为 AI Resume 所有。参数文本里偶然出现 marker 不构成所有权证据。
    /// </summary>
    public static bool IsManaged(string? command, string markerFileName, string source)
    {
        string value = command?.Trim() ?? string.Empty;
        string? executable = ExtractExecutable(value);
        if (executable is null ||
            !string.Equals(Path.GetFileName(executable), markerFileName, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        int executableEnd;
        if (value.StartsWith('"'))
        {
            executableEnd = value.IndexOf('"', 1);
            if (executableEnd < 0)
            {
                return false;
            }

            executableEnd++;
        }
        else
        {
            int exeEnd = value.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
            if (exeEnd < 0)
            {
                return false;
            }

            executableEnd = exeEnd + 4;
        }

        string remainder = value[executableEnd..].Trim();
        return remainder.Length == 0 || string.Equals(remainder, source, StringComparison.OrdinalIgnoreCase);
    }
}
