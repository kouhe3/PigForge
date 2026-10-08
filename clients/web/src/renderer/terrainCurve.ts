import { curveLayerBitmap, type LevelTerrain, type LevelTerrainCurve } from "@/schema/levelContent";
import { type Camera, worldToScreen } from "./camera";
import type { GroundTexture, GroundTextureSet } from "./terrain";

/**
 * The `_curve` band along every terrain's outline: the original's `e2d/Curve` shader over the
 * `_curve` mesh a `level-content-v4` document's `terrain[].curve` carries.
 *
 * The mesh is a triangle strip between two rows -- `nodes` on the terrain surface (shader v = 1) and
 * `stripe`, each node pushed outwards (v = 0) -- and the shader samples the layer's texture at
 * `(u * texW, (1 - v) * texH)` in that texture's own pixel space, where
 * `u[i] = u[i - 1] + |nodes[i] - nodes[i - 1]|` (u[0] = 0) times `uScale` and texW/texH are the
 * layer's size. Every quad is therefore one affine map from texture pixels onto the canvas, which is
 * exactly a canvas pattern transform, and the strip's two triangles carry one each.
 *
 * `wrap: "repeat"` is a plain repeating pattern. `wrap: "clamp"` is Unity's `wrapU: 1`: u up to 1 is
 * the texture, and everything past it samples the rightmost texel column -- drawn here as the
 * clamped half of the triangle filled with a gradient along v whose stops are that column.
 *
 * Textures come from the same `GroundTextureSet` as the ground fills (`LEVEL_TEXTURE_BASE` in
 * `./terrain`) and a terrain whose curve art did not load draws no band at all, silently, exactly
 * like a missing fill. See `docs/specs/level-terrain-visuals.md`.
 */

/** Every curve texture file name the levels need, in the document's own order, unique. */
export function curveTextureNames(terrains: readonly LevelTerrain[]): string[] {
  const names: string[] = [];
  const seen = new Set<string>();
  for (const terrain of terrains) {
    for (const layer of terrain.curve?.textures ?? []) {
      if (!seen.has(layer.texture)) {
        seen.add(layer.texture);
        names.push(layer.texture);
      }
    }
  }
  return names;
}

/**
 * The shader's `u` at every node: the cumulative arc length from the first node, times `uScale`
 * (which the document sets to 1 / the first layer's width). The nodes' own frame is the terrain's
 * local one, and only the arc length matters, so the terrain's position never enters.
 */
export function curveNodeU(curve: LevelTerrainCurve): number[] {
  const u: number[] = [];
  let length = 0;
  curve.nodes.forEach(([x, y], index) => {
    if (index > 0) {
      const [previousX, previousY] = curve.nodes[index - 1];
      length += Math.hypot(x - previousX, y - previousY);
    }
    u.push(length * curve.uScale);
  });
  return u;
}

/**
 * One corner of a curve triangle: where it sits in the layer's texture pixels and where it lands on
 * the canvas. `ix` is `u * texW` and `iy` is `(1 - v) * texH` -- Unity's v axis runs up, canvas rows
 * run down -- and `x`/`y` follow the same CSS-pixel convention `./terrain` paints in (a canvas whose
 * backing store is scaled by the device pixel ratio keeps that scale in its own transform).
 */
export interface CurveCorner {
  ix: number;
  iy: number;
  x: number;
  y: number;
}

/** The six numbers of a canvas pattern transform, mapping texture pixels onto canvas pixels. */
export interface CurveTransform {
  a: number;
  b: number;
  c: number;
  d: number;
  e: number;
  f: number;
}

/**
 * The affine map taking a triangle's three corners from texture pixels to canvas pixels, or null for
 * a degenerate one (zero image-space area, or a map that is not finite). Three correspondences fix a
 * unique affine, and on a triangle the shader's UV interpolation is exactly that affine map.
 */
export function bandTriangleTransform(corners: readonly CurveCorner[]): CurveTransform | null {
  const [p, q, r] = corners;
  if (p === undefined || q === undefined || r === undefined) {
    return null;
  }

  const det = p.ix * (q.iy - r.iy) + q.ix * (r.iy - p.iy) + r.ix * (p.iy - q.iy);
  if (det === 0 || !Number.isFinite(det)) {
    return null;
  }

  const a = (p.x * (q.iy - r.iy) + q.x * (r.iy - p.iy) + r.x * (p.iy - q.iy)) / det;
  const b = (p.y * (q.iy - r.iy) + q.y * (r.iy - p.iy) + r.y * (p.iy - q.iy)) / det;
  const c = (p.x * (r.ix - q.ix) + q.x * (p.ix - r.ix) + r.x * (q.ix - p.ix)) / det;
  const d = (p.y * (r.ix - q.ix) + q.y * (p.ix - r.ix) + r.y * (q.ix - p.ix)) / det;
  const e = (p.x * (q.ix * r.iy - r.ix * q.iy) + q.x * (r.ix * p.iy - p.ix * r.iy) + r.x * (p.ix * q.iy - q.ix * p.iy)) / det;
  const f = (p.y * (q.ix * r.iy - r.ix * q.iy) + q.y * (r.ix * p.iy - p.ix * r.iy) + r.y * (p.ix * q.iy - q.ix * p.iy)) / det;
  if (!Number.isFinite(a) || !Number.isFinite(b) || !Number.isFinite(c)
    || !Number.isFinite(d) || !Number.isFinite(e) || !Number.isFinite(f)) {
    return null;
  }
  return { a, b, c, d, e, f };
}

/** One curve triangle, in the strip's own order. */
type CurveTriangle = readonly [CurveCorner, CurveCorner, CurveCorner];

/**
 * Paints every terrain's `_curve` band, in the document's own terrain order. Called after
 * `drawTerrain`, so the whole band sits on top of every ground fill, like the original's curve mesh
 * at z = -0.01 sits in front of the fills at z = 0.
 */
export function drawTerrainCurves(
  ctx: CanvasRenderingContext2D,
  camera: Camera,
  terrains: readonly LevelTerrain[],
  textures: GroundTextureSet | null,
  width: number,
  height: number,
): void {
  if (textures === null) {
    return;
  }

  for (const terrain of terrains) {
    const curve = terrain.curve;
    if (curve === undefined) {
      continue;
    }

    // Either missing layer means no band at all: a terrain whose curve art did not load keeps the
    // ground fill it already drew, silently, exactly like a terrain whose fill art is absent.
    const first = textures.get(curve.textures[0].texture);
    const second = textures.get(curve.textures[1].texture);
    if (first === undefined || second === undefined) {
      continue;
    }

    const firstPattern = ctx.createPattern(first.source, "repeat");
    const secondPattern = ctx.createPattern(second.source, "repeat");
    if (firstPattern === null || secondPattern === null) {
      continue;
    }

    drawBand(ctx, camera, terrain, curve, [first, second], [firstPattern, secondPattern], width, height);
  }
}

/** One terrain's whole band: its quads in order, each split into the strip's two triangles. */
function drawBand(
  ctx: CanvasRenderingContext2D,
  camera: Camera,
  terrain: LevelTerrain,
  curve: LevelTerrainCurve,
  layers: readonly [GroundTexture, GroundTexture],
  patterns: readonly [CanvasPattern, CanvasPattern],
  width: number,
  height: number,
): void {
  const u = curveNodeU(curve);
  const layerPerNode = curveLayerBitmap(curve);
  const nodes = curve.nodes.map(([x, y]) => worldToScreen(camera, terrain.position[0] + x, terrain.position[1] + y, width, height));
  const stripes = curve.stripe.map(([x, y]) => worldToScreen(camera, terrain.position[0] + x, terrain.position[1] + y, width, height));

  for (let i = 0; i + 1 < nodes.length; i += 1) {
    // A duplicate node has no arc length, so `u * texW` is constant across its quad and no finite
    // affine map exists (the shader would sample one texel column). Skipping it keeps NaNs out of
    // every path, and a zero-length quad has no area to lose.
    if (!(u[i + 1] - u[i] > 0)) {
      continue;
    }

    const layer = layerPerNode[i];
    const texture = layers[layer];
    const pattern = patterns[layer];
    // Texture pixels of the two node columns: `u * texW` on the nodes row (v = 1, so `(1 - v) * texH`
    // is 0) and on the stripe row (v = 0, so it is the texture's height).
    const x0 = u[i] * texture.width;
    const x1 = u[i + 1] * texture.width;
    const bottom = texture.height;
    const clamp = curve.textures[layer].wrap === "clamp";

    paintTriangle(ctx, pattern, clamp, texture.width, texture, [
      { ix: x0, iy: 0, x: nodes[i].x, y: nodes[i].y },
      { ix: x1, iy: 0, x: nodes[i + 1].x, y: nodes[i + 1].y },
      { ix: x0, iy: bottom, x: stripes[i].x, y: stripes[i].y },
    ]);
    paintTriangle(ctx, pattern, clamp, texture.width, texture, [
      { ix: x1, iy: 0, x: nodes[i + 1].x, y: nodes[i + 1].y },
      { ix: x1, iy: bottom, x: stripes[i + 1].x, y: stripes[i + 1].y },
      { ix: x0, iy: bottom, x: stripes[i].x, y: stripes[i].y },
    ]);
  }
}

/**
 * Paints one triangle of the strip: its pattern transform is its own affine map. A `repeat` layer is
 * the whole triangle. A `clamp` layer is cut at u = 1 -- a straight cut, because u is linear across
 * the triangle -- into the half the texture covers and the half the rightmost column covers.
 */
function paintTriangle(
  ctx: CanvasRenderingContext2D,
  pattern: CanvasPattern,
  clamp: boolean,
  cut: number,
  texture: GroundTexture,
  triangle: CurveTriangle,
): void {
  const transform = bandTriangleTransform(triangle);
  if (transform === null) {
    return;
  }
  pattern.setTransform(transform);

  if (!clamp) {
    fillPolygon(ctx, triangle, pattern);
    return;
  }

  const far = cutPolygon(triangle, cut, false);
  const near = cutPolygon(triangle, cut, true);
  if (near !== null) {
    fillPolygon(ctx, near, pattern);
  }
  if (far !== null) {
    const gradient = clampGradient(ctx, texture, triangle, transform);
    if (gradient !== null) {
      fillPolygon(ctx, far, gradient);
    }
  }
}

/**
 * The clamped half's fill: a linear gradient along the *triangle's own* `v` axis, from the point
 * where `v` is 1 (the texture's top row) to the point where it is 0 (the bottom edge), with the
 * layer's rightmost texel column as its stops at each texel's centre -- which is what Unity's clamp
 * mode hands the shader for every u past 1.
 *
 * The axis must be the triangle's own `iy` gradient rather than the quad's midline: `v` is affine
 * per triangle (so a gradient parallel to its gradient reproduces it exactly), but the two rows of a
 * bent quad are not parallel, so the midline direction is only parallel to it for a parallelogram.
 */
function clampGradient(
  ctx: CanvasRenderingContext2D,
  texture: GroundTexture,
  triangle: CurveTriangle,
  transform: CurveTransform,
): CanvasGradient | null {
  const stops = rightmostColumn(texture);
  if (stops === null || stops.length === 0) {
    return null;
  }

  // `x = a*ix + c*iy + e` and `y = b*ix + d*iy + f` invert to `iy = (-b*(x - e) + a*(y - f)) / det`,
  // so the texture-space row grows along `(-b, a) / det` on the canvas.
  const determinant = transform.a * transform.d - transform.b * transform.c;
  if (determinant === 0 || !Number.isFinite(determinant)) {
    return null;
  }
  const gradientX = -transform.b / determinant;
  const gradientY = transform.a / determinant;
  const norm = gradientX * gradientX + gradientY * gradientY;
  if (!(norm > 0) || !Number.isFinite(norm)) {
    return null;
  }

  // Anchor the axis on the row this corner sits on, then step to `iy = texture.height`; a linear
  // gradient projects onto its own axis, and this one is parallel to `iy`'s gradient, so the colour
  // at a pixel is the column texel at that pixel's own `v`.
  const anchor = triangle[0];
  const from = { x: anchor.x - (anchor.iy * gradientX) / norm, y: anchor.y - (anchor.iy * gradientY) / norm };
  const to = { x: from.x + (texture.height * gradientX) / norm, y: from.y + (texture.height * gradientY) / norm };

  const gradient = ctx.createLinearGradient(from.x, from.y, to.x, to.y);
  stops.forEach((colour, row) => gradient.addColorStop((row + 0.5) / stops.length, colour));
  return gradient;
}

/** The parts of a triangle on one side of the texture-pixel line `ix = cut`. */
function cutPolygon(triangle: CurveTriangle, cut: number, keepBelow: boolean): CurveCorner[] | null {
  const polygon: CurveCorner[] = [];
  triangle.forEach((corner0, index) => {
    const corner1 = triangle[(index + 1) % triangle.length];
    const inCorner0 = keepBelow ? corner0.ix <= cut : corner0.ix >= cut;
    const inCorner1 = keepBelow ? corner1.ix <= cut : corner1.ix >= cut;
    if (inCorner0) {
      polygon.push(corner0);
    }
    if (inCorner0 !== inCorner1) {
      // The map is affine, so a texture-space ratio is the canvas-space ratio on the same edge.
      const ratio = (cut - corner0.ix) / (corner1.ix - corner0.ix);
      polygon.push({
        ix: cut,
        iy: corner0.iy + (corner1.iy - corner0.iy) * ratio,
        x: corner0.x + (corner1.x - corner0.x) * ratio,
        y: corner0.y + (corner1.y - corner0.y) * ratio,
      });
    }
  });
  return polygon.length >= 3 && polygonArea(polygon) > 0 ? polygon : null;
}

/** A polygon's area in canvas pixels; a zero (or non-finite) one paints nothing. */
function polygonArea(polygon: readonly CurveCorner[]): number {
  let sum = 0;
  polygon.forEach((corner0, index) => {
    const corner1 = polygon[(index + 1) % polygon.length];
    sum += corner0.x * corner1.y - corner1.x * corner0.y;
  });
  return Math.abs(sum) / 2;
}

/** Fills one canvas-space polygon with a pattern or gradient. */
function fillPolygon(
  ctx: CanvasRenderingContext2D,
  polygon: readonly CurveCorner[],
  style: CanvasPattern | CanvasGradient,
): void {
  if (polygon.length < 3) {
    return;
  }

  ctx.beginPath();
  polygon.forEach((point, index) => {
    if (index === 0) {
      ctx.moveTo(point.x, point.y);
    } else {
      ctx.lineTo(point.x, point.y);
    }
  });
  ctx.closePath();
  ctx.fillStyle = style;
  ctx.fill();
}

/** A texture's rightmost texel column as canvas colour stops (top row first), or null if unreadable. */
const columns = new WeakMap<GroundTexture, readonly string[] | null>();

function rightmostColumn(texture: GroundTexture): readonly string[] | null {
  const cached = columns.get(texture);
  if (cached !== undefined) {
    return cached;
  }

  const stops = readRightmostColumn(texture);
  columns.set(texture, stops);
  return stops;
}

/** The 2D context of either canvas flavour; the sampler only needs `drawImage` and `getImageData`. */
type SampleContext = CanvasRenderingContext2D | OffscreenCanvasRenderingContext2D;

/** An offscreen 2D context, or null where the host has none (node, a worker without OffscreenCanvas). */
function createOffscreen(width: number, height: number): SampleContext | null {
  if (typeof document !== "undefined") {
    const element = document.createElement("canvas");
    element.width = width;
    element.height = height;
    return element.getContext("2d");
  }

  if (typeof OffscreenCanvas !== "undefined") {
    return new OffscreenCanvas(width, height).getContext("2d");
  }

  return null;
}

function readRightmostColumn(texture: GroundTexture): readonly string[] | null {
  if (texture.width < 1 || texture.height < 1) {
    return null;
  }

  const canvas = createOffscreen(texture.width, texture.height);
  if (canvas === null) {
    return null;
  }
  canvas.drawImage(texture.source, 0, 0, texture.width, texture.height);

  try {
    const { data } = canvas.getImageData(texture.width - 1, 0, 1, texture.height);
    const stops: string[] = [];
    for (let row = 0; row < texture.height; row += 1) {
      const at = row * 4;
      stops.push(`rgba(${data[at]},${data[at + 1]},${data[at + 2]},${data[at + 3] / 255})`);
    }
    return stops;
  } catch {
    // A host without pixel reads (or a tainted bitmap): the clamped half then keeps the fill.
    return null;
  }
}
