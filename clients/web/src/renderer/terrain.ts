import type { LevelFillColor, LevelTerrain, LevelTerrainFill } from "@/schema/levelContent";
import type { Vec3 } from "@/schema/types";
import { type Camera, worldToScreen } from "./camera";

/**
 * The level's ground textures, and the painter that reproduces the original's `e2d/Fill` shader:
 *
 *     return tex2D(_MainTex, i.texcoord) * _Color;
 *
 * with the UVs `LevelLoader.ReadMesh` computes per vertex (`LevelLoader.cs:279-290`) as
 * `uv = (world - tileOffset) / tileSize`. The texture, the tint and the tile offset come from the
 * level document; only the texture *file* has to be fetched, and original art is never committed
 * (`.gitignore`, `clients/web/public/assets/original/`), exactly like the part atlases. See
 * `docs/specs/level-terrain-visuals.md`.
 */

/** Where a level's ground textures live, by the file name the level document carries. */
export const LEVEL_TEXTURE_BASE = "/assets/original/levels/";

/** The ground's own colours, used when a terrain has no texture or the texture did not load. */
export const GROUND_FILL = "#5c6b52";
export const GROUND_STROKE = "#3e4a37";

/** A decoded ground texture and the pixel size its UVs are measured in. */
export interface GroundTexture {
  /** The decoded bitmap, ready to become a canvas pattern. */
  source: CanvasImageSource;
  width: number;
  height: number;
}

/** The ground textures of one level, by file name. A name that never loaded is simply absent. */
export type GroundTextureSet = ReadonlyMap<string, GroundTexture>;

export type ImageLoader = (url: string) => Promise<CanvasImageSource>;

async function loadBitmap(url: string): Promise<CanvasImageSource> {
  const response = await fetch(url);
  if (!response.ok) {
    throw new Error(`level-textures: failed to load ${url}`);
  }
  return createImageBitmap(await response.blob());
}

/** Every texture file name the level's terrain needs, in the document's own order, unique. */
export function groundTextureNames(terrains: readonly LevelTerrain[]): string[] {
  const names: string[] = [];
  const seen = new Set<string>();
  for (const terrain of terrains) {
    const name = terrain.fill?.texture;
    if (name !== undefined && !seen.has(name)) {
      seen.add(name);
      names.push(name);
    }
  }
  return names;
}

/**
 * Loads the named ground textures. A texture that cannot be fetched is left out rather than
 * throwing: a level whose art was not extracted (a clean checkout, or `tools/bple-levels/
 * build-levels.mjs` not run) still plays, with the flat ground colour as its fallback.
 */
export async function loadGroundTextures(
  names: Iterable<string>,
  base: string = LEVEL_TEXTURE_BASE,
  loadImage: ImageLoader = loadBitmap,
): Promise<GroundTextureSet> {
  const textures = new Map<string, GroundTexture>();
  await Promise.all(
    [...new Set(names)].map(async (name) => {
      try {
        const source = await loadImage(`${base}${name}`);
        const { width, height } = source as { width: number; height: number };
        textures.set(name, { source, width, height });
      } catch {
        // Absent art is not an error: `drawTerrain` falls back to the flat fill.
      }
    }),
  );
  return textures;
}

/** The six numbers of a canvas pattern transform, in the canvas' own pixel space. */
export interface GroundPatternTransform {
  a: number;
  b: number;
  c: number;
  d: number;
  e: number;
  f: number;
}

/**
 * The transform that maps the texture's pixels onto the canvas so the ground tiles exactly as the
 * shader's UVs do: one texture repeat spans `tileSize` world metres from `tileOffset`, and Unity's
 * `v` axis runs *up* while a canvas' rows run down, which is why the tile's own top row (`f`) sits at
 * `tileOffset.y + tileSize.y`.
 *
 * The UVs come from the mesh's own vertices (`LevelLoader.ReadMesh`, `LevelLoader.cs:279-290`), and
 * the mesh sits in the terrain's **local** frame -- so the anchor is the terrain's own origin plus the
 * tile offset, not the world origin. Two terrains of one level therefore start their tiling phase at
 * their own corners (measured: without the position the drawn ground correlates with the texture at
 * only r = 0.19, with it at r = 0.93).
 */
export function groundPatternTransform(
  camera: Camera,
  terrain: { position: Vec3; fill: LevelTerrainFill },
  texture: { width: number; height: number },
  width: number,
  height: number,
): GroundPatternTransform {
  const [offsetX, offsetY] = terrain.fill.tileOffset;
  const [tileWidth, tileHeight] = terrain.fill.tileSize;
  const originX = terrain.position[0] + offsetX;
  const originY = terrain.position[1] + offsetY;
  return {
    a: (tileWidth * camera.scale) / texture.width,
    b: 0,
    c: 0,
    d: (tileHeight * camera.scale) / texture.height,
    e: (originX - camera.x) * camera.scale + width * 0.5,
    f: height * 0.5 - (originY + tileHeight - camera.y) * camera.scale,
  };
}

/** Paints one ground tile: the texture multiplied by the terrain's own `_Color`. */
export type GroundTinter = (texture: GroundTexture, color: LevelFillColor) => CanvasImageSource;

/** Tinted tiles, per texture, so a frame never re-multiplies the same pixels. */
const tinted = new WeakMap<GroundTexture, Map<string, CanvasImageSource>>();

const colorKey = (color: LevelFillColor): string => color.join(",");

/**
 * The original multiplies the sampled texel by `_Color` (`fill.shader`), and a canvas pattern can
 * only repeat one bitmap -- so the tile is multiplied once per (texture, colour) pair: the bitmap is
 * drawn into an offscreen canvas, then the colour is composited over it with `multiply`. That is
 * per-pixel `tex * _Color` *including* alpha, which the shader's own `Blend SrcAlpha OneMinusSrcAlpha`
 * also carries. A host without a canvas (a node test) gets the untiled bitmap instead of throwing.
 */
function tintTile(texture: GroundTexture, color: LevelFillColor): CanvasImageSource {
  let byColor = tinted.get(texture);
  if (byColor === undefined) {
    byColor = new Map<string, CanvasImageSource>();
    tinted.set(texture, byColor);
  }

  const key = colorKey(color);
  const cached = byColor.get(key);
  if (cached !== undefined) {
    return cached;
  }

  const canvas = createOffscreen(texture.width, texture.height);
  if (canvas === null) {
    return texture.source;
  }

  canvas.context.drawImage(texture.source, 0, 0, texture.width, texture.height);
  // `ReadColor` is `byte * 0.003921569f` (LevelLoader.cs:172-181): the same constant reproduces the
  // original's own float, and the bytes themselves are what the level file stores.
  const [red, green, blue, alpha] = color;
  canvas.context.globalCompositeOperation = "multiply";
  canvas.context.fillStyle = `rgba(${red},${green},${blue},${alpha / 255})`;
  canvas.context.fillRect(0, 0, texture.width, texture.height);
  canvas.context.globalCompositeOperation = "source-over";
  byColor.set(key, canvas.element);
  return canvas.element;
}

/** The 2D context of either canvas flavour; only the drawing calls the tinter makes are used. */
type TileContext = CanvasRenderingContext2D | OffscreenCanvasRenderingContext2D;

/** An offscreen 2D canvas, or null where the host has none (node, a worker without OffscreenCanvas). */
function createOffscreen(width: number, height: number): { element: CanvasImageSource; context: TileContext } | null {
  if (typeof document !== "undefined") {
    const element = document.createElement("canvas");
    element.width = width;
    element.height = height;
    const context = element.getContext("2d");
    return context === null ? null : { element, context };
  }

  if (typeof OffscreenCanvas !== "undefined") {
    const element = new OffscreenCanvas(width, height);
    const context = element.getContext("2d");
    return context === null ? null : { element, context };
  }

  return null;
}

/**
 * Paints the level's ground. Every terrain's loops are filled in the level's own order, textured with
 * the terrain's own fill when that texture is available and in the flat ground colour otherwise (a
 * v2 document, or a checkout whose art was not extracted). Each loop is closed and filled on its own,
 * so a hole (a loop walked the other way round) stays a hole.
 */
export function drawTerrain(
  ctx: CanvasRenderingContext2D,
  camera: Camera,
  terrains: readonly LevelTerrain[],
  textures: GroundTextureSet | null,
  width: number,
  height: number,
  tint: GroundTinter = tintTile,
): void {
  ctx.strokeStyle = GROUND_STROKE;
  ctx.lineWidth = 1.5;
  for (const terrain of terrains) {
    const fill = terrain.fill;
    const texture = fill === undefined ? undefined : textures?.get(fill.texture) ?? undefined;
    const tile = fill === undefined || texture === undefined ? null : tint(texture, fill.color);
    const pattern = tile === null ? null : ctx.createPattern(tile, "repeat");
    if (pattern !== null && fill !== undefined && texture !== undefined) {
      pattern.setTransform(groundPatternTransform(camera, { position: terrain.position, fill }, texture, width, height));
    }

    for (const loop of terrain.loops) {
      const points = loopToWorld(terrain, loop);
      ctx.beginPath();
      points.forEach((point, index) => {
        const screen = worldToScreen(camera, point.x, point.y, width, height);
        if (index === 0) {
          ctx.moveTo(screen.x, screen.y);
        } else {
          ctx.lineTo(screen.x, screen.y);
        }
      });
      ctx.closePath();
      ctx.fillStyle = pattern ?? GROUND_FILL;
      ctx.fill();
      ctx.stroke();
    }
  }
}

/**
 * A loop's points in world metres: the terrain's own frame is only translated (`position`), so each
 * local `[x, y]` is offset by it. The loop is closed implicitly by the painter.
 */
export function loopToWorld(
  terrain: LevelTerrain,
  loop: readonly (readonly [number, number])[],
): Array<{ x: number; y: number }> {
  return loop.map(([x, y]) => ({ x: terrain.position[0] + x, y: terrain.position[1] + y }));
}
