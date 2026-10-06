import type { WorldRect } from "@/schema/levelContent";

export interface Camera {
  x: number;
  y: number;
  scale: number;
}

export function createCamera(): Camera {
  return { x: 4, y: 2, scale: 36 };
}

/**
 * A camera framing a world rectangle: centred on its middle with the whole extent visible at
 * `width` x `height` CSS pixels, `padding` of each edge kept clear. The result is a fresh camera --
 * `viewState.camera` is the module singleton other modules hold by reference, so a caller copies
 * the fields onto it rather than replacing the object. A degenerate rectangle keeps the default
 * zoom.
 */
export function fitBounds(bounds: WorldRect, width: number, height: number, padding = 0.06): Camera {
  const usableWidth = width * (1 - 2 * padding);
  const usableHeight = height * (1 - 2 * padding);
  const spanX = bounds.maxX - bounds.minX;
  const spanY = bounds.maxY - bounds.minY;
  let scale = Number.POSITIVE_INFINITY;
  if (spanX > 0) {
    scale = Math.min(scale, usableWidth / spanX);
  }
  if (spanY > 0) {
    scale = Math.min(scale, usableHeight / spanY);
  }
  if (!Number.isFinite(scale) || scale <= 0) {
    scale = createCamera().scale;
  }
  return { x: (bounds.minX + bounds.maxX) / 2, y: (bounds.minY + bounds.maxY) / 2, scale };
}

export function worldToScreen(
  camera: Camera,
  worldX: number,
  worldY: number,
  canvasWidth: number,
  canvasHeight: number,
): { x: number; y: number } {
  return {
    x: (worldX - camera.x) * camera.scale + canvasWidth * 0.5,
    y: canvasHeight * 0.5 - (worldY - camera.y) * camera.scale,
  };
}

export function screenToWorld(
  camera: Camera,
  screenX: number,
  screenY: number,
  canvasWidth: number,
  canvasHeight: number,
): { x: number; y: number } {
  return {
    x: camera.x + (screenX - canvasWidth * 0.5) / camera.scale,
    y: camera.y - (screenY - canvasHeight * 0.5) / camera.scale,
  };
}

export function yawFromQuaternion(rotation: [number, number, number, number]): number {
  const [x, y, z, w] = rotation;
  return Math.atan2(2 * (w * z + x * y), 1 - 2 * (y * y + z * z));
}
