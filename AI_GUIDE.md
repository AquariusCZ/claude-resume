<!-- project-tour · generated 2026-07-16 21:45 · git 765ce12 -->
# Claude Resume — AI 导览(AI_GUIDE.md)

> 一句话:一个 Windows 后台工具,勾选若干 Claude Code 项目并「布防」后,在你撞上 **5 小时用量上限** 时静候,一旦额度重置就无人值守地在每个项目里 `claude --continue` 把活接着干完;外加一个**可选的飞书机器人**,把限流/续跑通知(带时间戳)推给你,并支持从飞书**双向**操作项目(闲聊 / 只读查询 / 修改——修改前先**选要续的会话**)。
> 本文件供 AI **只读问答**优先加载:80% 的常见技术问题看这里就能答;深挖时见文末「文档索引」。

## 1. 定位
- **用途**:解决「Claude Code 5h 限流打断多个项目」的痛点。勾项目 → 按**布防(Arm)** → 关窗;计划任务每 ~2 分钟一跑,按固定间隔**实时探测**账号是否可用,重置那一刻依次续跑所选项目。**无估算**——重置时间/额度百分比全部从 Claude 服务器的 `rate_limit_event` **实时读取**。
- **使用者 / 场景**:个人在 Windows 上跑 Claude Code、经常被 5h 限流打断的人;可把飞书机器人**开给同事**:同事自动只读(浏览/查询),绝不可改;每人的菜单/回复走**各自的私聊**,互不干扰。
- **技术栈**:
  - GUI + 引擎:**Windows PowerShell 5.1**(注意不是 pwsh 7)。GUI 用 WPF/XAML。
  - 飞书 agent:**Node.js**,`@larksuiteoapi/node-sdk` 的 `WSClient`(长连接 WebSocket,**无需公网 IP**)。
  - 隐藏启动:`.vbs`(经 `wscript`)+ **计划任务** `ClaudeResumeChecker`(每 2 分钟)。
  - 外部依赖:**Claude Code CLI**(`claude`,无头 `-p --output-format stream-json --verbose`,prompt 走 **stdin**)。
  - 无编译、无数据库;所有状态是 `%LOCALAPPDATA%\ClaudeResume` 下的 JSON + 日志文件。

## 2. 架构与数据流
两个相对独立的部分:**续跑器**(GUI + 引擎,PowerShell)与**飞书 agent**(Node),两者只通过 `config.json` 交换状态。
**为何拆 GUI / 引擎**:续跑可能跑几分钟到几小时,若放在 WPF UI 线程里窗口会冻住、Stop/Disarm 恰好失灵;所以 GUI 只**配置 + 监控**,真正的等待+续跑在计划任务拥有的独立进程里,能扛住关窗/注销/重启。

```
桌面快捷方式「Claude续跑」
      │ launcher.vbs(wscript 隐藏)
      ▼
 picker.ps1 (WPF GUI)──写──▶ ┌─────────────────────────┐ ◀──读/写── checker.ps1 (引擎/无状态状态机)
  勾选项目/布防/解除/更新导览  │ config.json  (GUI 写)    │            ▲ 计划任务 ClaudeResumeChecker 每 2 分钟
  额度/间隔/模型 chip ◀──读── │ state.json   (checker 写)│            │ (checker-launch.vbs,checker.lock 防重入)
  日志区(彩色)◀────读────── │ logs\run-*.log           │            │ 用
                             └──────────┬──────────────┘   ┌─────────┴─────────┐
                                        │                  │ lib.ps1 (共享函数) │
   探测/续跑 = 子进程 cmd.exe /c claude.cmd …               └─────────┬─────────┘
                                        │  claude CLI ──stream-json──▶ Claude 服务器
                                        │                              (rate_limit_event: resetsAt/utilization)
                                        ▼ 续跑:每项目 git 脏检查 → claude --continue -p "continue"

──────────────  飞书(可选,独立 Node 进程)  ──────────────
 开机 Startup → feishu-launch.vbs(守护,node 挂了 ~8s 重启;重启时全部会话重置为 idle)
      ▼
 feishu-agent.js ◀── WSClient 长连接 ──▶ 飞书开放平台
  · 收:im.message.receive_v1 / card.action.trigger / application.bot.menu_v6
  · 事件 handler 毫秒级返回(ACK);claude 跑在后台 bg()+inflight(否则飞书停止投递=「白天卡死」)
  · 回复按用户路由(userChats: open_id→各自私聊);通知经 Send-FeishuNotify → feishuChatId(仅 owner 可绑定),每条尾缀 · HH:mm
```

飞书会话状态机(存 `feishu-sessions.json`,agent 重启全部归 idle):

```
            ┌────────── idle(默认:自由文本只弹主菜单卡,绝不误跑)──────────┐
   [💬闲聊]│                    [进项目 / 项目名 / 编号]                     │底部菜单=逃生舱
            ▼                              ▼                               (任何状态→idle+新主菜单卡)
          chat ──自由文本──▶ claude    project(先选 sub,才接受自由文本)
   (scratch 会话,非 owner            ├─ sub=query  → 隔离 cwd 的固定查询会话(首建注入 AI_GUIDE.md)
    降权:plan+禁文件/执行)          └─ sub=modify → 先弹会话选择卡(选 work id)→ --resume <work>
                                          没选 work 之前发指令 = 只重弹选择卡,绝不跑 claude
```

## 3. 模块职责(路径 → 职责 → 关键函数/入口)
| 路径 | 一句话职责 | 关键函数 / 入口 |
|---|---|---|
| `src/lib.ps1` | 引擎共享库(被 dot-source):项目发现、探测、续跑、git 守护、日志、飞书通知、缓存清理、生成导览。 | `Test-ClaudeReady`(探测+读 resetsAt/utilization)、`Invoke-ClaudeResume`、`Invoke-ProjectTour`(headless 生成 AI_GUIDE.md,stdin 喂多行 prompt,成功=mtime 前进)、`Get-ClaudeProjects`、`Protect-GitRepo`、`Stop-ProcessTree`、`Save-RealResetFromProbe`、`Send-FeishuNotify`(自动加 `· HH:mm` 时戳)、`Get/Set-CcuConfig`、`Get/Set-CcuState`、`Clear-OldCaches`、`Format-Countdown` |
| `src/checker.ps1` | **无状态状态机**,计划任务每 ~2 分钟跑一次;文件锁防重入;节流→探测→限流/恢复→FIRE 续跑;误判限流 ≥6 次熔断;`-DryRun` 预演。 | 顶层线性脚本(无函数):节流(`sawLimited`→4min,否则 `probeIntervalMinutes`)、fail-closed、一次性(成功后 `enabled=false`) |
| `src/picker.ps1` | WPF/XAML GUI:发现/勾选项目、布防/解除/预演、额度 chip(开窗+点击**按需探测**,后台 runspace 不冻 UI)、彩色日志区+弹出大窗、模型/间隔 chip、忘记闲聊/清空查询/授权用户/**更新导览**(对勾选项目跑 `Invoke-ProjectTour`)。单实例(Mutex)。`-RenderTo <png>` 无头截图。 | `Get-CurLogFile`(永远读最新 `run-*.log`)、`Read-LogTail`、`Set-LogColored`、`Show-LogWindow`、`Show-AuthWindow` |
| `src/feishu-agent.js` | 飞书双向 agent(Node 长连接):消息/按钮 → 会话状态机(闲聊/只读查询/修改+会话选择)+ 权限(owner/viewer)→ 后台跑 claude → 按用户路由回复。离线测试模式 `FEISHU_TEST`。 | `onMessage`/`onCardAction`/`onBotMenu`、`bg`+`inflight`(后台执行,handler 秒回)、`runClaude`(prompt 走 **stdin**)、`runProjectQuery`(注入 AI_GUIDE + `projectGitHash` 新鲜度)、`listProjectSessions`/`sessionPreview`/`buildSessionCard`(✏️修改的会话选择器)、`buildMenuCard`/`buildProjectCard`、`showCard`/`refreshCard`/`currentCard`、`userTarget`/`rememberUserChat`(按用户路由)、`authLevel`、`querySession`/`clearQuerySession`、`MODELS` 注册表/`modelLabelOf`、`apiRetry`、`startHeartbeat` |
| `src/install.ps1` | 部署:src→`%LOCALAPPDATA%\ClaudeResume`、生成珊瑚色图标、桌面快捷方式、注册计划任务、飞书 Startup 项 + `npm install`。**可反复跑**(=重新部署)。 | 顶层脚本 |
| `src/*.vbs` | 隐藏启动器:`launcher.vbs`(GUI)、`checker-launch.vbs`(计划任务)、`feishu-launch.vbs`(守护 node,自动重启+重定向 stdout)。 | wscript 隐藏窗口 |
| `test/*.js` | 8 个自测(4 离线 + 4 跑真 claude,见 §4)。 | `card-flow`/`routing`/`session-pick`/`concurrency`/`modify-resume`/`query-e2e`/`chat-security`/`guide-e2e` |
| `docs/` | `ARCHITECTURE.md`(内部原理)、`LESSONS.md`(踩坑+开发史)。 | — |
| `.claude/skills/project-tour/` | 生成本导览的 skill(GUI「更新导览」按钮跑的是同一套流程的 headless 版)。 | `SKILL.md` |

## 4. 测试 / 运行流程
- **安装/部署入口**:`powershell -ExecutionPolicy Bypass -File src\install.ps1`。复制到 `%LOCALAPPDATA%\ClaudeResume`、建桌面快捷方式「Claude续跑」、注册计划任务 `ClaudeResumeChecker`(每 2 分钟),初始**未布防**。
- **改代码后重新生效(关键,容易忘)**:线上从 `%LOCALAPPDATA%\ClaudeResume` 跑,**不会**自动同步 `src/`:
  1. `.ps1`(GUI/引擎):复制到 AppDir(或重跑 `install.ps1`);GUI 改动**下次开窗**生效。
  2. `feishu-agent.js`:复制到 AppDir → **杀 node**(`Get-Process node | Stop-Process -Force`)→ VBS 守护 ~8s 自动重启(重启会把全部飞书会话重置为 idle)。
  3. 验证:node 进程应为 1 个,`logs\feishu-stdout.log` 出现 `ws client ready`。
- **离线自测(不联网、不占单实例锁,改完先跑,别每次去飞书点)**:
  - `node test/card-flow.js` — 卡片/菜单状态机:「进项目→积压菜单事件」不堆卡、不跳回主菜单。
  - `node test/routing.js` — 三个真实回归:A) 同事的菜单/卡片回复进**同事自己**的私聊、绝不劫持 `feishuChatId`;B) 会话内的「选 A」「1」归会话、不被当命令(idle 下仍是命令);C) 模型注册表(Fable 5 按钮、自由 `模型 claude-*`、垃圾拒绝)。
  - `node test/session-pick.js` — ✏️修改必先弹会话列表(带最近 2 轮摘要)、没选之前**绝不跑 claude**、🔀切换/🆕新开正常。
- **跑真 claude 的自测**(mock 飞书 API,haiku,~$0.1 级):
  - `node test/concurrency.js` — 「白天卡死」回归:查询 handler 秒回,查询进行中菜单/卡片仍即时响应,二次查询得到「查询进行中」,原查询仍送达。
  - `node test/modify-resume.js` — ✏️修改续的是**你选的那个**会话(问老会话里的标记词,答得上=真 `--resume` 到位)。
  - `node test/query-e2e.js` — 多行查询经 stdin 完整送达(换行不截断)。
  - `node test/chat-security.js` — 非 owner 闲聊套不出 `config.json` 里的 `feishuAppSecret`。
  - `node test/guide-e2e.js` — 有 AI_GUIDE.md 的项目,文件名解码题答案用导览(而非天真读法)。
- **典型使用流程(跑一次)**:
  1. 双击桌面「Claude续跑」→ 勾选要续跑的项目(自动发现自 `~/.claude/projects`;「+ 文件夹」可加任意目录);
  2. 点**预演**确认计划(日志区出现 DRY-RUN 行:哪些项目、探测节奏);
  3. 点**布防续跑**,可关窗(限流前/后布防都行——未限流就保持监视);
  4. 撞限流后 checker 每 4 分钟一探(被拒探测不耗额度),重置瞬间依次续跑;
  5. 每项目:git 脏检查(`stash`/`branch` 兜底)→ `claude --continue -p "continue"` → 飞书推 ✅/❌;
  6. 全部完成 → 自动解除布防(一次性)→ 飞书推「🎉 全部完成」。
- **续跑(fire)路径**:依赖真实额度,无法离线完全验证。先按 **预演(Preview / `checker -DryRun`)** 看计划(哪些项目、探测节奏),真实重置自行验证。
- **依赖 / 环境**:Windows 10/11;PowerShell 5.1(系统自带);Node.js LTS(仅飞书需要);`claude` CLI(`npm i -g @anthropic-ai/claude-code`,与 VS Code 扩展共享登录/会话)。
- **飞书后台一次性配置**(否则机器人收不到消息):启用机器人;权限 `im:message`(收+发);事件订阅方式=**长连接**,订阅 `im.message.receive_v1`、`card.action.trigger`、`application.bot.menu_v6`;配置机器人自定义菜单;**发布版本**。密钥填进 `config.json` 后重跑 `install.ps1`。

## 5. 数据格式与命名约定
本项目**没有科学数据文件**,「数据」= AppDir 下的 JSON 状态 + 日志 + Claude 会话文件。

- **目录布局**:源码/文档在仓库(`C:\Users\23230\Desktop\claude-resume`);**运行态**全在 `%LOCALAPPDATA%\ClaudeResume\`:
  - `config.json`(GUI 写)/ `state.json`(checker 写)/ `feishu-sessions.json`(agent 写);
  - `logs\`(全部日志)、`node_modules\`、`icon.ico`;
  - `feishu-agent.pid`(单实例锁)、`checker.lock`(防重入)、`feishu-token.json`(tenant token 缓存);
  - `feishu-query\`(每项目查询 `.started` 标记)、`feishu-query-cwd\<sha1>\`(只读查询的隔离 cwd)、`feishu-chat\`(闲聊 scratch 会话)。
  - **密钥只在 `config.json`,gitignore,绝不进仓库。**
- **日志文件名解码**(都在 `logs\`,**一律本地时间**,绝不用 UTC)。真实样例 `run-20260716.log` 拆解:
  - `run-` = 续跑引擎日志(`Write-CcuLog` 写,GUI 日志区显示的就是它);
  - `20260716` = 本地日期 `yyyyMMdd`(2026-07-16),每天一个文件;
  - GUI 永远读**最新的** `run-*.log`(`Get-CurLogFile` 按 `LastWriteTime`),绝不固定开窗那天的名字(跨午夜会空白)。
  - 其余:`feishu-<yyyy-MM-dd>.log` = 飞书 agent 日志(`logLine`);`feishu-stdout.log` = node stdout/stderr(SDK 连接日志,>2MB 由 `Clear-OldCaches` 清空);`gui-error.log` = GUI 自身异常。
  - `Clear-OldCaches` 每 tick 还会删 >30 天的日报志、删 AppDir 里的探测残留会话;导出日志 = 合并所有 `run-*.log` + `gui-error.log` 成一个 **UTF-8 带 BOM** 文件。
- **只读查询会话 id / cwd 解码**(每项目一个共享只读 Q&A 会话):
  - `querySession(projectPath)`:`h = sha1(projectPath.toLowerCase())` 的 hex;会话 id 拼成**固定 UUID**:`h[0:8]-h[8:12]-4h[13:16]-8h[17:20]-h[20:32]`(第 3 段固定 `4`、第 4 段固定 `8`,凑成合法 v4 UUID)。
  - 标记文件 `feishu-query\<h>.started`,内容 `{id,path,name}`(供 GUI「清空查询」定位会话 jsonl)。
  - 隔离 cwd `feishu-query-cwd\<h>\`——查询**在这里**跑 + `--add-dir <项目路径>`,**绝不**在 `project.path` 里跑(否则查询会话会污染 `--continue` 池,修改项目时会误续到查询会话)。
  - **清空** = 删 `.started` **且**删 `~/.claude/projects/*/<id>.jsonl`(光删标记不够——`--session-id` 撞已存在 id 报 `already in use`)。
- **项目发现与工作会话**:
  - 发现:`~/.claude/projects/<encoded>/*.jsonl`,文件夹名对路径有损/歧义,所以读每个 session `*.jsonl` 头部的真实 `cwd`(`-Encoding UTF8`,否则中文路径丢)+ 最后使用时间;探测会话固定落在 AppDir 对应文件夹,被发现逻辑排除。
  - ✏️修改的会话列表 `listProjectSessions`:同样按 cwd 定位文件夹;标题取 jsonl 顶部的 `ai-title`(约第 8 行,**只读文件头**,27MB 转录也不整读);摘要 `sessionPreview` 只读**文件尾** N 字节,取最近 2 轮对话。
- **JSON 内部结构**(完整字段见 [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md)):
  - `config.json`(GUI 写,UTF-8 **无 BOM**——有 BOM 会崩 Node 的 JSON.parse)核心字段:

    ```jsonc
    {
      "enabled": false,             // 布防总开关(解除=立即停一切)
      "selected": [{"name":"...","path":"..."}],   // 勾选的项目;customProjects = 手动加的文件夹
      "resumePrompt": "continue",   // 续跑时喂给 claude 的指令
      "skipPermissions": true,      // 全自动(--dangerously-skip-permissions,配 dirty-guard)
      "dirtyGuard": "stash",        // 或 "branch":续跑前保护未提交改动
      "probeIntervalMinutes": 15,   // 可用时探测节奏(GUI 间隔 chip 5/15/30);限流后自动 4 分钟
      "probeModel": "haiku", "resumeModel": "", "perProjectTimeoutMinutes": 30,
      "continuous": false,          // true = 每轮完成后不解除(默认一次性)
      "feishuAppId": "", "feishuAppSecret": "",    // 自建应用密钥(唯一存放处,gitignore)
      "feishuChatId": "",           // 通知聊天,仅 owner 的消息可绑定
      "feishuChatModel": "",        // 闲聊+项目+查询共用模型,GUI 模型 chip 同步
      "feishuAuthOpenIds": [],      // owner 名单;空 = 未锁定(人人可改)!
      "feishuAuthPassword": "",     // 可选:「解锁 <密码>」自助成为 owner
      "feishuWebhook": "", "feishuSecret": ""      // 备胎:自定义机器人 webhook 单向通知
    }
    ```
    (`feishuViewerOpenIds` 已是遗留 no-op——非 owner 自动就是 viewer。)
  - `state.json`(checker 写):`sawLimited`、`lastProbeUtc`、`limitedRefires`(≥6 熔断防误判死循环)、`projectStatus`(每项目 `success/error/timeout/limited/stopped`)、`phase`(`idle/waiting/resuming/done`)、`realFiveHourResetUtc`/`realSevenDayResetUtc`(**Unix 整数秒**,规避 ISO 时区坑)、`realFiveHourUtil`。
  - `feishu-sessions.json`:chatId → `{mode: idle|chat|project, project, sub: query|modify, work}`;`work` = ✏️修改要续的 claude 会话 id(用户从列表选的,或 🆕 新开的 uuid);**未选时不跑任何东西**;agent 每次(重)启动全部重置为 idle。

## 6. FAQ(同事高频问题,直接给答案)
- **Q:这工具到底做什么?** — A:见 §1。撞 5h 上限时后台等重置,重置就在所选项目里 `claude --continue` 续跑;可选飞书机器人做通知+双向操作。
- **Q:它怎么知道额度什么时候恢复?靠估算吗?** — A:**不估算**。`checker` 固定间隔跑廉价探测 `claude -p "ready"`(`Test-ClaudeReady`),Claude 回的 `rate_limit_event` 带服务器权威的 `resetsAt`/`utilization`;探测显示可用**且之前观察到过限流**才 FIRE。任何模糊读一律 fail-closed(当作仍限流)。见 [ARCHITECTURE.md](docs/ARCHITECTURE.md)。
- **Q:布防了但一直没动静,怎么排查?** — A:看 GUI 日志区(或 `logs\run-*.log`)。正常节奏:「等待中(额度可用)· 下次实探 ~Nm」→ 限流后「限流中,距真实重置 …」(4 分钟一探)→「额度已恢复 → 开始逐个续跑」。若见「探测未就绪 (reason)」= fail-closed 在重试(网络/claude 未装);若根本没日志 = 计划任务没注册,重跑 `install.ps1`。注意:布防在**未限流**时只保持监视,不会跑任何东西。
- **Q:改了 `src/feishu-agent.js` 为什么没反应?** — A:线上从 `%LOCALAPPDATA%\ClaudeResume` 跑。必须复制到 AppDir → 杀 node → 等 ~8s 守护重启 → 看 `feishu-stdout.log` 的 `ws client ready`。见 §4 / `CLAUDE.md`。
- **Q:以前白天点飞书按钮全没反应(「白天卡死」),现在怎么解决的?** — A:飞书 SDK 要等 handler resolve 才 ACK 事件;以前 handler 里 `await` 一个 1–4 分钟的 claude 跑,飞书等不到 ACK 就停止投递/重复投递。现在 handler **毫秒级返回**,claude 跑在后台(`bg()` + `inflight` 同步占位防竞态);运行中再发消息得到「查询进行中」。回归测试 `test/concurrency.js`。卡死兜底:点**底部菜单**任意项 = 逃生舱,回干净主菜单。
- **Q:✏️修改项目会续到哪个会话?会不会猜错?** — A:**不猜**。点 ✏️修改先弹**会话选择卡**:该项目最近 5 个 claude 会话(claude 自动标题 + 最后使用时间)或 🆕 新开会话;选中后先发**最近 2 轮摘要**让你想起停在哪,之后每条消息 `--resume <那个 id>`;🔀 切换会话随时重选。没选之前发指令只会重新弹选择卡(`test/session-pick.js` / `test/modify-resume.js` 验证)。
- **Q:同事用机器人,消息会不会串到我这里/他能看到我的通知吗?** — A:不会。每个用户的菜单/回复路由到**他自己与机器人的私聊**(`userChats`/`userTarget`);只有 **owner 本人的消息**才能绑定/改变通知聊天 `feishuChatId`,同事的点击/消息**绝不劫持**它(`test/routing.js` 验证)。
- **Q:谁能改我的项目?怎么控权限?** — A:只有 `config.json` 的 `feishuAuthOpenIds`(**owner/full**)能改项目/改配置/授权/停止运行;**其他所有人自动只读 viewer**(浏览/查询),进项目直接进只读问答,无死按钮。空名单 = 未锁定 = 人人可改,移除最后一个 owner 会解锁,GUI 会警告。闲聊对所有人开放,但**非 owner 的闲聊是 plan 模式且禁文件/执行工具**(套不出 config.json 密钥,`test/chat-security.js` 验证)。
- **Q:「只读查询」会不会改到代码 / 污染我 VS Code 的会话?** — A:不会。查询跑 `--permission-mode plan`,在**隔离 cwd**(`feishu-query-cwd\<sha1>`)+ `--add-dir` 里,固定 session-id,`--disallowedTools Task`。与 ✏️修改(`--resume` 你选的工作会话)完全隔离。
- **Q:在会话里回答 claude 的「选 A」为什么以前会跳回菜单?** — A:旧版在任何模式下都做模糊命令匹配,「选 A」「1」、问候语都被劫持成菜单命令。现在**模糊匹配只在 idle 模式**生效;会话内自由文本一律归会话。显式命令(退出/菜单/停止/模型/帮助)任何模式仍可用。
- **Q:想用新出的模型怎么办?要改代码吗?** — A:不用。按钮注册表 `MODELS` 覆盖当前一线(Fable 5 / Opus / Sonnet / Haiku / 默认),文本命令 **`模型 <任意 claude-* 完整 id>`** 接受任何合法 id,新模型发布当天即可用;GUI 模型 chip 与飞书共享 `feishuChatModel`。
- **Q:AI_GUIDE.md(本文件)是干嘛的?怎么刷新?** — A:给飞书**只读查询**用——`runProjectQuery` 在查询会话首建时**注入一次**,之后 `--resume` 走 prompt cache;首行记录生成时的 git hash,项目提交往前走会提示导览过时。刷新:GUI 勾选项目点**「更新导览」**(跑 `Invoke-ProjectTour`,每项目 1–3 分钟,进度看运行日志),或在项目里跑 `project-tour` skill;刷新后在飞书点该项目的「🧹 清空查询记忆」(或发「忘记查询」)让新导览生效。
- **Q:为什么全用 PowerShell 5.1?有坑吗?** — A:兼容系统自带。坑已处理:`Start-Process` 的 `.ExitCode` 退出后变 `$null`(改看 stream-json result 行)、`Set-Content -Encoding UTF8` 写 BOM 崩 Node(改 `WriteAllText` 无 BOM)、`ConvertFrom-Json` 把 ISO 时间偷偷转本地时区(改存 Unix 整数)、`.ps1` 必须存 UTF-8 **带 BOM**、cmd 把 `-p "多行"` 在首个换行截断(prompt 全部改走 stdin)。见 [LESSONS.md](docs/LESSONS.md)。
- **Q:日志在哪、怎么看?** — A:`%LOCALAPPDATA%\ClaudeResume\logs\`,见 §5。GUI 日志区(可「⤢ 弹出大窗」)显示最新 `run-*.log`;飞书问题看 `feishu-<date>.log` 和 `feishu-stdout.log`;「导出日志」合并成一份可分享文件。通知每条尾部的 `· HH:mm` 是事件真实发生的本地时间。
- **Q:续跑会不会把我未提交的改动搞丢?** — A:不会白丢。`skipPermissions`(全自动)时每个项目续跑前先 `Protect-GitRepo`:有未提交改动就 `git stash push -u`(留名 `claude-resume-guard <时间戳>`,可 `git stash pop` 找回),或 `dirtyGuard="branch"` 模式下新建 `claude-resume/<时间戳>` 分支再恢复改动。非 git 目录不做保护,谨慎勾选。
- **Q:新同事想用机器人,要做什么?** — A:飞书后台把他加进应用「可用范围」即可;他私聊机器人发一句「帮助」就能用。他自动是 viewer(浏览+只读查询+闲聊),不会碰到你的项目;要给他改项目权限,owner 发 `授权 ou_xxx`(他首次发消息时机器人会展示他的 open_id),或用 GUI「授权用户」窗口管理。
- **Q:为什么不用 `.lnk → powershell -WindowStyle Hidden`?** — A:火绒(Huorong)会删这种组合。所有隐藏启动走 `wscript` 隐藏的 `.vbs` + 计划任务。

- **Q:飞书里有哪些文本命令?**(与按钮等价)— A:
  - 导航:`菜单`/`项目`(主菜单卡)、`进入 <编号|名字>`、直接发项目名/编号(仅 idle)、`退出`;
  - 干活:`查询 <问题>`(只读直达)、`<项目名> <指令>`(一次性,仅 idle)、`停止`(owner);
  - 记忆:`忘记闲聊`、`忘记查询`(清当前项目查询会话);
  - 配置(owner):`模型`/`模型 <名字或任意 claude-* id>`、`授权 ou_xxx`、`取消授权 ou_xxx`、`授权列表`、`解锁 <密码>`;
  - 其他:`状态`、`帮助`。

## 7. 术语表(中英 / 缩写对照)
| 术语 | 含义 |
|---|---|
| 布防 / 解除 (Arm / Disarm) | 布防=开启自动续跑监视(`config.enabled=true`);解除=全局总开关关闭,立即停止一切自动续跑 |
| 预演 (Preview / DryRun) | `checker.ps1 -DryRun`,只报计划不探测不续跑 |
| 探测 (Probe) | `Test-ClaudeReady`:一次廉价 `claude -p "ready"`,读服务器 `rate_limit_event` 判断额度并取 resetsAt/utilization |
| fail-closed | 任何模糊/失败读一律当作「仍限流」,绝不误 fire |
| FIRE / 续跑 (Resume) | 依次在所选项目跑 `claude --continue -p "continue"` |
| 一次性 (one-shot) | 成功续跑后自动 `enabled=false`,不会每 5h 循环(`continuous=true` 可关) |
| git 脏检查 (dirty-guard) | 续跑前若 repo 有未提交改动,自动 `git stash`(或建分支)保底可恢复 |
| 只读查询 (read-only query) | `--permission-mode plan` 的每项目共享 Q&A 会话,只读不改,隔离 cwd + `--add-dir`,首建注入 AI_GUIDE |
| 修改项目 (modify) | ✏️:先从**会话选择卡**挑一个 claude 会话(或 🆕 新开),之后 `--resume <该 id>` 续它 |
| 会话选择卡 (session picker) | `buildSessionCard`:最近 5 个会话(ai-title+时间)+ 🆕 新开;选中发 2 轮摘要;🔀 可换 |
| 闲聊 (chat) | 在 scratch 会话里和 Claude 聊,不碰任何项目;非 owner 的闲聊被降权(plan+无文件/执行工具) |
| owner / viewer | owner=在 `feishuAuthOpenIds`,能改项目+配置;viewer=其他所有人,自动只读 |
| 按用户路由 (per-user routing) | `userChats`(open_id→私聊)+ `userTarget`:每人的回复进自己私聊;`feishuChatId` 只认 owner |
| 逃生舱 (escape hatch) | 底部菜单:任何状态点它都回 idle 并补发一张新的主菜单卡 |
| 后台执行 (bg / inflight) | 事件 handler 秒回 ACK,claude 在 `bg()` 后台跑;`inflight` 同步占位防「检查→spawn」竞态 |
| 心跳 (heartbeat) | 长任务每 15s 回「🤔 思考中…已 Ns」;结果尾缀 `⏱/tokens/$`(`fmtMeta`) |
| 控制卡 (control card) | 每聊天仅一张导航卡(主菜单⇄项目卡),`showCard/refreshCard` 原地 patch,绝不堆卡 |
| 模型注册表 (model registry) | `MODELS` 按钮一线模型 + 「模型 claude-*」自由 id,新模型零改码可用 |
| WSClient / 长连接 | 飞书 SDK 的持久 WebSocket,收事件无需公网 IP;handler resolve 后才 ACK |
| rate_limit_event | Claude stream-json 里带 `resetsAt`/`utilization`/`rateLimitType`(five_hour/seven_day)的行 |
| 更新导览 (project tour) | GUI 按钮/`Invoke-ProjectTour`/`project-tour` skill:生成本 AI_GUIDE.md 供查询提速提准省 |
| AppDir | `%LOCALAPPDATA%\ClaudeResume`,程序运行态目录(源码改动必须复制过去才生效) |

## 8. 文档索引(深挖时读)
- [README.md](README.md):安装、使用、飞书配置(开发者后台配置 + 发布版本)、两类项目会话、权限模型——面向用户的完整说明。
- [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md):内部原理——checker 状态机、探测/读重置、GUI 单实例/按需探测、飞书状态机与卡片机制、`config.json`/`state.json` **全字段**、测试状态。深挖实现先读它。
- [docs/LESSONS.md](docs/LESSONS.md):真实踩坑 + 开发史(PS 5.1 exit-code/BOM/时区、cmd 换行截断 `-p`、`--continue` 池污染、飞书卡片堆叠/抢卡、日志系统、权限模型简化、离线自测方法论)。排查「为什么这么写/为什么会这个 bug」看它。
- [CLAUDE.md](CLAUDE.md):协作约定——中文沟通、飞书 agent 重新部署步骤、离线自测命令、安全约束。
- [.claude/skills/project-tour/SKILL.md](.claude/skills/project-tour/SKILL.md):本导览的生成流程(GUI「更新导览」按钮即其 headless 版)。
