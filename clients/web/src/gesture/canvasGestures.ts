import { type Camera, screenToWorld } from "@/renderer/camera";
import {
  MIN_POINTER_DISTANCE,
  type Pose,
  type ToolId,
  type Vec2,
  contactBoxes,
  isTransformTool,
  movePose,
  entitiesInBox,
  pointerAngle,
  pointerDistance,
  rotatePose,
  scalePose,
  shortestAngleDelta,
  snapBoxOf,
} from "@/editor/tools";
import type { DrawEntity, GestureMessage, MarqueeRect, PartDefinition } from "@/schema/types";

export interface CanvasGestureOptions {
  /** Current build tool; pointer semantics follow it. Defaults to "place". */
  tool?: () => ToolId;
  /** Whether a tap on empty space may place the selected part (live + editing). */
  canPlace?: () => boolean;
  /** Whether this client may transform the entity (own + editing). */
  isEditable?: (entityId: number) => boolean;
  /** Content lookup used to snap a move drag flush against nearby parts. */
  partOf?: (partTypeId: number) => PartDefinition | undefined;
}

interface TransformDrag {
  tool: "move" | "rotate" | "scale";
  entityId: number;
  /** Pose when the drag started; every candidate is derived from it, never accumulated. */
  start: Pose;
  last: Pose;
  startWorld: Vec2;
  lastPointerAngle: number;
  startDistance: number;
  accumulatedAngle: number;
  /** Build-plane union-AABB half extents at drag start; 0 when the part has no snap shape. */
  halfX: number;
  halfY: number;
  /** Union-AABB centre relative to the entity origin at drag start; 0 without a snap shape. */
  offsetX: number;
  offsetY: number;
}

const DRAG_THRESHOLD_PX = 4;

/**
 * The only module that listens to canvas pointer events. It emits typed semantic messages:
 * camera pan/zoom always, plus placement or a transform request depending on the active
 * tool. A transform drag only previews locally — the command is emitted once on release.
 */
export function attachCanvasGestures(
  canvas: HTMLCanvasElement,
  camera: Camera,
  entities: { current: readonly DrawEntity[] },
  onMessage: (message: GestureMessage) => void,
  options?: CanvasGestureOptions,
): () => void {
  let dragging = false;
  let moved = false;
  let lastX = 0;
  let lastY = 0;
  let downX = 0;
  let downY = 0;
  let hitEntityThisDown: number | null = null;
  let drag: TransformDrag | null = null;
  let panning = false;
  let marquee: { startWorld: Vec2 } | null = null;

  const tool = (): ToolId => options?.tool?.() ?? "place";
  const toWorld = (x: number, y: number): Vec2 =>
    screenToWorld(camera, x, y, canvas.clientWidth, canvas.clientHeight);

  const panCamera = (dx: number, dy: number): void => {
    camera.x -= dx / camera.scale;
    camera.y += dy / camera.scale;
    onMessage({ kind: "CameraChanged", panX: camera.x, panY: camera.y, scale: camera.scale });
  };

  const onPointerDown = (event: PointerEvent): void => {
    dragging = true;
    moved = false;
    const point = pointerCss(canvas, event);
    lastX = point.x;
    lastY = point.y;
    downX = point.x;
    downY = point.y;
    canvas.setPointerCapture(event.pointerId);

    // Middle button pans in every tool; left-drag belongs to the active tool.
    if (event.button === 1) {
      event.preventDefault();
      panning = true;
      drag = null;
      marquee = null;
      hitEntityThisDown = null;
      return;
    }

    const world = toWorld(point.x, point.y);
    let hit: number | null = null;
    for (const entity of entities.current) {
      const dx = world.x - entity.x;
      const dy = world.y - entity.y;
      if (dx * dx + dy * dy <= 0.55 * 0.55 * entity.scale * entity.scale) {
        hit = entity.entityId;
      }
    }
    hitEntityThisDown = hit;

    const currentTool = tool();
    // The select tool drags a marquee from empty space; the box resolves on release so
    // Shift can merge with the selection that existed when the drag started.
    if (currentTool === "select" && hit === null) {
      marquee = { startWorld: world };
      drag = null;
      return;
    }

    onMessage({
      kind: "SelectEntities",
      entityIds: hit === null ? [] : [hit],
      mode: event.shiftKey ? "toggle" : "replace",
    });

    const target = hit === null ? undefined : entities.current.find((entity) => entity.entityId === hit);
    drag = null;
    if (hit !== null && target && isTransformTool(currentTool) && options?.isEditable?.(hit)) {
      const center = { x: target.x, y: target.y };
      const startPointerAngle = pointerAngle(center, world);
      const box = options?.partOf === undefined ? null : snapBoxOf(target, options.partOf(target.partTypeId));
      drag = {
        tool: currentTool,
        entityId: hit,
        start: { x: target.x, y: target.y, yaw: target.yaw, scale: target.scale },
        last: { x: target.x, y: target.y, yaw: target.yaw, scale: target.scale },
        startWorld: world,
        lastPointerAngle: startPointerAngle,
        startDistance: pointerDistance(center, world),
        accumulatedAngle: 0,
        halfX: box?.halfX ?? 0,
        halfY: box?.halfY ?? 0,
        offsetX: box?.offsetX ?? 0,
        offsetY: box?.offsetY ?? 0,
      };
    }
  };

  const onPointerMove = (event: PointerEvent): void => {
    if (!dragging) {
      return;
    }
    const point = pointerCss(canvas, event);
    const dx = point.x - lastX;
    const dy = point.y - lastY;
    lastX = point.x;
    lastY = point.y;
    if (Math.abs(point.x - downX) + Math.abs(point.y - downY) > DRAG_THRESHOLD_PX) {
      moved = true;
    }

    if (panning) {
      if (moved) {
        panCamera(dx, dy);
      }

      return;
    }

    if (marquee) {
      if (moved) {
        onMessage({ kind: "Marquee", rect: marqueeRect(marquee.startWorld, toWorld(point.x, point.y)) });
      }

      return;
    }

    if (drag && moved) {
      const world = toWorld(point.x, point.y);
      const snap = !event.altKey;
      const center = { x: drag.start.x, y: drag.start.y };
      if (drag.tool === "move") {
        const dx = world.x - drag.startWorld.x;
        const dy = world.y - drag.startWorld.y;
        const grid = event.altKey;
        const partOf = options?.partOf;
        const contacts =
          grid || partOf === undefined || drag.halfX <= 0 || drag.halfY <= 0
            ? undefined
            : {
                self: { entityId: drag.entityId, halfX: drag.halfX, halfY: drag.halfY, offsetX: drag.offsetX, offsetY: drag.offsetY },
                others: contactBoxes(entities.current, partOf),
              };
        drag.last = movePose(drag.start, dx, dy, grid, contacts);
      } else if (drag.tool === "rotate") {
        const angleNow = pointerAngle(center, world);
        drag.accumulatedAngle += shortestAngleDelta(drag.lastPointerAngle, angleNow);
        drag.lastPointerAngle = angleNow;
        drag.last = rotatePose(drag.start, drag.accumulatedAngle, snap);
      } else {
        const ratio =
          drag.startDistance < MIN_POINTER_DISTANCE ? 1 : pointerDistance(center, world) / drag.startDistance;
        drag.last = scalePose(drag.start, ratio, snap);
      }
      onMessage({ kind: "ToolPreview", preview: { entityId: drag.entityId, ...drag.last } });
      return;
    }
    if (!moved) {
      return;
    }
    panCamera(dx, dy);
  };

  const onPointerUp = (event: PointerEvent): void => {
    dragging = false;
    canvas.releasePointerCapture(event.pointerId);

    if (panning) {
      panning = false;
      hitEntityThisDown = null;
      return;
    }

    if (marquee) {
      const start = marquee;
      marquee = null;
      onMessage({ kind: "Marquee", rect: null });
      if (moved) {
        const point = pointerCss(canvas, event);
        const world = toWorld(point.x, point.y);
        const rect = marqueeRect(start.startWorld, world);
        onMessage({
          kind: "SelectEntities",
          entityIds: entitiesInBox(entities.current, rect.minX, rect.minY, rect.maxX, rect.maxY),
          mode: event.shiftKey ? "add" : "replace",
        });
      } else {
        onMessage({ kind: "SelectEntities", entityIds: [], mode: "replace" });
      }

      hitEntityThisDown = null;
      return;
    }

    if (drag) {
      const finished = drag;
      drag = null;
      if (moved) {
        emitTransformRequest(finished, onMessage);
      }
      onMessage({ kind: "ToolPreview", preview: null });
      hitEntityThisDown = null;
      return;
    }
    // A release over an existing part is a selection, never a placement; only an
    // empty-space tap with the place tool places a new part.
    if (!moved && tool() === "place" && options?.canPlace?.() && hitEntityThisDown === null) {
      const point = pointerCss(canvas, event);
      const world = toWorld(point.x, point.y);
      onMessage({ kind: "PlaceRequested", x: world.x, y: world.y });
    }
    hitEntityThisDown = null;
  };

  const onWheel = (event: WheelEvent): void => {
    event.preventDefault();
    const factor = event.deltaY < 0 ? 1.1 : 0.9;
    if (event.altKey && tool() === "place") {
      onMessage({ kind: "PartScaleChanged", scale: factor });
      return;
    }
    camera.scale = Math.min(160, Math.max(8, camera.scale * factor));
    onMessage({ kind: "CameraChanged", panX: camera.x, panY: camera.y, scale: camera.scale });
  };

  const onMouseDown = (event: MouseEvent): void => {
    // Middle-button drag pans; stop the browser's autoscroll gesture.
    if (event.button === 1) {
      event.preventDefault();
    }
  };
  canvas.addEventListener("pointerdown", onPointerDown);
  canvas.addEventListener("pointermove", onPointerMove);
  canvas.addEventListener("pointerup", onPointerUp);
  canvas.addEventListener("wheel", onWheel, { passive: false });
  canvas.addEventListener("mousedown", onMouseDown);
  return () => {
    canvas.removeEventListener("pointerdown", onPointerDown);
    canvas.removeEventListener("pointermove", onPointerMove);
    canvas.removeEventListener("pointerup", onPointerUp);
    canvas.removeEventListener("wheel", onWheel);
    canvas.removeEventListener("mousedown", onMouseDown);
  };
}

function emitTransformRequest(drag: TransformDrag, onMessage: (message: GestureMessage) => void): void {
  if (poseEquals(drag.start, drag.last)) {
    return;
  }
  if (drag.tool === "move") {
    onMessage({ kind: "MoveRequested", entityId: drag.entityId, x: drag.last.x, y: drag.last.y });
  } else if (drag.tool === "rotate") {
    onMessage({ kind: "RotateRequested", entityId: drag.entityId, angle: drag.last.yaw });
  } else {
    onMessage({ kind: "ScaleRequested", entityId: drag.entityId, scale: drag.last.scale });
  }
}

function poseEquals(left: Pose, right: Pose): boolean {
  return (
    Math.abs(left.x - right.x) < 1e-4 &&
    Math.abs(left.y - right.y) < 1e-4 &&
    Math.abs(left.yaw - right.yaw) < 1e-4 &&
    Math.abs(left.scale - right.scale) < 1e-4
  );
}

function marqueeRect(start: Vec2, end: Vec2): MarqueeRect {
  return {
    minX: Math.min(start.x, end.x),
    minY: Math.min(start.y, end.y),
    maxX: Math.max(start.x, end.x),
    maxY: Math.max(start.y, end.y),
  };
}

function pointerCss(canvas: HTMLCanvasElement, event: PointerEvent): { x: number; y: number } {
  const rect = canvas.getBoundingClientRect();
  return { x: event.clientX - rect.left, y: event.clientY - rect.top };
}
