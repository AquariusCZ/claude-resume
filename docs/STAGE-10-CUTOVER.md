# Stage 10 生产切换执行单(历史)

> 历史执行记录。v2 生产切换已完成;当前安装、切换和回滚契约以 `README.md`、`docs/ARCHITECTURE.md` 和 `CLAUDE.md` 为准。下文保留 2026-08-06 当时的前置条件与风险证据。

切换不可逆(现役 node agent 与 cc-connect 不能同时消费同一个飞书应用)。
本文件是可照做的执行单,不是设计文档。

## 0. 为什么不能由 AI 代劳最后一步

生成 `config.toml` 需要飞书 `app_id` + `app_secret`。写入凭据实值必须由用户自己执行:

- 工具刻意**不接受命令行参数**传凭据——命令行会进入进程列表(本项目自己的
  `CimRunningProcessLister` 就能读到)、PowerShell 历史与各类审计日志;
- 凭据只从**环境变量**读,直接写进 `~/.cc-connect/config.toml`,
  终端回显一律脱敏(`app_secret = "[REDACTED]"`)。

本机 `%TEMP%\cc-connect-pilot-config\config.toml` 含明文生产凭据。
**不要复用它、不要复制它的内容**——它的存在本身就是需要清理的债务(见 D-013)。

## 1. 前置状态核查(2026-08-06 实测)

| 项 | 状态 |
|---|---|
| 现役 node agent | **在跑**(PID 19076 `node feishu-agent.js`,PID 19024 是 VBS 守护起的 cmd wrapper) |
| cc-connect | 已装 v1.4.1 commit `5d4c96dd`(`AppData\Roaming\npm\cc-connect.ps1`) |
| `~/.cc-connect/config.toml` | **未配置**——仍是 `app_id = "your-feishu-app-id"` 的模板 |
| Stage 9 数据迁移 | 演练通过(config 14 字段 / state 10 字段) |
| 单消费者守卫 | 已可核验(D-008 已关闭) |

## 2. 执行步骤

### 2.1 迁移自有状态(可先做,幂等)

```bash
AiResume.Worker.exe migrate --dry-run
```

确认报告无 `failed` 后去掉 `--dry-run` 实跑。原文件只备份不删,备份落
`%LOCALAPPDATA%\ClaudeResumeShadow\migration-backup\<时间戳>\`。

### 2.2 写入 cc-connect 配置 【只能由用户执行】

在**用户自己的 PowerShell 会话**里(凭据不经过 AI、不进历史文件):

```bash
$env:FEISHU_APP_ID = '<你的 app_id>'; $env:FEISHU_APP_SECRET = '<你的 app_secret>'; AiResume.Worker.exe cutover-config
```

项目清单取 shadow 配置里已布防的 `selected`,为空则退回项目发现结果。
写完立刻清掉环境变量:`$env:FEISHU_APP_SECRET = $null`。

### 2.3 停现役

只杀命令行含当前 AppDir `feishu-agent.js` 的 node 进程,**并连带停掉 VBS 守护**
(否则它约 8 秒内会把 node 重新拉起来,切换当场变成双消费者)。

### 2.4 确认唯一消费者 【硬门禁】

```bash
AiResume.Worker.exe preflight
```

**必须返回 `Clear` 且退出码 0**。返回 `Conflict` 说明还有消费者在跑;
返回 `Unverifiable` 说明枚举器读不到命令行——两种情况都不得继续。

### 2.5 启动新链路

```bash
cc-connect --config "$env:USERPROFILE\.cc-connect\config.toml"
```

### 2.6 冒烟 【需真人在飞书里操作】

聊天 / 查询 / 修改 / 停止 / 完成通知 / 重启恢复,六项逐一验证。
**任一失败即回滚**:停 cc-connect → 重启现役 node agent(VBS 守护会自动拉起)。

## 3. 尚未验证的风险(切换前必须知道)

Stage 6 的兼容性验证**从未在独立测试应用上完整跑通**——`MIGRATION-BASELINE.md` §5
要求的门禁顺序(阶段 3/4/6 + 阶段 8 通知链 + 阶段 9 对账)只完成了阶段 9。
因此 2.6 的冒烟是**第一次**真实验证 cc-connect 能否承担飞书协议,不是复核。

具体未知项:cc-connect 的授权模型(`isAdmin` 只门禁特权命令,**没有**"非 owner 禁文件工具"
这层安全红线,见 ADR-0003 §4 修订)在生产上的实际表现;`allow_from` 未设置时的
授权缺口(既有待办)。切换后若发现越权读取,应立即回滚。
