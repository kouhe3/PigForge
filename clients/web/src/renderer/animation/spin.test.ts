import { describe, expect, it } from "vitest";
import {
  createSpinState,
  foreshorten,
  SPIN_DECAY_SPEED_THRESHOLD,
  SPIN_FAST_DECAY_PER_SECOND,
  SPIN_RELEASE_ANGLE_DEGREES,
  SPIN_RELEASE_SPEED_DEGREES,
  SPIN_SLOW_DECAY_PER_SECOND,
  stepSpin,
} from "./spin";

/** The extracted descriptor for fans/propellers: `1000 * powerFactor + 700` at powerFactor 1. */
// FanPropeller.cs:92,108-112.
const MAX_DEGREES_PER_SECOND = 1700;

const FRAME_SECONDS = 1 / 60;

describe("createSpinState", () => {
  it("starts at rest with the switch off", () => {
    expect(createSpinState()).toEqual({ angle: 0, speed: 0, active: false });
  });
});

describe("stepSpin", () => {
  it("runs the blades at the descriptor's full speed while the switch is on", () => {
    const state = createSpinState();
    for (let frame = 0; frame < 3; frame += 1) {
      stepSpin(state, MAX_DEGREES_PER_SECOND, true, FRAME_SECONDS);
    }
    expect(state.speed).toBe(MAX_DEGREES_PER_SECOND);
    expect(state.active).toBe(true);
    // 3 frames at 1700 deg/s, still short of the wrap.
    expect(state.angle).toBeCloseTo((MAX_DEGREES_PER_SECOND * 3) / 60, 10);
  });

  it("does not spin up a part that is already off", () => {
    const state = createSpinState();
    stepSpin(state, MAX_DEGREES_PER_SECOND, false, FRAME_SECONDS);
    expect(state.speed).toBe(0);
    expect(state.angle).toBe(0);
    expect(state.active).toBe(false);
  });

  it("snaps to the release speed and angle on the falling edge", () => {
    const state = createSpinState();
    stepSpin(state, MAX_DEGREES_PER_SECOND, true, FRAME_SECONDS);
    expect(state.speed).toBe(MAX_DEGREES_PER_SECOND);

    stepSpin(state, MAX_DEGREES_PER_SECOND, false, FRAME_SECONDS);
    // FanPropeller.cs:274-275: the switch-off writes both values and applies no decay.
    expect(state.speed).toBe(SPIN_RELEASE_SPEED_DEGREES);
    expect(state.angle).toBe(SPIN_RELEASE_ANGLE_DEGREES);
    expect(state.active).toBe(false);
  });

  it("decays the release speed away from the next step on", () => {
    const state = createSpinState();
    stepSpin(state, MAX_DEGREES_PER_SECOND, true, FRAME_SECONDS);
    stepSpin(state, MAX_DEGREES_PER_SECOND, false, FRAME_SECONDS);

    stepSpin(state, MAX_DEGREES_PER_SECOND, false, FRAME_SECONDS);
    // 800 deg/s is above the threshold, so it takes the slow factor: one FixedUpdate's
    // worth of `*= 0.98f` (0.02 s) per 0.02 s.
    expect(state.speed).toBeCloseTo(SPIN_RELEASE_SPEED_DEGREES * 0.98 ** (FRAME_SECONDS / 0.02), 5);
    expect(state.speed).toBeLessThan(SPIN_RELEASE_SPEED_DEGREES);
  });

  it("uses the original's per-FixedUpdate factors as continuous decay rates", () => {
    const fast = { angle: 0, speed: SPIN_DECAY_SPEED_THRESHOLD - 1, active: false };
    const slow = { angle: 0, speed: SPIN_DECAY_SPEED_THRESHOLD, active: false };
    stepSpin(fast, MAX_DEGREES_PER_SECOND, false, FRAME_SECONDS);
    stepSpin(slow, MAX_DEGREES_PER_SECOND, false, FRAME_SECONDS);

    // FanPropeller.cs:120-127: `*= 0.9f` below 450, `*= 0.98f` at or above it.
    // The exported rates are rounded at the 7th decimal, hence the loose precision here.
    expect(fast.speed).toBeCloseTo((SPIN_DECAY_SPEED_THRESHOLD - 1) * 0.9 ** (FRAME_SECONDS / 0.02), 5);
    expect(slow.speed).toBeCloseTo(SPIN_DECAY_SPEED_THRESHOLD * 0.98 ** (FRAME_SECONDS / 0.02), 5);
    expect(SPIN_FAST_DECAY_PER_SECOND).toBeCloseTo(-Math.log(0.9) / 0.02, 5);
    expect(SPIN_SLOW_DECAY_PER_SECOND).toBeCloseTo(-Math.log(0.98) / 0.02, 5);
  });

  it("wraps the angle into (-180, 180] once the blades turn past 180", () => {
    const state = createSpinState();
    stepSpin(state, MAX_DEGREES_PER_SECOND, true, FRAME_SECONDS);
    stepSpin(state, MAX_DEGREES_PER_SECOND, false, FRAME_SECONDS);
    expect(state.angle).toBe(SPIN_RELEASE_ANGLE_DEGREES);

    stepSpin(state, MAX_DEGREES_PER_SECOND, false, FRAME_SECONDS);
    // 292.3 + 800*exp(-1.010135/60)/60 = 305.41 -> -54.59 (FanPropeller.cs:129-132).
    const advanced =
      SPIN_RELEASE_ANGLE_DEGREES +
      SPIN_RELEASE_SPEED_DEGREES * Math.exp(-SPIN_SLOW_DECAY_PER_SECOND * FRAME_SECONDS) * FRAME_SECONDS;
    expect(advanced).toBeGreaterThan(180);
    expect(state.angle).toBeCloseTo(advanced - 360, 10);
    expect(state.angle).toBeGreaterThan(-180);
    expect(state.angle).toBeLessThanOrEqual(180);
  });

  it("reaches the decay threshold in ln(1700/450)/1.010135 s", () => {
    const dt = 1 / 240;
    const state = { angle: 0, speed: MAX_DEGREES_PER_SECOND, active: false };
    let elapsed = 0;
    while (state.speed >= SPIN_DECAY_SPEED_THRESHOLD && elapsed < 10) {
      stepSpin(state, MAX_DEGREES_PER_SECOND, false, dt);
      elapsed += dt;
    }

    const expected = Math.log(MAX_DEGREES_PER_SECOND / SPIN_DECAY_SPEED_THRESHOLD) / SPIN_SLOW_DECAY_PER_SECOND;
    expect(elapsed).toBeGreaterThan(expected * 0.95);
    expect(elapsed).toBeLessThan(expected * 1.05);
  });

  it("comes to a full stop about 2.5 s after the switch turns off", () => {
    const dt = 1 / 240;
    const state = { angle: 0, speed: MAX_DEGREES_PER_SECOND, active: false };
    let elapsed = 0;
    while (state.speed > 0 && elapsed < 10) {
      stepSpin(state, MAX_DEGREES_PER_SECOND, false, dt);
      elapsed += dt;
    }

    // Spec § 旋转运行时: ln(1700/450)/1.010135 + ln(450/0.5)/5.268026 = 2.61 s, the same
    // order as the reference implementation's ~2 s coast-down.
    expect(elapsed).toBeGreaterThan(2.5 * 0.95);
    expect(elapsed).toBeLessThan(2.5 * 1.05);
    expect(state.speed).toBe(0);
  });

  it("snaps the residual tail to zero instead of decaying forever", () => {
    const state = { angle: 10, speed: 0.52, active: false };
    stepSpin(state, MAX_DEGREES_PER_SECOND, false, FRAME_SECONDS);
    expect(state.speed).toBe(0);
    // The last moving frame still integrates its (tiny) rotation before the snap.
    expect(state.angle).toBeCloseTo(
      10 + 0.52 * Math.exp(-SPIN_FAST_DECAY_PER_SECOND * FRAME_SECONDS) * FRAME_SECONDS,
      10,
    );
  });

  it("keeps the angle inside (-180, 180] over a long spin", () => {
    const state = createSpinState();
    for (let frame = 0; frame < 5000; frame += 1) {
      stepSpin(state, MAX_DEGREES_PER_SECOND, true, FRAME_SECONDS);
      expect(state.angle).toBeGreaterThan(-180);
      expect(state.angle).toBeLessThanOrEqual(180);
    }
  });
});

describe("foreshorten", () => {
  it("is the original's |cos(angle)| factor", () => {
    expect(foreshorten(0)).toBe(1);
    expect(foreshorten(90)).toBeCloseTo(0, 10);
    expect(foreshorten(180)).toBe(1);
    expect(foreshorten(60)).toBeCloseTo(0.5, 10);
    expect(foreshorten(-120)).toBeCloseTo(0.5, 10);
  });

  it("stays within [0, 1] for every angle", () => {
    for (let angle = -1080; angle <= 1080; angle += 15) {
      const scale = foreshorten(angle);
      expect(scale).toBeGreaterThanOrEqual(0);
      expect(scale).toBeLessThanOrEqual(1);
    }
  });

  it("is periodic, so the wrapped and unwrapped angles compress identically", () => {
    expect(foreshorten(SPIN_RELEASE_ANGLE_DEGREES)).toBeCloseTo(
      foreshorten(SPIN_RELEASE_ANGLE_DEGREES - 360),
      10,
    );
  });
});
