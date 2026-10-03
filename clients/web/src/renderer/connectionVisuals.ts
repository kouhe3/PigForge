/**
 * Conditionals for the sprites the original shows per connection side (manifest v4).
 *
 * The original asks `Contraption.CanConnectTo(part, direction)` about the grid neighbour and
 * each `ChangeVisualConnections` override turns the answer into its `*Attachment` or
 * `*FrameSprite` nodes (Rocket.cs:139-161, TNT.cs:86-108, SpotLight.cs:70-105,
 * GrapplingHook.cs:218-255, Wings.cs:41-72). Every direction it asks about is rotated into the
 * part's own frame first (`Rotate(Direction.Up, m_gridRotation)`), so the labels here are
 * part-local and the renderer rotates them into the grid with the entity's yaw.
 *
 * The predicate is the original's weld rule (`Contraption.cs:690`, mirrored by
 * `CompoundAssembler.CanMergePair`): both ends non-`none` and at least one `source`. The
 * declared direction (`m_jointConnectionDirection`) and the frame-enclosure short-circuit are
 * not modelled yet — every conditional part in the catalog declares `Any` (ADR-014).
 */

import type { DrawEntity, PartDefinition } from "@/schema/types";
import type { ConnectionVisual, LocalSide, SpriteCondition } from "./atlas";

/** The grid cell the layout places parts on; the original's neighbours are one cell away. */
const CELL_SIZE = 1;
/** How far a neighbour may sit from a side's exact cell centre, in cells. */
const NEIGHBOUR_TOLERANCE = 0.35;
/** A turn of 45°, the step the eight-way parts rotate on. */
const EIGHTH_TURN = Math.PI / 4;

/** Direction of each local side, +y up (the same frame as the manifest's `cx`/`cy`). */
const SIDE_VECTORS: Record<LocalSide, readonly [number, number]> = {
  top: [0, 1],
  bottom: [0, -1],
  left: [-1, 0],
  right: [1, 0],
  topLeft: [-1, 1],
  topRight: [1, 1],
  bottomLeft: [-1, -1],
  bottomRight: [1, -1],
};

/** Every local side, in the original's `Direction` order (Right, Up, Left, Down) first. */
const SIDES: readonly LocalSide[] = ["right", "top", "left", "bottom", "topRight", "topLeft", "bottomLeft", "bottomRight"];

const DIAGONAL_SIDES: Record<LocalSide, boolean> = {
  top: false,
  bottom: false,
  left: false,
  right: false,
  topLeft: true,
  topRight: true,
  bottomLeft: true,
  bottomRight: true,
};

/** The original's weld rule: neither end `none`, at least one `source` (Contraption.cs:690). */
function canWeld(a: PartDefinition, b: PartDefinition): boolean {
  const typeA = a.capabilities?.jointConnectionType;
  const typeB = b.capabilities?.jointConnectionType;
  if (typeA === undefined || typeB === undefined || typeA === "none" || typeB === "none") return false;
  return typeA === "source" || typeB === "source";
}

/** True when the part sits on a 45°/135°/225°/315° turn (the original's `flag9`). */
function onDiagonalRotation(yaw: number): boolean {
  const eighth = Math.round(yaw / EIGHTH_TURN);
  return (((eighth % 8) + 8) % 8) % 2 === 1;
}

/**
 * Which local sides of each entity have a neighbour it would weld to. Only entities `wanted`
 * accepts are resolved: the caller asks for the parts whose manifest carries a connection rule,
 * so a scene of plain blocks costs nothing.
 */
export function connectableSides(
  entities: readonly DrawEntity[],
  partOf: (partTypeId: number) => PartDefinition | undefined,
  wanted: (entity: DrawEntity) => boolean,
): Map<number, Set<LocalSide>> {
  const result = new Map<number, Set<LocalSide>>();
  for (const entity of entities) {
    if (!wanted(entity)) continue;
    const part = partOf(entity.partTypeId);
    if (!part) continue;
    const cos = Math.cos(entity.yaw);
    const sin = Math.sin(entity.yaw);
    const sides = new Set<LocalSide>();
    for (const side of SIDES) {
      const [dx, dy] = SIDE_VECTORS[side];
      const targetX = entity.x + (dx * cos - dy * sin) * CELL_SIZE;
      const targetY = entity.y + (dx * sin + dy * cos) * CELL_SIZE;
      const neighbour = entities.find(
        (other) =>
          other.entityId !== entity.entityId &&
          Math.abs(other.x - targetX) <= NEIGHBOUR_TOLERANCE &&
          Math.abs(other.y - targetY) <= NEIGHBOUR_TOLERANCE,
      );
      if (neighbour === undefined) continue;
      const other = partOf(neighbour.partTypeId);
      if (other !== undefined && canWeld(part, other)) sides.add(side);
    }
    if (sides.size > 0) result.set(entity.entityId, sides);
  }
  return result;
}

/**
 * Whether one conditional sprite is drawn. `visual` is the part's manifest rule; a part without
 * one draws no conditional sprite, which is how a conditional host the extractor has no rule for
 * stays hidden. `sides` are the entity's connectable local sides (`connectableSides`), and `yaw`
 * is the entity's own turn, which the eight-way rule needs to pick its diagonal set.
 */
export function conditionalSpriteVisible(
  condition: SpriteCondition,
  visual: ConnectionVisual | undefined,
  sides: ReadonlySet<LocalSide> | undefined,
  yaw: number,
): boolean {
  if (visual === undefined) return false;
  const has = (side: LocalSide) => sides?.has(side) === true;
  if (condition.kind === "frame") {
    if (visual !== "frame") return false;
    // Wings.cs:50-55 / JetEngine.cs:207-211: the two mount sets are part-local
    // Right/Up/Left (top) and Left/Down/Right (bottom); the bottom is also the ghost.
    const top = has("right") || has("top") || has("left");
    const bottom = has("left") || has("bottom") || has("right");
    return condition.mount === "top" ? top : bottom || !top;
  }
  const side = condition.side;
  switch (visual) {
    case "attachmentFallback":
      // Rocket.cs:153-156: the bottom marker is also the "no other side can connect" ghost.
      if (side === "bottom") return has("bottom") || !(has("top") || has("left") || has("right"));
      return has(side);
    case "attachmentPlain":
      // TNT.cs:94-106: each marker follows its own side, with no fallback.
      return has(side);
    case "attachmentEight": {
      // SpotLight.cs:92-100 / GrapplingHook.cs:241-249: the diagonals show only while the part
      // sits on a 45° turn, the orthogonal ones otherwise. The decompiled fallback line checks
      // `flag6` twice and skips `flag2`; checked against all eight here (ADR-014).
      const diagonal = DIAGONAL_SIDES[side];
      const eighthTurn = onDiagonalRotation(yaw);
      if (diagonal) return has(side) && eighthTurn;
      if (side === "bottom") return (has("bottom") && !eighthTurn) || !SIDES.some(has);
      return has(side) && !eighthTurn;
    }
    case "frame":
      return false;
  }
}
