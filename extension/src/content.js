// JustDownload — content script (MV3)
//
// SCAFFOLD: this is a stub. Later tasks add real media detection and the
// in-page floating download button (see mockups/extension.html). For now it
// only proves the content-script seam is wired and can talk to the worker.
(() => {
  "use strict";

  const api = globalThis.browser ?? globalThis.chrome;

  // Guard against double-injection (e.g. SPA navigations re-running scripts).
  if (window.__justDownloadInjected) {
    return;
  }
  window.__justDownloadInjected = true;

  // TODO(TASK-068): scan the DOM / network for downloadable media and, when
  // found, inject the floating "Download" button shown in the mockup, then
  // notify the background worker via { type: "MEDIA_DETECTED", ... }.
  api.runtime.sendMessage({ type: "PING" }).catch(() => {
    // The worker may be asleep; safe to ignore in the scaffold.
  });
})();
