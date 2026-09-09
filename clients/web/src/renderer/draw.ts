import type { DrawEntity, PartContentDocument, PartDefinition } from "@/schema/types";
import { type Camera, worldToScreen } from "./camera";

const STATIC_FILL = "#5c6b52";
const DYNAMIC_FILL = "#c4a574";
const SELECT_STROKE = "#f0d090";
const PREVIEW_ALPHA = 0.45;

export function drawFrame(
  ctx: CanvasRenderingContext2D,
  camera: Camera,
  entities: readonly DrawEntity[],
  content: PartContentDocument | null,
  selectedId: number | null,
  goal?: { minX: number; minY: number; maxX: number; maxY: number },
  bounds?: { minX: number; minY: number; maxX: number; maxY: number },
): void {
  const width = ctx.canvas.clientWidth || ctx.canvas.width;
  const height = ctx.canvas.clientHeight || ctx.canvas.height;
  ctx.fillStyle = "#1c211c";
  ctx.fillRect(0, 0, width, height);
  drawGrid(ctx, camera, width, height);
  if (goal) {
    const a = worldToScreen(camera, goal.minX, goal.maxY, width, height);
    const b = worldToScreen(camera, goal.maxX, goal.minY, width, height);
    ctx.fillStyle = "rgba(80, 160, 90, 0.25)";
    ctx.fillRect(a.x, a.y, b.x - a.x, b.y - a.y);
  }
  if (bounds) {
    const a = worldToScreen(camera, bounds.minX, bounds.maxY, width, height);
    const b = worldToScreen(camera, bounds.maxX, bounds.minY, width, height);
    ctx.strokeStyle = "rgba(220, 100, 90, 0.85)";
    ctx.lineWidth = 2;
    ctx.setLineDash([8, 6]);
    ctx.strokeRect(a.x, a.y, b.x - a.x, b.y - a.y);
    ctx.setLineDash([]);
  }
  const parts = new Map<number, PartDefinition>();
  if (content) {
    for (const part of content.parts) {
      parts.set(part.partTypeId, part);
    }
  }

  for (const entity of entities) {
    const part = parts.get(entity.partTypeId);
    const origin = worldToScreen(camera, entity.x, entity.y, width, height);
    // The sandbox broadcasts previews with physicsBodyId 0: they are not physical
    // bodies yet, so they render translucent and without a halo or name label.
    const preview = entity.bodyId === 0;
    // Light parts cast a radial glow; the radius scales like the world (spatial
    // light range), so zooming in amplifies the halo like the original game.
    const lightRadius = part?.capabilities?.light;
    if (!preview && lightRadius !== undefined && lightRadius > 0) {
      const halo = lightRadius * entity.scale * camera.scale;
      ctx.save();
      const glow = ctx.createRadialGradient(origin.x, origin.y, 0, origin.x, origin.y, halo);
      glow.addColorStop(0, "rgba(255, 240, 180, 0.55)");
      glow.addColorStop(1, "rgba(255, 240, 180, 0)");
      ctx.fillStyle = glow;
      ctx.beginPath();
      ctx.arc(origin.x, origin.y, halo, 0, Math.PI * 2);
      ctx.fill();
      ctx.restore();
    }
    ctx.save();
    if (preview) {
      ctx.globalAlpha = PREVIEW_ALPHA;
    }
    ctx.translate(origin.x, origin.y);
    ctx.rotate(-entity.yaw);
    const shape = part?.shapes[0];
    if (shape?.kind === "sphere" && shape.radius) {
      const radius = shape.radius * entity.scale * camera.scale;
      ctx.beginPath();
      ctx.arc(0, 0, radius, 0, Math.PI * 2);
      ctx.fillStyle = part?.mode === "static" ? STATIC_FILL : DYNAMIC_FILL;
      ctx.fill();
      ctx.strokeStyle = part?.mode === "static" ? "#8a9a7a" : "#d8b880";
      ctx.lineWidth = 1;
      ctx.stroke();
    } else {
      const hx = (shape?.halfExtents?.[0] ?? 0.5) * entity.scale * camera.scale;
      const hy = (shape?.halfExtents?.[1] ?? 0.5) * entity.scale * camera.scale;
      ctx.fillStyle = part?.mode === "static" ? STATIC_FILL : DYNAMIC_FILL;
      ctx.fillRect(-hx, -hy, hx * 2, hy * 2);
      ctx.strokeStyle = part?.mode === "static" ? "#8a9a7a" : "#d8b880";
      ctx.lineWidth = 1;
      ctx.strokeRect(-hx, -hy, hx * 2, hy * 2);
    }
    // Placeholder texture: the type name is stamped at the part's collision centre.
    if (!preview && part && part.name) {
      ctx.fillStyle = "rgba(255, 255, 255, 0.85)";
      ctx.font = "10px system-ui, sans-serif";
      ctx.textAlign = "center";
      ctx.textBaseline = "middle";
      ctx.fillText(part.name, 0, 0);
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
