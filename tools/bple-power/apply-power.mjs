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
// Usage:
//   node tools/bple-power/apply-power.mjs [--report <file>] [--content <file>] [--dry-run]

import { readFileSync, writeFileSync } from "node:fs";
import { dirname, join, resolve } from "node:path";
import { fileURLToPath } from "node:url";

const HERE = dirname(fileURLToPath(import.meta.url));
const REPO = resolve(HERE, "..", "..");

function arg(name, fallback) {
  const index = process.argv.indexOf(`--${name}`);
  return index >= 0 && process.argv[index + 1] ? process.argv[index + 1] : fallback;
}

const REPORT = resolve(arg("report", join(REPO, "tasks", "bple-power-report.json")));
const CONTENT = resolve(arg("content", join(REPO, "content", "parts.json")));
const DRY_RUN = process.argv.includes("--dry-run");

const report = JSON.parse(readFileSync(REPORT, "utf8")).parts;

const num = (value) => (Number.isInteger(value) ? String(value) : String(Number(value.toFixed(4))));

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

/** Rewrites one document text with the report's power values, preserving everything else. */
function rewrite(text) {
  const document = JSON.parse(text);
  let updated = 0;
  let powered = 0;
  let engines = 0;

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
      ["powerConsumption", `"powerConsumption": ${num(powerConsumption)}`],
      ["enginePower", `"enginePower": ${num(enginePower)}`],
    ];

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

    if (powerConsumption > 0) powered += 1;
    if (enginePower > 0) engines += 1;
    updated += 1;
  }

  return { text, updated, powered, engines };
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
  }

  return document;
}

const first = rewrite(readFileSync(CONTENT, "utf8"));
verify(first.text);

// Idempotence: a second application over the rewritten text must be a byte-for-byte no-op.
const second = rewrite(first.text);
if (second.text !== first.text) {
  throw new Error("rewrite is not idempotent");
}

if (!DRY_RUN) writeFileSync(CONTENT, first.text);
console.log(`${DRY_RUN ? "would update" : "updated"} ${first.updated} parts in ${CONTENT}`);
console.log(`powered (powerConsumption > 0): ${first.powered}; engines (enginePower > 0): ${first.engines}`);
