#!/usr/bin/env node
// Writes the summed-mass value of the multi-body families into content/parts.json from the
// bple-mass report, preserving the file's hand-authored formatting everywhere else. The report
// is the ONLY admissible source: a row whose prefab the report does not carry is a hard error,
// never a silent fallback. Idempotent and `--dry-run`-able.
//
// Formula (report-driven):
//   contentMass(row) = anchorContentMass(family) * row.total / anchor.total
// where `total = prefab.m_mass * prefab.m_numberOfBalloons` is the mass the original gives one
// placed part (one clone per bag). The anchor is the family's base row, whose PigForge mass is
// the calibration constant — the original family ratios are what changes.
//
// Usage:
//   node tools/bple-mass/apply-mass.mjs [--report <file>] [--content <file>] [--dry-run]

import { readFileSync } from "node:fs";
import { join } from "node:path";

import { applyContent } from "../lib/parts.mjs";
import { ARTIFACTS, reportFile } from "../lib/paths.mjs";
import { numInt1 } from "../lib/report.mjs";

const REPORT = reportFile(join(ARTIFACTS, "bple-part-mass.json"));

const report = JSON.parse(readFileSync(REPORT, "utf8"));
if (!report.families || Object.keys(report.families).length === 0) {
  throw new Error(`${REPORT}: no families in the report`);
}

const round4 = (value) => Number(value.toFixed(4));

applyContent({
  rewrite: (text) => {
    const document = JSON.parse(text);
    const byId = new Map(document.parts.map((part) => [part.partTypeId, part]));
    const desired = new Map();

    for (const family of Object.values(report.families)) {
      // The report's member set IS the family (base rows + their skins): every prefab the family
      // pattern matched is a content row, and each is hard-required.
      for (const member of family.members) {
        if (!byId.has(member.partTypeId)) {
          throw new Error(`${family.name}: report carries part ${member.partTypeId} (${member.prefab}) but content has no such row`);
        }
      }
      const anchor = byId.get(family.anchorPartTypeId);
      if (!anchor) throw new Error(`${family.name}: anchor row ${family.anchorPartTypeId} not in content`);
      if (!Number.isFinite(anchor.mass) || anchor.mass <= 0) {
        throw new Error(`${family.name}: anchor row ${anchor.partTypeId} has a non-positive mass (${anchor.mass})`);
      }
      for (const member of family.members) {
        desired.set(member.partTypeId, round4(anchor.mass * (member.total / family.anchor.total)));
      }
    }

    let updated = 0;
    for (const [partTypeId, mass] of [...desired.entries()].sort((left, right) => left[0] - right[0])) {
      const part = byId.get(partTypeId);
      const anchorIndex = text.indexOf(`"partTypeId": ${partTypeId},`);
      if (anchorIndex < 0) throw new Error(`anchor missing for part ${partTypeId}`);
      const nextPart = text.indexOf('"partTypeId":', anchorIndex + 1);
      const massIndex = text.indexOf('"mass":', anchorIndex);
      if (massIndex < 0 || (nextPart >= 0 && massIndex > nextPart)) throw new Error(`mass missing for part ${partTypeId}`);
      const valueStart = massIndex + '"mass":'.length;
      const valueEnd = text.indexOf(",", valueStart);
      if (valueEnd < 0) throw new Error(`mass separator missing for part ${partTypeId}`);
      const rendered = ` ${numInt1(mass)}`;
      if (text.slice(valueStart, valueEnd) !== rendered) {
        text = `${text.slice(0, valueStart)}${rendered}${text.slice(valueEnd)}`;
        updated += 1;
        console.log(`${String(partTypeId).padStart(3)} ${part.name.padEnd(22)} ${part.mass} -> ${mass}`);
      }
    }

    return { text, updated, desired };
  },
  // Re-parse and re-derive: the rewrite must be valid JSON, exactly match the report, and be
  // idempotent (a second run changes nothing).
  verify: (result) => {
    for (const part of JSON.parse(result.text).parts) {
      const entry = result.desired.get(part.partTypeId);
      if (!entry) continue;
      if (part.mass !== entry) throw new Error(`part ${part.partTypeId}: wrote ${part.mass}, expected ${entry}`);
    }
  },
  report: ({ updated }, _verified, { dryRun, content }) => {
    console.log(`${dryRun ? "would update" : "updated"} ${updated} parts in ${content}`);
  },
});
