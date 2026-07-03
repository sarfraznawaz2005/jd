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
self-registering for browser integration (D8). Publishing with a `RuntimeIdentifier`
now also publishes `JustDownload.NativeHost` itself for that same RID first
(`PublishNativeHostForRid`, TASK-078) — cross-publishing e.g. `linux-x64` from a
Windows dev box used to bundle the *build machine's* apphost (a Windows `.exe`)
inside the Linux package, which would have silently broken native messaging on
Linux/macOS installs since the host is never executable there. Verified by
re-publishing both `linux-x64` (bundled host is now a real ELF binary) and `win-x64`
(still a PE `.exe`, no regression) and confirming the file signatures directly.

## Linux packaging: AppImage, .deb, .rpm (TASK-078)

`build/build-linux-packages.ps1` publishes `linux-x64` and packages it three ways.
Unlike `publish.ps1`, this script only runs on Linux — it shells out to
`appimagetool`, `dpkg-deb`, and `rpmbuild`, none of which exist on Windows:

```bash
./build/build-linux-packages.ps1                   # AppImage + deb + rpm
./build/build-linux-packages.ps1 -Formats deb,rpm   # a subset
```

Supporting assets live in `build/linux/`: the freedesktop `.desktop` entry, a
`hicolor`-theme icon set (16 through 256px, rendered from the same brand mark as
the browser extension — see `build/linux/gen-app-icon.js`, which reuses
`extension/scripts/gen-icons.js`'s rasterizer instead of duplicating it), and the
`.spec` template for the rpm build.

**Host manifest registration (AC2)** needs no packaging-time code: every bundle
already carries `JustDownload.App`, which calls `INativeHostInstaller.Install()` on
first launch and writes the per-browser manifests to the correct per-user Linux
paths (`NativeHostManifestLocations`, unit-tested independently of this script).
Deregistration on package removal is wired into the `.deb`'s `prerm` and the
`.rpm`'s `%preun`, both calling `JustDownload.App --uninstall-cleanup` — the same
switch the Windows MSI's uninstall custom action already uses
(`JustDownload.App/Services/UninstallCleanup.cs`), so all three platforms share one
cleanup code path.

**What is and isn't verified.** This was built and reviewed on a Windows dev
machine with no Linux install available, so:

- The `linux-x64` publish step, the RID-aware NativeHost fix, and the icon
  generator were run and verified directly (file signatures, PNG validity, a full
  `dotnet build`/`dotnet test`/`dotnet format` pass — see TASK-078 notes).
- The AppImage/deb/rpm assembly logic (AppDir layout, `DEBIAN/control`, the rpm
  spec) was written and reasoned through carefully but **has not been executed**
  — no `appimagetool`, `dpkg-deb`, or `rpmbuild` available here. Acceptance
  criteria AC0 ("AppImage runs on a clean distro") and AC1 ("deb and rpm
  install/uninstall cleanly") are **not verified** and must be run on real Linux
  (or Linux CI) before being signed off.

**A real tension worth a decision, not a silent choice:** JustDownload ships
framework-dependent (above), so the AppImage still requires the user to have the
.NET 8 Runtime installed — it is *not* the traditional zero-dependency "runs on any
Linux with nothing preinstalled" AppImage. The `.deb`/`.rpm` sidestep this cleanly
by declaring `Depends`/`Requires: dotnet-runtime-8.0` and letting the package
manager install it. If a truly standalone AppImage is wanted, the fix is a
self-contained (not framework-dependent) publish for that one target specifically
— bigger (self-contained .NET on Linux typically extracts to 70–100 MB) but
dependency-free. Left as a follow-up rather than decided unilaterally here.

## macOS packaging: .app bundle, .dmg, notarization (TASK-077)

`build/build-macos-packages.ps1` publishes `osx-x64`/`osx-arm64`, assembles a
`JustDownload.app` bundle, code-signs it if a Developer ID is supplied, wraps it in
a drag-to-Applications `.dmg`, and notarizes+staples that dmg if a notarytool
keychain profile is supplied. Like the Linux script, it only runs on the OS it
targets — it shells out to `codesign`/`hdiutil`/`xcrun`, none of which exist on
Windows:

```bash
./build/build-macos-packages.ps1
./build/build-macos-packages.ps1 -Rids osx-arm64
./build/build-macos-packages.ps1 -SigningIdentity "Developer ID Application: Name (TEAMID)" -NotarizeProfile jd-notary
```

Supporting assets live in `build/macos/`: `Info.plist` (`{{VERSION}}` substituted
from `Directory.Build.props`, same single-source-of-truth pattern as the Windows
installer's `ProductVersion`) and `Entitlements.plist` for the hardened-runtime
flags .NET needs to launch signed (the CLR still JITs even with ReadyToRun — see
the comment in that file). `CFBundleIdentifier` is `com.justdownload.app`, matching
the id `MacOsAutostartService` already uses for the per-user LaunchAgent label
(TASK-155), so every macOS-facing identifier in the app agrees.

Signing and notarization are both-or-neither in practice: Apple's notary service
rejects unsigned bundles, so omitting `-SigningIdentity` skips notarization too
regardless of `-NotarizeProfile` — this repo carries no Apple Developer ID
certificate, so the default run signs and notarizes nothing (same honest-by-default
posture as the Windows installer script's certificate handling).

**Host manifest registration (AC2)** is the same free ride as Linux: `Install()`
runs at first launch and resolves the correct
`~/Library/Application Support/<browser>/NativeMessagingHosts` path
(`NativeHostManifestLocations`), independently unit-tested. Unlike the
`.deb`/`.rpm`/MSI, there is **no scripted uninstall hook** for the dmg-drag-install
model — a `.app` has no package manager to hang a `prerm`/`%preun`/custom-action
off. Dragging `JustDownload.app` to the Trash leaves the (tiny, harmless) manifest
files behind; the browser just fails to connect to a host that's no longer there.
Worth a decision if it matters enough to build a real uninstaller (e.g. a small
`.pkg` with a postinstall/preremove script) — not built here since the task
description scoped this to bundle + dmg + notarization, not a full installer.

**What is and isn't verified.** Built and reviewed on a Windows dev machine with no
macOS install available, so:

- The `osx-x64`/`osx-arm64` publish step and the RID-aware NativeHost fix (shared
  with TASK-078) were run and verified directly: both bundle a real Mach-O
  `JustDownload.NativeHost` (magic `CF FA ED FE`), not the Windows apphost. The
  `Info.plist`/`Entitlements.plist` substitution and XML well-formedness were also
  verified directly (PowerShell's `[xml]` parser, both files parse cleanly with
  version placeholders fully substituted).
- The bundle/codesign/dmg/notarize logic itself was written and reasoned through
  carefully but **has not been executed** — no macOS, `codesign`, `hdiutil`, or
  `xcrun` available here. All 3 acceptance criteria (notarized dmg opens on a clean
  Mac, drag-drop from the mounted volume, host manifests registered end-to-end
  through this packaging) are **not verified** and need a real Mac (or macOS CI
  runner, ideally with an actual Developer ID + notarytool profile) before sign-off.
- No `AppIcon.icns` is set (`CFBundleIconFile` omitted) — the bundle will show a
  generic icon. Every platform currently ships with no app icon at all (only the
  browser extension has committed brand-mark PNGs); building one was left out here
  as out-of-scope for a packaging task, not silently forgotten.

## Auto-update asset coverage (TASK-172) vs. the release workflow (a known gap)

`UpdateChecker.ResolveInstallerAssetName` (`JustDownload.Core/Updates/UpdateChecker.cs`)
now resolves a per-OS/arch installer asset name — the Windows bootstrapper, the
matching-arch macOS `.dmg`, or the Linux `.AppImage` (deb/rpm are published but
deliberately not auto-applied — see `LinuxUpdateApplier`'s doc comment) — and
`MacOsUpdateApplier`/`LinuxUpdateApplier` know how to launch them. **But
`.github/workflows/release.yml` still only builds on `windows-latest` and only
collects `.msi`/`.exe` into the release** (it never invokes
`build-macos-packages.ps1`/`build-linux-packages.ps1`, and its checksums.txt only
ever covers the Windows asset). So today, a macOS/Linux user's `CheckAsync` will
always resolve a name that isn't in the release yet and cleanly fall back to
`AvailableForManualDownload` — the C#-side auto-apply is real and tested, but
nothing will actually auto-apply on macOS/Linux until the release workflow is
widened to publish those assets too. That's a CI/CD change, out of this task's
acceptance criteria, and deliberately not made unilaterally here (a release
workflow is shared, visible infrastructure).

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
