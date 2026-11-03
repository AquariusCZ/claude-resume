# 完成通知协议、实现与验证手册

最后验证：2026-08-20（Windows 11，AI Resume v2）；2026-10-12 v2.1 把 Codex 通知改为 hooks、收件人明文化；2026-10-30 v2.2 按 ADR-0006 重写 Claude Code 钩子（exec form，新增 `StopFailure`）、OpenCode 插件（v2 目录形态）与飞书投递链路（直接启动 `lark-cli.exe`、机器范围凭据副本、分级退避与熔断、死信、`notify check` / `notify test`）。v2.2 的离线回归结论见 ADR-0006 §6；真机验收步骤与仍待真机验证的事项见 §6。同日交叉审查后补齐：通知意图三态（`notifyIntentRecorded`）、超过 24 小时的事件不再补发、“结果不明”的重发 55 分钟封顶、永久失败跨轮升级、“可送达”还要求最近一次尝试成功且之后没改过设置。2026-11-02：会话没落盘的 Claude Code 事件不发；用量上限中断每个会话每个限额周期只提醒一次、同时被打断的会话合成一条，文案按投递那一刻的布防快照说清会不会自动续跑、何时重置；第二行加会话名，桌面端临时工作区改用会话名（§5.3 第 6、7 步，§5.7）；界面、CLI 与飞书文案改用“中文 + 业界原词”（hook、测试通知、未送达、后台服务……，§5.9）。同日按 ADR-0007 修正机器人凭据副本：lark-cli 的 env 来源只收现成令牌，副本改为由 AI Resume 自己换 tenant_access_token 后只注入 App ID + 令牌，保存与诊断改为“真实换令牌 + 一次只读机器人接口调用”（§5.5、§5.8）。

本文是完成通知的现役真身。v2.2 起飞书在 AI Resume 里只剩这一种用途：任务完成、停下来等你、因错误中断、续跑有进展或 Claude Code 登录将到期时，经官方 `lark-cli` 以机器人身份私信一个收件人。v2.1 及之前由 cc-connect 承载的飞书聊天、会话与授权名单已于 2026-10-30 随 ADR-0006 退役，本文只在标明“历史”的地方提及。决策依据：ADR-0005 C1（Codex hooks，仍有效）与 ADR-0006 §2.E（其余部分）。

## 1. 从第一性原理定义“可用”

完成通知不是“配置里有一条 Hook”就算可用。完整链路必须同时满足：

1. 上游客户端在**整个 agent 任务结束**、**停下来等人**或**因错误中断**时产生可靠事件，而不是每次模型请求结束都触发。
2. 上游按其真实协议把事件正文送入 `AiResume.Hook.exe`，而且这条命令在上游的执行方式下真的跑得起来：Claude Code 走 exec form、不经 shell；OpenCode 插件确实被 OpenCode 加载。
3. Hook 能确定来源、会话和绝对工作目录，拒绝内部运行、子代理和无法归属项目的事件。
4. Hook 只做本地原子入队，不读取飞书凭据、不联网，也不阻断上游客户端。
5. 常驻 `AiResume.Worker.exe` 消费队列，直接启动 `lark-cli.exe`，用 S4U 会话 0 里也解得开的机器人凭据，以 bot 身份投递给收件人。
6. 本地七天去重和飞书 `--idempotency-key`（服务端 uuid，一小时内去重）同时生效；失败按类别只推迟单条或熔断整个通道，永久失败进死信，坏 JSON 隔离。
7. 界面与 CLI 上的“可送达”只来自真实投递记录（`completion-notify-health.json`），不来自“配置写进去了”；而且最近一次真实尝试必须是成功的，并核对得上那次成功之后凭据、收件人与 lark-cli 位置都没变过（比不出来同样不算），界面还要确认后台 Worker 在跑——过去成功过不等于现在送得到。

任何一层缺失都会表现成“开关已开，但飞书没有消息”。2026-10-30 体检证实 v2.1 在第 2、5、6 层都断过：OpenCode 2.x 拒绝加载旧的单文件插件；S4U 下 lark-cli 解不开自己用 CurrentUser DPAPI 加密的钥匙串，10-14 以来 96% 的投递失败都是它；经 `cmd.exe /c` 调 npm 垫片时多行 `--text` 在换行处截断，`--idempotency-key` 从未送达（ADR-0006 §1）。

```text
agent event (Claude Code Stop / StopFailure / Notification, OpenCode v2, Codex Stop ...)
        |
        v
AiResume.Hook.exe -- normalize/admit --> state/completion-events/*.json <-- ResumeEngine / ClaudeLoginReminder (source=ai-resume)
                                                |
                                                v
AiResume.Worker NotificationWorker --> CompletionNotifier --> LarkCliNotificationChannel --> lark-cli.exe --> Feishu
```

## 2. 上游盘点与采用结论

| 来源 | 事件边界 | 正文传输 | AI Resume 接入 | 主界面 |
|---|---|---|---|---|
| Claude Code | `Stop`（完成）、`StopFailure`（中断）、`Notification`（等你） | JSON 写入 stdin | `~/.claude/settings.json` 三组 exec form：`command` 是 `AiResume.Hook.exe` 的绝对路径，`args` 依次为 `["claudecode"]`、`["claudecode","--kind=failure"]`、`["claudecode","--kind=decision"]` | 显示 |
| Codex | hooks `Stop`（Codex 0.148.0） | JSON 写入 stdin | `~/.codex/hooks.json` 里 AI Resume 自己的 Stop 分组：`"AiResume.Hook.exe" codex`，信任哈希登记在 `config.toml` 的 `[hooks.state]` | 隐藏 |
| Cline | `TaskComplete.ps1` | JSON 写入 stdin | PowerShell wrapper 把同一份 stdin 转给旧脚本和 Hook | 隐藏 |
| Qoder | `hooks.Stop` | JSON 写入 stdin | `"AiResume.Hook.exe" qoder` | 隐藏 |
| OpenCode（≥ 2.0） | 完成：顶层会话的 `session.execution.succeeded`；等你：`permission.asked`、`form.created`（question）、顶层会话的 `session.execution.failed` | v2 插件用 `node:child_process` 的 `spawn` 按 argv 启动 Hook，JSON 写入 stdin 后立即关闭 | 目录形态插件 `~/.config/opencode/plugins/airesume-notify/index.ts` | 显示 |

“隐藏”只影响 GUI：主界面默认只列 Claude Code 与 OpenCode，其余三个收进菜单「视图 › 显示其他通知源(Codex / Cline / Qoder)」；隐藏的源若仍启用或断链，主界面留一句提示。注册表 `NotificationRegistry`、`notify enable/disable`、意图持久化与 `install` 对账仍覆盖全部五个（§4、§5.8）。

采用依据：

- Claude Code（2026-10-30 按官方 hooks 文档核对；本机 CLI 2.1.300、VS Code 扩展 2.1.280、Desktop 2.1.309）：命令 Hook 从 stdin 接收 JSON；`Stop` 代表主 agent 停止，不支持 matcher；回合因 API 错误结束时 `Stop` 不触发，改发 `StopFailure`（2.1.78+，`error` 为官方枚举，如 `rate_limit`、`authentication_failed`、`billing_error`、`server_error`）。v2.1 只订阅了 `Stop` 与 `Notification`，用量上限、登录失效这类中断此前完全没有通知。`Notification` 带 `notification_type`、`message`、`title`。hook 带 `args` 时走 exec form、不经 shell（2.1.139+），带空格的绝对路径 `.exe` 合法；v2.1 写的是带引号的 shell 字符串，Claude Code 在没有 Git Bash 时回退 PowerShell，这条命令会 ParserError（本机复现）：[Claude Code hooks](https://code.claude.com/docs/en/hooks)。
- Claude Code 的 hooks 在终端、IDE 扩展、Desktop 与 Remote Control 会话里都触发；Remote Control 会话在本机运行，连接期间 hook 子进程的环境里有 `CLAUDE_CODE_BRIDGE_SESSION_ID`。本机 `claude-vscode` 会话已在 2026-08-19 实证触发全局 `Stop` Hook；这不是 VS Code 扩展直接发飞书，而是扩展执行 AI Resume 写入 `~/.claude/settings.json` 的 command Hook。settings 由 Claude Code 的文件监视器热加载，不需要新会话；交互式会话在用户接受工作区信任对话框之前不执行任何 settings hook。
- `stop_hook_active=true` 的官方含义是“Claude 正因某个 Stop hook 而继续”。AI Resume 的 hook 恒定退出 0、无 stdout、从不阻断，没有递归风险；v2.1 及之前把它当丢弃条件，会吞掉 `/goal` 等场景下真正的结束通知。v2.2 起 Claude Code 只记录该标志，连续的结束交给 Worker 按会话合并（§5.3）；其它来源仍按它抑制。
- Codex 0.148.0 的 hooks 提供 `Stop` 事件：command hook 从 stdin 读取 JSON（`session_id`、`transcript_path`、`cwd`、`hook_event_name`、`turn_id`、`stop_hook_active`）；非 managed hook 必须在 `config.toml` 有 `[hooks.state."<hooks.json 全路径>:stop:<group>:<handler>"].trusted_hash`，且等于上游对规范化 handler 身份（`{"event_name":"stop","hooks":[{"async":false,"command":…,"timeout":30,"type":"command"}]}` 键排序后的紧凑 JSON）取的 SHA-256 才会执行。算法用本机 codex-chatgpt-web 写入的 `interrupt` 条目作 oracle 复算验证。旧的 `notify` 是单值共享键，2026-10 被 Codex Desktop computer-use 改写为 `[codex-computer-use.exe, turn-ended]`，AI Resume 的链整体丢失，因此所有权不再放在 `notify` 上；Hook 仍兼容旧 argv 负载（`agent-turn-complete`）。
- Qoder 的命令 Hook 同样从 stdin 接收 JSON，`Stop` 是 agent 完成响应的边界：[Qoder hooks](https://docs.qoder.com/extensions/hooks)、[Qoder CLI hooks](https://docs.qoder.com/cli/hooks)。
- OpenCode：本机 2026-10-29 从 1.19.5 升到 Desktop 2.x（当前 opencode-cli 2.2.3）。2.x 要求插件默认导出带 `id` 与 `setup`（或 `effect`）的对象，v1 写法不再运行（日志 `failed to load plugin … Plugin must export a default definition with an id and an effect or setup function`），AI Resume 的 1.x 单文件插件从那天起一次都没被加载，OpenCode 通知全部失效。v2 事件经 `ctx.event.subscribe` 订阅，载荷在 `data` 里；`session.idle` 在 2.2.3 已标 deprecated、服务端不再发布；OpenCode 自己的 TUI/Desktop 通知判据是：完成 = `session.execution.succeeded` 且没有仍在运行的直接子会话，等你 = `permission.asked`（含子会话）与 `form.created`。插件按 `plugins/` 目录自动发现，OpenCode 监视插件源文件并热重载；加载结果写在 `~/.local/share/opencode/log/opencode.log`。依据是 OpenCode v2.2.3 源码与其仓库内的 v2 插件文档；opencode.ai 上的 plugins/config 页面描述的是 1.x 写法（`plugin` 键、`$`、`session.idle`），不适用于 2.x。
- Cline 使用本机已安装扩展的 `TaskComplete.ps1` 契约；wrapper 必须保留用户原脚本的输入、输出和取消语义。
- lark-cli 1.0.81：环境变量凭据来源（`LARKSUITE_CLI_APP_ID` + `LARKSUITE_CLI_TENANT_ACCESS_TOKEN`，及 `LARKSUITE_CLI_BRAND`、`LARKSUITE_CLI_STRICT_MODE`）优先于钥匙串，但**只接受现成令牌**：只给 `LARKSUITE_CLI_APP_SECRET` 时它不去换令牌，必报 `no tat available from credential source "env"`（2026-11-02 用假值实验查证，ADR-0007；v2.2 当初“dry-run 证实”不成立，dry-run 不换令牌）。成功信封写 stdout，错误信封写 **stderr**（`type`、`subtype`、`code`、`message`、`log_id`、`retryable`、`retry_after_seconds`）；退出码 validation=2、authentication/authorization/config=3、network=4、api=1、policy=6、internal=5、confirmation=10：[ERROR_CONTRACT](https://github.com/larksuite/cli/blob/v1.0.81/errs/ERROR_CONTRACT.md)。`--idempotency-key` 映射为发消息请求体的 `uuid`；`doctor` 在线核对 bot 身份；`auth list --json` 列出已登录用户；npm 安装形态是 `lark-cli.cmd` 垫片加 `node_modules/@larksuite/cli/bin/lark-cli.exe`。1.0.90 起新增钥匙串缓存的 tenant access token，升级 lark-cli 前须复核注入令牌时投递是否重新依赖 DPAPI。
- 飞书发消息接口：同一 uuid 一小时内至多成功发送一条，最长 50 字符；发给同一用户 5 QPS；230020 / 99991400 为频控：[发送消息](https://open.feishu.cn/document/server-docs/im-v1/message/create)。
- 官方替代只能部分覆盖：Claude Code Remote Control 的手机推送默认关闭，只覆盖已连接的 Claude Code 会话，完成推送由模型自行决定，覆盖不到 `StopFailure`、OpenCode 和未连接的会话，所以确定性的飞书通知仍需保留。会话连着 Remote Control 时 Hook 记 `bridged`，文案加“· 远程已连接”；两条通道可能对同一件事各提醒一次。

这里不引入 Hook 框架或新的飞书 SDK。各客户端已有边界，AI Resume 只做薄适配；飞书投递调用官方 `lark-cli`（直接启动其 exe），不手写同类 HTTP 请求。

## 3. Hook 规范化与准入

Hook 写出的统一事件（`state\completion-events\<eventId>.json`，UTF-8 无 BOM）包含：

- `version`：固定为 1
- `source`：`claudecode` / `codex` / `cline` / `qoder` / `opencode`（AI Resume 自己写的事件是 `ai-resume`，见 §5.2）
- `kind`：`completion`（完成）/ `decision`（等你）/ `failure`（中断）。种类**由命令行的 `--kind=` 决定，不看 payload**：每个 hook 分组在配置期就写死了自己的种类，认不出的一律按完成处理
- `cwd`：绝对工作目录，用于动态识别项目；文案取它的末级目录名
- `sessionId`、`turnId`、`taskId`、`transcriptPath`、`notificationType`：上游提供时保留；Worker 用 Claude Code 的 `transcriptPath` 判断会话落没落盘、读限额周期与会话名（§5.3 第 6、7 步，§5.7）
- `smoke`：测试通知标记（`notify test`、界面「发送测试」与手工注入的合成负载）
- `eventId`：16 位十六进制（SHA-256 的前 8 字节），规则见下
- `atUtc`：入队时间

Claude Code 事件另存官方 payload 里的几个短值（v2.2 新增）：`hookEvent`（`hook_event_name`）、`promptId`（`prompt_id`）、`error`（`StopFailure` 的错误类别；只收 64 字符以内的字母、数字、`_`、`-`，否则记 `unknown`）、`stopHookActive`、`backgroundBusy`（`background_tasks` 里有 `status` 为 `running` 或 `pending`、且 `type` 是 `subagent`、`workflow` 或 `cloud session` 的任务，说明这次 Stop 只是暂停、等它们结束后唤醒会话继续干活；`shell`（含开发服务器）、`monitor`、`teammate`、`dream`、`auto-mode scan`、`memory import` 这类常驻或内务任务一直在，不算——算进来会让每次完成都推迟 30 分钟。类型名取自本机 Claude Code 2.1.300 的映射表：`local_agent→subagent`、`local_workflow→workflow`、`remote_agent→cloud session`）、`bridged`（hook 环境里有 `CLAUDE_CODE_BRIDGE_SESSION_ID`）。`Notification` 的 `message` 只取哈希参与 eventId；消息正文、错误详情与 `last_assistant_message` 都不落盘。

eventId 的输入：

- Codex：来源、会话、turn、cwd、kind。`turn_id` 是全局唯一的回合身份，旧 `notify` 负载与 hooks `Stop` 描述同一回合时得到同一个 ID，升级期不会双发。
- OpenCode 带显式 `event_id`（`evt_…`、`permission:<id>`、`form:<id>`）时：只用来源、`event_id` 与 kind。同一上游事件可能经多个位置实例或重复投递到达，cwd 与时间戳不得参与。
- Claude Code：来源、会话、turn、task、cwd、transcript 修改时间、显式事件 ID、时间戳、最后回答、kind，再加 `hook_event_name`、`prompt_id`、`notification_type`、`error` 与消息哈希。官方说明 transcript 异步写入、可能滞后，只靠它会让同一提示里的多次 Stop 与多次 Notification 撞 ID；“哪一条才是最终结束”交给 Worker 合并。
- 其余：来源、会话、turn、task、cwd、transcript 修改时间、显式事件 ID、时间戳、最后回答与 kind。

同名事件文件已存在即视为重复，不覆盖。

以下情况不入队：

- `AI_RESUME_INTERNAL_RUN=1`。AI Resume 的续跑子进程（`ProviderExecutionPlan`、`ClaudeResumeRunner` 注入）与诊断探测（`ClaudeCodeProbe`）都带它，避免续跑通知自己；续跑结果由续跑引擎自己的 `resume-*` 事件报告（§5.2）。
- 空/坏 JSON、根节点不是对象、不支持的来源。
- `stop_hook_active=true`：Codex、Cline、Qoder、OpenCode 丢弃；Claude Code 只记录（§2）。
- 事件名与种类不匹配：Claude Code 的 `completion` 只认 `Stop`、`failure` 只认 `StopFailure`、`decision` 只认 `Notification`（拒绝原因依次为 `stop_event_mismatch`、`stop_failure_event_mismatch`、`notification_event_mismatch`）；Qoder 只认 `Stop`；Cline 有事件名时只认 `TaskComplete`；OpenCode 的 `completion` 只认 `session.execution.succeeded`（1.x 旧插件的 `session.idle` 仍兼容），`decision` 只认 `permission.asked`、`form.created`、`session.execution.failed`。
- 工作目录缺失或不是绝对路径；Qoder 专属环境变量（`QODER_SESSION_ID`、`QODER_CWD`、`QODER_TRANSCRIPT_PATH`）只允许补 Qoder 负载。
- Codex 缺少 session/turn，rollout 未持久化，或 rollout 明确属于 subagent、internal、`memory_consolidation`。hooks `Stop` 给出的 `transcript_path` 只有在以 `-<session_id>.jsonl` 结尾、是绝对路径且文件存在时才直接使用，否则回到按 session 扫描 rollout。
- Codex 的 `Documents\Codex\<日期>\<slug>` 临时 projectless 目录；目录内确有 Git 根时例外。
- OpenCode 的会话归属在插件侧判定：完成与失败只认顶层会话，查询会话失败时 fail-closed、不通知；完成后 5 秒、失败后 60 秒的宽限期内会话又开跑（执行代次变了）就不报，留给其它插件（如自动续跑）或排队输入；完成时仍有运行中的直接子会话也不报，等子会话汇报后父会话真正结束的那一次。授权请求不做顶层过滤（子 agent 的授权同样会卡住整件事）；`form.created` 只接 `metadata.kind=question` 且会话 ID 真实的表单，MCP elicitation 用的 `global` 哨兵不接。

Hook 的退出码始终为 0，stdout 保持为空；通知故障不能阻断 agent 自己的收尾流程。stdin 型来源必须从标准输入流显式按严格 UTF-8 解码（去掉开头的 BOM），不能继承 Windows 控制台 CP936；否则 `公司内网SOP项目` 会在入队前稳定损坏成 `鍏徃鍐呯綉SOP椤圭洰`；非法 UTF-8 整条拒绝。事件先写同目录临时文件，再原子改名为 `<eventId>.json`。旧版 `notify` 链的 `--previous-notify` 转发仍被 Hook 兼容（5 秒超时，不转发 `.cmd` / `.bat`），但 v2.1 起不再写入 `notify`。历史 `%LOCALAPPDATA%\AI` 截断命令没有不可伪造的所有权证据，可能也是用户命令，因此保守保留，不自动删除。

历史（v2.1 及之前）：cc-connect 由 AI Resume 生成配置并经计划任务运行，它驱动的 agent 任务用两层 `AI_RESUME_INTERNAL_RUN=1`（每个项目的 `projects.agent.options.env` 与计划任务脚本）抑制完成通知。cc-connect 已于 2026-10-30 随 ADR-0006 退役，AI Resume 不再生成或激活它的配置，也不再有外部组件替 AI Resume 设置这个变量。

## 4. 配置所有权

- Claude Code：只管 `<配置根>\settings.json`（与 Claude Code 一致：设置了 `CLAUDE_CONFIG_DIR` 时用它，否则 `%USERPROFILE%\.claude`）里的三组 hook——`Stop`（无 matcher）、`StopFailure`（无 matcher，接收全部 error）、`Notification`（`matcher` 为 `ClaudeCodeNotificationAdapter.DecisionMatcher`，即 `permission_prompt|worker_permission_prompt|elicitation_dialog|elicitation_url_dialog|agent_needs_input|quota_auto_resume_stale|quota_auto_resume_disabled`）。每组一条 `{"type":"command","command":"<安装目录>\\AiResume.Hook.exe","args":[…],"timeout":30}`。`quota_auto_resume_stale`（额度已重置但电脑睡过头，等你按回车）与 `quota_auto_resume_disabled`（不会自己继续）是 v2.2 新增；`worker_permission_prompt`（agent team 里的协作 agent 卡在工具或网络授权上，文案“协作 agent 等你授权”）是同日交叉审查后补上的；不订阅 `idle_prompt`（回合结束约 60 秒后的提醒，与 Stop 重复）、`auth_success`、`agent_completed` 等。
  - 所有权：exec form 要求 `command` 的文件名精确为 `AiResume.Hook.exe`、`args[0]` 为 `claudecode`、其余参数全是 `--kind=…`；v2.1 写的 shell form（`"…\AiResume.Hook.exe" claudecode`，等你类再加 `--kind=decision`）按“首个可执行文件名精确为 `AiResume.Hook.exe`，其后只有 `claudecode` 与我方 `--kind=` 开关”认领，以便迁移。参数文本里偶然出现文件名，或调用同一 exe 但参数不同，都不算所有权。
  - 启用：已恰好是三组规范条目（exec form、我方独占分组、事件/开关/matcher/命令都一致）时为幂等空操作，不改写文件；否则先摘掉全部我方条目（空分组、空事件键一并移除），再追加三组。我方条目若混在用户自己的分组里，只把我方那一条摘出来单独成组，不改写那一组的 matcher。写回前备份 `.bak`，再经临时文件原子替换。
  - 探测：三组都在且形状规范才报已启用（`已安装 AI Resume 通知钩子(完成 / 中断 / 等你)`）；缺任一组或仍是旧 shell form 时标 `hookBroken`：`钩子不完整或为旧格式(缺:…);关掉再打开本开关或重新安装即可修复`。命令指向的 exe 不存在时由 `HookHealth` 标断链。
  - 版本前提：exec form 需要 Claude Code 2.1.139+，`StopFailure` 需要 2.1.78+。AI Resume 不探测客户端版本；更旧的客户端会把 `command` 当 shell 命令执行，含空格的安装路径会被拆断，通知静默丢失。
- Qoder：只有“首个可执行文件名精确为 `AiResume.Hook.exe`，且后面无参数或只有 `qoder`”才算我方 command；参数文本里偶然出现文件名不算所有权。
- Codex：`hooks.json` 只增删「handler command 的可执行文件精确为 `AiResume.Hook.exe` 且参数为 `codex`」的我方分组，永远追加在 `hooks.Stop` 末尾（信任键按序号定位，插到前面会让用户自己条目的信任记录错位）；根节点不是对象时拒绝修改。`config.toml` 只增删 `trusted_hash` 等于我方命令哈希、且表内除 `trusted_hash` / `enabled` 外没有其它键的 `[hooks.state."…"]` 表；启用时先写信任再写 `hooks.json`（失败回滚）；信任表不存在时按文本追加在文件末尾，已存在时原位更新 `trusted_hash`（用户在 `/hooks` 里停用留下的 `enabled = false` 一并去掉），写前用 Tomlyn 解析核验，原子替换并保留 `.bak`。写 `config.toml` 与 `hooks.json` 前都比较读入时的原文，被外部改写过就放弃本次、保留现场；外部编辑器不共享 AI Resume 的锁，运维上仍应避免一边启停通知源一边保存 `config.toml`。停用摘掉我方分组后，排在其后的用户分组的信任键序号同步前移，否则 Codex 会把别人的钩子当成未信任而静默停跑。`CODEX_HOME` 指定的目录按上游规则 canonicalize 后再构造信任键；`config.toml` 的 `[features] hooks = false` 时报断链。跨进程用命名互斥体串行。旧版 `notify` 残留只在 `notify list` 提示，不自动改写。已打开的 Codex 会话不会重载 `hooks.json`，新会话才生效；Codex 里 `/hooks` 显示未信任时在那里信任一次即可。
- Cline：用户原 `TaskComplete.ps1` 备份为 `TaskComplete.ai-resume-previous.ps1`，wrapper 先执行原脚本；wrapper 的读取、原脚本 stdin 和 AI Resume Hook stdin 都固定为 UTF-8，stdout 与 stderr 分开保留，只用 stdout 解析 `cancel`，原脚本要求取消时不再发我方通知。
- OpenCode：插件写在 OpenCode 2.x 的全局配置目录（`OPENCODE_CONFIG_DIR`；未设置时 `%XDG_CONFIG_HOME%\opencode`，再未设置时 `%USERPROFILE%\.config\opencode`）下的 `plugins\airesume-notify\index.ts`，加载证据读数据目录（`%XDG_DATA_HOME%\opencode`，未设置时 `%USERPROFILE%\.local\share\opencode`）下的 `log\opencode.log`；靠自动发现加载，不写进用户的 `opencode.jsonc`（写进去会加载两次）。插件默认导出 `{ id: "ai-resume.notify", setup(ctx) }`，只有 `const cmd = "…";` 一行随安装位置变化。所有权要求首行是稳定 marker `// AI Resume managed OpenCode notification plugin`，且源码同时含 `id: "ai-resume.notify"`、`export default {`、`ctx.event.subscribe(` 与 `const cmd = "`；目录名相同不能证明属于 AI Resume。
  - 启用：插件目录里有不属于我方的入口类文件（`package.json`、`index.*`、`server.*`）时拒绝覆盖；内容不变不重写；写入先落以点开头、不带 `.ts` / `.js` 扩展名的临时文件（写到一半不会被当成插件发现），再原子替换。新插件写好后才退役我方 1.x 单文件 `plugins\airesume-notify.ts`（两代旧生成物都认，先备份 `.bak` 再删除），避免热重载出现没有插件的空窗；用户自己的同名单文件不动。
  - 停用：只删我方 `index.ts`（目录空了一并删除）与我方旧单文件。
  - 探测（“已加载”的外部证据）：只读 OpenCode 日志 `%USERPROFILE%\.local\share\opencode\log\opencode.log` 末尾 4 MiB，只看本插件目录（日志里反斜杠双写）且时间不早于 `index.ts` 写入时间的记录，以其中**最后一条**结论为准（一次加载先记 `loading plugin` 再记失败；之后哪次重启成功加载，新的 `loading plugin` 覆盖旧的失败）。最后是 `failed to load plugin` → `hookBroken`，附 `LoadError` 原因；最后是 `loading plugin` → `已安装,OpenCode 已加载(日志 MM-dd HH:mm)`；都没有 → `已写入插件,等 OpenCode 下次加载后核实`（GUI 显示“待核实”）。只剩我方旧单文件时同样标 `hookBroken`：`旧版插件已不被 OpenCode 2.x 加载,完成通知收不到;关掉再打开本开关(或重新安装)即可升级`。
  - 只支持 OpenCode ≥ 2.0：v2 写法在 1.x 里不会被加载。
- `ProductConfig.NotifySources` 保存用户意图；重装按意图对账，卸载只移除 AI Resume 自己的条目（§5.8）。

路径含空格必须作为一个可执行文件参数处理。Claude Code 用 exec form，`command` 只放 exe、参数进 `args`，不经 shell；Qoder/Codex 写入带引号的命令字符串（Codex 的 hooks handler `command` 由 Codex 自己按 shell 规则拆分）；Cline 使用 PowerShell 单引号；OpenCode 用 `spawn(cmd, args, { shell: false, windowsHide: true, stdio: ["pipe", "ignore", "ignore"] })`，JSON 写入 stdin 后立即关闭（Hook 读到 EOF 即落盘退出），每个子进程 30 秒超时，不在事件循环里等待。

## 5. 队列、投递与进程生命周期

### 5.1 位置与状态文件

Hook 与 Worker 共用状态根 `%LOCALAPPDATA%\AI Resume\state\`（`AIRESUME_SHADOW_DIR` 可覆盖，只用于测试与离屏截图）：

| 路径（相对状态根） | 写入方 | 内容 |
|---|---|---|
| `completion-events\<eventId>.json` | Hook、续跑引擎、登录提醒、`notify test` | 待投递事件 |
| `completion-events\malformed\` | Worker | 坏 JSON、字段类型错误、缺 `eventId` 或缺 `cwd`（`ai-resume` 事件除外）的事件 |
| `completion-events\dead\` | Worker | 死信事件，附同名 `.error.txt`（事件 ID、类别、退出码、错误类型/子类型、错误码、log_id、时间） |
| `completion-notify-seen.json` | Worker | 已送达的 eventId 与送达时间，保留 7 天 |
| `completion-notify-state.json` | Worker | 单条退避状态：尝试次数、首次与下次尝试时间、首次“结果不明”的时间、最近的类别/错误码/log_id；跨重启保留，事件文件不在了就剪掉 |
| `completion-notify-sessions.json` | Worker | 每个 Claude Code 会话最近一次**已送达**的完成事件时间，保留 2 天；更早的 `backgroundBusy` 完成据此作废（§5.3） |
| `completion-notify-limits.json` | Worker | 已提醒过的「限额周期 + 会话」（周期为 `rate_limit\|<配置根指纹>\|<rateLimitType>\|<resetsAt>` 或 `session\|<会话 ID>`，后接 `#<会话 ID>`）与失效时间：按重置时间的到重置后再 1 小时，按会话的从中断起 5 小时，过期即剪；同一会话同一周期之后的中断据此丢弃（§5.3 第 7 步） |
| `completion-notify-health.json` | Worker 单写，GUI 与 CLI 只读 | 通道健康：`state`（`healthy` / `blocked` / `unknown`）、`reason`、熔断起止时间、连续通道失败次数、熔断指纹、`lastSuccessUtc`、`lastAttemptUtc`、`lastAttemptOk`（最近一次真实尝试成没成，条目级失败也置 false）、`successFingerprint`（最近一次成功时的设置指纹）、跨轮永久失败记账（`permanentClass` / `permanentSinceUtc` / `permanentEventId`）、最近错误的类别/错误码/log_id、`backlog`、`dead`、`credentialSource`、`updatedAtUtc` |
| `secrets\feishu-bot.bin` | GUI「飞书通知设置」 | 机器人凭据，LocalMachine DPAPI（§5.5） |
| `config.json` 的 `notifyRecipientOpenId`、`notifySources`、`notifyIntentRecorded` | GUI、`notify enable/disable`、`install` | 收件人与通知源意图（以及意图是否记录过）；Worker 只读，据意图丢弃已关掉的源（§5.3） |
| `logs\worker-yyyyMMdd.log` | Worker | 结构化日志（§5.8） |

### 5.2 AI Resume 自己的事件（source=ai-resume）

续跑引擎（`ResumeEngine` 经 `IResumeNotices` → `ResumeNoticeWriter`）与 `ClaudeLoginReminder` 把提醒写进同一个队列，格式与钩子事件相同：`source=ai-resume`、`kind=resume`、`notificationType` 为下表中的类型，`cwd` 为项目路径（登录提醒为空，Worker 对 `ai-resume` 事件不要求 `cwd`），另带 `model`（模型 ID）与 `detail`（状态码或到期时间）。eventId 取 `SHA-256("ai-resume|<类型>|<去重键>")` 的前 16 位十六进制，同一件事无论写几次、Worker 重启几次，都只发一次（文件名去重、七天已送达表、飞书 uuid 三层）。事件里只有项目路径、模型 ID 与状态码，不写令牌、账号或会话正文。

| 类型 | 何时写 | 去重键 |
|---|---|---|
| `session-open` | 项目被用量上限打断，按该项目的模型额度已恢复，但会话仍开在客户端（`in-use`，或同目录另一会话 `cwd-busy`），且已过重置时刻（取不到时用中断时刻）10 分钟：原生自动继续没有接上 | 周期 + 项目 + 中断 uuid |
| `resume-started` | 续跑子进程已经起来（监控循环第一次询问时，不早于进程启动） | 周期 + 项目 + 中断 uuid |
| `resume-succeeded` / `resume-failed` | 单个项目续跑结束；结果为 `limited`（续跑中再次撞限）时不发 | 周期 + 项目 + runId + 结果 |
| `credentials-unavailable` | 某个待续项目的额度判定为 `credentials-unavailable` 或 `identity-unverified`（Claude Code 的访问令牌过期、登录失效、凭据挪进 Windows 凭据管理器，或账号核实不了）；`detail` 是原因码，文案按原因给出对应的处理办法 | 周期 + 原因 + 4 小时时间桶，即每个原因每 4 小时最多一次（原因变了重新提醒） |
| `provider-changed` | 额度判定为账号已变化 | 周期 |
| `resume-not-started` | 同一项目连续 3 次因同一个不会自己好的原因没能启动（`no-claude`：找不到 Claude Code 或项目目录；`execution-unverified`：执行条件核实不了） | 周期 + 项目 + 原因 |
| `login-expiring` / `login-expired` | `ClaudeLoginReminder` 每 30 分钟读一次 Claude Code 登录的到期时间，剩 3 天、2 天、1 天、12 小时以内与已过期时各写一次；登录被 Claude Code 清空（刷新被拒，到期时间也随之消失）时每个本地日写一次 `login-expired`，直到恢复；没设置收件人时不写，免得设置后一次补发一串过期提醒 | 到期时间 + 阈值；清空的登录为本地日期 |

续跑子进程本身带 `AI_RESUME_INTERNAL_RUN=1`，它的 `Stop` / `StopFailure` 会被 Hook 抑制；飞书上看到的续跑结果只来自上表。

### 5.3 调度：顺序、稳定期、替代与暂缓

`NotificationWorker` 是 Worker 里的后台服务：启动后立即扫一轮，之后每 15 秒一轮；调度规则全部在 `CompletionNotifier`。每一轮：

1. 读入全部事件。坏 JSON、字段类型错误、缺必填字段的移入 `malformed\`；读失败的留到下轮。
2. 按事件的 `atUtc` 从旧到新排序（缺时间的排最后，同一时刻按文件名）。
3. 七天内已送达过的 → 删除文件（`Duplicate`）。
4. 事件来源不在 `ProductConfig.NotifySources` 意图里（用户在界面或用 `notify disable` 关掉了它，残留链路却仍在写事件，例如 Codex `config.toml` 里按 ADR-0005 只提示不改写的旧版 `notify` 链）→ 不发，直接删除（`Suppressed`，`source_disabled`）。来源名与意图名只差大小写（`claudecode` 对 `ClaudeCode`）；`ai-resume` 事件总是放行。意图分三态：从未记录过（`notifyIntentRecorded=false` 且列表为空，即更早的旧配置）→ 不过滤；记录过 → 只放行列表里的来源，**哪怕列表为空**（用户把通知源全关了，残留链路的事件同样丢弃）。GUI 开关、`notify enable/disable`、`install` 与 `uninstall` 写意图时都会置 `notifyIntentRecorded=true`。
5. 事件发生（`atUtc`）超过 24 小时还没送出去（一直缺收件人、通道长期熔断、Worker 停了很久……）→ 不再补发，移入 `dead\`（类别 `stale`）。文案只有 HH:mm，恢复后一次冲刷一串昨天的“已完成”看起来像刚发生，只会误导。
6. 会话没落盘的 Claude Code 事件（`source=claudecode`、非冒烟、带 `transcriptPath`，完成、中断、等你都一样）：转录文件不存在 → 入队不满 20 秒先等（转录是异步写的），满了直接删除（`Suppressed`，`session_not_persisted`）。没有转录的会话谁都打不开、续不了（AI Resume 续跑与 `claude --resume` 都要转录），只可能是程序拉起的一次性调用。“不存在”只在路径形如 `<配置根>\projects\<目录>\<会话>.jsonl`、`projects` 目录本身可读、列目录确实没有这个文件时成立；路径形状陌生、目录读不了（映射盘、权限）、或升级前的旧事件没带路径，一律按判断不了照常发送，绝不因此吞掉真会话的通知。实证（2026-10-31 至 11-02）：第三方用量监视器（CodeZeno ClaudeCodeUsageMonitor，WinGet 安装）常驻在 `Documents\Default Project`，访问令牌过期（约 8 小时）时以 `--no-session-persistence` 拉起 `claude` 刷新令牌；它读同一份 `~/.claude/settings.json`，周额度用完的两天里每次都触发 `StopFailure(rate_limit)`，飞书收到一串“Default Project 因用量上限中断…会按布防自动续跑”（当时根本没布防），额度恢复后又是“Default Project 已完成”。监视器的事件照样带着 `transcriptPath`：Claude Code 2.1.300 构造 hook 负载时写的是按会话 ID 推出的路径，只有无盘的云端会话（`diskless`）才给空串，`--no-session-persistence` 关掉的只是写盘（本机二进制只读核实），所以这一步拦得住它。
7. 用量上限中断**每个会话每个限额周期只提醒一次**，同一周期里同时待发的会话合成一条。中断事件是 Claude Code 的 `StopFailure`（`error=rate_limit`）与 `Notification` 的 `quota_auto_resume_disabled`（原生自动继续放弃：设置关了、重置超过 24 小时——周额度必中——、续写被拦或反复撞限后放弃），两者是同一次中断的两面，合成同一个会话。
   - 入队不满 20 秒先不判（`Deferred`，`settling`）：新的限流记录可能还没写进转录，这时读到的是上一次中断，会被误判成“已提醒过”。还有没满 20 秒的中断时，已经攒好的组也先等它，最早一条满 2 分钟就不再等。
   - 周期键取该会话转录尾部合成限流记录里服务端给的 `quotaLimits.rateLimitType + resetsAt`，再加上配置根的短指纹（两个 `CLAUDE_CONFIG_DIR` 的账号可能恰好同一时刻重置）；事件发生时这份重置时刻已经过了，说明转录尾部是更早那次中断（这次没写新记录），不采信；读不到时退回 `session|<会话 ID>`。
   - `completion-notify-limits.json` 记着“这个周期 + 这个会话”已经提醒过且未失效 → 删除（`Suppressed`，`same_limit_cycle`）。别的会话撞上同一周期照样提醒：无人值守的会话之后才撞墙，第一条消息说不到它。
   - 同一周期的待发中断合成一组，最早一条代表整组发送与退避，其余几条记 `Deferred`（`limit_group`）。送达后全组记已送达、删文件（其余几条记 `Superseded`，`merged`），组里每个会话按周期记账（§5.1）。代表那条进了死信时同组一起进死信，不再轮流换一条重发；进死信是因为结果不明（可能已经送达）时，这些会话也按已提醒记账。代表那条过期作废（超过 24 小时）时：它有过结果不明的尝试，同周期其余几条按已提醒处理；从没尝试过，其余几条照常另发。
   - 理由：同一会话同一周期之后的每一次（手动重试、原生自动继续撞墙）都不带新信息；监视器那种程序化调用由第 6 步拦下，它每次都是新会话，只按会话去重本来挡不住它。
8. Claude Code 的完成类事件（`source=claudecode`、`kind=completion`、非冒烟、有会话 ID）：本轮队列里还有同一会话、晚于它且相隔不超过 2 分钟的另一条完成事件 → 删除旧的（`Superseded`）。`/goal` 续跑等连续 Stop 因此只报最后一次。`backgroundBusy=true` 的完成等的正是之后那一条，所以同会话**任何**更晚的完成都让它作废：队列里有更晚的（不限 2 分钟），或 `completion-notify-sessions.json` 记着这个会话已经送出过更晚的完成。
9. 收件人为空 → 全部记 `Skipped`（`recipient_missing`），文件保留。
10. Claude Code 的完成类事件入队不满 20 秒（稳定期）→ 暂缓；`backgroundBusy=true` 且不满 30 分钟 → 暂缓，等真正结束的那一条。限额组按第 7 步的规则攒齐再发。等你类、其余中断、测试通知与其它来源不等稳定期。
11. 通道熔断中，或这一条还在单条退避期 → 本轮不碰（§5.6）。这一条第一次“结果不明”已超过 55 分钟 → 不再重发，移入 `dead\`（`outcome_unknown`，§5.6）。
12. 其余逐条发送，两次发送之间间隔 300 毫秒（飞书对同一用户 5 QPS）。这一轮开头就把事件读进了内存，所以每一条发送前再确认事件文件还在，并在发送期间以允许读、不允许删的方式占住它：撤回测试通知与发送由此互斥，文件已不在的记 `Suppressed`（`withdrawn`）不再发；正在发的那一条撤不回，撤回方拿到 `withdrawn=false`。送达一条就落盘一次已送达表，积压冲刷持续几分钟时测试结局也能及时看到。

第 3 到 7 步在主循环之前单独过一轮：先剔除一定不发的，再给中断分组，所以限额组里不会混进已送达、来源已关、过期或没落盘的事件。第 6、7 步与文案要核对的外部事实（转录在不在、限额周期、会话名、布防快照）由 `INotifyFacts` 提供；生产实现 `NotifyFacts` 只读 Claude Code 公开落盘的转录尾部与 `sessions\<pid>.json` 登记，以及 AI Resume 自己的 `config.json` 与 `runs.db`（已布防却没有状态库时不去打开，免得 SQLite 就地建一个空库）。任何一项读不出都按“不知道”处理：不过滤、按会话记账、续跑句写“未核实”，绝不让一条通知因此卡住整轮。

死信移动本身失败（文件被占用等）时保留这一条的退避状态，15 分钟后再试，不会每 15 秒原样重发。

历史（v2.1 及之前）：按文件名（即 eventId 哈希）顺序处理，积压的送达顺序与发生顺序无关；一轮里只要有一条失败，整个队列就按 30 秒到 15 分钟全局退避，一条永久失败会让所有新通知长期按 15 分钟一轮走，而且没有死信。

### 5.4 投递：直接启动 lark-cli.exe

`LarkCliNotificationChannel.ResolveExecutable` 只找真正的 exe，按顺序：先 `%APPDATA%\npm`（npm 全局包的官方安装位置），再 PATH 各目录；每个目录先找包内的 `node_modules\@larksuite\cli\bin\lark-cli.exe`，再找独立的 `lark-cli.exe`。PATH 里的相对路径条目一律跳过：它们会按 Worker 的当前目录解析，而这个进程要把机器人 App Secret 放进子进程环境，不能交给来路不明的同名程序。**绝不经 `cmd.exe` 或 `lark-cli.cmd` 垫片**：垫片走 `cmd.exe /c` 时，多行 `--text` 在第一个换行处被截断，其后的 `--idempotency-key`、`--format` 全部丢失（2026-10-30 复现；v1 时代的同一教训见 `docs/LESSONS.md`）。找不到 exe 时整个通道报 `launch_failed`，不回退 cmd.exe；lark-cli 自更新中断等原因导致 exe 缺失或启动失败时也是 `launch_failed`，重装 `@larksuite/cli` 即可。

每条消息一次调用，参数经 `ProcessStartInfo.ArgumentList` 逐个传入：

```text
lark-cli.exe im +messages-send --as bot --user-id <open_id> --text <文案> --idempotency-key <eventId> --format json
```

- 子进程环境设 `LARKSUITE_CLI_NO_UPDATE_NOTIFIER=1`、`LARKSUITE_CLI_NO_SKILLS_NOTIFIER=1`（官方 lark-shared skill 对机器调用的建议），stdout/stderr 显式按 UTF-8 读取；凭据变量见 §5.5。
- 单次 20 秒超时；超时或取消时杀整个进程树。
- 成功信封在 stdout（`{"ok":true,"identity":"bot","data":{…}}`，`data.message_id` 记入结果）；退出码非 0 时错误信封先从 stderr 解析，stdout 只作兜底。输出全文先经 `LarkRedactor` 脱敏（子进程环境里名字含 `SECRET` 的值同样替换）再解析。
- 退出码 0、stdout 却不是单个 JSON 信封（例如新版本多打了一行提示）：lark-cli 用 0 表示发送成功，按已送达处理，类别记 `sent_unverified`。当成失败重试是错的——重试会一直持续到飞书 uuid 一小时去重窗口之外，真的重复送达。
- eventId 是 16 位十六进制，满足飞书 uuid 不超过 50 字符的要求，服务端一小时内按它去重。历史：2026-10-30 之前它因 cmd.exe 截断从未送达，飞书侧去重实际不存在，用户也只看得到首行。

### 5.5 机器人凭据与收件人

凭据来源（取代 ADR-0005 D1 中“app_secret 只在 GUI 侧 CurrentUser DPAPI、Worker 不依赖 DPAPI”的投递设计）：

- 根因：本机开机自启是 S4U 计划任务「AI Resume 续跑引擎」（`scripts/register-autostart.ps1`，需管理员；未注册时退回登录启动快捷方式，在交互会话里运行），Worker 跑在会话 0。会话 0 解 **CurrentUser** DPAPI 会间歇失败（`CryptUnprotectData` 0x8009000B，一次持续数小时；2026-09/10 两次实证）。lark-cli 1.0.81 把 appSecret 存在 HKCU 钥匙串里、用 CurrentUser DPAPI 加密，每次 `--as bot` 都要解一次；v2.1 只把 AI Resume 自己的 DPAPI 从投递路径拿掉，故障点于是移进了 lark-cli，10-14 以来 96% 的投递失败都是它（ADR-0006 §1）。
- v2.2：GUI「飞书通知设置」保存的机器人凭据（App ID + App Secret）以 DPAPI **LocalMachine** 范围写入 `state\secrets\feishu-bot.bin`。密文先写进一个**创建时就带受限 DACL**（断开继承，只留当前用户 + SYSTEM + Administrators）的临时文件，读回核验 ACL 后才原子替换正式文件；收不紧时只丢掉临时文件、报错，上一份可用凭据原样保留，也不存在“已落盘、还没收紧”的时间窗——宁可保存失败，也不留一份本机谁都能解的密文。保存前必须先用这组凭据真实向飞书换到 tenant_access_token，再以该令牌经 lark-cli 调通一次只读的 `GET /open-apis/bot/v3/info`（退出码 0 即通过，应答体不解析、不记录），两步都过才写入（ADR-0007；取代 v2.2 的 doctor 核验——doctor 在 env 来源下从不换令牌，对任何密钥都报 `no tat available`）。
- Worker 每次投递读这份副本，由 `FeishuTenantToken` 向 `POST https://open.feishu.cn/open-apis/auth/v3/tenant_access_token/internal` 换令牌（官方应答字段在顶层：`{"code":0,"msg":"ok","tenant_access_token":"t-…","expire":7200}`，expire 单位秒；令牌最长 2 小时，剩余不足 30 分钟时再换才发新令牌），只经子进程环境变量交给 lark-cli：`LARKSUITE_CLI_APP_ID`、`LARKSUITE_CLI_TENANT_ACCESS_TOKEN`、`LARKSUITE_CLI_BRAND=feishu`、`LARKSUITE_CLI_STRICT_MODE=bot`，先删掉继承来的全部凭据类变量（`APP_SECRET`、`USER_ACCESS_TOKEN`、`DEFAULT_AS`、`AUTH_PROXY` 等），并用 `LARKSUITE_CLI_CONFIG_DIR` 让 lark-cli 只用私有配置目录 `state\lark-cli`。env 令牌本就优先于钥匙串（实验查证）；私有目录里没有应用配置，lark-cli 从结构上碰不到用户钥匙串，不依赖它内部的优先级实现，所以会话 0 里不再碰 HKCU 钥匙串。日志与健康文件记 `credentialSource=env`。
- 令牌只在 Worker 进程内存里按“同一组凭据、剩余 ≥ 5 分钟”复用，不落盘、不进日志；缓存的令牌被飞书判无效时作废、重换一次再发（同一幂等键）。换令牌请求不跟随重定向，代理只认 `HTTPS_PROXY` / `NO_PROXY`（与 Go 的 ProxyFromEnvironment 一致，不读 `ALL_PROXY`；解析不出或 scheme 不支持就直连，绝不落回 WinINet 系统代理）。lark-cli 子进程里先删掉继承来的全部凭据类变量，并用 AI Resume 自有的空配置目录 `state\lark-cli\`，从结构上碰不到用户配置里引用的 CurrentUser 钥匙串。保存前的核验要求机器人信息接口调通且 `bot.activate_status` 为 2（读得到时）。
- 换令牌失败一律通道级、结果明确（消息没发出去，不进死信），日志 `errType=tenant_token`：已知凭据错误码（10003 参数缺失、10012、10013 应用状态异常、10014 app_id 不存在或 App Secret 不对）→ `auth_config`（重新保存即解除）；网络与 5xx → `network`；超时 → `network_timeout`；429 / 99991400 → `rate_limit`；其余（未识别的错误码、9499、跳转或拦截页、读应答时的意外异常）→ `token_unexpected`（短退避）；副本存在却解不开 → `credential_unreadable`（短退避，可能只是正被占用）。细类写进 `errSubtype` 与健康文件 `lastErrorType` / `lastErrorSubtype`，界面悬停标“换取令牌时失败”。注入了令牌 lark-cli 却报 `token_missing` → `cli_contract`，不提示重新保存。设置指纹在发送前记下。这些情况都不启动 lark-cli，**绝不**退回把 App Secret 放进环境变量，也不静默改用钥匙串。
- 没有副本时退回 lark-cli 自己的钥匙串，记 `credentialSource=keychain`；S4U 下它会间歇失败，界面标琥珀“未存凭据副本”。
- 实值只出现在用户输入那一刻、本进程调用栈和 lark-cli 子进程的环境变量里，不进 argv、日志、RPC 回包、健康文件或截图；界面只显示遮蔽后的 App ID。
- 历史：v2.1「飞书接入」保存的 `state\secrets\feishu-platform.bin` 是 CurrentUser DPAPI，只有交互会话能解。「飞书通知设置」在还没有新副本时提供“导入旧版凭据”，同样先真实换令牌并调通机器人接口再写机器范围副本；旧文件原样保留，由用户决定是否清除。

收件人：

- 唯一真身是 `config.json` 的 `notifyRecipientOpenId`（`ProductConfig.NotifyRecipientOpenId`，明文；open_id 不是秘密）。Worker 只读它，不为它解任何密文。只支持一个收件人；多收件人广播要先定义去重与权限语义，尚未扩展。只接受规整后的 `ou_` 加字母数字：先剥掉全部空白（含不换行空格与零宽字符），不硬编码长度。2026-08-08 曾因 open_id 中间夹进一个空格，所有消息都匹配不上，而健康检查全绿。
- 「识别」读取 `lark-cli auth list --json`（lark-cli 已登录的用户）。open_id 按应用隔离，收件人必须是机器人凭据所属应用下的 open_id，跨应用错配会让投递失败（`permission_or_recipient`）。因此已保存机器人凭据副本时，「识别」只列出与它同一应用的用户（比对只在本进程内做，不回传 app_id 实值）；没有同应用用户时提示先用该应用 `lark-cli auth login` 或手动填写。没有副本（凭据在 lark-cli 钥匙串）时不过滤。
- 历史：v2.1 由 GUI 从 cc-connect 的 `allow_from` 第一个 `ou_…` 同步写入，Worker 对更早没有该字段的旧配置回退读 DPAPI。v2.2 起授权名单已不存在，回退也已删除：字段为空时 Worker 整轮记 `recipient_missing`、文件保留，界面红“未设置收件人”。

### 5.6 失败分类、退避、熔断与死信

`NotifyClassifier` 按 lark-cli 1.0.81 的错误契约（退出码、`type` / `subtype`、飞书错误码）给每次失败定类别与作用域：

| 类别 | 判据 | 作用域 | 退避 |
|---|---|---|---|
| `confirmation_required` | 退出码 10 或 `type=confirmation` | 通道 | 30 分钟；绝不自动 `--yes` |
| `keychain_unavailable` | `type=api`、`subtype=unknown` 且 message 以 `keychain ` 开头（message 不是稳定契约，只作兜底识别） | 通道 | 60 秒起翻倍，上限 10 分钟 |
| `rate_limit` | `subtype=rate_limit`，或错误码 230020 / 99991400 | 通道 | 优先 `retry_after_seconds`，否则 2^n 秒，上限 60 秒 |
| `network` | `type=network` 或退出码 4；`subtype` 为 timeout / transport / server_error 时标“结果不明” | 通道 | 30 秒起翻倍，上限 5 分钟 |
| `network_timeout` | 我方 20 秒超时（结果不明） | 通道 | 同 `network` |
| `auth_config` | `type` 为 authentication / config，或 99991663 / 99991664 / 99991665 / 10014 / 10003（换 token 时凭据无效，如开放平台重置过 App Secret） | 通道 | 30 分钟 |
| `permission_or_recipient` | `type=authorization`，或 230013 / 230053 / 230006 / 99991672 | 通道 | 30 分钟 |
| `launch_failed` | 找不到或启动不了 `lark-cli.exe` | 通道 | 30 分钟 |
| `cli_contract` | 30 分钟内两条不同事件命中同一类永久失败（见下） | 通道 | 30 分钟 |
| `invalid_message` | `type` 为 validation / policy，或 230001 / 99992402 | 单条，永久 | 不重试，进死信 |
| `unknown` 等 | 其余（含 `type=internal`）、投递抛异常（`send_exception`） | 单条 | 30 秒 × 2^(n−1)，上限 15 分钟，±20% 抖动 |
| `stale` | 事件发生超过 24 小时仍未送出（§5.3 第 5 步） | 单条 | 不发，进死信 |
| `outcome_unknown` | 第一次“结果不明”已超过 55 分钟（见下） | 单条 | 不再重发，进死信 |

- 单条失败只推迟这一条，新事件照常立即投递；同一条从首次单条失败起超过 24 小时仍未送达 → 移入 `dead\`（类别记为 `retry_exhausted:<类别>`）。
- 永久失败直接移入 `dead\`。但 30 分钟内另一条不同事件又命中同一类永久失败（跨轮记账，记在健康文件的 `permanentClass` / `permanentSinceUtc` / `permanentEventId`；常态下每轮只有一条事件，只看同一轮永远凑不齐两条），就判定是 lark-cli 的参数或输出契约变了，升级为通道级 `cli_contract`，防止一次 CLI 升级把全部通知送进死信。这个判定是粘滞的：通道处于 `cli_contract` 熔断、且熔断之后设置指纹没变时，熔断到期后的任何永久失败继续按通道级再熔断，不进死信——否则只是把“全部进死信”放慢成每 30 分钟一条；积压的事件最终由 24 小时过期规则（`stale`）兜底。任何一次成功送达清空这份记账与熔断。
- 通道级失败立刻熔断：本轮其余条目不再逐条撞墙（记 `channel_blocked:<原因>`），健康文件转 `blocked`；同一原因连续出现时退避加倍。通道级失败一般不计入单条的尝试次数；只有结果不明（如网络超时）时为触发它的那一条记一次尝试和首次不明时间，供 55 分钟封顶使用。熔断到期后，下一轮由最早到期的那一条重新探测，成功即恢复 `healthy`。凭据文件的写入时间或收件人一变（用户刚在界面里改过），熔断立即解除，不等退避结束。熔断到期但那一轮没有可探测的条目时保持 `blocked`，直到下一次真实尝试给出结论，界面不得提前转绿。
- “结果不明”（超时、传输中断，单条级与通道级都算）可能已经送达：重试沿用同一个 idempotency-key，飞书一小时内按它去重。Worker 记下这一条第一次不明的时间，超过 55 分钟就不再重发、移入 `dead\`（`outcome_unknown`）——再往后发就可能真的重复送达；宁可漏报一条，也不在去重窗口之外盲发。

### 5.7 文案

`CompletionNotifier.BuildText` 生成两到三行：第一行说发生了什么；第二行“来源 · 模型短名 · HH:mm · 「会话名」”，模型只在事件带 `model` 时出现（如 `claude-opus-5-5` 显示为 `Opus 5.5`），会话连着 Remote Control 时末尾加“ · 远程已连接”；需要人动手时第三行写怎么做。项目名取 `cwd` 的末级目录名；Claude 桌面端的临时工作区（末级目录形如 `scratch-YYYY-MM-DD-xxxxxx`、上级又有 `scratch-workspaces`，两样都要）目录名对人没有意义，改用会话名，没有会话名写“临时会话”，此时第二行不再重复会话名。临时工作区判据、会话名读法与清洗和控制面续跑队列共用 `ProjectNames`（`IsScratchWorkspace`、`SessionName`、`Clean`），队列行据此显示同一个名字。会话名只对 Claude Code 事件取：先读进程登记 `sessions\<pid>.json` 的 `name`（桌面端侧栏标题、`/rename`），会话已关时退回转录尾部最近一条 `custom-title`；换行与控制字符换成空格（自动标题来自对话内容，不能在飞书里伪造出单独一行），超过 20 个字截断加“…”，只进消息、不进日志。时间按 Worker 所在机器的本地时区。来源名为 Claude Code、Codex、Cline、Qoder、OpenCode、AI Resume。正文不含回答内容、错误详情或完整路径。

| 事件 | 第一行（括号内为第三行建议或说明） |
|---|---|
| 测试 | `🧪 <项目> 测试通知`（能收到这条,说明飞书投递这一段是通的——它不经过任何客户端的 hook） |
| 完成 | `✅ <项目> 已完成` |
| 等你（decision） | `⏳ <项目> ` 加：`等你授权`（permission_prompt）、`协作 agent 等你授权`（worker_permission_prompt）、`等你确认`（elicitation_dialog、elicitation_url_dialog）、`等你输入`（agent_needs_input）、`额度已恢复,按回车即可继续`（quota_auto_resume_stale，Claude Code 原文 press enter to continue）、`运行失败,等你处理`（OpenCode 的 execution_failed）；`quota_auto_resume_disabled` 并入下一行的用量上限通知；认不出的类型说 `等你决定`，绝不编一个更具体的说法 |
| 用量上限中断（`rate_limit` 与 `quota_auto_resume_disabled`，按限额周期合成，§5.3 第 7 步） | 四行。`⏸️ <项目> 被用量上限打断`（多个会话：`⏸️ N 个会话被用量上限打断:a、b、c`，超过 3 个加“ 等”；转录里没有服务端给的额度窗口时只说“被限额打断”，那也可能是 API 的速率限制）；第二行同上（多个会话时模型都相同才写，不写会话名）；第三行 `<窗口>已用完,预计 <时间> 重置`，窗口按 `rateLimitType`：five_hour → 5 小时额度、seven_day → 周额度、seven_day_opus / seven_day_sonnet → Opus / Sonnet 周额度，其余“用量额度”，时间当天只写 HH:mm、次日“明天 HH:mm”、更远 M/d HH:mm，已过重置时刻写“已在 <时间> 重置”，读不到写“是哪个额度、何时恢复未核实”；第四行续跑句见下表 |
| 中断（failure，rate_limit 以外，按 `error`） | `⚠️ <项目> 因登录失效中断`（authentication_failed；在终端运行 claude 并执行 /login）、`因账单问题中断`、`因账号受限中断`（oauth_org_not_allowed、account_on_hold）、`因服务端错误中断`（server_error、overloaded、api_error），其余为 `⚠️ <项目> 运行中断` |
| 续跑与登录（resume，§5.2） | `⏳ <项目> 额度已恢复,会话仍开在客户端`、`▶️ <项目> 已开始自动续跑`、`✅ <项目> 自动续跑完成`、`⚠️ <项目> 自动续跑未确认完成(<结果>)`（结果用界面队列行同一套说法，如“撞限前已有操作”“进程异常退出”，不露原始状态码；建议“打开会话检查结果;这一轮不会再自动续跑,检查后解除并重新布防”）、续跑中因解除或重新布防被停下的 `⏹️ <项目> 自动续跑已中止`（打开会话检查结果;需要时重新布防）、`⚠️ <项目> 一直没能开始自动续跑(找不到 Claude Code 或项目目录 / 执行条件未核实)`（打开控制面查看这一行的原因，修好后会自动重试）；读不到额度时按原因：`⚠️ Claude 访问令牌已过期,AI Resume 暂时读不到额度`（打开一次 Claude Code 或保持手机远程连接即可自动续期）、`⚠️ Claude Code 登录已失效,AI Resume 读不到额度`（在终端运行 claude 并执行 /login）、`⚠️ Claude Code 把登录挪进了 Windows 凭据管理器`（AI Resume 读不到它，自动续跑会一直等待）、`⚠️ Claude 账号身份未核实,自动续跑在等待`（打开一次 Claude Code 让它写入账号信息）、其余 `⚠️ Claude 登录状态未核实,AI Resume 读不到额度`；`⚠️ <项目> 的 Claude 账号与布防时不同`（换了账号，或刚从 v2.1 升级：确认账号后解除并重新布防）、`⚠️ Claude Code 登录即将到期(<MM-dd HH:mm> 到期)`、`⚠️ Claude Code 登录已过期`（后两者都提示在终端运行 claude 并执行 /login） |

用量上限通知的续跑句只按投递那一刻的布防快照说（`NotifyFacts.ReadArm`：严格读 `config.json` 与状态库，读不出就是“未核实”，绝不沿用不看状态的套话）。续跑只认布防时绑定的精确会话 UUID；“会续跑”是唯一一句肯定的承诺，引擎此刻已知只会一直等（要重新布防、登录失效、续跑监视卡住）时不说：

| 布防快照 | 续跑句 |
|---|---|
| 读不出（配置损坏，或已布防却读不了状态库） | `AI Resume 布防状态未核实,不确定会不会自动续跑` |
| 读得出，但续跑监视很久没探过额度（`EngineLiveness` 判 Stalled） | 同上，未核实 |
| 未布防（`armed` 为 false 或没有 `armCycleId`） | `AI Resume 未布防,不会自动续跑;额度恢复后在原会话或手机远程里继续` |
| 本轮已停止（同一轮的 `replayBlocked`、`phase=blocked`、有项目状态落在 success / limited / continued-elsewhere / running 之外，或已布防却 `enabled=false`） | `AI Resume 本轮布防已停止,不会再自动续跑;检查后在控制面解除并重新布防` |
| 要重新布防（布防目标无效或模型对不上、供应商不在了、账号身份与布防时不同、`LastProbeReason=target-unbound`、这个项目的额度原因是 `provider-changed` / `target-unbound` / 认不出模型族，或旧配置的项目没绑定会话） | `AI Resume 要重新布防才会续跑它;在控制面解除并重新布防` |
| Claude 登录失效（`login_expired` / `no_credentials` / `credential_store_moved`；令牌待续期只是等待，不算） | `已布防,但 AI Resume 读不到 Claude 登录,恢复前不会续跑它;在终端运行 claude 并执行 /login` |
| 绑定的就是这个会话 | `已布防:额度恢复后 AI Resume 会续跑它;会话还开着的话 AI Resume 不插手,关掉后才续` |
| 绑定的就是这个会话，但一次性布防这一轮已处理过它（success / continued-elsewhere） | `一次性布防这一轮已处理过它,AI Resume 不会再续跑;额度恢复后在原会话或手机远程里继续` |
| 布防的是同一项目的另一个会话 | `AI Resume 布防的是这个项目的另一个会话,不会续跑这一个;额度恢复后在原会话或手机远程里继续` |
| 不在布防范围 | `不在 AI Resume 布防范围,不会自动续跑;额度恢复后在原会话或手机远程里继续` |

“会续跑”一句只说 AI Resume 自己会做什么（会话开在客户端时它一律不续，关掉才续），不替 Claude Code 承诺它会不会自己接上——Remote Control 会话、跨模型族的周额度它都不等。组里收到过 `quota_auto_resume_disabled` 时：不续跑的各句在第一个分号处补“;Claude Code 也不会自己继续”；会续跑的一句后半改为“;Claude Code 不会自己继续,会话还开着的话要先关掉”。多个会话结论不同时逐行写 `· <项目>「会话名」:<短句>`（如“额度恢复后会自动续跑(开着的话关掉后才续)”“不在布防范围,不会自动续跑”“要重新布防才会续跑”），有未布防、不在范围、另一个会话或这一轮已处理过的会话时，末行加“不会自动续跑的会话,额度恢复后在原会话或手机远程里继续”。

### 5.8 安装、卸载、进程与日志

`install` 先按规范化目标目录获取跨进程命名互斥体，再把四个项目（Gui / Worker / Hook / Launcher）的完整产物复制到同级 staging 目录，并校验 GUI / Worker / Hook 三个必需运行文件，写入 payload 清单，再备份本次会覆盖或由新清单淘汰的旧运行文件；只有 staging 与备份都成功后才停止旧 GUI/Worker。新 Worker 必须同时满足“进程未退出”和“Named Pipe `ping` 返回当前协议版本，且 pong PID 等于本次启动 PID”才算就绪；在此之前不修改快捷方式或用户级 Hook。复制、入口创建或就绪核验失败会回滚已替换和已淘汰文件，并在原来有 Worker 时重新启动旧 Worker。回滚不完整时返回独立错误码并保留 staging/backup 的绝对路径。安装目录内的 Worker 不能在 Windows 上删除自己，因此卸载会复制清单拥有的临时运行时：helper 先处理快捷方式和通知源，再把全部 payload、marker 与 manifest 事务性移动到私有退役区，完成后才向父进程返回成功。移动失败原路恢复，恢复不完整则保留 helper 目录；成功后的清理只碰 temp 退役区，不会与立即重装竞争。未知文件存在时写 preserved-root marker，使后续重装可继续，但该 marker 不能授权卸载。`state\`（含机器人凭据副本、事件队列与状态文件）始终保留。

通知源对账：`install` 在 Hook 二进制已复制、新 Worker 就绪之后，按 `NotifyIntent.Targets`（意图 ∪ 此刻已启用，再 ∩ 本机装了该工具）逐个原位刷新。不先关后开，避免第二步失败时拆掉原本能用的钩子；目标里包含已经开着且健康的源，要把它们重指到新的安装目录。单个源失败不终止其余，意图按目标整体写回（失败项保留，下次重试），写完再探测一遍复核；任何一项没对齐，`install` 返回 2。这次探测失败（`Unverified`，配置读不出、开没开不知道）的源不进目标、不改写它的配置，意图原样保留，同样按未对齐返回 2——不能把“读不出”当成“没装”抹掉意图，否则投递端会把它之后的事件全部丢弃。OpenCode 插件一写入就会被热重载，所以必须先换 Hook 二进制、后写插件，否则中间这段时间的新事件会被旧 Hook 拒绝。`uninstall` 关闭前先把此刻已启用的源记为意图（探测失败的源保留原意图），再只移除 AI Resume 自己的条目。

`NotificationWorker` 与续跑引擎在同一个 Worker 进程里。日志写 `logs\worker-yyyyMMdd.log`（结构化单行 JSON）：

- 启动时记 `worker.notify.channel_init`（`larkCli=exe`，找不到为 `missing`；`credentialSource=env` 或 `keychain`）与 `worker.notify.started`。
- 每个真正尝试过或状态有变的条目记一条 `worker.notify.item`：`Sent` / `Duplicate` / `Superseded` / `Suppressed` 记 `eventId`、`source`、`outcome`、`detail`（稳定原因码：`source_disabled`、`session_not_persisted`、`same_limit_cycle`、`merged`、`newer_completion`、`withdrawn`）、`durationMs`、`credentialSource`；失败、死信与坏事件另记 `class`、`scope`、`exit`、`errType`、`errSubtype`、`errCode`、`logId`、`ambiguous`、`attempts`。稳定期、退避、熔断中的暂缓与 `Skipped` 不逐条记。
- 通道状态只在变化时记一条 `worker.notify.channel`（`state`、`until`、`backlog`）；去重表与状态文件的读写问题记 `worker.notify.diagnostic code=…`。
- 整轮异常只记 `worker.notify.sweep_failed error=<异常类型>`：通知是辅助功能，绝不能把负责续跑的 Worker 拖崩。
- 日志不写消息正文、项目路径、会话名、收件人或凭据。

### 5.9 控制面：主界面、「飞书通知设置」与 CLI

`NotifyControlPlane` 是 GUI 与 CLI 共用的控制面；每一句肯定都来自外部证据（文件、真实投递结果、真实换令牌与机器人接口调用、`lark-cli doctor`）。

主界面右栏「完成通知 → 飞书」：

- Claude Code、OpenCode 两行开关（其余三个源在「视图 › 显示其他通知源」打开后才列出）。没检测到该工具时显示“未检测到”，开关不可点；`hookBroken` 时红“hook 失效”（OpenCode 行为“插件失效”；悬停看原因，关掉再打开可修复）；已启用但说明里还写着“核实”（OpenCode 尚无加载记录）时琥珀“待核实”；其余已启用的为“hook 正常”（命令指向的 `AiResume.Hook.exe` 在）——OpenCode 行为“插件已加载”，证据只来自它自己的日志。探测本身失败（配置正被改写、被占用，`notifications.list` 的 `unverified=true`）时显示“未核实”，开关画成既非开也非关的虚框（读屏为 mixed），点按只重新读取、不去改一个读不出来的配置，不说“已关闭”或“未检测到”。隐藏的源 hook 失效、读不出配置或仍启用时，下方留一句“… 的通知 hook 失效”“… 通知源状态未核实”或“另有 … 通知仍开启”，后接“ · 视图 › 其他通知源”。
- “飞书机器人”一行按以下顺序取第一条成立的结论：读不到通道状态 → “未核实”；没装 lark-cli → 红“未安装 lark-cli”；副本在却读不出 → 红“凭据副本读不出”（压过历史成功，也不说“lark-cli 默认应用”）；没设收件人 → 红“未设置收件人”；后台服务（Worker）不在（`arm.get` 的 `workerRunning=false`）→ 红“后台服务未运行”，即使健康文件里有过成功记录；健康文件为 `blocked` → 红，短句取熔断原因（如“读不到机器人凭据”“凭据或配置无效”“权限或收件人有误”“被飞书限流”“网络不通”“lark-cli 无法启动”），悬停给错误码与 log_id；后台服务在不在还没读到（`arm.get` 尚未返回）→ “核验中…”；核验不出（`workerRunning` 为 `null`：进程枚举失败，或有同名进程但本用户常驻 Worker 的 Named Pipe 不应答；`arm.get` 读失败时页面也按 `null` 处理）→ “未核实”；最近一次真实尝试失败（`lastAttemptOk=false`，条目级失败如收件人跨应用、未知错误码）→ 琥珀“最近投递失败”，悬停给类别、错误码、log_id 与上次成功时间；有成功记录但那之后改过凭据、收件人或 lark-cli（`settingsVerified=false`）→ 琥珀“设置已变更”，比不出来（`settingsVerified` 为 `null`，如没有成功指纹）→ 琥珀“待核实”，都点「发送测试」核实；健康文件为 `healthy`、有成功记录且 `settingsVerified=true` → 绿“可送达”；AI Resume 没存机器范围副本、只能用 lark-cli 钥匙串 → 琥珀“未存凭据副本”（钥匙串里有没有凭据并未核实；悬停说明后台服务可能读不到，建议保存一份）；其余 → 琥珀“待核实”。`settingsVerified` 由控制面比较健康文件的 `successFingerprint` 与当前设置指纹（`NotifyFingerprint`：机器人凭据副本写入时间 + 规整后的收件人 + lark-cli 位置，SHA-256 前 16 位）；Worker 熔断时用同一指纹判断“设置一变即解除”。下方备注“发给 <遮蔽的 open_id> · 待发 N · 未送达 N”（待发取健康文件的 `backlog`，未送达是 `dead\` 里的事件数）。
- “设置…”打开「飞书通知设置」；“发送测试”（与菜单「工具 › 发送测试通知」相同）先查 lark-cli、收件人与后台服务，缺一样就直接说明、不排队；排队后每 5 秒用 `notify.test.status` 问**这一条**测试通知的结局（与 `notify test` 同一判据），送达、没能送达且已停止重试（进了 `dead\`）、这一条确定失败（带类别、错误码与 log_id）或飞书投递此刻暂停（通道熔断）都如实说；失败、暂停或 90 秒没送达时用 `notify.test.withdraw` 撤回它，免得通道恢复后补发一条过时的测试通知。只有撤回成功才说“已撤回”；撤不回（后台服务正在发这一条）或撤回请求失败时再看一眼结局，如实说“已送达”“没能送达,已停止重试”或“撤回未确认，稍后仍可能收到”。
- 页脚右侧显示“后台服务运行中 / 未运行 / 未核实”：自动续跑和完成通知都由它（`AiResume.Worker`）执行。“运行中”要有本用户常驻 Worker 的 Named Pipe 应答；同名的 `notify test`、`install` 这类 CLI 进程不开管道，只算“未核实”。

「工具 › 飞书通知设置…」对话框：

- 顶部一行与主界面机器人行同一结论。
- 机器人凭据：App ID（`cli_` 开头）与 App Secret。「保存并验证」先向飞书真实换令牌、再以令牌经 lark-cli 调一次机器人接口，两步都过才以机器范围 DPAPI 保存；存后界面读不回实值，只显示遮蔽的 App ID。有旧版 `feishu-platform.bin` 而还没有新副本时出现「导入旧版凭据」；已有副本时可「清除凭据」（二次确认，之后退回 lark-cli 钥匙串）。
- 收件人：填写 `ou_…`，或点「识别」从 `lark-cli auth list` 列出已登录用户后选用（已保存机器人凭据时只列同一应用的用户，§5.5）；「保存」只接受规整后合法的 open_id。没有可选用户时提示先运行 `lark-cli auth login` 或手动填写。
- 「诊断通道」用投递实际会用的凭据来源诊断：有副本时先真实换令牌（`tenant_token`）、调一次机器人接口（`bot_api`，两步决定“机器人身份就绪”），再附注入令牌后的 `lark-cli doctor`（没换到令牌就不跑）；没有副本时只跑 doctor。逐项显示检查结果，以及“机器人身份就绪/未就绪 · 凭据来源”。doctor 的说明文字会原样带出完整 app_id，界面与 `notify check` 一律把其中的 app_id 与 open_id 遮蔽后再显示。

GUI RPC：`notify.channel`（只读状态）、`notify.check`（副本：换令牌 + 机器人接口 + doctor；钥匙串：doctor）、`notify.credential.set` / `notify.credential.import` / `notify.credential.clear`、`notify.recipient.set` / `notify.recipient.detect`、`notify.test` / `notify.test.status` / `notify.test.withdraw`；通知源开关仍是 `notifications.list` / `notifications.setEnabled`（启用时只把 `AiResume.Hook.exe` 的绝对路径交给适配器，找不到就拒绝启用；停用不要求 Hook 文件仍在；先把意图写进配置再改客户端配置，意图写不进去就失败、开关不动，改客户端失败就把这一项意图恢复原样——意图也是投递端的来源过滤）。应答只含结论、来源与遮蔽后的标识。“显示其他通知源”等偏好只存本机 WebView2 的 `localStorage`。

CLI（`AiResume.Worker.exe notify …`）：

| 子命令 | 作用 | 退出码 |
|---|---|---|
| `list`（默认） | 五个源的 已安装 / 已启用 / 可送达 / 说明；失效时可送达列为 `False(hook 失效)`（OpenCode 为 `False(插件失效)`） | 任一已启用的源失效返回 2，否则 0 |
| `enable <源>` / `disable <源>` | 源名为 `ClaudeCode`、`Codex`、`Cline`、`Qoder`、`OpenCode`（不分大小写）；先写意图再改客户端配置（`NotifyIntent.Apply`：改客户端失败时再探一次实际状态，只有证明没生效才恢复原意图，已部分生效或探不出时保留这次的选择）；启用时找不到 Hook 就拒绝 | 成功 0，失败 1 |
| `check` | lark-cli 是否找到、机器人凭据来源、收件人、最近投递（健康文件：状态、原因、最近一次尝试成没成、最近成功、待发、未送达；成功之后改过设置时另起一行提醒重新核实），再做一次在线诊断（有副本：换令牌 `tenant_token` + 机器人接口 `bot_api` + 注入令牌后的 doctor；没有副本：doctor），最后打印诊断用的凭据来源 | lark-cli 在、收件人已设、bot 身份就绪、通道没有熔断且最近一次尝试不是失败时为 0，否则 2（诊断只证明机器人身份，证明不了收件人可达） |
| `test` | 排队一条测试通知，最多等 90 秒看后台服务经 lark-cli 真实送达 | **这一条**测试通知进了已送达表（`completion-notify-seen.json`）且送达时间不早于排队时刻为 0（别的事件同时成功不算；撤回时后台服务恰好正在发这一条、随后确认送达也算）；没能送达且已停止重试（进了 `dead\`）、这一条确定失败（`TestFailure` 给出类别、错误码与 log_id）、飞书投递此刻仍暂停（通道熔断；到期了或改过设置都不算）或 90 秒超时为 2，失败、暂停与超时时撤回这条通知，免得通道恢复后补发一条过时的测试通知；撤不回时如实提示可能仍会送达 |

未知子命令返回 1。

## 6. 验证层级

### 离线回归

```powershell
dotnet test csharp\AiResume.sln
```

关键覆盖：

- 各来源的事件匹配、内部运行与 `stop_hook_active` 抑制、Codex rollout 准入、OpenCode 2.x 事件准入，以及同一上游 `event_id` 经不同实例到达只写一次（`HookHandlerTests`）。
- 真正启动 `AiResume.Hook.exe`，覆盖 Codex argv 与其余来源的 UTF-8 stdin（含 OpenCode `session.execution.succeeded`），用中文 cwd 断言字节边界，非法 UTF-8 拒绝入队，BOM 正确剥离（`NotificationHookProcessTests`）。
- 真正执行 Cline PowerShell wrapper，验证旧脚本和 AI Resume 同时收到未经损坏的中文事件。
- Claude Code 三组 exec form 的写入、幂等、旧 shell form 迁移、混在用户分组时移出单独成组且不改用户 matcher、停用只删我方、所有权边界（`ClaudeCodeNotificationAdapterTests`）。
- OpenCode v2 插件的目录形态、命令常量往返（空格、引号、中文路径）、旧单文件迁移与 `.bak`、拒绝覆盖用户入口文件、日志证据（断链 / 已加载 / 待核实）；生成的插件源码原样当 `.mjs` 在 Node 下跑 21 个 v2 事件场景，包括顶层完成只报一次、子会话不报、父会话有运行中子会话时抑制、宽限期内重新开跑不报、授权与提问各报一次、顶层失败报 `execution_failed`、会话查询失败 fail-closed、spawn 失败与事件流中断后循环不停、卸载后不再投递（`OpenCodeNotificationAdapterTests`，需要本机有 Node）。
- 投递分级：stderr 错误信封按类别分到单条或通道、成功信封记 message_id、定位 lark-cli 只认 exe 绝不返回 cmd 垫片、`doctor` 与 `auth list` 按录制形状解析、机器人凭据存为机器范围密文且能读旧版形状、通道熔断只尝试一次且设置一变即解除、单条失败不拖累新事件、永久失败进死信且 30 分钟内跨轮同类升级为熔断、超过 24 小时的事件不补发、结果不明 55 分钟后不再重发、退出码 0 的非信封输出按已送达、最近一次失败不再显示可送达、按 `atUtc` 排序、Claude 稳定期/替代/后台暂缓（含已送出更晚完成时作废）、用户关掉的通知源不发（意图三态：从未记录不过滤，记录过的空意图全部丢弃）而 AI Resume 自己的事件照发、各类文案与模型短名（`NotificationDeliveryTests`、`NotificationWorkerTests`、`CompletionNotifierTests`）。
- 2026-11-02 的三条规则：没落盘的会话不发（稳定期内先等，判断不了与旧事件照发）、同一周期同时撞上的会话合成一条、同一会话同一周期再撞不再发而别的会话照发、新周期再发、读不到重置时间按会话记账 5 小时且不断言是用量上限、刚到的中断先不判、事件晚于转录里的重置时间时那份证据作废、两个配置根同一时刻重置各发各的、组没攒齐先等而最早一条满 2 分钟就发、续跑句按布防快照的每个分支（含要重新布防、登录失效、监视卡住、读快照抛异常）、原生自动继续放弃并入且单独到达也按限额模板、多个会话结论不同时逐行说、会话名清洗与临时工作区判定、代表那条进死信或过期作废时同组的处理、续跑没成的提醒不露原始状态码（`LimitNotificationTests`）；`NotifyFacts` 按 2.1.309 录制形状读转录尾部（离谱的重置时间不采信、转录变了才重读）、进程登记与 `custom-title`，布防快照判出本轮已停止、要重新布防（目标无效、账号变了、额度原因）、登录失效与监视卡住，配置损坏或已布防却没有状态库时为未核实（`NotifyFactsTests`）。
- `LarkCliInvokerTests` 用本机 lark-cli 1.0.81 录下的 stderr 信封断言，并验证含换行的 `--text` 与 `--idempotency-key` 原样到达、环境变量里的机密被脱敏。
- 换令牌（ADR-0007）：成功应答字段在顶层、录制的 10014 / 10003 / 9499 与 429 / 5xx / 非 JSON 分类、请求形状、缓存复用与临期重换、换凭据与作废、网络与超时、生产处理器不跟随重定向；通道只注入 App ID + 令牌、换不到令牌不启动 lark-cli、缓存令牌被拒重换一次、副本读不出不改用钥匙串（`FeishuTenantTokenTests`）。
- 控制面：只有真实换到令牌且机器人接口调通的凭据才落盘、换不到令牌时诊断不跑 doctor、收件人规整与按应用过滤的识别、成功之后改过设置则成功记录不再作数、doctor 说明里的 app_id 与 open_id 一律遮蔽、测试通知走真实队列、登录到期按阈值各提醒一次且没收件人时不写（`NotifyControlPlaneTests`，假 lark-cli 照抄 1.0.81 的录制形状）；GUI 视图模型“通知行可证伪、隐藏的源仍启用必须提示、机器人行只认真实投递、Worker 不在或核验不出时不显示可送达、最近投递失败与设置已变更不显示绿色”（`GuiPageContractTests`）。
- 配置合并、幂等启停、含空格路径、严格所有权、同名用户文件保护与意图对账（各 `*NotificationAdapterTests`、`NotifyIntentTests`、`HookHealthTests`）。

所有自动测试只使用 `TestTemp` 临时目录和合成负载，不启动真实 AI 任务，不读写生产项目或生产会话，也不发飞书消息。

### 安装后诊断

```powershell
$worker = "$env:LOCALAPPDATA\AI Resume\AiResume.Worker.exe"
& $worker notify list
& $worker notify check
& $worker notify test
```

- `notify list`：已启用来源必须显示 `可送达=True` 且退出码为 0；Codex 行还必须显示信任哈希已登记（否则为 `False(hook 失效)`）。这一列只说明 hook 这一段：配置在、命令指向的程序在、没有被上游证据（Codex 信任记录、OpenCode 日志）判为断链。OpenCode 插件尚无加载记录时这一列仍为 `True`，说明里写着“等 OpenCode 下次加载后核实”。飞书这一段是否可送达，看下面两条。
- `notify check`：确认 lark-cli 找得到、机器人凭据来源为“AI Resume 机器范围副本”、收件人已设置、诊断里 `tenant_token` 与 `bot_api` 为 pass（副本路径；钥匙串路径看 doctor 的 `bot_identity`）、最近投递不是熔断。它在交互会话里运行：凭据来源若是 lark-cli 钥匙串，这里通过也不代表会话 0 里的 Worker 能用。
- `notify test`：测试通知走完整的“队列 → 后台服务（Worker）→ lark-cli → 飞书”链路，等到真实送达才返回 0，会真实发出一条飞书消息，手机上应收到三行：`🧪 AI Resume 测试通知`、`AI Resume · HH:mm` 与 `能收到这条,说明飞书投递这一段是通的`。它不启动、也不修改任何 AI 会话。

按来源核对 Hook 这一段（可选）：按各来源的真实协议，向安装目录的 `AiResume.Hook.exe` 的 stdin 注入带 `smoke=true` 的合成负载。例如 Claude Code 用参数 `claudecode` 与 `{"hook_event_name":"Stop","session_id":"smoke","cwd":"<某个绝对路径>","smoke":true}`；OpenCode 用参数 `opencode`，`hook_event_name` 为 `session.execution.succeeded` 并带 `event_id`。飞书应分别收到第二行为 `Claude Code · HH:mm` 与 `OpenCode · HH:mm` 的测试通知（`smoke=true` 的事件不受“会话没落盘”规则约束）。注入的来源必须是已开启的通知源，否则 Worker 按意图丢弃（`Suppressed`）。中文路径要保证 stdin 是 UTF-8（PowerShell 7 先把 `$OutputEncoding` 设为无 BOM 的 UTF-8），否则 Hook 会按非法 UTF-8 拒绝；用不含中文的临时目录更省事。

### v2.2 真机验收

第 1 到 4 条已于 2026-11-02 走通（记录见清单后）；第 5、6 条仍待真机验证。

1. `dotnet build csharp\AiResume.sln -c Release` 后运行 Release 目录的 `AiResume.Worker.exe install`，退出码 0；在 Claude Code 里用 `/hooks` 能看到 `Stop`、`StopFailure`、`Notification` 三组都指向安装目录的 `AiResume.Hook.exe`。
2. `notify list` 退出码 0。OpenCode 下次加载后，其日志里出现本插件目录的 `loading plugin`，之后没有 `failed to load plugin`；`notify list` 的 OpenCode 说明随之变为 `已安装,OpenCode 已加载(日志 …)`。
3. 在「飞书通知设置」保存或导入机器人凭据，识别并保存收件人；`notify check` 返回 0。
4. `notify test` 返回 0，手机收到这条测试通知；Worker 日志有 `worker.notify.channel_init larkCli=exe credentialSource=env` 与这条事件的 `worker.notify.item … outcome=Sent … credentialSource=env`。注意 `install` 刚拉起的 Worker 跑在当前交互会话里；要证明会话 0 这条路径，须在 Worker 由 S4U 计划任务拉起后（例如重新登录后，`Get-Process AiResume.Worker` 的 `SessionId` 为 0）再跑一次 `notify test`。可选：发送时刻 `Microsoft-Windows-Crypto-DPAPI/Operational` 不再出现新的 8198 错误。
5. 观察 3 到 7 天：失败率接近 0，仍有的失败在日志里带 `class` / `errCode` / `logId`。断网一次，健康文件应转为 `blocked`（`network`），恢复后第一条到期事件探测成功即转回 `healthy`。
6. 需要真实事件、不能由测试代劳的：一次真实的 Claude Code 完成；一次用量上限中断，核实 `StopFailure` 的 `error` 是否为 `rate_limit`（官方文档没有写明 claude.ai 用量上限对应哪个值）；OpenCode 2.2.3 里一次真实任务完成；一次真实中断后的逐项目自动续跑（`resume-started` / `resume-succeeded`）；Claude Code 登录到期提醒（本机登录 2026-11-11 05:55 UTC 到期，第一条“即将到期”应在到期前 3 天内到达）。

2026-11-02 实测（ADR-0007 方案 A 部署后）：全量回归 `1303/1303`，Release `-warnaserror` 0 警告，`install` 退出码 0。用户在「飞书通知设置」重新保存凭据（保存时真实换令牌、再调一次机器人接口）之后：

- `notify check` 退出码 0：凭据来源为机器范围副本，`tenant_token` 与 `bot_api` 为 pass，doctor 的 `bot_identity` 为 `provided by env`，换令牌网络路径为 `direct`。`notify list` 退出码 0，已启用的 Claude Code、OpenCode 均为 `可送达=True`，OpenCode 说明为已加载。
- 交互会话里的 Worker（`install` 拉起）：GUI「发送测试」与 `notify test` 各一条，日志均为 `outcome=Sent … credentialSource=env`，各约 0.8 秒；健康文件 `credentialSource=env`、`lastAttemptOk=true`；部署后的真实窗口里机器人行为绿色「可送达」。
- 会话 0：按 PID 停掉 `install` 拉起的 Worker，`schtasks /Run` 让 S4U 计划任务拉起新实例（`SessionId=0`），日志 `channel_init … credentialSource=env`；`notify test` 15 秒内返回 0，`outcome=Sent … credentialSource=env`，857 ms。这是会话 0 经副本真实送达的第一条记录；当天 DPAPI 运行日志没有 8198（上一条在 10-30）。验证后已结束任务实例，并用启动垫片把 Worker 拉回交互会话。
- 私有配置目录 `state\lark-cli` 里只有 lark-cli 自己的 `cache\remote_meta.meta.json`（只含 `last_check_at`），令牌与凭据都没有落盘。
- 同时发现（与方案 A 无关，另行处理）：Worker 由 S4U 计划任务跑在会话 0 时，交互会话以读写方式连它的命名管道被拒（Access denied），GUI 因此显示「后台服务未核实」，机器人行随之是「未核实」；投递本身不受影响。

### 历史记录（v2，v2.1 之前）

2026-08-09 最终安装版实证：仓库全量回归为 `972/972`。安装目录 Worker/Hook 与本次构建哈希一致；`notify list` 五源均为 `可送达=True`；`feishu-check` 返回 `code=0`；协议冒烟分别记录 `claudecode/cline/qoder/opencode` 的 `total=4 sent=4 failed=0` 与 Codex 的 `total=1 sent=1 failed=0`，五个来源均出现 `outcome=Sent`，队列归零。Codex 单独一轮是为了使用与真实客户端一致的现代 argv 传递，避免 Windows PowerShell 5.1 改写 JSON 参数。这里验证的是五种真实 Hook 协议到飞书的完整链路，不代表为了测试而让五个客户端各执行了一次真实 AI 任务。

2026-08-13 当前安装版复核：Release 构建 `0 warning / 0 error`，全量回归 `1089/1089`、0 skipped；在 stdout/stderr 被父进程捕获的环境中执行 `install` 约 3.7 秒正常返回，安装清单 108 项与 Release 合并产物逐文件 SHA-256 全部一致，新 Worker 只有一个稳定 PID。`notify list` 五源仍全部 `可送达=True`，`feishu-check` 为 `code=0`。按当时 `config.toml` 的完整 `codex-computer-use.exe → --previous-notify → AiResume.Hook.exe` argv 链注入合成 `smoke=true` 事件，Hook 入队、Worker `outcome=Sent`、队列归零；未启动真实 agent，也未调用模型推理端点。

这两次记录的局限（2026-10-30 回看）：`feishu-check`（已于 2026-10-30 随 ADR-0006 删除）只在交互会话里验证 AI Resume 自己保存的凭据能否换 token，测不到会话 0 里 lark-cli 读钥匙串的故障；当时 lark-cli 经 cmd.exe 垫片启动，消息只到首行，五条冒烟内容相同、分不出来源，`--idempotency-key` 也没有送达。它们证明的是 Hook → 队列 → Worker 这一段，不能证明 v2.1 的飞书投递在后台可靠。

## 7. 故障定位

| 现象 | 首查 | 判据 |
|---|---|---|
| 开关打开但从未入队 | 客户端配置和 Hook 命令 | 来源参数正确，exe 是绝对路径且存在（`HookHealth`）；Claude Code 是三组 exec form |
| 已入队但日志是 `outcome=Suppressed` | `config.json` 的 `notifySources` | 该来源不在意图里（已关掉），残留链路写的事件被丢弃；想收它就重新打开开关或 `notify enable <源>` |
| Claude Code 在某个新目录从不通知 | 工作区信任 | 交互式会话在接受工作区信任对话框之前不执行任何 settings hook；接受后即生效（settings 热加载，不必新开会话） |
| Claude Code 行红“hook 失效”，说明“hook 不完整或为旧格式” | `~/.claude/settings.json` | 三组缺一组或仍是旧 shell form；关掉再打开开关或重新 `install` |
| 第三方工具（用量监视器、脚本里的 `claude -p --no-session-persistence`）拉起的 claude 不再通知 | 设计如此 | 没有转录的会话打不开、续不了，日志记 `outcome=Suppressed detail=session_not_persisted`；想收到就让它落盘 |
| 同一个会话在周额度用完期间只收到一条“被用量上限打断” | `completion-notify-limits.json` | 每个会话每个限额周期（`rateLimitType + resetsAt`）只提醒一次，之后的记 `Suppressed`（`same_limit_cycle`）；别的会话撞上同一周期照样提醒；重置后再撞上是新周期，会再提醒 |
| 限额通知说“布防状态未核实”或“是哪个额度、何时恢复未核实” | `config.json`、`runs.db`、会话转录、Worker 日志 | 配置读不出、已布防却读不了状态库、续跑监视很久没探过额度，或转录尾部没有带 `quotaLimits` 的限流记录（旧版 Claude Code、API 速率限制）；读不出就不猜 |
| 限额通知说“要重新布防才会续跑它” | 控制面状态灯与这一行 | 布防目标无效、账号与布防时不同（换了账号或刚从 v2.1 升级）、或引擎已判这个项目要重新布防；解除并重新布防 |
| 用量上限、登录失效从不通知 | Claude Code 版本、`StopFailure` 分组 | `StopFailure` 需要 2.1.78+，exec form 需要 2.1.139+；`/hooks` 里应能看到 StopFailure 指向 AI Resume |
| Claude VS Code 曾经不通知 | VS Code Claude 日志、版本、全局 `settings.json` | 2.1.232 曾实测缺失；后续版本已实证执行 `Stop`，不要沿用旧版本结论 |
| 后台子代理还在跑就收到“已完成”，或一个任务收到两条完成 | 事件的 `backgroundBusy`、会话 | `background_tasks` 里有在途（running / pending）的子代理、workflow 或云端会话时 Worker 最多暂缓 30 分钟；同会话只有 2 分钟内更新的完成才会替代旧的 |
| 每次“已完成”都晚 30 分钟才到 | 会话里的常驻后台任务 | v2.2 交叉审查之前，monitor（例如发布 Artifact 后自动订阅评论的那个）、teammate、开发服务器 shell 之类常驻任务也算“还在忙”；现已只认会唤醒会话的子代理、workflow 与云端会话 |
| 只有中文项目名乱码 | Hook stdin 与 Cline wrapper | stdin 必须按 UTF-8 解码；不要在 Worker/飞书端猜测反转乱码 |
| Codex 独有不通知 | `~/.codex/hooks.json`、`config.toml` 的 `[hooks.state]` 与 `[features]`、会话启动时间 | Stop 分组存在且 `trusted_hash` 等于当前哈希（`notify list` 不报断链；否则在 codex `/hooks` 信任）；`[features] hooks = false` 会让 Codex 整体不跑 hooks；会话早于 hooks.json 写入时新开会话；`transcript_path` 指向顶层 persisted rollout |
| Cline 独有不通知 | `TaskComplete.ps1` | `$stdin` 同时管道给旧脚本和 `AiResume.Hook.exe cline` |
| OpenCode 独有不通知 | OpenCode 版本、`opencode.log`、`plugins\airesume-notify\index.ts` | 只支持 2.x；日志里本插件目录出现 `failed to load plugin` 即断链（附原因）；只剩旧单文件 `airesume-notify.ts` 时关开一次即升级；还没有加载记录时等 OpenCode 下次加载 |
| 机器人行红“未安装 lark-cli”或“lark-cli 无法启动” | npm 全局包 | `npm i -g @larksuite/cli`；必须存在 `node_modules\@larksuite\cli\bin\lark-cli.exe`，AI Resume 不经 `lark-cli.cmd` 垫片 |
| 机器人行红“读不到机器人凭据” | 凭据来源 | `keychain_unavailable`：会话 0 里的 Worker 读不了 lark-cli 钥匙串；在「飞书通知设置」保存或导入机器人凭据，保存后熔断立即解除 |
| 机器人行红“凭据或配置无效” | `notify check` | 飞书拒绝了凭据或应用配置（含应用已停用）；重新保存并验证 |
| 机器人行红“权限或收件人有误” | 应用权限、可用范围、收件人 | 机器人没有发消息的权限，收件人不在应用可用范围或关闭了机器人消息，或 open_id 不属于该应用；用「识别」从同一应用重选 |
| 机器人行红“被飞书限流”“网络不通”“网络超时” | 网络与频控 | 通道级熔断，自动退避重试；长时间不恢复时看日志里的 `errCode` / `logId` |
| 机器人行红“未设置收件人” | `config.json` 的 `notifyRecipientOpenId` | v2.1 之前的旧配置没有该字段，v2.2 不再回退 DPAPI；在「飞书通知设置」识别或填写 |
| 机器人行红“后台服务未运行” | Worker 进程、开机自启 | 通知只排队不发；重新 `install` 或重新登录 Windows；停了超过 24 小时的事件恢复后不会补发（`stale`） |
| 机器人行琥珀“最近投递失败” | 悬停的类别/错误码/log_id、`dead\` 的 `.error.txt` | 最近一次真实尝试是条目级失败（收件人跨应用、未知错误码……）；修正后点「发送测试」，成功即转回“可送达” |
| 机器人行琥珀“设置已变更” | 刚改过的凭据、收件人或 lark-cli | 最近一次成功是在旧设置下取得的；点「发送测试」或 `notify test` 重新核实 |
| 队列有文件但飞书没有 | Worker、健康文件、日志、`dead\` | `completion-notify-health.json` 的 `state` / `reason`；日志 `worker.notify.item` 的 `outcome=Failed` 或 `Dead` 及其 `class` / `errCode` / `logId`；死信看同名 `.error.txt` |
| 只有安装后突然失效 | 安装后的 Worker | 新 Worker 已立即启动，不是只留下登录自启快捷方式 |
| 重复消息 | eventId / seen / idempotency | 同一事件七天内本地去重；飞书侧同一 idempotency-key 一小时内去重；“结果不明”的重发在首次不明 55 分钟后停止，不会在去重窗口之外盲发；Claude App 推送与飞书是两条独立通道 |
| 一条通知始终没收到，`dead\` 里有它 | 同名 `.error.txt` 的类别 | `stale`：发生超过 24 小时才轮到投递；`outcome_unknown`：结果不明且已过 55 分钟，可能其实已送达；`retry_exhausted:*`：单条重试 24 小时未成；其余为参数类永久失败 |
| 消息只有一行、分不出来源（历史） | lark-cli 启动方式 | v2.1 经 cmd.exe 垫片截断；v2.2 直接启动 exe 后不应再出现，出现即说明启动路径回退了 |
| 通知间歇几小时发不出（历史） | 凭据来源 | v2.1 是会话 0 里的 CurrentUser DPAPI（先是 AI Resume 自己的，10-12 后是 lark-cli 钥匙串）；v2.2 日志里的 `credentialSource` 应为 `env`；若 env 下报 `no tat available` 或熔断原因为 `auth_config` 且 `errType=tenant_token`，按 ADR-0007 核对换令牌这一步 |
