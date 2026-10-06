import { afterEach, describe, expect, it, vi } from "vitest";
import { createCamera, worldToScreen } from "./camera";
import { drawFrame } from "./draw";
import {
  GROUND_FILL,
  drawTerrain,
  groundPatternTransform,
  groundTextureNames,
  loadGroundTextures,
  loopToWorld,
  type GroundTexture,
  type GroundTextureSet,
} from "./terrain";
import type { DrawEntity } from "@/schema/types";
import type { LevelTerrain, LevelTerrainFill } from "@/schema/levelContent";

const terrain: LevelTerrain = { position: [-2.79, 9.02, 0], depth: 10, loops: [[[0, 0], [1, 0], [0, 1]]] };

const block: DrawEntity = { entityId: 1, partTypeId: 1, x: 4, y: 2, yaw: 0, scale: 1, vx: 0, vy: 0, bodyId: 12, active: false };

const fill: LevelTerrainFill = {
  texture: "Ground_Rocks_Texture.png",
  color: [131, 131, 131, 255],
  tileOffset: [0, 6.2],
  tileSize: [5, 5],
};

// A v3 terrain always carries its fill, so the fixture's type keeps it required: `groundPatternTransform`
// is only ever called for a terrain that draws a texture.
const textured: LevelTerrain & { fill: LevelTerrainFill } = { ...terrain, collider: true, fill };

function makeRecorder() {
  const ops: string[] = [];
  const fills: unknown[] = [];
  const patterns: Array<{ source: unknown; repeat: string; transforms: unknown[] }> = [];
  let fillStyle: unknown = "";
  const recorder = {
    canvas: { clientWidth: 800, clientHeight: 600, width: 800, height: 600 },
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
    beginPath: () => { ops.push("beginPath"); },
    moveTo: () => { ops.push("moveTo"); },
    lineTo: () => { ops.push("lineTo"); },
    closePath: () => { ops.push("closePath"); },
    fill: () => {
      ops.push("fill");
      fills.push(fillStyle);
    },
    stroke: () => { ops.push("stroke"); },
    arc: () => { ops.push("arc"); },
    drawImage: () => { ops.push("drawImage"); },
    fillText: () => { ops.push("fillText"); },
    save: () => {},
    restore: () => {},
    translate: () => {},
    rotate: () => {},
    scale: () => {},
    setLineDash: () => {},
    createRadialGradient: () => ({ addColorStop: () => {} }),
    createPattern: (source: unknown, repeat: string) => {
      const pattern = { source, repeat, transforms: [] as unknown[], setTransform: (transform: unknown) => { pattern.transforms.push(transform); } };
      patterns.push(pattern);
      return pattern;
    },
  };
  return { ctx: recorder as unknown as CanvasRenderingContext2D, ops, fills, patterns };
}

function count(ops: readonly string[], op: string): number {
  return ops.filter((entry) => entry === op).length;
}

/** How many `op`s one frame draws on top of the same frame without terrain (the grid aside). */
function groundOps(terrains: readonly LevelTerrain[], op: string): number {
  const bare = makeRecorder();
  drawFrame(bare.ctx, createCamera(), [], null, []);
  const ground = makeRecorder();
  drawFrame(ground.ctx, createCamera(), [], null, [], undefined, undefined, null, null, null, terrains);
  return count(ground.ops, op) - count(bare.ops, op);
}

interface OffscreenRecord {
  elements: unknown[];
  draws: Array<{ image: unknown; args: number[] }>;
  operations: string[];
  rects: Array<{ style: unknown; args: number[] }>;
}

/** Stands in for the browser's offscreen canvas: records what the tinter does to a tile. */
function stubOffscreenCanvas(): OffscreenRecord {
  const record: OffscreenRecord = { elements: [], draws: [], operations: [], rects: [] };
  let composite = "source-over";
  let style: unknown = "";
  const context = {
    get globalCompositeOperation(): string {
      return composite;
    },
    set globalCompositeOperation(value: string) {
      composite = value;
      record.operations.push(value);
    },
    set fillStyle(value: unknown) {
      style = value;
    },
    drawImage: (image: unknown, ...args: number[]) => {
      record.draws.push({ image, args });
    },
    fillRect: (...args: number[]) => {
      record.rects.push({ style, args });
    },
  };
  vi.stubGlobal("document", {
    createElement: () => {
      const element = { width: 0, height: 0, getContext: () => context };
      record.elements.push(element);
      return element;
    },
  });
  return record;
}

/** A ground texture whose bitmap is a distinguishable sentinel. */
function texture(sentinel: object, width = 512, height = 512): GroundTexture {
  return { source: sentinel as unknown as CanvasImageSource, width, height };
}

afterEach(() => {
  vi.unstubAllGlobals();
});

describe("loopToWorld", () => {
  it("translates the terrain's local loop by its position", () => {
    expect(loopToWorld(terrain, [[0, 0], [1, 0], [0, 1]])).toEqual([
      { x: -2.79, y: 9.02 },
      { x: -1.79, y: 9.02 },
      { x: -2.79, y: 10.02 },
    ]);
  });

  it("keeps the terrain's own z out of the plane projection", () => {
    const raised: LevelTerrain = { ...terrain, position: [-2.79, 9.02, 40] };
    expect(loopToWorld(raised, [[2, 3]])).toEqual([{ x: -0.79, y: 12.02 }]);
  });
});

describe("drawFrame terrain", () => {
  it("fills every loop with the static ground green and closes its outline", () => {
    const { ctx, fills } = makeRecorder();
    drawFrame(ctx, createCamera(), [], null, [], undefined, undefined, null, null, null, [terrain]);
    expect(groundOps([terrain], "moveTo")).toBe(1);
    expect(groundOps([terrain], "lineTo")).toBe(2);
    expect(groundOps([terrain], "closePath")).toBe(1);
    expect(groundOps([terrain], "fill")).toBe(1);
    expect(fills).toEqual(["#5c6b52"]);
  });

  it("fills once per loop across terrains", () => {
    const second: LevelTerrain = { position: [10, 0, 0], depth: 5, loops: [[[0, 0], [1, 0], [0, 1]], [[0, 0], [2, 0], [0, 2], [1, 1]]] };
    expect(groundOps([terrain, second], "fill")).toBe(3);
    expect(groundOps([terrain, second], "moveTo")).toBe(3);
    expect(groundOps([terrain, second], "lineTo")).toBe(7);
  });

  it("draws the ground after the background grid and before every entity", () => {
    const { ctx, ops } = makeRecorder();
    drawFrame(ctx, createCamera(), [block], null, [], undefined, undefined, null, null, null, [terrain]);
    expect(ops.indexOf("fill")).toBeGreaterThan(ops.indexOf("stroke"));
    expect(ops.indexOf("fill")).toBeLessThan(ops.lastIndexOf("fillRect"));
    expect(ops[0]).toBe("fillRect");
  });

  it("draws no ground when the level has no terrain", () => {
    const { ctx, ops } = makeRecorder();
    drawFrame(ctx, createCamera(), [], null, []);
    expect(ops).not.toContain("fill");
    expect(ops).not.toContain("closePath");
  });
});

describe("groundPatternTransform", () => {
  const camera = createCamera();
  const size = { width: 512, height: 512 };

  it("anchors the tiling on the terrain's own frame, not the world origin", () => {
    const transform = groundPatternTransform(camera, textured, size, 800, 600);
    // The UVs come from the terrain's local vertices (`LevelLoader.ReadMesh`), so the tile's own
    // top-left corner sits at the terrain's origin plus the tile offset -- Unity's v runs up while a
    // canvas' rows run down, so pattern row 0 is the tile's *top* edge.
    const corner = worldToScreen(
      camera,
      textured.position[0] + fill.tileOffset[0],
      textured.position[1] + fill.tileOffset[1] + fill.tileSize[1],
      800,
      600,
    );
    expect(transform.e).toBeCloseTo(corner.x, 10);
    expect(transform.f).toBeCloseTo(corner.y, 10);
    expect(transform.b).toBe(0);
    expect(transform.c).toBe(0);
  });

  it("shifts with the terrain's own position", () => {
    const moved = groundPatternTransform(camera, { ...textured, position: [-2.79 + 5, 9.02, 0] }, size, 800, 600);
    const base = groundPatternTransform(camera, textured, size, 800, 600);
    expect(moved.e - base.e).toBeCloseTo(5 * camera.scale, 10);
    expect(moved.f).toBeCloseTo(base.f, 10);
  });

  it("maps one texture repeat onto tileSize world metres", () => {
    const transform = groundPatternTransform(camera, textured, size, 800, 600);
    // One repeat to the right and one down in pattern pixels is +tileSize.x world metres and
    // -tileSize.y, i.e. the tile's bottom-right corner.
    const bottomRight = worldToScreen(
      camera,
      textured.position[0] + fill.tileOffset[0] + fill.tileSize[0],
      textured.position[1] + fill.tileOffset[1],
      800,
      600,
    );
    expect(transform.e + transform.a * size.width).toBeCloseTo(bottomRight.x, 10);
    expect(transform.f + transform.d * size.height).toBeCloseTo(bottomRight.y, 10);
    expect(transform.a).toBeCloseTo((fill.tileSize[0] * camera.scale) / size.width, 10);
    expect(transform.d).toBeCloseTo((fill.tileSize[1] * camera.scale) / size.height, 10);
  });

  it("scales with the camera", () => {
    const zoomed = groundPatternTransform({ ...camera, scale: camera.scale * 2 }, textured, size, 800, 600);
    expect(zoomed.a).toBeCloseTo(groundPatternTransform(camera, textured, size, 800, 600).a * 2, 10);
  });
});

describe("groundTextureNames", () => {
  it("lists each level texture once, in document order", () => {
    const second: LevelTerrain = { ...textured, fill: { ...fill, texture: "Ground_Ice_Texture.png" } };
    const third: LevelTerrain = { ...textured, fill: { ...fill, texture: "Ground_Ice_Texture.png" } };
    expect(groundTextureNames([textured, second, third, terrain])).toEqual(["Ground_Rocks_Texture.png", "Ground_Ice_Texture.png"]);
  });

  it("is empty for a v2 level", () => {
    expect(groundTextureNames([terrain])).toEqual([]);
  });
});

describe("loadGroundTextures", () => {
  it("loads every name and keeps the bitmap's pixel size", async () => {
    const loaded = await loadGroundTextures(["a.png", "b.png"], "/base/", async (url) => ({ url, width: 4, height: 2 }) as unknown as CanvasImageSource);
    expect([...loaded.keys()]).toEqual(["a.png", "b.png"]);
    expect(loaded.get("a.png")).toEqual({ source: { url: "/base/a.png", width: 4, height: 2 }, width: 4, height: 2 });
  });

  it("drops a texture that failed to load instead of failing the level", async () => {
    const loaded = await loadGroundTextures(["a.png", "missing.png"], "/base/", async (url) => {
      if (url.endsWith("missing.png")) {
        throw new Error("404");
      }
      return { width: 8, height: 8 } as unknown as CanvasImageSource;
    });
    expect([...loaded.keys()]).toEqual(["a.png"]);
  });
});

describe("drawTerrain with a fill", () => {
  const sentinel = { name: "ground" };

  it("paints the ground with the textured pattern, multiplied by the terrain's own colour", () => {
    const offscreen = stubOffscreenCanvas();
    const { ctx, fills, patterns } = makeRecorder();
    // A fresh texture object each run: the tint cache is keyed by it.
    const textures: GroundTextureSet = new Map([["Ground_Rocks_Texture.png", texture(sentinel)] ]);

    drawTerrain(ctx, createCamera(), [textured], textures, 800, 600);

    // The tile is the bitmap, then the level's own RGBA quad multiplied over it -- `fill.shader` is
    // `tex2D(_MainTex, uv) * _Color`, with `ReadColor`'s own 0.003921569f factor on the bytes.
    expect(offscreen.draws).toEqual([{ image: sentinel, args: [0, 0, 512, 512] }]);
    expect(offscreen.operations).toEqual(["multiply", "source-over"]);
    expect(offscreen.rects).toEqual([{ style: "rgba(131,131,131,1)", args: [0, 0, 512, 512] }]);

    // The pattern repeats the *tinted tile*, not the raw bitmap, and carries the UV transform.
    const pattern = patterns[0];
    expect(pattern.repeat).toBe("repeat");
    expect(pattern.source).toBe(offscreen.elements[0]);
    expect(pattern.transforms).toEqual([groundPatternTransform(createCamera(), textured, { width: 512, height: 512 }, 800, 600)]);
    expect(fills).toEqual([pattern]);
  });

  it("tints a (texture, colour) pair once across frames", () => {
    const offscreen = stubOffscreenCanvas();
    const textures: GroundTextureSet = new Map([["Ground_Rocks_Texture.png", texture(sentinel)]]);
    const first = makeRecorder();
    const second = makeRecorder();

    drawTerrain(first.ctx, createCamera(), [textured], textures, 800, 600);
    drawTerrain(second.ctx, createCamera(), [textured], textures, 800, 600);

    expect(offscreen.elements).toHaveLength(1);
    expect(second.patterns[0].source).toBe(offscreen.elements[0]);
  });

  it("follows the camera: the same terrain repaints the pattern transform each frame", () => {
    stubOffscreenCanvas();
    const textures: GroundTextureSet = new Map([["Ground_Rocks_Texture.png", texture(sentinel)]]);
    const { ctx, patterns } = makeRecorder();

    drawTerrain(ctx, createCamera(), [textured], textures, 800, 600);
    drawTerrain(ctx, { x: 10, y: 2, scale: 36 }, [textured], textures, 800, 600);

    expect(patterns).toHaveLength(2);
    expect(patterns[1].transforms[0]).not.toEqual(patterns[0].transforms[0]);
  });

  it("falls back to the flat ground colour when the texture is missing", () => {
    stubOffscreenCanvas();
    const { ctx, fills, patterns } = makeRecorder();

    drawTerrain(ctx, createCamera(), [textured], new Map(), 800, 600);

    expect(patterns).toEqual([]);
    expect(fills).toEqual([GROUND_FILL]);
  });

  it("paints a v2 terrain (no fill) in the flat ground colour", () => {
    const { ctx, fills } = makeRecorder();
    drawTerrain(ctx, createCamera(), [terrain], null, 800, 600);
    expect(fills).toEqual([GROUND_FILL]);
  });
});
