# Architecture

This document summarizes how JustDownload is structured. It draws on [`PRD.md`](../PRD.md) §4.1/§4.4 and
[`CLAUDE.md`](../CLAUDE.md) §6 (locked decisions D1–D9). For product scope, the PRD is the source of truth.

## Guiding principle

`JustDownload.Core` is a **headless, fully testable** download engine with **zero Avalonia / UI
dependency** (locked decision **D5**). The Avalonia app and the Native Messaging Host are **thin clients**
over it, driving it through service interfaces. This is what makes high engine test coverage achievable and
keeps the engine reusable (a CLI front-end is a planned future use). The boundaries between projects are
treated as sacred — no UI type ever leaks into `Core`, and the UI never does network/disk/ffmpeg work
directly.

## Component overview

```
┌──────────────────────────────────────────────────────────────┐
│  Browser (Chrome / Edge / Firefox)                            │
│   └─ Extension (MV3): context menu, media sniffer, blacklist  │
└───────────────┬──────────────────────────────────────────────┘
                │  Native Messaging (length-prefixed stdio JSON)
┌───────────────▼──────────────────────────────────────────────┐
│  JustDownload.NativeHost  (console stub exe)                  │
│   └─ Bridges the extension to the app; drives Core, never UI  │
└───────────────┬──────────────────────────────────────────────┘
                │
┌───────────────▼──────────────────────────────────────────────┐
│  JustDownload.App  (Avalonia 11, MVVM)                        │
│   ├─ Views / ViewModels (themes, DPI-adaptive)               │
│   ├─ App services: queue, scheduler, notifications           │
│   └─ Talks to Core through interfaces only — no business code │
├──────────────────────────────────────────────────────────────┤
│  JustDownload.Core  (pure .NET class library — UI-agnostic)  │
│   ├─ DownloadEngine: dynamic segmentation, work-stealing     │
│   ├─ Transport: HttpClient (SocketsHttpHandler), FtpClient   │
│   ├─ Proxy (HTTP/SOCKS) + Auth (Basic/Digest/NTLM)           │
│   ├─ Throttle (token-bucket) + ConnectionManager (live)      │
│   ├─ Extractors (IMediaExtractor: HLS / DASH / progressive)  │
│   ├─ PostProcess: ffmpeg remux (.ts→.mp4), HLS concat, mux   │
│   └─ Persistence: SQLite (state, segments, settings, history)│
└───────────────┬──────────────────────────────────────────────┘
                │
        ┌───────▼────────┐   ┌──────────────┐
        │ SQLite (WAL)   │   │ OS Keychain  │
        │ download state │   │ creds/secrets│
        └────────────────┘   └──────────────┘
        External: ffmpeg (LGPL build, separate child process)
```

## Project boundaries

| Project | Responsibility | Constraints |
|---|---|---|
| `JustDownload.Core` | Download engine, transport, proxy/auth, throttle, extractors, post-process, persistence | **No Avalonia / UI dependency.** Fully unit-testable. |
| `JustDownload.App` | Avalonia UI (Views/ViewModels), app services (queue, scheduler, notifications) | Never does network/disk/ffmpeg work directly — drives `Core` via interfaces. No business logic in code-behind. |
| `JustDownload.NativeHost` | Native Messaging Host stub for the browser extension | Console exe; drives `Core`, never UI. Validates the calling extension ID. |
| `JustDownload.Tests` | xUnit test suite | Targets ≥ 85% line coverage on `Core`. |
| `extension/` | MV3 browser extension | Talks to the app via the native host (no open local port by default). |

## Key design decisions

- **MVVM, contract-first** — Views bind to ViewModels; ViewModels depend on **interfaces**, not concretes.
  No `object`/`dynamic` crossing a boundary.
- **Dependency injection everywhere** (`Microsoft.Extensions.DependencyInjection`) so seams are mockable
  and the engine is reusable. `Core` declares registrations against the DI *abstractions* package; the App
  and host build the concrete container from Core's composition root.
- **Never block the UI thread** — downloads, ffmpeg, and extraction run on background workers
  (parallelism-capped); the UI thread is touched only to update bound state.
- **Async + cancellation end to end** — all I/O honors `CancellationToken`, so pause/cancel is instant
  with no orphaned sockets or half-written files.

## Persistence (SQLite, WAL)

Persistence is centralized in one data layer over SQLite with **WAL** journaling (locked decision **D6**).
Migrations are **versioned and type-safe** — schema is never mutated ad-hoc at runtime. State that must
survive a restart (download queue, per-segment byte offsets, settings, blacklist) is checkpointed
atomically so a crash or quit loses nothing, and resume never re-fetches or corrupts data.

Simplified data model (PRD §4.4):

```
downloads(id, url, referrer, filename, dir, total_bytes, status,
          category_type, category_status, etag, last_modified,
          created_at, completed_at, error, max_connections, speed_limit)
segments(id, download_id, index, start, end, downloaded, state)
auth(download_id, scheme, realm, username, secret_ref)      -- secret in keychain
proxies(id, type, host, port, username, secret_ref)
extractor_jobs(id, page_url, playlist_url, variant, key_uri, status)
settings(key, value)
site_blacklist(domain, scope)
```

## Secrets at rest

HTTP/proxy credentials and tokens are stored via the **OS keychain** — DPAPI (Windows),
Keychain (macOS), libsecret (Linux) — never plaintext in SQLite, logs, or error messages. The data model
holds only a `secret_ref` pointer; the secret itself lives in the keychain. Logs **redact** auth headers,
tokens, and signed-URL query strings.

## Integration points

- **Browser ⇄ App** — Native Messaging over stdio (length-prefixed JSON). No listening socket by default.
- **App ⇄ ffmpeg** — child process; progress parsed from stderr. ffmpeg is an **LGPL** build invoked as a
  separate binary (locked decision **D7**) — never bundled or statically linked as GPL.
- **App ⇄ OS** — notifications, "open folder," file associations, drag-drop (macOS), single-instance with
  URL/argument forwarding.

## Current implementation state

Implemented today: the DI composition root, the logging pipeline with secret redaction + global error
handler, the SQLite data layer (WAL connection factory, versioned migrations, repositories for downloads /
segments / settings / blacklist), the typed settings service, and the OS-keychain secret-store
abstraction. The download engine, transport, extractors, post-processing, UI views, native host logic, and
browser extension are planned / in progress. See [`README.md`](../README.md#project-status) for the
breakdown and [`PRD.md` §5](../PRD.md) for the phased roadmap.
