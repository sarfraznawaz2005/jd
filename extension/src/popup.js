// JustDownload — popup logic (MV3)
//
// SCAFFOLD: wires the popup UI to the background worker. Real media listing,
// link sending, and blacklist persistence land in later tasks.
(() => {
  "use strict";

  const api = globalThis.browser ?? globalThis.chrome;

  /** Reflect app-connection state in the header pill. */
  async function refreshStatus() {
    const pill = document.getElementById("status");
    const text = document.getElementById("status-text");
    try {
      const res = await api.runtime.sendMessage({ type: "PING" });
      if (res?.ok) {
        pill.classList.add("connected");
        text.textContent = "App connected";
        return;
      }
    } catch {
      // fall through to disconnected state
    }
    pill.classList.remove("connected");
    text.textContent = "App not running";
  }

  function wireInteractions() {
    document.getElementById("send-link")?.addEventListener("click", async () => {
      const [tab] = await api.tabs.query({ active: true, currentWindow: true });
      if (tab?.url) {
        await api.runtime.sendMessage({ type: "DOWNLOAD_LINK", url: tab.url });
      }
    });

    document.getElementById("site-toggle")?.addEventListener("click", (e) => {
      const el = e.currentTarget;
      const on = el.classList.toggle("on");
      el.setAttribute("aria-checked", String(on));
      // TODO(TASK-071): persist per-site blacklist via api.storage.
    });

    document.getElementById("open-settings")?.addEventListener("click", () => {
      api.runtime.openOptionsPage?.();
    });
  }

  document.addEventListener("DOMContentLoaded", () => {
    wireInteractions();
    void refreshStatus();
  });
})();
