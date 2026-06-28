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

  // Media file extensions worth offering a download for (TASK-068 AC0).
  const MEDIA_KINDS = [
    { kind: "hls", ext: [".m3u8", ".m3u"] },
    { kind: "dash", ext: [".mpd"] },
    { kind: "video", ext: [".mp4", ".m4v", ".webm", ".mov", ".mkv", ".ts", ".flv", ".avi"] },
    { kind: "audio", ext: [".mp3", ".m4a", ".aac", ".ogg", ".opus", ".flac", ".wav"] },
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

  /** Classifies a URL as media by extension; returns the kind or null. */
  function classifyMedia(url) {
    let pathname;
    try {
      pathname = new URL(url).pathname.toLowerCase();
    } catch {
      return null;
    }
    for (const { kind, ext } of MEDIA_KINDS) {
      if (ext.some((e) => pathname.endsWith(e))) {
        return kind;
      }
    }
    return null;
  }

  /** Whether a URL points at downloadable media. */
  function isMediaUrl(url) {
    return classifyMedia(url) !== null;
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
    };
  }

  /**
   * Whether the in-page floating download button should be shown (TASK-068 AC1):
   * only when media was detected on the page and the site is not blacklisted (TASK-069 AC0).
   */
  function shouldShowFloatingButton(mediaCount, url, blacklist) {
    return mediaCount > 0 && !isBlacklisted(url, blacklist);
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
    pickContextUrl,
    buildDownloadMessage,
    formatCookieHeader,
    shouldShowFloatingButton,
    createMediaStore,
    MEDIA_KINDS,
  };

  root.JD = JD;
  if (typeof module !== "undefined" && module.exports) {
    module.exports = JD;
  }
})(typeof globalThis !== "undefined" ? globalThis : this);
