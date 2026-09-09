// @vitest-environment jsdom
import { describe, expect, it } from "vitest";
import { drawFrame } from "./draw";
import { createCamera } from "./camera";
import type { DrawEntity, PartContentDocument } from "@/schema/types";

function makeCtx() {
  const calls: Record<string, number> = {};
  const alphas: number[] = [];
  const translations: Array<[number, number]> = [];
  const alpha = { value: 1 };
  const gradient: CanvasGradient = { addColorStop: () => {} } as unknown as CanvasGradient;
  const ctx = {
    canvas: { clientWidth: 800, clientHeight: 600, width: 800, height: 600 },
    get globalAlpha(): number {
      return alpha.value;
    },
    set globalAlpha(value: number) {
      alpha.value = value;
    },
    fillRect: () => {
      calls.fillRect = (calls.fillRect ?? 0) + 1;
      alphas.push(alpha.value);
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
    fillText: () => {
      calls.fillText = (calls.fillText ?? 0) + 1;
    },
    moveTo: () => {},
    lineTo: () => {},
    save: () => {},
    restore: () => {},
    translate: (x: number, y: number) => {
      translations.push([x, y]);
    },
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
  return { ctx: ctx as unknown as CanvasRenderingContext2D, calls, alphas, translations };
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

const light: DrawEntity = { entityId: 1, partTypeId: 44, x: 2, y: 2, yaw: 0, scale: 1, vx: 0, vy: 0, bodyId: 11 };
const block: DrawEntity = { entityId: 2, partTypeId: 1, x: 4, y: 2, yaw: 0, scale: 1, vx: 0, vy: 0, bodyId: 12 };
const preview: DrawEntity = { ...light, entityId: 3, bodyId: 0 };

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

describe("drawFrame previews", () => {
  it("renders bodyId 0 translucent, without a halo or name label", () => {
    const { ctx, calls, alphas } = makeCtx();
    drawFrame(ctx, createCamera(), [preview], content, null);
    expect(calls.createRadialGradient).toBeUndefined();
    expect(calls.fillRect).toBe(2); // background + the part shape
    expect(alphas[alphas.length - 1]).toBeCloseTo(0.45);
    expect(calls.fillText).toBeUndefined();
  });

  it("keeps the halo, full alpha, and the label for a live body", () => {
    const { ctx, calls, alphas } = makeCtx();
    drawFrame(ctx, createCamera(), [light], content, null);
    expect(calls.createRadialGradient).toBe(1);
    expect(alphas[alphas.length - 1]).toBe(1);
    expect(calls.fillText).toBe(1);
  });
});

describe("drawFrame entity placement", () => {
  it("translates each entity to its own screen position before drawing", () => {
    const { ctx, translations } = makeCtx();
    drawFrame(ctx, createCamera(), [light, block], content, null);
    // camera { x: 4, y: 2, scale: 36 } over an 800x600 canvas:
    // world (2,2) -> (328,300), world (4,2) -> (400,300).
    expect(translations).toEqual([
      [328, 300],
      [400, 300],
    ]);
  });
});
