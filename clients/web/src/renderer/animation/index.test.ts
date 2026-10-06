import { describe, expect, it } from "vitest";
import type { DrawEntity, PartContentDocument } from "@/schema/types";
import type { AnimationFrame, PartSprite, PartTextureSet } from "../atlas";
import { createAnimationState, poseFor, resetAnimations, updateAnimations, type AnimationState } from "./index";

/** 60 Hz steps: one second of animation is 60 updates of this size. */
const STEP = 1 / 60;

const content: PartContentDocument = {
  format: "pigforge.part-content",
  schemaVersion: 1,
  contentVersion: "t",
  physics: { maximumAngularSpeed: 7, damping: { linear: 0.2, angular: 0.05 } },

  parts: [
    { partTypeId: 1, name: "block", mode: "dynamic", mass: 1, shapes: [{ kind: "box", halfExtents: [0.5, 0.5, 0.5] }] },
    { partTypeId: 8, name: "engine", mode: "dynamic", mass: 2, capabilities: { motor: { thrustPerTick: 2.2, directionX: 1 }, activation: "toggle" }, shapes: [{ kind: "box", halfExtents: [0.5, 0.5, 0.5] }] },
  ],
};

const blade: PartSprite = { atlas: "A.png", x: 10, y: 20, w: 40, h: 100, cx: 0, cy: 0, sx: 1, sy: 2, rot: 0, rotates: true, spin: { axis: "x", maxDegreesPerSecond: 1700 } };

/** Every pig clip as a single frame with its own source rect, so a pose reveals the clip. */
const frame = (x: number, seconds = 0.1): AnimationFrame => ({ atlas: "A.png", x, y: 289, w: 85, h: 48, cx: 0, cy: -0.1468, sx: 0.8854, sy: 0.5, rot: 0, seconds });
const clipOf = (x: number) => ({ loop: false, frames: [frame(x)] });
const face: PartSprite = {
  atlas: "A.png", x: 587, y: 289, w: 85, h: 48, cx: 0, cy: -0.1468, sx: 0.8854, sy: 0.5, rot: 0, rotates: false,
  clips: {
    Normal: clipOf(587),
    Blink: { loop: false, frames: [frame(1358), frame(587)] },
    Grin: clipOf(111),
    FearfulGrin: clipOf(222),
    Fear_1: clipOf(333),
    Fear_2: clipOf(444),
    Hit: clipOf(555),
  },
};
const expression = { speedFunRatio: 0.15, speedFearfulRatio: 0.3, speedFearRatio: 0.5, speedReference: 20, hitDeltaV: 5, fallFearThreshold: 3 };

const textures: PartTextureSet = {
  atlases: new Map(),
  parts: new Map([
    [11, { bbox: [1, 2], sprites: [blade] }],
    [4, { bbox: [1, 1], expression, sprites: [face] }],
  ]),
};

const entity = (partTypeId: number, overrides: Partial<DrawEntity> = {}): DrawEntity => ({
  entityId: 1, partTypeId, x: 0, y: 0, yaw: 0, scale: 1, vx: 0, vy: 0, bodyId: 9, active: false, ...overrides,
});

/** Runs `seconds` of animation and returns the pose the renderer would draw for sprite 0. */
function run(state: AnimationState, entities: DrawEntity[], seconds: number): PartSprite {
  for (let elapsed = 0; elapsed < seconds; elapsed += STEP) {
    updateAnimations(state, entities, content, textures, STEP);
  }
  return poseFor(state, entities[0], 0, entities[0].partTypeId === 11 ? blade : face).sprite;
}

describe("updateAnimations spin", () => {
  it("compresses a blade while its switch is on and leaves it alone when frozen", () => {
    const state = createAnimationState();
    const fan = entity(11, { active: true });
    // 45 degrees in, |cos| = 0.7071: the height alone shrinks and the manifest sprite shows.
    updateAnimations(state, [fan], content, textures, 45 / 1700);
    const spinning = poseFor(state, fan, 0, blade);
    expect(spinning.scaleX).toBe(1);
    expect(spinning.scaleY).toBeCloseTo(0.7071);
    expect(spinning.sprite).toBe(blade);

    const frozen = createAnimationState();
    updateAnimations(frozen, [fan], content, textures, 0);
    expect(poseFor(frozen, fan, 0, blade).scaleY).toBe(1);
  });

  it("releases an inactive blade to the original's jolt angle", () => {
    const state = createAnimationState();
    const on = entity(11, { active: true });
    const off = { ...on, active: false };
    updateAnimations(state, [on], content, textures, 0.02);
    updateAnimations(state, [off], content, textures, 0.02);
    expect(poseFor(state, off, 0, blade).scaleY).toBeCloseTo(Math.abs(Math.cos((292.3 * Math.PI) / 180)));
  });

  it("keeps no state for a preview part and sweeps a dropped entity", () => {
    const state = createAnimationState();
    updateAnimations(state, [{ ...entity(11, { active: true }), bodyId: 0 }], content, textures, 0.1);
    expect(state.spins.size).toBe(0);
    updateAnimations(state, [entity(11, { active: true })], content, textures, 0.1);
    expect(state.spins.size).toBe(1);
    updateAnimations(state, [], content, textures, 0.1);
    expect(state.spins.size).toBe(0);
    expect(state.frames.size).toBe(0);
  });

  it("does nothing without a texture manifest, like a clean checkout", () => {
    const state = createAnimationState();
    const fan = entity(11, { active: true });
    updateAnimations(state, [fan], content, null, 0.1);
    expect(state.spins.size).toBe(0);
    expect(state.frames.size).toBe(0);
  });

  it("clears every phase on reset", () => {
    const state = createAnimationState();
    updateAnimations(state, [entity(11, { active: true }), entity(4, { entityId: 2 })], content, textures, 0.1);
    // One spin runtime for the fan and one frame runtime (pig) for the second entity.
    expect(state.spins.size).toBe(1);
    expect(state.frames.size).toBe(1);
    resetAnimations(state);
    expect(state.spins.size).toBe(0);
    expect(state.frames.size).toBe(0);
  });
});

describe("updateAnimations frames and expression", () => {
  it("blinks once the idle timer rolls out and rests back on the normal art", () => {
    const state = createAnimationState(() => 0); // blinkTimer = 1.5 s exactly
    const pig = entity(4);
    expect(run(state, [pig], 1.4).x).toBe(587);
    expect(run(state, [pig], 0.2).x).toBe(1358);
    // The clip's last frame is the normal art, so a finished blink clears itself.
    expect(run(state, [pig], 0.2).x).toBe(587);
  });

  it("replays the blink from its first frame on every roll", () => {
    const state = createAnimationState(() => 0); // one roll every 1.5 s exactly
    const pig = entity(4);
    const seen: number[] = [];
    let previous = poseFor(state, pig, 0, face).sprite.x;
    // Five seconds of stepping covers three rolls (1.5 s apart). Each one must close the eyes
    // again (1358) and settle back on the normal art (587); a clip that only ever plays once
    // would show just the first pair.
    for (let step = 0; step < 300; step += 1) {
      updateAnimations(state, [pig], content, textures, STEP);
      const x = poseFor(state, pig, 0, face).sprite.x;
      if (x !== previous) {
        seen.push(x);
        previous = x;
      }
    }
    expect(seen).toEqual([1358, 587, 1358, 587, 1358, 587]);
  });

  it("plays Hit on a single-frame speed jump and holds it for a second", () => {
    const state = createAnimationState(() => 1);
    const pig = entity(4);
    run(state, [pig], 0.5);
    expect(run(state, [{ ...pig, vx: 12 }], 0.1).x).toBe(555);
    // Inside the hold nothing re-selects, so the face stays on Hit even at Grin speed.
    expect(run(state, [{ ...pig, vx: 12 }], 0.5).x).toBe(555);
    // After the second the face is handed back to the idle art, not left on the hit frame.
    expect(run(state, [{ ...pig, vx: 12 }], 0.6).x).toBe(587);
  });

  it("bands by the thrust and mass of the body the pig rides on", () => {
    const state = createAnimationState(() => 1);
    // Two 2.2-impulse motors on 4 kg (2 kg each): vRef = 60 * 4.4 / 4 = 66 m/s, so Grin starts
    // above 9.9 m/s and Fear_1 above 33 m/s.
    const pig = entity(4, { entityId: 1, bodyId: 7, vx: 11 });
    const motors = [entity(8, { entityId: 2, bodyId: 7, active: true }), entity(8, { entityId: 3, bodyId: 7, active: true })];
    expect(run(state, [pig, ...motors], 0.2).x).toBe(111);
    // The same speed without an active motor falls back to the manifest's 20 m/s reference,
    // where 11 m/s is already past the Fear ratio.
    const idle = createAnimationState(() => 1);
    expect(run(idle, [pig], 0.2).x).toBe(333);
  });

  it("plays Fear_2 while falling and leaves the normal art on the ground", () => {
    const state = createAnimationState(() => 1);
    const pig = entity(4);
    expect(run(state, [{ ...pig, vy: -4 }], 0.2).x).toBe(444);
    expect(run(state, [pig], 2).x).toBe(587);
  });

  it("leaves a sprite without animation descriptors a pass-through pose", () => {
    const state = createAnimationState();
    const plain = entity(1);
    updateAnimations(state, [plain], content, textures, 0.1);
    const pose = poseFor(state, plain, 0, blade);
    expect(pose).toEqual({
      sprite: blade,
      rot: 0,
      scaleX: 1,
      scaleY: 1,
      offsetX: 0,
      offsetY: 0,
      alpha: 1,
      visible: true,
    });
  });
});
