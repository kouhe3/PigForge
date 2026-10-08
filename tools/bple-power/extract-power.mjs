// Power capability extractor: reads each part prefab's `m_powerConsumption` and
// `m_enginePower` -- plus, for the wheels the original drives, its `m_force` -- and reports
// the values per PigForge partTypeId. This is the ONLY admissible source for power and wheel
// drive data in content/parts.json -- the same rule tools/bple-joints and tools/bple-materials
// established for their values.
//
// Why it matters (Contraption.cs:540-556, the DynamicPowerSystem = true branch):
//
//   raw = min(enginePower / powerConsumption, 10 * EnginePowerLimit)   // consumption > 1
//   raw = 1                                                            // enginePower > 0
//   factor = pow(raw, raw > 1 ? 0.585 : 0.75)
//
// where `enginePower`/`powerConsumption` are the sums of the members' serialized
// `m_enginePower`/`m_powerConsumption` over one connected component (Contraption.cs:1378-1379,
// and only ENABLED consumers: Contraption.cs:2633-2644). `BasePart.cs:162,164` declares the two
// fields; `BasePart.cs:601-608` defines a powered part as consumption > 0 and an engine as
// enginePower > 0. `Engine.cs:29,61` shows the engine applies no force and only powers parts of
// its own component while it is enclosed. Measured on the original (343 Part_*.prefab files):
// 296 prefabs consume nothing, 47 consume, 44 carry engine power (24 real engines plus 20 pig
// prefabs, which the original sums into enginePower but excludes from its `hasEngine` flag,
// Contraption.cs:1379-1380), and no prefab is both.
//
// Usage: node tools/bple-power/extract-power.mjs [--bple <path>] [--json <path>] [--md <path>]
import { existsSync, readFileSync, readdirSync } from "node:fs";
import { join } from "node:path";
import { CONTENT, bpleProject, gameObjects, reportJson, reportMd, scriptAssembly } from "../lib/paths.mjs";
import { fail, writeJsonArtifact, writeMarkdownArtifact } from "../lib/report.mjs";
import { assignments as loadAssignments, buildClassBases, buildGuidIndex, derivesFromBasePart, prefabText, readField } from "../lib/unity.mjs";

const BPLE = bpleProject();
const OUT_JSON = reportJson("power");
const OUT_MD = reportMd("power");
const GAMEOBJECT = gameObjects(BPLE);
const SCRIPTS = scriptAssembly(BPLE);

if (!existsSync(GAMEOBJECT)) {
  fail(`BPLE project not found: ${GAMEOBJECT}\nPass --bple <path to BPLE_Unity6>.`);
}

const warnings = [];

/** The first `m_powerConsumption`/`m_enginePower` in the file. Every part prefab carries
 * exactly one of each, on the BasePart MonoBehaviour that owns the part's physics behaviour
 * (BasePart.cs:162,164; the template assigns them at BasePart.cs:1445-1446). */
function readPower(text) {
  const powerConsumption = readField(text, "m_powerConsumption");
  const enginePower = readField(text, "m_enginePower");
  if (powerConsumption === null || enginePower === null) {
    return null;
  }

  return { powerConsumption, enginePower };
}

// ---------------------------------------------------------------- wheel drive
//
// The original separates the wheels it drives from the wheels that only roll. A driven wheel
// overrides `InitializeEngine()` -- BasePart's engine hook -- and scales `m_force` and its top
// speed by its component's engine power factor (MotorWheel.cs:99-104, StickyWheel.cs:117-122,
// OffRoadWheel.cs:172-180), and it toggles through `HasOnOffToggle()` (MotorWheel.cs:65-73,
// StickyWheel.cs:68-76). The passive CartWheel overrides neither: its FixedUpdate only reads
// contact (CartWheel.cs:129-149). Both driven classes overwrite the serialized `m_maximumSpeed`
// in InitializeEngine (MotorWheel.cs:103, StickyWheel.cs:121), so `m_force` is the only usable
// drive number in the prefab.
//
// PigForge models a driven wheel as `wheel` + `motor` (its per-tick impulse, the same capability
// the motor-wheel and propeller use, GameplayRules.RunMotors) + `activation: "toggle"`. Only the
// motor wheel's impulse has been calibrated by playtest so far -- 2.2 for `m_force` 50
// (content part 17, docs/specs/part-texture-animation.md "阈值标定") -- so every other driven
// wheel scales from that anchor by the original's own force ratio. That keeps the original's
// relative strengths: the sticky wheel carries `m_force` 100 against the motor wheel's 50, so it
// drives twice as hard.

/** Whether a class overrides BasePart's engine hook, i.e. is driven by the power factor. */
function overridesInitializeEngine(scriptName) {
  const text = readFileSync(join(SCRIPTS, `${scriptName}.cs`), "utf8");
  return /public override void InitializeEngine\s*\(\s*\)/.test(text);
}

const guidIndex = buildGuidIndex(SCRIPTS);
const classBases = buildClassBases(SCRIPTS);

// Derived, not listed: a driven wheel is a BasePart subclass whose name ends in `Wheel` and
// which overrides InitializeEngine (MotorWheel, OffRoadWheel, StickyWheel on 2.4.0 BPLE).
// Name-scoping is what keeps `FanPropeller` out: it is a BasePropulsion that overrides the same
// hook (`InitializeEngine`, FanPropeller.cs:83) and is a thruster, not a wheel -- tools/bple-fans
// owns its numbers. A part carrying both capabilities is a hard error below.
const drivenWheelClasses = new Set(
  [...classBases.keys()]
    .filter((name) => name.endsWith("Wheel") && derivesFromBasePart(classBases, name) && overridesInitializeEngine(name)),
);

/** The driven-wheel script a prefab instantiates, or null when it is a passive wheel
 * (or not a wheel at all). */
function drivenWheelScript(text) {
  const referenced = new Set(
    [...text.matchAll(/m_Script:\s*\{fileID:\s*-?\d+,\s*guid:\s*([0-9a-f]{32})/g)]
      .map((match) => guidIndex.get(match[1]))
      .filter(Boolean),
  );
  const driven = [...referenced].filter((name) => drivenWheelClasses.has(name));
  if (driven.length > 0) return driven[0];
  return null;
}

/** The serialized `m_force` / `m_maximumSpeed` of a wheel MonoBehaviour, plus whether the prefab
 * carries the `m_enabled` switch field the original toggles. */
function readDrive(text) {
  const force = readField(text, "m_force");
  const maximumSpeed = readField(text, "m_maximumSpeed");
  const enabled = readField(text, "m_enabled");
  if (force === null || force <= 0 || maximumSpeed === null || enabled === null) {
    return null;
  }

  return { force, maximumSpeed };
}

/** partTypeId -> prefab name, reusing the mapping the shapes/textures extractors established so
 * this tool cannot drift into a second convention. */
const assignments = loadAssignments();
const content = JSON.parse(readFileSync(CONTENT, "utf8"));
const nameByPart = new Map(content.parts.map((part) => [part.partTypeId, part.name ?? ""]));

// The drive anchor: the motor wheel is the one driven wheel whose PigForge impulse has been
// calibrated by playtest. Its original force comes from the prefab, its impulse from content,
// so re-running this tool can never drift away from the number the game was tuned around.
const DRIVE_ANCHOR_PREFAB = "Part_MotorWheel_01_SET";
const DRIVE_ANCHOR_PART = 17;
const anchorText = prefabText(GAMEOBJECT, DRIVE_ANCHOR_PREFAB);
const anchorForce = anchorText === null ? null : readDrive(anchorText);
const anchorPart = content.parts.find((part) => part.partTypeId === DRIVE_ANCHOR_PART);
const anchorImpulse = anchorPart?.capabilities?.motor?.thrustPerTick;
if (anchorForce === null || typeof anchorImpulse !== "number" || !(anchorImpulse > 0)) {
  fail(
    `drive anchor missing: ${DRIVE_ANCHOR_PREFAB} must carry m_force/m_maximumSpeed/m_enabled and ` +
      `content part ${DRIVE_ANCHOR_PART} must carry capabilities.motor.thrustPerTick`,
  );
}

/** The original's force ratio against the anchor, applied to the calibrated impulse. */
const motorThrustPerTick = (force) => anchorImpulse * (force / anchorForce.force);

const parts = {};
const unmapped = [];
for (const part of content.parts) {
  const prefab = assignments.get(part.partTypeId) ?? null;
  if (!prefab) {
    unmapped.push(part.partTypeId);
    continue;
  }

  const text = prefabText(GAMEOBJECT, prefab);
  if (text === null) {
    warnings.push(`part ${part.partTypeId} (${prefab}): prefab file missing`);
    continue;
  }

  const power = readPower(text);
  if (power === null) {
    warnings.push(`part ${part.partTypeId} (${prefab}): m_powerConsumption/m_enginePower absent`);
    continue;
  }

  const entry = {
    prefab,
    name: nameByPart.get(part.partTypeId) ?? "",
    powerConsumption: power.powerConsumption,
    enginePower: power.enginePower,
    // The original's own predicates (BasePart.cs:601-608).
    powered: power.powerConsumption > 0,
    engine: power.enginePower > 0,
  };

  const script = drivenWheelScript(text);
  if (script !== null) {
    const drive = readDrive(text);
    if (drive === null) {
      warnings.push(`part ${part.partTypeId} (${prefab}): ${script} prefab has no m_force/m_maximumSpeed/m_enabled`);
    } else {
      entry.drive = {
        script,
        force: drive.force,
        maximumSpeed: drive.maximumSpeed,
        motorThrustPerTick: motorThrustPerTick(drive.force),
        // HasOnOffToggle() => true on every driven wheel (MotorWheel.cs:65-73,
        // StickyWheel.cs:68-76); the sandbox starts every toggle off, as the prefab does.
        activation: "toggle",
      };
    }
  }

  parts[part.partTypeId] = entry;
}

// Content-level cross-check: no part carries both the wheel drive and the fan thrust. The
// FanPropeller family overrides InitializeEngine exactly like a driven wheel does, which is how
// the plane propellers once ended up modelled as `wheel` + `motor` (G50): this tool owns the
// wheels, tools/bple-fans owns the fans, and a part in both models is a mistake either side can
// introduce.
const bothModels = content.parts
  .filter((part) => part.capabilities?.motor !== undefined && part.capabilities?.fan !== undefined)
  .map((part) => part.partTypeId);
if (bothModels.length > 0) {
  fail(`parts carry both a motor and a fan capability: ${bothModels.join(", ")}`);
}

// Whole-project tally, so the report can state the distribution even for prefabs that never made
// it into content (dropped inventions, unimported parts).
const distribution = { powerConsumption: {}, enginePower: {}, classification: { neither: 0, powered: 0, engine: 0, both: 0 } };
const bump = (bucket, value) => {
  const key = String(value);
  bucket[key] = (bucket[key] ?? 0) + 1;
};

for (const entry of readdirSync(GAMEOBJECT)) {
  if (!entry.startsWith("Part_") || !entry.endsWith(".prefab")) {
    continue;
  }

  const power = readPower(readFileSync(join(GAMEOBJECT, entry), "utf8"));
  if (power === null) {
    warnings.push(`${entry}: m_powerConsumption/m_enginePower absent`);
    continue;
  }

  bump(distribution.powerConsumption, power.powerConsumption);
  bump(distribution.enginePower, power.enginePower);
  const powered = power.powerConsumption > 0;
  const engine = power.enginePower > 0;
  distribution.classification[powered && engine ? "both" : powered ? "powered" : engine ? "engine" : "neither"] += 1;
}

const report = { bple: BPLE, distribution, warnings, unmapped, parts };
writeJsonArtifact(OUT_JSON, report);

const byField = (field) =>
  Object.entries(parts)
    .filter(([, value]) => value[field] > 0)
    .sort((left, right) => Number(left[0]) - Number(right[0]));

const md = [];
md.push("# 原版动力数据报告（`m_powerConsumption` / `m_enginePower`）", "");
md.push("来源：`tools/bple-power/extract-power.mjs`，扫描原版全部 `Part_*.prefab`。");
md.push("规则出处：`Contraption.cs:540-556`（功率因子公式）、`Contraption.cs:1378-1379`（按分量累加）、`BasePart.cs:601-608`（耗能件/引擎判定）、`Engine.cs:29,61`（引擎不出力且必须被包裹）。", "");

md.push("## 全量分布（原版 prefab 计数）", "");
for (const field of ["powerConsumption", "enginePower"]) {
  md.push(`### \`${field}\``, "", "| 值 | 数量 |", "|---|---|");
  for (const [value, count] of Object.entries(distribution[field]).sort((a, b) => Number(b[0]) - Number(a[0]))) {
    md.push(`| \`${value}\` | ${count} |`);
  }

  md.push("");
}

md.push("### 分类", "", "| 类别 | 数量 |", "|---|---|");
for (const [kind, count] of Object.entries(distribution.classification)) {
  md.push(`| \`${kind}\` | ${count} |`);
}

md.push(
  "",
  "猪 prefab 带 `m_enginePower = 20`：原版把它累加进分量的 `enginePower`（`Contraption.cs:1379`",
  "不带类型判断），但 `hasEngine` 标志显式排除猪（`Contraption.cs:1380`）。规格按 `BasePart.cs:601-608`",
  "把 `enginePower > 0` 一律视为引擎，提取器照抄 prefab 真值，不做筛选。",
  "",
);

md.push("## PigForge 内容里的取值", "");
md.push(`### 引擎（\`enginePower > 0\`，${byField("enginePower").length} 条）`, "");
for (const [partTypeId, value] of byField("enginePower")) {
  md.push(`- \`${partTypeId}\` ${value.name} — \`${value.prefab}\`，enginePower ${value.enginePower}`);
}

md.push("", `### 耗能件（\`powerConsumption > 0\`，${byField("powerConsumption").length} 条）`, "");
for (const [partTypeId, value] of byField("powerConsumption")) {
  md.push(`- \`${partTypeId}\` ${value.name} — \`${value.prefab}\`，powerConsumption ${value.powerConsumption}`);
}

const drivenWheels = Object.entries(parts)
  .filter(([, value]) => value.drive)
  .sort((left, right) => Number(left[0]) - Number(right[0]));
md.push("", `### 驱动轮（原版脚本 override \`InitializeEngine()\`，${drivenWheels.length} 条）`, "");
md.push(
  `每 tick 冲量以马达轮为标定锚：\`${anchorImpulse} × m_force / ${anchorForce.force}\``,
  `（锚值来自 content 的 part ${DRIVE_ANCHOR_PART}，力来自 \`${DRIVE_ANCHOR_PREFAB}\`）。`,
  "驱动轮一律写成 `motor{directionX:1}` + `activation:\"toggle\"`，与马达轮/螺旋桨同一条 `GameplayRules.RunMotors` 门控路径。",
  "",
);
for (const [partTypeId, value] of drivenWheels) {
  md.push(
    `- \`${partTypeId}\` ${value.name} — \`${value.prefab}\`（${value.drive.script}），m_force ${value.drive.force}、` +
      `m_maximumSpeed ${value.drive.maximumSpeed}（运行时被覆盖）→ thrustPerTick ${value.drive.motorThrustPerTick}`,
  );
}

if (unmapped.length > 0) {
  md.push("", "## 未映射到 prefab 的 partTypeId", "", unmapped.join(", "), "");
}

if (warnings.length > 0) {
  md.push("## 警告", "");
  for (const warning of warnings) {
    md.push(`- ${warning}`);
  }

  md.push("");
}

writeMarkdownArtifact(OUT_MD, md);

console.log(`prefabs: ${Object.values(distribution.classification).reduce((sum, count) => sum + count, 0)} (${JSON.stringify(distribution.classification)})`);
console.log(`powerConsumption: ${JSON.stringify(distribution.powerConsumption)}`);
console.log(`enginePower: ${JSON.stringify(distribution.enginePower)}`);
for (const field of ["enginePower", "powerConsumption"]) {
  const rows = byField(field);
  console.log(`${field.padEnd(16)} parts=${String(rows.length).padStart(3)}  ${rows.map(([id, value]) => `${id}:${value.name}`).join(", ")}`);
}

console.log(
  `drivenWheels:     parts=${String(drivenWheels.length).padStart(3)}  ` +
    `${drivenWheels.map(([id, value]) => `${id}:${value.drive.script} force ${value.drive.force} -> ${value.drive.motorThrustPerTick}`).join(", ")}`,
);

if (unmapped.length > 0) {
  console.log(`unmapped partTypeIds: ${unmapped.join(", ")}`);
}

if (warnings.length > 0) {
  console.log(`warnings: ${warnings.length}`);
}

console.log(`report: ${OUT_JSON}\n        ${OUT_MD}`);
