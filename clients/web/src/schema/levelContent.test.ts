import { readFileSync } from "node:fs";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";
import { describe, expect, it } from "vitest";
import { decodeLevelContent, validateLevelContent, zoneRect } from "./levelContent";

/** The v2 document from the client brief: one terrain outline, one spawn, a goal zone and bounds. */
const v2 = {
  format: "pigforge.level-content",
  schemaVersion: 2,
  contentVersion: "episode_1_level_05",
  goalZone: { min: [12, -0.5, -2], max: [16, 2.5, 2] },
  bounds: { min: [-30, -12, -8], max: [30, 30, 8] },
  spawns: [{ partTypeId: 6, position: [-18.125, -1.758, 0], angle: 0.25, role: "part" }],
  terrain: [{ position: [-2.79, 9.02, 0], depth: 10, loops: [[[0, 0], [4, 0], [0, 3]]] }],
};

/** The exact JSON the server parsed, so the fixture is the same bytes the endpoint serves. */
const v1 = JSON.parse(
  readFileSync(join(dirname(fileURLToPath(import.meta.url)), "../../../../content/levels/terrain-v1.json"), "utf8"),
) as unknown;

/** The v2 document with `overrides` merged in and one required root property removed. */
function without(key: keyof typeof v2): string[] {
  const document = structuredClone(v2) as Record<string, unknown>;
  delete document[key];
  return validateLevelContent(document);
}

/** Asserts the v2 document with `overrides` merged in is rejected with `fragment` in an error. */
function expectRejected(overrides: Record<string, unknown>, fragment: string): void {
  const errors = validateLevelContent({ ...structuredClone(v2), ...overrides });
  expect(errors.some((error) => error.includes(fragment))).toBe(true);
}

describe("validateLevelContent", () => {
  it("accepts the v2 document", () => {
    expect(validateLevelContent(v2)).toEqual([]);
  });

  it("accepts the v1 fixture, which has no terrain", () => {
    expect(validateLevelContent(v1)).toEqual([]);
  });

  it("accepts a v1 document that carries v2 terrain anyway (the server parser does)", () => {
    expect(validateLevelContent({ ...structuredClone(v2), schemaVersion: 1 })).toEqual([]);
  });

  it("decodes the accepted document and nulls a rejected one", () => {
    expect(decodeLevelContent(v2).level?.contentVersion).toBe("episode_1_level_05");
    const decoded = decodeLevelContent({ format: "x" });
    expect(decoded.level).toBeNull();
    expect(decoded.errors.length).toBeGreaterThan(0);
  });
});

describe("validateLevelContent root", () => {
  it("rejects a non-object", () => {
    expect(validateLevelContent(null)).toEqual(["Level content document is required."]);
    expect(validateLevelContent([])).toEqual(["Level content document is required."]);
  });

  it("rejects the wrong format", () => {
    expectRejected({ format: "pigforge.part-content" }, "pigforge.level-content");
  });

  it("rejects an unsupported schema version", () => {
    expectRejected({ schemaVersion: 3 }, "versions 1 to 2");
  });

  it("rejects an empty, padded or non-string contentVersion", () => {
    expectRejected({ contentVersion: "" }, "contentVersion");
    expectRejected({ contentVersion: " padded " }, "contentVersion");
    expectRejected({ contentVersion: 42 }, "contentVersion");
  });

  it("rejects an unknown property", () => {
    expectRejected({ gravity: 9.81 }, "unknown property 'gravity'");
  });

  it("rejects every missing required property", () => {
    for (const key of ["format", "schemaVersion", "contentVersion", "goalZone", "bounds", "spawns"] as const) {
      expect(without(key).some((error) => error.includes(`missing required property '${key}'`))).toBe(true);
    }
  });

  it("rejects a non-array spawns or terrain", () => {
    expectRejected({ spawns: {} }, "spawns: must be an array");
    expectRejected({ terrain: {} }, "terrain: must be an array");
  });
});

describe("validateLevelContent zones", () => {
  it("rejects an inverted goalZone and bounds", () => {
    expectRejected({ goalZone: { min: [16, -0.5, -2], max: [12, 2.5, 2] } }, "greater than or equal to min");
    expectRejected({ bounds: { min: [-30, -12, -8], max: [30, -12.5, 8] } }, "root.bounds");
  });

  it("rejects a zone that is not an object, has an unknown key, or a malformed vector", () => {
    expectRejected({ goalZone: [12, 16] }, "zone must be a JSON object");
    expectRejected({ goalZone: { min: [12, -0.5, -2], max: [16, 2.5, 2], height: 1 } }, "unknown property 'height'");
    expectRejected({ goalZone: { min: [12, -0.5], max: [16, 2.5, 2] } }, "three finite numbers");
    expectRejected({ goalZone: { min: [12, "-0.5", -2], max: [16, 2.5, 2] } }, "three finite numbers");
    expectRejected({ goalZone: { max: [16, 2.5, 2] } }, "missing required property 'min'");
  });
});

describe("validateLevelContent spawns", () => {
  it("rejects a spawn that is not an object, an unknown key and a missing position", () => {
    expectRejected({ spawns: ["part"] }, "spawn must be a JSON object");
    expectRejected({ spawns: [{ ...v2.spawns[0], sprite: 1 }] }, "unknown property 'sprite'");
    expectRejected({ spawns: [{ partTypeId: 6 }] }, "missing required property 'position'");
  });

  it("rejects invalid spawn fields", () => {
    expectRejected({ spawns: [{ ...v2.spawns[0], partTypeId: 0 }] }, "positive 32-bit integer");
    expectRejected({ spawns: [{ ...v2.spawns[0], partTypeId: 1.5 }] }, "positive 32-bit integer");
    expectRejected({ spawns: [{ ...v2.spawns[0], position: [1, 2] }] }, "three finite numbers");
    expectRejected({ spawns: [{ ...v2.spawns[0], angle: "0.35" }] }, "angle: must be a finite number");
    expectRejected({ spawns: [{ ...v2.spawns[0], role: "cow" }] }, "role: must be 'part', 'pig' or 'tnt'");
    expectRejected({ spawns: [{ ...v2.spawns[0], tntFuseTicks: 70000 }] }, "tntFuseTicks");
    expectRejected({ spawns: [{ ...v2.spawns[0], motorImpulsePerTick: null }] }, "motorImpulsePerTick");
    expectRejected({ spawns: [{ ...v2.spawns[0], motorDirectionX: 2 }] }, "motorDirectionX: must be -1, 0 or 1");
    expectRejected({ spawns: [{ ...v2.spawns[0], wheel: "true" }] }, "wheel: must be a boolean");
  });

  it("accepts the optional spawn overrides", () => {
    expect(validateLevelContent({
      ...structuredClone(v2),
      spawns: [{ partTypeId: 9, position: [0, 0, 0], role: "tnt", tntFuseTicks: 120, motorImpulsePerTick: 2, motorDirectionX: -1, wheel: false, angle: 1 }],
    })).toEqual([]);
  });
});

describe("validateLevelContent terrain", () => {
  it("rejects a terrain that is not an object, an unknown key and a missing loop", () => {
    expectRejected({ terrain: [10] }, "terrain must be a JSON object");
    expectRejected({ terrain: [{ ...v2.terrain[0], textureIndex: 1 }] }, "unknown property 'textureIndex'");
    expectRejected({ terrain: [{ position: [-2.79, 9.02, 0], depth: 10 }] }, "missing required property 'loops'");
  });

  it("rejects a non-positive depth and a malformed position", () => {
    expectRejected({ terrain: [{ ...v2.terrain[0], depth: 0 }] }, "depth: must be a finite positive number");
    expectRejected({ terrain: [{ ...v2.terrain[0], depth: -1 }] }, "depth: must be a finite positive number");
    expectRejected({ terrain: [{ ...v2.terrain[0], position: [0, 0] }] }, "position: must be an array of three finite numbers");
  });

  it("rejects an empty loop list and a loop with fewer than three usable points", () => {
    expectRejected({ terrain: [{ ...v2.terrain[0], loops: [] }] }, "at least one outline loop");
    expectRejected({ terrain: [{ ...v2.terrain[0], loops: [[[0, 0], [1, 1]]] }] }, "at least three points");
    // A malformed point does not count towards the three, exactly like the server parser.
    expectRejected({ terrain: [{ ...v2.terrain[0], loops: [[[0, 0], [1, 1], "x"]] }] }, "at least three points");
    expectRejected({ terrain: [{ ...v2.terrain[0], loops: [[[0, 0], [1, 1], 7]] }] }, "outline point must be");
  });

  it("rejects a loop that is not an array and a point that is not [x, y]", () => {
    expectRejected({ terrain: [{ ...v2.terrain[0], loops: [3] }] }, "outline loop must be an array");
    expectRejected({ terrain: [{ ...v2.terrain[0], loops: [[[0, 0, 0], [1, 0], [0, 1]]] }] }, "outline point must be [x, y]");
    expectRejected({ terrain: [{ ...v2.terrain[0], loops: [[[0, 0], [1, "0"], [0, 1]]] }] }, "finite numbers");
  });

  it("accepts several loops and several terrains", () => {
    expect(validateLevelContent({
      ...structuredClone(v2),
      terrain: [
        { position: [0, 0, 0], depth: 10, loops: [[[0, 0], [1, 0], [1, 1]], [[2, 2], [3, 2], [3, 3], [2, 3]]] },
        { position: [5, 5, 0], depth: 2.5, loops: [[[0, 0], [1, 0], [0, 1]]] },
      ],
    })).toEqual([]);
  });
});

describe("zoneRect", () => {
  it("takes the x/y extent of the zone and ignores z", () => {
    expect(zoneRect({ min: [12, -0.5, -2], max: [16, 2.5, 2] })).toEqual({ minX: 12, minY: -0.5, maxX: 16, maxY: 2.5 });
  });
});
