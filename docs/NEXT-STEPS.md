# AI Resume 后续工作

> v2 迁移已完成。本文只列当前产品验证与发布工作，不再保留已完成的 Stage 待办。

## 1. 新会话恢复现场

1. 读 `CLAUDE.md`。
2. 读 `README.zh-CN.md`、`docs/ARCHITECTURE.md` 和 `AI_GUIDE.md`。
3. 检查 `git status --short`、当前分支、远端与最近提交。
4. 运行:

```powershell
dotnet test csharp\AiResume.sln --no-restore
dotnet build csharp\AiResume.sln -c Release --no-restore -warnaserror
```

## 2. 优先级

### P1 真实限额重置 E2E

目标:在不人工改写额度状态的前提下，完整观测一次:

```text
布防 → 真实限额 → 等待 reset → 项目 1 续跑 → 项目 2 续跑 → 解除/继续布防
```

验收证据:额度快照、cycleId、runKey、子进程登记、终态、通知和 GUI 呈现的时间线一致。用户取消不得 fallback 或重放。

### P1 24 小时长稳 soak

目标:确认 Worker、SQLite WAL、额度刷新、cc-connect 守护和通知队列在长时间运行下没有资源泄漏或代次漂移。

最低观测:进程数/PID 代次、内存、句柄、数据库体积、日志速率、额度请求频率、通知队列长度和 cc-connect 单消费者证据。

### P2 真客户端通知验收

自动回归已覆盖五种真实 Hook 协议到飞书。若需要更高的产品信心，可由用户在五个客户端各执行一个无副作用短任务，验收产品级完成边界。

### P2 发布与升级体验

- 为 GitHub Release 生成可验证的 Windows 安装包或自包含发布产物。
- 在 release note 中区分“已验证”与“待真机观测”。
- cc-connect 升级前固定新上游 commit，重跑 Windows PTY、provider/model、管理 API、S4U 自重启和单消费者门禁。

## 3. 不重新打开的边界

- 不在 AI Resume 重写 cc-connect 的聊天协议、会话、agent turn 或 cron。
- 不因为上游有同名接口就假设 Windows 上可用;先验证平台能力。
- 不把未下发的额度百分比估算成 0%/100%。
- 不为了测试通知而 resume 真实会话或修改真实项目。
- 不在规则文件里硬编码频繁变动的测试数量。

## 4. 取证入口

- 产品状态:`README.md` 与 `docs/MIGRATION-PROGRESS.md`。
- 现役机制:`docs/ARCHITECTURE.md`。
- 额度:`docs/CLAUDE-QUOTA-ACQUISITION.md`。
- 通知:`docs/COMPLETION-NOTIFICATIONS.md`。
- 上游依据:`docs/UPSTREAM-ARCHITECTURE-RESEARCH.md`。
- 历史过程:`docs/STAGE-*`、`docs/design/`、`docs/evidence/` 和 Git 历史。
