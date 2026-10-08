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

import { readFileSync } from "node:fs";
import { applyReport } from "../lib/paths.mjs";
import { applyContent, canonicalCapabilities, renderCapabilities, rewriteCapabilitiesLines } from "../lib/parts.mjs";
import { num6, numberOrNull6 } from "../lib/report.mjs";

const REPORT = applyReport("aero");

const report = JSON.parse(readFileSync(REPORT, "utf8"));
const parts = report.parts ?? {};
if (Object.keys(parts).length === 0) {
  throw new Error(`${REPORT} carries no parts`);
}

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

/// The wing object as the hand-authored content spells it.
const renderWing = (wing) => `{ "liftConstant": ${num6(wing.liftConstant)} }`;

const render = renderCapabilities({ wing: renderWing, tail: num6 });

/** Semantic form of one capabilities object, so "already applied" is a value comparison and not a
 * text one: `wing` is reduced to the one field this tool owns. */
const canonical = canonicalCapabilities({
  wing: (wing) => (typeof wing?.liftConstant === "number" ? numberOrNull6(wing.liftConstant) : null),
  tail: (value) => (typeof value === "number" ? numberOrNull6(value) : value),
});

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
      next.wing = { liftConstant: numberOrNull6(entry.wing.liftConstant) };
    } else {
      next.tail = numberOrNull6(entry.tail);
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

      const entry = aeroOf(part.partTypeId);
      const capabilities = part.capabilities ?? {};
      if (entry.mirror && capabilities.mirror !== true) {
        throw new Error(`part ${part.partTypeId}: capabilities.mirror was not written`);
      }

      if (entry.wing !== undefined && entry.wing !== null) {
        if (capabilities.wing === undefined) {
          throw new Error(`part ${part.partTypeId}: capabilities.wing was not written`);
        }

        if (capabilities.wing.liftConstant !== numberOrNull6(entry.wing.liftConstant)) {
          throw new Error(`part ${part.partTypeId}: written wing.liftConstant does not match the report`);
        }

        if (capabilities.wing.liftCoef !== undefined || capabilities.wing.maxLift !== undefined) {
          throw new Error(`part ${part.partTypeId}: the replaced wing keys are still present`);
        }
      } else {
        if (capabilities.tail !== numberOrNull6(entry.tail)) {
          throw new Error(`part ${part.partTypeId}: written tail does not match the report`);
        }
      }

      rewritten.add(part.partTypeId);
    }

    if (rewritten.size !== expected.size) {
      throw new Error(`aero capabilities written for ${rewritten.size} parts, expected ${expected.size}`);
    }

    return { verified: rewritten.size };
  },
  report: ({ updated, unchanged }, { verified }, { dryRun, content }) => {
    console.log(`${dryRun ? "would update" : "updated"} ${updated} parts in ${content}`);
    console.log(`already matching: ${unchanged}; verified: ${verified}`);
  },
});
