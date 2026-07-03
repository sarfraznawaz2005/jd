// JustDownload — popup logic (MV3)
//
// Wires the popup to the background worker: app-connection status, the per-site "detect videos on this
// site" toggle whose state is the inverse of the blacklist — toggling it off blacklists the site so the
// icon overlay never shows (TASK-069), persisting to sync storage and pushing the blacklist to the desktop
// app — and the global "take over browser downloads" toggle (TASK-183). No detected-media list and no
// "send this page link" button here (TASK-187/188): the per-video icon overlay (content.js), automatic
// download takeover (TASK-183), and the right-click context menu already cover detection/download end to
// end, so both were just redundant, lower-value paths to the same outcome.
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

  /** Initialise the global download-takeover toggle (TASK-183), default on. */
  async function initInterceptToggle() {
    const stored = await api.storage.sync.get("interceptDownloads");
    setToggle(document.getElementById("intercept-toggle"), stored?.interceptDownloads !== false);
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

    document.getElementById("intercept-toggle")?.addEventListener("click", async (e) => {
      const el = e.currentTarget;
      const on = !el.classList.contains("on");
      setToggle(el, on);
      await api.storage.sync.set({ interceptDownloads: on });
    });

    document.getElementById("open-settings")?.addEventListener("click", () => {
      api.runtime.openOptionsPage?.();
    });
  }

  document.addEventListener("DOMContentLoaded", () => {
    wireInteractions();
    void refreshStatus();
    void initSiteToggle();
    void initInterceptToggle();
  });
})();
