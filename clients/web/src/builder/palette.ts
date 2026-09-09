import type { PartContentDocument, PartDefinition } from "@/schema/types";

/** Skins of a base part, declared by content (variantOf), in content order. */
export function variantsOf(content: PartContentDocument | null, basePartTypeId: number): PartDefinition[] {
  return content?.parts.filter((part) => part.variantOf === basePartTypeId) ?? [];
}

/** Palette label for a variant: its content label, else `<base> #<ordinal>` (1-based). */
export function variantLabel(baseLabel: string, ordinal: number, variant: PartDefinition): string {
  return variant.variantName ?? `${baseLabel} #${ordinal}`;
}
