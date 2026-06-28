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
const { generateAll } = require("./scripts/gen-icons");

const ROOT = __dirname;
const SRC = path.join(ROOT, "src");
const ICONS_DIR = path.join(SRC, "icons");
const DIST = path.join(ROOT, "dist");

const TARGETS = ["chrome", "edge", "firefox"];
const ICON_SIZES = [16, 32, 48, 128];

// Shared assets copied verbatim into every browser bundle.
const SHARED_ASSETS = [
  "jdcore.js", "background.js", "content.js",
  "popup.html", "popup.css", "popup.js",
  "options.html", "options.js",
];

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
  // Committed brand icons (src/icons) are the source of truth; only generate if a clean
  // checkout is missing them. Run `npm run gen-icons` to deliberately regenerate.
  const generated = generateAll({ force: false });
  if (generated.length) {
    console.log(`  generated missing icons: ${generated.join(", ")}`);
  }
  for (const t of selected) {
    buildTarget(t);
  }
  console.log(`done → ${selected.length} target(s): ${selected.join(", ")}`);
}

main();
