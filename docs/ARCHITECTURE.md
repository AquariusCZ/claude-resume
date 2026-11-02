# AI Resume v2 架构

本文描述 `csharp/` 下的现役 C# 实现,是现役机制与完整配置的真身。v1 的 PowerShell + Node 运行时已于 2026-08-08 删除,只能经 Git 历史查阅。

最近一次结构修订:2026-10-30(v2.2.0,[ADR-0006](adr/0006-retire-cc-connect-per-project-resume.md))。cc-connect 整体退役,飞书只发完成通知,手机远程改用 Claude Code 官方 Remote Control;续跑改为逐项目(每个项目自己的模型,逐项目判定义务、额度与占用,未启动不锁周期);Claude 凭据只读 Claude Code 自己的登录,账号指纹改取全局配置里的 `oauthAccount`;scoped 额度按模型族判定;完成通知投递修复(直接启动 `lark-cli.exe`、机器范围凭据副本经环境变量交给 lark-cli、分级退避/熔断/死信、`notify check|test`);Claude Code 钩子改为官方 exec form 并新增 `StopFailure`;OpenCode 2.x 改用官方 v2 插件;DeepSeek 只读 OpenCode 的密钥且只展示余额;GUI 重排(菜单栏、逐项目模型队列)。上一次结构修订是 2026-10-12 的 v2.1([ADR-0005](adr/0005-owned-credentials-official-codex-and-hooks.md))。

版本号唯一真身是 `csharp/Directory.Build.props` 的 `<Version>`(当前 `2.2.0`),Gui / Worker / Hook 都从它继承,面板铭牌显示的就是它。

## 产品边界

AI Resume 只负责四件事:

1. 原生会话被用量上限打断后,按供应商证据**逐项目**排队,额度恢复后用原会话续跑;
2. 发现并索引本地项目;
3. 接收本地 AI 客户端的完成钩子/插件事件,经官方 `lark-cli` 以飞书机器人身份私信投递;
4. 提供 Windows 控制面(WPF + WebView2)。

它不实现聊天通道、agent 回合、cron 或供应商切换状态机,也不镜像 Claude Code / OpenCode 内部的会话状态机,只读它们公开落盘的产物(会话登记、转录、日志)。手机上的远程交互交给 Claude Code 官方 Remote Control:连接由 Claude Code 自己建立,AI Resume 不参与,只在钩子事件表明会话连着 Remote Control 时在通知文案里加「· 远程已连接」。飞书只剩「完成通知」一种用途,所有飞书调用都经官方 `lark-cli`。

决策关系:ADR-0002 继续约束 AI Resume 自己启动的进程(续跑、探测)。ADR-0004 的固定目标续跑(供应商/账号/agent + 原生会话 UUID、不换供应商自动重放、Codex 执行计划)继续有效,其「全队列共用一个模型」与 scoped 规则由 ADR-0006 修订。ADR-0005 的 B1(Codex 官方账号)与 C1(Codex hooks)继续有效;A1(setup-token 长期令牌)已被 ADR-0006 取代;D1 只保留「收件人 open_id 明文存配置」,其「app_secret 只在 GUI 侧 / Worker 不碰 DPAPI」的投递设计已被机器范围凭据副本取代。

**历史(v2.0–v2.1,已于 2026-10-30 随 ADR-0006 退役)**:cc-connect 曾作为上游 daemon 承担飞书/微信聊天、会话编排与 cron,AI Resume 负责生成并激活它的 `config.toml`、做单消费者预检并经管理 API 重启它(ADR-0003)。现在 AI Resume 不再生成、激活、重启或检查 cc-connect,也没有飞书授权名单(`allow_from` / `admin_from`)。本机遗留的 cc-connect 计划任务、npm 包与 `%USERPROFILE%\.cc-connect\`(其中有多份明文密钥)由用户自行清理并吊销,AI Resume 既不读取也不删除。用手机驱动 OpenCode / Codex 的能力随之放弃,是已知缺口。

## 进程拓扑

```text
Claude Code / OpenCode 2.x / Codex / Cline / Qoder
        |  官方 hooks / 插件 → AiResume.Hook.exe <源> [--kind=decision|failure]
        v
state\completion-events\<eventId>.json
        ^   另有写入方:ResumeNoticeWriter(续跑提醒)、ClaudeLoginReminder(登录到期)、
        |   notify test / notify.test(冒烟事件)
        v
AiResume.Worker(常驻;登录自启;单实例)
  +- NotificationWorker → CompletionNotifier → LarkCliNotificationChannel
  |      → lark-cli.exe im +messages-send --as bot → 飞书私信
  +- ResumeEngine + CheckerCycle
  |      +- ProviderResumeProbe:供应商额度证据 + 会话尾部证据 + 会话占用
  |      +- ClaudeResumeRunner → ProcessSupervisor(Job Object)
  |             → AiResume.Worker.exe internal-agent-run
  |             → claude --resume <UUID> … / codex exec resume <UUID> …
  +- ClaudeLoginReminder
  +- ObservationWorker、TransportBootstrap(Named Pipe)
  +- state\runs.db(SQLite/WAL)、state\config.json、state\completion-notify-*.json
        ^
        |  读写同一个状态目录;Named Pipe 只用来 ping(判定「后台服务运行中」)
AiResume.Gui(WPF + WebView2)── ControlPlaneBridge
        +- 只读外部证据:Claude Code 登录与会话、Codex auth.json、OpenCode 配置与日志、
           供应商元数据接口、lark-cli doctor / auth list

手机远程:Claude Code 官方 Remote Control(由 Claude Code 自己连接,不经 AI Resume)
```

- GUI 与 Worker 之间没有业务 RPC,二者读写同一个状态目录:`config.json` 一律在 `config.json.write.lock` 内重读、只改自己负责的字段、原子替换;周期状态在 `runs.db` 的 `product_state` 行;投递健康由 Worker 单写 `completion-notify-health.json`。GUI 判断后台 Worker 在不在分两步:引擎判定(`engine`)按进程名枚举 `AiResume.Worker`(`EngineLiveness.TryDetectEngineProcess`,枚举失败即「未核实」);「后台服务运行中」(`workerRunning`)还要本用户常驻 Worker 的 Named Pipe 以 300 毫秒超时应答 `ping`(`EngineLiveness.ResidentWorker`)——同名的 CLI 子命令进程(`notify test`、`install`)不开管道,别的 Windows 用户的 Worker 管道名不同,都只算「未核实」。
- Worker 的 Named Pipe 服务端(命令 `ping` / `start` / `status` / `cancel` / `list-runs`)现役客户端有两个,都只用 `ping`:`install` 的就绪核验(新 Worker 的 pong 必须来自本次启动的精确 PID)与 GUI 的 `workerRunning` 判定。服务端持有 `Global\` 命名空间的单实例互斥体(取不到时退回会话内名字),防止会话 0 与交互会话里各跑一个 ResumeEngine 共用一个数据库。
- 历史:v2.1 及之前「本机只能有一个 cc-connect 飞书长连接消费者」的约束已随 cc-connect 退役;AI Resume 自身从不消费飞书事件,只发消息。

主要数据流:

1. **完成通知**:客户端钩子/插件 → `AiResume.Hook.exe` 规范化并原子写入 `completion-events\` → `NotificationWorker` 每 15 秒扫一轮 → `lark-cli` 以机器人身份私信 → 结果写 `completion-notify-*.json`。
2. **续跑**:GUI `arm.set` 在锁内写 `config.json`(新 `armCycleId`)→ `ResumeEngine` 每 30 秒一拍读配置与 `product_state` → `ProviderResumeProbe` 取额度、会话尾部与占用证据 → `CheckerCycle` 逐项目判定并写 `product_state` → `ClaudeResumeRunner` 经受监督的子进程续原会话 → 结果写回 `product_state`,续跑提醒写入 `completion-events\`。
3. **额度与供应商显示**:GUI `quota.get` / `providers.catalog` 直接请求供应商元数据接口(零 token);Claude OAuth 快照经 `runs.db` 的 `quota_snapshots` 在 GUI 与 Worker 之间按账号合并。
4. **登录到期**:`ClaudeLoginReminder` 每 30 分钟只读一次 Claude Code 登录文件 → 到阈值时写入 `completion-events\`。

## 组件

| 项目 | 职责 |
|---|---|
| `AiResume.Core` | RunContract 记录、枚举、标识与接口;产品契约 `ProductConfig` / `ProjectRef`、`ResumeTarget`、`CheckerState` / `ProjectNote`、`ClaudeProbeResult`、`ProjectAdmission`、`ClaudeModelFamilies`。不做任何平台 I/O。 |
| `AiResume.Storage` | SQLite/WAL schema(当前版本 6:`runs`、`run_events`、`outbox`、`process_registry`、`product_state`、`quota_snapshots`),运行/事件/进程登记、产品周期状态与额度快照的持久化。`outbox` 表与 `OutboxStore` 目前没有生产调用方,完成通知走 `completion-events\` 文件队列。 |
| `AiResume.Ipc` | Worker 的 Named Pipe 服务端与客户端:帧格式为 4 字节小端长度前缀 + UTF-8 JSON,信封版本 `1`。 |
| `AiResume.Secrets` | DPAPI 存储与脱敏。默认 CurrentUser 范围;LocalMachine 范围只给必须在 S4U 会话 0 里解密的材料——现役只有飞书机器人凭据 `secrets\feishu-bot.bin`:密文先写进创建时就带受限 DACL(断开继承,只留当前用户 / SYSTEM / Administrators)的临时文件,读回核验后才原子替换正式文件;收不紧就丢掉临时文件、放弃保存,上一份凭据原样保留。 |
| `AiResume.LarkCli` | 结构化 `lark-cli` 进程封装:`ArgumentList` 直传参数、stdout/stderr 用 UTF-8、超时终止整个进程树、解析 stdout 与 stderr 两种信封、exit 10 高风险确认原样透传、输出脱敏(子进程环境变量里名字含 `SECRET` 的值也进入脱敏集合)。凭据只经调用方显式给出的子进程环境变量传入,绝不进 argv。 |
| `AiResume.Worker` | 常驻宿主:续跑引擎、额度与供应商目录、完成通知投递、登录到期提醒、进程监督;命令 `install` / `uninstall` / `shortcuts` / `notify` 与内部 `internal-agent-run`。 |
| `AiResume.Gui` | WPF 外壳 + WebView2 界面;`ControlPlaneBridge` 在线程池处理全部宿主请求。 |
| `AiResume.Hook` | 各客户端钩子/插件调用的小程序:做事件准入、规范化并原子写入 `completion-events\`;不读凭据、不联网,任何异常都返回 0,不阻断宿主客户端。 |
| `AiResume.Launcher` | 登录自启用的 WinExe 垫片,以 `CreateNoWindow` 拉起 Worker 后退出(见「登录自启」)。 |

历史:`AiResume.Wrapper` 项目(cc-connect 配置生成/校验、daemon 控制、provider 目录、单消费者检查、进程枚举)已于 2026-10-30 随 ADR-0006 删除。

## 持久化位置

安装产物位于:

```text
%LOCALAPPDATA%\AI Resume\
```

AI Resume 自己的持久状态放在单独的子目录,卸载时可以保留:

```text
%LOCALAPPDATA%\AI Resume\state\
  config.json                       产品意图(ProductConfig):项目、布防、续跑目标与逐项目模型、通知源意图(含是否记录过)、收件人 open_id
  config.json.write.lock            config.json 的跨进程独占锁(锁内重读 → 修改 → 原子替换)
  runs.db                           SQLite/WAL:runs、run_events、process_registry、product_state(CheckerState JSON)、quota_snapshots、outbox
  project-index.json                项目发现索引(GUI 写)
  completion-events\<eventId>.json  完成通知队列:Hook、续跑提醒与测试通知写入,Worker 是唯一消费者
  completion-events\malformed\      无法解析或缺必需字段的事件
  completion-events\dead\           永久失败、重试超过 24 小时、发生超过 24 小时未送出或结果不明超过 55 分钟的事件,旁附 <eventId>.error.txt(只含类别/退出码/错误码/log_id)
  completion-notify-seen.json       已送达的 eventId → 送达时间(7 天去重窗)
  completion-notify-state.json      逐条退避状态(次数、首次/下次尝试时间、首次结果不明时间、最近错误类别/错误码/log_id)
  completion-notify-sessions.json   每个 Claude Code 会话最近一次已送达的完成时间(2 天),让更早的 backgroundBusy 完成作废
  completion-notify-limits.json     已提醒过的「限额周期#会话」(周期为 rate_limit|<配置根指纹>|<窗口>|<重置时间> 或 session|<会话>)→ 失效时间;同一会话同周期之后的用量上限中断据此丢弃
  completion-notify-health.json     投递通道健康(Worker 单写;GUI 与 notify check/test 只读)
  secrets\feishu-bot.bin            飞书机器人 app_id / app_secret(DPAPI LocalMachine,ACL 收紧;GUI 写,Worker 读)
  secrets\feishu-platform.bin       仅 v2.1 遗留:「飞书接入」保存的 CurrentUser 密文,只供「导入旧版凭据」读取一次,原样保留
  logs\worker-yyyyMMdd.log          Worker 结构化单行 JSON 日志(按日滚动、经 SecretRedactor 脱敏)
  logs\gui-yyyyMMdd.log             GUI 日志
  logs\launcher.log                 Launcher 启动失败的痕迹
  webview2\                         WebView2 用户数据目录
```

v2.1 的 `secrets\claude-oauth-token.bin` 与 `claude-oauth-token.bin.rejected.json`(setup-token 长期令牌及其拒绝标记)已无读者,`install` 激活成功后由 `InstallCommand.RetireLegacyClaudeToken` 从实际状态根(`ShadowPaths.Root`)删除,自定义 `--target` 时安装目录旁的 `state\` 也清一遍;删不掉只打印完整路径提示手动删除,不影响安装结果。删除本地文件不等于吊销:setup-token 签发的令牌在服务端约一年有效,需要彻底作废时在 Claude 账号的授权管理里撤销。

测试与公开截图用 `AIRESUME_SHADOW_DIR` 覆盖状态根(`ShadowPaths.Root`)。v1 的 `ClaudeResumeShadow` 搬迁已于 v2.1 删除,`EnsureRoot` 只建目录。注意 `AiResume.Launcher` 的 `launcher.log` 固定写在 `%LOCALAPPDATA%\AI Resume\state\logs\`,不跟随该覆盖。

安装器在 staging 里写入精确的所有权标记与 payload 清单。自定义目标必须是空目录、只含保留下来的 `state`、每个文件都与当前 payload 一致、带有现役标记,或带有卸载因保留未知文件而写下的精确 preserved-root 标记。卷根、用户/系统根与重解析路径一律拒绝。升级时删除上一份清单里新 payload 已没有的文件,并先备份以便回滚。卸载要求现役标记加有效清单,才会改动快捷方式、进程、钩子或文件。从安装目录执行时,它先暂存一份受清单约束的临时运行时;helper 在报告成功前处理各集成入口,并把全部自有文件连同标记/清单事务性移入私有退役目录,任何失败都原路回滚,回滚不完整时保留 helper 目录作为恢复材料。结果信号缺失、无效或 helper 提前退出一律失败关闭:父进程只报告恢复材料的绝对路径,绝不删除 helper 目录。父进程退出后的清理只碰那个私有目录,所以立即重装不会与旧删除竞态。`state` 与未知文件会被保留;preserved-root 标记只授权重装,不能授权再次卸载。

AI Resume 读取(少数场合写入)的外部文件,所有权都在各自的客户端:

| 位置 | AI Resume 读什么 | AI Resume 写什么 |
|---|---|---|
| `%USERPROFILE%\.claude\.credentials.json`(或 `CLAUDE_CONFIG_DIR` 下同名文件) | `claudeAiOauth` 的访问令牌、`expiresAt`、`refreshTokenExpiresAt`、`subscriptionType` | 从不写;绝不刷新令牌 |
| Claude Code 全局配置:`<配置根>\.config.json`(非空时),否则 `%USERPROFILE%\.claude.json`(或 `CLAUDE_CONFIG_DIR\.claude.json`);另交叉核对 `<配置根>\.claude.json` | `oauthAccount` 的组织/账号 UUID(账号指纹)、`env`、`cachedGrowthBookFeatures.tengu_windows_credman` | 从不写 |
| `<配置根>\projects\**\<UUID>.jsonl` | 会话头部的 `cwd` 与首条时间戳(项目发现、会话绑定、「换会话接着干」判定;按行读，单行不设 64 KiB 上限,头部最多 60 行、32 MiB)与尾部元数据(续跑义务、最近模型) | 从不写;不读对话正文 |
| `<配置根>\sessions\<pid>.json` | 运行中会话登记(占用判定) | 从不写 |
| `<配置根>\settings.json` | `env`(`claude-api` 供应商行) | 从不写 |
| `<配置根>\settings.json`(与 Claude Code 一致跟随 `CLAUDE_CONFIG_DIR`,未设置时 `%USERPROFILE%\.claude`) | 我方 hooks 条目是否完整 | 只增删 AI Resume 自己的 hooks 条目 |
| `%USERPROFILE%\.codex\`(或 `AI_RESUME_CODEX_HOME` / `CODEX_HOME`) | `auth.json`、`config.toml`、`sessions\` | `hooks.json` 我方分组与 `config.toml` 的 `hooks.state` 信任记录(ADR-0005 C1) |
| OpenCode 2.x:配置目录 `OPENCODE_CONFIG_DIR`(未设置时 `%XDG_CONFIG_HOME%\opencode`,再未设置时 `%USERPROFILE%\.config\opencode`)下的 `opencode.json` / `opencode.jsonc`,`OPENCODE_CONFIG` 指定的文件,`OPENCODE_CONFIG_CONTENT` 内联配置,`%ProgramData%\opencode` 托管层(1.x 的 `config.json` 不读);数据目录 `%XDG_DATA_HOME%\opencode`(未设置时 `%USERPROFILE%\.local\share\opencode`)下的凭据库(`OPENCODE_DB`,默认 `opencode.db`,只读 `credential` 表)、`auth.json` 与 `log\opencode.log` | DeepSeek 密钥(只展示余额)、插件加载证据 | 配置目录下 `plugins\airesume-notify\` 我方插件 |
| Cline / Qoder 的 hooks 配置(见「完成通知」) | 我方条目是否完整 | 只增删 AI Resume 自己的条目 |
| lark-cli 自己的配置与钥匙串 | 不直接读;只经 `lark-cli` 子命令使用 | 从不写 |

`<配置根>` 指 `CLAUDE_CONFIG_DIR`,未设置时为 `%USERPROFILE%\.claude`。项目发现、会话绑定与续跑取证都在 `<配置根>\projects` 下找会话。

### 产品配置 `config.json`(`ProductConfig`)

字段名为 camelCase,反序列化大小写不敏感。

| 字段 | 含义 |
|---|---|
| `enabled` / `armed` / `armCycleId` | 布防开关与周期 id。布防时写入新的 `armCycleId`(无连字符 GUID),周期 id 变化即让上一周期的状态失效;解除布防清空 `armCycleId`。 |
| `continuous` | 连续模式:每次被打断都续跑;一次性模式下全部项目完成后自动解除布防。 |
| `selected[]` | 布防队列(`ProjectRef`),按顺序串行续跑。 |
| `customProjects[]` / `hiddenProjects[]` / `projectHome` | 手动添加的项目 / 不再扫描的目录 / 项目根。 |
| `probeIntervalMinutes` | 续跑引擎的探测节奏,默认 15,小于 2 时按 15;有项目待续跑时固定 4 分钟。 |
| `quotaRefreshMinutes` | GUI 额度自动刷新间隔,默认 15;不影响续跑引擎。 |
| `resumeTarget` | 布防固定的执行目标 `ResumeTarget`:`providerId`、`providerIdentity`(账号身份指纹)、`providerName`、`agent`(只能是 `claudecode` 或 `codex`)、`modelId` / `modelName`(默认模型)、`boundAtUtc`。只存身份指纹与模型 ID,绝不含凭据。 |
| `resumeModel` | 必须等于 `resumeTarget.modelId`,否则视为未绑定(`target-unbound`),等待重新布防。 |
| `resumePrompt` | 续跑提示词,默认 `continue`。 |
| `skipPermissions` | 是否给 Claude 追加 `--dangerously-skip-permissions`;默认 false(fail-closed)。 |
| `notifySources[]` | 用户想开着的完成通知源(意图,不是现状)。 |
| `notifyIntentRecorded` | `notifySources` 是否被记录过(界面开关、`notify enable/disable`、`install` / `uninstall` 写意图时置 true)。false 且列表为空 = 更早的旧配置，投递端不按来源过滤;true 时空列表 = 用户把通知源全关了，残留链路的事件一律丢弃(`ai-resume` 事件总是放行)。 |
| `notifyRecipientOpenId` | 飞书收件人 `open_id` 明文,唯一真身。 |
| `probeModel` / `dirtyGuard` | 兼容保留,现役代码不读取。 |

`ProjectRef` 字段:`name`、`path`、`sessionId`(布防时固定的原生会话 UUID),以及 v2.2 新增的逐项目模型:

- `modelId` / `modelName`:本项目续跑用的模型 ID,布防时冻结;多个项目可以指向同一个模型,共用同一份额度证据。
- `modelSource`:`session` = 未手选,跟随该会话最近一次真实应答的模型(在本次实时目录里);`user` = 手选(必须在本次实时目录里,否则整次布防被拒);`default` = 未手选,且会话模型读不到或不在目录里,退回布防目标的默认模型(会话模型不在目录时附提示,不阻止布防;Codex 项目没有会话模型证据,未手选时总是 `default`);空 = v2.2 之前的旧配置,`modelId` 也为空,沿用 `resumeTarget.modelId`(GUI 把空来源显示为 `default`)。
- 有效模型由 `ResumeTarget.EffectiveModel(project, target)` 给出:项目级优先,为空时退回 `resumeTarget.modelId`。`ResumeTarget.Identity(config)` 把供应商/账号/agent/默认模型、`resumeModel`、`resumePrompt`、`skipPermissions` 与每个项目的(路径, 会话 UUID, 有效模型)一起哈希,只在进程内比较:任何一项在异步边界前后变化,在途的额度证据立即作废。

旧 `config.json` 里的 `ccConnectAgent` 字段(v2.1 及之前)被忽略。

### 周期状态 `product_state`(`CheckerState`)

`runs.db` 的单例 `product_state` 行保存 `CheckerState` 的 JSON。schema v6 预置这一行;Worker 用严格读取(`LoadStrict`),行缺失、SQLite 或 JSON 错误、项目状态/说明/中断记录出现空键或空值时整拍失败关闭。

| 字段 | 含义 |
|---|---|
| `phase` | `idle` / `waiting` / `resuming` / `done` / `blocked`。 |
| `cycleId` | 对应 `armCycleId`;不一致时 `CheckerCycle.Initialize` 重置全部周期字段(含逐项目状态、说明与中断记录)。 |
| `sawLimited` | 逐项目模式下等于「是否有项目待续跑」,决定 4 分钟探测节奏。 |
| `lastProbeUtc` / `lastProbeReason` | 最近一次取证时间与稳定原因码(如 `ok`、`watching`、`blocked`、首个等待项目的原因、`probe-error`;只保留安全字符,最长 64)。 |
| `limitedRefires` | 「额度说可用、一跑就被限流」的全周期连续次数,只作诊断与兼容(升级前的状态以它作为第一次按项目计数的起点)。 |
| `projectLimitedRefires{路径 → 次数}` | v2.2 新增。按项目的「额度说可用、一跑就被限流」连续次数,这个项目成功一次即清零;同一项目达到 6 次记为 `error` 并锁存本周期。新一轮限流、周期初始化与完成时清空。 |
| `replayBlocked` | 本周期出现了不可自动重放的结果;必须解除并重新布防。 |
| `realFiveHourResetUtc` / `realSevenDayResetUtc` / `realResetProbedUtc` / `realFiveHourUtil` | 只取服务端精确值,只覆盖不清零。 |
| `projectStatus{路径 → 状态}` | `limited`(待续跑)、`running`、`success`、`continued-elsewhere`、`cancel-pending`、`stopped`、`error`、`exit-*`、`limited-side-effects` 等;`success` 与 `continued-elsewhere` 是完成态;除 `success` / `limited` / `continued-elsewhere` 外的结果都会锁存本周期(`running` 只在精确 `activeRunId` 守着时存在,宿主重启后无法确认完成即转为 `exit-null`)。 |
| `projectNotes{路径 → ProjectNote}` | v2.2 新增。每个项目「为什么在等、在等什么」,只用于界面与诊断,不参与阻断:`reason`、`detail`(只含窗口名与状态码)、`resetAtUnix`(预计解除阻挡的服务端重置时间)、`interruptedAtUnix`、`client`(占用会话的客户端入口)、`atUnix`。 |
| `consumedInterruptions{路径 → 中断 uuid}` | v2.2 新增。AI Resume 已经发起过续跑的限额中断;在 spawn 前与 `running` 同一次落盘,保证同一次中断最多续一次——即使续跑没改掉会话尾部,或 Worker 在运行中崩溃。 |
| `activeRunId` / `activeProjectPath` | 当前续跑的精确 RunId 与项目,在 spawn 前写入。 |
| `pendingCancellationRunId` / `…ProjectPath` / `…CycleId` | 已请求终止但尚未确认退出的 RunId,跨布防周期保留。 |

`ProjectNote.reason` 的稳定原因码:`not-interrupted`、`watching`、`ready`、`running`、`done`、`continued-elsewhere`、`unevaluated`、`quota:<额度结论>`、`session-in-use`、`cwd-busy`、`session-unverified`、`evidence-unverified`、`not-started:<原因>`、`blocked:<状态>`(如 `blocked:repeated-limit`)。界面据此出短句与悬停说明。

## GUI 与 Worker

GUI 先渲染外壳,再做项目发现与健康探测。WebView2 经虚拟 HTTPS 主机加载 `wwwroot/index.html`(页面 URL 带按文件修改时间生成的缓存参数),向 `ControlPlaneBridge` 发类型化请求并收 JSON 应答。DevTools 默认关闭,只有显式设置 `AI_RESUME_ENABLE_WEBVIEW_DEVTOOLS=1` 或 `true` 才开启。浏览器加速键与缩放都被关闭(`AreBrowserAcceleratorKeysEnabled=false`、`IsZoomControlEnabled=false`):F5 交给页面做「刷新额度与状态」,Ctrl± / Ctrl+滚轮会改变 DPR,让点阵字失去整数倍而发糊。宿主异常进入应答信封或初始化界面前先脱敏;完整异常只保留在本地脱敏后的 GUI 日志里。已打开的窗口不会热更新,部署后必须关掉旧窗口再打开。

控制面只承载自己的页面:被导航进 WebView2 的任何页面都能经 `postMessage` 调用全部 RPC(布防、改收件人、保存机器人凭据),所以外部拖放被关闭(`AllowExternalDrop=false`)、`NavigationStarting` 取消一切不在虚拟主机 `https://controlplane.airesume.local/` 上的导航、`NewWindowRequested` 一律吞掉,`WebMessageReceived` 再按 `e.Source` 核一次来源(`MainWindow.IsControlPlaneUri`),不是控制面页面发来的请求直接丢弃。

v2.2 的界面只回答四个问题,其余收进菜单栏:

- **菜单栏**:视图(显示 Codex〔额度栏与续跑供应商〕/ 显示其他通知源〔Codex / Cline / Qoder〕/ 供应商与额度详情… / 不再扫描的目录…)、工具(刷新额度与状态 F5 / 重新扫描项目 / 连续模式 / 飞书通知设置… / 发送测试通知)、帮助(使用说明 / 关于)。快捷键 Alt+V / Alt+T / Alt+H 打开菜单,F10 聚焦菜单栏。偏好(`showCodex`、`showExtraNotify`)只存在本机 WebView2 的 `localStorage`(键 `airesume.prefs`)。连续模式不是界面偏好而是布防配置(`config.json` 的 `continuous`):菜单里的勾选只是内存里的待定选择,没动过时跟随 `arm.get` 返回的当前配置,下一次布防随 `arm.set` 生效。
- **铭牌 + 状态灯**(一灯两行):它现在在不在干活。「监视中」要布防、引擎进程在、探测新鲜,且本用户常驻 Worker 应答 Named Pipe(有同名进程却不应答为琥珀「监视状态未核实」);Claude 登录的红/琥珀只套在 Claude 订阅周期上(`resumeTarget.providerId === 'claude-subscription'`),Codex 与走 API 凭据的 Claude Code 周期不受订阅登录影响;认不出模型族(`quota:unknown` + `model-invalid`)与账号变化一样提示「需要重新布防」。
- **额度表**:四块含义固定的 CRT——Claude 5 小时、Claude 7 天、Claude 按模型(已用最高或已满的一条 scoped 窗口,卡在该窗口上的布防项目显示角标「N 项在等」)、DeepSeek 余额;打开「显示 Codex」时追加 Codex 第五块。窄磁贴用容器查询换短标题。
- **续跑队列**:每行是勾选 + 序号 + 项目名/路径 + 「续跑模型 / 状态」列。未布防时勾选的行出现模型下拉(默认「沿用会话:最近模型」,可逐项目手选);已布防时显示固定模型(↺ 表示跟随会话)与本周期等待原因短句,悬停看完整说明与预计续跑时间。供应商下拉只在未布防且有两个以上可续跑供应商时出现在队列标题栏。布防栏给出摘要、「预览…」(只读,不执行)与布防/解除布防。
- **右栏**两块:「Claude 登录」(有效 / 即将到期 / 令牌待续期 / 已过期 / 未登录 / 凭据位置变化 / 未核实,来自 `claude.login.status`)与「完成通知 → 飞书」(Claude Code、OpenCode 两行开关 + 飞书机器人行 + 收件人/积压/未送达)。
- **页脚**:左侧操作反馈,右侧「后台服务运行中 / 未运行 / 未核实」(`arm.get` 的 `workerRunning`)。
- **对话框**(原生 `<dialog>`):飞书通知设置(机器人凭据 / 收件人 / 诊断通道)、续跑预览、供应商与额度详情、不再扫描的目录、关于、使用说明。

收进菜单的东西若处于启用、故障或未核实状态,主界面必须留一句提示,隐藏不得掩盖启用态、断链或读不出配置。页面每 30 秒重读 `arm.get` 与 `notify.channel`,每 `quotaRefreshMinutes` 分钟整体刷新一次,F5 立即刷新。一轮刷新里每块数据到了就各自绘制(额度磁贴不等最慢的供应商目录):`providers.catalog` 在 `quota.get` 返回之后才发,宿主让 Claude 行复用这一轮 1 分钟内的额度读数(`QuotaService.GetRecentAsync`),一轮只请求一次 usage 端点;`claude.login.status` 在额度请求结束后再读一次(只采用最后发出的那次),`serverRejected` 反映的是这一轮的结果。

视图模型是 `index.html` 里 `<script id="vm">` 的纯函数 `AR.*`,由 `GuiPageContractTests` 放进 Node 隔离上下文直接驱动,断言「无证据不绿」「承接读数琥珀」「按模型满额不染红主窗口」「刷新失败不留旧绿」「隐藏的通知源仍启用必须提示」「Worker 不在时机器人行不得显示可送达」「每项目模型」等语义,不逐字断言 DOM。

字号一律走 rem(1rem = 点阵字设计尺寸 12px),首次绘制前的开机脚本按 `devicePixelRatio` 把根字号挪到「乘上缩放率正好是 12 的整数倍」的值(150% → 16px),跨显示器改变 DPR 时重算;`font-synthesis:none` 与 `-webkit-font-smoothing:none` 是点阵字清晰的前提。布局尺寸仍用 px。动效全部是页面内联的原生 CSS 过渡/动画,不引任何外部脚本;每条过渡显式写出缓动(共用强 `ease-out` / `ease-in-out` 词汇),移动只用 transform/opacity。已知百分比的额度条是带名称的 ARIA `meter`;百分比未报告时用不带 `aria-valuenow` 的扫描动画,绝不合成一个百分比。`prefers-reduced-motion:reduce` 下去掉位移与扫描,忙碌转圈改为淡入淡出,只保留必要的进行中反馈。

Worker 是 Generic Host。托管服务强制串行启动(`ServicesStartConcurrently=false`),顺序为:

- `ProcessRecoveryBootstrap`:先对上次宿主留下的 `process_registry` 跑 `RecoverAsync`,再放行其余服务;
- `TransportBootstrap`:Named Pipe 服务端;`start` / `status` / `cancel` 走 RunContract 骨架 `TaskOrchestrator`,其 `IProviderAdapter` 现役注册的是 `FakeProviderAdapter`(可编程的测试骨架,供断电恢复等测试使用),续跑不经过它;
- `ObservationWorker`:持久化运行/进程的观测;
- `NotificationWorker`:每 15 秒扫一轮完成通知队列;
- `ClaudeLoginReminder`:每 30 分钟检查一次 Claude Code 登录到期;
- `ResumeEngine`:每 30 秒一拍,驱动布防周期与逐项目续跑。

无法识别的子命令在构造 Host 之前就被拦下(否则 `--help` 之类的笔误会悄悄起第二个常驻 Worker):帮助请求打印用法返回 0,不带前导横线的未知首参打印用法返回 2;带横线的首参仍作为宿主配置(如 `--Observation:IntervalSeconds=00:00:15`)。

### 控制面 RPC

前端经 WebView2 `postMessage` 发 `{id, type, …}`,`ControlPlaneBridge.HandleAsync` 在线程池处理,回 `{id, type: "<type>.result" | "<type>.error", payload, error}`;错误文本经 `SecretRedactor` 脱敏,未知类型返回 error。现役清单以 `HandleAsync` 的 `switch` 为准:

| 请求 | 参数 | 作用 / 返回 |
|---|---|---|
| `app.info` | — | 版本(程序集版本前三段)、状态目录、`quotaRefreshMinutes`。 |
| `projects.list` | — | 发现的项目(`name` / `path` / `lastUsed` / `isGit` / `isCustom`)、`elapsedMs`、`hidden`(仍然存在的不再扫描目录)。 |
| `projects.add` | `path` | 写入 `customProjects` 并从 `hiddenProjects` 移除;目录必须存在,拒绝系统或程序自身目录。 |
| `projects.remove` | `path` | 移出 `customProjects` 与 `selected`、加入 `hiddenProjects`;已布防且队列被移空时连带解除布防。 |
| `projects.restore` | `path`(可选) | 放回单个或全部不再扫描的目录。 |
| `dialog.pickFolder` | — | 在 UI 线程弹原生选目录对话框。 |
| `quota.get` | `force`(可选) | Claude OAuth 额度快照:`windows[]`(`name` / `label` / `status` / `usedPercent` / `windowSeconds` / `resetAtUnix` / `resetAfterSeconds` / `windowStartUnix` / `carriedForward`)、`hasData`、`allowed`、`limitReached`、`globalHasCurrentData` / `globalHasCarried` / `globalLimitReached`、`unattributedLimitReached`、`unavailableReason`、`storageWarning`。 |
| `providers.catalog` | — | 零 token 的供应商目录与额度/余额证据(见「供应商与准入注册」)。 |
| `claude.login.status` | — | `verdict`、`serverRejected`(usage 端点最近一次以 401 / 结构化 403 拒绝了本机令牌,且之后登录文件没再被改写:本地时间戳再新也不能显示「有效」)、`accessTokenExpiresAtUnix`、`loginExpiresAtUnix`、`subscriptionType`、`accountVerified`、`problem`;不回任何令牌、邮箱或账号 UUID。 |
| `arm.get` | — | `armed`、`continuous`、`resumeModel`、`resumeTarget`、`cycleId`、`phase`、`sawLimited`、`selected[]`、`projectStatus[]`、`engine` / `engineText`、`probeAgeSeconds`、`probeReason`、`projects[]`、`workerRunning`(true = 有同名进程**且**本用户常驻 Worker 的 Named Pipe 在 300 毫秒内应答 pong;false = 枚举到没有同名进程,不发 ping;null = 进程枚举失败,或有同名进程但管道不应答——`notify test` / `install` 这类 CLI 子命令、别的 Windows 用户的 Worker——即核验不出,序列化时原样写出 null)。引擎判定 `engine` 先只看进程;布防中判为 `Alive` 而 `workerRunning` 为 null 时改判 `WorkerUnverified`(界面琥珀「监视状态未核实」,既不说监视中也不报红)。 |
| `arm.set` | `armed`;布防时另带 `providerId`(缺省 `claude-subscription`)、`resumeModel`、`paths[]`、`projectModels{路径: 模型 ID 或空串}`、`continuous`、`expectCycleId`(调用方看到的当前周期,未布防为空串) | 布防见「布防:逐项目绑定会话与模型」;带 `expectCycleId` 时它必须仍等于现在的周期(未布防视为空串),否则拒绝——两个控制面窗口时,不能静默替换掉另一个窗口刚建立的周期及其已消费的中断与逐项目进度。解除布防只清 `armed` / `armCycleId`。应答同 `arm.get`,布防时可能附 `warnings[]`(提示但不阻止布防)。界面最多等 120 秒;宿主明确回错时直接显示原因(不对账:读到另一个窗口刚建立的同队列周期会被误认成这边成功);只有超时才立即、1 秒、5 秒各重读一次 `arm.get` 对账,观察到本次请求建立的周期(周期号变了且队列正是这次提交的项目)才按成功报告,否则只说“结果未确认”。成功应用 `arm.set` 的应答后,操作期间发出的旧 `arm.get` 读数作废。 |
| `arm.preview` | `providerId`、`paths[]` | 只读本地会话事实:`sessionId`、`sessionModel`、`evidence`、`limitType`、`limitResetsAtUnix`、`interruptedAtUnix`、`session`、`client`、`problem`;不调用绑定、不发网络请求、不写状态。 |
| `notifications.list` | — | 5 个通知源:`kind` / `displayName` / `isInstalled` / `isEnabled` / `configPath` / `detail` / `hookBroken` / `unverified`(探测本身失败:配置正被改写或被占用,开没开不知道,界面显示「未核实」而不是「已关闭」「未检测到」,开关画成既非开也非关的虚框,点按只重新读取)。 |
| `notifications.setEnabled` | `kind`、`enabled` | 启用前必须能定位 `AiResume.Hook.exe`;停用不需要 Hook 文件。`NotifyIntent.Apply`:先把 `notifySources` 意图写进配置、再改客户端配置——意图也是投递端的来源过滤,写不进去就失败、开关不动;改客户端失败时再探一次实际状态(`NotificationRegistry.ProbeEnabled`),只有证明根本没生效才把这一项意图恢复原样,已部分生效或探不出时保留这次的选择,然后报错。 |
| `notify.channel` | — | `larkCliFound`、`credentialSource`(`env` = 机器范围副本 / `keychain` = lark-cli 钥匙串)、`appIdMasked`、`legacyCredentialAvailable`、`recipientSet`、`recipientMasked`、`settingsVerified`(null = 还没有成功记录或比不出来;界面只有它为 true 才显示「可送达」)、`health{state, reason, lastAttemptOk, lastSuccessAtUnix, lastAttemptAtUnix, blockedUntilUnix, lastErrorClass, lastErrorCode, lastLogId, backlog, dead, credentialSource, updatedAtUnix}`。 |
| `notify.check` | — | `lark-cli doctor` 结果:`ok`、`botReady`、`credentialSource`、`problem`、`checks[]`(`name` / `status` / `message`)。 |
| `notify.credential.set` | `appId`、`appSecret` | 先用这组凭据跑 `lark-cli doctor`,机器人身份就绪才写 `feishu-bot.bin`。 |
| `notify.credential.import` | — | 读 v2.1 遗留的 `feishu-platform.bin`,同样先经 doctor 核验再写机器范围副本。 |
| `notify.credential.clear` | — | 删除 `feishu-bot.bin`。 |
| `notify.recipient.set` | `openId` | 去掉全部空白后必须是 `ou_` 开头的字母数字。 |
| `notify.recipient.detect` | — | `lark-cli auth list --json` 列出的已登录用户 `items[]`(`openId` / `userName` / `tokenStatus`)与 `filteredByApp`(保存过机器人凭据副本时只留同一应用的用户)。 |
| `notify.test` | — | 冒烟事件入队,返回 `eventId`、`enqueuedAtUnix` 与最新 `channel`。界面先查 lark-cli、收件人与后台 Worker,缺一样就不入队。 |
| `notify.test.status` | `eventId`、`sinceUnix` | 这一条测试事件的结局 `outcome`:`delivered`(进了已送达表)/ `dead` / `failed`(这一条真实尝试过、得到确定的条目级失败,`failure` 给出类别、错误码与 log_id;结果不明的不算,Worker 会在飞书去重窗口内重试)/ `blocked`(入队之后 Worker 看过、通道此刻仍在熔断:到期了或改过设置都不算)/ `pending`,外加最新 `channel`。与 `notify test` 同一判据(`NotifyControlPlane.TestOutcome`);界面每 5 秒问一次,最多 90 秒。 |
| `notify.test.withdraw` | `eventId` | 撤回还没送出的测试事件(失败、熔断或 90 秒没送达时),免得通道恢复后补发一条过时的测试通知;返回 `withdrawn`。Worker 发送前复查事件文件并在发送期间占住它,撤回与发送互斥:撤不回(正在发这一条)时界面再看一眼结局,如实说「已送达」或「撤回未确认,稍后仍可能收到」。事件 ID 只接受 16 位十六进制。 |

`--screenshot` 截图模式下,页面渲染所需的读请求改由进程内合成数据(`DemoPayload`)回答,其它请求一律报错,请求处理不读真实配置与状态(WebView2 用户数据与 GUI 日志仍落在状态根下,见「构建、安装与验证」)。历史:`feishu.*`、`cutover.*`、`agent.*`、`claude.token.*`、`quota.local` 已于 2026-10-30 删除,`providers.probe` 已于 v2.1 删除。

### Worker 命令

| 命令 | 作用 | 退出码 |
|---|---|---|
| (不带子命令) | 以常驻服务启动;可带 `--Section:Key=Value` 形式的宿主配置。 | — |
| `install [--target <目录>] [--from <构建根>]` | 安装或升级(见「构建、安装与验证」)。 | 0 成功;1 失败;2 通知源未能对齐;3 新 Worker 未就绪(已回滚);4 回滚不完整;快捷方式失败时返回其退出码。 |
| `uninstall [--target <目录>]` | 卸载,保留 `state\` 与未知文件。 | 0 成功;异常 1;移除快捷方式失败时返回其退出码;从安装目录执行而临时卸载 Worker 60 秒内未返回时 4。失败时 stderr 给出恢复材料的绝对路径。 |
| `shortcuts [install\|uninstall] [--gui …] [--worker …] [--icon …]` | 创建或移除开始菜单、桌面与登录自启入口(`install` / `uninstall` 内部调用)。 | 0 成功,失败非 0。 |
| `notify` / `notify list` | 列出 5 个通知源及「已安装 / 已启用 / 可送达」。 | 任一源已启用但 hook 失效(可送达列 `False(hook 失效)`,OpenCode 为 `False(插件失效)`)时 2,否则 0。 |
| `notify enable <源>` / `notify disable <源>` | 源名大小写不敏感:`ClaudeCode` / `Codex` / `Cline` / `Qoder` / `OpenCode`;先写意图、再改客户端配置(`NotifyIntent.Apply`,改客户端失败时按实际状态记意图,证明没生效才恢复原样)。 | 成功 0;找不到源、找不到 Hook、意图或客户端配置写入失败 1。 |
| `notify check` | 诊断飞书通道:lark-cli 位置、凭据来源、收件人、最近一次真实投递结论,再跑 `lark-cli doctor`(在线、只读)。 | lark-cli 在、收件人已设、机器人身份就绪、通道未熔断且最近一次真实投递尝试不是失败时 0,否则 2(doctor 只证明机器人身份,证明不了收件人可达)。 |
| `notify test` | 放一条测试通知进队列,等后台服务(Worker)真实投递,最多 90 秒;只认这一条事件自己的结局(`NotifyControlPlane.TestOutcome`)。 | 送达 0(包括撤回时 Worker 恰好正在发送、随后确认送达);进了死信、这一条确定失败、通道此刻熔断或超时 2(失败、熔断与超时时撤回这条事件,撤不回如实提示可能仍会送达)。 |
| `internal-agent-run <规格>` | 内部:续跑子进程的受监督包装(见「执行计划」)。 | 环境没有 `AI_RESUME_INTERNAL_RUN=1` 或规格无效时 2;执行异常 3;否则为子进程退出码。 |

`notify` 未知子命令返回 1。历史:`feishu-check`、`cc-connect-apply`、`preflight` 已于 2026-10-30 删除;v1 迁移命令 `migrate` / `import-feishu` / `sync-dirs` / `cutover-config` 已于 2026-10-12 删除。

## 运行生命周期

AI Resume 自己的工作在持久状态上使用 `Start`、`Status`、`Cancel`:

```text
queued -> starting -> running -> succeeded
                              -> failed_provider
                              -> failed_local
                              -> cancelled
```

AI 工作没有客户端总时限。结构化 HTTP 408/504 或 `gateway_timeout` 是供应商超时;DNS/TCP/TLS/reset、进程消失、解析失败与监控不确定都是本地失败。静默只是遥测指标,不是失败条件。

一旦可能已经产生副作用,禁止自动回退或重放。取消是终态。运行键在自有进程被确认关闭前一直占用;未知 PID 绝不当成已消失。

## 额度与续跑流程

### Claude 凭据与额度主路径

额度主路径是 `GET https://api.anthropic.com/api/oauth/usage`。`ClaudeCredentialSource` 是唯一的凭据解析入口,只读 Claude Code 自己的登录文件 `%USERPROFILE%\.claude\.credentials.json`(设置了 `CLAUDE_CONFIG_DIR` 时读其下同名文件),以共享读写/删除方式打开、**只读、绝不刷新、绝不写回**:Claude Code 的 refresh token 是一次性轮换的,外部进程刷新一次,Claude Code 手里那份就作废。访问令牌约 8 小时一换,由正在运行的 Claude Code(会话或 Remote Control 桥)自己续期;登录本身(`refreshTokenExpiresAt`,约 30 天)固定不变,只能由用户运行 `claude` 并 `/login` 续期。访问令牌剩余不足 60 秒视同过期,不发请求。

请求复现 Claude Code 的协议形状:JSON accept、`anthropic-beta: oauth-2025-04-20`、`anthropic-version: 2023-06-01` 与 `User-Agent: claude-code/<本机版本>`;普通客户端 UA 会被分进更激进的 429 桶。401 一定是凭据问题(`token_rejected_401`);403 只有带 Anthropic 结构化错误体时才算登录无权读额度(`token_rejected_403`),裸 403 归为网络/网关拦截(`http_403_gateway`),不会要求用户重新登录。

**账号指纹**只由账号材料派生:取 Claude Code 全局配置里 `oauthAccount` 的 `organizationUuid` 与 `accountUuid`,做 SHA-256。全局配置按 Claude Code 自己的定位规则选主文件(`<配置根>\.config.json` 非空时用它,否则 `%USERPROFILE%\.claude.json` 或 `CLAUDE_CONFIG_DIR\.claude.json`),再交叉核对 `<配置根>\.claude.json`;多份配置的账号不一致视为未核实,正被改写读不出时沿用本进程上一次成功的值。核实不了账号时指纹为空,续跑等待,**绝不退回令牌指纹**——v2.1 用令牌哈希当指纹,令牌每 8 小时一换,同一账号被反复判成「供应商已改变」,周期永远等待。

登录状态的结论 `ClaudeLoginVerdict`(`ClaudeCredentialSource.Describe`,不含任何实值):

| 结论 | 判据 |
|---|---|
| `Ok` | 已登录且访问令牌有效;账号未核实时附说明,界面显示「有效 · 账号未核实」。 |
| `LoginExpiring` | 仍可用,但登录将在 3 天内到期(与 Claude Code 自己提示 `/login` 的阈值一致)。 |
| `TokenExpired` | 登录有效、访问令牌已过期:打开一次 Claude Code 即自动续期,续期前读不到额度。 |
| `LoginExpired` | 登录已到期,或刷新被服务端拒绝后 Claude Code 已把令牌置空、`expiresAt=0`。 |
| `LoggedOut` | 没有登录:登录文件不存在、没有访问令牌,或缺到期时间。 |
| `StoreMoved` | `CLAUDE_CODE_FORCE_WINDOWS_CREDMAN=1` 或全局配置 `cachedGrowthBookFeatures.tengu_windows_credman=true`:Claude Code 已把登录挪进 Windows 凭据管理器,AI Resume 读不到。 |
| `Unverified` | 登录文件在,但这次读不了(被占用)或解析不了(多半正被 Claude Code 改写):判断不了,绝不说成「未登录」;凭据失败原因码为 `unreadable`,界面显示「未核实」且不把状态灯染红。 |

`ClaudeLoginReminder` 每 30 分钟检查一次:登录到期前 3 天、2 天、1 天、12 小时内与自然到期各写一次飞书提醒(`login-expiring` / `login-expired`);登录被 Claude Code 清空(到期时间随之消失)时每个本地日写一条 `login-expired`,直到登录恢复。没设置收件人时不写,免得设置后一次补发一串过期提醒。

历史:v2.1 的第二条凭据来源——AI Resume 自持、经 GUI「Claude · 凭据」粘贴、以 DPAPI LocalMachine 保存的 `claude setup-token` 长期令牌——实测作用域只有 `user:inference`,usage 端点一律拒绝,已于 2026-10-30 连同 `claude.token.*` RPC 与拒绝标记机制一并删除(ADR-0006 取代 ADR-0005 A1)。

额度协议、稀疏观测状态机、界面语义与真机验证清单的细节在 [`CLAUDE-QUOTA-ACQUISITION.md`](CLAUDE-QUOTA-ACQUISITION.md)。

### 供应商与准入注册

生产经 `AddQuotaResumeAdmission` 统一注册 `QuotaService`、`QuotaResumeProbe`、`ProviderRegistry`、`ResumeSessionCatalog`(同时作为 `IProjectSessionInspector`)、`ProviderCatalogService`,并把 `IClaudeUsageProbe` 绑定到 `ProviderResumeProbe`。会话证据与占用由同一个 `ResumeSessionCatalog` 实例提供,取证、执行计划与最终门禁看到的是同一套事实。

`ProviderRegistry.Read()` 每次都重新读取各客户端的凭据,产出这些供应商行(凭据只在内存里流动,界面只拿到 `ProviderObservation`):

| id | 名称 | agent / kind | 凭据来源 | 能否续跑 |
|---|---|---|---|---|
| `claude-subscription` | Claude · Anthropic 订阅 | `claudecode` / `claude-oauth` | Claude Code 登录文件;身份 = SHA-256(`claude-subscription:` + 账号指纹),账号未核实时为空 | 可以(身份为空时不能布防) |
| `claude-api` | Claude · DeepSeek,或「Claude · 端点主机名」 | `claudecode` / `deepseek` 或 `anthropic-api` | Claude Code 自己的全局配置与 `settings.json` 的 `env`(其次是进程环境变量)里的 `ANTHROPIC_BASE_URL`、`ANTHROPIC_AUTH_TOKEN` / `ANTHROPIC_API_KEY`;只在配置了端点或密钥时出现 | 可以 |
| `codex-official` | Codex · ChatGPT 官方 / Codex · OpenAI API 官方 / Codex · 官方(未登录) | `codex` / `chatgpt` 或 `openai-api` | 只读 `~/.codex/auth.json`(ChatGPT 登录或 OpenAI API key),不跟随 `config.toml` 的 `model_provider` | 可以 |
| `codex-active` | 「Codex · provider ID」 | `codex` / `openai-api` | `config.toml` 当前 `model_provider` 指向的非官方、非回环、不借用 OpenAI 登录的静态第三方 provider | 可以 |
| `deepseek` | DeepSeek · OpenCode(回退环境变量时为 DeepSeek · 环境变量) | `opencode` / `deepseek` | 合并后配置里的 `provider.deepseek.options.apiKey`(支持 `{env:…}` / `{file:…}`)优先;其次 OpenCode 2.x 的凭据库(`OPENCODE_DB`,默认 `opencode.db`)的 `credential` 表(只读、不建连接池、2 秒忙等,先取启用的再取最近更新的)——2.x 只在首次启动时把 `auth.json` 导入一次,之后换密钥、删密钥都只改库。所以库和表在时以库为准:库里没有 DeepSeek 就是 OpenCode 没存(不再拿 `auth.json` 的旧密钥),库读不了或记录认不出显示未核实;只有库或 `credential` 表根本不存在(1.x、尚未迁移)才看 `auth.json`;都没有才回退 `DEEPSEEK_API_KEY`。端点按 OpenCode 2.x 的配置层合并后判断:全局 `opencode.json` → `opencode.jsonc`(目录跟随 `OPENCODE_CONFIG_DIR`,否则 XDG)→ `OPENCODE_CONFIG` → `OPENCODE_CONFIG_CONTENT` → `%ProgramData%\opencode` 托管层(1.x 的 `config.json` 2.x 不读,这里也不读);合并后(后层覆盖前层)的 baseURL 指向非官方端点就不发余额请求;项目级配置与组织远程配置读不到 | 不能:只展示余额 |

`resumable` 的判据是 agent 为 `claudecode` 或 `codex` 且身份非空。DeepSeek 行只回答「OpenCode 那个账户还剩多少钱」:拿 OpenCode 的密钥去跑 Claude Code 等于把一个客户端的凭据挪给另一个客户端,所以它不是续跑目标;AI Resume 不保存任何 DeepSeek key。

GUI 用 `providers.catalog` 并发读取全部行,只发元数据请求,不做推理:

| 行 | 模型目录 | 额度 / 余额证据 |
|---|---|---|
| `claude-subscription` | `GET https://api.anthropic.com/v1/models?limit=1000`(OAuth bearer、`anthropic-beta`、`anthropic-version`、Claude Code UA;`after_id` 分页,最多 20 页) | `QuotaService` 的 OAuth usage:续跑与布防强制刷新;控制面的 `providers.catalog` 复用 1 分钟内的读数(先等在途的那次刷新完成,`GetRecentAsync`),一轮刷新只请求一次 usage 端点。快照的账号指纹必须与连接身份配对,否则显示「Claude 订阅额度未核实」 |
| `claude-api` | DeepSeek 端点读 `https://api.deepseek.com/models`,其它端点读 `<端点>/v1/models`(同样分页) | DeepSeek 端点读 `https://api.deepseek.com/user/balance`;其它端点读兼容 `/v1/usage` |
| `codex-official`(ChatGPT 登录) | `https://chatgpt.com/backend-api/codex/models?client_version=<本机 Codex 版本>`,只收 `visibility=list` 的 slug | `https://chatgpt.com/backend-api/wham/usage` 的账号窗口与 `model_usage` |
| `codex-official`(OpenAI API key)/ `codex-active` | `<base_url>/models`(`CodexAuthProbe.ProbeModelsAsync`) | 第三方读兼容 `/v1/usage`,按 CC Switch 字段优先级 `remaining` → `quota.remaining` → `balance`,默认 USD;官方 OpenAI/ChatGPT 端点没有这条第三方余额路由,额度保持未核实 |
| `deepseek` | `https://api.deepseek.com/models` | `https://api.deepseek.com/user/balance`(CNY) |

模型可列不等于额度可用;任一数据源失败只降级它自己,不保留旧绿,也不拖垮其它行。观测完成后若该行的供应商/账号身份已经变化,整行作废为「查询期间供应商或账号已改变」。

注册表读别人的文件一律共享读写与删除打开(`SharedFile`):`File.ReadAllText` 以 `FileShare.Read` 打开,句柄存续期间对方写不进、换不掉自己的凭据或配置。每一行各自包 try/catch,读不出只降级本行。`ProviderRegistry.Find(id)` 对两个可续跑的官方行(`claude-subscription`、`codex-official`)只构造被问到的那一行:续跑在途时每 500ms 经 `ProviderExecutionPlan.IsCurrent` 复核一次,不能顺带把 OpenCode、Codex 第三方配置与 Claude settings 全读一遍。GUI 的 `providers.catalog` 与 `quota.get` 和其它请求一样放到线程池执行:它们的同步部分要解析 `~/.claude.json`(可达数 MB)等多份配置,不能卡住窗口消息泵。布防时目录没读出来、而凭据本身有问题(需 `/login`、令牌待续期)时,报出真实原因,而不是「请刷新后重试」;Claude 订阅目标的模型认不出模型族(fable / opus / sonnet / haiku 之外)时拒绝布防(跟随会话的退回默认模型并提示),免得按模型额度永远核实不了而静默等待。

续跑准入的额度结论由 `ProviderCatalogService.CheckModelsAsync(target, models)` 给出:一次供应商观测,按模型逐个评估,同账号的多个项目共用这一份实时证据。

- 目标无效、连接不存在、agent 改变,或账号身份确实变了 → `provider-changed`(需重新布防)。
- 身份为空(Claude Code 全局配置里没有一致的 `oauthAccount`,与访问令牌在不在无关)→ `identity-unverified`,`Detail` 为凭据原因码(多为 `account_unverified`;注册表读登录抛意外异常时为 `unreadable`)。身份在、但 Claude 订阅凭据暂不可用 → `credentials-unavailable`,`Detail` 为原因码:没有访问令牌(`no_credentials` 未登录、`login_expired` 含刷新被拒后被清空、`credential_store_moved` 凭据挪进 Windows 凭据管理器)、`token_expired`、登录到期、缺到期时间,或登录文件这次读不出(`unreadable`)。二者都只等待。
- 模型目录这次没读出来 → `model-unverified`,`Detail = catalog-unverified`(界面「模型目录未核实」);目录读出来了、模型确实不在 → `model-unverified`,`Detail = absent`(界面「模型不在当前目录」)。
- Claude 订阅:`QuotaResumeProbe.Evaluate`(见下)。Codex 官方 ChatGPT:`ProviderCatalogService.EvaluateChatGpt`——账号任一窗口或附加限额打满、`spend_control` 命中、或目标模型在 `model_usage` 里标为不可用 → `limited`;账号窗口全部 allowed 且 0 ≤ 百分比 < 100、无未归因限流、目标模型未被列为不可用 → `ok`;无数据、格式未核实或任一窗口缺百分比 → `unknown`。`model_usage` 只枚举有过记录的模型,没列出不算不可用。
- 余额类(`claude-api`、`codex-active`):余额已知、未受限且大于 0 → `ok`;受限 → `limited`;否则 `unknown`。

**Claude 订阅的额度门禁按模型族**(`QuotaResumeProbe.Evaluate`,修订 ADR-0004):

- 证据必须来自 OAuth(`evidence-not-oauth`),且有数据、快照可用、无存储故障、目标模型能识别出模型族(`ClaudeModelFamilies`:fable / opus / sonnet / haiku),否则 `unknown`。
- 相关窗口 = 非 scoped 窗口(`five_hour`、`seven_day` 等)+ 与目标**同一模型族**的 `weekly_scoped:*`(任何版本都算,宁可多等)+ 认不出模型族的 scoped(不知道它管不管目标,就当它管)。其它已知模型族的 scoped 与目标无关:Fable 周额度打满不挡 Opus 项目,反之亦然。一次成功响应里的 `limits` 就是账号的全部模型级上限,目标模型没有专属窗口时由主窗口决定。
- 任一相关窗口 ≥100% 或 `status=blocked`(承接读数满额也算),或桶级未归因限流 → `limited`;`BlockingResetUtc` 取挡住它的窗口里最晚的重置时间,`Detail` 形如 `weekly_scoped:Fable:blocked`。
- 不受限,且 `five_hour` 与 `seven_day` 都是本次实时读数(非承接、0 ≤ 百分比 < 100)、其它非 scoped 相关窗口同样实时、相关 scoped 里没有「非承接却缺百分比」的窗口 → `ok`。相关 scoped 被本次响应省略(承接读数)且未满时不再阻塞;主窗口的承接读数仍判未核实。
- 其余 → `unknown`,`Detail` 只含窗口名与状态码(如 `five_hour:carried`、`seven_day:missing`、`weekly_scoped:Opus:no-percent`)。
- 禁止以 CLI/Haiku 探测成功代替目标模型额度恢复:CLI 证据不能为目标模型建立限流或可用事实。

### 布防:逐项目绑定会话与模型

`arm.set` 布防时依次:

1. `ResumeBindingService.BindAsync` → `ProviderCatalogService.BindWithCatalogAsync(providerId, 默认模型)`:供应商必须存在、agent 为 `claudecode` 或 `codex`、身份非空,默认模型必须在本次已核实的实时目录里;得到固定的 `ResumeTarget` 与这份目录。
2. `ResumeSessionCatalog.Bind`:为每个选中项目绑定最近修改的原生会话 UUID(见「会话边界」);任一项目没有可核实的会话,整次布防失败。
3. `ResumeBindingService.ChooseModel` 逐项目选模型:手选 > 会话最近模型 > 目标默认(规则见 `ProjectRef.modelSource`)。
4. 用 `ProviderExecutionPlan.Create(…, ExecutionPurpose.Arm)` 把每个项目的执行计划完整走一遍(不 spawn,不检查会话占用与令牌剩余寿命):Codex 未登录、组织/管理配置锁不住等问题在布防时就暴露,而不是等到额度恢复那一刻。
5. 会话正开在客户端、会话模型不在目录时只返回 `warnings`,不阻止布防;最后复核供应商身份未在布防期间改变。

`ControlPlaneBridge` 随后在配置锁内比对:布防期间 `config.json` 若被改过就拒绝(「布防期间设置已改变,请重试」);否则写入 `selected`(带会话与模型)、`resumeTarget`、`resumeModel`、`enabled=true`、`armed=true`、新 `armCycleId` 与 `continuous`。模型目录升级不重写当前周期,下次布防重新选择。`arm.preview` 只读本地会话事实,不调用绑定、不发网络请求、不写状态。

### 逐项目准入的四个维度

每次取证 `ProviderResumeProbe.ProbeAsync` 先读本地事实(毫秒级),再按去重后的项目模型做一次供应商观测,最后逐项目组装 `ProjectAdmission`(按 `selected` 顺序放进 `ClaudeProbeResult.Projects`)。四个维度彼此独立,任何一维「未核实」都只导致等待,绝不放行:

| 维度 | 取值 | 来源 | 阻挡范围 |
|---|---|---|---|
| 义务(Evidence) | `interrupted` / `clear` / `superseded` / `unverified` / `none` | `ClaudeSessionEvidence.TryRead`:按 UUID 定位本项目的转录,从尾部 1 MiB(不够再扩到 8 MiB 封顶)倒序找最近一条主链(非 sidechain、非 meta)的**真实应答或限流记录**;`isApiErrorMessage=true` 且 `error="rate_limit"`(Claude Code 写的合成记录,`message.model="<synthetic>"`)即限额中断,`quotaLimits.rateLimitType` / `resetsAt` 给出类型与重置时间,记录 `uuid` 是中断 id。限流以外的合成记录(`error` 为 `server_error` / `authentication_failed` / `unknown` 等:原生自动继续撞上 529、令牌被拒……)跳过——续的那一下失败了,中断仍然成立,绝不据此判「已在别处续上」。中断之后同一项目目录里**新开**的会话有了真实应答 → `superseded`(`ResumeSessionCatalog.ContinuedInAnotherSession`:只看绑定会话同一文件夹下按 UUID 命名的顶层转录,按修改时间预筛;「新开」= 首条带时间戳的记录晚于中断,或文件本身在中断之后才创建——分叉续写会复制旧记录;中断前就开着的并行会话不算)。它是三态:读不了目录、或中断后改过的会话文件读不出归属目录时回答「判断不了」,取证判 `unverified`,最终门禁记 `not-started:evidence-unverified`,都只等待。会话头部(`cwd`、首条时间戳)按行读、单行不设上限(首条提问贴图时图片以 base64 内联,2026-11 本机 138 个会话里 43 个的首条带 `cwd` 的记录超过 64 KiB,最长约 2 MB),最多 60 行、32 MiB。找不到或读不出本项目的转录、尾部(最多 8 MiB)里没有真实应答或限流记录,或中断记录缺 uuid → `unverified`(只是不判待续跑,不会放行)。Codex 没有会话证据 → `none`,沿用「观测到它的模型受限」的旧语义 | 只挡本项目 |
| 额度(Quota) | `ok` / `limited` / `unknown` / `provider-changed` / `identity-unverified` / `credentials-unavailable` / `model-unverified` | `CheckModelsAsync`,按本项目的有效模型 | 只挡用这个模型的项目 |
| 占用(Session) | `idle` / `in-use` / `cwd-busy` / `unverified` / `n/a` | `ResumeSessionCatalog.Occupancy` 读 `<配置根>\sessions\<pid>.json`(字段 `pid` / `sessionId` / `cwd` / `procStart` / `startedAt` / `status` / `entrypoint`):绑定会话的进程活着且不能证明是 PID 复用 → `in-use`;同目录另一会话活着且 `status=busy` → `cwd-busy`;登记缺 pid 或进程状态读不出 → `unverified`。PID 复用只认「活着的进程比登记的 Claude 进程启动得晚」(`procStart` 是合理的 FILETIME 时直接比,否则退回毫秒时间戳 `startedAt` 即登记时刻):登记那一刻这个 PID 属于 Claude,在那之前就启动、至今还活着的进程只能是它本身;启动得比登记还早却又对不上,说明字段语义变了 → `unverified`,绝不当成空闲。中断前就开着、`status=idle` 的同目录会话不算占用(否则留一个客户端窗口开着就永远续不了)。Codex → `n/a` | 只挡本项目 |
| 进程门禁 | `activeRunId` / `pendingCancellationRunId` | 全局 | 同一时刻最多一个续跑进程 |

`ProjectAdmission.Admissible = Quota == "ok" && Session ∈ {idle, n/a} && Evidence ∉ {unverified, superseded}`。顶层 `Ready` / `Reason` 只是兼容汇总(任一项目放行即 Ready),具体启动谁由状态机逐项目决定。会话证据只读文件尾部元数据字段,绝不读取或输出对话正文;字段形状以 2026-10-30 本机 Claude Code 2.1.185–2.1.308 的会话录制为准。

### 引擎单拍流程

```text
每 30 秒一拍(ResumeEngine.RunOnceAsync,单航班;上一拍未完就跳过)
  |
  +- 先对账进程门禁:pendingCancellationRunId / activeRunId 仍存活或无法核验 → 本拍结束
  +- 未布防 → 空转;armCycleId 换代 → CheckerCycle.Initialize 重置周期状态
  +- 遗留失败/停止/running → 锁存 blocked;一次性周期已 done 但未解除 → 只补做解除
  +- 未到探测节奏(有项目待续跑时 4 分钟,否则 probeIntervalMinutes)→ 空转
  v
ProviderResumeProbe:本地会话事实 + 一次供应商观测
  |   (请求期间 ResumeTarget.Identity 变化 → 丢弃证据,针对新配置重新取证)
  v
CheckerCycle.OnProbe:逐项目更新 projectStatus / projectNotes
  |   (随后 ResumeEngine 写 resume.probe.evaluated 日志,并按需写续跑提醒)
  |
  +- 没有可启动的项目 → 保持监视;一次性周期且全部项目完成 → 锁内提交完成并解除布防
  +- 有「待续跑且准入」的项目 → 续跑一轮(串行):
        按 selected 顺序取第一个可启动项目
          → 最终门禁(重读会话尾部与占用)
          → PrepareActiveRun:running + 精确 RunId + 中断 uuid 同一次落盘
          → spawn → 结果写回
        未启动:记 not-started:<原因>,本轮跳过该项目,不锁周期
        已 spawn:下一个项目启动前必须重新取证(同模型的项目共用额度,前一个可能刚好用完)
        一跑就被限流:这个项目与同模型的项目回到等待、本轮跳过,别的模型的项目照常轮到
        再没有可启动项目 → EndRound,回到 waiting
```

每次取证写一行 `resume.probe.evaluated` 日志,带决策、周期原因、待续跑数与逐项目 `名=模型|额度[:窗口]|占用|会话证据|原因`;只含窗口名、状态码与项目名,不含令牌、账号或会话正文。限流与未就绪日志也带具体原因,不再压成 `unknown`。

### 状态机与一次性/连续模式

`CheckerCycle.OnProbe` 对每个选中项目:

- **待续跑**:会话尾部是限额中断,且该中断 uuid 不在 `consumedInterruptions` 里;没有会话证据的项目(Codex)沿用旧语义,观测到它的模型受限即待续跑。已完成的项目在连续模式下再次被打断会重新入队;一次性模式下每个项目最多续一次。
- **已在别处继续**:曾经待续跑,而会话尾部已有新的真实应答(证据变为 `clear`)→ `continued-elsewhere`,视为完成——用户已在桌面、终端或手机上自己续上;或者中断之后同目录新开的会话接着干了(证据 `superseded`)→ 同样记 `continued-elsewhere`,`projectNotes` 的 `detail=other-session`,界面写「已在别的会话继续」。后者不再自动续旧会话:旧上下文去改已经变化的工作树,可能重做或回退刚完成的工作;仍要续它就自己在客户端里打开。看到 `superseded` 时(取证或最终门禁)这次中断的 uuid 随即记进 `consumedInterruptions`:那个新会话之后被删、或某一拍读不到,旧会话的尾部仍是这条中断,也不会被重新判成待续跑(连续模式下尤其如此)。
- 存在「待续跑且三维都放行」的项目 → `StartResuming`;一次性周期全部项目完成(`success` 或 `continued-elsewhere`)→ `CompleteCycle`;其它情况保持监视。从未被打断的项目会让一次性周期继续守着:宁可多守一会儿,也不能在它稍后被打断时已经解除布防。
- 上次「未启动」的原因在下一次真正尝试前一直保留在 `projectNotes` 里,额度放行不等于那个原因已经消失。

**未启动不锁周期**:会话刚被打开或同目录有会话在跑(`session-in-use` / `cwd-busy` / `session-unverified`)、会话尾部变了或读不出(`evidence-changed` / `evidence-unverified`)、访问令牌剩余不足 45 分钟(`token-expiring`)、执行条件未核实(`execution-unverified`)、启动前配置变化(`configuration-changed`)、执行器或项目目录不存在(`no-claude`)——这些都发生在 spawn 之前,没有 RunId、没有副作用:只撤销精确预登记、记 `not-started:<原因>`(原因与说明写进 `projectNotes`,不改 `projectStatus`)、本轮跳过该项目,下一拍重新评估。其中不会自己好的原因(`no-claude`、`execution-unverified`)同一项目连续出现第 3 次起发飞书提醒(`resume-not-started`,按周期|项目|原因去重,所以只发一次;第 3 次写入失败时第 4 次补发),免得一整夜静默重试、第二天才在界面上看到。最终门禁发现会话已经被续上(或同目录新开的会话接着干了)时直接记 `continued-elsewhere`;发现项目已被移出队列或这个项目的执行身份变化时重新读取队列或重新取证。

**启动后的失败仍阻断自动重放**(ADR-0002):进程一旦 spawn,除 `success`、`limited`、`continued-elsewhere` 外的任何结果都锁存本周期(`replayBlocked`,`phase=blocked`),必须解除并重新布防。续跑后又被判限流时这个项目回到等待,本轮跳过它与同模型的其它项目(它们凭的是同一份刚被实际请求推翻的额度证据,误判时不按项目数成倍空跑——每次空跑都会往用户会话里追加一条续跑提示和一条限流记录),继续别的模型的项目;「额度说可用、一跑就被限流」按项目计数(`CheckerState.projectLimitedRefires`,这个项目成功一次即清零;升级前只有全周期计数时,第一次按项目记以它为起点),同一项目连续 6 次则记为 `error` 并锁存,防止误判死循环。旧版是全周期一个计数、限流即结束本轮:排在队首、反复误判的项目会把别的模型的项目一直饿着,别的项目的成功还会把它清零。

### 最终门禁、执行与提醒

每个项目启动前,`beforeStart` 回调重新读取配置并确认:仍然布防且周期未变、项目仍在队列里、**这个项目自己的**执行身份(`ResumeTarget.ProjectIdentity`:目标的供应商/账号/agent/默认模型、续跑提示与权限开关,加上本项目的路径、会话与有效模型)未变;对 Claude 的限额中断还要重读会话尾部——必须仍是本次要续的那一条中断(uuid 相同)、同目录没有中断后新开并接着干活的会话,且此刻没有任何客户端打开它。全部通过才 `PrepareActiveRun`,把 `running`、精确 RunId 与中断 uuid 一次性落盘。运行中的存续条件(`shouldContinue`)同样只比较这个项目的执行身份:队列里别的项目被移除或改模型,不会把正在跑的项目杀掉、锁死周期;取证阶段仍用全队列身份(`ResumeTarget.Identity`),变了就丢弃证据重新取证。

与原生能力分工(上游盘点见 ADR-0006 §3):仍打开着的交互会话交给 Claude Code 原生的 `autoContinueAtUsageLimit`;它不覆盖 Remote Control 会话、重置超过 24 小时的周额度、已关闭的会话与 `-p`。AI Resume 对开着的会话不重复续跑;额度恢复(中断记录的重置时间,缺失时按中断时间)10 分钟后会话仍开着、尾部仍是那条中断,就飞书提醒一次。

续跑提醒经 `IResumeNotices`(`ResumeNoticeWriter`)写进同一个完成通知队列(`source=ai-resume`,`kind=resume`,只写项目路径、模型 ID 与状态码):

| 类型 | 时机 | 去重 |
|---|---|---|
| `session-open` | 额度已恢复、宽限 10 分钟后会话仍开在客户端或同目录有会话在跑 | 每个周期 × 项目 × 中断一次 |
| `resume-started` | 监控循环第一次询问时(进程已经起来);写入失败下次询问再试 | 每个周期 × 项目 × 中断一次 |
| `resume-succeeded` / `resume-failed` | 已 spawn 的项目结束且结果不是 `limited` | 每个周期 × 项目 × 运行 × 状态一次 |
| `credentials-unavailable` | 项目额度结论为 `credentials-unavailable` 或 `identity-unverified`(`detail` 为原因码,文案按原因给处理办法) | 每个周期每个原因每 4 小时一次 |
| `provider-changed` | 账号身份变化,需要重新布防 | 每个周期一次 |
| `resume-not-started` | 同一项目连续 3 次因同一个不会自己好的原因(`no-claude` / `execution-unverified`)没能启动 | 每个周期 × 项目 × 原因一次 |
| `login-expiring` / `login-expired` | `ClaudeLoginReminder` 的到期阈值;登录被 Claude Code 清空(到期时间消失)时为每天一次的 `login-expired` | 每个登录到期时间 × 阈值一次;清空的登录每个本地日一次 |

提醒写进队列失败(杀软占用、磁盘满)时撤回内存里的去重键,下一拍遇到同一件事重写,不会在本进程里永远发不出去。

事件 ID 是 SHA-256(`ai-resume|<类型>|<去重键>`) 的前 16 位十六进制;同一件事无论写几次、Worker 重启几次,投递端都只发一次(文件名去重 + 7 天已送达表 + 飞书幂等键)。写不进队列只记警告,不影响续跑。

### 安全门禁与恢复

项目行状态只属于当前布防周期。一次性周期把最终结果留在 SQLite 里供诊断,但 Worker 一自动解除布防,界面就不再把它当作当前状态展示;连续模式保持布防,可以继续显示本周期进度。`arm.get` 在持有 `config.json.write.lock` 时同时抓取配置与严格读取的 SQLite 状态,所以看不到「周期状态已 done、一次性解除尚未写进配置」的中间提交。手动刷新、30 秒轮询、布防与解除都会重读并重绘引擎摘要和每个项目行。已知的本地失败与未知状态码都按失败归类并给出中文说明,不会以中性的原始码呈现。

`activeRunId` 与 `pendingCancellationRunId` 是跨周期的安全门禁。匹配上的或无法核验的自有进程会阻止任何新的续跑;只有现有 RunId 被证明已消失或不匹配时才清理。逐项目模式下,触发本轮的那份证据在有项目真正 spawn 之前一直有效(没能启动的项目不消耗它);任何项目 spawn 之后,下一个项目启动前都会强制重新取证,并在这个异步边界之后复核布防周期、执行身份与当前队列。相关窗口打满、只有 CLI 证据、主窗口只有承接读数、快照不可用或取证异常,都只让对应项目(或本轮)回到等待,不会启动下一个进程。选中的项目必须有原生会话绑定;完成提交在配置锁内复核最新队列,刚有新项目加入时不会把尚未完成的队列提交成 done 或解除布防。解除布防或移除项目会按精确 RunId 请求终止,终止未确认时保留门禁。续跑输出只把结构化的 `rate_limit_event.rate_limit_info.status` 当作可重试的额度证据;Write/Edit/Bash 之后、未知工具活动之后、NDJSON 损坏或出现非对象行之后的限流事件,一律变成 `limited-side-effects` 并锁存周期,明确只读的工具之后才允许回到额度等待。恢复出的一次性 `phase=done + armed=true` 状态只补做解除布防,绝不再探测或重放整轮。一个没有终止证据就消失的 `activeRunId` 记为 `exit-null`(未确认完成);只有已确认的 `pendingCancellationRunId` 退出才记为 `stopped`。控制面把匹配上的旧周期运行明确报告为仍在运行、把无法核验的报告为未确认,而不是把新布防的周期说成「等待初始化」。解除布防时对账照常进行;Worker 不在仍是布防状态下优先级最高的告警。

IPC、观察与续跑服务启动前,Worker 先对遗留的进程登记跑 `RecoverAsync`;托管服务被显式锁定为串行启动,配置无法绕过这道门。恢复出的 `starting` 运行若登记仍然匹配,就推进为 `running` 而不再 spawn;证据无法核验时保持 `starting` 并保留运行键。进程监督一直持有 Windows Job Object 句柄,直到 `QueryInformationJobObject` 报告 `ActiveProcesses=0`。当前进程拥有该 Job 时,编排器与 `ResumeEngine` 都以监督器状态为准,而不是只看登记里的外层 PID;外层包装已退出、后代仍活着属于正常的 `Alive`,取消时先 `TerminateJobObject` 再释放句柄。GUI 没有 Job 句柄,所以外层 PID 消失的现有登记在 GUI 里报告为未核实,而不是已完成。当前依赖图里没有等价的 Job 封装,这仍是对 `kernel32` 的薄 P/Invoke 扩展。`Process.Start` 不能创建挂起进程,因此 Start 与 Assign 之间存在一个已记录的窄崩溃窗口:先持久化精确 PID、立即请求取消、在确认终止前保留所有权,而不是删除登记行。`ProcessStartResult` 的每个消费者都把「`Started=true` 但错误码非空」当作结果不明的启动,必须先取消,不能变成 `running`。

续跑启动时,`IProcessSupervisor.StartAsync` 抛出的 `OperationCanceledException` 有更窄的契约:尚未产生任何登记或进程创建副作用。`ClaudeResumeRunner` 把这个事实连同预先生成的 RunId 一起返回;只有周期、项目、状态与精确 RunId 都仍然匹配时,`ResumeEngine` 才经 `CheckerCycle.RollbackPreparedRun` 撤销 spawn 前的 `running` 标记——项目回到待续跑,本次中断 uuid 也从 `consumedInterruptions` 里撤回。任何不匹配都失败关闭。普通启动失败与结果不明的启动绝不走这条回滚路径。

RunContract 的启动驱动同时是每个 RunId 的启动/取消串行化边界。取消在等待活动驱动前先持久化;驱动在供应商启动后、恢复进程检查后和监督器启动后都重读它,所以一次运行不可能先进入终态 cancelled、再 spawn 一个无主进程。未确认的取消在每次观测时按精确 RunId 重试。终止进程树期间,供应商失败持久化在仍为 running 的行上;只有 `ActiveProcesses=0`(或等价的精确已消失证据)才允许进入终态并释放运行键。结算在同一个 SQLite `IMMEDIATE` 事务里读取取消标记与待定失败并写入最终状态,已提交的用户取消优先于失败。

内部状态机由受控测试覆盖;一次真实的 Claude 用量上限中断 → 逐项目自动续跑的端到端仍**待真机验证**。

### 执行计划

`ClaudeResumeRunner` 保留既有的监督与取消契约,分派 Claude stream-json 或 Codex exec JSONL。生产路径经 `ProviderExecutionPlan` 生成计划,启动内部 helper `AiResume.Worker.exe internal-agent-run <base64 规格>`(不起第二个 Host;只有环境里有 `AI_RESUME_INTERNAL_RUN=1` 才执行),用 .NET `ArgumentList` 传模型 ID 与提示词,不经 cmd/PowerShell 解释,捕获 stdout/stderr 并在输出落盘后确认退出码。续跑一律针对精确 UUID,绝不 `--last` 或 `--continue`。子进程环境里固定 `AI_RESUME_INTERNAL_RUN=1`,因此 AI Resume 自己的续跑不会触发完成钩子通知,改由 `resume-succeeded` / `resume-failed` 提醒告知结果。

计划创建时先对相关配置文件(Claude:`<配置根>\settings.json` / `settings.local.json`、`%ProgramFiles%\ClaudeCode\managed-settings.json` 与项目 `.claude\settings.json` / `settings.local.json`;Codex:`<CODEX_HOME>\config.toml` / `managed_config.toml`、`%ProgramData%\OpenAI\Codex\config.toml`,以及项目目录与其各级上级目录的 `.codex\config.toml`)取只读共享句柄(`ExecutionConfigLease`),整个运行期间阻止它们被改写或替换,验证期间存在性变化即拒绝;Claude 的全局配置(`.claude.json` / `.config.json`)由 Claude Code 频繁改写,不纳入租约。随后复核供应商身份与 agent 未变、`resumeModel` 仍等于目标默认模型、项目有效模型是安全 ID、原生会话仍归属该项目(`ResumeSessionCatalog.Matches`)、执行器已安装。

- **Claude**:spawn 前(`ExecutionPurpose.Spawn`)要求会话空闲,否则抛 `ResumeDeferredException`(`session-in-use` / `cwd-busy` / `session-unverified`);订阅凭据的访问令牌剩余寿命必须 ≥ 45 分钟(`ProviderExecutionPlan.MinimumTokenLifetime`),否则 `token-expiring`——子进程拿到的是启动那一刻的令牌,没有 refresh token,无法自行续期;45 分钟门槛只保证启动时充足,超长续跑仍可能中途令牌过期(已知缺口,见 ADR-0006 §5)。原生的 `CLAUDE_CODE_PROVIDER_MANAGED_BY_HOST=1` 阻止 settings 改写连接,而普通权限与 hooks 照常加载;组织/动态管理策略无法核实时拒绝执行。连接经环境变量固定:`ANTHROPIC_BASE_URL` 为该行端点;订阅行注入当前访问令牌到 `CLAUDE_CODE_OAUTH_TOKEN`,`claude-api` 行按来源注入 `ANTHROPIC_AUTH_TOKEN` 或 `ANTHROPIC_API_KEY`,其余凭据变量置空。参数为 `--resume <UUID> -p <提示词> --model <项目模型> --output-format stream-json --verbose`(`skipPermissions` 为真时追加 `--dangerously-skip-permissions`);stream-json 的 `system/init` 报告的模型必须等于该项目的有效模型,否则不算成功,记为 `exit-null`(未确认完成)并锁存本周期。权限模式不额外传参:Claude Code 按用户自己的 settings 运行,AI Resume 既不提权也不降权。
- **Codex**:本机 Codex 版本必须恰为 0.148.0(原生会话独占锁按这个固定上游核实过),且 `<CODEX_HOME>\managed_config.toml` 必须存在并被配置租约持有(管理配置的存在性要能锁定),否则执行条件未核实——这两项在布防时就会检查。官方账号用 `-c model_provider="openai" -c openai_base_url=<该认证方式的官方端点>` 覆盖,并把 `OPENAI_BASE_URL` 钉成同一官方端点,完全不传凭据:Codex 自己读 `auth.json`。第三方静态 provider 保留原 provider ID,用 `-c` 覆盖端点/wire/认证引用,密钥与请求头只经环境变量(`AI_RESUME_PROVIDER_KEY`、`AI_RESUME_HEADER_<n>`)传递;复杂的认证/查询参数/配置层(项目或管理配置覆盖模型连接、活动 profile 自带 `model_provider`、`active_profile` / `profile_v2`、自定义会话存储、未知 provider 字段)一律拒绝。参数为 `exec resume <UUID> <提示词> --model <项目模型> --json --skip-git-repo-check`。Codex 错误 JSONL 没有结构化限流码:任何错误、重路由、未知或损坏事件都阻断重放。

固定上游证据与兼容边界见 [ADR-0004](adr/0004-provider-bound-resume.md)。

## cc-connect 配置激活(已退役)

本节在 v2.1 及之前描述 GUI「生成并重启 cc-connect」与 CLI `AiResume.Worker.exe cc-connect-apply` 共用的 `CcConnectApplyCommand.Apply` 激活状态机:候选 `config.toml` 经上游 `config format` 校验、核验 `daemon.json` 与管理 API、计划任务契约与单消费者检查、原子提交后经管理 API `POST /api/v1/restart` 自重启,再按锁 PID 换代与同代日志验证新进程,失败时仅在文件仍精确等于本次提交字节时回滚;以及为什么不用上游 `restart --force`。

这整套机制已于 2026-10-30 随 ADR-0006 删除:`AiResume.Wrapper` 项目、`cc-connect-apply` / `preflight` / `feishu-check` 命令、`cutover.*` / `agent.*` / `feishu.*` RPC 与 GUI 关窗守卫都已不存在,AI Resume 不再触碰 cc-connect 的配置、进程或计划任务。需要查阅当时的完整契约时,读 [ADR-0003](adr/0003-cc-connect-direct-and-control-plane.md)、[ADR-0005](adr/0005-owned-credentials-official-codex-and-hooks.md) 或 `git show 49328da:docs/ARCHITECTURE.md`。

## agent、供应商与模型

三层彼此独立:

| 层 | 含义 | 现役取值 |
|---|---|---|
| agent | 本地执行器、工具与原生会话实现 | 可续跑:`claudecode`、`codex`;`opencode` 只出现在只展示余额的 DeepSeek 行 |
| 供应商 | 远端端点、凭据来源与协议映射 | `ProviderRegistry` 的行:`claude-subscription`、`claude-api`、`codex-official`、`codex-active`、`deepseek` |
| 模型 | 发给供应商的模型 ID | 布防时从该供应商的**实时目录**逐项目确定,冻结在 `ProjectRef.modelId` |

布防固定的是供应商、账号身份与 agent(`ResumeTarget`),外加每个项目的原生会话 UUID 与模型;供应商、账号或端点改变都必须重新布防,不自动换供应商重放。共用同一模型的项目共享同一份额度观测、排队串行,每个启动前都重新核验——「项目 A、B 共用模型 A」就是两个项目各自把有效模型设为 A。

Claude Code 走兼容 Anthropic 协议的 DeepSeek 端点是合法组合:当 Claude Code 自己的配置把 `ANTHROPIC_BASE_URL` 指向 `api.deepseek.com` 时,它出现为可续跑的 `claude-api` 行(「Claude · DeepSeek」)。这与 OpenCode 的 DeepSeek 账户是两回事:AI Resume 绝不把 OpenCode 的密钥挪给 Claude Code 用。

历史:v2.1 及之前本节还描述 cc-connect `config.toml` 的 provider/model 生成规则(`agent_types` 过滤、含 `/anthropic` 的端点不给 Codex 引用、`agent_model_lists` 补全、`[AI Resume] ` alias 所有权前缀、Codex agent 的 `cmd` 覆盖等),以及 AI Resume 自持 `DEEPSEEK_API_KEY` 的「DeepSeek · 官方 API」续跑行。前者随 cc-connect 于 2026-10-30 退役;后者改为只读 OpenCode 密钥、只展示余额的 `deepseek` 行。

## 额度来源优先级与连续性

权威来源是 Anthropic 的 OAuth usage 响应。现役生产刷新不再回退到推理进程:OAuth 失败返回不可用快照,只保留符合承接规则的历史读数。CLI 映射只保留给显式诊断与旧的注入式调用方。解析器逐字段优先使用现代 `limits` 数组(`session`、`weekly_all` 与每个按模型的 `weekly_scoped`),只在现代字段缺失时回退旧的顶层 `five_hour` / `seven_day`。既无百分比也无重置时间的窗口是空壳,不算额度证据。OAuth 与 CLI `rate_limit_event` 都按稀疏观测处理:缺字段、缺窗口不是删除信号。

AI Resume 把最新的账号级 `UsageSnapshot` 存进 SQLite `quota_snapshots` 表(schema v5 引入,当前 schema 版本 6),主键是供应商加账号指纹——即上文由 `oauthAccount` 组织/账号 UUID 派生的 SHA-256。核实不了账号时指纹为空,快照既不落库也不承接。存储在后台额度请求里惰性打开,绝不在 WPF 构造函数里打开。跨进程更新获取 SQLite `IMMEDIATE` 事务,在事务内完成读取、合并与写回,所以基于旧基线构造的稀疏快照不会覆盖另一个窗口刚提交的具体值。显式字段通常优先。较新的实时 OAuth 读数带着明确百分比时以它为准,同一重置周期里也可以下降(中途升级套餐、服务端提前重置);较旧(迟到)或非 OAuth 的观测才在同一重置周期里取已用百分比的较大值,迟到的 99% 不能替换已提交的 100%。重置周期冲突时按 `CapturedAt` 排序,更旧的观测不能把更新的周期回滚。scoped 窗口以完整 scope JSON 的规范化 SHA-256 作为内部身份,显示名重复或响应重排都不会串合并。缺失的字段或窗口只能在同一账号、同一身份、同一未来 `resetAtUnix` 周期内承接;重置换代、到期或账号变化立即失效。承接窗口重算 `resetAfterSeconds`,其阻挡状态参与安全判定,并标记 `carriedForward`。

CLI 回退的 `IsLimited` 只是桶/供应商级事实,说不出是哪个窗口导致限流;单个 5 小时或 7 天窗口只由它自己的 100% 读数标为 blocked。碰巧带出部分窗口的失败 CLI 探测仍是不可用,绝不让供应商变健康,并使用 30 秒失败 TTL 而不是 5 分钟成功 TTL。`UsageBucket.LimitReached` 继续汇总所有主窗口与 scoped 阻挡,供安全判定使用;但续跑准入只看目标模型相关的窗口(见上文按模型族的门禁)。GUI 的两块主窗口磁贴保持各自的状态,不会因为另一个 scoped 窗口打满而标满;「按模型」磁贴显示已用最高或已满的那条 scoped。承接读数渲染成琥珀色「最近读数」,绝不渲染成绿色或暗示限流;有真实百分比时即使缺重置时间也照常显示;只有重置时间的窗口用非数值的不定轨道;`hasData=false` 的结果绝不当作健康额度。过期窗口的刷新退避按服务端重置周期计,不会被本地 5 小时磁贴的重绘清掉。

供应商健康与 Claude 额度是两套证据。现役 GUI 只用 `providers.catalog`:已认证的模型目录、Claude OAuth usage、DeepSeek 余额、兼容第三方 usage 与 ChatGPT 账号窗口,全是元数据请求,不做推理。模型与额度分别核实,一个数据源失败不会让其它供应商失效。刷新失败会清掉旧的窗口与余额;迟到的 Claude 响应只更新 Claude 自己的缓存磁贴,切换供应商不会把别的账号数据贴错标签。每个窗口保留自己的阻挡与承接状态。旧的 `providers.probe` RPC 已于 v2.1 删除,GUI 没有任何推理型探测。Codex home 的解析顺序仍是构造参数、`AI_RESUME_CODEX_HOME`、`CODEX_HOME`、用户目录。当前、未过期、账号有效且大于 0 的第三方余额是供应商/账号证据;过期数据与缺失的额度接口不能授权自动恢复;官方 OpenAI/ChatGPT 的 OAuth 凭据不会发往第三方余额请求。

## 会话边界

AI Resume 只与客户端落盘的原生会话打交道,不在自己这里维护会话:

- **绑定**:`ResumeSessionCatalog.Bind` 为每个项目取最近修改的原生会话。Claude 在 `<配置根>\projects\` 下递归找 `*.jsonl`,文件名即会话 UUID,`cwd` 取自文件头部;Codex 在 `<CODEX_HOME>\sessions\` 下读首行 `session_meta`(`source` 只认 `cli` / `vscode` / `exec`)。`subagents` 目录下的文件一律忽略。
- **核实**:执行前再按 UUID 定位会话文件,其 `cwd` 必须仍是该项目(`Matches`),否则「原生会话归属无法核实」;会话证据的路径缓存键同时含 UUID 与项目路径,不会凭 UUID 借用别的项目的文件。
- **执行**:只用 `claude --resume <UUID>` / `codex exec resume <UUID>`,绝不 `--last` / `--continue`。
- **只读**:AI Resume 只读会话头部与尾部的元数据字段,不读对话正文,不修改、归档或删除原生会话。会话的删除归各客户端自己;Codex 原生线程要经 Codex / app-server 路径归档或删除,而不是删掉 AI Resume 的引用。
- **占用**:会话开在任何客户端(桌面、VS Code、终端或 Remote Control 桥)时由该客户端自己继续,AI Resume 只挡这一个项目,见「逐项目准入的四个维度」。

历史:v2.1 及之前本节描述 cc-connect v1.4.1 的会话语义(切换 agent 后 `sessions.InvalidateForAgent` 清空旧原生会话、`Session.ActiveProvider` 不随之清空、`/provider switch` 与 `/new` 的用法、当前会话受删除保护)。这些随 cc-connect 于 2026-10-30 退役,不再适用于本产品。

## 完成通知

### 通知源

注册表(`NotificationRegistry`)仍装配 5 个适配器,用户意图持久化在 `ProductConfig.notifySources`。GUI 主界面只显示 Claude Code 与 OpenCode,其余收进「视图 › 显示其他通知源」;隐藏的源若仍启用或断链,主界面留一句提示。

| 源 | 我方条目的位置 | 完成边界 | 其它事件 |
|---|---|---|---|
| Claude Code | `<配置根>\settings.json` 的 `hooks`(配置根跟随 `CLAUDE_CONFIG_DIR`,未设置时 `%USERPROFILE%\.claude`) | `Stop`(不写 matcher) | `StopFailure` → `--kind=failure`;`Notification` → `--kind=decision` |
| OpenCode(仅 2.x) | OpenCode 配置目录(`%XDG_CONFIG_HOME%\opencode`,未设置时 `%USERPROFILE%\.config\opencode`)下的 `plugins\airesume-notify\index.ts` | 顶层会话 `session.execution.succeeded`,且没有仍在运行的直接子会话 | `permission.asked`(含子会话)、`form.created`(AI 提问)、顶层 `session.execution.failed`(宽限期内未被自动续跑) → `--kind=decision` |
| Codex | `~/.codex/hooks.json` 的 `hooks.Stop` 末尾我方分组 + `config.toml` 的 `hooks.state` 信任哈希 | `Stop` | — |
| Cline | `%USERPROFILE%\Documents\Cline\Hooks\TaskComplete.ps1`(包装脚本,保留用户原脚本) | `TaskComplete` | — |
| Qoder | `%USERPROFILE%\.qoder\settings.json` 的 `hooks.Stop` | `Stop` | — |

单次请求级的供应商回调不算整件任务的完成。

**Claude Code**(按官方 hooks 文档于 2026-10-30 核对,本机 CLI 2.1.300 / VS Code 2.1.280 / Desktop 2.1.309):三组条目一律写成官方 **exec form**——`{"type": "command", "command": "<AiResume.Hook.exe 绝对路径>", "args": ["claudecode", …], "timeout": 30}`,不经 shell(Claude Code 2.1.139+)。`StopFailure`(2.1.78+)是回合因 API 错误结束时代替 `Stop` 发出的事件,用量上限、登录失效、账单问题此前完全没有通知。`Notification` 只订阅真在等人的类型,matcher 为 `ClaudeCodeNotificationAdapter.DecisionMatcher`:`permission_prompt`、`worker_permission_prompt`(agent team 的协作 agent 卡在授权上)、`elicitation_dialog`、`elicitation_url_dialog`、`agent_needs_input`、`quota_auto_resume_stale`、`quota_auto_resume_disabled`(用 `|` 连接);不订阅 `idle_prompt`、`auth_success`、`agent_completed`。所有权由可执行文件名精确等于 `AiResume.Hook.exe` 且参数只有 `claudecode` 加我方 `--kind=` 开关来证明;旧的 shell form 条目照样认领,以便迁移(旧写法在 Claude Code 回退到 PowerShell 时会 ParserError,本机复现过)。只增删我方条目:我方条目若混在用户的分组里,会被移出单独成组,而不是改写那一组的 matcher。三组都在且形状规范才算「已启用」,缺组或仍是旧格式标为断链,关掉再打开或重新 `install` 即修复。Claude Code 热加载 settings(据 ADR-0006 的上游盘点),不需要新会话。

**OpenCode**:2.x(本机 Desktop 2.2.3)不再加载 1.x 时代的单文件插件,2026-10-29 升级后旧插件的通知全部失效。现在写官方 v2 目录形态插件(默认导出 `{ id: "ai-resume.notify", setup(ctx) }`,经 `ctx.event.subscribe` 订阅事件,靠自动发现加载,不写进用户的 `opencode.jsonc`),插件以 `shell: false` 启动 `AiResume.Hook.exe`。启用时迁移我方旧单文件 `airesume-notify.ts`(留 `.bak`),拒绝覆盖目录里别人的 `index.*` / `server.*` / `package.json`;停用只删我方文件。「已加载」只以 OpenCode 自己的日志为外部证据:读 OpenCode 数据目录(`%XDG_DATA_HOME%\opencode`,未设置时 `%USERPROFILE%\.local\share\opencode`)下的 `log\opencode.log` 末尾 4 MiB 里、含我方插件目录路径且时间不早于插件写入时间的记录,以最后一条结论为准(一次加载先记 `loading plugin` 再记失败;之后重启成功加载,新的 `loading plugin` 覆盖旧失败)——最后是 `failed to load plugin` → 断链,最后是 `loading plugin` → 已加载,没有记录 → 待核实。Hook 仍接受 `session.idle`,只为兼容 1.x 旧插件。

Codex 的处理沿用 ADR-0005 C1:用 Codex 0.148.0 hooks 代替会被 Codex Desktop 自身功能覆盖的单值 `notify` 键。`CodexHooksNotificationAdapter` 在 `hooks.Stop` 末尾追加一个 AI Resume 分组(信任键是位置相关的,插到前面会错位用户自己的记录),并写 `[hooks.state."<hooks.json 路径>:stop:<group>:<handler>"] trusted_hash`,值为规范化 handler 的上游 canonical-JSON SHA-256(算法已对本机现有条目复算核对)。停用只删 AI Resume 分组,以及哈希等于我方命令哈希的信任记录。旧 `notify` 条目只报告、不改写。信任不匹配显示为断链,并提示在 Codex 里运行 `/hooks` 信任一次。已打开的 Codex 会话不会重载 `hooks.json`,新会话才生效。

钩子配置一律合并,绝不整体替换。用户意图与当前钩子健康分开持久化,所以「已启用但可执行文件路径断了」显示为故障,而不是「已关闭」。

### 事件写入(`AiResume.Hook`)

Hook 只做准入与落盘:按来源校验事件名(Claude Code 的 `--kind=decision` 只认 `Notification`、`--kind=failure` 只认 `StopFailure`,完成只认 `Stop`),要求 `cwd` 是绝对路径,计算稳定的 eventId,原子写入 `state\completion-events\<eventId>.json`。以下情况一律不入队:环境里有 `AI_RESUME_INTERNAL_RUN=1`(AI Resume 自己启动的续跑)、负载为空或不是 JSON 对象、非 Claude Code 来源带 `stop_hook_active=true`。Claude Code 的 `stop_hook_active=true` 不再丢弃——我方 hook 从不阻断,没有递归风险,直接丢弃会吞掉 `/goal` 等场景下真正的结束通知——改为记录该标志,由投递端按会话合并。

Claude Code 事件额外记录结构化短值:`hookEvent`、`promptId`、`error`(官方枚举,只收短的字母数字值)、`stopHookActive`、`backgroundBusy`(`background_tasks` 里有 running / pending 的 subagent、workflow 或 cloud session——结束后会唤醒会话继续干活的后台工作;shell、monitor、teammate 等常驻或内务任务不算)、`bridged`(环境变量 `CLAUDE_CODE_BRIDGE_SESSION_ID` 有值,即会话连着 Remote Control);`Notification` 的 `message` 只取哈希,正文不落盘。Claude Code 的 eventId 在通用字段之外纳入事件名、`promptId`、通知类型、`error` 与 message 哈希,让同一提示里的多次 Stop、多次授权各有自己的 ID,「谁是最终结束」交给投递端合并;`stopHookActive`、`backgroundBusy`、`bridged` 只随事件落盘,不参与 eventId。OpenCode 2.x 带上游全局唯一的事件 id 时,eventId 只由来源、该 id 与种类决定;Codex 有 `turn_id` 时只由来源、会话、回合、`cwd` 与种类决定。

### 投递(Worker)

`NotificationWorker` 启动后立即扫一轮,之后每 15 秒一轮;调度全部在 `CompletionNotifier`,实际发送在 `LarkCliNotificationChannel`:

- **直接启动 `lark-cli.exe`,绝不经 `cmd.exe`**:先找 `%APPDATA%\npm`(npm 全局包的官方位置),再按 PATH 逐个目录找;每个目录先认包内的 `node_modules\@larksuite\cli\bin\lark-cli.exe`,再认独立的 `lark-cli.exe`;PATH 里的相对路径条目一律跳过(会按 Worker 当前目录解析,而子进程环境里有机器人 App Secret);找不到即通道级 `launch_failed`。经 `cmd.exe /c` 调 npm 垫片会在换行处截断多行 `--text`,`--idempotency-key` 因此从未送达(2026-10-30 实证)。命令为 `lark-cli im +messages-send --as bot --user-id <open_id> --text <正文> --idempotency-key <eventId> --format json`;eventId 是 16 位十六进制,直接作为飞书幂等键(服务端一小时内去重)。子进程设 `LARKSUITE_CLI_NO_UPDATE_NOTIFIER=1` 与 `LARKSUITE_CLI_NO_SKILLS_NOTIFIER=1`,stdout/stderr 只剩信封。
- **凭据**:Worker 以 S4U 计划任务跑在会话 0,lark-cli 每次 `--as bot` 都要用 CurrentUser DPAPI 解自己钥匙串里的 appSecret,在会话 0 间歇失败(2026-10-14 以来占投递失败的 96%)。所以 GUI 在交互会话里把机器人凭据另存一份 **LocalMachine** DPAPI 副本 `state\secrets\feishu-bot.bin`,Worker 读出后只经官方环境变量凭据提供者交给 lark-cli:`LARKSUITE_CLI_APP_ID`、`LARKSUITE_CLI_APP_SECRET`、`LARKSUITE_CLI_BRAND=feishu`、`LARKSUITE_CLI_STRICT_MODE=bot`(它优先于钥匙串,本机 dry-run 证实)。没有副本时退回 lark-cli 自己的钥匙串,日志与界面如实标出 `credentialSource`(`env` / `keychain`)。
- **结果解析**:同时解析 stdout 与 stderr 两种信封(`code`、`log_id`、`retryable`、`retry_after`、`message_id`),由 `NotifyClassifier` 按 lark-cli 1.0.81 的错误契约分类:`confirmation_required`(exit 10,绝不自动 `--yes`)、`keychain_unavailable`、`rate_limit`、`network` / `network_timeout`、`auth_config`、`permission_or_recipient`,以及找不到或启动不了 lark-cli 的 `launch_failed`,属于通道级故障;`invalid_message`(参数/策略类)是条目级永久失败;其余未知错误(`unknown`)与投递异常是条目级可重试失败。退出码 0 但 stdout 不是单个信封时按已送达处理(`sent_unverified`):lark-cli 用 0 表示发送成功,当失败重试会在幂等窗口之外真的重复送达。超时与传输中断结果不明,重试沿用同一个幂等键,首次不明 55 分钟后不再重发(`outcome_unknown` 进死信)。
- **调度**:按事件 `atUtc` 从旧到新处理;7 天内已送达的直接删除;来源不在通知意图里的直接丢弃(`Suppressed`;意图三态:`notifyIntentRecorded=false` 的旧空配置不过滤,记录过的意图哪怕为空也只放行列表里的来源,`ai-resume` 总是放行);发生超过 24 小时仍未送出的不再补发、进死信(`stale`);同一 Claude Code 会话 2 分钟内更新的完成替代旧的(连续 Stop 只报最后一次),`backgroundBusy` 的完成被同会话任何更晚的完成作废(队列里的,或 `completion-notify-sessions.json` 记着已送出的);Claude 完成类事件有 20 秒稳定期,后台任务未完时最多暂缓 30 分钟;没设收件人时整轮记 `recipient_missing`、保留文件。两次发送之间留 300 毫秒间隔(飞书对同一用户 5 QPS)。会话没落盘的 Claude Code 事件(稳定期后转录文件仍不存在,如第三方工具以 `--no-session-persistence` 拉起的 claude)不发(`session_not_persisted`,判断不了照发);用量上限中断(`StopFailure` rate_limit 与 `quota_auto_resume_disabled`)按转录里服务端给的 `rateLimitType + resetsAt`(加配置根指纹)定限额周期(读不到按会话、5 小时),每个会话每个周期只提醒一次(`same_limit_cycle`),同一周期同时待发的会话合成一条;入队不满 20 秒先不判,事件晚于转录里的重置时间时那份证据作废;外部事实经 `INotifyFacts`(生产 `NotifyFacts`)只读获取,读不出一律按「不知道」。详见 [COMPLETION-NOTIFICATIONS.md](COMPLETION-NOTIFICATIONS.md) §5.3。
- **退避与熔断**:条目级失败只推迟这一条,间隔 30 秒 × 2^(n-1)、上限 15 分钟、±20% 抖动;首次尝试超过 24 小时仍未送达的移入 `completion-events\dead\`。通道级故障熔断整个通道:`keychain_unavailable` 60 秒起翻倍、上限 10 分钟,`rate_limit` 按 `retry_after`(缺省 2^n 秒、上限 60 秒),网络类 30 秒起翻倍、上限 5 分钟,凭据/权限/确认/启动/契约类 30 分钟;机器人凭据文件的写入时间、收件人或 lark-cli 位置一变(熔断指纹 `NotifyFingerprint` 变化)立即解除。条目级永久失败直接移入 `dead\`;30 分钟内另一条不同事件命中同类永久失败(跨轮记账,存在健康文件里),升级为通道级 `cli_contract`,防止 CLI 升级把全部通知送进死信。
- **状态文件**:`completion-notify-seen.json`(7 天去重)、`completion-notify-state.json`(逐条退避,跨重启保留)、`completion-notify-sessions.json`(每个 Claude Code 会话最近一次已送达的完成时间,保留 2 天)、`completion-notify-limits.json`(已提醒过的限额周期)、`completion-notify-health.json`(`state`(healthy / blocked / unknown)、`reason`、`blockedSinceUtc` / `blockedUntilUtc`、`consecutiveChannelFailures`、`blockFingerprint`、`lastSuccessUtc`、`lastAttemptUtc`、`lastAttemptOk`(最近一次真实尝试成没成,条目级失败也置 false)、`successFingerprint`(最近一次成功时的设置指纹)、`permanentClass` / `permanentSinceUtc` / `permanentEventId`(跨轮永久失败记账)、`lastErrorClass` / `lastErrorCode` / `lastLogId`、`backlog`、`dead`、`credentialSource`、`updatedAtUtc`)。熔断到期但这轮没有可尝试的条目时保持 blocked,直到下一次真实尝试给出结论。一轮开头就把事件读进内存,发送每一条之前再确认事件文件还在,并在发送期间以允许读、不允许删的方式占住它:撤回(测试通知)与发送由此互斥,文件已不在的记 `Suppressed`(`withdrawn`)不再发。送达一条就落盘一次已送达表,积压冲刷持续几分钟时 `notify test` / 「发送测试」也能及时看到这一条已送达。日志只记类别、错误码、log_id、退出码、耗时与凭据来源,不记正文、收件人或凭据。
- **文案**(`CompletionNotifier.BuildText` / `BuildLimitText`):第一行说发生了什么,第二行「来源 · 模型短名 · 时间 · 「会话名」」(会话名只对 Claude Code 取、只进消息不进日志;Remote Control 已连接时加「· 远程已连接」;模型短名如 `claude-opus-5-5` → `Opus 5.5`),需要人动手时第三行写怎么做;桌面端临时工作区用会话名代替目录名。完成、等你、中断、续跑/登录提醒与测试通知各有固定的首行符号与说法:等你写明在等什么(`quota_auto_resume_stale` 为「额度已恢复,按回车即可继续」),认不出类型就说「等你决定」,绝不编造更具体的说法;中断按 `StopFailure` 的 `error` 区分登录失效(提示 `/login`)、账单、账号受限与服务端错误。用量上限中断四行:谁被打断、来源行、哪个额度用完与预计重置时间(读不到写未核实)、续跑句——只按投递那一刻的布防快照说(未布防 / 本轮布防已停止 / 要重新布防 / 读不到 Claude 登录 / 会续跑它 / 一次性布防这一轮已处理过 / 布防的是同项目另一个会话 / 不在布防范围 / 未核实),`quota_auto_resume_disabled` 补「Claude Code 也不会自己继续」;没有服务端额度窗口时只说「被限额打断」。

收件人 `open_id` 只读明文 `ProductConfig.notifyRecipientOpenId`:它不是机密,而是 lark-cli 所登录应用下的用户标识(按应用隔离),由「飞书通知设置」识别或填写。v2.1 对旧配置回退解 DPAPI 授权名单的路径已删除;收件人也不再从 `allow_from` 同步(该名单已随 cc-connect 退役)。

### 控制面与命令

`NotifyControlPlane` 由 GUI 与 `notify check|test` 共用:

- `Describe`:lark-cli 是否找得到、凭据来源、遮蔽后的 app_id(`cli_ab…ef` 形式)、是否有可导入的旧凭据、收件人是否有效与遮蔽值、投递健康文件,以及 `settingsVerified`(健康文件的 `successFingerprint` 是否等于当前设置指纹;不等 = 最近一次成功之后改过凭据、收件人或 lark-cli,那次成功不再作数)。
- `CheckAsync`:用投递实际会用的凭据来源跑 `lark-cli doctor`(在线,Risk: read),核对机器人身份能否换到 token、端点是否可达。
- `SetCredentialAsync`:先用这组凭据跑 doctor,飞书确实换得到 bot token 才保存;`ImportLegacyAsync`:读 v2.1「飞书接入」的 CurrentUser 密文 `feishu-platform.bin`(只能在交互会话里解开),同样先核验再写机器范围副本,旧文件原样保留由用户决定是否清除;`ClearCredential`:删除副本。
- `SetRecipient`:去掉全部空白(含不换行空格与零宽字符)后必须是 `ou_` 开头的字母数字;`DetectRecipientsAsync`:`lark-cli auth list --json` 列出已登录的用户;保存过机器人凭据副本时只保留与它同一应用的用户(比对在进程内做,不回传 app_id 实值),避免跨应用错配。
- `EnqueueTest`:往队列放一条测试通知,走完「队列 → Worker → lark-cli → 飞书」全链路,而不是在界面进程里直接发。GUI「发送测试」与 `notify test` 共用 `TestOutcome`,只看**这一条**事件自己的结局(`delivered` / `dead` / `failed` / `blocked` / `pending`,`failed` 的原因由 `TestFailure` 给出);失败、熔断或 90 秒超时会 `WithdrawTest` 撤回这一条,撤不回时再看一眼结局如实说明。

凭据实值只出现在用户输入那一刻、`FeishuBotCredentials` 的调用栈内与 lark-cli 子进程的环境变量里,绝不进日志、RPC 应答或 argv。

### 界面语义

- 通知源行:钩子命令指向的可执行文件不存在时标 `hookBroken` 显红(`HookHealth`);OpenCode 的「已加载」以它自己的日志为准,没有记录就是「待核实」。
- 飞书机器人行只认真实投递:按顺序判定——lark-cli 未安装 / 未设收件人 / 后台服务未运行(红)→ 通道熔断(红,短句说明原因,悬停给错误码与 log_id)→ 后台服务在不在还没读到(「核验中…」)或核验不出(「未核实」)→ 最近一次尝试失败(琥珀「最近投递失败」)→ 成功之后改过设置(琥珀「设置已变更」)或比不出来(琥珀「待核实」)→ 健康文件有成功记录且 `settingsVerified === true`(「可送达」)→ 没存机器范围副本、只能用 lark-cli 钥匙串(琥珀「未存凭据副本」,建议保存一份)→ 还没有真实投递记录(「待核实」)。
- `notify list` 的「可送达」列只说明钩子命令跑得起来;通道是否真的送达看 `notify check` 与健康文件。

完整的来源契约、准入规则、安装生命周期、队列语义与冒烟流程见 [COMPLETION-NOTIFICATIONS.md](COMPLETION-NOTIFICATIONS.md)。待真机验证:OpenCode 2.2.3 里真实加载新插件并在任务完成时发出通知;用户在「飞书通知设置」保存或导入一次机器人凭据后,S4U Worker 经机器范围副本真实送达;登录到期提醒在真实到期窗口内发出。

## 进程与存储安全

- 产品配置写入使用独占锁,在锁内重读最新文档、只改自己负责的字段并原子替换文件。
- SQLite 使用 WAL、外键与忙等待超时;迁移单调且幂等。
- 自有进程由 PID、创建时间、命令签名与所有权元数据共同识别,仅凭 PID 绝不授权终止。
- 进程登记失败时阻止准入或请求终止;运行键保留到真实的 close/error。
- Claude 凭据只读、绝不刷新或写回;访问令牌只在调用栈内流动,不进日志、异常、RPC 应答或截图。`claude.login.status` 只回结论与到期时间,不含令牌、邮箱或账号 UUID。
- 飞书机器人凭据以 DPAPI LocalMachine 范围加密,文件从创建起 ACL 就只留当前用户 / SYSTEM / Administrators(临时文件核验后再替换);写入前必须经 `lark-cli doctor` 在线核验;`lark-cli doctor` 的说明文字会带出完整 app_id,界面与 `notify check` 显示前把其中的 app_id 与 open_id 遮蔽;Worker 只经子进程环境变量交给 lark-cli,`LarkCliInvoker` 把 app_secret 加入脱敏集合。app_secret 从不出现在任何 RPC 应答里;`notify.channel` 只回遮蔽后的 app_id 与收件人,`notify.recipient.detect` 回完整 open_id 供用户选择(它不是机密)。
- 任何 Worker 路径都不得依赖 CurrentUser 密文——包括第三方 CLI 自己的:S4U 会话 0 下解 CurrentUser DPAPI 会间歇失败数小时(0x8009000B,2026-10 两次实证)。
- 收件人 `open_id` 以明文存在配置里,它是应用内用户标识,不是机密。
- 日志与面向用户的错误一律经过脱敏。凭据不进仓库、不进命令行;续跑子进程的凭据只经环境变量传递。
- 历史:飞书 `allow_from` / `admin_from` 曾是 cc-connect 聊天入口的安全边界(空名单在生成配置时被拒绝),已随 cc-connect 于 2026-10-30 退役;AI Resume 现在不接受任何飞书入站消息。

## 构建、安装与验证

```powershell
dotnet build csharp\AiResume.sln -c Release -warnaserror
dotnet test csharp\AiResume.sln
csharp\src\AiResume.Worker\bin\Release\net10.0-windows\AiResume.Worker.exe install
```

`install` 把 Gui / Worker / Hook / Launcher 的产物合并暂存到 `%LOCALAPPDATA%\AI Resume\` 旁边并校验,写入 payload 清单,备份将被替换或退役的文件,冻结逐文件 SHA-256,只复制这份快照并逐项复核安装后的字节,再删除上一份清单里已过时的文件,然后才重建快捷方式/自启入口并按通知意图对账完成通知钩子。同一规范化目标目录的安装事务由目标哈希命名的互斥体跨进程串行化。新 Worker 经分离的 shell 路径启动,不会继承安装器被重定向的 stdout/stderr 句柄;它必须存活,并以刚启动的精确 PID 应答当前 Named Pipe 协议。失败时恢复被替换与退役的文件,回滚不完整时保留恢复目录。对账按 `NotifyIntent.Targets`(意图 ∪ 现状 ∩ 本机已装)对每个目标源原位重新启用:旧的 Claude Code shell form 钩子在这里被认领并改写为 exec form,旧的 OpenCode 单文件插件在这里迁移为目录插件。这次探测失败(`Unverified`,开没开不知道)的源不在目标里、不改写它读不出的配置,意图原样保留,并按「未对齐」如实返回;`uninstall` 记录现状时同样保留这类源的原意图。激活成功后删除 v2.1 遗留的 Claude 长期令牌文件(见「持久化位置」)。**通知源未能对齐返回 2,Worker 未就绪返回 3,回滚不完整返回 4,均不得当成功处理。** 已打开的 GUI 不会热更新,部署后关掉重开。`--screenshot <png>` 一律使用进程内合成数据桥与带缓存参数的本地页面 URL(`--screenshot-unarmed` 截未布防态),所以公开截图不会暴露真实项目、凭据、用户名或本机路径;公开截图前另把 `AIRESUME_SHADOW_DIR` 指向临时目录。

<a id="logon-autostart"></a>

### 登录自启

续跑引擎必须在注销重登后继续运行,所以 `install` 会为它创建登录自启入口。Worker 是控制台程序,也必须保持是——`install`、`uninstall`、`notify` 都经 stdout 汇报——但从启动文件夹 `.lnk` 拉起的控制台程序会得到一个控制台窗口,而 `.lnk` 没有隐藏窗口样式(只有正常/最小化/最大化)。那个窗口不只是难看:关掉它就终止了引擎,2026-08-13 曾因此让三个完成事件滞留了两个半小时。

因此启动文件夹快捷方式指向 `AiResume.Launcher.exe`:一个以 `CreateNoWindow` 启动 Worker 后立即退出的 WinExe 垫片。同一安装目录已有实例在跑时它不再起第二个;登录时的失败追加到 `state\logs\launcher.log`,否则登录失败完全不可见。

计划任务更好——S4U 让进程离开交互桌面,并提供快捷方式做不到的失败重启——但注册它需要提权(非提权注册对根目录/子文件夹 × S4U/Interactive 的每种组合都返回 `0x80070005`),而 `install` 不提权。`scripts/register-autostart.ps1` 在提权的 shell 里完成这次升级:注册任务、读回并核验 11 项契约属性后才报告成功,再删除启动文件夹快捷方式。它的 `-Revert` 路径在注销任务之前先确认替代入口存在,创建快捷方式前确认任务真的已删除,之后再确认快捷方式存在,所以任何顺序都不会让机器上两个入口都没有或同时都有。

`install` 读取 `%WINDIR%\System32\Tasks\AI Resume 续跑引擎` 的任务定义(非提权可读)来判断哪条路径在生效。仅仅存在还不够:早先安装遗留的任务,或指向另一个 `--target` 的任务,否则会压掉唯一的自启入口而 `install` 仍返回 0。定义 XML 被解析,只有任务未禁用、且至少一个 `Exec/Command` 解析为本安装目录的 Worker 时才算接管了自启;任何无法解析、已禁用或不匹配的情况都视为未接管,于是重新创建快捷方式。任务确实接管自启时,`install` 还会删除残留的启动文件夹快捷方式——只拒绝创建是不够的,上一次安装可能已经留下一个。

S4U 有两个承重的后果。第一,任务里的 Worker 跑在会话 0,而 `install` 在交互会话里非提权运行,读不到那个进程的 `MainModule`;「只停目标目录下的进程」规则因此会静默跳过它,让每个 payload DLL 保持锁定、安装失败并落入不完整回滚。`install` 现在先用 `schtasks /End` 结束任务实例(任务所有者无需提权即可),再等待 Worker 可执行文件能被独占打开,因为 `/End` 只发信号、在进程退出前就返回。第二,单实例互斥体使用 `Global\` 命名空间并回退 `Local\`:会话范围的互斥体跨不过会话 0 / 交互会话的边界,否则两个 ResumeEngine 可能同时对一个 SQLite 数据库运行。

S4U 下的 DPAPI 是一部演变史。2026-08-14 实测会话 0 的 Worker 解开 CurrentUser 范围的飞书凭据并送达(`outcome=Sent`),但这个单一样本没有站住:2026-10 同一路径在会话 0 间歇失败数小时(`CryptUnprotectData`)。v2.1 因此把收件人改为明文、让 Worker 不再解 AI Resume 自己的 CurrentUser 密文。2026-10-30 的体检又发现,故障点只是移到了 lark-cli:它每次 `--as bot` 都在会话 0 解自己钥匙串里的 CurrentUser 密文,占投递失败的 96%。v2.2 由此改为 GUI 保存机器范围副本 `feishu-bot.bin`、Worker 经环境变量交给 lark-cli(见「完成通知」)。S4U Worker 经该副本的真实送达仍**待真机验证**(需要用户在「飞书通知设置」保存或导入一次机器人凭据)。

C# 测试套件当前共 1250 项 xUnit 用例(2026-11-02,以当次 `dotnet test` 输出为准)。它们使用隔离的临时状态(只经 `TestTemp` 申请)、合成会话与注入的进程/API 运行器;不会对真实项目启动 AI 修改运行,不发付费 API 请求,不碰 `~/.claude`、`~/.codex`、`~/.qoder`、`~/.config/opencode` 与真实状态目录;mock 照抄录制下来的真实返回结构。`GuiPageContractTests` 与 `OpenCodeNotificationAdapterTests` 会启动 PATH 上的 `node` 驱动页面视图模型与插件脚本,运行环境需要装有 Node.js。

相关的重点测试:

- `ResumeSchedulerTests`:逐项目调度——按各自会话的中断判定待续跑、按各自模型取额度、占用只挡自己、未启动不锁周期、最终门禁发现已在别处续上就不再 spawn;
- `ProjectResumeFactsTests`、`ClaudeSessionEvidenceTests`:会话占用(procStart 防 PID 复用、同目录 busy)、会话文件定位、会话尾部证据(按真实录制形状)、逐项目模型选择、一次取证服务多个模型、续跑提醒去重;
- `QuotaResumeProbeTests`、`ScopedLimitTests`、`ScopeModelTests`、`ClaudeModelFamiliesTests`:按模型族的额度门禁与 scoped 窗口身份;
- `ClaudeCredentialSourceTests`、`ClaudeOAuthUsageProbeTests`、`QuotaServiceTests`、`QuotaSnapshotStoreTests`:凭据只来自 Claude Code 登录、账号指纹来自 `oauthAccount`、稀疏观测合并与承接;
- `ResumeEngineTests`、`CheckerCycleTests`、`ClaudeResumeRunnerTests`、`ProviderBoundResumeTests`、`ProviderExecutionTests`:状态机、门禁与执行计划;
- `PowerLossRecoveryTests`、`ProcessVerifierTests`、`ReconcilerTests`:失败关闭的进程恢复;
- `CodexAuthProbeTests`、`CodexOfficialProviderTests`、`CodexBalanceProbeTests`、`DeepSeekProbeTests`、`OpenCodeCredentialsTests`、`ControlPlaneBridgeProviderTests`:官方/第三方 Codex 路由、录制的 ChatGPT usage 解析与准入、第三方余额解析、DeepSeek 只读 OpenCode 密钥、GUI 证据分离;
- `NotificationDeliveryTests`、`CompletionNotifierTests`、`NotificationWorkerTests`、`LarkCliInvokerTests`、`NotifyControlPlaneTests`、`NotifyRecipientTests`:错误分类、通道熔断、单条退避与死信、稳定期与同会话合并、机器范围凭据、lark-cli doctor 与收件人识别的录制形状;`LimitNotificationTests` / `NotifyFactsTests`:没落盘的会话不发、限额周期合并与记账、续跑句按布防快照的各分支、会话名与临时工作区、`NotifyFacts` 按 2.1.309 录制形状读转录与进程登记;
- `ClaudeCodeNotificationAdapterTests`、`OpenCodeNotificationAdapterTests`、`CodexHooksNotificationAdapterTests`、`HookHandlerTests`、`NotificationHookProcessTests`、`HookHealthTests`、`NotifyIntentTests`:钩子合并、所有权与迁移、事件准入、断链报告;
- `GuiPageContractTests`、`ControlPlaneBridgeArmTests`、`ControlPlaneBridgeDemoTests`:页面静态纪律、视图模型语义与布防/演示应答;
- `InstallUninstallTests`、`InstallTargetSafetyTests`、`InstallWorkerLauncherTests`、`WorkerAutostartTests`、`WorkerStartupContractTests`:安装事务、目标安全、自启与宿主启动契约。

历史:`CcConnect*`、`Cutover*`、`FeishuCredentialVerifierTests`、`SingleConsumerGuardTests`、`CimRunningProcessListerTests`、`ClaudeUsageBlocksTests` 与旧的四份 `Gui*ContractTests` 已于 2026-10-30 随对应代码删除。

## 文档真身

- `README.md` / `README.zh-CN.md`:安装与使用。
- `CLAUDE.md`:仓库规则与生产安全不变量。
- `AI_GUIDE.md`:供只读 AI 问答用的紧凑现役导览。
- `docs/CLAUDE-QUOTA-ACQUISITION.md`:现役 Claude 额度协议、连续性规则与验证手册。
- `docs/COMPLETION-NOTIFICATIONS.md`:完成通知协议、所有权规则与冒烟流程。
- `docs/adr/0006-retire-cc-connect-per-project-resume.md`:cc-connect 退役、逐项目续跑、Claude 凭据来源与通知送达修复(v2.2)。
- `docs/adr/0004-provider-bound-resume.md`:供应商绑定续跑(由 ADR-0006 修订模型与 scoped 规则);`docs/adr/0005-owned-credentials-official-codex-and-hooks.md`:Codex 官方账号与 Codex hooks(A1 已被 ADR-0006 取代)。
- `docs/adr/0002-run-lifecycle-contract.md` 与 `docs/RUN-CONTRACT.md`:AI Resume 自有运行的语义。
- `docs/adr/0003-cc-connect-direct-and-control-plane.md`:cc-connect 相关部分只作历史记录(已被 ADR-0006 取代)。
- `docs/LESSONS.md`:仍约束工程决策的历史失败。
- `docs/UPSTREAM-ARCHITECTURE-RESEARCH.md`:固定上游研究快照(其中 cc-connect 部分为历史)。
- `docs/history/`:2026-08 迁移期的计划、门禁与审计,以及已退役的 `FEISHU-BOT-GUIDE.md`。只作历史。
