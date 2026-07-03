// VideoCaptureEnabled gating tests (TASK-185), run with `node --test`.
//
// Before this, AppSettings.VideoCaptureEnabled was never even exposed to the extension (GET_SETTINGS
// didn't include it), so turning it off in the app had zero effect on the extension's own detection —
// the network sniffer (background.js) kept adding media, and the icon overlay (content.js) kept scanning
// and attaching icons regardless.
"use strict";

const test = require("node:test");
const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");
const vm = require("node:vm");

const SRC = path.join(__dirname, "..", "src");
const read = (f) => fs.readFileSync(path.join(SRC, f), "utf8");

test("background.js: the network sniffer skips media entirely when video capture is off", async () => {
  let onInstalledCallback;
  let onBeforeRequestCallback;
  let onMessageCallback;
  const sentNativeMessages = [];

  const api = {
    webRequest: { onBeforeRequest: { addListener(cb) { onBeforeRequestCallback = cb; } } },
    tabs: { onUpdated: { addListener() {} }, onRemoved: { addListener() {} }, get: async () => ({ url: "https://example.com/" }) },
    action: { setBadgeText: async () => {} },
    runtime: {
      onInstalled: { addListener(cb) { onInstalledCallback = cb; } },
      onStartup: { addListener() {} },
      onMessage: { addListener(cb) { onMessageCallback = cb; } },
      sendNativeMessage: (host, message, cb) => {
        sentNativeMessages.push(message);
        if (message.type === "get_settings") {
          cb?.({ type: "settings", videoCaptureEnabled: false });
          return;
        }
        cb?.({ type: "ok" });
      },
    },
    contextMenus: { onClicked: { addListener() {} }, removeAll(cb) { cb?.(); }, create() {} },
    storage: { sync: { get: async () => ({}) } },
    downloads: { onCreated: { addListener() {} } },
  };

  const sandbox = { browser: api, console, URL, navigator: { userAgent: "test-agent" } };
  sandbox.globalThis = sandbox;
  const context = vm.createContext(sandbox);
  vm.runInContext(read("jdcore.js"), context, { filename: "jdcore.js" });
  vm.runInContext(read("background.js"), context, { filename: "background.js" });

  // onInstalled fires the videoCaptureEnabled cache refresh (piggybacking on the same ping-time hook).
  onInstalledCallback();
  await new Promise((resolve) => setTimeout(resolve, 0));
  assert.ok(sentNativeMessages.some((m) => m.type === "get_settings"), "the cache refresh actually ran");

  onBeforeRequestCallback({ tabId: 1, url: "https://cdn.example.com/clip.mp4" });
  await new Promise((resolve) => setTimeout(resolve, 0));

  // Probe the real mediaStore through the module's own GET_TAB_MEDIA handler — the same seam the popup uses.
  // (media is an array created inside the vm sandbox's own realm, so compare .length, not deepEqual against
  // an outer-realm [] literal — the two have different prototypes despite being structurally identical.)
  const media = await new Promise((resolve) => {
    onMessageCallback({ type: "GET_TAB_MEDIA", tabId: 1 }, {}, (response) => resolve(response.media));
  });
  assert.equal(media.length, 0, "the sniffer must not have added anything while video capture is off");
});

test("background.js: the network sniffer does add media once video capture is back on", async () => {
  // Companion/control case: proves the gate in the previous test is real (would catch a gate that's
  // accidentally inverted or always-on/always-off) by flipping the same flag and expecting the opposite.
  let onInstalledCallback;
  let onBeforeRequestCallback;
  let onMessageCallback;

  const api = {
    webRequest: { onBeforeRequest: { addListener(cb) { onBeforeRequestCallback = cb; } } },
    tabs: { onUpdated: { addListener() {} }, onRemoved: { addListener() {} }, get: async () => ({ url: "https://example.com/" }) },
    action: { setBadgeText: async () => {} },
    runtime: {
      onInstalled: { addListener(cb) { onInstalledCallback = cb; } },
      onStartup: { addListener() {} },
      onMessage: { addListener(cb) { onMessageCallback = cb; } },
      sendNativeMessage: (host, message, cb) => {
        if (message.type === "get_settings") {
          cb?.({ type: "settings", videoCaptureEnabled: true });
          return;
        }
        cb?.({ type: "ok" });
      },
    },
    contextMenus: { onClicked: { addListener() {} }, removeAll(cb) { cb?.(); }, create() {} },
    storage: { sync: { get: async () => ({}) } },
    downloads: { onCreated: { addListener() {} } },
  };

  const sandbox = { browser: api, console, URL, navigator: { userAgent: "test-agent" } };
  sandbox.globalThis = sandbox;
  const context = vm.createContext(sandbox);
  vm.runInContext(read("jdcore.js"), context, { filename: "jdcore.js" });
  vm.runInContext(read("background.js"), context, { filename: "background.js" });

  onInstalledCallback();
  await new Promise((resolve) => setTimeout(resolve, 0));

  onBeforeRequestCallback({ tabId: 1, url: "https://cdn.example.com/clip.mp4" });
  await new Promise((resolve) => setTimeout(resolve, 0));

  const media = await new Promise((resolve) => {
    onMessageCallback({ type: "GET_TAB_MEDIA", tabId: 1 }, {}, (response) => resolve(response.media));
  });
  assert.equal(media.length, 1, "video capture is on, so the sniffer adds the detected media as normal");
});

test("content.js: the icon overlay never scans when video capture is off, even for a directly-resolvable video", async () => {
  const appended = [];
  const documentStub = {
    baseURI: "https://example.com/page",
    documentElement: {},
    body: { appendChild(el) { appended.push(el); }, contains: () => true },
    querySelectorAll: (selector) =>
      selector === "video"
        ? [{
            tagName: "VIDEO",
            getAttribute: (name) => (name === "src" ? "/clip.mp4" : null),
            querySelector: () => null,
            getBoundingClientRect: () => ({ top: 10, left: 10, right: 110, bottom: 60, width: 100, height: 50 }),
          }]
        : [],
    createElement: () => ({
      style: {}, setAttribute() {}, addEventListener() {}, remove() {},
      set innerHTML(_v) {}, set type(_v) {}, set className(_v) {}, set title(_v) {},
    }),
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
  const api = {
    runtime: {
      sendMessage: (msg) => {
        if (msg.type === "GET_SETTINGS") {
          return Promise.resolve({ ok: true, settings: { videoCaptureEnabled: false } });
        }
        return Promise.resolve();
      },
    },
    storage: { sync: { get: async () => ({}) } },
  };

  const sandbox = {
    console, URL, document: documentStub, window: windowStub,
    location: { href: "https://example.com/page" }, browser: api,
    MutationObserver: class { observe() {} },
  };
  sandbox.globalThis = sandbox;
  const context = vm.createContext(sandbox);
  vm.runInContext(read("jdcore.js"), context, { filename: "jdcore.js" });
  vm.runInContext(read("content.js"), context, { filename: "content.js" });
  await new Promise((resolve) => setTimeout(resolve, 0));
  await new Promise((resolve) => setTimeout(resolve, 0));

  assert.equal(appended.length, 0, "no icon attached — video capture is off, even though the video's src was directly resolvable");
});
