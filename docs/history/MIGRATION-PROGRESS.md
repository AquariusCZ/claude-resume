# AI Resume v2 迁移进度总览

> 当前状态: **v2 迁移已完成**, `main` 为现役 C# 实现。本文保留迁移结论和剩余验证项;现役机制以 `README.md`、`docs/ARCHITECTURE.md` 和 `AI_GUIDE.md` 为准。

## 1. 终态

```text
Stage  0    1    2    3    4    5    6    7    8    9    10   11
       ██   ██   ██   ██   ██   ██   ██   ██   ██   ██   ██   ██
       完成 完成 完成 完成 完成 完成 完成 完成 完成 完成 完成 完成
```

- 现役代码全部在 `csharp/`。
- v1 PowerShell + Node 运行时已于 2026-08-08 从工作树删除;需要取证时从 Git 历史查阅。
- 版本唯一真身是 `csharp/Directory.Build.props` 的 `<Version>`，当前为 `2.0.0`。
- 生产运行时位于 `%LOCALAPPDATA%\AI Resume\`，源码构建不会自动热更新安装副本。

## 2. 最终产品边界

AI Resume 只保留四项自有职责:

1. Claude 限额后的项目队列与自动续跑。
2. 从 agent 历史和 Git 根目录生成的项目索引。
3. Claude Code / Codex / Cline / Qoder / OpenCode 的本地完成通知。
4. Windows WPF + WebView2 控制面。

飞书/微信协议、会话、agent turn 和聊天定时任务交给直接运行的 cc-connect。通用飞书 OpenAPI 能力交给 lark-cli 与官方 `lark-*` Skills。

## 3. 关键迁移结果

| 领域 | 终态 |
|---|---|
| 控制面 | WPF + WebView2，首帧不依赖额度 I/O，已有离屏截图通道 |
| 状态 | SQLite/WAL schema v5，含 run/event/outbox/process/product/quota 状态 |
| 额度 | Anthropic OAuth usage 主路径 + Claude CLI 降级，稀疏观测按账号/scope/reset 代次合并 |
| 完成通知 | 5 种真实 hook 协议，持久意图、钩子健康、队列重试和七天去重 |
| cc-connect | 候选 TOML 经上游解析器验证，原子提交，通过管理 API 自重启并校验新代次 |
| provider/model | 遵循上游大小写、last-wins、inline table 封闭和用户列表所有权 |
| 安装 | GUI/Worker/Hook 同步 staging、备份、激活校验与失败回滚 |

## 4. 当前门禁

- 全量 xUnit: **972 通过，0 跳过**。
- Release build: **0 warning，0 error**。
- 通知: `notify list` 五源均为 `可送达=True`，`feishu-check` 为 `code=0`。
- 额度:OAuth 真实返回、Fable scoped 行、reset-only 无定值状态、SQLite 并发与安装哈希已验证。
- GUI:左右 CRT 等高，已知 0%/100%/百分比未知分开呈现，刷新圆环无独立红色端点。

## 5. 剩余工作不再是“迁移”

- 观测一次真实账号的“限额 → reset → 按队列续跑”端到端周期。
- 完成 24 小时 Worker/cc-connect 长稳 soak。
- 按需对五个客户端做真实 AI 任务的人工通知验收;自动测试不会为此修改真实会话。
- cc-connect 升级时重跑锁定的 Windows 能力和重启场景。

## 6. 文档真身

1. 用户安装与使用:`README.md` / `README.zh-CN.md`。
2. 现役机制与配置:`docs/ARCHITECTURE.md`。
3. AI 问答导览:`AI_GUIDE.md`。
4. 额度协议:`docs/CLAUDE-QUOTA-ACQUISITION.md`。
5. 通知协议:`docs/COMPLETION-NOTIFICATIONS.md`。
6. 历史迁移证据:`docs/STAGE-*`、`docs/design/` 与 Git 历史。
