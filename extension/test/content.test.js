// Content-script icon overlay tests (TASK-166), run with `node --test`.
//
// content.js is a browser-only IIFE with no exports, so — following the same vm-sandbox pattern as
// background.test.js — it is evaluated inside a minimal DOM/WebExtension stub built just for the
// per-element icon overlay behavior: element/document/window enough to drive
// scanAndAttach()/attachIconTo() without a real browser.
"use strict";

const test = require("node:test");
const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");
const vm = require("node:vm");

const SRC = path.join(__dirname, "..", "src");
const read = (f) => fs.readFileSync(path.join(SRC, f), "utf8");

/** A stub <video>/<audio> element with just enough surface for resolveElementUrl/attachIconTo. */
function makeMediaElement(tag, src) {
  return {
    tagName: tag.toUpperCase(),
    getAttribute: (name) => (name === "src" ? src : null),
    querySelector: () => null,
    getBoundingClientRect: () => ({ top: 10, left: 10, right: 110, bottom: 60, width: 100, height: 50 }),
  };
}

/** A stub icon element (what document.createElement("button") returns). */
function makeIconElement() {
  return {
    style: {},
    /** Captured click handlers, so a test can drive the icon the way a user would. */
    listeners: [],
    setAttribute() {},
    addEventListener(type, handler) {
      this.listeners.push({ type, handler });
    },
    click() {
      for (const l of this.listeners.filter((x) => x.type === "click")) {
        l.handler({ preventDefault() {}, stopPropagation() {} });
      }
    },
    /** Awaits the async work an icon click kicks off (the sniffer round-trip for a fallback URL). */
    async clickAndSettle() {
      this.click();
      await new Promise((resolve) => setTimeout(resolve, 0));
      await new Promise((resolve) => setTimeout(resolve, 0));
    },
    remove() {},
    set innerHTML(_v) {},
    set type(_v) {},
    set className(_v) {},
    set title(_v) {},
  };
}

/**
 * Builds a fresh sandbox: a document containing `elements`, plus a minimal window/api stub.
 * @param {object[]} elements
 * @param {{ tabMedia?: Array<{url: string, kind: string}> }} [options] `tabMedia` is what the background's
 *   sniffer has detected — the stand-in for the blob:-URL fallback (TASK-181). Its GET_TAB_MEDIA reply is
 *   modelled on the real one (TASK-241): the background, not the content script, decides which detection to
 *   use and returns it as `preferred`, so this stub mirrors that rather than making content.js re-pick.
 */
function makeSandbox(elements, options = {}) {
  const appended = [];
  const documentStub = {
    baseURI: "https://example.com/page",
    documentElement: {},
    body: {
      appendChild(el) {
        appended.push(el);
      },
      contains: () => true,
    },
    querySelectorAll(selector) {
      return elements.filter((el) => el.tagName === selector.toUpperCase());
    },
    createElement: () => makeIconElement(),
  };

  const intervalCallbacks = [];
  const windowStub = {
    __justDownloadInjected: false,
    innerWidth: 1024,
    innerHeight: 768,
    setTimeout: (...args) => setTimeout(...args),
    clearTimeout: (...args) => clearTimeout(...args),
    setInterval: (cb) => {
      intervalCallbacks.push(cb);
      return intervalCallbacks.length;
    },
    clearInterval: () => {},
    addEventListener: () => {},
  };

  const sentMessages = [];
  const api = {
    runtime: {
      sendMessage: (msg) => {
        sentMessages.push(msg);
        if (msg.type === "GET_TAB_MEDIA") {
          const media = options.tabMedia ?? [];
          const usable = media.filter((m) => m?.kind !== "audio" && m?.url);
          return Promise.resolve({ ok: true, media, preferred: usable[usable.length - 1] ?? null });
        }
        return Promise.resolve();
      },
    },
    storage: { sync: { get: async () => ({}) } },
  };

  const sandbox = {
    console,
    URL,
    document: documentStub,
    window: windowStub,
    location: { href: options.href ?? "https://example.com/page" },
    browser: api,
    MutationObserver: class {
      observe() {}
    },
  };
  sandbox.globalThis = sandbox;
  return { sandbox, appended, sentMessages, intervalCallbacks };
}

/** Loads jdcore.js then content.js into a fresh vm context and waits for async init() to settle. */
async function runContentScript(elements, options = {}) {
  const { sandbox, appended, sentMessages, intervalCallbacks } = makeSandbox(elements, options);
  const context = vm.createContext(sandbox);
  vm.runInContext(read("jdcore.js"), context, { filename: "jdcore.js" });
  vm.runInContext(read("content.js"), context, { filename: "content.js" });
  // init() is fire-and-forget (`void init()`); flush the microtask/macrotask queue it awaits on.
  await new Promise((resolve) => setTimeout(resolve, 0));
  await new Promise((resolve) => setTimeout(resolve, 0)); // a second tick for the async attachIconTo fallback
  return { appended, sentMessages, intervalCallbacks };
}

test("a page with only <audio> elements gets no icon overlay (TASK-166 AC0/AC1)", async () => {
  const audio = makeMediaElement("audio", "/podcast.mp3");
  const { appended, sentMessages } = await runContentScript([audio]);
  assert.equal(appended.length, 0, "no icon button was appended for the <audio> element");
  assert.equal(
    sentMessages.some((m) => m.type === "MEDIA_DETECTED"),
    false,
    "no MEDIA_DETECTED message was sent for the <audio> element",
  );
});

test("a page with a <video> element still gets its icon overlay (TASK-164 regression guard)", async () => {
  const video = makeMediaElement("video", "/clip.mp4");
  const { appended, sentMessages } = await runContentScript([video]);
  assert.equal(appended.length, 1, "one icon button was appended for the <video> element");
  assert.equal(
    sentMessages.some((m) => m.type === "MEDIA_DETECTED" && m.url === "https://example.com/clip.mp4"),
    true,
    "MEDIA_DETECTED was sent for the video's resolved URL",
  );
});

test("a page with both <video> and <audio> only overlays the <video> (TASK-166 AC0)", async () => {
  const video = makeMediaElement("video", "/clip.mp4");
  const audio = makeMediaElement("audio", "/podcast.mp3");
  const { appended } = await runContentScript([video, audio]);
  assert.equal(appended.length, 1, "exactly one icon — the audio element is skipped entirely");
});

test("a blob: src video falls back to the network sniffer's detected URL (TASK-181)", async () => {
  // The real-world case: YouTube/Facebook/Twitter-style MediaSource playback, where <video src> is
  // page-local and never a real, fetchable address.
  const video = makeMediaElement("video", "blob:https://example.com/9c9b7f1e-...");
  const { appended, sentMessages } = await runContentScript([video], {
    tabMedia: [{ url: "https://cdn.example.com/seg1.ts", kind: "hls" }],
  });

  assert.equal(appended.length, 1, "an icon was attached using the sniffed URL");
  assert.equal(
    sentMessages.some((m) => m.type === "MEDIA_DETECTED" && m.url === "https://cdn.example.com/seg1.ts"),
    true,
  );
});

test("a blob: src video with only audio sniffed gets no icon (TASK-181, no audio-download feature)", async () => {
  const video = makeMediaElement("video", "blob:https://example.com/abc");
  const { appended } = await runContentScript([video], {
    tabMedia: [{ url: "https://cdn.example.com/ui-sound.mp3", kind: "audio" }],
  });

  assert.equal(appended.length, 0, "audio-kind sniffed media is never used as a video icon's target");
});

test("a blob: src video with nothing sniffed yet is retried, not given up on immediately (TASK-181)", async () => {
  const video = makeMediaElement("video", "blob:https://example.com/abc");
  const { appended, intervalCallbacks } = await runContentScript([video], { tabMedia: [] });

  assert.equal(appended.length, 0, "nothing to attach to yet");
  assert.equal(intervalCallbacks.length, 1, "a retry timer was started rather than giving up permanently");
});

// --- Page-URL hand-off on extractor sites (TASK-232) -------------------------------------------------

const WATCH_URL = "https://www.youtube.com/watch?v=kap7lC0lI7s";

test("a blob:-backed video on an extractor site gets an icon targeting the page (TASK-232)", async () => {
  // YouTube's player is MediaSource-backed and now serves everything over SABR, so neither the element's
  // own src nor the network sniffer can ever yield a fetchable URL — before this the page got no icon at all.
  const video = makeMediaElement("video", "blob:https://www.youtube.com/abc-123");
  const { appended, sentMessages } = await runContentScript([video], { href: WATCH_URL });

  assert.equal(appended.length, 1, "the watch page gets exactly one icon");
  assert.equal(
    sentMessages.some((m) => m.type === "MEDIA_DETECTED"),
    false,
    "a page URL is not reported to the sniffer's media store",
  );

  // Was a synchronous click(): an extraction hand-off now asks the sniffer for a fallback stream first
  // (TASK-241), so the DOWNLOAD_LINK message is sent a tick later.
  await appended[0].clickAndSettle();
  const sent = sentMessages.find((m) => m.type === "DOWNLOAD_LINK");
  assert.ok(sent, "clicking the icon hands the page off");
  assert.equal(sent.url, WATCH_URL, "the hand-off carries the page URL, not a guessed stream URL");
  assert.equal(sent.extract, true, "and flags it for the app's extractor pipeline");
  assert.equal(sent.fallbackUrl, null, "nothing was sniffed here, so there is no fallback to offer");
});

test("only one page-level icon appears however many players the page has (TASK-232)", async () => {
  // A watch page spawns extra preview players on hover; they all share the same page URL.
  const players = [
    makeMediaElement("video", "blob:https://www.youtube.com/main"),
    makeMediaElement("video", "blob:https://www.youtube.com/preview-1"),
    makeMediaElement("video", "blob:https://www.youtube.com/preview-2"),
  ];
  const { appended } = await runContentScript(players, { href: WATCH_URL });
  assert.equal(appended.length, 1, "the page URL is the same for all of them — one icon is enough");
});

// Was "a directly-resolvable video still wins over the page hand-off": on an EXTRACTABLE_HOSTS page a
// resolvable, non-blob src used to short-circuit extraction entirely. That was the bug — Facebook does
// sometimes serve a directly-resolvable src, but it's an opaque CDN-hash filename with no quality picker,
// so it must never beat the extractor pipeline. Rewritten to assert the fixed behavior: the page hand-off
// (extract=true) now always wins on these hosts, regardless of what the element's own src resolves to.
test("on an extractable host, the page hand-off wins even over a directly-resolvable video src (TASK-232 fix)", async () => {
  const video = makeMediaElement("video", "https://www.facebook.com/real-clip.mp4");
  const { appended, sentMessages } = await runContentScript([video], {
    href: "https://www.facebook.com/watch/?v=123",
  });

  assert.equal(appended.length, 1, "the watch page gets exactly one icon");
  await appended[0].clickAndSettle();
  const sent = sentMessages.find((m) => m.type === "DOWNLOAD_LINK");
  assert.equal(sent.url, "https://www.facebook.com/watch/?v=123", "extraction hands off the page URL");
  assert.equal(sent.extract, true, "extraction wins over the element's own resolvable src");
});

test("an extractable host with multiple video elements still routes every icon click through extraction (TASK-232 fix)", async () => {
  // A Facebook watch page can have several <video> elements at once (main clip, sidebar/suggested videos,
  // hover-preview autoplay clips), some with a resolvable src. Only the first gets an icon (dedup below),
  // but that icon — whichever element it landed on — must still hand off to extraction.
  const players = [
    makeMediaElement("video", "https://www.facebook.com/main-clip.mp4"),
    makeMediaElement("video", "https://www.facebook.com/sidebar-clip.mp4"),
    makeMediaElement("video", "blob:https://www.facebook.com/hover-preview"),
  ];
  const { appended, sentMessages } = await runContentScript(players, {
    href: "https://www.facebook.com/watch/?v=123",
  });

  assert.equal(appended.length, 1, "no duplicate icons for the extra players");
  await appended[0].clickAndSettle();
  const sent = sentMessages.find((m) => m.type === "DOWNLOAD_LINK");
  assert.equal(sent.extract, true, "the icon that exists always routes through extraction");
  assert.equal(sent.url, "https://www.facebook.com/watch/?v=123");
});

test("an ordinary site with an unresolvable video gets no page hand-off (TASK-232)", async () => {
  const video = makeMediaElement("video", "blob:https://example.com/abc");
  const { appended } = await runContentScript([video], { href: "https://example.com/page" });
  assert.equal(appended.length, 0, "we only hand off pages the app actually has an extractor for");
});

// --- x.com routing + no-regression fallback (TASK-241) ----------------------------------------------

const STATUS_URL = "https://x.com/unicodef1wn/status/2087461469881336049";
const X_MASTER_URL = "https://video.twimg.com/amplify_video/2087461469881336049/pl/9k3T.m3u8";

test("an x.com status page routes through extraction (TASK-241)", async () => {
  const video = makeMediaElement("video", "blob:https://x.com/abc");
  const { appended, sentMessages } = await runContentScript([video], {
    href: STATUS_URL,
    tabMedia: [{ url: X_MASTER_URL, kind: "hls" }],
  });

  assert.equal(appended.length, 1);
  await appended[0].clickAndSettle();
  const sent = sentMessages.find((m) => m.type === "DOWNLOAD_LINK");
  assert.equal(sent.url, STATUS_URL, "the page URL goes to the app's extractor pipeline");
  assert.equal(sent.extract, true);
});

test("an extraction hand-off carries the sniffed stream as a fallback (TASK-241)", async () => {
  // Without this, adding x.com to EXTRACTABLE_HOSTS would be a straight regression for anyone whose app has
  // no yt-dlp: there is no in-house Twitter extractor, so the page is declined by everything and the user
  // loses the working sniffed download they had before. The app offers this URL in the picker instead.
  const video = makeMediaElement("video", "blob:https://x.com/abc");
  const { appended, sentMessages } = await runContentScript([video], {
    href: STATUS_URL,
    tabMedia: [{ url: X_MASTER_URL, kind: "hls" }],
  });

  await appended[0].clickAndSettle();
  const sent = sentMessages.find((m) => m.type === "DOWNLOAD_LINK");
  assert.equal(sent.fallbackUrl, X_MASTER_URL, "the stream the sniffer saw travels with the page hand-off");
});

test("a direct (non-extraction) hand-off carries no fallback URL (TASK-241)", async () => {
  // The URL already *is* the stream — there is nothing for the app to fall back to.
  const video = makeMediaElement("video", "/clip.mp4");
  const { appended, sentMessages } = await runContentScript([video]);

  await appended[0].clickAndSettle();
  const sent = sentMessages.find((m) => m.type === "DOWNLOAD_LINK");
  assert.equal(sent.url, "https://example.com/clip.mp4");
  assert.equal(sent.extract, false);
  assert.equal(sent.fallbackUrl, null);
});

test("the icon on a non-extractable page targets the master the background picked (TASK-241)", async () => {
  const video = makeMediaElement("video", "blob:https://example.com/abc");
  const { appended, sentMessages } = await runContentScript([video], {
    tabMedia: [{ url: "https://cdn.example.com/master.m3u8", kind: "hls" }],
  });

  assert.equal(appended.length, 1);
  await appended[0].clickAndSettle();
  const sent = sentMessages.find((m) => m.type === "DOWNLOAD_LINK");
  assert.equal(sent.url, "https://cdn.example.com/master.m3u8", "content.js uses the background's choice");
});

// --- instagram.com routing + no-regression fallback (TASK-242) --------------------------------------

const REEL_URL = "https://www.instagram.com/reels/Dbnng9EBBfr/";
const IG_CDN_URL =
  "https://instagram.fkhi28-1.fna.fbcdn.net/o1/v/t2/f2/m78/clip.mp4?_nc_cat=110&bytestart=15876&byteend=30194";

test("an instagram.com reel page routes through extraction (TASK-242)", async () => {
  const video = makeMediaElement("video", "blob:https://www.instagram.com/abc");
  const { appended, sentMessages } = await runContentScript([video], {
    href: REEL_URL,
    tabMedia: [{ url: IG_CDN_URL, kind: "video" }],
  });

  assert.equal(appended.length, 1);
  await appended[0].clickAndSettle();
  const sent = sentMessages.find((m) => m.type === "DOWNLOAD_LINK");
  assert.equal(sent.url, REEL_URL, "the page URL goes to the app's extractor pipeline");
  assert.equal(sent.extract, true);
});

test("an instagram.com extraction hand-off carries the sniffed CDN URL as a fallback (TASK-242)", async () => {
  // Without this, adding instagram.com to EXTRACTABLE_HOSTS would be a straight regression for anyone whose
  // app has no yt-dlp: there is no in-house Instagram extractor, so the page is declined by everything and
  // the user loses the sniffed (if fragment-only) download they had before.
  const video = makeMediaElement("video", "blob:https://www.instagram.com/abc");
  const { appended, sentMessages } = await runContentScript([video], {
    href: REEL_URL,
    tabMedia: [{ url: IG_CDN_URL, kind: "video" }],
  });

  await appended[0].clickAndSettle();
  const sent = sentMessages.find((m) => m.type === "DOWNLOAD_LINK");
  assert.equal(sent.fallbackUrl, IG_CDN_URL, "the stream the sniffer saw travels with the page hand-off");
});
