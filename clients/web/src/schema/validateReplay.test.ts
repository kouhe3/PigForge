import { readFileSync } from "node:fs";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";
import { describe, expect, it } from "vitest";
import { validateReplay } from "./validateReplay";
import { validatePartContent } from "./validateContent";

const root = join(dirname(fileURLToPath(import.meta.url)), "../../../../");

describe("validateReplay", () => {
  it("accepts the v2 fixture", () => {
    const replay = JSON.parse(readFileSync(join(dirname(fileURLToPath(import.meta.url)), "../../fixtures/replay-v2.json"), "utf8"));
    expect(validateReplay(replay)).toEqual([]);
  });

  it("rejects the wrong protocol version", () => {
    const replay = JSON.parse(readFileSync(join(dirname(fileURLToPath(import.meta.url)), "../../fixtures/replay-v2.json"), "utf8"));
    replay.header.protocolVersion = 1;
    expect(validateReplay(replay).some((error) => error.includes("unsupported"))).toBe(true);
  });

  it("rejects a GUID-like part field", () => {
    expect(validatePartContent({
      format: "pigforge.part-content",
      schemaVersion: 1,
      contentVersion: "x",
      parts: [{ partTypeId: 1, name: "a", mode: "dynamic", mass: 1, guid: "abc", shapes: [{ kind: "box", halfExtents: [0.5, 0.5, 0.5] }] }],
    }).some((error) => error.includes("GUID"))).toBe(true);
  });

  it("accepts sample part content", () => {
    const content = JSON.parse(readFileSync(join(root, "content/parts.json"), "utf8"));
    expect(validatePartContent(content)).toEqual([]);
  });
});
