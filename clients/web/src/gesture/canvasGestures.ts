import { type Camera, screenToWorld } from "@/renderer/camera";
import type { DrawEntity, GestureMessage } from "@/schema/types";

export function attachCanvasGestures(
  canvas: HTMLCanvasElement,
  camera: Camera,
  entities: { current: readonly DrawEntity[] },
  onMessage: (message: GestureMessage) => void,
): () => void {
  let dragging = false;
  let lastX = 0;
  let lastY = 0;

  const onPointerDown = (event: PointerEvent): void => {
    dragging = true;
    lastX = event.offsetX;
    lastY = event.offsetY;
    canvas.setPointerCapture(event.pointerId);
    const world = screenToWorld(camera, event.offsetX, event.offsetY, canvas.clientWidth, canvas.clientHeight);
    let hit: number | null = null;
    for (const entity of entities.current) {
      const dx = world.x - entity.x;
      const dy = world.y - entity.y;
      if (dx * dx + dy * dy <= 0.55 * 0.55 * entity.scale * entity.scale) {
        hit = entity.entityId;
      }
    }
    onMessage({ kind: "SelectEntity", entityId: hit });
  };

  const onPointerMove = (event: PointerEvent): void => {
    if (!dragging) {
      return;
    }
    const dx = event.offsetX - lastX;
    const dy = event.offsetY - lastY;
    lastX = event.offsetX;
    lastY = event.offsetY;
    camera.x -= dx / camera.scale;
    camera.y += dy / camera.scale;
    onMessage({ kind: "CameraChanged", panX: camera.x, panY: camera.y, scale: camera.scale });
  };

  const onPointerUp = (event: PointerEvent): void => {
    dragging = false;
    canvas.releasePointerCapture(event.pointerId);
  };

  const onWheel = (event: WheelEvent): void => {
    event.preventDefault();
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
