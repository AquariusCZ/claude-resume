# AI Resume

**English** | [简体中文](README.zh-CN.md)

[![Platform](https://img.shields.io/badge/platform-Windows-2F8A56)](#install)
[![.NET](https://img.shields.io/badge/.NET-10-512BD4)](https://dotnet.microsoft.com/)
[![License](https://img.shields.io/badge/license-MIT-6B665B)](LICENSE)

> Your AI coding agent hits a usage limit at 2am. AI Resume queues the work, waits for the reset, and picks it up without you.

A local Windows control plane for Claude Code and OpenCode: it resumes Claude Code sessions that a usage limit cut off, and sends you a Feishu message when a task finishes, stops on an error or waits for you. No cloud backend of its own — the GUI, queue, state and hooks all stay on your machine, and Feishu only delivers the notifications. Codex, Cline and Qoder support stays available behind the **视图** (View) menu.

![AI Resume control panel](docs/assets/panel.png)

## What it does

| | |
|---|---|
| **Resumes after a limit** | When a usage limit cuts off a Claude Code session, it resumes that same session once the reset is proven — judged per project, each with its own model, one at a time. |
| **Finds your projects** | Lists projects from Claude Code's session history; add or hide folders by hand. No hand-maintained list. |
| **Tells you when it matters** | One Feishu message when a task finishes, stops on an error, or waits for your decision — from Claude Code and OpenCode. |
| **Shows real evidence** | Quota, login, queue and delivery — green only after a request or a delivery actually succeeded. |

## Install

Windows 10/11, the [.NET 10 SDK](https://dotnet.microsoft.com/download), and [Claude Code](https://claude.com/claude-code) 2.1.139 or newer with `claude.exe` on `PATH`, logged in with your Claude account. Feishu notifications also need Node.js and the official [lark-cli](https://github.com/larksuite/cli) (`npm i -g @larksuite/cli`; verified against 1.0.81).

```powershell
git clone https://github.com/AquariusCZ/claude-resume.git
cd claude-resume
dotnet build csharp\AiResume.sln -c Release
csharp\src\AiResume.Worker\bin\Release\net10.0-windows\AiResume.Worker.exe install
```

Open **AI Resume** from the Desktop or Start menu, then set up the two blocks on the right: **Claude 登录** ([Claude Code login](#claude-code-login)) and **完成通知 → 飞书** ([Feishu notification settings](#feishu-notification-settings)). Secondary options live in the menu bar — **视图** / **工具** / **帮助** (View / Tools / Help; Alt+V/T/H, F10), and F5 refreshes quota and status.

`install` is the deploy step: the runtime is the installed copy under `%LOCALAPPDATA%\AI Resume\`, not `bin\Release`, so re-run it after every rebuild and reopen the panel — an open window does not hot-reload. It stages and verifies the payload, waits for the new Worker to answer on its pipe before touching any entry point, re-applies the notification sources you had enabled, and keeps recovery material if a rollback is incomplete. Exit code 0 means everything is in place; 2 means some notification sources could not be aligned, 3 that the new Worker did not come up (the previous version was restored), 4 that the rollback was incomplete. It also registers a logon autostart that starts the engine **without a console window** — see [autostart](docs/ARCHITECTURE.md#logon-autostart) for the elevated scheduled-task upgrade (`scripts\register-autostart.ps1`) that adds restart-on-failure.

Uninstall removes the shortcuts and AI Resume's own hook entries, and keeps your settings and state, including the saved Feishu bot credential:

```powershell
& "$env:LOCALAPPDATA\AI Resume\AiResume.Worker.exe" uninstall
```

Optional: OpenCode 2.x for its notifications and DeepSeek balance; Codex CLI for Codex quota and resume.

## Claude Code login

AI Resume holds no Claude credential of its own. It reads Claude Code's login — `%USERPROFILE%\.claude\.credentials.json`, or the same file under `CLAUDE_CONFIG_DIR` — to query quota, and **never refreshes or writes it back**: the refresh token is single-use, so a refresh by any other process would log Claude Code out. The account identity comes from `oauthAccount` in Claude Code's global config (normally `%USERPROFILE%\.claude.json`), so it no longer drifts when the access token rotates.

The access token lasts about 8 hours, and Claude Code renews it whenever it runs — a terminal, Desktop or VS Code session, or a Remote Control connection. The login behind it lasts about 30 days, and only `/login` renews it. **Claude 登录** tells you where you stand:

| Panel | Meaning | What to do |
|---|---|---|
| **有效** | Logged in, access token valid | — |
| **有效 · 账号未核实** | No consistent `oauthAccount` yet; resume waits | Open Claude Code once |
| **令牌待续期** | Access token expired, login still valid | Open Claude Code once, or keep Remote Control connected |
| **即将到期** | Login expires within 3 days | Run `claude`, then `/login` |
| **已过期** / **未登录** | Login expired or cleared, or no login at all | Run `claude`, then `/login` |
| **凭据位置变化** | Claude Code moved its login into Windows Credential Manager (`CLAUDE_CODE_FORCE_WINDOWS_CREDMAN=1` or a server-side rollout), which AI Resume cannot read | Quota stays unverified |

With a Feishu recipient set, the Worker reminds you 3 days, 2 days, 1 day and 12 hours before the login expires, and once more when it has. A resume run starts only while the access token has at least 45 minutes left; otherwise that project waits for Claude Code to renew it.

The `claude setup-token` long-lived token of v2.1 is gone: the usage endpoint rejects it (its scope covers inference only), so it never read quota. `install` deletes the leftover `state\secrets\claude-oauth-token.bin`.

## Feishu notification settings

Feishu does one thing now: your bot sends you a direct message through the official lark-cli. There is no Feishu chat, session or allow-list anymore.

1. Install lark-cli: `npm i -g @larksuite/cli`. AI Resume starts the `lark-cli.exe` inside the npm package directly, never through `cmd.exe`, which cut multi-line messages at the first line break.
2. In the Feishu developer console, use a self-built app with the bot capability and permission to send messages. The recipient — you — must be within the app's availability.
3. **工具 › 飞书通知设置…** (or **设置…** under **完成通知 → 飞书**): enter the App ID (`cli_…`) and App Secret, then **保存并验证**. AI Resume runs `lark-cli doctor` online with exactly that pair and saves it only if Feishu issues a bot token. It is kept as a machine-scope DPAPI copy in `state\secrets\feishu-bot.bin` (readable by you, SYSTEM and Administrators) and handed to lark-cli through its official `LARKSUITE_CLI_*` environment variables, because the background Worker can run in session 0, where lark-cli's own keychain is not reliably readable. Without this copy, delivery falls back to that keychain, and until a delivery succeeds the panel says **凭据在钥匙串**. Upgrading from v2.1? **导入旧版凭据** imports the credential saved by the old **飞书接入** section after the same check.
4. Recipient: paste your `ou_…` open_id and **保存**, or **识别** to pick from the users logged in to lark-cli (`lark-cli auth list`). An open_id belongs to one app, so once a bot credential is saved, **识别** lists only users of that same app.
5. **通道体检** runs `lark-cli doctor` with the credential delivery will use; **发送测试** (also **工具 › 发送测试通知**) queues a test message through the full path.

**飞书机器人** shows **可送达** only while the latest real delivery succeeded and nothing has changed since: **后台服务未运行** when the Worker is down, the reason when delivery is blocked — credential, permission or recipient, rate limit, network — **最近投递失败** when the last attempt failed, and **设置已变更** when you changed the credential or recipient after the last success (press **发送测试** to re-check). A failed message is retried on its own backoff without delaying the others. A channel-wide failure pauses the whole channel for a backoff, and the pause lifts at once when you change the credential or the recipient. A message that can never be sent, that happened more than 24 hours ago, or whose outcome stayed unknown for 55 minutes (Feishu only de-duplicates for an hour) moves to `state\completion-events\dead\` with an `.error.txt` beside it.

## Notifications

Three kinds, because they are not equally urgent. **Finished** can wait; **stopped** and **waiting for you** are time you are losing right now.

| Source | Finished | Stopped by an error | Waiting for you |
|---|---|---|---|
| Claude Code | `Stop` hook | `StopFailure` — usage limit, login expired, billing… | `Notification` — permission prompt, MCP elicitation dialog, agent needs input, auto-continue after a limit stalled or disabled |
| OpenCode 2.x | top-level `session.execution.succeeded` with no sub-session still running | top-level `session.execution.failed` not retried within a grace period | `permission.asked`, `form.created` (a question) |

Claude Code hooks go into `%USERPROFILE%\.claude\settings.json` in the official exec form — `command` is the absolute path of `AiResume.Hook.exe`, `args` carries the switches — which needs Claude Code 2.1.139+. Claude Code reloads the file itself, so no new session is needed. The OpenCode plugin is an official v2 directory plugin at `%USERPROFILE%\.config\opencode\plugins\airesume-notify\index.ts`; an older single-file plugin is migrated and a `.bak` kept. The OpenCode row stays **待核实** until OpenCode's own log (`%USERPROFILE%\.local\share\opencode\log\opencode.log`) shows the plugin loaded, and turns red if OpenCode rejected it (the latest log entry wins, so a later successful load clears an earlier failure); restart OpenCode if it stays pending. Both paths follow the clients' own settings: `CLAUDE_CONFIG_DIR` for Claude Code, `XDG_CONFIG_HOME` / `XDG_DATA_HOME` for OpenCode.

Adapters merge into your existing configuration and remove only entries they can prove they own. AI Resume's own resume runs are marked internal, so their hooks never notify you; AI Resume posts its own notices instead — resume started, finished or not confirmed; quota back but the session still open; Claude token expired; account changed; login expiring or expired. Messages from a Claude Code session connected through Remote Control carry **· 远程已连接**.

Codex (`Stop` hook in `hooks.json`), Cline (`TaskComplete`) and Qoder (`hooks.Stop`) remain available — finished only — and are hidden by default: **视图 › 显示其他通知源**, or `notify enable <源>`. A hidden source that is still on or broken leaves a one-line hint on the main panel. Codex sessions that were already open do not reload `hooks.json`; start a new session, and if `/hooks` inside Codex lists the AI Resume hook as untrusted, trust it there once. Protocol and smoke procedure: [Completion notifications](docs/COMPLETION-NOTIFICATIONS.md).

## Quota and resume

Quota comes from Anthropic's OAuth usage endpoint, using Claude Code's real request shape and Claude Code's own login. Responses are sparse, so a missing field is not treated as a deletion. A carried-over value is shown amber as a *recent server reading*, never green. Reset-only data uses an indeterminate scan instead of inventing a percentage. The tiles are fixed: Claude 5 hours, Claude 7 days, Claude per model (the fullest weekly per-model limit, with an **N 项在等** badge when armed projects wait on it) and DeepSeek balance. The balance uses OpenCode's DeepSeek key (its config, then OpenCode 2.x's own credential store `opencode.db`, then the legacy `auth.json`, else `DEEPSEEK_API_KEY`) and is display-only: AI Resume stores no DeepSeek key and never resumes with it. If OpenCode's merged config (later layers override earlier ones) points DeepSeek at a custom endpoint, no balance request is sent. When a refresh fails but earlier readings are carried over, the header says **部分更新** and the tile tooltip names the reason.

Tick the projects to guard. Each ticked row gets a model menu: the default **沿用会话:<model>** follows the model that session last used (shown as ↺ once armed), or pick another per project — several projects can share a model, and then share one quota reading. **预演…** shows each project's current verdict without running anything. **布防** (Arm) pins the provider and account, each project's model ID from the provider's live catalog, and the exact native session; catalog changes apply at the next arm. An open session only produces a warning; a session whose model is not in the catalog needs a manual pick.

While armed, each project is judged on its own, and anything unverified means wait, never go:

- **Interrupted?** The session transcript must end in a usage-limit interruption. Each interruption is resumed at most once; a session you already continued yourself counts as done (**已在客户端继续**).
- **Quota back for its model?** A live reading must show the 5-hour and 7-day windows below 100% and no blocked limit that applies to that model. A weekly per-model limit applies to its own model family (any version; an unrecognised family is assumed to apply), so a full Fable weekly limit no longer holds back an Opus project.
- **Session free?** If the session is open in a client — terminal, Desktop, VS Code or Remote Control — or another session in the same folder is busy, the project waits. Claude Code's own auto-continue handles open interactive sessions; if one is still open and has not continued 10 minutes after the reset, you get one Feishu reminder.

Projects resume one at a time in queue order, each with `claude --resume <session-id> -p continue --model <model> …` in its own folder, and each is re-checked right before it starts. A project that could not start — session just opened, access token under 45 minutes, configuration changed — is skipped and retried next round instead of stopping the cycle. A failure after the process started is never replayed: the cycle stops until you disarm and arm again. One-shot (the default) disarms once every project is done; **工具 › 连续模式** resumes after every interruption and takes effect at the next arm. Hover a row's status for the full reason and the expected start time.

Codex is hidden until **视图 › 显示 Codex**, which adds its tile and offers it as the resume provider. It uses your official ChatGPT or OpenAI login from `~/.codex/auth.json` — a local bridge configured in Codex is not used. Codex has no transcript check: a project is queued when its model is observed limited, and resumes when every account window is below 100%, no additional limit or spend control is hit, and the target model is not marked unavailable. Other configured providers (a non-local third-party Codex provider, or Claude Code pointed at an API endpoint) need a verified positive balance. Missing quota data stays unverified, a provider is never switched automatically, and a fixed model ID does not freeze a provider's backend revision. See [provider-bound resume](docs/adr/0004-provider-bound-resume.md) and [ADR-0006](docs/adr/0006-retire-cc-connect-per-project-resume.md).

## From your phone

AI Resume no longer has a Feishu chat bot: cc-connect was retired in v2.2 ([ADR-0006](docs/adr/0006-retire-cc-connect-per-project-resume.md)), and Feishu carries notifications only. To keep working from your phone, use Claude Code's official [Remote Control](https://code.claude.com/docs/en/remote-control) (`claude remote-control`); it needs a Claude subscription login, and the local `claude` process has to keep running. The session still runs on this machine, so the hooks fire as usual. While a Remote Control session is open, AI Resume treats it like any open session and does not resume underneath it — and Claude Code does not auto-continue Remote Control sessions after a limit, so the 10-minute reminder above is your cue to continue from the phone.

Driving OpenCode or Codex from the phone is no longer covered. If you need that, you can run cc-connect yourself; AI Resume no longer generates, starts or checks its configuration.

## How it fits together

```text
Claude Code / OpenCode          (Codex · Cline · Qoder: hidden by default)
       |  hooks / plugin
       v
 AiResume.Hook ----------> state\completion-events\
                                     |
                                     v
AiResume.Gui <---- state dir ----> AiResume.Worker ----> lark-cli ----> Feishu bot message
   WPF + WebView2                  resume engine, quota,
                                   notification delivery,
                                   login reminders, supervision
                                     |
                                     v
                      claude --resume <session>  (one project at a time)
```

```text
%LOCALAPPDATA%\AI Resume\          installed copy: Gui, Worker, Hook, Launcher
  state\                           kept by uninstall
    config.json                    queue, arm, per-project models, notification sources, recipient open_id
    runs.db                        SQLite/WAL: resume cycle, process registry, quota snapshots
    project-index.json             project discovery cache
    secrets\feishu-bot.bin         Feishu bot credential (machine-scope DPAPI)
    completion-events\             notification queue; dead\ holds undeliverable messages
    completion-notify-*.json       dedupe, per-message retry state, channel health
    logs\                          daily JSON logs, e.g. worker-yyyyMMdd.log
    webview2\                      WebView2 user data
```

Rules the code holds itself to:

- **Green means verified.** An installed CLI, a filled-in key or a written hook is not proof — a request or a delivery has to have succeeded.
- **Unknown means wait.** Anything that cannot be verified holds that project back; it never counts as a pass.
- **Other tools' logins are read-only.** Claude Code's login, Codex's `auth.json` and OpenCode's DeepSeek key are read, never refreshed or written.
- **No PID-only termination.** Identity, start time and command signature must all match before a process is reclaimed.

## Commands

Run them from the installed copy, so any hook path they write points there:

```powershell
$w = "$env:LOCALAPPDATA\AI Resume\AiResume.Worker.exe"
& $w notify check
```

| Command | What it does | Exit code |
|---|---|---|
| `install` | Deploy, recreate entry points, re-apply notification sources | 0 ok · 1 error · 2 sources not aligned · 3 Worker not up · 4 rollback incomplete |
| `uninstall` | Remove the installed copy, shortcuts and own hook entries; keep `state\` | |
| `shortcuts` | Recreate the Desktop, Start-menu and logon entries (`shortcuts uninstall` removes them); `install` does this for you | |
| `notify` · `notify list` | Per source: installed, enabled, 可送达 | 2 if an enabled hook is broken |
| `notify enable <源>` · `notify disable <源>` | Turn `ClaudeCode`, `OpenCode`, `Codex`, `Cline` or `Qoder` on or off, and remember it for the next install | 1 on failure |
| `notify check` | lark-cli, credential source, recipient, last real delivery, then `lark-cli doctor` | 0 ready · 2 not ready |
| `notify test` | Queue a test message and wait up to 90 seconds for the Worker to deliver it | 0 delivered · 2 not delivered |

Without a subcommand the Worker runs as the background service, which is what the logon entry starts. An unknown subcommand prints the list and returns 2.

## Verify on your machine

After `install`:

1. `& $w notify list` — 可送达 is True for every enabled source and the exit code is 0. For OpenCode, the detail should read `OpenCode 已加载(日志 …)`, not `等 OpenCode 下次加载后核实`.
2. `& $w notify check` — ends with `结论: 飞书通知通道就绪`, exit code 0.
3. `& $w notify test` — exit code 0 within 90 seconds, and **AI Resume 通知冒烟通过** arrives on your phone. On exit code 2 it says why: the blocking reason with its error code and `log_id`, or that nothing was delivered within 90 seconds — then check that the Worker is running and run `notify check`.
4. The panel footer shows **后台服务运行中**, **Claude 登录** shows **有效**, and **飞书机器人** shows **可送达**.
5. Every resume decision is logged per project as `resume.probe.evaluated` (`name=model|quota|session|evidence|reason`) in `state\logs\worker-yyyyMMdd.log`.

Pending real-machine verification for 2.2.0 (as of 2026-10-30): an automatic per-project resume after a real usage-limit interruption; OpenCode 2.x loading the new plugin and notifying; delivery by the session-0 Worker through the machine-scope credential once you save or import it; the login-expiry reminders.

## Upgrading from v2.1

- Run `install` from the new build. It rewrites the Claude Code hooks into exec form with `StopFailure`, moves an enabled OpenCode notification to the 2.x directory plugin, and deletes the unused `claude-oauth-token.bin`.
- Open **飞书通知设置**: **导入旧版凭据**, or enter the bot credential again. The recipient already in `config.json` carries over.
- Disarm and arm again. The Claude account identity now comes from Claude Code's account record instead of the rotating access token, so a v2.1 arm no longer matches (an interrupted project shows **账号已变化 · 重新布防**); re-arming also pins each project's model.
- AI Resume no longer manages cc-connect and leaves its scheduled task, npm package and `%USERPROFILE%\.cc-connect\` untouched. If you are done with it, remove them yourself — that folder holds plaintext secrets, so rotate those too.
- The Codex, Cline and Qoder notification switches moved to **视图 › 显示其他通知源**; sources you had on stay on.
- The DeepSeek tile now shows the account behind OpenCode's DeepSeek key; `DEEPSEEK_API_KEY` is only a fallback.

## Development

```powershell
dotnet test csharp\AiResume.sln
dotnet build csharp\AiResume.sln -c Release --no-restore -warnaserror
```

The suite uses temporary state, synthetic sessions and injected runners. It never resumes a real session, modifies a real project or makes a paid model call. It needs Node.js on `PATH`: the panel's view model and the OpenCode plugin are exercised in Node. `AiResume.Gui.exe --screenshot <png>` renders the panel with synthetic data (add `--screenshot-unarmed` for the unarmed state); point `AIRESUME_SHADOW_DIR` at a temporary folder first so the run leaves your real state alone.

## Documentation

[Architecture](docs/ARCHITECTURE.md) · [Quota acquisition](docs/CLAUDE-QUOTA-ACQUISITION.md) · [Notification protocol](docs/COMPLETION-NOTIFICATIONS.md) · [Run contract](docs/RUN-CONTRACT.md) · [Decisions (ADR)](docs/adr/) · [v2.2 decision (ADR-0006)](docs/adr/0006-retire-cc-connect-per-project-resume.md) · [Engineering lessons](docs/LESSONS.md) · [AI guide](AI_GUIDE.md) · [History](docs/history/)

## License

MIT — see [LICENSE](LICENSE). The interface uses [Ark Pixel Font](https://github.com/TakWolf/ark-pixel-font) under SIL OFL 1.1 ([OFL.txt](csharp/src/AiResume.Gui/wwwroot/fonts/OFL.txt)).
