import { describe, expect, it } from "vitest";
import type { LevelChallenge } from "@/schema/levelContent";
import {
  RUN_FAILED_PHASE,
  RUN_WON_PHASE,
  challengeDone,
  challengeLabel,
  countdownText,
  showsCountdown,
  starSlots,
  starsFor,
} from "./runStars";

describe("starsFor", () => {
  it("counts one star for finishing plus the two scored challenges", () => {
    // 0: still running, 1: the goal alone, 2: goal + challenges[0], 3: goal + both.
    expect(starsFor(0, 0, 2)).toBe(0);
    expect(starsFor(RUN_WON_PHASE, 0, 2)).toBe(1);
    expect(starsFor(RUN_WON_PHASE, 0b01, 2)).toBe(2);
    expect(starsFor(RUN_WON_PHASE, 0b11, 2)).toBe(3);
  });

  it("scores only the first two challenges and ignores a lost run's flags", () => {
    // The third challenge (one original level has one) never scores (`LevelComplete.cs:422/460`),
    // and the room reports flags 0 until the run is won.
    expect(starsFor(RUN_WON_PHASE, 0b111, 3)).toBe(3);
    expect(starsFor(RUN_WON_PHASE, 0b11, 1)).toBe(2);
    expect(starsFor(RUN_FAILED_PHASE, 0, 2)).toBe(0);
  });
});

describe("starSlots", () => {
  it("gives one slot per star the level can award, filled from the frame's flags", () => {
    expect(starSlots(RUN_WON_PHASE, 0b10, 2)).toEqual([true, false, true]);
    expect(starSlots(0, 0, 2)).toEqual([false, false, false]);
  });

  it("stops at the first star when the level has no challenge", () => {
    expect(starSlots(RUN_WON_PHASE, 0, 0)).toEqual([true]);
    expect(starSlots(RUN_FAILED_PHASE, 0, 0)).toEqual([false]);
  });
});

describe("challengeDone", () => {
  it("reads the flag byte's bits and refuses anything outside it", () => {
    expect(challengeDone(0b10, 1)).toBe(true);
    expect(challengeDone(0b10, 0)).toBe(false);
    expect(challengeDone(0xff, -1)).toBe(false);
    expect(challengeDone(0xff, 8)).toBe(false);
  });
});

describe("countdownText", () => {
  it("prints the remaining time in the original's D2.D2", () => {
    expect(countdownText(30, 4.5)).toEqual({ text: "25.50", over: false });
    expect(countdownText(4, 0)).toEqual({ text: "04.00", over: false });
    expect(countdownText(120, 5.25)).toEqual({ text: "114.75", over: false });
    // A reading that rounds up to the next second carries into the whole part.
    expect(countdownText(25, 0.001)).toEqual({ text: "25.00", over: false });
  });

  it("keeps the magnitude and shows the original's plus once the limit is over", () => {
    expect(countdownText(4, 4.35)).toEqual({ text: "+00.35", over: true });
    expect(countdownText(30, 30)).toEqual({ text: "00.00", over: false });
  });
});

describe("showsCountdown", () => {
  const time = (targetTime: number): LevelChallenge => ({ name: "TimeChallenge", kind: "time", targetTime });

  it("draws a clock only when the level's time limit is positive", () => {
    expect(showsCountdown([])).toBe(false);
    expect(showsCountdown([{ name: "BoxChallenge", kind: "box", propIndex: 5 }])).toBe(false);
    expect(showsCountdown([time(4)])).toBe(true);
    // A zero target time leaves the level's limit at 0, which hides the clock (`ScoreCounters.cs:51-55`).
    expect(showsCountdown([time(0)])).toBe(false);
    expect(showsCountdown([time(3), time(80)])).toBe(true);
  });
});

describe("challengeLabel", () => {
  const nameOf = (partTypeId: number): string | undefined => (partTypeId === 24 ? "猪王" : undefined);

  it("describes each of the five kinds", () => {
    expect(challengeLabel({ name: "BoxChallenge", kind: "box", propIndex: 5 }, nameOf)).toBe("收集挑战箱");
    expect(challengeLabel({ name: "TimeChallenge", kind: "time", targetTime: 4 }, nameOf)).toBe("在 4 秒内完成");
    expect(challengeLabel({ name: "DontUsePartChallenge", kind: "dontUseParts", partTypeId: 24 }, nameOf)).toBe("不使用 猪王");
    expect(challengeLabel({ name: "PerfectFlightChallenge", kind: "perfectFlight" }, nameOf)).toBe("不损坏载具");
    expect(challengeLabel({ name: "TransportChallenge", kind: "transport", partTypeId: 24 }, nameOf)).toBe("运送 猪王");
  });

  it("falls back to the raw family id and never names a challenge without a part", () => {
    const asked: number[] = [];
    const spying = (partTypeId: number): string | undefined => {
      asked.push(partTypeId);
      return undefined;
    };

    expect(challengeLabel({ name: "DontUsePartChallenge", kind: "dontUseParts", partTypeId: 999 }, spying)).toBe("不使用 999");
    expect(asked).toEqual([999]);
    expect(challengeLabel({ name: "BoxChallenge", kind: "box", propIndex: 5 }, spying)).toBe("收集挑战箱");
    expect(challengeLabel({ name: "TimeChallenge", kind: "time", targetTime: 30 }, spying)).toBe("在 30 秒内完成");
    expect(challengeLabel({ name: "PerfectFlightChallenge", kind: "perfectFlight" }, spying)).toBe("不损坏载具");
    expect(asked).toEqual([999]);
  });
});
