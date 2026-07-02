// Manifest contract tests (TASK-095/096/098), run with `node --test`.
"use strict";

const test = require("node:test");
const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");
const crypto = require("node:crypto");

const SRC = path.join(__dirname, "..", "src");
const readJson = (f) => JSON.parse(fs.readFileSync(path.join(SRC, f), "utf8"));

test("base manifest drops unused permissions, keeps the needed ones (TASK-096)", () => {
  const perms = readJson("manifest.base.json").permissions;
  assert.ok(!perms.includes("downloads"), "the app downloads, not the extension");
  assert.ok(!perms.includes("activeTab"), "redundant given <all_urls> host_permissions");
  for (const needed of ["nativeMessaging", "cookies", "webRequest", "contextMenus", "storage"]) {
    assert.ok(perms.includes(needed), `missing required permission: ${needed}`);
  }
});

test("base manifest declares the options page (TASK-095)", () => {
  const base = readJson("manifest.base.json");
  assert.equal(base.options_ui?.page, "options.html");
  assert.ok(fs.existsSync(path.join(SRC, "options.html")));
  assert.ok(fs.existsSync(path.join(SRC, "options.js")));
});

test("Firefox uses a background.scripts event page, not an unsupported service_worker (TASK-097)", () => {
  const ff = readJson("manifest.firefox.json");
  assert.equal(ff.background?.service_worker, undefined,
    "Firefox MV3 does not support background.service_worker — it silently breaks the background");
  assert.ok(Array.isArray(ff.background?.scripts), "Firefox must use background.scripts");
  assert.deepEqual(ff.background.scripts, ["jdcore.js", "background.js"],
    "jdcore.js must load before background.js so globalThis.JD is defined");
  for (const f of ff.background.scripts) {
    assert.ok(fs.existsSync(path.join(SRC, f)), `firefox background script missing: ${f}`);
  }
});

test("content script runs in every frame so iframe-embedded videos get their own icon (TASK-164)", () => {
  const base = readJson("manifest.base.json");
  const [entry] = base.content_scripts;
  assert.equal(entry.all_frames, true, "all_frames must be true for iframe-embedded video detection");
});

test("Chromium targets keep the service_worker background (TASK-097)", () => {
  for (const file of ["manifest.chrome.json", "manifest.edge.json"]) {
    assert.equal(readJson(file).background?.service_worker, "background.js",
      `${file} must keep the MV3 service worker`);
  }
});

test("background.js guards importScripts so it loads as a Firefox event page (TASK-097)", () => {
  const bg = fs.readFileSync(path.join(SRC, "background.js"), "utf8");
  // importScripts is service-worker-only; an unguarded call throws in a Firefox event page, so every
  // call must sit behind a typeof guard.
  assert.ok(
    /if \(typeof importScripts === "function"\) \{\s*importScripts\("jdcore\.js"\);\s*\}/.test(bg),
    "background.js must call importScripts only inside a typeof guard");
});

test("Chromium manifests pin a key deriving to the wired extension id (TASK-098)", () => {
  const expectedId = "jomjhgmmkdicaonknkjlmhdfhlnchcnl";
  for (const file of ["manifest.chrome.json", "manifest.edge.json"]) {
    const key = readJson(file).key;
    assert.ok(typeof key === "string" && key.length > 0, `${file} missing key`);
    const der = Buffer.from(key, "base64");
    const sha = crypto.createHash("sha256").update(der).digest();
    const id = [...sha.subarray(0, 16).toString("hex")]
      .map((c) => String.fromCharCode(97 + parseInt(c, 16)))
      .join("");
    assert.equal(id, expectedId, `${file} key must derive to the host-allowlisted id`);
  }
});
