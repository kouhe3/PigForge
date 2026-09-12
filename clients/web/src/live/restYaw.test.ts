import { describe, expect, it } from "vitest";
import { createRestYawTracker } from "./restYaw";
import type { DrawEntity } from "@/schema/types";

function entity(overrides: Partial<DrawEntity> = {}): DrawEntity {
  return { entityId: 1, partTypeId: 7, x: 0, y: 0, yaw: 0.25, scale: 1, vx: 0, vy: 0, bodyId: 0, active: false, ...overrides };
}

describe("restYawTracker", () => {
  it("remembers the build orientation a preview reported", () => {
    const tracker = createRestYawTracker();
    tracker.apply([entity({ yaw: 0.25, bodyId: 0 })], false);
    const [rolling] = tracker.apply([entity({ yaw: 1.75, bodyId: 42 })], false);

    expect(rolling.restYaw).toBeCloseTo(0.25);
    expect(rolling.yaw).toBeCloseTo(1.75);
  });

  it("keeps refreshing while the layout is still a preview", () => {
    const tracker = createRestYawTracker();
    tracker.apply([entity({ yaw: 0.25 })], false);
    tracker.apply([entity({ yaw: 0.5 })], false);
    const [rolling] = tracker.apply([entity({ yaw: 2.5, bodyId: 7 })], false);

    expect(rolling.restYaw).toBeCloseTo(0.5);
  });

  it("treats every entity as layout in a building frame", () => {
    const tracker = createRestYawTracker();
    tracker.apply([entity({ yaw: 0.25, bodyId: 42 })], false);
    tracker.apply([entity({ yaw: 0.4, bodyId: 42 })], true);
    const [rolling] = tracker.apply([entity({ yaw: 3.1, bodyId: 42 })], false);

    expect(rolling.restYaw).toBeCloseTo(0.4);
  });

  it("falls back to the first observed pose when no preview was ever seen", () => {
    const tracker = createRestYawTracker();
    const [first] = tracker.apply([entity({ yaw: 1.1, bodyId: 42 })], false);
    const [second] = tracker.apply([entity({ yaw: 2.2, bodyId: 42 })], false);

    expect(first.restYaw).toBeCloseTo(1.1);
    expect(second.restYaw).toBeCloseTo(1.1);
  });

  it("forgets entities that leave the frame", () => {
    const tracker = createRestYawTracker();
    tracker.apply([entity({ yaw: 0.25 })], false);
    tracker.apply([], false);
    const [reappeared] = tracker.apply([entity({ yaw: 2.0, bodyId: 9 })], false);

    expect(reappeared.restYaw).toBeCloseTo(2.0);
  });
});
