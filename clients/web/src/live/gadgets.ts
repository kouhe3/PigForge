import { PALETTE } from "@/builder/slope";
import type { DrawEntity, PartContentDocument, PartDefinition } from "@/schema/types";

/** BPLE-style hotkey order: 1-9, 0, then A-Z. */
const HOTKEYS = "1234567890ABCDEFGHIJKLMNOPQRSTUVWXYZ";

export interface GadgetGroup {
  partTypeId: number;
  kind: "toggle" | "trigger";
  label: string;
  count: number;
  active: boolean;
  /** Empty when the group has no hotkey (more than 36 groups). */
  hotkey: string;
}

export function gadgetHotkey(index: number): string | null {
  return index >= 0 && index < HOTKEYS.length ? HOTKEYS[index] : null;
}

/**
 * The switch bar: one group per switchable part type this client owns and has
 * materialised, ascending by partTypeId. Parts whose content declares no
 * activation are dropped, so the bar follows what the player actually built.
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

  const groups = new Map<number, GadgetGroup>();
  for (const entity of entities) {
    if (!ownEntityIds.has(entity.entityId) || entity.bodyId === 0) {
      continue;
    }

    const part = partsById.get(entity.partTypeId);
    const kind = part?.capabilities?.activation;
    if (kind !== "toggle" && kind !== "trigger") {
      continue;
    }

    const existing = groups.get(entity.partTypeId);
    if (existing !== undefined) {
      existing.count += 1;
      existing.active ||= entity.active;
      continue;
    }

    groups.set(entity.partTypeId, {
      partTypeId: entity.partTypeId,
      kind,
      label: labelFor(part, entity.partTypeId),
      count: 1,
      active: entity.active,
      hotkey: "",
    });
  }

  return [...groups.values()]
    .sort((left, right) => left.partTypeId - right.partTypeId)
    .map((group, index) => ({ ...group, hotkey: gadgetHotkey(index) ?? "" }));
}

function labelFor(part: PartDefinition | undefined, partTypeId: number): string {
  if (part?.variantName !== undefined) {
    return part.variantName;
  }

  const palette = PALETTE.find((entry) => entry.partTypeId === partTypeId);
  return palette?.label ?? part?.name ?? `#${partTypeId}`;
}
