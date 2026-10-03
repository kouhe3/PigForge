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
import { snapBoxOf } from "@/editor/tools";
import type { ConnectionVisual, LocalSide, SpriteCondition } from "./atlas";

/** How far two alignment boxes may sit apart and still count as touching, in cells. */
const CONTACT_TOLERANCE = 0.06;
/** A turn of 45°, the step the eight-way parts rotate on. */
const EIGHTH_TURN = Math.PI / 4;

/** Every local side, in the original's `Direction` order (Right, Up, Left, Down) first. */
const SIDES: readonly LocalSide[] = ["right", "top", "left", "bottom", "topRight", "topLeft", "bottomLeft", "bottomRight"];

/** The four orthogonal sides, in the order the renderer's yaw rotation walks them. */
const LOCAL_ORDER: readonly LocalSide[] = ["top", "left", "bottom", "right"];

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
 *
 * Two parts are neighbours when their alignment boxes touch — the flush contact a build-mode
 * snap makes and the server welds on (`ConstructionRules.ConnectionProximity`) — not when their
 * origins sit a cell apart: a glider wing's box is its bracket, so its origin lands 0.74 of a
 * cell from the frame it welds to (ADR-018). The side is the axis whose boxes are flush.
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
    if (part === undefined) continue;
    const self = snapBoxOf(entity, part);
    if (self === null) continue;
    const sides = new Set<LocalSide>();
    for (const other of entities) {
      if (other.entityId === entity.entityId) continue;
      const otherPart = partOf(other.partTypeId);
      if (otherPart === undefined || !canWeld(part, otherPart)) continue;
      const box = snapBoxOf(other, otherPart);
      if (box === null) continue;
      const gapX = Math.abs(other.x + box.offsetX - entity.x - self.offsetX) - (self.halfX + box.halfX);
      const gapY = Math.abs(other.y + box.offsetY - entity.y - self.offsetY) - (self.halfY + box.halfY);
      if (gapX > CONTACT_TOLERANCE || gapY > CONTACT_TOLERANCE) continue;
      const world: LocalSide =
        gapX >= gapY ? (other.x >= entity.x ? "right" : "left") : (other.y >= entity.y ? "top" : "bottom");
      sides.add(localSide(world, entity.yaw));
    }
    if (sides.size > 0) result.set(entity.entityId, sides);
  }
  return result;
}

/** A world side back in the entity's own frame: the inverse of the renderer's yaw rotation. */
function localSide(world: LocalSide, yaw: number): LocalSide {
  const quarter = ((Math.round(yaw / (Math.PI / 2)) % 4) + 4) % 4;
  const index = LOCAL_ORDER.indexOf(world);
  return LOCAL_ORDER[(((index - quarter) % 4) + 4) % 4];
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
