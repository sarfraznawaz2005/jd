// Alt+click bypass tests (TASK-265), run with `node --test`.
//
// Holding Alt while clicking hands that one download back to the browser instead of taking it over. It is
// the escape hatch for links the app cannot fetch at all — above all one-shot URLs, where the browser has
// already spent the link's single answer by the time downloads.onCreated fires.
"use strict";

const test = require("node:test");
const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");
const vm = require("node:vm");

const SRC = path.join(__dirname, "..", "src");
const read = (f) => fs.readFileSync(path.join(SRC, f), "utf8");

/**
 * Like download-intercept.test.js's sandbox, but also captures the runtime.onMessage router so a test can
 * deliver ALT_BYPASS / GET_SETTINGS the way a content script and the popup really do.
 * @param {{ appSettings?: object }} [options]
 */
function makeSandbox(options = {}) {
  let onCreatedCallback = null;
  let onMessageCallback = null;
  const canceled = [];
  const sentNativeMessages = [];
  const storage = {};

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
        cb?.(message.type === "get_settings" ? (options.appSettings ?? {}) : { type: "ok" });
      },
      lastError: undefined,
    },
    contextMenus: { onClicked: { addListener() {} }, removeAll(cb) { cb?.(); }, create() {} },
    storage: {
      sync: {
        get: async (key) => (typeof key === "string" ? { [key]: storage[key] } : { ...storage }),
        set: async (values) => Object.assign(storage, values),
      },
    },
    cookies: { getAll: async () => [] },
    downloads: {
      onCreated: {
        addListener(cb) {
          onCreatedCallback = cb;
        },
      },
      cancel: async (id) => {
        canceled.push(id);
      },
      erase: async () => {},
    },
  };

  const sandbox = { browser: api, console, URL, navigator: { userAgent: "test-agent" } };
  sandbox.globalThis = sandbox;
  const context = vm.createContext(sandbox);
  vm.runInContext(read("jdcore.js"), context, { filename: "jdcore.js" });
  vm.runInContext(read("background.js"), context, { filename: "background.js" });

  const send = async (message) => {
    await new Promise((resolve) => onMessageCallback(message, {}, resolve));
  };

  return {
    altClick: () => send({ type: "ALT_BYPASS" }),
    loadAppSettings: () => send({ type: "GET_SETTINGS" }),
    fireDownloadCreated: async (item) => {
      await onCreatedCallback(item);
      await new Promise((resolve) => setTimeout(resolve, 0));
    },
    canceled,
    forwarded: () => sentNativeMessages.filter((m) => m.type === "DOWNLOAD_LINK"),
  };
}

const DOWNLOAD = { id: 1, url: "http://localhost:5380/api/settings/backup?token=abc", referrer: "http://localhost:5380/" };

test("after an Alt+click, the next download is left to the browser", async () => {
  const s = makeSandbox();

  await s.altClick();
  await s.fireDownloadCreated(DOWNLOAD);

  assert.deepEqual(s.canceled, [], "the browser's own download must not be canceled");
  assert.equal(s.forwarded().length, 0, "and nothing is handed to the app");
});

test("without an Alt+click the download is taken over as usual", async () => {
  const s = makeSandbox();

  await s.fireDownloadCreated(DOWNLOAD);

  assert.deepEqual(s.canceled, [1]);
  assert.equal(s.forwarded().length, 1);
});

test("the bypass is one-shot — the download after it is taken over again", async () => {
  // Otherwise one Alt+click would silently disarm takeover for every download in the next few seconds.
  const s = makeSandbox();

  await s.altClick();
  await s.fireDownloadCreated({ ...DOWNLOAD, id: 1 });
  await s.fireDownloadCreated({ ...DOWNLOAD, id: 2 });

  assert.deepEqual(s.canceled, [2], "only the second download is taken over");
  assert.equal(s.forwarded().length, 1);
});

test("the app setting turns the bypass off", async () => {
  const s = makeSandbox({ appSettings: { altClickBypassEnabled: false } });

  await s.loadAppSettings();
  await s.altClick();
  await s.fireDownloadCreated(DOWNLOAD);

  assert.deepEqual(s.canceled, [1], "Alt is ignored when the user switched the bypass off");
  assert.equal(s.forwarded().length, 1);
});

test("the app setting can be turned back on", async () => {
  const s = makeSandbox({ appSettings: { altClickBypassEnabled: true } });

  await s.loadAppSettings();
  await s.altClick();
  await s.fireDownloadCreated(DOWNLOAD);

  assert.deepEqual(s.canceled, []);
});

test("an app build that doesn't know the flag leaves the default in place", async () => {
  // applyAppSettings ignores absent fields, so an older desktop app must not silently disable the bypass.
  const s = makeSandbox({ appSettings: { videoCaptureEnabled: true } });

  await s.loadAppSettings();
  await s.altClick();
  await s.fireDownloadCreated(DOWNLOAD);

  assert.deepEqual(s.canceled, []);
});

test("the content script reports an Alt+click and only an Alt+click", async () => {
  // The listener has to survive a page that stops propagation, so it is capture-phase on mousedown.
  const messages = [];
  const listeners = [];
  const api = {
    runtime: {
      sendMessage: async (message) => {
        messages.push(message);
      },
    },
    storage: { sync: { get: async () => ({}) } },
  };
  const sandbox = {
    browser: api,
    console,
    URL,
    document: {
      documentElement: {},
      addEventListener() {},
      querySelectorAll: () => [],
    },
    window: {
      addEventListener: (type, handler, opts) => listeners.push({ type, handler, opts }),
      location: { href: "https://example.com/", hostname: "example.com" },
    },
    MutationObserver: class {
      observe() {}
    },
    setTimeout,
    setInterval: () => 0,
    clearTimeout,
    clearInterval,
  };
  sandbox.globalThis = sandbox;
  sandbox.window.__justDownloadInjected = undefined;
  Object.assign(sandbox, { self: sandbox });
  const context = vm.createContext(sandbox);
  vm.runInContext(read("jdcore.js"), context, { filename: "jdcore.js" });
  vm.runInContext(read("content.js"), context, { filename: "content.js" });

  const mousedown = listeners.find((l) => l.type === "mousedown");
  assert.ok(mousedown, "a mousedown listener is registered");
  assert.equal(mousedown.opts?.capture, true, "capture phase, so a page handler cannot swallow it");

  mousedown.handler({ altKey: false });
  assert.equal(messages.length, 0, "a plain click is not a bypass");

  mousedown.handler({ altKey: true });
  assert.equal(messages.length, 1);
  assert.equal(messages[0].type, "ALT_BYPASS");
});
