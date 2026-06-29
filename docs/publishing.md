# Publishing JustDownload (TASK-075)

How we produce the per-OS desktop builds, why the build is shaped this way, and the
NativeAOT evaluation.

## Strategy: framework-dependent + ReadyToRun

JustDownload ships **framework-dependent** (the user's machine supplies the shared
.NET 8 runtime) with **ReadyToRun** (R2R) ahead-of-time images for fast cold-start.
It is **not trimmed and not self-contained**.

Why:

- **Trimming the Avalonia GUI is runtime-risky.** Publishing trimmed surfaces
  unavoidable trim warnings from dependencies we don't control — Avalonia's
  reflection-based `MethodToCommandConverter`, the built-in COM activator
  (`BuiltInComInteropSupport`, required by Avalonia on Windows), and the
  `Avalonia.Controls.DataGrid` and `FluentFTP` assemblies. The trimmer can strip
  members these reach via reflection, so a fully-trimmed GUI can break at runtime in
  ways unit tests don't catch. The PRD risk register anticipates exactly this
  ("Avalonia trimming immaturity → fall back to trimmed ReadyToRun").
- **Framework-dependent ships the same IL the tests exercise.** No trimming means the
  published assemblies are byte-for-byte what the 700+ test suite and the headless
  Avalonia UI tests run against (R2R adds native images but does not change behaviour),
  so there is no "trimmed-only" failure mode to chase per platform.
- **Our own code is trim- and single-file-safe anyway.** Native-messaging JSON uses a
  source-generated `JsonSerializerContext` (no reflection serializer), the column menu
  uses typed bindings (no `ReflectionBinding`), and version resolution avoids
  `Assembly.Location`. So switching to a trimmed/self-contained model later is a
  build-config change, not a code change.

The cost is the shared-runtime prerequisite: users need the **.NET 8 Desktop Runtime**
installed (an OS installer can chain the Microsoft bootstrapper). In exchange the
download is small and the runtime behaviour matches what we test.

## Size budget (K3 ≤ 40 MB)

K3 measures the **compressed installer/download** — the artifact a user actually
fetches — not the extracted folder. Measured here (cross-published from Windows):

| RID        | Extracted | Compressed installer |
|------------|-----------|----------------------|
| win-x64    | ~45 MB    | **19.4 MB**          |
| linux-x64  | ~40 MB    | **17.4 MB**          |
| osx-x64    | ~47 MB    | **20.7 MB**          |
| osx-arm64  | ~50 MB    | **20.2 MB**          |

All installers are well under 40 MB. (Extracted folders are larger mainly because
Avalonia's native rendering libs — SkiaSharp / HarfBuzz — ship alongside the managed
assemblies; they compress well.)

Cold-start of the engine (DI graph + SQLite open + migrations) measures ~220 ms,
far under the 1.5 s K1 budget. The full GUI window-to-interactive time needs a real
display to measure and is a release-QA step per platform.

## How to publish

Per-OS publish profiles live in `JustDownload.App/Properties/PublishProfiles/`
(`win-x64`, `linux-x64`, `osx-x64`, `osx-arm64`). Build one with:

```bash
dotnet publish JustDownload.App -p:PublishProfile=win-x64 -o out/win-x64
```

Or build, package, and budget-check every RID at once (PowerShell 7+, any OS):

```bash
./build/publish.ps1                 # all RIDs → ./artifacts/<rid>/ + JustDownload-<rid>.zip
./build/publish.ps1 -Rids win-x64   # a subset
```

The script zips each bundle into the distributable installer and fails if any
compressed installer exceeds 40 MB. Point the perf probe at an installer to record
the gated bundle KPI:

```bash
dotnet run --project JustDownload.Perf -c Release -- --bundle artifacts/JustDownload-win-x64.zip
```

The Native Messaging Host (`JustDownload.NativeHost`) is co-located into the publish
output by the app's `CopyNativeHostToPublishOutput` target, so each bundle is
self-registering for browser integration (D8).

## NativeAOT evaluation (decision: not now — R2R instead)

NativeAOT was evaluated against the App and rejected for this release:

- **AOT requires full trim-compatibility, which our dependency set does not meet.** AOT
  is strictly stronger than trimming; the same reflection in Avalonia's binding/COM
  paths and in DataGrid/FluentFTP that makes full-trim unsafe also blocks a sound AOT
  build. Avalonia AOT is improving but is not production-proven for our control set
  (notably DataGrid).
- **AOT is inherently self-contained**, which conflicts with the chosen
  framework-dependent model. Adopting AOT would mean reversing that decision and taking
  on the per-platform runtime-QA burden it was made to avoid.
- **R2R already meets the startup budget** (engine cold-start ~220 ms ≪ 1.5 s) without
  AOT's trim/runtime-fidelity risk, so AOT buys little for the GUI today.

**Future option:** the headless `JustDownload.NativeHost` is a tiny stdio-only process
with no Avalonia dependency and is a clean AOT candidate. It is *not* AOT'd now because
host launch latency is not on the user-perceived hot path (the browser spawns it), and
keeping every project on one publish model is simpler. Revisit if host cold-start ever
becomes a concern.
