#!/usr/bin/env node
// Writes the extracted build-grid cell box of every mapped part into content/parts.json,
// preserving the file's hand-authored formatting everywhere else. Companion to
// extract-grid.mjs; the report is the ONLY admissible source for the cell box.
//
// Field written: the part-level `gridBox` object
//   { "minX": -1, "maxX": 1, "minY": 0, "maxY": 1 }
// -- the original's inclusive cell rectangle around the part's own grid coordinate
// (BasePart.cs:197-200, ConstructionUI.cs:1279,1344). It is written ONLY when the prefab
// declares something other than the original's default (one cell at the origin, 332 of 343
// prefabs); a default part carries no key, and parser/client read absence as that default.
// That mirrors how `capabilities.canEnclose` is written only for frames.
//
// Usage:
//   node tools/bple-grid/apply-grid.mjs [--report <file>] [--content <file>] [--dry-run]

import { readFileSync, writeFileSync } from "node:fs";
import { dirname, join, resolve } from "node:path";
import { fileURLToPath } from "node:url";

const HERE = dirname(fileURLToPath(import.meta.url));
const REPO = resolve(HERE, "..", "..");

function arg(name, fallback) {
  const index = process.argv.indexOf(`--${name}`);
  return index >= 0 && process.argv[index + 1] ? process.argv[index + 1] : fallback;
}

const REPORT = resolve(arg("report", join(REPO, "tasks", "bple-grid-report.json")));
const CONTENT = resolve(arg("content", join(REPO, "content", "parts.json")));
const DRY_RUN = process.argv.includes("--dry-run");

const report = JSON.parse(readFileSync(REPORT, "utf8"));

/** The box the report declares for one part; the report is the only admissible source, so a
 * missing or malformed box is a hard error rather than a silent fallback. */
function boxOf(entry, partTypeId) {
  const box = entry.gridBox;
  if (
    typeof box !== "object"
    || box === null
    || !["minX", "maxX", "minY", "maxY"].every((key) => Number.isInteger(box[key]))
    || box.minX > box.maxX
    || box.minY > box.maxY
  ) {
    throw new Error(`part ${partTypeId}: malformed gridBox ${JSON.stringify(box)}`);
  }

  return box;
}

const isDefault = (box) => box.minX === 0 && box.maxX === 0 && box.minY === 0 && box.maxY === 0;
const propertyPattern = (key) => new RegExp(`^\\s*"${key}":`);
const renderBox = (box) =>
  `      "gridBox": { "minX": ${box.minX}, "maxX": ${box.maxX}, "minY": ${box.minY}, "maxY": ${box.maxY} },`;

const parts = report.parts ?? {};
const document = JSON.parse(readFileSync(CONTENT, "utf8"));
let text = readFileSync(CONTENT, "utf8");
let lines = text.split("\n");

let written = 0;
let removed = 0;
let untouched = 0;
const updatedPartIds = new Set();

for (const part of document.parts) {
  const entry = parts[String(part.partTypeId)];
  if (!entry) {
    continue; // unmapped partTypeId (static level geometry): nothing extracted to write
  }

  const box = boxOf(entry, part.partTypeId);
  const anchorIndex = lines.findIndex((line) => line === `      "partTypeId": ${part.partTypeId},`);
  if (anchorIndex < 0) {
    throw new Error(`anchor missing for part ${part.partTypeId}`);
  }

  const shapesIndex = lines.findIndex((line, index) => index > anchorIndex && /^\s*"shapes":/.test(line));
  if (shapesIndex < 0) {
    throw new Error(`shapes missing for part ${part.partTypeId}`);
  }

  const boxIndex = lines.findIndex((line, index) => index > anchorIndex && index < shapesIndex && propertyPattern("gridBox").test(line));
  if (isDefault(box)) {
    if (boxIndex >= 0) {
      lines.splice(boxIndex, 1);
      removed += 1;
      updatedPartIds.add(part.partTypeId);
    } else {
      untouched += 1;
    }

    continue;
  }

  if (boxIndex >= 0) {
    lines[boxIndex] = renderBox(box);
  } else {
    lines.splice(shapesIndex, 0, renderBox(box));
  }

  written += 1;
  updatedPartIds.add(part.partTypeId);
}

text = lines.join("\n");

// Re-parse and re-derive: the rewrite must be valid JSON, exactly match the report, and be
// idempotent (a second run changes nothing).
const check = JSON.parse(text);
let declared = 0;
for (const part of check.parts) {
  const entry = parts[String(part.partTypeId)];
  const box = part.gridBox;
  if (!entry) {
    if (box !== undefined) {
      throw new Error(`part ${part.partTypeId}: gridBox on an unmapped part`);
    }

    continue;
  }

  const expected = boxOf(entry, part.partTypeId);
  if (isDefault(expected)) {
    if (box !== undefined) {
      throw new Error(`part ${part.partTypeId}: default gridBox must be absent, got ${JSON.stringify(box)}`);
    }

    continue;
  }

  if (
    box === undefined
    || box.minX !== expected.minX
    || box.maxX !== expected.maxX
    || box.minY !== expected.minY
    || box.maxY !== expected.maxY
  ) {
    throw new Error(`part ${part.partTypeId}: gridBox mismatch ${JSON.stringify(box)} != ${JSON.stringify(expected)}`);
  }

  declared += 1;
}

const expectedDeclared = Object.entries(parts).filter(([, entry]) => !isDefault(boxOf(entry, "report"))).length;
if (declared !== expectedDeclared) {
  throw new Error(`declared gridBox count: expected ${expectedDeclared}, got ${declared}`);
}

if (!DRY_RUN) writeFileSync(CONTENT, text);
console.log(`${DRY_RUN ? "would update" : "updated"} ${updatedPartIds.size} parts in ${CONTENT}`);
console.log(`gridBox written: ${written}; removed: ${removed}; left default/absent: ${untouched}`);
console.log(`declared gridBox in document: ${declared} (report non-default: ${expectedDeclared})`);
