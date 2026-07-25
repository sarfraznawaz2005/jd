// JustDownload — shared pure logic (TASK-067/068/069/071)
//
// This module holds the browser-agnostic, side-effect-free logic the background
// worker, content script, and popup all reuse: media-URL classification, the
// per-site blacklist, context-menu URL selection, and message building. Keeping
// it pure makes it unit-testable under Node (test/) and identical across the
// three browser bundles.
//
// It is loaded as a classic script everywhere (importScripts in the worker, a
// content_scripts entry, and a <script> tag in the popup), exposing `globalThis.JD`,
// and also exports under CommonJS so Node's test runner can require it.
(function (root) {
  "use strict";

  // Media file extensions worth offering a download for (TASK-068 AC0). Video/streaming only (TASK-181)
  // — the app has no audio-download feature, and on real sites (YouTube in particular) treating every
  // .mp3 as "media" surfaced UI sound effects (e.g. "success.mp3") rather than anything a user would
  // actually want to download.
  const MEDIA_KINDS = [
    { kind: "hls", ext: [".m3u8", ".m3u"] },
    { kind: "dash", ext: [".mpd"] },
    { kind: "video", ext: [".mp4", ".m4v", ".webm", ".mov", ".mkv", ".ts", ".flv", ".avi"] },
  ];

  /** The lower-cased hostname of a URL, or null if it cannot be parsed. */
  function hostnameOf(url) {
    try {
      return new URL(url).hostname.toLowerCase();
    } catch {
      return null;
    }
  }

  /** Normalizes a blacklist entry (a URL or bare host) to a hostname, or null. */
  function normalizeHost(input) {
    if (typeof input !== "string") {
      return null;
    }
    const trimmed = input.trim().toLowerCase();
    if (trimmed.length === 0) {
      return null;
    }
    // Accept a full URL or a bare host (optionally with a leading "www.").
    const host = trimmed.includes("://") ? hostnameOf(trimmed) : trimmed.split("/")[0];
    return host && host.length > 0 ? host.replace(/^www\./, "") : null;
  }

  /**
   * Whether `url`'s site is blacklisted: an exact host match or a subdomain of a
   * blacklisted host (so blacklisting example.com also covers cdn.example.com).
   */
  function isBlacklisted(url, blacklist) {
    const host = hostnameOf(url);
    if (!host || !Array.isArray(blacklist)) {
      return false;
    }
    const bare = host.replace(/^www\./, "");
    return blacklist.some((entry) => {
      const e = normalizeHost(entry);
      return e !== null && (bare === e || bare.endsWith("." + e));
    });
  }

  /** Adds a normalized host to the blacklist (no duplicates); returns the new list. */
  function addToBlacklist(blacklist, input) {
    const host = normalizeHost(input);
    const list = Array.isArray(blacklist) ? blacklist.slice() : [];
    if (host && !list.includes(host)) {
      list.push(host);
    }
    return list;
  }

  /** Removes a host from the blacklist; returns the new list. */
  function removeFromBlacklist(blacklist, input) {
    const host = normalizeHost(input);
    if (!Array.isArray(blacklist) || host === null) {
      return Array.isArray(blacklist) ? blacklist.slice() : [];
    }
    return blacklist.filter((entry) => normalizeHost(entry) !== host);
  }

  // Extension-less streaming endpoints (TASK-232). Big MSE sites serve media from a path that carries no
  // file extension at all — YouTube's is `/videoplayback?…&mime=video%2Fmp4&itag=248&range=0-…` — so the
  // extension-only rule above classified *every* request on a YouTube watch page as "not media", the
  // network sniffer's store stayed empty, and the content script's blob:-URL fallback (TASK-181) therefore
  // never had a URL to attach an icon to. Matched on path + a `mime=video/...` query param rather than on
  // hostname, so it holds for any CDN using the same shape and never fires on a `mime=audio/...` stream.
  const EXTENSIONLESS_MEDIA_PATHS = ["/videoplayback"];

  /** The `mime` query parameter's top-level type ("video", "audio", ...), or null. */
  function mimeTypeOf(parsed) {
    const mime = parsed.searchParams.get("mime");
    return mime ? mime.split("/")[0].toLowerCase() : null;
  }

  /** Classifies a URL as media by extension, or by a known extension-less streaming path; kind or null. */
  function classifyMedia(url) {
    let parsed;
    try {
      parsed = new URL(url);
    } catch {
      return null;
    }
    const pathname = parsed.pathname.toLowerCase();
    for (const { kind, ext } of MEDIA_KINDS) {
      if (ext.some((e) => pathname.endsWith(e))) {
        return kind;
      }
    }
    if (EXTENSIONLESS_MEDIA_PATHS.includes(pathname) && mimeTypeOf(parsed) === "video") {
      return "video";
    }
    return null;
  }

  /** Whether a URL points at downloadable media. */
  function isMediaUrl(url) {
    return classifyMedia(url) !== null;
  }

  // Per-chunk parameters an MSE player adds to each segment request (TASK-232). The sniffer sees the
  // player's own chunk fetches, so the captured URL addresses one slice ("range=0-1310719"), not the
  // stream — handing that to the engine downloads a fragment. Stripping them restores the whole-stream
  // URL the engine can then Range-request itself.
  const CHUNK_PARAMS = ["range", "rn", "rbuf"];

  // Sites the desktop app has a dedicated extractor for (TASK-232), mirroring Core's YouTubeMediaExtractor
  // and FacebookMediaExtractor host rules. On these the extension deliberately does NOT try to guess a
  // media URL: they stream via MediaSource, and YouTube in particular now serves everything over SABR — a
  // POST to /videoplayback whose format and byte range live in a UMP protobuf body, with no mime, itag or
  // range anywhere in the URL. There is simply no fetchable address for the sniffer to find, so the icon
  // hands the *page* URL to the app and lets the extractor pipeline (and, if the user enabled it, the
  // yt-dlp fallback — D3) resolve the real streams and mux audio. Per D3 this is best-effort: the app
  // reports a clear failure when no extractor can handle the page.
  const EXTRACTABLE_HOSTS = ["youtube.com", "youtu.be", "facebook.com", "fb.watch"];

  /** Whether the desktop app has a site extractor for this page's host, so the page URL is worth handing off. */
  function isExtractablePage(url) {
    const host = hostnameOf(url);
    if (!host) {
      return false;
    }
    const bare = host.replace(/^www\./, "");
    return EXTRACTABLE_HOSTS.some((h) => bare === h || bare.endsWith("." + h));
  }

  /** Strips per-chunk parameters from a sniffed media URL so it addresses the whole stream. */
  function normalizeMediaUrl(url) {
    let parsed;
    try {
      parsed = new URL(url);
    } catch {
      return url;
    }
    for (const param of CHUNK_PARAMS) {
      parsed.searchParams.delete(param);
    }
    return parsed.href;
  }

  /**
   * Picks the URL a context-menu click targets (TASK-067): a link's href wins,
   * then a media element's src, then the page URL.
   */
  function pickContextUrl(info) {
    if (!info || typeof info !== "object") {
      return null;
    }
    return info.linkUrl || info.srcUrl || info.pageUrl || null;
  }

  /**
   * Builds the typed DOWNLOAD message sent to the desktop app (TASK-067), carrying
   * the auth context (referrer, cookies, extra headers) for authenticated downloads.
   */
  function buildDownloadMessage(opts) {
    const o = opts || {};
    return {
      type: "DOWNLOAD_LINK",
      url: o.url,
      pageUrl: o.pageUrl || null,
      referrer: o.referrer || o.pageUrl || null,
      cookies: typeof o.cookies === "string" ? o.cookies : null,
      headers: o.headers && typeof o.headers === "object" ? o.headers : {},
      mediaKind: o.mediaKind || null,
      // Whether `url` is a page to run the extractor pipeline on rather than a direct media URL (TASK-232).
      extract: o.extract === true,
    };
  }

  /**
   * An in-memory store of media detected per browser tab (TASK-068). Deduplicates by URL and bounds the
   * list so a long-lived tab cannot grow without limit. Pure data structure — no browser APIs.
   */
  function createMediaStore(maxPerTab = 50) {
    const byTab = new Map();

    return {
      /** Records a detected media item for a tab; returns true if it was new. */
      add(tabId, item) {
        if (typeof tabId !== "number" || !item || typeof item.url !== "string") {
          return false;
        }
        let list = byTab.get(tabId);
        if (!list) {
          list = [];
          byTab.set(tabId, list);
        }
        if (list.some((m) => m.url === item.url)) {
          return false;
        }
        list.push(item);
        if (list.length > maxPerTab) {
          list.shift();
        }
        return true;
      },
      /** The media detected for a tab (a copy), or an empty array. */
      get(tabId) {
        const list = byTab.get(tabId);
        return list ? list.slice() : [];
      },
      /** The number of media items detected for a tab. */
      count(tabId) {
        const list = byTab.get(tabId);
        return list ? list.length : 0;
      },
      /** Forgets a tab's media (e.g. on navigation or tab close). */
      clear(tabId) {
        byTab.delete(tabId);
      },
    };
  }

  /** A short human label for a detected media item, for the popup list (TASK-071 AC0). */
  function mediaLabel(item) {
    if (!item || typeof item.url !== "string") {
      return "Media";
    }
    let name = "";
    try {
      const path = new URL(item.url).pathname;
      name = path.substring(path.lastIndexOf("/") + 1);
    } catch {
      name = "";
    }
    const kind = item.kind || classifyMedia(item.url) || "media";
    return name ? `${kind} · ${decodeURIComponent(name)}` : kind;
  }

  /** Builds the message that syncs the per-site blacklist to the desktop app (TASK-069 AC1). */
  function buildBlacklistSyncMessage(blacklist) {
    const domains = Array.isArray(blacklist)
      ? blacklist.map(normalizeHost).filter((h) => h !== null)
      : [];
    return { type: "BLACKLIST_SYNC", domains };
  }

  /**
   * Resolves a `<video>`/`<audio>` element's absolute, downloadable URL from its own `src` attribute or a
   * child `<source src>` (TASK-164). Returns null when there is no source, or the source is a `blob:` URL
   * (MediaSource-backed streams — page-local, not fetchable outside the page — TASK-068's DOM scan had the
   * same limitation).
   */
  function resolveMediaUrl(srcAttr, sourceSrcAttr, baseURI) {
    const raw = srcAttr || sourceSrcAttr;
    if (!raw) {
      return null;
    }
    let absolute;
    try {
      absolute = new URL(raw, baseURI).href;
    } catch {
      return null;
    }
    return absolute.startsWith("blob:") ? null : absolute;
  }

  /**
   * Computes where a small per-video download icon (TASK-164) should sit over a media element's current
   * viewport rect — IDM-style, pinned to the element's top-right corner — and whether it should be visible
   * right now (the element has size and is at least partly within the viewport).
   */
  function computeIconPosition(rect, viewport, iconSize = 28, margin = 8) {
    const visible =
      rect.width > 0 &&
      rect.height > 0 &&
      rect.bottom > 0 &&
      rect.right > 0 &&
      rect.top < viewport.height &&
      rect.left < viewport.width;
    if (!visible) {
      return { visible: false, top: 0, left: 0 };
    }
    return {
      visible: true,
      top: Math.max(rect.top + margin, 0),
      left: Math.max(rect.right - iconSize - margin, rect.left),
    };
  }

  /** Serializes an array of {name,value} cookies into a Cookie header value. */
  function formatCookieHeader(cookies) {
    if (!Array.isArray(cookies)) {
      return "";
    }
    return cookies
      .filter((c) => c && typeof c.name === "string" && typeof c.value === "string")
      .map((c) => `${c.name}=${c.value}`)
      .join("; ");
  }

  const JD = {
    hostnameOf,
    normalizeHost,
    isBlacklisted,
    addToBlacklist,
    removeFromBlacklist,
    classifyMedia,
    isMediaUrl,
    normalizeMediaUrl,
    isExtractablePage,
    pickContextUrl,
    buildDownloadMessage,
    formatCookieHeader,
    createMediaStore,
    buildBlacklistSyncMessage,
    mediaLabel,
    resolveMediaUrl,
    computeIconPosition,
    MEDIA_KINDS,
  };

  root.JD = JD;
  if (typeof module !== "undefined" && module.exports) {
    module.exports = JD;
  }
})(typeof globalThis !== "undefined" ? globalThis : this);
