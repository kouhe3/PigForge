// Deterministic file writing: the repo's shape (2-space indent, LF, trailing newline), hashed over
// exactly the bytes that are written, so a re-run that changes nothing writes nothing.
//
// Level content is three quarters outline points, so its writer is one level dumber than
// `JSON.stringify`: each boundary loop stays on a single line (`[[x,y],[x,y],...]`) and each
// number is printed as the *shortest decimal that still reads back as the same float32*. That is
// lossless: every value the pack defines reaches us through `BinaryReader.ReadSingle`
// (`lib/reader.mjs`), and PigForge stores every content number as `float` too, so every number in a
// content file is exactly a float32 -- a guarantee the writer enforces (it throws on anything
// else), which also keeps the file self-consistent: a consumer that reads `bounds` and adds 1 gets
// exactly the `goalZone` a level without a Goal* instance was given.

import { createHash } from "node:crypto";

/// `JSON.stringify(value, null, 2)` plus a trailing newline; used by the extractor's report, whose
/// numbers are counts (exact integers) and whose shape is unchanged.
export function formatJson(value) {
  return `${JSON.stringify(value, null, 2)}\n`;
}

export function sha256(text) {
  return createHash("sha256").update(text, "utf8").digest("hex");
}

/// A finite float32 shortened to the shortest decimal that parses back to the same float32. Values
/// that are not exactly a float32 are a caller bug (a derived value that skipped its rounding) and
/// are refused rather than silently widened.
function formatNumber(value) {
  if (typeof value !== "number" || !Number.isFinite(value)) {
    throw new Error(`level content must be finite numbers, got ${JSON.stringify(value)}`);
  }
  if (Math.fround(value) !== value) {
    throw new Error(`level content must be float32-exact, got ${value}`);
  }
  for (let precision = 1; precision <= 9; precision += 1) {
    const candidate = Number(value.toPrecision(precision));
    if (Math.fround(candidate) === value) return candidate;
  }
  return value;
}

const numberText = (value) => String(formatNumber(value));

const scalarText = (value) => {
  if (typeof value === "number") return numberText(value);
  if (typeof value === "string") return JSON.stringify(value);
  if (typeof value === "boolean") return value ? "true" : "false";
  if (value === null) return "null";
  throw new Error(`cannot serialise ${typeof value}`);
};

/// No whitespace at all: a loop (`[[x,y],[x,y],...]`) and a vector (`[x,y,z]`) both fit one line.
function compact(value) {
  if (Array.isArray(value)) return `[${value.map(compact).join(",")}]`;
  if (value !== null && typeof value === "object") {
    return `{${Object.keys(value).map((key) => `${JSON.stringify(key)}:${compact(value[key])}`).join(",")}}`;
  }
  return scalarText(value);
}

/// The readable 2-space layout, with float32-shortest numbers, for the small header objects
/// (`goalZone`, `bounds`, `spawns`). `indent` is the indentation of the line the value hangs off.
function pretty(value, indent) {
  const pad = " ".repeat(indent);
  const child = " ".repeat(indent + 2);
  if (Array.isArray(value)) {
    if (value.length === 0) return "[]";
    return `[\n${value.map((item) => child + pretty(item, indent + 2)).join(",\n")}\n${pad}]`;
  }
  if (value !== null && typeof value === "object") {
    const keys = Object.keys(value);
    if (keys.length === 0) return "{}";
    return `{\n${keys.map((key) => `${child}${JSON.stringify(key)}: ${pretty(value[key], indent + 2)}`).join(",\n")}\n${pad}}`;
  }
  return scalarText(value);
}

/// One `terrain` entry: `position`, `depth`, `collider`, the `fill` block and (v4) the `curve` block
/// on lines of their own, then one line per outline loop. `fill` is one line because it is four
/// scalars and two short vectors (`fill.shader`'s own inputs); a document without one (v2 and older)
/// omits the line entirely. `curve` is the `_curve` mesh -- two rows of points, one line each, plus
/// the two layer textures, the u scale and the second layer's node runs (`curve.shader`'s inputs).
function terrainText(terrains, schemaVersion) {
  if (terrains.length === 0) return "[]";
  const entries = terrains.map((terrain) => {
    // v3 is the version that carries the ground's look and the collider bit, so an entry missing one
    // is a converter bug rather than a document to write.
    if (schemaVersion >= 3 && (terrain.fill === undefined || typeof terrain.collider !== "boolean")) {
      throw new Error("a v3 terrain entry needs both a collider flag and a fill block");
    }
    if (schemaVersion < 3 && (terrain.fill !== undefined || terrain.collider !== undefined)) {
      throw new Error("a terrain fill block and collider flag are v3-only");
    }
    if (schemaVersion >= 4 && terrain.curve === undefined) {
      throw new Error("a v4 terrain entry needs a curve block");
    }
    if (schemaVersion < 4 && terrain.curve !== undefined) {
      throw new Error("a terrain curve block is v4-only");
    }

    const loops = terrain.loops.map((loop) => `        ${compact(loop)}`).join(",\n");
    const lines = [
      "    {",
      `      "position": ${compact(terrain.position)},`,
      `      "depth": ${numberText(terrain.depth)},`,
      `      "collider": ${scalarText(terrain.collider)},`,
    ];
    if (terrain.fill !== undefined) {
      lines.push(`      "fill": ${compact(terrain.fill)},`);
    }
    if (terrain.curve !== undefined) {
      lines.push(
        '      "curve": {',
        `        "textures": ${compact(terrain.curve.textures)},`,
        `        "uScale": ${numberText(terrain.curve.uScale)},`,
        `        "splat1": ${compact(terrain.curve.splat1)},`,
        `        "nodes": ${compact(terrain.curve.nodes)},`,
        `        "stripe": ${compact(terrain.curve.stripe)}`,
        "      },",
      );
    }
    lines.push('      "loops": [', loops, "      ]", "    }");
    return lines.join("\n");
  });
  return `[\n${entries.join(",\n")}\n  ]`;
}

/// One `props` entry per line: a decoration instance is `{ id, x, y, z, rotation, scaleX, scaleY }`
/// and a level carries tens of them, so the compact form (like a loop) keeps the readable header of
/// the document short without hiding the data. v6 is the version that places props at all.
function propsText(props, schemaVersion) {
  if (schemaVersion < 6) {
    if (props !== undefined) throw new Error("level props are a v6 field");
    return null;
  }
  if (!Array.isArray(props)) throw new Error("a v6 document needs the level's props (an array, possibly empty)");
  if (props.length === 0) return "[]";
  return `[\n${props.map((prop) => `    ${compact(prop)}`).join(",\n")}\n  ]`;
}

/// A PigForge level-content document, in the schema's own key order: `format`, `schemaVersion`,
/// `contentVersion`, `goalZone`, `bounds`, `cameraLimits` (v5), `spawns`, `props` (v6), `terrain`.
export function formatLevelDocument(document) {
  if (document.schemaVersion >= 5 && document.cameraLimits === undefined) {
    throw new Error("a v5 document needs the level's own cameraLimits");
  }
  if (document.schemaVersion < 5 && document.cameraLimits !== undefined) {
    throw new Error("cameraLimits is a v5 field");
  }
  const props = propsText(document.props, document.schemaVersion);
  return `${[
    "{",
    `  "format": ${JSON.stringify(document.format)},`,
    `  "schemaVersion": ${numberText(document.schemaVersion)},`,
    `  "contentVersion": ${JSON.stringify(document.contentVersion)},`,
    `  "goalZone": ${pretty(document.goalZone, 2)},`,
    `  "bounds": ${pretty(document.bounds, 2)},`,
    ...(document.cameraLimits === undefined ? [] : [`  "cameraLimits": ${compact(document.cameraLimits)},`]),
    `  "spawns": ${pretty(document.spawns, 2)},`,
    ...(props === null ? [] : [`  "props": ${props},`]),
    `  "terrain": ${terrainText(document.terrain, document.schemaVersion)}`,
    "}",
  ].join("\n")}\n`;
}
