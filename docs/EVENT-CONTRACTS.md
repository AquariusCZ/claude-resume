# AI Resume 事件 / 命令契约(EVENT-CONTRACTS)

> 状态:历史迁移设计,2026-08-01。Node/PowerShell 已退役;本文件没有整体接线为现役协议。当前运行生命周期真身是 ADR-0002、`RUN-CONTRACT.md` 与 C# 实现,引用本文件时必须逐项核对当前代码。

## 1. 目的与原则

- 所有跨进程通信(文件队列、Named Pipe、cc-connect 桥、outbox)使用版本化信封。
- 至少一次投递(at-least-once)+ 幂等键去重;同 run 事件由 RunStore 分配单调序号。
- 命令与事件分离:Start/Status/Cancel 是请求;状态变化、进程观察和投递结果是已发生事实。
- AI 运行不设客户端总时长上限;静默、heartbeat 和 elapsed 不能生成失败。
- GUI、飞书和 cc-connect 适配层只消费 Worker 统一事件/Status,不直接 spawn 或推断 terminal。
- 机密禁止进入信封、日志和 outbox。

## 2. 信封 v2

| 字段 | 类型 | 必填 | 说明 |
|---|---|---|---|
| `envelope_version` | string | 是 | 固定 `"2"`;消费者拒绝未知版本 |
| `event_id` | UUID | 是 | 本记录唯一 ID,生成后不可变 |
| `type` | string | 是 | 命令/事件类型(第 3 节) |
| `source` | string | 是 | `worker|gui|cc-connect|lark-cli|hook-codex|hook-claude|hook-cline|feishu` |
| `ts` | int64 | 是 | Unix 毫秒 UTC |
| `idempotency_key` | string | 是 | 消费方去重键 |
| `payload` | object | 是 | 类型化内容,禁止机密 |
| `run_id` | UUID | 否 | 运行相关记录必填 |
| `seq` | int64 | 否 | run 内事件序号;命令可为 0 |
| `actor` | string | 否 | open_id 或本地受权身份 |
| `correlation_id` | string | 否 | 飞书 event_id、attemptGroupId 等关联 |
| `causation_id` | string | 否 | 直接触发本记录的命令/事件 ID |
| `attempt` | int32 | 否 | 传输投递次数;不进入远端幂等键 |
| `ack` | object | 否 | `{state:pending|delivered|failed|expired,at,error_class}` |

RunContract payload 额外携带 `contract_version="1"`。

## 3. 命令与事件类型

| type | 类别 | 方向 | 语义 |
|---|---|---|---|
| `inbound.message|inbound.card|inbound.menu` | 事件 | 飞书/cc-connect -> Worker | 入站事件持久接纳 |
| `run.start.requested` | 命令 | GUI/飞书/cc-connect/checker -> Worker | RunContract Start |
| `run.status.requested` | 查询命令 | GUI/飞书/cc-connect -> Worker | 只读 RunContract Status |
| `run.cancel.requested` | 命令 | GUI/飞书/cc-connect/checker -> Worker | RunContract Cancel |
| `run.state_changed` | 事件 | Worker -> 所有消费者 | 权威状态变化 |
| `run.metrics` | 事件 | Worker -> GUI/飞书 | heartbeat/output/silent 指标;非 terminal |
| `run.recovered` | 事件 | Worker -> GUI/飞书/运维 | 重启后的恢复判断与动作 |
| `process.observed` | 事件 | ProcessSupervisor -> RunStore | alive/gone/unknown 观察 |
| `process.closed` | 事件 | ProcessSupervisor -> RunStore | 真实 close/error settle-once |
| `provider.health_changed` | 事件 | HealthProbe -> GUI/cc-connect | 真实最小请求结果 |
| `completion.event` | 事件 | 本地 hook -> Worker | Codex/Claude Code/Cline 完成边界 |
| `outbox.delivery` | 事件 | Worker -> Transport | 投递 attempt 与 ACK |
| `project.catalog_changed` | 事件 | Worker -> cc-connect | 确定性项目目录版本 |

旧 `task.requested/started/progress/completed/failed/cancelled/interrupted` 不进入目标 v2。兼容适配器若接收旧形状,必须在边界转换为 v2,不得让 GUI/飞书同时理解两套状态机。

## 4. 幂等键与重放

| 类型 | 幂等键 | 消费语义 |
|---|---|---|
| InboundEvent | 飞书 `event_id`;无 id 时为受控 350ms 窗口键 | 持久接纳后 ACK;同 chat 保序,跨 chat 并发 |
| Start | `requestId` | 同摘要返回原 runId;不同摘要返回 conflict;spawn 最多一次 |
| Status | `runId + callerRequestId` | 只读,不得改变任何状态 |
| Cancel | `commandId` | 同命令只发一次终止请求,返回相同结果 |
| RunState/Metrics/Recovered | `runId + seq` | 重复丢弃;terminal 后 progress/metrics 不覆盖状态 |
| ProcessObserved | `runId + observationId` | 记录三态观察,unknown 不等于 gone |
| ProcessClosed | `runId + pid + processStartedAt` | settle-once,真实 close 才清 childPending |
| ProviderHealthChanged | `fingerprint + runId + terminalState` | 配置指纹失配立即作废 |
| CompletionEvent | 客户端事件 ID + thread/session | 七天去重,稳定消息 UUID |
| OutboxDelivery | `outboxId` | 所有 attempt 复用同一远端幂等键 |
| ProjectCatalogChanged | `catalogVersion` | 按版本确定性生成配置 |

任何自动重放都受 `RUN-CONTRACT.md` 限制。`failed_local`、`cancelled`、`sideEffectsStarted=true` 或 `childPending=true` 禁止自动 fallback/重放。

## 5. 入站事件

`inbound.*` payload:

| 字段 | 必填 | 说明 |
|---|---|---|
| `channel` | 是 | `feishu` |
| `chat_id` / `open_id` | 是 | 会话和用户 |
| `platform_event_id` | 否 | 飞书 event_id |
| `message_type` | 否 | text/post/image 等 |
| `card_action` / `menu_key` | 否 | 一次性 token/底部菜单 key |
| `raw_ts` | 是 | 平台时间戳 |

入站必须在 500ms 内完成校验、幂等判断和持久接纳后返回传输 ACK。长任务不得占用 ACK handler;同 chat 业务严格保序。

## 6. Start/Status/Cancel

字段与状态真身见 `RUN-CONTRACT.md`。跨进程信封最小 payload:

### run.start.requested

`contract_version, requestId, runKey, taskKind, actor, projectRef, profileId, sessionRef, cwd, inputRef, credentialRef, fallbackPolicy, attemptGroupId, parentRunId`

禁止传入任何客户端总时长字段,包括 `deadline_ms`、`timeout_ms`、`timeout_minutes` 和 silent timeout。

### run.status.requested

`contract_version, runId, callerRequestId`

Status 只返回持久 `RunSnapshot`;不得隐式 probe、spawn、fallback、cancel 或发送远端消息。

### run.cancel.requested

`contract_version, commandId, runId, requestedBy, reason(user_stop|disarm|replaced|shutdown)`

不存在 timeout cancel reason。取消 terminal 为 `cancelled`,fallback 永远为 false。

## 7. 运行事件

### run.state_changed

`runId, seq, previousState, state, stateVersion, sideEffectsStarted, childPending, error, resultRef, changedAt`

权威状态:

`queued -> starting -> running -> succeeded | failed_provider | failed_local | cancelled`

允许 `queued|starting -> cancelled` 与 `starting -> failed_local`。terminal 不可逆。

### run.metrics

`runId, seq, heartbeatAt, lastOutputAt, silentSeconds, outputBytes, tokenCount, observedAt`

这些字段只用于观测。消费者不得以任何阈值自行产生 terminal、fallback 或 Cancel。

### run.recovered

`runId, seq, priorState, processLiveness, identityMatch, recoveryAction, childPending, recoveredAt`

恢复动作必须是 `continue_monitoring|mark_process_disappeared|mark_ambiguous_launch|mark_ownership_unverifiable|settle_closed` 之一;不得自动重放原 Start。

### process.observed / process.closed

Observed:`runId, observationId, pid, processStartedAt, liveness(alive|gone|unknown), identityMatch, commandSignatureMatch, jobOwnership, observedAt, monitorError`

Closed:`runId, pid, processStartedAt, exitCode, closeObservedAt, childPending=false`

只有 gone/closed 的明确证据可以释放进程登记。CIM/监控失败是 unknown,不得当 gone 或仅凭 PID kill。

## 8. 错误分类

| terminal/class | 证据 | 示例 | 自动 fallback |
|---|---|---|---|
| `failed_provider/provider` | provider 明确结构化终态 | auth、quota、rate-limit、model unavailable、HTTP 408/504、`gateway_timeout` | 仅显式 allowlist + 无副作用 + child closed |
| `failed_local/local` | 本地/网络/进程/存储/监控事实 | DNS、TCP、TLS、reset、spawn、SQLite、parser、process gone/unknown | 否 |
| `cancelled/cancelled` | 已持久化 Cancel/Disarm 命令 | user_stop、disarm、replaced、shutdown | 否 |

只有 HTTP 状态 `408`、`504` 或结构化 provider code `gateway_timeout` 可使用 `provider_timeout`。自由文本 `timeout`、静默时间、客户端计时器到点不得使用该分类。

错误 payload:`class, code, message(脱敏), evidenceKind, httpStatus, providerCode, retryableByUser, fallbackAllowed`。

## 9. fallback 与 side effect

自动 fallback 创建新 run,不得在原 run 中换 provider/route。必须满足:

- 原 run 为 `failed_provider`;
- `sideEffectsStarted=false`;
- `childPending=false`;
- provider error code 在 policy allowlist;
- Cancel 未请求;
- attemptGroup 尚未使用 fallback。

工具/活动事件:

- assistant token、思考、纯只读元数据:不设置 side effect;
- 文件写、命令执行、git、网络写、高风险飞书写:设置 true;
- malformed/unknown tool activity:fail-closed 设置 true。

side effect 标志一旦为 true 永不回退;持久化失败也按已开始副作用处理。

## 10. Provider 健康事件

`provider.health_changed` payload:

`provider, model, runId, state, route, reasonClass, fingerprint, ttlMs, childPending, observedAt`

- available 只能来自 `probe` run 的真实成功 terminal;
- probe 同样没有客户端总时长,使用 Start/Status/Cancel;
- DNS/TCP/TLS/reset 为 failed_local,不能冒充服务端 timeout;
- 配置指纹变化立即作废快照;
- 正式任务固定 Start 时选定线路,运行中不换线。

## 11. Completion / Outbox / Project

### completion.event

`sourceClient(codex|claude-code|cline), projectPath, projectName, threadId/sessionId, finishedAt, messageUuid`

保留现役准入:Codex 顶层持久 thread、真实 Git/workspace、internal-run 抑制、projectless 抑制。DeepSeek Copilot Chat 不新增完成边界。

### outbox.delivery

`outboxId, runId/eventId, channel, attempt, state(pending|delivered|failed|expired), deliveredAt, remoteMessageId, errorClass`

AI run terminal 与消息投递分离。结果/进度发送失败不把 `succeeded` 改为失败。create/send 重试复用稳定幂等键;不可幂等上传不自动重放。

### project.catalog_changed

`catalogVersion, projects:[{path,name,source}], trigger(discovery|custom|hidden), changedAt`

项目数量不是契约。

## 12. 单次请求超时

允许 request-level timeout:

- 飞书 send/reply/card patch;
- 飞书图片上传、资源下载和落盘阶段;
- lark-cli 单次 OpenAPI;
- Named Pipe 建连/单帧;
- outbox 单次 attempt。

这些 timeout 不得成为 AI run deadline。输入准备失败可拒绝 Start 或形成 failed_local;terminal 后投递失败进入 outbox。

## 13. 序号与排序

- RunStore 为 run 事件唯一分配 `seq>=1`;
- `run.state_changed` 按 seq 应用;重复丢弃;
- metrics 可只保留最新,但不得越过 terminal 改状态;
- terminal 后允许 process.closed/outbox 等辅助事件,不允许第二个 terminal;
- 事件推送丢失时,Status 快照必须足以恢复全部权威状态。

## 14. 机密禁入

- payload 只允许 ID、受控路径引用、时间、状态和脱敏错误;
- API key、app secret、token、bridge token、代理/订阅凭据和完整命令行禁止进入;
- 机密仅用 `credentialRef` 指向 C# 机密存储;
- prompt/附件使用 `inputRef`,不内联到 runs/events/outbox;
- 日志、RunStore 和 cc-connect 桥同样适用。

## 15. 版本演进

- v2 是 ADR-0002 后的目标信封;目标消费者不得接受 v1 task 语义后自行猜测状态;
- 新增可选字段可兼容;字段删除、类型/状态语义变化必须升 envelope version 并走 ADR;
- Stage 1 兼容适配器可读取现役形状,但输出给 C#/GUI/飞书的目标边界只能是 v2;
- Stage 2-6 的 SQLite schema、Named Pipe 帧和 cc-connect wrapper 必须通过 `RUN-CONTRACT.md` 最小门禁。
