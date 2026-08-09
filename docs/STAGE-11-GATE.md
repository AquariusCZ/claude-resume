# Stage 11 收尾门禁(历史)

> 历史收尾记录。仓库内 v1 PowerShell + Node 运行时已在后续提交 `2584139` 删除，Stage 11 已完成;当前终态见 `docs/MIGRATION-PROGRESS.md`。

## 1. 运行时退役(2026-08-07 执行完毕)

切换后确认 cc-connect 稳定承担生产,现役 PowerShell + Node 系统已停用并删除。

| 项 | 处置 | 复核 |
|---|---|---|
| 计划任务 `ClaudeResumeChecker` | 已 `Unregister-ScheduledTask` | 查询返回空 |
| 启动项 `ClaudeResumeFeishu.lnk` | 已删除 | 启动文件夹内不存在 |
| 桌面 `AI Resume.lnk` | **就地覆盖**为新控制面 | target = `AiResume.Gui.exe` |
| 用户级 Stop 钩子(`completion-notify.js`) | 已从 `~/.claude/settings.json` 移除 | 全文不含 `completion-notify` |
| `%LOCALAPPDATA%\ClaudeResume\` | 先打包备份再删除 | 目录不存在 |
| node / wscript 进程 | 0 | CIM 复核 |

**备份**:`%LOCALAPPDATA%\ClaudeResume-legacy-backup-20260807.zip`(4.47 MB,973 条目;
删除前抽查 `config.json` / `feishu-runtime.js` / `picker.ps1` / `checker.ps1` / `state.json` 尺寸吻合)。
`~/.claude/settings.json` 另存 `%LOCALAPPDATA%\claude-settings-backup-20260807.json`。

> ⚠️ 备份里的 `config.json` 含**明文**飞书 app_secret 与各家 API key。
> 它只在本机 `%LOCALAPPDATA%`,不进仓库、不进云同步目录。确认不再需要回滚后应删除。

## 2. 一处必须纠正的旧结论

本文件此前把下列三项列为「现役占用面」,**这是错的**——它们属于第三方工具
(CodeZeno 的 ClaudeCodeUsageMonitor,经 winget 安装,自带 LICENSE/README),
与 AI Resume 无关,退役时**不得触碰**:

- 注册表启动项 `HKCU\...\Run :: ClaudeCodeUsageMonitor`
- 启动文件夹 `Claude用量监控.lnk`
- `%LOCALAPPDATA%\ClaudeUsage\` 与桌面 `AI Usage.lnk`

教训与本项目其它几次同类:**清单是听来的,资产归属要查证到指向**。
本次退役前逐项读了快捷方式 target 与注册表值,才发现清单里一半不是我们的。

## 3. 已完成项(2026-08-06)

### 3.1 全仓凭据扫描 ✅

`git grep` 匹配 `(app_secret|appSecret|apiKey|api_key|accessToken|refreshToken|password|secret)`
后接 16 位以上字面量,全仓仅 2 处命中,均为合成测试 fixture:

- `LarkCliInvokerTests.cs:152` — `"fake-live-token-abcdef"`
- `SecretsTests.cs:37` — `"sk-live-0123456789ab"`

真实凭据零进仓库。

### 3.2 明文 secret 收口(D-013)✅

飞书凭据已 `import-feishu` 搬进 DPAPI。`~/.cc-connect/config.toml.bak-before-repair`
(含 1 个 app_secret、9 个 api_key、1 个 token 的明文)已于 2026-08-06 删除。

**仍待清理**:`%TEMP%\cc-connect-pilot-config\`(试点残留),以及 §1 的退役备份。

### 3.3 文档终审 ✅

修正了 S7-I 引入的漂移:限额数据取数以官方 `oauth/usage` 为准,不再声称消费
cc-connect 的 `UsageReport`(它依赖 `creack/pty`,构建约束命中 Windows)。

## 4. 生产现状(退役后)

| | |
|---|---|
| 飞书事件消费者 | cc-connect 单进程,`platform ready` = 配置内平台数 |
| 平台 | feishu + weixin(ilink,扫码绑定),各自 `allow_from` 非空 |
| 项目 | 单个 `ai-resume`,靠 `/dir` 切工作目录 |
| agent | 由控制面选择并写入生成的配置(改后需重启 cc-connect) |
| 控制面 | `AiResume.Gui.exe`,桌面 / 开始菜单入口已指向它 |
| 续跑引擎 | `AiResume.Worker.exe`,开机自启(启动文件夹) |
| 完成通知 | Claude Code Stop hook,**绝对路径** |
| 管理台 | http://localhost:9820 |

## 5. 历史删除清单(后续已完成)

`src/*.js` 11 个 + `src/*.ps1` 5 个:`authorization-policy` `channel-adapter`
`completion-events` `completion-notify` `conversation-store` `feishu-agent`
`feishu-runtime` `install-completion-hooks` `provider-health` `session-manager`
`task-orchestrator`(.js);`checker` `deploy-files` `install` `lib` `picker`(.ps1)。

运行时已退役,这些文件现在是**死代码**。但删除是单独的决定:

- 它们是行为等价性的**参照实现**——`stage1-recorded-equivalence` 一类测试仍以它们为基准;
- 删除后若发现目标实现有语义缺口,只能从 git 历史找回,成本高于留着。

建议:观察期(1-2 周真实使用)结束、确认不再需要回头对照后再删,
删除时连同 `test/` 下只测它们的用例一并处理。
