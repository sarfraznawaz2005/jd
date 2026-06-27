// JustDownload — background service worker (MV3)
//
// SCAFFOLD: this is a stub. Later tasks (TASK-067/068/071) add the real
// network sniffer, context-menu integration, and per-site blacklist logic.
//
// Cross-browser: Firefox exposes the WebExtension API as `browser`, Chromium
// as `chrome`. We alias so the same source runs unmodified on both.
const api = globalThis.browser ?? globalThis.chrome;

// Native Messaging Host name — must match the host manifest registered by the
// desktop app (JustDownload.NativeHost), per locked decision D8.
const NATIVE_HOST = "app.justdownload.host";

/**
 * Stub message handler. The popup and content script post typed messages
 * here; for now we only acknowledge them so the seams are wired and testable.
 * @param {{type?: string}} message
 */
function handleMessage(message, _sender, sendResponse) {
  switch (message?.type) {
    case "PING":
      sendResponse({ ok: true, type: "PONG", host: NATIVE_HOST });
      break;

    case "DOWNLOAD_LINK":
      // TODO(TASK-067): forward the URL to the desktop app via Native Messaging.
      sendResponse({ ok: true, type: "ACCEPTED", forwarded: false });
      break;

    case "MEDIA_DETECTED":
      // TODO(TASK-068): record detected media for the popup / floating button.
      sendResponse({ ok: true, type: "ACK" });
      break;

    default:
      sendResponse({ ok: false, error: "unknown_message_type" });
  }
  // Returning true keeps the message channel open for the async response.
  return true;
}

api.runtime.onMessage.addListener(handleMessage);

api.runtime.onInstalled.addListener(() => {
  // Placeholder: context menus and defaults get registered in later tasks.
  console.info("[JustDownload] extension installed (scaffold).");
});
