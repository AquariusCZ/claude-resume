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
        // 载入态一律以省略号结尾,与面板其它处的「探测中…」「正在加载…」「正在刷新…」一致。
        Assert.Contains("$('refreshLabel').textContent = '刷新中…';", html, StringComparison.Ordinal);

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
        Assert.Contains(".modal.open .sheet{opacity:1;transform:none}", html, StringComparison.Ordinal);
        Assert.Contains("transform:translateY(6px) scale(.98);transform-origin:center", html,
            StringComparison.Ordinal);
        Assert.Contains("transition:opacity .16s var(--ease-out),transform .2s var(--ease-out)", html,
            StringComparison.Ordinal);
        Assert.Contains("void modal.offsetWidth;", html, StringComparison.Ordinal);
        Assert.Contains("modal.classList.remove('open');", html, StringComparison.Ordinal);
        Assert.Contains("fsModalCloseTimer = window.setTimeout", html, StringComparison.Ordinal);
        Assert.Contains(".modal.instant,.modal.instant .sheet{transition:none}", html,
            StringComparison.Ordinal);
        Assert.Contains("const instant = ev?.detail === 0;", html, StringComparison.Ordinal);
        Assert.Contains("closeFsModal({ instant: true });", html, StringComparison.Ordinal);
        Assert.DoesNotContain("@keyframes sheet-in", html, StringComparison.Ordinal);
        Assert.Contains(".pv.enter{animation:statein", html, StringComparison.Ordinal);
        Assert.Contains("btn.textContent = '扫描中';", html, StringComparison.Ordinal);
        Assert.DoesNotContain("<script src=", html, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void 信息密集面板只保留有目的且GPU友好的动效()
    {
        string html = File.ReadAllText(FindRepositoryFile(
            Path.Combine("csharp", "src", "AiResume.Gui", "wwwroot", "index.html")));

        Assert.Contains("--ease-out:cubic-bezier(.23,1,.32,1)", html, StringComparison.Ordinal);
        Assert.Contains(".qc .fill{position:absolute;left:0;top:0;bottom:0;width:100%;transform:scaleX(0)", html,
            StringComparison.Ordinal);
        Assert.Contains("transition:transform .18s var(--ease-out)", html, StringComparison.Ordinal);
        Assert.Contains("style.transform = `scaleX(${hasPercent ? used : 1})`;", html, StringComparison.Ordinal);
        Assert.Contains("mark.style.transform = `translateX(${Math.round(elapsed * travel)}px)`;", html,
            StringComparison.Ordinal);
        Assert.Contains("@media (hover:hover) and (pointer:fine)", html, StringComparison.Ordinal);
        Assert.Contains("Math.min(i * 24, 120)", html, StringComparison.Ordinal);
        Assert.Contains(".qc .bar.unknown .fill::after", html, StringComparison.Ordinal);
        Assert.Contains("@keyframes quota-unknown{from{transform:translateX(-100%)}", html,
            StringComparison.Ordinal);
        Assert.Contains("animation:probe-ring 1.15s var(--ease-out) infinite", html,
            StringComparison.Ordinal);
        Assert.Contains("transform .12s var(--ease-out),box-shadow .12s var(--ease-out)", html,
            StringComparison.Ordinal);
        Assert.Contains("transition:transform .15s var(--ease-in-out)", html,
            StringComparison.Ordinal);
        Assert.Contains(".modal,.modal .sheet{transition-duration:.12s}", html,
            StringComparison.Ordinal);
        Assert.Contains(".modal .sheet{transform:none}", html, StringComparison.Ordinal);
        Assert.DoesNotContain("animation:drift", html, StringComparison.Ordinal);
        Assert.DoesNotContain("@keyframes chipbeat", html, StringComparison.Ordinal);
        Assert.DoesNotContain("@keyframes beat", html, StringComparison.Ordinal);
        Assert.DoesNotContain(".beacon.run .lamp", html, StringComparison.Ordinal);
        Assert.DoesNotContain("background-position:130%", html, StringComparison.Ordinal);
        Assert.DoesNotContain("transition:width", html, StringComparison.Ordinal);
        Assert.DoesNotContain("transition:left", html, StringComparison.Ordinal);
        // 缓动必须显式写出来。漏写会回落到 CSS 默认的 ease,和面板其它动效手感不一致 ——
        // 钉住"每一条过渡的每个时长后面都跟着缓动",比钉某一条具体声明更能挡住回归。
        foreach (System.Text.RegularExpressions.Match declaration in
                 System.Text.RegularExpressions.Regex.Matches(html, @"transition:([^;}]+)"))
        {
            string body = declaration.Groups[1].Value;
            if (body.Contains("none", StringComparison.Ordinal))
            {
                continue;
            }

            foreach (string segment in body.Split(','))
            {
                Assert.True(
                    System.Text.RegularExpressions.Regex.IsMatch(
                        segment, @"[0-9.]+s\s*(var\(--ease|cubic-bezier|linear|ease|steps)"),
                    $"过渡缺少显式缓动:{segment.Trim()}");
            }
        }

        // 常驻 will-change 只允许留在"一直在动"的元素上;偶发过渡挂着它等于永久多一个合成层。
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(html, "will-change"));
        Assert.Contains("animation:quota-unknown 2.4s linear infinite;will-change:transform", html,
            StringComparison.Ordinal);
        Assert.DoesNotContain(".bank .k.on{background:var(--sunk);color:var(--ink);transform", html,
            StringComparison.Ordinal);
    }

    [Fact]
    public void 安装态默认关闭DevTools且只能由显式环境变量开启()
    {
        Assert.False(AiResume.Gui.MainWindow.ShouldEnableDevTools(_ => null));
        Assert.False(AiResume.Gui.MainWindow.ShouldEnableDevTools(_ => "0"));
        Assert.False(AiResume.Gui.MainWindow.ShouldEnableDevTools(_ => "false"));
        Assert.True(AiResume.Gui.MainWindow.ShouldEnableDevTools(_ => "1"));
        Assert.True(AiResume.Gui.MainWindow.ShouldEnableDevTools(_ => "true"));
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
