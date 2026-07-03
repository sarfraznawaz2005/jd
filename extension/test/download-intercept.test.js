// Automatic download takeover tests (TASK-183), run with `node --test`.
//
// chrome.downloads.onCreated fires for every real browser download; background.js cancels it and forwards
// the URL to the desktop app instead (IDM/FDM-style), unless the user opted out, the source page is
// blacklisted, or the URL is a blob:/data: address the app could never fetch anyway.
"use strict";

const test = require("node:test");
const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");
const vm = require("node:vm");

const SRC = path.join(__dirname, "..", "src");
const read = (f) => fs.readFileSync(path.join(SRC, f), "utf8");

/**
 * Loads jdcore.js + background.js into a fresh vm context with a stub API sufficient to drive
 * chrome.downloads.onCreated end to end, and returns handles to inspect/drive it.
 * @param {{ interceptDownloads?: boolean, blacklist?: string[] }} [storageSeed]
 */
function makeSandbox(storageSeed = {}) {
  let onCreatedCallback = null;
  const canceled = [];
  const erased = [];
  const sentNativeMessages = [];
  const storage = { ...storageSeed };

  const api = {
    webRequest: { onBeforeRequest: { addListener() {} } },
    tabs: { onUpdated: { addListener() {} }, onRemoved: { addListener() {} } },
    runtime: {
      onInstalled: { addListener() {} },
      onStartup: { addListener() {} },
      onMessage: { addListener() {} },
      sendNativeMessage: (host, message, cb) => {
        sentNativeMessages.push(message);
        cb?.({ type: "ok" });
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
      erase: async (query) => {
        erased.push(query);
      },
    },
  };

  const sandbox = { browser: api, console, URL, navigator: { userAgent: "test-agent" } };
  sandbox.globalThis = sandbox;
  const context = vm.createContext(sandbox);
  vm.runInContext(read("jdcore.js"), context, { filename: "jdcore.js" });
  vm.runInContext(read("background.js"), context, { filename: "background.js" });

  return {
    fireDownloadCreated: async (item) => {
      await onCreatedCallback(item);
      await new Promise((resolve) => setTimeout(resolve, 0)); // flush handleBrowserDownload's internal awaits
    },
    canceled,
    erased,
    sentNativeMessages,
  };
}

test("a real download is canceled and forwarded to the app (default on)", async () => {
  const { fireDownloadCreated, canceled, erased, sentNativeMessages } = makeSandbox();

  await fireDownloadCreated({ id: 7, url: "https://cdn.example.com/movie.mkv", referrer: "https://example.com/page" });

  assert.deepEqual(canceled, [7]);
  // erased[0] is an object literal created inside the vm sandbox's own realm, so it fails a cross-realm
  // deepEqual against one created here — compare the field, not object identity/shape.
  assert.equal(erased.length, 1);
  assert.equal(erased[0].id, 7);
  assert.equal(sentNativeMessages.length, 1);
  assert.equal(sentNativeMessages[0].url, "https://cdn.example.com/movie.mkv");
});

test("interceptDownloads: false disables takeover entirely", async () => {
  const { fireDownloadCreated, canceled, sentNativeMessages } = makeSandbox({ interceptDownloads: false });

  await fireDownloadCreated({ id: 1, url: "https://cdn.example.com/file.zip", referrer: "https://example.com/" });

  assert.deepEqual(canceled, [], "the browser's own download is left alone when the user opted out");
  assert.equal(sentNativeMessages.length, 0);
});

test("blob: and data: URLs are never intercepted — the app could never fetch them", async () => {
  const { fireDownloadCreated, canceled, sentNativeMessages } = makeSandbox();

  await fireDownloadCreated({ id: 2, url: "blob:https://example.com/9c9b-...", referrer: "https://example.com/" });
  await fireDownloadCreated({ id: 3, url: "data:text/plain;base64,aGk=", referrer: "https://example.com/" });

  assert.deepEqual(canceled, []);
  assert.equal(sentNativeMessages.length, 0);
});

test("a download from a blacklisted site is left to the browser", async () => {
  const { fireDownloadCreated, canceled, sentNativeMessages } = makeSandbox({ blacklist: ["example.com"] });

  await fireDownloadCreated({ id: 4, url: "https://cdn.example.com/f.zip", referrer: "https://example.com/page" });

  assert.deepEqual(canceled, []);
  assert.equal(sentNativeMessages.length, 0);
});

test("a cancel/erase failure (already finished) still forwards the URL", async () => {
  let onCreatedCallback;
  const sentNativeMessages = [];
  const api = {
    webRequest: { onBeforeRequest: { addListener() {} } },
    tabs: { onUpdated: { addListener() {} }, onRemoved: { addListener() {} } },
    runtime: {
      onInstalled: { addListener() {} },
      onStartup: { addListener() {} },
      onMessage: { addListener() {} },
      sendNativeMessage: (host, message, cb) => {
        sentNativeMessages.push(message);
        cb?.({ type: "ok" });
      },
    },
    contextMenus: { onClicked: { addListener() {} }, removeAll(cb) { cb?.(); }, create() {} },
    storage: { sync: { get: async () => ({}), set: async () => {} } },
    cookies: { getAll: async () => [] },
    downloads: {
      onCreated: { addListener(cb) { onCreatedCallback = cb; } },
      cancel: async () => { throw new Error("already finished"); },
      erase: async () => { throw new Error("already removed"); },
    },
  };
  const sandbox = { browser: api, console, URL, navigator: { userAgent: "test-agent" } };
  sandbox.globalThis = sandbox;
  const context = vm.createContext(sandbox);
  vm.runInContext(read("jdcore.js"), context, { filename: "jdcore.js" });
  vm.runInContext(read("background.js"), context, { filename: "background.js" });

  await onCreatedCallback({ id: 9, url: "https://cdn.example.com/late.zip", referrer: "https://example.com/" });
  await new Promise((resolve) => setTimeout(resolve, 0));

  assert.equal(sentNativeMessages.length, 1, "forwards anyway rather than losing the download entirely");
});
