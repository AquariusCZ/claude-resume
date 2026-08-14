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

### 2026-08-08 固定 v1.4.1 平台复核

本轮以已安装二进制对应源码 `5d4c96dd12774574369e75b60084140101c9a59a` 为准，补齐 Windows 平台级结论：

- 裸 `cc-connect daemon restart` 在 Windows 可能退出 0，但旧进程没有退出；`daemon status` 只反映计划任务状态，也不能证明 API、配置或飞书已就绪。`restart --force` 只按锁 PID 硬杀 daemon，不能作为 AI Resume 的成功判据。
- v1.4.1 上游安装器创建的是 `Interactive + Limited` 登录任务，Windows 默认还会带来 `PT72H`、电池停机、无崩溃恢复和无无限重复 watchdog；这些值不满足常驻机器人边界。生产任务通过仓库的硬化脚本改为当前用户 `S4U + Limited`、`PT0S`、两项电池停机关闭、`RestartCount=3`/`PT1M`、`IgnoreNew`，并保留单一登录触发器的无限 `PT5M` repetition。GUI 必须验证这份产品级契约，不能把“上游任务存在”当成守护有效。
- 生产 S4U 任务中的 daemon 对交互式 GUI 隔离；本机实证进程路径、启动时间、句柄和 WMI `CommandLine` 均可能拒绝访问或为空。自行实现“核验后强杀进程树”在目标平台不可用，因此不采用；进程归属只能作为组合证据，不能替代锁/API/日志与完整任务定义。
- 上游已有带 token 的 `POST /api/v1/restart`。主进程收到后会执行 `Engine.Stop`，关闭平台、agent session 与 agent，释放实例锁，再在原安全上下文启动新 OS 进程。`RestartCh` 先入队、HTTP 响应后写，因此连接重置属于结果未知而不是明确拒绝。AI Resume 只做薄编排：候选验证与原子提交、调用该接口、验证不同锁 PID/本次启动时间/目标 agent/按锁 PID 分代的本次日志，再验证根路径唯一任务的 action/脚本/SID/principal/settings/trigger、LastRunTime/进程归属，并在返回前复核同一 PID/version/agent 仍在线。
- `provider.agent_types` 缺失或为空表示上游对所有 agent 开放；provider 名称和值、agent 类型和 `endpoints` / `agent_models` / `agent_model_lists` 的键均保留原字符串并区分大小写。`ResolveForAgent` 已提供按 agent 覆盖，无需自研第二套 provider 状态机。项目内联 provider 不走全局 `agent_types` 过滤，因此切 agent 时必须单独处理或失败关闭。
- Claude Code 可使用 Anthropic 兼容的 DeepSeek endpoint；Codex 需要 OpenAI/Codex 兼容 endpoint。默认标量 `model` 与可选模型列表是两套字段，Claude adapter 缺少列表时会回退 Claude 内置名称，Codex 还会优先本地 `model_catalog_json`。
- 新 Engine 会调用 `sessions.InvalidateForAgent` 清理旧 agent 的原生 session ID，因此 agent 生效不要求 `/new`；但 `Session.ActiveProvider` 不会清空，同名且仍兼容的 provider 可能恢复。

自研成立的边界仅限上游没有提供的“产品级激活事务”：跨窗口互斥、生产文件哈希对账、失败时条件回滚、单消费者门禁、计划任务重新布防，以及把锁 PID/API/agent/日志证据组合成用户可见的完成判据。

### 2026-08-09 Codex 模型目录复核

本轮继续以 cc-connect v1.4.1 对应提交 `5d4c96dd12774574369e75b60084140101c9a59a` 为平台真身，并核对 [OpenAI Codex models](https://developers.openai.com/codex/models) 与 [OpenAI API models](https://developers.openai.com/api/docs/models)：

- cc-connect Codex `AvailableModels` 的顺序是本地 `model_catalog_json` → 当前活动 provider 的配置模型 → `/v1/models` 白名单 → Codex `models_cache.json` → 内置回退。v1.4.1 的内置回退仍是 `o4-mini`、`o3`、`gpt-4.1*`、`codex-mini-latest`,不能代表 2026-08-09 的现行推荐。
- provider 即使已出现在 `provider_refs`,只要 `[projects.agent.options].provider` 为空,上游 `activeIdx` 仍是 `-1`,配置模型表不会被读取。这正是截图中旧菜单出现的直接原因。
- OpenAI 当前推荐 Codex 模型是 `gpt-5.6-sol`、`gpt-5.6-terra`、`gpt-5.6-luna`;`gpt-5.6` 仍作为 Codex 默认模型标识使用。`gpt-5.3-codex-spark` 有产品/账号可用性限制,不作为通用 provider 候选自动下发。
- 因而采用薄配置适配而非 fork cc-connect:唯一兼容 provider 自动写入活动选择;官方 OpenAI 端点的 `gpt-5.6` 家族且无用户列表时生成默认值加三项官方候选;第三方 relay 不继承官方能力假设,只保留有效默认值或自身显式列表;零个或多个 provider 不猜选;用户列表与本地 model catalog 继续优先。生成 alias 使用 `[AI Resume] ` 作为可跨上游 CRUD TOML 重编码的所有权证据,无标记列表不迁移。`config format` 本身保留注释;候选已用该命令实测通过,另有结构化重编码回归覆盖注释消失后的刷新。

- 目标机当前第三方 Codex relay 的 `/models` 与 `/usage` 直连实测均返回 HTTP 200,前者明确列出 `gpt-5.6-sol`、`gpt-5.6-terra`、`gpt-5.6-luna`,后者返回正余额；三项模型已通过候选 `config format` 验证并原子激活到该 provider 的显式模型列表,守护进程换代、目标 agent、飞书 ready 与计划任务 watchdog 均已核验。对这个 Sub2API provider,产品按 CC Switch 语义把有效正余额作为绿色 provider/account 证据；该结论只属于这一 relay,未调用 `/responses`,不得泛化为所有 OpenAI-compatible 服务都具备同样推理能力。

这项能力上游“部分已有但默认回退过时且需要活动 provider 才生效”,所以正确做法是补齐上游要求的配置形状,不是在 AI Resume 里另写一套模型菜单或修改 cc-connect 源码。

### 2026-08-09 Claude 额度读取生态复核

现役请求、解析、稀疏合并、SQLite 并发、GUI 语义与验证步骤已收敛到 [`CLAUDE-QUOTA-ACQUISITION.md`](CLAUDE-QUOTA-ACQUISITION.md);本节只保留上游与平台证据。

本轮先回看 AI Resume 旧 PowerShell `Test-ClaudeReady` / `Save-RealResetFromProbe`:它通过 Claude CLI 的 `rate_limit_event` 读取窗口,而且只有探测明确给出百分比时才覆盖旧值,缺字段不会清空。这说明“进度条以前能连续显示”来自稀疏合并语义,不是本地估算。随后通过 v2rayN `127.0.0.1:10808` 只读核对三个同类实现与目标机 Claude Code 2.1.185 二进制:

- [llm-cost-bar `SubscriptionProvider.swift`](https://github.com/kpnemo/llm-cost-bar/blob/378786c55ae4830de4c864592d679a3b44eaedad/Core/Sources/LLMCostBarCore/SubscriptionProvider.swift) 明确同时发送 `anthropic-beta: oauth-2025-04-20` 和 `User-Agent: claude-code/2.0.0`;其设计文档记录其它 UA 会进入更激进的限流桶,并采用“stale beats blank”:网络、429、5xx 时保留最后读数并标记陈旧。
- [CodexBar `ClaudeOAuthUsageFetcher.swift`](https://github.com/steipete/CodexBar/blob/171c2dce44d1e48cb1e9fab57c24df2a773fba2b/Sources/CodexBarCore/Providers/Claude/ClaudeOAuth/ClaudeOAuthUsageFetcher.swift) 同样生成 `claude-code/<version>` User-Agent;其变更记录明确按账号保留 429 前最后一次 OAuth 读数,并展示 `weekly_scoped` / Fable 窗口。
- [ClaudeTimer `ClaudeUsageClient.cs`](https://github.com/TimeWinder-dk/ClaudeTimer/blob/9b09a72d86b7f51241de7d61a2df71c59ef2294e/src/ClaudeTimer/Services/ClaudeUsageClient.cs) 优先遍历现代 `limits` 数组,让 `session`、`weekly_all`、`weekly_scoped` 等新增窗口无需硬编码响应顶层字段;旧顶层窗口只作兼容回退。

目标机实证与上述结论一致:同一 OAuth token 用普通 .NET 请求形状得到 HTTP 429;补齐 `Accept`、OAuth beta、Anthropic version 与 `claude-code/2.1.185` User-Agent 后立即得到 HTTP 200,并返回 `session=0%`、`weekly_all=100%`、`weekly_scoped:Fable=100%`。因此正确实现不是改成估算或只依赖 CLI,而是薄复现 Claude Code 请求协议,优先解析现代 `limits`,再按“账号 + 窗口名 + resetAt 代次”合并稀疏观测。当前明确值优先;缺字段可承接到同一未来 reset,账号变化、reset 换代或到期立即失效并以琥珀色“最近服务端读数”标记。

### 2026-08-09 GUI 微动画生态复核

本轮按“先查上游”盘点 [Motion](https://github.com/motiondivision/motion)、[Anime.js](https://github.com/juliangarnier/anime) 与 [GSAP](https://github.com/greensock/GSAP)。三者都能实现旋转、入场和状态过渡,但 AI Resume 的现役前端是随程序集离线分发的单个 `index.html`,没有 npm 打包链、模块加载器或外网运行依赖;为少量状态微动画引入库会新增版本、许可证、打包和供应链表面,却不提供当前 CSS Keyframes / Web Animations API 缺少的能力。

因此采用平台原生实现:刷新、provider 探测和保存等进行态使用 transform/opacity 动画;列表、状态行和弹窗只做 160-260ms 的短促入场;额度仍以真实数据直接决定宽度。目标机 Windows 当前报告 `ClientAreaAnimation=False`,WebView2 因而命中 `prefers-reduced-motion: reduce`;旧规则连刷新 spinner 一起停掉,才会把旋转环显示成静态字母 C。现役规则关闭扫光、位移等装饰性运动,把入场降级为短促透明度变化,必要的忙碌反馈改为更慢的分段旋转/明灭,不修改 Windows 全局动画设置。

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

### 完成通知上游盘点（2026-08-09）

- Codex 官方 `notify` 是 argv 数组，当前完成事件 `agent-turn-complete` 的 JSON 追加在最后一个参数；不能按 stdin 实现，也不能按空格拆含空格路径。
- Claude Code 与 Qoder 的 command Hook 从 stdin 读取 JSON，主 agent 完成边界为 `Stop`；`stop_hook_active` 必须抑制递归。
- Cline 的本机稳定边界是 `TaskComplete.ps1`，wrapper 必须把同一 stdin 继续传给用户旧脚本并保留其取消输出。
- OpenCode 已有插件事件系统和 `session.idle`，Windows 桌面版使用 Bun 运行插件；应使用 `Bun.spawn` argv 数组加 stdin，不能用 shell 模板字符串拼 JSON。
- 飞书投递继续复用项目已有的 `LarkCliInvoker` 与官方 `lark-cli`，没有自研 SDK 请求的必要。完整证据、准入和冒烟见 `docs/COMPLETION-NOTIFICATIONS.md`。
- 动态项目发现不依赖当前项目数量。

### 执行与成本熔断

1. 每个工作包开始前冻结目标、输入输出、状态所有权、异常边界、验收条件和范围外事项。
2. DeepSeek V4 Flash 每个工作包最多一次主实现和一次修正；两次仍未通过同一门禁即停止并重新设计，不启动第三轮。
   DeepSeek 委派统一使用异步 Start/Status/Cancel,每 15-30 秒读取持久状态和进程存活性;静默或本地固定时长不得触发 Cancel。
3. 单个工作包原则上不超过 4 个生产文件、4 个测试文件或 800 行净变更；超出先拆分。
4. Codex 审查当前增量并运行针对性测试；完整离线回归和真 API 冒烟只在阶段总门禁各运行一次。
5. 工作包只做 Codex 主审和针对性测试；整个阶段实现与基础验证完成后，再运行一轮两个独立只读总审查。审查只覆盖冻结门禁、阶段增量和本阶段绑定债务；范围外发现登记到 `MIGRATION-DEBT.md`，不得自动扩大当前阶段。
6. 未经用户授权不 commit/push/deploy，不连接生产应用，不切换生产消费者。
