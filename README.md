# AI Resume

**English** · [简体中文](README.zh-CN.md)

> Windows control plane that keeps your AI coding agents working while you sleep. When Claude Code hits its usage limit, AI Resume queues your projects and continues each one the moment the limit lifts.

Chat with your projects from Feishu or WeChat on your phone, get a notification on your desktop when a long agent task finishes, and watch quota burn down on a rack-panel dashboard. Everything runs locally — no server, no public IP, no third-party relay.

![Control panel](docs/assets/panel.png)

---

## What it actually does

AI Resume deliberately does **four** things and delegates the rest:

| | |
|---|---|
| **Post-limit resume** | The one irreplaceable part. When Claude Code reports `LimitReached`, AI Resume holds a queue of projects and resumes each one in order after the window resets. Nothing upstream does this — bridges only *read* the limit signal. |
| **Project discovery** | Finds your real projects from agent session history and Git roots, with a persisted index (full scan 2227 ms → 35 ms). |
| **Completion notifications** | When Claude Code / Codex / Cline / Qoder / OpenCode finishes a *whole task*, you get a message. Per-provider toggles; nothing is written to a config you did not enable. |
| **Control panel** | A Windows GUI for quota, the resume queue, agent selection, credentials and notification sources. |

Messaging-platform protocol, session persistence, agent lifecycle and cron are handled by **[cc-connect](https://github.com/chenhg5/cc-connect)**, which runs directly rather than being wrapped. Feishu OpenAPI work goes through `lark-cli`. The rule is *adapt to upstream, do not rewrite it* — see [ADR-0003](docs/adr/0003-cc-connect-direct-and-control-plane.md).

---

## Requirements

- Windows 10 / 11
- [.NET 10 SDK](https://dotnet.microsoft.com/download) (to build)
- [Claude Code CLI](https://claude.com/claude-code) — `npm i -g @anthropic-ai/claude-code`
- `cc-connect` — `npm i -g cc-connect` (only if you want the phone/chat side)
- Optional: Codex CLI, DeepSeek API key

---

## Install

```powershell
dotnet build csharp\AiResume.sln
csharp\src\AiResume.Worker\bin\Debug\net10.0-windows\AiResume.Worker.exe install
```

This copies the build output to `%LOCALAPPDATA%\AI Resume\`, creates the Desktop and Start-menu shortcuts, registers the resume engine for logon start, and re-points any enabled completion hooks at the installed copy.

**Why an install step exists:** entries that point straight into `bin\Debug\` break the moment you clean the build, switch branches, or rename the repo folder — and the Stop hook breaks *silently* (the UI still says "enabled", notifications just never arrive). After `install`, the repo is only source; the installed copy is what runs. Re-run `install` after code changes.

Uninstall with `AiResume.Worker.exe uninstall` — it disables hook sources individually rather than deleting your `settings.json`.

---

## Using the control panel

Double-click **AI Resume** on the Desktop.

**Status lamp** (top left) answers one question — is it working?

| Colour | Meaning |
|---|---|
| 🟢 Green | Working normally |
| 🟡 Amber | Waiting (limit not yet lifted). Not a fault. |
| 🔴 Red | Something needs your attention |
| ⚪ Grey | Not armed |

**Quota screens** show the 5-hour and 7-day windows. The bar draws **quota used**, not elapsed time; a separate hairline cursor marks where "now" sits inside the window. Numbers come from Claude's own usage endpoint — nothing is estimated.

**Queue** lists discovered projects. Tick the ones you want, press **布防 (Arm)**, close the window. The engine watches for the limit and resumes them in order.

**Providers** shows availability. A green light only ever comes from a **real request that succeeded** — a configured API key or an installed CLI is never treated as proof. Anything unverified stays grey.

The Codex row verifies one step further. Listing models only proves the server **recognises** the key; it does not prove the key is **allowed to do work**. So after `/v1/models` the probe sends one `max_tokens=1` completion — single-digit tokens. A key that can list but not infer shows red, because any task using it will fail.

---

## Talking to your projects from your phone

Once `cc-connect` is configured and running, message the bot from Feishu or WeChat:

| Command | What it does |
|---|---|
| `/help` | Command list |
| `/status` | System status (also shows your user ID) |
| `/dir <path>` · `/dir <n>` · `/dir -` · `/dir reset` | **Switch working directory — this is how you switch project** |
| `/mode <name>` | Permission mode: `plan` (read-only-ish) · `default` · `acceptEdits` · `auto-edit` · `yolo` |
| `/model switch <name>` | Switch model |
| `/provider switch <name>` | Switch API provider |
| `/new [name]` · `/list` · `/switch <n>` | New session · list · switch |
| `/stop` | Stop the running task |
| `/compress` | Compress context |
| `/cron` · `/timer` | Scheduled tasks |

**Read-only vs. modify is `/mode`, not a separate menu.** `/mode plan` makes the agent plan without editing; `/mode auto-edit` or `/mode yolo` lets it write.

**Agent selection is not a chat command.** In cc-connect one project is bound to exactly one agent (`claudecode`, `codex`, `cursor`, `gemini`, `qoder`, `opencode`, …). Change it in the control panel's **Agent** bank, regenerate the config, and restart cc-connect.

**Only you can drive it.** `allow_from` and `admin_from` pin the bot to your user ID on each platform. Messages from anyone else are dropped.

### Where conversation history lives

- **cc-connect session index** — `~/.cc-connect/sessions/<project>_<hash>.json`
- **Actual transcripts** — owned by the agent. For Claude Code: `~/.claude/projects/<encoded-workdir>/<sessionId>.jsonl`

Casual chat with no `/dir` switch is filed under the current working directory. To keep it separate, `/dir` to a scratch folder or start a named session with `/new`.

---

## Completion notifications

A local agent finishing a **whole task** produces one message. The admission rule is strict: only boundaries that represent the end of an entire agent task are accepted — a callback that fires per model request is rejected, because that would notify you dozens of times per task.

Verified sources:

| Provider | Mechanism |
|---|---|
| Claude Code | `Stop` hook |
| Codex | `notify` |
| Cline | `TaskComplete` |
| Qoder | `hooks.Stop` in `~/.qoder/settings.json` |
| OpenCode | `session.idle` plugin |

Adapters merge into existing hook configuration rather than overwriting it, and uninstall removes only their own entries. AI Resume's own probes and background resume runs set `AI_RESUME_INTERNAL_RUN=1` so they never notify.

The switch has **three** states, not two: off, on, and **on but undeliverable** — the hook is still written into the config, but the program it points at is gone. The third state turns red and carries a 「钩子断链」(broken hook) badge, because it is not the same thing as off: off is your choice, broken is a fault.

![Completion notification sources](docs/assets/panel-notify.png)

---

## Safety

Running an AI unattended against real repositories is guarded deliberately:

- **Single-consumer preflight.** A Feishu long connection is a *cluster*: with two consumers online, events are delivered to one of them at random — which looks like "the bot works sometimes". The panel scans for a second consumer and refuses to declare the machine ready when it finds one.
- **Fail-closed everywhere.** An ambiguous process probe is treated as "still running", never as "gone". A PID alone is never enough to kill something — parent PID, start time and command signature must all match.
- **Green means verified.** Availability is only ever asserted from a successful real request.
- **Credentials never enter the repository.** Feishu credentials are encrypted with Windows DPAPI for the current user, stored outside the tree, and never read back into the UI.
- **No client-side total timeout** for agent runs. Only a structured HTTP 408/504 counts as a provider timeout; DNS/TCP/TLS failures and monitor errors are local failures. Silence is a metric, not a verdict.
- **User cancellation is terminal** — it never falls back to another provider or replays a run that already had side effects.

---

## Architecture

```
┌── Feishu / WeChat ──┐
│                     ▼
│              cc-connect  ──►  agent (Claude Code / Codex / …)
│                     │
└─────────────────────┼────────────────────────────────────────┐
                      ▼                                        │
      AiResume.Worker ── resume engine, project index,          │
              │          notification sweep, process supervision│
              │                                                 │
      AiResume.Gui ──── WPF + WebView2 control panel ◄───────────┘
              │
      AiResume.Hook ─── the executable agents' hooks invoke
```

**Quota is read directly**, not borrowed from the bridge. The primary path is `GET https://api.anthropic.com/api/oauth/usage`, reusing the OAuth token Claude Code already holds — **read-only, never refreshed, never written back**, because refreshing it would race Claude Code for the refresh token. A token with under 60 seconds of life left is treated as expired. If that fails, a `ClaudeCodeProbe` subprocess takes over.

Further reading: [ARCHITECTURE.md](docs/ARCHITECTURE.md) · [RUN-CONTRACT.md](docs/RUN-CONTRACT.md) · [ADR-0003](docs/adr/0003-cc-connect-direct-and-control-plane.md) · [LESSONS.md](docs/LESSONS.md)

---

## Tests

```powershell
dotnet test csharp\AiResume.sln
```

723 xUnit tests. They never start an AI run against a real project or session, never touch `~/.claude`, `~/.codex` or the production state directory, and never make a paid API call. Probe classification is tested against recorded real responses rather than guessed shapes — a mock that guesses the wrong response shape produces an all-green suite and a silently broken product.

---

## Status

Version 2 is the C# implementation described above. The v1 PowerShell + Node runtime is retired but still present in `src/` and `test/` for reference.

Known gaps, stated plainly:

- **Resume under a real rate limit has never been carried through end to end.** The internal chain (arm → observe limit → wait → resume → disarm) has been walked with a controlled agent response; a real account quota reset has not.
- **The five notification sources have never all delivered in one run.** Only two of the five CLIs are installed on this machine.
- **The 24-hour soak has a short-sample baseline only**, which is not enough to conclude anything.

---

## Every claim on the screen

The main work in this release is not a feature. It is walking every **affirmative sentence** in the UI back to what it actually verifies.

The trigger was an external audit. None of its seven findings was a crash — all seven were **silent failures**: the panel said fine, the thing had been broken for a while.

| The panel said | Reality |
|---|---|
| Notification source "enabled" | The program the hook points at was deleted; notifications never arrive |
| Feishu "configured" | The credential had been reset upstream; every message was dropped |
| "cc-connect config generated" | That TOML would not parse at all |
| "Monitoring" in the header | The resume engine had been killed |
| Codex "credential verified" | Could list models; inference returned 403 |
| `install` exit code 0 | Not one of the five notification sources was enabled |

The common thread is not carelessness. It is that **the test looked at the configuration and never at the world**. So now:

- a notification source checks whether the executable in its command still exists;
- Feishu credentials get a **Verify** button that really exchanges a token;
- the cc-connect config is judged by **cc-connect's own parser** — on a copy, never touching the original;
- the status lamp checks whether the engine process is actually running, and how long since the last quota probe;
- install **persists the intent** ("which sources does the user want on") and reconciles against it — uninstall wipes the present state, and the present state was the only evidence there was;
- when nothing can be concluded it says "unverified", not "fine". **Reporting the unknown as normal is the same lie as reporting a fault as normal.**

---

## License

MIT — see [LICENSE](LICENSE).

The interface uses the [Ark Pixel Font](https://github.com/TakWolf/ark-pixel-font) (12px monospaced, zh_cn), distributed under the SIL Open Font License 1.1; the licence text ships at [`fonts/OFL.txt`](csharp/src/AiResume.Gui/wwwroot/fonts/OFL.txt).
