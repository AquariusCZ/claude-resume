# 与本仓库协作的约定

- **语言:始终用中文和我沟通**(所有回复、总结、说明都用中文)。代码注释沿用文件已有风格。

## 文档与版本真身

- 当前大版本为 **AI Resume v2**;版本号唯一真身是 `csharp/Directory.Build.props` 的 `<Version>`,三个项目都从它继承。禁止在别处硬编码版本;面板铭牌上的 BUILD 读的就是它。
- `README.md` 负责用户使用/安装,`docs/ARCHITECTURE.md` 负责现役机制与完整配置,`AI_GUIDE.md` 负责飞书只读问答,`docs/LESSONS.md` 只放历史教训与仍有效的工程经验。
- 跨 provider、会话 schema、GUI 流程、部署或安全边界的大改,必须同步上述受影响文档并刷新 `AI_GUIDE.md` 第一行的 project-tour 时间标记;历史说法不得继续冒充现役行为。
- **现役实现是 `csharp/` 下的 C# 实现**(v1 的 PowerShell + Node 运行时已于 2026-08-08 删除,查阅走 git 历史)。设计方向以 `docs/adr/0001-target-architecture.md` + **`docs/adr/0003-cc-connect-direct-and-control-plane.md`(修订 0001 的 cc-connect 边界,冲突处以 0003 为准)** 为准,AI 运行生命周期以 `docs/adr/0002-run-lifecycle-contract.md` + `docs/RUN-CONTRACT.md` 为准,恢复现场、阶段基线、状态所有权、事件契约、债务和当前门禁分别见 `docs/RECOVERY-AUDIT-20260801.md`、`docs/MIGRATION-BASELINE.md`、`docs/STATE-OWNERSHIP.md`、`docs/EVENT-CONTRACTS.md`、`docs/MIGRATION-DEBT.md`、`docs/STAGE-1-GATE.md`。

## 目标架构(已确认,渐进迁移)

- `cc-connect` **直接运行,不再由 wrapper 包装**(ADR-0003):它负责飞书/多平台协议、会话编排与持久化、agent 与 turn 生命周期、停止(`/stop` 经 bridge)、限额读取、cron、崩溃恢复、Web admin;`lark-cli` 与官方 `lark-*` Skills 是目标飞书 OpenAPI 能力层。**核心原则:接受 cc-connect 的用法约定,适配而非改造**——用法差异(`send` 不承载停止、provider 切换走管理 API、配置变更需重启而非 reload)一律适配。
- **AI Resume 只做四件事**(ADR-0003 §2.2):① Claude 限额后自动续跑编排(唯一不可替代的核心——cc-connect 只读取 `LimitReached`,不做排队续跑);② 动态项目发现;③ 本地完成通知(可配置注册表);④ Windows 控制面 GUI(AI Resume 退化后 GUI 即主要用户界面,质量即产品质量)。限额数据**自行获取**(ADR-0003 §2.3 已按证据推翻原「消费 cc-connect `UsageReport`」的判断:它依赖 `creack/pty`,`pty_unsupported.go` 的构建约束命中 Windows,管理 API 也没有 usage 端点)。取数主路径是官方 `GET https://api.anthropic.com/api/oauth/usage`,复用 Claude Code 已有的 OAuth token(`%USERPROFILE%\.claude\.credentials.json`),**只读、绝不刷新、绝不写回**——刷新会与 Claude Code 争用 refresh token;token 剩余寿命 < 60 秒视同过期。失败才降级到 `ClaudeCodeProbe` 子进程探测。
- 飞书长连接是**集群模式**:同一个应用有两个进程连着时事件会被随机投给其中一个,表现为「机器人时灵时不灵」。因此本机**只能有一个 cc-connect 消费者**,启动前走单消费者预检。
- 新增通用飞书消息/文档/日历/任务/OpenAPI 能力前,必须先检查官方 lark-cli 与对应 Skill;已有官方命令时优先调用/封装它,不得继续手写同类 SDK 请求。入站事件编排、AI Resume 自有状态和安全边界不因使用 CLI 自动消失。
- `lark-*` Skills 只使用用户级 `%USERPROFILE%\.agents\skills` 真身及现有 Codex/Claude Code/Cline/Copilot 桥接,禁止复制进仓库。lark-cli 的用户/bot 身份、scope、risk level、结构化错误和高风险确认契约必须原样保留。
- 迁移设计与固定上游快照见 `docs/UPSTREAM-ARCHITECTURE-RESEARCH.md`;任何改变该目标、引入双写、wrapper→Go fork 或决定整体替换/回退的方案都必须先形成新 ADR 并经用户确认。
- 目标 C# Worker **只承担 AI Resume 自有职责**(ADR-0003 §4 缩小了原范围):续跑编排、项目发现、完成通知 outbox、进程监督(`ProcessSupervisor` 用于自己启动的进程)与 GUI 后端;**不再镜像 cc-connect 内部的会话/授权/turn 状态机**。ADR-0002 的 RunContract 仍适用于 AI Resume 自己启动的进程(续跑、探测),不再要求映射 cc-connect turn。AI 生成和健康探测不设客户端总时限,每 15-30 秒读持久状态与进程存活性;静默指标不得触发失败。

## 部署(C# 控制面与续跑引擎)

v1 的 PowerShell + Node 运行时(仓库根的 `src/` 与 `test/`)已于 2026-08-08 从工作树删除;
需要查阅时用 `git log -- src test` 或检出 `059c46f` 之前的提交。现役实现全部在 `csharp/` 下。

改完 `csharp/` 里的任何代码,**线上不会自动生效**——运行的是安装目录里的副本:

```powershell
dotnet build csharp\AiResume.sln
csharp\src\AiResume.Worker\bin\Debug\net10.0-windows\AiResume.Worker.exe install
```

`install` 把三个项目(Gui / Worker / Hook)的产物合并复制到 `%LOCALAPPDATA%\AI Resume\`,
重建桌面/开始菜单/开机自启入口,并按持久化的通知意图(`ProductConfig.NotifySources`)
对账重建五个完成通知钩子。**通知源未能对齐时返回 2,不得当成功处理。**

`uninstall` 逐个关闭钩子源、保留 `state\`(内含 DPAPI 加密的飞书凭据与运行数据库),
不会删掉用户自己的 `settings.json`。

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
- **现役兼容行为**:GUI 默认模型和飞书模型卡都只能暴露最近一次真实探测成功的 provider;飞书使用 provider→model 两级选择,旧卡和文字命令也必须复用同一可用性校验,不可通过静态 profile id 绕过。OpenAI/DeepSeek 的真实探测先按大小写不敏感规则清除进程代理做直连,现役 Node 仅在 `transient` 网络类失败且配置了 `aiProxy` 时尝试备用代理;认证、额度、模型和命令错误禁止换线。成功线路随健康快照缓存 5 分钟,失败只负缓存 30 秒;密钥、端点或代理配置变化必须用哈希指纹立即作废旧线路。`childPending=true` 时禁止再次探测,真实 close 后通过 `waitForIdle` 清理临时目录并立即使快照过期。正式任务固定使用该线路且不得在任务中途换线重放;等待探测期间用户停止或现役 legacy deadline 到期都阻止正式子进程启动。`aiProxy` 不应用于 Claude。**目标 C# HealthProbe 不设置客户端总时限;DNS/TCP/TLS/reset 与监控异常归 `failed_local`,不得因静默或本地计时器触发 provider fallback/重放。**
- `-SelfTest`、`-SessionSelfTest`、`-RenderTo`、`-AISettingsRenderTo` 禁止发真实探测,只显示「待检测」;OpenAI/DeepSeek 成功状态必须区分「直连可用」和「代理可用」,双线路网络失败显示「代理异常」。改服务状态逻辑后必须跑 `dotnet test`,并在部署后的真实窗口核验状态行。

## 自测(改完必跑)

```powershell
dotnet test csharp\AiResume.sln
```

723 个 xUnit 用例,约 30 秒。红线:**绝不**对真实项目或真实会话启动 AI 运行,
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
- **现役 Node 兼容事实**:项目修改/一次性执行/后台续跑无总时限,查询/闲聊仍有 30 分钟 legacy deadline,Stage 1 不改变生产行为。**目标 RunContract**:chat/query/modify/resume/probe 全部不设客户端总时限,采用 Start/Status/Cancel;只有结构化 HTTP 408/504/`gateway_timeout` 是 `failed_provider` 超时,DNS/TCP/TLS/reset、进程消失和监控异常是 `failed_local`;用户停止为 `cancelled` 且不得 fallback。`heartbeatAt`、`lastOutputAt`、`silentSeconds` 仅为指标。`perProjectTimeoutMinutes` 等旧字段不得进入目标 C# 协议。
- 无总时限任务的进程边界必须同时覆盖正常退出和不可捕获崩溃:活跃 AI 子进程用临时文件+fsync+替换写入 AppDir 的 `feishu-ai-children.json`,包含父/子 PID、`runKey/taskKind`、启动时间和 provider。首次登记失败必须立即终止并拒绝任务,真实 close/error 前保留运行锁,后台继续重试落盘。下次启动只有父 agent PID、PID、5 秒内启动时间、provider 对应 Codex/Claude 命令签名都匹配时才能回收,禁止只凭 PID 杀进程。CIM/`taskkill` 未确认成功时必须恢复同 `runKey` 占位锁并每分钟重试;旧格式无 `runKey` 时全局禁止修改任务。超时/停止可在终止宽限期后结束消息等待,但只能在真实 close/error 后释放运行键和注销 PID。agent 内的 provider 健康探测复用同一 runner/登记表。
- PowerShell 后台自动续跑另用 `armCycleId` / `state.cycleId` 隔离每次布防周期。Node 与 PowerShell 写 `config.json` 必须共用 `config.json.write.lock`,在锁内重新读取最新配置后只修改本次负责字段,再 fsync + 原子替换;禁止锁外读旧快照后整体写回。后台在 spawn 前先向 `checker-ai-child.json` 写 `launching` 意图,启动后升级为含父/子 PID、周期 runKey、项目和启动时间的 `active` 登记;完整 `.tmp-*` 也是恢复候选。CIM 探测必须区分 found/gone/failed,只有明确 gone 才能删登记;启动时间、父 PID、命令签名任一不可核验都 fail-closed。解除只停止后台自动续跑,不影响飞书任务。计划任务必须设置 `ExecutionTimeLimit=PT0S`,否则 Windows 默认 72 小时仍会截断无上限运行。

## 安全约束

- 只有 `feishuAuthOpenIds`(full)里的用户能**修改**项目;其他人自动只读;闲聊对所有人开放。
- **非 owner 的查询/闲聊必须禁全部文件工具**(plan 模式拦不住"读",能读到 `config.json` 并借「解锁」提权——已实测)。该边界现由 cc-connect 的 `/mode` 与 `allow_from` / `admin_from` 承担;改动前先读 ADR-0003。
- OpenAI / DeepSeek / 飞书机密只放在 AppDir 下 gitignore 的 `config.json`,**绝不**进仓库、日志或测试输出。
- `feishuAuthOpenIds` 为空 = 未锁定(所有人可改),移除最后一个 full 用户会解锁,需警告。
- Fable 5 仅 owner 可用(按钮不展示、命令拒绝、运行时封顶三层)。

## 会话生命周期

- 飞书闲聊和只读查询按 `updatedAt` 计算:14 天未使用归档,30 天未使用永久删除;默认每 6 小时检查一次。
- 项目工作会话绝不自动归档或删除,只能由用户在 GUI「会话」窗口手动操作。
- Claude 归档必须同时移动 `<sessionId>.jsonl` 与同名 artifact 目录;Codex 必须走 app-server 的 `thread/archive` / `thread/unarchive` / `thread/delete`,不能只删本地引用。
- 会话清理逻辑集中在 `csharp/src/AiResume.Wrapper/CcConnectSessionBridge.cs`,禁止再复制一套哈希/删除逻辑。
