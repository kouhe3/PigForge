import type { PartContentDocument } from "@/schema/types";

export const PLAY_PARTS: PartContentDocument = {
  format: "pigforge.part-content",
  schemaVersion: 1,
  contentVersion: "pigforge-base-content-v1",
  parts: [
    { partTypeId: 1, name: "wooden-block", mode: "dynamic", mass: 1, shapes: [{ kind: "box", halfExtents: [0.5, 0.5, 0.5] }] },
    { partTypeId: 3, name: "ball-weight", mode: "dynamic", mass: 2.5, shapes: [{ kind: "sphere", radius: 0.4 }] },
    { partTypeId: 4, name: "pig", mode: "dynamic", mass: 1, capabilities: { pig: true }, shapes: [{ kind: "box", halfExtents: [0.4, 0.4, 0.4] }] },
    { partTypeId: 7, name: "wheel", mode: "dynamic", mass: 0.5, capabilities: { wheel: true }, shapes: [{ kind: "sphere", radius: 0.4 }] },
    { partTypeId: 8, name: "engine", mode: "dynamic", mass: 1.2, capabilities: { motor: { thrustPerTick: 2, directionX: 1 } }, shapes: [{ kind: "box", halfExtents: [0.4, 0.3, 0.4] }] },
    { partTypeId: 9, name: "tnt", mode: "dynamic", mass: 1, capabilities: { tnt: { fuseTicks: 5 } }, shapes: [{ kind: "box", halfExtents: [0.35, 0.35, 0.35] }] },
    { partTypeId: 6, name: "ramp-plank", mode: "static", mass: 0, shapes: [{ kind: "box", halfExtents: [6, 0.25, 1] }] },
  ],
};

export const PALETTE = [
  { partTypeId: 4, label: "猪" },
  { partTypeId: 1, label: "木块" },
  { partTypeId: 3, label: "配重球" },
  { partTypeId: 7, label: "轮子" },
  { partTypeId: 8, label: "发动机" },
  { partTypeId: 9, label: "TNT" },
] as const;

export const GOAL_ZONE = { minX: 12, minY: -0.5, maxX: 16, maxY: 2.5 };
export const MAP_BOUNDS = { minX: -30, minY: -12, maxX: 30, maxY: 30 };
