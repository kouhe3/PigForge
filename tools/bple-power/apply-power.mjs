#!/usr/bin/env node
// Writes the extracted power data of every mapped part into content/parts.json, preserving the
// file's hand-authored formatting everywhere else. Companion to extract-power.mjs; the report is
// the ONLY admissible source for the power values.
//
// Fields written (both inside the part's `capabilities` object):
//   - powerConsumption: the prefab's `m_powerConsumption` (BasePart.cs:162). > 0 means the part
//     is a consumer (BasePart.cs:601-603): its switch being on makes it count toward its
//     component's consumption (Contraption.cs:2633-2644), which is the denominator of the power
//     factor (Contraption.cs:540-556).
//   - enginePower: the prefab's `m_enginePower` (BasePart.cs:164). > 0 means the part is an
//     engine (BasePart.cs:606-608) and adds to its component's enginePower -- the numerator of
//     the same formula. The engine itself applies no force (Engine.cs:29,138).
//
// Both are written for every mapped part, including the explicit zeros, because the original
// serializes both fields on every part prefab and an absent field would be indistinguishable
// from a stale one. `0` is the model's "not a consumer / not an engine" (the parser stores them
// as plain numbers, never as null).
//
// A part the report marks as a driven wheel (its original script overrides InitializeEngine)
// additionally gets `motor: { thrustPerTick, directionX: 1 }` and `activation: "toggle"`, the
// same two fields the motor wheel and propeller already carry: `motor` is the per-tick impulse
// GameplayRules.RunMotors gates with the cluster power factor, `activation` is the switch the
// sandbox starts off. Both go at the end of the capabilities object, matching the hand-authored
// motor-wheel line. Nothing else is touched -- the propeller keeps the drive numbers it was
// calibrated with, because its original force is modulated by INSettings (FanPropeller.cs:95-101)
// rather than being the prefab's plain `m_force`.
//
// Usage:
//   node tools/bple-power/apply-power.mjs [--report <file>] [--content <file>] [--dry-run]

import { readFileSync } from "node:fs";
import { applyReport } from "../lib/paths.mjs";
import { applyContent, capabilitiesSpan, partSpan, upsert } from "../lib/parts.mjs";
import { num4 } from "../lib/report.mjs";

const REPORT = applyReport("power");

const report = JSON.parse(readFileSync(REPORT, "utf8")).parts;

/** Rewrites one document text with the report's power values, preserving everything else. */
function rewrite(text) {
  const document = JSON.parse(text);
  let updated = 0;
  let powered = 0;
  let engines = 0;
  let driven = 0;

  for (const part of document.parts) {
    const entry = report[String(part.partTypeId)];
    if (!entry) {
      continue; // unmapped partTypeId (static level geometry): nothing extracted to write
    }

    const { powerConsumption, enginePower } = entry;
    if (!Number.isFinite(powerConsumption) || !Number.isFinite(enginePower) || powerConsumption < 0 || enginePower < 0) {
      throw new Error(`part ${part.partTypeId}: invalid power values ${JSON.stringify(entry)}`);
    }

    const desired = [
      ["powerConsumption", `"powerConsumption": ${num4(powerConsumption)}`],
      ["enginePower", `"enginePower": ${num4(enginePower)}`],
    ];

    const drive = entry.drive ?? null;
    if (drive !== null) {
      desired.push([
        "motor",
        `"motor": { "thrustPerTick": ${num4(drive.motorThrustPerTick)}, "directionX": 1 }`,
        true,
      ]);
      desired.push(["activation", `"activation": "${drive.activation}"`, true]);
    }

    const span = capabilitiesSpan(text, part.partTypeId);
    if (span === null) {
      const { shapesIndex } = partSpan(text, part.partTypeId);
      const lineStart = text.lastIndexOf("\n", shapesIndex) + 1;
      const fields = desired.map(([, rendered]) => rendered).join(", ");
      text = `${text.slice(0, lineStart)}      "capabilities": { ${fields} },\n${text.slice(lineStart)}`;
    } else {
      const { open, close } = span;
      if (text.slice(open, close).includes("\n")) {
        throw new Error(`part ${part.partTypeId}: expected a single-line capabilities object`);
      }

      text = text.slice(0, open) + upsert(text.slice(open, close + 1), desired) + text.slice(close + 1);
    }

    if (powerConsumption > 0) powered += 1;
    if (enginePower > 0) engines += 1;
    if (drive !== null) driven += 1;
    updated += 1;
  }

  return { text, updated, powered, engines, driven };
}

// Re-parse and re-derive: the rewrite must be valid JSON and exactly match the report, and the
// untouched parts must not carry a stale field.
function verify(text) {
  const document = JSON.parse(text);
  for (const part of document.parts) {
    const entry = report[String(part.partTypeId)];
    const capabilities = part.capabilities ?? {};
    if (!entry) {
      if (capabilities.powerConsumption !== undefined || capabilities.enginePower !== undefined) {
        throw new Error(`part ${part.partTypeId}: unexpected power fields on an unmapped part`);
      }

      continue;
    }

    if (capabilities.powerConsumption !== entry.powerConsumption || capabilities.enginePower !== entry.enginePower) {
      throw new Error(
        `part ${part.partTypeId}: power mismatch ${capabilities.powerConsumption}/${capabilities.enginePower} != ${entry.powerConsumption}/${entry.enginePower}`,
      );
    }

    if (entry.drive) {
      const motor = capabilities.motor;
      if (
        motor === null
        || motor === undefined
        || num4(motor.thrustPerTick) !== num4(entry.drive.motorThrustPerTick)
        || motor.directionX !== 1
        || capabilities.activation !== entry.drive.activation
      ) {
        throw new Error(
          `part ${part.partTypeId}: drive mismatch ${JSON.stringify(motor)}/${capabilities.activation} != ` +
            `thrustPerTick ${entry.drive.motorThrustPerTick}/${entry.drive.activation}`,
        );
      }
    }
  }

  return document;
}

applyContent({
  rewrite,
  verify: (result) => {
    verify(result.text);

    // Idempotence: a second application over the rewritten text must be a byte-for-byte no-op.
    const second = rewrite(result.text);
    if (second.text !== result.text) {
      throw new Error("rewrite is not idempotent");
    }

    return {};
  },
  report: ({ updated, powered, engines, driven }, _verified, { dryRun, content }) => {
    console.log(`${dryRun ? "would update" : "updated"} ${updated} parts in ${content}`);
    console.log(`powered (powerConsumption > 0): ${powered}; engines (enginePower > 0): ${engines}`);
    console.log(`driven wheels (motor + toggle): ${driven}`);
  },
});
