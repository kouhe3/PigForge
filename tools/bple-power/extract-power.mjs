// Power capability extractor: reads each part prefab's `m_powerConsumption` and
// `m_enginePower` and reports the values per PigForge partTypeId. This is the ONLY
// admissible source for power data in content/parts.json -- the same rule
// tools/bple-joints and tools/bple-materials established for their values.
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
import { existsSync, mkdirSync, readFileSync, readdirSync, writeFileSync } from "node:fs";
import { dirname, join, resolve } from "node:path";
import { fileURLToPath } from "node:url";

const HERE = dirname(fileURLToPath(import.meta.url));
const REPO = resolve(HERE, "..", "..");

function arg(name, fallback) {
  const index = process.argv.indexOf(`--${name}`);
  return index >= 0 && process.argv[index + 1] ? process.argv[index + 1] : fallback;
}

const BPLE = resolve(arg("bple", process.env.BPLE_ROOT ?? join(REPO, "..", "BPLE_Unity6")));
const OUT_JSON = resolve(arg("json", join(REPO, "tasks", "bple-power-report.json")));
const OUT_MD = resolve(arg("md", join(REPO, "tasks", "bple-power-report.md")));
const GAMEOBJECT = join(BPLE, "Assets", "GameObject");
const CONTENT_PARTS = join(REPO, "content", "parts.json");
const TEXTURE_MAP = join(REPO, "tools", "bple-textures", "part-map.json");

if (!existsSync(GAMEOBJECT)) {
  console.error(`BPLE project not found: ${GAMEOBJECT}\nPass --bple <path to BPLE_Unity6>.`);
  process.exit(1);
}

const warnings = [];

function prefabText(name) {
  const path = join(GAMEOBJECT, `${name}.prefab`);
  return existsSync(path) ? readFileSync(path, "utf8") : null;
}

/** The first `m_powerConsumption`/`m_enginePower` in the file. Every part prefab carries
 * exactly one of each, on the BasePart MonoBehaviour that owns the part's physics behaviour
 * (BasePart.cs:162,164; the template assigns them at BasePart.cs:1445-1446). */
function readPowerField(text, field) {
  const match = new RegExp(`^\\s*${field}:\\s*(-?[0-9.]+)\\s*$`, "m").exec(text);
  if (!match) {
    return null;
  }

  const value = Number(match[1]);
  return Number.isFinite(value) ? value : null;
}

function readPower(text) {
  const powerConsumption = readPowerField(text, "m_powerConsumption");
  const enginePower = readPowerField(text, "m_enginePower");
  if (powerConsumption === null || enginePower === null) {
    return null;
  }

  return { powerConsumption, enginePower };
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

  const power = readPower(text);
  if (power === null) {
    warnings.push(`part ${part.partTypeId} (${prefab}): m_powerConsumption/m_enginePower absent`);
    continue;
  }

  parts[part.partTypeId] = {
    prefab,
    name: nameByPart.get(part.partTypeId) ?? "",
    powerConsumption: power.powerConsumption,
    enginePower: power.enginePower,
    // The original's own predicates (BasePart.cs:601-608).
    powered: power.powerConsumption > 0,
    engine: power.enginePower > 0,
  };
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
mkdirSync(dirname(OUT_JSON), { recursive: true });
writeFileSync(OUT_JSON, `${JSON.stringify(report, null, 2)}\n`);

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

writeFileSync(OUT_MD, `${md.join("\n")}\n`);

console.log(`prefabs: ${Object.values(distribution.classification).reduce((sum, count) => sum + count, 0)} (${JSON.stringify(distribution.classification)})`);
console.log(`powerConsumption: ${JSON.stringify(distribution.powerConsumption)}`);
console.log(`enginePower: ${JSON.stringify(distribution.enginePower)}`);
for (const field of ["enginePower", "powerConsumption"]) {
  const rows = byField(field);
  console.log(`${field.padEnd(16)} parts=${String(rows.length).padStart(3)}  ${rows.map(([id, value]) => `${id}:${value.name}`).join(", ")}`);
}

if (unmapped.length > 0) {
  console.log(`unmapped partTypeIds: ${unmapped.join(", ")}`);
}

if (warnings.length > 0) {
  console.log(`warnings: ${warnings.length}`);
}

console.log(`report: ${OUT_JSON}\n        ${OUT_MD}`);
