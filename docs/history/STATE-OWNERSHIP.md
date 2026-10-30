# AI Resume 状态所有权(STATE-OWNERSHIP)

> 历史快照:本文冻结的是 2026-08-01 迁移设计中的 current/shadow/target 所有权。迁移完成后的现役所有权以 `docs/ARCHITECTURE.md` 为准;下文的 PowerShell/Node “current” 不是当前运行态。

> 状态:阶段 0 交付物,2026-08-01 定稿并由 ADR-0002 补充。本文定义现役(current)/影子(shadow)/目标(target)三类状态的真身、迁移方式与回滚真身;核心原则:**任意时刻每类状态只有一个 writer,禁止双写**。目标运行状态字段与恢复语义以 `RUN-CONTRACT.md` 为准。

## 1. 术语

- **current(现役真身)**:2026-08-01 实际运行的状态文件与写入者(JSON/marker/PowerShell+Node)。
- **shadow(影子)**:迁移试点期间在独立测试应用 + 测试项目 + 隔离目录中存在的状态,与生产物理隔离;只用于验证,不承载生产语义。
- **target(目标真身)**:迁移完成后唯一允许写入的状态所在(.NET 10 C# Worker + SQLite/WAL、DPAPI/Windows Credential Manager、Named Pipe 控制通道、cc-connect 编排、C# completion outbox)。
- **迁移方式**:某类状态从 current 到 target 的既定路径(快照+一次性导入 / 冻结+切换 / wrapper 替换 / hook 替换等)。
- **回滚真身**:切换前保留的、可恢复该类状态的权威副本(JSON 快照、导出文件、git 标记等)。

## 2. 真身矩阵

| 状态类别 | 现役 writer(current) | 影子(shadow,仅测试) | 目标 writer(target) | 迁移方式 | 回滚真身 |
|---|---|---|---|---|---|
| 产品配置与密钥(`config.json`) | GUI `picker.ps1`/Node 共用 `.write.lock` 锁内增量写 | 测试应用独立配置(隔离目录) | C# Worker:SQLite+WAL 产品状态真身;长期密钥 DPAPI/Windows Credential Manager | 锁内导出→校验→一次性导入;导入前冻结写入 | 切换前 config.json 加密快照(密钥不回仓库) |
| 布防/额度/续跑周期(`state.json`,`armCycleId`/`cycleId`) | `checker.ps1` | 测试项目独立 state | C# Worker(SQLite) | 冻结+一次性导入,周期标识重签 | 导出快照;Node 侧旧文件保留至阶段 11 |
| 后台续跑子进程登记(`checker-ai-child.json`) | `lib.ps1`/`checker.ps1` | 测试环境登记表 | C# `ProcessSupervisor` + Windows Job Object + `RunStore` | RunContract wrapper 替换;登记格式迁移 | 旧登记文件 + fail-closed 校验保留 |
| 飞书 AI 运行登记(`feishu-ai-children.json`) | 生产 AppDir 自 2026-08-04 起由 `feishu-runtime.js` 写入;`task-orchestrator.js` 只接管现役兼容 runKey 预占/fallback/停止决策,D-001 的 matched/mismatched/unverifiable、非法元数据与写盘失败锁保留已随本次部署上线 | 测试环境登记表 | C# `TaskOrchestrator` + `ProcessSupervisor` + `RunStore`(stop barrier + settle-once) | Start/Status/Cancel 接管;运行键迁移 | 旧登记文件 + fail-closed 校验保留 |
| 飞书会话/闲聊/查询(`feishu-sessions.json`、`feishu-userchats.json`、`feishu-query\*.started`) | 生产 AppDir 自 2026-08-04 起由 Stage 1 的 `conversation-store.js` 写入,入口不再内联持有 | 测试应用独立会话存储 | cc-connect 会话编排 + C# 产品状态真身 | 一次性切换;测试门禁通过前不迁移生产 | 切换前导出;Node 侧文件保留至阶段 11 |
| 飞书在飞任务(`feishu-inflight.json`) | `feishu-agent.js` | 测试环境运行登记 | C# `RunStore` RunSnapshot/Event + `TaskOrchestrator` | 阶段 5 shadow-read,阶段 6 以 Start/Status/Cancel 一次性接管 | 切换前导出;Node 侧文件保留至阶段 11 |
| 本地完成事件(`completion-events\`、`completion-events-seen.json`) | `completion-notify.js` 写 / `feishu-agent.js` 处理 | 测试目录事件队列 | C# 单文件 hook 写入 + C# Worker completion outbox(SQLite) | hook 替换 + outbox 接管;入队格式版本化 | 队列快照;旧队列保留至新 outbox 追平 |
| 会话归档(`session-archive.json`、`session-archive\`) | `session-manager.js` | 测试归档 | C# Worker / 保留文件(阶段定稿) | 快照+引用迁移 | 归档目录备份 |
| 图片暂存(`feishu-in\`、`feishu-out\`) | `feishu-agent.js` | 测试暂存 | C# Worker 与 cc-connect 边界定稿 | 目录语义保留,归属迁移 | 暂存目录备份 |
| 飞书 tenant token(`feishu-token.json`) | `feishu-agent.js` | 测试应用独立凭据缓存 | 不迁入 SQLite;由切换后的唯一消费者按上游契约重新获取并短期缓存。长期 app 凭据由 C# 机密存储保管并受控注入 | 阶段 10 停旧消费者后废弃旧 token,新消费者重新获取 | 无需迁移 token;仅保留不含明文的审计记录 |
| 项目目录与发现结果(项目列表、custom/hidden) | `lib.ps1` + Node 动态发现 | 测试项目列表 | C# Worker 动态项目发现 → `ProjectCatalogBridge` 确定性生成 cc-connect 配置 | 发现逻辑迁移;项目数量不写死 | 配置快照 |
| 日志(`logs\`) | 各现役进程 | 测试日志 | C# Worker 结构化日志(阶段定稿) | 格式演进,保留本地时间 | 日志文件轮转备份 |
| 飞书消息/卡片通道(长连接、发消息) | 生产 AppDir 自 2026-08-04 起由 `channel-adapter.js` 持有长连接并仍是唯一生产消费者;入口不再直接依赖 SDK | 独立测试应用 | cc-connect(唯一生产消费者) | 全部门禁→一次性切换 | 切换快照;Node agent 进程停用不删除 |
| Agent 原生会话(Codex thread / Claude JSONL) | 生产 AppDir 由 provider 自有 + `session-manager.js`/`ai/runners.js` 引用,自 2026-08-04 起经 `ai/agent-adapter.js` 单 attempt 边界接入 | 测试原生会话 | provider 原生 + cc-connect 编排 | 引用迁移,不动原生数据 | provider 原生数据 + 导出索引 |

## 3. 单一 writer 规则(禁止双写)

1. 任意时刻,上表每一类状态**只有一个 writer**:当前为「现役 writer」列,迁移后为「目标 writer」列,测试期「影子」列只存在于隔离环境。
2. 禁止 Node agent 与 cc-connect 同时消费**同一个生产飞书应用**;禁止两边同时写同一会话/任务状态;cc-connect 只能先用独立测试应用 + 测试项目验证。
3. 迁移方式中的「冻结+切换」:导入前冻结旧 writer 的写入(停止消费/解除布防),导入校验通过后再放行新 writer;不存在「两边同时写一段窗口」。
4. 配置类写入即使迁移后,也必须保持「锁/事务内读最新值、只改本类字段、fsync+原子替换/事务提交」语义(现役 `.write.lock` 规则在 SQLite+WAL 下由事务等价继承)。
5. 任何「影子变生产」「双写」「整体回退」的意图都先走 ADR 由用户确认,不得静默改变方向。

## 4. 影子状态隔离

- 独立飞书测试应用(不同 appId/appSecret)与独立测试项目;凭据只放隔离测试环境,不写仓库。
- 测试 AppDir/数据目录与生产 `%LOCALAPPDATA%\ClaudeResume` 物理隔离;测试绝不读写生产 config.json 或生产会话。
- 测试产生的会话/事件/登记在测试目录内,退出即清理(沿用现役 `FEISHU_TEST` 隔离思想,从阶段 2 的 C# 骨架起等价实现,阶段 3/4 试点强制使用)。

## 5. 一次性切换与回滚

- 切换前置条件:独立测试应用通过阶段 3/4/6 及阶段 8 的通知链门禁,阶段 9 数据对账通过,回滚真身完整备份,生产消费者确认唯一。
- 切换动作:停用 Node agent 生产消费(进程停用,不删除);cc-connect 成为唯一生产消费者;状态导入并校验计数。
- 回滚条件:切换后门禁回归失败且无法快速修复时,恢复 Node 消费与回滚真身;回滚本身也必须是单 writer(先停新消费者再起旧消费者)。
- 阶段 11 前,回滚真身(JSON 快照、导出、旧登记)一律保留;阶段 11 退役由用户确认后进行。

## 6. 更新机制

- 本矩阵随 ADR 与阶段门禁更新;改动必须同步 `docs/MIGRATION-BASELINE.md` 与 `docs/adr/0001-target-architecture.md`,并由用户确认。
- 「目标 writer」列的 C# 基础协议按 `RUN-CONTRACT.md` 在阶段 2 建立,SQLite 状态映射与 DPAPI/CM 条目在阶段 5 定稿,会话/任务所有权在阶段 6 定稿;每次阶段门禁后回写本文。
