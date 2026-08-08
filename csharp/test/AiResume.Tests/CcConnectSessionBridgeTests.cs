using System.Text.Json;
using AiResume.Wrapper;
using Xunit;

namespace AiResume.Tests;

/// <summary>
/// S6-B 会话生命周期桥回归:14/30 天清理阈值、工作会话保护、fail-closed、
/// mock 端点照抄 S4 实证会话形状。全部离线,不发任何真实请求。
/// </summary>
public sealed class CcConnectSessionBridgeTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 5, 12, 0, 0, TimeSpan.Zero);

    /// <summary>记录归档/删除调用的假端点(fail-closed 语义由测试注入控制)。</summary>
    private sealed class FakeClient : ICcConnectSessionClient
    {
        public List<CcSessionRef> Sessions { get; } = new();
        public List<string> Archived { get; } = new();
        public List<string> Deleted { get; } = new();
        public Exception? ListException { get; set; }
        public string? SessionJson { get; set; }

        public Task<IReadOnlyList<CcSessionRef>> ListSessionsAsync(string project, CancellationToken cancellationToken = default)
        {
            if (ListException is not null)
            {
                throw ListException;
            }

            return Task.FromResult<IReadOnlyList<CcSessionRef>>(Sessions);
        }

        public Task<JsonDocument?> GetSessionAsync(string project, string sessionKey, CancellationToken cancellationToken = default) =>
            Task.FromResult(SessionJson is null ? null : JsonDocument.Parse(SessionJson));

        public Task ArchiveSessionAsync(string project, string sessionKey, CancellationToken cancellationToken = default)
        {
            Archived.Add(sessionKey);
            return Task.CompletedTask;
        }

        public Task DeleteSessionAsync(string project, string sessionKey, CancellationToken cancellationToken = default)
        {
            Deleted.Add(sessionKey);
            return Task.CompletedTask;
        }
    }

    private static CcSessionRef Ref(CcSessionKind kind, int ageDays, string key = "feishu:oc_x:ou_y") =>
        new("pilot", key, kind, Now.AddDays(-ageDays));

    // ---- 纯判定阈值(对齐 session-manager.js cleanup 的 if/else if 顺序)----

    [Theory]
    [InlineData(0, CleanupAction.Keep)]
    [InlineData(13, CleanupAction.Keep)]
    [InlineData(14, CleanupAction.Archive)]   // 边界:age >= archiveDays 即归档。
    [InlineData(29, CleanupAction.Archive)]
    [InlineData(30, CleanupAction.Delete)]    // 边界:age >= deleteDays 即删除(先判删除)。
    [InlineData(100, CleanupAction.Delete)]
    public void Classify_chat_follows_14_30_thresholds(int ageDays, CleanupAction expected)
    {
        var bridge = new CcConnectSessionBridge(new FakeClient());
        Assert.Equal(expected, bridge.Classify(Ref(CcSessionKind.Chat, ageDays), Now));
    }

    [Fact]
    public void Classify_query_uses_same_thresholds()
    {
        var bridge = new CcConnectSessionBridge(new FakeClient());
        Assert.Equal(CleanupAction.Archive, bridge.Classify(Ref(CcSessionKind.Query, 20), Now));
        Assert.Equal(CleanupAction.Delete, bridge.Classify(Ref(CcSessionKind.Query, 31), Now));
    }

    [Fact]
    public void Classify_work_session_is_always_protected()
    {
        // 现役红线:项目工作会话绝不自动归档或删除,无论多旧。
        var bridge = new CcConnectSessionBridge(new FakeClient());
        Assert.Equal(CleanupAction.ProtectedWork, bridge.Classify(Ref(CcSessionKind.Work, 15), Now));
        Assert.Equal(CleanupAction.ProtectedWork, bridge.Classify(Ref(CcSessionKind.Work, 365), Now));
    }

    [Fact]
    public void Policy_normalizes_to_minimum_one()
    {
        CleanupPolicy policy = CleanupPolicy.Normalize(archiveDays: 0, deleteDays: -5, intervalHours: 0);
        Assert.Equal(1, policy.ArchiveDays);
        Assert.Equal(1, policy.DeleteDays);
        Assert.Equal(1, policy.IntervalHours);
        Assert.Equal(6, new CleanupPolicy().IntervalHours); // 缺省 6 小时扫描。
    }

    // ---- 清理执行 ----

    [Fact]
    public async Task Cleanup_mixed_sessions_routes_correctly()
    {
        var client = new FakeClient();
        client.Sessions.Add(Ref(CcSessionKind.Chat, 1, key: "s:fresh"));
        client.Sessions.Add(Ref(CcSessionKind.Chat, 20, key: "s:old"));
        client.Sessions.Add(Ref(CcSessionKind.Query, 45, key: "s:ancient"));
        client.Sessions.Add(Ref(CcSessionKind.Work, 400, key: "s:work"));
        var bridge = new CcConnectSessionBridge(client);

        CleanupSummary summary = await bridge.RunCleanupAsync("pilot", Now);

        Assert.Equal(new CleanupSummary(Archived: 1, Deleted: 1, Skipped: 1, Protected: 1), summary);
        Assert.Equal(new[] { "s:old" }, client.Archived);
        Assert.Equal(new[] { "s:ancient" }, client.Deleted);
        Assert.DoesNotContain("s:work", client.Archived);  // work 绝不下发归档。
        Assert.DoesNotContain("s:work", client.Deleted);   // work 绝不下发删除。
        Assert.DoesNotContain("s:fresh", client.Deleted);
    }

    [Fact]
    public async Task Cleanup_list_failure_fails_closed()
    {
        // 现役红线:会话读取失败不得冒充空列表继续清理。
        var client = new FakeClient { ListException = new HttpRequestException("connection refused") };
        var bridge = new CcConnectSessionBridge(client);

        await Assert.ThrowsAsync<HttpRequestException>(() => bridge.RunCleanupAsync("pilot", Now));
        Assert.Empty(client.Archived);
        Assert.Empty(client.Deleted);
    }

    [Fact]
    public async Task Cleanup_custom_policy_days_respected()
    {
        var client = new FakeClient();
        client.Sessions.Add(Ref(CcSessionKind.Chat, 3, key: "s:a")); // 3 天 ≥ archiveDays(2)
        var bridge = new CcConnectSessionBridge(client, CleanupPolicy.Normalize(2, 5, 6));

        CleanupSummary summary = await bridge.RunCleanupAsync("pilot", Now);

        Assert.Equal(1, summary.Archived);
        Assert.Equal(new[] { "s:a" }, client.Archived);
    }

    // ---- S4 实证会话形状 ----

    [Fact]
    public async Task Get_session_parses_s4_evidenced_shape()
    {
        // 照抄 S4-B 实证持久化形状(version=1:sessions/active_session/user_sessions/user_meta,
        // 消息级 history[role/content/timestamp]);mock 猜错结构 = 线上静默失效(测试红线)。
        var client = new FakeClient
        {
            SessionJson = """
            {
              "version": 1,
              "active_session": "s2",
              "sessions": {
                "s2": {
                  "key": "bridge:127.0.0.1:web-user",
                  "project": "pilot",
                  "last_user_activity": "2026-08-05T03:00:00Z",
                  "history": [
                    { "role": "user", "content": "读取项目 README 并总结", "timestamp": 1780628400 },
                    { "role": "assistant", "content": "README 概要……", "timestamp": 1780628460 }
                  ]
                }
              },
              "user_sessions": { "web-user": ["s2"] },
              "user_meta": { "web-user": { "last_seen": "2026-08-05T03:00:00Z" } }
            }
            """,
        };
        var bridge = new CcConnectSessionBridge(client);

        using JsonDocument? doc = await bridge.GetSessionAsyncOrThrow("pilot", "s2");
        Assert.NotNull(doc);
        Assert.Equal(1, doc!.RootElement.GetProperty("version").GetInt32());
        JsonElement session = doc.RootElement.GetProperty("sessions").GetProperty("s2");
        Assert.Equal("bridge:127.0.0.1:web-user", session.GetProperty("key").GetString());
        Assert.Equal(2, session.GetProperty("history").GetArrayLength());
        Assert.Equal("user", session.GetProperty("history")[0].GetProperty("role").GetString());
    }
}

