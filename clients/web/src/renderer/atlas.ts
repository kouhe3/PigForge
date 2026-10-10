/**
 * Optional original-art sprite layer.
 *
 * Textures are extracted from the BPLE Unity project by `tools/bple-textures/extract.mjs`
 * into `public/assets/original/` (gitignored: the art is Rovio-copyrighted). The manifest
 * maps PigForge partTypeId to atlas pixel rects plus the part-local placement, so the
 * renderer only needs to blit. A missing manifest or atlas is not an error: parts fall
 * back to the procedural shape rendering.
 */

/** A part-local connection side. The client rotates it into the grid with the entity's yaw. */
export type LocalSide = "top" | "bottom" | "left" | "right" | "topLeft" | "topRight" | "bottomLeft" | "bottomRight";

/**
 * What a conditional sprite stands for (manifest v4): one of the original's `*Attachment`
 * side markers, or one of a wing's two frame mounts. `connectionVisuals.ts` turns the
 * neighbouring layout into the visibility of each.
 */
export type SpriteCondition =
  | { kind: "attachment"; side: LocalSide }
  | { kind: "frame"; mount: "top" | "bottom" };

/**
 * The rule a part's conditional sprites obey, from the host script the original mounts:
 * Rocket/`*Bottle` (fallback bottom), TNT (no fallback), SpotLight/GrapplingHook (eight sides),
 * Wings (two mounts). A part without one draws no conditional sprite.
 */
export type ConnectionVisual = "attachmentFallback" | "attachmentPlain" | "attachmentEight" | "frame";

export interface PartSprite {
  /** Atlas file name, resolved next to the manifest. */
  atlas: string;
  /** Source rect in atlas pixels (top-left origin, as canvas expects). */
  x: number;
  y: number;
  w: number;
  h: number;
  /** Sprite centre relative to the part origin, world units, +y up (BPLE convention). */
  cx: number;
  cy: number;
  /** Sprite size in world units, exactly as the original game draws it. */
  sx: number;
  sy: number;
  /** Local rotation in radians, counter-clockwise in the +y-up frame. */
  rot: number;
  /**
   * True when the sprite hangs off a node the original drives at runtime (a wheel pivot,
   * a fan/rotor/propeller visualization): it turns with the part's spin. False sprites —
   * a wheel's axle, which sits on the prefab root — keep the part's own orientation.
   * Additive to `schemaVersion` 2; the v3 animation descriptors are unaffected.
   */
  rotates: boolean;
  /**
   * Present on the blades of a fan, propeller or rotor (v3): the original compresses the
   * sprite's scale by `|cos(angle)|` instead of rotating it. Wheels carry no such
   * descriptor — their sprites ride a hinged body whose snapshot rotation is the roll.
   */
  spin?: SpinDescriptor;
  /** Present on sprites whose art the original swaps frame by frame (v3, the pig's face). */
  clips?: SpriteClips;
  /**
   * Present on the sprites the original shows conditionally (v4): the `*Attachment` markers
   * and a wing's two frame mounts. Their visibility follows the neighbouring layout.
   */
  condition?: SpriteCondition;
  /** Present when the original mirrors the sprite through a negative node scale (v4). */
  flipX?: boolean;
  flipY?: boolean;
}

/** The axis a part's blades turn about. */
export type SpinAxis = "x" | "y";

/**
 * Spin descriptor of a fan-like sprite. The original accumulates an angle while the part's
 * switch is on and writes `scale = |cos(angle)|` on the compression axis every frame
 * (FanPropeller.cs:120-142, 319-324); the sprite itself never rotates.
 */
export interface SpinDescriptor {
  /** "x" compresses the sprite's height (fan, plane propeller); "y" its width (rotor). */
  axis: SpinAxis;
  /** FanPropeller.cs:92,108-112 with `powerFactor` 1: `1000 * powerFactor + 700`. */
  maxDegreesPerSecond: number;
}

/**
 * One frame of a clip: a complete sprite descriptor plus how long it is shown. The original
 * rebuilds the sprite's mesh from the frame's sprite-database row while keeping the node's
 * own scales and pivots (`SpriteAnimation.InitializeAnimations` -> `Sprite.SelectSprite`),
 * so a frame carries placement as well as a source rect.
 */
export interface AnimationFrame {
  atlas: string;
  x: number;
  y: number;
  w: number;
  h: number;
  cx: number;
  cy: number;
  sx: number;
  sy: number;
  rot: number;
  /** Frame duration in seconds (`FrameTiming.time`). */
  seconds: number;
}

export interface SpriteClip {
  /** Loop forever, or stop on the last frame — the original's `Animation.loop`. */
  loop: boolean;
  frames: AnimationFrame[];
}

/** Named clips of one sprite; every sprite of a part plays the same name in lockstep. */
export type SpriteClips = Record<string, SpriteClip>;

/**
 * Pig and king-pig expression thresholds (Pig.cs:277-438). The speed bands are ratios of
 * `vRef` — the speed a body would reach after a second of full motor thrust — because
 * PigForge has no original motor speed limit to compare absolute m/s against (spec
 * "阈值标定（实测）"): the original's 8/14 m/s are crossed within a tenth of a second here.
 */
export interface ExpressionDescriptor {
  /** Speed measure above this ratio of `vRef` plays `Grin`. */
  speedFunRatio: number;
  /** ... plays `FearfulGrin`. */
  speedFearfulRatio: number;
  /** ... plays `Fear_1`. */
  speedFearRatio: number;
  /** vRef fallback for a body with no active motor (glider, rocket, free fall), m/s. */
  speedReference: number;
  /** `|Δ|v||` above this plays `Hit` for a second (Pig.cs:313-319), m/s. */
  hitDeltaV: number;
  /** `-vy` above this plays `Fear_2` (Pig.cs:428-431), m/s. */
  fallFearThreshold: number;
}

/**
 * One leg of an activation cross-fade: a sprite's alpha ramps linearly from `from` to `to` over
 * `seconds`, beginning `start` seconds after the part's switch edge. A leg that has not started
 * contributes nothing and one that has run out holds its `to` value, so later legs of the same
 * sprite overwrite earlier ones -- the order the original writes its `material.color` in
 * (`Rocket.Update`, Rocket.cs:202-226).
 */
export interface ActivationFade {
  sprite: number;
  from: number;
  to: number;
  start: number;
  seconds: number;
}

/**
 * The ignition jitter: every listed sprite belongs to one node in the original (the bottle's
 * `BottleVisualization`), so they all take the same random offset inside a circle of `radius`
 * world units while the run is younger than `seconds` (`Rocket.FixedUpdate` writes
 * `Random.insideUnitCircle * 0.1` on it, Rocket.cs:238-240, and zeroes it at the ignition's end).
 */
export interface ActivationJitter {
  sprites: number[];
  radius: number;
  seconds: number;
}

/**
 * The cork's launch: at `start` seconds the cork leaves the part along its own -X at `speed` m/s,
 * spinning `spinDegreesPerSecond`, and its own prefab object is destroyed after `lifetime`
 * seconds (`Rocket.FixedUpdate` calls `Cork.Fly(-20 * transform.right, 200, 0.75)`,
 * Rocket.cs:255-262; `Cork.Update` integrates the flight, Cork.cs:15-33).
 */
export interface ActivationLaunch {
  sprite: number;
  start: number;
  speed: number;
  spinDegreesPerSecond: number;
  lifetime: number;
}

/**
 * The blaster's expanding ring (`BlasterTNT.cs:123-169, 219-227`): a standalone image -- the node
 * carries a quad with its own material, not a sprite -- drawn at the blast centre, scaled to
 * `2 * radius` world units, with an alpha of `min(alphaNumerator / radius^2, alphaCap)`. The
 * radius starts at `startRadius` and grows at `radiusVelocity` m/s against `radiusDrag`, stepped
 * in fixed `stepSeconds` increments whoever the frame rate is.
 */
export interface ActivationRing {
  atlas: string;
  x: number;
  y: number;
  w: number;
  h: number;
  startRadius: number;
  radiusVelocity: number;
  radiusDrag: number;
  stepSeconds: number;
  alphaNumerator: number;
  alphaCap: number;
}

/**
 * What the original's own script does to a placed part's art between its switch edge and the end
 * of the activation (v6): the bottle family's ignition jitter, content cross-fade and cork
 * launch, or the blaster's expanding ring. `seconds` is how long the whole run lasts -- the
 * longest channel, after which the client drops it. Absent on every part whose art does not
 * animate on activation, which is all but the eleven bottle/blaster parts.
 */
export interface ActivationDescriptor {
  seconds: number;
  jitter?: ActivationJitter;
  fade?: ActivationFade[];
  launch?: ActivationLaunch;
  ring?: ActivationRing;
}

export interface PartTexture {
  /** Composite bounds in world units; validated as a manifest sanity check. */
  bbox: [number, number];
  sprites: PartSprite[];
  /**
   * The axis the part's rotating sprites turn about, in the same frame as `cx`/`cy`
   * (relative to the composite's layout anchor). Absent for parts that never spin.
   */
  pivot?: [number, number];
  /** Present on the pigs: the part runs the expression state machine (v3). */
  expression?: ExpressionDescriptor;
  /** Present on parts whose sprites the original shows per connection side (v4). */
  connectionVisual?: ConnectionVisual;
  /**
   * Art of the part's runtime sub-entity (v5): the prefab the part instantiates at run time --
   * a boxing glove's fist (`SpringBoxingGlove.m_BoxingGlovePrefab`) -- in the same frame as
   * `sprites` (relative to the sub-entity's own body origin). Drawn for the snapshot entity the
   * sub-entity flag marks; absent for parts with no sub-entity prefab.
   */
  subSprites?: PartSprite[];
  /** Present on the parts whose art animates when their switch fires (v6). */
  activation?: ActivationDescriptor;
}

export interface PartTextureSet {
  atlases: ReadonlyMap<string, CanvasImageSource>;
  parts: ReadonlyMap<number, PartTexture>;
}

export const PART_TEXTURE_URL = "/assets/original/part-textures.json";

/** Manifest versions this parser understands: 2 (static), 3 (adds animation), 4 (adds conditions),
 * 5 (adds the sub-entity prefab's art), 6 (adds the activation-time animation). */
const SUPPORTED_SCHEMA_VERSIONS = [2, 3, 4, 5, 6];

const LOCAL_SIDES: Record<LocalSide, true> = {
  top: true,
  bottom: true,
  left: true,
  right: true,
  topLeft: true,
  topRight: true,
  bottomLeft: true,
  bottomRight: true,
};
const CONNECTION_VISUALS: Record<ConnectionVisual, true> = {
  attachmentFallback: true,
  attachmentPlain: true,
  attachmentEight: true,
  frame: true,
};

function isLocalSide(value: unknown): value is LocalSide {
  return typeof value === "string" && Object.hasOwn(LOCAL_SIDES, value);
}

function isConnectionVisual(value: unknown): value is ConnectionVisual {
  return typeof value === "string" && Object.hasOwn(CONNECTION_VISUALS, value);
}

function finite(value: unknown, what: string): number {
  if (typeof value !== "number" || !Number.isFinite(value)) throw new Error(`part-textures: ${what} is not a finite number`);
  return value;
}

function positive(value: unknown, what: string): number {
  const result = finite(value, what);
  if (!(result > 0)) throw new Error(`part-textures: ${what} is not positive`);
  return result;
}

function spinOf(value: unknown, what: string): SpinDescriptor {
  const spin = value as { axis?: unknown; maxDegreesPerSecond?: unknown };
  if (spin.axis !== "x" && spin.axis !== "y") throw new Error(`part-textures: ${what} spin axis`);
  return { axis: spin.axis, maxDegreesPerSecond: positive(spin.maxDegreesPerSecond, `${what} spin speed`) };
}

function clipsOf(value: unknown, what: string): SpriteClips {
  if (typeof value !== "object" || value === null || Array.isArray(value)) throw new Error(`part-textures: ${what} clips`);
  const clips: SpriteClips = {};
  for (const [name, raw] of Object.entries(value as Record<string, unknown>)) {
    if (name.length === 0) throw new Error(`part-textures: ${what} clip name`);
    const clip = raw as { loop?: unknown; frames?: unknown };
    if (typeof clip.loop !== "boolean") throw new Error(`part-textures: ${what} clip ${name} loop`);
    if (!Array.isArray(clip.frames) || clip.frames.length === 0) throw new Error(`part-textures: ${what} clip ${name} has no frames`);
    clips[name] = {
      loop: clip.loop,
      frames: clip.frames.map((rawFrame, index): AnimationFrame => {
        const frame = rawFrame as Record<string, unknown>;
        const source = `part-textures: ${what} clip ${name} frame ${index}`;
        if (typeof frame.atlas !== "string" || frame.atlas.length === 0) throw new Error(`${source} atlas`);
        const rect = { x: finite(frame.x, `${source} x`), y: finite(frame.y, `${source} y`), w: finite(frame.w, `${source} w`), h: finite(frame.h, `${source} h`) };
        if (!(rect.w > 0 && rect.h > 0)) throw new Error(`${source} rect not positive`);
        return {
          atlas: frame.atlas,
          ...rect,
          cx: finite(frame.cx, `${source} cx`),
          cy: finite(frame.cy, `${source} cy`),
          sx: finite(frame.sx, `${source} sx`),
          sy: finite(frame.sy, `${source} sy`),
          rot: finite(frame.rot ?? 0, `${source} rot`),
          seconds: positive(frame.seconds, `${source} seconds`),
        };
      }),
    };
  }
  return clips;
}

function expressionOf(value: unknown, what: string): ExpressionDescriptor {
  const expression = value as Record<string, unknown>;
  return {
    speedFunRatio: positive(expression.speedFunRatio, `${what} speedFunRatio`),
    speedFearfulRatio: positive(expression.speedFearfulRatio, `${what} speedFearfulRatio`),
    speedFearRatio: positive(expression.speedFearRatio, `${what} speedFearRatio`),
    speedReference: positive(expression.speedReference, `${what} speedReference`),
    hitDeltaV: positive(expression.hitDeltaV, `${what} hitDeltaV`),
    fallFearThreshold: positive(expression.fallFearThreshold, `${what} fallFearThreshold`),
  };
}

function conditionOf(value: unknown, what: string): SpriteCondition {
  const condition = value as { kind?: unknown; side?: unknown; mount?: unknown };
  if (condition.kind === "attachment") {
    if (!isLocalSide(condition.side)) throw new Error(`part-textures: ${what} condition side`);
    return { kind: "attachment", side: condition.side };
  }
  if (condition.kind === "frame") {
    if (condition.mount !== "top" && condition.mount !== "bottom") {
      throw new Error(`part-textures: ${what} condition mount`);
    }
    return { kind: "frame", mount: condition.mount };
  }
  throw new Error(`part-textures: ${what} condition kind`);
}

/** Non-negative integer index into the part's own sprite array. */
function spriteIndex(value: unknown, what: string): number {
  const index = finite(value, what);
  if (!Number.isInteger(index) || index < 0) throw new Error(`part-textures: ${what} is not a sprite index`);
  return index;
}

function activationOf(value: unknown, what: string): ActivationDescriptor {
  const activation = value as Record<string, unknown>;
  if (typeof activation !== "object" || activation === null) throw new Error(`part-textures: ${what} activation`);
  const descriptor: ActivationDescriptor = { seconds: positive(activation.seconds, `${what} activation seconds`) };
  if (activation.jitter !== undefined) {
    const jitter = activation.jitter as Record<string, unknown>;
    if (!Array.isArray(jitter.sprites) || jitter.sprites.length === 0) {
      throw new Error(`part-textures: ${what} activation jitter sprites`);
    }
    descriptor.jitter = {
      sprites: jitter.sprites.map((sprite, index) => spriteIndex(sprite, `${what} activation jitter sprite ${index}`)),
      radius: positive(jitter.radius, `${what} activation jitter radius`),
      seconds: positive(jitter.seconds, `${what} activation jitter seconds`),
    };
  }
  if (activation.fade !== undefined) {
    if (!Array.isArray(activation.fade) || activation.fade.length === 0) {
      throw new Error(`part-textures: ${what} activation fade`);
    }
    descriptor.fade = activation.fade.map((rawLeg, index): ActivationFade => {
      const leg = rawLeg as Record<string, unknown>;
      const source = `${what} activation fade ${index}`;
      const from = finite(leg.from, `${source} from`);
      const to = finite(leg.to, `${source} to`);
      if (from < 0 || from > 1 || to < 0 || to > 1) throw new Error(`part-textures: ${source} alpha`);
      return {
        sprite: spriteIndex(leg.sprite, `${source} sprite`),
        from,
        to,
        start: finite(leg.start, `${source} start`),
        seconds: positive(leg.seconds, `${source} seconds`),
      };
    });
  }
  if (activation.launch !== undefined) {
    const launch = activation.launch as Record<string, unknown>;
    descriptor.launch = {
      sprite: spriteIndex(launch.sprite, `${what} activation launch sprite`),
      start: finite(launch.start, `${what} activation launch start`),
      speed: positive(launch.speed, `${what} activation launch speed`),
      spinDegreesPerSecond: finite(launch.spinDegreesPerSecond, `${what} activation launch spin`),
      lifetime: positive(launch.lifetime, `${what} activation launch lifetime`),
    };
  }
  if (activation.ring !== undefined) {
    const ring = activation.ring as Record<string, unknown>;
    const source = `${what} activation ring`;
    if (typeof ring.atlas !== "string" || ring.atlas.length === 0) throw new Error(`part-textures: ${source} atlas`);
    const rect = {
      x: finite(ring.x, `${source} x`),
      y: finite(ring.y, `${source} y`),
      w: positive(ring.w, `${source} w`),
      h: positive(ring.h, `${source} h`),
    };
    descriptor.ring = {
      atlas: ring.atlas,
      ...rect,
      startRadius: positive(ring.startRadius, `${source} startRadius`),
      radiusVelocity: positive(ring.radiusVelocity, `${source} radiusVelocity`),
      radiusDrag: positive(ring.radiusDrag, `${source} radiusDrag`),
      stepSeconds: positive(ring.stepSeconds, `${source} stepSeconds`),
      alphaNumerator: positive(ring.alphaNumerator, `${source} alphaNumerator`),
      alphaCap: positive(ring.alphaCap, `${source} alphaCap`),
    };
  }
  if (descriptor.jitter === undefined && descriptor.fade === undefined && descriptor.launch === undefined && descriptor.ring === undefined) {
    throw new Error(`part-textures: ${what} activation has no channel`);
  }
  return descriptor;
}

/** Validates a manifest document. Throws on malformed data; the loader turns that into a fallback. */
export function parsePartTextures(value: unknown): Map<number, PartTexture> {
  if (typeof value !== "object" || value === null) throw new Error("part-textures: not an object");
  const document = value as { format?: unknown; schemaVersion?: unknown; parts?: unknown };
  if (document.format !== "pigforge.part-textures") throw new Error("part-textures: unknown format");
  // A v2 manifest is a v3 one that describes no animation: the new fields are all optional.
  if (!SUPPORTED_SCHEMA_VERSIONS.includes(document.schemaVersion as number)) {
    throw new Error("part-textures: unsupported schemaVersion");
  }
  if (typeof document.parts !== "object" || document.parts === null) throw new Error("part-textures: missing parts");
  const parts = new Map<number, PartTexture>();
  for (const [key, raw] of Object.entries(document.parts as Record<string, unknown>)) {
    const partTypeId = Number(key);
    if (!Number.isInteger(partTypeId) || partTypeId <= 0) throw new Error(`part-textures: bad partTypeId ${key}`);
    const entry = raw as {
      bbox?: unknown;
      sprites?: unknown;
      subSprites?: unknown;
      pivot?: unknown;
      expression?: unknown;
      connectionVisual?: unknown;
      activation?: unknown;
    };
    if (!Array.isArray(entry.bbox) || entry.bbox.length !== 2) throw new Error(`part-textures: part ${key} bbox`);
    const bbox: [number, number] = [finite(entry.bbox[0], `part ${key} bbox width`), finite(entry.bbox[1], `part ${key} bbox height`)];
    if (!(bbox[0] > 0 && bbox[1] > 0)) throw new Error(`part-textures: part ${key} bbox not positive`);
    const sprites = spriteListOf(entry.sprites, `part ${key}`, `part ${key} has no sprites`);
    const subSprites = entry.subSprites === undefined
      ? undefined
      : spriteListOf(entry.subSprites, `part ${key} sub-entity`, `part ${key} has no sub-entity sprites`);
    const pivot =
      Array.isArray(entry.pivot) && entry.pivot.length === 2
        ? ([finite(entry.pivot[0], `part ${key} pivot x`), finite(entry.pivot[1], `part ${key} pivot y`)] as [number, number])
        : undefined;
    const expression = entry.expression === undefined ? undefined : expressionOf(entry.expression, `part ${key}`);
    const activation = entry.activation === undefined ? undefined : activationOf(entry.activation, `part ${key}`);
    const entryVisual = entry.connectionVisual;
    if (entryVisual !== undefined && !isConnectionVisual(entryVisual)) {
      throw new Error(`part-textures: part ${key} connectionVisual`);
    }
    parts.set(partTypeId, {
      bbox,
      sprites,
      ...(pivot ? { pivot } : {}),
      ...(expression ? { expression } : {}),
      ...(entryVisual === undefined ? {} : { connectionVisual: entryVisual }),
      ...(activation === undefined ? {} : { activation }),
      ...(subSprites === undefined ? {} : { subSprites }),
    });
  }
  return parts;
}

/** Parses one sprite array of a manifest part; `missingMessage` is how an absent list reads. */
function spriteListOf(value: unknown, what: string, missingMessage: string): PartSprite[] {
  if (!Array.isArray(value) || value.length === 0) throw new Error(`part-textures: ${missingMessage}`);
  return value.map((rawSprite, index): PartSprite => {
    const sprite = rawSprite as Record<string, unknown>;
    const where = `${what} sprite ${index}`;
    if (typeof sprite.atlas !== "string" || sprite.atlas.length === 0) throw new Error(`part-textures: ${where} atlas`);
    const rect = {
      x: finite(sprite.x, `${where} x`),
      y: finite(sprite.y, `${where} y`),
      w: finite(sprite.w, `${where} w`),
      h: finite(sprite.h, `${where} h`),
    };
    if (!(rect.w > 0 && rect.h > 0)) throw new Error(`part-textures: ${where} rect not positive`);
    return {
      atlas: sprite.atlas,
      ...rect,
      cx: finite(sprite.cx, `${where} cx`),
      cy: finite(sprite.cy, `${where} cy`),
      sx: finite(sprite.sx, `${where} sx`),
      sy: finite(sprite.sy, `${where} sy`),
      rot: finite(sprite.rot ?? 0, `${where} rot`),
      rotates: sprite.rotates === true,
      ...(sprite.flipX === true ? { flipX: true } : {}),
      ...(sprite.flipY === true ? { flipY: true } : {}),
      ...(sprite.condition === undefined ? {} : { condition: conditionOf(sprite.condition, where) }),
      ...(sprite.spin === undefined ? {} : { spin: spinOf(sprite.spin, where) }),
      ...(sprite.clips === undefined ? {} : { clips: clipsOf(sprite.clips, where) }),
    };
  });
}

export interface SpritePlacement {
  sprite: PartSprite;
  /** Placement centre relative to the part origin, world units, +y up. */
  x: number;
  y: number;
  /** Placement size in world units. */
  w: number;
  h: number;
}

/**
 * Places a part's sprite composite at the original game's world size and offsets,
 * scaled by the entity's build-time scale. Pure math so the renderer stays a thin
 * blit loop.
 */
export function layoutSprites(texture: PartTexture, scale: number): SpritePlacement[] {
  return texture.sprites.map((sprite) => ({
    sprite,
    x: sprite.cx * scale,
    y: sprite.cy * scale,
    w: sprite.sx * scale,
    h: sprite.sy * scale,
  }));
}

/**
 * The art one entity draws. A runtime sub-entity (snapshot flags bit1) borrows its host part's
 * type on the wire, so its host's composite would be drawn over it -- a boxing glove's fist would
 * look like a second glove part. The manifest's own sub-entity list replaces it; nothing else of
 * the host's texture applies, since a sub-entity never has mounts, an axle or clip art. A part
 * with no extracted sub-entity prefab (a broken spring's endpoint) keeps its host's art.
 */
export function subEntityTexture(texture: PartTexture | undefined, subEntity: boolean): PartTexture | undefined {
  if (!subEntity || texture?.subSprites === undefined) {
    return texture;
  }

  // Only `sprites` is read for a sub-entity; the composite's own bounds stay unread.
  return { bbox: texture.bbox, sprites: texture.subSprites };
}

export type ImageLoader = (url: string) => Promise<CanvasImageSource>;

async function loadImageElement(url: string): Promise<CanvasImageSource> {
  const response = await fetch(url);
  if (!response.ok) throw new Error(`part-textures: failed to load ${url}`);
  return createImageBitmap(await response.blob());
}

/** Loads the manifest and every atlas it references. Returns null when absent or malformed. */
export async function loadPartTextures(
  url: string = PART_TEXTURE_URL,
  loadImage: ImageLoader = loadImageElement,
  cache: Map<string, CanvasImageSource> = new Map(),
): Promise<PartTextureSet | null> {
  try {
    const response = await fetch(url);
    if (!response.ok) return null;
    const parts = parsePartTextures(await response.json());
    const base = url.slice(0, url.lastIndexOf("/") + 1);
    const atlases = new Map<string, CanvasImageSource>();
    const names = new Set<string>();
    for (const texture of parts.values()) {
      for (const sprite of texture.sprites) {
        names.add(sprite.atlas);
        // A frame may be packed in another atlas than the sprite's own first frame.
        for (const clip of Object.values(sprite.clips ?? {})) {
          for (const frame of clip.frames) names.add(frame.atlas);
        }
      }
      // The blaster's ring is a quad with a standalone image, not a sprite of any atlas
      // (manifest v6): it ships as its own file and is loaded alongside the atlases.
      if (texture.activation?.ring) names.add(texture.activation.ring.atlas);
    }
    await Promise.all(
      [...names].map(async (name) => {
        // A caller may share its images with another manifest (the level's props draw from the same
        // two atlases the parts use): an atlas is 16 MB decoded, so it is loaded once per name.
        const existing = cache.get(name);
        const source = existing ?? (await loadImage(`${base}${name}`));
        cache.set(name, source);
        atlases.set(name, source);
      }),
    );
    return { atlases, parts };
  } catch {
    return null;
  }
}
