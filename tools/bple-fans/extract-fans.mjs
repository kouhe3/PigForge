// FanPropeller extractor: reads the `m_force` / `m_forceDirection` / `m_isRotor` /
// `m_defaultSpeed` of every prefab whose FanPropeller component is referenced, and reports the
// PigForge `capabilities.fan` values per partTypeId. This is the ONLY admissible source for the
// fan/propeller/rotor thrust in content/parts.json -- the same rule tools/bple-joints,
// tools/bple-materials, tools/bple-power and tools/bple-grid established for their values.
//
// Why it matters: the original models the fan, the plane propeller and the rotor as ONE class,
// `FanPropeller` (FanPropeller.cs:6, `m_partType` picks the IN multipliers at :93-107). PigForge
// had drifted into three different approximations (G50): the fan carried a thrust with no speed
// cap, the rotor was a balloon with unbounded lift and a "deflate and destroy" switch, and the
// propeller was misclassified as a driven wheel by tools/bple-power (FanPropeller does override
// InitializeEngine -- it is not a wheel), so its impulse pushed its own hinged body and it never
// propelled anything. This tool converts the fan and the rotor; the 10 plane propellers are
// reported but deferred, because their original carries no speed cap at all (see below).
//
// The numbers, straight from the class:
//
//   FanPropeller.cs:83-112  powerFactor = GetEnginePowerFactor(this); `> 1` -> pow(f, 0.75)
//                           maximumSpeed = powerFactor * m_defaultSpeed   (* IN <X>Speed)
//                           maximumForce = m_force * powerFactor          (* IN <X>Force)
//   FanPropeller.cs:120-209 FixedUpdate applies `LimitForceForSpeed(maximumForce, dir)` along
//                           `TransformDirection(GetDirectionVector(m_forceDirection))`
//   FanPropeller.cs:245-257 above maximumSpeed the force decays as
//                           `force / (1 + v.dir - maximumSpeed)`
//   FanPropeller.cs:198-207 m_isRotor adds `-4 (|v| - maximumSpeed)^2 v̂` past the cap
//   INSettingsBExp.json     FanForce 1.0 / FanSpeed 6.0, PropellerForce 1.0 / PropellerSpeed
//                           Infinity, RotorForce 1.0 / RotorSpeed 2.0
//
// The impulse conversion is the one ADR-013 decision 4 established for a force the original
// applies every `FixedUpdate` and PigForge every 60 Hz tick: content carries `force / 60`, exactly
// as tools/bple-lift derives the balloon's 23 N -> 0.383333. It is also the unit the room's own
// seam threshold lives in (`GameplayConfig.SeamBreakImpulse` = 10), so a part whose thrust is
// stated honestly cannot rip its own weld apart. The script asserts the balloon still satisfies the
// same conversion, so the two families cannot drift into different seconds.
//
// Measured on BPLE_Unity6 (343 Part_*.prefab): 26 carry a FanPropeller component -- the 6 fans,
// the 10 plane propellers and the 10 rotors -- `m_isRotor` is 1 on exactly the 10 rotors, and the
// force directions are Left (fan), Right (propeller, one Left variant) and Up (rotor, one Down
// variant). Those are hard invariants: drift fails this tool instead of silently writing a
// different world into content/parts.json.
//
// Usage: node tools/bple-fans/extract-fans.mjs [--bple <path>] [--json <path>] [--md <path>]
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
const OUT_JSON = resolve(arg("json", join(REPO, "tasks", "bple-fans-report.json")));
const OUT_MD = resolve(arg("md", join(REPO, "tasks", "bple-fans-report.md")));
const GAMEOBJECT = join(BPLE, "Assets", "GameObject");
const SCRIPTS = join(BPLE, "Assets", "Scripts", "Assembly-CSharp");
const SETTINGS = join(BPLE, "Assets", "TextAsset", "INSettingsBExp.json");
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

/** The original's own direction enum (BasePart.cs:86-96) and its vector map (`BasePart.cs:1126-1136`);
 * a FanPropeller only uses the four cardinal values. */
const DIRECTIONS = {
  0: { name: "Right", directionX: 1, directionY: 0 },
  1: { name: "Up", directionX: 0, directionY: 1 },
  2: { name: "Left", directionX: -1, directionY: 0 },
  3: { name: "Down", directionX: 0, directionY: -1 },
};

/** `PartType` values that mark a FanPropeller (BasePart.cs:23-47): the IN multipliers and the
 * chassis gate are picked by them (`FanPropeller.cs:93-107`). */
const PART_TYPES = {
  4: { name: "Fan", force: "FanForce", speed: "FanSpeed" },
  13: { name: "Propeller", force: "PropellerForce", speed: "PropellerSpeed" },
  22: { name: "Rotor", force: "RotorForce", speed: "RotorSpeed" },
};

/** The room's fixed tick rate (`GameRoomOptions.TickRateHz`, PlayHost) and the original's
 * `FixedUpdate` rate: the divisor that turns a per-second force into one impulse per tick. */
const TICK_RATE_HZ = 60;

/** The balloon family's shipped content value, used as the cross-family witness that this is the
 * same conversion (ADR-013 decision 4: Part_Balloon_01_SET m_force 11.5 x BalloonForce 2.0). */
const BALLOON_PREFAB = "Part_Balloon_01_SET";
const BALLOON_FORCE_SETTING = "BalloonForce";
const BALLOON_PART = 10;

/** The six IN entries a FanPropeller reads (FanPropeller.cs:93-107). */
const fanSettingNames = ["FanForce", "FanSpeed", "PropellerForce", "PropellerSpeed", "RotorForce", "RotorSpeed", "BalloonForce"];

function prefabText(name) {
  const path = join(GAMEOBJECT, `${name}.prefab`);
  return existsSync(path) ? readFileSync(path, "utf8") : null;
}

/** The first `field: value` in the text, or null. Every FanPropeller field is serialized once. */
function readField(text, field) {
  const match = new RegExp(`^\\s*${field}:\\s*(-?[0-9.]+)\\s*$`, "m").exec(text);
  if (!match) {
    return null;
  }

  const value = Number(match[1]);
  return Number.isFinite(value) ? value : null;
}

/** The `--- !u!114 &...` MonoBehaviour block that carries the FanPropeller component: the one
 * that serializes `m_isRotor`. Reading the fields out of that single block keeps `m_partType`
 * (also carried by the BasePart block) from being paired with another component's force. */
function fanPropellerBlock(text) {
  const blocks = text.split(/\n--- !u!/);
  return blocks.find((block) => /^\s*m_isRotor:/m.test(block)) ?? null;
}

/** class name of the script a block references, via the `.cs.meta` guid index (the same index
 * tools/bple-power and tools/bple-springs build). */
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

/** The FanPropeller fields of one prefab, or null when it has no FanPropeller component. The
 * block's script must resolve to `FanPropeller` -- a hard check, not a name guess. */
function readFanPropeller(text, prefab) {
  const block = fanPropellerBlock(text);
  if (block === null) {
    return null;
  }

  const guid = /m_Script:\s*\{fileID:\s*-?\d+,\s*guid:\s*([0-9a-f]{32})/m.exec(block)?.[1];
  const script = guid ? guidIndex.get(guid) : undefined;
  if (script !== "FanPropeller") {
    fail(`${prefab}: m_isRotor block references script '${script ?? "unresolved"}', expected FanPropeller`);
  }

  const partType = readField(block, "m_partType");
  const force = readField(block, "m_force");
  const direction = readField(block, "m_forceDirection");
  const isRotor = readField(block, "m_isRotor");
  const defaultSpeed = readField(block, "m_defaultSpeed");
  if (partType === null || force === null || direction === null || isRotor === null || defaultSpeed === null) {
    fail(`${prefab}: FanPropeller block is missing m_partType/m_force/m_forceDirection/m_isRotor/m_defaultSpeed`);
  }

  const partTypeInfo = PART_TYPES[partType];
  const directionInfo = DIRECTIONS[direction];
  if (!partTypeInfo || !directionInfo) {
    fail(`${prefab}: unexpected m_partType ${partType} / m_forceDirection ${direction}`);
  }

  return {
    script,
    partType,
    partTypeName: partTypeInfo.name,
    force,
    direction,
    directionName: directionInfo.name,
    isRotor: isRotor !== 0,
    defaultSpeed,
    powerConsumption: readField(block, "m_powerConsumption") ?? 0,
    enginePower: readField(block, "m_enginePower") ?? 0,
  };
}

/** The IN global multipliers (INSettingsBExp.json), reported as-is; `"Infinity"` is a real value
 * in that file and means "no speed cap" (the propeller). */
function readInSettings() {
  const text = readFileSync(SETTINGS, "utf8");
  const values = new Map();
  const pattern = /"name":\s*"(\w+)",\s*"scope":\s*"[^"]*",\s*"value":\s*("Infinity"|-?[0-9.]+)/g;
  for (const match of text.matchAll(pattern)) {
    values.set(match[1], match[2] === '"Infinity"' ? Number.POSITIVE_INFINITY : Number(match[2]));
  }

  for (const name of fanSettingNames) {
    if (!values.has(name)) {
      fail(`INSettingsBExp.json is missing '${name}'`);
    }
  }

  return values;
}

const inSettings = readInSettings();

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

const assignments = loadAssignments();
const content = JSON.parse(readFileSync(CONTENT_PARTS, "utf8"));
const nameByPart = new Map(content.parts.map((part) => [part.partTypeId, part.name ?? ""]));

// ---------------------------------------------------------------- whole-project scan
// The histogram is the fingerprint: 26 FanPropeller components, `m_isRotor` on exactly the 10
// rotors, and the three part types 6 / 10 / 10.
const scan = { count: 0, byPartType: {}, byDirection: {}, rotors: [], prefabs: {} };
for (const entry of readdirSync(GAMEOBJECT)) {
  if (!/^Part_.*\.prefab$/.test(entry)) continue;
  const text = readFileSync(join(GAMEOBJECT, entry), "utf8");
  if (!/^\s*m_isRotor:/m.test(text)) continue;

  const name = basename(entry, ".prefab");
  const fan = readFanPropeller(text, name);
  scan.count++;
  scan.byPartType[fan.partTypeName] = (scan.byPartType[fan.partTypeName] ?? 0) + 1;
  scan.byDirection[fan.directionName] = (scan.byDirection[fan.directionName] ?? 0) + 1;
  if (fan.isRotor) {
    scan.rotors.push(name);
  }

  scan.prefabs[name] = fan;
}

// ---------------------------------------------------------------- the force-to-impulse conversion
//
// `m_force` is a per-second force (`AddForceAtPosition(..., ForceMode.Force)`, FanPropeller.cs:209)
// and PigForge applies one impulse per tick, so every FanPropeller value is `m_force / 60` -- the
// IN `<X>Force` multiplier first (all three are 1.0 in the shipped settings, but the original
// multiplies them, so this tool does too).
const thrustPerTick = (force, forceMultiplier) => (force * forceMultiplier) / TICK_RATE_HZ;

// The balloon's own content value witnesses the same 60: if this drifts, the fan/propeller/rotor
// and the balloon are no longer measured in the same second.
const balloonText = prefabText(BALLOON_PREFAB);
const balloonForce = balloonText === null ? null : readField(balloonText, "m_force");
const balloonPart = content.parts.find((part) => part.partTypeId === BALLOON_PART);
const balloonLift = balloonPart?.capabilities?.balloon;

// ---------------------------------------------------------------- per content part
//
// This slice converts the fan and the rotor. The 10 plane propellers are reported but NOT applied:
// their original carries `PropellerSpeed = Infinity` (INSettingsBExp.json:299-301), i.e. no speed
// cap at all, so `LimitForceForSpeed` never bounds them and the original relies on the plane's own
// drag and weight. Converting them today would turn a part that currently does nothing (they are
// misclassified as driven wheels, so their impulse pushes their own hinged body) into an uncapped
// thruster, and picking a cap is a gameplay decision, not an extraction. See
// docs/specs/fan-propeller.md.
const DEFERRED_PART_TYPES = new Set(["Propeller"]);

const parts = {};
const deferred = {};
const unmapped = [];
let fanPropellerContentParts = 0;
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

  const fan = readFanPropeller(text, prefab);
  if (fan === null) {
    continue;
  }

  const info = PART_TYPES[fan.partType];
  const forceMultiplier = inSettings.get(info.force);
  const speedMultiplier = inSettings.get(info.speed);
  // `maximumSpeed = powerFactor * m_defaultSpeed * IN <X>Speed` (FanPropeller.cs:90,100-106): the
  // content value is the per-unit-power-factor speed, so the rules layer multiplies it by the
  // cluster's power factor. Infinity means the original never caps it (the propeller).
  const maxSpeed = Number.isFinite(speedMultiplier) ? fan.defaultSpeed * speedMultiplier : null;
  const thrust = thrustPerTick(fan.force, forceMultiplier);
  fanPropellerContentParts++;

  const entry = {
    prefab,
    name: nameByPart.get(part.partTypeId) ?? "",
    partType: fan.partTypeName,
    direction: fan.directionName,
    directionX: DIRECTIONS[fan.direction].directionX,
    directionY: DIRECTIONS[fan.direction].directionY,
    force: fan.force,
    forceMultiplier,
    defaultSpeed: fan.defaultSpeed,
    speedMultiplier: Number.isFinite(speedMultiplier) ? speedMultiplier : "Infinity",
    maxSpeed,
    rotor: fan.isRotor,
    thrustPerTick: thrust,
    powerConsumption: fan.powerConsumption,
  };

  if (DEFERRED_PART_TYPES.has(entry.partType)) {
    deferred[part.partTypeId] = entry;
  } else {
    parts[part.partTypeId] = entry;
  }
}

// ---------------------------------------------------------------- hard invariants
// Measured on BPLE_Unity6: 26 FanPropeller components, 6 fans / 10 propellers / 10 rotors,
// `m_isRotor` on exactly the 10 rotors, and the direction histogram below. Drift fails the tool
// instead of silently writing a different world into content/parts.json.
const invariants = [];
const expect = (label, actual, wanted) => {
  if (actual !== wanted) {
    invariants.push(`${label}: expected ${wanted}, got ${actual}`);
  }
};

expect("FanPropeller prefab count", scan.count, 26);
expect("Fan part type", scan.byPartType.Fan, 6);
expect("Propeller part type", scan.byPartType.Propeller, 10);
expect("Rotor part type", scan.byPartType.Rotor, 10);
expect("m_isRotor count", scan.rotors.length, 10);
expect("Left directions", scan.byDirection.Left, 7);
expect("Right directions", scan.byDirection.Right, 9);
expect("Up directions", scan.byDirection.Up, 9);
expect("Down directions", scan.byDirection.Down, 1);
expect("content parts on a FanPropeller prefab", fanPropellerContentParts, 26);
expect("fan + rotor parts converted", Object.keys(parts).length, 16);
expect("propeller parts deferred", Object.keys(deferred).length, 10);

const rotorsOffFamily = scan.rotors.filter((name) => !/^Part_Rotor_/.test(name));
if (rotorsOffFamily.length > 0) {
  invariants.push(`m_isRotor outside the Rotor family: ${rotorsOffFamily.join(", ")}`);
}

const deferredOffFamily = Object.values(deferred).filter((part) => part.partType !== "Propeller");
if (deferredOffFamily.length > 0) {
  invariants.push(`deferred parts outside the Propeller family: ${deferredOffFamily.map((part) => part.prefab).join(", ")}`);
}

const missingCoverage = Object.keys(scan.prefabs).filter(
  (name) => ![...Object.values(parts), ...Object.values(deferred)].some((part) => part.prefab === name),
);
if (missingCoverage.length > 0) {
  invariants.push(`FanPropeller prefabs with no content part: ${missingCoverage.join(", ")}`);
}

for (const [partTypeId, part] of Object.entries(parts)) {
  if (part.rotor !== (part.partType === "Rotor")) {
    invariants.push(`part ${partTypeId}: rotor flag does not match part type ${part.partType}`);
  }

  if (part.maxSpeed !== null && !(part.maxSpeed > 0)) {
    invariants.push(`part ${partTypeId}: maxSpeed must be positive when present, got ${part.maxSpeed}`);
  }
}

// Cross-family witness (ADR-013 decision 4): the balloon family must still satisfy the same
// `/ 60` this tool applies, in the same IN settings file.
if (typeof balloonLift !== "number" || balloonForce === null) {
  invariants.push(`the ${BALLOON_PREFAB} witness is missing from ${BALLOON_PREFAB} or content part ${BALLOON_PART}`);
} else {
  const balloonSetting = inSettings.get(BALLOON_FORCE_SETTING);
  if (!Number.isFinite(balloonSetting)) {
    invariants.push(`INSettingsBExp.json is missing '${BALLOON_FORCE_SETTING}'`);
  } else {
    const expected = (balloonForce * balloonSetting) / TICK_RATE_HZ;
    if (Math.abs(balloonLift - expected) > 1e-6) {
      invariants.push(`content part ${BALLOON_PART} balloon ${balloonLift} is not m_force x ${BALLOON_FORCE_SETTING} / ${TICK_RATE_HZ} = ${expected}`);
    }
  }
}

// Every FanPropeller must convert at exactly the same rate; a per-part calibration would make the
// rules layer's own `4 * excess^2 / 60` brake (FanPropeller.cs:198-207) measure something else.
for (const [partTypeId, part] of [...Object.entries(parts), ...Object.entries(deferred)]) {
  const expected = thrustPerTick(part.force, part.forceMultiplier);
  if (Math.abs(part.thrustPerTick - expected) > 1e-6) {
    invariants.push(`part ${partTypeId}: thrustPerTick ${part.thrustPerTick} is not m_force / ${TICK_RATE_HZ} = ${expected}`);
  }
}

if (invariants.length > 0) {
  fail(`FanPropeller invariants failed:\n  - ${invariants.join("\n  - ")}`);
}

// ---------------------------------------------------------------- report
const report = { bple: BPLE, tickRateHz: TICK_RATE_HZ, inSettings: Object.fromEntries(fanSettingNames.map((name) => [name, inSettings.get(name)])), scan, warnings, unmapped, parts, deferred };
mkdirSync(dirname(OUT_JSON), { recursive: true });
writeFileSync(OUT_JSON, `${JSON.stringify(report, null, 2)}\n`);

const f = (value, digits = 6) => (typeof value === "number" ? Number(value.toFixed(digits)) : value);
const md = [];
md.push("# 原版 FanPropeller 报告（风扇 / 螺旋桨 / 旋翼）", "");
md.push("来源：`tools/bple-fans/extract-fans.mjs`，扫描原版全部 `Part_*.prefab`。");
md.push("规则出处：`FanPropeller.cs:83-112`（功率因子与上限）、`:120-209`（出力）、`:245-257`（限速）、");
md.push("`:198-207`（旋翼过速刹车）、`INSettingsBExp.json`（`FanForce`/`FanSpeed`/`PropellerForce`/");
md.push("`PropellerSpeed`/`RotorForce`/`RotorSpeed`）。", "");
md.push(`**冲量换算**：原版在 \`FixedUpdate\` 施加的是**每秒的力**（\`AddForceAtPosition(..., ForceMode.Force)\`，`);
md.push(`FanPropeller.cs:209），容器每 60 Hz tick 施加一次冲量，故 \`thrustPerTick = m_force × IN <X>Force / ${TICK_RATE_HZ}\`——`);
md.push(`与 ADR-013 决策 4 给气球的换算（\`tools/bple-lift\`，23 N → 0.383333）同一条，也是容器接缝阈值`);
md.push(`（\`GameplayConfig.SeamBreakImpulse\` = 10）所在的单位。本工具用气球族的现有内容值做同族见证断言。`, "");
md.push("**最高速**：原版 `maximumSpeed = powerFactor × m_defaultSpeed × IN <X>Speed`，内容只写单位功率因子那一份，");
md.push("规则层再乘簇功率因子；`PropellerSpeed = Infinity` 的原版没有上限，内容不写该字段。", "");
md.push("## 全量扫描（原版 prefab 计数）", "");
md.push(`- FanPropeller prefab：**${scan.count}**`);
md.push(`- 类型：${Object.entries(scan.byPartType).map(([key, value]) => `${key} ${value}`).join("、")}`);
md.push(`- 方向：${Object.entries(scan.byDirection).map(([key, value]) => `${key} ${value}`).join("、")}`);
md.push(`- \`m_isRotor\`：${scan.rotors.length}（${scan.rotors.join(", ")}）`, "");
md.push("## PigForge 内容映射（26 件）", "");
md.push("`范围` = `写入` 表示 `apply-fans.mjs` 会写进 `content/parts.json`；`缓办` 见下一节。", "");
md.push("| 内容 id | 名称 | prefab | 类型 | 方向 | m_force | thrustPerTick | maxSpeed | rotor | 范围 |", "|---|---|---|---|---|---|---|---|---|---|");
for (const [partTypeId, part] of [...Object.entries(parts), ...Object.entries(deferred)].sort((left, right) => Number(left[0]) - Number(right[0]))) {
  const scope = Object.hasOwn(parts, partTypeId) ? "写入" : "缓办";
  md.push(`| \`${partTypeId}\` | ${part.name} | \`${part.prefab}\` | ${part.partType} | ${part.direction} | ${part.force} | ${f(part.thrustPerTick)} | ${part.maxSpeed === null ? "∞" : f(part.maxSpeed)} | ${part.rotor} | ${scope} |`);
}

md.push("", "## 缓办的螺旋桨族（10 件）", "");
md.push("原版给螺旋桨的 `PropellerSpeed` 是 **`Infinity`**（`INSettingsBExp.json:299-301`），");
md.push("于是 `maximumSpeed = powerFactor × m_defaultSpeed × Infinity = ∞`，`LimitForceForSpeed` **永不生效**——");
md.push("原版靠飞机自身的阻力与重量收住它。PigForge 现在把这 10 件建模成 `wheel` + `motor`（`tools/bple-power`");
md.push("的「有 `InitializeEngine` 覆盖即驱动轮」判据误判：`FanPropeller` 确实覆盖了它），装配时它们被铰接成独立刚体，");
md.push("冲量只推自己，**不产生推进**。直接转成 FanPropeller 会把它变成**没有上限的推进器**，");
md.push("给它定上限是玩法决定而非提取，故本切片缓办（`docs/specs/fan-propeller.md` §7）。", "");
md.push("| 内容 id | 名称 | prefab | 方向 | m_force | 若转换的 thrustPerTick |", "|---|---|---|---|---|---|");
for (const [partTypeId, part] of Object.entries(deferred).sort((left, right) => Number(left[0]) - Number(right[0]))) {
  md.push(`| \`${partTypeId}\` | ${part.name} | \`${part.prefab}\` | ${part.direction} | ${part.force} | ${f(part.thrustPerTick)}（**未写入**） |`);
}

md.push("", "## IN 全局倍率（`INSettingsBExp.json`）", "");
for (const [key, value] of Object.entries(report.inSettings)) {
  md.push(`- \`${key}\` = ${Number.isFinite(value) ? value : "Infinity"}`);
}

if (unmapped.length > 0) {
  md.push("", `未映射的 partTypeId（${unmapped.length}）：${unmapped.join(", ")}`);
}

if (warnings.length > 0) {
  md.push("", "## Warnings", "");
  for (const warning of warnings) {
    md.push(`- ${warning}`);
  }
}

writeFileSync(OUT_MD, `${md.join("\n")}\n`);

console.log(`FanPropeller prefabs: ${scan.count} (${JSON.stringify(scan.byPartType)}; ${JSON.stringify(scan.byDirection)})`);
console.log(`rotor flags: ${scan.rotors.length}; content parts: ${fanPropellerContentParts}`);
console.log(`applied: ${Object.keys(parts).length} (fan + rotor); deferred: ${Object.keys(deferred).length} (plane propeller, no speed cap in the original)`);
console.log(`conversion: m_force x IN <X>Force / ${TICK_RATE_HZ} (witnessed against the balloon family)`);
if (warnings.length > 0) {
  console.log(`warnings: ${warnings.length}`);
}

console.log(`report: ${OUT_JSON}\n        ${OUT_MD}`);
