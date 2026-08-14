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

    /// <summary>从引号命令或裸路径中提取可执行文件路径。</summary>
    public static string? ExtractExecutable(string? command)
    {
        string value = command?.Trim() ?? string.Empty;
        if (value.Length == 0)
        {
            return null;
        }

        int executableEnd = FindExecutableEnd(value);
        if (executableEnd < 0)
        {
            return null;
        }

        return value[..executableEnd].Trim().Trim('"');
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

        int executableEnd = FindExecutableEnd(value);
        if (executableEnd < 0)
        {
            return false;
        }

        string remainder = value[executableEnd..].Trim();
        if (remainder.Length == 0)
        {
            return true;
        }

        // 允许来源参数后面再跟我们自己的开关(目前只有 --kind=),否则决策类那条
        // `… claudecode --kind=decision` 会被判成"不是我方条目" —— 后果是既删不掉、
        // 也每次安装都再追加一条。但**只认已知开关**:仅凭 exe 名就认领,会把用户
        // 自己写的、恰好也调用这个 exe 的命令一并改掉。
        string[] tokens = Tokenize(remainder);
        if (tokens.Length == 0 || !string.Equals(tokens[0], source, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return tokens.Skip(1).All(IsOwnSwitch);
    }

    private static string[] Tokenize(string value) => value.Split(
        (char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>目前只有 <c>--kind=</c>。新增开关时必须同步这里,否则旧条目会被当成用户自己的。</summary>
    private static bool IsOwnSwitch(string token) =>
        token.StartsWith("--kind=", StringComparison.Ordinal);

    /// <summary>尾部是否形如「来源名 + 若干我方开关」——不校验来源名具体是什么,只看形状。</summary>
    private static bool IsOwnArgumentTail(string remainder)
    {
        string[] tokens = Tokenize(remainder);
        return tokens.Length >= 1 && tokens.Skip(1).All(IsOwnSwitch);
    }

    private static int FindExecutableEnd(string value)
    {
        if (value.StartsWith('"'))
        {
            int closing = value.IndexOf('"', 1);
            return closing > 1 ? closing + 1 : -1;
        }

        if (value.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            return value.Length;
        }

        int searchStart = 0;
        while (searchStart < value.Length)
        {
            int exeEnd = value.IndexOf(".exe", searchStart, StringComparison.OrdinalIgnoreCase);
            if (exeEnd < 0)
            {
                return -1;
            }

            int candidateEnd = exeEnd + 4;
            if (candidateEnd == value.Length || char.IsWhiteSpace(value[candidateEnd]))
            {
                string remainder = value[candidateEnd..].Trim();
                // "尾部不含空格"是用来避免在带空格的路径里切错 .exe 的启发式。
                // 但我方决策命令尾部本来就有两段(`claudecode --kind=decision`),
                // 一刀切会把它判成找不到边界。放宽到"多出来的都是我们自己的开关"为止:
                // 真切错位置时 tokens[0] 会是路径片段而不是来源名,循环会继续找下一个 .exe。
                if (remainder.Length == 0 || IsOwnArgumentTail(remainder))
                {
                    return candidateEnd;
                }
            }

            searchStart = candidateEnd;
        }

        return -1;
    }
}
