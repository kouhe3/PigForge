import { yawFromQuaternion } from "@/renderer/camera";
import type { DrawEntity, ReplayEntityState, SnapshotEntity } from "./types";

export function toDrawEntities(entities: readonly ReplayEntityState[] | readonly SnapshotEntity[]): DrawEntity[] {
  return entities.map((entity) => ({
    entityId: entity.entityId,
    partTypeId: entity.partTypeId,
    x: entity.position[0],
    y: entity.position[1],
    yaw: yawFromQuaternion(entity.rotation),
    // The snapshot's attach frame (see PGFS v4): a hinged wheel's mounts follow the chassis.
    // Replay documents carry no such frame, so the tracker supplies the build angle instead.
    attachYaw: "attachYaw" in entity ? entity.attachYaw : undefined,
    scale: entity.scale,
    vx: entity.linearVelocity[0],
    vy: entity.linearVelocity[1],
    bodyId: entity.physicsBodyId,
    active: "active" in entity ? entity.active : false,
    // PGFS v5's sub-entity flag (a glove's fist, a broken spring's endpoint); replay documents
    // have no such entity and carry no flag.
    subEntity: "subEntity" in entity ? entity.subEntity : false,
    // PGFS v6's mirror bit (ADR-030); replay documents have no such bit.
    mirrored: "mirrored" in entity ? entity.mirrored : false,
  }));
}
