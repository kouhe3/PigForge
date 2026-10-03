// Wheel-suspension extractor: reads the original's wheel-family scripts and the
// per-prefab overrides of their serialized spring field, and reports the elastic
// wheel connection per PigForge partTypeId. This is the ONLY admissible source for
// `capabilities.suspension` in content/parts.json -- the same rule tools/bple-joints,
// tools/bple-materials and tools/bple-power established for their values.
//
// Why it matters: a part is welded to its neighbour by `Contraption.AddFixedJoint`
// (Contraption.cs:1507-1546, a ConfigurableJoint with every motion mode Locked) unless
// the part overrides `BasePart.CustomConnectToPart` (BasePart.cs:1206 returns null) or
// `PartGeneratorManager.CreateJoints` picks the part as the joint's owner
// (PartGeneratorManager.cs:472-546, driven by `m_customJointConnectionDirection`).
// Among the wheel family only `OffRoadWheel` overrides it, and that override is the
// original's one soft wheel attachment:
//
//   configurableJoint.angularX/Y/ZMotion = Locked
//   configurableJoint.xMotion = Locked; yMotion = Limited; zMotion = Locked
//   linearLimitSpring.spring = m_springStiffness; linearLimitSpring.damper = 5f
//   linearLimit.limit = 0f; linearLimit.bounciness = 0f            (OffRoadWheel.cs:202-220)
//
// i.e. the wheel's local Y is a spring holding it at zero offset from the chassis.
//
// Usage: node tools/bple-springs/extract-springs.mjs [--bple <path>] [--json <path>] [--md <path>]
import { existsSync, mkdirSync, readFileSync, readdirSync, writeFileSync } from "node:fs";
import { basename, dirname, join, resolve } from "node:path";
import { fileURLToPath } from "node:url";

const HERE = dirname(fileURLToPath(import.meta.url));
const REPO = resolve(HERE, "..", "..");

function arg(name, fallback) {
  const index = process.argv.indexOf(`--${name}`);
  const value = index >= 0 && process.argv[index + 1] ? process.argv[index + 1] : fallback;
  return value;
}

const BPLE = resolve(arg("bple", process.env.BPLE_ROOT ?? join(REPO, "..", "BPLE_Unity6")));
const OUT_JSON = resolve(arg("json", join(REPO, "tasks", "bple-springs-report.json")));
const OUT_MD = resolve(arg("md", join(REPO, "tasks", "bple-springs-report.md")));
const ASSETS = join(BPLE, "Assets");
const SCRIPTS = join(ASSETS, "Scripts", "Assembly-CSharp");
const GAMEOBJECT = join(ASSETS, "GameObject");
const TEXTEXT = join(ASSETS, "TextAsset");
const CONTENT_PARTS = join(REPO, "content", "parts.json");
const TEXTURE_MAP = join(REPO, "tools", "bple-textures", "part-map.json");

if (!existsSync(SCRIPTS) || !existsSync(GAMEOBJECT)) {
  console.error(`BPLE project not found: ${SCRIPTS}\nPass --bple <path to BPLE_Unity6>.`);
  process.exit(1);
}

const warnings = [];
const fail = (message) => {
  console.error(message);
  process.exit(1);
};

// ------------------------------------------------------------------ script index
/** guid -> script class name, so a prefab's `m_Script` guids become class names. */
function buildGuidIndex() {
  const byGuid = new Map();
  for (const entry of readdirSync(SCRIPTS)) {
    if (!entry.endsWith(".cs.meta")) continue;
    const text = readFileSync(join(SCRIPTS, entry), "utf8");
    const match = /^guid:\s*([0-9a-f]{32})/m.exec(text);
    if (match) byGuid.set(match[1], basename(entry, ".cs.meta"));
  }
  return byGuid;
}

/** class name -> base class name, for every script in the assembly. */
function buildClassBases() {
  const bases = new Map();
  for (const entry of readdirSync(SCRIPTS)) {
    if (!entry.endsWith(".cs")) continue;
    const text = readFileSync(join(SCRIPTS, entry), "utf8");
    const match = /^\s*public class (\w+)\s*:\s*([\w<>]+)/m.exec(text);
    if (match) bases.set(match[1], match[2]);
  }
  return bases;
}

const guidIndex = buildGuidIndex();
const classBases = buildClassBases();

const derivesFromBasePart = (name) => {
  const seen = new Set();
  let current = name;
  while (current && !seen.has(current)) {
    seen.add(current);
    if (current === "BasePart") return true;
    current = classBases.get(current);
  }
  return false;
};

// The wheel family is derived, not listed: every class whose name ends in `Wheel` and
// that is a BasePart (CartWheel, MotorWheel, OffRoadWheel, StickyWheel on 2.4.0 BPLE).
const wheelClasses = [...classBases.keys()].filter((name) => name.endsWith("Wheel") && derivesFromBasePart(name)).sort();

// ------------------------------------------------------------------ method bodies
/** Body of `public override Joint CustomConnectToPart(...)` inside a class, or null. */
function readCustomConnectBody(className) {
  const text = readFileSync(join(SCRIPTS, `${className}.cs`), "utf8");
  const signature = /public override Joint CustomConnectToPart\([^)]*\)\s*\r?\n\s*\{/.exec(text);
  if (!signature) return null;
  const start = text.indexOf("{", signature.index);
  let depth = 0;
  for (let index = start; index < text.length; index++) {
    if (text[index] === "{") depth++;
    else if (text[index] === "}") {
      depth--;
      if (depth === 0) return text.slice(start, index + 1);
    }
  }
  fail(`${className}.cs: CustomConnectToPart body is unbalanced`);
}

/** `field = 123f;` declared on the class itself (the serialized default). */
function readFieldDefault(className, field) {
  const text = readFileSync(join(SCRIPTS, `${className}.cs`), "utf8");
  const match = new RegExp(`^\\s*public float ${field} = (-?[0-9.]+)f\\s*;`, "m").exec(text);
  return match ? Number(match[1]) : null;
}

const MOTION = /ConfigurableJointMotion\.(\w+)/;

/** The `ConfigurableJointMotion` token a `field = ConfigurableJointMotion.X;` assignment uses. */
function motionOf(body, className, field) {
  const assignment = new RegExp(`(?<![A-Za-z])${field} = ConfigurableJointMotion\\.\\w+`).exec(body);
  if (!assignment) fail(`${className}: CustomConnectToPart does not set ${field}`);
  return MOTION.exec(assignment[0])[1];
}

/**
 * The suspension a wheel class declares in `CustomConnectToPart`, or null when the class
 * does not override it (BasePart.cs:1206 returns null -> Contraption.AddFixedJoint welds
 * the part rigidly). Throws when an override stops looking like the known shape, so a
 * changed original cannot silently produce wrong content.
 */
function readSuspensionDeclaration(className, body) {
  const angular = ["angularXMotion", "angularYMotion", "angularZMotion"].map((name) => motionOf(body, className, name));
  const linear = ["xMotion", "yMotion", "zMotion"].map((name) => motionOf(body, className, name));

  const springField = /linearLimitSpring\.spring = (\w+)\s*;/.exec(body);
  const damper = /linearLimitSpring\.damper = (-?[0-9.]+)f\s*;/.exec(body);
  const limit = /linearLimit\.limit = (-?[0-9.]+)f\s*;/.exec(body);
  const bounciness = /linearLimit\.bounciness = (-?[0-9.]+)f\s*;/.exec(body);
  if (!springField || !damper || !limit || !bounciness) {
    fail(`${className}: CustomConnectToPart does not look like the known linear-limit spring declaration`);
  }

  const defaultStiffness = readFieldDefault(className, springField[1]);
  if (defaultStiffness === null) {
    fail(`${className}: serialized field ${springField[1]} has no literal default`);
  }

  return {
    stiffnessField: springField[1],
    defaultStiffness,
    damper: Number(damper[1]),
    restOffset: Number(limit[1]),
    bounciness: Number(bounciness[1]),
    angularLocked: angular.every((mode) => mode === "Locked"),
    linearLocked: linear[0] === "Locked" && linear[2] === "Locked",
    linearLimited: linear[1] === "Limited",
    angular,
    linear,
  };
}

// Only the class that declares the suspension needs its declaration parsed; the rest are
// reported as rigid by absence of an override.
const declarations = new Map();
for (const className of wheelClasses) {
  const body = readCustomConnectBody(className);
  declarations.set(className, body === null ? null : readSuspensionDeclaration(className, body));
}

const springClasses = wheelClasses.filter((name) => declarations.get(name) !== null);
if (springClasses.length === 0) {
  fail("no wheel class overrides CustomConnectToPart: the extractor's premise changed");
}

// ------------------------------------------------------------------ prefab scan
const CONTENT = JSON.parse(readFileSync(CONTENT_PARTS, "utf8"));
const MAP = JSON.parse(readFileSync(TEXTURE_MAP, "utf8"));
const nameByPrefab = new Map();
const partByPrefab = new Map();
for (const [section, ids] of [["parts", MAP.parts], ["variants", MAP.variants]]) {
  for (const [id, prefab] of Object.entries(ids ?? {})) {
    if (typeof prefab === "string" && prefab.length > 0) {
      nameByPrefab.set(prefab, Number(id));
      partByPrefab.set(prefab, CONTENT.parts.find((part) => part.partTypeId === Number(id)) ?? null);
    }
  }
}

const readNumber = (text, field) => {
  const match = new RegExp(`^\\s*${field}:\\s*(-?[0-9.]+)\\s*$`, "m").exec(text);
  return match ? Number(match[1]) : null;
};

const prefabFiles = readdirSync(GAMEOBJECT).filter((entry) => entry.startsWith("Part_") && entry.endsWith(".prefab"));
const prefabs = {};
for (const file of prefabFiles.sort()) {
  const name = basename(file, ".prefab");
  const text = readFileSync(join(GAMEOBJECT, file), "utf8");
  const classes = [...new Set([...text.matchAll(/m_Script:\s*\{fileID:\s*-?\d+,\s*guid:\s*([0-9a-f]{32})/g)]
    .map((match) => guidIndex.get(match[1]))
    .filter((value) => value !== undefined))];
  const wheelClass = classes.find((value) => wheelClasses.includes(value)) ?? null;
  const declaration = wheelClass ? declarations.get(wheelClass) : null;

  /** `m_springStiffness: 50` present in the prefab text means the prefab-authored override. */
  const serialized = declaration ? readNumber(text, declaration.stiffnessField) : null;
  prefabs[name] = {
    classes,
    wheelClass,
    springClass: declaration !== null,
    stiffnessSource: declaration === null ? null : (serialized === null ? "class default" : "prefab override"),
    stiffness: declaration === null ? null : (serialized ?? declaration.defaultStiffness),
    damper: declaration?.damper ?? null,
    restOffset: declaration?.restOffset ?? null,
    bounciness: declaration?.bounciness ?? null,
    motion: declaration === null ? null : {
      angular: declaration.angular,
      linear: declaration.linear,
      angularLocked: declaration.angularLocked,
      linearLocked: declaration.linearLocked,
      linearLimited: declaration.linearLimited,
    },
    jointType: readNumber(text, "m_jointType"),
    jointConnectionType: readNumber(text, "m_jointConnectionType"),
    jointConnectionDirection: readNumber(text, "m_jointConnectionDirection"),
    customJointConnectionDirection: readNumber(text, "m_customJointConnectionDirection"),
    jointConnectionStrength: readNumber(text, "m_jointConnectionStrength"),
    jointPreprocessing: readNumber(text, "m_jointPreprocessing"),
    partTypeId: nameByPrefab.get(name) ?? null,
  };
}

// ------------------------------------------------------- content-part projection
const parts = {};
const unmappedSpringPrefabs = [];
for (const [name, entry] of Object.entries(prefabs)) {
  if (entry.partTypeId === null) {
    if (entry.springClass) unmappedSpringPrefabs.push(name);
    continue;
  }

  const part = partByPrefab.get(name);
  parts[entry.partTypeId] = {
    prefab: name,
    name: part?.name ?? "",
    wheelClass: entry.wheelClass,
    suspension: entry.springClass
      ? { stiffness: entry.stiffness, damper: entry.damper, restOffset: entry.restOffset, bounciness: entry.bounciness }
      : null,
    jointConnectionType: entry.jointConnectionType,
    customJointConnectionDirection: entry.customJointConnectionDirection,
    jointConnectionStrength: entry.jointConnectionStrength,
  };
}

// Every wheel-family prefab (mapped or not), so the report states the whole truth about
// "which wheel prefabs reach a wheel connection and what they declare".
const wheelPrefabs = Object.entries(prefabs)
  .filter(([, entry]) => entry.wheelClass !== null)
  .sort((left, right) => left[0].localeCompare(right[0]))
  .map(([name, entry]) => ({
    prefab: name,
    partTypeId: entry.partTypeId,
    wheelClass: entry.wheelClass,
    stiffness: entry.springClass ? entry.stiffness : null,
    stiffnessSource: entry.stiffnessSource,
  }));

// -------------------------------------------------------------- frame welds (context)
// Report-only: what the original's *weld* path declares. The wheels above are the only
// parts whose joint value is a spring; everything else is a FixedJoint/ConfigurableJoint
// whose stiffness comes from Unity's `enablePreprocessing`, which is per-part.
const frameFacts = (() => {
  const preprocessing = Object.entries(prefabs)
    .filter(([, entry]) => entry.jointPreprocessing === 1)
    .map(([name]) => name)
    .sort();
  const strengthDistribution = {};
  for (const entry of Object.values(prefabs)) {
    const key = entry.jointConnectionStrength === null ? "absent" : String(entry.jointConnectionStrength);
    strengthDistribution[key] = (strengthDistribution[key] ?? 0) + 1;
  }

  const contraption = readFileSync(join(SCRIPTS, "Contraption.cs"), "utf8");
  const addFixedJoint = /public void AddFixedJoint\(BasePart part, BasePart other\)/.test(contraption);
  const preprocessingExpression = /joint\.enablePreprocessing = ([^;]+);/.exec(contraption)?.[1]?.trim() ?? null;
  const strengths = {};
  const strengthBlock = /public float GetJointConnectionStrength\(BasePart\.JointConnectionStrength strength\)\s*\{([\s\S]*?)\n\t\}/.exec(contraption);
  if (strengthBlock) {
    for (const match of strengthBlock[1].matchAll(/(\w+) => WPFMonoBehaviour\.gameData\.(m_jointConnectionStrength\w*)/g)) {
      strengths[match[1]] = match[2];
    }
  }

  const frameJointManager = readFileSync(join(SCRIPTS, "FrameJointManager.cs"), "utf8");
  const frameBreak = /breakForce = ([^;]+);/.exec(frameJointManager)?.[1]?.trim() ?? null;

  const gameData = readFileSync(join(ASSETS, "MonoBehaviour", "GameData.asset"), "utf8");
  const strengthValues = {};
  for (const field of new Set(Object.values(strengths))) {
    const match = new RegExp(`^\\s*${field}:\\s*(-?[0-9.]+)\\s*$`, "m").exec(gameData);
    if (match) strengthValues[field] = Number(match[1]);
  }

  const insettingsPath = join(TEXTEXT, "INSettingsBExp.json");
  const insettings = existsSync(insettingsPath)
    ? JSON.parse(readFileSync(insettingsPath, "utf8")).items
    : [];
  const featureValue = (feature) => insettings.find((item) => item.name === feature)?.value ?? null;

  return {
    addFixedJoint,
    enablePreprocessing: preprocessingExpression,
    jointPreprocessingTrue: preprocessing.length,
    jointPreprocessingPrefabs: preprocessing,
    jointConnectionStrengthDistribution: strengthDistribution,
    jointConnectionStrengthFields: strengths,
    jointConnectionStrengthValues: strengthValues,
    frameJointManagerBreakForce: frameBreak,
    springFeatureValues: {
      StrongSpringConnection: featureValue("StrongSpringConnection"),
      StableSpringConnection: featureValue("StableSpringConnection"),
      OffRoadWheel: featureValue("OffRoadWheel"),
    },
  };
})();

const report = {
  bple: BPLE,
  wheelClasses,
  springClasses,
  declarations: Object.fromEntries([...declarations].map(([name, value]) => [name, value === null
    ? { customConnectToPart: false, note: "inherits BasePart.CustomConnectToPart (null) -> Contraption.AddFixedJoint welds it rigidly" }
    : { customConnectToPart: true, ...value, source: `${name}.cs:202-220` }])),
  springPrefabs: Object.entries(prefabs).filter(([, entry]) => entry.springClass).map(([name]) => name),
  unmappedSpringPrefabs,
  wheelPrefabs,
  parts,
  frames: frameFacts,
  warnings,
};
mkdirSync(dirname(OUT_JSON), { recursive: true });
writeFileSync(OUT_JSON, `${JSON.stringify(report, null, 2)}\n`);

const md = [];
md.push("# 原版轮子悬挂报告（`CustomConnectToPart` 的线性限位弹簧）", "");
md.push("来源：`tools/bple-springs/extract-springs.mjs`，扫描 `Assembly-CSharp` 的轮子类与全部 `Part_*.prefab`。", "");
md.push("## 轮子族与 `CustomConnectToPart`", "");
for (const name of wheelClasses) {
  const declaration = declarations.get(name);
  md.push(`- \`${name}\`：${declaration === null ? "未覆写 → `BasePart.cs:1206` 返回 null → `Contraption.AddFixedJoint` 刚性焊接（六轴全 Locked，`enablePreprocessing` 见下）" : `覆写 → 角运动 ${declaration.angular.join("/")}，线运动 ${declaration.linear.join("/")}，弹簧字段 \`${declaration.stiffnessField}\` 默认 ${declaration.defaultStiffness}，damper ${declaration.damper}，limit ${declaration.restOffset}，bounciness ${declaration.bounciness}`}`);
}

md.push("", "## 全量 prefab 里声明了弹簧的（唯一来源）", "");
md.push("| prefab | partTypeId | 类 | stiffness | 来源 |", "|---|---|---|---|---|");
for (const [name, entry] of Object.entries(prefabs).filter(([, value]) => value.springClass).sort()) {
  md.push(`| \`${name}\` | ${entry.partTypeId ?? "未映射"} | \`${entry.wheelClass}\` | ${entry.stiffness} | ${entry.stiffnessSource} |`);
}

md.push("", "## 轮子族 prefab 一览", "");
md.push("| prefab | partTypeId | 类 | stiffness |", "|---|---|---|---|");
for (const entry of wheelPrefabs) {
  md.push(`| \`${entry.prefab}\` | ${entry.partTypeId ?? "未映射"} | \`${entry.wheelClass}\` | ${entry.stiffness ?? "—（无弹簧字段）"} |`);
}

md.push("", "## 内容映射", "");
for (const [id, entry] of Object.entries(parts).sort((left, right) => Number(left[0]) - Number(right[0]))) {
  if (entry.suspension === null) continue;
  md.push(`- \`${id}\` ${entry.name} — \`${entry.prefab}\`：stiffness ${entry.suspension.stiffness}，damper ${entry.suspension.damper}，restOffset ${entry.suspension.restOffset}`);
}

if (unmappedSpringPrefabs.length > 0) {
  md.push("", "## 有弹簧但不在内容里的 prefab", "", unmappedSpringPrefabs.map((name) => `\`${name}\``).join(", "), "");
}

md.push("## 关节刚度背景（只记录，不实现）", "");
md.push(`- \`Contraption.AddFixedJoint\`：\`enablePreprocessing = ${frameFacts.enablePreprocessing}\`（每端一个开关，逐字见 Contraption.cs:1540）。`);
md.push(`- 全量 prefab 中 \`m_jointPreprocessing: 1\` 的有 ${frameFacts.jointPreprocessingTrue} 个：${frameFacts.jointPreprocessingPrefabs.join(", ")}。`);
md.push(`- \`m_jointConnectionStrength\` 分布（原始枚举值）：${JSON.stringify(frameFacts.jointConnectionStrengthDistribution)}。`);
md.push(`- \`GetJointConnectionStrength\` 映射：${JSON.stringify(frameFacts.jointConnectionStrengthFields)} → ${JSON.stringify(frameFacts.jointConnectionStrengthValues)}。`);
md.push(`- \`FrameJointManager\` 框↔框断力：\`${frameFacts.frameJointManagerBreakForce}\`（预处理恒为 true，见 FrameJointManager.cs:172-184）。`);
md.push(`- ` + "`INSettingsBExp.json`：`StrongSpringConnection` = " + `${JSON.stringify(frameFacts.springFeatureValues.StrongSpringConnection)}，` + "`StableSpringConnection` = " + `${JSON.stringify(frameFacts.springFeatureValues.StableSpringConnection)}，` + "`OffRoadWheel` = " + `${JSON.stringify(frameFacts.springFeatureValues.OffRoadWheel)}（越野轮功能开关）。`);
if (warnings.length > 0) {
  md.push("", "## 警告", "");
  for (const warning of warnings) md.push(`- ${warning}`);
}

writeFileSync(OUT_MD, `${md.join("\n")}\n`);

console.log(`wheel classes: ${wheelClasses.join(", ")}`);
console.log(`spring classes: ${springClasses.join(", ")}`);
console.log(`prefabs with a spring: ${Object.entries(prefabs).filter(([, entry]) => entry.springClass).length}`);
for (const [name, entry] of Object.entries(prefabs).filter(([, value]) => value.springClass && value.partTypeId !== null).sort()) {
  console.log(`  mapped ${entry.partTypeId} (${partByPrefab.get(name)?.name}) <- ${name}: stiffness ${entry.stiffness} (${entry.stiffnessSource})`);
}
console.log(`unmapped spring prefabs: ${unmappedSpringPrefabs.join(", ") || "-"}`);
console.log(`wheel prefabs: ${wheelPrefabs.length}; content parts carrying a suspension: ${Object.values(parts).filter((entry) => entry.suspension !== null).length}`);
console.log(`report: ${OUT_JSON}\n        ${OUT_MD}`);
