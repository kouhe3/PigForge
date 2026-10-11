import { describe, expect, it } from "vitest";
import { allowedPalette, partFamily, remainingCounts, variantLabel, variantsOf } from "./palette";
import type { LevelBuildLimits } from "@/schema/levelContent";
import type { PartContentDocument, PartDefinition } from "@/schema/types";

const shapes: PartDefinition["shapes"] = [{ kind: "box", halfExtents: [0.5, 0.5, 0.5] }];
const bomb: PartDefinition = { partTypeId: 48, name: "tnt-bomb", variantOf: 9, mode: "dynamic", mass: 1, shapes };
const nitro: PartDefinition = { partTypeId: 47, name: "tnt-nitro", variantOf: 9, variantName: "Nitro TNT", mode: "dynamic", mass: 1, shapes };
const gift: PartDefinition = { partTypeId: 49, name: "tnt-gift", variantOf: 9, mode: "dynamic", mass: 1, shapes };

// Variants are listed out of id order so content order (not id order) is observable.
const content: PartContentDocument = {
  format: "pigforge.part-content",
  schemaVersion: 1,
  contentVersion: "t",
  physics: { maximumAngularSpeed: 7, damping: { linear: 0.2, angular: 0.05 } },

  parts: [
    { partTypeId: 9, name: "tnt", mode: "dynamic", mass: 1, shapes },
    bomb,
    nitro,
    gift,
    { partTypeId: 4, name: "pig", mode: "dynamic", mass: 1, shapes },
  ],
};

describe("variantsOf", () => {
  it("returns a base part's variants in content order", () => {
    expect(variantsOf(content, 9).map((part) => part.partTypeId)).toEqual([48, 47, 49]);
  });

  it("returns no variants for a base with none, an unknown base, or absent content", () => {
    expect(variantsOf(content, 4)).toEqual([]);
    expect(variantsOf(content, 999)).toEqual([]);
    expect(variantsOf(null, 9)).toEqual([]);
  });
});

describe("variantLabel", () => {
  it("prefers the variant's content label", () => {
    expect(variantLabel("TNT", 2, nitro)).toBe("Nitro TNT");
  });

  it("falls back to the base label and 1-based ordinal", () => {
    expect(variantLabel("TNT", 1, bomb)).toBe("TNT #1");
    expect(variantLabel("TNT", 2, gift)).toBe("TNT #2");
  });
});

describe("partFamily", () => {
  it("maps a variant to its base and a base to itself", () => {
    expect(partFamily(content, 48)).toBe(9);
    expect(partFamily(content, 4)).toBe(4);
    expect(partFamily(content, 999)).toBe(999);
    expect(partFamily(null, 9)).toBe(9);
  });
});

/** A v9 level's own allowance, with a zero-count family, an unknown family and a repeated one. */
const limits: LevelBuildLimits = {
  origin: [2.4379761, 5.4575901],
  rows: [31, 31, 31, 0],
  partCounts: [
    { partTypeId: 1, count: 3 },
    { partTypeId: 4, count: 1 },
    { partTypeId: 9, count: 2 },
    { partTypeId: 6, count: 0 },
    { partTypeId: 900, count: 5 },
  ],
};

describe("allowedPalette", () => {
  it("keeps only families the level grants a positive count of, in PALETTE order", () => {
    expect(allowedPalette(limits).map((entry) => entry.partTypeId)).toEqual([4, 1, 9]);
  });
});

describe("remainingCounts", () => {
  it("subtracts the placed parts of each family, clamped at zero", () => {
    const remaining = remainingCounts(limits, new Map([[9, 1], [1, 5]]));
    expect(remaining.get(4)).toBe(1);
    expect(remaining.get(1)).toBe(0);
    expect(remaining.get(9)).toBe(1);
    expect(remaining.get(6)).toBe(0);
    expect(remaining.get(900)).toBe(5);
  });

  it("uses the first entry of a repeated family, like the server's GetPartCount", () => {
    const repeated: LevelBuildLimits = {
      ...limits,
      partCounts: [
        { partTypeId: 9, count: 2 },
        { partTypeId: 9, count: 7 },
      ],
    };
    expect(remainingCounts(repeated, new Map()).get(9)).toBe(2);
  });
});
