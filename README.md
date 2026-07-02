# JustDownload

**JustDownload is a cross-platform, extremely light & fast, free and open-source download manager** for
Windows, macOS, and Linux — built on .NET 8 and Avalonia 11. Its defining promise is staying fast and
frugal even on slow or older hardware: a dynamic segmentation engine saturates your available bandwidth,
downloads are organized automatically, and the whole thing ships as a small native app with no Electron,
no accounts, no telemetry, and no license server.

> Status: early development. The headless engine's **foundation and data layer are implemented**; the
> download engine, media extractors, full UI, native messaging host, and browser extension are **planned /
> in progress**. See [Project status](#project-status) for an honest breakdown.

---

## Why JustDownload

Existing download managers tend to be heavy (Electron/Java, 150–300 MB idle RAM), paid/closed-source, or
visually dated. JustDownload aims to be the fast, modern, free, cross-platform option that runs well on a
five-year-old laptop.

Performance is treated as a hard requirement, not an aspiration. The target KPIs:

| KPI | Target |
|---|---|
| Cold-start to interactive | ≤ 1.5 s |
| Idle RAM (no active downloads) | ≤ 90 MB |
| Bundle size per platform | ≤ 40 MB |
| Bandwidth utilization vs. raw `curl` | ≥ 95% |
| Resume integrity after a hard crash (SHA-256 match) | 100% |

---

## Features

The feature set below is the product scope defined in [`PRD.md`](PRD.md). Items are marked by current
build state. Nothing here is claimed as shipping until it actually is.

### Download engine (planned)
- **Dynamic segmented downloads** — split into N connections (default 8, configurable 1–32) with
  **work-stealing**: when a segment finishes early, its idle connection re-splits the largest remaining
  segment instead of sitting idle.
- **Pause / resume / crash recovery** — per-segment byte offsets checkpointed atomically to SQLite, so a
  crash or power loss never re-fetches or corrupts data; resumed files are SHA-256-identical to the source.
- **Live bandwidth limiting** — global and per-download speed caps, adjustable mid-download.
- **Live connection control** — add or drain connections on a running download without restarting it.
- **Graceful fallback** — single-connection mode when a server does not honor HTTP `Range`.

### Protocols, proxies & auth (planned)
- **HTTP / HTTPS** (TLS 1.2+, redirects, cookies, custom headers, `Content-Disposition` filenames).
- **FTP / FTPS** with passive mode and `REST`-based resume.
- **Proxies** — HTTP and SOCKS4/5 (SOCKS5 with remote DNS), per-download or global, toggleable live.
- **Authentication** — Basic, Digest (RFC 7616), and NTLM for both origin and proxy; credentials stored in
  the **OS keychain**, never in plaintext.

### Media (planned)
- **HLS** (`.m3u8`) download + merge, including AES-128 segment decryption and variant/quality selection.
- **DASH** (`.mpd`) and the "separate video + audio streams" case — downloaded in parallel and **muxed**
  with ffmpeg (stream-copy, no re-encode) into a single `.mkv`/`.mp4`.
- **Remux** `.ts` → `.mp4` and configurable default video quality / container.
- Pluggable in-house `IMediaExtractor` registry. Extraction is **best-effort** for hostile sites
  (YouTube/Facebook) — see [Legal & acceptable use](docs/LEGAL.md). No DRM circumvention.

### App & experience (planned)
- **Auto-organization** by status (Complete / Incomplete) and type (Video, Audio, Document, Compressed,
  Program, Image, Other) via extension + MIME rules.
- **Modern-minimal UI** (Linear/Arc/Raycast reference): category-tree sidebar, downloads list, per-download
  detail view, command palette, light + dark themes, DPI-adaptive from 800×600 to 4K.
- **Live segment/connection visualization** — a signature surface showing each parallel connection filling
  in live.
- **Browser extension** (Chrome/Edge/Firefox, MV3) — send links, sniff media, floating download button,
  per-site blacklist — talking to the app over a **Native Messaging Host** (no open local port by default).

Non-goals for v1.0 include bundling yt-dlp into the app (it's available only as an optional, user-enabled,
downloaded-on-demand fallback — D3), torrent/P2P, mobile apps, telemetry/accounts, and any re-encoding
beyond stream-copy remux. See [`PRD.md` §2.3](PRD.md).

---

## Tech stack

JustDownload is built on a set of locked decisions (CLAUDE.md D1–D9):

| Area | Choice |
|---|---|
| Runtime | **.NET 8 (LTS)** |
| UI | **Avalonia 11** + CommunityToolkit.Mvvm (MVVM, native, no WebView) |
| Engine | **`JustDownload.Core`** — headless class library with **zero Avalonia dependency** |
| Persistence | **SQLite** (Microsoft.Data.Sqlite, WAL) for queue, segment offsets, settings, resume |
| Secrets | **OS keychain** — DPAPI (Windows) / Keychain (macOS) / libsecret (Linux) |
| Media post-processing | **ffmpeg** (LGPL build) invoked as a **separate child process** |
| Browser integration | MV3 extension ⇄ app via **Native Messaging Host** |
| License | **MIT** (app) — ffmpeg is a separate LGPL component, see below |

The repository is split along strict project boundaries:

- `JustDownload.Core` — the headless engine (download, transport, proxy/auth, throttle, extractors,
  post-process, persistence). Fully unit-testable, no UI dependency.
- `JustDownload.App` — the Avalonia UI (Views/ViewModels). Drives `Core` through interfaces only.
- `JustDownload.NativeHost` — the Native Messaging Host stub for the browser extension.
- `JustDownload.Tests` — the xUnit test suite.
- `extension/` — the MV3 browser extension.
- `mockups/` — interactive HTML mockups of the intended UI.

See [`docs/ARCHITECTURE.md`](docs/ARCHITECTURE.md) for the full picture.

---

## Project status

This is an honest snapshot of what exists today, not the roadmap.

**Implemented (`JustDownload.Core` foundation + data layer):**
- Solution scaffolding and project boundaries (Core / App / NativeHost / Tests / extension).
- Dependency-injection composition root and service registration.
- Logging pipeline with **secret redaction** (auth headers, tokens, signed-URL query strings) and a
  global error handler.
- **SQLite data layer**: WAL-mode connection factory, **versioned, type-safe schema migrations**, and
  repositories for downloads, segments, settings, and the site blacklist.
- **Typed settings service** with defaults, change notifications, and persistence.
- **OS-keychain secret storage** abstraction with DPAPI / macOS Keychain / libsecret backends.

**Planned / in progress (not yet built):**
- The download engine itself (segmentation, work-stealing, transport, throttle, pause/resume).
- Proxy/auth, FTP, and the HLS/DASH extractors + ffmpeg post-processing.
- The Avalonia UI views and view-models (the App project is currently a shell).
- The Native Messaging Host logic and the MV3 browser extension (currently placeholders).

Work is tracked in the `aitasks` backlog (`.aitasks/`); the intended look & feel lives in the mockups
(see below). The phased roadmap is in [`PRD.md` §5](PRD.md).

---

## UI mockups

The interactive HTML mockups in [`mockups/`](mockups/) define the intended look & feel — sidebar/list/detail
layout, the segment visualization, dialogs, settings, and the extension popup. Open
[`mockups/index.html`](mockups/index.html) in a browser to explore them. The Avalonia UI is built to match
these.

---

## Build & run

Requires the [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0). ffmpeg is only needed for
media (HLS/mux) features and is invoked as a separate process when present.

```bash
dotnet build -c Release                 # build (warnings = errors)
dotnet test                             # run the test suite
dotnet format --verify-no-changes       # check formatting
dotnet run --project JustDownload.App   # launch the app (dev)
```

See [`CONTRIBUTING.md`](CONTRIBUTING.md) for the full quality bar and workflow.

---

## License

JustDownload is licensed under the **MIT License** — see [`LICENSE`](LICENSE).

ffmpeg is **not** part of this codebase and is **not** statically linked. JustDownload uses an **LGPL**
build of ffmpeg invoked as a **separate child process**; it never bundles or statically links a GPL build.
See [`docs/THIRD-PARTY-NOTICES.md`](docs/THIRD-PARTY-NOTICES.md) for third-party dependencies and the full
ffmpeg notice, and [`docs/LEGAL.md`](docs/LEGAL.md) for the acceptable-use and Terms-of-Service notice.
