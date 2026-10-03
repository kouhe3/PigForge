#!/usr/bin/env node
// Extracts the original's per-placed-part MASS for the families PigForge models as one body
// whose mass is the sum of the bodies the original materializes at START. Reads the BPLE Unity
// project read-only and prints a JSON report on stdout; `apply-mass.mjs` turns that report into
// content/parts.json. Companion of tools/bple-shapes (same prefab mapping), see ADR-005/ADR-011.
//
// Usage:
//   node tools/bple-mass/extract-mass.mjs [--bple <dir>] [--out <file>]
//   node tools/bple-mass/apply-mass.mjs [--report <file>] [--content <file>] [--dry-run]
//
// Provenance (verified against BPLE source):
//   - Sandbag materializes one rigidbody clone per charged bag, recursively, in
//     `Sandbag.Initialize` / `Contraption.StartContraption` (`Sandbag.cs:112-120`,
//     `Contraption.cs:2580`): a placed part yields `m_numberOfBalloons` bodies, each carrying the
//     prefab's `m_mass`. The original's mass for one placed part is therefore
//     `m_mass * m_numberOfBalloons`.
//   - The serialized fields live on the prefab: `m_mass` on the BasePart component
//     (`BasePart.cs`), `m_numberOfBalloons` on the part script (`Sandbag.cs:10`; `Balloon.cs:15`
//     declares the same name, which is why the report emits it for every prefab that has it).
//   - PigForge content masses are its own calibrated scale, not a uniform multiple of the
//     original (wooden block 1.0 vs 0.5, wheel 0.5 vs 0.75, sandbag 3.0 vs 1.1, ...), so the
//     tool never invents a global converter: it anchors each FAMILY on the PigForge base row's
//     existing mass and carries the original's family ratios over (`total / anchorTotal`).
//   - `Part_Rope_05..08_SET` are HingePlates outside GameData.m_customParts and have no content
//     row (see tools/bple-shapes/extract-shapes.mjs); they are absent from this report too.

import { existsSync, readFileSync, writeFileSync } from "node:fs";
import { basename, dirname, join, resolve } from "node:path";
import { fileURLToPath } from "node:url";

const HERE = dirname(fileURLToPath(import.meta.url));
const REPO = resolve(HERE, "..", "..");

function arg(name, fallback) {
  const index = process.argv.indexOf(`--${name}`);
  return index >= 0 && process.argv[index + 1] ? process.argv[index + 1] : fallback;
}

const BPLE = resolve(arg("bple", process.env.BPLE_ROOT ?? join(REPO, "..", "BPLE_Unity6")));
const OUT = arg("out", "");
const GAMEOBJECT = join(BPLE, "Assets", "GameObject");

if (!existsSync(GAMEOBJECT)) {
  console.error(`BPLE project not found: ${GAMEOBJECT}`);
  process.exit(1);
}

function field(body, name) {
  const match = new RegExp(`^\\s*${name}:\\s*(.*)$`, "m").exec(body);
  return match ? match[1].trim() : undefined;
}

/** `m_mass` and `m_numberOfBalloons` of one prefab; both are plain serialized scalars. */
function readPrefabMass(name) {
  const path = join(GAMEOBJECT, `${name}.prefab`);
  if (!existsSync(path)) return null;
  const text = readFileSync(path, "utf8");
  const mass = field(text, "m_mass");
  const count = field(text, "m_numberOfBalloons");
  return {
    name,
    mass: mass === undefined ? null : Number(mass),
    count: count === undefined ? null : Number(count),
  };
}

/**
 * Families PigForge models as one body with the summed mass. `anchorPartTypeId` names the
 * PigForge base row whose existing content mass is the family's calibration anchor.
 */
const FAMILIES = [
  {
    name: "sandbag",
    pattern: /^Part_Sandbags?(2|3)?_\d+_SET$/,
    anchorPartTypeId: 21,
    source: "Sandbag.cs:112-120 (clone loop) + Contraption.cs:2580",
  },
];

const map = JSON.parse(readFileSync(join(REPO, "tools", "bple-textures", "part-map.json"), "utf8"));
const assignments = { ...map.parts, ...map.variants };
const warnings = [];
const families = {};

for (const family of FAMILIES) {
  const members = [];
  for (const [partTypeId, prefabName] of Object.entries(assignments)) {
    if (typeof prefabName !== "string" || !family.pattern.test(prefabName)) continue;
    const prefab = readPrefabMass(prefabName);
    if (!prefab) throw new Error(`${family.name}: mapped prefab missing on disk: ${prefabName} (part ${partTypeId})`);
    if (prefab.mass === null) throw new Error(`${family.name}: prefab declares no m_mass: ${prefabName}`);
    if (prefab.count === null) throw new Error(`${family.name}: prefab declares no m_numberOfBalloons: ${prefabName}`);
    members.push({
      partTypeId: Number(partTypeId),
      prefab: prefab.name,
      mass: prefab.mass,
      count: prefab.count,
      total: Number((prefab.mass * prefab.count).toFixed(6)),
    });
  }
  members.sort((left, right) => left.partTypeId - right.partTypeId);
  const anchor = members.find((member) => member.partTypeId === family.anchorPartTypeId);
  if (!anchor) throw new Error(`${family.name}: anchor part ${family.anchorPartTypeId} is not mapped by the pattern`);
  if (anchor.total <= 0) throw new Error(`${family.name}: anchor ${anchor.prefab} has a non-positive total mass`);
  if (members.length === 0) warnings.push(`${family.name}: no mapped prefab matched ${family.pattern}`);
  families[family.name] = {
    name: family.name,
    anchorPartTypeId: family.anchorPartTypeId,
    pattern: String(family.pattern),
    source: family.source,
    anchor,
    members,
  };
}

const report = {
  format: "pigforge.bple-part-mass",
  schemaVersion: 1,
  source: BPLE,
  families,
  warnings,
};
const text = `${JSON.stringify(report, null, 2)}\n`;
if (OUT) {
  writeFileSync(resolve(OUT), text);
  console.log(`wrote ${resolve(OUT)}`);
} else {
  process.stdout.write(text);
}
