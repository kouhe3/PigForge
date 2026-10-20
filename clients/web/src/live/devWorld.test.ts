import { describe, expect, it } from "vitest";
import { devPageUrl, initialLiveUrl, liveUrlFor, parseDevQuery, worldQuery } from "./devWorld";

describe("parseDevQuery", () => {
  it("reads the drawer flag and one selection", () => {
    expect(parseDevQuery("?dev=1&scenario=sky-bench")).toEqual({ dev: true, scenario: "sky-bench", level: undefined });
    expect(parseDevQuery("?dev=1&level=original%2Fepisode_1_levels%2FLevel_05.json")).toEqual({
      dev: true,
      scenario: undefined,
      level: "original/episode_1_levels/Level_05.json",
    });
    expect(parseDevQuery("?scenario=sky-bench")).toEqual({ dev: false, scenario: "sky-bench", level: undefined });
    expect(parseDevQuery("")).toEqual({ dev: false, scenario: undefined, level: undefined });
  });

  it("never returns both selections, matching the server's rejection", () => {
    expect(parseDevQuery("?dev=1&scenario=sky-bench&level=terrain-v1.json")).toEqual({
      dev: true,
      scenario: undefined,
      level: undefined,
    });
  });

  it("ignores blank values", () => {
    expect(parseDevQuery("?dev=1&scenario=%20%20")).toEqual({ dev: true, scenario: undefined, level: undefined });
  });
});

describe("worldQuery", () => {
  it("writes one selection and escapes it", () => {
    expect(worldQuery({})).toBe("");
    expect(worldQuery({ scenario: "sky-bench" })).toBe("scenario=sky-bench");
    expect(worldQuery({ level: "original/episode_1_levels/Level_05.json" })).toBe(
      "level=original%2Fepisode_1_levels%2FLevel_05.json",
    );
  });

  it("drops both when both are given", () => {
    expect(worldQuery({ scenario: "sky-bench", level: "terrain-v1.json" })).toBe("");
  });
});

describe("liveUrlFor", () => {
  it("appends the world query to the socket", () => {
    expect(liveUrlFor("ws://127.0.0.1:5088/play", {})).toBe("ws://127.0.0.1:5088/play");
    expect(liveUrlFor("ws://127.0.0.1:5088/play", { scenario: "sky-bench" })).toBe(
      "ws://127.0.0.1:5088/play?scenario=sky-bench",
    );
    expect(liveUrlFor("/play?room=1", { scenario: "sky-bench" })).toBe("/play?room=1&scenario=sky-bench");
  });
});

describe("devPageUrl", () => {
  it("keeps dev=1 and replaces the world", () => {
    expect(devPageUrl("?dev=1&scenario=old", { scenario: "sky-bench" })).toBe("?dev=1&scenario=sky-bench");
    expect(devPageUrl("?dev=1&scenario=old", {})).toBe("?dev=1");
    expect(devPageUrl("", { level: "terrain-v1.json" })).toBe("?dev=1&level=terrain-v1.json");
  });
});

describe("initialLiveUrl", () => {
  it("only carries the world when dev=1", () => {
    expect(initialLiveUrl("ws://127.0.0.1:5088/play", "?scenario=sky-bench")).toBe("ws://127.0.0.1:5088/play");
    expect(initialLiveUrl("ws://127.0.0.1:5088/play", "?dev=1&scenario=sky-bench")).toBe(
      "ws://127.0.0.1:5088/play?scenario=sky-bench",
    );
    expect(initialLiveUrl("ws://127.0.0.1:5088/play", undefined)).toBe("ws://127.0.0.1:5088/play");
  });
});
