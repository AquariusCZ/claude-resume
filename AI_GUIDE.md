<!-- project-tour · generated 2026-11-02 · v2.2.0 + 限额通知规则与界面用语(两路交叉审查后) -->
# AI Resume — AI 导览(AI_GUIDE.md)

> 一句话:AI Resume 是 Windows 本地控制面:Claude Code 会话被用量上限打断后,按每个项目自己的模型与实时额度证据自动续跑原会话,并把 Claude Code / OpenCode 的完成、中断与「等你」事件经官方 lark-cli 私信到飞书。
> 本文件供 AI **只读问答**优先加载:常见架构、运行、配置和故障问题先查这里;深挖再按文末索引读取正式文档。

## 1. 定位

- **用途**:管理本机 AI 编码 agent 的限额后**逐项目**续跑、项目队列、服务健康和完成通知(飞书私信)。
- **使用者 / 场景**:Windows 上用 Claude Code(订阅)与 OpenCode(DeepSeek 等)写代码,希望长任务被用量上限打断后无人值守接着跑,并在手机上收到完成、中断与「等你」提醒的个人开发者。手机上直接操作会话用 Claude Code 官方 Remote Control(Claude App / claude.ai/code),不经 AI Resume。
- **当前版本**:v2.2.0;版本真身是 `csharp/Directory.Build.props` 的 `<Version>`。
- **现役实现**:`csharp/` 下的 .NET 10 C#(9 个产品项目 + 1 个测试项目)。v1 PowerShell + Node 已于 2026-08-08 从工作树删除,只能通过 Git 历史查阅。
- **技术栈**:.NET 10、C#、WPF + WebView2、SQLite/WAL、Windows Named Pipe、DPAPI、官方 lark-cli(飞书)、Tomlyn(解析与校验 Codex TOML);Node 只在两组测试里用到。
- **产品边界**:AI Resume 只做四件事:① 限额后逐项目续跑编排;② 动态项目发现;③ 本地完成通知(通知源注册表 + 飞书投递);④ Windows 控制面 GUI。会话、聊天与手机远程归各客户端和 Claude Code 官方能力,AI Resume 只读它们公开落盘的产物(会话转录、会话登记、日志)。
- **核心原则**:适配上游而不是重写上游;界面上的肯定句必须有外部可验证证据;任何「未知」只导致等待,绝不放行。
- **v2.2(2026-10-30)要点**(决策与上游证据见 [ADR-0006](docs/adr/0006-retire-cc-connect-per-project-resume.md)):cc-connect 整体退役,飞书只剩完成通知,手机远程改用 Claude Code Remote Control;Claude 凭据只读 Claude Code 自己的登录(setup-token 长期令牌删除),账号指纹改用全局配置里的 `oauthAccount`;续跑逐项目化(每个项目自己的模型,逐项目判定义务/额度/占用,未启动不锁周期),scoped 额度按模型族;完成通知投递修复(直接启动 lark-cli.exe、机器范围凭据副本经环境变量、stderr 信封、分级退避/熔断/死信、`notify check/test`);Claude Code 钩子改 exec form 并新增 `StopFailure`;OpenCode 2.x 官方 v2 插件;DeepSeek 只读 OpenCode 的密钥且只展示;GUI 重排为菜单栏 + 逐项目模型队列。
- **仍有效的 v2.1 决策**([ADR-0005](docs/adr/0005-owned-credentials-official-codex-and-hooks.md)):Codex 固定官方 ChatGPT/OpenAI 账号(B1)、Codex 完成通知走 hooks `Stop`(C1)、通知收件人明文存配置(D1 的这一半)。

## 2. 架构与数据流

```text
Claude Code(终端/VS Code/Desktop,含 Remote Control 已连接的会话)      OpenCode 2.x
   │ ~/.claude/settings.json 钩子(exec form):                       │ 插件 plugins/airesume-notify/index.ts
   │   Stop / StopFailure / Notification                               │   session.execution.* / permission.asked / form.created
   ▼                                                                    ▼
AiResume.Hook.exe ──原子写──▶ state\completion-events\<eventId>.json ◀── 续跑与登录提醒(source=ai-resume)
                                      │   (Codex/Cline/Qoder 通知源也写这里,界面默认隐藏)
                                      ▼ 每 15 秒
AiResume.Worker(常驻:Startup → AiResume.Launcher.exe,或可选 S4U 计划任务「AI Resume 续跑引擎」)
   ├─ NotificationWorker → CompletionNotifier → LarkCliNotificationChannel → lark-cli.exe --as bot ──▶ 飞书私信
   ├─ ResumeEngine(30 秒一拍)→ ProviderResumeProbe ─┬─ OAuth usage / 供应商目录(ProviderCatalogService)
   │                                                  ├─ 会话尾部 ~/.claude/projects/**/<会话 UUID>.jsonl
   │                                                  └─ 会话登记 ~/.claude/sessions/<pid>.json
   │     └─ ClaudeResumeRunner → internal-agent-run(Job 监督)→ claude --resume <UUID> -p … --model <项目模型>
   ├─ ClaudeLoginReminder(30 分钟)   ├─ ObservationWorker   └─ Named Pipe(RunContract;install 就绪核验、GUI ping)
   ▲ 共享状态:config.json(锁内读改写)/ runs.db(product_state、quota_snapshots、process_registry)/ completion-notify-health.json
   │
AiResume.Gui(WPF + WebView2)── postMessage JSON RPC ──▶ ControlPlaneBridge(线程池 I/O)
```

### 2.1 控制面:请求、菜单与 RPC

1. WPF 窗口先显示骨架,不等待项目扫描或探测;WebView2 从虚拟主机 `controlplane.airesume.local` 加载 `wwwroot/index.html?v=<文件时间戳>`(换缓存键)。关闭默认右键菜单、浏览器加速键与缩放(保点阵字整数倍);DevTools 只在 `AI_RESUME_ENABLE_WEBVIEW_DEVTOOLS` 为 `1`/`true` 时开启。
2. 前端 `call(type, payload)` 经 `postMessage` 发 `{id,type,…}`(默认 15 秒超时);`ControlPlaneBridge.HandleAsync` 在线程池执行,回 `{id, type:"<type>.result"|"<type>.error", payload, error}`,错误文本经 `SecretRedactor` 脱敏。
3. 现役 RPC:`projects.list/add/remove/restore`、`dialog.pickFolder`、`app.info`、`quota.get`、`providers.catalog`(零 token)、`arm.get/set/preview`、`claude.login.status`、`notifications.list/setEnabled`、`notify.channel/check/test`、`notify.test.status|withdraw`、`notify.credential.set|import|clear`、`notify.recipient.set|detect`。v2.1 的 `feishu.*`、`cutover.*`、`agent.*`、`claude.token.*` 与 `quota.local` 已于 2026-10-30 删除(`providers.probe` 更早于 2026-10-12 删除)。
4. 菜单栏(Alt+V/T/H 打开,F10 聚焦,F5 刷新):**视图**(显示 Codex(额度栏与续跑供应商)/ 显示其他通知源(Codex / Cline / Qoder)/ 供应商与额度详情… / 不再扫描的目录…)、**工具**(刷新额度与状态 / 重新扫描项目 / 连续模式 / 飞书通知设置… / 发送测试通知)、**帮助**(使用说明 / 关于)。显示 Codex / 显示其他通知源两项偏好只存本机 `localStorage`(`airesume.prefs`);连续模式不是界面偏好而是布防配置(`config.json` 的 `continuous`),菜单勾选只是内存里的待定选择,没动过时跟随 `arm.get`,随下一次 `arm.set` 生效。
5. 主界面:铭牌 + 状态灯(一灯两行,引擎与进程门禁压过一切阶段文案);四块含义固定的额度磁贴(Claude 5 小时 / 7 天 / 按模型(已用最高或已满的一条,角标「N 项在等」)/ DeepSeek 余额;显示 Codex 时第五块);续跑队列(勾选 + 序号 + 项目 + 「续跑模型 / 状态」);右栏「Claude 登录」与「完成通知 → 飞书」(Claude Code、OpenCode 开关 + 飞书机器人行);页脚右侧「后台服务运行中 / 未运行 / 未核实」(`arm.get.workerRunning`)。对话框全部是原生 `<dialog>`(飞书通知设置、续跑预览、供应商与额度详情、不再扫描的目录、关于、使用说明)。
6. 节奏:布防状态与通知通道每 30 秒轮询;额度、目录与登录按 `quotaRefreshMinutes`(默认 15)自动刷新,失败的条目降级为未核实,不保留旧绿。一轮刷新里每块数据到了就各自绘制(磁贴不等最慢的供应商目录);目录在 `quota.get` 之后才读,Claude 行复用这一轮 1 分钟内的额度读数(一轮只请求一次 usage 端点);登录在额度请求结束后再读一次。页面语义集中在 `<script id="vm">` 的纯函数 `AR.*`,由 `GuiPageContractTests` 在 Node 里驱动断言。
7. 项目发现(`projects.list` → `ProjectCatalog.Discover`):读 `~/.claude/projects` 下各会话 jsonl 头部的 `cwd`,并上手动添加的 `customProjects`,排除「不再扫描」名单(`hiddenProjects`)、AI Resume 自己的状态目录、系统临时目录与系统/安装目录;索引缓存在 `state\project-index.json`。

### 2.2 Worker 常驻链路

- `ProcessRecoveryBootstrap`:宿主显式串行启动,先 `ProcessSupervisor.RecoverAsync` 对账遗留 `process_registry`,再开放其它服务。
- `TransportBootstrap`:Named Pipe 服务端(`ping`/`start`/`status`/`cancel`/`list-runs`);`install` 用 pong 里的 PID 核验新 Worker;GUI 只在判定「后台服务运行中」(`arm.get.workerRunning`)时以 300 毫秒超时 ping 一次——同名进程在、管道不应答(CLI 子命令、别的用户的 Worker)就是「未核实」,布防中的状态灯随之降为琥珀「监视状态未核实」(`WorkerUnverified`);没有同名进程是「未运行」,不发 ping。其余协作都经共享配置、数据库与状态文件。
- `ObservationWorker`:观察持久 run 与进程存活。
- `NotificationWorker`:每 15 秒消费 `completion-events\` 并投递(§2.5)。
- `ClaudeLoginReminder`:每 30 分钟检查 Claude Code 登录到期(§2.6)。
- `ResumeEngine`:每 30 秒一拍驱动布防周期(§2.3)。
- `DailyJsonFileLoggerProvider`:`logs\worker-yyyyMMdd.log`(GUI 写 `gui-yyyyMMdd.log`),每行一个脱敏 JSON。
- 自启:默认 Startup 快捷方式「AI Resume 续跑引擎.lnk」→ `AiResume.Launcher.exe` 无窗口拉起 Worker;可选 `scripts/register-autostart.ps1`(需管理员)升级为 S4U 计划任务「AI Resume 续跑引擎」(会话 0、失败自动重启)。

### 2.3 逐项目续跑(ADR-0006 §2.C,修订 ADR-0004「全队列一个模型」)

```text
GUI 勾选项目,每行选模型(默认「沿用会话」)→ arm.set {paths, providerId, resumeModel(默认模型), projectModels{路径: 模型ID|''}, continuous}
   ResumeBindingService.BindAsync:实时目录(BindWithCatalogAsync)→ 每个项目绑定该目录最近修改的原生会话 UUID
   → ChooseModel:手选(必须在目录,否则整次拒绝)> 会话最近实际用的模型 > 目标默认 → 逐项目走一遍执行计划(不 spawn)
   → 写 config.selected[]{sessionId, modelId, modelName, modelSource} + resumeTarget + 新 armCycleId(会话开着/模型不在目录只给 warnings)
ResumeEngine 每拍:进程门禁(PendingCancellation / ActiveRunId)→ 周期初始化 → 节奏(有待续项目 4 分钟,否则 probeIntervalMinutes,默认 15)
   ProviderResumeProbe:先读本地会话尾部,再做一次供应商观测,按每个不同模型各评估一次(同模型项目共享证据)
   → 每个项目一份 ProjectAdmission → CheckerCycle.OnProbe:中断 uuid 未消费 → 待续;尾部已有新的真实应答,或中断后同目录新开的会话接着干了 → continued-elsewhere
     (「新开」= 首条记录晚于中断或文件在中断后才创建;一旦判定这次中断记为已处理,新会话之后消失也不回头续;判断不了只等待)
   → 按 Selected 顺序取第一个「待续且放行」的项目 → beforeStart 最终门禁(尾部仍是同一中断 uuid、同目录没有中断后新开并接着干的会话、占用 idle、周期未变、这个项目自己的执行身份 ProjectIdentity 未变)
   → 一跑就被限流:这个项目与同模型的项目回等待、本轮跳过,别的模型的项目照常轮到(误判按项目计数,同一项目连续 6 次才锁存)
   → PrepareActiveRun 一次落盘 running + ConsumedInterruptions[项目]=uuid + ActiveRunId → spawn → 跑完再取证选下一个(串行)
一次性模式:所有 Selected 都是 success 或 continued-elsewhere 才完成并解除布防(从未被打断的项目让周期保持监视);连续模式每次被打断都续
```

- **预览**:布防栏「预览…」调 `arm.preview`(`ResumeBindingService.Preview`),逐项目只读本地会话事实(绑定哪个会话、会话最近模型、是否被限额打断、是否开在客户端),不调用绑定、不发网络请求、不写任何状态。
- **四个维度**(`Core/ProjectAdmission.cs`;任何一维未核实只导致等待):
  - 义务 `Evidence`:`ClaudeSessionEvidence.TryRead` 只读会话尾部元数据(先 1 MiB、最多 8 MiB)。倒序找最近一条主链**真实应答或限流记录**——限流以外的合成错误记录(`server_error` / `authentication_failed` / `unknown`:原生自动继续撞 529、令牌被拒)跳过,续的那一下失败了不等于续上了。它是 `isApiErrorMessage=true` + `error="rate_limit"`(`message.model="<synthetic>"`,带 `quotaLimits.rateLimitType/resetsAt`)→ `interrupted`(带中断 uuid);否则 `clear`;中断之后同目录**新开**的会话(首条记录晚于中断)有了真实应答 → `superseded`(`ResumeSessionCatalog.ContinuedInAnotherSession`,中断前就开着的并行会话不算);读不出或中断记录缺 uuid → `unverified`;Codex 没有会话证据 → `none`(沿用「观测到它的模型受限」旧语义)。同一扫描还取最近一条真实应答的 `message.model` 作为「会话最近模型」。
  - 额度 `Quota`:按项目有效模型(`ResumeTarget.EffectiveModel`)评估:`ok / limited / unknown / provider-changed / identity-unverified / credentials-unavailable / model-unverified`(规则见 §2.4)。
  - 占用 `Session`:`ResumeSessionCatalog.Occupancy` 读 `~/.claude/sessions/<pid>.json`,进程活着且不能证明是 PID 复用才算(只有「活着的进程比登记的 Claude 进程启动得晚」才算复用;`procStart` 不是合理 FILETIME 时退回 `startedAt` 毫秒时间戳;启动得比登记还早却对不上 → 字段语义变了 → `unverified`,绝不当空闲);绑定会话活着 → `in-use`(带客户端入口);同目录另一会话 `status=busy` → `cwd-busy`;读不出 → `unverified`;Codex → `n/a`。
  - 进程门禁:`ActiveRunId` / `PendingCancellationRunId` 全局,精确进程仍存活或无法核验时禁止新续跑,同一时刻最多一个续跑进程。
  - `Admissible = Quota=="ok" && Session ∈ {idle, n/a} && Evidence ∉ {unverified, superseded}`。
- **未启动不锁周期**:spawn 之前的失败(会话刚被打开、访问令牌剩余 < 45 分钟(`ProviderExecutionPlan.MinimumTokenLifetime`)、执行条件未核实、配置刚变化、尾部有新记录…)只记 `ProjectNotes[项目].Reason = not-started:<原因>`,跳过该项目,下一拍重评。**进入进程登记/启动之后**的失败(`launch-error`、`registry-error`、`monitor-error`、`exit-null`、`limited-side-effects`…,可能已有副作用或结果未知)仍按 ADR-0002 阻断自动重放:周期级 `ReplayBlocked`,需解除后重新布防。续跑中再次被限流且没有副作用 → 这个项目回到等待、本轮跳过它继续别的项目;「额度说可用、一跑就限流」按项目计数(`CheckerState.ProjectLimitedRefires`,该项目成功一次清零),同一项目连续 6 次 → `blocked:repeated-limit`。不会自己好的未启动原因(`no-claude`、`execution-unverified`)同一项目连续 3 次 → 飞书提醒一次(`resume-not-started`)。在途运行的存续条件只看这个项目自己的执行身份(`ResumeTarget.ProjectIdentity`),别的项目被移除或改模型不会杀掉它。
- **同一次中断最多续一次**:中断 uuid 在 `PrepareActiveRun` 时与 running 一起写入 `ConsumedInterruptions`,Worker 崩溃重启也不会重复;spawn 前取消按精确 RunId 回滚。
- **与 Claude Code 原生能力分工**:`autoContinueAtUsageLimit`(2.1.234+ 默认开)处理仍打开的交互会话,但官方说明它不覆盖 Remote Control 会话与重置超过 24 小时的周额度,也不覆盖已关闭的会话与 `-p`。AI Resume 对开着的会话不重复续跑;额度已放行、且距中断记录的 `resetsAt`(没有则用中断时间)已过 10 分钟、会话仍开着且尾部仍是那次中断 → 飞书提醒一次(`session-open`)。
- **执行**:Claude 经 `internal-agent-run`(.NET ArgumentList,Job Object 监督,先登记 `process_registry`)运行 `claude --resume <UUID> -p <resumePrompt> --model <项目模型> --output-format stream-json --verbose`;环境带 `AI_RESUME_INTERNAL_RUN=1`(钩子据此不入队)、`CLAUDE_CODE_PROVIDER_MANAGED_BY_HOST=1` 与当前访问令牌 `CLAUDE_CODE_OAUTH_TOKEN`;权限沿用用户自己的 settings,`skipPermissions` 默认 false。输出只把结构化 `rate_limit_event` 认作限流,此前若出现非只读工具调用、未知事件或损坏输出 → `limited-side-effects`。Codex 执行计划不变(官方账号 `-c model_provider="openai" -c openai_base_url=…`,需 Codex 0.148.0)。
- **续跑提醒**(`IResumeNotices` → `ResumeNoticeWriter`,source=`ai-resume`、kind=`resume`,按去重键只发一次):`session-open`、`resume-started`、`resume-succeeded`、`resume-failed`、`credentials-unavailable`(令牌过期、登录失效、凭据挪进凭据管理器或账号未核实;文案按原因给处理办法,每个原因每 4 小时至多一次)、`provider-changed`、`resume-not-started`;写入失败时撤回内存去重键,下一拍重写;登录到期类 `login-expiring` / `login-expired` 由 §2.6 的提醒服务写入。
- **可观测**:每次取证一行 `resume.probe.evaluated`,逐项目 `名=模型|额度[:窗口]|占用|会话证据|原因`;`resume.project.not_started` / `resume.project.done` 带模型与原因;`arm.get.projects[]` 给出 `reasonText`(短句)与 `hint`(悬停说明)、`resetAtUnix`、`modelSource` 等。
- **仍有效的安全语义**(细节见 ADR-0002、`docs/RUN-CONTRACT.md`):每个异步边界后复核周期、模型与最新 `Selected`;解除布防或移除正在执行的项目按精确 RunId 请求终止,未确认前保留门禁并显示「终止待确认」;`arm.get` 在 `config.json.write.lock` 内同时捕获配置与严格读取的 SQLite 状态,读不出时整体显示「布防状态未核实」;一次性周期 SQLite 已 `done` 但配置未解除时崩溃,重启只补做解除。

### 2.4 额度门禁(`QuotaResumeProbe.Evaluate`,按模型族)

- 取数主路径:官方 `GET https://api.anthropic.com/api/oauth/usage`,请求头复现 Claude Code(`anthropic-beta: oauth-2025-04-20`、`anthropic-version: 2023-06-01`、`User-Agent: claude-code/<本机版本>`);优先解析现代 `limits` 的 `session` / `weekly_all` / `weekly_scoped`,兼容旧顶层窗口。快照按账号指纹隔离写入 SQLite `quota_snapshots`(`IMMEDIATE` 事务内重读、合并、写回);GUI 成功缓存 5 分钟、失败 30 秒,续跑取证一律强刷。
- **只看目标模型相关的窗口**:`five_hour` / `seven_day` 主窗口 + 与目标**同模型族**的 `weekly_scoped:*`(同族任何版本都算)+ 认不出模型族的 scoped(保守视为相关)。其它族的 scoped 用尽不挡本项目(Fable 周额度用尽不挡 Opus 项目,反之亦然);目标模型没有专属 scoped 时由主窗口决定。
- 判定:只认 OAuth 证据;任一相关窗口打满/blocked 或出现未归因限流 → `limited`;两个主窗口都是本次实时读数且 0 ≤ % < 100、相关 scoped 没有「非承接且缺百分比」、无存储故障 → `ok`;其余 → `unknown`。相关 scoped 的承接读数(`carriedForward`)未满不阻塞、满额仍算限流;主窗口必须是实时读数。
- `ClaudeProbeResult.Detail` 只含窗口名与状态码(如 `weekly_scoped:Fable:blocked`、`seven_day:carried`);`BlockingResetUtc` 取阻塞窗口里最晚的重置时间(界面「预计 HH:mm 起续跑」)。禁止以 CLI/Haiku 成功代替目标模型恢复;GUI 刷新不做任何推理。
- **Codex 官方账号**(ADR-0005 B1,`ProviderCatalogService.EvaluateChatGpt`):`/backend-api/wham/usage` 账号主/次窗口全部 allowed 且 0 ≤ % < 100、无附加限额或 `spend_control` 命中、目标模型未在 `model_usage` 标为不可用 → ok;任一限额命中 → limited;无数据或缺百分比 → unknown。
- **DeepSeek**:`deepseek` 行读 OpenCode 的 DeepSeek 密钥(OpenCode 2.x 配置层 `OPENCODE_CONFIG_DIR`(否则 XDG,默认 `~/.config/opencode`)下的 `opencode.json` → `opencode.jsonc` → `OPENCODE_CONFIG` → `OPENCODE_CONFIG_CONTENT` → `%ProgramData%\opencode` 托管层合并后的 `provider.deepseek.options.apiKey`,支持 `{env:…}`/`{file:…}`;1.x 的 `config.json` 不读 → OpenCode 2.x 凭据库(`OPENCODE_DB`,默认 `~/.local/share/opencode/opencode.db`)的 `credential` 表(只读;2.x 只在首次启动时导入一次 `auth.json`,之后换密钥、删密钥只改库:库里没有 DeepSeek 就不再看 `auth.json`,库读不了显示未核实)→ 仅当库或表不存在时看 `auth.json` → 最后才是环境变量 `DEEPSEEK_API_KEY`,来源如实标出;各层合并后(后层覆盖前层)的 baseURL 指向非官方端点就不发余额请求),agent=`opencode`、`resumable=false`,只展示余额;AI Resume 不保存任何 DeepSeek key。所有第三方凭据/配置都以共享读写方式打开(`SharedFile`),不妨碍对方刷新自己的文件。

### 2.5 完成通知投递

| 来源 | 写在哪 | 完成 | 中断 / 等你 |
|---|---|---|---|
| Claude Code | `~/.claude/settings.json`,exec form:`command` = `AiResume.Hook.exe` 绝对路径,`args` = `["claudecode", "--kind=…"]`,timeout 30(需 2.1.139+) | `Stop`(无 matcher) | `StopFailure`(`--kind=failure`,2.1.78+;用量上限、登录失效、账单等)/ `Notification`(`--kind=decision`,matcher = `permission_prompt\|worker_permission_prompt\|elicitation_dialog\|elicitation_url_dialog\|agent_needs_input\|quota_auto_resume_stale\|quota_auto_resume_disabled`) |
| OpenCode ≥ 2.0 | `~/.config/opencode/plugins/airesume-notify/index.ts`(`export default { id: "ai-resume.notify", setup(ctx) }`,靠自动发现,不写进 opencode.jsonc) | 顶层会话 `session.execution.succeeded` 且无运行中的直接子会话(5 秒宽限) | `permission.asked`、`form.created`(question)、顶层 `session.execution.failed`(60 秒宽限) |
| Codex / Cline / Qoder | 各自配置(Codex 为 hooks `Stop`,ADR-0005 C1) | 各自既有边界 | 见 `docs/COMPLETION-NOTIFICATIONS.md` |

- 注册表仍是 5 个适配器(`NotificationRegistry`);GUI 开关与 `notify enable/disable` 都同步意图 `ProductConfig.NotifySources`。主界面只显示 Claude Code 与 OpenCode,其余在「视图 › 显示其他通知源」;隐藏的源若仍启用或 hook 失效,右栏留一句提示。
- Claude Code:只增删我方条目;我方条目混在用户分组里会被移出单独成组,不改写用户分组的 matcher;旧 shell form 照样认领并迁移;三组都在且形状正确才显示「hook 正常」,缺组或旧格式标红「hook 失效」(开关关掉再打开或重新 install 即修复)。Claude Code 热加载 settings,无需新会话;未接受工作区信任的目录不执行任何 settings hook(官方说明)。`stop_hook_active` 只记录、不丢弃;事件补存 `hookEvent/promptId/error/stopHookActive/backgroundBusy/bridged`(`bridged` = 钩子环境里有 `CLAUDE_CODE_BRIDGE_SESSION_ID`,即 Remote Control 已连接),eventId 另纳入 notification_type 与消息哈希。
- OpenCode:启用时把我方旧单文件 `airesume-notify.ts` 迁移为目录插件并留 `.bak`;拒绝覆盖别人的 `index.*`/`server.*`/`package.json`;「插件已加载」只以 `~/.local/share/opencode/log/opencode.log` 末尾 4 MiB 中、插件写入之后的记录为证据,以最后一条结论为准:最后是 `failed to load plugin` → 「插件失效」,最后是 `loading plugin` → 「插件已加载」(之后成功的重启覆盖先前的失败),没有记录 → 待核实。Hook 对 OpenCode 显式 `event_id` 只按 `opencode|event_id|kind` 算 eventId(跨插件实例去重)。
- 投递链路:`NotificationWorker`(15 秒)→ `CompletionNotifier.SweepAsync` → `LarkCliNotificationChannel.SendAsync` → `lark-cli.exe im +messages-send --as bot --user-id <ou_…> --text <多行> --idempotency-key <eventId> --format json`。
  - **直接启动** `lark-cli.exe`:先 `%APPDATA%\npm`,再 PATH 各目录(每个目录先认包内 `node_modules/@larksuite/cli/bin/lark-cli.exe`,再认独立 `lark-cli.exe`;相对路径条目跳过,因为子进程环境里有 App Secret),**绝不经 cmd.exe**(cmd 会在换行处截断参数,`--idempotency-key` 曾因此从未送达);设 `LARKSUITE_CLI_NO_UPDATE_NOTIFIER=1`、`LARKSUITE_CLI_NO_SKILLS_NOTIFIER=1`,UTF-8;找不到 exe → 通道级 `launch_failed`。
  - 凭据:有 `state\secrets\feishu-bot.bin`(GUI 保存的 **LocalMachine** DPAPI 副本;先写进创建时就只允许当前用户 + SYSTEM + Administrators 的临时文件,核验 ACL 后才替换正式文件)时,经 `LARKSUITE_CLI_APP_ID/APP_SECRET/BRAND=feishu/STRICT_MODE=bot` 环境变量交给 lark-cli(`credentialSource=env`,官方环境变量凭据优先于钥匙串);没有副本时退回 lark-cli 自己的钥匙串(`keychain`,S4U 会话 0 下解 CurrentUser DPAPI 会间歇失败,界面标琥珀)。
  - 结果:stdout 与 stderr 两种信封都解析(`code`、`log_id`、`retryable`、`retry_after`、`message_id`),由 `NotifyClassifier` 按 lark-cli 错误契约分级。通道级(`keychain_unavailable` / `rate_limit` / `network`(含 `network_timeout`)/ `auth_config` / `permission_or_recipient` / `confirmation_required`,以及找不到 exe 的 `launch_failed`)→ 熔断整个通道并按类别退避,凭据文件、收件人或 lark-cli 位置一变(`NotifyFingerprint`)立即解除;`invalid_message` 为条目级永久失败 → 移入 `completion-events\dead\`(附 `.error.txt`);其余可重试条目只推迟该条(30s×2^(n-1),上限 15 分钟,±20% 抖动),超过 24 小时进死信;30 分钟内(跨轮记账)两条不同事件命中同类永久失败 → 升级为通道级 `cli_contract`。退出码 0 但输出不是信封 → 按已送达(`sent_unverified`,重试会在幂等窗口外真的重复);结果不明(超时/中断)首次起 55 分钟后不再重发(`outcome_unknown` 进死信)。
  - 调度:按事件 `atUtc` 从旧到新;来源不在通知意图 `NotifySources` 里的事件直接丢弃(`Suppressed`,`source_disabled`,防止已关闭但残留旧链路的客户端继续发,如 Codex `config.toml` 旧 `notify`)。意图三态:`NotifyIntentRecorded=false` 的旧空配置不过滤;记录过的意图哪怕为空也只放行列表里的来源;`ai-resume` 总是放行。发生超过 24 小时还没送出的不补发、进死信(`stale`);Claude 完成类事件 20 秒稳定期,同会话 2 分钟内的后续完成替代前一条;`backgroundBusy`(后台子任务未完)最多暂缓 30 分钟,同会话任何更晚的完成(队列里的,或 `completion-notify-sessions.json` 记着已送出的)都让它作废;收件人为空整轮记 `recipient_missing` 并保留文件;两次发送间隔 300 ms。2026-11-02 起另有两条:① 会话没落盘的 Claude Code 事件(带 `transcriptPath`,稳定期后转录文件仍不存在)不发(`session_not_persisted`)——第三方用量监视器以 `--no-session-persistence` 拉起的 claude 因此不再发「Default Project 因用量上限中断/已完成」;判断不了(路径形状陌生、目录读不了、旧事件没带路径)照发;② 用量上限中断(`StopFailure` rate_limit + `quota_auto_resume_disabled`)按转录里服务端给的 `rateLimitType + resetsAt`(加配置根指纹)定限额周期(读不到按会话,5 小时),每个会话每个周期只提醒一次(`same_limit_cycle`),同一周期同时待发的会话合成一条;入队不满 20 秒先不判(新的限流记录可能还没写),事件晚于转录里的重置时间时那份证据作废,还有刚到的中断时已攒好的组先等它、最早一条满 2 分钟就发;外部事实经 `INotifyFacts`(生产 `NotifyFacts`)读取,读不出一律按「不知道」。
  - 状态:`completion-notify-seen.json`(7 天去重)、`completion-notify-state.json`(条目退避与首次不明时间)、`completion-notify-sessions.json`(每会话最近已送出的完成,2 天)、`completion-notify-limits.json`(已提醒过的限额周期,到重置后 1 小时或 5 小时失效)、`completion-notify-health.json`(Worker 单写:State/Reason/BlockedUntil/BlockFingerprint/LastSuccess/LastAttempt/LastAttemptOk/SuccessFingerprint/Permanent*/LastErrorClass/LastErrorCode/LastLogId/Backlog/Dead/CredentialSource)。界面「可送达」只认这里的真实成功记录,并且要求后台 Worker 在跑、最近一次尝试不是失败(否则琥珀「最近投递失败」)、成功之后没改过设置(否则琥珀「设置已变更」,`notify.channel.settingsVerified=false`)。
  - 文案:第一行「发生了什么」,第二行「来源 · 模型短名 · HH:mm · 「会话名」」(会话名取 `sessions\<pid>.json` 的 `name`,会话已关时取转录尾部的 `custom-title`,只进消息不进日志;Remote Control 已连接时加「· 远程已连接」),需要动手时第三行写怎么做;桌面端临时工作区(`scratch-workspaces`)用会话名代替目录名,没有就写「临时会话」。中断按 `error` 映射(登录失效请 /login / 账单 / 账号受限 / 服务端错误);用量上限中断是四行:`⏸️ <项目> 被用量上限打断`(多个会话合成「N 个会话被用量上限打断:…」)、来源行、`<窗口>已用完,预计 <时间> 重置`(读不到写「重置时间未核实」)、续跑句——只按投递那一刻的布防快照说:未布防 / 本轮布防已停止 / 要重新布防(目标无效、账号变了、额度原因)/ 读不到 Claude 登录 / 会续跑它(会话开着时 AI Resume 不插手,关掉后才续;不替 Claude Code 承诺)/ 一次性布防这一轮已处理过 / 布防的是同项目另一个会话 / 不在布防范围 / 布防状态未核实(含续跑监视卡住);收到 `quota_auto_resume_disabled` 时补「Claude Code 也不会自己继续」。没有服务端额度窗口时只说「被限额打断」「是哪个额度、何时恢复未核实」。测试通知为「🧪 AI Resume 测试通知」(只证明飞书投递这一段)。
- 控制面 `NotifyControlPlane`(GUI 与 CLI 共用):`Describe`;`CheckAsync`(`lark-cli doctor`,在线只读,核对 bot 身份;输出里的 app_id / open_id 遮蔽后才显示);`SetCredentialAsync`(先用这组凭据跑 doctor,飞书换得到 bot token 才保存);`ImportLegacyAsync`(导入 v2.1「飞书接入」的 CurrentUser `feishu-platform.bin`,同样先核验,旧文件保留);`ClearCredential`;`SetRecipient`(规整去空白后必须是 `ou_` 开头的 open_id);`DetectRecipientsAsync`(`lark-cli auth list --json`;保存过机器人凭据副本时只留同一应用的用户,`filteredByApp`);`EnqueueTest`(测试通知走完整队列链路);GUI「发送测试」与 `notify test` 共用 `TestOutcome`,只看这一条事件自己的结局(`delivered` / `dead` / `failed`(这一条确定失败,原因见 `TestFailure`)/ `blocked`(通道此刻仍在熔断)/ `pending`),失败、熔断或 90 秒超时会 `WithdrawTest` 撤回这一条;Worker 发送前复查并在发送期间占住事件文件,撤回与发送互斥,撤不回时如实提示可能仍会送达。

### 2.6 Claude 登录与账号指纹

- `ClaudeCredentialSource` 只读 Claude Code 自己的登录 `%USERPROFILE%\.claude\.credentials.json`(或 `CLAUDE_CONFIG_DIR` 下同名文件),共享读、**绝不刷新、绝不写回**:refresh token 一次性轮换,外部刷新会把用户登出。访问令牌约 8 小时,由正在运行的 Claude Code(会话或 Remote Control 桥)提前续期;登录本身(`refreshTokenExpiresAt`,约 30 天)只能由用户 `/login` 续期。访问令牌剩余 < 60 秒视同过期。
- `ClaudeLoginVerdict`:`Ok` / `LoginExpiring`(登录 ≤ 3 天到期,与 Claude Code 自己的提示阈值一致)/ `TokenExpired`(访问令牌过期,等 Claude Code 续期)/ `LoginExpired`(登录到期,或刷新被拒后 Claude Code 已清空令牌、`expiresAt=0`)/ `LoggedOut`(没有凭据、文件不可读或损坏)/ `StoreMoved`(`CLAUDE_CODE_FORCE_WINDOWS_CREDMAN=1` 或全局配置 `cachedGrowthBookFeatures.tengu_windows_credman=true`,凭据改存 Windows 凭据管理器,AI Resume 读不到)。
- **账号指纹**:Claude Code 全局配置里 `oauthAccount.organizationUuid` + `accountUuid` 的 SHA-256(主配置:`<配置根>/.config.json` 非空就用它,否则 `CLAUDE_CONFIG_DIR` 或用户目录下的 `.claude.json`;另交叉核对 `<配置根>/.claude.json`,默认即 `~/.claude/.claude.json`,两份不一致视为未核实)。按长度与写入时间缓存,正被改写时沿用本进程上一次成功值;核实不了 → 身份为空 → 续跑 `identity-unverified` 等待,**绝不退回令牌指纹**(v2.1 的令牌指纹每 8 小时漂移一次,同一账号被反复判成 provider-changed)。
- `claude.login.status` 只回 `verdict`、`serverRejected`、访问令牌/登录到期时间、订阅类型、`accountVerified` 与 `problem`,不回令牌、邮箱或 UUID。`serverRejected`:usage 端点最近一次以 401 / 结构化 403 拒绝了本机令牌(`QuotaService.LastRejection`,之后任何一次成功清掉)且之后登录文件没再被改写 → 卡片红色「被服务端拒绝 · 需 /login」,不再只凭本地时间戳显示绿色「有效」。额度磁贴承接最近读数时,悬停说明写出本次读取失败的原因,页头写「部分更新」。
- Claude 订阅的额度快照合并:较新的实时 OAuth 读数带着明确百分比时以它为准(升级套餐、服务端提前重置后用量会合法下降);同代取大只留给较旧/晚到的观测与非 OAuth 观测(详见 CLAUDE-QUOTA-ACQUISITION §7.2)。
- `ClaudeLoginReminder`:登录到期前 3 天、2 天、1 天、12 小时内与自然到期各发一次飞书提醒;登录被 Claude Code 清空(到期时间随之消失)时每个本地日提醒一次,直到恢复;没设收件人时不写事件,每一档按到期时间去重只发一次,不补发已错过的档。
- 已知缺口:续跑注入的 `CLAUDE_CODE_OAUTH_TOKEN` 没有 refresh token,超长续跑可能中途过期(45 分钟门槛只保证启动时充足)。
- v2.1 及之前:AI Resume 自持 `claude setup-token` 长期令牌(DPAPI LocalMachine `state\secrets\claude-oauth-token.bin`)。usage 端点不接受它,已于 2026-10-30 随 ADR-0006 删除;`install` 激活成功后删除该文件及 `.rejected.json`。

### 2.7 已退役:cc-connect(v2.1 及之前,仅作历史)

v2.1 及之前由直接运行的 cc-connect 承担飞书/微信聊天、会话与 cron,AI Resume 负责生成、校验、激活它的配置(`cc-connect-apply`、单消费者预检、`allow_from`/`admin_from`)。这些已于 2026-10-30 随 ADR-0006 整体退役:`AiResume.Wrapper` 项目、相关命令、RPC 与 14 个测试文件已删除;AI Resume 不再读取、改写或删除 `~/.cc-connect`。历史细节见 ADR-0003 与 [docs/history/FEISHU-BOT-GUIDE.md](docs/history/FEISHU-BOT-GUIDE.md)。

## 3. 模块职责(路径 → 职责 → 关键函数/入口)

| 路径 | 一句话职责 | 关键函数 / 入口 |
|---|---|---|
| `csharp/src/AiResume.Core/` | 无平台 I/O 的契约与领域类型 | `ProductConfig`/`ProjectRef`、`ResumeTarget`(`EffectiveModel`/`Identity`)、`ProjectAdmission`、`CheckerState`/`ProjectNote`、`ClaudeProbeResult`、`ClaudeModelFamilies`、`StartRequest`、`RunKey` |
| `csharp/src/AiResume.Storage/` | SQLite/WAL 持久化(schema v6) | `StorageDatabase.Migrate`、`RunStore`、`ProductStateStore`(严格读取)、`OutboxStore`(目前无生产调用方) |
| `csharp/src/AiResume.Ipc/` | Named Pipe 帧与客户端/服务端 | `NamedPipeTransport`、`PipeFraming`、`PipeProtocol` |
| `csharp/src/AiResume.Secrets/` | DPAPI 与脱敏 | `DpapiSecretStore`(LocalMachine 存机器人凭据并收紧 ACL;CurrentUser 只用于导入旧版)、`SecretRedactor` |
| `csharp/src/AiResume.LarkCli/` | lark-cli 结构化进程适配 | `LarkCliInvoker`(环境注入、UTF-8、stdout/stderr 信封、耗时)、`LarkEnvelope`、`LarkRedactor` |
| `csharp/src/AiResume.Launcher/` | 开机自启的无窗口垫片 | `Program.Main`(无控制台窗口拉起同目录 Worker) |
| `csharp/src/AiResume.Worker/Program.cs` | 命令分派与 Host 装配 | `install/uninstall/shortcuts/notify`、`internal-agent-run`、常驻 Host |
| `csharp/src/AiResume.Worker/Migration/` | 运维命令(目录名沿用,已无迁移命令) | `InstallCommand`(含 `RetireLegacyClaudeToken`)、`NotifyCommand`、`ShortcutCommand`、`WorkerAutostart` |
| `csharp/src/AiResume.Worker/Resume/` | 逐项目续跑:绑定、会话证据、执行与提醒 | `ResumeEngine`、`ResumeBindingService.BindAsync/ChooseModel/Preview`、`ResumeSessionCatalog.Occupancy/ReadEvidence`、`ClaudeSessionEvidence`、`ProviderExecutionPlan`、`ClaudeResumeRunner`、`AgentExecCommand`、`ClaudeHostProvider`、`ResumeNoticeWriter`、`ClaudeLoginReminder` |
| `csharp/src/AiResume.Worker/Quota/` | Claude 登录、OAuth 额度、稀疏快照与门禁 | `ClaudeCredentialSource.Read/Describe`、`ClaudeOAuthUsageProbe`、`QuotaService`、`QuotaResumeProbe.Evaluate`、`QuotaSnapshotStore`、`AddQuotaResumeAdmission` |
| `csharp/src/AiResume.Worker/Probes/` | 供应商目录、余额与续跑取证 | `ProviderRegistry.Read`、`ProviderCatalogService`(`CheckModelsAsync`/`BindWithCatalogAsync`/`EvaluateChatGpt`)、`ProviderResumeProbe`、`OpenCodeCredentials.ReadDeepSeek`、`DeepSeekProbe`、`CodexAuthProbe`、`CodexBalanceProbe`、`ChatGptUsage` |
| `csharp/src/AiResume.Worker/Products/` | 配置、项目发现与布防周期状态机 | `ProductConfigStore`、`ProjectCatalog.Discover`、`ProjectIndex`、`ProjectPaths`、`CheckerCycle.OnProbe/PrepareActiveRun/ApplyProjectResult`、`EngineLiveness` |
| `csharp/src/AiResume.Worker/Supervision/` | 进程登记、Job Object、恢复对账 | `ProcessSupervisor`、`ProcessVerifier`、`Reconciler` |
| `csharp/src/AiResume.Worker/Notifications/` | 通知源注册表、各客户端适配器与飞书投递 | `NotificationRegistry`、`ClaudeCodeNotificationAdapter`、`OpenCodeNotificationAdapter`、`CodexHooksNotificationAdapter`、`HookHealth`、`NotificationWorker`、`CompletionNotifier`、`LarkCliNotificationChannel`/`NotifyClassifier`、`FeishuBotCredentials`、`NotifyControlPlane`、`NotifyRecipient`、`NotifyIntent` |
| `csharp/src/AiResume.Gui/` | WPF + WebView2 宿主与 RPC 桥 | `MainWindow`、`ControlPlaneBridge.HandleAsync`、`DemoPayload`(截图合成数据) |
| `csharp/src/AiResume.Gui/wwwroot/index.html` | 单页前端(菜单栏、磁贴、队列、右栏、对话框) | `<script id="vm">` 的 `AR.quotaTiles/rowModel/beaconModel/notifyModel/loginModel/armRequest`、`call` |
| `csharp/src/AiResume.Hook/Program.cs` | agent hook 入口 | `TryWriteEvent`、`ReadKind`、`ShouldSuppress`、`ComputeEventId` |
| `csharp/test/AiResume.Tests/` | 隔离的 xUnit 回归 | 本轮 1250 项(以当次输出为准),不触碰真实会话/项目运行 |

v2.1 的 `csharp/src/AiResume.Wrapper/`(cc-connect 薄适配与安全检查)已于 2026-10-30 删除。

## 4. 测试 / 运行流程

- **构建入口**:`csharp/AiResume.sln`;Worker 入口 `csharp/src/AiResume.Worker/Program.cs`;GUI 入口 `csharp/src/AiResume.Gui/App.xaml` / `MainWindow.xaml.cs`。
- **环境要求**:Windows 10/11、.NET 10 SDK、WebView2 Runtime;飞书通知需 lark-cli(`npm i -g @larksuite/cli`,本机核对 1.0.81);Claude Code 钩子需 2.1.139+(`StopFailure` 需 2.1.78+;2026-10-30 本机 CLI 2.1.300 / VS Code 2.1.280 / Desktop 2.1.309);OpenCode 通知需 2.x(本机 Desktop 2.2.3);Codex 续跑需 0.148.0;两组测试需本机 node。

### 4.1 本地构建和测试

```powershell
dotnet build csharp\AiResume.sln -c Release -warnaserror
dotnet test csharp\AiResume.sln
```

本轮完整回归 1250 项全绿(约 40 秒;用例数以当次输出为准)。测试经 `TestTemp` 申请临时目录,注入 runner/API/时钟、假 PID 与合成会话隔离生产状态,不发付费请求;`GuiPageContractTests`(Node 驱动视图模型)与 `OpenCodeNotificationAdapterTests`(内嵌 Node harness,21 个事件场景)需要 node;通知回归会启动真实 Hook 进程,但不启动 agent。

### 4.2 部署现役副本

```powershell
csharp\src\AiResume.Worker\bin\Release\net10.0-windows\AiResume.Worker.exe install
```

`install` 事务语义不变:staging 校验并冻结逐文件 SHA-256 → 备份 → 停旧 Worker → 按冻结清单复制并逐项复核 → 删除上一版清单里已移除的文件(如 v2.1 的 `AiResume.Wrapper.dll`)→ 不继承调用方重定向句柄地启动新 Worker,核对 Named Pipe pong 的 PID → 重建快捷方式 → 按 `NotifySources` 对账 5 个通知源(Claude 钩子同时改写为 exec form,OpenCode 迁移为目录插件)→ 删除旧 Claude 长期令牌文件。退出码:通知源未对齐 2、Worker 未启动 3、回滚不完整 4(保留恢复材料)。`uninstall` 经清单约束的临时 Worker 事务性退役 payload,保留 `state\` 与未知文件。已打开的 GUI 不热更新,部署后关闭重开;Claude Code 与 OpenCode 热加载钩子/插件;已打开的 Codex 会话不重载 `hooks.json`,新会话才生效。

### 4.3 常用 Worker 命令

```text
install / uninstall            安装或卸载现役副本
shortcuts                      重建桌面/开始菜单/开机自启入口
notify [list]                  列出 5 个通知源:已安装 / 已启用 / 可送达(有启用且断链的返回 2)
notify enable|disable <源>     开关通知源并同步意图(ClaudeCode / Codex / Cline / Qoder / OpenCode,大小写不敏感)
notify check                   诊断飞书通道:lark-cli、凭据来源、收件人、最近投递、lark-cli doctor;就绪 0,否则 2
notify test                    入队一条冒烟事件,最多等 90 秒看后台 Worker 真实送达;送达 0,否则 2
(不带子命令)                   以常驻服务启动;internal-agent-run 只供续跑内部使用
```

未知子命令返回 2(`notify` 下的未知子命令返回 1)。v1 的 `migrate` / `import-feishu` / `sync-dirs` / `cutover-config` 已于 2026-10-12 删除;v2.1 的 `cc-connect-apply` / `feishu-check` / `preflight` 已于 2026-10-30 删除,现按未知命令处理。

### 4.4 重点测试路由

- 逐项目续跑:`ResumeSchedulerTests`、`ProjectResumeFactsTests`、`ClaudeSessionEvidenceTests`、`ResumeEngineTests`、`CheckerCycleTests`、`ClaudeResumeRunnerTests`、`ProviderBoundResumeTests`、`ProviderExecutionTests`。
- 额度门禁:`QuotaResumeProbeTests`、`ScopedLimitTests`、`ScopeModelTests`、`ClaudeModelFamiliesTests`、`QuotaServiceTests`、`QuotaSnapshotStoreTests`、`ClaudeOAuthUsageProbeTests`。
- Claude 登录:`ClaudeCredentialSourceTests`、`ControlPlaneBridgeProviderTests`(`claude.login.status` 不泄密)。
- 通知源:`ClaudeCodeNotificationAdapterTests`、`OpenCodeNotificationAdapterTests`、`CodexHooksNotificationAdapterTests`、`ClineNotificationAdapterTests`、`QoderNotificationAdapterTests`、`HookHandlerTests`、`HookCommandTests`、`HookHealthTests`、`NotifyIntentTests`、`NotificationHookProcessTests`、`NotificationMarkerIdentityTests`。
- 投递与通道:`CompletionNotifierTests`、`NotificationDeliveryTests`、`NotificationWorkerTests`、`NotifyControlPlaneTests`、`NotifyRecipientTests`、`LarkCliInvokerTests`、`ControlPlaneBridgeNotificationTests`。
- GUI:`GuiPageContractTests`、`ControlPlaneBridgeArmTests`、`ControlPlaneBridgeDemoTests`、`ControlPlaneBridgeProjectsTests`、`ControlPlaneBridgeQuotaTests`。
- 供应商:`OpenCodeCredentialsTests`、`DeepSeekProbeTests`、`CodexAuthProbeTests`、`CodexOfficialProviderTests`、`CodexBalanceProbeTests`。
- 进程与恢复:`PowerLossRecoveryTests`、`ProcessVerifierTests`、`ReconcilerTests`、`SupervisionProcessTests`;安装:`InstallUninstallTests`、`InstallTargetSafetyTests`、`InstallWorkerLauncherTests`、`WorkerAutostartTests`。

### 4.5 真机核验

- install 后核对:`notify list` 的「可送达」列、`notify check` 退出码 0、`notify test` 真实送达、`opencode.log` 出现新插件的 `loading plugin` 且其后没有 `failed to load plugin`。本轮(2026-10-30)记录:Release 构建 0 警告、1156 项全绿、install 退出码 0 并删除旧令牌文件、`notify list` 显示 OpenCode 插件已被加载、`notify disable` 关闭 Codex/Cline/Qoder(意图只留 ClaudeCode 与 OpenCode)、`notify check` doctor 就绪、`notify test` 真实送达(当时凭据来源为 keychain)。两轮交叉审查修正后(2026-10-31 本地时间 00:10 前后)复核:Release 构建 0 警告、1195 项全绿、仓库 secrets 门禁 0 命中、install 退出码 0(Claude Code 钩子按新 matcher 重写)、`notify list` 退出码 0 且 OpenCode 已加载、`notify check` 退出码 0、`notify test` 真实送达、`--screenshot` 真实 WebView2 渲染正常;凭据来源仍是 keychain(机器人凭据尚未在「飞书通知设置」里保存成机器范围副本)。修复轮复审修正后(2026-11-02 本地时间 08:50 前后)复核:Release `-warnaserror` 无增量重建 0 警告、1215 项全绿、install 退出码 0、`notify list` 退出码 0(Claude Code 与 OpenCode 可送达=True,OpenCode 已加载)、`notify check` 退出码 0、`notify test` 真实送达;部署后的真实窗口:四块额度磁贴读出实时读数(不再停在「读取中」)、Claude 登录「有效」、两个通知源「钩子正常」、飞书机器人「可送达」、页脚「后台服务运行中」,项目发现多出一个此前因会话首行超 64 KiB 读不出目录的项目。凭据来源仍是 keychain。限额通知规则与界面用语修正后(2026-11-02 本地时间 11:45 前后)复核:Release `-warnaserror` 无增量重建 0 警告、1250 项全绿、install 退出码 0、`notify list` 退出码 0(Claude Code 与 OpenCode 可送达=True)、`notify check` 退出码 0、`notify test` 真实送达(Worker 日志 `outcome=Sent`);部署后的真实窗口:两个通知源「hook 正常」「插件已加载」、飞书机器人「可送达」、页脚「后台服务运行中」、布防栏「预览…」。凭据来源仍是 keychain(界面显示「未存凭据副本」的前提是还没有真实投递记录)。
- **待真机验证**:一次真实的用量上限中断后的逐项目自动续跑端到端;OpenCode 里一次真实任务完成时发出通知;在「飞书通知设置」保存或导入机器人凭据后,S4U Worker 经机器范围副本(`credentialSource=env`)的真实送达;Claude Code 登录到期提醒。
- 界面证据:`$env:AIRESUME_SHADOW_DIR=<临时目录>; AiResume.Gui.exe --screenshot <png>`(演示模式只用合成数据,约 2 秒后截取;加 `--screenshot-unarmed` 为未布防态)。不设 `AIRESUME_SHADOW_DIR` 会在真实状态目录下生成 webview2 缓存与日志。

## 5. 数据格式与命名约定

### 5.1 AI Resume 状态目录

```text
%LOCALAPPDATA%\AI Resume\state\            可经 AIRESUME_SHADOW_DIR 覆盖;uninstall 保留
  config.json / config.json.write.lock     产品意图(锁内读改写,原子替换)
  runs.db                                  SQLite/WAL,schema v6
  project-index.json                       项目发现索引
  completion-events\<eventId>.json         通知队列(钩子与续跑提醒共用);dead\ 死信、malformed\ 坏事件
  completion-notify-seen.json / completion-notify-state.json / completion-notify-health.json
  logs\worker-yyyyMMdd.log、logs\gui-yyyyMMdd.log
  webview2\                                WebView2 用户数据
  secrets\feishu-bot.bin                   飞书机器人凭据(DPAPI LocalMachine,ACL 收紧)
  secrets\feishu-platform.bin              v2.1 及之前「飞书接入」凭据(CurrentUser),只用于一键导入
```

- `config.json`(camelCase):`armed`、`armCycleId`、`continuous`、`selected[]`(`name/path/sessionId/modelId/modelName/modelSource`;`modelSource` = `session` 跟随会话 / `user` 手选 / `default` 会话模型读不到或不在目录时用目标默认 / 空 = v2.1 旧配置,沿用 `resumeTarget.modelId`)、`resumeTarget`(`providerId/providerIdentity/providerName/agent/modelId/modelName/boundAtUtc`,只存身份指纹)、`resumeModel`(目标默认模型)、`resumePrompt`(默认 `continue`)、`skipPermissions`(默认 false)、`probeIntervalMinutes` / `quotaRefreshMinutes`(默认 15)、`customProjects`、`hiddenProjects`、`notifySources`(意图)、`notifyRecipientOpenId`(明文 `ou_…`)。不含任何凭据;旧 `ccConnectAgent` 字段读时忽略、写回时消失。
- `runs.db` 表:`runs`、`run_events`、`outbox`(目前无生产写入,通知队列是文件)、`process_registry`、`product_state`、`quota_snapshots`(账号指纹隔离)。`product_state` 存 `CheckerState` JSON:`phase`(idle/waiting/resuming/done/blocked)、`cycleId`、`sawLimited`、`lastProbeUtc`、`lastProbeReason`、`limitedRefires`、`replayBlocked`、`projectStatus{路径: limited|running|success|continued-elsewhere|…}`、`projectNotes{路径: reason/detail/resetAtUnix/interruptedAtUnix/client/atUnix}`、`consumedInterruptions{路径: 中断 uuid}`、`activeRunId/activeProjectPath`、`pendingCancellation*`。
- 环境变量:`AIRESUME_SHADOW_DIR`(状态根覆盖,测试与公开截图用);`AI_RESUME_ENABLE_WEBVIEW_DEVTOOLS`(仅 `1`/`true` 开 DevTools);`AI_RESUME_INTERNAL_RUN=1`(AI Resume 自己启动的进程带它,钩子据此不入队)。

### 5.2 文件名解码样例

真实文件名 `logs\worker-20261030.log` 拆解为:

- `worker` = 写日志的进程(GUI 为 `gui`)。
- `20261030` = 本地日期 `yyyyMMdd`,按日滚动。
- `.log` = 每行一个 JSON(`ts/level/component/run_id?/event/data`,`data` 经 `SecretRedactor` 脱敏);续跑看 `resume.*`,投递看 `worker.notify.*`。

队列文件 `completion-events\9c41e07a5d2b3f18.json`(示例 ID)拆解为:

- `9c41e07a5d2b3f18` = eventId:SHA-256 前 8 字节的 16 位十六进制。Claude Code 由来源、会话、prompt、hookEvent、notification_type、error、消息哈希等派生;OpenCode 由 `opencode|event_id|kind` 派生;续跑提醒由 `ai-resume|类型|去重键` 派生。它同时是飞书 `--idempotency-key`(飞书 1 小时内去重)与本地 7 天去重键。
- `.json` 字段:`version=1`、`eventId`、`source`(claudecode/opencode/codex/cline/qoder/ai-resume)、`kind`(completion/failure/decision/resume)、`notificationType`、`sessionId`、`turnId/taskId/transcriptPath`(可空)、`cwd`、`atUtc`;Claude 另有 `hookEvent/promptId/error/stopHookActive/backgroundBusy/bridged`,续跑提醒另有 `model/detail`,冒烟事件 `smoke=true`。

AI Resume 只读的外部文件:

- Claude 会话转录 `%USERPROFILE%\.claude\projects\<编码目录>\<会话 UUID>.jsonl`:`<编码目录>` 是 Claude Code 对 cwd 的编码(如 `D:\Demo\ledger-api` → `D--Demo-ledger-api`);AI Resume 不反推它,而是读 jsonl 头部的 `cwd` 字段;`subagents\` 下的子代理会话不参与绑定。
- 会话登记 `%USERPROFILE%\.claude\sessions\<pid>.json`(官方运行中会话登记,用到 `pid/sessionId/cwd/procStart/status/entrypoint`,schema 未文档化,读不出按「占用未核实」处理)。
- Claude 登录 `.credentials.json`(只取 `claudeAiOauth` 的 `accessToken/expiresAt/refreshTokenExpiresAt/subscriptionType`)与全局配置 `.claude.json` 的 `oauthAccount`。
- OpenCode(配置目录跟随 `OPENCODE_CONFIG_DIR`,否则 `XDG_CONFIG_HOME`;数据目录跟随 `XDG_DATA_HOME`,凭据库可由 `OPENCODE_DB` 改位置;未设置时为下列默认位置):`~/.config/opencode/plugins/airesume-notify/index.ts`(我方生成,首行固定所有权标记)、`~/.local/share/opencode/log/opencode.log`(只读末尾找加载证据,以最后一条结论为准)、全局配置层与 `~/.local/share/opencode/opencode.db` 的 `credential` 表 / `auth.json`(只读 DeepSeek 密钥)。
- Codex:`~/.codex/auth.json`(官方登录,只读)、`~/.codex/hooks.json` 与 `config.toml` 中 `[hooks.state."…"].trusted_hash`(只增删我方分组与信任记录)。

### 5.3 RunContract 命名

- `requestId`:调用方幂等 UUID;同请求重试不重复 spawn。
- `runId`:Worker 生成的运行身份。
- `runKey`:并发所有权键;同键存在非终态或 `childPending` 时拒绝新运行。
- `taskKind`:`chat|query|modify|resume|probe`。
- `childPending`:子进程未确认 close;即使逻辑终态也不能释放运行键。
- `stateVersion` / `seq`:持久状态和事件的单调版本。

## 6. FAQ(同事高频问题,直接给答案)

- **Q:布防了却一直没续跑,怎么看卡在哪?** — A:看队列行「续跑模型 / 状态」的短句,悬停有完整说明与「预计 HH:mm 起续跑」;状态灯说引擎在不在干活,页脚说后台服务在不在。常见原因码(`ProjectNote.Reason`):`not-interrupted`(会话没被用量上限打断,无需续跑)、`quota:limited`(等额度,短句写明哪个窗口)、`session-in-use` / `cwd-busy`(会话或同目录另一会话开在客户端)、`quota:credentials-unavailable` / `quota:identity-unverified`(读不到可用的 Claude 凭据,短句按原因区分:令牌过期 → 打开一次 Claude Code;登录失效 → `/login`;凭据挪进 Windows 凭据管理器;账号未核实)、`quota:provider-changed`(账号变了,需重新布防)、`quota:model-unverified`(模型不在当前目录)、`session-unverified` / `evidence-unverified`(读不出会话占用或会话结尾)、`not-started:<原因>`(这次没启动,下一轮重试)、`blocked:<结果>`(进入启动/登记之后失败,本周期停止)。后台日志 `state\logs\worker-yyyyMMdd.log` 的 `resume.probe.evaluated` 每次取证逐项目一行。页脚「后台服务未运行」时什么都不会发生:重新 install 或重新登录 Windows。
- **Q:项目 A、B 想用同一个模型(或各用各的)怎么办?** — A:每行有自己的「续跑模型」下拉:默认「沿用会话」(该会话最近一次真实应答的模型,显示 ↺),也可手选。A、B 都选(或都沿用到)模型 A 就是共用:每次取证只观测一次额度,按队列顺序串行续跑,每个项目启动前重新取证,A 跑完把额度用尽时 B 回到「等额度」;用不同模型的项目各等各的窗口。手选模型必须在供应商实时目录里,否则整次布防被拒;读不出会话模型的行要手选才能布防。布防后模型固定(行内悬停可见模型 ID 与来源),想换要解除后重新布防。
- **Q:Fable 周额度满了,Opus 项目会被挡吗?5 小时窗口已重置为什么还不续?** — A:不会被挡:门禁只看目标模型相关的窗口(5 小时 / 7 天主窗口 + 同模型族的 `weekly_scoped`,认不出族的也算)。5 小时重置只是重新取证的时机:7 天窗口或同族 scoped 仍满、主窗口只有承接读数、令牌过期、会话还开着,都会继续等,行内短句会写明原因。「按模型」磁贴显示已用最高或已满的 scoped,角标「N 项在等」表示有几个布防项目卡在它上面。
- **Q:会话还开着,或者我正用手机 Remote Control 操作它,会被重复续跑吗?Remote Control 和自动续跑什么关系?** — A:不会重复。会话登记显示绑定会话开在客户端(或同目录另一会话正忙)时,AI Resume 只等不续;Claude Code 原生 `autoContinueAtUsageLimit` 只处理打开着的交互会话,官方说明它不对 Remote Control 会话、重置超过 24 小时的周额度自动等待。所以额度恢复 10 分钟后会话仍开着、尾部仍是那次中断时,AI Resume 发一次飞书「额度已恢复,会话仍开在客户端」:你可以在原会话或手机里直接继续(状态随后变「已在客户端继续」,算完成),也可以关掉会话让 AI Resume 续跑。钩子在 Remote Control 会话里照常触发,通知带「· 远程已连接」;保持一个 Remote Control 会话在线还会让 Claude Code 持续续期访问令牌,夜里无人值守时额度证据才不中断(AI Resume 自己从不刷新令牌)。
- **Q:飞书通知收不到怎么排查?** — A:按链路逐段看:① `AiResume.Worker.exe notify list`:Claude Code / OpenCode 行「可送达」应为 True(`False(hook 失效)` / `False(插件失效)` 时退出码 2;GUI 开关关掉再打开即可修复);② `notify check`:lark-cli 是否找到、凭据来源(`env` 是机器范围副本;`keychain` 在后台计划任务下会间歇失败,去「工具 › 飞书通知设置…」保存凭据或「导入旧版凭据」)、收件人(可点「识别」从 `lark-cli auth list` 读取)、最近投递结论与 doctor 各检查项;③ `notify test` 或右栏「发送测试」:应收到「🧪 AI Resume 测试通知」;④ 还不行看 `state\completion-notify-health.json`(Reason / LastErrorCode / LastLogId)、`completion-events\dead\*.error.txt` 与 Worker 日志里 `worker.notify.item … class=… errCode=… logId=…`。另外:页脚「后台服务未运行」时通知只会排队;通知源开关关掉(意图里没有)时,该来源的事件会被投递端直接丢弃;Claude Code 在未接受工作区信任的目录不执行 settings hook;exec form hook 需要 Claude Code 2.1.139+;会话没落盘(没有转录)的 claude 进程按设计不通知(日志 `detail=session_not_persisted`)。
- **Q:飞书反复收到「Default Project 因用量上限中断」,可我没有这个项目,也没布防?** — A:2026-11-02 之前的现象,来源是第三方用量监视器(CodeZeno ClaudeCodeUsageMonitor,工作目录 `Documents\Default Project`):访问令牌过期(约 8 小时)时它以 `--no-session-persistence` 拉起 claude 刷新令牌,这个进程读同一份 `~/.claude/settings.json`,周额度用完时每次都触发 `StopFailure`;旧文案又是写死的「会按布防自动续跑」。现在:没落盘的会话不发;每个会话每个限额周期只提醒一次、同时被打断的会话合成一条;续跑句按真实布防状态说,并写明哪个额度、预计何时重置。排查「谁发的通知」先看进程的工作目录与命令行。
- **Q:OpenCode 的通知怎么开、怎么确认生效?** — A:只支持 OpenCode 2.x(1.x 单文件插件在 2.x 下不再加载,本机 2026-10-29 升级后旧插件即失效)。打开右栏 OpenCode 开关(或 `notify enable OpenCode`),会写 `~/.config/opencode/plugins/airesume-notify/index.ts` 并迁移我方旧单文件(留 `.bak`),OpenCode 热加载。行内显示「待核实」直到 `~/.local/share/opencode/log/opencode.log` 出现 `loading plugin …airesume-notify`(随后显示「插件已加载」),出现 `failed to load plugin` 则标红「插件失效」。只有顶层会话完成才报「已完成」,子代理会话不报;授权请求、AI 提问与顶层运行失败报「等你」。
- **Q:Claude 登录到期了,或额度一直「未核实」怎么办?** — A:看右栏「Claude 登录」:「令牌待续期」= 访问令牌(约 8 小时)过期,打开一次 Claude Code 或保持 Remote Control 在线即自动续期,AI Resume 不代刷;「即将到期」「已过期」「未登录」= 登录本身(约 30 天)要在终端运行 `claude` 并执行 `/login`;「有效 · 账号未核实」= 全局配置里没有一致的 `oauthAccount`,打开一次 Claude Code 让它写入;「凭据位置变化」= Claude Code 改用 Windows 凭据管理器,AI Resume 读不到;「被服务端拒绝」= 本地文件还是新的,但额度接口拒绝了这个令牌(改过密码、被移出组织、在别处登出),同样要 `/login`;「未核实」= 登录文件这次读不出(多半正被 Claude Code 改写),稍后自动重试,不是未登录。登录到期前 3 天、2 天、1 天、12 小时与自然到期时各有一条飞书提醒,登录被清空时每天一条(需已设收件人)。v2.1 的「粘贴 setup-token 长期令牌」已删除。
- **Q:从 v2.1 升级后,原来的布防还有效吗?** — A:Claude 订阅的旧布防身份由访问令牌派生,v2.2 改为账号派生,两者对不上:项目一旦被打断,行内会显示「账号已变化 · 重新布防」并发飞书提醒,不会续跑。升级后建议直接解除并重新布防一次,顺带得到逐项目模型。其它兼容:旧 `ccConnectAgent` 字段被忽略;旧 `ProjectRef` 没有模型时沿用 `resumeTarget.modelId`;旧长期令牌文件在 install 时删除;v2.1「飞书接入」凭据可在「飞书通知设置」里一键导入(先经 doctor 核验)。
- **Q:还能在飞书或微信里聊天驱动项目吗?cc-connect 去哪了?** — A:不能。cc-connect 已于 2026-10-30 整体退役(ADR-0006):飞书只用来接收通知,手机上操作 Claude Code 用官方 Remote Control。AI Resume 不再生成、启动或检查 cc-connect,也不读不改不删 `~/.cc-connect`;本机残留的 cc-connect 计划任务、npm 包和 `~/.cc-connect`(内有多份明文密钥)请自行删除并吊销相关密钥。需要在手机上驱动 OpenCode/Codex 时可以自己运行 cc-connect,但它不再受 AI Resume 管理(已知缺口)。
- **Q:为什么 DeepSeek 只显示余额、不能选来续跑?** — A:`deepseek` 行读的是 OpenCode 里那把 DeepSeek 密钥(配置 → OpenCode 2.x 凭据库 `opencode.db` → `auth.json` → 环境变量 `DEEPSEEK_API_KEY`,来源如实标在磁贴底行),回答的是「OpenCode 那个账户还剩多少钱」。续跑只针对 Claude Code / Codex 原生会话;拿 OpenCode 的密钥去跑 Claude Code 等于把一个客户端的凭据挪给另一个客户端。AI Resume 不再保存任何 DeepSeek key。
- **Q:Codex 什么时候自动续跑?界面上怎么看不到 Codex?** — A:Codex 在界面默认隐藏:「视图 › 显示 Codex」后额度栏多一块 Codex 磁贴,有两个以上可续跑供应商时队列标题栏出现供应商下拉。规则不变(ADR-0005 B1):只认 `~/.codex/auth.json` 的官方 ChatGPT/OpenAI 登录;`/backend-api/wham/usage` 账号主/次窗口全部 allowed 且低于 100%、无附加限额或 `spend_control` 命中、目标模型未被 `model_usage` 标为不可用才放行。Codex 没有会话中断证据,沿用「观测到受限、再等恢复」的语义;执行需 Codex 0.148.0。
- **Q:一个项目续跑失败后,为什么这一轮都停了?** — A:进入进程登记/启动之后的失败(撞限前已有写操作 `limited-side-effects`,界面「撞限前已有操作」;未确认完成 `exit-null`;启动/登记/监控错误)可能已有副作用或结果未知,按 ADR-0002 一律不自动重放:状态灯显示「N 个续跑未确认完成 / 本轮已停止」(副标题「不会再自动续跑,检查后解除并重新布防」),检查会话结果后解除并重新布防。只有 spawn 之前的失败(`not-started:*`)才会跳过本轮、自动重试。
- **Q:绿灯的依据是什么?** — A:都来自外部证据:额度磁贴绿 = 本次实时服务端读数且未满(承接读数琥珀、没有百分比不绿);「hook 正常」= 配置里有我方条目且 `AiResume.Hook.exe` 存在(OpenCode 行是「插件已加载」,要它自己的日志证明);飞书机器人「可送达」= 健康文件里有真实送达记录、最近一次尝试成功、核对得上那次成功之后没改过设置(`settingsVerified === true`),且确认后台服务(Worker)在跑(`workerRunning === true`);「监视中」= 布防、后台服务进程在、探测新鲜且常驻 Worker 应答管道;DeepSeek 绿 = 余额接口成功且大于 0;Sub2API 第三方余额按 CC Switch 语义判定。核对不出一律「未核实」,刷新失败不保留旧绿。
- **Q:测试会不会碰真实会话或项目?** — A:不会。测试只用 `TestTemp` 临时目录、假 PID/runner、合成会话与录制的真实形状,禁止 resume 真实会话或对真实仓库启动修改运行;公开截图必须设 `AIRESUME_SHADOW_DIR` 并用 `--screenshot` 的合成数据。

## 7. 术语表(中英 / 缩写对照)

| 术语 | 含义 |
|---|---|
| AI Resume | 本项目;Windows 控制面 + 限额后逐项目续跑引擎 + 完成通知投递 |
| agent | 本地执行 CLI,如 Claude Code / Codex / OpenCode |
| provider | agent 调用的远端 API 端点、凭据和协议(Claude 订阅、ChatGPT 官方、第三方、DeepSeek) |
| model / 有效模型 | 发给 provider 的模型 ID;`ResumeTarget.EffectiveModel` = 项目自己的 `ModelId`,空则用布防目标的默认模型 |
| ResumeTarget | 布防固定的供应商、账号身份指纹、agent 与默认模型;不含凭据 |
| ProjectAdmission | 一个项目一次取证的事实:义务、额度、占用(进程门禁另为全局) |
| 义务 / 中断 uuid | 会话尾部的用量上限中断记录及其 uuid;同一 uuid 最多续一次(`ConsumedInterruptions`) |
| continued-elsewhere | 被打断后会话里又有新应答:视为已在客户端或手机续上,算完成 |
| not-started | spawn 前没能启动(会话刚被打开、令牌不足 45 分钟等);只记原因,不锁周期 |
| ReplayBlocked | 本周期出现可能有副作用的结果,禁止自动重放,需重新布防 |
| weekly_scoped / 模型族 | 按模型的周额度窗口;门禁只看与目标同模型族(或认不出族)的 scoped |
| carriedForward / 承接读数 | 同账号、同窗口身份、同一未来重置周期内的最近服务端读数;显示琥珀,不当实时证据 |
| 账号指纹 | `oauthAccount` 组织 + 账号 UUID 的不可逆哈希;不随令牌轮换变化 |
| Remote Control | Claude Code 官方手机/网页远程;会话在本机运行,钩子照常触发 |
| exec form | Claude Code 钩子写法:`command` 只放 exe,参数放 `args`,不经 shell(2.1.139+) |
| StopFailure | 回合因 API 错误结束时的钩子事件(此时 Stop 不触发) |
| bridged | 钩子事件里的「Remote Control 已连接」标记 |
| S4U | 计划任务「不论用户是否登录都运行」形态,跑在会话 0;CurrentUser DPAPI 在其中会间歇失败 |
| DPAPI | Windows Data Protection API;LocalMachine(机器范围,存机器人凭据并收紧 ACL)/ CurrentUser(用户范围) |
| 熔断 / 死信 | 通道级故障时整个通道暂停并退避 / 永久失败或超过 24 小时的事件移入 `completion-events\dead\`;界面与 CLI 分别叫「飞书投递已暂停」/「未送达、已停止重试」 |
| 限额周期 | 一次用量上限的账号级周期,键为转录里服务端给的 `rateLimitType + resetsAt` 加配置根指纹(读不到按会话);每个会话每个周期只发一次用量上限通知 |
| 后台服务 | 界面对常驻 `AiResume.Worker`(续跑引擎 + 通知投递)的叫法;计划任务名仍是「AI Resume 续跑引擎」 |
| 本轮 | 一次布防(`armCycleId`)从布防到解除;界面不再叫「周期」,免得与额度的重置周期混淆 |
| eventId / idempotency-key | 事件的 16 位十六进制 ID,同时是飞书幂等键(1 小时)与本地 7 天去重键 |
| lark-cli doctor | 官方飞书 CLI 的在线只读体检;机器人凭据「有效」的外部证据 |
| WAL | SQLite Write-Ahead Logging;支持崩溃恢复与并发读写 |
| RunContract | AI Resume 自己启动的进程统一 Start/Status/Cancel 契约 |
| runKey | 一个活动运行对资源/项目的并发所有权键 |
| childPending | 子进程尚未确认退出,运行键仍必须保留 |
| fail-closed | 核对不清时拒绝继续,不把未知当安全/成功 |
| completion boundary | 代表整个 agent 任务结束的 hook/event,不是单次模型请求结束 |
| cc-connect(历史) | v2.1 及之前的多平台聊天桥;2026-10-30 随 ADR-0006 退役 |

## 8. 文档索引(深挖时读)

- [README.zh-CN.md](README.zh-CN.md):中文安装、界面、飞书通知配置与手机远程说明。
- [README.md](README.md):英文用户说明与产品定位。
- [CLAUDE.md](CLAUDE.md):仓库协作规则、部署方式、测试红线和生产安全不变量。
- [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md):现役 C# v2 组件、数据流与完整配置。
- [docs/CLAUDE-QUOTA-ACQUISITION.md](docs/CLAUDE-QUOTA-ACQUISITION.md):Claude OAuth 额度协议、Claude 登录与账号指纹、稀疏合并、SQLite 并发、按模型族门禁与真机验证手册。
- [docs/COMPLETION-NOTIFICATIONS.md](docs/COMPLETION-NOTIFICATIONS.md):各客户端完成边界、钩子/插件形态、飞书投递、去重与故障排查。
- [docs/adr/0006-retire-cc-connect-per-project-resume.md](docs/adr/0006-retire-cc-connect-per-project-resume.md):v2.2:cc-connect 退役、Claude 登录与账号指纹、逐项目续跑、按模型族门禁、完成通知送达修复、DeepSeek 只展示与 GUI 重排的决策和上游证据。
- [docs/adr/0005-owned-credentials-official-codex-and-hooks.md](docs/adr/0005-owned-credentials-official-codex-and-hooks.md):v2.1:Codex 官方账号与放行规则(B1)、Codex hooks 通知(C1)仍有效;A1(setup-token)与 D1 的 Worker 投递设计已被 ADR-0006 取代。
- [docs/adr/0004-provider-bound-resume.md](docs/adr/0004-provider-bound-resume.md):供应商实时目录、固定调用 ID、不换供应商重放与 Codex 执行计划;「全队列一个模型」已被 ADR-0006 改为逐项目。
- [docs/adr/0003-cc-connect-direct-and-control-plane.md](docs/adr/0003-cc-connect-direct-and-control-plane.md):历史:cc-connect 直接运行的决策;其中 cc-connect 相关现役描述已作废,只保留「AI Resume 收敛为四件事」「额度自行获取」「WPF + WebView2」等结论。
- [docs/adr/0002-run-lifecycle-contract.md](docs/adr/0002-run-lifecycle-contract.md):Start/Status/Cancel 与无客户端总时限的决策。
- [docs/RUN-CONTRACT.md](docs/RUN-CONTRACT.md):运行字段、状态机、错误分类与恢复规则。
- [docs/LESSONS.md](docs/LESSONS.md):静默失败、测试污染、mock 猜错结构、cmd 截断参数等仍有效的工程教训。
- [docs/UPSTREAM-ARCHITECTURE-RESEARCH.md](docs/UPSTREAM-ARCHITECTURE-RESEARCH.md):固定上游快照与平台能力盘点。
- [docs/history/README.md](docs/history/README.md):迁移期与已退役材料的只读归档(含 2026-10-30 归档的 `FEISHU-BOT-GUIDE.md` 与 cc-connect 隐窗脚本)。
- [docs/EVENT-CONTRACTS.md](docs/EVENT-CONTRACTS.md):2026-08-01 的迁移期目标事件契约(历史);现役运行生命周期以 ADR-0002、`RUN-CONTRACT.md` 和代码为准。
