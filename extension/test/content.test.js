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

/** Builds a fresh sandbox: a document containing `elements`, plus a minimal window/api stub. */
function makeSandbox(elements) {
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

  const windowStub = {
    __justDownloadInjected: false,
    innerWidth: 1024,
    innerHeight: 768,
    setTimeout: (...args) => setTimeout(...args),
    clearTimeout: (...args) => clearTimeout(...args),
    setInterval: () => 1,
    clearInterval: () => {},
    addEventListener: () => {},
  };

  const sentMessages = [];
  const api = {
    runtime: {
      sendMessage: (msg) => {
        sentMessages.push(msg);
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
  return { sandbox, appended, sentMessages };
}

/** Loads jdcore.js then content.js into a fresh vm context and waits for async init() to settle. */
async function runContentScript(elements) {
  const { sandbox, appended, sentMessages } = makeSandbox(elements);
  const context = vm.createContext(sandbox);
  vm.runInContext(read("jdcore.js"), context, { filename: "jdcore.js" });
  vm.runInContext(read("content.js"), context, { filename: "content.js" });
  // init() is fire-and-forget (`void init()`); flush the microtask/macrotask queue it awaits on.
  await new Promise((resolve) => setTimeout(resolve, 0));
  return { appended, sentMessages };
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
