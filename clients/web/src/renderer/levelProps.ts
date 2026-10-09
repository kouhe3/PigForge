import type { LevelProp } from "@/schema/levelContent";
import type { ImageLoader } from "./atlas";
import { type Camera, worldToScreen } from "./camera";

/**
 * One prop's art, from the client's own `level-props.json` (written by
 * `tools/bple-props/extract-props.mjs` out of the original's prefabs): the atlas file, the source
 * rectangle inside it in pixels, and the quad's own frame in world metres -- `cx`/`cy` are the quad
 * centre's offset from the instance's origin (the original's `Sprite` pivot; zero for an
 * `UnmanagedSprite`) and `sx`/`sy` are its size. See `docs/specs/level-props.md` §3.
 */
export interface LevelPropArt {
  atlas: string;
  x: number;
  y: number;
  w: number;
  h: number;
  cx: number;
  cy: number;
  sx: number;
  sy: number;
}

/** The prop manifest and the atlas images it draws from. */
export interface LevelPropsSet {
  atlases: ReadonlyMap<string, CanvasImageSource>;
  props: ReadonlyMap<string, LevelPropArt>;
}

export const LEVEL_PROPS_URL = "/assets/original/level-props.json";

/** Manifest versions this parser understands: 1 (the decoration quads). */
const SUPPORTED_SCHEMA_VERSIONS = [1];

const ART_KEYS = ["atlas", "x", "y", "w", "h", "cx", "cy", "sx", "sy"] as const;

function isFiniteNumber(value: unknown): value is number {
  return typeof value === "number" && Number.isFinite(value);
}

function readArt(value: unknown): LevelPropArt | null {
  if (value === null || typeof value !== "object" || Array.isArray(value)) {
    return null;
  }

  const art = value as Record<string, unknown>;
  if (typeof art.atlas !== "string" || art.atlas.length === 0) {
    return null;
  }
  for (const key of ART_KEYS) {
    if (key !== "atlas" && !isFiniteNumber(art[key])) {
      return null;
    }
  }
  if (!(art.w as number > 0) || !(art.h as number > 0) || !(art.sx as number > 0) || !(art.sy as number > 0)) {
    return null;
  }
  return {
    atlas: art.atlas,
    x: art.x as number,
    y: art.y as number,
    w: art.w as number,
    h: art.h as number,
    cx: art.cx as number,
    cy: art.cy as number,
    sx: art.sx as number,
    sy: art.sy as number,
  };
}

/**
 * Parses a `level-props.json` value, returning null for anything the renderer cannot trust. The
 * manifest is generated, so the parse is a gate rather than a repair: a malformed entry is dropped
 * with the rest of the document (the painter then draws the level without its dressing, the same
 * fallback the ground and part atlas loaders take).
 */
export function parseLevelProps(value: unknown): { atlasNames: string[]; props: Map<string, LevelPropArt> } | null {
  if (value === null || typeof value !== "object" || Array.isArray(value)) {
    return null;
  }

  const manifest = value as Record<string, unknown>;
  if (manifest.format !== "pigforge.level-props") {
    return null;
  }
  if (typeof manifest.schemaVersion !== "number" || !SUPPORTED_SCHEMA_VERSIONS.includes(manifest.schemaVersion)) {
    return null;
  }
  if (manifest.atlases === null || typeof manifest.atlases !== "object" || Array.isArray(manifest.atlases)) {
    return null;
  }
  if (manifest.props === null || typeof manifest.props !== "object" || Array.isArray(manifest.props)) {
    return null;
  }

  const props = new Map<string, LevelPropArt>();
  for (const [id, art] of Object.entries(manifest.props as Record<string, unknown>)) {
    const read = readArt(art);
    if (read !== null) {
      props.set(id, read);
    }
  }
  if (props.size === 0) {
    return null;
  }

  // Every art's atlas must be declared, so a load of the manifest is enough to know what to fetch.
  const atlasNames = new Set<string>();
  for (const art of props.values()) {
    atlasNames.add(art.atlas);
  }
  const declared = new Set(Object.keys(manifest.atlases as Record<string, unknown>));
  for (const name of atlasNames) {
    if (!declared.has(name)) {
      return null;
    }
  }

  return { atlasNames: [...atlasNames], props };
}

export type { ImageLoader };

async function loadImageElement(url: string): Promise<CanvasImageSource> {
  const response = await fetch(url);
  if (!response.ok) throw new Error(`level-props: failed to load ${url}`);
  return createImageBitmap(await response.blob());
}

/**
 * Loads the decoration manifest and the atlases it references. Returns null when the manifest is
 * absent or malformed -- a checkout where `build-levels.mjs`/`extract-props.mjs` has not run just
 * draws a bare level. `cache` lets the caller share already-decoded images with the part textures
 * (the decoration quads mostly live in the same two atlases the parts use).
 */
export async function loadLevelProps(
  url: string = LEVEL_PROPS_URL,
  loadImage: ImageLoader = loadImageElement,
  cache: Map<string, CanvasImageSource> = new Map(),
): Promise<LevelPropsSet | null> {
  try {
    const response = await fetch(url);
    if (!response.ok) return null;
    const parsed = parseLevelProps(await response.json());
    if (parsed === null) return null;
    const base = url.slice(0, url.lastIndexOf("/") + 1);
    const atlases = new Map<string, CanvasImageSource>();
    await Promise.all(
      parsed.atlasNames.map(async (name) => {
        const existing = cache.get(name);
        const source = existing ?? (await loadImage(`${base}${name}`));
        cache.set(name, source);
        atlases.set(name, source);
      }),
    );
    return { atlases, props: parsed.props };
  } catch {
    return null;
  }
}

/**
 * The level's decorations in the original's own paint order: farthest first. The original tests depth
 * per pixel; the plane renderer has only the instance's own `z`, which is what the original authors
 * used to layer the level (`docs/specs/level-props.md` §4: the ground's fill sits at z = 0, its edge
 * band at -0.01 and the game plane at -5). Equal depths keep the level file's own order -- `sort` is
 * stable -- because the original's depth test cannot separate them either.
 */
export function sortPropsForDepth(props: readonly LevelProp[]): LevelProp[] {
  return [...props].sort((left, right) => right.z - left.z);
}

/**
 * Paints one depth bucket of the level's decorations, in the order given. The painter splits the
 * frame at the depths it already has (`../renderer/draw`'s `propDepthBuckets`) and calls this once per
 * break, rather than sorting the whole frame at once: the ground and the contraption keep their own,
 * already-decided order.
 */
export function drawProps(
  ctx: CanvasRenderingContext2D,
  camera: Camera,
  props: readonly LevelProp[],
  set: LevelPropsSet | null,
  width: number,
  height: number,
): void {
  if (set === null || props.length === 0) {
    return;
  }

  for (const prop of props) {
    const art = set.props.get(prop.id);
    if (art === undefined) continue;
    const image = set.atlases.get(art.atlas);
    if (image === undefined) continue;
    const at = worldToScreen(camera, prop.x, prop.y, width, height);
    const pixelScale = camera.scale;
    const w = art.sx * pixelScale;
    const h = art.sy * pixelScale;
    ctx.save();
    ctx.translate(at.x, at.y);
    // The canvas' y axis points down and the level's up, so a world angle is a negated canvas
    // rotation -- the same convention the part painter uses for its own yaw.
    ctx.rotate(-prop.rotation);
    // The quad centre's offset is in the instance's own frame: `cy` points up in the level, hence
    // the negation, and both axes scale with the instance's own localScale below.
    ctx.translate(art.cx * pixelScale, -art.cy * pixelScale);
    if (prop.scaleX !== 1 || prop.scaleY !== 1) {
      ctx.scale(prop.scaleX, prop.scaleY);
    }
    ctx.drawImage(image, art.x, art.y, art.w, art.h, -w / 2, -h / 2, w, h);
    ctx.restore();
  }
}
