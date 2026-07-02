# yt-dlp distribution & licensing

> Source of truth for **how JustDownload obtains yt-dlp** and **why it stays optional and MIT-clean**.
> See also [`THIRD-PARTY-NOTICES.md`](./THIRD-PARTY-NOTICES.md) (the user-facing notice) and locked
> decision **D3** / **CLAUDE.md §4**.

## Policy

In-house media extraction (`IMediaExtractor`) is the default and only path unless the user explicitly
turns on "video capture/detection" in Settings (off by default). Only once that's on, and only after every
in-house extractor has declined, does JustDownload consider falling back to yt-dlp.

- **Never bundled.** No yt-dlp binary lives in this repository or the published packages.
- **Separate process only.** yt-dlp is invoked as a **separate child process** — never statically linked —
  so it remains an independently-licensed binary and the app stays **MIT** (D2), the same posture ffmpeg
  has under D7.
- **Public domain.** yt-dlp is released under the Unlicense (public domain), so — unlike ffmpeg's
  LGPL/GPL split (D7) — there is no license variant to police, only integrity pinning.

## How it is resolved (`IYtDlpProvisioner` → `IYtDlpLocator`)

yt-dlp is resolved lazily, only after the user has opted in and asked for it (via the Settings "Download
yt-dlp" button, or a later fallback attempt, TASK-163):

1. **Locate** (`YtDlpLocator`): an explicitly-configured `YtDlpOptions.YtDlpPath`, then the vendor
   directory, then the system `PATH` — running `yt-dlp --version` on each candidate. Running that command
   successfully *is* the self-validation.
2. **Provision** (`YtDlpProvisioner`): if nothing is located and a **pinned build exists for the current
   platform** (`YtDlpManifest`), download it, **verify its SHA-256**, and move it into the per-user vendor
   directory (`%APPDATA%\JustDownload\yt-dlp` and equivalents). Unlike ffmpeg's zip builds, yt-dlp ships one
   standalone executable per platform, so there is no archive-extraction step — just a fetch, a checksum
   check, and (on Linux/macOS) setting the executable bit.

If no yt-dlp is found and **no pinned source exists** for the platform, provisioning returns `null` and the
Settings UI reports an error — it never silently substitutes an unverified build.

### Integrity

Every download is pinned by **SHA-256**, taken from yt-dlp's own `SHA2-256SUMS` release asset. A corrupt,
truncated, or substituted download fails the check and is discarded (`YtDlpException`, nothing kept).

## Pinned release

Source: [yt-dlp GitHub releases](https://github.com/yt-dlp/yt-dlp/releases) — standalone single-file
executables per platform, pinned to an immutable release tag.

| RID | yt-dlp | Asset | SHA-256 |
|-----|--------|-------|---------|
| `win-x64` | 2026.06.09 | `yt-dlp.exe` | `3a48cb955d55c8821b60ccbdbbc6f61bc958f2f3d3b7ad5eaf3d83a543293a27` |
| `win-arm64` | 2026.06.09 | `yt-dlp_arm64.exe` | `847583f91bb6d26479c1dc9643c2f4b8857a90b40d619da97b0cfabccb9138d0` |
| `linux-x64` | 2026.06.09 | `yt-dlp_linux` | `bf8aac79b72287a6d2043074415132558b43743a8f9461a22b0141e90f16ce66` |
| `linux-arm64` | 2026.06.09 | `yt-dlp_linux_aarch64` | `cabd246445bdfde0eda0dfe68bbe90354be83f3fdbbf077df11a2ea55f41cdbd` |
| `osx-x64` / `osx-arm64` | 2026.06.09 | `yt-dlp_macos` (universal2) | `b82c3626952e6c14eaf654cc565866775ffd0b9ffb7021628ac59b42c2f4f244` |

The exact URLs and hashes are pinned in `JustDownload.Core/Media/YtDlpManifest.cs`. macOS ships one
universal2 binary that covers both `osx-x64` and `osx-arm64`.

## Maintenance — refreshing the pinned release

yt-dlp cuts releases roughly weekly, far more often than ffmpeg's autobuilds, so this pin goes stale fast.
When a fallback attempt or Settings download starts failing integrity checks against an old pin:

1. Note the latest release tag from <https://github.com/yt-dlp/yt-dlp/releases/latest>.
2. Download that release's `SHA2-256SUMS` asset and read off the hashes for `yt-dlp.exe`, `yt-dlp_arm64.exe`,
   `yt-dlp_linux`, `yt-dlp_linux_aarch64`, and `yt-dlp_macos`.
3. Update the tag and hashes in `YtDlpManifest.cs` and the table above.
4. Run the test suite — `YtDlpManifestTests` asserts every entry is SHA-256-pinned over HTTPS.

yt-dlp's source is available at <https://github.com/yt-dlp/yt-dlp>; JustDownload modifies neither the
source nor the released binaries.
