#!/usr/bin/env node
// Extracts the original's per-rigidbody DAMPING (Unity `Rigidbody.drag` / `angularDrag`, renamed
// `linearDamping` / `angularDamping` in Unity 6) for every part PigForge models, plus the
// project-wide `Physics.maxAngularSpeed` every original rigidbody inherits. Reads the BPLE Unity
// project read-only, prints a JSON report, and `apply-damping.mjs` turns that report into
// content/parts.json. Companion of tools/bple-mass and tools/bple-shapes (same prefab mapping).
//
// Usage:
//   node tools/bple-damping/extract-damping.mjs [--bple <dir>] [--json <file>] [--md <file>]
//   node tools/bple-damping/apply-damping.mjs [--report <file>] [--content <file>] [--dry-run]
//
// Provenance (verified against the BPLE source, `Assembly-CSharp`):
//
//   BasePart.EnsureRigidbody()   drag 0.2 / angularDrag 0.05   (BasePart.cs:1192-1205)  <- default
//   Wings.EnsureRigidbody()      1.0 / 0.2                     (Wings.cs:91-102)
//   Tail.EnsureRigidbody()       1.0 / 0.2                     (Tail.cs:44-55)
//   Balloon.Initialize()         2.0 / 0.5                     (Balloon.cs:83,129-132)
//   Sandbag.Initialize()         1.0 / 10.0                    (Sandbag.cs:61,132-135)
//   KingPig.Initialize()         0.5 / 1.0                     (KingPig.cs:79-82)
//   GoldenPig.Initialize()       0.5 / 1.0                     (GoldenPig.cs:14-18)  (not in content)
//   FanPropeller.FixedUpdate()   angularDrag 1.0 (off) / 1000.0 (rotor on)   -- RUNTIME, excluded
//   Rope.FixedUpdate()           per-node drag                                -- RUNTIME, excluded
//   INContraption.FixedUpdateSelf()  zeroes every body's damping behind the `NoDrag` switch
//                                    (INContraption.cs:329-341)               -- a player setting, not content
//   Pig.FixedUpdate()            while the contraption runs, `|v| < 1` rewrites BOTH drag and
//                                angularDrag to `0.2 + 2.5 * (1 - |v|)` and otherwise puts the
//                                class's spawn pair back (Pig.cs:249-262)   -- EXTRACTED, below
//
// The values are read out of the decompiled C# (never authored here): one class per file, the
// damping lands in `EnsureRigidbody` or in the spawn-time `Initialize` the original calls right
// after it (`INContraption.cs:813-816`), so a class's value is its `EnsureRigidbody` chain
// followed by its `Initialize` chain. Assignments anywhere else FAIL this tool instead of being
// silently ignored -- that is how the rotor's runtime `angularDrag = 1000` stays visible.
//
// The project-wide angular clamp comes from the original's own `ProjectSettings`:
// `m_DefaultMaxAngularSpeed: 7` (DynamicsManager.asset). Unity 2021.3 documents the effect
// (Rigidbody.maxAngularVelocity: "The angular velocity of rigidbodies is clamped to
// maxAngularVelocity", default 7 rad/s) and PhysX implements it as a magnitude clamp after
// damping (`DyBodyCoreIntegrator.h::bodyCoreComputeUnconstrainedVelocity`:
// `if (angVelSq > maxAngularVelocitySq) angularVelocity *= PxSqrt(maxAngularVelocitySq / angVelSq)`).
// Nothing in the original overrides it: no script assigns `maxAngularVelocity` and none of the
// 343 `Part_*.prefab` serialize `m_MaxAngularVelocity`.
//
// Hard invariants (drift fails this tool):
//   - every content part maps to an original prefab and to a BasePart-derived script class;
//   - the class->damping table equals EXPECTED_CLASS_DAMPING exactly;
//   - damping is only ever assigned from {EnsureRigidbody, Initialize} (RUNTIME_METHODS lists the
//     known runtime overrides and is reported, not applied);
//   - the original's PhysicsManager clamp is the value the report carries.
//   - the runtime damping ramps are exactly EXPECTED_DAMPING_RAMPS, read out of `FixedUpdate`.

import { existsSync, readFileSync } from "node:fs";
import { join } from "node:path";
import { flag } from "../lib/args.mjs";
import { CONTENT, PART_MAP, assets, bpleProject, gameObjects, reportJson, reportMd } from "../lib/paths.mjs";
import { fail, writeJsonArtifact, writeMarkdownArtifact } from "../lib/report.mjs";
import { filesUnder } from "../lib/unity.mjs";

/** `BPLE 2022.1.9` is the pristine original (the same editor the original shipped with,
 * 2021.3.45f2); `BPLE_Unity6` is the migrated copy the other tools default to and carries the
 * Unity 6 API names (`linearDamping` / `angularDamping`). Both spellings are parsed, so either
 * root works, but the pristine copy is the one to trust when they disagree. */
const BPLE = bpleProject("BPLE 2022.1.9");
const OUT_JSON = reportJson("damping");
const OUT_MD = reportMd("damping");
const GAMEOBJECT = gameObjects(BPLE);
const SCRIPTS = join(assets(BPLE), "Scripts");
const SETTINGS = join(BPLE, "ProjectSettings");

if (!existsSync(GAMEOBJECT) || !existsSync(SCRIPTS)) {
  fail(`BPLE project not found: ${GAMEOBJECT}\nPass --bple <path to the BPLE project>.`);
}

/** The spawn-time methods the original runs in this order (`INContraption.cs:813-816`:
 * `EnsureRigidbody()` then `Initialize()`), so a later layer overrides an earlier one. */
const SPAWN_METHODS = ["EnsureRigidbody", "Initialize"];

/** Methods that assign damping at runtime rather than at spawn. They are reported (so the
 * deviation stays on the record) and never folded into a part's content value. */
const RUNTIME_METHODS = new Set([
  // FanPropeller.cs:145-154 -- a rotor raises its own angularDrag to 1000 while it spins and drops
  // it back to 1 when switched off; Rope.cs:365-366 -- every rope node recomputes its drag from
  // its own stretch. Both are per-frame state, not a part's spawn-time damping.
  "FixedUpdate",
  "Update",
  "Awake",
  "Start",
  "OnCollisionEnter",
  "Pop",
  "PostInitialize",
  // HingePlate.cs:243-262 -- the two plate bodies of the IN hinge plate, a part PigForge does not
  // have; INContraption.cs:329-341 -- the `NoDrag` player setting zeroing every body in the world.
  "EnsurePlateRigidbody",
  "FixedUpdateSelf",
]);

/** The one admissible class -> (linear, angular) table. A change in the original's source (or in a
 * mis-parsed file) fails this tool rather than silently writing a different world. */
const EXPECTED_CLASS_DAMPING = {
  BasePart: [0.2, 0.05],
  Wings: [1, 0.2],
  Tail: [1, 0.2],
  Balloon: [2, 0.5],
  Sandbag: [1, 10],
  KingPig: [0.5, 1],
};

const warnings = [];

// ---------------------------------------------------------------------------------------------
// The decompiled C#: one class per file, `class X : Y`, methods on their own line.
// ---------------------------------------------------------------------------------------------

/** `guid -> class name`, from the `.cs.meta` files next to every script. */
function buildGuidMap() {
  const map = new Map();
  for (const path of filesUnder(SCRIPTS, ".cs")) {
    const meta = `${path}.meta`;
    if (!existsSync(meta)) {
      continue;
    }
    const guid = /^guid:\s*([0-9a-f]{32})\s*$/m.exec(readFileSync(meta, "utf8"));
    if (guid) {
      map.set(guid[1], basename(path).replace(/\.cs$/, ""));
    }
  }
  return map;
}

function basename(path) {
  const index = Math.max(path.lastIndexOf("/"), path.lastIndexOf("\\"));
  return index >= 0 ? path.slice(index + 1) : path;
}

/** `class -> base class`, and `class -> source file`. */
function buildClasses() {
  const bases = new Map();
  const files = new Map();
  const texts = new Map();
  for (const path of filesUnder(SCRIPTS, ".cs")) {
    const text = readFileSync(path, "utf8");
    const declaration = /^\s*(?:public\s+|internal\s+)?(?:sealed\s+|abstract\s+|partial\s+|static\s+)*class\s+(\w+)\s*:\s*([\w.]+)/m.exec(text);
    if (!declaration) {
      continue;
    }
    bases.set(declaration[1], declaration[2].split(".").pop());
    files.set(declaration[1], path);
    texts.set(declaration[1], text);
  }
  return { bases, files, texts };
}

/** The methods a class declares itself, as `name -> body text`. Brace matching skips string and
 * character literals, which is enough for this decompiled shape. */
function methodBodies(text) {
  const bodies = new Map();
  const signature = /^[ \t]*(?:public\s+|private\s+|protected\s+|internal\s+)*(?:override\s+|virtual\s+|static\s+|new\s+|sealed\s+|async\s+)*[\w<>[\],.\s?]+\s(\w+)\s*\([^()]*\)\s*$/gm;
  let match;
  while ((match = signature.exec(text)) !== null) {
    const open = text.indexOf("{", match.index + match[0].length);
    if (open < 0) {
      continue;
    }
    let depth = 0;
    let index = open;
    let inString = false;
    let inChar = false;
    for (; index < text.length; index++) {
      const character = text[index];
      if (inString) {
        if (character === "\\") {
          index++;
        } else if (character === '"') {
          inString = false;
        }
        continue;
      }
      if (inChar) {
        if (character === "\\") {
          index++;
        } else if (character === "'") {
          inChar = false;
        }
        continue;
      }
      if (character === '"') {
        inString = true;
      } else if (character === "'") {
        inChar = true;
      } else if (character === "{") {
        depth++;
      } else if (character === "}") {
        depth--;
        if (depth === 0) {
          break;
        }
      }
    }
    bodies.set(match[1], text.slice(open + 1, index));
  }
  return bodies;
}

const DAMPING_FIELD = /(?:base\.)?rigidbody\.(drag|angularDrag|linearDamping|angularDamping)\s*=\s*(-?\d+(?:\.\d+)?)f?\s*;/g;
const BASE_CALL = /base\.(\w+)\s*\(\s*\)\s*;/;

/** The assignments of one method, split at its `base.<same>();` call: statements before it run
 * before the base chain, the ones after run once the base chain returned. A method that never
 * calls base keeps every statement in `after`. */
function assignmentsOf(body) {
  const call = BASE_CALL.exec(body);
  const split = call ? call.index + call[0].length : 0;
  const before = [];
  const after = [];
  DAMPING_FIELD.lastIndex = 0;
  let match;
  while ((match = DAMPING_FIELD.exec(body)) !== null) {
    const field = match[1] === "drag" || match[1] === "linearDamping" ? "linear" : "angular";
    (match.index < split ? before : after).push([field, Number(match[2])]);
  }
  return { before, after };
}

/** Walks the inheritance chain in execution order and folds every damping assignment into the
 * class's values: derived-before-base for the pre-call statements, then base-to-derived for the
 * post-call ones. Returns null when no class in the chain assigns damping. */
function resolveClassDamping(className, classes, methodName) {
  const chain = [];
  let current = className;
  while (current && !chain.includes(current)) {
    chain.push(current);
    current = classes.bases.get(current);
  }

  const bodies = new Map();
  for (const name of chain) {
    const text = classes.texts.get(name);
    if (text === undefined) {
      continue;
    }
    const body = methodBodies(text).get(methodName);
    if (body !== undefined) {
      bodies.set(name, assignmentsOf(body));
    }
  }
  if (bodies.size === 0) {
    return null;
  }

  const values = {};
  const apply = (entries) => {
    for (const [field, value] of entries) {
      values[field] = { value, declaringClass: null };
    }
  };
  // Pre-call statements: most derived first.
  for (let index = 0; index < chain.length; index++) {
    const body = bodies.get(chain[index]);
    if (!body) {
      continue;
    }
    for (const entry of body.before) {
      values[entry[0]] = { value: entry[1], declaringClass: chain[index] };
    }
  }
  // Post-call statements: base first (the root's whole body is its own post-call part).
  for (let index = chain.length - 1; index >= 0; index--) {
    const body = bodies.get(chain[index]);
    if (!body) {
      continue;
    }
    for (const entry of body.after) {
      values[entry[0]] = { value: entry[1], declaringClass: chain[index] };
    }
  }
  return values;
}

/** Every method in the project that assigns damping, so a method outside SPAWN_METHODS is caught
 * (either it is a known runtime override, or the tool refuses to guess). */
function collectAssignmentMethods(classes, classNames) {
  const found = new Map();
  for (const name of classNames) {
    const text = classes.texts.get(name);
    if (text === undefined) {
      continue;
    }
    for (const [method, body] of methodBodies(text)) {
      DAMPING_FIELD.lastIndex = 0;
      if (DAMPING_FIELD.test(body)) {
        found.set(`${name}.${method}`, method);
      }
    }
  }
  return found;
}

// ---------------------------------------------------------------------------------------------
// The original's runtime damping ramps.
// ---------------------------------------------------------------------------------------------

/** The one admissible class -> ramp table. `Pig.FixedUpdate` is the project's only method that
 * rewrites a rigidbody's damping every step, and its numbers are read out of that body: a
 * source-level drift (or a mis-parse) fails this tool rather than silently writing a different
 * world. The ramp is content (`capabilities.dampingRamp`) because it is a class fact of exactly
 * the classes listed here -- `KingPig` and `GoldenPig` are `BasePart` subclasses and never ramp. */
const EXPECTED_DAMPING_RAMPS = { Pig: { speedThreshold: 1, base: 0.2, slope: 2.5 } };

const RAMP_GATE = /if\s*\(\s*\(bool\)base\.contraption\s*&&\s*base\.contraption\.IsRunning\s*\)/;
const RAMP_SPEED = /float\s+magnitude\s*=\s*base\.rigidbody\.velocity\.magnitude\s*;/;
const RAMP_LOW = /if\s*\(\s*magnitude\s*<\s*(-?\d+(?:\.\d+)?)f\s*\)/;
const RAMP_RAMPED =
  /base\.rigidbody\.(drag|angularDrag)\s*=\s*(-?\d+(?:\.\d+)?)f\s*\+\s*(-?\d+(?:\.\d+)?)f\s*\*\s*\(\s*1f\s*-\s*magnitude\s*\)\s*;/g;
const RAMP_IDLE = /base\.rigidbody\.(drag|angularDrag)\s*=\s*(-?\d+(?:\.\d+)?)f\s*;/g;

/** Reads a class's `FixedUpdate` damping ramp, or null when it declares none. The shape is
 * asserted rather than guessed: an `IsRunning` gate, a `magnitude` speed read, one
 * `magnitude < threshold` branch whose two assignments agree on base and slope, and a pair of
 * plain assignments that restores exactly the class's own spawn damping. */
function resolveDampingRamp(className, classes, spawn) {
  // Only a class the table expects is parsed as a ramp: every other runtime assignment
  // (`FanPropeller`'s rotor angularDrag, a rope node's drag) is reported by
  // `collectAssignmentMethods` instead, and must not be mistaken for this shape.
  if (!(className in EXPECTED_DAMPING_RAMPS)) {
    return null;
  }

  const text = classes.texts.get(className);
  if (text === undefined) {
    return null;
  }

  const body = methodBodies(text).get("FixedUpdate");
  if (body === undefined) {
    return null;
  }

  RAMP_IDLE.lastIndex = 0;
  const idle = [...body.matchAll(RAMP_IDLE)];
  if (idle.length === 0) {
    return null;
  }

  const source = `${basename(classes.files.get(className) ?? "")}:FixedUpdate`;
  const drift = (message) => fail(`${className}.${source} ${message}`);
  if (!RAMP_GATE.test(body)) {
    drift("has no `contraption.IsRunning` gate.");
  }
  if (!RAMP_SPEED.test(body)) {
    drift("has no `float magnitude = base.rigidbody.velocity.magnitude;` read.");
  }
  const low = RAMP_LOW.exec(body);
  if (low === null) {
    drift("has no `if (magnitude < threshold)` branch.");
  }

  RAMP_RAMPED.lastIndex = 0;
  const ramped = [...body.matchAll(RAMP_RAMPED)];
  const fields = new Map();
  for (const match of ramped) {
    const field = match[1] === "drag" ? "linear" : "angular";
    if (fields.has(field)) {
      drift(`assigns ${match[1]} twice in the ramped branch.`);
    }
    fields.set(field, { at: match.index, base: Number(match[2]), slope: Number(match[3]) });
  }
  if (fields.size !== 2) {
    drift(`ramps ${fields.size} of the two drags, expected both.`);
  }
  const linear = fields.get("linear");
  const angular = fields.get("angular");
  if (linear.base !== angular.base || linear.slope !== angular.slope) {
    drift(`ramps the two drags differently (${linear.base} + ${linear.slope}, ${angular.base} + ${angular.slope}).`);
  }

  const restores = new Map();
  for (const match of idle) {
    const field = match[1] === "drag" ? "linear" : "angular";
    if (restores.has(field)) {
      drift(`restores ${match[1]} twice.`);
    }
    restores.set(field, { at: match.index, value: Number(match[2]) });
  }
  if (restores.size !== 2) {
    drift(`restores ${restores.size} of the two drags, expected both.`);
  }
  if (restores.get("linear").value !== spawn.linear || restores.get("angular").value !== spawn.angular) {
    drift(
      `restores ${restores.get("linear").value} / ${restores.get("angular").value}, not the class's own spawn pair ${spawn.linear} / ${spawn.angular}.`,
    );
  }
  if (linear.at > restores.get("linear").at || angular.at > restores.get("angular").at) {
    drift("assigns the ramped pair after the restoring pair; the ramp is the `magnitude < threshold` branch.");
  }

  const speedThreshold = Number(low[1]);
  if (!(speedThreshold > 0)) {
    drift(`has a non-positive speed threshold (${speedThreshold}).`);
  }

  const expected = EXPECTED_DAMPING_RAMPS[className];
  if (
    !expected ||
    expected.speedThreshold !== speedThreshold ||
    expected.base !== linear.base ||
    expected.slope !== linear.slope
  ) {
    drift(
      `extracted { speedThreshold: ${speedThreshold}, base: ${linear.base}, slope: ${linear.slope} }, expected ${JSON.stringify(expected ?? null)}.`,
    );
  }

  return {
    speedThreshold,
    base: linear.base,
    slope: linear.slope,
    source: `${basename(classes.files.get(className) ?? "")}:FixedUpdate`,
  };
}

// ---------------------------------------------------------------------------------------------
// The original's project-wide physics settings.
// ---------------------------------------------------------------------------------------------

function readSettings() {
  const dynamics = readFileSync(join(SETTINGS, "DynamicsManager.asset"), "utf8");
  const time = readFileSync(join(SETTINGS, "TimeManager.asset"), "utf8");
  const scalar = (text, name) => {
    const match = new RegExp(`^\\s*${name}:\\s*(-?[0-9.]+)\\s*$`, "m").exec(text);
    return match ? Number(match[1]) : null;
  };
  const gravity = /m_Gravity:\s*\{\s*x:\s*(-?[0-9.]+),\s*y:\s*(-?[0-9.]+),\s*z:\s*(-?[0-9.]+)\s*\}/.exec(dynamics);
  return {
    source: `${SETTINGS.replace(/\\/g, "/")}/DynamicsManager.asset`,
    maximumAngularSpeed: scalar(dynamics, "m_DefaultMaxAngularSpeed"),
    gravity: gravity ? [Number(gravity[1]), Number(gravity[2]), Number(gravity[3])] : null,
    solverIterations: scalar(dynamics, "m_DefaultSolverIterations"),
    bounceThreshold: scalar(dynamics, "m_BounceThreshold"),
    fixedTimeStep: scalar(time, "Fixed Timestep"),
  };
}

// ---------------------------------------------------------------------------------------------
// Report.
// ---------------------------------------------------------------------------------------------

const classes = buildClasses();
const guidToClass = buildGuidMap();

const content = JSON.parse(readFileSync(CONTENT, "utf8"));
const map = JSON.parse(readFileSync(PART_MAP, "utf8"));
const assignments = { ...map.parts, ...map.variants };
if (Object.keys(assignments).length !== content.parts.length) {
  fail(`part-map.json maps ${Object.keys(assignments).length} parts but content declares ${content.parts.length}.`);
}

const classDamping = new Map();
const classRamps = new Map();
const usedClasses = new Set();
const parts = {};
const missing = [];

/** A class's runtime damping ramp, resolved at most once per class (see `resolveDampingRamp`). */
function dampingRampOf(className, spawn) {
  if (!classRamps.has(className)) {
    classRamps.set(className, resolveDampingRamp(className, classes, spawn));
  }
  return classRamps.get(className);
}

/** A class's damping: its `EnsureRigidbody` chain folded with its `Initialize` chain, resolved at
 * most once per class. */
function dampingOf(className) {
  if (!classDamping.has(className)) {
    const resolved = {};
    for (const method of SPAWN_METHODS) {
      const values = resolveClassDamping(className, classes, method);
      if (!values) {
        continue;
      }
      for (const [field, entry] of Object.entries(values)) {
        resolved[field] = { ...entry, method };
      }
    }
    classDamping.set(className, resolved);
  }
  return classDamping.get(className);
}

for (const part of content.parts) {
  const partTypeId = String(part.partTypeId);
  const prefabName = assignments[partTypeId];
  if (prefabName === null || prefabName === undefined) {
    // `part-map.json` writes null for the parts PigForge invented and the original has no prefab
    // for (ground-slab, terrain-box, ramp-plank). Those are the static level pieces, and a static
    // body has no damping to carry, so only a dynamic part may be missing a prefab.
    if (part.mode === "dynamic") {
      missing.push(`${partTypeId} (${part.name}): no prefab in tools/bple-textures/part-map.json`);
    } else {
      warnings.push(`${partTypeId} (${part.name}): PigForge-only static part, no original prefab and no damping.`);
    }
    continue;
  }
  if (typeof prefabName !== "string") {
    missing.push(`${partTypeId} (${part.name}): no prefab in tools/bple-textures/part-map.json`);
    continue;
  }
  const prefabPath = join(GAMEOBJECT, `${prefabName}.prefab`);
  if (!existsSync(prefabPath)) {
    missing.push(`${partTypeId} (${part.name}): prefab missing on disk: ${prefabName}`);
    continue;
  }

  const text = readFileSync(prefabPath, "utf8");
  const guids = [...text.matchAll(/m_Script:\s*\{fileID:\s*11500000,\s*guid:\s*([0-9a-f]{32}),\s*type:\s*3\}/g)].map((m) => m[1]);
  const partClasses = guids.map((guid) => guidToClass.get(guid)).filter((name) => name !== undefined);
  const derived = partClasses.filter((name) => {
    let current = name;
    for (let depth = 0; current && depth < 16; depth++) {
      if (current === "BasePart") {
        return true;
      }
      current = classes.bases.get(current);
    }
    return false;
  });
  if (derived.length === 0) {
    missing.push(`${partTypeId} (${part.name}): ${prefabName} carries no BasePart-derived script (found: ${partClasses.join(", ") || "none"})`);
    continue;
  }
  if (derived.length > 1) {
    warnings.push(`${partTypeId} (${prefabName}): ${derived.length} BasePart-derived classes, using the first: ${derived[0]}`);
  }

  const className = derived[0];
  usedClasses.add(className);
  const resolved = dampingOf(className);
  const linear = resolved.linear;
  const angular = resolved.angular;
  if (!linear || !angular) {
    missing.push(`${partTypeId} (${part.name}): class ${className} declares no damping (${prefabName})`);
    continue;
  }

  const ramp = dampingRampOf(className, { linear: linear.value, angular: angular.value });
  parts[partTypeId] = {
    partTypeId: Number(partTypeId),
    name: part.name,
    prefab: prefabName,
    className,
    linear: linear.value,
    angular: angular.value,
    source: `${basename(classes.files.get(linear.declaringClass) ?? "")}:${linear.method}`,
    ...(ramp ? { dampingRamp: ramp } : {}),
  };
}

if (missing.length > 0) {
  fail(`damping is unknown for ${missing.length} content part(s):\n  ${missing.join("\n  ")}`);
}

// The class table is the invariant: a source-level drift (or a mis-parse) fails here. `BasePart`
// is the baseline even when no content part carries it directly, because every class that declares
// no override must inherit exactly its values.
const drifts = [];
const resolveEntry = (className) => {
  const resolved = dampingOf(className);
  if (!resolved.linear || !resolved.angular) {
    drifts.push(`${className}: declares no damping in ${SPAWN_METHODS.join(" or ")}`);
    return null;
  }
  return {
    linear: resolved.linear.value,
    angular: resolved.angular.value,
    linearSource: `${basename(classes.files.get(resolved.linear.declaringClass) ?? "")} (${resolved.linear.declaringClass}.${resolved.linear.method})`,
    angularSource: `${basename(classes.files.get(resolved.angular.declaringClass) ?? "")} (${resolved.angular.declaringClass}.${resolved.angular.method})`,
  };
};

const classTable = {};
const baseline = resolveEntry("BasePart");
if (baseline) {
  classTable.BasePart = baseline;
  if (baseline.linear !== EXPECTED_CLASS_DAMPING.BasePart[0] || baseline.angular !== EXPECTED_CLASS_DAMPING.BasePart[1]) {
    drifts.push(`BasePart: extracted ${[baseline.linear, baseline.angular].join(" / ")}, expected ${EXPECTED_CLASS_DAMPING.BasePart.join(" / ")}`);
  }
}
for (const className of [...usedClasses].sort()) {
  const entry = resolveEntry(className);
  if (!entry) {
    continue;
  }
  classTable[className] = entry;
  const expected = EXPECTED_CLASS_DAMPING[className] ?? (baseline ? [baseline.linear, baseline.angular] : null);
  if (expected && (entry.linear !== expected[0] || entry.angular !== expected[1])) {
    drifts.push(`${className}: extracted ${[entry.linear, entry.angular].join(" / ")}, expected ${expected.join(" / ")}`);
  }
}

// The ramp set is the second invariant: exactly the classes EXPECTED_DAMPING_RAMPS names declare a
// `FixedUpdate` ramp, and every part of such a class carries it.
const rampedClasses = [...classRamps.entries()].filter(([, ramp]) => ramp !== null).map(([name]) => name).sort();
const expectedRamped = Object.keys(EXPECTED_DAMPING_RAMPS).sort();
if (rampedClasses.join(",") !== expectedRamped.join(",")) {
  fail(`classes that ramp their damping at runtime: ${rampedClasses.join(", ") || "none"}; expected ${expectedRamped.join(", ")}`);
}
for (const part of Object.values(parts)) {
  if (rampedClasses.includes(part.className) !== (part.dampingRamp !== undefined)) {
    fail(`part ${part.partTypeId} (${part.name}) of class ${part.className} carries the ramp inconsistently.`);
  }
}
const rampedParts = Object.values(parts).filter((part) => part.dampingRamp !== undefined).length;
const rampTable = Object.fromEntries(rampedClasses.map((name) => [name, classRamps.get(name)]));

// Any other method that assigns damping must be a known runtime override, so a new one is caught
// instead of silently overwritten every step.
const assignmentMethods = collectAssignmentMethods(classes, [...usedClasses, "FanPropeller", "Rope", "INContraption", "HingePlate", "Pig", "GoldenPig"]);
const unknownMethods = [];
const runtimeOverrides = {};
for (const [site, method] of [...assignmentMethods].sort()) {
  if (SPAWN_METHODS.includes(method)) {
    continue;
  }
  if (!RUNTIME_METHODS.has(method)) {
    unknownMethods.push(`${site} assigns damping from ${method}, which is neither a spawn method nor a known runtime override`);
    continue;
  }

  runtimeOverrides[site] = {
    method,
    // `Pig.FixedUpdate` is the one runtime override content models; the rest are either a player
    // setting the vanilla declaration defaults leave off, or a part family PigForge does not have
    // (docs/specs/body-defaults.md §6.4).
    disposition: rampedClasses.includes(site.split(".")[0]) ? "content:capabilities.dampingRamp" : "not modelled",
  };
}
if (drifts.length > 0 || unknownMethods.length > 0) {
  fail([...drifts, ...unknownMethods].join("\n"));
}

const settings = readSettings();
if (settings.maximumAngularSpeed === null) {
  fail(`${settings.source} declares no m_DefaultMaxAngularSpeed.`);
}
// The pair `BasePart.EnsureRigidbody` writes on every part, i.e. what content declares once at the
// document level; a part only carries its own `damping` where its class overrides it.
settings.damping = { linear: baseline.linear, angular: baseline.angular };

const histogram = {};
for (const part of Object.values(parts)) {
  const key = `${part.linear} / ${part.angular}`;
  histogram[key] = (histogram[key] ?? 0) + 1;
}

const dynamicParts = content.parts.filter((part) => part.mode === "dynamic").length;
const report = {
  format: "pigforge.bple-part-damping",
  schemaVersion: 2,
  source: BPLE,
  physics: settings,
  classTable,
  dampingRamps: rampTable,
  runtimeOverrides,
  counts: {
    contentParts: content.parts.length,
    dynamicParts,
    reportedParts: Object.keys(parts).length,
    rampedParts,
  },
  histogram,
  parts,
  warnings,
};

if (flag("write")) {
  writeJsonArtifact(OUT_JSON, report);
  const markdown = [
    "# BPLE per-part damping",
    "",
    `Source: ${BPLE}`,
    "",
    "| class | linear | angular | provenance |",
    "|---|---|---|---|",
    ...Object.entries(classTable).map(([name, entry]) => `| ${name} | ${entry.linear} | ${entry.angular} | ${entry.linearSource} |`),
    "",
    `Project default: maxAngularSpeed ${settings.maximumAngularSpeed} rad/s, gravity ${JSON.stringify(settings.gravity)}, solver iterations ${settings.solverIterations}, fixed timestep ${settings.fixedTimeStep}`,
    "",
    `Histogram: ${Object.entries(histogram).map(([key, value]) => `${key} x${value}`).join(", ")}`,
    "",
    `Runtime damping ramps (content \`capabilities.dampingRamp\`, ${rampedParts} parts): ${
      rampedClasses.map((name) => `${name} { ${Object.entries(rampTable[name]).filter(([key]) => key !== "source").map(([key, value]) => `${key}: ${value}`).join(", ")} } (${rampTable[name].source})`).join(", ") || "none"
    }`,
    "",
    `Runtime overrides: ${Object.entries(runtimeOverrides).map(([site, entry]) => `${site} (${entry.disposition})`).join(", ") || "none"}`,
  ];
  writeMarkdownArtifact(OUT_MD, markdown);
  console.log(`wrote ${OUT_JSON} and ${OUT_MD}`);
} else {
  process.stdout.write(`${JSON.stringify(report, null, 2)}\n`);
}
