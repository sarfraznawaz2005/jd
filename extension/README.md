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
│   └── icons/               ← generated placeholder PNGs (by build.js)
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
npm run clean              # remove dist/
```

Each `dist/<browser>/` contains a valid `manifest.json` plus the shared assets
(`background.js`, `content.js`, `popup.*`, `icons/`) and is ready to load
unpacked. Zipping for store upload is intentionally left out of the scaffold —
zip a `dist/<browser>/` folder when a release is needed.

## Load the unpacked extension

**Chrome** — `chrome://extensions` → enable *Developer mode* → *Load unpacked* →
select `extension/dist/chrome/`.

**Edge** — `edge://extensions` → enable *Developer mode* → *Load unpacked* →
select `extension/dist/edge/`.

**Firefox** — `about:debugging#/runtime/this-firefox` → *Load Temporary Add-on…*
→ pick `extension/dist/firefox/manifest.json`. (Temporary add-ons are removed on
restart; that is expected for development.)
