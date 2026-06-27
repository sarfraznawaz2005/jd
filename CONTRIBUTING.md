# Contributing to JustDownload

Thanks for your interest in JustDownload — a cross-platform, light & fast, free and open-source download
manager (.NET 8 + Avalonia 11). This guide covers how to build, test, and contribute. The authoritative
engineering rules live in [`CLAUDE.md`](CLAUDE.md); the product scope lives in [`PRD.md`](PRD.md). This
file is the short, practical version.

## Prerequisites

- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) (LTS).
- A git client.
- Optional, for media features: an **LGPL** build of ffmpeg/ffprobe on `PATH` (used as a child process).

## Build, test, format

```bash
dotnet build -c Release                 # build with analyzers on, warnings = errors
dotnet test                             # run the xUnit suite
dotnet format --verify-no-changes       # verify formatting against .editorconfig
dotnet run --project JustDownload.App   # launch the app (dev)
```

## Definition of Done (quality gates)

A change is not finished until **all** of these pass (CLAUDE.md §2):

1. **Build** — `dotnet build -c Release` is clean with analyzers on and warnings treated as errors
   (enforced in `Directory.Build.props`). Zero warnings.
2. **Format** — `dotnet format --verify-no-changes` is clean and `.editorconfig` is honored.
3. **Tests** — unit/integration tests cover the new or changed behavior and are all green.
4. **Smoke check** — where possible, actually run the thing end-to-end (e.g. download a known file and
   verify its SHA-256; confirm `ffprobe` reads muxed output as valid; confirm the app window mounts). If a
   change genuinely can't be smoke-tested yet, say so explicitly rather than skipping silently.
5. **Process cleanup** — kill every process a test/smoke run spawned (ffmpeg/ffprobe, the native host stub,
   fixture servers, headless browsers). Leftover ffmpeg holds file locks and skews the next run. Track
   child PIDs so cleanup is precise; never kill the user's normal browser windows.
6. **Performance budget** — for engine/UI hot-path changes, no regression past the PRD KPIs (cold-start
   ≤ 1.5 s, idle RAM ≤ 90 MB, bundle ≤ 40 MB, ≥ 95% bandwidth utilization). Measure, don't assume.
7. **License check** — no non-permissive dependency introduced (see below).
8. **Commit** — once 1–7 pass, make a focused Conventional Commit.

## Code-quality bar

From CLAUDE.md §1:

- **SOLID, KISS, DRY, YAGNI**, separation of concerns, composition over inheritance. Small,
  single-responsibility classes; no god-files, no circular project references.
- **Strict typing & null-safety** — `<Nullable>enable</Nullable>` everywhere, warnings as errors. No
  `dynamic` to dodge the type system, no `!` null-forgiving operator to silence the compiler. Make illegal
  states unrepresentable (sealed types, enums, records).
- **Pure functions** wherever possible — especially the segmentation math and resume-offset logic, which
  must be deterministic and unit-testable in isolation.
- **`async`/`await` end to end** for all I/O; never block on `.Result`/`.Wait()`. Honor `CancellationToken`
  on every cancellable operation (pause/cancel must be instant).
- **No silent failures** — surface every error through the global error handler. A swallowed `catch {}` is
  a bug.
- **Respect the project boundaries** — `JustDownload.Core` has **zero** Avalonia/UI dependency; the UI and
  native host are thin clients over it (CLAUDE.md §6). ViewModels depend on interfaces, not concretes.

## Testing

- Unit-test pure logic (segmentation/work-stealing, resume offsets, token-bucket throttle, `.m3u8`/`.mpd`
  parsing, category/MIME rules, filename derivation, auth helpers).
- Integration-test the seams with local fixtures (HTTP range server + a no-`Range` server, FTP/FTPS,
  proxies with Basic/Digest/NTLM, self-hosted HLS, DB migrations, OS-keychain storage).
- **Crash-resume integrity is non-negotiable**: randomized kill/power-loss simulation at random byte
  offsets, then assert the resumed file is SHA-256-identical to the reference. Never weaken these to make a
  change pass.
- UI tests via **Avalonia.Headless**, including multi-DPI snapshots at 100/125/150/200/300%.
- Stack: **xUnit** + **FluentAssertions** (7.x, Apache-2.0) + **NSubstitute**. Target **≥ 85% line
  coverage on `JustDownload.Core`**.
- Always tear down spawned processes in `finally` / fixture teardown — never rely on the OS to reap them.

## Dependency & license policy

JustDownload ships under **MIT**, so every dependency must be MIT-compatible (CLAUDE.md §4):

- **Allowed by default:** MIT, Apache-2.0, BSD-2/3, ISC, MS-PL, CC0 / public domain.
- **Forbidden by default — stop and ask first:** GPL/AGPL (strong copyleft), LGPL when *static-linked*,
  source-available / use-restricted licenses, anything requiring a commercial fee.
- **ffmpeg** ships as the **LGPL** build, invoked as a **separate process** — never bundled or statically
  linked as a GPL build.
- Before adding a dependency: confirm the license is allowed, it's actively maintained and reputable, the
  version is pinned, and it doesn't meaningfully hurt the light-&-fast budget. Record it in
  [`docs/THIRD-PARTY-NOTICES.md`](docs/THIRD-PARTY-NOTICES.md). Prefer **no dependency** over a heavy one
  for something the BCL already does.

## Commit convention

Use **[Conventional Commits](https://www.conventionalcommits.org/)** (CLAUDE.md §8). One completed task ≈
one focused commit. Examples:

```
feat(core): dynamic segmentation work-stealing
fix(transport): honor Range fallback on 200 responses
docs: add architecture overview
test(core): crash-resume SHA-256 integrity matrix
```

- Commit locally by default; **pushing requires an explicit ask**.
- Never commit secrets, vendored binaries (ffmpeg), `bin/`/`obj/` output, the SQLite DB, downloaded test
  files, or any process/test leftovers. Keep `git status` clean of stray test output before committing.

## Task workflow (aitasks)

Work is coordinated through the `aitasks` CLI (see CLAUDE.md), which is the single source of truth for the
backlog. At a high level:

1. `aitasks next` / `aitasks list --status ready` — find available work.
2. `aitasks claim <id>` then `aitasks start <id>` — take and begin a task.
3. Add `aitasks note <id> "..."` entries continuously as you make decisions and changes.
4. Verify **every** acceptance criterion with concrete evidence (`aitasks check <id> <n> --evidence "..."`),
   then `aitasks done <id>`. A task is only complete once `done` succeeds — verified criteria or `review`
   status do not count.

Found a flaw in the requirements or a better approach? Raise it (CLAUDE.md §0: ask upfront, critique at the
end) rather than silently diverging — the locked decisions D1–D9 are settled and require sign-off to change.
