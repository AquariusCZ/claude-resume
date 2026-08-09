using System.Net;
using System.Text;
using System.Text.Json;
using AiResume.Worker.Quota;
using Xunit;

namespace AiResume.Tests;

public class ClaudeOAuthUsageProbeTests
{
    private const string TestAccessToken = "test-access-token-UNIQUE-7f3a9c";

    // ---------- 1. 正常响应 → 两个窗口都解析出来 ----------
    [Fact]
    public async Task NormalResponse_ParsesBothWindows()
    {
        string json = """
        {
          "five_hour": { "utilization": 49, "resets_at": "2026-08-06T07:50:00Z" },
          "seven_day": { "utilization": 78, "resets_at": "2026-08-10T07:00:00Z" }
        }
        """;

        using var handler = new FakeHandler(json);
        using var http = new HttpClient(handler);
        var probe = new ClaudeOAuthUsageProbe(http, WriteCredentials());

        var result = await probe.TryFetchAsync(CancellationToken.None);

        Assert.True(result.Ok);
        Assert.Null(result.FailureReason);
        Assert.NotEmpty(result.CredentialFingerprint);
        Assert.DoesNotContain(TestAccessToken, result.CredentialFingerprint, StringComparison.Ordinal);
        Assert.NotNull(result.Snapshot);
        Assert.True(result.Snapshot.HasData);
        Assert.Equal("claudecode", result.Snapshot.Provider);

        var bucket = Assert.Single(result.Snapshot.Buckets);
        Assert.Equal(2, bucket.Windows.Count);

        var fiveHour = bucket.Windows[0];
        Assert.Equal("five_hour", fiveHour.Name);
        Assert.Equal(49, fiveHour.UsedPercent);
        Assert.Equal(UsageWindow.FiveHourSeconds, fiveHour.WindowSeconds);
        Assert.Equal("allowed", fiveHour.Status);
        Assert.Equal(DateTimeOffset.Parse("2026-08-06T07:50:00Z").ToUnixTimeSeconds(), fiveHour.ResetAtUnix);

        var sevenDay = bucket.Windows[1];
        Assert.Equal("seven_day", sevenDay.Name);
        Assert.Equal(78, sevenDay.UsedPercent);
        Assert.Equal(UsageWindow.SevenDaySeconds, sevenDay.WindowSeconds);
        Assert.Equal("allowed", sevenDay.Status);
        Assert.Equal(DateTimeOffset.Parse("2026-08-10T07:00:00Z").ToUnixTimeSeconds(), sevenDay.ResetAtUnix);

        Assert.False(bucket.LimitReached);
        Assert.True(bucket.Allowed);
    }

    // ---------- 2. utilization 为 100 → blocked + LimitReached ----------
    [Fact]
    public async Task Utilization100_MarksBlockedAndLimitReached()
    {
        string json = """
        {
          "five_hour": { "utilization": 100, "resets_at": "2026-08-06T07:50:00Z" },
          "seven_day": { "utilization": 78, "resets_at": "2026-08-10T07:00:00Z" }
        }
        """;

        using var handler = new FakeHandler(json);
        using var http = new HttpClient(handler);
        var probe = new ClaudeOAuthUsageProbe(http, WriteCredentials());

        var result = await probe.TryFetchAsync(CancellationToken.None);

        Assert.True(result.Ok);
        var bucket = Assert.Single(result.Snapshot!.Buckets);
        Assert.Equal("blocked", bucket.Windows[0].Status);
        Assert.True(bucket.LimitReached);
        Assert.False(bucket.Allowed);
    }

    // ---------- 3. utilization 缺失 → UsedPercent 为 null 而不是 0 ----------
    [Fact]
    public async Task MissingUtilization_UsedPercentIsNull()
    {
        string json = """
        {
          "five_hour": { "resets_at": "2026-08-06T07:50:00Z" },
          "seven_day": { "utilization": 78, "resets_at": "2026-08-10T07:00:00Z" }
        }
        """;

        using var handler = new FakeHandler(json);
        using var http = new HttpClient(handler);
        var probe = new ClaudeOAuthUsageProbe(http, WriteCredentials());

        var result = await probe.TryFetchAsync(CancellationToken.None);

        Assert.True(result.Ok);
        var bucket = Assert.Single(result.Snapshot!.Buckets);
        Assert.Null(bucket.Windows[0].UsedPercent);
        Assert.Equal("allowed", bucket.Windows[0].Status);
    }

    [Fact]
    public async Task 请求携带ClaudeCode反限流与OAuth协议头()
    {
        using var handler = new FakeHandler("{}");
        using var http = new HttpClient(handler);
        var probe = new ClaudeOAuthUsageProbe(
            http, WriteCredentials(), userAgent: "claude-code/9.9.9");

        await probe.TryFetchAsync(CancellationToken.None);

        Assert.Equal("Bearer " + TestAccessToken, handler.Headers["Authorization"]);
        Assert.Equal("application/json", handler.Headers["Accept"]);
        Assert.Equal("oauth-2025-04-20", handler.Headers["anthropic-beta"]);
        Assert.Equal("2023-06-01", handler.Headers["anthropic-version"]);
        Assert.Equal("claude-code/9.9.9", handler.Headers["User-Agent"]);
    }

    [Fact]
    public async Task 只有现代Limits数组时仍生成主窗口与Fable()
    {
        string json = """
        {
          "limits": [
            { "kind": "session", "percent": 12, "resets_at": "2026-08-09T18:00:00Z" },
            { "kind": "weekly_all", "percent": 34, "resets_at": "2026-08-10T14:00:00Z" },
            {
              "kind": "weekly_scoped",
              "percent": 56,
              "resets_at": "2026-08-10T14:00:00Z",
              "scope": { "model": { "display_name": "Fable" } }
            }
          ]
        }
        """;
        using var handler = new FakeHandler(json);
        using var http = new HttpClient(handler);
        var probe = new ClaudeOAuthUsageProbe(http, WriteCredentials());

        OAuthUsageResult result = await probe.TryFetchAsync(CancellationToken.None);

        UsageWindow[] windows = Assert.Single(result.Snapshot!.Buckets).Windows.ToArray();
        Assert.Equal(12, Assert.Single(windows, window => window.Name == "five_hour").UsedPercent);
        Assert.Equal(34, Assert.Single(windows, window => window.Name == "seven_day").UsedPercent);
        Assert.Equal(56, Assert.Single(windows, window => window.Name == "weekly_scoped:Fable").UsedPercent);
    }

    [Fact]
    public async Task 现代Limits逐字段优先于残留Legacy窗口()
    {
        string json = """
        {
          "five_hour": { "utilization": 20, "resets_at": "2026-08-09T18:00:00Z" },
          "limits": [
            { "kind": "session", "percent": 100, "resets_at": "2026-08-10T18:00:00Z" }
          ]
        }
        """;
        using var handler = new FakeHandler(json);
        using var http = new HttpClient(handler);
        var probe = new ClaudeOAuthUsageProbe(http, WriteCredentials());

        OAuthUsageResult result = await probe.TryFetchAsync(CancellationToken.None);

        UsageWindow window = Assert.Single(Assert.Single(result.Snapshot!.Buckets).Windows);
        Assert.Equal(100, window.UsedPercent);
        Assert.Equal("blocked", window.Status);
        Assert.Equal(DateTimeOffset.Parse("2026-08-10T18:00:00Z").ToUnixTimeSeconds(), window.ResetAtUnix);
        Assert.True(Assert.Single(result.Snapshot.Buckets).LimitReached);
    }

    [Fact]
    public async Task Scoped缺百分比时仍保留Fable窗口与Reset()
    {
        string json = """
        {
          "limits": [
            {
              "kind": "weekly_scoped",
              "resets_at": "2026-08-10T14:00:00Z",
              "scope": { "model": { "display_name": "Fable" } }
            }
          ]
        }
        """;
        using var handler = new FakeHandler(json);
        using var http = new HttpClient(handler);
        var probe = new ClaudeOAuthUsageProbe(http, WriteCredentials());

        OAuthUsageResult result = await probe.TryFetchAsync(CancellationToken.None);

        UsageWindow window = Assert.Single(Assert.Single(result.Snapshot!.Buckets).Windows);
        Assert.Equal("weekly_scoped:Fable", window.Name);
        Assert.Null(window.UsedPercent);
        Assert.Equal(DateTimeOffset.Parse("2026-08-10T14:00:00Z").ToUnixTimeSeconds(), window.ResetAtUnix);
        Assert.Equal("allowed", window.Status);
    }

    [Fact]
    public async Task 多条Scoped全部显示且后续满额模型不会被隐藏()
    {
        string json = """
        {
          "limits": [
            {
              "kind": "weekly_scoped", "percent": 40,
              "resets_at": "2026-08-10T14:00:00Z",
              "scope": { "model": { "display_name": "Fable" } }
            },
            {
              "kind": "weekly_scoped", "percent": 100,
              "resets_at": "2026-08-10T14:00:00Z",
              "scope": { "model": { "display_name": "Sonnet" } }
            }
          ]
        }
        """;
        using var handler = new FakeHandler(json);
        using var http = new HttpClient(handler);
        var probe = new ClaudeOAuthUsageProbe(http, WriteCredentials());

        OAuthUsageResult result = await probe.TryFetchAsync(CancellationToken.None);

        UsageBucket bucket = Assert.Single(result.Snapshot!.Buckets);
        Assert.Equal(40, Assert.Single(bucket.Windows, window => window.Name == "weekly_scoped:Fable").UsedPercent);
        UsageWindow blocked = Assert.Single(bucket.Windows, window => window.Name == "weekly_scoped:Sonnet");
        Assert.Equal(100, blocked.UsedPercent);
        Assert.Equal("blocked", blocked.Status);
        Assert.True(bucket.LimitReached);
    }

    [Theory]
    [InlineData("{\"five_hour\":{}}")]
    [InlineData("{\"limits\":[{\"kind\":\"session\"}]}")]
    public async Task 空窗口对象不构成额度数据或健康证据(string json)
    {
        using var handler = new FakeHandler(json);
        using var http = new HttpClient(handler);
        var probe = new ClaudeOAuthUsageProbe(http, WriteCredentials());

        OAuthUsageResult result = await probe.TryFetchAsync(CancellationToken.None);

        Assert.True(result.Ok);
        Assert.NotNull(result.Snapshot);
        Assert.False(result.Snapshot.HasData);
        Assert.Empty(Assert.Single(result.Snapshot.Buckets).Windows);
        Assert.NotNull(result.Snapshot.UnavailableReason);
    }

    [Fact]
    public async Task 同名Scoped重排后保持稳定身份且不会交叉承接百分比()
    {
        const string firstJson = """
        {
          "limits": [
            {
              "kind": "weekly_scoped", "percent": 40,
              "resets_at": "2026-08-10T14:00:00Z",
              "scope": { "model": { "id": "model-a", "display_name": "Fable" }, "surface": "alpha" }
            },
            {
              "kind": "weekly_scoped", "percent": 100,
              "resets_at": "2026-08-10T14:00:00Z",
              "scope": { "surface": "beta", "model": { "display_name": "Fable", "id": "model-b" } }
            }
          ]
        }
        """;
        const string reorderedJson = """
        {
          "limits": [
            {
              "kind": "weekly_scoped", "percent": 90,
              "resets_at": "2026-08-10T14:00:00Z",
              "scope": { "model": { "id": "model-b", "display_name": "Fable" }, "surface": "beta" }
            },
            {
              "kind": "weekly_scoped", "percent": 50,
              "resets_at": "2026-08-10T14:00:00Z",
              "scope": { "surface": "alpha", "model": { "display_name": "Fable", "id": "model-a" } }
            }
          ]
        }
        """;

        UsageSnapshot first = await FetchSnapshot(firstJson);
        UsageSnapshot reordered = await FetchSnapshot(reorderedJson);
        UsageWindow firstA = Assert.Single(Assert.Single(first.Buckets).Windows, window => window.UsedPercent == 40);
        UsageWindow firstB = Assert.Single(Assert.Single(first.Buckets).Windows, window => window.UsedPercent == 100);
        UsageWindow secondA = Assert.Single(Assert.Single(reordered.Buckets).Windows, window => window.UsedPercent == 50);
        UsageWindow secondB = Assert.Single(Assert.Single(reordered.Buckets).Windows, window => window.UsedPercent == 90);

        Assert.NotEqual(firstA.Identity, firstB.Identity);
        Assert.Equal(firstA.Identity, secondA.Identity);
        Assert.Equal(firstB.Identity, secondB.Identity);
        Assert.Equal(firstA.Name, secondA.Name);
        Assert.Equal(firstB.Name, secondB.Name);

        UsageSnapshot merged = QuotaService.MergeSparseObservation(
            reordered, first, DateTimeOffset.Parse("2026-08-09T12:00:00Z"));
        Assert.Equal(50, Assert.Single(Assert.Single(merged.Buckets).Windows,
            window => window.Identity == firstA.Identity).UsedPercent);
        Assert.Equal(100, Assert.Single(Assert.Single(merged.Buckets).Windows,
            window => window.Identity == firstB.Identity).UsedPercent);
    }

    [Fact]
    public async Task 同一Scope旧Reset满额不会污染新Reset未满状态()
    {
        const string json = """
        {
          "limits": [
            {
              "kind": "weekly_scoped", "percent": 100,
              "resets_at": "2026-08-09T14:00:00Z",
              "scope": { "model": { "id": "model-a", "display_name": "Fable" } }
            },
            {
              "kind": "weekly_scoped", "percent": 0,
              "resets_at": "2026-08-16T14:00:00Z",
              "scope": { "model": { "display_name": "Fable", "id": "model-a" } }
            }
          ]
        }
        """;

        UsageSnapshot snapshot = await FetchSnapshot(json);
        UsageBucket bucket = Assert.Single(snapshot.Buckets);
        UsageWindow window = Assert.Single(bucket.Windows);

        Assert.Equal(0, window.UsedPercent);
        Assert.Equal(DateTimeOffset.Parse("2026-08-16T14:00:00Z").ToUnixTimeSeconds(), window.ResetAtUnix);
        Assert.False(bucket.LimitReached);
        Assert.True(bucket.Allowed);
    }

    // ---------- 4. resets_at 为 epoch 数字 ----------
    [Fact]
    public async Task EpochResetsAt_ParsesCorrectly()
    {
        long epoch = DateTimeOffset.Parse("2026-08-06T07:50:00Z").ToUnixTimeSeconds();
        string json = $$"""
        {
          "five_hour": { "utilization": 49, "resets_at": {{epoch}} },
          "seven_day": { "utilization": 78, "resets_at": "2026-08-10T07:00:00Z" }
        }
        """;

        using var handler = new FakeHandler(json);
        using var http = new HttpClient(handler);
        var probe = new ClaudeOAuthUsageProbe(http, WriteCredentials());

        var result = await probe.TryFetchAsync(CancellationToken.None);

        Assert.True(result.Ok);
        var bucket = Assert.Single(result.Snapshot!.Buckets);
        Assert.Equal(epoch, bucket.Windows[0].ResetAtUnix);
    }

    // ---------- 5. 凭据文件不存在 → no_credentials,不抛异常 ----------
    [Fact]
    public async Task MissingCredentials_ReturnsNoCredentials()
    {
        string missingPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), ".credentials.json");
        using var handler = new FakeHandler("{}");
        using var http = new HttpClient(handler);
        var probe = new ClaudeOAuthUsageProbe(http, missingPath);

        var result = await probe.TryFetchAsync(CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Null(result.Snapshot);
        Assert.Equal("no_credentials", result.FailureReason);
        Assert.Equal(0, handler.RequestCount);
    }

    // ---------- 6. token 过期 → token_expired,不发请求 ----------
    [Fact]
    public async Task ExpiredToken_ReturnsTokenExpired_NoRequestSent()
    {
        long expiredAt = DateTimeOffset.UtcNow.AddSeconds(-10).ToUnixTimeMilliseconds();
        string credentials = $$"""
        {
          "claudeAiOauth": {
            "accessToken": "{{TestAccessToken}}",
            "expiresAt": {{expiredAt}},
            "scopes": ["user:inference"]
          }
        }
        """;

        string credPath = WriteCredentials(credentials);
        using var handler = new FakeHandler("{}");
        using var http = new HttpClient(handler);
        var probe = new ClaudeOAuthUsageProbe(http, credPath);

        var result = await probe.TryFetchAsync(CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Equal("token_expired", result.FailureReason);
        Assert.NotEmpty(result.CredentialFingerprint);
        Assert.Equal(0, handler.RequestCount);
    }

    // ---------- 7. HTTP 401 → token_rejected_401 ----------
    [Fact]
    public async Task Http401_ReturnsTokenRejected()
    {
        using var handler = new FakeHandler("{}", HttpStatusCode.Unauthorized);
        using var http = new HttpClient(handler);
        var probe = new ClaudeOAuthUsageProbe(http, WriteCredentials());

        var result = await probe.TryFetchAsync(CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Equal("token_rejected_401", result.FailureReason);
    }

    // ---------- 8. HTTP 504 → gateway_timeout ----------
    [Fact]
    public async Task Http504_ReturnsGatewayTimeout()
    {
        using var handler = new FakeHandler("{}", HttpStatusCode.GatewayTimeout);
        using var http = new HttpClient(handler);
        var probe = new ClaudeOAuthUsageProbe(http, WriteCredentials());

        var result = await probe.TryFetchAsync(CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Equal("gateway_timeout", result.FailureReason);
    }

    [Fact]
    public async Task Http429保留账号指纹供同周期快照降级()
    {
        using var handler = new FakeHandler("{}", HttpStatusCode.TooManyRequests);
        using var http = new HttpClient(handler);
        var probe = new ClaudeOAuthUsageProbe(http, WriteCredentials());

        OAuthUsageResult result = await probe.TryFetchAsync(CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Equal("http_429", result.FailureReason);
        Assert.NotEmpty(result.CredentialFingerprint);
    }

    // ---------- 8b. HTTP 408 → gateway_timeout(S10-O/P2 补:结构化超时判据是 408/504 两个,原来只钉了 504) ----------
    [Fact]
    public async Task Http408_ReturnsGatewayTimeout()
    {
        using var handler = new FakeHandler("{}", HttpStatusCode.RequestTimeout);
        using var http = new HttpClient(handler);
        var probe = new ClaudeOAuthUsageProbe(http, WriteCredentials());

        var result = await probe.TryFetchAsync(CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Equal("gateway_timeout", result.FailureReason);
    }

    // ---------- 9. 网络异常 → failed_local ----------
    [Fact]
    public async Task NetworkException_ReturnsFailedLocal()
    {
        using var handler = new ThrowingHandler(new HttpRequestException("connection refused"));
        using var http = new HttpClient(handler);
        var probe = new ClaudeOAuthUsageProbe(http, WriteCredentials());

        var result = await probe.TryFetchAsync(CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Equal("failed_local", result.FailureReason);
        Assert.NotEmpty(result.CredentialFingerprint);
    }

    // ---------- 10. 响应不是 JSON → malformed_response ----------
    [Fact]
    public async Task NonJsonResponse_ReturnsMalformedResponse()
    {
        using var handler = new FakeHandler("<html>not json</html>");
        using var http = new HttpClient(handler);
        var probe = new ClaudeOAuthUsageProbe(http, WriteCredentials());

        var result = await probe.TryFetchAsync(CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Equal("malformed_response", result.FailureReason);
        Assert.NotEmpty(result.CredentialFingerprint);
    }

    // ---------- 11. token 不外泄 ----------
    [Fact]
    public async Task TokenNeverLeaks_IntoFailureReasonsOrSnapshot()
    {
        // 覆盖各失败分支
        var cases = new (string Name, Func<HttpMessageHandler> Handler, string ExpectedReason)[]
        {
            ("401", () => new FakeHandler("{}", HttpStatusCode.Unauthorized), "token_rejected_401"),
            ("403", () => new FakeHandler("{}", HttpStatusCode.Forbidden), "token_rejected_403"),
            ("504", () => new FakeHandler("{}", HttpStatusCode.GatewayTimeout), "gateway_timeout"),
            ("500", () => new FakeHandler("{}", HttpStatusCode.InternalServerError), "http_500"),
            ("network", () => new ThrowingHandler(new HttpRequestException("boom")), "failed_local"),
            ("malformed", () => new FakeHandler("<html>"), "malformed_response"),
        };

        foreach (var (name, handlerFactory, expectedReason) in cases)
        {
            using var handler = handlerFactory();
            using var http = new HttpClient(handler);
            var probe = new ClaudeOAuthUsageProbe(http, WriteCredentials());

            var result = await probe.TryFetchAsync(CancellationToken.None);

            Assert.False(result.Ok);
            Assert.Equal(expectedReason, result.FailureReason);
            Assert.DoesNotContain(TestAccessToken, result.FailureReason);
        }

        // 成功分支:序列化后的 Snapshot 不含 token
        string json = """
        {
          "five_hour": { "utilization": 49, "resets_at": "2026-08-06T07:50:00Z" },
          "seven_day": { "utilization": 78, "resets_at": "2026-08-10T07:00:00Z" }
        }
        """;

        using var okHandler = new FakeHandler(json);
        using var okHttp = new HttpClient(okHandler);
        var okProbe = new ClaudeOAuthUsageProbe(okHttp, WriteCredentials());

        var okResult = await okProbe.TryFetchAsync(CancellationToken.None);
        Assert.True(okResult.Ok);

        string serialized = JsonSerializer.Serialize(okResult.Snapshot);
        Assert.DoesNotContain(TestAccessToken, serialized);
    }

    [Fact]
    public async Task 同一组织轮换AccessToken仍得到同一账号指纹()
    {
        const string organization = "org-stable-test";
        string firstCredentials = $$"""
        {
          "organizationUuid": "{{organization}}",
          "claudeAiOauth": {
            "accessToken": "token-one",
            "expiresAt": {{DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeMilliseconds()}}
          }
        }
        """;
        string secondCredentials = firstCredentials.Replace("token-one", "token-two", StringComparison.Ordinal);

        using var firstHandler = new FakeHandler("{}");
        using var firstHttp = new HttpClient(firstHandler);
        using var secondHandler = new FakeHandler("{}");
        using var secondHttp = new HttpClient(secondHandler);
        OAuthUsageResult first = await new ClaudeOAuthUsageProbe(
            firstHttp, WriteCredentials(firstCredentials)).TryFetchAsync(CancellationToken.None);
        OAuthUsageResult second = await new ClaudeOAuthUsageProbe(
            secondHttp, WriteCredentials(secondCredentials)).TryFetchAsync(CancellationToken.None);

        Assert.NotEmpty(first.CredentialFingerprint);
        Assert.Equal(first.CredentialFingerprint, second.CredentialFingerprint);
        Assert.DoesNotContain(organization, first.CredentialFingerprint, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 显式取消不会被吞成failedLocal()
    {
        using var handler = new CancelingHandler();
        using var http = new HttpClient(handler);
        var probe = new ClaudeOAuthUsageProbe(http, WriteCredentials());
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => probe.TryFetchAsync(cts.Token));
    }

    // ---------- 辅助 ----------

    private static async Task<UsageSnapshot> FetchSnapshot(string json)
    {
        using var handler = new FakeHandler(json);
        using var http = new HttpClient(handler);
        var probe = new ClaudeOAuthUsageProbe(http, WriteCredentials());
        OAuthUsageResult result = await probe.TryFetchAsync(CancellationToken.None);
        Assert.True(result.Ok);
        return Assert.IsType<UsageSnapshot>(result.Snapshot);
    }

    private static string WriteCredentials(string? json = null)
    {
        string dir = TestTemp.NewDir("claude-oauth");
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, ".credentials.json");

        string content = json ?? $$"""
        {
          "claudeAiOauth": {
            "accessToken": "{{TestAccessToken}}",
            "expiresAt": {{DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeMilliseconds()}},
            "scopes": ["user:inference"]
          }
        }
        """;

        File.WriteAllText(path, content);
        return path;
    }

    /// <summary>假 handler:返回固定响应,记录请求次数。</summary>
    private sealed class FakeHandler : HttpMessageHandler
    {
        private readonly string _responseBody;
        private readonly HttpStatusCode _statusCode;

        public int RequestCount { get; private set; }
        public Dictionary<string, string> Headers { get; } = new(StringComparer.OrdinalIgnoreCase);

        public FakeHandler(string responseBody, HttpStatusCode statusCode = HttpStatusCode.OK)
        {
            _responseBody = responseBody;
            _statusCode = statusCode;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestCount++;
            foreach (var header in request.Headers)
            {
                Headers[header.Key] = string.Join(" ", header.Value);
            }
            var response = new HttpResponseMessage(_statusCode)
            {
                Content = new StringContent(_responseBody, Encoding.UTF8, "application/json"),
            };
            return Task.FromResult(response);
        }
    }

    /// <summary>假 handler:直接抛异常模拟网络故障。</summary>
    private sealed class ThrowingHandler : HttpMessageHandler
    {
        private readonly Exception _exception;

        public ThrowingHandler(Exception exception) => _exception = exception;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
            => throw _exception;
    }

    private sealed class CancelingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromCanceled<HttpResponseMessage>(cancellationToken);
    }
}
