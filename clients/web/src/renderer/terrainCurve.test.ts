import { afterEach, describe, expect, it, vi } from "vitest";
import { curveLayerBitmap, type LevelTerrain, type LevelTerrainCurve } from "@/schema/levelContent";
import { type Camera } from "./camera";
import { drawFrame } from "./draw";
import type { GroundTexture, GroundTextureSet } from "./terrain";
import { bandTriangleTransform, curveNodeU, curveTextureNames, drawTerrainCurves } from "./terrainCurve";

/** The camera the fixtures below are measured with: 10 px per world metre over a 100 x 100 canvas. */
const camera: Camera = { x: 0, y: 0, scale: 10 };

/**
 * A fresh texture set: two 8 x 8 sentinel bitmaps (the pixel size their UVs are measured in). The
 * rightmost-column cache is keyed by the texture object, so tests never share one.
 */
function loadedTextures(): GroundTextureSet {
  const grass: GroundTexture = { source: { sentinel: "grass" } as unknown as CanvasImageSource, width: 8, height: 8 };
  const rock: GroundTexture = { source: { sentinel: "rock" } as unknown as CanvasImageSource, width: 8, height: 8 };
  return new Map([["grass.png", grass], ["rock.png", rock]]);
}

/**
 * Two nodes spanning 2 world metres with the stripe row 1 metre below them, measured in 8 x 8 pixel
 * textures at `uScale` 0.125 -- so the texture-pixel columns are `[0, 2]` and the shader's
 * `u[1] = 0.25`, which keeps every number below exact.
 */
function curveFixture(overrides: Partial<LevelTerrainCurve> = {}): LevelTerrainCurve {
  const curve: LevelTerrainCurve = {
    nodes: [[0, 0], [2, 0]],
    stripe: [[0, -1], [2, -1]],
    textures: [
      { texture: "grass.png", wrap: "repeat" },
      { texture: "rock.png", wrap: "repeat" },
    ],
    uScale: 0.125,
    splat1: [],
  };
  return Object.assign(curve, overrides);
}

function curveTerrain(curve: LevelTerrainCurve): LevelTerrain {
  return { position: [0, 0, 0], depth: 10, loops: [[[0, 0], [1, 0], [0, 1]]], curve };
}

interface FillRecord {
  points: Array<[number, number]>;
  style: unknown;
}

interface PatternRecord {
  source: unknown;
  repeat: string;
  transforms: Array<Record<string, number>>;
  setTransform: (transform: Record<string, number>) => void;
}

interface GradientRecord {
  from: [number, number];
  to: [number, number];
  stops: Array<{ offset: number; colour: string }>;
  addColorStop: (offset: number, colour: string) => void;
}

/** A fake 2D context that records paths, fills, patterns and gradients -- and nothing else. */
function makeRecorder() {
  const ops: string[] = [];
  const fills: FillRecord[] = [];
  const patterns: PatternRecord[] = [];
  const gradients: GradientRecord[] = [];
  let points: Array<[number, number]> = [];
  let fillStyle: unknown = "";
  const recorder = {
    canvas: { clientWidth: 100, clientHeight: 100, width: 100, height: 100 },
    get fillStyle(): unknown {
      return fillStyle;
    },
    set fillStyle(value: unknown) {
      fillStyle = value;
    },
    strokeStyle: "",
    lineWidth: 1,
    globalAlpha: 1,
    font: "",
    textAlign: "",
    textBaseline: "",
    fillRect: () => { ops.push("fillRect"); },
    strokeRect: () => { ops.push("strokeRect"); },
    beginPath: () => {
      points = [];
      ops.push("beginPath");
    },
    moveTo: (x: number, y: number) => { points.push([x, y]); },
    lineTo: (x: number, y: number) => { points.push([x, y]); },
    closePath: () => { ops.push("closePath"); },
    fill: () => {
      ops.push("fill");
      fills.push({ points: [...points], style: fillStyle });
    },
    stroke: () => { ops.push("stroke"); },
    arc: () => {},
    drawImage: () => { ops.push("drawImage"); },
    fillText: () => {},
    save: () => {},
    restore: () => {},
    translate: () => {},
    rotate: () => {},
    scale: () => {},
    setLineDash: () => {},
    createRadialGradient: () => ({ addColorStop: () => {} }),
    createPattern: (source: unknown, repeat: string) => {
      const pattern: PatternRecord = {
        source,
        repeat,
        transforms: [],
        setTransform: (transform: Record<string, number>) => {
          pattern.transforms.push(transform);
          ops.push("setTransform");
        },
      };
      patterns.push(pattern);
      ops.push("pattern");
      return pattern;
    },
    createLinearGradient: (x0: number, y0: number, x1: number, y1: number) => {
      const gradient: GradientRecord = {
        from: [x0, y0],
        to: [x1, y1],
        stops: [],
        addColorStop: (offset: number, colour: string) => {
          gradient.stops.push({ offset, colour });
        },
      };
      gradients.push(gradient);
      ops.push("gradient");
      return gradient;
    },
  };
  return { ctx: recorder as unknown as CanvasRenderingContext2D, ops, fills, patterns, gradients };
}

/** Stands in for a host canvas that can hand back the pixels of a drawn bitmap. */
function stubPixelSource(height: number): void {
  const data = new Uint8ClampedArray(height * 4);
  for (let row = 0; row < height; row += 1) {
    data[row * 4] = 10 + row;
    data[row * 4 + 3] = 255;
  }
  vi.stubGlobal("document", {
    createElement: () => ({
      width: 0,
      height: 0,
      getContext: () => ({
        drawImage: () => {},
        getImageData: () => ({ data }),
      }),
    }),
  });
}

/** Every coordinate a fill drew, to prove a degenerate quad never reaches the path. */
function allFinite(fills: readonly FillRecord[]): boolean {
  for (const fill of fills) {
    for (const [x, y] of fill.points) {
      if (!Number.isFinite(x) || !Number.isFinite(y)) {
        return false;
      }
    }
  }
  return true;
}

afterEach(() => {
  vi.unstubAllGlobals();
});

describe("curveNodeU", () => {
  it("accumulates the arc length of every segment, scaled by uScale", () => {
    const curve = curveFixture({
      nodes: [[0, 0], [3, 4], [3, 8]],
      stripe: [[0, -1], [3, 3], [3, 7]],
      uScale: 0.5,
    });
    expect(curveNodeU(curve)).toEqual([0, 2.5, 4.5]);
  });

  it("measures the curve's own frame, so node coordinates outside the origin shift nothing", () => {
    expect(curveNodeU(curveFixture())[0]).toBe(0);
    expect(curveNodeU(curveFixture({ nodes: [[-4, 7], [-2, 7]], stripe: [[-4, 6], [-2, 6]] }))).toEqual([0, 0.25]);
  });
});

describe("curveLayerBitmap", () => {
  it("decodes splat1 into one layer byte per node", () => {
    const curve = curveFixture({
      nodes: [[0, 0], [1, 0], [2, 0], [3, 0], [4, 0]],
      stripe: [[0, -1], [1, -1], [2, -1], [3, -1], [4, -1]],
      splat1: [[1, 2]],
    });
    expect([...curveLayerBitmap(curve)]).toEqual([0, 1, 1, 0, 0]);
  });

  it("is all zeroes without any splat1 run", () => {
    expect([...curveLayerBitmap(curveFixture({ splat1: [] }))]).toEqual([0, 0]);
  });
});

describe("bandTriangleTransform", () => {
  it("refuses a degenerate triangle instead of returning NaNs", () => {
    expect(bandTriangleTransform([
      { ix: 0, iy: 0, x: 50, y: 50 },
      { ix: 0, iy: 0, x: 70, y: 50 },
      { ix: 0, iy: 8, x: 50, y: 60 },
    ])).toBeNull();
    expect(bandTriangleTransform([
      { ix: 0, iy: 0, x: 50, y: 50 },
      { ix: 2, iy: 0, x: 70, y: 50 },
      { ix: 0, iy: 0, x: 50, y: 60 },
    ])).toBeNull();
  });
});

describe("drawTerrainCurves", () => {
  it("maps a straight horizontal band through one affine per triangle", () => {
    const textures = loadedTextures();
    const { ctx, fills, patterns } = makeRecorder();
    drawTerrainCurves(ctx, camera, [curveTerrain(curveFixture())], textures, 100, 100);

    // world (0, 0) -> (50, 50), (2, 0) -> (70, 50) and the stripe row 1 m down -> y 60; the layer is
    // 8 x 8 px and its columns are [0, 2] texture pixels: 2 px -> 20 canvas px, 8 px -> 10 canvas px.
    const expected = { a: 10, b: 0, c: 0, d: 1.25, e: 50, f: 50 };
    expect(patterns[0].transforms).toEqual([expected, expected]);
    expect(patterns[0].repeat).toBe("repeat");
    expect(patterns[0].source).toBe((textures.get("grass.png") as GroundTexture).source);
    expect(fills.map((fill) => fill.points)).toEqual([
      [[50, 50], [70, 50], [50, 60]],
      [[70, 50], [70, 60], [50, 60]],
    ]);
    expect(fills.every((fill) => fill.style === patterns[0])).toBe(true);
    expect(allFinite(fills)).toBe(true);
  });

  it("fills the curve band with the layer a splat1 run starts", () => {
    const curve = curveFixture({
      nodes: [[0, 0], [2, 0], [4, 0]],
      stripe: [[0, -1], [2, -1], [4, -1]],
      splat1: [[1, 1]],
    });
    const { ctx, fills, patterns } = makeRecorder();
    drawTerrainCurves(ctx, camera, [curveTerrain(curve)], loadedTextures(), 100, 100);

    // The run starts at node 1, so the second quad (nodes 1 -> 2) is the one drawn with textures[1].
    expect(patterns[0].transforms).toHaveLength(2);
    expect(patterns[1].transforms).toHaveLength(2);
    expect(fills.map((fill) => fill.style)).toEqual([patterns[0], patterns[0], patterns[1], patterns[1]]);
    expect(fills[2].points[0]).toEqual([70, 50]);
  });

  it("cuts a clamp layer at u = 1 and fills the far half with the rightmost texel column", () => {
    stubPixelSource(8);
    const curve = curveFixture({
      textures: [
        { texture: "grass.png", wrap: "clamp" },
        { texture: "rock.png", wrap: "repeat" },
      ],
      // u[1] = 4, so `u * texW` runs from 0 to 32 and Unity's u = 1 falls at texture x = 8, a quarter
      // of the way along the quad.
      uScale: 2,
    });
    const { ctx, fills, gradients, patterns } = makeRecorder();
    drawTerrainCurves(ctx, camera, [curveTerrain(curve)], loadedTextures(), 100, 100);

    expect(fills.map((fill) => fill.points)).toEqual([
      [[50, 50], [55, 50], [55, 57.5], [50, 60]],
      [[55, 50], [70, 50], [55, 57.5]],
      [[55, 60], [50, 60], [55, 57.5]],
      // The far half of the second triangle is the trapezium the straight `u = 1` cut leaves: its
      // fourth corner is where that cut crosses the shared diagonal (nodes[1] -> stripe[0]).
      [[70, 50], [70, 60], [55, 60], [55, 57.5]],
    ]);
    // One gradient per triangle (the axis comes from the triangle's own affine), both the same here
    // because this quad's two rows are parallel.
    expect(gradients).toHaveLength(2);
    expect(fills.map((fill) => fill.style)).toEqual([patterns[0], gradients[0], patterns[0], gradients[1]]);
    // The gradient runs along the triangle's own v axis: anchored on the row of its first corner
    // (v = 1 there) and stepped to v = 0, with the rightmost column sampled at each texel's centre.
    expect([gradients[0].from, gradients[0].to]).toEqual([[50, 50], [50, 60]]);
    expect([gradients[1].from, gradients[1].to]).toEqual([[70, 50], [70, 60]]);
    expect(gradients[0].stops).toEqual([
      { offset: 0.0625, colour: "rgba(10,0,0,1)" },
      { offset: 0.1875, colour: "rgba(11,0,0,1)" },
      { offset: 0.3125, colour: "rgba(12,0,0,1)" },
      { offset: 0.4375, colour: "rgba(13,0,0,1)" },
      { offset: 0.5625, colour: "rgba(14,0,0,1)" },
      { offset: 0.6875, colour: "rgba(15,0,0,1)" },
      { offset: 0.8125, colour: "rgba(16,0,0,1)" },
      { offset: 0.9375, colour: "rgba(17,0,0,1)" },
    ]);
    expect(allFinite(fills)).toBe(true);
  });

  it("tilts each clamp gradient to its own triangle when the band bends", () => {
    stubPixelSource(8);
    const curve = curveFixture({
      textures: [
        { texture: "grass.png", wrap: "clamp" },
        { texture: "rock.png", wrap: "repeat" },
      ],
      // A bent quad: the stripe row is not parallel to the nodes row, so the two triangles of the quad
      // have different affine maps and the gradient must follow the triangle's own v axis rather than
      // the quad's midline.
      stripe: [[0, -1], [2.4, -0.6]],
      uScale: 1,
    });
    const { ctx, gradients } = makeRecorder();
    drawTerrainCurves(ctx, camera, [curveTerrain(curve)], loadedTextures(), 100, 100);

    // Screen: nodes (50, 50) -> (70, 50), stripe (50, 60) -> (74, 56); texture pixels 8 x 8 with
    // u[1] = 2, so the quad spans ix 0..16 and Unity's clamp starts at ix = 8.
    // Triangle (nodes[0], nodes[1], stripe[0]) is the parallelogram one: v = iy / 8 straight down.
    expect([gradients[0].from, gradients[0].to]).toEqual([[50, 50], [50, 60]]);
    // Triangle (nodes[1], stripe[1], stripe[0]) maps (ix, iy) = (16, 0) -> (70, 50), (16, 8) -> (74, 56)
    // and (0, 8) -> (50, 60), i.e. x = 1.5*ix + 0.5*iy + 46, y = -0.25*ix + 0.75*iy + 54; its iy
    // gradient is (0.2, 1.2) / 1.48, so the axis starts at its (16, 0) corner and is tilted.
    expect(gradients[1].from[0]).toBeCloseTo(70, 6);
    expect(gradients[1].from[1]).toBeCloseTo(50, 6);
    expect(gradients[1].to[0]).toBeCloseTo(70 + (8 * 0.2) / 1.48, 6);
    expect(gradients[1].to[1]).toBeCloseTo(50 + (8 * 1.2) / 1.48, 6);
    // The quad's midline would be (60, 50) -> (62, 58); the triangle's own axis is not that.
    expect(gradients[1].to[0]).not.toBeCloseTo(62, 3);
  });

  it("keeps the whole quad textured while u stays at or below 1", () => {
    const curve = curveFixture({
      textures: [
        { texture: "grass.png", wrap: "clamp" },
        { texture: "rock.png", wrap: "repeat" },
      ],
    });
    const { ctx, fills, gradients } = makeRecorder();
    drawTerrainCurves(ctx, camera, [curveTerrain(curve)], loadedTextures(), 100, 100);

    expect(gradients).toHaveLength(0);
    expect(fills.map((fill) => fill.points)).toEqual([
      [[50, 50], [70, 50], [50, 60]],
      [[70, 50], [70, 60], [50, 60]],
    ]);
  });

  it("draws no band for a terrain without a curve, without textures or with unloaded art", () => {
    const bare: LevelTerrain = { position: [0, 0, 0], depth: 10, loops: [] };
    for (const terrains of [[bare], [curveTerrain(curveFixture())]]) {
      for (const textures of [null, new Map() as GroundTextureSet]) {
        const { ctx, fills, patterns } = makeRecorder();
        drawTerrainCurves(ctx, camera, terrains, textures, 100, 100);
        expect(fills).toHaveLength(0);
        expect(patterns).toHaveLength(0);
      }
    }
  });

  it("skips a degenerate quad instead of emitting NaNs", () => {
    const curve = curveFixture({
      nodes: [[0, 0], [0, 0], [2, 0]],
      stripe: [[0, -1], [0, -1], [2, -1]],
    });
    const { ctx, fills, patterns } = makeRecorder();
    drawTerrainCurves(ctx, camera, [curveTerrain(curve)], loadedTextures(), 100, 100);

    // The first quad's two nodes coincide, so only the second quad's two triangles are drawn.
    expect(fills.map((fill) => fill.points)).toEqual([
      [[50, 50], [70, 50], [50, 60]],
      [[70, 50], [70, 60], [50, 60]],
    ]);
    expect(allFinite(fills)).toBe(true);
    expect(patterns[0].transforms.every((transform) => Object.values(transform).every(Number.isFinite))).toBe(true);
  });

  it("draws nothing for a curve whose nodes all coincide", () => {
    const curve = curveFixture({
      nodes: [[1, 1], [1, 1], [1, 1]],
      stripe: [[1, 0], [1, 0], [1, 0]],
    });
    const { ctx, fills, patterns } = makeRecorder();
    drawTerrainCurves(ctx, camera, [curveTerrain(curve)], loadedTextures(), 100, 100);

    expect(fills).toHaveLength(0);
    expect(patterns[0].transforms).toHaveLength(0);
  });

  it("draws every terrain's fills before any terrain's band", () => {
    const { ctx, ops } = makeRecorder();
    drawFrame(ctx, camera, [], null, [], undefined, undefined, null, null, null, [curveTerrain(curveFixture())], loadedTextures());

    // The loop fill `drawTerrain` paints comes first; the band's pattern transform comes after it.
    const firstFill = ops.indexOf("fill");
    const firstTransform = ops.indexOf("setTransform");
    expect(firstFill).toBeGreaterThan(-1);
    expect(firstTransform).toBeGreaterThan(firstFill);
  });
});

describe("curveTextureNames", () => {
  it("lists each curve texture once, in document order", () => {
    const second = curveTerrain(curveFixture({
      textures: [
        { texture: "rock.png", wrap: "repeat" },
        { texture: "rock.png", wrap: "clamp" },
      ],
    }));
    expect(curveTextureNames([curveTerrain(curveFixture()), second])).toEqual(["grass.png", "rock.png"]);
  });

  it("is empty for a terrain without a curve", () => {
    expect(curveTextureNames([{ position: [0, 0, 0], depth: 10, loops: [] }])).toEqual([]);
  });
});
