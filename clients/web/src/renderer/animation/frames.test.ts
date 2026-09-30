import { describe, expect, it } from "vitest";
import type { AnimationFrame, ExpressionDescriptor, PartSprite, SpriteClips } from "../atlas";
import {
  BLINK_MAX_SECONDS,
  BLINK_MIN_SECONDS,
  EXPRESSION_MIN_SECONDS,
  HIT_SECONDS,
  createClipPlayer,
  createExpressionState,
  playClip,
  speedReference,
  stepClip,
  stepExpression,
} from "./frames";

function frame(x: number, seconds: number, overrides: Partial<AnimationFrame> = {}): AnimationFrame {
  return { atlas: "A.png", x, y: 0, w: 10, h: 10, cx: 0, cy: 0, sx: 1, sy: 1, rot: 0, seconds, ...overrides };
}

function sprite(overrides: Partial<PartSprite> = {}): PartSprite {
  return { atlas: "A.png", x: 0, y: 0, w: 10, h: 10, cx: 0, cy: 0, sx: 1, sy: 1, rot: 0, rotates: false, ...overrides };
}

/** The manifest's pig thresholds; `bands` relaxes the hit detector so a test can jump bands. */
const thresholds: ExpressionDescriptor = {
  speedFunRatio: 0.15,
  speedFearfulRatio: 0.3,
  speedFearRatio: 0.5,
  speedReference: 20,
  hitDeltaV: 5,
  fallFearThreshold: 3,
};
const bands: ExpressionDescriptor = { ...thresholds, hitDeltaV: 1000 };

describe("clip player", () => {
  it("opens on the Normal clip, or the first clip when there is no Normal", () => {
    const normal = createClipPlayer(sprite(), {
      Blink: { loop: false, frames: [frame(1, 0.25)] },
      Normal: { loop: false, frames: [frame(2, 0.25)] },
    });
    expect(normal.clip).toBe("Normal");
    expect(normal.pose.x).toBe(2);
    const fallback = createClipPlayer(sprite(), {
      Idle: { loop: false, frames: [frame(5, 0.25)] },
      Walk: { loop: true, frames: [frame(6, 0.25)] },
    });
    expect(fallback.clip).toBe("Idle");
    expect(fallback.pose.x).toBe(5);
  });

  it("holds a frame until its own duration has accumulated, then advances in place", () => {
    const player = createClipPlayer(sprite(), { Walk: { loop: true, frames: [frame(0, 0.25), frame(1, 0.5)] } });
    const pose = player.pose;
    expect(player.pose.x).toBe(0);
    stepClip(player, 0.2);
    expect(player.frame).toBe(0);
    expect(player.pose).toBe(pose);
    stepClip(player, 0.05);
    expect(player.frame).toBe(1);
    expect(player.pose.x).toBe(1);
    expect(player.pose).toBe(pose);
    expect(player.elapsed).toBe(0);
    stepClip(player, 0.25);
    expect(player.frame).toBe(1);
    stepClip(player, 0.25);
    expect(player.frame).toBe(0);
    expect(player.elapsed).toBe(0);
    stepClip(player, 0.75);
    expect(player.frame).toBe(0);
    expect(player.elapsed).toBe(0);

    const single = createClipPlayer(sprite(), { Idle: { loop: true, frames: [frame(8, 0.25)] } });
    const singlePose = single.pose;
    stepClip(single, 1);
    expect(single.frame).toBe(0);
    expect(single.pose.x).toBe(8);
    expect(single.pose).toBe(singlePose);
  });

  it("stops a non-looping clip on its last frame and never moves again", () => {
    const player = createClipPlayer(sprite(), { Hit: { loop: false, frames: [frame(3, 0.25), frame(4, 0.25)] } });
    const pose = player.pose;
    stepClip(player, 0.25);
    expect(player.frame).toBe(1);
    stepClip(player, 0.25);
    expect(player.frame).toBe(1);
    stepClip(player, 5);
    expect(player.frame).toBe(1);
    expect(player.elapsed).toBe(0);
    expect(player.pose.x).toBe(4);
    expect(player.pose).toBe(pose);
  });

  it("ignores a clip name the sprite does not have", () => {
    const player = createClipPlayer(sprite(), {
      Normal: { loop: false, frames: [frame(3, 0.25), frame(4, 0.25)] },
      Blink: { loop: false, frames: [frame(9, 0.25)] },
    });
    stepClip(player, 0.25);
    playClip(player, "Nope");
    expect(player.clip).toBe("Normal");
    expect(player.frame).toBe(1);
    expect(player.pose.x).toBe(4);
    playClip(player, "Blink");
    expect(player.clip).toBe("Blink");
    expect(player.frame).toBe(0);
    expect(player.elapsed).toBe(0);
    expect(player.pose.x).toBe(9);
    playClip(player, "Nope");
    expect(player.clip).toBe("Blink");
  });

  it("merges the frame rect over the static sprite, keeping its rotates flag only", () => {
    const clips: SpriteClips = {
      Normal: {
        loop: false,
        frames: [frame(7, 0.1, { w: 20, h: 30, cx: 0.5, cy: -0.25, sx: 2, sy: 3, rot: 0.5 })],
      },
    };
    const staticSprite = sprite({ rotates: true, clips, spin: { axis: "x", maxDegreesPerSecond: 1700 } });
    const player = createClipPlayer(staticSprite, clips);
    expect(player.pose).not.toBe(staticSprite);
    expect(player.pose).toMatchObject({ atlas: "A.png", x: 7, w: 20, h: 30, cx: 0.5, cy: -0.25, sx: 2, sy: 3, rot: 0.5 });
    expect(player.pose.rotates).toBe(true);
    expect(Object.hasOwn(player.pose, "clips")).toBe(false);
    expect(Object.hasOwn(player.pose, "spin")).toBe(false);
  });

  it("stands on the static sprite when the sprite has no clips", () => {
    const player = createClipPlayer(sprite({ x: 42, rotates: true }), {});
    expect(player.clip).toBe("");
    expect(player.pose.x).toBe(42);
    expect(player.pose.rotates).toBe(true);
    expect(Object.hasOwn(player.pose, "clips")).toBe(false);
    stepClip(player, 1);
    expect(player.frame).toBe(0);
    expect(player.pose.x).toBe(42);
  });

  it("lets a Blink clip whose last frame repeats the Normal art clear itself", () => {
    const normalFrame = frame(100, 0.1);
    const clips: SpriteClips = {
      Normal: { loop: false, frames: [normalFrame] },
      Blink: { loop: false, frames: [frame(200, 0.1), normalFrame] },
    };
    const player = createClipPlayer(sprite(), clips);
    playClip(player, "Blink");
    expect(player.pose.x).toBe(200);
    stepClip(player, 0.1);
    expect(player.pose.x).toBe(100);
    stepClip(player, 1);
    expect(player.pose.x).toBe(100);
  });
});

describe("speedReference", () => {
  it("derives vRef from thrust and mass, falling back without an active motor", () => {
    expect(speedReference(4.4, 3.6, 20)).toBeCloseTo(73.33, 2);
    expect(speedReference(0, 3.6, 20)).toBe(20);
    expect(speedReference(4.4, 0, 20)).toBe(20);
  });
});

describe("clip names", () => {
  it("requests a clip the pig's sprite actually carries", () => {
    const normalFrame = frame(100, 0.25);
    const player = createClipPlayer(sprite(), {
      Normal: { loop: false, frames: [normalFrame] },
      Blink: { loop: false, frames: [frame(200, 0.25), normalFrame] },
    });
    const state = createExpressionState(() => 0);
    stepExpression(state, thresholds, 0, 0, 100, 0.25);
    stepExpression(state, thresholds, 0, 0, 100, BLINK_MIN_SECONDS);
    playClip(player, state.playing);
    expect(player.clip).toBe("Blink");
    expect(player.pose.x).toBe(200);
  });
});

describe("expression machine", () => {
  it("never reads the first frame of an entity as a hit", () => {
    const state = createExpressionState(() => 1);
    expect(stepExpression(state, thresholds, 100, -100, 100, 0.25)).toBe("Normal");
    expect(state.started).toBe(true);
    expect(state.hitRemaining).toBe(0);
    expect(state.lastSpeed).toBeCloseTo(Math.hypot(100, 100));
  });

  it("selects the band strictly above each ratio of vRef", () => {
    const reference = 100;
    const funEdge = thresholds.speedFunRatio * reference;
    const fearfulEdge = thresholds.speedFearfulRatio * reference;
    const fearEdge = thresholds.speedFearRatio * reference;
    const cases: ReadonlyArray<readonly [number, number, string]> = [
      [0, 0, "Normal"],
      [funEdge, 0, "Normal"],
      [funEdge + 1, 0, "Grin"],
      [fearfulEdge, 0, "Grin"],
      [fearfulEdge + 1, 0, "FearfulGrin"],
      [fearEdge, 0, "FearfulGrin"],
      [fearEdge + 1, 0, "Fear_1"],
      [0, 40, "Fear_1"],
    ];
    for (const [vx, vy, expected] of cases) {
      const state = createExpressionState(() => 1);
      stepExpression(state, bands, 0, 0, reference, 0.25);
      stepExpression(state, bands, vx, vy, reference, 0.25);
      expect([vx, vy, state.expression, state.playing]).toEqual([vx, vy, expected, expected]);
    }
  });

  it("blinks on the injected roll, and only from an idle face", () => {
    const fastest = createExpressionState(() => 0);
    expect(fastest.blinkTimer).toBe(BLINK_MIN_SECONDS);
    stepExpression(fastest, thresholds, 0, 0, 100, 0.25);
    stepExpression(fastest, thresholds, 0, 0, 100, BLINK_MIN_SECONDS);
    expect(fastest.playing).toBe("Blink");
    expect(fastest.expression).toBe("Normal");
    expect(fastest.blinkTimer).toBe(BLINK_MIN_SECONDS);

    const slowest = createExpressionState(() => 1);
    expect(slowest.blinkTimer).toBe(BLINK_MAX_SECONDS);
    stepExpression(slowest, thresholds, 0, 0, 100, 0.25);
    for (let i = 0; i < 15; i++) stepExpression(slowest, thresholds, 0, 0, 100, 0.25);
    expect(slowest.playing).toBe("Normal");
    stepExpression(slowest, thresholds, 0, 0, 100, 0.25);
    expect(slowest.playing).toBe("Blink");
    expect(slowest.blinkTimer).toBe(BLINK_MAX_SECONDS);

    const distracted = createExpressionState(() => 0);
    stepExpression(distracted, bands, 0, 0, 100, 0.25);
    stepExpression(distracted, bands, 60, 0, 100, 0.25);
    expect(distracted.expression).toBe("Fear_1");
    for (let i = 0; i < 40; i++) stepExpression(distracted, bands, 60, 0, 100, 0.25);
    expect(distracted.playing).toBe("Fear_1");
    expect(distracted.blinkTimer).toBeLessThan(0);
    stepExpression(distracted, bands, 0, 0, 100, 0.25);
    expect(distracted.expression).toBe("Normal");
    stepExpression(distracted, bands, 0, 0, 100, 0.25);
    expect(distracted.playing).toBe("Blink");
  });

  it("plays Hit for one second on a single-frame speed jump", () => {
    const state = createExpressionState(() => 1);
    stepExpression(state, thresholds, 10, 0, 100, 0.25);
    stepExpression(state, thresholds, 14, 0, 100, 0.25);
    expect(state.playing).toBe("Normal");
    stepExpression(state, thresholds, 9, 0, 100, 0.25);
    expect(state.playing).toBe("Normal");
    stepExpression(state, thresholds, 31, 0, 100, 0.25);
    expect(state.playing).toBe("Hit");
    expect(state.hitRemaining).toBe(HIT_SECONDS);
    expect(state.expression).toBe("Normal");
    for (let i = 0; i < 4; i++) stepExpression(state, thresholds, 31, 0, 100, 0.25);
    // `AnimationCoroutine` waits its second and then calls `SetExpression(Normal)`
    // (Pig.cs:735-741), so the hold ends by handing the face back to the idle clip — and that
    // call also restarts the expression guard, exactly like any other selection.
    expect(state.playing).toBe("Normal");
    expect(state.hitRemaining).toBe(0);
    stepExpression(state, thresholds, 31, 0, 100, 0.25);
    expect(state.playing).toBe("Normal");
    for (let i = 0; i < 4; i++) stepExpression(state, thresholds, 31, 0, 100, 0.25);
    expect(state.playing).toBe("FearfulGrin");
  });

  it("does not fire Hit on repeated frames with an unchanged speed", () => {
    const state = createExpressionState(() => 1);
    stepExpression(state, thresholds, 25, 0, 100, 0.25);
    for (let i = 0; i < 8; i++) stepExpression(state, thresholds, 25, 0, 100, 0.25);
    expect(state.playing).not.toBe("Hit");
    expect(state.hitRemaining).toBe(0);
  });

  it("holds a changed expression for a second before re-selecting", () => {
    const state = createExpressionState(() => 1);
    stepExpression(state, bands, 10, 0, 100, 0.25);
    stepExpression(state, bands, 16, 0, 100, 0.25);
    expect(state.expression).toBe("Grin");
    expect(state.setCooldown).toBeGreaterThan(0);
    expect(state.setCooldown).toBeLessThanOrEqual(EXPRESSION_MIN_SECONDS);
    // A jump into the Fear band; only the guard keeps the face on Grin for the next second.
    for (let i = 0; i < 3; i++) {
      stepExpression(state, bands, 80, 0, 100, 0.25);
      expect(state.expression).toBe("Grin");
    }
    stepExpression(state, bands, 80, 0, 100, 0.25);
    expect(state.expression).toBe("Fear_1");
    expect(state.playing).toBe("Fear_1");
  });

  it("shows Fear_2 when the body falls past the threshold", () => {
    const state = createExpressionState(() => 1);
    stepExpression(state, thresholds, 0, 0, 100, 0.25);
    stepExpression(state, thresholds, 0, -3, 100, 0.25);
    expect(state.playing).toBe("Normal");
    stepExpression(state, thresholds, 0, -3.1, 100, 0.25);
    expect(state.playing).toBe("Fear_2");
    expect(state.expression).toBe("Fear_2");
  });
});
