import { describe, expect, it } from "vitest";
import { gadgetGroups, gadgetHotkey } from "./gadgets";
import type { DrawEntity, PartContentDocument } from "@/schema/types";

const content: PartContentDocument = {
  format: "pigforge.part-content",
  schemaVersion: 1,
  contentVersion: "t",
  physics: { maximumAngularSpeed: 7, damping: { linear: 0.2, angular: 0.05 } },

  parts: [
    { partTypeId: 1, name: "block", mode: "dynamic", mass: 1, shapes: [{ kind: "box", halfExtents: [0.5, 0.5, 0.5] }] },
    { partTypeId: 8, name: "engine", mode: "dynamic", mass: 1, capabilities: { motor: { thrustPerTick: 2, directionX: 1 }, activation: "toggle" }, shapes: [{ kind: "box", halfExtents: [0.5, 0.5, 0.5] }] },
    { partTypeId: 13, name: "rocket", mode: "dynamic", mass: 1, capabilities: { rocket: { thrustPerTick: 4, directionX: 1, ignitionTicks: 0, boostTicks: 30, endTicks: 0, maxSpeed: 18 }, activation: "trigger" }, shapes: [{ kind: "box", halfExtents: [0.5, 0.5, 0.5] }] },
    { partTypeId: 47, name: "tnt-nitro", variantOf: 9, variantName: "Nitro TNT", mode: "dynamic", mass: 1, capabilities: { tnt: { fuseTicks: 5 }, activation: "trigger" }, shapes: [{ kind: "box", halfExtents: [0.5, 0.5, 0.5] }] },
  ],
};

function entity(entityId: number, partTypeId: number, active: boolean, bodyId = 1): DrawEntity {
  return { entityId, partTypeId, x: 0, y: 0, yaw: 0, scale: 1, vx: 0, vy: 0, bodyId, active };
}

describe("gadgetGroups", () => {
  it("groups switchable own materialised parts by type in ascending order", () => {
    const entities = [
      entity(3, 13, false),
      entity(1, 8, true),
      entity(2, 8, false),
      entity(4, 1, false),
    ];

    const groups = gadgetGroups(entities, new Set([1, 2, 3, 4]), content);

    expect(groups).toEqual([
      { partTypeId: 8, kind: "toggle", label: "发动机", count: 2, active: true, hotkey: "1" },
      { partTypeId: 13, kind: "trigger", label: "火箭", count: 1, active: false, hotkey: "2" },
    ]);
  });

  it("drops other players, previews and switchless parts", () => {
    const entities = [
      entity(1, 8, true, 0),
      entity(2, 8, true),
      entity(3, 1, false),
    ];

    expect(gadgetGroups(entities, new Set([1, 3]), content)).toEqual([]);
  });

  it("falls back to the variant name when the palette has no entry", () => {
    const groups = gadgetGroups([entity(1, 47, false)], new Set([1]), content);

    expect(groups[0].label).toBe("Nitro TNT");
  });

  it("returns nothing without content", () => {
    expect(gadgetGroups([entity(1, 8, true)], new Set([1]), null)).toEqual([]);
  });
});

describe("gadgetHotkey", () => {
  it("maps bar order to 1-9, 0, then A-Z", () => {
    expect(gadgetHotkey(0)).toBe("1");
    expect(gadgetHotkey(8)).toBe("9");
    expect(gadgetHotkey(9)).toBe("0");
    expect(gadgetHotkey(10)).toBe("A");
    expect(gadgetHotkey(35)).toBe("Z");
    expect(gadgetHotkey(36)).toBeNull();
  });
});
