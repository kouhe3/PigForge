import type { ReplayDocument, ReplayEntityState, ReplayFrame, ReplayHeader } from "./types";

const HASH = /^[0-9a-fA-F]{64}$/;

export function validateReplay(value: unknown): string[] {
  const errors: string[] = [];
  if (value === null || typeof value !== "object") {
    return ["Replay document is required."];
  }

  const document = value as Partial<ReplayDocument>;
  if (document.format !== "pigforge.physics.replay") {
    errors.push("Format must be 'pigforge.physics.replay'.");
  }
  validateHeader(document.header, errors);
  if (!document.initialState || !Array.isArray(document.initialState.entities)) {
    errors.push("initialState.entities is required.");
  } else {
    document.initialState.entities.forEach((entity, index) => validateEntity(entity, `initialState.entities[${index}]`, errors));
  }
  if (!Array.isArray(document.commands)) {
    errors.push("commands is required.");
  }
  if (!Array.isArray(document.frames) || document.frames.length === 0) {
    errors.push("frames must contain at least one frame.");
  } else {
    validateFrames(document.frames, document.header?.simulationTicks, errors);
  }
  if (!document.finalResult) {
    errors.push("finalResult is required.");
  } else {
    if (!["SUCCESS", "FAILURE", "ABORTED"].includes(document.finalResult.outcome)) {
      errors.push("finalResult.outcome is invalid.");
    }
    if (typeof document.finalResult.completedTick !== "number") {
      errors.push("finalResult.completedTick is required.");
    }
    if (typeof document.finalResult.stateHash !== "string" || !HASH.test(document.finalResult.stateHash)) {
      errors.push("finalResult.stateHash must be 64 hex characters.");
    }
  }
  return errors;
}

function validateHeader(header: ReplayHeader | undefined, errors: string[]): void {
  if (!header) {
    errors.push("Header is required.");
    return;
  }
  if (header.protocolVersion !== 2) {
    errors.push(`ProtocolVersion ${header.protocolVersion} is unsupported.`);
  }
  if (header.stateHashAlgorithm !== "sha256-canonical-v2") {
    errors.push("StateHashAlgorithm must be 'sha256-canonical-v2'.");
  }
  if (!header.contentVersion) {
    errors.push("ContentVersion is required.");
  }
  if (!Number.isInteger(header.fixedTickRate) || header.fixedTickRate < 1) {
    errors.push("fixedTickRate must be a positive integer.");
  }
  if (!Number.isInteger(header.simulationTicks) || header.simulationTicks < 1) {
    errors.push("simulationTicks must be a positive integer.");
  }
}

function validateFrames(frames: ReplayFrame[], simulationTicks: number | undefined, errors: string[]): void {
  if (simulationTicks !== undefined && frames.length !== simulationTicks) {
    errors.push(`frames length ${frames.length} does not match simulationTicks ${simulationTicks}.`);
  }
  for (let index = 0; index < frames.length; index++) {
    const frame = frames[index];
    if (frame.tick !== index + 1) {
      errors.push(`frames[${index}].tick must be ${index + 1}.`);
    }
    if (!Array.isArray(frame.snapshots) || !Array.isArray(frame.events)) {
      errors.push(`frames[${index}] needs snapshots and events arrays.`);
      continue;
    }
    frame.snapshots.forEach((entity, entityIndex) =>
      validateEntity(entity, `frames[${index}].snapshots[${entityIndex}]`, errors),
    );
  }
}

function validateEntity(entity: ReplayEntityState, path: string, errors: string[]): void {
  if (!Number.isInteger(entity.entityId) || entity.entityId < 1) {
    errors.push(`${path}.entityId must be a positive integer.`);
  }
  if (!isVec3(entity.position) || entity.position.some((value) => !Number.isFinite(value))) {
    errors.push(`${path}.position must be three finite numbers.`);
  }
  if (!Array.isArray(entity.rotation) || entity.rotation.length !== 4 || entity.rotation.some((value) => !Number.isFinite(value))) {
    errors.push(`${path}.rotation must be four finite numbers.`);
  }
  if (typeof entity.scale !== "number" || !Number.isFinite(entity.scale) || entity.scale <= 0 || entity.scale > 4) {
    errors.push(`${path}.scale must be in (0, 4].`);
  }
}

function isVec3(value: unknown): value is [number, number, number] {
  return Array.isArray(value) && value.length === 3 && value.every((item) => typeof item === "number");
}
