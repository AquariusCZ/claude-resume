# AI Resume RunContract v1

> 状态:现役运行契约。2026-08-01 由 ADR-0002 接受，随后由 C# Worker 实现；Node/PowerShell 迁移阶段的接管顺序仅作为历史背景保留。当前实现与偏差以 `docs/ARCHITECTURE.md` 和 C# 回归为准。

## 1. 目标与不变量

RunContract 是 GUI、飞书/cc-connect、后台续跑与 provider adapter 之间唯一的 AI 运行协议。所有调用者只使用 `Start`、`Status`、`Cancel` 和统一事件流。

强制不变量:

1. AI 生成无客户端总时长上限;不存在 `deadlineMs`、`timeoutMinutes` 或 silent timeout。
2. Worker 每 15-30 秒观察持久状态与进程存活性;默认 20 秒。
3. `heartbeatAt`、`lastOutputAt`、`silentSeconds` 只用于展示与诊断。
4. 只有结构化 HTTP 408/504/`gateway_timeout` 是服务端主动 timeout。
5. DNS/TCP/TLS/reset、进程消失、监控/解析/存储异常属于 `failed_local`。
6. 用户取消为 `cancelled`,不可 fallback。
7. `sideEffectsStarted=true` 后禁止自动 fallback、自动重放和线路切换。
8. 同一生产 run 的状态只有 C# `RunStore` 一个 writer。
9. terminal settle-once;迟到输出不能覆盖 terminal。
10. 进程未真实 close 或身份不明时 `childPending=true`,runKey 不释放。

## 2. 服务接口

### 2.1 Start

`Start(StartRequest) -> StartResponse`

Start 只负责持久接纳和排队。成功响应不表示 provider 子进程已经启动。

`StartRequest`:

| 字段 | 类型 | 必填 | 说明 |
|---|---|---|---|
| `contractVersion` | string | 是 | 固定 `"1"` |
| `requestId` | UUID | 是 | 调用方生成的幂等键;一次用户动作保持稳定 |
| `runKey` | string | 是 | 并发所有权键,如项目路径/query cwd/用户 chat key |
| `taskKind` | enum | 是 | `chat|query|modify|resume|probe` |
| `actor` | string | 否 | open_id 或本地控制面身份 |
| `projectRef` | string | 否 | 项目标识/路径引用;不得内联机密 |
| `profileId` | string | 是 | provider/model profile |
| `sessionRef` | object | 否 | provider 原生 session/thread 引用 |
| `cwd` | string | 否 | 经策略校验的工作目录 |
| `inputRef` | string | 是 | prompt/附件的受控存储引用,不把大文本放运行表 |
| `credentialRef` | string | 否 | DPAPI/Credential Manager 引用 |
| `attemptGroupId` | UUID | 否 | fallback 链关联;首次 Start 可省略 |
| `parentRunId` | UUID | 否 | 仅显式 fallback/续跑关联 |
| `fallbackPolicy` | string | 是 | `none|provider_explicit_once` |

禁止字段:`deadlineMs`、`timeoutMs`、`timeoutMinutes`、以静默秒数表达的失败阈值。

`StartResponse`:

| 字段 | 说明 |
|---|---|
| `accepted` | 是否已持久接纳 |
| `runId` | Worker 生成的稳定 UUID |
| `state` | 首次响应通常为 `queued` |
| `stateVersion` | 乐观并发版本 |
| `existing` | requestId 已存在时为 true |
| `conflict` | 同 requestId 不同规范化请求摘要时返回 |

Start 幂等:

- 同 `requestId` + 相同规范化请求摘要:返回原 `runId`,不重复 spawn;
- 同 `requestId` + 不同摘要:拒绝 `idempotency_conflict`;
- 同 `runKey` 已有非 terminal 或 `childPending=true` 的 run:拒绝 `run_key_busy`,返回占用 runId;
- 只有 `queued` 已在 SQLite/WAL 事务中提交后才返回 accepted。

### 2.2 Status

`Status(runId) -> RunSnapshot`

Status 是只读操作,不得触发 spawn、cancel、fallback、健康探测或远端发送。调用方每 15-30 秒读取;事件订阅只能降低延迟,不能替代持久快照真身。

### 2.3 Cancel

`Cancel(CancelRequest) -> CancelResponse`

`CancelRequest`:

| 字段 | 说明 |
|---|---|
| `commandId` | Cancel 命令幂等 UUID |
| `runId` | 目标 run |
| `requestedBy` | 用户/GUI/周期控制面 |
| `reason` | `user_stop|disarm|replaced|shutdown` |

规则:

- 重复 `commandId` 返回相同结果;
- terminal run 返回当前 terminal,不再次杀进程;
- Cancel 持久化 `cancelRequestedAt` 后请求 ProcessSupervisor 终止 Job Object/完整进程树;
- run 进入 `cancelled`;即使终止宽限期后进程尚未 close,也保持 `childPending=true` 与 runKey 锁;
- Cancel 之后 fallback/replay 永远为 false。

## 3. RunSnapshot

| 分组 | 字段 |
|---|---|
| identity | `runId, requestId, runKey, taskKind, actor, attemptGroupId, parentRunId` |
| selection | `profileId, provider, model, route, sessionRef` |
| state | `state, stateVersion, seq, terminalReason` |
| time | `queuedAt, startingAt, startedAt, terminalAt, observedAt` |
| metrics | `heartbeatAt, lastOutputAt, silentSeconds, outputBytes, tokenCount` |
| process | `wrapperPid, childPid, processStartedAt, executablePathHash, commandSignature, jobId, processLiveness, childPending` |
| safety | `sideEffectsStarted, sideEffectsStartedAt, fallbackAllowed, replayAllowed, cancelRequestedAt` |
| error | `errorClass, errorCode, providerHttpStatus, providerErrorCode, message` |
| recovery | `workerInstanceId, recovered, recoveryAction, monitorHealth, lastMonitorErrorAt` |

字段约束:

- `processLiveness=alive|gone|unknown`;只有匹配 PID + 启动时间 + 命令签名 + 所有权才是 alive;
- `heartbeatAt` 是 ProcessSupervisor 最近一次成功观察,不是 provider 心跳要求;
- `lastOutputAt` 只在收到有效 stdout/stderr/provider event 时更新;
- `silentSeconds = now - max(startedAt, heartbeatAt, lastOutputAt)` 的展示值;缺数据时为 null;
- `sideEffectsStarted` 一旦 true 永不回退;
- `fallbackAllowed`/`replayAllowed` 是 Worker 根据 terminal、side effect、childPending 计算的派生字段,调用方不能覆盖;
- `message` 必须脱敏,不得含 prompt、密钥、token 或完整命令行。

## 4. 状态机

```text
queued -> starting -> running -> succeeded
                              -> failed_provider
                              -> failed_local
                              -> cancelled
```

允许的提前取消:

- `queued -> cancelled`
- `starting -> cancelled`

允许的启动失败:

- `starting -> failed_local`

其余转换全部拒绝。terminal 状态为 `succeeded|failed_provider|failed_local|cancelled`。

### queued

- Start 已持久接纳;
- 尚未 spawn;
- runKey 已占用;
- Cancel 可直接终止,不创建进程。

### starting

- spawn 意图、provider/profile/cwd/argv 摘要已持久化;
- ProcessSupervisor 正在创建 Job Object 和子进程;
- PID 登记失败必须请求终止并进入 `failed_local/registry_write_failed`;
- 不允许从 starting 自动重新 Start 第二个进程。

### running

- 进程登记、启动时间、命令签名、Job Object 所有权已持久化;
- 每 15-30 秒观察 liveness;
- 静默保持 running;
- malformed/unknown tool activity 按 fail-closed 将 `sideEffectsStarted=true`。

### succeeded

- ProviderAdapter 解析到明确成功 terminal;
- terminal 与最终事件在同一事务写入;
- 后续消息/图片发送由 outbox 负责,投递失败不改写 succeeded。

### failed_provider

仅用于 provider 明确返回的结构化终态,例如:

- HTTP 401/403 或结构化认证错误;
- 结构化 quota/rate-limit/model unavailable;
- HTTP 408、HTTP 504、结构化 `gateway_timeout`;
- provider 文档定义的其他结构化 terminal。

自由文本包含 `timeout`、进程 exit 非零但无结构化 provider 错误、JSON/NDJSON 解析失败不能归入 failed_provider。

### failed_local

包括:

- DNS lookup、TCP connect、TLS 握手、connection reset/broken pipe;
- executable/CLI 缺失、spawn 失败、Job Object 失败;
- RunStore/SQLite/登记/fsync/事务失败;
- provider 输出无法按约定解析、监控器异常;
- 进程消失且没有持久 provider terminal;
- PID/启动时间/命令签名/父子关系/Job 所有权不可核验;
- 启动意图存在但是否 spawn 无法证明。

failed_local 默认 `fallbackAllowed=false`、`replayAllowed=false`。

### cancelled

- 只由已持久化 Cancel/Disarm/受权 shutdown 产生;
- 不使用 timeout 作为 cancel reason;
- 永不 fallback/replay;
- childPending 可在 terminal 后继续为 true,直到真实 close/gone 被验证。

## 5. 错误信封

`RunError`:

| 字段 | 说明 |
|---|---|
| `class` | `provider|local|cancelled` |
| `code` | 稳定机器码 |
| `message` | 脱敏短消息 |
| `retryableByUser` | 用户可否显式重新 Start |
| `fallbackAllowed` | Worker 最终判断 |
| `httpStatus` | 仅结构化 provider HTTP 状态 |
| `providerCode` | 结构化 provider 错误码 |
| `evidenceKind` | `structured_http|structured_provider|process|network|storage|monitor|user_command` |

关键机器码:

| terminal | code |
|---|---|
| failed_provider | `provider_auth|provider_quota|provider_rate_limit|provider_model_unavailable|provider_timeout|provider_rejected` |
| failed_local | `dns_failure|tcp_failure|tls_failure|connection_reset|spawn_failed|registry_write_failed|process_disappeared|monitor_failed|ownership_unverifiable|ambiguous_launch|protocol_parse_failed|storage_failed` |
| cancelled | `user_stop|disarm|replaced|shutdown` |

## 6. fallback 契约

自动 fallback 只从 `failed_provider` 创建一个新的 child run。必须全部满足:

- 原 run `sideEffectsStarted=false`;
- `childPending=false`;
- `fallbackPolicy=provider_explicit_once`;
- error code 在 provider policy allowlist;
- attemptGroup 尚无已启动 fallback;
- Cancel 未请求;
- 新 run 使用独立 `runId` 和相同 `attemptGroupId`,`parentRunId` 指向失败 run。

禁止:

- 在同一 run 中替换 provider/model/route;
- `failed_local` 自动 fallback;
- Cancel 后 fallback;
- side effect 后 fallback;
- childPending 或进程身份 unknown 时 fallback;
- 因静默、elapsed、heartbeat 旧而 fallback。

## 7. side effect 契约

ProviderAdapter 将工具/活动事件规范化为:

| activity | 是否设置 sideEffectsStarted |
|---|---|
| 纯 assistant token/思考/只读元数据 | 否 |
| 文件写入、命令执行、git、网络写、高风险飞书写 | 是 |
| malformed/unknown tool activity | **是,fail-closed** |

`sideEffectsStarted` 必须在处理 fallback 决策前持久化。若写入状态失败,run 进入 `failed_local/storage_failed`,且按已开始副作用处理,禁止重放。

## 8. RunStore 持久化

逻辑表(具体 SQLite schema 在 Stage 2 固化):

| 表 | 内容 |
|---|---|
| `runs` | 当前 RunSnapshot、规范化请求摘要、runKey 所有权 |
| `run_events` | append-only seq 事件 |
| `run_commands` | Start/Cancel 命令幂等结果 |
| `process_registry` | PID、启动时间、签名、Job、childPending、最近观察 |
| `outbox` | GUI/飞书/cc-connect/完成通知投递 |

事务边界:

1. Start:插入 command + queued run + seq 事件后提交,再响应;
2. Spawn:写 starting 意图后提交,再启动进程;
3. Running:进程身份与所有权登记提交后才能发 running;
4. Side effect:标志与事件同事务提交;
5. Terminal:状态、错误/结果引用和 terminal 事件同事务提交;
6. Release:只有 `childPending=false` 后释放 runKey/process_registry。

## 9. 恢复算法

Worker 启动扫描:

1. 非 terminal run;
2. terminal 且 `childPending=true` 的 run;
3. starting 意图与临时/未完成进程登记;
4. 未投递 outbox。

恢复判断:

| 证据 | 处理 |
|---|---|
| 完整身份匹配且进程 alive | 重新监督,保留/恢复 running,不重放 |
| 明确 gone,已有持久 terminal | 置 childPending=false,释放 runKey |
| 明确 gone,无 provider terminal | failed_local/process_disappeared |
| starting 意图但 spawn 不可证明 | failed_local/ambiguous_launch,不重放 |
| PID 存在但身份/所有权不可核验 | failed_local/ownership_unverifiable + childPending=true,保留锁并继续观察 |
| CIM/监控读取失败 | 记录 monitor degraded;若无法维持可靠所有权则 failed_local/monitor_failed,不得当 gone |

禁止仅凭 PID kill、仅凭静默判定 gone、自动重启同一请求或清除不可核验登记。

## 10. HealthProbe

HealthProbe 使用相同 RunContract,`taskKind=probe`:

- 必须发真实最小请求成功才能 available;
- 不设客户端总时限;
- 可由用户 Cancel;
- 每 15-30 秒通过 Status 观察;
- DNS/TCP/TLS/reset -> failed_local,不冒充 provider 不可用/超时;
- 结构化 provider 错误 -> failed_provider;
- GUI 自测/截图模式不 Start probe;
- 正式任务只使用 Start 时冻结的成功线路,运行中不换线。

## 11. 单次传输超时

以下操作可设置 request timeout:

- 飞书 send/reply/card patch;
- 飞书图片上传、资源下载及落盘阶段;
- lark-cli 单次 OpenAPI;
- Named Pipe 建连/单帧应答;
- outbox 单次投递 attempt。

它们不得成为 AI run deadline。进度/结果投递失败由 outbox 重试;远端 create 重试复用同一幂等键。无法安全幂等的上传不自动重放。

## 12. 事件与轮询

RunStore 为每次变化分配单调 `seq`。Transport 可推送 `run.state_changed`/`run.metrics`,但消费者必须能够仅靠 Status 恢复完整状态。

- 默认轮询:20 秒;
- 合法范围:15-30 秒;
- terminal 后可停止常规轮询,但 `childPending=true` 时继续低频读取;
- 同 run 的迟到 progress 不能覆盖 terminal;
- GUI/飞书不得根据没有新事件自行生成失败卡。

## 13. 最小门禁测试

1. 模拟进程 2 倍历史 deadline 无输出但 alive,状态仍 running;
2. heartbeat/lastOutput/silentSeconds 变化不触发 terminal;
3. 结构化 HTTP 408/504/gateway_timeout -> failed_provider/provider_timeout;
4. 文本 `timeout`、DNS/TCP/TLS/reset -> failed_local;
5. 进程无 terminal 消失 -> failed_local/process_disappeared;
6. 监控 unknown 不当作 gone,不按 PID kill;
7. Cancel -> cancelled,完整进程树终止,fallback=0;
8. sideEffectsStarted 后 provider 失败,fallback=0;
9. 重复 Start 返回同 runId,spawn=1;
10. 重复 Cancel 终止请求=1;
11. Worker 在 queued/starting/running/terminal-childPending 各点崩溃后恢复符合第 9 节;
12. 结果发送 timeout 时 run 保持 succeeded,outbox 保留;
13. 同一入站事件重放 100 次,run/远端消息重复数为 0;
14. GUI/飞书只通过 Transport 消费事件,不存在直接 spawn 路径。
