# S7-C 限额取数规格(额度潮汐轴数据源)

> 冻结于 2026-08-06。实现方按本文写代码;本文与代码冲突以本文为准,发现规格错误先改规格。

## 1. 为什么自研(记录在案的 ADR-0003 §2.2 偏离)

ADR-0003 §2.2 原文要求"限额数据消费 cc-connect 的 `UsageReport`,不再自行探测"。该前提在本产品目标平台上**不成立**,证据三条:

1. cc-connect `agent/claudecode/claude_usage.go` 的 `GetUsage` 并非读 API,而是 **PTY 起 `claude` TUI → 发 `/usage` → ANSI 抓屏 → 正则解析**;
2. 它用 `github.com/creack/pty v1.1.24`。该库 `run.go` 无构建约束(Windows 上照常编译),但其调用的 `open()` 来自 `pty_unsupported.go`,构建约束为 `//go:build !linux && !darwin && !freebsd && !dragonfly && !netbsd && !openbsd && !solaris && !zos` —— **命中 Windows**,函数体是 `return nil, nil, ErrUnsupported`。实测 Windows 二进制中 creack/pty 只嵌入了 `run.go` 一个文件,与该推断一致;
3. cc-connect 管理 API 路由全表(`core/management.go:218-252`)为 status / restart / reload / config / settings / agents / projects / cron / setup / providers / skills / bridge-adapters,**无任何 usage 端点**;`/usage` 只是聊天命令,必须经会话。

结论:该通道在 Windows 上不可用,而"限额后自动续跑"是 ADR-0003 §2.2 列明的**本产品唯一不可替代的核心**,不能建在不通的通道上。故自取数据,但**保持 `UsageReport` 兼容形状**,将来上游可用时可无痛切换实现。

## 2. 数据源(已实测,2026-08-06 本机)

命令:

```
claude -p ready --model haiku --max-turns 1 --output-format stream-json --verbose
```

输出为 NDJSON(每行一个 JSON 对象)。实测消息类型:`system`(多个子类型)、`assistant`、`rate_limit_event`、`result`。

限额数据在 `rate_limit_event`,实测原样:

```json
{"type":"rate_limit_event","rate_limit_info":{"status":"allowed","resetsAt":1786027800,"rateLimitType":"five_hour","overageStatus":"rejected","overageDisabledReason":"org_level_disabled","isUsingOverage":false},"uuid":"...","session_id":"..."}
```

**字段可得性不对称(关键)**:

| 字段 | 可得性 | 说明 |
|---|---|---|
| `status` | 常态 | `allowed` / `blocked` / `rejected` / `limited` / `exceeded` |
| `resetsAt` | 常态 | unix 秒,窗口精确重置时刻 |
| `rateLimitType` | 常态 | `five_hour` / `seven_day` |
| `utilization` | **仅高用量时** | 0..1 小数;实测低用量时**整个字段缺席**(本次探测就没有) |
| `isUsingOverage` / `overageStatus` | 常态 | 本规格不消费 |

> 现役 `src/lib.ps1:513` 记载 `resetsAt` 只在窗口越过 ~0.75 时下发;**本次实测在 `status=allowed` 时已下发 `resetsAt`**,故该说法对 `resetsAt` 不成立,对 `utilization` 成立。规格以本次实测为准。

**红线**:`utilization` 缺席意味着"未报告",**不得当成 0% 渲染**,也不得本地估算百分比。

## 3. 要实现的类型

契约类型已存在于 `csharp/src/AiResume.Worker/Quota/UsageSnapshot.cs`(勿修改):
`UsageSnapshot` / `UsageBucket` / `UsageWindow` / `ClaudeProbeFailure`。

### 3.1 `ClaudeStreamJsonUsageParser`(任务 A)

文件:`csharp/src/AiResume.Worker/Quota/ClaudeStreamJsonUsageParser.cs`
命名空间:`AiResume.Worker.Quota`

```csharp
public static class ClaudeStreamJsonUsageParser
{
    public static UsageSnapshot Parse(string streamJsonText, DateTimeOffset now);
}
```

行为:

1. 输入是 `claude` 的 **stdout+stderr 合并文本**,因此**必然含非 JSON 行**(stderr、空行、告警)。按行切分(`\r\n` / `\n` / `\r` 都要处理),逐行尝试解析;**任何一行解析失败都必须静默跳过,绝不抛异常**。
2. 只取 `type == "rate_limit_event"` 且含对象属性 `rate_limit_info` 的行。
3. 从 `rate_limit_info` 读:`status`(字符串)、`resetsAt`(数字,unix 秒)、`rateLimitType`(字符串)、`utilization`(数字,可缺席)。缺 `rateLimitType` 的事件**丢弃**(无法归窗)。
4. **同一 `rateLimitType` 出现多次时,后出现的覆盖先出现的**(后发事件更新)。
5. 映射到 `UsageWindow`:
   - `Name` = `rateLimitType` 原值;
   - `Status` = `status` 原值(缺失时用空串 `""`);
   - `WindowSeconds`:`five_hour` → `UsageWindow.FiveHourSeconds`,`seven_day` → `UsageWindow.SevenDaySeconds`,其它 → `0`;
   - `ResetAtUnix` = `resetsAt`,缺席则 `null`;
   - `ResetAfterSeconds` = `resetsAt - now.ToUnixTimeSeconds()`,**下限截断为 0**(已过期显示 0 而非负数);`resetsAt` 缺席则 `null`。因为 `int` 装不下 7 天以上的极端值也不会溢出(604800 远小于 int.MaxValue),直接转 `int` 即可,但须先在 `long` 上做减法再截断;
   - `UsedPercent` = `utilization` 存在时 `(int)Math.Round(utilization * 100)` 并**钳制到 0..100**;缺席则 `null`。
6. 窗口排序:`five_hour` 在前,`seven_day` 次之,其余按首次出现顺序排在后面。
7. `LimitReached` = 任一窗口的 `Status` 属于 `blocked` / `rejected` / `limited` / `exceeded`(**大小写不敏感**)。`Allowed` = `!LimitReached`。
8. Bucket:恰一个,`Name = "Usage"`(与 cc-connect 一致)。
9. 一条 `rate_limit_event` 都没有时,返回 `UsageSnapshot.Unavailable("claudecode", now, "未收到 rate_limit_event(本次调用未下发限额信息)")`。
10. 成功时 `Provider = "claudecode"`,`CapturedAt = now`,`UnavailableReason = null`。
11. 输入为 `null` 或全空白时,同第 9 条走 Unavailable 分支(理由文本相同)。

### 3.2 `ClaudeProbeFailureClassifier`(任务 B)

文件:`csharp/src/AiResume.Worker/Quota/ClaudeProbeFailureClassifier.cs`
命名空间:`AiResume.Worker.Quota`

```csharp
public static class ClaudeProbeFailureClassifier
{
    public static ClaudeProbeFailure Classify(string? text);
}
```

移植自现役 `src/lib.ps1` 的 `Get-ClaudeProbeFailureReason`,**判定顺序必须保持一致**(先命中先返回),大小写不敏感:

| 顺序 | 匹配(正则,作用于小写文本) | 返回 |
|---|---|---|
| 1 | `usage limit\|rate.?limit\|limit reached\|5-hour limit\|weekly limit\|too many requests\|resets at\|quota exceeded\|429` | `Limited` |
| 2 | `not logged in\|please run /login\|login required\|unauthori[sz]ed\|authentication\|invalid api key\|invalid.*auth\|api key.*missing\|\b401\b\|\b403\b` | `Auth` |
| 3 | `subscription.*(expired\|required\|inactive)\|billing\|payment required\|insufficient (credit\|balance)\|credit balance\|plan expired` | `Billing` |
| 4 | `model.*(not found\|unavailable\|unsupported)\|unknown model\|模型.*不可用` | `ModelUnavailable` |
| 5 | `timed? ?out\|timeout\|econn\|socket\|tls\|dns\|network\|connection (reset\|refused\|failed)\|\b502\b\|\b503\b\|\b504\b\|server overloaded\|temporar` | `Transient` |
| 6 | `enoent\|not recognized\|command not found\|系统找不到指定的文件\|启动.*失败` | `NotInstalled` |
| 7 | 以上都不命中 | `Unknown` |

`null` / 空串 / 纯空白 → `Unknown`。使用 `RegexOptions.Compiled | RegexOptions.IgnoreCase`,正则为静态只读字段(每次调用重新构造正则是性能缺陷)。

**注意顺序的语义含义**:顺序 1 必须早于顺序 5,否则 "rate limit ... timed out" 会被误判成网络问题从而触发重试,而实际应当进入续跑排队。

## 4. 测试要求

xUnit,`net10.0-windows`,命名空间 `AiResume.Tests`,文件放 `csharp/test/AiResume.Tests/`。

### 4.1 `ClaudeStreamJsonUsageParserTests`(任务 C)

必须覆盖:

1. **实测样本**:用 §2 的真实 `rate_limit_event` 行,断言 `five_hour` 窗口 `ResetAtUnix == 1786027800`、`Status == "allowed"`、`WindowSeconds == 18000`、**`UsedPercent == null`**、`LimitReached == false`。
2. **utilization 存在**:`"utilization":0.87` → `UsedPercent == 87`。
3. **utilization 边界钳制**:`1.5` → `100`;`-0.2` → `0`。
4. **混入非 JSON 行**:前后各插入 `"warning: something"` 与空行,断言仍能解析且不抛异常。
5. **后发覆盖先发**:同一 `five_hour` 两条事件,`resetsAt` 不同,断言取后者。
6. **blocked → LimitReached**:`"status":"blocked"` → `LimitReached == true`、`Allowed == false`;并测 `"BLOCKED"` 大写同样命中。
7. **窗口排序**:输入顺序 `seven_day` 在前、`five_hour` 在后,断言输出 `Windows[0].Name == "five_hour"`。
8. **ResetAfterSeconds 截断**:`resetsAt` 早于 `now` → `ResetAfterSeconds == 0`(非负数)。
9. **无 rate_limit_event**:只有 `system`/`result` 行 → `HasData == false` 且 `UnavailableReason != null`。
10. **`null` 与空白输入** → 走 Unavailable 分支,不抛异常。
11. **缺 `rateLimitType` 的事件被丢弃**:一条缺该字段、一条完整 → 只剩一个窗口。
12. **`DerivedWindowStart`**:`five_hour` 且 `ResetAtUnix == 1786027800` → 等于 `FromUnixTimeSeconds(1786027800 - 18000)`。

**隐含约束(必读,否则断言必然写错)**:
- `Parse` 的 `now` 是**显式入参**,测试必须传固定值,**禁止用 `DateTimeOffset.UtcNow`** —— 两次取值相差毫秒会让 `ResetAfterSeconds` 断言随机失败;
- `UsedPercent` 是 `int?`,断言"未报告"要写 `Assert.Null(...)`,写 `Assert.Equal(0, ...)` 是错的;
- `UsageSnapshot.HasData` 要求 Buckets 非空**且**至少一个 bucket 的 Windows 非空。

### 4.2 `ClaudeProbeFailureClassifierTests`(任务 D)

必须覆盖:

1. 七个分支各至少一个用例,用**贴近真实输出的整句**而非裸关键词(例如 `"Claude usage limit reached. Your limit will reset at 3pm."` → `Limited`);
2. **顺序红线用例**:`"rate limit exceeded; the request also timed out"` → 必须是 `Limited` 而非 `Transient`;
3. 大小写不敏感:`"NOT LOGGED IN"` → `Auth`;
4. 中文用例:`"系统找不到指定的文件"` → `NotInstalled`;`"模型 gpt-x 不可用"` → `ModelUnavailable`;
5. `null` / `""` / `"   "` → `Unknown`;
6. 不相关文本(如 `"ready"`)→ `Unknown`。

## 5. 红线

- 不修改 `UsageSnapshot.cs`;不新增 NuGet 依赖(只用 BCL:`System.Text.Json`、`System.Text.RegularExpressions`)。
- 解析器**不得抛异常**,不得访问文件系统、网络、环境变量、进程。
- 不得在代码或测试中写入任何真实凭据、token、app id/secret。
- 测试不得触碰真实用户目录(`~/.claude` 等),不得启动 `claude` 进程 —— 全部用字符串 fixture。
- 注释用中文,风格对齐仓库现有 C# 文件(`///` 摘要 + 关键处解释"为什么")。
