#!/usr/bin/env node
/*
 * JustDownload Windows app-icon generator (TASK-222) — zero dependencies.
 *
 * Packs the same brand mark (extension/src/icon.svg, rendered by extension/scripts/gen-icons.js — the
 * single source of truth for the geometry/gradient, already reused by build/linux/gen-app-icon.js) into a
 * multi-resolution .ico and a standalone PNG logo for Windows packaging:
 *   - icon.ico:  JustDownload.App.csproj's ApplicationIcon (exe resource → titlebar/taskbar/Explorer) and
 *                the Avalonia Window.Icon (titlebar + the tray icon, which falls back to it), and the
 *                installer's Product.wxs ARPPRODUCTICON + Bundle.wxs IconSourceFile (Setup.exe icon).
 *   - logo.png:  the WixStdBA wizard-page corner logo (Bundle.wxs LogoFile).
 *
 * ICO frames are PNG-compressed (supported by every consumer since Windows Vista), so this just packs the
 * same renderRgba/encodePng output the other platforms already use, at each requested size.
 *
 * Usage:
 *   node build/windows/gen-app-icon.js          (re)generate build/windows/icon.ico + logo.png
 */
"use strict";

const fs = require("node:fs");
const path = require("node:path");
const { renderRgba, encodePng } = require("../../extension/scripts/gen-icons.js");

const OUT_DIR = __dirname;
const ICO_SIZES = [16, 32, 48, 64, 128, 256];
const LOGO_SIZE = 32; // WixStdBA wizard-page corner logo (rtfLicense theme)

/** Pack PNG-compressed frames into a valid multi-resolution ICO container. */
function buildIco(sizes) {
  const images = sizes.map((size) => encodePng(size, size, renderRgba(size)));

  const headerSize = 6 + 16 * images.length;
  const header = Buffer.alloc(headerSize);
  header.writeUInt16LE(0, 0); // reserved
  header.writeUInt16LE(1, 2); // type: icon
  header.writeUInt16LE(images.length, 4);

  let offset = headerSize;
  images.forEach((png, i) => {
    const size = sizes[i];
    const entry = 6 + i * 16;
    const dim = size >= 256 ? 0 : size; // ICO encodes 256 as 0
    header.writeUInt8(dim, entry); // width
    header.writeUInt8(dim, entry + 1); // height
    header.writeUInt8(0, entry + 2); // color count (0 = true color)
    header.writeUInt8(0, entry + 3); // reserved
    header.writeUInt16LE(1, entry + 4); // color planes
    header.writeUInt16LE(32, entry + 6); // bits per pixel
    header.writeUInt32LE(png.length, entry + 8); // image data size
    header.writeUInt32LE(offset, entry + 12); // image data offset
    offset += png.length;
  });

  return Buffer.concat([header, ...images]);
}

function generateAll() {
  const icoFile = path.join(OUT_DIR, "icon.ico");
  const logoFile = path.join(OUT_DIR, "logo.png");
  fs.writeFileSync(icoFile, buildIco(ICO_SIZES));
  fs.writeFileSync(logoFile, encodePng(LOGO_SIZE, LOGO_SIZE, renderRgba(LOGO_SIZE)));
  return [icoFile, logoFile];
}

module.exports = { generateAll, ICO_SIZES, LOGO_SIZE };

if (require.main === module) {
  const written = generateAll();
  console.log(`generated: ${written.map((f) => path.basename(f)).join(", ")} in ${OUT_DIR}`);
}
