// @vitest-environment jsdom
import { describe, expect, it } from "vitest";
import { attachCanvasGestures, type CanvasGestureOptions } from "./canvasGestures";
import { createCamera } from "@/renderer/camera";
import type { DrawEntity, GestureMessage, PartDefinition } from "@/schema/types";

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

function pointerEvent(type: string, x: number, y: number, altKey = false, pointerId = 1, button = 0, shiftKey = false) {
  return { type, clientX: x, clientY: y, pointerId, altKey, button, shiftKey, preventDefault: () => {} } as unknown as PointerEvent;
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
    expect(findMessage(messages, "SelectEntities")).toEqual({ kind: "SelectEntities", entityIds: [], mode: "replace" });
    detach();
  });

  it("selects an existing part and never places on top of it", () => {
    const [x, y] = worldToCss(part.x, part.y);
    const { listeners, messages, detach } = attach([part], { tool: () => "place", canPlace: () => true });
    listeners.pointerdown(pointerEvent("pointerdown", x, y));
    listeners.pointerup(pointerEvent("pointerup", x, y));
    expect(messages.filter((m) => m.kind === "PlaceRequested")).toHaveLength(0);
    expect(findMessage(messages, "SelectEntities")).toEqual({ kind: "SelectEntities", entityIds: [part.entityId], mode: "replace" });
    detach();
  });
});

describe("canvas gestures: nesting into a frame", () => {
  // `part` is already partTypeId 1, the frame entry below.
  const frame: DrawEntity = { ...part, x: 3, y: 2 };
  const framePart: PartDefinition = { partTypeId: 1, name: "frame", mode: "static", mass: 1, capabilities: { canEnclose: true }, shapes: [] };
  const pigPart: PartDefinition = { partTypeId: 4, name: "pig", mode: "dynamic", mass: 1, capabilities: { pig: true }, shapes: [] };
  const nestOptions = (armed: number): CanvasGestureOptions => ({
    tool: () => "place",
    canPlace: () => true,
    // A frame is content-marked as one; everything else stays enclosable (canEnclose absent).
    partOf: (partTypeId) => (partTypeId === 1 ? framePart : pigPart),
    armedPart: () => armed,
  });

  it("places the armed part into the frame's cell and never selects it", () => {
    const [x, y] = worldToCss(3.2, 2.2);
    const { listeners, messages, detach } = attach([frame], nestOptions(4));
    listeners.pointerdown(pointerEvent("pointerdown", x, y));
    listeners.pointerup(pointerEvent("pointerup", x, y));

    // The frame's own cell centre, not the pointer that landed inside the hit radius.
    expect(findMessage(messages, "PlaceRequested")).toEqual({ kind: "PlaceRequested", x: 3, y: 2 });
    expect(messages.some((m) => m.kind === "SelectEntities")).toBe(false);
    detach();
  });

  it("keeps a tap on a frame armed with a frame as a plain selection", () => {
    const [x, y] = worldToCss(frame.x, frame.y);
    const { listeners, messages, detach } = attach([frame], nestOptions(1));
    listeners.pointerdown(pointerEvent("pointerdown", x, y));
    listeners.pointerup(pointerEvent("pointerup", x, y));

    expect(messages.some((m) => m.kind === "PlaceRequested")).toBe(false);
    expect(findMessage(messages, "SelectEntities")).toEqual({ kind: "SelectEntities", entityIds: [frame.entityId], mode: "replace" });
    detach();
  });

  it("keeps a tap on an enclosing-less part as a plain selection", () => {
    const pig: DrawEntity = { ...frame, partTypeId: 4 };
    const [x, y] = worldToCss(pig.x, pig.y);
    const { listeners, messages, detach } = attach([pig], nestOptions(4));
    listeners.pointerdown(pointerEvent("pointerdown", x, y));
    listeners.pointerup(pointerEvent("pointerup", x, y));

    expect(messages.some((m) => m.kind === "PlaceRequested")).toBe(false);
    expect(findMessage(messages, "SelectEntities")).toEqual({ kind: "SelectEntities", entityIds: [pig.entityId], mode: "replace" });
    detach();
  });
});

describe("canvas gestures: transform tools", () => {
  it("moves an editable part freely by default and emits one request on release", () => {
    const [x, y] = worldToCss(part.x, part.y);
    const { listeners, messages, detach } = attach([part], { tool: () => "move", isEditable: () => true });
    listeners.pointerdown(pointerEvent("pointerdown", x, y));
    listeners.pointermove(pointerEvent("pointermove", x + 12, y - 12));
    const preview = findMessage(messages, "ToolPreview")?.preview;
    expect(preview?.entityId).toBe(7);
    expect(preview?.x).toBeCloseTo(5 / 6);
    expect(preview?.y).toBeCloseTo(5 / 6);
    listeners.pointerup(pointerEvent("pointerup", x + 12, y - 12));
    const request = findMessage(messages, "MoveRequested");
    expect(request?.entityId).toBe(7);
    expect(request?.x).toBeCloseTo(5 / 6);
    expect(messages[messages.length - 1]).toEqual({ kind: "ToolPreview", preview: null });
    detach();
  });

  it("snaps a move to the 0.5 grid while Alt is held", () => {
    const [x, y] = worldToCss(part.x, part.y);
    const { listeners, messages, detach } = attach([part], { tool: () => "move", isEditable: () => true });
    listeners.pointerdown(pointerEvent("pointerdown", x, y));
    listeners.pointermove(pointerEvent("pointermove", x + 12, y, true));
    expect(findMessage(messages, "ToolPreview")?.preview).toMatchObject({ x: 1, y: 0.5 });
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

  it("snaps a move drag flush against a neighbouring part", () => {
    const dragged: DrawEntity = { ...part, entityId: 7, partTypeId: 1 };
    const neighbour: DrawEntity = { ...part, entityId: 8, partTypeId: 2, x: 2 };
    const parts: Record<number, PartDefinition> = {
      1: { partTypeId: 1, name: "a", mode: "dynamic", mass: 1, shapes: [{ kind: "box", halfExtents: [0.45, 0.45, 0.5] }] },
      2: { partTypeId: 2, name: "b", mode: "dynamic", mass: 1, shapes: [{ kind: "box", halfExtents: [0.475, 0.475, 0.5] }] },
    };
    const [x, y] = worldToCss(dragged.x, dragged.y);
    const { listeners, messages, detach } = attach([dragged, neighbour], {
      tool: () => "move",
      isEditable: () => true,
      partOf: (partTypeId) => parts[partTypeId],
    });
    listeners.pointerdown(pointerEvent("pointerdown", x, y));
    listeners.pointermove(pointerEvent("pointermove", x + 20, y));
    const preview = findMessage(messages, "ToolPreview")?.preview;
    // Cell edges, not collider extents: the neighbour's left face at 2 - 0.5 minus the dragged
    // half cell 0.5.
    expect(preview?.x).toBeCloseTo(1);
    expect(preview?.y).toBeCloseTo(0.5);
    detach();
  });

  it("snaps a wheel one cell under a block on its only weldable edge", () => {
    const wheelPart: PartDefinition = {
      partTypeId: 7, name: "wheel", mode: "dynamic", mass: 1,
      shapes: [
        { kind: "box", halfExtents: [0.2, 0.32, 0.5], offset: [0, 0.1702, 0] },
        { kind: "sphere", radius: 0.33, offset: [0.0106, -0.2057, 0] },
      ],
    };
    const blockPart: PartDefinition = {
      partTypeId: 1, name: "block", mode: "dynamic", mass: 1,
      shapes: [{ kind: "box", halfExtents: [0.5, 0.5, 0.5] }],
    };
    const wheel: DrawEntity = { ...part, entityId: 7, partTypeId: 7, x: 0, y: 0 };
    const block: DrawEntity = { ...part, entityId: 8, partTypeId: 1, x: 0, y: 1 };
    const [x, y] = worldToCss(0, 0);
    const { listeners, messages, detach } = attach([wheel, block], {
      tool: () => "move",
      isEditable: () => true,
      partOf: (partTypeId) => (partTypeId === 7 ? wheelPart : blockPart),
    });
    listeners.pointerdown(pointerEvent("pointerdown", x, y));
    listeners.pointermove(pointerEvent("pointermove", x + 5, y));
    const preview = findMessage(messages, "ToolPreview")?.preview;
    // The wheel's own cell lands flush under the block's cell (one cell below y = 1); its
    // collider offsets no longer move the snap.
    expect(preview?.y).toBeCloseTo(0);
    detach();
  });
});

describe("canvas gestures: marquee select", () => {
  const far: DrawEntity = { ...part, entityId: 9, x: 6, y: 2 };

  it("box-selects entities in ascending order and never pans", () => {
    const { listeners, messages, detach } = attach([far, part], { tool: () => "select" });
    const [x0, y0] = worldToCss(-1, -1);
    const [x1, y1] = worldToCss(8, 4);
    listeners.pointerdown(pointerEvent("pointerdown", x0, y0));
    listeners.pointermove(pointerEvent("pointermove", x1, y1));
    listeners.pointerup(pointerEvent("pointerup", x1, y1));

    expect(findMessage(messages, "SelectEntities")).toEqual({ kind: "SelectEntities", entityIds: [7, 9], mode: "replace" });
    const marquees = messages.filter((m) => m.kind === "Marquee");
    expect(marquees[marquees.length - 1]).toEqual({ kind: "Marquee", rect: null });
    expect(messages.some((m) => m.kind === "CameraChanged")).toBe(false);
    detach();
  });

  it("merges into the existing selection while Shift is held", () => {
    const { listeners, messages, detach } = attach([far, part], { tool: () => "select" });
    const [x0, y0] = worldToCss(-1, -1);
    const [x1, y1] = worldToCss(1, 1);
    listeners.pointerdown(pointerEvent("pointerdown", x0, y0));
    listeners.pointermove(pointerEvent("pointermove", x1, y1));
    listeners.pointerup(pointerEvent("pointerup", x1, y1, false, 1, 0, true));

    expect(findMessage(messages, "SelectEntities")).toEqual({ kind: "SelectEntities", entityIds: [7], mode: "add" });
    detach();
  });

  it("clears the selection on an empty select-tool tap", () => {
    const { listeners, messages, detach } = attach([part], { tool: () => "select" });
    listeners.pointerdown(pointerEvent("pointerdown", 10, 10));
    listeners.pointerup(pointerEvent("pointerup", 10, 10));

    expect(findMessage(messages, "SelectEntities")).toEqual({ kind: "SelectEntities", entityIds: [], mode: "replace" });
    detach();
  });

  it("pans the camera with the middle button without touching the selection", () => {
    const { listeners, messages, detach } = attach([part], { tool: () => "select" });
    listeners.pointerdown(pointerEvent("pointerdown", 100, 100, false, 1, 1));
    listeners.pointermove(pointerEvent("pointermove", 140, 100, false, 1, 1));
    listeners.pointerup(pointerEvent("pointerup", 140, 100, false, 1, 1));

    expect(messages.some((m) => m.kind === "CameraChanged")).toBe(true);
    expect(messages.some((m) => m.kind === "SelectEntities")).toBe(false);
    detach();
  });

  it("previews the marquee while dragging and clears it on release", () => {
    const { listeners, messages, detach } = attach([part], { tool: () => "select" });
    const [x0, y0] = worldToCss(-1, -1);
    const [x1, y1] = worldToCss(1, 1);
    listeners.pointerdown(pointerEvent("pointerdown", x0, y0));
    listeners.pointermove(pointerEvent("pointermove", x1, y1));

    expect(findMessage(messages, "Marquee")?.rect).toEqual({ minX: -1, minY: -1, maxX: 1, maxY: 1 });
    listeners.pointerup(pointerEvent("pointerup", x1, y1));
    expect(messages.filter((m) => m.kind === "Marquee")[1]).toEqual({ kind: "Marquee", rect: null });
    detach();
  });
});
