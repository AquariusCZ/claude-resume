# AI Resume v2 审计 + 冒烟计划(2026-08-08 交接版)

> 基线:`main` = 见 `git log --oneline -1`,`dotnet test csharp\AiResume.sln` = **587 通过 / 0 失败 / 0 跳过**。
> 仓库根:`C:\Users\<你>\Desktop\AI Resume`(**路径含空格,命令里必须加引号**)。

## 0. 先读这一段:上一轮审计到底覆盖了什么

上一轮交接出去的审计**只跑到前置条件就停住了**,三条里第 3 条不符(cc-connect 未运行),
执行方按指示停手报告——**这是正确的行为**,但结果是:

**本计划 §3 之后的内容,一条都没有被验证过。** 不要把上一轮的"533 通过"当成功能验证,
那只是单元测试,和"这个产品在真机上能用"是两件事。

它有价值的产出只有一条,已确认为真:**没有任何东西守护 cc-connect**——该项已于 2026-08-08 修复(§4.1)。

---

## 1. 红线(违反即中止,不要"先试试看")

1. **不得对真实项目或真实会话启动任何 AI 修改运行。** 曾经一个测试 resume 了真实会话,
   AI 带着旧上下文执行并 push 了 commit;"事后停止"拦不住。
2. **不得触碰生产状态目录** `%LOCALAPPDATA%\AI Resume\state`。里面有 DPAPI 加密的飞书凭据。
   **2026-08-08 刚在这里丢过一次数据**:`ShadowPaths.EnsureRoot()` 曾无条件迁移,
   而 legacy 路径取的是真实 `%LOCALAPPDATA%\ClaudeResumeShadow`;
   `PowerLossRecoveryTests` 用 `AIRESUME_SHADOW_DIR` 把 Worker 子进程隔离到临时目录,
   那个子进程于是把生产凭据搬进临时目录并随测试收尾删除。
   **教训:隔离靠的是被测代码尊重隔离开关,不是靠测试自己小心。**
   动任何"自动迁移/自动清理"逻辑时,先问它在被隔离时会不会伸手到真实路径。
3. **不得触碰** `~/.claude`、`~/.codex`、`~/.qoder`、`~/.config/opencode` 的用户配置。
4. **不得回显** `app_secret`、`api_key`、`[management] token`、`~/.claude/.credentials.json`、
   `~/.codex/auth.json` 的值。需要证明"读到了"就报长度或前 4 字符。
5. **清理时作用域必须写死。** `%TEMP%` 下同时有 `pss_smoke_*` / `ocs_smoke`,
   那是用户**另外两个项目**的残留,与本项目无关。任何 `*smoke*` 的模糊匹配都是错的。
6. **不得 `git push`**,不得改远端,不得放宽 `allow_from`。
7. **不得 `daemon install` / `uninstall`,不得 `--force` 启动 cc-connect。**
   但**允许**在前置条件 4 不满足时做一次有界恢复:先跑 `AiResume.Worker.exe preflight`,
   返回 `Clear` 之后执行 `Start-ScheduledTask -TaskName 'cc-connect'`(或 `cc-connect daemon start`),
   等 30 秒再复核。仍不满足才停手报告。
   *这一条是补的*:2026-08-08 首次交接就死在"红线禁止启停"与"前置条件要求 Running"的
   自相矛盾上——执行方三次复核、三次拒绝自行修复,**它做得对,是计划把它锁死了。**

---

## 2. 前置条件(不符就停手报告,不要自行修复)

| # | 检查 | 期望 |
|---|---|---|
| 1 | `dotnet test "…\AI Resume\csharp\AiResume.sln"` | 587 通过 / 0 失败 / 0 跳过 |
| 2 | `git -C "…\AI Resume" status --short` + `git log --oneline -1` | 干净;HEAD 与 origin/main 一致 |
| 3 | `Get-ChildItem "$env:LOCALAPPDATA\AI Resume\state"` | 目录存在,含 `secrets\feishu-platform.bin` |
| 4 | `cc-connect daemon status` | `Status: Running` |
| 5 | `Get-CimInstance Win32_Process -Filter "Name='cc-connect.exe'"` | **恰好 1 个进程** |

条件 5 是硬红线:飞书长连接是集群模式,**两个消费者在线时事件会被随机投给其中一个**,
表现为"机器人时灵时不灵"。数量不是 1 就停手报告,不要自行杀进程。

---

## 3. 按价值排序的验证项

### T1 — 限额后自动续跑(最高价值,从未端到端验证)

这是本产品**唯一不可替代**的能力,而它从来没有在真实限额下跑通过。

1. 新建靶子目录 `%TEMP%\airesume-t1-target`,`git init`,提交一个 README。
2. 打开控制面 → 「添加项目」选它 → 勾选 → 「布防」。
3. 确认状态灯变绿、文案为「监视中」;`state\config.json` 的 `Armed=true` 且 `Selected` 含该路径。
4. **不要真的去耗尽额度。** 用 `AIRESUME_SHADOW_DIR` 指向临时目录起一个隔离 Worker,
   构造一个 `LimitReached` 的持久状态,观察 `ResumeEngine` 是否按队列顺序发起续跑。
5. 记录:是否只对**勾选的**项目发起、顺序是否与 `Selected` 一致、
   续跑进程是否带 `AI_RESUME_INTERNAL_RUN=1`(否则会触发一条假的完成通知)。

**判定**:能观察到"限额→等待→恢复→按序续跑"完整链路即通过。中途任何一步拿不到证据都算未通过,
**不要用单元测试的绿色替代**。

### T2 — Codex 真实授权探测(2026-08-08 新增,只验证过成功路径)

`CodexAuthProbe` 带凭据 GET `{base_url}/v1/models`。已实测:带 key → 200,去掉 key → 401。
**失败路径全部只有单元测试,没有真机验证。**

1. 断网 → 打开控制面 → Codex 行应显示灰色「未验证授权(网络不可达…)」,
   **绝不能显示红色"认证被拒"**(把断网说成认证失败会把排查方向带偏)。
2. 临时把 `~/.codex/auth.json` 改名(**记得改回来**)→ 应显示灰色「读不到 Codex 的 base_url 或凭据」。
3. 正常状态 → 应显示绿色「可用 · 凭据已验证」,且**面板打开约 1-2 秒内**出现,不烧 token。
4. 用 `codex doctor --json` 的输出对照:doctor 里的 401 **不得**导致红灯。

### T3 — 状态目录迁移(刚改,只在开发机验证过一次)

1. 造一个假的 `%LOCALAPPDATA%\ClaudeResumeShadow`,塞入 `config.json` 与 `secrets\x.bin`。
2. 起一次控制面 → 内容应被搬到 `%LOCALAPPDATA%\AI Resume\state`,旧目录空后被删。
3. 再造一次,但让新位置**已有同名** `config.json` → 应**跳过不覆盖**,旧目录保留。
4. **回归红线**:设 `AIRESUME_SHADOW_DIR` 后跑全量测试,
   跑完确认 `%LOCALAPPDATA%\AI Resume\state` 内容一字未动。

### T4 — 安装/卸载往返

1. `AiResume.Worker.exe install` → 三个快捷方式存在、**图标非空白**、TargetPath 指向安装目录。
2. `AiResume.Worker.exe uninstall` → 程序文件删除,**`state\` 必须保留**,
   且 `~/.claude/settings.json` 里用户自己的钩子完好。
3. 再 `install` → 通知钩子应只有一条我方条目(不是两条)。

### T5 — 完成通知端到端

对 5 个源逐一:开关打开 → 在该客户端跑一个短任务 → 确认收到且只有一条。
**重点**:Qoder 的脚本必须检查 stdin 的 `stop_hook_active`,为 true 时立即 exit 0,
否则会触发阻断→重试的无限循环。

### T6 — cc-connect 手机端(daemon 已就位,可直接测)

`/dir` 切目录、`/mode plan` vs `/mode yolo` 的读写边界、`/model switch`、`/provider switch`、
`/new` + `/list` + `/switch`、`/stop`。修改类命令**只允许对靶子目录**执行。

### T7 — 额度读数准确性

面板读数 vs `claude` 自己的 `/usage`。核对两点:
**光柱画的是额度不是时间**(曾经画反过),以及 5h/7d 两个窗口的起止时刻。

---

## 4. 已知缺口(不是 bug,是没做完)

| 缺口 | 状态 | 既定修法 |
|---|---|---|
| ~~cc-connect 无自启无守护~~ | **2026-08-08 已解决** —— 注册为 Windows 计划任务;S4U 无窗口 + 5 分钟无限期重复自愈。见 §4.1 / §4.1.1 | — |
| **限额续跑未端到端验证** | 见 T1 | — |
| **改配置后 GUI 不提示要重启 daemon** | 「生成 cc-connect 配置」写完文件就返回,但 daemon 不会自己重读 | 需在 GUI 补一句提示或直接调 `cc-connect daemon restart` |
| **IPC `ipc_status` 曾 60/60 空响应** | 未复查 | 读 IPC 服务端代码后再定 |
| `~/.codex/config.toml` 的 `notify` 链损坏 | 六层嵌套转义,指向已被删除的 `%LOCALAPPDATA%\ClaudeResume\completion-notify.js` | 需人工确认后清理;**不要自动改用户的 codex 配置** |
| 仓库仍留 39 个 v1 的 JS 模块 | 有意保留备查 | — |

### 4.1 计划任务的四项设置必须复核(装 daemon 时真踩到)

`cc-connect daemon install` 建出来的计划任务,**四项默认值里三项是错的**:

| 设置 | 上游默认 | 已改成 | 不改会怎样 |
|---|---|---|---|
| `ExecutionTimeLimit` | **`PT72H`** | `PT0S` | **跑满 72 小时被 Windows 掐掉**,且极难联想到是计划任务干的 |
| `StopIfGoingOnBatteries` | **`True`** | `False` | 笔记本一拔电源机器人就停 |
| `DisallowStartIfOnBatteries` | **`True`** | `False` | 电池供电时开机根本不启动 |
| `RestartCount` | **`0`** | `3`(间隔 `PT1M`) | **崩了不会自动拉起** —— "装了 daemon 就有守护"这句话在改之前是假的 |

还缺一项**上游根本没有**的:触发器只有「登录时」,没有重复。
一旦停掉就再也回不来——`RestartCount` 只在任务**失败**时生效,而 Ctrl+C 退出(0xC000013A)不算失败。

**任何一次 `daemon uninstall` + `install` 都会把这些打回默认。** 重装后必须重跑:

```powershell
$t = Get-ScheduledTask -TaskName 'cc-connect'
$s = $t.Settings
$s.ExecutionTimeLimit = 'PT0S'; $s.StopIfGoingOnBatteries = $false
$s.DisallowStartIfOnBatteries = $false; $s.RestartCount = 3; $s.RestartInterval = 'PT1M'
$s.MultipleInstances = 'IgnoreNew'
$trg = $t.Triggers[0]
$trg.Repetition = (New-ScheduledTaskTrigger -Once -At (Get-Date) `
    -RepetitionInterval (New-TimeSpan -Minutes 5)).Repetition
$trg.Repetition.Duration = $null            # 无限期
$trg.Repetition.StopAtDurationEnd = $false
Set-ScheduledTask -TaskName 'cc-connect' -Trigger $trg -Settings $s
```

### 4.1.1 那个黑窗口(已解决,但重装会回来)

上游装出来的任务是 `LogonType = Interactive`,动作虽然写着 `-WindowStyle Hidden`,
但**该参数在交互会话里不生效**——控制台由系统在 PowerShell 处理它之前就分配好了,
桌面上于是留着一个黑窗口,**而那个窗口就是 cc-connect 本体**。

**2026-08-08 实测:用户顺手关掉那个"多余的 cmd 窗口",机器人立刻下线**
(`CTRL_CLOSE_EVENT` → 退出码 `0xC000013A`)。同一原因至少停过三次,
而当时只有登录触发器、`RestartCount` 又只在任务**失败**时生效(Ctrl+C 退出不算失败),
于是停了就再也回不来。

**已修**:`LogonType` 改为 **S4U**(以本用户身份、不存密码、不要求已登录),
进程因此跑在 **session 0** —— 那里没有桌面,窗口不可能存在。
复核证据:`SessionId = 0`、`MainWindowHandle = 0`。

改法见 [`tools/cc-connect-hide-window.ps1`](../tools/cc-connect-hide-window.ps1),
**必须以管理员身份运行**(账户在 Administrators 组不够,进程本身要提权)。
该脚本同时重申 §4.1 的全部加固项。

> 走过的弯路,别再试:`wscript` + `WScript.Shell.Run(cmd,0,True)` 包一层,
> 交互式手跑可行,**但在计划任务里 wscript 立即以 0 退出且什么都没启动**。
>
> 另:交给人手动执行的 `.ps1` **必须存成 UTF-8 with BOM**。
> Windows PowerShell 5.1 读无 BOM 的 UTF-8 会按 GBK 解码,中文和 emoji 全乱、
> 直接变语法错误(实测)。脚本里也不要用 emoji。

### 4.2 启动日志里那条 WARN 是正常的

`engine started with partial readiness ... ready=1 pending=1` 出现在启动瞬间,
因为 weixin(ilink)握手比 feishu 慢约 18 秒。**判据是之后有没有第二条 `platform ready`**,
不是这条 WARN 本身。实测 01:20:16 feishu ready → 01:20:34 weixin ready,之后无新告警。

---

## 5. 交接提示词(直接发给下一个 AI)

> 你要审计的项目在 `C:\Users\<你>\Desktop\AI Resume`(**路径含空格,命令加引号**)。
> 先完整读 `CLAUDE.md` 和 `docs/AUDIT-AND-SMOKE-PLAN-20260808.md`,**§1 红线逐条遵守**。
>
> 按 §2 核对**五条**前置条件。条件 4/5 不满足时按 §1 第 7 条做一次有界恢复;
> 其余任一不符立即停手报告,不要自行修复。
>
> 通过后按 §3 的 T1→T7 顺序执行。每一项都要给出**可核验的证据**
> (命令原文 + 输出片段 + 文件路径 + 时间戳),而不是"看起来正常"。
> 拿不到证据就写"未验证",**绝不用单元测试的绿色替代真机验证**。
>
> 特别注意 §1 第 2 条和第 5 条——那两条都是本项目真出过事故才写下来的。
>
> 最后输出一份报告:已验证 / 待真机验证 / 未验证 / 范围外,四类分开写。
