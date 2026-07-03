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
    setAttribute() {},
    addEventListener() {},
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
 * @param {{ tabMedia?: Array<{url: string, kind: string}> }} [options] `tabMedia` is what GET_TAB_MEDIA
 *   responds with — the network sniffer's stand-in for the blob:-URL fallback (TASK-181).
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
          return Promise.resolve({ ok: true, media: options.tabMedia ?? [] });
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
    location: { href: "https://example.com/page" },
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
