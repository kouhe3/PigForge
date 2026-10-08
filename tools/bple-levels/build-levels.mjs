// Level builder: turns the original's 277 binary level files into PigForge level content v5
// (`content/levels/original/<area>/<scene>.json`) -- the level's own `PrefabOverrides` camera limits
// (the rectangle the original drops the pig out of, `Pig.cs:396-403`), every `e2dTerrain` object (its
// collision outline, whether it carries a MeshCollider, and the fill texture/color/tile that draw its
// ground), the finish trigger, the map bounds and the (rare) placed parts.
//
// It is the write side of the same decoder the extractor reports with (`lib/`), so every emitted
// number comes out of the pack: the palette resolves a `PrefabIndex` to its prefab, the goal zone
// is that prefab's own trigger BoxCollider, the extrusion depth is `e2dConstants.cs`, and the
// terrain loops are the fill mesh's real boundary (`lib/outline.mjs` walks the boundary fan,
// because the vertex table is not the outline for 5 of the 1648 collider terrains -- see
// docs/specs/original-level-pack.md §3). Nothing here is hand-written that the pack defines.
//
// Output shape (`lib/write.mjs`): the readable 2-space layout for `format`/`schemaVersion`/
// `contentVersion`/`goalZone`/`bounds`/`spawns`, one line for `cameraLimits`, one `terrain` entry per
// object with its `position`, `depth` and `loops`, and one line per outline loop -- the pack is three quarters
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
// The fill's three inputs come from three places and all three are read, never written by hand: the
// texture name and the RGBA color out of the level file's `m_references` / `m_references` index
// (`LevelLoader.cs:214-230`), the tile offset out of the level file itself, and the tile size out of
// the terrain prefab (`lib/fill.mjs`). The referenced PNGs are copied to `--textures` (default
// `clients/web/public/assets/original/levels`, gitignored like the part atlases) so the client can
// draw them; the level document only ever names a file.
//
// Usage:
//   node tools/bple-levels/build-levels.mjs [--bple <path>] [--out <dir>] [--dry-run]
//                                          [--textures <dir>] [--json <path>] [--md <path>]

import { existsSync, mkdirSync, readFileSync, readdirSync, rmSync, writeFileSync } from "node:fs";
import { basename, dirname, join, resolve } from "node:path";
import { arg, flag } from "../lib/args.mjs";
import { buildGuidIndex, loadEpisodes, loadLoaders } from "./lib/unity-yaml.mjs";
import { readLevel } from "./lib/reader.mjs";
import { outlineLoops } from "./lib/outline.mjs";
import { BUNDLE_EXPECT, REPO, discoverDataFiles, loadPartMap, readCollisionMeshDepth } from "./lib/pack.mjs";
import { readBoxCollider } from "./lib/goal.mjs";
import { assertTerrainFillTileSizes, readE2dTerrainGuid, readImportState, readTerrainFillTile } from "./lib/fill.mjs";
import { curveBlockOf } from "./lib/curve.mjs";
import { readCameraLimits } from "./lib/overrides.mjs";
import { formatJson, formatLevelDocument, sha256 } from "./lib/write.mjs";

const BPLE = resolve(arg("bple", process.env.BPLE_ROOT ?? join(REPO, "..", "BPLE 2022.1.9")));
const ASSETS = join(BPLE, "Assets");
const OUT = resolve(arg("out", join(REPO, "content", "levels", "original")));
// Original art never enters the repository (see .gitignore): the client reads the fill textures from
// its own asset tree, exactly as it reads the part atlases.
const OUT_TEXTURES = resolve(arg("textures", join(REPO, "clients", "web", "public", "assets", "original", "levels")));
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
// `bounds` is PigForge's own coarse box around the terrain (terrain AABB + this margin): the client
// frames the view on it. The pig's own bound is the level's camera limits, read from the level file's
// `PrefabOverrides` (`lib/overrides.mjs`) -- the original drops the pig out of that rectangle
// (`Pig.cs:396-403`). 25 = 2.5 x the collider depth (the extrusion the terrain sticks into z).
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
const E2D_TERRAIN_GUID = readE2dTerrainGuid(BPLE, check);

// The fill's third input (`lib/fill.mjs`): read lazily per terrain prefab and asserted as a histogram
// once every level has been walked. `fillTextures` is `file name -> asset path` for the textures the
// content names; the file name is what the document carries, so two assets sharing one is refused.
const terrainFillTiles = new Map();
const levelTextures = new Map();
const fillTextureNames = new Set();
const curveTextureNames = new Set();
const fillTileOf = (prefabPath) => {
  if (!terrainFillTiles.has(prefabPath)) {
    terrainFillTiles.set(prefabPath, readTerrainFillTile(BPLE, prefabPath, E2D_TERRAIN_GUID, check));
  }
  return terrainFillTiles.get(prefabPath);
};

const goalBoxCache = new Map();
/// One texture the content names, by its file name (the document only ever carries that). The
/// client's level-texture directory is flat, so two assets sharing a file name are a hard failure
/// rather than a silent overwrite -- `kind` says which half of the terrain asked for it.
const registerLevelTexture = (assetPath, kind) => {
  const name = basename(assetPath);
  const taken = levelTextures.get(name);
  check(
    taken === undefined || taken === assetPath,
    `${name}: two level texture assets share the file name (${taken} and ${assetPath}, asked for by the ${kind} of a terrain)`,
  );
  levelTextures.set(name, assetPath);
  (kind === "fill" ? fillTextureNames : curveTextureNames).add(name);
  return name;
};

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
const skippedFills = [];
const skippedCurves = [];
const totals = {
  terrain: 0,
  loops: 0,
  points: 0,
  curveNodes: 0,
  curveRuns: 0,
  colliderTerrains: 0,
  fillTextures: 0,
  fillTextureBytes: 0,
  spawns: 0,
  goals: 0,
  levelsWithoutGoal: 0,
  cameraLimits: 0,
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
  // The terrain's texture table. `m_references` holds the fill texture and the curve textures; a
  // null guid is a legal entry (Unity serialises an empty reference that way) but no fill ever
  // resolves to one -- measured 0/2146.
  const references = (loader?.referenceGuids ?? []).map((guid) => {
    if (!guid) return null;
    const path = guidToPath.get(guid);
    check(Boolean(path), `${basename(file)}: reference guid ${guid} resolves to no asset`);
    return path ?? null;
  });

  // ---------------------------------------------------------------- terrain
  const terrainEntries = [];
  let skippedHere = 0;
  for (const terrain of data.terrain) {
    if (terrain.hasCollider) totals.colliderTerrains += 1;
    const instance = terrain.instance;
    check(
      instance.name.includes("e2dTerrain"),
      `${basename(file)}: a terrain data block sits on instance "${instance.name}"`,
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

    // The ground's look: `fill.shader` = `tex2D(_MainTex, uv) * _Color` with
    // `uv = (world - tileOffset) / tileSize`. Two of the three inputs are in the level file (the
    // texture through its `m_references` index, the color and the tile offset directly), the tile size
    // is on the prefab (`lib/fill.mjs`). A terrain whose fill cannot be assembled is skipped like one
    // whose outline does not walk: the document never carries a half-filled entry.
    const prefabPath = palette[instance.prefabIndex];
    const fillTexturePath = typeof prefabPath === "string" ? references[terrain.fillTextureIndex] : null;
    const fillTextureName = typeof fillTexturePath === "string" && fillTexturePath.endsWith(".png")
      ? basename(fillTexturePath)
      : null;
    const tile = typeof prefabPath === "string" ? fillTileOf(prefabPath) : null;
    if (tile === null || fillTextureName === null) {
      skippedHere += 1;
      skippedFills.push({
        level: sceneName,
        bundle,
        instance: instance.name,
        prefab: prefabPath ?? null,
        textureIndex: terrain.fillTextureIndex,
        texture: fillTexturePath ?? null,
        tile,
        reason: tile === null
          ? "the terrain prefab declares no positive FillTextureTileWidth/Height"
          : `fill texture index ${terrain.fillTextureIndex} resolves to no PNG`,
      });
      continue;
    }
    registerLevelTexture(fillTexturePath, "fill");
    // The edge trim (`curve.shader`): the level file's own `_curve` mesh -- a two-row strip -- plus
    // the two `e2dCurveTexture` layers with their wrap modes, the shader's u scale and the runs of
    // nodes the control texture's green channel switches to the second layer (`lib/curve.mjs`).
    const curveLabel = `${basename(file)}: terrain "${instance.name}"`;
    const curve = curveBlockOf({
      terrain,
      referencePaths: references,
      bpleRoot: BPLE,
      registerTexture: (assetPath) => registerLevelTexture(assetPath, "curve"),
      check,
      label: curveLabel,
    });
    if (curve === null) {
      skippedHere += 1;
      skippedCurves.push({ level: sceneName, bundle, instance: instance.name, reason: "the curve mesh or its layers could not be read" });
      continue;
    }
    terrainEntries.push({
      position: instance.position.slice(),
      depth: DEPTH,
      collider: terrain.hasCollider,
      fill: {
        texture: fillTextureName,
        color: [
          (terrain.fillColor >>> 24) & 0xff,
          (terrain.fillColor >>> 16) & 0xff,
          (terrain.fillColor >>> 8) & 0xff,
          terrain.fillColor & 0xff,
        ],
        tileOffset: terrain.fillOffset.map((value) => Math.fround(value)),
        tileSize: [tile.tileWidth, tile.tileHeight],
      },
      curve,
      loops,
    });
    totals.curveNodes += curve.nodes.length;
    totals.curveRuns += curve.splat1.length;
  }
  check(terrainEntries.length > 0, `${basename(file)}: no terrain with a closed outline`);

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

  // ---------------------------------------------------------------- camera limits
  // The original's own pig bound, straight out of the level file's `PrefabOverrides`: every one of
  // the 277 levels overrides `LevelManager.m_cameraLimits`, so a level without one is drift.
  const cameraLimits = readCameraLimits(data.overrides, check);
  if (cameraLimits) totals.cameraLimits += 1;

  // ---------------------------------------------------------------- write
  const contentVersion = contentVersionOf(sceneName);
  const area = areaOf(bundle);
  const relativePath = `content/levels/original/${area}/${contentVersion}.json`;
  const document = {
    format: "pigforge.level-content",
    schemaVersion: 5,
    contentVersion,
    goalZone,
    bounds: levelBounds,
    cameraLimits,
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
check(skippedCurves.length === 0, `${skippedCurves.length} terrain(s) have no usable curve mesh and were skipped`);

// ---------------------------------------------------------------- level textures

// The tile size histogram is asserted over every terrain prefab the levels place (21 of them, all
// 5 x 5 today), and the level textures -- the ground's fill and the edge trim's two layers -- are
// copied next to the part atlases: the original art never enters the repository, so the document only
// names a file and the client reads it from its own tree.
assertTerrainFillTileSizes(new Map([...terrainFillTiles].filter(([, tile]) => tile !== null)), check);
check(skippedFills.length === 0, `${skippedFills.length} terrain(s) have no usable fill and were skipped`);

const textureRows = [];
let textureBytes = 0;
for (const [name, source] of [...levelTextures.entries()].sort((left, right) => left[0].localeCompare(right[0]))) {
  readImportState(BPLE, source, check);
  const bytes = readFileSync(join(BPLE, source));
  const target = join(OUT_TEXTURES, name);
  const changed = !existsSync(target) || !bytes.equals(readFileSync(target));
  if (changed && !DRY_RUN) {
    mkdirSync(OUT_TEXTURES, { recursive: true });
    writeFileSync(target, bytes);
  }
  textureBytes += bytes.length;
  textureRows.push({
    name,
    source,
    bytes: bytes.length,
    changed,
    fill: fillTextureNames.has(name),
    curve: curveTextureNames.has(name),
  });
}
totals.levelTextures = textureRows.length;
totals.levelTextureBytes = textureBytes;

// The directory belongs to this tool alone (it sits inside the gitignored original-art tree), so a
// texture that is no longer referenced is removed instead of left behind.
const staleTextures = existsSync(OUT_TEXTURES)
  ? readdirSync(OUT_TEXTURES, { withFileTypes: true })
      .filter((entry) => entry.isFile() && !levelTextures.has(entry.name))
      .map((entry) => entry.name)
      .sort()
  : [];
for (const name of staleTextures) {
  if (!DRY_RUN) rmSync(join(OUT_TEXTURES, name), { force: true });
}

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
    visualTerrains: totals.terrain - totals.colliderTerrains,
    loops: totals.loops,
    points: totals.points,
    curveNodes: totals.curveNodes,
    curveRuns: totals.curveRuns,
    spawns: totals.spawns,
    goals: totals.goals,
    levelsWithoutGoal: totals.levelsWithoutGoal,
    cameraLimits: totals.cameraLimits,
    terrainSkipped: skippedTerrains.length,
    terrainSkippedForFill: skippedFills.length,
    terrainSkippedForCurve: skippedCurves.length,
    levelTextures: textureRows.length,
    levelTextureBytes: textureBytes,
    levelTexturesChanged: textureRows.filter((row) => row.changed).length,
    levelTexturesRemoved: staleTextures.length,
    fillTextures: fillTextureNames.size,
    curveTextures: curveTextureNames.size,
    textureRoot: OUT_TEXTURES,
  },
  skipped: skippedTerrains,
  skippedFills,
  skippedCurves,
  textures: textureRows,
  levels: rows,
  failures,
};
mkdirSync(dirname(OUT_JSON), { recursive: true });
writeFileSync(OUT_JSON, formatJson(report));

const md = [];
md.push("# 原版关卡构建报告", "");
md.push("来源：`node tools/bple-levels/build-levels.mjs`（默认读 pristine `BPLE 2022.1.9`，写 `content/levels/original/**` 与贴图目录）。");
md.push("每个数字都来自原版：调色板解析 prefab，终点区读该 prefab 自己的触发 `BoxCollider`，挤出深度读 `e2dConstants.COLLISION_MESH_Z_DEPTH`，");
md.push("地形轮廓走 fill 网格的真实边界环（`lib/outline.mjs`；顶点表在 5/1648 个地形上不是轮廓），");
md.push("地面的样子读 `fill.shader` 的三个输入（贴图/颜色来自关卡文件，tile 尺寸来自地形 prefab，见 `docs/specs/level-terrain-visuals.md`）。", "");
md.push(`- 关卡 **${rows.length}**，地形条目 **${totals.terrain}**（每个 \`e2dTerrain\` 对象一条：带碰撞体 ${totals.colliderTerrains} / 纯视觉 ${totals.terrain - totals.colliderTerrains}），环 ${totals.loops}，轮廓点 ${totals.points}`);
md.push(`- 边缘条带（\`curve.shader\`）节点 **${totals.curveNodes}**，第二层运行段 **${totals.curveRuns}**（层选择来自关卡文件内嵌控制贴图的 G 通道）`);
md.push(`- 关卡贴图 **${textureRows.length}** 张 / ${textureBytes} 字节（fill ${fillTextureNames.size} + curve ${curveTextureNames.size}，Bilinear 已断言），复制到 \`${OUT_TEXTURES}\``);
md.push(`- 零件实例 **${totals.spawns}**，有终点的关卡 **${totals.goals}**，无终点（沙盒/MM）**${totals.levelsWithoutGoal}**`);
md.push(`- 相机界（\`PrefabOverrides\` 的 \`LevelManager.m_cameraLimits\`，原版出界判定的矩形）**${totals.cameraLimits}/${rows.length}** 关`);
md.push(`- 深度 **${DEPTH}**（\`e2dConstants.cs\`），bounds 外扩 **${BOUNDS_MARGIN}** m`);
md.push(`- 跳过地形 **${skippedTerrains.length}**（轮廓不可走）+ **${skippedFills.length}**（fill 不可组装）+ **${skippedCurves.length}**（条带不可组装），失败 **${failures.length}**`);
md.push("", `**${changed} changed / ${unchanged} unchanged**${DRY_RUN ? " (dry-run，未写盘)" : ""}，合计 ${totals.bytes} 字节。`, "");
md.push("## 跳过与失败", "");
if (skippedTerrains.length === 0 && skippedFills.length === 0 && skippedCurves.length === 0 && failures.length === 0) {
  md.push("无。每个地形都有闭环轮廓、可组装的 fill 与条带，每条调色板下标都能解析。", "");
} else {
  for (const skipped of skippedTerrains) md.push(`- SKIP \`${skipped.level}\` / \`${skipped.instance}\`：${skipped.reason}（${skipped.links}）`);
  for (const skipped of skippedFills) md.push(`- SKIP-FILL \`${skipped.level}\` / \`${skipped.instance}\`：${skipped.reason}`);
  for (const skipped of skippedCurves) md.push(`- SKIP-CURVE \`${skipped.level}\` / \`${skipped.instance}\`：${skipped.reason}`);
  for (const failure of failures) md.push(`- FAIL ${failure}`);
  md.push("");
}
md.push("## 关卡贴图", "");
md.push("| 文件 | 源资产 | 字节 | fill | curve | 本次 |", "|---|---|---|---|---|---|");
for (const texture of textureRows) {
  md.push(
    `| \`${texture.name}\` | \`${texture.source}\` | ${texture.bytes} | ${texture.fill ? "✓" : ""} | ${texture.curve ? "✓" : ""} | ${texture.changed ? "写入" : "相同"} |`,
  );
}
if (staleTextures.length > 0) md.push("", `已删除不再被引用的：${staleTextures.map((name) => `\`${name}\``).join("、")}`);
md.push("");
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
console.log(`curve: nodes: ${totals.curveNodes}  second-layer runs: ${totals.curveRuns}`);
console.log(`goal: ${totals.goals}  no-goal: ${totals.levelsWithoutGoal}  depth: ${DEPTH}  bounds margin: ${BOUNDS_MARGIN}`);
console.log(`camera limits: ${totals.cameraLimits}/${rows.length} levels`);
console.log(
  `terrain collider/visual: ${totals.colliderTerrains}/${totals.terrain - totals.colliderTerrains}` +
    `  skipped: ${skippedTerrains.length} outline + ${skippedFills.length} fill + ${skippedCurves.length} curve`,
);
console.log(
  `level textures: ${textureRows.length} (fill ${fillTextureNames.size} + curve ${curveTextureNames.size}, ${textureBytes} bytes, ` +
    `${textureRows.filter((row) => row.changed).length} changed, ` +
    `${staleTextures.length} removed) -> ${OUT_TEXTURES}`,
);
console.log(`report: ${OUT_JSON}\n        ${OUT_MD}`);
if (failures.length > 0) {
  console.error(`\n${failures.length} invariant failure(s):`);
  for (const failure of failures.slice(0, 40)) console.error(`  - ${failure}`);
  process.exit(1);
}
console.log(`${DRY_RUN ? "plan" : "content"} root: ${OUT}`);
