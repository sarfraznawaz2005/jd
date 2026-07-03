// Popup logic tests (TASK-187), run with `node --test`.
//
// The detected-media list this file used to test was removed (TASK-187): the icon overlay and automatic
// download takeover cover detection/download end to end, so a redundant raw-URL list in the popup was
// just noise. This now covers what's actually left: connection status, the per-site and global toggles,
// and "send this page link".
"use strict";

const test = require("node:test");
const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");
const vm = require("node:vm");

const SRC = path.join(__dirname, "..", "src");
const read = (f) => fs.readFileSync(path.join(SRC, f), "utf8");

/** A minimal stub element supporting exactly what popup.js touches. */
function makeElement(tag = "div") {
  const el = {
    tagName: tag.toUpperCase(),
    children: [],
    listeners: {},
    className: "",
    hidden: false,
    textContent: "",
    innerHTML: "",
    style: {},
    classList: {
      classes: new Set(),
      add(c) { this.classes.add(c); },
      remove(c) { this.classes.delete(c); },
      toggle(c, on) { on ? this.classes.add(c) : this.classes.delete(c); },
      contains(c) { return this.classes.has(c); },
    },
    attributes: {},
    setAttribute(name, value) { el.attributes[name] = value; },
    addEventListener(type, handler) {
      el.listeners[type] = handler;
    },
    appendChild(child) {
      el.children.push(child);
      return child;
    },
  };
  return el;
}

/**
 * @param {{ activeTabUrl?: string, pingOk?: boolean, blacklist?: string[], interceptDownloads?: boolean }} [options]
 */
function makeSandbox(options = {}) {
  const { activeTabUrl = "https://example.com/page", pingOk = true, blacklist = [], interceptDownloads } = options;
  const elementsById = new Map();
  const ids = ["status", "status-dot", "status-text", "site-toggle", "intercept-toggle", "send-link", "open-settings"];
  for (const id of ids) {
    elementsById.set(id, makeElement());
  }

  const documentStub = {
    getElementById: (id) => elementsById.get(id) ?? null,
    createElement: (tag) => makeElement(tag),
    addEventListener(type, handler) {
      if (type === "DOMContentLoaded") {
        documentStub.__ready = handler;
      }
    },
  };

  const storage = { blacklist, ...(interceptDownloads === undefined ? {} : { interceptDownloads }) };
  const sentMessages = [];
  const api = {
    tabs: { query: async () => [{ id: 1, url: activeTabUrl }] },
    storage: {
      sync: {
        get: async () => ({ ...storage }),
        set: async (values) => Object.assign(storage, values),
      },
    },
    runtime: {
      sendMessage: (msg) => {
        sentMessages.push(msg);
        if (msg.type === "PING") {
          return Promise.resolve({ ok: pingOk });
        }
        return Promise.resolve({ ok: true });
      },
      openOptionsPage: () => {
        sentMessages.push({ type: "__OPEN_OPTIONS__" });
      },
    },
  };

  const sandbox = { console, URL, document: documentStub, browser: api };
  sandbox.globalThis = sandbox;
  return { sandbox, elementsById, sentMessages, storage };
}

/** Loads jdcore.js then popup.js, fires DOMContentLoaded, and flushes pending async work. */
async function runPopup(options) {
  const { sandbox, elementsById, sentMessages, storage } = makeSandbox(options);
  const context = vm.createContext(sandbox);
  vm.runInContext(read("jdcore.js"), context, { filename: "jdcore.js" });
  vm.runInContext(read("popup.js"), context, { filename: "popup.js" });
  await sandbox.document.__ready();
  await new Promise((resolve) => setTimeout(resolve, 0));
  return { elementsById, sentMessages, storage };
}

test("app connected: the status pill shows connected", async () => {
  const { elementsById } = await runPopup({ pingOk: true });

  assert.equal(elementsById.get("status").classList.contains("connected"), true);
  assert.equal(elementsById.get("status-text").textContent, "App connected");
});

test("app not running: the status pill shows not running", async () => {
  const { elementsById } = await runPopup({ pingOk: false });

  assert.equal(elementsById.get("status").classList.contains("connected"), false);
  assert.equal(elementsById.get("status-text").textContent, "App not running");
});

test("site-toggle starts on when the current site is not blacklisted", async () => {
  const { elementsById } = await runPopup({ activeTabUrl: "https://example.com/", blacklist: [] });

  assert.equal(elementsById.get("site-toggle").classList.contains("on"), true);
});

test("site-toggle starts off when the current site is blacklisted", async () => {
  const { elementsById } = await runPopup({ activeTabUrl: "https://example.com/", blacklist: ["example.com"] });

  assert.equal(elementsById.get("site-toggle").classList.contains("on"), false);
});

test("clicking site-toggle off blacklists the current site and syncs it to the app", async () => {
  const { elementsById, sentMessages, storage } = await runPopup({ activeTabUrl: "https://example.com/page" });

  elementsById.get("site-toggle").listeners.click({ currentTarget: elementsById.get("site-toggle") });
  await new Promise((resolve) => setTimeout(resolve, 0));

  assert.deepEqual(storage.blacklist, ["example.com"]);
  assert.ok(sentMessages.some((m) => m.type === "SYNC_BLACKLIST"), "pushed to the desktop app (TASK-069 AC1)");
});

test("intercept-toggle defaults on and persists a toggle-off", async () => {
  const { elementsById, storage } = await runPopup({});

  assert.equal(elementsById.get("intercept-toggle").classList.contains("on"), true, "default on (TASK-183)");

  elementsById.get("intercept-toggle").listeners.click({ currentTarget: elementsById.get("intercept-toggle") });
  await new Promise((resolve) => setTimeout(resolve, 0));

  assert.equal(storage.interceptDownloads, false);
});

test("send-link forwards the active tab's own URL", async () => {
  const { elementsById, sentMessages } = await runPopup({ activeTabUrl: "https://example.com/file.zip" });

  elementsById.get("send-link").listeners.click();
  await new Promise((resolve) => setTimeout(resolve, 0));

  assert.ok(sentMessages.some((m) => m.type === "DOWNLOAD_LINK" && m.url === "https://example.com/file.zip"));
});

test("open-settings opens the options page", async () => {
  const { elementsById, sentMessages } = await runPopup({});

  elementsById.get("open-settings").listeners.click();

  assert.ok(sentMessages.some((m) => m.type === "__OPEN_OPTIONS__"));
});
