// Level builder: turns the original's 277 binary level files into PigForge level content v2
// (`content/levels/original/<area>/<scene>.json`) -- the terrain mesh the original bakes into
// every `e2dTerrain` collider, the finish trigger, the map bounds and the (rare) placed parts.
//
// It is the write side of the same decoder the extractor reports with (`lib/`), so every emitted
// number comes out of the pack: the palette resolves a `PrefabIndex` to its prefab, the goal zone
// is that prefab's own trigger BoxCollider, the extrusion depth is `e2dConstants.cs`, and the
// terrain loops are the fill mesh's real boundary (`lib/outline.mjs` walks the boundary fan,
// because the vertex table is not the outline for 5 of the 1648 collider terrains -- see
// docs/specs/original-level-pack.md §3). Nothing here is hand-written that the pack defines.
//
// Output shape (`lib/write.mjs`): the readable 2-space layout for `format`/`schemaVersion`/
// `contentVersion`/`goalZone`/`bounds`/`spawns`, one `terrain` entry per object with its
// `position`, `depth` and `loops`, and one line per outline loop -- the pack is three quarters
// outline points, so those carry the file (33.8 MB pretty-printed, 8.7 MB this way). Every number
// in a content file is exactly a float32 -- the pack reaches us through `BinaryReader.ReadSingle`
// and PigForge stores these fields as `float` -- so the derived corners (bounds, goal zone, spawn
// angle) are rounded here and the writer prints each number as the shortest decimal that reads back
// as the same float32; anything that is not a float32 is refused, not widened.
//
// Determinism and idempotency: every object is built in a fixed key order, JSON is written with LF
// and a trailing newline, and a file is only touched when its bytes differ, so running the tool
// twice rewrites nothing ("0 changed"). `--dry-run` prints the same plan and the same diff without
// writing. Any terrain whose boundary is not closed loops, any palette index that does not resolve,
// any non-identity terrain transform or unrepresentable spawn rotation, is skipped/reported and
// makes the run exit non-zero -- drift never passes silently.
//
// Usage:
//   node tools/bple-levels/build-levels.mjs [--bple <path>] [--out <dir>] [--dry-run]
//                                          [--json <path>] [--md <path>]

import { existsSync, mkdirSync, readFileSync, writeFileSync } from "node:fs";
import { basename, dirname, join, resolve } from "node:path";
import { arg, flag } from "./lib/args.mjs";
import { buildGuidIndex, loadEpisodes, loadLoaders } from "./lib/unity-yaml.mjs";
import { readLevel } from "./lib/reader.mjs";
import { outlineLoops } from "./lib/outline.mjs";
import { BUNDLE_EXPECT, REPO, discoverDataFiles, loadPartMap, readCollisionMeshDepth } from "./lib/pack.mjs";
import { readBoxCollider } from "./lib/goal.mjs";
import { formatJson, formatLevelDocument, sha256 } from "./lib/write.mjs";

const BPLE = resolve(arg("bple", process.env.BPLE_ROOT ?? join(REPO, "..", "BPLE 2022.1.9")));
const ASSETS = join(BPLE, "Assets");
const OUT = resolve(arg("out", join(REPO, "content", "levels", "original")));
const OUT_JSON = resolve(arg("json", join(REPO, "tasks", "build-report.json")));
const OUT_MD = resolve(arg("md", join(REPO, "tasks", "build-report.md")));
const DRY_RUN = flag("dry-run");

if (!existsSync(ASSETS)) {
  console.error(`BPLE project not found: ${ASSETS}\nPass --bple <path to a BPLE project root>.`);
  process.exit(2);
}

const failures = [];
const check = (condition, message) => {
  if (!condition) failures.push(message);
};

// The terrain transform is representable as `{ position, ... }` only when the instance is not
// rotated or scaled: 2146/2146 terrains are, and the tolerance only absorbs float32 noise (the
// largest measured deviation is one euler component at 4.6e-5 rad). Anything larger is reported,
// never silently dropped -- the schema has no place to put it.
const ROTATION_EPSILON = 0.0001;
const SCALE_EPSILON = 0.0001;
// The original has no out-of-bounds kill; PigForge's is a safety net, so bounds only has to stop a
// pig wandering to infinity, not clip level play. 25 = 2.5 x the collider depth (the extrusion the
// terrain sticks into z).
const BOUNDS_MARGIN = 25;

const contentVersionOf = (sceneName) => sceneName.replaceAll(" ", "-");
const areaOf = (bundle) => bundle.replace(/\.unity3d$/, "");

/// Unity `Quaternion.Euler(x, y, z)` is R = Ry * Rx * Rz (degrees). Levels are 2D so only z is
/// ever non-zero, but the goal box is an AABB of a full 3D box, so the general form is used.
function eulerMatrix(degrees) {
  const [x, y, z] = degrees.map((degree) => (degree * Math.PI) / 180);
  const [cx, sx, cy, sy, cz, sz] = [Math.cos(x), Math.sin(x), Math.cos(y), Math.sin(y), Math.cos(z), Math.sin(z)];
  return [
    [cy * cz + sy * sx * sz, -cy * sz + sy * sx * cz, sy * cx],
    [cx * sz, cx * cz, -sx],
    [-sy * cz + cy * sx * sz, sy * sz + cy * sx * cz, cy * cx],
  ];
}

/// The AABB of a prefab's BoxCollider placed by an instance transform: the eight box corners,
/// rotated and scaled into the level frame.
function boxZone(position, eulerDegrees, scale, center, size) {
  const rotation = eulerMatrix(eulerDegrees);
  const bounds = { min: [Infinity, Infinity, Infinity], max: [-Infinity, -Infinity, -Infinity] };
  for (const signX of [-1, 1]) {
    for (const signY of [-1, 1]) {
      for (const signZ of [-1, 1]) {
        const local = [
          (center[0] + (signX * size[0]) / 2) * scale[0],
          (center[1] + (signY * size[1]) / 2) * scale[1],
          (center[2] + (signZ * size[2]) / 2) * scale[2],
        ];
        for (let axis = 0; axis < 3; axis += 1) {
          const world = position[axis] + (rotation[axis][0] * local[0] + rotation[axis][1] * local[1] + rotation[axis][2] * local[2]);
          bounds.min[axis] = Math.min(bounds.min[axis], world);
          bounds.max[axis] = Math.max(bounds.max[axis], world);
        }
      }
    }
  }
  return bounds;
}

const guidToPath = buildGuidIndex(BPLE, ASSETS);
const loadersByScene = loadLoaders(BPLE, ASSETS, check);
// Read for its invariants only: the eight Episode*Levels manifests must still list all 277 levels.
loadEpisodes(BPLE, ASSETS, check);
const partMap = loadPartMap();
check(partMap.duplicates.length === 0, `part-map.json maps ${partMap.duplicates.join(", ")} to more than one partTypeId`);
const DEPTH = readCollisionMeshDepth(BPLE, check);

const goalBoxCache = new Map();
const goalBoxOf = (prefabPath) => {
  if (!goalBoxCache.has(prefabPath)) {
    const file = join(BPLE, prefabPath);
    check(existsSync(file), `${prefabPath}: goal prefab does not exist`);
    goalBoxCache.set(prefabPath, existsSync(file) ? readBoxCollider(file, check) : null);
  }
  return goalBoxCache.get(prefabPath);
};

const dataFiles = discoverDataFiles(ASSETS);
check(dataFiles.length === 277, `expected 277 level files, found ${dataFiles.length}`);

const bundleCounts = {};
const rows = [];
const skippedTerrains = [];
const totals = {
  terrain: 0,
  loops: 0,
  points: 0,
  colliderTerrains: 0,
  spawns: 0,
  goals: 0,
  levelsWithoutGoal: 0,
  bytes: 0,
};

for (const { bundle, file } of dataFiles) {
  const sceneName = basename(file).replace(/_data\.bytes$/, "");
  bundleCounts[bundle] = (bundleCounts[bundle] ?? 0) + 1;
  const data = readLevel(readFileSync(file), check);
  check(data.trailingBytes === 0, `${basename(file)}: ${data.trailingBytes} trailing bytes`);

  const loader = loadersByScene.get(sceneName.toLowerCase());
  check(Boolean(loader), `${basename(file)}: no LevelLoader prefab with m_sceneName ${sceneName}`);
  const palette = (loader?.paletteGuids ?? []).map((guid) => {
    const path = guidToPath.get(guid);
    check(Boolean(path), `${basename(file)}: palette guid ${guid} resolves to no asset`);
    return path ?? `<unresolved:${guid}>`;
  });
  for (const index of data.prefabIndexes.keys()) {
    check(index >= 0 && index < palette.length, `${basename(file)}: PrefabIndex ${index} outside palette of ${palette.length}`);
  }

  // ---------------------------------------------------------------- terrain
  const terrainEntries = [];
  let skippedHere = 0;
  for (const terrain of data.terrain) {
    if (!terrain.hasCollider) continue;
    totals.colliderTerrains += 1;
    const instance = terrain.instance;
    check(
      instance.name.includes("e2dTerrain"),
      `${basename(file)}: a collider-carrying terrain data block sits on instance "${instance.name}"`,
    );
    const euler = instance.euler.map((value) => (Math.abs(value) <= ROTATION_EPSILON ? 0 : value));
    const scale = instance.localScale;
    check(
      euler.every((value) => value === 0) && scale.every((value) => Math.abs(value - 1) <= SCALE_EPSILON),
      `${basename(file)}: terrain "${instance.name}" is not a plain translation (euler ${JSON.stringify(instance.euler)}, scale ${JSON.stringify(scale)})`,
    );

    const outline = outlineLoops(terrain.fill);
    if (outline.loops === null) {
      skippedHere += 1;
      skippedTerrains.push({
        level: sceneName,
        bundle,
        instance: instance.name,
        label: outline.label,
        reason: outline.diagnostics.reason ?? outline.diagnostics.label,
        vertices: outline.diagnostics.vertices,
        boundaryEdges: outline.diagnostics.boundaryEdges,
        boundaryVertices: outline.diagnostics.boundaryVertices,
        links: outline.diagnostics.links,
      });
      continue;
    }

    const loops = outline.loops.map((loop) =>
      loop.map((vertex) => {
        const [x, y] = terrain.fill.vertices[vertex];
        check(Number.isFinite(x) && Number.isFinite(y), `${basename(file)}: terrain "${instance.name}" has a non-finite outline point`);
        return [x, y];
      }),
    );
    check(
      instance.position.every(Number.isFinite),
      `${basename(file)}: terrain "${instance.name}" has a non-finite position ${JSON.stringify(instance.position)}`,
    );
    terrainEntries.push({ position: instance.position.slice(), depth: DEPTH, loops });
  }
  check(terrainEntries.length > 0, `${basename(file)}: no collider-carrying terrain with a closed outline`);

  // ---------------------------------------------------------------- bounds
  const bounds = { min: [Infinity, Infinity, Infinity], max: [-Infinity, -Infinity, -Infinity] };
  let points = 0;
  let loops = 0;
  for (const terrain of terrainEntries) {
    loops += terrain.loops.length;
    for (const loop of terrain.loops) {
      points += loop.length;
      for (const [x, y] of loop) {
        const world = [x + terrain.position[0], y + terrain.position[1], terrain.position[2]];
        bounds.min[0] = Math.min(bounds.min[0], world[0]);
        bounds.max[0] = Math.max(bounds.max[0], world[0]);
        bounds.min[1] = Math.min(bounds.min[1], world[1]);
        bounds.max[1] = Math.max(bounds.max[1], world[1]);
        bounds.min[2] = Math.min(bounds.min[2], world[2] - DEPTH / 2);
        bounds.max[2] = Math.max(bounds.max[2], world[2] + DEPTH / 2);
      }
    }
  }
  // Every content number is a float32 (the engine stores these fields as float), so the derived
  // corners are rounded here and the writer refuses anything that is not exactly a float32.
  const levelBounds = {
    min: bounds.min.map((value) => Math.fround(value - BOUNDS_MARGIN)),
    max: bounds.max.map((value) => Math.fround(value + BOUNDS_MARGIN)),
  };

  // ---------------------------------------------------------------- goal zone
  check(data.goalInstances.length <= 1, `${basename(file)}: ${data.goalInstances.length} Goal* instances, expected at most one`);
  let goalZone;
  let goalSource;
  if (data.goalInstances.length === 1) {
    const goal = data.goalInstances[0];
    const prefabPath = palette[goal.prefabIndex];
    check(Boolean(prefabPath), `${basename(file)}: goal "${goal.name}" has PrefabIndex ${goal.prefabIndex} outside the palette`);
    check(
      typeof prefabPath === "string" && /(^|\/)GoalArea_[^/]+\.prefab$/.test(prefabPath),
      `${basename(file)}: goal "${goal.name}" resolves to "${prefabPath}", not a GoalArea prefab`,
    );
    const box = typeof prefabPath === "string" ? goalBoxOf(prefabPath) : null;
    check(Boolean(box), `${basename(file)}: goal "${goal.name}" prefab "${prefabPath}" has no readable BoxCollider`);
    if (box) {
      const zone = boxZone(goal.position, goal.euler, goal.localScale, box.center, box.size);
      goalZone = {
        min: zone.min.map((value) => Math.fround(value)),
        max: zone.max.map((value) => Math.fround(value)),
      };
      goalSource = `Goal* "${goal.name}" -> ${prefabPath} BoxCollider ${JSON.stringify(box.size)} at ${JSON.stringify(box.center)}`;
      totals.goals += 1;
    }
  }
  if (!goalZone) {
    // No finish trigger: park the zone just outside the level's own bounds so no entity can ever
    // be inside it and the room never reports Won.
    goalZone = {
      min: levelBounds.max.map((value) => Math.fround(value + 1)),
      max: levelBounds.max.map((value) => Math.fround(value + 2)),
    };
    goalSource = data.goalInstances.length === 1
      ? "Goal* instance present but its BoxCollider could not be read (failure) -> goalZone parked outside bounds"
      : "no Goal* instance (sandbox/MM level) -> goalZone parked outside bounds, the room never reports Won";
    totals.levelsWithoutGoal += 1;
  }

  // ---------------------------------------------------------------- placed parts
  const spawns = [];
  for (const instance of data.instanceList) {
    const prefabPath = palette[instance.prefabIndex];
    const prefabName = basename(prefabPath ?? `<missing:${instance.prefabIndex}>`, ".prefab");
    const partTypeId = partMap.prefabToPartTypeId.get(prefabName);
    if (partTypeId === undefined) continue;
    check(
      Math.abs(instance.euler[0]) <= ROTATION_EPSILON && Math.abs(instance.euler[1]) <= ROTATION_EPSILON,
      `${basename(file)}: placed part "${instance.name}" (${prefabName}) has an out-of-plane rotation ${JSON.stringify(instance.euler)}`,
    );
    check(
      instance.localScale.every((value) => Math.abs(value - 1) <= SCALE_EPSILON),
      `${basename(file)}: placed part "${instance.name}" (${prefabName}) is scaled ${JSON.stringify(instance.localScale)}`,
    );
    // Unity's Quaternion.Euler takes degrees; PigForge's spawn angle is radians about z.
    check(
      instance.position.every(Number.isFinite),
      `${basename(file)}: placed part "${instance.name}" (${prefabName}) has a non-finite position ${JSON.stringify(instance.position)}`,
    );
    spawns.push({
      partTypeId,
      position: instance.position.slice(),
      angle: Math.fround((instance.euler[2] * Math.PI) / 180),
      role: "part",
    });
  }

  // ---------------------------------------------------------------- write
  const contentVersion = contentVersionOf(sceneName);
  const area = areaOf(bundle);
  const relativePath = `content/levels/original/${area}/${contentVersion}.json`;
  const document = {
    format: "pigforge.level-content",
    schemaVersion: 2,
    contentVersion,
    goalZone,
    bounds: levelBounds,
    spawns,
    terrain: terrainEntries,
  };
  const json = formatLevelDocument(document);
  const bytes = Buffer.byteLength(json, "utf8");
  const hash = sha256(json);
  const path = join(OUT, area, `${contentVersion}.json`);
  const changed = !existsSync(path) || readFileSync(path, "utf8") !== json;
  if (changed && !DRY_RUN) {
    mkdirSync(dirname(path), { recursive: true });
    writeFileSync(path, json);
  }

  totals.terrain += terrainEntries.length;
  totals.loops += loops;
  totals.points += points;
  totals.spawns += spawns.length;
  totals.bytes += bytes;

  const state = DRY_RUN ? (changed ? "new" : "same") : changed ? "wrote" : "same";
  console.log(
    `${state.padEnd(5)} ${relativePath}  terrain=${terrainEntries.length} loops=${loops} points=${points}` +
      ` spawns=${spawns.length} goal=${data.goalInstances.length === 1 ? basename(palette[data.goalInstances[0].prefabIndex], ".prefab") : "none"}` +
      ` skipped=${skippedHere} bytes=${bytes}`,
  );

  rows.push({
    bundle,
    area,
    sceneName,
    contentVersion,
    path: relativePath,
    bytes,
    hash,
    terrain: terrainEntries.length,
    loops,
    points,
    colliderTerrains: terrainEntries.length + skippedHere,
    skipped: skippedHere,
    spawns: spawns.length,
    goalSource,
    changed,
  });
}

for (const [bundle, expected] of Object.entries(BUNDLE_EXPECT)) {
  check(bundleCounts[bundle] === expected, `${bundle}: ${bundleCounts[bundle] ?? 0} files, expected ${expected}`);
}
check(skippedTerrains.length === 0, `${skippedTerrains.length} terrain(s) have no closed outline and were skipped`);

rows.sort((left, right) => left.bundle.localeCompare(right.bundle) || left.sceneName.localeCompare(right.sceneName));
const changed = rows.filter((row) => row.changed).length;
const unchanged = rows.length - changed;

const report = {
  format: "pigforge.bple-level-build",
  schemaVersion: 1,
  source: { bpleRoot: BPLE, out: OUT, generatedBy: "tools/bple-levels/build-levels.mjs" },
  dryRun: DRY_RUN,
  depth: DEPTH,
  boundsMargin: BOUNDS_MARGIN,
  counts: {
    levels: rows.length,
    bundles: bundleCounts,
    filesWritten: DRY_RUN ? 0 : changed,
    changed,
    unchanged,
    bytes: totals.bytes,
    terrain: totals.terrain,
    colliderTerrains: totals.colliderTerrains,
    loops: totals.loops,
    points: totals.points,
    spawns: totals.spawns,
    goals: totals.goals,
    levelsWithoutGoal: totals.levelsWithoutGoal,
    terrainSkipped: skippedTerrains.length,
  },
  skipped: skippedTerrains,
  levels: rows,
  failures,
};
mkdirSync(dirname(OUT_JSON), { recursive: true });
writeFileSync(OUT_JSON, formatJson(report));

const md = [];
md.push("# 原版关卡构建报告", "");
md.push("来源：`node tools/bple-levels/build-levels.mjs`（默认读 pristine `BPLE 2022.1.9`，写 `content/levels/original/**`）。");
md.push("每个数字都来自原版：调色板解析 prefab，终点区读该 prefab 自己的触发 `BoxCollider`，挤出深度读 `e2dConstants.COLLISION_MESH_Z_DEPTH`，");
md.push("地形轮廓走 fill 网格的真实边界环（`lib/outline.mjs`；顶点表在 5/1648 个地形上不是轮廓）。", "");
md.push(`- 关卡 **${rows.length}**，地形条目 **${totals.terrain}**（带碰撞体的地形对象 ${totals.colliderTerrains}），环 ${totals.loops}，轮廓点 ${totals.points}`);
md.push(`- 零件实例 **${totals.spawns}**，有终点的关卡 **${totals.goals}**，无终点（沙盒/MM）**${totals.levelsWithoutGoal}**`);
md.push(`- 深度 **${DEPTH}**（\`e2dConstants.cs\`），bounds 外扩 **${BOUNDS_MARGIN}** m`);
md.push(`- 跳过地形 **${skippedTerrains.length}**，失败 **${failures.length}**`);
md.push("", `**${changed} changed / ${unchanged} unchanged**${DRY_RUN ? " (dry-run，未写盘)" : ""}，合计 ${totals.bytes} 字节。`, "");
md.push("## 跳过与失败", "");
if (skippedTerrains.length === 0 && failures.length === 0) {
  md.push("无。每个带碰撞体的地形都有闭环轮廓，每条调色板下标都能解析。", "");
} else {
  for (const skipped of skippedTerrains) md.push(`- SKIP \`${skipped.level}\` / \`${skipped.instance}\`：${skipped.reason}（${skipped.links}）`);
  for (const failure of failures) md.push(`- FAIL ${failure}`);
  md.push("");
}
md.push("## 每关", "");
md.push("| area | scene | contentVersion | 地形 | 环 | 点 | 零件 | 字节 | 终点来源 | hash |", "|---|---|---|---|---|---|---|---|---|---|");
for (const row of rows) {
  md.push(
    `| ${row.area} | \`${row.sceneName}\` | \`${row.contentVersion}\` | ${row.terrain} | ${row.loops} | ${row.points} | ${row.spawns} | ${row.bytes} | ${row.goalSource.replaceAll("|", "\\|")} | \`${row.hash.slice(0, 12)}\` |`,
  );
}
md.push("", "## 产出文件", "");
md.push("`content/levels/original/<area>/<scene>.json`（LF、2 空格缩进、结尾换行）。`--dry-run` 只打印计划不写盘；重跑时内容相同的文件不会被动。", "");
writeFileSync(OUT_MD, `${md.join("\n")}\n`);

console.log("");
console.log(
  DRY_RUN
    ? `dry run: would write ${changed} file(s) (${changed} new, ${unchanged} unchanged), ${totals.bytes} bytes total`
    : `build: wrote ${changed} file(s) (${changed} changed, ${unchanged} unchanged), ${totals.bytes} bytes total`,
);
console.log(`levels: ${rows.length}  terrain: ${totals.terrain}  loops: ${totals.loops}  points: ${totals.points}  spawns: ${totals.spawns}`);
console.log(`goal: ${totals.goals}  no-goal: ${totals.levelsWithoutGoal}  skipped terrains: ${skippedTerrains.length}  depth: ${DEPTH}  bounds margin: ${BOUNDS_MARGIN}`);
console.log(`report: ${OUT_JSON}\n        ${OUT_MD}`);
if (failures.length > 0) {
  console.error(`\n${failures.length} invariant failure(s):`);
  for (const failure of failures.slice(0, 40)) console.error(`  - ${failure}`);
  process.exit(1);
}
console.log(`${DRY_RUN ? "plan" : "content"} root: ${OUT}`);
