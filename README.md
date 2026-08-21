# AI Resume

**English** | [简体中文](README.zh-CN.md)

[![Platform](https://img.shields.io/badge/platform-Windows-2F8A56)](#install)
[![.NET](https://img.shields.io/badge/.NET-10-512BD4)](https://dotnet.microsoft.com/)
[![License](https://img.shields.io/badge/license-MIT-6B665B)](LICENSE)

> Your AI coding agent hits a usage limit at 2am. AI Resume queues the work, waits for the reset, and picks it up without you.

A local Windows control plane for Claude Code, Codex, Cline, Qoder and OpenCode. No cloud backend of its own — the GUI, queue, state, credentials and hooks all stay on your machine.

![AI Resume control panel](docs/assets/panel.png)

## What it does

| | |
|---|---|
| **Resumes after a limit** | Holds a project queue while Claude Code is limited, then resumes in order once the reset is proven. |
| **Finds your projects** | Builds a persisted index from agent history and Git roots — no hand-maintained list. |
| **Tells you when it matters** | One message when a task finishes, and one when the agent stops to ask you something. |
| **Shows real evidence** | Quota, queue, provider health and credentials — green only after a request actually succeeded. |

## Install

Windows 10/11, [.NET 10 SDK](https://dotnet.microsoft.com/download), and [Claude Code CLI](https://claude.com/claude-code).

```powershell
git clone https://github.com/AquariusCZ/claude-resume.git
cd claude-resume
dotnet build csharp\AiResume.sln -c Release
csharp\src\AiResume.Worker\bin\Release\net10.0-windows\AiResume.Worker.exe install
```

Open **AI Resume** from the Desktop or Start menu.

`install` is the deploy step: the runtime is the installed copy under `%LOCALAPPDATA%\AI Resume\`, not `bin\Release`, so re-run it after every rebuild. It stages and verifies the payload, waits for the new Worker to answer on its pipe before touching any entry point, and keeps recovery material if a rollback is incomplete. It also registers a logon autostart that starts the engine **without a console window** — see [autostart](docs/ARCHITECTURE.md#logon-autostart) for the elevated scheduled-task upgrade that adds restart-on-failure.

Uninstall keeps your settings and state:

```powershell
& "$env:LOCALAPPDATA\AI Resume\AiResume.Worker.exe" uninstall
```

Optional: `npm i -g cc-connect@1.4.1` for phone chat; Codex CLI or DeepSeek credentials for other providers.

## Notifications

Two kinds, because they are not equally urgent. **Finished** can wait; **waiting for you** is time you are losing right now.

| Source | Finished | Waiting for you |
|---|---|---|
| Claude Code | `Stop` hook | `Notification` — agent needs input, confirmation dialog |
| OpenCode | `session.idle` plugin | `permission.asked` |
| Codex | `notify` callback | — |
| Cline | `TaskComplete` | — |
| Qoder | `hooks.Stop` | — |

Adapters merge into your existing configuration and remove only entries they can prove they own. AI Resume's own probes and resume runs are marked internal, so they never notify you about AI Resume's own work.

A Codex Desktop process that was already running will not pick up a later `notify` write — restart it after install. Protocol and smoke procedure: [Completion notifications](docs/COMPLETION-NOTIFICATIONS.md).

## Quota and resume

Quota comes from Anthropic's OAuth usage endpoint using Claude Code's real request shape. AI Resume reads the existing token and **never refreshes or writes it**.

Responses are sparse, so a missing field is not treated as a deletion. A carried-over value is shown amber as a *recent server reading*, never green. Reset-only data uses an indeterminate scan instead of inventing a percentage.

Select the resume model, select projects, press **Arm**, then close the window. The Worker resumes only when a fresh scoped quota reading for that same model is available, and launches Claude Code with the same explicit `--model`. A five-hour reset alone is not enough. Details: [Claude quota acquisition](docs/CLAUDE-QUOTA-ACQUISITION.md).

## Chat from your phone

With cc-connect configured, drive the agent from Feishu or WeChat:

| Command | Purpose |
|---|---|
| `/dir <path>` or `/dir <n>` | Switch project |
| `/mode plan` · `/mode auto-edit` | Read-only planning or editing |
| `/model switch <name>` · `/provider switch <name>` | Switch model or API provider |
| `/new` · `/list` · `/switch <n>` | Manage conversations |
| `/stop` | Stop the active task |
| `/cron` · `/timer` | Scheduled chat tasks |

Three things people conflate — **agent** is the local executor that owns the session (Claude Code, Codex); **provider** is the remote endpoint and credential it uses; **model** is the identifier sent to that provider. You can run the Claude Code agent against a DeepSeek provider.

The control panel stages a candidate cc-connect config, validates it with cc-connect's own parser, commits atomically, and verifies a new process generation. It never reports success from an exit code alone.

## How it fits together

```text
Feishu / WeChat
       |
       v
  cc-connect ---------> Claude Code / Codex / other agents
       |                            |
       |                            v
       +---------------------- AiResume.Hook
                                    |
                                    v
AiResume.Gui <---- Named Pipe ---- AiResume.Worker
   WPF + WebView2                  queue, quota, discovery,
                                   notifications, supervision
```

State lives in `%LOCALAPPDATA%\AI Resume\state\`; cc-connect keeps its own config and sessions in `%USERPROFILE%\.cc-connect\`.

Three rules the code holds itself to:

- **Green means verified.** An installed CLI or a filled-in key is not proof — a request has to have succeeded.
- **One Feishu consumer.** Two long-connection consumers split events randomly, so preflight fails closed.
- **No PID-only termination.** Identity, start time and command signature must all match before a process is reclaimed.

## Development

```powershell
dotnet test csharp\AiResume.sln
dotnet build csharp\AiResume.sln -c Release --no-restore -warnaserror
```

The suite uses temporary state, synthetic sessions and injected runners. It never resumes a real session, modifies a real project or makes a paid model call.

## Documentation

[Architecture](docs/ARCHITECTURE.md) · [Quota acquisition](docs/CLAUDE-QUOTA-ACQUISITION.md) · [Notification protocol](docs/COMPLETION-NOTIFICATIONS.md) · [Run contract](docs/RUN-CONTRACT.md) · [Engineering lessons](docs/LESSONS.md) · [AI guide](AI_GUIDE.md)

## License

MIT — see [LICENSE](LICENSE). The interface uses [Ark Pixel Font](https://github.com/TakWolf/ark-pixel-font) under SIL OFL 1.1 ([OFL.txt](csharp/src/AiResume.Gui/wwwroot/fonts/OFL.txt)).
