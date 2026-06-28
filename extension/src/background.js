// JustDownload — background service worker (MV3)
//
// Forwards links and detected media to the desktop app over Native Messaging,
// with the auth context (cookies, referrer, user-agent) for authenticated
// downloads (TASK-067). The media sniffer + floating button (TASK-068), the
// per-site blacklist (TASK-069), and launch-if-not-running / queue (TASK-070)
// build on the seams here.
"use strict";

importScripts("jdcore.js");

const api = globalThis.browser ?? globalThis.chrome;

// Native Messaging Host name — must match the host manifest the desktop app
// registers (TASK-065), per locked decision D8.
const NATIVE_HOST = "app.justdownload.host";

const MENU_ROOT = "jd-download";

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
      sendResponse({ ok: true, type: "PONG", host: NATIVE_HOST });
      break;

    case "DOWNLOAD_LINK":
      void sendDownload(
        message.url,
        sender?.tab,
        message.pageUrl || sender?.tab?.url || null,
        message.mediaKind,
      ).then((forwarded) => sendResponse({ ok: true, forwarded }));
      return true; // async response

    default:
      sendResponse({ ok: false, error: "unknown_message_type" });
  }
  return true;
});
