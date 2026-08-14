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
  /** Whether this frame already shows a page-level (extractor hand-off) icon (TASK-232). */
  let pageIconAttached = false;
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

  function createIcon(url, kind, extract) {
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
      void sendHandoff(url, kind, extract);
    });
    document.body.appendChild(icon);
    return icon;
  }

  /**
   * Hands the icon's target to the desktop app. An extraction hand-off also carries whatever stream the
   * sniffer has seen for this tab as `fallbackUrl` (TASK-241), so a user whose app cannot extract this site
   * — no in-house extractor and yt-dlp not enabled (D3) — is offered that stream in the quality picker
   * rather than a dead end. Resolved at click time, not at attach time: by the time a human clicks, the
   * player has been fetching for seconds, whereas the sniffer usually has nothing the instant the <video>
   * element first appears.
   */
  async function sendHandoff(url, kind, extract) {
    const fallback = extract ? await sniffedVideoUrl() : null;
    try {
      api.runtime
        .sendMessage({
          type: "DOWNLOAD_LINK",
          url,
          pageUrl: location.href,
          mediaKind: kind,
          extract,
          fallbackUrl: fallback?.url ?? null,
        })
        .catch(() => {});
    } catch {
      // background unreachable — nothing to hand off to
    }
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
   * fallback for MSE-backed players whose <video src> is a page-local blob: URL. The background picks which
   * detection to use (TASK-241): the master playlist when it saw one, else the most recent stream, and never
   * audio, since the app has no audio-download feature. */
  async function sniffedVideoUrl() {
    try {
      const res = await api.runtime.sendMessage({ type: "GET_TAB_MEDIA" });
      return res?.preferred?.url ? res.preferred : null;
    } catch {
      return null; // background unreachable — nothing to fall back to
    }
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
    // Home/feed pages (YouTube, X/Twitter, Facebook, Instagram) never get an icon (TASK-243): re-checked
    // on every attach attempt, not cached at init, since these are SPAs — navigating between the feed and
    // a watch/status/post page never reloads the content script.
    if (JD.isSuppressedHomePage(location.href)) {
      pending.delete(el);
      return;
    }
    inFlight.add(el);
    try {
      // On a site the app has an extractor for, the page URL always wins over the element's own src
      // (TASK-232 follow-up): Facebook (and friends) sometimes serves a directly-resolvable, non-blob src
      // that still points at an opaque CDN-hash file with no quality picker — trusting it instead of the
      // extractor pipeline is exactly the bug this branch used to have, so extraction must never lose to
      // "the browser happened to find an exact URL". The page URL is identical for every <video> here, so
      // only the first element gets an icon at all: later ones (hover-preview/sidebar clips) are skipped
      // outright rather than sprouting a duplicate direct-src or sniffer-based icon of their own.
      if (JD.isExtractablePage(location.href)) {
        if (pageIconAttached) {
          return;
        }
        pending.delete(el);
        const icon = createIcon(location.href, "video", true);
        tracked.set(el, { icon, url: location.href, extract: true });
        pageIconAttached = true;
        positionIcon(el, icon);
        ensureRepositionTimer();
        return;
      }

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
      const icon = createIcon(url, kind, false);
      tracked.set(el, { icon, url, extract: false });
      positionIcon(el, icon);
      // Only report real media URLs to the sniffer's store: a page URL is not something GET_TAB_MEDIA
      // should later hand back to another element as a downloadable stream (TASK-232).
      try {
        api.runtime.sendMessage({ type: "MEDIA_DETECTED", url }).catch(() => {});
      } catch {
        // background unreachable — nothing to report to
      }
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
        if (entry.extract) {
          // Its <video> is gone (e.g. the hover preview it landed on), so let the next scan place a fresh
          // page-level icon rather than leaving the page with none (TASK-232).
          pageIconAttached = false;
        }
        continue;
      }
      if (entry.extract && entry.url !== location.href) {
        // These hosts are SPAs: navigating between watch pages swaps the video in place without reloading
        // the frame, so a page-level icon would keep pointing at the previously watched URL (TASK-232).
        // Drop it and let the next scan attach one carrying the current page.
        entry.icon.remove();
        tracked.delete(el);
        pageIconAttached = false;
        scheduleRescan();
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
