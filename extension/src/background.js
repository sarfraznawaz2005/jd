// JustDownload — background script (MV3)
//
// Forwards links and detected media to the desktop app over Native Messaging,
// with the auth context (cookies, referrer, user-agent) for authenticated
// downloads (TASK-067). The media sniffer + floating button (TASK-068), the
// per-site blacklist (TASK-069), and launch-if-not-running / queue (TASK-070)
// build on the seams here.
"use strict";

// Load the shared core. Chrome/Edge run this as a service worker, where
// importScripts pulls jdcore in; Firefox MV3 runs it as an event page that has
// no importScripts, so its manifest lists jdcore.js first in background.scripts
// and globalThis.JD is already defined here (TASK-097).
if (typeof importScripts === "function") {
  importScripts("jdcore.js");
}

const api = globalThis.browser ?? globalThis.chrome;

// Native Messaging Host name — must match the host manifest the desktop app
// registers (TASK-065), per locked decision D8.
const NATIVE_HOST = "app.justdownload.host";

const MENU_ROOT = "jd-download";

// Media detected per tab via the network sniffer + content-script DOM scan (TASK-068).
const mediaStore = JD.createMediaStore();

/** The user's per-site blacklist (TASK-069), read from sync storage (default empty). */
async function getBlacklist() {
  try {
    const stored = await api.storage.sync.get("blacklist");
    return Array.isArray(stored?.blacklist) ? stored.blacklist : [];
  } catch {
    return [];
  }
}

// Sniff the network for media requests (HLS/DASH/MP4/audio) so a page with a
// playing video offers a download even when the URL never appears as a link.
api.webRequest.onBeforeRequest.addListener(
  (details) => {
    if (details.tabId < 0) {
      return; // not tied to a tab (e.g. the worker itself)
    }
    const kind = JD.classifyMedia(details.url);
    if (kind && mediaStore.add(details.tabId, { url: details.url, kind })) {
      void onMediaDetected(details.tabId);
    }
  },
  { urls: ["<all_urls>"] },
);

/** Updates the badge and tells the tab's content script whether to show the button. */
async function onMediaDetected(tabId) {
  const count = mediaStore.count(tabId);
  let pageUrl = "";
  try {
    pageUrl = (await api.tabs.get(tabId))?.url ?? "";
  } catch {
    pageUrl = "";
  }

  const show = JD.shouldShowFloatingButton(count, pageUrl, await getBlacklist());
  try {
    await api.action.setBadgeText({ tabId, text: show && count > 0 ? String(count) : "" });
  } catch {
    /* the action API may be unavailable on some pages */
  }

  if (show) {
    api.tabs.sendMessage(tabId, { type: "SHOW_MEDIA_BUTTON", count }).catch(() => {
      // The content script may not be injected on this page; ignore.
    });
  }
}

// Forget a tab's media when it navigates away or closes.
api.tabs.onUpdated.addListener((tabId, changeInfo) => {
  if (changeInfo.status === "loading" && changeInfo.url) {
    mediaStore.clear(tabId);
    api.action.setBadgeText({ tabId, text: "" }).catch(() => {});
  }
});
api.tabs.onRemoved.addListener((tabId) => mediaStore.clear(tabId));

api.runtime.onInstalled.addListener(() => {
  api.contextMenus.removeAll(() => {
    // One entry that appears on links and media elements (TASK-067 AC0).
    api.contextMenus.create({
      id: MENU_ROOT,
      title: "Download with JustDownload",
      contexts: ["link", "image", "video", "audio"],
    });
  });
});

api.contextMenus.onClicked.addListener((info, tab) => {
  if (info.menuItemId === MENU_ROOT) {
    const url = JD.pickContextUrl(info);
    if (url) {
      void sendDownload(url, tab, info.pageUrl || tab?.url || null);
    }
  }
});

/**
 * Forwards a download to the desktop app with its auth context (TASK-067 AC1):
 * the site's cookies as a Cookie header, the referrer, and the browser UA.
 * @param {string} url
 * @param {{id?: number}=} tab
 * @param {string|null} pageUrl
 * @param {string|null} mediaKind
 */
async function sendDownload(url, tab, pageUrl, mediaKind = null) {
  const cookies = await collectCookieHeader(url);
  const message = JD.buildDownloadMessage({
    url,
    pageUrl,
    referrer: pageUrl,
    cookies,
    headers: { "User-Agent": navigator.userAgent },
    mediaKind,
  });
  return forwardToApp(message);
}

/** Reads the cookies the browser would send for `url` and serializes them. */
async function collectCookieHeader(url) {
  try {
    const cookies = await api.cookies.getAll({ url });
    return JD.formatCookieHeader(cookies);
  } catch {
    return "";
  }
}

/**
 * Sends a message to the desktop app over Native Messaging. If the host/app is
 * not running the send fails; TASK-070 adds launch-or-queue handling here.
 * @param {object} message
 * @returns {Promise<boolean>} whether the app accepted it
 */
function forwardToApp(message) {
  return new Promise((resolve) => {
    try {
      api.runtime.sendNativeMessage(NATIVE_HOST, message, (response) => {
        if (api.runtime.lastError) {
          console.warn("[JustDownload] native send failed:", api.runtime.lastError.message);
          resolve(false);
          return;
        }
        resolve(Boolean(response && response.ok !== false));
      });
    } catch (err) {
      console.warn("[JustDownload] native send threw:", err);
      resolve(false);
    }
  });
}

// Messages from the popup and content script (the in-page button, detected media).
api.runtime.onMessage.addListener((message, sender, sendResponse) => {
  switch (message?.type) {
    case "PING":
      // Actually reach the desktop app's native host so the popup reflects the real connection state —
      // "connected" only when the host answers with a pong (TASK-094).
      try {
        api.runtime.sendNativeMessage(NATIVE_HOST, { type: "ping" }, (response) => {
          sendResponse({
            ok: !api.runtime.lastError && response?.type === "pong",
            host: NATIVE_HOST,
          });
        });
      } catch {
        sendResponse({ ok: false, host: NATIVE_HOST });
      }
      return true; // async response

    case "DOWNLOAD_LINK":
      void sendDownload(
        message.url,
        sender?.tab,
        message.pageUrl || sender?.tab?.url || null,
        message.mediaKind,
      ).then((forwarded) => sendResponse({ ok: true, forwarded }));
      return true; // async response

    case "MEDIA_DETECTED": {
      // A media element the content script found in the DOM (TASK-068 AC0).
      const tabId = sender?.tab?.id;
      const kind = JD.classifyMedia(message.url) ?? "video";
      if (typeof tabId === "number" && message.url && mediaStore.add(tabId, { url: message.url, kind })) {
        void onMediaDetected(tabId);
      }
      sendResponse({ ok: true });
      break;
    }

    case "SYNC_BLACKLIST":
      // The popup changed the per-site blacklist; push it to the desktop app (TASK-069 AC1).
      void forwardToApp(JD.buildBlacklistSyncMessage(message.blacklist)).then((ok) =>
        sendResponse({ ok }),
      );
      return true;

    case "GET_SETTINGS":
      // The popup asks the desktop app for its current settings (TASK-071 AC2).
      try {
        api.runtime.sendNativeMessage(NATIVE_HOST, { type: "get_settings" }, (response) => {
          sendResponse({ ok: !api.runtime.lastError, settings: response ?? null });
        });
      } catch {
        sendResponse({ ok: false, settings: null });
      }
      return true;

    case "GET_TAB_MEDIA": {
      // The popup / floating button asks what was detected for a tab (TASK-068/071).
      const tabId = message.tabId ?? sender?.tab?.id;
      sendResponse({ ok: true, media: typeof tabId === "number" ? mediaStore.get(tabId) : [] });
      break;
    }

    case "DOWNLOAD_DETECTED_MEDIA": {
      // The floating button / popup asks to download the media detected for a tab (TASK-068 AC2).
      const tabId = message.tabId ?? sender?.tab?.id;
      const tab = sender?.tab;
      const items = typeof tabId === "number" ? mediaStore.get(tabId) : [];
      const target = message.url ? items.find((m) => m.url === message.url) ?? items[0] : items[0];
      if (target) {
        void sendDownload(target.url, tab, tab?.url || null, target.kind).then((forwarded) =>
          sendResponse({ ok: true, forwarded }),
        );
        return true;
      }
      sendResponse({ ok: false, error: "no_media" });
      break;
    }

    default:
      sendResponse({ ok: false, error: "unknown_message_type" });
  }
  return true;
});
