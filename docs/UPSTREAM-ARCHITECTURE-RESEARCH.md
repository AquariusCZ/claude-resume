# 上游架构研究：Claude Code、OpenCode、飞书官方 lark-cli 与已退役的 cc-connect

> 本文按时间顺序保留历次上游盘点:2026-07-31 研究快照、2026-08-01 的 Stage 0-11 迁移计划(已于 2026-08 执行完毕,见 `history/STAGE-11-GATE.md`)、2026-08 至 10 月的各轮平台复核,以及文末的「2026-10-30 v2.2 上游盘点」(现役)。**cc-connect 已于 2026-10-30 随 ADR-0006 整体退役**,文中所有 cc-connect 结论都只是历史证据,不描述现役行为。现役决策以 `adr/0006-retire-cc-connect-per-project-resume.md` 为准(它取代 ADR-0003 中 cc-connect 的全部现役部分、ADR-0005 的 A1 与 D1 中的投递设计,并修订 ADR-0004 的「全队列共用一个模型」);AI 运行生命周期仍以 `adr/0002-run-lifecycle-contract.md` 与 `RUN-CONTRACT.md` 为准。

## 现役结论(2026-10-30,v2.2)

- **Claude Code**(本机 CLI 2.1.300、VS Code 扩展 2.1.280、Desktop 内嵌 2.1.309):完成通知经官方 hooks(`Stop` / `StopFailure` / `Notification`,exec form);额度凭据只读它自己的登录文件,账号身份读全局配置里的 `oauthAccount`;续跑取证读它的运行中会话登记 `sessions/<pid>.json` 与会话转录尾部;手机远程交给官方 Remote Control,AI Resume 不再实现任何聊天通道。
- **OpenCode 2.x**(本机 Desktop 2.2.3):完成通知经官方 v2 插件 API(目录形态插件),是否已加载只以 OpenCode 自己的日志为证;DeepSeek 余额只读 OpenCode 里的密钥,只展示、不续跑。
- **飞书官方 lark-cli**(本机 1.0.81):只剩「以机器人身份发完成通知私信」这一种用途;直接启动 npm 包内的 `lark-cli.exe`,机器人凭据由 AI Resume 用机器范围副本自己换 tenant_access_token,只把 App ID + 令牌经官方环境变量注入(env 来源只收现成令牌,ADR-0007),错误从 stderr 信封读取。
- **Codex 0.148.0**:ADR-0005 的 B1(官方账号)与 C1(hooks 通知)不变,本轮未复核;GUI 默认隐藏。
- **cc-connect**:2026-10-30 退役,不再跟随其版本(本机停在 1.4.1,npm 已到 1.5.0);评估见下文「2026-10-30 退役评估(ADR-0006)」。

研究日期：2026-07-31(下文未注日期的段落都是当日快照,只作历史;带日期的小节是之后各轮复核)

固定快照：

- [chenhg5/cc-connect](https://github.com/chenhg5/cc-connect) 研究快照 `12a589fcaae28bf5b05d960e03862f61bebf2e95`
- [larksuite/cli](https://github.com/larksuite/cli) `003d0f42f84d3799c62f2a666fb0ddf4084283c7`
- 2026-10-30 v2.2 盘点所用的本机版本见文末版本表。

## 2026-07-31 研究结论(历史)

> 这是 2026-07-31 的判断。之后的走向是:2026-08 按 Stage 0-11 迁到 C#;2026-08-06 起直接使用 cc-connect(ADR-0003);2026-10-30 cc-connect 整体退役,飞书只保留完成通知(ADR-0006)。现役结论见文首。

AI Resume 不适合继续把所有飞书事件、权限、卡片、会话、Agent CLI 参数、运行锁和完成通知堆在一个进程模块里；但也不应直接整体替换成任一上游项目。

用户已于 2026-07-31 确认：实际采用这两个上游组件，而不是只借鉴其设计。当前机器已安装 `cc-connect 1.4.1`（二进制报告 commit `5d4c96dd`）、`lark-cli 1.0.81` 和 27 个官方 `lark-*` Skills。cc-connect daemon 尚未安装/启动，lark-cli 尚未写入应用凭据或用户授权，因此生产飞书仍由现役 Node agent 唯一消费。研究快照与已安装 cc-connect 二进制不是同一提交，阶段 3 必须以实际二进制重新验证，不能把源码快照结论直接当运行时保证。

推荐采用渐进式混合架构：

1. 保留 AI Resume 的 Windows GUI、项目发现、Claude 限额后自动续跑、原生会话管理和 Codex/Claude Code/Cline 本地完成通知。
2. 将飞书渠道、任务/会话编排和 Agent 执行器拆成明确接口；优先验证以 cc-connect sidecar 承担飞书长连接与 Agent 会话编排的可行性。
3. 将 lark-cli 作为 AI 可调用的飞书能力层，而不是机器人运行时。它负责消息、文档、日历、任务等 OpenAPI 操作和对应 Skills；入站事件路由、项目状态和本地 AI 生命周期仍由编排层负责。
4. 在完成兼容性试验前，不移除现有运行链路，不同时维护两套可写会话真身。

## cc-connect

> **2026-10-30 退役说明(ADR-0006)**:本节及其下各轮复核(2026-07-31、08-06、08-08、08-09)全部是历史证据。AI Resume 已删除 `AiResume.Wrapper` 整个项目与 `cc-connect-apply` / `feishu-check` / `preflight` 命令,不再生成、激活、重启或检查 cc-connect,也不再有飞书聊天、会话与授权名单;本机的 cc-connect 计划任务、npm 包与 `~/.cc-connect`(内含多份明文密钥)由用户自行清理,并吊销其中的密钥。退役原因有三:用户的手机远程已改用 Claude Code 官方 Remote Control;cc-connect 近两个月几乎没人用(9 月飞书入站 0 条、10 月 2 条,cron 从未用过,计划任务已被用户停用);继续管理它的成本在上升(版本钉死在 1.4.1 而上游已到 1.5.0,激活契约要随上游配置格式与进程形态维护)。证据见本节末「2026-10-30 退役评估(ADR-0006)」。

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

### 2026-08-06 限额取数复核(历史)

cc-connect v1.4.1 的 `UsageReporter.GetUsage` 不是读 API,而是用 PTY 起 Claude Code TUI、发送 `/usage`、抓取 ANSI 屏幕再正则解析;它依赖 `github.com/creack/pty`,而该库在 Windows 上命中 `pty_unsupported.go`(`open()` 直接返回 `ErrUnsupported`),管理 API 也没有任何 usage 端点。所以「消费上游 `UsageReport`」在目标平台不可用,限额数据由 AI Resume 自取(ADR-0003 §2.3,证据细节见 `history/design/s7c-quota-probe-spec.md` §1)。这是「上游有不等于能用,要验证到平台一级」的原始案例;cc-connect 退役后「额度自取」的结论不变,现役取数协议见 `CLAUDE-QUOTA-ACQUISITION.md`。

### 2026-08-08 固定 v1.4.1 平台复核

> 历史:本节描述的「产品级激活事务」(配置候选校验、原子提交、`/api/v1/restart`、计划任务重新布防与单消费者预检)已随 cc-connect 退役整体删除。

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

> 历史:本节的 cc-connect 配置生成(活动 provider、`[AI Resume] ` alias、`config format` 校验)已随退役作废;文中的 OpenAI 推荐模型名是 2026-08-09 的快照,现役模型一律来自供应商实时目录。第三方 relay 以 `/v1/usage` 正余额作为 provider/account 绿色证据的 CC Switch 语义,仍由 `ProviderCatalogService` 的 Codex 第三方行沿用。

本轮继续以 cc-connect v1.4.1 对应提交 `5d4c96dd12774574369e75b60084140101c9a59a` 为平台真身，并核对 [OpenAI Codex models](https://developers.openai.com/codex/models) 与 [OpenAI API models](https://developers.openai.com/api/docs/models)：

- cc-connect Codex `AvailableModels` 的顺序是本地 `model_catalog_json` → 当前活动 provider 的配置模型 → `/v1/models` 白名单 → Codex `models_cache.json` → 内置回退。v1.4.1 的内置回退仍是 `o4-mini`、`o3`、`gpt-4.1*`、`codex-mini-latest`,不能代表 2026-08-09 的现行推荐。
- provider 即使已出现在 `provider_refs`,只要 `[projects.agent.options].provider` 为空,上游 `activeIdx` 仍是 `-1`,配置模型表不会被读取。这正是截图中旧菜单出现的直接原因。
- OpenAI 当前推荐 Codex 模型是 `gpt-5.6-sol`、`gpt-5.6-terra`、`gpt-5.6-luna`;`gpt-5.6` 仍作为 Codex 默认模型标识使用。`gpt-5.3-codex-spark` 有产品/账号可用性限制,不作为通用 provider 候选自动下发。
- 因而采用薄配置适配而非 fork cc-connect:唯一兼容 provider 自动写入活动选择;官方 OpenAI 端点的 `gpt-5.6` 家族且无用户列表时生成默认值加三项官方候选;第三方 relay 不继承官方能力假设,只保留有效默认值或自身显式列表;零个或多个 provider 不猜选;用户列表与本地 model catalog 继续优先。生成 alias 使用 `[AI Resume] ` 作为可跨上游 CRUD TOML 重编码的所有权证据,无标记列表不迁移。`config format` 本身保留注释;候选已用该命令实测通过,另有结构化重编码回归覆盖注释消失后的刷新。

- 目标机当前第三方 Codex relay 的 `/models` 与 `/usage` 直连实测均返回 HTTP 200,前者明确列出 `gpt-5.6-sol`、`gpt-5.6-terra`、`gpt-5.6-luna`,后者返回正余额；三项模型已通过候选 `config format` 验证并原子激活到该 provider 的显式模型列表,守护进程换代、目标 agent、飞书 ready 与计划任务 watchdog 均已核验。对这个 Sub2API provider,产品按 CC Switch 语义把有效正余额作为绿色 provider/account 证据；该结论只属于这一 relay,未调用 `/responses`,不得泛化为所有 OpenAI-compatible 服务都具备同样推理能力。

这项能力上游“部分已有但默认回退过时且需要活动 provider 才生效”,所以正确做法是补齐上游要求的配置形状,不是在 AI Resume 里另写一套模型菜单或修改 cc-connect 源码。

### 2026-10-30 退役评估(ADR-0006)

用户 2026-10-30 的用法:手机上已改用 Claude Code 官方 App 的 Remote Control;DeepSeek 在 OpenCode 里用;飞书只想收通知,微信不要了。按「先查已有,再决定自研」逐项核对 cc-connect 还剩什么价值(本机日志按月统计,只读,未输出任何密钥):

| 能力 | 本机使用证据 | 官方替代 | 结论 |
|---|---|---|---|
| 手机聊天驱动 Claude Code | 飞书入站 8 月 54 条、9 月 0 条、10 月 2 条 | Remote Control(发消息、图片与文件、批准权限、停止后台子代理、切模型;`claude remote-control --spawn` 可从手机新建会话)、Dispatch | 退役 |
| `/stop`、权限确认 | 49 条命令审计记录全在 8 月,`/stop` 只用过 1 次 | Remote Control 的停止与权限转发 | 退役 |
| cron / 定时任务 | `crons/jobs.json` 为 `[]`,34 次启动全部 `jobs=0` | Desktop 定时任务、云端 routines、会话内 `/loop` | 退役 |
| `/provider`(在 Claude Code 里用 DeepSeek) | 8 月 14 次 | 无(Remote Control 不支持指向非 Anthropic 主机的 `ANTHROPIC_BASE_URL`);用户已改在 OpenCode 里用 DeepSeek | 不需要 |
| 微信平台 | 整个周期 1 条消息,却有 10,739 行 `getUpdates failed`,每次启动都是 partial readiness | — | 删除 |
| 完成提醒 | — | Remote Control 推送只覆盖已连接的 Claude Code 会话,完成推送由模型自行决定 | 保留飞书通知(经 lark-cli,本来就不依赖 cc-connect) |
| 手机驱动 OpenCode / Codex | 无 | Remote Control 不覆盖 | 唯一缺口;需要时用户可自行运行 cc-connect,AI Resume 不管理 |

其它证据:

- 计划任务 `cc-connect` 已被用户停用,没有进程,管理端口未监听,日志停在 2026-10-28。
- 维护成本在上升:激活契约当时是 `CLAUDE.md` 里最长的一段门禁,版本钉死在 v1.4.1,升级要单独 ADR;npm 上 `latest` 已是 1.5.0(2026-10-26),beta 为 1.5.1-beta.4。
- 完成通知链路与 cc-connect 完全解耦:Hook → `completion-events` → S4U Worker → `lark-cli im +messages-send --as bot`;AI Resume 从不消费飞书事件,「同一飞书应用只能有一个消费者」的约束对 AI Resume 已失去对象。
- 删除面:`AiResume.Wrapper` 整个项目(约 3.7k 行)、Worker 侧的配置生成/激活/预检/飞书接入凭据、GUI 的 `feishu.*` / `cutover.*` / `agent.*` RPC 与关窗守卫、14 个测试文件;`ProductConfig.CcConnectAgent` 删除后旧 `config.json` 里的该键被忽略。
- 否决的方案:冻结隐藏(代码与维护成本全留,误点「生成并重启」会重新拉起微信平台);只留只读诊断(仍要跟随上游配置格式,仍要读含明文密钥的 TOML)。

上游盘点结论:这一次是「上游官方已经有,所以删掉自有实现」。Remote Control、Dispatch、Desktop 定时任务与云端 routines 覆盖了 cc-connect 对 Claude Code 的聊天、停止、权限、定时任务与附件价值;飞书通知继续用官方 lark-cli,不需要 cc-connect 的 `send`;没有需要自研的部分。

### 2026-08-09 Claude 额度读取生态复核

> 2026-10-30 注:本节的协议形状与稀疏合并结论仍有效。凭据来源已改为只读 Claude Code 自己的登录(v2.1 的 setup-token 被 usage 端点拒绝,已删除),账号指纹改读 `oauthAccount`,见文末 v2.2 盘点。

现役请求、解析、稀疏合并、SQLite 并发、GUI 语义与验证步骤已收敛到 [`CLAUDE-QUOTA-ACQUISITION.md`](CLAUDE-QUOTA-ACQUISITION.md);本节只保留上游与平台证据。

本轮先回看 AI Resume 旧 PowerShell `Test-ClaudeReady` / `Save-RealResetFromProbe`:它通过 Claude CLI 的 `rate_limit_event` 读取窗口,而且只有探测明确给出百分比时才覆盖旧值,缺字段不会清空。这说明“进度条以前能连续显示”来自稀疏合并语义,不是本地估算。随后通过 v2rayN `127.0.0.1:10808` 只读核对三个同类实现与目标机 Claude Code 2.1.185 二进制:

- [llm-cost-bar `SubscriptionProvider.swift`](https://github.com/kpnemo/llm-cost-bar/blob/378786c55ae4830de4c864592d679a3b44eaedad/Core/Sources/LLMCostBarCore/SubscriptionProvider.swift) 明确同时发送 `anthropic-beta: oauth-2025-04-20` 和 `User-Agent: claude-code/2.0.0`;其设计文档记录其它 UA 会进入更激进的限流桶,并采用“stale beats blank”:网络、429、5xx 时保留最后读数并标记陈旧。
- [CodexBar `ClaudeOAuthUsageFetcher.swift`](https://github.com/steipete/CodexBar/blob/171c2dce44d1e48cb1e9fab57c24df2a773fba2b/Sources/CodexBarCore/Providers/Claude/ClaudeOAuth/ClaudeOAuthUsageFetcher.swift) 同样生成 `claude-code/<version>` User-Agent;其变更记录明确按账号保留 429 前最后一次 OAuth 读数,并展示 `weekly_scoped` / Fable 窗口。
- [ClaudeTimer `ClaudeUsageClient.cs`](https://github.com/TimeWinder-dk/ClaudeTimer/blob/9b09a72d86b7f51241de7d61a2df71c59ef2294e/src/ClaudeTimer/Services/ClaudeUsageClient.cs) 优先遍历现代 `limits` 数组,让 `session`、`weekly_all`、`weekly_scoped` 等新增窗口无需硬编码响应顶层字段;旧顶层窗口只作兼容回退。

目标机实证与上述结论一致:同一 OAuth token 用普通 .NET 请求形状得到 HTTP 429;补齐 `Accept`、OAuth beta、Anthropic version 与 `claude-code/2.1.185` User-Agent 后立即得到 HTTP 200,并返回 `session=0%`、`weekly_all=100%`、`weekly_scoped:Fable=100%`。因此正确实现不是改成估算或只依赖 CLI,而是薄复现 Claude Code 请求协议,优先解析现代 `limits`,再按“账号 + 窗口名 + resetAt 代次”合并稀疏观测。当前明确值优先;缺字段可承接到同一未来 reset,账号变化、reset 换代或到期立即失效并以琥珀色“最近服务端读数”标记。

### 2026-08-09 GUI 微动画生态复核

本轮按“先查上游”盘点 [Motion](https://github.com/motiondivision/motion)、[Anime.js](https://github.com/juliangarnier/anime) 与 [GSAP](https://github.com/greensock/GSAP)。三者都能实现旋转、入场和状态过渡,但 AI Resume 的现役前端是随程序集离线分发的单个 `index.html`,没有 npm 打包链、模块加载器或外网运行依赖;为少量状态微动画引入库会新增版本、许可证、打包和供应链表面,却不提供当前 CSS Keyframes / Web Animations API 缺少的能力。

因此采用平台原生实现:刷新、provider 探测和保存等进行态使用 transform/opacity 动画;列表、状态行和弹窗只做 160-260ms 的短促入场;额度仍以真实数据直接决定宽度。目标机 Windows 当前报告 `ClientAreaAnimation=False`,WebView2 因而命中 `prefers-reduced-motion: reduce`;旧规则连刷新 spinner 一起停掉,才会把旋转环显示成静态字母 C。现役规则关闭扫光、位移等装饰性运动,把入场降级为短促透明度变化,必要的忙碌反馈改为更慢的分段旋转/明灭,不修改 Windows 全局动画设置。

> 2026-10-30 注:v2.2 重排后的 `index.html` 仍不引入动画库;`prefers-reduced-motion: reduce` 下去掉位移与扫描动画、队列行不再做入场动画,忙碌转圈改为 1.2 秒的淡入淡出。

## 飞书官方 lark-cli

> 2026-07-31 快照。此后本机的 lark-cli 已配置好通知所用飞书应用的机器人凭据与用户授权;v2.2 起 AI Resume 只用它做三件事:以机器人身份发完成通知私信(`im +messages-send --as bot`)、通道体检(`doctor`)与识别收件人(`auth list --json`),不消费任何飞书事件。v2.2 的平台实证(npm 垫片与 cmd.exe 截断、钥匙串与 S4U、环境变量凭据、stderr 错误信封)见文末。

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
- 官方安全说明建议把具有用户权限的机器人作为私人助手使用，并避免暴露给不可信群成员。AI Resume 仍必须保留 open_id 校验、owner/viewer 边界和最小权限策略。(2026-10-30 注:v2.2 起没有任何飞书入站,owner/viewer 边界随聊天通道一起消失;仍保留的是收件人 open_id 的规整与校验(`NotifyRecipient`:去掉全部空白后必须是 `ou_` 加字母数字),以及只用机器人身份发送(`--as bot`、`LARKSUITE_CLI_STRICT_MODE=bot`)。)

## v1 为何容易出现零散缺陷(2026-07-31 快照,历史)

> 描述的是已于 2026-08-08 删除的 v1 Node 实现(`src/feishu-agent.js`),只作历史。

问题不是某一个 API 写错，而是几个生命周期目前集中在同一模块中：

- `src/feishu-agent.js` 约 205 KB，同时处理长连接、鉴权、项目发现、会话、卡片、图片、运行锁、provider 健康、任务执行和完成通知投递。
- Agent CLI 是外部、版本化的协议，但首次运行与 resume 曾分别拼参数，没有共享不变量；这次 Codex 非 Git cwd 缺陷就是直接结果。
- 完成 hook 的回调语义与“用户顶层任务完成”不是同一件事；此前只有 event id 去重，没有先定义事件准入，因此 projectless 和 ephemeral turn 会被当成项目任务。
- 运行源码与 `%LOCALAPPDATA%\ClaudeResume` 部署副本并存，若修改后未精确安装和重启，代码、日志和用户看到的行为可能属于不同版本。
- 测试覆盖较多局部规则，但历史上缺少“首次请求 -> resume -> 重启/通知”的跨边界真实回归。

这次修复已经先补两条不变量：Codex 新建/续接共用参数构建器；完成事件必须先满足顶层持久会话和有效项目目录准入，再进入队列与去重。后续架构调整应继续围绕可验证的接口和状态所有权，而不是继续在入口文件追加分支。

## 推荐实施顺序(2026-07-31,历史)

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

## 2026-07-31 决策(历史)

> 此方向已先后被 ADR-0003(2026-08-06,直接使用 cc-connect,AI Resume 收敛为控制面与续跑引擎)与 ADR-0006(2026-10-30,cc-connect 退役,飞书只保留完成通知)修订。现役通知源注册表有 Claude Code / Codex / Cline / Qoder / OpenCode 五个适配器,GUI 主界面只显示 Claude Code 与 OpenCode。

已确认的执行方向：完成阶段 0 后推进阶段 1；阶段 2 与阶段 3 使用独立测试应用和测试项目，验证通过后让 cc-connect 成为唯一生产飞书/Agent 编排真身。不要让新旧运行时双消费，也不要为 `DeepSeek V4 for Copilot Chat` 维护私有扩展分支。Codex、Claude Code 和 Cline 仍是本地完成通知的三个可靠边界。

## 2026-08-01 Stage 0-11 执行计划(已执行完毕,历史)

上方旧的 0-4 编号只保留为研究历史;下表是当时的执行计划,已于 2026-08 执行完毕(Stage 11 收尾见 `history/STAGE-11-GATE.md`),只作历史。恢复现场与即时工作包顺序见 `history/RECOVERY-AUDIT-20260801.md`,基线、状态和事件契约分别见 `history/MIGRATION-BASELINE.md`、`history/STATE-OWNERSHIP.md`、`EVENT-CONTRACTS.md`，已知问题见 `history/MIGRATION-DEBT.md`。本节中按时间插入的两轮上游盘点(2026-08-09 完成通知、2026-10-12 v2.1)结论已被文末 v2.2 盘点部分修订,修订处逐条标注。

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

> 迁移期指标(历史)。其中「同一飞书应用只有一个事件消费者」「非 owner 无飞书写入能力」已随 cc-connect 与飞书入站一起失去对象;仍然有效的约束(AI 生成无客户端总时限、只有结构化 408/504/gateway_timeout 算 provider 超时、cancelled 与副作用开始后不自动重放、密钥在仓库与日志中零出现)以 `CLAUDE.md` 与 `RUN-CONTRACT.md` 为准。

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

- Codex 官方 `notify` 是 argv 数组，当前完成事件 `agent-turn-complete` 的 JSON 追加在最后一个参数；不能按 stdin 实现，也不能按空格拆含空格路径。(v2.1 起改用 Codex 0.148.0 hooks,见下文 2026-10-12;Hook 仍兼容旧 `notify` 负载。)
- Claude Code 与 Qoder 的 command Hook 从 stdin 读取 JSON，主 agent 完成边界为 `Stop`；`stop_hook_active` 必须抑制递归。(2026-10-30 修订:对 Claude Code 不再按 `stop_hook_active` 丢弃——官方含义是「Claude 正因某个 Stop hook 而继续」,我方 hook 从不阻断,没有递归风险,丢弃反而吞掉 `/goal` 等场景下真正的结束,改为投递端按会话合并;Claude Code 另需订阅 `StopFailure` 与真正等人的 `Notification`,见文末 v2.2 盘点。Qoder 仍按原规则抑制。)
- Cline 的本机稳定边界是 `TaskComplete.ps1`，wrapper 必须把同一 stdin 继续传给用户旧脚本并保留其取消输出。(v2.2 起 GUI 默认隐藏 Cline,适配器与持久意图仍保留。)
- OpenCode 已有插件事件系统和 `session.idle`，Windows 桌面版使用 Bun 运行插件；应使用 `Bun.spawn` argv 数组加 stdin，不能用 shell 模板字符串拼 JSON。(2026-10-30 修订:只适用于 OpenCode 1.x。2.x 不再运行 v1 插件,`session.idle` 已废弃且服务端不再发布,插件改为 `export default { id, setup }` 并经 `ctx.event.subscribe` 订阅,子进程改用 `node:child_process`(运行时仍是 Bun);「argv 数组加 stdin、不经 shell」的原则不变。见文末。)
- 飞书投递继续复用项目已有的 `LarkCliInvoker` 与官方 `lark-cli`，没有自研 SDK 请求的必要。完整证据、准入和冒烟见 `docs/COMPLETION-NOTIFICATIONS.md`。(2026-10-30 补充:投递改为直接启动 `lark-cli.exe`、凭据经环境变量注入、错误从 stderr 信封读取,见文末。)
- 动态项目发现不依赖当前项目数量。

### 2026-10-12 v2.1 上游盘点(自持凭据、官方 Codex、Codex hooks)

- **Claude Code 2.1.296(CLI)/ 2.1.295(桌面)**:`claude setup-token` 为无人值守签发约一年期 OAuth 令牌,`CLAUDE_CODE_OAUTH_TOKEN` 可供 `-p`;`claude auth status` 给出登录态。原生 `autoContinueAtUsageLimit`(≥2.1.234)只作用于交互式会话,`-p`/后台不生效,不能替代续跑引擎。`Notification` 类型新增 `permission_prompt`、`idle_prompt`、`quota_auto_resume_*`。结论:令牌由官方签发、AI Resume 只保管使用,不实现 OAuth 刷新。**(2026-10-30 推翻)**:setup-token 签发的令牌作用域只有 `user:inference`,usage 端点需要 `user:profile`,本机实测一律 403——读不到额度,ADR-0005 A1 作废;`claude auth status` 的 `loggedIn` 在访问令牌过期时仍为 true,不能当有效性信号。「AI Resume 不实现 OAuth 刷新」的结论仍成立且更严格:refresh token 一次性轮换,任何外部刷新都会把用户登出。见文末 v2.2 盘点。
- **Codex 0.148.0(tag `rust-v0.148.0`)**:hooks 含 `Stop`;非 managed hook 需 `trusted_hash == current_hash`;`hook_key` = `"{key_source}:{event_label}:{group}:{handler}"`,hooks.json 的 `key_source` 为其绝对路径 `display()`;哈希为规范化身份(键排序紧凑 JSON)的 SHA-256,本机 `interrupt` 条目作 oracle 复算一致。`-c key=value` 在 `exec` 前作全局覆盖;内置 `openai` provider 的 base_url 取 `openai_base_url`,缺省按认证模式选 chatgpt backend 或 `api.openai.com/v1`。ChatGPT `/backend-api/wham/usage` 真实返回(本机只读录制):`rate_limit.{allowed,limit_reached,primary_window,secondary_window}`、`additional_rate_limits[]`、`model_usage{slug:{available,available_at}}`、`spend_control.reached`、`rate_limit_reached_type`。
- **cc-connect v1.4.1**(历史,已退役):`cmd` 经 `strings.Fields` 拆分;codex agent 配了 provider 时 `ensureCodexAuth` 重写 `~/.codex/auth.json`(api_key 模式);管理 API 仍无 usage 端点。
- **Windows DPAPI**:`CRYPTPROTECT_LOCAL_MACHINE (0x4)` 允许同机任何用户解密;S4U session 0 下 CurrentUser 解密会间歇失败(本机 2026-10 日志)。(2026-10-30 补充:同样的间歇失败也发生在 S4U Worker 启动的第三方 CLI 内部——lark-cli 的钥匙串就是 CurrentUser DPAPI,见文末。)
- 决策与取舍见 `docs/adr/0005-owned-credentials-official-codex-and-hooks.md`(其 A1 与 D1 的投递设计已被 ADR-0006 取代)。

### 执行与成本熔断

> 迁移期的开发流程约束(历史)。

1. 每个工作包开始前冻结目标、输入输出、状态所有权、异常边界、验收条件和范围外事项。
2. DeepSeek V4 Flash 每个工作包最多一次主实现和一次修正；两次仍未通过同一门禁即停止并重新设计，不启动第三轮。
   DeepSeek 委派统一使用异步 Start/Status/Cancel,每 15-30 秒读取持久状态和进程存活性;静默或本地固定时长不得触发 Cancel。
3. 单个工作包原则上不超过 4 个生产文件、4 个测试文件或 800 行净变更；超出先拆分。
4. Codex 审查当前增量并运行针对性测试；完整离线回归和真 API 冒烟只在阶段总门禁各运行一次。
5. 工作包只做 Codex 主审和针对性测试；整个阶段实现与基础验证完成后，再运行一轮两个独立只读总审查。审查只覆盖冻结门禁、阶段增量和本阶段绑定债务；范围外发现登记到 `history/MIGRATION-DEBT.md`，不得自动扩大当前阶段。
6. 未经用户授权不 commit/push/deploy，不连接生产应用，不切换生产消费者。

## 2026-10-30 v2.2 上游盘点(现役)

本轮按「先查已有,再决定自研」逐项核对。证据来自本机只读检查(二进制字符串与版本文件、日志计数、配置文件结构,不输出任何凭据值)与官方文档、源码;决策见 ADR-0006。

| 上游 | 本机版本(2026-10-30) | 上游最新 | 本轮关注点 |
|---|---|---|---|
| Claude Code | CLI 2.1.300、VS Code 扩展 2.1.280、Desktop 内嵌 2.1.309 | 2.1.314(2026-10-30) | hooks、原生续跑的边界、Remote Control、凭据刷新与账号身份、会话登记与转录 |
| OpenCode | Desktop 2.2.3(内嵌 `opencode-cli` v2.2.3,运行时 Bun 1.4.4) | 2.2.4(2026-10-30) | v2 插件 API、加载日志、DeepSeek 密钥位置 |
| lark-cli | 1.0.81(npm 全局安装) | 1.0.98(lark-cli 自己的更新检查记录) | npm 安装形态、钥匙串与 S4U、环境变量凭据、错误信封、`doctor`、`auth list` |
| cc-connect | 1.4.1(计划任务已被用户停用) | 1.5.0(npm,2026-10-26) | 退役评估,见上文「2026-10-30 退役评估(ADR-0006)」 |
| WebView2 运行时 | 156 | — | GUI 重排所需的原生能力 |

### Claude Code:hooks

- **触发范围**:官方说明同一套 hook 事件在终端、IDE 扩展、Desktop 与云端会话里都会触发;云端会话不读本机 `~/.claude/settings.json`。Remote Control 会话跑在本机进程里,hooks 照常触发;连接期间 hook 子进程的环境里有 `CLAUDE_CODE_BRIDGE_SESSION_ID`(2.1.199+,与 claude.ai/code 链接里的会话 ID 相同)。本机旁证:2026-10-28 Desktop 日志里 37 次 Stop hook,其中 28 次在 40 秒内出现了 AI Resume 的 claudecode 通知条目(其余推断落在当时的全局退避窗口里)。
- **exec form 与 shell form**:hook 带 `args` 时走 exec form,不经任何 shell,含空格的 exe 绝对路径合法(2.1.139+);不带 `args` 是 shell form,Windows 上优先用 Git Bash,没装才用 PowerShell。官方排障建议:只运行一个 exe 的 hook 应把程序放进 `command`、参数放进 `args`。AI Resume 旧写法是带引号的整条命令 `"…\AiResume.Hook.exe" claudecode`,在回退 PowerShell 时是 ParserError(本机 pwsh 实测 `Unexpected token`),只是因为本机装了 Git Bash 才一直没暴露。
- **`StopFailure`(2.1.78+)**:回合因 API 错误结束时 `Stop` **不触发**,改发 `StopFailure`。`error` 取值为 `rate_limit` / `overloaded` / `authentication_failed` / `oauth_org_not_allowed` / `account_on_hold` / `billing_error` / `invalid_request` / `model_not_found` / `server_error` / `max_output_tokens` / `cloud_credential_error` / `unknown`(本机二进制另有 `verification_required`),另带 `error_details` 与 `last_assistant_message`(此处是 API 错误串);matcher 按 `error` 过滤,hook 的输出与退出码一律被忽略。
- **`Notification`**:官方列出 13 种 `notification_type`,载荷字段为 `message`、`title`、`notification_type`。真正在等人的是 `permission_prompt`、`elicitation_dialog`、`elicitation_url_dialog`、`agent_needs_input`,以及 `quota_auto_resume_stale`(额度已重置但电脑睡过头,等用户按回车)与 `quota_auto_resume_disabled`(不会自己继续);`quota_auto_resume_fired` 是「已自动继续」的信息类。`idle_prompt` 是回合结束约 60 秒后的提醒,与 `Stop` 重复;`auth_success`、`elicitation_complete`、`elicitation_response`、`agent_completed`、`plugin_notification` 都不是等人。`PermissionRequest` 在请求瞬间触发、没有延迟门控,官方建议用 `permission_prompt` 作延迟信号:Desktop / VS Code 约 6 秒后无条件触发(2.1.233 起),终端约 6 秒无操作才触发,`CLAUDE_CODE_DISABLE_PERMISSION_PROMPT_NOTIFY_HOOKS=1` 可关闭。
- **`stop_hook_active`**:官方含义是「Claude 正因某个 Stop hook 而继续」,用来防止**阻断型** hook 自己造成死循环;不阻断的 hook 没有递归风险。
- **`background_tasks`(2.1.145+)与 `session_crons`**:`Stop` 载荷带后台任务(id、type、status)与会话定时任务,官方用它区分「会话结束」与「暂停等后台唤醒」。
- **生效与信任**:直接编辑 settings 里的 hooks 会被文件监视器热加载,不需要新会话(几秒后 `/hooks` 里仍看不到才需要重启会话);交互式会话在用户接受工作区信任对话框之前不执行任何 settings 文件里的 hook(`-p` 与 SDK 会话视为已信任),新目录在接受信任前没有通知;同一 handler 在多个 settings 文件里定义时只运行一次。

→ AI Resume 的用法:`ClaudeCodeNotificationAdapter` 在 `~/.claude/settings.json` 写三组 exec form——`Stop`(完成)、`StopFailure`(`--kind=failure`,不写 matcher 即接收全部 error)、`Notification`(`--kind=decision`,matcher 为 `DecisionMatcher`),不订阅 `idle_prompt`、`quota_auto_resume_fired` 与 `PermissionRequest`;旧 shell form 照样认领,重新启用(`install` 对账或界面开关)时改写为 exec form;我方条目混在用户分组里时移出单独成组,不改写用户分组的 matcher;三组缺任一组或形状不对时 `Probe` 标断链。`AiResume.Hook` 对 claudecode 不再按 `stop_hook_active` 丢弃,只记录该标志,并把 `hookEvent`、`promptId`、`error`、消息哈希纳入事件 ID;`background_tasks` 里仍有非 shell 任务在跑时记 `backgroundBusy`,`CLAUDE_CODE_BRIDGE_SESSION_ID` 有值时记 `bridged`。Claude Code 完成类事件的合并(20 秒稳定期、同会话 2 分钟内后者替代前者)与后台任务未完时的暂缓(最多 30 分钟),以及各类文案,都在 `CompletionNotifier`。

### Claude Code:原生续跑能力的边界

- **`autoContinueAtUsageLimit`(2.1.234+,默认开)**:交互式会话撞上 claude.ai 用量上限后原地等待,重置后自动继续。不会自动开始等待的情况:Remote Control 与 agent team teammate 会话(需本人执行 `/rate-limit-options`,该命令不在手机端可用命令清单里,推断手机上开不了)、重置时间超过 24 小时的周额度、在用其他模型族时撞上 Opus/Sonnet 限额;后台会话与 `-p` 根本不提供等待。进程退出等待即结束,resume 后也不会恢复;连续撞限最多自动重挂 2 次;电脑睡眠超过约 30 分钟改为等用户按回车(即 `quota_auto_resume_stale`)。Desktop Code 页另有一个只作用于会话限额卡的「Auto-continue when limits reset」勾选,与 CLI 设置相互独立。
- **`CLAUDE_CODE_RETRY_WATCHDOG`(2.1.186+)**:让 `-p` 无限重试 429/529,响应带重置时间时等到重置为止;配套的 `…_MAX_WAIT_MS`(2.1.295+)、`…_MAX_USAGE_LIMIT_WAIT_MS`(2.1.301+)、`…_USAGE_LIMIT_RECHECK_MS`(2.1.305+)中,本机 2.1.300 缺后两项。
- **其它 CLI 能力**:`--bg` / `--background` 可配合 `--resume` 在后台续写同一会话;`claude agents --json` 是列出交互与后台活动会话的官方脚本接口;另有 `--fork-session`、`--fallback-model`。

→ 分工:仍打开的交互会话交给官方自动继续,AI Resume 对开着的会话不重复续跑(占用判定见下),限额重置(或中断)10 分钟后会话仍开着、尾部仍是那条中断,就发一次飞书提醒(`ResumeEngine.SessionOpenGrace`);已关闭的会话、周额度、Remote Control 会话与多项目编排仍由 AI Resume 续跑。`CLAUDE_CODE_RETRY_WATCHDOG` 未采用:它会让续跑进程长时间持有会话、改变运行时长语义,需要另立 ADR;`--bg --resume` 与 `claude agents --json` 复核也未采用(后者的字段形状待录制)。

### Claude Code:Remote Control 与手机推送

- 本机 2.1.300 有 `--remote-control` 参数。Remote Control 把本机会话连到 claude.ai/code 与 Claude App,手机上可以发消息、发图片与文件、批准权限、停止后台子代理、切换模型(2.1.238+);服务模式 `claude remote-control --spawn same-dir|worktree` 可从手机新建会话;Dispatch 可从手机给 Desktop 派任务。限制:只支持 Pro/Max/Team/Enterprise 订阅登录,不支持 API key,`ANTHROPIC_BASE_URL` 指向非 Anthropic 主机(例如 DeepSeek 兼容端点)时不可用,本机 claude 进程必须一直运行;登录本身过期后 Remote Control 与后台会话都会停摆。
- 手机推送:「Push when actions required」(`inputNeededNotifEnabled`,对权限请求与提问确定推送)与「Push when Claude decides」(`agentPushNotifEnabled`,由模型自行决定,通常在长任务完成时)默认都关;用户正在终端打字或焦点在终端时不推送;`CLAUDE_CLIENT_PRESENCE_FILE`(2.1.181+)可在人在电脑前时整段静音。本机两个开关都未设置。
- Remote Control 桥会提前 5 分钟主动续期访问令牌(`token_refresh_buffer_ms` 默认 300000),是无人值守时最可靠的续期主体(见下节)。

→ AI Resume:手机远程完全交给 Remote Control,不实现任何聊天通道。飞书通知保留,因为官方推送只覆盖已连接的 Claude Code 会话、完成推送由模型自行决定,也覆盖不到 `StopFailure` 与 OpenCode。事件带 `bridged` 时文案加「· 远程已连接」;两边都开推送时同一件事可能提醒两次,AI Resume 只管理自己的 hooks,不替用户改 `~/.claude/settings.json` 里的推送开关。

### Claude Code:凭据刷新与账号身份

(按 2.1.300 二进制与官方 authentication / errors 文档核对。)

- **凭据文件**:`%USERPROFILE%\.claude\.credentials.json`(或 `CLAUDE_CONFIG_DIR` 下同名文件)顶层只有 `claudeAiOauth` 与 `mcpOAuth`,**没有任何账号字段**。`expiresAt` 是访问令牌到期(本机实测约 8 小时);`refreshTokenExpiresAt` 是登录本身的固定到期(登录时服务端未返回则默认 30 天,每次刷新只带剩余寿命、不会延长);Claude Code 在剩 0–3 天时提示 `/login`,官方文档写明只有 `/login` 能续期。
- **刷新机制**:在 `<配置目录>/.oauth_refresh.lock` 互斥锁内进行(另有旧锁 `<配置目录>.lock`,默认即 `~/.claude.lock`,以及持有者记录 `.oauth_refresh.lock.owner`):先重读凭据,别的进程已经刷新就直接采纳;否则刷新,再以「磁盘上的 refreshToken 仍是本次提交的那一个」为条件做 CAS 写回。刷新被拒(`invalid_grant`)时把 accessToken / refreshToken 置空、`expiresAt` 置 0。refresh token 按一次性设计(官方客户端文案 "cannot be used again"),任何外部刷新都会让 Claude Code 手里那份作废,它下次刷新失败后会清空登录。CLI 与 Desktop 内嵌的 Claude Code 共用同一把锁、同一份凭据文件。
- **没有零推理成本的官方刷新命令**:`claude auth status`(含 `--json`)本身不调用刷新,它的 `loggedIn` 只表示「存有含 `user:inference` 的访问令牌」,令牌过期仍为 true;`mcp list` 会拉起全部 MCP 服务器;`auth login` 配合 `CLAUDE_CODE_OAUTH_REFRESH_TOKEN` 绕过刷新锁,还要求调用方持有 refresh token;`-p` 是一次推理。可靠的续期主体只有正在运行的官方 Claude Code:Remote Control 桥提前 5 分钟主动刷新,任何会话在发请求前也会在 5 分钟窗口内刷新。
- **`claude setup-token`**:签发约一年期令牌,但作用域只有 `user:inference`(官方文档写明只能发模型请求);usage 端点需要 `user:profile`,本机实测一律 403。这推翻了 v2.1 的 A1 方案。
- **账号身份**:全局配置的定位规则是 `<配置目录>/.config.json` 为非空文件就用它(旧版兼容),否则用 `CLAUDE_CONFIG_DIR`(或用户目录)下的 `.claude.json`;其中 `oauthAccount.organizationUuid` / `accountUuid` 是账号身份,`claude auth status` 的 orgId 读的就是同一字段。本机有两份全局配置:CLI 用 `~/.claude.json`,以 `CLAUDE_CONFIG_DIR=~/.claude` 运行的 Desktop 内嵌 Claude Code 写 `~/.claude/.claude.json`,两者共用同一份凭据文件、当前组织一致,切换账号后可能分叉。`~/.claude.json` 的 `projects` 映射里有仅大小写不同的重复路径键(PowerShell `ConvertFrom-Json` 会因此失败,`JsonDocument` 允许)。
- **Windows 凭据管理器灰度**:`CLAUDE_CODE_FORCE_WINDOWS_CREDMAN=1` 或全局配置 `cachedGrowthBookFeatures.tengu_windows_credman=true` 时,Claude Code 改把凭据写进 Windows 凭据管理器,首次写入成功后删除 `.credentials.json`。本机目前两份配置都未开启,官方文档也仍写凭据在 `.credentials.json`;一旦服务端开启灰度,只读文件的路线随即失效,S4U Worker 也读不了用户的凭据库。
- **usage 响应语义**:2.1.300 内嵌的 usage schema 说明,端点实际作答时 `limits[]` 已列出账号的全部按模型周窗口,空数组表示该账号没有任何按模型的周窗口(缓存答复不适用);另有顶层 `seven_day_opus`、`seven_day_sonnet`、`seven_day_oauth_apps`(可为 null),AI Resume 目前不解析。`-p` 的 stream-json 里 `rate_limit_event` 自带 status / rateLimitType / resetsAt。

→ AI Resume:`ClaudeCredentialSource` 以共享读写/删除方式只读凭据文件,**绝不刷新、绝不写回**,剩余寿命低于 60 秒视同过期;账号指纹 = `oauthAccount` 组织 + 账号 UUID 的不可逆哈希(按文件长度与写入时间缓存,正被改写读不出时沿用本进程上一次成功的值),读不到就为空、续跑等待,**绝不退回令牌哈希**;`ClaudeLoginVerdict` 区分 Ok / TokenExpired / LoginExpiring / LoginExpired / LoggedOut / StoreMoved,其中「被 Claude Code 清空的登录」判为 LoginExpired;`ClaudeLoginReminder` 在登录到期前 3 天、2 天、1 天、12 小时内与已过期各发一次飞书提醒(需已设置通知收件人)。续跑子进程注入的是启动那一刻的访问令牌(`CLAUDE_CODE_OAUTH_TOKEN`,环境变量里没有 refresh token,子进程无法自行续期),所以要求剩余寿命 ≥ 45 分钟(`ProviderExecutionPlan.MinimumTokenLifetime`),不足就本轮不启动。额度门禁以「一次实时答复里的 `limits[]` 就是全部按模型窗口」为依据,只看目标模型相关的窗口(`QuotaResumeProbe.Evaluate`:主窗口 + 同模型族的 scoped,认不出族的 scoped 视为相关)。

### Claude Code:会话登记与转录(续跑取证)

- **`~/.claude/sessions/<pid>.json`**:官方说明这是运行中会话的登记,用于检测并发会话与崩溃;会话退出时删除,崩溃残留在下次启动时清理。字段 schema 未文档化,本机 2.1.309 的文件含 `pid`、`sessionId`、`cwd`、`startedAt`、`procStart`、`version`、`kind`、`entrypoint`、`status`(idle / busy)、`statusUpdatedAt`、`bridgeSessionId`(Remote Control 已连接)。`procStart` 与该 PID 进程的启动时间(`Process.StartTime.ToFileTimeUtc()`)逐位相等(本机两例),上游自己也把它当作进程身份。
- **会话转录里的限额中断**:撞上用量上限时 Claude Code 写一条合成的 assistant 记录:`isApiErrorMessage=true`、`error="rate_limit"`、`apiErrorStatus=429`、`message.model="<synthetic>"`,较新版本另带 `quotaLimits.rateLimitType / resetsAt`;这类记录不带 `requestedModel`。会话会中途换模型,最近一条非合成、非 sidechain 的 assistant 记录的 `message.model` 才是会话最近实际用的模型。

→ AI Resume:`ResumeSessionCatalog.Occupancy` 要求登记里的进程还活着,且启动时间与 `procStart` 一致(崩溃残留的登记遇上 PID 复用不算占用;缺 `procStart` 时退回只看 PID 存活)——绑定会话活着 → in-use,同目录另一会话 busy → cwd-busy,读不出 → unverified;`ClaudeSessionEvidence` 只读转录尾部(先 1 MiB,最多 8 MiB)的元数据、不读正文,给出「是否被限额打断(中断 uuid、limitType、resetsAt)」与「最近实际模型」(字段形状按本机 2.1.185–2.1.308 会话录制)。登记文件 schema 未文档化,Claude Code 升级后要复核字段名。

### OpenCode 2.2.3:v2 插件 API 与日志

- **版本**:Desktop 自带的 `opencode-cli.exe` 不在 PATH 上(位于桌面版的 resources 目录),`--version` 为 v2.2.3,升级通道 `latest`。OpenCode 日志记录的版本链:1.19.5 → 2.2.1(2026-10-29 05:54 UTC)→ 2.2.2 → 2.2.3;上游 2026-10-30 已发布 2.2.4。
- **v1 插件不再运行**:官方迁移指南原话 "V1 plugin implementations do not run in V2"。AI Resume 的旧单文件插件(具名导出 async 函数)从升级后首次启动起每次都被拒:`failed to load plugin … Plugin must export a default definition with an id and an effect or setup function. (cause: SchemaError(Expected object at ["default"]))`,到 2026-10-30 共 296 条。
- **加载位置**:v2 自动扫描每个配置目录下的 `plugin/` 与 `plugins/`,收录其中的 `.ts` / `.js` 文件与子目录;单文件只是靠 legacy 自动发现被接纳,官方示例都是目录形态 `plugins/<name>/index.ts`(没有 `package.json` 时按 server → index 解析)。v2 的配置键叫 `plugins`,配置里列出的本地路径必须是目录;靠自动发现就够,再写进配置会加载两次。OpenCode 监视本地插件源文件,改动触发热重载。opencode.ai 网站上的插件与配置文档是 v1 的(键名 `plugin`、示例用 `$` 与 `session.idle`),不适用于 2.x。
- **模块与事件形状**:`export default { id, setup(ctx) }`(`Plugin.define` 只是恒等函数,不必 import);`setup` 可返回 cleanup,事件循环必须放后台并在 cleanup 里 abort。订阅用 `ctx.event.subscribe({ types?, signal? })`,返回 AsyncIterable,未知事件类型被忽略并告警;事件信封为 `{ id: "evt_…", created, type, durable?, location?, data }`,载荷在 `data`(v1 是 `properties`)。`ctx.session.get({ sessionID })` 返回含 `parentID`、`location.directory` 的会话信息。v1 的 `$` 没有对应物,官方要求插件自己管理所用的进程 API。同 id 重复加载报 `Duplicate plugin ID`。
- **事件语义**:`session.idle` 在 2.2.3 源码里标为 deprecated,服务端已不再发布(官方迁移指南的示例仍写着它——文档与实现不一致,以源码为准)。官方 TUI 与 Desktop 自己的通知判据:完成 = `session.execution.succeeded` 且没有仍在运行的直接子会话(后台子 agent 完成后会唤醒父会话);失败 = `session.execution.failed`(只对顶层);等人 = `permission.asked`(子会话也提醒,按请求 id 去重)与 `form.created`(`metadata.kind` 为 question / mcp-elicitation / websearch.provider,MCP elicitation 的 sessionID 是哨兵值 `global`)。一个忙碌周期只产生一次终态事件。
- **规模与投递**:本机 OpenCode 数据库副本里 214 个会话有 138 个是子会话,近 48 小时进入空闲的顶层会话 17 个、子会话 130 个——只对顶层通知是必须的。插件按位置(location)各起一个实例(日志里每轮加载约 5–8 次),订阅是不重放历史的实时通道,所以去重只能用上游给的唯一 id。本机另外几个按 v2 写法的第三方插件在 2.2.3 下都能正常加载。
- **运行时**:`opencode-cli.exe` 内嵌 Bun 1.4.4;插件里用 `node:child_process` 以 argv 数组启动外部 exe 可行(Bun 下 `windowsHide` 是否真正生效未在 OpenCode 内实测)。
- **日志**:`%USERPROFILE%\.local\share\opencode\log\opencode.log`(本机约 22 MB),路径里的反斜杠被双写;插件加载结果是 `loading plugin` / `failed to load plugin` 行。

→ AI Resume:`OpenCodeNotificationAdapter` 只支持 2.x,写目录形态插件 `~/.config/opencode/plugins/airesume-notify/index.ts`(`id: "ai-resume.notify"`),不写进用户的 `opencode.jsonc`。完成 = 顶层 `session.execution.succeeded` 且无运行中的直接子会话(5 秒宽限内又开跑则不报);等你 = `permission.asked`、`form.created`(question)与顶层 `session.execution.failed`(60 秒宽限内未被续跑才报)。插件以 `node:child_process.spawn`(`shell:false`、`windowsHide:true`,JSON 经 stdin)启动 `AiResume.Hook.exe`,每个子进程 30 秒超时,事件回调里不 await;OpenCode 事件的 eventId 只按 `source|上游 id|kind` 计算。「已加载」只认 OpenCode 日志末尾 4 MB 里、插件文件写入之后的本插件记录:`failed to load plugin` → 断链,`loading plugin` → 已加载,没有记录 → 待核实。启用时先写新插件,再把我方旧单文件复制为 `.bak` 后删除(先删旧再写新会出现一段没有插件的空窗);目录里有别人的 `index.*`、`server.*` 或 `package.json` 时拒绝覆盖。**待真机验证**:OpenCode 2.2.3 内真实加载新插件并在任务完成时发出通知。

### OpenCode:DeepSeek 密钥位置

`OpenCodeCredentials` 只读 OpenCode 给 DeepSeek 用的密钥:先看配置目录(`%XDG_CONFIG_HOME%\opencode`,缺省 `~/.config/opencode`)里 `opencode.jsonc` / `opencode.json` 的 `provider.deepseek.options.apiKey`(支持 `{env:VAR}` 与 `{file:路径}`),再看数据目录(`%XDG_DATA_HOME%\opencode`,缺省 `~/.local/share/opencode`)里 `opencode auth login` 写入的 `auth.json`,两处都没有才回退旧的 `DEEPSEEK_API_KEY` 环境变量,来源如实标出;配置把 DeepSeek 指到非官方端点时不发余额请求。DeepSeek 行 `resumable=false`,只展示余额、不参与续跑,AI Resume 不再保存任何 DeepSeek key。

### lark-cli 1.0.81

- **npm 安装形态与 cmd.exe 截断**:PATH 上是 npm 生成的 `lark-cli.cmd` 垫片,真正的官方二进制在 `node_modules/@larksuite/cli/bin/lark-cli.exe`。v2.1 的代码以为「Windows 上没有 .exe」,经 `cmd.exe /c lark-cli.cmd` 启动。本机按生产完全相同的方式(.NET `ArgumentList` + `cmd.exe /c` + 同结构的回显垫片)复现:参数在 `--text` 的第一个换行处被截断(LF 与 CRLF 都一样),其后的 `--idempotency-key`、`--format` 全部丢失;直接启动 exe 时参数完整。`--dry-run` 证实 `--idempotency-key` 映射为请求体的 `uuid`,多行文本原样保留。已知取舍(调研判断):直接启动 exe 会绕过垫片 `run.js` 的自更新恢复逻辑,自更新中断时表现为找不到 exe,重新安装 lark-cli 即可。
- **钥匙串与 S4U**:Windows 上 appSecret 存在 `HKCU\Software\LarkCli\keychain\lark-cli`,是 CurrentUser 范围的 DPAPI 密文(源码中 `CryptProtectData` / `CryptUnprotectData` 的 dwFlags=0);`~/.lark-cli/config.json` 里记录的是 `appSecret.source = keychain`(实值在钥匙串)。每次 `--as bot` 都要先解密再换 tenant_access_token(推断 1.0.81 不缓存 TAT)。在 S4U 计划任务的会话 0 里这一步间歇报 `0x8009000B`:本机 Microsoft-Windows-Crypto-DPAPI/Operational 日志(覆盖 10-14 以后)与 Worker 投递日志逐条对齐,312 次失败投递里 300 次(96%)对得上,每条失败恰好伴随 2 次 DPAPI 失败,全成功的轮次一次也没有。lark-cli 自己的 `~/.lark-cli/logs/auth-*.log` 只记 OAuth 设备流,不记发送失败。1.0.90 起加入了存进钥匙串的 tenant access token,升级前必须复核是否重新引入 DPAPI 依赖。
- **环境变量凭据提供者**:lark-cli 读取 `LARKSUITE_CLI_APP_ID`、`LARKSUITE_CLI_APP_SECRET`、`LARKSUITE_CLI_BRAND`、`LARKSUITE_CLI_STRICT_MODE` 等变量(源码 `internal/envvars`、`extension/credential/env`)。本机用假值实测:设置后 `auth status` 返回 "credentials are provided externally",`im +messages-send --dry-run` 输出的 `context.app_id` 正是环境变量里的假 app id——环境变量凭据优先于配置与钥匙串。**但 env 来源只接受现成令牌**(2026-11-02 补测,ADR-0007):只给 `APP_ID` + `APP_SECRET` 时它不去换令牌,`api … --as bot` 立即 `authentication/token_missing`、doctor 报 `no tat available from credential source "env"`(网络接到只记录的代理:env 路径一次换令牌请求都没发);改给 `APP_ID` + 假 `LARKSUITE_CLI_TENANT_ACCESS_TOKEN`,同一调用拿到飞书侧 99991663(带 log_id),在用户真实配置目录下同样如此,发消息路径也一样。二进制里有报错串「TENANT_ACCESS_TOKEN is set but LARKSUITE_CLI_APP_ID is missing」与哨兵 `__LARKSUITE_CLI_TOKEN_ONLY__`。dry-run 不换令牌,证明不了凭据可用。官方 lark-shared Skill 建议机器调用时设 `LARKSUITE_CLI_NO_UPDATE_NOTIFIER=1` 与 `LARKSUITE_CLI_NO_SKILLS_NOTIFIER=1`。
- **错误信封**:成功信封写 stdout(`{ok:true, identity, data}`);**错误信封写 stderr**,此时 stdout 为空,字段有 `identity`、`type`、`subtype`、`code`、`message`、`hint`、`log_id`、`retryable`(以及 `retry_after_seconds`),另有可忽略的 `_notice`。退出码按类型区分:validation=2,authentication / authorization / config=3,network=4,api=1,policy=6,internal=5,confirmation=10(官方 `errs/ERROR_CONTRACT.md` 与 lark-shared Skill)。本机录制:未知命令 exit=2、stdout 空、stderr 为 `{ok:false, error:{type:"validation", subtype:"invalid_argument", …}, _notice:{update:…}}`。`message` 只是说明文字,不是稳定契约。
- **`doctor`(Risk: read)**:输出 `{checks:[{name,status,message,hint?}], ok, workspace}`,检查项含 `config_file`、`app_resolved`、`bot_identity`、`identity_ready` 等;不加 `--offline` 时在线核对机器人身份(env 来源下用注入的令牌去飞书核验;只给 App Secret 时一律 `no tat available from credential source env`,与密钥对错无关)。配置目录里没有 `config.json` 时 doctor 在 `config_file` 处就停下,而 `api` / 发消息不需要它。
- **`auth list --json`**:输出 `[{appId, tokenStatus, userName, userOpenId}]`,即 lark-cli 里已登录的用户。open_id 按应用隔离,只有与发送用的机器人属于同一个应用时才能收到消息——从这里取收件人比手抄更不容易错配,但前提是 lark-cli 配置的应用就是机器人所在的应用。

→ AI Resume:`LarkCliNotificationChannel` 只直接启动 exe——逐个检查 PATH 目录(最后兜底 `%APPDATA%\npm`)里的 `lark-cli.exe`,以及同目录 `node_modules\@larksuite\cli\bin\lark-cli.exe`(npm 全局安装时它与 `.cmd` 垫片同目录),找不到就是通道级 `launch_failed`,**绝不回退 cmd.exe**;有 `FeishuBotCredentials` 的 LocalMachine 副本(`state\secrets\feishu-bot.bin`,ACL 收紧到当前用户 + SYSTEM + Administrators)时,由 `FeishuTenantToken` 用它向官方端点换 tenant_access_token(进程内按到期缓存),只把 App ID + 令牌经上述环境变量注入,并让 lark-cli 用私有配置目录 `state\lark-cli`,App Secret 绝不进子进程环境;换不到令牌就不启动 lark-cli;副本在却读不出时拒发(`credential_unreadable`),不改用钥匙串;根本没有副本才退回钥匙串,日志与界面如实标 `credentialSource`(ADR-0007);`LarkCliInvoker` 以 UTF-8 读两条流,错误信封按「先 stderr、后 stdout」解析;`NotifyClassifier` 把退出码与信封映射为条目级或通道级失败,换令牌这一步的失败另有分类。`NotifyControlPlane` 保存凭据前真实换令牌、再以令牌调一次机器人信息接口,两步都过才落盘;诊断在有副本时同样换令牌 + 调机器人接口、再附 doctor,只有钥匙串时才只用 doctor;用 `auth list --json` 列出候选收件人(目前不按 appId 过滤,由用户在「飞书通知设置」里选定),收件人只接受规整后的 `ou_` open_id。2026-11-02 真机:由 S4U 计划任务拉起的会话 0 Worker 经副本真实送达,当天 DPAPI 运行日志没有新的失败。

### 飞书 OpenAPI(消息发送)

- 发消息的 `uuid` 在 1 小时内至多成功发送一条,最长 50 字符。AI Resume 的事件 ID 是 16 位十六进制,直接作 `--idempotency-key`;但只有参数真正送达(见上)去重才生效,结果不明的失败超过 1 小时后重试仍可能重复。
- 频控:每个应用每分钟 1000 次、每秒 50 次;发给同一用户 5 QPS。常见错误码:230020 触发频控,99991400 请求过于频繁(HTTP 429,响应头 `x-ogw-ratelimit-reset` 给出恢复秒数),99991663 / 99991665 token 无效,99991672 缺少权限,10014 应用已停用,230006 未开启机器人能力,230013 用户不在可用范围或已离职,230053 用户关闭了机器人消息,230001 参数错误。

### WebView2 运行时(GUI 重排)

- 本机 WebView2 运行时 156(用同版本无头 Edge 探测):popover、invoker commands、focusgroup、anchor positioning、`appearance: base-select`、`text-wrap: pretty / balance`、`@starting-style`、`dialog closedby` 均可用。现役 `index.html` 只用到其中的 `popover`(菜单栏用 HTML 实现,不用 WPF Menu,与面板共用字体、色板与 RPC)、原生 `<dialog>`(`showModal`)、容器查询与 `text-wrap`。
- `CoreWebView2Settings.AreBrowserAcceleratorKeysEnabled` 与 `IsZoomControlEnabled` 默认都开:F5 / Ctrl+R 会重载页面,Ctrl± / Ctrl+滚轮会改缩放,点阵字随之失去整数倍;`MainWindow` 把两者都关掉。

### 待核实与未核实

- **待真机核实**:claude.ai 用量上限触发的 `StopFailure` 的 `error` 是否为 `rate_limit`;Desktop 会话连着 Remote Control 时是否算官方所说「不自动开始等待」的 Remote Control 会话;AskUserQuestion 在终端与 Desktop 下是否触发 `permission_prompt`;`agent_completed` 是否与后台会话自身的 `Stop` 重复;`/rate-limit-options` 能否在手机端执行。
- **未核实**:`claude auth status --json` 的整个进程是否会经旁路(遥测、托管策略)触发官方刷新(静态判断对个人订阅不会,未真机执行);refresh token 每次刷新是否轮换(按官方文案与 CAS 设计如此,本机未在自然刷新前后比对复核);用目录 ID 作 `--model` 续跑时,能否保留会话原来用带 `[1m]` 别名选中的 1M 上下文变体;Bun 下 `windowsHide` 在 OpenCode 内是否生效;lark-cli 用环境变量凭据时在 S4U 下是否完全不碰 HKCU 钥匙串(推断如此,需上线后看 DPAPI 运行日志确认)。

### 结论(自研边界)

1. **上游已有,删掉自有实现**:手机远程交给 Remote Control / Dispatch / Desktop 定时任务,cc-connect 退役;setup-token 读不到额度,回到只读 Claude Code 自己的登录;AI Resume 自持的 DeepSeek key 让位于 OpenCode 里的密钥。
2. **按官方契约薄适配,直接读官方产物**:hooks 用 exec form 并订阅 `StopFailure`;OpenCode 用 v2 插件 API,加载证据读它自己的日志;lark-cli 用环境变量凭据来源(注入 AI Resume 自己换来的 tenant_access_token,ADR-0007)、stderr 错误信封、`doctor` 与 `auth list`;续跑的占用、义务与最近模型分别读 `sessions/<pid>.json` 与会话转录尾部;账号身份读 `oauthAccount`。
3. **仍需自研(上游没有,或在目标平台不可用)**:已关闭会话、周额度、Remote Control 会话与多项目的限额后续跑编排;覆盖 `StopFailure` 与 OpenCode 的确定性飞书通知;投递的分级退避、通道熔断与死信(lark-cli 只负责发一次)。
4. **看过但未采用**:`CLAUDE_CODE_RETRY_WATCHDOG`、`claude --bg --resume`、`claude agents --json` 复核、用 Remote Control 推送代替飞书——理由见上,要改变行为需先形成新 ADR。
