// Bellows-family extractor: reads `m_direction` / `m_boostForce` / `m_alienBellow` / `m_partType`
// / `m_partTier` out of the one `Bellows` MonoBehaviour block of each prefab -- Part_Bellows_01_SET,
// Part_Bellows_02_SET, Part_Bellows_03_SET, Part_Bellows_04_SET, Part_Bellows_05_SET,
// Part_Bellows_06_SET, Part_Bellows_07_SET and Part_Bellows_08_SET, 8 prefabs in
// `<bple>/Assets/GameObject` -- and reports the PigForge `capabilities.bellows` values per
// partTypeId. This is the ONLY admissible source for those values in content/parts.json, the same
// rule tools/bple-rockets, tools/bple-aero, tools/bple-fans, tools/bple-joints, tools/bple-lift and
// tools/bple-power established for theirs.
//
// Why it matters (gap G98 in tasks/original-vs-implemented.md): the bellows family's content was a
// hand-written TOTAL IMPULSE -- `bellows: 8.0` on part 40 and on variants 88..94, `32.0` on v07 --
// that the rules spent once per press. The original applies a per-frame `ForceMode.Force` ramp
// instead. The original's numbers are these:
//
//   Bellows.cs:5-20    the serialized fields and constants: `m_direction = Vector3.up` (:5),
//                      `[SerializeField] private bool m_alienBellow` (:9-10), `m_boostForce = 10`
//                      (:12) and the four durations BOOST_DURATION 0.5 (:14), WAIT_DURATION 0.3
//                      (:16), INFLATE_DURATION 0.3 (:18), ALIEN_INFLATE_DURATION 0.15 (:20).
//   Bellows.cs:36-46   the `InflateDuration` property: `0.15f` when `m_alienBellow`, else `0.3f`.
//                      That alien half is the ONLY per-skin difference in the cycle, and it is
//                      what `inflateTicks` carries.
//   Bellows.cs:92-121  FixedUpdate, the puff: `num = Time.time - m_timeBoostStarted`; past
//                      `0.8 + InflateDuration` the part is off and applies nothing; while
//                      `num < 0.5` it applies `(1 - (1 - num/0.5)^2) * m_boostForce` through
//                      `AddForceAtPosition(force, transform.position + dir * 0.5, ForceMode.Force)`
//                      along `transform.TransformDirection(m_direction)`; from 0.5 s to
//                      `0.8 + InflateDuration` it applies NO force (wait + inflate animation).
//   Bellows.cs:123-142 OnTouch refuses a fresh puff until `0.8 + InflateDuration` has elapsed, so
//                      the cycle is 0.5 s boost + 0.3 s wait + 0.3 s (0.15 s alien) inflate =
//                      1.1 s / 0.95 s.
//   Bellows.cs:144-160 CompressionScale is the visual squash only (t = time/0.5, held at 1 during
//                      the wait, relaxed over InflateDuration); it never touches the force.
//
// Only the force and that one duration travel in content: `thrustPerTick = m_boostForce / 60`, the
// ADR-013 decision 4 conversion of the per-second `ForceMode.Force` of Bellows.cs:105-110 into one
// impulse per 60 Hz tick (the same divisor tools/bple-rockets, tools/bple-fans and tools/bple-lift
// use), and `inflateTicks = round(InflateDuration * 60)` = 18 normally, 9 on the alien skin. The
// 0.5 s boost and the 0.3 s wait are CLASS CONSTANTS (`Bellows.cs:14-15`, BOOST_DURATION /
// WAIT_DURATION) shared by every skin, so they do NOT travel in content: the rules own them
// (GameplayRules.RunBellows).
//
// Measured on BPLE_Unity6: 26 Part_*.prefab files serialize `m_boostForce` -- 8 `Bellows` and 18
// `Rocket` -- `m_direction` is (1,0,0) on all 8 bellows skins, `m_boostForce` is 30 on 01-06 and
// 08 and 120 on the alien skin 07, whose `m_alienBellow` is the only 1 among the eight. All of
// that is a hard invariant: drift fails this tool instead of silently writing a different world
// into content/parts.json.
//
// Usage: node tools/bple-bellows/extract-bellows.mjs [--bple <path>] [--json <path>] [--md <path>]
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
const OUT_JSON = resolve(arg("json", join(REPO, "tasks", "bple-bellows-report.json")));
const OUT_MD = resolve(arg("md", join(REPO, "tasks", "bple-bellows-report.md")));
const GAMEOBJECT = join(BPLE, "Assets", "GameObject");
const SCRIPTS = join(BPLE, "Assets", "Scripts", "Assembly-CSharp");
const CONTENT_PARTS = join(REPO, "content", "parts.json");
const TEXTURE_MAP = join(REPO, "tools", "bple-textures", "part-map.json");

if (!existsSync(GAMEOBJECT)) {
  console.error(`BPLE project not found: ${GAMEOBJECT}\nPass --bple <path to BPLE_Unity6>.`);
  process.exit(1);
}

const fail = (message) => {
  console.error(message);
  process.exit(1);
};

/** The room's fixed tick rate (`GameRoomOptions.TickRateHz`, PlayHost) and the original's
 * `FixedUpdate` rate: the divisor that turns the per-second `ForceMode.Force` of Bellows.cs:105-110
 * into one impulse per tick (ADR-013 decision 4), and the factor that turns the inflate duration
 * into ticks. */
const TICK_RATE_HZ = 60;

/** The 8 prefabs of the family, in skin order (07 is the alien). */
const PREFABS = [
  "Part_Bellows_01_SET",
  "Part_Bellows_02_SET",
  "Part_Bellows_03_SET",
  "Part_Bellows_04_SET",
  "Part_Bellows_05_SET",
  "Part_Bellows_06_SET",
  "Part_Bellows_07_SET",
  "Part_Bellows_08_SET",
];

/** The one skin whose `m_alienBellow` is 1 -- the skin Bellows.cs:36-46 gives the 0.15 s inflate. */
const ALIEN_PREFAB = "Part_Bellows_07_SET";

/** `PartType.Bellows` (BasePart.cs:31, `Bellows = 6`): the `m_partType` all 8 prefabs carry. */
const PART_TYPE = 6;

/** `PartTier.Legendary` (BasePart.cs:14-21): the highest tier the family reaches (skin 07). */
const MAX_TIER = 4;

/** The four duration constants this report leans on (Bellows.cs:14,16,18,20), by report key. The
 * two shared phase lengths travel in NO content field (the rules own them); the two inflate
 * durations are what `inflateTicks` is derived from. */
const CONSTANT_FIELDS = {
  boostSeconds: "BOOST_DURATION",
  waitSeconds: "WAIT_DURATION",
  inflateSeconds: "INFLATE_DURATION",
  alienInflateSeconds: "ALIEN_INFLATE_DURATION",
};

/** The values those four must have (measured, and the ones the content conversion assumes). */
const EXPECTED_CONSTANTS = { boostSeconds: 0.5, waitSeconds: 0.3, inflateSeconds: 0.3, alienInflateSeconds: 0.15 };

/** The first `field: value` in the text, or null. Every serialized field appears once per block. */
function readField(text, field) {
  const match = new RegExp(`^\\s*${field}:\\s*(-?[0-9.]+)\\s*$`, "m").exec(text);
  if (!match) {
    return null;
  }

  const value = Number(match[1]);
  return Number.isFinite(value) ? value : null;
}

/** The first `field: {x: a, y: b, z: c}` of a serialized Vector3, or null. */
function readVector(text, field) {
  const match = new RegExp(`^\\s*${field}:\\s*\\{x:\\s*(-?[0-9.]+),\\s*y:\\s*(-?[0-9.]+),\\s*z:\\s*(-?[0-9.]+)\\s*\\}\\s*$`, "m").exec(text);
  if (!match) {
    return null;
  }

  const vector = { x: Number(match[1]), y: Number(match[2]), z: Number(match[3]) };
  return Object.values(vector).every(Number.isFinite) ? vector : null;
}

/** The `Bellows` class constants, read out of the original source itself (Bellows.cs:14-20) so a
 * re-balanced original fails this tool instead of a stale cycle being written into content. */
function readConstants() {
  const path = join(SCRIPTS, "Bellows.cs");
  if (!existsSync(path)) {
    fail(`Bellows.cs not found: ${path}`);
  }

  const source = readFileSync(path, "utf8");
  const constants = {};
  for (const [key, name] of Object.entries(CONSTANT_FIELDS)) {
    const match = new RegExp(`public const float ${name} = ([0-9.]+)f;`).exec(source);
    if (match === null) {
      fail(`Bellows.cs: '${name}' is missing`);
    }

    constants[key] = Number(match[1]);
  }

  // Bellows.cs:36-46: the property that picks between the two inflate durations. Its two returns
  // must be the two constants above, or `inflateTicks` would be derived from the wrong pair.
  const property = /private float InflateDuration\s*\{[^}]*if \(m_alienBellow\)\s*\{\s*return ([0-9.]+)f;\s*\}\s*return ([0-9.]+)f;/.exec(source);
  if (property === null) {
    fail("Bellows.cs: the InflateDuration property's alien/vanilla returns were not found");
  }

  if (Number(property[1]) !== constants.alienInflateSeconds || Number(property[2]) !== constants.inflateSeconds) {
    fail(`Bellows.cs: InflateDuration returns ${property[1]}/${property[2]}s, expected ${constants.alienInflateSeconds}/${constants.inflateSeconds}s`);
  }

  return constants;
}

const constants = readConstants();

/** class name of the script a block references, via the `.cs.meta` guid index (the same index
 * tools/bple-rockets, tools/bple-aero, tools/bple-fans and tools/bple-power build). */
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

/** The `--- !u!114 &...` MonoBehaviour block that carries the `Bellows` component. `m_boostForce`
 * is serialized by `Rocket` too (26 prefabs measure it: these 8 plus the 18 rockets), so the block
 * is only accepted once its `m_Script` guid resolves to `Bellows` -- a hard check, never a name
 * guess. Unity writes ONE block per MonoBehaviour including the inherited BasePart fields, so that
 * block also carries `m_partType` / `m_partTier` / `m_direction`. */
function readBellowsBlock(text, prefab) {
  const candidates = text.split(/\n--- !u!/).filter((block) => /^\s*m_boostForce:/m.test(block));
  let block = null;
  for (const candidate of candidates) {
    const guid = /m_Script:\s*\{fileID:\s*-?\d+,\s*guid:\s*([0-9a-f]{32})/m.exec(candidate)?.[1];
    const script = guid ? guidIndex.get(guid) : undefined;
    if (script === undefined) {
      fail(`${prefab}: the m_boostForce block references an unresolved script guid '${guid ?? "none"}'`);
    }

    if (script !== "Bellows") {
      continue;
    }

    if (block !== null) {
      fail(`${prefab}: more than one Bellows MonoBehaviour block carries m_boostForce`);
    }

    block = candidate;
  }

  return block;
}

/** The Bellows fields of one prefab, or null when it has no Bellows component. */
function readBellows(text, prefab) {
  const block = readBellowsBlock(text, prefab);
  if (block === null) {
    return null;
  }

  const scalars = ["m_partType", "m_partTier", "m_boostForce", "m_alienBellow"];
  const values = {};
  for (const field of scalars) {
    values[field] = readField(block, field);
  }

  const direction = readVector(block, "m_direction");
  const missing = scalars.filter((field) => values[field] === null);
  if (missing.length > 0 || direction === null) {
    fail(`${prefab}: Bellows block is missing ${[...missing, ...(direction === null ? ["m_direction"] : [])].join("/")}`);
  }

  if (values["m_partType"] !== PART_TYPE) {
    fail(`${prefab}: unexpected m_partType ${values["m_partType"]}, expected ${PART_TYPE} (PartType.Bellows)`);
  }

  const tier = values["m_partTier"];
  if (!Number.isInteger(tier) || tier < 0 || tier > MAX_TIER) {
    fail(`${prefab}: unexpected m_partTier ${tier}`);
  }

  const alienBellow = values["m_alienBellow"];
  if (alienBellow !== 0 && alienBellow !== 1) {
    fail(`${prefab}: unexpected m_alienBellow ${alienBellow}, expected 0 or 1`);
  }

  if (!(values["m_boostForce"] > 0)) {
    fail(`${prefab}: m_boostForce ${values["m_boostForce"]} is not a positive number`);
  }

  return {
    prefab,
    partType: values["m_partType"],
    tier,
    direction,
    boostForce: values["m_boostForce"],
    alien: alienBellow === 1,
    inflateTicks: ticksOf(alienBellow === 1 ? constants.alienInflateSeconds : constants.inflateSeconds, prefab, "InflateDuration"),
  };
}

/** Bump one histogram bucket. The histograms are Maps, not objects: the report orders the numeric
 * ones ascending, and a Map keeps that choice explicit. */
function count(histogram, value) {
  const key = String(value);
  histogram.set(key, (histogram.get(key) ?? 0) + 1);
}

/** A histogram's entries, sorted ascending numerically where the keys are numbers (9 before 18,
 * which a plain string sort gets wrong) and lexically otherwise (`"1,0,0"`, a direction triple). */
const sortedEntries = (histogram) =>
  [...histogram].sort(([left], [right]) =>
    Number.isFinite(Number(left)) && Number.isFinite(Number(right)) ? Number(left) - Number(right) : left.localeCompare(right));

/** A histogram as its report/console line. */
const histogramLine = (histogram) => `{ ${sortedEntries(histogram).map(([key, value]) => `"${key}": ${value}`).join(", ")} }`;

/** A histogram as a JSON object, same order. */
const numericHistogram = (histogram) => Object.fromEntries(sortedEntries(histogram));

const ROUND6 = (value) => Number(value.toFixed(6));

/** A duration in seconds -> ticks, refusing anything that is not an exact multiple of 1/60:
 * 0.5 s -> 30, 0.3 s -> 18, 0.15 s -> 9. */
function ticksOf(seconds, prefab, field) {
  const ticks = seconds * TICK_RATE_HZ;
  if (Math.abs(ticks - Math.round(ticks)) > 1e-6) {
    fail(`${prefab}: ${field} ${seconds}s is not an exact multiple of 1/${TICK_RATE_HZ}s`);
  }

  return Math.round(ticks);
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

// ---------------------------------------------------------------- whole-project scan
// The histogram is the fingerprint: 26 Part_*.prefab files carry `m_boostForce`, 8 of them a
// `Bellows` block (the other 18 are `Rocket`).
const scan = {
  count: 0,
  byBoostForce: new Map(),
  alien: [],
  direction: new Map(),
  inflateTicks: new Map(),
};
const facts = new Map();
let boostForcePrefabs = 0;

for (const entry of readdirSync(GAMEOBJECT).sort()) {
  if (!/^Part_.*\.prefab$/.test(entry)) continue;

  const name = basename(entry, ".prefab");
  const text = readFileSync(join(GAMEOBJECT, entry), "utf8");
  if (/^\s*m_boostForce:/m.test(text)) {
    boostForcePrefabs++;
  }

  const bellows = readBellows(text, name);
  if (bellows === null) {
    continue;
  }

  facts.set(name, bellows);
  scan.count++;
  count(scan.byBoostForce, bellows.boostForce);
  count(scan.direction, `${bellows.direction.x},${bellows.direction.y},${bellows.direction.z}`);
  count(scan.inflateTicks, bellows.inflateTicks);
  if (bellows.alien) {
    scan.alien.push(name);
  }
}

scan.alien.sort();

// ---------------------------------------------------------------- hard invariants
const invariants = [];
const expect = (label, actual, wanted) => {
  if (actual !== wanted) {
    invariants.push(`${label}: ${actual}, expected ${wanted}`);
  }
};
const expectClose = (label, actual, wanted) => {
  if (typeof actual !== "number" || Math.abs(actual - wanted) > 1e-9) {
    invariants.push(`${label}: ${actual}, expected ${wanted}`);
  }
};
const histogramEquals = (label, histogram, wanted) => {
  const actual = JSON.stringify(numericHistogram(histogram));
  const expected = JSON.stringify(Object.fromEntries(wanted));
  if (actual !== expected) {
    invariants.push(`${label}: ${actual}, expected ${expected}`);
  }
};

// The four class constants the report cites must be the ones this conversion assumes
// (Bellows.cs:14,16,18,20).
for (const key of Object.keys(CONSTANT_FIELDS)) {
  expectClose(`Bellows.cs ${CONSTANT_FIELDS[key]}`, constants[key], EXPECTED_CONSTANTS[key]);
}

expect("Part_*.prefab files carrying m_boostForce", boostForcePrefabs, 26);
expect("Bellows prefabs", scan.count, 8);
expect("Bellows prefab names", [...facts.keys()].sort().join(", "), PREFABS.join(", "));

// Measured per-prefab values (the numbers the original ships) -- the raw prefab side.
histogramEquals("m_boostForce", scan.byBoostForce, [["30", 7], ["120", 1]]);
expect("alien skins", scan.alien.join(", "), ALIEN_PREFAB);
expect("m_direction histogram", histogramLine(scan.direction), '{ "1,0,0": 8 }');
histogramEquals("inflateTicks", scan.inflateTicks, [["9", 1], ["18", 7]]);

/** The expected raw numbers of one skin, straight from the measurements above. */
function expectedOf(name) {
  const alien = name === ALIEN_PREFAB;
  return {
    boostForce: alien ? 120 : 30,
    alien,
    inflateTicks: alien ? ticksOf(constants.alienInflateSeconds, name, "InflateDuration") : ticksOf(constants.inflateSeconds, name, "InflateDuration"),
  };
}

const skins = new Map();
for (const fact of facts.values()) {
  const expected = expectedOf(fact.prefab);
  expect(`${fact.prefab}: m_partType`, fact.partType, PART_TYPE);
  expect(`${fact.prefab}: m_boostForce`, fact.boostForce, expected.boostForce);
  expect(`${fact.prefab}: m_alienBellow`, fact.alien, expected.alien);
  expect(`${fact.prefab}: inflateTicks`, fact.inflateTicks, expected.inflateTicks);

  if (fact.direction.x !== 1 || fact.direction.y !== 0) {
    invariants.push(`${fact.prefab}: m_direction (${fact.direction.x},${fact.direction.y},${fact.direction.z}), expected (1,0,0)`);
  }

  if (fact.direction.z !== 0) {
    invariants.push(`${fact.prefab}: m_direction z is ${fact.direction.z}, expected 0 (PigForge's plane is 2.5D)`);
  }

  /** The exact object apply-bellows.mjs writes, keys in the content order. */
  const bellows = {
    directionX: fact.direction.x,
    directionY: fact.direction.y,
    thrustPerTick: ROUND6(fact.boostForce / TICK_RATE_HZ),
    inflateTicks: fact.inflateTicks,
  };

  skins.set(fact.prefab, {
    fact,
    bellows,
    source: { boostForce: fact.boostForce },
  });
}

// ---------------------------------------------------------------- per content part

const assignments = loadAssignments();
const content = JSON.parse(readFileSync(CONTENT_PARTS, "utf8"));

/** partTypeId -> COUNT of content parts on that prefab, so coverage can be asserted both ways. */
const partsByPrefab = new Map();
for (const part of content.parts) {
  const prefab = assignments.get(part.partTypeId) ?? null;
  if (prefab === null || !facts.has(prefab)) continue;
  partsByPrefab.set(prefab, [...(partsByPrefab.get(prefab) ?? []), part.partTypeId]);
}

const parts = {};
for (const part of content.parts) {
  const prefab = assignments.get(part.partTypeId) ?? null;
  if (prefab === null) continue;

  const entry = skins.get(prefab);
  if (entry === undefined) continue;

  if (part.capabilities === undefined || part.capabilities.bellows === undefined) {
    fail(`content part ${part.partTypeId} (${prefab}): capabilities.bellows is missing, so there is no slot to rewrite`);
  }

  parts[String(part.partTypeId)] = {
    partTypeId: part.partTypeId,
    prefab,
    alien: entry.fact.alien,
    bellows: entry.bellows,
    source: entry.source,
  };
}

// Coverage: every one of the 8 Bellows prefabs has exactly one content part.
for (const [prefab, mapped] of [...partsByPrefab].sort((left, right) => left[0].localeCompare(right[0]))) {
  if (mapped.length !== 1) {
    invariants.push(`${prefab}: ${mapped.length} content parts (${mapped.join(", ")}), expected exactly 1`);
  }
}

expect("content parts on a Bellows prefab", Object.keys(parts).length, 8);
expect("content partTypeIds", Object.keys(parts).map(Number).sort((left, right) => left - right).join(", "), "40, 88, 89, 90, 91, 92, 93, 94");
expect("part 40 prefab", parts["40"]?.prefab, "Part_Bellows_01_SET");
expect("part 93 prefab", parts["93"]?.prefab, ALIEN_PREFAB);
expect("alien content part", parts["93"]?.alien, true);
// Only the alien skin's part carries the 9-tick inflate.
const alienInflateParts = Object.entries(parts)
  .filter(([, entry]) => entry.bellows.inflateTicks === ticksOf(constants.alienInflateSeconds, ALIEN_PREFAB, "InflateDuration"))
  .map(([partTypeId]) => partTypeId);
expect("content parts with inflateTicks 9", alienInflateParts.join(", "), "93");

if (invariants.length > 0) {
  fail(`bellows invariants failed:\n  - ${invariants.join("\n  - ")}`);
}

// ---------------------------------------------------------------- report
const report = {
  format: "pigforge.bple-bellows.report",
  tickRateHz: TICK_RATE_HZ,
  classConstants: {
    boostSeconds: constants.boostSeconds,
    waitSeconds: constants.waitSeconds,
    inflateSeconds: constants.inflateSeconds,
    alienInflateSeconds: constants.alienInflateSeconds,
  },
  scan: {
    count: scan.count,
    byBoostForce: numericHistogram(scan.byBoostForce),
    alien: scan.alien,
    direction: Object.fromEntries(scan.direction),
    inflateTicks: numericHistogram(scan.inflateTicks),
  },
  parts,
};
mkdirSync(dirname(OUT_JSON), { recursive: true });
writeFileSync(OUT_JSON, `${JSON.stringify(report, null, 2)}\n`);

const f = (value, digits = 6) => (typeof value === "number" ? Number(value.toFixed(digits)) : value);
const bellowsObject = (bellows) => `{ "directionX": ${bellows.directionX}, "directionY": ${bellows.directionY}, "thrustPerTick": ${f(bellows.thrustPerTick)}, "inflateTicks": ${bellows.inflateTicks} }`;

const md = [];
md.push("# 原版风箱族报告", "");
md.push("来源：`tools/bple-bellows/extract-bellows.mjs`，扫描 8 个 `Bellows` prefab（`Part_Bellows_01..08_SET`，07 是 alien 皮肤）。");
md.push("规则出处：`Bellows.cs:5-20`（序列化字段与四个时长常量）、`:36-46`（`InflateDuration` 的 alien/vanilla 两档）、");
md.push("`:92-121`（`FixedUpdate` 的 0.5 s 力斜坡 + 0.3 s 静默 + inflate）、`:123-142`（`OnTouch` 的再触发门）、");
md.push("`:144-160`（`CompressionScale` 只做视觉压缩）。", "");
md.push("## prefab 直方图", "");
md.push(`- 带 \`m_boostForce\` 的 prefab：**${boostForcePrefabs}**（\`Bellows\` **${scan.count}**、\`Rocket\` ${boostForcePrefabs - scan.count}）`);
md.push(`- \`m_boostForce\`：${histogramLine(scan.byBoostForce)}`);
md.push(`- \`m_alienBellow 1\`：${scan.alien.map((name) => `\`${name}\``).join("、")}`);
md.push(`- \`m_direction\`：${histogramLine(scan.direction)}（z 必须为 0）`);
md.push(`- \`inflateTicks\`：${histogramLine(scan.inflateTicks)}`, "");
md.push("## PigForge 内容映射（8 件）", "");
md.push("`thrustPerTick = m_boostForce / 60`（ADR-013 决策 4），`inflateTicks = round(InflateDuration × 60)`；");
md.push("0.5 s 的 boost 与 0.3 s 的 wait 是类常量（`Bellows.cs:14-15`），不进内容。", "");
md.push("| 内容 id | prefab | alien | capabilities.bellows |", "|---|---|---|---|");
for (const [partTypeId, entry] of Object.entries(parts).sort((left, right) => Number(left[0]) - Number(right[0]))) {
  md.push(`| \`${partTypeId}\` | \`${entry.prefab}\` | ${entry.alien} | ${bellowsObject(entry.bellows)} |`);
}

writeFileSync(OUT_MD, `${md.join("\n")}\n`);

console.log(`count: ${scan.count}`);
console.log(`byBoostForce: ${histogramLine(scan.byBoostForce)}`);
console.log(`alien: ${scan.alien.join(", ")}`);
console.log(`direction: ${histogramLine(scan.direction)}`);
console.log(`inflateTicks: ${histogramLine(scan.inflateTicks)}`);
console.log(`boostForce prefabs: ${boostForcePrefabs} (Bellows ${scan.count})`);
console.log(`content parts: ${Object.keys(parts).length}`);

console.log(`report: ${OUT_JSON}\n        ${OUT_MD}`);
