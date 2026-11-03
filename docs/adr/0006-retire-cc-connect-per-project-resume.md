# ADR-0006:cc-connect 退役、逐项目续跑与完成通知送达修复(v2.2)

状态:已实现(2026-10-30);离线回归与对抗审查结果、真机验证结论见 §6。本 ADR 取代 ADR-0003 中 cc-connect 相关的全部现役部分、
ADR-0005 的 A1(setup-token)与 D1 中「app_secret 只在 GUI 侧 / Worker 不依赖 DPAPI」的投递设计,并修订 ADR-0004 的「全队列共用一个模型」。
ADR-0002(RunContract)、ADR-0004 的其余约束、ADR-0005 的 B1(Codex 官方账号)与 C1(Codex hooks)继续有效。

## 1. 背景:2026-10-30 体检

用户的诉求(原话要点):通知只保留 Claude Code 与 OpenCode;微信不要了,飞书只保留通知——手机上已经用 Claude Code 官方 App 远程;
评估 cc-connect 还剩什么价值;要菜单栏、隐藏次要选项;DeepSeek 已改用公司 key(在 OpenCode 里),软件里还挂着个人的;界面重新排版、审查全部页面;
**多项目续跑时,项目 A 用模型 A,项目 B 也用模型 A(或各用各的)怎么办**;按推荐方案实施并同步文档与 GitHub。

先按第一性原理核实现状(全部来自本机只读检查与代码审查,未打印任何凭据正文):

| # | 现象 | 根因证据 |
|---|---|---|
| 1 | 布防后一直不能自动续跑 | (a) Claude 订阅账号指纹由访问令牌派生,令牌约每 8 小时轮换一次,同一账号被判成「供应商已改变」,周期永久停在 provider-changed;生产库里同一账号已有 28 个指纹。(b) scoped 门禁要求**目标模型**有正证据,且整桶聚合:Fable 周额度用尽连带挡住 Opus 项目。(c) 占用检查遍历全部 Selected,任一会话开着就整轮不续;启动前失败(令牌将过期、会话刚被打开)也会锁死整个周期;notready 日志不带原因,事后无法定位 |
| 2 | 完成通知时灵时不灵,10-14 以来大量失败 | Worker 跑在 S4U 计划任务的会话 0,lark-cli 1.0.81 每次 `--as bot` 都用 **CurrentUser** DPAPI 解自己钥匙串里的 appSecret,会话 0 间歇失败(0x8009000B),占失败的 96%——10-12 修掉的是 AI Resume 自己的 DPAPI,故障点只是移到了 lark-cli。另有三处放大:经 `cmd.exe /c` 调 npm 垫片,多行 `--text` 在换行处截断,`--idempotency-key` 从未送达、消息只到首行;错误信封写在 **stderr**,封装只解析 stdout(测试 mock 形状也猜错了);退避是全局的,一条失败拖慢所有新通知,也没有死信 |
| 3 | OpenCode 通知自 10-29 起完全失效 | OpenCode 从 1.19.5 升级到 Desktop 2.2.3,2.x 不再加载旧的单文件插件(日志 `failed to load plugin`),而界面仍显示「已启用」——这句话无法被外部证伪 |
| 4 | Claude 的用量上限中断、登录失效从不通知 | 只订阅了 `Stop`;回合因 API 错误结束时 Claude Code 发的是 `StopFailure`(2.1.78+),Stop 不触发。`Notification` matcher 缺 `quota_auto_resume_stale/disabled`;钩子写成 shell 字符串,Claude Code 回退 PowerShell 时 ParserError(本机复现) |
| 5 | setup-token 路线读不到额度 | usage 端点不接受 `claude setup-token` 签发的令牌;机器范围密文却仍留在磁盘 |
| 6 | cc-connect 几乎无人使用 | 10 月飞书入站 2 条;cron 在 34 次启动中一直为 0;微信 1 条消息、上万行错误;计划任务已被用户停用;官方 Remote Control 已覆盖手机聊天驱动 Claude Code 的价值;继续维护要钉死 v1.4.1 并跟随其配置格式与进程形态 |
| 7 | DeepSeek 行用的是个人 key | AI Resume 自持了一份 DeepSeek key;用户实际在 OpenCode 里用公司 key |
| 8 | 界面问题 | 右栏 6 个职责挤在 276px 一列;150% 缩放下字号 16px 而栏宽按 px,凭据行和到期时间被截断;`[hidden]` 被作者样式覆盖;CRT 含义随供应商下拉改变;续跑模型是全局单选,无法表达用户第 5 点 |

## 2. 决策(用户 2026-10-30 授权「我没提到的按照你的推荐来」)

### A. cc-connect 整体退役(方案 A)

- 删除 `csharp/src/AiResume.Wrapper/` 整个项目(配置生成/校验、daemon 控制、provider 目录、单消费者预检、进程枚举),
  Worker 命令 `cc-connect-apply`、`feishu-check`、`preflight`,`FeishuCredentialStore`/`FeishuCredentialVerifier`、`Core/CcConnectAgents.cs`、
  `ProductConfig.CcConnectAgent`,GUI 的 `feishu.*`、`cutover.*`、`agent.*` RPC 与关窗守卫,以及对应的 14 个测试文件。
- AI Resume 不再生成、激活、重启或检查 cc-connect;本机 cc-connect 的计划任务、npm 包与 `~/.cc-connect`(内含多份明文密钥)由用户自行清理与吊销,
  AI Resume 不删。需要用手机驱动 OpenCode/Codex 时可以自己运行 cc-connect,但它不再是本产品的组件。
- 飞书只剩一种用途:**完成通知**,经官方 lark-cli 以机器人身份私信;没有飞书聊天、会话、授权名单(`allow_from`/`admin_from`)。
- 手机远程交给 Claude Code 官方 Remote Control。它在本机运行,hooks 照常触发;事件带 `bridged`(Remote Control 已连接)时文案加「· 远程已连接」。
- 否决:B 冻结隐藏(代码与维护成本全留,误点「生成并重启」会重新拉起微信平台);C 只留只读诊断(仍要跟随上游配置格式,仍读含明文密钥的 TOML)。

### B. Claude 凭据:只读 Claude Code 自己的登录(取代 ADR-0005 A1)

- 删除 setup-token 长期令牌、GUI「Claude · 凭据」与 `claude.token.*` RPC;`install` 激活成功后删除旧 `state\secrets\claude-oauth-token.bin` 与 `.rejected.json`。
- `ClaudeCredentialSource` 只读 `%USERPROFILE%\.claude\.credentials.json`(或 `CLAUDE_CONFIG_DIR`),共享读、**绝不刷新、绝不写回**。
  访问令牌约 8 小时,由正在运行的官方 Claude Code(含 Remote Control 桥,它会提前 5 分钟续期)轮换;登录本身 `refreshTokenExpiresAt` 固定约 30 天,只能 `/login` 续期。
- **账号指纹**改读 Claude Code 全局配置里 `oauthAccount` 的组织 UUID + 账号 UUID(与 `claude auth status` 的 orgId 同源)取不可逆哈希:
  `<配置根>\.config.json` 非空时优先,否则 `~/.claude.json`(或 `CLAUDE_CONFIG_DIR\.claude.json`);默认构造还交叉核对 `<配置根>\.claude.json`(桌面端内嵌的 Claude Code 写这一份),
  不一致视为未核实。读不到时身份为空、续跑等待,**不**退回令牌指纹。
- 判定 `ClaudeLoginVerdict`:Ok / TokenExpired(等 Claude Code 续期)/ LoginExpiring(≤3 天,照搬 Claude Code 自己提示 `/login` 的阈值)/
  LoginExpired(登录到期,或刷新被拒后 Claude Code 把令牌清空、expiresAt=0)/ LoggedOut(没有登录、凭据文件不可读或损坏、缺到期时间)/
  StoreMoved(`CLAUDE_CODE_FORCE_WINDOWS_CREDMAN=1` 或 `tengu_windows_credman` 灰度把凭据挪进 Windows 凭据管理器)。
- `ClaudeLoginReminder`(Worker 后台服务,每 30 分钟):登录到期前 3 天、2 天、1 天、12 小时内与自然到期各发一次飞书提醒;登录被 Claude Code 清空(到期时间随之消失)时每个本地日提醒一次,直到恢复。
- 续跑子进程仍以 `CLAUDE_CODE_OAUTH_TOKEN` 注入当前访问令牌,但要求剩余寿命 ≥ 45 分钟(`ProviderExecutionPlan.MinimumTokenLifetime`),不足则本次不启动。
- 否决:AI Resume 用 refresh token 自行刷新(refresh token 一次性轮换,任何外部刷新都会把用户登出);用 `claude auth status` 保活(它不刷新,`loggedIn` 也不代表令牌有效);
  继续 setup-token(端点不接受)。

### C. 逐项目续跑(用户第 5 点;修订 ADR-0004)

- `ProjectRef` 增加 `ModelId` / `ModelName` / `ModelSource`(`session` 跟随该会话最近实际用的模型,`user` 手选;空 = 旧配置,沿用 `ResumeTarget.ModelId`)。
  `ResumeTarget.EffectiveModel(project, target)` 给出每个项目的有效模型,`ResumeTarget.Identity` 按 (路径, 会话 UUID, 有效模型) 序列化。
  **项目 A、B 共用模型 A 就是两个项目各自把有效模型设为 A**;共用同一模型的项目共享同一份额度观测、排队串行,每个启动前都重新核验。
- 布防:`arm.set` 带 `projectModels`;`ResumeBindingService.ChooseModel` 逐项目按「手选 > 会话最近模型 > 目标默认」从供应商**实时目录**选模型,
  会话正开着或会话模型不在目录时给 warnings 但不阻止布防。`arm.preview` 只读本地会话事实(不调用绑定、不发网络请求)。
- 每个项目四个维度(`ProjectAdmission`),任何「未知」只导致等待:
  - **义务**:会话转录尾部(最近一条真实应答或限流记录;限流以外的合成错误记录跳过)是否是用量上限中断(rate_limit 记录,含 limitType、resetsAt、中断 uuid);
    中断之后同目录新开的会话(首条记录晚于中断,或文件在中断后才创建——分叉续写)有了真实应答 → superseded(用户已换会话接着干,不再自动续旧会话,
    这次中断随即记为已处理;判断不了只等待);
  - **额度**:按该项目的模型评估,同模型一次观测共享;
  - **占用**:`~/.claude/sessions/<pid>.json`——绑定会话活着 → in-use;同目录另一会话 busy → cwd-busy;读不出 → unverified;
    只有「活着的进程比登记的启动得晚」才算 PID 复用(procStart 不是合理 FILETIME 时退回 startedAt),字段语义一变就判未核实,绝不把开着的会话当空闲;
  - **进程门禁**:全局 `ActiveRunId` / `PendingCancellation`,同一时刻最多一个续跑进程。
- 状态机(`CheckerCycle.OnProbe`):被打断且中断 uuid 未消费 → 待续;被打断后会话里又有新对话 → continued-elsewhere(视为完成);
  **未启动**类失败(会话刚被打开、令牌不足 45 分钟、执行条件未核实、配置刚变化…)只记 `not-started:<原因>`、跳过本轮,**不锁死周期**;
  进程启动后的失败仍按 ADR-0002 阻断自动重放(周期级 ReplayBlocked 保留)。中断 uuid 在 `PrepareActiveRun` 时持久化(`ConsumedInterruptions`),同一次中断最多续一次。
  一次性模式下所有项目都完成(success 或 continued-elsewhere)才解除;连续模式每次被打断都续跑。每个项目启动前**最终门禁**重读会话尾部与占用。
  门禁与在途存续条件只比较**这个项目自己的执行身份**(`ResumeTarget.ProjectIdentity`):别的项目被移除或改模型,不杀正在跑的项目。
  「一跑就被限流」按项目计数(`ProjectLimitedRefires`),这个项目回到等待、本轮跳过它,别的项目照常轮到;同一项目连续 6 次才记 error 并锁存。
  不会自己好的未启动原因(no-claude、execution-unverified)连续 3 次发一次飞书提醒(`resume-not-started`)。
- 与原生能力分工:Claude Code `autoContinueAtUsageLimit`(2.1.234+ 默认开)处理**仍打开**的交互会话;它不覆盖 Remote Control 会话、重置超过 24 小时的周额度、
  已关闭的会话与 `-p`。AI Resume 对开着的会话不重复续跑;额度恢复 10 分钟后会话仍开着且未继续,飞书提醒一次。
- 可观测:`resume.probe.evaluated` 日志逐项目带 `name=model|quota[:detail]|session|evidence|reason`;GUI `arm.get.projects[]` 给出短句与悬停说明。
- 否决:全队列共用一个模型(无法表达「各用各的」);周期级聚合门禁(一项卡住全体等待);未启动即锁周期(夜里一次令牌临近过期就放弃整晚)。

### D. 额度门禁按模型族(修订 ADR-0004 的 scoped 规则)

- 只评估目标模型相关的窗口:5 小时 / 7 天主窗口 + 同模型族的 weekly_scoped(同族任何版本都算相关;认不出族的 scoped 视为相关,保守)。
  其它族的 scoped 用尽不挡本项目。主窗口仍必须是本次实时读数且 0 ≤ % < 100(承接值得到 `five_hour:carried` 一类的 unknown);
  相关 scoped 窗口在同一重置周期内的承接读数未满不再阻塞,无百分比且非承接时才判未核实。`BlockingResetUtc` 取阻塞窗口里最晚的重置时间。

### E. 完成通知

- 通知源注册表仍是 5 个(Claude Code / Codex / Cline / Qoder / OpenCode),意图仍持久化在 `ProductConfig.NotifySources`;GUI 主界面只显示 Claude Code 与 OpenCode,
  其余收进「视图 › 显示其他通知源」,隐藏的源若仍启用或断链,主界面留一句提示。
- **Claude Code**:官方 exec form(`command` 只放 `AiResume.Hook.exe` 绝对路径、`args` 放参数,2.1.139+);`Stop`(完成)、`StopFailure`(`--kind=failure`,中断)、
  `Notification`(`--kind=decision`,matcher = `permission_prompt|worker_permission_prompt|elicitation_dialog|elicitation_url_dialog|agent_needs_input|quota_auto_resume_stale|quota_auto_resume_disabled`)。
  对 Claude Code,`stop_hook_active` 不再作为丢弃条件,改为 Worker 端同会话合并;后台任务未完时暂缓。事件文件补存 hookEvent、promptId、error、stopHookActive、
  backgroundBusy、bridged;eventId 额外纳入 hookEvent、promptId、notificationType、error 与消息哈希(messageKey 只参与计算、不落盘),同一事件重复触发去重、不同事件不冲突。
- **OpenCode**:只支持 2.x。写官方 v2 目录形态插件 `~/.config/opencode/plugins/airesume-notify/index.ts`(`id = "ai-resume.notify"`,`setup(ctx)` 订阅 `ctx.event`);
  完成 = 顶层会话 `session.execution.succeeded` 且无运行中的子会话;等你 = `permission.asked` / `form.created` / `session.execution.failed`。
  「已加载」只以 OpenCode 自己的日志为证据;旧单文件插件迁移时留 `.bak`;拒绝覆盖别人的 `index.*`/`server.*`/`package.json`。
- **投递**:直接启动 npm 包内的 `lark-cli.exe`(绝不经 cmd.exe);机器人凭据由 GUI「飞书通知设置」保存为 **LocalMachine** DPAPI 副本
  `state\secrets\feishu-bot.bin`(从创建起 ACL 只留当前用户 + SYSTEM + Administrators,临时文件核验后再替换;保存前用 `lark-cli doctor` 在线核验),Worker 经
  `LARKSUITE_CLI_APP_ID/APP_SECRET/BRAND/STRICT_MODE` 环境变量交给 lark-cli(官方环境变量凭据提供者优先于钥匙串)〔2026-11-02 更正:env 来源只收现成令牌、不拿 App Secret 去换,这条路对任何密钥都失败;改为 AI Resume 自己换 tenant_access_token 后只注入 App ID + 令牌,保存前的 doctor 核验同步改为真实换令牌 + 机器人接口调用,见 ADR-0007〕;没有副本时退回钥匙串。
  解析 stdout/stderr 两种信封;`NotifyClassifier` 分级——单条失败只推迟这一条,凭据/网络/限流等通道故障熔断整个通道,凭据或收件人一变立即解除;
  参数类永久错误与首次尝试超过 24 小时的条目进 `completion-events\dead\`(30 分钟内跨轮两条不同事件命中同类永久错误时改判为通道级 `cli_contract`);
  发生超过 24 小时仍未送出的事件不补发(`stale`),结果不明的重发在首次不明 55 分钟后停止(飞书 uuid 只去重一小时),退出码 0 的非信封输出按已送达。
  Claude 完成类 20 秒稳定期、同会话 2 分钟内替代,`backgroundBusy` 的完成被同会话任何更晚的完成作废。健康写 `completion-notify-health.json`,界面只认真实投递,
  且最近一次尝试失败或成功之后改过设置时不显示「可送达」。
- 用户关掉的通知源一律不发:关掉 Codex 后,`~/.codex/config.toml` 里残留的旧版 `notify` 链(共享键,按 ADR-0005 只提示不改写)仍会写事件,
  投递端按 `ProductConfig.NotifySources` 丢弃这类事件(`Suppressed` / `source_disabled`);AI Resume 自己的事件总是放行。意图分三态:从未记录过
  (`NotifyIntentRecorded=false` 的旧空配置)不过滤;记录过的意图哪怕为空(通知源全关)也只放行列表里的来源。
- `lark-cli doctor` 的说明文字会原样带出完整 app_id,界面与 `notify check` 一律遮蔽后再显示。
- 续跑与登录事件(source=ai-resume,kind=resume)与钩子事件走同一队列:会话仍开着、开始/完成/失败续跑、令牌不可用、账号变化、登录将到期/已过期。
- 控制面 `NotifyControlPlane`(GUI 与 CLI 共用);CLI 新增 `notify check`(doctor)与 `notify test`(冒烟事件走完整链路并等待真实送达)。
- 否决:Worker 改交互式登录任务(失去 S4U 的会话无关性与崩溃重启);继续经 cmd.exe 并转义(截断发生在 npm 垫片与 cmd 的参数解析,转义治不了);
  只靠 Remote Control 手机推送(只覆盖已连接的 Claude Code 会话,完成推送由模型自行决定,不覆盖 StopFailure 与 OpenCode)。

### F. DeepSeek 只展示

- DeepSeek 行改读 **OpenCode** 的 DeepSeek 密钥(OpenCode 配置 → OpenCode 2.x 凭据库(`OPENCODE_DB`,默认 `opencode.db`)的 `credential` 表(只读;2.x 只在首次启动时导入一次 `auth.json`,之后换密钥、删密钥只改库:库里没有 DeepSeek 就不再看 `auth.json`,库读不了显示未核实)→ 仅当库或表不存在时看 `auth.json`;都没有时才回退 `DEEPSEEK_API_KEY` 环境变量;端点按 OpenCode 2.x 的配置层合并后判断:`OPENCODE_CONFIG_DIR`(否则 XDG)下的 opencode.json/.jsonc → `OPENCODE_CONFIG` → `OPENCODE_CONFIG_CONTENT` → `%ProgramData%\opencode` 托管层),名称「DeepSeek · OpenCode」(回退时为「DeepSeek · 环境变量」),
  `resumable=false`:只展示余额,不参与续跑——拿 OpenCode 的密钥去跑 Claude Code 等于把一个客户端的凭据挪给另一个客户端。AI Resume 不再保存任何 DeepSeek key。

### G. 界面

- 菜单栏(视图 / 工具 / 帮助)收纳次要功能;铭牌状态灯一灯两行;四块含义固定的 CRT(Claude 5 小时 / 7 天 / 按模型 / DeepSeek 余额,可选 Codex 第五块);
  续跑队列每行自带模型与等待原因;右栏只剩「Claude 登录」与「完成通知 → 飞书」;页脚显示后台 Worker 是否在跑。
- 字号仍走 rem 并按 DPR 挪到像素网格;WebView2 关闭浏览器加速键与缩放(Ctrl± 会让点阵字失去整数倍);窄磁贴用容器查询换短标题。
- 视图模型抽成 `<script id="vm">` 的纯函数,由 `GuiPageContractTests` 在 Node 里驱动断言语义(「无证据不绿」「承接琥珀」「隐藏的通知源仍启用必须提示」「Worker 不在时机器人行不得显示可送达」等),不逐字断言 DOM。

### H. 清理与版本

- 删除无调用方的本地 5 小时块 `ClaudeUsageBlocks` 与 `quota.local` RPC;`docs/FEISHU-BOT-GUIDE.md` 与 `tools/cc-connect-hide-window.ps1` 移入 `docs/history/`。
- 版本号 `2.2.0`(`csharp/Directory.Build.props`)。

## 3. 上游盘点(先查已有,再决定自研)

- **Claude Code**(本机 CLI 2.1.300、VS Code 2.1.280、Desktop 2.1.309;最新 2.1.314):hooks 有 `StopFailure`(2.1.78+)与 exec form(2.1.139+),
  `Notification` 类型含 `quota_auto_resume_fired/stale/disabled`,配置文件监视器热加载;`autoContinueAtUsageLimit` 只作用于打开着的交互会话(边界见 §2.C);
  `CLAUDE_CODE_RETRY_WATCHDOG` 可让 `-p` 等待额度重置(未采用,会长时间持有会话,另议);Remote Control 推送默认关闭且只覆盖已连接会话;
  `~/.claude/sessions/<pid>.json` 是官方的运行中会话登记(含 procStart、status、entrypoint);凭据刷新在 `.oauth_refresh.lock` 锁内重读 + CAS 写回,
  被拒时清空令牌;没有零推理成本的官方刷新命令。结论:续跑引擎、确定性通知与逐项目绑定仍需自研,但占用判定、中断证据、登录判定都直接读官方产物。
- **OpenCode Desktop 2.2.3**:v2 插件 API 为 `export default { id, setup(ctx) }`,事件经 `ctx.event.subscribe`,载荷在 `data`;`session.idle` 已无人发布;
  官方自己的「完成/等你」判据与本插件一致;加载结果写 `~/.local/share/opencode/log/opencode.log`——AI Resume 按日志行里我方插件目录路径匹配
  `loading plugin` / `failed to load plugin`,只认插件写入之后的记录。
- **lark-cli 1.0.81**:官方环境变量凭据提供者(`LARKSUITE_CLI_APP_ID/APP_SECRET/BRAND/STRICT_MODE`)优先于钥匙串(本机 dry-run 证实〔2026-11-02 更正:dry-run 不换令牌,证明不了凭据可用;env 来源只收 `LARKSUITE_CLI_TENANT_ACCESS_TOKEN`,见 ADR-0007〕);
  错误信封写 stderr(`error.code`、`log_id`、`retryable`、`retry_after_seconds`);`doctor` 在线核对 bot 身份(假凭据报 `no tat available from credential source env`);
  `auth list --json` 给出同一应用下已登录用户;npm 安装形态是 `lark-cli.cmd` 垫片 + `node_modules/@larksuite/cli/bin/lark-cli.exe`。
- **飞书 OpenAPI**:消息发送的 uuid 幂等去重、频控错误码与 `retry_after` 语义按官方文档;只有 `--idempotency-key` 真正送达后去重才生效。
- **cc-connect 1.4.1**:评估结论见 §2.A;不再跟随其版本。

## 4. 迁移与兼容

- 旧 `config.json` 的 `CcConnectAgent` 字段被忽略;`ProjectRef` 新字段缺省为空 = 沿用 `ResumeTarget.ModelId`。但 v2.1 对 Claude 订阅的布防**不能**沿用:
  它的 `ProviderIdentity` 由当时的令牌指纹派生,与 v2.2 的账号指纹对不上,被打断的项目会显示「账号已变化 · 重新布防」——升级后需要解除并重新布防一次
  (重新布防同时固定逐项目模型)。Codex 官方目标的身份由 AccountId 派生,不受影响。
- `CheckerState` 新增 `ProjectNotes`、`ConsumedInterruptions`,旧状态缺省为空;周期级字段全部保留。
- 旧 `feishu-platform.bin`(v2.1「飞书接入」,CurrentUser DPAPI)只用于在「飞书通知设置」里一键导入并核验,导入后原文件保留由用户决定。
- 旧 OpenCode 单文件插件在启用时迁移为目录插件并留 `.bak`;旧 Claude shell form 钩子由 `install` 对账时认领并改写为 exec form。

## 5. 已知缺口

- 手机驱动 OpenCode/Codex 的能力随 cc-connect 一起放弃;Remote Control 只覆盖 Claude Code。
- Claude 登录(约 30 天)必须由用户 `/login` 续期;AI Resume 只提醒,不代刷。
- 续跑子进程注入的访问令牌不可刷新,超长续跑可能在中途过期(45 分钟门槛只保证启动时充足)。

## 6. 验证边界

- 离线:`dotnet build -c Release -warnaserror` 0 警告;`dotnet test` 全绿(数量以当次输出为准;2026-10-30 首轮 1156,10-31 交叉审查后 1195,11-02 修复轮复审后 1215,同日限额通知规则与用语修正后 1250,飞书凭据副本自己换令牌(ADR-0007)后 1303)。逐项目续跑、送达分级、控制面、OpenCode 插件
  (内嵌 Node harness 21 个事件场景)、GUI 视图模型(Node 驱动)均有对真实录制形状断言的测试。
- 截图:`AiResume.Gui.exe --screenshot`(合成数据)与无头 Edge 渲染,在 1120×760 与 900×600、150% 缩放下核对布局。
- 真机(本轮 install 后):`notify list`、`notify check`、`notify test`,OpenCode 日志中的插件加载记录。
- 真机(需要事件或用户操作):一次真实的用量上限中断后逐项目自动续跑;OpenCode 里真实加载新插件并在任务完成时发出通知;
  在「飞书通知设置」保存或导入机器人凭据后 S4U Worker 的真实送达;登录到期提醒(本机登录 2026-11-11 05:55 UTC 到期)。
