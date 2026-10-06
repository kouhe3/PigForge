#!/usr/bin/env node
// Writes the extracted damping of `extract-damping.mjs` into content/parts.json, preserving the
// file's hand-authored formatting everywhere else. The report is the ONLY admissible source for
// these numbers, so a part the report does not cover is a hard error rather than a silent value.
//
// Written once at the document level (the original's project default plus the pair
// `BasePart.EnsureRigidbody` gives every part):
//   "physics": { "maximumAngularSpeed": <Unity Physics.defaultMaxAngularSpeed>,
//                "damping": { "linear": <drag>, "angular": <angularDrag> } }
// and per part only where the original's class overrides that pair -- a wing, a tail, a balloon, a
// sandbag, the king pig -- which is 62 of the 264 dynamic parts:
//   "damping": { "linear": 1.0, "angular": 0.2 }
// A stale override that equals the document default is removed, so the file states each fact once.
// A static part carries nothing: the original's static level pieces are not rigidbodies at all.
//
// A class that rewrites its own damping every fixed step (`Pig.FixedUpdate`) carries one more key,
// patched into its existing `capabilities` line:
//   "dampingRamp": { "speedThreshold": 1.0, "base": 0.2, "slope": 2.5 }
// which reads as "while running, below `speedThreshold` m/s both drags are
// `base + slope * (1 - |v|)`, otherwise the class's own spawn pair".
//
// Usage:
//   node tools/bple-damping/apply-damping.mjs [--report <file>] [--content <file>] [--dry-run]

import { readFileSync, writeFileSync } from "node:fs";
import { dirname, join, resolve } from "node:path";
import { fileURLToPath } from "node:url";

const HERE = dirname(fileURLToPath(import.meta.url));
const REPO = resolve(HERE, "..", "..");

function arg(name, fallback) {
  const index = process.argv.indexOf(`--${name}`);
  return index >= 0 && process.argv[index + 1] ? process.argv[index + 1] : fallback;
}

const REPORT = resolve(arg("report", join(REPO, "tasks", "bple-damping-report.json")));
const CONTENT = resolve(arg("content", join(REPO, "content", "parts.json")));
const DRY_RUN = process.argv.includes("--dry-run");

const report = JSON.parse(readFileSync(REPORT, "utf8"));
const entries = report.parts ?? {};
if (Object.keys(entries).length === 0) {
  throw new Error(`${REPORT} carries no parts -- run extract-damping.mjs first`);
}
const clamp = report.physics?.maximumAngularSpeed;
const defaultDamping = report.physics?.damping;
if (typeof clamp !== "number" || !Number.isFinite(clamp) || clamp <= 0) {
  throw new Error(`${REPORT} carries no usable physics.maximumAngularSpeed (${clamp})`);
}
if (typeof defaultDamping?.linear !== "number" || typeof defaultDamping?.angular !== "number") {
  throw new Error(`${REPORT} carries no usable physics.damping (${JSON.stringify(defaultDamping)})`);
}

/** JSON with at most six decimals and an explicit `.0` for an integral value -- the same style the
 * hand-authored content uses (`2.0`, `0.05`), so an extracted float never looks like an int. */
const num = (value) => (Number.isInteger(value) ? `${value.toFixed(1)}` : `${Number(value.toFixed(6))}`);

const renderDamping = (entry) => `{ "linear": ${num(entry.linear)}, "angular": ${num(entry.angular)} }`;
const isDefault = (entry) => entry.linear === defaultDamping.linear && entry.angular === defaultDamping.angular;
const renderRamp = (ramp) =>
  `"dampingRamp": { "speedThreshold": ${num(ramp.speedThreshold)}, "base": ${num(ramp.base)}, "slope": ${num(ramp.slope)} }`;
const RAMP_KEY = /"dampingRamp":\s*\{[^{}]*\}/;

const document_ = JSON.parse(readFileSync(CONTENT, "utf8"));
if (!Array.isArray(document_.parts)) {
  throw new Error(`${CONTENT} is not a part-content document`);
}

const lines = readFileSync(CONTENT, "utf8").split("\n");

/** The line span of every entry in the `parts` array, found by brace depth: an entry opens with a
 * lone `{` one level inside the array. */
function entrySpans() {
  const spans = [];
  let depth = 0;
  let start = -1;
  for (let index = 0; index < lines.length; index++) {
    const trimmed = lines[index].trim();
    const before = depth;
    depth += (lines[index].match(/\{/g) ?? []).length - (lines[index].match(/\}/g) ?? []).length;
    if (before === 1 && depth === 2 && trimmed === "{") {
      start = index;
    } else if (before === 2 && depth === 1 && (trimmed === "}," || trimmed === "}")) {
      spans.push([start, index]);
    }
  }
  return spans;
}

const spans = entrySpans();
if (spans.length !== document_.parts.length) {
  throw new Error(`found ${spans.length} entry spans for ${document_.parts.length} parts in ${CONTENT}`);
}

const physicsLine = `  "physics": { "maximumAngularSpeed": ${num(clamp)}, "damping": ${renderDamping(defaultDamping)} },`;

let updated = 0;
let unchanged = 0;
const overrides = new Map();
const ramps = new Map();
const covered = new Set();

// Top level: the `physics` block goes right after `contentVersion`, and a stale one is replaced.
const contentVersionIndex = lines.findIndex((line) => /^\s*"contentVersion":/.test(line));
if (contentVersionIndex < 0) {
  throw new Error(`${CONTENT} declares no contentVersion line`);
}
const physicsIndex = lines.findIndex((line) => /^\s*"physics":/.test(line));
if (physicsIndex >= 0) {
  if (lines[physicsIndex] === physicsLine) {
    unchanged++;
  } else {
    lines[physicsIndex] = physicsLine;
    updated++;
  }
} else {
  lines.splice(contentVersionIndex + 1, 0, physicsLine);
  updated++;
  for (const span of spans) {
    span[0]++;
    span[1]++;
  }
}

for (const span of spans) {
  const [start, end] = span;
  const within = (pattern) => lines.slice(start, end + 1).findIndex((line) => pattern.test(line));
  const partTypeIdIndex = within(/^\s*"partTypeId":/);
  if (partTypeIdIndex < 0) {
    throw new Error(`entry at line ${start + 1} declares no partTypeId`);
  }
  const partTypeId = Number(/^\s*"partTypeId":\s*(\d+)/.exec(lines[start + partTypeIdIndex])[1]);
  const modeIndex = within(/^\s*"mode":/);
  if (modeIndex < 0) {
    throw new Error(`part ${partTypeId} declares no mode`);
  }
  const mode = /^\s*"mode":\s*"(\w+)"/.exec(lines[start + modeIndex])[1];
  const existingIndex = within(/^\s*"damping":/);
  const dampingLine = existingIndex >= 0 ? start + existingIndex : -1;

  if (mode !== "dynamic") {
    if (dampingLine >= 0) {
      throw new Error(`static part ${partTypeId} carries a damping key; a static body has none`);
    }
    continue;
  }

  covered.add(partTypeId);
  const entry = entries[String(partTypeId)];
  if (!entry) {
    throw new Error(`part ${partTypeId} is dynamic but the report carries no damping for it`);
  }

  // The original's runtime ramp (`Pig.FixedUpdate`) rides inside `capabilities`, which the content
  // writes on one line, so this is a patch of that line rather than a new key.
  const capabilitiesIndex = within(/^\s*"capabilities":\s*\{/);
  if (capabilitiesIndex >= 0) {
    const lineIndex = start + capabilitiesIndex;
    const line = lines[lineIndex];
    const existing = RAMP_KEY.test(line);
    let patched = line;
    if (!entry.dampingRamp) {
      patched = existing ? line.replace(/,\s*"dampingRamp":\s*\{[^{}]*\}/, "") : line;
    } else if (existing) {
      patched = line.replace(RAMP_KEY, renderRamp(entry.dampingRamp));
    } else {
      const close = line.lastIndexOf("}");
      patched = `${line.slice(0, close).replace(/\s+$/, "")}, ${renderRamp(entry.dampingRamp)} }${line.slice(close + 1)}`;
    }
    if (patched !== line) {
      lines[lineIndex] = patched;
      updated++;
    }
    ramps.set(partTypeId, entry.dampingRamp ?? null);
  } else if (entry.dampingRamp) {
    throw new Error(`part ${partTypeId} ramps its damping but declares no capabilities block`);
  }

  // Only an override is written; anything equal to the document default stays implicit.
  const materialIndex = within(/^\s*"material":/) >= 0 ? start + within(/^\s*"material":/) : start + within(/^\s*"mass":/);
  if (materialIndex < start || materialIndex > end) {
    throw new Error(`part ${partTypeId} declares neither material nor mass to anchor the damping key to`);
  }
  const rendered = isDefault(entry) ? null : `${/^\s*/.exec(lines[materialIndex])[0]}"damping": ${renderDamping(entry)},`;
  const anchor = materialIndex;

  if (rendered === null) {
    if (dampingLine >= 0) {
      lines.splice(dampingLine, 1);
      updated++;
      for (const other of spans) {
        if (other[0] > start) {
          other[0]--;
          other[1]--;
        }
      }
    } else {
      unchanged++;
    }
    continue;
  }

  if (dampingLine >= 0) {
    if (lines[dampingLine] === rendered) {
      unchanged++;
    } else {
      lines[dampingLine] = rendered;
      updated++;
    }
  } else {
    lines.splice(anchor + 1, 0, rendered);
    updated++;
    for (const other of spans) {
      if (other[0] > start) {
        other[0]++;
        other[1]++;
      }
    }
  }
  overrides.set(partTypeId, entry);
}

if (covered.size !== Object.keys(entries).length) {
  throw new Error(`content declares ${covered.size} dynamic parts, the report ${Object.keys(entries).length}`);
}

const text = lines.join("\n");

// Re-parse and re-derive: the rewrite must be valid JSON and resolve to the report's values.
const check = JSON.parse(text);
if (check.physics?.maximumAngularSpeed !== clamp) {
  throw new Error(`rewritten content carries physics.maximumAngularSpeed ${check.physics?.maximumAngularSpeed}, expected ${clamp}`);
}
if (check.physics?.damping?.linear !== defaultDamping.linear || check.physics?.damping?.angular !== defaultDamping.angular) {
  throw new Error(`rewritten content carries physics.damping ${JSON.stringify(check.physics?.damping)}, expected ${renderDamping(defaultDamping)}`);
}
if (check.parts.length !== document_.parts.length) {
  throw new Error(`rewritten content has ${check.parts.length} parts, expected ${document_.parts.length}`);
}
for (const part of check.parts) {
  const entry = entries[String(part.partTypeId)];
  if (part.mode !== "dynamic") {
    if (part.damping !== undefined) {
      throw new Error(`rewritten part ${part.partTypeId} is static but carries damping`);
    }
    continue;
  }
  if (!entry) {
    throw new Error(`rewritten part ${part.partTypeId} has no report entry`);
  }
  const effective = part.damping ?? defaultDamping;
  if (effective.linear !== entry.linear || effective.angular !== entry.angular) {
    throw new Error(`rewritten part ${part.partTypeId} resolves to ${renderDamping(effective)}, expected ${renderDamping(entry)}`);
  }
  const ramp = part.capabilities?.dampingRamp;
  const expectedRamp = entry.dampingRamp;
  if ((ramp === undefined) !== (expectedRamp === undefined)) {
    throw new Error(`rewritten part ${part.partTypeId} carries ${JSON.stringify(ramp)}, expected ${JSON.stringify(expectedRamp)}`);
  }
  if (ramp !== undefined
    && (ramp.speedThreshold !== expectedRamp.speedThreshold || ramp.base !== expectedRamp.base || ramp.slope !== expectedRamp.slope)) {
    throw new Error(`rewritten part ${part.partTypeId} ramps ${JSON.stringify(ramp)}, expected ${JSON.stringify(expectedRamp)}`);
  }
}

if (!DRY_RUN) {
  writeFileSync(CONTENT, text);
}

console.log(`${DRY_RUN ? "would update" : "updated"} ${updated} lines in ${CONTENT}`);
console.log(
  `already matching: ${unchanged}; overrides written: ${overrides.size}; ramps written: ${[...ramps.values()].filter(Boolean).length}; dynamic parts verified: ${covered.size}; clamp ${clamp}`,
);
