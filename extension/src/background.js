// JustDownload — background script (MV3)
//
// Forwards links and detected media to the desktop app over Native Messaging,
// with the auth context (cookies, referrer, user-agent) for authenticated
// downloads (TASK-067). The media sniffer feeding the per-video download icons'
// blob:-URL fallback (TASK-068/164/181), the per-site blacklist (TASK-069),
// automatic download takeover (TASK-183), and launch-if-not-running / queue
// (TASK-070) build on the seams here.
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

/** Whether automatic download takeover (TASK-183) is on — default on, opt-out via the popup toggle. */
async function isInterceptEnabled() {
  try {
    const stored = await api.storage.sync.get("interceptDownloads");
    return stored?.interceptDownloads !== false;
  } catch {
    return true;
  }
}

// The app's own AppSettings.VideoCaptureEnabled (TASK-185), mirrored here so the extension's media
// sniffer and icon overlay actually stop when the user turns it off in Settings — before this, the flag
// was never even exposed to the extension (GET_SETTINGS didn't include it), so the two were completely
// independent and the app setting had zero effect on the extension. There is no push notification when
// the app's settings change, so this is refreshed opportunistically: on install/startup (alongside the
// existing ping) and every time the popup asks for settings — good enough since a user who just flipped
// the setting in the app naturally reopens the popup around the same time to check.
let videoCaptureEnabled = true;

async function refreshVideoCaptureCache() {
  try {
    const settings = await new Promise((resolve) => {
      api.runtime.sendNativeMessage(NATIVE_HOST, { type: "get_settings" }, (response) => {
        resolve(api.runtime.lastError ? null : response);
      });
    });
    if (typeof settings?.videoCaptureEnabled === "boolean") {
      videoCaptureEnabled = settings.videoCaptureEnabled;
    }
  } catch {
    /* app/host unreachable — keep the last-known value */
  }
}

// Automatic download takeover (TASK-183): every real download the browser starts is handed to the app
// instead, IDM/FDM-style — this is the actual point of a download manager, not just a manual right-click
// action. chrome.downloads.onCreated fires as soon as the browser begins a download; cancel it immediately
// and forward the same URL through the existing native-messaging path (auth context, launch-or-notify —
// TASK-070/182 — all already handled by sendDownload/forwardToApp). blob:/data: URLs are skipped: they're
// page-local, in-memory content with no real network address the desktop app could ever fetch (the same
// limitation the content-script icon overlay has, TASK-181) — the browser is the only thing that can save
// those, so they're left alone.
if (api.downloads?.onCreated) {
  api.downloads.onCreated.addListener((item) => {
    void handleBrowserDownload(item);
  });
}

async function handleBrowserDownload(item) {
  if (!item?.url || item.url.startsWith("blob:") || item.url.startsWith("data:")) {
    return;
  }
  if (!(await isInterceptEnabled())) {
    return;
  }

  const blacklist = await getBlacklist();
  if (item.referrer && JD.isBlacklisted(item.referrer, blacklist)) {
    return; // the site the download came from is on the "don't take over here" list (TASK-069's list)
  }

  try {
    await api.downloads.cancel(item.id);
    await api.downloads.erase({ id: item.id });
  } catch {
    // already finished, already removed, or cancel unsupported for this item — forward anyway
  }

  void sendDownload(item.url, null, item.referrer || null);
}

// Sniff the network for media requests (HLS/DASH/MP4/audio) so a page with a playing video can still be
// downloaded even when the URL never appears as a link. Feeds the per-video icon overlay's blob:-URL
// fallback (TASK-181) via GET_TAB_MEDIA — there is no popup UI or toolbar badge surfacing this list
// directly (TASK-187): it's plumbing for the icon overlay, not a user-facing feature of its own. Gated on
// videoCaptureEnabled (TASK-185) so turning the app setting off genuinely stops detection.
api.webRequest.onBeforeRequest.addListener(
  (details) => {
    if (details.tabId < 0 || !videoCaptureEnabled) {
      return; // not tied to a tab (e.g. the worker itself), or video capture is off in Settings
    }
    const kind = JD.classifyMedia(details.url);
    if (kind) {
      mediaStore.add(details.tabId, { url: details.url, kind });
    }
  },
  { urls: ["<all_urls>"] },
);

// Forget a tab's media when it navigates away or closes.
api.tabs.onUpdated.addListener((tabId, changeInfo) => {
  if (changeInfo.status === "loading" && changeInfo.url) {
    mediaStore.clear(tabId);
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
  pingHost(); // announce this install to the desktop app right away (TASK-175), not just on popup-open
  void refreshVideoCaptureCache();
});

// Re-announce on every browser startup too, so the app's "last contacted" state stays fresh across
// sessions rather than only reflecting a one-time install ping (TASK-175).
api.runtime.onStartup.addListener(() => {
  pingHost();
  void refreshVideoCaptureCache();
});

/** Fire-and-forget native-messaging ping, purely to register real contact with the desktop app. */
function pingHost() {
  try {
    api.runtime.sendNativeMessage(NATIVE_HOST, { type: "ping" }, () => {
      void api.runtime.lastError; // the host may not be running yet; nothing to do either way
    });
  } catch {
    /* native messaging unavailable (e.g. host not installed yet) — nothing to do */
  }
}

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
      if (typeof tabId === "number" && message.url) {
        mediaStore.add(tabId, { url: message.url, kind });
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
      // The popup (or content.js, checking videoCaptureEnabled before scanning) asks the desktop app for
      // its current settings (TASK-071 AC2). Also refreshes the sniffer's cached videoCaptureEnabled
      // (TASK-185) from this same response, piggybacking on a round-trip that's happening anyway rather
      // than making a second one.
      try {
        api.runtime.sendNativeMessage(NATIVE_HOST, { type: "get_settings" }, (response) => {
          if (!api.runtime.lastError && typeof response?.videoCaptureEnabled === "boolean") {
            videoCaptureEnabled = response.videoCaptureEnabled;
          }
          sendResponse({ ok: !api.runtime.lastError, settings: response ?? null });
        });
      } catch {
        sendResponse({ ok: false, settings: null });
      }
      return true;

    case "GET_TAB_MEDIA": {
      // content.js's icon-overlay fallback asks what the sniffer detected for its tab (TASK-181) — the
      // only remaining consumer since the popup's own media list was removed (TASK-187).
      const tabId = message.tabId ?? sender?.tab?.id;
      sendResponse({ ok: true, media: typeof tabId === "number" ? mediaStore.get(tabId) : [] });
      break;
    }

    default:
      sendResponse({ ok: false, error: "unknown_message_type" });
  }
  return true;
});
