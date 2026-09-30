import { beforeEach, describe, expect, it } from "vitest";
import { createAnimationClock } from "./clock";
import type { AnimationClock } from "./clock";

describe("createAnimationClock", () => {
  let clock: AnimationClock;

  beforeEach(() => {
    clock = createAnimationClock();
  });

  it("returns 0 on the first step and starts measuring from there", () => {
    expect(clock.step(1000, true)).toBe(0);
    expect(clock.step(1016, true)).toBeCloseTo(0.016);
  });

  it("returns 0 while frozen without advancing the animation", () => {
    clock.step(0, true);
    expect(clock.step(100, false)).toBe(0);
    expect(clock.step(200, false)).toBe(0);
  });

  it("keeps the timestamp current while frozen so a paused tab does not catch up", () => {
    clock.step(0, true);
    // A frozen frame still records `now`: the resumed frame measures one frame, not the pause.
    expect(clock.step(60000, false)).toBe(0);
    expect(clock.step(60016, true)).toBeCloseTo(0.016);
  });

  it("clamps a long step at 0.1 s", () => {
    clock.step(0, true);
    expect(clock.step(1000, true)).toBe(0.1);
    clock.step(1001, true);
    expect(clock.step(2000, true)).toBe(0.1);
  });

  it("returns 0 for a backwards clock instead of a negative step", () => {
    clock.step(1000, true);
    expect(clock.step(900, true)).toBe(0);
    expect(clock.step(950, true)).toBeCloseTo(0.05);
  });

  it("returns 0 on the step after reset, discarding the previous timestamp", () => {
    clock.step(0, true);
    expect(clock.step(500, true)).toBe(0.1);
    clock.reset();
    expect(clock.step(2000, true)).toBe(0);
    expect(clock.step(2100, true)).toBeCloseTo(0.1);
  });

  it("resets a frozen clock as well, so a resumed phase starts clean", () => {
    clock.step(0, true);
    clock.step(1000, false);
    clock.reset();
    expect(clock.step(5000, true)).toBe(0);
  });
});
