# 上游架构研究：cc-connect 与飞书官方 lark-cli

> 本文前半保留 2026-07-31 研究快照；文末 2026-08-01 的 Stage 0-11 计划是当前执行真身。方向性决策以 `adr/0001-target-architecture.md` 为准,AI 运行生命周期以 `adr/0002-run-lifecycle-contract.md` 与 `RUN-CONTRACT.md` 为准。

研究日期：2026-07-31

固定快照：

- [chenhg5/cc-connect](https://github.com/chenhg5/cc-connect) 研究快照 `12a589fcaae28bf5b05d960e03862f61bebf2e95`
- [larksuite/cli](https://github.com/larksuite/cli) `003d0f42f84d3799c62f2a666fb0ddf4084283c7`

## 结论

AI Resume 不适合继续把所有飞书事件、权限、卡片、会话、Agent CLI 参数、运行锁和完成通知堆在一个进程模块里；但也不应直接整体替换成任一上游项目。

用户已于 2026-07-31 确认：实际采用这两个上游组件，而不是只借鉴其设计。当前机器已安装 `cc-connect 1.4.1`（二进制报告 commit `5d4c96dd`）、`lark-cli 1.0.81` 和 27 个官方 `lark-*` Skills。cc-connect daemon 尚未安装/启动，lark-cli 尚未写入应用凭据或用户授权，因此生产飞书仍由现役 Node agent 唯一消费。研究快照与已安装 cc-connect 二进制不是同一提交，阶段 3 必须以实际二进制重新验证，不能把源码快照结论直接当运行时保证。

推荐采用渐进式混合架构：

1. 保留 AI Resume 的 Windows GUI、项目发现、Claude 限额后自动续跑、原生会话管理和 Codex/Claude Code/Cline 本地完成通知。
2. 将飞书渠道、任务/会话编排和 Agent 执行器拆成明确接口；优先验证以 cc-connect sidecar 承担飞书长连接与 Agent 会话编排的可行性。
3. 将 lark-cli 作为 AI 可调用的飞书能力层，而不是机器人运行时。它负责消息、文档、日历、任务等 OpenAPI 操作和对应 Skills；入站事件路由、项目状态和本地 AI 生命周期仍由编排层负责。
4. 在完成兼容性试验前，不移除现有运行链路，不同时维护两套可写会话真身。

## cc-connect

cc-connect 是 Go 实现的多 Agent、多消息平台桥接器。当前源码支持 Claude Code、Codex、Cursor、Gemini、OpenCode、Copilot CLI 等 10+ Agent，以及飞书、钉钉、Slack、Telegram、Discord、企业微信等平台。它不是简单的 webhook 转发器，而是包含持久会话、多工作区、权限命令、定时任务、管理 API 和 Web UI 的完整编排内核。

### 值得直接借鉴的边界

- `core.Platform` 只负责消息平台启动、回复、主动发送和停止。
- `core.Agent` / `core.AgentSession` 只负责创建或恢复原生 Agent 会话、发送 turn、取消和关闭。
- `session_key` 使用 `platform:chat_id:user_id` 形成稳定路由键；项目、消息平台会话和 Agent 原生 session id 分层保存。
- Codex 适配器把首次 `codex exec` 与后续 `codex exec resume` 放在同一会话对象中，并有专门的参数回归测试。它同样要求 resume 带 `--skip-git-repo-check`，且针对 Codex CLI 不同子命令的参数能力分别编码。
- 平台能力通过可选接口扩展，例如 typing、done reaction、reply context 重建、消息撤回检测，而不是让核心引擎判断具体平台类型。
- 飞书支持 `done_emoji`。这说明“卡片原地更新不触发推送，需要独立完成提醒”是渠道语义，不应混入 Agent 成功判定。
- 管理 API 明确定义项目、会话、provider、定时任务和 bridge adapter，适合被现有 GUI 作为 sidecar 控制面调用。

### 不能直接替代的部分

- cc-connect 没有 AI Resume 的 Claude 配额检测和限额后自动续跑状态机。
- 没有现有 GUI 的八项目动态发现、布防/预演和 Windows 安装升级语义。
- 没有 Cline VS Code 扩展的可靠 TaskComplete 边界；仓库中的 Copilot 是 Copilot CLI，不等同于 `DeepSeek V4 for Copilot Chat` 扩展。
- 直接迁移会引入 Go sidecar、第二份配置和会话数据迁移问题。若同时让旧 Node agent 与 cc-connect 接收飞书事件，会产生重复回复和双写会话。
- cc-connect 自身也有较大的核心文件，不能把“采用上游”误解成“上游没有复杂度”。它的价值主要在已经稳定下来的接口、测试矩阵和运维契约。

## 飞书官方 lark-cli

lark-cli 是 larksuite 团队维护的官方 CLI。README 当前声明 18 个业务域、200+ 命令和 26 个 Agent Skills；本次固定快照的 `skills/` 代码树实际包含 27 个顶层 `SKILL.md`，说明该仓库仍在快速更新。当前机器已安装 1.0.81,但尚未配置应用或用户授权。

### 它解决的问题

- 将飞书 OpenAPI 统一成可发现的 CLI 命令和结构化 JSON 成功/错误信封。
- 明确区分 `--as user` 与 `--as bot`，并把身份、scope 缺失、授权链接和恢复提示写入机器可读错误。
- 对命令标注 `read`、`write`、`high-risk-write`；高风险写操作没有显式确认时以退出码 10 阻断。
- 通过 `lark-cli skills list/read` 从二进制读取同版本嵌入的 Skill 内容，避免 CLI 与 Skill 文档版本漂移。
- `lark-event` 为事件消费定义 ready marker、NDJSON stdout、结构化 stderr、退出码和优雅停止协议；这类明确的 subprocess contract 很适合 Agent 编排器。
- `lark-im` 覆盖消息、话题、资源、卡片、回调、reaction 和幂等发送，可逐步替代项目中手写的通用飞书 OpenAPI 封装。

### 它不解决的问题

- lark-cli 是飞书能力层，不负责选择项目、维护 Codex/Claude 会话、执行本地编码任务或判断一次开发任务何时完成。
- Skills 是教 AI 正确调用 CLI 的知识包，不是后台任务调度器，也不会自动接管当前机器的飞书机器人。
- 官方安全说明建议把具有用户权限的机器人作为私人助手使用，并避免暴露给不可信群成员。AI Resume 仍必须保留 open_id 校验、owner/viewer 边界和最小权限策略。

## 当前项目为何容易出现零散缺陷

问题不是某一个 API 写错，而是几个生命周期目前集中在同一模块中：

- `src/feishu-agent.js` 约 205 KB，同时处理长连接、鉴权、项目发现、会话、卡片、图片、运行锁、provider 健康、任务执行和完成通知投递。
- Agent CLI 是外部、版本化的协议，但首次运行与 resume 曾分别拼参数，没有共享不变量；这次 Codex 非 Git cwd 缺陷就是直接结果。
- 完成 hook 的回调语义与“用户顶层任务完成”不是同一件事；此前只有 event id 去重，没有先定义事件准入，因此 projectless 和 ephemeral turn 会被当成项目任务。
- 运行源码与 `%LOCALAPPDATA%\ClaudeResume` 部署副本并存，若修改后未精确安装和重启，代码、日志和用户看到的行为可能属于不同版本。
- 测试覆盖较多局部规则，但历史上缺少“首次请求 -> resume -> 重启/通知”的跨边界真实回归。

这次修复已经先补两条不变量：Codex 新建/续接共用参数构建器；完成事件必须先满足顶层持久会话和有效项目目录准入，再进入队列与去重。后续架构调整应继续围绕可验证的接口和状态所有权，而不是继续在入口文件追加分支。

## 推荐实施顺序

### 阶段 0：当前修复

保留现有产品行为，修复 Codex resume 参数漂移和 Codex 完成通知误报，并部署回归。

### 阶段 1：本仓库内解耦

先抽出 `ChannelAdapter`、`AgentAdapter`、`ConversationStore`、`TaskOrchestrator` 和 `CompletionEventAdmission`，让 `feishu-agent.js` 只负责装配。此阶段不改变用户配置与运行方式。

### 阶段 2：lark-cli 能力试点

已安装的 lark-cli 与官方 Skills 只在隔离测试应用中配置/授权,先用只读身份验证消息查询、文档读取和结构化错误。确认凭证存储、scope、Windows 进程控制和输出契约后，再替换通用 OpenAPI 辅助代码。入站长连接和现有卡片状态机暂不迁移。

### 阶段 3：cc-connect sidecar 兼容性试验

用一个测试项目和测试飞书应用验证：项目/用户路由、Codex/Claude 原生 session 恢复、停止、进度、done reaction、崩溃恢复和管理 API。只有这些行为全部与 AI Resume 的安全边界对齐，才决定是否让 sidecar 成为唯一飞书编排真身。

### 阶段 4：单一真身迁移

若试验通过，GUI 只写 AI Resume 自有配置并通过适配层生成/调用 sidecar 配置；飞书会话和 Agent 运行由一个进程唯一拥有。旧 Node 飞书 agent 在迁移完成后停用，避免双写。

## 决策

已确认的执行方向：完成阶段 0 后推进阶段 1；阶段 2 与阶段 3 使用独立测试应用和测试项目，验证通过后让 cc-connect 成为唯一生产飞书/Agent 编排真身。不要让新旧运行时双消费，也不要为 `DeepSeek V4 for Copilot Chat` 维护私有扩展分支。Codex、Claude Code 和 Cline 仍是本地完成通知的三个可靠边界。

## 2026-08-01 Stage 0-11 执行计划

上方旧的 0-4 编号只保留为研究历史；当前执行采用下表。恢复现场与即时工作包顺序见 `RECOVERY-AUDIT-20260801.md`,基线、状态和事件契约分别见 `MIGRATION-BASELINE.md`、`STATE-OWNERSHIP.md`、`EVENT-CONTRACTS.md`，已知问题见 `MIGRATION-DEBT.md`。

| 阶段 | 内容 | 出口门禁 |
|---|---|---|
| 0 | 产品基线 | 功能清单、状态所有权、事件契约、ADR、现役版本/进程/日志/测试证据齐全；不改生产行为 |
| 1 | 原系统解耦 | Channel、Agent、Task、Conversation、Authorization、CompletionAdmission 六边界完成；录制事件行为等价；绑定债务关闭 |
| 2 | C# 基础设施 | .NET 10 WPF + Worker Service + Named Pipe + SQLite/WAL + 结构化日志 + DPAPI/Credential Manager shadow 骨架可构建；`TaskOrchestrator`、`ProcessSupervisor`、`ProviderAdapter`、`RunStore`、`HealthProbe`、`Transport` 实现 RunContract Start/Status/Cancel；单实例、锁竞争、崩溃恢复、安装卸载通过 |
| 3 | lark-cli 试点 | 独立测试应用中封装 envelope/catalog/ndjson/binary、显式 bot/user 身份、scope、exit 10 高风险确认、超时/取消/脱敏；只读消息/文档/日历和错误场景通过 |
| 4 | cc-connect 试点 | 用实际 1.4.1 二进制和独立测试项目验证 Codex/Claude Code/DeepSeek V4 Flash 新建/resume、用户隔离、项目路由、停止、进度、图片、完成提示、崩溃恢复；wrapper 不满足则新 ADR |
| 5 | 产品状态迁移 | 项目发现、provider 健康、Claude 限额、布防周期、RunStore/进程登记迁至 C# shadow；15-30 秒观察、静默不失败、断电、PID 复用、CIM 失败 fail-closed；旧系统仍是唯一生产 writer |
| 6 | 会话与任务迁移 | cc-connect 接管测试应用聊天、查询、修改和原生 Agent session；多用户/项目/provider 并发、旧卡、重复事件、延迟回调、停止竞态通过 |
| 7 | GUI 迁移 | C# WPF 覆盖现有核心工作流、DPI/缩放、模型不可用、会话管理和日志导出；旧 GUI 只作回滚入口且不双写 |
| 8 | Hook 与部署 | C# 单文件 hook + completion outbox；Codex/Claude Code/Cline 真机边界、subagent/projectless/internal-run 零误报；原子安装升级卸载和旧 hook 恢复通过 |
| 9 | 数据迁移演练 | JSON、marker、会话映射幂等迁入 SQLite；数量、哈希、session ID、权限和布防周期对账；原文件只备份不删除 |
| 10 | 生产切换 | 维护窗口先停 Node 再启动 C# 服务与 cc-connect；确认唯一消费者；聊天、查询、修改、停止、图片、通知、重启冒烟；P0/P1 失败立即回滚 |
| 11 | 收尾 | 删除失去职责的 Node/PowerShell 模块；依赖、残留进程/任务/启动项、秘密泄漏、文档和完整回归通过；迁移备份保留一个版本周期 |

### 全局强制指标

- 同一生产飞书应用任何时刻只有一个事件消费者；测试并行必须使用独立应用。
- 同一事件重放 100 次，远端重复消息为 0；入站 ACK p95 小于 500ms。
- 用户、聊天、项目、provider、原生 session 不串线。
- 停止终止完整进程树，遗留 AI 子进程为 0；崩溃重启后恢复或明确报告中断。
- AI 生成无客户端总时限;静默超过历史 deadline 仍保持 running;只有结构化 408/504/gateway_timeout 是 failed_provider 超时。
- DNS/TCP/TLS/reset、进程消失和监控异常进入 failed_local;cancelled 与 sideEffectsStarted 后自动 fallback/重放为 0。
- 非 owner 无文件、执行和高风险飞书写入能力。
- API Key、飞书密钥和授权令牌在仓库与日志中出现次数为 0。
- Codex、Claude Code、Cline 完成通知保持；DeepSeek Copilot Chat 不伪造任务完成边界。
- 动态项目发现不依赖当前项目数量。

### 执行与成本熔断

1. 每个工作包开始前冻结目标、输入输出、状态所有权、异常边界、验收条件和范围外事项。
2. DeepSeek V4 Flash 每个工作包最多一次主实现和一次修正；两次仍未通过同一门禁即停止并重新设计，不启动第三轮。
   DeepSeek 委派统一使用异步 Start/Status/Cancel,每 15-30 秒读取持久状态和进程存活性;静默或本地固定时长不得触发 Cancel。
3. 单个工作包原则上不超过 4 个生产文件、4 个测试文件或 800 行净变更；超出先拆分。
4. Codex 审查当前增量并运行针对性测试；完整离线回归和真 API 冒烟只在阶段总门禁各运行一次。
5. 工作包只做 Codex 主审和针对性测试；整个阶段实现与基础验证完成后，再运行一轮两个独立只读总审查。审查只覆盖冻结门禁、阶段增量和本阶段绑定债务；范围外发现登记到 `MIGRATION-DEBT.md`，不得自动扩大当前阶段。
6. 未经用户授权不 commit/push/deploy，不连接生产应用，不切换生产消费者。
