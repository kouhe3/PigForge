import type { LevelBuildLimits } from "@/schema/levelContent";
import type { PartContentDocument, PartDefinition } from "@/schema/types";
import { PALETTE } from "./slope";

/** A palette row: a base part family and its label, the shape `slope.ts`'s `PALETTE` has. */
export interface PaletteEntry {
  partTypeId: number;
  label: string;
}

/** Skins of a base part, declared by content (variantOf), in content order. */
export function variantsOf(content: PartContentDocument | null, basePartTypeId: number): PartDefinition[] {
  return content?.parts.filter((part) => part.variantOf === basePartTypeId) ?? [];
}

/** Palette label for a variant: its content label, else `<base> #<ordinal>` (1-based). */
export function variantLabel(baseLabel: string, ordinal: number, variant: PartDefinition): string {
  return variant.variantName ?? `${baseLabel} #${ordinal}`;
}

/**
 * The base family a `partTypeId` belongs to: its content `variantOf` when it is a skin, else its own
 * id. The level's allowance is keyed by the family, so a variant spends its base's allowance.
 */
export function partFamily(content: PartContentDocument | null, partTypeId: number): number {
  return content?.parts.find((part) => part.partTypeId === partTypeId)?.variantOf ?? partTypeId;
}

/**
 * The palette rows a level allows: only families its `partCounts` grants more than zero of, in
 * `PALETTE` order. A family with no allowance never appears, matching the original hiding a zero
 * count (`ConstructionUI.CheckUnlockedParts`); variants hang off their base row, so they follow it.
 */
export function allowedPalette(limits: LevelBuildLimits): PaletteEntry[] {
  const allowed = new Set(limits.partCounts.filter((entry) => entry.count > 0).map((entry) => entry.partTypeId));
  return PALETTE.filter((entry) => allowed.has(entry.partTypeId))
    .map((entry) => ({ partTypeId: entry.partTypeId, label: entry.label }));
}

/**
 * Remaining allowance per family: the level's count minus the parts the player already placed. The
 * first entry of a repeated family wins, matching `BaseGameMode.GetPartCount`; `placed` counts only
 * the player's own parts (deviation D2).
 */
export function remainingCounts(limits: LevelBuildLimits, placed: ReadonlyMap<number, number>): Map<number, number> {
  const remaining = new Map<number, number>();
  for (const entry of limits.partCounts) {
    if (remaining.has(entry.partTypeId)) {
      continue;
    }
    remaining.set(entry.partTypeId, Math.max(0, entry.count - (placed.get(entry.partTypeId) ?? 0)));
  }
  return remaining;
}
