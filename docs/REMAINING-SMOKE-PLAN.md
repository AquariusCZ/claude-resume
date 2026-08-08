# 收尾冒烟与审计计划(交接给执行 AI)

冻结于 2026-08-07。范围:迁移收尾阶段**尚未验证**的项。**不含开机自启**(用户明确排除)。

> **路径**:仓库根目录是 `C:\Users\<you>\Desktop\AI Resume`
> (2026-08-07 由 `claude-resume-migration` 更名而来)。
> **路径含空格,命令里必须加引号**——不加引号的 `cd C:\Users\<you>\Desktop\AI Resume`
> 会被解析成两个参数。
>
> 全仓仍有 6 处提到旧名,**那些是历史陈述**(记录「项目名曾从 claude-resume-migration
> 漂成 _smoke-cutover」这次事故),**不要当成待修的路径去替换**。

---

## 0. 现场基线(执行前核对,不符即停手报告)

| 项 | 期望 |
|---|---|
| 测试 | `cd csharp; dotnet test` → **533 通过 / 0 失败 / 0 跳过** |
| 工作树 | `git status --short` 干净 |
| cc-connect | 恰好 1 个进程;日志里 `platform ready` 出现 **2** 次(feishu + weixin) |
| 生产 Worker | 从 `%LOCALAPPDATA%\AI Resume\` 运行(**不是**从仓库 bin) |
| 通知源 | `"%LOCALAPPDATA%\AI Resume\AiResume.Worker.exe" notify list` → ClaudeCode / Cline / Qoder 三项已启用 |
| 授权 | cc-connect 日志**不含** `allow_from is not set` |

**基线不符不要修**——现场与任务书不一致时继续做只会把问题搅浑,停下来报告。

### 0.1 两个会让你误判的已知情况

1. **产物现在装在 `%LOCALAPPDATA%\AI Resume\`,仓库只是源码。**
   快捷方式、开机自启、Claude Code 的 Stop 钩子全部指向安装目录。
   改了代码要重新 `AiResume.Worker.exe install` 才会生效——直接跑仓库 bin 只影响你自己,
   不影响生产。

2. **`~/.claude/settings.json` 归 Claude Code 所有,它运行期间会覆盖外部改动。**
   2026-08-07 实测:我们写进去的 Stop 钩子被 Claude Code 用它内存里的旧配置整份写回、
   整个 `hooks` 键消失。判据是我们的适配器每次写入都会刷新同目录的 `.bak`,
   而那次写入没刷新。**若你发现钩子莫名消失,先看 `.bak` 的时间戳再下结论**,
   不要以为是我们的代码丢了它。

---

## 1. 硬红线(违反即中止)

1. **不得启停、重启、kill cc-connect**;不得手改 `~/.cc-connect/config.toml`。
2. **不得对任何真实项目启动会写文件的 AI 运行。** 修改类操作只允许对
   `C:\Users\<you>\Desktop\_smoke-cutover` 这一个靶子目录做。
3. **不得读取、打印或写入任何凭据实值**:`config.toml` 的 `app_secret`/`api_key`/
   `[management] token`、`~/.claude/.credentials.json`、DPAPI 里的飞书凭据。
   报告里出现即失败。只报键名、长度、前 4 位。
4. **不得触碰**用户级 AI 配置(`~/.claude`、`~/.codex`、`~/.qoder` 等)——
   §2 里明确要求的受控写入除外,且写完必须复原。
5. **不得 `git push`**,不得改 `main`,不得 `git reset --hard` / `checkout --` / `clean`。
6. 自己拉起的 Worker 宿主必须设 `AIRESUME_TEST_PIPE_SUFFIX`(`[A-Za-z0-9]{1,32}`),
   否则会和生产 Worker 抢同一把单实例互斥体。
7. **绝不通过弱化断言、删测试、加 Skip 让红变绿。** 分不清是实现错还是测试错,
   就保留红的状态并记录。

---

## 2. 任务(按价值排序)

### T1 — 续跑编排:真实限流下的自动接续(**最高价值,本项目的存在理由**)

ADR-0003 说 AI Resume 唯一不可替代的职责是「Claude 限额后自动续跑」。
代码写了、测试绿了,但**从未在真实限流下端到端验证过**。

它和别的功能不一样:飞书通了你立刻知道,续跑没通你**不会知道**,
直到某天夜里限流、第二天早上发现活没干。

**不要靠真烧用户额度来触发。** 用可控方式:

1. 读 `csharp/src/AiResume.Worker/Resume/` 与 `Probes/` 下的实现,弄清
   「判定为限流」的输入是什么(退出码?stderr 模式?探测结果?);
2. 在**隔离的 shadow 目录**(系统 temp 新建,设 `AIRESUME_SHADOW_DIR`)里
   构造一次限流→恢复的完整周期,用假 provider / 桩探测驱动;
3. 断言:限流被识别 → 队列按顺序等待 → 额度恢复后**自动**拉起下一个项目 →
   状态机落到终态;
4. 若现有测试已覆盖其中一部分,**补的是缺的那部分**,不要重复造。

**产出**:① 「已覆盖 / 未覆盖」对照;② 新增用例(全绿);
③ 明确说明「哪些环节仍然只能靠真实限流才能验证」——这一条必须诚实写。

时间盒 3 小时。

### T2 — IPC 层:未知 runId 不应答,以及句柄可能不释放

浸泡已完成(结论见 `docs/evidence/soak-conclusion-20260807.md`):
**私有字节通过**(暖机后稳定在 22 MB),但**句柄与 GC 堆的底部在抬高,不判定**。

同一批数据里有一条具体线索:
- `ipc_ping` 与 `ipc_list_runs` **60/60 正常**;
- `ipc_status` **60/60 全是空应答**。

采样器本意是用一个固定的假 runId 去打错误路径——那**应该收到一个错误应答**,
而不是什么都不回。如果「未知 runId 时服务端不应答」属实:
① 客户端只能等到超时,是明确缺陷;
② 每 5 分钟 3 次连接与句柄增长同频,这可能正是句柄不释放的同一处根因。

任务:
1. 读 IPC 服务端(`csharp/src/AiResume.Ipc/` 与 Worker 里的 `TransportBootstrap`),
   确认未知 runId 的应答路径;
2. 确认每连接的资源释放(`NamedPipeServerStream`、`CancellationTokenSource`、
   注册的回调是否都 Dispose);
3. 补测试:未知 runId **必须**返回结构化错误应答而不是静默;
   连续 N 次连接后句柄数不得持续增长(可用 `Process.GetCurrentProcess().HandleCount` 断言上界);
4. 若确认是缺陷,**修实现**并在报告里单独点名。

> 别再跑空转浸泡去"再看看"——它已经跑满 60 个连续样本,
> 空转能回答的问题已经回答完了。剩下的要靠读码与定向测试。

时间盒 2 小时。

### T3 — 完成通知的边界行为(投递端刚上线,只验过成功路径)

飞书投递已实测成功(`sweep total=1 sent=1`,用户真实收到消息)。
`CompletionNotifierTests` 有 14 个单测,现在要验**真实环境**下的几条:

1. **七天去重真的生效**:往 `%LOCALAPPDATA%\ClaudeResumeShadow\completion-events\`
   写一条 eventId 与 `completion-notify-seen.json` 里**已有记录相同**的合成事件,
   等一轮(30 秒),断言 `duplicate=1`、**没有**第二条飞书消息;
2. **坏事件被隔离**:写一条 `{ 坏JSON`,断言它被移进 `completion-events\malformed\`,
   主队列清空,后续正常事件不受影响;
3. **收件人缺失时不丢事件**:**只读验证**——检查代码路径确认 `receiverOpenId`
   为空时事件文件保留(不要真去清空授权名单)。

> 合成事件的 `cwd` 一律写 `C:\Users\<you>\Desktop\_smoke-cutover`,项目名一眼认得出是测试。
> **每条真实投递都会推到用户飞书**,所以第 1 条必须先确认 seen 表里确有该 eventId 再写文件。

时间盒 1 小时。

### T4 — 通知源身份判定的全面复核(刚修完一个,可能还有)

2026-08-07 修了一个:`QoderNotificationAdapter` 的所有权标记是
`airesume-completion-hook.cmd`(早期批处理脚本的名字),而 Enable 写进配置的是
`AiResume.Hook.exe`。两者对不上 → 判定彻底失效 → 探测永远报未安装 →
界面开关永远显示关 → **每点一次追加一条**。用户配置里累积到了 14 条。

**而原有的 3 个 Qoder 测试一直是绿的**:它们传的 hookCommand 恰好等于那个标记,
于是「写完认得出自己」在测试里恒成立、在生产里恒不成立。**测试在自证。**

任务:对**每一个**适配器(ClaudeCode / Codex / Cline / Qoder / OpenCode)复核:
1. 它的测试传的 hookCommand 是不是**生产真正会传的形状**
   (安装层写的是 `…\AI Resume\AiResume.Hook.exe`)?若不是,改 fixture 并观察是否变红;
2. Enable → Probe 是否自洽?连续 Enable 三次是否只留一条?Disable 能否清干净?
3. OpenCode 目前探测为「插件文件未安装」——确认它的启用路径是否可用,
   还是同类的标记不匹配问题。

`NotificationMarkerIdentityTests` 已经为 Qoder / ClaudeCode / Codex 建了模板,照着扩。

时间盒 1.5 小时。

### T5 — 文档一致性

代码与运行时是真身,文档是影子。**冲突时改文档,不要反过来改代码。**

已知漂移(不要只查这些):

1. `docs/FEISHU-BOT-GUIDE.md`——①「底部三个菜单按钮」整段已过时(用户已删菜单);
   ② 现在**微信也接入了**,该文档只讲飞书,需补微信部分(命令完全相同);
2. `README.md` 是否还在描述现役 PowerShell + Node 系统(已于 2026-08-07 退役);
3. 全仓凡提到旧仓库路径的地方(若已改名);
4. `docs/STAGE-10-SMOKE-PLAN.md` 的 A 段判据里 `_smoke-cutover` 已不是
   cc-connect 的项目名(现为 `ai-resume`);
5. **安装层是新的**(`install` / `uninstall` 命令,产物在 `%LOCALAPPDATA%\AI Resume\`),
   `docs/ARCHITECTURE.md` 与 `README.md` 应当有一节说明「改了代码要重新 install」。

**产出**:改动 + 一份「改了什么、依据是哪段代码/哪条实测」对照表。
拿不准的列进「待人工裁决」,不要自行改变语义。

时间盒 1.5 小时。

### T6 — provider 探测的边界(刚上线,只验过 happy path)

2026-08-07 新增 `CodexProbe` 与 `DeepSeekProbe`(见 `csharp/src/AiResume.Worker/Probes/`),
控制面「额度模型」区已接线,两档语义:面板加载走 shallow(不烧额度),
用户点「刷新额度」走 deep(Codex 会发一次真实请求)。

只验过成功路径。要补的:

1. `CodexProbe` 的分类分支——目前只在「doctor 全 ok 但 details 含 401」这一种现场验过。
   用注入的假 doctor JSON 覆盖:installation=error / auth=error /
   reachability=error / details 含 429 / JSON 损坏 / codex 命令不存在。
   **注意 CodexProbe 目前没有可注入的输出源**,要测得先给它一个可测缝
   (参照 `DeepSeekProbe` 把纯解析拆成 `Parse` 的做法),**这属于可测性重构,不是行为变更**。
2. `DeepSeekProbe` 的网络分支(超时 / 401 / 5xx / DNS 失败)——
   `HttpMessageHandler` 已经可注入,照着补,**绝不真调 DeepSeek**。
3. 核对摘要文案在**所有**分支下都不含 URL、路径、密钥
   (已有一个用例覆盖部分分支,补全)。

时间盒 1.5 小时。

### T7 — 残留凭据(只报告,不删)

`%TEMP%\cc-connect-pilot-config\` 是试点残留,**含明文生产 app_secret 与 API key**。
桌面 `AI Resume 旧代码归档\ClaudeResume-运行时备份-20260807.zip` 同样含明文。

任务:确认它们还在不在、有没有被复制到别处、有没有进过 git 历史。
**只报告位置与命中的键名(不得给出完整值),删除由用户决定。**

时间盒 30 分钟。

### T8 — Agent 切换的真实验证(**需要用户配合,不要自己做**)

控制面能切 agent(本机只装了 claudecode / codex,其余已隐藏),
切换写进 shadow 配置,但**生效需要重新生成 cc-connect 配置并重启 cc-connect**
——重启是红线 §1.1。

任务只做**只读准备**:写一份逐步操作单交给用户,含每步的期望现象与回滚方法。
不要自己执行。

时间盒 20 分钟。

---

## 3. 卡住怎么办

- 同一个测试连续修 3 次仍红 → 保留红的状态,记录两种假设,进下一条;
- 需要重启 cc-connect 才能验证的 → **跳过**,记进「需要人工执行」;
- 需要在飞书/微信里发消息才能验证的 → **跳过**,只有 owner 能做;
- 需要改变产品行为、架构或数据兼容性的 → **停手**,写成方案选项,不要自行决定;
- 发现本任务书与现场对不上 → 以现场为准,并在报告里点明任务书哪里错了。

---

## 4. 交付格式

写进 `docs/SMOKE-REPORT-<日期>.md` 并提交(**不要 push**),必须包含:

1. **一句话结论**:哪些收口了,哪些没有;
2. **逐项表**:T1–T8 完成/部分/未开始 + 提交 sha + 证据位置;
3. **测试数变化**:533 → N,新增用例分别钉住了什么;
4. **发现的缺陷**:分「已修」「已确认未修」「疑似待判断」三档,各给复现路径;
5. **我没做到什么**——时间盒截断的、跳过的、无法验证的。
   **这一节写得越诚实越有用**;最常见的失败模式是报告说全做完、实际一半没验证;
6. **不变量核对**:cc-connect PID 前后一致?`config.toml` SHA256 未变?
   `git status` 干净?红线有没有触碰过?通知源三项是否仍为启用?
