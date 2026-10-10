import type { LevelProp } from "@/schema/levelContent";
import type { ImageLoader } from "./atlas";
import { type Camera, worldToScreen } from "./camera";

/**
 * One sprite quad of a prop, from the client's own `level-props.json` (written by
 * `tools/bple-props/extract-props.mjs` out of the original's prefabs). Two ways to paint it: an atlas
 * file plus the source rectangle inside it in pixels, or -- for a background layer's `*_Fill` quad,
 * whose material is `_Custom/Unlit_Monochrome` and samples nothing -- a solid `color`.
 *
 * The rest is the quad's frame in world metres, relative to its prop's root: `dx`/`dy` its position,
 * `rotation` about z, `scaleX`/`scaleY` its own scale, `cx`/`cy` the art's pivot offset from the quad
 * centre (the original's `Sprite` pivot; zero for an `UnmanagedSprite`) and `sx`/`sy` its size. See
 * `docs/specs/level-props.md` §3.
 */
export interface LevelPropQuad {
  atlas?: string;
  x?: number;
  y?: number;
  w?: number;
  h?: number;
  color?: [number, number, number, number];
  cx: number;
  cy: number;
  sx: number;
  sy: number;
  dx: number;
  dy: number;
  /** The depth the transform chain puts this quad on, before its prop instance's own z. */
  dz: number;
  rotation: number;
  scaleX: number;
  scaleY: number;
  /** How much of the camera's own movement this quad takes with it (`ParallaxManager.Update`). */
  parallax: { x: number; y: number; limitY?: number };
}

/** The prop manifest and the atlas images it draws from. */
export interface LevelPropsSet {
  atlases: ReadonlyMap<string, CanvasImageSource>;
  props: ReadonlyMap<string, readonly LevelPropQuad[]>;
}

export const LEVEL_PROPS_URL = "/assets/original/level-props.json";

/** Manifest versions this parser understands: 2 (a prop is a list of quads). */
const SUPPORTED_SCHEMA_VERSIONS = [2];

const FRAME_KEYS = ["cx", "cy", "sx", "sy", "dx", "dy", "dz", "rotation", "scaleX", "scaleY"] as const;

function isFiniteNumber(value: unknown): value is number {
  return typeof value === "number" && Number.isFinite(value);
}

/** One quad: an atlas rect or a colour, plus the frame that places it inside its prop. */
function readQuad(value: unknown): LevelPropQuad | null {
  if (value === null || typeof value !== "object" || Array.isArray(value)) {
    return null;
  }

  const quad = value as Record<string, unknown>;
  for (const key of FRAME_KEYS) {
    if (!isFiniteNumber(quad[key])) {
      return null;
    }
  }
  if (!((quad.sx as number) > 0) || !((quad.sy as number) > 0)) {
    return null;
  }

  const parallax = quad.parallax;
  if (parallax === null || typeof parallax !== "object" || Array.isArray(parallax)) {
    return null;
  }
  const { x, y, limitY } = parallax as Record<string, unknown>;
  if (!isFiniteNumber(x) || !isFiniteNumber(y) || (limitY !== undefined && !isFiniteNumber(limitY))) {
    return null;
  }
  const frame = {
    cx: quad.cx as number,
    cy: quad.cy as number,
    sx: quad.sx as number,
    sy: quad.sy as number,
    dx: quad.dx as number,
    dy: quad.dy as number,
    dz: quad.dz as number,
    rotation: quad.rotation as number,
    scaleX: quad.scaleX as number,
    scaleY: quad.scaleY as number,
    parallax: { x, y, ...(limitY === undefined ? {} : { limitY }) },
  };

  const color = quad.color;
  if (color !== undefined) {
    if (!Array.isArray(color) || color.length !== 4 || !color.every((channel) => isFiniteNumber(channel) && channel >= 0 && channel <= 1)) {
      return null;
    }
    return { ...frame, color: [color[0], color[1], color[2], color[3]] as [number, number, number, number] };
  }

  if (typeof quad.atlas !== "string" || quad.atlas.length === 0) {
    return null;
  }
  for (const key of ["x", "y", "w", "h"] as const) {
    if (!isFiniteNumber(quad[key])) {
      return null;
    }
  }
  if (!((quad.w as number) > 0) || !((quad.h as number) > 0)) {
    return null;
  }
  return { ...frame, atlas: quad.atlas, x: quad.x as number, y: quad.y as number, w: quad.w as number, h: quad.h as number };
}

/**
 * Parses a `level-props.json` value, returning null for anything the renderer cannot trust. The
 * manifest is generated, so the parse is a gate rather than a repair: a malformed entry is dropped
 * with the rest of the document (the painter then draws the level without its dressing, the same
 * fallback the ground and part atlas loaders take).
 */
export function parseLevelProps(value: unknown): { atlasNames: string[]; props: Map<string, readonly LevelPropQuad[]> } | null {
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

  // `cloudProps` carries the cloud prefabs the level's `Cloud*Set`s name (`docs/specs/level-props.md`
  // §3.7): a level places one instance per spawned cloud, so they resolve exactly like a palette prop.
  const sections: Array<Record<string, unknown>> = [];
  for (const section of [manifest.props, manifest.cloudProps]) {
    if (section === null || typeof section !== "object" || Array.isArray(section)) continue;
    sections.push(section as Record<string, unknown>);
  }
  if (sections.length === 0) {
    return null;
  }

  const props = new Map<string, readonly LevelPropQuad[]>();
  for (const section of sections) {
    for (const [id, entry] of Object.entries(section)) {
      if (entry === null || typeof entry !== "object" || Array.isArray(entry)) continue;
      const quads = (entry as Record<string, unknown>).quads;
      if (!Array.isArray(quads) || quads.length === 0) continue;
      const read = quads.map(readQuad);
      if (read.some((quad) => quad === null)) continue;
      props.set(id, read as LevelPropQuad[]);
    }
  }
  if (props.size === 0) {
    return null;
  }

  // Every quad's atlas must be declared, so a load of the manifest is enough to know what to fetch.
  const atlasNames = new Set<string>();
  for (const quads of props.values()) {
    for (const quad of quads) {
      if (quad.atlas !== undefined) atlasNames.add(quad.atlas);
    }
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

/** One quad of one placed decoration: the level instance that carries it and the quad's own frame. */
export interface DecorationQuad {
  prop: LevelProp;
  quad: LevelPropQuad;
}

/**
 * Paints one depth bucket of the level's decoration quads, in the order given. The painter splits the
 * frame at the depths it already has (`../renderer/draw`'s `propDepthBuckets`, which flattens each
 * prop into its quads because a prop's own quads sit on their own depths) and calls this once per
 * break, rather than sorting the whole frame at once: the ground and the contraption keep their own,
 * already-decided order.
 *
 * Every quad is drawn through its own frame, composed with its prop instance's: `parallaxFrom` is the
 * camera the level started on, and the difference between it and the live camera is what the
 * original's `ParallaxManager` moves a tagged backdrop layer by. A layer with `parallax.y > 0` (the
 * foreground) also tracks the camera's own vertical movement downwards, never rising above where it
 * started.
 */
export function drawProps(
  ctx: CanvasRenderingContext2D,
  camera: Camera,
  quads: readonly DecorationQuad[],
  set: LevelPropsSet | null,
  width: number,
  height: number,
  parallaxFrom: { x: number; y: number } = camera,
): void {
  if (set === null || quads.length === 0) {
    return;
  }

  const shiftX = camera.x - parallaxFrom.x;
  const shiftY = camera.y - parallaxFrom.y;
  for (const { prop, quad } of quads) {
    const at = worldToScreen(camera, prop.x, prop.y, width, height);
    const pixelScale = camera.scale;
    const image = quad.atlas === undefined ? undefined : set.atlases.get(quad.atlas);
    if (quad.atlas !== undefined && image === undefined) continue;
    ctx.save();
    ctx.translate(at.x, at.y);
    // Everything from here on is in world metres: the canvas' y axis points down and the level's up,
    // so a world offset is a negated canvas y and a world angle a negated canvas rotation -- the same
    // convention the part painter uses for its own yaw.
    ctx.scale(pixelScale, pixelScale);
    if (quad.parallax.x !== 0 || quad.parallax.y !== 0) {
      // `ParallaxManager.Update` shifts the layer in world x/y by the camera's own movement since the
      // level started; the foreground follows vertically at 0.2 and never above its own start.
      ctx.translate(quad.parallax.x * shiftX, quad.parallax.y === 0 ? 0 : -Math.min(0, quad.parallax.y * shiftY));
    }
    ctx.rotate(-prop.rotation);
    ctx.scale(prop.scaleX, prop.scaleY);
    ctx.translate(quad.dx, -quad.dy);
    ctx.rotate(-quad.rotation);
    ctx.scale(quad.scaleX, quad.scaleY);
    ctx.translate(quad.cx, -quad.cy);
    if (quad.color !== undefined) {
      const [red, green, blue, alpha] = quad.color;
      ctx.fillStyle = `rgba(${Math.round(red * 255)}, ${Math.round(green * 255)}, ${Math.round(blue * 255)}, ${alpha})`;
      ctx.fillRect(-quad.sx / 2, -quad.sy / 2, quad.sx, quad.sy);
    } else if (image !== undefined) {
      ctx.drawImage(image, quad.x as number, quad.y as number, quad.w as number, quad.h as number, -quad.sx / 2, -quad.sy / 2, quad.sx, quad.sy);
    }
    ctx.restore();
  }
}
