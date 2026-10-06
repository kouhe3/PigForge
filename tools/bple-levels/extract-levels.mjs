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
import { arg } from "./lib/args.mjs";
import { buildGuidIndex, episodeIndex, loadEpisodes, loadLoaders } from "./lib/unity-yaml.mjs";
import { readLevel } from "./lib/reader.mjs";
import { OUTLINE_LOOP, classifyOutline } from "./lib/outline.mjs";
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

  for (const terrain of data.terrain) {
    if (!terrain.hasCollider) continue;
    const outline = classifyOutline(terrain.fill);
    outlineHistogram.set(outline.label, (outlineHistogram.get(outline.label) ?? 0) + 1);
    check(
      outline.label === OUTLINE_LOOP || outline.label.includes("closed loop"),
      `${basename(file)}: a terrain with a collider has no closed outline to extrude (${outline.label}; ` +
        `${outline.vertices} vertices, ${outline.boundaryEdges} boundary edges over ${outline.boundaryVertices} of them, ` +
        `edge count per vertex ${outline.links})`,
    );
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
    terrain: sum("terrain"),
    terrainWithCollider: sum("terrainWithCollider"),
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
md.push("", `### 碰撞轮廓（\`hasCollider\` 的 ${report.totals.terrainWithCollider} 个地形）`, "");
md.push("原版 `CreateCollider` 把 fill 网格的**顶点表按顺序**当作轮廓挤出（`LevelLoader.cs:339-380`）。实测：", "");
for (const entry of report.collisionOutlines) md.push(`- ${entry.key}：**${entry.count}**`);
md.push("", "⇒ 转换器必须**走边界环**（甚至度数顶点：4 度 = 两个环在一点相接），不能直接信任顶点表顺序。", "");
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
console.log(`name mismatches: ${nameMismatches.length}`);
if (failures.length > 0) {
  console.error(`\n${failures.length} invariant failure(s):`);
  for (const failure of failures.slice(0, 40)) console.error(`  - ${failure}`);
  process.exit(1);
}
console.log(`report: ${OUT_JSON}\n        ${OUT_MD}`);
