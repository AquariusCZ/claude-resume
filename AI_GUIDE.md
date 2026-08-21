<!-- project-tour · generated 2026-08-21 02:08 · git 439d72e + working tree -->
# AI Resume — AI 导览(AI_GUIDE.md)

> 一句话:AI Resume 是 Windows 本地控制面,在 Claude Code 额度恢复后按队列续跑项目,并整合项目发现、完成通知、cc-connect 聊天接入与运行安全验证。
> 本文件供 AI **只读问答**优先加载:常见架构、运行、配置和故障问题先查这里;深挖再按文末索引读取正式文档。

## 1. 定位

- **用途**:管理本机 AI 编码 agent 的额度后续跑、项目队列、服务健康、完成通知和手机聊天入口。
- **使用者 / 场景**:Windows 上同时使用 Claude Code、Codex 等 CLI,希望长任务无人值守运行,并通过飞书/微信查看或驱动项目的个人开发者。
- **当前版本**:v2.0.0;版本真身是 `csharp/Directory.Build.props` 的 `<Version>`。
- **现役实现**:`csharp/` 下的 .NET 10 C#。v1 PowerShell + Node 已于 2026-08-08 从工作树删除,只能通过 Git 历史查阅。
- **技术栈**:.NET 10、C#、WPF、WebView2、SQLite/WAL、Windows Named Pipe、DPAPI、cc-connect v1.4.1、lark-cli。
- **产品边界**:AI Resume 只做限额后续跑、项目发现、完成通知和 Windows 控制面;聊天平台协议、会话、agent turn、cron 交给直接运行的 cc-connect。
- **核心原则**:适配上游而不是重写上游;界面上的肯定句必须有外部可验证证据。

## 2. 架构与数据流

```text
飞书 / 微信
    |
    v
cc-connect daemon ----------------------> Claude Code / Codex / 其它 agent CLI
    ^                                           |
    | ~/.cc-connect/config.toml                 | 原生 session / transcript
    |                                           v
AiResume.Gui                         ~/.claude / ~/.codex / agent 自有目录
    |
    | WebView2 postMessage(JSON RPC)
    v
ControlPlaneBridge ----- 本地配置 / 真探测 / cc-connect CLI
    |
    | Windows Named Pipe
    v
AiResume.Worker ----- SQLite/WAL ----- ResumeEngine / ProcessSupervisor
    ^
    |
AiResume.Hook ----- Claude Code/Codex/Cline/Qoder/OpenCode 完成边界
```

### 2.1 控制面请求

1. WPF 窗口先显示骨架,不等待项目扫描或健康探测。
2. WebView2 从虚拟 HTTPS 主机加载 `wwwroot/index.html`。
3. 前端通过 `postMessage` 发 `{id,type,payload}`。
4. `ControlPlaneBridge.HandleAsync` 在线程池执行 I/O。
5. 宿主把 `{id,type,result/error,payload}` 回投 WebView2。
6. 前端动效只表达状态变化且全部离线内置；系统减少动态效果时关闭扫光/位移、把入场降级为短促淡入，但刷新、探测和保存仍保留低幅度分段指示，避免工作中状态退化成静态图标。

### 2.2 Worker 常驻链路

- `TransportBootstrap`:启动 Named Pipe 服务端。
- `ObservationWorker`:观察持久 run 与进程存活。
- `NotificationWorker`:消费完成事件 outbox 并投递。
- `ResumeEngine`:观察额度/限流状态并续跑已布防项目。
- `DailyJsonFileLoggerProvider`:写按日滚动、已脱敏的结构化日志。

### 2.3 额度后续跑

```text
用户勾选项目并布防
        |
        v
ResumeEngine 获取 Claude 用量/限流状态
        |
        +-- 未限流 --> 按周期继续观察
        |
        +-- 已限流 --> 持久化 cycleId 并等待
                           |
                           v
                  强刷本次 OAuth 额度证据
                           |
                 +---------+---------+
                 |                   |
          任一窗口满/未知         实时可用
                 |                   |
                 +--> 继续等待       v
                                续跑首项目
                                     |
                             后续每项目启动前
                             再次强刷并过闸
                                     |
                                     v
                             解除布防或连续模式
```

这里的“实时可用”要求布防时显式选择 Fable/Opus/Sonnet/Haiku,并在本次 OAuth 快照里看到与目标模型族匹配的 `weekly_scoped` 窗口；目标 scoped 缺失,或任一返回窗口缺少实时百分比/已满,都无法证明目标模型可用。服务端若长期不下发目标 scoped，自动续跑会保持等待并交给人工处理。

控制面项目行只展示当前有效布防周期的进度。一次性周期全部完成并自动解除布防后，旧周期结果仍保留在 SQLite 供诊断，但不再冒充当前状态；连续模式仍属于当前周期，可以继续展示本轮进度。`arm.get` 在 `config.json.write.lock` 内同时捕获配置与严格 SQLite 状态，不能读到“state 已完成、config 尚未解除”的提交中间态。手动刷新、30 秒轮询、布防和解除布防都会重新读取顶部状态与全部项目行。`success` 显示绿色“已完成”，`limited` 与安全停止类状态显示琥珀色，启动、登记、监控、异常退出及未知状态一律按失败显示红色中文文案。

已登记的 `ActiveRunId` 和终止待确认的 `PendingCancellationRunId` 都是跨周期安全门禁：精确进程仍存活或无法核验时禁止新续跑，只有确认 gone/mismatched 后才清理。生产 `ResumeEngine` 的额度入口固定为 `QuotaService + QuotaResumeProbe`;只有实时 OAuth、目标模型匹配的本次 scoped、全部已知主/scoped 窗口未满且无历史承接时才能 ready,CLI/Haiku 成功、5H reset 或 OAuth 不可验证都不能单独放行。额度证据绑定探测时的 `ResumeModel`,runner 用同一个 `--model`;探测、项目切换或最终 spawn 门禁期间模型变化会废弃旧证据并重探。首项目复用触发恢复的证据,此后每个项目 spawn 前强刷并在异步边界后复核布防周期、模型与最新 `Selected`;新增项目会按最新顺序进入当前轮，完成提交也在配置锁内复核最新队列全部成功，不能漏跑后直接解除。解除布防或移除正在执行的项目会按精确 RunId 请求终止；终止未确认时保留门禁，不启动下一个项目。续跑输出只把结构化 `rate_limit_event.rate_limit_info.status` 识别为限流；若此前出现 Write/Edit/Bash、未知工具活动、损坏或非对象的 NDJSON 行，则落为 `limited-side-effects` 并锁住本周期，禁止额度恢复后自动重放。一次性周期若在 SQLite 已写 `done`、配置尚未解除时崩溃，重启后只补做解除，不再探测或重跑。控制面会把旧周期仍存活的进程明确显示为“续跑仍在进行”，无法核验时显示“续跑状态未确认”，不会在重新布防后伪装成“待初始化”。Worker 对 `product_state` 使用严格读取，SQLite/JSON/默认行读取失败时整拍停止，不能把未知状态降级为空状态；GUI 同样不作肯定判断，而是清空旧项目行并显示红色“布防状态未核实”。裸 `ActiveRunId` 消失但没有终止证据时标记红色“未确认完成”，只有 `PendingCancellationRunId` 的确认退出才显示“已停止”。本进程持有的 Windows Job Object 会通过 `ActiveProcesses` 核验完整进程树，`ResumeEngine`、编排器状态和取消都以同一个 supervisor/Job 为权威，不先依赖 SQLite registry；外层 `cmd` 消失但后代仍在时按正常 `Alive` 继续等待，不得报完成或累计成监控失败，取消用 `TerminateJobObject` 并保持句柄直到整树归零。GUI 不持有 Job 句柄，登记仍在但外层 PID 消失时只能显示“状态未核实”，不得说已结束。任何消费者收到 `Started=true` 且带错误码的启动结果都必须立即取消，退出未确认前保留运行键。Worker 启动时先调用现有 `RecoverAsync` 对账遗留登记，并显式串行启动 hosted services，再开放 IPC、观察与续跑服务；恢复后若 `starting` 的登记仍精确匹配，就直接恢复为 `running` 而不二次 spawn，未知则继续保持 `starting` 和运行键。现有依赖没有同类 Job 封装，因此沿用项目已有的 `kernel32` 薄封装；`Process.Start` 到 `AssignProcessToJobObject` 之间仍有极短的已知残余窗口，Assign 失败会保留精确 PID、立即请求终止并 fail-closed 收敛。这个对账不依赖当前是否仍布防，因此用户解除布防后进程退出，状态也会自动恢复；Worker 不在时，“引擎没在运行”仍优先于阶段文案。

RunContract 的同一 `RunId` 启动驱动与取消共享串行边界：取消先持久化，再等待已进入的启动驱动完成最后一次取消复核；provider 返回、恢复核验后以及进程启动后都会重读取消标记，不能出现“状态已取消、进程随后才启动”。`running` 的未确认取消会在每个观察周期按精确 RunId 重试。provider 失败先写入 SQLite 作为待收尾意图，只有 Job 中完整进程树确认退出后才在同一个 `IMMEDIATE` 事务内按“已提交的用户取消优先，否则失败/成功”选择 terminal，因此 child pending 期间不会释放 runKey，重启也不会把待收尾失败误判为成功。

额度主路径是官方 `GET https://api.anthropic.com/api/oauth/usage`,只读 Claude Code 已有 OAuth access token;请求按 Claude Code 协议携带 OAuth beta、Anthropic version 与本机 `claude-code/<version>` User-Agent,避免普通 UA 的激进 429。解析逐字段优先使用现代 `limits` 的 `session` / `weekly_all` / 全部 `weekly_scoped`,同时兼容旧顶层窗口;percent/reset 都缺失的空对象不算数据。失败时降级到 `ClaudeCodeProbe`,但降级只服务诊断和 GUI 连续显示,不能授权自动续跑。权威快照延迟写入 SQLite `quota_snapshots` 并按 `organizationUuid` 的 SHA-256 指纹隔离;跨窗口更新在单个 SQLite `IMMEDIATE` 事务内重读、合并、写回。OAuth 与 CLI 都视为稀疏观测,缺字段不作删除;同一 reset 代次的已用百分比单调不回退,旧观测也不能把新 reset 代次倒写成旧代次。scoped 以规范化完整 scope 的 SHA-256 作内部身份,同名模型重排不会串窗。只在同账号、同身份、同一未来重置周期内承接并标记 `carriedForward`;账号变化、reset 换代或到期立即清除,承接值显示琥珀“最近读数”而非“已限流”或绿色实时正常。窗口只有 reset、没有百分比时,GUI 保留分段轨道并显示无数值含义的移动未知扫描,不伪造 0%/100%;CLI 只知道全局限流时也不把具体 5H/7D 窗口猜成已满,整体失败但取得部分窗口时也不显示绿色正常,并只负缓存 30 秒。`UsageBucket` 为续跑安全继续保留“任一主/scoped 窗口已满即限流”的聚合语义；GUI 的 `Claude Code` 总行只表示 5H/7D 主窗口,模型 scoped 各自成行,因此仅 Fable 用尽时总行仍正常、Fable 行显示已用尽,而 Fable 自动续跑仍被门禁阻止。Codex home 统一按“显式参数 → `AI_RESUME_CODEX_HOME` → `CODEX_HOME` → `%USERPROFILE%\.codex`”解析,doctor、models、responses、usage 与通知配置都使用同一路径。开窗/定时 shallow 跑 doctor、带凭据的 `{base_url}/models` 与第三方零 token `/v1/usage`;Sub2API 请求成功、账户未显式失效且余额大于 0 时按 CC Switch 语义直接点绿,没有有效余额证据时保持未核实。用户主动刷新时 deep 才额外向 `{base_url}/responses` 发一次 `max_output_tokens=1` 的最小推理请求。HTTP 探针复现 provider 的 `query_params`、`http_headers` 与 `env_http_headers`,环境头覆盖静态头;余额字段依次兼容 `remaining` / `quota.remaining` / `balance` 并默认 USD,余额为 0、账户失效、鉴权失败、402/429 等明确失败优先。官方 OpenAI/ChatGPT 和 ChatGPT OAuth token 不走该第三方接口,因此这里不是 ChatGPT Plus/Pro 订阅额度。完整协议、状态机和冒烟步骤见 `docs/CLAUDE-QUOTA-ACQUISITION.md`。

### 2.4 cc-connect 配置激活

```text
取得 .ai-resume-cutover.lock
        |
        v
读取最新配置与控制面 agent
        |
        +-- agent 未变且选择一致 --> 保留项目 provider/model
        |
        +-- agent 改变或选择残留 --> 清除项目 provider/model
        |
        v
生成候选文件
        |
        v
cc-connect config format --config <候选副本>
        |
        +-- 失败/未知 --> 旧生产文件不动,不重启
        |
        v
核验 daemon.json、management token/端口、当前 API 版本与锁 PID
        |
        v
计划任务 action/脚本/账号/LastRunTime 绑定 + 两次单消费者检查
        |
        v
生产哈希未变后原子替换 config.toml
        |
        v
POST 本地带 token 的 /api/v1/restart
        |
        v
上游 Engine.Stop 关闭平台/agent 会话并在 S4U 上下文自拉新进程
        |
        v
锁 PID 变化、本次操作内的新启动时间、目标 agent
        |
        v
同一稳定 PID 代次日志出现 config loaded / Feishu ready / running
        |
        v
验证根路径唯一 S4U/Limited 任务、PT0S/电池/restart/PT5M 守护设置,
新 LastRunTime 任务实例、任务进程归属或既有 watchdog,再复核同一 PID/agent
```

Windows 上裸 `daemon restart`、`restart --force` 的退出码和 `daemon status` 都不足以证明换代,而交互式 GUI 对 S4U daemon 的进程句柄没有可靠权限。AI Resume 不调用 force,改用上游管理 API 触发 daemon 自重执行;HTTP 连接断开属于结果未知,会先对账新代次。失败时只在生产文件仍是本次提交内容时回滚。日志必须绑定同一锁 PID,marker 时间还不能早于该 PID 写入锁文件的时间;即使某代管理 API 短暂不可达也不能跨 PID 拼接。rearm 后还要再确认管理 API、锁 PID、version 和 agent 未变化。

## 3. 模块职责(路径 → 职责 → 关键函数/入口)

| 路径 | 一句话职责 | 关键函数 / 入口 |
|---|---|---|
| `csharp/src/AiResume.Core/` | 无平台 I/O 的运行契约与领域类型 | `StartRequest`、`RunSnapshot`、`RunKey`、`ProductConfig` |
| `csharp/src/AiResume.Storage/` | SQLite/WAL 持久化 | `StorageDatabase.Migrate`、`RunStore`、`OutboxStore`、`ProductStateStore` |
| `csharp/src/AiResume.Ipc/` | Named Pipe 帧与客户端/服务端 | `NamedPipeTransport`、`PipeFraming`、`PipeProtocol` |
| `csharp/src/AiResume.Secrets/` | 当前用户 DPAPI 与脱敏 | `DpapiSecretStore`、`SecretRedactor` |
| `csharp/src/AiResume.LarkCli/` | lark-cli 结构化进程适配 | `LarkCliInvoker`、`LarkEnvelope`、`LarkRedactor` |
| `csharp/src/AiResume.Wrapper/` | cc-connect 薄适配和安全检查 | `CcConnectConfigGenerator`、`CcConnectConfigValidator`、`CcConnectDaemonController`、`SingleConsumerGuard` |
| `csharp/src/AiResume.Worker/Program.cs` | Worker 命令分派与 Host 装配 | `install`、`preflight`、`cutover-config`、常驻 Host |
| `csharp/src/AiResume.Worker/Resume/` | 限额后续跑核心 | `ResumeEngine`、`ClaudeResumeRunner` |
| `csharp/src/AiResume.Worker/Quota/` | OAuth/CLI 额度、稀疏快照与续跑门禁 | `QuotaService`、`QuotaResumeProbe`、`QuotaSnapshotStore` |
| `csharp/src/AiResume.Worker/Products/` | 产品配置、项目索引和布防周期 | `ProductConfigStore`、`ProjectCatalog`、`ProjectIndex`、`CheckerCycle` |
| `csharp/src/AiResume.Worker/Supervision/` | 进程登记、Job Object、恢复对账 | `ProcessSupervisor`、`ProcessVerifier`、`Reconciler` |
| `csharp/src/AiResume.Worker/Probes/` | Claude/Codex/DeepSeek 真实探测 | `ClaudeCodeProbe`、`CodexAuthProbe`、`CodexBalanceProbe`、`DeepSeekProbe` |
| `csharp/src/AiResume.Worker/Notifications/` | 完成通知注册表和投递 | `NotificationRegistry`、`HookHealth`、`NotificationWorker` |
| `csharp/src/AiResume.Gui/` | WPF + WebView2 控制面 | `MainWindow`、`ControlPlaneBridge.HandleAsync` |
| `csharp/src/AiResume.Gui/wwwroot/index.html` | 单页前端、交互和视觉 | `call`、`render*`、`genCutover` |
| `csharp/src/AiResume.Hook/Program.cs` | agent hook 入口 | 解析来源、内部运行抑制、事件落队列 |
| `csharp/test/AiResume.Tests/` | 隔离的 xUnit 回归 | 1386 项,不触碰真实会话/项目运行 |

## 4. 测试 / 运行流程

- **构建入口**:`csharp/AiResume.sln`。
- **Worker 入口**:`csharp/src/AiResume.Worker/Program.cs`。
- **GUI 入口**:`csharp/src/AiResume.Gui/App.xaml` / `MainWindow.xaml.cs`。
- **环境要求**:Windows 10/11、.NET 10 SDK、WebView2 Runtime;手机聊天另需 cc-connect;具体 agent 需安装对应 CLI。

### 4.1 本地构建和测试

```powershell
dotnet build csharp\AiResume.sln
dotnet test csharp\AiResume.sln
```

当前完整回归:1386 个 xUnit,0 skipped。测试通过临时目录、注入 runner/API、假 PID/时钟和合成 session 隔离生产状态,不发付费 API 请求；通知回归会启动真实 Hook 进程和 Cline wrapper,但不启动 agent。

### 4.2 部署现役副本

```powershell
csharp\src\AiResume.Worker\bin\Release\net10.0-windows\AiResume.Worker.exe install
```

`install` 先在安装目录同级 staging 并校验 Gui/Worker/Hook,写入 payload 清单,备份将覆盖或淘汰的旧运行文件并冻结逐文件 SHA-256 后再替换；提交只复制快照清单中的文件并逐字节复核,上一版清单中已移除的 DLL/脚本会删除并纳入回滚。随后以不继承调用方重定向管道的方式启动 Worker,重建入口并按持久化意图原位刷新通知钩子。新 Worker 必须存活,且 Named Pipe pong 的 PID 等于本次启动 PID；失败会恢复旧运行版本,回滚不完整则保留恢复目录。从安装目录执行卸载时会复制清单拥有的临时 Worker,由它在返回成功前把全部 payload 事务性移入私有退役区；失败原路恢复,无信号/坏信号/提前退出时父进程保留恢复目录,成功后的 temp 清理不再触碰安装目录。状态和未知文件保留,未知文件存在时写入仅允许重装的 preserved-root marker。已打开 GUI 不热更新,部署后要关闭重开；写入时已运行的 Codex 也必须重启后才会加载新的 `notify`。

### 4.3 常用 Worker 命令

```text
install / uninstall       安装或卸载现役副本
preflight                 只读检查是否有第二个飞书消费者
import-feishu             把本机旧凭据导入 DPAPI
feishu-check              真实换 tenant token 验证凭据
cutover-config            仅 CLI 生成 cc-connect 配置
sync-dirs                 同步项目目录到 cc-connect /dir 历史
notify                    管理完成通知源
```

控制面里的 **生成并重启 cc-connect** 比 CLI `cutover-config` 多了候选验证、原子提交、守护重启和新代次验证。

### 4.4 重点测试路由

- 改 cc-connect 配置:`CcConnectConfigValidatorTests`、`CcConnectConfigPreserveTests`、`CcConnectProjectIdentityTests`。
- 改 agent/provider/model:`CcConnectAgentCoherenceTests`、`CcConnectProjectExtraKeysTests`、`CcConnectProviderCatalogTests`。
- 改重启:`CcConnectDaemonControllerTests`、`ControlPlaneBridgeCutoverTests`。
- 改进程恢复:`PowerLossRecoveryTests`、`ProcessVerifierTests`、`ReconcilerTests`。
- 改续跑:`ResumeEngineTests`、`CheckerCycleTests`、`ClaudeResumeRunnerTests`。
- 改通知:各 `*NotificationAdapterTests`、`HookHealthTests`、`NotifyIntentTests`、`NotificationHookProcessTests`、`NotificationWorkerTests`;协议和冒烟见 `docs/COMPLETION-NOTIFICATIONS.md`。
- 改 GUI bridge:`ControlPlaneBridgeArmTests`、`ControlPlaneBridgeProjectsTests`。
- 改 Codex 可用性/第三方余额:`CodexAuthProbeTests`、`CodexProbeTests`、`CodexBalanceProbeTests`、`ControlPlaneBridgeProviderTests`。

## 5. 数据格式与命名约定

### 5.1 AI Resume 状态目录

```text
%LOCALAPPDATA%\AI Resume\state\
  config.json
  runs.db
  logs\YYYY-MM-DD.jsonl
  webview2\
  <DPAPI 加密凭据文件>
```

- `config.json`:产品意图,包括项目、自定义/隐藏列表、布防、agent 选择、通知源。
- `runs.db`:SQLite schema v6;含 `runs`、`run_events`、`outbox`、`process_registry`、`product_state`、`quota_snapshots`。v5 会丢弃无法证明账号归属的旧 v4 三列额度行,再按账号指纹重新建立基线；v6 为 `product_state` 预置唯一默认行，使运行期整行丢失可以被严格读取识别为故障。
- `logs\YYYY-MM-DD.jsonl`:按本地日期滚动的结构化日志;每行一个 JSON 对象。
- `AIRESUME_SHADOW_DIR`:测试/并行运行用状态根覆盖。设置后禁止迁移真实旧目录。
- `AI_RESUME_ENABLE_WEBVIEW_DEVTOOLS`:仅显式设为 `1` / `true` 时开启 WebView2 DevTools;安装态默认关闭。

### 5.2 cc-connect 状态

```text
%USERPROFILE%\.cc-connect\
  config.toml
  .config.toml.lock
  daemon.json
  logs\cc-connect.log
  sessions\ai-resume_<hash>.json
```

真实文件名样例 `sessions\ai-resume_a1b2c3d4.json` 拆解为:

- `ai-resume` = 固定 cc-connect 项目身份键,不能随工作目录变化。
- `_` = 项目名与派生哈希的分隔符。
- `a1b2c3d4` = cc-connect 用于区分项目/session 索引的稳定派生片段,不是 agent 原生 session ID。
- `.json` = cc-connect 会话索引;真正 transcript 在 Claude/Codex 自己的目录。

### 5.3 cc-connect provider 关键字段

```toml
[[providers]]
name = "chatpt-monthly"
base_url = "https://router.example/v1"
model = "gpt-5.6"
agent_types = ["codex"]

[[providers.agent_model_lists.codex]]
model = "gpt-5.6"
```

- `agent_types`:缺失/空数组时上游视为所有 agent 可用;provider 名、值、agent 值和映射键保留 TOML 原字符串并严格区分大小写。
- `base_url`:provider 默认端点;`[providers.endpoints]` 可按 agent 覆盖。
- `model`:默认模型,不等同于菜单列表。
- `[[providers.models]]`:全局模型菜单候选;所有 agent 共用时才适合。
- `[providers.agent_models]` / `[[providers.agent_model_lists.<agent>]]`:按 agent 默认模型和候选覆盖。AI Resume 兼容表数组与内联数组,只在当前 agent 没有用户列表时补候选。官方 OpenAI 端点的当前 Codex 家族补 `sol/terra/luna` 并保留有效默认值;第三方 relay 只补其有效默认模型,除非配置已有自身验证的显式列表。生成 alias 以 `[AI Resume] ` 标记所有权,上游 CRUD 经 TOML 解码/重编码剥注释后仍可刷新;无标记和用户列表不迁移。唯一兼容 provider 自动写入活动选择,多候选不猜;Codex 本地 `model_catalog_json` 优先级更高。项目内联 `[[projects.agent.providers]]` 不受全局 `agent_types` 过滤,切 agent 时遇到它会失败关闭。合法尾注释、引号式 TOML 表头和引号式 owned 赋值键同样纳入保留边界。

### 5.4 RunContract 命名

- `requestId`:调用方幂等 UUID;同请求重试不重复 spawn。
- `runId`:Worker 生成的运行身份。
- `runKey`:并发所有权键;同键存在非终态或 `childPending` 时拒绝新运行。
- `taskKind`:`chat|query|modify|resume|probe`。
- `childPending`:子进程未确认 close;即使逻辑终态也不能释放运行键。
- `stateVersion` / `seq`:持久状态和事件的单调版本。

## 6. FAQ(同事高频问题,直接给答案)

- **Q:为什么点“删除会话”没反应?** — A:cc-connect 保护当前活动会话;活动行不是可删除选择。先 `/new` 或切到别的会话,再选择旧会话删除。Codex 会话删除还必须走上游 thread API,不能只删本地引用。
- **Q:为什么选了 Codex,新会话还是 Claude 模型?** — A:先确认点击了“生成并重启 cc-connect”且结果验证到 `agent=codex`。v1.4.1 新 Engine 会失效旧 agent 的原生 session ID 并更新会话类型,不要求 `/new`;但会话保存的兼容 provider 可能恢复,可用 `/provider switch` 改。仍显示 Claude 时先判断 daemon 是否根本没有换代。
- **Q:为什么 provider 里一直有 DeepSeek?** — A:上游把没写 `agent_types` 的 provider 当成通用。DeepSeek 的 `/anthropic` 端点可由 Claude Code 使用,但不能直接当 Codex/OpenAI 端点。生成器现在会把它从 Codex `provider_refs` 排除,除非配置了 Codex 专用 endpoint override。
- **Q:Claude Code 真能用 DeepSeek 吗?** — A:能,前提是端点说 Anthropic 兼容协议。cc-connect 会给 Claude Code 注入 `ANTHROPIC_BASE_URL`、token 和 model;agent 是本地执行器,provider 是远端后端,两者不是同一概念。
- **Q:为什么 Codex `/model` 还显示 o4-mini/o3/gpt-4.1?** — A:cc-connect 未激活 provider 时会走 v1.4.1 的旧硬编码回退表。重新“生成并重启 cc-connect”;唯一兼容 provider 会自动激活。官方 OpenAI 端点显示默认值与 Sol/Terra/Luna;第三方中转只显示其显式配置或自身 `/models` 已验证的候选。多 provider 必须先 `/provider switch`,本地 `model_catalog_json` 仍有最高优先级。
- **Q:Fable 额度为什么以前会偶尔消失?** — A:旧 OAuth 请求缺少 Claude Code User-Agent/OAuth beta 头时可能收到 429,而 CLI 降级又常不含 `weekly_scoped`。现役请求已对齐 Claude Code,并把现代 `limits` 中全部 `weekly_scoped` 分别映射成模型行;后续稀疏响应只在同账号、同 reset 周期内承接最近读数。同周期百分比只增不减,账号变化、窗口换代或到期才清除。
- **Q:为什么 Claude Code 显示正常,Fable 却显示已用尽?** — A:两行回答不同问题。Claude Code 总行只表示账户 5H/7D 主窗口,Fable 行表示该模型自己的 `weekly_scoped`；主窗口有余量而 Fable scoped 已满时,总行正常、Fable 行已用尽是准确状态。自动续跑会同时检查两层额度,因此不会在 Fable 满额时误启动 Fable。
- **Q:为什么 5H 已重置,布防仍不续跑?** — A:5H 只是重新取证的时机。布防选择 Fable 后,若 7D 或 `weekly_scoped:Fable` 仍满/未返回,目标任务照样不能运行;现役 Worker 会强刷 OAuth 快照,要求目标 scoped 明确匹配且所有返回窗口实时可用,只有 CLI/历史证据或取证失败都继续等待。多项目队列会在每个后续项目启动前再次验额,模型变化还会废弃旧证据。若账号长期不下发目标 scoped,需要人工处理,系统不会用 5H/7D 正常去猜测 Fable 可用。
- **Q:为什么不用普通 `cc-connect daemon restart` 或 `restart --force`?** — A:v1.4.1 Windows 实测会退出 0 但旧 PID 不退;force 又只按后来重读的锁 PID 硬杀父进程,GUI 对 S4U daemon 也没有可靠进程权限。控制面改用带 token 的 daemon 自重启,再独立验证锁 PID、新启动时间、agent、本代日志和任务 `LastRunTime`/归属/watchdog。
- **Q:生成配置成功是否代表机器人已经用新 agent?** — A:不是。完整成功必须同时满足候选被上游解析、生产文件原子提交、旧 PID 退出、新 PID 存活、目标 agent 日志、Feishu ready 和 running。否则只报告部分完成。
- **Q:为什么重启会被拒绝?** — A:daemon 元数据、management 配置/API/版本、锁 PID、计划任务 action/脚本/账号或单消费者检查任一无法确认都会 fail-closed。旧 `feishu-agent.js` 与 `feishu-launch.vbs` 都算冲突。候选在提交前不会改生产文件;提交后若换代未验证,仅在文件未被外部改动时回滚。
- **Q:为什么显示“配置已提交,但新代次未验证”?** — A:文件提交和运行代次是两个独立事实。此时配置已落盘,但任务、锁、稳定 PID 或同代日志仍有一项缺证据;界面会同时显示阶段与两个布尔状态,不能把它说成整体成功。
- **Q:项目在手机上怎么切?** — A:用 `/dir <路径>`、`/dir <序号>`、`/dir -` 或 `/dir reset`;cc-connect 项目身份固定为 `ai-resume`,工作目录才是实际项目。
- **Q:绿灯依据是什么?** — A:真实最小请求成功,不是“填了 key”或“命令存在”。核对不了时显示未验证,不会回退成可用。
- **Q:Sub2API 有正余额为什么现在会点绿?** — A:因为当前产品明确采用 CC Switch 的判定:用量请求成功、账户未显式失效且余额大于 0,就是该 provider/account 的绿色可用证据。它不是对每一次未来推理请求的绝对保证;若随后拿到 401/403、余额为 0、账户失效、402/429 等更强失败证据,失败状态优先。
- **Q:为什么 AI Resume 的 Codex 通知开着却没有消息?** — A:先看 `notify list`、队列和 Worker 日志。若完整合成链路能送达,再比较 ChatGPT/Codex 主进程启动时间与 `~/.codex/config.toml` 写入时间；主进程更早时说明它尚未加载新 `notify`,重启 Codex Desktop 后再验证下一次真实任务。
- **Q:测试会不会碰真实会话或项目?** — A:不应。测试使用临时状态根、假 PID/runner 和合成 session;禁止 resume 真实会话或对真实仓库启动修改运行。

## 7. 术语表(中英 / 缩写对照)

| 术语 | 含义 |
|---|---|
| AI Resume | 本项目;Windows 控制面 + 限额后续跑引擎 |
| cc-connect | 上游多平台消息、会话与 agent turn 编排 daemon |
| agent | 本地执行 CLI,如 Claude Code / Codex |
| provider | agent 调用的远端 API 端点、凭据和协议映射 |
| model | 发给 provider 的模型标识 |
| provider refs | 项目引用的全局 provider 名单 |
| DPAPI | Windows Data Protection API;按当前用户加密本机凭据 |
| WAL | SQLite Write-Ahead Logging;支持崩溃恢复与并发读写 |
| RunContract | AI Resume 自己启动的进程统一 Start/Status/Cancel 契约 |
| runKey | 一个活动运行对资源/项目的并发所有权键 |
| childPending | 子进程尚未确认退出,运行键仍必须保留 |
| outbox | 先持久化再异步投递的消息队列 |
| generation | 一次明确的进程启动代次;不能复用历史 ready 日志 |
| fail-closed | 核对不清时拒绝继续,不把未知当安全/成功 |
| single consumer | 同一生产飞书应用同时只能有一个 cc-connect 长连接消费者 |
| candidate config | 尚未替换生产文件、先交给上游解析器验证的候选 TOML |
| atomic replace | 临时完整文件写好后一次替换,磁盘只出现旧完整或新完整状态 |
| completion boundary | 代表整个 agent 任务结束的 hook/event,不是单次模型请求结束 |

## 8. 文档索引(深挖时读)

- [README.zh-CN.md](README.zh-CN.md):中文安装、界面和手机聊天使用说明。
- [README.md](README.md):英文用户说明与产品定位。
- [CLAUDE.md](CLAUDE.md):仓库协作规则、部署方式、测试红线和生产安全不变量。
- [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md):现役 C# v2 组件、数据流、cc-connect 激活、provider/model/session 机制。
- [docs/MIGRATION-PROGRESS.md](docs/MIGRATION-PROGRESS.md):v2 迁移完成状态、终态边界和当前验证门禁。
- [docs/NEXT-STEPS.md](docs/NEXT-STEPS.md):真实限额重置、长稳 soak、真客户端通知与发布工作的后续路线。
- [docs/CLAUDE-QUOTA-ACQUISITION.md](docs/CLAUDE-QUOTA-ACQUISITION.md):Claude OAuth/CLI 额度协议、Fable、稀疏合并、SQLite 并发与真机验证手册。
- [docs/COMPLETION-NOTIFICATIONS.md](docs/COMPLETION-NOTIFICATIONS.md):五类客户端完成边界、持久化投递、去重与真机验收。
- [docs/adr/0003-cc-connect-direct-and-control-plane.md](docs/adr/0003-cc-connect-direct-and-control-plane.md):为何 cc-connect 直接运行、AI Resume 只保留四项职责。
- [docs/adr/0002-run-lifecycle-contract.md](docs/adr/0002-run-lifecycle-contract.md):Start/Status/Cancel 与无客户端总时限的决策。
- [docs/RUN-CONTRACT.md](docs/RUN-CONTRACT.md):运行字段、状态机、错误分类与恢复规则。
- [docs/LESSONS.md](docs/LESSONS.md):静默失败、测试污染、错误判据等仍有效工程教训。
- [docs/UPSTREAM-ARCHITECTURE-RESEARCH.md](docs/UPSTREAM-ARCHITECTURE-RESEARCH.md):固定上游快照与平台能力盘点。
- [docs/STATE-OWNERSHIP.md](docs/STATE-OWNERSHIP.md):2026-08-01 迁移启动时的历史 current/shadow/target 所有权基线。
- [docs/EVENT-CONTRACTS.md](docs/EVENT-CONTRACTS.md):2026-08-01 的迁移期目标事件契约;现役运行生命周期以 ADR-0002、`RUN-CONTRACT.md` 和代码为准。
- [docs/MIGRATION-DEBT.md](docs/MIGRATION-DEBT.md):历史迁移债务编号、证据与当时关闭条件;当前排期看 `docs/NEXT-STEPS.md`。
- [docs/AUDIT-ROUND2-20260808.md](docs/AUDIT-ROUND2-20260808.md):第二轮“界面肯定句必须可证伪”审计背景。
