# AI Resume 全量迁移 · 阶段 0 文档基线(MIGRATION-BASELINE)

> 状态:阶段 0 交付物之一,2026-08-01 定稿。本文只记录**已验证**基线,不夸大、不冒充已实现;本文不修改代码/配置。
> 归属:受控文档实现工执行,OpenAI Codex 架构监督;DeepSeek V4-flash 为主要开发执行器——这是开发流程分工,不是产品运行时密钥或依赖写入仓库。

## 1. 目的与范围

为「AI Resume 全量迁移」固化阶段 0 文档基线:

- 记录当前**现役**运行拓扑、版本与状态文件类型(JSON/marker/PowerShell+Node,**SQLite/C# 尚未实现**);
- 记录 2026-08-01 已通过的测试证据与已知风险;
- 定义阶段 0 完成判定与外部前提(独立测试应用);
- 供阶段 1+ 的 C# Worker/WPF/SQLite/cc-connect 迁移作为对照基线。

相关文档:恢复后的双工作区/安装目录/进程证据与执行顺序见 `docs/RECOVERY-AUDIT-20260801.md`,状态所有权见 `docs/STATE-OWNERSHIP.md`,事件/命令契约见 `docs/EVENT-CONTRACTS.md`,运行生命周期见 `docs/RUN-CONTRACT.md` 与 `docs/adr/0002-run-lifecycle-contract.md`,方向决策见 `docs/adr/0001-target-architecture.md`,上游研究见 `docs/UPSTREAM-ARCHITECTURE-RESEARCH.md`。

## 2. 执行边界(阶段 0)

- 只允许新增/修改任务列出的文档;禁止修改 `src/`、`test/` 或其他文件。
- 不 commit/push/deploy;不启动服务/AI/网络命令;不读取 AppDir `config.json` 或任何密钥/令牌。
- 保留工作区既有未提交改动(completion-events、conversation-store 拆分及相关文档)。

## 3. 现役运行拓扑(2026-08-01 已验证)

- 操作系统:Windows 11 Pro 10.0.26200 x64。
- GUI 与续跑引擎:Windows PowerShell 5.1(WPF/XAML),`src/picker.ps1` + `src/checker.ps1` + `src/lib.ps1`,经 `.vbs` 隐藏启动与计划任务 `ClaudeResumeChecker`(每 2 分钟,`ExecutionTimeLimit=PT0S`)运行。
- 飞书双向 agent:Node.js `src/feishu-agent.js`(**唯一生产飞书消费者**),`@larksuiteoapi/node-sdk` WSClient 长连接;由 `feishu-launch.vbs` 守护(约 8 秒自动重启)。
- 本地完成通知 hook:Codex `notify`、Claude Code `Stop`、Cline `TaskComplete` 三个可靠边界;适配器写 AppDir `completion-events\`,由 `feishu-agent.js`(经 `src/completion-events.js`)动态识别项目并投递;`DeepSeek V4 for Copilot Chat` 不接入完成边界。
- 运行态目录:`%LOCALAPPDATA%\ClaudeResume\`(AppDir);源码仓库 `C:\Users\<you>\Desktop\claude-resume`;改动需部署到 AppDir 才生效。
- 阶段 0 初始取样时 Git:main 分支 HEAD `6826704`;迁移相关改动(含 conversation-store/completion-events 拆分)**未提交**。失控执行后的现行双工作区状态不得用本行覆盖,以 `RECOVERY-AUDIT-20260801.md` 为准。

## 4. 版本基线表(2026-08-01 已验证)

| 组件 | 版本 / 标识 | 验证状态 | 说明 |
|---|---|---|---|
| Windows | 11 Pro 10.0.26200 x64 | 已验证 | — |
| .NET Runtime | 8.0.25(runtime + WindowsDesktop runtime) | 已验证 | **没有 SDK**;winget 官方源可见 `Microsoft.DotNet.SDK.10` `10.0.302`;按基线**阶段 2** 才安装固定 SDK |
| cc-connect | 1.4.1(commit `5d4c96dd`) | 已验证 | daemon 未安装;当前无 cc-connect 进程;源码研究快照与二进制非同一提交,阶段 4 试点前以实际二进制复核 |
| 生产飞书消费者 | Node `feishu-agent.js` | 已验证 | 唯一消费者;`logs\feishu-stdout.log` 出现 `ws client ready` |
| lark-cli | 1.0.81 | 已验证 | 原生 exe SHA256 `7F41992F5FD4021F7CBE3AD4CFEF405DC69853D2777F4ADFF65F1AD7F8541E51`;27 个用户级 `lark-*` Skills;尚无独立测试应用授权 |
| Codex | 0.145.0 | 已验证 | — |
| Claude Code | 2.1.202 | 已验证 | — |
| Node.js | 24.18.0 | 已验证 | — |
| npm | 11.16.0 | 已验证 | — |
| Git | main HEAD `6826704` | 已验证 | 迁移改动未提交 |
| 产品版本 | AI Resume v2(`src/package.json` 唯一真身) | 已验证 | 禁止另写硬编码版本 |

## 5. 现役状态文件类型(AppDir)

| 文件 / 目录 | 现役写入者 | 用途 | 迁移目标(阶段 >0) |
|---|---|---|---|
| `config.json` | GUI(`picker.ps1`)+ Node 共用 `.write.lock` 锁内增量写 | 配置与密钥;gitignore | C# Worker:SQLite+WAL 产品状态真身;长期密钥 DPAPI/Windows Credential Manager |
| `state.json` | `checker.ps1` | 布防周期、额度/重置缓存 | C# Worker(SQLite) |
| `checker-ai-child.json` | `lib.ps1` / `checker.ps1` | 后台自动续跑子进程登记(launching→active) | C# launcher wrapper + durable run registry |
| `feishu-ai-children.json` | `feishu-agent.js` | 飞书 AI 子进程登记(父/子 PID、runKey、provider) | C# durable run registry |
| `feishu-sessions.json` / `feishu-userchats.json` / `feishu-query\*.started` | `conversation-store.js` | 飞书会话映射与 scratch 状态 | 目标由 cc-connect 编排 + C# 产品状态真身 |
| `feishu-inflight.json` | `feishu-agent.js` | 在飞运行登记/中断汇报 | C# 运行状态 |
| `completion-events\` + `completion-events-seen.json` | `completion-notify.js`(写)/`feishu-agent.js`(处理) | 本地完成事件队列、七天去重 | C# 单文件 hook + completion outbox |
| `session-archive.json` / `session-archive\` | `session-manager.js` | 会话归档/恢复 | C#/SQLite 或保留文件(阶段定稿) |
| `feishu-in\` / `feishu-out\` | `feishu-agent.js` | 入站/出站图片暂存 | C# Worker 与 cc-connect 边界定稿 |
| `logs\` | 各现役进程 | 日志(本地时间) | C# Worker 结构化日志(阶段定稿) |
| `feishu-token.json` | `feishu-agent.js` | tenant token 缓存(敏感,AppDir 内) | C# 机密存储,不落明文 |

> 当前状态仍是 **JSON/marker/PowerShell+Node**;SQLite+WAL 与 C# Worker/GUI **均未实现**,任何文档不得写成已上线。

## 6. 测试证据(2026-08-01 已验证)

### 6.1 完整离线矩阵(通过)

`node test/ai-providers.js`、`session-manager.js`、`routing.js`、`card-flow.js`、`session-pick.js`、`conversation-store.js`、`concurrency.js`、`image-send.js`、`progress-image.js`、`config-lock.js`、`completion-events.js`、`completion-hooks.js`、`icon-asset.js`,以及 `powershell -File test/install-deploy.ps1`、`test/auto-resume.ps1` —— **全部通过**,含 conversation-store(od: 迁移、legacy/full round-trip、隔离与稳定 ID、mark/clear、I/O 容错)、completion-events(准入/去重/重试)、config-lock(双向锁序)、install-deploy(同内容跳过/回滚/解锁)、auto-resume(无限时长/周期 ABA/崩溃恢复)。

### 6.2 GUI 自测(通过 + 人工看图)

- `-SelfTest`:真实按钮事件打开模型设置窗,校验 provider 分组、逐项切换 8 个模型、模拟 Claude 不可用整组隐藏、右侧 Chip 不裁切。
- `-SessionSelfTest`:打开真实会话管理窗。
- `-RenderTo <png>`:主窗口离屏截图。
- `-AISettingsRenderTo <png>`:AI 设置窗离屏截图。

四种模式全部通过,且人工查看截图**无裁切/重叠/空白**。

### 6.3 真 API 冒烟(两次最终均通过)

`node test/provider-live.js` 两次运行最终都通过:GPT-5.6 Sol(first + resume,隔离非 Git cwd)、DeepSeek V4、V4 Pro 直连。安全 chat canary 中间出现**一次** OpenAI auth 分类后自动切 DeepSeek,随后复测 OpenAI 完全通过——记为**待观测单次异常**,不冒充持续故障。

### 6.4 真安全 e2e(通过)

`query-security.js`、`chat-security.js` 真实 canary 均通过。

### 6.5 动态项目发现

本次动态发现 UI 显示 **7 个项目**;项目数量**不是契约**,禁止硬编码 7/8,发现逻辑与数量解耦。

## 7. 已知风险(现役 → 迁移必须处理)

| # | 风险 | 现状 | 迁移处置 |
|---|---|---|---|
| 1 | `feishu-agent.js` 单体同时处理长连接/会话/卡片/运行/健康/投递 | 已开始拆分 conversation-store/completion-events(未提交) | C# Worker + cc-connect 拆分职责 |
| 2 | 现役 JSON/marker 状态无 WAL/事务,崩溃窗口内可能截断 | 原子替换+锁缓解,非数据库级 | SQLite+WAL 真身 |
| 3 | 子进程登记(`checker-ai-child.json`/`feishu-ai-children.json`)靠文件+校验,无 Job Object | 现有 fail-closed 恢复 | C# RunContract + TaskOrchestrator + ProcessSupervisor + RunStore + Job Object + stop barrier + settle-once |
| 4 | 完成投递依赖 feishu-agent 存活轮询 | 七天去重+重试 | C# completion outbox |
| 5 | 生产飞书单一消费者是 Node agent;cc-connect 若误接同一应用会造成双写/双回复 | 当前 daemon 未安装 | 门禁后一次性切换,测试应用隔离 |
| 6 | cc-connect Management API 空 token 无认证、/status 暴露 bridge token、/config 返回原始凭据 | 未安装 daemon,风险未暴露 | 生产禁用 Management/Bridge;GUI 不直连;配置由 C# ProjectCatalogBridge 确定性生成 |
| 7 | cc-connect 无可靠 stop barrier/durable child registry/completion outbox;Session 遗漏 ActiveProvider/LastUserActivity;done reaction 不可靠 | 上游审计事实 | C# Worker 补齐;wrapper 优先,不维护 Go 私有 fork |
| 8 | 机器无 .NET SDK | 仅 runtime 8.0.25 | 阶段 2 安装固定 SDK(winget 官方源 10.0.302) |
| 9 | 源码/AppDir 双副本漂移 | 部署步骤约束 | 迁移后以安装/发布流程消除 |
| 10 | lark-cli 尚无测试应用授权;Skills 版本漂移 | 1.0.81 + 27 skills | 独立测试应用授权;以用户级真身为准 |

## 8. 阶段 0 完成判定

- [x] 四份新文档建立:`docs/MIGRATION-BASELINE.md`、`docs/STATE-OWNERSHIP.md`、`docs/EVENT-CONTRACTS.md`、`docs/adr/0001-target-architecture.md`
- [x] 五个现役文档区分「现役/目标」并加入文档索引:`docs/UPSTREAM-ARCHITECTURE-RESEARCH.md`(0-11 阶段)、`docs/ARCHITECTURE.md`、`README.md`、`AI_GUIDE.md`(首行标记刷新,git 仍 6826704)、`CLAUDE.md`
- [x] 本基线仅记录已验证事实;不含 sk-/app_secret/token 实值
- [x] 阶段 0 文档任务未新增修改 src/test/配置;未 commit/push/deploy;未运行服务/AI/网络命令;未读取 AppDir `config.json` 或任何密钥(工作区中既有的阶段 1 拆分改动保持不动)
- [x] `git diff --check` 无空白错误;rg 机密检查通过

阶段 0 后的补充架构决策:`ADR-0002`、`RUN-CONTRACT.md` 与 `EVENT-CONTRACTS` v2 于恢复审计后新增,只更新目标契约,不改变上述现役证据。

阶段进展注记(不改变本基线的已验证事实):Stage 1 稳定化、Stage 2 C# 骨架(102 测试)、Stage 3 lark-cli 试点、Stage 4 cc-connect 试点、Stage 5 产品状态迁移(C# shadow,177 测试,D-001/D-007/D-009/D-011 关闭)均已验收;现役生产链路仍是 Node `feishu-agent.js` + PowerShell(唯一生产 writer),SQLite/C# 均未部署。各阶段交付记录见对应 `docs/STAGE-*-SPEC.md` §7,债务状态见 `docs/MIGRATION-DEBT.md`。

**2026-08-06 方向修订(ADR-0003)**:Stage 6 执行中确认 wrapper 补丁面持续扩大、且上游 cc-connect 已具备我方正在重写的能力(`claude_usage.go`/`UsageReporter`/`/usage`),而上游 1.5.0-beta 对 D-014 零修复。经用户决策改为**直接使用 cc-connect、不再包装**,AI Resume 收敛为「控制面 + 续跑引擎」四项职责。**受影响的已验收成果**:Stage 5-B `ClaudeCodeProbe`(17 测试)作废(改消费上游 `UsageReport`);Stage 6 wrapper 中 `SessionBridge` 作废、`AuthMapper`/`RunMapper` 缩减。上述阶段当时的验收结论在其历史语境下仍成立,本注记只记录后续方向变更,不追溯改写历史证据。详见 `docs/adr/0003-cc-connect-direct-and-control-plane.md`。

## 9. 外部前提(独立测试应用等)

阶段 3 的 lark-cli 试点、阶段 4 的 cc-connect 试点与阶段 10 生产切换依赖以下外部前提,任一未满足即不得进入对应阶段:

1. **独立飞书测试应用**:与生产应用完全隔离的 appId/appSecret,仅用于测试项目;凭据只放隔离测试环境,不写仓库。
2. **测试项目**:独立本地测试项目(非真实工作仓库),用于 cc-connect 路由/原生 session/停止/进度/崩溃恢复验证。
3. **lark-cli 测试身份授权**:对测试应用完成用户/bot 授权与 scope 验证;高风险命令契约(exit 10 不自动 `--yes`)保持原样。
4. **工具链**:winget 官方源可见 `Microsoft.DotNet.SDK.10` `10.0.302`,阶段 2 安装固定版本;不引入其他未固定依赖。
5. **门禁**:独立测试应用依次通过阶段 3/4/6 以及阶段 8 中的通知链门禁,并且阶段 9 数据迁移对账通过后,阶段 10 才可一次性切换唯一生产消费者。
6. **用户授权**:任何改变生产消费边界、引入双写或整体回退的方案必须先经用户确认(ADR 机制)。
