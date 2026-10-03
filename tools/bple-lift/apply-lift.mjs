#!/usr/bin/env node
// Writes the extracted balloon lift and runtime mass into content/parts.json, preserving the
// file's hand-authored formatting everywhere else. Companion to extract-lift.mjs; the report is
// the ONLY admissible source for `capabilities.balloon` and the balloon family's `mass`.
//
// What it writes, per balloon-family part (base and every imported variant):
//   - mass:            `Balloon.cs:129` runtime mass (0.1 kg) times the prefab's balloon count
//   - capabilities.balloon: the original's force converted to the per-tick impulse the room
//                      applies (`GameplayRules.RunBalloons`), i.e. `m_force * BalloonForce / 60`
//
// Parts that carry a `balloon` capability but do not come from a balloon prefab are reported and
// left untouched -- the rotor is a documented PigForge deviation (docs/original-vs-implemented.md),
// not an original balloon.
//
// Usage:
//   node tools/bple-lift/apply-lift.mjs [--report <file>] [--content <file>] [--dry-run]

import { readFileSync, writeFileSync } from "node:fs";
import { dirname, join, resolve } from "node:path";
import { fileURLToPath } from "node:url";

const HERE = dirname(fileURLToPath(import.meta.url));
const REPO = resolve(HERE, "..", "..");

function arg(name, fallback) {
  const index = process.argv.indexOf(`--${name}`);
  return index >= 0 && process.argv[index + 1] ? process.argv[index + 1] : fallback;
}

const REPORT = resolve(arg("report", join(REPO, "tasks", "bple-lift-report.json")));
const CONTENT = resolve(arg("content", join(REPO, "content", "parts.json")));
const DRY_RUN = process.argv.includes("--dry-run");

const report = JSON.parse(readFileSync(REPORT, "utf8")).parts;
const number = (value) => String(Number(Number(value).toFixed(6)));

const propertyPattern = (key) => new RegExp(`"${key}":\\s*(?:"[^"]*"|true|false|-?[0-9.]+|\\{(?:[^{}]|\\{[^{}]*\\})*\\})`);

let text = readFileSync(CONTENT, "utf8");
const document = JSON.parse(text);
let updated = 0;

for (const part of document.parts) {
  const entry = report[String(part.partTypeId)];
  if (!entry) {
    continue;
  }

  const anchor = `"partTypeId": ${part.partTypeId},`;
  const anchorIndex = text.indexOf(anchor);
  if (anchorIndex < 0) throw new Error(`anchor missing for part ${part.partTypeId}`);
  const shapesIndex = text.indexOf('"shapes":', anchorIndex);
  if (shapesIndex < 0) throw new Error(`shapes missing for part ${part.partTypeId}`);

  const massMatch = /"mass":\s*-?[0-9.]+/.exec(text.slice(anchorIndex, shapesIndex));
  if (!massMatch) throw new Error(`mass missing for part ${part.partTypeId}`);
  text =
    text.slice(0, anchorIndex + massMatch.index) +
    `"mass": ${number(entry.mass)}` +
    text.slice(anchorIndex + massMatch.index + massMatch[0].length);

  // Recomputed after the mass rewrite, which may have changed the text length.
  const shapesAfterMass = text.indexOf('"shapes":', anchorIndex);
  const capabilitiesIndex = text.indexOf('"capabilities":', anchorIndex);
  if (capabilitiesIndex < 0 || capabilitiesIndex > shapesAfterMass) {
    throw new Error(`part ${part.partTypeId}: balloon content needs an inline capabilities object`);
  }

  // The segment between this part's anchor and its `shapes` holds exactly one inline
  // capabilities object; rewriting the property there avoids brace matching across the nested
  // objects (a balloon carries an `attachment` object) while touching nothing else.
  const segment = text.slice(anchorIndex, shapesAfterMass);
  const pattern = propertyPattern("balloon");
  if (!pattern.test(segment)) {
    throw new Error(`part ${part.partTypeId}: balloon capability missing`);
  }

  text =
    text.slice(0, anchorIndex) +
    segment.replace(pattern, `"balloon": ${number(entry.liftPerTick)}`) +
    text.slice(shapesAfterMass);
  updated += 1;
}

// Re-parse and re-derive: the rewrite must be valid JSON, exactly match the report, and be
// idempotent (a second run changes nothing).
const check = JSON.parse(text);
for (const part of check.parts) {
  const entry = report[String(part.partTypeId)];
  if (!entry) continue;
  if (part.mass !== entry.mass) {
    throw new Error(`part ${part.partTypeId}: mass ${part.mass} != ${entry.mass}`);
  }

  if (part.capabilities?.balloon !== entry.liftPerTick) {
    throw new Error(`part ${part.partTypeId}: balloon ${part.capabilities?.balloon} != ${entry.liftPerTick}`);
  }
}

// Any part still carrying a balloon capability that no balloon prefab backs is a deviation and
// must be named explicitly, so a stale value cannot hide.
const strays = check.parts
  .filter((part) => part.capabilities?.balloon !== undefined && report[String(part.partTypeId)] === undefined)
  .map((part) => `${part.partTypeId}:${part.name}`);

if (!DRY_RUN) {
  writeFileSync(CONTENT, text);
}

console.log(`${DRY_RUN ? "would update" : "updated"} ${updated} balloon parts in ${CONTENT}`);
if (strays.length > 0) {
  console.log(`balloon-capability parts with no balloon prefab (left untouched): ${strays.join(", ")}`);
}
