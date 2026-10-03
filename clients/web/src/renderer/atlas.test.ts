import { describe, expect, it, vi } from "vitest";
import { layoutSprites, loadPartTextures, parsePartTextures } from "./atlas";

const manifest = {
  format: "pigforge.part-textures",
  schemaVersion: 2,
  atlases: { "A.png": { width: 2048, height: 2048 } },
  parts: {
    "10": {
      bbox: [2, 1],
      sprites: [
        { atlas: "A.png", x: 10, y: 20, w: 100, h: 50, cx: 0.5, cy: 0, sx: 1, sy: 1, rot: 0.25, rotates: true },
        { atlas: "A.png", x: 200, y: 300, w: 100, h: 50, cx: -0.5, cy: 0, sx: 1, sy: 1, rot: 0, rotates: false },
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
      rotates: true,
    });
  });

  it("rejects a foreign format", () => {
    expect(() => parsePartTextures({ ...manifest, format: "other" })).toThrow();
  });

  it("rejects an unsupported schema version", () => {
    expect(() => parsePartTextures({ ...manifest, schemaVersion: 1 })).toThrow();
  });

  it("carries the flag that marks the sprites riding a rotating pivot", () => {
    const sprites = parsePartTextures(manifest).get(10)!.sprites;
    expect(sprites.map((sprite) => sprite.rotates)).toEqual([true, false]);
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
  it("places sprites at the original world size and offsets", () => {
    const [left, right] = layoutSprites(parsePartTextures(manifest).get(10)!, 1);
    expect(left.w).toBeCloseTo(1);
    expect(left.h).toBeCloseTo(1);
    expect(left.x).toBeCloseTo(0.5);
    expect(right.x).toBeCloseTo(-0.5);
    expect(left.y).toBeCloseTo(0);
  });

  it("scales the whole composite by the entity scale", () => {
    const [left] = layoutSprites(parsePartTextures(manifest).get(10)!, 2);
    expect(left.w).toBeCloseTo(2);
    expect(left.x).toBeCloseTo(1);
  });
});

// A v3 manifest: the same static sprites plus the animation descriptors of the extractor
// (a fan blade's spin, the pig's face clips and its expression thresholds).
const frame = { atlas: "A.png", x: 587, y: 289, w: 85, h: 48, cx: 0, cy: -0.1468, sx: 0.8854, sy: 0.5, rot: 0, seconds: 0.1 };
const blinkSecond = { ...frame, x: 1358, y: 740, w: 86, h: 46, seconds: 0.2 };
const expression = { speedFunRatio: 0.15, speedFearfulRatio: 0.3, speedFearRatio: 0.5, speedReference: 20, hitDeltaV: 5, fallFearThreshold: 3 };
const animatedManifest = {
  format: "pigforge.part-textures",
  schemaVersion: 3,
  atlases: { "A.png": { width: 2048, height: 2048 } },
  parts: {
    "4": {
      bbox: [2, 1] as [number, number],
      expression,
      sprites: [
        { atlas: "A.png", x: 587, y: 289, w: 85, h: 48, cx: 0, cy: -0.1468, sx: 0.8854, sy: 0.5, rot: 0, rotates: false, clips: { Normal: { loop: false, frames: [frame] }, Blink: { loop: false, frames: [frame, blinkSecond] } } },
        { atlas: "A.png", x: 10, y: 20, w: 100, h: 50, cx: 0, cy: 0, sx: 1, sy: 1, rot: 0, rotates: true, spin: { axis: "x", maxDegreesPerSecond: 1700 } },
      ],
    },
  },
};

describe("parsePartTextures animation descriptors", () => {
  it("reads the spin descriptor of a fan blade", () => {
    const sprites = parsePartTextures(animatedManifest).get(4)!.sprites;
    expect(sprites[1].spin).toEqual({ axis: "x", maxDegreesPerSecond: 1700 });
    expect(sprites[1].clips).toBeUndefined();
    expect(sprites[0].spin).toBeUndefined();
  });

  it("reads the clips of a sprite that swaps frames", () => {
    const clips = parsePartTextures(animatedManifest).get(4)!.sprites[0].clips!;
    expect(Object.keys(clips)).toEqual(["Normal", "Blink"]);
    expect(clips.Normal).toEqual({ loop: false, frames: [frame] });
    expect(clips.Blink).toEqual({ loop: false, frames: [frame, blinkSecond] });
  });

  it("reads the expression thresholds of a pig", () => {
    expect(parsePartTextures(animatedManifest).get(4)!.expression).toEqual(expression);
  });

  it("still accepts a v2 manifest as a v3 one without any animation", () => {
    const part = parsePartTextures(manifest).get(10)!;
    expect(part.sprites).toHaveLength(2);
    expect(part.expression).toBeUndefined();
    expect(part.sprites.some((sprite) => sprite.spin !== undefined || sprite.clips !== undefined)).toBe(false);
  });

  it("rejects a spin axis the renderer cannot compress", () => {
    const broken = { ...animatedManifest, parts: { "4": { ...animatedManifest.parts["4"], sprites: [{ ...animatedManifest.parts["4"].sprites[1], spin: { axis: "z", maxDegreesPerSecond: 1700 } }] } } };
    expect(() => parsePartTextures(broken)).toThrow(/spin axis/);
  });

  it("rejects an empty frame table and a non-positive frame duration", () => {
    const face = animatedManifest.parts["4"].sprites[0];
    const empty = { ...animatedManifest, parts: { "4": { ...animatedManifest.parts["4"], sprites: [{ ...face, clips: { Normal: { loop: false, frames: [] } } }] } } };
    expect(() => parsePartTextures(empty)).toThrow(/has no frames/);
    const zero = { ...animatedManifest, parts: { "4": { ...animatedManifest.parts["4"], sprites: [{ ...face, clips: { Normal: { loop: false, frames: [{ ...frame, seconds: 0 }] } } }] } } };
    expect(() => parsePartTextures(zero)).toThrow(/seconds is not positive/);
  });

  it("rejects an expression threshold that is not a positive number", () => {
    const broken = { ...animatedManifest, parts: { "4": { ...animatedManifest.parts["4"], expression: { ...expression, hitDeltaV: 0 } } } };
    expect(() => parsePartTextures(broken)).toThrow(/hitDeltaV is not positive/);
  });

  it("rejects a schema version past the connection one", () => {
    expect(() => parsePartTextures({ ...animatedManifest, schemaVersion: 5 })).toThrow(/unsupported schemaVersion/);
  });
});

// A v4 manifest: the same sprites plus the connection conditions of the extractor (a rocket's
// four side markers, a wing's two mounts) and the host rule that gives them meaning.
const conditionalManifest = {
  format: "pigforge.part-textures",
  schemaVersion: 4,
  atlases: { "A.png": { width: 2048, height: 2048 } },
  parts: {
    "13": {
      bbox: [2, 1] as [number, number],
      connectionVisual: "attachmentFallback",
      sprites: [
        { atlas: "A.png", x: 1, y: 2, w: 3, h: 4, cx: 0, cy: 0.37, sx: 0.5, sy: 0.28, rot: 0, rotates: false, condition: { kind: "attachment", side: "top" } },
        { atlas: "A.png", x: 5, y: 6, w: 3, h: 4, cx: 0, cy: 0, sx: 1, sy: 1, rot: 0, rotates: false },
      ],
    },
  },
};

describe("parsePartTextures connection conditions", () => {
  it("reads the host rule and the side tags of the conditional sprites", () => {
    const part = parsePartTextures(conditionalManifest).get(13)!;
    expect(part.connectionVisual).toBe("attachmentFallback");
    expect(part.sprites[0].condition).toEqual({ kind: "attachment", side: "top" });
    expect(part.sprites[1].condition).toBeUndefined();
  });

  it("reads a wing's mount condition", () => {
    const wing = {
      ...conditionalManifest,
      parts: {
        "31": {
          bbox: [2, 1] as [number, number],
          connectionVisual: "frame",
          sprites: [{ ...conditionalManifest.parts["13"].sprites[0], condition: { kind: "frame", mount: "bottom" } }],
        },
      },
    };
    expect(parsePartTextures(wing).get(31)!.sprites[0].condition).toEqual({ kind: "frame", mount: "bottom" });
  });

  it("still accepts a v3 manifest as a v4 one without any condition", () => {
    const part = parsePartTextures(animatedManifest).get(4)!;
    expect(part.connectionVisual).toBeUndefined();
    expect(part.sprites.some((sprite) => sprite.condition !== undefined)).toBe(false);
  });

  it("rejects an unknown side, mount and host rule", () => {
    const side = { ...conditionalManifest, parts: { "13": { ...conditionalManifest.parts["13"], sprites: [{ ...conditionalManifest.parts["13"].sprites[0], condition: { kind: "attachment", side: "north" } }] } } };
    expect(() => parsePartTextures(side)).toThrow(/condition side/);
    const mount = { ...conditionalManifest, parts: { "13": { ...conditionalManifest.parts["13"], sprites: [{ ...conditionalManifest.parts["13"].sprites[0], condition: { kind: "frame", mount: "middle" } }] } } };
    expect(() => parsePartTextures(mount)).toThrow(/condition mount/);
    const rule = { ...conditionalManifest, parts: { "13": { ...conditionalManifest.parts["13"], connectionVisual: "guess" } } };
    expect(() => parsePartTextures(rule)).toThrow(/connectionVisual/);
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

  it("loads the atlas a clip frame lives in, not only the static ones", async () => {
    const frameInAnotherAtlas = {
      ...animatedManifest,
      parts: {
        "4": {
          ...animatedManifest.parts["4"],
          sprites: [
            {
              ...animatedManifest.parts["4"].sprites[0],
              clips: { Normal: { loop: false, frames: [{ ...frame, atlas: "B.png" }] } },
            },
          ],
        },
      },
    };
    vi.stubGlobal(
      "fetch",
      vi.fn(async () => ({ ok: true, json: async () => frameInAnotherAtlas }) as Response),
    );
    const loader = vi.fn(async (url: string) => ({ url }) as unknown as CanvasImageSource);
    const textures = await loadPartTextures("/assets/original/part-textures.json", loader);
    expect(loader).toHaveBeenCalledWith("/assets/original/B.png");
    expect(textures?.atlases.has("B.png")).toBe(true);
    vi.unstubAllGlobals();
  });
});
