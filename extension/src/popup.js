// JustDownload — popup logic (MV3)
//
// Wires the popup to the background worker: app-connection status, sending the
// current page link, and the per-site "show download button" toggle whose state
// is the inverse of the blacklist — toggling it off blacklists the site so the
// floating button never shows (TASK-069), persisting to sync storage and pushing
// the blacklist to the desktop app. The detected-media list is filled in TASK-071.
(() => {
  "use strict";

  const api = globalThis.browser ?? globalThis.chrome;

  let currentHost = null;

  async function activeTab() {
    const [tab] = await api.tabs.query({ active: true, currentWindow: true });
    return tab ?? null;
  }

  async function getBlacklist() {
    const stored = await api.storage.sync.get("blacklist");
    return Array.isArray(stored?.blacklist) ? stored.blacklist : [];
  }

  /** Reflect app-connection state in the header pill. */
  async function refreshStatus() {
    const pill = document.getElementById("status");
    const text = document.getElementById("status-text");
    try {
      const res = await api.runtime.sendMessage({ type: "PING" });
      if (res?.ok) {
        pill?.classList.add("connected");
        if (text) {
          text.textContent = "App connected";
        }
        return;
      }
    } catch {
      // fall through to disconnected state
    }
    pill?.classList.remove("connected");
    if (text) {
      text.textContent = "App not running";
    }
  }

  /** Initialise the per-site toggle from the stored blacklist (TASK-069). */
  async function initSiteToggle() {
    const toggle = document.getElementById("site-toggle");
    const tab = await activeTab();
    currentHost = tab?.url ? JD.hostnameOf(tab.url) : null;

    const blacklist = await getBlacklist();
    const blocked = tab?.url ? JD.isBlacklisted(tab.url, blacklist) : false;
    // Toggle ON = show the button here = NOT blacklisted.
    setToggle(toggle, !blocked);
  }

  function setToggle(toggle, on) {
    if (!toggle) {
      return;
    }
    toggle.classList.toggle("on", on);
    toggle.setAttribute("aria-checked", String(on));
  }

  /** Persist the blacklist and push it to the desktop app (TASK-069 AC1). */
  async function applyBlacklist(blacklist) {
    await api.storage.sync.set({ blacklist });
    await api.runtime.sendMessage({ type: "SYNC_BLACKLIST", blacklist }).catch(() => {});
  }

  function wireInteractions() {
    document.getElementById("send-link")?.addEventListener("click", async () => {
      const tab = await activeTab();
      if (tab?.url) {
        await api.runtime.sendMessage({ type: "DOWNLOAD_LINK", url: tab.url });
      }
    });

    document.getElementById("site-toggle")?.addEventListener("click", async (e) => {
      const el = e.currentTarget;
      const on = !el.classList.contains("on");
      setToggle(el, on);
      if (!currentHost) {
        return;
      }
      let blacklist = await getBlacklist();
      // ON = not blacklisted (remove); OFF = blacklisted (add).
      blacklist = on ? JD.removeFromBlacklist(blacklist, currentHost) : JD.addToBlacklist(blacklist, currentHost);
      await applyBlacklist(blacklist);
    });

    document.getElementById("open-settings")?.addEventListener("click", () => {
      api.runtime.openOptionsPage?.();
    });
  }

  /** Lists the media detected on the active tab, each with a download action (TASK-071 AC0). */
  async function renderMedia() {
    const empty = document.getElementById("media-empty");
    const list = document.getElementById("media-list");
    if (!list) {
      return;
    }

    const tab = await activeTab();
    const res = await api.runtime.sendMessage({ type: "GET_TAB_MEDIA", tabId: tab?.id }).catch(() => null);
    const media = Array.isArray(res?.media) ? res.media : [];

    list.replaceChildren();
    if (media.length === 0) {
      if (empty) {
        empty.hidden = false;
      }
      list.hidden = true;
      return;
    }

    if (empty) {
      empty.hidden = true;
    }
    list.hidden = false;
    for (const item of media) {
      const row = document.createElement("button");
      row.type = "button";
      row.className = "media-item";
      row.textContent = JD.mediaLabel(item);
      row.title = item.url;
      row.addEventListener("click", () => {
        api.runtime.sendMessage({ type: "DOWNLOAD_DETECTED_MEDIA", tabId: tab?.id, url: item.url }).catch(() => {});
      });
      list.appendChild(row);
    }
  }

  /** Shows the desktop app's default quality so popup settings stay in sync (TASK-071 AC2). */
  async function syncAppSettings() {
    const res = await api.runtime.sendMessage({ type: "GET_SETTINGS" }).catch(() => null);
    const quality = res?.settings?.defaultVideoQuality;
    const el = document.getElementById("default-quality");
    if (el && typeof quality === "number") {
      el.textContent = `${quality}p`;
    }
  }

  document.addEventListener("DOMContentLoaded", () => {
    wireInteractions();
    void refreshStatus();
    void initSiteToggle();
    void renderMedia();
    void syncAppSettings();
  });
})();
