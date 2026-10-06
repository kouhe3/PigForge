// Rocket-family extractor: reads `m_direction` / `m_boostForce` / `m_ignitionTime` /
// `m_boostDuration` / `m_boostEndDuration` / `m_maximumSpeed` / `m_explodes` / `m_explosionRadius` /
// `m_explosionImpulse` / `m_partType` / `m_partTier` out of the one `Rocket` MonoBehaviour block of
// each prefab -- Part_Rocket_01..04_SET, Part_RedRocket_01..04_SET, Part_CokeBottle_01..05_SET and
// Part_SodaBottle_01..05_SET, 18 prefabs in `<bple>/Assets/GameObject` -- and reports the PigForge
// `capabilities.rocket` values per partTypeId. This is the ONLY admissible source for those values
// in content/parts.json, the same rule tools/bple-aero, tools/bple-fans, tools/bple-joints,
// tools/bple-lift and tools/bple-power established for theirs.
//
// Why it matters (docs/decisions/ADR-029-part-local-content-directions.md, gap G101): the rocket
// family's content values were hand-written leftovers from an early commit -- `thrustPerTick` 4/6/8
// and `durationTicks` 60/30/40, with no ignition phase, no speed cap and a direction that did not
// even agree with the prefab (`Part_SodaBottle_01_SET` carried (0,1) while its `m_direction` is
// (1,0,0)). The original's numbers are these:
//
//   Rocket.cs:9-30    the serialized fields and their C# defaults: `m_direction = Vector3.up`,
//                     `m_boostForce = 10`, `m_ignitionTime = 1`, `m_boostDuration = 2`,
//                     `m_boostEndDuration = 0.5`, `m_maximumSpeed = 0`, `m_explodes`,
//                     `m_explosionImpulse`, `m_explosionRadius`.
//   Rocket.cs:68-83   Awake: `m_visualization` is bound from a child GameObject named
//                     "BottleVisualization" (`transform.Find("BottleVisualization")`), so the
//                     serialized witness is that child, not a field: the 10 bottle prefabs
//                     (Part_CokeBottle_01..05_SET, Part_SodaBottle_01..05_SET) carry it, none of the
//                     8 rocket prefabs (Part_Rocket_01..04_SET, Part_RedRocket_01..04_SET) does.
//   Rocket.cs:180-200 Initialize: the IN override. A *legendary* (tier 4) CokeBottle takes
//                     `AlienCokeForceValue` / `AlienCokeSpeedValue` and a legendary SodaBottle
//                     `AlienSodaForceValue` / `AlienSodaSpeedValue` -- REPLACING the prefab's
//                     `m_boostForce` / `m_maximumSpeed`; every other bottle MULTIPLIES them by
//                     `CokeSodaForce` / `CokeSodaSpeed` and every rocket/red rocket by `RocketForce`
//                     / `RocketSpeed`.
//   Rocket.cs:228-300 FixedUpdate, the three phases: `num = Time.time - m_timeBoostStarted`;
//                     `num > m_ignitionTime + m_boostDuration + m_boostEndDuration` ends the boost
//                     (and explodes, if `m_explodes`), `num > m_ignitionTime + m_boostDuration`
//                     ramps the force down `1 - (num - m_boostDuration - m_ignitionTime) /
//                     m_boostEndDuration`, and the force is `num2 * m_boostForce` applied with
//                     `AddForceAtPosition(..., ForceMode.Force)` -- a *per-second* force, hence the
//                     /60 of ADR-013 decision 4 (PigForge ticks at 60 Hz), the same conversion
//                     tools/bple-fans and tools/bple-lift use.
//   Rocket.cs:235-240 a part with `m_visualization != null` returns before the force is applied
//                     while `num < m_ignitionTime`: a bottle only thrusts after its full ignition
//                     phase, a plain rocket thrusts from its first tick.
//   Rocket.cs:529-541 LimitForceForSpeed: past `m_maximumSpeed` along the thrust axis the force is
//                     divided by `1 + v.along - m_maximumSpeed` -- the cap the content's
//                     `maxSpeed` carries (the original's `maximumSpeed` before the IN multiplier,
//                     which every vanilla `<X>Speed` leaves at 1.0).
//   Rocket.cs:541-581 OnTouch: with the vanilla `SwitchableCokeSodaRocket false` the rocket is a
//                     ONE-SHOT -- `if (m_boostUsed) return;` then
//                     `contraption.ChangeOneShotPartAmount(m_partType, EffectDirection(), -1)` --
//                     so it fires once for exactly ignition + boost + end ticks.
//   Rocket.cs:627-646 Explode: `Physics.OverlapSphere(pos, m_explosionRadius *
//                     RocketExplosionRadius)` and `AddExplosionForce(..., RocketExplosionForce /
//                     num)`, gated by `RocketExplosionCoolingTime`; those three are the identity in
//                     the vanilla settings (1.0 / 1.0 / 0.0), which this tool asserts, so the raw
//                     `m_explosionRadius` / `m_explosionImpulse` ARE the effective values.
//
// The three phases and the cap become ticks and m/s: `ignitionTicks = round(m_ignitionTime * 60)`,
// `boostTicks = round(m_boostDuration * 60)`, `endTicks = round(m_boostEndDuration * 60)` (each
// asserted to be an exact multiple of 1/60 so the rounding can never drift: 60, 180, 60, 30 and the
// CokeBottle_05 outlier's 30000) and `maxSpeed = m_maximumSpeed * IN <X>Speed`.
//
// Measured on BPLE_Unity6: 26 Part_*.prefab files serialize `m_boostForce` -- 8 `Bellows` and these
// 18 `Rocket` -- `m_direction` is (1,0,0) on all 18, rockets/red rockets are 50 / 1 / 3 / 1 / 18,
// bottles 01..04 are 35 / 1 / 1 / 0.5 / 10, CokeBottle_05 is 35 / 1 / 500 / 0.5 / 25 (legendary,
// so the Alien coke pair), SodaBottle_05 is 150 / 1 / 1 / 0.5 / 150 (legendary, the Alien soda pair)
// and only Part_Rocket_03_SET + Part_RedRocket_03_SET have `m_explodes 1` with radius 8 / impulse
// 25. All of that is a hard invariant: drift fails this tool instead of silently writing a
// different world into content/parts.json.
//
// Usage: node tools/bple-rockets/extract-rockets.mjs [--bple <path>] [--json <path>] [--md <path>]
import { existsSync, mkdirSync, readFileSync, readdirSync, writeFileSync } from "node:fs";
import { basename, dirname, join, resolve } from "node:path";
import { fileURLToPath } from "node:url";
import { loadVanillaSettings, VANILLA_SETTINGS_NAME } from "../in-settings/vanilla-settings.mjs";

const HERE = dirname(fileURLToPath(import.meta.url));
const REPO = resolve(HERE, "..", "..");

function arg(name, fallback) {
  const index = process.argv.indexOf(`--${name}`);
  return index >= 0 && process.argv[index + 1] ? process.argv[index + 1] : fallback;
}

const BPLE = resolve(arg("bple", process.env.BPLE_ROOT ?? join(REPO, "..", "BPLE_Unity6")));
const OUT_JSON = resolve(arg("json", join(REPO, "tasks", "bple-rockets-report.json")));
const OUT_MD = resolve(arg("md", join(REPO, "tasks", "bple-rockets-report.md")));
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
 * `FixedUpdate` rate: the divisor that turns the per-second `ForceMode.Force` of Rocket.cs:295-300
 * into one impulse per tick (ADR-013 decision 4), and the factor that turns the three phase
 * durations into ticks. */
const TICK_RATE_HZ = 60;

/** The four prefab families, in the order the report lists them (rockets, then the two bottles). */
const FAMILIES = ["Rocket", "RedRocket", "CokeBottle", "SodaBottle"];

/** `PartType` (BasePart.cs:42,56,58,59) -> family name and the vanilla IN pair
 * `Rocket.Initialize` picks (Rocket.cs:180-200). Only the two bottles have an Alien pair, and only
 * at tier 4. */
const PART_TYPES = {
  17: { name: "Rocket", force: "RocketForce", speed: "RocketSpeed" },
  33: { name: "RedRocket", force: "RocketForce", speed: "RocketSpeed" },
  31: { name: "CokeBottle", force: "CokeSodaForce", speed: "CokeSodaSpeed", alienForce: "AlienCokeForceValue", alienSpeed: "AlienCokeSpeedValue" },
  34: { name: "SodaBottle", force: "CokeSodaForce", speed: "CokeSodaSpeed", alienForce: "AlienSodaForceValue", alienSpeed: "AlienSodaSpeedValue" },
};

/** `PartTier.Legendary` (BasePart.cs:14-21): the tier that swaps a bottle onto its Alien pair. */
const LEGENDARY_TIER = 4;

/** The child GameObject `Rocket.Awake` binds `m_visualization` from (Rocket.cs:78-83). It is a
 * private field with no `[SerializeField]`, so Unity never writes `m_visualization` into the YAML
 * (measured: 0 of the 18 blocks) -- the child IS the serialized witness. */
const VISUALIZATION_CHILD = "BottleVisualization";

/** The only two prefabs whose `m_explodes` is 1 (Rocket.cs:9-30 + :627-646); the other 16 carry a
 * zero, so they must not gain an `explodeRadius` / `explodeImpulse` in content. */
const EXPLODING = new Set(["Part_Rocket_03_SET", "Part_RedRocket_03_SET"]);

/** The three IN entries that gate the rocket's shape (Rocket.cs:229-292): all three have to be the
 * vanilla declaration default `false`, because with any of them on the part stops being the
 * one-shot triple-phase rocket this report describes. */
const FEATURE_SETTINGS = ["SwitchableCokeSodaRocket", "AvoidanceRocket", "TrackingRocket"];

/** Every IN entry these 18 prefabs' numbers pass through (Rocket.cs:180-200, 293-300, 529-541,
 * 627-646), reported so the conversion is verifiable. */
const SETTING_NAMES = [
  "RocketForce",
  "RocketSpeed",
  "CokeSodaForce",
  "CokeSodaSpeed",
  "AlienCokeForceValue",
  "AlienCokeSpeedValue",
  "AlienSodaForceValue",
  "AlienSodaSpeedValue",
  "RocketExplosionForce",
  "RocketExplosionRadius",
  "RocketExplosionCoolingTime",
  ...FEATURE_SETTINGS,
];

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

/** The first `field: {x: a, y: b, z: c}` of a serialized Vector3, or null. */
function readVector(text, field) {
  const match = new RegExp(`^\\s*${field}:\\s*\\{x:\\s*(-?[0-9.]+),\\s*y:\\s*(-?[0-9.]+),\\s*z:\\s*(-?[0-9.]+)\\s*\\}\\s*$`, "m").exec(text);
  if (!match) {
    return null;
  }

  const vector = { x: Number(match[1]), y: Number(match[2]), z: Number(match[3]) };
  return Object.values(vector).every(Number.isFinite) ? vector : null;
}

/** class name of the script a block references, via the `.cs.meta` guid index (the same index
 * tools/bple-aero, tools/bple-fans and tools/bple-power build). */
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

/** The `--- !u!114 &...` MonoBehaviour block that carries the `Rocket` component. `m_boostForce`
 * is serialized by `Bellows` too (8 prefabs measure it), so the block is only accepted once its
 * `m_Script` guid resolves to `Rocket` -- a hard check, never a name guess. Unity writes ONE block
 * per MonoBehaviour including the inherited BasePart fields, so that block also carries
 * `m_partType` / `m_partTier` / `m_direction`. */
function readRocketBlock(text, prefab) {
  const candidates = text.split(/\n--- !u!/).filter((block) => /^\s*m_boostForce:/m.test(block));
  let block = null;
  for (const candidate of candidates) {
    const guid = /m_Script:\s*\{fileID:\s*-?\d+,\s*guid:\s*([0-9a-f]{32})/m.exec(candidate)?.[1];
    const script = guid ? guidIndex.get(guid) : undefined;
    if (script === undefined) {
      fail(`${prefab}: the m_boostForce block references an unresolved script guid '${guid ?? "none"}'`);
    }

    if (script !== "Rocket") {
      continue;
    }

    if (block !== null) {
      fail(`${prefab}: more than one Rocket MonoBehaviour block carries m_boostForce`);
    }

    block = candidate;
  }

  return block;
}

/** The Rocket fields of one prefab, or null when it has no Rocket component. */
function readRocket(text, prefab) {
  const block = readRocketBlock(text, prefab);
  if (block === null) {
    return null;
  }

  const scalars = ["m_partType", "m_partTier", "m_boostForce", "m_ignitionTime", "m_boostDuration", "m_boostEndDuration", "m_maximumSpeed", "m_explodes", "m_explosionRadius", "m_explosionImpulse"];
  const values = {};
  for (const field of scalars) {
    values[field] = readField(block, field);
  }

  const direction = readVector(block, "m_direction");
  const missing = scalars.filter((field) => values[field] === null);
  if (missing.length > 0 || direction === null) {
    fail(`${prefab}: Rocket block is missing ${[...missing, ...(direction === null ? ["m_direction"] : [])].join("/")}`);
  }

  const partType = values["m_partType"];
  if (!(partType in PART_TYPES)) {
    fail(`${prefab}: unexpected m_partType ${partType}, expected one of ${Object.keys(PART_TYPES).join("/")}`);
  }

  const tier = values["m_partTier"];
  if (!Number.isInteger(tier) || tier < 0 || tier > LEGENDARY_TIER) {
    fail(`${prefab}: unexpected m_partTier ${tier}`);
  }

  // Rocket.cs:78-83. `m_visualization` itself is never serialized (it is a private field without
  // [SerializeField]); if a future prefab ever writes it, it must agree with the child name.
  const serialized = /^\s*m_visualization:\s*\{fileID:\s*(-?\d+)/m.exec(block);
  const child = new RegExp(`^\\s*m_Name:\\s*${VISUALIZATION_CHILD}\\s*$`, "m").test(text);
  if (serialized !== null && (Number(serialized[1]) !== 0) !== child) {
    fail(`${prefab}: m_visualization {fileID: ${serialized[1]}} disagrees with the ${VISUALIZATION_CHILD} child (${child})`);
  }

  return {
    prefab,
    partType,
    partTypeName: PART_TYPES[partType].name,
    tier,
    direction,
    boostForce: values["m_boostForce"],
    ignitionTime: values["m_ignitionTime"],
    boostDuration: values["m_boostDuration"],
    boostEndDuration: values["m_boostEndDuration"],
    maximumSpeed: values["m_maximumSpeed"],
    explodes: values["m_explodes"],
    explosionRadius: values["m_explosionRadius"],
    explosionImpulse: values["m_explosionImpulse"],
    visualization: child,
  };
}

/** The vanilla declaration defaults (`INDeclarationSettingsExp.json` through
 * tools/in-settings/vanilla-settings.mjs -- never `INSettingsBExp.json`, which is the IN mod's
 * profile B and the source of gap G104/G105). */
const inSettingsSource = loadVanillaSettings(BPLE);
const inSettings = new Map();
for (const name of SETTING_NAMES) {
  if (!inSettingsSource.has(name)) {
    fail(`${VANILLA_SETTINGS_NAME} is missing '${name}'`);
  }

  inSettings.set(name, FEATURE_SETTINGS.includes(name) ? inSettingsSource.getBool(name) : inSettingsSource.getFloat(name));
}

const setting = (name) => inSettings.get(name);

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
const familyOf = (prefab) => FAMILIES.find((family) => prefab.startsWith(`Part_${family}_`)) ?? null;

/** Bump one histogram bucket. The histograms are Maps, not objects: the report orders the numeric
 * ones ascending and `byFamily` in family order, and a Map keeps that choice explicit. */
function count(histogram, value) {
  const key = String(value);
  histogram.set(key, (histogram.get(key) ?? 0) + 1);
}

/** A histogram's entries, sorted ascending numerically where the keys are numbers (3 before 10,
 * which a plain string sort gets wrong) and lexically otherwise (`"1,0,0"`, a direction triple). */
const sortedEntries = (histogram) =>
  [...histogram].sort(([left], [right]) =>
    Number.isFinite(Number(left)) && Number.isFinite(Number(right)) ? Number(left) - Number(right) : left.localeCompare(right));

/** A histogram as its report/console line. */
const histogramLine = (histogram) => `{ ${sortedEntries(histogram).map(([key, value]) => `"${key}": ${value}`).join(", ")} }`;

/** A histogram as a JSON object, same order. */
const numericHistogram = (histogram) => Object.fromEntries(sortedEntries(histogram));

const ROUND6 = (value) => Number(value.toFixed(6));

/** A phase duration in seconds -> ticks, refusing anything that is not an exact multiple of 1/60:
 * 1 s -> 60, 0.5 s -> 30, 500 s -> 30000. */
function ticksOf(seconds, prefab, field) {
  const ticks = seconds * TICK_RATE_HZ;
  if (Math.abs(ticks - Math.round(ticks)) > 1e-6) {
    fail(`${prefab}: ${field} ${seconds}s is not an exact multiple of 1/${TICK_RATE_HZ}s`);
  }

  return Math.round(ticks);
}

/** `Rocket.Initialize` (Rocket.cs:180-200) reproduced exactly: a legendary bottle's pair is
 * REPLACED by its Alien pair, every other bottle MULTIPLIES by the CokeSoda pair and every
 * rocket/red rocket by the Rocket pair. */
function effectiveOf(rocket) {
  const info = PART_TYPES[rocket.partType];
  if (rocket.tier === LEGENDARY_TIER) {
    if (info.alienForce === undefined) {
      fail(`${rocket.prefab}: tier ${LEGENDARY_TIER} on a ${info.name}, which has no Alien pair`);
    }

    return {
      boostForce: setting(info.alienForce),
      maximumSpeed: setting(info.alienSpeed),
      forceSetting: info.alienForce,
      speedSetting: info.alienSpeed,
      override: true,
    };
  }

  return {
    boostForce: rocket.boostForce * setting(info.force),
    maximumSpeed: rocket.maximumSpeed * setting(info.speed),
    forceSetting: info.force,
    speedSetting: info.speed,
    override: false,
  };
}

// ---------------------------------------------------------------- whole-project scan
// The histogram is the fingerprint: 26 Part_*.prefab files carry `m_boostForce`, 18 of them a
// `Rocket` block (the other 8 are `Bellows`).
const scan = {
  count: 0,
  byFamily: new Map(),
  byBoostForce: new Map(),
  byMaxSpeed: new Map(),
  byBoostDuration: new Map(),
  explodes: [],
  direction: new Map(),
  visualization: [],
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

  const rocket = readRocket(text, name);
  if (rocket === null) {
    continue;
  }

  facts.set(name, rocket);
  scan.count++;
  count(scan.byFamily, rocket.partTypeName);
  count(scan.byBoostForce, rocket.boostForce);
  count(scan.byMaxSpeed, rocket.maximumSpeed);
  count(scan.byBoostDuration, rocket.boostDuration);
  count(scan.direction, `${rocket.direction.x},${rocket.direction.y},${rocket.direction.z}`);
  if (rocket.explodes !== 0) {
    scan.explodes.push(name);
  }

  if (rocket.visualization) {
    scan.visualization.push(name);
  }
}

// Family order (rockets first, then the bottles) so the report is stable regardless of the
// filesystem's directory order.
const byFamilyOrder = (left, right) => FAMILIES.indexOf(familyOf(left)) - FAMILIES.indexOf(familyOf(right)) || left.localeCompare(right);
scan.explodes.sort(byFamilyOrder);
scan.visualization.sort(byFamilyOrder);

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

// The vanilla explosion multipliers must be the identity, or the raw m_explosionRadius /
// m_explosionImpulse are not the effective values this report writes (Rocket.cs:627-646).
expectClose("vanilla RocketExplosionForce", setting("RocketExplosionForce"), 1);
expectClose("vanilla RocketExplosionRadius", setting("RocketExplosionRadius"), 1);
expectClose("vanilla RocketExplosionCoolingTime", setting("RocketExplosionCoolingTime"), 0);
// ... and with any of the three features on, the part is no longer the one-shot triple-phase rocket
// this shape describes (Rocket.cs:229, 291-292, 543).
for (const name of FEATURE_SETTINGS) {
  expect(`vanilla ${name}`, setting(name), false);
}

expect("Part_*.prefab files carrying m_boostForce", boostForcePrefabs, 26);
expect("Rocket prefabs", scan.count, 18);
for (const family of FAMILIES) {
  expect(`${family} prefabs`, scan.byFamily.get(family) ?? 0, family === "CokeBottle" || family === "SodaBottle" ? 5 : 4);
}

// Measured per-prefab values (the numbers the original ships) -- the raw prefab side.
histogramEquals("m_boostForce", scan.byBoostForce, [["35", 9], ["50", 8], ["150", 1]]);
histogramEquals("m_maximumSpeed", scan.byMaxSpeed, [["10", 8], ["18", 8], ["25", 1], ["150", 1]]);
histogramEquals("m_boostDuration", scan.byBoostDuration, [["1", 9], ["3", 8], ["500", 1]]);
expect("m_direction histogram", histogramLine(scan.direction), '{ "1,0,0": 18 }');
expect("exploding prefabs", scan.explodes.join(", "), "Part_Rocket_03_SET, Part_RedRocket_03_SET");
expect("visualization prefabs", scan.visualization.length, 10);
expect("bottle prefabs with m_visualization", scan.visualization.filter((name) => PART_TYPES[facts.get(name).partType].name.endsWith("Bottle")).length, 10);
expect("tier 4 prefabs", [...facts.values()].filter((fact) => fact.tier === LEGENDARY_TIER).map((fact) => fact.prefab).sort().join(", "), "Part_CokeBottle_05_SET, Part_SodaBottle_05_SET");

/** The effective numbers every prefab must produce, straight from the table above. */
function expectedOf(name) {
  if (/^Part_(Rocket|RedRocket)_0[1-4]_SET$/.test(name)) {
    return { boostForce: 50, maximumSpeed: 18, ignitionTicks: 60, boostTicks: 180, endTicks: 60 };
  }

  if (/^Part_(CokeBottle|SodaBottle)_0[1-4]_SET$/.test(name)) {
    return { boostForce: 35, maximumSpeed: 10, ignitionTicks: 60, boostTicks: 60, endTicks: 30 };
  }

  if (name === "Part_CokeBottle_05_SET") {
    return { boostForce: 35, maximumSpeed: 25, ignitionTicks: 60, boostTicks: 30000, endTicks: 30 };
  }

  if (name === "Part_SodaBottle_05_SET") {
    return { boostForce: 150, maximumSpeed: 150, ignitionTicks: 60, boostTicks: 60, endTicks: 30 };
  }

  return null;
}

const rockets = new Map();
for (const fact of facts.values()) {
  const expected = expectedOf(fact.prefab);
  if (expected === null) {
    fail(`${fact.prefab}: a Rocket prefab outside the four known families`);
  }

  if (familyOf(fact.prefab) !== fact.partTypeName) {
    invariants.push(`${fact.prefab}: m_partType ${fact.partType} (${fact.partTypeName}) does not match its prefab family`);
  }

  if (fact.direction.x !== 1 || fact.direction.y !== 0) {
    invariants.push(`${fact.prefab}: m_direction (${fact.direction.x},${fact.direction.y},${fact.direction.z}), expected (1,0,0)`);
  }

  if (fact.direction.z !== 0) {
    invariants.push(`${fact.prefab}: m_direction z is ${fact.direction.z}, expected 0 (PigForge's plane is 2.5D)`);
  }

  // Rocket.cs:78-83 + :235-240: only a part with the visualization child stalls during ignition.
  const bottle = fact.partTypeName.endsWith("Bottle");
  if (fact.visualization !== bottle) {
    invariants.push(`${fact.prefab}: m_visualization ${fact.visualization}, expected ${bottle}`);
  }

  const ignitionTicks = ticksOf(fact.ignitionTime, fact.prefab, "m_ignitionTime");
  const boostTicks = ticksOf(fact.boostDuration, fact.prefab, "m_boostDuration");
  const endTicks = ticksOf(fact.boostEndDuration, fact.prefab, "m_boostEndDuration");
  const effective = effectiveOf(fact);
  const exploding = fact.explodes !== 0;

  expect(`${fact.prefab}: m_explodes`, exploding ? 1 : 0, EXPLODING.has(fact.prefab) ? 1 : 0);
  expectClose(`${fact.prefab}: effective boostForce`, effective.boostForce, expected.boostForce);
  expectClose(`${fact.prefab}: effective maximumSpeed`, effective.maximumSpeed, expected.maximumSpeed);
  expect(`${fact.prefab}: ignitionTicks`, ignitionTicks, expected.ignitionTicks);
  expect(`${fact.prefab}: boostTicks`, boostTicks, expected.boostTicks);
  expect(`${fact.prefab}: endTicks`, endTicks, expected.endTicks);
  if (exploding) {
    expectClose(`${fact.prefab}: m_explosionRadius`, fact.explosionRadius, 8);
    expectClose(`${fact.prefab}: m_explosionImpulse`, fact.explosionImpulse, 25);
  } else {
    expectClose(`${fact.prefab}: m_explosionRadius`, fact.explosionRadius, 0);
    expectClose(`${fact.prefab}: m_explosionImpulse`, fact.explosionImpulse, 0);
  }

  /** The exact object apply-rockets.mjs writes, keys in the content order. */
  const rocket = {
    directionX: fact.direction.x,
    directionY: fact.direction.y,
    thrustPerTick: ROUND6(effective.boostForce / TICK_RATE_HZ),
    ignitionTicks,
    boostTicks,
    endTicks,
    maxSpeed: ROUND6(effective.maximumSpeed),
  };
  if (fact.visualization) {
    rocket.visualization = true;
  }

  if (exploding) {
    rocket.explodeRadius = ROUND6(fact.explosionRadius);
    rocket.explodeImpulse = ROUND6(fact.explosionImpulse);
  }

  rockets.set(fact.prefab, {
    fact,
    effective,
    rocket,
    source: {
      boostForce: fact.boostForce,
      maximumSpeed: fact.maximumSpeed,
      ignitionTime: fact.ignitionTime,
      boostDuration: fact.boostDuration,
      boostEndDuration: fact.boostEndDuration,
      explodes: fact.explodes,
      explosionRadius: fact.explosionRadius,
      explosionImpulse: fact.explosionImpulse,
      forceSetting: effective.forceSetting,
      speedSetting: effective.speedSetting,
      override: effective.override,
    },
  });
}

// ---------------------------------------------------------------- per content part

const assignments = loadAssignments();
const content = JSON.parse(readFileSync(CONTENT_PARTS, "utf8"));

/** partTypeId -> COUNT of content parts on that prefab, so coverage can be asserted both ways. */
const partsByPrefab = new Map();
for (const part of content.parts) {
  const prefab = assignments.get(part.partTypeId) ?? null;
  if (prefab === null) continue;
  if (!facts.has(prefab)) continue;
  partsByPrefab.set(prefab, [...(partsByPrefab.get(prefab) ?? []), part.partTypeId]);
}

const parts = {};
for (const part of content.parts) {
  const prefab = assignments.get(part.partTypeId) ?? null;
  if (prefab === null) continue;

  const entry = rockets.get(prefab);
  if (entry === undefined) continue;

  if (part.capabilities === undefined || typeof part.capabilities.rocket !== "object" || part.capabilities.rocket === null) {
    fail(`content part ${part.partTypeId} (${prefab}): capabilities.rocket is missing, so there is no slot to rewrite`);
  }

  parts[String(part.partTypeId)] = {
    partTypeId: part.partTypeId,
    prefab,
    partType: entry.fact.partType,
    tier: entry.fact.tier,
    rocket: entry.rocket,
    source: entry.source,
  };
}

// Coverage: every one of the 18 Rocket prefabs has exactly one content part.
for (const [prefab, mapped] of [...partsByPrefab].sort((left, right) => byFamilyOrder(left[0], right[0]))) {
  if (mapped.length !== 1) {
    invariants.push(`${prefab}: ${mapped.length} content parts (${mapped.join(", ")}), expected exactly 1`);
  }
}

expect("content parts on a Rocket prefab", Object.keys(parts).length, 18);

if (invariants.length > 0) {
  fail(`rocket invariants failed:\n  - ${invariants.join("\n  - ")}`);
}

// ---------------------------------------------------------------- report
const report = {
  format: "pigforge.bple-rockets.report",
  bple: BPLE,
  tickRateHz: TICK_RATE_HZ,
  inSettings: Object.fromEntries(inSettings),
  scan: {
    count: scan.count,
    byFamily: Object.fromEntries(FAMILIES.map((family) => [family, scan.byFamily.get(family) ?? 0])),
    byBoostForce: numericHistogram(scan.byBoostForce),
    byMaxSpeed: numericHistogram(scan.byMaxSpeed),
    byBoostDuration: numericHistogram(scan.byBoostDuration),
    explodes: scan.explodes,
    direction: Object.fromEntries(scan.direction),
    visualization: scan.visualization,
  },
  warnings,
  parts,
};
mkdirSync(dirname(OUT_JSON), { recursive: true });
writeFileSync(OUT_JSON, `${JSON.stringify(report, null, 2)}\n`);

const f = (value, digits = 6) => (typeof value === "number" ? Number(value.toFixed(digits)) : value);
const rocketObject = (rocket) => `{ "directionX": ${rocket.directionX}, "directionY": ${rocket.directionY}, "thrustPerTick": ${f(rocket.thrustPerTick)}, "ignitionTicks": ${rocket.ignitionTicks}, "boostTicks": ${rocket.boostTicks}, "endTicks": ${rocket.endTicks}, "maxSpeed": ${f(rocket.maxSpeed)}${rocket.visualization ? ', "visualization": true' : ""}${rocket.explodeRadius === undefined ? "" : `, "explodeRadius": ${f(rocket.explodeRadius)}, "explodeImpulse": ${f(rocket.explodeImpulse)}`} }`;

const md = [];
md.push("# 原版火箭族报告", "");
md.push("来源：`tools/bple-rockets/extract-rockets.mjs`，扫描 18 个 `Rocket` prefab（`Part_Rocket_01..04_SET`、");
md.push("`Part_RedRocket_01..04_SET`、`Part_CokeBottle_01..05_SET`、`Part_SodaBottle_01..05_SET`）。");
md.push("规则出处：`Rocket.cs:9-30`（序列化字段）、`:68-83`（`BottleVisualization` -> `m_visualization`）、");
md.push("`:180-200`（`Initialize` 的家族/等级覆盖）、`:228-300`（点火 / 平台 / 收尾三阶段，`ForceMode.Force`）、");
md.push("`:529-541`（`LimitForceForSpeed` 限速）、`:541-581`（`OnTouch` 一次性）、`:627-646`（`Explode`）。");
md.push(`IN 来源：\`${VANILLA_SETTINGS_NAME}\`（声明默认值，不是 \`INSettingsBExp.json\` 的 IN mod B 档）。`, "");
md.push("## prefab 直方图", "");
md.push(`- 带 \`m_boostForce\` 的 prefab：**${boostForcePrefabs}**（\`Rocket\` **${scan.count}**、\`Bellows\` ${boostForcePrefabs - scan.count}）`);
md.push(`- 家族：${FAMILIES.map((family) => `${family} **${scan.byFamily.get(family) ?? 0}**`).join("、")}`);
md.push(`- \`m_boostForce\`：${histogramLine(scan.byBoostForce)}`);
md.push(`- \`m_maximumSpeed\`：${histogramLine(scan.byMaxSpeed)}`);
md.push(`- \`m_boostDuration\`：${histogramLine(scan.byBoostDuration)}`);
md.push(`- \`m_direction\`：${histogramLine(scan.direction)}（z 必须为 0）`);
md.push(`- \`m_explodes 1\`：${scan.explodes.map((name) => `\`${name}\``).join("、")}`);
md.push(`- \`BottleVisualization\` 子物体（= 点火期内不推进，\`Rocket.cs:235-240\`）：${scan.visualization.length} 件`, "");
md.push("## PigForge 内容映射（18 件）", "");
md.push("`thrustPerTick = m_boostForce × IN <X>Force / 60`（ADR-013 决策 4），三段时间换算成 tick；legendary（tier 4）瓶子改用 Alien 对。", "");
md.push("| 内容 id | prefab | partType | tier | capabilities.rocket |", "|---|---|---|---|---|");
for (const [partTypeId, entry] of Object.entries(parts).sort((left, right) => Number(left[0]) - Number(right[0]))) {
  md.push(`| \`${partTypeId}\` | \`${entry.prefab}\` | ${entry.partType} ${PART_TYPES[entry.partType].name} | ${entry.tier} | ${rocketObject(entry.rocket)} |`);
}

if (warnings.length > 0) {
  md.push("", `## 警告（${warnings.length}）`, ...warnings.map((warning) => `- ${warning}`));
}

writeFileSync(OUT_MD, `${md.join("\n")}\n`);

console.log(`count: ${scan.count}`);
console.log(`byFamily: ${JSON.stringify(Object.fromEntries(FAMILIES.map((family) => [family, scan.byFamily.get(family) ?? 0])))}`);
console.log(`byBoostForce: ${histogramLine(scan.byBoostForce)}`);
console.log(`byMaxSpeed: ${histogramLine(scan.byMaxSpeed)}`);
console.log(`byBoostDuration: ${histogramLine(scan.byBoostDuration)}`);
console.log(`explodes: ${scan.explodes.join(", ")}`);
console.log(`direction: ${histogramLine(scan.direction)}`);
console.log(`visualization: ${scan.visualization.length} (${scan.visualization.join(", ")})`);
console.log(`content parts: ${Object.keys(parts).length}`);
if (warnings.length > 0) {
  console.log(`warnings: ${warnings.length}`);
}

console.log(`report: ${OUT_JSON}\n        ${OUT_MD}`);
