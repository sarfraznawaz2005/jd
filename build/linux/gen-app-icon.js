#!/usr/bin/env node
/*
 * JustDownload Linux app-icon generator (TASK-078) — zero dependencies.
 *
 * Renders the same brand mark as the browser extension (extension/src/icon.svg, TASK-086) to the PNG
 * sizes Linux packaging needs: the freedesktop hicolor icon theme sizes (16/32/48/64/128/256) plus the
 * 256x256 root icon AppImage requires. Reuses the extension's anti-aliased rasterizer instead of
 * duplicating it — see extension/scripts/gen-icons.js for the geometry/gradient that is the single
 * source of truth for the mark.
 *
 * Usage:
 *   node build/linux/gen-app-icon.js          (re)generate all sizes into build/linux/icons/
 */
"use strict";

const fs = require("node:fs");
const path = require("node:path");
const { renderRgba, encodePng } = require("../../extension/scripts/gen-icons.js");

const OUT_DIR = path.join(__dirname, "icons");
const SIZES = [16, 32, 48, 64, 128, 256];

function generateAll() {
  fs.mkdirSync(OUT_DIR, { recursive: true });
  const written = [];
  for (const size of SIZES) {
    const file = path.join(OUT_DIR, `icon-${size}.png`);
    fs.writeFileSync(file, encodePng(size, size, renderRgba(size)));
    written.push(`icon-${size}.png`);
  }
  return written;
}

module.exports = { generateAll, SIZES };

if (require.main === module) {
  const written = generateAll();
  console.log(`generated ${written.length} icon(s) in ${OUT_DIR}: ${written.join(", ")}`);
}
