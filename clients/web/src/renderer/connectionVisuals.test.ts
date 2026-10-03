import { describe, expect, it } from "vitest";
import type { DrawEntity, PartDefinition } from "@/schema/types";
import type { LocalSide } from "./atlas";
import { conditionalSpriteVisible, connectableSides } from "./connectionVisuals";

const SOURCE: PartDefinition = { partTypeId: 1, name: "source", mode: "dynamic", mass: 1, capabilities: { jointConnectionType: "source" }, shapes: [] };
const TARGET: PartDefinition = { partTypeId: 2, name: "target", mode: "dynamic", mass: 1, capabilities: { jointConnectionType: "target" }, shapes: [] };
const INERT: PartDefinition = { partTypeId: 3, name: "none", mode: "dynamic", mass: 1, capabilities: { jointConnectionType: "none" }, shapes: [] };
const PLAIN: PartDefinition = { partTypeId: 9, name: "plain", mode: "dynamic", mass: 1, shapes: [] };
const TYPES: Record<number, PartDefinition> = { 1: SOURCE, 2: TARGET, 3: INERT, 9: PLAIN };

function entity(entityId: number, x: number, y: number, partTypeId = 1, yaw = 0): DrawEntity {
  return { entityId, partTypeId, x, y, yaw, scale: 1, vx: 0, vy: 0, bodyId: 0, active: false };
}

const partOf = (partTypeId: number) => TYPES[partTypeId];

describe("connectableSides", () => {
  it("marks the local side each end of a weldable pair faces", () => {
    const sides = connectableSides([entity(1, 0, 0, 1), entity(2, 0, 1, 2)], partOf, () => true);
    expect([...(sides.get(1) ?? [])]).toEqual(["top"]);
    expect([...(sides.get(2) ?? [])]).toEqual(["bottom"]);
  });

  it("turns the local side with the entity's own yaw", () => {
    const sides = connectableSides([entity(1, 0, 0, 1, Math.PI / 2), entity(2, -1, 0, 2)], partOf, () => true);
    expect([...(sides.get(1) ?? [])]).toEqual(["top"]);
  });

  it("needs a weldable pair, not just a neighbour", () => {
    // Two targets: neither is a source, so the original welds nothing.
    expect(connectableSides([entity(1, 0, 0, 2), entity(2, 0, 1, 2)], partOf, () => true).size).toBe(0);
    // A `none` end never welds, whatever the other side declares.
    expect(connectableSides([entity(1, 0, 0, 3), entity(2, 0, 1, 1)], partOf, () => true).size).toBe(0);
  });

  it("skips the entities the caller does not ask about", () => {
    const sides = connectableSides([entity(1, 0, 0, 1), entity(2, 0, 1, 2)], partOf, (e) => e.entityId === 2);
    expect(sides.has(1)).toBe(false);
    expect([...(sides.get(2) ?? [])]).toEqual(["bottom"]);
  });

  it("ignores a neighbour that is not one cell away", () => {
    expect(connectableSides([entity(1, 0, 0, 1), entity(2, 0, 2, 2)], partOf, () => true).size).toBe(0);
  });
});

const ATTACHMENT_BOTTOM = { kind: "attachment", side: "bottom" } as const;
const ATTACHMENT_TOP = { kind: "attachment", side: "top" } as const;
const ATTACHMENT_UP_RIGHT = { kind: "attachment", side: "topRight" } as const;
const FRAME_TOP = { kind: "frame", mount: "top" } as const;
const FRAME_BOTTOM = { kind: "frame", mount: "bottom" } as const;

describe("conditionalSpriteVisible", () => {
  it("draws nothing without a rule the extractor recognises", () => {
    expect(conditionalSpriteVisible(ATTACHMENT_TOP, undefined, new Set<LocalSide>(["top"]), 0)).toBe(false);
  });

  it("falls back to the rocket's bottom marker only when no other side connects", () => {
    expect(conditionalSpriteVisible(ATTACHMENT_BOTTOM, "attachmentFallback", undefined, 0)).toBe(true);
    expect(conditionalSpriteVisible(ATTACHMENT_TOP, "attachmentFallback", undefined, 0)).toBe(false);
    const up = new Set<LocalSide>(["top"]);
    expect(conditionalSpriteVisible(ATTACHMENT_BOTTOM, "attachmentFallback", up, 0)).toBe(false);
    expect(conditionalSpriteVisible(ATTACHMENT_TOP, "attachmentFallback", up, 0)).toBe(true);
  });

  it("draws each TNT marker only on its own side", () => {
    expect(conditionalSpriteVisible(ATTACHMENT_BOTTOM, "attachmentPlain", undefined, 0)).toBe(false);
    expect(conditionalSpriteVisible(ATTACHMENT_BOTTOM, "attachmentPlain", new Set<LocalSide>(["bottom"]), 0)).toBe(true);
  });

  it("shows a wing's top mount for a front side, both mounts for a side", () => {
    const up = new Set<LocalSide>(["top"]);
    expect(conditionalSpriteVisible(FRAME_TOP, "frame", up, 0)).toBe(true);
    expect(conditionalSpriteVisible(FRAME_BOTTOM, "frame", up, 0)).toBe(false);
    // Left/right are in both mount sets, so both are shown — the original's behaviour.
    const left = new Set<LocalSide>(["left"]);
    expect(conditionalSpriteVisible(FRAME_TOP, "frame", left, 0)).toBe(true);
    expect(conditionalSpriteVisible(FRAME_BOTTOM, "frame", left, 0)).toBe(true);
  });

  it("falls back to a wing's bottom mount while nothing connects", () => {
    expect(conditionalSpriteVisible(FRAME_TOP, "frame", undefined, 0)).toBe(false);
    expect(conditionalSpriteVisible(FRAME_BOTTOM, "frame", undefined, 0)).toBe(true);
  });

  it("shows the eight-way diagonals only on a 45 degree turn", () => {
    const upRight = new Set<LocalSide>(["topRight"]);
    expect(conditionalSpriteVisible(ATTACHMENT_UP_RIGHT, "attachmentEight", upRight, Math.PI / 4)).toBe(true);
    expect(conditionalSpriteVisible(ATTACHMENT_UP_RIGHT, "attachmentEight", upRight, 0)).toBe(false);
    expect(conditionalSpriteVisible(ATTACHMENT_UP_RIGHT, "attachmentEight", undefined, Math.PI / 4)).toBe(false);
  });
});
