import { yawFromQuaternion } from "@/renderer/camera";
import type { DrawEntity, ReplayEntityState, SnapshotEntity } from "./types";

export function toDrawEntities(entities: readonly ReplayEntityState[] | readonly SnapshotEntity[]): DrawEntity[] {
  return entities.map((entity) => ({
    entityId: entity.entityId,
    partTypeId: entity.partTypeId,
    x: entity.position[0],
    y: entity.position[1],
    yaw: yawFromQuaternion(entity.rotation),
    scale: entity.scale,
    vx: entity.linearVelocity[0],
    vy: entity.linearVelocity[1],
    bodyId: entity.physicsBodyId,
  }));
}
