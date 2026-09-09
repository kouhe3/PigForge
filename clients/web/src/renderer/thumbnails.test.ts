import { describe, expect, it } from "vitest";
import type { PartSprite, PartTexture, PartTextureSet } from "./atlas";
import { compositeBounds, partThumbnailDataUrl, thumbnailPlacements } from "./thumbnails";

function sprite(overrides: Partial<PartSprite> = {}): PartSprite {
  return { atlas: "atlas.png", x: 0, y: 0, w: 10, h: 10, cx: 0, cy: 0, sx: 1, sy: 1, rot: 0, ...overrides };
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
});

describe("partThumbnailDataUrl", () => {
  it("returns null for an unknown part or a missing atlas", () => {
    const set: PartTextureSet = { atlases: new Map(), parts: new Map([[9, texture([sprite()])]]) };

    expect(partThumbnailDataUrl(set, 404)).toBeNull();
    expect(partThumbnailDataUrl(set, 9)).toBeNull();
  });
});
