// Master-playlist preference in the network sniffer (TASK-241), run with `node --test`.
//
// An HLS player fetches the master ("multivariant") playlist once and then a variant playlist for whichever
// quality it decides to stream. The sniffer sees both, and handing over the newest one gave the app the
// *currently playing* variant — which is how a 720p default still produced a 396x270 file, and why the
// quality picker had a single fixed rendition to offer. background.js now resolves the master by reading the
// candidate bodies (only a master carries #EXT-X-STREAM-INF) and requiring it to actually list the variant
// that is playing, so a page with several videos can't hand over the wrong one. Every uncertain path
// degrades to the variant, never to nothing.
"use strict";

const test = require("node:test");
const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");
const vm = require("node:vm");

const SRC = path.join(__dirname, "..", "src");
const read = (f) => fs.readFileSync(path.join(SRC, f), "utf8");

const VIDEO_ID = "2087461469881336049";
const MASTER_URL = `https://video.twimg.com/amplify_video/${VIDEO_ID}/pl/9k3T.m3u8?container=fmp4`;
const LOW_VARIANT_URL = `https://video.twimg.com/amplify_video/${VIDEO_ID}/vid/avc1/396x270/lo.m3u8?v=e6c`;
const HIGH_VARIANT_URL = `https://video.twimg.com/amplify_video/${VIDEO_ID}/vid/avc1/1280x720/hi.m3u8?v=e6c`;

const MASTER_BODY = [
  "#EXTM3U",
  "#EXT-X-STREAM-INF:BANDWIDTH=256000,RESOLUTION=396x270",
  `/amplify_video/${VIDEO_ID}/vid/avc1/396x270/lo.m3u8?v=e6c`,
  "#EXT-X-STREAM-INF:BANDWIDTH=2176000,RESOLUTION=1280x720",
  `/amplify_video/${VIDEO_ID}/vid/avc1/1280x720/hi.m3u8?v=e6c`,
].join("\n");

const VARIANT_BODY = ["#EXTM3U", "#EXT-X-TARGETDURATION:3", "#EXTINF:3.000,", "seg1.m4s"].join("\n");

/**
 * Loads jdcore.js + background.js with a stubbed network.
 * @param {Record<string, string>} bodies url -> playlist body served to background.js's own fetch
 */
function makeSandbox(bodies = {}) {
  let onBeforeRequest = null;
  let onMessage = null;
  const fetched = [];

  const api = {
    webRequest: {
      onBeforeRequest: {
        addListener(cb) {
          onBeforeRequest = cb;
        },
      },
    },
    tabs: { onUpdated: { addListener() {} }, onRemoved: { addListener() {} } },
    runtime: {
      onInstalled: { addListener() {} },
      onStartup: { addListener() {} },
      onMessage: {
        addListener(cb) {
          onMessage = cb;
        },
      },
      sendNativeMessage: (host, message, cb) => cb?.({ type: "ok" }),
      lastError: undefined,
    },
    contextMenus: { onClicked: { addListener() {} }, removeAll(cb) { cb?.(); }, create() {} },
    storage: { sync: { get: async () => ({}), set: async () => {} } },
    cookies: { getAll: async () => [] },
    downloads: { onCreated: { addListener() {} } },
  };

  const sandbox = {
    browser: api,
    console,
    URL,
    navigator: { userAgent: "test-agent" },
    fetch: async (url) => {
      fetched.push(url);
      if (!(url in bodies)) {
        throw new TypeError("network error");
      }
      return { ok: true, text: async () => bodies[url] };
    },
  };
  sandbox.globalThis = sandbox;
  const context = vm.createContext(sandbox);
  vm.runInContext(read("jdcore.js"), context, { filename: "jdcore.js" });
  vm.runInContext(read("background.js"), context, { filename: "background.js" });

  return {
    /** Replays what the browser's webRequest listener would report, in order. */
    sniff: (tabId, ...urls) => {
      for (const url of urls) {
        onBeforeRequest({ tabId, url });
      }
    },
    tabMedia: (tabId) =>
      new Promise((resolve) => {
        onMessage({ type: "GET_TAB_MEDIA", tabId }, {}, resolve);
      }),
    fetched,
  };
}

test("the hand-off uses the master when both a master and a variant were observed (TASK-241 AC)", async () => {
  const { sniff, tabMedia } = makeSandbox({ [MASTER_URL]: MASTER_BODY, [LOW_VARIANT_URL]: VARIANT_BODY });

  sniff(1, MASTER_URL, LOW_VARIANT_URL); // the real order: master first, then the variant the player picks
  const { preferred } = await tabMedia(1);

  assert.equal(preferred.url, MASTER_URL, "not the 396x270 variant the browser happened to be streaming");
  assert.equal(preferred.kind, "hls");
});

test("a master is preferred even after the player switches variants mid-playback (TASK-241)", async () => {
  const { sniff, tabMedia } = makeSandbox({
    [MASTER_URL]: MASTER_BODY,
    [LOW_VARIANT_URL]: VARIANT_BODY,
    [HIGH_VARIANT_URL]: VARIANT_BODY,
  });

  sniff(1, MASTER_URL, LOW_VARIANT_URL, HIGH_VARIANT_URL);
  const { preferred } = await tabMedia(1);

  assert.equal(preferred.url, MASTER_URL, "the newest entry is a later variant, not a second master");
});

test("with no master observed the hand-off keeps the variant (TASK-241 no-regression)", async () => {
  const { sniff, tabMedia } = makeSandbox({ [LOW_VARIANT_URL]: VARIANT_BODY });

  sniff(1, LOW_VARIANT_URL);
  const { preferred } = await tabMedia(1);

  assert.equal(preferred.url, LOW_VARIANT_URL, "degrades to today's behaviour rather than to nothing");
});

test("an unreadable playlist degrades to the variant rather than failing the hand-off (TASK-241)", async () => {
  const { sniff, tabMedia } = makeSandbox({}); // every fetch throws

  sniff(1, MASTER_URL, LOW_VARIANT_URL);
  const { preferred } = await tabMedia(1);

  assert.equal(preferred.url, LOW_VARIANT_URL);
});

test("another video's master is never handed over for the playing one (TASK-241)", async () => {
  const otherMaster = "https://video.twimg.com/amplify_video/999/pl/other.m3u8";
  const otherBody = ["#EXTM3U", "#EXT-X-STREAM-INF:BANDWIDTH=1", "/amplify_video/999/vid/avc1/396x270/x.m3u8"].join(
    "\n",
  );
  const { sniff, tabMedia } = makeSandbox({ [otherMaster]: otherBody, [LOW_VARIANT_URL]: VARIANT_BODY });

  // A timeline page: an unrelated video's master was seen first, the played video's master never was.
  sniff(1, otherMaster, LOW_VARIANT_URL);
  const { preferred } = await tabMedia(1);

  assert.equal(preferred.url, LOW_VARIANT_URL, "a master that doesn't list this variant is not its master");
});

test("a non-HLS detection is handed over without reading any playlist (TASK-241)", async () => {
  const { sniff, tabMedia, fetched } = makeSandbox();

  sniff(1, "https://cdn.example.com/clip.mp4");
  const { preferred } = await tabMedia(1);

  assert.equal(preferred.url, "https://cdn.example.com/clip.mp4");
  assert.equal(fetched.length, 0, "no network cost for a plain progressive file");
});

test("playlist bodies are read once per URL, not once per poll (TASK-241)", async () => {
  const { sniff, tabMedia, fetched } = makeSandbox({ [MASTER_URL]: MASTER_BODY, [LOW_VARIANT_URL]: VARIANT_BODY });

  sniff(1, MASTER_URL, LOW_VARIANT_URL);
  await tabMedia(1);
  await tabMedia(1); // content.js polls this on a bounded retry loop
  await tabMedia(1);

  assert.deepEqual(
    [...new Set(fetched)].sort(),
    [LOW_VARIANT_URL, MASTER_URL].sort(),
    "only the two candidate playlists were ever read",
  );
  assert.equal(fetched.length, 2, "and each exactly once — the analysis is memoized");
});

test("a page whose only requests are audio yields no preferred media (TASK-181 still holds)", async () => {
  const { sniff, tabMedia } = makeSandbox();

  sniff(1, "https://cdn.example.com/ui-sound.mp3");
  const { preferred } = await tabMedia(1);

  assert.equal(preferred, null, "the app has no audio-download feature, so an .mp3 is never a target");
});
