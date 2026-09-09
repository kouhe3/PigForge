// @vitest-environment jsdom
import { describe, expect, it } from "vitest";
import { attachCanvasGestures } from "./canvasGestures";
import { createCamera } from "@/renderer/camera";
import type { DrawEntity, GestureMessage } from "@/schema/types";

/** Minimal canvas mock: records listeners, captures pointers, gives a fixed rect. */
function makeCanvas() {
  const listeners: Record<string, (event: unknown) => void> = {};
  const canvas = {
    clientWidth: 800,
    clientHeight: 600,
    getBoundingClientRect: () => ({ left: 0, top: 0, width: 800, height: 600, right: 800, bottom: 600 }),
    setPointerCapture: () => {},
    releasePointerCapture: () => {},
    addEventListener: (type: string, fn: (event: unknown) => void) => {
      listeners[type] = fn;
    },
    removeEventListener: (type: string) => {
      delete listeners[type];
    },
  };
  return { canvas: canvas as unknown as HTMLCanvasElement, listeners };
}

function pointerEvent(type: string, x: number, y: number, pointerId = 1) {
  return { type, clientX: x, clientY: y, pointerId, altKey: false } as unknown as PointerEvent;
}

/** World -> CSS position on the 800x600 canvas for the default camera {x:4,y:2,scale:36}. */
function worldToCss(worldX: number, worldY: number): [number, number] {
  const width = 800;
  const height = 600;
  return [(worldX - 4) * 36 + width * 0.5, height * 0.5 - (worldY - 2) * 36];
}

const blocker: DrawEntity = { entityId: 7, partTypeId: 1, x: 0.5, y: 0.5, yaw: 0, scale: 1, vx: 0, vy: 0, bodyId: 7 };

describe("attachCanvasGestures selection vs placement", () => {
  it("tap on empty space emits PlaceRequested", () => {
    const { canvas, listeners } = makeCanvas();
    const messages: GestureMessage[] = [];
    const detach = attachCanvasGestures(canvas, createCamera(), { current: [] }, (m) => messages.push(m), { building: () => true });
    listeners.pointerdown(pointerEvent("pointerdown", 100, 100));
    listeners.pointerup(pointerEvent("pointerup", 100, 100));
    expect(messages.filter((m) => m.kind === "PlaceRequested")).toHaveLength(1);
    detach();
  });

  it("tap on an existing part selects it and does NOT place", () => {
    const { canvas, listeners } = makeCanvas();
    const messages: GestureMessage[] = [];
    const detach = attachCanvasGestures(canvas, createCamera(), { current: [blocker] }, (m) => messages.push(m), { building: () => true });
    const [x, y] = worldToCss(blocker.x, blocker.y);
    listeners.pointerdown(pointerEvent("pointerdown", x, y));
    listeners.pointerup(pointerEvent("pointerup", x, y));
    expect(messages.filter((m) => m.kind === "PlaceRequested")).toHaveLength(0);
    expect(messages.find((m) => m.kind === "SelectEntity")).toEqual({ kind: "SelectEntity", entityId: blocker.entityId });
    detach();
  });

});
