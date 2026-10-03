import type { DrawEntity, PartDefinition } from "@/schema/types";

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

/**
 * Build cell size, mirroring `ConstructionRules.CellSize` on the server: a 1x1 part centred on
 * a whole number fills exactly one cell. Placement snaps to these centres, never to a cell
 * boundary, so a 1x1 part always fills one cell instead of straddling four.
 */
export const CELL_SIZE = 1;

/** Fine grid for `Alt` transform drags; half a cell, so it is never used to place parts. */
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

/** Snaps to the nearest cell centre. Half-cell offsets are deliberately unreachable. */
export function snapToCell(value: number): number {
  const snapped = Math.round(value / CELL_SIZE) * CELL_SIZE;
  // Normalise -0 so positions compare and serialise like their positive twins.
  return snapped === 0 ? 0 : snapped;
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

/** Contact-snap inputs for a move drag: the dragged part and its neighbours. */
export interface MoveSnapContext {
  self: SnapTarget;
  others: readonly SnapBox[];
}

/**
 * Move candidate from the drag start. Default (`grid` false) follows the pointer and
 * only snaps flush against a neighbour (see `snapMoveToParts`); `grid` true (`Alt`)
 * snaps both axes to the absolute 0.5 grid instead.
 */
export function movePose(start: Pose, dx: number, dy: number, grid: boolean, contacts?: MoveSnapContext): Pose {
  const rawX = start.x + dx;
  const rawY = start.y + dy;
  if (grid) {
    return { x: snapMove(rawX, true), y: snapMove(rawY, true), yaw: start.yaw, scale: start.scale };
  }

  const snapped =
    contacts === undefined ? { x: rawX, y: rawY } : snapMoveToParts(rawX, rawY, contacts.self, contacts.others);
  return { x: snapped.x, y: snapped.y, yaw: start.yaw, scale: start.scale };
}

export function rotatePose(start: Pose, accumulatedDelta: number, snap: boolean): Pose {
  return { x: start.x, y: start.y, yaw: snapAngle(start.yaw + accumulatedDelta, snap), scale: start.scale };
}

export function scalePose(start: Pose, ratio: number, snap: boolean): Pose {
  const factor = Number.isFinite(ratio) && ratio > 0 ? ratio : 1;
  return { x: start.x, y: start.y, yaw: start.yaw, scale: snapScale(start.scale * factor, snap) };
}

/** Entity ids whose centre lies inside the world-space box, ascending (marquee hit test). */
export function entitiesInBox(
  entities: readonly DrawEntity[],
  minX: number,
  minY: number,
  maxX: number,
  maxY: number,
): number[] {
  return entities
    .filter((entity) => entity.x >= minX && entity.x <= maxX && entity.y >= minY && entity.y <= maxY)
    .map((entity) => entity.entityId)
    .sort((left, right) => left - right);
}

/** Flush-contact snap distance for a move drag (world metres, below the 0.5 grid step). */
export const PART_SNAP = 0.35;

/**
 * The build-pose fields the snap math reads: any placed entity, or the candidate pose of a part
 * that is not placed yet (a click about to become a `PlacePart` command).
 */
export interface SnapEntity {
  entityId: number;
  x: number;
  y: number;
  yaw: number;
  scale: number;
}

/** Union AABB of a part's build-plane shape union, plus the id to exclude from contact tests. */
export interface SnapTarget {
  entityId: number;
  /** Union AABB half extents of the part's build-plane shape union, in world metres. */
  halfX: number;
  halfY: number;
  /** Union AABB centre relative to the entity origin (independent of the drag position). */
  offsetX: number;
  offsetY: number;
}

/** A placed part's build-plane AABB: a contact target with its centre. */
export interface SnapBox extends SnapTarget {
  x: number;
  y: number;
}

/**
 * Union build-plane AABB of an entity's collision shapes, or null when the part is
 * unknown or carries no box/sphere shape (nothing to snap against). Mirrors the server's
 * `PartFootprint`: every shape's part-local offset is rotated by `entity.yaw` and scaled,
 * boxes project to rotated-rect AABBs and spheres to squares of `radius * scale`.
 * Conditional shapes (joint attachment brackets) count: they are what a player lines a part
 * up against, even though the server's occupancy test ignores them.
 */
export function snapBoxOf(entity: SnapEntity, part: PartDefinition | undefined): SnapBox | null {
  if (part === undefined) {
    return null;
  }

  const cos = Math.cos(entity.yaw);
  const sin = Math.sin(entity.yaw);
  let minX = Number.POSITIVE_INFINITY;
  let minY = Number.POSITIVE_INFINITY;
  let maxX = Number.NEGATIVE_INFINITY;
  let maxY = Number.NEGATIVE_INFINITY;

  for (const shape of part.shapes) {
    const offset = shape.offset ?? [0, 0, 0];
    const centreX = (offset[0] * cos - offset[1] * sin) * entity.scale;
    const centreY = (offset[0] * sin + offset[1] * cos) * entity.scale;
    let extentX: number;
    let extentY: number;

    if (shape.kind === "sphere") {
      if (shape.radius === undefined) {
        continue;
      }

      extentX = shape.radius * entity.scale;
      extentY = extentX;
    } else {
      const half = shape.halfExtents;
      if (half === undefined) {
        continue;
      }

      const halfX = half[0] * entity.scale;
      const halfY = half[1] * entity.scale;
      extentX = Math.abs(halfX * cos) + Math.abs(halfY * sin);
      extentY = Math.abs(halfX * sin) + Math.abs(halfY * cos);
    }

    minX = Math.min(minX, centreX - extentX);
    maxX = Math.max(maxX, centreX + extentX);
    minY = Math.min(minY, centreY - extentY);
    maxY = Math.max(maxY, centreY + extentY);
  }

  if (minX > maxX) {
    return null;
  }

  return {
    entityId: entity.entityId,
    x: entity.x,
    y: entity.y,
    halfX: (maxX - minX) / 2,
    halfY: (maxY - minY) / 2,
    offsetX: (minX + maxX) / 2,
    offsetY: (minY + maxY) / 2,
  };
}

/**
 * Flush-contact candidate: zero gap between the two parts' union AABB boxes, against a
 * nearby part whose other axis already overlaps. Only contact makes the server connect
 * parts (`ConstructionRules` ConnectionProximity = 0.15 m), so this is the only snapping
 * a free move applies; candidates further than `threshold` from the pointer are ignored.
 */
export function snapMoveToParts(
  rawX: number,
  rawY: number,
  self: SnapTarget,
  others: readonly SnapBox[],
  threshold = PART_SNAP,
): Vec2 {
  let bestX = rawX;
  let bestXDelta = Number.POSITIVE_INFINITY;
  let bestY = rawY;
  let bestYDelta = Number.POSITIVE_INFINITY;

  for (const other of others) {
    if (other.entityId === self.entityId) {
      continue;
    }

    const selfCentreX = rawX + self.offsetX;
    const selfCentreY = rawY + self.offsetY;
    const otherCentreX = other.x + other.offsetX;
    const otherCentreY = other.y + other.offsetY;

    if (Math.abs(selfCentreY - otherCentreY) < other.halfY + self.halfY) {
      for (const candidate of [
        otherCentreX + other.halfX + self.halfX - self.offsetX,
        otherCentreX - other.halfX - self.halfX - self.offsetX,
      ]) {
        const delta = Math.abs(candidate - rawX);
        if (delta <= threshold && delta < bestXDelta) {
          bestX = candidate;
          bestXDelta = delta;
        }
      }
    }

    if (Math.abs(selfCentreX - otherCentreX) < other.halfX + self.halfX) {
      for (const candidate of [
        otherCentreY + other.halfY + self.halfY - self.offsetY,
        otherCentreY - other.halfY - self.halfY - self.offsetY,
      ]) {
        const delta = Math.abs(candidate - rawY);
        if (delta <= threshold && delta < bestYDelta) {
          bestY = candidate;
          bestYDelta = delta;
        }
      }
    }
  }

  return { x: bestX, y: bestY };
}

/** Build-plane boxes of every entity whose part carries a snappable shape. */
export function contactBoxes(
  entities: readonly DrawEntity[],
  partOf: (partTypeId: number) => PartDefinition | undefined,
): SnapBox[] {
  const boxes: SnapBox[] = [];
  for (const entity of entities) {
    const box = snapBoxOf(entity, partOf(entity.partTypeId));
    if (box !== null) {
      boxes.push(box);
    }
  }
  return boxes;
}

/**
 * Placement candidate for a build click: both axes go to the nearest cell centre and nothing
 * else. A tap has no drag to align against, and a cell grid already makes neighbours line up
 * exactly (a 1x1 part on a whole number fills one cell, so the next one is flush by
 * construction), so contact snapping is deliberately not applied here — it used to drag a
 * click onto half cells whenever terrain or a slanted part sat within `PART_SNAP`. Free-form
 * flush fitting stays available through the move tool.
 */
export function placePose(pose: Pose): Vec2 {
  return { x: snapToCell(pose.x), y: snapToCell(pose.y) };
}
