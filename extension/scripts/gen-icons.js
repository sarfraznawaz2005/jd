#!/usr/bin/env node
/*
 * JustDownload extension icon generator (TASK-086) — zero dependencies.
 *
 * Renders the brand mark (see src/icon.svg) — a rounded-square accent gradient
 * with a white download glyph — to anti-aliased PNGs at the manifest sizes. The
 * geometry mirrors the SVG master; both are the single source of truth for the
 * mark. Output goes to src/icons/icon-<size>.png and IS committed (Chrome/Edge
 * need raster icons, and committing them lets the unpacked extension load
 * without a build step).
 *
 * Usage:
 *   node scripts/gen-icons.js          (re)generate all sizes
 *   require("./scripts/gen-icons").generateAll({ force })   from build.js
 *
 * renderRgba/encodePng are also exported so other packaging steps (e.g. build/linux/gen-app-icon.js,
 * TASK-078) can render the same brand mark at their own sizes/output paths without duplicating the
 * rasterizer.
 */
"use strict";

const fs = require("node:fs");
const path = require("node:path");
const zlib = require("node:zlib");

const ICONS_DIR = path.join(__dirname, "..", "src", "icons");
const SIZES = [16, 32, 48, 128];
const SS = 4; // supersampling factor for anti-aliasing

// Gradient endpoints (#5b67d6 → #8a7bf0), matching the app/mockup logo.
const G0 = [0x5b, 0x67, 0xd6];
const G1 = [0x8a, 0x7b, 0xf0];
const WHITE = [0xff, 0xff, 0xff];
const CORNER = 0.22; // corner radius as a fraction of the icon size

/* ----------------------------------------------------------- geometry */

function insideRoundedSquare(u, v) {
  const r = CORNER;
  const cx = Math.min(Math.max(u, r), 1 - r);
  const cy = Math.min(Math.max(v, r), 1 - r);
  return (u - cx) ** 2 + (v - cy) ** 2 <= r * r;
}

// White download glyph: arrow shaft + downward head + tray, in normalized coords.
function insideGlyph(u, v) {
  const shaft = Math.abs(u - 0.5) < 0.0625 && v > 0.26 && v < 0.53;
  const head = v >= 0.45 && v <= 0.6875 && Math.abs(u - 0.5) < (0.6875 - v) * 1.0;
  const tray = v > 0.78 && v < 0.86 && Math.abs(u - 0.5) < 0.22;
  return shaft || head || tray;
}

function sample(u, v) {
  if (!insideRoundedSquare(u, v)) {
    return [0, 0, 0, 0];
  }
  if (insideGlyph(u, v)) {
    return [WHITE[0], WHITE[1], WHITE[2], 255];
  }
  const t = Math.min(Math.max((u + v) / 2, 0), 1);
  return [
    Math.round(G0[0] + (G1[0] - G0[0]) * t),
    Math.round(G0[1] + (G1[1] - G0[1]) * t),
    Math.round(G0[2] + (G1[2] - G0[2]) * t),
    255,
  ];
}

/* ----------------------------------------------------------- rendering */

function renderRgba(size) {
  const rgba = Buffer.alloc(size * size * 4);
  for (let y = 0; y < size; y++) {
    for (let x = 0; x < size; x++) {
      // Premultiplied average of SS*SS subpixels → correct edge anti-aliasing.
      let sumA = 0;
      let sumR = 0;
      let sumG = 0;
      let sumB = 0;
      for (let sy = 0; sy < SS; sy++) {
        for (let sx = 0; sx < SS; sx++) {
          const u = (x + (sx + 0.5) / SS) / size;
          const v = (y + (sy + 0.5) / SS) / size;
          const [r, g, b, a] = sample(u, v);
          sumA += a;
          sumR += r * a;
          sumG += g * a;
          sumB += b * a;
        }
      }
      const n = SS * SS;
      const i = (y * size + x) * 4;
      const a = Math.round(sumA / n);
      rgba[i] = sumA > 0 ? Math.round(sumR / sumA) : 0;
      rgba[i + 1] = sumA > 0 ? Math.round(sumG / sumA) : 0;
      rgba[i + 2] = sumA > 0 ? Math.round(sumB / sumA) : 0;
      rgba[i + 3] = a;
    }
  }
  return rgba;
}

/** Encode raw RGBA bytes into a PNG buffer (Node zlib + crc32, no deps). */
function encodePng(width, height, rgba) {
  const sig = Buffer.from([0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a]);

  const ihdr = Buffer.alloc(13);
  ihdr.writeUInt32BE(width, 0);
  ihdr.writeUInt32BE(height, 4);
  ihdr[8] = 8; // bit depth
  ihdr[9] = 6; // color type: RGBA

  const stride = width * 4;
  const raw = Buffer.alloc((stride + 1) * height);
  for (let y = 0; y < height; y++) {
    raw[y * (stride + 1)] = 0; // filter: none
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

/**
 * Generate the icon PNGs. With `force` false, only missing files are written
 * (so a build never churns committed icons); with `force` true, all are rewritten.
 */
function generateAll({ force = false } = {}) {
  fs.mkdirSync(ICONS_DIR, { recursive: true });
  const written = [];
  for (const size of SIZES) {
    const file = path.join(ICONS_DIR, `icon-${size}.png`);
    if (force || !fs.existsSync(file)) {
      fs.writeFileSync(file, encodePng(size, size, renderRgba(size)));
      written.push(`icon-${size}.png`);
    }
  }
  return written;
}

module.exports = { generateAll, renderRgba, encodePng, SIZES };

if (require.main === module) {
  const written = generateAll({ force: true });
  console.log(`generated ${written.length} icon(s): ${written.join(", ")}`);
}
