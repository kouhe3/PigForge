#!/usr/bin/env node
// Writes the extracted FanPropeller values of the 26 fan/propeller/rotor parts into
// content/parts.json, preserving the file's hand-authored formatting everywhere else. Companion
// to extract-fans.mjs; the report is the ONLY admissible source for these numbers.
//
// Written per part (see docs/specs/fan-propeller.md):
//   capabilities.fan = { thrustPerTick, directionX, directionY, maxSpeed?, rotor? }
// and the three approximations it replaces are removed:
//   capabilities.balloon  (the rotor's unbounded lift)
//   capabilities.wheel    (the plane propellers hand-authored as driven wheels)
//   capabilities.motor    (the same)
// The propeller family is the one whose original has no speed cap, so its `fan` carries no
// `maxSpeed` at all -- `renderFan` omits the field and the rules layer then never limits it, the
// way `FanPropeller` never does when its multiplier is Infinity.
// `activation` becomes "toggle" on every one of them: the original's FanPropeller is
// `HasOnOffToggle() = true` and turning it off only stops the thrust (FanPropeller.cs:49-56,
// 265-296) -- never destroys the part, which is what the rotor's old "trigger" did.
//
// The rest of the capabilities object keeps its authored key order; `fan` takes the slot it (or
// the key it replaces) already had, otherwise it lands just before `activation`.
//
// Usage:
//   node tools/bple-fans/apply-fans.mjs [--report <file>] [--content <file>] [--dry-run]

import { readFileSync, writeFileSync } from "node:fs";
import { dirname, join, resolve } from "node:path";
import { fileURLToPath } from "node:url";

const HERE = dirname(fileURLToPath(import.meta.url));
const REPO = resolve(HERE, "..", "..");

function arg(name, fallback) {
  const index = process.argv.indexOf(`--${name}`);
  return index >= 0 && process.argv[index + 1] ? process.argv[index + 1] : fallback;
}

const REPORT = resolve(arg("report", join(REPO, "tasks", "bple-fans-report.json")));
const CONTENT = resolve(arg("content", join(REPO, "content", "parts.json")));
const DRY_RUN = process.argv.includes("--dry-run");

const report = JSON.parse(readFileSync(REPORT, "utf8"));
const parts = report.parts ?? {};
if (Object.keys(parts).length === 0) {
  throw new Error(`${REPORT} carries no parts`);
}

/** The values the report declares for one part; the report is the only admissible source, so a
 * missing or malformed entry is a hard error rather than a silent fallback. */
function fanOf(partTypeId) {
  const entry = parts[partTypeId];
  if (!entry) {
    return null;
  }

  if (typeof entry.thrustPerTick !== "number" || !Number.isFinite(entry.thrustPerTick) || !(entry.thrustPerTick > 0)) {
    throw new Error(`report part ${partTypeId}: thrustPerTick must be a finite positive number`);
  }

  if (entry.directionX !== -1 && entry.directionX !== 0 && entry.directionX !== 1) {
    throw new Error(`report part ${partTypeId}: directionX must be -1, 0 or 1`);
  }

  if (entry.directionY !== -1 && entry.directionY !== 0 && entry.directionY !== 1) {
    throw new Error(`report part ${partTypeId}: directionY must be -1, 0 or 1`);
  }

  if (entry.maxSpeed !== null && (typeof entry.maxSpeed !== "number" || !Number.isFinite(entry.maxSpeed) || !(entry.maxSpeed > 0))) {
    throw new Error(`report part ${partTypeId}: maxSpeed must be null or a finite positive number`);
  }

  if (typeof entry.rotor !== "boolean") {
    throw new Error(`report part ${partTypeId}: rotor must be a boolean`);
  }

  return entry;
}

/** JSON with at most six decimals, and an explicit `.0` for an integral value -- the same style
 * the hand-authored content uses (`4.0`, `1.0`), so an extracted float never looks like an int. */
const num = (value) => {
  const rounded = Number(value.toFixed(6));
  return Number.isInteger(rounded) ? `${rounded}.0` : String(rounded);
};

const renderFan = (entry) => {
  const pairs = [
    `"thrustPerTick": ${num(entry.thrustPerTick)}`,
    `"directionX": ${entry.directionX}`,
    `"directionY": ${entry.directionY}`,
  ];
  if (entry.maxSpeed !== null) {
    pairs.push(`"maxSpeed": ${num(entry.maxSpeed)}`);
  }

  if (entry.rotor) {
    pairs.push(`"rotor": true`);
  }

  return `{ ${pairs.join(", ")} }`;
};

const renderValue = (value) =>
  typeof value === "string" ? `"${value}"` : typeof value === "object" && value !== null ? JSON.stringify(value) : String(value);
const renderCapabilities = (object) => `{ ${Object.entries(object)
  .map(([key, value]) => `"${key}": ${key === "fan" ? renderFan(value) : renderValue(value)}`)
  .join(", ")} }`;

/** Semantic form of one capabilities object, so "already applied" is a value comparison and not
 * a text one: `fan` is reduced to the five fields this tool owns. */
const round6 = (value) => Number(value.toFixed(6));
const fanSignature = (fan) => ({
  thrustPerTick: round6(fan.thrustPerTick),
  directionX: fan.directionX,
  directionY: fan.directionY,
  maxSpeed: fan.maxSpeed === null || fan.maxSpeed === undefined ? null : round6(fan.maxSpeed),
  rotor: fan.rotor ?? false,
});
const canonical = (capabilities) =>
  JSON.stringify(Object.entries(capabilities).map(([key, value]) => [key, key === "fan" ? fanSignature(value) : value]));

/** The rewritten capabilities of one part: the report's `fan`, `activation` toggle, and the three
 * replaced keys gone. Key order is preserved so the diff stays a single line per part. */
function rewriteCapabilities(partTypeId, capabilities) {
  const entry = fanOf(partTypeId);
  if (entry === null) {
    return null;
  }

  const next = {};
  let fanPlaced = false;
  const insertFan = () => {
    if (!fanPlaced) {
      next.fan = entry;
      fanPlaced = true;
    }
  };

  for (const [key, value] of Object.entries(capabilities)) {
    if (key === "balloon" || key === "wheel" || key === "motor") {
      insertFan();
      continue;
    }

    if (key === "fan") {
      insertFan();
      continue;
    }

    if (key === "activation") {
      insertFan();
      next.activation = "toggle";
      continue;
    }

    if (key === "jointConnectionType" || key === "enginePower") {
      // Kept in place; the fan slot opens right after the fields the extractor owns.
      next[key] = value;
      continue;
    }

    next[key] = value;
  }

  insertFan();
  next.activation = next.activation ?? "toggle";
  return next;
}

const isSameCapabilities = (left, right) => canonical(left) === canonical(right);

const document = JSON.parse(readFileSync(CONTENT, "utf8"));
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

  const entry = fanOf(part.partTypeId);
  const capabilities = part.capabilities ?? {};
  if (capabilities.fan === undefined) {
    throw new Error(`part ${part.partTypeId}: capabilities.fan was not written`);
  }

  if (capabilities.fan.thrustPerTick !== Number(num(entry.thrustPerTick))
    || capabilities.fan.directionX !== entry.directionX
    || capabilities.fan.directionY !== entry.directionY
    || (capabilities.fan.maxSpeed ?? null) !== (entry.maxSpeed === null ? null : Number(num(entry.maxSpeed)))
    || (capabilities.fan.rotor ?? false) !== entry.rotor) {
    throw new Error(`part ${part.partTypeId}: written fan does not match the report`);
  }

  if (capabilities.balloon !== undefined || capabilities.wheel !== undefined || capabilities.motor !== undefined) {
    throw new Error(`part ${part.partTypeId}: the replaced capabilities are still present`);
  }

  if (capabilities.activation !== "toggle") {
    throw new Error(`part ${part.partTypeId}: activation must be toggle, got ${capabilities.activation}`);
  }

  rewritten.set(part.partTypeId, true);
}

if (rewritten.size !== expected.size) {
  throw new Error(`fan capabilities written for ${rewritten.size} parts, expected ${expected.size}`);
}

if (!DRY_RUN) {
  writeFileSync(CONTENT, text);
}

console.log(`${DRY_RUN ? "would update" : "updated"} ${updated} parts in ${CONTENT}`);
console.log(`already matching: ${unchanged}; verified: ${rewritten.size}`);
