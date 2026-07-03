// JustDownload — content script (MV3)
//
// Renders a small, per-video download icon on/near each detected <video> element on the page (TASK-164),
// IDM-style — replacing the earlier single generic floating button (TASK-068). Only <video> elements get
// an icon (TASK-166): a page embedding an <audio> player (e.g. a podcast) should not get one, since the
// app has no audio-download feature at all — <audio> elements and audio-kind media are never surfaced
// anywhere in the extension (TASK-181).
//
// Most real sites (YouTube, Facebook, Twitter/X, ...) stream via MediaSource Extensions: the <video>
// element's own `src` is a page-local `blob:` URL that resolveElementUrl can never turn into a real,
// fetchable address (real-world testing confirmed: without a fallback, this content script found zero
// downloadable videos on any of them). When that happens, attachIconTo instead asks the background
// script's network sniffer (background.js's webRequest listener, already working correctly on these same
// sites — it sees the real segment/manifest requests MSE makes under the hood) what it has already
// detected for this tab, and uses that as the icon's target. The sniffer may not have seen anything yet
// the instant a <video> element first appears, so an unresolved element is retried on a bounded interval
// rather than given up on immediately.
//
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
  const PENDING_RETRY_INTERVAL_MS = 1000;
  const PENDING_MAX_ATTEMPTS = 15; // ~15s: long enough for the network sniffer to see a stream start

  const DOWNLOAD_SVG =
    '<svg width="14" height="14" viewBox="0 0 24 24" fill="none" aria-hidden="true">' +
    '<path d="M12 3v10m0 0l-4-4m4 4l4-4M5 19h14" stroke="#fff" stroke-width="2.2" ' +
    'stroke-linecap="round" stroke-linejoin="round"/></svg>';

  /** media element -> { icon: HTMLElement, url: string } */
  const tracked = new Map();
  /** media element -> attempt count, for elements still waiting on the network sniffer (TASK-181). */
  const pending = new Map();
  /** media elements currently mid-resolution, so a concurrent scan can't double-attach (TASK-181). */
  const inFlight = new Set();
  let blacklisted = false;
  let videoCaptureOff = false;
  let rescanTimer = null;
  let repositionTimer = null;
  let pendingRetryTimer = null;

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

  /** Asks the background sniffer what real media it has already seen for this tab (TASK-181) — the
   * fallback for MSE-backed players whose <video src> is a page-local blob: URL. Never returns audio: the
   * app has no audio-download feature. Prefers the most recently detected item, since for a single-video
   * page that tracks the actively playing stream as new segments keep arriving. */
  async function sniffedVideoUrl() {
    try {
      const res = await api.runtime.sendMessage({ type: "GET_TAB_MEDIA" });
      const media = Array.isArray(res?.media) ? res.media : [];
      for (let i = media.length - 1; i >= 0; i--) {
        if (media[i]?.kind !== "audio" && media[i]?.url) {
          return media[i];
        }
      }
    } catch {
      // background unreachable — nothing to fall back to
    }
    return null;
  }

  /**
   * Attaches an icon to a newly-seen media element with a resolvable URL (idempotent). Falls back to the
   * network sniffer when the element's own src is unusable, retrying on a bounded interval since the
   * sniffer may not have seen a stream yet the instant the element appears.
   */
  async function attachIconTo(el) {
    if (tracked.has(el) || inFlight.has(el)) {
      return;
    }
    inFlight.add(el);
    try {
      let url = resolveElementUrl(el);
      let kind = "video";
      if (!url) {
        const sniffed = await sniffedVideoUrl();
        if (sniffed) {
          url = sniffed.url;
          kind = sniffed.kind;
        }
      }

      if (!url) {
        const attempts = (pending.get(el) ?? 0) + 1;
        if (attempts <= PENDING_MAX_ATTEMPTS && document.body.contains(el)) {
          pending.set(el, attempts);
          ensurePendingRetryTimer();
        } else {
          pending.delete(el); // gave up, or the element already left the DOM
        }
        return;
      }

      pending.delete(el);
      const icon = createIcon(url, kind);
      tracked.set(el, { icon, url });
      positionIcon(el, icon);
      api.runtime.sendMessage({ type: "MEDIA_DETECTED", url }).catch(() => {});
      ensureRepositionTimer();
    } finally {
      inFlight.delete(el);
    }
  }

  function scanAndAttach() {
    if (blacklisted || videoCaptureOff) {
      return;
    }
    for (const el of document.querySelectorAll("video")) {
      void attachIconTo(el);
    }
  }

  /** Retries every element still waiting on the sniffer (TASK-181). */
  function retryPending() {
    for (const el of pending.keys()) {
      void attachIconTo(el);
    }
    if (pending.size === 0 && pendingRetryTimer !== null) {
      window.clearInterval(pendingRetryTimer);
      pendingRetryTimer = null;
    }
  }

  function ensurePendingRetryTimer() {
    if (pendingRetryTimer === null) {
      pendingRetryTimer = window.setInterval(retryPending, PENDING_RETRY_INTERVAL_MS);
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

  /** Whether the app's AppSettings.VideoCaptureEnabled is off (TASK-185), read once at startup — turning
   * it off in Settings must actually stop the icon overlay, not just the app's own yt-dlp fallback. */
  async function isVideoCaptureOff() {
    try {
      const response = await api.runtime.sendMessage({ type: "GET_SETTINGS" });
      return response?.settings?.videoCaptureEnabled === false;
    } catch {
      return false;
    }
  }

  async function init() {
    blacklisted = await isThisFrameBlacklisted();
    videoCaptureOff = await isVideoCaptureOff();
    if (blacklisted || videoCaptureOff) {
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
