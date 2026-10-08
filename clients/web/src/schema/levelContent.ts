import type { Vec3 } from "./types";

/** The actor a spawn creates: an inert part, a pig, or a lit fuse that explodes (`LevelActorRole`). */
export type LevelActorRole = "part" | "pig" | "tnt";

/**
 * One part instance the level places. Only `partTypeId` and `position` are required; the rest is
 * the original's per-instance override (`PrefabOverrides`) or a spawn-time convenience.
 */
export interface LevelSpawn {
  partTypeId: number;
  position: Vec3;
  angle?: number;
  role?: LevelActorRole;
  tntFuseTicks?: number;
  motorImpulsePerTick?: number;
  motorDirectionX?: -1 | 0 | 1;
  wheel?: boolean;
}

/** An axis-aligned world box; `max` is never smaller than `min` on any axis. */
export interface LevelZone {
  min: Vec3;
  max: Vec3;
}

/**
 * A terrain's own RGBA tint, in bytes -- the level file's own `uint32` (`LevelLoader.ReadColor`
 * multiplies each byte by 0.003921569f to get the shader's `_Color`, so the bytes are the exact
 * value the file holds).
 */
export type LevelFillColor = [number, number, number, number];

/**
 * How one terrain's ground is painted: the original's `e2d/Fill` shader is
 * `tex2D(_MainTex, uv) * _Color` with `uv = (world - tileOffset) / tileSize`, where `texture` names
 * a file under `LEVEL_TEXTURE_BASE`, `tileOffset` comes from the level file and `tileSize` -- the one
 * input the level file does not carry -- from the terrain prefab (5 x 5 world metres on all of them).
 * See `docs/specs/level-terrain-visuals.md`.
 */
export interface LevelTerrainFill {
  texture: string;
  color: LevelFillColor;
  tileOffset: [number, number];
  tileSize: [number, number];
}

/** One of the `e2d/Curve` shader's two layers (`_Splat0` / `_Splat1`) and its own Unity wrap mode. */
export type LevelCurveWrap = "repeat" | "clamp";

/** A curve layer's file name (under `LEVEL_TEXTURE_BASE`) and how its u past 1 is sampled. */
export interface LevelCurveTexture {
  texture: string;
  wrap: LevelCurveWrap;
}

/**
 * One terrain's `_curve` band, the original's `e2d/Curve` shader's own inputs: `nodes` are the
 * `TerrainCurve` points on the terrain surface (the strip's even vertices) and `stripe` is each node
 * pushed outwards by its `e2dCurveTexture.size.y` (the odd vertices). The shader maps `u` along the
 * cumulative arc length times `uScale` (= 1 / `textures[0]`'s width) and `v` = 1 on `nodes` / 0 on
 * `stripe`; `splat1` lists the node runs drawn with `textures[1]` (the original's control texture's
 * green channel) and every other node uses `textures[0]`. See
 * `docs/specs/level-terrain-visuals.md`.
 */
export interface LevelTerrainCurve {
  nodes: Array<[number, number]>;
  stripe: Array<[number, number]>;
  textures: [LevelCurveTexture, LevelCurveTexture];
  uScale: number;
  splat1: Array<[number, number]>;
}

/**
 * The per-node layer of one curve: one byte per node, 1 where `splat1` draws it with `textures[1]`
 * and 0 everywhere else. Decoded once per document, so a painter never re-walks the runs per frame.
 */
export function curveLayerBitmap(curve: LevelTerrainCurve): Uint8Array {
  const layers = new Uint8Array(curve.nodes.length);
  for (const [start, count] of curve.splat1) {
    layers.fill(1, start, start + count);
  }
  return layers;
}

/**
 * One `e2dTerrain`: `loops` are the ground's outline polygons in the terrain's own local frame
 * (`[x, y]` metres, closed implicitly: the last point joins the first) and `position` places that
 * frame in the world. `depth` is the extrusion along z the server turns into the collision shell;
 * the visual ground is the filled loop, textured by `fill` when the document is a v3 one and
 * trimmed by `curve` when it is a v4 one.
 *
 * A v3 document also carries the original's own `hasCollider`: 498 of the original's 2146 terrains
 * are decoration and the server builds no body for them. That is the server's business -- this
 * client draws every terrain, exactly as the original does.
 */
export interface LevelTerrain {
  position: Vec3;
  depth: number;
  collider?: boolean;
  fill?: LevelTerrainFill;
  curve?: LevelTerrainCurve;
  loops: Array<Array<[number, number]>>;
}

export interface LevelContentDocument {
  format: "pigforge.level-content";
  schemaVersion: 1 | 2 | 3 | 4;
  contentVersion: string;
  goalZone: LevelZone;
  bounds: LevelZone;
  spawns: LevelSpawn[];
  /** v2 only; a v1 document omits it and the client draws no terrain. */
  terrain?: LevelTerrain[];
}

/** A rectangle in the plane the renderer draws (x/y world metres, y up). */
export interface WorldRect {
  minX: number;
  minY: number;
  maxX: number;
  maxY: number;
}

/** The x/y rectangle a zone covers; the plane renderer never uses a zone's z. */
export function zoneRect(zone: LevelZone): WorldRect {
  return { minX: zone.min[0], minY: zone.min[1], maxX: zone.max[0], maxY: zone.max[1] };
}

const ROOT_REQUIRED_KEYS = ["format", "schemaVersion", "contentVersion", "goalZone", "bounds", "spawns"];
// v1 has no `terrain`, but the server parser accepts the v2 addition on either version, and the
// client only ever decodes what the server already parsed.
const ROOT_KEYS = [...ROOT_REQUIRED_KEYS, "terrain"];
const ZONE_KEYS = ["min", "max"];
const SPAWN_REQUIRED_KEYS = ["partTypeId", "position"];
const SPAWN_KEYS = [...SPAWN_REQUIRED_KEYS, "angle", "role", "tntFuseTicks", "motorImpulsePerTick", "motorDirectionX", "wheel"];
const TERRAIN_REQUIRED_KEYS = ["position", "depth", "loops"];
// v3 gives every terrain a collider bit and the ground's fill; older versions must not carry them.
const TERRAIN_V3_KEYS = ["collider", "fill"];
// v4 adds the `_curve` band along the terrain's outline; only a v4 terrain carries one.
const TERRAIN_V4_KEYS = ["curve"];
const TERRAIN_V3_REQUIRED_KEYS = [...TERRAIN_REQUIRED_KEYS, ...TERRAIN_V3_KEYS];
const TERRAIN_KEYS = [...TERRAIN_V3_REQUIRED_KEYS, ...TERRAIN_V4_KEYS];
const FILL_KEYS = ["texture", "color", "tileOffset", "tileSize"];
const CURVE_KEYS = ["nodes", "stripe", "textures", "uScale", "splat1"];
const CURVE_TEXTURE_KEYS = ["texture", "wrap"];
const LEVEL_ROLES: readonly LevelActorRole[] = ["part", "pig", "tnt"];
const LEVEL_WRAPS: readonly LevelCurveWrap[] = ["repeat", "clamp"];

export function validateLevelContent(value: unknown): string[] {
  const errors: string[] = [];
  if (value === null || typeof value !== "object" || Array.isArray(value)) {
    return ["Level content document is required."];
  }

  const document = value as Record<string, unknown>;
  validateKeys(document, ROOT_REQUIRED_KEYS, ROOT_KEYS, "root", errors);
  if (document.format !== "pigforge.level-content") {
    errors.push("root.format: must be 'pigforge.level-content'.");
  }
  if (document.schemaVersion !== 1 && document.schemaVersion !== 2
    && document.schemaVersion !== 3 && document.schemaVersion !== 4) {
    errors.push("root.schemaVersion: versions 1 to 4 are supported.");
  }
  if (!isContentVersion(document.contentVersion)) {
    errors.push("root.contentVersion: must contain 1 to 128 non-whitespace-padded characters.");
  }
  validateZone(document.goalZone, "root.goalZone", errors);
  validateZone(document.bounds, "root.bounds", errors);
  if (!Array.isArray(document.spawns)) {
    errors.push("root.spawns: must be an array.");
  } else {
    document.spawns.forEach((spawn, index) => validateSpawn(spawn, `root.spawns[${index}]`, errors));
  }
  if (document.terrain !== undefined) {
    if (!Array.isArray(document.terrain)) {
      errors.push("root.terrain: must be an array.");
    } else {
      const modern = document.schemaVersion === 3 || document.schemaVersion === 4;
      const curved = document.schemaVersion === 4;
      document.terrain.forEach((terrain, index) => {
        validateTerrain(terrain, `root.terrain[${index}]`, modern, curved, errors);
      });
    }
  }
  return errors;
}

/**
 * Validates and narrows the document the server served. `level` is null whenever anything was
 * rejected: the caller then keeps its own fallback (no terrain, the builder's zone and bounds).
 */
export function decodeLevelContent(value: unknown): { level: LevelContentDocument | null; errors: string[] } {
  const errors = validateLevelContent(value);
  return { level: errors.length === 0 ? (value as LevelContentDocument) : null, errors };
}

/** The exact-key-set rule the server parser applies: nothing unknown, nothing required missing. */
function validateKeys(
  value: Record<string, unknown>,
  required: readonly string[],
  allowed: readonly string[],
  path: string,
  errors: string[],
): void {
  for (const key of Object.keys(value)) {
    if (!allowed.includes(key)) {
      errors.push(`${path}: unknown property '${key}'.`);
    }
  }
  for (const key of required) {
    if (!(key in value)) {
      errors.push(`${path}: missing required property '${key}'.`);
    }
  }
}

/** A version string: 1 to 128 characters, no whitespace padding (`LevelContentParser.ReadVersion`). */
function isContentVersion(value: unknown): value is string {
  return typeof value === "string" && value.length >= 1 && value.length <= 128 && value.trim() === value;
}

function isFiniteNumber(value: unknown): value is number {
  return typeof value === "number" && Number.isFinite(value);
}

function isVec3(value: unknown): value is Vec3 {
  return Array.isArray(value) && value.length === 3 && value.every(isFiniteNumber);
}

function validateZone(value: unknown, path: string, errors: string[]): void {
  if (value === null || typeof value !== "object" || Array.isArray(value)) {
    errors.push(`${path}: zone must be a JSON object.`);
    return;
  }

  const zone = value as Record<string, unknown>;
  validateKeys(zone, ZONE_KEYS, ZONE_KEYS, path, errors);
  if (!isVec3(zone.min)) {
    errors.push(`${path}.min: must be an array of three finite numbers.`);
  }
  if (!isVec3(zone.max)) {
    errors.push(`${path}.max: must be an array of three finite numbers.`);
  }
  if (isVec3(zone.min) && isVec3(zone.max)
    && (zone.max[0] < zone.min[0] || zone.max[1] < zone.min[1] || zone.max[2] < zone.min[2])) {
    errors.push(`${path}: zone max must be greater than or equal to min on every axis.`);
  }
}

function validateSpawn(value: unknown, path: string, errors: string[]): void {
  if (value === null || typeof value !== "object" || Array.isArray(value)) {
    errors.push(`${path}: spawn must be a JSON object.`);
    return;
  }

  const spawn = value as Record<string, unknown>;
  validateKeys(spawn, SPAWN_REQUIRED_KEYS, SPAWN_KEYS, path, errors);
  if (!Number.isInteger(spawn.partTypeId) || (spawn.partTypeId as number) < 1) {
    errors.push(`${path}.partTypeId: must be a positive 32-bit integer.`);
  }
  if (!isVec3(spawn.position)) {
    errors.push(`${path}.position: must be an array of three finite numbers.`);
  }
  if (spawn.angle !== undefined && !isFiniteNumber(spawn.angle)) {
    errors.push(`${path}.angle: must be a finite number.`);
  }
  if (spawn.role !== undefined && !LEVEL_ROLES.includes(spawn.role as LevelActorRole)) {
    errors.push(`${path}.role: must be 'part', 'pig' or 'tnt'.`);
  }
  if (spawn.tntFuseTicks !== undefined
    && (!Number.isInteger(spawn.tntFuseTicks) || (spawn.tntFuseTicks as number) < 0 || (spawn.tntFuseTicks as number) > 65535)) {
    errors.push(`${path}.tntFuseTicks: must be a 16-bit non-negative integer.`);
  }
  if (spawn.motorImpulsePerTick !== undefined && !isFiniteNumber(spawn.motorImpulsePerTick)) {
    errors.push(`${path}.motorImpulsePerTick: must be a finite number.`);
  }
  if (spawn.motorDirectionX !== undefined
    && spawn.motorDirectionX !== -1 && spawn.motorDirectionX !== 0 && spawn.motorDirectionX !== 1) {
    errors.push(`${path}.motorDirectionX: must be -1, 0 or 1.`);
  }
  if (spawn.wheel !== undefined && typeof spawn.wheel !== "boolean") {
    errors.push(`${path}.wheel: must be a boolean.`);
  }
}

/**
 * One terrain entry. A v1/v2 document has `{ position, depth, loops }`; a v3 one adds `collider` and
 * a required `fill`, a v4 one a required `curve` as well, and the shapes are not interchangeable
 * (the server parser is version-gated the same way, `LevelContentParser.ParseTerrain`).
 */
function validateTerrain(value: unknown, path: string, modern: boolean, curved: boolean, errors: string[]): void {
  if (value === null || typeof value !== "object" || Array.isArray(value)) {
    errors.push(`${path}: terrain must be a JSON object.`);
    return;
  }

  const terrain = value as Record<string, unknown>;
  const required = curved ? TERRAIN_KEYS : modern ? TERRAIN_V3_REQUIRED_KEYS : TERRAIN_REQUIRED_KEYS;
  validateKeys(terrain, required, TERRAIN_KEYS, path, errors);
  if (modern) {
    if (typeof terrain.collider !== "boolean") {
      errors.push(`${path}.collider: must be a boolean.`);
    }
    validateFill(terrain.fill, `${path}.fill`, errors);
  } else {
    if (TERRAIN_V3_KEYS.some((key) => key in terrain)) {
      errors.push(`${path}: a collider bit and a fill are v3-only.`);
    }
  }
  if (curved) {
    validateCurve(terrain.curve, `${path}.curve`, errors);
  } else if (TERRAIN_V4_KEYS.some((key) => key in terrain)) {
    errors.push(`${path}: a curve is v4-only.`);
  }

  if (!isVec3(terrain.position)) {
    errors.push(`${path}.position: must be an array of three finite numbers.`);
  }
  if (!isFiniteNumber(terrain.depth) || terrain.depth <= 0) {
    errors.push(`${path}.depth: must be a finite positive number.`);
  }
  if (!Array.isArray(terrain.loops)) {
    errors.push(`${path}.loops: must be an array of polygons.`);
    return;
  }
  terrain.loops.forEach((loop, index) => validateLoop(loop, `${path}.loops[${index}]`, errors));
  if (terrain.loops.length === 0) {
    errors.push(`${path}.loops: a terrain needs at least one outline loop.`);
  }
}

/** A v3 `fill` block: a file name, four bytes of tint and two positive tiling pairs. */
function validateFill(value: unknown, path: string, errors: string[]): void {
  if (value === null || typeof value !== "object" || Array.isArray(value)) {
    errors.push(`${path}: must be a JSON object.`);
    return;
  }

  const fill = value as Record<string, unknown>;
  validateKeys(fill, FILL_KEYS, FILL_KEYS, path, errors);
  if (typeof fill.texture !== "string" || fill.texture.length === 0 || fill.texture.length > 128 || fill.texture.trim() !== fill.texture) {
    errors.push(`${path}.texture: must be 1 to 128 non-whitespace-padded characters.`);
  }
  if (!Array.isArray(fill.color) || fill.color.length !== 4 || !fill.color.every(isByte)) {
    errors.push(`${path}.color: must be four bytes [r, g, b, a].`);
  }
  if (!isPair(fill.tileOffset)) {
    errors.push(`${path}.tileOffset: must be [x, y] with finite numbers.`);
  }
  if (!isPair(fill.tileSize) || fill.tileSize[0] <= 0 || fill.tileSize[1] <= 0) {
    errors.push(`${path}.tileSize: must be [w, h] with positive finite numbers.`);
  }
}

/** An integer 0 to 255. */
function isByte(value: unknown): value is number {
  return typeof value === "number" && Number.isInteger(value) && value >= 0 && value <= 255;
}

/**
 * A v4 `curve` block: the strip's two rows, the two `e2d/Curve` layers, the shader's u scale and the
 * node runs the control texture switches to the second layer.
 */
function validateCurve(value: unknown, path: string, errors: string[]): void {
  if (value === null || typeof value !== "object" || Array.isArray(value)) {
    errors.push(`${path}: must be a JSON object.`);
    return;
  }

  const curve = value as Record<string, unknown>;
  validateKeys(curve, CURVE_KEYS, CURVE_KEYS, path, errors);

  const nodes = validateCurveRow(curve.nodes, `${path}.nodes`, errors);
  validateCurveRow(curve.stripe, `${path}.stripe`, errors);
  if (Array.isArray(curve.nodes) && Array.isArray(curve.stripe) && curve.nodes.length !== curve.stripe.length) {
    errors.push(`${path}: nodes and stripe must have the same length.`);
  }

  validateCurveTextures(curve.textures, `${path}.textures`, errors);

  if (!isFiniteNumber(curve.uScale) || curve.uScale <= 0) {
    errors.push(`${path}.uScale: must be a finite positive number.`);
  }

  validateSplat1(curve.splat1, `${path}.splat1`, nodes, errors);
}

/**
 * One curve row (`nodes` / `stripe`): points in the terrain's local frame, at least two of them
 * usable. Returns how many points were valid, which bounds `splat1`'s runs.
 */
function validateCurveRow(value: unknown, path: string, errors: string[]): number | null {
  if (!Array.isArray(value)) {
    errors.push(`${path}: must be an array of [x, y] points.`);
    return null;
  }

  let points = 0;
  value.forEach((point, index) => {
    if (!isPair(point)) {
      errors.push(`${path}[${index}]: a curve point must be [x, y] with finite numbers.`);
      return;
    }
    points += 1;
  });
  if (points < 2) {
    errors.push(`${path}: needs at least two points.`);
  }
  return points;
}

/** The curve's two layers: exactly `[splat0, splat1]`, since the shader samples both by name. */
function validateCurveTextures(value: unknown, path: string, errors: string[]): void {
  if (!Array.isArray(value) || value.length !== 2) {
    errors.push(`${path}: must be exactly two { texture, wrap } layers.`);
    return;
  }

  value.forEach((layer, index) => validateCurveTexture(layer, `${path}[${index}]`, errors));
}

/** One curve layer: a file name (no whitespace, like the v4 schema's `^\S+$`) and a wrap mode. */
function validateCurveTexture(value: unknown, path: string, errors: string[]): void {
  if (value === null || typeof value !== "object" || Array.isArray(value)) {
    errors.push(`${path}: must be a JSON object.`);
    return;
  }

  const layer = value as Record<string, unknown>;
  validateKeys(layer, CURVE_TEXTURE_KEYS, CURVE_TEXTURE_KEYS, path, errors);
  if (typeof layer.texture !== "string" || layer.texture.length === 0 || layer.texture.length > 128
    || /\s/.test(layer.texture)) {
    errors.push(`${path}.texture: must be 1 to 128 characters with no whitespace.`);
  }
  if (!LEVEL_WRAPS.includes(layer.wrap as LevelCurveWrap)) {
    errors.push(`${path}.wrap: must be 'repeat' or 'clamp'.`);
  }
}

/**
 * The `splat1` runs: disjoint node-index ranges, sorted strictly by `start`, each at least one node
 * long and inside the curve. A run is `[start, count]`, and the node it starts at is the one whose
 * segment (to the next node) is drawn with `textures[1]`.
 */
function validateSplat1(value: unknown, path: string, nodeCount: number | null, errors: string[]): void {
  if (!Array.isArray(value)) {
    errors.push(`${path}: must be an array of [start, count] runs.`);
    return;
  }

  let previousStart = -1;
  let previousEnd = 0;
  value.forEach((run, index) => {
    const runPath = `${path}[${index}]`;
    if (!Array.isArray(run) || run.length !== 2) {
      errors.push(`${runPath}: a run must be [start, count].`);
      return;
    }
    const [start, count] = run as [number, number];
    if (!Number.isInteger(start) || start < 0 || !Number.isInteger(count) || count < 1) {
      errors.push(`${runPath}: start must be a non-negative integer and count a positive one.`);
      return;
    }
    if (start <= previousStart) {
      errors.push(`${runPath}: runs must be sorted strictly by start.`);
    } else if (start < previousEnd) {
      errors.push(`${runPath}: runs must not overlap.`);
    }
    if (nodeCount !== null && start + count > nodeCount) {
      errors.push(`${runPath}: must stay within the ${nodeCount} curve nodes.`);
    }
    previousStart = start;
    previousEnd = start + count;
  });
}

/** A two-element pair of finite numbers. */
function isPair(value: unknown): value is [number, number] {
  return Array.isArray(value) && value.length === 2 && value.every(isFiniteNumber);
}

/** One outline loop: at least three `[x, y]` points, each finite (the server's own count rule). */
function validateLoop(value: unknown, path: string, errors: string[]): void {
  if (!Array.isArray(value)) {
    errors.push(`${path}: an outline loop must be an array of points.`);
    return;
  }

  let points = 0;
  value.forEach((point, index) => {
    if (!Array.isArray(point) || point.length !== 2 || !point.every(isFiniteNumber)) {
      errors.push(`${path}[${index}]: an outline point must be [x, y] with finite numbers.`);
      return;
    }
    points += 1;
  });
  if (points < 3) {
    errors.push(`${path}: an outline loop needs at least three points.`);
  }
}
