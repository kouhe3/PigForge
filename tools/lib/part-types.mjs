// The original's `BasePart.PartType` enum (`BasePart.cs:23`) and the only bridge to a PigForge
// `partTypeId`: the prefab both sides name.
//
// The whole construction UI speaks PartType -- a level's `m_partTypeCounts` list, the unlock table
// in `ConstructionUI`, and every part prefab's own `m_partType` field all carry the enum. PigForge
// numbers its parts itself (`content/parts.json`, mirrored for the original by
// `tools/bple-textures/part-map.json`), so an enum value is *not* a PigForge id and must never be
// written as one: it resolves through the prefab the map names. `part-map.json`'s `parts` section
// is the PigForge base of a family and `variants` its imported skins, so a prefab's `m_partType` is
// the enum the original groups it under and its map entry is the id PigForge shipped it as.
//
// A PartType no map entry resolves has no PigForge base at all, and the caller reports it instead
// of inventing an id: `Basket` (8) is a vehicle body rather than a part prefab (nothing in
// `Assets/GameObject` declares the enum); `JetEngine` (37) and `ObsoleteWheel` (38) name prefabs
// PigForge has no content for; `Pumpkin` (42) and `GoldenPig` (45) are the parts G91 has not
// delivered yet.

import { readFileSync } from "node:fs";
import { join } from "node:path";
import { scriptAssembly } from "./paths.mjs";
import { prefabText, readField } from "./unity.mjs";

/**
 * `BasePart.PartType` as enum value -> member name, read out of the pack so a report names the
 * values it refuses instead of numbering them. `check` records a missing or unreadable enum.
 */
export function readPartTypeNames(bpleRoot, check) {
  const file = join(scriptAssembly(bpleRoot), "BasePart.cs");
  const text = readFileSync(file, "utf8");
  const body = /public enum PartType\s*\{([\s\S]*?)\}/.exec(text)?.[1] ?? null;
  check(body !== null, `${file}: no PartType enum`);
  const names = new Map();
  for (const member of (body ?? "").split(",")) {
    const match = /^\s*(\w+)\s*=\s*(-?\d+)\s*$/.exec(member);
    if (match) names.set(Number(match[2]), match[1]);
  }
  check(names.size > 0, `${file}: PartType enum declares no readable member`);
  return names;
}

/**
 * The enum -> PigForge partTypeId table a level's `m_partTypeCounts` is read with. `parts` and
 * `variants` are `partTypeId -> prefab name` (exactly what `loadPartMap()` returns, and what the
 * `parts`/`variants` sections of `part-map.json` hold); `bpleRoot` is the original project root.
 *
 * Returns `{ byEnum, prefabByEnum, variantsByEnum, names }`:
 *   byEnum          PartType -> PigForge base partTypeId (the `parts` section)
 *   prefabByEnum    PartType -> that base's prefab name
 *   variantsByEnum  PartType -> the PigForge variant ids sharing that enum (the `variants` section)
 *   names           PartType -> enum member name (`readPartTypeNames`)
 *
 * Every leg is checked, because the bridge is only trustworthy while all of them are the pack's
 * own: a map entry whose prefab the pack lacks, a prefab without `m_partType`, two bases claiming
 * one enum, or a variant whose enum no base declares is drift, not a value to round off.
 */
export function resolvePartTypes(bpleRoot, { parts, variants }, check) {
  const gameObjectDir = join(bpleRoot, "Assets", "GameObject");
  const byEnum = new Map();
  const prefabByEnum = new Map();
  const variantsByEnum = new Map();
  const enumOf = (partTypeId, prefab) => {
    const text = prefabText(gameObjectDir, prefab);
    check(text !== null, `part-map names ${prefab} (partTypeId ${partTypeId}), which is not a prefab in the pack`);
    const partType = text === null ? null : readField(text, "m_partType");
    check(partType !== null, `${prefab} (partTypeId ${partTypeId}): no m_partType field`);
    return partType;
  };

  for (const [partTypeId, prefab] of parts) {
    if (typeof prefab !== "string" || prefab.length === 0) continue;
    const partType = enumOf(partTypeId, prefab);
    if (partType === null) continue;
    check(!byEnum.has(partType), `PartType ${partType} is declared by both ${prefabByEnum.get(partType)} and ${prefab}`);
    byEnum.set(partType, partTypeId);
    prefabByEnum.set(partType, prefab);
  }

  for (const [partTypeId, prefab] of variants) {
    if (typeof prefab !== "string" || prefab.length === 0) continue;
    const partType = enumOf(partTypeId, prefab);
    if (partType === null) continue;
    // A skin joins its family: an enum with no base would be unreachable from a level's table.
    check(byEnum.has(partType), `variant ${prefab} declares PartType ${partType}, which no base prefab declares`);
    if (!variantsByEnum.has(partType)) variantsByEnum.set(partType, []);
    variantsByEnum.get(partType).push(partTypeId);
  }

  return { byEnum, prefabByEnum, variantsByEnum, names: readPartTypeNames(bpleRoot, check) };
}
