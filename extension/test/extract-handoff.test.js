// Extraction hand-off URL correction tests, run with `node --test`.
//
// content.js runs in every frame (all_frames), so on a site like Facebook the <video> lives in a nested
// frame whose own location.href is just the bare origin — the hand-off it sends carries a useless page
// URL. background.js is the only side that knows the top-level page (sender.tab.url), so it overrides the
// payload's URL for extraction hand-offs. Direct/sniffed media URLs are frame-correct and stay untouched.
"use strict";

const test = require("node:test");
const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");
const vm = require("node:vm");

const SRC = path.join(__dirname, "..", "src");
const read = (f) => fs.readFileSync(path.join(SRC, f), "utf8");

/** Loads jdcore.js + background.js and returns a driver for its runtime.onMessage listener. */
function makeSandbox() {
  let onMessageCallback = null;
  const sentNativeMessages = [];

  const api = {
    webRequest: { onBeforeRequest: { addListener() {} } },
    tabs: { onUpdated: { addListener() {} }, onRemoved: { addListener() {} } },
    runtime: {
      onInstalled: { addListener() {} },
      onStartup: { addListener() {} },
      onMessage: {
        addListener(cb) {
          onMessageCallback = cb;
        },
      },
      sendNativeMessage: (host, message, cb) => {
        sentNativeMessages.push(message);
        cb?.({ type: "ok" });
      },
      lastError: undefined,
    },
    contextMenus: { onClicked: { addListener() {} }, removeAll(cb) { cb?.(); }, create() {} },
    storage: { sync: { get: async () => ({}), set: async () => {} } },
    cookies: { getAll: async () => [] },
    downloads: { onCreated: { addListener() {} } },
  };

  const sandbox = { browser: api, console, URL, navigator: { userAgent: "test-agent" } };
  sandbox.globalThis = sandbox;
  const context = vm.createContext(sandbox);
  vm.runInContext(read("jdcore.js"), context, { filename: "jdcore.js" });
  vm.runInContext(read("background.js"), context, { filename: "background.js" });

  return {
    sendMessage: async (message, sender) => {
      onMessageCallback(message, sender, () => {});
      await new Promise((resolve) => setTimeout(resolve, 0)); // flush sendDownload's internal awaits
    },
    sentNativeMessages,
  };
}

const REEL_URL = "https://www.facebook.com/reel/2044478973099445";

test("an extraction hand-off from a nested frame uses the top-level tab URL", async () => {
  const { sendMessage, sentNativeMessages } = makeSandbox();

  // What a content script inside Facebook's player frame actually sends: the frame's location.href is
  // the bare origin, so both url and pageUrl are useless to the extractor.
  await sendMessage(
    {
      type: "DOWNLOAD_LINK",
      url: "https://www.facebook.com/",
      pageUrl: "https://www.facebook.com/",
      mediaKind: "video",
      extract: true,
    },
    { tab: { id: 3, url: REEL_URL } },
  );

  assert.equal(sentNativeMessages.length, 1);
  assert.equal(sentNativeMessages[0].url, REEL_URL, "the reel URL reaches the app, not the bare origin");
  assert.equal(sentNativeMessages[0].pageUrl, REEL_URL);
  assert.equal(sentNativeMessages[0].referrer, REEL_URL);
  assert.equal(sentNativeMessages[0].extract, true);
});

test("an extraction hand-off with no tab context falls back to the message URL", async () => {
  const { sendMessage, sentNativeMessages } = makeSandbox();

  await sendMessage(
    { type: "DOWNLOAD_LINK", url: REEL_URL, pageUrl: REEL_URL, mediaKind: "video", extract: true },
    {}, // no sender.tab (e.g. a message from an extension page)
  );

  assert.equal(sentNativeMessages.length, 1);
  assert.equal(sentNativeMessages[0].url, REEL_URL);
  assert.equal(sentNativeMessages[0].pageUrl, REEL_URL);
});

test("a non-extraction hand-off keeps the frame's own media URL", async () => {
  const { sendMessage, sentNativeMessages } = makeSandbox();

  const mediaUrl = "https://cdn.example.com/stream/master.m3u8";
  await sendMessage(
    {
      type: "DOWNLOAD_LINK",
      url: mediaUrl,
      pageUrl: "https://example.com/embed",
      mediaKind: "video",
      extract: false,
    },
    { tab: { id: 4, url: "https://example.com/article" } },
  );

  assert.equal(sentNativeMessages.length, 1);
  assert.equal(sentNativeMessages[0].url, mediaUrl, "a sniffed/direct media URL is passed through as-is");
  assert.equal(sentNativeMessages[0].pageUrl, "https://example.com/embed");
  assert.equal(sentNativeMessages[0].extract, false);
});
