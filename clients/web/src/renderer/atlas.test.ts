import { describe, expect, it, vi } from "vitest";
import { layoutSprites, loadPartTextures, parsePartTextures } from "./atlas";

const manifest = {
  format: "pigforge.part-textures",
  schemaVersion: 1,
  atlases: { "A.png": { width: 2048, height: 2048 } },
  parts: {
    "10": {
      bbox: [2, 1],
      sprites: [
        { atlas: "A.png", x: 10, y: 20, w: 100, h: 50, cx: 0.5, cy: 0, sx: 1, sy: 1, rot: 0.25 },
        { atlas: "A.png", x: 200, y: 300, w: 100, h: 50, cx: -0.5, cy: 0, sx: 1, sy: 1, rot: 0 },
      ],
    },
  },
};

describe("parsePartTextures", () => {
  it("reads part entries keyed by partTypeId", () => {
    const parts = parsePartTextures(manifest);
    expect(parts.get(10)?.sprites).toHaveLength(2);
    expect(parts.get(10)?.sprites[0]).toEqual({
      atlas: "A.png",
      x: 10,
      y: 20,
      w: 100,
      h: 50,
      cx: 0.5,
      cy: 0,
      sx: 1,
      sy: 1,
      rot: 0.25,
    });
  });

  it("rejects a foreign format", () => {
    expect(() => parsePartTextures({ ...manifest, format: "other" })).toThrow();
  });

  it("rejects an unsupported schema version", () => {
    expect(() => parsePartTextures({ ...manifest, schemaVersion: 2 })).toThrow();
  });

  it("rejects a sprite with a non-positive source rect", () => {
    const broken = {
      ...manifest,
      parts: { "10": { bbox: [1, 1], sprites: [{ ...manifest.parts["10"].sprites[0], w: 0 }] } },
    };
    expect(() => parsePartTextures(broken)).toThrow(/rect not positive/);
  });
});

describe("layoutSprites", () => {
  it("fits the composite into the shape half-extents, preserving aspect ratio", () => {
    const [left, right] = layoutSprites(parsePartTextures(manifest).get(10)!, 0.5, 0.5);
    // bbox 2x1 into a 1x1 box: scale 0.5, so each 1x1 sprite becomes 0.5x0.5.
    expect(left.w).toBeCloseTo(0.5);
    expect(left.h).toBeCloseTo(0.5);
    expect(left.x).toBeCloseTo(0.25);
    expect(right.x).toBeCloseTo(-0.25);
    expect(left.y).toBeCloseTo(0);
  });

  it("centres the composite on the part origin", () => {
    const placements = layoutSprites(parsePartTextures(manifest).get(10)!, 2, 2);
    const centre = placements.reduce((sum, p) => sum + p.x, 0) / placements.length;
    expect(centre).toBeCloseTo(0);
  });
});

describe("loadPartTextures", () => {
  it("returns null when the manifest is absent", async () => {
    vi.stubGlobal("fetch", vi.fn(async () => ({ ok: false }) as Response));
    const loader = vi.fn();
    expect(await loadPartTextures("/assets/original/part-textures.json", loader)).toBeNull();
    expect(loader).not.toHaveBeenCalled();
    vi.unstubAllGlobals();
  });

  it("loads every atlas next to the manifest", async () => {
    vi.stubGlobal(
      "fetch",
      vi.fn(async () => ({ ok: true, json: async () => manifest }) as Response),
    );
    const loader = vi.fn(async (url: string) => ({ url }) as unknown as CanvasImageSource);
    const textures = await loadPartTextures("/assets/original/part-textures.json", loader);
    expect(loader).toHaveBeenCalledWith("/assets/original/A.png");
    expect(textures?.atlases.has("A.png")).toBe(true);
    expect(textures?.parts.get(10)?.bbox).toEqual([2, 1]);
    vi.unstubAllGlobals();
  });

  it("returns null instead of throwing on a malformed manifest", async () => {
    vi.stubGlobal(
      "fetch",
      vi.fn(async () => ({ ok: true, json: async () => ({ format: "pigforge.part-textures" }) }) as Response),
    );
    expect(await loadPartTextures("/assets/original/part-textures.json", vi.fn())).toBeNull();
    vi.unstubAllGlobals();
  });
});
