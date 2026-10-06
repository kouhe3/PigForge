import { beforeEach, describe, expect, it, vi } from "vitest";
import { createPinia, setActivePinia } from "pinia";
import { useSessionStore } from "./session";
import type { LevelContentDocument } from "@/schema/levelContent";

const level: LevelContentDocument = {
  format: "pigforge.level-content",
  schemaVersion: 2,
  contentVersion: "episode_1_level_05",
  goalZone: { min: [12, -0.5, -2], max: [16, 2.5, 2] },
  bounds: { min: [-30, -12, -8], max: [30, 30, 8] },
  spawns: [],
  terrain: [{ position: [-2.79, 9.02, 0], depth: 10, loops: [[[0, 0], [4, 0], [0, 3]]] }],
};

function response(body: unknown, ok = true): Response {
  return { ok, json: async () => body } as unknown as Response;
}

beforeEach(() => {
  setActivePinia(createPinia());
  vi.unstubAllGlobals();
});

describe("session level", () => {
  it("decodes a valid level document into the store", () => {
    const session = useSessionStore();
    expect(session.loadLevel(level)).toBe(true);
    expect(session.level).toEqual(level);
  });

  it("clears the level on a malformed document without disturbing the session errors", () => {
    const session = useSessionStore();
    session.loadLevel(level);
    session.setErrors(["socket failed"]);
    expect(session.loadLevel({ format: "pigforge.level-content", schemaVersion: 3 })).toBe(false);
    expect(session.level).toBeNull();
    expect(session.errors).toEqual(["socket failed"]);
  });

  it("fetches the level next to the live socket on connect", async () => {
    const session = useSessionStore();
    const fetcher = vi.fn(async () => response(level));
    vi.stubGlobal("fetch", fetcher);
    expect(await session.loadLiveLevel()).toBe(true);
    expect(session.level).toEqual(level);
    expect(fetcher).toHaveBeenCalledWith("http://127.0.0.1:5088/level", { headers: { accept: "application/json" } });
  });

  it("degrades to no level when the endpoint is unreachable", async () => {
    const session = useSessionStore();
    session.loadLevel(level);
    vi.stubGlobal("fetch", vi.fn(async () => response("missing", false)));
    expect(await session.loadLiveLevel()).toBe(false);
    expect(session.level).toBeNull();
  });

  it("ignores a response that lands after the socket URL changed", async () => {
    const session = useSessionStore();
    session.liveUrl = "ws://127.0.0.1:5088/play";
    let release!: () => void;
    const gate = new Promise<void>((resolve) => {
      release = resolve;
    });
    vi.stubGlobal("fetch", vi.fn(async () => {
      await gate;
      return response(level);
    }));
    const pending = session.loadLiveLevel();
    session.liveUrl = "ws://other:5088/play";
    release();
    expect(await pending).toBe(false);
    expect(session.level).toBeNull();
  });
});
