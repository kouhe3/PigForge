import { PALETTE } from "@/builder/slope";
import type { DrawEntity, PartContentDocument, PartDefinition } from "@/schema/types";

/** BPLE-style hotkey order: 1-9, 0, then A-Z. */
const HOTKEYS = "1234567890ABCDEFGHIJKLMNOPQRSTUVWXYZ";

/**
 * The direction buckets, in the original's `BasePart.Direction` order (up-right first is ours, the
 * original counts Right = 0): the bar's group key is `(part type, effective direction)`, so two
 * fans aimed opposite ways get a button each, exactly as `UIPartButtonList` makes one button per
 * distinct `UIPartButtonInfo` — `(buttonType, buttonIndex, partType, partIndex = EffectDirection,
 * componentIndex)` — and hangs every part of that group on it (`UIPartButtonList.cs:539-575`,
 * entry `LevelManager.cs:886` → `Contraption.ActivatePartType(type, direction)`,
 * `Contraption.cs:955-1021`).
 *
 * The labels are the original's own 8-way arrows.
 */
const DIRECTION_ARROWS = ["▶", "◥", "▲", "◤", "◀", "◣", "▼", "◢"] as const;

/**
 * A part's local effect direction — the original's per-class `EffectDirection()`:
 *
 * | class | local direction | source |
 * |---|---|---|
 * | `FanPropeller` (fan/propeller/rotor) | `m_forceDirection` | `FanPropeller.cs:59-62` |
 * | `Rocket` (rocket/red rocket/bottles) | `m_direction` is its thrust; the *effect* direction is `Rotate(Right, gridRotation)` | `Rocket.cs:97-100` |
 * | `Bellows` | `Rotate(Right, gridRotation)` | `Bellows.cs:65-68` |
 * | `GrapplingHook` | `RotateWithEightDirections(Right, gridRotation)` | `GrapplingHook.cs:156-159` |
 * | `SpringBoxingGlove` | `Rotate(Down, gridRotation)` | `SpringBoxingGlove.cs:86-89` |
 * | `TNT` (and every yellow/blaster charge) | `Rotate(Right, gridRotation)` (the `RotatableTNT` gate is profile B) | `TNT.cs:63-70` |
 * | `Umbrella` / `SpotLight` | `Rotate(Up, gridRotation)` | `Umbrella.cs:74-77`, `SpotLight.cs:107-110` |
 * | `Kicker` (our detacher) | `Rotate(Right, gridRotation)` | `Kicker.cs:26-29` |
 *
 * The class's own vector is `Rotate(localDirection, gridRotation)`, i.e. the *build yaw* turns it;
 * that is why the group key needs the entity, not just the part definition. A part whose class has
 * no direction (a plain switch) reads as Right, the base `BasePart.EffectDirection()`
 * (`BasePart.cs:611-614`).
 */
function localEffectDirection(part: PartDefinition | undefined): [number, number] {
  const capabilities = part?.capabilities;
  if (capabilities?.fan !== undefined) {
    return [capabilities.fan.directionX, capabilities.fan.directionY ?? 0];
  }

  if (capabilities?.rocket !== undefined || capabilities?.bellows !== undefined) {
    return [capabilities.rocket?.directionX ?? capabilities.bellows?.directionX ?? 1, 0];
  }

  if (capabilities?.grapple !== undefined) {
    return [1, 0];
  }

  if (capabilities?.glove !== undefined) {
    return [0, -1];
  }

  if (capabilities?.umbrella !== undefined || capabilities?.light !== undefined) {
    return [0, 1];
  }

  return [1, 0];
}

/**
 * The direction bucket a placed part's effect lands in: the class's local direction turned by the
 * part's build yaw, quantised to 45 degrees (the original's `Direction` enum has four cardinals and
 * four diagonals, and `RotateWithEightDirections` is what the eight-way classes use). An arbitrary
 * yaw — ours, where the original's grid rotation is a multiple of 90 — falls in the nearest bucket,
 * the same rule the build grid's own occupancy uses (`PartFootprint.QuarterTurns`).
 */
function directionBucket(part: PartDefinition | undefined, yaw: number): number {
  const [x, y] = localEffectDirection(part);
  const angle = Math.atan2(y, x) + yaw;
  return ((Math.round(angle / (Math.PI / 4)) % 8) + 8) % 8;
}

export interface GadgetGroup {
  partTypeId: number;
  kind: "toggle" | "trigger";
  label: string;
  count: number;
  active: boolean;
  /** Empty when the group has no hotkey (more than 36 groups). */
  hotkey: string;
  /** The effect-direction bucket (0 = right, rising counter-clockwise in 45 degree steps). */
  direction: number;
  /**
   * The entities this button drives, ascending. The original sends one
   * `ActivatePartType(type, direction)` for the whole group; PigForge sends one
   * `SetPartActive` per entity, so the server's own per-part validation and ownership
   * checks stay the only authority (PGFC kind 8, see docs/specs/play-part-switches.md).
   */
  entityIds: number[];
}

export function gadgetHotkey(index: number): string | null {
  return index >= 0 && index < HOTKEYS.length ? HOTKEYS[index] : null;
}

/**
 * The switch bar: one group per switchable part type **and effect direction** this client owns and
 * has materialised, ascending by (partTypeId, direction). Parts whose content declares no
 * activation are dropped, so the bar follows what the player actually built; a part built at an
 * angle the original could not express still lands in the nearest bucket rather than vanishing from
 * the bar.
 */
export function gadgetGroups(
  entities: readonly DrawEntity[],
  ownEntityIds: ReadonlySet<number>,
  content: PartContentDocument | null,
): GadgetGroup[] {
  if (content === null) {
    return [];
  }

  const partsById = new Map<number, PartDefinition>();
  for (const part of content.parts) {
    partsById.set(part.partTypeId, part);
  }

  const groups = new Map<string, GadgetGroup>();
  for (const entity of entities) {
    if (!ownEntityIds.has(entity.entityId) || entity.bodyId === 0) {
      continue;
    }

    const part = partsById.get(entity.partTypeId);
    const kind = part?.capabilities?.activation;
    if (kind !== "toggle" && kind !== "trigger") {
      continue;
    }

    const direction = directionBucket(part, entity.yaw);
    const key = `${entity.partTypeId}:${direction}`;
    const existing = groups.get(key);
    if (existing !== undefined) {
      existing.count += 1;
      existing.active ||= entity.active;
      existing.entityIds.push(entity.entityId);
      continue;
    }

    groups.set(key, {
      partTypeId: entity.partTypeId,
      kind,
      label: `${labelFor(part, entity.partTypeId)} ${DIRECTION_ARROWS[direction]}`,
      count: 1,
      active: entity.active,
      hotkey: "",
      direction,
      entityIds: [entity.entityId],
    });
  }

  return [...groups.values()]
    .sort((left, right) => left.partTypeId - right.partTypeId || left.direction - right.direction)
    .map((group, index) => ({ ...group, hotkey: gadgetHotkey(index) ?? "", entityIds: group.entityIds.sort((a, b) => a - b) }));
}

function labelFor(part: PartDefinition | undefined, partTypeId: number): string {
  if (part?.variantName !== undefined) {
    return part.variantName;
  }

  const palette = PALETTE.find((entry) => entry.partTypeId === partTypeId);
  return palette?.label ?? part?.name ?? `#${partTypeId}`;
}
