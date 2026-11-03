using System.Text.Json;
using AiResume.Secrets;
using AiResume.Worker.Notifications;
using AiResume.Worker.Products;
using AiResume.Worker.Quota;
using AiResume.Worker.Resume;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AiResume.Tests;

/// <summary>
/// 「完成通知 → 飞书」控制面：凭据经真实换令牌 + 一次只读机器人接口调用核验后才落盘(ADR-0007)、收件人规整、
/// 测试通知走真实队列;以及 Claude Code 登录到期提醒。假 lark-cli 是 TestTemp 下的 .cmd 脚本，输出照抄 1.0.81 录制形状
/// (合成 ID);换令牌端点是 <see cref="FeishuTokenStub"/>,绝不真调飞书。
/// </summary>
public sealed class NotifyControlPlaneTests
{
    private const string Doctor = """{"checks":[{"name":"bot_identity","status":"pass","message":"Bot identity: ready"}],"ok":true,"workspace":"local"}""";
    private const string DoctorFail = """{"checks":[{"name":"bot_identity","status":"warn","message":"Bot identity: verify failed: no tat available from credential source env"}],"ok":false,"workspace":"local"}""";
    private const string Users = """[{"appId":"cli_test000000","tokenStatus":"valid","userName":"测试用户","userOpenId":"ou_0123456789abcdef0123456789abcdef"}]""";

    [Fact]
    public async Task 只有真实换到令牌且机器人接口调通的凭据才落盘_不通过不写()
    {
        string root = TestTemp.NewDir("notify-control");
        string fake = FakeLarkCli(root);
        var control = new NotifyControlPlane(new ProductConfigStore(root), root, new FeishuBotCredentials(root), () => fake,
            new FeishuTenantToken(new FeishuTokenStub()));

        // 飞书拒绝换令牌(录制形状 10014):不落盘，说明里带错误码与 log_id,绝不回显 App Secret。
        var refused = await Assert.ThrowsAsync<InvalidOperationException>(
            () => control.SetCredentialAsync("cli_test000000", "fake-wrong-secret", CancellationToken.None));
        Assert.Contains("10014", refused.Message);
        Assert.Contains("log-10014", refused.Message);
        Assert.DoesNotContain("fake-wrong-secret", refused.Message);
        Assert.False(new FeishuBotCredentials(root).Exists);

        // 换到了令牌，机器人接口却被拒(应用没开机器人能力或缺权限):同样不落盘。
        var noBot = await Assert.ThrowsAsync<InvalidOperationException>(
            () => control.SetCredentialAsync("cli_test000000", "fake-nobot-secret", CancellationToken.None));
        Assert.Contains("机器人接口被拒", noBot.Message);
        Assert.False(new FeishuBotCredentials(root).Exists);

        NotifyChannelStatus saved = await control.SetCredentialAsync("cli_test000000", "fake-good-secret", CancellationToken.None);

        Assert.Equal("env", saved.CredentialSource);
        Assert.Equal("cli_te…00", saved.AppIdMasked);
        Assert.True(new FeishuBotCredentials(root).TryLoad(out _, out string secret));
        Assert.Equal("fake-good-secret", secret);

        // 诊断：副本路径先换令牌、调机器人接口(决定就绪),再附注入令牌后的 doctor;令牌不出现在任何说明里。
        LarkDoctorReport check = await control.CheckAsync(CancellationToken.None);
        Assert.True(check.BotReady);
        Assert.Null(check.Problem);
        Assert.Equal("env", check.CredentialSource);
        Assert.Equal(["tenant_token", "bot_api"], check.Checks.Take(2).Select(item => item.Name));
        Assert.Contains(check.Checks, item => item.Name == "bot_identity" && item.Status == "pass");
        Assert.DoesNotContain(check.Checks, item => (item.Message ?? string.Empty).Contains(FeishuTokenStub.Good, StringComparison.Ordinal));
        // doctor 看的是用户自己的 lark-cli 配置:不带副本路径的私有配置目录;机器人接口那次带。
        string[] calls = File.ReadAllLines(Path.Combine(root, "calls.txt"));
        Assert.Contains(calls, line => line.Trim() == "api|cli_test000000|" + Path.Combine(root, "lark-cli"));
        Assert.Contains(calls, line => line.Trim() == "doctor|cli_test000000|");

        Assert.Equal("keychain", control.ClearCredential().CredentialSource);
    }

    [Fact]
    public async Task 机器人未启用或doctor出问题时说法分明_doctor的问题不推翻核验结论()
    {
        string root = TestTemp.NewDir("notify-control-bot");
        string fake = FakeLarkCli(root);
        var control = new NotifyControlPlane(new ProductConfigStore(root), root, new FeishuBotCredentials(root), () => fake,
            new FeishuTenantToken(new FeishuTokenStub()));

        // 机器人信息接口调通了，但 activate_status 不是 2(停用或待启用):发不了消息，不落盘。
        var inactive = await Assert.ThrowsAsync<InvalidOperationException>(
            () => control.SetCredentialAsync("cli_test000000", "fake-inactive-secret", CancellationToken.None));
        Assert.Contains("机器人没有启用", inactive.Message);
        Assert.Contains("activate_status_1", inactive.Message);
        Assert.False(new FeishuBotCredentials(root).Exists);

        // 换令牌与机器人接口都过、doctor 自己出问题:就绪与问题只看前两步,doctor 的问题只作附注检查项。
        await control.SetCredentialAsync("cli_test000000", "fake-doctor-fail-secret", CancellationToken.None);
        LarkDoctorReport check = await control.CheckAsync(CancellationToken.None);
        Assert.True(check.BotReady);
        Assert.Null(check.Problem);
        Assert.Contains(check.Checks, item => item.Name == "doctor" && item.Status == "warn");
    }

    [Fact]
    public async Task 副本读不出时诊断与识别都如实报错_状态标明读不出()
    {
        string root = TestTemp.NewDir("notify-control-unreadable");
        string fake = FakeLarkCli(root);
        var credentials = new FeishuBotCredentials(root);
        credentials.Save("cli_test000000", "fake-good-secret");
        File.WriteAllBytes(credentials.SecretPath, [1, 2, 3, 4]);
        var control = new NotifyControlPlane(new ProductConfigStore(root), root, credentials, () => fake, new FeishuTenantToken(new FeishuTokenStub()));

        NotifyChannelStatus status = control.Describe();
        Assert.Equal("env", status.CredentialSource);
        Assert.True(status.CredentialUnreadable);
        Assert.Null(status.AppIdMasked);

        LarkDoctorReport check = await control.CheckAsync(CancellationToken.None);
        Assert.False(check.BotReady);
        Assert.Equal("credential_copy", Assert.Single(check.Checks).Name);
        Assert.False(File.Exists(Path.Combine(root, "calls.txt")));
        // 没法按应用过滤：不能把别的应用下的 open_id 当候选递出去。
        await Assert.ThrowsAsync<InvalidOperationException>(() => control.DetectRecipientsAsync(CancellationToken.None));

        credentials.Delete();
        Assert.False(control.Describe().CredentialUnreadable);
    }

    [Fact]
    public async Task 导入旧版凭据同样先换令牌并调通机器人接口()
    {
        string root = TestTemp.NewDir("notify-control-import");
        string fake = FakeLarkCli(root);
        var control = new NotifyControlPlane(new ProductConfigStore(root), root, new FeishuBotCredentials(root), () => fake,
            new FeishuTenantToken(new FeishuTokenStub()));
        async Task Legacy(string secret) => await new DpapiSecretStore(root).SaveAsync(FeishuBotCredentials.LegacyCredentialRef,
            JsonSerializer.SerializeToUtf8Bytes(new { AppId = "cli_test000000", AppSecret = secret, AllowFrom = "ou_x" }), CancellationToken.None);

        await Legacy("fake-wrong-secret");
        var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => control.ImportLegacyAsync(CancellationToken.None));
        Assert.Contains("旧版飞书凭据未导入", refused.Message);
        Assert.Contains("10014", refused.Message);
        Assert.False(new FeishuBotCredentials(root).Exists);

        await Legacy("fake-good-secret");
        NotifyChannelStatus imported = await control.ImportLegacyAsync(CancellationToken.None);
        Assert.Equal("env", imported.CredentialSource);
        Assert.True(new FeishuBotCredentials(root).TryLoad(out _, out string secret));
        Assert.Equal("fake-good-secret", secret);
    }

    [Fact]
    public async Task 副本在却换不到令牌时诊断不跑doctor_也不把AppSecret交给lark_cli()
    {
        string root = TestTemp.NewDir("notify-control-refused");
        string fake = FakeLarkCli(root);
        new FeishuBotCredentials(root).Save("cli_test000000", "fake-wrong-secret");
        var control = new NotifyControlPlane(new ProductConfigStore(root), root, new FeishuBotCredentials(root), () => fake,
            new FeishuTenantToken(new FeishuTokenStub()));

        LarkDoctorReport check = await control.CheckAsync(CancellationToken.None);

        Assert.False(check.BotReady);
        Assert.Equal("env", check.CredentialSource);
        LarkDoctorCheck token = Assert.Single(check.Checks);
        Assert.Equal("tenant_token", token.Name);
        Assert.Equal("fail", token.Status);
        Assert.Contains("10014", check.Problem);
        Assert.False(File.Exists(Path.Combine(root, "calls.txt")), "没换到令牌就不该启动 lark-cli");
    }

    [Fact]
    public async Task 收件人只接受规整后的open_id_识别从lark_cli同一应用读取()
    {
        string root = TestTemp.NewDir("notify-recipient");
        string fake = FakeLarkCli(root);
        var store = new ProductConfigStore(root);
        var control = new NotifyControlPlane(store, root, new FeishuBotCredentials(root), () => fake);

        Assert.Throws<ArgumentException>(() => control.SetRecipient("not-an-open-id"));
        NotifyChannelStatus status = control.SetRecipient(" ou_0123456789abcdef 0123456789abcdef ");

        Assert.Equal("ou_0123456789abcdef0123456789abcdef", store.Load().NotifyRecipientOpenId);
        Assert.True(status.RecipientSet);
        Assert.Equal("ou_0123…cdef", status.RecipientMasked);
        RecipientCandidates found = await control.DetectRecipientsAsync(CancellationToken.None);
        Assert.False(found.FilteredByApp);
        LarkUserCandidate user = Assert.Single(found.Users);
        Assert.Equal("ou_0123456789abcdef0123456789abcdef", user.UserOpenId);

        // open_id 按应用隔离:保存了另一个应用的机器人凭据后,lark-cli 默认应用下的用户不能再被选作收件人。
        new FeishuBotCredentials(root).Save("cli_other00000", "fake-good-secret");
        found = await control.DetectRecipientsAsync(CancellationToken.None);
        Assert.True(found.FilteredByApp);
        Assert.Empty(found.Users);
        new FeishuBotCredentials(root).Save("cli_test000000", "fake-good-secret");
        Assert.Single((await control.DetectRecipientsAsync(CancellationToken.None)).Users);
    }

    [Fact]
    public void 设置在最近一次成功之后改过_成功记录不再作数()
    {
        string root = TestTemp.NewDir("notify-settings");
        var store = new ProductConfigStore(root);
        var control = new NotifyControlPlane(store, root, new FeishuBotCredentials(root), () => null);
        control.SetRecipient("ou_0123456789abcdef0123456789abcdef");
        string fingerprint = NotifyFingerprint.Compute(new FeishuBotCredentials(root), "ou_0123456789abcdef0123456789abcdef", null);
        File.WriteAllText(control.HealthPath, JsonSerializer.Serialize(new DeliveryHealth
        {
            State = "healthy", LastSuccessUtc = DateTimeOffset.UtcNow, LastAttemptOk = true, SuccessFingerprint = fingerprint,
        }));

        Assert.True(control.Describe().SettingsVerified);
        control.SetRecipient("ou_fedcba9876543210fedcba9876543210");
        Assert.False(control.Describe().SettingsVerified);
    }

    [Fact]
    public async Task 测试通知写进真实队列_投递端按测试通知文案发送()
    {
        string root = TestTemp.NewDir("notify-test");
        var control = new NotifyControlPlane(new ProductConfigStore(root), root, new FeishuBotCredentials(root), () => null);

        string eventId = control.EnqueueTest();

        string? text = null;
        var notifier = new CompletionNotifier(control.EventsDirectory, Path.Combine(root, "completion-notify-seen.json"),
            (_, message, key, _) => { text = message; Assert.Equal(eventId, key); return Task.FromResult(NotifySendResult.Delivered()); },
            sendSpacing: TimeSpan.Zero);
        DateTimeOffset since = DateTimeOffset.UtcNow.AddSeconds(-5);
        Assert.Equal("pending", control.TestOutcome(eventId, since));
        NotifySweepResult result = await notifier.SweepAsync("ou_test");

        Assert.Equal(1, result.Sent);
        Assert.StartsWith("🧪 AI Resume 测试通知\n", text);
        DeliveryHealth health = control.Describe().Health!;
        Assert.Equal("healthy", health.State);
        Assert.False(control.Describe().LarkCliFound);
        // 只认这一条测试事件自己的结局(界面「发送测试」与 notify test 同一判据)。
        Assert.Equal("delivered", control.TestOutcome(eventId, since));
    }

    [Fact]
    public void 测试通知只认此刻仍然有效的熔断_到期或改过设置都还在排队()
    {
        string root = TestTemp.NewDir("notify-test-blocked");
        var control = new NotifyControlPlane(new ProductConfigStore(root), root, new FeishuBotCredentials(root), () => null);
        DateTimeOffset since = DateTimeOffset.UtcNow.AddSeconds(-5);
        string eventId = control.EnqueueTest();
        string fingerprint = NotifyFingerprint.Compute(new FeishuBotCredentials(root), NotifyRecipient.Normalize(null), null);
        void Health(DateTimeOffset until, string blockFingerprint) => File.WriteAllText(control.HealthPath, JsonSerializer.Serialize(
            new DeliveryHealth { State = "blocked", Reason = "network", UpdatedAtUtc = DateTimeOffset.UtcNow, BlockedUntilUtc = until, BlockFingerprint = blockFingerprint }));

        Health(DateTimeOffset.UtcNow.AddMinutes(5), fingerprint);
        Assert.Equal("blocked", control.TestOutcome(eventId, since));

        // 熔断已到期、只是还没被下一轮改写:下一轮就会去试这一条。
        Health(DateTimeOffset.UtcNow.AddMinutes(-1), fingerprint);
        Assert.Equal("pending", control.TestOutcome(eventId, since));

        // 熔断之后改过设置:Worker 会立即解除。
        Health(DateTimeOffset.UtcNow.AddMinutes(5), "settings-before-change");
        Assert.Equal("pending", control.TestOutcome(eventId, since));
    }

    [Fact]
    public async Task 测试通知条目级确定失败直接报失败并给出原因_结果不明的继续等()
    {
        string root = TestTemp.NewDir("notify-test-failed");
        var control = new NotifyControlPlane(new ProductConfigStore(root), root, new FeishuBotCredentials(root), () => null);
        DateTimeOffset since = DateTimeOffset.UtcNow.AddSeconds(-5);
        string eventId = control.EnqueueTest();
        NotifySendResult next = NotifySendResult.ItemFailure("recipient_invalid") with { ErrorCode = 230001, LogId = "log-test" };
        var notifier = new CompletionNotifier(control.EventsDirectory, Path.Combine(root, "completion-notify-seen.json"),
            (_, _, _, _) => Task.FromResult(next), sendSpacing: TimeSpan.Zero);

        await notifier.SweepAsync("ou_test");

        Assert.Equal("failed", control.TestOutcome(eventId, since));
        Assert.Contains("recipient_invalid", control.TestFailure(eventId));
        Assert.Contains("230001", control.TestFailure(eventId));
    }

    [Fact]
    public async Task 撤回与发送互斥_先撤回的不发_正在发的撤不回()
    {
        string root = TestTemp.NewDir("notify-test-claim");
        var control = new NotifyControlPlane(new ProductConfigStore(root), root, new FeishuBotCredentials(root), () => null);
        string first = control.EnqueueTest();
        string second = control.EnqueueTest();
        var sentKeys = new List<string>();
        bool? withdrawnWhileSending = null;
        var notifier = new CompletionNotifier(control.EventsDirectory, Path.Combine(root, "completion-notify-seen.json"),
            (_, _, key, _) =>
            {
                sentKeys.Add(key);
                // 发第一条的时候:它自己撤不回;还没轮到的那一条撤得回，这一轮也就不再发它。
                withdrawnWhileSending ??= control.WithdrawTest(key);
                Assert.True(control.WithdrawTest(key == first ? second : first));
                return Task.FromResult(NotifySendResult.Delivered());
            },
            sendSpacing: TimeSpan.Zero);

        NotifySweepResult result = await notifier.SweepAsync("ou_test");

        Assert.False(withdrawnWhileSending);
        Assert.Single(sentKeys);
        Assert.Equal(1, result.Sent);
        Assert.Contains(result.Items, item => item.Outcome == NotifyOutcome.Suppressed && item.Detail == "withdrawn");
        Assert.Equal("delivered", control.TestOutcome(sentKeys[0], DateTimeOffset.UtcNow.AddMinutes(-1)));
    }

    [Fact]
    public void 没送出去的测试通知可以撤回_事件ID只接受十六位十六进制()
    {
        string root = TestTemp.NewDir("notify-withdraw");
        var control = new NotifyControlPlane(new ProductConfigStore(root), root, new FeishuBotCredentials(root), () => null);
        string eventId = control.EnqueueTest();

        Assert.True(control.WithdrawTest(eventId));
        Assert.False(File.Exists(Path.Combine(control.EventsDirectory, eventId + ".json")));
        Assert.False(control.WithdrawTest(eventId));
        Assert.Throws<ArgumentException>(() => control.WithdrawTest(@"..\config"));
        Assert.Throws<ArgumentException>(() => control.TestOutcome("not-hex-at-all!!", DateTimeOffset.UtcNow));
    }

    [Fact]
    public void doctor说明里的应用与用户标识一律遮蔽()
    {
        // 1.0.81 录制形状:app_resolved 的 message 原样带出完整 app_id(此处为合成值)。
        const string stdout = """{"checks":[{"name":"app_resolved","status":"pass","message":"app: cli_test0000000000aa (feishu)"},{"name":"user_identity","status":"pass","message":"user ou_0123456789abcdef0123456789abcdef ok"}],"ok":true}""";

        LarkDoctorReport report = LarkDoctorReport.Parse(stdout, "keychain");

        Assert.Equal("app: cli_te…aa (feishu)", report.Checks[0].Message);
        Assert.Equal("user ou_0123…cdef ok", report.Checks[1].Message);
        Assert.DoesNotContain("cli_test0000000000aa", string.Join(" ", report.Checks.Select(check => check.Message)));
    }

    [Theory]
    [InlineData(80, null)]
    [InlineData(70, "login-expiring")]
    [InlineData(20, "login-expiring")]
    [InlineData(-1, "login-expired")]
    public void Claude登录到期前按阈值各提醒一次(int hoursLeft, string? expected)
    {
        DateTimeOffset now = new(2026, 11, 8, 6, 0, 0, TimeSpan.Zero);
        var notices = new List<ResumeNotice>();
        var reminder = new ClaudeLoginReminder(
            () => new ClaudeCredentialStatus(ClaudeLoginVerdict.Ok, now.AddHours(5), now.AddHours(hoursLeft), "max", true, null),
            new ListNotices(notices), NullLogger<ClaudeLoginReminder>.Instance, () => true, () => now);

        Assert.Equal(expected, reminder.CheckOnce());
        Assert.Null(reminder.CheckOnce());
        Assert.Equal(expected is null ? 0 : 1, notices.Count);
        if (expected is not null)
        {
            Assert.Contains("到期", notices[0].Detail);
            Assert.Equal(string.Empty, notices[0].ProjectPath);
        }
    }

    [Fact]
    public void 登录提醒写进队列失败时撤回去重键_下一次检查重写()
    {
        DateTimeOffset now = new(2026, 11, 8, 6, 0, 0, TimeSpan.Zero);
        var notices = new List<ResumeNotice>();
        var flaky = new FlakyNotices(notices) { FailNext = 1 };
        var reminder = new ClaudeLoginReminder(
            () => new ClaudeCredentialStatus(ClaudeLoginVerdict.LoginExpiring, now.AddHours(5), now.AddHours(20), "max", true, null),
            flaky, NullLogger<ClaudeLoginReminder>.Instance, () => true, () => now);

        Assert.Throws<IOException>(() => reminder.CheckOnce());
        Assert.Empty(notices);
        Assert.Equal("login-expiring", reminder.CheckOnce());
        Assert.Single(notices);
        Assert.Null(reminder.CheckOnce());
    }

    [Fact]
    public void 没设置收件人时不写登录提醒()
    {
        var notices = new List<ResumeNotice>();
        var reminder = new ClaudeLoginReminder(
            () => new ClaudeCredentialStatus(ClaudeLoginVerdict.LoginExpiring, null, DateTimeOffset.UtcNow.AddHours(3), "max", true, null),
            new ListNotices(notices), NullLogger<ClaudeLoginReminder>.Instance, () => false);

        Assert.Null(reminder.CheckOnce());
        Assert.Empty(notices);
    }

    /// <summary>
    /// 假 lark-cli(照 1.0.81 实测行为):auth list 输出一个用户;其它调用每次记一行「命令|APP_ID|CONFIG_DIR」。env 来源只认现成令牌——
    /// 环境里只有 App Secret 时一律回 token_missing(真 lark-cli 不会拿它去换);有令牌却没有 App ID 时报错。
    /// api(机器人信息接口):Good / DoctorFail 成功且 activate_status=2,Inactive 成功但 activate_status=1,NoBot 回授权错误
    /// (NotificationDeliveryTests 里 1.0.81 录制的 authorization/missing_scope 形状),其余回 99991663。
    /// doctor:Good 或未注入凭据(钥匙串)报 bot 就绪,DoctorFail 输出不认识的格式，其余 verify failed。
    /// </summary>
    private static string FakeLarkCli(string root)
    {
        string path = Path.Combine(root, "lark-cli-fake.cmd");
        static string BotInfo(int status) => "{\"code\":0,\"msg\":\"ok\",\"bot\":{\"activate_status\":" + status +
                                             ",\"app_name\":\"test-bot\",\"open_id\":\"ou_fedcba9876543210fedcba9876543210\"}}";
        File.WriteAllText(path, string.Join("\r\n",
            "@echo off",
            "if \"%1\"==\"auth\" (",
            "  echo " + Users,
            "  exit /b 0",
            ")",
            "echo %1^|%LARKSUITE_CLI_APP_ID%^|%LARKSUITE_CLI_CONFIG_DIR%>> \"%~dp0calls.txt\"",
            "if defined LARKSUITE_CLI_APP_SECRET (",
            "  echo " + FeishuTokenStub.TokenMissingEnvelope + " 1>&2",
            "  exit /b 3",
            ")",
            "if defined LARKSUITE_CLI_TENANT_ACCESS_TOKEN if not defined LARKSUITE_CLI_APP_ID (",
            "  echo {\"ok\":false,\"error\":{\"type\":\"config\",\"message\":\"LARKSUITE_CLI_TENANT_ACCESS_TOKEN is set but LARKSUITE_CLI_APP_ID is missing\"}} 1>&2",
            "  exit /b 3",
            ")",
            "if \"%1\"==\"api\" goto api",
            "if \"%LARKSUITE_CLI_TENANT_ACCESS_TOKEN%\"==\"" + FeishuTokenStub.Good + "\" (",
            "  echo " + Doctor,
            "  exit /b 0",
            ")",
            "if \"%LARKSUITE_CLI_TENANT_ACCESS_TOKEN%\"==\"" + FeishuTokenStub.DoctorFail + "\" (",
            "  echo not-a-doctor-report",
            "  exit /b 0",
            ")",
            "if defined LARKSUITE_CLI_TENANT_ACCESS_TOKEN (",
            "  echo " + DoctorFail,
            "  exit /b 0",
            ")",
            "echo " + Doctor,
            "exit /b 0",
            ":api",
            "if \"%LARKSUITE_CLI_TENANT_ACCESS_TOKEN%\"==\"" + FeishuTokenStub.Good + "\" (",
            "  echo " + BotInfo(2),
            "  exit /b 0",
            ")",
            "if \"%LARKSUITE_CLI_TENANT_ACCESS_TOKEN%\"==\"" + FeishuTokenStub.DoctorFail + "\" (",
            "  echo " + BotInfo(2),
            "  exit /b 0",
            ")",
            "if \"%LARKSUITE_CLI_TENANT_ACCESS_TOKEN%\"==\"" + FeishuTokenStub.Inactive + "\" (",
            "  echo " + BotInfo(1),
            "  exit /b 0",
            ")",
            "if \"%LARKSUITE_CLI_TENANT_ACCESS_TOKEN%\"==\"" + FeishuTokenStub.NoBot + "\" (",
            "  echo {\"ok\":false,\"identity\":\"bot\",\"error\":{\"type\":\"authorization\",\"subtype\":\"missing_scope\",\"message\":\"m\"}} 1>&2",
            "  exit /b 3",
            ")",
            "echo " + FeishuTokenStub.TokenInvalidEnvelope + " 1>&2",
            "exit /b 3",
            string.Empty));
        return path;
    }

    private sealed class ListNotices(List<ResumeNotice> items) : IResumeNotices
    {
        public void Post(ResumeNotice notice) => items.Add(notice);
    }

    /// <summary>接下来 FailNext 次写入抛 IOException(杀软占用、磁盘满)。</summary>
    private sealed class FlakyNotices(List<ResumeNotice> items) : IResumeNotices
    {
        public int FailNext { get; set; }

        public void Post(ResumeNotice notice)
        {
            if (FailNext > 0)
            {
                FailNext--;
                throw new IOException("模拟写入失败");
            }

            items.Add(notice);
        }
    }
}
