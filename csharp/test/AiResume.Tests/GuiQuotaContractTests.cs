using Xunit;

namespace AiResume.Tests;

public sealed class GuiQuotaContractTests
{
    [Fact]
    public void 前端不得把无额度数据的成功RPC渲染成绿色正常()
    {
        string html = File.ReadAllText(FindRepositoryFile(
            Path.Combine("csharp", "src", "AiResume.Gui", "wwwroot", "index.html")));

        Assert.DoesNotContain("lastQuotaOk = true", html, StringComparison.Ordinal);
        Assert.Contains("detail: lastQuotaDetail || head.txt", html, StringComparison.Ordinal);
        Assert.Contains("const globalHasCurrentData = !!p.globalHasCurrentData;", html,
            StringComparison.Ordinal);
        Assert.Contains("const scopedInfos = (p.windows || [])", html,
            StringComparison.Ordinal);
        Assert.Contains("scopedRows = scopedInfos.map(info => info.row);", html, StringComparison.Ordinal);
        Assert.Contains("const scopedBlockedInfos = scopedInfos.filter(info => info.blocked);", html,
            StringComparison.Ordinal);
        Assert.Contains("ws.status === 'blocked'", html, StringComparison.Ordinal);
        Assert.Contains("scopedPercent == null ? '未报告'", html, StringComparison.Ordinal);
        Assert.Contains("const isBlockedWindow = window => !!window", html,
            StringComparison.Ordinal);
        Assert.Contains("const globalHasCarried = !!p.globalHasCarried;", html,
            StringComparison.Ordinal);
        Assert.Contains("const globalLimited = !!p.globalLimitReached;", html,
            StringComparison.Ordinal);
        Assert.Contains("lastQuotaState = globalLimited", html, StringComparison.Ordinal);
        Assert.Contains(": globalHasCurrentData ? true : false;", html, StringComparison.Ordinal);
        Assert.Contains("Claude Code 总额度未限流", html, StringComparison.Ordinal);
        Assert.Contains("Claude Code 总额度窗口未完整核实", html, StringComparison.Ordinal);
        Assert.DoesNotContain("const anyLimited = globalLimited || scopedLimited;", html,
            StringComparison.Ordinal);
        Assert.DoesNotContain("p.limitReached && !scopedLimited", html,
            StringComparison.Ordinal);
        Assert.DoesNotContain("const globalHasData =", html, StringComparison.Ordinal);
        Assert.Contains("if (ok === 'limited') return { cls: 'wait', txt: '已限流' };", html,
            StringComparison.Ordinal);
        Assert.Contains("if (ok === 'stale') return { cls: 'wait', txt: '最近读数' };", html,
            StringComparison.Ordinal);
        Assert.Contains("ws.carriedForward ? 'wait'", html, StringComparison.Ordinal);
        Assert.Contains(".concat(scopedRows)", html, StringComparison.Ordinal);
        Assert.Contains("const hasPercent = Number.isFinite(o.used);", html, StringComparison.Ordinal);
        Assert.Contains("el.classList.toggle('stale', !!o.carried);", html, StringComparison.Ordinal);
        Assert.DoesNotContain("classList.toggle('live'", html, StringComparison.Ordinal);
        Assert.Contains(".qc.stale:not(.blocked) .bar:not(.unknown) .fill", html,
            StringComparison.Ordinal);
        Assert.Contains(".qc.stale:not(.blocked) .bar.unknown .fill{background:var(--amber)",
            html, StringComparison.Ordinal);
        Assert.Contains("carried: !!w7.carriedForward", html, StringComparison.Ordinal);
        Assert.Contains("carried: !!w5.carriedForward", html, StringComparison.Ordinal);
        Assert.Contains("$('q5src').textContent = '本地会话统计';", html, StringComparison.Ordinal);
        Assert.Contains("$('q7src').textContent = '服务端未下发';", html, StringComparison.Ordinal);
        Assert.Contains("$('q7src').textContent = '探测失败';", html, StringComparison.Ordinal);
        Assert.Contains("bar.classList.toggle('unknown', hasWindow && !hasPercent);", html,
            StringComparison.Ordinal);
        Assert.Contains("bar.setAttribute('role', 'meter');", html, StringComparison.Ordinal);
        Assert.Contains("bar.setAttribute('role', 'status');", html, StringComparison.Ordinal);
        Assert.Contains("bar.setAttribute('aria-label', quotaName);", html, StringComparison.Ordinal);
        Assert.Contains("`${quotaName}：用量百分比未报告`", html,
            StringComparison.Ordinal);
        Assert.Contains("bar.removeAttribute('aria-valuenow');", html, StringComparison.Ordinal);
        Assert.Contains("$(id).querySelector('.fill').style.transform = `scaleX(${hasPercent ? used : 1})`;", html,
            StringComparison.Ordinal);
        Assert.Contains(".qc .bar.unknown .fill{transform:scaleX(1);box-shadow:none;", html,
            StringComparison.Ordinal);
        Assert.Contains("@keyframes quota-unknown", html, StringComparison.Ordinal);
        Assert.Contains(".qc .bar.unknown .fill{background:#0D2C1E}", html,
            StringComparison.Ordinal);
        Assert.Contains("const staleRefreshNextAt = new Map();", html, StringComparison.Ordinal);
        Assert.Contains("Math.max(60, refreshMinutes * 60)", html, StringComparison.Ordinal);
        Assert.Contains("while (staleRefreshNextAt.size > 32)", html, StringComparison.Ordinal);
        Assert.DoesNotContain("activeStaleKey", html, StringComparison.Ordinal);
        Assert.DoesNotContain("if (stale && !quotaBusy) refreshQuota(true, false);", html,
            StringComparison.Ordinal);
        Assert.DoesNotContain("(o.used ?? 0)", html, StringComparison.Ordinal);
        Assert.Contains("重置时间未报告", html, StringComparison.Ordinal);
        Assert.Contains("const blocked7 = isBlockedWindow(w7);", html, StringComparison.Ordinal);
        Assert.Contains("warn: false,", html, StringComparison.Ordinal);
        Assert.Contains("同一重置周期的最近一次服务端读数", html, StringComparison.Ordinal);
        Assert.Contains("if (p.storageWarning) lastQuotaDetail", html, StringComparison.Ordinal);
        Assert.Contains("$('refreshBtn').addEventListener('click', () => refreshQuota(true, true));", html,
            StringComparison.Ordinal);
        Assert.Contains("refreshQuota(false, false);", html, StringComparison.Ordinal);
        Assert.Contains("setInterval(() => refreshQuota(true, false)", html, StringComparison.Ordinal);
        Assert.DoesNotContain("deep 会跑 `codex exec`", html, StringComparison.Ordinal);
        Assert.Contains(".qc{position:relative;padding:5px;background:var(--panel-lo);display:flex;", html,
            StringComparison.Ordinal);
        Assert.Contains(".qc .scr{position:relative;overflow:hidden;background:var(--crt);flex:1;min-width:0;", html,
            StringComparison.Ordinal);
        Assert.Contains("id=\"agentBank\" role=\"radiogroup\"", html, StringComparison.Ordinal);
        Assert.Contains("tabindex=\"${o.id === p.current ? '0' : '-1'}\"", html, StringComparison.Ordinal);
        Assert.Contains("e.key === 'ArrowRight' || e.key === 'ArrowDown'", html, StringComparison.Ordinal);
        Assert.Contains("if (e.key === 'Home') nextIndex = 0;", html, StringComparison.Ordinal);
        Assert.Contains("if (restoreFocus) focusAgent(r.current);", html, StringComparison.Ordinal);

        string host = File.ReadAllText(FindRepositoryFile(
            Path.Combine("csharp", "src", "AiResume.Gui", "MainWindow.xaml.cs")));
        Assert.Contains("File.GetLastWriteTimeUtc(indexPath).Ticks", host, StringComparison.Ordinal);
        Assert.Contains("index.html?v={cacheVersion}", host, StringComparison.Ordinal);
        Assert.Contains("demoMode: _screenshotMode", host, StringComparison.Ordinal);
        Assert.Contains("exception_type={ExceptionType}", host, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "_logger.LogError(\n                exception,",
            host.Replace("\r\n", "\n", StringComparison.Ordinal),
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
