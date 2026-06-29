// Background load smoke test (TASK-097), run with `node --test`.
//
// Proves background.js loads as a Firefox MV3 *event page*: a context with NO
// importScripts (the Chrome/Edge service-worker global), where the manifest has
// already loaded jdcore.js first so globalThis.JD exists. If the importScripts
// guard regressed, evaluating background.js here throws — exactly the silent
// breakage the task fixes.
"use strict";

const test = require("node:test");
const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");
const vm = require("node:vm");

const SRC = path.join(__dirname, "..", "src");
const read = (f) => fs.readFileSync(path.join(SRC, f), "utf8");

/** A minimal WebExtension API stub covering only what background.js touches at load. */
function makeApi() {
  const listener = () => ({ addListener() {} });
  return {
    webRequest: { onBeforeRequest: listener() },
    tabs: { onUpdated: listener(), onRemoved: listener() },
    runtime: { onInstalled: listener(), onMessage: listener() },
    contextMenus: {
      onClicked: listener(),
      removeAll(cb) { if (cb) cb(); },
      create() {},
    },
    storage: { sync: { get: async () => ({}) } },
  };
}

test("background.js loads as a Firefox event page (no importScripts, JD preloaded)", () => {
  // Firefox event page: importScripts is intentionally absent from the global.
  const sandbox = { browser: makeApi(), console };
  sandbox.globalThis = sandbox;
  const context = vm.createContext(sandbox);

  // The manifest lists jdcore.js before background.js, so load it first — it defines globalThis.JD.
  vm.runInContext(read("jdcore.js"), context, { filename: "jdcore.js" });
  assert.equal(typeof sandbox.JD, "object", "jdcore.js must define globalThis.JD");

  // Loading background.js must not throw (an unguarded importScripts would).
  assert.doesNotThrow(
    () => vm.runInContext(read("background.js"), context, { filename: "background.js" }),
    "background.js must load without importScripts present");
});

test("background.js still loads as a service worker (importScripts present)", () => {
  let imported = null;
  const sandbox = { browser: makeApi(), console };
  sandbox.globalThis = sandbox;
  const context = vm.createContext(sandbox);

  // A service worker has no jdcore preloaded; importScripts pulls it in.
  sandbox.importScripts = (file) => {
    imported = file;
    vm.runInContext(read(file), context, { filename: file });
  };

  assert.doesNotThrow(
    () => vm.runInContext(read("background.js"), context, { filename: "background.js" }),
    "background.js must load with importScripts present");
  assert.equal(imported, "jdcore.js", "the service-worker path imports jdcore.js");
  assert.equal(typeof sandbox.JD, "object", "importScripts must have defined globalThis.JD");
});
