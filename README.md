# Claude Resume

> Pick your Claude Code projects, arm it, and it auto-continues them the moment your usage window resets — fully unattended, in the background.

A polished Windows tool for people who hit the Claude Code **5-hour usage limit** mid-task across several projects. Tick the projects you want, press **布防 (Arm)**, and close it. When the limit resets, a background task confirms the account is usable and runs `claude --continue` in each selected project so you come back to finished work.

![GUI](docs/gui.png)

## Requirements

- Windows 10 / 11
- [Node.js](https://nodejs.org) (LTS) — also runs the optional Feishu two-way agent
- Claude Code CLI — `npm i -g @anthropic-ai/claude-code` (shares login/sessions with the VS Code extension)

## Install

```powershell
powershell -ExecutionPolicy Bypass -File src\install.ps1
```

This copies the program to `%LOCALAPPDATA%\ClaudeResume`, creates the Desktop shortcut **「Claude续跑」**, and registers the Scheduled Task `ClaudeResumeChecker` (runs every 2 minutes). It starts **disarmed**.

## Use

1. Double-click **「Claude续跑」** on the Desktop — the picker opens (no console window).
2. Tick one or more projects (auto-discovered from your Claude Code history; use **+ 文件夹** to add any folder).
3. Press **布防 (Arm)**. Close the window if you like. You can arm **before or after** hitting the limit — if you still have quota it simply stays armed and watches until the limit hits, then resumes the moment it lifts.
4. When the 5-hour window resets, the background checker confirms readiness and continues each project. Watch progress in the log, or in `logs\run-*.log`.

- **预演 (Preview)** — dry-run: shows what *would* happen (which projects, the probe cadence) without running anything.
- **解除 (Disarm)** — global kill switch; stops all auto-resume instantly.
- **导出日志 (Export log)** — merges every `run-*.log` (+ GUI error log) into one shareable UTF-8 file for troubleshooting.
- **Quota chip (top-right)** — shows your live usage as a percentage (`5h 62%`, or `7d 53%` when the 5h window is fresh), switching to a precise countdown (`5h 限流 · 1h 04m`) once limited. It **probes on open** and **re-probes when you click it** (it doubles as the ⟳ refresh button).
- **间隔 (Interval chip)** — how often the checker auto-probes your quota while armed; click to cycle 5m / 15m / 30m. Once limited it tightens to ~4 min automatically (rejected probes are free).
- The GUI is **single-instance** (opening it again focuses the existing window) and shows its own coral icon in the taskbar.

> **About the numbers:** there is **no estimation** — every reset time / percentage is read *live* from Claude. The probe runs `claude -p` as `stream-json`; Claude emits a `rate_limit_event` carrying the server's `resetsAt` and `utilization` (the same values the `/usage` screen shows). Firing is driven by the same live probe, so the tool resumes at the real reset regardless of what's on screen.

## Feishu (飞书) integration — optional

Two independent channels, both configured in `%LOCALAPPDATA%\ClaudeResume\config.json`:

1. **Notifications (one-way).** Set `feishuWebhook` to a group's **custom-bot** webhook URL (and `feishuSecret` if you enabled 签名校验). The checker pushes key events — limited detected / resume started / per-project ✅❌ / all done.
2. **Two-way assistant.** Set `feishuAppId` + `feishuAppSecret` from a Feishu **自建应用**, then re-run `install.ps1`. A background agent (`feishu-agent.js`, Node long-connection — **no public IP needed**) lets you DM the bot to run commands: send `<项目名> <指令>` (or just `<指令>` for the single armed project) and it runs `claude --continue` there and replies with the result. `帮助` / `状态` / `项目` / `停止 <项目>` are also understood. It continues the **same** conversation your VS Code session shows (reopen the session to see it — the panel doesn't live-refresh external appends).
   - App setup: enable the **bot**, add scope `im:message` (send + receive), subscribe event `接收消息 im.message.receive_v1`, set 订阅方式 to **长连接**, then **发布版本**. Add the bot to a chat (or DM it).

## Safety

This runs Claude **unattended** on your real repos, so it is deliberately guarded:

- **Full-autonomy mode** (`--dangerously-skip-permissions`) is paired with a **git dirty-guard**: before resuming a repo with uncommitted changes, it auto-`git stash`es so anything the run does is recoverable.
- **Live probe** before every real resume — a `claude -p` call must succeed first, which is the only thing that also proves the separate **weekly** cap is clear (a passed 5-hour reset is necessary but not sufficient). Success is judged from the probe's structured `stream-json` result, **never** from the process exit code (which PowerShell 5.1 reads back as `$null`).
- **One-shot**: after a successful run it disarms itself, so it never loops every 5 hours.
- **Per-project timeout** + **process-tree kill**, **fail-closed** on any ambiguous read (assumes "still limited"), and full logging.

## Where things live

- **Project / source / docs:** `C:\Users\23230\Desktop\claude-resume`
- **Runtime copy:** `%LOCALAPPDATA%\ClaudeResume`

Edit the source here, then re-run `src\install.ps1` to redeploy.

See [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) for how it works internally.
