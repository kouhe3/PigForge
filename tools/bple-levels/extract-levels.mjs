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
// Usage: node tools/bple-levels/extract-levels.mjs [--bple <path>] [--json <path>] [--md <path>]
import { existsSync, mkdirSync, readdirSync, readFileSync, statSync, writeFileSync } from "node:fs";
import { basename, dirname, join, relative, resolve } from "node:path";
import { fileURLToPath } from "node:url";

const HERE = dirname(fileURLToPath(import.meta.url));
const REPO = resolve(HERE, "..", "..");

function arg(name, fallback) {
  const index = process.argv.indexOf(`--${name}`);
  return index >= 0 && process.argv[index + 1] ? process.argv[index + 1] : fallback;
}

const BPLE = resolve(arg("bple", process.env.BPLE_ROOT ?? join(REPO, "..", "BPLE 2022.1.9")));
const ASSETS = join(BPLE, "Assets");
const OUT_JSON = resolve(arg("json", join(REPO, "tasks", "bple-levels-report.json")));
const OUT_MD = resolve(arg("md", join(REPO, "tasks", "bple-levels-report.md")));

if (!existsSync(ASSETS)) {
  console.error(`BPLE project not found: ${ASSETS}\nPass --bple <path to a BPLE project root>.`);
  process.exit(2);
}

const BUNDLE_EXPECT = {
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

const failures = [];
const check = (condition, message) => {
  if (!condition) failures.push(message);
};

// ---------------------------------------------------------------- filesystem walk

function* walk(directory) {
  for (const entry of readdirSync(directory, { withFileTypes: true })) {
    const path = join(directory, entry.name);
    if (entry.isDirectory()) yield* walk(path);
    else yield path;
  }
}

// Unity YAML is regular enough for a line scanner, with one quirk: list entries sit at the SAME
// indentation as their key (Unity writes `  m_prefabs:` then `  - {fileID: ..., guid: ...}`), so
// the block ends at the first non-list line at or above the key's indentation. An entry is its
// `- ` line plus every deeper line under it (a level info spans four lines).
function collectList(text, key) {
  const lines = text.split("\n");
  const header = lines.findIndex((line) => new RegExp(`^\\s*${key}:\\s*$`).test(line));
  if (header < 0) return [];
  const indent = lines[header].length - lines[header].trimStart().length;
  const entries = [];
  let current = null;
  for (let index = header + 1; index < lines.length; index += 1) {
    const line = lines[index];
    if (line.trim() === "") continue;
    const lineIndent = line.length - line.trimStart().length;
    if (line.trimStart().startsWith("- ")) {
      if (lineIndent < indent) break;
      if (current) entries.push(current.join("\n"));
      current = [line.trim()];
      continue;
    }
    if (lineIndent <= indent) break;
    if (current) current.push(line.trim());
  }
  if (current) entries.push(current.join("\n"));
  return entries;
}

const guidToPath = new Map();
for (const file of walk(ASSETS)) {
  if (!file.endsWith(".meta")) continue;
  const text = readFileSync(file, "utf8");
  const match = /^guid: ([0-9a-f]{32})\s*$/m.exec(text);
  if (match) guidToPath.set(match[1], relative(BPLE, file.slice(0, -".meta".length)).replaceAll("\\", "/"));
}

// ---------------------------------------------------------------- loader prefabs (palettes)

const loadersByScene = new Map();
const LOADER_ROOT = join(ASSETS, "Resources", "levels");
for (const file of walk(LOADER_ROOT)) {
  if (!file.endsWith("_loader.prefab")) continue;
  const text = readFileSync(file, "utf8");
  const sceneName = /^\s*m_sceneName: (.+)$/m.exec(text)?.[1]?.trim();
  const referenceCount = collectList(text, "m_references").length;
  const guids = collectList(text, "m_prefabs").map((entry) => /guid: ([0-9a-f]{32})/.exec(entry)?.[1]);
  if (!sceneName) {
    check(false, `loader without m_sceneName: ${relative(BPLE, file)}`);
    continue;
  }
  for (const [index, guid] of guids.entries()) {
    check(Boolean(guid), `${relative(BPLE, file)}: m_prefabs[${index}] has no guid`);
  }
  const referenceGuids = collectList(text, "m_references").map((entry) => /guid: ([0-9a-f]{32})/.exec(entry)?.[1] ?? null);
  loadersByScene.set(sceneName.toLowerCase(), {
    sceneName,
    loaderPath: relative(BPLE, file).replaceAll("\\", "/"),
    paletteGuids: guids,
    referenceGuids,
    referenceCount,
  });
}

// ---------------------------------------------------------------- episode manifests (play order)

const episodes = [];
for (const file of walk(join(ASSETS, "GameObject"))) {
  if (!/Episode.*Levels\.prefab$/.test(file)) continue;
  const text = readFileSync(file, "utf8");
  const name = /^\s*m_name: (.+)$/m.exec(text)?.[1]?.trim();
  const label = /^\s*m_label: (.+)$/m.exec(text)?.[1]?.trim();
  const totalLevelCountRaw = /^\s*totalLevelCount: (-?\d+)$/m.exec(text)?.[1];
  const totalLevelCount = totalLevelCountRaw === undefined ? null : Number(totalLevelCountRaw);
  const starLimitsHex = /^\s*m_starLimits: ([0-9a-f]+)$/m.exec(text)?.[1];
  const starLimits = [];
  if (starLimitsHex) {
    const bytes = Buffer.from(starLimitsHex, "hex");
    for (let offset = 0; offset + 4 <= bytes.length; offset += 4) starLimits.push(bytes.readInt32LE(offset));
  }
  check(starLimits.length === 0 || starLimits.length === 9, `${basename(file)}: m_starLimits decoded to ${starLimits.length} int32, expected 9`);
  // Episode manifests list `- sceneName: X` followed by the loader guid/path; the race and sandbox
  // manifests use a different class that lists `- m_levelLoaderPath: .../<scene>_loader.prefab`
  // only, so the scene name comes off that path.
  const levelInfos = [...collectList(text, "m_levelInfos"), ...collectList(text, "m_levels")].map((entry) => {
    const explicit = /sceneName: (.+)$/.exec(entry)?.[1]?.trim();
    const path = /([^/\\]+)_loader\.prefab/.exec(entry)?.[1];
    const loaderGuid = /levelLoaderGUID: ([0-9a-f]{32})/.exec(entry)?.[1] ?? null;
    return { sceneName: explicit ?? path ?? null, loaderGuid };
  });
  check(levelInfos.length > 0, `${basename(file)}: no m_levelInfos entries`);
  for (const info of levelInfos) check(Boolean(info.sceneName), `${basename(file)}: level info without a scene name`);
  episodes.push({
    file: relative(BPLE, file).replaceAll("\\", "/"),
    name,
    label,
    totalLevelCount,
    starLimits,
    levelInfos,
  });
}
check(episodes.length >= 8, `expected at least 8 Episode*Levels manifests, found ${episodes.length}`);
for (const episode of episodes) {
  check(
    episode.totalLevelCount === null || episode.totalLevelCount === episode.levelInfos.length,
    `${basename(episode.file)}: totalLevelCount ${episode.totalLevelCount} != ${episode.levelInfos.length} level infos`,
  );
}
check(
  episodes.reduce((total, episode) => total + episode.levelInfos.length, 0) === 277,
  `manifests list ${episodes.reduce((total, episode) => total + episode.levelInfos.length, 0)} levels, expected 277`,
);

const episodeByScene = new Map();
for (const episode of episodes) {
  episode.levelInfos.forEach((info, index) => {
    episodeByScene.set(info.sceneName.toLowerCase(), { episode: episode.name, index });
  });
}

// ---------------------------------------------------------------- the binary level format

class Reader {
  constructor(buffer) {
    this.buffer = buffer;
    this.offset = 0;
  }
  int16() {
    const value = this.buffer.readInt16LE(this.offset);
    this.offset += 2;
    return value;
  }
  int32() {
    const value = this.buffer.readInt32LE(this.offset);
    this.offset += 4;
    return value;
  }
  uint32() {
    const value = this.buffer.readUInt32LE(this.offset);
    this.offset += 4;
    return value;
  }
  float() {
    const value = this.buffer.readFloatLE(this.offset);
    this.offset += 4;
    return value;
  }
  byte() {
    return this.buffer[this.offset++];
  }
  bool() {
    return this.byte() !== 0;
  }
  string() {
    let length = 0;
    let shift = 0;
    for (;;) {
      const byte = this.byte();
      length |= (byte & 0x7f) << shift;
      if ((byte & 0x80) === 0) break;
      shift += 7;
    }
    const value = this.buffer.toString("utf8", this.offset, this.offset + length);
    this.offset += length;
    return value;
  }
  vector2() {
    return [this.float(), this.float()];
  }
  vector3() {
    return [this.float(), this.float(), this.float()];
  }
  mesh() {
    const vertexCount = this.int32();
    check(vertexCount >= 0, `negative vertex count ${vertexCount}`);
    this.offset += vertexCount * 8;
    const indexCount = this.int32();
    check(indexCount >= 0, `negative index count ${indexCount}`);
    this.offset += indexCount * 2;
    return { vertexCount, indexCount };
  }
}

const readLevel = (buffer) => {
  const reader = new Reader(buffer);
  const level = {
    rootCount: 0,
    groups: 0,
    instances: 0,
    terrain: [],
    overrideBytes: [],
    prefabIndexes: new Map(),
    instanceNames: new Map(),
    maxDepth: 0,
  };
  const readData = (node) => {
    const type = reader.byte();
    node.dataType = type;
    if (type === 1) {
      const fillOffset = reader.vector2();
      const fill = reader.mesh();
      const fillColor = reader.uint32();
      const fillTextureIndex = reader.int32();
      const curve = reader.mesh();
      const curveTextureCount = reader.int32();
      const curveTextures = [];
      for (let index = 0; index < curveTextureCount; index += 1) {
        curveTextures.push({
          textureIndex: reader.int32(),
          size: reader.vector2(),
          fixedAngle: reader.bool(),
          fadeThreshold: reader.float(),
        });
      }
      let controlTextureBytes = 0;
      if (reader.int32() > 0) {
        controlTextureBytes = reader.int32();
        reader.offset += controlTextureBytes;
      }
      const hasCollider = reader.bool();
      level.terrain.push({ fillOffset, fill, fillColor, fillTextureIndex, curve, curveTextureCount, controlTextureBytes, hasCollider });
    } else if (type === 2) {
      const length = reader.int32();
      reader.offset += length;
      level.overrideBytes.push(length);
    }
  };
  const readObject = (depth) => {
    const childCount = reader.int16();
    level.maxDepth = Math.max(level.maxDepth, depth);
    if (childCount === 0) {
      const name = reader.string();
      const prefabIndex = reader.int16();
      const position = reader.vector3();
      const euler = reader.vector3();
      const localScale = reader.vector3();
      level.instances += 1;
      level.prefabIndexes.set(prefabIndex, (level.prefabIndexes.get(prefabIndex) ?? 0) + 1);
      level.instanceNames.set(name, (level.instanceNames.get(name) ?? 0) + 1);
      readData({ name, prefabIndex, position, euler, localScale });
    } else {
      reader.string();
      reader.vector3();
      level.groups += 1;
      for (let index = 0; index < childCount; index += 1) readObject(depth + 1);
    }
  };
  level.rootCount = reader.int32();
  for (let index = 0; index < level.rootCount; index += 1) readObject(0);
  level.trailingBytes = buffer.length - reader.offset;
  return level;
};

// ---------------------------------------------------------------- scan + decode every level

const dataFiles = [];
for (const bundleDirectory of readdirSync(join(ASSETS, "assetbundles"), { withFileTypes: true })) {
  if (!bundleDirectory.isDirectory() || !bundleDirectory.name.includes("levels")) continue;
  const directory = join(ASSETS, "assetbundles", bundleDirectory.name);
  for (const entry of readdirSync(directory)) {
    if (entry.endsWith("_data.bytes")) dataFiles.push({ bundle: bundleDirectory.name, file: join(directory, entry) });
  }
}

const bundleCounts = {};
const levels = [];
const paletteUsage = new Map();
const referenceUsage = new Map();
const nameMismatches = [];

for (const { bundle, file } of dataFiles) {
  const sceneName = basename(file).replace(/_data\.bytes$/, "");
  bundleCounts[bundle] = (bundleCounts[bundle] ?? 0) + 1;
  const buffer = readFileSync(file);
  const data = readLevel(buffer);
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

const partMap = JSON.parse(readFileSync(join(HERE, "..", "bple-textures", "part-map.json"), "utf8")).parts;
const prefabToPartTypeId = new Map();
for (const [partTypeId, prefab] of Object.entries(partMap)) if (prefab) prefabToPartTypeId.set(prefab, Number(partTypeId));

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
  textures: [...referenceUsage.entries()]
    .map(([path, count]) => ({ path, levels: count }))
    .sort((left, right) => right.levels - left.levels || left.path.localeCompare(right.path)),
  nameMismatches,
  failures,
};

mkdirSync(dirname(OUT_JSON), { recursive: true });
writeFileSync(OUT_JSON, `${JSON.stringify(report, null, 2)}\n`);

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
