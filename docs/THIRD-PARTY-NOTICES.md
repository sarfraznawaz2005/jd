# Third-Party Notices

JustDownload is licensed under the [MIT License](../LICENSE). It depends on the third-party components
listed below. Every dependency is required to be MIT-compatible (MIT, Apache-2.0, BSD, ISC, MS-PL, or
public domain) per the dependency policy in [`CLAUDE.md`](../CLAUDE.md) §4. This file is the authoritative
allowlist — new dependencies must be recorded here.

The version numbers reflect the package references in the project files at the time of writing
(`Directory.Build.props` and each `*.csproj`). Each component is distributed under its own license; consult
each project for full license text.

## Runtime dependencies

### `JustDownload.Core`

| Package | Version | License |
|---|---|---|
| Microsoft.Extensions.DependencyInjection.Abstractions | 8.0.2 | MIT |
| Microsoft.Extensions.Logging | 8.0.1 | MIT |
| Microsoft.Data.Sqlite | 8.0.10 | MIT |
| System.Security.Cryptography.ProtectedData | 8.0.0 | MIT |
| Microsoft.Win32.Registry | 5.0.0 | MIT |
| FluentFTP | 54.2.0 | MIT |
| SharpCompress | 1.0.0 | MIT |

`FluentFTP` (MIT) is the FTP/FTPS client for the FTP transport (TASK-033): passive mode, REST resume,
explicit/implicit FTPS, and directory listings. It is pure managed code with no native payload and is only
loaded when a download targets an `ftp(s)://` URL.

`SharpCompress` (MIT) provides `.7z`/`.rar` auto-extraction (TASK-156, extending TASK-135's built-in `.zip`
support). It is pure managed code (~1.5 MB, no transitive package dependencies on `net8.0`) and is read-only
for `.7z`/`.rar` — SharpCompress has no encoder for either format — which matches JustDownload's needs, since
it only ever extracts archives, never authors them. Negligible impact on the K3 ≤ 40 MB bundle budget (current
installers are 17–21 MB compressed; see [`docs/publishing.md`](./publishing.md)).

`Microsoft.Data.Sqlite` embeds the **SQLite** engine (public domain) via **SQLitePCLRaw**
(Apache-2.0 / MIT). `System.Security.Cryptography.ProtectedData` (MIT) wraps Windows DPAPI and is only
invoked under a Windows guard; macOS and Linux use the OS keychain through a child process with no managed
dependency.

### `JustDownload.App`

| Package | Version | License |
|---|---|---|
| Avalonia | 11.3.18 | MIT |
| Avalonia.Desktop | 11.3.18 | MIT |
| Avalonia.Themes.Fluent | 11.3.18 | MIT |
| Avalonia.Controls.DataGrid | 11.3.13 | MIT |
| Avalonia.Fonts.Inter | 11.3.18 | MIT (bundles the **Inter** typeface, SIL Open Font License 1.1) |
| Avalonia.Diagnostics | 11.3.18 | MIT (Debug builds only; never shipped in Release) |
| Microsoft.Extensions.DependencyInjection | 8.0.1 | MIT |

### `JustDownload.NativeHost`

| Package | Version | License |
|---|---|---|
| Microsoft.Extensions.DependencyInjection | 8.0.1 | MIT |

## Test-only dependencies

These are used by `JustDownload.Tests` and are **not** distributed with the application.

| Package | Version | License |
|---|---|---|
| Microsoft.NET.Test.Sdk | 18.7.0 | MIT |
| xunit | 2.9.3 | Apache-2.0 |
| xunit.runner.visualstudio | 3.1.5 | MIT |
| FluentAssertions | 7.2.2 | Apache-2.0 |
| NSubstitute | 5.3.0 | BSD-3-Clause |
| coverlet.collector | 6.0.2 | MIT |

> **FluentAssertions is pinned to the 7.x line on purpose.** Version 8.x relocated to the use-restricted
> Xceed license, which the dependency policy forbids. Do not upgrade past 7.x without sign-off.

## ffmpeg notice

JustDownload uses **ffmpeg** for media post-processing (HLS concatenation, separate-stream muxing, and
`.ts` → `.mp4` remux), per locked decision **D7**.

- ffmpeg is **not** part of this repository, is **not** bundled into the source tree, and is **not**
  statically linked into JustDownload.
- JustDownload invokes ffmpeg/ffprobe as a **separate child process**, communicating over the command line
  and stderr. This keeps ffmpeg a distinct, independently-licensed binary.
- JustDownload targets an **LGPL** build of ffmpeg. It must **never** ship or statically link a **GPL**
  build (e.g. one configured with GPL-only components such as `libx264`/`libx265`). A GPL system ffmpeg on
  a developer's machine is for development only.
- Default media operations are **stream-copy** (no re-encode) — fast and lossless.

### How ffmpeg is obtained (download-on-first-use)

JustDownload ships **no** ffmpeg binary. ffmpeg is resolved lazily, only when a media task needs it:

1. an explicitly-configured path, then a previously-provisioned vendor directory, then the system `PATH`;
2. if none is found, and a **pinned LGPL build** exists for the current platform, JustDownload downloads it
   on first use, verifies it against a **pinned SHA-256**, and extracts it into the per-user vendor directory.

Only LGPL builds are ever fetched. The pinned hash is the guarantee that the downloaded bytes are exactly
the reviewed LGPL artifact, so a GPL build can never be substituted. On platforms with no pinned source
(Linux/macOS, where the package manager already provides ffmpeg), JustDownload uses the system ffmpeg and,
if absent, asks the user to install it — it never silently downloads a non-LGPL build. The pinned builds,
their source URLs, and how to refresh them are documented in [`docs/ffmpeg.md`](./ffmpeg.md).

ffmpeg is a trademark of its respective owners and is licensed under the GNU Lesser General Public License
(LGPL) version 2.1 or later (depending on build configuration). See <https://ffmpeg.org/legal.html> for
ffmpeg's own licensing terms. JustDownload is not affiliated with the ffmpeg project.

## Build tooling (not shipped)

The Windows MSI installer (TASK-076, `build/build-installer.ps1`) is packaged with the **WiX Toolset v5**
CLI and its `WixToolset.UI.wixext` / `WixToolset.Util.wixext` extensions, pinned as repo-local `.NET` tools
(`.config/dotnet-tools.json`) rather than `PackageReference`s. WiX is licensed **MS-RL** (Microsoft
Reciprocal License) — not on the permissive allowlist above — but it is build-time-only tooling: it never
runs alongside JustDownload, is not linked into any assembly, and none of its files are part of the
installed product (the MSI's payload is exactly the win-x64 publish output, listed above). It is therefore
analogous to the compiler/MSBuild toolchain rather than a runtime dependency, and out of scope for the
`licenses.allowlist.json` gate (which enforces every `PackageReference` a shipped project compiles against).
