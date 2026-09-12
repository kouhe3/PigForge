import type { SnapshotEntity, SnapshotFrame, Vec3, Quat } from "./types";

export const SNAPSHOT_VERSION = 4;
export const SNAPSHOT_BUILDING_PHASE = 0x10;
export const SNAPSHOT_HEADER_BYTES = 15;
export const SNAPSHOT_ENTITY_BYTES = 73;

export function decodeSnapshotFrame(source: Uint8Array): SnapshotFrame | string {
  if (source.length < SNAPSHOT_HEADER_BYTES) {
    return "Snapshot frame is truncated.";
  }
  if (source[0] !== 0x50 || source[1] !== 0x47 || source[2] !== 0x46 || source[3] !== 0x53) {
    return "Snapshot magic must be PGFS.";
  }

  const view = new DataView(source.buffer, source.byteOffset, source.byteLength);
  const version = view.getUint16(4, true);
  if (version !== SNAPSHOT_VERSION) {
    return `Snapshot version ${version} is unsupported.`;
  }

  const tick = view.getUint32(6, true);
  const phase = view.getUint8(10);
  const entityCount = view.getUint32(11, true);
  const needed = SNAPSHOT_HEADER_BYTES + entityCount * SNAPSHOT_ENTITY_BYTES;
  if (source.length < needed) {
    return "Snapshot entity payload is truncated.";
  }

  const entities: SnapshotEntity[] = [];
  for (let index = 0; index < entityCount; index++) {
    const offset = SNAPSHOT_HEADER_BYTES + index * SNAPSHOT_ENTITY_BYTES;
    entities.push({
      entityId: view.getUint32(offset, true),
      physicsBodyId: view.getUint32(offset + 4, true),
      partTypeId: view.getUint32(offset + 8, true),
      position: readVec3(view, offset + 12),
      rotation: readQuat(view, offset + 24),
      linearVelocity: readVec3(view, offset + 40),
      angularVelocity: readVec3(view, offset + 52),
      scale: view.getFloat32(offset + 64, true),
      attachYaw: view.getFloat32(offset + 68, true),
      active: (view.getUint8(offset + 72) & 1) === 1,
    });
  }

  return { version, tick, phase, entities };
}

function readVec3(view: DataView, offset: number): Vec3 {
  return [view.getFloat32(offset, true), view.getFloat32(offset + 4, true), view.getFloat32(offset + 8, true)];
}

function readQuat(view: DataView, offset: number): Quat {
  return [
    view.getFloat32(offset, true),
    view.getFloat32(offset + 4, true),
    view.getFloat32(offset + 8, true),
    view.getFloat32(offset + 12, true),
  ];
}
