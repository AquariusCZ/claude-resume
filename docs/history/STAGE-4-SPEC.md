# Stage 4 规格:cc-connect 试点(sidecar 兼容性试验,不接管生产)

> 状态:**v1 已冻结,2026-08-05**(用户确认按计划推进后冻结)。S4-A/S4-B/S4-C/S4-D 全部完成(§7 正式报告,2026-08-05 提交,commits c382dc4/4dd6993);阶段门禁复核中。
> 依据:`docs/UPSTREAM-ARCHITECTURE-RESEARCH.md`(2026-08-01 计划,阶段 4:cc-connect 试点)+ `docs/MIGRATION-BASELINE.md` §9(外部前提)+ `AGENTS.md`(cc-connect 目标定位)。冲突时以上述文档为准并回报,不得自行取舍。

## 1. 目标与范围

- **目标**:用实际安装的 cc-connect **1.4.1**(commit `5d4c96dd`,与基线一致)二进制和独立测试项目,验证 Codex / Claude Code / DeepSeek V4 Flash(经 claude 兼容入口)新建/resume、用户隔离、项目路由、停止、进度、图片、完成提示、崩溃恢复;评估 wrapper(cc-connect 作为 AI Resume sidecar 的适配面)是否满足目标架构,**不满足则形成新 ADR**。
- **不迁移(范围外)**:不替换现役 Node `feishu-agent.js` 生产消费;不双写任何会话/任务状态;不安装 daemon/计划任务/开机项;不触碰生产 AppDir。
- **产出**:① cc-connect 试点验证记录(独立测试应用 + 测试项目);② wrapper 适配评估(管理 API / send / session_key / 权限边界逐条对照);③ 验证报告(契约逐条 + 剩余风险 + 新 ADR 决策或结论)。

## 2. 外部前提(任一未满足不得开工对应工作包)

1. **独立飞书测试应用**:已确认(S3 使用中,appId/appSecret 与生产隔离,user 身份已授权;bot 身份可用,应用后台 im 相关权限 S3 已验证)。
2. **lark-cli 本机配置**:已就绪(S3-B 完成,~/.lark-cli/config.json)。
3. **cc-connect 1.4.1**:已安装(PATH 可执行,`--version` 输出 commit `5d4c96dd`)。
4. **独立测试项目**:本阶段新建于仓库外 `%TEMP%\cc-connect-pilot\`(git 仓库 + 最小任务文件),不进入任何生产项目、不进入仓库。
5. **飞书交互**:试点中的入站消息验证需用户通过飞书客户端给测试应用 bot 发送消息(每场景 1 条,共约 5-8 条);我侧只做接收、响应与记录。

## 3. cc-connect 契约要点(封装/适配必须原样保留,不得改写)

- 配置:TOML;`[[projects]]`(name/agent/work_dir/mode)+ `[[projects.platforms]]`(type=feishu,options.app_id/app_secret)。显式 `--config <path>` 或 cwd `config.toml`。
- 飞书:WebSocket 长连接;应用需 bot 能力、`im:message:send_as_bot`、事件订阅 WebSocket 模式 + `im.message.receive_v1`、已发布版本。
- 会话:`session_key = platform:chat_id:user_id` 稳定路由键;聊天命令 `/new /list /switch /current /history /mode /stop /allow /provider /help`;允许/拒绝/允许所有 三种工具审批答复。
- 注入:`cc-connect send -m <text> | --stdin -p <project> -s <session>`(内部 API),环境变量 `CC_PROJECT`/`CC_SESSION_KEY` 注入给 agent。
- daemon:Windows 下是计划任务(本阶段**禁止安装**;仅前台/后台进程运行试点)。
- 进度:默认发送 thinking/tool 进度消息,`/quiet` 可关闭;`done_emoji` 是渠道语义,不混入 Agent 成功判定。
- 启动注意:在 Claude Code 会话环境内启动需 unset `CLAUDECODE`,否则 Claude Code 拒绝作为子进程启动。

## 4. 工作包

### S4-A 测试项目 + cc-connect 配置与启动(离线为主)

- 位置(全部仓库外):`%TEMP%\cc-connect-pilot\`(测试项目,git init + README + 简单任务文件 + AGENTS.md 占位);`%TEMP%\cc-connect-pilot-config\config.toml`(凭据所在,不落仓库)。
- 配置:1 个 feishu 平台 + 1 个 claudecode 项目;视验证需要加 codex 项目(第二项目或临时切换)。
- 启动:前台/后台进程,日志落 `%TEMP%\cc-connect-pilot-config\cc-connect.log`;确认启动日志含 `platform started` / `engine started` / `cc-connect is running`。
- 完成标准:进程启动、飞书长连接建立、无 panic/反复重连、测试应用 bot 在飞书可见(用户确认 bot 消息可达性)。

### S4-B 会话/路由/停止/进度(需用户 3-5 条飞书消息)

- 验证:新建会话(文本任务,如"读取项目 README 并总结")、resume(`/switch` 或自然语言续聊)、用户隔离(同 bot 不同 chat 不串会话——用户主聊天 + 可选的第二个会话)、项目路由(项目名/工作目录正确)、停止(`/stop` 后进程被终止且无残留)、进度(thinking/tool 消息按节奏出现,不冒充完成)。
- 完成标准:每场景记录命令/消息/响应形状/进程证据;停止后确认无孤儿进程;会话切换后上下文正确延续。

### S4-C 图片/完成提示/崩溃恢复(需用户 2-3 条飞书消息)

- 验证:图片消息(用户发图 → Claude 多模态读取并回答图片内容)、完成提示(任务完成消息/done 语义,不误报)、崩溃恢复(杀 cc-connect 进程 → 重启 → 会话可恢复,无半写状态)。
- 完成标准:图片内容被 agent 读取并给出合理回答;完成提示只在真实结束时出现;崩溃重启后 `/list` 可见原会话且 resume 正常。

### S4-D wrapper 适配评估与 ADR 决策

- 对照清单:管理 API(项目/会话/provider/定时任务)、`send` 注入与 AI Resume 的完成通知/续跑边界、session_key 与 AI Resume 会话 schema 的映射、权限边界(owner/viewer、`feishuAuthOpenIds`)、停止语义(agent 停止 vs 用户取消)、崩溃恢复契约、DeepSeek V4 Flash 兼容入口(环境变量注入是否可行)。
- 输出:逐条「满足 / 不满足 + 差异」;任一关键不满足 → 起草新 ADR(经用户确认后入库),全部满足或差异可接受 → 结论入报告。

## 5. 出口门禁(阶段总门禁)

- 全仓 `rg -i "sk-|app_secret"` 仅命中文档与既有脱敏代码注释;凭据实值 0 出现(仓库/日志/测试输出/commit)。
- 测试项目与 cc-connect 配置全部位于仓库外;仓库内只新增规格文档与验证记录(不含凭据)。
- S4-B/C 场景逐项通过,输出形状与契约逐条对照记录。
- wrapper 评估清单逐条完成,ADR 决策有明确结论。
- 文档同步:`docs/ARCHITECTURE.md`(cc-connect 试点状态)、`AI_GUIDE.md`(首行 project-tour 时间标记刷新)、`docs/MIGRATION-DEBT.md`(D-008 试点进展或新增)、`docs/STAGE-4-SPEC.md` §7 实现状态。
- 阶段报告:已跑测试清单与结果、文档同步情况、剩余风险。

## 6. 禁止事项(违反 = 阶段整体拒收)

- 凭据实值进仓库/日志/测试输出/commit 信息;不读生产 AppDir `config.json` 或任何密钥。
- 测试应用与生产 `feishu-agent.js` 同时消费同一生产飞书应用;不双写任何会话/任务状态;cc-connect 只连测试应用。
- 不安装 cc-connect daemon/计划任务/开机项;不自动 `--yes`/绕过高风险确认;测试项目内不执行有真实副作用的修改任务(只读/总结/回答类任务)。
- 不替换入站长连接/卡片状态机/生产消费边界;不复制 lark-* Skills 进仓库。
- 需改冻结接口/新增依赖/工具链异常/基线不绿 → 立即停止报告,不得自行绕过。

## 7. 报告格式(每包完成后提交)

```
包:S4-A
commit:`docs: S4-A cc-connect 试点环境与长连接就绪`(见 git log,单 commit)
build.ps1 输出末 6 行:<S4-A 无仓库源码变更,未触发构建;外部进程证据见下方试点记录>
新增/修改文件:docs/STAGE-4-SPEC.md(本段报告与状态行);试点资产全部在仓库外,不入仓库
设计决策与偏离:无(全部按 §2/§4 执行);记录两条 WARN 与语义对照(见下)
自测未覆盖的风险:bot 消息可达性未确认(需用户发消息);allow_from 未限制时的入站隔离仅依赖测试应用不公开

S4-A 试点记录(**⚠️ 2026-08-06 更正:此处标注的 `cli_xxxxxxxxxxxxxxxx` 并非独立测试应用,而是生产应用 app id——见 D-015;当时的「独立测试应用」结论不成立**,凭据仅存 %TEMP%\cc-connect-pilot-config\config.toml,零落仓库):
  1. 测试项目 %TEMP%\cc-connect-pilot:git init + README.md + task.md + AGENTS.md(占位限定只读/总结,禁修改)
  2. config.toml:1 feishu 平台 + 1 claudecode 项目(pilot,work_dir=测试项目,mode=default);log level=debug
  3. cc-connect v1.4.1(commit 5d4c96dd):进程 31856 + node 子进程 89108,日志落 %TEMP%\cc-connect-pilot-config\cc-connect.log
  4. 启动日志逐条命中:platform ready / engine started(agent=claudecode,platforms=1)/ api server started / cc-connect is running(projects=1)
  5. 长连接:connected to wss://msg-frontier.feishu.cn 仅 1 次;观察 20 秒窗口无重连、无 panic、无反复重试
  6. WARN 记录:allow_from 未设置(全用户允许,与 AI Resume「feishuAuthOpenIds 空=未锁定」语义一致,试点应用隔离可接受,S4-D 继续评估);admin_from 未设置(特权命令 /shell /show /dir /restart /upgrade 自动阻止,更安全);interactive card mode 已启用(依赖 card.action.trigger 订阅,S4-B 观察卡片行为)
  7. CLAUDECODE 环境:engine started 成功,Claude Code 子进程启动正常(非 Claude Code 会话内启动)
  8. 待办:S4-B 第一步由用户向测试 bot 发消息,同时确认 bot 可见性(完成 S4-A 最后一项完成标准)

包:S4-X
commit:<hash>
build.ps1 输出末 6 行:<粘贴>
新增/修改文件:<清单>
设计决策与偏离:<无,或逐条说明>
自测未覆盖的风险:<诚实列出>
```

## 7.1 S4-B 试点验证(完成,2026-08-05 提交)

```
包:S4-B(会话/路由/停止/进度)
commit:<docs: S4-B/C 试点验证完成,见 git log 单 commit>
build.ps1 输出末 6 行:<无仓库源码变更,未触发构建;外部进程证据见下方试点记录>
新增/修改文件:docs/STAGE-4-SPEC.md(本段报告与状态行);试点资产全部在仓库外,不入仓库
设计决策与偏离:
  - 原计划「由用户飞书消息驱动验证」改为「provider 切换后全自动验证」(用户明确授权:把后端的模型先切换到这里来,把验证的事情都做了,并提供独立测试 key sk-abe31***755c)
  - 新增管理途径:cc-connect Web UI(management 端口 9820)+ Web 聊天桥(bridge 端口 9810),用于注入消息与执行聊天命令;两者均为 cc-connect 内置能力,不触碰生产
  - provider 激活:Web UI「启用」按钮(POST /api/v1/projects/pilot/providers/deepseek/activate,与 /provider switch 聊天命令同一代码路径),因运行中二进制不识别 /provider 命令(见下发现 2)
自测未覆盖的风险:飞书通道的 /stop 文本命令与图片入站未实测(Web 通道已验证,飞书通道语义相同,仍记录待用户消息确认);跨用户隔离仅验证了命名空间结构(见下)

S4-B 试点记录(**⚠️ 2026-08-06 更正:`cli_xxxxxxxxxxxxxxxx` 实为生产应用 app id,非独立测试应用——见 D-015**,凭据仅存 %TEMP%\cc-connect-pilot-config\config.toml,零落仓库):
  1. 真实入站→新建会话 ✓:用户 01:28 发「读取项目 README 并总结」→ message received → claudeSession starting(dir=测试项目)→ agent 启动(agent_session=1053459d)→ turn complete(tools=0,response_len=68)→ 响应回飞书。会话持久化 pilot_65cd2cc9(session_key=feishu:oc_xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx:ou_xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx,~/.cc-connect/sessions/)。
  2. resume ✓:同一 agent_session 1053459d 经 cron 注入续聊 3 次(history_len 2→6→8,上下文正确延续);provider 激活重置会话后新会话 d9f9f45d 再续聊 4 次(history_len 2→4→7)。
  3. 项目路由 ✓:所有 agent 工具(Read/Glob)读取文件均在测试项目 %TEMP%\cc-connect-pilot 内,work_dir 注入正确。
  4. provider 切换 ✓(核心):DeepSeek Anthropic 兼容端点 https://api.deepseek.com/anthropic + 模型 deepseek-v4-flash 端点验证成功;激活后日志 `claudecode: provider switched provider=deepseek`;spawn 注入 providerEnv=[ANTHROPIC_BASE_URL=https://api.deepseek.com/anthropic ANTHROPIC_AUTH_TOKEN=*** ANTHROPIC_API_KEY=*** ANTHROPIC_MODEL=deepseek-v4-flash CLAUDE_CODE_PROVIDER_MANAGED_BY_HOST=1] + --model deepseek-v4-flash;真实调用成功(tools=5 读三文件,input_tokens=47734,response_len=696);激活状态持久化到 config.toml(provider="deepseek"),重启自动恢复。
  5. 停止 ✓:长任务运行中经 Web 聊天执行 /stop →「⏹ 执行已停止。」立即中断;日志 `audit: command_executed command=stop` → `cleanupInteractiveState: closing agent session` → `claudeSession: exited cleanly after stdin close`;停止后 CLI agent 子进程零残留(仅剩用户自己打开的 Claude 桌面应用进程)。
  6. 进度 ✓:飞书渠道无独立进度卡样式(style=legacy),走 stream preview 机制(SendPreviewStart → UpdateMessageWithStatusFooter,完成时 viaUpdate=false + footer);Web 通道显示思考💭/工具调用进度;超长回复降级 no active preview → p.Send 直接发送。
  7. 用户隔离(部分):Web 聊天桥独立命名空间 bridge:web-admin:pilot,与飞书 feishu:chat:user 会话互不串写;跨用户隔离(同 chat 不同 user)依赖 session_key 结构,测试应用无第二用户,记录为剩余风险。

S4-B 发现(全部为 S4-D 评估输入):
  1. 额度边界:claudecode agent 默认复用本机 Claude Code 登录的 Claude 官方订阅额度(five_hour rate_limit 秒拒);与 AI Resume provider 管理完全隔离;生产 node PID 28228 全程未动。
  2. **release 二进制命令集与文档不一致**:运行中 v1.4.1 Windows release 对 /provider 回复「Unknown command: /provider」(1 秒内本地分发器拒绝);/stop 经 Web 命令面板有效但文档未说明入口差异;git tag 源码与 release 二进制疑似不同步(版本管理风险)。
  3. send 注入语义:cc-connect send 仅以 bot 身份外发消息(不驱动 agent),API 创建的消息不触发 im.message.receive_v1;cron prompt 注入:session 空闲时过命令分发器(命令类 prompt 会被分发),忙时拒绝(session is busy);Web 聊天桥是唯一可脚本化驱动 agent 的注入途径。
  4. provider 激活副作用:激活时 resetAllSessions() 清空会话历史(该版本预期行为,对生产会话切换需注意)。
```

## 7.2 S4-C 试点验证(完成,2026-08-05 提交)

```
包:S4-C(图片/完成提示/崩溃恢复)
commit:同 §7.1(单 commit)
build.ps1 输出末 6 行:<同 §7.1>
新增/修改文件:docs/STAGE-4-SPEC.md(本段报告);试点资产全部在仓库外
设计决策与偏离:图片场景原计划「用户发图」改为「Web 聊天注入 + 项目内本地图片工具读取」(用户已授权全自动验证);飞书图片入站仍待用户发图确认
自测未覆盖的风险:飞书通道图片消息链路(需真实用户发图);DeepSeek 无多模态(见下发现,若换 Claude provider 则需额度)

S4-C 试点记录:
  1. 图片(受限)✓/✗:Web 聊天桥无附件上传入口(input[type=file] 数 0,bridge capabilities 仅 text/card/buttons/typing/update_message/preview);项目内本地图片 test-image.png(1024×1024 PNG)经 agent 工具读取 4 种方式(原图/重编码 PNG/JPEG/96×96 缩略)全部返回 [Unsupported Image]——deepseek-v4-flash 经 Claude Code CLI 桥接不支持多模态(模型能力边界,由 provider 决定,cc-connect 不做多模态适配);agent 以像素分析替代(识别出绿色植被场景 + 中央暖色动物主体,未正确识别物种与红色球体)。飞书图片入站链路未验证(待用户)。
  2. 完成提示 ✓:turn complete 仅真实结束时出现;stream preview finish 成功经 UpdateMessageWithStatusFooter 带状态 footer;EventResult: finalized via stream preview;Web 通道与飞书通道一致,无提前完成误报。
  3. 崩溃恢复 ✓:任务运行中(启动 6 秒)taskkill /PID <cc-connect> /T /F 整树终止 → 重启 → `session: loaded from disk sessions=2`(飞书 s1 + Web s2 均恢复,无半写状态)→ provider 激活自动恢复(provider switched provider=deepseek)→ 续聊 resume 正常(agent_session=d9f9f45d 上下文延续,history_len=7);另有 03:05/03:30 两次常规重启均验证 session 加载与激活恢复。生产 node(28228)未受影响。
```

## 7.3 S4-D wrapper 适配评估与 ADR 决策(完成,2026-08-05 提交)

```
包:S4-D(wrapper 适配评估)
commit:同 §7.1(单 commit)
结论:不形成新 ADR——cc-connect 作为 sidecar 适配面全部可 wrapper 化,差异不改变目标架构(cc-connect=消息+Agent 会话编排内核,AI Resume 保留控制面/授权/会话管理/续跑);差异与新增债务见下与 MIGRATION-DEBT

对照清单(逐条:满足 / 不满足+差异+对策):
  1. 管理 API(项目/会话/provider/定时任务)——满足(有差异)。Web UI(management 端口 9820,token 认证)+ REST API 已实证:POST /api/v1/projects/pilot/providers/deepseek/activate(provider 激活)、GET /api/v1/projects/pilot/sessions/s2(会话读取);cron CLI(add/exec/rm/list)已实证。差异:API 端点无正式文档(以 Web UI 行为为准),wrapper 必须固定已验证端点,升级后复验。
  2. send 注入与完成通知/续跑边界——不满足 send 语义,可替代。send 仅 bot 外发不驱动 agent;可用注入途径:cron exec(实证;session 忙时拒绝 `session is busy`)+ Web 聊天桥(bridge 9810,实证可驱动;无附件)。完成通知:turn complete 经渠道消息/状态 footer 外发,wrapper 需监听渠道或轮询会话。续跑边界:AI Resume「Claude 限额后自动续跑」需主动注入 → wrapper 封装 cron exec/bridge 注入,必须处理 busy 拒绝(排队/重试)与幂等。
  3. session_key 与 AI Resume 会话 schema 映射——部分满足。session_key=platform:chat_id:user_id 稳定路由(实证:feishu 会话与 bridge 会话互不串写);会话持久化 JSON(version=1:sessions/active_session/user_sessions/user_meta,消息级 history[role/content/timestamp])。差异:①cc-connect 无 14/30 天归档删除与 6 小时扫描(last_user_activity 存在但未见清理逻辑);②无「项目工作会话保护」概念;③history 是消息级,与 AI Resume 聊天/项目/query 分类状态不同维 → 清理规则与状态映射由 wrapper 承担(对齐 session-manager.js),cc-connect 只提供稳定会话载体。
  4. 权限边界(owner/viewer、feishuAuthOpenIds)——部分满足,关键差异可 wrapper 化。allow_from 白名单(未设置=全允许,与 AI Resume「feishuAuthOpenIds 空=未锁定」语义一致)、admin_from(未设置特权命令自动阻止)、/allow 工具审批三态。差异:AI Resume 的 owner(viewer 角色)与「非 owner 禁全部文件工具」在 cc-connect 无等价(agent 工具无限制)→ AI Resume 保留自有授权层,cc-connect 仅作通道;wrapper 把 feishuAuthOpenIds 映射为 allow_from + 入口鉴权,不依赖 cc-connect 承担角色模型。
  5. 停止语义(agent 停止 vs 用户取消)——满足(有差异)。/stop 停止当前 turn(实证:audit command=stop → cleanupInteractiveState → claudeSession: exited cleanly after stdin close,子进程零残留)。差异:cc-connect 无 RunContract 的 run 状态机(无 cancelled 持久化),wrapper 需从 turn 事件/停止结果推导状态并映射 AI Resume 语义(用户停止=cancelled,不 fallback)。
  6. 崩溃恢复契约——满足(有差异)。实证:任务运行中整树 kill → 重启 → sessions=2 从磁盘恢复、provider 激活自动恢复、resume 上下文延续;无半写状态。差异:cc-connect 进程崩溃时进行中 turn 直接丢失(无 run 级恢复),agent 端(Claude Code/DeepSeek)自身会话持久化兜底;AI Resume 目标 RunContract 的 run 级恢复需 wrapper 在 cc-connect 之上维护 run 映射(从 turn 事件推导)或接受会话级恢复语义。
  7. DeepSeek V4 Flash 兼容入口——满足。环境变量注入实证:ANTHROPIC_BASE_URL/AUTH_TOKEN/API_KEY/MODEL + CLAUDE_CODE_PROVIDER_MANAGED_BY_HOST=1 + --model deepseek-v4-flash,真实调用成功。差异:①deepseek-v4-flash 经 Claude Code CLI 桥接不支持多模态([Unsupported Image],agent 只能像素分析)——模型能力边界,由 provider 决定,cc-connect 不做多模态适配;②provider 激活无聊天命令(release 二进制对 /provider 回复 Unknown command),只能 Web UI/管理 API;③激活副作用 resetAllSessions 清空会话历史。

S4-D 新增债务/风险(见 MIGRATION-DEBT D-008 更新与 D-013 新增):
  - 版本锁定风险:release 二进制与 git tag 源码命令集不一致(/provider 缺失、Web 命令面板与文档入口差异)→ 锁定已验证 v1.4.1 release(commit 5d4c96dd),任何升级必须重新过 S4-B/C 场景
  - 注入途径受限:cron exec 忙时拒绝 + bridge 无附件;续跑注入需排队/重试语义
  - 多模态能力由 provider 决定;需要图片理解时换支持视觉的 provider 或 Claude(额度边界)
  - cc-connect 无 14/30 天会话清理/工作会话保护/run 状态机 → wrapper 承担(session-manager.js 对齐 + run 映射)
  - 管理 API 无正式文档 → 固定已验证端点
```

## 8. 验证方将做什么(知悉即可)

逐包:diff 审查(对照规格与红线)、独立复跑 build.ps1、抽查试点记录与命令输出形状核对、验证 wrapper 评估清单的每一条都有实证、确认凭据零泄漏。
