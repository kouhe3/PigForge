import type { DrawEntity, PartContentDocument, PartDefinition } from "@/schema/types";
import { type Camera, worldToScreen } from "./camera";

const STATIC_FILL = "#5c6b52";
const DYNAMIC_FILL = "#c4a574";
const SELECT_STROKE = "#f0d090";

export function drawFrame(
  ctx: CanvasRenderingContext2D,
  camera: Camera,
  entities: readonly DrawEntity[],
  content: PartContentDocument | null,
  selectedId: number | null,
): void {
  const width = ctx.canvas.width;
  const height = ctx.canvas.height;
  ctx.fillStyle = "#1c211c";
  ctx.fillRect(0, 0, width, height);
  drawGrid(ctx, camera, width, height);

  const parts = new Map<number, PartDefinition>();
  if (content) {
    for (const part of content.parts) {
      parts.set(part.partTypeId, part);
    }
  }

  for (const entity of entities) {
    const part = parts.get(entity.partTypeId);
    const origin = worldToScreen(camera, entity.x, entity.y, width, height);
    ctx.save();
    ctx.translate(origin.x, origin.y);
    ctx.rotate(-entity.yaw);
    const shape = part?.shapes[0];
    if (shape?.kind === "sphere" && shape.radius) {
      const radius = shape.radius * entity.scale * camera.scale;
      ctx.beginPath();
      ctx.arc(0, 0, radius, 0, Math.PI * 2);
      ctx.fillStyle = part?.mode === "static" ? STATIC_FILL : DYNAMIC_FILL;
      ctx.fill();
    } else {
      const hx = (shape?.halfExtents?.[0] ?? 0.5) * entity.scale * camera.scale;
      const hy = (shape?.halfExtents?.[1] ?? 0.5) * entity.scale * camera.scale;
      ctx.fillStyle = part?.mode === "static" ? STATIC_FILL : DYNAMIC_FILL;
      ctx.fillRect(-hx, -hy, hx * 2, hy * 2);
    }
    if (selectedId === entity.entityId) {
      ctx.strokeStyle = SELECT_STROKE;
      ctx.lineWidth = 2;
      ctx.strokeRect(-4, -4, 8, 8);
    }
    ctx.restore();
  }
}

function drawGrid(ctx: CanvasRenderingContext2D, camera: Camera, width: number, height: number): void {
  ctx.strokeStyle = "#2a332a";
  ctx.lineWidth = 1;
  const step = camera.scale;
  const origin = worldToScreen(camera, 0, 0, width, height);
  ctx.beginPath();
  for (let x = origin.x % step; x < width; x += step) {
    ctx.moveTo(x, 0);
    ctx.lineTo(x, height);
  }
  for (let y = origin.y % step; y < height; y += step) {
    ctx.moveTo(0, y);
    ctx.lineTo(width, y);
  }
  ctx.stroke();
}
