import { describe, expect, it, vi } from "vitest";
import { fetchLevel, levelUrlFrom } from "./levelEndpoint";
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

describe("levelUrlFrom", () => {
  it("derives the HTTP level endpoint next to the play socket", () => {
    expect(levelUrlFrom("ws://127.0.0.1:5088/play")).toBe("http://127.0.0.1:5088/level");
  });

  it("derives it from the broadcast socket too, dropping the query string", () => {
    expect(levelUrlFrom("ws://127.0.0.1:5088/snapshots?room=1")).toBe("http://127.0.0.1:5088/level");
  });

  it("keeps TLS for a secure socket and mirrors an http/https origin", () => {
    expect(levelUrlFrom("wss://play.example.com/play")).toBe("https://play.example.com/level");
    expect(levelUrlFrom("http://127.0.0.1:5088/snapshots")).toBe("http://127.0.0.1:5088/level");
    expect(levelUrlFrom("https://play.example.com/play")).toBe("https://play.example.com/level");
  });

  it("keeps a relative socket URL relative (the dev proxy)", () => {
    expect(levelUrlFrom("/play")).toBe("/level");
    expect(levelUrlFrom("  /snapshots  ")).toBe("/level");
    expect(levelUrlFrom("")).toBe("/level");
  });
});

describe("fetchLevel", () => {
  it("fetches the derived URL and decodes the document", async () => {
    const fetcher = vi.fn(async () => response(level));
    expect(await fetchLevel("ws://127.0.0.1:5088/play", fetcher as unknown as typeof fetch)).toEqual(level);
    expect(fetcher).toHaveBeenCalledWith("http://127.0.0.1:5088/level", { headers: { accept: "application/json" } });
  });

  it("returns null for a malformed document, a missing endpoint and a network failure", async () => {
    const malformed = vi.fn(async () => response({ format: "pigforge.level-content" }));
    expect(await fetchLevel("ws://127.0.0.1:5088/play", malformed as unknown as typeof fetch)).toBeNull();

    const missing = vi.fn(async () => response("not found", false));
    expect(await fetchLevel("ws://127.0.0.1:5088/play", missing as unknown as typeof fetch)).toBeNull();

    const failing = vi.fn(async () => { throw new Error("offline"); });
    expect(await fetchLevel("ws://127.0.0.1:5088/play", failing as unknown as typeof fetch)).toBeNull();
  });
});
