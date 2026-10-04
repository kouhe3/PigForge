import { readFileSync } from "node:fs";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";
import { describe, expect, it } from "vitest";
import { PALETTE, PLAY_PARTS } from "./slope";
import type { PartContentDocument } from "@/schema/types";

const root = join(dirname(fileURLToPath(import.meta.url)), "../../../../");
const content = JSON.parse(readFileSync(join(root, "content/parts.json"), "utf8")) as PartContentDocument;

describe("PLAY_PARTS", () => {
  // The client bundles a generated copy of the server's content/parts.json. Drift means the
  // palette and the offline slope builder render parts the server does not know, so the two
  // must agree; regenerate with tools/web-parts/generate-play-parts.mjs.
  it("deep-equals content/parts.json", () => {
    expect(PLAY_PARTS).toEqual(content);
  });

  it("declares every part the server content declares", () => {
    expect(PLAY_PARTS.parts.length).toBe(content.parts.length);
  });
});

describe("PALETTE", () => {
  // The palette is hand-ordered UI (Chinese labels, the pig first), but it must not be incomplete:
  // a dynamic base part missing from it is invisible in the build tab, which is how the original's
  // other two engine families (270 small, 271 big) and the dynamite (42) stayed unplaceable.
  // Static level pieces (ground slab, terrain box, ramp plank) are deliberately absent.
  it("offers every dynamic base part, and only dynamic base parts", () => {
    const dynamicBaseIds = content.parts
      .filter((part) => part.variantOf === undefined && part.mode === "dynamic")
      .map((part) => part.partTypeId)
      .sort((left, right) => left - right);
    const paletteIds = PALETTE.map((entry) => entry.partTypeId);

    expect(paletteIds.length).toBe(new Set(paletteIds).size);
    expect([...paletteIds].sort((left, right) => left - right)).toEqual(dynamicBaseIds);
    expect(PALETTE.every((entry) => entry.label.length > 0)).toBe(true);
  });
});
