# AI Resume

[English](README.md) | **简体中文**

[![Platform](https://img.shields.io/badge/platform-Windows-2F8A56)](#安装)
[![.NET](https://img.shields.io/badge/.NET-10-512BD4)](https://dotnet.microsoft.com/)
[![License](https://img.shields.io/badge/license-MIT-6B665B)](LICENSE)

> 凌晨两点，AI 撞上额度上限停住了。AI Resume 把活儿排进队列，等额度恢复后自己接着跑，不用你守着。

面向 Claude Code、Codex、Cline、Qoder 和 OpenCode 的本地 Windows 控制面。没有自己的云端后台——界面、队列、状态、凭据和钩子全部留在你的机器上。

![AI Resume 控制面](docs/assets/panel.png)

## 它做什么

| | |
|---|---|
| **限额后自动续跑** | Claude Code 被限额时扣住项目队列，等额度恢复被证实后按顺序接着跑。 |
| **自己找项目** | 从 agent 历史和 Git 根目录建持久化索引，不用手工维护清单。 |
| **该打扰你时才打扰** | 任务跑完发一条；AI 停下来问你话时也发一条。 |
| **只说能被证伪的话** | 额度、队列、provider 健康和凭据——只有真请求成功过才点绿。 |

## 安装

需要 Windows 10/11、[.NET 10 SDK](https://dotnet.microsoft.com/download) 和 [Claude Code CLI](https://claude.com/claude-code)。

```powershell
git clone https://github.com/AquariusCZ/claude-resume.git
cd claude-resume
dotnet build csharp\AiResume.sln -c Release
csharp\src\AiResume.Worker\bin\Release\net10.0-windows\AiResume.Worker.exe install
```

从桌面或开始菜单打开 **AI Resume**。

`install` 就是部署这一步：真正运行的是 `%LOCALAPPDATA%\AI Resume\` 下的副本而不是 `bin\Release`，所以每次重新构建后都要再跑一次。它会暂存并校验产物，等新 Worker 在管道上应答之后才去改任何入口；回滚不完整时保留恢复材料。它同时装好登录自启，**不会弹控制台窗口**——想再要"挂了自动重启"，见[开机自启](docs/ARCHITECTURE.md#logon-autostart)里那个需要提权一次的计划任务升级。

卸载会保留你的设置和数据：

```powershell
& "$env:LOCALAPPDATA\AI Resume\AiResume.Worker.exe" uninstall
```

可选：`npm i -g cc-connect@1.4.1` 用于手机聊天；Codex CLI 或 DeepSeek 凭据用于其它 provider。

## 通知

分两类，因为它们的紧迫程度根本不同。**跑完了**可以晚点看；**在等你**是此刻正在白白流走的时间。

| 来源 | 跑完了 | 在等你 |
|---|---|---|
| Claude Code | `Stop` 钩子 | `Notification`——需要输入、弹出确认框 |
| OpenCode | `session.idle` 插件 | `permission.asked` |
| Codex | `notify` 回调 | — |
| Cline | `TaskComplete` | — |
| Qoder | `hooks.Stop` | — |

适配器合并进你已有的配置，且只移除能证明属于自己的条目。AI Resume 自己的探测和续跑运行都带内部标记，不会拿它自己的活儿来通知你。

安装时已经在运行的 Codex Desktop 不会加载后写入的 `notify`，装完请重启它。协议与冒烟步骤见[完成通知](docs/COMPLETION-NOTIFICATIONS.md)。

## 额度与续跑

额度取自 Anthropic 的 OAuth usage 接口，复用 Claude Code 的真实请求形状。AI Resume 只读现有令牌，**绝不刷新、绝不写回**。

响应是稀疏的，所以缺字段不等于被删除。承接下来的旧值显示为琥珀色的*最近服务端读数*，绝不点绿；只有重置时间没有百分比时用不确定态扫描，而不是编一个数字出来。

先选择续跑模型，再勾选项目、点**布防**、关窗即可。Worker 只有在同一目标模型的实时 scoped 额度明确可用后才续跑，并用同一个显式 `--model` 启动 Claude Code；5H 单独重置不等于可以续跑。细节见[额度获取](docs/CLAUDE-QUOTA-ACQUISITION.md)。

## 用手机指挥

配好 cc-connect 后，在飞书或微信里操作：

| 命令 | 作用 |
|---|---|
| `/dir <路径>` 或 `/dir <序号>` | 切换项目 |
| `/mode plan` · `/mode auto-edit` | 只读规划或允许编辑 |
| `/model switch <名称>` · `/provider switch <名称>` | 切换模型或 API provider |
| `/new` · `/list` · `/switch <序号>` | 管理会话 |
| `/stop` | 停止当前任务 |
| `/cron` · `/timer` | 定时任务 |

三个最容易被混为一谈的概念——**agent** 是本地执行器、会话的归属者（Claude Code、Codex）；**provider** 是它用的远端地址和凭据；**model** 是发给该 provider 的模型标识。所以你完全可以用 Claude Code 这个 agent 去跑 DeepSeek。

控制面会先生成候选 cc-connect 配置，用 cc-connect 自己的解析器校验，原子提交，再核验确实换了新的进程代次。它绝不会仅凭退出码就报成功。

## 结构

```text
飞书 / 微信
       |
       v
  cc-connect ---------> Claude Code / Codex / 其它 agent
       |                            |
       |                            v
       +---------------------- AiResume.Hook
                                    |
                                    v
AiResume.Gui <---- 命名管道 ------ AiResume.Worker
   WPF + WebView2                  队列、额度、发现、
                                   通知、进程监督
```

状态存在 `%LOCALAPPDATA%\AI Resume\state\`；cc-connect 自己的配置和会话在 `%USERPROFILE%\.cc-connect\`。

代码给自己定的三条硬规矩：

- **绿色代表已验证。** CLI 装着、key 填了都不算证据——必须有一次请求真的成功过。
- **只能有一个飞书消费者。** 两个长连接消费者会把事件随机分走，所以预检失败关闭。
- **不靠 PID 杀进程。** 身份、启动时间和命令签名全部对上才允许回收。

## 开发

```powershell
dotnet test csharp\AiResume.sln
dotnet build csharp\AiResume.sln -c Release --no-restore -warnaserror
```

测试使用临时状态、合成会话和注入的 runner，绝不续跑真实会话、不改真实项目、不发付费模型请求。

## 文档

[架构](docs/ARCHITECTURE.md) · [额度获取](docs/CLAUDE-QUOTA-ACQUISITION.md) · [通知协议](docs/COMPLETION-NOTIFICATIONS.md) · [运行契约](docs/RUN-CONTRACT.md) · [工程教训](docs/LESSONS.md) · [AI 导览](AI_GUIDE.md)

## 许可

MIT，见 [LICENSE](LICENSE)。界面使用[方舟像素字体](https://github.com/TakWolf/ark-pixel-font)，遵循 SIL OFL 1.1（[OFL.txt](csharp/src/AiResume.Gui/wwwroot/fonts/OFL.txt)）。
