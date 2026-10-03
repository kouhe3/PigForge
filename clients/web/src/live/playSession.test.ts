import { describe, expect, it } from "vitest";
import { createSessionId, withSessionParam } from "./playSession";

describe("playSession", () => {
  it("creates an opaque id the host accepts", () => {
    const first = createSessionId();
    const second = createSessionId();

    // PlaySessions.IsWellFormed: 16..64 characters from [A-Za-z0-9_-], never a bare number.
    expect(first).toMatch(/^[0-9a-zA-Z_-]{16,64}$/);
    expect(second).toMatch(/^[0-9a-zA-Z_-]{16,64}$/);
    expect(first).not.toBe(second);
  });

  it("appends the session parameter to a bare play URL", () => {
    expect(withSessionParam("ws://127.0.0.1:5088/play", "abc123def456ghi7")).toBe(
      "ws://127.0.0.1:5088/play?session=abc123def456ghi7",
    );
  });

  it("keeps an existing query and replaces an existing session value", () => {
    expect(withSessionParam("ws://127.0.0.1:5088/play?room=1", "abc123def456ghi7")).toBe(
      "ws://127.0.0.1:5088/play?room=1&session=abc123def456ghi7",
    );
    expect(withSessionParam("ws://127.0.0.1:5088/play?session=stale&room=1", "abc123def456ghi7")).toBe(
      "ws://127.0.0.1:5088/play?session=abc123def456ghi7&room=1",
    );
  });

  it("leaves a URL it cannot parse alone, so the socket still connects as a new player", () => {
    expect(withSessionParam("/play", "abc123def456ghi7")).toBe("/play");
  });
});
