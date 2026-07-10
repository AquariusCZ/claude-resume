# Claude Resume

> Pick your Claude Code projects, arm it, and it auto-continues them the moment your usage window resets — fully unattended, in the background.

A polished Windows tool for people who hit the Claude Code **5-hour usage limit** mid-task across several projects. Tick the projects you want, press **布防 (Arm)**, and close it. When the limit resets, a background task confirms the account is usable and runs `claude --continue` in each selected project so you come back to finished work.

![GUI](docs/gui.png)

## Requirements

- Windows 10 / 11
- [Node.js](https://nodejs.org) (LTS)
- [`ccusage`](https://github.com/ryoppippi/ccusage) — `npm i -g ccusage` (used to read the reset time)
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

- **预演 (Preview)** — dry-run: shows what *would* happen (which projects, the computed reset time) without running anything.
- **解除 (Disarm)** — global kill switch; stops all auto-resume instantly.
- **导出日志 (Export log)** — merges every `run-*.log` (+ GUI error log) into one shareable UTF-8 file for troubleshooting.

> **About the countdown:** when the tool can read the **exact** reset it shows `距重置 … · 精确` — this number is read *live* from Claude itself: the probe runs `claude -p` as `stream-json` and Claude emits a `rate_limit_event` carrying the server's precise `resetsAt` (the same value the `/usage` screen shows). Claude only sends `resetsAt` once a window is ~75%+ used (and always when you're actually blocked), so before you're near the limit there's no server number to read and the chip falls back to a `≈` *estimate* reconstructed from your local `~/.claude` logs. Either way correctness doesn't depend on the displayed number: firing is driven by the same **live probe**, so the tool resumes at the real reset regardless.

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
