# P4 文档一致性对照表(S10-O,2026-08-06 夜)

原则:代码和运行时是真身,文档是影子;冲突时改文档,绝不反过来改代码迁就文档。
不确定的一律列「待人工裁决」。

| # | 漂移 | 改了什么 | 依据(代码/实测) |
|---|---|---|---|
| 1 | FEISHU-BOT-GUIDE「底部三个菜单按钮」描述的现象已不存在 | 引用块改为「菜单**已在飞书开放平台删除**(2026-08-06)」,删除原因(菜单事件不带 chat_id → `230001`)留档 | 任务书明示用户已删菜单;原句「建议在开放平台把菜单删掉」已执行完毕 |
| 2 | STAGE-10-SMOKE-PLAN A6/A7/A8 依赖 `cc-connect send` 驱动 agent,与 STAGE-4-SPEC §7.1 矛盾 | A6 行标注「计划缺陷(2026-08-06 重跑跳过)」,改用 Web 聊天桥/cron exec 注入;A6 边界注同步;§0.5 新增第 4 条 | STAGE-4-SPEC §7.1 S4-B 发现 3:「send 仅以 bot 身份外发消息(不驱动 agent),API 创建的消息不触发 im.message.receive_v1」;S4-D 对照清单 2:可替代途径为 cron exec / Web 桥 |
| 3 | STAGE-11-GATE §2 进程 PID(node 19076 / wscript 18972)失效 | 进程行标「已失效」并注明随 Stage 10 切换退役、退役清理时须重新实测;§1 的 19076 表述改为历史现场 | 2026-08-06 实测:`Get-CimInstance Win32_Process`(node/wscript/cc-connect)只剩 cc-connect 1 个,无 node/wscript |
| 4 | 全仓把 `claude-resume-migration` 当 cc-connect 项目名的残留 | 代码侧已由 `a120410` 修复(`CutoverConfigCommand.ProjectName = "ai-resume"`,`CcConnectProjectIdentityTests.项目名是固定常量` 钉住);文档侧把 SMOKE-PLAN 的靶子项目名/判据/收尾从 `_smoke-cutover` 同步为 `ai-resume`,并注明不得用仓库目录名 | `~/.cc-connect/config.toml` 实测 `name = "ai-resume"`;grep 全仓无其他把目录名当 `[[projects]]` 名的地方(仅历史注释提及漂移过程) |
| 5 | STAGE-10-SMOKE-PLAN 缺两条实测教训 | §0.5 新增第 3 条(构建产物在 `net10.0-windows` 不是 `net10.0`,用错目录拿到无 `preflight` 的陈旧 exe)与第 4 条(send 不驱动 agent 属计划缺陷) | 2026-08-06 冒烟 A 段重跑实测(上一会话交付) |

## 主动多查的部分(任务书要求「不要只查这些」)

- `FEISHU-BOT-GUIDE.md` 其余段落:交互方式描述与 cc-connect 现状一致,无其它漂移。
- `STAGE-10-CUTOVER.md` §基线表里的「现役 node agent 在跑(PID 19076/19024)」:
  该文是**切换当日的现场快照**(冻结文档),描述的是切换前时刻的真身,
  按「历史文档记录历史现场」原则**不改**;失效 PID 的现役指引职责已由
  STAGE-11-GATE 的修订承担。

## 待人工裁决

- STAGE-11-GATE §2 表里「计划任务 ClaudeResumeChecker / 注册表启动项 / 启动文件夹
  快捷方式」三行是否仍存在于当前系统,本夜未逐项实测核验(只读红线内可查,
  但涉及注册表/计划任务的核验超出 P4 时间盒,且与本条漂移无关);
  退役清理执行时应重新实测整表,不得沿用表内任何旧值。
- NEXT-STEPS.md / MIGRATION-PROGRESS.md 等规划类文档含大量切换前表述,
  属计划演进而非现役描述,未逐条同步;如需整体刷新建议另起 neat-freak 收尾任务。
