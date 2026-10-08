#!/usr/bin/env node
// Writes the extracted joint capability of every mapped part into content/parts.json,
// preserving the file's hand-authored formatting everywhere else. Companion to
// extract-joints.mjs; the report is the ONLY admissible source for the joint values.
//
// Fields written (all inside the part's `capabilities` object):
//   - jointConnectionType: the report's `jointType` (none/source/target). The original's
//     Contraption.cs:690 merges two adjacent parts only when neither is `none` and at
//     least one is `source`.
//   - canEnclose: the report's `canEncloseParts` -- true when the part's class or an ancestor
//     overrides `CanEncloseParts()` to return true (Frame.cs:32, corrected back to false by
//     BoxFrame.cs:3). Since 2026-10-06 this is read from the report; a report without the field
//     is a hard error instead of a fallback to the prefab-name rule used before.
//   - canBeEnclosed: the report's `canBeEnclosed` -- true when the class or an ancestor overrides
//     `CanBeEnclosed()` to return true (Pig.cs:170, Egg.cs:14, ...), which is exactly the set the
//     original lets a frame enclose under the vanilla `EnclosableParts = false` profile. Written
//     as `true` when the report says so and REMOVED when it does not; until 2026-10-06 the key was
//     never authored and the runtime derived `!canEnclose`, which is strictly wider.
//   - attachment: the runtime rope attachment of the balloon/sandbag families. `none` means
//     "no design-time joint", not "attaches to nothing": both families build a SpringJoint
//     to a released chassis at start of simulation (Sandbag.cs:136-164, Balloon.cs:143-166),
//     which is why they need this capability instead.
//
// The attachment numbers are the original's own constants, quoted per prefab family below;
// the report's prefab name selects the family. The balloon's rope length is derived at
// runtime from the build separation, so its content carries the formula's constants.
//
// Usage:
//   node tools/bple-joints/apply-joints.mjs [--report <file>] [--content <file>] [--dry-run]

import { readFileSync, writeFileSync } from "node:fs";
import { flag } from "../lib/args.mjs";
import { applyReport, contentFile } from "../lib/paths.mjs";
import { capabilitiesSpan, partSpan, propertyPattern, readContentText, removeProperty, upsert } from "../lib/parts.mjs";
import { numInt1 } from "../lib/report.mjs";

const REPORT = applyReport("joints");
const CONTENT = contentFile();
const DRY_RUN = flag("dry-run");

const report = JSON.parse(readFileSync(REPORT, "utf8")).parts;

/** The report's enclosure flags, resolved by extract-joints.mjs from the part's class chain. The
 * report is the only admissible source, so a missing field is a hard error -- this tool must never
 * fall back to the prefab-name rule it used before 2026-10-06. */
function enclosureFlagOf(entry, partTypeId, field) {
  const value = entry[field];
  if (typeof value !== "boolean") {
    throw new Error(`part ${partTypeId}: report has no boolean ${field} (re-run extract-joints.mjs)`);
  }

  return value;
}

const JOINT_VALUES = new Set(["none", "source", "target"]);
const STRENGTH_VALUES = new Set(["weak", "normal", "high", "extreme", "highlyExtreme"]);
const DIRECTION_VALUES = new Set(["any", "right", "up", "left", "down", "leftAndRight", "upAndDown", "none"]);

/** The report's connection-direction name for one part; the report is the only admissible
 * source, so a missing or unknown value is a hard error rather than a silent fallback. */
function directionNameOf(entry, partTypeId) {
  const name = entry.jointConnectionDirection?.name;
  if (!DIRECTION_VALUES.has(name)) {
    throw new Error(`part ${partTypeId}: unknown jointConnectionDirection ${JSON.stringify(entry.jointConnectionDirection)}`);
  }

  return name;
}

/** The report's strength name for one part; the report is the only admissible source, so a
 * missing or unknown value is a hard error rather than a silent fallback. */
function strengthNameOf(entry, partTypeId) {
  const name = entry.jointStrength?.name;
  if (!STRENGTH_VALUES.has(name)) {
    throw new Error(`part ${partTypeId}: unknown jointConnectionStrength ${JSON.stringify(entry.jointStrength)}`);
  }

  return name;
}

// Runtime attachments, keyed by prefab family. Sources:
//   Sandbag.cs:96     search direction `m_direction = Vector3.up` (it hangs from what it finds)
//   Sandbag.cs:144-160  switch (m_numberOfBalloons): 1 -> 0.5 / (-0.15,-0.15,-0.01),
//                       2 -> 0.55 / (0.35,-0.30,-0.02), default (3) -> 0.65 / (0,-0.35,-0.03).
//                       The stack count is serialized per prefab (Part_Sandbag_* = 1,
//                       Part_Sandbags2_* = 2, Part_Sandbags3_* = 3).
//   Sandbag.cs:157-163  minDistance 0, anchor up*0.5, spring 100, damper 10.
//   Balloon.cs:104      searches downward; Balloon.cs:145-160 floats at
//                       up*0.5 with anchor up*-0.5, minDistance 0, spring 100, damper 10 and
//                       maxDistance = Random.Range(0.8f, 1.2f) * (distance - 0.5f) + (anchor is pig ? 0.3f : 0).
//                       PigForge pins determinism with double-run hash tests, so the random factor
//                       is replaced by its deterministic mean 1.0 (Balloon.cs:157) and
//                       distanceFactor/distanceOffset/pigDistanceBonus carry the formula's constants.
const ATTACHMENTS = [
  { pattern: /^Part_Sandbag_/, direction: "up", maxDistance: 0.5, offset: [-0.15, -0.15, -0.01] },
  { pattern: /^Part_Sandbags2_/, direction: "up", maxDistance: 0.55, offset: [0.35, -0.3, -0.02] },
  { pattern: /^Part_Sandbags3_/, direction: "up", maxDistance: 0.65, offset: [0, -0.35, -0.03] },
  { pattern: /^Part_Balloon/, direction: "down", offset: [0, 0.5, 0], distanceFactor: 1, distanceOffset: -0.5, pigDistanceBonus: 0.3 },
];

const attachmentFor = (prefab) => ATTACHMENTS.find((entry) => entry.pattern.test(prefab ?? "")) ?? null;

function renderAttachment(attachment) {
  const fields = [`"direction": ${JSON.stringify(attachment.direction)}`];
  if (attachment.maxDistance !== undefined) {
    fields.push(`"maxDistance": ${numInt1(attachment.maxDistance)}`);
  }

  if (attachment.offset !== undefined) {
    fields.push(`"offset": [${attachment.offset.map(numInt1).join(", ")}]`);
  }

  if (attachment.distanceFactor !== undefined) {
    fields.push(`"distanceFactor": ${numInt1(attachment.distanceFactor)}`);
  }

  if (attachment.distanceOffset !== undefined) {
    fields.push(`"distanceOffset": ${numInt1(attachment.distanceOffset)}`);
  }

  if (attachment.pigDistanceBonus !== undefined) {
    fields.push(`"pigDistanceBonus": ${numInt1(attachment.pigDistanceBonus)}`);
  }

  return `"attachment": { ${fields.join(", ")} }`;
}

let text = readContentText();
const document = JSON.parse(text);
let updated = 0;
const written = { none: 0, source: 0, target: 0 };
const strengthWritten = { weak: 0, normal: 0, high: 0, extreme: 0, highlyExtreme: 0 };
const directionWritten = {};
let frames = 0;
let enclosedParts = 0;
let canBeEnclosedRemoved = 0;
let attachments = 0;

for (const part of document.parts) {
  const entry = report[String(part.partTypeId)];
  if (!entry) {
    continue; // unmapped partTypeId (static level geometry): nothing extracted to write
  }

  const jointType = entry.jointType;
  if (!JOINT_VALUES.has(jointType)) {
    throw new Error(`part ${part.partTypeId}: unknown jointType ${JSON.stringify(jointType)}`);
  }

  const encloses = enclosureFlagOf(entry, part.partTypeId, "canEncloseParts");
  const enclosed = enclosureFlagOf(entry, part.partTypeId, "canBeEnclosed");
  const attachment = attachmentFor(entry.prefab);
  const strengthName = strengthNameOf(entry, part.partTypeId);
  const directionName = directionNameOf(entry, part.partTypeId);
  const desired = [
    ["jointConnectionType", `"jointConnectionType": ${JSON.stringify(jointType)}`],
    ["jointConnectionStrength", `"jointConnectionStrength": ${JSON.stringify(strengthName)}`],
    ["jointConnectionDirection", `"jointConnectionDirection": ${JSON.stringify(directionName)}`],
  ];
  if (encloses) {
    desired.push(["canEnclose", `"canEnclose": true`]);
  }

  if (enclosed) {
    desired.push(["canBeEnclosed", `"canBeEnclosed": true`]);
  }

  if (attachment) {
    desired.push(["attachment", renderAttachment(attachment)]);
  }

  const { shapesIndex } = partSpan(text, part.partTypeId);
  const span = capabilitiesSpan(text, part.partTypeId);

  if (span === null) {
    const lineStart = text.lastIndexOf("\n", shapesIndex) + 1;
    const fields = desired.map(([, rendered]) => rendered).join(", ");
    text = `${text.slice(0, lineStart)}      "capabilities": { ${fields} },\n${text.slice(lineStart)}`;
  } else {
    if (text.slice(span.open, span.close).includes("\n")) {
      throw new Error(`part ${part.partTypeId}: expected a single-line capabilities object`);
    }

    let capabilities = upsert(text.slice(span.open, span.close + 1), desired);
    if (!enclosed && propertyPattern("canBeEnclosed").test(capabilities)) {
      capabilities = removeProperty(capabilities, "canBeEnclosed");
      canBeEnclosedRemoved += 1;
    }

    text = text.slice(0, span.open) + capabilities + text.slice(span.close + 1);
  }

  written[jointType] += 1;
  strengthWritten[strengthName] += 1;
  directionWritten[directionName] = (directionWritten[directionName] ?? 0) + 1;
  if (encloses) frames += 1;
  if (enclosed) enclosedParts += 1;
  if (attachment) attachments += 1;
  updated += 1;
}

// Re-parse and re-derive: the rewrite must be valid JSON, exactly match the report, and be
// idempotent (a second run changes nothing).
const check = JSON.parse(text);
for (const part of check.parts) {
  const entry = report[String(part.partTypeId)];
  if (!entry) continue;
  const capabilities = part.capabilities;
  if (!capabilities || capabilities.jointConnectionType !== entry.jointType) {
    throw new Error(`part ${part.partTypeId}: jointConnectionType mismatch`);
  }

  if (capabilities.jointConnectionStrength !== strengthNameOf(entry, part.partTypeId)) {
    throw new Error(`part ${part.partTypeId}: jointConnectionStrength mismatch`);
  }

  if (capabilities.jointConnectionDirection !== directionNameOf(entry, part.partTypeId)) {
    throw new Error(`part ${part.partTypeId}: jointConnectionDirection mismatch`);
  }

  if (enclosureFlagOf(entry, part.partTypeId, "canEncloseParts") !== (capabilities.canEnclose === true)) {
    throw new Error(`part ${part.partTypeId}: canEnclose mismatch`);
  }

  if (enclosureFlagOf(entry, part.partTypeId, "canBeEnclosed") !== (capabilities.canBeEnclosed === true)) {
    throw new Error(`part ${part.partTypeId}: canBeEnclosed mismatch`);
  }

  const expected = attachmentFor(entry.prefab);
  const actual = capabilities.attachment;
  if (!expected) {
    if (actual !== undefined) throw new Error(`part ${part.partTypeId}: unexpected attachment`);
  } else if (
    actual?.direction !== expected.direction
    || actual.maxDistance !== expected.maxDistance
    || JSON.stringify(actual.offset) !== JSON.stringify(expected.offset)
    || actual.distanceFactor !== expected.distanceFactor
    || actual.distanceOffset !== expected.distanceOffset
    || actual.pigDistanceBonus !== expected.pigDistanceBonus
  ) {
    throw new Error(`part ${part.partTypeId}: attachment mismatch ${JSON.stringify(actual)} != ${JSON.stringify(expected)}`);
  }
}

if (!DRY_RUN) writeFileSync(CONTENT, text);
console.log(`${DRY_RUN ? "would update" : "updated"} ${updated} parts in ${CONTENT}`);
console.log(`jointConnectionType: none ${written.none} / source ${written.source} / target ${written.target}`);
console.log(`jointConnectionStrength: ${JSON.stringify(strengthWritten)}`);
console.log(`jointConnectionDirection: ${JSON.stringify(directionWritten)}`);
console.log(`canEnclose: ${frames} frames; canBeEnclosed: ${enclosedParts} parts (removed ${canBeEnclosedRemoved}); attachment: ${attachments} parts`);
