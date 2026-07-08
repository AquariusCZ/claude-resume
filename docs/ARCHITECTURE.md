# Architecture

## Overview

Claude Resume is split into a **GUI front-end** and a **headless engine**, on purpose:

```
┌─────────────────────────┐        writes         ┌──────────────────┐
│  picker.ps1 (WPF GUI)   │ ───── config.json ──► │  config.json     │
│  - discover projects    │                       │  state.json      │
│  - arm / disarm         │ ◄──── reads log ───── │  logs/run-*.log  │
│  - live monitor         │                       └────────▲─────────┘
└─────────────────────────┘                                │ reads/writes
            ▲ launched by launcher.vbs (hidden)            │
            │                                     ┌─────────┴─────────┐
      Desktop shortcut                            │  checker.ps1      │  ◄── Scheduled Task
                                                  │  (the engine)     │      every 2 min
                                                  └─────────┬─────────┘
                                                            │ uses
                                                  ┌─────────┴─────────┐
                                                  │  lib.ps1 (shared) │
                                                  └───────────────────┘
```

**Why split?** The resume run can take many minutes/hours. If it ran inside the WPF UI thread the window would freeze and the Stop/Disarm controls would die exactly when needed. So the GUI only *configures and monitors*; the actual waiting + resuming happens in a separate process owned by a **Scheduled Task**. That also makes it survive closing the window / logging off-and-on / reboot.

## Components

| File | Role |
|---|---|
| `lib.ps1` | shared engine functions (discovery, reset-time, probe, launch, git-guard, logging). Dot-sourced by both other scripts. |
| `checker.ps1` | the stateless state machine, run by the Scheduled Task every 2 min. `-DryRun` = preview. |
| `picker.ps1` | WPF/XAML GUI (config + monitor). `-RenderTo <png>` snapshots it headless. |
| `launcher.vbs` | opens the GUI with the PowerShell console hidden. |
| `checker-launch.vbs` | the Scheduled Task action; runs the checker fully hidden. |
| `install.ps1` | icon, Desktop shortcut, task registration. |

State lives in `%LOCALAPPDATA%\ClaudeResume`: `config.json`, `state.json`, `logs/`.

## The checker state machine (every 2 min)

1. **Read** `ccusage blocks --active --json`. On any bad/ambiguous read → log and return (**fail-closed** — never fire on a bad read).
2. **Adopt** the live window: if a block is active and `now < endTime`, record it as the target and **never fire mid-window** (just wait). A new block id resets per-window bookkeeping.
3. **Fire decision**: only after the *adopted* window's `endTime + safetyMargin` has passed (or `blocks[]` is empty). A cold start with no adopted window stays idle (avoids surprise fires).
4. **Probe** (`claude -p`) — the authoritative check that the account is usable *now*. This is the only signal that also proves the separate **weekly 7-day cap** is clear. If limited → long weekly back-off.
5. **Fire**: mark `firedForId = targetId` **first** (idempotency), then resume selected projects **sequentially**. Per project: git dirty-guard → `claude --continue -p "continue"` (+ `--dangerously-skip-permissions` in full mode) → record status.
6. **One-shot**: after a successful run, set `enabled=false` (disarm) so it doesn't loop every reset. Crash-recovery: if it died mid-run, remaining projects resume on the next tick (per-project status prevents re-running finished ones).

## Key implementation details (and the bugs they avoid)

- **Timezone (the ~8h footgun).** `ConvertFrom-Json` silently rebases ISO-`Z` datetimes to local (+08:00 here). We regex the **raw** JSON for `endTime`, then `[DateTimeOffset]::Parse(…RoundtripKind).ToUniversalTime()` compared against `[DateTimeOffset]::UtcNow`. Verified in both PowerShell 5.1 and 7.
- **Launching `claude`.** `Start-Process claude.cmd -RedirectStandardOutput` can't exec a `.cmd` (UseShellExecute=false). We launch `cmd.exe /c claude.cmd …`, **tail the redirect file** for live log lines, and kill the **whole process tree** (recursive `Win32_Process` by `ParentProcessId`) on stop/timeout — the returned PID is only the cmd wrapper. Verified with a stand-in that spawns children.
- **Project discovery.** The `~/.claude/projects/<encoded>` folder names are lossy/ambiguous, so we read the real `cwd` (and last-used time) from each session `*.jsonl`. Resume uses `claude --continue` in that `cwd` (continue the most recent conversation there) — which also works for folders added via **+ 文件夹**.
- **Encoding.** Every `.ps1` is saved **UTF-8 with BOM** so Windows PowerShell 5.1 parses non-ASCII correctly.
- **Responsive UI.** The GUI's 1-second timer only does fast local/file reads (log tail, config/state); the slow `ccusage` call runs on a background runspace. Button actions show a 6-second "flash" status so the timer doesn't stomp their feedback.
- **AV-safe.** Nothing uses the `.lnk → powershell -WindowStyle Hidden -ExecutionPolicy Bypass` pattern that Huorong (火绒) deletes; launches go through `wscript`-hidden `.vbs` and the Scheduled Task.

## config.json (written by the GUI)

```jsonc
{
  "enabled": false,          // armed? (disarm = global kill switch)
  "selected": [ { "name": "...", "path": "..." } ],
  "customProjects": [ { "name": "...", "path": "..." } ],
  "resumePrompt": "continue",
  "skipPermissions": true,   // full unattended
  "dirtyGuard": "stash",     // or "branch"
  "perProjectTimeoutMinutes": 30,
  "safetyMarginSeconds": 60,
  "weeklyBackoffMinutes": 45,
  "probeModel": "haiku",
  "continuous": false,       // one-shot by default
  "projectHome": "C:\\Users\\23230\\Desktop\\claude-resume"
}
```

## state.json (written by the checker)

```jsonc
{
  "targetId": "2026-07-07T15:00:00.000Z",   // adopted window's block id
  "targetEndUtc": "2026-07-07T20:00:00Z",   // its reset instant (UTC)
  "firedForId": null,                        // set == targetId once fired (idempotency)
  "projectStatus": { "<path>": "success|error|timeout|limited|stopped" },
  "phase": "idle|waiting|resuming|weekly-backoff|done"
}
```

## Not yet live-tested

Every component is verified (discovery, timezone, launch+stream+tree-kill, GUI render + live smoke test, task registration, dry-run). The **end-to-end fire/resume** path is intentionally not executed in testing — it would run Claude autonomously on real repos and consume quota. Validate it yourself with **预演** first, and on the first real run pick a low-stakes project.
