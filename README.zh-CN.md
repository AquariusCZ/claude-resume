# AI Resume

[English](README.md) | **简体中文**

[![平台](https://img.shields.io/badge/platform-Windows-2F8A56)](#环境要求)
[![.NET](https://img.shields.io/badge/.NET-10-512BD4)](https://dotnet.microsoft.com/)
[![许可](https://img.shields.io/badge/license-MIT-6B665B)](LICENSE)

> 本地 Windows AI 编码控制面。Claude Code 触发额度限制时持有项目队列，窗口重置后按顺序续跑；同时负责项目发现和整个 agent 任务的完成通知。

AI Resume 没有自己的云服务。GUI、队列、状态、凭据和钩子都在本机；agent 请求只发往你自己配置的 provider。

![AI Resume 控制面](docs/assets/panel.png)

## 它解决什么

AI Resume 只拥有四项职责：

| 能力 | 保证什么 |
|---|---|
| **限额后续跑** | Claude Code 被限额时持久化项目队列，重置后按顺序续跑。 |
| **项目发现** | 从 agent 历史与 Git 根目录生成持久索引，不维护硬编码项目名单。 |
| **完成通知** | Claude Code、Codex、Cline、Qoder 或 OpenCode 跑完整个任务时，飞书只收到一条通知。 |
| **Windows 控制面** | 展示额度证据、续跑队列、provider 健康、agent 选择、凭据和通知送达状态。 |

聊天平台、会话、agent turn 和聊天定时任务交给 [cc-connect](https://github.com/chenhg5/cc-connect)。AI Resume 适配上游，不重写它。

## 快速开始

### 环境要求

- Windows 10 / 11
- [.NET 10 SDK](https://dotnet.microsoft.com/download)
- [Claude Code CLI](https://claude.com/claude-code)：`npm i -g @anthropic-ai/claude-code`
- 手机聊天可选：`npm i -g cc-connect@1.4.1`
- 其它可选 agent/provider：Codex CLI、DeepSeek API 凭据

### 构建与安装

```powershell
git clone https://github.com/AquariusCZ/claude-resume.git
cd claude-resume
dotnet build csharp\AiResume.sln -c Release
csharp\src\AiResume.Worker\bin\Release\net10.0-windows\AiResume.Worker.exe install
```

在桌面或开始菜单打开 **AI Resume**。重新构建后要再跑一次 `install`；真正运行的是 `%LOCALAPPDATA%\AI Resume\` 中的安装副本，不是 `bin\Release`。

安装器只接受空目录、仅含保留状态的目录、与本次 payload 精确匹配的旧运行时、现役 AI Resume 安装根，或上次卸载留下的精确 preserved-root marker；真正卸载仍必须同时验证现役 marker 与 payload 清单。安装先 staging、备份并删除旧清单已淘汰的文件，整体替换 GUI / Worker / Hook；新 Worker 通过 Named Pipe 身份校验后才改快捷方式和通知钩子。从安装目录执行卸载时会启动受清单约束的临时 Worker，在返回成功前把全部 payload 事务性移动到私有退役区，因此立即重装也不会被旧清理误删。无结果、坏结果或 helper 异常退出时父进程只报告并保留恢复目录，不会删除唯一退役副本。状态和未知文件保留，preserved-root marker 只授权后续重装、不授权删除。回滚不完整时返回非零并保留恢复材料。

卸载但保留用户状态：

```powershell
& "$env:LOCALAPPDATA\AI Resume\AiResume.Worker.exe" uninstall
```

## 核心工作流

### 额度与自动续跑

主数据源是 Anthropic OAuth 用量端点，请求形状与 Claude Code 一致。AI Resume 只读现有 token，绝不刷新或写回。现代 `session`、`weekly_all` 与全部 `weekly_scoped` 都会解析，Fable 等 scoped 限额单独显示。

额度响应是稀疏观测，“本次没返回”不等于“已删除”。只有同账号、同 scope、同一未过期 reset 代次才能承接最近读数，并以琥珀色**最近服务端读数**标记。已知 `0%` 是空轨道，已知 `100%` 是满条，只有 reset 时用无定值扫描，绝不伪造百分比。

勾选项目后按**布防**即可关闭窗口。Worker 持有队列，只在 reset 证据有效后续跑。

完整协议：[Claude 额度获取与验证](docs/CLAUDE-QUOTA-ACQUISITION.md)。

### 通过 cc-connect 用手机聊天

cc-connect 配好后，在飞书或微信中使用：

| 命令 | 用途 |
|---|---|
| `/dir <路径>` 或 `/dir <序号>` | 切换项目/工作目录 |
| `/mode plan` / `/mode auto-edit` | 选择只读规划或允许编辑 |
| `/model switch <名称>` | 切换模型 |
| `/provider switch <名称>` | 切换 API provider |
| `/new`、`/list`、`/switch <序号>` | 管理会话 |
| `/stop` | 停止当前任务 |
| `/cron`、`/timer` | 管理聊天定时任务 |

Agent、provider 和 model 是三层不同的东西：

- **Agent**：本地执行体和会话所有者，如 Claude Code 或 Codex。
- **Provider**：agent 调用的远端端点与凭据。
- **Model**：发往 provider 的模型标识。

控制面会生成候选 cc-connect 配置，交给 cc-connect 自己的解析器验证，原子提交后请求带认证的自重启，最后校验新进程代次。它不会只凭 CLI 退出码宣布成功。Provider/model 保留规则和 Codex 模型目录见 [架构文档](docs/ARCHITECTURE.md#agent-provider-and-model-semantics)。

### 完成通知

| 来源 | 已验证的任务完成边界 |
|---|---|
| Claude Code | `Stop` hook |
| Codex | `notify` callback |
| Cline | `TaskComplete` |
| Qoder | `hooks.Stop` |
| OpenCode | `session.idle` plugin |

适配器会合并用户现有配置，只移除能证明属于 AI Resume 的条目。内部探测和续跑会直接设置 `AI_RESUME_INTERNAL_RUN=1`；生成的 cc-connect 项目通过 `projects.agent.options.env` 设置同一标记，计划任务启动脚本再提供 daemon 级兜底，因此从飞书启动的 agent 不会把 AI Resume 自己的工作重复通知回来。

协议与冒烟步骤：[完成通知](docs/COMPLETION-NOTIFICATIONS.md)。

## 安全边界

- **绿灯必须是真实验证。** provider 只有在真实请求成功后才可用；命令已安装或 key 已填写都不是证据。
- **飞书只允许一个消费者。** 两个长连接会随机分流事件，预检发现冲突时 fail-closed。
- **凭据不进 Git。** 飞书凭据存在仓库外，并用当前 Windows 用户的 DPAPI 加密。
- **绝不只凭 PID 结束进程。** 父进程身份、启动时间和命令签名必须同时匹配。
- **agent 工作不设客户端总时限。** 静默是指标，不是失败判据。
- **用户停止是终态。** 已取消任务不会换 provider 重放。

## 架构

```text
飞书 / 微信
      |
      v
 cc-connect ----------> Claude Code / Codex / 其它 agent
      |                              |
      |                              v
      +------------------------- AiResume.Hook
                                      |
                                      v
AiResume.Gui <----- Named Pipe ----- AiResume.Worker
 WPF + WebView2                     队列、额度、项目发现、
                                    通知与进程监督
```

AI Resume 状态在 `%LOCALAPPDATA%\AI Resume\state\`；cc-connect 的配置与会话在 `%USERPROFILE%\.cc-connect\`。详细所有权与数据流见 [架构文档](docs/ARCHITECTURE.md) 和 [AI 导览](AI_GUIDE.md)。

## 验证状态

当前 Windows 安装上已验证：

- 隔离的全量 xUnit 与警告当错误的 Release 构建；
- OAuth 额度解析、稀疏连续性、scoped/Fable 行与 SQLite 并发；
- 左右额度面板等高、reset-only 无定值状态与减少动态效果；
- 五种 Hook 协议经队列、Worker、lark-cli 到飞书；
- cc-connect 候选解析、原子激活与绑定进程代次的重启验证；
- 安装目录 GUI / Worker / Hook 哈希与 Release 构建一致。

仍未验证的边界也直接写明：

- 真实账号的“限额 → 窗口重置 → 自动续跑”尚未完成一次端到端观测；
- 五个通知源是协议级冒烟，不是为了测试而各启动一次真实 AI 任务；
- 长时稳定性只有短采样基线，还没有完成 24 小时 soak。

## 开发

```powershell
dotnet test csharp\AiResume.sln
dotnet build csharp\AiResume.sln -c Release --no-restore -warnaserror
```

测试使用临时状态、合成会话和注入的进程/API runner，不会 resume 真实会话、修改真实项目或发付费模型请求。

## 文档

- [English README](README.md)
- [架构与完整配置](docs/ARCHITECTURE.md)
- [Claude 额度获取](docs/CLAUDE-QUOTA-ACQUISITION.md)
- [完成通知协议](docs/COMPLETION-NOTIFICATIONS.md)
- [运行生命周期契约](docs/RUN-CONTRACT.md)
- [上游研究](docs/UPSTREAM-ARCHITECTURE-RESEARCH.md)
- [工程教训](docs/LESSONS.md)
- [面向 AI 的仓库导览](AI_GUIDE.md)

## 许可

MIT，见 [LICENSE](LICENSE)。界面使用 [方舟像素字体](https://github.com/TakWolf/ark-pixel-font)，依 SIL Open Font License 1.1 分发；许可证见 [OFL.txt](csharp/src/AiResume.Gui/wwwroot/fonts/OFL.txt)。
