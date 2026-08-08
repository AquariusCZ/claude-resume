# ADR-0003:直接使用 cc-connect,AI Resume 收敛为控制面与续跑引擎

- 状态:**Accepted**(方向由用户 2026-08-06 决策:「悬崖勒马,直接使用 cc-connect 这个成熟上游,而不是发现风险后在错误的道路上狂奔」);§5 GUI 技术选型为本 ADR 提出的推荐,标注确认状态
- 日期:2026-08-06
- 决策者:用户(架构权威)
- 关系:**修订 ADR-0001 的「C# 与 cc-connect 边界」一节**;不改变 ADR-0002(RunContract)的运行语义,但大幅缩小其适用面(见 §4.3)
- 关联:`docs/UPSTREAM-ARCHITECTURE-RESEARCH.md`、`docs/MIGRATION-DEBT.md`(D-014)、`docs/STAGE-4-SPEC.md` §7、`docs/STAGE-6-SPEC.md` §7.4

## 1. 背景:为什么在 Stage 6 中途翻案

ADR-0001 确定「cc-connect 作为目标编排内核,C# wrapper 承接 AI Resume 自有语义」。Stage 4 试点与 Stage 6 前三个工作包实施后,四条实证推翻了该边界的成本假设:

**实证 1:wrapper 补丁面持续扩大。** `AiResume.Wrapper` 已 1087 行 / 5 类职责(进程编排、配置生成、会话生命周期、授权映射、run 状态推导 + 注入幂等),且 S6-D 尚未完成即已排出后续补丁需求。「用上游省事」的前提是补的量小于自己写的量,该前提正在失效。

**实证 2:我们在重写上游已有的能力。** cc-connect 1.4.1 锁定源码(commit `5d4c96dd`)中已存在:
- `agent/claudecode/claude_usage.go` + `agent/codex/usage.go`:Claude 与 Codex 的**限额读取**实现;
- `core/interfaces.go:484` `UsageReporter` 接口 + `UsageReport{Buckets,Windows{UsedPercent,ResetAfterSeconds,ResetAt,LimitReached}}`;
- `core/engine.go`:`/usage` 命令、`renderUsageCard`、`formatReplyFooterUsage`(回复页脚用量)。

**Stage 5-B 的 `ClaudeCodeProbe`(17 测试)与上游能力重复,属已发生的返工。**

**实证 3:上游不会替我们补基础设施。** 1.5.0-beta.1(2026-07-06)= 新增腾讯元宝/cloud_web/Reasonix 平台 + `agent_session_idle_timeout_mins` + Feishu `mention_map` + ru i18n;1.5.0-beta.2(2026-07-14)= 修 codex `/model` 可见性。**D-014 记录的八条(`/provider` 缺失、管理 API 无 reload、无会话清理、无 run 状态机、`send` 不承载停止、cron 不支持 bridge 会话、无多模态)零修复**,且截至 2026-08-06 已 3 周无新 beta。上游开发重心是**横向加平台**,不是纵向补基础设施。等待上游修复不是可行策略。

**实证 4:AI Resume 的核心价值被反向证实。** 全量检索 `LimitReached` 的消费方:该字段只被**读取与展示**,`core` 中不存在任何 requeue / wait-for-reset / 限额后自动继续的编排;检索 `auto.?retry|retry.?after|wait.*reset|requeue|backoff` 仅命中消息发送的 500ms/1500ms 短 backoff。

> **「Claude 限额撞墙后排队等待、恢复后自动续跑」是 cc-connect 明确不具备的能力。这是 AI Resume 不可替代的存在理由。**

## 2. 决策

### 2.1 产品定位翻转

| | 旧定位(ADR-0001) | 新定位(本 ADR) |
|---|---|---|
| AI Resume 是什么 | 飞书 AI 机器人 + 附带 GUI | **本机 AI 工作台控制面 + 续跑引擎** |
| cc-connect 是什么 | 被 wrapper 包装的编排内核 | **直接运行的消息/会话/agent 层,不包装** |
| 集成方式 | wrapper 承接自有语义 | **接受 cc-connect 的用法约定,经其既有接口驱动** |

**核心原则:不再为了改变 cc-connect 的用法而包装它。** 用法差异(如 `send` 不承载停止、停止走 bridge、provider 切换走管理 API、配置变更需重启而非 reload)一律**适配而非改造**。

### 2.2 能力边界(唯一真身)

**cc-connect 直接负责,AI Resume 零代码**:飞书/多平台协议、会话编排与持久化、agent 进程与 turn 生命周期、停止(`/stop` 经 bridge)、限额**读取**、cron 定时、崩溃后会话恢复、Web admin。

**AI Resume 只做四件事**:

1. **限额后自动续跑编排**(唯一不可替代的核心):消费 cc-connect 的用量数据,在 `LimitReached` 后按 `ResetAfterSeconds` 排队,恢复后按项目顺序自动继续。
2. **动态项目发现**:不硬编码项目数量/名单。
3. **本机完成通知**:见 §3。
4. **Windows 控制面 GUI**:见 §5。

> AI Resume 退化后,**GUI 从外围升级为其主要用户界面**;GUI 质量即产品质量。

### 2.3 限额数据来源

> **【2026-08-06 修订:本节原判断已被平台实证推翻,以下为修订后结论。】**
> 原文为"不再自行探测 Claude 限额,改为消费 cc-connect 的 `UsageReport`(经管理 API 或等价入口)"。

**修订后结论:限额数据由 AI Resume 自取,但保持 `UsageReport` 兼容形状。** 三条实证(详见 `docs/design/s7c-quota-probe-spec.md` §1):

1. cc-connect 的 `GetUsage` 并非读 API,而是 **PTY 起 Claude Code TUI → 发 `/usage` → ANSI 抓屏 → 正则解析**;
2. 它依赖 `github.com/creack/pty v1.1.24`。`run.go` 无构建约束(Windows 照常编译),但其调用的 `open()` 来自 `pty_unsupported.go`,该文件构建约束 `//go:build !linux && !darwin && !freebsd && !dragonfly && !netbsd && !openbsd && !solaris && !zos` **命中 Windows**,函数体为 `return nil, nil, ErrUnsupported`。故 **`GetUsage` 在 Windows 上必然失败**;
3. cc-connect 管理 API 路由全表(`core/management.go:218-252`)**无任何 usage 端点**,`/usage` 只是聊天命令,必须经会话。

"限额后自动续跑"是 §2.2 列明的本产品**唯一不可替代的核心**,不能建在目标平台不通的通道上。因此:

- 取数复用 S5-B 已实现的 `ClaudeCodeProbe`(读 `claude -p --output-format stream-json` 的 `rate_limit_event`,得到服务端结构化 `resetsAt`/`rateLimitType`/`utilization`),**比上游的抓屏方案更可靠**;
- 经 `UsageSnapshotMapper` 映射成与 `UsageReport` 同形状的 `UsageSnapshot`,将来上游在 Windows 可用时可无痛切换取数实现;
- **关闭条件**:上游为 Windows 提供可用的 usage 通道(ConPTY 或管理 API 端点)且实测通过后,可切回消费上游。

**实测的字段可得性不对称**:`resetsAt` 常态下发,`utilization` 仅在高用量时下发。因此 GUI 潮汐轴以**时间窗口**为轴(起点由 `resetsAt - windowSeconds` 推导并标注为推导),用量百分比作为独立读数;未下发时显示"用量未报告",**不得渲染成 0%**。

## 3. 本地完成通知:可配置注册表

现役硬编码 Codex / Claude Code / Cline 三个边界。本 ADR 改为**用户可选择启用的适配器注册表**,并新增两个已验证具备可靠边界的 provider:

| Provider | 完成边界机制 | 可靠性 | 状态 |
|---|---|---|---|
| Claude Code | `Stop` hook | ✅ 可靠 | 现役 |
| Codex | `notify` | ✅ 可靠(仅顶层持久化 thread) | 现役 |
| Cline | `TaskComplete` | ✅ 可靠 | 现役 |
| **Qoder** | `Stop` hook(`~/.qoder/settings.json` → `hooks`;agent 完成响应且无更多工具调用时触发) | ✅ 可靠,与 Claude Code 近乎同构 | **新增** |
| **OpenCode** | 插件 `session.idle` 事件(TS 插件,`~/.config/opencode/plugins/`) | ✅ 可靠(agent 完成响应时触发) | **新增** |
| DeepSeek V4 for Copilot Chat | provider 回调 | ❌ 仅单次模型请求结束 | **保持拒绝** |

**准入标准不变(红线)**:只接受代表**整个 agent 任务结束**的边界;代表单次模型请求/流式分片结束的回调一律拒绝。新增 provider 必须先证明其边界语义,再进注册表。

**实现要点**:
- Qoder hooks 支持用户级/项目级**多级合并且不覆盖**,与现役 `install-completion-hooks.js` 的合并语义一致,可复用。
- **Qoder `Stop` hook 必须检查 stdin payload 的 `stop_hook_active`,为 true 时立即 `exit 0`**,否则触发「阻断→重试→再阻断」无限循环。
- Qoder 提供 `QODER_SESSION_ID` / `QODER_CWD` 环境变量与 stdin JSON(`session_id`/`cwd`/`transcript_path`),项目归属可直接解析。
- OpenCode 插件 context 提供 `project` / `directory` / `worktree`。
- 注册表由 GUI 提供开关;**未启用的 provider 不写入任何 hook 配置**;卸载必须能干净移除且不破坏用户既有 hook。
- `AI_RESUME_INTERNAL_RUN=1` 抑制自触发的规则对全部 provider 一致适用。

## 4. 作废与重估清单

| 项目 | 处置 | 理由 |
|---|---|---|
| `CcConnectSessionBridge`(171 行) | ~~作废~~ → **保留**(2026-08-06 复核) | 原判「交还 cc-connect」不成立:上游 `sessions prune` 只做去重/清空,**没有按年龄的归档删除**,交还即等于功能消失。详见 STAGE-6-SPEC v2 §2 |
| `CcConnectAuthMapper`(97 行) | ~~重估~~ → **保留,缩小职责**(2026-08-06 复核) | cc-connect 的 `isAdmin` 只门禁特权命令(`core/engine.go:1177`),没有「非 owner 禁全部文件工具」这层,而后者是本产品安全红线;`allow_from` 只收敛「谁能进」 |
| `CcConnectRunMapper`(229 行) | ~~大幅缩减~~ → **已删除**(2026-08-06) | 续跑引擎跑**自己**的 `claude --continue` 进程(S7-D),从不消费 cc-connect turn 事件;全仓零引用 |
| `CcConnectSupervisor`(426 行) | **保留并简化** | 进程编排仍需要(启动/停止/崩溃重启),去掉为改造用法而写的部分 |
| `CcConnectConfigGenerator`(164 行) | **保留** | 确定性生成仓库外 config.toml 仍有效 |
| S5-B `ClaudeCodeProbe`(17 测试) | ~~作废~~ → **恢复启用**(2026-08-06) | 原因见 §2.3 修订:上游 `GetUsage` 依赖 `creack/pty`,Windows 上返回 `ErrUnsupported`,管理 API 亦无 usage 端点。本探测是该平台上唯一可用通道,且读的是服务端结构化事件而非抓屏 |
| S6-D 场景 4/6/7 | **暂停** | 验证目标随边界变化;按新边界重新定义后再执行 |
| ADR-0002 RunContract | **保留但缩小适用面** | 仍适用于 AI Resume 自己启动的进程(续跑、探测);不再要求映射 cc-connect 内部 turn |

## 5. GUI 技术选型(推荐,待用户确认)

### 5.1 问题实测

现役 `picker.ps1`(1094 行 + `lib.ps1` 818 行,XAML 内联于 PowerShell 字符串):**冷启动到首帧渲染实测 3701 ms**(2026-08-06,`-RenderTo` 离屏渲染,无真实探测)。现代桌面应用期望 <500 ms。

根因分解(需实现时逐项验证):PowerShell 5.1 + .NET Framework 启动开销、XAML 字符串运行时解析、UI 线程上的同步 I/O(检出 15 处项目扫描/git 状态调用)。

视觉侧问题不是「难看」而是「平」:几乎无图标(仅 logo)、字重层级扁平(右侧三行纯文本状态)、橙色主按钮与暗色调饱和度冲突、日志区大面积留白、信息密度偏低。图标字体用 `Segoe MDL2 Assets`(Win10 时代),Win11 应为 `Segoe Fluent Icons`。

### 5.2 选项

| 方案 | 设计自由度 | 预估冷启动 | 风险 |
|---|---|---|---|
| A. C# WPF + 现代控件库(WPF-UI 等) | 中 | ~300-500 ms | XAML 达成 Claude/Apple 级质感成本高,动效与排版控制弱 |
| B. WinUI 3 | 中高 | ~500-800 ms | Windows App SDK 部署复杂、生态不成熟 |
| C. Avalonia | 中高 | ~400 ms | 仍是 XAML 范式;跨平台能力本项目用不上 |
| **D. C# 壳 + WebView2 + Web 前端**(推荐) | **高** | WebView2 初始化 ~200-400 ms + 渲染 ~100 ms | 需前后端 IPC;WebView2 冷启动开销;打包复杂度上升 |

### 5.3 推荐 D 的理由

1. 用户目标是「参考 Claude / Google / Apple design」的观感——**这些是 Web/移动端设计语言,在 XAML 中复刻是逆流**。
2. 字体与排版:Web 栈可完全控制字体族、字重、字距、行高与可变字体,直接解决「字体支持不全」。
3. 已有 `AiResume.Ipc`(Named Pipe)与独立 Worker 进程,前后端分离是既有事实而非新增复杂度。
4. WebView2 Runtime 在 Windows 11 为系统组件,无需分发。
5. 可直接使用官方 `frontend-design` / `web-design-guidelines` skill 体系。

### 5.4 与启动速度无关的架构约束(任何方案都适用)

**首帧渲染不得依赖项目发现/健康探测等 I/O。** 窗口骨架先出,数据异步填充。现役 3701 ms 的主要成分是同步 I/O,换栈本身不解决该问题。

### 5.5 控制面请求契约(前端 postMessage → `ControlPlaneBridge`)

应答统一为 `{id, type:"<请求>.result"|"<请求>.error", payload, error}`;**任何请求都不在 UI 线程做 I/O**。

| 请求 | 输入 | 作用 |
|---|---|---|
| `app.info` | — | 版本、shadow 根、`quotaRefreshMinutes` |
| `projects.list` | — | 续跑队列 + `hidden` 已移除清单 |
| `projects.add` | `path` | 手动加入队列(写 `customProjects`,并解除该路径的隐藏) |
| `projects.remove` | `path` | 移出队列(退出 `customProjects`、进入 `hiddenProjects`、退出 `selected`) |
| `projects.restore` | `path?` | 恢复单个;省略 `path` 则全部恢复 |
| `dialog.pickFolder` | — | 宿主弹原生选目录框;取消返回空 `path`,**不是错误** |
| `quota.get` / `quota.local` | `force?` | 服务端 7 天窗口 / 本地 5 小时块 |
| `arm.get` / `arm.set` | `armed`,`paths[]`,`continuous?` | 读写布防 |

**手动增删的持久化真身是 shadow `config.json` 的 `customProjects` / `hiddenProjects`**,与项目发现共用同一份配置指纹缓存,不另建列表。「添加」与「移除」必须互为逆操作——只写一边会让移除过的目录再也添加不回来。移除还必须同步清理 `selected`,否则用户在界面上删掉的项目引擎照样会去续跑;若因此清空了已布防的选择,连带解除布防。

**配置写入一律走 `ProductConfigStore.Update`(锁内重读 + 只改本次负责字段)。** GUI 与 Worker 续跑引擎并发写同一份配置,锁外读快照再整体写回会互相覆盖(红线见项目 `CLAUDE.md`)。

### 5.6 确认状态

§5 为推荐方案。若用户否决 D,回退顺位为 A(栈统一优先)。**该选择只影响 Stage 7 实现方式,不影响 §2-§4 的边界决策。**

## 6. 阶段重规划

| 阶段 | 原内容 | 新内容 |
|---|---|---|
| 6 | cc-connect 经 wrapper 接管测试应用 | **收敛**:直接运行 cc-connect,验证停止/崩溃恢复/授权可用;删除为改造用法而写的 wrapper 面 |
| 7 | GUI 迁移 | **升级为重点**:控制面重构(选型见 §5)+ 视觉系统 + 首帧不阻塞 |
| 8 | Hook 与部署 | **扩展**:完成通知可配置注册表 + 新增 Qoder/OpenCode(共 5 provider) |
| 9 | 数据迁移演练 | **缩小**:会话状态交还 cc-connect,只迁移 AI Resume 自有状态(续跑周期、项目清单、通知去重) |
| 10 | 生产切换 | 不变(仍需用户显式授权维护窗口) |
| 11 | 收尾 | 不变 |

## 6.1 执行约束放松(用户澄清,2026-08-06)

用户明确三点,显著改变后续阶段的风险模型:

1. **用户已停止使用现役 AI Resume**,期待的是重构后的成品。→ **不再投入现役维护**;`docs/design/gui-startup-profile.md` 定位的现役 3701ms 性能问题**不在现役上修复**,其价值转为**新实现的设计约束**(项目发现必须索引化,不得沿用 O(全部历史会话) 的全量扫描)。
2. **允许直接在生产环境修改与测试。** → 取消「独立测试应用 + 测试项目」的强制前提;Stage 10「生产切换」不再需要维护窗口式的谨慎流程。
3. 飞书侧**本就只有一个在用应用**(`cli_xxxxxxxxxxxxxxxx`);为迁移创建的 `cli_xxxxxxxxxxxxxxxx` 未使用且即将关停。

**仍然保留的边界(不因上述放松而取消)**:
- **同一飞书应用同一时刻只能有一个消费者在线**——这是故障风险(重复回复、状态竞争),不是合规要求。验证 cc-connect 前须停掉现役 node agent,反之亦然。
- 凭据不进仓库/日志/测试输出。
- 不破坏用户既有数据与既有 hook 配置。
- 破坏性或不可逆操作仍需用户确认。

## 7. 替代方案(评估)

- **继续 wrapper 全量补齐**:被本 ADR 的实证 1-3 否决,补丁面无收敛迹象。
- **升级到 1.5.0-beta**:实证 3 表明 D-014 零修复,升级不解决问题;且 beta 引入稳定性风险。**维持锁定 1.4.1 commit `5d4c96dd`**。
- **fork cc-connect 为私有 Go 分支**:ADR-0001 已否决(维护成本),本 ADR 维持否决。
- **向上游提 PR**:长期最优,但不能作为当前路径的前提(上游节奏不可控)。**列为后续可选行动,不阻塞本 ADR。**
- **放弃 cc-connect 自研**:与实证 4 冲突——飞书/多平台/会话编排是通用能力,自研回到 ADR-0001 之前的单体困境。

## 8. 后果

**正面**:消除持续扩大的 wrapper 债务;止损已发生的重复实现;产品定位清晰(控制面 + 续跑引擎);GUI 获得应有投入。

**负面与风险**:
1. Stage 5-B 与 Stage 6 部分产出作废,已投入工时不可回收。
2. 依赖 cc-connect 的用法约定,上游行为变更将直接影响本产品;**必须维持版本锁定 + 升级前重跑场景验证**。
3. ~~限额数据依赖上游 `UsageReport` 的准确性~~ → 已按 §2.3 修订改为自取:代价是 Windows 上多维护一条探测路径,收益是核心功能不受上游平台缺口拖累;需持续关注上游是否补齐 Windows 通道以便切回。
4. GUI 选型 D 引入 WebView2 依赖与前后端 IPC 复杂度。

**验收**:本 ADR 生效后,`docs/ARCHITECTURE.md`、`docs/MIGRATION-BASELINE.md`、`docs/MIGRATION-DEBT.md`(D-014 重定性)、`AI_GUIDE.md` 首行标记、各 `STAGE-*-SPEC.md` 须同步;`CLAUDE.md` 中「cc-connect 是目标飞书消息 + Agent 会话编排内核」一段须按 §2.2 改写。
