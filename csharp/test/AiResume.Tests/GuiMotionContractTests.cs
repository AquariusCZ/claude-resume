using Xunit;

namespace AiResume.Tests;

public sealed class GuiMotionContractTests
{
    [Fact]
    public void 刷新使用真实忙碌控件且减少动态时仍保留必要反馈()
    {
        string html = File.ReadAllText(FindRepositoryFile(
            Path.Combine("csharp", "src", "AiResume.Gui", "wwwroot", "index.html")));

        Assert.Contains("<button type=\"button\" class=\"lnk refresh\" id=\"refreshBtn\" aria-busy=\"false\">", html,
            StringComparison.Ordinal);
        Assert.Contains("<span class=\"link-label\" id=\"refreshLabel\">刷新额度</span>", html,
            StringComparison.Ordinal);
        Assert.Contains("<span class=\"refresh-spinner\" aria-hidden=\"true\"></span>", html,
            StringComparison.Ordinal);
        Assert.Contains(".refresh.busy .refresh-spinner{visibility:visible;opacity:1;animation:spin", html,
            StringComparison.Ordinal);
        Assert.DoesNotContain(".refresh-spinner::after", html, StringComparison.Ordinal);
        Assert.Contains("btn.setAttribute('aria-busy', 'true');", html, StringComparison.Ordinal);
        Assert.Contains("btn.disabled = true;", html, StringComparison.Ordinal);
        Assert.Contains("$('refreshLabel').textContent = '刷新中';", html, StringComparison.Ordinal);

        Assert.Contains("@media (prefers-reduced-motion:reduce)", html, StringComparison.Ordinal);
        Assert.Contains(".refresh.busy .refresh-spinner,.btn.busy::after", html, StringComparison.Ordinal);
        Assert.Contains("animation-name:low-motion-fade", html, StringComparison.Ordinal);
        Assert.DoesNotContain(".refresh.busy .refresh-spinner{animation:none", html, StringComparison.Ordinal);
    }

    [Fact]
    public void 状态探测和短促入场动画不依赖外部脚本()
    {
        string html = File.ReadAllText(FindRepositoryFile(
            Path.Combine("csharp", "src", "AiResume.Gui", "wwwroot", "index.html")));

        Assert.Contains(".pv.probing .led::after", html, StringComparison.Ordinal);
        Assert.Contains("let providerProbeBusy = true;", html, StringComparison.Ordinal);
        Assert.Contains("providerProbeBusy = false;", html, StringComparison.Ordinal);
        Assert.Contains(".modal:not([hidden]) .sheet{animation:sheet-in", html, StringComparison.Ordinal);
        Assert.Contains(".pv{animation:statein", html, StringComparison.Ordinal);
        Assert.Contains("btn.textContent = '扫描中';", html, StringComparison.Ordinal);
        Assert.DoesNotContain("<script src=", html, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Provider探测异常必须结束探测态且不得保留旧绿()
    {
        string html = File.ReadAllText(FindRepositoryFile(
            Path.Combine("csharp", "src", "AiResume.Gui", "wwwroot", "index.html")));

        Assert.Contains("if (probedRows == null)", html, StringComparison.Ordinal);
        Assert.Contains("nm, cls: 'bad', txt: '探测失败', detail: failure", html, StringComparison.Ordinal);
        Assert.Contains("nm: row.nm, cls: 'wait', txt: '状态过期'", html, StringComparison.Ordinal);
        Assert.Contains("renderProviders(lastQuotaState);", html, StringComparison.Ordinal);
        Assert.DoesNotContain("探测失败保持上一次显示", html, StringComparison.Ordinal);
    }

    [Fact]
    public void 队列标题操作使用原生键盘可达按钮()
    {
        string html = File.ReadAllText(FindRepositoryFile(
            Path.Combine("csharp", "src", "AiResume.Gui", "wwwroot", "index.html")));

        Assert.Contains("<button type=\"button\" class=\"act\" id=\"hidToggle\" hidden>", html,
            StringComparison.Ordinal);
        Assert.Contains("<button type=\"button\" class=\"act\" id=\"addBtn\"", html,
            StringComparison.Ordinal);
        Assert.Contains("<button type=\"button\" class=\"act\" id=\"rescanBtn\"", html,
            StringComparison.Ordinal);
        Assert.Contains(".ch .act:focus-visible{outline:1px solid var(--vermilion)", html,
            StringComparison.Ordinal);
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
