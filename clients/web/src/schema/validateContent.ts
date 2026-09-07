import type { PartContentDocument, PartDefinition, PartShape } from "./types";

export function validatePartContent(value: unknown): string[] {
  const errors: string[] = [];
  if (value === null || typeof value !== "object") {
    return ["Part content document is required."];
  }

  const document = value as Partial<PartContentDocument>;
  if (document.format !== "pigforge.part-content") {
    errors.push("Format must be 'pigforge.part-content'.");
  }
  if (document.schemaVersion !== 1) {
    errors.push("schemaVersion must be 1.");
  }
  if (typeof document.contentVersion !== "string" || document.contentVersion.trim().length === 0) {
    errors.push("contentVersion is required.");
  }
  if (!Array.isArray(document.parts) || document.parts.length === 0) {
    errors.push("parts must contain at least one definition.");
    return errors;
  }

  const seen = new Set<number>();
  for (const part of document.parts) {
    validatePart(part, seen, errors);
  }
  return errors;
}

function validatePart(part: PartDefinition, seen: Set<number>, errors: string[]): void {
  if (!Number.isInteger(part.partTypeId) || part.partTypeId < 1) {
    errors.push("A partTypeId must be a positive integer.");
    return;
  }
  if (seen.has(part.partTypeId)) {
    errors.push(`Duplicate partTypeId ${part.partTypeId}.`);
  }
  seen.add(part.partTypeId);
  if (typeof part.name !== "string" || part.name.length === 0) {
    errors.push(`Part ${part.partTypeId} is missing a name.`);
  }
  if (part.mode !== "static" && part.mode !== "dynamic") {
    errors.push(`Part ${part.partTypeId} has an unknown mode.`);
  }
  if (typeof part.mass !== "number" || !Number.isFinite(part.mass)) {
    errors.push(`Part ${part.partTypeId} mass must be finite.`);
  } else if (part.mode === "dynamic" && part.mass <= 0) {
    errors.push(`Part ${part.partTypeId} dynamic mass must be positive.`);
  } else if (part.mode === "static" && part.mass !== 0) {
    errors.push(`Part ${part.partTypeId} static mass must be zero.`);
  }
  if ("assetGuid" in part || "guid" in part) {
    errors.push(`Part ${part.partTypeId} contains a GUID-like field.`);
  }
  if (!Array.isArray(part.shapes) || part.shapes.length === 0) {
    errors.push(`Part ${part.partTypeId} needs at least one shape.`);
    return;
  }
  for (const shape of part.shapes) {
    validateShape(part.partTypeId, shape, errors);
  }
}

function validateShape(partTypeId: number, shape: PartShape, errors: string[]): void {
  if (shape.kind === "box") {
    if (!isVec3(shape.halfExtents) || shape.halfExtents.some((value) => value <= 0)) {
      errors.push(`Part ${partTypeId} box halfExtents must be three positive numbers.`);
    }
  } else if (shape.kind === "sphere") {
    if (typeof shape.radius !== "number" || !Number.isFinite(shape.radius) || shape.radius <= 0) {
      errors.push(`Part ${partTypeId} sphere radius must be finite and positive.`);
    }
  }
}

function isVec3(value: unknown): value is [number, number, number] {
  return Array.isArray(value) && value.length === 3 && value.every((item) => typeof item === "number");
}
