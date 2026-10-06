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

    // Every entity here has yaw 0, so each part type is one direction bucket (Right, the arrow the
    // bar shows). The group carries the entities it drives so the bar can command them one by one.
    expect(groups).toEqual([
      { partTypeId: 8, kind: "toggle", label: "发动机 ▶", count: 2, active: true, hotkey: "1", direction: 0, entityIds: [1, 2] },
      { partTypeId: 13, kind: "trigger", label: "火箭 ▶", count: 1, active: false, hotkey: "2", direction: 0, entityIds: [3] },
    ]);
  });

  it("splits one part type into a group per effect direction", () => {
    // The original's bar key is (part type, `EffectDirection()`) -- `Rotate(localDirection,
    // gridRotation)` -- so a rocket built a half turn round belongs to a different button, exactly
    // as `UIPartButtonList` makes one button per distinct `UIPartButtonInfo` (UIPartButtonList.cs:539-575).
    const entities = [
      entity(1, 13, false),
      { ...entity(2, 13, true), yaw: Math.PI },
      { ...entity(3, 13, false), yaw: Math.PI / 2 },
    ];

    const groups = gadgetGroups(entities, new Set([1, 2, 3]), content);

    expect(groups.map((group) => [group.direction, group.entityIds, group.active])).toEqual([
      [0, [1], false],   // built at 0: pushes right
      [2, [3], false],   // built a quarter turn round: pushes up
      [4, [2], true],    // built a half turn round: pushes left
    ]);
    expect(groups.map((group) => group.label)).toEqual(["火箭 ▶", "火箭 ▲", "火箭 ◀"]);
    expect(groups.map((group) => group.hotkey)).toEqual(["1", "2", "3"]);
  });

  it("puts a part built at an in-between angle in the nearest bucket", () => {
    // Our free yaw (the original's grid rotation is a multiple of 90 degrees) still has to be
    // reachable from the bar: the nearest 45 degree bucket is the same rule the build grid's
    // occupancy uses for an arbitrary angle.
    const groups = gadgetGroups([{ ...entity(1, 13, false), yaw: Math.PI / 8 }], new Set([1]), content);

    expect(groups).toHaveLength(1);
    expect(groups[0].direction).toBe(1); // just past right, the first diagonal
    expect(groups[0].label).toBe("火箭 ◥");
  });

  it("takes the effect direction from the content, not the part type", () => {
    // A fan's `m_forceDirection` (FanPropeller.cs:59-62) is its own content value; two fans of the
    // same type built the same way stay one group whatever the rest of the catalogue does.
    const fanContent: PartContentDocument = {
      ...content,
      parts: [
        ...content.parts,
        { partTypeId: 11, name: "fan", mode: "dynamic", mass: 0.25, capabilities: { fan: { thrustPerTick: 0.12, directionX: -1, directionY: 0 }, activation: "toggle" }, shapes: [{ kind: "box", halfExtents: [0.5, 0.5, 0.5] }] },
      ],
    };

    const groups = gadgetGroups([entity(1, 11, false), entity(2, 11, false)], new Set([1, 2]), fanContent);

    expect(groups).toHaveLength(1);
    expect(groups[0].direction).toBe(4); // Left, the fan's own m_forceDirection
    expect(groups[0].entityIds).toEqual([1, 2]);
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

    // A TNT's own effect direction is `Rotate(Right, gridRotation)` (TNT.cs:63-70), so its label
    // carries the arrow like every other group's.
    expect(groups[0].label).toBe("Nitro TNT ▶");
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
