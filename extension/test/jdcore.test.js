// Unit tests for the shared extension logic (TASK-067/068/069), run with `node --test`.
"use strict";

const test = require("node:test");
const assert = require("node:assert/strict");
const JD = require("../src/jdcore.js");

test("classifyMedia detects HLS/DASH/MP4, never audio (TASK-068 AC0, TASK-181)", () => {
  assert.equal(JD.classifyMedia("https://x/playlist.m3u8"), "hls");
  assert.equal(JD.classifyMedia("https://x/manifest.mpd"), "dash");
  assert.equal(JD.classifyMedia("https://x/video.mp4?t=1"), "video");
  assert.equal(JD.classifyMedia("https://x/seg.ts"), "video");
  assert.equal(JD.classifyMedia("https://x/song.mp3"), null, "the app has no audio-download feature");
  assert.equal(JD.classifyMedia("https://x/page.html"), null);
  assert.equal(JD.classifyMedia("not a url"), null);
});

test("classifyMedia detects extension-less streaming endpoints (TASK-232)", () => {
  const yt =
    "https://rr3---sn-4g5e6nez.googlevideo.com/videoplayback?expire=1&mime=video%2Fmp4&itag=248";
  assert.equal(JD.classifyMedia(yt), "video", "YouTube's /videoplayback has no file extension at all");
  assert.equal(
    JD.classifyMedia(yt.replace("video%2Fmp4", "audio%2Fwebm")),
    null,
    "an audio-only stream stays unclassified — the app has no audio-download feature",
  );
  assert.equal(
    JD.classifyMedia("https://rr3.googlevideo.com/videoplayback?expire=1"),
    null,
    "no mime= param means we cannot tell it is video",
  );
  assert.equal(
    JD.classifyMedia("https://x/api/watch?mime=video%2Fmp4"),
    null,
    "an unrelated path is not a streaming endpoint just because it mentions a video mime",
  );
});

test("normalizeMediaUrl strips per-chunk params so one stream is one entry (TASK-232)", () => {
  const base = "https://rr3.googlevideo.com/videoplayback?expire=1&mime=video%2Fmp4&itag=248";
  assert.equal(JD.normalizeMediaUrl(`${base}&range=0-1310719&rn=4&rbuf=0`), base);
  assert.equal(
    JD.normalizeMediaUrl(`${base}&range=1310720-2621439&rn=5&rbuf=12`),
    JD.normalizeMediaUrl(`${base}&range=0-1310719&rn=4&rbuf=0`),
    "consecutive chunks of the same stream normalize to the same URL",
  );
  assert.equal(JD.normalizeMediaUrl("https://x/video.mp4"), "https://x/video.mp4");
  assert.equal(JD.normalizeMediaUrl("not a url"), "not a url", "unparseable input is returned as-is");
});

test("normalizeMediaUrl strips Instagram/Facebook's byte-serving params, keeping unrelated ones (TASK-242)", () => {
  const stripped = JD.normalizeMediaUrl(
    "https://instagram.fkhi28-1.fna.fbcdn.net/o1/v/t2/clip.mp4?_nc_cat=110&bytestart=15876&byteend=30194",
  );
  assert.equal(stripped, "https://instagram.fkhi28-1.fna.fbcdn.net/o1/v/t2/clip.mp4?_nc_cat=110");
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

test("createMediaStore dedupes and is per-tab (TASK-068)", () => {
  const store = JD.createMediaStore();
  assert.equal(store.add(1, { url: "https://a/x.m3u8", kind: "hls" }), true);
  assert.equal(store.add(1, { url: "https://a/x.m3u8", kind: "hls" }), false, "duplicate URL ignored");
  assert.equal(store.add(1, { url: "https://a/y.mp4", kind: "video" }), true);
  assert.equal(store.count(1), 2);
  assert.equal(store.count(2), 0, "other tabs are isolated");
  store.clear(1);
  assert.equal(store.count(1), 0);
});

test("createMediaStore bounds the list per tab (TASK-068)", () => {
  const store = JD.createMediaStore(3);
  for (let i = 0; i < 10; i++) {
    store.add(1, { url: `https://a/${i}.mp4`, kind: "video" });
  }
  assert.equal(store.count(1), 3, "the list is capped");
});

test("buildBlacklistSyncMessage normalizes domains (TASK-069 AC1)", () => {
  const msg = JD.buildBlacklistSyncMessage(["https://www.Example.com/x", "foo.test", "  "]);
  assert.equal(msg.type, "BLACKLIST_SYNC");
  assert.deepEqual(msg.domains, ["example.com", "foo.test"]);
});

test("mediaLabel describes a detected item (TASK-071 AC0)", () => {
  assert.equal(JD.mediaLabel({ url: "https://x/clip%20a.mp4", kind: "video" }), "video · clip a.mp4");
  assert.equal(JD.mediaLabel({ url: "https://x/playlist.m3u8" }), "hls · playlist.m3u8");
  assert.equal(JD.mediaLabel({}), "Media");
});

test("resolveMediaUrl prefers the element's own src, falls back to a child source (TASK-164)", () => {
  assert.equal(
    JD.resolveMediaUrl("/clip.mp4", null, "https://x/page"),
    "https://x/clip.mp4",
    "own src resolves against the document base",
  );
  assert.equal(
    JD.resolveMediaUrl(null, "clip.mp4", "https://x/dir/page"),
    "https://x/dir/clip.mp4",
    "falls back to a child <source src> when the element has none of its own",
  );
  assert.equal(JD.resolveMediaUrl(null, null, "https://x/page"), null, "no source at all");
  assert.equal(JD.resolveMediaUrl("not a url", null, ""), null, "unparseable src yields null");
});

test("resolveMediaUrl rejects blob: URLs — not fetchable outside the page (TASK-164)", () => {
  assert.equal(JD.resolveMediaUrl("blob:https://x/abc-123", null, "https://x/page"), null);
});

test("computeIconPosition pins the icon to the element's top-right corner (TASK-164)", () => {
  const rect = { top: 100, left: 200, right: 500, bottom: 300, width: 300, height: 200 };
  const viewport = { width: 1024, height: 768 };
  const pos = JD.computeIconPosition(rect, viewport, 28, 8);
  assert.equal(pos.visible, true);
  assert.equal(pos.top, 108, "top = rect.top + margin");
  assert.equal(pos.left, 464, "left = rect.right - iconSize - margin");
});

test("computeIconPosition hides the icon when the element is off-screen or zero-sized (TASK-164)", () => {
  const viewport = { width: 1024, height: 768 };
  assert.equal(
    JD.computeIconPosition({ top: -500, left: 0, right: 300, bottom: -300, width: 300, height: 200 }, viewport)
      .visible,
    false,
    "scrolled entirely above the viewport",
  );
  assert.equal(
    JD.computeIconPosition({ top: 2000, left: 0, right: 300, bottom: 2200, width: 300, height: 200 }, viewport)
      .visible,
    false,
    "scrolled entirely below the viewport",
  );
  assert.equal(
    JD.computeIconPosition({ top: 0, left: 0, right: 0, bottom: 0, width: 0, height: 0 }, viewport).visible,
    false,
    "collapsed/hidden element",
  );
});

test("computeIconPosition never places the icon left of the element when it's narrower than the icon (TASK-164)", () => {
  const rect = { top: 10, left: 10, right: 20, bottom: 30, width: 10, height: 20 };
  const pos = JD.computeIconPosition(rect, { width: 1024, height: 768 }, 28, 8);
  assert.equal(pos.visible, true);
  assert.equal(pos.left, 10, "clamped to rect.left rather than going negative past it");
});

test("formatCookieHeader serializes name=value pairs (TASK-067 AC1)", () => {
  const header = JD.formatCookieHeader([
    { name: "sid", value: "abc" },
    { name: "theme", value: "dark" },
    { bad: true },
  ]);
  assert.equal(header, "sid=abc; theme=dark");
});

test("isExtractablePage matches the app's site extractors (TASK-232)", () => {
  assert.equal(JD.isExtractablePage("https://www.youtube.com/watch?v=abc"), true);
  assert.equal(JD.isExtractablePage("https://youtube.com/watch?v=abc"), true);
  assert.equal(JD.isExtractablePage("https://m.youtube.com/watch?v=abc"), true, "subdomains count");
  assert.equal(JD.isExtractablePage("https://youtu.be/abc"), true);
  assert.equal(JD.isExtractablePage("https://www.facebook.com/watch/?v=1"), true);
  assert.equal(JD.isExtractablePage("https://fb.watch/xyz"), true);
  assert.equal(JD.isExtractablePage("https://example.com/video"), false);
  assert.equal(JD.isExtractablePage("https://notyoutube.com/x"), false, "no false suffix match");
  assert.equal(JD.isExtractablePage("not a url"), false);
});

test("isSuppressedHomePage suppresses only the home/root page of YouTube, X/Twitter, Facebook, Instagram (TASK-243)", () => {
  // Bare domain, www., http/https, trailing slash — all home-page forms are suppressed.
  for (const url of [
    "https://youtube.com",
    "https://youtube.com/",
    "https://www.youtube.com",
    "https://www.youtube.com/",
    "http://youtube.com/",
    "https://x.com",
    "https://x.com/",
    "https://www.x.com/",
    "https://x.com/home",
    "http://twitter.com",
    "https://www.twitter.com/",
    "https://twitter.com/home",
    "https://www.twitter.com/home/",
    "https://facebook.com",
    "https://www.facebook.com/",
    "http://www.facebook.com",
    "https://instagram.com",
    "https://www.instagram.com/",
    "http://instagram.com/",
  ]) {
    assert.equal(JD.isSuppressedHomePage(url), true, `${url} should be suppressed`);
  }

  // Any other page on these same hosts is untouched.
  for (const url of [
    "https://www.youtube.com/watch?v=abc",
    "https://x.com/someuser/status/12345",
    "https://twitter.com/someuser",
    "https://www.facebook.com/watch/?v=1",
    "https://www.facebook.com/someuser",
    "https://www.instagram.com/reel/abc123/",
    "https://www.instagram.com/someuser/",
  ]) {
    assert.equal(JD.isSuppressedHomePage(url), false, `${url} should still show the icon`);
  }

  // Unrelated sites and malformed input are never suppressed.
  assert.equal(JD.isSuppressedHomePage("https://example.com/"), false);
  assert.equal(JD.isSuppressedHomePage("https://facebook.com.evil.com/"), false, "not fooled by a lookalike host");
  assert.equal(JD.isSuppressedHomePage("not a url"), false);
});

test("buildDownloadMessage carries the extract flag only when set (TASK-232)", () => {
  assert.equal(JD.buildDownloadMessage({ url: "https://x/a.mp4" }).extract, false);
  assert.equal(JD.buildDownloadMessage({ url: "https://x/p", extract: true }).extract, true);
  assert.equal(
    JD.buildDownloadMessage({ url: "https://x/p", extract: "yes" }).extract,
    false,
    "only a real boolean true turns a direct download into an extraction",
  );
});

test("isExtractablePage covers x.com/twitter.com (TASK-241)", () => {
  // Verified hands-on: yt-dlp resolves an x.com status page into a real title and every HLS variant, so the
  // page URL is worth handing to the extractor pipeline. There is no in-house Twitter extractor, which is
  // why the hand-off also carries a fallbackUrl.
  assert.equal(JD.isExtractablePage("https://x.com/unicodef1wn/status/2087461469881336049"), true);
  assert.equal(JD.isExtractablePage("https://twitter.com/someone/status/123"), true);
  assert.equal(JD.isExtractablePage("https://mobile.twitter.com/someone/status/123"), true, "subdomains count");
  assert.equal(JD.isExtractablePage("https://notx.com/status/1"), false, "no false suffix match");
});

test("isExtractablePage covers instagram.com (TASK-242)", () => {
  // Instagram serves video via a custom bytestart/byteend query string on the CDN URL rather than standard
  // HTTP Range, so the sniffer only ever sees one small buffered chunk. Like Twitter, there is no in-house
  // Instagram extractor, so the page URL routes through yt-dlp (if enabled) with a fallbackUrl safety net.
  assert.equal(JD.isExtractablePage("https://www.instagram.com/reels/Dbnng9EBBfr/"), true);
  assert.equal(JD.isExtractablePage("https://instagram.com/reels/Dbnng9EBBfr/"), true);
  assert.equal(JD.isExtractablePage("https://notinstagram.com/reels/1"), false, "no false suffix match");
});

test("buildDownloadMessage carries a fallback stream URL, normalizing absent ones to null (TASK-241)", () => {
  assert.equal(JD.buildDownloadMessage({ url: "https://x/p" }).fallbackUrl, null);
  assert.equal(
    JD.buildDownloadMessage({ url: "https://x/p", extract: true, fallbackUrl: "https://cdn/pl.m3u8" }).fallbackUrl,
    "https://cdn/pl.m3u8",
  );
  assert.equal(JD.buildDownloadMessage({ url: "https://x/p", fallbackUrl: "" }).fallbackUrl, null);
  assert.equal(JD.buildDownloadMessage({ url: "https://x/p", fallbackUrl: 42 }).fallbackUrl, null);
});

// --- HLS master-playlist recognition (TASK-241) ------------------------------------------------------

const TWITTER_MASTER = [
  "#EXTM3U",
  "#EXT-X-INDEPENDENT-SEGMENTS",
  '#EXT-X-STREAM-INF:BANDWIDTH=256000,RESOLUTION=396x270,CODECS="mp4a.40.2,avc1.4d001e"',
  "/amplify_video/2087461469881336049/vid/avc1/396x270/lo.m3u8?v=e6c",
  '#EXT-X-STREAM-INF:BANDWIDTH=2176000,RESOLUTION=1280x720,CODECS="mp4a.40.2,avc1.640020"',
  "/amplify_video/2087461469881336049/vid/avc1/1280x720/hi.m3u8?v=e6c",
  "",
].join("\n");

const VARIANT_PLAYLIST = [
  "#EXTM3U",
  "#EXT-X-TARGETDURATION:3",
  "#EXT-X-MAP:URI=/amplify_video/2087461469881336049/vid/avc1/396x270/init.mp4",
  "#EXTINF:3.000,",
  "/amplify_video/2087461469881336049/vid/avc1/396x270/seg1.m4s",
  "#EXT-X-ENDLIST",
].join("\n");

test("parseMasterVariants lists a master's variant URIs and ignores a variant playlist (TASK-241)", () => {
  assert.deepEqual(JD.parseMasterVariants(TWITTER_MASTER), [
    "/amplify_video/2087461469881336049/vid/avc1/396x270/lo.m3u8?v=e6c",
    "/amplify_video/2087461469881336049/vid/avc1/1280x720/hi.m3u8?v=e6c",
  ]);
  assert.deepEqual(JD.parseMasterVariants(VARIANT_PLAYLIST), [], "a media playlist has no #EXT-X-STREAM-INF");
  assert.deepEqual(JD.parseMasterVariants(""), []);
  assert.deepEqual(JD.parseMasterVariants(null), []);
});

test("parseMasterVariants tolerates CRLF and blank lines between tag and URI (TASK-241)", () => {
  const body = "#EXTM3U\r\n#EXT-X-STREAM-INF:BANDWIDTH=1\r\n\r\nhigh.m3u8\r\n";
  assert.deepEqual(JD.parseMasterVariants(body), ["high.m3u8"]);
});

test("playlistTargets matches a variant regardless of the player's own query params (TASK-241)", () => {
  const masterUrl = "https://video.twimg.com/amplify_video/2087461469881336049/pl/9k3T.m3u8?container=fmp4";
  const uris = JD.parseMasterVariants(TWITTER_MASTER);

  assert.equal(
    JD.playlistTargets(
      uris,
      masterUrl,
      "https://video.twimg.com/amplify_video/2087461469881336049/vid/avc1/396x270/lo.m3u8?v=e6c&t=9",
    ),
    true,
    "resolved against the master and compared on origin+path, so extra query params still match",
  );
  assert.equal(
    JD.playlistTargets(uris, masterUrl, "https://video.twimg.com/amplify_video/999/vid/avc1/396x270/lo.m3u8"),
    false,
    "another video's variant is not claimed by this master",
  );
  assert.equal(JD.playlistTargets(uris, masterUrl, "not a url"), false);
  assert.equal(JD.playlistTargets(null, masterUrl, "https://x/a.m3u8"), false);
});
