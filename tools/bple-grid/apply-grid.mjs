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

import { readFileSync } from "node:fs";
import { applyReport } from "../lib/paths.mjs";
import { applyContent, partSpan } from "../lib/parts.mjs";

const REPORT = applyReport("grid");

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
/** The line-anchored part-field pattern: a part-level field owns its whole line, so this scans
 * the part's own block one line at a time. It is NOT `parts.mjs`'s `propertyPattern`, which
 * matches a value inside an inline object. */
const keyLine = (key) => new RegExp(`^[^\\S\\n]*"${key}":`, "m");
const renderBox = (box) =>
  `      "gridBox": { "minX": ${box.minX}, "maxX": ${box.maxX}, "minY": ${box.minY}, "maxY": ${box.maxY} },`;

const parts = report.parts ?? {};

applyContent({
  rewrite: (text) => {
    const document = JSON.parse(text);
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
      const { anchorIndex, shapesIndex } = partSpan(text, part.partTypeId);
      const blockStart = text.indexOf("\n", anchorIndex) + 1;
      const shapesLine = text.lastIndexOf("\n", shapesIndex) + 1;
      const found = keyLine("gridBox").exec(text.slice(blockStart, shapesLine));
      const boxLine = found === null ? -1 : blockStart + found.index;

      if (isDefault(box)) {
        if (boxLine >= 0) {
          text = text.slice(0, boxLine) + text.slice(text.indexOf("\n", boxLine) + 1);
          removed += 1;
          updatedPartIds.add(part.partTypeId);
        } else {
          untouched += 1;
        }

        continue;
      }

      if (boxLine >= 0) {
        text = `${text.slice(0, boxLine)}${renderBox(box)}${text.slice(text.indexOf("\n", boxLine))}`;
      } else {
        text = `${text.slice(0, shapesLine)}${renderBox(box)}\n${text.slice(shapesLine)}`;
      }

      written += 1;
      updatedPartIds.add(part.partTypeId);
    }

    return { text, written, removed, untouched, updatedPartIds };
  },
  // Re-parse and re-derive: the rewrite must be valid JSON, exactly match the report, and be
  // idempotent (a second run changes nothing).
  verify: (result) => {
    const check = JSON.parse(result.text);
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

    return { declared, expectedDeclared };
  },
  report: ({ updatedPartIds, written, removed, untouched }, { declared, expectedDeclared }, { dryRun, content }) => {
    console.log(`${dryRun ? "would update" : "updated"} ${updatedPartIds.size} parts in ${content}`);
    console.log(`gridBox written: ${written}; removed: ${removed}; left default/absent: ${untouched}`);
    console.log(`declared gridBox in document: ${declared} (report non-default: ${expectedDeclared})`);
  },
});
