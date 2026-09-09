// @vitest-environment jsdom
import { describe, expect, it } from "vitest";
import { attachCanvasGestures, type CanvasGestureOptions } from "./canvasGestures";
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

function pointerEvent(type: string, x: number, y: number, altKey = false, pointerId = 1) {
  return { type, clientX: x, clientY: y, pointerId, altKey } as unknown as PointerEvent;
}

/** World -> CSS position on the 800x600 canvas for the default camera {x:4,y:2,scale:36}. */
function worldToCss(worldX: number, worldY: number): [number, number] {
  const width = 800;
  const height = 600;
  return [(worldX - 4) * 36 + width * 0.5, height * 0.5 - (worldY - 2) * 36];
}

function findMessage<T extends GestureMessage["kind"]>(messages: GestureMessage[], kind: T) {
  return messages.find((message) => message.kind === kind) as Extract<GestureMessage, { kind: T }> | undefined;
}

function attach(entities: DrawEntity[], options: CanvasGestureOptions) {
  const { canvas, listeners } = makeCanvas();
  const messages: GestureMessage[] = [];
  const detach = attachCanvasGestures(canvas, createCamera(), { current: entities }, (m) => messages.push(m), options);
  return { listeners, messages, detach };
}

const part: DrawEntity = { entityId: 7, partTypeId: 1, x: 0.5, y: 0.5, yaw: 0, scale: 1, vx: 0, vy: 0, bodyId: 0, active: false };

describe("canvas gestures: place and select", () => {
  it("places on an empty tap with the place tool", () => {
    const { listeners, messages, detach } = attach([], { tool: () => "place", canPlace: () => true });
    listeners.pointerdown(pointerEvent("pointerdown", 100, 100));
    listeners.pointerup(pointerEvent("pointerup", 100, 100));
    expect(messages.filter((m) => m.kind === "PlaceRequested")).toHaveLength(1);
    detach();
  });

  it("never places with the select tool and clears the selection on empty space", () => {
    const { listeners, messages, detach } = attach([], { tool: () => "select", canPlace: () => true });
    listeners.pointerdown(pointerEvent("pointerdown", 100, 100));
    listeners.pointerup(pointerEvent("pointerup", 100, 100));
    expect(messages.filter((m) => m.kind === "PlaceRequested")).toHaveLength(0);
    expect(findMessage(messages, "SelectEntity")).toEqual({ kind: "SelectEntity", entityId: null });
    detach();
  });

  it("selects an existing part and never places on top of it", () => {
    const [x, y] = worldToCss(part.x, part.y);
    const { listeners, messages, detach } = attach([part], { tool: () => "place", canPlace: () => true });
    listeners.pointerdown(pointerEvent("pointerdown", x, y));
    listeners.pointerup(pointerEvent("pointerup", x, y));
    expect(messages.filter((m) => m.kind === "PlaceRequested")).toHaveLength(0);
    expect(findMessage(messages, "SelectEntity")).toEqual({ kind: "SelectEntity", entityId: part.entityId });
    detach();
  });
});

describe("canvas gestures: transform tools", () => {
  it("moves an editable part with grid snap and emits one request on release", () => {
    const [x, y] = worldToCss(part.x, part.y);
    const { listeners, messages, detach } = attach([part], { tool: () => "move", isEditable: () => true });
    listeners.pointerdown(pointerEvent("pointerdown", x, y));
    listeners.pointermove(pointerEvent("pointermove", x + 12, y - 12));
    expect(findMessage(messages, "ToolPreview")?.preview).toEqual({ entityId: 7, x: 1, y: 1, yaw: 0, scale: 1 });
    listeners.pointerup(pointerEvent("pointerup", x + 12, y - 12));
    expect(findMessage(messages, "MoveRequested")).toEqual({ kind: "MoveRequested", entityId: 7, x: 1, y: 1 });
    expect(messages[messages.length - 1]).toEqual({ kind: "ToolPreview", preview: null });
    detach();
  });

  it("keeps raw coordinates while Alt is held", () => {
    const [x, y] = worldToCss(part.x, part.y);
    const { listeners, messages, detach } = attach([part], { tool: () => "move", isEditable: () => true });
    listeners.pointerdown(pointerEvent("pointerdown", x, y));
    listeners.pointermove(pointerEvent("pointermove", x + 12, y, true));
    expect(findMessage(messages, "ToolPreview")?.preview?.x).toBeCloseTo(5 / 6);
    detach();
  });

  it("rotates around the part centre and snaps to 15 degrees", () => {
    const [x, y] = worldToCss(part.x, part.y);
    const { listeners, messages, detach } = attach([part], { tool: () => "rotate", isEditable: () => true });
    listeners.pointerdown(pointerEvent("pointerdown", x + 9, y));
    listeners.pointermove(pointerEvent("pointermove", x, y - 9));
    listeners.pointerup(pointerEvent("pointerup", x, y - 9));
    const request = findMessage(messages, "RotateRequested");
    expect(request?.entityId).toBe(7);
    expect(request?.angle).toBeCloseTo(Math.PI / 2);
    detach();
  });

  it("scales by the pointer distance ratio", () => {
    const [x, y] = worldToCss(part.x, part.y);
    const { listeners, messages, detach } = attach([part], { tool: () => "scale", isEditable: () => true });
    listeners.pointerdown(pointerEvent("pointerdown", x + 9, y));
    listeners.pointermove(pointerEvent("pointermove", x + 18, y));
    listeners.pointerup(pointerEvent("pointerup", x + 18, y));
    expect(findMessage(messages, "ScaleRequested")).toEqual({ kind: "ScaleRequested", entityId: 7, scale: 2 });
    detach();
  });

  it("does not transform another player's part; the drag pans the camera instead", () => {
    const [x, y] = worldToCss(part.x, part.y);
    const { listeners, messages, detach } = attach([part], { tool: () => "move", isEditable: () => false });
    listeners.pointerdown(pointerEvent("pointerdown", x, y));
    listeners.pointermove(pointerEvent("pointermove", x + 40, y));
    listeners.pointerup(pointerEvent("pointerup", x + 40, y));
    expect(messages.some((m) => m.kind === "MoveRequested")).toBe(false);
    expect(messages.some((m) => m.kind === "CameraChanged")).toBe(true);
    detach();
  });

  it("emits no request for a click that never crosses the drag threshold", () => {
    const [x, y] = worldToCss(part.x, part.y);
    const { listeners, messages, detach } = attach([part], { tool: () => "move", isEditable: () => true });
    listeners.pointerdown(pointerEvent("pointerdown", x, y));
    listeners.pointerup(pointerEvent("pointerup", x, y));
    expect(messages.some((m) => m.kind === "MoveRequested")).toBe(false);
    detach();
  });
});
