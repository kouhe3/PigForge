// Joint capability extractor: reads each part prefab's `m_jointConnectionType` and reports the
// value per PigForge partTypeId. This is the ONLY admissible source for joint capability in
// content/parts.json -- the same rule tools/bple-materials established for material values.
//
// Why it matters: the original decides whether two adjacent parts are jointed at all
// (Contraption.cs:690):
//
//   if (part1.m_jointConnectionType != None && part2.m_jointConnectionType != None
//       && (part2.m_jointConnectionType == Source || part1.m_jointConnectionType == Source))
//
// so a part whose value is None can never be jointed to anything -- it can only be attached by
// being enclosed in a frame (Frame.cs:44-50). Measured on the original: 109 prefabs are None
// (pig, king pig, egg, engine, sandbag, balloon ...), 38 are Source (the frames), 196 are Target.
//
// Enclosure capability is a CLASS property, not a prefab field: `BasePart.CanBeEnclosed()`
// (BasePart.cs:1148-1165) returns false unless an IN feature is on, and 13 subclasses override it
// with a body that resolves to `true`; `BasePart.CanEncloseParts()` (BasePart.cs:1142-1145) is
// false and `Frame.CanEncloseParts()` (Frame.cs:32-35) is true, with `BoxFrame` overriding it back
// to false (BoxFrame.cs:3-6). This tool therefore resolves every part prefab's class through its
// `m_Script` guid, walks the base-class chain, and reports what the chain actually returns. The
// override tables and the resulting histograms are hard-asserted (measured 2026-10-06), so a
// changed original fails the tool instead of silently producing a different world.
//
// Usage: node tools/bple-joints/extract-joints.mjs [--bple <path>] [--json <path>] [--md <path>]
import { existsSync, mkdirSync, readFileSync, readdirSync, writeFileSync } from "node:fs";
import { basename, dirname, join, resolve } from "node:path";
import { fileURLToPath } from "node:url";

const HERE = dirname(fileURLToPath(import.meta.url));
const REPO = resolve(HERE, "..", "..");

function arg(name, fallback) {
  const index = process.argv.indexOf(`--${name}`);
  return index >= 0 && process.argv[index + 1] ? process.argv[index + 1] : fallback;
}

const BPLE = resolve(arg("bple", process.env.BPLE_ROOT ?? join(REPO, "..", "BPLE_Unity6")));
const OUT_JSON = resolve(arg("json", join(REPO, "tasks", "bple-joints-report.json")));
const OUT_MD = resolve(arg("md", join(REPO, "tasks", "bple-joints-report.md")));
const ASSETS = join(BPLE, "Assets");
const SCRIPTS = join(ASSETS, "Scripts", "Assembly-CSharp");
const GAMEOBJECT = join(ASSETS, "GameObject");
const CONTENT_PARTS = join(REPO, "content", "parts.json");
const TEXTURE_MAP = join(REPO, "tools", "bple-textures", "part-map.json");

if (!existsSync(GAMEOBJECT) || !existsSync(SCRIPTS)) {
  console.error(`BPLE project not found: ${ASSETS}\nPass --bple <path to BPLE_Unity6>.`);
  process.exit(1);
}

// Unity's enum, spelled out so a report reader does not have to decode integers.
const JOINT_TYPES = { 0: "none", 1: "source", 2: "target" };

// The original's `JointConnectionStrength` enum (BasePart.cs:130-137), spelled out for the same
// reason. The floats it resolves to live in GameData.asset:101-105 and are applied in code
// (Contraption.cs:1494-1506), so the report keeps the enum name and its raw value.
const JOINT_STRENGTHS = { 0: "weak", 1: "normal", 2: "high", 3: "extreme", 4: "highlyExtreme" };

// `m_jointConnectionDirection` (BasePart.cs:118-127): the sides a part may weld on. Any = all
// four, None = none; the two paired values are the original's LeftAndRight / UpAndDown. The
// client's build-time alignment only lets a part snap on the sides it can actually connect on.
const JOINT_DIRECTIONS = {
  0: "any",
  1: "right",
  2: "up",
  3: "left",
  4: "down",
  5: "leftAndRight",
  6: "upAndDown",
  7: "none",
};

const warnings = [];

// ------------------------------------------------------------------ script index
// Unity serializes a MonoBehaviour as a script guid, so a prefab's class is only readable once
// the guid is mapped back to the `.cs` file beside its `.meta`. Reused from
// tools/bple-springs/extract-springs.mjs so both tools resolve classes the same way.
const CLASS_DECLARATION = /^\s*(?:public |internal )?(?:sealed |abstract |partial |static |unsafe )*class\s+(\w+)\s*:\s*([\w<>,.\s]*?)\s*\{?\s*$/gm;

/** The first class declared in a file; `null` for files that only declare enums/structs. */
function firstClassName(text) {
  return /^\s*(?:public |internal )?(?:sealed |abstract |partial |static |unsafe )*class\s+(\w+)\b/m.exec(text)?.[1] ?? null;
}

/** guid -> class name, from the `.cs.meta` file's guid and the class declared in the `.cs`. */
function buildGuidIndex() {
  const byGuid = new Map();
  for (const entry of readdirSync(SCRIPTS)) {
    if (!entry.endsWith(".cs.meta")) continue;
    const meta = /^guid:\s*([0-9a-f]{32})/m.exec(readFileSync(join(SCRIPTS, entry), "utf8"));
    if (!meta) continue;
    const file = basename(entry, ".cs.meta");
    const source = join(SCRIPTS, `${file}.cs`);
    const declared = existsSync(source) ? firstClassName(readFileSync(source, "utf8")) : null;
    byGuid.set(meta[1], declared ?? file);
  }
  return byGuid;
}

/** class name -> base class name, for every class in the assembly. */
function buildClassBases() {
  const bases = new Map();
  for (const entry of readdirSync(SCRIPTS)) {
    if (!entry.endsWith(".cs")) continue;
    for (const match of readFileSync(join(SCRIPTS, entry), "utf8").matchAll(CLASS_DECLARATION)) {
      // A generic base (`PartManager<T>`) is kept verbatim: it simply does not resolve further.
      bases.set(match[1], match[2].split(",")[0].trim());
    }
  }
  return bases;
}

const guidIndex = buildGuidIndex();
const classBases = buildClassBases();

/** `className` and every ancestor, nearest first. C# forbids cycles, but the guard keeps a
 * malformed source from hanging the tool. */
function classChain(className) {
  const chain = [];
  const seen = new Set();
  for (let name = className; name && !seen.has(name); name = classBases.get(name)) {
    seen.add(name);
    chain.push(name);
  }
  return chain;
}

const derivesFromBasePart = (className) => classChain(className).includes("BasePart");

/** The `{...}` body of one boolean override, or `null` when the class does not declare it. */
function readOverrideBody(text, method) {
  const signature = new RegExp(`override\\s+bool\\s+${method}\\s*\\(\\s*\\)\\s*\\{`).exec(text);
  if (!signature) return null;
  let depth = 0;
  for (let index = signature.index + signature[0].length - 1; index < text.length; index += 1) {
    if (text[index] === "{") depth += 1;
    else if (text[index] === "}" && --depth === 0) return text.slice(signature.index + signature[0].length, index);
  }
  return null;
}

/** `true`/`false` when the body returns a literal, `other` when it delegates or is conditional
 * (CustomPart forwards to its injected part). `other` is never counted as `true`. */
function overrideValue(body) {
  const returned = /\breturn\s+([^;]+);/.exec(body)?.[1]?.trim();
  if (returned === "true") return "true";
  if (returned === "false") return "false";
  return "other";
}

/** method -> (class name -> { value, body, declaringClass }), over every script in the assembly. */
function buildOverrideTable(method) {
  const byClass = new Map();
  for (const entry of readdirSync(SCRIPTS)) {
    if (!entry.endsWith(".cs")) continue;
    const text = readFileSync(join(SCRIPTS, entry), "utf8");
    const body = readOverrideBody(text, method);
    if (body === null) continue;
    byClass.set(firstClassName(text) ?? basename(entry, ".cs"), { value: overrideValue(body), body: body.replace(/\s+/g, " ").trim() });
  }
  return byClass;
}

const overrideTable = {
  canBeEnclosed: buildOverrideTable("CanBeEnclosed"),
  canEncloseParts: buildOverrideTable("CanEncloseParts"),
};

/** What `className` returns for one of the two enclosure methods: the nearest declaration in the
 * chain wins, `null` means no class in the chain overrides it (the virtual base decides). */
function resolveEnclosure(className, method) {
  for (const name of classChain(className)) {
    const override = overrideTable[method].get(name);
    if (override) return { ...override, declaringClass: name };
  }
  return null;
}

/** The prefab's part class. Every `Part_*.prefab` carries exactly one BasePart-derived
 * MonoBehaviour; 0 or 2+ of them is a source-tree change the caller has to see. */
function partClassOf(text) {
  const guids = [...new Set([...text.matchAll(/m_Script:\s*\{fileID:\s*-?\d+,\s*guid:\s*([0-9a-f]{32})/g)]
    .map((match) => guidIndex.get(match[1]))
    .filter((value) => value !== undefined))];
  const derived = guids.filter(derivesFromBasePart);
  return derived.length === 1 ? derived[0] : null;
}

/** Enclosure capability of one prefab, resolved from its class chain -- never from its name.
 * `canEncloseParts` = the class or an ancestor overrides `CanEncloseParts()` to return true;
 * `canBeEnclosed` = same for `CanBeEnclosed()`. The `*Value` fields keep the raw resolution
 * (`true` / `false` / `other` / `inherited`) for the report and the histograms. */
function enclosureOf(text, prefabName) {
  const partClass = partClassOf(text);
  const enclosing = resolveEnclosure(partClass, "canEncloseParts");
  const enclosed = resolveEnclosure(partClass, "canBeEnclosed");
  return {
    prefabName,
    partClass,
    canEncloseParts: enclosing?.value === "true",
    canBeEnclosed: enclosed?.value === "true",
    canEnclosePartsValue: enclosing?.value ?? "inherited",
    canBeEnclosedValue: enclosed?.value ?? "inherited",
    canEnclosePartsClass: enclosing?.declaringClass ?? null,
    canBeEnclosedClass: enclosed?.declaringClass ?? null,
  };
}

function prefabText(name) {
  const path = join(GAMEOBJECT, `${name}.prefab`);
  return existsSync(path) ? readFileSync(path, "utf8") : null;
}

/** The first `m_jointConnectionType` in the file. Every part prefab carries exactly one, on the
 * BasePart MonoBehaviour that owns the part's physics behaviour. */
function readJointType(text) {
  const match = /^\s*m_jointConnectionType:\s*(-?\d+)\s*$/m.exec(text);
  if (!match) {
    return null;
  }

  const raw = Number(match[1]);
  return { raw, name: JOINT_TYPES[raw] ?? `unknown(${raw})` };
}

function readJointStrength(text) {
  const match = /^\s*m_jointConnectionStrength:\s*(-?\d+)\s*$/m.exec(text);
  if (!match) {
    return null;
  }

  const raw = Number(match[1]);
  return { raw, name: JOINT_STRENGTHS[raw] ?? `unknown(${raw})` };
}

/** `m_jointPreprocessing` only toggles `Joint.enablePreprocessing` (Contraption.cs:1540); it is
 * not a strength gate, but its 316/27 split is a stable fingerprint of the source tree. */
function readJointPreprocessing(text) {
  const match = /^\s*m_jointPreprocessing:\s*(-?\d+)\s*$/m.exec(text);
  return match ? Number(match[1]) : null;
}

function readJointDirection(text) {
  const match = /^\s*m_jointConnectionDirection:\s*(-?\d+)\s*$/m.exec(text);
  if (!match) {
    return null;
  }

  const raw = Number(match[1]);
  return { raw, name: JOINT_DIRECTIONS[raw] ?? `unknown(${raw})` };
}

/** `m_jointType` selects the Unity joint kind: 0 FixedJoint, 1 HingeJoint (the wheels). */
function readJointKind(text) {
  const match = /^\s*m_jointType:\s*(-?\d+)\s*$/m.exec(text);
  return match ? Number(match[1]) : null;
}

/** partTypeId -> prefab name, reusing the mapping the shapes/textures extractors established so
 * this tool cannot drift into a second convention. */
function loadAssignments() {
  const map = JSON.parse(readFileSync(TEXTURE_MAP, "utf8"));
  const byPart = new Map();
  // `parts` covers the 44 bases, `variants` the 223 imported skins; both map partTypeId -> the
  // prefab the part was extracted from, and either may be null for a PigForge-only invention.
  for (const section of ["parts", "variants"]) {
    for (const [partTypeId, prefab] of Object.entries(map[section] ?? {})) {
      if (typeof prefab === "string" && prefab.length > 0) {
        byPart.set(Number(partTypeId), prefab);
      }
    }
  }

  return byPart;
}

const assignments = loadAssignments();
const content = JSON.parse(readFileSync(CONTENT_PARTS, "utf8"));
const nameByPart = new Map(content.parts.map((part) => [part.partTypeId, part.name ?? ""]));

const parts = {};
const unmapped = [];
for (const part of content.parts) {
  const prefab = assignments.get(part.partTypeId) ?? null;
  if (!prefab) {
    unmapped.push(part.partTypeId);
    continue;
  }

  const text = prefabText(prefab);
  if (text === null) {
    warnings.push(`part ${part.partTypeId} (${prefab}): prefab file missing`);
    continue;
  }

  const joint = readJointType(text);
  if (joint === null) {
    warnings.push(`part ${part.partTypeId} (${prefab}): m_jointConnectionType absent`);
    continue;
  }

  const enclosure = enclosureOf(text, prefab);
  parts[part.partTypeId] = {
    prefab,
    name: nameByPart.get(part.partTypeId) ?? "",
    jointType: joint.name,
    rawJointType: joint.raw,
    jointStrength: readJointStrength(text),
    jointPreprocessing: readJointPreprocessing(text),
    jointKind: readJointKind(text),
    jointConnectionDirection: readJointDirection(text),
    partClass: enclosure.partClass,
    canEncloseParts: enclosure.canEncloseParts,
    canBeEnclosed: enclosure.canBeEnclosed,
  };
}

// Whole-project tally, so the report can state the distribution even for prefabs that never made
// it into content (dropped inventions, unimported parts).
const distribution = {};
const prefabScan = {
  count: 0,
  strengthHistogram: {},
  preprocessingHistogram: {},
  jointKindHistogram: {},
  jointConnectionDirectionHistogram: {},
  partClassHistogram: {},
  canEnclosePartsPrefabs: {},
  canBeEnclosedPrefabs: {},
  canEnclosePartsValues: {},
  canBeEnclosedValues: {},
  unresolvedClassPrefabs: [],
  prefabsByClass: {},
  parts: {},
};
for (const entry of readdirSync(GAMEOBJECT)) {
  if (!entry.startsWith("Part_") || !entry.endsWith(".prefab")) {
    continue;
  }

  const text = readFileSync(join(GAMEOBJECT, entry), "utf8");
  const joint = readJointType(text);
  const key = joint ? joint.name : "absent";
  distribution[key] = (distribution[key] ?? 0) + 1;

  // Every part prefab declares exactly one strength, preprocessing flag and joint kind, so the
  // histograms double as a fingerprint: a move here means the extraction must be re-derived
  // before anything trusts it.
  const strength = readJointStrength(text);
  const preprocessing = readJointPreprocessing(text);
  const kind = readJointKind(text);
  const direction = readJointDirection(text);
  const strengthKey = strength ? strength.name : "absent";
  const preprocessingKey = preprocessing === null ? "absent" : String(preprocessing);
  const kindKey = kind === null ? "absent" : String(kind);
  prefabScan.count += 1;
  prefabScan.strengthHistogram[strengthKey] = (prefabScan.strengthHistogram[strengthKey] ?? 0) + 1;
  prefabScan.preprocessingHistogram[preprocessingKey] = (prefabScan.preprocessingHistogram[preprocessingKey] ?? 0) + 1;
  prefabScan.jointKindHistogram[kindKey] = (prefabScan.jointKindHistogram[kindKey] ?? 0) + 1;
  prefabScan.parts[entry] = [strength ? strength.raw : null, preprocessing, kind];
  const directionKey = direction ? direction.name : "absent";
  prefabScan.jointConnectionDirectionHistogram[directionKey] = (prefabScan.jointConnectionDirectionHistogram[directionKey] ?? 0) + 1;

  // Enclosure is keyed by the prefab's class, not by its name: the histogram is the fingerprint
  // that the class-override table below is being read correctly.
  const enclosure = enclosureOf(text, entry.slice(0, -".prefab".length));
  const classKey = enclosure.partClass ?? "unresolved";
  if (enclosure.partClass === null) {
    prefabScan.unresolvedClassPrefabs.push(entry);
  }

  prefabScan.partClassHistogram[classKey] = (prefabScan.partClassHistogram[classKey] ?? 0) + 1;
  prefabScan.canEnclosePartsValues[enclosure.canEnclosePartsValue] = (prefabScan.canEnclosePartsValues[enclosure.canEnclosePartsValue] ?? 0) + 1;
  prefabScan.canBeEnclosedValues[enclosure.canBeEnclosedValue] = (prefabScan.canBeEnclosedValues[enclosure.canBeEnclosedValue] ?? 0) + 1;
  (prefabScan.prefabsByClass[classKey] ??= []).push(enclosure.prefabName);
  if (enclosure.canEncloseParts) {
    prefabScan.canEnclosePartsPrefabs[classKey] = (prefabScan.canEnclosePartsPrefabs[classKey] ?? 0) + 1;
  }

  if (enclosure.canBeEnclosed) {
    prefabScan.canBeEnclosedPrefabs[classKey] = (prefabScan.canBeEnclosedPrefabs[classKey] ?? 0) + 1;
  }
}

// readdir order is filesystem-dependent; sort so two machines write byte-identical reports.
for (const names of Object.values(prefabScan.prefabsByClass)) {
  names.sort();
}

/** class -> its prefab names, for every class whose chain resolves `method` to true. */
function enclosurePrefabsByClass(method) {
  const byClass = {};
  for (const [className, names] of Object.entries(prefabScan.prefabsByClass)) {
    if (resolveEnclosure(className, method)?.value === "true") {
      byClass[className] = names;
    }
  }
  return byClass;
}

/** The declared overrides of one method, sorted by class, for the report and the md. */
const overrideSummary = (method) => [...overrideTable[method].entries()]
  .map(([className, override]) => ({ className, value: override.value, body: override.body }))
  .sort((left, right) => left.className.localeCompare(right.className));

// Hard invariants: measured on BPLE_Unity6 (343 Part_*.prefab, strength 45/120/98/60/20,
// preprocessing 316/27, joint kind 302/41). Drift fails the tool instead of silently writing a
// different world into content/parts.json.
const sameHistogram = (actual, expected) =>
  [...new Set([...Object.keys(actual), ...Object.keys(expected)])].every(
    (key) => (actual[key] ?? 0) === (expected[key] ?? 0),
  );
const invariants = [];
if (prefabScan.count !== 343) {
  invariants.push(`prefab count: expected 343, got ${prefabScan.count}`);
}
if (!sameHistogram(prefabScan.strengthHistogram, { weak: 45, normal: 120, high: 98, extreme: 60, highlyExtreme: 20 })) {
  invariants.push(`strength histogram: expected weak 45 / normal 120 / high 98 / extreme 60 / highlyExtreme 20, got ${JSON.stringify(prefabScan.strengthHistogram)}`);
}
if (!sameHistogram(prefabScan.preprocessingHistogram, { 0: 316, 1: 27 })) {
  invariants.push(`preprocessing histogram: expected 0:316 / 1:27, got ${JSON.stringify(prefabScan.preprocessingHistogram)}`);
}
if (!sameHistogram(prefabScan.jointKindHistogram, { 0: 302, 1: 41 })) {
  invariants.push(`joint kind histogram: expected 0:302 / 1:41, got ${JSON.stringify(prefabScan.jointKindHistogram)}`);
}

// ---------------------------------------------------------------------------------------------
// Enclosure invariants. Every number below was measured on BPLE_Unity6 on 2026-10-06 with
// `grep -rn "override bool CanBeEnclosed" .` (and the CanEncloseParts twin) plus a body read of
// each hit, so a moved source tree fails this tool instead of writing a different world.
// ---------------------------------------------------------------------------------------------
const CAN_BE_ENCLOSED_OVERRIDES = ["CustomPart", "Egg", "Engine", "Gearbox", "GoldenPig", "HingePlate", "KingPig", "Pig", "PointLight", "Pumpkin", "SpringBoxingGlove", "TNT", "TimeBomb"];
const CAN_ENCLOSE_PARTS_OVERRIDES = ["BoxFrame", "CustomPart", "Frame"];
// Prefabs per class whose class chain resolves `CanBeEnclosed()` to true: 93 of 343 prefabs.
const CAN_BE_ENCLOSED_PREFABS = { AlienEgg: 1, AlienPointLight: 1, AlienTNT: 1, AutoControlLight: 1, BlasterTNT: 1, DecelerationLight: 1, Egg: 5, Engine: 24, Gearbox: 6, GoldenPig: 4, HingePlate: 4, KingPig: 7, Lantern: 1, Mushroom: 1, Pig: 20, PointLight: 2, Pumpkin: 2, SpringBoxingGlove: 5, TNT: 5, TimeBomb: 1 };
// Prefabs per class whose chain resolves `CanEncloseParts()` to true: 24 of 343 prefabs.
const CAN_ENCLOSE_PARTS_PREFABS = { ColoredFrame: 1, Frame: 23 };
const CAN_BE_ENCLOSED_VALUES = { inherited: 249, other: 1, true: 93 };
const CAN_ENCLOSE_PARTS_VALUES = { false: 2, inherited: 316, other: 1, true: 24 };
const sameSet = (actual, expected) => [...actual].sort().join("\u0000") === [...expected].sort().join("\u0000");
const difference = (left, right) => [...new Set(left)].filter((value) => !right.includes(value)).sort();

if (!sameSet(overrideTable.canBeEnclosed.keys(), CAN_BE_ENCLOSED_OVERRIDES)) {
  invariants.push(`CanBeEnclosed overrides: expected ${JSON.stringify(CAN_BE_ENCLOSED_OVERRIDES)}, got ${JSON.stringify([...overrideTable.canBeEnclosed.keys()].sort())}`);
}
if (!sameSet(overrideTable.canEncloseParts.keys(), CAN_ENCLOSE_PARTS_OVERRIDES)) {
  invariants.push(`CanEncloseParts overrides: expected ${JSON.stringify(CAN_ENCLOSE_PARTS_OVERRIDES)}, got ${JSON.stringify([...overrideTable.canEncloseParts.keys()].sort())}`);
}
if (prefabScan.unresolvedClassPrefabs.length !== 0) {
  invariants.push(`prefabs without exactly one BasePart-derived class: ${JSON.stringify(prefabScan.unresolvedClassPrefabs)}`);
}
if (!sameHistogram(prefabScan.canBeEnclosedPrefabs, CAN_BE_ENCLOSED_PREFABS)) {
  invariants.push(`canBeEnclosed prefabs by class: expected ${JSON.stringify(CAN_BE_ENCLOSED_PREFABS)}, got ${JSON.stringify(prefabScan.canBeEnclosedPrefabs)}`);
}
if (!sameHistogram(prefabScan.canEnclosePartsPrefabs, CAN_ENCLOSE_PARTS_PREFABS)) {
  invariants.push(`canEncloseParts prefabs by class: expected ${JSON.stringify(CAN_ENCLOSE_PARTS_PREFABS)}, got ${JSON.stringify(prefabScan.canEnclosePartsPrefabs)}`);
}
if (!sameHistogram(prefabScan.canBeEnclosedValues, CAN_BE_ENCLOSED_VALUES)) {
  invariants.push(`canBeEnclosed resolution values: expected ${JSON.stringify(CAN_BE_ENCLOSED_VALUES)}, got ${JSON.stringify(prefabScan.canBeEnclosedValues)}`);
}
if (!sameHistogram(prefabScan.canEnclosePartsValues, CAN_ENCLOSE_PARTS_VALUES)) {
  invariants.push(`canEncloseParts resolution values: expected ${JSON.stringify(CAN_ENCLOSE_PARTS_VALUES)}, got ${JSON.stringify(prefabScan.canEnclosePartsValues)}`);
}

// Cross-check `canEncloseParts` against the prefab-NAME criterion this tool used until 2026-10-06
// (`/^Part_(WoodenFrame|MetalFrame)_/`). The two sets are NOT identical, and the difference is
// evidence, not slop:
//   * Part_WoodenFrame_11_SET / Part_MetalFrame_131_SET are `BoxFrame`, which overrides
//     CanEncloseParts() back to false (BoxFrame.cs:3-6): the name criterion wrongly claimed them;
//   * Part_ColoredFrame is `ColoredFrame : Frame` with no override, so it DOES enclose and the
//     name criterion missed it.
// Both sides are pinned exactly, so neither can drift. They agree on every prefab that is mapped
// to a PigForge partTypeId, which is why content/parts.json's `canEnclose` values do not move.
const canEnclosePartsPrefabs = Object.values(enclosurePrefabsByClass("canEncloseParts")).flat();
const nameCriterionPrefabs = readdirSync(GAMEOBJECT)
  .filter((entry) => /^Part_(WoodenFrame|MetalFrame)_.*\.prefab$/.test(entry))
  .map((entry) => entry.slice(0, -".prefab".length));
const nameOnly = difference(nameCriterionPrefabs, canEnclosePartsPrefabs);
const classOnly = difference(canEnclosePartsPrefabs, nameCriterionPrefabs);
if (!sameSet(nameOnly, ["Part_MetalFrame_131_SET", "Part_WoodenFrame_11_SET"])) {
  invariants.push(`name criterion minus canEncloseParts: expected the two BoxFrame prefabs, got ${JSON.stringify(nameOnly)}`);
}
if (!sameSet(classOnly, ["Part_ColoredFrame"])) {
  invariants.push(`canEncloseParts minus name criterion: expected Part_ColoredFrame, got ${JSON.stringify(classOnly)}`);
}
const mappedPrefabDisagreement = [...new Set(assignments.values())]
  .filter((prefab) => nameCriterionPrefabs.includes(prefab) !== canEnclosePartsPrefabs.includes(prefab));
if (mappedPrefabDisagreement.length !== 0) {
  invariants.push(`canEncloseParts disagrees with the name criterion on mapped prefabs: ${JSON.stringify(mappedPrefabDisagreement.sort())}`);
}

if (invariants.length > 0) {
  console.error("extract-joints: source-tree invariants changed:");
  for (const invariant of invariants) {
    console.error(`  - ${invariant}`);
  }

  process.exit(1);
}

const report = {
  bple: BPLE,
  distribution,
  prefabScan,
  enclosureOverrides: {
    canBeEnclosed: overrideSummary("canBeEnclosed"),
    canEncloseParts: overrideSummary("canEncloseParts"),
  },
  warnings,
  unmapped,
  parts,
};
mkdirSync(dirname(OUT_JSON), { recursive: true });
writeFileSync(OUT_JSON, `${JSON.stringify(report, null, 2)}\n`);

const byType = (wanted) =>
  Object.entries(parts)
    .filter(([, value]) => value.jointType === wanted)
    .sort((left, right) => Number(left[0]) - Number(right[0]));

const md = [];
md.push("# 原版关节能力报告（`m_jointConnectionType`）", "");
md.push("来源：`tools/bple-joints/extract-joints.mjs`，扫描原版全部 `Part_*.prefab`。");
md.push("规则出处：`Contraption.cs:690` —— 两端都不是 `None` 且至少一端是 `Source` 才建立关节。", "");
md.push("## 全量分布（原版 prefab 计数）", "");
md.push("| 值 | 数量 |", "|---|---|");
for (const [key, count] of Object.entries(distribution).sort((a, b) => b[1] - a[1])) {
  md.push(`| \`${key}\` | ${count} |`);
}

md.push("", "## PigForge 内容里的取值", "");
for (const wanted of ["none", "source", "target"]) {
  const rows = byType(wanted);
  md.push(`### ${wanted}（${rows.length} 条）`, "");
  for (const [partTypeId, value] of rows) {
    const strength = value.jointStrength === null ? "?" : `${value.jointStrength.name} (${value.jointStrength.raw})`;
    const enclosure = [value.canEncloseParts ? "可包裹" : null, value.canBeEnclosed ? "可入框" : null].filter(Boolean).join(" + ");
    md.push(`- \`${partTypeId}\` ${value.name} — \`${value.prefab}\`，强度 ${strength}${enclosure ? `，${enclosure}` : ""}`);
  }

  md.push("");
}

md.push("## 包裹能力（类覆写，2026-10-06 实测）", "");
md.push("vanilla 声明默认档 `EnclosableParts = false`（`INDeclarationSettingsExp.json`），所以 `BasePart.CanBeEnclosed()`（BasePart.cs:1148-1165）一律返回 false；在 vanilla 下能入框的件，全部来自「自己的类（或祖先）覆写 `CanBeEnclosed()` 并返回 true」。`BasePart.CanEncloseParts()`（BasePart.cs:1142-1145）同样默认 false，`Frame.CanEncloseParts()`（Frame.cs:32-35）覆写为 true，包裹焊接本身没有 IN 门控（Frame.cs:42-56）。");
md.push("");
md.push("### 覆写名单与函数体", "");
md.push("| 类 | 覆写 | 函数体 | 解析值 | prefab 数 |", "|---|---|---|---|---|");
for (const { className, value, body } of report.enclosureOverrides.canBeEnclosed) {
  md.push(`| \`${className}\` | \`CanBeEnclosed()\` | \`${body}\` | ${value} | ${(prefabScan.prefabsByClass[className] ?? []).length} |`);
}

for (const { className, value, body } of report.enclosureOverrides.canEncloseParts) {
  md.push(`| \`${className}\` | \`CanEncloseParts()\` | \`${body}\` | ${value} | ${(prefabScan.prefabsByClass[className] ?? []).length} |`);
}

md.push("", "解析值 `true`/`false` 是函数体里 `return` 的字面量；`other` 表示函数体在转发（`CustomPart` 转给 `m_injectionPart`），**不计入 true**。prefab 数按 prefab 自己的类计。", "");

const enclosureTable = (title, method) => {
  const byClass = enclosurePrefabsByClass(method);
  const rows = Object.entries(byClass).sort(([left], [right]) => left.localeCompare(right));
  const total = rows.reduce((sum, [, names]) => sum + names.length, 0);
  md.push(`### ${title}（${total} 个 prefab，${rows.length} 个类）`, "");
  md.push("| 类 | 覆写声明处 | prefab 数 | prefab |", "|---|---|---|---|");
  for (const [className, names] of rows) {
    const declaring = resolveEnclosure(className, method)?.declaringClass ?? "?";
    md.push(`| \`${className}\` | ${declaring === className ? "本类" : `\`${declaring}\``} | ${names.length} | ${names.map((name) => `\`${name}\``).join(", ")} |`);
  }

  md.push("");
};

enclosureTable("能入框的类（`CanBeEnclosed()` → true）", "canBeEnclosed");
enclosureTable("能包裹别的件的类（`CanEncloseParts()` → true）", "canEncloseParts");

md.push("### 与旧「prefab 名字判据」的一致性", "");
md.push("- 旧判据 `/^Part_(WoodenFrame|MetalFrame)_/` 命中 25 个 prefab，新判据（按类覆写）命中 24 个，两者**不完全相同**。");
md.push("- 旧有新无：`Part_WoodenFrame_11_SET`、`Part_MetalFrame_131_SET` —— 两者都是 `BoxFrame`，`BoxFrame.cs:3-6` 把 `CanEncloseParts()` 覆写回 `false`，旧判据在这两件上是错的。");
md.push("- 新有旧无：`Part_ColoredFrame` —— `ColoredFrame : Frame`（ColoredFrame.cs:4）没有覆写，因此确实可包裹，旧判据漏了它。");
md.push("- 在映射到 PigForge partTypeId 的 prefab 上两者完全一致，所以 `content/parts.json` 里已写的 `canEnclose` 取值不变（22 条）。");
md.push("");

if (unmapped.length > 0) {
  md.push("## 未映射到 prefab 的 partTypeId", "", unmapped.join(", "), "");
}

if (warnings.length > 0) {
  md.push("## 警告", "");
  for (const warning of warnings) {
    md.push(`- ${warning}`);
  }

  md.push("");
}

writeFileSync(OUT_MD, `${md.join("\n")}\n`);

console.log(`prefabs: ${Object.values(distribution).reduce((sum, count) => sum + count, 0)} (${JSON.stringify(distribution)})`);
console.log(`strength: ${JSON.stringify(prefabScan.strengthHistogram)}; preprocessing: ${JSON.stringify(prefabScan.preprocessingHistogram)}; joint kind: ${JSON.stringify(prefabScan.jointKindHistogram)}`);
console.log(`jointConnectionDirection: ${JSON.stringify(prefabScan.jointConnectionDirectionHistogram)}`);
console.log(`canBeEnclosed values: ${JSON.stringify(prefabScan.canBeEnclosedValues)} (${Object.values(prefabScan.canBeEnclosedPrefabs).reduce((sum, count) => sum + count, 0)} prefabs true)`);
console.log(`canBeEnclosed prefabs by class: ${JSON.stringify(prefabScan.canBeEnclosedPrefabs)}`);
console.log(`canEncloseParts values: ${JSON.stringify(prefabScan.canEnclosePartsValues)} (${Object.values(prefabScan.canEnclosePartsPrefabs).reduce((sum, count) => sum + count, 0)} prefabs true)`);
console.log(`canEncloseParts prefabs by class: ${JSON.stringify(prefabScan.canEnclosePartsPrefabs)}`);
for (const wanted of ["none", "source", "target"]) {
  const rows = byType(wanted);
  console.log(`${wanted.padEnd(7)} parts=${String(rows.length).padStart(3)}  ${rows.slice(0, 12).map(([id, value]) => `${id}:${value.name}`).join(", ")}`);
}

if (unmapped.length > 0) {
  console.log(`unmapped partTypeIds: ${unmapped.length}`);
}

if (warnings.length > 0) {
  console.log(`warnings: ${warnings.length}`);
}

console.log(`report: ${OUT_JSON}\n        ${OUT_MD}`);
