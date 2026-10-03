// Build-grid cell box extractor: reads each part prefab's `m_gridXmin` / `m_gridXmax` /
// `m_gridYmin` / `m_gridYmax` and reports the inclusive cell rectangle per PigForge partTypeId.
// This is the ONLY admissible source for the cell box in content/parts.json -- the same rule
// tools/bple-joints established for joint capability and tools/bple-materials for materials.
//
// Why it matters: PigForge used to derive a part's occupied cells from the union of its collider
// shapes. The original never does that -- it occupies the cells the prefab declares, and the
// collider overhangs its neighbours freely. A rotor's blades are 2.55 cells wide while the rotor
// still occupies one cell, so the old rule made a rotor unplaceable next to a wooden frame
// (`CellsOccupied`, error 6). The prefab fields are the original's own account:
//
//   ConstructionUI.cs:1279   num + part.m_gridXmin <= coordX && num + part.m_gridXmax >= coordX
//                            && num2 + part.m_gridYmin <= coordY && num2 + part.m_gridYmax >= coordY
//   ConstructionUI.cs:1344   for (int j = part.m_gridXmin; j <= part.m_gridXmax; j++)
//   BasePart.cs:197-200      the four serialized fields; a part's grid coordinate is its own cell
//                            (GridPositionToWorldPosition: contraption.position + right*x + up*y),
//                            so cell (0,0) is the one the part stands in and the box is inclusive.
//
// Measured on BPLE_Unity6 (343 Part_*.prefab): 332 declare one cell at the origin (0..0, 0..0) --
// every propeller, fan, wing, spotlight, grapple, spring and pig -- and 11 declare the 3x2 box
// x[-1, 1] y[0, 1]: the KingPig_01..07 and GoldenPig_01..04 families. Zero prefabs miss a field.
// The histogram is a hard invariant: drift fails this tool instead of silently writing a
// different world into content/parts.json.
//
// Usage: node tools/bple-grid/extract-grid.mjs [--bple <path>] [--json <path>] [--md <path>]
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
const OUT_JSON = resolve(arg("json", join(REPO, "tasks", "bple-grid-report.json")));
const OUT_MD = resolve(arg("md", join(REPO, "tasks", "bple-grid-report.md")));
const GAMEOBJECT = join(BPLE, "Assets", "GameObject");
const CONTENT_PARTS = join(REPO, "content", "parts.json");
const TEXTURE_MAP = join(REPO, "tools", "bple-textures", "part-map.json");

if (!existsSync(GAMEOBJECT)) {
  console.error(`BPLE project not found: ${GAMEOBJECT}\nPass --bple <path to BPLE_Unity6>.`);
  process.exit(1);
}

// The original's own default, spelled out so every reader shares one vocabulary: a part that
// declares the box 0..0 / 0..0 occupies exactly the cell it stands in.
const DEFAULT_BOX = { minX: 0, maxX: 0, minY: 0, maxY: 0 };

const warnings = [];

function prefabText(name) {
  const path = join(GAMEOBJECT, `${name}.prefab`);
  return existsSync(path) ? readFileSync(path, "utf8") : null;
}

/** The four serialized grid fields (BasePart.cs:197-200). Every part prefab carries all four on
 * the BasePart MonoBehaviour; a missing one is a hard error, not a silent default. */
function readGridBox(text) {
  const field = (name) => {
    const match = new RegExp(`^\\s*m_${name}:\\s*(-?\\d+)\\s*$`, "m").exec(text);
    return match ? Number(match[1]) : null;
  };

  const minX = field("gridXmin");
  const maxX = field("gridXmax");
  const minY = field("gridYmin");
  const maxY = field("gridYmax");
  if (minX === null || maxX === null || minY === null || maxY === null) {
    return null;
  }

  return { minX, maxX, minY, maxY };
}

const boxKey = (box) => `${box.minX},${box.maxX},${box.minY},${box.maxY}`;
const sameBox = (left, right) =>
  left.minX === right.minX && left.maxX === right.maxX && left.minY === right.minY && left.maxY === right.maxY;

/** partTypeId -> prefab name, reusing the mapping the shapes/textures/joints extractors
 * established so this tool cannot drift into a second convention. */
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

  const gridBox = readGridBox(text);
  if (gridBox === null) {
    warnings.push(`part ${part.partTypeId} (${prefab}): m_gridXmin/m_gridXmax/m_gridYmin/m_gridYmax absent`);
    continue;
  }

  parts[part.partTypeId] = {
    prefab,
    name: nameByPart.get(part.partTypeId) ?? "",
    gridBox,
    isDefault: sameBox(gridBox, DEFAULT_BOX),
  };
}

// Whole-project scan: the histogram is the fingerprint, the per-prefab boxes are the payload the
// applier reads back.
const prefabScan = { count: 0, histogram: {}, missing: [], nonDefault: [], parts: {} };
for (const entry of readdirSync(GAMEOBJECT)) {
  if (!entry.startsWith("Part_") || !entry.endsWith(".prefab")) {
    continue;
  }

  const text = readFileSync(join(GAMEOBJECT, entry), "utf8");
  const gridBox = readGridBox(text);
  prefabScan.count += 1;
  if (gridBox === null) {
    prefabScan.missing.push(entry);
    prefabScan.histogram.absent = (prefabScan.histogram.absent ?? 0) + 1;
    prefabScan.parts[entry] = null;
    continue;
  }

  const key = boxKey(gridBox);
  prefabScan.histogram[key] = (prefabScan.histogram[key] ?? 0) + 1;
  prefabScan.parts[entry] = [gridBox.minX, gridBox.maxX, gridBox.minY, gridBox.maxY];
  if (!sameBox(gridBox, DEFAULT_BOX)) {
    prefabScan.nonDefault.push(entry);
  }
}

// Hard invariants: measured on BPLE_Unity6 -- 343 prefabs, 332 one-cell-at-origin and 11 at
// x[-1, 1] y[0, 1], and those 11 are exactly the KingPig/GoldenPig families. Drift fails the
// tool instead of silently writing a different world into content/parts.json.
const invariants = [];
if (prefabScan.count !== 343) {
  invariants.push(`prefab count: expected 343, got ${prefabScan.count}`);
}

if (prefabScan.missing.length > 0) {
  invariants.push(`prefabs missing a grid field: ${prefabScan.missing.length} (${prefabScan.missing.slice(0, 5).join(", ")})`);
}

const expectedHistogram = { "0,0,0,0": 332, "-1,1,0,1": 11 };
const histogramKeys = new Set([...Object.keys(prefabScan.histogram), ...Object.keys(expectedHistogram)]);
for (const key of histogramKeys) {
  if ((prefabScan.histogram[key] ?? 0) !== (expectedHistogram[key] ?? 0)) {
    invariants.push(`grid box histogram: expected ${JSON.stringify(expectedHistogram)}, got ${JSON.stringify(prefabScan.histogram)}`);
    break;
  }
}

if (prefabScan.nonDefault.length !== 11) {
  invariants.push(`non-default grid boxes: expected 11, got ${prefabScan.nonDefault.length}`);
}

const misfits = prefabScan.nonDefault.filter((name) => !/^Part_(KingPig|GoldenPig)_/.test(name));
if (misfits.length > 0) {
  invariants.push(`non-default grid box outside the KingPig/GoldenPig families: ${misfits.join(", ")}`);
}

if (invariants.length > 0) {
  console.error("extract-grid: source-tree invariants changed:");
  for (const invariant of invariants) {
    console.error(`  - ${invariant}`);
  }

  process.exit(1);
}

const report = { bple: BPLE, default: DEFAULT_BOX, prefabScan, warnings, unmapped, parts };
mkdirSync(dirname(OUT_JSON), { recursive: true });
writeFileSync(OUT_JSON, `${JSON.stringify(report, null, 2)}\n`);

const nonDefaultParts = Object.entries(parts)
  .filter(([, value]) => !value.isDefault)
  .sort((left, right) => Number(left[0]) - Number(right[0]));

const md = [];
md.push("# 原版建造格盒报告（`m_gridXmin/m_gridXmax/m_gridYmin/m_gridYmax`）", "");
md.push("来源：`tools/bple-grid/extract-grid.mjs`，扫描原版全部 `Part_*.prefab`。");
md.push("规则出处：`BasePart.cs:197-200`（四个序列化字段）、`ConstructionUI.cs:1279,1344`（按格盒清占位）、");
md.push("`ConstructionUI.GridPositionToWorldPosition`（零件的网格坐标就是它自己所在的那一格，故格盒含两端）。", "");
md.push("**默认值**：格盒缺省即 `0..0, 0..0` —— 只占零件自己所在的那一格（原版 343 件里 332 件如此）。");
md.push("占格（`CellsOccupied`/`Overlaps`）只用格盒；碰撞体伸到邻格不占格，这正是原版行为。", "");
md.push("## 全量分布（原版 prefab 计数）", "");
md.push("| 格盒 `minX,maxX,minY,maxY` | 数量 |", "|---|---|");
for (const [key, count] of Object.entries(prefabScan.histogram).sort((a, b) => b[1] - a[1])) {
  md.push(`| \`${key}\` | ${count} |`);
}

md.push("", "## 非默认格盒的 prefab（3×2 王猪/金猪族）", "");
for (const name of prefabScan.nonDefault) {
  md.push(`- \`${name}\` — ${boxKey({ minX: prefabScan.parts[name][0], maxX: prefabScan.parts[name][1], minY: prefabScan.parts[name][2], maxY: prefabScan.parts[name][3] })}`);
}

md.push("", `## PigForge 内容里非默认格盒的部件（${nonDefaultParts.length} 条）`, "");
for (const [partTypeId, value] of nonDefaultParts) {
  md.push(`- \`${partTypeId}\`（variantOf ${content.parts.find((part) => part.partTypeId === Number(partTypeId))?.variantOf ?? "-"}） — \`${value.prefab}\`，格盒 \`${boxKey(value.gridBox)}\``);
}

md.push("", `其余 ${Object.keys(parts).length - nonDefaultParts.length} 个映射件都是默认单格，内容里不写字段。`, "");

if (unmapped.length > 0) {
  md.push("## 未映射到 prefab 的 partTypeId（静态关卡几何，永不进入建造占格）", "");
  for (const partTypeId of unmapped) {
    md.push(`- \`${partTypeId}\` ${nameByPart.get(partTypeId) ?? ""}`);
  }

  md.push("");
}

if (warnings.length > 0) {
  md.push("## 警告", "");
  for (const warning of warnings) {
    md.push(`- ${warning}`);
  }

  md.push("");
}

writeFileSync(OUT_MD, `${md.join("\n")}\n`);

console.log(`prefabs: ${prefabScan.count} (${JSON.stringify(prefabScan.histogram)})`);
console.log(`non-default: ${prefabScan.nonDefault.length} (${prefabScan.nonDefault.join(", ")})`);
console.log(`content parts with a non-default box: ${nonDefaultParts.length} / ${Object.keys(parts).length} mapped`);
if (unmapped.length > 0) {
  console.log(`unmapped partTypeIds: ${unmapped.length} (${unmapped.join(", ")})`);
}

if (warnings.length > 0) {
  console.log(`warnings: ${warnings.length}`);
}

console.log(`report: ${OUT_JSON}\n        ${OUT_MD}`);
