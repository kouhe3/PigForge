import { describe, expect, it } from "vitest";
import { variantLabel, variantsOf } from "./palette";
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
