#!/usr/bin/env node
// Imports the original Bad Piggies variant registry (BPLE GameData.m_customParts)
// into PigForge content as flat partTypeId entries (ADR-004), appending to
// content/parts.json and rewriting the `variants` section of the texture map.
// Reads the original project read-only. Idempotent: existing entries are never
// touched, ids are only appended.
//
// Usage:
//   node tools/bple-variants/import-variants.mjs [--bple <dir>] [--dry-run]
//
// Provenance (verified against BPLE source):
//   - Assets/MonoBehaviour/GameData.asset holds `m_parts` (base prefabs) and
//     `m_customParts` (per PartType: the ordered custom-part prefab list).
//   - A prefab's `m_partType` identifies its group; `customPartIndex` its order.
//   - PigForge base parts are mapped to base prefabs by tools/bple-textures/part-map.json.
//   - Skin entries copy the base content entry (mode/mass/material/shapes/capabilities);
//     tools/bple-variants/variant-overrides.json carries the curated exceptions.
//   - `defer: true` keeps a prefab in the texture map but out of content until its
//     capability fields land.

import { existsSync, readFileSync, writeFileSync } from "node:fs";
import { basename, dirname, join, resolve } from "node:path";
import { fileURLToPath } from "node:url";
import { arg, flag } from "../lib/args.mjs";
import { ARTIFACTS, PART_MAP, bpleProject, contentFile, reportFile } from "../lib/paths.mjs";
import { fail, writeJsonArtifact } from "../lib/report.mjs";
import { indexAssetGuids } from "../lib/unity.mjs";

const HERE = dirname(fileURLToPath(import.meta.url));

const BPLE = bpleProject();
const CONTENT = contentFile();
const MAP = resolve(arg("map", PART_MAP));
const OVERRIDES = resolve(arg("overrides", join(HERE, "variant-overrides.json")));
const REPORT = reportFile(join(ARTIFACTS, "bple-variant-import-report.json"));
const DRY_RUN = flag("dry-run");
const ASSETS = join(BPLE, "Assets");
const GAMEOBJECT = join(ASSETS, "GameObject");

if (!existsSync(ASSETS) || !existsSync(GAMEOBJECT)) {
  fail(`BPLE project not found at ${BPLE} (expected Assets/GameObject).`);
}

// ---------------------------------------------------------------- guid index
const guidToPath = indexAssetGuids(ASSETS, { suffix: "" });

// --------------------------------------------------------- original registry
const gameData = readFileSync(join(ASSETS, "MonoBehaviour", "GameData.asset"), "utf8");
const customGroups = new Map();
{
  let current = null;
  for (const line of gameData.split("\n")) {
    const groupMatch = line.match(/^  - partType:\s*(\d+)/);
    if (groupMatch) {
      current = Number(groupMatch[1]);
      if (!customGroups.has(current)) customGroups.set(current, []);
      continue;
    }
    const memberMatch = line.match(/^    - \{fileID: \d+, guid: ([0-9a-f]{32})/);
    if (memberMatch && current !== null) customGroups.get(current).push(memberMatch[1]);
  }
}

function prefabName(guid) {
  const path = guidToPath.get(guid);
  return path && path.startsWith(GAMEOBJECT) ? basename(path, ".prefab") : null;
}

function readPrefab(name) {
  const path = join(GAMEOBJECT, `${name}.prefab`);
  if (!existsSync(path)) return null;
  const text = readFileSync(path, "utf8");
  const number = (pattern) => {
    const match = text.match(pattern);
    return match ? Number(match[1]) : null;
  };
  return {
    name,
    partType: number(/^\s*m_partType:\s*(\d+)/m),
    customPartIndex: number(/^\s*customPartIndex:\s*(\d+)/m),
    mass: number(/^\s*m_mass:\s*(-?[\d.eE+-]+)/m),
    // Only the `Engine` family declares this; it is what an `extras` entry may override, and the
    // assertion below keeps that override equal to the prefab instead of to a hand-written guess.
    enginePower: number(/^\s*m_enginePower:\s*(-?[\d.eE+-]+)/m),
  };
}

const groups = new Map();
for (const [partType, guids] of customGroups) {
  const members = guids
    .map((guid) => prefabName(guid))
    .filter((name) => name !== null)
    .map(readPrefab)
    .filter((prefab) => prefab !== null)
    .sort((left, right) => (left.customPartIndex ?? 999) - (right.customPartIndex ?? 999) || left.name.localeCompare(right.name));
  groups.set(partType, members);
}

// -------------------------------------------------------------- PigForge side
const contentText = readFileSync(CONTENT, "utf8");
const content = JSON.parse(contentText);
const map = JSON.parse(readFileSync(MAP, "utf8"));
const overrideDocument = JSON.parse(readFileSync(OVERRIDES, "utf8"));
const overrides = overrideDocument.variants ?? {};
const extras = overrideDocument.extras ?? [];
const baseById = new Map(content.parts.map((part) => [part.partTypeId, part]));
const existingVariantPrefabs = new Map(Object.entries(map.variants ?? {}).map(([id, name]) => [name, Number(id)]));
const usedIds = new Set([...content.parts.map((part) => part.partTypeId), ...existingVariantPrefabs.values()]);
let nextId = Math.max(...usedIds) + 1;

// Base prefab -> PigForge base part id, via part-map + prefab partType.
const basePrefabByPartType = new Map();
for (const [id, prefab] of Object.entries(map.parts)) {
  if (!prefab) continue;
  const parsed = readPrefab(prefab);
  if (parsed && parsed.partType !== null) basePrefabByPartType.set(parsed.partType, { id: Number(id), prefab: parsed });
}

const warnings = [];
const deferred = [];
const additions = [];
const variants = { ...(map.variants ?? {}) };

/** Trailing prefab number ("Part_WoodenFrame_02_SET" -> "02"); null when absent. */
function prefabSuffix(name) {
  const stem = name.replace(/^Part_/, "").replace(/_SET( \d+)?$/, "");
  const match = stem.match(/(\d+)$/);
  return match ? match[1] : null;
}

/** Keys whose values are integers in the hand-authored content (ids, fuse/duration ticks, motor direction). */
const INTEGER_KEYS = new Set(["partTypeId", "variantOf", "fuseTicks", "durationTicks", "directionX", "directionY"]);

/** Inline JSON matching content/parts.json's hand-authored style (scalars, arrays, objects on one line). */
function inlineValue(value, key) {
  if (Array.isArray(value)) return `[${value.map((item) => inlineValue(item, key)).join(", ")}]`;
  if (value !== null && typeof value === "object") {
    const rows = Object.entries(value).map(([name, item]) => `"${name}": ${inlineValue(item, name)}`);
    return `{ ${rows.join(", ")} }`;
  }
  return renderScalar(value, key);
}

function renderScalar(value, key) {
  if (typeof value === "number") {
    if (INTEGER_KEYS.has(key)) return String(Math.trunc(value));
    return Number.isInteger(value) ? value.toFixed(1) : String(value);
  }
  return JSON.stringify(value);
}

/** Shape arrays are the one block-formatted value in the file. */
function blockShapes(shapes) {
  const items = shapes.map((shape) => {
    const rows = Object.entries(shape).map(([name, value]) => `          "${name}": ${inlineValue(value, name)}`);
    return `        {\n${rows.join(",\n")}\n        }`;
  });
  return `[\n${items.join(",\n")}\n      ]`;
}

function renderEntry(entry) {
  const order = ["partTypeId", "name", "variantOf", "variantName", "mode", "mass", "material", "capabilities", "shapes"];
  const rows = [];
  for (const key of order) {
    if (!(key in entry)) continue;
    rows.push(`      "${key}": ${key === "shapes" ? blockShapes(entry[key]) : inlineValue(entry[key], key)}`);
  }
  return `    {\n${rows.join(",\n")}\n    }`;
}

function mergeCapabilities(base, override) {
  if (!base && !override) return undefined;
  return { ...(base ?? {}), ...(override ?? {}) };
}

function round4(value) {
  return Number(value.toFixed(4));
}

/** Builds a content entry: base fields copied, curated overrides applied on top. */
function buildEntry(baseEntry, id, prefab, override, suffix, usedNames) {
  let name = override.name ?? `${baseEntry.name}-v${suffix}`;
  while (usedNames.has(name)) name = `${name}-b`;
  usedNames.add(name);
  const capabilities = override.clearCapabilities
    ? mergeCapabilities(undefined, override.capabilities)
    : mergeCapabilities(baseEntry.capabilities, override.capabilities);
  const mass = override.massFactor ? round4(baseEntry.mass * override.massFactor) : baseEntry.mass;
  return {
    partTypeId: id,
    name,
    variantOf: baseEntry.partTypeId,
    ...(override.variantName ? { variantName: override.variantName } : {}),
    mode: baseEntry.mode,
    mass,
    ...(baseEntry.material ? { material: baseEntry.material } : {}),
    ...(capabilities ? { capabilities } : {}),
    shapes: baseEntry.shapes,
  };
}

for (const [partType, members] of [...groups.entries()].sort((left, right) => left[0] - right[0])) {
  const base = basePrefabByPartType.get(partType);
  if (!base) {
    // No PigForge base carries this partType, so the group cannot be imported as variants. That is
    // how the two missing engine families (PartTypes 25/26) went unnoticed: the group was skipped
    // silently. Every member must therefore be declared in `extras`; anything else is a family the
    // original has and content does not, and it says so here.
    const uncovered = members.filter((member) => !extras.some((extra) => extra.prefab === member.name));
    if (uncovered.length > 0) {
      warnings.push(`partType ${partType} has no PigForge base and ${uncovered.length}/${members.length} member(s) are not in variant-overrides.json extras: ${uncovered.map((member) => member.name).join(", ")}`);
    }
    continue;
  }
  const baseEntry = baseById.get(base.id);
  if (!baseEntry) {
    warnings.push(`base part ${base.id} (${base.prefab.name}) missing from content`);
    continue;
  }

  const usedNames = new Set();
  let ordinal = 1;
  for (const member of members) {
    ordinal++;
    if (member.name === base.prefab.name) continue;
    const override = overrides[member.name] ?? {};
    // A skin may override `enginePower` (Part_EngineSmall_05_SET is the only one that does), and the
    // number must be the prefab's own `m_enginePower` -- 5000 there, 100x its nine siblings.
    const declaredPower = override.capabilities?.enginePower;
    if (declaredPower !== undefined && member.enginePower !== null && declaredPower !== member.enginePower) {
      throw new Error(`${member.name}: override declares enginePower ${declaredPower} but the prefab says ${member.enginePower}`);
    }
    const id = existingVariantPrefabs.get(member.name) ?? nextId++;
    if (existingVariantPrefabs.has(member.name) && baseById.has(id)) {
      usedNames.add(baseById.get(id).name);
      continue; // already in content
    }

    variants[String(id)] = member.name;
    usedIds.add(id);
    if (override.defer) {
      deferred.push({ id, prefab: member.name, base: base.id });
      continue;
    }

    const entry = buildEntry(baseEntry, id, member.name, override, prefabSuffix(member.name) ?? String(ordinal), usedNames);
    additions.push({ entry, prefab: member.name, base: base.id, ordinal });
  }
}

// IN extension variants the original adds outside GameData.m_customParts (e.g. BlasterTNT).
for (const extra of extras) {
  const baseEntry = baseById.get(extra.base);
  if (!baseEntry) throw new Error(`extra ${extra.prefab}: unknown base part ${extra.base}`);
  if (baseById.has(extra.partTypeId)) {
    continue; // already in content (the generator only appends)
  }
  if (!existsSync(join(GAMEOBJECT, `${extra.prefab}.prefab`))) {
    throw new Error(`extra ${extra.prefab}: prefab not found under ${GAMEOBJECT}`);
  }

  // An extra may override `enginePower` and scale the base mass, and both are asserted against the
  // prefab: `m_enginePower` is the original's own field (Part_EngineSmall_05_SET really does say
  // 5000) and the mass factor must be the prefab's mass over the base prefab's mass. A curated
  // number that drifts from the prefab fails here instead of silently re-writing the world.
  const extraPrefab = readPrefab(extra.prefab);
  const declaredPower = extra.capabilities?.enginePower;
  if (declaredPower !== undefined && extraPrefab.enginePower !== null && declaredPower !== extraPrefab.enginePower) {
    throw new Error(`extra ${extra.prefab}: declares enginePower ${declaredPower} but the prefab says ${extraPrefab.enginePower}`);
  }
  if (extra.massFactor !== undefined) {
    const basePrefabName = map.parts[String(extra.base)];
    const basePrefab = basePrefabName ? readPrefab(basePrefabName) : null;
    if (!basePrefab?.mass) {
      throw new Error(`extra ${extra.prefab}: cannot resolve the base prefab mass of base part ${extra.base}`);
    }
    const expectedFactor = round4(extraPrefab.mass / basePrefab.mass);
    if (Math.abs(expectedFactor - extra.massFactor) > 1e-4) {
      throw new Error(`extra ${extra.prefab}: massFactor ${extra.massFactor} does not match the prefab ratio ${expectedFactor} (${extraPrefab.mass} / ${basePrefab.mass})`);
    }
  }

  variants[String(extra.partTypeId)] = extra.prefab;
  usedIds.add(extra.partTypeId);
  const entry = buildEntry(baseEntry, extra.partTypeId, extra.prefab, extra, String(extra.partTypeId), new Set());
  additions.push({ entry, prefab: extra.prefab, base: extra.base, ordinal: 0 });
}

// --------------------------------------------------------------------- checks
const ids = new Set();
for (const { entry } of additions) {
  if (ids.has(entry.partTypeId) || baseById.has(entry.partTypeId)) {
    throw new Error(`duplicate partTypeId ${entry.partTypeId} (${entry.name})`);
  }
  ids.add(entry.partTypeId);
}
for (const { entry } of additions) {
  const base = baseById.get(entry.variantOf);
  if (!base || base.variantOf !== undefined) {
    throw new Error(`variant ${entry.partTypeId} references invalid base ${entry.variantOf}`);
  }
}

// -------------------------------------------------------------------- output
const report = {
  format: "pigforge.bple-variant-import-report",
  schemaVersion: 1,
  source: BPLE,
  groups: [...groups.entries()]
    .sort((left, right) => left[0] - right[0])
    .map(([partType, members]) => ({
      partType,
      base: basePrefabByPartType.get(partType)?.id ?? null,
      members: members.map((member) => member.name),
    })),
  additions: additions.map(({ entry, prefab, base }) => ({ partTypeId: entry.partTypeId, name: entry.name, prefab, base })),
  deferred,
  warnings,
};
writeJsonArtifact(REPORT, report);

if (!DRY_RUN && additions.length > 0) {
  const closing = contentText.lastIndexOf("\n  ]\n}");
  if (closing < 0) throw new Error("content/parts.json: parts array closing not found");
  const head = contentText.slice(0, closing);
  const tail = contentText.slice(closing);
  const separator = head.trimEnd().endsWith("[") ? "\n" : ",\n";
  const body = additions.map(({ entry }) => renderEntry(entry)).join(",\n");
  writeFileSync(CONTENT, `${head}${separator}${body}${tail}`);
}

if (!DRY_RUN) {
  map.variants = Object.fromEntries(
    Object.entries(variants).sort((left, right) => Number(left[0]) - Number(right[0])),
  );
  const nextMap = `${JSON.stringify(map, null, 2)}\n`;
  if (nextMap !== readFileSync(MAP, "utf8")) {
    writeFileSync(MAP, nextMap);
  }
}

console.log(`bple:      ${BPLE}`);
console.log(`content:   ${CONTENT}`);
console.log(`additions: ${additions.length}${DRY_RUN ? " (dry run)" : ""}`);
console.log(`deferred:  ${deferred.length}${deferred.length ? ` (${deferred.map((item) => item.prefab).join(", ")})` : ""}`);
console.log(`mapped:    ${Object.keys(variants).length} variants`);
if (warnings.length) {
  console.warn(`\nwarnings (${warnings.length}):`);
  for (const warning of warnings) console.warn(`  - ${warning}`);
}
