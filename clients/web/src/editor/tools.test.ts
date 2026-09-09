import { describe, expect, it } from "vitest";
import {
  MAX_SCALE,
  MIN_SCALE,
  entitiesInBox,
  movePose,
  pointerAngle,
  pointerDistance,
  rotatePose,
  scalePose,
  shortestAngleDelta,
  snapAngle,
  snapBoxOf,
  snapMove,
  snapMoveToParts,
  snapScale,
  toolByHotkey,
} from "./tools";
import type { DrawEntity, PartDefinition } from "@/schema/types";

describe("editor tools", () => {
  it("maps hotkeys to tool ids", () => {
    expect(toolByHotkey("3")).toBe("move");
    expect(toolByHotkey("5")).toBe("scale");
    expect(toolByHotkey("9")).toBeNull();
  });

  it("snaps move to the absolute 0.5 grid and Alt keeps the raw value", () => {
    expect(snapMove(1.24, true)).toBe(1);
    expect(snapMove(1.26, true)).toBe(1.5);
    expect(snapMove(1.24, false)).toBe(1.24);
  });

  it("snaps rotation to 15 degrees", () => {
    expect(snapAngle(0.35, true)).toBeCloseTo(Math.PI / 12);
    expect(snapAngle(0.35, false)).toBeCloseTo(0.35);
  });

  it("snaps scale to 0.25 and clamps to the protocol range even without snapping", () => {
    expect(snapScale(1.13, true)).toBeCloseTo(1.25);
    expect(snapScale(10, true)).toBe(MAX_SCALE);
    expect(snapScale(10, false)).toBe(MAX_SCALE);
    expect(snapScale(0.01, false)).toBe(MIN_SCALE);
  });

  it("wraps angle deltas across the ±π seam", () => {
    expect(shortestAngleDelta(3, -3)).toBeCloseTo(2 * Math.PI - 6);
    expect(shortestAngleDelta(-3, 3)).toBeCloseTo(6 - 2 * Math.PI);
    expect(shortestAngleDelta(0.1, 0.4)).toBeCloseTo(0.3);
  });

  it("measures pointer angle and distance from the part centre", () => {
    expect(pointerAngle({ x: 0, y: 0 }, { x: 0, y: 1 })).toBeCloseTo(Math.PI / 2);
    expect(pointerDistance({ x: 1, y: 1 }, { x: 4, y: 5 })).toBeCloseTo(5);
  });

  it("moves from the start pose by the world delta", () => {
    const start = { x: 0.3, y: 0.7, yaw: 1, scale: 2 };
    expect(movePose(start, 0.1, -0.2, true)).toEqual({ x: 0.5, y: 0.5, yaw: 1, scale: 2 });
    const raw = movePose(start, 0.1, -0.2, false);
    expect(raw.x).toBeCloseTo(0.4);
    expect(raw.y).toBeCloseTo(0.5);
    expect(raw.yaw).toBe(1);
    expect(raw.scale).toBe(2);
  });

  it("rotates from the start pose by the accumulated pointer delta", () => {
    const start = { x: 1, y: 2, yaw: Math.PI / 12, scale: 1 };
    expect(rotatePose(start, Math.PI / 12, true).yaw).toBeCloseTo(Math.PI / 6);
    expect(rotatePose(start, 0.4, false).yaw).toBeCloseTo(Math.PI / 12 + 0.4);
  });

  it("scales from the start pose by the pointer distance ratio and guards bad ratios", () => {
    const start = { x: 1, y: 2, yaw: 0, scale: 1.5 };
    expect(scalePose(start, 2, true).scale).toBeCloseTo(3);
    expect(scalePose(start, Number.NaN, true).scale).toBeCloseTo(1.5);
    expect(scalePose(start, 0, true).scale).toBeCloseTo(1.5);
    expect(scalePose(start, 10, false).scale).toBe(MAX_SCALE);
  });

  it("selects entities whose centre is inside the box, ascending", () => {
    const entity = (entityId: number, x: number, y: number): DrawEntity => ({
      entityId, partTypeId: 1, x, y, yaw: 0, scale: 1, vx: 0, vy: 0, bodyId: 1, active: false,
    });
    const entities = [entity(9, 0.5, 0.5), entity(3, -1, 2), entity(5, 4, 0.5)];

    expect(entitiesInBox(entities, -2, -2, 1, 3)).toEqual([3, 9]);
    expect(entitiesInBox(entities, 0.5, 0.5, 0.5, 0.5)).toEqual([9]); // boundary counts
    expect(entitiesInBox(entities, 10, 10, 11, 11)).toEqual([]);
  });
});

describe("part contact snap", () => {
  const base: DrawEntity = {
    entityId: 7, partTypeId: 1, x: 2, y: 3, yaw: 0, scale: 1, vx: 0, vy: 0, bodyId: 0, active: false,
  };
  const block: PartDefinition = {
    partTypeId: 1, name: "block", mode: "dynamic", mass: 1,
    shapes: [{ kind: "box", halfExtents: [0.5, 0.25, 0.5] }],
  };

  it("projects a shape into world-axis half extents with yaw and scale applied", () => {
    const projected = snapBoxOf({ ...base, yaw: Math.PI / 2, scale: 2 }, block);
    expect(projected).toMatchObject({ entityId: 7, x: 2, y: 3 });
    expect(projected?.halfX).toBeCloseTo(0.5);
    expect(projected?.halfY).toBeCloseTo(1);
  });

  it("uses the sphere radius on both axes", () => {
    const sphere: PartDefinition = { ...block, shapes: [{ kind: "sphere", radius: 0.4 }] };
    const projected = snapBoxOf({ ...base, scale: 1.5 }, sphere);
    expect(projected?.halfX).toBeCloseTo(0.6);
    expect(projected?.halfY).toBeCloseTo(0.6);
  });

  it("has no box without a part or a usable shape", () => {
    expect(snapBoxOf(base, undefined)).toBeNull();
    expect(snapBoxOf(base, { ...block, shapes: [{ kind: "convexMesh" }] })).toBeNull();
  });

  const self = { entityId: 2, halfX: 0.45, halfY: 0.45 };
  const neighbour = { entityId: 1, x: 0, y: 0, halfX: 0.475, halfY: 0.475 };

  it("snaps flush against a neighbour inside the threshold", () => {
    const snapped = snapMoveToParts(0.93, 0.05, self, [neighbour]);
    expect(snapped.x).toBeCloseTo(0.925);
    expect(snapped.y).toBeCloseTo(0.05);
  });

  it("keeps the raw candidate when the neighbour is off the other axis", () => {
    expect(snapMoveToParts(0.93, 0.05, self, [{ ...neighbour, y: 5 }])).toEqual({ x: 0.93, y: 0.05 });
  });

  it("ignores contacts past the threshold and never snaps to itself", () => {
    expect(snapMoveToParts(0.05, 0.05, self, [{ ...neighbour, x: -0.5 }])).toEqual({ x: 0.05, y: 0.05 });
    expect(snapMoveToParts(0.93, 0.05, { ...self, entityId: 1 }, [neighbour])).toEqual({ x: 0.93, y: 0.05 });
  });

  it("moves freely by default, snaps flush with contacts, and grids only with Alt", () => {
    const start = { x: 0.5, y: 0.5, yaw: 1, scale: 2 };
    const free = movePose(start, 0.43, -0.45, false);
    expect(free.x).toBeCloseTo(0.93);
    expect(free.y).toBeCloseTo(0.05);
    expect(free.yaw).toBe(1);
    expect(free.scale).toBe(2);
    const contacted = movePose(start, 0.43, -0.45, false, { self, others: [neighbour] });
    expect(contacted.x).toBeCloseTo(0.925);
    expect(contacted.y).toBeCloseTo(0.05);
    expect(movePose(start, 0.43, -0.45, true)).toEqual({ x: 1, y: 0, yaw: 1, scale: 2 });
  });
});
