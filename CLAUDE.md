# 与本仓库协作的约定

- **语言:始终用中文和我沟通**(所有回复、总结、说明都用中文)。代码注释沿用文件已有风格。

## 文档与版本真身

- 当前大版本为 **AI Resume v2**;版本号唯一真身是 `csharp/Directory.Build.props` 的 `<Version>`,三个项目都从它继承。禁止在别处硬编码版本;面板铭牌上的 BUILD 读的就是它。
- `README.md` 负责用户使用/安装,`docs/ARCHITECTURE.md` 负责现役机制与完整配置,`AI_GUIDE.md` 负责飞书只读问答,`docs/LESSONS.md` 只放历史教训与仍有效的工程经验。
- 跨 provider、会话 schema、GUI 流程、部署或安全边界的大改,必须同步上述受影响文档并刷新 `AI_GUIDE.md` 第一行的 project-tour 时间标记;历史说法不得继续冒充现役行为。
- **现役实现是 `csharp/` 下的 C# 实现**(v1 的 PowerShell + Node 运行时已于 2026-08-08 删除,查阅走 git 历史)。现役机制与门禁分别以 `docs/ARCHITECTURE.md`、`docs/MIGRATION-PROGRESS.md` 和 `AI_GUIDE.md` 为准;ADR-0003 修订 ADR-0001 的 cc-connect 边界,AI Resume 自己启动的进程仍遵循 ADR-0002 + `docs/RUN-CONTRACT.md`。`RECOVERY-AUDIT`、`MIGRATION-BASELINE`、`STATE-OWNERSHIP`、`MIGRATION-DEBT` 和 `STAGE-*` 都是历史迁移证据,不得当成当前运行说明。

## 现役架构

- `cc-connect` **直接运行,不再由 wrapper 包装**(ADR-0003):它负责飞书/多平台协议、会话编排与持久化、agent 与 turn 生命周期、停止(`/stop` 经 bridge)、cron、崩溃恢复、Web admin;`lark-cli` 与官方 `lark-*` Skills 是飞书 OpenAPI 能力层。**核心原则:接受 cc-connect 的用法约定,适配而非改造**——用法差异(`send` 不承载停止、provider 切换走管理 API、配置变更需重启而非 reload)一律适配。
- **AI Resume 只做四件事**(ADR-0003 §2.2):① Claude 限额后自动续跑编排(唯一不可替代的核心——cc-connect 只读取 `LimitReached`,不做排队续跑);② 动态项目发现;③ 本地完成通知(可配置注册表);④ Windows 控制面 GUI(AI Resume 退化后 GUI 即主要用户界面,质量即产品质量)。限额数据**自行获取**(ADR-0003 §2.3 已按证据推翻原「消费 cc-connect `UsageReport`」的判断:它依赖 `creack/pty`,`pty_unsupported.go` 的构建约束命中 Windows,管理 API 也没有 usage 端点)。取数主路径是官方 `GET https://api.anthropic.com/api/oauth/usage`,复用 Claude Code 已有的 OAuth token(`%USERPROFILE%\.claude\.credentials.json`),**只读、绝不刷新、绝不写回**——刷新会与 Claude Code 争用 refresh token;token 剩余寿命 < 60 秒视同过期。失败才降级到 `ClaudeCodeProbe` 子进程探测。
- 飞书长连接是**集群模式**:同一个应用有两个进程连着时事件会被随机投给其中一个,表现为「机器人时灵时不灵」。因此本机**只能有一个 cc-connect 消费者**,启动前走单消费者预检。
- **cc-connect 配置激活契约**:GUI 的「生成并重启 cc-connect」必须跨窗口串行化,操作中禁止正常关窗。先在 `config.toml` 同目录生成候选,用上游 `config format --config <副本>` 验证,再核验 `daemon.json` 的 `work_dir` / `binary_path`、生产与候选 `[management]` 完全一致且含 token、当前管理 API 可达且版本固定为 v1.4.1、锁 PID;计划任务必须是根路径唯一同名任务,action/脚本精确匹配,账号/触发器账号 SID 等于当前用户,principal 为 S4U+Limited,settings 为 PT0S、两项电池停机均关闭、RestartCount=3/RestartInterval=PT1M、IgnoreNew,且只有一个已启用的登录触发器按 PT5M 无限重复。计划任务脚本必须设置 `AI_RESUME_INTERNAL_RUN=1`;生成配置还必须在 `projects.agent.options.env` 写入同一标记并保留用户其它环境变量,这样 daemon 进程树和每个上游 agent 子进程都有独立证据。两次单消费者检查只豁免当前锁 PID,旧 `feishu-agent.js` 与 `feishu-launch.vbs` 都是冲突。随后核对生产文件哈希并原子提交候选,通过 `POST http://127.0.0.1:<port>/api/v1/restart` 让上游在 S4U 安全上下文内执行 `Engine.Stop`、关闭平台/agent 会话并自拉新 OS 进程;禁止调用裸 `daemon restart` 或 `restart --force`,也禁止由交互式 GUI 检查/强杀 S4U daemon 进程。HTTP 结果必须区分 accepted/rejected/unknown,连接中断先观察锁/API/日志换代,不得直接当成未执行。成功必须同时证明锁 PID 变化、本次操作内的新启动时间、目标 project+agent、同一稳定 PID 代次内出现 config loaded / Feishu ready / running,并验证更晚 LastRunTime 的新任务实例、任务直接拥有新 PID,或重启前已存在的 watchdog;rearm 后必须再探测管理 API,确认仍是同一 PID/version/agent。日志证据不得跨 PID 拼接,每条启动 marker 必须不早于本次请求与当前 PID 写入锁文件的时间。未验证时仅在生产文件仍精确等于本次提交字节时回滚,不得覆盖外部写入。provider/模型/TOML 语义字符串不得 Trim,名称、值、`agent_types` 与各 agent map 键遵循上游大小写敏感和同名 last-wins;Tomlyn 必须兼容表数组与内联数组;明确的 `/anthropic` 有效端点不得引用给 Codex(除非存在 Codex endpoint override);缺当前 agent 模型候选时只补 `[[providers.agent_model_lists.<agent>]]`,不得扩展封闭的内联表或覆盖用户列表。只有官方 OpenAI 端点的当前 Codex 家族可自动补默认值+Sol/Terra/Luna;第三方 relay 只补有效默认值或保留其显式列表。生成的 Codex alias 必须以 `[AI Resume] ` 提供可跨上游 CRUD TOML 重编码的所有权证据;`config format` 本身保留注释,无标记列表不得迁移。唯一兼容 provider 自动写入活动选择,多候选不猜。项目顶层、agent、agent options 与自定义子表必须按原层级保留;合法尾注释、引号式表头、引号式 owned 赋值键和缩进的后续全局表必须与 Tomlyn 解析一致;切 agent 时通过 Tomlyn 结构树发现任何 `agent.providers` 都必须失败关闭,不得静默带入新 agent。任何一项不明都不得报成功。
- **agent 切换后的会话语义以上游为准**:cc-connect v1.4.1 在新 Engine 创建时调用 `sessions.InvalidateForAgent`,清空旧 agent 的原生 session ID 并更新会话 `AgentType`;因此验证重启成功后无需为了 agent 生效强制 `/new`。但 `Session.ActiveProvider` 不会随之清空:同名 provider 若仍适用于新 agent,可能被会话恢复;用 `/provider switch` 修改,只有需要全新上下文时才用 `/new`。
- 新增通用飞书消息/文档/日历/任务/OpenAPI 能力前,必须先检查官方 lark-cli 与对应 Skill;已有官方命令时优先调用/封装它,不得继续手写同类 SDK 请求。入站事件编排、AI Resume 自有状态和安全边界不因使用 CLI 自动消失。
- `lark-*` Skills 只使用用户级 `%USERPROFILE%\.agents\skills` 真身及现有 Codex/Claude Code/Cline/Copilot 桥接,禁止复制进仓库。lark-cli 的用户/bot 身份、scope、risk level、结构化错误和高风险确认契约必须原样保留。
- 固定上游快照与平台实证见 `docs/UPSTREAM-ARCHITECTURE-RESEARCH.md`;任何引入双写、wrapper→Go fork 或决定整体替换/回退的方案都必须先形成新 ADR 并经用户确认。
- C# Worker **只承担 AI Resume 自有职责**:续跑编排、项目发现、完成通知 outbox、进程监督(`ProcessSupervisor` 用于自己启动的进程)与 GUI 后端;**不镜像 cc-connect 内部的会话/授权/turn 状态机**。ADR-0002 的 RunContract 只适用于 AI Resume 自己启动的进程(续跑、探测)。AI 生成和健康探测不设客户端总时限,静默指标不得触发失败。

## 部署(C# 控制面与续跑引擎)

v1 的 PowerShell + Node 运行时(仓库根的 `src/` 与 `test/`)已于 2026-08-08 从工作树删除;
需要查阅时用 `git log -- src test` 或检出 `059c46f` 之前的提交。现役实现全部在 `csharp/` 下。

改完 `csharp/` 里的任何代码,**线上不会自动生效**——运行的是安装目录里的副本:

```powershell
dotnet build csharp\AiResume.sln -c Release
csharp\src\AiResume.Worker\bin\Release\net10.0-windows\AiResume.Worker.exe install
```

`install` 把三个项目(Gui / Worker / Hook)的产物合并复制到 `%LOCALAPPDATA%\AI Resume\`,
重建桌面/开始菜单/开机自启入口,并按持久化的通知意图(`ProductConfig.NotifySources`)
对账重建五个完成通知钩子。安装必须先 staging 校验并备份将覆盖的运行文件,之后才停止旧 Worker；
新 Worker 必须存活且 Named Pipe pong 的 PID 精确等于本次启动 PID。回滚不完整时保留 staging/backup,
禁止在 finally 删除唯一恢复材料。通知源原位刷新,失败项仍保留在持久意图中供下次重试。
**通知源未能对齐时返回 2,Worker 未启动时返回 3,回滚不完整时返回 4,均不得当成功处理。**

`uninstall` 从安装目录执行时必须经清单约束的临时 Worker 完成:helper 先关闭快捷方式与钩子源,再把全部 payload/marker/manifest 事务性移动到 temp 私有退役区,完成后才返回成功；失败原路恢复,恢复不完整保留 helper 目录。无信号、坏信号或 helper 提前退出时,父进程只能报告恢复材料绝对路径,禁止清理 helper 目录。成功后的清理只碰退役区,不得在父进程返回后再触碰安装目录,否则会与立即重装竞态。`state\`(内含 DPAPI 加密的飞书凭据与运行数据库)和未知文件必须保留；未知文件存在时写精确 preserved-root marker,只允许后续重装,不得据此授权卸载。不会删除用户自己的 `settings.json`。

GUI 是 WPF + WebView2,前端在 `csharp/src/AiResume.Gui/wwwroot/`。
**已打开的窗口不会热更新**:部署后必须关掉旧窗口再打开。

## GUI 服务状态语义

- **界面上每一句肯定句都必须能被外部证伪(2026-08-08 第二轮审计后的硬规则)。** 判据不得停在"我们自己那一步":
  「已启用」要核对钩子命令指向的可执行文件是否存在(`HookHealth`),断链时标 `hookBroken` 并显红,不得继续显示成普通的"开";
  「已配置(飞书)」只代表 DPAPI 里有值,真实结论必须来自 `FeishuCredentialVerifier` 换一次 tenant_access_token,且**飞书把业务错误码放在 200 响应体里,只看 HTTP 状态会把 `code=10003` 读成成功**;
  「cc-connect 配置已生成」必须由 **cc-connect 自己的解析器**判定(`CcConnectConfigValidator` → `config format --config <副本>`;标志必须在子命令之后,前置 `--config` 会获取实例锁走启动路径;该命令会重写文件,只能校验副本);
  「监视中」必须同时满足 `config.Armed`、引擎进程存活与探测新鲜度(`EngineLiveness`),引擎不在时压过一切阶段文案显红。
  核对不出结论时一律显示「未核实」,**绝不显示「没问题」**。
- **通知源意图必须持久化。** `ProductConfig.NotifySources` 记录"用户想开哪几个",`install` 按 `NotifyIntent.Targets`(意图 ∪ 现状 ∩ 本机已装)对账重建,`uninstall` 关闭前先记录现状。只按现状恢复必然失败——卸载会把现状清空。安装未能对齐通知源时**不得返回 0**。
- OpenAI / DeepSeek / Claude 的绿色「可用」只能来自启动时或手动刷新的**真实最小请求成功**;API Key 已填写、CLI 命令存在只能说明“可探测”,绝不能显示成可用。
- **Codex 探测必须两步**:`/v1/models` 只证明服务端接受这把 key,不证明允许推理;之后再发一次 `max_tokens=1` 的最小推理请求。能列不能推理(`NoInference`)归红;端点不支持该形状(400/404/422)不得判成无权限,只能降级为"推理权限未核实"。
- **前端字号一律走 rem,1rem = 点阵字设计尺寸 12px。** 开机脚本按 `devicePixelRatio` 把 `html` 的基准挪到"乘上缩放率正好是 12 的整数倍"的值(150% → 16px);字距用 rem 折算的整数设计像素;`font-synthesis:none` 与 `-webkit-font-smoothing:none` 是点阵字清晰的前提,不得移除。布局尺寸仍用 px,不随字号缩放。
- Claude 探测必须区分未登录、订阅/额度、网络/超时、模型不可用和未安装;探测失败时额度区不得回退成「空闲」。
- OAuth usage 请求必须复现 Claude Code 的协议形状:`Accept: application/json`、`anthropic-beta: oauth-2025-04-20`、`anthropic-version: 2023-06-01` 与 `User-Agent: claude-code/<本机版本>`;普通 UA 会进入更激进的 429 桶。解析逐字段优先使用现代 `limits` 的 `session` / `weekly_all` / `weekly_scoped`,兼容旧顶层窗口。权威额度快照在后台请求中延迟写入 SQLite `quota_snapshots`,不得阻塞 WPF 首帧;以 `organizationUuid` 的不可逆账号指纹隔离,缺失时才退回 token 指纹。跨进程更新必须在同一个 SQLite `IMMEDIATE` 事务内重读、合并并写回,禁止锁外读旧快照后整体覆盖。OAuth 与 CLI 都按稀疏观测处理:当前明确字段优先,缺字段/缺窗口不是 tombstone;仅在同账号、同稳定窗口身份、同一未来 `resetAtUnix` 周期内承接并标记“最近服务端读数”。主窗口身份是协议 kind;scoped 身份是规范化完整 scope 的 SHA-256,不能按显示名或数组序号猜。账号变化、身份变化、reset 换代或到期立即失效;纯承接值不得渲染成绿色实时正常。详细协议、故障边界和真机验证手册见 `docs/CLAUDE-QUOTA-ACQUISITION.md`。
- GUI 的 provider 行由现役 C# `CodexProbe` / `DeepSeekProbe` 产生:Codex 只有完成相应真实校验后才能绿,DeepSeek 只有余额接口成功后才能绿;认证、余额不足和网络错误必须分别表达。provider 可用性与 Claude 额度是两套证据,不得相互代替。正式任务一旦可能产生副作用,不得在失败时换 provider 自动重放。
- 现役 GUI 只提供 `AiResume.Gui.exe --screenshot <png>` 离屏证据入口;公开截图必须使用合成数据,不得上传真实项目名、app_id 或本机路径。改服务状态逻辑后必须跑 `dotnet test`,并在部署后的真实窗口核验状态行。

## 自测(改完必跑)

```powershell
dotnet test csharp\AiResume.sln
```

全量 xUnit 门禁约 30 秒;用例数以当次 `dotnet test` 输出和 `AI_GUIDE.md` 的最新记录为准,不在规则层硬编码。红线:**绝不**对真实项目或真实会话启动 AI 运行,
不碰 `~/.claude`、`~/.codex`、`~/.qoder`、`~/.config/opencode` 和 `%LOCALAPPDATA%\AI Resume\state`,
不发任何付费 API 请求。探测判定一律对**录下来的真实响应**断言,不对臆想的结构断言——
mock 猜错结构的结果是测试全绿、线上静默失效(已实测过一次)。

- **临时目录只能经 `TestTemp` 申请**(`NewDir` / `NewFile` / `NewPath`)。
  直接用 `Path.GetTempPath()` 会在用户的 `%TEMP%` 里堆垃圾——2026-08-08 数出来 1499 个。
  需要「这个目录不存在」当前提的用例必须用 `NewPath`,`NewDir` 会把前提创没了。
- 界面证据:`AiResume.Gui.exe --screenshot <png>` 离屏截图(等 20 秒待数据填充)。
  要产出**公开可用**的截图必须喂合成数据,真机数据里有用户的项目名与 app_id。
- 改了通知钩子/安装流程,除单测外还要真机跑一次 `install` 并核对
  `AiResume.Worker.exe notify list` 的「可送达」列全为 True、退出码为 0。

## 测试红线(真踩过的事故,绝不重犯)

- **测试绝不能对真实项目/真实会话启动任何 AI 修改运行**——曾经一个测试 resume 了真实会话,AI 带着旧上下文执行并 push 了 commit;"事后停止"拦不住。要么用不存在的假会话 id,要么设 `FEISHU_TEST_NO_AI=1`。
- **mock 必须照抄线上 API 的真实返回结构**(如 `im.image.create` 返回顶层 `{image_key}`,无 `data` 外壳)——mock 猜错结构 = 测试全绿但线上静默失效。拿不准就真调一次打印 `Object.keys()`。
- 现役 RunContract 对 chat/query/modify/resume/probe 都不设客户端总时限,统一使用 Start/Status/Cancel。只有结构化 HTTP 408/504 或 `gateway_timeout` 是 `failed_provider`;DNS/TCP/TLS/reset、进程消失、解析和监控异常是 `failed_local`;用户停止为 `cancelled` 且不得 fallback。`heartbeatAt`、`lastOutputAt`、`silentSeconds` 只是指标。
- AI Resume 自己启动的进程必须先把 PID、创建时间、命令签名、Job 与 `childPending` 写入 SQLite `process_registry`;登记失败立即拒绝/终止。只有身份完整匹配才允许终止,未知不得当 gone。真实 close/error 前保持 runKey,只有 `childPending=false` 后才释放并清理登记。
- 布防周期、项目状态和额度快照由 SQLite/WAL 与 `ProductConfigStore` 的锁内读改写持久化;跨进程更新必须在事务/锁内重读最新值后合并。解除布防只停止后台自动续跑,不影响 cc-connect 中由用户发起的聊天任务。

## 安全约束

- 飞书 `allow_from` 必须非空;GUI、DPAPI 存储、配置生成和校验均 fail-closed。生成配置把同一授权名单写入每个项目的 `admin_from`;现役没有 viewer/陌生人只读三态,不得引用未接线的 `CcConnectAuthMapper` 作为生产边界。
- 飞书 app secret 由 `%LOCALAPPDATA%\AI Resume\state\secrets` 下的 DPAPI 存储持有;Claude/Codex 等 provider 凭据继续由各自上游认证文件或环境变量持有。任何令牌、密码、私钥都不得进入仓库、日志、截图、TOML 脱敏输出或测试夹具。
- 修改飞书授权、项目管理员或 provider 配置时必须保持 fail-closed;无法证明授权名单有效时拒绝生成/激活,不能退回“空名单放行所有人”。

## 会话生命周期

- 现役飞书会话由 cc-connect v1.4.1 管理;GUI 尚未提供会话归档/删除窗口。
- `CcConnectSessionBridge` 与 `CcConnectAuthMapper` 当前仅有研究性实现和单元测试,没有生产调用方;接线前必须重新盘点上游接口、形成明确产品行为并补真实集成验证,不得把其 14/30 天策略或 owner/viewer 模型写成现役事实。
