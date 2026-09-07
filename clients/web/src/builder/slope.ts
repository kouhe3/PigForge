import type { PartContentDocument } from "@/schema/types";

export const PLAY_PARTS: PartContentDocument = {
  format: "pigforge.part-content",
  schemaVersion: 1,
  contentVersion: "pigforge-base-content-v1",
  parts: [
    { partTypeId: 1, name: "wooden-block", mode: "dynamic", mass: 1, shapes: [{ kind: "box", halfExtents: [0.5, 0.5, 0.5] }] },
    { partTypeId: 3, name: "ball-weight", mode: "dynamic", mass: 2.5, shapes: [{ kind: "sphere", radius: 0.4 }] },
    { partTypeId: 4, name: "pig", mode: "dynamic", mass: 1, shapes: [{ kind: "box", halfExtents: [0.4, 0.4, 0.4] }] },
    { partTypeId: 6, name: "ramp-plank", mode: "static", mass: 0, shapes: [{ kind: "box", halfExtents: [6, 0.25, 1] }] },
  ],
};

export const PALETTE = [
  { partTypeId: 4, label: "猪" },
  { partTypeId: 1, label: "木块" },
  { partTypeId: 3, label: "配重球" },
] as const;

export const GOAL_ZONE = { minX: 4.5, minY: -0.5, maxX: 7.5, maxY: 2.5 };
