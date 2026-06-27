#!/usr/bin/env node
/*
 * JustDownload extension release packager (TASK-086) — zero dependencies.
 *
 * Zips each built dist/<browser>/ into a store-uploadable dist/<browser>.zip
 * using a minimal, standards-compliant ZIP writer (local headers + central
 * directory + EOCD, DEFLATE method) built on Node's zlib. Deterministic: a
 * fixed timestamp is used so identical inputs produce identical archives.
 *
 * Usage:
 *   node scripts/zip.js                 zip every dist/<browser>/ found
 *   node scripts/zip.js chrome firefox  zip a subset
 */
"use strict";

const fs = require("node:fs");
const path = require("node:path");
const zlib = require("node:zlib");

const DIST = path.join(__dirname, "..", "dist");
const TARGETS = ["chrome", "edge", "firefox"];

// Fixed MS-DOS date/time (2021-01-01 00:00:00) → reproducible archives.
const DOS_TIME = 0;
const DOS_DATE = ((2021 - 1980) << 9) | (1 << 5) | 1;

/** Recursively collect files under `dir` as { name (posix-relative), data }. */
function collect(dir, base = dir) {
  const out = [];
  for (const entry of fs.readdirSync(dir, { withFileTypes: true })) {
    const full = path.join(dir, entry.name);
    if (entry.isDirectory()) {
      out.push(...collect(full, base));
    } else if (entry.isFile()) {
      const name = path.relative(base, full).split(path.sep).join("/");
      out.push({ name, data: fs.readFileSync(full) });
    }
  }
  return out.sort((a, b) => (a.name < b.name ? -1 : a.name > b.name ? 1 : 0));
}

function zipDirectory(srcDir, outFile) {
  const files = collect(srcDir);
  const localChunks = [];
  const centralChunks = [];
  let offset = 0;

  for (const file of files) {
    const nameBuf = Buffer.from(file.name, "utf8");
    const crc = zlib.crc32(file.data) >>> 0;
    const compressed = zlib.deflateRawSync(file.data, { level: 9 });

    const local = Buffer.alloc(30);
    local.writeUInt32LE(0x04034b50, 0); // local file header signature
    local.writeUInt16LE(20, 4); // version needed
    local.writeUInt16LE(0, 6); // flags
    local.writeUInt16LE(8, 8); // method: deflate
    local.writeUInt16LE(DOS_TIME, 10);
    local.writeUInt16LE(DOS_DATE, 12);
    local.writeUInt32LE(crc, 14);
    local.writeUInt32LE(compressed.length, 18);
    local.writeUInt32LE(file.data.length, 22);
    local.writeUInt16LE(nameBuf.length, 26);
    local.writeUInt16LE(0, 28); // extra length
    localChunks.push(local, nameBuf, compressed);

    const central = Buffer.alloc(46);
    central.writeUInt32LE(0x02014b50, 0); // central directory header signature
    central.writeUInt16LE(20, 4); // version made by
    central.writeUInt16LE(20, 6); // version needed
    central.writeUInt16LE(0, 8); // flags
    central.writeUInt16LE(8, 10); // method
    central.writeUInt16LE(DOS_TIME, 12);
    central.writeUInt16LE(DOS_DATE, 14);
    central.writeUInt32LE(crc, 16);
    central.writeUInt32LE(compressed.length, 20);
    central.writeUInt32LE(file.data.length, 24);
    central.writeUInt16LE(nameBuf.length, 28);
    central.writeUInt16LE(0, 30); // extra length
    central.writeUInt16LE(0, 32); // comment length
    central.writeUInt16LE(0, 34); // disk number start
    central.writeUInt16LE(0, 36); // internal attributes
    central.writeUInt32LE(0, 38); // external attributes
    central.writeUInt32LE(offset, 42); // local header offset
    centralChunks.push(central, nameBuf);

    offset += local.length + nameBuf.length + compressed.length;
  }

  const localBuf = Buffer.concat(localChunks);
  const centralBuf = Buffer.concat(centralChunks);

  const eocd = Buffer.alloc(22);
  eocd.writeUInt32LE(0x06054b50, 0); // end of central directory signature
  eocd.writeUInt16LE(0, 4); // this disk
  eocd.writeUInt16LE(0, 6); // disk with central directory
  eocd.writeUInt16LE(files.length, 8);
  eocd.writeUInt16LE(files.length, 10);
  eocd.writeUInt32LE(centralBuf.length, 12);
  eocd.writeUInt32LE(localBuf.length, 16); // central directory offset
  eocd.writeUInt16LE(0, 20); // comment length

  fs.writeFileSync(outFile, Buffer.concat([localBuf, centralBuf, eocd]));
  return { files: files.length, bytes: localBuf.length + centralBuf.length + eocd.length };
}

function main() {
  const args = process.argv.slice(2).filter((a) => !a.startsWith("--"));
  const selected = args.length ? args : TARGETS;

  for (const target of selected) {
    if (!TARGETS.includes(target)) {
      throw new Error(`unknown target "${target}" (valid: ${TARGETS.join(", ")})`);
    }
    const srcDir = path.join(DIST, target);
    if (!fs.existsSync(srcDir)) {
      throw new Error(`dist/${target}/ not found — run "npm run build" first`);
    }
    const outFile = path.join(DIST, `${target}.zip`);
    const { files, bytes } = zipDirectory(srcDir, outFile);
    console.log(`  packaged dist/${target}.zip (${files} files, ${bytes} bytes)`);
  }
  console.log(`done → ${selected.length} archive(s)`);
}

if (require.main === module) {
  main();
}

module.exports = { zipDirectory };
