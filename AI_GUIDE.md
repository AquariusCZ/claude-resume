<!-- project-tour · generated 2026-08-05 · git e718fe2 · 迁移方向于 2026-08-06 经 ADR-0003 修订,目标架构描述以 ADR-0003 为准;2026-08-07(S10-O)同步了 FEISHU-BOT-GUIDE 菜单删除、STAGE-10-SMOKE-PLAN 项目名 ai-resume 与 send 不驱动 agent 教训、STAGE-11-GATE 失效 PID,见 docs/P4-DOC-SYNC-TABLE.md;本导览正文待下次 project-tour 重跑后同步;2026-08-08 第二轮审计后同步了「界面肯定句必须可证伪」的 GUI 状态语义、通知源意图持久化(NotifyIntent)与点阵字排版规则,见 CLAUDE.md「GUI 服务状态语义」与 docs/LESSONS.md 第十一节 -->
# AI Resume — AI 导览(AI_GUIDE.md)

> **2026-08-06 方向变更**:目标架构经 `docs/adr/0003-cc-connect-direct-and-control-plane.md` 修订——cc-connect 直接运行不再包装,AI Resume 收敛为控制面 + 续跑引擎(四项职责)。本文正文描述的**现役**行为不受影响。

> 一句话:**AI Resume v2.0.0** 是一个 Windows 多 AI 工作台:显式切换 OpenAI GPT-5.6 Sol、DeepSeek V4/V4 Pro 或 Claude,管理 provider-native 会话,通过飞书闲聊/查询/授权修改项目,并保留 Claude 额度重置后的多项目自动续跑;飞书 scratch 会话 14 天归档、30 天删除,项目工作会话只手动归档。
> 本文件供 AI **只读问答**优先加载:80% 的常见技术问题看这里就能答;深挖时见文末「文档索引」。

## 1. 定位
- **用途**:解决「Claude Code 5h 限流打断多个项目」的痛点。勾项目 → 按**布防(Arm)** → 关窗;计划任务每 ~2 分钟一跑,按固定间隔**实时探测**账号是否可用,重置那一刻依次续跑所选项目。**无估算**——重置时间/额度百分比全部从 Claude 服务器的 `rate_limit_event` **实时读取**。
- **使用者 / 场景**:个人在 Windows 上跑 Claude Code、经常被 5h 限流打断的人;可把飞书机器人**开给同事**:同事自动只读(浏览/查询),绝不可改;每人的菜单/回复走**各自的私聊**、用**各自的模型**,互不干扰。
- **技术栈**:
  - GUI + 引擎:**Windows PowerShell 5.1**(注意不是 pwsh 7)。GUI 用 WPF/XAML。
  - 飞书 agent:**Node.js**,`@larksuiteoapi/node-sdk`(`^1.53`)的 `WSClient`(长连接 WebSocket,**无需公网 IP**)。唯一 npm 依赖。
  - 隐藏启动:`.vbs`(经 `wscript`)+ **计划任务** `ClaudeResumeChecker`(每 2 分钟)。
  - 外部依赖:**Claude Code CLI**(Claude + DeepSeek Anthropic 兼容入口)与 **Codex CLI/Desktop**(OpenAI GPT-5.6 Sol);prompt 均走 stdin。
  - 无编译、无数据库;所有状态是 `%LOCALAPPDATA%\ClaudeResume`(下称 **AppDir**)下的 JSON + 日志文件。
  - **迁移工程(C# shadow,未部署)**:`.NET 10` 六组件骨架(`csharp/`,SQLite/WAL + Named Pipe + DPAPI + WPF 空壳)已实现,独立 shadow 目录 `%LOCALAPPDATA%\ClaudeResumeShadow`(可 `AIRESUME_SHADOW_DIR` 覆盖),不连生产飞书、不碰 AppDir;现役运行链路不变。**Stage 3 增**:lark-cli 能力层封装 `AiResume.LarkCli`(envelope/脱敏/exit 10/超时取消,9 项离线测试)与独立测试应用只读试点(im/calendar/docs)已完成。**Stage 4 增**:cc-connect 1.4.1 独立测试应用试点完成(provider 切换/注入/停止/进度/崩溃恢复;发现 release 命令集与文档不一致等,wrapper 可适配)。**Stage 5 增**:产品状态迁移完成(项目发现/shadow 配置/Claude 限额探测/布防周期状态机/断电真 kill 恢复与三方对账报告),177 测试全绿,旧系统仍是唯一生产 writer。见 [docs/STAGE-2-SPEC.md](docs/STAGE-2-SPEC.md) §7、[docs/STAGE-3-SPEC.md](docs/STAGE-3-SPEC.md) §7、[docs/STAGE-4-SPEC.md](docs/STAGE-4-SPEC.md) §7、[docs/STAGE-5-SPEC.md](docs/STAGE-5-SPEC.md) §7 与 [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md)。
  - **版本真身**:`src/package.json`。当前为 `2.0.0`,Codex app-server 初始化时直接读取该值;AppDir/计划任务/Startup 快捷方式仍保留历史 `ClaudeResume*` 名称以兼容升级。

## 2. 架构与数据流
两个相对独立的部分:**续跑器**(GUI + 引擎,PowerShell)与**飞书 agent**(Node),两者只通过 `config.json` 交换状态。
**为何拆 GUI / 引擎**:续跑可能跑几分钟到几小时,若放在 WPF UI 线程里窗口会冻住、Stop/Disarm 恰好失灵;所以 GUI 只**配置 + 监控**,真正的等待+续跑在计划任务拥有的独立进程里,能扛住关窗/注销/重启。

```
桌面快捷方式「AI Resume」
      │ launcher.vbs(wscript 隐藏)
      ▼
 picker.ps1 (WPF 工作台)──写▶ ┌─────────────────────────┐ ◀──读/写── checker.ps1 (引擎/无状态状态机)
  项目/AI 服务/会话/布防控制  │ config.json  (GUI 写)    │            ▲ 计划任务 ClaudeResumeChecker 每 2 分钟
  provider 状态/额度/日志 ◀── │ state.json   (checker 写)│            │ (checker-launch.vbs,checker.lock 防重入)
  日志区(彩色)◀────读────── │ logs\run-*.log           │            │ 用
                             └──────────┬──────────────┘   ┌─────────┴─────────┐
                                        │                  │ lib.ps1 (共享函数) │
   探测/续跑 = 子进程 cmd.exe /c claude.cmd …               └─────────┬─────────┘
                                        │  claude CLI ──stream-json──▶ Claude 服务器
                                        │                              (rate_limit_event: resetsAt/utilization)
                                        ▼ 续跑:每项目 git 脏检查 → claude --continue -p "continue"

──────────────  飞书(可选,独立 Node 进程)  ──────────────
 开机 Startup → feishu-launch.vbs(守护,node 挂了 ~8s 重启;重启时全部会话重置为 idle
      ▼          + reportInterruptedRuns 告知「上次运行被打断」)
 feishu-agent.js(稳定 wrapper) → feishu-runtime.js(现役兼容壳) ◀── WSClient 长连接 ──▶ 飞书开放平台
      (Stage 1 结构已于 2026-08-04 11:30 部署为生产真身:唯一 node PID 34600,BOOT/READY generation 一致)
  · 收:im.message.receive_v1(+_v2) / card.action.trigger / application.bot.menu_v6
  · 注册回调毫秒级返回(ACK);同 chat handler 保序,统一 AI runner 在后台 bg()+inflight 执行(否则飞书停止投递=「白天卡死」)
  · 长跑 = 一张进度卡原地 patch(startProgress ~20s 一跳)+ feishu-inflight.json 崩溃留痕
  · 结果 = mdToLark 渲染成 lark_md 卡片(sendResult),卡片失败自动退化纯文本分片
  · 图片:独立 image 下载挂起→折进下一条文字;图文 post 解析文字+多图→同轮请求;出站 AI 存 feishu-out\ → 跑完上传发送
  · 回复按用户路由(userChats: open_id→各自私聊,仅 p2p 学习);通知经 Send-FeishuNotify → feishuChatId(仅 owner 可绑定)

 Codex 顶层持久会话 / Claude Code / Cline 完成 hook
      │ 只写项目路径、客户端、事件 ID、时间;不读项目内容/密钥
      ▼
 AppDir\completion-events\ ──▶ feishu-agent 动态识别 cwd/workspace/Git 根 ──▶ owner 飞书完成通知
      (原子落盘/失败重试)       (稳定 UUID + 七天去重;项目数量不写死)
```

飞书会话状态机(存 `feishu-sessions.json`,agent 重启全部归 idle):

```
            ┌────────── idle(默认:自由文本只弹主菜单卡,绝不误跑)──────────┐
   [💬闲聊]│                    [进项目 / 项目名 / 编号]                     │底部菜单(菜单/idle/exit)=逃生舱
            ▼                              ▼                               (任何状态→idle+新主菜单卡)
          chat ──自由文本──▶ 当前 AI    project(先选 sub,才接受自由文本)
   (按用户+profile scratch;           ├─ sub=query  → 隔离 cwd 的按(project,用户,profile)查询会话
    非 owner 禁文件/执行)             └─ sub=modify → provider-aware 会话选择卡(owner-only)→ 原生 resume
                                          没选 work 之前发指令 = 只重弹选择卡,绝不跑 AI

   底部菜单的 🤖模型 和 🛑停止 是例外:不重置会话/不算逃生舱。🤖模型弹一张独立的两级选择卡:
   先选实测可用的 provider 父级,再选模型子项;切完直接回原 chat/project 继续;🛑停止(event_key=stop,
   走 stopRuns)只停止调用者自己的闲聊/查询;只有 full 用户能停止项目修改进程(killTree 连根拔),不动会话状态。
```

## 3. 模块职责(路径 → 职责 → 关键函数/入口)
| 路径 | 一句话职责 | 关键函数 / 入口 |
|---|---|---|
| `src/lib.ps1` | 引擎共享库:项目发现、探测、续跑、git 守护、跨 Node/PowerShell 的 config 锁内增量更新、布防周期和后台子进程崩溃恢复。 | `Update-CcuConfig`、`Invoke-CcuPortableWriteLock`、`Initialize-CcuCycleState`、`Register-CcuBackgroundLaunch/Child`、`Recover-CcuBackgroundChild`、`Get-CcuProcessProbe`、`Write-CcuJsonAtomic`、`Stop-ProcessTree` |
| `src/checker.ps1` | **周期隔离状态机**,计划任务每 ~2 分钟跑一次;文件锁防重入;启动先回收残留 AI 子进程;节流→探测→限流/恢复→FIRE 无限时长续跑;每 500ms 校验 `armCycleId` 并终止已解除/被取代的进程树;`-DryRun` 预演。 | 周期绑定的状态写入、git 守护后二次取消检查、`RunKey=<cycle>|<project>`、一次性 `Complete-CcuCycle` |
| `src/picker.ps1` | WPF/XAML **AI Resume 工作台**:项目列表 + 右侧当前 AI/真实 provider 状态/Claude 额度/布防控制 + 底部日志;默认模型按 provider 分组且只显示实测可用服务,配置区仍保留不可用服务的登录/API Key 修复入口。 | `Set-AIModelOptions`、`Get-ProviderPresentation`、`Update-ProviderState`、`Update-ClaudeQuotaState`、`Show-AISettingsWindow`、`Show-SessionManagerWindow` |
| `src/provider-health.js` | 共享最小请求探测:GUI 默认探 OpenAI/DeepSeek;飞书用 `includeClaude` 同时探三家。OpenAI/DeepSeek 先清除进程代理直连,仅网络类失败再试 `aiProxy`,输出 status/reason/ms/route,不输出响应正文或密钥。 | `probeProviders`、`readRuntimeConfig` |
| `src/feishu-agent.js` | migration 工作区的 Stage 1 稳定入口:只加载并导出 `feishu-runtime.js`,不再实现 SDK、权限、会话、provider、卡片、图片、进程登记或完成通知。已于 2026-08-04 部署为生产 AppDir 入口,旧单文件入口不再运行。 | `module.exports = require('./feishu-runtime')` |
| `src/feishu-runtime.js` | 由移动前入口逐字节复制得到初始 legacy compatibility application shell,随后只接受 Stage 1 总审查要求的安全加固;D-006 fixture 继续冻结初始来源 SHA/生成时间,不把后续修正冒充移动前实现。它继续装配六个 Stage 1 边界和现役启动生命周期,启动时把安装周期 generation 原样写入结构化 BOOT/READY;不是第七目标边界,将在 Stage 6/10/11 被替换或删除;已于 2026-08-04 部署为生产运行真身。 | `refreshProviderHealth`、`buildModelCard`、`runForUser`、`runProjectQuery`、`stopRuns` |
| `src/channel-adapter.js` | Stage 1 的飞书通道边界:集中创建 Client/WSClient/EventDispatcher,执行单次 API timeout/现役一次网络重试、目标映射、消息/卡片/图片/资源调用,透传 SDK 原生 `onReady`,并保证事件立即 ACK、同 key 保序、跨 key 并发。已通过 Stage 1 总门禁并于 2026-08-04 部署到生产 AppDir。 | `createChannelAdapter` |
| `src/authorization-policy.js` | Stage 1 的纯权限决策边界:owner/viewer/allowlist、身份缺失 fail-closed、owner-only profile;不做 I/O。 | `createAuthorizationPolicy` |
| `src/completion-events.js` | Stage 1 的完成事件准入边界:事件校验、项目解析、稳定消息 UUID、队列 claim/恢复/去重;同 eventId 的跨进程重查、发送和 seen 持久化在同一锁内完成,malformed/schema 错误隔离后继续处理后续事件,seen 索引用不可覆盖 generation 恢复 canonical 损坏;不读取飞书密钥。 | `createCompletionEvents`、`stableMessageUuid` |
| `src/conversation-store.js` | Stage 1 的会话状态边界:单一持有 user-chat 映射、idle/chat/project 状态、query/chat scratch marker、稳定 session ID 和 Claude scratch 清理;不拥有卡片、飞书 SDK 或 provider 执行。已通过 Stage 1 总门禁并于 2026-08-04 部署到生产 AppDir。 | `createConversationStore` |
| `src/task-orchestrator.js` | Stage 1 的现役兼容任务编排边界:同步 runKey 预占、可取消的 accepted-before-spawn reservation、provider 候选/fallback、健康预检取消、活动 child/reservation/preflight 停止决策、shutdown 拒绝和 legacy deadline 透传;取消不可 fallback。它不拥有飞书 SDK、进程登记或目标持久化 Start/Status/Cancel 状态机,尚未部署到生产 AppDir。 | `createTaskOrchestrator` |
| `src/session-manager.js` | GUI 与 agent 共用的会话生命周期真身:扫描/报表、14/30 天自动规则、手动归档/恢复/永久删除、安全测试/探针垃圾清理。Claude 移 transcript+artifact;Codex 用原生 thread API。 | `createSessionManager().report/cleanup/archive/restore/remove/forgetChat/clearQuery` |
| `src/ai/profiles.js` | 提供商/模型目录、旧配置迁移、别名解析、按人选择和降级顺序。 | `PROFILES`、`getUserProfileId`、`parseProfileInput`、`fallbackProfiles` |
| `src/ai/agent-adapter.js` | Stage 1 的单 provider attempt 边界:精确透传 run/resume/cancel/waitForIdle,只观察 starting/running/terminal 小元数据,不拥有 deadline、fallback、重试或 provider 选择。已通过 Stage 1 总门禁并于 2026-08-04 部署到生产 AppDir。 | `createAgentAdapter` |
| `src/ai/runners.js` | Claude Code / Codex / DeepSeek 子进程适配、JSONL 解析、错误分类、显式 direct/proxy 子进程环境、副作用检测和整棵进程终止。D-002 malformed/unknown activity fail-closed 已完成,录制 retryable 429 不再触发第二 provider;已于 2026-08-04 部署。 | `createAIRunner`、`classifyClaudeStreamLine`、`classifyCodexStreamLine`、`childEnv`、`classifyError` |
| `src/ai/codex-sessions.js` | Codex app-server 客户端:读取/预览及原生归档、恢复、删除线程。 | `createCodexSessions().list/listAll/preview/archive/unarchive/remove` |
| `src/completion-notify.js` | Codex/Claude Code/Cline 完成 hook 适配器:标准化事件,校验 Codex 顶层 rollout/真实项目边界并原子写入 AppDir 队列;内部 AI Resume 子进程直接抑制。 | `normalizeEvent`、`admitCompletionEvent`、`writeEvent`、`forwardPrevious` |
| `src/install-completion-hooks.js` | 用户级 hook 幂等安装器:保留 Codex notify 链、Claude Stop hooks 和 Cline 既有 TaskComplete 脚本。 | `installCodex`、`installClaude`、`installCline` |
| `src/deploy-files.ps1` | 安装器的事务文件部署核心:SHA256 跳过同内容锁定文件,同卷原子替换,失败逆序回滚;回滚也失败时保留恢复副本。 | `Test-SameFileContent`、`Set-CcuDeployedFile`、`Invoke-CcuFileDeployment` |
| `src/package.json` | v2 版本真身 + 飞书 SDK 依赖;Codex app-server client version 从这里读取。 | `version`、`dependencies` |
| `src/install.ps1` | 部署:先按 `package-lock.json`/类型契约确认飞书 SDK 精确为 `1.70.0`,再执行 src→AppDir、合并三类完成 hook、图标、快捷方式、计划任务和 Startup 项。重部署为本次安装生成一次性 generation,必须确认所有旧 agent PID 已 gone,随后恰有一个稳定新进程输出同 generation 的结构化 BOOT/READY;任一不可确认即失败。**可反复跑**(=重新部署),末尾把 `enabled` 置 false(未布防)。 | 顶层脚本 |
| `src/*.vbs` | 隐藏启动器:`launcher.vbs`(GUI)、`checker-launch.vbs`(计划任务)、`feishu-launch.vbs`(守护 node,自动重启+重定向 stdout,>1MB 轮转)。 | wscript 隐藏窗口 |
| `test/` | 离线回归、三 provider 真 API 冒烟、query/chat 真安全 e2e、2 个 PowerShell 回归和 4 种 GUI smoke 模式。 | `channel-adapter`、`task-orchestrator`、`agent-adapter`、`completion-events`、`completion-hooks`、`install-deploy.ps1`、`icon-asset`;其余见 §4 |
| `docs/` | `ARCHITECTURE.md`(现役内部原理)、`LESSONS.md`(踩坑+开发史)、`RECOVERY-AUDIT-20260801.md`(恢复现场/即时执行顺序)、`UPSTREAM-ARCHITECTURE-RESEARCH.md`(0-11 迁移计划)、`MIGRATION-BASELINE.md`、`STATE-OWNERSHIP.md`、`EVENT-CONTRACTS.md`、`RUN-CONTRACT.md`、`MIGRATION-DEBT.md` 与 ADR。 | — |
| `.agents/skills/project-tour/` | 生成本导览的 skill(GUI「更新导览」按钮跑的是同一套流程的 headless 版)。 | `SKILL.md` |

## 4. 测试 / 运行流程
- **安装/部署入口**:`powershell -ExecutionPolicy Bypass -File src\install.ps1`。先校验/修复固定的 `@larksuiteoapi/node-sdk@1.70.0` 与 `onReady` 类型契约,再复制到 AppDir、幂等合并 Codex `notify` / Claude Code `Stop` / Cline `TaskComplete` hook、建桌面快捷方式「AI Resume」、注册计划任务 `ClaudeResumeChecker`(每 2 分钟且 `ExecutionTimeLimit=PT0S`),初始**未布防**。既有 hooks 会串联保留;首次安装后已打开的客户端需重载。重部署的 taskkill/CIM/唯一新 agent/本次 generation 的结构化 BOOT+READY 任一不可确认时返回非零,不得报告成功。AppDir/任务名保留旧 `ClaudeResume` 内部名以兼容升级。
- **改代码后重新生效(关键,容易忘)**:线上从 AppDir 跑,**不会**自动同步 `src/`:
  1. `.ps1`(GUI/引擎)和 `provider-health.js`:复制到 AppDir(或重跑 `install.ps1`);GUI 改动**关闭旧窗口后再打开**才生效。
  2. `feishu-agent.js`、`feishu-runtime.js`、`channel-adapter.js`、`authorization-policy.js`、`completion-events.js`、`conversation-store.js`、`task-orchestrator.js`、`session-manager.js`、`ai/`:复制到 AppDir → 只精确定位命令行入口等于当前 AppDir `feishu-agent.js` 的 `node.exe` PID并执行 `taskkill /PID <pid> /T /F`(不得误杀其他 Node 服务,也不得只杀父进程)→ 逐个确认旧 PID 明确 gone → VBS 守护 ~8s 自动重启(重启会把全部飞书会话重置为 idle,并向被打断的运行者补发说明)。
  3. 验证:node 进程应恰为 1 个,且旧进程停止后新增的 `logs\feishu-stdout.log` 内容出现同一安装 generation 的 `AI_RESUME_AGENT_BOOT` 与 `AI_RESUME_AGENT_READY`;SDK 通用 `ws client ready` 文本不能作为部署身份凭据。
- **D-006 等价门禁**:`node test/stage1-recorded-equivalence.js` 默认只读比较移动前 fixture,强制临时 config/state/home 与 `FEISHU_TEST_NO_AI=1`;覆盖 ACK、消息/卡片、状态、provider attempt、完成通知和拒绝路径。只有显式 `--record` 才更新 fixture。
- **离线自测(不联网、不占单实例锁、不跑真 AI)**:`node test/stage1-recorded-equivalence.js`、`config-isolation.js`、`channel-adapter.js`、`agent-adapter.js`、`task-orchestrator.js`、`conversation-store.js`、`routing.js`、`card-flow.js`、`session-pick.js`、`authorization-policy.js`、`menu-authorization.js`、`concurrency.js`、`image-send.js`、`progress-image.js`、`ai-providers.js`、`session-manager.js`、`config-lock.js`、`completion-events.js`、`completion-hooks.js`、`icon-asset.js`,以及 `powershell -File test/install-deploy.ps1`、`test/auto-resume.ps1`。`task-orchestrator` 钉死同步预占、accepted-before-spawn 取消、shutdown、preflight/child 停止、fallback 与 legacy deadline 兼容;`agent-adapter` 钉死单 attempt 透传、provider-native session、事件脱敏、观察回调隔离及无 adapter deadline/fallback/retry;`conversation-store` 钉死 od: 迁移、legacy/full session、仅无显式项目列表的 legacy basename fallback,以及显式项目快照未命中 fail-closed。`completion-events` 覆盖 malformed 后继续、发送重试、稳定 UUID、canonical 损坏与 generation 恢复,并用两个独立实例证明同 eventId 只发送一次;`ai-providers` 锁定 D-001 三态身份、主文件检查失败/损坏 registry 原字节保留且全局阻断、backup/generation 前沿、写回前指纹复核、写入/删除失败锁保留、shutdown 真实 close 前不解锁、D-002 malformed/unknown activity fail-closed 和 retryable 429 有副作用不重放;`install-deploy` 验证 SDK `1.70.0`/`onReady` 契约、taskkill/CIM、旧 PID gone、PID 复用和一次性 generation READY fail-fast。`config-isolation` 递归清空飞书/解锁/provider 密钥及顶层/嵌套 `aiProxy`;所有 Feishu Node 测试使用进程独占临时 config/state/AppDir/Claude/Codex home,真实 config 只做 no-reparse 只读与 SHA256 前后比对。
- **GUI smoke / 截图**:`powershell -NoProfile -ExecutionPolicy Bypass -File src\picker.ps1 -SelfTest` 会真实触发“当前 AI”按钮,校验三组 provider 标题、逐项切换 8 个模型,再模拟 Claude 不可用并断言 Claude 全组折叠;同时检查右栏 Chip 不裁切及 Claude auth/额度状态一致。`-SessionSelfTest` 打开真实会话窗;`-RenderTo <png>` 与 `-AISettingsRenderTo <png>` 分别渲染主工作台和 AI 设置窗。四种模式都禁止真实探测。
- **真 API 冒烟**:`node test/provider-live.js`。只发无工具「OK」请求,依次验证 GPT-5.6 Sol、DeepSeek V4/V4 Pro;OpenAI 首轮成功后还在同一隔离非 Git cwd 续接第二轮,专门防 `codex exec` 与 `codex exec resume` 参数漂移;密钥只从 AppDir 配置读取且不打印。
- **真安全 e2e**:`node test/query-security.js`、`node test/chat-security.js`。mock 飞书、真实当前 OpenAI provider;只把顶层 provider key 注入当前测试进程,使用临时合成 config/chat/owner/project 和 Codex/Claude home。非 owner 诱导读取合成 canary,测试每 15 秒报告状态并等待成功/失败终态,静默不判超时;要求**成功结果卡**且回复不含 canary,生产 config 哈希不变。
- **铁律**:测试**绝不能**对真实项目/真实会话跑任何 AI 修改——曾有测试 resume 真会话并 push。派发断言用假 id + `FEISHU_TEST_NO_AI=1`;mock 必须照抄线上真实 API 结构。
- **典型使用流程(跑一次)**:
  1. 双击桌面「AI Resume」→ 勾选项目(自动发现自 `~/.claude/projects`;「添加文件夹」可加任意目录);
  2. 点**预演**确认计划(日志区出现 DRY-RUN 行);
  3. 点**布防续跑**,可关窗(限流前/后布防都行——未限流就保持监视);
  4. 撞限流后 checker 每 4 分钟一探(被拒探测不耗额度),重置瞬间依次续跑;
  5. 每项目:git 脏检查(`stash`/`branch` 兜底)→ `claude --continue -p "continue"` → 飞书推 ✅/❌;
  6. 全部完成 → 自动解除布防(一次性)→ 飞书推「🎉 全部完成」。
- **依赖 / 环境**:Windows 10/11;PowerShell 5.1;Node.js LTS;Claude Code CLI(续跑/Claude/DeepSeek);Codex CLI/Desktop(OpenAI);OpenAI-compatible Responses endpoint/key 与 DeepSeek key 从 GUI AI 设置写入 AppDir。
- **飞书后台一次性配置**(缺一不通):启用机器人;权限 `im:message`(收+发)**和 `im:resource`**(收发图片,缺它图片通道退化成文字提示);事件订阅方式=**长连接**,订阅 `im.message.receive_v1`、`card.action.trigger`、`application.bot.menu_v6`;机器人自定义菜单三项 `event_key` = `menu` / `model` / `stop`;**发布版本**。密钥填进 `config.json` 后重跑 `install.ps1`。

## 5. 数据格式与命名约定
本项目**没有科学数据文件**,「数据」= AppDir 下的 JSON 状态 + 日志 + provider-native 会话文件。

- **目录布局**:源码/文档在仓库(`C:\Users\<you>\Desktop\claude-resume`);**运行态**全在 AppDir(`%LOCALAPPDATA%\ClaudeResume\`):
  - `config.json`(GUI/Node 通过同一 `.write.lock` 锁内增量写,含 `armCycleId`)/ `state.json`(checker 写,含匹配的 `cycleId`)/ `checker-ai-child.json`(**后台自动续跑登记**:spawn 前 `launching`,启动后 `active`;父/子 PID+周期 runKey+项目+启动时间;主文件及完整临时代都可恢复)/ `feishu-sessions.json`/ `feishu-userchats.json`/ `feishu-inflight.json`/ `feishu-ai-children.json`;
  - `logs\`(全部日志)、`node_modules\`、`icon.ico`、`feishu-agent.pid`(单实例锁)、`checker.lock`(防重入)、`feishu-token.json`(tenant token 缓存);
  - `feishu-query\<sha1>.started`(查询会话标记)、`feishu-query-cwd\<sha1>\`(只读查询的隔离 cwd)、`feishu-chat\`(闲聊 scratch 会话);
  - `session-archive.json`(归档索引/原 marker 快照/恢复元数据)、`session-archive\<archiveId>\claude\...`(Claude transcript + 同名 artifact 的可恢复归档);`session-archive.lock` 只在操作期间存在。
  - `feishu-out\<sha1(cwd)>\`(**出站图片暂存**:AI 要发飞书的图存这里,跑完上传即删——放 AppDir 而非项目内);`feishu-in\<sha1(chatId+sender)>\`(**入站图片暂存**:按聊天+发送者隔离;`post` 同轮原子消费,独立图折进该用户下条文字;最终每轮最多 6 张且 post 图优先;运行后删除,孤儿满 24h 后每小时清磁盘+内存队列)。
  - `completion-events\`(**本地客户端完成事件队列**:Codex/Claude Code/Cline hook 原子写入,malformed/schema 错误隔离后继续后续事件,飞书发送失败保留重试,事件最长保留 7 天);`completion-events-seen.json` 与同前缀 `.gen-*` 是最多 2000 条的七天去重索引及可恢复 generation。
  - **密钥只在 `config.json`,gitignore,绝不进仓库。**
- **日志文件名解码**(都在 `logs\`,**一律本地时间**,绝不用 UTC)。真实样例 `run-20260716.log` 拆解:
  - `run-` = 续跑引擎日志(`Write-CcuLog` 写,GUI 日志区显示的就是它);`20260716` = 本地日期 `yyyyMMdd`,每天一个文件;
  - GUI 永远读**最新的** `run-*.log`(`Get-CurLogFile` 按 `LastWriteTime`),绝不固定开窗那天的名字(跨午夜会空白);
  - 其余:`feishu-<yyyyMMdd>.log` = 飞书 agent 日志(`logLine`);`feishu-stdout.log` = node stdout/stderr(>1MB 守护 vbs 轮转、>2MB `Clear-OldCaches` 清空);`gui-error.log` = GUI 异常。日志保留 30 天;导出 = 合并成 UTF-8 带 BOM 单文件。
- **只读查询会话 id / cwd 解码**(按 **project + 提问人 openId + AI profile** 隔离):
  - `querySession(projectPath, openId, profileId)`:`h = sha1(projectPath.toLowerCase() + '|' + openId + '|' + profileId)`。Claude/DeepSeek 使用由该 seed 派生的固定 UUID;Codex 首轮返回原生 thread id 后写入标记文件。
  - 标记文件 `feishu-query\<h>.started`,内容含 `{kind,openId,id,sessionId,profileId,engine,path,name,updatedAt}`;闲聊 marker 同样写 `kind/openId/profileId/engine/updatedAt`。Claude/DeepSeek 首次 `--session-id <uuid>`、之后 `--resume`;Codex 首轮成功后记录原生 thread id,之后 `codex exec resume`。Codex 新建/续接共用同一参数构造器,两者都带隔离非 Git cwd 所需的 `--skip-git-repo-check`。
  - 隔离 cwd `feishu-query-cwd\<h>\` + `--add-dir <项目路径>`,**绝不**在 `project.path` 里跑(否则查询会话污染 `--continue` 池,修改项目会误续到查询会话——实测过的 bug)。
  - **清空**统一走 `session-manager.js`:Claude/DeepSeek 删除 `.jsonl` + 同名 artifact 目录;Codex 调 `thread/delete`;同时删 marker/cwd。飞书 `忘记查询` 只清自己的当前 profile,GUI 可按项目或全局清理。
  - **生命周期**:闲聊/查询 14 天未用归档、30 天未用永久删除(从 `lastUsedAt` 算,不是归档日);agent 启动 10s 后及每 6h 执行。真实项目工作会话绝不自动处理,仅 GUI 手动归档/恢复/永久删除。
  - **工具分级(HIGH 安全修复)**:`--permission-mode plan` 只挡「写」不挡「读」,且读不限工作区——`disallowedTools` 按调用者算:仅**显式在 `feishuAuthOpenIds` 里的 owner** 跑 `['Task']`(保留 Read,密钥本就是他的);其余所有人一律 `['Task','Bash','Read','Write','Edit','Glob','Grep','NotebookEdit']`(禁全部文件/执行工具,只据首建时注入的 AI_GUIDE.md 作答;导览首行的 git hash 与项目当前 hash 不一致时附「导览可能过时」提示)。
- **项目发现与工作会话**:
  - 项目发现读 `~/.claude/projects/<encoded>/*.jsonl` 头部真实 `cwd` + 最后使用时间;AppDir、Windows 系统目录和 `os.tmpdir()` 全排除,防探测/查询/冒烟会话污染菜单。
  - migration 的 D-003 门禁要求每个消息/卡片/菜单事件只使用入口读取的一份配置快照:授权、项目发现、active project、菜单/状态/帮助、进入/清空查询/停止和 owner 通知 chat 预检不得中途换配置。`discoverProjects(cfg)` 的 3 秒缓存按 `hiddenProjects/customProjects` 指纹隔离;`activeProject(chatId, projects)` 显式列表未命中即返回 null,隐藏/移除项目不能由旧 session、旧卡或文字进入恢复;入口为 `none` 后磁盘升级为 owner 也不能在同一事件披露项目或绑定通知 chat。该加固尚未部署,Stage 6 需在目标链路复验。
  - ✏️修改会话:Claude/DeepSeek 从 JSONL 取 `ai-title`/尾部摘要;OpenAI 通过 Codex app-server `thread/list`/`thread/read` 读取原生 threads。统一只展示最近 5 个。
- **JSON 内部结构**(完整字段见 [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md)):
  - `config.json`(GUI 写,UTF-8 **无 BOM**——有 BOM 会崩 Node 的 JSON.parse)核心字段:

    ```jsonc
    {
      "enabled": false,             // 后台自动续跑开关;解除不影响飞书任务
      "armed": false,               // 旧 GUI 兼容镜像
      "armCycleId": "",             // 每次布防生成新值,隔离旧 checker
      "selected": [{"name":"...","path":"..."}],
      "customProjects": [{"name":"...","path":"..."}], // 手动加的文件夹
      "hiddenProjects": ["C:\\path\\hidden"],             // 从自动发现中隐藏
      "resumePrompt": "continue",   // 续跑时喂给 claude 的指令
      "resumeModel": "",            // 空 = Claude 当前默认
      "skipPermissions": true,      // 全自动(--dangerously-skip-permissions,配 dirty-guard)
      "dirtyGuard": "stash",        // 或 "branch":续跑前保护未提交改动
      "probeIntervalMinutes": 15,   // 可用时探测节奏(GUI 间隔 chip 5/15/30);限流后自动 4 分钟
      "probeModel": "haiku", "resumeModel": "", "perProjectTimeoutMinutes": 0,  // 兼容字段;后台续跑无限时长
      "continuous": false,          // true = 每轮完成后不解除(默认一次性)
      "feishuAppId": "", "feishuAppSecret": "",    // 自建应用密钥(唯一存放处,gitignore)
      "feishuChatId": "",           // 通知聊天,仅 owner 的 p2p 消息可绑定
      "completionNotifyEnabled": true, // Codex/Claude Code/Cline 本地完成边界通知
      "feishuChatProfile": "openai-sol", // 主 owner 的 AI profile
      "feishuUserProfiles": {},     // 其他用户各自的 profile:open_id → profile id
      "feishuQueryTimeoutMinutes": 30, "feishuChatTimeoutMinutes": 30,
      "aiFallbackProfiles": ["deepseek-v4", "openai-sol"],
      "aiProxy": "", "aiNoProxy": "127.0.0.1,localhost,::1",
      "openaiBaseUrl": "https://api.openai.com/v1", // Responses-compatible
      "openaiApiKey": "", "openaiReasoning": "xhigh",
      "deepseekApiKey": "", "deepseekMillionContext": true,
      "deepseekEffort": "",        // 空 = V4 high / V4 Pro max
      "sessionAutoCleanup": true,
      "feishuSessionArchiveDays": 14,
      "feishuSessionDeleteDays": 30,
      "sessionCleanupIntervalHours": 6,
      "feishuChatModel": "", "feishuUserModels": {}, // 旧版兼容镜像
      "feishuAuthOpenIds": [],      // owner 名单;空 = 未锁定(人人可改)!
      "feishuAllowOpenIds": [],     // 可选发送者白名单;非空时只接收这些 open_id
      "feishuAuthPassword": "",     // 可选:「解锁 <密码>」自助成为 owner(仅 idle 生效)
      "feishuWebhook": "", "feishuSecret": ""      // 备胎:自定义机器人 webhook 单向通知
    }
    ```
    (`feishuViewerOpenIds` 已无须配置——非 owner 自动是 viewer;`feishuAllowOpenIds` 仍是应用层发送者白名单,非空时消息/卡片/底部菜单都要求有效且命中的 open_id。)
  - `state.json`(checker 写):`sawLimited`、`lastProbeUtc`、`limitedRefires`(≥6 熔断防误判死循环)、`projectStatus`(每项目 `success/error/timeout/limited/stopped`)、`phase`(`idle/waiting/resuming/done`)、`realFiveHourResetUtc`/`realSevenDayResetUtc`(**Unix 整数秒**,规避 ISO 时区坑)、`realFiveHourUtil`。
  - `feishu-sessions.json`:chatId→`{mode,project,sub,work,workProfile,workTitle}`;`workProfile` 防止把 Codex thread id 当 Claude session id 续跑。跨提供商切换时新建原生会话并注入旧会话摘要。
  - `feishu-inflight.json`:`{key: {chatId, label, kind, startedAt}}` 在飞运行表;正常结束删除,重启读到残留 → `reportInterruptedRuns` 向对应用户补发「上次运行被打断,改动可能已部分完成」。

## 6. FAQ(同事高频问题,直接给答案)
- **Q:这工具到底做什么?** — A:见 §1。撞 5h 上限时后台等重置,重置就在所选项目里 `claude --continue` 续跑;可选飞书机器人做通知+双向操作(含图片收发)。
- **Q:本地开发任务跑完为什么会收到飞书?支持哪些软件?** — A:`install.ps1` 会串联安装 Codex `notify`、Claude Code `Stop`、Cline `TaskComplete`。Codex/Claude 的准确语义是“本轮响应已结束”,不是整个长会话永久关闭;Cline 是原生 TaskComplete,既有 hook 若返回 `cancel:true` 则不通知。Codex 只接收有本地 rollout 的顶层 thread;未持久化 turn、已持久化 subagent 和 `Documents\Codex\日期\slug` projectless 临时目录都不会通知,用户主动 fork 出来的独立顶层 thread 则保留。hook 只落一个小事件文件,飞书 agent 拒绝 UNC/设备路径后按真实本地 cwd/workspace/Git 根动态取项目名并发给 owner;malformed/schema 错误会被隔离而不阻塞后续事件,稳定 UUID + canonical/generation 七天索引防止损坏后重复。发送失败会重试,内部探测/飞书任务/自动续跑带 `AI_RESUME_INTERNAL_RUN=1` 不会重复提醒。`DeepSeek V4 for Copilot Chat` 不支持,因为它只能观察单次模型 API 响应,没有可靠的整个 Copilot Agent 完成事件;DeepSeek 走 Claude Code 或 Cline 时仍可通知。可用 `completionNotifyEnabled=false` 关闭。
- **Q:cc-connect 和飞书官方 lark-cli 已经用上了吗?** — A:已确定为目标组件并完成工具层安装:`cc-connect 1.4.1`、`lark-cli 1.0.81` 和 27 个官方 `lark-*` Skills,Skills 通过用户级真身提供给 Codex、Claude Code、Cline、GitHub Copilot。当前生产飞书仍由 Node agent 唯一消费;cc-connect daemon 未启动,lark-cli 也尚未写应用凭据/用户授权。下一阶段先用独立测试应用验证 cc-connect 的路由、原生 session、停止、进度、崩溃恢复和管理 API,通过后一次性切换,禁止新旧双消费/双写。lark-cli 是飞书 OpenAPI 能力层,不是本地编码任务完成判定器。
- **Q:它怎么知道额度什么时候恢复?靠估算吗?** — A:**不估算**。`checker` 固定间隔跑廉价探测 `claude -p "ready"`(`Test-ClaudeReady`),Claude 回的 `rate_limit_event` 带服务器权威的 `resetsAt`/`utilization`;探测显示可用**且之前观察到过限流**才 FIRE。任何模糊读一律 fail-closed(当作仍限流)。
- **Q:布防了但一直没动静,怎么排查?** — A:看 GUI 日志区(或 `logs\run-*.log`)。正常节奏:「等待中 · 下次实探 ~Nm」→ 限流后 4 分钟一探 →「额度已恢复 → 开始逐个续跑」。若见「探测未就绪」= fail-closed 在重试(网络/claude 未装);若根本没日志 = 计划任务没注册,重跑 `install.ps1`。布防在**未限流**时只保持监视,不跑任何东西。
- **Q:改了 `src/feishu-agent.js` 为什么没反应?** — A:线上从 AppDir 跑。Stage 1 后入口只是 wrapper,必须连同 `feishu-runtime.js`、六个边界模块、`session-manager.js`、`package-lock.json` 和整个 `ai/` 一起部署;缺 runtime/锁文件或 SDK 契约不符应由安装计划 fail-fast。然后只精确定位命令行入口等于当前 AppDir `feishu-agent.js` 的 node PID,逐个 `taskkill /PID <pid> /T /F` 结束完整 AI 子进程树并确认旧 PID gone → 等守护重启 → 确认恰好一个新 agent,且日志新增本次安装 generation 的结构化 BOOT/READY。重跑 `install.ps1` 会自动执行并验证这些条件,任一不可确认即返回非零。见 §4 / `CLAUDE.md`。
- **Q:以前白天点飞书按钮全没反应(「白天卡死」),怎么解决的?** — A:飞书 SDK 要等注册回调 resolve 才 ACK。现在 `dispatchEvent` 先 `setImmediate` 并按 chat 串行 handler:ACK 毫秒级返回,同一聊天的“清空→下一问”等状态转换仍严格保序;长 AI 任务再转入 `bg()`,`inflight` 同步占位防双跑。回归 `test/concurrency.js` + `test/session-pick.js`。
- **Q:飞书开发或自动续跑超过半小时会被截断吗?** — A:不会。项目修改、一次性执行和后台续跑都以 0 表示无计时器,计划任务也是 `PT0S`;飞书「停止」与 GUI「解除」作用域独立。查询/闲聊仍各自默认 30 分钟且 fallback 共用一份 deadline。后台每次布防生成新周期;Node/GUI/checker 共用 config 锁并在锁内读最新值,旧快照不能复活旧周期。spawn 前先落 `launching` 意图,随后登记 PID/父 PID/启动时间;下次 tick 会恢复主文件或完整临时代。CIM 的 failed 不等于 gone,父 PID、时间或 `claude --continue` 签名无法确认时一律保留登记并 fail-closed。
- **Q:迁移后的 AI 任务还会有 30 分钟总时限吗?** — A:不会。上条是现役 Node 行为;ADR-0002 的目标 C# Worker 对 chat/query/modify/resume/probe 全部采用 Start/Status/Cancel,不设客户端总时长。Worker 每 15-30 秒读取 RunStore 与进程存活性;静默、heartbeat 和输出间隔只显示指标。只有结构化 HTTP 408/504/`gateway_timeout` 是 provider timeout,DNS/TCP/TLS/reset、进程消失和监控异常是本地失败;用户停止与已开始副作用的 run 都不得自动 fallback/重放。
- **Q:点「停止」为什么以前停不掉、还把活干完了?** — A:`child.kill()` 在 Windows 上只杀外壳,子进程继续跑。现在 `killTree` 用 `taskkill /t /f` 终止整棵树;viewer 只能停止自己的查询/闲聊,owner 还能停止项目修改。
- **Q:✏️修改项目会续到哪个会话?会不会猜错?** — A:**不猜**。会话列表在后台读取,超过 250ms 会在同一控制卡显示加载态;加载态若因旧卡被删而补发,最终选择页会复用该替代卡,不会堆两张,替代窗口内后续点击也跟随执行时的 live 卡。项目卡绑项目,选择卡再用令牌绑用户 AI且一次性消费,双击不会把项目卡改成失效卡。切项目/模型/页面会取消旧读取并作废旧卡,所以旧 A 卡既不能操作 B,也不会把 A 会话续到 B;若项目已隐藏、移除或不在本事件的显式项目快照,旧 session/旧卡/文字进入会被拒绝并回到当前菜单。Claude/DeepSeek/Codex 读取失败都会显示重试/新开,不会冒充“没有历史”。选中后读取最近 2 轮摘要,发送前复核项目/会话/AI 仍匹配;已经切走就丢弃旧摘要,随后用对应 provider 的原生 resume。
- **Q:同事用机器人,消息会不会串到我这里?群里 @ 会乱吗?** — A:不会。每人的菜单/回复路由到**他自己与机器人的私聊**(`userChats`/`userTarget`),且 `userChats` **只从 p2p 消息学习**——群 @ 不污染映射;`onCardAction` 完全不写 `userChats`(卡片事件无 chat_type,写了会把私聊路由进群);没映射时用 `od:<open_id>` 兜底、首条 p2p 自动迁移。通知聊天 `feishuChatId` 只认**明确在 `feishuAuthOpenIds` 里的 owner** 的 p2p 消息绑定。回归 `test/routing.js`。
- **Q:谁能改我的项目?怎么控权限?** — A:只有 `feishuAuthOpenIds`(**owner/full**)能改项目/改配置/授权/停止/浏览会话卡;**其他所有人自动只读 viewer**(浏览/查询)。空名单 = 未锁定 = 人人可改,移除最后一个 owner 会解锁,GUI 会警告。闲聊对所有人开放,但**非 owner 的闲聊是 plan 模式且禁文件/执行工具**(`test/chat-security.js`)。
- **Q:「只读查询」真的安全吗?会不会读到 `config.json` 里的密钥?** — A:非 owner 的 Claude/DeepSeek 显式禁文件工具;Codex 还使用 `--ignore-user-config --ignore-rules` 并关闭 shell/apps/multi-agent/image/web/memory,再显式补回最小 OpenAI provider。安全边界来自**工具不可用**,不是模型拒答。应用在 provider 启动前还逐组件拒绝项目路径/`AI_GUIDE.md` 的 symlink/junction/reparse,验证 realpath containment、普通单链接文件和已打开句柄一致性;失败形成本地终态。`query-security`/`chat-security` 用真实 GPT-5.6 Sol + 合成 canary 验证,其中 query canary 覆盖 junction guide 且必须在 provider 前拒绝。
- **Q:「只读查询」会不会串会话?** — A:不会。查询按 **(project,用户,profile)** 隔离 cwd 和会话;切 AI 就切到另一份原生上下文。`查询/只读` 前缀必须带空格或冒号。
- **Q:在会话里回答 AI 的「选 A」为什么以前会跳回菜单?** — A:旧版任何模式都做模糊命令匹配。现在模糊匹配/解锁/密码只在 idle 生效;会话内自由文本一律归会话。
- **Q:我和同事能各用各的 AI 吗?模型菜单为什么没 Claude?** — A:能,`feishuChatProfile` / `feishuUserProfiles` 分开保存。模型卡先列实测可用的 provider 父级,再列它的模型子项;快照超过 5 分钟会先刷新,刷新中不给按钮。Claude 登录/订阅/额度实探失败时整组隐藏,旧卡和 `模型 claude` 也不能绕过;恢复后重新打开模型菜单即可出现。Fable 5 仍 owner-only。
- **Q:GUI 的「可用」是不是只看我填没填 API Key?代理会不会强制接管?** — A:都不是。正常打开窗口会实测 OpenAI、DeepSeek 和 Claude;OpenAI/DeepSeek 先按大小写不敏感规则清除进程代理直连,仅网络类失败才尝试 `aiProxy`,认证/额度/模型错误不会换线。成功后分别显示绿色「直连可用」或「代理可用」并缓存 5 分钟,失败只缓存 30 秒;密钥、端点或代理变化会立即作废旧线路。若超时子进程尚未真实退出,不会再起第二条线路或新探测,真实 close 后才清理并重新允许检测。双线路均失败显示「代理异常」。正式任务固定使用缓存线路一次,不会因超时在中途换线重放;等待检测时停止或总时限耗尽都不会再启动正式请求。`aiProxy` 不用于 Claude,也不修改 Windows/Clash。不可用 provider 的整组模型会隐藏,配置入口仍保留。Claude 的同一次探测还驱动 5h/7d 额度区,失败绝不显示「空闲」。
- **Q:DeepSeek 的“V4 / V4 Pro”实际调用什么?** — A:V4 → `deepseek-v4-flash[1m]` + `high`;V4 Pro → `deepseek-v4-pro[1m]` + `max`,都走官方 `https://api.deepseek.com/anthropic`。`deepseekMillionContext=false` 可去掉 `[1m]`,`deepseekEffort` 可覆盖 effort。
- **Q:会话越来越多把电脑弄乱怎么办?** — A:GUI 顶部「会话」按项目/用户/AI/状态列出 provider-native 会话。飞书闲聊/查询 14 天未用自动归档、30 天自动删除;Claude 归档连 transcript+artifact 一起可恢复移动,Codex 走原生 archive/unarchive/delete。项目工作会话**永不自动删除**,只能手动操作。
- **Q:图片怎么收发?** — A:**双向**。单独图片按聊天+发送者挂起到该用户下一条文字;同一气泡的“图片+问题”是 `post`,会解析语言包文字(含 `@user_id/@all` 回退),按文档顺序下载最多 6 张(单张 10MB),与文字原子绑定后在同一轮交给 AI,owner 项目查询/修改/闲聊都支持。暂存图与 post 图合并后每轮仍最多 6 张,当前 post 图优先并明确提示超额数。后到图片或群内其他人的图片不会串入;忙碌/拒绝会回滚,运行后删除,孤儿满 24h 后每小时同时清磁盘与内存队列。非 owner 因无文件工具会明确拒绝并删除图片;配置不可读/身份缺失/allowlist 未命中时在下载前拒绝整条事件。出站 AI 把图存 `feishu-out\<hash>\`,只在上传+图片消息都成功后删除;失败保留文件并告知真实路径。需 `im:resource`。
- **Q:飞书里收到的结果为什么是卡片?markdown 符号还会糊一脸吗?** — A:飞书纯文本不渲染 markdown。结果统一走 `sendResult` 发交互卡片(lark_md),`mdToLark` 把 `#`标题→粗体、`-`列表→`•`、表格行→`a · b · c`、代码块/反引号剥壳;超 9000 字符截断并提示去 VS Code 看全文;卡片失败退化纯文本。尾缀 `⏱ Ns · 输出 N tokens · ≈ $X` 让每次问答开销透明。
- **Q:为什么全用 PowerShell 5.1?有坑吗?** — A:兼容系统自带。坑已处理:`.ExitCode` 退出后变 `$null`(改看 stream-json result 行)、`Set-Content -Encoding UTF8` 写 BOM 崩 Node(改 `WriteAllText` 无 BOM)、`ConvertFrom-Json` 偷偷转时区(改存 Unix 整数)、`.ps1` 必须 UTF-8 **带 BOM**、cmd 把 `-p "多行"` 在首个换行截断(prompt 全走 stdin)。详见 [LESSONS.md](docs/LESSONS.md)。
- **Q:续跑会不会搞丢我未提交的改动?** — A:不会白丢。全自动模式每项目续跑前 `Protect-GitRepo`:未提交改动自动 `git stash push -u`(留名 `claude-resume-guard <时间戳>`),或 `dirtyGuard="branch"` 建 `claude-resume/<时间戳>` 分支。非 git 目录不保护,谨慎勾选。
- **Q:新同事想用机器人,要做什么?** — A:飞书后台把他加进应用「可用范围」;他私聊机器人发「帮助」即可用,自动是 viewer(闲聊+浏览+只读查询)。要给改项目权限:owner 发 `授权 ou_xxx`(他首次发消息时会展示 open_id),或 GUI「授权用户」窗口。
- **Q:飞书里有哪些文本命令?** — A:导航:`菜单`/`项目`、idle 下 `进入 <编号|名字>`、`退出`;干活:`查询 <问题>`、`停止`(任何人停自己的任务)、`停止 <项目>`(owner);记忆:`忘记闲聊`/`忘记查询`;AI:`模型 sol|v4|v4pro|claude|opus...`(按人保存);owner 配置:`授权/取消授权`、`授权列表`、idle 下 `解锁 <密码>`。

## 7. 术语表(中英 / 缩写对照)
| 术语 | 含义 |
|---|---|
| 布防 / 解除 (Arm / Disarm) | 布防=开启后台自动续跑监视;解除=终止该后台周期及其当前 Claude 进程树,不影响飞书任务 |
| 预演 (Preview / DryRun) | `checker.ps1 -DryRun`,只报计划不探测不续跑 |
| 探测 (Probe) | `Test-ClaudeReady`:廉价 `claude -p "ready"`,读服务器 `rate_limit_event` 取 resetsAt/utilization |
| Provider 健康探测 | GUI 启动/刷新实探三家;飞书启动、过期模型菜单及正式任务需要过期线路时也实探。OpenAI/DeepSeek 直连优先、仅网络失败代理回退,成功线路缓存 5 分钟;只有成功 provider 才暴露模型按钮/下拉项 |
| fail-closed | 任何模糊/失败读一律当「仍限流」,绝不误 fire |
| FIRE / 续跑 (Resume) | 依次在所选项目跑 `claude --continue -p "continue"` |
| 一次性 (one-shot) | 成功续跑后自动 `enabled=false`(`continuous=true` 可关) |
| git 脏检查 (dirty-guard) | 续跑前未提交改动自动 `git stash`(或建分支)保底可恢复 |
| 只读查询 (read-only query) | 按 **(project,用户,profile)** 隔离的 Q&A;首建注入 AI_GUIDE;非 owner 禁全部文件/执行工具 |
| 工具分级 (tool-gating by caller) | Claude/DeepSeek 用 `disallowedTools`;Codex 非 owner 还忽略用户配置/规则并关闭全部扩展工具 |
| 修改项目 (modify) | ✏️:先挑 provider-native session(或 🆕),之后用对应 Codex/Claude resume;owner-only |
| 会话选择卡 (session picker) | `requestSessionCard`:后台枚举当前 profile 最近 5 个原生会话 + 🆕;令牌绑定 chat+项目+profile,可取消、防过期卡串项目 |
| 会话生命周期 (session lifecycle) | `session-manager.js`:scratch 14 天归档/30 天删除;work 仅手动;GUI/agent 共用同一真身 |
| 会话归档 (session archive) | Claude 移 JSONL+artifact 到 AppDir archive;Codex 调原生 thread archive;都可恢复 |
| 闲聊 (chat) | 按用户+profile 的 scratch 会话,不碰项目;非 owner 无文件/执行工具 |
| owner / viewer | owner=在 `feishuAuthOpenIds`,能改+配置+授权;viewer=其他所有人,自动只读 |
| 按用户路由 (per-user routing) | `userChats`(open_id→私聊,仅 p2p 学习)+ `userTarget`:每人的回复进自己私聊 |
| od: 伪目标 | 用户建映射前(如先点底部菜单)状态暂存 `od:<open_id>`;首条 p2p 消息迁移到真实私聊 |
| 逃生舱 (escape hatch) | 底部菜单 菜单/idle/exit(及未知 key):任何状态回 idle+补发新主菜单卡;🤖模型/🛑停止例外(不动会话) |
| 后台执行 (dispatch / bg / inflight) | `dispatchEvent` + `setImmediate` 先回 ACK并按 chat 保序;长 AI 转 `bg`;`inflight` 同步占位防竞态 |
| killTree | `taskkill /pid <pid> /t /f`:杀 AI CLI 的完整包装进程树(`child.kill()` 可能只杀 cmd 外壳) |
| 进度卡 (progress card) | `startProgress`:一次运行**一张独立卡**原地 patch,tick/stop 串行且不改 `lastCard` |
| 中断汇报 (interrupted-run report) | `feishu-inflight.json` 落盘在飞运行;进程死后重启 `reportInterruptedRuns` 告知等待者 |
| 控制卡 (control card) | `enqueueControlCard` 按 chat 串行;`lastCard`+可见性代次决定 patch 还是底部新建;项目卡绑 project hash |
| mdToLark / 结果卡片 | markdown→lark_md 规整(标题→粗体、列表→•、表格→`a · b · c`、去代码壳);`sendResult` 发卡,失败退化纯文本 |
| 图片通道 (image channel) | 入站:`image` 按 chat+sender 挂起,`post` 与暂存图合并后每轮最多 6 图(post 优先),单张≤10MB,与文字同轮原子处理并在运行后清理;出站:AI 存 `feishu-out\` → 上传发送;都需 `im:resource` |
| 按用户 AI (per-user profile) | owner 存 `feishuChatProfile`,其余人存 `feishuUserProfiles[open_id]`;旧 model 字段仅兼容镜像 |
| 两级模型选择 (provider → model) | 先选实测可用的 OpenAI/DeepSeek/Claude 父级,再选子模型;不可用 provider 整组隐藏,旧卡/文字命令同样受限 |
| AI Profile | 提供商 + 模型 + 本地执行引擎的组合,如 `openai-sol` / `deepseek-v4-pro` / `claude-sonnet` |
| WSClient / 长连接 | 飞书 SDK 持久 WebSocket,收事件无需公网 IP;handler resolve 后才 ACK |
| rate_limit_event | Claude stream-json 里带 `resetsAt`/`utilization`/`rateLimitType`(five_hour/seven_day)的行 |
| 更新导览 (project tour) | GUI 按钮/`Invoke-ProjectTour`/`project-tour` skill:生成本 AI_GUIDE.md,查询提速提准省 token |
| AppDir | `%LOCALAPPDATA%\ClaudeResume`,程序运行态目录(源码改动必须复制过去才生效) |

## 8. 文档索引(深挖时读)
- [README.md](README.md):AI Resume 工作台、安装、显式模型配置、会话生命周期、飞书配置、图片收发与权限模型——面向用户的完整说明。
- [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md):内部原理——checker 状态机、探测/读重置、GUI 单实例/按需探测、飞书状态机/卡片/路由/安全/图片/进度与崩溃恢复、`config.json`/`state.json` **全字段**、测试清单。深挖实现先读它。
- [docs/LESSONS.md](docs/LESSONS.md):真实踩坑+开发史(PS 5.1 exit-code/BOM/时区、cmd 换行截断 `-p`、`--continue` 池污染、plan 模式读漏洞、卡片堆叠/抢卡、mock 与真实 API 形状、长跑三坑、**测试绝不对真实项目跑修改**的墨菲现场、日志系统约定)。排查「为什么这么写/为什么有这 bug」看它。
- [docs/UPSTREAM-ARCHITECTURE-RESEARCH.md](docs/UPSTREAM-ARCHITECTURE-RESEARCH.md):2026-07-31 固定快照下对 cc-connect 与飞书官方 lark-cli 的源码级研究，说明哪些边界可借鉴、哪些能力不能直接替代，以及推荐的渐进迁移顺序。
- [docs/RECOVERY-AUDIT-20260801.md](docs/RECOVERY-AUDIT-20260801.md):失控执行后的双工作区、AppDir/内存分裂、S1-A/S1-B 独立审查证据和恢复后的详细工作包顺序。
- [docs/MIGRATION-BASELINE.md](docs/MIGRATION-BASELINE.md):迁移开始时的现役版本、运行拓扑、测试证据与外部前提。
- [docs/STATE-OWNERSHIP.md](docs/STATE-OWNERSHIP.md):current/shadow/target 状态真身和禁止双写规则。
- [docs/EVENT-CONTRACTS.md](docs/EVENT-CONTRACTS.md):跨进程事件、命令、幂等与错误分类契约。
- [docs/RUN-CONTRACT.md](docs/RUN-CONTRACT.md):目标 C# Worker 的 Start/Status/Cancel、状态机、错误、fallback、side effect、持久化和恢复真身。
- [docs/STAGE-2-SPEC.md](docs/STAGE-2-SPEC.md):C# Stage 2 六组件骨架规格与验收;§7 为实现状态、已识别偏差与冒烟记录。
- [docs/STAGE-3-SPEC.md](docs/STAGE-3-SPEC.md):lark-cli 能力层试点规格与验收;§7 为交付记录。
- [docs/STAGE-4-SPEC.md](docs/STAGE-4-SPEC.md):cc-connect 试点规格与验收;§7 为交付记录与发现。
- [docs/STAGE-5-SPEC.md](docs/STAGE-5-SPEC.md):产品状态迁移(C# shadow)规格与验收;§7 为四包交付记录。
- [docs/MIGRATION-DEBT.md](docs/MIGRATION-DEBT.md):已知缺陷的处理阶段、关闭条件和证据。
- [docs/adr/0001-target-architecture.md](docs/adr/0001-target-architecture.md):已接受的目标架构决策。
- [docs/adr/0002-run-lifecycle-contract.md](docs/adr/0002-run-lifecycle-contract.md):已接受的无客户端总时限运行生命周期决策。
- [CLAUDE.md](CLAUDE.md):协作约定——中文沟通、v2 版本/文档真身、精确部署重启、离线自测命令、安全约束。
- [.agents/skills/project-tour/SKILL.md](.agents/skills/project-tour/SKILL.md):本导览的生成流程(GUI「更新导览」按钮即其 headless 版)。
