using System.Globalization;
using System.Text.Json;
using AiResume.Core;
using AiResume.Storage;
using AiResume.Worker.Notifications;
using Xunit;

namespace AiResume.Tests;

/// <summary>
/// Claude Code 通知的三条新规则(2026-11-02):不落盘的会话不发、同一会话同一限额周期只提醒一次(同时待发的合成一条)、
/// 续跑句按投递那一刻的布防快照说。事实一律经假 <see cref="INotifyFacts"/> 注入,不读真实 ~/.claude,不发网络请求。
/// 背景:第三方用量监视器每 8 小时以 --no-session-persistence 拉起 claude 刷新令牌,周额度用完的两天里
/// 每次都触发 StopFailure,飞书收到一串「Default Project 因用量上限中断…会按布防自动续跑」,而当时根本没布防。
/// </summary>
public sealed class LimitNotificationTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 30, 12, 0, 0, TimeSpan.Zero);
    private static readonly ArmSnapshot NotArmed = new(false, false, false, []);

    [Fact]
    public async Task 会话没落盘的Claude事件不发_稳定期内先等_落盘的与判断不了的照发()
    {
        string dir = TestTemp.NewDir("notify-persist");
        string events = Path.Combine(dir, "events");
        var facts = new FakeFacts();
        facts.Transcripts[@"C:\t\ghost-done.jsonl"] = false;
        facts.Transcripts[@"C:\t\ghost-limit.jsonl"] = false;
        facts.Transcripts[@"C:\t\fresh.jsonl"] = false;
        facts.Transcripts[@"C:\t\unknown.jsonl"] = null;
        Write(events, "1000000000000001", "Default Project", Now.AddMinutes(-3), "g1", transcript: @"C:\t\ghost-done.jsonl");
        Write(events, "1000000000000002", "Default Project", Now.AddMinutes(-2), "g2", kind: "failure", error: "rate_limit",
            transcript: @"C:\t\ghost-limit.jsonl");
        Write(events, "1000000000000003", "Default Project", Now.AddSeconds(-5), "g3", transcript: @"C:\t\fresh.jsonl");
        Write(events, "1000000000000004", "ledger-api", Now.AddMinutes(-1), "k1", transcript: @"C:\t\real.jsonl");
        Write(events, "1000000000000005", "atlas-docs", Now.AddMinutes(-1), "k2", transcript: @"C:\t\unknown.jsonl");
        // 升级前 Hook 写的旧事件没有 transcriptPath:不知道就照发。
        Write(events, "1000000000000006", "pixel-sprites", Now.AddMinutes(-1), "k3", transcript: null);
        var sent = new List<(string Key, string Text)>();
        DateTimeOffset now = Now;
        CompletionNotifier notifier = NewNotifier(dir, events, facts, sent, () => now);

        NotifySweepResult result = await notifier.SweepAsync("ou_test");

        Assert.Equal(new[] { "1000000000000004", "1000000000000005", "1000000000000006" }, sent.Select(s => s.Key));
        Assert.Contains(result.Items, i => i.EventId == "1000000000000001" && i.Outcome == NotifyOutcome.Suppressed && i.Detail == "session_not_persisted");
        Assert.Contains(result.Items, i => i.EventId == "1000000000000002" && i.Outcome == NotifyOutcome.Suppressed && i.Detail == "session_not_persisted");
        Assert.Contains(result.Items, i => i.EventId == "1000000000000003" && i.Outcome == NotifyOutcome.Deferred && i.Detail == "settling");
        Assert.False(File.Exists(Path.Combine(events, "1000000000000001.json")));
        Assert.False(File.Exists(Path.Combine(events, "1000000000000002.json")));
        Assert.True(File.Exists(Path.Combine(events, "1000000000000003.json")), "稳定期内转录可能还没写出来,先等");

        now = Now.AddSeconds(30);
        result = await notifier.SweepAsync("ou_test");
        Assert.Contains(result.Items, i => i.EventId == "1000000000000003" && i.Detail == "session_not_persisted");
        Assert.Equal(3, sent.Count);
    }

    [Fact]
    public async Task 同一会话同一限额周期只提醒一次_同时撞上的合成一条_别的会话撞上同周期照样提醒()
    {
        string dir = TestTemp.NewDir("notify-cycle");
        string events = Path.Combine(dir, "events");
        DateTimeOffset resets = Now.AddDays(2);
        var facts = new FakeFacts { Arm = NotArmed };
        facts.Limits[@"C:\t\a.jsonl"] = new LimitFacts("seven_day", resets, "claude-opus-5-5");
        facts.Limits[@"C:\t\b.jsonl"] = new LimitFacts("seven_day", resets, "claude-opus-5-5");
        Write(events, "2000000000000001", "AI Resume", Now.AddSeconds(-60), "sa", kind: "failure", error: "rate_limit", transcript: @"C:\t\a.jsonl");
        Write(events, "2000000000000002", "月会PPT", Now.AddSeconds(-30), "sb", kind: "failure", error: "rate_limit", transcript: @"C:\t\b.jsonl");
        var sent = new List<(string Key, string Text)>();
        DateTimeOffset now = Now;
        CompletionNotifier notifier = NewNotifier(dir, events, facts, sent, () => now);

        NotifySweepResult result = await notifier.SweepAsync("ou_test");

        (string key, string text) = Assert.Single(sent);
        Assert.Equal("2000000000000001", key);
        Assert.Equal(
            "⏸️ 2 个会话被用量上限打断:AI Resume、月会PPT\n" +
            $"Claude Code · Opus 5.5 · {Local(Now.AddSeconds(-60))}\n" +
            $"周额度已用完,预计 {Moment(resets, Now)} 重置\n" +
            "AI Resume 未布防,不会自动续跑;额度恢复后在原会话或手机远程里继续",
            text);
        Assert.Contains(result.Items, i => i.EventId == "2000000000000002" && i.Outcome == NotifyOutcome.Superseded && i.Detail == "merged");
        Assert.Empty(Directory.GetFiles(events, "*.json"));

        // 8 小时后:AI Resume 那个会话在同一周期又撞上(手动重试)→ 不再发;
        // 新会话「专利」撞上同一周期(连同原生自动继续放弃)→ 只说它这一个。
        now = Now.AddHours(8);
        facts.Limits[@"C:\t\c.jsonl"] = new LimitFacts("seven_day", resets, null);
        Write(events, "2000000000000003", "AI Resume", now.AddMinutes(-2), "sa", kind: "failure", error: "rate_limit", transcript: @"C:\t\a.jsonl");
        Write(events, "2000000000000004", "专利", now.AddMinutes(-1), "sc", kind: "failure", error: "rate_limit", transcript: @"C:\t\c.jsonl");
        Write(events, "2000000000000005", "专利", now.AddSeconds(-50), "sc", kind: "decision", notificationType: "quota_auto_resume_disabled",
            transcript: @"C:\t\c.jsonl");
        result = await notifier.SweepAsync("ou_test");
        Assert.Equal(2, sent.Count);
        Assert.Contains(result.Items, i => i.EventId == "2000000000000003" && i.Outcome == NotifyOutcome.Suppressed && i.Detail == "same_limit_cycle");
        Assert.Equal("2000000000000004", sent[1].Key);
        Assert.StartsWith("⏸️ 专利 被用量上限打断\n", sent[1].Text, StringComparison.Ordinal);
        Assert.EndsWith("AI Resume 未布防,不会自动续跑;Claude Code 也不会自己继续;额度恢复后在原会话或手机远程里继续", sent[1].Text, StringComparison.Ordinal);
        Assert.Contains(result.Items, i => i.EventId == "2000000000000005" && i.Outcome == NotifyOutcome.Superseded && i.Detail == "merged");
        Assert.Empty(Directory.GetFiles(events, "*.json"));

        now = Now.AddHours(9);
        Write(events, "2000000000000006", "专利", now.AddMinutes(-1), "sc", kind: "failure", error: "rate_limit", transcript: @"C:\t\c.jsonl");
        result = await notifier.SweepAsync("ou_test");
        Assert.Equal(2, sent.Count);
        Assert.Contains(result.Items, i => i.EventId == "2000000000000006" && i.Detail == "same_limit_cycle");

        // 周额度重置之后又撞上 5 小时额度:新周期,再提醒一次;过期的周期记账被剪掉。
        now = resets.AddHours(2);
        facts.Limits[@"C:\t\a.jsonl"] = new LimitFacts("five_hour", resets.AddHours(5), "claude-opus-5-5");
        Write(events, "2000000000000007", "AI Resume", now.AddMinutes(-1), "sa", kind: "failure", error: "rate_limit", transcript: @"C:\t\a.jsonl");
        await notifier.SweepAsync("ou_test");
        Assert.Equal(3, sent.Count);
        Assert.StartsWith("⏸️ AI Resume 被用量上限打断\n", sent[2].Text, StringComparison.Ordinal);
        Assert.Contains($"5 小时额度已用完,预计 {Moment(resets.AddHours(5), now)} 重置", sent[2].Text);
        string ledger = File.ReadAllText(Path.Combine(dir, "completion-notify-limits.json"));
        Assert.DoesNotContain("seven_day", ledger);
        Assert.Contains("five_hour", ledger);
    }

    [Fact]
    public async Task 读不到重置时间时按会话记账五小时_也不断言是用量上限()
    {
        string dir = TestTemp.NewDir("notify-session-cycle");
        string events = Path.Combine(dir, "events");
        var facts = new FakeFacts { Arm = NotArmed };
        var sent = new List<(string Key, string Text)>();
        DateTimeOffset now = Now;
        CompletionNotifier notifier = NewNotifier(dir, events, facts, sent, () => now);

        Write(events, "3000000000000001", "ledger-api", Now.AddMinutes(-1), "s1", kind: "failure", error: "rate_limit", transcript: @"C:\t\s1.jsonl");
        await notifier.SweepAsync("ou_test");
        Assert.Single(sent);
        Assert.StartsWith("⏸️ ledger-api 被限额打断\n", sent[0].Text, StringComparison.Ordinal);
        Assert.Contains("\n是哪个额度、何时恢复未核实\n", sent[0].Text);
        Assert.DoesNotContain("已用完", sent[0].Text);

        now = Now.AddHours(1);
        Write(events, "3000000000000002", "ledger-api", now.AddMinutes(-1), "s1", kind: "failure", error: "rate_limit", transcript: @"C:\t\s1.jsonl");
        Write(events, "3000000000000003", "atlas-docs", now.AddMinutes(-1), "s2", kind: "failure", error: "rate_limit", transcript: @"C:\t\s2.jsonl");
        NotifySweepResult result = await notifier.SweepAsync("ou_test");
        Assert.Contains(result.Items, i => i.EventId == "3000000000000002" && i.Detail == "same_limit_cycle");
        Assert.Equal(new[] { "3000000000000001", "3000000000000003" }, sent.Select(s => s.Key));

        now = Now.AddHours(6);
        Write(events, "3000000000000004", "ledger-api", now.AddMinutes(-1), "s1", kind: "failure", error: "rate_limit", transcript: @"C:\t\s1.jsonl");
        await notifier.SweepAsync("ou_test");
        Assert.Equal("3000000000000004", sent[^1].Key);
    }

    [Fact]
    public async Task 刚到的中断先不判_事件晚于转录里的重置时间时那份证据作废_不被旧周期误删()
    {
        string dir = TestTemp.NewDir("notify-stale-facts");
        string events = Path.Combine(dir, "events");
        DateTimeOffset reset = Now.AddMinutes(50);
        var facts = new FakeFacts { Arm = NotArmed };
        facts.Limits[@"C:\t\a.jsonl"] = new LimitFacts("five_hour", reset, null);
        Write(events, "4100000000000001", "ledger-api", Now.AddMinutes(-1), "s1", kind: "failure", error: "rate_limit", transcript: @"C:\t\a.jsonl");
        var sent = new List<(string Key, string Text)>();
        DateTimeOffset now = Now;
        CompletionNotifier notifier = NewNotifier(dir, events, facts, sent, () => now);
        await notifier.SweepAsync("ou_test");
        Assert.Single(sent);

        // 5 小时额度重置后原生自动继续又撞上周额度,而转录尾部仍是那条 5 小时记录(新的还没写,或根本没写)。
        now = reset.AddMinutes(5);
        Write(events, "4100000000000002", "ledger-api", now.AddSeconds(-5), "s1", kind: "failure", error: "rate_limit", transcript: @"C:\t\a.jsonl");
        NotifySweepResult result = await notifier.SweepAsync("ou_test");
        Assert.Contains(result.Items, i => i.EventId == "4100000000000002" && i.Outcome == NotifyOutcome.Deferred && i.Detail == "settling");
        Assert.True(File.Exists(Path.Combine(events, "4100000000000002.json")), "满稳定期之前不按旧周期删");

        now = reset.AddMinutes(5).AddSeconds(30);
        await notifier.SweepAsync("ou_test");
        Assert.Equal(2, sent.Count);
        Assert.StartsWith("⏸️ ledger-api 被限额打断\n", sent[1].Text, StringComparison.Ordinal);
        Assert.Contains("\n是哪个额度、何时恢复未核实\n", sent[1].Text);
    }

    [Fact]
    public async Task 两个配置根恰好同一时刻重置_各提醒各的()
    {
        string dir = TestTemp.NewDir("notify-roots");
        string events = Path.Combine(dir, "events");
        DateTimeOffset resets = Now.AddDays(1);
        var facts = new FakeFacts { Arm = NotArmed };
        const string a = @"C:\cfgA\projects\C--work-a-proj\11111111-1111-1111-1111-111111111111.jsonl";
        const string b = @"C:\cfgB\projects\C--work-b-proj\22222222-2222-2222-2222-222222222222.jsonl";
        facts.Limits[a] = new LimitFacts("seven_day", resets, null);
        facts.Limits[b] = new LimitFacts("seven_day", resets, null);
        Write(events, "4200000000000001", "a-proj", Now.AddSeconds(-60), "sa", kind: "failure", error: "rate_limit", transcript: a);
        Write(events, "4200000000000002", "b-proj", Now.AddSeconds(-50), "sb", kind: "failure", error: "rate_limit", transcript: b);
        var sent = new List<(string Key, string Text)>();
        CompletionNotifier notifier = NewNotifier(dir, events, facts, sent, () => Now);

        await notifier.SweepAsync("ou_test");

        Assert.Equal(new[] { "4200000000000001", "4200000000000002" }, sent.Select(s => s.Key));
        Assert.StartsWith("⏸️ a-proj 被用量上限打断\n", sent[0].Text, StringComparison.Ordinal);
        Assert.StartsWith("⏸️ b-proj 被用量上限打断\n", sent[1].Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 组还在陆续撞墙时先攒着_最早一条满两分钟就发_后到的单独再发()
    {
        string dir = TestTemp.NewDir("notify-group-ready");
        string events = Path.Combine(dir, "events");
        DateTimeOffset resets = Now.AddHours(3);
        var facts = new FakeFacts { Arm = NotArmed };
        foreach (string name in new[] { "a", "b", "c" })
        {
            facts.Limits[$@"C:\t\{name}.jsonl"] = new LimitFacts("five_hour", resets, null);
        }

        Write(events, "4000000000000001", "a-proj", Now.AddSeconds(-100), "sa", kind: "failure", error: "rate_limit", transcript: @"C:\t\a.jsonl");
        Write(events, "4000000000000002", "b-proj", Now.AddSeconds(-5), "sb", kind: "failure", error: "rate_limit", transcript: @"C:\t\b.jsonl");
        var sent = new List<(string Key, string Text)>();
        DateTimeOffset now = Now;
        CompletionNotifier notifier = NewNotifier(dir, events, facts, sent, () => now);

        NotifySweepResult result = await notifier.SweepAsync("ou_test");
        Assert.Empty(sent);
        Assert.Contains(result.Items, i => i.EventId == "4000000000000001" && i.Detail == "settling");
        Assert.Contains(result.Items, i => i.EventId == "4000000000000002" && i.Detail == "settling");

        // 又有一个会话刚撞上,但最早那条已满两分钟:不再无限推迟,把已经满稳定期的一起发出。
        now = Now.AddSeconds(25);
        Write(events, "4000000000000003", "c-proj", now.AddSeconds(-1), "sc", kind: "failure", error: "rate_limit", transcript: @"C:\t\c.jsonl");
        await notifier.SweepAsync("ou_test");
        (string key, string text) = Assert.Single(sent);
        Assert.Equal("4000000000000001", key);
        Assert.StartsWith("⏸️ 2 个会话被用量上限打断:a-proj、b-proj\n", text, StringComparison.Ordinal);

        now = Now.AddSeconds(50);
        await notifier.SweepAsync("ou_test");
        Assert.Equal(2, sent.Count);
        Assert.StartsWith("⏸️ c-proj 被用量上限打断\n", sent[1].Text, StringComparison.Ordinal);
    }

    private const string WillResume = "已布防:额度恢复后 AI Resume 会续跑它;会话还开着的话 AI Resume 不插手,关掉后才续";
    private const string NeedsRearm = "AI Resume 要重新布防才会续跑它;在控制面解除并重新布防";
    private const string Unverified = "AI Resume 布防状态未核实,不确定会不会自动续跑";

    public static TheoryData<string, string> Outlooks => new()
    {
        { "unknown", Unverified },
        { "throws", Unverified },
        { "stalled", Unverified },
        { "not-armed", "AI Resume 未布防,不会自动续跑;额度恢复后在原会话或手机远程里继续" },
        { "bound", WillResume },
        { "bound-handled-once", "一次性布防这一轮已处理过它,AI Resume 不会再续跑;额度恢复后在原会话或手机远程里继续" },
        { "bound-handled-continuous", WillResume },
        { "other-session", "AI Resume 布防的是这个项目的另一个会话,不会续跑这一个;额度恢复后在原会话或手机远程里继续" },
        { "not-in-scope", "不在 AI Resume 布防范围,不会自动续跑;额度恢复后在原会话或手机远程里继续" },
        { "stopped", "AI Resume 本轮布防已停止,不会再自动续跑;检查后在控制面解除并重新布防" },
        { "rearm", NeedsRearm },
        { "project-rearm", NeedsRearm },
        { "legacy-unbound", NeedsRearm },
        { "login", "已布防,但 AI Resume 读不到 Claude 登录,恢复前不会续跑它;在终端运行 claude 并执行 /login" },
    };

    [Theory]
    [MemberData(nameof(Outlooks))]
    public async Task 续跑句按投递时的布防快照说_引擎只会一直等的就不说会续跑(string arm, string expected)
    {
        string dir = TestTemp.NewDir("notify-outlook");
        string events = Path.Combine(dir, "events");
        const string session = "11111111-1111-1111-1111-111111111111";
        const string project = @"C:\work\ledger-api";
        var bound = new ArmedProject(project, session, false);
        var facts = new FakeFacts
        {
            ThrowOnArm = arm == "throws",
            Arm = arm switch
            {
                "not-armed" => NotArmed,
                "bound" => new ArmSnapshot(true, false, false, [bound]),
                "bound-handled-once" => new ArmSnapshot(true, false, false, [bound with { Handled = true }]),
                "bound-handled-continuous" => new ArmSnapshot(true, true, false, [bound with { Handled = true }]),
                "other-session" => new ArmSnapshot(true, false, false, [new ArmedProject(project + @"\", "22222222-2222-2222-2222-222222222222", false)]),
                "not-in-scope" => new ArmSnapshot(true, false, false, [new ArmedProject(@"C:\work\atlas-docs", "33333333-3333-3333-3333-333333333333", false)]),
                "stopped" => new ArmSnapshot(true, false, true, [bound]),
                "rearm" => new ArmSnapshot(true, false, false, [bound]) { Hold = ArmSnapshot.HoldRearm },
                "project-rearm" => new ArmSnapshot(true, false, false, [bound with { NeedsRearm = true }]),
                "legacy-unbound" => new ArmSnapshot(true, false, false, [new ArmedProject(project, string.Empty, false)]),
                "login" => new ArmSnapshot(true, false, false, [bound]) { Hold = ArmSnapshot.HoldLogin },
                "stalled" => new ArmSnapshot(true, false, false, [bound]) { Hold = ArmSnapshot.HoldStalled },
                _ => null,
            },
        };
        facts.Limits[@"C:\t\x.jsonl"] = new LimitFacts("seven_day", Now.AddDays(3), "claude-fable-5-1");
        Write(events, "5000000000000001", "ledger-api", Now.AddMinutes(-1), session, kind: "failure", error: "rate_limit", transcript: @"C:\t\x.jsonl");
        var sent = new List<(string Key, string Text)>();
        CompletionNotifier notifier = NewNotifier(dir, events, facts, sent, () => Now);

        await notifier.SweepAsync("ou_test");

        string[] lines = Assert.Single(sent).Text.Split('\n');
        Assert.Equal(4, lines.Length);
        Assert.Equal("⏸️ ledger-api 被用量上限打断", lines[0]);
        Assert.Equal($"Claude Code · Fable 5.1 · {Local(Now.AddMinutes(-1))}", lines[1]);
        Assert.Equal(expected, lines[3]);
        Assert.DoesNotContain("会按布防自动续跑", sent[0].Text);
    }

    [Fact]
    public async Task 原生自动继续放弃与中断合成一条_单独到达也按限额模板说()
    {
        string dir = TestTemp.NewDir("notify-autoresume");
        string events = Path.Combine(dir, "events");
        const string session = "11111111-1111-1111-1111-111111111111";
        var facts = new FakeFacts { Arm = new ArmSnapshot(true, false, false, [new ArmedProject(@"C:\work\ledger-api", session, false)]) };
        facts.Limits[@"C:\t\x.jsonl"] = new LimitFacts("seven_day", Now.AddDays(3), null);
        Write(events, "6000000000000001", "ledger-api", Now.AddSeconds(-40), session, kind: "failure", error: "rate_limit", transcript: @"C:\t\x.jsonl");
        Write(events, "6000000000000002", "ledger-api", Now.AddSeconds(-35), session, kind: "decision",
            notificationType: "quota_auto_resume_disabled", transcript: @"C:\t\x.jsonl");
        var sent = new List<(string Key, string Text)>();
        DateTimeOffset now = Now;
        CompletionNotifier notifier = NewNotifier(dir, events, facts, sent, () => now);

        await notifier.SweepAsync("ou_test");

        (string key, string text) = Assert.Single(sent);
        Assert.Equal("6000000000000001", key);
        Assert.StartsWith("⏸️ ledger-api 被用量上限打断\n", text, StringComparison.Ordinal);
        Assert.EndsWith("已布防:额度恢复后 AI Resume 会续跑它;Claude Code 不会自己继续,会话还开着的话要先关掉", text, StringComparison.Ordinal);

        // 另一个会话只收到「不会自己继续」(StopFailure 已在别处处理过):仍按限额模板说,不再是「自动继续未开启」。
        now = Now.AddHours(1);
        facts.Arm = NotArmed;
        facts.Limits[@"C:\t\y.jsonl"] = new LimitFacts("five_hour", Now.AddHours(3), null);
        Write(events, "6000000000000003", "atlas-docs", now.AddMinutes(-1), "s2", kind: "decision", notificationType: "quota_auto_resume_disabled",
            transcript: @"C:\t\y.jsonl");
        await notifier.SweepAsync("ou_test");
        Assert.Equal(2, sent.Count);
        Assert.StartsWith("⏸️ atlas-docs 被用量上限打断\n", sent[1].Text, StringComparison.Ordinal);
        Assert.EndsWith("AI Resume 未布防,不会自动续跑;Claude Code 也不会自己继续;额度恢复后在原会话或手机远程里继续", sent[1].Text, StringComparison.Ordinal);
        Assert.DoesNotContain("自动继续未开启", sent[1].Text);
    }

    [Fact]
    public async Task 多个会话状态不一时逐行说清()
    {
        string dir = TestTemp.NewDir("notify-mixed");
        string events = Path.Combine(dir, "events");
        const string bound = "11111111-1111-1111-1111-111111111111";
        var facts = new FakeFacts { Arm = new ArmSnapshot(true, false, false, [new ArmedProject(@"C:\work\ledger-api", bound, false)]) };
        facts.Names[bound] = "v2.2 收尾";
        facts.Limits[@"C:\t\a.jsonl"] = new LimitFacts("seven_day", Now.AddDays(3), "claude-opus-5-5");
        facts.Limits[@"C:\t\b.jsonl"] = new LimitFacts("seven_day", Now.AddDays(3), "claude-fable-5-1");
        Write(events, "7000000000000001", "ledger-api", Now.AddSeconds(-50), bound, kind: "failure", error: "rate_limit", transcript: @"C:\t\a.jsonl");
        Write(events, "7000000000000002", "专利", Now.AddSeconds(-45), "s2", kind: "failure", error: "rate_limit", transcript: @"C:\t\b.jsonl");
        var sent = new List<(string Key, string Text)>();
        CompletionNotifier notifier = NewNotifier(dir, events, facts, sent, () => Now);

        await notifier.SweepAsync("ou_test");

        string[] lines = Assert.Single(sent).Text.Split('\n');
        Assert.Equal("⏸️ 2 个会话被用量上限打断:ledger-api、专利", lines[0]);
        Assert.Equal($"Claude Code · {Local(Now.AddSeconds(-50))}", lines[1]);
        Assert.Equal("· ledger-api「v2.2 收尾」:额度恢复后会自动续跑(开着的话关掉后才续)", lines[3]);
        Assert.Equal("· 专利:不在布防范围,不会自动续跑", lines[4]);
        Assert.Equal("不会自动续跑的会话,额度恢复后在原会话或手机远程里继续", lines[5]);
    }

    [Fact]
    public async Task 会话名放在第二行_清洗换行_临时工作区用会话名代替目录名()
    {
        string dir = TestTemp.NewDir("notify-names");
        string events = Path.Combine(dir, "events");
        var facts = new FakeFacts();
        facts.Names["s1"] = "v2.2 收尾";
        facts.Names["s2"] = "整理周报";
        facts.Names["s4"] = "一个非常非常长的会话名字超过了二十个字的上限";
        facts.Names["s5"] = "第一行\n第二行\u2028第三行";
        const string scratch = @"C:\Users\u\AppData\Local\Claude-Data\claude_x\scratch-workspaces\g1\g2\scratch-2026-10-29-03a48e";
        Write(events, "8000000000000001", "ledger-api", Now.AddMinutes(-7), "s1", transcript: @"C:\t\1.jsonl");
        WriteAt(events, "8000000000000002", scratch, Now.AddMinutes(-6), "s2", @"C:\t\2.jsonl");
        WriteAt(events, "8000000000000003", scratch, Now.AddMinutes(-5), "s3", @"C:\t\3.jsonl");
        Write(events, "8000000000000004", "ledger-api", Now.AddMinutes(-4), "s4", transcript: @"C:\t\4.jsonl");
        Write(events, "8000000000000005", "ledger-api", Now.AddMinutes(-3), "s5", transcript: @"C:\t\5.jsonl");
        // 用户自建的同名目录不算临时工作区:要末级形如 scratch-日期-编号、上级又有 scratch-workspaces。
        WriteAt(events, "8000000000000006", @"C:\work\scratch-workspaces\notes", Now.AddMinutes(-2), "s6", @"C:\t\6.jsonl");
        WriteAt(events, "8000000000000007", @"C:\Users\u\scratch-2026-10-29-03a48e", Now.AddMinutes(-1), "s7", @"C:\t\7.jsonl");
        var sent = new List<(string Key, string Text)>();
        CompletionNotifier notifier = NewNotifier(dir, events, facts, sent, () => Now);

        await notifier.SweepAsync("ou_test");

        Assert.Equal(7, sent.Count);
        Assert.Equal($"✅ ledger-api 已完成\nClaude Code · {Local(Now.AddMinutes(-7))} · 「v2.2 收尾」", sent[0].Text);
        Assert.Equal($"✅ 整理周报 已完成\nClaude Code · {Local(Now.AddMinutes(-6))}", sent[1].Text);
        Assert.Equal($"✅ 临时会话 已完成\nClaude Code · {Local(Now.AddMinutes(-5))}", sent[2].Text);
        Assert.EndsWith(" · 「一个非常非常长的会话名字超过了二十个字…」", sent[3].Text, StringComparison.Ordinal);
        Assert.EndsWith(" · 「第一行 第二行 第三行」", sent[4].Text, StringComparison.Ordinal);
        Assert.StartsWith("✅ notes 已完成\n", sent[5].Text, StringComparison.Ordinal);
        Assert.StartsWith("✅ scratch-2026-10-29-03a48e 已完成\n", sent[6].Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 代表那条进了死信_同组其余几条一起进死信不再轮流重发()
    {
        string dir = TestTemp.NewDir("notify-group-dead");
        string events = Path.Combine(dir, "events");
        var facts = new FakeFacts { Arm = NotArmed };
        facts.Limits[@"C:\t\a.jsonl"] = new LimitFacts("seven_day", Now.AddDays(2), null);
        facts.Limits[@"C:\t\b.jsonl"] = new LimitFacts("seven_day", Now.AddDays(2), null);
        Write(events, "9000000000000001", "a-proj", Now.AddSeconds(-60), "sa", kind: "failure", error: "rate_limit", transcript: @"C:\t\a.jsonl");
        Write(events, "9000000000000002", "b-proj", Now.AddSeconds(-50), "sb", kind: "failure", error: "rate_limit", transcript: @"C:\t\b.jsonl");
        int attempts = 0;
        var notifier = new CompletionNotifier(events, Path.Combine(dir, "seen.json"),
            (_, _, _, _) => { attempts++; return Task.FromResult(NotifySendResult.ItemFailure("invalid_message", permanent: true)); },
            () => Now, sendSpacing: TimeSpan.Zero, facts: facts);

        NotifySweepResult result = await notifier.SweepAsync("ou_test");

        Assert.Equal(1, attempts);
        Assert.Contains(result.Items, i => i.EventId == "9000000000000001" && i.Outcome == NotifyOutcome.Dead);
        Assert.Contains(result.Items, i => i.EventId == "9000000000000002" && i.Outcome == NotifyOutcome.Dead);
        Assert.Empty(Directory.GetFiles(events, "*.json"));
        Assert.Equal(2, Directory.GetFiles(Path.Combine(events, "dead"), "*.json").Length);

        await notifier.SweepAsync("ou_test");
        Assert.Equal(1, attempts);
    }

    [Fact]
    public async Task 代表那条过期作废_结果不明时同周期按已提醒_从没尝试过时其余会话照发()
    {
        string dir = TestTemp.NewDir("notify-group-stale");
        string events = Path.Combine(dir, "events");
        DateTimeOffset resets = Now.AddDays(3);
        var facts = new FakeFacts { Arm = NotArmed };
        foreach (string name in new[] { "a", "b", "c", "d" })
        {
            facts.Limits[$@"C:\t\{name}.jsonl"] = new LimitFacts("seven_day", resets, null);
        }

        // 场景一:发过一次、结果不明(可能已送达),之后通道一直不通;25 小时后它过期作废。
        Write(events, "9100000000000001", "a-proj", Now.AddSeconds(-60), "sa", kind: "failure", error: "rate_limit", transcript: @"C:\t\a.jsonl");
        int calls = 0;
        DateTimeOffset now = Now;
        var notifier = new CompletionNotifier(events, Path.Combine(dir, "seen.json"),
            (_, _, _, _) => { calls++; return Task.FromResult(NotifySendResult.ChannelFailure("network_timeout", ambiguous: true)); },
            () => now, sendSpacing: TimeSpan.Zero, facts: facts);
        await notifier.SweepAsync("ou_test");
        Assert.Equal(1, calls);

        now = Now.AddHours(25);
        Write(events, "9100000000000002", "b-proj", now.AddSeconds(-60), "sb", kind: "failure", error: "rate_limit", transcript: @"C:\t\b.jsonl");
        NotifySweepResult result = await notifier.SweepAsync("ou_test");
        Assert.Equal(1, calls);
        Assert.Contains(result.Items, i => i.EventId == "9100000000000001" && i.Outcome == NotifyOutcome.Dead && i.Detail == "stale");
        Assert.Contains(result.Items, i => i.EventId == "9100000000000002" && i.Outcome == NotifyOutcome.Suppressed && i.Detail == "same_limit_cycle");

        // 场景二:一直缺收件人,从没尝试过;它过期作废,同周期新撞上的会话照发。
        string dir2 = TestTemp.NewDir("notify-group-stale2");
        string events2 = Path.Combine(dir2, "events");
        Write(events2, "9200000000000001", "c-proj", Now.AddSeconds(-60), "sc", kind: "failure", error: "rate_limit", transcript: @"C:\t\c.jsonl");
        var sent = new List<(string Key, string Text)>();
        now = Now;
        CompletionNotifier second = NewNotifier(dir2, events2, facts, sent, () => now);
        await second.SweepAsync(string.Empty);
        now = Now.AddHours(25);
        Write(events2, "9200000000000002", "d-proj", now.AddSeconds(-60), "sd", kind: "failure", error: "rate_limit", transcript: @"C:\t\d.jsonl");
        result = await second.SweepAsync("ou_test");
        Assert.Contains(result.Items, i => i.EventId == "9200000000000001" && i.Outcome == NotifyOutcome.Dead && i.Detail == "stale");
        (string key, string text) = Assert.Single(sent);
        Assert.Equal("9200000000000002", key);
        Assert.StartsWith("⏸️ d-proj 被用量上限打断\n", text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("exit-null", "⚠️ ledger-api 自动续跑未确认完成", "这一轮不会再自动续跑")]
    [InlineData("limited-side-effects", "⚠️ ledger-api 自动续跑未确认完成(撞限前已有操作)", "这一轮不会再自动续跑")]
    [InlineData("exit-3", "⚠️ ledger-api 自动续跑未确认完成(进程异常退出)", "这一轮不会再自动续跑")]
    [InlineData("stopped", "⏹️ ledger-api 自动续跑已中止", "需要时重新布防")]
    [InlineData("cancel-pending", "⚠️ ledger-api 自动续跑正在终止,进程还没退出", "确认进程退出前不会启动新的续跑")]
    public async Task 续跑没成的提醒用界面同一套说法_不露原始状态码(string detail, string heading, string advice)
    {
        string dir = TestTemp.NewDir("notify-resume-failed");
        string events = Path.Combine(dir, "events");
        Directory.CreateDirectory(events);
        File.WriteAllText(Path.Combine(events, "a100000000000001.json"), JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["version"] = 1, ["eventId"] = "a100000000000001", ["source"] = "ai-resume", ["kind"] = "resume",
            ["notificationType"] = "resume-failed", ["cwd"] = @"C:\work\ledger-api", ["model"] = "claude-opus-5-5", ["detail"] = detail,
            ["atUtc"] = Now.AddMinutes(-1).ToString("o", CultureInfo.InvariantCulture),
        }));
        var sent = new List<(string Key, string Text)>();
        CompletionNotifier notifier = NewNotifier(dir, events, new FakeFacts(), sent, () => Now);

        await notifier.SweepAsync("ou_test");

        string[] lines = Assert.Single(sent).Text.Split('\n');
        Assert.Equal(heading, lines[0]);
        Assert.Contains(advice, lines[2]);
        Assert.DoesNotContain(detail, sent[0].Text);
    }

    private static CompletionNotifier NewNotifier(string dir, string events, INotifyFacts facts, List<(string Key, string Text)> sent,
        Func<DateTimeOffset> now) =>
        new(events, Path.Combine(dir, "seen.json"),
            (_, text, key, _) => { sent.Add((key, text)); return Task.FromResult(NotifySendResult.Delivered()); },
            now, sendSpacing: TimeSpan.Zero, facts: facts);

    private static void Write(string events, string eventId, string project, DateTimeOffset at, string session, string? kind = null,
        string? notificationType = null, string? error = null, string? transcript = "auto") =>
        WriteAt(events, eventId, @"C:\work\" + project, at, session, transcript == "auto" ? $@"C:\t\{eventId}.jsonl" : transcript,
            kind, notificationType, error);

    /// <summary>与 AiResume.Hook 写出的 Claude Code 事件同形(version / eventId / source / sessionId / cwd / transcriptPath / kind …)。</summary>
    private static void WriteAt(string events, string eventId, string cwd, DateTimeOffset at, string session, string? transcript,
        string? kind = null, string? notificationType = null, string? error = null)
    {
        Directory.CreateDirectory(events);
        File.WriteAllText(Path.Combine(events, eventId + ".json"), JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["version"] = 1, ["eventId"] = eventId, ["source"] = "claudecode", ["sessionId"] = session, ["turnId"] = null, ["taskId"] = null,
            ["cwd"] = cwd, ["transcriptPath"] = transcript, ["smoke"] = false, ["kind"] = kind, ["notificationType"] = notificationType,
            ["atUtc"] = at.ToString("o", CultureInfo.InvariantCulture), ["hookEvent"] = kind == "failure" ? "StopFailure" : kind == "decision" ? "Notification" : "Stop",
            ["promptId"] = null, ["error"] = error, ["stopHookActive"] = false, ["backgroundBusy"] = false, ["bridged"] = false,
        }));
    }

    private static string Local(DateTimeOffset utc) => utc.ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture);

    private static string Moment(DateTimeOffset utc, DateTimeOffset now)
    {
        DateTimeOffset local = utc.ToLocalTime();
        DateTime today = now.ToLocalTime().Date;
        return local.Date == today ? Local(utc)
            : local.Date == today.AddDays(1) ? "明天 " + Local(utc)
            : $"{local.Month}/{local.Day} {Local(utc)}";
    }

    private sealed class FakeFacts : INotifyFacts
    {
        public Dictionary<string, bool?> Transcripts { get; } = new(StringComparer.OrdinalIgnoreCase);

        public Dictionary<string, LimitFacts?> Limits { get; } = new(StringComparer.OrdinalIgnoreCase);

        public Dictionary<string, string> Names { get; } = new(StringComparer.Ordinal);

        public ArmSnapshot? Arm { get; set; }

        public bool ThrowOnArm { get; set; }

        public bool? TranscriptExists(string transcriptPath) =>
            Transcripts.TryGetValue(transcriptPath, out bool? exists) ? exists : true;

        public LimitFacts? ReadLimit(string transcriptPath) => Limits.GetValueOrDefault(transcriptPath);

        public string? SessionName(string? sessionId, string? transcriptPath) =>
            sessionId is not null && Names.TryGetValue(sessionId, out string? name) ? name : null;

        public ArmSnapshot? ReadArm() => ThrowOnArm ? throw new IOException("config busy") : Arm;
    }
}

/// <summary>
/// <see cref="NotifyFacts"/> 的生产读取:只对临时目录里按 Claude Code 2.1.309 录制形状写的文件断言,
/// 账号身份与时钟注入,不碰真实 ~/.claude 与 AI Resume state。
/// </summary>
[Collection(SqliteCollection.Name)]
public sealed class NotifyFactsTests
{
    private const string Session = "11111111-1111-1111-1111-111111111111";

    [Fact]
    public void 转录在不在_只在projects目录可读且形状对时才说不在()
    {
        string root = TestTemp.NewDir("facts-transcript");
        string folder = Path.Combine(root, "projects", "C--work-ledger-api");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, Session + ".jsonl"), "{}\n");
        var facts = new NotifyFacts(TestTemp.NewDir("facts-shadow"), root);

        Assert.True(facts.TranscriptExists(Path.Combine(folder, Session + ".jsonl")));
        Assert.False(facts.TranscriptExists(Path.Combine(folder, "22222222-2222-2222-2222-222222222222.jsonl")));
        // 监视器所在目录从来没有会话落盘过:项目文件夹都不存在,也是「不在」。
        Assert.False(facts.TranscriptExists(Path.Combine(root, "projects", "C--Users-u-Documents-Default-Project", Session + ".jsonl")));
        // projects 根不在(映射盘、别的配置根)或路径形状陌生:判断不了。
        Assert.Null(facts.TranscriptExists(Path.Combine(TestTemp.NewPath("facts-missing"), "projects", "x", Session + ".jsonl")));
        Assert.Null(facts.TranscriptExists(Path.Combine(root, "elsewhere", Session + ".jsonl")));
    }

    [Fact]
    public void 限额中断取转录尾部的服务端窗口与重置时间_没被打断或时间离谱时不采信()
    {
        string root = TestTemp.NewDir("facts-limit");
        string folder = Path.Combine(root, "projects", "C--work-ledger-api");
        Directory.CreateDirectory(folder);
        string transcript = Path.Combine(folder, Session + ".jsonl");
        string reply = """{"parentUuid":"u1","isSidechain":false,"message":{"model":"claude-opus-5-5","id":"msg_01","type":"message","role":"assistant","content":[{"type":"text","text":"好"}],"stop_reason":"end_turn"},"type":"assistant","uuid":"a1","timestamp":"2026-10-30T01:00:05.000Z","sessionId":"11111111-1111-1111-1111-111111111111","version":"2.1.309"}""";
        string limit = """{"parentUuid":"a1","isSidechain":false,"type":"assistant","uuid":"a2","timestamp":"2026-10-30T02:00:00.000Z","message":{"id":"x1","model":"<synthetic>","role":"assistant","stop_reason":"stop_sequence","type":"message","usage":{"input_tokens":0,"output_tokens":0},"content":[{"type":"text","text":"You've hit your weekly limit"}]},"quotaLimits":{"status":"rejected","resetsAt":1793628000,"unifiedRateLimitFallbackAvailable":false,"rateLimitType":"seven_day","overageStatus":"rejected","isUsingOverage":false},"error":"rate_limit","isApiErrorMessage":true,"apiErrorStatus":429,"sessionId":"11111111-1111-1111-1111-111111111111","version":"2.1.309"}""";
        File.WriteAllText(transcript, reply + "\n" + limit + "\n");
        DateTimeOffset now = DateTimeOffset.FromUnixTimeSeconds(1793628000).AddDays(-2);
        var facts = new NotifyFacts(TestTemp.NewDir("facts-shadow"), root, clock: () => now);

        LimitFacts? read = facts.ReadLimit(transcript);

        Assert.NotNull(read);
        Assert.Equal("seven_day", read!.LimitType);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1793628000), read.ResetsAtUtc);
        Assert.Equal("claude-opus-5-5", read.Model);

        // 转录变了(会话接着有了真实应答)就重读,不沿用缓存。
        File.WriteAllText(transcript, reply + "\n");
        Assert.Null(facts.ReadLimit(transcript));

        // 重置时间离现在超过一个月:当作没给,只留窗口类型。
        File.WriteAllText(transcript, reply + "\n" + limit.Replace("1793628000", "4102444800", StringComparison.Ordinal) + "\n");
        read = facts.ReadLimit(transcript);
        Assert.NotNull(read);
        Assert.Null(read!.ResetsAtUtc);
    }

    [Fact]
    public void 会话名先读进程登记_会话关了退回转录里的标题()
    {
        string root = TestTemp.NewDir("facts-name");
        string folder = Path.Combine(root, "projects", "C--work-ledger-api");
        Directory.CreateDirectory(folder);
        Directory.CreateDirectory(Path.Combine(root, "sessions"));
        string transcript = Path.Combine(folder, Session + ".jsonl");
        File.WriteAllText(transcript,
            """{"type":"custom-title","customTitle":"旧标题","sessionId":"11111111-1111-1111-1111-111111111111"}""" + "\n" +
            """{"type":"user","message":{"role":"user","content":"x"},"uuid":"u1","sessionId":"11111111-1111-1111-1111-111111111111"}""" + "\n" +
            """{"type":"custom-title","customTitle":"v2.2 收尾","sessionId":"11111111-1111-1111-1111-111111111111"}""" + "\n");
        var facts = new NotifyFacts(TestTemp.NewDir("facts-shadow"), TestTemp.NewPath("facts-unused-root"));

        Assert.Equal("v2.2 收尾", facts.SessionName(Session, transcript));

        // 坏掉的登记与 name 不是字符串的登记跳过,不影响别的。
        File.WriteAllText(Path.Combine(root, "sessions", "100.json"), "{ not json");
        File.WriteAllText(Path.Combine(root, "sessions", "101.json"), JsonSerializer.Serialize(new { pid = 101, sessionId = Session, name = 42 }));
        Assert.Equal("v2.2 收尾", facts.SessionName(Session, transcript));

        // 2.1.309 登记形状(只留用得到的字段):名字以登记为准,配置根从转录路径反推。
        File.WriteAllText(Path.Combine(root, "sessions", "41440.json"), JsonSerializer.Serialize(new
        {
            pid = 41440, sessionId = Session, cwd = @"C:\work\ledger-api", startedAt = 1793000000000, procStart = "134062000000000000",
            version = "2.1.309", kind = "interactive", entrypoint = "claude-desktop", name = "通知文案重做", nameSource = "user", status = "busy",
        }));
        Assert.Equal("通知文案重做", facts.SessionName(Session, transcript));
        Assert.Null(facts.SessionName("22222222-2222-2222-2222-222222222222", null));
        Assert.Null(facts.SessionName(null, transcript));
    }

    [Fact]
    public void 布防快照_配置坏了或已布防却读不出状态库都算未核实()
    {
        string shadow = TestTemp.NewDir("facts-arm");
        var facts = new NotifyFacts(shadow, TestTemp.NewPath("facts-claude"), accounts: _ => Account("acct-1"));
        string config = Path.Combine(shadow, "config.json");

        ArmSnapshot? none = facts.ReadArm();
        Assert.NotNull(none);
        Assert.False(none!.Armed);

        File.WriteAllText(config, "{\"armed\": tru");
        Assert.Null(facts.ReadArm());

        WriteConfig(config, Armed());
        Assert.Null(facts.ReadArm());

        // 已布防却 enabled=false:引擎不续,按本轮已停止说。
        ProductConfig disabled = Armed();
        disabled.Enabled = false;
        WriteConfig(config, disabled);
        ArmSnapshot off = facts.ReadArm()!;
        Assert.True(off.Armed);
        Assert.True(off.Stopped);
    }

    [Fact]
    public void 布防快照_本轮结论与引擎会一直等的原因()
    {
        string shadow = TestTemp.NewDir("facts-hold");
        string config = Path.Combine(shadow, "config.json");
        string db = Path.Combine(shadow, "runs.db");
        StorageDatabase.Migrate(db);
        DateTimeOffset now = new(2026, 10, 30, 12, 0, 0, TimeSpan.Zero);
        ProviderAccount? account = Account("acct-1");
        var facts = new NotifyFacts(shadow, TestTemp.NewPath("facts-claude"), accounts: _ => account, clock: () => now);
        try
        {
            var store = new ProductStateStore(db);
            CheckerState state = CheckerState.CreateDefault();
            state.CycleId = "cycle-1";
            state.Phase = CheckerState.PhaseWaiting;
            state.LastProbeUtc = now.AddMinutes(-5);
            state.ProjectStatus = new Dictionary<string, string> { [@"c:\work\ledger-api\"] = "success" };
            store.Save(state);

            WriteConfig(config, Armed());
            ArmSnapshot snapshot = facts.ReadArm()!;
            Assert.True(snapshot.Armed);
            Assert.False(snapshot.Stopped);
            Assert.Null(snapshot.Hold);
            ArmedProject project = Assert.Single(snapshot.Projects);
            Assert.Equal(Session, project.SessionId);
            Assert.True(project.Handled);
            Assert.False(project.NeedsRearm);

            // 账号身份变了 / 供应商不在了:要重新布防。读不出身份(令牌待续期)只是等待。
            account = Account("acct-2");
            Assert.Equal(ArmSnapshot.HoldRearm, facts.ReadArm()!.Hold);
            account = null;
            Assert.Equal(ArmSnapshot.HoldRearm, facts.ReadArm()!.Hold);
            account = new ProviderAccount("claudecode", string.Empty, "token_expired");
            Assert.Null(facts.ReadArm()!.Hold);
            account = new ProviderAccount("claudecode", "acct-1", "login_expired");
            Assert.Equal(ArmSnapshot.HoldLogin, facts.ReadArm()!.Hold);
            account = Account("acct-1");

            // 布防没有固定目标(旧配置):要重新布防。
            ProductConfig legacy = Armed();
            legacy.ResumeTarget = null;
            WriteConfig(config, legacy);
            Assert.Equal(ArmSnapshot.HoldRearm, facts.ReadArm()!.Hold);
            WriteConfig(config, Armed());

            // 引擎已判这个项目要重新布防(账号变化);或续跑监视很久没探过额度。
            state.ProjectNotes = new Dictionary<string, ProjectNote> { [@"C:\work\ledger-api"] = new() { Reason = "quota:provider-changed" } };
            store.Save(state);
            Assert.True(Assert.Single(facts.ReadArm()!.Projects).NeedsRearm);
            state.ProjectNotes = null;
            state.LastProbeUtc = now.AddHours(-2);
            store.Save(state);
            Assert.Equal(ArmSnapshot.HoldStalled, facts.ReadArm()!.Hold);

            state.LastProbeUtc = now.AddMinutes(-5);
            state.ProjectStatus = new Dictionary<string, string> { [@"C:\work\ledger-api"] = "exit-null" };
            store.Save(state);
            Assert.True(facts.ReadArm()!.Stopped);

            // 状态库还停在上一轮:上一轮的结论不算数。
            state.CycleId = "cycle-0";
            state.ProjectStatus = new Dictionary<string, string> { [@"C:\work\ledger-api"] = "success" };
            store.Save(state);
            snapshot = facts.ReadArm()!;
            Assert.False(snapshot.Stopped);
            Assert.False(Assert.Single(snapshot.Projects).Handled);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        }
    }

    private static ProviderAccount Account(string identity) => new("claudecode", identity, null);

    private static ProductConfig Armed() => new()
    {
        Enabled = true, Armed = true, ArmCycleId = "cycle-1", Continuous = false, ResumeModel = "claude-opus-5-5",
        ResumeTarget = new ResumeTarget
        {
            ProviderId = "claude-subscription", ProviderIdentity = "acct-1", ProviderName = "Claude · Anthropic 订阅",
            Agent = "claudecode", ModelId = "claude-opus-5-5", ModelName = "Claude Opus 5.5",
        },
        Selected = [new ProjectRef { Name = "ledger-api", Path = @"C:\work\ledger-api", SessionId = Session }],
    };

    private static void WriteConfig(string path, ProductConfig config) =>
        File.WriteAllText(path, JsonSerializer.Serialize(config, ProductConfig.JsonOptions));
}
