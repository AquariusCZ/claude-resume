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
3. Press **布防 (Arm)**. Close the window if you like.
4. When the 5-hour window resets, the background checker confirms readiness and continues each project. Watch progress in the log, or in `logs\run-*.log`.

- **预演 (Preview)** — dry-run: shows what *would* happen (which projects, the computed reset time) without running anything.
- **解除 (Disarm)** — global kill switch; stops all auto-resume instantly.

## Safety

This runs Claude **unattended** on your real repos, so it is deliberately guarded:

- **Full-autonomy mode** (`--dangerously-skip-permissions`) is paired with a **git dirty-guard**: before resuming a repo with uncommitted changes, it auto-`git stash`es so anything the run does is recoverable.
- **Live probe** before every real resume — a `claude -p` call must succeed first, which is the only thing that also proves the separate **weekly** cap is clear (a passed 5-hour reset is necessary but not sufficient).
- **One-shot**: after a successful run it disarms itself, so it never loops every 5 hours.
- **Per-project timeout** + **process-tree kill**, **fail-closed** on any ambiguous read (assumes "still limited"), and full logging.

## Where things live

- **Project / source / docs:** `C:\Users\23230\Desktop\claude-resume`
- **Runtime copy:** `%LOCALAPPDATA%\ClaudeResume`

Edit the source here, then re-run `src\install.ps1` to redeploy.

See [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) for how it works internally.
