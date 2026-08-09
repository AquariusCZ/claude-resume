# Claude 额度获取协议与验证手册

> 现役详细真身。最后一次代码、真机响应、安装产物与界面交叉验证:2026-08-09。
> 仓库协作与安全规则仍以 [`CLAUDE.md`](../CLAUDE.md) 为准;本文件定义 Claude 额度链路的现役行为、故障边界与回归方法。
> [`design/s7i-oauth-usage-spec.md`](design/s7i-oauth-usage-spec.md) 与 [`design/s7c-quota-probe-spec.md`](design/s7c-quota-probe-spec.md) 是初始设计记录,不得再当现役契约。

## 1. 第一性目标

额度功能必须回答三个不同的问题:

1. 服务端当前报告了哪些额度窗口、已用百分比和重置时间?
2. Claude Code 此刻是否因任一总额度或模型额度而不能继续运行?
3. 服务端本次响应稀疏、网络失败或多个 AI Resume 进程并发刷新时,上一次仍有效的服务端证据应如何连续展示?

由此得到不可破坏的约束:

- **只展示外部可验证事实。** 百分比来自 Anthropic 用量响应或 Claude Code 的结构化 `rate_limit_event`;不得用本地 token 数估算账户额度。
- **未知不是 0,失败不是空闲。** 缺百分比、缺窗口或探测失败不得渲染成绿色正常。
- **缺失不是删除。** OAuth 与 CLI 都是稀疏观测;一次没返回 Fable 或百分比,不等于服务端明确撤销了它。
- **连续性必须有边界。** 只能在同账号、同稳定窗口身份、同一尚未到期的 reset 周期内承接旧值；主窗口身份是协议 kind，scoped 身份是规范化完整 scope 的 SHA-256。
- **任一限额打满都算限流。** 总窗口未满但 `weekly_scoped` 已满时,任务仍可能被拒绝。
- **凭据只读且不外泄。** AI Resume 不刷新、不写回 Claude Code token,不把 token、响应正文或凭据路径写进日志与 UI。
- **首帧不依赖 I/O。** 凭据读取、Claude 版本探测、SQLite 迁移和网络请求都发生在后台额度请求路径,不得进入 WPF 构造函数。

## 2. 上游盘点与自取依据

AI Resume 先查了已依赖上游,再决定维护自己的 Windows 取数通道:

- cc-connect v1.4.1 的 `UsageReporter.GetUsage` 通过 PTY 驱动 Claude Code TUI、发送 `/usage`、抓取 ANSI 屏幕并解析。
- 它依赖 `creack/pty`;该库的 Windows 构建命中 `pty_unsupported.go`,`open()` 返回 `ErrUnsupported`。
- cc-connect 管理 API 没有 usage 端点,聊天 `/usage` 也不是可供本地控制面稳定轮询的管理接口。
- 因此“上游有接口”在 Windows 上不等于“本产品可用”。AI Resume 自取成立,但 `UsageSnapshot` 保持与 cc-connect `UsageReport` 兼容的形状,以后上游补齐 Windows 能力时可以替换数据源而不重做 GUI 与续跑引擎。

生态复核还确认了三条共同实践:

- [llm-cost-bar](https://github.com/kpnemo/llm-cost-bar/blob/378786c55ae4830de4c864592d679a3b44eaedad/Core/Sources/LLMCostBarCore/SubscriptionProvider.swift) 使用 OAuth beta 头和 `claude-code/...` User-Agent,并采用“陈旧证据优于空白”。
- [CodexBar](https://github.com/steipete/CodexBar/blob/171c2dce44d1e48cb1e9fab57c24df2a773fba2b/Sources/CodexBarCore/Providers/Claude/ClaudeOAuth/ClaudeOAuthUsageFetcher.swift) 动态构造 Claude Code User-Agent,按账号保存最近成功快照并展示 Fable 等 scoped 窗口。
- [ClaudeTimer](https://github.com/TimeWinder-dk/ClaudeTimer/blob/9b09a72d86b7f51241de7d61a2df71c59ef2294e/src/ClaudeTimer/Services/ClaudeUsageClient.cs) 优先解析现代 `limits` 数组,旧顶层字段只作兼容回退。

固定快照、平台证据和本机实测过程见 [`UPSTREAM-ARCHITECTURE-RESEARCH.md`](UPSTREAM-ARCHITECTURE-RESEARCH.md#2026-08-09-claude-额度读取生态复核)。

## 3. 端到端数据流

```text
GUI / ResumeEngine
       |
       v
QuotaService.GetAsync(forceRefresh)
       |
       +-- 5 分钟成功缓存 / 30 秒失败缓存
       +-- single-flight,同进程并发只执行一轮探测
       |
       v
ClaudeOAuthUsageProbe
       |
       +-- 有可用 OAuth 窗口
       |      |
       |      v
       |  SQLite IMMEDIATE 事务内:读取 -> 稀疏合并 -> 写回
       |      |
       |      v
       |  UsageSnapshot
       |
       +-- OAuth 不可用或无窗口
              |
              v
        ClaudeCodeProbe 读取 rate_limit_event
              |
              v
        与同账号最近权威快照稀疏合并
              |
              v
        UsageSnapshot / Unavailable
       |
       v
ControlPlaneBridge 转成 JSON RPC
       |
       v
GUI 进度条、reset、Fable 行和 provider 状态
```

`QuotaService` 是 GUI 与续跑引擎共用的唯一额度入口。不得在 GUI、Worker 或飞书侧再复制一套解析、缓存或合并逻辑。

## 4. OAuth 请求协议

### 4.1 端点与凭据

```http
GET https://api.anthropic.com/api/oauth/usage
Authorization: Bearer <Claude Code access token>
Accept: application/json
anthropic-beta: oauth-2025-04-20
anthropic-version: 2023-06-01
User-Agent: claude-code/<本机版本>
```

- Windows 默认凭据文件:`%USERPROFILE%\.claude\.credentials.json`。
- 只读取 `claudeAiOauth.accessToken`、`claudeAiOauth.expiresAt` 与顶层 `organizationUuid`。
- token 剩余寿命小于 60 秒时返回 `token_expired`,不发网络请求。
- 绝不读取后刷新、写回或替换 refresh token。Claude Code 是凭据生命周期的唯一 writer。
- 产品不读取 v2rayN 订阅、不修改系统代理或 Windows 网络设置;请求使用 .NET/Windows 当前网络栈。开发阶段查公开上游时按全局规则先直连,网络链路失败后才试 `127.0.0.1:10808` / `10809`。

### 4.2 User-Agent 解析

`ClaudeOAuthUsageProbe` 按以下顺序取得本机 Claude Code 版本:

1. `%USERPROFILE%\.local\bin\claude.exe`。
2. `PATH` 中的 `claude.exe` / `claude.cmd` 等候选。
3. Windows `FileVersionInfo.ProductVersion`。
4. 无法识别时退回兼容基线 `claude-code/2.0.0`。

版本解析发生在后台请求路径。2026-08-09 目标机实际版本为 `2.1.185 (Claude Code)`,请求使用 `claude-code/2.1.185`。

### 4.3 账号指纹

持久化键不能直接使用 token,也不能让 token 轮换把同一账号误判为新账号:

```text
优先:SHA-256("organization:" + Trim(organizationUuid))
回退:SHA-256("token:" + accessToken)
```

结果是 64 位十六进制不可逆指纹。数据库和返回值只保存指纹,不保存 identity material。账号指纹不同,历史窗口绝不互相承接。

## 5. 响应解析契约

### 5.1 现代与旧字段的优先级

现代响应的 `limits` 是主协议,旧顶层 `five_hour` / `seven_day` 只作逐字段兼容:

| 现代 `kind` | 内部窗口名 | 窗口长度 | 旧字段回退 |
| --- | --- | ---: | --- |
| `session` | `five_hour` | 5 小时 | `five_hour` |
| `weekly_all` | `seven_day` | 7 天 | `seven_day` |
| `weekly_scoped` | `weekly_scoped:<display_name>` | 7 天 | 无 |

同一个主窗口按字段分别取值:

```text
UsedPercent = modern.percent ?? legacy.utilization
ResetAtUnix = modern.resets_at ?? legacy.resets_at
```

不能因为 legacy 对象存在就整体盖掉 modern 对象。现代 percent 和 reset 可以分别出现;两边都缺的字段才是 `null`。

### 5.2 字段语义

- `UsedPercent:int?`:四舍五入后钳制到 `0..100`;`null` 表示服务端未报告,不是 0。
- `ResetAtUnix:long?`:支持 ISO 8601 字符串和 epoch 秒。
- `ResetAfterSeconds`:由 `ResetAtUnix - now` 计算,最小为 0。
- `DerivedWindowStart`:由 `ResetAtUnix - WindowSeconds` 推导,不是服务端原始字段,UI 必须标“推导”。
- `Status`:窗口自身百分比达到 100 时为 `blocked`;百分比低于 100 或本次探测整体成功时可为 `allowed`;只有 reset 的失败/全局限流降级观测保持未知,不自行判满或判可用。
- `LimitReached`:任一主窗口或 `limits` 条目的 percent 达到 100 即为 true。
- `severity=critical` 不是限流判据。实测未到 100% 也可能为 critical,它只能表示接近上限。

### 5.3 Fable 与其它 scoped 限额

`weekly_scoped.scope.model.display_name` 是用户需要看到的具体模型名。示例:

```json
{
  "kind": "weekly_scoped",
  "percent": 100,
  "resets_at": "2026-08-10T14:00:00Z",
  "scope": { "model": { "display_name": "Fable" } }
}
```

映射为 `weekly_scoped:Fable`。同一次响应里的每条逻辑 `weekly_scoped` 都要保留并分别显示,不能只取第一条;否则后一条模型已满时会被侧栏隐藏。内部身份不是显示名或数组序号,而是完整 scope JSON 经过属性名排序后的 SHA-256;同名 scope 即使响应顺序互换也不会串窗。完全相同的重复 scope 合并为一条:同 reset 取更高百分比,不同 reset 取更新代次。即使 percent 缺失,只要 scoped 条目有 reset,也保留窗口名并显示“未报告”;不得把它删除或显示成 `0%`。模型名中的冒号会被移除,避免破坏内部窗口名分隔。

## 6. 失败分类与 CLI 降级

OAuth 探测不把 token、响应正文或异常正文回传。失败分类如下:

| 场景 | `FailureReason` | 后续动作 |
| --- | --- | --- |
| 凭据不存在、不可读、JSON 损坏或无 access token | `no_credentials` | 降级 CLI |
| token 剩余寿命小于 60 秒 | `token_expired` | 降级 CLI,不发 OAuth 请求 |
| HTTP 401 / 403 | `token_rejected_401/403` | 降级 CLI |
| HTTP 408 / 504 | `gateway_timeout` | 降级 CLI |
| HTTP 429 或其它非 2xx | `http_<status>` | 降级 CLI |
| DNS/TCP/TLS/reset/HttpClient 自身超时 | `failed_local` | 降级 CLI |
| 2xx 但 JSON 不可解析 | `malformed_response` | 降级 CLI |
| 调用方明确取消 | 抛出取消 | 终止本轮,不得伪装成失败后继续 |

CLI 降级由 [`ClaudeCodeProbe`](../csharp/src/AiResume.Worker/Probes/ClaudeCodeProbe.cs) 执行:

```text
claude -p ready --model haiku --max-turns 1 --output-format stream-json --verbose
```

- 工作目录固定为 AI Resume 状态根,不污染真实项目发现。
- 子进程带 `AI_RESUME_INTERNAL_RUN=1`,不得产生完成通知。
- 只解析结构化 `rate_limit_event`;输出写临时文件,解析后删除,不落正式日志。
- CLI 常只给 reset、状态或部分窗口,尤其可能没有百分比与 `weekly_scoped`;因此它是降级观测,不是删除 OAuth 历史证据的理由。
- CLI 探测若整体失败,即使解析到部分窗口也只能作为失败详情与连续性证据;`Allowed=false`,GUI 不得显示绿色正常。
- OAuth 与 CLI 都没有可用窗口时返回 `Unavailable`,GUI 显示真实原因,不得回退成“空闲”。

## 7. 稀疏观测状态机

### 7.1 合并身份

一条历史值可被承接,必须同时满足:

```text
同一个 credential fingerprint
+ 主窗口同一个 window name / scoped 同一个 canonical scope Identity
+ 同一个 resetAtUnix 代次
+ 历史 resetAtUnix > 当前时间
```

这里的 `resetAtUnix` 是窗口代次。主窗口名相同但 reset 改变,就是新周期;旧百分比不能跨周期泄漏。scoped 的显示名不构成身份,规范化 scope 哈希才构成身份。schema v5 早期无 Identity 的 scoped 行只在本次完全没有稳定 scoped 观测时作为历史证据承接;一旦出现同名稳定 scoped,旧行因无法证明归属而丢弃,不能按名字猜给新 scope。

### 7.2 合并规则

1. 当前明确字段通常优先;同一 reset 代次的已用百分比必须单调不回退,较晚提交的 99% 不得覆盖已落库的 100%;reset 代次冲突时按原始 `CapturedAt` 判新旧,旧观测不得倒写新代次。
2. 当前窗口存在但 percent 缺失,且 reset 与旧值相同,可承接旧 percent。
3. 当前窗口完全缺失,旧窗口仍未 reset 时可整体承接。
4. 当前 reset 缺失时可承接旧 reset;但较旧观测的 percent-only 窗口无法证明属于较新的 reset 代次,必须完整保留 prior,不得吸收其 percent/status。当前给了不同 reset 时不得承接旧 percent。
5. 承接窗口重新计算 `ResetAfterSeconds`,并设置 `CarriedForward=true`。
6. 承接的 blocked/100% 仍参与 `LimitReached`,避免错误启动任务。
7. 只有历史值、没有本次实时窗口时,`Allowed` 必须为 false;历史证据不能冒充实时成功。
8. `CapturedAt` 单调不回退,避免旧进程覆盖新进程时间线。
9. 历史快照结构损坏时放弃历史,保留本次观测。

### 7.3 失效条件

以下任一条件立即停止承接:

- 账号指纹变化。
- 同名窗口返回新的 `resetAtUnix`。
- 历史 `resetAtUnix` 已到或已过。
- 历史快照结构无效。

服务端“没返回某条”不是失效条件;只有可证明的账号、代次或时间边界才是。

## 8. 存储、并发与缓存

### 8.1 SQLite schema v5

权威快照位于 `%LOCALAPPDATA%\AI Resume\state\runs.db` 的 `quota_snapshots`:

| 列 | 含义 |
| --- | --- |
| `provider` | 当前为 `claudecode` |
| `credential_fingerprint` | 64 位不可逆账号指纹 |
| `captured_at` | 快照观察时间 |
| `snapshot_json` | 只含窗口、百分比、状态与 reset 的 `UsageSnapshot` |
| `updated_at` | 数据库更新时间 |

主键是 `(provider, credential_fingerprint)`。v4 旧表只有 provider、JSON、更新时间,无法证明账号归属;迁移到 v5 时必须丢弃旧额度行,不能把它猜给当前账号。

### 8.2 原子更新

多开 GUI、GUI 与 Worker 同时刷新时,锁外 `Load -> Merge -> Save` 会发生丢失更新。现役边界是:

```text
BEGIN IMMEDIATE
  SELECT 当前账号快照
  MergeSparseObservation(本次观测, 当前快照)
  INSERT ... ON CONFLICT DO UPDATE
COMMIT
```

读取、合并、写回必须在同一个 SQLite `IMMEDIATE` 事务内。这样后到事务必然看到先提交事务的窗口,不会用基于旧基线生成的 carried 值覆盖新实值。

SQLite 在后台额度请求时才迁移/打开,不阻塞 WPF 首帧。存储失败不应阻断实时额度显示;UI 继续显示本次快照,并附带脱敏的 `StorageWarning`。

### 8.3 进程内缓存

- 有窗口的成功快照缓存 5 分钟。
- 无窗口、失败或只有部分失败证据的快照只缓存 30 秒;只有成功且无 `UnavailableReason` 的有数据快照缓存 5 分钟。
- 手动或定时刷新传 `force=true`,绕过额度缓存。
- 同进程并发请求通过 single-flight 合并,避免同时启动多个 Claude 探测。
- 降级前重新读取 SQLite,让另一个 GUI/Worker 进程刚提交的快照立即可见。

## 9. GUI 呈现契约

额度区有两个不同来源,不能混为一谈:

- 左侧 5 小时屏先从本地 `~/.claude/projects/**/*.jsonl` 计算当前会话块的开始、结束、token 和回复数。这是毫秒级本地统计,**不是账户已用百分比**。
- 服务端返回 `five_hour` 后覆盖左屏,此时才显示真实已用百分比。
- 右侧 7 天屏与 Fable 行来自服务端额度快照。

呈现规则:

| 数据状态 | 进度/刻度 | 文案与颜色 |
| --- | --- | --- |
| percent + reset | 光柱宽度=已用百分比;细刻度=窗口内当前时间 | 实时来源正常显示 |
| 只有 percent | 仍画真实光柱;不画时间刻度 | “重置时间未报告” |
| 只有 reset | 完整低亮分段轨道 + 移动的未知读数扫描;不把扫描位置当数值;系统启用减少动态效果时为静态低亮轨道;可显示重置倒计时/时间刻度 | “服务端未下发用量百分比” |
| 非限流 carried | 保留数值与 reset | 琥珀“最近服务端读数” |
| carried 且已满 | 仍按限流处理 | 红色“已限流/已用尽”,并注明最近快照 |
| `weekly_scoped:Fable` 缺 percent | 无假百分比 | Fable 行“未报告” |
| 无任何数据 | 清空光柱 | 灰色/错误“不可得”,绝不绿色 |

其它 UI 不变量:

- 两块 CRT 内屏等高;内容多少不得改变单边高度。
- 光柱表示额度已用百分比,不是时间流逝;时间位置使用独立细刻度。
- 已知百分比的轨道使用具名 `meter`;未知读数使用具名 `status`,不冒充任务进度条,不设置 `aria-valuenow`,也不对应 0% 或 100%。
- scoped 限额只影响对应模型行;不得把 5 小时 18% 的卡片因为 Fable 满额而整块涂红。
- CLI 降级只提供全局限流结论时,结论留在 bucket/provider 层;没有窗口自身的 100% 证据就不得给 5H/7D 单窗贴“已限流”。
- 已过期的同一 `resetAt` 代次最多按当前自动刷新周期强刷一次;上游持续返回旧 reset 时不得形成每秒探测循环,新 reset 代次可立即刷新。
- provider 绿色状态只来自本次探测成功、有数据、未限流且未承接的真实证据;失败探测中的部分窗口不得点绿。
- carried 主窗口使用静态琥珀光柱,不播放表示实时活动的绿色扫光;5H/7D 来源标签每个绘制分支都显式重置,不得泄漏上一轮来源。
- `hasData=false` 的成功 RPC 也不是健康额度。

## 10. 回归测试矩阵

| 层 | 必须覆盖的行为 | 现役测试 |
| --- | --- | --- |
| OAuth 请求 | 五个协议头、token 只读、到期短路、错误分类、取消传播 | `ClaudeOAuthUsageProbeTests` |
| 响应解析 | modern-only、modern 逐字段优先 legacy、空对象、多条/同名重排 scoped、缺 percent、ISO/epoch reset、100% 限流 | `ClaudeOAuthUsageProbeTests` |
| CLI 映射 | `rate_limit_event`、无窗口、部分失败不 Allowed、状态/错误分类、未报告不当 0 | `ClaudeProbeTests`,`UsageSnapshotMapperTests` |
| 稀疏合并 | 同 reset 承接与百分比单调、旧 reset 晚提交拒绝、换代/到期清除、纯历史不冒充 Allowed、scoped 稳定身份 | `QuotaServiceTests` |
| 账号隔离 | 同账号跨实例可见、不同指纹不承接 | `QuotaServiceTests` |
| SQLite | schema v5、真实 v4 升级、无账号身份旧行丢弃、旧代次晚提交拒绝、损坏容错、原子并发更新 | `QuotaSnapshotStoreTests` |
| scoped 安全 | Fable 等多个模型额度全部显示并影响总限流判定 | `ScopedLimitTests`,`ClaudeOAuthUsageProbeTests` |
| GUI 契约 | 缺数据/部分失败不绿、percent 无 reset 仍画、carried 语义、多 scoped、未知扫描、ARIA、等高布局 | `GuiQuotaContractTests`,`GuiMotionContractTests` + 真机截图 |

只改额度链路时的最小自动化验证:

```powershell
dotnet test csharp\test\AiResume.Tests\AiResume.Tests.csproj `
  --filter "FullyQualifiedName~ClaudeOAuthUsageProbeTests|FullyQualifiedName~ClaudeProbeTests|FullyQualifiedName~UsageSnapshotMapperTests|FullyQualifiedName~QuotaServiceTests|FullyQualifiedName~QuotaSnapshotStoreTests|FullyQualifiedName~ScopedLimitTests|FullyQualifiedName~GuiQuotaContractTests"
```

交付前全量门禁:

```powershell
dotnet build csharp\AiResume.sln -c Release -warnaserror
dotnet test csharp\AiResume.sln -c Release --no-build
```

测试必须使用假 `HttpMessageHandler`、临时凭据与临时 SQLite;不得把真实 token 写进 fixture,不得对真实项目启动 AI 修改运行。

## 11. 真机验证手册

### 11.1 前置检查

1. `claude --version` 能返回本机版本。
2. Claude Code 已登录,但不要读取或打印 `.credentials.json` 内容。
3. 先完成 Release build 与全量测试。
4. 安装 Release 产物,关闭旧 GUI 后再打开;已打开窗口不会热更新。

```powershell
csharp\src\AiResume.Worker\bin\Release\net10.0-windows\AiResume.Worker.exe install
```

### 11.2 脱敏界面冒烟

用安装目录的 GUI 生成不读取生产状态的合成数据截图(用于布局与公开文档):

```powershell
$shot = Join-Path $env:TEMP ("ai-resume-quota-" + [guid]::NewGuid().ToString("N") + ".png")
& "$env:LOCALAPPDATA\AI Resume\AiResume.Gui.exe" --screenshot $shot
$shot
```

人工核对:

1. 左右 CRT 内屏等高。
2. 服务端给 percent 时进度条存在,宽度与数字一致。
3. percent 存在但 reset 缺失时,进度仍在并显示“重置时间未报告”。
4. Fable 存在时侧栏出现 `Claude · Fable`;100% 显示“已用尽”。
5. 非限流 carried 值为琥珀“最近读数”,不是绿色;即使百分比未知也必须是静态琥珀,且后端不得将含 carried 窗口的 bucket 标为 `Allowed=true`。
6. 探测失败时显示不可得/错误,不是空闲。

### 11.3 脱敏存储核对

若机器已安装 `sqlite3`,只查询列长度与时间,不要输出 `snapshot_json` 正文:

```powershell
sqlite3 "$env:LOCALAPPDATA\AI Resume\state\runs.db" `
  "SELECT provider,length(credential_fingerprint),length(snapshot_json),updated_at FROM quota_snapshots;"
```

预期同一 Claude 账号只有一行,指纹长度为 64。切换账号后允许出现另一行,但两个账号不得互相承接窗口。

### 11.4 安装产物一致性

```powershell
Get-FileHash csharp\src\AiResume.Worker\bin\Release\net10.0-windows\AiResume.Worker.dll
Get-FileHash "$env:LOCALAPPDATA\AI Resume\AiResume.Worker.dll"
Get-FileHash csharp\src\AiResume.Gui\bin\Release\net10.0-windows\AiResume.Gui.dll
Get-FileHash "$env:LOCALAPPDATA\AI Resume\AiResume.Gui.dll"
```

同名源码产物与安装产物哈希应一致。若不一致,用户看到的不是本次实现,不得继续解释界面行为。

## 12. 2026-08-09 验证记录

本轮在同一个真实 OAuth token 上得到以下脱敏证据:

- 普通 .NET 请求形状返回 HTTP 429。
- 补齐 `Accept`、OAuth beta、Anthropic version 与 `claude-code/2.1.185` User-Agent 后返回 HTTP 200。
- 现代响应包含 `session=0%`、`weekly_all=100%`、`weekly_scoped:Fable=100%`。
- 产品 `ClaudeOAuthUsageProbe` 映射为:`five_hour` 0%、`seven_day` 100% blocked、`weekly_scoped:Fable` 100% blocked。
- 真机 GUI 显示 7 天真实满额进度条与 `Claude · Fable 已用尽`;5 小时 0% 在 reset 缺失时仍显示百分比并明确“重置时间未报告”。
- 合成 reset-only GUI 冒烟显示完整低亮分段轨道与移动未知扫描,没有把扫描位置写成百分比或 `aria-valuenow`;全局 limited 但单窗无 100% 证据时不再给该窗贴“已限流”。
- 两块 CRT 内屏等高。
- 生产 `quota_snapshots` 有 1 行,账号指纹长度 64,快照 JSON 长度 780;未输出 token 或 JSON 正文。
- Release Worker/GUI DLL 与安装目录 DLL 哈希一致。
- 全量 xUnit:972 通过、0 失败、0 跳过;Release build:0 warning、0 error。
- 本轮额度/GUI 聚焦测试:94 通过、0 失败、0 跳过。
- 两轮独立只读对抗性审查覆盖 modern/legacy 优先级、跨进程丢失更新、v4 到 v5 迁移、Fable 稀疏承接、carried UI、percent 无 reset、scoped 缺 percent 与左右布局;发现项均复核并修正。

这些百分比只证明 2026-08-09 当时的链路与语义,不是长期固定值。以后验证必须以当次服务端响应和截图为准。

## 13. 修改额度功能时的检查清单

改动前:

- 先读本文件与 [`UPSTREAM-ARCHITECTURE-RESEARCH.md`](UPSTREAM-ARCHITECTURE-RESEARCH.md)。
- 若引入新端点、新状态机、新解析或新持久化形状,先重新盘点 Claude Code、cc-connect 和成熟监视器在 Windows 上的现状。
- 明确本次改变的是请求、解析、合并、存储、续跑判定还是 UI;不要在错误层补丁。

改动中:

- 不打印真实凭据或完整响应。
- 保持 modern 逐字段优先 legacy。
- 保持 null、0、100、blocked、carriedForward 的不同语义。
- 保持账号 + 稳定窗口身份（主窗口 kind / scoped 完整 scope 哈希）+ reset 代次边界。
- 保持 SQLite 事务内 read-merge-write。
- 保持存储和网络 I/O 在后台路径。
- 新服务端字段先保留未知,不要猜成已有窗口。

交付前:

- 跑额度聚焦测试与全量测试。
- Release build 使用 `-warnaserror`。
- 重新 install,确认 DLL 哈希。
- 用安装目录 GUI 真机截图。
- 同时检查进度条、reset 缺失、Fable、失败态、carried 态与左右等高。
- 更新本文件的“最后验证日期”和验证记录;若协议发生决策级变化,同步 `CLAUDE.md`、`ARCHITECTURE.md`、双语 README 与 `AI_GUIDE.md`。

## 14. 代码索引

- OAuth 请求、凭据、UA、modern/legacy/Fable 解析:[`ClaudeOAuthUsageProbe.cs`](../csharp/src/AiResume.Worker/Quota/ClaudeOAuthUsageProbe.cs)
- 数据源优先级、缓存、single-flight、稀疏合并:[`QuotaService.cs`](../csharp/src/AiResume.Worker/Quota/QuotaService.cs)
- SQLite 账号快照与原子更新:[`QuotaSnapshotStore.cs`](../csharp/src/AiResume.Worker/Quota/QuotaSnapshotStore.cs)
- SQLite schema v4/v5 迁移:[`StorageDatabase.cs`](../csharp/src/AiResume.Storage/StorageDatabase.cs)
- CLI 降级探测:[`ClaudeCodeProbe.cs`](../csharp/src/AiResume.Worker/Probes/ClaudeCodeProbe.cs)
- provider-neutral 契约:[`UsageSnapshot.cs`](../csharp/src/AiResume.Worker/Quota/UsageSnapshot.cs)
- GUI RPC 与本地 5 小时块:[`ControlPlaneBridge.cs`](../csharp/src/AiResume.Gui/ControlPlaneBridge.cs)
- 进度条、Fable 行和状态呈现:[`index.html`](../csharp/src/AiResume.Gui/wwwroot/index.html)
- 自动化测试:[`AiResume.Tests`](../csharp/test/AiResume.Tests)
