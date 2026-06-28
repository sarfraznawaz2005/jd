# JustDownload — Code & Feature Analysis

> Audit date: 2026-06-29 · Scope: full codebase, `PRD.md`, aitasks (78 `done`), browser extension, settings, performance, feature gaps.
> Method: six parallel specialist passes over the code, each citing `file:line`; the five headline findings below were re-verified by hand against source.

## TL;DR — what's actually broken

The engine and the pure-logic layers are genuinely strong and honestly tested. **The weaknesses cluster at integration seams** — features built and unit-tested in isolation but never wired into a working whole, plus a few settings that save but never take effect. None of the `done` tasks are fabricated; the problem is "done" sometimes means "the unit passed," not "the feature works end-to-end."

### Priority fix list (highest impact first)

| # | Issue | Severity | Where |
|---|-------|----------|-------|
| 1 | **Browser ⇄ app is non-functional end-to-end** (host manifest never registered; host allowlist empty so it rejects every connection) | 🔴 High | `ServiceCollectionExtensions.cs:401`, `NativeHostOptions.cs:11`, `NativeHost/Program.cs:35` |
| 2 | **Theme not re-applied on restart** (saves fine, ignored on launch) | 🔴 High | `App.axaml.cs` (after `LoadAsync`), `ThemeService.cs:63` |
| 3 | **Global speed limit is a dead setting** (UI saves it; engine never throttles) | 🔴 High | `ServiceCollectionExtensions.cs:300` |
| 4 | **Browser hand-off drops cookies/referrer** (authenticated downloads fail) | 🔴 High | `App.axaml.cs:276`, `MainWindowViewModel.cs:125` |
| 5 | **Media quality/variant picker is unreachable** (dialog built, no flow opens it) | 🟠 Med | `NewDownloadViewModel`, `App.axaml.cs:42` |
| 6 | **Checkpoint N+1 DB writes** on the hot path (per-segment DELETE/INSERT every 500ms) | 🟠 Med | `DownloadManager.cs:353-378` |
| 7 | **Progress events fire per-chunk** → UI thrash on slow systems | 🟠 Med | `DownloadManager.cs:422-429`, `SegmentedDownloader.cs:593-601` |
| 8 | **In-app "Browsers" toolbar button + settings section are placeholders** | 🟠 Med | `MainWindowViewModel.cs:161`, `SettingsViewModel.cs:40` |
| 9 | **Power-loss durability unproven** (fsync only at completion, not before checkpoint) | 🟠 Med | `DownloadManager` checkpoint path |
| 10 | Completion-action & enqueue fire-and-forget swallow exceptions | 🟠 Med | `DownloadScheduler.cs:145`, `DownloadsListViewModel.cs:189` |

---

## 1. aitasks marked `done` but not truly complete

78 tasks are `done`. **No task is a fabricated/empty stub** — every audited feature has real, competent code, and task notes are honest about caveats. The issues are acceptance-criteria checkboxes that claim more than the *assembled* system delivers.

### High — `done`, but the integrated feature does not work

- **TASK-064 / TASK-065 — Native Messaging host + manifest registration.** AC: host accepts the allowlisted extension; manifests registered on install. Reality: `INativeHostRegistrar.Register(...)` is called **only in tests** (`NativeHostRegistrationTests.cs:109`) — no installer or startup hook ever writes the manifest/registry, and `NativeHostOptions.AllowedExtensionIds` defaults to `[]` and is never populated, so the host **fails closed and rejects every connection** (`ExtensionOrigin.cs:18`, `NativeHost/Program.cs:35`). The extension cannot talk to the app. *(Verified by hand.)*
- **No end-to-end extension test exists** (no Playwright/Puppeteer/Selenium anywhere). All extension AC evidence is unit-level on pure helpers — which is exactly why the broken wiring above went unnoticed.

### Medium — AC checked, but the named capability is unproven or absent

- **TASK-035 — HTTP/proxy auth.** Basic + Digest are genuinely end-to-end tested (fixture computes RFC-7616 digest server-side). **NTLM is never handshake-tested** — the only evidence asserts a domain credential sets a flag (`AuthTests.cs:173-184`), yet AC says "NTLM succeed."
- **TASK-082 — Test fixtures.** HTTP range/no-range, Basic/Digest proxy, HLS plain+AES-128 fixtures are real. **NTLM proxy and a real FTP/FTPS server were never built** (only `Fakes/FakeFtp.cs`), though AC claims them and CLAUDE.md §3 requires them.
- **TASK-033 — FTP/FTPS transport.** REST-resume/segmentation logic is real and tested — but only against the in-memory fake. The concrete `FluentFtpConnection` (real sockets, `ValidateAnyCertificate=false`) is executed by **no test**; "FTPS download works" is unproven.
- **TASK-022 — OS keychain.** All three backends are really implemented, but tests early-return on non-Windows, so **mac/Linux are uncovered**. Minor: macOS backend passes the secret on argv (`security … -w secret`), briefly visible to `ps`.
- **TASK-084 — "multi-DPI snapshot tests."** Never sets DPI/`RenderScaling` (just five window *sizes*) and asserts no pixels (headless has no backend). Useful layout-geometry guard, but the title overstates it.
- **TASK-029 — Crash recovery.** Resume-offset math is correct and cooperative-kill resume is SHA-256-perfect. But **fsync appears only at completion, not before a checkpoint**, so a true power loss could leave the checkpoint ahead of physically-flushed bytes → silent gap on resume. The mandated power-loss simulation only tests the safe direction. *(Worth confirming in `DownloadManager`/`PreallocatedFile`.)*
- **TASK-057 — Settings screens.** General/Connections/Categories are real; **Proxy, Authentication, Browsers, Advanced are static `InfoSettingsViewModel` placeholders** (no bindings). User-signed-off deviation, recorded honestly — but "all sections bound" is overstated.
- **TASK-060 — Quality/variant picker.** Dialog + VM are real and honor defaults, **but no user flow opens it** (`MediaVariantPickerViewModel` is only DI-registered; `NewDownloadViewModel` enqueues a plain HTTP download). *(Verified by hand.)*
- **D3 roadmap gap — no hostile-site extractor.** Only Progressive/HLS/DASH exist; the PRD's "best-effort YouTube/Facebook" is delivered by **no task**. Not a false `done` (TASK-043 correctly ships a real fixture corpus), but the capability is absent — track it.

### Low — disclosed caveats / cosmetic

- TASK-034 SOCKS only asserted at URI boundary (no routing test). · TASK-039 DASH handles only single-file `BaseURL`; **SegmentTemplate/SegmentList skipped** (most real DASH). · TASK-040 progress reads `-progress pipe:1` (stdout) while AC says stderr — code is correct, AC wording wrong. · TASK-025 `SegmentCheckpointer` is dead code (prod uses `DownloadManager.CheckpointLoopAsync`). · TASK-071 "Open extension settings" is a no-op (no options page). · TASK-062 macOS drag&drop never run on a Mac.

**Genuinely complete (sampled):** 021, 023, 024, 027, 028, 030, 031, 036–038, 041, 042, 044–047, 049, 051, 054, 056, 058, 059, 063, 066–070, 072–074, 085, 086, plus real AES-128-CBC HLS decryption and ffmpeg stream-copy mux.

---

## 2. Performance & N+1 (structural fixes only — no caching)

**Already good:** single shared `SocketsHttpHandler` (`SharedHttpHandlerProvider`), `ArrayPool<byte>` copy buffers (`SegmentedDownloader.cs:552,629`), DataGrid virtualization (`MainWindow.axaml`), batched queue query (`GetByStatusOrderedByPriorityAsync`), no `.Result`/`.Wait()` on hot paths.

**Fix these:**

1. **🟠 Checkpoint N+1 (hot path).** `DownloadManager.PersistSegmentsAsync` (`:353-378`) runs `1 SELECT + N DELETE + N INSERT` **every 500ms** for the whole download. → Replace with `DELETE FROM segments WHERE download_id=$id` + a single multi-row `INSERT` (≈80% fewer queries).
2. **🟠 Progress events per-chunk.** `ProgressChanged` fires per write (`DownloadManager.cs:422-429`, `SegmentedDownloader.cs:593-601`), each marshalling to the UI thread (`DownloadsListViewModel.cs:238`, `DownloadDetailViewModel.cs:108` which also rebuilds the connection list). On a fast link that's hundreds/sec → frame drops on slow hardware (violates D9). → Coalesce to ~10–20 Hz or an N-bytes threshold; batch the UI marshal.
3. **🟠 Recovery loop N+1 (startup).** `DownloadRecoveryService.cs:26-50` issues one `UpdateAsync` per interrupted download. → Single `UPDATE downloads SET status=$paused WHERE status=$active`.
4. **🟡 Reorder N+1.** `DownloadQueueService.ReorderAsync` (`:88-103`) one `SetPriorityAsync` per id. → One `UPDATE … CASE id …`.
5. **🟡 Clear-segments N+1.** `DownloadManager.ClearSegmentsAsync` (`:381-388`) per-segment delete. → Single `DELETE … WHERE download_id=$id`.
6. **🔵 O(n) per status change in the list VM.** `DownloadsListViewModel` uses `IndexOf` (`:362,:378`) and full recount `ComputeCounts` (`:391-417`) on every status change. Fine at n<100; make counts incremental (+1/−1) and use the existing `_byId` dict if large lists matter.

Cold-start and idle-RAM: no blocking work on the critical path; init/recovery run after the window paints. No leaks or unbounded allocations found.

---

## 3. Other issues worth fixing

- **🟠 Completion power-action fire-and-forget swallows errors.** `DownloadScheduler.OnStatusChanged` (`:145`) does `_ = MaybeRunCompletionActionAsync()` with no try/catch; a failing `ShutdownAsync()`/`SleepAsync()` becomes an unobserved exception surfaced only at GC. → wrap and report synchronously.
- **🟠 Enqueue fire-and-forget.** `DownloadsListViewModel.cs:189` `_ = AddNewlyEnqueuedAsync(id)` — a repo failure isn't surfaced to the user promptly. → try/catch + error state.
- **🟡 Over-narrow catch filter.** `NewDownloadViewModel.DetectAsync` (`:170-174`) only catches three exception types; a `TimeoutException`/`ArgumentException` from probing escapes as unobserved and the UI silently resets. → broaden or document.
- **Verified clean:** secret redaction (`SecretRedactor` masks auth headers/bearer/query strings, installed globally), `IAsyncDisposable` discipline (`PreallocatedFile`, `FtpTransportResponse`, `SqliteConnectionFactory`, `FfmpegRunner`), event unsubscription in `Dispose()`, parameterized SQL + WAL pragmas, cancellation threaded through I/O. No empty `catch {}` found.

---

## 4. Browser extension — issues & completeness

**Completeness:** the MV3 extension itself is **mostly built** (the README "scaffold" wording is stale): context menu, `webRequest` media sniffer, DOM scan + floating button, popup (status pill / send-link / per-site toggle / detected media), per-site blacklist with sync, and cookie/referrer/UA capture. Shared pure logic is well unit-tested. **But the extension⇄app integration does not work end-to-end today.**

- **🔴 Host manifest never registered.** No installer/first-run calls `INativeHostRegistrar.Register(...)` (only tests do), so browsers can't locate the host. → register on install/first-run; unregister on uninstall.
- **🔴 Host allowlist empty → rejects all.** `NativeHostOptions.AllowedExtensionIds` is never populated; host fails closed. → populate Chromium id(s) + Firefox `justdownload@justdownload.app`, consistent with the manifest's `allowed_origins`.
- **🔴 Hand-off drops auth context.** `ExtensionMessageHandler` captures `Referrer`/`Cookies`/`MediaKind` into `PendingLink`, but `App.axaml.cs:276` delivers via `RequestDownloadForUrl(link.Url)` (URL only) → cookie-gated/signed URLs 403. → pass the full `PendingLink` into the engine's credential context.
- **🟠 Popup "App connected" is fake.** `background.js` answers `PING` locally (never round-trips to the host), so it always shows green — masking the breakage above. → make `PING` actually reach the host.
- **🟠 Missing options page.** Popup's "Open extension settings" calls `openOptionsPage()` but no `options_ui`/`options.html` exists. → add one (host the blacklist + host name) or remove the control.
- **🟠 Over-broad permissions / cookie exfiltration.** `host_permissions: ["<all_urls>"]` + `cookies` + `webRequest`; `cookies.getAll` includes httpOnly cookies serialized into a Cookie header sent to the native process. Required for authenticated downloads, but it's the biggest attack surface. → drop unused `downloads`/`activeTab`, gather cookies only on explicit action, document the data flow.
- **🟡 Firefox `background.service_worker`** (`manifest.firefox.json`) may not load on many Firefox versions → provide a `background.scripts` fallback.
- **🟡 No fixed Chromium `key`** → extension id is non-deterministic, so the host allowlist can't be pinned for dev. → add a dev `"key"`, parameterize the published id.
- **🟡 Host logs full URLs** (`ExtensionMessageHandler.cs:143`) at Information level — verify the redactor strips signed-URL query strings here.

**Tests:** good unit coverage of `jdcore.js` and the C# host (codec, loop, origin, inbox, registrar writing files/registry) — but **nothing exercises the real native-messaging round-trip**, so the suite is green while the flow is broken.

---

## 5. Is settings functionality complete, and does it save correctly?

**Saving: ✅ yes, correct.** UI change → `ISettingsService.UpdateAsync` → diff via `SettingsSerializer` → only changed keys persisted to SQLite under a `SemaphoreSlim`; `Changed` fires after release; hydration is suppressed so loading doesn't write back. **All 11 `AppSettings` properties round-trip** through `ToStorage`/`FromStorage`. Clamping (connections 1–32, concurrency 1–16, speed ≥0) and empty-string→null handling are correct.

**Applying: ⚠️ two settings save but don't take effect.**

| Setting | Saved | Live | On restart | Consumer |
|---|---|---|---|---|
| MaxConcurrentDownloads | ✅ | ✅ | ✅ | `DownloadQueueService.cs:155` |
| ConnectionsPerDownload | ✅ | ✅ | ✅ | `NewDownloadViewModel.cs:233` |
| **GlobalSpeedLimitBytesPerSecond** | ✅ | ❌ | ❌ | **none — `TokenBucket` built unlimited at `ServiceCollectionExtensions.cs:300`; never fed the setting** |
| DefaultVideoQuality | ✅ | ✅* | ✅* | `MediaVariantPickerViewModel.cs:136` *(*but picker is never opened — §1)* |
| DefaultContainer | ✅ | ✅* | ✅* | `MediaVariantPickerViewModel.cs:70` *(* same caveat)* |
| Density | ✅ | ✅ | ✅ | `DensityService.cs:50` |
| **Theme** | ✅ | ✅ (live) | ❌ | **never re-applied on launch — nothing reads `Current.Theme`; `ThemeService` defaults Light** |
| OrganizeByCategory / Root | ✅ | ✅ | ✅ | `DownloadOrganizer.cs:33-41` |
| StartMinimizedToTray / CloseToTray | ✅ | ✅ | ✅ | `App.axaml.cs:229 / :115` |

**Two bugs to fix:**
1. **🔴 Theme ignored on restart.** After `LoadAsync`, call `IThemeService.SetMode(...)` from `settings.Current.Theme` (Light/Dark) during startup. Today a dark-theme user relaunches into light until they re-toggle.
2. **🔴 Global speed limit dead.** Feed `GlobalSpeedLimitBytesPerSecond` into the singleton `IRateLimiter` and subscribe to `ISettingsService.Changed` to call `TokenBucket.SetRate(...)` live.

**Completeness vs PRD §2.4.5:** 3 of 7 sections are real (General, Connections, Categories); Proxy/Authentication/Browsers/Advanced are placeholders (some intentionally, e.g. credentials live in the keychain). Missing real settings users will expect: **default download folder**, **run-at-OS-startup/autostart**, **notification toggle**, **update-check toggle/interval**, and the proxy/browser-management panels. Nice-to-have: input validation feedback, settings import/export.

---

## 6. In-app browser integration (the toolbar "Browsers" button)

**Both surfaces are placeholders today.**

- The toolbar globe button raises `BrowsersRequested` (`MainWindowViewModel.cs:161`) — **no subscriber exists** outside a test, so clicking it does nothing.
- The Settings "Browsers" section is static prose ("*Connected browsers will be managed here.*").
- There is **no in-app path to install the extension or register the native host** — which is the same wiring missing in §4.

**Recommendation:** make "Browsers" open a real panel that shows per-browser host-registration status and an **Install / Register** button that calls `INativeHostRegistrar.Register(...)` (fixes #1 in §4 at the same time).

---

## 7. Feature gaps vs other download managers

All suggestions fit the "light, fast, FOSS, no-telemetry" positioning and respect D1–D9.

**Quick wins (≈1 week each, high value):**
- **Auto-retry with backoff** (IDM/FDM/JDownloader) — network glitches are the #1 cause of failed downloads; persist retry count.
- **Checksum verify (MD5/SHA-256)** (IDM/FDM/XDM) — post-complete, optional; we already compute SHA-256 in tests.
- **Clipboard URL monitoring (opt-in)** (Motrix/aria2 GUIs) — copy-link → auto-queue; `IClipboardService` already exists.
- **Download history search/filter** (NDM/FDM/IDM) — query by name/URL/date/status.
- **Default download folder setting** — currently missing from Settings; trivial and expected.

**Medium (2–4 weeks):**
- **Post-download command/script hook** (IDM/JDownloader) — run move/scan/extract on completion.
- **Archive auto-extract** (`.zip` via `System.IO.Compression`) (IDM/FDM).
- **Portable mode** (no registry; config in app folder) (aria2/XDM/FDM).
- **Bandwidth dashboard** (per-download + aggregate graph) — visually proves the segmentation engine works.
- **Per-category concurrency limits** (NDM/FDM).
- **URL pre-check (HEAD)** before queuing to catch 404s/sizes.

**Big rocks (sign-off needed):**
- **Multi-mirror / multi-source failover** (IDM/aria2) — pairs with auto-retry; needs `AlternateUrls` on the model + a picker.
- **Bandwidth schedule by time-of-day** (depends on a solid scheduler) — fits once the speed limiter actually works (§5).
- **i18n / localization** — large but high reach; consider community translations.
- **Auto-update via GitHub Releases (opt-in, signature-verified)** — must stay telemetry-free (D2).

**Deliberately out of scope (locked decisions / non-goals):** torrents/magnets, bundling yt-dlp (D3 — keep `IMediaExtractor` pluggable for a future sidecar), transcoding (stream-copy only), mobile apps, telemetry/accounts/cloud sync/paid tiers (D2 — the key differentiator). A **CLI over `JustDownload.Core`** and an optional **web remote** are reasonable v2 parking-lot items.

---

## Suggested sequencing

1. **Make the browser feature real** (register host on first-run + populate allowlist + pass full `PendingLink`) and wire the "Browsers" panel — turns three `done`-but-dead tasks into a working flow. *(High)*
2. **Fix the two dead settings** (theme-on-startup, global speed limit). *(High, small)*
3. **Open the media variant picker** from the new-download/extension flow. *(Med)*
4. **Batch the checkpoint writes + coalesce progress events.** *(Med — directly serves the D9 "light & fast" promise.)*
5. Harden durability (fsync before checkpoint) and prove it with a real power-loss test. *(Med)*
6. Then pick quick-win features (auto-retry, checksum, clipboard, history, default folder).
