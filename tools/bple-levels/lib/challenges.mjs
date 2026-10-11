// The level's own challenges -- content v10's `challenges` array (`docs/specs/level-stars.md` §3).
//
// A challenge is a *level instance*, never a component the loader looks up: every `Challenge`
// subclass registers itself in its own `Awake`, the registry is then sorted by `gameObject.name`
// -- the level file's own instance label (`LevelLoader.cs:127` writes it) -- and renumbered 1..N
// (`Challenge.cs:62-81`). So `challenges` is the level's challenge instances in the ordinal order
// of their labels, and the two parameters a challenge carries are fields of the component the
// level's `PrefabOverrides` block mounts on top of the prefab:
//
//   TimeChallenge        Float m_targetTime     the seconds `IsCompleted()` compares `CompletionTime` to
//   DontUsePartChallenge Enum  m_partType       the `BasePart.PartType` the layout must not use
//   TransportChallenge   Enum  partToTransport  the `BasePart.PartType` that must reach the goal
//
// `PrefabOverrides` is the original's own `ObjectDeserializer` text (`lib/overrides.mjs`): the
// loader rebuilds the component field by field, so a block that does not name the class leaves the
// prefab's serialized value in place -- one of the 113 `TimeChallenge` instances is exactly that
// (`episode_6_level_V`, whose block is a Transform-only patch, so its `m_targetTime` stays 30).
// The block belongs to the instance it was stored next to (`lib/reader.mjs` keeps that pair), never
// to the first instance with the same label: three levels carry two same-labelled challenges.
//
// Nothing here guesses a class from a name: a palette prefab is a challenge when one of its
// `m_Script` guids resolves to a `Challenge` subclass (`tools/lib/unity.mjs`'s guid -> class index
// over the decompiled assembly), and the PartType -> PigForge id bridge is
// `tools/lib/part-types.mjs`'s `resolvePartTypes`, the only implementation of that mapping.

import { existsSync, readFileSync } from "node:fs";
import { basename, join } from "node:path";
import { scriptAssembly } from "../../lib/paths.mjs";
import { buildGuidIndex, readField } from "../../lib/unity.mjs";
import { componentOf, fieldOf, parseOverrideText } from "./overrides.mjs";

/// The five `Challenge` subclasses the pack's prefabs carry, each one's content `kind` (the same
/// five values `Challenge.ChallengeType` names, `Challenge.cs:7-15`). Only five prefabs exist for
/// them (`BoxChallenge`/`DynamicBoxChallenge` are both a `WaypointChallenge`); `NoChallenge` --
/// whose `IsCompleted()` is `true` on its own -- has no prefab and no instance in the pack, so it
/// is not part of the bridge.
export const CHALLENGE_KIND_BY_CLASS = new Map([
  ["WaypointChallenge", "box"],
  ["TimeChallenge", "time"],
  ["DontUsePartChallenge", "dontUseParts"],
  ["PerfectFlightChallenge", "perfectFlight"],
  ["TransportChallenge", "transport"],
]);

/// The 14 levels the pack gives no challenge at all: the ten `Level_Sandbox_*`, the three
/// `Episode_6_* Sandbox` levels and `MMSandbox` (`docs/specs/level-stars.md` §7). Content v10 still
/// requires the array, so these documents carry `challenges: []` -- the room's objectives are off in
/// a sandbox level, and the original's own level files place no `Challenge` instance there at all.
/// Both tools assert the pack against this list, so a level that gains or loses its last challenge
/// fails the run instead of quietly changing star 2 and 3.
export const CHALLENGE_LESS_LEVELS = [
  ...Array.from({ length: 10 }, (_, index) => `Level_Sandbox_${String(index + 1).padStart(2, "0")}`),
  "Episode_6_Dark Sandbox",
  "Episode_6_Ice Sandbox",
  "Episode_6_Tower Sandbox",
  "MMSandbox",
].sort();

/// The serialized field each class keeps its *per-level* value in, and the name the reader hands it
/// back under. The two classes without one carry no parameter at all: a box is completed by its own
/// collectable (`WaypointChallenge.cs:9`) and a perfect flight by not breaking
/// (`PerfectFlightChallenge.cs:5-12`), so their content entries carry nothing but their identity.
const PARAMETER_BY_CLASS = new Map([
  ["TimeChallenge", { field: "m_targetTime", type: "Float", key: "targetTime" }],
  ["DontUsePartChallenge", { field: "m_partType", type: "Enum", key: "partTypeEnum" }],
  ["TransportChallenge", { field: "partToTransport", type: "Enum", key: "partTypeEnum" }],
]);

/**
 * A reader over one BPLE checkout: `read(prefabPath)` says whether a palette prefab is a challenge
 * and which one. Built once per tool run and cached per prefab, like `openProps`.
 *
 * Returns `{ className, kind, prefabName, targetTime, partTypeEnum }`, or `null` for the hundreds of
 * palette prefabs that carry no `Challenge` subclass. `targetTime`/`partTypeEnum` are the prefab's
 * own serialized values -- the defaults a level's override patches -- and every leg is checked
 * rather than defaulted: an `m_Script` guid that resolves to no class, two challenge components on
 * one prefab, or a component without its parameter field is drift, not a case to cover. The guid
 * index resolves through the class each `.cs` *declares* (`declaredClass`), so the file name is
 * never trusted.
 */
export function openChallengePrefabs(bpleRoot, check) {
  const guidToClass = buildGuidIndex(scriptAssembly(bpleRoot), { declaredClass: true });
  const cache = new Map();
  return (prefabPath) => {
    if (cache.has(prefabPath)) return cache.get(prefabPath);
    const prefabName = basename(prefabPath, ".prefab");
    const file = join(bpleRoot, prefabPath);
    check(existsSync(file), `${prefabPath}: challenge prefab does not exist`);
    let record = null;
    if (existsSync(file)) {
      // The `m_Script` guid of every component block: Unity writes one block per MonoBehaviour,
      // inherited fields included, so the block that carries the class is the component itself.
      for (const block of readFileSync(file, "utf8").split(/\n--- !u!/)) {
        const guid = /m_Script:\s*\{fileID:\s*-?\d+,\s*guid:\s*([0-9a-f]{32})/.exec(block)?.[1];
        const className = guid === undefined ? undefined : guidToClass.get(guid);
        const kind = className === undefined ? undefined : CHALLENGE_KIND_BY_CLASS.get(className);
        if (kind === undefined) continue;
        check(record === null, `${prefabName}: two Challenge components (${record?.className} and ${className})`);
        if (record !== null) continue;
        record = { className, kind, prefabName, targetTime: null, partTypeEnum: null };
        const parameter = PARAMETER_BY_CLASS.get(className);
        if (parameter !== undefined) {
          const value = readField(block, parameter.field);
          check(value !== null, `${prefabName}: ${className}.${parameter.field} is not serialized`);
          if (value !== null) record[parameter.key] = value;
        }
      }
    }

    cache.set(prefabPath, record);
    return record;
  };
}

/**
 * The level's own challenge instances: instance node -> the prefab's record plus the level's own
 * label, in the level file's own order. A palette entry that did not resolve (the builder's
 * `<unresolved:...>` placeholder) is skipped exactly like the props walk skips it.
 */
export function challengeInstances(data, palette, readPrefab) {
  const instances = new Map();
  for (const instance of data.instanceList) {
    const prefabPath = palette[instance.prefabIndex];
    if (typeof prefabPath !== "string" || prefabPath.startsWith("<")) continue;
    const record = readPrefab(prefabPath);
    if (record === null) continue;
    instances.set(instance, { ...record, name: instance.name, prefabPath });
  }

  return instances;
}

/**
 * One challenge instance's *per-level* parameter, out of the `PrefabOverrides` block the file stores
 * next to it: `{ targetTime, partTypeEnum }`, each `null` when the level does not override it --
 * no block at all, a block whose own root is a different label (`LevelLoader.cs:113` drops such a
 * block), or a block that does not mount the class, which is the original's own fall-back to the
 * prefab's serialized value. A block that *does* mount the class without the field is drift.
 *
 * A `Float` is stored as the float32 the original's `float.Parse` -> `float` assignment produces;
 * `Enum`s stay the `BasePart.PartType` value (never a PigForge id -- see `readLevelChallenges`).
 */
export function challengeParameters(instance, overrideText, check) {
  const where = `"${instance.name}" (${instance.prefabName})`;
  const parameter = PARAMETER_BY_CLASS.get(instance.className);
  if (parameter === undefined || overrideText === undefined) return { targetTime: null, partTypeEnum: null };
  const root = parseOverrideText(overrideText);
  check(
    root?.type === "GameObject" && root.name === instance.name,
    `${where}: its PrefabOverrides block is rooted at ${root?.type ?? "nothing"} "${root?.name ?? ""}", not at the instance's own label`,
  );
  const component = componentOf(root, instance.className);
  if (component === null) return { targetTime: null, partTypeEnum: null };
  const field = fieldOf(component, parameter.field);
  check(field !== null, `${where}: its ${instance.className} override carries no ${parameter.field}`);
  if (field === null) return { targetTime: null, partTypeEnum: null };
  check(field.type === parameter.type, `${where}: ${parameter.field} is written as ${field.type}, not ${parameter.type}`);
  const value = Number(field.value);
  check(Number.isFinite(value), `${where}: ${parameter.field} is ${JSON.stringify(field.value)}`);
  if (!Number.isFinite(value)) return { targetTime: null, partTypeEnum: null };
  return parameter.type === "Float"
    ? { targetTime: Math.fround(value), partTypeEnum: null }
    : { targetTime: null, partTypeEnum: value };
}

/**
 * One level's `challenges` array (content v10) plus the same instances as metadata, both in the
 * original's own order -- `challenges[0]` is star 2 and `challenges[1]` star 3
 * (`docs/specs/level-stars.md` §2.2) -- so a caller can count what the content carries without
 * re-reading the level. Sorting is ordinal and stable (`Array#sort` keeps equal keys in input
 * order, the level file's own order -- deviation D1).
 *
 * `instances` is `challengeInstances`' map, `overrides` the level file's `[{ node, text }]` pairs,
 * `partTypes` `tools/lib/part-types.mjs`'s `resolvePartTypes` result (the only enum -> partTypeId
 * bridge), and `propIndexByNode` instance -> index into the level's `props` array, with `propCount`
 * that array's length -- the box challenges' own slot, which the collection segment indexes. A
 * caller that reads level files rather than a document (the extractor's census) passes `null` and
 * gets the boxes without a `propIndex`.
 */
export function readLevelChallenges({ sceneName, instances, overrides, partTypes, propIndexByNode, propCount, check }) {
  const textsByNode = new Map(overrides.map(({ node, text }) => [node, text]));
  const entries = [];
  for (const [node, instance] of instances) {
    const where = `${sceneName}: challenge "${instance.name}" (${instance.prefabName})`;
    check(instance.name.length > 0, `${sceneName}: a ${instance.prefabName} instance has an empty label`);
    const parameters = challengeParameters(instance, textsByNode.get(node), check);
    const entry = { name: instance.name, kind: instance.kind };
    if (instance.kind === "box") {
      const propIndex = propIndexByNode === null ? null : propIndexByNode.get(node);
      if (propIndexByNode !== null) {
        const inside = Number.isInteger(propIndex) && propIndex >= 0 && propIndex < propCount;
        check(inside, `${where}: no props entry of the level's ${propCount} (propIndex ${propIndex})`);
        if (inside) entry.propIndex = propIndex;
      }
    } else if (instance.kind === "time") {
      // The level's own `m_targetTime`, else the prefab's: never a value the tool invented.
      const targetTime = parameters.targetTime ?? instance.targetTime;
      const usable = Number.isFinite(targetTime) && targetTime >= 0;
      check(usable, `${where}: m_targetTime is ${targetTime}`);
      if (usable) entry.targetTime = Math.fround(targetTime);
    } else if (instance.kind === "dontUseParts" || instance.kind === "transport") {
      const partType = parameters.partTypeEnum ?? instance.partTypeEnum;
      const partTypeId = partType === null ? undefined : partTypes.byEnum.get(partType);
      check(
        partTypeId !== undefined,
        `${where}: PartType ${partType ?? "?"} (${partTypes.names.get(partType) ?? "?"}) has no PigForge base`,
      );
      if (partTypeId !== undefined) entry.partTypeId = partTypeId;
    }

    entries.push({ node, instance, entry, parameters });
  }

  entries.sort((left, right) => (left.entry.name < right.entry.name ? -1 : left.entry.name > right.entry.name ? 1 : 0));

  return {
    challenges: entries.map(({ entry }) => entry),
    instances: entries.map(({ instance, entry, parameters }) => ({
      name: instance.name,
      className: instance.className,
      kind: instance.kind,
      prefabName: instance.prefabName,
      propIndex: entry.propIndex ?? null,
      targetTime: entry.targetTime ?? null,
      targetTimeOverridden: parameters.targetTime !== null,
      partTypeId: entry.partTypeId ?? null,
      partTypeEnum: parameters.partTypeEnum ?? instance.partTypeEnum,
    })),
  };
}
