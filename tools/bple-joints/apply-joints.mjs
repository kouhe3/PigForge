#!/usr/bin/env node
// Writes the extracted joint capability of every mapped part into content/parts.json,
// preserving the file's hand-authored formatting everywhere else. Companion to
// extract-joints.mjs; the report is the ONLY admissible source for the joint values.
//
// Fields written (all inside the part's `capabilities` object):
//   - jointConnectionType: the report's `jointType` (none/source/target). The original's
//     Contraption.cs:690 merges two adjacent parts only when neither is `none` and at
//     least one is `source`.
//   - canEnclose: true only for the frame prefabs (Part_WoodenFrame_* / Part_MetalFrame_*,
//     Frame.cs:32). `canBeEnclosed` is derived in code as `!canEnclose`, never authored.
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
import { dirname, join, resolve } from "node:path";
import { fileURLToPath } from "node:url";

const HERE = dirname(fileURLToPath(import.meta.url));
const REPO = resolve(HERE, "..", "..");

function arg(name, fallback) {
  const index = process.argv.indexOf(`--${name}`);
  return index >= 0 && process.argv[index + 1] ? process.argv[index + 1] : fallback;
}

const REPORT = resolve(arg("report", join(REPO, "tasks", "bple-joints-report.json")));
const CONTENT = resolve(arg("content", join(REPO, "content", "parts.json")));
const DRY_RUN = process.argv.includes("--dry-run");

const report = JSON.parse(readFileSync(REPORT, "utf8")).parts;

/** The original only frames enclose (Frame.cs:32). */
const isFramePrefab = (prefab) => /^Part_(WoodenFrame|MetalFrame)_/.test(prefab ?? "");

const JOINT_VALUES = new Set(["none", "source", "target"]);

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

const num = (value) => (Number.isInteger(value) ? value.toFixed(1) : String(Number(value.toFixed(4))));

function renderAttachment(attachment) {
  const fields = [`"direction": ${JSON.stringify(attachment.direction)}`];
  if (attachment.maxDistance !== undefined) {
    fields.push(`"maxDistance": ${num(attachment.maxDistance)}`);
  }

  if (attachment.offset !== undefined) {
    fields.push(`"offset": [${attachment.offset.map(num).join(", ")}]`);
  }

  if (attachment.distanceFactor !== undefined) {
    fields.push(`"distanceFactor": ${num(attachment.distanceFactor)}`);
  }

  if (attachment.distanceOffset !== undefined) {
    fields.push(`"distanceOffset": ${num(attachment.distanceOffset)}`);
  }

  if (attachment.pigDistanceBonus !== undefined) {
    fields.push(`"pigDistanceBonus": ${num(attachment.pigDistanceBonus)}`);
  }

  return `"attachment": { ${fields.join(", ")} }`;
}

/** Matches one property's value in the inline capabilities text (values nest at most one level). */
const propertyPattern = (key) => new RegExp(`"${key}":\\s*(?:"[^"]*"|true|false|-?[0-9.]+|\\{(?:[^{}]|\\{[^{}]*\\})*\\})`);

function upsert(capabilities, desired) {
  let cap = capabilities;
  const missing = desired.filter(([key]) => !propertyPattern(key).test(cap));
  if (missing.length > 0) {
    cap = `{ ${missing.map(([, rendered]) => rendered).join(", ")},${cap.slice(1)}`;
  }

  for (const [key, rendered] of desired) {
    cap = cap.replace(propertyPattern(key), rendered);
  }

  return cap;
}

let text = readFileSync(CONTENT, "utf8");
const document = JSON.parse(text);
let updated = 0;
const written = { none: 0, source: 0, target: 0 };
let frames = 0;
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

  const encloses = isFramePrefab(entry.prefab);
  const attachment = attachmentFor(entry.prefab);
  const desired = [["jointConnectionType", `"jointConnectionType": ${JSON.stringify(jointType)}`]];
  if (encloses) {
    desired.push(["canEnclose", `"canEnclose": true`]);
  }

  if (attachment) {
    desired.push(["attachment", renderAttachment(attachment)]);
  }

  const anchor = `"partTypeId": ${part.partTypeId},`;
  const anchorIndex = text.indexOf(anchor);
  if (anchorIndex < 0) throw new Error(`anchor missing for part ${part.partTypeId}`);
  const shapesIndex = text.indexOf('"shapes":', anchorIndex);
  if (shapesIndex < 0) throw new Error(`shapes missing for part ${part.partTypeId}`);

  const capabilitiesIndex = text.indexOf('"capabilities":', anchorIndex);
  const hasCapabilities = capabilitiesIndex >= 0 && capabilitiesIndex < shapesIndex;

  if (!hasCapabilities) {
    const lineStart = text.lastIndexOf("\n", shapesIndex) + 1;
    const fields = desired.map(([, rendered]) => rendered).join(", ");
    text = `${text.slice(0, lineStart)}      "capabilities": { ${fields} },\n${text.slice(lineStart)}`;
  } else {
    const open = text.indexOf("{", capabilitiesIndex);
    const close = text.indexOf("}", open);
    if (open < 0 || close < 0 || text.slice(open, close).includes("\n")) {
      throw new Error(`part ${part.partTypeId}: expected a single-line capabilities object`);
    }

    text = text.slice(0, open) + upsert(text.slice(open, close + 1), desired) + text.slice(close + 1);
  }

  written[jointType] += 1;
  if (encloses) frames += 1;
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

  const encloses = isFramePrefab(entry.prefab);
  if (encloses !== (capabilities.canEnclose === true)) {
    throw new Error(`part ${part.partTypeId}: canEnclose mismatch`);
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
console.log(`canEnclose: ${frames} frames; attachment: ${attachments} parts`);
