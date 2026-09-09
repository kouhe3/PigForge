import { readFileSync } from "node:fs";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";
import { describe, expect, it } from "vitest";
import { PLAY_PARTS } from "./slope";

const root = join(dirname(fileURLToPath(import.meta.url)), "../../../../");
const content = JSON.parse(readFileSync(join(root, "content/parts.json"), "utf8")) as {
  parts: Array<Record<string, unknown>>;
};

describe("PLAY_PARTS", () => {
  // The client inlines the part table for its offline slope builder. Drift from the
  // server's content/parts.json silently renders and simulates the wrong part, so the
  // two must agree on every field the builder and renderer read.
  it("matches content/parts.json for every declared part", () => {
    expect(PLAY_PARTS.parts.length).toBeGreaterThan(0);
    for (const part of PLAY_PARTS.parts) {
      const server = content.parts.find((candidate) => candidate.partTypeId === part.partTypeId);
      expect(server, `part ${part.partTypeId} missing from content/parts.json`).toBeDefined();
      expect(part.mode, `part ${part.partTypeId} mode`).toBe(server?.mode);
      expect(part.mass, `part ${part.partTypeId} mass`).toBe(server?.mass);
      expect(part.shapes, `part ${part.partTypeId} shapes`).toEqual(server?.shapes);
      expect(part.capabilities ?? {}, `part ${part.partTypeId} capabilities`).toEqual(server?.capabilities ?? {});
    }
  });

  it("declares no part the server content does not know", () => {
    for (const part of PLAY_PARTS.parts) {
      expect(content.parts.some((candidate) => candidate.partTypeId === part.partTypeId), `unknown part ${part.partTypeId}`).toBe(true);
    }
  });
});
