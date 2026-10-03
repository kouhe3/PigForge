#!/usr/bin/env node
// Writes the extracted original physics materials of every mapped part into content/parts.json,
// preserving the file's hand-authored formatting everywhere else. Companion to
// extract-materials.mjs; the report is the ONLY admissible source for `material.restitution` and
// `material.friction` (ADR-010 decision 7).
//
// Fields written (both inside the part's `material` object, in this order):
//   - restitution: the original material's Unity `bounciness`.
//   - friction: the original material's Unity `dynamicFriction`.
//
// Per-part value resolution (auditable, no guessing):
//   - status `single`/`mixed`: the report row's `originalBounciness` /
//     `originalDynamicFriction`. For `mixed` the row carries the scalar of the material used by
//     the most colliders (the report documents this collapse); the per-collider materials stay
//     in `diff[].originalMaterials`. Our content model has one material per part, so a mixed
//     prefab (every wheel: box body -> Contraption_PhysMat, sphere tyre ->
//     Contraption_*WheelFriction_PhysMat) cannot keep both. This is a loss, not a decision to
//     reimplement per-shape materials.
//   - status `noMaterial` (no PhysicMaterial asset on any collider, or no collider at all in the
//     prefab because a runtime script builds it): the report's recorded `unityDefaultMaterial`
//     (bounciness 0, dynamic friction 0.6), i.e. Unity's built-in fallback the original actually
//     ran with. The report never invents numbers for these.
//   - status `prefabNotMapped` (static level geometry authored directly in content, e.g.
//     `terrain-box` / `ramp-plank`), and parts absent from the report entirely (`ground-slab`):
//     nothing extracted to write, so the part is left exactly as authored and listed as skipped.
//
// Friction semantics (the combine mode is NOT modelled -- do not read this as parity):
//   Unity's PhysicMaterial carries two friction coefficients (static + dynamic) and a
//   `frictionCombine` mode (Average / Minimum / Multiply / Maximum) used to blend the two
//   colliding materials. Our content and both backends carry ONE coefficient per material:
//   `PhysicsMaterial.Friction`. The value written here is the original material's OWN dynamic
//   (sliding) coefficient; the static/dynamic split is lost, and BepuPhysics v2 applies
//   whatever single `PairMaterialProperties.FrictionCoefficient` the backend computes --
//   BepuPhysicsWorld.CombineFriction averages the two bodies' coefficients (see the comment
//   there), which reproduces Unity's default `Average` combine but silently approximates
//   `Multiply` (wheel tyres: Contraption_WheelFriction_PhysMat / the wooden variant, and the
//   ice ground) and `Minimum` (Contraption_PhysMat_Alien) as if they were Average. `Maximum`
//   exists for bounce, not friction, and restitution is handled by ADR-010's rules layer.
//
// Usage:
//   node tools/bple-materials/apply-materials.mjs [--report <file>] [--content <file>] [--dry-run]

import { readFileSync, writeFileSync } from "node:fs";
import { dirname, join, resolve } from "node:path";
import { fileURLToPath } from "node:url";

const HERE = dirname(fileURLToPath(import.meta.url));
const REPO = resolve(HERE, "..", "..");

function arg(name, fallback) {
  const index = process.argv.indexOf(`--${name}`);
  return index >= 0 && process.argv[index + 1] ? process.argv[index + 1] : fallback;
}

const REPORT = resolve(arg("report", join(REPO, "tasks", "bple-materials-report.json")));
const CONTENT = resolve(arg("content", join(REPO, "content", "parts.json")));
const DRY_RUN = process.argv.includes("--dry-run");

const report = JSON.parse(readFileSync(REPORT, "utf8"));
const unityDefault = report.unityDefaultMaterial;
if (!unityDefault || !Number.isFinite(unityDefault.bounciness) || !Number.isFinite(unityDefault.dynamicFriction)) {
  throw new Error(`report is missing unityDefaultMaterial: ${JSON.stringify(unityDefault)}`);
}

const num = (value) => (Number.isInteger(value) ? String(value) : String(Number(value.toFixed(4))));

const COMBINE_NAMES = ["average", "minimum", "multiply", "maximum"];

/** The report carries the combine either as Unity's index or as its title-case name. */
const combineNameOf = (value) => {
  if (typeof value === "number") return COMBINE_NAMES[value] ?? "average";
  if (typeof value === "string" && value.length > 0) return value.toLowerCase();
  return "average";
};

/**
 * The material of the colliders that actually make up a part's physics body. A wheel's body is
 * its tyre spheres (ADR-009) and its support box rides the parent body, so a wheel takes the
 * tyre's material: the hub's `Contraption_PhysMat` (Average, 0.7) only ever touches the parent.
 * Everything else keeps the report's primary material (the one most colliders use).
 */
function bodyMaterialOf(row, part) {
  if (part?.capabilities?.wheel !== true) return null;
  const colliders = report.prefabs?.[row.prefab]?.colliders ?? [];
  const tyre = colliders.find((collider) => collider.kind === "sphere" && !collider.isTrigger && collider.hasMaterial);
  return tyre ? report.materials[tyre.materialName] ?? null : null;
}

/**
 * The material the report admits for one part, or null when the report has no extracted source
 * for it (`prefabNotMapped`, or no row at all). Throws on a status the extractor should never
 * emit with usable numbers, so a report-format change fails loudly instead of writing garbage.
 */
function desiredMaterial(row, part) {
  if (!row) return null;
  if (!row.originalPrefabFound) return null; // prefabNotMapped: authored content, not extracted
  if (row.originalStatus === "noMaterial") {
    return {
      restitution: unityDefault.bounciness,
      friction: unityDefault.dynamicFriction,
      frictionCombine: combineNameOf(unityDefault.frictionCombine),
      unityDefault: true,
    };
  }

  if (row.originalStatus !== "single" && row.originalStatus !== "mixed") {
    throw new Error(`part ${row.partTypeId}: unexpected originalStatus ${JSON.stringify(row.originalStatus)}`);
  }

  const body = bodyMaterialOf(row, part);
  if (body) {
    if (!Number.isFinite(body.bounciness) || !Number.isFinite(body.dynamicFriction)) {
      throw new Error(`part ${row.partTypeId}: body material without numeric values: ${JSON.stringify(body)}`);
    }

    return {
      restitution: body.bounciness,
      friction: body.dynamicFriction,
      frictionCombine: combineNameOf(body.frictionCombine),
      unityDefault: false,
    };
  }

  if (!Number.isFinite(row.originalBounciness) || !Number.isFinite(row.originalDynamicFriction)) {
    throw new Error(`part ${row.partTypeId}: ${row.originalStatus} row without numeric values: ${JSON.stringify(row)}`);
  }

  return {
    restitution: row.originalBounciness,
    friction: row.originalDynamicFriction,
    frictionCombine: combineNameOf(row.originalFrictionCombine),
    unityDefault: false,
  };
}

/** partTypeId -> desired material (null = leave untouched), derived only from the report. */
const contentParts = new Map(JSON.parse(readFileSync(CONTENT, "utf8")).parts.map((part) => [String(part.partTypeId), part]));
const desired = new Map();
for (const row of report.diff ?? []) {
  desired.set(String(row.partTypeId), desiredMaterial(row, contentParts.get(String(row.partTypeId))));
}

// Average is Unity's default and the pre-extraction behaviour, so it stays implicit; only a
// mode that actually changes the blend is written.
const materialText = (value) => (value.frictionCombine === "average"
  ? `{ "restitution": ${num(value.restitution)}, "friction": ${num(value.friction)} }`
  : `{ "restitution": ${num(value.restitution)}, "friction": ${num(value.friction)}, "frictionCombine": ${JSON.stringify(value.frictionCombine)} }`);

/** Rewrites one document text with the report's material values, preserving everything else. */
function rewrite(text) {
  const document = JSON.parse(text);
  const before = new Map(document.parts.map((part) => [String(part.partTypeId), JSON.stringify(part.material ?? null)]));
  let updated = 0;
  let changed = 0;
  let unityDefaultParts = 0;
  const skipped = [];

  for (const part of document.parts) {
    const key = String(part.partTypeId);
    const want = desired.get(key) ?? null;
    if (!want) {
      skipped.push({ partTypeId: part.partTypeId, name: part.name, mapped: desired.has(key) });
      continue;
    }

    const rendered = materialText(want);
    const anchor = `"partTypeId": ${part.partTypeId},`;
    const anchorIndex = text.indexOf(anchor);
    if (anchorIndex < 0) throw new Error(`anchor missing for part ${part.partTypeId}`);
    const shapesIndex = text.indexOf('"shapes":', anchorIndex);
    if (shapesIndex < 0) throw new Error(`shapes missing for part ${part.partTypeId}`);

    const materialIndex = text.indexOf('"material":', anchorIndex);
    const hasMaterial = materialIndex >= 0 && materialIndex < shapesIndex;

    if (hasMaterial) {
      const open = text.indexOf("{", materialIndex);
      const close = text.indexOf("}", open);
      if (open < 0 || close < 0 || text.slice(open, close).includes("\n")) {
        throw new Error(`part ${part.partTypeId}: expected a single-line material object`);
      }

      text = text.slice(0, open) + rendered + text.slice(close + 1);
    } else {
      const capabilitiesIndex = text.indexOf('"capabilities":', anchorIndex);
      const target = capabilitiesIndex >= 0 && capabilitiesIndex < shapesIndex ? capabilitiesIndex : shapesIndex;
      const lineStart = text.lastIndexOf("\n", target) + 1;
      text = `${text.slice(0, lineStart)}      "material": ${rendered},\n${text.slice(lineStart)}`;
    }

    const wantMaterial = want.frictionCombine === "average"
      ? { restitution: want.restitution, friction: want.friction }
      : { restitution: want.restitution, friction: want.friction, frictionCombine: want.frictionCombine };
    if (before.get(key) !== JSON.stringify(wantMaterial)) changed += 1;
    if (want.unityDefault) unityDefaultParts += 1;
    updated += 1;
  }

  return { text, updated, changed, unityDefaultParts, skipped, before };
}

// Re-parse and re-derive: the rewrite must be valid JSON and exactly match the report, and every
// part the report has no source for must be byte-identical to what it was.
function verify(result) {
  const document = JSON.parse(result.text);
  for (const part of document.parts) {
    const key = String(part.partTypeId);
    const want = desired.get(key) ?? null;
    const material = part.material ?? null;

    if (!want) {
      if (JSON.stringify(material) !== result.before.get(key)) {
        throw new Error(`part ${part.partTypeId}: skipped part was modified`);
      }

      continue;
    }

    const wantCombine = want.frictionCombine === "average" ? undefined : want.frictionCombine;
    if (material === null
      || material.restitution !== want.restitution
      || material.friction !== want.friction
      || material.frictionCombine !== wantCombine) {
      throw new Error(
        `part ${part.partTypeId}: material mismatch ${JSON.stringify(material)} != ${JSON.stringify(want)}`,
      );
    }
  }

  return document;
}

const first = rewrite(readFileSync(CONTENT, "utf8"));
verify(first);

// Idempotence: a second application over the rewritten text must be a byte-for-byte no-op.
const second = rewrite(first.text);
if (second.text !== first.text) throw new Error("rewrite is not idempotent");

if (!DRY_RUN) writeFileSync(CONTENT, first.text);

const tally = (pick, format = num) => {
  const counts = new Map();
  for (const part of JSON.parse(first.text).parts) {
    const want = desired.get(String(part.partTypeId));
    if (!want) continue;
    const value = pick(want);
    counts.set(value, (counts.get(value) ?? 0) + 1);
  }

  return [...counts]
    .sort((a, b) => (typeof a[0] === "number" && typeof b[0] === "number" ? a[0] - b[0] : String(a[0]).localeCompare(String(b[0]))))
    .map(([value, count]) => `${format(value)}×${count}`)
    .join(", ");
};

console.log(`${DRY_RUN ? "would update" : "updated"} ${first.updated} parts in ${CONTENT} (${first.changed} changed)`);
console.log(`unity default material (no PhysicMaterial in the original): ${first.unityDefaultParts}`);
console.log(`restitution: ${tally((want) => want.restitution)}`);
console.log(`friction: ${tally((want) => want.friction)}`);
console.log(`frictionCombine: ${tally((want) => want.frictionCombine, String)}`);
const mappedSkips = first.skipped.filter((part) => part.mapped);
const unmappedSkips = first.skipped.filter((part) => !part.mapped);
console.log(`skipped (report has no source): ${first.skipped.length}`);
for (const part of mappedSkips) console.log(`  - ${part.partTypeId} ${part.name} (prefab not mapped to a part)`);
for (const part of unmappedSkips) console.log(`  - ${part.partTypeId} ${part.name} (absent from the report)`);
