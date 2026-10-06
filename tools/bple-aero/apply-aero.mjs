#!/usr/bin/env node
// Writes the extracted mirror / wing / tail values of the 17 FlipVertically parts into
// content/parts.json, preserving the file's hand-authored formatting everywhere else. Companion to
// extract-aero.mjs; the report is the ONLY admissible source for these numbers
// (docs/specs/part-mirror.md section 3).
//
// Written per part:
//   capabilities.mirror = true          -- original `BasePart.m_autoAlign == FlipVertically` (2),
//                                          i.e. the build pose has a handedness (a 180-degree turn
//                                          about the part's own up axis, BasePart.cs:639-651).
//                                          Written only when true: absent means false, the repo's
//                                          "absent = the original's default" convention.
//   capabilities.wing = { liftConstant } -- original `Wings.liftConstant` (Wings.cs:6,104-118),
//                                          REPLACING the invented `{ liftCoef, maxLift }`.
//   capabilities.tail = <num>            -- the original `Tail.liftConstant` (Tail.cs:6,57-75),
//                                          same key as the old drag coefficient, new meaning.
//
// `dragConstant` from the report is deliberately NOT written: `Wings.FixedUpdate` never reads it
// (Wings.cs:104-118) and `Tail` has no such field, so content would only gain a second source of
// truth (docs/specs/part-mirror.md sections 3 and 6.4).
//
// Key order: the rest of the capabilities object keeps its authored order; `mirror` lands in the
// slot `wing`/`tail` occupied -- immediately before it -- so the diff stays one line per part.
//
// Usage:
//   node tools/bple-aero/apply-aero.mjs [--report <file>] [--content <file>] [--dry-run]

import { readFileSync, writeFileSync } from "node:fs";
import { dirname, join, resolve } from "node:path";
import { fileURLToPath } from "node:url";

const HERE = dirname(fileURLToPath(import.meta.url));
const REPO = resolve(HERE, "..", "..");

function arg(name, fallback) {
  const index = process.argv.indexOf(`--${name}`);
  return index >= 0 && process.argv[index + 1] ? process.argv[index + 1] : fallback;
}

const REPORT = resolve(arg("report", join(REPO, "tasks", "bple-aero-report.json")));
const CONTENT = resolve(arg("content", join(REPO, "content", "parts.json")));
const DRY_RUN = process.argv.includes("--dry-run");

const report = JSON.parse(readFileSync(REPORT, "utf8"));
const parts = report.parts ?? {};
if (Object.keys(parts).length === 0) {
  throw new Error(`${REPORT} carries no parts`);
}

const round6 = (value) => Number(value.toFixed(6));

/** The values the report declares for one part; the report is the only admissible source, so a
 * missing or malformed entry is a hard error rather than a silent fallback. */
function aeroOf(partTypeId) {
  const entry = parts[partTypeId];
  if (!entry) {
    return null;
  }

  if (typeof entry.mirror !== "boolean") {
    throw new Error(`report part ${partTypeId}: mirror must be a boolean`);
  }

  const hasWing = entry.wing !== undefined && entry.wing !== null;
  const hasTail = entry.tail !== undefined && entry.tail !== null;
  if (hasWing === hasTail) {
    throw new Error(`report part ${partTypeId}: exactly one of wing/tail is required`);
  }

  if (hasWing && (typeof entry.wing.liftConstant !== "number" || !Number.isFinite(entry.wing.liftConstant))) {
    throw new Error(`report part ${partTypeId}: wing.liftConstant must be a finite number`);
  }

  if (hasTail && (typeof entry.tail !== "number" || !Number.isFinite(entry.tail))) {
    throw new Error(`report part ${partTypeId}: tail must be a finite number`);
  }

  return entry;
}

/** JSON with at most six decimals, and an explicit `.0` for an integral value -- the same style
 * the hand-authored content uses (`4.0`, `1.0`), so an extracted float never looks like an int
 * (this is why the metal tail's 1 comes back out as `1.0`). */
const num = (value) => {
  const rounded = Number(value.toFixed(6));
  return Number.isInteger(rounded) ? `${rounded}.0` : String(rounded);
};

const renderWing = (wing) => `{ "liftConstant": ${num(wing.liftConstant)} }`;

const renderValue = (value) =>
  typeof value === "string" ? `"${value}"` : typeof value === "object" && value !== null ? JSON.stringify(value) : String(value);
const renderCapabilities = (object) => `{ ${Object.entries(object)
  .map(([key, value]) => `"${key}": ${key === "wing" ? renderWing(value) : key === "tail" ? num(value) : renderValue(value)}`)
  .join(", ")} }`;

/** Semantic form of one capabilities object, so "already applied" is a value comparison and not a
 * text one: `wing` is reduced to the one field this tool owns. */
const wingSignature = (wing) =>
  wing !== null && typeof wing === "object" && typeof wing.liftConstant === "number" ? round6(wing.liftConstant) : null;
const canonical = (capabilities) =>
  JSON.stringify(Object.entries(capabilities).map(([key, value]) => [key, key === "wing"
    ? wingSignature(value)
    : key === "tail" && typeof value === "number" ? round6(value) : value]));

/** The rewritten capabilities of one part: the report's `mirror` / `wing` / `tail`, with the
 * invented `wing` object replaced. Key order is preserved so the diff stays a single line. */
function rewriteCapabilities(partTypeId, capabilities) {
  const entry = aeroOf(partTypeId);
  if (entry === null) {
    return null;
  }

  const next = {};
  let mirrorPlaced = false;
  let aeroPlaced = false;

  const placeMirror = () => {
    if (mirrorPlaced) {
      return;
    }

    if (entry.mirror) {
      next.mirror = true;
    }

    mirrorPlaced = true;
  };

  const placeAero = () => {
    if (aeroPlaced) {
      return;
    }

    if (entry.wing !== undefined && entry.wing !== null) {
      next.wing = { liftConstant: round6(entry.wing.liftConstant) };
    } else {
      next.tail = round6(entry.tail);
    }

    aeroPlaced = true;
  };

  for (const [key, value] of Object.entries(capabilities)) {
    if (key === "mirror") {
      placeMirror();
      continue;
    }

    if (key === "wing" || key === "tail") {
      placeMirror();
      placeAero();
      continue;
    }

    next[key] = value;
  }

  placeMirror();
  placeAero();
  return next;
}

const isSameCapabilities = (left, right) => canonical(left) === canonical(right);

let text = readFileSync(CONTENT, "utf8");
const lines = text.split("\n");

let updated = 0;
let unchanged = 0;
let partTypeId = null;
const rewritten = new Map();

for (let index = 0; index < lines.length; index++) {
  const partMatch = /^\s*"partTypeId":\s*(\d+),\s*$/.exec(lines[index]);
  if (partMatch) {
    partTypeId = Number(partMatch[1]);
    continue;
  }

  const capabilitiesMatch = /^(\s*)"capabilities":\s*(\{.*\}),\s*$/.exec(lines[index]);
  if (!capabilitiesMatch || partTypeId === null) {
    continue;
  }

  const current = JSON.parse(capabilitiesMatch[2]);
  const next = rewriteCapabilities(partTypeId, current);
  partTypeId = null;
  if (next === null) {
    continue;
  }

  if (isSameCapabilities(current, next)) {
    unchanged++;
    continue;
  }

  lines[index] = `${capabilitiesMatch[1]}"capabilities": ${renderCapabilities(next)},`;
  updated++;
}

text = lines.join("\n");

// Re-parse and re-derive: the rewrite must be valid JSON, match the report exactly, and be
// idempotent (a second pass changes nothing).
const check = JSON.parse(text);
const expected = new Set(Object.keys(parts).map(Number));
for (const part of check.parts) {
  if (!expected.has(part.partTypeId)) {
    continue;
  }

  const entry = aeroOf(part.partTypeId);
  const capabilities = part.capabilities ?? {};
  if (entry.mirror && capabilities.mirror !== true) {
    throw new Error(`part ${part.partTypeId}: capabilities.mirror was not written`);
  }

  if (entry.wing !== undefined && entry.wing !== null) {
    if (capabilities.wing === undefined) {
      throw new Error(`part ${part.partTypeId}: capabilities.wing was not written`);
    }

    if (capabilities.wing.liftConstant !== Number(num(entry.wing.liftConstant))) {
      throw new Error(`part ${part.partTypeId}: written wing.liftConstant does not match the report`);
    }

    if (capabilities.wing.liftCoef !== undefined || capabilities.wing.maxLift !== undefined) {
      throw new Error(`part ${part.partTypeId}: the replaced wing keys are still present`);
    }
  } else {
    if (capabilities.tail !== Number(num(entry.tail))) {
      throw new Error(`part ${part.partTypeId}: written tail does not match the report`);
    }
  }

  rewritten.set(part.partTypeId, true);
}

if (rewritten.size !== expected.size) {
  throw new Error(`aero capabilities written for ${rewritten.size} parts, expected ${expected.size}`);
}

if (!DRY_RUN) {
  writeFileSync(CONTENT, text);
}

console.log(`${DRY_RUN ? "would update" : "updated"} ${updated} parts in ${CONTENT}`);
console.log(`already matching: ${unchanged}; verified: ${rewritten.size}`);
