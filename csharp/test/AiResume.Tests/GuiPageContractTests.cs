using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using Xunit;

namespace AiResume.Tests;

/// <summary>
/// 控制面页面契约(v2.2 重排后)。两类断言：
/// <list type="bullet">
/// <item>静态纪律：不引外部脚本、[hidden] 不被作者样式盖掉、每条过渡写明缓动、常驻 will-change 只有一处、
///   减少动态时仍有反馈、点阵字前提(font-synthesis / font-smoothing)、宿主关闭浏览器加速键与缩放;</item>
/// <item>行为：把页面里的视图模型脚本(<c>script#vm</c>,纯函数)放进 Node 直接驱动，断言「无证据不绿」「承接读数琥珀」
///   「按模型满额不染红主窗口」「刷新失败不留旧绿」「隐藏的通知源仍启用必须提示」「每项目模型」等语义。</item>
/// </list>
/// 不对 DOM 文本逐字断言：排版会变，语义不能变。
/// </summary>
public sealed class GuiPageContractTests
{
    [Fact]
    public void 页面守住静态纪律()
    {
        string html = File.ReadAllText(RepositoryFile("csharp", "src", "AiResume.Gui", "wwwroot", "index.html"));

        Assert.DoesNotContain("<script src=", html, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("[hidden]{display:none !important}", html, StringComparison.Ordinal);
        Assert.Contains("font-synthesis:none", html, StringComparison.Ordinal);
        Assert.Contains("-webkit-font-smoothing:none", html, StringComparison.Ordinal);
        Assert.Contains("@media (prefers-reduced-motion:reduce)", html, StringComparison.Ordinal);
        Assert.Contains("animation-name:low-motion-fade", html, StringComparison.Ordinal);
        Assert.Contains("<script id=\"vm\">", html, StringComparison.Ordinal);

        // 缓动必须显式写出来：漏写会回落到 CSS 默认的 ease,和面板其它动效手感不一致。
        foreach (Match declaration in Regex.Matches(html, @"transition:([^;}]+)"))
        {
            string body = declaration.Groups[1].Value;
            if (body.Contains("none", StringComparison.Ordinal))
            {
                continue;
            }

            foreach (string segment in body.Split(','))
            {
                Assert.True(Regex.IsMatch(segment, @"[0-9.]+s\s*(var\(--ease|cubic-bezier|linear|ease|steps)"),
                    $"过渡缺少显式缓动:{segment.Trim()}");
            }
        }

        // 常驻 will-change 只允许留在「一直在动」的未知百分比扫描条上。
        Assert.Single(Regex.Matches(html, "will-change"));
        Assert.Contains("animation:quota-unknown 2.4s linear infinite;will-change:transform", html, StringComparison.Ordinal);

        // 百分比已知才报 meter 的 aria-valuenow;未知时是 status,不冒充 0% 或 100%。
        Assert.Contains("role=\"meter\" aria-label=", html, StringComparison.Ordinal);
        Assert.Contains("role=\"status\" aria-label=", html, StringComparison.Ordinal);
        // 供应商目录刷新失败时旧条目一律降级为未核实，不保留旧绿。
        Assert.Contains("modelsVerified: false, quotaKnown: false", html, StringComparison.Ordinal);
        Assert.Contains("balance: null", html, StringComparison.Ordinal);
        // 布防请求逐项目带模型;刷新只读元数据，不调用推理。
        Assert.Contains("AR.armRequest(pickedRows(), providerId, continuousNext())", html, StringComparison.Ordinal);
        Assert.Contains("call('arm.set', request", html, StringComparison.Ordinal);
        // 布防请求带上本窗口看到的周期,另一个窗口刚布防过时后端拒绝而不是静默替换。
        Assert.Contains("const beforeCycle = (arm && arm.cycleId) || '';", html, StringComparison.Ordinal);
        Assert.Contains("expectCycleId: beforeCycle", html, StringComparison.Ordinal);
        // 对账时「已布防」必须是本次请求建立的周期:周期号变了、队列正是这次提交的项目(另一个窗口的布防不算这边成功)。
        Assert.Contains("arm.cycleId !== beforeCycle && sameQueue()", html, StringComparison.Ordinal);
        // 请求超时只能说「结果未确认」,不能断言没有成功。
        Assert.Contains("{ timeout: true }", html, StringComparison.Ordinal);
        Assert.Contains("结果未确认", html, StringComparison.Ordinal);
        // 30 秒轮询读回同一份状态时不整块重建(下拉不被收起、焦点不丢)。
        Assert.Contains("if (sig === lastArmSig) { syncArm(); return; }", html, StringComparison.Ordinal);
        // 飞书设置对话框里没有 submit 按钮:Enter 不会变成「关闭」把刚填的凭据清掉。
        Assert.DoesNotContain("<button class=\"btn\" value=\"close\">关闭</button>\n    </div>\n    <div id=\"fsOut\"", html, StringComparison.Ordinal);
        Assert.Contains("id=\"fsClose\"", html, StringComparison.Ordinal);
        Assert.Contains("hooks.json 并登记信任", html, StringComparison.Ordinal);
        Assert.DoesNotContain("claude.token", html, StringComparison.Ordinal);
        Assert.DoesNotContain("feishu.status", html, StringComparison.Ordinal);
        Assert.DoesNotContain("agent.get", html, StringComparison.Ordinal);
    }

    [Fact]
    public void 宿主关闭浏览器加速键与缩放_换缓存键_截图只用合成数据()
    {
        string host = File.ReadAllText(RepositoryFile("csharp", "src", "AiResume.Gui", "MainWindow.xaml.cs"));

        Assert.Contains("core.Settings.AreBrowserAcceleratorKeysEnabled = false;", host, StringComparison.Ordinal);
        Assert.Contains("core.Settings.IsZoomControlEnabled = false;", host, StringComparison.Ordinal);
        Assert.Contains("File.GetLastWriteTimeUtc(indexPath).Ticks", host, StringComparison.Ordinal);
        Assert.Contains("index.html?v={cacheVersion}", host, StringComparison.Ordinal);
        Assert.Contains("demoMode: _screenshotMode", host, StringComparison.Ordinal);
        Assert.Contains("exception_type={ExceptionType}", host, StringComparison.Ordinal);
        string xaml = File.ReadAllText(RepositoryFile("csharp", "src", "AiResume.Gui", "MainWindow.xaml"));
        Assert.Contains("DefaultBackgroundColor=\"#DEDACB\"", xaml, StringComparison.Ordinal);
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
    public void 额度磁贴无证据不绿_承接读数琥珀_按模型满额不染红主窗口()
        => RunViewModel("""
            const now = 1800000000;
            const w = (name, usedPercent, extra = {}) => ({ name, status: usedPercent >= 100 ? 'blocked' : 'allowed', usedPercent, resetAtUnix: now + 7200, ...extra });
            let t = AR.quotaTiles({ hasData: true, windows: [w('five_hour', 20), w('seven_day', 50), w('weekly_scoped:Fable', 100)] }, [], null, {}, now);
            assert.equal(t[0].state, 'ok'); assert.equal(t[1].state, 'ok');
            assert.equal(t[2].state, 'limit'); assert.equal(t[2].title, 'Claude · Fable'); assert.equal(t[2].unit, '已用尽');

            t = AR.quotaTiles({ hasData: true, windows: [w('five_hour', null), w('seven_day', 40, { carriedForward: true })] }, [], null, {}, now);
            assert.equal(t[0].state, 'unknown', '没有百分比就不能是绿色');
            assert.equal(t[0].percent, null);
            assert.equal(t[1].state, 'stale'); assert.equal(t[1].badge, '最近读数');
            assert.equal(t[2].state, 'empty'); assert.match(t[2].foot, /未返回按模型额度/);

            t = AR.quotaTiles({ hasData: true, windows: [w('five_hour', 12, { status: 'blocked' })] }, [], null, {}, now);
            assert.equal(t[0].state, 'limit', '明确 blocked 即使不到 100% 也是受限');

            t = AR.quotaTiles(null, null, null, {}, now);
            assert.ok(t.every(x => x.state === 'loading'));
            t = AR.quotaTiles({ error: '网络不可达' }, [], null, {}, now);
            assert.equal(t[0].state, 'empty'); assert.match(t[0].foot, /读取失败/);

            const armed = { armed: true, projects: [{ reason: 'quota:limited', detail: 'weekly_scoped:Fable:blocked' }, { reason: 'session-in-use' }] };
            t = AR.quotaTiles({ hasData: true, windows: [w('five_hour', 20), w('seven_day', 50), w('weekly_scoped:Fable', 100)] }, [], armed, {}, now);
            assert.equal(t[2].badge, '1 项在等');
            // 服务端给小写模型名时标题仍按模型名写法首字母大写，角标匹配不分大小写。
            t = AR.quotaTiles({ hasData: true, windows: [w('weekly_scoped:fable', 100)] }, [], armed, {}, now);
            assert.equal(t[2].title, 'Claude · Fable'); assert.equal(t[2].badge, '1 项在等');
            // 按模型取已用最高或已满的一条，其余进悬停说明。
            t = AR.quotaTiles({ hasData: true, windows: [w('weekly_scoped:Opus', 30), w('weekly_scoped:Fable', 80)] }, [], null, {}, now);
            assert.equal(t[2].title, 'Claude · Fable'); assert.match(t[2].more, /Opus 30%/);
            // 本次读取失败、显示的是承接读数:失败原因(令牌被拒、限流…)必须出现在磁贴说明里,不能只剩「最近读数」。
            t = AR.quotaTiles({ hasData: true, unavailableReason: 'Claude Code 登录被服务端拒绝', windows: [w('five_hour', 20, { carriedForward: true }), w('seven_day', 50, { carriedForward: true })] }, [], null, {}, now);
            assert.equal(t[0].state, 'stale'); assert.match(t[0].more, /本次读取失败:Claude Code 登录被服务端拒绝/);
            assert.match(t[2].more, /本次读取失败/);
            """);

    [Fact]
    public void DeepSeek余额只有真实证据才绿_刷新失败降级后不留旧绿_Codex按偏好显示()
        => RunViewModel("""
            const now = 1800000000;
            const ok = [{ id: 'deepseek', quotaKnown: true, balance: 11.27, currency: 'CNY', credentialSource: 'opencode-auth' }];
            let t = AR.quotaTiles({ hasData: true, windows: [] }, ok, null, {}, now);
            assert.equal(t[3].state, 'ok'); assert.equal(t[3].big, '¥11.27'); assert.equal(t[3].foot, '密钥来自 OpenCode');
            const degraded = [{ id: 'deepseek', quotaKnown: false, balance: null, problem: '数据刷新失败,请重试', modelsVerified: false }];
            t = AR.quotaTiles({ hasData: true, windows: [] }, degraded, null, {}, now);
            assert.notEqual(t[3].state, 'ok'); assert.equal(t[3].big, '未核实');
            t = AR.quotaTiles({ hasData: true, windows: [] }, [{ id: 'deepseek', quotaKnown: true, balance: 0, currency: 'CNY', limited: true }], null, {}, now);
            assert.equal(t[3].state, 'limit');
            t = AR.quotaTiles({ hasData: true, windows: [] }, [], null, {}, now);
            assert.equal(t[3].big, '未接入');
            const codex = [{ id: 'codex-official', windows: [{ name: 'primary · 7D', usedPercent: 12, windowSeconds: 604800, status: 'allowed' }] }];
            assert.equal(AR.quotaTiles({ hasData: true, windows: [] }, codex, null, {}, now).length, 4);
            t = AR.quotaTiles({ hasData: true, windows: [] }, codex, null, { showCodex: true }, now);
            assert.equal(t.length, 5); assert.equal(t[4].title, 'Codex · 7 天'); assert.equal(t[4].state, 'ok');
            // 主/次窗口未满但附加限额或消费上限命中:供应商判定受限,磁贴不能是绿的。
            t = AR.quotaTiles({ hasData: true, windows: [] }, [{ ...codex[0], limited: true }], null, { showCodex: true }, now);
            assert.equal(t[4].state, 'limit');
            // 目录请求本身失败(从没读成功过):不能说「未接入」或「未登录」。
            t = AR.quotaTiles({ hasData: true, windows: [] }, null, null, { showCodex: true }, now, '请求超时');
            assert.equal(t[3].big, '未核实'); assert.match(t[3].foot, /读取失败/); assert.match(t[4].foot, /读取失败/);
            // 服务端报告已达上限却没指明窗口:磁贴要说出来,不能只显示几个未满的窗口。
            t = AR.quotaTiles({ hasData: true, unattributedLimitReached: true, windows: [{ name: 'five_hour', usedPercent: 40, status: 'allowed' }] }, [], null, {}, now);
            assert.equal(t[2].state, 'limit'); assert.equal(t[2].unit, '窗口不明');
            """);

    [Fact]
    public void Claude登录到期琥珀_过期与未登录标红_账号未核实不绿()
        => RunViewModel("""
            const now = 1800000000;
            let m = AR.loginModel({ verdict: 'Ok', accessTokenExpiresAtUnix: now + 3600, loginExpiresAtUnix: now + 20 * 86400, accountVerified: true }, now);
            assert.equal(m.led, 'ok'); assert.equal(m.head, '有效');
            m = AR.loginModel({ verdict: 'Ok', loginExpiresAtUnix: now + 20 * 86400, accountVerified: false }, now);
            assert.equal(m.led, 'wait');
            m = AR.loginModel({ verdict: 'LoginExpiring', loginExpiresAtUnix: now + 86400 }, now);
            assert.equal(m.led, 'wait'); assert.ok(m.warnBeacon); assert.match(m.note, /\/login/);
            for (const verdict of ['LoginExpired', 'LoggedOut', 'StoreMoved']) {
              m = AR.loginModel({ verdict }, now);
              assert.equal(m.led, 'bad', verdict); assert.ok(m.badBeacon, verdict);
            }
            m = AR.loginModel({ verdict: 'TokenExpired' }, now);
            assert.equal(m.led, 'wait');
            assert.equal(AR.loginModel({ error: 'x' }, now).head, '未核实');
            assert.equal(AR.loginModel(null, now).head, '读取中…');
            // 登录文件在却读不出(多半正被改写):未核实，不能说「未登录」,也不能把状态灯染红。
            m = AR.loginModel({ verdict: 'Unverified', problem: 'Claude Code 登录文件无法读取' }, now);
            assert.equal(m.head, '未核实'); assert.equal(m.led, ''); assert.ok(!m.badBeacon);
            """);

    [Fact]
    public void 通知行可证伪_隐藏的通知源仍启用必须在主界面提示_机器人行只认真实投递()
        => RunViewModel("""
            const now = 1800000000;
            const list = { items: [
              { kind: 'ClaudeCode', displayName: 'Claude Code', isInstalled: true, isEnabled: true, hookBroken: false, detail: '已安装' },
              { kind: 'OpenCode', displayName: 'OpenCode', isInstalled: true, isEnabled: true, hookBroken: false, detail: '已写入插件,等 OpenCode 下次加载后核实' },
              { kind: 'Codex', displayName: 'Codex', isInstalled: true, isEnabled: true, hookBroken: false, detail: '' },
              { kind: 'Cline', displayName: 'Cline', isInstalled: false, isEnabled: false, hookBroken: false, detail: '' },
            ] };
            let m = AR.notifyModel(list, null, {}, now);
            assert.deepEqual(m.rows.map(r => r.kind), ['ClaudeCode', 'OpenCode']);
            assert.equal(m.rows[0].led, 'ok'); assert.equal(m.rows[0].text, 'hook 正常'); assert.equal(m.rows[1].text, '待核实');
            assert.match(m.extraHint, /Codex/); assert.match(m.extraHint, /仍开启/);
            m = AR.notifyModel(list, null, { showExtraNotify: true }, now);
            assert.equal(m.extraHint, ''); assert.equal(m.rows.length, 4);
            const broken = { items: [{ kind: 'Qoder', displayName: 'Qoder', isInstalled: true, isEnabled: true, hookBroken: true }] };
            assert.match(AR.notifyModel(broken, null, {}, now).extraHint, /Qoder 的通知 hook 失效/);
            // OpenCode 是插件:「已加载」只来自它自己的日志(detail 不含「核实」);失效同样按插件说。
            const plugin = (extra) => AR.notifyModel({ items: [{ kind: 'OpenCode', displayName: 'OpenCode', isInstalled: true, isEnabled: true, hookBroken: false, detail: '已安装,OpenCode 已加载(日志 11-02 10:00)', ...extra }] }, null, {}, now).rows[0];
            assert.equal(plugin().text, '插件已加载'); assert.equal(plugin().led, 'ok');
            assert.equal(plugin({ hookBroken: true, detail: 'OpenCode 拒绝加载 AI Resume 通知插件' }).text, '插件失效');
            const claudeBroken = AR.notifyModel({ items: [{ kind: 'ClaudeCode', displayName: 'Claude Code', isInstalled: true, isEnabled: true, hookBroken: true, detail: 'x' }] }, null, {}, now).rows[0];
            assert.equal(claudeBroken.text, 'hook 失效'); assert.equal(claudeBroken.led, 'bad');
            // 探测本身失败(配置正被改写、被占用):不能说「已关闭」或「未检测到」,开关保持可点。
            const unreadable = { items: [{ kind: 'ClaudeCode', displayName: 'Claude Code', isInstalled: true, isEnabled: false, hookBroken: false, unverified: true, detail: '配置读取失败: busy' }] };
            const u = AR.notifyModel(unreadable, null, {}, now).rows[0];
            assert.equal(u.text, '未核实'); assert.ok(u.installed); assert.match(u.title, /配置读取失败/);
            assert.equal(u.on, null, '开没开不知道:开关既不画开也不画关');
            // 收进菜单的通知源读不出配置:主界面同样要留一句。
            const hiddenUnknown = { items: [{ kind: 'Codex', displayName: 'Codex', isInstalled: false, isEnabled: false, unverified: true, detail: 'busy' }] };
            assert.match(AR.notifyModel(hiddenUnknown, null, {}, now).extraHint, /Codex 通知源状态未核实/);

            const base = { larkCliFound: true, recipientSet: true, recipientMasked: 'ou_0123…cdef', appIdMasked: 'cli_a1…7f', credentialSource: 'env' };
            const healthy = { ...base, health: { state: 'healthy', lastSuccessAtUnix: now - 60 } };
            m = AR.notifyModel(list, { ...healthy, settingsVerified: true }, {}, now, true);
            assert.equal(m.robot.text, '可送达'); assert.equal(m.robot.led, 'ok');
            assert.equal(m.note, '发给 ou_0123…cdef'); assert.match(m.robot.title, /应用 cli_a1…7f/);
            // 通知由后台 Worker 投递:它不在时历史上的成功记录也不能让这一行显示「可送达」。
            const down = AR.notifyModel(list, healthy, {}, now, false).robot;
            assert.equal(down.led, 'bad'); assert.equal(down.text, '后台服务未运行');
            assert.equal(AR.notifyModel(list, healthy, {}, now, null).robot.text, '未核实', '核验不出 Worker 是否在跑就不能说可送达');
            assert.notEqual(AR.notifyModel(list, healthy, {}, now, undefined).robot.text, '可送达', 'arm.get 还没读到(或读失败)时同样不能说可送达');
            assert.notEqual(AR.notifyModel(list, healthy, {}, now).robot.led, 'ok');
            // 成功指纹比不出来(settingsVerified 为 null):核对不上就不能说可送达。
            assert.equal(AR.notifyModel(list, { ...healthy, settingsVerified: null }, {}, now, true).robot.text, '待核实');
            // 最近一次投递失败(条目级)或成功之后改过设置:历史成功不再让这一行显示绿色。
            const failedLast = AR.notifyModel(list, { ...healthy, health: { ...healthy.health, lastAttemptOk: false, lastErrorClass: 'permission_or_recipient' } }, {}, now, true).robot;
            assert.equal(failedLast.led, 'wait'); assert.equal(failedLast.text, '最近投递失败');
            const changed = AR.notifyModel(list, { ...healthy, settingsVerified: false }, {}, now, true).robot;
            assert.equal(changed.text, '设置已变更');
            assert.equal(AR.notifyModel(list, { ...healthy, settingsVerified: true }, {}, now, true).robot.text, '可送达');
            // 读不到通知源状态:主通知源留「未核实」占位行,开关禁用,不能整块消失。
            m = AR.notifyModel({ items: [], error: '请求超时' }, healthy, {}, now, true);
            assert.deepEqual(m.rows.map(r => r.text), ['未核实', '未核实']); assert.ok(m.rows.every(r => !r.installed));
            const blocked = AR.notifyModel(list, { ...base, health: { state: 'blocked', reason: 'keychain_unavailable', lastErrorCode: 1 } }, {}, now).robot;
            assert.equal(blocked.led, 'bad'); assert.equal(blocked.text, AR.DELIVERY_REASON.keychain_unavailable);
            // 换令牌路径(ADR-0007)的熔断原因都要有自己的文案与说明，不能落成笼统的「投递受阻」。
            for (const reason of ['credential_unreadable', 'token_unexpected', 'cli_contract', 'auth_config', 'network', 'network_timeout', 'rate_limit']) {
                const r = AR.notifyModel(list, { ...base, health: { state: 'blocked', reason } }, {}, now).robot;
                assert.ok(AR.DELIVERY_REASON[reason], reason); assert.equal(r.text, AR.DELIVERY_REASON[reason]);
                assert.ok(AR.DELIVERY_DETAIL[reason], reason); assert.ok(r.title.includes(AR.DELIVERY_DETAIL[reason]), reason);
            }
            // 换令牌这一步失败与 lark-cli 失败同名类别:悬停标出是哪一段、细类是什么。
            const tokenBlocked = AR.notifyModel(list, { ...base, health: { state: 'blocked', reason: 'network', lastErrorType: 'tenant_token', lastErrorSubtype: 'server_error:http_502@direct' } }, {}, now).robot;
            assert.match(tokenBlocked.title, /换取令牌时失败/); assert.match(tokenBlocked.title, /server_error:http_502@direct/);
            assert.doesNotMatch(AR.notifyModel(list, { ...base, health: { state: 'blocked', reason: 'network', lastErrorType: 'network' } }, {}, now).robot.title, /换取令牌时/);
            // 副本在却读不出:投递会拒发、不改用钥匙串——显红，压过历史成功;也不能说成「lark-cli 默认应用」。
            const copyUnreadable = AR.notifyModel(list, { ...healthy, appIdMasked: null, credentialUnreadable: true, settingsVerified: true }, {}, now, true);
            assert.equal(copyUnreadable.robot.led, 'bad'); assert.equal(copyUnreadable.robot.text, AR.DELIVERY_REASON.credential_unreadable);
            assert.equal(copyUnreadable.app, ''); assert.doesNotMatch(copyUnreadable.robot.title, /默认应用/);
            assert.equal(AR.notifyModel(list, { ...base, appIdMasked: null, credentialSource: 'keychain', health: null }, {}, now, true).app, 'lark-cli 默认应用');
            assert.equal(AR.notifyModel(list, { ...base, health: null }, {}, now, true).robot.text, '待核实');
            const keychainOnly = AR.notifyModel(list, { ...base, credentialSource: 'keychain', health: null }, {}, now, true).robot;
            assert.equal(keychainOnly.led, 'wait'); assert.equal(keychainOnly.text, '未存凭据副本'); assert.match(keychainOnly.title, /后台服务/);
            assert.equal(AR.notifyModel(list, { ...base, health: null }, {}, now).robot.text, '核验中…');
            assert.equal(AR.notifyModel(list, { ...base, recipientSet: false }, {}, now).robot.led, 'bad');
            assert.equal(AR.notifyModel(list, { ...base, larkCliFound: false }, {}, now).robot.led, 'bad');
            assert.match(AR.notifyModel(list, { ...base, health: { state: 'healthy', lastSuccessAtUnix: now, backlog: 2, dead: 1 } }, {}, now).note, /待发 2\s· 未送达 1/);

            assert.equal(AR.workerModel({ workerRunning: true }).cls, 'ok');
            assert.equal(AR.workerModel({ workerRunning: false }).cls, 'bad');
            assert.equal(AR.workerModel({ workerRunning: null }).text, '后台服务未核实');
            assert.equal(AR.workerModel(null).text, '');
            """);

    [Fact]
    public async Task 截图演示态的飞书机器人行显示可送达()
    {
        // 首页截图拍的就是演示态:把合成应答原样交给视图模型，与真实窗口走同一条判定(worker 取 arm.get 的 workerRunning)。
        var bridge = new AiResume.Gui.ControlPlaneBridge(demoMode: true);
        string list = await DemoPayloadJson(bridge, "notifications.list");
        string channel = await DemoPayloadJson(bridge, "notify.channel");
        string arm = await DemoPayloadJson(bridge, "arm.get");

        RunViewModel($$"""
            const now = Math.floor(Date.now() / 1000);
            const robot = AR.notifyModel({{list}}, {{channel}}, {}, now, ({{arm}}).workerRunning).robot;
            assert.equal(robot.text, '可送达'); assert.equal(robot.led, 'ok');
            """);
    }

    [Fact]
    public void 队列每项目模型_沿用会话或手选_读不出就不能布防_布防后固定()
        => RunViewModel("""
            const now = 1800000000;
            const models = [{ id: 'claude-opus-5-5', name: 'Claude Opus 5.5' }, { id: 'claude-fable-5-1', name: 'Claude Fable 5.1' }];
            const base = { now, armed: false, models, catalogVerified: true, chosen: '' };
            const item = { name: 'ledger-api', path: 'C:\\demo\\ledger-api', lastUsed: '8/7 23:05' };
            let r = AR.rowModel(item, { ...base, preview: { sessionModel: 'claude-opus-5-5', evidence: 'interrupted', interruptedAtUnix: now - 600, session: 'idle' } });
            assert.equal(r.options[0].label, '沿用会话:Opus 5.5'); assert.ok(r.armable); assert.equal(r.effectiveModel, 'claude-opus-5-5');
            assert.equal(r.status.cls, 'wait'); assert.match(r.status.text, /被限额打断/);

            r = AR.rowModel(item, { ...base, preview: { sessionModel: 'claude-retired-4', evidence: 'clear', session: 'idle' } });
            assert.ok(r.needsChoice); assert.ok(!r.armable);
            r = AR.rowModel(item, { ...base, chosen: 'claude-fable-5-1', preview: { sessionModel: 'claude-retired-4', evidence: 'clear', session: 'idle' } });
            assert.ok(r.armable); assert.equal(r.effectiveModel, 'claude-fable-5-1');

            r = AR.rowModel(item, { ...base, preview: { problem: '没有可核实的 claudecode 会话', evidence: 'none', session: 'n/a' } });
            assert.ok(!r.armable); assert.equal(r.status.cls, 'bad');
            r = AR.rowModel(item, { ...base, catalogVerified: false, preview: { sessionModel: 'claude-opus-5-5', evidence: 'clear', session: 'idle' } });
            assert.ok(!r.armable);
            // 目录没读出来(令牌过期、限流)时 models 是空的:手选不能被判成「已下线」,也不能丢,显示成待核实占位项。
            r = AR.rowModel(item, { ...base, models: [], catalogVerified: false, chosen: 'claude-fable-5-1', preview: { sessionModel: 'claude-opus-5-5', evidence: 'clear', session: 'idle' } });
            assert.equal(r.value, 'claude-fable-5-1'); assert.ok(!r.armable);
            assert.ok(!r.options.some(o => o.value === '__gone'));
            assert.ok(r.options.some(o => o.value === 'claude-fable-5-1' && /待核实/.test(o.label)));
            assert.equal(r.status.text, '模型目录未核实');
            r = AR.rowModel(item, { ...base });
            assert.ok(!r.armable, '会话事实还没读到就不能布防');
            r = AR.rowModel(item, { ...base, picked: false, preview: { sessionModel: 'claude-opus-5-5', evidence: 'clear', session: 'idle' } });
            assert.equal(r.mode, 'idle', '没勾选的行不出模型下拉'); assert.ok(!r.armable); assert.match(r.status.text, /最近会话/);
            r = AR.rowModel(item, { ...base, preview: { sessionModel: 'claude-opus-5-5', evidence: 'interrupted', interruptedAtUnix: now - 600, session: 'in-use' } });
            assert.equal(r.status.text, '被打断 · 会话开着'); assert.match(r.status.title, /关闭后才会自动续跑/);

            r = AR.rowModel(item, { ...base, armed: true, armProject: { modelId: 'claude-fable-5-1', modelName: 'Claude Fable 5.1', modelSource: 'session',
              category: 'waiting', reasonText: '等额度', hint: 'Fable 周额度已用尽', resetAtUnix: now + 3600 } });
            assert.equal(r.mode, 'fixed'); assert.equal(r.modelLabel, 'Fable 5.1'); assert.match(r.modelTitle, /^模型 ID claude-fable-5-1 · 跟随会话 · 本轮固定$/);
            assert.match(r.status.text, /^等额度 · \d/); assert.equal(r.status.cls, 'wait');
            assert.match(r.status.title, /Fable 周额度已用尽;预计 .* 起续跑/);
            r = AR.rowModel(item, { ...base, armed: true, armProject: { modelId: 'claude-fable-5-1', category: 'waiting', reasonText: '等额度', resetAtUnix: now - 60 } });
            assert.equal(r.status.text, '等额度', '已过的重置时间不再当预计时间显示');
            // 手选的模型已不在实时目录:当作没选,要求重选,不得提交旧 ID。
            r = AR.rowModel(item, { ...base, chosen: 'claude-retired-4', preview: { sessionModel: 'claude-opus-5-5', evidence: 'clear', session: 'idle' } });
            assert.ok(r.needsChoice); assert.equal(r.value, '__gone'); assert.equal(r.options[0].value, '__gone'); assert.match(r.status.text, /已下线/);
            assert.ok(!r.armable, '手选的模型下线了必须重选,不能悄悄改用会话模型布防'); assert.equal(r.effectiveModel, '');
            assert.equal(AR.rowModel(item, { ...base, armed: true, armProject: null }).mode, 'none');
            // 在布防队列里却没有逐项目数据(状态库读不出):不能肯定地说「不在本轮布防」。
            r = AR.rowModel(item, { ...base, armed: true, armProject: null, inCycle: true });
            assert.equal(r.status.text, '状态未核实'); assert.equal(r.status.cls, 'wait');
            assert.equal(AR.rowModel(item, { ...base, armed: true, armProject: null, inCycle: false }).status.text, '不在本轮布防');

            const req = AR.armRequest([
              { path: 'C:\\a', chosen: '', effectiveModel: 'claude-opus-5-5' },
              { path: 'C:\\b', chosen: 'claude-fable-5-1', effectiveModel: 'claude-fable-5-1' },
            ], 'claude-subscription', true);
            assert.equal(req.projectModels['C:\\a'], ''); assert.equal(req.projectModels['C:\\b'], 'claude-fable-5-1');
            assert.equal(req.resumeModel, 'claude-opus-5-5'); assert.ok(req.continuous); assert.deepEqual(req.paths, ['C:\\a', 'C:\\b']);
            assert.equal(AR.pickSummary([{ effectiveModel: 'claude-opus-5-5' }, { effectiveModel: 'claude-opus-5-5' }, { effectiveModel: 'claude-fable-5-1' }], models),
              '已选 3 个 · Opus 5.5 ×2 · Fable 5.1 ×1');

            // 供应商下拉:Codex 跟随「显示 Codex」偏好;已布防时只剩布防目标。
            const catalog = [{ id: 'claude-subscription', agent: 'claudecode', resumable: true }, { id: 'codex-official', agent: 'codex', resumable: true },
              { id: 'deepseek', agent: 'opencode', resumable: false }];
            assert.deepEqual(AR.providerOptions(catalog, {}, null).map(p => p.id), ['claude-subscription']);
            assert.deepEqual(AR.providerOptions(catalog, { showCodex: true }, null).map(p => p.id), ['claude-subscription', 'codex-official']);
            assert.deepEqual(AR.providerOptions(catalog, {}, { armed: true, resumeTarget: { providerId: 'codex-official' } }).map(p => p.id), ['codex-official']);
            """);

    [Fact]
    public void 状态灯_引擎与进程门禁压过一切_登录过期标红_等待原因逐项目说清()
        => RunViewModel("""
            const now = 1800000000;
            const target = { providerId: 'claude-subscription', agent: 'claudecode', modelId: 'claude-opus-5-5' };
            const armed = (extra) => ({ armed: true, selected: ['C:\\a', 'C:\\b'], resumeTarget: target, phase: 'waiting', projects: [], ...extra });
            assert.equal(AR.beaconModel(null, null, now).head, '正在读取状态');
            assert.equal(AR.beaconModel(armed({ engine: 'NotRunning' }), null, now).cls, 'bad');
            assert.equal(AR.beaconModel(armed({ engine: 'CancelPending' }), null, now).cls, 'bad');
            assert.equal(AR.beaconModel(armed({ engine: 'RunActive' }), null, now).cls, 'wait');
            assert.equal(AR.beaconModel(armed({ engine: 'StateUnverified' }), null, now).head, '布防状态未核实');
            assert.equal(AR.beaconModel({ armed: false }, null, now).head, '未布防');
            assert.equal(AR.beaconModel(armed({ resumeTarget: null }), null, now).cls, 'bad');

            const expired = AR.loginModel({ verdict: 'LoginExpired' }, now);
            assert.equal(AR.beaconModel(armed({ engine: 'Healthy' }), expired, now).cls, 'bad');
            // Codex 周期的额度来自 ChatGPT 账号:Claude 没登录不能把它的状态灯盖成「Claude 登录已过期」。
            const codexTarget = { providerId: 'codex-official', agent: 'codex', modelId: 'gpt-5.5' };
            assert.notEqual(AR.beaconModel(armed({ engine: 'Healthy', resumeTarget: codexTarget }), expired, now).head.indexOf('Claude 登录'), 0);
            // 服务端拒绝过本机令牌:本地时间戳再新也不能是绿色「有效」。
            const rejected = AR.loginModel({ verdict: 'Ok', serverRejected: true, accountVerified: true }, now);
            assert.equal(rejected.led, 'bad');
            assert.equal(rejected.head, '被服务端拒绝');
            assert.equal(AR.beaconModel(armed({ engine: 'Healthy' }), rejected, now).cls, 'bad');

            let b = AR.beaconModel(armed({ engine: 'Healthy', projects: [
              { name: 'a', status: 'limited', reason: 'quota:limited', reasonText: '等额度', resetAtUnix: now + 7200 },
              { name: 'b', status: 'limited', reason: 'quota:limited', reasonText: '等额度', resetAtUnix: now + 3600 }] }), null, now);
            assert.equal(b.cls, 'wait'); assert.equal(b.head, '等待额度恢复'); assert.match(b.sub, /最早/);
            b = AR.beaconModel(armed({ engine: 'Healthy', projects: [
              { name: 'a', status: 'limited', reason: 'quota:limited', reasonText: '等额度' },
              { name: 'b', status: 'limited', reason: 'session-in-use', reasonText: '会话开着 · 关闭后续跑' }] }), null, now);
            assert.equal(b.head, '等待续跑'); assert.equal(b.sub, 'b 会话开着 · 关闭后续跑 · 共 2 个在等');
            const expiring = AR.loginModel({ verdict: 'LoginExpiring', loginExpiresAtUnix: now + 86400 }, now);
            b = AR.beaconModel(armed({ engine: 'Healthy', projects: [{ name: 'a', status: '', category: 'idle' }] }), expiring, now);
            assert.equal(b.cls, 'wait'); assert.match(b.sub, /^Claude 登录 .+ 到期$/);
            b = AR.beaconModel(armed({ engine: 'Healthy', phase: 'resuming', projects: [{ name: 'a', status: 'running', modelId: 'claude-opus-5-5' }] }), null, now);
            assert.equal(b.cls, 'on'); assert.equal(b.head, '正在续跑'); assert.match(b.sub, /Opus 5.5/);
            b = AR.beaconModel(armed({ engine: 'Healthy', projects: [{ name: 'a', status: 'exit-null', category: 'failure' }] }), null, now);
            assert.equal(b.cls, 'bad'); assert.match(b.head, /未确认完成/);
            b = AR.beaconModel(armed({ engine: 'Healthy', projects: [{ name: 'a', status: '', category: 'idle' }] }), null, now);
            assert.equal(b.head, '监视中');
            // 未启动(找不到程序或目录,下一轮自动重试)不是「不会再自动续跑」的执行后失败。
            b = AR.beaconModel(armed({ engine: 'Alive', projects: [{ name: 'a', status: 'limited', category: 'failure', reason: 'not-started:no-claude', reasonText: '找不到程序或目录' }] }), null, now);
            assert.equal(b.cls, 'wait'); assert.match(b.sub, /找不到程序或目录/);
            b = AR.beaconModel(armed({ engine: 'Alive', projects: [{ name: 'a', status: 'limited', category: 'failure', reason: 'quota:provider-changed', reasonText: '账号已变化 · 重新布防' }] }), null, now);
            assert.equal(b.cls, 'bad'); assert.equal(b.head, '需要重新布防');
            // 还在核对 / 会话记录未核实:后台服务不会放行,不得说「尚未被限额打断」。
            b = AR.beaconModel(armed({ engine: 'Alive', projects: [{ name: 'a', status: null, category: 'waiting', reason: 'evidence-unverified', reasonText: '会话记录未核实' }] }), null, now);
            assert.equal(b.cls, 'wait'); assert.equal(b.head, '等待核对');
            // 后台服务还没接手新一轮:不能说「监视中」。
            b = AR.beaconModel(armed({ engine: 'Alive', phase: '', projects: [{ name: 'a', category: 'idle' }] }), null, now);
            assert.equal(b.head, '待初始化'); assert.match(b.sub, /后台服务还没接手这一轮/);
            b = AR.beaconModel(armed({ engine: 'Alive', phase: 'blocked', projects: [] }), null, now);
            assert.equal(b.head, '本轮已停止'); assert.match(b.sub, /不会再自动续跑/);
            b = AR.beaconModel(armed({ engine: 'Stalled', probeAgeSeconds: 1800 }), null, now);
            assert.equal(b.head, '续跑监视卡住', '后台服务进程还在(页脚绿),卡住的是续跑监视'); assert.equal(b.sub, '已 30 分钟未核验额度');
            // 有同名进程但常驻 Worker 不应答:不说监视中，也不报红。
            b = AR.beaconModel(armed({ engine: 'WorkerUnverified', projects: [{ name: 'a', category: 'idle' }] }), null, now);
            assert.equal(b.cls, 'wait'); assert.equal(b.head, '监视状态未核实');
            // Claude Code 走 API 凭据的周期注入的是 API Key:订阅登录过期不能把它的状态灯染红。
            const apiTarget = { providerId: 'claude-api', agent: 'claudecode', modelId: 'deepseek-v4' };
            b = AR.beaconModel(armed({ engine: 'Alive', resumeTarget: apiTarget, projects: [{ name: 'a', status: '', category: 'idle' }] }), expired, now);
            assert.equal(b.head, '监视中');
            // 认不出模型族(额度永远无法核实):不会自己好，要重新布防。
            b = AR.beaconModel(armed({ engine: 'Alive', projects: [{ name: 'a', status: 'limited', category: 'failure', reason: 'quota:unknown', detail: 'model-invalid', reasonText: '模型额度无法核实 · 重新布防' }] }), null, now);
            assert.equal(b.cls, 'bad'); assert.equal(b.head, '需要重新布防');
            """);

    /// <summary>演示模式下一种请求的应答 payload(JSON 原文),可直接嵌进断言脚本。</summary>
    private static async Task<string> DemoPayloadJson(AiResume.Gui.ControlPlaneBridge bridge, string type)
    {
        string response = await bridge.HandleAsync($$"""{"id":"demo","type":"{{type}}"}""", CancellationToken.None);
        using JsonDocument document = JsonDocument.Parse(response);
        return document.RootElement.GetProperty("payload").GetRawText();
    }

    /// <summary>把 script#vm 放进 Node 的隔离上下文，再执行断言脚本;任何断言失败都让进程以非 0 退出。</summary>
    private static void RunViewModel(string assertions)
    {
        string html = RepositoryFile("csharp", "src", "AiResume.Gui", "wwwroot", "index.html");
        string dir = TestTemp.NewDir("gui-vm");
        string assertionsPath = Path.Combine(dir, "assertions.js");
        File.WriteAllText(assertionsPath, assertions);
        string script = Path.Combine(dir, "verify.cjs");
        File.WriteAllText(script, """
            const fs = require('node:fs'), vm = require('node:vm'), assert = require('node:assert/strict');
            const source = fs.readFileSync(process.argv[2], 'utf8');
            const start = source.indexOf('<script id="vm">');
            const end = source.indexOf('</script>', start);
            if (start < 0 || end < 0) throw new Error('页面里找不到 script#vm');
            const code = source.slice(start + '<script id="vm">'.length, end);
            const context = vm.createContext({ assert, console });
            vm.runInContext(code + '\n;globalThis.AR = AR;', context);
            vm.runInContext(fs.readFileSync(process.argv[3], 'utf8'), context);
            """);
        var start = new ProcessStartInfo("node")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
        };
        start.ArgumentList.Add(script);
        start.ArgumentList.Add(html);
        start.ArgumentList.Add(assertionsPath);
        using Process process = Process.Start(start)!;
        string stderr = process.StandardError.ReadToEnd();
        Assert.True(process.WaitForExit(20_000), "视图模型验证超时");
        Assert.True(process.ExitCode == 0, stderr);
    }

    private static string RepositoryFile(params string[] parts)
    {
        DirectoryInfo? current = new(AppContext.BaseDirectory);
        while (current is not null && !File.Exists(Path.Combine(current.FullName, "CLAUDE.md")))
        {
            current = current.Parent;
        }

        Assert.NotNull(current);
        return Path.Combine(new[] { current!.FullName }.Concat(parts).ToArray());
    }
}
