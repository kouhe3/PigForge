export interface Camera {
  x: number;
  y: number;
  scale: number;
}

export function createCamera(): Camera {
  return { x: 4, y: 2, scale: 36 };
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
