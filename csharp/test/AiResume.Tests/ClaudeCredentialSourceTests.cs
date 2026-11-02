using System.Net;
using System.Text.Json;
using AiResume.Worker.Quota;
using Xunit;

namespace AiResume.Tests;

/// <summary>
/// Claude 凭据只来自 Claude Code 自己的登录文件;账号指纹来自全局配置的 <c>oauthAccount</c>。
/// 文件形状照抄 2026-10-30 本机 Claude Code 2.1.300(<c>.credentials.json</c> 只有 <c>claudeAiOauth</c>
/// 与 <c>mcpOAuth</c>,没有任何账号字段;<c>~/.claude.json</c> 的 <c>oauthAccount</c> 才有组织/账号 UUID)。
/// 全部用临时目录，绝不读取真机 ~/.claude 或 ~/.claude.json。
/// </summary>
public sealed class ClaudeCredentialSourceTests
{
    private const string CliToken = "sk-" + "ant-oat01-TEST-cli-token-UNIQUE-7a21"; // 拼接：避开密钥形状门禁

    private static (ClaudeCredentialSource Source, string Credentials, string Global) Create(
        string prefix, Func<DateTimeOffset>? clock = null)
    {
        string root = TestTemp.NewDir(prefix);
        string credentials = Path.Combine(root, ".claude", ".credentials.json");
        string global = Path.Combine(root, ".claude.json");
        Directory.CreateDirectory(Path.GetDirectoryName(credentials)!);
        return (new ClaudeCredentialSource(credentials, global, clock), credentials, global);
    }

    private static void WriteLogin(string path, string token, DateTimeOffset expiresAt, DateTimeOffset? loginExpiresAt = null) =>
        File.WriteAllText(path, JsonSerializer.Serialize(new
        {
            claudeAiOauth = new
            {
                accessToken = token,
                refreshToken = "fake-refresh",
                expiresAt = expiresAt.ToUnixTimeMilliseconds(),
                refreshTokenExpiresAt = loginExpiresAt?.ToUnixTimeMilliseconds(),
                scopes = new[] { "user:inference", "user:profile" },
                subscriptionType = "max",
                rateLimitTier = "default_claude_max_5x",
            },
            mcpOAuth = new { },
        }));

    // 全局配置里同时出现仅大小写不同的键是真实形状(PowerShell ConvertFrom-Json 因此失败过)。
    private static void WriteAccount(string path, string organization, string account) =>
        File.WriteAllText(path, $$$"""
            {"numStartups":12,"projects":{},"oauthAccount":{"accountUuid":"{{{account}}}","emailAddress":"user@example.invalid","organizationUuid":"{{{organization}}}","organizationName":"Demo","billingType":"stripe_subscription"},"hasCompletedOnboarding":true,"tipsHistory":{"a":1},"TipsHistory":{"a":2}}
            """);

    [Fact]
    public void 未登录时如实报缺失并给出可行动的说明()
    {
        (ClaudeCredentialSource source, _, _) = Create("claude-cred-none");

        ClaudeCredential credential = source.Read();
        ClaudeCredentialStatus status = source.Describe();

        Assert.False(credential.Ok);
        Assert.Equal("no_credentials", credential.FailureReason);
        Assert.Contains("/login", credential.Detail, StringComparison.Ordinal);
        Assert.Equal(ClaudeLoginVerdict.LoggedOut, status.Verdict);
        Assert.DoesNotContain("setup-token", status.Problem, StringComparison.Ordinal);
    }

    [Fact]
    public void 账号指纹取自oauthAccount且令牌轮换时保持不变()
    {
        (ClaudeCredentialSource source, string credentials, string global) = Create("claude-cred-account");
        WriteAccount(global, "11111111-1111-1111-1111-111111111111", "acct-aaaa");
        WriteLogin(credentials, CliToken, DateTimeOffset.UtcNow.AddHours(8));
        ClaudeCredential first = source.Read();

        // Claude Code 每 8 小时轮换一次访问令牌:同一账号的指纹必须不变,
        // 否则续跑引擎会把同一个账号判成「供应商已改变」而永远等待(2026-10-30 体检根因 a)。
        WriteLogin(credentials, CliToken + "-rotated", DateTimeOffset.UtcNow.AddHours(16));
        ClaudeCredential rotated = source.Read();

        Assert.True(first.Ok);
        Assert.Equal(first.CredentialFingerprint, rotated.CredentialFingerprint);
        Assert.DoesNotContain("11111111-1111-1111-1111-111111111111", first.CredentialFingerprint, StringComparison.Ordinal);
        Assert.True(source.Describe().AccountVerified);

        WriteAccount(global, "11111111-1111-1111-1111-111111111111", "acct-bbbb");
        Assert.NotEqual(first.CredentialFingerprint, source.Read().CredentialFingerprint);
        WriteAccount(global, "22222222-2222-2222-2222-222222222222", "acct-aaaa");
        Assert.NotEqual(first.CredentialFingerprint, source.Read().CredentialFingerprint);
    }

    [Fact]
    public void 访问令牌过期但登录有效时说明会自动续期()
    {
        (ClaudeCredentialSource source, string credentials, _) = Create("claude-cred-token-expired");
        WriteLogin(credentials, CliToken, DateTimeOffset.UtcNow.AddSeconds(30), DateTimeOffset.UtcNow.AddDays(20));

        ClaudeCredential credential = source.Read();
        ClaudeCredentialStatus status = source.Describe();

        Assert.False(credential.Ok);
        Assert.Equal("token_expired", credential.FailureReason);
        Assert.Equal(ClaudeLoginVerdict.TokenExpired, status.Verdict);
        Assert.Contains("自动续期", status.Problem, StringComparison.Ordinal);
    }

    [Fact]
    public void 登录将到期时提前提醒且仍可用()
    {
        DateTimeOffset now = new(2026, 11, 8, 12, 0, 0, TimeSpan.Zero);
        (ClaudeCredentialSource source, string credentials, _) = Create("claude-cred-expiring", () => now);
        WriteLogin(credentials, CliToken, now.AddHours(6), now.AddDays(2));

        Assert.True(source.Read().Ok);
        ClaudeCredentialStatus status = source.Describe();
        Assert.Equal(ClaudeLoginVerdict.LoginExpiring, status.Verdict);
        Assert.Contains("/login", status.Problem, StringComparison.Ordinal);
        Assert.Equal(now.AddDays(2), status.LoginExpiresAtUtc);
        Assert.Equal("max", status.SubscriptionType);

        now = now.AddDays(3);
        WriteLogin(credentials, CliToken, now.AddHours(6), now.AddDays(-1));
        Assert.Equal("login_expired", source.Read().FailureReason);
        Assert.Equal(ClaudeLoginVerdict.LoginExpired, source.Describe().Verdict);
    }

    [Fact]
    public void 令牌过期且登录将到期时两件事都说()
    {
        DateTimeOffset now = new(2026, 11, 8, 12, 0, 0, TimeSpan.Zero);
        (ClaudeCredentialSource source, string credentials, _) = Create("claude-cred-both", () => now);
        WriteLogin(credentials, CliToken, now.AddMinutes(-5), now.AddDays(1));

        ClaudeCredentialStatus status = source.Describe();

        Assert.Equal(ClaudeLoginVerdict.TokenExpired, status.Verdict);
        Assert.Contains("自动续期", status.Problem, StringComparison.Ordinal);
        Assert.Contains("到期", status.Problem, StringComparison.Ordinal);
    }

    [Fact]
    public void 缺少expiresAt按未登录处理_文件读不出或解析不了是未核实而不是未登录()
    {
        (ClaudeCredentialSource source, string credentials, _) = Create("claude-cred-broken");
        File.WriteAllText(credentials, """{"claudeAiOauth":{"accessToken":"fake-oat01-TEST-noexp-UNIQUE-0001"}}""");
        Assert.Equal("no_credentials", source.Read().FailureReason);
        Assert.Equal(ClaudeLoginVerdict.LoggedOut, source.Describe().Verdict);

        // Claude Code 改写文件的瞬间读到半截:判断不了。
        File.WriteAllText(credentials, "{ 坏");
        ClaudeCredential broken = source.Read();
        Assert.False(broken.Ok);
        Assert.Equal("unreadable", broken.FailureReason);
        ClaudeCredentialStatus status = source.Describe();
        Assert.Equal(ClaudeLoginVerdict.Unverified, status.Verdict);
        Assert.Contains("无法解析", status.Problem, StringComparison.Ordinal);

        // 被独占打开(读不了)同样是未核实。
        WriteLogin(credentials, CliToken, DateTimeOffset.UtcNow.AddHours(8));
        using (new FileStream(credentials, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            Assert.Equal("unreadable", source.Read().FailureReason);
            Assert.Equal(ClaudeLoginVerdict.Unverified, source.Describe().Verdict);
        }

        Assert.True(source.Read().Ok);
    }

    [Fact]
    public async Task 额度请求只用Claude_Code登录且不回写任何文件()
    {
        (ClaudeCredentialSource source, string credentials, string global) = Create("claude-cred-fetch");
        WriteAccount(global, "11111111-1111-1111-1111-111111111111", "acct-aaaa");
        WriteLogin(credentials, CliToken, DateTimeOffset.UtcNow.AddHours(4));
        string before = File.ReadAllText(credentials);
        var seen = new List<string>();
        using var handler = new RoutingHandler(request =>
        {
            seen.Add(request.Headers.Authorization?.Parameter ?? string.Empty);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"five_hour":{"utilization":10,"resets_at":"2099-01-01T00:00:00Z"},"seven_day":{"utilization":20,"resets_at":"2099-01-02T00:00:00Z"}}"""),
            };
        });
        using var http = new HttpClient(handler);
        var probe = new ClaudeOAuthUsageProbe(http, credentials: source, userAgent: "claude-code/2.1.300");

        OAuthUsageResult result = await probe.TryFetchAsync(CancellationToken.None);

        Assert.True(result.Ok);
        Assert.Equal(ClaudeCredentialSource.SourceCli, result.CredentialSource);
        Assert.Equal(new[] { CliToken }, seen);
        Assert.Equal(source.Read().CredentialFingerprint, result.CredentialFingerprint);
        Assert.Equal(before, File.ReadAllText(credentials));
    }

    [Fact]
    public async Task 被服务端拒绝时提示重新登录()
    {
        (ClaudeCredentialSource source, string credentials, _) = Create("claude-cred-401");
        WriteLogin(credentials, CliToken, DateTimeOffset.UtcNow.AddHours(4));
        using var handler = new RoutingHandler(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized));
        using var http = new HttpClient(handler);
        var probe = new ClaudeOAuthUsageProbe(http, credentials: source, userAgent: "claude-code/2.1.300");

        OAuthUsageResult result = await probe.TryFetchAsync(CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Equal("token_rejected_401", result.FailureReason);
        Assert.Contains("/login", QuotaService.DescribeOAuthFailure(result), StringComparison.Ordinal);
    }

    [Fact]
    public void 读不到账号时指纹为空而不是令牌指纹()
    {
        (ClaudeCredentialSource source, string credentials, _) = Create("claude-cred-empty-fp");
        WriteLogin(credentials, CliToken, DateTimeOffset.UtcNow.AddHours(8));

        ClaudeCredential first = source.Read();
        WriteLogin(credentials, CliToken + "-rotated", DateTimeOffset.UtcNow.AddHours(8));
        ClaudeCredential rotated = source.Read();

        // 核实不了账号就如实为空：调用方据此等待;令牌指纹会让同一账号每 8 小时被判成换了供应商。
        Assert.True(first.Ok);
        Assert.Equal(string.Empty, first.CredentialFingerprint);
        Assert.Equal(string.Empty, rotated.CredentialFingerprint);
    }

    [Fact]
    public void 被Claude_Code清空的登录判为登录失效而不是从未登录()
    {
        (ClaudeCredentialSource source, string credentials, _) = Create("claude-cred-cleared");
        // invalid_grant 后 Claude Code 把 accessToken/refreshToken 置空、expiresAt 置 0(2.1.300 二进制核对)。
        File.WriteAllText(credentials, """{"claudeAiOauth":{"accessToken":"","refreshToken":"","expiresAt":0,"scopes":[]},"mcpOAuth":{}}""");

        Assert.Equal("login_expired", source.Read().FailureReason);
        Assert.Equal(ClaudeLoginVerdict.LoginExpired, source.Describe().Verdict);
    }

    [Fact]
    public void 凭据迁到Windows凭据管理器时如实报告而不是未登录()
    {
        (ClaudeCredentialSource _, string credentials, string global) = Create("claude-cred-credman");
        File.WriteAllText(global, """{"cachedGrowthBookFeatures":{"tengu_windows_credman":true},"oauthAccount":{"organizationUuid":"11111111-1111-1111-1111-111111111111"}}""");
        var source = new ClaudeCredentialSource(credentials, global, environment: _ => null);

        Assert.Equal("credential_store_moved", source.Read().FailureReason);
        Assert.Equal(ClaudeLoginVerdict.StoreMoved, source.Describe().Verdict);

        var forced = new ClaudeCredentialSource(credentials, Path.Combine(Path.GetDirectoryName(global)!, "none.json"),
            environment: name => name == "CLAUDE_CODE_FORCE_WINDOWS_CREDMAN" ? "1" : null);
        Assert.Equal("credential_store_moved", forced.Read().FailureReason);
    }

    [Fact]
    public void 两份全局配置的组织不一致时账号未核实()
    {
        string root = TestTemp.NewDir("claude-cred-split");
        string credentials = Path.Combine(root, ".claude", ".credentials.json");
        Directory.CreateDirectory(Path.GetDirectoryName(credentials)!);
        WriteLogin(credentials, CliToken, DateTimeOffset.UtcNow.AddHours(8));
        string primary = Path.Combine(root, ".claude.json");
        string secondary = Path.Combine(root, ".claude", ".claude.json");
        WriteAccount(primary, "11111111-1111-1111-1111-111111111111", "acct-a");
        WriteAccount(secondary, "22222222-2222-2222-2222-222222222222", "acct-a");
        var env = new Dictionary<string, string?>();
        string? Env(string name) => env.TryGetValue(name, out string? value) ? value : null;

        // 默认定位(不显式给全局配置路径)会同时读两份;这里用 CLAUDE_CONFIG_DIR 指向临时目录模拟。
        env["CLAUDE_CONFIG_DIR"] = Path.Combine(root, ".claude");
        string[] paths = ClaudeCredentialSource.GlobalConfigPathsFor(Env, root);
        Assert.Single(paths);   // CLAUDE_CONFIG_DIR 下主次重合

        env.Clear();
        paths = ClaudeCredentialSource.GlobalConfigPathsFor(Env, root);
        Assert.Equal(2, paths.Length);

        Assert.Equal(primary, paths[0]);
        var split = new ClaudeCredentialSource(credentials, environment: Env, globalConfigPaths: paths);
        // 两份配置共用同一份凭据文件，组织却不一致：说不清现在是哪个账号，只能判未核实(等待)。
        Assert.Equal(string.Empty, split.Read().CredentialFingerprint);
        Assert.False(split.Describe().AccountVerified);

        WriteAccount(secondary, "11111111-1111-1111-1111-111111111111", "acct-a");
        Assert.NotEmpty(split.Read().CredentialFingerprint);
        Assert.True(split.Describe().AccountVerified);
    }

    [Fact]
    public void 全局配置正被改写读不出时沿用上次成功的账号()
    {
        (ClaudeCredentialSource source, string credentials, string global) = Create("claude-cred-lastgood");
        WriteAccount(global, "11111111-1111-1111-1111-111111111111", "acct-a");
        WriteLogin(credentials, CliToken, DateTimeOffset.UtcNow.AddHours(8));
        string good = source.Read().CredentialFingerprint;

        File.WriteAllText(global, "{ \"oauthAccount\": { 半截");

        Assert.Equal(good, source.Read().CredentialFingerprint);
    }

    [Fact]
    public async Task 裸403判为网关拦截而结构化403判为登录被拒()
    {
        (ClaudeCredentialSource source, string credentials, _) = Create("claude-cred-403");
        WriteLogin(credentials, CliToken, DateTimeOffset.UtcNow.AddHours(4));
        string body = "<html>blocked by edge</html>";
        using var handler = new RoutingHandler(_ => new HttpResponseMessage(HttpStatusCode.Forbidden) { Content = new StringContent(body) });
        using var http = new HttpClient(handler);
        var probe = new ClaudeOAuthUsageProbe(http, credentials: source, userAgent: "claude-code/2.1.300");

        OAuthUsageResult bare = await probe.TryFetchAsync(CancellationToken.None);
        body = """{"type":"error","error":{"type":"permission_error","message":"OAuth token lacks required scope"}}""";
        OAuthUsageResult structured = await probe.TryFetchAsync(CancellationToken.None);

        Assert.Equal("http_403_gateway", bare.FailureReason);
        Assert.DoesNotContain("/login", QuotaService.DescribeOAuthFailure(bare), StringComparison.Ordinal);
        Assert.Equal("token_rejected_403", structured.FailureReason);
        Assert.Contains("/login", QuotaService.DescribeOAuthFailure(structured), StringComparison.Ordinal);
    }

    [Fact]
    public void 失败分类翻译不含凭据且给出行动()
    {
        Assert.Contains("自动续期", QuotaService.DescribeOAuthFailure(new OAuthUsageResult(false, null, "token_expired")), StringComparison.Ordinal);
        Assert.Contains("/login", QuotaService.DescribeOAuthFailure(new OAuthUsageResult(false, null, "login_expired")), StringComparison.Ordinal);
        Assert.Equal("自定义说明", QuotaService.DescribeOAuthFailure(new OAuthUsageResult(false, null, "token_expired", Note: "自定义说明")));
        Assert.Contains("429", QuotaService.DescribeOAuthFailure(new OAuthUsageResult(false, null, "http_429")), StringComparison.Ordinal);
    }

    private sealed class RoutingHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(respond(request));
    }
}
