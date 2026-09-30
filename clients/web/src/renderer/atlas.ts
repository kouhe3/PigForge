/**
 * Optional original-art sprite layer.
 *
 * Textures are extracted from the BPLE Unity project by `tools/bple-textures/extract.mjs`
 * into `public/assets/original/` (gitignored: the art is Rovio-copyrighted). The manifest
 * maps PigForge partTypeId to atlas pixel rects plus the part-local placement, so the
 * renderer only needs to blit. A missing manifest or atlas is not an error: parts fall
 * back to the procedural shape rendering.
 */

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
}

export interface PartTextureSet {
  atlases: ReadonlyMap<string, CanvasImageSource>;
  parts: ReadonlyMap<number, PartTexture>;
}

export const PART_TEXTURE_URL = "/assets/original/part-textures.json";

/** Manifest versions this parser understands: 2 (static sprites) and 3 (adds animation). */
const SUPPORTED_SCHEMA_VERSIONS = [2, 3];

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
    const entry = raw as { bbox?: unknown; sprites?: unknown; pivot?: unknown; expression?: unknown };
    if (!Array.isArray(entry.bbox) || entry.bbox.length !== 2) throw new Error(`part-textures: part ${key} bbox`);
    const bbox: [number, number] = [finite(entry.bbox[0], `part ${key} bbox width`), finite(entry.bbox[1], `part ${key} bbox height`)];
    if (!(bbox[0] > 0 && bbox[1] > 0)) throw new Error(`part-textures: part ${key} bbox not positive`);
    if (!Array.isArray(entry.sprites) || entry.sprites.length === 0) throw new Error(`part-textures: part ${key} has no sprites`);
    const sprites = entry.sprites.map((rawSprite, index): PartSprite => {
      const sprite = rawSprite as Record<string, unknown>;
      if (typeof sprite.atlas !== "string" || sprite.atlas.length === 0) throw new Error(`part-textures: part ${key} sprite ${index} atlas`);
      const rect = {
        x: finite(sprite.x, `part ${key} sprite ${index} x`),
        y: finite(sprite.y, `part ${key} sprite ${index} y`),
        w: finite(sprite.w, `part ${key} sprite ${index} w`),
        h: finite(sprite.h, `part ${key} sprite ${index} h`),
      };
      if (!(rect.w > 0 && rect.h > 0)) throw new Error(`part-textures: part ${key} sprite ${index} rect not positive`);
      const what = `part ${key} sprite ${index}`;
      return {
        atlas: sprite.atlas,
        ...rect,
        cx: finite(sprite.cx, `part ${key} sprite ${index} cx`),
        cy: finite(sprite.cy, `part ${key} sprite ${index} cy`),
        sx: finite(sprite.sx, `part ${key} sprite ${index} sx`),
        sy: finite(sprite.sy, `part ${key} sprite ${index} sy`),
        rot: finite(sprite.rot ?? 0, `part ${key} sprite ${index} rot`),
        rotates: sprite.rotates === true,
        ...(sprite.spin === undefined ? {} : { spin: spinOf(sprite.spin, what) }),
        ...(sprite.clips === undefined ? {} : { clips: clipsOf(sprite.clips, what) }),
      };
    });
    const pivot =
      Array.isArray(entry.pivot) && entry.pivot.length === 2
        ? ([finite(entry.pivot[0], `part ${key} pivot x`), finite(entry.pivot[1], `part ${key} pivot y`)] as [number, number])
        : undefined;
    const expression = entry.expression === undefined ? undefined : expressionOf(entry.expression, `part ${key}`);
    parts.set(partTypeId, { bbox, sprites, ...(pivot ? { pivot } : {}), ...(expression ? { expression } : {}) });
  }
  return parts;
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
    }
    await Promise.all(
      [...names].map(async (name) => {
        atlases.set(name, await loadImage(`${base}${name}`));
      }),
    );
    return { atlases, parts };
  } catch {
    return null;
  }
}
