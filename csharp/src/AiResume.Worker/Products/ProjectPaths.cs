using AiResume.Core;

namespace AiResume.Worker.Products;

/// <summary>项目路径的等价比较:与 <see cref="RunKey.NormalizeProjectPath"/> 一致，非法路径退回去尾分隔符的忽略大小写比较。</summary>
public static class ProjectPaths
{
    public static bool Equal(string? left, string? right)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
        {
            return false;
        }

        try
        {
            return string.Equals(
                RunKey.NormalizeProjectPath(left),
                RunKey.NormalizeProjectPath(right),
                StringComparison.Ordinal);
        }
        catch (ArgumentException)
        {
            return string.Equals(
                left.Trim().TrimEnd('\\', '/'),
                right.Trim().TrimEnd('\\', '/'),
                StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>字典里与该路径等价的已有键;没有时返回路径本身(新建条目用)。</summary>
    public static string KeyFor<T>(IReadOnlyDictionary<string, T>? map, string path) =>
        map?.Keys.FirstOrDefault(key => Equal(key, path)) ?? path;

    public static T? Find<T>(IReadOnlyDictionary<string, T>? map, string path) where T : class =>
        map?.FirstOrDefault(pair => Equal(pair.Key, path)).Value;
}
