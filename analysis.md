# JustDownload — Analysis (residual context)

> Audit date: 2026-06-29 · Original scope: full codebase, `PRD.md`, aitasks (78 `done`), extension, settings, performance, feature gaps.
>
> **All actionable findings from this audit have been converted to aitasks.** This document now retains only the
> non-actionable context: what's already solid, what's deliberately out of scope, and a map of where each finding went.
> Nothing below is a to-do — open `aitasks` for the work items.

## Where the findings went (aitasks created)

- **Already fixed:** `TASK-087` (apply saved theme on startup), `TASK-088` (wire global speed limit) — both `done`.
- **Browser integration & extension:** `TASK-089`–`TASK-099` (host registration, allowlist, auth-context hand-off, popup status, options page, permissions, Firefox background, extension key, log redaction), `TASK-092` (e2e test), `TASK-093` (in-app Browsers panel).
- **Media:** `TASK-100` (variant picker hookup), `TASK-101` (hostile-site extractor), `TASK-102` (DASH SegmentTemplate).
- **Performance / N+1:** `TASK-103`–`TASK-108`.
- **Durability & verification gaps:** `TASK-109` (power-loss fsync), `TASK-110`–`TASK-117` (NTLM, FTP/FTPS fixtures, FTPS integration, mac/Linux keychain, DPI snapshots, SOCKS, dead checkpointer, macOS drag), `TASK-130` (TASK-040 AC wording).
- **Code-quality fixes:** `TASK-118`–`TASK-120`.
- **Settings completeness:** `TASK-121`–`TASK-129`.
- **New features:** `TASK-131`–`TASK-150` (quick wins, medium, big rocks, v2 parking lot).

---

## What's already solid (no action)

**Engine & data layer.** Resume-offset math and cooperative-kill resume are byte-perfect (SHA-256 asserted); real AES-128-CBC HLS decryption and ffmpeg stream-copy mux work; work-stealing segmentation is real; parameterized SQL with WAL pragmas; secret redaction (`SecretRedactor`) masks auth headers / bearer tokens / signed-URL query strings and is installed globally; `IAsyncDisposable` discipline across `PreallocatedFile`, `FtpTransportResponse`, `SqliteConnectionFactory`, `FfmpegRunner`; event handlers unsubscribed in `Dispose()`; cancellation threaded through I/O; no empty `catch {}` found.

**Performance already done well.** Single shared `SocketsHttpHandler` (`SharedHttpHandlerProvider`); `ArrayPool<byte>` copy buffers; DataGrid virtualization; batched queue query (`GetByStatusOrderedByPriorityAsync`); no `.Result`/`.Wait()` on hot paths; init/recovery run after the window paints (cold-start budget safe); no leaks or unbounded allocations found.

**Settings save path is correct.** UI change → `ISettingsService.UpdateAsync` → diff via `SettingsSerializer` → only changed keys persisted under a `SemaphoreSlim`; `Changed` fires after release; hydration suppressed so loading doesn't write back; all 11 `AppSettings` properties round-trip. (The two settings that saved-but-didn't-apply — theme & speed limit — are fixed in `TASK-087`/`TASK-088`; remaining gaps are *missing* settings, now `TASK-121`–`TASK-129`.)

**`done` tasks that are genuinely complete (sampled):** 021, 023, 024, 027, 028, 030, 031, 036–038, 041, 042, 044–047, 049, 051, 054, 056, 058, 059, 063, 066–070, 072–074, 085, 086. No `done` task was found to be a fabricated/empty stub — the weaknesses (now tasked) cluster at integration seams and on-OS verification, not in the core logic.

---

## Deliberately out of scope (will NOT do — no tasks created)

These conflict with locked decisions (CLAUDE.md D1–D9) or PRD §2.3 non-goals and are intentionally excluded:

| Feature | Why excluded |
|---|---|
| Torrents / P2P / magnet links | PRD §2.3 non-goal; heavy (DHT/peers/seeding); keep engine pluggable for a future external tool only. |
| Bundling **yt-dlp** | D3 (revised 2026-07-02): in-house extraction is the default; yt-dlp is only an optional, user-enabled, downloaded-on-demand fallback (never bundled/statically linked, separate-process only, TASK-161/162/163) — `IMediaExtractor`'s pluggable design (`TASK-150`) is the seam it uses. |
| Video re-encoding / transcoding | D4 + §2.3: stream-copy only, "no farm." Users can post-process with ffmpeg manually. |
| Mobile apps (iOS/Android) | §2.3 non-goal; native desktop UI. A telemetry-free web remote (`TASK-149`) is the lighter alternative. |
| Telemetry / accounts / license server | **D2** — the key differentiator. Never add; resist pressure. |
| Cloud sync / paid tier / freemium | D2 + §2.3; stays MIT, free forever. Queue import/export (`TASK-129`/`TASK-140`) covers portability. |

> Note: `TASK-143` (subtitle auto-download) is parked pending a D3 sign-off — confirm it counts as acceptable in-house extraction before building.
