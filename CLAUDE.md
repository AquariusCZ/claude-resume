# 与本仓库协作的约定

- **语言:始终用中文和我沟通**(所有回复、总结、说明都用中文)。代码注释沿用文件已有风格。

## 文档与版本真身

- 当前大版本为 **AI Resume v2**;版本号唯一真身是 `src/package.json`。Codex app-server 客户端从该文件读取版本,禁止另写硬编码版本。
- `README.md` 负责用户使用/安装,`docs/ARCHITECTURE.md` 负责现役机制与完整配置,`AI_GUIDE.md` 负责飞书只读问答,`docs/LESSONS.md` 只放历史教训与仍有效的工程经验。
- 跨 provider、会话 schema、GUI 流程、部署或安全边界的大改,必须同步上述受影响文档并刷新 `AI_GUIDE.md` 第一行的 project-tour 时间标记;历史说法不得继续冒充现役行为。
- **现役/目标必须区分**:现役实现仍是 PowerShell 5.1 + Node `feishu-agent.js` + JSON/marker 状态,SQLite/C# 尚未实现;迁移方向以 `docs/adr/0001-target-architecture.md` + **`docs/adr/0003-cc-connect-direct-and-control-plane.md`(修订 0001 的 cc-connect 边界,冲突处以 0003 为准)** 为准,AI 运行生命周期以 `docs/adr/0002-run-lifecycle-contract.md` + `docs/RUN-CONTRACT.md` 为准,恢复现场、阶段基线、状态所有权、事件契约、债务和当前门禁分别见 `docs/RECOVERY-AUDIT-20260801.md`、`docs/MIGRATION-BASELINE.md`、`docs/STATE-OWNERSHIP.md`、`docs/EVENT-CONTRACTS.md`、`docs/MIGRATION-DEBT.md`、`docs/STAGE-1-GATE.md`。

## 目标架构(已确认,渐进迁移)

- `cc-connect` **直接运行,不再由 wrapper 包装**(ADR-0003):它负责飞书/多平台协议、会话编排与持久化、agent 与 turn 生命周期、停止(`/stop` 经 bridge)、限额读取、cron、崩溃恢复、Web admin;`lark-cli` 与官方 `lark-*` Skills 是目标飞书 OpenAPI 能力层。**核心原则:接受 cc-connect 的用法约定,适配而非改造**——用法差异(`send` 不承载停止、provider 切换走管理 API、配置变更需重启而非 reload)一律适配。
- **AI Resume 只做四件事**(ADR-0003 §2.2):① Claude 限额后自动续跑编排(唯一不可替代的核心——cc-connect 只读取 `LimitReached`,不做排队续跑);② 动态项目发现;③ 本地完成通知(可配置注册表);④ Windows 控制面 GUI(AI Resume 退化后 GUI 即主要用户界面,质量即产品质量)。限额数据**自行获取**(ADR-0003 §2.3 已按证据推翻原「消费 cc-connect `UsageReport`」的判断:它依赖 `creack/pty`,`pty_unsupported.go` 的构建约束命中 Windows,管理 API 也没有 usage 端点)。取数主路径是官方 `GET https://api.anthropic.com/api/oauth/usage`,复用 Claude Code 已有的 OAuth token(`%USERPROFILE%\.claude\.credentials.json`),**只读、绝不刷新、绝不写回**——刷新会与 Claude Code 争用 refresh token;token 剩余寿命 < 60 秒视同过期。失败才降级到 `ClaudeCodeProbe` 子进程探测。
- 现役生产链路在迁移完成前仍是 Node `feishu-agent.js`;禁止让它与 cc-connect 同时消费同一个生产飞书应用,也禁止两边同时写同一会话/任务状态。cc-connect 只能先用独立测试应用 + 测试项目做兼容性验证,通过后一次性切换唯一运行真身。
- 新增通用飞书消息/文档/日历/任务/OpenAPI 能力前,必须先检查官方 lark-cli 与对应 Skill;已有官方命令时优先调用/封装它,不得继续手写同类 SDK 请求。入站事件编排、AI Resume 自有状态和安全边界不因使用 CLI 自动消失。
- `lark-*` Skills 只使用用户级 `%USERPROFILE%\.agents\skills` 真身及现有 Codex/Claude Code/Cline/Copilot 桥接,禁止复制进仓库。lark-cli 的用户/bot 身份、scope、risk level、结构化错误和高风险确认契约必须原样保留。
- 迁移设计与固定上游快照见 `docs/UPSTREAM-ARCHITECTURE-RESEARCH.md`;任何改变该目标、引入双写、wrapper→Go fork 或决定整体替换/回退的方案都必须先形成新 ADR 并经用户确认。
- 目标 C# Worker **只承担 AI Resume 自有职责**(ADR-0003 §4 缩小了原范围):续跑编排、项目发现、完成通知 outbox、进程监督(`ProcessSupervisor` 用于自己启动的进程)与 GUI 后端;**不再镜像 cc-connect 内部的会话/授权/turn 状态机**。ADR-0002 的 RunContract 仍适用于 AI Resume 自己启动的进程(续跑、探测),不再要求映射 cc-connect turn。AI 生成和健康探测不设客户端总时限,每 15-30 秒读持久状态与进程存活性;静默指标不得触发失败。

## 部署(飞书 Node 机器人)

改完 `src/feishu-agent.js`、`src/session-manager.js` 或 `src/ai/*.js` 后,线上机器人**不会**自动生效——它从 `%LOCALAPPDATA%\ClaudeResume\` 运行:

1. 把 `src/feishu-agent.js`、`src/feishu-runtime.js`、`src/channel-adapter.js`、`src/authorization-policy.js`、`src/completion-events.js`、`src/conversation-store.js`、`src/task-orchestrator.js`、`src/session-manager.js`、`src/completion-notify.js`、`src/install-completion-hooks.js` 和整个 `src/ai\`(含 `agent-adapter.js`)复制到 `%LOCALAPPDATA%\ClaudeResume\`(或重跑 `src/install.ps1`);`feishu-agent.js` 只是稳定 wrapper,缺少 `feishu-runtime.js` 必须 fail-fast;完成通知适配器还必须由 `install-completion-hooks.js` 合并进用户级 Codex/Claude Code/Cline hooks,不得覆盖既有 hook;
2. 只定位命令行包含当前 AppDir `feishu-agent.js` 的 `node.exe` PID,用 `taskkill /PID <pid> /T /F` 结束它和全部 AI 子进程(不得误杀其他 Node 服务),并逐个确认旧 PID 明确 gone;VBS 守护循环约 8 秒内自动重启;
3. 验证:安装器先确认固定 `@larksuiteoapi/node-sdk@1.70.0` 与原生 `onReady` 契约;node 进程应恰为 1 个,且停止位置之后的 `logs\feishu-stdout.log` 新增本次安装 generation 对应的结构化 `AI_RESUME_AGENT_BOOT` 与 `AI_RESUME_AGENT_READY`。SDK 通用 `ws client ready` 文本不能证明进程代次;taskkill/CIM/SDK/唯一进程/generation READY 任一不可确认即返回非零,不得继续报告安装成功。

GUI(`src/picker.ps1`)、共享配置库(`src/lib.ps1`)和健康探测入口(`src/provider-health.js`)同理复制到该目录(或重跑 `src/install.ps1`),改动在**下次打开窗口**时生效。已打开的 WPF 窗口不会热更新,部署 GUI 后必须关闭旧窗口再打开。

## GUI 服务状态语义

- OpenAI / DeepSeek / Claude 的绿色「可用」只能来自启动时或手动刷新的**真实最小请求成功**;API Key 已填写、CLI 命令存在只能说明“可探测”,绝不能显示成可用。
- Claude 探测必须区分未登录、订阅/额度、网络/超时、模型不可用和未安装;探测失败时额度区不得回退成「空闲」。
- **现役兼容行为**:GUI 默认模型和飞书模型卡都只能暴露最近一次真实探测成功的 provider;飞书使用 provider→model 两级选择,旧卡和文字命令也必须复用同一可用性校验,不可通过静态 profile id 绕过。OpenAI/DeepSeek 的真实探测先按大小写不敏感规则清除进程代理做直连,现役 Node 仅在 `transient` 网络类失败且配置了 `aiProxy` 时尝试备用代理;认证、额度、模型和命令错误禁止换线。成功线路随健康快照缓存 5 分钟,失败只负缓存 30 秒;密钥、端点或代理配置变化必须用哈希指纹立即作废旧线路。`childPending=true` 时禁止再次探测,真实 close 后通过 `waitForIdle` 清理临时目录并立即使快照过期。正式任务固定使用该线路且不得在任务中途换线重放;等待探测期间用户停止或现役 legacy deadline 到期都阻止正式子进程启动。`aiProxy` 不应用于 Claude。**目标 C# HealthProbe 不设置客户端总时限;DNS/TCP/TLS/reset 与监控异常归 `failed_local`,不得因静默或本地计时器触发 provider fallback/重放。**
- `-SelfTest`、`-SessionSelfTest`、`-RenderTo`、`-AISettingsRenderTo` 禁止发真实探测,只显示「待检测」;OpenAI/DeepSeek 成功状态必须区分「直连可用」和「代理可用」,双线路网络失败显示「代理异常」。改服务状态逻辑后必须同时跑 GUI 自测、`node test/ai-providers.js`、`node test/provider-live.js`,并在部署后的真实窗口核验三行状态。

## 自测(改完必跑,别让用户在飞书里试错)

- 离线快测(秒级):`node test/ai-providers.js`(提供商/降级/任务超时、登记损坏和 shutdown 锁红线)、`stage1-recorded-equivalence.js`(冻结的移动前 SHA/时间 fixture,ACK/消息/状态/provider attempt 等价,默认只读且强制 no-AI)、`channel-adapter.js`(SDK fail-fast、请求级 timeout/retry、幂等 UUID、立即 ACK、同 key 保序/跨 key 并发、v2 回退)、`task-orchestrator.js`(同步预占、accepted-before-spawn 取消、fallback/停止/预检取消、shutdown 和 legacy deadline 兼容)、`agent-adapter.js`(单 attempt 透传、session 模式、事件脱敏、观察回调隔离、无 deadline/fallback/retry)、`session-manager.js`(14/30 天规则、归档恢复、工作会话保护)、`conversation-store.js`(聊天/项目/query 状态、显式项目列表 fail-closed、legacy/full round-trip、稳定 ID、mark/clear 和 I/O 容错)、`routing.js`(路由/劫持/按人 AI/pre-spawn stop)、`card-flow.js`(含隐藏项目旧卡拒绝)、`session-pick.js`、`authorization-policy.js`、`menu-authorization.js`、`concurrency.js`、`image-send.js`、`progress-image.js`、`completion-events.js`(malformed 隔离、generation 去重恢复)、`completion-hooks.js`、`icon-asset.js`,以及 `powershell -File test/install-deploy.ps1`(运行时依赖清单、taskkill/CIM/唯一重启/ready fail-fast、缺失模块、同内容锁定跳过、失败回滚、解锁后部署)。
- 本地完成通知采用**用户可开关的适配器注册表**(ADR-0003 §3),准入红线不变:**只接受代表整个 agent 任务结束的边界**,代表单次模型请求/流式分片结束的回调一律拒绝。当前 5 个已验证 provider:Codex(`notify`)、Claude Code(`Stop` hook)、Cline(`TaskComplete`)、**Qoder**(`~/.qoder/settings.json` 的 `hooks.Stop`,多级合并不覆盖;**脚本必须检查 stdin 的 `stop_hook_active` 并在其为 true 时立即 exit 0,否则触发阻断→重试无限循环**)、**OpenCode**(`~/.config/opencode/plugins/` 的 `session.idle` TS 插件)。未启用的 provider 不写入任何 hook 配置;卸载须干净移除且不破坏用户既有 hook。`DeepSeek V4 for Copilot Chat` 的 provider 回调只代表单次模型请求结束,不得冒充整个 Copilot Agent 任务完成。Codex 只允许有本地持久化 rollout 的顶层 thread 入队:未持久化/ephemeral turn 和已持久化 subagent 均拒绝,用户主动 fork 的独立顶层 thread 保留;`Documents\Codex\<日期>\<slug>` projectless 生成目录不得冒充项目通知(该目录内明确存在 Git 根时除外)。适配器只写 AppDir 事件队列,飞书 agent 负责项目动态识别、七天去重和失败重试;禁止硬编码项目数量/名单。AI Resume 自己启动的探测、飞书任务和后台续跑必须带 `AI_RESUME_INTERNAL_RUN=1`,避免重复通知。
- GUI 自测:`powershell -NoProfile -ExecutionPolicy Bypass -File src\picker.ps1 -SelfTest`(通过真实按钮事件打开模型设置窗,校验 provider 分组、逐项切换 8 个模型、模拟 Claude 不可用整组隐藏并检查右侧 Chip 不裁切)、`-SessionSelfTest`(打开真实会话管理窗)、`-RenderTo <png>`(主窗口离屏截图)、`-AISettingsRenderTo <png>`(AI 设置窗离屏截图)。任一弹窗解析/布局断言失败必须返回非零退出码。
- 真 API 冒烟:`node test/provider-live.js`(默认验证 GPT-5.6 Sol、DeepSeek V4/V4 Pro;只发无工具 `OK` 请求,不读项目;OpenAI 还必须在隔离的非 Git cwd 中续接第二轮,防新建/resume 参数漂移)。
- 真跑 AI 的 e2e(改了查询/安全逻辑时跑):`query-security.js`、`chat-security.js`。它们只读生产配置基线,通过 `keepSecrets` 仅把顶层 `openaiApiKey`/`deepseekApiKey` 注入当前测试进程环境,`aiProxy` 与其余凭据一律不写临时 JSON,飞书 canary 只写临时 config;不备份、不写入、不恢复真实 `config.json`。`query-security` 还必须用 junction guide canary 证明 provider 启动前 fail-closed。两项测试使用合成 chat/owner/project 与临时 Codex/Claude home,每 15 秒报告状态并等待结构化终态,静默不判超时。
- 机制:`FEISHU_TEST=1` = mock 飞书 client(不联网、不占锁),并要求显式 `FEISHU_TEST_STATE_DIR`/`FEISHU_TEST_CONFIG_PATH`;所有 Feishu Node 测试统一经 `test/feishu-test-config.js` 创建系统 temp 直接子目录、PID+nonce owner marker、临时 config/state/AppDir、`USERPROFILE`、`CLAUDE_CONFIG_DIR` 和 `CODEX_HOME`。helper 对真实 config 只做逐组件 no-reparse 只读和 SHA256 前后比对,递归清空嵌套凭据;marker 不匹配、树含 reparse 或检查异常时宁可残留拒绝递归删除。`FEISHU_TEST_NO_AI=1`(兼容旧名 `FEISHU_TEST_NO_CLAUDE=1`) = 桩掉全部 AI。
- 改飞书事件/卡片状态机时,`session-pick.js` 必须覆盖:慢会话枚举不阻塞 ACK、handler 同步前缀也不阻塞 dispatcher、同聊天事件严格保序、同卡加载态的最终顺序、旧卡被删后的加载/最终页只保留一张替代卡、替代窗口连续导航仍只一张、picker 双击只消费一次、卡片 patch 永久 pending 的超时降级、取消后无过期写入、项目 A 旧卡不能污染或操作项目 B、延迟摘要不能跨项目/AI 发送、文字推高卡片前后的可见性代次与排队顺序、会话读取失败不冒充空历史。控制卡导航必须统一经 `enqueueControlCard`;独立模型/进度/过期/结果卡不得提升为 `lastCard`。`image-send.js`/`progress-image.js` 必须覆盖失败图片保留、下载落盘超时清理、真实 `post` 语言包结构中的多图+文字/富文本标签/图片去重、按 chat+sender 隔离、后到图片不串入前一请求、忙碌回滚、最终请求 6 图/单图 10MB 上限(含暂存+post 合并)、部分下载失败、allowlist 未命中/缺失身份/配置读取失败 fail-closed、告警失败不阻断主请求、运行后清理及 24h 阈值+每小时扫描的磁盘/内存孤儿清理、进度卡不抢卡、慢 tick 不能覆盖完成态及 tick 超时后的新完成卡兜底。

## 测试红线(真踩过的事故,绝不重犯)

- **测试绝不能对真实项目/真实会话启动任何 AI 修改运行**——曾经一个测试 resume 了真实会话,AI 带着旧上下文执行并 push 了 commit;"事后停止"拦不住。要么用不存在的假会话 id,要么设 `FEISHU_TEST_NO_AI=1`。
- **mock 必须照抄线上 API 的真实返回结构**(如 `im.image.create` 返回顶层 `{image_key}`,无 `data` 外壳)——mock 猜错结构 = 测试全绿但线上静默失效。拿不准就真调一次打印 `Object.keys()`。
- **现役 Node 兼容事实**:项目修改/一次性执行/后台续跑无总时限,查询/闲聊仍有 30 分钟 legacy deadline,Stage 1 不改变生产行为。**目标 RunContract**:chat/query/modify/resume/probe 全部不设客户端总时限,采用 Start/Status/Cancel;只有结构化 HTTP 408/504/`gateway_timeout` 是 `failed_provider` 超时,DNS/TCP/TLS/reset、进程消失和监控异常是 `failed_local`;用户停止为 `cancelled` 且不得 fallback。`heartbeatAt`、`lastOutputAt`、`silentSeconds` 仅为指标。`perProjectTimeoutMinutes` 等旧字段不得进入目标 C# 协议。
- 无总时限任务的进程边界必须同时覆盖正常退出和不可捕获崩溃:活跃 AI 子进程用临时文件+fsync+替换写入 AppDir 的 `feishu-ai-children.json`,包含父/子 PID、`runKey/taskKind`、启动时间和 provider。首次登记失败必须立即终止并拒绝任务,真实 close/error 前保留运行锁,后台继续重试落盘。下次启动只有父 agent PID、PID、5 秒内启动时间、provider 对应 Codex/Claude 命令签名都匹配时才能回收,禁止只凭 PID 杀进程。CIM/`taskkill` 未确认成功时必须恢复同 `runKey` 占位锁并每分钟重试;旧格式无 `runKey` 时全局禁止修改任务。超时/停止可在终止宽限期后结束消息等待,但只能在真实 close/error 后释放运行键和注销 PID。agent 内的 provider 健康探测复用同一 runner/登记表。
- PowerShell 后台自动续跑另用 `armCycleId` / `state.cycleId` 隔离每次布防周期。Node 与 PowerShell 写 `config.json` 必须共用 `config.json.write.lock`,在锁内重新读取最新配置后只修改本次负责字段,再 fsync + 原子替换;禁止锁外读旧快照后整体写回。后台在 spawn 前先向 `checker-ai-child.json` 写 `launching` 意图,启动后升级为含父/子 PID、周期 runKey、项目和启动时间的 `active` 登记;完整 `.tmp-*` 也是恢复候选。CIM 探测必须区分 found/gone/failed,只有明确 gone 才能删登记;启动时间、父 PID、命令签名任一不可核验都 fail-closed。解除只停止后台自动续跑,不影响飞书任务。计划任务必须设置 `ExecutionTimeLimit=PT0S`,否则 Windows 默认 72 小时仍会截断无上限运行。

## 安全约束

- 只有 `feishuAuthOpenIds`(full)里的用户能**修改**项目;其他人自动只读;闲聊对所有人开放。
- **非 owner 的查询/闲聊必须禁全部文件工具**(plan 模式拦不住"读",能读到 `config.json` 并借「解锁」提权——已实测)。改查询/闲聊的工具配置后必跑 `query-security.js` + `chat-security.js`。
- OpenAI / DeepSeek / 飞书机密只放在 AppDir 下 gitignore 的 `config.json`,**绝不**进仓库、日志或测试输出。
- `feishuAuthOpenIds` 为空 = 未锁定(所有人可改),移除最后一个 full 用户会解锁,需警告。
- Fable 5 仅 owner 可用(按钮不展示、命令拒绝、运行时封顶三层)。

## 会话生命周期

- 飞书闲聊和只读查询按 `updatedAt` 计算:14 天未使用归档,30 天未使用永久删除;默认每 6 小时检查一次。
- 项目工作会话绝不自动归档或删除,只能由用户在 GUI「会话」窗口手动操作。
- Claude 归档必须同时移动 `<sessionId>.jsonl` 与同名 artifact 目录;Codex 必须走 app-server 的 `thread/archive` / `thread/unarchive` / `thread/delete`,不能只删本地引用。
- GUI、飞书命令和自动清理必须共用 `src/session-manager.js`,禁止再复制一套哈希/删除逻辑。
