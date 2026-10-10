import { describe, expect, it } from "vitest";
import { createCamera } from "./camera";
import {
  LEVEL_PROPS_URL,
  drawProps,
  loadLevelProps,
  parseLevelProps,
  type DecorationQuad,
  type LevelPropQuad,
  type LevelPropsSet,
} from "./levelProps";
import type { LevelProp } from "@/schema/levelContent";

/** One decorated quad: a 64x64 atlas cell, 0.99 world metres, no frame of its own. */
const quad: LevelPropQuad = {
  atlas: "Props_Generic_Sheet_01.png",
  x: 96,
  y: 32,
  w: 64,
  h: 64,
  cx: 0,
  cy: 0,
  sx: 0.99,
  sy: 0.99,
  dx: 0,
  dy: 0,
  dz: 0,
  rotation: 0,
  scaleX: 1,
  scaleY: 1,
  parallax: { x: 0, y: 0 },
};

/** A backdrop layer: a colour fill that takes 0.8 of the camera's own movement with it. */
const sky: LevelPropQuad = {
  color: [0.2, 0.5333334, 0.2666667, 1],
  cx: 0,
  cy: 0,
  sx: 3.3333,
  sy: 3.3333,
  dx: -7,
  dy: 24.9311,
  dz: 20,
  rotation: 0,
  scaleX: 65,
  scaleY: 12.4687,
  parallax: { x: 0.8, y: 0 },
};

const manifest = {
  format: "pigforge.level-props",
  schemaVersion: 2,
  unitsPerPixel: 20 / 768,
  atlases: { "Props_Generic_Sheet_01.png": { width: 512, height: 512 } },
  props: {
    Grass_02: { quads: [quad] },
    background_jungle_01_set: { quads: [sky, { ...quad, dx: 4, dy: 2, rotation: 0.25, scaleX: 2, scaleY: 0.5 }] },
  },
};

const prop = (id: string, x: number, y: number, z: number, rotation = 0, scaleX = 1, scaleY = 1): LevelProp => ({
  id,
  x,
  y,
  z,
  rotation,
  scaleX,
  scaleY,
});

/** One quad of a placed prop, as the painter's depth buckets hand it over. */
const decoration = (id: string, x: number, y: number, z: number, rotation = 0, scaleX = 1, scaleY = 1): DecorationQuad => ({
  prop: prop(id, x, y, z, rotation, scaleX, scaleY),
  quad: set_quads(id),
});

/** The manifest's first quad for `id` (every fixture prop has at least one). */
function set_quads(id: string): LevelPropQuad {
  if (id === "background_jungle_01_set") return sky;
  if (id === "framed") return { ...quad, dx: 1, dy: -0.5, rotation: 0.25, scaleX: 2, scaleY: 0.5, cx: 0.5, cy: -0.25, sx: 0.5, sy: 1.25 };
  return quad;
}

function makeRecorder() {
  const ops: Array<{ op: string; args: unknown[] }> = [];
  const record = (op: string) => (...args: unknown[]): void => {
    ops.push({ op, args });
  };
  const rec = {
    save: record("save"),
    restore: record("restore"),
    translate: record("translate"),
    rotate: record("rotate"),
    scale: record("scale"),
    drawImage: record("drawImage"),
    fillRect: record("fillRect"),
    fillStyle: "",
  };
  return { ctx: rec as unknown as CanvasRenderingContext2D, ops };
}

describe("parseLevelProps", () => {
  it("accepts the generated manifest", () => {
    const parsed = parseLevelProps(manifest);
    expect(parsed?.props.get("Grass_02")).toEqual([quad]);
    expect(parsed?.props.get("background_jungle_01_set")?.length).toBe(2);
    expect(parsed?.atlasNames).toEqual(["Props_Generic_Sheet_01.png"]);
  });

  it("rejects a document it cannot trust", () => {
    expect(parseLevelProps(null)).toBeNull();
    expect(parseLevelProps([])).toBeNull();
    expect(parseLevelProps({ ...manifest, format: "pigforge.part-content" })).toBeNull();
    expect(parseLevelProps({ ...manifest, schemaVersion: 1 })).toBeNull();
    expect(parseLevelProps({ ...manifest, props: {} })).toBeNull();
    expect(parseLevelProps({ ...manifest, atlases: {} })).toBeNull();
    expect(parseLevelProps({ ...manifest, props: { Grass_02: { quads: [] } } })).toBeNull();
  });

  it("drops a malformed prop and an undeclared atlas", () => {
    const withBad = { ...manifest, props: { ...manifest.props, Broken: { quads: [{ ...quad, sx: 0 }] } } };
    expect(parseLevelProps(withBad)?.props.has("Broken")).toBe(false);
    expect(parseLevelProps(withBad)?.props.has("Grass_02")).toBe(true);
    const undeclared = { ...manifest, props: { ...manifest.props, Other: { quads: [{ ...quad, atlas: "Missing.png" }] } } };
    expect(parseLevelProps(undeclared)).toBeNull();
    // A colour quad needs no atlas, so it stays valid on its own.
    expect(parseLevelProps({ ...manifest, props: { Sky: { quads: [sky] } } })?.atlasNames).toEqual([]);
  });
});

describe("loadLevelProps", () => {
  const loader = async (url: string): Promise<CanvasImageSource> => ({ url }) as unknown as CanvasImageSource;

  it("fetches the manifest and its atlases", async () => {
    const fetched: string[] = [];
    const fakeFetch = async (url: string): Promise<Response> => {
      fetched.push(url);
      return { ok: true, json: async () => manifest } as unknown as Response;
    };
    const original = globalThis.fetch;
    globalThis.fetch = fakeFetch as unknown as typeof fetch;
    try {
      const set = await loadLevelProps(LEVEL_PROPS_URL, loader);
      expect(set?.props.size).toBe(2);
      expect(set?.atlases.get("Props_Generic_Sheet_01.png")).toEqual({ url: "/assets/original/Props_Generic_Sheet_01.png" });
      expect(fetched).toEqual([LEVEL_PROPS_URL]);
    } finally {
      globalThis.fetch = original;
    }
  });

  it("returns null when the manifest is absent, and reuses a shared image", async () => {
    const original = globalThis.fetch;
    globalThis.fetch = (async () => ({ ok: false } as unknown as Response)) as unknown as typeof fetch;
    try {
      expect(await loadLevelProps(LEVEL_PROPS_URL, loader)).toBeNull();
    } finally {
      globalThis.fetch = original;
    }

    const shared = new Map<string, CanvasImageSource>([["Props_Generic_Sheet_01.png", { url: "shared" } as unknown as CanvasImageSource]]);
    let loads = 0;
    globalThis.fetch = (async () => ({ ok: true, json: async () => manifest } as unknown as Response)) as unknown as typeof fetch;
    try {
      const set = await loadLevelProps(LEVEL_PROPS_URL, async (url) => {
        loads += 1;
        return { url } as unknown as CanvasImageSource;
      }, shared);
      expect(loads).toBe(0);
      expect(set?.atlases.get("Props_Generic_Sheet_01.png")).toEqual({ url: "shared" });
    } finally {
      globalThis.fetch = original;
    }
  });
});

describe("drawProps", () => {
  const camera = createCamera();
  const set: LevelPropsSet = {
    atlases: new Map([["Props_Generic_Sheet_01.png", {} as unknown as CanvasImageSource]]),
    props: new Map([
      ["Grass_02", [quad]],
      ["framed", [{ ...quad, dx: 1, dy: -0.5, rotation: 0.25, scaleX: 2, scaleY: 0.5, cx: 0.5, cy: -0.25, sx: 0.5, sy: 1.25 }]],
      ["background_jungle_01_set", [sky, { ...quad, dx: 1, dy: -0.5, rotation: 0.25, scaleX: 2, scaleY: 0.5, cx: 0.5, cy: -0.25, sx: 0.5, sy: 1.25 }]],
    ]),
  };

  it("places a quad through its own frame and its prop's", () => {
    const { ctx, ops } = makeRecorder();
    // 4 world metres right of the camera centre, 2 above it; the camera is 36 px per metre and the
    // canvas 800 x 600, so the prop origin lands on (400 + 144, 300 - 72).
    drawProps(ctx, camera, [decoration("framed", 8, 4, 0, 0.5, -1, 2)], set, 800, 600);

    expect(ops.map((entry) => entry.op)).toEqual([
      "save", "translate", "scale", "rotate", "scale", "translate", "rotate", "scale", "translate", "drawImage", "restore",
    ]);
    expect(ops[1].args).toEqual([544, 228]);
    expect(ops[2].args).toEqual([36, 36]);
    // A world angle is a negated canvas rotation; the instance's own localScale is the next scale.
    expect(ops[3].args).toEqual([-0.5]);
    expect(ops[4].args).toEqual([-1, 2]);
    // The quad's own position, rotated about z and the art's pivot offset, all in world metres.
    expect(ops[5].args).toEqual([1, 0.5]);
    expect(ops[6].args).toEqual([-0.25]);
    expect(ops[7].args).toEqual([2, 0.5]);
    expect(ops[8].args).toEqual([0.5, 0.25]);
    // The atlas rect the manifest names, then the quad drawn about its own centre in world metres.
    expect(ops[9].args.slice(1)).toEqual([96, 32, 64, 64, -0.25, -0.625, 0.5, 1.25]);
  });

  it("fills a colour quad instead of drawing an image", () => {
    const { ctx, ops } = makeRecorder();
    // The backdrop's sky, then one of its textured quads: the manifest's own order.
    drawProps(ctx, camera, [decoration("background_jungle_01_set", 0, 0, 0), decoration("Grass_02", 0, 0, 0)], set, 800, 600);
    expect(ops[0].op).toBe("save");
    // The sky is a fill in its material's `_Color`: the quad's own size, stretched by its own scale
    // (65 x 12.4687) and its prop instance's (1 x 1).
    const fills = ops.filter((entry) => entry.op === "fillRect");
    expect(fills.length).toBe(1);
    expect(fills[0].args[2]).toBeCloseTo(3.3333, 4);
    expect(fills[0].args[3]).toBeCloseTo(3.3333, 4);
    expect(ops.filter((entry) => entry.op === "scale").map((entry) => entry.args)).toContainEqual([65, 12.4687]);
    expect(ops.filter((entry) => entry.op === "drawImage").length).toBe(1);
    expect(ops[ops.length - 1].op).toBe("restore");
  });

  it("moves a parallax layer with the camera, and a plain quad not at all", () => {
    // `+ 0` folds the negative zero a negated offset produces into a plain 0.
    const translations = (ops: readonly { op: string; args: unknown[] }[]): number[][] =>
      ops.filter((entry) => entry.op === "translate").map((entry) => (entry.args as number[]).map((value) => value + 0));

    const moved = makeRecorder();
    drawProps(moved.ctx, { x: 14, y: 6, scale: 36 }, [decoration("background_jungle_01_set", 0, 0, 0)], set, 800, 600, { x: 4, y: 2 });
    // 10 metres of camera x at 0.8 (the sky layer); its y parallax is 0, so only x moves.
    expect(translations(moved.ops)[1]).toEqual([8, 0]);

    const still = makeRecorder();
    drawProps(still.ctx, { x: 14, y: 6, scale: 36 }, [decoration("Grass_02", 0, 0, 0)], set, 800, 600, { x: 4, y: 2 });
    // The origin, the quad's own (zero) offset and the art's (zero) pivot: a decoration never parallaxes.
    expect(translations(still.ops)).toEqual([[-104, 516], [0, 0], [0, 0]]);
  });

  it("skips a quad whose atlas did not load, and an empty bucket", () => {
    const { ctx, ops } = makeRecorder();
    drawProps(ctx, camera, [], set, 800, 600);
    expect(ops).toEqual([]);

    const noAtlas: LevelPropsSet = { atlases: new Map(), props: set.props };
    expect(() => drawProps(ctx, camera, [decoration("Grass_02", 0, 0, 0)], noAtlas, 800, 600)).not.toThrow();
    expect(ops).toEqual([]);

    expect(() => drawProps(ctx, camera, [decoration("Grass_02", 0, 0, 0)], null, 800, 600)).not.toThrow();
  });
});
