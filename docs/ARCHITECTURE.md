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
| `lib.ps1` | shared engine functions (discovery, probe, launch, git-guard, logging, Feishu notify). Dot-sourced by the PowerShell scripts. |
| `checker.ps1` | the stateless state machine, run by the Scheduled Task every 2 min. `-DryRun` = preview. |
| `picker.ps1` | WPF/XAML GUI (config + monitor + on-demand probe). `-RenderTo <png>` snapshots it headless; single-instance. |
| `feishu-agent.js` | Node long-connection listener: receives Feishu messages → runs `claude --continue` in the target project → replies. |
| `launcher.vbs` / `checker-launch.vbs` / `feishu-launch.vbs` | hidden launchers (GUI / checker task / Feishu agent, the last auto-restarts node). |
| `install.ps1` | deploy files, icon, Desktop shortcut, checker task, Feishu Startup entry + `npm install`. |

State lives in `%LOCALAPPDATA%\ClaudeResume`: `config.json`, `state.json`, `logs/`, `node_modules/`, `feishu-agent.pid`.

## The checker state machine (every ~2 min) — probe-driven, fixed cadence

There is **no reset-time estimation**. The checker does not fire on a clock; it fires when a **live probe** proves the account is usable again, and it probes on a **fixed interval** (not gated by any estimate — an off estimate used to delay limit-detection):

1. If disarmed → return. If no projects selected → return.
2. Throttle: probe every `probeIntervalMinutes` while usable (GUI chip: 5/15/30, default 15), tightening to ~4 min once limited. If the last probe was more recent than that, return.
3. **Probe** (`claude -p "ready" --max-turns 1`, cheap model):
   - **limited** → set `sawLimited=true`, stay `waiting`.
   - **not ready** (network/other) → `waiting`, retry — **fail-closed**, never fire on an ambiguous read.
   - **usable + never saw limited** → you armed *before* hitting the cap; **stay armed** and keep watching (an earlier version auto-disarmed here, which silently cancelled exactly the "arm right before the limit, go to bed" flow).
   - **usable + had been limited** → the reset happened → **FIRE**.
5. **Fire**: resume selected projects **sequentially**. Per project: git dirty-guard → `claude --continue -p "continue"` (+ `--dangerously-skip-permissions` in full mode) → record per-project status. If a resume itself hits the limit, stop and go back to `waiting`.
6. **One-shot**: after a successful run, `enabled=false` (disarm). Per-project status means a crash-restart resumes only the unfinished ones.

### Why probe-driven, no estimation?

Every local estimate we tried (ccusage's gap-split blocks; a rolling-5h-window over `~/.claude` timestamps) could be off by hours from Claude's real rolling session window (observed: one estimate said **4h35m** when claude.ai said **1h12m**). Worse, when the estimate *gated probing*, an over-long estimate delayed limit-detection by exactly its error. So estimation was removed entirely: the checker probes on a fixed interval and **fires only on a live probe**, which is correct regardless.

### Reading the **exact** reset + utilization live

The probe (`Test-ClaudeReady`) runs `claude -p "ready" --max-turns 1` as `--output-format stream-json --verbose`. Claude emits a `rate_limit_event` line carrying the server's authoritative values — the same numbers the interactive `/usage` screen shows:

```json
{"type":"rate_limit_event","rate_limit_info":{"status":"allowed_warning","resetsAt":1783706400,"rateLimitType":"five_hour|seven_day","utilization":0.88,...}}
```

`Test-ClaudeReady` parses `resetsAt` (Unix seconds) and `utilization` per `rateLimitType` (both `five_hour` and `seven_day`); `Save-RealResetFromProbe` caches the reset in `state.json` **as an integer** (ConvertFrom-Json rebases ISO strings to local `[DateTime]` but leaves integers untouched → timezone-safe). The GUI chip shows **both windows** (`5h 62% · 7d 53%`), each switching to a precise countdown (`5h 限流 · 1h 04m`) once limited. Note the server only sends a window's `rate_limit_info` once it is **utilized enough** (and always when `blocked`), so early in a fresh 5h window the server sends no 5h number — the chip shows `5h 低` for that window (well below its limit) alongside the 7-day figure. `/usage` itself is interactive-only (no `claude usage` subcommand), so this stream-json event is the scriptable way to read the same data.

### The GUI probes on demand, not on a loop

`picker.ps1` fires **one** probe when it opens and **one** each time you click the quota chip (which doubles as the ⟳ refresh button) — never on a timer. The probe runs on a background runspace so the window never freezes; results land in a synchronized hashtable the 1-second UI timer reads. The GUI is **single-instance** (a named `Mutex`; a second launch focuses the existing window via its process `MainWindowHandle` — `FindWindow` is unreliable for WPF layered windows) and carries its own coral taskbar icon (`SetCurrentProcessExplicitAppUserModelID`).

## Key implementation details (and the bugs they avoid)

- **Timezone (the ~8h footgun).** `ConvertFrom-Json` silently rebases ISO-`Z` datetimes to local (+08:00 here). We sidestep it entirely by storing the reset as a **Unix integer** (`resetsAt`) — integers round-trip through JSON untouched — and reading it back with `FromUnixTimeSeconds`, compared against `[DateTimeOffset]::UtcNow`. Verified in both PowerShell 5.1 and 7.
- **Launching `claude`.** `Start-Process claude.cmd -RedirectStandardOutput` can't exec a `.cmd` (UseShellExecute=false). We launch `cmd.exe /c claude.cmd …`, **tail the redirect file** for live log lines, and kill the **whole process tree** (recursive `Win32_Process` by `ParentProcessId`) on stop/timeout — the returned PID is only the cmd wrapper. Verified with a stand-in that spawns children.
- **`$p.ExitCode` is a lie in PS 5.1 (the bug that silently blocked every resume).** After `Start-Process -PassThru`, `.ExitCode` reads **`$null`** once the process has exited — with `WaitForExit(ms)` *and* with `HasExited` polling (both verified live; `WaitForExit(ms)` opens the handle SYNCHRONIZE-only). Result: a *successful* probe fell through to the exit-code test, read `$null ≠ 0`, logged `探测未就绪 (exit-)`, and the fail-closed loop never fired. Fix, twice over: `$null = $p.Handle` right after launch caches a query-capable handle, **and** success no longer relies on the exit code at all — the stream-json line `"type":"result" … "is_error":false` is the authoritative success signal (checked before the fuzzy limit-text match, so a successful run that merely *mentions* limits can't be misread as limited; the structured `"status":"blocked"` check still runs first).
- **Probe self-noise.** Probes run with `-WorkingDirectory` = AppDir so their own `claude` sessions land in one known `.claude/projects` folder that project discovery skips — they never pollute the project list.
- **Project discovery.** The `~/.claude/projects/<encoded>` folder names are lossy/ambiguous, so we read the real `cwd` (and last-used time) from each session `*.jsonl`. Resume uses `claude --continue` in that `cwd` (continue the most recent conversation there) — which also works for folders added via **+ 文件夹**.
- **Encoding.** Every `.ps1` is saved **UTF-8 with BOM** so Windows PowerShell 5.1 parses non-ASCII correctly.
- **Responsive UI.** The GUI's 1-second timer only does fast local/file reads (log tail, config/state, countdown from the cached probe result); the slow work — the on-demand probe — runs on a background runspace and publishes into a synchronized hashtable. Button actions show a 6-second "flash" status so the timer doesn't stomp their feedback.
- **AV-safe.** Nothing uses the `.lnk → powershell -WindowStyle Hidden -ExecutionPolicy Bypass` pattern that Huorong (火绒) deletes; launches go through `wscript`-hidden `.vbs` and the Scheduled Task.

## Feishu integration (two channels)

**One bot does both.** With a self-built app configured, notifications and two-way commands both go through the single app bot, in the same chat. `Send-FeishuNotify` (in `lib.ps1`) prefers the **app API** (`im/v1/messages` with a cached `tenant_access_token`) sending to `feishuChatId` — the chat the agent last saw a message in, which it writes back to `config.json`. If the app isn't fully set up it falls back to a **custom-bot webhook** (optionally **签名校验**-signed: HMAC-SHA256 with key `"<timestamp>\n<secret>"` over an empty message, base64, as `{timestamp, sign}`).

The **two-way agent** (`feishu-agent.js`, Node) uses the official `@larksuiteoapi/node-sdk` `WSClient` — a persistent WebSocket to Feishu, so **no public IP** is needed. It registers `im.message.receive_v1` (and `_v2` as a safety net) and runs a small **conversation state machine**, keyed by chat in `feishu-sessions.json`:

- **Chat mode** (default): a plain message is answered by Claude in a scratch session (`feishu-chat/`, no `--dangerously-skip-permissions`, kept out of project discovery) — it touches **no project**.
- **进入 `<编号|名字>`** enters a project (persisted); from then every message runs `claude --continue -p "<text>"` in that project's cwd. **退出** returns to chat mode.
- Shortcuts that work anywhere: **项目** (list), **状态**, **停止**, **帮助**; a bare project name/number enters it; **`<项目名> <指令>`** does a one-off run without switching; greetings get a quick reply (never burn quota).

Because `--continue` appends to the cwd's most-recent conversation, a project run continues the **same** thread the VS Code extension shows (reopen the session there; the panel doesn't live-refresh external appends). Replies chunk to ≤6 parts. Single-instance via a pidfile with a liveness check (Windows lets two sockets share a loopback port, so a port lock is unreliable). Started at logon by a Startup-folder shortcut → `feishu-launch.vbs`, which auto-restarts node on exit and captures its stdout for diagnosis. Requires the app to have: bot enabled, scopes `im:message` (+ send), the **接收消息 `im.message.receive_v1`** event (a "v2.0" schema badge on it is fine — the event name still ends `_v1`), **长连接** subscription mode, and a published version.

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
  "probeIntervalMinutes": 15, // probe cadence while usable (GUI chip cycles 5/15/30); limited = 4 min
  "feishuAppId": "",          // 自建应用 App ID — one bot for notify + two-way (preferred)
  "feishuAppSecret": "",      // 自建应用 App Secret
  "feishuChatId": "",         // auto-filled by the agent: the chat notifications are sent to
  "feishuWebhook": "",        // fallback: custom-bot webhook URL (used only if the app isn't set up)
  "feishuSecret": "",         // custom-bot 签名校验 secret (optional, for the webhook fallback)
  "feishuDefaultProject": "", // default project (name or path) for un-prefixed Feishu commands
  "feishuAllowOpenIds": [],   // optional allowlist of sender open_ids; empty = anyone in-chat
  "continuous": false,       // one-shot by default
  "projectHome": "C:\\Users\\23230\\Desktop\\claude-resume"
}
```

## state.json (written by the checker)

```jsonc
{
  "sawLimited": false,        // has the account been observed rate-limited during this arming?
  "lastProbeUtc": null,       // marker for the probe throttle
  "limitedRefires": 0,        // consecutive resume-was-limited count; ≥6 => treat as misclassification, skip
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

Component-verified (discovery, launch+stream+tree-kill, probe returns exact reset + utilization, GUI layout + single-instance + on-open probe, Feishu one-way push signed, two-way agent long-connection handshake). The **end-to-end fire/resume** path and the **Feishu receive→run→reply** round-trip depend on live quota / your Feishu console setup, so validate them yourself: **预演** first, and DM the bot `帮助` once its app is published.
