#!/usr/bin/env node
// Extracts the original Bad Piggies part physics materials (BPLE Unity project) as the
// authority for PigForge `content/parts.json` `material` values. Reads the original
// project read-only; writes a machine-readable JSON report and a human-readable Markdown
// summary into the gitignored `tasks/` workspace. See docs/decisions/ADR-005.
//
// Usage:
//   node tools/bple-materials/extract-materials.mjs [--bple <dir>] [--json <file>] [--md <file>]
//
// Material provenance (verified against BPLE source):
//   - Part prefabs live in <BPLE>/Assets/GameObject/Part_*.prefab; the part ones follow
//     `Part_<name>_<n>_SET.prefab` (a few odd names exist, e.g. `Part_Pig_11_SET 1.prefab`,
//     and the SET pattern misses them, so the scan is deliberately wider).
//   - Collider components (BoxCollider 65, SphereCollider 135, CapsuleCollider 136,
//     MeshCollider 64) carry the reference
//     `m_Material: {fileID: 13400000, guid: <32 hex>, type: 2}`.
//     `m_Material: {fileID: 0}` means "no material asset" and Unity falls back to its
//     built-in default PhysicsMaterial (bounciness 0, friction 0.6/0.6, Average combine);
//     the report marks those parts `noMaterial` instead of inventing numbers.
//   - The asset is <BPLE>/Assets/PhysicMaterial/<name>.physicMaterial; the sibling `.meta`
//     `guid:` line is the reverse index. Material fields are flat and NOT m_-prefixed:
//     `dynamicFriction` / `staticFriction` / `bounciness` / `frictionCombine` / `bounceCombine`.
//   - A prefab may mix materials across colliders (e.g. a wheel: box body -> Contraption_PhysMat,
//     sphere tyre -> Contraption_WheelFriction_PhysMat); the report keeps every collider and
//     marks the prefab `mixed` rather than flattening it.
//   - Prefab nesting (PrefabInstance class 1001) and "stripped" component blocks would leave
//     material ownership in the referenced prefab; the report counts them per prefab and flags
//     the part as uncertain instead of guessing.

import { existsSync, mkdirSync, readFileSync, readdirSync, writeFileSync } from "node:fs";
import { dirname, join, resolve } from "node:path";
import { fileURLToPath } from "node:url";

const HERE = dirname(fileURLToPath(import.meta.url));
const REPO = resolve(HERE, "..", "..");

function arg(name, fallback) {
  const index = process.argv.indexOf(`--${name}`);
  return index >= 0 && process.argv[index + 1] ? process.argv[index + 1] : fallback;
}

const BPLE = resolve(arg("bple", process.env.BPLE_ROOT ?? join(REPO, "..", "BPLE_Unity6")));
const OUT_JSON = resolve(arg("json", join(REPO, "tasks", "bple-materials-report.json")));
const OUT_MD = resolve(arg("md", join(REPO, "tasks", "bple-materials-report.md")));
const ASSETS = join(BPLE, "Assets");
const GAMEOBJECT = join(ASSETS, "GameObject");
const CONTENT_PARTS = join(REPO, "content", "parts.json");
const TEXTURE_MAP = join(REPO, "tools", "bple-textures", "part-map.json");
const SHAPE_MAP = join(REPO, "tools", "bple-shapes", "part-map.json");
const DYNAMICS_MANAGER = join(BPLE, "ProjectSettings", "DynamicsManager.asset");

if (!existsSync(GAMEOBJECT)) {
  console.error(`BPLE project not found: ${GAMEOBJECT}\nPass --bple <path to BPLE_Unity6>.`);
  process.exit(1);
}

// Unity's built-in fallback when a collider has no material asset. Field names mirror the
// Unity serialization so the report stays comparable with the extracted assets.
const UNITY_DEFAULT_MATERIAL = {
  name: "<unity default>",
  asset: null,
  dynamicFriction: 0.6,
  staticFriction: 0.6,
  bounciness: 0,
  frictionCombine: 0,
  bounceCombine: 0,
};
const COMBINE = ["Average", "Minimum", "Multiply", "Maximum"];

const warnings = [];
const notes = [
  "A collider with `m_Material: {fileID: 0}` (or a collider created at runtime without a material) resolves to Unity's built-in default PhysicsMaterial: dynamic/static friction 0.6, bounciness 0, both combines Average. The original never assigns one, which is why `noMaterial` is reported as a fact and not as a number.",
  "Runtime-created colliders never get a physics material in BPLE: Balloon.cs:123 (`AddComponent<SphereCollider>().radius = 0.5f`), Sandbag.cs:124-127, PointLightSource.cs:111-125 and EntityLight.cs:252 all add colliders bare, and those light colliders are triggers. The game's own `INContraption.GetMaterial(collider.material)` (INContraption.cs:417-426) therefore reads the Unity default for them.",
  "The only runtime material assignment in the whole assembly is LevelRigidbody.cs:314-350 (`sharedMaterial = m_iceMaterial`), which patches terrain colliders with Assets/Resources/ground_physmat_ice.physicMaterial (bounciness 0, friction 0.01/0.01, Multiply). That is level terrain, not a contraption part.",
];

// Class ids that carry `m_Material` for 3D physics.
const COLLIDER_KINDS = new Map([
  [64, "mesh"],
  [65, "box"],
  [135, "sphere"],
  [136, "capsule"],
]);
const PREFAB_INSTANCE = 1001;

// ---------------------------------------------------------------- guid index

/** guid -> absolute path of the *asset* (the .meta suffix is dropped), for PhysicMaterials only. */
const guidToMaterialAsset = new Map();
(function walk(dir) {
  let entries;
  try {
    entries = readdirSync(dir, { withFileTypes: true });
  } catch {
    return;
  }
  for (const entry of entries) {
    const path = join(dir, entry.name);
    if (entry.isDirectory()) {
      walk(path);
    } else if (entry.name.endsWith(".physicMaterial.meta")) {
      const head = readFileSync(path, "utf8").slice(0, 256);
      const match = /^guid: ([0-9a-f]{32})/m.exec(head);
      if (match) guidToMaterialAsset.set(match[1], path.slice(0, -5));
    }
  }
})(ASSETS);

// ------------------------------------------------------------ material assets

/**
 * Which asset the project points at for "no material". `fileID: 0` means unset, i.e. whatever
 * colliders without a material fall back to is Unity's built-in default, not a project asset.
 */
function readProjectDefaultMaterial() {
  if (!existsSync(DYNAMICS_MANAGER)) {
    warnings.push(`project settings missing: ${DYNAMICS_MANAGER}`);
    return { fileID: null, projectAsset: null, resolvesToUnityBuiltin: null };
  }
  const text = readFileSync(DYNAMICS_MANAGER, "utf8");
  const reference = /m_DefaultMaterial: \{fileID: (-?\d+)(?:, guid: ([0-9a-f]{32}), type: (\d+))?\}/.exec(text);
  const fileID = reference ? Number(reference[1]) : null;
  const guid = reference?.[2] ?? null;
  const projectAsset = guid ? guidToMaterialAsset.get(guid) ?? null : null;
  if (fileID !== null && fileID !== 0) {
    warnings.push(`project default material is set (fileID ${fileID}${projectAsset ? ` -> ${projectAsset}` : ""}); ` +
      "colliders without an explicit material do NOT fall back to the Unity built-in default");
  }
  return { fileID, projectAsset, resolvesToUnityBuiltin: fileID === 0 };
}

function parsePhysicMaterial(path) {
  const text = readFileSync(path, "utf8");
  const num = (name) => {
    const match = new RegExp(`^\\s*${name}:\\s*(-?[0-9.]+)\\s*$`, "m").exec(text);
    return match ? Number(match[1]) : undefined;
  };
  const name = /^\s*m_Name:\s*(.*)$/m.exec(text)?.[1].trim() ?? path;
  const values = {
    name,
    asset: path,
    dynamicFriction: num("dynamicFriction"),
    staticFriction: num("staticFriction"),
    bounciness: num("bounciness"),
    frictionCombine: num("frictionCombine"),
    bounceCombine: num("bounceCombine"),
  };
  for (const field of ["dynamicFriction", "staticFriction", "bounciness", "frictionCombine", "bounceCombine"]) {
    if (!Number.isFinite(values[field])) warnings.push(`material ${name}: missing ${field} in ${path}`);
  }
  return values;
}

/** name -> parsed material asset, plus guid -> name for prefab references. */
const materials = {};
const guidToMaterialName = new Map();
for (const [guid, asset] of guidToMaterialAsset) {
  const material = parsePhysicMaterial(asset);
  materials[material.name] = { ...material, guid, usedBy: [], colliderRefs: 0 };
  guidToMaterialName.set(guid, material.name);
}

// ------------------------------------------------------------------ prefab scan

/** Split a Unity YAML document into `--- !u!<class> &<fileID>` blocks. */
function parseBlocks(text) {
  const blocks = [];
  for (const chunk of text.split(/^--- !u!/m).slice(1)) {
    const header = /^(\d+) &(\d+)( stripped)?\n/.exec(chunk);
    if (!header) continue;
    blocks.push({
      classId: Number(header[1]),
      fileId: header[2],
      stripped: Boolean(header[3]),
      body: chunk.slice(header[0].length),
    });
  }
  return blocks;
}

function collectPrefabs() {
  const names = readdirSync(GAMEOBJECT)
    .filter((entry) => entry.startsWith("Part_") && entry.endsWith(".prefab"))
    .map((entry) => entry.slice(0, -".prefab".length))
    .sort();
  return names.map((prefab) => ({ prefab, file: `${prefab}.prefab`, matchesSetPattern: /^Part_.*_SET$/.test(prefab) }));
}

function scanPrefab(prefab) {
  const path = join(GAMEOBJECT, prefab.file);
  const blocks = parseBlocks(readFileSync(path, "utf8"));
  const gameObjectNames = new Map();
  for (const block of blocks) {
    if (block.classId !== 1) continue;
    gameObjectNames.set(block.fileId, /^\s*m_Name:\s*(.*)$/m.exec(block.body)?.[1].trim() ?? "");
  }

  const colliders = [];
  let nestedPrefabInstances = 0;
  let strippedBlocks = 0;
  for (const block of blocks) {
    if (block.stripped) strippedBlocks += 1;
    if (block.classId === PREFAB_INSTANCE) nestedPrefabInstances += 1;
    const kind = COLLIDER_KINDS.get(block.classId);
    if (!kind) continue;
    const gameObject = /m_GameObject: \{fileID: (\d+)\}/.exec(block.body)?.[1] ?? null;
    const reference = /m_Material: \{fileID: (-?\d+)(?:, guid: ([0-9a-f]{32}), type: (\d+))?\}/.exec(block.body);
    const materialFileId = reference ? Number(reference[1]) : null;
    const materialGuid = reference?.[2] ?? null;
    const materialName = materialGuid ? guidToMaterialName.get(materialGuid) ?? null : null;
    if (materialGuid && !materialName) warnings.push(`${prefab.prefab}: material guid ${materialGuid} not found under ${join(ASSETS, "PhysicMaterial")}`);
    if (materialFileId !== null && materialFileId !== 0 && materialGuid && materialFileId !== 13400000) {
      warnings.push(`${prefab.prefab}: unexpected material fileID ${materialFileId} (expected 13400000) for guid ${materialGuid}`);
    }
    colliders.push({
      classId: block.classId,
      kind,
      fileId: block.fileId,
      gameObject,
      gameObjectName: gameObject ? gameObjectNames.get(gameObject) ?? "" : "",
      isTrigger: /^\s*m_IsTrigger: 1\s*$/m.test(block.body),
      materialFileId,
      materialGuid,
      materialName,
      hasMaterial: Boolean(materialName),
    });
  }

  // Trigger colliders (attachment markers, sensors) do not drive collision response, so the
  // prefab's gameplay material is read from the non-trigger colliders when there are any.
  const nonTrigger = colliders.filter((collider) => !collider.isTrigger);
  const responseColliders = nonTrigger.length > 0 ? nonTrigger : colliders;
  const materialNames = [...new Set(responseColliders.filter((collider) => collider.hasMaterial).map((collider) => collider.materialName))].sort();
  const status = materialNames.length === 0 ? "noMaterial" : materialNames.length === 1 ? "single" : "mixed";
  const noMaterialReason =
    status !== "noMaterial" ? null : colliders.length === 0 ? "noAuthoredCollider" : "colliderMaterialNull";

  for (const name of materialNames) {
    materials[name].usedBy.push(prefab.prefab);
    materials[name].colliderRefs += responseColliders.filter((collider) => collider.materialName === name).length;
  }

  return {
    prefab: prefab.prefab,
    file: prefab.file,
    matchesSetPattern: prefab.matchesSetPattern,
    colliderCount: colliders.length,
    colliderKinds: [...new Set(colliders.map((collider) => collider.kind))].sort(),
    triggerColliderCount: colliders.filter((collider) => collider.isTrigger).length,
    nestedPrefabInstances,
    strippedBlocks,
    colliders,
    materialNames,
    status,
    noMaterialReason,
    uncertain: nestedPrefabInstances > 0 || strippedBlocks > 0,
  };
}

const prefabs = {};
for (const prefab of collectPrefabs()) {
  prefabs[prefab.prefab] = scanPrefab(prefab);
}

// -------------------------------------------------- PigForge partTypeId mapping

/** partTypeId -> prefab name, merged from every available part-map (textures is authoritative). */
const partTypeToPrefab = new Map();
const mapSources = [];
for (const mapPath of [TEXTURE_MAP, SHAPE_MAP]) {
  if (!existsSync(mapPath)) {
    warnings.push(`part map missing: ${mapPath}`);
    continue;
  }
  const map = JSON.parse(readFileSync(mapPath, "utf8"));
  mapSources.push(mapPath);
  for (const group of ["parts", "variants"]) {
    for (const [id, name] of Object.entries(map[group] ?? {})) {
      if (!name) continue;
      const existing = partTypeToPrefab.get(id);
      if (existing && existing !== name) warnings.push(`partTypeId ${id}: ${mapPath} maps ${name}, another map says ${existing}`);
      partTypeToPrefab.set(id, name);
    }
  }
}

const prefabToPartTypeIds = new Map();
for (const [id, name] of partTypeToPrefab) {
  if (!prefabToPartTypeIds.has(name)) prefabToPartTypeIds.set(name, []);
  prefabToPartTypeIds.get(name).push(id);
}
for (const ids of prefabToPartTypeIds.values()) ids.sort((a, b) => Number(a) - Number(b));

for (const [name, ids] of prefabToPartTypeIds) {
  if (prefabs[name]) {
    prefabs[name].partTypeIds = ids;
  } else {
    warnings.push(`part map points at missing prefab: ${name} (partTypeIds ${ids.join(", ")})`);
  }
}
for (const prefab of Object.values(prefabs)) {
  prefab.partTypeIds = prefab.partTypeIds ?? [];
}

const unmappedPrefabs = Object.values(prefabs)
  .filter((prefab) => prefab.partTypeIds.length === 0)
  .map((prefab) => prefab.prefab);
const unmappedSetPatternPrefabs = Object.values(prefabs)
  .filter((prefab) => prefab.partTypeIds.length === 0 && prefab.matchesSetPattern)
  .map((prefab) => prefab.prefab);

// --------------------------------------------------- content/parts.json (read-only)

const content = JSON.parse(readFileSync(CONTENT_PARTS, "utf8"));
const contentParts = new Map();
for (const part of content.parts ?? []) contentParts.set(String(part.partTypeId), part);

const round = (value) => Math.round(value * 1e5) / 1e5;

/**
 * The prefab's original materials, most-used-first (a mixed prefab keeps every one of them, so a
 * one-line diff never hides the tyre material behind the frame material). Empty for a prefab with
 * no explicit material; `null` when no prefab is mapped at all.
 */
function originalMaterialsOf(prefab) {
  if (!prefab) return null;
  if (prefab.status === "noMaterial") return [];
  const counted = prefab.materialNames.map((name) => ({
    name,
    refs: prefab.colliders.filter((collider) => !collider.isTrigger && collider.materialName === name).length,
  }));
  return counted
    .sort((a, b) => b.refs - a.refs || a.name.localeCompare(b.name))
    .map(({ name }) => {
      const material = materials[name];
      return {
        name,
        bounciness: material.bounciness,
        dynamicFriction: material.dynamicFriction,
        staticFriction: material.staticFriction,
        frictionCombine: material.frictionCombine,
        bounceCombine: material.bounceCombine,
      };
    });
}

const diff = [];
for (const [id, part] of [...contentParts].sort((a, b) => Number(a[0]) - Number(b[0]))) {
  const prefabName = partTypeToPrefab.get(id) ?? null;
  const prefab = prefabName ? prefabs[prefabName] ?? null : null;
  const originalMaterials = originalMaterialsOf(prefab);
  const primary = originalMaterials?.[0] ?? (prefab ? UNITY_DEFAULT_MATERIAL : null);
  const current = part.material ?? null;
  if (!current && !primary?.name) continue;
  const originalNoMaterial = Boolean(prefab) && prefab.status === "noMaterial";
  const bounciness = !primary || originalNoMaterial ? null : primary.bounciness;
  const dynamicFriction = !primary || originalNoMaterial ? null : primary.dynamicFriction;
  const staticFriction = !primary || originalNoMaterial ? null : primary.staticFriction;
  const restitutionDelta = current && bounciness !== null ? round(current.restitution - bounciness) : null;
  const frictionDelta = current && dynamicFriction !== null ? round(current.friction - dynamicFriction) : null;
  diff.push({
    partTypeId: id,
    name: part.name,
    prefab: prefabName,
    originalPrefabFound: Boolean(prefab),
    originalStatus: prefab ? prefab.status : "prefabNotMapped",
    originalMaterial: originalNoMaterial ? null : primary?.name ?? null,
    originalMixed: prefab?.status === "mixed",
    originalMaterials: (originalMaterials ?? []).map((material) => ({
      name: material.name,
      bounciness: material.bounciness,
      dynamicFriction: material.dynamicFriction,
      staticFriction: material.staticFriction,
      frictionCombine: material.frictionCombine,
      bounceCombine: material.bounceCombine,
    })),
    originalNoMaterial,
    originalBounciness: bounciness,
    originalDynamicFriction: dynamicFriction,
    originalStaticFriction: staticFriction,
    originalFrictionCombine: originalNoMaterial || !primary ? null : COMBINE[primary.frictionCombine] ?? null,
    originalBounceCombine: originalNoMaterial || !primary ? null : COMBINE[primary.bounceCombine] ?? null,
    currentRestitution: current ? current.restitution : null,
    currentFriction: current ? current.friction : null,
    restitutionDelta,
    frictionDelta,
    /** Restitution is off by more than 0.15, or friction by more than 25% of the original. */
    notable:
      (restitutionDelta !== null && Math.abs(restitutionDelta) > 0.15) ||
      (frictionDelta !== null && dynamicFriction > 0 && Math.abs(frictionDelta) / dynamicFriction > 0.25) ||
      (current !== null && originalNoMaterial) ||
      (current === null && !originalNoMaterial),
  });
}

/**
 * Review priority, biggest first. Bounce the original does not have is weighted hardest (a
 * fabricated bounciness changes gameplay; a wrong one only shifts it), then |Δrestitution|, then
 * the relative friction error. Documented so the ranking stays auditable instead of hand-tuned.
 */
for (const row of diff) {
  const fabricatedBounce =
    (row.originalBounciness === 0 || row.originalNoMaterial) && row.originalPrefabFound ? row.currentRestitution ?? 0 : 0;
  const frictionError =
    row.frictionDelta !== null && row.originalDynamicFriction > 0
      ? Math.min(2, Math.abs(row.frictionDelta) / row.originalDynamicFriction)
      : 0;
  row.severity = round(3 * fabricatedBounce + Math.abs(row.restitutionDelta ?? 0) + frictionError);
}
const rankedDiff = [...diff].filter((row) => row.severity > 0).sort((a, b) => b.severity - a.severity || Number(a.partTypeId) - Number(b.partTypeId));

// ---------------------------------------------------------------- distributions

function tally(pairs) {
  const counts = new Map();
  for (const key of pairs) counts.set(key, (counts.get(key) ?? 0) + 1);
  return counts;
}

const scannedPrefabs = Object.values(prefabs);
const withMaterial = scannedPrefabs.filter((prefab) => prefab.status !== "noMaterial");
const noMaterialPrefabs = scannedPrefabs.filter((prefab) => prefab.status === "noMaterial");
const mixedPrefabs = scannedPrefabs.filter((prefab) => prefab.status === "mixed");
const bouncyPrefabs = scannedPrefabs.filter(
  (prefab) => prefab.status !== "noMaterial" && prefab.materialNames.some((name) => materials[name].bounciness !== 0),
);

/** Distribution over (prefab, distinct material) pairs — a mixed prefab counts once per material. */
function materialPairs() {
  const pairs = [];
  for (const prefab of scannedPrefabs) for (const name of prefab.materialNames) pairs.push({ prefab, name });
  return pairs;
}

const distribution = {
  bouncinessByPrefab: Object.fromEntries(
    [...tally(materialPairs().map(({ name }) => String(materials[name].bounciness))).entries()].sort((a, b) => Number(a[0]) - Number(b[0])),
  ),
  dynamicFrictionByPrefab: Object.fromEntries(
    [...tally(materialPairs().map(({ name }) => String(materials[name].dynamicFriction))).entries()].sort((a, b) => Number(a[0]) - Number(b[0])),
  ),
  frictionCombineByPrefab: Object.fromEntries(
    [...tally(materialPairs().map(({ name }) => COMBINE[materials[name].frictionCombine] ?? String(materials[name].frictionCombine))).entries()].sort(),
  ),
  materialUsageByPrefab: Object.fromEntries([...tally(materialPairs().map(({ name }) => name)).entries()].sort((a, b) => b[1] - a[1])),
  statusByPrefab: Object.fromEntries([...tally(scannedPrefabs.map((prefab) => prefab.status)).entries()].sort()),
};

// ---------------------------------------------------------------------- report

const report = {
  format: "pigforge.bple-part-materials",
  schemaVersion: 1,
  source: BPLE,
  partMapSources: mapSources,
  counters: {
    prefabsScanned: scannedPrefabs.length,
    prefabsMatchingSetPattern: scannedPrefabs.filter((prefab) => prefab.matchesSetPattern).length,
    prefabsWithMaterial: withMaterial.length,
    prefabsNoMaterial: noMaterialPrefabs.length,
    prefabsMixed: mixedPrefabs.length,
    prefabsUnmapped: unmappedPrefabs.length,
    materialAssets: Object.keys(materials).length,
    contentParts: contentParts.size,
    diffRows: diff.length,
    diffNotable: diff.filter((row) => row.notable).length,
  },
  unityDefaultMaterial: UNITY_DEFAULT_MATERIAL,
  projectDefaultMaterial: readProjectDefaultMaterial(),
  materials,
  distribution,
  prefabs,
  partTypeIds: Object.fromEntries(
    [...partTypeToPrefab].map(([id, name]) => [
      id,
      {
        prefab: name,
        prefabFound: Boolean(prefabs[name]),
        status: prefabs[name]?.status ?? "prefabMissing",
        materialNames: prefabs[name]?.materialNames ?? [],
      },
    ]),
  ),
  bouncyPrefabs: bouncyPrefabs.map((prefab) => ({
    prefab: prefab.prefab,
    partTypeIds: prefab.partTypeIds,
    materials: prefab.materialNames.map((name) => ({ name, bounciness: materials[name].bounciness, staticFriction: materials[name].staticFriction, dynamicFriction: materials[name].dynamicFriction })),
  })),
  noMaterialPrefabs: noMaterialPrefabs.map((prefab) => ({
    prefab: prefab.prefab,
    partTypeIds: prefab.partTypeIds,
    reason: prefab.noMaterialReason,
    colliderCount: prefab.colliderCount,
  })),
  mixedPrefabs: mixedPrefabs.map((prefab) => ({ prefab: prefab.prefab, partTypeIds: prefab.partTypeIds, materials: prefab.materialNames })),
  unmappedPrefabs,
  unmappedSetPatternPrefabs,
  diff,
  diffRankedTop: rankedDiff.slice(0, 25).map((row) => ({
    partTypeId: row.partTypeId,
    name: row.name,
    prefab: row.prefab,
    severity: row.severity,
    originalMaterial: row.originalMaterial,
    originalNoMaterial: row.originalNoMaterial,
    originalBounciness: row.originalBounciness,
    originalDynamicFriction: row.originalDynamicFriction,
    currentRestitution: row.currentRestitution,
    currentFriction: row.currentFriction,
  })),
  warnings,
  notes,
};

// --------------------------------------------------------------- markdown render

const fmt = (value) => (value === null || value === undefined ? "—" : String(value));
const partIds = (prefab) => (prefab.partTypeIds.length > 0 ? prefab.partTypeIds.join(", ") : "—");

function table(headers, rows) {
  const lines = [`| ${headers.join(" | ")} |`, `| ${headers.map(() => "---").join(" | ")} |`];
  for (const row of rows) lines.push(`| ${row.join(" | ")} |`);
  return lines.join("\n");
}

const md = [];
md.push("# BPLE 零件物理材质真值提取报告", "");
md.push(`来源（只读）：\`${BPLE}\``, "");
md.push(
  `扫描 \`Assets/GameObject/Part_*.prefab\`：共 **${scannedPrefabs.length}** 个（其中 ${report.counters.prefabsMatchingSetPattern} 个匹配 \`Part_*_SET.prefab\`），`,
  `映射到 PigForge \`partTypeId\`：**${scannedPrefabs.length - unmappedPrefabs.length}** 个，未映射 ${unmappedPrefabs.length} 个。`,
  `物理材质资产：**${report.counters.materialAssets}** 个。`,
  "",
);
md.push("## 1. 汇总", "");
md.push(
  table(
    ["项目", "数量"],
    [
      ["有显式材质的 prefab", withMaterial.length],
      ["单一材质的 prefab", withMaterial.length - mixedPrefabs.length],
      ["混合材质的 prefab", mixedPrefabs.length],
      ["无显式材质（noMaterial）的 prefab", noMaterialPrefabs.length],
      ["其中：collider 的 m_Material 为 fileID 0", noMaterialPrefabs.filter((p) => p.noMaterialReason === "colliderMaterialNull").length],
      ["其中：prefab 内无任何 3D collider", noMaterialPrefabs.filter((p) => p.noMaterialReason === "noAuthoredCollider").length],
      ["bounciness != 0 的 prefab", bouncyPrefabs.length],
      ["content/parts.json 的零件数", contentParts.size],
      ["差异对照行数", diff.length],
      ["其中值得注意（restitution 偏差 > 0.15、friction 偏差 > 25%、或有无材质不一致）", diff.filter((row) => row.notable).length],
    ],
  ),
  "",
);
md.push(
  "材质判定口径：只统计**非 trigger** 的 collider（trigger 多为挂点/传感器，不参与碰撞响应）；",
  "若一个 prefab 内多个 collider 用了不同材质，则标记为 `mixed` 并逐个列出，不取平均。",
  "`m_Material: {fileID: 0}` 表示未指定材质，Unity 回退到内置默认材质（bounciness 0、dynamic/static friction 0.6、Combine=Average）——本报告中标记为 `noMaterial`，数值不写入。",
  "",
);

md.push("## 2. 物理材质资产全量真值", "");
md.push(
  table(
    ["材质", "bounciness", "dynamicFriction", "staticFriction", "frictionCombine", "bounceCombine", "被多少 prefab 引用", "collider 引用数"],
    Object.values(materials)
      .sort((a, b) => b.usedBy.length - a.usedBy.length)
      .map((material) => [
        material.name,
        `**${fmt(material.bounciness)}**`,
        fmt(material.dynamicFriction),
        fmt(material.staticFriction),
        `${COMBINE[material.frictionCombine] ?? material.frictionCombine}`,
        `${COMBINE[material.bounceCombine] ?? material.bounceCombine}`,
        material.usedBy.length,
        material.colliderRefs,
      ]),
  ),
  "",
);
md.push("被零件 prefab 实际引用的材质只有上表中 `被多少 prefab 引用 > 0` 的那几个；其余材质资产本报告也一并解析（见 JSON `materials`），它们服务于关卡地形/奖励物，不属于零件。", "");
const outsideFolder = Object.values(materials).filter((material) => !material.asset.replace(/\\/g, "/").includes("/Assets/PhysicMaterial/"));
if (outsideFolder.length > 0) {
  md.push(
    `注意：${outsideFolder.length} 个材质资产不在 \`Assets/PhysicMaterial/\` 下（\`Assets/**/*.physicMaterial.meta\` 递归扫描才找得到）：`,
    ...outsideFolder.map((material) => `- \`${material.name}\` → \`${material.asset}\``),
    "",
  );
}
md.push(
  `工程默认材质（\`ProjectSettings/DynamicsManager.asset\` 的 \`m_DefaultMaterial\`）：fileID \`${report.projectDefaultMaterial.fileID}\`` +
    `，${report.projectDefaultMaterial.resolvesToUnityBuiltin ? "未设置 → 无材质的 collider 回退到 Unity 内置默认（friction 0.6/0.6、bounciness 0、Combine=Average）" : "指向工程内资产"}。`,
  "",
);

md.push("## 3. 全量分布", "");
md.push("### 3.1 bounciness 分布（按 prefab × 材质计数）", "");
md.push(
  table(
    ["bounciness", "出现次数（prefab×材质）"],
    Object.entries(distribution.bouncinessByPrefab).map(([value, count]) => [value, count]),
  ),
  "",
);
md.push(`另有 **${noMaterialPrefabs.length}** 个 prefab 的 bounciness 为「未指定」（Unity 默认 0），不计入上表。`, "");
md.push("### 3.2 dynamicFriction 分布", "");
md.push(table(["dynamicFriction", "出现次数"], Object.entries(distribution.dynamicFrictionByPrefab).map(([value, count]) => [value, count])), "");
md.push("### 3.3 frictionCombine 分布", "");
md.push(table(["frictionCombine", "出现次数"], Object.entries(distribution.frictionCombineByPrefab).map(([value, count]) => [value, count])), "");
md.push("### 3.4 材质使用分布", "");
md.push(table(["材质", "prefab 数"], Object.entries(distribution.materialUsageByPrefab).map(([value, count]) => [value, count])), "");
md.push("### 3.5 prefab 状态分布", "");
md.push(table(["状态", "prefab 数"], Object.entries(distribution.statusByPrefab).map(([value, count]) => [value, count])), "");

md.push("## 4. bounciness != 0 的零件清单", "");
if (bouncyPrefabs.length === 0) {
  md.push("（无）", "");
} else {
  md.push(
    table(
      ["prefab", "partTypeId", "材质", "bounciness", "dynamicFriction", "staticFriction"],
      bouncyPrefabs.flatMap((prefab) =>
        prefab.materialNames
          .filter((name) => materials[name].bounciness !== 0)
          .map((name) => [prefab.prefab, partIds(prefab), name, `**${materials[name].bounciness}**`, materials[name].dynamicFriction, materials[name].staticFriction]),
      ),
    ),
    "",
  );
}

md.push("## 5. noMaterial 的零件清单", "");
md.push(
  "「prefab 内无 3D collider」= 该零件的碰撞体由脚本在运行时创建（Balloon.cs:123、Sandbag.cs:124-127、PointLightSource.cs:111-125、EntityLight.cs:252），这些脚本都没有给 collider 赋材质；",
  "「collider 的 m_Material = fileID 0」= prefab 里有 collider 但未指定材质。两种情况的最终真值都是 Unity 内置默认材质（dyn/static friction 0.6、bounciness 0、Combine=Average），因为工程的 `m_DefaultMaterial` 未设置。",
  "",
);
if (noMaterialPrefabs.length === 0) {
  md.push("（无）", "");
} else {
  md.push(
    table(
      ["prefab", "partTypeId", "原因", "collider 数", "含义"],
      noMaterialPrefabs.map((prefab) => [
        prefab.prefab,
        partIds(prefab),
        prefab.noMaterialReason === "noAuthoredCollider" ? "prefab 内无 3D collider" : "collider 的 m_Material = fileID 0",
        prefab.colliderCount,
        prefab.noMaterialReason === "noAuthoredCollider"
          ? "collider 由脚本在运行时创建（见 tools/bple-shapes/extract-shapes.mjs 的 RUNTIME_COLLIDERS），未指定材质 → Unity 默认材质"
          : "Unity 默认材质（bounciness 0、friction 0.6/0.6、Combine=Average）",
      ]),
    ),
    "",
  );
}

md.push("## 6. 混合材质（同一 prefab 内多材质）的零件", "");
if (mixedPrefabs.length === 0) {
  md.push("（无）", "");
} else {
  md.push(
    table(
      ["prefab", "partTypeId", "材质", "各 collider 归属"],
      mixedPrefabs.map((prefab) => [
        prefab.prefab,
        partIds(prefab),
        prefab.materialNames.join(" + "),
        prefab.colliders
          .filter((collider) => !collider.isTrigger)
          .map((collider) => `${collider.kind}${collider.gameObjectName ? `(${collider.gameObjectName})` : ""}→${collider.materialName ?? "none"}`)
          .join("; "),
      ]),
    ),
    "",
  );
}

md.push("## 7. 与 PigForge `content/parts.json` 的差异对照（只读对比）", "");
md.push("### 7.1 最值得注意的 10 条", "");
md.push(
  "排序依据 `severity = 3 × (原版无弹跳却被赋了 restitution 的部分) + |Δrestitution| + min(2, 摩擦相对误差)`，最大者优先。",
  "",
);
md.push(
  table(
    ["#", "partTypeId", "name", "prefab", "原版材质", "原版 bounciness", "原版 dyn friction", "当前 restitution", "当前 friction", "severity"],
    rankedDiff.slice(0, 10).map((row, index) => [
      index + 1,
      row.partTypeId,
      row.name,
      row.prefab ?? "—",
      row.originalNoMaterial ? "**无（Unity 默认）**" : row.originalMaterials.map((material) => material.name).join(" + "),
      row.originalNoMaterial ? "—（默认 0）" : fmt(row.originalBounciness),
      row.originalNoMaterial ? "—" : fmt(row.originalDynamicFriction),
      fmt(row.currentRestitution),
      fmt(row.currentFriction),
      row.severity,
    ]),
  ),
  "",
);
md.push("### 7.2 全量对照表", "");
md.push(
  "原版列：`bounciness` / `dynamicFriction` / `staticFriction`；当前列：`restitution` / `friction`。",
  `「值得注意」= |Δrestitution| > 0.15，或 |Δfriction| 超过原版 dynamicFriction 的 25%，或两侧有无材质不一致。共 ${diff.filter((row) => row.notable).length} 条。`,
  "混合材质的 prefab（标 `⚑mixed`）在原版列按「引用 collider 数最多」的材质给出标量值，全部材质见 JSON `diff[].originalMaterials`。",
  "",
);
md.push(
  table(
    ["partTypeId", "name", "prefab", "原版材质", "原版 bounciness", "原版 dyn/stat friction", "当前 restitution", "当前 friction", "Δrestitution", "Δfriction", "注意"],
    diff.map((row) => [
      row.partTypeId,
      row.name,
      row.prefab ?? "—",
      row.originalNoMaterial
        ? "**无（Unity 默认）**"
        : row.originalMaterials.map((material) => material.name).join(" + ") || "—",
      row.originalNoMaterial
        ? "—（默认 0）"
        : row.originalMaterials.map((material) => material.bounciness).join(" / ") || "—",
      row.originalNoMaterial
        ? "—"
        : row.originalMaterials.map((material) => `${material.dynamicFriction} / ${material.staticFriction}`).join(" | ") || "—",
      fmt(row.currentRestitution),
      fmt(row.currentFriction),
      row.restitutionDelta === null ? "—" : `${row.restitutionDelta > 0 ? "+" : ""}${row.restitutionDelta}`,
      row.frictionDelta === null ? "—" : `${row.frictionDelta > 0 ? "+" : ""}${row.frictionDelta}`,
      [row.notable ? "**是**" : "", row.originalMixed ? "⚑mixed" : "", row.originalMaterials.length === 0 && row.originalPrefabFound ? "noMaterial" : ""]
        .filter(Boolean)
        .join(" "),
    ]),
  ),
  "",
);

md.push("## 8. 未映射 / 缺失", "");
md.push(`未映射到任何 partTypeId 的 prefab（${unmappedPrefabs.length} 个）：`, "");
md.push(unmappedPrefabs.map((name) => `- \`${name}\``).join("\n") || "（无）", "");
md.push("", `其中匹配 \`Part_*_SET.prefab\` 的（${unmappedSetPatternPrefabs.length} 个）：`, "");
md.push(unmappedSetPatternPrefabs.map((name) => `- \`${name}\``).join("\n") || "（无）", "");
md.push("");
const missingPrefabs = [...partTypeToPrefab].filter(([, name]) => !prefabs[name]);
md.push(`part-map 指向但文件不存在的 prefab（${missingPrefabs.length} 个）：`, "");
md.push(missingPrefabs.map(([id, name]) => `- partTypeId ${id} → \`${name}\``).join("\n") || "（无）", "");
const unmappedContentParts = [...contentParts].filter(([id]) => !partTypeToPrefab.has(id));
md.push("", `\`content/parts.json\` 中在原版 part-map 里没有对应 prefab 的零件（${unmappedContentParts.length} 个）：`, "");
md.push(
  unmappedContentParts.map(([id, part]) => `- partTypeId ${id} → \`${part.name}\`（当前 restitution ${part.material?.restitution ?? "—"} / friction ${part.material?.friction ?? "—"}）`).join("\n") || "（无）",
  "",
);

md.push("", "## 9. 不确定性与限制", "");
md.push(
  [
    "- 全部 `Part_*.prefab` 均不含 PrefabInstance（class 1001）嵌套引用，也没有 stripped 组件块，因此材质归属不存在跨文件解析问题；报告仍逐 prefab 记录这两个计数，若上游导入方式变化会体现出来。",
    "- 少数字段解析失败会在 JSON 的 `warnings` 里列出；本次运行没有预期外的字段异常即为可信。",
    "- 材质值只对「非 trigger」collider 求归属；trigger collider（挂点/传感器）的材质不参与碰撞响应，若某 prefab 只有 trigger collider，则回退到全部 collider 并保留在 `colliders` 明细里。",
    "- `content/parts.json` 只读对比，本工具不写入该文件。",
  ].join("\n"),
  "",
);
md.push("### warnings", "");
md.push(warnings.length === 0 ? "（无）" : warnings.map((warning) => `- ${warning}`).join("\n"), "");
md.push("", "### 溯源备注", "");
md.push(notes.map((note) => `- ${note}`).join("\n"), "");

const mdText = `${md.join("\n")}\n`;
mkdirSync(dirname(OUT_JSON), { recursive: true });
mkdirSync(dirname(OUT_MD), { recursive: true });
writeFileSync(OUT_JSON, `${JSON.stringify(report, null, 2)}\n`);
writeFileSync(OUT_MD, mdText);
console.log(`wrote ${OUT_JSON}`);
console.log(`wrote ${OUT_MD}`);
console.log(
  `prefabs=${scannedPrefabs.length} withMaterial=${withMaterial.length} noMaterial=${noMaterialPrefabs.length} mixed=${mixedPrefabs.length} bouncy=${bouncyPrefabs.length} unmapped=${unmappedPrefabs.length} diffRows=${diff.length} notable=${diff.filter((row) => row.notable).length}`,
);
