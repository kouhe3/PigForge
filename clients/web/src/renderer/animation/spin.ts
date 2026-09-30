/**
 * Spin runtime for fans, propellers and rotors.
 *
 * The original accumulates a blade angle while the part's switch is on and writes
 * `m_fanVisualization.localRotation = m_origRot * AngleAxis(m_angle, axis)` every
 * LateUpdate (FanPropeller.cs:320-324), with `axis = m_isRotor ? Vector3.up : Vector3.right`
 * (Part_Rotor_01_SET.prefab:135 sets `m_isRotor` for the rotor, the other prefabs leave it 0).
 * The sprite itself is never rotated: the renderer compresses it by `|cos(angle)|`
 * (spec § 旋转运行时). This module is the state machine only — no imports, no allocation,
 * no DOM, so it can run inside the draw hot path.
 */

/** Blade speed, in degrees per second, forced the instant the switch turns off (the original's visible "jolt"). */
// FanPropeller.cs:274, `SetEnabled(false)`.
export const SPIN_RELEASE_SPEED_DEGREES = 800;

/** Blade angle the same edge snaps to. */
// FanPropeller.cs:275, `SetEnabled(false)`.
export const SPIN_RELEASE_ANGLE_DEGREES = 292.3;

/** Below this speed (degrees per second) the original switches to the faster decay factor. */
// FanPropeller.cs:130, `m_rotationSpeed < 450f`.
export const SPIN_DECAY_SPEED_THRESHOLD = 450;

/**
 * Continuous decay rate below the threshold: `-ln(0.9) / 0.02`, the continuous form of the
 * original's `m_rotationSpeed *= 0.9f` per FixedUpdate.
 */
// FanPropeller.cs:132 (`0.9f`) with FixedUpdate's default 0.02 s timestep.
export const SPIN_FAST_DECAY_PER_SECOND = 5.268026;

/**
 * Continuous decay rate at or above the threshold: `-ln(0.98) / 0.02`, the continuous form
 * of the original's `m_rotationSpeed *= 0.98f` per FixedUpdate.
 */
// FanPropeller.cs:136 (`0.98f`) with FixedUpdate's default 0.02 s timestep.
export const SPIN_SLOW_DECAY_PER_SECOND = 1.010135;

/** Speeds below this (degrees per second) snap to zero once the switch is off. */
// Spec § 旋转运行时: an inactive part below 0.5 deg/s is zeroed so the exponential tail
// never keeps spinning forever; the original leaves the float tail in place instead.
export const SPIN_STOP_SPEED = 0.5;

export interface SpinState {
  /** Blade angle in degrees, kept in (-180, 180] except on the release step (see `stepSpin`). */
  angle: number;
  /** Current blade speed in degrees per second; decays towards zero while the switch is off. */
  speed: number;
  /** Switch state the previous step ran with, used to detect the falling edge. */
  active: boolean;
}

export function createSpinState(): SpinState {
  return { angle: 0, speed: 0, active: false };
}

export function stepSpin(
  state: SpinState,
  maxDegreesPerSecond: number,
  active: boolean,
  dtSeconds: number,
): void {
  const released = state.active && !active;

  if (active) {
    // FanPropeller.cs:128-129: the switch pins the speed at the part's maximum every step.
    state.speed = maxDegreesPerSecond;
    state.angle = wrapAngleDegrees(state.angle + state.speed * dtSeconds);
  } else if (released) {
    // FanPropeller.cs:274-275: turning the switch off writes the speed and the angle
    // directly, in the input path rather than in the accumulation step — so no decay is
    // applied here, and the angle is published as written (the wrap runs on the next step,
    // exactly as the original wraps on its following FixedUpdate).
    state.speed = SPIN_RELEASE_SPEED_DEGREES;
    state.angle = SPIN_RELEASE_ANGLE_DEGREES;
  } else {
    // FanPropeller.cs:130-136: `speed < 450 ? speed *= 0.9 : speed *= 0.98` per 0.02 s
    // FixedUpdate, rewritten as a continuous exponential so the frame rate does not change
    // the stopping time. Both forms telescope over equal steps.
    const decay =
      state.speed >= SPIN_DECAY_SPEED_THRESHOLD
        ? SPIN_SLOW_DECAY_PER_SECOND
        : SPIN_FAST_DECAY_PER_SECOND;
    state.speed *= Math.exp(-decay * dtSeconds);
    // FanPropeller.cs:138-141: `m_angle += m_rotationSpeed * Time.deltaTime`, then the
    // `m_angle > 180 -> m_angle -= 360` wrap (generalised here to (-180, 180]).
    state.angle = wrapAngleDegrees(state.angle + state.speed * dtSeconds);
    if (state.speed < SPIN_STOP_SPEED) {
      state.speed = 0;
    }
  }

  state.active = active;
}

/** |cos(angle)|: the original's foreshortening factor for a spinning sprite (spec § 旋转运行时). */
export function foreshorten(angleDegrees: number): number {
  return Math.abs(Math.cos((angleDegrees * Math.PI) / 180));
}

/** Wraps degrees into (-180, 180]: the original's `angle > 180 -> angle -= 360`. */
function wrapAngleDegrees(angleDegrees: number): number {
  const wrapped = (((angleDegrees + 180) % 360) + 360) % 360 - 180;
  return wrapped === -180 ? 180 : wrapped;
}
