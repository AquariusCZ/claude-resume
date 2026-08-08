using AiResume.Core;
using Xunit;

namespace AiResume.Tests;

public sealed class RunKeyTests
{
    [Fact]
    public void Same_components_produce_stable_key()
    {
        const string path = @"C:\Repo\Src";
        const string openId = "ou_123";

        string first = RunKey.Create(TaskKind.Modify, path, openId);
        string second = RunKey.Create(TaskKind.Modify, path, openId);

        Assert.Equal(first, second);
        Assert.Equal("modify|c:\\repo\\src|ou_123", first);
    }

    [Fact]
    public void Path_case_and_separators_are_normalized()
    {
        string canonical = RunKey.Create(TaskKind.Modify, @"C:\Repo\Src", "ou_123");

        Assert.Equal(canonical, RunKey.Create(TaskKind.Modify, @"c:\repo\src", "ou_123"));
        Assert.Equal(canonical, RunKey.Create(TaskKind.Modify, @"C:/Repo/Src", "ou_123"));
        Assert.Equal(canonical, RunKey.Create(TaskKind.Modify, @"c:/REPO//src/", "ou_123"));
        Assert.Equal(canonical, RunKey.Create(TaskKind.Modify, @"  C:\Repo\Src\  ", "ou_123"));
    }

    [Fact]
    public void Drive_root_is_normalized_stably()
    {
        string canonical = RunKey.Create(TaskKind.Query, @"C:\", "ou_1");

        Assert.Equal(canonical, RunKey.Create(TaskKind.Query, @"c:/", "ou_1"));
        Assert.Equal(canonical, RunKey.Create(TaskKind.Query, @"C:", "ou_1"));
        Assert.Equal("query|c:\\|ou_1", canonical);
    }

    [Fact]
    public void Component_changes_produce_different_keys()
    {
        const string path = @"C:\Repo";

        Assert.NotEqual(
            RunKey.Create(TaskKind.Modify, path, "ou_1"),
            RunKey.Create(TaskKind.Query, path, "ou_1"));
        Assert.NotEqual(
            RunKey.Create(TaskKind.Modify, path, "ou_1"),
            RunKey.Create(TaskKind.Modify, path, "ou_2"));
        Assert.NotEqual(
            RunKey.Create(TaskKind.Modify, path, "ou_1"),
            RunKey.Create(TaskKind.Modify, @"D:\Repo", "ou_1"));
    }

    [Fact]
    public void Missing_open_id_keeps_empty_suffix()
    {
        string key = RunKey.Create(TaskKind.Resume, @"C:\Repo", null);

        Assert.Equal("resume|c:\\repo|", key);
        Assert.Equal(key, RunKey.Create(TaskKind.Resume, @"C:\Repo", string.Empty));
        Assert.Equal(key, RunKey.Create(TaskKind.Resume, @"C:\Repo", "   "));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Empty_or_null_project_path_is_rejected(string? path)
    {
        if (path is null)
        {
            Assert.Throws<ArgumentNullException>(() => RunKey.Create(TaskKind.Modify, path!, "ou_1"));
        }
        else
        {
            Assert.Throws<ArgumentException>(() => RunKey.Create(TaskKind.Modify, path, "ou_1"));
        }
    }

    [Fact]
    public void RunKey_function_exists_in_Core_and_nowhere_else_in_solution()
    {
        string solutionRoot = FindSolutionRoot();
        string coreDir = Path.Combine(solutionRoot, "src", "AiResume.Core");

        List<string> definitionFiles = new();
        foreach (string file in Directory.EnumerateFiles(solutionRoot, "*.cs", SearchOption.AllDirectories))
        {
            string normalized = Path.GetFullPath(file);
            if (normalized.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal) ||
                normalized.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal) ||
                normalized.Contains($"{Path.DirectorySeparatorChar}test{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            {
                continue;
            }

            if (File.ReadAllText(file).Contains("static class RunKey", StringComparison.Ordinal))
            {
                definitionFiles.Add(normalized);
            }
        }

        Assert.Single(definitionFiles);
        string corePrefix = Path.GetFullPath(coreDir) + Path.DirectorySeparatorChar;
        Assert.StartsWith(corePrefix, definitionFiles[0], StringComparison.OrdinalIgnoreCase);
    }

    private static string FindSolutionRoot()
    {
        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "AiResume.sln")))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        throw new InvalidOperationException("未找到 AiResume.sln(测试需在 csharp 目录下运行)。");
    }
}
