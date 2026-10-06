import type {
  ConnectionVisual,
  GridBox,
  PartContentDocument,
  PartDefinition,
  PartDamping,
  PartGlove,
  PartGloveDrive,
  PartGloveShoot,
  PartGloveWind,
  PartShape,
  PartSpring,
} from "./types";

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
  if (document.physics === null || typeof document.physics !== "object") {
    errors.push("physics is required (the original's project-wide rigidbody defaults).");
  } else if (typeof document.physics.maximumAngularSpeed !== "number" || !Number.isFinite(document.physics.maximumAngularSpeed) || document.physics.maximumAngularSpeed <= 0) {
    errors.push("physics.maximumAngularSpeed must be finite and positive (7 rad/s in the original).");
  } else if (!isDamping(document.physics.damping)) {
    errors.push("physics.damping must be { linear, angular } with non-negative finite numbers.");
  }
  if (!Array.isArray(document.parts) || document.parts.length === 0) {
    errors.push("parts must contain at least one definition.");
    return errors;
  }

  const seen = new Set<number>();
  for (const part of document.parts) {
    validatePart(part, seen, errors);
  }
  validateVariants(document.parts, errors);
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
  if (part.variantOf !== undefined && (!Number.isInteger(part.variantOf) || part.variantOf < 1)) {
    errors.push(`Part ${part.partTypeId} variantOf must be a positive integer.`);
  }
  if (part.variantName !== undefined && (typeof part.variantName !== "string" || part.variantName.length === 0 || part.variantName.length > 64)) {
    errors.push(`Part ${part.partTypeId} variantName must contain 1 to 64 characters.`);
  }
  validateCapabilities(part.partTypeId, part.capabilities, errors);
  if (part.gridBox !== undefined) {
    validateGridBox(part.partTypeId, part.gridBox, errors);
  }
  if (part.connectionVisual !== undefined && !CONNECTION_VISUALS.includes(part.connectionVisual)) {
    errors.push(`Part ${part.partTypeId} connectionVisual must be one of attachmentFallback, attachmentPlain, attachmentEight or frame.`);
  }
  if (part.damping !== undefined && !isDamping(part.damping)) {
    errors.push(`Part ${part.partTypeId} damping must be { linear, angular } with non-negative finite numbers.`);
  }
  if (!Array.isArray(part.shapes) || part.shapes.length === 0) {
    errors.push(`Part ${part.partTypeId} needs at least one shape.`);
    return;
  }
  for (const shape of part.shapes) {
    validateShape(part.partTypeId, shape, errors);
  }
}

/**
 * Unity's `Rigidbody.drag` / `angularDrag` pair (`tools/bple-damping`): both values are required and
 * neither may be negative, because a negative damping would accelerate a body instead of slowing it.
 */
function isDamping(value: unknown): value is PartDamping {
  if (value === null || typeof value !== "object") {
    return false;
  }
  const damping = value as { linear?: unknown; angular?: unknown };
  return (
    Object.keys(value).every((key) => key === "linear" || key === "angular")
    && typeof damping.linear === "number"
    && Number.isFinite(damping.linear)
    && damping.linear >= 0
    && typeof damping.angular === "number"
    && Number.isFinite(damping.angular)
    && damping.angular >= 0
  );
}

/** The original prefab scripts that gate conditional colliders (see `tools/bple-connections`). */
const CONNECTION_VISUALS: readonly ConnectionVisual[] = ["attachmentFallback", "attachmentPlain", "attachmentEight", "frame"];

/**
 * The original's build-grid cell box (`m_gridXmin/m_gridXmax/m_gridYmin/m_gridYmax`). The bounds are
 * inclusive cell indices, so they must be integers and ordered; an absent box means the original's
 * default single cell at the origin, which is why only the 3x2 KingPig/GoldenPig families declare one.
 */
function validateGridBox(partTypeId: number, gridBox: GridBox, errors: string[]): void {
  if (gridBox === null || typeof gridBox !== "object") {
    errors.push(`Part ${partTypeId} gridBox must be an object.`);
    return;
  }
  for (const key of ["minX", "maxX", "minY", "maxY"] as const) {
    if (!Number.isInteger(gridBox[key])) {
      errors.push(`Part ${partTypeId} gridBox.${key} must be an integer cell index.`);
      return;
    }
  }
  if (gridBox.minX > gridBox.maxX) {
    errors.push(`Part ${partTypeId} gridBox.minX must not exceed maxX.`);
  }
  if (gridBox.minY > gridBox.maxY) {
    errors.push(`Part ${partTypeId} gridBox.minY must not exceed maxY.`);
  }
}

/** A variant groups under a declared base part; chains and self references are rejected. */
function validateVariants(parts: PartDefinition[], errors: string[]): void {
  const byId = new Map<number, PartDefinition>();
  for (const part of parts) {
    if (!byId.has(part.partTypeId)) byId.set(part.partTypeId, part);
  }
  for (const part of parts) {
    if (part.variantOf === undefined) continue;
    const base = byId.get(part.variantOf);
    if (part.variantOf === part.partTypeId) {
      errors.push(`Part ${part.partTypeId} cannot be a variant of itself.`);
    } else if (!base) {
      errors.push(`Part ${part.partTypeId} is a variant of undeclared part ${part.variantOf}.`);
    } else if (base.variantOf !== undefined) {
      errors.push(`Part ${part.partTypeId} is a variant of part ${part.variantOf}, which is itself a variant.`);
    }
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
  if (shape.offset !== undefined && (!isVec3(shape.offset) || !shape.offset.every((value) => Number.isFinite(value)))) {
    errors.push(`Part ${partTypeId} shape offset must be three finite numbers.`);
  }
  if (shape.condition !== undefined) {
    const condition = shape.condition as { kind?: unknown; side?: unknown };
    if (condition.kind === "frame") {
      if (condition.side !== undefined) {
        errors.push(`Part ${partTypeId} shape condition { kind: 'frame' } takes no side.`);
      }
    } else {
      const sides = ["top", "bottom", "left", "right", "topLeft", "topRight", "bottomLeft", "bottomRight"];
      if (condition.kind !== "attachment" || typeof condition.side !== "string" || !sides.includes(condition.side)) {
        errors.push(`Part ${partTypeId} shape condition must be { kind: 'attachment', side: <side> } or { kind: 'frame' }.`);
      }
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
    const tnt = value.tnt as { fuseTicks?: unknown; chainDetonate?: unknown; igniteOnImpact?: unknown } | null;
    if (typeof tnt !== "object" || tnt === null
      || typeof tnt.fuseTicks !== "number" || !Number.isInteger(tnt.fuseTicks) || tnt.fuseTicks < 0
      || tnt.chainDetonate !== undefined && typeof tnt.chainDetonate !== "boolean"
      || tnt.igniteOnImpact !== undefined && typeof tnt.igniteOnImpact !== "boolean") {
      errors.push(`Part ${partTypeId} capabilities.tnt needs a non-negative integer fuseTicks and optional boolean chainDetonate/igniteOnImpact.`);
    }
  }
  if (value.balloon !== undefined) {
    if (typeof value.balloon !== "number" || !Number.isFinite(value.balloon)) {
      errors.push(`Part ${partTypeId} capabilities.balloon must be a finite liftPerTick number.`);
    }
  }
  if (value.fan !== undefined) {
    const fan = value.fan as
      | { thrustPerTick?: unknown; directionX?: unknown; directionY?: unknown; maxSpeed?: unknown; rotor?: unknown }
      | null;
    if (typeof fan !== "object" || fan === null
      || typeof fan.thrustPerTick !== "number" || !Number.isFinite(fan.thrustPerTick)
      || fan.maxSpeed !== undefined && (typeof fan.maxSpeed !== "number" || !Number.isFinite(fan.maxSpeed) || fan.maxSpeed <= 0)
      || fan.rotor !== undefined && typeof fan.rotor !== "boolean") {
      errors.push(`Part ${partTypeId} capabilities.fan needs a finite thrustPerTick, an optional positive maxSpeed and an optional boolean rotor.`);
    }
  }
  if (value.dampingRamp !== undefined) {
    const ramp = value.dampingRamp as { speedThreshold?: unknown; base?: unknown; slope?: unknown } | null;
    if (typeof ramp !== "object" || ramp === null
      || typeof ramp.speedThreshold !== "number" || !Number.isFinite(ramp.speedThreshold) || ramp.speedThreshold <= 0
      || typeof ramp.base !== "number" || !Number.isFinite(ramp.base) || ramp.base < 0
      || typeof ramp.slope !== "number" || !Number.isFinite(ramp.slope) || ramp.slope < 0) {
      errors.push(`Part ${partTypeId} capabilities.dampingRamp needs a positive speedThreshold and a finite non-negative base and slope.`);
    }
  }
  if (value.spring !== undefined) {
    if (!isSpring(value.spring)) {
      errors.push(`Part ${partTypeId} capabilities.spring must be { stiffness, damper, limit, bounciness, breakForce }, with a positive stiffness/breakForce and non-negative damper/limit/bounciness.`);
    }
  }
  if (value.glove !== undefined) {
    if (!isGlove(value.glove)) {
      errors.push(`Part ${partTypeId} capabilities.glove must be { mass, shapes, limit, yDrive, xDrive, projectionDistance, shoot, wind, solverIterationScale }: a positive glove mass and solver scale, a non-empty shape list, a non-negative limit/projectionDistance, { spring, damper } drives, and shoot/wind move objects.`);
    } else {
      for (const shape of (value.glove as PartGlove).shapes) {
        validateShape(partTypeId, shape, errors);
      }
    }
  }
  if (value.rocket !== undefined) {
    const rocket = value.rocket as { thrustPerTick?: unknown; directionX?: unknown; directionY?: unknown; ignitionTicks?: unknown; boostTicks?: unknown; endTicks?: unknown; maxSpeed?: unknown; visualization?: unknown; explodeRadius?: unknown; explodeImpulse?: unknown } | null;
    const phase = (ticks: unknown) => typeof ticks === "number" && Number.isInteger(ticks) && ticks >= 0 && ticks <= 65535;
    if (typeof rocket !== "object" || rocket === null
      || typeof rocket.thrustPerTick !== "number" || !Number.isFinite(rocket.thrustPerTick)
      || rocket.directionX !== -1 && rocket.directionX !== 0 && rocket.directionX !== 1
      || rocket.directionY !== undefined && rocket.directionY !== -1 && rocket.directionY !== 0 && rocket.directionY !== 1
      || !phase(rocket.ignitionTicks) || !phase(rocket.boostTicks) || !phase(rocket.endTicks)
      || typeof rocket.maxSpeed !== "number" || !Number.isFinite(rocket.maxSpeed) || rocket.maxSpeed < 0
      || rocket.visualization !== undefined && typeof rocket.visualization !== "boolean"
      || rocket.explodeRadius !== undefined && (typeof rocket.explodeRadius !== "number" || !Number.isFinite(rocket.explodeRadius) || rocket.explodeRadius < 0)
      || rocket.explodeImpulse !== undefined && (typeof rocket.explodeImpulse !== "number" || !Number.isFinite(rocket.explodeImpulse) || rocket.explodeImpulse < 0)) {
      errors.push(`Part ${partTypeId} capabilities.rocket needs finite thrustPerTick, directionX/directionY in -1, 0, 1, non-negative integer ignitionTicks/boostTicks/endTicks, a non-negative finite maxSpeed, optional visualization and optional non-negative explodeRadius/explodeImpulse.`);
    }
  }
  if (value.egg !== undefined && typeof value.egg !== "boolean") {
    errors.push(`Part ${partTypeId} capabilities.egg must be a boolean.`);
  }
  if (value.wing !== undefined) {
    const wing = value.wing as { liftConstant?: unknown } | null;
    if (typeof wing !== "object" || wing === null
      || typeof wing.liftConstant !== "number" || !Number.isFinite(wing.liftConstant)) {
      errors.push(`Part ${partTypeId} capabilities.wing needs a finite liftConstant.`);
    }
  }
  if (value.tail !== undefined && (typeof value.tail !== "number" || !Number.isFinite(value.tail))) {
    errors.push(`Part ${partTypeId} capabilities.tail must be a finite liftConstant number.`);
  }
  if (value.mirror !== undefined && typeof value.mirror !== "boolean") {
    errors.push(`Part ${partTypeId} capabilities.mirror must be a boolean.`);
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
  if (value.bellows !== undefined) {
    const bellows = value.bellows as { directionX?: unknown; directionY?: unknown; thrustPerTick?: unknown; inflateTicks?: unknown } | null;
    if (typeof bellows !== "object" || bellows === null
      || bellows.directionX !== -1 && bellows.directionX !== 0 && bellows.directionX !== 1
      || bellows.directionY !== undefined && bellows.directionY !== -1 && bellows.directionY !== 0 && bellows.directionY !== 1
      || typeof bellows.thrustPerTick !== "number" || !Number.isFinite(bellows.thrustPerTick)
      || typeof bellows.inflateTicks !== "number" || !Number.isInteger(bellows.inflateTicks) || bellows.inflateTicks < 0 || bellows.inflateTicks > 65535) {
      errors.push(`Part ${partTypeId} capabilities.bellows needs a directionX/directionY in -1, 0, 1, a finite thrustPerTick and an inflateTicks integer in [0, 65535].`);
    }
  }
  if (value.light !== undefined && (typeof value.light !== "number" || !Number.isFinite(value.light) || value.light < 0)) {
    errors.push(`Part ${partTypeId} capabilities.light must be a non-negative radius number.`);
  }
  if (value.grapple !== undefined) {
    const grapple = value.grapple as { impulse?: unknown; directionX?: unknown; directionY?: unknown } | null;
    if (typeof grapple !== "object" || grapple === null
      || typeof grapple.impulse !== "number" || !Number.isFinite(grapple.impulse)
      || grapple.directionX !== undefined && (typeof grapple.directionX !== "number" || !Number.isFinite(grapple.directionX))
      || grapple.directionY !== undefined && (typeof grapple.directionY !== "number" || !Number.isFinite(grapple.directionY))) {
      errors.push(`Part ${partTypeId} capabilities.grapple needs finite impulse and optional finite directionX/directionY.`);
    }
  }
  if (value.blaster !== undefined) {
    const blaster = value.blaster as { radius?: unknown; impulse?: unknown; chainRadius?: unknown } | null;
    if (typeof blaster !== "object" || blaster === null
      || typeof blaster.radius !== "number" || !Number.isFinite(blaster.radius) || blaster.radius <= 0
      || typeof blaster.impulse !== "number" || !Number.isFinite(blaster.impulse) || blaster.impulse < 0
      || blaster.chainRadius !== undefined && (typeof blaster.chainRadius !== "number" || !Number.isFinite(blaster.chainRadius) || blaster.chainRadius < 0)) {
      errors.push(`Part ${partTypeId} capabilities.blaster needs a positive finite radius, a non-negative finite impulse, and an optional non-negative finite chainRadius.`);
    }
  }
  if (value.glue !== undefined && typeof value.glue !== "boolean") {
    errors.push(`Part ${partTypeId} capabilities.glue must be a boolean.`);
  }
  if (value.activation !== undefined && value.activation !== "toggle" && value.activation !== "trigger") {
    errors.push(`Part ${partTypeId} capabilities.activation must be 'toggle' or 'trigger'.`);
  }
  if (value.jointConnectionType !== undefined && value.jointConnectionType !== "none" && value.jointConnectionType !== "source" && value.jointConnectionType !== "target") {
    errors.push(`Part ${partTypeId} capabilities.jointConnectionType must be 'none', 'source' or 'target'.`);
  }
  if (value.jointConnectionStrength !== undefined && !["weak", "normal", "high", "extreme", "highlyExtreme"].includes(value.jointConnectionStrength as string)) {
    errors.push(`Part ${partTypeId} capabilities.jointConnectionStrength must be 'weak', 'normal', 'high', 'extreme' or 'highlyExtreme'.`);
  }
  if (value.jointConnectionDirection !== undefined
    && !["any", "right", "up", "left", "down", "leftAndRight", "upAndDown", "none"].includes(value.jointConnectionDirection as string)) {
    errors.push(`Part ${partTypeId} capabilities.jointConnectionDirection must be one of any/right/up/left/down/leftAndRight/upAndDown/none.`);
  }
}

// The server parser rejects unknown properties inside these objects, so the client checks the same
// exact key sets instead of accepting a typo the server would refuse.
const SPRING_KEYS = ["stiffness", "damper", "limit", "bounciness", "breakForce"];
const GLOVE_KEYS = ["mass", "shapes", "limit", "yDrive", "xDrive", "projectionDistance", "shoot", "wind", "solverIterationScale"];
const GLOVE_DRIVE_KEYS = ["spring", "damper"];
const GLOVE_SHOOT_KEYS = ["distanceY", "deviationX", "time", "limitSpring"];
const GLOVE_WIND_KEYS = ["time", "mass", "driveSpring", "driveDamper"];

function hasExactKeys(value: Record<string, unknown>, keys: string[]): boolean {
  const actual = Object.keys(value);
  return actual.length === keys.length && actual.every((key) => keys.includes(key));
}

function isPositiveNumber(value: unknown): value is number {
  return typeof value === "number" && Number.isFinite(value) && value > 0;
}

function isNonNegativeNumber(value: unknown): value is number {
  return typeof value === "number" && Number.isFinite(value) && value >= 0;
}

/** The original Spring's own joint: the declaration defaults' y soft limit, all values extracted. */
function isSpring(value: unknown): value is PartSpring {
  if (value === null || typeof value !== "object") {
    return false;
  }
  const spring = value as Record<string, unknown>;
  return hasExactKeys(spring, SPRING_KEYS)
    && isPositiveNumber(spring.stiffness)
    && isNonNegativeNumber(spring.damper)
    && isNonNegativeNumber(spring.limit)
    && isNonNegativeNumber(spring.bounciness)
    && isPositiveNumber(spring.breakForce);
}

function isGloveDrive(value: unknown): value is PartGloveDrive {
  if (value === null || typeof value !== "object") {
    return false;
  }
  const drive = value as Record<string, unknown>;
  return hasExactKeys(drive, GLOVE_DRIVE_KEYS) && isPositiveNumber(drive.spring) && isNonNegativeNumber(drive.damper);
}

function isGloveShoot(value: unknown): value is PartGloveShoot {
  if (value === null || typeof value !== "object") {
    return false;
  }
  const shoot = value as Record<string, unknown>;
  return hasExactKeys(shoot, GLOVE_SHOOT_KEYS)
    && isNonNegativeNumber(shoot.distanceY)
    && isNonNegativeNumber(shoot.deviationX)
    && isPositiveNumber(shoot.time)
    && isNonNegativeNumber(shoot.limitSpring);
}

function isGloveWind(value: unknown): value is PartGloveWind {
  if (value === null || typeof value !== "object") {
    return false;
  }
  const wind = value as Record<string, unknown>;
  return hasExactKeys(wind, GLOVE_WIND_KEYS)
    && isPositiveNumber(wind.time)
    && isPositiveNumber(wind.mass)
    && isPositiveNumber(wind.driveSpring)
    && isNonNegativeNumber(wind.driveDamper);
}

/** The interactive SpringBoxingGlove's second body, joint drives and shoot/wind moves. */
function isGlove(value: unknown): value is PartGlove {
  if (value === null || typeof value !== "object") {
    return false;
  }
  const glove = value as Record<string, unknown>;
  return hasExactKeys(glove, GLOVE_KEYS)
    && isPositiveNumber(glove.mass)
    && Array.isArray(glove.shapes) && glove.shapes.length > 0
    && isNonNegativeNumber(glove.limit)
    && isGloveDrive(glove.yDrive)
    && isGloveDrive(glove.xDrive)
    && isNonNegativeNumber(glove.projectionDistance)
    && isGloveShoot(glove.shoot)
    && isGloveWind(glove.wind)
    && isPositiveNumber(glove.solverIterationScale);
}
