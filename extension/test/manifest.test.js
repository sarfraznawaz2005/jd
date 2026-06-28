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
