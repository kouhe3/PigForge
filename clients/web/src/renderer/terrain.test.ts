import { describe, expect, it } from "vitest";
import { createCamera } from "./camera";
import { drawFrame, loopToWorld } from "./draw";
import type { DrawEntity } from "@/schema/types";
import type { LevelTerrain } from "@/schema/levelContent";

const terrain: LevelTerrain = { position: [-2.79, 9.02, 0], depth: 10, loops: [[[0, 0], [1, 0], [0, 1]]] };

const block: DrawEntity = { entityId: 1, partTypeId: 1, x: 4, y: 2, yaw: 0, scale: 1, vx: 0, vy: 0, bodyId: 12, active: false };

function makeRecorder() {
  const ops: string[] = [];
  const fills: string[] = [];
  let fillStyle = "";
  const recorder = {
    canvas: { clientWidth: 800, clientHeight: 600, width: 800, height: 600 },
    get fillStyle(): string {
      return fillStyle;
    },
    set fillStyle(value: string) {
      fillStyle = value;
    },
    strokeStyle: "",
    lineWidth: 1,
    globalAlpha: 1,
    font: "",
    textAlign: "",
    textBaseline: "",
    fillRect: () => { ops.push("fillRect"); },
    strokeRect: () => { ops.push("strokeRect"); },
    beginPath: () => { ops.push("beginPath"); },
    moveTo: () => { ops.push("moveTo"); },
    lineTo: () => { ops.push("lineTo"); },
    closePath: () => { ops.push("closePath"); },
    fill: () => {
      ops.push("fill");
      fills.push(fillStyle);
    },
    stroke: () => { ops.push("stroke"); },
    arc: () => { ops.push("arc"); },
    drawImage: () => { ops.push("drawImage"); },
    fillText: () => { ops.push("fillText"); },
    save: () => {},
    restore: () => {},
    translate: () => {},
    rotate: () => {},
    scale: () => {},
    setLineDash: () => {},
    createRadialGradient: () => ({ addColorStop: () => {} }),
  };
  return { ctx: recorder as unknown as CanvasRenderingContext2D, ops, fills };
}

function count(ops: readonly string[], op: string): number {
  return ops.filter((entry) => entry === op).length;
}

/** How many `op`s one frame draws on top of the same frame without terrain (the grid aside). */
function groundOps(terrains: readonly LevelTerrain[], op: string): number {
  const bare = makeRecorder();
  drawFrame(bare.ctx, createCamera(), [], null, []);
  const ground = makeRecorder();
  drawFrame(ground.ctx, createCamera(), [], null, [], undefined, undefined, null, null, null, terrains);
  return count(ground.ops, op) - count(bare.ops, op);
}

describe("loopToWorld", () => {
  it("translates the terrain's local loop by its position", () => {
    expect(loopToWorld(terrain, [[0, 0], [1, 0], [0, 1]])).toEqual([
      { x: -2.79, y: 9.02 },
      { x: -1.79, y: 9.02 },
      { x: -2.79, y: 10.02 },
    ]);
  });

  it("keeps the terrain's own z out of the plane projection", () => {
    const raised: LevelTerrain = { ...terrain, position: [-2.79, 9.02, 40] };
    expect(loopToWorld(raised, [[2, 3]])).toEqual([{ x: -0.79, y: 12.02 }]);
  });
});

describe("drawFrame terrain", () => {
  it("fills every loop with the static ground green and closes its outline", () => {
    const { ctx, fills } = makeRecorder();
    drawFrame(ctx, createCamera(), [], null, [], undefined, undefined, null, null, null, [terrain]);
    expect(groundOps([terrain], "moveTo")).toBe(1);
    expect(groundOps([terrain], "lineTo")).toBe(2);
    expect(groundOps([terrain], "closePath")).toBe(1);
    expect(groundOps([terrain], "fill")).toBe(1);
    expect(fills).toEqual(["#5c6b52"]);
  });

  it("fills once per loop across terrains", () => {
    const second: LevelTerrain = { position: [10, 0, 0], depth: 5, loops: [[[0, 0], [1, 0], [0, 1]], [[0, 0], [2, 0], [0, 2], [1, 1]]] };
    expect(groundOps([terrain, second], "fill")).toBe(3);
    expect(groundOps([terrain, second], "moveTo")).toBe(3);
    expect(groundOps([terrain, second], "lineTo")).toBe(7);
  });

  it("draws the ground after the background grid and before every entity", () => {
    const { ctx, ops } = makeRecorder();
    drawFrame(ctx, createCamera(), [block], null, [], undefined, undefined, null, null, null, [terrain]);
    expect(ops.indexOf("fill")).toBeGreaterThan(ops.indexOf("stroke"));
    expect(ops.indexOf("fill")).toBeLessThan(ops.lastIndexOf("fillRect"));
    expect(ops[0]).toBe("fillRect");
  });

  it("draws no ground when the level has no terrain", () => {
    const { ctx, ops } = makeRecorder();
    drawFrame(ctx, createCamera(), [], null, []);
    expect(ops).not.toContain("fill");
    expect(ops).not.toContain("closePath");
  });
});
