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
// Usage: node tools/bple-joints/extract-joints.mjs [--bple <path>] [--json <path>] [--md <path>]
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
const OUT_JSON = resolve(arg("json", join(REPO, "tasks", "bple-joints-report.json")));
const OUT_MD = resolve(arg("md", join(REPO, "tasks", "bple-joints-report.md")));
const GAMEOBJECT = join(BPLE, "Assets", "GameObject");
const CONTENT_PARTS = join(REPO, "content", "parts.json");
const TEXTURE_MAP = join(REPO, "tools", "bple-textures", "part-map.json");

if (!existsSync(GAMEOBJECT)) {
  console.error(`BPLE project not found: ${GAMEOBJECT}\nPass --bple <path to BPLE_Unity6>.`);
  process.exit(1);
}

// Unity's enum, spelled out so a report reader does not have to decode integers.
const JOINT_TYPES = { 0: "none", 1: "source", 2: "target" };

const warnings = [];

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

  parts[part.partTypeId] = {
    prefab,
    name: nameByPart.get(part.partTypeId) ?? "",
    jointType: joint.name,
    rawJointType: joint.raw,
    jointStrength: readJointStrength(text),
  };
}

// Whole-project tally, so the report can state the distribution even for prefabs that never made
// it into content (dropped inventions, unimported parts).
const distribution = {};
for (const entry of readdirSync(GAMEOBJECT)) {
  if (!entry.startsWith("Part_") || !entry.endsWith(".prefab")) {
    continue;
  }

  const text = readFileSync(join(GAMEOBJECT, entry), "utf8");
  const joint = readJointType(text);
  const key = joint ? joint.name : "absent";
  distribution[key] = (distribution[key] ?? 0) + 1;
}

const report = { bple: BPLE, distribution, warnings, unmapped, parts };
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
    const strength = value.jointStrength === null ? "?" : value.jointStrength;
    md.push(`- \`${partTypeId}\` ${value.name} — \`${value.prefab}\`，强度 ${strength}`);
  }

  md.push("");
}

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
