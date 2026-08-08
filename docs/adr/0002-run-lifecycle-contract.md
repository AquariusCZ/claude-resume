# ADR-0002:AI 运行生命周期采用 Start/Status/Cancel

- 状态:**Accepted**
- 日期:2026-08-01
- 决策者:用户(架构权威);OpenAI Codex 架构监督
- 取代范围:ADR-0001 中关于目标运行时「无总时限」的简略描述;`EVENT-CONTRACTS` v1 中 `deadline_ms`、通用 `task.failed`、超时取消和 transient 网络 fallback 语义
- 关联:`docs/RUN-CONTRACT.md`、`docs/EVENT-CONTRACTS.md`、`docs/STATE-OWNERSHIP.md`、`docs/adr/0001-target-architecture.md`

## 背景

现役 Node/PowerShell 链路把 AI 调用建模为一次等待 Promise/子进程结束的同步操作。部分任务没有总时限,查询/闲聊仍有 30 分钟 deadline,provider 健康探测还有客户端墙钟预算。该模型会把「长时间静默」「监控读取失败」「网络连接故障」「服务端主动超时」混成 timeout,并诱发错误取消、线路切换或 provider fallback。

迁移目标需要支持可持续数小时的生成、进程崩溃恢复、GUI/飞书共同查看状态以及可靠停止。静默时间只能说明没有输出,不能证明任务失败。

## 决策

### 1. 统一运行协议

所有 AI 生成和 provider 健康探测统一采用异步 `Start / Status / Cancel`:

- `Start` 持久接纳请求并立即返回 `runId`,不等待生成结束;
- `Status` 只读取 RunStore 持久快照和 ProcessSupervisor 的最近进程观察;
- `Cancel` 是幂等命令,请求终止完整进程树;
- Worker 每 15-30 秒观察持久状态和进程存活性,默认建议 20 秒;
- GUI、飞书层和 cc-connect 适配层只消费统一运行事件/快照,不得直接拥有 AI 子进程或自行推断 terminal。

### 2. 不设客户端总时长上限

AI 生成过程不创建客户端总时长计时器。适用于 chat、query、modify、resume、provider probe 和后台自动续跑。任务只因以下事实进入 terminal:

- provider 明确成功;
- provider 返回可验证的结构化失败;
- 本地/网络/进程边界明确失败;
- 用户或受权控制面明确取消。

`heartbeatAt`、`lastOutputAt`、`silentSeconds` 仅为观测指标。无论静默多久,都不得单独触发失败、取消、fallback 或重放。

### 3. 状态机

权威状态为:

`queued -> starting -> running -> succeeded | failed_provider | failed_local | cancelled`

取消可在 `queued`/`starting` 阶段提前终止,但不得产生 `running` 假事件。terminal 不可逆;任何迟到输出或监控结果只能记录为诊断,不能改写 terminal。

### 4. 错误分类

- `failed_provider`:provider/服务端明确返回的结构化终态。只有 HTTP `408`、HTTP `504` 或结构化错误码 `gateway_timeout` 可归类为服务端主动超时。
- `failed_local`:DNS、TCP connect、TLS、connection reset/broken pipe、spawn/Job Object/登记/SQLite/解析/监控错误、进程消失或进程身份不可核验。
- `cancelled`:用户停止、GUI 解除对应周期或受权控制面取消。取消不是错误,不得 fallback。

字符串中出现 `timeout`、长时间无输出或本地计时器到点,都不足以生成 `failed_provider`。

### 5. fallback 与重放

自动 fallback 必须同时满足:

1. terminal 为 `failed_provider`;
2. ProviderAdapter 给出明确可 fallback 的结构化错误策略;
3. `sideEffectsStarted=false`;
4. 原子进程已真实 close,`childPending=false`;
5. 同一请求尚未消费 fallback 配额;
6. 新 provider 使用新的 `runId`,并通过 `parentRunId`/`attemptGroupId` 关联。

`failed_local`、`cancelled`、`sideEffectsStarted=true`、进程身份不明或 child pending 时一律禁止自动 fallback/重放。DNS/TCP/TLS/reset 不因更换 provider 自动重试;用户可在确认现场后显式 Start 新请求。

### 6. 持久化与恢复

C# Worker 的 `RunStore` 是运行状态唯一 writer,使用 SQLite/WAL。Start 在响应前持久化 `queued`;spawn 前持久化 `starting` 意图;进程 PID、启动时间、命令签名和 Job Object 所有权持久化成功后才进入 `running`。

Worker 重启后逐条恢复非 terminal run 与 `childPending=true` 的 terminal run:

- 进程身份完整匹配且可重新监督:继续 `running`,不重放;
- 进程明确 gone 且无已持久化 provider terminal:进入 `failed_local/process_disappeared`;
- 启动意图存在但是否 spawn 无法证明:进入 `failed_local/ambiguous_launch`,禁止重放;
- PID/启动时间/命令签名/所有权不可核验:进入 `failed_local/ownership_unverifiable`,保留 `childPending` 与 runKey 锁,继续观察,不得按 PID 猜测终止。

### 7. 单次 API 请求仍有超时

飞书消息发送/卡片更新、图片上传下载、lark-cli 单次 OpenAPI、Named Pipe 单帧请求等传输操作保留请求级超时。它们不等于 AI run 总时限:

- 输入准备阶段失败可拒绝 Start 或形成 `failed_local`;
- progress/result/notification 投递失败进入 outbox 重试,不回写已完成 AI run 的 terminal;
- create/send 重试必须复用稳定幂等键;图片上传等不可安全重试的操作不自动重放。

## C# 责任边界

| 组件 | 强制职责 |
|---|---|
| `TaskOrchestrator` | Start/Status/Cancel、状态转换、runKey 并发、fallback 决策、terminal settle-once |
| `ProcessSupervisor` | spawn、Windows Job Object、PID 身份、进程树取消、15-30 秒存活观察、childPending |
| `ProviderAdapter` | provider argv/env/session、结构化输出、服务端错误分类、side effect 活动事件 |
| `RunStore` | SQLite/WAL 运行快照、事件序号、命令幂等、进程登记、恢复扫描 |
| `HealthProbe` | 以同一 Start/Status/Cancel 运行最小真实请求;只把真实成功写为 available |
| `Transport` | Named Pipe/cc-connect/GUI/飞书命令与统一信封转换;不拥有运行状态 |

## 替代方案

| 方案 | 结论 | 原因 |
|---|---|---|
| 为不同 taskKind 设置固定总时限 | 拒绝 | 长生成和静默无法区分;会制造假失败和重复执行 |
| 以 heartbeat/silentSeconds 触发 timeout | 拒绝 | 心跳/输出是观测信号,不是 provider terminal 事实 |
| 任意网络错误自动切 provider | 拒绝 | 本地故障不证明备用 provider 可用,且可能重放副作用 |
| sideEffectsStarted 后继续 fallback | 拒绝 | 两个 provider 可能同时修改同一项目 |
| Start/Status/Cancel + durable RunStore | **采纳** | 可恢复、可取消、可审计,且不把静默误判为失败 |

## 后果

正面:

- 长任务不再被客户端计时器截断;
- GUI/飞书/cc-connect 看到同一状态和错误语义;
- 崩溃恢复、取消和 fallback 有可验证的持久边界;
- 网络故障与服务端主动 timeout 不再混淆。

代价:

- 需要持久 RunStore、进程监督和显式轮询;
- terminal 与进程实际 close 可短暂分离,调用方必须理解 `childPending`;
- 现役 Node 查询/闲聊 30 分钟 deadline 在迁移切换前仍是现役事实,不得把本 ADR 冒充已部署行为。

## 验收

- 静默进程持续超过历史 deadline 仍为 `running`;
- 结构化 408/504/gateway_timeout -> `failed_provider`;
- DNS/TCP/TLS/reset、进程消失、监控/存储异常 -> `failed_local`;
- Cancel -> `cancelled`,自动 fallback 次数为 0;
- sideEffectsStarted 后自动 fallback/重放次数为 0;
- 重复 Start/Cancel/事件投递不重复 spawn、不重复远端副作用;
- Worker 崩溃重启后恢复或明确 `failed_local`,不因静默猜测 terminal;
- 飞书/图片等单次 API timeout 不改变已成功 AI run 的 terminal。
