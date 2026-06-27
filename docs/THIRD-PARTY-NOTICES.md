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

ffmpeg is a trademark of its respective owners and is licensed under the GNU Lesser General Public License
(LGPL) version 2.1 or later (depending on build configuration). See <https://ffmpeg.org/legal.html> for
ffmpeg's own licensing terms. JustDownload is not affiliated with the ffmpeg project.
