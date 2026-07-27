# NightShift — Implementation Plan

> **For the implementing agent:** This is the authoritative plan. Work top-to-bottom through
> the phases. After completing each task, tick its checkbox in this file and commit. Never
> skip the acceptance criteria — they are the definition of done. If a decision in this plan
> turns out to be wrong when you hit the code, fix the plan first, then the code.

---

## 1. What we are building

A Windows-first (cross-platform-capable) desktop app that babysits a Claude Code project:

1. You point it at a **project directory** containing an existing **`plan.md`**.
2. Every **N minutes (default 5)** it wakes up and checks your **Claude subscription usage**.
3. If usage is **below a threshold (default 90%)**, it launches **Claude Code in that directory**,
   applies the **`caveman` skill at `full` level**, and instructs Claude to continue working
   through `plan.md`.
4. If usage is at or above the threshold, it skips the cycle, logs why, and waits for the next tick.
5. Every run is logged, streamed live into the UI, and browsable in a history view.

### Non-goals (v1)

- No multi-project orchestration (one project directory at a time). Design the config for a
  list so v2 can add it, but the UI ships with a single active project.
- No editing of `plan.md` from inside the app. The app reads it; Claude writes it.
- No macOS/Linux packaging. The code must stay platform-neutral where cheap, but only Windows
  is tested and shipped.

---

## 2. Tech stack (fixed — do not substitute)

| Concern | Choice | Notes |
|---|---|---|
| Runtime | **.NET 10** (`net10.0`) | `net10.0-windows` only if a Windows-only API is genuinely needed; prefer plain `net10.0`. |
| UI | **Avalonia UI 12.1.0** | Newest stable 12.x as of 2026-07-26 (verified with `dotnet package search Avalonia --exact-match`). The `avalonia.mvvm` template already emits 12.1.0 on `net10.0`. Use `Avalonia.Themes.Fluent`. |
| MVVM | `CommunityToolkit.Mvvm` (source generators) | `[ObservableProperty]`, `[RelayCommand]`. |
| DI / hosting | `Microsoft.Extensions.Hosting` + `Microsoft.Extensions.DependencyInjection` | Background scheduler as an `IHostedService`. |
| Logging | `Microsoft.Extensions.Logging` + `Serilog.Sinks.File` | Rolling file in the app data dir. |
| JSON | `System.Text.Json` with source-generated contexts | Needed for trimming/AOT friendliness later. |
| Tray icon | Avalonia `TrayIcon` | Built in; no extra dependency. |
| Tests | `xunit.v3` 3.2.2 + `NSubstitute` 6.0.0 | The SDK's `dotnet new xunit` template still emits xunit v2; the csproj is hand-written for v3 (`OutputType=Exe`). |

Create the solution with a `Directory.Build.props` that sets `<Nullable>enable</Nullable>`,
`<TreatWarningsAsErrors>true</TreatWarningsAsErrors>`, `<ImplicitUsings>enable</ImplicitUsings>`,
`<LangVersion>latest</LangVersion>`.

---

## 3. Solution layout

```
NightShift.sln
├─ Directory.Build.props
├─ src/
│  ├─ NightShift.Core/                 (net10.0, no UI references)
│  │  ├─ Configuration/
│  │  │   ├─ PilotSettings.cs
│  │  │   ├─ ISettingsStore.cs
│  │  │   └─ JsonSettingsStore.cs
│  │  ├─ Usage/
│  │  │   ├─ UsageSnapshot.cs
│  │  │   ├─ IUsageProvider.cs
│  │  │   ├─ OAuthUsageProvider.cs      (primary)
│  │  │   ├─ CcusageProvider.cs         (fallback)
│  │  │   ├─ CompositeUsageProvider.cs
│  │  │   └─ ClaudeCredentialReader.cs
│  │  ├─ Execution/
│  │  │   ├─ IClaudeRunner.cs
│  │  │   ├─ HeadlessClaudeRunner.cs
│  │  │   ├─ TerminalClaudeRunner.cs
│  │  │   ├─ ClaudeExecutableLocator.cs
│  │  │   ├─ PromptBuilder.cs
│  │  │   └─ StreamJsonParser.cs
│  │  ├─ Scheduling/
│  │  │   ├─ PilotScheduler.cs          (IHostedService)
│  │  │   ├─ CycleDecision.cs
│  │  │   └─ RunGate.cs                 (overlap guard)
│  │  ├─ History/
│  │  │   ├─ RunRecord.cs
│  │  │   └─ RunHistoryStore.cs
│  │  └─ Preflight/
│  │      └─ PreflightChecker.cs
│  └─ NightShift.Desktop/              (net10.0, Avalonia)
│     ├─ App.axaml(.cs)
│     ├─ Program.cs
│     ├─ Views/  (MainWindow, DashboardView, SettingsView, HistoryView)
│     ├─ ViewModels/
│     ├─ Controls/ (UsageGauge)
│     └─ Assets/
└─ tests/
   └─ NightShift.Core.Tests/
```

`NightShift.Core` must have **zero** Avalonia references so it stays unit-testable and
reusable by a future CLI/service host.

---

## 4. Usage detection — the important part

> **Read this section carefully. This is where a naive implementation goes wrong.**

The requirement is *"check Claude for usage and if less than 90%"*. That means **percentage of
your subscription's rate-limit quota**, not raw token counts or dollars. Two sources exist and
they measure different things:

### 4.1 Primary: Anthropic OAuth usage endpoint (gives a true percentage)

```
GET https://api.anthropic.com/api/oauth/usage
Authorization: Bearer <oauth access token>
anthropic-beta: oauth-2025-04-20
User-Agent: claude-code/<version>
Content-Type: application/json
```

Response shape:

```json
{
  "five_hour":        { "utilization": 33.0, "resets_at": "2026-04-11T07:00:00.528743+00:00" },
  "seven_day":        { "utilization": 13.0, "resets_at": "2026-04-17T00:59:59.951713+00:00" },
  "seven_day_opus":   null,
  "seven_day_sonnet": { "utilization": 1.0,  "resets_at": "2026-04-16T03:00:00.951719+00:00" },
  "extra_usage":      { "is_enabled": false, "monthly_limit": null, "used_credits": null, "utilization": null }
}
```

`utilization` is a percentage 0–100. Any window object may be `null`.

> **Correction (verified 2026-07-26).** An earlier version of this plan said to treat a null window
> as **0**. That is wrong and dangerous: 0 reads to the §4.3 threshold check as "plenty of quota
> left", so a provider hiccup would start a run instead of skipping one — the exact failure mode
> §11.2 exists to prevent. A missing window is modelled as a **null `UsageWindow`**, and
> `UsageMetricSelector` returns null (⇒ *unavailable* ⇒ `OnUsageUnavailable`, default Skip).
> A 200 response whose windows are *all* absent is likewise reported as unavailable.
>
> Live response for a Max subscription, for reference: `five_hour` and `seven_day` present,
> `seven_day_opus` and `seven_day_sonnet` both `null`.

**Credentials.** On **Windows and Linux**, read `%USERPROFILE%\.claude\.credentials.json`
(`~/.claude/.credentials.json`), field `claudeAiOauth.accessToken`. There is also
`claudeAiOauth.expiresAt` (epoch **milliseconds**) — if it is in the past, treat the token as
stale, surface "Claude login expired — run `claude` and re-authenticate" in the UI, and fall
back to §4.2. On **macOS** the credentials live in the Keychain; shell out to
`security find-generic-password -s "Claude Code-credentials" -w` and parse the same JSON.
Wrap this in `ClaudeCredentialReader` behind an interface so tests can inject a fake.

**The `User-Agent` header is mandatory.** Without `claude-code/<version>` you land in an
aggressively rate-limited bucket. Get the version once at startup by running
`claude --version` and caching the result; fall back to a hardcoded plausible version string
if that fails.

**Caveats to code against:**
- This endpoint is **undocumented and community-discovered**. It can change or disappear
  without notice. Every call must be defensive: unknown fields ignored, missing fields
  tolerated, non-200 handled.
- `401` → token invalid/expired. Do **not** retry in a loop. Mark the provider unhealthy,
  show a clear "re-authenticate" banner, fall back to ccusage.
- `429` → back off exponentially (1m, 5m, 15m, cap 30m) and fall back to ccusage meanwhile.
  There are known reports of persistent 429s from this endpoint; never let that spin the app.
- Cache the response for at least 60 seconds; never call it more than once per scheduler tick
  plus manual refreshes.

### 4.2 Fallback: `ccusage`

`ccusage` reads local `~/.claude` JSONL transcripts. It reports **tokens and cost**, not plan
quota, so it can only produce a percentage when you give it a token budget.

```
npx ccusage@latest blocks --json --token-limit max
```

> **Correction (verified against ccusage 20.0.18, 2026-07-26).** This plan originally specified
> `blocks --active --json --token-limit max`. That combination is silently broken: `max` means
> "the highest previously observed block", so it needs the full block history to resolve, and
> `--active` filters that history away. ccusage then emits **no `tokenLimitStatus` and no
> `tokenLimit` at all** — no error, just a missing field — so no percentage can be derived and the
> whole fallback path is dead. Drop `--active` and pick the active block out of the array
> ourselves (the parser already scans for `isActive: true`). An explicit numeric
> `--token-limit 500000` *does* survive `--active`, so a user override may legitimately use both.
>
> Prefer ccusage's own `tokenLimitStatus.percentUsed` when present; recompute from token counts
> only when it is absent. The two differ — ccusage's figure accounts for projected usage.
>
> **How far off "approximate" really is:** on the same 5-hour window, the OAuth endpoint reported
> **14%** while ccusage reported **41.18%**. Treating the ccusage number as a quota percentage
> would skip runs for most of a night that had 86% of its session quota free. The "approximate"
> chip in §9.1 is not cosmetic.

Relevant flags: `--active` (current 5-hour window only), `--json`, `--token-limit <n|max>`
(`max` = your highest previously observed block, used as the implied ceiling),
`--session-length <hours>`, `-O/--offline` (skip pricing network calls).

Parse the active block's `totalTokens` against the resolved limit to derive a percentage.
**Be defensive about the schema** — published examples disagree on the exact envelope
(`{ "blocks": [...] }` vs `{ "type": "blocks", "data": [...], "summary": {...} }`), and field
names have shifted across versions (`tokenCounts.inputTokens` vs flat `inputTokens`).
Write `CcusageProvider` to accept either shape: locate the first array of objects containing
an `isActive: true` element, then read token fields by trying both nestings. Add unit tests
with both fixture shapes committed under `tests/Fixtures/`.

Mark snapshots from this provider with `Source = UsageSource.Ccusage` and
`IsApproximate = true`, and show a small "approximate" badge in the UI so the number is never
mistaken for the real quota figure.

### 4.3 Composition and the threshold decision

`CompositeUsageProvider` tries OAuth first, falls back to ccusage, and returns
`UsageSnapshot.Unavailable` if both fail.

```csharp
public sealed record UsageWindow(double UtilizationPercent, DateTimeOffset? ResetsAt);

public sealed record UsageSnapshot(
    UsageWindow? FiveHour,
    UsageWindow? SevenDay,
    UsageWindow? SevenDayOpus,
    UsageWindow? SevenDaySonnet,
    UsageSource Source,
    bool IsApproximate,
    DateTimeOffset RetrievedAt)
{
    public static UsageSnapshot Unavailable(string reason) => /* ... */;
}
```

The setting `UsageMetric` selects what the threshold compares against:

- `FiveHour` — the 5-hour session window only.
- `SevenDay` — the weekly window only.
- `HighestOfAll` (**default**) — `max(five_hour, seven_day, seven_day_opus, seven_day_sonnet)`.
  This is the safe default: it will not start a run that immediately burns the weekly cap.

**Decision rule.** Run only if `selectedMetric < ThresholdPercent`.
If usage is **unavailable**, honour the `OnUsageUnavailable` setting: `Skip` (default) or `Run`.
Default to `Skip` — silently burning quota because a scrape broke is the worst failure mode.

---

## 5. Launching Claude

### 5.1 Locating the executable

`ClaudeExecutableLocator` resolves, in order:
1. `PilotSettings.ClaudeExecutablePath` if set and the file exists.
2. `claude.cmd` / `claude.exe` / `claude` found on `PATH` (probe `PATHEXT` on Windows).
3. `%APPDATA%\npm\claude.cmd`, `%LOCALAPPDATA%\Programs\claude\claude.exe`, `~/.local/bin/claude`.

On Windows the npm shim is a `.cmd`. Implement and unit test the argument-quoting helper; do not
hand-concatenate arguments — use `ProcessStartInfo.ArgumentList` wherever the target is a real
executable.

> **Correction (measured on .NET 10 / Windows 11, 2026-07-26).** This section originally called
> `cmd.exe /c ""<path>" <args>"` the "safer" route. It is the opposite — **launch the `.cmd`
> directly** with `UseShellExecute = false` and `ArgumentList`, and treat `cmd.exe /c` as a
> fallback only.
>
> Verified with a scratch app plus a generated `.cmd` shim in a directory containing a space:
> - Direct launch of a `.cmd` starts fine; exit code and redirected stdout both work. Nothing has
>   to go "via the shell".
> - Arguments arrive at the batch file with Win32 escaping intact, and `%*` forwards that tail
>   **verbatim** — observed `RAW=[-p "say \"hi\" 100% C:\work\\"]` — so an npm shim's `node … %*`
>   re-parses them correctly.
> - The `cmd.exe /c` route is **lossy**: cmd parses the line before the callee does, so `%VAR%`
>   expands even inside quotes and `\"` means "literal quote" to the callee but "quoting off" to
>   cmd. **A prompt containing a double quote cannot survive that route** — and our prompts are
>   user-editable free text. Batch `%~1` de-quoting does not undo `\"` either; only `%*` forwarding
>   is faithful.
>
> `ProcessArguments.HasCommandShellHazard` flags the characters cmd re-interprets even when quoted
> (`%`, `!`, `"`, CR, LF) so the fallback route can refuse rather than silently corrupt a prompt.
>
> If `ClaudeExecutablePath` is set but the file is gone, fall through to `PATH` (logged, and named
> in the failure reason) rather than failing every run on a stale setting.

### 5.2 The prompt

Build with `PromptBuilder`. The prompt sent to Claude is a single string:

```
/caveman full

Continue work on this project.

Read `plan.md` in this directory. It is the authoritative task list.

Pick up from the first unchecked item in plan.md and make concrete progress.
- After finishing an item, tick its checkbox.
- If an item is ambiguous or blocked, mark it `- [!]` with a one-line note
  explaining the blocker, then move to the next item.
- Prefer finishing one item completely over starting several.

Rules for this session:
- You are running unattended. There is no human available to answer questions.
  Never ask for confirmation, clarification, or approval — decide and proceed.
- Work only on what plan.md already describes. Do not invent new scope.
- Commit your work, using a conventional commit message.
- Run the project's tests before you finish. If they fail, fix them.
- End your run in a clean state: no half-applied edits, everything committed.
```

> **Refinement (2026-07-27): the middle block is a `{planConventions}` token.**
> The three bullets above are the *checkbox* conventions. A milestone plan (§9.1) gets a different
> block telling the run to complete the lowest-numbered undelivered milestone and to write
> `— **delivered YYYY-MM-DD**` plus a `**Status:**` line — the exact markers `PlanParser` reads
> back, so a run that follows the prompt is what makes the tally move. `PromptBuilder` substitutes
> whichever block matches the format preflight resolved; the scheduler pins that onto the settings
> the runner receives, since preflight is the only thing that has actually read the plan file. A
> template with no `{planConventions}` token is left alone, and `SettingsVersion` 2 upgrades a
> stored template that is still the v1 default verbatim — a hand-edited one is never rewritten.

> ## ⚠ Correction: the slash command MUST be namespaced
>
> This section said the prompt begins `/caveman full`. **That is wrong, and it fails silently —
> the worst way anything in this app can fail.** Measured against Claude Code 2.1.220, 2026-07-26:
>
> ```
> claude -p "/caveman full\n\nReply with exactly: FORM-A-OK"
>   → result "Unknown command: /caveman",  is_error FALSE, exit code 0
> claude -p "/caveman:caveman full\n\nReply with exactly: FORM-B-OK"
>   → result "FORM-B-OK",                  is_error false, exit code 0
> ```
>
> With the unnamespaced form the **entire prompt is swallowed** — nothing runs, no tools are used,
> no files change — and Claude Code still reports a successful result with exit code 0. The first
> Phase 3 acceptance run did exactly this: 1 second, $0, zero text deltas, repo untouched, and the
> run recorded as **Success**. An unattended pilot would burn every scheduled slot all night doing
> nothing while its history showed clean runs.
>
> Plugin commands are registered as `<plugin>:<command>`. The live inventory from `system/init`
> contains `caveman:caveman`, `caveman:caveman-review`, `caveman:caveman-commit`, … with **no bare
> alias**. `PromptBuilder.CavemanCommand` is therefore `/caveman:caveman`.
>
> Two defences, because a future plugin rename would reintroduce this:
> 1. `HeadlessClaudeRunner` treats a result beginning "Unknown command:" as a **failed** run, with a
>    detail naming the likely cause. Pinned by a regression test.
> 2. `system/init` publishes the available `slash_commands` and `plugins`, so preflight can verify
>    caveman is present *before* a run rather than discovering it from a wasted night.

Notes:
- The level `full` is the caveman skill's default
  ("drop articles, fragments OK, short synonyms") — pass it explicitly anyway so a future
  default change doesn't alter behaviour. Levels are `lite | full | ultra | wenyan-lite |
  wenyan-full | wenyan-ultra`; expose them all in a dropdown, defaulting to `full`.
- The prompt body above must be **user-editable** in Settings, with a "Reset to default"
  button. Store the template with a `{planFile}` token.
- **The caveman plugin must be installed on the machine** for `/caveman full` to resolve:
  `claude plugin marketplace add JuliusBrussee/caveman && claude plugin install caveman@caveman`.
  `PreflightChecker` verifies this (see §7) and offers a one-click install.

### 5.3 Trusting the folder and running fully unattended

> **This is a hard requirement: a run must never stop and wait for a human.** Two separate
> gates can block it — the *workspace trust dialog* and the *permission prompts*. They are
> different mechanisms and both must be handled.

#### 5.3.1 Pre-trusting the project folder

> **Correction (measured against Claude Code 2.1.220, 2026-07-26).** This section assumes an
> untrusted folder stops a run dead. **It does not, in headless mode.** A full
> `claude -p … --output-format stream-json --permission-mode auto` run in a directory Claude Code
> had never seen completed normally with stdin closed, exit 0, and added **zero** new keys to
> `~/.claude.json` (7 project keys before, 7 after). Headless `-p` does not appear to gate on the
> trust dialog at all on this version.
>
> Consequences: `WorkspaceTrustManager` stays — visible-terminal mode (§5.5) does show the dialog,
> and a future version could reinstate the gate — but trust application is **advisory** and must
> never fail a run. The `TrustBlocked` detect-and-retry loop below is very likely dead code on
> 2.1.220; ship the *detector* (`LooksTrustBlocked`) so a runner can log it, not a retry loop
> around an event we have no evidence occurs. Interactive mode was **not** verified.
>
> The other §5.3.1 warning is fully vindicated: the live config on this machine really does carry
> `C:\code\TrestleBoard` **and** `C:/code/TrestleBoard` as separate keys. Six of seven keys use
> forward slashes, one uses backslashes — so current Claude Code mostly writes forward-slash keys,
> and both forms exist in the wild. The dual-write plus case-insensitive matching is load-bearing.

On first use of a directory, Claude Code asks *"Do you trust the files in this folder?"*.
There is currently **no dedicated flag** to accept only the trust dialog — the feature request
for `--trust-cwd` / a `trustedDirectories` allowlist is open and unimplemented. Trust is
recorded in the **global** `~/.claude.json` (`%USERPROFILE%\.claude.json`) under a per-project
key:

```json
{
  "projects": {
    "C:\\src\\my-project": {
      "hasTrustDialogAccepted": true,
      "hasTrustDialogHooksAccepted": true,
      "hasCompletedProjectOnboarding": true
    }
  }
}
```

Implement `WorkspaceTrustManager` in `Core/Execution/`:

- [ ] Read `~/.claude.json`, locate `projects[<key>]`, and set the three flags above to `true`,
      creating the object if absent. Preserve every other key in the file verbatim — parse with
      `JsonNode`, not into a typed model, so unknown properties survive the round-trip.
- [ ] **Windows path normalization is the known failure mode.** Claude Code has historically
      written this key with mixed separators, so the same folder appears under both
      `C:\\src\\my-project` and `C:/src/my-project` and the trust never takes. Write **both**
      the backslash form (`Path.GetFullPath(dir)`) and the forward-slash form of the key, and
      also match case-insensitively against existing keys before adding a duplicate. Normalize
      away any trailing separator.
- [ ] Back up `.claude.json` to `.claude.json.nightshift.bak` before the first write, and write
      atomically (temp file + `File.Move(overwrite: true)`). This file holds the user's whole
      Claude Code config — corrupting it is the worst thing this app could do. Unit test the
      round-trip against a fixture with unrelated top-level keys and assert they all survive.
- [ ] Never write to `.claude.json` while a `claude` process is running. The `RunGate` already
      serializes runs; take the same gate here.
      **Implemented as optimistic concurrency, not as a refusal.** The obvious reading — refuse to
      write while a `claude` process is live — is unusable: on a developer's machine `claude` is
      essentially always running, so trust would never be applied at all. Instead `ApplyAsync`
      snapshots the file, re-reads it immediately before replacing it, and starts over if it moved
      (up to `MaxWriteAttempts`, then gives up rather than clobbering). A file that never settles
      returns `Failed` with a clear reason and writes nothing.

      **Measured 2026-07-26:** the CLI rewrites this file *continuously during a session*, not only
      on exit — over ~20 minutes of an unrelated live session, `promptQueueUseCount`,
      `cachedGrowthBookFeatures`, `cachedExperimentData`, `cachedGrowthBookFeaturesAt`,
      `pluginUsage` and `clientDataCacheSlots` all changed while every `projects` entry and trust
      flag stayed put. So this is not a tidy-shutdown race we might lose — it is a race we *will*
      lose. Read-modify-write must happen only when no `claude` process is live.

Trust is applied as a **preflight fix action** ("Trust this folder"), shown to the user with the
exact path being trusted, and re-verified before every scheduled run in case the CLI rewrote it.

**Detect-and-retry.** If a headless run exits immediately with no `system/init` event and the
output mentions trust, treat it as `TrustBlocked`: re-apply trust, retry the run **once**, and
if it fails again record `Failed(TrustBlocked)` and surface a banner rather than looping.

**Escape hatch.** `--dangerously-skip-permissions` also bypasses the trust dialog, but it
implies `--permission-mode bypassPermissions` and disables *all* permission checking. Expose it
in Settings as `Trust fallback: skip permissions entirely` — off by default, with a plain
warning that it should only be used on a repo you would be happy to have deleted. Note the known
bug where it has no effect for projects living under a `.vscode` directory path.

#### 5.3.2 Getting straight to work: `auto` permission mode

Claude Code's permission modes are `default` (aliased `manual`), `acceptEdits`, `plan`, `auto`,
`dontAsk`, and `bypassPermissions`. For this app the correct choice is **`auto`**:

> `auto` — everything runs without prompting, with a separate classifier model reviewing each
> action in the background and blocking anything irreversible, destructive, or aimed outside
> your environment. Documented as the mode for *"long tasks, reducing prompt fatigue."*

So the default becomes `--permission-mode auto`, **not** `acceptEdits`. `acceptEdits` only
auto-approves file edits and a short list of filesystem commands — everything else (`npm test`,
`dotnet build`, `git commit`) would still need an explicit `--allowedTools` entry, and the run
aborts when it hits one that isn't listed. That is exactly the "stops and waits" behaviour we're
eliminating.

Implementation notes:

- Set it via the **flag**, not settings. `defaultMode: "auto"` is ignored in project and local
  settings files (`.claude/settings.json`, `.claude/settings.local.json`) — it is only honoured
  in user settings. Passing `--permission-mode auto` sidesteps that entirely.
- **Availability fallback ladder.** Auto mode requires a supported model and, on Team/Enterprise
  plans, Owner enablement. Probe once at preflight with `claude auto-mode config` (exit code 0
  and parseable JSON ⇒ available) and cache the result. If unavailable, fall back to
  `acceptEdits` **with a broad `--allowedTools` list**, and show an amber preflight pill
  explaining the downgrade. Offer `bypassPermissions` as an explicit opt-in third rung.
- **`permissions.ask` rules will hang an unattended run.** Explicit ask rules are evaluated
  before the classifier and always force a prompt, even in auto mode. Preflight must read the
  user's `~/.claude/settings.json` and the project's `.claude/settings*.json`, and warn loudly
  if any `permissions.ask` entries exist, listing them. Same for MCP tools marked
  `requiresUserInteraction`.
- **Optional advanced setting:** expose an `autoMode.environment` editor that writes to
  `~/.claude/settings.json` (always splicing in the literal `"$defaults"` string, so built-in
  rules are preserved). This lets the classifier stop blocking the user's own package registry
  or git host. Ship it collapsed under Advanced with a link to the docs; do not touch
  `allow`/`soft_deny`/`hard_deny` from the UI — writing those without `"$defaults"` silently
  discards every built-in safety rule.

#### 5.3.3 Belt-and-braces: nothing can block the run

- [ ] `--disallowedTools "AskUserQuestion"` — removes the "ask the user a question" tool from
      Claude's context entirely, so it cannot pause to poll a developer who isn't there.
- [ ] Redirect stdin and close it immediately after the process starts, so anything that does
      try to read input gets EOF rather than blocking forever.
- [ ] Never pass `--permission-mode plan`. Plan mode ends by waiting for a human to approve.
- [ ] **Stall detector**, separate from the overall timeout: if no stream event arrives for
      `StallTimeoutMinutes` (default 10), assume a hidden prompt or a hung tool and kill the
      process tree, recording `TimedOut(Stalled)`.
- [ ] Add to the prompt template: *"You are running unattended — there is no human to answer
      questions. Never ask for confirmation or clarification. If you are blocked, record the
      blocker in plan.md as `- [!]` and move to the next item."*
- [ ] Assert in tests that the built argument list always contains `--permission-mode` and never
      contains `--bare` or `plan`.

> **Refinement (2026-07-27): "never plan mode" is a rule about *unattended* runs.**
>
> The plan window's **Edit with Claude** opens an attended session with `--permission-mode plan`,
> and that is not a breach of the bullet above — it is the case the bullet exists to distinguish.
> Plan mode ends by waiting for a human to approve; here the human is sitting in front of it, and
> waiting is the point.
>
> Nothing about the unattended path changes. `PermissionMode` still has no `Plan` member,
> `ToCliValue` still throws outside its three, `ClaudeArgumentsBuilder` still guards, and
> `The_argument_list_never_selects_plan_mode` still passes untouched. The attended launch reaches
> `IClaudeTerminalLauncher` directly with the mode as a **string**, which is why the enum never had
> to be widened — widening it would have put plan mode in the settings dropdown and into the
> scheduler, which is exactly the failure this section prevents.
>
> The session is also not a run: no `RunGate`, no history row, no transcript, and no workspace
> trust write. It bypasses `IClaudeRunner` entirely, because that contract requires a `RunRecord`
> for every outcome and manufacturing one would put a user's own typing in the History tab.

### 5.4 Headless mode (`LaunchMode.Headless`)

```
claude -p "<prompt>"
       --output-format stream-json
       --verbose
       --include-partial-messages
       --permission-mode auto
       --allowedTools "Bash,Read,Edit,Write,Glob,Grep"
       --disallowedTools "AskUserQuestion"
       --model <settings.Model or omitted>
```

(`--allowedTools` is redundant under `auto` but harmless, and it is what carries the fallback
ladder in §5.3.2 when auto mode is unavailable. Keep it.)

with `WorkingDirectory = ProjectDirectory`, `RedirectStandardOutput/Error = true`,
`UseShellExecute = false`.

- **Do NOT pass `--bare`.** Bare mode skips discovery of skills, plugins, hooks, MCP servers
  and `CLAUDE.md`, and skips OAuth/keychain auth — it would break both `/caveman full` and
  subscription authentication. This is the single most likely mistake in this build.
- Permission mode comes from §5.3.2. Default `auto`; fall back to `acceptEdits` +
  broad `--allowedTools` when auto mode isn't available on the account.
- Read stdout as **newline-delimited JSON**. `StreamJsonParser` handles one JSON object per
  line, tolerating partial lines and non-JSON noise. Events to handle:
  - `system` / `init` → capture `session_id`, `model`, `plugins`, `plugin_errors`.
    **If `plugin_errors` mentions caveman, surface a warning** — the skill didn't load.
  - `assistant` / `user` → append to the live transcript pane.
  - `stream_event` with `event.delta.type == "text_delta"` → append `event.delta.text` for
    smooth streaming.
  - `system` / `api_retry` → show a "retrying (attempt N)" indicator.
  - final `result` message → capture `total_cost_usd`, duration, `is_error`, `session_id`.
- Persist `session_id` per project. Offer a setting `ResumeStrategy`:
  `Fresh` (default) vs `Resume` (pass `--resume <id>`). Fresh sessions keep context small,
  which matters when the whole point is conserving quota; `plan.md` carries the continuity.
- Handle process exit codes: `0` success, `143` = SIGTERM/aborted turn, anything else = failure.
- Enforce `MaxRunDurationMinutes` (default 55, so a run cannot outlive its slot). On timeout,
  send SIGTERM-equivalent (`Process.Kill(entireProcessTree: true)`) and record the run as
  `TimedOut`.

> **Inherited session markers are scrubbed.** If NightShift is launched from inside a Claude Code
> session — exactly how it gets developed and tested — the child inherits that session's identity
> variables. Observed consequence: a spawned run reported *"Transcript saving is off — inherited
> `CLAUDE_CODE_CHILD_SESSION` marker"* and silently stopped persisting its transcript, which is the
> one artefact an unattended run exists to leave behind. `ClaudeProcessLauncher` clears
> `CLAUDE_CODE_CHILD_SESSION`, `CLAUDE_CODE_SESSION_ID`, `CLAUDE_CODE_BRIDGE_SESSION_ID`,
> `CLAUDE_CODE_ENTRYPOINT`, `CLAUDE_PID` and `CLAUDECODE` from the child environment.
> `CLAUDE_CONFIG_DIR` and anything else deliberately set are left alone: this removes an accident of
> parentage, not the user's configuration.

### 5.5 Terminal mode (`LaunchMode.VisibleTerminal`)

Spawn a visible, interactive session so you can watch and intervene. Apply trust first (§5.3.1)
so the window doesn't open on a trust prompt, and launch with the same permission mode:

1. Prefer Windows Terminal:
   `wt.exe -d "<projectDir>" cmd /k "<claudePath>" --permission-mode auto`.
2. Fall back to `cmd.exe /k` via `UseShellExecute = true` with `WorkingDirectory` set.
3. Write the prompt to the clipboard **and** to `<projectDir>/.nightshift/next-prompt.txt`,
   then show a toast: "Terminal launched — prompt copied to clipboard." Do not attempt to
   type into the terminal; it is unreliable.
4. In this mode the app cannot capture output. Record the run as `Launched` with no transcript
   and skip the overlap guard's completion tracking after a short grace period.

Make the mode a per-project setting with a radio group in the UI, exactly as specified.

### 5.6 Remote Control

Optional: start the launched session with Claude Code's Remote Control enabled, named after the
repository, so it can be driven from another device.

```
claude --permission-mode auto --remote-control <repository-name>
```

> **Visible-terminal mode only, and this is not negotiable.** Measured against Claude Code 2.1.220
> on 2026-07-27:
>
> - `claude -p "…" --remote-control <name>` **exits 0 and silently ignores the flag.** No error, and
>   no remote-control field anywhere in the `system/init` event — the same "accepted, did nothing"
>   shape as the unnamespaced `/caveman` failure in §5.2.
> - The flag's own help says it "starts an interactive session", and an interactive session needs a
>   real terminal: with stdin redirected, the CLI falls back to `--print` and refuses to start at all
>   (*"Input must be provided either through stdin or as a prompt argument when using --print"*).
>
> So NightShift **does not pass the flag to headless runs**. Passing a flag that looks like it worked
> is worse than not offering the feature. Instead:
> - `TerminalClaudeRunner` appends `--remote-control <name>` (both the `wt.exe` and `cmd /k` paths);
> - `HeadlessClaudeRunner` logs a warning when the setting is on;
> - `PreflightChecker` raises a **warning** pill — never an error, the run itself is fine;
> - Settings shows an inline note when the box is ticked while the launch mode is Headless.

**The session name** comes from `RepositoryName.Resolve`: the last segment of the `origin` remote in
`.git/config` (handling `https://host/owner/repo.git` and `git@host:owner/repo.git`), falling back to
the directory name. It reads `.git/config` directly rather than shelling out to `git` — no process to
spawn, nothing to time out, and it still works when git is not installed. The result is sanitised to
letters, digits, dash, underscore and dot, capped at 64 characters, because directory names with
spaces are ordinary on Windows. `RemoteControlName` overrides it entirely.

---

## 6. Scheduling

`PilotScheduler : BackgroundService`.

- Uses `TimeSpan.FromMinutes(settings.IntervalMinutes)` (default 5, range 5–1440) as the **upper
  bound** between checks, not as a metronome.

> **Correction (2026-07-27): the default interval is 5 minutes, not 60.**
> An hourly tick can miss most of a freed window: quota that came back at 02:05 goes unspent until
> 03:00, and the whole point of this app is that an unused window is gone. A cycle that decides to
> skip costs one usage lookup, and `RunGate` makes a tick that lands during a run skip rather than
> queue, so a short interval is close to free.
>
> Two consequences, both handled rather than lived with:
> - **Reset alignment goes quiet.** Alignment only ever pulls a check *earlier* than
>   `now + interval`, and with a 5-minute interval there is almost never anything to pull. That is
>   fine — a 5-minute poll already catches a reset within 5 minutes — but the anchoring rule below
>   is now mostly relevant to anyone who lengthens the interval.
> - **Skips must not flood the history.** At 5 minutes a blocked pilot would write 288 rows a day
>   into a 200-row index and prune every real run out of it. `RecordSkipAsync` therefore writes a
>   skip only when the previous history row is not already a skip for the same reason; a recorded
>   run resets that. A quiet night is one line, which is what the history was for.

> **Correction (2026-07-27): a finished plan stopped the pilot, but only in the wording.**
> `PreflightChecker.PlanItemsCheck` reports "every checkbox is already ticked" as an **amber** row,
> and §7's rule is that amber never blocks — an untrusted folder or an auto-mode downgrade are
> things an unattended run survives. A finished plan is not. The result was that a plan with
> nothing left launched Claude on every interval, spending a slot and a chunk of the weekly quota
> to be told there was nothing to do. That check's own remarks name this exact cost; it simply had
> no way to act on it, and `CanRun => !HasErrors` was never going to give it one.
>
> The gate is now in the scheduler as `SkipReason.NoWorkLeft`, deliberately **not** by promoting
> the row to red: a finished plan is the good outcome, and a red row would say "Blocked" and send
> the user hunting for a fault that is not there. The status pill reads *Nothing left to do*.
>
> Three cases must still run, and each has a test:
> - **No items at all.** "Not written yet" is not "finished", and a plan whose format is being
>   detected the other way round must not silently park the pilot forever.
> - **Counts unreadable.** Preflight returning null means it could not read the file; refusing to
>   run then would turn a transient read failure into a pilot that never starts again.
> - **Force run.** As at every other gate — the user asking explicitly has already been told on the
>   card that there is nothing to do.
>
> The check sits **after** the usage read rather than before it, so a pilot parked on a finished
> plan still refreshes its gauges and reset times. Freezing those was the other half of what "the
> number never refreshes" looked like.

> **Correction (2026-07-27): pointing the app at a different repository did not re-read the plan.**
> `DashboardViewModel.ApplySettings` copied labels and stopped, and nothing else re-ran preflight
> on a settings change — the only triggers were a scheduler cycle, an applied fix, and the manual
> button. So switching projects left the previous one's tally, pills and plan rows on screen,
> reporting one project's progress under another project's name until the next cycle came round.
>
> Preflight now re-runs when the project directory, the plan file name, or the plan format changes,
> and only then: everything else on the Settings screen saves on a debounce as the user types, and
> re-scanning per keystroke would be a scan per character. The first application is skipped because
> it comes from the constructor, with `InitializeAsync` about to establish the first preflight
> anyway — treating it as a change would scan twice on every launch.
>
> The plan window follows the same change, and **drops an unsaved edit to the old file when it
> does**. Carrying that buffer across would mean a later save writing one project's text into
> another project's plan.

> **Refinement: the schedule anchors itself to quota resets.**
> The interval alone is blind — it can park a check in the middle of a window and leave the pilot
> idle across the moment its quota came back. Every usage snapshot carries `resets_at` for the
> windows it reports, so the next check is placed at:
>
> ```
> next = min(now + interval, earliest known future reset + QuotaResetGraceMinutes)
> ```
>
> Alignment only ever moves a check **earlier** — a reset four hours out never delays the ordinary
> cadence. Rules:
> - When a cycle was blocked by the threshold, the **blocking window's** reset is the anchor, not
>   the earliest reset overall: waking when some other window rolls over would only produce another
>   skip.
> - The anchor is read at startup too, so the first interval after a restart is not blind.
> - `QuotaResetGraceMinutes` (default 1, clamped 0–60) keeps the wake just clear of the boundary;
>   firing exactly on it races the server's clock and reads the window that is about to close.
> - Reset timestamps are absolute, so a snapshot stays useful for anchoring long after it was taken
>   — including across a run that outlasted the window.
> - **`resets_at` is not when your quota comes back.** See the warning below.
> - `AlignToQuotaReset` (default on) turns the whole behaviour off for anyone who wants a strict
>   metronome.
>
> Manual `Run now` / `Force run` never reschedule: a user action must not silently reprogram the
> cadence.
- Persists `NextRunAtUtc` to disk on every tick so a restart doesn't reset the cadence. On
  startup: if `NextRunAtUtc` is in the past, run a tick immediately (subject to `RunOnStartup`,
  default `false`).
- Each tick executes `CycleDecision` logic:
  1. Is the pilot enabled? → else `Skipped(Disabled)`
  2. Is a run already in flight (`RunGate`)? → else `Skipped(AlreadyRunning)`
  3. Preflight passes? → else `Skipped(PreflightFailed, details)`
  4. Fetch usage, forcing past the provider's 60s cache. Unavailable → apply `OnUsageUnavailable`.
     A **force run** takes this path too: it bypasses the *gate*, not the reading.
  5. `metric >= Threshold` → `Skipped(OverThreshold, metric, resetsAt)`
     — and set the *next* check to `min(nextInterval, resetsAt + 1min)` so it resumes promptly
     when the window rolls over.
  6. Otherwise → `Run`.
  7. When the run ends, **fetch usage again** (also forced) and carry it on `CycleCompleted` as
     `UsageAfterRun`. The pre-run snapshot is, by definition, from before Claude spent anything;
     showing it for the rest of the interval is what made the dashboard look frozen. The post-run
     snapshot is also what `ScheduleNextAsync` anchors against, so a window that rolled over
     *during* the run is seen immediately.
- `RunGate` is a `SemaphoreSlim(1,1)`; a run that outlasts the interval simply causes the next
  tick to skip with `AlreadyRunning`. Never queue up runs.

> ## ⚠ `seven_day.resets_at` cannot be trusted as "when quota returns"
>
> Corroborated against two independent community sources (2026-07-26), plus the shape of the
> endpoint used by the `she-llac/claude-counter` browser extension, which reads the same payload
> from `claude.ai/api/organizations/{orgId}/usage`:
>
> - `resets_at` reports **when the oldest tokens age out of the rolling window** — roughly seven
>   days ahead — not when a fresh allocation arrives.
> - The `seven_day` counter actually resets on a **~72-hour cycle**. Measured across three
>   consecutive cycles at **71.9h, 72.6h, 72.5h** (±0.6h).
> - Separately observed: weekly utilization dropping **60% → 2%** while `resets_at` still claimed
>   nine hours in the future. (Reported to Anthropic, closed as "not related to Claude Code".)
> - None of this is documented by Anthropic.
>
> **Consequences for §6's anchoring rule.** The general rule is safe by construction: it only ever
> moves a check *earlier*, so a too-distant `resets_at` is simply never selected. The dangerous path
> is §6.1's rate-limit wait, which is the one place a check moves *later*. Left uncapped, a pilot
> blocked on the weekly window would sleep for up to a week on a timestamp that was wrong by four
> days.
>
> Hence `MaxQuotaWaitHours` (default **6**, clamped 1–72): the wait is `min(resets_at + grace,
> now + MaxQuotaWaitHours)`, applied both in-session and when a quota wait is restored after a
> restart. A usage check costs one cached HTTP call — re-checking early is strictly cheaper than
> being wrong. **Do not "simplify" this cap away.**
>
> Secondary note: the `/usage` endpoint appears to report **whole-number** percentages (our live
> capture: `five_hour` 14, `seven_day` 37), while the stream's `rate_limit_event` carries an
> unrounded fraction (`0.36`) for the same window minutes apart. `claude-counter` makes the same
> observation about the web endpoint being rounded relative to its SSE `message_limit` data. Our
> threshold comparison is therefore accurate to about ±0.5pp when it uses the endpoint; the
> unrounded figure is available mid-run and is already surfaced as
> `ClaudeRateLimitEvent.UtilizationPercent` if finer gating is ever wanted.

### 6.1 Running out of quota *mid-task*

The §4.3 gate only asks whether there is quota **before** a run. The harder case is a run that
starts healthy, works for twenty minutes, and then hits the wall partway through an item. Handled
as its own outcome, `RunOutcome.RateLimited`, because nothing is broken — the window is simply spent.

**Detection** (`RateLimitDetector`), strongest signal first:
1. `rate_limit_event` whose `status` is not one of `allowed`/`allowed_warning`/`ok`, or whose
   utilization has reached 100%. This also carries `resetsAt`, which is the useful part.
2. `api_error_status` containing `429` on the final `result`.
3. Claude Code's own usage-limit sentence in the `result` text or on stderr.

> **False positives are a live hazard, not a hypothetical one.** This plan discusses rate limits on
> nearly every page, so a NightShift run against its own repo produces assistant prose, commit
> messages and diffs full of the words "rate limit", "429" and "usage limit". Matching a bare
> substring would mark healthy runs as quota-blocked and stall the pilot for hours. Detection
> therefore never inspects assistant message text — that is the model talking, not the CLI
> reporting — and the prose pattern is anchored to the whole sentence. A test suite of realistic
> NightShift commit messages pins this.

**Recovery:**
- `RunRecord` gains `RateLimitResetsAt` and `IsResumable`.
- The scheduler waits: `next = RateLimitResetsAt + QuotaResetGraceMinutes`. This is the **one** case
  where a check may be pushed *later* than the interval — the run itself is hard evidence the window
  is spent, so ticking hourly against it would only produce a queue of skips. With no reported reset
  time it falls back to the interval rather than retrying straight into the wall.
- **The interrupted session is resumed, not restarted.** `--resume <session_id>` is passed on the
  next cycle even when `ResumeStrategy` is `Fresh`: choosing "keep context small" was never a
  request to abandon half-finished work. Only sessions that actually completed a tool call are
  resumable — resuming one that achieved nothing just replays the prompt at extra cost.
- Both the pending session and the quota deadline are persisted to `state.json`. A five-hour window
  that ran out at midnight is not back until morning, and the machine may well be restarted in
  between; on startup the pilot restores the pending resume and refuses to launch until the quota is
  actually back, even when `RunOnStartup` is set.
- Expose `RunNowCommand` that bypasses the interval but **still honours the usage check**,
  plus `ForceRunCommand` (shift-click / explicit menu item) that bypasses the usage check with
  a confirmation dialog.
- `StopCurrentRun()` cancels whatever run is in flight, **whoever started it**. §9.1 asks for a
  "Stop run" button but nothing in the original plan let the UI reach a run the *background
  scheduler* owns — cancellation travelled only down the token passed to `RunNowAsync`, which a
  scheduled cycle never has, so the button could only ever have worked for window-initiated runs.
  Each cycle now links its own `CancellationTokenSource`, and a user-stopped run is recorded in
  history ("Stopped by the user.") rather than vanishing.

---

## 7. Preflight checks

`PreflightChecker` returns a list of `(CheckName, Status, Message, FixAction?)`:

| Check | Failure message | Fix action |
|---|---|---|
| Claude CLI found | "Claude Code not found on PATH" | Open file picker to set path |
| `claude --version` runs | "Claude Code failed to start" | — |
| Project directory exists | "Directory not found" | Pick directory |
| `plan.md` exists in it | "No plan.md in project directory" | Create a starter `plan.md` |
| Credentials readable | "Claude login expired or missing" | Show `claude` login instructions |
| caveman plugin installed | "caveman skill not installed" | Run the two `claude plugin` commands |
| **Folder trusted** | "Folder not yet trusted — Claude will stop and ask" | Write the trust keys (§5.3.1) |
| **Auto mode available** | "Auto mode unavailable — falling back to acceptEdits" | Warning only; explains the downgrade |
| **No `permissions.ask` rules** | "Ask rules will pause an unattended run: `<list>`" | Open settings file |
| Directory is a git repo | "Not a git repository — runs won't be recoverable" | Warning only, `git init` offered |
| Working tree clean | "Uncommitted changes present" | Warning only |

Detect the caveman plugin by running `claude plugin list` and looking for `caveman`, with a
filesystem fallback: check for a `caveman` directory under `~/.claude/plugins/`.
Run preflight on startup, on settings change, and before every scheduled run. Show results as a
list on the Dashboard with red/amber/green pills.

---

## 8. Persistence

App data root: `%APPDATA%\NightShift\` (`Environment.SpecialFolder.ApplicationData`).

```
NightShift/
├─ settings.json            PilotSettings, written atomically (temp file + File.Move)
├─ state.json               NextRunAtUtc, last session id, provider health
├─ logs/nightshift-.log    Serilog rolling daily
└─ runs/
   ├─ index.jsonl           one RunRecord per line, append-only
   └─ <runId>.log           full transcript for that run
```

`RunRecord`: `Id`, `StartedAt`, `EndedAt`, `Outcome` (`Success|Failed|TimedOut|Skipped|Launched`),
`SkipReason`, `UsageAtStart` (the whole snapshot), `SessionId`, `CostUsd`, `ExitCode`,
`TranscriptPath`, `Summary` (last assistant text, truncated to 500 chars).

Never write secrets to any of these files. The access token must not appear in logs — add a
Serilog destructuring policy / explicit redaction and a unit test asserting a token-shaped
string never reaches the sink.

---

## 9. UI specification (Avalonia 12, Fluent theme, dark-mode default)

`MainWindow` — 1000×700, min 800×560, `NavigationView`-style left rail with three sections.

### 9.1 Dashboard (default view)

- **Status pill** top-left: `Idle · next run in 42m` / `Running — 3m elapsed` / `Paused` /
  `Blocked: usage 94%`.
- **Two usage gauges** side by side (custom `UsageGauge` control — arc + big % label):
  "Session (5h)" and "Weekly (7d)", each with `resets in 2h 14m` underneath. Colour by
  threshold: green `< 70`, amber `70–<threshold`, red `>= threshold`. Show a small
  "approximate" chip when `IsApproximate`.
- **Project card**: directory path (click to open in Explorer), `plan.md` status
  (`12 of 30 items complete` — parse `- [ ]` / `- [x]` / `- [!]` checkboxes), last run outcome.

> **Refinement (2026-07-27): plans come in two formats, and the gate figure needs to be visible.**
>
> **Plan formats.** Not every plan is a checkbox list. A milestone plan is a sequence of
> `### M7 — Title` headings, delivered ones marked `— **delivered YYYY-MM-DD**` in the heading or
> by a `**Status:**` / `**Post-milestone status …**` body line, blocked ones by `**Blocked:**`.
> Parsed with the checkbox counter such a file reports `0 of 0`, and — worse — the prompt tells the
> run to "tick its checkbox", which it cannot do, so the plan never advances. `PlanParser` handles
> both, `PlanFormat` (`Auto` / `Checkbox` / `Milestone`, default `Auto`) selects, and
> `PromptBuilder` substitutes the matching conventions into the template's `{planConventions}`
> token. Detection prefers checkboxes when a file has both — this repo's own plan.md is exactly
> that shape. Fenced blocks are skipped in both, for the reason the checkbox counter already gives.
>
> **Correction (2026-07-27): "checkboxes win" was too eager, and the rationale was wrong.** The rule
> returned `Checkbox` on the *first* checkbox anywhere in the file, and it was justified by this
> repo's own plan.md — which has no `M<n>` headings at all and so never reached the tie-break. The
> shape it actually broke is the opposite one: a delivered milestone plan that ends with a residual
> list of `- [x]` / `- [!]` open items. A real 16-milestone plan, every milestone delivered, read as
> "4 of 13 items complete, 9 blocked" — and because `PilotScheduler` pins the resolved format onto
> the settings the runner receives, the run was also handed checkbox conventions for a milestone
> plan. **Only an unticked `- [ ]` now settles it**: ticked and blocked boxes are a record of work,
> not work. A fully ticked checkbox plan with no milestone headings still reads as `Checkbox`, so the
> familiar "every checkbox is already ticked" warning is unchanged.
>
> **Everything numbered below the highest delivered milestone counts as delivered.** Plans start
> marking milestones only once shipping begins, so the early ones carry no marker and a literal
> reading calls a nearly finished project barely started. An explicit `**Blocked:**` beats the
> backfill; a milestone someone deliberately flagged is never quietly counted as done.

> **Refinement (2026-07-27): the plan can be read and edited inside NightShift.**
>
> The card printed the tally and offered no way to look at what it was counting. The only route to
> the file was the preflight "Open the plan file" fix, which shells out — and that pill is only
> offered when the plan is empty or finished, so a healthy plan could not be opened from the app at
> all. The plan block on the Project card is now a button, and the "⋯" flyout carries the same
> command for when there is no tally to click on yet.
>
> **`PlanWindow`** is a separate, non-modal, unowned window holding `PlanDocumentView`. Unowned
> deliberately: closing the main window can mean "hide to tray", and on Win32 hiding an owner hides
> everything it owns, so an owned plan window would vanish and never come back. The presenter hides
> and shows it explicitly instead. One window and one `PlanDocumentViewModel`, both singletons —
> there is one project and one plan, and a second window would only be a second unsaved buffer.
>
> **Rendering is hand-rolled** (`NightShift.Core.Markdown` → `Controls/MarkdownView`). No free
> stable markdown renderer targets Avalonia 12, and none of them know this app's vocabulary anyway:
> `- [!]` and `— **delivered …**` are facts the tally already counts, and a general renderer prints
> both as literal text. The reader shares `PlanParser`'s fence walk, checkbox pattern and marker
> constants, so the window and the card cannot disagree about the same file.
>
> **Saving is explicit and byte-preserving.** Never on a timer: this is a document a run is also
> writing to. The file's encoding, byte-order mark, line endings and trailing newline are captured
> on read and re-applied on write, or a two-line edit would report every line as changed at the next
> `git status`. A successful save re-runs preflight so the card's counts move immediately.
>
> **The file is watched, and a conflict is a question, not an action.** A change on disk with a
> clean buffer reloads silently; with unsaved edits it raises an inline notice — never a modal,
> because the change usually arrives mid-run and a modal would trap the user away from the dashboard
> — offering **Keep mine** or **Load theirs**, with what each version counts. Our own save coming
> back through the watcher is recognised by content, not by a timing window.
>
> **The gate figure gets a number and an age.** `HighestOfAll` maximises over four windows but only
> two have gauges, so a gate driven by `seven_day_opus` moved nothing on screen — which reads as a
> value that never refreshes. The provenance line now shows
> `Gate metric: Highest of all — 41% (7d Opus)` via `UsageMetricSelector.SelectDetailed`, plus
> `Updated 2 min ago` recomputed on the one-second tick, and a caption under the gauges lists any
> per-model window the provider reported. Mid-run `rate_limit_event` messages move the matching
> gauge live at no HTTP cost — **display only**, never the gate, and always via the already-scaled
> `UtilizationPercent` rather than the 0–1 wire fraction.
- **Buttons**: `Run now`, `Force run` (in an overflow menu, red, confirmation dialog),
  `Pause` / `Resume`, `Refresh usage`.
- **Live output pane**: monospace, auto-scrolling, streams the parsed transcript. Toolbar:
  `Copy`, `Open log`, `Stop run`. Cap the in-memory buffer at ~5k lines (ring buffer) and keep
  the full text on disk.
- **Preflight strip**: horizontal list of check pills; click a red one to run its fix action.

### 9.2 Settings

Grouped into cards, saved on change with a debounce (no OK/Cancel):

- **Project** — directory picker (`IStorageProvider.OpenFolderPickerAsync`), plan file name
  (default `plan.md`), plan format (`Auto` / `Checkbox` / `Milestone`, default `Auto`).
- **Schedule** — interval (numeric, minutes), run on startup, start with Windows, start minimized to tray.
- **Usage gate** — threshold slider 50–100 (default 90), metric dropdown
  (`Highest of all` default / `Session 5h` / `Weekly 7d`), behaviour when usage is unavailable
  (`Skip` default / `Run`).
- **Claude** — executable path (auto-detected, overridable), launch mode radio
  (`Headless (logged)` / `Visible terminal`), model (blank = default), resume strategy,
  max run duration, stall timeout.
- **Autonomy** — permission mode dropdown (default `auto`, with the availability state shown
  inline), `Auto-trust the project folder` toggle (default **on**) with the exact
  `~/.claude.json` key it will write displayed underneath, allowed-tools text box,
  `Trust fallback: skip permissions entirely` toggle (default off, red warning), and a
  read-only list of any `permissions.ask` rules detected with a "these will hang a run" note.
- **Prompt** — caveman level dropdown (default `full`), multiline prompt template with
  `Reset to default`, and a live preview of the final prompt string.
- **Advanced** — usage provider order, ccusage command override, open app data folder, dry-run
  mode (goes through the whole cycle, logs the exact command line, never spawns Claude),
  collapsed `autoMode.environment` editor (§5.3.2) that always splices in `"$defaults"`.

### 9.3 History

`DataGrid` of runs: started, duration, outcome badge, usage at start, cost, summary.
Selecting a row opens the transcript in a read-only pane with search. Right-click →
`Open transcript file`, `Copy session id`, `Delete`. Filter chips for outcome. Retention
setting: keep last N runs (default 200), prune on startup.

### 9.4 Tray

`TrayIcon` with tooltip `NightShift — next run in 42m`, icon state reflecting
idle / running / blocked / paused. Menu: `Show`, `Run now`, `Pause`, `Quit`.
Closing the window minimizes to tray when "start minimized" is on; `Quit` genuinely exits.
Single-instance enforcement via a named `Mutex`; a second launch surfaces the existing window.

---

## 10. Phases

### Phase 0 — Scaffolding
- [x] `dotnet new sln -n NightShift`; create `NightShift.Core`, `NightShift.Desktop`
      (Avalonia MVVM template), `NightShift.Core.Tests`; wire up `Directory.Build.props`.
- [x] Confirm `dotnet --version` reports a .NET 10 SDK; pin it with a `global.json`.
      (SDK 10.0.302, `rollForward: latestFeature`.)
- [x] Add all NuGet packages from §2. Verify `dotnet build` is clean with warnings-as-errors.
- [x] Set up the generic host in `Program.cs` and register DI for the services in §3.
      (`AddNightShiftCore` in `NightShift.Core/ServiceCollectionExtensions.cs` is the single
      composition entry point; `AppPaths` and `LoggingSetup` landed here since the host needs
      both before anything else can be registered.)
- **Acceptance:** `dotnet build` and `dotnet test` both succeed; an empty Avalonia window shows.

### Phase 1 — Settings and persistence
- [x] `PilotSettings` record with every option in §9.2 and sensible defaults.
      Two decisions worth keeping: (a) properties are `get; set;`, not `get; init;` — the
      System.Text.Json **source generator turns `init` properties into constructor parameters and
      passes `default(T)` for anything absent from the JSON**, silently wiping every default when an
      older settings file is read (`[JsonConstructor]` on a parameterless ctor does *not* change
      this); (b) the provider order is a `UsageProviderPreference` enum rather than a list, so the
      record keeps value equality — a collection property would make every `PilotSettings` compare
      unequal and break settings-change detection in Phase 4.
- [x] `JsonSettingsStore` with atomic writes, schema versioning (`SettingsVersion` int), and
      forward-compatible deserialization. Corrupt files are quarantined to `settings.json.bad[-n]`;
      an *unreadable* file (lock, permissions) is left alone and defaults are used for that session.
- [x] `RunHistoryStore` (append-only JSONL + per-run log files + pruning). Uses a second
      source-generated context (`NightShiftJsonLinesContext`, `WriteIndented = false`) — the
      indented settings context would put a record across many lines and break the JSONL contract.
      `UsageSnapshot`/`UsageWindow` from §4.3 landed here too, since `RunRecord.UsageAtStart`
      needs them; the providers that fill them are still Phase 2.
- [x] Serilog wired to `logs/`, with token redaction. Redaction lives in an `ITextFormatter`
      (`RedactingTextFormatter`), not an enricher — an enricher only sees property values, so a
      secret inside a message-template literal or an exception message would reach the file
      unscrubbed. `StartupTasks` (`IHostedService`) loads settings and prunes history on startup.
- **Acceptance:** unit tests cover round-trip, corrupt-file recovery (backs up and resets to
  defaults rather than crashing), and concurrent-write safety.

### Phase 2 — Usage providers
- [x] `ClaudeCredentialReader` (Windows/Linux file, macOS keychain shell-out), with expiry check.
      Real file also carries `refreshToken`, `refreshTokenExpiresAt`, `scopes`, `subscriptionType`
      and `rateLimitTier` — the last two are worth surfacing in the UI later.
- [x] `OAuthUsageProvider` with the exact headers from §4.1, 60s cache, 401/429 handling,
      exponential backoff. The mandated `Content-Type` header cannot be set on a bodyless GET in
      .NET, so the request carries an empty `ByteArrayContent` to hold it. The 401 latch is keyed to
      a SHA-256 fingerprint of the token (never the token), so re-authenticating clears it without
      an app restart.
- [x] `CcusageProvider` tolerating both documented JSON envelope shapes — and the command itself
      corrected, see the §4.2 note.
- [x] `CompositeUsageProvider` with health tracking and provider-order setting. Provider order is
      the `UsageProviderPreference` enum from Phase 1; a provider that throws is contained and the
      next one still runs, because "no usage" already has a safe defined behaviour.
- [x] Fixture-based unit tests for every parse path, including `null` windows and unknown fields.
      Two fixtures are captured from a real ccusage 20.0.18 run rather than derived from this plan.
- [x] `UsageMetricSelector` — the §4.3 metric selection and the "earliest reset at or above the
      threshold" helper the scheduler needs in Phase 4.
- **Acceptance met 2026-07-26** against a live login: OAuth reported 5h 14% / 7d 37%; with
  credentials removed it fell back to ccusage (41.18%, marked approximate); with both broken it
  returned `Unavailable` carrying both reasons and threw nothing.
- **Acceptance:** with a valid local Claude login, a console harness prints real 5h/7d
  percentages. With credentials removed, it falls back to ccusage and marks the result
  approximate. With both broken, it returns `Unavailable` and does not throw.

### Phase 3 — Execution
- [x] `ClaudeExecutableLocator` + argument-quoting helper (unit tested against nasty paths).
      Quoting is round-tripped through the real `CommandLineToArgvW` via P/Invoke, and a generated
      `.cmd` shim is launched from a directory containing a space. See the §5.1 correction: direct
      launch is the default, `cmd.exe /c` is the lossy fallback.
- [x] `WorkspaceTrustManager` (§5.3.1): both path-separator key forms, JsonNode round-trip that
      preserves unknown keys, backup, atomic write, gated against concurrent runs. Trust is
      **advisory** — see the §5.3.1 correction; headless runs do not gate on it.
- [x] Auto-mode availability probe (`claude auto-mode config`) + the permission-mode fallback
      ladder from §5.3.2, cached with a manual re-probe. Exit code 0 alone is insufficient: an
      older CLI prints usage for an unknown subcommand and also exits 0, so parseable JSON is
      required too.
- [x] `permissions.ask` scanner across user and project settings files.
- [x] `PromptBuilder` with the template from §5.2 and token substitution — **namespaced command**,
      see the correction above.
- [x] `StreamJsonParser` — NDJSON, partial-line tolerant, typed events; fixture tests built from
      two real captured sessions. Five event kinds this plan never mentioned are now typed:
      `system/hook_started`, `system/hook_response`, `system/status`, `rate_limit_event`, and
      `content_block_delta/input_json_delta`.
- [x] `HeadlessClaudeRunner` — spawn, stream, timeout, stall detector, kill-tree, exit-code
      mapping, `session_id` capture, caveman-availability detection, closed stdin, and the
      unknown-slash-command guard. The trust detect-and-retry loop was deliberately **not** built;
      see §5.3.1.
- [x] `TerminalClaudeRunner` — `wt.exe` with `cmd /k` fallback, clipboard + prompt file written to
      `<project>/.nightshift/next-prompt.txt`.
- [x] Dry-run mode that logs the exact resolved command line and exits, in both runners.
- **Acceptance met 2026-07-26 (second attempt).** Against a brand-new git repo Claude Code had
  never opened: no trust dialog, no permission prompt, no hang. 47s, $0.38, exit 0, permission mode
  `auto` confirmed from `system/init`. `hello.txt` created with the right contents, all three
  checkboxes ticked, **three separate conventional commits**, working tree clean, and tools
  `Read, Write, Edit, PowerShell` used — the PowerShell call proves the mode is looser than
  `acceptEdits`. Transcript captured: 239 lines / 105,584 chars. `~/.claude.json` afterwards: only
  the expected 4 keys added (2 directories × 2 separator forms), **all 7 pre-existing project
  entries byte-identical**.
  The *first* attempt is the more valuable result — it failed, silently, and is written up in §5.2.
- **Acceptance:** against a **brand-new, never-before-opened** scratch git repo with a trivial
  `plan.md` (e.g. "create hello.txt, then run the tests"), a headless run completes with **zero
  human interaction** — no trust dialog, no permission prompt, no hang. `hello.txt` exists, the
  checkbox is ticked, a commit was made, the test command actually ran (proving the mode is
  looser than `acceptEdits`), and the transcript contains the streamed output. Deleting the
  project's trust keys from `.claude.json` and rerunning still succeeds unattended. Terminal
  mode opens a real window in the right directory with no trust prompt.

### Phase 4 — Scheduler
- [x] `PilotScheduler` background service, `RunGate`, `CycleDecision`, persisted `NextRunAtUtc`.
- [x] Reset-aware rescheduling — generalised beyond "when blocked" into the anchoring rule in §6.
- [x] Mid-run quota exhaustion: `RateLimitDetector`, `RunOutcome.RateLimited`, wait-for-reset and
      resume-the-interrupted-session (§6.1).
- [x] `PreflightChecker` with all checks and fix actions from §7, behind `IPreflightChecker` so the
      scheduler's gate is testable. Fix actions are **data**, not delegates — Core has no UI.
- **Acceptance met.** A stubbed provider driven through `20, 45, 95, 91, 30, 10` across six
  scheduled ticks produces the right run/skip decision every time and only four launches; a
  blocking run makes the next tick skip with `AlreadyRunning` rather than queueing; and the
  schedule, the pending resume and the quota deadline all survive a restart.
- **Acceptance:** with the interval set to 1 minute and a stubbed usage provider, the log shows
  correct run/skip decisions across ≥5 ticks; a long-running fake run causes `AlreadyRunning`
  skips rather than concurrent launches; restarting the app preserves the schedule.

### Phase 5 — UI
- [x] `UsageGauge` control. Verified by headless Skia renders at twelve sizes in both theme
      variants. `NaN` behaves as unknown, and the unknown state draws a **dotted** ring with a dash
      — never `0%`, which reads as "plenty of quota left".
- [x] Dashboard, Settings, History views + view models, all bound to Core services via DI.
      `TableView` rather than `DataGrid` (Avalonia 12 deprecates the latter for read-only tables),
      and `AvaloniaUseCompiledBindingsByDefault` so a binding typo is a build error.
- [x] Live output streaming with a bounded ring buffer and smooth auto-scroll. `input_json_delta`
      fragments and tool results are excluded, and the duplicate that arrives as both deltas and a
      whole message is suppressed.
- [x] Tray icon, single-instance mutex, start-with-Windows (registry `Run` key, HKCU).
- **Two threading bugs found by driving the real app, not by tests.** Worth recording because the
  symptom in each case was silent or fatal rather than a failing assertion:
  1. `IRelayCommand.NotifyCanExecuteChanged()` raises `CanExecuteChanged` **synchronously on the
     calling thread**, and Avalonia throws when a bound command does that off the UI thread. It fired
     on every startup (aborting dashboard initialisation) and again the instant a manual run ended —
     the latter would have taken the app down.
  2. Rebuilding a bound `ObservableCollection` off the UI thread **corrupts the target**: Avalonia
     posts the notifications, so `Clear()`'s `Reset` lands after the collection has been refilled and
     the queued `Add`s replay. Twelve preflight pills rendered as twenty-four.
  Both bite specifically after `await … ConfigureAwait(false)`. Fixed at source via
  `ViewModelBase.OnUiThread`, not with a view-layer workaround.
- **Acceptance met.** The app was driven through UI Automation: all three sections navigate, gauges
  show live figures with reset countdowns, twelve preflight pills render once each, `Force run`
  confirms with Cancel focused, History loads transcripts and searches them, close-to-tray keeps the
  process alive, a second launch surfaces the existing window, and a settings toggle flipped 200 ms
  before close still reached `settings.json` — proving `ShutdownAsync` flushes the debounce.
- **Acceptance:** every setting in §9.2 round-trips to disk and takes effect without a restart;
  the dashboard reflects a real run end-to-end; the app survives being left running for an hour.

### Phase 6 — Hardening and ship
- [x] Global exception handlers (`AppDomain.UnhandledException`,
      `TaskScheduler.UnobservedTaskException`, Avalonia dispatcher) → log + non-fatal notice.
      The dispatcher handler marks faults **handled**: a dead UI with a live tray icon over a
      still-running scheduler is worse than a degraded window.
- [ ] Retry/backoff audit: nothing can busy-loop when Claude, the network, or the endpoint is down.
- [x] Verify no secret ever reaches a log or the history store (automated test).
      `SecretLeakAuditTests` covers the *routes*, not just the redactor: log file, run transcript,
      history record, `settings.json`, and record `ToString()`. It found a real leak on its first
      run — `ClaudeCredentials` is a record, so its generated `ToString()` printed the access token
      into any interpolated log line, exception message or UI label, none of which pass through the
      log formatter's redactor.
- [x] `dotnet publish -c Release -r win-x64 --self-contained false /p:PublishSingleFile=true`;
      confirm a clean-machine-ish run. Verified: the published exe starts, reads live usage, anchors
      its schedule to the next reset and logs no errors.
      **Publish size needed work.** The bare command emits 133 MB across five files, ~120 MB of it
      dependency symbols (`libSkiaSharp.pdb` alone is 84 MB). The csproj now strips managed and
      native symbols in Release and bundles the native libraries, giving **one 33 MB exe**.
- [x] Write `README.md`: setup, the caveman install commands, the undocumented-endpoint caveat,
      and a "what to do when usage detection breaks" section.
- [x] **Screenshots of the app in action**, captured from the real running window — not mock-ups —
      and committed under `docs/images/`. At minimum the Dashboard (gauges with live figures, a run
      streaming into the output pane, preflight pills), Settings, and History with real rows.
      Sanitize before committing: no usernames in paths, no session ids, no personal project names.
      Reference them from the README so the first screenful of the page shows the thing working.
- [x] **README must open with why you would use this**, in depth and honestly — the problem it
      solves (a subscription's quota resets whether or not you were awake to use it), what it does
      unattended, what it deliberately will not do, and the real risks: it runs Claude Code with a
      broad tool surface against your repo with no human present, and it depends on an undocumented
      endpoint that can vanish. Someone should be able to decide *not* to use it from the README.
- [x] **README must explain how to get it**: download the published exe from Releases, or build from
      source; prerequisites (.NET 10 runtime or the self-contained build, Claude Code installed and
      logged in, the caveman plugin, a git repo with a `plan.md`); and first-run setup ending at a
      green preflight.
- [x] **License: PolyForm Noncommercial 1.0.0** (`LICENSE.md`), verbatim from the canonical source
      with a `Required Notice:` line appended. Free for personal, research, educational, charitable
      and government use; commercial use needs a separate license. This is **source-available, not
      OSI open source** — restricting commercial use is exactly what OSI approval forbids, so the
      README says so rather than claiming otherwise.
- [x] **CI/CD** (`.github/workflows/`). `ci.yml` builds and tests every push and PR on a Windows
      runner — Linux would prove nothing here, since the suite touches Windows paths, the registry
      and process launching. It also **guards the publish output**: the bare publish command emits
      133 MB across five files, so CI fails if more than the single exe appears or if it exceeds
      60 MB. `release.yml` builds, tests, publishes and drafts a GitHub release on a `v*` tag, with
      a SHA256 for the binary.
- **Acceptance:** a fresh clone builds, tests pass, publish produces a working single exe.

### Phase 7 — The plan window (delivered 2026-07-27, `v0.4.0`)

Added after `v0.3.0`, from using the app: the Project card printed `12 of 30 items complete` and
gave you no way to look at what it was counting. The only route to the file was the preflight
"Open the plan file" fix, which shells out to the OS — and that pill is only offered when the plan
has zero items or zero remaining, so on a healthy plan the plan could not be opened from inside
the app at all.

- [x] **`MarkdownLines` — one fence rule, shared.** `PlanParser.ContentLines` is lifted into
      `NightShift.Core.Markdown` and both readers consume it. They have to share it: a window
      rendering thirty checkboxes beside a card reading "28 items complete" is the app
      contradicting itself on one screen, and the user has no way to tell which half is lying.
      Every quirk is preserved deliberately — fence character unmatched, fence length ignored, an
      unclosed fence swallowing the rest of the file. All three differ from CommonMark, and
      tightening any of them changes what the dashboard counts, so it would be a change to the
      product rather than a bug fix. `PlanParserTests` passes **unmodified**, which is the whole
      safety argument for touching it.
- [x] **`MarkdownDocument` + `MarkdownReader`.** Blocks and inlines, immutable, BCL-only, parsed
      off the UI thread. Hand-rolled rather than taken as a dependency for two reasons: no free
      stable markdown renderer targets Avalonia 12 (`Markdown.Avalonia` is alpha on its 12.x line;
      `Avalonia.Controls.Markdown` is the paid Avalonia Pro tier), and neither knows this app's
      vocabulary. `- [!]` is NightShift's own invention and `— **delivered 2026-07-27**` is a fact
      the tally already counts; a general renderer prints both as literal text. Here they are
      `TaskMark.Blocked` and `MilestoneMarker.Delivered`, read through `PlanParser`'s own
      `CheckboxPattern`, `MilestoneHeadingPattern` and marker constants.
      **Non-goals, each with a reason, stated in the file's own docs:** raw HTML (every `<…>` in
      both real corpora sits inside a backtick span — interpreting tags would eat that content);
      setext headings (CommonMark makes a `---` after a paragraph an H2, and these plans use
      standalone `---` as a section rule a dozen times per file and never use setext); four-space
      indented code blocks (they collide with the two-to-four space indentation these plans use for
      nested list items); reference-style links (`[label]:` is one bracket away from `- [x]`);
      footnotes, entity escapes, autolinks, lazy continuation, and quotes nested past one level.
      Anything unrecognised falls through as literal text — the right failure mode for a document
      whose bytes the user is about to edit.
- [x] **`PilotSettings.ResolvePlanPath`.** The repo had no shared plan-path helper;
      `Path.Combine(projectDirectory, planFileName)` appeared twice, both in `PreflightChecker`.
      The window is the third caller and the first writer. It also fixed something dead: the
      `try/catch (ArgumentException)` it replaced had stopped catching anything when .NET Core
      dropped `Path.Combine`'s invalid-character check, so the check is now explicit and tested.
- [x] **`AtomicFile` gains an encoding overload, and `TextFileShape` captures the rest.** A latent
      bug the editor would otherwise have shipped: `File.ReadAllTextAsync` detects and strips a
      byte-order mark while `AtomicFile` always wrote UTF-8 without one, so a BOM'd plan lost three
      bytes on its first save. `TextFileShape` adds line endings and the trailing newline —
      `.gitattributes` here is `* text=auto`, so on Windows the working tree is CRLF while a text
      box normalises everything to LF. Without it, editing two lines reports every line in the file
      as changed at the next `git status`. None of this is visible on screen, which is why it is
      pinned by a byte-equality test rather than by an eye.
      `CreatePlanFileAsync` moved to `AtomicFile` in the same pass — it was the last plain
      `File.WriteAllTextAsync` in Core, and one non-atomic writer beside an atomic one is a latent
      bug now that two code paths write this file.
- [x] **`IClaudeTerminalLauncher`**, extracted from `TerminalClaudeRunner`'s private `TryLaunch`.
      It takes the permission mode as a **string**, which is the whole point — see the §5.3.3
      refinement. The `Func<ProcessStartInfo, bool>` seam moves across intact, so
      `TerminalClaudeRunnerTests` passes untouched. It also gives
      `ProcessArguments.HasCommandShellHazard` its first production caller: the helper was written,
      tested, and then never wired up, so the `cmd /k` line was hand-concatenated without consulting
      it.
- [x] **`Controls/MarkdownView`**, built in code rather than XAML. The structure is recursive and
      heterogeneous — a quote holds blocks, a list item holds blocks, a table cell holds inlines —
      and nine nested templates would each need an `x:DataType` under
      `AvaloniaUseCompiledBindingsByDefault`. There is nothing to bind: the model is immutable and
      the tree is rebuilt whole. Everything visual reuses `App.axaml`'s vocabulary, so a plan looks
      like part of this app rather than a browser embedded in it.
- [x] **The card's plan block is a `Button`.** Not a `PointerPressed` handler — a button is
      tab-stoppable and Space/Enter-activatable for free, which is the entire keyboard story. The
      "⋯" flyout carries the same command for when there is no tally to click on yet.
- [x] **`PlanWindow` — separate, non-modal, and deliberately unowned.** Closing the main window can
      mean "hide to tray", and on Win32 hiding an owner hides everything it owns, so an owned plan
      window would vanish and `ShowMainWindow` would not bring it back. The presenter hides and
      shows it explicitly. One window and one `PlanDocumentViewModel`, both singletons: there is one
      project and one plan, so a second window would only be a second unsaved buffer to reconcile.
- [x] **Explicit save, never autosave.** Settings debounce-save because they are small and
      reversible; a plan is a document a background run is also writing to. `Ctrl+S` saves; `Esc`
      closes when clean and asks when dirty. Shutdown *offers* to save rather than saving — silently
      writing over what a run just committed is the one outcome nobody could undo. A successful save
      re-runs preflight so the card's counts move immediately, skipped while a run is live because
      preflight reads the same file and `CycleCompleted` re-runs it anyway.
- [x] **`IFileWatcher` — the first `FileSystemWatcher` in this repo.** It watches the *directory*
      filtered to the file name, never the file: a watcher bound to a file stops reporting the
      moment that file is replaced by a rename, which is exactly how `AtomicFile` writes and exactly
      what `git checkout` does. Debounced 400 ms on a `TimeProvider` timer, re-armed per event, in
      the shape `SettingsViewModel.ScheduleSave` already uses — writers emit bursts, and
      `AtomicFile`'s temp-write-then-rename is two events by itself. Our own save coming back
      through the watcher is recognised **by content**, not by a suppression window, which makes it
      exact rather than a race.
- [x] **A conflict is a question, not an action.** Clean buffer reloads silently; unsaved edits
      raise an inline notice with **Keep mine** / **Load theirs** and what each version counts.
      Never a modal: the change usually arrives mid-run, and a modal would trap the user in this
      window and away from the dashboard that explains why.
- [x] **"Edit with Claude"** — an attended plan-mode session. See the §5.3.3 refinement for why
      that is not a breach of "never plan mode", and the §9.1 refinement for what it does.
- **Acceptance:** the plan opens from the card, renders both real corpora correctly, round-trips
  byte-for-byte, and a change on disk during an edit never silently wins.

---

## 11. Risks and standing decisions

1. **The usage endpoint is undocumented.** It is the only source of a true quota percentage, so
   we use it — but the whole app must degrade gracefully to ccusage and then to "unavailable →
   skip". Isolate it behind `IUsageProvider` so swapping it costs one class.
2. **Don't burn quota on failures.** Default `OnUsageUnavailable = Skip`. A bug in usage
   detection should cost you nothing, not a night of unattended runs.
3. **`--bare` breaks everything we need.** Skills, plugins, and OAuth auth are all skipped by it.
   Add a code comment at the call site saying so, and an assertion in tests that the built
   argument list never contains `--bare`.
4. **Auto mode is not a sandbox.** Its classifier blocks destructive and exfiltrating actions,
   but unattended Claude with a broad tool surface can still do real damage. Require the project
   directory to be a git repo (warn loudly if not), and put a plainly-worded warning next to the
   Autonomy settings. `bypassPermissions` has no classifier at all — treat that toggle as
   "isolated VMs only", exactly as the docs do.
4b. **We write to the user's global `~/.claude.json`.** That file holds their entire Claude Code
   config. Backup + atomic write + JsonNode round-trip are non-negotiable, and there must be a
   "restore my .claude.json backup" button in Advanced. Trust keys are also the one thing most
   likely to be silently reverted by a CLI update, hence re-verification before every run.
4c. **Auto mode may not be available on every account/model.** Never assume it; always probe,
   always fall back, always tell the user which mode a run actually used (record it on the
   `RunRecord` and show it in History).
5. **Runs can outlast the interval.** The overlap guard plus `MaxRunDurationMinutes` (default
   55) handle this. Never queue.
6. **caveman is a third-party skill.** If it disappears or renames, the prompt still works —
   the `/caveman full` line just becomes a no-op. Detect `plugin_errors` and warn rather than
   fail the run.
7. **Windows npm shims are `.cmd` files.** Process launching must handle this explicitly; it is
   a classic source of "works on my machine" failures.

---

## 12. Verification checklist (do this before declaring done)

- [x] Threshold logic: at 89% it runs, at 90% it skips, at 91% it skips. Unit tested.
- [x] Metric selection: `HighestOfAll` blocks when only the weekly window is hot.
- [x] A real headless run modifies `plan.md` and commits. (Three conventional commits, all
      checkboxes ticked, clean tree, 47s, $0.38.)
- [x] **Zero-interaction proof**, verified twice as required:
      **auto available** — 47s, $0.38, three commits, tools `Read, Write, Edit, PowerShell` (the
      PowerShell call is the proof the mode is looser than `acceptEdits`);
      **auto forced unavailable** — the ladder fell back to `acceptEdits` with the broadened tool
      list, 40.8s, $0.387, three commits, `RunRecord.PermissionModeUsed = AcceptEdits`.
      Neither run prompted, hung, or showed a trust dialog.
- [x] `~/.claude.json` survives a trust write with every unrelated key intact. Verified twice, on
      the real file and on a byte-copy: all pre-existing project entries and every non-`projects`
      value byte-identical, only the expected keys added.
- [x] A deliberately added `permissions.ask` rule is detected by preflight and surfaced before
      the run.
- [x] Stall detector fires: killed and recorded as `TimedOut`, with the fake clock.
- [x] `RunRecord` shows which permission mode was actually used — taken from `system/init`, so it
      is measured rather than assumed.
- [x] **Killing the app mid-run does not leave an orphaned `claude` process.**
      **This failed the first time and the fix is `ProcessJob`.** Measured before: killing only the
      NightShift process left `claude` alive, and it committed to the repository **7s and 19s after
      its supervisor was dead**, with no `RunRecord` written — a silent gap in history while an
      unsupervised agent kept writing. Grandchildren (a `sleep.exe` from a tool call) survived for
      minutes. Root cause: every guard we had — stall detector, `MaxRunDurationMinutes`,
      `Kill(entireProcessTree)` — needs our code to be alive to run. A Win32 job object with
      `JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE` moves that guarantee into the OS. Re-verified after the
      fix: `claude` gone within **1 second**, no descendants, no commits after the kill.
- [x] Deleting `settings.json` while running does not crash the app. Survives because settings are
      cached in memory; the next save recreates the file atomically.
- [x] Usage unavailable → skip, logged clearly, no exception dialogs. **Simulated** (unreachable
      endpoint plus a nonexistent ccusage command), not a real network outage.
- [x] 401 from the endpoint shows the re-authenticate banner and does not retry-storm. Against the
      **live** endpoint with a bogus token: 10 calls produced exactly **1** HTTP request.
- [x] The published exe starts, minimizes to tray, and fires a scheduled run. Tray tooltip read
      "NightShift — next run in 2m"; the scheduled cycle fired by itself and completed in 31s.
- [x] Terminal mode opens a real Windows Terminal window in the project directory with no trust
      prompt, and writes `.nightshift/next-prompt.txt`. (Not in the original checklist; it had never
      been exercised live.)
- [x] **The plan reader agrees with the tally on both real corpora**, not just on fixtures.
      TrestleBoard's 1350-line milestone plan parses in 44 ms — 58 headings, 16 milestones, 6 with
      an explicit delivered marker, 13 task items against a tally of 13. This repo's own 1213-line
      checkbox plan parses in 36 ms — 40 headings, 19 block quotes, 10 fences, 65 task items
      against a tally of 65. Both agree exactly, including that the example checkboxes inside a
      fence are counted by neither.
- [x] **A plan file survives an unedited round trip byte for byte**, across CRLF, LF, a byte-order
      mark, and a file with no trailing newline; and editing one line of a CRLF file changes only
      that line.
- [x] **The "Edit with Claude" seed prompt is free of everything `cmd.exe` reinterprets**
      (`%`, `!`, `"`, CR, LF), asserted for both plan formats. Without this a future rewording
      would silently break the launch: the launcher drops a hazardous prompt rather than mangling
      it, so the session would open bare with no visible failure. The literal `- [!]` marker text
      goes to the clipboard, where nothing reinterprets it.
- [ ] **The plan window has not been driven by a person yet.** Everything above is machine-checked
      and the renderer itself has no test — there is no `Avalonia.Headless` in this repo, and
      adding it is a larger decision than this feature. Open it against both corpora and look at
      the milestone pills, the tables, the block quotes and the `- [!]` marks.

**Two deliberate deviations from the specification, recorded so neither reads as an oversight.**
`MilestoneMarker` has no `InProgress` member: nothing in a heading can produce one, and a value no
code path can reach invites a dead branch. `MarkdownLine` carries an `IsDelimiter` flag the spec's
three-field record omitted; without it the reader has to re-implement the delimiter test, which
duplicates the exact quirk the shared walk exists to centralise.

**One thing knowingly not done.** `ResolvePlanPath` does not assert that the resolved path stays
inside the project directory. Nothing stops `PlanFileName` being `..\..\elsewhere.md`, and the
window will now write there. It is the user's own setting on their own machine rather than a
privilege boundary, so this is a decision rather than a hole — but it is the one place where
gaining a writer changed the stakes of an existing setting.
