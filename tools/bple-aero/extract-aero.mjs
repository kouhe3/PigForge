// Aero extractor: reads `m_autoAlign` / `m_flipped` / `liftConstant` / `dragConstant` from the one
// MonoBehaviour block per part -- the block that carries the concrete BasePart subclass and every
// inherited field (Unity serializes one block per MonoBehaviour, so the class, `m_autoAlign` and
// `liftConstant` always sit together) -- and reports the PigForge `capabilities.mirror` /
// `capabilities.wing` / `capabilities.tail` values per partTypeId. This is the ONLY admissible
// source for those three values in content/parts.json, the same rule tools/bple-fans,
// tools/bple-joints, tools/bple-lift and tools/bple-power established for theirs.
//
// Why it matters (docs/specs/part-mirror.md, gaps G05 + G55): the original's build pose has a
// handedness the content model did not have. `BasePart.AutoAlignType` is a per-part prefab field
// (BasePart.cs:79-84); `FlipVertically` (2) makes `Contraption.Flip` call
// `SetFlipped(!IsFlipped())`, which writes `m_flipped` and turns the part 180 degrees about its own
// up axis (BasePart.cs:639-651) -- a horizontal mirror inside the part's own frame, which a float
// yaw cannot express. PigForge had no mirror at all (G05) and its wing lift was a constant world-Y
// force (G55); the two had to land together, because a constant world axis lift makes a mirror
// physically meaningless.
//
// The numbers, straight from the classes:
//
//   BasePart.cs:79-84    AutoAlignType { None = 0, Rotate = 1, FlipVertically = 2 }; the whole
//                        project is 153 / 173 / 17 over the 343 `Part_*.prefab` files, and the 17
//                        are exactly the Wings and Tail families -- 5 WoodenWings, 4 MetalWings,
//                        4 WoodenTail, 4 MetalTail.
//   BasePart.cs:639-651  SetFlipped: writes `m_flipped`, then localRotation =
//                        AngleAxis(180, Vector3.up) (flipped) or identity.
//   Wings.cs:6           `liftConstant` (serialized, per part).
//   Wings.cs:76-88       the wing response curve (a source constant -- not in the prefab, so not
//                        extractable; PigForge keeps it in code).
//   Wings.cs:104-118     FixedUpdate builds the force from `liftConstant` only; `dragConstant`
//                        (Wings.cs:8) is never read by the class.
//   Tail.cs:6            `liftConstant` (the Tail class has no `dragConstant` field at all).
//   Tail.cs:32-41        the tail response curve (source constant).
//   Tail.cs:57-75        FixedUpdate, the same `liftConstant`-only force.
//
// So content carries `wing: { liftConstant }`, `tail: <liftConstant>` and `mirror: true`; the old
// `wing { liftCoef, maxLift }` and `tail 0.03` (a drag coefficient) were PigForge inventions.
// `dragConstant` is REPORTED here as `dragConstantUnused` and never written into content: the
// original never reads it, so writing it would only create a second source of truth.
//
// Usage: node tools/bple-aero/extract-aero.mjs [--bple <path>] [--json <path>] [--md <path>]
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
const OUT_JSON = resolve(arg("json", join(REPO, "tasks", "bple-aero-report.json")));
const OUT_MD = resolve(arg("md", join(REPO, "tasks", "bple-aero-report.md")));
const GAMEOBJECT = join(BPLE, "Assets", "GameObject");
const SCRIPTS = join(BPLE, "Assets", "Scripts", "Assembly-CSharp");
const CONTENT_PARTS = join(REPO, "content", "parts.json");
const TEXTURE_MAP = join(REPO, "tools", "bple-textures", "part-map.json");

if (!existsSync(GAMEOBJECT)) {
  console.error(`BPLE project not found: ${GAMEOBJECT}\nPass --bple <path to BPLE_Unity6>.`);
  process.exit(1);
}

const warnings = [];

const fail = (message) => {
  console.error(message);
  process.exit(1);
};

/** The room's fixed tick rate (`GameRoomOptions.TickRateHz`, PlayHost) and the original's
 * `FixedUpdate` rate; the aero itself converts in the rules layer, but the report states the same
 * 60 the fan/balloon conversions use. */
const TICK_RATE_HZ = 60;

/** The two classes that override `SetRotation`/`SetFlipped` (Wings.cs:130-165, Tail.cs:86-121) and
 * are therefore the only ones the original lets mirror -- and the only ones this tool writes. */
const AERO_CLASSES = ["Wings", "Tail"];

/** The prefab families of those classes, in the order the report lists them (wooden before metal,
 * wings before tails). */
const FAMILIES = ["WoodenWings", "MetalWings", "WoodenTail", "MetalTail"];

/** `BasePart.AutoAlignType` (BasePart.cs:79-84), by value. */
const AUTO_ALIGN = { 0: "none", 1: "rotate", 2: "flipVertically" };

function prefabText(name) {
  const path = join(GAMEOBJECT, `${name}.prefab`);
  return existsSync(path) ? readFileSync(path, "utf8") : null;
}

/** The first `field: value` in the text, or null. Every serialized field appears once per block. */
function readField(text, field) {
  const match = new RegExp(`^\\s*${field}:\\s*(-?[0-9.]+)\\s*$`, "m").exec(text);
  if (!match) {
    return null;
  }

  const value = Number(match[1]);
  return Number.isFinite(value) ? value : null;
}

/** The `--- !u!114 &...` MonoBehaviour block that carries the part's concrete class: the one that
 * serializes `m_autoAlign`. Unity writes ONE block per MonoBehaviour, including the inherited
 * BasePart fields, so that single block also carries `m_Script`, `m_flipped`, `m_eightWay` and --
 * for `Wings` only -- `liftConstant` and `dragConstant`. */
function partBlock(text) {
  const blocks = text.split(/\n--- !u!/);
  return blocks.find((block) => /^\s*m_autoAlign:/m.test(block)) ?? null;
}

/** class name of the script a block references, via the `.cs.meta` guid index (the same index
 * tools/bple-fans, tools/bple-power and tools/bple-springs build). */
function buildGuidIndex() {
  const byGuid = new Map();
  for (const entry of readdirSync(SCRIPTS)) {
    if (!entry.endsWith(".cs.meta")) continue;
    const match = /^guid:\s*([0-9a-f]{32})/m.exec(readFileSync(join(SCRIPTS, entry), "utf8"));
    if (match) byGuid.set(match[1], basename(entry, ".cs.meta"));
  }

  return byGuid;
}

const guidIndex = buildGuidIndex();

/** The script name a block mounts; unresolved guids are a hard error, never a name guess. */
function blockScript(block, prefab) {
  const guid = /m_Script:\s*\{fileID:\s*-?\d+,\s*guid:\s*([0-9a-f]{32})/m.exec(block)?.[1];
  const script = guid ? guidIndex.get(guid) : undefined;
  if (!script) {
    fail(`${prefab}: m_autoAlign block references script '${script ?? "unresolved"}'`);
  }

  return script;
}

/** partTypeId -> prefab name, reusing the mapping the shapes/textures/joints/power extractors
 * established so this tool cannot drift into a second convention. */
function loadAssignments() {
  const map = JSON.parse(readFileSync(TEXTURE_MAP, "utf8"));
  const byPart = new Map();
  for (const section of ["parts", "variants"]) {
    for (const [partTypeId, prefab] of Object.entries(map[section] ?? {})) {
      if (typeof prefab === "string" && prefab.length > 0) {
        byPart.set(Number(partTypeId), prefab);
      }
    }
  }

  return byPart;
}

/** The family a prefab belongs to, by name; null for anything outside the four. */
function familyOf(prefab) {
  return FAMILIES.find((family) => prefab.startsWith(`Part_${family}_`)) ?? null;
}

/** Bump one histogram bucket. The histograms are Maps, not objects: a JSON object reorders an
 * integer-like key (`"1"`) to the front, and the family order is the meaningful one. */
function count(histogram, value) {
  const key = String(value);
  histogram.set(key, (histogram.get(key) ?? 0) + 1);
}

/** The histogram as the report/markdown prints it, in family order. */
const histogramLine = (histogram) => `{ ${[...histogram].map(([key, value]) => `"${key}": ${value}`).join(", ")} }`;

const ROUND6 = (value) => Number(value.toFixed(6));

// ---------------------------------------------------------------- whole-project scan
// The `m_autoAlign` histogram over all 343 prefabs is the fingerprint: 153 None / 173 Rotate /
// 17 FlipVertically, and the 17 are exactly the Wings and Tail families.
const scan = {
  autoAlign: { none: 0, rotate: 0, flipVertically: 0 },
  flipVertically: [],
  classHistogram: {},
};
const wingLiftConstant = new Map();
const tailLiftConstant = new Map();
const dragConstantUnused = new Map();
const prefabFacts = new Map();

for (const entry of readdirSync(GAMEOBJECT).sort()) {
  if (!/^Part_.*\.prefab$/.test(entry)) continue;

  const name = basename(entry, ".prefab");
  const text = readFileSync(join(GAMEOBJECT, entry), "utf8");
  const block = partBlock(text);
  if (block === null) {
    fail(`${name}: no MonoBehaviour block carries m_autoAlign`);
  }

  const autoAlign = readField(block, "m_autoAlign");
  if (autoAlign === null || !(autoAlign in AUTO_ALIGN)) {
    fail(`${name}: unexpected m_autoAlign ${autoAlign}`);
  }

  const fact = {
    name,
    script: blockScript(block, name),
    autoAlign,
    autoAlignName: AUTO_ALIGN[autoAlign],
    liftConstant: readField(block, "liftConstant"),
    dragConstant: readField(block, "dragConstant"),
  };
  prefabFacts.set(name, fact);
  scan.autoAlign[fact.autoAlignName]++;

  if (fact.autoAlign === 2) {
    scan.flipVertically.push(name);
  }
}

// Family order (wooden/metal wings, wooden/metal tails) so the report is stable regardless of the
// filesystem's directory order.
scan.flipVertically.sort((left, right) => {
  const leftFamily = FAMILIES.indexOf(familyOf(left));
  const rightFamily = FAMILIES.indexOf(familyOf(right));
  return leftFamily - rightFamily || left.localeCompare(right);
});

// ---------------------------------------------------------------- the 17 FlipVertically prefabs
const classCounts = {};
const invariants = [];
const expect = (label, actual, wanted) => {
  if (actual !== wanted) {
    invariants.push(`${label}: ${actual}, expected ${wanted}`);
  }
};

for (const name of scan.flipVertically) {
  const fact = prefabFacts.get(name);
  classCounts[fact.script] = (classCounts[fact.script] ?? 0) + 1;

  if (!AERO_CLASSES.includes(fact.script)) {
    invariants.push(`${name}: m_autoAlign 2 on '${fact.script}', expected Wings or Tail`);
    continue;
  }

  if (fact.liftConstant === null) {
    invariants.push(`${name}: ${fact.script} has no liftConstant`);
    continue;
  }

  if (fact.script === "Wings") {
    count(wingLiftConstant, fact.liftConstant);
    if (fact.dragConstant === null) {
      invariants.push(`${name}: Wings has no dragConstant`);
    } else {
      count(dragConstantUnused, fact.dragConstant);
    }
  } else {
    count(tailLiftConstant, fact.liftConstant);
    if (fact.dragConstant !== null) {
      invariants.push(`${name}: Tail carries dragConstant ${fact.dragConstant}, expected none`);
    }
  }
}

for (const script of AERO_CLASSES) {
  scan.classHistogram[script] = classCounts[script] ?? 0;
}
for (const script of Object.keys(classCounts)) {
  if (!AERO_CLASSES.includes(script)) {
    invariants.push(`FlipVertically class histogram carries '${script}'`);
  }
}

// Every Wings/Tail prefab in the project must be one of the 17: the original only opens the
// mirror to the classes that override SetRotation/SetFlipped (docs/specs/part-mirror.md section 7).
const aeroPrefabs = [...prefabFacts.values()].filter((fact) => AERO_CLASSES.includes(fact.script));
for (const fact of aeroPrefabs) {
  if (fact.autoAlign !== 2) {
    invariants.push(`${fact.name} is a ${fact.script} with m_autoAlign ${fact.autoAlignName}, expected flipVertically`);
  }
}

expect("Part_*.prefab files", prefabFacts.size, 343);
expect("m_autoAlign None", scan.autoAlign.none, 153);
expect("m_autoAlign Rotate", scan.autoAlign.rotate, 173);
expect("m_autoAlign FlipVertically", scan.autoAlign.flipVertically, 17);
expect("Wings prefabs", scan.classHistogram.Wings, 9);
expect("Tail prefabs", scan.classHistogram.Tail, 8);
expect("WoodenWings prefabs", scan.flipVertically.filter((name) => familyOf(name) === "WoodenWings").length, 5);
expect("MetalWings prefabs", scan.flipVertically.filter((name) => familyOf(name) === "MetalWings").length, 4);
expect("WoodenTail prefabs", scan.flipVertically.filter((name) => familyOf(name) === "WoodenTail").length, 4);
expect("MetalTail prefabs", scan.flipVertically.filter((name) => familyOf(name) === "MetalTail").length, 4);
expect("Wings/Tail prefabs outside the four families", scan.flipVertically.filter((name) => familyOf(name) === null).length, 0);

// The serialized `liftConstant` per family (docs/specs/part-mirror.md section 2 table 11): wooden
// wings 0.8 / metal wings 1.5, wooden tails 0.2 / metal tails 1.0, and nothing else.
const histogramEquals = (label, histogram, wanted) => {
  const actual = JSON.stringify([...histogram]);
  const expected = JSON.stringify(wanted);
  if (actual !== expected) {
    invariants.push(`${label}: ${actual}, expected ${expected}`);
  }
};

histogramEquals("wing liftConstant", wingLiftConstant, [["0.8", 5], ["1.5", 4]]);
histogramEquals("tail liftConstant", tailLiftConstant, [["0.2", 4], ["1", 4]]);
// `dragConstant` is dead (Wings.cs:104-118 never reads it) but still serialized on the 9 Wings
// prefabs and on none of the 8 Tails -- reported so the omission from content is verifiable.
histogramEquals("dragConstant on Wings", dragConstantUnused, [["0.8", 5], ["0.4", 4]]);

// ---------------------------------------------------------------- per content part

const assignments = loadAssignments();
const content = JSON.parse(readFileSync(CONTENT_PARTS, "utf8"));

/** partTypeId -> COUNT of content parts on that prefab, so coverage can be asserted both ways. */
const partsByPrefab = new Map();
for (const part of content.parts) {
  const prefab = assignments.get(part.partTypeId) ?? null;
  if (prefab === null) continue;
  if (!prefabFacts.has(prefab)) {
    warnings.push(`part ${part.partTypeId} (${prefab}): prefab file missing`);
    continue;
  }

  partsByPrefab.set(prefab, [...(partsByPrefab.get(prefab) ?? []), part.partTypeId]);
}

const parts = {};
for (const part of content.parts) {
  const prefab = assignments.get(part.partTypeId) ?? null;
  if (prefab === null) continue;

  const fact = prefabFacts.get(prefab);
  if (fact === undefined || fact.autoAlign !== 2) continue;

  if (!AERO_CLASSES.includes(fact.script)) {
    fail(`content part ${part.partTypeId} (${prefab}): m_autoAlign 2 on '${fact.script}', expected Wings or Tail`);
  }

  if (fact.liftConstant === null) {
    fail(`content part ${part.partTypeId} (${prefab}): ${fact.script} has no liftConstant`);
  }

  const entry = {
    partTypeId: part.partTypeId,
    prefab,
    script: fact.script,
    autoAlign: 2,
    // `m_autoAlign == 2` is the mirror (docs/specs/part-mirror.md section 3): the original lets
    // only these parts flip, and the report's `mirror` is exactly this field.
    mirror: true,
  };

  if (fact.script === "Wings") {
    if (fact.dragConstant === null) {
      fail(`content part ${part.partTypeId} (${prefab}): Wings has no dragConstant`);
    }

    entry.wing = { liftConstant: ROUND6(fact.liftConstant) };
    // Reported for completeness; apply-aero must NOT write it (the original never reads it).
    entry.dragConstant = ROUND6(fact.dragConstant);
  } else {
    entry.tail = ROUND6(fact.liftConstant);
  }

  parts[String(part.partTypeId)] = entry;
}

// Coverage: every one of the 17 FlipVertically prefabs has exactly one content part, and every
// reported part has a wing XOR tail.
for (const name of scan.flipVertically) {
  const mapped = partsByPrefab.get(name) ?? [];
  if (mapped.length !== 1) {
    invariants.push(`${name}: ${mapped.length} content parts, expected exactly 1`);
  }
}

for (const [partTypeId, entry] of Object.entries(parts)) {
  const hasWing = entry.wing !== undefined;
  const hasTail = entry.tail !== undefined;
  if (hasWing === hasTail) {
    invariants.push(`part ${partTypeId}: mirror requires exactly one of wing/tail`);
  }

  if (entry.script === "Wings" && !hasWing) {
    invariants.push(`part ${partTypeId}: Wings part without a wing value`);
  }

  if (entry.script === "Tail" && !hasTail) {
    invariants.push(`part ${partTypeId}: Tail part without a tail value`);
  }
}

expect("content parts on a FlipVertically prefab", Object.keys(parts).length, 17);

if (invariants.length > 0) {
  fail(`aero invariants failed:\n  - ${invariants.join("\n  - ")}`);
}

// ---------------------------------------------------------------- report
const report = {
  format: "pigforge.bple-aero.report",
  tickRateHz: TICK_RATE_HZ,
  scan: {
    ...scan,
    wingLiftConstant: Object.fromEntries(wingLiftConstant),
    tailLiftConstant: Object.fromEntries(tailLiftConstant),
    dragConstantUnused: Object.fromEntries(dragConstantUnused),
  },
  warnings,
  parts,
};
mkdirSync(dirname(OUT_JSON), { recursive: true });
writeFileSync(OUT_JSON, `${JSON.stringify(report, null, 2)}\n`);

const f = (value, digits = 6) => (typeof value === "number" ? Number(value.toFixed(digits)) : value);

const md = [];
md.push("# 原版机翼 / 尾翼 / 镜像报告", "");
md.push("来源：`tools/bple-aero/extract-aero.mjs`，扫描原版全部 343 个 `Part_*.prefab`。");
md.push("规则出处：`BasePart.cs:79-84`（`AutoAlignType`）、`BasePart.cs:639-651`（`SetFlipped`）、");
md.push("`Wings.cs:6` / `:76-88` / `:104-118`（`liftConstant`、响应曲线、`FixedUpdate`）、");
md.push("`Tail.cs:6` / `:32-41` / `:57-75`（同上，尾翼没有 `dragConstant` 字段）。", "");
md.push("## 全量扫描（原版 prefab 计数）", "");
md.push(`- \`m_autoAlign\`：${Object.entries(scan.autoAlign).map(([key, value]) => `${key} **${value}**`).join("、")}`);
md.push(`- \`FlipVertically\` 家族：${FAMILIES.map((family) => `${family} ${scan.flipVertically.filter((name) => familyOf(name) === family).length}`).join("、")}`);
md.push(`- 类分布：${AERO_CLASSES.map((script) => `${script} **${scan.classHistogram[script]}**`).join("、")}`);
md.push(`- 机翼 \`liftConstant\`：${histogramLine(wingLiftConstant)}（木翼 / 金属翼）`);
md.push(`- 尾翼 \`liftConstant\`：${histogramLine(tailLiftConstant)}（木尾 / 金属尾）`);
md.push(`- \`dragConstant\`（**死字段**，\`Wings.FixedUpdate\` 从不读它，\`Tail\` 根本没有这个字段）：${histogramLine(dragConstantUnused)}`, "");
md.push("## PigForge 内容映射（17 件）", "");
md.push("`mirror: true` = 原版 `m_autoAlign == FlipVertically`，只有这 17 件；`wing.liftConstant` / `tail` 就是原先的 `liftConstant`。", "");
md.push("| 内容 id | prefab | 类 | mirror | wing.liftConstant | tail | dragConstant（不写入） |", "|---|---|---|---|---|---|---|");
for (const [partTypeId, entry] of Object.entries(parts).sort((left, right) => Number(left[0]) - Number(right[0]))) {
  md.push(`| \`${partTypeId}\` | \`${entry.prefab}\` | ${entry.script} | ${entry.mirror} | ${entry.wing ? f(entry.wing.liftConstant) : ""} | ${entry.tail === undefined ? "" : f(entry.tail)} | ${entry.dragConstant === undefined ? "—" : f(entry.dragConstant)} |`);
}

md.push("", `\`FlipVertically\` prefab（${scan.flipVertically.length}）：${scan.flipVertically.map((name) => `\`${name}\``).join("、")}`);

if (warnings.length > 0) {
  md.push("", `## 警告（${warnings.length}）`, ...warnings.map((warning) => `- ${warning}`));
}

writeFileSync(OUT_MD, `${md.join("\n")}\n`);

console.log(`autoAlign: ${JSON.stringify(scan.autoAlign)}`);
console.log(`flipVertically: ${scan.flipVertically.length} (${AERO_CLASSES.map((script) => `${script} ${scan.classHistogram[script]}`).join(", ")})`);
console.log(`wing liftConstant: ${histogramLine(wingLiftConstant)}; tail liftConstant: ${histogramLine(tailLiftConstant)}`);
console.log(`dragConstant (unused, never written): ${histogramLine(dragConstantUnused)}`);
console.log(`content parts: ${Object.keys(parts).length}`);
if (warnings.length > 0) {
  console.log(`warnings: ${warnings.length}`);
}

console.log(`report: ${OUT_JSON}\n        ${OUT_MD}`);
