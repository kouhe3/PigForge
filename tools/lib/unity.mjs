// Readers for the original Unity project: prefab YAML, the decompiled C#, and the two indices
// that connect them (script guid -> class name, class -> base class). Every extractor reads the
// original through here, so none of them can drift into a second convention.

import { existsSync, readdirSync, readFileSync } from "node:fs";
import { basename, join } from "node:path";
import { PART_MAP } from "./paths.mjs";

// ------------------------------------------------------------------ prefab YAML
/// A prefab's YAML text, or null when the original has no such prefab.
export function prefabText(gameObjectDir, name) {
  const path = join(gameObjectDir, `${name}.prefab`);
  return existsSync(path) ? readFileSync(path, "utf8") : null;
}

/** The first `field: <number>` in the text, or null. Every serialized field appears once per
 * block. */
export function readField(text, field) {
  const match = new RegExp(`^\\s*${field}:\\s*(-?[0-9.]+)\\s*$`, "m").exec(text);
  if (!match) {
    return null;
  }

  const value = Number(match[1]);
  return Number.isFinite(value) ? value : null;
}

/** The first `field: {x: .., y: .., z: ..}` in the text, or null. */
export function readVector(text, field) {
  const match = new RegExp(`^\\s*${field}:\\s*\\{x:\\s*(-?[0-9.]+),\\s*y:\\s*(-?[0-9.]+),\\s*z:\\s*(-?[0-9.]+)\\s*\\}\\s*$`, "m").exec(text);
  if (!match) {
    return null;
  }

  const vector = { x: Number(match[1]), y: Number(match[2]), z: Number(match[3]) };
  return Object.values(vector).every(Number.isFinite) ? vector : null;
}

/** The first `name: <rest>` raw string, trimmed; `undefined` when the field is absent. */
export function fieldOf(body, name) {
  const match = new RegExp(`^\\s*${name}:\\s*(.*)$`, "m").exec(body);
  return match ? match[1].trim() : undefined;
}

/** The first `--- !u!<class> &...` block of a prefab that matches `pattern`, or null. Unity
 * writes ONE block per component, so the block that carries one serialized field carries every
 * inherited field of that component too. */
export function blockWith(text, pattern) {
  return text.split(/\n--- !u!/).find((block) => pattern.test(block)) ?? null;
}

// ------------------------------------------------------------------ script index
const CLASS_DECLARATION = /^\s*(?:public |internal )?(?:sealed |abstract |partial |static |unsafe )*class\s+(\w+)\s*:\s*([\w<>,.\s]*?)\s*\{?\s*$/gm;
const FIRST_CLASS = /^\s*(?:public |internal )?(?:sealed |abstract |partial |static |unsafe )*class\s+(\w+)\b/m;

/** The first class declared in a source file; null for a file that only declares structs/enums. */
export function firstClassName(text) {
  return FIRST_CLASS.exec(text)?.[1] ?? null;
}

/** guid -> class name, over a folder of `.cs.meta` files. `declaredClass` resolves the class from
 * the `.cs` beside the `.meta` instead of trusting the file name (the two differ when a file
 * declares several types). */
export function buildGuidIndex(scriptsDir, { declaredClass = false } = {}) {
  const byGuid = new Map();
  for (const entry of readdirSync(scriptsDir)) {
    if (!entry.endsWith(".cs.meta")) continue;
    const match = /^guid:\s*([0-9a-f]{32})/m.exec(readFileSync(join(scriptsDir, entry), "utf8"));
    if (!match) continue;
    const file = basename(entry, ".cs.meta");
    if (!declaredClass) {
      byGuid.set(match[1], file);
      continue;
    }

    const source = join(scriptsDir, `${file}.cs`);
    byGuid.set(match[1], (existsSync(source) ? firstClassName(readFileSync(source, "utf8")) : null) ?? file);
  }

  return byGuid;
}

/** class name -> base class name, for every class in a folder of `.cs` files. The modifier-aware
 * pattern also sees `internal`/`partial`/`sealed` classes, and a generic base (`PartManager<T>`)
 * is kept verbatim: it simply does not resolve further. */
export function buildClassBases(scriptsDir) {
  const bases = new Map();
  for (const entry of readdirSync(scriptsDir)) {
    if (!entry.endsWith(".cs")) continue;
    for (const match of readFileSync(join(scriptsDir, entry), "utf8").matchAll(CLASS_DECLARATION)) {
      bases.set(match[1], match[2].split(",")[0].trim());
    }
  }

  return bases;
}

/** Every file under `directory` (recursively) whose name ends with `suffix`. */
export function filesUnder(directory, suffix) {
  const found = [];
  for (const entry of readdirSync(directory, { withFileTypes: true })) {
    const path = join(directory, entry.name);
    if (entry.isDirectory()) {
      found.push(...filesUnder(path, suffix));
    } else if (entry.name.endsWith(suffix)) {
      found.push(path);
    }
  }

  return found;
}

/** guid -> absolute asset path (the `.meta` suffix dropped) for every `<suffix>.meta` file under
 * `root`, walked recursively. The guid sits in the file's first 256 bytes, which is where Unity
 * writes it. `ignoreUnreadable` keeps a dangling directory from failing the whole index. */
export function indexAssetGuids(root, { suffix = ".meta", ignoreUnreadable = false } = {}) {
  const byGuid = new Map();
  const walk = (dir) => {
    let entries;
    try {
      entries = readdirSync(dir, { withFileTypes: true });
    } catch (error) {
      if (ignoreUnreadable) {
        return;
      }

      throw error;
    }

    for (const entry of entries) {
      const path = join(dir, entry.name);
      if (entry.isDirectory()) {
        walk(path);
        continue;
      }

      if (!entry.name.endsWith(`${suffix}.meta`)) continue;
      const match = /^guid: ([0-9a-f]{32})/m.exec(readFileSync(path, "utf8").slice(0, 256));
      if (match) byGuid.set(match[1], path.slice(0, -5));
    }
  };

  walk(root);
  return byGuid;
}

/** `className` and every ancestor, nearest first. C# forbids cycles, but the guard keeps a
 * malformed source from hanging the tool. */
export function classChain(classBases, className) {
  const chain = [];
  const seen = new Set();
  for (let name = className; name && !seen.has(name); name = classBases.get(name)) {
    seen.add(name);
    chain.push(name);
  }

  return chain;
}

/// True when the class or one of its ancestors is the original's `BasePart`.
export const derivesFromBasePart = (classBases, className) => classChain(classBases, className).includes("BasePart");

// ------------------------------------------------------------------ content mapping
/// `tools/bple-textures/part-map.json`: the two sections as read.
export const readPartMap = () => JSON.parse(readFileSync(PART_MAP, "utf8"));

/** partTypeId -> prefab name. `parts` covers the bases and `variants` the imported skins; either
 * may be null for a PigForge-only invention, which is skipped. */
export function assignments(partMap = readPartMap()) {
  const byPart = new Map();
  for (const section of ["parts", "variants"]) {
    for (const [partTypeId, prefab] of Object.entries(partMap[section] ?? {})) {
      if (typeof prefab === "string" && prefab.length > 0) {
        byPart.set(Number(partTypeId), prefab);
      }
    }
  }

  return byPart;
}
