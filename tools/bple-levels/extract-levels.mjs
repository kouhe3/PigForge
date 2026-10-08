// Level-pack extractor: decodes the original's 277 binary level files and the per-level palettes
// that give their prefab indices meaning, then reports what a PigForge level port has to account
// for. This is the ONLY admissible source for level geometry: no number in a PigForge level file
// may be hand-written when this tool can read it out of the original (the rule tools/bple-* set).
//
// Where the data lives (BPLE project, read-only):
//
//   <bple>/Assets/assetbundles/<episode>_levels*/<scene>_data.bytes   the level itself
//   <bple>/Assets/Resources/levels/<episode*>[/_cave]/<scene>_loader.prefab
//       the `LevelLoader` component: `m_sceneName` names the data file and `m_prefabs` is the
//       palette an instance's `PrefabIndex` indexes into (guid list, resolved through *.meta).
//   <bple>/Assets/GameObject/Episode*Levels.prefab
//       the play order: `m_levelInfos[].sceneName` + `totalLevelCount` + the packed
//       `m_starLimits` blob (9 int32 little-endian: 8/8/8/10/10/10/12/12/12).
//
// Read the levels from the PRISTINE `BPLE 2022.1.9` copy, not `BPLE_Unity6`: all 277 `_data.bytes`
// files are byte-identical between the two, but the Unity 6 migration dropped palette entries from
// three loaders that those files still index into -- `episode_6_level_12` 152 -> 151,
// `episode_6_level_ii` 191 -> 190, `mmsandbox` 804 -> 799 -- and a short palette silently
// mis-resolves every instance past the missing slot. The invariant below catches that (measured
// 2026-10-06: pristine 0 failures, BPLE_Unity6 4), which is also why the default root is pristine.
//
// The wire format (little-endian; `BinaryReader` semantics, so strings are 7-bit length prefixed
// UTF-8) -- LevelLoader.cs:88-208 is the working reader, LevelFormatReader.cs:9-124 the same thing
// with the Unity calls stripped out:
//
//   int32 rootCount, then rootCount objects.
//   object  := int16 childCount
//              childCount == 0 -> prefab instance: string name; int16 prefabIndex;
//                                 Vector3 position, Vector3 euler, Vector3 localScale;
//                                 then data
//              childCount  > 0 -> group: string name; Vector3 position; childCount objects
//   data    := uint8 type: 0 none | 1 terrain | 2 prefabOverrides
//   terrain := float fillTextureTileOffsetX, float fillTextureTileOffsetY;
//              (int32 n, n * Vector2) fillVertices; (int32 m, m * int16) fillTriangles;
//              uint32 fillColor (RGBA bytes, read back through LevelLoader.ReadColor);
//              int32 fillTextureIndex (into the loader's `m_references`);
//              (int32 n, n * Vector2) curveVertices; (int32 m, m * int16) curveTriangles;
//              int32 curveTextureCount, then per entry: int32 textureIndex, Vector2 size,
//              bool fixedAngle, float fadeThreshold;
//              int32 controlFlag; if > 0: int32 byteLength + byteLength bytes (a PNG);
//              bool hasCollider
//   overrides := int32 byteLength + byteLength bytes (UTF-8, ObjectDeserializer format)
//
// Two things the reader does on top of the file and that a port must reproduce: every instance's
// position/scale is multiplied by `IN TerrainScale x user TerrainScale` (LevelLoader.cs:186-193;
// `INDeclarationSettingsExp.json` declares 1.0, so vanilla is identity), and a terrain with
// `hasCollider` gets a MeshCollider extruded from the **fill** polygon (LevelLoader.cs:339-380,
// depth e2dConstants.COLLISION_MESH_Z_DEPTH) rather than from the curve mesh.
//
// Invariants this tool fails on (drift must not pass silently):
//   - the nine bundles hold exactly 45/45/45/45/30/45/8/10/4 = 277 files;
//   - every file decodes byte-exactly (no trailing bytes, no short read);
//   - every level has exactly one `LevelLoader` prefab whose `m_sceneName` matches its file name;
//   - every `PrefabIndex` is inside that palette and every palette guid resolves to a real asset;
//   - every level places at least one terrain object.
//
// The decoder itself lives in `lib/` and is shared with `build-levels.mjs`; this file is the CLI
// that reports on it. See `docs/specs/original-level-pack.md` for the measured totals.
//
// Usage: node tools/bple-levels/extract-levels.mjs [--bple <path>] [--json <path>] [--md <path>]
import { existsSync, mkdirSync, readFileSync, writeFileSync } from "node:fs";
import { basename, dirname, join, resolve } from "node:path";
import { arg } from "../lib/args.mjs";
import { buildGuidIndex, episodeIndex, loadEpisodes, loadLoaders } from "./lib/unity-yaml.mjs";
import { readLevel } from "./lib/reader.mjs";
import { OUTLINE_LOOP, classifyOutline } from "./lib/outline.mjs";
import { loadTerrainFillTiles, readTextureImport } from "./lib/fill.mjs";
import { curveLayerRuns, curveRows, readCurveTextureWrap } from "./lib/curve.mjs";
import { readCameraLimits } from "./lib/overrides.mjs";
import { BUNDLE_EXPECT, REPO, discoverDataFiles, loadPartMap } from "./lib/pack.mjs";
import { formatJson } from "./lib/write.mjs";

const BPLE = resolve(arg("bple", process.env.BPLE_ROOT ?? join(REPO, "..", "BPLE 2022.1.9")));
const ASSETS = join(BPLE, "Assets");
const OUT_JSON = resolve(arg("json", join(REPO, "tasks", "bple-levels-report.json")));
const OUT_MD = resolve(arg("md", join(REPO, "tasks", "bple-levels-report.md")));

if (!existsSync(ASSETS)) {
  console.error(`BPLE project not found: ${ASSETS}\nPass --bple <path to a BPLE project root>.`);
  process.exit(2);
}

const failures = [];
const check = (condition, message) => {
  if (!condition) failures.push(message);
};

const guidToPath = buildGuidIndex(BPLE, ASSETS);
const loadersByScene = loadLoaders(BPLE, ASSETS, check);
const episodes = loadEpisodes(BPLE, ASSETS, check);
const episodeByScene = episodeIndex(episodes);

// ---------------------------------------------------------------- scan + decode every level

const dataFiles = discoverDataFiles(ASSETS);

const bundleCounts = {};
const levels = [];
const paletteUsage = new Map();
const referenceUsage = new Map();
const nameMismatches = [];
const terrainEulerHistogram = new Map();
const terrainScaleHistogram = new Map();
const goalNameHistogram = new Map();
const outlineHistogram = new Map();
const fillTextureUsage = new Map();
const fillColorHistogram = new Map();
const fillOffsetHistogram = new Map();
const terrainPrefabs = new Set();
const curveNodeHistogram = new Map();
const curveWidthHistogram = new Map();
const curveSegmentHistogram = new Map();
const curveRunHistogram = new Map();
const curveSizeHistogram = new Map();
const curveTextureCountHistogram = new Map();
const curveTextureUsage = new Map();
let curveTerrains = 0;
let curveNodes = 0;
let curveRuns = 0;
let curveNodesInSecondLayer = 0;
let levelsWithGoal = 0;

for (const { bundle, file } of dataFiles) {
  const sceneName = basename(file).replace(/_data\.bytes$/, "");
  bundleCounts[bundle] = (bundleCounts[bundle] ?? 0) + 1;
  const buffer = readFileSync(file);
  const data = readLevel(buffer, check);
  check(data.trailingBytes === 0, `${basename(file)}: ${data.trailingBytes} trailing bytes`);
  check(data.terrain.length > 0, `${basename(file)}: no terrain object`);

  const loader = loadersByScene.get(sceneName.toLowerCase());
  check(Boolean(loader), `${basename(file)}: no LevelLoader prefab with m_sceneName ${sceneName}`);
  const palette = (loader?.paletteGuids ?? []).map((guid) => {
    const path = guidToPath.get(guid);
    check(Boolean(path), `${basename(file)}: palette guid ${guid} resolves to no asset`);
    return path ?? `<unresolved:${guid}>`;
  });
  for (const index of data.prefabIndexes.keys()) {
    check(
      index >= 0 && index < palette.length,
      `${basename(file)}: PrefabIndex ${index} outside palette of ${palette.length}` +
        (index >= palette.length ? " (a migrated BPLE_Unity6 copy loses palette entries; use the pristine BPLE 2022.1.9)" : ""),
    );
  }
  const paletteNames = new Set(palette.map((path) => basename(path, ".prefab")));
  for (const name of data.instanceNames.keys()) {
    if (!paletteNames.has(name)) nameMismatches.push(`${basename(file)}: instance "${name}" matches no palette prefab name`);
  }
  // `m_references` is the terrain's texture table: the fill material/texture and the curve
  // textures index into it, and the control texture is the PNG embedded in the level file.
  const referencePaths = (loader?.referenceGuids ?? []).map((guid) => {
    if (!guid) return "<null>";
    const path = guidToPath.get(guid);
    check(Boolean(path), `${basename(file)}: reference guid ${guid} resolves to no asset`);
    return path ?? `<unresolved:${guid}>`;
  });
  for (const path of referencePaths) referenceUsage.set(path, (referenceUsage.get(path) ?? 0) + 1);
  for (const transform of data.terrainTransforms) {
    const euler = transform.euler.map((value) => (Math.round(value * 57.29578 * 10) / 10).toFixed(1)).join(",");
    const scale = transform.localScale.map((value) => (Math.round(value * 10000) / 10000).toString()).join(",");
    terrainEulerHistogram.set(euler, (terrainEulerHistogram.get(euler) ?? 0) + 1);
    terrainScaleHistogram.set(scale, (terrainScaleHistogram.get(scale) ?? 0) + 1);
  }

  // Every terrain object becomes a content entry (v3 carries all 2146, not only the collider ones),
  // so its outline is walked for the collider *and* for the visual fill. The fill's other two inputs
  // are the level file's own: the texture index into `m_references`, the RGBA color and the tile
  // offset (`LevelLoader.cs:214-230`); the tile size lives on the terrain prefab (`lib/fill.mjs`).
  const levelFillTextures = new Set();
  for (const terrain of data.terrain) {
    const outline = classifyOutline(terrain.fill);
    const kind = terrain.hasCollider ? "collider" : "visual";
    const label = `${kind}: ${outline.label}`;
    outlineHistogram.set(label, (outlineHistogram.get(label) ?? 0) + 1);
    check(
      outline.label === OUTLINE_LOOP || outline.label.includes("closed loop"),
      `${basename(file)}: a ${kind} terrain has no closed outline to walk (${outline.label}; ` +
        `${outline.vertices} vertices, ${outline.boundaryEdges} boundary edges over ${outline.boundaryVertices} of them, ` +
        `edge count per vertex ${outline.links})`,
    );

    const prefab = palette[terrain.instance.prefabIndex];
    check(
      typeof prefab === "string" && !prefab.startsWith("<"),
      `${basename(file)}: terrain "${terrain.instance.name}" prefab ${prefab} does not resolve`,
    );
    if (typeof prefab === "string" && !prefab.startsWith("<")) terrainPrefabs.add(prefab);

    const fillTexture = referencePaths[terrain.fillTextureIndex];
    check(
      terrain.fillTextureIndex >= 0 && typeof fillTexture === "string" && !fillTexture.startsWith("<"),
      `${basename(file)}: terrain "${terrain.instance.name}" fill texture index ${terrain.fillTextureIndex} resolves to ${fillTexture ?? "<out of range>"}`,
    );
    if (typeof fillTexture === "string" && !fillTexture.startsWith("<")) {
      const usage = fillTextureUsage.get(fillTexture) ?? { path: fillTexture, terrains: 0, levels: 0 };
      usage.terrains += 1;
      fillTextureUsage.set(fillTexture, usage);
      levelFillTextures.add(fillTexture);
    }

    const color = `0x${terrain.fillColor.toString(16).padStart(8, "0")}`;
    fillColorHistogram.set(color, (fillColorHistogram.get(color) ?? 0) + 1);
    // Grouped at 1e-3: the file's floats are float32 (`6.199999809265137`), and 3 decimals is
    // already finer than any hand-set offset in the pack.
    const offset = terrain.fillOffset.map((value) => (Math.round(value * 1000) / 1000).toString()).join(",");
    fillOffsetHistogram.set(offset, (fillOffsetHistogram.get(offset) ?? 0) + 1);

    // The edge trim (`curve.shader`): structure, layer choice and layer textures are asserted here
    // (`lib/curve.mjs`), the histograms below report what the pack actually holds.
    const curveLabel = `${basename(file)}: terrain "${terrain.instance.name}"`;
    const rows = curveRows(terrain, check, curveLabel);
    if (rows) {
      const nodeKey = rows.nodes.length.toString();
      curveNodeHistogram.set(nodeKey, (curveNodeHistogram.get(nodeKey) ?? 0) + 1);
      for (let index = 0; index < rows.nodes.length; index += 1) {
        const width = Math.hypot(rows.stripe[index][0] - rows.nodes[index][0], rows.stripe[index][1] - rows.nodes[index][1]);
        const key = (Math.round(width * 1000) / 1000).toString();
        curveWidthHistogram.set(key, (curveWidthHistogram.get(key) ?? 0) + 1);
        if (index + 1 < rows.nodes.length) {
          const length = Math.hypot(rows.nodes[index + 1][0] - rows.nodes[index][0], rows.nodes[index + 1][1] - rows.nodes[index][1]);
          const lengthKey = (Math.round(length * 1000) / 1000).toString();
          curveSegmentHistogram.set(lengthKey, (curveSegmentHistogram.get(lengthKey) ?? 0) + 1);
        }
      }
      const runs = curveLayerRuns(terrain.controlTexture, rows.nodes.length, check, curveLabel);
      if (runs) {
        const runKey = runs.length.toString();
        curveRunHistogram.set(runKey, (curveRunHistogram.get(runKey) ?? 0) + 1);
        curveRuns += runs.length;
        curveNodesInSecondLayer += runs.reduce((total, run) => total + run[1], 0);
      }
      curveNodes += rows.nodes.length;
      curveTerrains += 1;
      check(
        terrain.curveTextureCount >= 2,
        `${curveLabel}: the curve declares ${terrain.curveTextureCount} layer texture(s); the shader samples _Splat0 and _Splat1`,
      );
      for (const texture of terrain.curveTextures ?? []) {
        const path = referencePaths[texture.textureIndex] ?? "<out of range>";
        const sizeKey = `${Math.round(texture.size[0] * 1000) / 1000}x${Math.round(texture.size[1] * 1000) / 1000}`;
        curveSizeHistogram.set(sizeKey, (curveSizeHistogram.get(sizeKey) ?? 0) + 1);
        const layersKey = terrain.curveTextureCount.toString();
        curveTextureCountHistogram.set(layersKey, (curveTextureCountHistogram.get(layersKey) ?? 0) + 1);
        if (typeof path === "string" && path.endsWith(".png")) {
          const wrap = readCurveTextureWrap(BPLE, path, check);
          const usage = curveTextureUsage.get(path) ?? { path, slots: 0, wrap };
          usage.slots += 1;
          curveTextureUsage.set(path, usage);
        } else {
          check(false, `${curveLabel}: a curve layer texture resolves to ${path}, expected a PNG`);
        }
      }
    }
  }
  for (const path of levelFillTextures) {
    fillTextureUsage.get(path).levels += 1;
  }

  for (const goal of data.goalInstances) {
    goalNameHistogram.set(goal.name, (goalNameHistogram.get(goal.name) ?? 0) + 1);
  }
  if (data.goalInstances.length > 0) levelsWithGoal += 1;
  for (const [index, count] of data.prefabIndexes) {
    const path = palette[index] ?? `<missing:${index}>`;
    const usage = paletteUsage.get(path) ?? { path, instances: 0, levels: 0 };
    usage.instances += count;
    usage.levels += 1;
    paletteUsage.set(path, usage);
  }

  const placement = episodeByScene.get(sceneName.toLowerCase());
  check(Boolean(placement), `${basename(file)}: not listed in any Episode*Levels manifest`);
  levels.push({
    bundle,
    sceneName,
    loaderPath: loader?.loaderPath ?? null,
    episode: placement?.episode ?? null,
    indexInEpisode: placement?.index ?? null,
    bytes: buffer.length,
    roots: data.rootCount,
    groups: data.groups,
    prefabInstances: data.instances,
    prefabOverrides: data.overrideBytes.length,
    overrideBytes: data.overrideBytes.reduce((sum, value) => sum + value, 0),
    cameraLimits: readCameraLimits(data.overrides, check),
    paletteSize: palette.length,
    references: loader?.referenceCount ?? null,
    terrain: data.terrain.length,
    terrainInstances: data.terrainTransforms.length,
    goalInstances: data.goalInstances.length,
    terrainWithCollider: data.terrain.filter((entry) => entry.hasCollider).length,
    fillVertices: data.terrain.reduce((sum, entry) => sum + entry.fill.vertexCount, 0),
    fillTriangles: data.terrain.reduce((sum, entry) => sum + entry.fill.indexCount, 0),
    curveVertices: data.terrain.reduce((sum, entry) => sum + entry.curve.vertexCount, 0),
    curveTriangles: data.terrain.reduce((sum, entry) => sum + entry.curve.indexCount, 0),
    curveTextures: data.terrain.reduce((sum, entry) => sum + entry.curveTextureCount, 0),
    controlTextures: data.terrain.filter((entry) => entry.controlTextureBytes > 0).length,
    maxDepth: data.maxDepth,
  });
}

check(dataFiles.length === 277, `expected 277 level files, found ${dataFiles.length}`);
for (const [bundle, expected] of Object.entries(BUNDLE_EXPECT)) {
  check(bundleCounts[bundle] === expected, `${bundle}: ${bundleCounts[bundle] ?? 0} files, expected ${expected}`);
}

// ---------------------------------------------------------------- fill (the ground's look)

// The tile size is the one fill input the level file does not carry, so it is read from every terrain
// prefab the levels place (and its histogram asserted); the texture import state is asserted too,
// because tiling UVs need Repeat (`lib/fill.mjs`).
const terrainFillTiles = loadTerrainFillTiles(BPLE, terrainPrefabs, check);
const fillTextures = [...fillTextureUsage.values()]
  .sort((left, right) => right.terrains - left.terrains || left.path.localeCompare(right.path));
for (const texture of fillTextures) readTextureImport(BPLE, texture.path, check);

const fillTileHistogram = new Map();
for (const { tileWidth, tileHeight } of terrainFillTiles.values()) {
  const key = `${tileWidth}x${tileHeight}`;
  fillTileHistogram.set(key, (fillTileHistogram.get(key) ?? 0) + 1);
}

// ---------------------------------------------------------------- curve (the edge trim's look)

// Every terrain's `_curve` mesh is asserted in the walk above (`lib/curve.mjs`): an even vertex count
// with two triangles per segment, a control texture one node per texel, layer channels r/g only, and
// at least two layer textures. What is left is the layer table itself: its textures' wrap modes.
const curveTextures = [...curveTextureUsage.values()]
  .map((usage) => ({ ...usage, wrap: usage.wrap ?? readCurveTextureWrap(BPLE, usage.path, check) }))
  .sort((left, right) => right.slots - left.slots || left.path.localeCompare(right.path));
check(
  curveTerrains === levels.reduce((total, level) => total + level.terrain, 0),
  `${curveTerrains} terrain(s) have a usable curve mesh; every terrain the pack stores has one`,
);

// ---------------------------------------------------------------- part coverage

const partMap = loadPartMap();
const prefabToPartTypeId = new Map();
for (const [partTypeId, prefab] of partMap.partsByType) prefabToPartTypeId.set(prefab, partTypeId);

const props = [];
const parts = [];
for (const usage of [...paletteUsage.values()].sort((left, right) => right.instances - left.instances)) {
  const name = basename(usage.path, ".prefab");
  const partTypeId = prefabToPartTypeId.get(name);
  const entry = { name, path: usage.path, instances: usage.instances, levels: usage.levels, partTypeId: partTypeId ?? null };
  if (partTypeId !== undefined) parts.push(entry);
  else props.push(entry);
}

// ---------------------------------------------------------------- report

const sum = (key) => levels.reduce((total, level) => total + level[key], 0);
const sortedHistogram = (histogram) => [...histogram.entries()]
  .map(([key, count]) => ({ key, count }))
  .sort((left, right) => right.count - left.count || left.key.localeCompare(right.key));

const report = {
  format: "pigforge.bple-levels",
  schemaVersion: 1,
  source: { bpleRoot: BPLE, generatedBy: "tools/bple-levels/extract-levels.mjs" },
  counts: {
    files: dataFiles.length,
    bundles: bundleCounts,
    episodes: episodes.length,
    levelsInManifests: episodes.reduce((total, episode) => total + episode.levelInfos.length, 0),
    loaders: loadersByScene.size,
    palettePrefabs: paletteUsage.size,
    partPrefabs: parts.length,
    nonPartPrefabs: props.length,
  },
  totals: {
    roots: sum("roots"),
    groups: sum("groups"),
    prefabInstances: sum("prefabInstances"),
    prefabOverrides: sum("prefabOverrides"),
    overrideBytes: sum("overrideBytes"),
    cameraLimits: levels.filter((level) => level.cameraLimits !== null).length,
    terrain: sum("terrain"),
    terrainWithCollider: sum("terrainWithCollider"),
    terrainVisualOnly: sum("terrain") - sum("terrainWithCollider"),
    fillVertices: sum("fillVertices"),
    fillTriangles: sum("fillTriangles"),
    curveVertices: sum("curveVertices"),
    curveTriangles: sum("curveTriangles"),
    curveTextures: sum("curveTextures"),
    controlTextures: sum("controlTextures"),
  },
  episodes,
  levels: levels.sort((left, right) => left.bundle.localeCompare(right.bundle) || left.sceneName.localeCompare(right.sceneName)),
  palette: { parts, props },
  terrainTransforms: {
    eulerDegrees: sortedHistogram(terrainEulerHistogram),
    localScale: sortedHistogram(terrainScaleHistogram),
  },
  collisionOutlines: sortedHistogram(outlineHistogram),
  fill: {
    textures: fillTextures,
    tileSizes: sortedHistogram(fillTileHistogram),
    terrainPrefabs: [...terrainFillTiles.entries()]
      .map(([path, tile]) => ({ path, tileWidth: tile.tileWidth, tileHeight: tile.tileHeight }))
      .sort((left, right) => left.path.localeCompare(right.path)),
    colors: sortedHistogram(fillColorHistogram),
    offsets: sortedHistogram(fillOffsetHistogram),
  },
  curve: {
    terrains: curveTerrains,
    nodes: curveNodes,
    runs: curveRuns,
    nodesInSecondLayer: curveNodesInSecondLayer,
    nodeCounts: sortedHistogram(curveNodeHistogram),
    bandWidths: sortedHistogram(curveWidthHistogram),
    segmentLengths: sortedHistogram(curveSegmentHistogram),
    runCounts: sortedHistogram(curveRunHistogram),
    layerSizes: sortedHistogram(curveSizeHistogram),
    layerCounts: sortedHistogram(curveTextureCountHistogram),
    textures: curveTextures,
  },
  goals: {
    levelsWithGoal,
    names: sortedHistogram(goalNameHistogram),
  },
  textures: [...referenceUsage.entries()]
    .map(([path, count]) => ({ path, levels: count }))
    .sort((left, right) => right.levels - left.levels || left.path.localeCompare(right.path)),
  nameMismatches,
  failures,
};

mkdirSync(dirname(OUT_JSON), { recursive: true });
writeFileSync(OUT_JSON, formatJson(report));

const histogramLine = (values) =>
  [...values.entries()].sort((left, right) => right[1] - left[1]).slice(0, 8).map(([key, count]) => `${key}:${count}`).join(" ");

const md = [];
md.push("# 原版关卡包报告", "");
md.push("来源：`tools/bple-levels/extract-levels.mjs`，解码 `Assets/assetbundles/*levels*/*.bytes` 全部关卡数据 +");
md.push("`Assets/Resources/levels/**/<scene>_loader.prefab` 的调色板（`m_prefabs` guid 经 `*.meta` 解析到 prefab 路径）。", "");
md.push("格式出处：`LevelLoader.cs:88-208`（运行期读法：对象树 / 地形 / `PrefabOverrides`）、`LevelFormatReader.cs:9-124`（纯解析版）、");
md.push("`LevelLoader.cs:218-262`（网格：`float2` 顶点 + `int16` 索引）、`:186-193`（每个实例的位置/缩放乘 `IN TerrainScale × user TerrainScale`，");
md.push("声明默认档 `INDeclarationSettingsExp.json` 里 `TerrainScale = 1.0`）、`:339-380`（`hasCollider` 的地形从 **fill** 多边形挤出 MeshCollider）。", "");
md.push("## 总量", "");
md.push(`- 关卡文件 **${report.counts.files}**，按 bundle：${Object.entries(bundleCounts).map(([bundle, count]) => `\`${bundle}\` ${count}`).join("、")}`);
md.push(`- episode 清单 **${report.counts.episodes}** 张（\`m_levelInfos\` 合计 ${report.counts.levelsInManifests} 关），per-level loader prefab **${report.counts.loaders}**`);
md.push(`- 调色板里去重后的 prefab **${report.counts.palettePrefabs}** 个：零件 **${report.counts.partPrefabs}**（\`part-map.json\` 认得）、非零件道具 **${report.counts.nonPartPrefabs}**`);
md.push(`- 实例总数 **${report.totals.prefabInstances}**（group ${report.totals.groups}）、地形对象 **${report.totals.terrain}**（带碰撞体 ${report.totals.terrainWithCollider}）、`);
md.push(`  \`PrefabOverrides\` ${report.totals.prefabOverrides} 条 / ${report.totals.overrideBytes} 字节`);
md.push(`- 相机界（\`PrefabOverrides\` 里 \`LevelManager.m_cameraLimits\`，\`Pig.cs:396-403\` 的出界矩形）**${report.totals.cameraLimits}/${report.counts.files}** 关有`);
md.push(`- 地形网格合计：fill ${report.totals.fillVertices} 顶点 / ${report.totals.fillTriangles} 索引，curve ${report.totals.curveVertices} 顶点 / ${report.totals.curveTriangles} 索引，`);
md.push(`  curve texture 条目 ${report.totals.curveTextures}，control texture ${report.totals.controlTextures}`, "");
md.push("## 非零件道具（PigForge 没有对应件，搬关必须先有）", "");
md.push("| prefab | 实例数 | 出现关卡数 |", "|---|---|---|");
for (const prop of props.slice(0, 40)) md.push(`| \`${prop.name}\` | ${prop.instances} | ${prop.levels} |`);
md.push("", `（共 ${props.length} 个；\`tasks/bple-levels-report.json\` 有全量）`, "");
md.push("", "## 关卡里出现的零件", "");
md.push("| PigForge id | prefab | 实例数 | 出现关卡数 |", "|---|---|---|---|");
for (const part of parts.sort((left, right) => left.partTypeId - right.partTypeId)) {
  md.push(`| \`${part.partTypeId}\` | \`${part.name}\` | ${part.instances} | ${part.levels} |`);
}
md.push("", "## 地形实例的变换与终点", "");
md.push(`地形实例（名字含 \`e2dTerrain\`）**${sum("terrainInstances")}** 个，\`euler\` 直方图（度）：`, "");
md.push(`- ${report.terrainTransforms.eulerDegrees.slice(0, 6).map((entry) => `\`(${entry.key})\` ${entry.count}`).join("、")}`);
md.push(`- \`localScale\` 直方图：${report.terrainTransforms.localScale.slice(0, 6).map((entry) => `\`(${entry.key})\` ${entry.count}`).join("、")}`);
md.push("", `终点类实例（名字以 \`Goal\` 开头）出现在 **${report.goals.levelsWithGoal}** 关里，共 ${report.goals.names.reduce((total, entry) => total + entry.count, 0)} 个：`, "");
md.push(`- ${report.goals.names.slice(0, 10).map((entry) => `\`${entry.key}\` ${entry.count}`).join("、")}`);
md.push("", `### 轮廓（全部 ${report.totals.terrain} 个地形：collider ${report.totals.terrainWithCollider} / visual ${report.totals.terrain - report.totals.terrainWithCollider}）`, "");
md.push("原版 `CreateCollider` 把 fill 网格的**顶点表按顺序**当作轮廓挤出（`LevelLoader.cs:339-380`）；");
md.push("内容 v3 把每个地形对象都写进关卡（`collider` 位决定房间建不建刚体），所以两种地形都要有闭环。实测：", "");
for (const entry of report.collisionOutlines) md.push(`- ${entry.key}：**${entry.count}**`);
md.push("", "⇒ 转换器必须**走边界环**（甚至度数顶点：4 度 = 两个环在一点相接），不能直接信任顶点表顺序。", "");
md.push("", "## 地形 fill（地面的样子：贴图 / 颜色 / tile）", "");
md.push("`Assets/Resources/fill.shader` 是 `tex2D(_MainTex, uv) * _Color`，uv 由 `LevelLoader.ReadMesh` 逐顶点算：");
md.push("`uv = (世界坐标 − tile 偏移) / tile 尺寸`（`LevelLoader.cs:279-290`）；贴图取 `m_references[fillTextureIndex]`");
md.push("（`:229-230`），`_Color` 取关卡文件里的 `uint32`（R 在高字节，`:172-181` 的 `byte * 0.003921569f`）。");
md.push("**tile 尺寸不在关卡文件里**，只有地形 prefab 有（`:217-218` 只写偏移）。实测：", "");
md.push(`- tile 尺寸直方图：${report.fill.tileSizes.map((entry) => `\`${entry.key}\` ${entry.count}`).join("、")}（${report.fill.terrainPrefabs.length} 个地形 prefab）`);
md.push(`- 颜色直方图（RGBA 字节）：${report.fill.colors.map((entry) => `\`${entry.key}\` ${entry.count}`).join("、")}`);
md.push(`- tile 偏移直方图（按 1e-3 归并）：${report.fill.offsets.slice(0, 6).map((entry) => `\`(${entry.key})\` ${entry.count}`).join("、")}`);
md.push(`- 用到的 fill 贴图 **${report.fill.textures.length}** 张，全部 Repeat + Bilinear（工具硬断言，见 \`lib/fill.mjs\`）：`, "");
md.push("| 贴图 | 地形数 | 关卡数 |", "|---|---|---|");
for (const texture of report.fill.textures) md.push(`| \`${texture.path}\` | ${texture.terrains} | ${texture.levels} |`);
md.push("", "⇒ 内容 v3 写 `fill { texture, color, tileOffset, tileSize }`。贴图本体**不入库**（原版美术，`.gitignore` 里那条），");
md.push("由 `build-levels.mjs` 复制到 `clients/web/public/assets/original/levels/`。见 `docs/specs/level-terrain-visuals.md`。", "");
md.push("", "## 地形 curve（边缘条带的几何与层）", "");
md.push("每块地形还有一条沿轮廓的 `_curve` 网格（`e2dTerrainCurveMesh.RebuildMesh`）：偶顶点是 `TerrainCurve` 节点、");
md.push("奇顶点是把该节点沿法线外推 `e2dCurveTexture.size.y` 再夹进地形包围盒（`e2dTerrainBoundary.EnsurePointIsInBoundary`）；");
md.push("`Assets/Resources/curve.shader` 取 u = 弧长 × `_SplatParams0.x`、v = 节点行 1 / 条带行 0，");
md.push("层由**嵌在关卡文件里**的控制贴图 G 通道在 `_Splat0`/`_Splat1` 间选。实测：", "");
md.push(`- 有 curve 网格的地形 **${report.curve.terrains}** / ${report.totals.terrain}，节点 **${report.curve.nodes}**（索引恒 = (节点−1)×6，0 例外）`);
md.push(`- 每地形节点数（前 6）：${report.curve.nodeCounts.slice(0, 6).map((entry) => `\`${entry.key}\` ${entry.count}`).join("、")}`);
md.push(`- 条带宽（\`|stripe − node|\`，按 1e-3 归并，前 6）：${report.curve.bandWidths.slice(0, 6).map((entry) => `\`${entry.key}\` ${entry.count}`).join("、")} 米`);
md.push(`- 段长（相邻节点距离，前 6）：${report.curve.segmentLengths.slice(0, 6).map((entry) => `\`${entry.key}\` ${entry.count}`).join("、")} 米`);
md.push(`- 第二层运行段合计 **${report.curve.runs}**，命中节点 **${report.curve.nodesInSecondLayer}** / ${report.curve.nodes}；`);
md.push(`- 每地形段数（前 6）：${report.curve.runCounts.slice(0, 6).map((entry) => `\`${entry.key}\` ${entry.count}`).join("、")}`);
md.push(`- \`e2dCurveTexture.size\`（前 6，x×y 米）：${report.curve.layerSizes.slice(0, 6).map((entry) => `\`${entry.key}\` ${entry.count}`).join("、")}；每地形层数：${report.curve.layerCounts.map((entry) => `\`${entry.key}\` ${entry.count}`).join("、")}`);
md.push("", "| 层贴图 | 槽位引用数 | wrap |", "|---|---|---|");
for (const texture of report.curve.textures) {
  md.push(`| \`${texture.path}\` | ${texture.slots} | ${texture.wrap} |`);
}
md.push("", "⇒ 内容 v4 写 `curve { nodes, stripe, textures[{ texture, wrap }], uScale, splat1 }`：控制贴图只用来折出 `splat1` 运行段，");
md.push("其余三样都直接进内容；wrap 必须保留——u = 弧长 × 10（≈ 每米 10 次）远超 1，Clamp 的层只会显示贴图最右一列。", "");
md.push("", "## 地形贴图表（`m_references`）", "");
md.push(`去重后 **${report.textures.length}** 个资源，被 loader 引用；每个地形还有一张**嵌在关卡文件里**的控制贴图（PNG，合计 ${report.totals.controlTextures} 张）。`, "");
md.push("| 资源 | 被多少关卡的 loader 引用 |", "|---|---|");
for (const texture of report.textures.slice(0, 25)) md.push(`| \`${texture.path}\` | ${texture.levels} |`);
md.push("", "## 每关", "");
md.push("| bundle | scene | episode | # | 字节 | 实例 | group | 地形 | fill 顶点 | curve 顶点 | overrides | 调色板 |", "|---|---|---|---|---|---|---|---|---|---|---|---|");
for (const level of report.levels) {
  md.push(`| ${level.bundle} | \`${level.sceneName}\` | ${level.episode ?? "—"} | ${level.indexInEpisode ?? "—"} | ${level.bytes} | ${level.prefabInstances} | ${level.groups} | ${level.terrain} | ${level.fillVertices} | ${level.curveVertices} | ${level.prefabOverrides} | ${level.paletteSize} |`);
}
md.push("", "## 实例标签 vs 调色板 prefab 名", "");
md.push("关卡作者给每个实例起的名字（`LevelLoader.cs:113` 的 `gameObject.name = text`）**不是** prefab 名：");
md.push("搬关要的是「调色板下标 → prefab」，标签只用于辨认。两处实测差异：");
md.push(`调色板里最常复用的 prefab 是 \`${props[0]?.name}\`（${props[0]?.instances} 个实例跨 ${props[0]?.levels} 关），标签靠后缀区分；`);
md.push("`background_*_set` 系族标签是大写，资产名是小写。", "");
md.push(nameMismatches.length === 0 ? "全部实例标签都能在所在关卡的调色板里找到同名 prefab。" : `标签与调色板名不同的实例 **${nameMismatches.length}** 条（前 20）：`);
for (const mismatch of nameMismatches.slice(0, 20)) md.push(`- ${mismatch}`);
writeFileSync(OUT_MD, `${md.join("\n")}\n`);

console.log(`files: ${report.counts.files}  bundles: ${histogramLine(new Map(Object.entries(bundleCounts)))}`);
console.log(`instances: ${report.totals.prefabInstances}  groups: ${report.totals.groups}  terrain: ${report.totals.terrain}`);
console.log(`palette prefabs: ${report.counts.palettePrefabs} (parts ${report.counts.partPrefabs} / props ${report.counts.nonPartPrefabs})`);
console.log(`camera limits: ${report.totals.cameraLimits}/${report.counts.files} levels`);
console.log(
  `fill: ${report.fill.textures.length} texture(s) over ${report.totals.terrain} terrain(s) ` +
    `(collider ${report.totals.terrainWithCollider} / visual ${report.totals.terrainVisualOnly}), ` +
    `tile ${report.fill.tileSizes.map((entry) => entry.key).join("/")}, ${report.fill.colors.length} color(s)`,
);
console.log(`name mismatches: ${nameMismatches.length}`);
if (failures.length > 0) {
  console.error(`\n${failures.length} invariant failure(s):`);
  for (const failure of failures.slice(0, 40)) console.error(`  - ${failure}`);
  process.exit(1);
}
console.log(`report: ${OUT_JSON}\n        ${OUT_MD}`);
