import { describe, expect, it } from "vitest";
import { toDrawEntities } from "./toDrawEntities";
import type { SnapshotEntity } from "./types";

function snapshot(physicsBodyId: number, mirrored = false): SnapshotEntity {
  return {
    entityId: 7,
    physicsBodyId,
    partTypeId: 1,
    position: [1, 2, 0],
    rotation: [0, 0, 0, 1],
    linearVelocity: [0.5, 0, 0],
    angularVelocity: [0, 0, 1],
    scale: 1.5,
    attachYaw: 0.25,
    active: false,
    subEntity: false,
    mirrored,
  };
}

describe("toDrawEntities", () => {
  it("keeps physicsBodyId 0 as a preview bodyId", () => {
    const [entity] = toDrawEntities([snapshot(0)]);
    expect(entity.bodyId).toBe(0);
    expect(entity.x).toBe(1);
    expect(entity.y).toBe(2);
    expect(entity.vx).toBe(0.5);
    expect(entity.scale).toBe(1.5);
    expect(entity.active).toBe(false);
  });

  it("keeps a non-zero physicsBodyId as bodyId", () => {
    const [entity] = toDrawEntities([snapshot(19)]);
    expect(entity.bodyId).toBe(19);
  });

  it("carries the switch flag through", () => {
    const [entity] = toDrawEntities([{ ...snapshot(19), active: true }]);
    expect(entity.active).toBe(true);
  });

  it("carries the sub-entity flag through", () => {
    const [entity] = toDrawEntities([{ ...snapshot(19), subEntity: true }]);
    expect(entity.subEntity).toBe(true);
  });

  it("carries the attach frame onto the draw entity", () => {
    const [entity] = toDrawEntities([{ ...snapshot(19), attachYaw: 0.75 }]);
    expect(entity.attachYaw).toBe(0.75);
  });

  it("carries the mirror flag through", () => {
    const [plain] = toDrawEntities([snapshot(19)]);
    const [mirrored] = toDrawEntities([snapshot(19, true)]);
    expect(plain.mirrored).toBe(false);
    expect(mirrored.mirrored).toBe(true);
  });
});
