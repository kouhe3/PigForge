import { describe, expect, it } from "vitest";
import type { PartSprite, PartTexture, PartTextureSet } from "./atlas";
import { compositeBounds, partThumbnailDataUrl, thumbnailPlacements } from "./thumbnails";

function sprite(overrides: Partial<PartSprite> = {}): PartSprite {
  return { atlas: "atlas.png", x: 0, y: 0, w: 10, h: 10, cx: 0, cy: 0, sx: 1, sy: 1, rot: 0, rotates: false, ...overrides };
}

function texture(sprites: PartSprite[]): PartTexture {
  return { bbox: [1, 1], sprites };
}

describe("compositeBounds", () => {
  it("uses the rotated extent of each sprite", () => {
    const bounds = compositeBounds(texture([sprite({ sx: 2, sy: 1, rot: Math.PI / 2 })]));

    expect(bounds.maxX - bounds.minX).toBeCloseTo(1, 6);
    expect(bounds.maxY - bounds.minY).toBeCloseTo(2, 6);
  });

  it("unions every sprite", () => {
    const bounds = compositeBounds(texture([sprite({ cx: -2 }), sprite({ cx: 2 })]));

    expect(bounds.minX).toBeCloseTo(-2.5, 6);
    expect(bounds.maxX).toBeCloseTo(2.5, 6);
  });
});

describe("thumbnailPlacements", () => {
  it("fits the composite inside the padded square", () => {
    const [placement] = thumbnailPlacements(texture([sprite({ sx: 4, sy: 1 })]), 64, 4);

    expect(placement.w).toBeCloseTo(56, 6);
    expect(placement.h).toBeCloseTo(14, 6);
    expect(placement.x).toBeCloseTo(32, 6);
    expect(placement.y).toBeCloseTo(32, 6);
  });

  it("centres the composite instead of the part origin", () => {
    const [placement] = thumbnailPlacements(texture([sprite({ cx: 3, cy: -1, sx: 2, sy: 2 })]), 64, 4);

    expect(placement.x).toBeCloseTo(32, 6);
    expect(placement.y).toBeCloseTo(32, 6);
  });

  it("flips world +y into canvas +y", () => {
    const [upper, lower] = thumbnailPlacements(texture([sprite({ cy: 1 }), sprite({ cy: -1 })]), 64, 0);

    expect(upper.y).toBeLessThan(lower.y);
    expect(upper.y).toBeGreaterThanOrEqual(0);
    expect(lower.y).toBeLessThanOrEqual(64);
  });

  /**
   * The reported display bug: in the palette the wooden wheel's disc sat above its axle
   * instead of on it. Content part 7's disc sprite carries the original prefab offset, and
   * the pivot is the axle the wheel actually turns about.
   */
  it("puts a wheel's disc on the axle, below the axle sprite", () => {
    const wheel: PartTexture = {
      bbox: [1, 1],
      pivot: [0.0106, -0.2107],
      sprites: [
        sprite({ cx: 0.0029, cy: -0.0317, sx: 0.6563, sy: 0.6667, rotates: true }),
        sprite({ cx: 0, cy: 0, sx: 0.6875, sy: 0.8125, rotates: false }),
      ],
    };
    const [disc, axle] = thumbnailPlacements(wheel, 64, 3);
    const bounds = compositeBounds(wheel);
    const scale = Math.min((64 - 6) / (bounds.maxX - bounds.minX), (64 - 6) / (bounds.maxY - bounds.minY));

    // The disc sits at the pivot, measured from the axle sprite's own centre.
    expect(disc.x - axle.x).toBeCloseTo(wheel.pivot![0] * scale, 6);
    expect(disc.y - axle.y).toBeCloseTo(-wheel.pivot![1] * scale, 6);
    // Canvas +y is down, so the disc renders below the axle the wheel hangs from.
    expect(disc.y).toBeGreaterThan(axle.y);
  });
});

describe("partThumbnailDataUrl", () => {
  it("returns null for an unknown part or a missing atlas", () => {
    const set: PartTextureSet = { atlases: new Map(), parts: new Map([[9, texture([sprite()])]]) };

    expect(partThumbnailDataUrl(set, 404)).toBeNull();
    expect(partThumbnailDataUrl(set, 9)).toBeNull();
  });
});
