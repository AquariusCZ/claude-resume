using System.Net;
using System.Text.Json;
using AiResume.Worker.Quota;
using Xunit;

namespace AiResume.Tests;

/// <summary>
/// Claude 凭据来源:AI Resume 自持长期令牌(机器范围 DPAPI)优先,Claude CLI 登录文件其次。
/// 全部用临时目录,绝不读取真机 ~/.claude 或生产 DPAPI 目录。
/// </summary>
public sealed class ClaudeCredentialSourceTests
{
    private const string StoredToken = "sk-" + "ant-oat01-TEST-stored-token-UNIQUE-4f9c"; // 拼接:避开密钥形状门禁
    private const string CliToken = "sk-" + "ant-oat01-TEST-cli-token-UNIQUE-7a21";

    [Fact]
    public void 没有任何凭据时如实报缺失并给出可行动的说明()
    {
        string root = TestTemp.NewDir("claude-cred-none");
        var source = new ClaudeCredentialSource(Path.Combine(root, ".credentials.json"), Path.Combine(root, "secrets"));

        ClaudeCredential credential = source.Read();
        ClaudeCredentialStatus status = source.Describe();

        Assert.False(credential.Ok);
        Assert.Equal("no_credentials", credential.FailureReason);
        Assert.Equal(ClaudeCredentialSource.SourceNone, credential.Source);
        Assert.Contains("setup-token", credential.Detail, StringComparison.Ordinal);
        Assert.False(status.HasStoredToken);
        Assert.False(status.CliLoggedIn);
        Assert.Equal(ClaudeCredentialSource.SourceNone, status.ActiveSource);
    }

    [Fact]
    public void 长期令牌经机器范围DPAPI往返且优先于CLI登录()
    {
        string root = TestTemp.NewDir("claude-cred-stored");
        string credentialsPath = Path.Combine(root, ".credentials.json");
        WriteCli(credentialsPath, CliToken, DateTimeOffset.UtcNow.AddHours(4));
        var source = new ClaudeCredentialSource(credentialsPath, Path.Combine(root, "secrets"));
        Assert.Equal(ClaudeCredentialSource.SourceCli, source.Read().Source);

        source.SaveToken(StoredToken);

        ClaudeCredential credential = source.Read();
        Assert.True(credential.Ok);
        Assert.Equal(ClaudeCredentialSource.SourceStored, credential.Source);
        Assert.Equal(StoredToken, credential.AccessToken);
        Assert.DoesNotContain(StoredToken, credential.CredentialFingerprint, StringComparison.Ordinal);
        // 密文落盘:文件里绝不能有明文令牌。
        byte[] cipher = File.ReadAllBytes(Path.Combine(root, "secrets", "secrets", ClaudeCredentialSource.StoredTokenRef + ".bin"));
        Assert.DoesNotContain(StoredToken, System.Text.Encoding.UTF8.GetString(cipher), StringComparison.Ordinal);
        Assert.Equal(ClaudeCredentialSource.SourceStored, source.Describe().ActiveSource);

        source.ClearToken();
        Assert.False(source.HasStoredToken);
        Assert.Equal(ClaudeCredentialSource.SourceCli, source.Read().Source);
    }

    [Theory]
    [InlineData("short")]
    [InlineData("has whitespace inside the token value 0123456789")]
    [InlineData("非ASCII字符的令牌内容abcdefghijklmnopqrstuvwxyz")]
    public void 形状非法的令牌拒绝保存(string token)
    {
        string root = TestTemp.NewDir("claude-cred-invalid");
        var source = new ClaudeCredentialSource(Path.Combine(root, ".credentials.json"), Path.Combine(root, "secrets"));

        Assert.Throws<ArgumentException>(() => source.SaveToken(token));
        Assert.False(source.HasStoredToken);
    }

    [Fact]
    public void CLI登录到期或expiresAt为零时视同未登录()
    {
        string root = TestTemp.NewDir("claude-cred-expired");
        string credentialsPath = Path.Combine(root, ".credentials.json");
        var source = new ClaudeCredentialSource(credentialsPath, Path.Combine(root, "secrets"));

        WriteCli(credentialsPath, CliToken, DateTimeOffset.UtcNow.AddSeconds(30));
        Assert.Equal("token_expired", source.ReadCli().FailureReason);

        File.WriteAllText(credentialsPath, """{"claudeAiOauth":{"accessToken":"x","expiresAt":0,"refreshTokenExpiresAt":1},"organizationUuid":"org"}""");
        ClaudeCredential expired = source.ReadCli();
        Assert.False(expired.Ok);
        Assert.Equal("token_expired", expired.FailureReason);
        Assert.Contains("/login", expired.Detail, StringComparison.Ordinal);
        Assert.False(source.Describe().CliLoggedIn);
    }

    [Fact]
    public void 同账号CLI指纹随organizationUuid稳定而长期令牌指纹随令牌()
    {
        string root = TestTemp.NewDir("claude-cred-fingerprint");
        string credentialsPath = Path.Combine(root, ".credentials.json");
        var source = new ClaudeCredentialSource(credentialsPath, Path.Combine(root, "secrets"));
        WriteCli(credentialsPath, CliToken, DateTimeOffset.UtcNow.AddHours(1), "org-1");
        string first = source.ReadCli().CredentialFingerprint;
        WriteCli(credentialsPath, CliToken + "-rotated", DateTimeOffset.UtcNow.AddHours(1), "org-1");
        Assert.Equal(first, source.ReadCli().CredentialFingerprint);
        WriteCli(credentialsPath, CliToken, DateTimeOffset.UtcNow.AddHours(1), "org-2");
        Assert.NotEqual(first, source.ReadCli().CredentialFingerprint);

        source.SaveToken(StoredToken);
        string stored = source.ReadStored().CredentialFingerprint;
        source.SaveToken(StoredToken + "-new");
        Assert.NotEqual(stored, source.ReadStored().CredentialFingerprint);
    }

    [Fact]
    public async Task 长期令牌被usage端点拒绝后记录并回退CLI登录()
    {
        string root = TestTemp.NewDir("claude-cred-fallback");
        string credentialsPath = Path.Combine(root, ".credentials.json");
        WriteCli(credentialsPath, CliToken, DateTimeOffset.UtcNow.AddHours(4));
        var source = new ClaudeCredentialSource(credentialsPath, Path.Combine(root, "secrets"));
        source.SaveToken(StoredToken);
        var seen = new List<string>();
        using var handler = new RoutingHandler(request =>
        {
            string bearer = request.Headers.Authorization?.Parameter ?? string.Empty;
            seen.Add(bearer);
            return bearer == StoredToken
                ? new HttpResponseMessage(HttpStatusCode.Unauthorized)
                : new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("""{"five_hour":{"utilization":10,"resets_at":"2099-01-01T00:00:00Z"},"seven_day":{"utilization":20,"resets_at":"2099-01-02T00:00:00Z"}}"""),
                };
        });
        using var http = new HttpClient(handler);
        var probe = new ClaudeOAuthUsageProbe(http, credentials: source, userAgent: "claude-code/2.1.280");

        OAuthUsageResult first = await probe.TryFetchAsync(CancellationToken.None);

        Assert.True(first.Ok);
        Assert.Equal(ClaudeCredentialSource.SourceCli, first.CredentialSource);
        Assert.Contains("拒绝", first.Note, StringComparison.Ordinal);
        Assert.Equal(new[] { StoredToken, CliToken }, seen);
        Assert.Equal("token_rejected_401", source.StoredRejectedReason);
        // 记录生效后不再反复拿被拒的令牌去撞端点;重新保存令牌即清除记录。
        OAuthUsageResult second = await probe.TryFetchAsync(CancellationToken.None);
        Assert.True(second.Ok);
        Assert.Equal(3, seen.Count);
        Assert.Equal(CliToken, seen[^1]);
        source.SaveToken(StoredToken + "-replaced");
        Assert.Null(source.StoredRejectedReason);
        Assert.Equal(ClaudeCredentialSource.SourceStored, source.Read().Source);
    }

    [Fact]
    public void 被拒标记只绑定被拒的那份令牌()
    {
        string root = TestTemp.NewDir("claude-cred-marker");
        var source = new ClaudeCredentialSource(Path.Combine(root, ".credentials.json"), Path.Combine(root, "secrets"));
        source.SaveToken(StoredToken);
        ClaudeCredential first = source.Read();
        Assert.NotNull(first.FileLength);
        source.SaveToken(StoredToken + "-second");

        // 在途请求拿着旧令牌被拒:不能给刚保存的新令牌打标。
        source.MarkStoredRejected("token_rejected_401", first);
        Assert.Null(source.StoredRejectedReason);
        Assert.Equal(StoredToken + "-second", source.Read().AccessToken);

        source.MarkStoredRejected("token_rejected_401", source.Read());
        Assert.Equal("token_rejected_401", source.StoredRejectedReason);

        // 非自持来源的凭据不写标记。
        source.SaveToken(StoredToken + "-third");
        source.MarkStoredRejected("token_rejected_401", new ClaudeCredential(true, "x", "f", null, ClaudeCredentialSource.SourceCli));
        Assert.Null(source.StoredRejectedReason);
    }

    [Fact]
    public void 被拒标记一小时后失效允许再试一次()
    {
        string root = TestTemp.NewDir("claude-cred-ttl");
        DateTimeOffset now = new(2026, 10, 12, 8, 0, 0, TimeSpan.Zero);
        var source = new ClaudeCredentialSource(Path.Combine(root, ".credentials.json"), Path.Combine(root, "secrets"), () => now);
        source.SaveToken(StoredToken);
        source.MarkStoredRejected("token_rejected_401", source.Read());
        Assert.Equal("token_rejected_401", source.StoredRejectedReason);
        Assert.Equal(ClaudeCredentialSource.SourceNone, source.Read().Source);

        now = now.AddMinutes(59);
        Assert.Equal("token_rejected_401", source.StoredRejectedReason);
        now = now.AddMinutes(2);
        Assert.Null(source.StoredRejectedReason);
        Assert.Equal(ClaudeCredentialSource.SourceStored, source.Read().Source);
    }

    [Fact]
    public async Task 裸403不持久标记但Anthropic结构化403会标记()
    {
        string root = TestTemp.NewDir("claude-cred-403");
        string credentialsPath = Path.Combine(root, ".credentials.json");
        WriteCli(credentialsPath, CliToken, DateTimeOffset.UtcNow.AddHours(4));
        var source = new ClaudeCredentialSource(credentialsPath, Path.Combine(root, "secrets"));
        source.SaveToken(StoredToken);
        string storedBody = "<html>blocked by edge</html>";
        using var handler = new RoutingHandler(request =>
        {
            string bearer = request.Headers.Authorization?.Parameter ?? string.Empty;
            return bearer == StoredToken
                ? new HttpResponseMessage(HttpStatusCode.Forbidden) { Content = new StringContent(storedBody) }
                : new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("""{"five_hour":{"utilization":10,"resets_at":"2099-01-01T00:00:00Z"}}"""),
                };
        });
        using var http = new HttpClient(handler);
        var probe = new ClaudeOAuthUsageProbe(http, credentials: source, userAgent: "claude-code/2.1.280");

        OAuthUsageResult bare = await probe.TryFetchAsync(CancellationToken.None);

        Assert.True(bare.Ok);
        Assert.Equal(ClaudeCredentialSource.SourceCli, bare.CredentialSource);
        Assert.Contains("未确认", bare.Note, StringComparison.Ordinal);
        Assert.Null(source.StoredRejectedReason);
        Assert.Equal(ClaudeCredentialSource.SourceStored, source.Read().Source);

        storedBody = """{"type":"error","error":{"type":"permission_error","message":"OAuth token lacks required scope"}}""";
        OAuthUsageResult structured = await probe.TryFetchAsync(CancellationToken.None);
        Assert.True(structured.Ok);
        Assert.Equal("token_rejected_403", source.StoredRejectedReason);
    }

    [Fact]
    public void 长期令牌文件损坏时Describe报不可读而不是已保存()
    {
        string root = TestTemp.NewDir("claude-cred-corrupt");
        string credentialsPath = Path.Combine(root, ".credentials.json");
        WriteCli(credentialsPath, CliToken, DateTimeOffset.UtcNow.AddHours(4));
        var source = new ClaudeCredentialSource(credentialsPath, Path.Combine(root, "secrets"));
        string binPath = Path.Combine(root, "secrets", "secrets", ClaudeCredentialSource.StoredTokenRef + ".bin");
        Directory.CreateDirectory(Path.GetDirectoryName(binPath)!);
        File.WriteAllBytes(binPath, new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 });

        ClaudeCredentialStatus status = source.Describe();

        Assert.True(status.HasStoredToken);
        Assert.NotNull(status.StoredProblem);
        Assert.Equal(ClaudeCredentialSource.SourceCli, status.ActiveSource);
        Assert.Contains("重新粘贴", status.Problem, StringComparison.Ordinal);
        Assert.Equal("stored_token_unreadable", source.ReadStored().FailureReason);
    }

    [Fact]
    public void CLI凭据缺少expiresAt按未登录处理()
    {
        string root = TestTemp.NewDir("claude-cred-noexpiry");
        string credentialsPath = Path.Combine(root, ".credentials.json");
        File.WriteAllText(credentialsPath, """{"claudeAiOauth":{"accessToken":"fake-oat01-TEST-noexp-UNIQUE-0001"},"organizationUuid":"org"}""");

        ClaudeCredential cli = new ClaudeCredentialSource(credentialsPath, Path.Combine(root, "secrets")).ReadCli();

        Assert.False(cli.Ok);
        Assert.Equal("no_credentials", cli.FailureReason);
        Assert.NotEmpty(cli.CredentialFingerprint);
    }

    [Fact]
    public void 机器范围密文文件ACL只留本用户SYSTEM与管理员()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        string root = TestTemp.NewDir("claude-cred-acl");
        var source = new ClaudeCredentialSource(Path.Combine(root, ".credentials.json"), Path.Combine(root, "secrets"));
        source.SaveToken(StoredToken);
        var file = new FileInfo(Path.Combine(root, "secrets", "secrets", ClaudeCredentialSource.StoredTokenRef + ".bin"));

        System.Security.AccessControl.FileSecurity acl = file.GetAccessControl();

        Assert.True(acl.AreAccessRulesProtected);
        var allowed = new HashSet<System.Security.Principal.SecurityIdentifier>
        {
            System.Security.Principal.WindowsIdentity.GetCurrent().User!,
            new(System.Security.Principal.WellKnownSidType.LocalSystemSid, null),
            new(System.Security.Principal.WellKnownSidType.BuiltinAdministratorsSid, null),
        };
        var rules = acl.GetAccessRules(true, true, typeof(System.Security.Principal.SecurityIdentifier));
        Assert.True(rules.Count > 0);
        foreach (System.Security.AccessControl.FileSystemAccessRule rule in rules)
        {
            Assert.Contains((System.Security.Principal.SecurityIdentifier)rule.IdentityReference, allowed);
        }
        Assert.Equal(StoredToken, source.Read().AccessToken);
    }

    [Fact]
    public void 失败分类翻译不含凭据且给出行动()
    {
        Assert.Contains("setup-token", QuotaService.DescribeOAuthFailure(new OAuthUsageResult(false, null, "token_expired")), StringComparison.Ordinal);
        Assert.Equal("自定义说明", QuotaService.DescribeOAuthFailure(new OAuthUsageResult(false, null, "token_expired", Note: "自定义说明")));
        Assert.Contains("429", QuotaService.DescribeOAuthFailure(new OAuthUsageResult(false, null, "http_429")), StringComparison.Ordinal);
    }

    private static void WriteCli(string path, string token, DateTimeOffset expiresAt, string organization = "org-test")
    {
        File.WriteAllText(path, JsonSerializer.Serialize(new
        {
            claudeAiOauth = new { accessToken = token, expiresAt = expiresAt.ToUnixTimeMilliseconds() },
            organizationUuid = organization,
        }));
    }

    private sealed class RoutingHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(respond(request));
    }
}
