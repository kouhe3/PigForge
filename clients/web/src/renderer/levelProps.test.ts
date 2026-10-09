import { describe, expect, it } from "vitest";
import { createCamera } from "./camera";
import {
  LEVEL_PROPS_URL,
  drawProps,
  loadLevelProps,
  parseLevelProps,
  sortPropsForDepth,
  type LevelPropArt,
  type LevelPropsSet,
} from "./levelProps";
import type { LevelProp } from "@/schema/levelContent";

const art: LevelPropArt = { atlas: "Props_Generic_Sheet_01.png", x: 96, y: 32, w: 64, h: 64, cx: 0, cy: 0, sx: 0.99, sy: 0.99 };

const manifest = {
  format: "pigforge.level-props",
  schemaVersion: 1,
  unitsPerPixel: 20 / 768,
  atlases: { "Props_Generic_Sheet_01.png": { width: 512, height: 512 } },
  props: { Grass_02: art, Star_01: { ...art, x: 0, y: 0, cx: 0.5, cy: -0.25, sx: 0.5, sy: 1.25 } },
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
  };
  return { ctx: rec as unknown as CanvasRenderingContext2D, ops };
}

describe("parseLevelProps", () => {
  it("accepts the generated manifest", () => {
    expect(parseLevelProps(manifest)?.props.get("Grass_02")).toEqual(art);
    expect(parseLevelProps(manifest)?.atlasNames).toEqual(["Props_Generic_Sheet_01.png"]);
  });

  it("rejects a document it cannot trust", () => {
    expect(parseLevelProps(null)).toBeNull();
    expect(parseLevelProps([])).toBeNull();
    expect(parseLevelProps({ ...manifest, format: "pigforge.part-content" })).toBeNull();
    expect(parseLevelProps({ ...manifest, schemaVersion: 9 })).toBeNull();
    expect(parseLevelProps({ ...manifest, props: {} })).toBeNull();
    expect(parseLevelProps({ ...manifest, atlases: {} })).toBeNull();
  });

  it("drops a malformed entry and an undeclared atlas", () => {
    const withBad = { ...manifest, props: { ...manifest.props, Broken: { ...art, sx: 0 } } };
    expect(parseLevelProps(withBad)?.props.has("Broken")).toBe(false);
    const undeclared = { ...manifest, props: { ...manifest.props, Other: { ...art, atlas: "Missing.png" } } };
    expect(parseLevelProps(undeclared)).toBeNull();
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
    const missing = async (): Promise<Response> => ({ ok: false } as unknown as Response);
    const original = globalThis.fetch;
    globalThis.fetch = missing as unknown as typeof fetch;
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

describe("sortPropsForDepth", () => {
  it("paints the farthest first and keeps the level's order at equal depth", () => {
    const props = [prop("a", 0, 0, -5), prop("b", 0, 0, 0), prop("c", 0, 0, 15), prop("d", 0, 0, -5)];
    expect(sortPropsForDepth(props).map((entry) => entry.id)).toEqual(["c", "b", "a", "d"]);
  });
});

describe("drawProps", () => {
  const camera = createCamera();
  const set: LevelPropsSet = { atlases: new Map([["Props_Generic_Sheet_01.png", {} as unknown as CanvasImageSource]]), props: new Map(Object.entries(manifest.props)) };

  it("places a quad at the instance's own transform, in the art's own frame", () => {
    const { ctx, ops } = makeRecorder();
    // 4 world metres right of the camera centre, 2 above it; the camera is 36 px per metre and the
    // canvas 800 x 600, so the screen point is (400 + 144, 300 - 72).
    drawProps(ctx, camera, [prop("Star_01", 8, 4, 0, 0.5, -1, 2)], set, 800, 600);

    expect(ops.map((entry) => entry.op)).toEqual(["save", "translate", "rotate", "translate", "scale", "drawImage", "restore"]);
    expect(ops[1].args).toEqual([544, 228]);
    expect(ops[2].args).toEqual([-0.5]);
    // The art's own centre offset (+0.5 west-pointing x, -0.25 y) negated into canvas y and scaled.
    expect(ops[3].args).toEqual([0.5 * 36, 0.25 * 36]);
    expect(ops[4].args).toEqual([-1, 2]);
    // The atlas rect the manifest names, then the quad drawn about its own centre (the instance's own
    // localScale rides the context's scale above, so the destination stays the art's own size).
    expect(ops[5].args.slice(1, 5)).toEqual([0, 0, 64, 64]);
    expect(ops[5].args.slice(5)).toEqual([-0.25 * 36, -0.625 * 36, 0.5 * 36, 1.25 * 36]);
  });

  it("skips an unknown id, a missing atlas or a missing manifest", () => {
    const { ctx, ops } = makeRecorder();
    drawProps(ctx, camera, [prop("Nope", 0, 0, 0)], set, 800, 600);
    expect(ops).toEqual([]);

    const noAtlas: LevelPropsSet = { atlases: new Map(), props: set.props };
    expect(() => drawProps(ctx, camera, [prop("Grass_02", 0, 0, 0)], noAtlas, 800, 600)).not.toThrow();
    expect(ops).toEqual([]);

    expect(() => drawProps(ctx, camera, [prop("Grass_02", 0, 0, 0)], null, 800, 600)).not.toThrow();
  });
});
