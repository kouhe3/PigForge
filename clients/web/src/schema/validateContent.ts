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
  validateCapabilities(part.partTypeId, part.capabilities, errors);
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

function validateCapabilities(partTypeId: number, capabilities: unknown, errors: string[]): void {
  if (capabilities === undefined || capabilities === null) {
    return;
  }
  if (typeof capabilities !== "object" || capabilities === null) {
    errors.push(`Part ${partTypeId} capabilities must be an object.`);
    return;
  }
  const value = capabilities as Record<string, unknown>;
  if (value.pig !== undefined && typeof value.pig !== "boolean") {
    errors.push(`Part ${partTypeId} capabilities.pig must be a boolean.`);
  }
  if (value.wheel !== undefined && typeof value.wheel !== "boolean") {
    errors.push(`Part ${partTypeId} capabilities.wheel must be a boolean.`);
  }
  if (value.motor !== undefined) {
    const motor = value.motor as { thrustPerTick?: unknown; directionX?: unknown } | null;
    if (typeof motor !== "object" || motor === null
      || typeof motor.thrustPerTick !== "number" || !Number.isFinite(motor.thrustPerTick)
      || motor.directionX !== -1 && motor.directionX !== 0 && motor.directionX !== 1) {
      errors.push(`Part ${partTypeId} capabilities.motor needs a finite thrustPerTick and directionX in -1, 0, 1.`);
    }
  }
  if (value.tnt !== undefined) {
    const tnt = value.tnt as { fuseTicks?: unknown } | null;
    if (typeof tnt !== "object" || tnt === null
      || typeof tnt.fuseTicks !== "number" || !Number.isInteger(tnt.fuseTicks) || tnt.fuseTicks < 0) {
      errors.push(`Part ${partTypeId} capabilities.tnt needs a non-negative integer fuseTicks.`);
    }
  }
  if (value.balloon !== undefined) {
    if (typeof value.balloon !== "number" || !Number.isFinite(value.balloon)) {
      errors.push(`Part ${partTypeId} capabilities.balloon must be a finite liftPerTick number.`);
    }
  }
  if (value.fan !== undefined) {
    const fan = value.fan as { thrustPerTick?: unknown; directionX?: unknown; directionY?: unknown } | null;
    if (typeof fan !== "object" || fan === null
      || typeof fan.thrustPerTick !== "number" || !Number.isFinite(fan.thrustPerTick)) {
      errors.push(`Part ${partTypeId} capabilities.fan needs a finite thrustPerTick.`);
    }
  }
}
