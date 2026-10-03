#!/usr/bin/env node
// Writes the extracted wheel suspension of every mapped part into content/parts.json,
// preserving the file's hand-authored formatting everywhere else. Companion to
// extract-springs.mjs; the report is the ONLY admissible source for these numbers.
//
// Field written (inside the part's `capabilities` object):
//   - suspension: the original's linear-limit spring of the wheel's own joint
//     (OffRoadWheel.CustomConnectToPart, OffRoadWheel.cs:202-220): the wheel's local Y is
//     Limited and held at `restOffset` by `stiffness` N/m with `damper` N*s/m. The axis is
//     not content: it is the wheel's own build-frame Y, perpendicular to its axle (the
//     joint's default axes), and the code attaches it to the parent body.
// A part whose report entry declares no suspension must not carry the key: the extractor's
// negative evidence (CartWheel/MotorWheel/StickyWheel never override CustomConnectToPart,
// so Contraption.AddFixedJoint welds them rigidly) is what makes their axles stay rigid.
//
// Usage:
//   node tools/bple-springs/apply-springs.mjs [--report <file>] [--content <file>] [--dry-run]

import { readFileSync, writeFileSync } from "node:fs";
import { dirname, join, resolve } from "node:path";
import { fileURLToPath } from "node:url";

const HERE = dirname(fileURLToPath(import.meta.url));
const REPO = resolve(HERE, "..", "..");

function arg(name, fallback) {
  const index = process.argv.indexOf(`--${name}`);
  return index >= 0 && process.argv[index + 1] ? process.argv[index + 1] : fallback;
}

const REPORT = resolve(arg("report", join(REPO, "tasks", "bple-springs-report.json")));
const CONTENT = resolve(arg("content", join(REPO, "content", "parts.json")));
const DRY_RUN = process.argv.includes("--dry-run");

const report = JSON.parse(readFileSync(REPORT, "utf8"));
const parts = report.parts;

/** Same rendering as tools/bple-joints/apply-joints.mjs: integers keep one decimal, like the hand-authored content. */
const num = (value) => (Number.isInteger(Number(value)) ? Number(value).toFixed(1) : String(Number(Number(value).toFixed(4))));

/** Numeric comparison of a rendered value against the report's. */
const same = (left, right) => Number(left) === Number(right);

function renderSuspension(suspension) {
  const fields = [
    `"stiffness": ${num(suspension.stiffness)}`,
    `"damper": ${num(suspension.damper)}`,
    `"restOffset": ${num(suspension.restOffset)}`,
  ];
  return `"suspension": { ${fields.join(", ")} }`;
}

/** Matches one property's value in the inline capabilities text (values nest at most one level). */
const propertyPattern = (key) => new RegExp(`"${key}":\\s*(?:"[^"]*"|true|false|-?[0-9.]+|\\{(?:[^{}]|\\{[^{}]*\\})*\\})`);

function upsert(capabilities, desired) {
  let cap = capabilities;
  if (!propertyPattern(desired[0]).test(cap)) {
    cap = `{ ${desired[1]},${cap.slice(1)}`;
  }

  return cap.replace(propertyPattern(desired[0]), desired[1]);
}

/** The suspension the report derives for a mapped part, or null when the part declares none. */
function expectedSuspension(partTypeId) {
  const entry = parts[String(partTypeId)];
  if (!entry) return undefined; // unmapped partTypeId (PigForge-only invention): nothing extracted
  if (entry.suspension === null) return null;
  const suspension = entry.suspension;
  if (!(suspension.stiffness > 0) || !(suspension.damper >= 0) || !Number.isFinite(suspension.restOffset)) {
    throw new Error(`part ${partTypeId}: report suspension is not a usable spring: ${JSON.stringify(suspension)}`);
  }

  return suspension;
}

// A prefab that declares the spring but has no content part is the state the catalog is in:
// the OffRoadWheel (Part_MotorWheel_08_SET) is an IN extension part GameData.m_customParts
// never listed, so PigForge never imported it. Nothing can be written for it, and this is
// reported instead of silently applying nothing: the moment the part is added to
// tools/bple-variants/variant-overrides.json extras, this script writes its suspension.
if (report.unmappedSpringPrefabs.length > 0) {
  console.warn(
    `no content part for the prefab(s) that declare the wheel suspension: ${report.unmappedSpringPrefabs.join(", ")}`);
  console.warn("  -> they are not in the catalog (IN extension parts); add them to tools/bple-variants/variant-overrides.json extras");
}

let text = readFileSync(CONTENT, "utf8");
const document = JSON.parse(text);
let updated = 0;
let removed = 0;

for (const part of document.parts) {
  const suspension = expectedSuspension(part.partTypeId);
  if (suspension === undefined) continue;

  const anchor = `"partTypeId": ${part.partTypeId},`;
  const anchorIndex = text.indexOf(anchor);
  if (anchorIndex < 0) throw new Error(`anchor missing for part ${part.partTypeId}`);
  const shapesIndex = text.indexOf('"shapes":', anchorIndex);
  if (shapesIndex < 0) throw new Error(`shapes missing for part ${part.partTypeId}`);

  const capabilitiesIndex = text.indexOf('"capabilities":', anchorIndex);
  const hasCapabilities = capabilitiesIndex >= 0 && capabilitiesIndex < shapesIndex;

  if (suspension === null) {
    if (!hasCapabilities) continue;
    const open = text.indexOf("{", capabilitiesIndex);
    const close = text.indexOf("}", open);
    if (open < 0 || close < 0 || text.slice(open, close).includes("\n")) {
      throw new Error(`part ${part.partTypeId}: expected a single-line capabilities object`);
    }

    const cap = text.slice(open, close + 1);
    if (!propertyPattern("suspension").test(cap)) continue;
    text = text.slice(0, open) + cap.replace(propertyPattern("suspension"), "").replace("{ ,", "{ ").replace(", }", " }") + text.slice(close + 1);
    removed += 1;
    continue;
  }

  const rendered = renderSuspension(suspension);
  if (!hasCapabilities) {
    const lineStart = text.lastIndexOf("\n", shapesIndex) + 1;
    text = `${text.slice(0, lineStart)}      "capabilities": { ${rendered} },\n${text.slice(lineStart)}`;
  } else {
    const open = text.indexOf("{", capabilitiesIndex);
    const close = text.indexOf("}", open);
    if (open < 0 || close < 0 || text.slice(open, close).includes("\n")) {
      throw new Error(`part ${part.partTypeId}: expected a single-line capabilities object`);
    }

    text = text.slice(0, open) + upsert(text.slice(open, close + 1), ["suspension", rendered]) + text.slice(close + 1);
  }

  updated += 1;
}

// Re-parse and re-derive: the rewrite must be valid JSON, exactly match the report, and be
// idempotent (a second run changes nothing).
const check = JSON.parse(text);
for (const part of check.parts) {
  const suspension = expectedSuspension(part.partTypeId);
  if (suspension === undefined) continue;
  const actual = part.capabilities?.suspension ?? null;
  if (suspension === null) {
    if (actual !== null) throw new Error(`part ${part.partTypeId}: unexpected suspension`);
    continue;
  }

  if (actual === null
    || !same(actual.stiffness, suspension.stiffness)
    || !same(actual.damper, suspension.damper)
    || !same(actual.restOffset, suspension.restOffset)) {
    throw new Error(`part ${part.partTypeId}: suspension mismatch ${JSON.stringify(actual)} != ${JSON.stringify(suspension)}`);
  }
}

if (!DRY_RUN) writeFileSync(CONTENT, text);
console.log(`${DRY_RUN ? "would update" : "updated"} ${updated} parts in ${CONTENT}${removed > 0 ? ` (dropped ${removed} stale suspensions)` : ""}`);
for (const part of check.parts) {
  const suspension = expectedSuspension(part.partTypeId);
  if (suspension) {
    console.log(`  - ${part.partTypeId} ${part.name}: stiffness ${suspension.stiffness}, damper ${suspension.damper}, restOffset ${suspension.restOffset}`);
  }
}
