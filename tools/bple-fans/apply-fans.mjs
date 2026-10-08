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

import { readFileSync } from "node:fs";
import { applyReport } from "../lib/paths.mjs";
import { applyContent, canonicalCapabilities, renderCapabilities, rewriteCapabilitiesLines } from "../lib/parts.mjs";
import { num6, numberOrNull6 } from "../lib/report.mjs";

const REPORT = applyReport("fans");

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

/// The fan object as one line: the direction is a plain integer, `rotor` and `maxSpeed` are
/// written only when they apply.
const renderFan = (entry) => {
  const pairs = [
    `"thrustPerTick": ${num6(entry.thrustPerTick)}`,
    `"directionX": ${entry.directionX}`,
    `"directionY": ${entry.directionY}`,
  ];
  if (entry.maxSpeed !== null) {
    pairs.push(`"maxSpeed": ${num6(entry.maxSpeed)}`);
  }

  if (entry.rotor) {
    pairs.push(`"rotor": true`);
  }

  return `{ ${pairs.join(", ")} }`;
};

const render = renderCapabilities({ fan: renderFan });

/** Semantic form of one capabilities object, so "already applied" is a value comparison and not
 * a text one: `fan` is reduced to the five fields this tool owns. */
const canonical = canonicalCapabilities({
  fan: (fan) => ({
    thrustPerTick: numberOrNull6(fan.thrustPerTick),
    directionX: fan.directionX,
    directionY: fan.directionY,
    maxSpeed: fan.maxSpeed === null || fan.maxSpeed === undefined ? null : numberOrNull6(fan.maxSpeed),
    rotor: fan.rotor ?? false,
  }),
});

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
    if (key === "balloon" || key === "wheel" || key === "motor" || key === "fan") {
      insertFan();
      continue;
    }

    if (key === "activation") {
      insertFan();
      next.activation = "toggle";
      continue;
    }

    next[key] = value;
  }

  insertFan();
  next.activation = next.activation ?? "toggle";
  return next;
}

const expected = new Set(Object.keys(parts).map(Number));

applyContent({
  rewrite: (text) => rewriteCapabilitiesLines(text, { rewrite: rewriteCapabilities, canonical, render }),
  // Re-parse and re-derive: the rewrite must be valid JSON and match the report exactly.
  verify: (result) => {
    const rewritten = new Set();
    for (const part of JSON.parse(result.text).parts) {
      if (!expected.has(part.partTypeId)) {
        continue;
      }

      const entry = fanOf(part.partTypeId);
      const capabilities = part.capabilities ?? {};
      const fan = capabilities.fan;
      if (fan === undefined) {
        throw new Error(`part ${part.partTypeId}: capabilities.fan was not written`);
      }

      if (fan.thrustPerTick !== numberOrNull6(entry.thrustPerTick)
        || fan.directionX !== entry.directionX
        || fan.directionY !== entry.directionY
        || (fan.maxSpeed ?? null) !== (entry.maxSpeed === null ? null : numberOrNull6(entry.maxSpeed))
        || (fan.rotor ?? false) !== entry.rotor) {
        throw new Error(`part ${part.partTypeId}: written fan does not match the report`);
      }

      if (capabilities.balloon !== undefined || capabilities.wheel !== undefined || capabilities.motor !== undefined) {
        throw new Error(`part ${part.partTypeId}: the replaced capabilities are still present`);
      }

      if (capabilities.activation !== "toggle") {
        throw new Error(`part ${part.partTypeId}: activation must be toggle, got ${capabilities.activation}`);
      }

      rewritten.add(part.partTypeId);
    }

    if (rewritten.size !== expected.size) {
      throw new Error(`fan capabilities written for ${rewritten.size} parts, expected ${expected.size}`);
    }

    return { verified: rewritten.size };
  },
  report: ({ updated, unchanged }, { verified }, { dryRun, content }) => {
    console.log(`${dryRun ? "would update" : "updated"} ${updated} parts in ${content}`);
    console.log(`already matching: ${unchanged}; verified: ${verified}`);
  },
});
