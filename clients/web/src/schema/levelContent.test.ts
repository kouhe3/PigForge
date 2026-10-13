import { readFileSync } from "node:fs";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";
import { describe, expect, it } from "vitest";
import {
  cameraLimitsRect,
  constructionCellExists,
  constructionGridShape,
  decodeLevelContent,
  timeLimit,
  validateLevelContent,
  zoneRect,
} from "./levelContent";

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
    expectRejected({ schemaVersion: 12 }, "root.schemaVersion");
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

/** The v5 document: the v4 shape plus the level's own camera limits (the pig's real bound). */
const v5 = {
  ...structuredClone(v4),
  schemaVersion: 5,
  cameraLimits: { topLeft: [-10.46, 13.45], size: [56.3, 24.7] },
};

/** The v5 document with its `cameraLimits` replaced by `value`. */
function withCameraLimits(value: unknown): string[] {
  return validateLevelContent({ ...structuredClone(v5), cameraLimits: value });
}

describe("validateLevelContent camera limits", () => {
  it("accepts a v5 document with the level's camera limits", () => {
    expect(validateLevelContent(v5)).toEqual([]);
  });

  it("requires cameraLimits on a v5 document and refuses it on an older one", () => {
    const missing = structuredClone(v5) as Record<string, unknown>;
    delete missing.cameraLimits;
    expect(validateLevelContent(missing)).toContain("root.cameraLimits: required on a schemaVersion 5 document.");
    for (const schemaVersion of [1, 2, 3, 4]) {
      const older = structuredClone(v5) as Record<string, unknown>;
      older.schemaVersion = schemaVersion;
      expect(validateLevelContent(older).some((error) => error.startsWith("root.cameraLimits:"))).toBe(true);
    }
  });

  it("rejects a malformed camera rectangle", () => {
    expect(withCameraLimits(3)).toContain("root.cameraLimits: camera limits must be a JSON object.");
    expect(withCameraLimits({ topLeft: [0, 0], size: [1, 1], extra: 1 }))
      .toContain("root.cameraLimits: unknown property 'extra'.");
    expect(withCameraLimits({ topLeft: [0, 0] }))
      .toContain("root.cameraLimits: missing required property 'size'.");
    expect(withCameraLimits({ topLeft: [0], size: [1, 1] }))
      .toContain("root.cameraLimits.topLeft: must be [x, y] with finite numbers.");
    expect(withCameraLimits({ topLeft: [0, Number.NaN], size: [1, 1] }))
      .toContain("root.cameraLimits.topLeft: must be [x, y] with finite numbers.");
    expect(withCameraLimits({ topLeft: [0, 0], size: [0, 1] }))
      .toContain("root.cameraLimits.size: must be positive on both axes.");
  });
});

/** The v6 document: the v5 one plus the level's own decoration instances. */
const v6 = {
  ...structuredClone(v5),
  schemaVersion: 6,
  props: [
    { id: "Star_01", x: 37.530018, y: -7.0505567, z: -5, rotation: 0, scaleX: -1, scaleY: 1 },
    { id: "Grass_06", x: 8.645143, y: 4.4807925, z: -5, rotation: 6.060668, scaleX: 1, scaleY: 1 },
  ],
};

/** The v6 document with its `props` replaced by `value`. */
function withProps(value: unknown): string[] {
  return validateLevelContent({ ...structuredClone(v6), props: value });
}

describe("validateLevelContent props", () => {
  it("accepts a v6 document with the level's decorations", () => {
    expect(validateLevelContent(v6)).toEqual([]);
  });

  it("requires props on a v6 document and refuses them on an older one", () => {
    const missing = structuredClone(v6) as Record<string, unknown>;
    delete missing.props;
    expect(validateLevelContent(missing)).toContain("root.props: required on a schemaVersion 6 document.");
    for (const schemaVersion of [1, 2, 3, 4, 5]) {
      const older = structuredClone(v6) as Record<string, unknown>;
      older.schemaVersion = schemaVersion;
      expect(validateLevelContent(older).some((error) => error.startsWith("root.props:"))).toBe(true);
    }
  });

  it("rejects a malformed decoration", () => {
    expect(withProps(3)).toContain("root.props: must be an array.");
    expect(withProps([3])).toContain("root.props[0]: prop must be a JSON object.");
    expect(withProps([{ id: "", x: 0, y: 0, z: 0, rotation: 0, scaleX: 1, scaleY: 1 }]))
      .toContain("root.props[0].id: must be a non-empty string without whitespace.");
    expect(withProps([{ id: "Grass 01", x: 0, y: 0, z: 0, rotation: 0, scaleX: 1, scaleY: 1 }]))
      .toContain("root.props[0].id: must be a non-empty string without whitespace.");
    expect(withProps([{ id: "Grass_01", x: 0, y: 0, z: 0, rotation: 0, scaleX: 1 }]))
      .toContain("root.props[0]: missing required property 'scaleY'.");
    expect(withProps([{ id: "Grass_01", x: 0, y: 0, z: 0, rotation: 0, scaleX: 1, scaleY: 1, extra: 1 }]))
      .toContain("root.props[0]: unknown property 'extra'.");
    expect(withProps([{ id: "Grass_01", x: Number.NaN, y: 0, z: 0, rotation: 0, scaleX: 1, scaleY: 1 }]))
      .toContain("root.props[0].x: must be a finite number.");
  });
});

/** The v7 document: the v6 one with its first prop carrying the original's own solid box. */
const v7 = {
  ...structuredClone(v6),
  schemaVersion: 7,
  props: [
    {
      ...structuredClone(v6.props[0]),
      id: "TNT_Box",
      boxes: [{ dx: 0, dy: 0, dz: 0, sizeX: 0.95, sizeY: 0.95, sizeZ: 0.95, rotation: 0 }],
    },
    structuredClone(v6.props[1]),
  ],
};

describe("validateLevelContent solid boxes", () => {
  it("accepts a v7 document whose prop carries a box", () => {
    expect(validateLevelContent(v7)).toEqual([]);
  });

  it("accepts a v7 document whose props carry none", () => {
    const bare = structuredClone(v7) as Record<string, unknown>;
    bare.props = structuredClone(v6.props);
    expect(validateLevelContent(bare)).toEqual([]);
  });

  it("refuses boxes on a v6 document and malformed boxes on a v7 one", () => {
    const older = structuredClone(v7) as Record<string, unknown>;
    older.schemaVersion = 6;
    expect(validateLevelContent(older)).toContain("root.props[0]: unknown property 'boxes'.");

    const malformed = (boxes: unknown): string[] =>
      validateLevelContent({ ...structuredClone(v7), props: [{ ...structuredClone(v7.props[0]), boxes }] });
    expect(malformed(3)).toContain("root.props[0].boxes: must be an array.");
    expect(malformed([{ dx: 0, dy: 0, dz: 0, sizeX: 0, sizeY: 1, sizeZ: 1, rotation: 0 }]))
      .toContain("root.props[0].boxes[0].sizeX: a solid box needs a positive size.");
    expect(malformed([{ dx: 0, dy: 0, dz: 0, sizeX: 1, sizeY: 1 }]))
      .toContain("root.props[0].boxes[0]: missing required property 'sizeZ'.");
    expect(malformed([{ dx: 0, dy: 0, dz: 0, sizeX: 1, sizeY: 1, sizeZ: 1, rotation: 0, extra: 1 }]))
      .toContain("root.props[0].boxes[0]: unknown property 'extra'.");
  });

  it("requires the level's props on a v7 document", () => {
    const missing = structuredClone(v7) as Record<string, unknown>;
    delete missing.props;
    expect(validateLevelContent(missing)).toContain("root.props: required on a schemaVersion 7 document.");
  });
});

/** The v8 document: the v7 one with its first prop carrying the original's own trigger box. */
const v8 = {
  ...structuredClone(v7),
  schemaVersion: 8,
  props: [
    {
      ...structuredClone(v7.props[0]),
      triggers: [{ dx: 0, dy: 0, dz: 0, sizeX: 3.81, sizeY: 9.650253, sizeZ: 0, rotation: 0 }],
    },
    structuredClone(v7.props[1]),
  ],
};

describe("validateLevelContent trigger boxes", () => {
  it("accepts a v8 document whose prop carries a trigger", () => {
    expect(validateLevelContent(v8)).toEqual([]);
  });

  it("refuses triggers before v8 and malformed triggers on v8", () => {
    const older = structuredClone(v8) as Record<string, unknown>;
    older.schemaVersion = 7;
    expect(validateLevelContent(older)).toContain("root.props[0]: unknown property 'triggers'.");

    const malformed = (triggers: unknown): string[] =>
      validateLevelContent({ ...structuredClone(v8), props: [{ ...structuredClone(v8.props[0]), triggers }] });
    expect(malformed(3)).toContain("root.props[0].triggers: must be an array.");
    expect(malformed([{ dx: 0, dy: 0, dz: 0, sizeX: 0, sizeY: 1, sizeZ: 1, rotation: 0 }]))
      .toContain("root.props[0].triggers[0].sizeX: a solid box needs a positive size.");
  });

  it("rejects an unsupported schema version past v11", () => {
    expectRejected({ schemaVersion: 12 }, "root.schemaVersion");
  });
});

describe("cameraLimitsRect", () => {
  it("turns topLeft + size into the rectangle the original builds", () => {
    const rect = cameraLimitsRect({ topLeft: [-10.46, 13.45], size: [56.3, 24.7] });
    expect(rect.minX).toBeCloseTo(-10.46);
    expect(rect.maxX).toBeCloseTo(45.84);
    expect(rect.maxY).toBeCloseTo(13.45);
    // size extends *down* from topLeft (`IngameCamera.cs:599` builds the rect that way).
    expect(rect.minY).toBeCloseTo(-11.25);
  });
});

/** The v9 document: the v8 one plus the level's own construction limits (`build`). */
const v9 = {
  ...structuredClone(v8),
  schemaVersion: 9,
  build: {
    origin: [2.4379761, 5.4575901],
    rows: [15, 15, 0, 0],
    partCounts: [
      { partTypeId: 4, count: 1 },
      { partTypeId: 1, count: 3 },
    ],
  },
};

/** The v9 document with its `build` replaced by `value`. */
function withBuild(value: unknown): string[] {
  return validateLevelContent({ ...structuredClone(v9), build: value });
}

describe("validateLevelContent build limits", () => {
  it("accepts a v9 document with the level's construction limits", () => {
    expect(validateLevelContent(v9)).toEqual([]);
  });

  it("accepts a v9 document with no build (the server's nullable limits)", () => {
    const bare = structuredClone(v9) as Record<string, unknown>;
    delete bare.build;
    expect(validateLevelContent(bare)).toEqual([]);
    expect(decodeLevelContent(bare).level?.build).toBeUndefined();
  });

  it("refuses build before v9", () => {
    for (const schemaVersion of [1, 2, 3, 4, 5, 6, 7, 8]) {
      const older = { ...structuredClone(v9), schemaVersion };
      expect(validateLevelContent(older))
        .toContain("root.build: only a schemaVersion 9 document carries the level's construction limits.");
    }
  });

  it("rejects a malformed build block", () => {
    expect(withBuild(3)).toContain("root.build: must be a JSON object.");
    expect(withBuild({})).toContain("root.build: missing required property 'origin'.");
    expect(withBuild({ origin: [0, 0], rows: [1], partCounts: [], extra: 1 }))
      .toContain("root.build: unknown property 'extra'.");
    expect(withBuild({ origin: [0], rows: [1], partCounts: [] }))
      .toContain("root.build.origin: must be [x, y] with finite numbers.");
    expect(withBuild({ origin: [0, Number.NaN], rows: [1], partCounts: [] }))
      .toContain("root.build.origin: must be [x, y] with finite numbers.");
    expect(withBuild({ origin: [0, 0], rows: 3, partCounts: [] }))
      .toContain("root.build.rows: must be an array of row bitmasks.");
    expect(withBuild({ origin: [0, 0], rows: [], partCounts: [] }))
      .toContain("root.build.rows: needs at least one row.");
    expect(withBuild({ origin: [0, 0], rows: [1, -1], partCounts: [] }))
      .toContain("root.build.rows[1]: must be a non-negative integer bitmask.");
    expect(withBuild({ origin: [0, 0], rows: [1], partCounts: 3 }))
      .toContain("root.build.partCounts: must be an array of allowances.");
  });

  it("rejects a malformed allowance entry", () => {
    expect(withBuild({ origin: [0, 0], rows: [1], partCounts: [3] }))
      .toContain("root.build.partCounts[0]: must be a JSON object.");
    expect(withBuild({ origin: [0, 0], rows: [1], partCounts: [{ partTypeId: 4 }] }))
      .toContain("root.build.partCounts[0]: missing required property 'count'.");
    expect(withBuild({ origin: [0, 0], rows: [1], partCounts: [{ partTypeId: 0, count: 1 }] }))
      .toContain("root.build.partCounts[0].partTypeId: must be a positive 32-bit integer.");
    expect(withBuild({ origin: [0, 0], rows: [1], partCounts: [{ partTypeId: 4, count: 0 }] }))
      .toContain("root.build.partCounts[0].count: must be a positive 32-bit integer.");
    expect(withBuild({ origin: [0, 0], rows: [1], partCounts: [{ partTypeId: 4, count: 1, extra: 1 }] }))
      .toContain("root.build.partCounts[0]: unknown property 'extra'.");
  });
});

/** The v10 document: the v9 one plus the level's own challenges (`challenges`). */
const v10 = {
  ...structuredClone(v9),
  schemaVersion: 10,
  challenges: [
    { name: "BoxChallenge", kind: "box", propIndex: 1 },
    { name: "TimeChallenge", kind: "time", targetTime: 4 },
    { name: "PerfectFlightChallenge", kind: "perfectFlight" },
    { name: "DontUsePartChallenge", kind: "dontUseParts", partTypeId: 32 },
    { name: "TransportChallenge", kind: "transport", partTypeId: 32 },
    // Two instances sharing one name is legal (episode_6_level_25 / _32 / _IX).
    { name: "BoxChallenge", kind: "box", propIndex: 0 },
  ],
};

/** The v10 document with its `challenges` replaced by `value`. */
function withChallenges(value: unknown): string[] {
  return validateLevelContent({ ...structuredClone(v10), challenges: value });
}

describe("validateLevelContent challenges", () => {
  it("accepts a v10 document with the level's challenges", () => {
    expect(validateLevelContent(v10)).toEqual([]);
    expect(decodeLevelContent(v10).level?.challenges).toHaveLength(6);
  });

  it("accepts a v10 document whose level has no challenge at all", () => {
    expect(withChallenges([])).toEqual([]);
  });

  it("refuses challenges before v10", () => {
    for (const schemaVersion of [1, 2, 3, 4, 5, 6, 7, 8, 9]) {
      expect(validateLevelContent({ ...structuredClone(v10), schemaVersion }))
        .toContain("root.challenges: only a schemaVersion 10 document carries the level's challenges.");
    }
  });

  it("requires challenges on a v10 document", () => {
    const missing = structuredClone(v10) as Record<string, unknown>;
    delete missing.challenges;
    expect(validateLevelContent(missing)).toContain("root.challenges: required on a schemaVersion 10 document.");
  });

  it("rejects a malformed challenge entry", () => {
    expect(withChallenges(3)).toContain("root.challenges: must be an array.");
    expect(withChallenges([3])).toContain("root.challenges[0]: challenge must be a JSON object.");
    expect(withChallenges([{ kind: "box", propIndex: 0 }]))
      .toContain("root.challenges[0]: missing required property 'name'.");
    expect(withChallenges([{ name: "", kind: "box", propIndex: 0 }]))
      .toContain("root.challenges[0].name: must be a non-empty string.");
    expect(withChallenges([{ name: "BoxChallenge", kind: "boxes", propIndex: 0 }]))
      .toContain("root.challenges[0].kind: must be one of 'dontUseParts', 'time', 'perfectFlight', 'transport' or 'box'.");
    expect(withChallenges([{ name: "BoxChallenge", kind: "box" }]))
      .toContain("root.challenges[0].propIndex: must be a non-negative integer.");
    expect(withChallenges([{ name: "BoxChallenge", kind: "box", propIndex: -1 }]))
      .toContain("root.challenges[0].propIndex: must be a non-negative integer.");
    expect(withChallenges([{ name: "BoxChallenge", kind: "box", propIndex: 2 }]))
      .toContain("root.challenges[0].propIndex: must index one of this document's 2 props.");
    expect(withChallenges([{ name: "TimeChallenge", kind: "time" }]))
      .toContain("root.challenges[0].targetTime: must be a finite, non-negative number.");
    expect(withChallenges([{ name: "TimeChallenge", kind: "time", targetTime: "4" }]))
      .toContain("root.challenges[0].targetTime: must be a finite, non-negative number.");
    expect(withChallenges([{ name: "TimeChallenge", kind: "time", targetTime: -1 }]))
      .toContain("root.challenges[0].targetTime: must be a finite, non-negative number.");
    expect(withChallenges([{ name: "DontUsePartChallenge", kind: "dontUseParts" }]))
      .toContain("root.challenges[0].partTypeId: must be a positive 32-bit integer.");
    expect(withChallenges([{ name: "TransportChallenge", kind: "transport", partTypeId: 0 }]))
      .toContain("root.challenges[0].partTypeId: must be a positive 32-bit integer.");
    expect(withChallenges([{ name: "TimeChallenge", kind: "time", targetTime: 4, propIndex: 0 }]))
      .toContain("root.challenges[0]: unknown property 'propIndex' for a 'time' challenge.");
    expect(withChallenges([{ name: "PerfectFlightChallenge", kind: "perfectFlight", partTypeId: 32 }]))
      .toContain("root.challenges[0]: unknown property 'partTypeId' for a 'perfectFlight' challenge.");
    expect(withChallenges([{ name: "BoxChallenge", kind: "box", propIndex: 0, extra: 1 }]))
      .toContain("root.challenges[0]: unknown property 'extra'.");
  });
});

/** The v11 document: the v10 one with its first prop carrying a vanilla crate's own blast. */
const v11 = {
  ...structuredClone(v10),
  schemaVersion: 11,
  props: [
    {
      ...structuredClone(v10.props[0]),
      explosive: { triggerSpeed: 2, explosionRadius: 8, explosionImpulse: 25 },
    },
    structuredClone(v10.props[1]),
  ],
};

describe("validateLevelContent prop blasts", () => {
  it("accepts a v11 document whose prop carries a blast", () => {
    expect(validateLevelContent(v11)).toEqual([]);
  });

  it("refuses a blast before v11 and malformed blasts on v11", () => {
    const older = structuredClone(v11) as Record<string, unknown>;
    older.schemaVersion = 10;
    expect(validateLevelContent(older)).toContain("root.props[0]: unknown property 'explosive'.");

    const malformed = (explosive: unknown): string[] =>
      validateLevelContent({ ...structuredClone(v11), props: [{ ...structuredClone(v11.props[0]), explosive }] });
    expect(malformed(3)).toContain("root.props[0].explosive: must be a JSON object.");
    expect(malformed({ triggerSpeed: 2, explosionRadius: 8 })).toContain(
      "root.props[0].explosive: missing required property 'explosionImpulse'.",
    );
    expect(malformed({ triggerSpeed: -1, explosionRadius: 8, explosionImpulse: 25 })).toContain(
      "root.props[0].explosive.triggerSpeed: must not be negative.",
    );
    expect(malformed({ triggerSpeed: 2, explosionRadius: 0, explosionImpulse: 25 })).toContain(
      "root.props[0].explosive.explosionRadius: must be positive.",
    );
  });
});

describe("timeLimit", () => {
  it("takes the largest time challenge's target time", () => {
    expect(timeLimit([
      { name: "TimeChallenge", kind: "time", targetTime: 4 },
      { name: "TimeChallenge_2", kind: "time", targetTime: 7.5 },
    ])).toBe(7.5);
  });

  it("is 0 when the level has no timing challenge", () => {
    expect(timeLimit([])).toBe(0);
    expect(timeLimit([
      { name: "BoxChallenge", kind: "box", propIndex: 0 },
      { name: "PerfectFlightChallenge", kind: "perfectFlight" },
      { name: "TimeChallenge", kind: "time", targetTime: 0 },
    ])).toBe(0);
  });
});

describe("constructionGridShape / constructionCellExists", () => {
  it("reads the vanilla 4x2 shape (21 levels use [15, 15, 0, 0])", () => {
    const rows = [15, 15, 0, 0];
    expect(constructionGridShape(rows)).toEqual({ columns: 4, height: 2, xMin: -1, xMax: 2 });
    for (let j = 0; j < 2; j += 1) {
      for (let i = -1; i <= 2; i += 1) {
        expect(constructionCellExists(rows, i, j)).toBe(true);
      }
    }
    expect(constructionCellExists(rows, -2, 0)).toBe(false);
    expect(constructionCellExists(rows, 3, 0)).toBe(false);
    expect(constructionCellExists(rows, 0, -1)).toBe(false);
    expect(constructionCellExists(rows, 0, 2)).toBe(false);
  });

  it("reads the vanilla 5x3 shape (29 levels use [31, 31, 31, 0])", () => {
    const rows = [31, 31, 31, 0];
    expect(constructionGridShape(rows)).toEqual({ columns: 5, height: 3, xMin: -2, xMax: 2 });
    let cells = 0;
    for (let j = 0; j < 3; j += 1) {
      for (let i = -2; i <= 2; i += 1) {
        if (constructionCellExists(rows, i, j)) {
          cells += 1;
        }
      }
    }
    expect(cells).toBe(15);
  });

  it("treats a cleared bit as a hole (vanilla ships no ragged level rows)", () => {
    // Row 0 sets bits 0, 2, 3; row 1 sets bit 1 only -- the formula's hole branch.
    const rows = [0b1101, 0b0010];
    expect(constructionGridShape(rows)).toEqual({ columns: 4, height: 2, xMin: -1, xMax: 2 });
    expect(constructionCellExists(rows, 0, 0)).toBe(true);
    expect(constructionCellExists(rows, 0, 1)).toBe(false);
    expect(constructionCellExists(rows, -1, 1)).toBe(true);
    expect(constructionCellExists(rows, 2, 1)).toBe(true);
    expect(constructionCellExists(rows, -1, 0)).toBe(false);
    expect(constructionCellExists(rows, 1, 0)).toBe(false);
  });

  it("owns no cell when the grid is rows of zeros", () => {
    expect(constructionGridShape([0, 0, 0])).toEqual({ columns: 0, height: 0, xMin: 0, xMax: 0 });
    expect(constructionCellExists([0, 0, 0], 0, 0)).toBe(false);
  });
});
