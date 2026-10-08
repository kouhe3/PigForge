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
    expectRejected({ schemaVersion: 5 }, "versions 1 to 4");
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

  /**
   * v3 is the version that describes every `e2dTerrain` the original ships: the `hasCollider` bit
   * (498 of the 2146 terrains are decoration) and the ground's fill (docs/specs/level-terrain-
   * visuals.md). The two shapes are not interchangeable: the same document is rejected either way.
   */
  it("accepts a v3 terrain with its collider bit and fill", () => {
    expect(validateLevelContent({
      ...structuredClone(v2),
      schemaVersion: 3,
      terrain: [{
        position: [-2.79, 9.02, 0],
        depth: 10,
        collider: false,
        fill: { texture: "Ground_Rocks_Texture.png", color: [131, 131, 131, 255], tileOffset: [0, 6.2], tileSize: [5, 5] },
        loops: [[[0, 0], [4, 0], [0, 3]]],
      }],
    })).toEqual([]);
  });

  it("rejects a v3 terrain without its collider bit or fill", () => {
    const fill = { texture: "Ground_Rocks_Texture.png", color: [255, 255, 255, 255], tileOffset: [0, 6.2], tileSize: [5, 5] };
    const loops = [[[0, 0], [4, 0], [0, 3]]];
    expect(validateLevelContent({ ...structuredClone(v2), schemaVersion: 3, terrain: [{ position: [0, 0, 0], depth: 10, collider: true, loops }] }))
      .toContain("root.terrain[0]: missing required property 'fill'.");
    expect(validateLevelContent({ ...structuredClone(v2), schemaVersion: 3, terrain: [{ position: [0, 0, 0], depth: 10, fill, loops }] }))
      .toContain("root.terrain[0]: missing required property 'collider'.");
    expect(validateLevelContent({ ...structuredClone(v2), schemaVersion: 3, terrain: [{ position: [0, 0, 0], depth: 10, collider: "yes", fill, loops }] }))
      .toContain("root.terrain[0].collider: must be a boolean.");
  });

  it("rejects the v3 fields on an older document", () => {
    expectRejected({ terrain: [{ ...v2.terrain[0], collider: true }] }, "collider bit and a fill are v3-only");
    expectRejected({ terrain: [{ ...v2.terrain[0], fill: { texture: "a.png", color: [1, 2, 3, 4], tileOffset: [0, 0], tileSize: [5, 5] } }] }, "v3-only");
  });

  it("rejects a malformed fill", () => {
    const withFill = (fill: unknown): Record<string, unknown> => ({
      ...structuredClone(v2),
      schemaVersion: 3,
      terrain: [{ position: [0, 0, 0], depth: 10, collider: true, fill, loops: [[[0, 0], [4, 0], [0, 3]]] }],
    });
    const good = { texture: "a.png", color: [1, 2, 3, 4], tileOffset: [0, 0], tileSize: [5, 5] };
    expect(validateLevelContent(withFill("a.png"))).toContain("root.terrain[0].fill: must be a JSON object.");
    expect(validateLevelContent(withFill({ ...good, texture: " padded.png" }))).toContain("root.terrain[0].fill.texture: must be 1 to 128 non-whitespace-padded characters.");
    expect(validateLevelContent(withFill({ ...good, color: [1, 2, 3] }))).toContain("root.terrain[0].fill.color: must be four bytes [r, g, b, a].");
    expect(validateLevelContent(withFill({ ...good, color: [1, 2, 3, 256] }))).toContain("root.terrain[0].fill.color: must be four bytes [r, g, b, a].");
    expect(validateLevelContent(withFill({ ...good, tileOffset: [0] }))).toContain("root.terrain[0].fill.tileOffset: must be [x, y] with finite numbers.");
    expect(validateLevelContent(withFill({ ...good, tileSize: [5, 0] }))).toContain("root.terrain[0].fill.tileSize: must be [w, h] with positive finite numbers.");
    expect(validateLevelContent(withFill({ ...good, extra: 1 }))).toContain("root.terrain[0].fill: unknown property 'extra'.");
  });
});

/** A v4 terrain's `curve`: two nodes, the stripe row below them and two texture layers. */
const curve = {
  nodes: [[0, 0], [2, 0]],
  stripe: [[0, -1], [2, -1]],
  textures: [
    { texture: "Curve_Grass_Texture.png", wrap: "repeat" },
    { texture: "Curve_Rock_Texture.png", wrap: "clamp" },
  ],
  uScale: 0.001953125,
  splat1: [[1, 1]],
};

/** The v4 document: the v3 terrain shape plus the required curve. */
const v4 = {
  ...structuredClone(v2),
  schemaVersion: 4,
  terrain: [{
    position: [-2.79, 9.02, 0],
    depth: 10,
    collider: true,
    fill: { texture: "Ground_Rocks_Texture.png", color: [131, 131, 131, 255], tileOffset: [0, 6.2], tileSize: [5, 5] },
    curve,
    loops: [[[0, 0], [4, 0], [0, 3]]],
  }],
};

/** The v4 document with its terrain's `curve` replaced by `value`. */
function withCurve(value: unknown): string[] {
  const document = structuredClone(v4) as { terrain: Array<Record<string, unknown>> };
  document.terrain[0].curve = value;
  return validateLevelContent(document);
}

/** The v4 document with `overrides` merged into its terrain's curve. */
function expectCurveRejected(overrides: Record<string, unknown>, fragment: string): void {
  expect(withCurve({ ...structuredClone(curve), ...overrides }).some((error) => error.includes(fragment))).toBe(true);
}

describe("validateLevelContent curve", () => {
  it("accepts a v4 terrain with its collider bit, fill and curve", () => {
    expect(validateLevelContent(v4)).toEqual([]);
  });

  it("rejects a v4 terrain without its curve, collider bit or fill", () => {
    const deleteTerrainKey = (key: string): string[] => {
      const document = structuredClone(v4) as { terrain: Array<Record<string, unknown>> };
      delete document.terrain[0][key];
      return validateLevelContent(document);
    };
    expect(deleteTerrainKey("curve")).toContain("root.terrain[0]: missing required property 'curve'.");
    expect(deleteTerrainKey("fill")).toContain("root.terrain[0]: missing required property 'fill'.");
    expect(deleteTerrainKey("collider")).toContain("root.terrain[0]: missing required property 'collider'.");
  });

  it("rejects the curve on an older document", () => {
    for (const schemaVersion of [1, 2, 3]) {
      const document = structuredClone(v4) as Record<string, unknown>;
      document.schemaVersion = schemaVersion;
      expect(validateLevelContent(document)).toContain("root.terrain[0]: a curve is v4-only.");
    }
  });

  it("rejects a curve that is not an object, an unknown key or a missing key", () => {
    expect(withCurve(3)).toContain("root.terrain[0].curve: must be a JSON object.");
    expect(withCurve({ ...structuredClone(curve), fill: 1 })).toContain("root.terrain[0].curve: unknown property 'fill'.");
    expect(withCurve({
      nodes: [[0, 0], [2, 0]],
      stripe: [[0, -1], [2, -1]],
      textures: curve.textures,
      uScale: 1,
    })).toContain("root.terrain[0].curve: missing required property 'splat1'.");
  });

  it("rejects rows that are not arrays of two or more [x, y] points", () => {
    expectCurveRejected({ nodes: 3 }, "root.terrain[0].curve.nodes: must be an array of [x, y] points.");
    expectCurveRejected({ nodes: [[0, 0]] }, "needs at least two points");
    expectCurveRejected({ stripe: 3 }, "root.terrain[0].curve.stripe: must be an array of [x, y] points.");
    expectCurveRejected({ nodes: [[0, 0], [2, "0"]] }, "a curve point must be [x, y] with finite numbers");
    expectCurveRejected({ nodes: [[0, 0], [2, 0], [3, 0]], stripe: [[0, -1], [2, -1]] }, "must have the same length");
  });

  it("rejects textures that are not exactly two layers", () => {
    expectCurveRejected({ textures: [curve.textures[0]] }, "must be exactly two { texture, wrap } layers");
    expectCurveRejected({ textures: [curve.textures[0], curve.textures[0], curve.textures[1]] }, "must be exactly two");
    expectCurveRejected({ textures: "textures" }, "must be exactly two");
  });

  it("rejects a malformed layer, an unknown key and a bad wrap", () => {
    expectCurveRejected({ textures: [3, curve.textures[1]] }, "root.terrain[0].curve.textures[0]: must be a JSON object.");
    expectCurveRejected({ textures: [{ ...curve.textures[0], size: 512 }, curve.textures[1]] }, "unknown property 'size'");
    expectCurveRejected({ textures: [{ texture: "a.png", wrap: "mirror" }, curve.textures[1]] }, "wrap: must be 'repeat' or 'clamp'");
    expectCurveRejected({ textures: [{ texture: "a.png" }, curve.textures[1]] }, "missing required property 'wrap'");
  });

  it("rejects a texture name that is empty, padded, whitespace-ridden or too long", () => {
    const withName = (texture: string): string[] => withCurve({
      ...structuredClone(curve),
      textures: [{ texture, wrap: "repeat" }, curve.textures[1]],
    });
    expect(withName("")).toContain("root.terrain[0].curve.textures[0].texture: must be 1 to 128 characters with no whitespace.");
    const message = "root.terrain[0].curve.textures[0].texture: must be 1 to 128 characters with no whitespace.";
    expect(withName(" padded.png")).toContain(message);
    expect(withName("two words.png")).toContain(message);
    expect(withName(`${"a".repeat(129)}.png`)).toContain(message);
  });

  it("rejects a uScale that is not finite and positive", () => {
    expectCurveRejected({ uScale: 0 }, "uScale: must be a finite positive number");
    expectCurveRejected({ uScale: -1 }, "uScale: must be a finite positive number");
    expectCurveRejected({ uScale: "1" }, "uScale: must be a finite positive number");
  });

  it("rejects splat1 runs that are unsorted, overlapping, out of range or malformed", () => {
    expectCurveRejected({ splat1: "runs" }, "splat1: must be an array of [start, count] runs.");
    expectCurveRejected({ splat1: [[0]] }, "a run must be [start, count]");
    expectCurveRejected({ splat1: [[0, 0]] }, "start must be a non-negative integer and count a positive one");
    expectCurveRejected({ splat1: [[0.5, 1]] }, "start must be a non-negative integer and count a positive one");
    expectCurveRejected({ splat1: [[1, 1], [0, 1]] }, "runs must be sorted strictly by start");
    expectCurveRejected({ splat1: [[0, 1], [0, 1]] }, "runs must be sorted strictly by start");
    expectCurveRejected({ splat1: [[0, 2], [1, 1]] }, "runs must not overlap");
    expectCurveRejected({ splat1: [[1, 2]] }, "must stay within the 2 curve nodes");
  });

  it("accepts disjoint sorted runs and an empty splat1", () => {
    expect(withCurve({ ...structuredClone(curve), nodes: [[0, 0], [2, 0], [4, 0]], stripe: [[0, -1], [2, -1], [4, -1]], splat1: [[0, 1], [2, 1]] }))
      .toEqual([]);
    expect(withCurve({ ...structuredClone(curve), splat1: [] })).toEqual([]);
  });
});

describe("zoneRect", () => {
  it("takes the x/y extent of the zone and ignores z", () => {
    expect(zoneRect({ min: [12, -0.5, -2], max: [16, 2.5, 2] })).toEqual({ minX: 12, minY: -0.5, maxX: 16, maxY: 2.5 });
  });
});
