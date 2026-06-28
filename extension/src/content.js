// JustDownload — content script (MV3)
//
// Detects in-page media (DOM <video>/<audio> sources) and shows a floating
// "Download" button when the background worker reports media on this page
// (TASK-068). The button hands the detected media to the desktop app, which
// fetches it with the page's auth context (TASK-067/068 AC2). jdcore.js is
// injected before this script (manifest content_scripts).
(() => {
  "use strict";

  const api = globalThis.browser ?? globalThis.chrome;

  if (window.__justDownloadInjected) {
    return;
  }
  window.__justDownloadInjected = true;

  const BUTTON_ID = "jd-floating-download";

  /** Reports any media element sources in the DOM to the background sniffer. */
  function scanDomForMedia() {
    const sources = new Set();
    for (const el of document.querySelectorAll("video[src], audio[src], source[src]")) {
      const src = el.getAttribute("src");
      if (src) {
        try {
          sources.add(new URL(src, document.baseURI).href);
        } catch {
          /* skip unparseable src */
        }
      }
    }
    for (const url of sources) {
      api.runtime.sendMessage({ type: "MEDIA_DETECTED", url }).catch(() => {});
    }
  }

  /** Injects the floating download button once (idempotent). */
  function showFloatingButton(count) {
    if (document.getElementById(BUTTON_ID)) {
      updateButtonCount(count);
      return;
    }

    const button = document.createElement("button");
    button.id = BUTTON_ID;
    button.type = "button";
    button.textContent = labelFor(count);
    Object.assign(button.style, {
      position: "fixed",
      right: "20px",
      bottom: "20px",
      zIndex: "2147483647",
      padding: "10px 14px",
      borderRadius: "10px",
      border: "none",
      background: "#3b82f6",
      color: "#fff",
      font: "600 13px system-ui, sans-serif",
      boxShadow: "0 6px 20px rgba(0,0,0,0.25)",
      cursor: "pointer",
    });
    button.addEventListener("click", () => {
      api.runtime.sendMessage({ type: "DOWNLOAD_DETECTED_MEDIA" }).catch(() => {});
    });
    document.body.appendChild(button);
  }

  function updateButtonCount(count) {
    const button = document.getElementById(BUTTON_ID);
    if (button) {
      button.textContent = labelFor(count);
    }
  }

  function labelFor(count) {
    return count > 1 ? `Download media (${count})` : "Download media";
  }

  // The background worker tells us when media has been detected for this tab.
  api.runtime.onMessage.addListener((message) => {
    if (message?.type === "SHOW_MEDIA_BUTTON") {
      showFloatingButton(message.count ?? 1);
    }
  });

  scanDomForMedia();
})();
