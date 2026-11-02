using AiResume.Worker.Resume;
using Xunit;

namespace AiResume.Tests;

/// <summary>
/// 会话尾部证据。夹具逐字段照抄 2026-10-30 本机 Claude Code 2.1.2xx–2.1.308 会话里的真实记录形状
/// (只换掉正文与标识值):限流中断是 <c>model="&lt;synthetic&gt;"</c> + <c>error="rate_limit"</c>
/// 的合成 assistant 记录，权限模式记在主链 user 记录上。
/// </summary>
public sealed class ClaudeSessionEvidenceTests
{
    private const string User =
        """{"parentUuid":null,"isSidechain":false,"promptId":"p1","type":"user","message":{"role":"user","content":"做点事"},"uuid":"u1","timestamp":"2026-10-30T01:00:00.000Z","permissionMode":"bypassPermissions","userType":"external","entrypoint":"claude-desktop","cwd":"D:\\Demo\\ledger-api","sessionId":"11111111-1111-1111-1111-111111111111","version":"2.1.300","gitBranch":"main"}""";

    private const string Assistant =
        """{"parentUuid":"u1","isSidechain":false,"message":{"model":"claude-opus-5-5","id":"msg_01","type":"message","role":"assistant","content":[{"type":"text","text":"好"}],"stop_reason":"end_turn","stop_sequence":null,"usage":{"input_tokens":1,"output_tokens":1}},"requestId":"req_01","requestedModel":"claude-opus-5-5","type":"assistant","uuid":"a1","timestamp":"2026-10-30T01:00:05.000Z","userType":"external","entrypoint":"claude-desktop","cwd":"D:\\Demo\\ledger-api","sessionId":"11111111-1111-1111-1111-111111111111","version":"2.1.300","gitBranch":"main"}""";

    private const string RateLimit =
        """{"parentUuid":"a1","isSidechain":false,"type":"assistant","uuid":"a2","timestamp":"2026-10-30T02:00:00.000Z","message":{"diagnostics":null,"id":"x1","container":null,"model":"<synthetic>","role":"assistant","stop_details":null,"stop_reason":"stop_sequence","stop_sequence":"","type":"message","usage":{"input_tokens":0,"output_tokens":0},"content":[{"type":"text","text":"You've hit your session limit"}],"context_management":null},"requestId":"req_02","apiError":"usage_limit_reached","apiErrorParams":{"rate_limit_info":{}},"quotaLimits":{"status":"rejected","resetsAt":1793001600,"unifiedRateLimitFallbackAvailable":false,"rateLimitType":"five_hour","overageStatus":"rejected","overageDisabledReason":"org_level_disabled","upgradePaths":[],"isUsingOverage":false},"error":"rate_limit","isApiErrorMessage":true,"apiErrorStatus":429,"userType":"external","entrypoint":"claude-desktop","cwd":"D:\\Demo\\ledger-api","sessionId":"11111111-1111-1111-1111-111111111111","version":"2.1.300","gitBranch":"main"}""";

    // 2.1.2xx 早期版本:同样是 rate_limit 合成记录，但没有 quotaLimits。
    private const string LegacyRateLimit =
        """{"parentUuid":"a1","isSidechain":false,"type":"assistant","uuid":"a3","timestamp":"2026-08-08T02:00:00.000Z","message":{"id":"x2","model":"<synthetic>","role":"assistant","stop_reason":"stop_sequence","type":"message","usage":{"input_tokens":0,"output_tokens":0},"content":[{"type":"text","text":"Claude AI usage limit reached"}]},"error":"rate_limit","isApiErrorMessage":true,"apiErrorStatus":429,"version":"2.1.221"}""";

    // 2.1.309 录制形状:原生自动继续撞上流式看门狗(streamCutCause=watchdog)后写下的合成错误记录。
    private const string ServerError =
        """{"parentUuid":"u2","isSidechain":false,"type":"assistant","uuid":"a4","timestamp":"2026-10-30T07:00:00.000Z","message":{"diagnostics":null,"id":"x4","container":null,"model":"<synthetic>","role":"assistant","stop_details":null,"stop_reason":"stop_sequence","stop_sequence":"","type":"message","usage":{"output_tokens_details":null,"input_tokens":0,"output_tokens":0,"cache_creation_input_tokens":0,"cache_read_input_tokens":0,"server_tool_use":{"web_search_requests":0,"web_fetch_requests":0},"service_tier":null,"cache_creation":{"ephemeral_1h_input_tokens":0,"ephemeral_5m_input_tokens":0},"inference_geo":null,"iterations":null,"speed":null,"fallback_credit":null},"content":[{"type":"text","text":"API Error"}],"context_management":null},"error":"server_error","isApiErrorMessage":true,"streamCutCause":"watchdog","userType":"external","entrypoint":"claude-desktop","cwd":"D:\\Demo\\ledger-api","sessionId":"11111111-1111-1111-1111-111111111111","version":"2.1.309","gitBranch":"main"}""";

    // 2.1.258 录制形状:令牌被服务端拒绝(401)。
    private const string AuthFailed =
        """{"parentUuid":"u2","isSidechain":false,"type":"assistant","uuid":"a5","timestamp":"2026-10-30T07:05:00.000Z","message":{"diagnostics":null,"id":"x5","container":null,"model":"<synthetic>","role":"assistant","stop_details":null,"stop_reason":"stop_sequence","stop_sequence":"","type":"message","usage":{"output_tokens_details":null,"input_tokens":0,"output_tokens":0,"cache_creation_input_tokens":0,"cache_read_input_tokens":0,"server_tool_use":{"web_search_requests":0,"web_fetch_requests":0},"service_tier":null,"cache_creation":{"ephemeral_1h_input_tokens":0,"ephemeral_5m_input_tokens":0},"inference_geo":null,"iterations":null,"speed":null},"content":[{"type":"text","text":"Please run /login"}],"context_management":null},"error":"authentication_failed","isApiErrorMessage":true,"apiErrorStatus":401,"userType":"external","entrypoint":"claude-desktop","cwd":"D:\\Demo\\ledger-api","sessionId":"11111111-1111-1111-1111-111111111111","version":"2.1.258","gitBranch":"main"}""";

    private const string SidechainAssistant =
        """{"parentUuid":"s0","isSidechain":true,"message":{"model":"claude-haiku-4-5","id":"msg_s","type":"message","role":"assistant","content":[{"type":"text","text":"子代理"}],"stop_reason":"end_turn"},"type":"assistant","uuid":"s1","timestamp":"2026-10-30T02:30:00.000Z"}""";

    private static string Session(params string[] lines)
    {
        string path = TestTemp.NewFile("claude-session", ".jsonl");
        File.WriteAllText(path, string.Join("\n", lines) + "\n");
        return path;
    }

    [Fact]
    public void 末条主链记录是限流合成消息时判为被限额打断()
    {
        ClaudeSessionEvidence? evidence = ClaudeSessionEvidence.TryRead(Session(User, Assistant, RateLimit));

        Assert.NotNull(evidence);
        Assert.True(evidence.InterruptedByLimit);
        Assert.Equal("five_hour", evidence.LimitType);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1793001600), evidence.LimitResetsAtUtc);
        Assert.Equal("a2", evidence.LastAssistantUuid);
        // 合成消息的 <synthetic> 不是模型;取它之前最近一条真实应答的模型。
        Assert.Equal("claude-opus-5-5", evidence.LastModel);
        Assert.Equal("bypassPermissions", evidence.PermissionMode);
    }

    [Fact]
    public void 限流之后有人续上过就不再算中断()
    {
        string resumed = Assistant.Replace("\"uuid\":\"a1\"", "\"uuid\":\"a9\"", StringComparison.Ordinal)
            .Replace("2026-10-30T01:00:05.000Z", "2026-10-30T07:00:00.000Z", StringComparison.Ordinal);

        ClaudeSessionEvidence? evidence = ClaudeSessionEvidence.TryRead(Session(User, Assistant, RateLimit, User, resumed));

        Assert.NotNull(evidence);
        Assert.False(evidence.InterruptedByLimit);
        Assert.Equal("a9", evidence.LastAssistantUuid);
        Assert.Null(evidence.LimitType);
    }

    [Fact]
    public void 限流之后续的那一下失败了_中断仍然成立()
    {
        // 原生自动继续撞上 529、或用户输入后令牌被拒:都没有真实应答，不能判「已在别处续上」。
        ClaudeSessionEvidence? evidence = ClaudeSessionEvidence.TryRead(Session(User, Assistant, RateLimit, User, ServerError, AuthFailed));

        Assert.NotNull(evidence);
        Assert.True(evidence.InterruptedByLimit);
        Assert.Equal("a2", evidence.LastAssistantUuid);
        Assert.Equal("five_hour", evidence.LimitType);
        Assert.Equal("claude-opus-5-5", evidence.LastModel);
    }

    [Fact]
    public void 真实应答之后的合成错误不改变结论()
    {
        ClaudeSessionEvidence? evidence = ClaudeSessionEvidence.TryRead(Session(User, Assistant, User, ServerError));

        Assert.NotNull(evidence);
        Assert.False(evidence.InterruptedByLimit);
        Assert.Equal("a1", evidence.LastAssistantUuid);
    }

    [Fact]
    public void 子代理记录不代表主会话状态()
    {
        ClaudeSessionEvidence? evidence = ClaudeSessionEvidence.TryRead(Session(User, Assistant, RateLimit, SidechainAssistant));

        Assert.NotNull(evidence);
        Assert.True(evidence.InterruptedByLimit);
        Assert.Equal("claude-opus-5-5", evidence.LastModel);
    }

    [Fact]
    public void 早期版本没有quotaLimits也认得限流中断()
    {
        ClaudeSessionEvidence? evidence = ClaudeSessionEvidence.TryRead(Session(User, Assistant, LegacyRateLimit));

        Assert.NotNull(evidence);
        Assert.True(evidence.InterruptedByLimit);
        Assert.Null(evidence.LimitType);
        Assert.Null(evidence.LimitResetsAtUtc);
    }

    [Fact]
    public void 正在追加的半截末行被跳过()
    {
        string path = Session(User, Assistant, RateLimit);
        File.AppendAllText(path, "{\"parentUuid\":\"a2\",\"isSidechain\":false,\"type\":\"assistant\",\"message\":{\"model\":\"claude-op");

        ClaudeSessionEvidence? evidence = ClaudeSessionEvidence.TryRead(path);

        Assert.NotNull(evidence);
        Assert.True(evidence.InterruptedByLimit);
    }

    [Fact]
    public void 证据跨过一兆尾窗时扩大窗口继续找()
    {
        // 真实会话里常有几百 KB 的工具输出行;权限模式所在的 user 记录可能离文件尾很远。
        string filler = "{\"type\":\"attachment\",\"isSidechain\":false,\"attachment\":{\"content\":\"" + new string('x', 300_000) + "\"}}";
        string path = Session(User, Assistant, filler, filler, filler, filler, RateLimit);

        ClaudeSessionEvidence? evidence = ClaudeSessionEvidence.TryRead(path);

        Assert.NotNull(evidence);
        Assert.True(evidence.InterruptedByLimit);
        Assert.Equal("claude-opus-5-5", evidence.LastModel);
        Assert.Equal("bypassPermissions", evidence.PermissionMode);
    }

    [Fact]
    public void 文件不存在或没有主链应答时返回null()
    {
        Assert.Null(ClaudeSessionEvidence.TryRead(TestTemp.NewPath("missing-session")));
        Assert.Null(ClaudeSessionEvidence.TryRead(Session(User)));
        Assert.Null(ClaudeSessionEvidence.TryRead(Session(User, SidechainAssistant)));
        // 只有合成错误、没有任何真实应答或限流记录:说不清状态，交给上层按未核实处理。
        Assert.Null(ClaudeSessionEvidence.TryRead(Session(User, ServerError)));
    }
}
