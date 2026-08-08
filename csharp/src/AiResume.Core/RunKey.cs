using System.Text.RegularExpressions;

namespace AiResume.Core;

/// <summary>
/// runKey 的唯一规范生成函数(关 D-011)。
/// 格式:taskKind|normalizedProjectPath|openId。全解决方案只允许调用本函数生成 runKey,
/// 不得在 Storage/Ipc/Worker/Gui 等位置复制实现。
/// </summary>
public static class RunKey
{
    private static readonly Regex SeparatorRun = new(@"\\+", RegexOptions.Compiled);

    public static string Create(TaskKind taskKind, string projectPath, string? openId)
    {
        ArgumentNullException.ThrowIfNull(projectPath);

        string normalized = NormalizeProjectPath(projectPath);
        string openIdPart = string.IsNullOrWhiteSpace(openId) ? string.Empty : openId.Trim();
        return string.Concat(taskKind.ToWireCode(), "|", normalized, "|", openIdPart);
    }

    /// <summary>
    /// 路径归一化:统一分隔符为 '\'、合并连续分隔符、去除尾部分隔符(保留根路径)、
    /// 大小写统一为小写(Windows 路径不区分大小写)。相对路径与 UNC 前缀保留。
    /// </summary>
    public static string NormalizeProjectPath(string path)
    {
        ArgumentNullException.ThrowIfNull(path);

        string trimmed = path.Trim();
        if (trimmed.Length == 0)
        {
            throw new ArgumentException("项目路径不能为空。", nameof(path));
        }

        bool unc = trimmed.StartsWith(@"\\", StringComparison.Ordinal);
        string separators = trimmed.Replace('/', '\\');
        string collapsed = unc
            ? @"\\" + SeparatorRun.Replace(separators[2..], "\\")
            : SeparatorRun.Replace(separators, "\\");

        if (IsRootPath(collapsed))
        {
            // 裸盘符 "C:" 与 "C:\" 必须归一到同一个 key,统一补足尾分隔符。
            string lower = collapsed.ToLowerInvariant();
            return lower.Length == 2 ? lower + '\\' : lower;
        }

        return collapsed.TrimEnd('\\').ToLowerInvariant();
    }

    private static bool IsRootPath(string path) =>
        path.Length == 2 && IsDriveLetter(path[0]) && path[1] == ':' ||
        path.Length == 3 && IsDriveLetter(path[0]) && path[1] == ':' && path[2] == '\\' ||
        path == @"\\";

    private static bool IsDriveLetter(char c) => c is >= 'A' and <= 'Z' or >= 'a' and <= 'z';
}
