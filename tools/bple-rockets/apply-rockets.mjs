#!/usr/bin/env node
// Writes the extracted rocket-family values of the 18 rocket prefabs into content/parts.json,
// preserving the file's hand-authored formatting everywhere else. Companion to extract-rockets.mjs;
// the report is the ONLY admissible source for these numbers.
//
// Written per part -- `capabilities.rocket` becomes EXACTLY this object, keys in this order:
//   { "directionX", "directionY", "thrustPerTick", "ignitionTicks", "boostTicks", "endTicks",
//     "maxSpeed", "visualization"?, "explodeRadius"?, "explodeImpulse"? }
// where
//   directionX/directionY   the prefab's `m_direction` (Rocket.cs:9), asserted (1,0,0) on all 18;
//                           written as INTEGERS because the loader reads them with `TryGetInt32`
//                           (PartContentParser.TryReadRocket).
//   thrustPerTick           effective `m_boostForce` / 60 -- the ADR-013 decision 4 conversion of
//                           the `ForceMode.Force` of Rocket.cs:295-300 into one impulse per 60 Hz
//                           tick, the same divisor tools/bple-fans and tools/bple-lift use.
//   ignitionTicks           round(m_ignitionTime * 60)   -- Rocket.cs:228-300
//   boostTicks              round(m_boostDuration * 60)  -- Rocket.cs:228-300
//   endTicks                round(m_boostEndDuration * 60), the ramp-down of :283-286
//   maxSpeed                effective `m_maximumSpeed` in m/s, the cap LimitForceForSpeed applies
//                           (Rocket.cs:529-541)
//   visualization           written only when true: a `BottleVisualization` child makes the part
//                           stall for its whole ignition phase (Rocket.cs:78-83, 235-240), so the
//                           10 bottle parts carry it and the 8 rockets do not.
//   explodeRadius/          written only when the prefab has `m_explodes` (the two `*_03` prefabs,
//   explodeImpulse          radius 8 / impulse 25), Rocket.cs:627-646
// The object REPLACES the old hand-written shape, so the invented `durationTicks` and the stale
// (and partly wrong) `directionX`/`directionY` are gone -- see gap G101 in
// docs/decisions/ADR-029-part-local-content-directions.md.
//
// Key order: the rocket family's own object is rendered in the order above (that IS the contract);
// everything else in `capabilities` keeps its authored order, and `rocket` keeps the slot it
// already had, so the diff stays one line per part.
//
// Usage:
//   node tools/bple-rockets/apply-rockets.mjs [--report <file>] [--content <file>] [--dry-run]

import { readFileSync, writeFileSync } from "node:fs";
import { dirname, join, resolve } from "node:path";
import { fileURLToPath } from "node:url";

const HERE = dirname(fileURLToPath(import.meta.url));
const REPO = resolve(HERE, "..", "..");

function arg(name, fallback) {
  const index = process.argv.indexOf(`--${name}`);
  return index >= 0 && process.argv[index + 1] ? process.argv[index + 1] : fallback;
}

const REPORT = resolve(arg("report", join(REPO, "tasks", "bple-rockets-report.json")));
const CONTENT = resolve(arg("content", join(REPO, "content", "parts.json")));
const DRY_RUN = process.argv.includes("--dry-run");

const REPORT_FORMAT = "pigforge.bple-rockets.report";

/** The 18 rocket-family content parts this report is allowed to speak for (gap G101); a different
 * count means the content no longer matches the extracted world and the tool must not guess. */
const EXPECTED_PARTS = 18;

/** The required key order, and the only keys that may ever be written. */
const ROCKET_FIELDS = ["directionX", "directionY", "thrustPerTick", "ignitionTicks", "boostTicks", "endTicks", "maxSpeed"];
const OPTIONAL_FIELDS = ["visualization", "explodeRadius", "explodeImpulse"];

const report = JSON.parse(readFileSync(REPORT, "utf8"));
if (report.format !== REPORT_FORMAT) {
  throw new Error(`${REPORT}: format '${report.format}', expected '${REPORT_FORMAT}' -- regenerate with extract-rockets.mjs`);
}

const parts = report.parts ?? {};
if (Object.keys(parts).length === 0) {
  throw new Error(`${REPORT} carries no parts`);
}

const round6 = (value) => Number(value.toFixed(6));

/** The rocket object the report declares for one part; the report is the only admissible source, so
 * a missing or malformed entry is a hard error rather than a silent fallback. */
function rocketOf(partTypeId) {
  const entry = parts[partTypeId];
  if (!entry) {
    return null;
  }

  const rocket = entry.rocket;
  if (rocket === null || typeof rocket !== "object") {
    throw new Error(`report part ${partTypeId}: rocket must be an object`);
  }

  for (const key of Object.keys(rocket)) {
    if (!ROCKET_FIELDS.includes(key) && !OPTIONAL_FIELDS.includes(key)) {
      throw new Error(`report part ${partTypeId}: unexpected rocket key '${key}'`);
    }
  }

  for (const key of ROCKET_FIELDS) {
    if (!(key in rocket)) {
      throw new Error(`report part ${partTypeId}: rocket.${key} is missing`);
    }
  }

  for (const key of ["directionX", "directionY"]) {
    if (!Number.isInteger(rocket[key]) || ![1, 0, -1].includes(rocket[key])) {
      throw new Error(`report part ${partTypeId}: rocket.${key} must be -1, 0 or 1`);
    }
  }

  if (typeof rocket.thrustPerTick !== "number" || !Number.isFinite(rocket.thrustPerTick) || !(rocket.thrustPerTick > 0)) {
    throw new Error(`report part ${partTypeId}: rocket.thrustPerTick must be a finite positive number`);
  }

  for (const key of ["ignitionTicks", "boostTicks", "endTicks"]) {
    if (!Number.isInteger(rocket[key]) || rocket[key] < 0 || rocket[key] > 65535) {
      throw new Error(`report part ${partTypeId}: rocket.${key} must be an integer in [0, 65535]`);
    }
  }

  if (typeof rocket.maxSpeed !== "number" || !Number.isFinite(rocket.maxSpeed) || rocket.maxSpeed < 0) {
    throw new Error(`report part ${partTypeId}: rocket.maxSpeed must be a finite number >= 0`);
  }

  if (rocket.visualization !== undefined && rocket.visualization !== true) {
    throw new Error(`report part ${partTypeId}: rocket.visualization is written only when true`);
  }

  for (const key of ["explodeRadius", "explodeImpulse"]) {
    if (rocket[key] !== undefined && (typeof rocket[key] !== "number" || !Number.isFinite(rocket[key]) || rocket[key] < 0)) {
      throw new Error(`report part ${partTypeId}: rocket.${key} must be a finite number >= 0`);
    }
  }

  // The loader pairs them (PartContentParser.TryReadRocket): a radius without an impulse would
  // silently drop the blast this part's `m_explodes` asks for.
  if ((rocket.explodeRadius === undefined) !== (rocket.explodeImpulse === undefined)) {
    throw new Error(`report part ${partTypeId}: rocket.explodeRadius and rocket.explodeImpulse must be written together`);
  }

  if (rocket.directionX === 0 && rocket.directionY === 0) {
    throw new Error(`report part ${partTypeId}: rocket direction is the zero vector`);
  }

  return rocket;
}

/** JSON with at most six decimals, and an explicit `.0` for an integral value -- the same style the
 * hand-authored content uses (`4.0`, `1.0`), so an extracted float never looks like an int. */
const num = (value) => {
  const rounded = Number(value.toFixed(6));
  return Number.isInteger(rounded) ? `${rounded}.0` : String(rounded);
};

/** The exact key order of the written object, which IS the contract. */
const rocketKeys = (rocket) => [
  ...ROCKET_FIELDS,
  ...(rocket.visualization === true ? ["visualization"] : []),
  ...(rocket.explodeRadius !== undefined ? ["explodeRadius", "explodeImpulse"] : []),
];

/** The rocket object as one line, keys in `rocketKeys` order. The direction is a plain integer
 * (the loader reads it with `TryGetInt32`); the tick counts are plain integers; `thrustPerTick` and
 * `maxSpeed` -- and a blast's radius/impulse -- go through `num`, so `18.0` never looks like a tick. */
const renderRocket = (rocket) => {
  const rendered = {
    directionX: String(rocket.directionX),
    directionY: String(rocket.directionY),
    thrustPerTick: num(rocket.thrustPerTick),
    ignitionTicks: String(rocket.ignitionTicks),
    boostTicks: String(rocket.boostTicks),
    endTicks: String(rocket.endTicks),
    maxSpeed: num(rocket.maxSpeed),
  };

  if (rocket.visualization === true) {
    rendered.visualization = "true";
  }

  if (rocket.explodeRadius !== undefined) {
    rendered.explodeRadius = num(rocket.explodeRadius);
    rendered.explodeImpulse = num(rocket.explodeImpulse);
  }

  return `{ ${rocketKeys(rocket).map((key) => `"${key}": ${rendered[key]}`).join(", ")} }`;
};

const renderValue = (value) =>
  typeof value === "string" ? `"${value}"` : typeof value === "object" && value !== null ? JSON.stringify(value) : String(value);
const renderCapabilities = (object) => `{ ${Object.entries(object)
  .map(([key, value]) => `"${key}": ${key === "rocket" ? renderRocket(value) : renderValue(value)}`)
  .join(", ")} }`;

/** A finite number rounded to six decimals, or null for anything else -- including the fields the
 * old hand-written shape does not have, so a stale `{ thrustPerTick, directionX, durationTicks }`
 * never compares equal to an extracted one. */
const numberOrNull = (value) => (typeof value === "number" && Number.isFinite(value) ? round6(value) : null);

/** Semantic form of one capabilities object, so "already applied" is a value comparison and not a
 * text one: `rocket` is reduced to the ten fields this tool owns. */
const rocketSignature = (rocket) => ({
  directionX: rocket.directionX ?? null,
  directionY: rocket.directionY ?? null,
  thrustPerTick: numberOrNull(rocket.thrustPerTick),
  ignitionTicks: rocket.ignitionTicks ?? null,
  boostTicks: rocket.boostTicks ?? null,
  endTicks: rocket.endTicks ?? null,
  maxSpeed: numberOrNull(rocket.maxSpeed),
  visualization: rocket.visualization === true,
  explodeRadius: numberOrNull(rocket.explodeRadius),
  explodeImpulse: numberOrNull(rocket.explodeImpulse),
});
const canonical = (capabilities) =>
  JSON.stringify(Object.entries(capabilities).map(([key, value]) => [key, key === "rocket" && value !== null && typeof value === "object" ? rocketSignature(value) : value]));

/** The rewritten capabilities of one part: the report's `rocket` object in the slot the old one
 * occupied (or just before `activation`), everything else untouched and in place. */
function rewriteCapabilities(partTypeId, capabilities) {
  const rocket = rocketOf(partTypeId);
  if (rocket === null) {
    return null;
  }

  const next = {};
  let placed = false;
  const placeRocket = () => {
    if (!placed) {
      next.rocket = rocket;
      placed = true;
    }
  };

  for (const [key, value] of Object.entries(capabilities)) {
    if (key === "rocket") {
      placeRocket();
      continue;
    }

    if (key === "activation") {
      placeRocket();
    }

    next[key] = value;
  }

  placeRocket();
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

// Re-parse and re-derive: the rewrite must be valid JSON, carry exactly the report's rocket object
// with exactly the required keys in order, leave no invented key behind, and be complete.
const check = JSON.parse(text);
const expected = new Set(Object.keys(parts).map(Number));
for (const part of check.parts) {
  if (!expected.has(part.partTypeId)) {
    continue;
  }

  const rocket = rocketOf(part.partTypeId);
  const written = part.capabilities?.rocket;
  if (written === undefined) {
    throw new Error(`part ${part.partTypeId}: capabilities.rocket was not written`);
  }

  if (written.durationTicks !== undefined) {
    throw new Error(`part ${part.partTypeId}: the replaced rocket key durationTicks is still present`);
  }

  const keys = Object.keys(written);
  const wanted = rocketKeys(rocket);
  if (keys.join(", ") !== wanted.join(", ")) {
    throw new Error(`part ${part.partTypeId}: written rocket keys ${keys.join(", ")}, expected ${wanted.join(", ")}`);
  }

  for (const key of ["directionX", "directionY", "ignitionTicks", "boostTicks", "endTicks"]) {
    if (written[key] !== rocket[key]) {
      throw new Error(`part ${part.partTypeId}: written ${key} ${written[key]} does not match the report`);
    }
  }

  for (const key of ["thrustPerTick", "maxSpeed", "explodeRadius", "explodeImpulse"]) {
    if (rocket[key] !== undefined && written[key] !== round6(rocket[key])) {
      throw new Error(`part ${part.partTypeId}: written ${key} ${written[key]} does not match the report`);
    }
  }

  if ((rocket.visualization === true) !== (written.visualization === true)) {
    throw new Error(`part ${part.partTypeId}: written visualization does not match the report`);
  }

  rewritten.set(part.partTypeId, true);
}

if (rewritten.size !== expected.size) {
  throw new Error(`rocket capabilities written for ${rewritten.size} parts, expected ${expected.size}`);
}

if (expected.size !== EXPECTED_PARTS) {
  throw new Error(`${REPORT} carries ${expected.size} parts, expected ${EXPECTED_PARTS} (gap G101)`);
}

if (!DRY_RUN) {
  writeFileSync(CONTENT, text);
}

console.log(`${DRY_RUN ? "would update" : "updated"} ${updated} parts in ${CONTENT}`);
console.log(`already matching: ${unchanged}; verified: ${rewritten.size}`);
