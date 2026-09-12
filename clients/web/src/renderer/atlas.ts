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
}

export interface PartTextureSet {
  atlases: ReadonlyMap<string, CanvasImageSource>;
  parts: ReadonlyMap<number, PartTexture>;
}

export const PART_TEXTURE_URL = "/assets/original/part-textures.json";

function finite(value: unknown, what: string): number {
  if (typeof value !== "number" || !Number.isFinite(value)) throw new Error(`part-textures: ${what} is not a finite number`);
  return value;
}

/** Validates a manifest document. Throws on malformed data; the loader turns that into a fallback. */
export function parsePartTextures(value: unknown): Map<number, PartTexture> {
  if (typeof value !== "object" || value === null) throw new Error("part-textures: not an object");
  const document = value as { format?: unknown; schemaVersion?: unknown; parts?: unknown };
  if (document.format !== "pigforge.part-textures") throw new Error("part-textures: unknown format");
  if (document.schemaVersion !== 2) throw new Error("part-textures: unsupported schemaVersion");
  if (typeof document.parts !== "object" || document.parts === null) throw new Error("part-textures: missing parts");
  const parts = new Map<number, PartTexture>();
  for (const [key, raw] of Object.entries(document.parts as Record<string, unknown>)) {
    const partTypeId = Number(key);
    if (!Number.isInteger(partTypeId) || partTypeId <= 0) throw new Error(`part-textures: bad partTypeId ${key}`);
    const entry = raw as { bbox?: unknown; sprites?: unknown; pivot?: unknown };
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
      return {
        atlas: sprite.atlas,
        ...rect,
        cx: finite(sprite.cx, `part ${key} sprite ${index} cx`),
        cy: finite(sprite.cy, `part ${key} sprite ${index} cy`),
        sx: finite(sprite.sx, `part ${key} sprite ${index} sx`),
        sy: finite(sprite.sy, `part ${key} sprite ${index} sy`),
        rot: finite(sprite.rot ?? 0, `part ${key} sprite ${index} rot`),
        rotates: sprite.rotates === true,
      };
    });
    const pivot =
      Array.isArray(entry.pivot) && entry.pivot.length === 2
        ? ([finite(entry.pivot[0], `part ${key} pivot x`), finite(entry.pivot[1], `part ${key} pivot y`)] as [number, number])
        : undefined;
    parts.set(partTypeId, { bbox, sprites, ...(pivot ? { pivot } : {}) });
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
      for (const sprite of texture.sprites) names.add(sprite.atlas);
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
