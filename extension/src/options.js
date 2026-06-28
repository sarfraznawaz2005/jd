// JustDownload — options page (MV3)
//
// Manages the full per-site blacklist (the popup only toggles the current site) and shows the live
// app-connection status. Reuses the shared JD blacklist helpers and persists to sync storage, then pushes
// the list to the desktop app so its blacklist table stays in sync (TASK-095, mirrors popup.js).
(() => {
  "use strict";

  const api = globalThis.browser ?? globalThis.chrome;

  async function getBlacklist() {
    const stored = await api.storage.sync.get("blacklist");
    return Array.isArray(stored?.blacklist) ? stored.blacklist : [];
  }

  async function applyBlacklist(blacklist) {
    await api.storage.sync.set({ blacklist });
    await api.runtime.sendMessage({ type: "SYNC_BLACKLIST", blacklist }).catch(() => {});
  }

  async function refreshStatus() {
    const pill = document.getElementById("status");
    const text = document.getElementById("status-text");
    let connected = false;
    try {
      const res = await api.runtime.sendMessage({ type: "PING" });
      connected = Boolean(res?.ok);
    } catch {
      connected = false;
    }
    pill?.classList.toggle("connected", connected);
    if (text) {
      text.textContent = connected ? "App connected" : "App not running";
    }
  }

  async function render() {
    const list = document.getElementById("list");
    const empty = document.getElementById("empty");
    if (!list) {
      return;
    }

    const blacklist = await getBlacklist();
    list.replaceChildren();
    if (empty) {
      empty.hidden = blacklist.length > 0;
    }

    for (const domain of blacklist) {
      const li = document.createElement("li");
      const label = document.createElement("span");
      label.textContent = domain;
      const remove = document.createElement("button");
      remove.type = "button";
      remove.textContent = "Remove";
      remove.addEventListener("click", async () => {
        await applyBlacklist(JD.removeFromBlacklist(await getBlacklist(), domain));
        await render();
      });
      li.append(label, remove);
      list.appendChild(li);
    }
  }

  function wire() {
    const input = document.getElementById("add-input");
    const add = document.getElementById("add-button");
    const submit = async () => {
      const value = input?.value.trim();
      if (!value) {
        return;
      }
      await applyBlacklist(JD.addToBlacklist(await getBlacklist(), value));
      if (input) {
        input.value = "";
      }
      await render();
    };
    add?.addEventListener("click", submit);
    input?.addEventListener("keydown", (e) => {
      if (e.key === "Enter") {
        void submit();
      }
    });
  }

  document.addEventListener("DOMContentLoaded", () => {
    wire();
    void refreshStatus();
    void render();
  });
})();
