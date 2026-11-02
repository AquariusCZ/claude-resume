using System.Globalization;
using System.Text.Json;
using AiResume.LarkCli;
using AiResume.Secrets;
using AiResume.Worker.Notifications;
using Xunit;

namespace AiResume.Tests;

/// <summary>
/// 通知投递可靠性(2026-10-30 体检后的重写):错误分类、通道熔断、单条退避与死信、稳定期与同会话合并、
/// 机器范围凭据、lark-cli 体检与收件人识别的录制形状。全部离线，绝不调用真实 lark-cli 或网络。
/// </summary>
public sealed class NotificationDeliveryTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 30, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    // lark-cli 1.0.81 本机录制与官方 ERROR_CONTRACT 形状;只保留分类所需字段。
    [InlineData(1, """{"ok":false,"error":{"type":"api","subtype":"unknown","message":"keychain Get failed: dpapi unprotect failed: x"}}""", "keychain_unavailable", NotifyScope.Channel, false)]
    [InlineData(1, """{"ok":false,"error":{"type":"api","subtype":"rate_limit","code":230020,"message":"rate"}}""", "rate_limit", NotifyScope.Channel, false)]
    [InlineData(1, """{"ok":false,"error":{"type":"api","code":99991400,"message":"too many"}}""", "rate_limit", NotifyScope.Channel, false)]
    [InlineData(4, """{"ok":false,"error":{"type":"network","subtype":"timeout","message":"t"}}""", "network", NotifyScope.Channel, false)]
    [InlineData(3, """{"ok":false,"error":{"type":"authentication","subtype":"token_invalid","message":"a"}}""", "auth_config", NotifyScope.Channel, false)]
    [InlineData(1, """{"ok":false,"error":{"type":"api","code":99991663,"message":"token invalid"}}""", "auth_config", NotifyScope.Channel, false)]
    // 10003:换 tenant_access_token 时凭据无效(开放平台重置过 App Secret),是凭据问题,不是发消息的永久参数错误。
    [InlineData(1, """{"ok":false,"error":{"type":"api","subtype":"unknown","code":10003,"message":"invalid param"}}""", "auth_config", NotifyScope.Channel, false)]
    [InlineData(3, """{"ok":false,"error":{"type":"authorization","subtype":"missing_scope","message":"m"}}""", "permission_or_recipient", NotifyScope.Channel, false)]
    [InlineData(1, """{"ok":false,"error":{"type":"api","code":230013,"message":"user not in scope"}}""", "permission_or_recipient", NotifyScope.Channel, false)]
    [InlineData(2, """{"ok":false,"error":{"type":"validation","subtype":"invalid_argument","message":"v"}}""", "invalid_message", NotifyScope.Item, true)]
    [InlineData(5, """{"ok":false,"error":{"type":"internal","message":"i"}}""", "unknown", NotifyScope.Item, false)]
    [InlineData(10, """{"ok":false,"error":{"type":"confirmation","message":"c"}}""", "confirmation_required", NotifyScope.Channel, false)]
    public void 错误信封按类别分到条目级或通道级(int exit, string stderr, string expectedClass, NotifyScope scope, bool permanent)
    {
        using LarkEnvelope? error = LarkEnvelope.TryParse(stderr);

        NotifySendResult result = NotifyClassifier.Classify(exit, null, error);

        Assert.False(result.Sent);
        Assert.Equal(expectedClass, result.Class);
        Assert.Equal(scope, result.Scope);
        Assert.Equal(permanent, result.Permanent);
        Assert.Equal(exit, result.ExitCode);
    }

    [Fact]
    public void 成功信封记下消息ID()
    {
        using LarkEnvelope? success = LarkEnvelope.TryParse("""{"ok":true,"identity":"bot","data":{"message_id":"om_test","chat_id":"oc_test"}}""");

        NotifySendResult result = NotifyClassifier.Classify(0, success, null);

        Assert.True(result.Sent);
        Assert.Equal("om_test", result.MessageId);
    }

    [Fact]
    public void 定位lark_cli只认真正的exe_绝不返回cmd垫片()
    {
        string npm = TestTemp.NewDir("npm-shim");
        File.WriteAllText(Path.Combine(npm, "lark-cli.cmd"), "@echo off");
        Assert.Null(LarkCliNotificationChannel.ResolveExecutable(npm, null));

        string bin = Path.Combine(npm, "node_modules", "@larksuite", "cli", "bin");
        Directory.CreateDirectory(bin);
        File.WriteAllText(Path.Combine(bin, "lark-cli.exe"), string.Empty);
        Assert.Equal(Path.Combine(bin, "lark-cli.exe"), LarkCliNotificationChannel.ResolveExecutable("\"" + npm + "\"", null));

        string appData = TestTemp.NewDir("appdata");
        string fallback = Path.Combine(appData, "npm", "node_modules", "@larksuite", "cli", "bin");
        Directory.CreateDirectory(fallback);
        File.WriteAllText(Path.Combine(fallback, "lark-cli.exe"), string.Empty);
        Assert.Equal(Path.Combine(fallback, "lark-cli.exe"), LarkCliNotificationChannel.ResolveExecutable(string.Empty, appData));

        // 用户自己的 npm 全局包优先于 PATH 上的同名程序;相对 PATH 条目不参与解析。
        string planted = TestTemp.NewDir("planted");
        File.WriteAllText(Path.Combine(planted, "lark-cli.exe"), string.Empty);
        Assert.Equal(Path.Combine(fallback, "lark-cli.exe"), LarkCliNotificationChannel.ResolveExecutable(planted, appData));
        Assert.Null(LarkCliNotificationChannel.ResolveExecutable(".;bin;tools", null));
    }

    [Fact]
    public void doctor与auth_list按录制形状解析()
    {
        // lark-cli 1.0.81 本机 doctor 输出形状(应用 ID 已换成合成值)。
        LarkDoctorReport ready = LarkDoctorReport.Parse("""
            {"checks":[{"name":"cli_version","status":"pass","message":"1.0.81"},
              {"name":"app_resolved","status":"pass","message":"app: cli_test000000 (feishu)"},
              {"name":"bot_identity","status":"pass","message":"Bot identity: ready"},
              {"name":"endpoint_open","status":"pass","message":"https://open.feishu.cn reachable"}],"ok":true,"workspace":"local"}
            """, "env");
        LarkDoctorReport failed = LarkDoctorReport.Parse("""
            {"checks":[{"name":"bot_identity","status":"warn","message":"Bot identity: verify failed: no tat available from credential source \"env\"","hint":"x"},
              {"name":"identity_ready","status":"fail","message":"no usable bot or user identity is available"}],"ok":false,"workspace":"local"}
            """, "env");

        Assert.True(ready.Ok);
        Assert.True(ready.BotReady);
        Assert.False(failed.Ok);
        Assert.False(failed.BotReady);
        Assert.False(LarkDoctorReport.Parse("not json", "env").Ok);

        IReadOnlyList<LarkUserCandidate> users = LarkUserCandidate.Parse("""
            [{"appId":"cli_test000000","tokenStatus":"valid","userName":"测试用户","userOpenId":"ou_0123456789abcdef0123456789abcdef"},
             {"appId":"cli_test000000","tokenStatus":"valid","userName":"坏数据","userOpenId":"not-an-open-id"}]
            """);
        LarkUserCandidate user = Assert.Single(users);
        Assert.Equal("ou_0123456789abcdef0123456789abcdef", user.UserOpenId);
    }

    [Fact]
    public async Task 机器人凭据存为机器范围密文_也能读旧版飞书接入保存的形状()
    {
        string root = TestTemp.NewDir("feishu-bot");
        var credentials = new FeishuBotCredentials(root);
        Assert.False(credentials.Exists);

        credentials.Save("cli_test000000", "fake-app-secret-value");

        Assert.True(credentials.TryLoad(out string appId, out string secret));
        Assert.Equal("cli_test000000", appId);
        Assert.Equal("fake-app-secret-value", secret);
        Assert.DoesNotContain("fake-app-secret-value", File.ReadAllText(credentials.SecretPath), StringComparison.Ordinal);
        Assert.Equal("cli_te…00", credentials.MaskedAppId());

        // 旧版 FeishuCredentialStore 用 CurrentUser DPAPI 写 {AppId, AppSecret, AllowFrom}。
        byte[] legacy = JsonSerializer.SerializeToUtf8Bytes(new { AppId = "cli_legacy0000", AppSecret = "fake-legacy-secret", AllowFrom = "ou_x" });
        await new DpapiSecretStore(root).SaveAsync(FeishuBotCredentials.LegacyCredentialRef, legacy, CancellationToken.None);
        Assert.True(credentials.LegacyExists);
        Assert.True(credentials.TryLoadLegacy(out string legacyId, out string legacySecret));
        Assert.Equal("cli_legacy0000", legacyId);
        Assert.Equal("fake-legacy-secret", legacySecret);

        Assert.Throws<ArgumentException>(() => credentials.Save("not-an-app", "fake-app-secret-value"));
        credentials.Delete();
        Assert.False(credentials.Exists);
    }

    [Fact]
    public async Task 通道级失败立刻熔断本轮_只尝试一次_设置一变就解除()
    {
        string dir = TestTemp.NewDir("notify-breaker");
        string events = Path.Combine(dir, "events");
        WriteEvent(events, "1111111111111111", "a", Now.AddMinutes(-30));
        WriteEvent(events, "2222222222222222", "b", Now.AddMinutes(-20));
        WriteEvent(events, "3333333333333333", "c", Now.AddMinutes(-10));
        int calls = 0;
        bool healthy = false;
        string fingerprint = "v1";
        var notifier = new CompletionNotifier(events, Path.Combine(dir, "seen.json"),
            (_, _, _, _) =>
            {
                calls++;
                return Task.FromResult(healthy ? NotifySendResult.Delivered() : NotifySendResult.ChannelFailure("keychain_unavailable"));
            },
            () => Now, channelFingerprint: () => fingerprint, sendSpacing: TimeSpan.Zero);

        NotifySweepResult first = await notifier.SweepAsync("ou_test");
        Assert.Equal(1, calls);
        Assert.Equal(2, first.Deferred);
        Assert.NotNull(first.ChannelBlockedUntilUtc);
        DeliveryHealth health = DeliveryHealth.TryLoad(notifier.HealthPath)!;
        Assert.Equal("blocked", health.State);
        Assert.Equal("keychain_unavailable", health.Reason);

        await notifier.SweepAsync("ou_test");
        Assert.Equal(1, calls);

        healthy = true;
        fingerprint = "v2";
        NotifySweepResult recovered = await notifier.SweepAsync("ou_test");
        Assert.Equal(4, calls);
        Assert.Equal(3, recovered.Sent);
        Assert.Equal("healthy", DeliveryHealth.TryLoad(notifier.HealthPath)!.State);
    }

    [Fact]
    public async Task 单条失败只退避这一条_新事件照常立即投递()
    {
        string dir = TestTemp.NewDir("notify-item-backoff");
        string events = Path.Combine(dir, "events");
        WriteEvent(events, "aaaaaaaaaaaaaaaa", "old", Now.AddMinutes(-30));
        DateTimeOffset now = Now;
        var attempted = new List<string>();
        var notifier = new CompletionNotifier(events, Path.Combine(dir, "seen.json"),
            (_, _, key, _) =>
            {
                attempted.Add(key);
                return Task.FromResult(key == "aaaaaaaaaaaaaaaa" ? NotifySendResult.ItemFailure("unknown") : NotifySendResult.Delivered());
            },
            () => now, sendSpacing: TimeSpan.Zero);

        await notifier.SweepAsync("ou_test");
        WriteEvent(events, "bbbbbbbbbbbbbbbb", "new", now.AddMinutes(-1));
        now = now.AddSeconds(5);
        NotifySweepResult second = await notifier.SweepAsync("ou_test");

        Assert.Equal(new[] { "aaaaaaaaaaaaaaaa", "bbbbbbbbbbbbbbbb" }, attempted);
        Assert.Equal(1, second.Sent);
        Assert.Contains(second.Items, item => item.EventId == "aaaaaaaaaaaaaaaa" && item.Outcome == NotifyOutcome.Deferred);
        Assert.True(File.Exists(Path.Combine(events, "aaaaaaaaaaaaaaaa.json")));

        now = now.AddMinutes(1);
        await notifier.SweepAsync("ou_test");
        Assert.Equal(3, attempted.Count);
    }

    [Fact]
    public async Task 参数类永久失败进死信不拖累后续_同轮两条同类就升级为通道熔断()
    {
        string dir = TestTemp.NewDir("notify-dead");
        string events = Path.Combine(dir, "events");
        WriteEvent(events, "1111111111111111", "a", Now.AddMinutes(-30));
        WriteEvent(events, "2222222222222222", "b", Now.AddMinutes(-20));
        WriteEvent(events, "3333333333333333", "c", Now.AddMinutes(-10));
        var notifier = new CompletionNotifier(events, Path.Combine(dir, "seen.json"),
            (_, _, key, _) => Task.FromResult(key == "3333333333333333"
                ? NotifySendResult.Delivered()
                : NotifySendResult.ItemFailure("invalid_message", permanent: true)),
            () => Now, sendSpacing: TimeSpan.Zero);

        NotifySweepResult result = await notifier.SweepAsync("ou_test");

        Assert.Contains(result.Items, item => item.EventId == "1111111111111111" && item.Outcome == NotifyOutcome.Dead);
        Assert.True(File.Exists(Path.Combine(events, "dead", "1111111111111111.json")));
        Assert.Contains(result.Items, item => item.EventId == "2222222222222222" && item.Detail == "cli_contract");
        Assert.True(File.Exists(Path.Combine(events, "2222222222222222.json")));
        Assert.Equal("cli_contract", DeliveryHealth.TryLoad(notifier.HealthPath)!.Reason);
    }

    [Fact]
    public async Task CLI契约坏了之后熔断粘滞_到期重试仍失败也不再一条条进死信()
    {
        // 真实节奏:一轮只来一条事件。A 进死信、B 升级熔断;30 分钟后熔断到期,B 重试仍是同类永久失败,
        // 此时应再次熔断,而不是把 B(以及之后的 C)送进死信。
        string dir = TestTemp.NewDir("notify-contract");
        string events = Path.Combine(dir, "events");
        DateTimeOffset now = Now;
        var notifier = new CompletionNotifier(events, Path.Combine(dir, "seen.json"),
            (_, _, _, _) => Task.FromResult(NotifySendResult.ItemFailure("invalid_message", permanent: true)),
            () => now, sendSpacing: TimeSpan.Zero);

        WriteEvent(events, "aaaaaaaaaaaaaaa1", "a", now.AddSeconds(-30));
        await notifier.SweepAsync("ou_test");
        Assert.True(File.Exists(Path.Combine(events, "dead", "aaaaaaaaaaaaaaa1.json")));

        now = now.AddMinutes(1);
        WriteEvent(events, "aaaaaaaaaaaaaaa2", "b", now.AddSeconds(-30));
        await notifier.SweepAsync("ou_test");
        Assert.Equal("cli_contract", DeliveryHealth.TryLoad(notifier.HealthPath)!.Reason);

        now = now.AddMinutes(31);
        WriteEvent(events, "aaaaaaaaaaaaaaa3", "c", now.AddSeconds(-30));
        NotifySweepResult third = await notifier.SweepAsync("ou_test");

        Assert.Contains(third.Items, item => item.EventId == "aaaaaaaaaaaaaaa2" && item.Detail == "cli_contract");
        Assert.False(File.Exists(Path.Combine(events, "dead", "aaaaaaaaaaaaaaa2.json")));
        Assert.True(File.Exists(Path.Combine(events, "aaaaaaaaaaaaaaa3.json")));
        DeliveryHealth health = DeliveryHealth.TryLoad(notifier.HealthPath)!;
        Assert.Equal("blocked", health.State);
        Assert.Equal("cli_contract", health.Reason);
    }

    [Fact]
    public async Task 按事件发生时间从旧到新投递()
    {
        string dir = TestTemp.NewDir("notify-order");
        string events = Path.Combine(dir, "events");
        WriteEvent(events, "0000000000000003", "c", Now.AddMinutes(-1), session: "s3");
        WriteEvent(events, "0000000000000001", "a", Now.AddMinutes(-3), fileName: "zzz.json", session: "s1");
        WriteEvent(events, "0000000000000002", "b", Now.AddMinutes(-2), fileName: "aaa.json", session: "s2");
        var order = new List<string>();
        var notifier = new CompletionNotifier(events, Path.Combine(dir, "seen.json"),
            (_, _, key, _) => { order.Add(key); return Task.FromResult(NotifySendResult.Delivered()); },
            () => Now, sendSpacing: TimeSpan.Zero);

        await notifier.SweepAsync("ou_test");

        Assert.Equal(new[] { "0000000000000001", "0000000000000002", "0000000000000003" }, order);
    }

    [Fact]
    public async Task Claude完成有稳定期_同会话更新的完成替代旧的_后台任务未完时暂缓()
    {
        string dir = TestTemp.NewDir("notify-settle");
        string events = Path.Combine(dir, "events");
        WriteEvent(events, "aaaaaaaaaaaaaaaa", "proj", Now.AddSeconds(-90), session: "s1");
        WriteEvent(events, "bbbbbbbbbbbbbbbb", "proj", Now.AddSeconds(-5), session: "s1");
        WriteEvent(events, "cccccccccccccccc", "busy", Now.AddMinutes(-3), session: "s2", backgroundBusy: true);
        DateTimeOffset now = Now;
        var sent = new List<string>();
        var notifier = new CompletionNotifier(events, Path.Combine(dir, "seen.json"),
            (_, _, key, _) => { sent.Add(key); return Task.FromResult(NotifySendResult.Delivered()); },
            () => now, sendSpacing: TimeSpan.Zero);

        NotifySweepResult first = await notifier.SweepAsync("ou_test");
        Assert.Empty(sent);
        Assert.Contains(first.Items, item => item.EventId == "aaaaaaaaaaaaaaaa" && item.Outcome == NotifyOutcome.Superseded);
        Assert.Contains(first.Items, item => item.EventId == "bbbbbbbbbbbbbbbb" && item.Detail == "settling");
        Assert.Contains(first.Items, item => item.EventId == "cccccccccccccccc" && item.Detail == "background_busy");

        now = Now.AddSeconds(30);
        await notifier.SweepAsync("ou_test");
        Assert.Equal(new[] { "bbbbbbbbbbbbbbbb" }, sent);

        now = Now.AddMinutes(40);
        await notifier.SweepAsync("ou_test");
        Assert.Equal(new[] { "bbbbbbbbbbbbbbbb", "cccccccccccccccc" }, sent);
    }

    [Theory]
    [InlineData("failure", null, "rate_limit", "因用量上限中断")]
    [InlineData("failure", null, "authentication_failed", "/login")]
    [InlineData("decision", "execution_failed", null, "运行失败")]
    [InlineData("decision", "quota_auto_resume_stale", null, "没能自动继续")]
    [InlineData("resume", "session-open", null, "额度已恢复")]
    [InlineData("resume", "resume-succeeded", null, "自动续跑完成")]
    public async Task 通知文案按类型说清发生了什么(string kind, string? type, string? error, string expected)
    {
        string dir = TestTemp.NewDir("notify-text");
        string events = Path.Combine(dir, "events");
        WriteEvent(events, "dddddddddddddddd", "ledger-api", Now.AddMinutes(-5), kind: kind, notificationType: type, error: error,
            source: kind == "resume" ? "ai-resume" : "claudecode", model: kind == "resume" ? "claude-opus-5-5" : null, bridged: kind == "failure");
        string? text = null;
        var notifier = new CompletionNotifier(events, Path.Combine(dir, "seen.json"),
            (_, message, _, _) => { text = message; return Task.FromResult(NotifySendResult.Delivered()); },
            () => Now, sendSpacing: TimeSpan.Zero);

        await notifier.SweepAsync("ou_test");

        Assert.NotNull(text);
        Assert.Contains("ledger-api", text);
        Assert.Contains(expected, text);
        Assert.Contains('\n', text);
        if (kind == "resume")
        {
            Assert.Contains("AI Resume · Opus 5.5", text);
        }

        if (kind == "failure")
        {
            Assert.Contains("远程已连接", text);
        }
    }

    [Fact]
    public async Task AI_Resume的登录提醒不需要项目目录()
    {
        string dir = TestTemp.NewDir("notify-login");
        string events = Path.Combine(dir, "events");
        Directory.CreateDirectory(events);
        File.WriteAllText(Path.Combine(events, "eeeeeeeeeeeeeeee.json"), JsonSerializer.Serialize(new
        {
            eventId = "eeeeeeeeeeeeeeee", source = "ai-resume", kind = "resume", notificationType = "login-expiring",
            detail = "11-11 05:55", atUtc = Now.AddMinutes(-1).ToString("o", CultureInfo.InvariantCulture),
        }));
        string? text = null;
        var notifier = new CompletionNotifier(events, Path.Combine(dir, "seen.json"),
            (_, message, _, _) => { text = message; return Task.FromResult(NotifySendResult.Delivered()); },
            () => Now, sendSpacing: TimeSpan.Zero);

        NotifySweepResult result = await notifier.SweepAsync("ou_test");

        Assert.Equal(1, result.Sent);
        Assert.Contains("登录即将到期", text);
        Assert.Contains("/login", text);
    }

    [Fact]
    public async Task 用户关掉的通知源不发_残留链路写的事件直接丢弃_AI_Resume自己的事件照发()
    {
        string dir = TestTemp.NewDir("notify-intent");
        string events = Path.Combine(dir, "events");
        // Codex 已在界面里关掉，但 config.toml 的旧版 notify 链仍会写事件(共享键，按 ADR-0005 不改写)。
        WriteEvent(events, "1000000000000001", "codex-proj", Now.AddMinutes(-3), source: "codex", session: "c1");
        WriteEvent(events, "1000000000000002", "claude-proj", Now.AddMinutes(-2), source: "claudecode", session: "k1");
        WriteEvent(events, "1000000000000003", "AI Resume", Now.AddMinutes(-1), source: "ai-resume", kind: "resume", notificationType: "resume-succeeded");
        string[] intent = ["ClaudeCode", "OpenCode"];
        var sent = new List<string>();
        var notifier = new CompletionNotifier(events, Path.Combine(dir, "seen.json"),
            (_, _, key, _) => { sent.Add(key); return Task.FromResult(NotifySendResult.Delivered()); },
            () => Now, sendSpacing: TimeSpan.Zero,
            sourceEnabled: source => CompletionNotifier.IsSourceEnabled(source, intent));

        NotifySweepResult result = await notifier.SweepAsync("ou_test");

        Assert.Equal(new[] { "1000000000000002", "1000000000000003" }, sent);
        Assert.Contains(result.Items, item => item.EventId == "1000000000000001" && item.Outcome == NotifyOutcome.Suppressed);
        Assert.False(File.Exists(Path.Combine(events, "1000000000000001.json")));

        // 意图为空且从未记录过(旧配置)时不过滤;记录过的空列表 = 用户把通知源全关了,钩子事件一律不发。来源名只差大小写。
        Assert.True(CompletionNotifier.IsSourceEnabled("codex", Array.Empty<string>()));
        Assert.False(CompletionNotifier.IsSourceEnabled("codex", Array.Empty<string>(), intentRecorded: true));
        Assert.True(CompletionNotifier.IsSourceEnabled("ai-resume", Array.Empty<string>(), intentRecorded: true));
        Assert.True(CompletionNotifier.IsSourceEnabled("opencode", intent));
        Assert.False(CompletionNotifier.IsSourceEnabled(null, intent));
    }

    [Fact]
    public async Task 后台任务未完的完成被同会话任何更新的完成作废_哪怕更新的那条早已送出()
    {
        string dir = TestTemp.NewDir("notify-busy-late");
        string events = Path.Combine(dir, "events");
        DateTimeOffset now = Now;
        var sent = new List<string>();
        var notifier = new CompletionNotifier(events, Path.Combine(dir, "seen.json"),
            (_, _, key, _) => { sent.Add(key); return Task.FromResult(NotifySendResult.Delivered()); },
            () => now, sendSpacing: TimeSpan.Zero);

        // t0:Stop 时后台子 agent 还在跑;10 分钟后才真正结束(远超 2 分钟的普通合并窗口)。
        WriteEvent(events, "b000000000000001", "proj", Now, session: "s1", backgroundBusy: true);
        now = Now.AddMinutes(10).AddSeconds(30);
        WriteEvent(events, "b000000000000002", "proj", Now.AddMinutes(10), session: "s1");
        NotifySweepResult first = await notifier.SweepAsync("ou_test");
        Assert.Equal(new[] { "b000000000000002" }, sent);
        Assert.Contains(first.Items, item => item.EventId == "b000000000000001" && item.Outcome == NotifyOutcome.Superseded);

        // 又一条更早的「后台未完」完成晚到队列(更新的那条已送出、文件已删):同样作废,不补发过期的第二条「已完成」。
        WriteEvent(events, "b000000000000003", "proj", Now.AddMinutes(5), session: "s1", backgroundBusy: true);
        now = Now.AddMinutes(45);
        await notifier.SweepAsync("ou_test");
        Assert.Equal(new[] { "b000000000000002" }, sent);
    }

    [Fact]
    public async Task 超过24小时没送出的事件不再补发_进死信()
    {
        string dir = TestTemp.NewDir("notify-stale");
        string events = Path.Combine(dir, "events");
        WriteEvent(events, "c000000000000001", "old", Now.AddHours(-30), session: "o1");
        WriteEvent(events, "c000000000000002", "fresh", Now.AddMinutes(-3), session: "f1");
        var sent = new List<string>();
        var notifier = new CompletionNotifier(events, Path.Combine(dir, "seen.json"),
            (_, _, key, _) => { sent.Add(key); return Task.FromResult(NotifySendResult.Delivered()); },
            () => Now, sendSpacing: TimeSpan.Zero);

        NotifySweepResult result = await notifier.SweepAsync("ou_test");

        Assert.Equal(new[] { "c000000000000002" }, sent);
        Assert.Contains(result.Items, item => item.EventId == "c000000000000001" && item.Outcome == NotifyOutcome.Dead && item.Detail == "stale");
        Assert.True(File.Exists(Path.Combine(events, "dead", "c000000000000001.json")));
    }

    [Fact]
    public async Task 结果不明的重发只在飞书去重窗口内_超过55分钟不再发()
    {
        string dir = TestTemp.NewDir("notify-ambiguous");
        string events = Path.Combine(dir, "events");
        WriteEvent(events, "d000000000000001", "proj", Now.AddMinutes(-1), session: "a1");
        DateTimeOffset now = Now;
        int attempts = 0;
        var notifier = new CompletionNotifier(events, Path.Combine(dir, "seen.json"),
            (_, _, _, _) => { attempts++; return Task.FromResult(NotifySendResult.ItemFailure("invalid_output") with { Ambiguous = true }); },
            () => now, sendSpacing: TimeSpan.Zero);

        await notifier.SweepAsync("ou_test");
        Assert.Equal(1, attempts);
        DeliveryHealth health = DeliveryHealth.TryLoad(notifier.HealthPath)!;
        Assert.False(health.LastAttemptOk);

        now = Now.AddMinutes(56);
        NotifySweepResult late = await notifier.SweepAsync("ou_test");
        Assert.Equal(1, attempts);
        Assert.Contains(late.Items, item => item.EventId == "d000000000000001" && item.Outcome == NotifyOutcome.Dead && item.Detail == "outcome_unknown");
    }

    [Fact]
    public async Task 成功记下设置指纹_之后的条目级失败让最近一次尝试变为失败()
    {
        string dir = TestTemp.NewDir("notify-health");
        string events = Path.Combine(dir, "events");
        WriteEvent(events, "e000000000000001", "ok", Now.AddMinutes(-2), session: "h1");
        bool fail = false;
        var notifier = new CompletionNotifier(events, Path.Combine(dir, "seen.json"),
            (_, _, _, _) => Task.FromResult(fail ? NotifySendResult.ItemFailure("unknown") : NotifySendResult.Delivered()),
            () => Now, channelFingerprint: () => "fp-1", sendSpacing: TimeSpan.Zero);

        await notifier.SweepAsync("ou_test");
        DeliveryHealth health = DeliveryHealth.TryLoad(notifier.HealthPath)!;
        Assert.True(health.LastAttemptOk);
        Assert.Equal("fp-1", health.SuccessFingerprint);

        fail = true;
        WriteEvent(events, "e000000000000002", "bad", Now.AddMinutes(-1), session: "h2");
        await notifier.SweepAsync("ou_test");
        health = DeliveryHealth.TryLoad(notifier.HealthPath)!;
        Assert.Equal("healthy", health.State);
        Assert.False(health.LastAttemptOk);
        Assert.Equal("unknown", health.LastErrorClass);
    }

    [Theory]
    [InlineData("claude-opus-5-5", "Opus 5.5")]
    [InlineData("claude-fable-5-1", "Fable 5.1")]
    [InlineData("claude-sonnet-4-5-20250929", "Sonnet 4.5")]
    [InlineData("gpt-5.6-sol", "gpt-5.6-sol")]
    public void 模型调用ID显示为短名(string id, string expected) => Assert.Equal(expected, CompletionNotifier.ModelLabel(id));

    private static void WriteEvent(string events, string eventId, string project, DateTimeOffset at, string? fileName = null,
        string session = "s", bool backgroundBusy = false, string? kind = null, string? notificationType = null, string? error = null,
        string source = "claudecode", string? model = null, bool bridged = false)
    {
        Directory.CreateDirectory(events);
        File.WriteAllText(Path.Combine(events, fileName ?? eventId + ".json"), JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["version"] = 1, ["eventId"] = eventId, ["source"] = source, ["sessionId"] = session,
            ["cwd"] = @"C:\work\" + project, ["kind"] = kind, ["notificationType"] = notificationType, ["error"] = error,
            ["backgroundBusy"] = backgroundBusy, ["bridged"] = bridged, ["model"] = model,
            ["atUtc"] = at.ToString("o", CultureInfo.InvariantCulture),
        }));
    }
}
