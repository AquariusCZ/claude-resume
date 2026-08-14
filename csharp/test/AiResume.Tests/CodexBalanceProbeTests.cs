using System.Net;
using AiResume.Worker.Probes;
using Xunit;

namespace AiResume.Tests;

/// <summary>
/// Codex 第三方 provider 余额探测。测试使用假 Codex home 与假 HTTP handler,
/// 不读取用户真实配置、不发真实网络请求、不消耗任何额度。
/// </summary>
public sealed class CodexBalanceProbeTests : IDisposable
{
    private readonly List<string> _dirs = new();

    public void Dispose()
    {
        foreach (string dir in _dirs)
        {
            try
            {
                Directory.Delete(dir, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    [Fact]
    public void 解析ccSwitchSub2Api脚本期望的顶层余额()
    {
        CodexBalanceResult r = CodexBalanceProbe.Parse(
            """{"is_active":true,"remaining":518.52,"unit":"USD"}""");

        Assert.Equal(ProviderReadiness.Ok, r.Readiness);
        Assert.Equal(518.52m, r.Remaining);
        Assert.Equal("USD", r.Unit);
        Assert.Equal("余额 518.52 USD", r.Summary);
    }

    /// <summary>
    /// 2026-08-13 用探针自己的请求头向本机活动 provider 的 /v1/usage 录下的真实 200 响应形状。
    /// 字段名、类型与嵌套层级照抄,金额与账期换成合成值 —— 真实读数是用户的账单数据,不进仓库。
    /// 要点:<c>remaining</c> 是 JSON 数字,<c>isValid</c> 是 JSON 布尔,没有 is_active、
    /// 没有 success/ok/status,也没有 data 外壳。
    /// </summary>
    private const string RecordedUsageBody = """
        {
          "daily_usage": [
            {"date":"2026-08-13","requests":1,"input_tokens":1,"output_tokens":1,
             "cache_read_tokens":1,"cache_write_tokens":0,"total_tokens":3,
             "cost":1.5,"actual_cost":1.5}
          ],
          "isValid": true,
          "mode": "unrestricted",
          "model_stats": [
            {"model":"gpt-5.6-sol","requests":1,"input_tokens":1,"output_tokens":1,
             "cache_creation_tokens":0,"cache_read_tokens":1,"total_tokens":3,
             "cost":1.5,"actual_cost":1.5,"account_cost":1.5}
          ],
          "planName": "合成套餐",
          "remaining": 123.45678901,
          "subscription": {
            "daily_limit_usd": 0, "daily_usage_usd": 1.5,
            "expires_at": "2026-09-30T00:00:00+08:00",
            "monthly_limit_usd": 0, "monthly_usage_usd": 1.5,
            "weekly_limit_usd": 600, "weekly_usage_usd": 476.54321099,
            "weekly_window_start": "2026-08-13T00:00:00+08:00"
          },
          "unit": "USD",
          "usage": {
            "average_duration_ms": 1.0, "rpm": 0, "tpm": 0,
            "today": {"actual_cost":1.5,"cache_creation_tokens":0,"cache_read_tokens":1,
                      "cost":1.5,"input_tokens":1,"output_tokens":1,"requests":1,"total_tokens":3},
            "total": {"actual_cost":1.5,"cache_creation_tokens":0,"cache_read_tokens":1,
                      "cost":1.5,"input_tokens":1,"output_tokens":1,"requests":1,"total_tokens":3}
          }
        }
        """;

    [Fact]
    public void 录下来的真实响应按顶层remaining与isValid判定()
    {
        CodexBalanceResult r = CodexBalanceProbe.Parse(RecordedUsageBody);

        Assert.Equal(ProviderReadiness.Ok, r.Readiness);
        Assert.Equal("ok", r.Reason);
        Assert.Equal(123.45678901m, r.Remaining);
        Assert.Equal("USD", r.Unit);
    }

    [Fact]
    public void 兼容quotaRemaining和顶层balance()
    {
        CodexBalanceResult nested = CodexBalanceProbe.Parse(
            """{"quota":{"remaining":7.50,"unit":"USD"}}""");
        CodexBalanceResult flat = CodexBalanceProbe.Parse(
            """{"balance":3.25}""");

        Assert.Equal(7.50m, nested.Remaining);
        Assert.Equal("USD", nested.Unit);
        Assert.Equal(3.25m, flat.Remaining);
        Assert.Equal("USD", flat.Unit);
    }

    [Theory]
    [InlineData("""{"remaining":"4.50"}""")]
    [InlineData("""{"quota":{"remaining":"4.50"}}""")]
    [InlineData("""{"balance":"4.50"}""")]
    [InlineData("""{"remaining":true}""")]
    [InlineData("""{"remaining":["4.50"]}""")]
    public void 余额字段不是数字时拒绝而不是替上游猜一个数(string body)
    {
        // 上游 usage_script 是无类型 JS,字符串余额会原样透传;这里比上游严一档:
        // 真实端点返回的是 JSON 数字,拿不准的类型宁可显示"未核实"也不点绿。
        CodexBalanceResult r = CodexBalanceProbe.Parse(body);

        Assert.Equal(ProviderReadiness.Unknown, r.Readiness);
        Assert.Equal("invalid-balance-type", r.Reason);
        Assert.Null(r.Remaining);
    }

    [Fact]
    public void 冲突字段严格遵循ccSwitch脚本优先级()
    {
        CodexBalanceResult topLevel = CodexBalanceProbe.Parse(
            """{"remaining":1,"quota":{"remaining":2},"balance":3}""");
        CodexBalanceResult nested = CodexBalanceProbe.Parse(
            """{"quota":{"remaining":2},"balance":3}""");

        Assert.Equal(1m, topLevel.Remaining);
        Assert.Equal(2m, nested.Remaining);
    }

    [Fact]
    public void 零余额显示为不足且保留金额()
    {
        CodexBalanceResult r = CodexBalanceProbe.Parse(
            """{"isValid":true,"quota":{"remaining":0,"unit":"USD"}}""");

        Assert.Equal(ProviderReadiness.Insufficient, r.Readiness);
        Assert.Equal("empty", r.Reason);
        Assert.Equal(0m, r.Remaining);
        Assert.Contains("0 USD", r.Summary);
    }

    [Theory]
    [InlineData("""{"success":false,"remaining":4.50,"unit":"USD"}""")]
    [InlineData("""{"ok":false,"remaining":4.50,"unit":"USD"}""")]
    [InlineData("""{"status":"error","remaining":4.50,"unit":"USD"}""")]
    public void 有效性只认isActive与isValid两个字段(string body)
    {
        // 本机 Sub2API 的 usage_script 判有效性只有一行:
        //   isValid: response?.is_active ?? response?.isValid ?? true
        // success / ok / status 都不在其中。凭空多认几个负向字段,会把上游判成绿的读成红的。
        CodexBalanceResult r = CodexBalanceProbe.Parse(body);

        Assert.Equal(ProviderReadiness.Ok, r.Readiness);
        Assert.Equal(4.50m, r.Remaining);
    }

    [Theory]
    [InlineData("""{"success":true,"is_active":false,"remaining":19.50,"unit":"USD"}""")]
    [InlineData("""{"success":true,"isValid":false,"remaining":19.50,"unit":"USD"}""")]
    public void 显式账户失效压过其它正向字段(string body)
    {
        CodexBalanceResult r = CodexBalanceProbe.Parse(body);

        Assert.Equal(ProviderReadiness.Insufficient, r.Readiness);
        Assert.Equal("invalid", r.Reason);
        Assert.Equal(19.50m, r.Remaining);
        Assert.Contains("账户不可用", r.Summary);
    }

    [Fact]
    public void isActive优先于isValid()
    {
        // usage_script 的 ?? 链是 is_active 在前;两个都在时不能读后一个。
        CodexBalanceResult r = CodexBalanceProbe.Parse(
            """{"is_active":false,"isValid":true,"remaining":19.50,"unit":"USD"}""");

        Assert.Equal(ProviderReadiness.Insufficient, r.Readiness);
        Assert.Equal("invalid", r.Reason);
    }

    [Fact]
    public void 负余额不当成普通余额不足()
    {
        CodexBalanceResult r = CodexBalanceProbe.Parse(
            """{"remaining":-1.25,"unit":"USD"}""");

        Assert.Equal(ProviderReadiness.Unknown, r.Readiness);
        Assert.Equal("invalid-balance", r.Reason);
        Assert.Null(r.Remaining);
    }

    [Theory]
    [InlineData("12345678901234567")]
    [InlineData("USD\nINJECT")]
    public void 非法单位不进入展示文本(string unit)
    {
        string body = System.Text.Json.JsonSerializer.Serialize(new { remaining = 1, unit });

        CodexBalanceResult r = CodexBalanceProbe.Parse(body);

        Assert.Equal(ProviderReadiness.Unknown, r.Readiness);
        Assert.Equal("invalid-unit", r.Reason);
        Assert.Null(r.Remaining);
    }

    [Theory]
    [InlineData("https://relay.example", "https://relay.example/v1/usage")]
    [InlineData("https://relay.example/v1", "https://relay.example/v1/usage")]
    [InlineData("https://relay.example/v1/", "https://relay.example/v1/usage")]
    public void 余额端点兼容根地址与v1地址(string baseUrl, string expected)
    {
        Assert.Equal(expected, CodexBalanceProbe.BuildUsageUrl(baseUrl));
    }

    [Fact]
    public void 缺少余额时报未知而不是编造数字()
    {
        CodexBalanceResult r = CodexBalanceProbe.Parse("""{"is_active":true}""");

        Assert.Equal(ProviderReadiness.Unknown, r.Readiness);
        Assert.Equal("no-balance", r.Reason);
        Assert.Null(r.Remaining);
    }

    [Fact]
    public void 不把data外壳当成余额来源()
    {
        // usage_script 只看顶层与 quota 两层,没有 data 外壳。多认一层就是替上游发明协议:
        // 真实端点从不这么返回,而认了之后一旦别的 relay 用 data 装别的东西就会读出错数。
        CodexBalanceResult r = CodexBalanceProbe.Parse(
            """{"success":true,"data":{"remaining":19.5,"unit":"USD"}}""");

        Assert.Equal(ProviderReadiness.Unknown, r.Readiness);
        Assert.Equal("no-balance", r.Reason);
        Assert.Null(r.Remaining);
    }

    [Theory]
    [InlineData("")]
    [InlineData("{ 坏 JSON")]
    [InlineData("[1,2,3]")]
    public void 响应损坏时报malformed且不抛(string body)
    {
        CodexBalanceResult r = CodexBalanceProbe.Parse(body);

        Assert.Equal(ProviderReadiness.Unknown, r.Readiness);
        Assert.Equal("malformed", r.Reason);
    }

    [Fact]
    public async Task 请求使用活动provider并带Bearer但摘要不泄露凭据()
    {
        string home = NewCodexHome();
        var handler = new CapturingHandler(
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"remaining":9.99,"unit":"USD"}"""),
            });

        CodexBalanceResult r = await new CodexBalanceProbe(home, handler).ProbeAsync();

        Assert.Equal(ProviderReadiness.Ok, r.Readiness);
        Assert.Equal("https://relay.example.invalid/v1/usage", handler.Uri);
        Assert.Equal("Bearer sk-test-secret", handler.Authorization);
        Assert.Contains("Mozilla/5.0", handler.UserAgent);
        Assert.DoesNotContain("sk-test-secret", r.Summary, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("relay.example", r.Summary, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task 无认证Provider匿名探测余额且不发送Authorization()
    {
        string home = NewCodexHome(requiresOpenAiAuth: false, authJson: null);
        var handler = new CapturingHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""{"remaining":6.25,"unit":"USD"}"""),
        });

        CodexBalanceResult result = await new CodexBalanceProbe(home, handler).ProbeAsync();

        Assert.Equal(ProviderReadiness.Ok, result.Readiness);
        Assert.Equal(6.25m, result.Remaining);
        Assert.Null(handler.Authorization);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task 匿名余额端点拒绝访问时说明需要凭据(HttpStatusCode status)
    {
        string home = NewCodexHome(requiresOpenAiAuth: false, authJson: null);
        var handler = new CapturingHandler(new HttpResponseMessage(status));

        CodexBalanceResult result = await new CodexBalanceProbe(home, handler).ProbeAsync();

        Assert.Equal(ProviderReadiness.Auth, result.Readiness);
        Assert.Equal("http-" + (int)status, result.Reason);
        Assert.Equal("余额接口需要凭据", result.Summary);
        Assert.Null(handler.Authorization);
    }

    /// <summary>
    /// 2026-08-13 用默认 UA(而不是探针的浏览器 UA)请求本机活动 provider 的 /v1/usage,
    /// 录到的真实 403 响应。域名与 ray_id 换成合成值,其余字段名与类型照抄。
    /// 换成浏览器 UA 后,同一把凭据同一个端点立刻返回 200。
    /// </summary>
    private const string RecordedCloudflareBlockBody = """
        {
          "type": "https://developers.cloudflare.com/support/troubleshooting/http-status-codes/cloudflare-1xxx-errors/error-1010/",
          "title": "Error 1010: Access denied",
          "status": 403,
          "detail": "The site owner has blocked access based on your browser's signature.",
          "instance": "0000000000000000",
          "error_code": 1010,
          "error_name": "browser_signature_banned",
          "error_category": "access_denied",
          "ray_id": "0000000000000000",
          "timestamp": "2026-08-13T05:31:38Z",
          "zone": "relay.example.invalid",
          "cloudflare_error": true,
          "retryable": false,
          "owner_action_required": true
        }
        """;

    [Fact]
    public async Task CDN拦截的403不说成凭据被拒()
    {
        string home = NewCodexHome();
        var handler = new CapturingHandler(new HttpResponseMessage(HttpStatusCode.Forbidden)
        {
            Content = new StringContent(RecordedCloudflareBlockBody),
        });

        CodexBalanceResult r = await new CodexBalanceProbe(home, handler).ProbeAsync();

        // 拦的是客户端不是凭据:红着说"拒绝凭据"会把人往换 key 的方向带,而换 UA 就能通。
        Assert.Equal(ProviderReadiness.Unknown, r.Readiness);
        Assert.Equal("cdn-blocked", r.Reason);
        Assert.Equal("余额接口被 CDN 拦截(非凭据问题)", r.Summary);
    }

    [Fact]
    public async Task 普通403仍然按凭据被拒处理()
    {
        string home = NewCodexHome();
        var handler = new CapturingHandler(new HttpResponseMessage(HttpStatusCode.Forbidden)
        {
            Content = new StringContent("""{"error":{"message":"invalid api key","type":"forbidden"}}"""),
        });

        CodexBalanceResult r = await new CodexBalanceProbe(home, handler).ProbeAsync();

        Assert.Equal(ProviderReadiness.Auth, r.Readiness);
        Assert.Equal("http-403", r.Reason);
        Assert.Equal("余额接口拒绝凭据", r.Summary);
    }

    [Fact]
    public async Task CDN拦截可以续用十分钟内的最近余额()
    {
        string home = NewCodexHome();
        DateTimeOffset now = new(2026, 8, 13, 12, 0, 0, TimeSpan.Zero);
        var handler = new SequenceHandler(
            () => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"remaining":8,"unit":"USD"}"""),
            },
            () => new HttpResponseMessage(HttpStatusCode.Forbidden)
            {
                Content = new StringContent(RecordedCloudflareBlockBody),
            });
        var probe = new CodexBalanceProbe(home, handler, retryDelay: TimeSpan.Zero, clock: () => now);

        Assert.Equal(ProviderReadiness.Ok, (await probe.ProbeAsync()).Readiness);
        now = now.AddMinutes(1);
        CodexBalanceResult blocked = await probe.ProbeAsync();

        Assert.True(blocked.IsStale);
        Assert.Equal("stale-cdn-blocked", blocked.Reason);
        Assert.Equal(8m, blocked.Remaining);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task 携带Bearer的余额端点拒绝访问时说明凭据被拒(HttpStatusCode status)
    {
        string home = NewCodexHome();
        var handler = new CapturingHandler(new HttpResponseMessage(status));

        CodexBalanceResult result = await new CodexBalanceProbe(home, handler).ProbeAsync();

        Assert.Equal(ProviderReadiness.Auth, result.Readiness);
        Assert.Equal("余额接口拒绝凭据", result.Summary);
        Assert.Equal("Bearer sk-test-secret", handler.Authorization);
    }

    [Fact]
    public async Task 余额请求复用Provider查询参数与自定义请求头()
    {
        string home = NewCodexHome(
            providerExtra:
            """
              query_params = { tenant = "alpha" }
              http_headers = { X-Static = "static" }
              env_http_headers = { X-Env = "BALANCE_HEADER" }
            """);
        var handler = new CapturingHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""{"remaining":4.25,"unit":"USD"}"""),
        });

        CodexBalanceResult result = await new CodexBalanceProbe(
            home,
            handler,
            environmentVariable: name => name == "BALANCE_HEADER" ? "from-environment" : null).ProbeAsync();

        Assert.Equal(ProviderReadiness.Ok, result.Readiness);
        Assert.Equal("https://relay.example.invalid/v1/usage?tenant=alpha", handler.Uri);
        Assert.Equal("static", handler.Header("X-Static"));
        Assert.Equal("from-environment", handler.Header("X-Env"));
    }

    [Fact]
    public async Task 自定义Authorization使余额401按凭据被拒分类()
    {
        string home = NewCodexHome(
            authJson: null,
            requiresOpenAiAuth: false,
            providerExtra: "  http_headers = { Authorization = \"Bearer relay-header-token\" }\n");
        var handler = new CapturingHandler(new HttpResponseMessage(HttpStatusCode.Unauthorized));

        CodexBalanceResult result = await new CodexBalanceProbe(home, handler).ProbeAsync();

        Assert.Equal(ProviderReadiness.Auth, result.Readiness);
        Assert.Equal("余额接口拒绝凭据", result.Summary);
        Assert.Equal("Bearer relay-header-token", handler.Authorization);
    }

    [Fact]
    public async Task 响应头返回后响应体卡住仍受整体超时约束()
    {
        string home = NewCodexHome();
        var handler = new SequenceHandler(
            () => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(new NeverEndingReadStream()),
            });

        CodexBalanceResult result = await new CodexBalanceProbe(
            home,
            handler,
            TimeSpan.FromMilliseconds(50),
            retryDelay: TimeSpan.Zero).ProbeAsync();

        Assert.Equal(ProviderReadiness.Timeout, result.Readiness);
        Assert.Equal("timeout", result.Reason);
        Assert.Equal(2, handler.Count);
    }

    [Fact]
    public async Task 余额接口404只说明不支持不影响主可用性判定()
    {
        string home = NewCodexHome();
        var handler = new CapturingHandler(new HttpResponseMessage(HttpStatusCode.NotFound));

        CodexBalanceResult r = await new CodexBalanceProbe(home, handler).ProbeAsync();

        Assert.Equal(ProviderReadiness.Unknown, r.Readiness);
        Assert.Equal("not-supported", r.Reason);
        Assert.Equal("余额接口不可用", r.Summary);
    }

    [Fact]
    public async Task 官方OpenAi不尝试第三方余额路由()
    {
        string home = NewCodexHome(
            baseUrl: "https://unused.example",
            providerId: "openai",
            includeProviderTable: false,
            authJson: """{"auth_mode":"apikey","OPENAI_API_KEY":"sk-test-secret"}""");
        var handler = new CapturingHandler(new HttpResponseMessage(HttpStatusCode.OK));

        CodexBalanceResult result = await new CodexBalanceProbe(home, handler).ProbeAsync();

        Assert.Equal(ProviderReadiness.Unknown, result.Readiness);
        Assert.Equal("official-provider", result.Reason);
        Assert.Null(handler.Uri);
    }

    [Fact]
    public async Task 内置OpenAi配置第三方BaseUrl时仍探测余额()
    {
        string home = NewCodexHome(
            providerId: "openai",
            includeProviderTable: false,
            openAiBaseUrl: "https://relay.example.invalid/v1");
        var handler = new CapturingHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""{"remaining":8.75,"unit":"USD"}"""),
        });

        CodexBalanceResult result = await new CodexBalanceProbe(home, handler).ProbeAsync();

        Assert.Equal(ProviderReadiness.Ok, result.Readiness);
        Assert.Equal(8.75m, result.Remaining);
        Assert.Equal("https://relay.example.invalid/v1/usage", handler.Uri);
    }

    [Fact]
    public async Task 自定义名称指向官方OpenAi主机也不尝试第三方余额路由()
    {
        string home = NewCodexHome(baseUrl: "https://api.openai.com/v1", providerId: "CompanyOpenAI");
        var handler = new CapturingHandler(new HttpResponseMessage(HttpStatusCode.OK));

        CodexBalanceResult result = await new CodexBalanceProbe(home, handler).ProbeAsync();

        Assert.Equal(ProviderReadiness.Unknown, result.Readiness);
        Assert.Equal("official-provider", result.Reason);
        Assert.Null(handler.Uri);
    }

    [Fact]
    public async Task 第三方Provider不把ChatGPT登录令牌发给额外余额路由()
    {
        string home = NewCodexHome(
            authJson: """{"auth_mode":"chatgpt","tokens":{"access_token":"chatgpt-access","account_id":"acct-1"}}""");
        var handler = new CapturingHandler(new HttpResponseMessage(HttpStatusCode.OK));

        CodexBalanceResult result = await new CodexBalanceProbe(home, handler).ProbeAsync();

        Assert.Equal(ProviderReadiness.Unknown, result.Readiness);
        Assert.Equal("oauth-not-supported", result.Reason);
        Assert.Null(handler.Uri);
    }

    [Fact]
    public async Task 余额不足单独表达且不重试()
    {
        string home = NewCodexHome();
        var handler = new SequenceHandler(
            () => new HttpResponseMessage(HttpStatusCode.PaymentRequired));

        CodexBalanceResult r = await new CodexBalanceProbe(
            home,
            handler,
            retryDelay: TimeSpan.Zero).ProbeAsync();

        Assert.Equal(ProviderReadiness.Insufficient, r.Readiness);
        Assert.Equal("http-402", r.Reason);
        Assert.Equal("余额不足或需充值", r.Summary);
        Assert.Equal(1, handler.Count);
    }

    [Fact]
    public async Task 限流不是余额不足也不立即重试()
    {
        string home = NewCodexHome();
        var handler = new SequenceHandler(
            () => new HttpResponseMessage(HttpStatusCode.TooManyRequests));

        CodexBalanceResult r = await new CodexBalanceProbe(
            home,
            handler,
            retryDelay: TimeSpan.Zero).ProbeAsync();

        // 限流只说明"这次没问出来",不能红着说余额不够;立即重试还会把限流窗口拖得更长。
        Assert.Equal(ProviderReadiness.Unknown, r.Readiness);
        Assert.Equal("http-429", r.Reason);
        Assert.Equal("余额接口被限流", r.Summary);
        Assert.Equal(1, handler.Count);
    }

    [Fact]
    public async Task 限流可以续用十分钟内的最近余额()
    {
        string home = NewCodexHome();
        DateTimeOffset now = new(2026, 8, 13, 12, 0, 0, TimeSpan.Zero);
        var handler = new SequenceHandler(
            () => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"remaining":8,"unit":"USD"}"""),
            },
            () => new HttpResponseMessage(HttpStatusCode.TooManyRequests));
        var probe = new CodexBalanceProbe(home, handler, retryDelay: TimeSpan.Zero, clock: () => now);

        Assert.Equal(ProviderReadiness.Ok, (await probe.ProbeAsync()).Readiness);
        now = now.AddMinutes(1);
        CodexBalanceResult throttled = await probe.ProbeAsync();

        Assert.True(throttled.IsStale);
        Assert.Equal("stale-http-429", throttled.Reason);
        Assert.Equal(8m, throttled.Remaining);
        Assert.Contains("最近余额 8 USD", throttled.Summary);
    }

    [Fact]
    public async Task 网络失败重试一次并采用第二次成功()
    {
        string home = NewCodexHome();
        var handler = new SequenceHandler(
            () => throw new HttpRequestException("connection reset"),
            () => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"remaining":12.5,"unit":"USD"}"""),
            });

        CodexBalanceResult result = await new CodexBalanceProbe(
            home,
            handler,
            retryDelay: TimeSpan.Zero).ProbeAsync();

        Assert.Equal(2, handler.Count);
        Assert.Equal(ProviderReadiness.Ok, result.Readiness);
        Assert.Equal(12.5m, result.Remaining);
        Assert.False(result.IsStale);
    }

    [Fact]
    public async Task 服务端5xx不立即重试()
    {
        string home = NewCodexHome();
        var handler = new SequenceHandler(
            () => new HttpResponseMessage(HttpStatusCode.InternalServerError),
            () => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"remaining":12.5,"unit":"USD"}"""),
            });

        CodexBalanceResult result = await new CodexBalanceProbe(
            home,
            handler,
            retryDelay: TimeSpan.Zero).ProbeAsync();

        // 5xx 是服务端已经答复过的失败,马上再问一次多半还是同一个答案,只是多打一次。
        Assert.Equal(1, handler.Count);
        Assert.Equal(ProviderReadiness.Unreachable, result.Readiness);
        Assert.Equal("http-500", result.Reason);
    }

    [Fact]
    public async Task 最近成功后瞬时失败十分钟内保留余额但标为琥珀证据()
    {
        string home = NewCodexHome();
        DateTimeOffset now = new(2026, 8, 13, 12, 0, 0, TimeSpan.Zero);
        var handler = new SequenceHandler(
            () => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"remaining":8,"unit":"USD"}"""),
            },
            () => new HttpResponseMessage(HttpStatusCode.InternalServerError),
            () => new HttpResponseMessage(HttpStatusCode.InternalServerError));
        var probe = new CodexBalanceProbe(
            home,
            handler,
            retryDelay: TimeSpan.Zero,
            clock: () => now);

        CodexBalanceResult fresh = await probe.ProbeAsync();
        now = now.AddMinutes(9);
        CodexBalanceResult stale = await probe.ProbeAsync();

        Assert.False(fresh.IsStale);
        Assert.True(stale.IsStale);
        Assert.Equal(ProviderReadiness.Ok, stale.Readiness);
        Assert.Equal("stale-http-500", stale.Reason);
        Assert.Equal(8m, stale.Remaining);
        Assert.Contains("最近余额 8 USD", stale.Summary);
    }

    [Fact]
    public async Task 确定性失败清空最近余额且后续网络失败不能复活旧值()
    {
        string home = NewCodexHome();
        var handler = new SequenceHandler(
            () => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"remaining":8,"unit":"USD"}"""),
            },
            () => new HttpResponseMessage(HttpStatusCode.Unauthorized),
            () => new HttpResponseMessage(HttpStatusCode.InternalServerError),
            () => new HttpResponseMessage(HttpStatusCode.InternalServerError));
        var probe = new CodexBalanceProbe(home, handler, retryDelay: TimeSpan.Zero);

        Assert.Equal(ProviderReadiness.Ok, (await probe.ProbeAsync()).Readiness);
        Assert.Equal(ProviderReadiness.Auth, (await probe.ProbeAsync()).Readiness);
        CodexBalanceResult afterNetworkFailure = await probe.ProbeAsync();

        Assert.Equal(ProviderReadiness.Unreachable, afterNetworkFailure.Readiness);
        Assert.False(afterNetworkFailure.IsStale);
        Assert.Null(afterNetworkFailure.Remaining);
    }

    [Fact]
    public async Task 最近余额按Provider身份隔离且超出十分钟自动失效()
    {
        string home = NewCodexHome();
        DateTimeOffset now = new(2026, 8, 13, 12, 0, 0, TimeSpan.Zero);
        var handler = new SequenceHandler(
            () => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"remaining":8,"unit":"USD"}"""),
            },
            () => new HttpResponseMessage(HttpStatusCode.InternalServerError),
            () => new HttpResponseMessage(HttpStatusCode.InternalServerError),
            () => new HttpResponseMessage(HttpStatusCode.InternalServerError),
            () => new HttpResponseMessage(HttpStatusCode.InternalServerError));
        var probe = new CodexBalanceProbe(
            home,
            handler,
            retryDelay: TimeSpan.Zero,
            clock: () => now);
        CodexProviderCredentials providerA = CodexAuthProbe.ReadActiveProviderCredentials(home);
        CodexProviderCredentials providerB = providerA with { ProviderId = "other-provider" };

        Assert.Equal(ProviderReadiness.Ok, (await probe.ProbeAsync(providerA)).Readiness);
        CodexBalanceResult otherProviderFailure = await probe.ProbeAsync(providerB);
        now = now.AddMinutes(10);
        CodexBalanceResult expired = await probe.ProbeAsync(providerA);

        Assert.False(otherProviderFailure.IsStale);
        Assert.False(expired.IsStale);
        Assert.Equal(ProviderReadiness.Unreachable, expired.Readiness);
    }

    [Fact]
    public async Task baseUrl格式无效时余额探测返回未配置而不是抛()
    {
        string home = NewCodexHome("not a url");

        CodexBalanceResult r = await new CodexBalanceProbe(home).ProbeAsync();

        Assert.Equal(ProviderReadiness.NoCredential, r.Readiness);
        Assert.Equal("invalid-url", r.Reason);
    }

    [Fact]
    public async Task 超过64KiB的成功响应拒绝读取()
    {
        string home = NewCodexHome();
        string oversized = "{\"remaining\":1,\"padding\":\"" + new string('x', 70 * 1024) + "\"}";
        var handler = new CapturingHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(oversized),
        });

        CodexBalanceResult r = await new CodexBalanceProbe(home, handler).ProbeAsync();

        Assert.Equal(ProviderReadiness.Unknown, r.Readiness);
        Assert.Equal("response-too-large", r.Reason);
        Assert.Null(r.Remaining);
    }

    [Fact]
    public async Task 调用方取消余额探测会继续传播取消()
    {
        string home = NewCodexHome();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => new CodexBalanceProbe(home, new CancellationHandler()).ProbeAsync(cts.Token));
    }

    [Theory]
    [InlineData("http://relay.example")]
    [InlineData("https://user:pass@relay.example")]
    [InlineData("https://relay.example?token=x")]
    [InlineData("https://relay.example#fragment")]
    public async Task 不安全baseUrl不发余额请求(string baseUrl)
    {
        string home = NewCodexHome(baseUrl);
        var handler = new CapturingHandler(new HttpResponseMessage(HttpStatusCode.OK));

        CodexBalanceResult r = await new CodexBalanceProbe(home, handler).ProbeAsync();

        Assert.Equal(ProviderReadiness.NoCredential, r.Readiness);
        Assert.Equal("invalid-url", r.Reason);
        Assert.Null(handler.Uri);
    }

    private string NewCodexHome(
        string baseUrl = "https://relay.example.invalid",
        string providerId = "Sub2API",
        bool includeProviderTable = true,
        string? authJson = """{"OPENAI_API_KEY":"sk-test-secret"}""",
        string? openAiBaseUrl = null,
        bool requiresOpenAiAuth = true,
        string? providerExtra = null)
    {
        string dir = TestTemp.NewDir("codex-balance-home");
        _dirs.Add(dir);
        string providerTable = includeProviderTable
            ? """

              [model_providers.__PROVIDER__]
              base_url = "__BASE_URL__"
              wire_api = "responses"
              requires_openai_auth = __REQUIRES_OPENAI_AUTH__
              __PROVIDER_EXTRA__
              """
                .Replace("__PROVIDER__", providerId)
                .Replace("__BASE_URL__", baseUrl)
                .Replace("__REQUIRES_OPENAI_AUTH__", requiresOpenAiAuth ? "true" : "false")
                .Replace("__PROVIDER_EXTRA__", providerExtra ?? string.Empty)
            : string.Empty;
        string builtInOverride = openAiBaseUrl is null
            ? string.Empty
            : $"openai_base_url = \"{openAiBaseUrl}\"\n";
        File.WriteAllText(
            Path.Combine(dir, "config.toml"),
            $"model_provider = \"{providerId}\"\nmodel = \"gpt-5.5\"\n{builtInOverride}{providerTable}");
        if (authJson is not null)
        {
            File.WriteAllText(Path.Combine(dir, "auth.json"), authJson);
        }
        return dir;
    }

    private sealed class CapturingHandler : HttpMessageHandler
    {
        private readonly HttpResponseMessage _response;

        public CapturingHandler(HttpResponseMessage response) => _response = response;

        public string? Uri { get; private set; }

        public string? Authorization { get; private set; }

        public string? UserAgent { get; private set; }

        private Dictionary<string, string> Headers { get; } = new(StringComparer.OrdinalIgnoreCase);

        public string? Header(string name) => Headers.TryGetValue(name, out string? value) ? value : null;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Uri = request.RequestUri?.ToString();
            Authorization = request.Headers.TryGetValues("Authorization", out IEnumerable<string>? auth)
                ? string.Join(",", auth)
                : null;
            UserAgent = request.Headers.TryGetValues("User-Agent", out IEnumerable<string>? userAgent)
                ? string.Join(" ", userAgent)
                : null;
            Headers.Clear();
            foreach ((string name, IEnumerable<string> values) in request.Headers)
            {
                Headers[name] = string.Join(",", values);
            }

            return Task.FromResult(_response);
        }
    }

    private sealed class SequenceHandler : HttpMessageHandler
    {
        private readonly Func<HttpResponseMessage>[] _responses;

        public SequenceHandler(params Func<HttpResponseMessage>[] responses) => _responses = responses;

        public int Count { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Func<HttpResponseMessage> response = _responses[Math.Min(Count, _responses.Length - 1)];
            Count++;
            return Task.FromResult(response());
        }
    }

    private sealed class CancellationHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromCanceled<HttpResponseMessage>(ct);
    }

    private sealed class NeverEndingReadStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush() => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }
    }
}
