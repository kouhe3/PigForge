/**
 * Activation-time animation: what the original's own scripts do to a placed part's art between
 * its switch edge and the end of the activation. Three parts of the original do this
 * (`docs/specs/part-texture-animation.md`, `G103`):
 *
 *  - the bottle family jitters its `BottleVisualization` node, cross-fades the two
 *    `BottleContent` sprites and launches the cork (`Rocket.cs:202-262`, `Cork.cs:15-33`);
 *  - the BlasterTNT grows its blast ring for two seconds (`BlasterTNT.cs:123-169, 219-227`).
 *
 * The one driver is the snapshot's switch bit. A *trigger* part -- which is every part that has
 * an activation descriptor -- spends its press in the same tick the server reads it
 * (`GameplayRules.TryConsumeButtonPress`), so the bit is true for exactly one snapshot: measured
 * on the real room, a bottle's press raised it on tick 186 and nothing after, a blaster's on tick
 * 311 (`tasks/HANDOFF.md` §0.6 probe method). The rising edge is therefore taken from the
 * *snapshot stream* (`noteActivationEdges`), never from a rendered frame, which would drop it as
 * soon as two snapshots land between two paints.
 *
 * No clock of its own: `startedAt` is the animation state's own accumulated time, so a frozen
 * view (build mode, a paused replay) freezes the activation with it, exactly like the original's
 * `Time.timeScale = 0`.
 *
 * A run outlives its own window on purpose. The original never restores what its cross-fade did:
 * after the ignition both `BottleContent` alphas are zero and the cork object is destroyed
 * (`Rocket.Update`, Rocket.cs:202-226), so a spent bottle really does look spent. The client keeps
 * the clamped fade values and the hidden cork sprite for as long as the entity lives, and gates
 * the blast ring -- the one channel the original does switch off, `BlasterTNT.FixedUpdate`
 * :163-169 -- on the descriptor's own window.
 */

import type { ActivationDescriptor, PartTexture, PartTextureSet } from "../atlas";
import type { DrawEntity } from "@/schema/types";

/** One live activation of one entity. Mutable: it carries the frame state the pose reads. */
export interface ActivationRun {
  /** The part's manifest descriptor; kept so the per-sprite reads need no second lookup. */
  descriptor: ActivationDescriptor;
  /** The part's texture, for the launched cork's own sprite. */
  texture: PartTexture;
  /** Animation-clock seconds at the switch's rising edge. */
  startedAt: number;
  /** Seconds the whole run lasts (the descriptor's own window). */
  seconds: number;
  /** Seconds since the edge, refreshed once per frame. */
  elapsed: number;
  /** World point and velocity of the part at the edge: the blast centre, the cork's launch. */
  originX: number;
  originY: number;
  velocityX: number;
  velocityY: number;
  /** The part's world yaw at the edge: the cork leaves along its own -X. */
  yaw: number;
  /** Current jitter offset of the jittered node, world units, part-local (0 once it is over). */
  jitterX: number;
  jitterY: number;
  /** The launched cork's world position and spin, from `start`. */
  launched: boolean;
  corkX: number;
  corkY: number;
  corkVelocityX: number;
  corkVelocityY: number;
  corkAngle: number;
  /** The blast ring: its radius, its own drift and the fixed-step remainder. */
  ringRadius: number;
  ringVelocity: number;
  ringX: number;
  ringY: number;
  ringVelocityX: number;
  ringVelocityY: number;
  ringStep: number;
}

export interface ActivationState {
  /** entityId -> its live run; a fresh rising edge restarts it. */
  runs: Map<number, ActivationRun>;
  /** Entities whose last snapshot had the switch bit set, so a held switch fires once. */
  held: Set<number>;
}

/** The per-sprite reads one activation imposes on the part's own art. */
export interface ActivationSpriteOverride {
  /** Alpha multiplier, 1 when the sprite has no fade leg. */
  alpha: number;
  /** Offset of the sprite in the part's own frame, world units (the ignition jitter). */
  offsetX: number;
  offsetY: number;
  /** False for the cork while it flies: the overlay draws it instead. */
  visible: boolean;
}

/** One sprite drawn in the world instead of the part's frame: the flying cork, the blast ring. */
export interface ActivationOverlay {
  atlas: string;
  /** Source rect in atlas pixels. */
  x: number;
  y: number;
  w: number;
  h: number;
  /** World centre, world size and world rotation (radians, counter-clockwise, +y up). */
  worldX: number;
  worldY: number;
  worldW: number;
  worldH: number;
  rotation: number;
  alpha: number;
  /** True for the blast ring, which the part's own art covers. */
  behind: boolean;
}

export function createActivationState(): ActivationState {
  return { runs: new Map(), held: new Set() };
}

export function resetActivation(state: ActivationState): void {
  state.runs.clear();
  state.held.clear();
}

/**
 * Records the switch edge of every entity in one snapshot. Called once per decoded snapshot, not
 * per rendered frame (see the module note). Only parts whose manifest carries an activation
 * descriptor start a run, so a fan's or a rocket's ordinary switch costs nothing.
 */
export function noteActivationEdges(
  state: ActivationState,
  entities: readonly DrawEntity[],
  textures: PartTextureSet | null,
  now: number,
): void {
  for (const entity of entities) {
    if (entity.bodyId === 0) continue;
    if (!entity.active) {
      state.held.delete(entity.entityId);
      continue;
    }
    const texture = textures?.parts.get(entity.partTypeId);
    const descriptor = texture?.activation;
    if (!texture || !descriptor) continue;
    if (state.held.has(entity.entityId)) continue;
    state.held.add(entity.entityId);
    state.runs.set(entity.entityId, {
      descriptor,
      texture,
      startedAt: now,
      seconds: descriptor.seconds,
      elapsed: 0,
      originX: entity.x,
      originY: entity.y,
      velocityX: entity.vx,
      velocityY: entity.vy,
      yaw: entity.yaw,
      jitterX: 0,
      jitterY: 0,
      launched: false,
      corkX: 0,
      corkY: 0,
      corkVelocityX: 0,
      corkVelocityY: 0,
      corkAngle: 0,
      ringRadius: descriptor.ring?.startRadius ?? 0,
      ringVelocity: descriptor.ring?.radiusVelocity ?? 0,
      ringX: entity.x,
      ringY: entity.y,
      ringVelocityX: entity.vx,
      ringVelocityY: entity.vy,
      ringStep: 0,
    });
  }
}

/**
 * Advances every live run by `dtSeconds` and drops the ones whose entity left the snapshot. Runs
 * do not expire (see the module note). The jitter is re-rolled every frame, the way
 * `Rocket.FixedUpdate` re-rolls it every FixedUpdate, and the ring is integrated in the
 * original's own fixed 0.02 s steps.
 */
export function stepActivations(
  state: ActivationState,
  live: ReadonlySet<number>,
  dtSeconds: number,
  random: () => number,
): void {
  if (state.runs.size === 0) return;
  for (const [entityId, run] of state.runs) {
    if (!live.has(entityId)) {
      state.runs.delete(entityId);
      continue;
    }
    run.elapsed += dtSeconds;
    if (dtSeconds <= 0) continue;
    const jitter = run.descriptor.jitter;
    if (jitter && run.elapsed < jitter.seconds) {
      // `Random.insideUnitCircle * 0.1`: a uniform point in the disc, and the whole node moves
      // with it, so every jittered sprite takes the same offset.
      const angle = random() * Math.PI * 2;
      const radius = Math.sqrt(random()) * jitter.radius;
      run.jitterX = Math.cos(angle) * radius;
      run.jitterY = Math.sin(angle) * radius;
    } else {
      run.jitterX = 0;
      run.jitterY = 0;
    }
    const launch = run.descriptor.launch;
    if (launch) {
      const since = run.elapsed - launch.start;
      if (since > 0) {
        if (!run.launched) {
          run.launched = true;
          // The cork's own offset in the part's frame, rotated into the world by the edge's yaw:
          // `transform.parent = base.transform.parent` keeps its world position (`Rocket.cs:258`).
          const sprite = run.texture.sprites[launch.sprite];
          const offsetX = sprite ? sprite.cx : 0;
          const offsetY = sprite ? sprite.cy : 0;
          run.corkX = run.originX + Math.cos(run.yaw) * offsetX - Math.sin(run.yaw) * offsetY;
          run.corkY = run.originY + Math.sin(run.yaw) * offsetX + Math.cos(run.yaw) * offsetY;
          run.corkVelocityX = -launch.speed * Math.cos(run.yaw);
          run.corkVelocityY = -launch.speed * Math.sin(run.yaw);
        }
        // `Cork.Update`: `position += dt * (velocity + dt * 9.81 * down)` -- the velocity is never
        // integrated, so gravity adds `dt^2 * 9.81` per frame (Cork.cs:24-26).
        run.corkX += dtSeconds * run.corkVelocityX;
        run.corkY += dtSeconds * (run.corkVelocityY - dtSeconds * 9.81);
        run.corkAngle += (dtSeconds * launch.spinDegreesPerSecond * Math.PI) / 180;
      }
    }
    const ring = run.descriptor.ring;
    if (ring && run.elapsed < run.seconds) {
      // `BlasterInfo.Update` (BlasterTNT.cs:45-51) runs once per FixedUpdate, i.e. every
      // `DeltaTime` = 0.02 s; the frame's own dt only feeds the remainder.
      run.ringStep += dtSeconds;
      while (run.ringStep >= ring.stepSeconds) {
        run.ringStep -= ring.stepSeconds;
        run.ringVelocity *= Math.max(1 - ring.radiusDrag * ring.stepSeconds, 0);
        run.ringRadius += run.ringVelocity * ring.stepSeconds;
        run.ringVelocityX *= Math.max(1 - ring.radiusDrag * ring.stepSeconds, 0);
        run.ringVelocityY *= Math.max(1 - ring.radiusDrag * ring.stepSeconds, 0);
        run.ringX += run.ringVelocityX * ring.stepSeconds;
        run.ringY += run.ringVelocityY * ring.stepSeconds;
      }
    }
  }
}

/** The sprite override one run imposes, or null when the entity is not animating. */
export function activationOverride(
  run: ActivationRun | undefined,
  spriteIndex: number,
): ActivationSpriteOverride | null {
  if (!run) return null;
  const override = OVERRIDE;
  override.alpha = 1;
  override.offsetX = 0;
  override.offsetY = 0;
  override.visible = true;
  const fade = run.descriptor.fade;
  if (fade) {
    for (const leg of fade) {
      if (leg.sprite !== spriteIndex || run.elapsed < leg.start) continue;
      const progress = Math.min(1, (run.elapsed - leg.start) / leg.seconds);
      override.alpha = leg.from + (leg.to - leg.from) * progress;
    }
  }
  const jitter = run.descriptor.jitter;
  if (jitter && run.elapsed < jitter.seconds && jitter.sprites.includes(spriteIndex)) {
    override.offsetX = run.jitterX;
    override.offsetY = run.jitterY;
  }
  const launch = run.descriptor.launch;
  if (launch && launch.sprite === spriteIndex && run.elapsed >= launch.start) {
    override.visible = false;
  }
  return override;
}

/**
 * The world-space sprite of one run, or null: the flying cork (above the part) or the blast ring
 * (behind it). One record, reused between calls, so the renderer must consume it before asking
 * for the next entity.
 */
export function activationOverlay(run: ActivationRun | undefined): ActivationOverlay | null {
  if (!run) return null;
  const descriptor = run.descriptor;
  const launch = descriptor.launch;
  if (launch) {
    if (run.elapsed < launch.start || run.elapsed >= launch.start + launch.lifetime) return null;
    const sprite = run.texture.sprites[launch.sprite];
    if (!sprite || !run.launched) return null;
    const overlay = OVERLAY;
    overlay.atlas = sprite.atlas;
    overlay.x = sprite.x;
    overlay.y = sprite.y;
    overlay.w = sprite.w;
    overlay.h = sprite.h;
    overlay.worldX = run.corkX;
    overlay.worldY = run.corkY;
    overlay.worldW = sprite.sx;
    overlay.worldH = sprite.sy;
    overlay.rotation = run.corkAngle + sprite.rot;
    overlay.alpha = 1;
    overlay.behind = false;
    return overlay;
  }
  const ring = descriptor.ring;
  if (ring && run.elapsed < run.seconds) {
    const overlay = OVERLAY;
    overlay.atlas = ring.atlas;
    overlay.x = ring.x;
    overlay.y = ring.y;
    overlay.w = ring.w;
    overlay.h = ring.h;
    overlay.worldX = run.ringX;
    overlay.worldY = run.ringY;
    overlay.worldW = 2 * run.ringRadius;
    overlay.worldH = 2 * run.ringRadius;
    overlay.rotation = 0;
    // `min(64 / radius^2, 0.25)` (BlasterTNT.cs:227).
    overlay.alpha = Math.min(ring.alphaNumerator / (run.ringRadius * run.ringRadius), ring.alphaCap);
    overlay.behind = true;
    return overlay;
  }
  return null;
}

// Reused between calls: the pose and overlay paths run per sprite and per entity per frame, and
// the renderer consumes each record before the next call (the same contract as `poseFor`).
const OVERRIDE: ActivationSpriteOverride = { alpha: 1, offsetX: 0, offsetY: 0, visible: true };
const OVERLAY: ActivationOverlay = {
  atlas: "",
  x: 0,
  y: 0,
  w: 0,
  h: 0,
  worldX: 0,
  worldY: 0,
  worldW: 0,
  worldH: 0,
  rotation: 0,
  alpha: 1,
  behind: true,
};
