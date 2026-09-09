/**
 * Build-mode editing tools (advanced building): the tool set, its snap steps, and the
 * pure pose math the gesture bridge uses. No DOM, no Vue — see docs/specs/advanced-building.md.
 */

export type ToolId = "place" | "select" | "move" | "rotate" | "scale";

export interface ToolDefinition {
  id: ToolId;
  label: string;
  hotkey: string;
}

export const TOOLS: readonly ToolDefinition[] = [
  { id: "place", label: "放置", hotkey: "1" },
  { id: "select", label: "选择", hotkey: "2" },
  { id: "move", label: "移动", hotkey: "3" },
  { id: "rotate", label: "旋转", hotkey: "4" },
  { id: "scale", label: "缩放", hotkey: "5" },
];

export const MOVE_SNAP = 0.5;
export const ROTATE_SNAP = Math.PI / 12;
export const SCALE_SNAP = 0.25;
export const MIN_SCALE = 0.25;
export const MAX_SCALE = 4;

/** Below this pointer-to-centre distance the rotate/scale ratio is treated as unchanged. */
export const MIN_POINTER_DISTANCE = 0.05;

export interface Vec2 {
  x: number;
  y: number;
}

/** A build-mode planar pose: metres, radians, uniform scale. */
export interface Pose {
  x: number;
  y: number;
  yaw: number;
  scale: number;
}

/** Narrowing guard: only these tools transform the selected part. */
export function isTransformTool(tool: ToolId): tool is "move" | "rotate" | "scale" {
  return tool === "move" || tool === "rotate" || tool === "scale";
}

export function toolByHotkey(key: string): ToolId | null {
  return TOOLS.find((tool) => tool.hotkey === key)?.id ?? null;
}

/** Absolute grid snap; disabled (`Alt`) keeps the raw value. */
export function snapMove(value: number, snap: boolean): number {
  return snap ? Math.round(value / MOVE_SNAP) * MOVE_SNAP : value;
}

export function snapAngle(angle: number, snap: boolean): number {
  return snap ? Math.round(angle / ROTATE_SNAP) * ROTATE_SNAP : angle;
}

/** Snapped scale is always clamped to the protocol range, even with snapping disabled. */
export function snapScale(scale: number, snap: boolean): number {
  const value = snap ? Math.round(scale / SCALE_SNAP) * SCALE_SNAP : scale;
  return Math.min(MAX_SCALE, Math.max(MIN_SCALE, value));
}

export function pointerAngle(center: Vec2, pointer: Vec2): number {
  return Math.atan2(pointer.y - center.y, pointer.x - center.x);
}

export function pointerDistance(center: Vec2, pointer: Vec2): number {
  return Math.hypot(pointer.x - center.x, pointer.y - center.y);
}

/** Signed delta wrapped to (-π, π] so a drag across the ±π seam does not jump a full turn. */
export function shortestAngleDelta(from: number, to: number): number {
  let delta = to - from;
  while (delta > Math.PI) {
    delta -= 2 * Math.PI;
  }
  while (delta <= -Math.PI) {
    delta += 2 * Math.PI;
  }
  return delta;
}

export function movePose(start: Pose, dx: number, dy: number, snap: boolean): Pose {
  return { x: snapMove(start.x + dx, snap), y: snapMove(start.y + dy, snap), yaw: start.yaw, scale: start.scale };
}

export function rotatePose(start: Pose, accumulatedDelta: number, snap: boolean): Pose {
  return { x: start.x, y: start.y, yaw: snapAngle(start.yaw + accumulatedDelta, snap), scale: start.scale };
}

export function scalePose(start: Pose, ratio: number, snap: boolean): Pose {
  const factor = Number.isFinite(ratio) && ratio > 0 ? ratio : 1;
  return { x: start.x, y: start.y, yaw: start.yaw, scale: snapScale(start.scale * factor, snap) };
}
