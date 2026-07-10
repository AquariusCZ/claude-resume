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

## The checker state machine (every ~2 min) — probe-driven

The reset **time** is only ever an estimate (see below), so the checker does **not** fire on a clock. It fires when a **live probe** proves the account is usable again:

1. If disarmed → return. If no projects selected → return.
2. Compute the reset **estimate** (`Get-SessionReset`) — used only for the display and to gate probing.
3. **Cost gate**: only probe when near the estimate (≤ ~25 min) or once we've already seen the account limited. Tiered throttle: **limited → every ~4 min** (limited probes are rejected server-side, costing no quota, and firing must be prompt), **not yet limited → ~15 min**, **no estimate at all → ~30 min** (each non-limited probe costs a sliver of quota).
4. **Probe** (`claude -p "ready" --max-turns 1`, cheap model):
   - **limited** → set `sawLimited=true`, stay `waiting`.
   - **not ready** (network/other) → `waiting`, retry — **fail-closed**, never fire on an ambiguous read.
   - **usable + never saw limited** → you armed *before* hitting the cap; **stay armed** and keep watching (an earlier version auto-disarmed here, which silently cancelled exactly the "arm right before the limit, go to bed" flow).
   - **usable + had been limited** → the reset happened → **FIRE**.
5. **Fire**: resume selected projects **sequentially**. Per project: git dirty-guard → `claude --continue -p "continue"` (+ `--dangerously-skip-permissions` in full mode) → record per-project status. If a resume itself hits the limit, stop and go back to `waiting`.
6. **One-shot**: after a successful run, `enabled=false` (disarm). Per-project status means a crash-restart resumes only the unfinished ones.

### Why probe-driven? (the reset time is only an estimate)

`ccusage blocks --active` reconstructs 5-hour "blocks" from local logs and **splits on activity gaps**, so its `endTime` can be hours off from Claude's real rolling session window (observed: ccusage said **4h35m** when claude.ai said **1h12m**). A closer local estimate is `Get-SessionReset`, which uses a **rolling 5-hour window from the message timestamps** in `~/.claude` (reset = oldest message still within the last 5h, + 5h). But that's still an estimate. So the **firing** never trusts it — it uses the live probe, which is correct regardless of how far off the estimate is.

### Reading the **exact** reset live (no clock math)

The probe (`Test-ClaudeReady`) runs `claude -p "ready" --max-turns 1` as `--output-format stream-json --verbose`. Claude emits a `rate_limit_event` line carrying the server's authoritative value — the same number the interactive `/usage` screen shows:

```json
{"type":"rate_limit_event","rate_limit_info":{"status":"allowed_warning","resetsAt":1783706400,"rateLimitType":"five_hour|seven_day","utilization":0.88,...}}
```

`Test-ClaudeReady` parses `resetsAt` (Unix seconds) per `rateLimitType`; `Save-RealResetFromProbe` caches it in `state.json` **as an integer** (ConvertFrom-Json rebases ISO strings to local `[DateTime]` but leaves integers untouched → timezone-safe). The display then shows `距重置 … · 精确` from that value instead of the `≈` estimate. Caveat: the server only sends `resetsAt` once a window is **~75%+ utilized** (and always when `blocked`), so `five_hour` is absent early in a window — exactly when the estimate is good enough and you're nowhere near a limit. `/usage` itself is interactive-only (no `claude usage` subcommand), so this stream-json event is the scriptable way to read the same data. The GUI probes for it only near the estimated reset (est ≤ 90 min) or when limited, at most every 5 min; the checker reuses its existing near-reset probes.

## Key implementation details (and the bugs they avoid)

- **Timezone (the ~8h footgun).** `ConvertFrom-Json` silently rebases ISO-`Z` datetimes to local (+08:00 here). We regex the **raw** JSON for `endTime`, then `[DateTimeOffset]::Parse(…RoundtripKind).ToUniversalTime()` compared against `[DateTimeOffset]::UtcNow`. Verified in both PowerShell 5.1 and 7.
- **Launching `claude`.** `Start-Process claude.cmd -RedirectStandardOutput` can't exec a `.cmd` (UseShellExecute=false). We launch `cmd.exe /c claude.cmd …`, **tail the redirect file** for live log lines, and kill the **whole process tree** (recursive `Win32_Process` by `ParentProcessId`) on stop/timeout — the returned PID is only the cmd wrapper. Verified with a stand-in that spawns children.
- **`$p.ExitCode` is a lie in PS 5.1 (the bug that silently blocked every resume).** After `Start-Process -PassThru`, `.ExitCode` reads **`$null`** once the process has exited — with `WaitForExit(ms)` *and* with `HasExited` polling (both verified live; `WaitForExit(ms)` opens the handle SYNCHRONIZE-only). Result: a *successful* probe fell through to the exit-code test, read `$null ≠ 0`, logged `探测未就绪 (exit-)`, and the fail-closed loop never fired. Fix, twice over: `$null = $p.Handle` right after launch caches a query-capable handle, **and** success no longer relies on the exit code at all — the stream-json line `"type":"result" … "is_error":false` is the authoritative success signal (checked before the fuzzy limit-text match, so a successful run that merely *mentions* limits can't be misread as limited; the structured `"status":"blocked"` check still runs first).
- **Probe self-noise.** Probes run with `-WorkingDirectory` = AppDir, and `Get-SessionReset` excludes that project folder (plus legacy `C--Windows-*` ones from the task's System32 default cwd) — otherwise the probes' own session logs feed back into the activity estimate that gates probing.
- **Project discovery.** The `~/.claude/projects/<encoded>` folder names are lossy/ambiguous, so we read the real `cwd` (and last-used time) from each session `*.jsonl`. Resume uses `claude --continue` in that `cwd` (continue the most recent conversation there) — which also works for folders added via **+ 文件夹**.
- **Encoding.** Every `.ps1` is saved **UTF-8 with BOM** so Windows PowerShell 5.1 parses non-ASCII correctly.
- **Responsive UI.** The GUI's 1-second timer only does fast local/file reads (log tail, config/state, countdown from a cached target); the slow work — the `Get-SessionReset` estimate and the occasional exact-reset probe — runs on a background runspace and just publishes results into a synchronized hashtable. Button actions show a 6-second "flash" status so the timer doesn't stomp their feedback.
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
  "sawLimited": false,        // has the account been observed rate-limited during this arming?
  "lastProbeUtc": null,       // marker for the ~4-min probe throttle
  "projectStatus": { "<path>": "success|error|timeout|limited|stopped" },
  "phase": "idle|waiting|resuming|done",
  "realFiveHourResetUtc": null,  // EXACT 5h reset (Unix seconds) from the probe's rate_limit_event
  "realSevenDayResetUtc": null,  // EXACT weekly reset (Unix seconds), same source
  "realResetProbedUtc": null,    // when the above were last read (Unix seconds); trusted for 5h
  "realFiveHourUtil": null       // 5h utilization 0..1 at that probe
  // targetId / targetEndUtc / firedForId: legacy fields, unused by the probe-driven engine
}
```

## Not yet live-tested

Every component is verified (discovery, timezone, launch+stream+tree-kill, GUI render + live smoke test, task registration, dry-run). The **end-to-end fire/resume** path is intentionally not executed in testing — it would run Claude autonomously on real repos and consume quota. Validate it yourself with **预演** first, and on the first real run pick a low-stakes project.
