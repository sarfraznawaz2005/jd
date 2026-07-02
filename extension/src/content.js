// JustDownload — content script (MV3)
//
// Renders a small, per-video download icon on/near each detected <video> element on the page (TASK-164),
// IDM-style — replacing the earlier single generic floating button (TASK-068). Only <video> elements get
// an icon (TASK-166): a page embedding an <audio> player (e.g. a podcast) should not get one, since audio
// files are already reachable via the network-sniffing-based popup media list (background.js/jdcore.js).
// Runs in every frame (manifest.base.json content_scripts `all_frames: true`) so videos embedded via a third-party
// iframe (e.g. a blog embedding a YouTube player) get their own icon too: each frame's content-script
// instance independently detects and messages its own videos — cross-origin iframes are opaque to page JS,
// but the browser still injects a content script into them, so this needs no cross-frame DOM access.
// jdcore.js is injected before this script (manifest content_scripts) and supplies the pure URL/geometry
// helpers plus the per-site blacklist (TASK-069), which this script also honors.
(() => {
  "use strict";

  const api = globalThis.browser ?? globalThis.chrome;

  if (window.__justDownloadInjected) {
    return;
  }
  window.__justDownloadInjected = true;

  const ICON_CLASS = "jd-video-icon";
  const ICON_SIZE = 28;
  const RESCAN_DEBOUNCE_MS = 150;
  const REPOSITION_INTERVAL_MS = 800;

  const DOWNLOAD_SVG =
    '<svg width="14" height="14" viewBox="0 0 24 24" fill="none" aria-hidden="true">' +
    '<path d="M12 3v10m0 0l-4-4m4 4l4-4M5 19h14" stroke="#fff" stroke-width="2.2" ' +
    'stroke-linecap="round" stroke-linejoin="round"/></svg>';

  /** media element -> { icon: HTMLElement, url: string } */
  const tracked = new Map();
  let blacklisted = false;
  let rescanTimer = null;
  let repositionTimer = null;

  /** Resolves the element's own downloadable URL (its `src`, or its first `<source src>`). */
  function resolveElementUrl(el) {
    const source = el.querySelector("source[src]");
    return JD.resolveMediaUrl(
      el.getAttribute("src"),
      source ? source.getAttribute("src") : null,
      document.baseURI,
    );
  }

  function createIcon(url, kind) {
    const icon = document.createElement("button");
    icon.type = "button";
    icon.className = ICON_CLASS;
    icon.setAttribute("aria-label", "Download this video");
    icon.title = "Download with JustDownload";
    icon.style.all = "initial";
    Object.assign(icon.style, {
      position: "fixed",
      zIndex: "2147483647",
      width: `${ICON_SIZE}px`,
      height: `${ICON_SIZE}px`,
      borderRadius: "6px",
      border: "none",
      background: "#3b82f6",
      color: "#fff",
      display: "none",
      alignItems: "center",
      justifyContent: "center",
      cursor: "pointer",
      boxShadow: "0 2px 8px rgba(0,0,0,0.35)",
      padding: "0",
    });
    icon.innerHTML = DOWNLOAD_SVG;
    icon.addEventListener("click", (event) => {
      event.preventDefault();
      event.stopPropagation();
      api.runtime
        .sendMessage({ type: "DOWNLOAD_LINK", url, pageUrl: location.href, mediaKind: kind })
        .catch(() => {});
    });
    document.body.appendChild(icon);
    return icon;
  }

  /** Positions (or hides) one video's icon over its current viewport rect. */
  function positionIcon(mediaEl, icon) {
    const rect = mediaEl.getBoundingClientRect();
    const pos = JD.computeIconPosition(
      rect,
      { width: window.innerWidth, height: window.innerHeight },
      ICON_SIZE,
    );
    icon.style.display = pos.visible ? "flex" : "none";
    if (pos.visible) {
      icon.style.top = `${pos.top}px`;
      icon.style.left = `${pos.left}px`;
    }
  }

  /** Attaches an icon to a newly-seen media element with a resolvable URL (idempotent). */
  function attachIconTo(el, kind) {
    if (tracked.has(el)) {
      return;
    }
    const url = resolveElementUrl(el);
    if (!url) {
      return;
    }
    const icon = createIcon(url, kind);
    tracked.set(el, { icon, url });
    positionIcon(el, icon);
    api.runtime.sendMessage({ type: "MEDIA_DETECTED", url }).catch(() => {});
    ensureRepositionTimer();
  }

  function scanAndAttach() {
    if (blacklisted) {
      return;
    }
    for (const el of document.querySelectorAll("video")) {
      attachIconTo(el, "video");
    }
  }

  function scheduleRescan() {
    if (blacklisted || rescanTimer !== null) {
      return;
    }
    rescanTimer = window.setTimeout(() => {
      rescanTimer = null;
      scanAndAttach();
    }, RESCAN_DEBOUNCE_MS);
  }

  /** Repositions every tracked icon and drops ones whose video left the DOM. */
  function repositionAll() {
    for (const [el, entry] of tracked) {
      if (!document.body.contains(el)) {
        entry.icon.remove();
        tracked.delete(el);
        continue;
      }
      positionIcon(el, entry.icon);
    }
    if (tracked.size === 0 && repositionTimer !== null) {
      window.clearInterval(repositionTimer);
      repositionTimer = null;
    }
  }

  function ensureRepositionTimer() {
    if (repositionTimer === null) {
      repositionTimer = window.setInterval(repositionAll, REPOSITION_INTERVAL_MS);
    }
  }

  /** Whether this frame's own page is blacklisted (TASK-069), read once at startup. */
  async function isThisFrameBlacklisted() {
    try {
      const stored = await api.storage.sync.get("blacklist");
      const blacklist = Array.isArray(stored?.blacklist) ? stored.blacklist : [];
      return JD.isBlacklisted(location.href, blacklist);
    } catch {
      return false;
    }
  }

  async function init() {
    blacklisted = await isThisFrameBlacklisted();
    if (blacklisted) {
      return;
    }

    scanAndAttach();

    new MutationObserver(scheduleRescan).observe(document.documentElement, {
      childList: true,
      subtree: true,
      attributes: true,
      attributeFilter: ["src"],
    });

    window.addEventListener("scroll", repositionAll, { passive: true, capture: true });
    window.addEventListener("resize", repositionAll, { passive: true });
  }

  void init();
})();
