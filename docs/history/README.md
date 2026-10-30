# 历史文档(只读归档)

这里的文件是 2026-08 v1 → v2 迁移期的计划、阶段门禁、审计记录、设计稿与 soak 证据。
它们描述的是**当时**的状态与决策,不是现役运行说明;现役机制以仓库根 `CLAUDE.md`、`docs/ARCHITECTURE.md`、
`docs/CLAUDE-QUOTA-ACQUISITION.md`、`docs/COMPLETION-NOTIFICATIONS.md` 与 `docs/adr/` 为准(ADR-0005 记录了 2026-10-12 的 v2.1 重构)。

| 目录 / 文件 | 内容 |
|---|---|
| `STAGE-*.md` | 迁移阶段规格与门禁(Stage 1–11) |
| `MIGRATION-BASELINE.md` / `MIGRATION-PROGRESS.md` / `MIGRATION-DEBT.md` | 迁移基线、完成状态与债务编号 |
| `STATE-OWNERSHIP.md` / `RECOVERY-AUDIT-20260801.md` | current/shadow/target 所有权与恢复审计 |
| `AUDIT-*.md` / `FINAL-AUDIT-PLAN.md` / `RED-LINE-COVERAGE.md` / `REMAINING-SMOKE-PLAN.md` | 审计与冒烟计划 |
| `OVERNIGHT-*.md` / `P4-DOC-SYNC-TABLE.md` / `P5-CREDENTIAL-AUDIT-20260807.md` / `NEXT-STEPS.md` | 夜间加固简报、文档同步表、凭据审计、当时的后续路线 |
| `design/` | S7–S10 设计规格、GUI 原型与截图 |
| `evidence/` | 2026-08-06/07 soak 脚本与结论 |

引用这些文件时请注明「历史」。其中提到的 `CcConnectSessionBridge`、`CcConnectAuthMapper`、`CcConnectSupervisor`、
`CodexNotificationAdapter`、`CodexProbe`、`ProductStateMigrator` 与 `migrate` / `import-feishu` / `sync-dirs` / `cutover-config` 命令
均已在 v2.1(2026-10-12)删除。
