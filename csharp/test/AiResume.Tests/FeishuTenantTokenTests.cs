using System.Net;
using System.Text;
using System.Text.Json;
using AiResume.Worker.Notifications;
using Xunit;

namespace AiResume.Tests;

/// <summary>
/// 方案 A(ADR-0007):机器范围副本自己换 tenant_access_token,只把 App ID + 令牌注入 lark-cli。
/// 换令牌应答照抄官方文档的成功形状与 2026-11-02 本机用假值录制的错误形状;网络一律注入桩，绝不真调飞书。
/// 假 lark-cli 是 TestTemp 下的 .cmd 脚本，按注入的环境变量回 1.0.81 录制形状(合成 ID)。
/// </summary>
public sealed class FeishuTenantTokenTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 2, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void 成功应答字段在顶层_过期时间按秒计_ToString不含令牌()
    {
        // 官方文档成功应答：字段在顶层，没有 data 外壳,expire 单位秒。
        TenantTokenOutcome outcome = FeishuTenantToken.Parse(200, FeishuTokenStub.Issue(FeishuTokenStub.Good), "log-ok", Now);

        Assert.True(outcome.Ok);
        Assert.Equal(FeishuTokenStub.Good, outcome.Token);
        Assert.Equal(Now.AddSeconds(7200), outcome.ExpiresAtUtc);
        Assert.DoesNotContain(FeishuTokenStub.Good, outcome.ToString(), StringComparison.Ordinal);

        // 臆想的 data 外壳不是这个接口的形状：没有顶层令牌就是应答异常，不能当成功。
        TenantTokenOutcome wrapped = FeishuTenantToken.Parse(200,
            "{\"code\":0,\"msg\":\"ok\",\"data\":{\"tenant_access_token\":\"" + FeishuTokenStub.Good + "\",\"expire\":7200}}", null, Now);
        Assert.Equal(TenantTokenFailureKind.Unexpected, wrapped.Failure!.Kind);
    }

    [Theory]
    // 2026-11-02 本机录制(假 app_id):app_id 不存在、缺参数都是 HTTP 200 + 非零 code;请求体不是 JSON 是 HTTP 400 + 9499。
    [InlineData(200, """{"code":10014,"msg":"app id not exists"}""", TenantTokenFailureKind.Rejected, 10014L)]
    [InlineData(200, """{"code":10003,"data":{},"msg":"invalid param"}""", TenantTokenFailureKind.Rejected, 10003L)]
    [InlineData(400, """{"code":9499,"msg":"Bad Request","error":{"log_id":"log-9499"}}""", TenantTokenFailureKind.Unexpected, 9499L)]
    // 未识别的非零 code 不冒充「凭据不对」。
    [InlineData(200, """{"code":12345,"msg":"something else"}""", TenantTokenFailureKind.Unexpected, 12345L)]
    [InlineData(429, """{"code":99991400,"msg":"too many requests"}""", TenantTokenFailureKind.RateLimited, 99991400L)]
    [InlineData(502, "<html>bad gateway</html>", TenantTokenFailureKind.ServerError, null)]
    // 代理或网关拦截:跳转、拦截页。
    [InlineData(302, "", TenantTokenFailureKind.Unexpected, null)]
    [InlineData(403, "<html>blocked by gateway</html>", TenantTokenFailureKind.Unexpected, null)]
    [InlineData(200, "not json", TenantTokenFailureKind.Unexpected, null)]
    [InlineData(200, """{"code":0,"msg":"ok","expire":7200}""", TenantTokenFailureKind.Unexpected, 0L)]
    [InlineData(200, """{"code":0,"msg":"ok","tenant_access_token":"t-fakegood0000000000000000","expire":0}""", TenantTokenFailureKind.Unexpected, 0L)]
    // 令牌会进子进程环境块与输出脱敏集合：过短或含控制字符的值不收。
    [InlineData(200, """{"code":0,"msg":"ok","tenant_access_token":"t-x","expire":7200}""", TenantTokenFailureKind.Unexpected, 0L)]
    [InlineData(200, """{"code":0,"msg":"ok","tenant_access_token":"t-fakegood\u00000000000000000","expire":7200}""", TenantTokenFailureKind.Unexpected, 0L)]
    public void 失败应答按性质归类(int status, string body, TenantTokenFailureKind kind, long? code)
    {
        TenantTokenOutcome outcome = FeishuTenantToken.Parse(status, body, null, Now);

        Assert.False(outcome.Ok);
        Assert.Null(outcome.Token);
        Assert.Equal(kind, outcome.Failure!.Kind);
        Assert.Equal(code, outcome.Failure.Code);
        Assert.Equal(status, outcome.Failure.HttpStatus);
    }

    [Fact]
    public async Task log_id取响应头或应答体_说明里的app_id遮蔽_回显的AppSecret也不外泄()
    {
        const string body = """{"code":9499,"msg":"Bad Request for cli_test0000000000aa","error":{"log_id":"log-body"}}""";

        Assert.Equal("log-header", FeishuTenantToken.Parse(400, body, "log-header", Now).Failure!.LogId);
        TenantTokenFailure failure = FeishuTenantToken.Parse(400, body, null, Now).Failure!;
        Assert.Equal("log-body", failure.LogId);
        Assert.DoesNotContain("cli_test0000000000aa", failure.Message, StringComparison.Ordinal);

        // 飞书的 msg 万一回显了 App Secret,也不进说明。
        var echo = new FeishuTenantToken(new FeishuTokenStub { EchoSecretInMessage = true });
        TenantTokenOutcome echoed = await echo.ExchangeAsync("cli_test000000", "fake-echo-secret", CancellationToken.None);
        Assert.Equal(TenantTokenFailureKind.Unexpected, echoed.Failure!.Kind);
        Assert.DoesNotContain("fake-echo-secret", echoed.Failure.Message, StringComparison.Ordinal);
        Assert.Contains("[REDACTED]", echoed.Failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 请求照官方契约发出_同一组凭据按过期时间复用_换了凭据或作废就重换()
    {
        DateTimeOffset now = Now;
        var stub = new FeishuTokenStub();
        var tokens = new FeishuTenantToken(stub, () => now);

        TenantTokenOutcome first = await tokens.GetAsync("cli_test000000", "fake-good-secret", CancellationToken.None);

        Assert.True(first.Ok);
        Assert.False(first.FromCache);
        FeishuTokenStub.Request request = Assert.Single(stub.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal(FeishuTenantToken.Endpoint, request.Uri);
        Assert.Equal("application/json; charset=utf-8", request.ContentType);
        using (JsonDocument body = JsonDocument.Parse(request.Body))
        {
            Assert.Equal("cli_test000000", body.RootElement.GetProperty("app_id").GetString());
            Assert.Equal("fake-good-secret", body.RootElement.GetProperty("app_secret").GetString());
        }

        // 剩余远多于 5 分钟：复用，不再请求。
        now = Now.AddMinutes(100);
        TenantTokenOutcome cached = await tokens.GetAsync("cli_test000000", "fake-good-secret", CancellationToken.None);
        Assert.True(cached.FromCache);
        Assert.Equal(first.Token, cached.Token);
        Assert.Single(stub.Requests);

        // 剩余不足 5 分钟：重换(此时剩余已不足 30 分钟，飞书会发新令牌)。
        now = Now.AddMinutes(116);
        Assert.False((await tokens.GetAsync("cli_test000000", "fake-good-secret", CancellationToken.None)).FromCache);
        Assert.Equal(2, stub.Requests.Count);

        // 换了 App Secret:不得复用上一组凭据换来的令牌。
        Assert.False((await tokens.GetAsync("cli_test000000", "fake-good-secret-2", CancellationToken.None)).FromCache);
        Assert.Equal(3, stub.Requests.Count);

        tokens.Invalidate();
        Assert.False((await tokens.GetAsync("cli_test000000", "fake-good-secret-2", CancellationToken.None)).FromCache);
        Assert.Equal(4, stub.Requests.Count);

        // 换令牌失败不进缓存：下一次照样去换。
        Assert.False((await tokens.GetAsync("cli_test000000", "fake-wrong-secret", CancellationToken.None)).Ok);
        Assert.False((await tokens.GetAsync("cli_test000000", "fake-wrong-secret", CancellationToken.None)).Ok);
        Assert.Equal(6, stub.Requests.Count);
    }

    [Fact]
    public async Task 连接失败超时与意外异常都归类不抛_调用方取消照常抛出()
    {
        var down = new FeishuTenantToken(new FeishuTokenStub { Throw = new HttpRequestException(HttpRequestError.NameResolutionError, "no such host") });
        TenantTokenFailure network = (await down.ExchangeAsync("cli_test000000", "fake-good-secret", CancellationToken.None)).Failure!;
        Assert.Equal(TenantTokenFailureKind.Network, network.Kind);
        Assert.Equal("NameResolutionError", network.Detail);
        Assert.Equal("direct", network.Route);

        var slow = new FeishuTenantToken(new FeishuTokenStub { Delay = TimeSpan.FromSeconds(30) }, timeout: TimeSpan.FromMilliseconds(100));
        Assert.Equal(TenantTokenFailureKind.Timeout,
            (await slow.ExchangeAsync("cli_test000000", "fake-good-secret", CancellationToken.None)).Failure!.Kind);

        // 应答 charset 不认识:读应答时抛 InvalidOperationException,仍归通道级的应答异常。
        var bogus = new FeishuTenantToken(new FeishuTokenStub { BogusCharset = true });
        TenantTokenFailure unexpected = (await bogus.ExchangeAsync("cli_test000000", "fake-good-secret", CancellationToken.None)).Failure!;
        Assert.Equal(TenantTokenFailureKind.Unexpected, unexpected.Kind);
        Assert.Equal("InvalidOperationException", unexpected.Detail);

        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => new FeishuTenantToken(new FeishuTokenStub()).ExchangeAsync("cli_test000000", "fake-good-secret", cts.Token));
    }

    [Fact]
    public async Task 限流的RetryAfter一路带到熔断时长()
    {
        var limited = new FeishuTenantToken(new FeishuTokenStub { RateLimitRetryAfter = TimeSpan.FromSeconds(7) });

        TenantTokenFailure failure = (await limited.ExchangeAsync("cli_test000000", "fake-good-secret", CancellationToken.None)).Failure!;
        NotifySendResult result = NotifyClassifier.ClassifyTokenExchange(failure);

        Assert.Equal(TenantTokenFailureKind.RateLimited, failure.Kind);
        Assert.Equal("rate_limit", result.Class);
        Assert.Equal(7, result.RetryAfterSeconds);
        Assert.Equal(TimeSpan.FromSeconds(7), CompletionNotifier.ChannelBackoff(result.Class, 1, result.RetryAfterSeconds));
    }

    [Theory]
    // 与 Go net/http 的 ProxyFromEnvironment 同一规则：只看 HTTPS_PROXY;没写 scheme 按 http://;NO_PROXY 命中就直连。
    [InlineData(null, null, null, "direct", null)]
    [InlineData(null, null, "http://127.0.0.1:10808", "direct", null)]
    [InlineData("127.0.0.1:10808", null, null, "env_proxy", "http://127.0.0.1:10808/")]
    [InlineData("socks5://127.0.0.1:10808", null, null, "env_proxy", "socks5://127.0.0.1:10808/")]
    [InlineData("http://127.0.0.1:10808", "feishu.cn", null, "direct(no_proxy)", null)]
    [InlineData("http://127.0.0.1:10808", ".feishu.cn", null, "direct(no_proxy)", null)]
    [InlineData("http://127.0.0.1:10808", "open.feishu.cn:443", null, "direct(no_proxy)", null)]
    [InlineData("http://127.0.0.1:10808", "*", null, "direct(no_proxy)", null)]
    [InlineData("http://127.0.0.1:10808", "larksuite.com,localhost", null, "env_proxy", "http://127.0.0.1:10808/")]
    [InlineData("http://[bad", null, null, "direct(proxy_unparsable)", null)]
    [InlineData("socks5h://127.0.0.1:10808", null, null, "direct(proxy_unsupported)", null)]
    public void 代理只认HTTPS_PROXY与NO_PROXY_解析不出就直连(string? httpsProxy, string? noProxy, string? allProxy, string route, string? proxy)
    {
        var environment = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["HTTPS_PROXY"] = httpsProxy,
            ["NO_PROXY"] = noProxy,
            ["ALL_PROXY"] = allProxy,
        };

        (Uri? resolved, string actual) = FeishuTenantToken.ResolveProxy(name => environment.GetValueOrDefault(name));

        Assert.Equal(route, actual);
        Assert.Equal(proxy, resolved?.ToString());
    }

    [Fact]
    public void 生产处理器不跟随重定向_代理显式给出不落回系统代理_路径说明不带口令()
    {
        using (var direct = (SocketsHttpHandler)FeishuTenantToken.CreateHandler(null))
        {
            // 307/308 会带着含 App Secret 的请求体重发到别处。
            Assert.False(direct.AllowAutoRedirect);
            Assert.False(direct.UseProxy);
        }

        using (var proxied = (SocketsHttpHandler)FeishuTenantToken.CreateHandler(new Uri("http://127.0.0.1:10808")))
        {
            Assert.False(proxied.AllowAutoRedirect);
            Assert.True(proxied.UseProxy);
            Assert.NotNull(proxied.Proxy);
        }

        string described = FeishuTenantToken.DescribeRoute(name => name == "HTTPS_PROXY" ? "http://user:pass@127.0.0.1:10808" : null);
        Assert.Equal("env_proxy http://127.0.0.1:10808", described);
        Assert.DoesNotContain("pass", described, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(TenantTokenFailureKind.Rejected, "auth_config", "rejected")]
    [InlineData(TenantTokenFailureKind.RateLimited, "rate_limit", "rate_limited")]
    [InlineData(TenantTokenFailureKind.Timeout, "network_timeout", "timeout")]
    [InlineData(TenantTokenFailureKind.Network, "network", "network")]
    [InlineData(TenantTokenFailureKind.ServerError, "network", "server_error")]
    [InlineData(TenantTokenFailureKind.Unexpected, "token_unexpected", "unexpected")]
    public void 换令牌失败一律通道级且结果明确(TenantTokenFailureKind kind, string expected, string subtype)
    {
        NotifySendResult result = NotifyClassifier.ClassifyTokenExchange(
            new TenantTokenFailure(kind, 200, 10014, "app id not exists", "log-1", Route: "direct", DurationMs: 42));

        Assert.False(result.Sent);
        Assert.Equal(expected, result.Class);
        Assert.Equal(NotifyScope.Channel, result.Scope);
        // 消息根本没发出去:不是「可能已送达」,也不进死信。
        Assert.False(result.Ambiguous);
        Assert.False(result.Permanent);
        Assert.Equal("tenant_token", result.ErrorType);
        Assert.Equal(subtype + "@direct", result.ErrorSubtype);
        Assert.Equal(10014, result.ErrorCode);
        Assert.Equal("log-1", result.LogId);
        Assert.Equal(42, result.DurationMs);

        // 细类带上 HTTP 状态与原因，运维不用翻别处就分得清。
        Assert.Equal("server_error:http_502:x@env_proxy", NotifyClassifier.ClassifyTokenExchange(
            new TenantTokenFailure(TenantTokenFailureKind.ServerError, 502, Detail: "x", Route: "env_proxy")).ErrorSubtype);
    }

    [Fact]
    public async Task 有副本时只把AppID与令牌交给lark_cli_用私有配置目录_绝不交AppSecret()
    {
        string root = TestTemp.NewDir("token-send");
        var credentials = new FeishuBotCredentials(root);
        credentials.Save("cli_test000000", "fake-good-secret");
        string fake = FakeSender(root);
        string configDir = Path.Combine(root, "lark-cli");
        var channel = new LarkCliNotificationChannel(credentials, () => fake, tokens: new FeishuTenantToken(new FeishuTokenStub()), cliConfigDirectory: configDir);

        NotifySendResult result = await channel.SendAsync("ou_0123456789abcdef0123456789abcdef", "测试", "0123456789abcdef", CancellationToken.None);

        Assert.True(result.Sent, result.Class);
        Assert.Equal("om_token", result.MessageId);
        Assert.Equal("env", result.CredentialSource);
        Assert.False(result.TokenRetried);
        // 子进程看到的 App ID 与配置目录(假 lark-cli 把它们记进 calls.txt)。
        Assert.Equal("im|cli_test000000|" + configDir, Assert.Single(File.ReadAllLines(Path.Combine(root, "calls.txt"))).Trim());
        Assert.True(Directory.Exists(configDir));

        // 继承来的凭据类变量一律删掉(null = 从子进程环境里移除)。
        Dictionary<string, string?> environment = LarkCliNotificationChannel.TokenEnvironment("cli_test000000", FeishuTokenStub.Good, configDir);
        Assert.Equal(FeishuTokenStub.Good, environment["LARKSUITE_CLI_TENANT_ACCESS_TOKEN"]);
        Assert.Equal("cli_test000000", environment["LARKSUITE_CLI_APP_ID"]);
        Assert.Equal(configDir, environment["LARKSUITE_CLI_CONFIG_DIR"]);
        foreach (string name in new[] { "LARKSUITE_CLI_APP_SECRET", "LARKSUITE_CLI_USER_ACCESS_TOKEN", "LARKSUITE_CLI_DEFAULT_AS", "LARKSUITE_CLI_AUTH_PROXY" })
        {
            Assert.True(environment.ContainsKey(name), name);
            Assert.Null(environment[name]);
        }

        Assert.DoesNotContain(environment.Values, value => value == "fake-good-secret");
        Dictionary<string, string?> keychain = LarkCliNotificationChannel.KeychainEnvironment();
        Assert.Null(keychain["LARKSUITE_CLI_APP_ID"]);
        Assert.Null(keychain["LARKSUITE_CLI_TENANT_ACCESS_TOKEN"]);
        Assert.False(keychain.ContainsKey("LARKSUITE_CLI_CONFIG_DIR"));
    }

    [Fact]
    public async Task 换不到令牌就不启动lark_cli_按凭据问题熔断并带上错误码()
    {
        string root = TestTemp.NewDir("token-refused");
        var credentials = new FeishuBotCredentials(root);
        credentials.Save("cli_test000000", "fake-wrong-secret");
        string fake = FakeSender(root);
        var channel = new LarkCliNotificationChannel(credentials, () => fake, tokens: new FeishuTenantToken(new FeishuTokenStub()),
            cliConfigDirectory: Path.Combine(root, "lark-cli"));

        NotifySendResult result = await channel.SendAsync("ou_0123456789abcdef0123456789abcdef", "测试", "0123456789abcdef", CancellationToken.None);

        Assert.False(result.Sent);
        Assert.Equal("auth_config", result.Class);
        Assert.Equal(NotifyScope.Channel, result.Scope);
        Assert.Equal("tenant_token", result.ErrorType);
        Assert.Equal(10014, result.ErrorCode);
        Assert.Equal("log-10014", result.LogId);
        Assert.Equal("env", result.CredentialSource);
        Assert.Null(result.ExitCode);
        Assert.False(File.Exists(Path.Combine(root, "calls.txt")), "换不到令牌时不得启动 lark-cli(更不能改把 App Secret 交给它)");
    }

    [Fact]
    public async Task 缓存的令牌被飞书判无效_作废后重换一次再发_刚换的令牌被拒不重试()
    {
        string root = TestTemp.NewDir("token-retry");
        var credentials = new FeishuBotCredentials(root);
        credentials.Save("cli_test000000", "fake-good-secret");
        var stub = new FeishuTokenStub(FeishuTokenStub.Old, FeishuTokenStub.Good);
        var tokens = new FeishuTenantToken(stub);
        await tokens.GetAsync("cli_test000000", "fake-good-secret", CancellationToken.None);
        var channel = new LarkCliNotificationChannel(credentials, () => FakeSender(root), tokens: tokens, cliConfigDirectory: Path.Combine(root, "lark-cli"));

        NotifySendResult result = await channel.SendAsync("ou_0123456789abcdef0123456789abcdef", "测试", "0123456789abcdef", CancellationToken.None);

        Assert.True(result.Sent, result.Class);
        Assert.True(result.TokenRetried);
        Assert.Equal(2, stub.Requests.Count);
        Assert.Equal(2, File.ReadAllLines(Path.Combine(root, "calls.txt")).Length);

        // 刚换来的令牌也被拒：再换也一样，不重试，按凭据问题熔断。
        string again = TestTemp.NewDir("token-no-retry");
        var stale = new FeishuTokenStub(FeishuTokenStub.Old, FeishuTokenStub.Old);
        var freshChannel = new LarkCliNotificationChannel(credentials, () => FakeSender(again), tokens: new FeishuTenantToken(stale),
            cliConfigDirectory: Path.Combine(again, "lark-cli"));
        NotifySendResult refused = await freshChannel.SendAsync("ou_0123456789abcdef0123456789abcdef", "测试", "0123456789abcdef", CancellationToken.None);
        Assert.Equal("auth_config", refused.Class);
        Assert.Equal(99991663, refused.ErrorCode);
        Assert.False(refused.TokenRetried);
        Assert.Single(stale.Requests);
        Assert.Single(File.ReadAllLines(Path.Combine(again, "calls.txt")));
    }

    [Theory]
    // 缓存的令牌遇到非鉴权失败(网络)不重试，免得多发一次;lark-cli 本地没读到令牌(token_missing)是契约变化，重换也没用。
    [InlineData(FeishuTokenStub.Network, "network")]
    [InlineData(FeishuTokenStub.Missing, "cli_contract")]
    public async Task 缓存令牌遇到非令牌失效的失败不重试(string token, string expectedClass)
    {
        string root = TestTemp.NewDir("token-no-retry-kind");
        var credentials = new FeishuBotCredentials(root);
        credentials.Save("cli_test000000", "fake-good-secret");
        var stub = new FeishuTokenStub(token, FeishuTokenStub.Good);
        var tokens = new FeishuTenantToken(stub);
        await tokens.GetAsync("cli_test000000", "fake-good-secret", CancellationToken.None);
        var channel = new LarkCliNotificationChannel(credentials, () => FakeSender(root), tokens: tokens, cliConfigDirectory: Path.Combine(root, "lark-cli"));

        NotifySendResult result = await channel.SendAsync("ou_0123456789abcdef0123456789abcdef", "测试", "0123456789abcdef", CancellationToken.None);

        Assert.False(result.Sent);
        Assert.Equal(expectedClass, result.Class);
        Assert.Equal(NotifyScope.Channel, result.Scope);
        Assert.False(result.TokenRetried);
        Assert.Single(stub.Requests);
        Assert.Single(File.ReadAllLines(Path.Combine(root, "calls.txt")));
    }

    [Fact]
    public async Task 副本读不出时如实报错_不静默改用钥匙串_清除后才用钥匙串()
    {
        string root = TestTemp.NewDir("token-unreadable");
        var credentials = new FeishuBotCredentials(root);
        credentials.Save("cli_test000000", "fake-good-secret");
        File.WriteAllBytes(credentials.SecretPath, [1, 2, 3, 4]);
        string fake = FakeSender(root);
        var stub = new FeishuTokenStub();
        var channel = new LarkCliNotificationChannel(credentials, () => fake, tokens: new FeishuTenantToken(stub), cliConfigDirectory: Path.Combine(root, "lark-cli"));

        NotifySendResult broken = await channel.SendAsync("ou_0123456789abcdef0123456789abcdef", "测试", "0123456789abcdef", CancellationToken.None);

        Assert.Equal("credential_unreadable", broken.Class);
        Assert.Equal(NotifyScope.Channel, broken.Scope);
        Assert.Equal("env", broken.CredentialSource);
        Assert.Empty(stub.Requests);
        Assert.False(File.Exists(Path.Combine(root, "calls.txt")));
        // 可能只是正被占用：短退避，不等 30 分钟。
        Assert.Equal(TimeSpan.FromSeconds(60), CompletionNotifier.ChannelBackoff(broken.Class, 1, null));

        credentials.Delete();
        NotifySendResult keychain = await channel.SendAsync("ou_0123456789abcdef0123456789abcdef", "测试", "0123456789abcdef", CancellationToken.None);
        Assert.True(keychain.Sent, keychain.Class);
        Assert.Equal("om_keychain", keychain.MessageId);
        Assert.Equal("keychain", keychain.CredentialSource);
    }

    /// <summary>
    /// 假 lark-cli(发消息):每次启动记一行「命令|APP_ID|CONFIG_DIR」;环境里有 App Secret 就报错;有令牌却没有 App ID
    /// 时报错(真 lark-cli:TENANT_ACCESS_TOKEN is set but LARKSUITE_CLI_APP_ID is missing);令牌是 Good 就成功;
    /// Network 回网络信封;Missing 回 token_missing(lark-cli 本地没读到令牌);别的令牌回 1.0.81 录制的 99991663 信封
    /// (写在 stderr);没有令牌(钥匙串路径)也成功。
    /// </summary>
    internal static string FakeSender(string root)
    {
        string path = Path.Combine(root, "lark-cli-sender.cmd");
        File.WriteAllText(path, string.Join("\r\n",
            "@echo off",
            "echo %1^|%LARKSUITE_CLI_APP_ID%^|%LARKSUITE_CLI_CONFIG_DIR%>> \"%~dp0calls.txt\"",
            "if defined LARKSUITE_CLI_APP_SECRET (",
            "  echo {\"ok\":false,\"error\":{\"type\":\"validation\",\"message\":\"app secret reached lark-cli\"}} 1>&2",
            "  exit /b 2",
            ")",
            "if defined LARKSUITE_CLI_TENANT_ACCESS_TOKEN if not defined LARKSUITE_CLI_APP_ID (",
            "  echo {\"ok\":false,\"error\":{\"type\":\"config\",\"message\":\"LARKSUITE_CLI_TENANT_ACCESS_TOKEN is set but LARKSUITE_CLI_APP_ID is missing\"}} 1>&2",
            "  exit /b 3",
            ")",
            "if \"%LARKSUITE_CLI_TENANT_ACCESS_TOKEN%\"==\"" + FeishuTokenStub.Good + "\" (",
            "  echo {\"ok\":true,\"identity\":\"bot\",\"data\":{\"message_id\":\"om_token\",\"chat_id\":\"oc_test\"}}",
            "  exit /b 0",
            ")",
            "if \"%LARKSUITE_CLI_TENANT_ACCESS_TOKEN%\"==\"" + FeishuTokenStub.Network + "\" (",
            "  echo {\"ok\":false,\"identity\":\"bot\",\"error\":{\"type\":\"network\",\"subtype\":\"transport\",\"message\":\"connection reset\"}} 1>&2",
            "  exit /b 4",
            ")",
            "if \"%LARKSUITE_CLI_TENANT_ACCESS_TOKEN%\"==\"" + FeishuTokenStub.Missing + "\" (",
            "  echo " + FeishuTokenStub.TokenMissingEnvelope + " 1>&2",
            "  exit /b 3",
            ")",
            "if defined LARKSUITE_CLI_TENANT_ACCESS_TOKEN (",
            "  echo " + FeishuTokenStub.TokenInvalidEnvelope + " 1>&2",
            "  exit /b 3",
            ")",
            "echo {\"ok\":true,\"identity\":\"bot\",\"data\":{\"message_id\":\"om_keychain\",\"chat_id\":\"oc_test\"}}",
            "exit /b 0",
            string.Empty));
        return path;
    }
}

/// <summary>
/// 换令牌端点的桩：按 App Secret 回录制形状。fake-good* 发令牌(构造时给了令牌序列就依次发放),fake-nobot-secret / fake-inactive-secret
/// / fake-doctor-fail-secret 各发一个专用令牌,fake-wrong-secret 回 10014(本机录制的 app id not exists 形状),其余回 10003。
/// 响应头带 X-Tt-Logid。
/// </summary>
internal sealed class FeishuTokenStub(params string[] tokens) : HttpMessageHandler
{
    // 合成令牌：形状照官方示例(t- 前缀、可见 ASCII、足够长)。
    public const string Good = "t-fakegood0000000000000000";
    public const string Old = "t-fakeold00000000000000000";
    public const string NoBot = "t-fakenobot000000000000000";
    public const string Inactive = "t-fakeinactive000000000000";
    public const string DoctorFail = "t-fakedoctorfail0000000000";
    public const string Network = "t-fakenetwork0000000000000";
    public const string Missing = "t-fakemissing0000000000000";

    /// <summary>lark-cli 1.0.81 本机录制:令牌无效时写在 stderr 的错误信封(log_id 为合成值)。</summary>
    public const string TokenInvalidEnvelope =
        "{\"ok\":false,\"identity\":\"bot\",\"error\":{\"type\":\"authentication\",\"subtype\":\"token_invalid\",\"code\":99991663," +
        "\"message\":\"Invalid access token for authorization. Please make a request with token attached.\",\"log_id\":\"log-invalid\"}}";

    /// <summary>lark-cli 1.0.81 本机录制:env 来源里没有可用令牌时的信封。</summary>
    public const string TokenMissingEnvelope =
        "{\"ok\":false,\"identity\":\"bot\",\"error\":{\"type\":\"authentication\",\"subtype\":\"token_missing\"," +
        "\"message\":\"no access token available for bot\",\"hint\":\"run: lark-cli auth login to re-authorize\"}}";

    private readonly Queue<string> _tokens = new(tokens);

    public sealed record Request(HttpMethod Method, string? Uri, string? ContentType, string Body);

    public List<Request> Requests { get; } = [];

    public Exception? Throw { get; init; }

    public TimeSpan Delay { get; init; }

    public bool BogusCharset { get; init; }

    public bool EchoSecretInMessage { get; init; }

    public TimeSpan? RateLimitRetryAfter { get; init; }

    public static string Issue(string token) => "{\"code\":0,\"msg\":\"ok\",\"tenant_access_token\":\"" + token + "\",\"expire\":7200}";

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
        lock (Requests)
        {
            Requests.Add(new Request(request.Method, request.RequestUri?.ToString(), request.Content?.Headers.ContentType?.ToString(), body));
        }

        if (Delay > TimeSpan.Zero)
        {
            await Task.Delay(Delay, cancellationToken);
        }

        if (Throw is not null)
        {
            throw Throw;
        }

        string secret;
        using (JsonDocument doc = JsonDocument.Parse(body))
        {
            secret = doc.RootElement.TryGetProperty("app_secret", out JsonElement value) ? value.GetString() ?? string.Empty : string.Empty;
        }

        if (RateLimitRetryAfter is { } retryAfter)
        {
            var limited = new HttpResponseMessage(HttpStatusCode.TooManyRequests)
            {
                Content = new StringContent("""{"code":99991400,"msg":"too many requests"}""", Encoding.UTF8, "application/json"),
            };
            limited.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(retryAfter);
            return limited;
        }

        (string json, string logId) = secret switch
        {
            _ when EchoSecretInMessage => ("{\"code\":12345,\"msg\":\"bad secret " + secret + "\"}", "log-echo"),
            _ when secret.StartsWith("fake-good", StringComparison.Ordinal) => (Issue(_tokens.Count > 0 ? _tokens.Dequeue() : Good), "log-ok"),
            "fake-nobot-secret" => (Issue(NoBot), "log-ok"),
            "fake-inactive-secret" => (Issue(Inactive), "log-ok"),
            "fake-doctor-fail-secret" => (Issue(DoctorFail), "log-ok"),
            "fake-wrong-secret" => ("""{"code":10014,"msg":"app id not exists"}""", "log-10014"),
            _ => ("""{"code":10003,"data":{},"msg":"invalid param"}""", "log-10003"),
        };
        var content = new ByteArrayContent(Encoding.UTF8.GetBytes(json));
        content.Headers.TryAddWithoutValidation("Content-Type", BogusCharset ? "application/json; charset=bogus-charset" : "application/json; charset=utf-8");
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
        response.Headers.Add("X-Tt-Logid", logId);
        return response;
    }
}
