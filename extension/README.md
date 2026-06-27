# JustDownload Browser Extension

Manifest V3 browser extension that hands downloads and detected media off to the
JustDownload desktop app via the Native Messaging Host (`JustDownload.NativeHost`),
per locked decision **D8**.

This is the **scaffold** (TASK-066): structure, a working per-browser build, a
stub background service worker, a stub content script, and the popup UI styled to
match `mockups/extension.html`. The real network sniffer, context-menu action,
and per-site blacklist arrive in later tasks (TASK-067/068/071).

## Layout

```
extension/
├── src/                     ← single shared source for all browsers
│   ├── manifest.base.json   ← shared manifest fields
│   ├── manifest.chrome.json ← Chromium override (background.service_worker)
│   ├── manifest.edge.json   ← Edge override (same as Chrome)
│   ├── manifest.firefox.json← Firefox override (+ browser_specific_settings.gecko)
│   ├── background.js        ← MV3 service worker (stub message handler)
│   ├── content.js           ← content script (stub)
│   ├── popup.html/.css/.js  ← extension popup (modern-minimal, light + dark)
│   ├── icon.svg             ← brand-mark master (source of truth for the icons)
│   └── icons/               ← real PNG icons rendered from icon.svg (committed)
├── scripts/
│   ├── gen-icons.js         ← renders src/icons/*.png from the brand mark (anti-aliased)
│   └── zip.js               ← packages dist/<browser>/ → dist/<browser>.zip (no deps)
├── build.js                 ← zero-dependency Node build (merge + copy)
├── package.json             ← npm scripts
└── dist/                    ← build output, one folder per browser (git-ignored)
```

There is **no copy-paste manifest**: each browser's `manifest.json` is
`manifest.base.json` deep-merged with its small per-browser override at build
time, so the three never drift apart.

## Build

Requires Node 18+ (uses `zlib.crc32`). No dependencies to install.

```bash
cd extension
npm run build              # builds dist/chrome, dist/edge, dist/firefox
# or: node build.js
```

Other scripts:

```bash
npm run build:chrome       # single target (also :edge, :firefox)
npm run gen-icons          # re-render src/icons/*.png from src/icon.svg
npm run package            # build, then zip each dist/<browser>/ → dist/<browser>.zip
npm run zip                # zip already-built dist/<browser>/ folders
npm run clean              # remove dist/
```

Each `dist/<browser>/` contains a valid `manifest.json` plus the shared assets
(`background.js`, `content.js`, `popup.*`, `icons/`) and is ready to load
unpacked.

## Icons

`src/icon.svg` is the brand-mark master (a rounded-square accent gradient with a
white download glyph, matching the app/mockup logo). `npm run gen-icons` renders
it to anti-aliased `src/icons/icon-{16,32,48,128}.png` — the raster sizes
Chrome/Edge require — using a zero-dependency Node renderer. Those PNGs are
**committed**, so a fresh checkout loads unpacked without a build; `build.js` only
regenerates them if a checkout is missing them.

## Packaging for store upload

`npm run package` builds all targets and writes a store-uploadable
`dist/chrome.zip`, `dist/edge.zip`, and `dist/firefox.zip` (a minimal,
dependency-free, deterministic ZIP writer). Upload the matching archive to each
browser's developer dashboard.

## Load the unpacked extension

**Chrome** — `chrome://extensions` → enable *Developer mode* → *Load unpacked* →
select `extension/dist/chrome/`.

**Edge** — `edge://extensions` → enable *Developer mode* → *Load unpacked* →
select `extension/dist/edge/`.

**Firefox** — `about:debugging#/runtime/this-firefox` → *Load Temporary Add-on…*
→ pick `extension/dist/firefox/manifest.json`. (Temporary add-ons are removed on
restart; that is expected for development.)
