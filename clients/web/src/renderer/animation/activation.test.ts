import { describe, expect, it } from "vitest";
import type { DrawEntity } from "@/schema/types";
import type { ActivationDescriptor, PartSprite, PartTexture, PartTextureSet } from "../atlas";
import {
  activationOverlay,
  activationOverride,
  createActivationState,
  noteActivationEdges,
  stepActivations,
  type ActivationRun,
  type ActivationState,
} from "./activation";
import {
  createAnimationState,
  noteActivationEdges as noteSnapshotEdges,
  resetAnimations,
  updateAnimations,
} from "./index";

/** 60 Hz steps: one second of animation is 60 updates of this size, like the paint loop. */
const STEP = 1 / 60;

const sprite = (overrides: Partial<PartSprite> = {}): PartSprite => ({
  atlas: "A.png",
  x: 10,
  y: 20,
  w: 100,
  h: 100,
  cx: 0,
  cy: 0,
  sx: 1,
  sy: 1,
  rot: 0,
  rotates: false,
  ...overrides,
});

/** The shipped bottle activation (part-textures.json part 25). */
const bottleActivation: ActivationDescriptor = {
  seconds: 2,
  jitter: { sprites: [0, 1, 2, 7], radius: 0.1, seconds: 1 },
  fade: [
    { sprite: 1, from: 1, to: 0, start: 0, seconds: 1 },
    { sprite: 0, from: 0, to: 1, start: 0, seconds: 1 },
    { sprite: 0, from: 1, to: 0, start: 1, seconds: 1 },
  ],
  launch: { sprite: 7, start: 1, speed: 20, spinDegreesPerSecond: 200, lifetime: 0.75 },
};

const bottleTexture: PartTexture = {
  bbox: [1, 1],
  sprites: [
    sprite(),
    sprite(),
    sprite(),
    sprite(),
    sprite(),
    sprite(),
    sprite(),
    // The cork: its own atlas rect, its own size, and its own offset in the part's frame.
    sprite({ x: 700, y: 800, w: 60, h: 60, cx: 0.2, cy: 0.3, sx: 0.5, sy: 0.5 }),
  ],
  activation: bottleActivation,
};

/** The shipped blaster activation (part-textures.json part 52); the parser drops the inner seconds. */
const blasterActivation: ActivationDescriptor = {
  seconds: 2,
  ring: {
    atlas: "Blast_Texture.png",
    x: 0,
    y: 0,
    w: 2048,
    h: 2048,
    startRadius: 0.5,
    radiusVelocity: 160,
    radiusDrag: 0.2,
    stepSeconds: 0.02,
    alphaNumerator: 64,
    alphaCap: 0.25,
  },
};

const blasterTexture: PartTexture = { bbox: [1, 1], sprites: [sprite()], activation: blasterActivation };

const textures: PartTextureSet = {
  atlases: new Map(),
  parts: new Map([
    [25, bottleTexture],
    [52, blasterTexture],
  ]),
};

const entity = (partTypeId: number, overrides: Partial<DrawEntity> = {}): DrawEntity => ({
  entityId: 1,
  partTypeId,
  x: 0,
  y: 0,
  yaw: 0,
  scale: 1,
  vx: 0,
  vy: 0,
  bodyId: 9,
  active: false,
  ...overrides,
});

/** A snapshot entity with its switch up: a trigger part spends its press in one snapshot. */
const pressed = (partTypeId: number, overrides: Partial<DrawEntity> = {}): DrawEntity =>
  entity(partTypeId, { active: true, ...overrides });

/** The live set `stepActivations` sweeps against; entity 1 is the subject of every case below. */
const LIVE = new Set([1]);

const runOf = (state: ActivationState): ActivationRun | undefined => state.runs.get(1);

/** A `random` that walks a fixed list, so an ignition roll is reproducible. */
function rolls(values: number[]): () => number {
  let index = 0;
  return () => values[index++ % values.length];
}

/** Steps `seconds` in 60 Hz frames, exactly as the paint loop does. */
function advance(state: ActivationState, seconds: number, random: () => number = () => 0.5): void {
  const frames = Math.round(seconds / STEP);
  for (let frame = 0; frame < frames; frame += 1) {
    stepActivations(state, LIVE, STEP, random);
  }
}

describe("noteActivationEdges", () => {
  it("starts a run on the switch's rising edge, carrying the snapshot's own pose", () => {
    const state = createActivationState();
    noteActivationEdges(state, [pressed(25, { x: 3, y: 4, vx: 1.5, vy: -2.5, yaw: 0.7 })], textures, 7);
    const run = runOf(state)!;
    expect(run.startedAt).toBe(7);
    expect(run.seconds).toBe(2);
    expect(run.descriptor).toBe(bottleActivation);
    expect(run.texture).toBe(bottleTexture);
    expect([run.originX, run.originY, run.velocityX, run.velocityY, run.yaw]).toEqual([3, 4, 1.5, -2.5, 0.7]);
    expect(run.elapsed).toBe(0);
    expect(run.launched).toBe(false);
  });

  it("does not restart a run while the switch stays held", () => {
    const state = createActivationState();
    const bottle = pressed(25);
    noteActivationEdges(state, [bottle], textures, 3);
    const started = runOf(state)!;
    advance(state, 0.5);
    noteActivationEdges(state, [bottle], textures, 9);
    expect(runOf(state)).toBe(started);
    expect(runOf(state)!.startedAt).toBe(3);
    expect(runOf(state)!.elapsed).toBeCloseTo(0.5, 6);
    expect(state.held.has(1)).toBe(true);
  });

  it("restarts the run when the switch falls and rises again", () => {
    const state = createActivationState();
    noteActivationEdges(state, [pressed(25)], textures, 1);
    const first = runOf(state)!;
    noteActivationEdges(state, [entity(25)], textures, 2);
    // The falling edge only clears the held flag: the run it started plays on.
    expect(state.held.has(1)).toBe(false);
    expect(runOf(state)).toBe(first);
    noteActivationEdges(state, [pressed(25)], textures, 3);
    expect(runOf(state)).not.toBe(first);
    expect(runOf(state)!.startedAt).toBe(3);
    expect(runOf(state)!.elapsed).toBe(0);
  });

  it("never starts a run for a preview, a part without an activation or without a manifest", () => {
    const state = createActivationState();
    noteActivationEdges(state, [pressed(25, { bodyId: 0 })], textures, 0);
    noteActivationEdges(state, [pressed(1)], textures, 0);
    noteActivationEdges(state, [pressed(25)], null, 0);
    expect(state.runs.size).toBe(0);
    expect(state.held.size).toBe(0);
  });
});

describe("activation fade", () => {
  /** The override alpha of sprite `index` `seconds` after the edge. */
  const fadeAt = (seconds: number, index: number): number => {
    const state = createActivationState();
    noteActivationEdges(state, [pressed(25)], textures, 0);
    advance(state, seconds);
    return activationOverride(runOf(state), index)!.alpha;
  };

  it("ramps each content sprite on the manifest's own legs", () => {
    // Sprite 1 has one leg, 1 -> 0 over the first second.
    expect(fadeAt(0.25, 1)).toBeCloseTo(0.75, 6);
    expect(fadeAt(0.5, 1)).toBeCloseTo(0.5, 6);
    expect(fadeAt(1, 1)).toBeCloseTo(0, 6);
    // Sprite 0 goes 0 -> 1 over the first second, then 1 -> 0 over the second: a leg that has not
    // started leaves the earlier value alone (0.5 s), and a later one overwrites it (1.5 s).
    expect(fadeAt(0.25, 0)).toBeCloseTo(0.25, 6);
    expect(fadeAt(0.5, 0)).toBeCloseTo(0.5, 6);
    expect(fadeAt(1, 0)).toBeCloseTo(1, 6);
    expect(fadeAt(1.5, 0)).toBeCloseTo(0.5, 6);
  });

  it("leaves a sprite without a fade leg at full opacity", () => {
    expect(fadeAt(0.5, 2)).toBe(1);
    expect(fadeAt(1.5, 2)).toBe(1);
  });
});

describe("activation jitter", () => {
  it("gives every jittered sprite of the part one shared offset inside the disc", () => {
    const state = createActivationState();
    noteActivationEdges(state, [pressed(25)], textures, 0);
    // One frame: angle 0, radius sqrt(0.25) * 0.1 = 0.05 world units.
    stepActivations(state, LIVE, STEP, rolls([0, 0.25]));
    const run = runOf(state)!;
    expect(Math.hypot(run.jitterX, run.jitterY)).toBeLessThanOrEqual(0.1);
    expect([run.jitterX, run.jitterY]).toEqual([0.05, 0]);
    // The descriptor lists one node, so every listed sprite takes the very same offset.
    for (const index of [0, 1, 2, 7]) {
      const jittered = activationOverride(run, index)!;
      expect([jittered.offsetX, jittered.offsetY]).toEqual([run.jitterX, run.jitterY]);
    }
    // A sprite the descriptor does not list keeps the manifest's own placement.
    const still = activationOverride(run, 3)!;
    expect([still.offsetX, still.offsetY]).toEqual([0, 0]);
  });

  it("re-rolls the offset on every frame of the ignition", () => {
    const state = createActivationState();
    noteActivationEdges(state, [pressed(25)], textures, 0);
    stepActivations(state, LIVE, STEP, rolls([0, 0.25]));
    const first = runOf(state)!.jitterX;
    // A second frame rolls again: angle pi, radius sqrt(0.5) * 0.1.
    stepActivations(state, LIVE, STEP, rolls([0.5, 0.5]));
    expect(runOf(state)!.jitterX).toBeCloseTo(-Math.sqrt(0.5) * 0.1, 9);
    expect(runOf(state)!.jitterX).not.toBeCloseTo(first, 6);
  });

  it("zeroes the offset once the ignition window is over", () => {
    const state = createActivationState();
    noteActivationEdges(state, [pressed(25)], textures, 0);
    advance(state, 1.1);
    const run = runOf(state)!;
    expect([run.jitterX, run.jitterY]).toEqual([0, 0]);
    for (const index of [0, 1, 2, 7]) {
      const over = activationOverride(run, index)!;
      expect([over.offsetX, over.offsetY]).toEqual([0, 0]);
    }
  });
});

describe("activation cork launch", () => {
  /** Advances a bottle to its launch frame with 1 ms steps, so the launch reads in isolation. */
  const launch = (overrides: Partial<DrawEntity> = {}): ActivationRun => {
    const state = createActivationState();
    noteActivationEdges(state, [pressed(25, overrides)], textures, 0);
    advance(state, 0.5);
    while (runOf(state)!.elapsed <= 1) {
      stepActivations(state, LIVE, 1 / 1000, () => 0.5);
    }
    return runOf(state)!;
  };

  it("hides the cork sprite at the launch and draws it as its own world sprite", () => {
    const before = createActivationState();
    noteActivationEdges(before, [pressed(25)], textures, 0);
    advance(before, 0.5);
    // Before the launch the cork is a sprite of the part's own frame and has no overlay.
    expect(activationOverride(runOf(before), 7)!.visible).toBe(true);
    expect(activationOverlay(runOf(before))).toBeNull();

    const run = launch();
    expect(run.launched).toBe(true);
    // The part stops drawing the cork itself; only that sprite leaves the frame.
    expect(activationOverride(run, 7)!.visible).toBe(false);
    expect(activationOverride(run, 6)!.visible).toBe(true);
    const overlay = activationOverlay(run)!;
    expect([overlay.x, overlay.y, overlay.w, overlay.h]).toEqual([700, 800, 60, 60]);
    expect([overlay.worldW, overlay.worldH]).toEqual([0.5, 0.5]);
    expect(overlay.behind).toBe(false);
  });

  it("throws the cork along the part's own -X", () => {
    // A part facing +90 degrees: -X of the part's frame is straight down in the world.
    const upward = launch({ x: 1, y: 2, yaw: Math.PI / 2 });
    expect(upward.corkVelocityX).toBeCloseTo(0, 6);
    expect(upward.corkVelocityY).toBeCloseTo(-20, 6);
    // It leaves from the part origin plus its own offset, rotated into the world by the yaw.
    expect(activationOverlay(upward)!.worldX).toBeCloseTo(0.7, 3);
    expect(activationOverlay(upward)!.worldY).toBeLessThan(2.2);

    const along = launch();
    expect(along.corkVelocityX).toBeCloseTo(-20, 6);
    expect(along.corkVelocityY).toBeCloseTo(0, 6);
  });

  it("drops the cork once its own lifetime is over, and never draws it again", () => {
    const state = createActivationState();
    noteActivationEdges(state, [pressed(25)], textures, 0);
    advance(state, 1.7);
    expect(runOf(state)!.launched).toBe(true);
    expect(activationOverlay(runOf(state))).not.toBeNull();

    advance(state, 0.1); // 1.8 s: past launch.start + lifetime (1.75)
    expect(activationOverlay(runOf(state))).toBeNull();
    // The part never draws the cork again: the original destroys the object it launched, so the
    // sprite stays out of the frame for the rest of the run.
    expect(activationOverride(runOf(state), 7)!.visible).toBe(false);
    expect(activationOverlay(undefined)).toBeNull();
  });
});

describe("activation ring", () => {
  it("grows the ring in the descriptor's fixed 0.02 s steps, carrying the frame's remainder", () => {
    const framed = createActivationState();
    noteActivationEdges(framed, [pressed(52)], textures, 0);
    stepActivations(framed, LIVE, 0.5, () => 0.5);
    // Each step multiplies the velocity by 1 - drag*step (0.996) and adds it to the radius, so
    // after n steps r = 0.5 + 796.8 * (1 - 0.996^n). One 0.5 s frame runs 24 of them: 0.5 minus
    // 24 * 0.02 leaves 0.019999999999999827, which is below stepSeconds, so the 25th step stays
    // in the remainder instead of running inside the frame.
    expect(runOf(framed)!.ringRadius).toBeCloseTo(73.57521223001905, 6);
    stepActivations(framed, LIVE, 0.02, () => 0.5);
    expect(runOf(framed)!.ringRadius).toBeCloseTo(76.47011138109897, 6);

    // 25 frames of exactly one step each land on the same radius.
    const stepped = createActivationState();
    noteActivationEdges(stepped, [pressed(52)], textures, 0);
    for (let step = 0; step < 25; step += 1) {
      stepActivations(stepped, LIVE, 0.02, () => 0.5);
    }
    expect(runOf(stepped)!.ringRadius).toBeCloseTo(76.47011138109897, 6);
    expect(runOf(framed)!.ringRadius).toBeCloseTo(runOf(stepped)!.ringRadius, 6);
  });

  it("reports the ring's radius, its 2r world size and its alpha", () => {
    const state = createActivationState();
    noteActivationEdges(state, [pressed(52)], textures, 0);
    stepActivations(state, LIVE, 0.5, () => 0.5);
    const overlay = activationOverlay(runOf(state))!;
    expect(overlay.atlas).toBe("Blast_Texture.png");
    expect([overlay.x, overlay.y, overlay.w, overlay.h]).toEqual([0, 0, 2048, 2048]);
    expect(overlay.behind).toBe(true);
    // The blast centre is where the part was at the edge; the quad is the ring's diameter wide.
    expect([overlay.worldX, overlay.worldY]).toEqual([0, 0]);
    expect([overlay.worldW, overlay.worldH]).toEqual([147.1504244600381, 147.1504244600381]);
    // radius 73.57521223001905: 64 / r^2 = 0.0118227, under the 0.25 cap.
    expect(overlay.alpha).toBeCloseTo(0.011822707007822538, 9);

    // Three steps in, the numerator is still above the cap: 64 / 10.0234^2 = 0.637.
    const early = createActivationState();
    noteActivationEdges(early, [pressed(52)], textures, 0);
    for (let step = 0; step < 3; step += 1) {
      stepActivations(early, LIVE, 0.02, () => 0.5);
    }
    expect(runOf(early)!.ringRadius).toBeCloseTo(10.0234045952, 9);
    expect(activationOverlay(runOf(early))!.alpha).toBe(0.25);
  });

  it("switches the ring off once the descriptor's own window is over", () => {
    const state = createActivationState();
    noteActivationEdges(state, [pressed(52)], textures, 0);
    // Inside the two seconds the ring is a world sprite; past them the original switches the
    // quad off (BlasterTNT.cs:163-169), so the client must stop drawing it.
    advance(state, 1.5);
    expect(activationOverlay(runOf(state))).not.toBeNull();
    advance(state, 1);
    expect(activationOverlay(runOf(state))).toBeNull();
  });
});

describe("activation lifecycle", () => {
  it("keeps a spent bottle spent after the descriptor's window", () => {
    const state = createActivationState();
    noteActivationEdges(state, [pressed(25)], textures, 0);
    advance(state, 2.5);
    // The original never restores what the cross-fade did: both content alphas end at zero and
    // the cork object was destroyed (Rocket.cs:202-226), so the part stays empty instead of
    // popping back to its built art at the end of the run.
    expect(activationOverride(runOf(state), 0)!.alpha).toBe(0);
    expect(activationOverride(runOf(state), 1)!.alpha).toBe(0);
    expect(activationOverride(runOf(state), 7)!.visible).toBe(false);
  });

  it("freezes a run, clock included, on a 0 dt frame", () => {
    const state = createAnimationState();
    const bottle = pressed(25);
    noteSnapshotEdges(state, [bottle], textures);
    updateAnimations(state, [bottle], null, textures, 0);
    expect(state.now).toBe(0);
    const run = runOf(state.activation)!;
    expect(run.elapsed).toBe(0);
    expect(run.launched).toBe(false);
    expect([run.jitterX, run.jitterY]).toEqual([0, 0]);

    // One 60 Hz frame then advances the very clock the run reads.
    updateAnimations(state, [bottle], null, textures, STEP);
    expect(state.now).toBeCloseTo(STEP, 9);
    expect(runOf(state.activation)!.elapsed).toBeCloseTo(STEP, 9);
  });

  it("sweeps a run whose entity left the snapshot", () => {
    const state = createAnimationState();
    const bottle = pressed(25);
    noteSnapshotEdges(state, [bottle], textures);
    updateAnimations(state, [bottle], null, textures, STEP);
    expect(state.activation.runs.size).toBe(1);
    updateAnimations(state, [], null, textures, STEP);
    expect(state.activation.runs.size).toBe(0);
    expect(state.live.size).toBe(0);
  });

  it("clears runs, the held flags and the clock on reset", () => {
    const state = createAnimationState();
    const bottle = pressed(25);
    noteSnapshotEdges(state, [bottle], textures);
    updateAnimations(state, [bottle], null, textures, STEP * 5);
    expect(state.activation.runs.size).toBe(1);
    expect(state.activation.held.has(1)).toBe(true);
    expect(state.now).toBeCloseTo(STEP * 5, 9);
    resetAnimations(state);
    expect(state.activation.runs.size).toBe(0);
    expect(state.activation.held.size).toBe(0);
    expect(state.now).toBe(0);
  });
});
