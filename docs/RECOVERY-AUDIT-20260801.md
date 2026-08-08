# AI Resume 迁移恢复审计(2026-08-01)

> 状态:恢复基线已确认。本文记录双工作区、安装目录和运行进程的只读证据,以及恢复后的执行顺序。第 1-7 节的取证过程中未读取 `config.json`、密钥、令牌或订阅信息,也未部署、重启、提交、推送或修改生产配置。**第 8 节记录 2026-08-04 经用户授权执行的提交与部署方案 a**,该节之后生产运行真身已变更;第 2-4 节的 PID、哈希与"未提交"描述只代表 2026-08-01 取证时点,不得当作现状。

## 1. 权威边界

| 角色 | 路径 / 标识 | 约束 |
|---|---|---|
| 唯一写入工作区 | `C:\Users\<you>\Desktop\AI Resume` | 分支 `migration-recovery-20260801`;后续源码、测试和文档只在此修改 |
| 取证工作区 | `C:\Users\<you>\Desktop\claude-resume` | `main`;保留失控执行现场,只读,禁止清理或同步回写 |
| 生产运行目录 | `%LOCALAPPDATA%\ClaudeResume` | 只读取证;未经授权不得部署、重启或改配置 |
| Git 基线 | `6826704` | 两个工作区从同一提交分叉;未提交增量不能按目录整体复制 |
| 远端 | `origin` = 个人 GitHub 仓库 | 本轮不 commit/push |

## 2. 仓库层证据

### 2.1 migration 工作区

- 当前分支:`migration-recovery-20260801`,HEAD `6826704`。
- Stage 0 文档与 S1-A/S1-B/S1-C/S1-D/S1-E/S1-F/S1-G、D-001/D-002/D-003 Stage 1 稳定化均为未提交增量;当前还包含 ADR-0002、RunContract 和安装边界修正。
- `src/authorization-policy.js` 是 S1-A `AuthorizationPolicy` 纯决策边界。
- `src/completion-events.js` 是 S1-B `CompletionAdmission`/完成队列边界。
- `src/conversation-store.js` 是 S1-D `ConversationStore` 边界,单一持有 user-chat/session/chat-query scratch 状态与 Claude scratch 清理;未知旧项目继续按 basename fallback。
- `src/ai/agent-adapter.js` 是 S1-E `AgentAdapter` 单 attempt 边界,精确透传 run/resume/cancel/waitForIdle 并输出脱敏 starting/running/terminal 观察事件;不拥有 deadline、fallback 或 retry。
- `src/task-orchestrator.js` 是 S1-F `TaskOrchestrator` 现役兼容边界,负责同步 runKey 预占、provider 候选/fallback、健康预检取消、活动 child/preflight 停止决策和 legacy deadline 透传;不拥有飞书 SDK、进程登记或目标持久化 Start/Status/Cancel 状态机。
- `src/channel-adapter.js` 是 S1-G `ChannelAdapter` 飞书传输边界,集中 SDK 创建/启动、单次 API timeout/现役一次网络重试、目标映射、消息/卡片/图片/资源调用与事件 ACK/排队;不拥有权限、会话、卡片可见性或任务编排。
- `src/feishu-agent.js` 已收口为稳定 wrapper,只加载并导出 `src/feishu-runtime.js`;runtime 是移动前入口逐字节复制得到的 Stage 1 legacy compatibility shell,继续装配六个边界和现役启动生命周期,不是目标架构的新边界。D-001/D-002/D-003 加固均保留在 runtime 中。上述增量已于 2026-08-04 提交并部署,取证见第 8 节。

### 2.2 取证工作区

- 当前分支:`main`,HEAD 同为 `6826704`。
- 存在远多于 migration 的源码、测试和文档改动,包括 `conversation-store`、测试隔离 helper、provider/卡片/并发/图片等交叉修改。
- 这些文件形成失控执行证据,不能整体复制到 migration;后续只能按工作包重新设计、审查和验证。
- 恢复审计没有修改该目录。

## 3. 安装目录层证据

以下比较均对文本换行归一化后完成,避免 CRLF/LF 造成假差异:

| AppDir 文件 | 与 migration 当前文件 | 与取证当前文件 | 结论 |
|---|---:|---:|---|
| `feishu-agent.js` | 不同 | 相同 | 磁盘是失控工作区版本,不是恢复分支版本 |
| `authorization-policy.js` | 不同 | 相同 | 磁盘已有较早/不同的 S1-A 版本 |
| `completion-events.js` | 不同 | 相同 | 磁盘已有较早/不同的 S1-B 版本 |
| `ai/runners.js` | 不同 | 相同 | 磁盘是失控工作区版本 |
| `session-manager.js` | 相同 | 相同 | 两工作区与安装目录在此文件一致 |
| `lib.ps1` | 不同 | 不同 | 安装目录为第三种版本,必须保留现场,禁止猜测覆盖 |
| `install.ps1` | 不同 | 不同 | 安装目录仍等于 Git HEAD 基线;两个工作区各有未提交增量 |
| `deploy-files.ps1` | 不同 | 不同 | 安装目录仍等于 Git HEAD 基线;两个工作区各有未提交增量 |

`feishu-agent.js` 和 `ai/runners.js` 的 AppDir 修改时间晚于当前 Node agent 的启动时间。因此当前进程不可能加载这些后写入的文件内容;磁盘状态与内存状态分裂。非侵入式只读取证不能逐字证明进程内已加载模块,故以启动时间和文件时间作为可验证边界,不对内存内容作更多猜测。

## 4. 运行进程层证据

2026-08-01 恢复复核:

- 生产 Feishu agent 恰好 1 个:`node ...\ClaudeResume\feishu-agent.js`,PID `26988`,启动时间 `20:08:25.398 +08:00`。
- 该 PID 当前无子进程;没有正在由它拥有的 AI child 可见。
- `ClaudeResumeChecker` 为 `Ready`,执行时限 `PT0S`,最近一次任务结果为 0。
- `cc-connect` 进程数为 0;生产飞书应用仍只有 Node agent 消费。
- 本轮未终止或重启任何进程。PID 和状态只代表本次审计时点,后续操作前必须重新取证。

## 5. S1-A / S1-B 独立审查

### 5.1 结论

- S1-A 权限工厂的输入验证、纯决策入口、owner/viewer/allowlist、缺身份和 malformed 配置 fail-closed、owner-only profile 约束通过 Codex 独立审查。
- S1-B 完成事件的准入、Codex 顶层持久会话边界、内部任务抑制、项目解析、稳定 UUID、claim/恢复/去重和 hook 链通过 Codex 独立审查。
- 没有再次调用 DeepSeek 修改 S1-A/S1-B。
- 初审发现 P1 集成缺口:`feishu-agent.js` 已 `require` 两个新模块,旧安装清单却未部署它们;干净安装会启动失败,增量安装可能继续使用陈旧 AppDir 副本。

### 5.2 已在 migration 修正,未部署

- `New-CcuDeploymentPlan` 集中定义必需运行文件,包含 `authorization-policy.js`、`completion-events.js` 和三个必需 `ai/` 模块。
- 顶层文件、`ai` 目录或必需 AI 模块缺失/类型错误时 fail-fast;事务部署不再静默跳过缺失 source。
- `test/install-deploy.ps1` 自动核对 `feishu-agent.js` 顶层本地依赖均在部署计划中,并覆盖缺失模块、锁定跳过、失败回滚和解锁后部署。
- 这些修正只在 migration 工作区,没有复制到 AppDir,生产内存/磁盘均未改变。

### 5.3 已验证

- PowerShell 解析:`src/deploy-files.ps1`、`src/install.ps1`、`test/install-deploy.ps1` 通过。
- `powershell -NoProfile -ExecutionPolicy Bypass -File test/install-deploy.ps1` 通过。
- `node --check`:`authorization-policy.js`、`completion-events.js`、`feishu-agent.js` 通过。
- `test/authorization-policy.js`、`test/menu-authorization.js`、`test/completion-events.js`、`test/completion-hooks.js` 通过。
- `git diff --check` 通过,仅有工作区 LF/CRLF 提示。

### 5.4 尚未关闭的范围

- S1-A/S1-B/S1-C/S1-D/S1-E/S1-F/S1-G 代码与安装边界、D-001/D-002/D-003 Stage 1 稳定化及 D-006 入口收口已通过针对性门禁;Stage 1 总门禁尚未完成。
- D-004 已关闭:所有加载 `feishu-agent.js` 的 Node 测试均经显式临时 config/state/AppDir/Claude/Codex home 启动;旧的真实 config 备份/写回模式扫描为零。`config-isolation`、S1-C2/S1-C3 离线回归、`query-security` 和 `chat-security` 真 AI canary 均通过,生产 config 前后 SHA256 一致。
- S1-D Codex 独立验证通过:`conversation-store`、安装清单、routing/card-flow/session-pick、config-isolation、progress-image/concurrency/image-send 及 query/chat 真 AI canary 全部通过;生产配置哈希不变。一次外层工具默认时限使首个 query-security 宿主输出丢失,AI 子进程仍自然结束;其测试根随后缺 owner marker,按 fail-closed 规则保留且未递归删除,作为 D-005 的新增恢复证据,不计入有效测试结果。
- S1-E DeepSeek 主实现自然完成后由 Codex 独立审查并修正三处观察边界:Codex 无 sessionId 必须标记为 new、生命周期事件各自取时间、onEvent 与日志回调同时异常不得打断 attempt。`agent-adapter`、语法、`ai-providers`、`install-deploy`、`routing`、`concurrency`、`config-isolation` 全部通过;未运行真实 provider 或生产飞书测试。
- S1-F DeepSeek 主实现自然完成后由 Codex 独立审查;Codex 修正健康预检 Promise 拒绝、健康快照/后续同步异常时的 preflight token 泄漏,并补充异常清理回归。`task-orchestrator` 57 项、语法、`ai-providers`、`routing`、`concurrency`、`session-pick`、`card-flow`、`install-deploy`、`config-isolation` 全部通过;`config-isolation` 无跳过且真实配置 SHA 不变。生产 agent 仍为 PID 26988、启动时间 2026-08-01 20:08:25,未部署或重启。
- S1-G DeepSeek 主实现自然完成后由 Codex 独立审查;Codex 发现并修正 `dispatchEvent` 返回后台 Promise 可能导致 SDK 等待业务处理后才 ACK 的回归,补充返回值断言。`channel-adapter` 25 项、语法、`card-flow`、`session-pick`、`image-send`、`progress-image`、`routing`、`concurrency`、`ai-providers`、`install-deploy`、`config-isolation` 全部通过;真实配置 SHA 不变。生产 agent 仍为 PID 26988、启动时间 2026-08-01 20:08:25,未部署或重启。
- D-001 DeepSeek 主实现完成三态进程核验、orphan cancel 和退出保护后,Codex 独立审查发现非法 PID 登记会在读取/回收前被丢弃、回收写盘失败仍会更新内存锁;唯一修正补齐后,Codex 再发现 registry 删除失败未被报告、无合法 PID 的登记会被后续正常持久化覆盖,并直接收口。最终 `ai-providers` 覆盖 matched/mismatched/unverifiable、PID/父 PID/启动时间/命令签名、CIM/kill 二次核验、非法元数据、写盘失败、后续持久化、stop/exit fail-closed;`task-orchestrator`、`config-isolation`、`routing`、`concurrency`、语法和 `git diff --check` 全部通过,真实配置 SHA 在隔离测试前后不变。生产 agent 仍为 PID 26988、启动时间 2026-08-01 20:08:25,未部署或重启。
- D-002 DeepSeek 主实现把 Claude/DeepSeek stream-json 与 Codex JSONL activity 分类抽成纯 helper,malformed JSON、未知/非法 tool/content/item 均 fail-closed,并补充 retryable 429 录制与 TaskOrchestrator 不 fallback 回归。Codex 独立审查发现 `redacted_thinking` 字段误用和无证据放行 `user_message` 两项问题,唯一修正改为验证 `data` 字段并恢复未知 item fail-closed。Codex 随后独立运行 `ai-providers`、`task-orchestrator`、`agent-adapter`、语法和 `git diff --check` 全部通过。DeepSeek 主实现 runId `20260801-173751-7ec83e5c`,修正 runId `20260801-174745-4e955675`;两次均自然终态成功,未按静默取消。生产 agent 仍为 PID 26988、启动时间 2026-08-01 20:08:25,未部署或重启。
- D-003 DeepSeek 主实现 runId `20260801-175936-caec4d1c` 完成 malformed/missing owner 配置下的项目披露门禁;Codex 独立审查发现 `discoverProjects()` 与 `ConversationStore.activeProject()` 仍可绕过入口配置快照。唯一修正 runId `20260801-181939-0bbeefc7` 自然终态成功,将显式 cfg 与已发现项目列表贯穿消息、卡片、菜单、会话选择和项目解析路径,并按 `hiddenProjects/customProjects` 指纹隔离 3 秒缓存。Codex 随后发现 owner chat 绑定仍可能在同一事件内由 `none` 随磁盘配置升级为 owner,直接增加入口快照预检并保留锁内最新配置复核。最终语法、`menu-authorization`、`conversation-store`、`authorization-policy`、`routing`、`session-pick`、`card-flow`、`config-isolation`、`ai-providers`、`concurrency`、`agent-adapter`、`task-orchestrator` 和 `git diff --check` 全部通过;真实配置 SHA 不变。生产 agent 仍为 PID 26988、启动时间 2026-08-01 20:08:25,未部署或重启。
- D-006 DeepSeek 主实现 runId `20260801-184425-beabadff` 自然终态成功。worker 先在移动前用完整 `feishu-agent.js` 显式 `--record` 生成 10 场景固定 fixture,记录帮助/菜单、查询、修改、模型切换、停止、完成通知、拒绝和 ACK/同 chat 保序;随后将入口逐字节复制为 `feishu-runtime.js`(当时 SHA256 `D2F7E63C1557FEA51C1CCF5ADC8620D6B29892C7B6B39DE1E80A97E3A6C0960D`),再把 `feishu-agent.js` 收口为稳定 wrapper。Codex 独立审查并重跑语法、录制等价、六边界、权限/路由/卡片/会话/并发/图片/provider/config 隔离和 `install-deploy` 全部通过;fixture 默认只读,假 session + `FEISHU_TEST_NO_AI=1` 未启动真实 AI,安装计划缺 runtime 时 fail-fast。生产 agent 仍为 PID 26988、启动时间 2026-08-01 20:08:25;生产 config SHA256 为 `C56FE658EFF4959BD01ECA34087FA256106C446F029474BF868C27F96F93EADC`,未部署或重启。
- Stage 1 两名独立只读总审查及两轮修复复核已完成。Codex 逐条复核并直接加固 accepted-before-spawn reservation 取消、shutdown 锁保留、child registry 检查错误/损坏锁存/backup-generation 前沿/写回指纹、部署 taskkill/CIM/唯一新 agent/安装 generation READY、SDK `1.70.0`/`onReady` 契约、`aiProxy` 测试配置脱敏、项目路径与 `AI_GUIDE.md` no-reparse/句柄读取、显式项目快照未命中拒绝、malformed completion 继续处理、同 eventId 跨进程单次发送及 generation seen 恢复,同时冻结 D-006 fixture 来源 SHA/生成时间。所有修正均由 Codex 实施,未再次调用 DeepSeek。
- Stage 1 已完成 22 项完整离线回归、一次 `provider-live`、`query-security`、`chat-security` 和生产三层只读取证。生产 agent 仍为唯一 PID 26988、启动于 2026-08-01 20:08:25;生产 config SHA256 仍为 `C56FE658EFF4959BD01ECA34087FA256106C446F029474BF868C27F96F93EADC`,安装目录关键文件哈希/时间未变。当前仅剩两名原审查代理对第二轮加固的最终复核;没有连接生产飞书应用、修改真实项目、部署或重启生产进程。
- 2026-08-04 最终复核完成:原审查代理上下文已不可用,改由两名新的相互独立只读代理分别从正确性/并发/进程边界与安全/部署/回归视角复核第二轮加固,均同意关闭 Stage 1 总门禁。审查 A 确认一处 P2:`writeChildRegistry` 失败路径不清理自建临时文件,ENOSPC 等写失败残留的更新损坏 tmp 会把有效主登记按 generation 前沿判为整体损坏并永久锁存 AI 启动;当日以 catch 内 `rmSync(tmp)` 清理 + `after-tmp` 窄注入回归修复。硬杀窗口残余风险与其余 P2/P3 观察(写回指纹失配无用例、run 中 reservation 取消无用例、dynamic.runKey 潜在漂移、键名级脱敏)登记为 `MIGRATION-DEBT.md` D-009~D-012。全套离线回归在修复前后各完整通过一次。

## 6. 恢复后的详细执行计划

### 6.1 Stage 1 剩余工作包顺序

1. **S1-C 测试状态隔离 / D-004（已完成）**:所有 Feishu Node 测试使用显式临时 config/state/AppDir/Claude/Codex home,生产配置前后 SHA256 不变,不采用备份后写回;真 AI 安全 canary 使用合成项目并按 15 秒状态轮询等待终态。
2. **S1-D ConversationStore（已完成）**:从现役入口抽取聊天、项目、query/work session 映射和生命周期;只重新实现通过门禁的最小边界,不复制取证 helper。
3. **S1-E AgentAdapter（已完成）**:抽取 provider run/resume/cancel/progress/terminal 与现役 CLI/session 兼容层;不固化目标 RunContract 已拒绝的 deadline/fallback 语义。
4. **S1-F TaskOrchestrator（已完成）**:抽取 runKey、并发、stop、settle-once 和现役兼容 fallback;接口边界不把 legacy deadline 固化为目标 RunContract,Stage 1 不改变生产用户行为。
5. **S1-G ChannelAdapter（已完成）**:抽取 Feishu ACK、文本/卡片/图片意图;业务层不再直接依赖 SDK。
6. **S1-H 绑定债务（已完成 Stage 1 稳定化）**:D-001 进程三态、D-002 malformed activity fail-closed、D-003 配置快照与项目披露门禁均已通过。D-001 的最终关闭仍在 Stage 5 durable registry + Job Object 门禁,D-002/D-003 仍需 Stage 4/6 目标链路复验。
7. **D-006 入口收口（已完成）**:`feishu-agent.js` 为稳定 wrapper,`feishu-runtime.js` 为机械迁移兼容壳;移动前录制和移动后回放已证明 ACK、消息/卡片意图、状态变更、provider attempt 与结果等价。
8. **Stage 1 总门禁（最终复核）**:22 项完整离线回归、一次 provider live smoke、query/chat security、生产三层只读取证和两轮双代理审查修正均已完成;仅等待原两名审查代理对最新 registry/SDK/install-generation 加固给出最终结论。

每个 S1 工作包最多一次 DeepSeek 主实现和一次修正。S1-A/S1-B 已禁止再次交给 DeepSeek。未来 DeepSeek 统一按全局 `deepseek-developer` 的 Start/Status/Cancel 异步协议执行,15-30 秒读取状态与进程存活性;静默或本地固定总时长不得 Cancel。

### 6.2 Stage 2-11 门禁

| 阶段 | 进入条件 | 关键交付 | 出口证据 |
|---|---|---|---|
| 2 C# 基础设施 | Stage 1 总门禁通过;固定 .NET 10 SDK | Worker/WPF/Named Pipe/SQLite/WAL/日志/机密 shadow;六组件实现 RunContract | build、锁竞争、幂等、崩溃恢复、取消/side effect/fallback 契约 |
| 3 lark-cli 试点 | 独立测试应用和 bot/user 授权可用 | envelope/catalog/NDJSON/binary、scope/risk/exit 10/请求级 timeout | 只读消息/文档/日历及错误/脱敏,不接生产应用 |
| 4 cc-connect 试点 | 独立测试应用+测试项目;Stage 2 Worker 可监督 | 实际 1.4.1 wrapper,新建/resume/stop/progress/image/recovery | 多用户/项目/provider 隔离;wrapper 不足先新 ADR |
| 5 状态 shadow | Stage 2/4 契约稳定 | 项目、健康、额度、周期、RunStore/进程登记 shadow | 15-30 秒观察、静默不失败、PID 复用/断电/CIM fail-closed |
| 6 会话与任务 | 测试应用唯一由 cc-connect 消费 | chat/query/modify/session 迁移 | 重复/乱序/旧卡/停止竞态/权限等价 |
| 7 GUI | Worker 状态/命令稳定 | C# WPF 核心工作流 | DPI、不可用模型、会话、日志;旧 GUI 不双写 |
| 8 Hook/部署 | C# 安装边界稳定 | C# 单文件 hook、completion outbox、原子升级卸载 | 三客户端边界零误报;旧 hook 可恢复 |
| 9 数据演练 | schema 冻结 | JSON/marker/session 幂等迁入 SQLite | 数量/哈希/ID/权限/周期对账;原文件只备份 |
| 10 生产切换 | Stage 3-9 全门禁、P0/P1 清零、用户授权维护窗口 | 先停 Node,再启 C# + cc-connect,保持唯一消费者 | 生产冒烟;P0/P1 失败立即回滚 |
| 11 收尾 | 生产观察稳定 | 删除失责旧模块、残留和过期入口 | 完整回归、秘密/进程/任务/启动项/文档审计;备份保留一版本周期 |

## 7. 当前停止线与外部依赖

- 未经明确授权,不得 commit、push、deploy、重启生产进程、修改生产配置或切换生产消费者。
- Stage 3/4 缺少独立测试应用/身份授权时必须停在对应入口,不得借生产应用试验。
- Stage 10 必须另行确认维护窗口和回滚授权;当前目标不包含生产切换。
- 任何方案若改变 cc-connect 目标、引入双写、转为 Go 私有 fork 或整体回退,必须先写新 ADR 并等待用户确认。
- 取证工作区与 AppDir 的现有差异全部保留;恢复分支只按门禁前进,不做“同步现场”操作。

## 8. Stage 1 提交与部署取证(2026-08-04)

用户在 2026-08-04 单独授权两项动作:按工作包分批 commit(不 push)、以门禁版本重跑 `src/install.ps1` 覆盖 AppDir(部署方案 a)。第 7 节其余停止线继续有效,**push 仍未授权**。

### 8.1 最终复核

- 两名新的相互独立只读代理分别从「正确性/并发/进程边界」与「安全/部署/回归」视角复核第二轮加固,均同意关闭 Stage 1 总门禁。
- 唯一新增缺陷为 P2:`writeChildRegistry` 失败路径不清理自建 tmp,残留的损坏 tmp 会把完好主登记按 generation 前沿判为整体损坏并永久锁存 AI 启动。当日以 `catch` 内 `rmSync(tmp)` 修复(`src/feishu-runtime.js`),并补 `after-tmp` 窄注入回归。
- 其余 P2/P3 观察登记为 `MIGRATION-DEBT.md` D-009~D-012,不阻断 Stage 1。

### 8.2 提交

- 分支 `migration-recovery-20260801`,13 个工作包提交 `0492200`..`39d6aee`,顺序为 docs 基线 → 测试隔离 → S1-A/B/D/E/F/G → D-001/D-002 → D-006 入口收口 → 既有回归适配 → 安装链 → 规则文档同步。
- 未 push;`origin/main` 仍停在 `6826704`。取证工作区 `claude-resume` 保持 `main` HEAD `6826704`、工作树干净、guard stash `claude-resume-guard 20260804-105013` 完好。

### 8.3 部署与验证

| 项目 | 值 |
|---|---|
| 旧生产 agent | PID 26988,启动于 2026-08-01 20:08:25(部署前取证) |
| 新生产 agent | PID 34600,启动于 2026-08-04 11:30:27 |
| 安装 generation | `111b9a28e3b94d0387be6a006bb424c0`,`AI_RESUME_AGENT_BOOT` 与 `AI_RESUME_AGENT_READY` 同代次(11:30:27 / 11:30:28) |
| 进程唯一性 | CIM 复核 `node.exe` 恰有一个,命令行指向 AppDir `feishu-agent.js` |
| 文件一致性 | AppDir 的 14 个顶层运行文件与 `src/ai/` 4 个模块逐个 SHA256 等于迁移仓提交版本 |
| 启动后行为 | 11:30:35 三家 provider 实探完成(DeepSeek/Claude available,OpenAI rate_limit);11:46:55 完成通知正常投递 |

### 8.4 部署后复验(2026-08-04 16:xx,本次会话独立执行)

- 21 项离线回归(20 个 Node + `install-deploy.ps1`)在已提交树上全部通过,`TOTAL_FAILED=0`。
- 生产 `config.json` 在回归前后 SHA256 均为 `20CB0F3A…987C`,证明测试隔离未触碰生产配置。
- 生产 config 相对部署前的 `C56FE658…EADC` 已变化,写入时间 11:30:29,落在部署窗口内;顶层键集合、8 个 `feishuAuthOpenIds` 与三类凭据均完好。安装器本身不写 config,写入方为运行期进程(续跑引擎周期字段或 agent 自身),因缺少部署前逐字段快照,未做逐字段 diff。
