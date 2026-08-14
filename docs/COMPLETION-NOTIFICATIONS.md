# 完成通知协议、实现与验证手册

最后验证：2026-08-13（Windows 11，AI Resume v2）

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
| Codex | `notify` 的 `agent-turn-complete` | JSON 作为命令最后一个 argv | `AiResume.Hook.exe codex ... <payload>` |
| Cline | `TaskComplete.ps1` | JSON 写入 stdin | PowerShell wrapper 把同一份 stdin 转给旧脚本和 Hook |
| Qoder | `hooks.Stop` | JSON 写入 stdin | `"AiResume.Hook.exe" qoder` |
| OpenCode | 顶层 `session.idle` 插件事件 | 插件先用上游 client 查询 session，`parentID` 非空的子 session 不通知，再用 `Bun.spawn` 把 JSON 写入 stdin | 独立 `airesume-notify.ts` 插件 |

采用依据：

- Codex 官方配置把 `notify` 定义为命令数组，目前完成事件为 `agent-turn-complete`，JSON 负载追加在命令参数末尾：[Codex configuration reference](https://developers.openai.com/codex/config-reference/)、[Advanced configuration](https://developers.openai.com/codex/config-advanced/)。
- Claude Code 命令 Hook 从 stdin 接收 JSON，`Stop` 代表主 agent 停止；`stop_hook_active=true` 用于防递归：[Claude Code hooks](https://code.claude.com/docs/en/hooks)。
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
- Codex 缺少 thread/turn，rollout 未持久化，或 rollout 明确属于 subagent、internal、`memory_consolidation`。
- Codex 的 `Documents\Codex\<日期>\<slug>` 临时 projectless 目录；目录内确有 Git 根时例外。
- OpenCode 在 `session.idle` 后必须查询 session；`parentID` 非空的 Task/subagent 会话拒绝通知，查询失败也 fail-closed。

Hook 的退出码始终为 0，stdout 保持为空；通知故障不能阻断 agent 自己的收尾流程。Codex 原有 `notify` 用 `--previous-notify` 继续转发。历史 `%LOCALAPPDATA%\AI` 截断命令没有不可伪造的所有权证据，可能也是用户命令，因此保守保留，不自动删除。

cc-connect 使用两层抑制，缺一层都不能把激活报告为完整成功：

- 配置生成器在每个项目的 `projects.agent.options.env` 中确定性写入 `AI_RESUME_INTERNAL_RUN = "1"`，并保留其它用户环境变量。上游 cc-connect 会把这组环境变量合并进 Claude Code / Codex 子进程，这是每个 agent 任务的直接证据链。
- 计划任务入口 `cc-connect-daemon.ps1` 在启动 daemon 前设置同一变量，作为整个进程树的兜底。控制器把它作为守护脚本所有权与安全校验的一部分。

只改脚本或配置而不重启既有 daemon 不会改变它已经继承的环境；部署时必须经管理 API 自重启，并复核锁 PID、监听 PID和启动代次已经变化。

## 4. 配置所有权

- Claude Code / Qoder：只有“首个可执行文件名精确为 `AiResume.Hook.exe`，且后面无参数或只有对应来源参数”才算我方 command；参数文本里偶然出现文件名不算所有权。
- Codex：只改顶层单行 `notify`，用 Tomlyn 解析外层 TOML argv 数组，支持 basic/literal string 混用和字符串内部的嵌套 JSON 方括号；数组必须全部是字符串。已有命令作为 JSON 文本串在 `--previous-notify` 后；多行、非字符串数组或无法安全解析时拒绝修改。
- Codex 配置修复只在 TOML 语句边界识别 `notify`；多行字符串、数组和内联表内容中的同形文本必须逐字保留。当前版本不能假设已经运行的 Codex app-server 会自动重载该写入，安装或切换后需重启当时已打开的 Codex Desktop。
- Cline：用户原 `TaskComplete.ps1` 备份为 `TaskComplete.airesume-previous.ps1`，wrapper 先执行原脚本；stdout 与 stderr 分开保留，只用 stdout 解析 `cancel`，原脚本要求取消时不再发我方通知。
- OpenCode：文件名和文件内稳定 marker 必须同时匹配才算我方插件；同名用户插件拒绝覆盖，停用时也不删除。
- `ProductConfig.NotifySources` 保存用户意图；重装按意图对账，卸载只移除 AI Resume 自己的条目。

路径含空格必须作为一个可执行文件参数处理。Claude/Qoder 写入带引号的命令；Codex 配置是 TOML argv 数组，AI Resume 写回使用其合法的 JSON 字符串数组子集，只有 `--previous-notify` 的值是嵌套 JSON 文本；Cline 使用 PowerShell 单引号；OpenCode 使用 `Bun.spawn([cmd, "opencode"], ...)`，不走 shell 字符串拆分。

## 5. 队列、投递与进程生命周期

- 队列目录：`%LOCALAPPDATA%\AI Resume\state\completion-events\`
- 去重状态：`%LOCALAPPDATA%\AI Resume\state\completion-notify-seen.json`
- 日志：`%LOCALAPPDATA%\AI Resume\state\logs\`
- 扫描：Worker 启动后立即扫一轮，之后通常每 30 秒扫描；连续发送失败按 30 秒、60 秒、120 秒指数退避，上限 15 分钟，成功后恢复 30 秒。
- 收件人：DPAPI 飞书凭据中 `allow_from` 的第一个 `ou_...`。
- 投递：`lark-cli im +messages-send --as bot --user-id ... --idempotency-key <eventId>`。

`install` 先按规范化目标目录获取跨进程命名互斥体，再把三个项目的完整产物复制到同级 staging 目录并校验 GUI/Worker/Hook，写入 payload 清单，再备份本次会覆盖或由新清单淘汰的旧运行文件；只有 staging 与备份都成功后才停止旧 GUI/Worker。新 Worker 必须同时满足“进程未退出”和“Named Pipe `ping` 返回当前协议版本，且 pong PID 等于本次启动 PID”才算就绪；在此之前不修改快捷方式或用户级 Hook。复制、入口创建或就绪核验失败会回滚已替换和已淘汰文件，并在原来有 Worker 时重新启动旧 Worker。回滚不完整时返回独立错误码并保留 staging/backup 的绝对路径。安装目录内的 Worker 不能在 Windows 上删除自己，因此卸载会复制清单拥有的临时运行时：helper 先处理快捷方式和通知源，再把全部 payload、marker 与 manifest 事务性移动到私有退役区，完成后才向父进程返回成功。移动失败原路恢复，恢复不完整则保留 helper 目录；成功后的清理只碰 temp 退役区，不会与立即重装竞争。未知文件存在时写 preserved-root marker，使后续重装可继续，但该 marker 不能授权卸载。

投递成功后删除事件并写入七天去重表；发送失败保留事件供下轮重试；坏 JSON 移入 `completion-events\malformed\`。日志为每个事件记录脱敏后的 `eventId/source/outcome/reason`，去重表损坏、拒绝访问或写入失败也记录稳定诊断码；不会写入消息正文、路径或凭据。通知异常不得拖垮负责额度续跑的 Worker。

当前收件人语义是 `allow_from` 中第一个 `ou_...`。多 owner 的广播、显式通知收件人和排序稳定性属于产品选择，尚未扩展；改变前必须先定义去重与权限语义。Codex 配置适配器按配置路径跨进程串行，提交前比较原文并用 `File.Replace` 的备份核对“实际被替换版本”；若外部写入恰好落在最终比较与原子替换之间，适配器恢复该外部版本、拒绝报告成功，并保留冲突文件。外部编辑器不共享 AI Resume 的锁，运维上仍应避免同时启停通知源和保存 `config.toml`。

## 6. 验证层级

### 离线回归

```powershell
dotnet test csharp\AiResume.sln
```

关键覆盖：

- 五类来源的事件匹配、内部运行/递归 Stop 抑制、Codex rollout 准入。
- 真正启动 `AiResume.Hook.exe`，覆盖 Codex argv 与其余来源 stdin。
- 真正执行 Cline PowerShell wrapper，验证旧脚本和 AI Resume 同时收到事件。
- OpenCode 真实执行生成插件，确认顶层 session 通知一次、Task/subagent 与 session 查询失败均不通知。
- 配置合并、幂等启停、含空格路径、严格所有权和同名用户文件保护。
- Worker 启动即扫描、发送/失败/重复/坏事件状态机、失败退避，以及安装后 Named Pipe 就绪核验。

所有自动测试只使用系统临时目录和合成 rollout，不启动真实 AI 任务，不读写生产项目或生产会话。

### 安装后诊断

```powershell
& "$env:LOCALAPPDATA\AI Resume\AiResume.Worker.exe" notify list
& "$env:LOCALAPPDATA\AI Resume\AiResume.Worker.exe" feishu-check
```

`notify list` 中已启用来源必须显示 `可送达=True` 且退出码为 0；`feishu-check` 必须确认凭据可解密、配置可用和 bot token 可获取。随后按五种真实协议各注入一条带 `smoke=true` 的事件，等待 Worker 日志出现 `worker.notify.sweep ... sent=5`，并在飞书收到五条“通知冒烟通过”。这验证的是完整 Hook → 队列 → Worker → lark-cli → 飞书链路，不会启动或修改任何真实 AI 会话。

2026-08-09 最终安装版实证：仓库全量回归为 `972/972`。安装目录 Worker/Hook 与本次构建哈希一致；`notify list` 五源均为 `可送达=True`；`feishu-check` 返回 `code=0`；协议冒烟分别记录 `claudecode/cline/qoder/opencode` 的 `total=4 sent=4 failed=0` 与 Codex 的 `total=1 sent=1 failed=0`，五个来源均出现 `outcome=Sent`，队列归零。Codex 单独一轮是为了使用与真实客户端一致的现代 argv 传递，避免 Windows PowerShell 5.1 改写 JSON 参数。这里验证的是五种真实 Hook 协议到飞书的完整链路，不代表为了测试而让五个客户端各执行了一次真实 AI 任务。

2026-08-13 当前安装版复核：Release 构建 `0 warning / 0 error`，全量回归 `1089/1089`、0 skipped；在 stdout/stderr 被父进程捕获的环境中执行 `install` 约 3.7 秒正常返回，安装清单 108 项与 Release 合并产物逐文件 SHA-256 全部一致，新 Worker 只有一个稳定 PID。`notify list` 五源仍全部 `可送达=True`，`feishu-check` 为 `code=0`。按现役 `config.toml` 的完整 `codex-computer-use.exe → --previous-notify → AiResume.Hook.exe` argv 链注入合成 `smoke=true` 事件，Hook 入队、Worker `outcome=Sent`、队列归零；未启动真实 agent，也未调用模型推理端点。

## 7. 故障定位

| 现象 | 首查 | 判据 |
|---|---|---|
| 开关打开但从未入队 | 客户端配置和 Hook 命令 | 来源参数正确，路径有引号/argv 保护，Hook 文件存在 |
| Codex 独有不通知 | `~/.codex/config.toml`、主进程启动时间与 rollout | JSON 位于最后一个 argv，顶层 persisted rollout 可找到；主进程早于配置写入时先重启 Codex Desktop |
| Cline 独有不通知 | `TaskComplete.ps1` | `$stdin` 同时管道给旧脚本和 `AiResume.Hook.exe cline` |
| OpenCode 独有不通知 | `airesume-notify.ts` | 监听 `session.idle`，查询 session 后拒绝 `parentID` 子会话，使用 `Bun.spawn` + stdin，而不是 shell 模板字符串 |
| 队列有文件但飞书没有 | Worker、凭据、lark-cli、日志 | Worker 进程存在；`feishu-check` 成功；日志无持续 `failed/skipped` |
| 只有安装后突然失效 | 安装后的 Worker | 新 Worker 已立即启动，不是只留下登录自启快捷方式 |
| 重复消息 | eventId/seen/idempotency | 同一事件七天内本地去重，飞书请求携带相同 idempotency key |
