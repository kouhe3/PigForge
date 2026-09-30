/**
 * Frame-table player and pig expression machine (`docs/specs/part-texture-animation.md`
 * § 运行时契约 → 逐帧运行时).
 *
 * Both halves port BPLE: `SpriteAnimation.cs:196-215` pre-builds one mesh per frame and
 * `:229-260` swaps `sharedMesh` once the accumulated timer passes the current frame's
 * duration (hard cut, no interpolation, no event frames); `Pig.cs:277-438` picks the face
 * clip from the body's velocity every frame. Nothing here touches Vue or the DOM — the
 * renderer reads the descriptors these functions return.
 */

import type { AnimationFrame, ExpressionDescriptor, PartSprite, SpriteClips } from "../atlas";

/** The clip a fresh player opens on: the pig's idle face, the clip every animated sprite has. */
const DEFAULT_CLIP = "Normal";

export interface ClipPlayer {
  /** The static sprite this player animates; also the pose source for any clip gap. */
  readonly sprite: PartSprite;
  readonly clips: SpriteClips;
  /** Clip currently playing. */
  clip: string;
  frame: number;
  elapsed: number;
  /** Drawable descriptor of the current frame; rebuilt in place only when the frame changes. */
  pose: PartSprite;
}

/** The fields a pose copies: everything an `AnimationFrame` places, minus its duration. */
type PoseSource = Omit<AnimationFrame, "seconds">;

/**
 * Builds the single pose object a player owns. A pose is a leaf: it carries the placement the
 * renderer blits plus the static sprite's `rotates` flag (the wheel path reads it), and never
 * the `clips`/`spin` descriptors — those describe the manifest sprite, not a drawn frame.
 */
function createPose(source: PoseSource, rotates: boolean): PartSprite {
  return {
    atlas: source.atlas,
    x: source.x,
    y: source.y,
    w: source.w,
    h: source.h,
    cx: source.cx,
    cy: source.cy,
    sx: source.sx,
    sy: source.sy,
    rot: source.rot,
    rotates,
  };
}

/**
 * In-place twin of `createPose`, kept in sync field for field: the frame hot path rewrites the
 * pose the player already holds, so a step that stays on the same frame allocates nothing.
 */
function writePose(pose: PartSprite, source: PoseSource, rotates: boolean): void {
  pose.atlas = source.atlas;
  pose.x = source.x;
  pose.y = source.y;
  pose.w = source.w;
  pose.h = source.h;
  pose.cx = source.cx;
  pose.cy = source.cy;
  pose.sx = source.sx;
  pose.sy = source.sy;
  pose.rot = source.rot;
  pose.rotates = rotates;
}

export function createClipPlayer(sprite: PartSprite, clips: SpriteClips): ClipPlayer {
  const start = Object.hasOwn(clips, DEFAULT_CLIP) ? DEFAULT_CLIP : (Object.keys(clips)[0] ?? "");
  const clip = clips[start];
  // A sprite without clips stays on its manifest descriptor (`PartSprite` carries every field
  // `AnimationFrame` has, minus `seconds`). The parser guarantees a present clip has a frame 0.
  return {
    sprite,
    clips,
    clip: start,
    frame: 0,
    elapsed: 0,
    pose: createPose(clip ? clip.frames[0] : sprite, sprite.rotates),
  };
}

export function playClip(player: ClipPlayer, name: string): void {
  // A name the sprite has no clip for is ignored: unlike the original's `Play`
  // (`SpriteAnimation.cs:143-166`, which falls back to the first animation), PigForge keeps
  // whatever was playing — a caller lagging behind the manifest should not jump the art.
  if (!Object.hasOwn(player.clips, name)) return;
  player.clip = name;
  player.frame = 0;
  player.elapsed = 0;
  writePose(player.pose, player.clips[name].frames[0], player.sprite.rotates);
}

export function stepClip(player: ClipPlayer, dtSeconds: number): void {
  const clip = player.clips[player.clip];
  if (!clip) return;
  const frames = clip.frames;
  player.elapsed += dtSeconds;
  let advanced = false;
  while (player.elapsed >= frames[player.frame].seconds) {
    if (player.frame < frames.length - 1) {
      player.elapsed -= frames[player.frame].seconds;
      player.frame++;
      advanced = true;
    } else if (clip.loop) {
      player.elapsed -= frames[player.frame].seconds;
      if (player.frame !== 0) advanced = true;
      player.frame = 0;
    } else {
      // Past the last frame without a loop: hold it (`SpriteAnimation.cs:246-252` does nothing
      // when the last frame has no successor and the clip does not loop). Drop the surplus time
      // so later steps cannot re-enter this branch, and leave the pose untouched.
      player.elapsed = 0;
      break;
    }
  }
  if (advanced) writePose(player.pose, frames[player.frame], player.sprite.rotates);
}

/** Blink re-arm floor: `UnityEngine.Random.Range(1.5f, 4f)` (Pig.cs:296). */
export const BLINK_MIN_SECONDS = 1.5;

/** Blink re-arm ceiling: `UnityEngine.Random.Range(1.5f, 4f)` (Pig.cs:296). */
export const BLINK_MAX_SECONDS = 4;

/** How long `Hit` holds: `PlayAnimation(Expressions.Hit, 1f)` (Pig.cs:315). */
export const HIT_SECONDS = 1;

/** How long a newly selected expression is committed: `Time.time > m_expressionSetTime + 1f` (Pig.cs:413). */
export const EXPRESSION_MIN_SECONDS = 1;

/** `num` counts vertical speed 0.3× on top of the speed magnitude (`Pig.cs:415`). */
const VERTICAL_SPEED_WEIGHT = 0.3;

/** Physics ticks per second used to turn a per-tick thrust into a "one second of thrust" speed. */
const THRUST_TICKS_PER_SECOND = 60;

export interface ExpressionState {
  /** Selected expression: Normal | Grin | FearfulGrin | Fear_1 | Fear_2. */
  expression: string;
  /** Clip currently requested: an expression name, or the transient Blink / Hit. */
  playing: string;
  /**
   * Bumped whenever a clip is *requested* — a new expression, a fresh blink, a hit, or the end
   * of a hit. The renderer restarts the player on a change, which is what the original gets from
   * `SpriteAnimation.Play` queuing the same clip again (`Pig.cs:730-742`): without it a second
   * blink never reopens the eyes' first frame and `Hit` would hold its face forever.
   */
  requestId: number;
  hitRemaining: number;
  setCooldown: number;
  blinkTimer: number;
  lastSpeed: number;
  started: boolean;
  /**
   * Uniform source for the blink re-arm. The original draws from `UnityEngine.Random`
   * (Pig.cs:296); injection keeps the roll out of the state hash and lets tests pin it.
   */
  readonly random: () => number;
}

export function createExpressionState(random: () => number): ExpressionState {
  return {
    expression: "Normal",
    playing: "Normal",
    requestId: 0,
    hitRemaining: 0,
    setCooldown: 0,
    // Armed at materialization so the first blink lands 1.5-4s after the pig starts running.
    blinkTimer: BLINK_MIN_SECONDS + random() * (BLINK_MAX_SECONDS - BLINK_MIN_SECONDS),
    lastSpeed: 0,
    started: false,
    random,
  };
}

/** vRef: the speed a body reaches after one second of full motor thrust, or the fallback. */
export function speedReference(sumThrustPerTick: number, totalMass: number, fallback: number): number {
  // Calibrated on the sandbox (spec "阈值标定（实测）"): 2 motors × 2.2 thrust / 3.6 kg → 73.33 m/s.
  // A body with no active motor (glider, rocket, free fall) has no such speed, so the manifest's
  // absolute `speedReference` stands in.
  if (!(sumThrustPerTick > 0) || !(totalMass > 0)) return fallback;
  return (THRUST_TICKS_PER_SECOND * sumThrustPerTick) / totalMass;
}

/**
 * Applies a selection the way `SetExpression` does: the identity is the *selected* expression,
 * so a pick equal to it is a no-op, and any change restarts the 1s guard (Pig.cs:726-727).
 * Band comparison therefore reads `state.expression`, never `state.playing`: the face must stay
 * on the Blink clip while the expression is still `Normal`.
 */
function applyExpression(state: ExpressionState, next: string): void {
  if (next === state.expression) return;
  state.expression = next;
  requestClip(state, next);
  state.setCooldown = EXPRESSION_MIN_SECONDS;
}

/**
 * Requests a clip, restarting it even when the name is unchanged: `SpriteAnimation.Play` queues
 * the clip and replays it from frame 0 when the current one ends (`Pig.cs:730-742`), which is how
 * a second blink reopens the eyes and how a finished `Hit` hands the face back to the expression.
 */
function requestClip(state: ExpressionState, name: string): void {
  state.playing = name;
  state.requestId += 1;
}

/** Speed bands of `Pig.cs:416-424`, expressed as ratios of vRef (spec "阈值标定（实测）"). */
function pickBandExpression(expression: ExpressionDescriptor, num: number, reference: number): string {
  let result = "Normal";
  if (num > expression.speedFunRatio * reference) result = "Grin";
  if (num > expression.speedFearfulRatio * reference) result = "FearfulGrin";
  if (num > expression.speedFearRatio * reference) result = "Fear_1";
  return result;
}

/**
 * One frame of the pig expression machine; returns the clip to play (`state.playing`), which is
 * an expression name or the transient Blink / Hit.
 */
export function stepExpression(
  state: ExpressionState,
  expression: ExpressionDescriptor,
  vx: number,
  vy: number,
  speedReference: number,
  dtSeconds: number,
): string {
  const speed = Math.hypot(vx, vy);
  const num = speed + VERTICAL_SPEED_WEIGHT * Math.abs(vy);
  if (!state.started) {
    // The original compares against the magnitude it kept from the previous Update, so the very
    // first frame of an entity must not read as a hit (Pig.cs:145, 312-321).
    state.started = true;
    state.lastSpeed = speed;
    return state.playing;
  }
  if (state.hitRemaining > 0) {
    // `Hit` holds for one second and nothing re-selects meanwhile (Pig.cs:313-320, 730-741);
    // the magnitude keeps tracking so the hold does not end in a phantom jump.
    state.hitRemaining = Math.max(0, state.hitRemaining - dtSeconds);
    if (state.hitRemaining === 0) {
      // `AnimationCoroutine` ends with `SetExpression(Normal)` (Pig.cs:735-741): the face returns
      // to the idle clip and the expression guard restarts from that moment.
      state.expression = "Normal";
      requestClip(state, "Normal");
      state.setCooldown = EXPRESSION_MIN_SECONDS;
    }
    state.lastSpeed = speed;
    return state.playing;
  }
  state.blinkTimer -= dtSeconds;
  if (state.blinkTimer <= 0 && state.expression === "Normal") {
    // Blink is armed by this roll alone and only while the face is idle (Pig.cs:283-296). It is
    // played straight on the face animation, outside `SetExpression`, so the selected expression
    // is untouched and every roll replays the clip from frame 0. The timer is not clamped: it
    // stays expired until the expression returns to Normal, and the Blink clip ends on the
    // Normal art, so the clip self-clears.
    requestClip(state, "Blink");
    state.blinkTimer = BLINK_MIN_SECONDS + state.random() * (BLINK_MAX_SECONDS - BLINK_MIN_SECONDS);
  }
  if (Math.abs(speed - state.lastSpeed) > expression.hitDeltaV) {
    requestClip(state, "Hit");
    state.hitRemaining = HIT_SECONDS;
  } else if (state.setCooldown <= 0) {
    if (-vy > expression.fallFearThreshold) {
      // Falling fast: `Fear_2`. The original also requires a quarter second off the ground,
      // which needs contact state the snapshot does not carry (spec 偏差 4).
      applyExpression(state, "Fear_2");
    } else {
      applyExpression(state, pickBandExpression(expression, num, speedReference));
    }
  }
  state.lastSpeed = speed;
  state.setCooldown = Math.max(0, state.setCooldown - dtSeconds);
  return state.playing;
}
