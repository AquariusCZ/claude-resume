using Xunit;

namespace AiResume.Tests;

public sealed class GuiNotificationContractTests
{
    [Fact]
    public void 启用Codex通知后明确提示重启已运行客户端()
    {
        string html = File.ReadAllText(FindRepositoryFile(
            Path.Combine("csharp", "src", "AiResume.Gui", "wwwroot", "index.html")));

        Assert.Contains("const kind = el.dataset.kind;", html, StringComparison.Ordinal);
        Assert.Contains("const enabling = !el.classList.contains('on');", html, StringComparison.Ordinal);
        Assert.Contains("kind === 'Codex' && enabling", html, StringComparison.Ordinal);
        Assert.Contains("重启已运行的 Codex 后生效", html, StringComparison.Ordinal);
    }

    private static string FindRepositoryFile(string relativePath)
    {
        DirectoryInfo? current = new(AppContext.BaseDirectory);
        while (current is not null)
        {
            string candidate = Path.Combine(current.FullName, relativePath);
            if (File.Exists(candidate))
            {
                return candidate;
            }
            current = current.Parent;
        }

        throw new FileNotFoundException(relativePath);
    }
}
