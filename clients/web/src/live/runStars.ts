import { timeLimit, type LevelChallenge } from "@/schema/levelContent";

/**
 * The run's stars, clock and objective text (`docs/specs/level-stars.md` §2, §6): the client-side
 * half of the challenge chain, derived from the two fields PGFS v8 appends to every snapshot
 * (`elapsedSeconds`, `challengeFlags`) plus the level document's own `challenges`. Everything here
 * is pure, so `App.vue` only reads the frame's tail and never re-derives any of it while painting.
 */

/** `GameplayRules.Won`: the pig reached the goal area, so the run has its first star. */
export const RUN_WON_PHASE = 1;
/** `GameplayRules.Failed`: the run ended without the goal. */
export const RUN_FAILED_PHASE = 2;
/**
 * The original's own stars: one for finishing plus one each for the first two challenges
 * (`LevelComplete.cs:422/460`), so a run awards three at most.
 */
const SCORED_CHALLENGES = 2;

/**
 * How many stars the run has earned: one for reaching the goal plus one per completed challenge
 * among the first two (`LevelComplete.cs:287-346`; the original's icon swap only reorders them).
 * The room reports `challengeFlags` as 0 until the run is won, so a lost run is always 0 stars.
 */
export function starsFor(phase: number, challengeFlags: number, challengeCount: number): number {
  let stars = phase === RUN_WON_PHASE ? 1 : 0;
  for (let index = 0; index < Math.min(challengeCount, SCORED_CHALLENGES); index++) {
    if (challengeDone(challengeFlags, index)) {
      stars += 1;
    }
  }
  return stars;
}

/**
 * The stars the HUD paints, in slot order: slot 1 is the run itself (`phase == Won`) and the next
 * slots are the first two challenges' own flags, dark until the room reports them. A level with one
 * challenge (or none) has that many slots -- the original hides the star objects it has no use for.
 */
export function starSlots(phase: number, challengeFlags: number, challengeCount: number): boolean[] {
  const slots = [phase === RUN_WON_PHASE];
  for (let index = 0; index < Math.min(challengeCount, SCORED_CHALLENGES); index++) {
    slots.push(challengeDone(challengeFlags, index));
  }
  return slots;
}

/** Bit `index` of the room's `challengeFlags`; outside the byte the answer is always false. */
export function challengeDone(challengeFlags: number, index: number): boolean {
  if (index < 0 || index > 7) {
    return false;
  }
  return ((challengeFlags >>> index) & 1) === 1;
}

/**
 * The clock's reading, the original's `D2.D2`: `limitSeconds - elapsedSeconds` with at least two
 * integer digits and two decimals. A negative reading keeps the magnitude and takes the `+` the
 * original shows past the limit (`ScoreCounters.cs:67-91`); the caller colours that state red.
 */
export function countdownText(limitSeconds: number, elapsedSeconds: number): { text: string; over: boolean } {
  const remaining = limitSeconds - elapsedSeconds;
  const over = remaining < 0;
  const [whole, fraction] = Math.abs(remaining).toFixed(2).split(".");
  return { text: `${over ? "+" : ""}${whole.padStart(2, "0")}.${fraction}`, over };
}

/**
 * Whether the level's clock is drawn at all: only a positive time limit shows one, and a level
 * without a `time` challenge has a limit of 0 (`ScoreCounters.cs:51-55`, `timeLimit`).
 */
export function showsCountdown(challenges: readonly LevelChallenge[]): boolean {
  return timeLimit(challenges) > 0;
}

/**
 * One objective's text. The original's own complete menu shows icons only (`level-stars.md` §8 D5),
 * so the wording is the client's; a part-named challenge asks `partName` for the family's name and
 * prints the raw id when the caller cannot name it.
 */
export function challengeLabel(
  challenge: LevelChallenge,
  partName: (partTypeId: number) => string | undefined,
): string {
  // Only a part-named challenge carries the family; the other kinds never reach the lookup.
  const named = challenge.partTypeId === undefined
    ? "未知零件"
    : partName(challenge.partTypeId) ?? String(challenge.partTypeId);
  switch (challenge.kind) {
    case "box":
      return "收集挑战箱";
    case "time":
      return `在 ${challenge.targetTime ?? 0} 秒内完成`;
    case "dontUseParts":
      return `不使用 ${named}`;
    case "perfectFlight":
      return "不损坏载具";
    case "transport":
      return `运送 ${named}`;
  }
}
