/**
 * Animation clock: wall-clock difference plus the running gate.
 *
 * The original drives every part animation from `Time.deltaTime` and freezes all of them
 * by pausing the game (`GameTime.Pause` -> `Time.timeScale = 0`, GameTime.cs:42-47), so
 * the client mirrors that with a timestamp difference and a boolean gate instead of ever
 * reading a clock of its own: `App.vue` passes its `requestAnimationFrame` timestamp in.
 */

/**
 * Longest step this layer will ever take, in seconds.
 * Spec `docs/specs/part-texture-animation.md` § Assumptions 5: a single frame's `dt` is
 * clamped to 0.1 s, so a stalled or backgrounded tab cannot jump the animation forward.
 */
const MAX_STEP_SECONDS = 0.1;

export interface AnimationClock {
  /** Advances the wall clock and returns this frame's animation step in seconds; 0 when frozen. */
  step(now: number, running: boolean): number;
  /** Forgets the previous timestamp, so the next step returns 0 (phase restart on Start/RESET). */
  reset(): void;
}

export function createAnimationClock(): AnimationClock {
  let previous: number | null = null;

  return {
    step(now: number, running: boolean): number {
      if (previous === null) {
        previous = now;
        return 0;
      }
      const elapsedSeconds = (now - previous) / 1000;
      // The timestamp is recorded even while frozen: when a paused or backgrounded frame
      // comes back, `dt` must be one frame, not the whole pause (spec § 时钟与门控).
      previous = now;
      if (!running) {
        return 0;
      }
      // A backwards clock never rewinds animation state; `now` comes from the host.
      if (elapsedSeconds <= 0) {
        return 0;
      }
      return elapsedSeconds > MAX_STEP_SECONDS ? MAX_STEP_SECONDS : elapsedSeconds;
    },

    reset(): void {
      previous = null;
    },
  };
}
