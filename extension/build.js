#!/usr/bin/env node
/*
 * JustDownload extension build — zero runtime/dev dependencies.
 *
 * Assembles a per-browser, ready-to-load MV3 extension from ONE shared source
 * tree (src/) into dist/<browser>/. There is no copy-paste manifest: each
 * browser's manifest.json is base + a small override, deep-merged here, so the
 * three stay in lock-step.
 *
 * Usage:
 *   node build.js              build all targets (chrome, edge, firefox)
 *   node build.js chrome edge  build a subset
 *   node build.js --clean      remove dist/
 */
"use strict";

const fs = require("node:fs");
const path = require("node:path");
const zlib = require("node:zlib");

const ROOT = __dirname;
const SRC = path.join(ROOT, "src");
const ICONS_DIR = path.join(SRC, "icons");
const DIST = path.join(ROOT, "dist");

const TARGETS = ["chrome", "edge", "firefox"];
const ICON_SIZES = [16, 32, 48, 128];

// Shared assets copied verbatim into every browser bundle.
const SHARED_ASSETS = ["background.js", "content.js", "popup.html", "popup.css", "popup.js"];

/* ---------------------------------------------------------------- helpers */

function rmrf(dir) {
  fs.rmSync(dir, { recursive: true, force: true });
}

function readJson(file) {
  return JSON.parse(fs.readFileSync(file, "utf8"));
}

/** Deep-merge `override` onto a clone of `base` (objects merge, scalars/arrays replace). */
function deepMerge(base, override) {
  const out = Array.isArray(base) ? [...base] : { ...base };
  for (const [key, val] of Object.entries(override)) {
    if (
      val && typeof val === "object" && !Array.isArray(val) &&
      out[key] && typeof out[key] === "object" && !Array.isArray(out[key])
    ) {
      out[key] = deepMerge(out[key], val);
    } else {
      out[key] = val;
    }
  }
  return out;
}

/* ----------------------------------------------------------- PNG encoding */

/** Encode raw RGBA bytes into a PNG buffer (no deps; uses Node zlib + crc32). */
function encodePng(width, height, rgba) {
  const sig = Buffer.from([0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a]);

  const ihdr = Buffer.alloc(13);
  ihdr.writeUInt32BE(width, 0);
  ihdr.writeUInt32BE(height, 4);
  ihdr[8] = 8;  // bit depth
  ihdr[9] = 6;  // color type: RGBA
  // bytes 10-12: compression/filter/interlace = 0

  // Prefix each scanline with filter byte 0 (none).
  const stride = width * 4;
  const raw = Buffer.alloc((stride + 1) * height);
  for (let y = 0; y < height; y++) {
    raw[y * (stride + 1)] = 0;
    rgba.copy(raw, y * (stride + 1) + 1, y * stride, y * stride + stride);
  }
  const idat = zlib.deflateSync(raw, { level: 9 });

  const chunk = (type, data) => {
    const len = Buffer.alloc(4);
    len.writeUInt32BE(data.length, 0);
    const typed = Buffer.concat([Buffer.from(type, "ascii"), data]);
    const crc = Buffer.alloc(4);
    crc.writeUInt32BE(zlib.crc32(typed) >>> 0, 0);
    return Buffer.concat([len, typed, crc]);
  };

  return Buffer.concat([
    sig,
    chunk("IHDR", ihdr),
    chunk("IDAT", idat),
    chunk("IEND", Buffer.alloc(0)),
  ]);
}

/** Draw the JustDownload mark: accent rounded square + white download arrow. */
function drawIcon(size) {
  const rgba = Buffer.alloc(size * size * 4);
  // Light-theme accent #5b67d6.
  const ACC = [0x5b, 0x67, 0xd6];
  const radius = size * 0.22;

  const inRoundedRect = (x, y) => {
    const r = radius;
    const minX = r, minY = r, maxX = size - r, maxY = size - r;
    const cx = Math.min(Math.max(x, minX), maxX);
    const cy = Math.min(Math.max(y, minY), maxY);
    return (x - cx) ** 2 + (y - cy) ** 2 <= r * r;
  };

  for (let y = 0; y < size; y++) {
    for (let x = 0; x < size; x++) {
      const i = (y * size + x) * 4;
      if (!inRoundedRect(x + 0.5, y + 0.5)) {
        rgba[i + 3] = 0; // transparent outside the rounded square
        continue;
      }
      const u = x / (size - 1);
      const v = y / (size - 1);

      const stem = Math.abs(u - 0.5) < 0.075 && v > 0.24 && v < 0.55;
      const head = v >= 0.5 && v <= 0.74 && Math.abs(u - 0.5) < (0.74 - v) * 0.95;
      const tray = v > 0.80 && v < 0.875 && Math.abs(u - 0.5) < 0.26;

      let col = ACC;
      if (stem || head || tray) col = [0xff, 0xff, 0xff];

      rgba[i] = col[0];
      rgba[i + 1] = col[1];
      rgba[i + 2] = col[2];
      rgba[i + 3] = 0xff;
    }
  }
  return encodePng(size, size, rgba);
}

/** Generate source icons into src/icons (idempotent — only writes if missing). */
function ensureIcons() {
  fs.mkdirSync(ICONS_DIR, { recursive: true });
  for (const size of ICON_SIZES) {
    const file = path.join(ICONS_DIR, `icon-${size}.png`);
    if (!fs.existsSync(file)) {
      fs.writeFileSync(file, drawIcon(size));
      console.log(`  generated icons/icon-${size}.png`);
    }
  }
}

/* -------------------------------------------------------------- building */

function buildTarget(target) {
  const outDir = path.join(DIST, target);
  rmrf(outDir);
  fs.mkdirSync(path.join(outDir, "icons"), { recursive: true });

  // Merge manifest: base + per-browser override.
  const base = readJson(path.join(SRC, "manifest.base.json"));
  const override = readJson(path.join(SRC, `manifest.${target}.json`));
  const manifest = deepMerge(base, override);

  if (manifest.manifest_version !== 3) {
    throw new Error(`${target}: manifest_version must be 3`);
  }
  if (target === "firefox" && !manifest.browser_specific_settings?.gecko?.id) {
    throw new Error("firefox: missing browser_specific_settings.gecko.id");
  }

  fs.writeFileSync(
    path.join(outDir, "manifest.json"),
    JSON.stringify(manifest, null, 2) + "\n",
  );

  for (const asset of SHARED_ASSETS) {
    fs.copyFileSync(path.join(SRC, asset), path.join(outDir, asset));
  }
  for (const size of ICON_SIZES) {
    const name = `icon-${size}.png`;
    fs.copyFileSync(path.join(ICONS_DIR, name), path.join(outDir, "icons", name));
  }

  console.log(`  built dist/${target}/ (manifest_version 3${target === "firefox" ? ", gecko id present" : ""})`);
}

/* ------------------------------------------------------------------ main */

function main() {
  const args = process.argv.slice(2);
  if (args.includes("--clean")) {
    rmrf(DIST);
    console.log("cleaned dist/");
    return;
  }

  const targets = args.filter((a) => !a.startsWith("--"));
  const selected = targets.length ? targets : TARGETS;
  for (const t of selected) {
    if (!TARGETS.includes(t)) {
      throw new Error(`unknown target "${t}" (valid: ${TARGETS.join(", ")})`);
    }
  }

  console.log("JustDownload extension build");
  ensureIcons();
  for (const t of selected) {
    buildTarget(t);
  }
  console.log(`done → ${selected.length} target(s): ${selected.join(", ")}`);
}

main();
