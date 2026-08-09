# S7-I 规格:Claude 额度改走官方 oauth/usage 接口

> **历史初始设计,不再是现役契约。** 本文冻结了 2026-08-06 首版 OAuth 方案,尚未包含现代 `limits`、Claude Code User-Agent、Fable/scoped 窗口、账号指纹、稀疏合并、SQLite v5 与现役 UI 语义。当前实现与验证步骤以 [`../CLAUDE-QUOTA-ACQUISITION.md`](../CLAUDE-QUOTA-ACQUISITION.md) 为准;本文只保留设计演进证据。

## 1. 为什么(上游盘点结论)

现役实现起 `claude` 子进程等约 7 秒解析 `rate_limit_event`,且 `five_hour` 只偶发下发,
因此另建了本地 jsonl 块计算(`ClaudeUsageBlocks`,扫 2533 个文件约 600ms)。

参考 `github.com/Carstin520/token-remain`(macOS 菜单栏应用)后实测确认存在更优上游:

```
GET https://api.anthropic.com/api/oauth/usage
Authorization: Bearer <Claude Code 已有的 OAuth access token>
```

**本机实测(2026-08-06,Windows)**返回:

```
five_hour  : utilization=49  resets_at=2026-08-06T07:50:00
seven_day  : utilization=78  resets_at=2026-08-10T07:00:00
seven_day_opus / seven_day_sonnet / seven_day_cowork / seven_day_oauth_apps : 存在但本账户为 null
extra_usage: {is_enabled:false, ...}
spend      : {used:{amount_minor,currency,exponent}, percent, severity, ...}
```

两个窗口都有、瞬时、权威。凭据在 Windows 上位于
`%USERPROFILE%\.claude\.credentials.json`,实测形状:

```json
{ "claudeAiOauth": { "accessToken": "...", "refreshToken": "...",
                     "expiresAt": 1786050019964,
                     "scopes": ["user:inference", "..."],
                     "subscriptionType": "max", "rateLimitTier": "..." },
  "organizationUuid": "...", "mcpOAuth": { ... } }
```

`expiresAt` 是 **Unix 毫秒**。

## 2. 红线(照抄参考实现的约束,它们有明确理由)

1. **只读 token,绝不刷新、绝不写回凭据文件。** 续期由 Claude Code 自己完成;
   我们去刷新会与它争用 refresh token,并可能触发续期限流。
2. **token 不进日志、不进异常消息、不进任何返回值。** 出错只报状态码与分类。
3. token 剩余寿命低于 **60 秒**视同过期 → 不发请求,直接降级。
4. 凭据文件读不到 / JSON 损坏 / 无 `claudeAiOauth.accessToken` → 降级,不抛。
5. 降级目标是现有的 `ClaudeCodeProbe`(PTY/子进程探测),**不删除它**。

## 3. 产出

新增 `csharp/src/AiResume.Worker/Quota/ClaudeOAuthUsageProbe.cs`,namespace `AiResume.Worker.Quota`。

```csharp
/// <summary>oauth/usage 的取数结果。Failed 时 Snapshot 为 null。</summary>
public sealed record OAuthUsageResult(bool Ok, UsageSnapshot? Snapshot, string? FailureReason);

public sealed class ClaudeOAuthUsageProbe
{
    public ClaudeOAuthUsageProbe(HttpClient? httpClient = null, string? credentialsPath = null);
    public Task<OAuthUsageResult> TryFetchAsync(CancellationToken cancellationToken);
}
```

- `credentialsPath` 默认 `Path.Combine(UserProfile, ".claude", ".credentials.json")`;测试注入临时文件。
- `httpClient` 默认自建(超时 15 秒);测试注入带假 handler 的实例。
- **绝不 dispose 注入进来的 HttpClient**(调用方拥有它)。

### 3.1 映射到既有类型(不要新建形状)

附件 `UsageSnapshot.cs` 已定义 `UsageSnapshot` / `UsageBucket` / `UsageWindow`,直接复用:

- `UsageWindow.UsedPercent` 是 `int?`,**null 表示"未报告",绝不当 0**;
  API 的 `utilization` 是 0–100 的数(可能是小数,四舍五入取整)。
- `UsageWindow.ResetAtUnix` 是 `long?`(秒)。API 的 `resets_at` 是 **ISO 8601 字符串**
  (也可能是 epoch 数字,两种都要处理)。
- `WindowSeconds` 用 `UsageWindow.FiveHourSeconds` / `SevenDaySeconds` 常量。
- `Status`:该窗口 `utilization >= 100` 记 `"blocked"`,否则 `"allowed"`。
- `UsageBucket.LimitReached`:任一窗口 `utilization >= 100` 即为 true。
- `UsageSnapshot.Provider` 用 `"claudecode"`(与 `UsageSnapshotMapper` 保持一致)。
- `HasData` = 至少解析出一个窗口。

只映射 `five_hour` 与 `seven_day` 两个主窗口。`seven_day_opus` 等按 scope 细分的窗口
本轮**不做**(本账户实测为 null,没有可验证的真实形状,不臆测)。

### 3.2 失败分类(对齐 `docs/RUN-CONTRACT.md`)

- HTTP 401/403 → `FailureReason = "token_rejected_<status>"`;
- HTTP 408/504 → `"gateway_timeout"`(这是**唯一**算 provider 超时的情形);
- 其它非 2xx → `"http_<status>"`;
- `HttpRequestException` / `SocketException` / `TaskCanceledException`(即 DNS/TCP/TLS/超时)
  → `"failed_local"`;
- JSON 解析失败 → `"malformed_response"`;
- 凭据不可用 → `"no_credentials"` 或 `"token_expired"`。

**任何 FailureReason 都不得包含 token、URL 查询串或响应正文。**

## 4. 测试

`csharp/test/AiResume.Tests/ClaudeOAuthUsageProbeTests.cs`。用假 `HttpMessageHandler` 注入响应,
**绝不发真实网络请求、绝不读真实 `%USERPROFILE%\.claude`**(凭据文件写到临时目录)。

必须覆盖:
1. 正常响应 → 两个窗口都解析出来,`UsedPercent` 分别是 49 / 78,`ResetAtUnix` 正确;
2. `utilization` 为 100 → 该窗口 `Status == "blocked"` 且 `LimitReached == true`;
3. `utilization` 缺失/为 null → 该窗口 `UsedPercent` 为 **null 而不是 0**;
4. `resets_at` 为 epoch 数字时也能解析;
5. 凭据文件不存在 → `Ok == false`、`FailureReason == "no_credentials"`,**不抛异常**;
6. `expiresAt` 已过期(或剩余 < 60 秒)→ `"token_expired"` 且**没有发出任何 HTTP 请求**
   (用假 handler 的调用计数断言);
7. HTTP 401 → `"token_rejected_401"`;
8. HTTP 504 → `"gateway_timeout"`;
9. 网络异常(handler 抛 `HttpRequestException`)→ `"failed_local"`;
10. 响应不是 JSON → `"malformed_response"`;
11. **token 不外泄**:把 accessToken 设成一个独特字符串,断言
    `FailureReason`(各失败分支)与序列化后的 `UsageSnapshot` 全文都**不包含**它。

隐含前置:
- 假 handler 用 `HttpMessageHandler` 子类重写 `SendAsync`,`new HttpClient(handler)` 注入;
- `UsageSnapshot` 等类型定义见附件,**不要自己另造**;
- 凭据 JSON 由测试自己拼字符串,Windows 路径要转义。
