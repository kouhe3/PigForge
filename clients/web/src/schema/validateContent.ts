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
  if (value.spring !== undefined) {
    if (typeof value.spring !== "number" || !Number.isFinite(value.spring)) {
      errors.push(`Part ${partTypeId} capabilities.spring must be a finite bounceImpulsePerTick number.`);
    }
  }
  if (value.rocket !== undefined) {
    const rocket = value.rocket as { thrustPerTick?: unknown; directionX?: unknown; directionY?: unknown; durationTicks?: unknown; explodeRadius?: unknown; explodeImpulse?: unknown } | null;
    if (typeof rocket !== "object" || rocket === null
      || typeof rocket.thrustPerTick !== "number" || !Number.isFinite(rocket.thrustPerTick)
      || rocket.directionX !== -1 && rocket.directionX !== 0 && rocket.directionX !== 1
      || rocket.directionY !== undefined && rocket.directionY !== -1 && rocket.directionY !== 0 && rocket.directionY !== 1
      || typeof rocket.durationTicks !== "number" || !Number.isInteger(rocket.durationTicks) || rocket.durationTicks < 0
      || rocket.explodeRadius !== undefined && (typeof rocket.explodeRadius !== "number" || !Number.isFinite(rocket.explodeRadius) || rocket.explodeRadius < 0)
      || rocket.explodeImpulse !== undefined && (typeof rocket.explodeImpulse !== "number" || !Number.isFinite(rocket.explodeImpulse) || rocket.explodeImpulse < 0)) {
      errors.push(`Part ${partTypeId} capabilities.rocket needs finite thrustPerTick, directionX/directionY in -1, 0, 1, non-negative integer durationTicks, optional non-negative explodeRadius/explodeImpulse.`);
    }
  }
  if (value.egg !== undefined && typeof value.egg !== "boolean") {
    errors.push(`Part ${partTypeId} capabilities.egg must be a boolean.`);
  }
  if (value.wing !== undefined) {
    const wing = value.wing as { liftCoef?: unknown; maxLift?: unknown } | null;
    if (typeof wing !== "object" || wing === null
      || typeof wing.liftCoef !== "number" || !Number.isFinite(wing.liftCoef)
      || wing.maxLift !== undefined && (typeof wing.maxLift !== "number" || !Number.isFinite(wing.maxLift) || wing.maxLift < 0)) {
      errors.push(`Part ${partTypeId} capabilities.wing needs finite liftCoef and optional non-negative maxLift.`);
    }
  }
  if (value.tail !== undefined && (typeof value.tail !== "number" || !Number.isFinite(value.tail))) {
    errors.push(`Part ${partTypeId} capabilities.tail must be a finite dragCoef number.`);
  }
  if (value.umbrella !== undefined && (typeof value.umbrella !== "number" || !Number.isFinite(value.umbrella))) {
    errors.push(`Part ${partTypeId} capabilities.umbrella must be a finite dragCoef number.`);
  }
  if (value.gearbox !== undefined && typeof value.gearbox !== "boolean") {
    errors.push(`Part ${partTypeId} capabilities.gearbox must be a boolean.`);
  }
  if (value.detacher !== undefined && typeof value.detacher !== "boolean") {
    errors.push(`Part ${partTypeId} capabilities.detacher must be a boolean.`);
  }
  if (value.bellows !== undefined && (typeof value.bellows !== "number" || !Number.isFinite(value.bellows))) {
    errors.push(`Part ${partTypeId} capabilities.bellows must be a finite boostImpulse number.`);
  }
}
