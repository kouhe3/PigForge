import type { DrawEntity } from "@/schema/types";

/**
 * Build-orientation tracker for rolling parts.
 *
 * A wheel's physics body integrates its roll into the snapshot rotation, so `yaw` no longer
 * says where the part was built: the renderer needs that angle back for the sprites that do
 * not spin (a wheel's axle). Every frame that still shows the *layout* — a building-phase
 * frame, or a sandbox preview with `bodyId = 0` — reports the build pose, so it refreshes the
 * remembered angle. The first pose an entity is ever seen at is the fallback, which is exact
 * for a replay or a client that joins before Start and merely stale for one that joins
 * mid-run (better than spinning a mount that must not move).
 */
export interface RestYawTracker {
  /** Annotates a frame with each entity's build orientation. */
  apply(entities: readonly DrawEntity[], layoutFrame: boolean): DrawEntity[];
}

export function createRestYawTracker(): RestYawTracker {
  const restYawByEntity = new Map<number, number>();
  return {
    apply(entities: readonly DrawEntity[], layoutFrame: boolean): DrawEntity[] {
      const present = new Set<number>();
      const annotated = entities.map((entity) => {
        present.add(entity.entityId);
        if (layoutFrame || entity.bodyId === 0 || !restYawByEntity.has(entity.entityId)) {
          restYawByEntity.set(entity.entityId, entity.yaw);
        }

        return { ...entity, restYaw: restYawByEntity.get(entity.entityId) };
      });
      for (const entityId of restYawByEntity.keys()) {
        if (!present.has(entityId)) {
          restYawByEntity.delete(entityId);
        }
      }

      return annotated;
    },
  };
}
