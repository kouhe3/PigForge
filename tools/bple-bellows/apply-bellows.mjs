#!/usr/bin/env node
// Writes the extracted bellows-family values of the 8 Bellows prefabs into content/parts.json,
// preserving the file's hand-authored formatting everywhere else. Companion to extract-bellows.mjs;
// the report is the ONLY admissible source for these numbers.
//
// Written per part -- `capabilities.bellows` becomes EXACTLY this object, keys in this order:
//   { "directionX", "directionY", "thrustPerTick", "inflateTicks" }
// where
//   directionX/directionY   the prefab's `m_direction` (Bellows.cs:5), asserted (1,0,0) on all 8;
//                           written as INTEGERS because the loader reads them with `TryGetInt32`
//                           (PartContentParser.TryReadBellows).
//   thrustPerTick           `m_boostForce` / 60 -- the ADR-013 decision 4 conversion of the
//                           per-second `ForceMode.Force` of Bellows.cs:105-110 into one impulse per
//                           60 Hz tick, the same divisor tools/bple-rockets, tools/bple-fans and
//                           tools/bple-lift use. 0.5 normally, 2.0 on the alien skin.
//   inflateTicks            round(InflateDuration * 60) -- Bellows.cs:36-46: 18 normally, 9 on the
//                           skin whose `m_alienBellow` is 1 (`Part_Bellows_07_SET`).
//
// The 0.5 s boost and the 0.3 s wait are CLASS CONSTANTS of Bellows.cs (BOOST_DURATION /
// WAIT_DURATION, `Bellows.cs:14-15`) shared by every skin, so they travel in NO content field: the
// rules own them (GameplayRules.RunBellows). The object REPLACES the old hand-written scalar, which
// was a whole-puff total impulse (`"bellows": 8.0`, 32.0 on v07) -- see gap G98 in
// tasks/original-vs-implemented.md.
//
// Key order: the bellows family's own object is rendered in the order above (that IS the contract);
// everything else in `capabilities` keeps its authored order, and `bellows` keeps the slot it
// already had, so the diff stays one line per part.
//
// Usage:
//   node tools/bple-bellows/apply-bellows.mjs [--report <file>] [--content <file>] [--dry-run]

import { readFileSync, writeFileSync } from "node:fs";
import { dirname, join, resolve } from "node:path";
import { fileURLToPath } from "node:url";

const HERE = dirname(fileURLToPath(import.meta.url));
const REPO = resolve(HERE, "..", "..");

function arg(name, fallback) {
  const index = process.argv.indexOf(`--${name}`);
  return index >= 0 && process.argv[index + 1] ? process.argv[index + 1] : fallback;
}

const REPORT = resolve(arg("report", join(REPO, "tasks", "bple-bellows-report.json")));
const CONTENT = resolve(arg("content", join(REPO, "content", "parts.json")));
const DRY_RUN = process.argv.includes("--dry-run");

const REPORT_FORMAT = "pigforge.bple-bellows.report";

/** The 8 bellows-family content parts this report is allowed to speak for (gap G98, content ids
 * 40 and 88..94); a different count means the content no longer matches the extracted world and
 * the tool must not guess. */
const EXPECTED_PARTS = 8;

/** The required key order, and the only keys that may ever be written. */
const BELLOWS_FIELDS = ["directionX", "directionY", "thrustPerTick", "inflateTicks"];

const report = JSON.parse(readFileSync(REPORT, "utf8"));
if (report.format !== REPORT_FORMAT) {
  throw new Error(`${REPORT}: format '${report.format}', expected '${REPORT_FORMAT}' -- regenerate with extract-bellows.mjs`);
}

const parts = report.parts ?? {};
if (Object.keys(parts).length === 0) {
  throw new Error(`${REPORT} carries no parts`);
}

const round6 = (value) => Number(value.toFixed(6));

/** The bellows object the report declares for one part; the report is the only admissible source,
 * so a missing or malformed entry is a hard error rather than a silent fallback. */
function bellowsOf(partTypeId) {
  const entry = parts[partTypeId];
  if (!entry) {
    return null;
  }

  const bellows = entry.bellows;
  if (bellows === null || typeof bellows !== "object") {
    throw new Error(`report part ${partTypeId}: bellows must be an object`);
  }

  for (const key of Object.keys(bellows)) {
    if (!BELLOWS_FIELDS.includes(key)) {
      throw new Error(`report part ${partTypeId}: unexpected bellows key '${key}'`);
    }
  }

  for (const key of BELLOWS_FIELDS) {
    if (!(key in bellows)) {
      throw new Error(`report part ${partTypeId}: bellows.${key} is missing`);
    }
  }

  for (const key of ["directionX", "directionY"]) {
    if (!Number.isInteger(bellows[key]) || ![1, 0, -1].includes(bellows[key])) {
      throw new Error(`report part ${partTypeId}: bellows.${key} must be -1, 0 or 1`);
    }
  }

  if (typeof bellows.thrustPerTick !== "number" || !Number.isFinite(bellows.thrustPerTick) || !(bellows.thrustPerTick > 0)) {
    throw new Error(`report part ${partTypeId}: bellows.thrustPerTick must be a finite positive number`);
  }

  if (!Number.isInteger(bellows.inflateTicks) || bellows.inflateTicks < 0 || bellows.inflateTicks > 65535) {
    throw new Error(`report part ${partTypeId}: bellows.inflateTicks must be an integer in [0, 65535]`);
  }

  if (bellows.directionX === 0 && bellows.directionY === 0) {
    throw new Error(`report part ${partTypeId}: bellows direction is the zero vector`);
  }

  return bellows;
}

/** JSON with at most six decimals, and an explicit `.0` for an integral value -- the same style the
 * hand-authored content uses (`4.0`, `1.0`), so an extracted float never looks like an int. */
const num = (value) => {
  const rounded = Number(value.toFixed(6));
  return Number.isInteger(rounded) ? `${rounded}.0` : String(rounded);
};

/** The bellows object as one line, keys in the contract order. The directions are plain integers
 * (the loader reads them with `TryGetInt32`), `inflateTicks` is a plain integer and `thrustPerTick`
 * goes through `num`, so `2.0` never looks like a tick count. */
const renderBellows = (bellows) =>
  `{ "directionX": ${bellows.directionX}, "directionY": ${bellows.directionY}, "thrustPerTick": ${num(bellows.thrustPerTick)}, "inflateTicks": ${bellows.inflateTicks} }`;

const renderValue = (value) =>
  typeof value === "string" ? `"${value}"` : typeof value === "object" && value !== null ? JSON.stringify(value) : String(value);
const renderCapabilities = (object) => `{ ${Object.entries(object)
  .map(([key, value]) => `"${key}": ${key === "bellows" ? renderBellows(value) : renderValue(value)}`)
  .join(", ")} }`;

/** A finite number rounded to six decimals, or null for anything else -- so a stale scalar
 * (`"bellows": 8.0`), which is not an object at all, never compares equal to an extracted one. */
const numberOrNull = (value) => (typeof value === "number" && Number.isFinite(value) ? round6(value) : null);

/** Semantic form of one capabilities object, so "already applied" is a value comparison and not a
 * text one: `bellows` is reduced to the four fields this tool owns. */
const bellowsSignature = (bellows) => ({
  directionX: bellows.directionX ?? null,
  directionY: bellows.directionY ?? null,
  thrustPerTick: numberOrNull(bellows.thrustPerTick),
  inflateTicks: bellows.inflateTicks ?? null,
});
const canonical = (capabilities) =>
  JSON.stringify(Object.entries(capabilities).map(([key, value]) => [key, key === "bellows" && value !== null && typeof value === "object" ? bellowsSignature(value) : value]));

/** The rewritten capabilities of one part: the report's `bellows` object in the slot the old one
 * occupied (or just before `activation`), everything else untouched and in place. */
function rewriteCapabilities(partTypeId, capabilities) {
  const bellows = bellowsOf(partTypeId);
  if (bellows === null) {
    return null;
  }

  const next = {};
  let placed = false;
  const placeBellows = () => {
    if (!placed) {
      next.bellows = bellows;
      placed = true;
    }
  };

  for (const [key, value] of Object.entries(capabilities)) {
    if (key === "bellows") {
      placeBellows();
      continue;
    }

    if (key === "activation") {
      placeBellows();
    }

    next[key] = value;
  }

  placeBellows();
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

// Re-parse and re-derive: the rewrite must be valid JSON, carry exactly the report's bellows object
// with exactly the required keys in order, leave no invented key (or the replaced scalar) behind,
// and be complete.
const check = JSON.parse(text);
const expected = new Set(Object.keys(parts).map(Number));
for (const part of check.parts) {
  if (!expected.has(part.partTypeId)) {
    continue;
  }

  const bellows = bellowsOf(part.partTypeId);
  const written = part.capabilities?.bellows;
  if (written === null || typeof written !== "object") {
    throw new Error(`part ${part.partTypeId}: capabilities.bellows was not written as an object`);
  }

  const keys = Object.keys(written);
  const wanted = BELLOWS_FIELDS;
  if (keys.join(", ") !== wanted.join(", ")) {
    throw new Error(`part ${part.partTypeId}: written bellows keys ${keys.join(", ")}, expected ${wanted.join(", ")}`);
  }

  for (const key of ["directionX", "directionY", "inflateTicks"]) {
    if (written[key] !== bellows[key]) {
      throw new Error(`part ${part.partTypeId}: written ${key} ${written[key]} does not match the report`);
    }
  }

  if (written.thrustPerTick !== round6(bellows.thrustPerTick)) {
    throw new Error(`part ${part.partTypeId}: written thrustPerTick ${written.thrustPerTick} does not match the report`);
  }

  rewritten.set(part.partTypeId, true);
}

if (rewritten.size !== expected.size) {
  throw new Error(`bellows capabilities written for ${rewritten.size} parts, expected ${expected.size}`);
}

if (expected.size !== EXPECTED_PARTS) {
  throw new Error(`${REPORT} carries ${expected.size} parts, expected ${EXPECTED_PARTS} (gap G98)`);
}

if (!DRY_RUN) {
  writeFileSync(CONTENT, text);
}

console.log(`${DRY_RUN ? "would update" : "updated"} ${updated} parts in ${CONTENT}`);
console.log(`already matching: ${unchanged}; verified: ${rewritten.size}`);
