# Product Requirements Document — JustDownload

> A cross-platform, extremely lightweight, fast download manager.
> **Stack:** .NET 8 + Avalonia 11 · **License:** Free & Open Source · **Platforms:** Windows, macOS, Linux

| | |
|---|---|
| **Doc status** | Draft v0.1 |
| **Last updated** | 2026-06-26 |
| **Owner** | Sarfraz |
| **Repo** | `JustDownload` |

---

## 1. Executive Summary

**Problem Statement**
Existing download managers are either heavy and slow (Electron/Java-based, 150–300 MB RAM idle), paid/closed-source (IDM), or visually dated and clunky. Users on slow or older hardware have no fast, modern, free, cross-platform option.

**Proposed Solution**
JustDownload is a native .NET 8 + Avalonia download manager whose **defining selling point is being extremely light and fast even on slow systems**. It uses a dynamic segmentation engine to saturate available bandwidth, organizes downloads automatically, supports HTTP/HTTPS/FTP with proxies and authentication, handles HLS video, and ships with a browser extension — all in a polished, modern-minimal UI.

**Success Criteria (measurable KPIs)**

| # | KPI | Target |
|---|-----|--------|
| K1 | Cold-start time to interactive (idle 4-core, HDD) | ≤ 1.5 s |
| K2 | Idle RAM (Working Set, no active downloads) | ≤ 90 MB |
| K3 | Installer/bundle size per platform | ≤ 40 MB |
| K4 | Bandwidth utilization vs. raw `curl` on a 100 MB segmented file | ≥ 95% |
| K5 | Resume integrity after hard crash/kill (SHA-256 match) | 100% across test matrix |
| K6 | Automated test line coverage on core engine library | ≥ 85% |
| K7 | UI renders correctly from 100% → 300% DPI scaling | 0 layout-break defects |

---

## 2. User Experience & Functionality

### 2.1 User Personas

| Persona | Description | Primary Need |
|---|---|---|
| **Budget-hardware Bilal** | Student on a 5-year-old laptop, limited RAM, slow ISP | Fast downloads without his machine choking |
| **Power-user Petra** | Developer who grabs ISOs, datasets, HLS streams | Segmentation, proxies, scriptable control, queue management |
| **Casual Carla** | Wants to save a video/PDF from a website with one click | Browser extension + zero-config "it just works" |
| **Privacy Pat** | Avoids closed-source tools, runs Linux | Open source, no telemetry, no license server |

### 2.2 User Stories & Acceptance Criteria

> Legend — Priority: **P0** = MVP must-have, **P1** = v1.0, **P2** = post-1.0.

---

**US-1 — Dynamic segmented download (P0)**
*As a user, I want files split into parallel segments so downloads finish as fast as my bandwidth allows.*
- **AC1:** Engine splits a download into N connections (default 8, configurable 1–32).
- **AC2:** **Dynamic** segmentation: when one segment finishes early, its idle connection re-splits the largest remaining segment (work-stealing), not just static range division.
- **AC3:** Falls back gracefully to single-connection when the server does not honor `Range` (no `Accept-Ranges`/206).
- **AC4:** Achieves ≥ 95% of raw multi-connection throughput (KPI K4).

**US-2 — Pause / Resume / Crash recovery (P0)**
*As a user, I want to pause and resume downloads, and never lose progress on a crash.*
- **AC1:** Pause halts all connections and persists per-segment byte offsets to SQLite within 500 ms.
- **AC2:** Resume re-issues `Range` requests from persisted offsets; already-downloaded bytes are never re-fetched.
- **AC3:** After `kill -9` / power loss simulation, restarting the app offers resume and final file passes SHA-256 verification (KPI K5).
- **AC4:** Detects when a server can no longer resume (offset rejected) and surfaces a clear "restart required" state.

**US-3 — Live bandwidth limiting (P0)**
*As a user, I want to cap download speed and change the cap mid-download.*
- **AC1:** Global and per-download speed caps (KB/s).
- **AC2:** Changing the cap while downloading takes effect within 1 s without restarting the transfer.
- **AC3:** A "0 / unlimited" setting removes throttling.

**US-4 — Live max-connection control (P0)**
*As a user, I want to change the number of connections while a download is running.*
- **AC1:** Increasing connections spawns new segments via work-stealing without restarting.
- **AC2:** Decreasing connections gracefully drains and closes excess connections at segment boundaries.

**US-5 — Protocol support: HTTP/HTTPS/FTP (P0 for HTTP/S, P1 for FTP)**
- **AC1:** HTTP & HTTPS with TLS 1.2+, redirects, cookies, custom headers, `Content-Disposition` filename detection.
- **AC2:** FTP & FTPS with passive mode, `REST` for resume, directory listing for filename.

**US-6 — Proxy support: HTTP proxy + SOCKS4/5 (P1)**
- **AC1:** Per-download and global proxy config.
- **AC2:** SOCKS5 with remote DNS resolution.
- **AC3:** Proxy on/off toggle without app restart.

**US-7 — Authentication: HTTP & proxy auth (P1)**
*Basic, Digest, NTLM for both origin and proxy.*
- **AC1:** Prompts for credentials on 401/407; offers "save to OS credential store."
- **AC2:** Digest (RFC 7616) and NTLM handshakes succeed against test servers.
- **AC3:** Credentials are stored via OS keychain (DPAPI / Keychain / libsecret), never plaintext.

**US-8 — Auto-organization (P0)**
*As a user, I want downloads sorted by status and file type automatically.*
- **AC1:** Categories by **status**: Completed, Incomplete (in-progress/paused/failed).
- **AC2:** Categories by **type**: Video, Audio, Document, Compressed, Program, Image, Other — by extension + MIME.
- **AC3:** Optional move-to-folder-by-category on completion (user toggle); category rules user-editable.

**US-9 — HLS download + merge (P1)**
*As a user, I want to download an HLS stream and get one file.*
- **AC1:** Parses `.m3u8` (master + media playlists), lists available quality variants.
- **AC2:** Downloads all `.ts` segments (segmented/parallel), handles AES-128 segment decryption when a key URI is present.
- **AC3:** Concatenates segments into a single `.ts` in playlist order with no gaps/reordering.
- **AC4:** Supports live-to-VOD finite playlists; reports progress by segment count when total size is unknown.

**US-9b — Separate video+audio streams → mux (P1)** *(added from reference screenshot 2)*
*As a user, when a site (e.g. YouTube) serves video and audio as separate adaptive streams, I want them downloaded in parallel and combined into one playable file.*
- **AC1:** Engine can download **two independent streams concurrently** (video + audio), each with its own segmentation/connection set and its own progress.
- **AC2:** The detail view shows **separate Video Status and Audio Status**, each with its own segment bar and size (matches the reference UI).
- **AC3:** On completion, ffmpeg **muxes** the streams into a single container (`.mkv` by default, `.mp4` when codecs allow) via stream-copy (no re-encode).
- **AC4:** Covers DASH (`.mpd`) and the common "two progressive URLs" case; partial failure of either stream leaves the other's data resumable, not discarded.

**US-10 — Convert / remux + quality default (P1)**
- **AC1:** Optional post-download remux `.ts` → `.mp4` and muxed-stream → `.mkv`/`.mp4` via bundled/located **ffmpeg** (stream copy, no re-encode by default → fast & lossless).
- **AC2:** Setting: **"default video quality"** used to auto-pick the HLS/DASH variant (e.g., Highest / 1080p / 720p / Lowest) without prompting.
- **AC3:** Setting: **"default container"** (MKV vs MP4) for muxed output.
- **AC4:** Conversion runs as a queued post-process job with its own progress; failure leaves the source segments/streams intact.

**US-11 — Browser extension: send links + video detection (P1)**
*As a user, I want to send links from my browser and download videos/audio from any site.*
- **AC1:** Extensions for Chrome, Edge, Firefox (Manifest V3).
- **AC2:** Context-menu "Download with JustDownload" for links/media; captures page cookies, referrer, and headers for authenticated downloads.
- **AC3:** Detects media via in-house network sniffing (observes media requests: `.m3u8`, `.mpd`, `.mp4`, `.ts`, audio) and shows a **floating download button** on pages with detected video.
- **AC4:** Communicates with the desktop app via a **Native Messaging Host** (no open local port by default).
- **AC5:** If the app is not running, the extension launches it (or queues the link for next start).

**US-12 — Per-site blacklist for the video button (P1)**
- **AC1:** User can blacklist a domain so the floating download button never appears there.
- **AC2:** Blacklist managed both in the extension popup and synced to the app settings.

**US-13 — Renew expired downloads (P1)**
*As a user, when a download URL expires (signed/temporary URLs), I want to refresh it.*
- **AC1:** Detects expiry (403/410/expired-token responses) and marks the download "Expired — needs new link."
- **AC2:** "Renew" flow: re-fetch a fresh URL via the original referrer page (through the extension) or let the user paste a new URL; resumes from existing bytes if the file identity (size/ETag) matches.

**US-14 — macOS drag & drop (P1, macOS only)**
- **AC1:** Drag a link or media element from the browser onto the app/dock icon to enqueue.
- **AC2:** Drag a completed file out of the app into Finder.

**US-15 — Modern, adaptive, themeable UI (P0)**
- **AC1:** Modern-minimal design (Linear/Arc/Raycast reference): list + detail, command palette, subtle motion.
- **AC2:** Light & dark themes; follows OS theme by default.
- **AC3:** Renders correctly across 100%–300% DPI and window sizes from 800×600 up to 4K (KPI K7).
- **AC4:** Full keyboard navigation; meets WCAG 2.1 AA contrast.
- **AC5:** Implements the layout defined in **§2.4 UI/UX Specification** (category-tree sidebar, downloads list, per-download detail view, segment visualization).

**US-15b — Live segment/connection visualization (P0)** *(signature feature from reference shots 2–4)*
*As a user, I want to see each parallel connection's progress so I trust the segmentation engine is working.*
- **AC1:** A horizontal **segment bar** renders the download's byte range as blocks; each connection's downloaded portion fills in live.
- **AC2:** Shows the active connection count ("Segments: N") and updates as connections are added/removed live (US-4).
- **AC3:** For separate video+audio (US-9b), shows **two stacked segment bars**, each labeled with its stream size.
- **AC4:** Repaints at ≤ 4 Hz to stay cheap on slow hardware (no per-chunk redraw).

**US-15c — Per-download detail view (P1)**
*As a user, I want a focused view of one download with all its stats and controls.*
- **AC1:** Opens from a list row (double-click / Enter). Available as a **dockable inline panel** (default, modern) and an **optional detached window** (NDM-style, for multi-monitor power users).
- **AC2:** Tabs: **Download** (URL, status, size, % done, bandwidth, ETA, Resumable Yes/No, segment bar), **Options** (rename, target folder, category, speed/connection limits), **Connections** (per-connection live list: index, range, bytes, speed, state).
- **AC3:** Per-download **Pause / Resume / Cancel** controls present in the view.

**US-16 — Queue, scheduling & batch (P2)**
- **AC1:** Max concurrent downloads setting; queue with priority reorder (drag).
- **AC2:** Scheduler: start/stop at times; "shutdown/sleep when queue done."
- **AC3:** Batch add (paste many URLs, URL pattern `[001-100]`).

### 2.3 Non-Goals (explicitly NOT building in v1.0)

- ❌ **No yt-dlp bundling** — extraction is fully in-house per your decision (architecture stays pluggable so it can be added later).
- ❌ No torrent / P2P / Metalink support.
- ❌ No mobile (iOS/Android) apps.
- ❌ No paid tier, license server, accounts, or telemetry.
- ❌ No cloud sync of download history across devices.
- ❌ No video **re-encoding/transcoding** beyond stream-copy remux (no quality conversion farm).
- ❌ No built-in media player beyond OS "open file."

### 2.4 UI/UX Specification

> Derived from the reference product (**Neat Download Manager**, screenshots `1–4.jpg`). **Design intent:** keep NDM's proven *information architecture* but render it in the chosen **modern-minimal** visual language (Linear/Arc/Raycast) — generous spacing, a restrained palette, one accent color, subtle motion, light+dark. The reference defines *what information appears*; the modern-minimal direction defines *how it looks*.

#### 2.4.1 Main window — three-pane master/detail
```
┌─────────────────────────────────────────────────────────────────────┐
│  [+ New URL]  [Resume] [Pause] [Stop] [Delete]      [⌕ search]  [⚙]   │  Toolbar / command bar
├───────────────┬─────────────────────────────────────────────────────┤
│ ▾ All         │  Name            Size     Status        Speed   ETA   │  Sortable columns
│   ▸ Video     │  🎬 Thriller.mkv 52.5 MB  Downloading 33% 442KB/s 1:19 │
│   ▸ Audio     │  📦 firefox.dmg  55.2 MB  Paused 74%     —      —     │
│   ▸ Document  │  📄 report.pdf   121 KB   Complete       —      —     │
│   ▸ Compressed│  🎞 stream.m3u8  47 segs  Downloading…   1.2MB/s —    │
│   ▸ Program   │  …                                                    │
│   ▸ Other     │                                                       │
│ ▾ Status      │                                                       │
│   ▸ Complete  │                                                       │
│   ▸ Incomplete ├──────────────────────────────────────────────────────┤
│               │  ▼ Detail panel (selected item) — see 2.4.3           │
└───────────────┴─────────────────────────────────────────────────────┘
  Status bar:  3 active · 1.6 MB/s total · 12 connections · 4.2 GB queued
```
- **Sidebar (category tree):** `All Downloads` expandable into **type** categories (Video, Audio, Document, Compressed, Program, Image, Other) + a **Status** group (Complete, Incomplete). Counts shown as subtle pill badges. Collapsible to an icon rail on narrow widths.
- **Downloads list:** columns **Name (with file-type icon) · Size · Status · Speed (Bandwidth) · ETA · Added/Last-Try**. Columns sortable, reorderable, hideable. Status cell shows an inline mini-progress + label (`Downloading 33%`, `Paused 74%`, `Complete`, `Error`, `Queued`, `N segments`). Right-click context menu (Open, Open folder, Pause/Resume, Renew, Copy URL, Remove, Delete file, Properties).
- **Empty state:** friendly illustration + "Paste a URL or drag a link here."

#### 2.4.2 Toolbar / global actions
- Primary: **New URL** (paste-URL dialog with auto-detected filename, folder, category). Secondary: **Resume / Pause / Stop / Delete** (act on selection), **Settings**, **Browsers** (extension/integration status), **About**.
- A **command palette** (Ctrl/Cmd+K) is the modern-minimal addition over NDM: New URL, jump to category, toggle theme, change limits.

#### 2.4.3 Detail view & segment visualization (see US-15b, US-15c)
- Header: filename, type icon, overall green **progress bar**, big % and ETA.
- Stat grid: File size · Downloaded (bytes + %) · Bandwidth · Time remaining · **Resumable: Yes/No**.
- **Segment bar(s):** the standout visual — a block strip showing each connection's filled progress, with "Segments: N". Separate **Video** and **Audio** bars (with sizes) for muxed downloads.
- Tabs **Download / Options / Connections** (US-15c). Per-item **Pause / Cancel**.

#### 2.4.4 Visual & responsive system
- **Tokens:** 4px spacing grid, 2 type sizes for data density, one accent color, neutral grays; rounded-12 cards; hairline separators.
- **Theme:** light + dark, follows OS; honors OS accent on Win/mac where available.
- **Density toggle:** Comfortable (default) ↔ Compact (power-user, closer to NDM density).
- **DPI/resolution:** vector icons, layout valid 800×600 → 4K, 100%–300% scaling (KPI K7); list virtualization for thousands of rows on slow hardware.
- **Motion:** ≤150ms transitions, reduced-motion respected; never animate the segment bar per-chunk (perf).
- **Platform fit:** native window chrome, traffic-lights on macOS, system menu/tray, OS notifications on complete/error.

#### 2.4.5 Key screens to design (deliverable checklist)
Main window (empty + populated) · New URL dialog · Detail panel + detached window · Settings (General, Connections, Proxy, Authentication, Categories, Browsers, Advanced) · Add-video / quality picker · Browser-extension popup (with per-site blacklist toggle) · About · macOS drag-drop affordance.

---

## 3. Media Extraction System Requirements

> This replaces the generic "AI" section — the comparable risk/quality surface here is the **in-house media extractor**, which is the highest-maintenance subsystem.

### 3.1 Extractor Architecture
- **Pluggable `IMediaExtractor` interface.** A registry of site/format extractors, tried in priority order. This is the seam where yt-dlp (or a yt-dlp sidecar) could be slotted in later without core changes.
- **Generic extractors (always present):**
  - **HLS** (`.m3u8` master/media, AES-128, variant selection).
  - **DASH** (`.mpd`, separate video+audio representations) — **P1** (raised from P2; required by the muxing flow in US-9b / reference shot 2).
  - **Separate-stream + mux:** discover paired video/audio URLs, download both, hand off to the ffmpeg mux post-process (US-9b).
  - **Progressive** (`.mp4`/`.webm`/audio) via direct URL or sniffed request.
- **Variant/quality selection** honors the user's "default video quality" + "default container" settings (US-10).
- **Sniffer-driven:** the browser extension observes network requests and reports media URLs + the headers/cookies needed to fetch them. This is the primary discovery mechanism for "any website."

### 3.2 Known Limitation & Risk (must be surfaced in UI/README)
- Site-specific players (YouTube, Facebook) actively obfuscate stream URLs and rotate signatures. **In-house-only extraction will have limited and brittle coverage** for these sites. The product will:
  - Be honest in docs about which sites are "best-effort."
  - Degrade gracefully (no crash, clear "couldn't extract" message).
  - Keep the extractor interface ready for a yt-dlp plugin if priorities change.
- **Legal:** Downloading from some sites may violate their Terms of Service. Show a one-time notice; do not bypass DRM (Widevine/PlayReady protected content is explicitly out of scope).

### 3.3 Evaluation Strategy (extraction quality)
- **Corpus test:** maintain a fixture set of ~30 sample pages/playlists (self-hosted + permissively-licensed public test streams like Apple/Unified Streaming sample HLS).
- **Pass criteria:** generic HLS/MP4/audio extraction succeeds on 100% of self-hosted fixtures; merged output byte-identical to a known-good reference.
- **Regression:** extractor corpus runs in CI nightly; flaky third-party sites are quarantined, not allowed to fail the build.

---

## 4. Technical Specifications

### 4.1 Architecture Overview

```
┌──────────────────────────────────────────────────────────────┐
│  Browser (Chrome/Edge/Firefox)                                 │
│   └─ Extension (MV3): context menu, media sniffer, blacklist   │
└───────────────┬──────────────────────────────────────────────┘
                │  Native Messaging (stdio JSON)
┌───────────────▼──────────────────────────────────────────────┐
│  JustDownload.App  (Avalonia 11, MVVM)                         │
│   ├─ UI (Views/ViewModels, themes, DPI-adaptive)              │
│   ├─ App services: queue, scheduler, notifications            │
│   └─ NativeMessagingHost (separate stub exe)                  │
├──────────────────────────────────────────────────────────────┤
│  JustDownload.Core  (pure .NET class library — UI-agnostic)   │
│   ├─ DownloadEngine: dynamic segmentation, work-stealing      │
│   ├─ Transport: HttpClient (SocketsHttpHandler), FtpClient    │
│   ├─ Proxy (HTTP/SOCKS) + Auth (Basic/Digest/NTLM)            │
│   ├─ Throttle (token-bucket) + ConnectionManager (live)       │
│   ├─ Extractors (IMediaExtractor: HLS/DASH/progressive)       │
│   ├─ PostProcess: ffmpeg remux (.ts→.mp4), HLS concat         │
│   └─ Persistence: SQLite (state, segments, history)           │
└───────────────┬──────────────────────────────────────────────┘
                │
        ┌───────▼────────┐   ┌──────────────┐
        │ SQLite (file)  │   │ OS Keychain  │
        │ ~/.justdownload│   │ creds/secrets│
        └────────────────┘   └──────────────┘
        External: ffmpeg (bundled or auto-located)
```

**Key principle:** `JustDownload.Core` is a **headless, fully testable** library with zero Avalonia dependency. The UI and the Native Messaging Host are thin clients over it. This is what makes KPI K6 (≥85% coverage) achievable and keeps the engine reusable (CLI later, P2).

### 4.2 Technology Choices

| Concern | Choice | Rationale |
|---|---|---|
| Runtime | **.NET 8 (LTS)** | Installed; LTS until Nov 2026; NativeAOT support |
| UI | **Avalonia 11** + CommunityToolkit.Mvvm | True cross-platform native UI, no WebView, good DPI |
| HTTP | `SocketsHttpHandler` / `HttpClient` | Range requests, connection pooling, proxy, decompression |
| FTP | `FluentFTP` | Mature, supports `REST` resume, FTPS |
| SOCKS | `SocketsHttpHandler` SOCKS support + custom connector | Built-in SOCKS5 in .NET 8 |
| DB | **SQLite** via `Microsoft.Data.Sqlite` | Embedded, zero-config, crash-safe with WAL |
| Secrets | DPAPI (Win) / Keychain (mac) / libsecret (Linux) | No plaintext credentials |
| Media post-proc | **ffmpeg 7.1** (present on dev machine) | Remux/concat; stream-copy = fast & lossless |
| Throttling | Token-bucket rate limiter | Smooth live-adjustable caps |
| Tests | xUnit + `Avalonia.Headless` + Testcontainers (proxy/ftp/http fixtures) | Engine + UI + integration |
| Packaging | MSI/MSIX (Win), `.dmg`+notarize (mac), AppImage/deb/rpm (Linux) | Native install per OS |

### 4.3 Performance / "Light & Fast" Engineering (the selling point)
- **Trimming** (`PublishTrimmed`) + **ReadyToRun**; evaluate **NativeAOT** for the engine/host (Avalonia AOT is improving — measure, fall back to R2R if needed).
- Lazy-load views and the extractor registry; defer SQLite open until first use.
- Single shared `HttpClient`/`SocketsHttpHandler`; pooled buffers (`ArrayPool<byte>`), `Span`-based I/O, no LOH thrash.
- Async streaming writes with sequential pre-allocated sparse file; periodic (not per-chunk) state checkpoints.
- **Budget gates in CI:** fail the build if K1/K2/K3 regress beyond threshold (BenchmarkDotNet + startup probe).

### 4.4 Data Model (SQLite, simplified)

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

### 4.5 Integration Points
- **Browser ⇄ App:** Native Messaging (stdio, length-prefixed JSON). Manifest registered per-browser at install. No listening socket by default (security).
- **App ⇄ ffmpeg:** child process, parsed progress via stderr; bundled binary or system-PATH discovery with version check.
- **App ⇄ OS:** notifications, "open folder," file associations, drag-drop (macOS), single-instance + URL/argument forwarding.

### 4.6 Security & Privacy
- **No telemetry, no accounts, no network calls** except user-initiated downloads and update checks (update check is opt-in/configurable).
- Credentials only in OS keychain; never logged. Logs redact auth headers, tokens, signed-URL query strings.
- Native Messaging host validates the calling extension ID allowlist.
- TLS validation on by default; "ignore cert" is per-download, explicit, and warned.
- No DRM circumvention. No execution of downloaded files.
- Reproducible builds + signed releases (Authenticode / macOS notarization) where feasible.

---

## 5. Risks & Roadmap

### 5.1 Phased Rollout

| Phase | Scope | Exit criteria |
|---|---|---|
| **MVP (v0.x)** | Core engine library; HTTP/HTTPS dynamic segmentation; pause/resume + crash recovery; live speed + connection control; SQLite persistence; auto-organization; Avalonia UI per §2.4 (category-tree sidebar + list + detail + **segment visualization**, light/dark, DPI). Win+Linux. | KPIs K1,K2,K3,K4,K5 met on Win/Linux; engine coverage ≥85%; segment bar renders live. |
| **v1.0** | FTP; proxy (HTTP/SOCKS) + auth (Basic/Digest/NTLM); HLS download+merge; **DASH + separate video+audio download & ffmpeg mux (US-9b)**; .ts→mp4 remux + quality/container defaults; browser extension (Chrome/Edge/FF) with send-link + sniffer + blacklist; renew expired; macOS build + drag-drop; polished UI + command palette + detached detail window. | All P0/P1 ACs pass; 3-OS install tested; muxed YouTube-style download produces one playable file; extension in stores (or sideload docs). |
| **v1.1** | Queue priority/scheduler/batch; download speed graphs; density toggle polish; portable mode. | — |
| **v2.0** | Optional yt-dlp plugin (off by default); CLI front-end over Core; scripting/automation API. | — |

### 5.2 Technical Risks & Mitigations

| Risk | Impact | Mitigation |
|---|---|---|
| **In-house extraction brittle on YouTube/FB** | Casual users disappointed | Honest docs; sniffer covers generic media; keep extractor pluggable for future yt-dlp |
| **Avalonia NativeAOT immaturity** vs. size/startup goals | Miss K1/K3 | Measure early; fall back to trimmed ReadyToRun; AOT only the headless host |
| **NTLM/Digest edge cases** across servers | Auth failures | Test matrix with real IIS/Apache/Squid containers; rely on `System.Net` where possible |
| **Crash-resume corruption** | Data integrity (core promise) | Atomic checkpoints, WAL, sparse-file offsets, SHA verify, fuzz/kill tests in CI |
| **ffmpeg distribution/licensing/size** | Bundle bloat / legal | Ship LGPL build or auto-download on first use; document license |
| **MV3 + Native Messaging packaging** across 3 browsers/3 OS | Install friction | Per-OS installer registers host manifests; signed extensions; sideload guide |
| **DPI/layout breakage** on exotic scaling | UX defects (K7) | Snapshot/headless UI tests at 100/125/150/200/300% |
| **Cross-platform CI cost/complexity** | Slow delivery | GitHub Actions matrix (win/mac/linux); cache; nightly extractor corpus |

### 5.3 Testing Strategy (KPI K6)
- **Unit:** segmentation math, work-stealing, throttle token-bucket, m3u8 parser, range/resume logic, category rules.
- **Integration (Testcontainers/local servers):** HTTP range server, FTP server, Squid proxy (Basic/Digest/NTLM), self-hosted HLS (plain + AES-128).
- **Crash/resume fuzz:** randomized kill points; assert SHA-256 of final file.
- **UI (Avalonia.Headless):** view-model bindings, navigation, multi-DPI snapshot tests.
- **E2E smoke:** per-OS install → download a known file → verify, in CI.
- **Performance gates:** BenchmarkDotNet + startup/RAM probes fail CI on regression.

---

## 6. Open Questions / Assumptions

1. **App name** — using "JustDownload" (folder name). Confirm final brand name + logo direction.
2. **ffmpeg** — bundle (bigger installer, works offline) vs. download-on-first-use (smaller). *Assumption: download-on-first-use, with bundled option for offline installer.*
3. **Update mechanism** — GitHub Releases + in-app "check for updates" (opt-in)? *Assumption: yes.*
4. **Minimum OS targets** — *Assumption:* Windows 10 1809+, macOS 12+, Ubuntu 20.04+/equivalent.
5. **Default download concurrency & connections** — *Assumption:* 4 concurrent downloads, 8 connections each.
6. **License** — MIT vs. GPL-3.0. (GPL aligns with ffmpeg-adjacent tooling; MIT maximizes adoption.) *Assumption: MIT for app, document ffmpeg's license separately.*
7. **Detail view default** — inline dockable panel (modern) vs. NDM-style detached window. *Assumption: inline by default, detached available.* Confirm preference.
8. **Muxed-video container default** — MKV (always works, matches reference) vs. MP4 (more compatible, codec-dependent). *Assumption: MKV default, MP4 when codecs allow.*
9. **Density default** — Comfortable vs. Compact out of the box. *Assumption: Comfortable.*

> **Note:** §2.4 (UI/UX), US-9b (separate video+audio mux), US-15b (segment visualization) and US-15c (detail view) were added/revised after reviewing the Neat Download Manager reference screenshots (`1–4.jpg`).

---

*Next step after sign-off: scaffold the solution (`JustDownload.Core`, `JustDownload.App`, `JustDownload.NativeHost`, `JustDownload.Tests`, `extension/`) and stand up CI with the performance budget gates.*
