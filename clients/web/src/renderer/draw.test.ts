// @vitest-environment jsdom
import { describe, expect, it } from "vitest";
import { drawFrame } from "./draw";
import { createCamera } from "./camera";
import type { DrawEntity, PartContentDocument } from "@/schema/types";

function makeCtx() {
  const calls: Record<string, number> = {};
  const gradient: CanvasGradient = { addColorStop: () => {} } as unknown as CanvasGradient;
  const ctx = {
    canvas: { clientWidth: 800, clientHeight: 600, width: 800, height: 600 },
    fillRect: () => {
      calls.fillRect = (calls.fillRect ?? 0) + 1;
    },
    strokeRect: () => {
      calls.strokeRect = (calls.strokeRect ?? 0) + 1;
    },
    beginPath: () => {
      calls.beginPath = (calls.beginPath ?? 0) + 1;
    },
    arc: () => {
      calls.arc = (calls.arc ?? 0) + 1;
    },
    fill: () => {
      calls.fill = (calls.fill ?? 0) + 1;
    },
    stroke: () => {
      calls.stroke = (calls.stroke ?? 0) + 1;
    },
    fillText: () => {},
    moveTo: () => {},
    lineTo: () => {},
    save: () => {},
    restore: () => {},
    translate: () => {},
    rotate: () => {},
    setLineDash: () => {},
    createRadialGradient: () => {
      calls.createRadialGradient = (calls.createRadialGradient ?? 0) + 1;
      return gradient;
    },
    fillStyle: "",
    strokeStyle: "",
    lineWidth: 1,
    font: "",
    textAlign: "",
    textBaseline: "",
  };
  return { ctx: ctx as unknown as CanvasRenderingContext2D, calls };
}

const content: PartContentDocument = {
  format: "pigforge.part-content",
  schemaVersion: 1,
  contentVersion: "t",
  parts: [
    { partTypeId: 44, name: "flashlight", mode: "dynamic", mass: 0.4, capabilities: { light: 3 }, shapes: [{ kind: "box", halfExtents: [0.2, 0.2, 0.2] }] },
    { partTypeId: 1, name: "block", mode: "dynamic", mass: 1, shapes: [{ kind: "box", halfExtents: [0.5, 0.5, 0.5] }] },
  ],
};

const light: DrawEntity = { entityId: 1, partTypeId: 44, x: 2, y: 2, yaw: 0, scale: 1, vx: 0, vy: 0 };
const block: DrawEntity = { entityId: 2, partTypeId: 1, x: 4, y: 2, yaw: 0, scale: 1, vx: 0, vy: 0 };

describe("drawFrame light halo", () => {
  it("casts a radial glow for light parts", () => {
    const { ctx, calls } = makeCtx();
    drawFrame(ctx, createCamera(), [light], content, null);
    expect(calls.createRadialGradient).toBe(1);
    expect(calls.arc).toBe(1); // the glow circle; the box part itself is a rect
  });

  it("does not glow for plain parts", () => {
    const { ctx, calls } = makeCtx();
    drawFrame(ctx, createCamera(), [block], content, null);
    expect(calls.createRadialGradient).toBeUndefined();
  });
});
