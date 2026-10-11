// Level builder: turns the original's 277 binary level files into PigForge level content v10
// (`content/levels/original/<area>/<scene>.json`) -- the level's own `PrefabOverrides` camera limits
// (the rectangle the original drops the pig out of, `Pig.cs:396-403`), every `e2dTerrain` object (its
// collision outline, whether it carries a MeshCollider, and the fill texture/color/tile that draw its
// ground), the finish trigger, the map bounds, the (rare) placed parts, and the level's own build
// limits (v9): the construction grid its `m_constructionGridRows` describes, anchored on its
// `LevelStart` instance, and the part allowances its `m_partTypeCounts` leaves (`lib/overrides.mjs`
// reads both as the patches on the `LevelManager` prefab's own table that the original's
// `ObjectDeserializer` makes them; `build` is optional, because the sandbox levels the tool carves
// out carry no limit at all) -- and the level's own challenges (v10): one entry per `Challenge`
// instance the level places, in the original's own numbering, with the parameter each one's
// `PrefabOverrides` block mounts (`lib/challenges.mjs`; 263 of the 277 levels have two, one has
// three, and the 14 sandbox levels have none).
//
// It is the write side of the same decoder the extractor reports with (`lib/`), so every emitted
// number comes out of the pack: the palette resolves a `PrefabIndex` to its prefab, the goal zone
// is that prefab's own trigger BoxCollider, the extrusion depth is `e2dConstants.cs`, and the
// terrain loops are the fill mesh's real boundary (`lib/outline.mjs` walks the boundary fan,
// because the vertex table is not the outline for 5 of the 1648 collider terrains -- see
// docs/specs/original-level-pack.md §3). Nothing here is hand-written that the pack defines.
//
// Output shape (`lib/write.mjs`): the readable 2-space layout for `format`/`schemaVersion`/
// `contentVersion`/`goalZone`/`bounds`/`build`/`spawns`/`challenges` (one line per challenge),
// one line for `cameraLimits`, one `terrain` entry per
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
import { openProps, propId, PropException } from "../lib/props.mjs";
import { buildGuidIndex, loadEpisodes, loadLoaders } from "./lib/unity-yaml.mjs";
import { readLevel } from "./lib/reader.mjs";
import { outlineLoops } from "./lib/outline.mjs";
import { BUNDLE_EXPECT, REPO, discoverDataFiles, loadPartMap, readCollisionMeshDepth } from "./lib/pack.mjs";
import { readBoxCollider } from "./lib/goal.mjs";
import { assertTerrainFillTileSizes, readE2dTerrainGuid, readImportState, readTerrainFillTile } from "./lib/fill.mjs";
import { curveBlockOf } from "./lib/curve.mjs";
import { readBuildLimits, readCameraLimits, readLevelManagerDefaults } from "./lib/overrides.mjs";
import {
  CHALLENGE_LESS_LEVELS,
  CHALLENGE_KIND_BY_CLASS,
  challengeInstances,
  openChallengePrefabs,
  readLevelChallenges,
} from "./lib/challenges.mjs";
import { resolvePartTypes } from "../lib/part-types.mjs";
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

// Levels whose content carries no `build` block. The original's sandbox levels are the one place a
// level's table is not the whole allowance: `BaseGameMode.GetPartCount` adds the *player's* own
// `GameProgress` sandbox unlocks on top of `m_partTypeCounts` (`BaseGameMode.cs:568-578`), which
// PigForge does not model, so a `build` block would forbid exactly the parts the sandbox player has
// unlocked. `Level_Sandbox_02`/`Level_Sandbox_06` are the two the content spec carves out; the
// report names them, their real grid and their allowance, and `check`s that they are the only ones.
const WITHOUT_BUILD = new Set(["Level_Sandbox_02", "Level_Sandbox_06"]);

// PartTypes that have no PigForge base at all (see `tools/lib/part-types.mjs`), so a level that
// offers one cannot carry that allowance across. Listed explicitly rather than derived from the
// map, because a PartType *becoming* mappable (G91's `Pumpkin`/`GoldenPig`) must be noticed here,
// and the check below re-confirms each value against the pack's own prefabs on every run.
const WITHOUT_PIGFORGE_BASE = new Set([8, 37, 38, 42, 45]);

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

/// The construction grid a `m_constructionGridRows` list describes, straight out of the original's
/// own rectangle (`BaseGameMode.InitGameMode`, `BaseGameMode.cs:49-71`) for the vanilla settings
/// (`InfiniteGrid=false`, `GridSize=1` -- both the pack's own `INDeclarationSettingsExp.json`
/// values, which is the branch `LevelManager.CanPlacePartAtGridCell` tests for): `columns` is the
/// widest row's highest set bit + 1 (at least 1), `height` the last non-zero row's index + 1 (at
/// least 1 -- the original seeds both counters with 1), `xMin`/`xMax` the half-open span
/// `-(columns-1)/2 .. columns/2` in C# integer division, and `cells` how many of those (i, j)
/// actually have their bit set. Only the census needs the numbers; the content carries the list.
function gridExtent(rows) {
  let columns = 1;
  let height = 1;
  rows.forEach((row, index) => {
    if (row === 0) return;
    for (let bit = 30; bit >= 0; bit -= 1) {
      if ((row & (1 << bit)) !== 0) {
        columns = Math.max(columns, bit + 1);
        break;
      }
    }
    height = index + 1;
  });
  const xMin = -Math.trunc((columns - 1) / 2);
  const xMax = Math.trunc(columns / 2);
  let cells = 0;
  for (let y = 0; y < height; y += 1) {
    for (let x = xMin; x <= xMax; x += 1) {
      if ((rows[height - 1 - y] & (1 << (x - xMin))) !== 0) cells += 1;
    }
  }
  return { columns, height, cells };
}

const guidToPath = buildGuidIndex(BPLE, ASSETS);
const loadersByScene = loadLoaders(BPLE, ASSETS, check);
// Read for its invariants only: the eight Episode*Levels manifests must still list all 277 levels.
loadEpisodes(BPLE, ASSETS, check);
const partMap = loadPartMap();
check(partMap.duplicates.length === 0, `part-map.json maps ${partMap.duplicates.join(", ")} to more than one partTypeId`);
// The PartType enum -> PigForge partTypeId bridge a level's `m_partTypeCounts` is read with
// (`tools/lib/part-types.mjs`), built from the map's own prefabs so an enum never reaches the
// content as an id.
const partTypes = resolvePartTypes(BPLE, { parts: partMap.partsByType, variants: partMap.variantsByType }, check);
for (const type of WITHOUT_PIGFORGE_BASE) {
  check(!partTypes.byEnum.has(type), `PartType ${type} (${partTypes.names.get(type) ?? "?"}) is in the no-base list but the part map resolves it`);
}
// The levels' `m_constructionGridRows` / `m_partTypeCounts` are patches on the prefab's own table,
// so the table is read once, from the pack (`lib/overrides.mjs`).
const LEVEL_MANAGER_DEFAULTS = readLevelManagerDefaults(BPLE, check);
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

/**
 * The cloud sets' own generator (`docs/specs/level-props.md` §3.7). `CloudGenerator.SpawnCloud` draws a
 * random prefab and a random point of the set's own box with `Random.Range`, which no two runs share;
 * this tool lays the same cloud count out with a seed taken from the instance's own name, so a level's
 * sky is stable and each set instance gets its own field. Everything else is the original's: the box,
 * the count and the variants.
 */
/// The prop's own boxes as float32 content: the same six numbers a quad's frame carries.
function boxesOf(boxes) {
  return boxes.map((box) => ({
    dx: Math.fround(box.dx),
    dy: Math.fround(box.dy),
    dz: Math.fround(box.dz),
    sizeX: Math.fround(box.sizeX),
    sizeY: Math.fround(box.sizeY),
    sizeZ: Math.fround(box.sizeZ),
    rotation: Math.fround(box.rotation),
  }));
}

function cloudSeed(name, position) {
  let hash = 0x9e3779b9;
  for (const character of `${name}:${position[0].toFixed(3)}:${position[1].toFixed(3)}`) {
    hash = Math.imul(hash ^ character.charCodeAt(0), 0x85ebca6b);
  }
  return hash >>> 0;
}

/** mulberry32: a 32-bit PRNG in ten lines, deterministic and platform independent. */
function cloudRandom(seed) {
  let state = seed >>> 0;
  return () => {
    state = (state + 0x6d2b79f5) | 0;
    let value = Math.imul(state ^ (state >>> 15), 1 | state);
    value = (value + Math.imul(value ^ (value >>> 7), 61 | value)) ^ value;
    return ((value ^ (value >>> 14)) >>> 0) / 4294967296;
  };
}

// The level's non-part, non-terrain prefabs (`docs/specs/level-props.md`). One reader for the whole
// run: building it walks the original's asset tree once.
const props = openProps(BPLE);
// The five prefabs the levels' own challenges are instances of (`lib/challenges.mjs`): one reader
// for the whole run, so each prefab's class and its own parameter defaults are read once.
const challengePrefabs = openChallengePrefabs(BPLE, check);

const dataFiles = discoverDataFiles(ASSETS);
check(dataFiles.length === 277, `expected 277 level files, found ${dataFiles.length}`);

const bundleCounts = {};
const rows = [];
/** How many instances of each prop class the pack places -- the census the report carries. */
const propCensus = new Map();
const skippedTerrains = [];
const skippedFills = [];
const skippedCurves = [];
/** The build-limits census: what `build` blocks the content carries, and every part of it that did
 * not cross over. `shapes` is `columns x height` -> levels, `rowsShapes` the raw row lists. */
const buildCensus = {
  levels: 0,
  shapes: new Map(),
  rowsShapes: new Map(),
  families: 0,
  parts: 0,
  secondStar: new Map(),
  sandbox: [],
  withoutBuild: [],
  unmapped: new Map(),
};
/** The challenge census (`docs/specs/level-stars.md` §7): how many `Challenge` instances the pack
 * places, which class and parameter each carries, and the two shapes the report pins -- instances
 * per level, and the kind of each instance. Every map counts `{ instances, levels }` so a level
 * that starts or stops placing one shows up as a number rather than as silence. */
const challengeCensus = {
  instances: 0,
  levels: 0,
  kinds: new Map(),
  classes: new Map(),
  prefabs: new Map(),
  prefabClass: new Map(),
  labels: new Map(),
  perLevel: new Map(),
  targetTimes: new Map(),
  prefabDefaults: [],
  dontUseParts: new Map(),
  transports: new Map(),
  partTypeIds: new Map(),
  duplicateLabels: [],
};
/** The census entry for one instance of `key`, counted per level (a level placing two of the same
 * kind counts one instance twice, one level once). */
const countChallenge = (map, key, sceneName) => {
  const entry = map.get(key) ?? { instances: 0, levels: new Set() };
  entry.instances += 1;
  entry.levels.add(sceneName);
  map.set(key, entry);
};
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
  propInstances: 0,
  cloudInstances: 0,
  triggerProps: 0,
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

  // ---------------------------------------------------------------- construction limits
  // The level's own build limits: the grid its `m_constructionGridRows` describes (a patch on the
  // `LevelManager` prefab's own default -- see `lib/overrides.mjs`) and the part allowances its
  // `m_partTypeCounts` patches leave. Both hang off the level's own `LevelStart` instance, which is
  // what the original hands `LevelManager.CreateGrid` (`BaseGameMode.cs:70-71`,
  // `LevelManager.cs:1261-1278`): a cell (i, j) is the world point anchor + (i, j)
  // (`Contraption.SetPartPos`, `Contraption.cs:1579-1590`). PigForge's cells are integer world
  // units, so the rule layer floors this origin; the content carries it exactly.
  const limits = readBuildLimits(data.overrides, LEVEL_MANAGER_DEFAULTS, check);
  const anchors = data.instanceList.filter((instance) => palette[instance.prefabIndex]?.endsWith("LevelStart.prefab"));
  check(anchors.length === 1, `${basename(file)}: ${anchors.length} LevelStart instance(s), expected exactly one`);
  const anchor = anchors[0] ?? null;
  if (anchor !== null) {
    // The original only ever reads the position, so a rotated or scaled anchor would mean the grid
    // no longer lines up with the level it was authored against.
    check(
      anchor.euler.every((value) => Math.abs(value) <= ROTATION_EPSILON) &&
        anchor.localScale.every((value) => Math.abs(value - 1) <= SCALE_EPSILON),
      `${basename(file)}: LevelStart instance "${anchor.name}" is not a plain translation (euler ${JSON.stringify(anchor.euler)}, scale ${JSON.stringify(anchor.localScale)})`,
    );
    check(
      Number.isFinite(anchor.position[0]) && Number.isFinite(anchor.position[1]),
      `${basename(file)}: LevelStart instance "${anchor.name}" has a non-finite position ${JSON.stringify(anchor.position)}`,
    );
  }
  // `BaseGameMode.GetPartCount` takes the *first* entry whose type matches, so a list naming a
  // PartType twice gives the earlier entry's count (`BaseGameMode.cs:558-578`); the bar then hides
  // every PartType whose count is zero (`ConstructionUI.CheckUnlockedParts`,
  // `ConstructionUI.cs:545-549`), so only the positive ones are content.
  const allowances = new Map();
  for (const entry of limits.partTypeCounts) if (!allowances.has(entry.type)) allowances.set(entry.type, entry.count);
  const partCounts = [];
  for (const [type, count] of allowances) {
    if (count <= 0) continue;
    const partTypeId = partTypes.byEnum.get(type);
    if (partTypeId === undefined) {
      const name = partTypes.names.get(type) ?? `PartType ${type}`;
      check(
        WITHOUT_PIGFORGE_BASE.has(type),
        `${basename(file)}: ${name} has no PigForge base and is not in the recorded no-base list`,
      );
      const census = buildCensus.unmapped.get(type) ?? { name, levels: [] };
      census.levels.push(sceneName);
      buildCensus.unmapped.set(type, census);
      continue;
    }
    partCounts.push({ partTypeId, count });
  }
  partCounts.sort((left, right) => left.partTypeId - right.partTypeId);

  const extent = gridExtent(limits.rows);
  const build = WITHOUT_BUILD.has(sceneName) || anchor === null
    ? null
    : { origin: [Math.fround(anchor.position[0]), Math.fround(anchor.position[1])], rows: limits.rows.slice(), partCounts };
  buildCensus.shapes.set(`${extent.columns}x${extent.height}`, (buildCensus.shapes.get(`${extent.columns}x${extent.height}`) ?? 0) + 1);
  const rowsShape = JSON.stringify(limits.rows);
  buildCensus.rowsShapes.set(rowsShape, (buildCensus.rowsShapes.get(rowsShape) ?? 0) + 1);
  for (const field of limits.secondStar) buildCensus.secondStar.set(field, (buildCensus.secondStar.get(field) ?? 0) + 1);
  if (limits.sandbox) buildCensus.sandbox.push(sceneName);
  if (build === null) {
    buildCensus.withoutBuild.push({
      sceneName,
      bundle,
      sandbox: limits.sandbox,
      rows: limits.rows,
      rowsPatched: limits.rowsPatched,
      columns: extent.columns,
      height: extent.height,
      cells: extent.cells,
      families: partCounts.length,
      parts: partCounts.reduce((total, entry) => total + entry.count, 0),
    });
  } else {
    buildCensus.levels += 1;
    buildCensus.families += partCounts.length;
    buildCensus.parts += partCounts.reduce((total, entry) => total + entry.count, 0);
  }

  // ---------------------------------------------------------------- drawable props
  // The level's dressing: grass, mushrooms, crystals, wall art, and the backdrop sets with their
  // parallax layers -- one entry per instance, drawn by the client in the original's own depth order
  // (`docs/specs/level-props.md`). Instances of every other prop class are counted instead of placed,
  // so a family that stops being classified shows up as a number in the report rather than as silence.
  //
  // The walk also keeps each challenge box's own slot: a `box` challenge's `propIndex` (v10) is the
  // index of the very instance in this level's `props` array -- the same slot the collection segment
  // reads (`docs/specs/level-challenges.md` §3) -- so the challenge reader and the props walk agree
  // on one instance object (`lib/challenges.mjs`).
  const challengeNodes = challengeInstances(data, palette, challengePrefabs);
  const challengePropIndex = new Map();
  const propInstances = [];
  let cloudInstances = 0;
  for (const instance of data.instanceList) {
    const prefabPath = palette[instance.prefabIndex];
    if (typeof prefabPath !== "string" || prefabPath.startsWith("<")) continue;
    if (partMap.prefabToPartTypeId.get(basename(prefabPath, ".prefab")) !== undefined) continue;
    let record;
    try {
      record = props.read(prefabPath);
    } catch (error) {
      check(error instanceof PropException, `${basename(file)}: ${error.stack}`);
      check(false, `${basename(file)}: prop "${instance.name}" (${prefabPath}): ${error.message}`);
      continue;
    }
    propCensus.set(record.kind, (propCensus.get(record.kind) ?? 0) + 1);
    if (record.clouds !== null) {
      // The set itself draws nothing: its clouds are placed as their own props, in world coordinates
      // (`SpawnCloud` writes the world position and Unity's `SetParent` keeps the world scale, so the
      // instance's own scale never reaches them).
      const random = cloudRandom(cloudSeed(instance.name, instance.position));
      cloudInstances += record.clouds.maxClouds;
      for (let index = 0; index < record.clouds.maxClouds; index += 1) {
        const offset = [
          random() * 2 * record.clouds.limits - record.clouds.limits,
          random() * 2 * record.clouds.height - record.clouds.height,
          random() * record.clouds.farPlane,
        ];
        const variant = record.clouds.variants[Math.min(record.clouds.variants.length - 1, Math.floor(random() * record.clouds.variants.length))];
        propInstances.push({
          id: propId(basename(variant, ".prefab")),
          x: Math.fround(instance.position[0] + offset[0]),
          y: Math.fround(instance.position[1] + offset[1]),
          z: Math.fround(instance.position[2] + offset[2]),
          rotation: 0,
          scaleX: 1,
          scaleY: 1,
        });
      }

      continue;
    }

    if (!record.drawable) continue;
    check(
      instance.dataType === 0,
      `${basename(file)}: prop "${instance.name}" (${record.name}) carries a level data block ${instance.dataType}`,
    );
    propInstances.push({
      id: record.id,
      x: instance.position[0],
      y: instance.position[1],
      z: instance.position[2],
      // Unity's `Quaternion.Euler` takes degrees; the client's canvas rotation is radians about z.
      rotation: Math.fround((instance.euler[2] * Math.PI) / 180),
      scaleX: instance.localScale[0],
      scaleY: instance.localScale[1],
      // A solid prop (schemaVersion 7) carries the original's own non-trigger box colliders, in the
      // prop's frame: the room turns each into a static body (`docs/specs/level-props.md` §3.6). A
      // trigger box (schemaVersion 8) is the marker a challenge's rule overlaps with instead
      // (`docs/specs/level-challenges.md` §3).
      ...(record.boxes.length > 0 ? { boxes: boxesOf(record.boxes) } : {}),
      ...(record.triggers.length > 0 ? { triggers: boxesOf(record.triggers) } : {}),
    });
    // One instance object, one slot: the challenge's own entry (if it is one) points back at it.
    if (challengeNodes.has(instance)) challengePropIndex.set(instance, propInstances.length - 1);
  }

  // ---------------------------------------------------------------- write
  const contentVersion = contentVersionOf(sceneName);
  const area = areaOf(bundle);
  const relativePath = `content/levels/original/${area}/${contentVersion}.json`;
  // -------------------------------------------------------------- challenges (v10)
  // The level's own challenge instances, in the original's numbering (`Challenge.Refresh` sorts the
  // registry by instance label and renumbers it 1..N), each with the parameter its own
  // `PrefabOverrides` block mounts -- and a box's back-reference into the `props` array just built.
  const challengeRead = readLevelChallenges({
    sceneName,
    instances: challengeNodes,
    overrides: data.overrides,
    partTypes,
    propIndexByNode: challengePropIndex,
    propCount: propInstances.length,
    check,
  });
  const challenges = challengeRead.challenges;
  const document = {
    format: "pigforge.level-content",
    // v10 is the version that carries the level's own challenges, and every original level is v10 --
    // including the sandbox levels, whose document carries `challenges: []` rather than dropping
    // back to v9 (the array is required; only its contents may be empty).
    schemaVersion: 10,
    contentVersion,
    ...(build === null ? {} : { build }),
    goalZone,
    bounds: levelBounds,
    cameraLimits,
    spawns,
    props: propInstances,
    challenges,
    terrain: terrainEntries,
  };
  const json = formatLevelDocument(document);
  // The document is read back once: a `box` challenge's `propIndex` has to land on a real `props`
  // element (that is what the collection segment indexes), and the 14 challenge-less levels have to
  // write `challenges: []` rather than omit the (v10-required) array.
  const written = JSON.parse(json);
  check(
    Array.isArray(written.challenges) && written.challenges.length === challenges.length,
    `${relativePath}: the document carries ${JSON.stringify(written.challenges)}`,
  );
  check(
    written.challenges.every(
      (challenge) =>
        challenge.kind !== "box" ||
        (Number.isInteger(challenge.propIndex) && challenge.propIndex >= 0 && challenge.propIndex < written.props.length),
    ),
    `${relativePath}: a box challenge's propIndex points outside its ${written.props.length} props`,
  );
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
  totals.propInstances += propInstances.length;
  totals.cloudInstances += cloudInstances;
  totals.triggerProps += propInstances.filter((prop) => prop.triggers !== undefined).length;
  totals.bytes += bytes;

  const state = DRY_RUN ? (changed ? "new" : "same") : changed ? "wrote" : "same";
  console.log(
    `${state.padEnd(5)} ${relativePath}  terrain=${terrainEntries.length} loops=${loops} points=${points}` +
      ` spawns=${spawns.length} goal=${data.goalInstances.length === 1 ? basename(palette[data.goalInstances[0].prefabIndex], ".prefab") : "none"}` +
      ` props=${propInstances.length} challenges=${challenges.length} skipped=${skippedHere} bytes=${bytes}` +
      ` build=${build === null ? "none" : `${extent.columns}x${extent.height} families=${build.partCounts.length} parts=${build.partCounts.reduce((total, entry) => total + entry.count, 0)}`}`,
  );

  // The census: what the level's own challenges are, how many, and which parameter each carries.
  challengeCensus.instances += challengeRead.instances.length;
  if (challengeRead.instances.length > 0) challengeCensus.levels += 1;
  challengeCensus.perLevel.set(
    challengeRead.instances.length,
    (challengeCensus.perLevel.get(challengeRead.instances.length) ?? 0) + 1,
  );
  const labels = new Set();
  for (const instance of challengeRead.instances) {
    countChallenge(challengeCensus.kinds, instance.kind, sceneName);
    countChallenge(challengeCensus.classes, instance.className, sceneName);
    countChallenge(challengeCensus.prefabs, instance.prefabName, sceneName);
    countChallenge(challengeCensus.labels, instance.name, sceneName);
    // One prefab, one class (`BoxChallenge`/`DynamicBoxChallenge` are both a `WaypointChallenge`,
    // never both): the report's prefab table names both halves of every instance.
    const prefabClass = challengeCensus.prefabClass.get(instance.prefabName);
    check(
      prefabClass === undefined || prefabClass === instance.className,
      `${instance.prefabName} is both ${prefabClass} and ${instance.className}`,
    );
    challengeCensus.prefabClass.set(instance.prefabName, instance.className);
    // Two same-labelled challenges in one level: the ordinal sort has no key to order them, so the
    // level file's own order decides (deviation D1). Counted, because the three such levels are the
    // only places the sort is not fully determined by the data.
    if (labels.has(instance.name)) challengeCensus.duplicateLabels.push({ sceneName, label: instance.name });
    labels.add(instance.name);
    if (instance.kind === "time") {
      countChallenge(challengeCensus.targetTimes, instance.targetTime, sceneName);
      // The one instance whose level never mounts the class keeps the prefab's own value (30).
      if (!instance.targetTimeOverridden) {
        challengeCensus.prefabDefaults.push({ sceneName, name: instance.name, targetTime: instance.targetTime });
      }
    } else if (instance.kind === "dontUseParts") {
      countChallenge(challengeCensus.dontUseParts, instance.partTypeEnum, sceneName);
      countChallenge(challengeCensus.partTypeIds, instance.partTypeId, sceneName);
    } else if (instance.kind === "transport") {
      countChallenge(challengeCensus.transports, instance.partTypeEnum, sceneName);
      countChallenge(challengeCensus.partTypeIds, instance.partTypeId, sceneName);
    }
  }

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
    props: propInstances.length,
    // The level's own challenges (v10): how many instances it places (0 for the 14 sandbox levels,
    // whose document carries an empty array) and which kinds they are.
    challenges: challenges.length,
    challengeKinds: challenges.map((challenge) => challenge.kind).join(","),
    // The level's own construction limits: the grid its `m_constructionGridRows` describes, and --
    // for a level that carries a `build` block -- how many part families and parts it allows.
    build: {
      carried: build !== null,
      sandbox: limits.sandbox,
      columns: extent.columns,
      height: extent.height,
      cells: extent.cells,
      rows: limits.rows.length,
      rowsPatched: limits.rowsPatched,
      families: partCounts.length,
      parts: partCounts.reduce((total, entry) => total + entry.count, 0),
      secondStar: limits.secondStar,
    },
    goalSource,
    changed,
  });
}

for (const [bundle, expected] of Object.entries(BUNDLE_EXPECT)) {
  check(bundleCounts[bundle] === expected, `${bundle}: ${bundleCounts[bundle] ?? 0} files, expected ${expected}`);
}
check(skippedTerrains.length === 0, `${skippedTerrains.length} terrain(s) have no closed outline and were skipped`);
check(skippedCurves.length === 0, `${skippedCurves.length} terrain(s) have no usable curve mesh and were skipped`);
// The census covers every prefab instance in the pack except the placed parts (`spawns`), so its sum
// is the pack's own instance count -- a prop family that stops being classified cannot hide.
check(totals.propInstances === 18204, `placed prop instances: ${totals.propInstances}, expected 18204`);
check(totals.triggerProps === 948, `props with trigger boxes: ${totals.triggerProps}, expected 948`);
check(totals.cloudInstances === 1020, `cloud instances: ${totals.cloudInstances}, expected 1020`);
const cachedInstances = [...propCensus.values()].reduce((total, count) => total + count, 0) + totals.spawns;
check(cachedInstances === 26072, `classified instances: ${cachedInstances}, expected 26072`);

// The build-limits census: every level's grid and allowance is read, so a level that stops being
// walked cannot hide behind a quieter report. The no-`build` levels are named, not counted: the
// carve-out is a content decision (see `WITHOUT_BUILD`), and the assertion keeps it to exactly the
// levels the spec records -- each of them a sandbox level, whose allowance the original tops up
// from the player's own unlocks.
const withoutBuildNames = buildCensus.withoutBuild.map((entry) => entry.sceneName).sort();
check(
  JSON.stringify(withoutBuildNames) === JSON.stringify([...WITHOUT_BUILD].sort()),
  `levels without a build block: ${withoutBuildNames.join(", ")}, expected ${[...WITHOUT_BUILD].sort().join(", ")}`,
);
check(
  buildCensus.withoutBuild.every((entry) => entry.sandbox),
  `levels without a build block that are not sandbox levels: ${buildCensus.withoutBuild.filter((entry) => !entry.sandbox).map((entry) => entry.sceneName).join(", ")}`,
);
check(
  buildCensus.levels === 275,
  `levels with a build block: ${buildCensus.levels}, expected 275 of ${rows.length}`,
);
// The exempt levels are exactly the ones that never wrote a grid of their own: their override
// touches one row of the `LevelManager` prefab's default and inherits the rest. Naming a level in
// `WITHOUT_BUILD` that does define its own grid (or leaving one out) is drift, not a judgement call.
const partialRows = rows
  .filter((row) => row.build.rowsPatched < row.build.rows)
  .map((row) => row.sceneName)
  .sort();
check(
  JSON.stringify(partialRows) === JSON.stringify(withoutBuildNames),
  `levels whose m_constructionGridRows override does not write every row: ${partialRows.join(", ")}, expected exactly the exempt ones (${withoutBuildNames.join(", ")})`,
);
// No level in the pack is unplaceable: the carve-out above is about the sandbox allowance, never
// about an empty grid (`gridExtent` reads the original's own rectangle).
const unplaceable = rows.filter((row) => row.build.cells === 0).map((row) => row.sceneName);
check(unplaceable.length === 0, `levels with no placeable grid cell: ${unplaceable.join(", ")}`);

// ---------------------------------------------------------------- challenges (v10)

// The challenge census, pinned the way the prop and build counts above are: a prefab that stops
// resolving to its `Challenge` subclass, a level that stops placing one, or a parameter that moves
// source changes one of these numbers instead of passing silently. Measured 2026-10-11 against the
// pristine pack (`docs/specs/level-stars.md` §7).
const challengePerLevel = [...challengeCensus.perLevel.entries()]
  .sort((left, right) => left[0] - right[0])
  .map(([count, levels]) => ({ instances: count, levels }));
const challengeKindCounts = Object.fromEntries(
  [...challengeCensus.kinds].map(([kind, entry]) => [kind, entry.instances]).sort(([left], [right]) => left.localeCompare(right)),
);
check(
  challengeCensus.instances === 527,
  `challenge instances: ${challengeCensus.instances}, expected 527`,
);
check(challengeCensus.levels === 263, `levels with a challenge: ${challengeCensus.levels}, expected 263 of ${rows.length}`);
check(
  JSON.stringify(challengePerLevel) === JSON.stringify([
    { instances: 0, levels: 14 },
    { instances: 2, levels: 262 },
    { instances: 3, levels: 1 },
  ]),
  `challenges per level: ${JSON.stringify(challengePerLevel)}, expected 14 levels with 0, 262 with 2 and 1 with 3`,
);
check(
  JSON.stringify(challengeKindCounts) ===
    JSON.stringify({ box: 281, dontUseParts: 79, perfectFlight: 44, time: 113, transport: 10 }),
  `challenges per kind: ${JSON.stringify(challengeKindCounts)}, expected 281 box / 113 time / 79 dontUseParts / 44 perfectFlight / 10 transport`,
);
// The one level with three challenges is the one whose third instance is the only one outside the
// star pair (`LevelComplete` consumes `m_challenges[0]` and `[1]` only).
const threeChallengeLevels = rows.filter((row) => row.challenges === 3).map((row) => row.sceneName);
check(
  JSON.stringify(threeChallengeLevels) === JSON.stringify(["episode_6_level_25"]),
  `levels with three challenges: ${threeChallengeLevels.join(", ")}, expected episode_6_level_25`,
);
// The 14 challenge-less levels: every one still writes the (required) empty array, and they are
// exactly the pack's own sandbox levels -- the same 14 `m_sandbox` levels the build census lists.
const zeroChallengeLevels = rows.filter((row) => row.challenges === 0).map((row) => row.sceneName).sort();
check(
  JSON.stringify(zeroChallengeLevels) === JSON.stringify(CHALLENGE_LESS_LEVELS),
  `levels with no challenge: ${zeroChallengeLevels.join(", ")}, expected ${CHALLENGE_LESS_LEVELS.join(", ")}`,
);
check(
  zeroChallengeLevels.every((sceneName) => buildCensus.sandbox.includes(sceneName)),
  `levels with no challenge that are not sandbox levels: ${zeroChallengeLevels.filter((sceneName) => !buildCensus.sandbox.includes(sceneName)).join(", ")}`,
);
check(
  rows.every((row) => row.challengeKinds.split(",").filter((kind) => kind.length > 0).length === row.challenges),
  "a level's challenge kinds do not line up with its challenge count",
);
// Every entry carries the label the original sorts on (`gameObject.name`, `LevelLoader.cs:127`); the
// three levels with two same-labelled challenges are the only ones the ordinal sort cannot order on
// its own (`lib/challenges.mjs`, deviation D1).
check(
  [...challengeCensus.labels.keys()].every((label) => typeof label === "string" && label.length > 0),
  "a challenge carries an empty label",
);
const duplicateLabelLevels = [...new Set(challengeCensus.duplicateLabels.map((entry) => entry.sceneName))].sort();
check(
  JSON.stringify(duplicateLabelLevels) ===
    JSON.stringify(["episode_6_level_IX", "episode_6_level_25", "episode_6_level_32"].sort()),
  `levels with two same-labelled challenges: ${duplicateLabelLevels.join(", ")}, expected episode_6_level_25, episode_6_level_32 and episode_6_level_IX`,
);

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

// The build-limits census in the order the report reads it: the grid shapes the levels describe
// (`columns x height`), the raw row lists behind them, the second-star fields, and every allowance
// that could not cross over because no part-map prefab declares its PartType.
const gridShapes = [...buildCensus.shapes]
  .map(([shape, levels]) => ({ columns: Number(shape.split("x")[0]), height: Number(shape.split("x")[1]), levels }))
  .sort((left, right) => left.columns - right.columns || left.height - right.height)
  .map((shape) => ({ shape: `${shape.columns}x${shape.height}`, columns: shape.columns, height: shape.height, levels: shape.levels }));
const rowShapes = [...buildCensus.rowsShapes]
  .map(([shape, levels]) => ({ rows: JSON.parse(shape), levels }))
  .sort((left, right) => right.levels - left.levels || JSON.stringify(left.rows).localeCompare(JSON.stringify(right.rows)));
const secondStar = Object.fromEntries([...buildCensus.secondStar.entries()].sort());
const noPigForgeBase = [...buildCensus.unmapped]
  .map(([type, census]) => ({ type, name: census.name, levels: census.levels.length, levelNames: census.levels }))
  .sort((left, right) => left.type - right.type);

// The census totals, pinned the way the prop counts above are: a prefab table that stops being read
// or an element applied to the wrong slot moves `families`/`parts`, and the second-star triple
// (counted, never carried -- D3) catches a level that starts or stops declaring one.
check(buildCensus.families === 1926, `build allowances: ${buildCensus.families} part families over ${buildCensus.levels} levels, expected 1926`);
check(buildCensus.parts === 3394, `build allowances: ${buildCensus.parts} parts over ${buildCensus.levels} levels, expected 3394`);
check(secondStar.m_secondStarConstructionGridRows === 20, `m_secondStarConstructionGridRows: ${secondStar.m_secondStarConstructionGridRows} level(s), expected 20`);
check(secondStar.m_extraPartsForSecondStar === 21, `m_extraPartsForSecondStar: ${secondStar.m_extraPartsForSecondStar} level(s), expected 21`);
check(secondStar.m_gridForSecondStar === 22, `m_gridForSecondStar: ${secondStar.m_gridForSecondStar} level(s), expected 22`);
const noBaseLevels = Object.fromEntries(noPigForgeBase.map((entry) => [entry.type, entry.levels]));
check(
  noBaseLevels[42] === 16 && noBaseLevels[45] === 14,
  `no-PigForge-base allowances: ${JSON.stringify(noBaseLevels)}, expected Pumpkin(42) 16 and GoldenPig(45) 14 levels`,
);

// The challenge census, rendered for the report: one row per key (a kind, a prefab name, an
// instance label, a PartType enum or a `m_targetTime` in seconds), biggest first, and the PartType
// rows resolved through `resolvePartTypes` so the report shows the enum *and* the PigForge id it
// became (the enum is never an id).
const challengeRows = (map) =>
  [...map.entries()]
    .map(([key, entry]) => ({ key, instances: entry.instances, levels: entry.levels.size }))
    .sort((left, right) => right.instances - left.instances || String(left.key).localeCompare(String(right.key)));
const challengePartTypes = (map) =>
  challengeRows(map).map((entry) => ({
    partType: entry.key,
    name: partTypes.names.get(entry.key) ?? null,
    partTypeId: partTypes.byEnum.get(entry.key) ?? null,
    instances: entry.instances,
    levels: entry.levels,
  }));
const challengeReport = {
  instances: challengeCensus.instances,
  levels: challengeCensus.levels,
  perLevel: challengePerLevel,
  kinds: challengeRows(challengeCensus.kinds),
  classes: challengeRows(challengeCensus.classes),
  prefabs: challengeRows(challengeCensus.prefabs),
  prefabClasses: Object.fromEntries([...challengeCensus.prefabClass].sort()),
  labels: challengeRows(challengeCensus.labels),
  // `seconds` -> instances: the value the challenge is actually compared against (`TimeChallenge`),
  // whether the level wrote it or it is the prefab's own (the one entry in `prefabDefaults`).
  targetTimes: challengeRows(challengeCensus.targetTimes).map((entry) => ({
    seconds: entry.key,
    instances: entry.instances,
    levels: entry.levels,
  })),
  prefabDefaults: challengeCensus.prefabDefaults,
  dontUseParts: challengePartTypes(challengeCensus.dontUseParts),
  transports: challengePartTypes(challengeCensus.transports),
  partTypeIds: challengeRows(challengeCensus.partTypeIds).map((entry) => ({
    partTypeId: entry.key,
    instances: entry.instances,
    levels: entry.levels,
  })),
  duplicateLabels: challengeCensus.duplicateLabels,
  zeroLevels: zeroChallengeLevels,
  threeChallengeLevels,
};

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
    propInstances: totals.propInstances,
    propKinds: Object.fromEntries([...propCensus.entries()].sort(([left], [right]) => left.localeCompare(right))),
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
    buildLevels: buildCensus.levels,
    buildWithout: buildCensus.withoutBuild.length,
    buildFamilies: buildCensus.families,
    buildParts: buildCensus.parts,
    buildGridShapes: gridShapes.length,
    buildRowShapes: rowShapes.length,
    buildSandboxLevels: buildCensus.sandbox.length,
    buildNoPigForgeBase: noPigForgeBase.length,
    challengeInstances: challengeCensus.instances,
    challengeLevels: challengeCensus.levels,
    challengeKinds: challengeKindCounts,
    challengePrefabs: challengeCensus.prefabs.size,
    challengeLabels: challengeCensus.labels.size,
    challengeZeroLevels: zeroChallengeLevels.length,
    challengeDuplicateLabels: challengeCensus.duplicateLabels.length,
    textureRoot: OUT_TEXTURES,
  },
  build: {
    levels: buildCensus.levels,
    families: buildCensus.families,
    parts: buildCensus.parts,
    gridShapes,
    rowShapes,
    secondStar,
    sandboxLevels: [...buildCensus.sandbox].sort(),
    noPigForgeBase,
    withoutBuild: buildCensus.withoutBuild,
  },
  challenges: challengeReport,
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
md.push(`- 建造限制（\`build\`，v9）：**${buildCensus.levels}/${rows.length}** 关携带，**${buildCensus.withoutBuild.length}** 关豁免；零件族合计 **${buildCensus.families}**，配额合计 **${buildCensus.parts}**，网格形状 **${gridShapes.length}** 种（\`columns x height\`），原始 rows 列表 **${rowShapes.length}** 种`);
md.push(`- 挑战（\`challenges\`，v10）：**${challengeCensus.instances}** 个实例落在 **${challengeCensus.levels}/${rows.length}** 关，每类 ${challengeReport.kinds.map((entry) => `**${entry.key}** ${entry.instances}`).join(" / ")}；每关实例数 ${challengePerLevel.map((entry) => `**${entry.levels}** 关 ${entry.instances} 个`).join(" / ")}；**${zeroChallengeLevels.length}** 关写 \`[]\``);
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
md.push("## 建造限制", "");
md.push("`build` 块的三个字段都来自原版：`origin` = 该关 `LevelStart` 实例的 `(x, y)`（float32 精确；单元格 → 世界 = 锚点 + (i, j)，`Contraption.cs:1579-1590`），");
md.push("`rows` = 该关 `m_constructionGridRows` 原样，`partCounts` = `m_partTypeCounts` 里第一个 `type` 匹配项的 `count`（`BaseGameMode.GetPartCount`，`BaseGameMode.cs:558-578`），只保留 `count > 0` 且能解析到 PigForge 基座的行。");
md.push("两者都是对 `Assets/GameObject/LevelManager.prefab` 自己那张表的**按下标补丁**（`ObjectDeserializer.ReadArray`，`ObjectDeserializer.cs:720-800`：`ArraySize` 截断、`Element <下标>` 就地改写、缺字段保留原值、缺元素保留原槽），所以默认表读自 prefab，工具里没有硬编码。");
md.push(`网格的列数/行数按原版自己的矩形（\`BaseGameMode.InitGameMode\`，\`BaseGameMode.cs:49-71\`，vanilla 设置 \`InfiniteGrid=false\`/\`GridSize=1\`）：\`columns\` = 最宽行的最高位 + 1，\`height\` = 最后一个非零行的下标 + 1，两者最小 1。`)
md.push(`- 关卡 **${rows.length}**，携带 \`build\` **${buildCensus.levels}**，豁免 **${buildCensus.withoutBuild.length}**（逐关见下）`);
md.push(`- 零件族合计 **${buildCensus.families}**，配额合计 **${buildCensus.parts}**`);
md.push(`- 网格形状（\`columns x height\`）**${gridShapes.length}** 种，原始 rows 列表 **${rowShapes.length}** 种`);
md.push(`- 第二星字段（原版反编译源码里没有任何读取者，本批不搬）：${Object.entries(secondStar).map(([field, levels]) => `\`${field}\` ${levels} 关`).join("、")}`);
md.push(`- 无 PigForge 基座的 PartType：${noPigForgeBase.map((entry) => `\`${entry.name}\`(${entry.type}) ${entry.levels} 关`).join("、")}`);
md.push("", "### 网格形状直方图", "");
md.push("| columns x height | 关卡数 |", "|---|---|");
for (const shape of gridShapes) md.push(`| ${shape.shape} | ${shape.levels} |`);
md.push("", "### rows 列表（按关卡数）", "");
md.push("| rows | 关卡数 |", "|---|---|");
for (const shape of rowShapes) md.push(`| \`${JSON.stringify(shape.rows)}\` | ${shape.levels} |`);
md.push("", "### 豁免关卡（不写 `build`）", "");
md.push("原版沙盒模式的零件来源是 `GameProgress` 的解锁表（`BaseGameMode.GetPartCount` 在 `m_sandbox` 时叠加玩家自己的解锁，我们没建模），");
md.push("所以这几关不写 `build`（= 不限制，即今天的行为）。它们也是**唯一**不自己写整套 `rows` 的关卡：override 只写了个别行，其余继承 `LevelManager` prefab 自己的 7×1023 默认表，");
md.push(`按原版公式仍是 ${[...new Set(buildCensus.withoutBuild.map((entry) => `${entry.columns}x${entry.height}`))].join(" / ")}，共 ${buildCensus.withoutBuild.reduce((total, entry) => total + entry.cells, 0)} 个可放单元，\`m_sandbox\` 全为真。`, "");
md.push("| area | scene | m_sandbox | 自写行/总行 | rows | 网格 | 可放单元 | 族 | 配额 |", "|---|---|---|---|---|---|---|---|---|");
for (const entry of buildCensus.withoutBuild) {
  md.push(
    `| ${areaOf(entry.bundle)} | \`${entry.sceneName}\` | ${entry.sandbox} | ${entry.rowsPatched}/${entry.rows.length} | \`${JSON.stringify(entry.rows)}\` | ${entry.columns}x${entry.height} | ${entry.cells} | ${entry.families} | ${entry.parts} |`,
  );
}
md.push("", "### 无 PigForge 基座的 PartType", "");
md.push("一个 `PartType` 只有在 `part-map.json` 名下的某个 prefab 声明它时才有 PigForge 基座（`tools/lib/part-types.mjs`）；枚举值本身永远不是 `partTypeId`。");
md.push("没有基座的配额搬不进内容（PigForge 没有对应零件），逐条计数如下（`Pumpkin`/`GoldenPig` 是 G91 未交付，见规格 D4）。");
md.push("统计覆盖全部 277 关的原始配额表，包含上面不写 `build` 的豁免关。", "");
md.push("| PartType | 名称 | 出现关数 | 关卡 |", "|---|---|---|---|");
for (const entry of noPigForgeBase) md.push(`| ${entry.type} | \`${entry.name}\` | ${entry.levels} | ${entry.levelNames.map((name) => `\`${name}\``).join("、")} |`);
md.push("");
md.push("## 挑战（`challenges`，v10）", "");
md.push("挑战就是关卡实例：类名来自实例 → 调色板 prefab → 该 prefab 的 `m_Script` guid（`tools/lib/unity.mjs` 的 guid → class 索引），");
md.push("元素顺序 = 原版自己的编号顺序（`Challenge.Refresh` 按实例名排序后重编号 1..N，`Challenge.cs:62-81`；本工具用**序数**比较，同名实例按关卡文件出现顺序，见规格 D1）。");
md.push("逐关参数只来自该实例自己的 `PrefabOverrides` 块：`TimeChallenge.m_targetTime`（Float）与 `DontUsePartChallenge.m_partType`（Enum）；");
md.push("块没挂这个类（或整关没有这块）就用 prefab 自己的序列化值 —— `TransportChallenge.partToTransport` 在 prefab 里是 `32`（`KingPig`），没有一关改写它。");
md.push("枚举 → PigForge `partTypeId` 只经 `tools/lib/part-types.mjs` 的 `resolvePartTypes`（唯一实现），解析不到即失败。");
md.push(`- 实例 **${challengeCensus.instances}** 个 / **${challengeCensus.levels}** 关（${rows.length} 关里），实例标签去重后 **${challengeCensus.labels.size}** 种`);
md.push(`- 每关实例数：${challengePerLevel.map((entry) => `**${entry.instances}** 个 → ${entry.levels} 关`).join("、")}（3 个的那关是 \`${threeChallengeLevels.join("`、`")}\`，第 3 个不参与星级）`);
md.push(`- 重名实例（排序无法唯一定序）：${challengeCensus.duplicateLabels.map((entry) => `\`${entry.sceneName}\` 的 \`${entry.label}\``).join("、") || "无"}`);
md.push(`- 没有挑战的 **${zeroChallengeLevels.length}** 关（文档写 \`challenges: []\`，它们也正是 \`m_sandbox\` 的 14 关）：${zeroChallengeLevels.map((name) => `\`${name}\``).join("、")}`, "");
md.push("| 类 | kind | 实例数 | 关卡数 |", "|---|---|---|---|");
for (const entry of challengeReport.classes) {
  md.push(`| \`${entry.key}\` | \`${CHALLENGE_KIND_BY_CLASS.get(entry.key)}\` | ${entry.instances} | ${entry.levels} |`);
}
md.push("", "| prefab | 类 | 实例数 | 关卡数 |", "|---|---|---|---|");
for (const entry of challengeReport.prefabs) {
  md.push(`| \`${entry.key}\` | \`${challengeReport.prefabClasses[entry.key]}\` | ${entry.instances} | ${entry.levels} |`);
}
md.push("", "`time` 的 `m_targetTime`（对比值本身；`prefab 默认` = 该实例的 override 块没挂 `TimeChallenge`，值来自 prefab）：", "");
md.push("| 秒 | 实例数 | 关卡数 |", "|---|---|---|");
for (const entry of challengeReport.targetTimes) md.push(`| ${entry.seconds} | ${entry.instances} | ${entry.levels} |`);
md.push("", `用 prefab 默认值的实例：${challengeReport.prefabDefaults.map((entry) => `\`${entry.sceneName}\`（${entry.targetTime} 秒）`).join("、") || "无"}`, "");
md.push("`dontUseParts` 的 `m_partType`（31 个不同枚举，79 个实例全部在 override 里写了；枚举值本身永远不是 `partTypeId`）：", "");
md.push("| PartType | 名称 | PigForge partTypeId | 实例数 | 关卡数 |", "|---|---|---|---|---|");
for (const entry of challengeReport.dontUseParts) {
  md.push(`| ${entry.partType} | \`${entry.name}\` | \`${entry.partTypeId}\` | ${entry.instances} | ${entry.levels} |`);
}
md.push("", "`transport` 的 `partToTransport`：", "");
md.push("| PartType | 名称 | PigForge partTypeId | 实例数 | 关卡数 |", "|---|---|---|---|---|");
for (const entry of challengeReport.transports) {
  md.push(`| ${entry.partType} | \`${entry.name}\` | \`${entry.partTypeId}\` | ${entry.instances} | ${entry.levels} |`);
}
md.push("");
md.push("## 每关", "");
md.push("| area | scene | contentVersion | 地形 | 环 | 点 | 零件 | 挑战 | 网格 | 族 | 配额 | 字节 | 终点来源 | hash |", "|---|---|---|---|---|---|---|---|---|---|---|---|---|---|");
for (const row of rows) {
  md.push(
    `| ${row.area} | \`${row.sceneName}\` | \`${row.contentVersion}\` | ${row.terrain} | ${row.loops} | ${row.points} | ${row.spawns}` +
      ` | ${row.challenges === 0 ? "—" : `${row.challenges} (${row.challengeKinds})`}` +
      ` | ${row.build.carried ? `${row.build.columns}x${row.build.height}` : "—"} | ${row.build.families} | ${row.build.parts} | ${row.bytes}` +
      ` | ${row.goalSource.replaceAll("|", "\\|")} | \`${row.hash.slice(0, 12)}\` |`,
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
  `build limits: ${buildCensus.levels}/${rows.length} levels (${buildCensus.withoutBuild.length} without: ` +
    `${buildCensus.withoutBuild.map((entry) => entry.sceneName).join(", ") || "none"}), ` +
    `${buildCensus.families} families / ${buildCensus.parts} parts, ` +
    `${gridShapes.length} grid shape(s), ${rowShapes.length} row list(s)`,
);
console.log(
  `build no-base PartTypes: ${noPigForgeBase.map((entry) => `${entry.name}(${entry.type}) x${entry.levels}`).join(", ") || "none"}` +
    `  sandbox levels: ${buildCensus.sandbox.length}` +
    `  second-star fields: ${Object.entries(secondStar).map(([field, levels]) => `${field}=${levels}`).join(" ")}`,
);
console.log(
  `challenges: ${challengeCensus.instances} instance(s) over ${challengeCensus.levels}/${rows.length} levels (` +
    `${challengeReport.kinds.map((entry) => `${entry.key} ${entry.instances}`).join(" / ")}, ` +
    `${challengeCensus.prefabs.size} prefab(s)), ` +
    `per level ${challengePerLevel.map((entry) => `${entry.levels}x${entry.instances}`).join(" ")}` +
    `  no challenge: ${zeroChallengeLevels.length} level(s)`,
);
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
