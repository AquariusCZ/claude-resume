# Stage 10 切换冒烟测试计划

冻结于 2026-08-06 切换当日。**执行者可以是另一个 AI**,但 A/B 两段的边界不可越过。

## 0. 现场基线(执行前先核对,不符就停)

| 项 | 期望 |
|---|---|
| cc-connect | 单进程在跑,`~/.cc-connect/config.toml` 已生成 |
| 现役 node agent / VBS 守护 | **不在跑**(`Get-CimInstance Win32_Process -Filter "Name='node.exe' OR Name='wscript.exe'"` 为空) |
| 单消费者预检 | `AiResume.Worker.exe preflight` → 结论行为「唯一的消费者是 cc-connect 本身」。**切换后这是正常状态,退出码为 1 也算通过**;只有出现 `legacy-node-agent` 才是真冲突 |
| 授权 | cc-connect 日志**不含** `allow_from is not set` / `admin_from is not set` |
| 靶子项目 | cc-connect 配置里本仓库对应的 `[[projects]]` 名为 **`ai-resume`**(提交 `a120410` 后固定,不再是 `_smoke-cutover`,也不得用仓库目录名 `claude-resume-migration`);`CutoverConfigCommand.ProjectName` 常量与 `CcConnectProjectIdentityTests` 钉住此名 |

**回滚(任何一项失败即执行)**:

```bash
Get-Process cc-connect -ErrorAction SilentlyContinue | Stop-Process -Force; Start-Process wscript.exe -ArgumentList '"C:\Users\<you>\AppData\Local\ClaudeResume\feishu-launch.vbs"'
```

## 0.5 执行者必读:两个会导致误判的已知情况

1. **测试与生产 Worker 已隔离,不需要再停 Worker。**
   `PowerLossRecoveryTests` 拉起的宿主此前会和本机生产 Worker 抢同一把
   单实例互斥体(由 Named Pipe 名派生)而起不来,表现为两个用例超时 30 秒失败。
   已修复:测试经 `AIRESUME_TEST_PIPE_SUFFIX` 注入 GUID 后缀,与生产名互不干扰;
   生产仍是固定的 SID 派生名,第二个生产实例照样被拒绝。
   **基线是 425 全绿**(实测:生产 Worker 运行中跑全量,全绿且 Worker 未受影响)。

2. **不要在 cc-connect 的 admin 页(9820)增删项目。**
   项目列表由 AI Resume 的 `cutover-config` 生成,admin 页的改动下次重跑就被覆盖。
   增删一律走 AI Resume 控制面的续跑队列(`×` 移除 /「添加项目」),
   然后「生成 cc-connect 配置」并重启 cc-connect。

3. **构建产物在 `net10.0-windows` 不是 `net10.0`**(2026-08-06 实测教训)。
   用错目录会拿到不含 `preflight` 分支的陈旧 exe,冒烟判据直接失真。

4. **`cc-connect send` 不驱动 agent,属本计划 A6-A8 的设计缺陷**(2026-08-06 重跑证实,
   依据 STAGE-4-SPEC §7.1 S4-B 发现 3:send 仅以 bot 身份外发消息,
   API 创建的消息不触发 `im.message.receive_v1`)。可脚本化驱动 agent 的
   唯一注入途径是 Web 聊天桥(bridge 9810)或 cron exec;见 §2 A6-A8 行标注。

## 1. 红线(执行者必须遵守,违反即中止)

1. **「修改项目」只能对 `ai-resume` 这一个靶子项目做,且指令必须是 owner 明确批准的单点修改。** 绝不对任何真实项目发会写文件的指令。
   本项目有过实事故:一个测试 resume 了真实会话,AI 带着旧上下文执行并 push 了 commit;
   "事后停止"拦不住已经发生的写入。
2. **不得放宽 `allow_from`**。它现在锁死在 owner 的 open_id;为了让自动化能发消息而改它,
   等于把刚补上的安全缺口重新打开。
3. **不得读取或打印** `~/.cc-connect/config.toml` 的 `app_secret` 与 `[management]` 的 `token`、
   `%LOCALAPPDATA%\ClaudeResume\config.json` 的任何凭据字段、
   `~/.claude/.credentials.json` 的 token。日志里出现即算失败。
4. 不得 `git push`、不得改动 `ai-resume` 冒烟指令明确范围以外的任何仓库。

## 2. A 段:可全自动执行(不需要人在飞书里操作)

执行者逐条跑,记录「通过 / 失败 + 证据」。

| # | 项 | 操作 | 通过判据 |
|---|---|---|---|
| A1 | 进程存活 | `Get-CimInstance Win32_Process -Filter "Name='cc-connect.exe'"` | 恰好 1 个 |
| A2 | 单消费者 | `AiResume.Worker.exe preflight` | 冲突列表里**只有 `cc-connect`、没有 `legacy-node-agent`**,且结论行说明这是切换后的正常状态。**不要用退出码判定**——它回答的是"能不能再启一个",切换后答案本就是不能 |
| A3 | 配置形状 | 读 `~/.cc-connect/config.toml`(**只看结构,不看 secret**) | 每个 `[[projects]]` 都有 `admin_from`;每个 `[projects.platforms.options]` 都有 `allow_from` |
| A4 | 项目就绪 | 日志 `platform ready` 计数 | **等于 `~/.cc-connect/config.toml` 里 `[[projects]]` 的实际条数**(项目数随控制面增删变化,不要写死) |
| A5 | 会话存储 | `cc-connect sessions list` | 命令成功返回(可以为空) |
| A6 | agent 直驱 | ⚠️ **计划缺陷(2026-08-06 重跑跳过)**:原文用 `cc-connect send -p ai-resume` 驱动,但 send 仅 bot 外发不驱动 agent(STAGE-4-SPEC §7.1)。改用 Web 聊天桥(bridge 9810)或 cron exec 注入「把冒烟文件改一行」的单点指令 | 命令被接受;随后靶子文件内容变更 |
| A7 | 写入确实发生 | `git -C <靶子工作目录> diff --stat` | 显示靶子文件有改动 |
| A8 | 完成通知落队 | 检查 `%LOCALAPPDATA%\ClaudeResumeShadow\events\` | A6 任务结束后出现新的事件 json(证明 Stop hook 触发) |
| A9 | 额度取数 | `AiResume.Worker.exe`(GUI 或直接调) → 观察 5h/7d | 两个窗口都有 `utilization`,来源标注「服务端下发」 |
| A10 | 重启恢复 | 停 cc-connect → 重启 → 看日志 | 重新 `platform ready`,无 `error`;A5 的会话仍在 |
| A11 | 崩溃不留残 | `Get-CimInstance Win32_Process` | 无孤儿 `claude.exe` / `node.exe` |
| A12 | 凭据零泄漏 | 全量 grep 本轮所有日志与输出 | 不含 app_secret / apiKey / token 实值 |
| A13 | **admin 页可用** | `Invoke-WebRequest http://localhost:9820/` | HTTP 200。**这一项被我们打断过一次**:`cutover-config` 曾整份覆盖配置、抹掉 `[management]`,admin 页直接消失。重跑生成器后必须复检 |
| A14 | **配置未被覆盖** | 重跑 `cutover-config` 后读 `~/.cc-connect/config.toml` | `[management]` 仍在且只出现一次;`[[projects]]` 数量等于项目数、无累积 |

> **A6 的边界**:无论经 Web 桥还是 cron exec,验证的都是「cc-connect → Claude Code → 文件」这一段,
> **不覆盖飞书入站事件路由**。所以它不能替代 B 段,只能先把下游打通、缩小 B 段失败时的排查面。
> 原计划的 `cc-connect send` 途径已作废(send 不驱动 agent,见 §0.5 第 4 条)。

## 3. B 段:必须由 owner 本人在飞书里操作(6 项)

原因:`allow_from` 只放行 owner 的 open_id。自动化要么冒充身份,要么放宽授权——两者都不接受。

执行者的角色是:**每项由 owner 发消息,执行者负责验证可观测证据并记录**。

| # | 场景 | owner 发什么 | 期望现象 | 执行者验证什么 |
|---|---|---|---|---|
| B1 | 闲聊 | 对机器人说「你好,现在几点」 | 收到回复 | 日志出现该 session 的 turn;无 error |
| B2 | 只读查询 | 「`ai-resume` 里靶子文件现在是什么内容」 | 回复内容正确 | `git diff` 仍无新增改动(查询不写文件) |
| B3 | 修改项目 | 「把 `ai-resume` 的靶子文件改成指定内容」 | 回复完成 | 文件确为指定内容;**其余所有项目 `git status` 无变化** |
| B4 | 停止 | 发一个长任务后立刻发 `/stop` | 任务中断 | 日志显示 cancel;无孤儿 claude 进程残留 |
| B5 | 完成通知 | 等 B3 的任务自然结束 | 本机弹出/记录完成通知 | shadow `events/` 有对应事件且未重复投递 |
| B6 | 重启恢复 | 重启 cc-connect 后再发一条消息 | 仍能正常回复 | 会话历史未丢;`sessions show` 能看到 B1–B3 |

**非授权用户验证(可选但推荐)**:让另一个飞书账号给机器人发消息,期望**没有任何响应**。
这是 `allow_from` 真正生效的唯一直接证据。

## 4. 判定与交付

- A 段全过 + B 段全过 → 切换成功,进入观察期,之后才可执行 `STAGE-11-GATE.md` 的删除清单;
- **任一项失败 → 立即回滚**(§0 的命令),记录失败项与日志片段,不要在生产上反复试;
- 交付物:逐项「通过/失败 + 证据(命令输出或日志行)」的表格,以及是否回滚的结论。

## 5. 收尾

冒烟通过后清理靶子改动:把 A6/B3 写入的冒烟改动 `git checkout` 还原(靶子项目现为
本仓库自身的 `ai-resume` 条目,不再存在独立的 `_smoke-cutover` 靶子目录;
历史版本的「删除靶子目录 + 从配置移除」流程随 `a120410` 项目名固定而废止)。
