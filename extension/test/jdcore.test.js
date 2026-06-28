// Unit tests for the shared extension logic (TASK-067/068/069), run with `node --test`.
"use strict";

const test = require("node:test");
const assert = require("node:assert/strict");
const JD = require("../src/jdcore.js");

test("classifyMedia detects HLS/DASH/MP4/audio (TASK-068 AC0)", () => {
  assert.equal(JD.classifyMedia("https://x/playlist.m3u8"), "hls");
  assert.equal(JD.classifyMedia("https://x/manifest.mpd"), "dash");
  assert.equal(JD.classifyMedia("https://x/video.mp4?t=1"), "video");
  assert.equal(JD.classifyMedia("https://x/seg.ts"), "video");
  assert.equal(JD.classifyMedia("https://x/song.mp3"), "audio");
  assert.equal(JD.classifyMedia("https://x/page.html"), null);
  assert.equal(JD.classifyMedia("not a url"), null);
});

test("isBlacklisted matches host and subdomains (TASK-069 AC0)", () => {
  const list = ["example.com", "videos.test"];
  assert.equal(JD.isBlacklisted("https://example.com/a", list), true);
  assert.equal(JD.isBlacklisted("https://www.example.com/a", list), true);
  assert.equal(JD.isBlacklisted("https://cdn.example.com/a", list), true, "subdomains are covered");
  assert.equal(JD.isBlacklisted("https://videos.test/x", list), true);
  assert.equal(JD.isBlacklisted("https://other.com/a", list), false);
  assert.equal(JD.isBlacklisted("https://notexample.com/a", list), false, "no false suffix match");
});

test("blacklist add/remove normalizes and dedupes (TASK-069)", () => {
  let list = JD.addToBlacklist([], "https://www.Example.com/watch");
  assert.deepEqual(list, ["example.com"]);
  list = JD.addToBlacklist(list, "example.com");
  assert.deepEqual(list, ["example.com"], "no duplicate");
  list = JD.addToBlacklist(list, "foo.test");
  assert.deepEqual(list, ["example.com", "foo.test"]);
  list = JD.removeFromBlacklist(list, "www.example.com");
  assert.deepEqual(list, ["foo.test"]);
});

test("pickContextUrl prefers link, then media src, then page (TASK-067)", () => {
  assert.equal(
    JD.pickContextUrl({ linkUrl: "https://a/l", srcUrl: "https://a/s", pageUrl: "https://a/p" }),
    "https://a/l",
  );
  assert.equal(JD.pickContextUrl({ srcUrl: "https://a/s", pageUrl: "https://a/p" }), "https://a/s");
  assert.equal(JD.pickContextUrl({ pageUrl: "https://a/p" }), "https://a/p");
  assert.equal(JD.pickContextUrl({}), null);
});

test("buildDownloadMessage carries auth context (TASK-067 AC1)", () => {
  const msg = JD.buildDownloadMessage({
    url: "https://a/file.zip",
    pageUrl: "https://a/page",
    cookies: "sid=abc",
    headers: { Authorization: "Bearer x" },
  });
  assert.equal(msg.type, "DOWNLOAD_LINK");
  assert.equal(msg.url, "https://a/file.zip");
  assert.equal(msg.referrer, "https://a/page", "referrer defaults to the page URL");
  assert.equal(msg.cookies, "sid=abc");
  assert.deepEqual(msg.headers, { Authorization: "Bearer x" });
});

test("formatCookieHeader serializes name=value pairs (TASK-067 AC1)", () => {
  const header = JD.formatCookieHeader([
    { name: "sid", value: "abc" },
    { name: "theme", value: "dark" },
    { bad: true },
  ]);
  assert.equal(header, "sid=abc; theme=dark");
});
