<!-- project-tour · generated 2026-07-14 01:27 · git 42edf56 -->
# Claude Resume — AI 导览(AI_GUIDE.md)

> 一句话:一个 Windows 后台工具,勾选若干 Claude Code 项目并「布防」后,在你撞上 **5 小时用量上限** 时静候,一旦额度重置就无人值守地在每个项目里跑 `claude --continue` 把活接着干完;外加一个**可选的飞书机器人**,把限流/续跑通知推给你,并支持从飞书**双向**对项目发指令(闲聊 / 只读查询 / 修改)。
> 本文件供 AI **只读问答**优先加载:80% 的常见技术问题看这里就能答;深挖时见文末「文档索引」。

## 1. 定位
- **用途**:解决「Claude Code 5h 限流打断多个项目」的痛点。勾项目 → 按**布防(Arm)** → 关窗;计划任务后台每 2 分钟一次**实时探测**账号是否可用,重置那一刻依次续跑所选项目。**无估算**——重置时间/额度百分比全部从 Claude 服务器的 `rate_limit_event` **实时读取**。
- **使用者 / 场景**:个人在 Windows 上跑 Claude Code、经常被 5h 限流打断的人;可选把飞书机器人**开给同事**只读查询代码(同事默认只读、不能改)。
- **技术栈**:
  - GUI + 引擎:**Windows PowerShell 5.1**(注意不是 pwsh 7)。GUI 用 WPF/XAML。
  - 飞书 agent:**Node.js**,`@larksuiteoapi/node-sdk` 的 `WSClient`(长连接 WebSocket,**无需公网 IP**)。
  - 隐藏启动:`.vbs`(经 `wscript`)+ **计划任务** `ClaudeResumeChecker`(每 2 分钟)。
  - 外部依赖:**Claude Code CLI**(`claude`,无头 `-p --output-format stream-json --verbose`)。
  - 无编译、无数据库;所有状态是 `%LOCALAPPDATA%\ClaudeResume` 下的 JSON + 日志文件。

## 2. 架构与数据流
两个相对独立的部分:**续跑器**(GUI + 引擎,PowerShell)与**飞书 agent**(Node),两者只通过 `config.json` 交换状态。**为何拆 GUI / 引擎**:续跑可能跑几分钟到几小时,若放在 WPF UI 线程里窗口会冻住、Stop/Disarm 恰好失灵;所以 GUI 只**配置 + 监控**,真正的等待+续跑在计划任务拥有的独立进程里,能扛住关窗/注销/重启。

```
桌面快捷方式「Claude续跑」
      │ launcher.vbs(wscript 隐藏)
      ▼
 picker.ps1 (WPF GUI)──写──▶ ┌─────────────────────────┐ ◀──读/写── checker.ps1 (引擎/无状态状态机)
  勾选项目/布防/解除          │ config.json  (GUI 写)    │            ▲ 计划任务 ClaudeResumeChecker 每 2 分钟
  额度 chip(按需探测)◀──读── │ state.json   (checker 写)│            │ (checker-launch.vbs)
  日志区 ◀────读──────────── │ logs\run-*.log           │            │ 用
                             └──────────┬──────────────┘   ┌─────────┴─────────┐
                                        │                  │ lib.ps1 (共享函数) │
   探测/续跑 = 子进程 cmd.exe /c claude.cmd -p …            └─────────┬─────────┘
                                        │  claude CLI ──stream-json──▶ Claude 服务器
                                        │                              (rate_limit_event: resetsAt/utilization)
                                        ▼ 续跑:每项目 git 脏检查 → claude --continue -p "continue"

──────────────  飞书(可选,独立 Node 进程)  ──────────────
 开机 Startup → feishu-launch.vbs(守护,node 挂了 ~8s 重启)
      ▼
 feishu-agent.js ◀── WSClient 长连接 ──▶ 飞书开放平台
  · 收:im.message.receive_v1 / card.action.trigger / application.bot.menu_v6
  · 会话状态机(idle/chat/project · 只读查询/修改) 存 feishu-sessions.json
  · 发指令 → runClaude(spawn claude,prompt 走 stdin) → 回复
  · 通知:lib.ps1 的 Send-FeishuNotify 经 config.feishuChatId 由同一个 bot 推送
```

## 3. 模块职责(路径 → 职责 → 关键函数/入口)
| 路径 | 一句话职责 | 关键函数 / 入口 |
|---|---|---|
| `src/lib.ps1` | 引擎共享库(被 dot-source):项目发现、探测、续跑、git 守护、日志、飞书通知、缓存清理。 | `Test-ClaudeReady`(探测+读 resetsAt/utilization)、`Invoke-ClaudeResume`、`Get-ClaudeProjects`、`Protect-GitRepo`、`Stop-ProcessTree`、`Save-RealResetFromProbe`、`Send-FeishuNotify`、`Get/Set-CcuConfig`、`Get/Set-CcuState`、`Clear-OldCaches`、`Format-Countdown` |
| `src/checker.ps1` | **无状态状态机**,计划任务每 ~2 分钟跑一次;判定探测节流→探测→限流/恢复→FIRE 续跑;`-DryRun` 预演。 | 顶层线性脚本(无函数):节流(`sawLimited`→4min,否则 `probeIntervalMinutes`)、fail-closed、一次性(成功后 `enabled=false`) |
| `src/picker.ps1` | WPF/XAML GUI:发现/勾选项目、布防/解除/预演、额度 chip(开窗+点击时**按需探测**,后台 runspace 不冻 UI)、日志区、模型 chip、忘记闲聊/清空查询/授权用户窗口。单实例(Mutex)。`-RenderTo <png>` 无头截图。 | `Get-CurLogFile`(永远读最新 `run-*.log`)、`Read-LogTail`、`Set-LogColored`、`Show-AuthWindow` |
| `src/feishu-agent.js` | 飞书双向 agent(Node 长连接):消息/按钮 → 会话状态机(闲聊/只读查询/修改)+ 三级权限 → 回复 + 携带 checker 的通知。有离线测试模式 `FEISHU_TEST`。 | `onMessage`/`onCardAction`/`onBotMenu`、`runClaude`(prompt 走 **stdin**)、`runProjectQuery`、`buildMenuCard`/`buildProjectCard`、`showCard`/`refreshCard`/`currentCard`、`authLevel`、`querySession`、`clearQuerySession`、`apiRetry`、`startHeartbeat` |
| `src/install.ps1` | 部署:src→`%LOCALAPPDATA%\ClaudeResume`、生成珊瑚色图标、桌面快捷方式、注册计划任务、飞书 Startup 项 + `npm install`。**可反复跑**(=重新部署)。 | 顶层脚本 |
| `src/*.vbs` | 隐藏启动器:`launcher.vbs`(GUI)、`checker-launch.vbs`(计划任务)、`feishu-launch.vbs`(守护 node,自动重启+重定向 stdout)。 | wscript 隐藏窗口 |
| `test/*.js` | 离线/端到端自测(见 §4)。 | `card-flow` / `query-e2e` / `chat-security` / `guide-e2e` |
| `docs/` | `ARCHITECTURE.md`(内部原理)、`LESSONS.md`(踩坑+开发史)。 | — |

## 4. 测试 / 运行流程
- **安装/部署入口**:`powershell -ExecutionPolicy Bypass -File src\install.ps1`。复制到 `%LOCALAPPDATA%\ClaudeResume`、建桌面快捷方式「Claude续跑」、注册计划任务 `ClaudeResumeChecker`(每 2 分钟),初始**未布防**。
- **改代码后重新生效(关键,容易忘)**:线上从 `%LOCALAPPDATA%\ClaudeResume` 跑,**不会**自动同步 `src/`。
  - `.ps1`(GUI/引擎):复制到 AppDir(或重跑 `install.ps1`);GUI 改动**下次开窗**生效。
  - `feishu-agent.js`:复制到 AppDir → **杀 node**(`Get-Process node | Stop-Process -Force`)→ VBS 守护 ~8s 自动重启 → 验证 `logs\feishu-stdout.log` 出现 `ws client ready`、node 进程为 1 个。
- **飞书卡片/状态机自测(改完先跑,别每次去飞书点)**:
  - `node test/card-flow.js` — 纯逻辑离线(`FEISHU_TEST=1` 用记录型 mock client,不联网/不长连接/不占锁),断言「进项目→积压菜单→回主菜单」不堆卡、不跳回。
  - `node test/query-e2e.js` — mock 飞书 API 但**真跑 claude**(haiku,~$0.1),发多行查询断言换行完整送达(stdin)。
  - `node test/chat-security.js` — 非 owner 闲聊尝试读 `config.json` 里的 `feishuAppSecret`,断言回复**不含**该密钥。
  - `node test/guide-e2e.js` — 有 AI_GUIDE.md 的项目,查询文件名解码题,断言答案用了导览里的解码(而非天真读法)。
- **续跑(fire)路径**:依赖真实额度,无法离线完全验证。先按 **预演(Preview / `checker -DryRun`)** 看计划(哪些项目、探测节奏),真实重置自行验证。
- **依赖 / 环境**:Windows 10/11;PowerShell 5.1(系统自带);Node.js LTS(仅飞书需要);`claude` CLI(`npm i -g @anthropic-ai/claude-code`,与 VS Code 扩展共享登录/会话)。

## 5. 数据格式与命名约定
本项目**没有科学数据文件**,「数据」= AppDir 下的 JSON 状态 + 日志 + Claude 会话文件。目录布局与命名逐字段解码如下。

- **目录布局**:源码/文档在仓库(`C:\Users\23230\Desktop\claude-resume`);**运行态**全在 `%LOCALAPPDATA%\ClaudeResume\`:`config.json`、`state.json`、`logs\`、`node_modules\`、`feishu-agent.pid`、`feishu-sessions.json`、`feishu-query\`(每项目 `.started` 标记)、`feishu-query-cwd\<sha1>\`(只读查询的隔离 cwd)、`feishu-chat\`(闲聊 scratch 会话)。**密钥只在 `config.json`,gitignore,绝不进仓库。**

- **日志文件名解码**(都在 `logs\`,**一律本地时间**,绝不用 UTC):
  - `run-<yyyyMMdd>.log` — 续跑引擎日志(`Write-CcuLog`),按**本地日期**每天一个。**GUI 主窗口/大窗显示的就是它**;GUI 永远读**最新的** `run-*.log`(`Get-CurLogFile` 按 `LastWriteTime`),绝不固定开窗那天的名字(跨午夜会空白)。
  - `feishu-<yyyy-MM-dd>.log` — 飞书 agent 日志(`logLine`),本地日期每天一个。
  - `feishu-stdout.log` — node 进程 stdout/stderr(SDK 连接日志);>1MB 重启前删。
  - `gui-error.log` — GUI 自身异常。
  - 导出日志 = 合并所有 `run-*.log` + `gui-error.log` 成一个 **UTF-8 带 BOM** 文件。

- **只读查询会话 id / cwd 解码**(每项目一个共享只读 Q&A 会话):
  - `querySession(projectPath)`:`h = sha1(projectPath.toLowerCase())` 的 hex;会话 id 拼成一个**固定 UUID**:`h[0:8]-h[8:12]-4h[13:16]-8h[17:20]-h[20:32]`(第 3 段固定 `4`、第 4 段固定 `8`,凑成合法 v4 UUID)。
  - 标记文件 `feishu-query\<h>.started`(内容 `{id,path,name}`,供 GUI「清空查询记忆」重建 id 定位)。
  - 隔离 cwd `feishu-query-cwd\<h>\`——查询**在这里**跑 + `--add-dir <项目路径>`,**绝不**在 `project.path` 里跑(否则查询会话会污染 `--continue` 池,修改项目时会误续到查询会话)。
  - **清空** = 删 `.started` **且**删 `~/.claude/projects/*/​<id>.jsonl`(光删标记不够——`--session-id` 撞已存在 id 会报 `already in use`)。

- **项目发现的编码**:`~/.claude/projects/<encoded>/​*.jsonl`,文件夹名对路径有损/歧义,所以读每个 session `*.jsonl` 里真实的 `cwd`(`-Encoding UTF8`,否则中文路径丢)+ 最后使用时间;续跑用 `claude --continue`(续该 cwd 最近的对话)。

- **JSON 内部结构**(完整字段见 [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md)):
  - `config.json`(GUI 写):`enabled`(是否布防=全局总开关)、`selected`/`customProjects`(`{name,path}`)、`resumePrompt`、`skipPermissions`(全自动)、`dirtyGuard`(`stash`|`branch`)、`probeIntervalMinutes`(探测节奏 5/15/30,限流时收紧到 4)、`probeModel`、`feishuAppId/AppSecret/ChatId/Webhook/Secret`、`feishuChatModel`(闲聊+项目+查询的模型)、`feishuAuthOpenIds`(**FULL 用户**名单)。
  - `state.json`(checker 写):`sawLimited`、`lastProbeUtc`、`projectStatus`(每项目 `success/error/timeout/limited/stopped`)、`phase`、`realFiveHourResetUtc`/`realSevenDayResetUtc`(**Unix 整数**,规避 ISO 时区坑)、`realFiveHourUtil`。
  - `feishu-sessions.json`:按 chatId → `{mode, project, sub}` 的会话状态。

## 6. FAQ(同事高频问题,直接给答案)
- **Q:这工具到底做什么?** — A:见 §1。撞 5h 上限时后台等重置,重置就在所选项目里 `claude --continue` 续跑;可选飞书机器人做通知+双向指令。
- **Q:它怎么知道额度什么时候恢复?靠估算吗?** — A:**不估算**。`checker` 固定间隔跑一个廉价探测 `claude -p "ready"`(`Test-ClaudeReady`),Claude 回的 `rate_limit_event` 带服务器权威的 `resetsAt`/`utilization`;探测显示可用**且之前观察到过限流**才 FIRE。任何模糊读一律 fail-closed(当作仍限流)。见 [ARCHITECTURE.md](docs/ARCHITECTURE.md#the-checker-state-machine)。
- **Q:改了 `src/feishu-agent.js` 为什么没反应?** — A:线上从 `%LOCALAPPDATA%\ClaudeResume` 跑。必须复制到 AppDir → 杀 node → 等 ~8s 守护重启 → 看 `feishu-stdout.log` 的 `ws client ready`。见 §4 / `CLAUDE.md`。
- **Q:飞书里查询「总是失败/说没看到问题」怎么回事?** — A:历史元凶是 Windows `cmd` 把 `-p "多行文本"` 在第一个换行处截断。现已把 prompt 改走 **stdin**(`runClaude` 写 `child.stdin` 再 `end()`);成功判定只信 stream-json 的 `result` 行,不信 exit code。见 [LESSONS.md 二](docs/LESSONS.md)。
- **Q:谁能改我的项目?怎么控权限?** — A:只有 `config.json` 的 `feishuAuthOpenIds`(**full/owner**)能改项目/改配置/授权;**其他所有人自动只读**(browse/query)。`feishuAuthOpenIds` **空 = 未锁定 = 人人可改**,移除最后一个 owner 会解锁,GUI 会警告。闲聊对所有人开放。`authLevel(openId)` 判定。
- **Q:「只读查询」会不会改到代码 / 污染我 VS Code 的会话?** — A:不会。只读查询跑 `--permission-mode plan`,在**隔离 cwd**(`feishu-query-cwd\<sha1>`)+ `--add-dir` 里,固定 session-id,`--disallowedTools Task`。它和「修改项目」(`claude --continue` 续 VS Code 那条工作会话)完全隔离。
- **Q:AI_GUIDE.md(本文件)是干嘛的?** — A:给飞书**只读查询**用。`runProjectQuery` 在查询会话首次创建时把它**注入一次**,之后 `--resume` 走 prompt cache;它记录了生成时的 git hash,项目提交往前走了会提示导览可能过时。所以代码/数据格式大改后应重跑 `project-tour` skill 刷新,并在飞书点「🧹 清空查询记忆」让新导览生效。
- **Q:为什么全用 PowerShell 5.1 而不是 pwsh 7?有坑吗?** — A:兼容系统自带。坑很多且已处理:`Start-Process` 的 `.ExitCode` 退出后变 `$null`(改看 stream-json result)、`Set-Content -Encoding UTF8` 写 BOM 崩 Node(改 `WriteAllText` 无 BOM)、`ConvertFrom-Json` 把 ISO 时间偷偷转本地时区(改存 Unix 整数)、`.ps1` 必须存 UTF-8 **带 BOM**。见 [LESSONS.md 一](docs/LESSONS.md)。
- **Q:日志在哪、怎么看?** — A:`%LOCALAPPDATA%\ClaudeResume\logs\`,见 §5。GUI 日志区显示最新 `run-*.log`;飞书问题看 `feishu-<date>.log` 和 `feishu-stdout.log`;点「导出日志」合并成一份可分享文件。
- **Q:为什么不用 `.lnk → powershell -WindowStyle Hidden`?** — A:火绒(Huorong)会删这种组合。所有隐藏启动走 `wscript` 隐藏的 `.vbs` + 计划任务。

## 7. 术语表(中英 / 缩写对照)
| 术语 | 含义 |
|---|---|
| 布防 / 解除 (Arm / Disarm) | 布防=开启自动续跑监视(`config.enabled=true`);解除=全局总开关关闭,立即停止一切自动续跑 |
| 预演 (Preview / DryRun) | `checker.ps1 -DryRun`,只报计划不探测不续跑 |
| 探测 (Probe) | `Test-ClaudeReady`:一次廉价 `claude -p "ready"`,读服务器 `rate_limit_event` 判断额度并取 resetsAt/utilization |
| fail-closed | 任何模糊/失败读一律当作「仍限流」,绝不误 fire |
| FIRE / 续跑 (Resume) | 依次在所选项目跑 `claude --continue -p "continue"` |
| 一次性 (one-shot) | 成功续跑后自动 `enabled=false`,不会每 5h 循环 |
| git 脏检查 (dirty-guard) | 续跑前若 repo 有未提交改动,自动 `git stash`(或建分支)保底可恢复 |
| 只读查询 (read-only query) | `--permission-mode plan` 的每项目共享 Q&A 会话,只读不改,隔离 cwd + `--add-dir` |
| 修改项目 (modify) | `claude --continue`,续 VS Code 扩展显示的同一条工作会话(重开才可见外部追加) |
| 闲聊 (chat) | 在 scratch 会话里和 Claude 聊,不碰任何项目 |
| owner / full / viewer | full=owner(在 `feishuAuthOpenIds`,能改);viewer=其他所有人(自动只读) |
| 逃生舱 (escape hatch) | 底部菜单:任何状态点它都回 idle 并补发一张主菜单卡 |
| 心跳 (heartbeat) | 长任务每 15s 回「🤔 思考中…已 Ns」防用户以为卡死 |
| WSClient / 长连接 | 飞书 SDK 的持久 WebSocket,收事件无需公网 IP |
| rate_limit_event | Claude stream-json 里带 `resetsAt`/`utilization`/`rateLimitType`(five_hour/seven_day)的行 |
| SMU/激光器等 | 本项目**无**硬件/科学数据;若在别的被查询项目里见到,与本工具无关 |

## 8. 文档索引(深挖时读)
- [README.md](README.md):安装、使用、飞书配置(开发者后台 3 处配置 + 发布版本)、安全说明——面向用户的完整说明。
- [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md):内部原理——checker 状态机、探测/读重置、GUI 单实例/按需探测、飞书状态机与卡片机制、`config.json`/`state.json` **全字段**、测试状态。深挖实现先读它。
- [docs/LESSONS.md](docs/LESSONS.md):真实踩坑 + 开发史(PS 5.1 exit-code/BOM/时区、cmd 换行截断 `-p`、`--continue` 池污染、飞书卡片堆叠/抢卡、日志系统、权限模型简化、离线自测方法论)。排查「为什么这么写/为什么会这个 bug」看它。
- [CLAUDE.md](CLAUDE.md):协作约定——中文沟通、飞书 agent 重新部署步骤、离线自测命令、安全约束。
