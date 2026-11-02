# AI Resume 事件 / 命令契约(EVENT-CONTRACTS)

> 状态:历史迁移设计,2026-08-01。Node/PowerShell 已退役;本文件没有整体接线为现役协议。当前运行生命周期真身是 ADR-0002、`RUN-CONTRACT.md` 与 C# 实现,引用本文件时必须逐项核对当前代码。
>
> 2026-10-30 修订(v2.2,ADR-0006):cc-connect 已整体退役,AI Resume 不再有任何飞书入站,飞书只剩 Worker 经官方 lark-cli 以机器人身份发出的完成通知。下文凡以 cc-connect 或飞书入站为来源、目标或调用方的条目(`inbound.*`、`project.catalog_changed`、`source` 中的 `cc-connect` 与 `feishu`、cc-connect 桥与 wrapper)只作历史记录,不再有生产者或消费者。代码里真实存在的信封、事件类型与完成通知队列格式见第 16 节;完成通知的投递细节见 `COMPLETION-NOTIFICATIONS.md`。

## 1. 目的与原则

- 所有跨进程通信(文件队列、Named Pipe、outbox)使用版本化信封;原列出的 cc-connect 桥已随 ADR-0006 退役。各通道的现役形态见第 16 节(完成通知文件队列只带 `version` 字段,不套第 2 节信封)。
- 至少一次投递(at-least-once)+ 幂等键去重;同 run 事件由 RunStore 分配单调序号。
- 命令与事件分离:Start/Status/Cancel 是请求;状态变化、进程观察和投递结果是已发生事实。
- AI 运行不设客户端总时长上限;静默、heartbeat 和 elapsed 不能生成失败。
- GUI 只消费 Worker 统一事件/Status,不直接 spawn 或推断 terminal(原文同时约束的飞书与 cc-connect 适配层已不存在)。
- 机密禁止进入信封、日志和 outbox。

## 2. 信封 v2

| 字段 | 类型 | 必填 | 说明 |
|---|---|---|---|
| `envelope_version` | string | 是 | 固定 `"2"`;消费者拒绝未知版本 |
| `event_id` | UUID | 是 | 本记录唯一 ID,生成后不可变 |
| `type` | string | 是 | 命令/事件类型(第 3 节) |
| `source` | string | 是 | `worker|gui|lark-cli|hook-codex|hook-claude|hook-cline`;原设计中的 `cc-connect`、`feishu` 随 ADR-0006 作废 |
| `ts` | int64 | 是 | Unix 毫秒 UTC |
| `idempotency_key` | string | 是 | 消费方去重键 |
| `payload` | object | 是 | 类型化内容,禁止机密 |
| `run_id` | UUID | 否 | 运行相关记录必填 |
| `seq` | int64 | 否 | run 内事件序号;命令可为 0 |
| `actor` | string | 否 | open_id 或本地受权身份(open_id 来自已退役的飞书入站) |
| `correlation_id` | string | 否 | attemptGroupId 等关联(原设计中的飞书 event_id 随飞书入站退役) |
| `causation_id` | string | 否 | 直接触发本记录的命令/事件 ID |
| `attempt` | int32 | 否 | 传输投递次数;不进入远端幂等键 |
| `ack` | object | 否 | `{state:pending|delivered|failed|expired,at,error_class}` |

RunContract payload 额外携带 `contract_version="1"`。

## 3. 命令与事件类型

| type | 类别 | 方向 | 语义 |
|---|---|---|---|
| `inbound.message|inbound.card|inbound.menu` | 事件 | (历史)飞书/cc-connect -> Worker | 入站事件持久接纳;ADR-0006 后没有生产者 |
| `run.start.requested` | 命令 | GUI/checker -> Worker | RunContract Start |
| `run.status.requested` | 查询命令 | GUI -> Worker | 只读 RunContract Status |
| `run.cancel.requested` | 命令 | GUI/checker -> Worker | RunContract Cancel |
| `run.state_changed` | 事件 | Worker -> 所有消费者 | 权威状态变化 |
| `run.metrics` | 事件 | Worker -> GUI | heartbeat/output/silent 指标;非 terminal |
| `run.recovered` | 事件 | Worker -> GUI/运维 | 重启后的恢复判断与动作 |
| `process.observed` | 事件 | ProcessSupervisor -> RunStore | alive/gone/unknown 观察 |
| `process.closed` | 事件 | ProcessSupervisor -> RunStore | 真实 close/error settle-once |
| `provider.health_changed` | 事件 | HealthProbe -> GUI | 真实最小请求结果 |
| `completion.event` | 事件 | 本地 hook、续跑引擎 -> Worker 投递端 | 完成通知;现役不套本信封,格式见第 16.2 节 |
| `outbox.delivery` | 事件 | Worker -> Transport | 投递 attempt 与 ACK |
| `project.catalog_changed` | 事件 | (历史)Worker -> cc-connect | 确定性项目目录版本;原用于生成 cc-connect 配置,ADR-0006 后没有消费者 |

> 2026-10-30:上表删去了原有的飞书/cc-connect 方向(Start/Status/Cancel 的飞书与 cc-connect 调用方、`run.metrics` 与 `run.recovered` 推往飞书、`provider.health_changed` 推往 cc-connect)。表中类型大多没有落地,代码实际写出的只有 `run.started`、`run.state_changed`、`run.terminal` 三种,见第 16.1 节。

旧 `task.requested/started/progress/completed/failed/cancelled/interrupted` 不进入目标 v2。兼容适配器若接收旧形状,必须在边界转换为 v2,不得让 GUI 同时理解两套状态机。

## 4. 幂等键与重放

| 类型 | 幂等键 | 消费语义 |
|---|---|---|
| InboundEvent | (历史)飞书 `event_id`;无 id 时为受控 350ms 窗口键 | 持久接纳后 ACK;同 chat 保序,跨 chat 并发;ADR-0006 后没有入站 |
| Start | `requestId` | 同摘要返回原 runId;不同摘要返回 conflict;spawn 最多一次 |
| Status | `runId + callerRequestId` | 只读,不得改变任何状态 |
| Cancel | `commandId` | 同命令只发一次终止请求,返回相同结果 |
| RunState/Metrics/Recovered | `runId + seq` | 重复丢弃;terminal 后 progress/metrics 不覆盖状态 |
| ProcessObserved | `runId + observationId` | 记录三态观察,unknown 不等于 gone |
| ProcessClosed | `runId + pid + processStartedAt` | settle-once,真实 close 才清 childPending |
| ProviderHealthChanged | `fingerprint + runId + terminalState` | 配置指纹失配立即作废 |
| CompletionEvent | 事件 ID `eventId`(即队列文件名 `<eventId>.json`,生成规则见第 16.2 节) | 同名文件已存在即不再写入;送达后记入七天已发送表;发送请求以 `eventId` 作 `--idempotency-key`(飞书服务端约一小时内去重) |
| OutboxDelivery | `outboxId` | 所有 attempt 复用同一远端幂等键 |
| ProjectCatalogChanged | `catalogVersion` | (历史)按版本确定性生成 cc-connect 配置;ADR-0006 后没有消费者 |

任何自动重放都受 `RUN-CONTRACT.md` 限制。`failed_local`、`cancelled`、`sideEffectsStarted=true` 或 `childPending=true` 禁止自动 fallback/重放。

## 5. 入站事件

> 历史:ADR-0006 之后 AI Resume 不再有任何飞书入站——cc-connect 已退役,lark-cli 只用于发送完成通知(`im +messages-send`)、诊断通道(`doctor`)与识别收件人(`auth list`),不调用 `event consume`。本节只作历史记录。

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

目标设计曾定为 `sourceClient(codex|claude-code|cline), projectPath, projectName, threadId/sessionId, finishedAt, messageUuid`,没有落地。现役完成事件不套第 2 节信封,而是 `completion-events\` 下每事件一个 JSON 文件;字段、来源、准入与事件 ID 规则以第 16.2 节为准。

现役准入:所有来源统一抑制 `AI_RESUME_INTERNAL_RUN=1` 的内部运行;Codex 只接受顶层持久 thread(拒绝子代理、internal 与 `memory_*` 线程),并抑制 `Documents\Codex\<日期>\<slug>` 下没有 Git 根的临时目录。DeepSeek Copilot Chat 不新增完成边界。

### outbox.delivery

`outboxId, runId/eventId, channel, attempt, state(pending|delivered|failed|expired), deliveredAt, remoteMessageId, errorClass`

AI run terminal 与消息投递分离。结果/进度发送失败不把 `succeeded` 改为失败。create/send 重试复用稳定幂等键;不可幂等上传不自动重放。

现役说明:SQLite `outbox` 表与 `OutboxStore` 仍在,但没有生产调用方;完成通知的投递状态记在状态根下的 `completion-notify-seen.json`、`completion-notify-state.json`、`completion-notify-health.json`,见第 16.2 节。

### project.catalog_changed

(历史)`catalogVersion, projects:[{path,name,source}], trigger(discovery|custom|hidden), changedAt`

原用于按版本确定性生成 cc-connect 配置,ADR-0006 后没有消费者;现役项目目录只在 AI Resume 内部使用(`ProjectCatalog` / `ProjectIndex`)。项目数量不是契约。

## 12. 单次请求超时

允许 request-level timeout:

- lark-cli 单次调用(现役只用于完成通知:`im +messages-send` 与 `auth list` 各 20 秒,`doctor` 30 秒;发送超时按结果不明处理,重试沿用同一 `--idempotency-key`);
- Named Pipe 建连/单帧;
- 通知队列单次投递 attempt。

这些 timeout 不得成为 AI run deadline。输入准备失败可拒绝 Start 或形成 failed_local;terminal 后投递失败留在完成通知队列按条目退避重试,不改写 terminal。

原文还列有飞书 send/reply/card patch 与飞书图片上传、资源下载和落盘阶段:它们属于 v1 Node 飞书机器人与此后由 cc-connect 承担的飞书聊天链路,AI Resume 现无对应操作。

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
- 日志、RunStore 与完成通知队列同样适用(原列出的 cc-connect 桥已退役)。现役队列只写路径、ID、状态码与结构化短值:钩子不落盘 `last_assistant_message` 与负载里的 `message` 正文(只参与事件 ID 哈希),续跑提醒不写令牌、账号或会话正文;飞书机器人凭据只经环境变量交给 lark-cli 子进程,不进 argv、日志或队列。

## 15. 版本演进

- v2 是 ADR-0002 后的目标信封;目标消费者不得接受 v1 task 语义后自行猜测状态;
- 新增可选字段可兼容;字段删除、类型/状态语义变化必须升 envelope version 并走 ADR;
- Stage 1 兼容适配器可读取现役形状,但输出给 C#/GUI 的目标边界只能是 v2;
- Stage 2-6 的 SQLite schema 与 Named Pipe 帧必须通过 `RUN-CONTRACT.md` 最小门禁(原列出的 cc-connect wrapper 已于 ADR-0003 放弃,cc-connect 本身随 ADR-0006 退役);
- 截至 2026-10-30,v2 信封没有落地:代码只有 v1 骨架 `EventEnvelopeV1`,完成通知队列使用自己的 `version: 1` 文件格式,见第 16 节。

## 16. 现役对照(2026-10-30,v2.2)

本节只记录代码里真实存在的形态;与前文目标设计冲突时,以本节和代码为准。

### 16.1 信封、事件与通道

- **RunStore `run_events`**:v1 骨架 `EventEnvelopeV1`(`AiResume.Core/Events`),camelCase JSON,信封版本 `"1"`,兼容字段 `deadlineMs` 恒为 0。`source` 固定为 `worker`,`idempotencyKey` 由 runId 与 seq 以竖线拼接,`payload` 只是状态 wire code;类型只有 `run.started`(queued→starting)、`run.state_changed`、`run.terminal` 三种。写入方是 `TaskOrchestrator` + `RunStore`;Worker 中为它注册的是 `FakeProviderAdapter` / `FakeHealthProbe`,没有生产调用方。生产续跑不写 `runs` / `run_events`,见 `RUN-CONTRACT.md` 文首。
- **Named Pipe**:`IpcEnvelope`,键为 `envelopeVersion`(只接受 `"1"`)、`type`、`correlationId`、`payload`;帧是 4 字节小端长度前缀 + UTF-8 JSON,单帧上限 1 MiB;命令 `ping`、`start`、`status`、`cancel`、`list-runs`。现役生产调用方有两个,都只用 `ping`:`InstallCommand` 启动 Worker 后的就绪核验(pong 中的 PID 必须等于本次启动的 Worker 进程),以及 GUI 判定 `arm.get.workerRunning` 时的存活核验:有同名进程才以 300 毫秒超时 ping 一次,应答为 true,不应答为 null(「未核实」;布防中引擎判定随之降为 `WorkerUnverified`);进程枚举失败同样为 null;没有同名进程为 false(「未运行」),不发 ping。
- **完成通知队列**:每事件一个 JSON 文件,不套信封,见 16.2。
- **SQLite `outbox` 表**:`OutboxStore` 按 `idempotency_key` 幂等入队与 ACK,没有生产调用方(只有测试使用)。
- 第 3 节其余类型(`run.metrics`、`run.recovered`、`process.observed`、`process.closed`、`provider.health_changed`、`outbox.delivery`)没有代码产出;`inbound.*` 与 `project.catalog_changed` 只作历史。GUI 的供应商状态来自零 token 的 `providers.catalog`,不经 `provider.health_changed`。

### 16.2 完成通知队列

- 位置:`%LOCALAPPDATA%\AI Resume\state\completion-events\<eventId>.json`(状态根可由 `AIRESUME_SHADOW_DIR` 覆盖,见 `ShadowPaths`)。
- 写入:先写同目录的 `<eventId>.tmp-<guid>`(钩子与续跑提醒会 `Flush(true)` 落盘),再 `File.Move` 到目标名且不覆盖;目标已存在即视为同一事件已入队,不重复写。UTF-8,无 BOM。
- 唯一消费者:Worker 的 `NotificationWorker`(启动后立即扫一轮,之后每 15 秒)→ `CompletionNotifier` → `LarkCliNotificationChannel`。

生产者:

| 生产者 | 入口 | `source` | `kind` |
|---|---|---|---|
| 客户端钩子 | `AiResume.Hook.exe <来源> [--kind=…]`(`AiResume.Hook/Program.cs`) | `claudecode`、`codex`、`cline`、`qoder`、`opencode` | 默认 `completion`;`--kind=decision` 为 `decision`;`--kind=failure` 为 `failure`(只用于 Claude Code `StopFailure`) |
| 续跑引擎提醒 | `ResumeNoticeWriter`(`AiResume.Worker/Resume/ResumeNotices.cs`),由 `ResumeEngine` 与 `ClaudeLoginReminder` 调用 | `ai-resume` | `resume` |
| 通道冒烟 | `NotifyControlPlane.EnqueueTest`(GUI「发送测试通知」与 CLI `notify test`) | `ai-resume` | `resume`,另带 `smoke=true` 与 `notificationType=test` |

字段:

| 字段 | 写入方 | 说明 |
|---|---|---|
| `version` | 全部 | 固定 `1`;投递端当前不校验 |
| `eventId` | 全部 | 16 位小写十六进制,与文件名一致;生成规则见下 |
| `source` | 全部 | 见上表 |
| `kind` | 全部 | `completion`、`decision`、`failure`、`resume` |
| `notificationType` | 全部 | 钩子:上游负载的 `notification_type`(Claude Code `Notification`;OpenCode 插件写 `permission_prompt`、`agent_needs_input`、`execution_failed`),没有时为 null。`ai-resume`:`session-open`、`resume-started`、`resume-succeeded`、`resume-failed`、`resume-not-started`、`credentials-unavailable`、`provider-changed`、`login-expiring`、`login-expired`;冒烟事件为 `test` |
| `cwd` | 全部 | 钩子:绝对工作目录(准入必需)。`ai-resume`:项目路径;登录到期提醒为空串,冒烟事件为 `AI Resume` |
| `atUtc` | 全部 | 入队时刻(ISO 8601 UTC);投递端据此从旧到新处理 |
| `sessionId`、`turnId`、`taskId`、`transcriptPath` | 钩子 | 上游提供时保留,否则为 null |
| `smoke` | 钩子、冒烟 | 上游负载带 `smoke=true` 或测试通知时为 true |
| `hookEvent`、`promptId`、`error`、`stopHookActive`、`backgroundBusy`、`bridged` | 仅 `claudecode` | 官方 hook 负载的结构化短值。`error` 只收不超过 64 位的字母数字、`_`、`-`,否则记 `unknown`;`backgroundBusy` 表示 `background_tasks` 里有 running / pending 的 subagent、workflow 或 cloud session(结束后会唤醒会话继续干活;shell、monitor、teammate 等常驻或内务任务不算);`bridged` 表示环境变量 `CLAUDE_CODE_BRIDGE_SESSION_ID` 有值(会话连着 Remote Control)。负载里的 `message` 正文不落盘,只取哈希参与事件 ID |
| `model`、`detail` | 仅 `ai-resume` | 续跑模型 ID;状态码或短说明(如 `resume-failed` 的运行结果码、`session-open` 的占用来源、登录到期时刻),不含令牌、账号或会话正文 |

事件 ID(决定去重与幂等):

- 钩子:把下列字段以竖线拼接,取 SHA-256 前 8 字节的小写十六进制。
  - Codex 有 thread 与 turn ID 时:`codex`、sessionId、turnId、cwd、kind——旧 `notify` 与 hooks `Stop` 描述同一回合时得到同一 ID;
  - OpenCode 带上游 `event_id` 时:`opencode`、event_id、kind;
  - Claude Code:source、sessionId、turnId、taskId、cwd、transcript 文件修改时间、显式 event_id、timestamp、last_assistant_message、kind,再加 hookEvent、promptId、notificationType、error 与 `message` 哈希;
  - 其它来源:同上去掉最后五项。
- 续跑提醒:`ai-resume`、type、dedupeKey 以竖线拼接,取 SHA-256 十六进制的前 16 位。dedupeKey 由 `ResumeEngine` / `ClaudeLoginReminder` 按布防周期、项目、中断或运行 ID、登录到期时刻与阈值组成,同一件事无论写几次、Worker 重启几次都得到同一 ID。
- 冒烟事件:随机 ID。

钩子准入(被拒即不入队;`AiResume.Hook.exe` 始终以 0 退出,不阻断宿主客户端):

- 环境 `AI_RESUME_INTERNAL_RUN=1`(AI Resume 自己启动的进程)、负载为空或不是 JSON 对象,以及非 `claudecode` 来源的 `stop_hook_active=true`(Claude Code 只记录该标志,由投递端按会话合并);
- 事件名不符:`claudecode` 完成只认 `Stop`,`--kind=failure` 只认 `StopFailure`,`--kind=decision` 只认 `Notification`;`qoder` 只认 `Stop`;`cline` 只认 `TaskComplete`(负载缺事件名时放行);`codex` 只认旧 `notify` 的 `type=agent-turn-complete` 或 hooks `Stop`;`opencode` 完成只认 `session.execution.succeeded`(2.x)与 `session.idle`(1.x 旧插件兼容),`--kind=decision` 只认 `permission.asked`、`form.created`、`session.execution.failed`;
- `cwd` 缺失或不是绝对路径;Codex 另需 thread 与 turn ID,并按 rollout 核验归属(见第 11 节)。

投递端语义(调度细节见 `COMPLETION-NOTIFICATIONS.md`):

- 根节点不是对象、字段类型不对、缺 `eventId`(或非 `ai-resume` 来源缺 `cwd`)的文件移入 `completion-events\malformed\`;
- 按 `atUtc` 从旧到新处理(缺时间排最后,同刻按文件名);七天已发送表 `completion-notify-seen.json` 命中则直接删除文件;
- 来源不在通知意图 `ProductConfig.NotifySources` 里的直接删除不发(`ai-resume` 总是放行;`NotifyIntentRecorded=false` 的旧空配置不过滤,记录过的空意图全部丢弃);`atUtc` 早于 24 小时前的不再补发,移入 `dead\`(`stale`);
- Claude Code 事件(非冒烟)带 `transcriptPath` 而该转录文件在稳定期(20 秒)后仍不存在 → 会话没落盘,删除不发(`session_not_persisted`;路径形状陌生、目录读不了或没带路径时照发);
- 用量上限中断(`kind=failure` 且 `error=rate_limit`,或 `kind=decision` 且 `notificationType=quota_auto_resume_disabled`)满稳定期后按转录尾部 `quotaLimits.rateLimitType + resetsAt` 加配置根定限额周期(事件晚于重置时间时不采信;读不到按会话):`completion-notify-limits.json` 记着这个会话这一周期已提醒过 → 删除不发(`same_limit_cycle`);同周期同时待发的中断合成一条,以最早一条的 `eventId` 作幂等键发送,送达后整组记已送达;
- Claude Code 完成类事件(`source=claudecode`,`kind` 为空或 `completion`,非冒烟且有 `sessionId`)入队未满 20 秒不发;同一会话 2 分钟内出现更新的完成时旧条目被替代;`backgroundBusy=true` 时最多暂缓 30 分钟,同会话任何更晚的完成(队列中,或 `completion-notify-sessions.json` 记录的已送出)都让它作废;
- 未设收件人(`ProductConfig.NotifyRecipientOpenId`)时保留文件不发;
- 发送:`lark-cli im +messages-send --as bot --user-id <收件人> --text <文案> --idempotency-key <eventId> --format json`,成功后记入已发送表并删除文件;机器人凭据优先取 AI Resume 的机器范围副本、经环境变量交给 lark-cli,没有副本时退回 lark-cli 自己的钥匙串(S4U Worker 经该副本的真实投递待真机验证);
- 失败:条目级只推迟该条(30 秒起指数退避,上限 15 分钟);通道级故障(凭据、网络、频控等)熔断整个通道,凭据文件、收件人或 lark-cli 位置一变即解除;参数类永久错误、首次尝试后超过 24 小时仍未送达、或首次结果不明后超过 55 分钟(飞书 uuid 只去重一小时)的条目移入 `completion-events\dead\`,并写同名 `.error.txt`;退出码 0 而输出不是信封按已送达;
- 状态文件与队列同在状态根:`completion-notify-seen.json`(已送达,保留七天)、`completion-notify-state.json`(条目退避)、`completion-notify-sessions.json`(每个 Claude Code 会话最近已送出的完成,保留两天)、`completion-notify-limits.json`(已提醒过的限额周期,重置后 1 小时或 5 小时失效)、`completion-notify-health.json`(通道健康,Worker 单写、GUI 只读)。
