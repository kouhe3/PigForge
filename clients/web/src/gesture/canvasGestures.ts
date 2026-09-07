import { type Camera, screenToWorld } from "@/renderer/camera";
import type { DrawEntity, GestureMessage } from "@/schema/types";

export function attachCanvasGestures(
  canvas: HTMLCanvasElement,
  camera: Camera,
  entities: { current: readonly DrawEntity[] },
  onMessage: (message: GestureMessage) => void,
  options?: { building?: () => boolean },
): () => void {
  let dragging = false;
  let moved = false;
  let lastX = 0;
  let lastY = 0;
  let downX = 0;
  let downY = 0;
  let hitEntityThisDown: number | null = null;

  const onPointerDown = (event: PointerEvent): void => {
    dragging = true;
    moved = false;
    const point = pointerCss(canvas, event);
    lastX = point.x;
    lastY = point.y;
    downX = point.x;
    downY = point.y;
    canvas.setPointerCapture(event.pointerId);
    const world = screenToWorld(camera, point.x, point.y, canvas.clientWidth, canvas.clientHeight);
    let hit: number | null = null;
    for (const entity of entities.current) {
      const dx = world.x - entity.x;
      const dy = world.y - entity.y;
      if (dx * dx + dy * dy <= 0.55 * 0.55 * entity.scale * entity.scale) {
        hit = entity.entityId;
      }
    }
    hitEntityThisDown = hit;
    onMessage({ kind: "SelectEntity", entityId: hit });
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
    if (Math.abs(point.x - downX) + Math.abs(point.y - downY) > 4) {
      moved = true;
    }
    if (!moved) {
      return;
    }
    camera.x -= dx / camera.scale;
    camera.y += dy / camera.scale;
    onMessage({ kind: "CameraChanged", panX: camera.x, panY: camera.y, scale: camera.scale });
  };

  const onPointerUp = (event: PointerEvent): void => {
    dragging = false;
    canvas.releasePointerCapture(event.pointerId);
    // A release over an existing part is a selection, never a placement; only an
    // empty-space tap places a new part.
    if (!moved && options?.building?.() && hitEntityThisDown === null) {
      const point = pointerCss(canvas, event);
      const world = screenToWorld(camera, point.x, point.y, canvas.clientWidth, canvas.clientHeight);
      onMessage({ kind: "PlaceRequested", x: world.x, y: world.y });
    }
    hitEntityThisDown = null;
  };

  const onWheel = (event: WheelEvent): void => {
    event.preventDefault();
    if (event.altKey) {
      const factor = event.deltaY < 0 ? 1.1 : 0.9;
      onMessage({ kind: "PartScaleChanged", scale: factor });
      return;
    }
    const factor = event.deltaY < 0 ? 1.1 : 0.9;
    camera.scale = Math.min(160, Math.max(8, camera.scale * factor));
    onMessage({ kind: "CameraChanged", panX: camera.x, panY: camera.y, scale: camera.scale });
  };

  canvas.addEventListener("pointerdown", onPointerDown);
  canvas.addEventListener("pointermove", onPointerMove);
  canvas.addEventListener("pointerup", onPointerUp);
  canvas.addEventListener("wheel", onWheel, { passive: false });
  return () => {
    canvas.removeEventListener("pointerdown", onPointerDown);
    canvas.removeEventListener("pointermove", onPointerMove);
    canvas.removeEventListener("pointerup", onPointerUp);
    canvas.removeEventListener("wheel", onWheel);
  };
}

function pointerCss(canvas: HTMLCanvasElement, event: PointerEvent): { x: number; y: number } {
  const rect = canvas.getBoundingClientRect();
  return { x: event.clientX - rect.left, y: event.clientY - rect.top };
}
