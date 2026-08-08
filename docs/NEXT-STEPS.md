# 后续执行计划(接续用)

> **本文是上下文压缩后的接续入口。** 新会话开始时按 §1 恢复现场,按 §3 顺序推进。
> 写于 2026-08-06,HEAD `3a9a454`(+ 未提交的 skill/文档改动),分支 `s2-external`,未推远端。

## 1. 恢复现场(新会话先做这四件事)

1. 读 `docs/MIGRATION-PROGRESS.md` —— 进度总览与已达成的硬指标;
2. 读 `docs/adr/0003-cc-connect-direct-and-control-plane.md` —— **当前方向真身**,与 ADR-0001 冲突处以它为准;
3. 跑 `powershell -NoProfile -ExecutionPolicy Bypass -File csharp\build.ps1` 确认基线
   (应为 **292 测试全绿 / 0 警告 / secrets gate 0 命中**);
4. `git log --oneline -8` 与 `git status --short` 确认工作区状态。

## 2. 工作方式(已与用户约定,不必再问)

- **实现一律委托 deepseek**(skill:`deepseek-developer`),监督者只做架构、规格、审查、集成、测试。
  调用要点:绝对路径传 `-SpecFile`/`-Files`;`run_in_background`;默认 `-ReasoningEffort none`;
  一批 3-5 个并行(瓶颈是审查带宽不是 API)。
- **中途不汇报**,一个工作包打完再一次性总结。
- 每个工作包:规格 → 委托 → 审查(逐条对红线)→ 应用 → `build.ps1` 全绿 → 单一 commit。
- **worker 产出无自证效力**:编译通过 ≠ 逻辑正确,必须实跑测试。已知它会
  ①照抄参考实现里的身份常量 ②不了解被测代码的隐含行为 ③忠实实现错误的规格前提。

## 3. 剩余工作(按建议顺序)

### S7-C 额度潮汐轴接真实数据 【下一个】

GUI 上「额度潮汐」现为占位。数据源按 ADR-0003 §2.3 = cc-connect 的 `UsageReport`
(`core/interfaces.go:484` `UsageReporter`;字段 `Buckets`/`Windows{UsedPercent,ResetAfterSeconds,ResetAt,LimitReached}`)。

- 先查证取数通道:cc-connect 管理 API 是否暴露 usage(S4 实证管理 API 无正式文档,需实探端点);
  若无 HTTP 端点,退而经 bridge 发 `/usage` 聊天命令解析回复(S4 已证 bridge 可用)。
- `ControlPlaneBridge` 加 `quota.get`;前端把 `tideUsed` 宽度、起止时刻、倒计时接上。
- **拿不到就如实显示「不可用」,不得伪造进度**(现役 GUI 服务状态红线同理)。

### S7-D Provider 状态与布防交互

- Provider 三行(Claude/OpenAI/DeepSeek)现为「待接入」。同样优先消费 cc-connect 侧数据,
  **不自研探测**(S5-B 已因重复实现被作废,勿重蹈)。
- 项目多选 + 「布防所选项目」「预演」「立即续跑」按钮接真实动作;
  当前按钮为 `disabled`,接入前不得放开。

### S7-E 会话管理入口

现役 GUI 有「会话」窗(14/30 天规则、工作会话仅手动)。按 ADR-0003,会话生命周期已交还 cc-connect,
**先确认哪些仍属 AI Resume 职责**再实现,避免又一次重复上游。

### Stage 6 重定义(原 wrapper 路线已作废)

- 重写 `docs/STAGE-6-SPEC.md`:目标从「wrapper 接管」改为「直接运行 cc-connect 并验证可用性」;
- `AiResume.Wrapper` 按 ADR-0003 §4 处置:`SessionBridge` 删除,`AuthMapper`/`RunMapper` 缩减,
  `Supervisor`/`ConfigGenerator` 保留并简化;
- **`CcConnectSupervisor` 需新增职责**:启动 cc-connect 前确认现役 node agent 已停止
  (飞书长连接是集群模式、随机分流,两个消费者在线会导致事件被随机截走——见 D-015)。

### Stage 9 数据迁移演练 【已完成,规格见 `docs/design/s9-migration-spec.md`】

范围已缩小(会话状态交还 cc-connect)。只迁移 AI Resume 自有状态:
续跑周期、项目清单/隐藏项、完成通知去重记录。要求:幂等、可对账(数量/哈希)、原文件只备份不删。

**实现**:`ProductStateMigrator` + `AiResume.Worker.exe migrate [--dry-run] [--force]`。

**收敛结果**——三项里只有两项可迁:

| 源 | 结论 |
|---|---|
| `config.json` 的 14 个自有字段 | 迁移(白名单;33 个非自有键含全部凭据,**不读取其值、报告里连键名都不列**) |
| `state.json` → `CheckerState` | 迁移(注意现役同一文件里 Unix 秒整数与 ISO 字符串两种时间表示并存) |
| `completion-events-seen.json` | **不迁移**——两侧 eventId 算法完全不同(SHA1/40 位/带 `claude:` 前缀 vs SHA256/16 位/无前缀),93 条历史键在新系统里一条也不会命中。新 hook 只对切换后的新事件触发、不回放历史,故不存在重复通知风险。 |

对真实现役状态的演练实测(2026-08-06):config 14 字段、state 10 字段、跳过非自有键 33 个、
丢弃现役独有字段 3 个(`targetId`/`targetEndUtc`/`firedForId`),dry-run 未产生任何 shadow 产物。

### Stage 10 生产切换 【需用户在场】

用户已停用现役、允许直接在生产改测,但**切换时机仍须用户确认**(不可逆)。
顺序:停现役 node → 启新链路 → 确认唯一消费者 → 冒烟(聊天/查询/修改/停止/通知/重启)→ 失败即回滚。

### Stage 11 收尾

删除失去职责的 Node/PowerShell 模块;残留进程/计划任务/启动项清零;
全仓凭据扫描;文档终审;完整回归。

## 4. 红线(任何工作包都适用)

- 凭据实值零进仓库/日志/测试输出/commit;不读生产 AppDir `config.json`。
- **同一飞书应用同一时刻只能有一个消费者在线**(故障风险,非合规问题)。
- 不破坏用户既有 hook/配置;`Enable` 合并写入、`Disable` 只移除自己的。
- 测试禁止触碰真实用户目录(`~/.claude`、`~/.codex`、`~/.qoder`、`~/.config/opencode`、shadow 目录)。
- 破坏性或不可逆操作前须用户确认。

## 5. 已知坑(踩过,别再踩)

| 坑 | 表现 | 对策 |
|---|---|---|
| DeepSeek 推理吃预算 | 16384 tokens 全烧在 62420 字符推理上,content 为空却照常扣费 | `-ReasoningEffort none`(已是默认) |
| PowerShell 工具 2 分钟超时 | 长调用被杀,钱花了没产出 | `run_in_background`;skill 已改流式边收边落盘 |
| 相对路径 | PowerShell 工具工作目录不在项目根,`-SpecFile` 找不到 | 一律绝对路径 |
| 代理 | v2rayN 是 `127.0.0.1:10808/10809`,**不是** Clash 的 7897 | 先直连,失败再挂代理(见全局 AGENTS.md) |
| 全角/半角标点 | 仓库内文档标点风格不统一,`Edit` 精确匹配失败 | 先 `grep` 取原文再编辑 |
| 批量替换 | 用脚本替换字符串会误伤方法体内的自调用(曾造成无限递归) | 优先 `Edit` 精确改;批量替换后必须复查 |

## 6. 未决/待用户决定

- **`cli_xxxxxxxxxxxxxxxx`** 测试应用用户称将关停,无需处理(D-015 已降级 P2)。
- **D-013** 仅剩用户在开放平台重置生产 app secret 后关闭。
- Stage 10 切换时机。
