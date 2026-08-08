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

    // ---------- 辅助 ----------

    private static string WriteCredentials(string? json = null)
    {
        string dir = Path.Combine(Path.GetTempPath(), "claude-oauth-tests-" + Guid.NewGuid().ToString("N"));
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

        public FakeHandler(string responseBody, HttpStatusCode statusCode = HttpStatusCode.OK)
        {
            _responseBody = responseBody;
            _statusCode = statusCode;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestCount++;
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
}