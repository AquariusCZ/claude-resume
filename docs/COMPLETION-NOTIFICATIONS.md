# 完成通知协议、实现与验证手册

最后验证：2026-08-20（Windows 11，AI Resume v2）；2026-10-12 v2.1 把 Codex 通知改为 hooks、收件人明文化（§2、§3、§4、§5、§7），真机记录见 §6 末尾。

## 1. 从第一性原理定义“可用”

完成通知不是“配置里有一条 Hook”就算可用。完整链路必须同时满足：

1. 上游客户端在**整个 agent 任务结束**时产生可靠事件，而不是每次模型请求结束都触发。
2. 上游把事件正文按其真实协议送入 `AiResume.Hook.exe`。
3. Hook 能确定来源、会话和绝对工作目录，拒绝内部运行、递归 Stop、子代理和无法归属项目的事件。
4. Hook 只做本地原子入队，不读取飞书凭据、不联网，也不阻断上游客户端。
5. 常驻 `AiResume.Worker.exe` 消费队列，通过 `lark-cli` 以 bot 身份投递给授权用户。
6. 本地七天去重和飞书 `idempotency-key` 同时生效；失败事件保留重试，坏 JSON 隔离。

任何一层缺失都会表现成“开关已开，但飞书没有消息”。

```text
agent completion event
        |
        v
AiResume.Hook.exe -- normalize/admit --> state/completion-events/*.json
                                                |
                                                v
AiResume.Worker NotificationWorker --> lark-cli --> Feishu
```

## 2. 上游盘点与采用结论

| 来源 | 整任务边界 | 正文传输 | AI Resume 接入 |
|---|---|---|---|
| Claude Code | `Stop` | JSON 写入 stdin | `"AiResume.Hook.exe" claudecode` |
| Codex | hooks `Stop`（Codex 0.148.0） | JSON 写入 stdin | `~/.codex/hooks.json` 里 AI Resume 自己的 Stop 分组：`"AiResume.Hook.exe" codex`，信任哈希登记在 `config.toml` 的 `[hooks.state]` |
| Cline | `TaskComplete.ps1` | JSON 写入 stdin | PowerShell wrapper 把同一份 stdin 转给旧脚本和 Hook |
| Qoder | `hooks.Stop` | JSON 写入 stdin | `"AiResume.Hook.exe" qoder` |
| OpenCode | 顶层 `session.idle` 插件事件 | 插件先用上游 client 查询 session，`parentID` 非空的子 session 不通知，再用 `Bun.spawn` 把 JSON 写入 stdin | 独立 `airesume-notify.ts` 插件 |

采用依据：

- Codex 0.148.0 的 hooks 提供 `Stop` 事件：command hook 从 stdin 读取 JSON（`session_id`、`transcript_path`、`cwd`、`hook_event_name`、`turn_id`、`stop_hook_active`）；非 managed hook 必须在 `config.toml` 有 `[hooks.state."<hooks.json 全路径>:stop:<group>:<handler>"].trusted_hash`，且等于上游对规范化 handler 身份（`{"event_name":"stop","hooks":[{"async":false,"command":…,"timeout":30,"type":"command"}]}` 键排序后的紧凑 JSON）取的 SHA-256 才会执行。算法用本机 codex-chatgpt-web 写入的 `interrupt` 条目作 oracle 复算验证。旧的 `notify` 是单值共享键，2026-10 被 Codex Desktop computer-use 改写为 `[codex-computer-use.exe, turn-ended]`，AI Resume 的链整体丢失，因此所有权不再放在 `notify` 上；Hook 仍兼容旧 argv 负载（`agent-turn-complete`）。
- Claude Code 命令 Hook 从 stdin 接收 JSON，`Stop` 代表主 agent 停止；`stop_hook_active=true` 用于防递归：[Claude Code hooks](https://code.claude.com/docs/en/hooks)。
- Claude Code 现行 Hook 契约同时适用于 terminal、IDE extensions、Desktop 和 Web。本机 `claude-vscode` 会话已在 2026-08-19 实证触发全局 `Stop` Hook；这不是 VS Code 扩展直接发飞书，而是扩展执行 AI Resume 写入 `~/.claude/settings.json` 的 command Hook。
- Qoder 的命令 Hook 同样从 stdin 接收 JSON，`Stop` 是 agent 完成响应的边界：[Qoder hooks](https://docs.qoder.com/extensions/hooks)、[Qoder CLI hooks](https://docs.qoder.com/cli/hooks)。
- OpenCode 插件可以订阅事件；上游 schema 的 `session.idle` 携带 `sessionID`，插件输入提供 client，Task 工具创建的子 session 带 `parentID`。因此插件必须查询 session 并只承认顶层 idle；运行时由 Bun 提供：[OpenCode plugins](https://opencode.ai/docs/plugins/)、[Bun spawn](https://bun.sh/docs/api/spawn)。
- Cline 使用本机已安装扩展的 `TaskComplete.ps1` 契约；wrapper 必须保留用户原脚本的输入、输出和取消语义。

这里不引入动画库、Hook 框架或新的飞书 SDK。各客户端已有完成边界，AI Resume 只做薄适配；飞书投递复用项目既有的官方 `lark-cli` 封装。

## 3. Hook 规范化与准入

统一事件至少包含：

- `source`：`claudecode` / `codex` / `cline` / `qoder` / `opencode`
- `cwd`：绝对工作目录，用于动态识别项目
- `sessionId`、`turnId`、`taskId`：上游提供时保留
- `eventId`：由来源、会话、turn、工作目录、显式事件 ID 等稳定生成
- `atUtc`：入队时间

以下情况不入队：

- `AI_RESUME_INTERNAL_RUN=1`，避免探测、飞书任务和后台续跑通知自己。
- `stop_hook_active=true`，避免 Stop Hook 递归。
- 空/坏 JSON、不支持的来源、事件类型不匹配、工作目录缺失或不是绝对路径。
- Claude Code / Qoder 缺少明确的 `Stop` 事件名；Qoder 专属环境变量只允许补 Qoder 负载。
- Codex 缺少 session/turn，rollout 未持久化，或 rollout 明确属于 subagent、internal、`memory_consolidation`。hooks `Stop` 给出的 `transcript_path` 只有在以 `-<session_id>.jsonl` 结尾、是绝对路径且文件存在时才直接使用，否则回到按 session 扫描 rollout；`stop_hook_active=true` 同样抑制。
- Codex 的 `Documents\Codex\<日期>\<slug>` 临时 projectless 目录；目录内确有 Git 根时例外。
- OpenCode 在 `session.idle` 后必须查询 session；`parentID` 非空的 Task/subagent 会话拒绝通知，查询失败也 fail-closed。

Hook 的退出码始终为 0，stdout 保持为空；通知故障不能阻断 agent 自己的收尾流程。stdin 型来源必须从标准输入流显式按严格 UTF-8 解码，不能继承 Windows 控制台 CP936；否则 `公司内网SOP项目` 会在入队前稳定损坏成 `鍏徃鍐呯綉SOP椤圭洰`。旧版 `notify` 链的 `--previous-notify` 转发仍被 Hook 兼容，但 v2.1 起不再写入 `notify`。历史 `%LOCALAPPDATA%\AI` 截断命令没有不可伪造的所有权证据，可能也是用户命令，因此保守保留，不自动删除。

cc-connect 使用两层抑制，缺一层都不能把激活报告为完整成功：

- 配置生成器在每个项目的 `projects.agent.options.env` 中确定性写入 `AI_RESUME_INTERNAL_RUN = "1"`，并保留其它用户环境变量。上游 cc-connect 会把这组环境变量合并进 Claude Code / Codex 子进程，这是每个 agent 任务的直接证据链。
- 计划任务入口 `cc-connect-daemon.ps1` 在启动 daemon 前设置同一变量，作为整个进程树的兜底。控制器把它作为守护脚本所有权与安全校验的一部分。

只改脚本或配置而不重启既有 daemon 不会改变它已经继承的环境；部署时必须经管理 API 自重启，并复核锁 PID、监听 PID和启动代次已经变化。

## 4. 配置所有权

- Claude Code / Qoder：只有“首个可执行文件名精确为 `AiResume.Hook.exe`，且后面无参数或只有对应来源参数”才算我方 command；参数文本里偶然出现文件名不算所有权。
- Codex：`hooks.json` 只增删「handler command 的可执行文件精确为 `AiResume.Hook.exe` 且参数为 `codex`」的我方分组，永远追加在 `hooks.Stop` 末尾（信任键按序号定位，插到前面会让用户自己条目的信任记录错位）；根节点不是对象时拒绝修改。`config.toml` 只增删 `trusted_hash` 等于我方命令哈希、且表内除 `trusted_hash` / `enabled` 外没有其它键的 `[hooks.state."…"]` 表；写入按文本追加在文件末尾、原子替换并保留 `.bak`，再用 Tomlyn 读回核验。跨进程用命名互斥体串行。旧版 `notify` 残留只在 `notify list` 提示，不自动改写。已打开的 Codex 会话不会重载 `hooks.json`，新会话才生效；Codex 里 `/hooks` 显示未信任时在那里信任一次即可。
- Cline：用户原 `TaskComplete.ps1` 备份为 `TaskComplete.ai-resume-previous.ps1`，wrapper 先执行原脚本；wrapper 的读取、原脚本 stdin 和 AI Resume Hook stdin 都固定为 UTF-8，stdout 与 stderr 分开保留，只用 stdout 解析 `cancel`，原脚本要求取消时不再发我方通知。
- OpenCode：文件名和文件内稳定 marker 必须同时匹配才算我方插件；同名用户插件拒绝覆盖，停用时也不删除。
- `ProductConfig.NotifySources` 保存用户意图；重装按意图对账，卸载只移除 AI Resume 自己的条目。

路径含空格必须作为一个可执行文件参数处理。Claude/Qoder/Codex 写入带引号的命令字符串（Codex 的 hooks handler `command` 由 Codex 自己按 shell 规则拆分）；Cline 使用 PowerShell 单引号；OpenCode 使用 `Bun.spawn([cmd, "opencode"], ...)`，不走 shell 字符串拆分。

## 5. 队列、投递与进程生命周期

- 队列目录：`%LOCALAPPDATA%\AI Resume\state\completion-events\`
- 去重状态：`%LOCALAPPDATA%\AI Resume\state\completion-notify-seen.json`
- 日志：`%LOCALAPPDATA%\AI Resume\state\logs\`
- 扫描：Worker 启动后立即扫一轮，之后通常每 30 秒扫描；连续发送失败按 30 秒、60 秒、120 秒指数退避，上限 15 分钟，成功后恢复 30 秒。
- 收件人：`config.json` 的 `notifyRecipientOpenId`——GUI 在读取/保存飞书凭据时从 `allow_from` 第一个 `ou_...` 同步写入，清除凭据时清空；Worker 只对 v2.1 之前没有该字段的旧配置回退读取 DPAPI。S4U session 0 下 `CryptUnprotectData` 会间歇失败数小时（2026-10 实证），投递路径因此不再依赖 DPAPI。
- 投递：`lark-cli im +messages-send --as bot --user-id ... --idempotency-key <eventId>`。

`install` 先按规范化目标目录获取跨进程命名互斥体，再把三个项目的完整产物复制到同级 staging 目录并校验 GUI/Worker/Hook，写入 payload 清单，再备份本次会覆盖或由新清单淘汰的旧运行文件；只有 staging 与备份都成功后才停止旧 GUI/Worker。新 Worker 必须同时满足“进程未退出”和“Named Pipe `ping` 返回当前协议版本，且 pong PID 等于本次启动 PID”才算就绪；在此之前不修改快捷方式或用户级 Hook。复制、入口创建或就绪核验失败会回滚已替换和已淘汰文件，并在原来有 Worker 时重新启动旧 Worker。回滚不完整时返回独立错误码并保留 staging/backup 的绝对路径。安装目录内的 Worker 不能在 Windows 上删除自己，因此卸载会复制清单拥有的临时运行时：helper 先处理快捷方式和通知源，再把全部 payload、marker 与 manifest 事务性移动到私有退役区，完成后才向父进程返回成功。移动失败原路恢复，恢复不完整则保留 helper 目录；成功后的清理只碰 temp 退役区，不会与立即重装竞争。未知文件存在时写 preserved-root marker，使后续重装可继续，但该 marker 不能授权卸载。

投递成功后删除事件并写入七天去重表；发送失败保留事件供下轮重试；坏 JSON 移入 `completion-events\malformed\`。日志为每个事件记录脱敏后的 `eventId/source/outcome/reason`，去重表损坏、拒绝访问或写入失败也记录稳定诊断码；不会写入消息正文、路径或凭据。通知异常不得拖垮负责额度续跑的 Worker。

当前收件人语义是 `allow_from` 中第一个 `ou_...`。多 owner 的广播、显式通知收件人和排序稳定性属于产品选择，尚未扩展；改变前必须先定义去重与权限语义。Codex hooks 适配器按 Codex home 跨进程串行（命名互斥体），写 `config.toml` 前比较预期原文，不一致则拒绝提交并保留现场；外部编辑器不共享 AI Resume 的锁，运维上仍应避免同时启停通知源和保存 `config.toml`。

## 6. 验证层级

### 离线回归

```powershell
dotnet test csharp\AiResume.sln
```

关键覆盖：

- 五类来源的事件匹配、内部运行/递归 Stop 抑制、Codex rollout 准入。
- 真正启动 `AiResume.Hook.exe`，覆盖 Codex argv 与其余来源的 UTF-8 stdin，并用中文 cwd 断言字节边界。
- 真正执行 Cline PowerShell wrapper，验证旧脚本和 AI Resume 同时收到未经损坏的中文事件。
- OpenCode 真实执行生成插件，确认顶层 session 通知一次、Task/subagent 与 session 查询失败均不通知。
- 配置合并、幂等启停、含空格路径、严格所有权和同名用户文件保护。
- Worker 启动即扫描、发送/失败/重复/坏事件状态机、失败退避，以及安装后 Named Pipe 就绪核验。

所有自动测试只使用系统临时目录和合成 rollout，不启动真实 AI 任务，不读写生产项目或生产会话。

### 安装后诊断

```powershell
& "$env:LOCALAPPDATA\AI Resume\AiResume.Worker.exe" notify list
& "$env:LOCALAPPDATA\AI Resume\AiResume.Worker.exe" feishu-check
```

`notify list` 中已启用来源必须显示 `可送达=True` 且退出码为 0，Codex 行还必须显示信任哈希已登记（否则为 hookBroken 断链）；`feishu-check` 必须确认凭据可解密、配置可用和 bot token 可获取。随后按五种真实协议各注入一条带 `smoke=true` 的事件，等待 Worker 日志出现 `worker.notify.sweep ... sent=5`，并在飞书收到五条“通知冒烟通过”。这验证的是完整 Hook → 队列 → Worker → lark-cli → 飞书链路，不会启动或修改任何真实 AI 会话。

2026-08-09 最终安装版实证：仓库全量回归为 `972/972`。安装目录 Worker/Hook 与本次构建哈希一致；`notify list` 五源均为 `可送达=True`；`feishu-check` 返回 `code=0`；协议冒烟分别记录 `claudecode/cline/qoder/opencode` 的 `total=4 sent=4 failed=0` 与 Codex 的 `total=1 sent=1 failed=0`，五个来源均出现 `outcome=Sent`，队列归零。Codex 单独一轮是为了使用与真实客户端一致的现代 argv 传递，避免 Windows PowerShell 5.1 改写 JSON 参数。这里验证的是五种真实 Hook 协议到飞书的完整链路，不代表为了测试而让五个客户端各执行了一次真实 AI 任务。

2026-08-13 当前安装版复核：Release 构建 `0 warning / 0 error`，全量回归 `1089/1089`、0 skipped；在 stdout/stderr 被父进程捕获的环境中执行 `install` 约 3.7 秒正常返回，安装清单 108 项与 Release 合并产物逐文件 SHA-256 全部一致，新 Worker 只有一个稳定 PID。`notify list` 五源仍全部 `可送达=True`，`feishu-check` 为 `code=0`。按现役 `config.toml` 的完整 `codex-computer-use.exe → --previous-notify → AiResume.Hook.exe` argv 链注入合成 `smoke=true` 事件，Hook 入队、Worker `outcome=Sent`、队列归零；未启动真实 agent，也未调用模型推理端点。

## 7. 故障定位

| 现象 | 首查 | 判据 |
|---|---|---|
| 开关打开但从未入队 | 客户端配置和 Hook 命令 | 来源参数正确，路径有引号/argv 保护，Hook 文件存在 |
| Claude VS Code 曾经不通知 | VS Code Claude 日志、版本、全局 `settings.json` | 2.1.232 曾实测缺失；后续版本已实证执行 `Stop`，不要沿用旧版本结论 |
| 只有中文项目名乱码 | Hook stdin 与 Cline wrapper | stdin 必须按 UTF-8 解码；不要在 Worker/飞书端猜测反转乱码 |
| Codex 独有不通知 | `~/.codex/hooks.json`、`config.toml` 的 `[hooks.state]`、会话启动时间 | Stop 分组存在且 `trusted_hash` 等于当前哈希（`notify list` 不报断链；否则在 codex `/hooks` 信任）；会话早于 hooks.json 写入时新开会话；`transcript_path` 指向顶层 persisted rollout |
| 通知间歇几小时发不出，日志 `worker.notify.sweep_failed` | `config.json` 的 `notifyRecipientOpenId` | v2.1 起收件人明文；字段为空说明旧配置未同步，打开控制面让它读取一次飞书凭据状态即可 |
| Cline 独有不通知 | `TaskComplete.ps1` | `$stdin` 同时管道给旧脚本和 `AiResume.Hook.exe cline` |
| OpenCode 独有不通知 | `airesume-notify.ts` | 监听 `session.idle`，查询 session 后拒绝 `parentID` 子会话，使用 `Bun.spawn` + stdin，而不是 shell 模板字符串 |
| 队列有文件但飞书没有 | Worker、凭据、lark-cli、日志 | Worker 进程存在；`feishu-check` 成功；日志无持续 `failed/skipped` |
| 只有安装后突然失效 | 安装后的 Worker | 新 Worker 已立即启动，不是只留下登录自启快捷方式 |
| 重复消息 | eventId/seen/idempotency | 同一事件七天内本地去重，飞书请求携带相同 idempotency key |
