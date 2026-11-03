# 与本仓库协作的约定

- **语言:始终用中文和我沟通**(所有回复、总结、说明都用中文)。代码注释沿用文件已有风格。

## 文档与版本真身

- 当前大版本为 **AI Resume v2**(2026-10-30 起 2.2.x,决策见 ADR-0006);版本号唯一真身是 `csharp/Directory.Build.props` 的 `<Version>`,所有项目都从它继承。禁止在别处硬编码版本;面板铭牌上的版本读的就是它。
- `README.md` 负责用户使用/安装,`docs/ARCHITECTURE.md` 负责现役机制与完整配置,`AI_GUIDE.md` 负责 AI 只读问答导览,`docs/LESSONS.md` 只放历史教训与仍有效的工程经验。
- 跨 provider、会话 schema、GUI 流程、部署或安全边界的大改,必须同步上述受影响文档并刷新 `AI_GUIDE.md` 第一行的 project-tour 时间标记;历史说法不得继续冒充现役行为。
- **现役实现是 `csharp/` 下的 C# 实现**(v1 的 PowerShell + Node 运行时已于 2026-08-08 删除,查阅走 git 历史)。现役机制与门禁分别以 `docs/ARCHITECTURE.md`、`docs/CLAUDE-QUOTA-ACQUISITION.md`、`docs/COMPLETION-NOTIFICATIONS.md` 和 `AI_GUIDE.md` 为准;AI Resume 自己启动的进程遵循 ADR-0002 + `docs/RUN-CONTRACT.md`。`RECOVERY-AUDIT`、`MIGRATION-*`、`STATE-OWNERSHIP`、`STAGE-*`、`AUDIT-*`、`design/`、`evidence/`、`FEISHU-BOT-GUIDE` 等历史材料已全部移入 `docs/history/`,不得当成当前运行说明。
- **cc-connect 已于 2026-10-30 整体退役(ADR-0006)**:AI Resume 不再生成、激活、重启或检查 cc-connect,也不再有飞书聊天、会话或授权名单;飞书只用于完成通知。ADR-0001/0003 中 cc-connect 相关内容、ADR-0005 的 A1(setup-token)与 E1 版本固定只作历史记录。

## 现役架构

- **AI Resume 只做五件事**:① 按供应商证据进行限额后**逐项目**自动续跑编排(唯一不可替代的核心);② 动态项目发现;③ 本地完成通知(可配置注册表,经官方 lark-cli 发飞书私信);④ Windows 控制面 GUI(GUI 即主要用户界面,质量即产品质量);⑤ 子代理发起前的额度预检(ADR-0008:判断在 Claude Code 进程内的 mod 里,AI Resume 做控制面与证据可视化)。手机远程交互交给 Claude Code 官方 Remote Control,AI Resume 不实现任何聊天通道。
- **限额数据自行获取**(上游 cc-connect 的 `GetUsage` 依赖 `creack/pty`,在 Windows 上不可用,结论见 `docs/UPSTREAM-ARCHITECTURE-RESEARCH.md`)。取数主路径是官方 `GET https://api.anthropic.com/api/oauth/usage`;凭据只来自 **Claude Code 自己的登录**(`%USERPROFILE%\.claude\.credentials.json`,或 `CLAUDE_CONFIG_DIR`),由 `ClaudeCredentialSource` **共享只读、绝不刷新、绝不写回**——refresh token 一次性轮换,任何外部刷新都会把用户登出;访问令牌由正在运行的官方 Claude Code(含 Remote Control 桥)轮换,登录本身(`refreshTokenExpiresAt`,约 30 天)只能由用户 `/login` 续期。v2.1 的 `claude setup-token` 长期令牌已删除(usage 端点不接受它),`install` 会删掉旧的 `state\secrets\claude-oauth-token.bin`。判定 `ClaudeLoginVerdict`:Ok / TokenExpired / LoginExpiring(≤3 天)/ LoginExpired / LoggedOut / StoreMoved(Windows 凭据管理器灰度)/ Unverified(登录文件在但读不出或解析不了,多半正被改写:判断不了,绝不说成未登录,原因码 `unreadable`);`ClaudeLoginReminder` 在到期前 3 天、2 天、1 天、12 小时与已过期各发一次飞书提醒,登录被 Claude Code 清空(到期时间随之消失)时每个本地日提醒一次,直到恢复。令牌绝不进入日志、RPC 回包或截图。失败显示未核实,允许同账号未过期历史读数按承接规则展示;现役生产刷新不降级为 AI 推理。
- **账号指纹只由账号材料派生**:Claude Code 全局配置(`<配置根>\.config.json` 非空时优先,否则 `~/.claude.json`;默认构造还交叉核对 `<配置根>\.claude.json`,不一致视为未核实)里 `oauthAccount` 的组织 + 账号 UUID 的不可逆哈希;读不到时身份为空、续跑等待,**绝不退回令牌指纹**(令牌约 8 小时轮换,v2.1 因此把同一账号反复判成「供应商已改变」;v2.1 的布防因此在升级后必须解除并重新布防)。
- **自动续跑额度入口由 `AddQuotaResumeAdmission` 统一注册 `ProviderResumeProbe` + `ProviderCatalogService`。** 布防从当前供应商实时目录**逐项目**选择实际模型 ID(手选 > 该会话最近实际用的模型 > 目标默认;`ProjectRef.ModelId/ModelName/ModelSource`),保存 `ResumeTarget`(供应商/账号/agent)与每个项目的原生会话 UUID;共用同一模型的项目共享一次额度观测、串行续跑。旧配置的空 `ProjectRef.ModelId` 沿用 `ResumeTarget.ModelId`。每个项目按四个维度判定(`ProjectAdmission`):义务(会话转录尾部最近一条真实应答或限流记录——限流以外的合成错误记录跳过,续的那一下失败了不等于续上了)、额度(按该项目的模型)、占用(`~/.claude/sessions/<pid>.json`;只有「活着的进程比登记的启动得晚」才算 PID 复用,字段语义变了就判未核实,绝不把开着的会话当空闲)、全局进程门禁;任何未知只导致等待。**启动前的失败(会话刚被打开、令牌剩余 < 45 分钟、执行条件未核实、配置刚变化)只记 `not-started:<原因>` 并跳过本轮,不锁周期**(不会自己好的原因连续 3 次发一次飞书提醒);进程启动后的失败仍阻断自动重放。同一次中断(uuid 持久化于 `ConsumedInterruptions`)最多续一次;被打断后会话里已有新的真实应答、或中断后同目录新开的会话接着干了(首条记录晚于中断,或会话文件在中断之后才创建——分叉续写会复制旧记录),都视为已在别处继续,这次中断随即记为已处理,那个新会话之后被删或读不到也不回头续旧会话;判断不了(目录读不了、疑似新会话读不出归属)只等待。会话头部按行读、单行不设 64 KiB 上限(首条提问贴图时一行可达数 MB)。每个项目启动前最终门禁重读会话尾部与占用;门禁与在途存续只比较**这个项目自己的**执行身份(`ResumeTarget.ProjectIdentity`),别的项目增删不得杀掉正在跑的项目。「一跑就被限流」按项目计数,本轮跳过该项目及同模型的其它项目(它们凭的是同一份刚被推翻的额度证据),别的模型照常轮到,不得让排在队首的误判项目饿死别的项目。Claude 额度门禁只看目标模型相关窗口:5h/7d 主窗口(必须是本次实时读数且 0 ≤ % < 100)+ 同模型族的 weekly_scoped(认不出族的视为相关;同一重置周期的承接读数未满不阻塞);其它族的 scoped 用尽不挡本项目;禁止以 CLI/Haiku 成功代替目标模型恢复。打开着的交互会话交给 Claude Code 原生 `autoContinueAtUsageLimit`,AI Resume 不重复续跑,额度恢复 10 分钟后仍开着且未继续则飞书提醒一次。Codex 现役供应商是官方 ChatGPT/OpenAI 账号(`CodexAuthProbe.ReadOfficialCredentials` 只读 `~/.codex/auth.json`,不读也不改 `config.toml` 的第三方桥);面板读 `/backend-api/codex/models` 与 `/backend-api/wham/usage`,放行由 `ProviderCatalogService.EvaluateChatGpt` 判定(账号主/次窗口全部 allowed 且 0 ≤ percent < 100、无附加限额或 `spend_control` 命中、目标模型未在 `model_usage` 标为不可用 → ready;任一限额命中 → limited;无数据或缺百分比 → unknown 等待)。`config.toml` 显式配置的非官方、非回环第三方 provider 只作附加行。DeepSeek 行只读 **OpenCode** 的 DeepSeek 密钥(OpenCode 2.x 把凭据存在 `opencode.db`(`OPENCODE_DB` 可改)的 `credential` 表,`auth.json` 只在首次启动时导入一次;只读库:库里没有 DeepSeek 就是 OpenCode 没存、不再拿 `auth.json` 的旧密钥,库读不了显示未核实,只有库或表根本不存在才看 `auth.json`;端点按 OpenCode 2.x 的配置层合并后判断:`OPENCODE_CONFIG_DIR`(否则 XDG)下的 opencode.json/.jsonc → `OPENCODE_CONFIG` → `OPENCODE_CONFIG_CONTENT` → `%ProgramData%\opencode` 托管层,1.x 的 config.json 不读)、只展示余额,`resumable=false`;AI Resume 不保存任何 DeepSeek key。读别人的凭据/配置文件一律共享读写与删除打开。未知、失效、账号变化或不支持的认证/配置保持未核实;不自动换供应商重放。运行器通过受监督的 `internal-agent-run` helper 使用 ArgumentList 与精确 UUID;Claude 用原生 host-provider 能力固定连接并注入当前访问令牌(剩余寿命须 ≥ 45 分钟),组织/动态管理约束无法核实时拒绝;Codex 官方账号用 `-c model_provider="openai" -c openai_base_url=<官方端点>` 覆盖、由 Codex 自己的 `auth.json` 提供凭据;Codex 静态第三方保留原 provider ID,用 CLI 覆盖连接字段、环境承载凭据。Codex 错误 JSONL 不含结构化限流码,未知/错误/重路由/损坏输出均阻断自动重放。详见 ADR-0004 与 ADR-0006。
- **子代理额度预检(ADR-0008)**:mod 本体在 `mods/airesume-budget-gate/`(TypeScript,经 Worker csproj 进安装 payload,装到 `%LOCALAPPDATA%\AI Resume\mods\airesume-budget-gate`),`install` 在用户级 `settings.json` 的 `env.CLAUDE_CODE_PLUGIN_DIRS` 里**只增删我方条目**(末级目录名 `airesume-budget-gate` 的都算我方),没对齐返回 2;`uninstall` 在拆任何入口之前先撤下登记(撤不下就中止卸载,什么都不动)。**组织托管策略 `disableSideloadFlags` 为 true 时,只要这个变量非空,所有新启动的 Claude Code 会话都会直接退出**:登记前与 Worker 每小时都查托管设置文件与注册表策略(`SideloadPolicy`),禁止或读不出就不登记、已有的撤下。模式意图持久化在 `ProductConfig.BudgetGateMode` / `BudgetGateConfigVersion` 并写 `state\budget-gate\config.json`:install 后默认 `shadow`(照常判断、一律放行),「已关闭」即撤下登记,`enforce`(推迟派发、软着陆、重置后补派提醒)**只能由用户在界面上勾选确认后打开**,install 从不默认它。mod 只用 Claude Code 给的额度读数,绝不调用 `$.session.authorize`、不读写任何凭据;按模型族的周额度来自 `QuotaService` 导出的只读 `quota-snapshot.json`(只挑字段,不含指纹与账号标识,承接值不导出)。mod 自身出错一律放行;日志与心跳只记计量、读数与判断,提示与描述只留长度和加盐哈希。界面「生效中」只认 3 分钟内的全部新鲜心跳都回报当前模式、配置版本与已装 mod 版本;只有一部分跟上是「部分生效」,只装好没有会话加载时是「等新会话」。**Desktop 是 MSIX 包**:它拉起的进程在 `%LOCALAPPDATA%` 下新建顶层目录会被重定向进包私有目录,包外看不到,所以 mod 只往 AI Resume 已建好的 `state` 里写,绝不自己创建 `AI Resume`。
- 新增通用飞书消息/文档/日历/任务/OpenAPI 能力前,必须先检查官方 lark-cli 与对应 Skill;已有官方命令时优先调用/封装它,不得手写同类 SDK 请求。lark-cli 的 bot 身份、scope、结构化错误(stdout **与 stderr** 两种信封)与高风险确认契约必须原样保留。`lark-*` Skills 只使用用户级 `%USERPROFILE%\.agents\skills` 真身及现有桥接,禁止复制进仓库。
- **lark-cli 只能直接启动真正的 `lark-cli.exe`(先找 `%APPDATA%\npm` 下 npm 包内的 `node_modules/@larksuite/cli/bin/lark-cli.exe`,再按 PATH 的绝对路径条目找包内 exe 或独立 `lark-cli.exe`;相对路径条目一律跳过,子进程环境里有 App Secret),绝不经 `cmd.exe /c` 或 `lark-cli.cmd` 垫片**:cmd 会在换行处截断多行参数,`--idempotency-key` 因此从未送达(2026-10-30 实证)。子进程设 `LARKSUITE_CLI_NO_UPDATE_NOTIFIER` / `LARKSUITE_CLI_NO_SKILLS_NOTIFIER`,stdout/stderr 用 UTF-8。
- 固定上游快照与平台实证见 `docs/UPSTREAM-ARCHITECTURE-RESEARCH.md`;任何引入双写、fork 上游或整体替换/回退的方案都必须先形成新 ADR 并经用户确认。
- C# Worker **只承担 AI Resume 自有职责**:续跑编排、项目发现、完成通知 outbox 与投递、登录到期提醒、进程监督(`ProcessSupervisor` 只用于自己启动的进程)与 GUI 后端;不镜像 Claude Code / OpenCode 内部的会话状态机,只读它们公开落盘的产物(会话登记、转录、日志)。ADR-0002 的 RunContract 只适用于 AI Resume 自己启动的进程(续跑、探测)。AI 生成和健康探测不设客户端总时限,静默指标不得触发失败。

## 部署(C# 控制面与续跑引擎)

v1 的 PowerShell + Node 运行时(仓库根的 `src/` 与 `test/`)已于 2026-08-08 从工作树删除;
需要查阅时用 `git log -- src test` 或检出 `059c46f` 之前的提交。现役实现全部在 `csharp/` 下。Worker 现役命令只有 `install / uninstall / shortcuts / notify [list|check|test|enable <源>|disable <源>]`(另有内部 `internal-agent-run`);v1 迁移命令与 v2.1 的 `feishu-check` / `cc-connect-apply` / `preflight` 已删除。不带子命令才以常驻服务启动,未知子命令返回 2。

改完 `csharp/` 里的任何代码,**线上不会自动生效**——运行的是安装目录里的副本:

```powershell
dotnet build csharp\AiResume.sln -c Release
csharp\src\AiResume.Worker\bin\Release\net10.0-windows\AiResume.Worker.exe install
```

`install` 把四个项目(Gui / Worker / Hook / Launcher)的产物合并复制到 `%LOCALAPPDATA%\AI Resume\`,
重建桌面/开始菜单/开机自启入口,并按持久化的通知意图(`ProductConfig.NotifySources`)
对账重建五个完成通知钩子。同一规范化目标目录的完整安装事务必须由目标哈希命名互斥体跨进程串行化;安装必须先 staging 校验并备份将覆盖的运行文件,之后才停止旧 Worker;
安装还必须冻结 staging 的逐文件 SHA-256,提交只复制冻结清单并逐项复核;后台 Worker 不得继承 install 调用方的 stdout/stderr 重定向句柄。新 Worker 必须存活且 Named Pipe pong 的 PID 精确等于本次启动 PID。回滚不完整时保留 staging/backup,
禁止在 finally 删除唯一恢复材料。通知源原位刷新,失败项仍保留在持久意图中供下次重试。
**通知源或子代理预检登记未能对齐时返回 2,Worker 未启动时返回 3,回滚不完整时返回 4,均不得当成功处理。**

`uninstall` 从安装目录执行时必须经清单约束的临时 Worker 完成:helper 先关闭快捷方式与钩子源,再把全部 payload/marker/manifest 事务性移动到 temp 私有退役区,完成后才返回成功;失败原路恢复,恢复不完整保留 helper 目录。无信号、坏信号或 helper 提前退出时,父进程只能报告恢复材料绝对路径,禁止清理 helper 目录。成功后的清理只碰退役区,不得在父进程返回后再触碰安装目录,否则会与立即重装竞态。`state\`(内含 DPAPI 加密的飞书机器人凭据与运行数据库)和未知文件必须保留;未知文件存在时写精确 preserved-root marker,只允许后续重装,不得据此授权卸载。不会删除用户自己的 `settings.json`。

GUI 是 WPF + WebView2,前端在 `csharp/src/AiResume.Gui/wwwroot/`。
**已打开的窗口不会热更新**:部署后必须关掉旧窗口再打开。

## GUI 服务状态语义

- **界面上每一句肯定句都必须能被外部证伪(2026-08-08 第二轮审计后的硬规则)。** 判据不得停在"我们自己那一步":
  通知源「hook 正常」要核对 hook 命令指向的可执行文件是否存在(`HookHealth`),断链时标 `hookBroken` 并显红「hook 失效」;OpenCode 的「插件已加载」只以 OpenCode 自己的日志(`~/.local/share/opencode/log/opencode.log`,数据目录跟随 `XDG_DATA_HOME`)为证据,以插件写入之后的最后一条结论为准,没有记录就是「待核实」;
  飞书机器人「可送达」只来自投递健康文件里的**真实送达记录**,且必须确认后台 Worker 在跑(`workerRunning === true`;还没读到或核验不出都不行)、最近一次尝试成功、并核对得上那次成功之后没改过凭据/收件人/lark-cli(`settingsVerified === true`,比不出来同样只能「待核实」)——历史成功不证明现在送得到;机器人凭据只有真实向飞书换到 tenant_access_token、再以该令牌经 lark-cli 调通一次只读机器人接口后才保存(ADR-0007);
  「监视中」必须同时满足 `config.Armed`、引擎进程存活、探测新鲜度(`EngineLiveness`)与本用户常驻 Worker 应答 Named Pipe:引擎不在时压过一切阶段文案显红,有同名进程却不应答显琥珀「监视状态未核实」(`WorkerUnverified`)。
  核对不出结论时一律显示「未核实」,**绝不显示「没问题」**。
- 收进菜单的东西(Codex 额度、Codex/Cline/Qoder 通知源、供应商明细)若处于启用或故障状态,主界面必须留一句提示,隐藏不得掩盖启用态或断链。
- **通知源意图必须持久化。** `ProductConfig.NotifySources` 记录"用户想开哪几个",`install` 按 `NotifyIntent.Targets`(意图 ∪ 现状 ∩ 本机已装)对账重建,`uninstall` 关闭前先记录现状。只按现状恢复必然失败——卸载会把现状清空。探测失败(`Unverified`,开没开不知道)的源不改写它的配置、意图原样保留,install 按未对齐返回。安装未能对齐通知源时**不得返回 0**。`notify enable/disable` 与界面开关必须先写意图、再改客户端配置(`NotifyIntent.Apply`):意图也是投递端的来源过滤,写不进去就不能改开关;改客户端失败时再探一次实际状态,只有证明这次改动根本没生效才把这一项意图恢复原样,已部分生效(如新插件已写好、清理旧文件失败)或探不出时保留这次的选择——否则插件在跑、意图却被改回「关」,事件会被投递端全部丢弃。
- Claude Code 钩子写在 `~/.claude/settings.json`(与 Claude Code 一致跟随 `CLAUDE_CONFIG_DIR`),用官方 exec form(`command` 只放 `AiResume.Hook.exe` 绝对路径,`args` 放参数),三组:`Stop`、`StopFailure`(`--kind=failure`)、`Notification`(`--kind=decision`,matcher 见 `ClaudeCodeNotificationAdapter.DecisionMatcher`);只增删我方条目,混在用户分组里的我方条目移出单独成组,不改写用户分组的 matcher。Claude Code 热加载 settings。
- Codex 完成通知走 0.148.0 hooks(ADR-0005 C1):`~/.codex/hooks.json` 的 `hooks.Stop` 末尾追加 AI Resume 自己的分组,`config.toml` 写 `[hooks.state."<hooks.json 全路径>:stop:<g>:<h>"].trusted_hash`(与上游 canonical JSON SHA-256 一致);只增删自己的分组和哈希等于我方命令哈希的信任记录,旧 `notify` 残留只提示不改写。已打开的 Codex 会话不重载 hooks.json,新会话才生效;信任不匹配时 `notify list` 标 hookBroken 并提示在 codex `/hooks` 信任。
- OpenCode 只支持 2.x:写官方 v2 目录形态插件 `~/.config/opencode/plugins/airesume-notify/index.ts`(配置目录跟随 `XDG_CONFIG_HOME`);拒绝覆盖别人的 `index.*`/`server.*`/`package.json`,迁移旧单文件插件时留 `.bak`。
- **完成通知投递**:Worker(S4U 计划任务、会话 0)读 GUI 保存的**机器范围** DPAPI 机器人凭据 `state\secrets\feishu-bot.bin`(ACL 收紧到当前用户 + SYSTEM + Administrators),由 `FeishuTenantToken` 向飞书换 tenant_access_token(只换令牌、只用于 bot;令牌只在进程内存按过期时间缓存),只把 `LARKSUITE_CLI_APP_ID` + `LARKSUITE_CLI_TENANT_ACCESS_TOKEN`(及 BRAND/STRICT_MODE)经环境变量交给 lark-cli(先删掉继承来的全部凭据类变量,并让它用 AI Resume 自有的空配置目录 `state\lark-cli\`,碰不到用户配置里引用的钥匙串)——lark-cli 的 env 来源只收现成令牌、不会拿 App Secret 去换(ADR-0007 实验查证);换令牌失败(已知凭据错误码被拒 `auth_config`、网络与 5xx `network`/`network_timeout`、限流、应答不是预期结果 `token_unexpected`)或副本解不开(`credential_unreadable`)时不启动 lark-cli、**绝不**退回把 App Secret 塞进环境变量;换令牌请求不跟随重定向,代理只认 `HTTPS_PROXY`/`NO_PROXY`(与 Go 一致),不落回系统代理;保存前的核验还要求机器人 `activate_status` 为启用;没有副本时退回 lark-cli 自己的钥匙串(S4U 下会间歇失败,界面标琥珀)。S4U 会话 0 下解 **CurrentUser** DPAPI 会间歇失败数小时(0x8009000B,2026-10 两次实证),Worker 路径不得依赖任何 CurrentUser 密文——包括第三方 CLI 自己的。收件人 `open_id` 明文存 `ProductConfig.NotifyRecipientOpenId`,由「飞书通知设置」识别(`lark-cli auth list`)或填写。投递失败按 `NotifyClassifier` 分级:单条失败只推迟该条,凭据/网络/限流熔断整个通道,凭据文件或收件人一变立即解除,永久错误进 `completion-events\dead\`;日志只记类别、错误码、log_id、退出码与耗时,不记正文与凭据。Claude Code 事件:稳定期后转录仍不存在(会话没落盘,如第三方工具以 `--no-session-persistence` 拉起的 claude)不发;用量上限中断(`StopFailure` rate_limit 与 `quota_auto_resume_disabled`)按转录里服务端给的 `rateLimitType + resetsAt`(加配置根)定限额周期,每个会话每个周期只提醒一次、同一周期同时待发的会话合成一条;续跑句只按投递那一刻的布防快照(`NotifyFacts`)说,引擎已知只会一直等(要重新布防、登录失效、监视卡住)时不说「会续跑」,读不出写「未核实」;会话名只进消息不进日志(`docs/COMPLETION-NOTIFICATIONS.md` §5.3、§5.7)。
- OpenAI / DeepSeek / Claude 的绿色「可用」必须来自启动时或手动刷新的**真实外部请求证据**;API Key 已填写、CLI 命令存在只能说明“可探测”,绝不能显示成可用。对配置了 CC Switch 风格用量查询的 Sub2API provider,成功返回、账户未显式失效且余额大于 0 的 `/v1/usage` 是其 provider/account 可用证据,可直接点绿;这不等于对每一次未来推理请求作绝对保证。
- **现役 GUI 供应商刷新使用零 token 的 `providers.catalog`。** Claude 读取 Models API 与 OAuth usage,DeepSeek 读取 Models API 与 balance(密钥来自 OpenCode),当前 Codex 第三方复用带凭据的 `/models` 与兼容 `/v1/usage`,ChatGPT 使用 `/backend-api/codex/models` 与 `/backend-api/wham/usage`。模型可列不等于额度可用;数据源失败分别降级,不保留旧绿。GUI 没有任何推理型探测。页面每块数据到了就各自绘制(额度磁贴不等最慢的供应商目录);目录在 `quota.get` 返回之后才读,Claude 行复用这一轮 1 分钟内的额度读数,一轮刷新只请求一次 usage 端点;登录状态在额度请求结束后再读一次,「被服务端拒绝」反映的是这一轮的结果。
- **前端字号一律走 rem,1rem = 点阵字设计尺寸 12px。** 开机脚本按 `devicePixelRatio` 把 `html` 的基准挪到"乘上缩放率正好是 12 的整数倍"的值(150% → 16px);字距用 rem 折算的整数设计像素;`font-synthesis:none` 与 `-webkit-font-smoothing:none` 是点阵字清晰的前提,不得移除。布局尺寸仍用 px,不随字号缩放;窄列放不下的文案用短句 + 悬停说明,窄磁贴用容器查询换短标题。WebView2 必须关闭浏览器加速键与缩放(`AreBrowserAcceleratorKeysEnabled` / `IsZoomControlEnabled`),否则 Ctrl± 会让点阵字失去整数倍。
- 页面的语义集中在 `index.html` 的 `<script id="vm">` 纯函数视图模型(`AR.*`),`GuiPageContractTests` 在 Node 里直接驱动它断言「无证据不绿」等语义;改界面语义必须改视图模型并补断言,不要把判断散落进渲染代码。
- Claude 登录状态必须区分有效、令牌待续期、即将到期、已过期、未登录与凭据位置变化;登录失效时额度区不得回退成「空闲」。
- OAuth usage 请求必须复现 Claude Code 的协议形状:`Accept: application/json`、`anthropic-beta: oauth-2025-04-20`、`anthropic-version: 2023-06-01` 与 `User-Agent: claude-code/<本机版本>`;普通 UA 会进入更激进的 429 桶。解析逐字段优先使用现代 `limits` 的 `session` / `weekly_all` / `weekly_scoped`,兼容旧顶层窗口。权威额度快照在后台请求中延迟写入 SQLite `quota_snapshots`,不得阻塞 WPF 首帧;以账号指纹隔离。跨进程更新必须在同一个 SQLite `IMMEDIATE` 事务内重读、合并并写回,禁止锁外读旧快照后整体覆盖。OAuth 与 CLI 都按稀疏观测处理:当前明确字段优先,缺字段/缺窗口不是 tombstone;仅在同账号、同稳定窗口身份、同一未来 `resetAtUnix` 周期内承接并标记“最近服务端读数”。主窗口身份是协议 kind;scoped 身份是规范化完整 scope 的 SHA-256,不能按显示名或数组序号猜。账号变化、身份变化、reset 换代或到期立即失效;纯承接值不得渲染成绿色实时正常。详细协议、故障边界和真机验证手册见 `docs/CLAUDE-QUOTA-ACQUISITION.md`。
- GUI 的 provider 行由 `ProviderCatalogService` 聚合,复用 `CodexAuthProbe` / `CodexBalanceProbe` / `DeepSeekProbe`。第三方 OpenAI-compatible provider 的余额按当前 Codex provider 只读请求 `/v1/usage`(根地址补 `/v1`,已含 `/v1` 不重复),兼容 CC Switch usage_script 的 `remaining` / `quota.remaining` / `balance`,默认 USD;成功、账户有效且余额大于 0 时按 CC Switch 语义直接显示绿色,余额为 0、账户失效、401/403、402/429 等明确失败优先。没有有效余额证据的 provider 显示额度未核实,不因目录成功而点绿。官方 OpenAI/ChatGPT 端点不请求该第三方路由,ChatGPT OAuth token 也不得发往额外余额接口;这不是 ChatGPT Plus/Pro 订阅额度。DeepSeek 只有余额接口成功后才能绿。任一探针异常只降级自身,不得拖垮整个 provider 面板。provider 可用性与 Claude 额度是两套证据,不得相互代替。正式任务一旦可能产生副作用,不得在失败时换 provider 自动重放。
- 现役 GUI 只提供 `AiResume.Gui.exe --screenshot <png>` 离屏证据入口(演示模式只回合成数据;`--screenshot-unarmed` 为未布防态);公开截图必须使用合成数据并把 `AIRESUME_SHADOW_DIR` 指向临时目录,不得上传真实项目名、app_id、open_id 或本机路径。改服务状态逻辑后必须跑 `dotnet test`,并在部署后的真实窗口核验状态行。

## 自测(改完必跑)

```powershell
dotnet test csharp\AiResume.sln
```

全量 xUnit 门禁约 40 秒;用例数以当次 `dotnet test` 输出和 `AI_GUIDE.md` 的最新记录为准,不在规则层硬编码。`GuiPageContractTests` 与 `OpenCodeNotificationAdapterTests` 需要本机 `node`。红线:**绝不**对真实项目或真实会话启动 AI 运行,
不碰 `~/.claude`、`~/.codex`、`~/.qoder`、`~/.config/opencode` 和 `%LOCALAPPDATA%\AI Resume\state`,
不发任何付费 API 请求。探测判定一律对**录下来的真实响应**断言,不对臆想的结构断言——
mock 猜错结构的结果是测试全绿、线上静默失效(已实测过两次:`im.image.create` 的外壳、lark-cli 写在 stderr 的错误信封)。

- **临时目录只能经 `TestTemp` 申请**(`NewDir` / `NewFile` / `NewPath`)。
  直接用 `Path.GetTempPath()` 会在用户的 `%TEMP%` 里堆垃圾——2026-08-08 数出来 1499 个。
  需要「这个目录不存在」当前提的用例必须用 `NewPath`,`NewDir` 会把前提创没了。
- 界面证据:`AiResume.Gui.exe --screenshot <png>` 离屏截图(演示模式约 2 秒后截取)。
  要产出**公开可用**的截图必须喂合成数据,真机数据里有用户的项目名与 app_id。
- 改了通知钩子/投递/安装流程,除单测外还要真机跑一次 `install`,核对 `AiResume.Worker.exe notify list` 的「可送达」列全为 True、退出码为 0,
  并用 `notify check`(副本路径:真实换令牌 + 机器人接口,再附 lark-cli doctor)与 `notify test`(冒烟事件走完整链路,等待真实送达)确认通道。

## 测试红线(真踩过的事故,绝不重犯)

- **测试绝不能对真实项目/真实会话启动任何 AI 修改运行**——曾经一个测试 resume 了真实会话,AI 带着旧上下文执行并 push 了 commit;"事后停止"拦不住。要么用不存在的假会话 id,要么注入假 `IClaudeResumeRunner` 与 `TestTemp` 下的合成会话目录;`FEISHU_TEST_NO_AI` 是 v1 Node 测试的开关,C# 代码里没有读者,不要依赖它。
- **mock 必须照抄线上 API 的真实返回结构**(如 `im.image.create` 返回顶层 `{image_key}`,无 `data` 外壳;lark-cli 失败时信封在 stderr)——mock 猜错结构 = 测试全绿但线上静默失效。拿不准就真调一次打印 `Object.keys()`。
- 现役 RunContract 对 chat/query/modify/resume/probe 都不设客户端总时限,统一使用 Start/Status/Cancel。只有结构化 HTTP 408/504 或 `gateway_timeout` 是 `failed_provider`;DNS/TCP/TLS/reset、进程消失、解析和监控异常是 `failed_local`;用户停止为 `cancelled` 且不得 fallback。`heartbeatAt`、`lastOutputAt`、`silentSeconds` 只是指标。
- AI Resume 自己启动的进程必须先把 PID、创建时间、命令签名、Job 与 `childPending` 写入 SQLite `process_registry`;登记失败立即拒绝/终止。只有身份完整匹配才允许终止,未知不得当 gone。真实 close/error 前保持 runKey,只有 `childPending=false` 后才释放并清理登记。
- 布防周期、项目状态(含 `ProjectNotes`、`ConsumedInterruptions`)和额度快照由 SQLite/WAL 与 `ProductConfigStore` 的锁内读改写持久化;跨进程更新必须在事务/锁内重读最新值后合并。解除布防只停止后台自动续跑,不影响用户自己在客户端里的会话。

## 安全约束

- 飞书机器人凭据(app_id + app_secret)只经 GUI「飞书通知设置」进入,真实换到 tenant_access_token 并以该令牌经 lark-cli 调通一次只读机器人接口后才以 DPAPI **LocalMachine** 范围存 `%LOCALAPPDATA%\AI Resume\state\secrets\feishu-bot.bin`,保存后把文件 ACL 收紧到当前用户 + SYSTEM + Administrators 并读回核验,收不紧就放弃保存;界面只回遮蔽后的 app_id,App Secret 实值只出现在用户输入那一刻、本进程调用栈与发往飞书官方换令牌端点的请求体里,绝不进 argv、日志、RPC 回包、截图或子进程环境;换来的 tenant_access_token 只在进程内存与 lark-cli 子进程环境变量里,不落盘。
- Claude/Codex/OpenCode 等 provider 凭据继续由各自上游认证文件或环境变量持有;AI Resume 只读,不复制、不保存。任何令牌、密码、私钥都不得进入仓库、日志、截图或测试夹具。
- 不读取、不改写也不删除 `~/.cc-connect` 等已退役组件的配置;其中的明文密钥由用户自行清理与吊销。

## 会话生命周期

- 会话属于各客户端(Claude Code / OpenCode / Codex)自己;AI Resume 只读它们公开落盘的产物:Claude Code 的 `~/.claude/projects/**/<会话 UUID>.jsonl` 转录(用量上限中断证据与最近使用的模型)和 `~/.claude/sessions/<pid>.json` 运行登记(占用判定;只有活着的进程比登记的启动得晚才算 PID 复用)。
- 续跑只用布防时绑定的精确会话 UUID(`internal-agent-run` 以 ArgumentList 传入),不新建会话、不改写转录;会话开在客户端时不续跑,交给客户端自己继续。
- 研究性 `CcConnectSessionBridge`、`CcConnectAuthMapper`、`CcConnectSupervisor` 已于 2026-10-12 删除,cc-connect 整体已于 2026-10-30 退役;如需会话归档/删除等能力,必须先盘点上游接口、形成新 ADR 并补真实集成验证。
