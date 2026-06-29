# ffmpeg distribution & licensing

> Source of truth for **how JustDownload obtains ffmpeg** and **why it stays MIT-clean**.
> See also [`THIRD-PARTY-NOTICES.md`](./THIRD-PARTY-NOTICES.md) (the user-facing notice) and locked
> decision **D7** / **CLAUDE.md §4**.

## Policy

JustDownload uses ffmpeg for media post-processing (HLS concatenation, separate-stream A/V muxing,
`.ts` → `.mp4` remux). It is invoked as a **separate child process** — never statically linked — so it
remains an independently-licensed binary and the app stays **MIT** (D2).

- **LGPL only.** Only an **LGPL** build of ffmpeg is ever downloaded or shipped. A **GPL** build (e.g.
  one configured `--enable-gpl` / `--enable-nonfree`, pulling in `libx264`/`libx265`) would force the whole
  app to GPL and is forbidden. A GPL ffmpeg on a developer's machine is for local development only.
- **Nothing is bundled.** No ffmpeg binary lives in this repository or the published packages.

## How it is resolved (`IFfmpegProvisioner` → `IFfmpegLocator`)

ffmpeg is resolved lazily, only when a media task first needs it:

1. **Locate** (`FfmpegLocator`): an explicitly-configured `FfmpegOptions.FfmpegPath`, then the vendor
   directory, then the system `PATH` — running `ffmpeg -version` on each candidate.
2. **Provision** (`FfmpegProvisioner`): if nothing is located and a **pinned LGPL build exists for the
   current platform** (`FfmpegManifest`), download it on first use, **verify its SHA-256**, and extract the
   `bin/` payload into the per-user vendor directory (`%APPDATA%\JustDownload\ffmpeg` and equivalents).
   The locator then finds the provisioned binary.

If no ffmpeg is found and **no pinned source exists** for the platform, provisioning returns `null` and the
app surfaces a "please install ffmpeg" message — it never downloads a non-LGPL build to fill the gap.

### Integrity

Every download is pinned by **SHA-256**. The pinned hash — not the transport — is what guarantees the
bytes are exactly the reviewed LGPL artifact: a corrupt, truncated, or substituted download fails the check
and is discarded (`FfmpegException`, nothing extracted). This also makes "no GPL build shipped" enforceable:
the only bytes that pass are the LGPL build whose hash is pinned below.

## Pinned builds

Source: [BtbN/FFmpeg-Builds](https://github.com/BtbN/FFmpeg-Builds) — official **LGPL "shared"** builds
(separate `ffmpeg.exe`/`ffprobe.exe` + libraries), pinned to an immutable dated autobuild tag.

| RID | ffmpeg | Archive | License |
|-----|--------|---------|---------|
| `win-x64` | n7.1 | `ffmpeg-n7.1.5-1-g7d0e842004-win64-lgpl-shared-7.1.zip` | LGPL |

The exact URL and SHA-256 digest are pinned in `JustDownload.Core/Media/FfmpegManifest.cs`.

> **Windows ARM64, Linux, macOS:** no auto-download source is pinned, so the locator's `PATH` search is
> used. On Linux/macOS the system package manager (`apt`, `dnf`, `brew`, …) already provides ffmpeg (LGPL,
> or the user's own choice); JustDownload does not redistribute it. To add `win-arm64` (or any RID),
> follow the refresh process below — the mechanism takes any number of pinned sources.

## Maintenance — refreshing a pinned build

BtbN prunes old dated autobuild tags over time; when a pinned URL starts returning **404**, refresh it:

1. Pick a current **LGPL "shared"** asset from the latest BtbN release for each Windows RID.
2. Download it and compute the SHA-256 (`sha256sum <file>` / `Get-FileHash <file> -Algorithm SHA256`).
3. Update the tag, build string, version, and hashes in `FfmpegManifest.cs` and the table above.
4. Run the test suite — `FfmpegManifestTests` asserts every entry is LGPL and SHA-256-pinned over HTTPS.

LGPL source-offer: the ffmpeg sources corresponding to a pinned build are available from the FFmpeg project
(<https://ffmpeg.org/download.html>) and the BtbN build repository; JustDownload modifies neither.
