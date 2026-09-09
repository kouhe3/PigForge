import { readFileSync } from "node:fs";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";
import { describe, expect, it } from "vitest";
import { validateReplay } from "./validateReplay";
import { validatePartContent } from "./validateContent";

const root = join(dirname(fileURLToPath(import.meta.url)), "../../../../");

describe("validateReplay", () => {
  it("accepts the v2 fixture", () => {
    const replay = JSON.parse(readFileSync(join(dirname(fileURLToPath(import.meta.url)), "../../fixtures/replay-v2.json"), "utf8"));
    expect(validateReplay(replay)).toEqual([]);
  });

  it("rejects the wrong protocol version", () => {
    const replay = JSON.parse(readFileSync(join(dirname(fileURLToPath(import.meta.url)), "../../fixtures/replay-v2.json"), "utf8"));
    replay.header.protocolVersion = 1;
    expect(validateReplay(replay).some((error) => error.includes("unsupported"))).toBe(true);
  });

  it("rejects a GUID-like part field", () => {
    expect(validatePartContent({
      format: "pigforge.part-content",
      schemaVersion: 1,
      contentVersion: "x",
      parts: [{ partTypeId: 1, name: "a", mode: "dynamic", mass: 1, guid: "abc", shapes: [{ kind: "box", halfExtents: [0.5, 0.5, 0.5] }] }],
    }).some((error) => error.includes("GUID"))).toBe(true);
  });

  it("accepts sample part content", () => {
    const content = JSON.parse(readFileSync(join(root, "content/parts.json"), "utf8"));
    expect(validatePartContent(content)).toEqual([]);
  });
});

describe("validatePartContent variants", () => {
  const base: Record<string, unknown> = {
    partTypeId: 9,
    name: "tnt",
    mode: "dynamic",
    mass: 1,
    shapes: [{ kind: "box", halfExtents: [0.35, 0.35, 0.35] }],
  };
  const document = (parts: unknown[]) => ({ format: "pigforge.part-content", schemaVersion: 1, contentVersion: "x", parts });

  it("accepts a variant that references a declared base part", () => {
    const variant = { ...base, partTypeId: 47, name: "tnt-nitro", variantOf: 9, variantName: "Nitro TNT" };
    expect(validatePartContent(document([base, variant]))).toEqual([]);
  });

  it("rejects a variant of an undeclared part", () => {
    const variant = { ...base, partTypeId: 47, name: "tnt-nitro", variantOf: 404 };
    expect(validatePartContent(document([base, variant])).some((error) => error.includes("undeclared part"))).toBe(true);
  });

  it("rejects a variant chain", () => {
    const first = { ...base, partTypeId: 47, name: "tnt-nitro", variantOf: 9 };
    const second = { ...base, partTypeId: 48, name: "tnt-bomb", variantOf: 47 };
    expect(validatePartContent(document([base, first, second])).some((error) => error.includes("itself a variant"))).toBe(true);
  });

  it("rejects a self-referencing variant", () => {
    const self = { ...base, partTypeId: 9, variantOf: 9 };
    expect(validatePartContent(document([self])).some((error) => error.includes("variant of itself"))).toBe(true);
  });

  it("rejects an unknown activation value", () => {
    const part = { ...base, capabilities: { tnt: { fuseTicks: 5 }, activation: "latch" } };
    expect(validatePartContent(document([part])).some((error) => error.includes("activation"))).toBe(true);
  });

  it("accepts the original-effect capabilities", () => {
    const part = {
      ...base,
      capabilities: {
        tnt: { fuseTicks: 5, chainDetonate: true, igniteOnImpact: false },
        blaster: { radius: 3.5, impulse: 30, chainRadius: 8 },
        glue: true,
      },
    };
    expect(validatePartContent(document([part]))).toEqual([]);
  });

  it("rejects a blaster without a positive radius", () => {
    const part = { ...base, capabilities: { blaster: { radius: 0, impulse: 30 } } };
    expect(validatePartContent(document([part])).some((error) => error.includes("blaster"))).toBe(true);
  });

  it("rejects non-boolean tnt chain flags", () => {
    const part = { ...base, capabilities: { tnt: { fuseTicks: 5, igniteOnImpact: 1 } } };
    expect(validatePartContent(document([part])).some((error) => error.includes("chainDetonate/igniteOnImpact"))).toBe(true);
  });
});
