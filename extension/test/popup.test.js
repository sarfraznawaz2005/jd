// Popup detected-media rendering tests (TASK-181), run with `node --test`.
//
// Real-world testing (screenshots) found the detected-media list rendered as plain unstyled buttons
// (class="media-item", with zero matching CSS rules) instead of the already-designed `.media` row
// (icon + title + download button) the mockup and popup.css define. This exercises renderMedia()'s
// actual DOM output against a minimal document stub, following the same vm-sandbox pattern as
// content.test.js/background.test.js.
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
      add() {},
      remove() {},
      toggle() {},
      contains: () => false,
    },
    setAttribute() {},
    addEventListener(type, handler) {
      el.listeners[type] = handler;
    },
    appendChild(child) {
      el.children.push(child);
      return child;
    },
    append(...kids) {
      el.children.push(...kids);
    },
    replaceChildren() {
      el.children = [];
    },
  };
  return el;
}

function makeSandbox({ tabMedia = [], settings = {} } = {}) {
  const elementsById = new Map();
  const ids = [
    "status", "status-dot", "status-text", "site-toggle", "media-empty", "media-list",
    "send-link", "default-quality", "open-settings",
  ];
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

  const sentMessages = [];
  const api = {
    tabs: { query: async () => [{ id: 1, url: "https://example.com/page" }] },
    storage: { sync: { get: async () => ({}) } },
    runtime: {
      sendMessage: (msg) => {
        sentMessages.push(msg);
        if (msg.type === "PING") {
          return Promise.resolve({ ok: true });
        }
        if (msg.type === "GET_TAB_MEDIA") {
          return Promise.resolve({ ok: true, media: tabMedia });
        }
        if (msg.type === "GET_SETTINGS") {
          return Promise.resolve({ ok: true, settings });
        }
        return Promise.resolve({ ok: true });
      },
      openOptionsPage: () => {},
    },
  };

  const sandbox = { console, URL, document: documentStub, browser: api };
  sandbox.globalThis = sandbox;
  return { sandbox, elementsById, sentMessages };
}

/** Loads jdcore.js then popup.js, fires DOMContentLoaded, and flushes pending async work. */
async function runPopup(options) {
  const { sandbox, elementsById, sentMessages } = makeSandbox(options);
  const context = vm.createContext(sandbox);
  vm.runInContext(read("jdcore.js"), context, { filename: "jdcore.js" });
  vm.runInContext(read("popup.js"), context, { filename: "popup.js" });
  await sandbox.document.__ready();
  await new Promise((resolve) => setTimeout(resolve, 0));
  return { elementsById, sentMessages };
}

test("with detected media, the empty state is hidden and the list is populated with styled rows", async () => {
  const { elementsById } = await runPopup({
    tabMedia: [{ url: "https://cdn.example.com/clip.mp4", kind: "video" }],
  });

  const empty = elementsById.get("media-empty");
  const list = elementsById.get("media-list");
  assert.equal(empty.hidden, true, "the 'No media detected yet' placeholder is hidden once media exists");
  assert.equal(list.hidden, false);
  assert.equal(list.children.length, 1);

  const row = list.children[0];
  assert.equal(row.className, "media", "uses the already-styled .media row, not an unstyled plain button");
  const [icon, name, download] = row.children;
  assert.equal(icon.className, "ft");
  assert.equal(name.className, "nm");
  assert.equal(name.children[0].className, "t");
  assert.equal(name.children[0].textContent, "video · clip.mp4");
  assert.equal(download.className, "dl");
});

test("with no detected media, the empty state stays visible and the list stays empty", async () => {
  const { elementsById } = await runPopup({ tabMedia: [] });

  const empty = elementsById.get("media-empty");
  const list = elementsById.get("media-list");
  assert.equal(empty.hidden, false);
  assert.equal(list.hidden, true);
  assert.equal(list.children.length, 0);
});

test("clicking a row's download control sends DOWNLOAD_DETECTED_MEDIA for that item's URL", async () => {
  const { elementsById, sentMessages } = await runPopup({
    tabMedia: [{ url: "https://cdn.example.com/clip.mp4", kind: "video" }],
  });

  const row = elementsById.get("media-list").children[0];
  const download = row.children[2];
  download.listeners.click();
  await new Promise((resolve) => setTimeout(resolve, 0));

  assert.equal(
    sentMessages.some((m) => m.type === "DOWNLOAD_DETECTED_MEDIA" && m.url === "https://cdn.example.com/clip.mp4"),
    true,
  );
});
