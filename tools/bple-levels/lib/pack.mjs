// The level pack's inventory: where the level files live, the part map, and the one numeric
// constant the content needs from the original's C# source. Nothing here is hand-written when it
// can be read out of the pack (the tools/bple-* rule).

import { readFileSync, readdirSync } from "node:fs";
import { dirname, join, resolve } from "node:path";
import { fileURLToPath } from "node:url";

const LIB = dirname(fileURLToPath(import.meta.url));
export const TOOLS = resolve(LIB, "..", "..");
export const REPO = resolve(TOOLS, "..");

/// `Assets/assetbundles/<bundle>/<scene>_data.bytes` files the pack must hold, per bundle.
export const BUNDLE_EXPECT = {
  "episode_1_levels.unity3d": 45,
  "episode_2_levels.unity3d": 45,
  "episode_3_levels.unity3d": 45,
  "episode_4_levels.unity3d": 45,
  "episode_5_levels.unity3d": 30,
  "episode_6_levels.unity3d": 45,
  "episode_race_levels.unity3d": 8,
  "episode_sandbox_levels.unity3d": 10,
  "episode_sandbox_levels_2.unity3d": 4,
};

/// Every `*_data.bytes` under `Assets/assetbundles/*levels*`, as
/// `{ bundle, file, sceneName }`, sorted by bundle then scene so both tools walk the same order.
export function discoverDataFiles(assetsRoot) {
  const dataFiles = [];
  for (const bundleDirectory of readdirSync(join(assetsRoot, "assetbundles"), { withFileTypes: true })) {
    if (!bundleDirectory.isDirectory() || !bundleDirectory.name.includes("levels")) continue;
    const directory = join(assetsRoot, "assetbundles", bundleDirectory.name);
    for (const entry of readdirSync(directory)) {
      if (entry.endsWith("_data.bytes")) dataFiles.push({ bundle: bundleDirectory.name, file: join(directory, entry) });
    }
  }
  return dataFiles;
}

/// `tools/bple-textures/part-map.json` in both directions. `parts` is the curated part list (the
/// extractor classifies palettes with it); `prefabToPartTypeId` additionally folds in the
/// `variants` so a placed `Part_Balloon_02_SET` maps to its own id instead of looking unmapped.
export function loadPartMap() {
  const file = join(TOOLS, "bple-textures", "part-map.json");
  const json = JSON.parse(readFileSync(file, "utf8"));
  const partsByType = new Map();
  const variantsByType = new Map();
  const prefabToPartTypeId = new Map();
  const duplicates = [];
  for (const [partTypeId, prefab] of Object.entries(json.parts ?? {})) {
    if (!prefab) continue;
    partsByType.set(Number(partTypeId), prefab);
  }
  for (const [partTypeId, prefab] of Object.entries(json.variants ?? {})) {
    if (!prefab) continue;
    variantsByType.set(Number(partTypeId), prefab);
  }
  for (const [partTypeId, prefab] of [...partsByType, ...variantsByType]) {
    if (prefabToPartTypeId.has(prefab)) duplicates.push(prefab);
    prefabToPartTypeId.set(prefab, partTypeId);
  }
  return { partsByType, variantsByType, prefabToPartTypeId, duplicates };
}

/// `e2dConstants.COLLISION_MESH_Z_DEPTH` read out of the original's C# source -- the extrusion
/// depth every `e2dTerrain` collider uses (`LevelLoader.CreateCollider`, LevelLoader.cs:350-351).
/// Extracted, never hard-coded: 10f today, but the content must follow the source if it moves.
export function readCollisionMeshDepth(bpleRoot, check) {
  const file = join(bpleRoot, "Assets", "Scripts", "Assembly-CSharp", "e2dConstants.cs");
  const text = readFileSync(file, "utf8");
  const matches = [...text.matchAll(/COLLISION_MESH_Z_DEPTH\s*=\s*(-?[0-9]*\.?[0-9]+)f\s*;/g)].map((match) => Number(match[1]));
  check(matches.length === 1, `${file}: expected exactly one COLLISION_MESH_Z_DEPTH declaration, found ${matches.length}`);
  const depth = matches[0];
  check(Number.isFinite(depth) && depth > 0, `${file}: COLLISION_MESH_Z_DEPTH must be finite and positive, got ${depth}`);
  return depth;
}
