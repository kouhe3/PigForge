import { readFileSync } from "node:fs";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";
import { describe, expect, it } from "vitest";
import { PLAY_PARTS } from "./slope";
import type { PartContentDocument } from "@/schema/types";

const root = join(dirname(fileURLToPath(import.meta.url)), "../../../../");
const content = JSON.parse(readFileSync(join(root, "content/parts.json"), "utf8")) as PartContentDocument;

describe("PLAY_PARTS", () => {
  // The client bundles a generated copy of the server's content/parts.json. Drift means the
  // palette and the offline slope builder render parts the server does not know, so the two
  // must agree; regenerate with tools/web-parts/generate-play-parts.mjs.
  it("deep-equals content/parts.json", () => {
    expect(PLAY_PARTS).toEqual(content);
  });

  it("declares every part the server content declares", () => {
    expect(PLAY_PARTS.parts.length).toBe(content.parts.length);
  });
});
