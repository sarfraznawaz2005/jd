# CLAUDE.md — JustDownload

> Cross-platform, extremely light & fast download manager.
> **Stack:** .NET 8 + Avalonia 11 (C#) · **License:** Free & Open Source (MIT) · **Platforms:** Windows, macOS, Linux
> Source of truth for product scope is `PRD.md`. This file is *how we build it*.

---

## 0. Locked decisions

| # | Decision | Notes |
|---|----------|-------|
| **D1** | **.NET 8 (LTS) + Avalonia 11**, MVVM (CommunityToolkit.Mvvm) | Native UI, no WebView |
| **D2** | **Free & open source (MIT)** — no license server, accounts, or telemetry | |
| **D3** | **In-house media extractors / network sniffing — no yt-dlp bundled** | Architecture stays pluggable (`IMediaExtractor`) so yt-dlp can be added in a later major version |
| **D4** | **Modern-minimal UI** (Linear/Arc/Raycast), light+dark, DPI-adaptive | NDM's information architecture, modern skin |
| **D5** | **Headless `JustDownload.Core`** library with **zero Avalonia dependency** | Engine is the testable heart; UI/host are thin clients |
| **D6** | **SQLite** (Microsoft.Data.Sqlite, WAL) for persistence/resume | |
| **D7** | **ffmpeg (LGPL build)** for HLS concat, separate-stream mux, `.ts`→mp4 | Stream-copy by default; never bundle a GPL build (§4) |
| **D8** | **Browser extension ⇄ app via Native Messaging Host** | No open local port by default |
| **D9** | **"Light & fast on slow systems" is the headline promise** | Performance budgets are quality gates (§2) |

If a task forces a change to D1–D9, **stop and get sign-off**.

---

## Constitution

These are rules you must ALWAYS follow:

### Think Before Coding

**Don't assume. Don't hide confusion. Surface tradeoffs.**

Before implementing:
- State your assumptions explicitly. If uncertain, ask.
- If multiple interpretations exist, present them - don't pick silently.
- If a simpler approach exists, say so. Push back when warranted.
- If something is unclear, stop. Name what's confusing. Ask.

### Simplicity First

**Minimum code that solves the problem. Nothing speculative.**

- No features beyond what was asked.
- No abstractions for single-use code.
- No "flexibility" or "configurability" that wasn't requested.
- No error handling for impossible scenarios.
- If you write 200 lines and it could be 50, rewrite it.

Ask yourself: "Would a senior engineer say this is overcomplicated?" If yes, simplify.

### Surgical Changes

**Touch only what you must. Clean up only your own mess.**

When editing existing code:
- Don't "improve" adjacent code, comments, or formatting.
- Don't refactor things that aren't broken.
- Match existing style, even if you'd do it differently.
- If you notice unrelated dead code, mention it - don't delete it.

When your changes create orphans:
- Remove imports/variables/functions that YOUR changes made unused.
- Don't remove pre-existing dead code unless asked.

The test: Every changed line should trace directly to the user's request.

### Goal-Driven Execution

**Define success criteria. Loop until verified.**

Transform tasks into verifiable goals:
- "Add validation" → "Write tests for invalid inputs, then make them pass"
- "Fix the bug" → "Write a test that reproduces it, then make it pass"
- "Refactor X" → "Ensure tests pass before and after"

For multi-step tasks, state a brief plan:
```
1. [Step] → verify: [check]
2. [Step] → verify: [check]
3. [Step] → verify: [check]
```

Strong success criteria let you loop independently. Weak criteria ("make it work") require constant clarification.

### Safety
- NEVER execute destructive commands (rm -rf /, format, drop database) without explicit human approval
- NEVER access files outside the project workspace directory
- NEVER expose API keys, secrets, or credentials in code, logs, or chat
- NEVER make network requests to unknown or unauthorized endpoints
- NEVER modify system files or configurations outside the project

### Code Quality
- Follow the project's existing code style and conventions
- Write code that is readable, maintainable, and well-structured
- Include error handling for all external operations (I/O, network, parsing)
- Do not introduce known security vulnerabilities (OWASP Top 10)
- Prefer simple solutions over clever ones
- **SOLID, KISS, DRY, YAGNI**, separation of concerns, composition over inheritance.
- **Modular:** small, single-responsibility classes with clear boundaries and explicit interfaces.
  No god-files, no circular project references. A method/class doing two jobs gets split.
- Reusable components
- Keep comments minimal — do NOT add JSDoc or docstrings for obvious methods, constructors, getters, or simple utilities. Self-documenting code (clear names, small functions) over verbose comments. Only comment non-obvious logic.
- **Strict typing & null-safety:** `<Nullable>enable</Nullable>` everywhere; treat warnings as errors.
  No `dynamic` to dodge the type system, no `!` null-forgiving operator to silence the compiler, no
  `as`-cast-then-ignore. Make illegal states unrepresentable (sealed types, enums, records).
- **No silent failures** — every error is surfaced through the global error handler.
- Always follow established best practices in your implementations.
  
### Completness
- Finish tasks to the real end-to-end Definition of Done; no stub-marked or `later` comments.

### Communication
- Be honest about limitations and uncertainties
- Report errors and failures or code issues that should be fixed
- Ask for clarification rather than making risky assumptions
- Provide concise, actionable status updates
- After implementing changes or new features, always provide a list of **suggestions or improvements** if possible, even if they differ from the user's original request.

### Collaboration Protocol
- **Ask questions UPFRONT.** If requirements are ambiguous, conflicting, or underspecified, stop and
  ask *before* writing code — especially for anything hard to reverse or wide-impact. Do not guess.
- **Critique at the end of every task.** When the work is done, briefly surface: flaws or gaps in the
  stated requirements, risky assumptions made, and anywhere the requested approach is suboptimal —
  then propose a **better alternative with trade-offs**. Be concrete, not hand-wavy.
- **Don't reinvent solved problems.** If a **free, permissively-licensed, well-maintained, popular**
  library does the job correctly, use it instead of hand-rolling.
  Conversely, don't pull a heavy dependency for something trivial (the app's selling point is being
  light — every dependency is weighed against startup time, RAM, and bundle size).
- **Be honest about state.** If tests fail, say so with the output. If a step was skipped, say it.
  Never report a task as done when a quality gate hasn't actually passed.

### Resource Limits
- Respect token budgets and context limits
- Do not create unnecessary files or bloat the codebase
- Clean up temporary files after use
- Clean up temporary processes you created after checking

## 2. Definition of Done — quality gates (run AFTER each task, before marking done)

A task is not finished until **all** of these pass, with concrete evidence recorded:

1. **Build** — `dotnet build -c Release` with **analyzers on and warnings-as-errors**, zero warnings.
2. **Format** — `dotnet format --verify-no-changes` clean (+ `.editorconfig` honored).
3. **Tests** — unit/integration tests for the new or changed behavior, all green (§3).
4. **Smoke check (where possible)** — actually run the thing and observe it end-to-end: download a
   known file and verify its **SHA-256**; pause/resume and confirm byte-correctness; HLS/mux produces a
   file that **`ffprobe`** reads as valid; the app window mounts and is usable. If a task genuinely
   cannot be smoke-tested yet (pure scaffolding, an interface with no consumer), say so explicitly
   rather than skipping silently.
5. **Process cleanup** — after any test or smoke run, **kill every process the run spawned** so nothing
   is left orphaned: **ffmpeg/ffprobe**, the **Native Messaging Host** stub, any **headless browser**
   used for extension tests, test HTTP/FTP/proxy fixture servers, and dev instances of the app. Verify
   (Task Manager / `tasklist` / `ps`) that none of ours survive. Leftover ffmpeg holds file locks on the
   output, skews the next run, and eats memory — a clean process table is part of done. (§3)
6. **Performance budget (for engine/UI tasks that touch hot paths)** — no regression past the PRD KPIs:
   cold-start ≤1.5s, idle RAM ≤90MB, bundle ≤40MB, ≥95% bandwidth utilization. Measure, don't assume.
7. **License check** — no non-permissive dependency introduced (§4).
8. **Commit** — once 1–7 pass, **commit the change** (§8).

If a gate can't pass, the task is **not done** — note why and either fix it or hand it back with a reason.

## 3. Testing

- **Test as a step right after each task**, not deferred to the end of the project.
- **Unit-test pure logic:** segmentation/work-stealing math, resume-offset calculation, token-bucket
  throttle, `.m3u8`/`.mpd` parsing, category/MIME rules, filename derivation (`Content-Disposition`),
  Digest/NTLM helpers, URL-expiry detection.
- **Integration-test the seams (with local fixtures / Testcontainers):**
  - HTTP range server (and a server that *refuses* `Range`, for fallback),
  - FTP/FTPS server (resume via `REST`),
  - a proxy enforcing **Basic / Digest / NTLM**,
  - self-hosted HLS (plain + AES-128) and separate video+audio → ffmpeg mux,
  - typed IPC across the Native Messaging Host, DB migrations, OS-keychain secret storage.
- **Crash-resume integrity is non-negotiable:** randomized `kill -9` / power-loss simulation at random
  byte offsets, then assert the resumed final file is **SHA-256-identical** to the reference. Never weaken
  or skip these to make a change pass. (Resume correctness is the core promise — like determinism was
  for the source project.)
- **UI tests** via **Avalonia.Headless**: view-model bindings, navigation, and **multi-DPI snapshot
  tests at 100/125/150/200/300%** (KPI K7).
- Prefer **xUnit** (popular, MIT) + **FluentAssertions**; mock with **NSubstitute**. Test behavior and
  contracts, not implementation detail. Target **≥85% line coverage on `JustDownload.Core`**.
- **Always clean up spawned processes** when a test/smoke run ends — including on failure or early exit.
  - Tests that launch ffmpeg / the host / fixture servers / a headless browser must tear them down in a
    `finally` / `IAsyncLifetime.DisposeAsync` / xUnit fixture teardown — never rely on the OS to reap them.
  - Track child PIDs you spawn so cleanup is **precise**. On Windows fall back to image name only for our
    **own** spawned binaries (`taskkill /F /T /IM ffmpeg.exe /IM ffprobe.exe`) — **never** kill the user's
    normal Chrome/Edge windows. A stray ffmpeg holds locks on the frame/output cache and corrupts the next run.

## 4. Library & dependency policy — LICENSE HYGIENE IS LOAD-BEARING

JustDownload ships under **MIT**, so every dependency must be **MIT-compatible**:

- **Allowed by default:** MIT, Apache-2.0, BSD-2/3, ISC, MS-PL, CC0 / public-domain.
- **Forbidden by default — STOP and ask first:**
  - **Strong copyleft (GPL / AGPL)** — would force the whole app to GPL and break the MIT promise (D2).
  - **LGPL static-linked** — allowed only when **dynamically linked / separate binary** (e.g. ffmpeg as a
    child process is fine; statically linking LGPL code into the app is not, without sign-off).
  - "Source-available" / use-restricted licenses, and anything requiring a commercial fee.
- **ffmpeg:** ship the **LGPL build** and invoke it as a **separate process** — never bundle or statically
  link a GPL (`libx264`/`libx265` GPL) build. A GPL system ffmpeg on a dev machine is dev-only.
- **Before adding any dep:** verify (a) license is in the allowed list, (b) it's actively maintained,
  (c) it's reputable/widely-used, (d) the version is pinned, and (e) it doesn't meaningfully hurt the
  light-&-fast budget (§2.6). Record it in the license allowlist so the automated check keeps passing.
- Prefer the **famous, maintained** option over an obscure or abandoned one — but prefer **no dependency**
  over a heavy one for something the BCL already does.

## 5. Project-specific guardrails (engine, privacy, policy — do not violate)

- **Resume correctness contract:** segment math and resume offsets are **pure and deterministic**;
  state is checkpointed atomically (SQLite WAL) so a crash never corrupts or re-fetches data. Every
  resume path is covered by the SHA-256 integrity tests (§3). This is the feature users trust us on.
- **Pause/cancel is instant:** all I/O honors `CancellationToken`; no orphaned sockets or half-written
  files after a pause/cancel.
- **No telemetry, no accounts, no phone-home (D2):** the only network traffic is user-initiated downloads
  and an **opt-in** update check. Nothing else leaves the machine.
- **Secrets at rest:** HTTP/proxy credentials and any tokens are stored via the **OS keychain**
  (DPAPI / macOS Keychain / libsecret) — **never** plaintext in SQLite, logs, or error messages. Logs
  **redact** auth headers, tokens, and signed-URL query strings.
- **No DRM circumvention:** Widevine/PlayReady-protected media is explicitly out of scope. Don't add it.
- **Honest extraction (D3):** in-house extraction is best-effort for hostile sites (YouTube/Facebook);
  degrade gracefully with a clear "couldn't extract" message — never crash, never pretend it worked.
- **Legal posture:** show the one-time "may violate site ToS" notice for media downloads; don't build
  features whose only purpose is evading site protections.
- **Light by default:** lazy-load views and the extractor registry; pooled buffers (`ArrayPool<byte>`),
  `Span`/`Memory` I/O, a single shared `SocketsHttpHandler`. New allocations on hot paths need a reason.

## 6. Architecture rules (.NET 8 + Avalonia, MVVM)

- **Project boundaries are sacred:**
  - `JustDownload.Core` — headless engine (download, transport, proxy/auth, throttle, extractors,
    post-process, persistence). **Zero Avalonia / UI dependency.** Fully unit-testable.
  - `JustDownload.App` — Avalonia UI only (Views/ViewModels). It **never** does network/disk/ffmpeg work
    directly — it drives `Core` through service interfaces.
  - `JustDownload.NativeHost` — the Native Messaging Host stub for the browser extension.
  - `JustDownload.Tests` — xUnit tests. `extension/` — the MV3 browser extension.
- **MVVM, contract-first:** Views bind to ViewModels; ViewModels depend on **interfaces**, not concretes.
  No code-behind business logic. No `any`-equivalent (`object`/`dynamic`) crossing a boundary.
- **Never block the UI thread:** downloads, ffmpeg, and extraction run on background tasks/workers,
  parallelism-capped; marshal back to the UI thread only to update bound state.
- **Centralize DB access:** one data layer over SQLite (WAL). Migrations are **versioned and type-safe**;
  never mutate schema ad-hoc at runtime.
- **Persist what must survive restarts** (download queue, per-segment offsets, settings) in SQLite, so a
  crash or quit loses nothing.
- **DI everywhere** (Microsoft.Extensions.DependencyInjection) so seams are mockable and the engine is
  reusable (a CLI front-end over `Core` is a planned future use).

> **Tooling:** an **`avalonia` skill** is available — invoke it anytime to dig deeper on Avalonia
> (MVVM, XAML, data binding, styling/theming, custom controls, cross-platform deployment). Prefer it
> over guessing on any non-trivial Avalonia question or UI task.

## 7. UI/UX bar

The product promises a **superb, extremely professional, modern-minimal** UI (PRD §2.4). Hold that bar:
- **Visual reference:** interactive HTML mockups live in **`mockups/`** (open `mockups/index.html`).
  They define the intended look & feel — sidebar/list/detail layout, the segment visualization, dialogs,
  and the extension. **Build the Avalonia UI to match them**; if you deviate, say why.
- Avalonia 11 + **FluentTheme**, design-token system (4px grid, one accent color); **light + dark**.
  **Default theme is light**; dark is available and the app may optionally follow the OS theme.
- **Accessible:** full keyboard navigation, focus order, WCAG 2.1 AA contrast.
- **DPI/resolution-proof:** vector icons, layout valid 800×600 → 4K at 100%–300% scaling; virtualize long
  lists for slow hardware.
- Strong typography, spacing, and visual hierarchy; deliberate **empty / loading / error** states.
- The **segment/connection visualization** and per-download detail view (PRD US-15b/c) are signature
  surfaces — polish them.
- No half-finished or placeholder screens shipped as "done."

## 8. Git & repo hygiene

- **Commit after every completed task.** Once a task passes its quality gates (§2), make a small, focused,
  **Conventional Commit** for it (e.g. `feat(engine): dynamic segmentation work-stealing`). One task ≈ one
  commit; don't let finished work pile up uncommitted. (Standing instruction — no need to ask each time.)
- **Pushing requires an explicit ask** — commit locally by default; push only when asked.
- The repo is initialized (branch `master`) with a .NET `.gitignore` and an initial commit
  (`docs: initial project scaffolding`). The commit-per-task rule (§8) is in effect from here on.
- Never commit secrets, vendored binaries (gitignore `vendor/` / the ffmpeg binary), `bin/` `obj/` build
  output, the SQLite DB, downloaded test files, or any process/test leftovers.
- Keep the working tree honest — verify `git status` is clean of stray test output (temp downloads, mp4s,
  fixture data) before committing.

## 9. Documentation

- Keep `docs/` and `PRD.md` in sync when a decision or contract changes; **`PRD.md` (and D1–D9) is the
  source of truth** for scope.
- Reference real user stories / acceptance criteria from the PRD in code comments and commits — don't
  invent fictional task IDs.

---

## Build & run quick reference

```bash
dotnet build -c Release                 # build (warnings = errors)
dotnet test                             # run the test suite
dotnet format --verify-no-changes       # check formatting
dotnet run --project JustDownload.App   # launch the app (dev)
```

<!-- aitasks:instructions -->

## AITasks — Agent Task Protocol (v1.4.6)

You have access to the `aitasks` CLI. This is your single source of truth for
all work in this project. Follow this protocol without exception.

### Environment Setup

Set your agent ID once so all commands use it automatically:
```
export AITASKS_AGENT_ID=<your-unique-agent-id>
```

Use a stable, descriptive ID (e.g. `claude-sonnet-4-6`, `agent-backend-1`).
For machine-readable output on any command, add `--json` or set `AITASKS_JSON=true`.

---

### Discovering Work

```bash
aitasks list                          # All tasks, sorted by priority
aitasks list --status ready           # Only tasks available to claim
aitasks list --status in_progress     # Currently active work
aitasks next                          # Highest-priority unblocked ready task (recommended)
aitasks next --claim --agent <id>     # Auto-claim and start the best task (one-liner)
aitasks show TASK-001                 # Full detail on a specific task
aitasks search <query>                # Full-text search across titles, descriptions, notes
aitasks deps TASK-001                 # Show dependency tree (what blocks what)
aitasks delete TASK-001               # Delete a task (no need to claim first)
```

---

### Starting a Task

**Option 1: One-liner (recommended)**
```bash
aitasks next --claim --agent $AITASKS_AGENT_ID
```
This finds the best task, claims it, and starts it in one command.

**Option 2: Step by step**
1. Find available work:
   ```bash
   aitasks next --agent $AITASKS_AGENT_ID
   ```

2. Claim it (prevents other agents from taking it):
   ```bash
   aitasks claim TASK-001 --agent $AITASKS_AGENT_ID
   ```
   This will FAIL if the task is blocked. Fix blockers first.

3. Start it when you begin active work:
   ```bash
   aitasks start TASK-001 --agent $AITASKS_AGENT_ID
   ```

**Bulk operations:** You can claim, start, or complete multiple tasks at once:
```bash
aitasks claim TASK-001 TASK-002 TASK-003 --agent $AITASKS_AGENT_ID
aitasks start TASK-001 TASK-002 --agent $AITASKS_AGENT_ID
aitasks done TASK-001 TASK-002 TASK-003 --agent $AITASKS_AGENT_ID  # all criteria must be verified
```

**Pattern matching:** Use wildcards to match multiple tasks:
```bash
aitasks claim TASK-0* --agent $AITASKS_AGENTID    # Claims TASK-001, TASK-002, ..., TASK-009
aitasks done TASK-01* --agent $AITASKS_AGENT_ID   # Claims TASK-010 through TASK-019
```

---

### During Implementation

After every significant decision, discovery, or file change:
```bash
aitasks note TASK-001 "Discovered rate limit of 100 req/min — added backoff in src/retry.ts:L44" --agent $AITASKS_AGENT_ID
```

Always note:
- Architectural decisions and why alternatives were rejected
- File paths and line numbers of key changes
- External dependencies added
- Gotchas, edge cases, or known limitations
- If you split a task into subtasks

Creating subtasks:
```bash
aitasks create --title "Write unit tests for auth" --desc "Add unit tests covering all auth edge cases" --ac "All tests pass" --ac "Coverage ≥ 90%" --parent TASK-001 --priority high --type chore --agent $AITASKS_AGENT_ID
```

Editing acceptance criteria (if the requirements change or were worded wrong):
```bash
aitasks update TASK-001 --ac "A brand-new criterion to append"       # add one
aitasks update TASK-001 --set-ac 1="Returns 404 with a JSON error body"  # fix criterion #1 in place
aitasks update TASK-001 --remove-ac 2                                # delete criterion #2
aitasks update TASK-001 --replace-ac $'first\nsecond\nthird'        # replace the whole list
```
Indices are 0-based (matching `aitasks show` and `aitasks check`). Use exactly one of these
flags per call. `--ac` only APPENDS — re-passing existing criteria duplicates them; use
`--set-ac` to correct one criterion or `--replace-ac` to rewrite all of them. After
`--set-ac`, re-run `aitasks check` for that index since editing the wording clears its prior verification.

If you discover your task is blocked by something:
```bash
aitasks block TASK-001 --on TASK-002,TASK-003
```

View dependencies:
```bash
aitasks deps TASK-001    # Shows what this task is blocked by and what it blocks
aitasks show TASK-001    # Also lists "Blocked by" / "Blocks" for a single task
```

When a prerequisite is marked `done`, its dependents are **auto-unblocked**: any task
whose last blocker just completed moves to `ready` automatically (no manual promotion
needed). You can then `aitasks claim` it directly. To override a status by hand, use
`aitasks update <id> --status ready`.

---

### Completing a Task

> **A task is only complete when its status is `done`. Verified criteria, implementation notes, and `review` status do NOT mean the task is done. You have not finished a task until `aitasks done` has succeeded.**

You MUST verify every acceptance criterion before marking done.

1. View all criteria:
   ```bash
   aitasks show TASK-001
   ```

2. Check off each criterion with concrete evidence:
   ```bash
   aitasks check TASK-001 0 --evidence "curl -X GET /users/999 returns 404 with body {error:'not found'}"
   aitasks check TASK-001 1 --evidence "unit test UserService.patch_invalid passes, see test output line 47"
   aitasks check TASK-001 2 --evidence "integration test suite passes: 12/12 green"
   ```

3. Mark done (will FAIL if any criterion is unchecked):
   ```bash
   aitasks done TASK-001 --agent $AITASKS_AGENT_ID
   ```

> The task is only done when `aitasks done` completes successfully. Do not treat a task as finished until you see the done confirmation.

---

### Undoing Mistakes

Made a mistake? Use undo to revert the last action:
```bash
aitasks undo TASK-001    # Undoes the last action (claim, start, done, check, note, etc.)
```

Undoable actions:
- claimed → unclaims the task
- started → reverts to ready status
- completed → reverts to in_progress
- criterion_checked → removes the verification
- note_added → removes the implementation note

---

### Abandoning a Task

If you must stop working on a task, NEVER silently abandon it:
```bash
aitasks unclaim TASK-001 --agent $AITASKS_AGENT_ID --reason "Blocked on missing API credentials — needs human input"
```

---

### Rules

1. **A task is only complete when its status is `done`.** No other status — not criteria-verified, not `review`, not `in_progress` — counts as complete. Your work on a task is not finished until `aitasks done` succeeds.
2. Never mark a task done without checking EVERY acceptance criterion with evidence.
3. Never start a task you haven't claimed.
4. Never silently abandon a task — always unclaim with a reason.
5. Add implementation notes continuously, not just at the end.
6. If a task needs splitting, create subtasks BEFORE marking parent done.
7. Your evidence strings must be concrete and verifiable — not vague affirmations.
8. Always provide --desc, at least one --ac, and --agent when creating a task. All three are required.

---

### Quick Reference

```
aitasks next [--claim] [--agent <id>]       Find best task (optionally auto-claim/start)
aitasks list [--status <s>] [--json]        List tasks
aitasks board                               Live kanban board (visual TUI)
aitasks show <id>                           Full task detail (includes time tracking)
aitasks search <query>                      Search titles, descriptions, notes
aitasks deps <id>                           Show dependency tree
aitasks create --title <t> --desc <d> --ac <c> [--ac <c> ...] --agent <id>   Create a task
aitasks update <id> [--status|--priority|--title|--desc|--type]        Update task fields
aitasks update <id> --ac <text>             Append a new acceptance criterion
aitasks update <id> --set-ac <n>=<text>     Replace a single criterion at index n (0-based)
aitasks update <id> --remove-ac <n>         Remove a single criterion at index n (0-based)
aitasks update <id> --replace-ac <list>     Replace ALL criteria (newline-separated)
aitasks claim <id...> --agent <id>          Claim task(s) - supports patterns like TASK-0*
aitasks start <id...> --agent <id>          Begin work on task(s)
aitasks note <id> <text> --agent <id>       Add implementation note
aitasks check <id> <n> --evidence <text>    Verify acceptance criterion n
aitasks done <id...> --agent <id>           Mark task(s) complete (only valid completion)
aitasks block <id> --on <id,...>            Mark as blocked
aitasks unblock <id> --from <id>            Remove a blocker
aitasks unclaim <id> --agent <id>           Release task
aitasks undo <id>                           Undo last action on task
aitasks delete <id...>                      Delete task(s) - no claim required
aitasks log <id>                            Full event history
aitasks agents                              List active agents
aitasks heartbeat [<id>] --agent <id>       Keep-alive: refresh agent last-seen
aitasks export --format json                Export all tasks
```

**Time tracking:** The `show` command displays duration for in-progress and completed tasks (e.g., "2h 34m" or "1d 5h ongoing").

<!-- aitasks:instructions:end -->
