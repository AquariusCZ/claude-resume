# AI Resume v2 Architecture

This document describes the current C# implementation. The PowerShell + Node v1 runtime was removed on 2026-08-08 and is available only through Git history.

## Product boundary

AI Resume owns four product concerns:

1. queueing projects after Claude Code reaches a usage limit and resuming them after reset;
2. discovering and indexing local projects;
3. receiving local task-completion hooks and delivering notifications;
4. presenting a Windows control plane.

It does not reimplement messaging-platform protocol, chat sessions, agent turns, cron or the provider switch state machine. Those belong to cc-connect, which runs as the upstream daemon. Feishu OpenAPI operations outside the chat bridge use `lark-cli`.

The accepted boundary is ADR-0003. ADR-0002 still governs processes started by AI Resume itself, especially probes and resume runs.

## Process topology

```text
Feishu / WeChat
      |
      v
cc-connect daemon ---------------------> Claude Code / Codex / other agent CLI
      ^                                      |
      | config.toml                          | native session/transcript
      |                                      v
AiResume.Gui                         ~/.claude / ~/.codex / agent home
      |
      | WebView2 postMessage
      v
ControlPlaneBridge ---- local files / probes / cc-connect CLI
      |
      | named pipe
      v
AiResume.Worker ---- SQLite/WAL ---- ResumeEngine / ProcessSupervisor
      ^
      |
AiResume.Hook ---- completion events from agent hooks
```

Only one cc-connect consumer may connect to the production Feishu application. A second long-connection consumer causes cluster delivery to alternate between processes and appears as an intermittent bot.

## Components

| Project | Responsibility |
|---|---|
| `AiResume.Core` | RunContract records, enums, identifiers and interfaces with no platform I/O. |
| `AiResume.Storage` | SQLite/WAL schema, run/event/outbox/process persistence, product-cycle state and quota snapshot migration. |
| `AiResume.Ipc` | Versioned named-pipe framing between GUI, Worker and Hook paths. |
| `AiResume.Secrets` | Current-user DPAPI storage and redaction. |
| `AiResume.LarkCli` | Structured `lark-cli` process adapter with error preservation and secret redaction. |
| `AiResume.Wrapper` | Thin cc-connect integration: config generation/validation, session helpers, single-consumer checks and verified daemon restart. It does not wrap the daemon runtime. |
| `AiResume.Worker` | Long-running host: observation, resume engine, notifications, process supervision, migration/install commands. |
| `AiResume.Gui` | WPF shell + WebView2 UI. `ControlPlaneBridge` performs host-side requests off the UI thread. |
| `AiResume.Hook` | Small executable called by supported agent completion hooks; normalises and queues completion events. |

## Persistent locations

Installed binaries live under:

```text
%LOCALAPPDATA%\AI Resume\
```

Durable AI Resume state lives separately so uninstall can preserve it:

```text
%LOCALAPPDATA%\AI Resume\state\
  config.json                 product intent: projects, arm state, agent choice, notifications
  runs.db                     SQLite/WAL run, event, outbox, process, product-cycle and quota snapshot state
  logs\                       daily structured JSON logs
  <DPAPI secret files>        Feishu credentials encrypted for the current Windows user
  webview2\                   WebView2 user-data directory
```

Tests override the state root with `AIRESUME_SHADOW_DIR`. When that override is present, legacy-state migration is disabled so a test cannot move or delete production credentials.

The installer writes an exact ownership marker and payload manifest into staging. A custom target must be empty, contain only preserved `state`, match every file against the current payload, carry the active marker, or carry the exact preserved-root marker written after an uninstall retained unknown files. Volume roots, user/system roots and reparse paths are rejected. Upgrade removes previous-manifest files absent from the new payload and backs them up for rollback. Uninstall requires the active marker plus a valid manifest before changing shortcuts, processes, hooks or files. When invoked from the installed Worker, it stages a manifest-bounded temporary runtime. Before reporting success, the helper handles integrations and transactionally moves every owned file plus marker/manifest into a private retirement directory; any failure rolls those moves back, and incomplete rollback preserves the helper directory as recovery material. Missing/invalid result signals or premature helper exit are fail-closed: the parent reports the absolute recovery path and never deletes the helper directory. Cleanup after the parent exits touches only that private directory, so immediate reinstall cannot race old deletion. `state` and unknown files survive; the preserved-root marker authorizes reinstall only and cannot authorize another uninstall.

cc-connect owns its state under `%USERPROFILE%\.cc-connect\`, including `config.toml`, the daemon lock, logs and session indexes. Agent transcripts remain in each agent's own home.

## GUI and Worker

The GUI renders its shell before performing project discovery or health probes. WebView2 loads `wwwroot/index.html` through a virtual HTTPS host, sends typed requests to `ControlPlaneBridge`, and receives JSON results. DevTools are disabled by default and can be enabled only by explicitly setting `AI_RESUME_ENABLE_WEBVIEW_DEVTOOLS=1` or `true`. Host exceptions are redacted before entering the response envelope or initialization UI; the full exception is retained only in the locally redacted GUI log.

GUI micro-motion is bundled directly in `index.html` with native CSS transitions/animations; it has no CDN, runtime fetch or JavaScript animation dependency. Motion is reserved for state changes and physical feedback: refresh and operation spinners, provider probing, quota fill changes, an indeterminate quota scan when a window has no reported percentage, first-only list/status entrance, short mechanical button presses and an interruptible modal enter/exit transition. A known quota uses a named ARIA `meter`; the unknown visual is a named status, never sets `aria-valuenow` and supplies no synthetic percentage. The refresh control is a real disabled/`aria-busy` button while work is active, with fixed spinner space so text changes do not shift the header. Movement uses the shared strong `ease-out`/`ease-in-out` tokens and transform/opacity rather than layout properties. `prefers-reduced-motion` disables decorative travel and the persistent unknown scan, removes modal displacement, replaces entrance movement with short opacity fades, and keeps only essential active-operation feedback as slower stepped rotation or blinking; otherwise an active operation would regress to a misleading static glyph.

The Worker is a Generic Host with:

- `TransportBootstrap` for the named-pipe service;
- `ObservationWorker` for persisted run/process observation;
- `NotificationWorker` for completion outbox delivery;
- `ResumeEngine` for limit observation and queued project continuation.

Unknown Worker subcommands are rejected before host construction. Otherwise a typo such as `--help` can accidentally start a second long-running Worker.

## Run lifecycle

AI Resume-owned work uses `Start`, `Status` and `Cancel` over durable state:

```text
queued -> starting -> running -> succeeded
                              -> failed_provider
                              -> failed_local
                              -> cancelled
```

There is no client total timeout for AI work. Structured HTTP 408/504 or `gateway_timeout` is a provider timeout; DNS/TCP/TLS/reset, process loss, parsing failure and monitor uncertainty are local failures. Silence is telemetry, not a failure condition.

Once side effects may have started, automatic fallback or replay is forbidden. Cancellation is terminal. A run key stays occupied until the owned process is confirmed closed; an unknown PID is never treated as gone.

## Quota and resume flow

The primary quota source is `GET https://api.anthropic.com/api/oauth/usage` using Claude Code's existing OAuth access token. Requests mirror Claude Code's protocol headers: JSON accept, `anthropic-beta: oauth-2025-04-20`, `anthropic-version: 2023-06-01`, and `User-Agent: claude-code/<detected local version>`. A generic client user agent is assigned a substantially more aggressive 429 bucket. AI Resume reads but never refreshes or writes the credential because refresh-token races could invalidate Claude Code's own session. A token with under 60 seconds remaining is treated as expired.

The detailed current protocol, sparse-observation state machine, UI semantics and live verification checklist are maintained in [`CLAUDE-QUOTA-ACQUISITION.md`](CLAUDE-QUOTA-ACQUISITION.md).

If the OAuth route cannot provide a usable snapshot, `ClaudeCodeProbe` is the fallback. The resume flow is:

```text
user arms selected projects
        |
        v
ResumeEngine observes usage/limit state
        |
        +-- not limited --> keep observing
        |
        +-- limited -----> persist cycle and wait for reset
                              |
                              v
                     resume selected projects in order
                              |
                              v
                       disarm or continue mode
```

The real-account full limit-reset cycle remains a documented live-verification gap; the internal state machine is covered by controlled tests.

## cc-connect configuration activation

The control-panel button is **生成并重启 cc-connect**, not a file-only generator. One operation owns this state machine:

```text
acquire .ai-resume-cutover.lock
        |
        v
read latest config + selected agent
        |
        +-- same agent + coherent selection --> preserve project provider/model
        |
        +-- agent changed or stale selection --> clear project provider/model
        |
        v
render candidate beside config.toml
        |
        v
cc-connect config format --config <candidate copy>
        |
        +-- rejected/unknown --> keep old production file, do not restart
        |
        v
validate daemon.json work_dir/binary_path, management token/port,
current API version + lock PID, task state, and single-consumer guard
        |
        v
recheck production hash + single-consumer guard
        |
        v
atomic replacement of config.toml
        |
        v
POST authenticated localhost /api/v1/restart
        |
        v
upstream Engine.Stop closes platforms, agent sessions and agent;
old process releases lock and launches a new S4U OS process
        |
        v
verify different stable lock PID + start time in this operation + expected agent
        |
        v
verify same-generation timestamped logs: config loaded,
expected project+agent, Feishu platform ready, cc-connect running
        |
        v
verify the unique root task: exact action/script, current-user S4U/Limited principal,
PT0S/battery/restart/IgnoreNew settings and one infinite PT5M logon trigger;
then verify a newer LastRunTime task instance, direct ownership or a pre-existing watchdog,
and probe the same PID/version/agent once more before success
```

### Why AI Resume does not use upstream `restart --force`

In cc-connect v1.4.1 on Windows, bare `daemon restart` can return exit 0 while the old daemon process remains alive. The scheduled task may be `Running` even when it only contains a retrying PowerShell watchdog. Therefore neither CLI success text nor `daemon status` is a readiness signal.

Upstream `restart --force` is also insufficient for this product boundary: it rereads the lock PID after local checks and hard-kills only that daemon PID. More importantly, the installed task uses `LogonType=S4U`; the interactive GUI cannot reliably open that daemon's process handle, query its executable/start time or kill its tree. AI Resume therefore uses upstream's authenticated management self-restart. That path performs `Engine.Stop`, closes platforms and agent sessions, releases the lock and launches the next OS process inside the daemon's own security context.

The production config is committed only after candidate parsing, daemon metadata/API/version checks, exact task path/action/script/principal/settings/trigger binding, two single-consumer checks and a production hash comparison. Restart transport has three states: accepted, rejected and unknown. Because upstream queues `RestartCh` before writing the HTTP response, a reset connection is reconciled against the lock/API/log generation before rollback. Log evidence is keyed by the non-baseline lock PID, survives only a temporary API outage for that same PID, and is cleared when the lock PID changes; every startup marker must be no earlier than both the restart request and that PID's lock-file write time. If the new generation cannot be verified, AI Resume rolls the file back only when it still exactly matches this operation's committed bytes, then verifies recovery. The management token never enters logs or the UI. The confirmation warns that an active Feishu task will be interrupted, and the GUI cannot be closed normally while activation is in progress.

## Agent, provider and model semantics

These are independent layers:

| Layer | Meaning | Example |
|---|---|---|
| agent | local executor, tool and session implementation | `claudecode`, `codex` |
| provider | remote endpoint, credential and protocol mapping | Anthropic-compatible DeepSeek, OpenAI-compatible router |
| model | identifier sent to the provider | `deepseek-v4-flash`, `gpt-5.6` |

Claude Code can validly use DeepSeek when the endpoint speaks the Anthropic-compatible protocol. cc-connect injects the provider as `ANTHROPIC_BASE_URL`, token and model environment variables.

Upstream treats a provider with no `agent_types` as universal. AI Resume respects explicit `agent_types` and also applies one conservative protocol rule: a provider whose effective URL contains `/anthropic` is not referenced by a Codex project unless `[providers.endpoints]` supplies a Codex-specific non-Anthropic endpoint.

The default model and selectable model menu are also separate upstream fields. AI Resume parses provider TOML structurally, including inline arrays, applies `agent_models` and `agent_model_lists`, and follows upstream's case-sensitive identifiers and duplicate-name last-definition-wins rule. When a referenced provider has an effective default model but no list for the current agent, AI Resume adds `[[providers.agent_model_lists.<agent>]]` entries. Official OpenAI endpoints in the current Codex family receive `gpt-5.6-sol`, `gpt-5.6-terra` and `gpt-5.6-luna`, with the effective default kept first. A third-party relay receives only its configured effective default unless it already declares an explicit list; OpenAI's catalog is not evidence that an arbitrary relay implements the same slugs. Exactly one compatible provider is written to `[projects.agent.options]`; zero or multiple candidates remain unselected. User inline tables are never extended because TOML seals them. Codex's local `model_catalog_json` remains higher priority.

Global provider blocks remain user-owned and are preserved. Generated Codex entries use an `[AI Resume] ` alias prefix as durable ownership evidence because some upstream CRUD paths decode and re-encode TOML, removing comments; `config format` itself preserves them. AI Resume removes/rebuilds a Codex list only when every entry has that prefix; unmarked singletons, aliased user entries, explicit empty lists and all other user-authored lists remain untouched. TOML table matching accepts legal trailing comments, quoted path segments, quoted owned assignment keys and indented following tables so preservation cannot disagree with Tomlyn parsing. TOML strings are not trimmed because upstream compares their exact values. Project-level fields, `[projects.agent]`, `[projects.agent.options]` and custom project subtables are preserved at their original semantic level rather than flattened into one table. Inline project providers are preserved only when the agent is unchanged; an agent switch fails closed because upstream does not apply global `agent_types` filtering to `[[projects.agent.providers]]`.

## Quota source precedence and continuity

The authoritative source is Anthropic's OAuth usage response. The parser prefers the modern `limits` array (`session`, `weekly_all`, and every model-scoped `weekly_scoped`) field by field, and falls back to legacy top-level `five_hour` / `seven_day` values only where the modern field is absent. A window with neither percent nor reset is an empty shell, not quota evidence. Both OAuth and CLI `rate_limit_event` responses are sparse observations: omission is not a deletion signal. AI Resume stores the latest account-scoped `UsageSnapshot` in SQLite schema v5 table `quota_snapshots`, keyed by provider plus a SHA-256 fingerprint derived from Claude's stable `organizationUuid` (or the access token only when that field is absent). The v5 migration deliberately discards v4 rows because the old three-column table had no account identity. Storage opens lazily on the background quota request, never in the WPF constructor. Cross-process updates acquire an SQLite `IMMEDIATE` transaction and perform read, merge and write inside that transaction, so a sparse snapshot built from an old baseline cannot overwrite another window's newly committed concrete value. Explicit fields normally win, but used percentage is monotonic within the same reset generation: a late 99% observation cannot replace an already committed 100% observation. When reset generations conflict, `CapturedAt` orders them and an older observation cannot roll a newer generation back. Scoped limits use a canonical SHA-256 hash of the complete scope JSON as internal identity; display-name duplicates and response reordering therefore cannot cross-merge. A missing field or window may be carried only for the same account, same identity and same future `resetAtUnix` generation; reset changes, expiry and account changes invalidate it. Carried windows recompute `resetAfterSeconds`, include their blocked state in safety decisions, and are marked `carriedForward`. The CLI fallback's `IsLimited` flag remains a bucket/provider-level fact because it cannot identify which window caused the limit; an individual 5-hour or 7-day window is marked blocked only by its own 100% reading. A failed CLI probe that happens to expose partial windows is still unavailable, never makes the provider healthy, and uses the 30-second failure TTL rather than the five-minute success TTL. The GUI renders carried values amber as a recent server reading, never green or as an implied limit; a real percentage remains visible even when the reset timestamp is absent, a reset-only window uses a non-numeric indeterminate track, every scoped limit receives its own row, and an RPC result with `hasData=false` is never treated as healthy quota. Expired-window refresh backoff is keyed by the server reset generation and is not cleared by the local 5-hour card repaint.

Provider health uses separate evidence from Claude quota. Codex home resolution is shared by doctor, HTTP probes and notification configuration: explicit constructor input, then `AI_RESUME_CODEX_HOME`, then `CODEX_HOME`, then `%USERPROFILE%\.codex`. On window open, periodic refresh and expired-data backfill, Codex runs `codex doctor --json`, an authenticated `{base_url}/models` request, and a read-only zero-token `/v1/usage` request for eligible third-party providers. Only a user-initiated deep refresh adds one `{base_url}/responses` request with `max_output_tokens=1`. `base_url` is used exactly as Codex uses it, so an existing `/v1` segment is not duplicated. Provider `query_params`, `http_headers` and resolved `env_http_headers` are reproduced for models, responses and usage; environment-backed headers override static ones, while the final authentication, account, user-agent and accept headers retain authoritative precedence. The balance parser follows the common CC Switch `usage_script` precedence (`remaining`, then `quota.remaining`, then `balance`; default unit `USD`). Balance and account validity are read strictly: `remaining` must be a JSON number and `is_active`/`isValid` must be JSON booleans, and only those two validity fields are consulted — the recorded live `/v1/usage` response returns exactly those types, and the upstream `usage_script` reads no other field, so honouring invented markers such as `success`, `ok`, `status` or a `data` envelope would turn upstream-green providers red. For Sub2API-style providers, a successful response with no explicit invalid-account signal and `remaining > 0` is accepted as green provider/account evidence, matching CC Switch; zero balance, explicit invalidity, authentication failure and HTTP 402 take precedence and are red. HTTP 429 and CDN edge blocks are *not* insufficiency: they mean the reading failed this time, so they render amber, may carry a last-good balance for ten minutes, and never override a successful deep inference in the same round. Cloudflare `1xxx` rejections also arrive as HTTP 403; they are separated from credential rejection by inspecting the body, because the same credentials succeed once a browser user-agent is used, and reporting "credentials rejected" would send the user to replace a working key. Transient failures retry once only for network, timeout and body-read errors; HTTP 429 and 5xx are not retried immediately. A provider without valid balance evidence stays unverified until the deep minimal-inference request succeeds. When the availability and balance results carry different provider identities — the user switched provider mid-refresh — both are discarded: the row goes grey and says the configuration changed, rather than showing one provider's balance under another's lamp. Green is a current evidence state rather than an absolute guarantee for every future request. Official OpenAI/ChatGPT endpoints and ChatGPT OAuth credentials are excluded from the extra balance route; it is not a ChatGPT Plus/Pro subscription meter. Probe failures are isolated per provider. DeepSeek is green only after its authenticated balance request succeeds.

## Session boundary

When a new Engine starts with a different agent, cc-connect v1.4.1 calls `sessions.InvalidateForAgent`: it clears the old agent's native session ID and updates the stored `AgentType`. A verified daemon restart is therefore sufficient for the new agent to own subsequent turns. `/new` remains available for a deliberately clean conversation, but is not an activation requirement. `Session.ActiveProvider` is separate and is not cleared by that invalidation; a same-named provider that is still registered for the new agent may be restored. `/provider switch` changes it, while `/new` is appropriate when the user also wants fresh context.

If provider/model menus still reflect the old agent after `/new`, the first suspect is a daemon generation that never changed, not a permanently pinned session.

Session deletion also follows upstream ownership. The active session is protected; create or switch to another session before deleting it. Codex native threads must be archived/deleted through the Codex/app-server path rather than by deleting an AI Resume reference.

## Completion notifications

Supported whole-task boundaries are:

| Source | Boundary |
|---|---|
| Claude Code | `Stop` hook |
| Codex | top-level persisted thread `notify` |
| Cline | `TaskComplete` |
| Qoder | `hooks.Stop` |
| OpenCode | `session.idle` plugin |

Per-request provider callbacks are not accepted as whole-task completion. Hooks write a local event; the Worker resolves the project, deduplicates and delivers from the outbox. `AI_RESUME_INTERNAL_RUN=1` suppresses AI Resume's own probes and resume runs. For cc-connect, the generated project config owns `projects.agent.options.env.AI_RESUME_INTERNAL_RUN = "1"`, preserving unrelated user environment entries; the scheduled-task watchdog must also set the same value before starting the daemon as a process-tree fallback. Activation verifies both layers before reporting success.

Hook configuration is merged, never replaced wholesale. User intent is persisted independently from current hook health, so an enabled-but-broken executable path is shown as a fault rather than as disabled. Codex handling is limited to the top-level single-line `notify` assignment. Its outer argv array is parsed as TOML with Tomlyn, including mixed basic/literal strings and quoted nested JSON text; only an all-string array is accepted. AI Resume writes the outer array using JSON serialization because that output is a valid TOML basic-string-array subset, while the `--previous-notify` value remains JSON text for the Hook chain. Text that merely resembles `notify = [...]` inside a multiline string, array or inline table is ignored by the ownership repair scanner. Multiline, malformed or non-string assignments fail closed without turning the independently known Codex installation fact into “not installed”. A running Codex client/app-server does not prove it reloaded a later file write, so configuration changes explicitly require restarting any already-running Codex process.

The complete source contracts, admission rules, install lifecycle, queue semantics and smoke procedure are documented in [COMPLETION-NOTIFICATIONS.md](COMPLETION-NOTIFICATIONS.md).

## Process and storage safety

- Product config writes use an exclusive lock, reread the latest document inside the lock, mutate owned fields and atomically replace the file.
- SQLite uses WAL, foreign keys and a busy timeout; migrations are monotonic and idempotent.
- Owned processes are identified by PID, creation time, command signature and ownership metadata. PID alone never authorises a kill.
- Process registration failure blocks admission or requests termination; the run key is retained until real close/error.
- Logs and user-facing errors pass through secret redaction. Credentials do not enter the repository or command line.
- Feishu `allow_from` and `admin_from` are security boundaries. Empty allow lists are rejected during generated-config validation.

## Build, install and verification

```powershell
dotnet build csharp\AiResume.sln -c Release
dotnet test csharp\AiResume.sln
csharp\src\AiResume.Worker\bin\Release\net10.0-windows\AiResume.Worker.exe install
```

`install` stages and validates the merged runtime beside `%LOCALAPPDATA%\AI Resume\`, writes a payload manifest, backs up files it will replace or retire, freezes per-file SHA-256 digests, copies exactly that snapshot, verifies the installed bytes and removes obsolete files from the previous manifest before recreating shortcuts/startup entries and reconciling enabled completion hooks. The new Worker is started through a detached shell path so it cannot retain redirected installer stdout/stderr handles; it must remain alive and answer the current Named Pipe protocol from the exact PID just started. Failure restores replaced and retired files, while an incomplete rollback preserves its recovery directories. An already open GUI does not hot-reload; close and reopen it after deployment. `--screenshot` always uses an in-process synthetic bridge and a cache-busted local page URL, so public screenshots cannot expose real projects, credentials, usernames or machine paths.

### Logon autostart

The resume engine must survive logout, so `install` creates a logon entry for it. The Worker is a console executable and must stay one — `install`, `notify` and `feishu-check` all report through stdout — but a console program launched from a Startup `.lnk` is given a console window, and a `.lnk` has no hidden window style (only Normal/Minimized/Maximized). That window is not merely ugly: closing it terminates the engine, which on 2026-08-13 stranded three completion events for two and a half hours.

The Startup shortcut therefore targets `AiResume.Launcher.exe`, a WinExe shim that starts the Worker with `CreateNoWindow` and exits. It skips starting a second instance when one is already running from the same install directory, and failures are appended to `state\logs\launcher.log` because a logon failure is otherwise invisible.

A scheduled task is better still — S4U keeps the process off the interactive desktop and adds restart-on-failure, which the shortcut cannot provide — but registering one requires elevation (non-elevated registration returns `0x80070005` for every root/subfolder × S4U/Interactive combination), and `install` is not elevated. `scripts/register-autostart.ps1` performs that upgrade from an elevated shell: it registers the task, reads it back and verifies eleven contract properties before reporting success, then removes the Startup shortcut. Its `-Revert` path confirms a replacement target exists *before* unregistering, verifies the task is really gone before creating the shortcut, and verifies the shortcut exists afterwards, so no ordering leaves the machine with neither entry or with both.

`install` decides which path is live by reading the task definition at `%WINDIR%\System32\Tasks\AI Resume 续跑引擎` (readable without elevation). Existence alone is not enough: a task left behind by an earlier install, or one pointing at a different `--target`, would otherwise suppress the only autostart entry while `install` still returned 0. The definition XML is parsed and accepted only when the task is not disabled and at least one `Exec/Command` resolves to this install directory's Worker; anything unparseable, disabled or mismatched counts as *not managing autostart*, so the shortcut is created again. When the task does manage autostart, `install` also deletes any leftover Startup shortcut — refusing to create one is not enough when a previous install already left one.

Two consequences of S4U are load-bearing. First, the task's Worker runs in session 0 while `install` runs non-elevated in the interactive session and cannot read that process's `MainModule`; the "only stop processes under the target directory" rule therefore skips it silently, leaving every payload DLL locked and the install failing into an incomplete rollback. `install` now ends the task instance with `schtasks /End` (permitted for the task owner without elevation) and then waits until the Worker executable can be opened exclusively, because `/End` only signals and returns before the process exits. Second, the single-instance mutex uses the `Global\` namespace with a `Local\` fallback: a session-scoped mutex cannot be seen across the session-0/interactive boundary, so two ResumeEngines could otherwise run against one SQLite database.

DPAPI under S4U was verified on 2026-08-14 rather than assumed: a completion event delivered by the session-0 Worker requires decrypting the `CurrentUser`-scoped Feishu credential, and it reported `outcome=Sent` with no `CryptographicException`.

The C# suite currently contains 1183 xUnit tests. It uses isolated temporary state, synthetic sessions and injected process/API runners. It does not start an AI modify run against a real project or send a paid API request.

Relevant focused suites include:

- `CcConnectConfigValidatorTests`: upstream parser invocation and semantic safety;
- `CcConnectProjectIdentityTests`: agent-aware provider filtering;
- `CcConnectConfigPreserveTests`: preservation, atomic candidate validation and model-list materialisation;
- `CcConnectProjectExtraKeysTests`: same-agent preservation and cross-agent selection reset;
- `CcConnectDaemonControllerTests`: management restart, rollback, task re-arm, cross-generation log isolation and new-generation readiness;
- `PowerLossRecoveryTests`, `ProcessVerifierTests`, `ReconcilerTests`: fail-closed process recovery;
- `CodexAuthProbeTests`, `CodexProbeTests`, `CodexBalanceProbeTests`, `ControlPlaneBridgeProviderTests`: Codex route/auth semantics, third-party balance parsing and GUI evidence separation;
- `ResumeEngineTests`, `CheckerCycleTests`: limit/resume state machine;
- notification adapter and hook-health suites: merge, pruning, admission and broken-link reporting.

## Source-of-truth documents

- `README.md` / `README.zh-CN.md`: installation and user operation.
- `CLAUDE.md`: repository rules and production safety invariants.
- `AI_GUIDE.md`: compact current-project map for read-only AI Q&A.
- `docs/CLAUDE-QUOTA-ACQUISITION.md`: current Claude quota protocol, continuity rules and verification manual.
- `docs/adr/0003-cc-connect-direct-and-control-plane.md`: accepted product boundary.
- `docs/adr/0002-run-lifecycle-contract.md` and `docs/RUN-CONTRACT.md`: AI Resume-owned run semantics.
- `docs/LESSONS.md`: historical failures that still constrain engineering decisions.
- `docs/UPSTREAM-ARCHITECTURE-RESEARCH.md`: pinned upstream research.
