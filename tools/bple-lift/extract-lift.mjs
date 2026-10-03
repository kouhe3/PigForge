// Balloon lift extractor: reads the original balloon prefabs' `m_force` and the `BalloonForce`
// feature value, and reports the per-tick lift impulse PigForge content has to carry. This is the
// ONLY admissible source for `capabilities.balloon` and the balloon family's mass.
//
// Original truth (Unity):
//   - Balloon.cs:7          `m_force` default 10f (serialized 11.5 in every Part_Balloon* prefab)
//   - Balloon.cs:181        `m_force *= INSettings.GetFloat(INFeature.BalloonForce)` -> 11.5 * 2.0 = 23 N
//   - Balloon.cs:207-210    `FixedUpdate` does `rigidbody.AddForce(m_force * m_direction, ForceMode.Force)`
//                           on the balloon's OWN body, every FixedUpdate (Unity's default 0.02 s here
//                           is irrelevant to us: our room ticks at 60 Hz, see below)
//   - Balloon.cs:129-132    the runtime body rewrite: `mass = 0.1f`, `linearDamping = 2f`,
//                           `angularDamping = 0.5f`, `constraints = (RigidbodyConstraints)48`
//   - Balloon.cs:112-120    `m_numberOfBalloons > 1` clones the part, so a double/triple prefab is
//                           N independent 0.1 kg balloons, each with its own 23 N
//
// PigForge applies one impulse per tick to the balloon body (`GameplayRules.RunBalloons`), so a
// constant force F becomes `F * dt` with `dt = 1 / tickRate`; the room ticks at 60 Hz
// (`GameRoomOptions.Create` default, and `PlayHost` does not override it). The content therefore
// carries `force / 60` per stacked balloon, which makes the body's acceleration the original's
// `force / mass = 23 / 0.1 = 230 m/s^2`.
//
// Usage: node tools/bple-lift/extract-lift.mjs [--bple <path>] [--json <path>] [--md <path>]
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
const OUT_JSON = resolve(arg("json", join(REPO, "tasks", "bple-lift-report.json")));
const OUT_MD = resolve(arg("md", join(REPO, "tasks", "bple-lift-report.md")));
const GAMEOBJECT = join(BPLE, "Assets", "GameObject");
const INSETTINGS = join(BPLE, "Assets", "TextAsset", "INSettingsBExp.json");
const CONTENT_PARTS = join(REPO, "content", "parts.json");
const TEXTURE_MAP = join(REPO, "tools", "bple-textures", "part-map.json");

if (!existsSync(GAMEOBJECT)) {
  console.error(`original GameObject folder not found: ${GAMEOBJECT}`);
  process.exit(1);
}

// The room's fixed tick rate. The content field is "per tick", so the force has to be divided by
// this to become an impulse; changing the room's tick rate changes the conversion, not the force.
const TICK_RATE = 60;
// Balloon.cs:129 `base.rigidbody.mass = 0.1f;` -- the runtime rewrite, not the prefab's `m_mass` (0).
const RUNTIME_MASS = 0.1;
const TICK_RATE_SOURCE = "GameRoomOptions.Create default 60 Hz";
const MASS_SOURCE = "Balloon.cs:129";
const FORCE_SOURCE = "Balloon.cs:181 (m_force * BalloonForce)";

const warnings = [];

function prefabText(name) {
  const path = join(GAMEOBJECT, `${name}.prefab`);
  return existsSync(path) ? readFileSync(path, "utf8") : null;
}

/** Serialized `m_force` of the Balloon component. Every Part_Balloon* prefab carries 11.5. */
function readForce(text) {
  const match = /^\s*m_force:\s*(-?[0-9.]+)\s*$/m.exec(text);
  return match ? Number(match[1]) : null;
}

/** Serialized `m_numberOfBalloons`: how many 0.1 kg balloons this one prefab spawns. */
function readStack(text) {
  const match = /^\s*m_numberOfBalloons:\s*(-?\d+)\s*$/m.exec(text);
  return match ? Number(match[1]) : null;
}

/** `INSettings.GetFloat(INFeature.BalloonForce)` -- the global multiplier Balloon.cs:181 applies. */
function readBalloonForce() {
  const settings = JSON.parse(readFileSync(INSETTINGS, "utf8"));
  const entries = settings.items;
  const entry = entries.find((candidate) => candidate.name === "BalloonForce");
  if (!entry) {
    warnings.push("INSettingsBExp.json has no BalloonForce entry");
    return null;
  }

  return Number(entry.value);
}

/** partTypeId -> prefab name, reusing the mapping the shapes/textures extractors established so
 * this tool cannot drift into a second convention. */
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

const balloonForce = readBalloonForce();
const assignments = loadAssignments();
const content = JSON.parse(readFileSync(CONTENT_PARTS, "utf8"));

const parts = {};
const unmapped = [];
for (const part of content.parts) {
  const prefab = assignments.get(part.partTypeId) ?? null;
  if (!prefab) {
    unmapped.push(part.partTypeId);
    continue;
  }

  if (!/^Part_Balloons?\d*_/.test(prefab)) {
    continue;
  }

  const text = prefabText(prefab);
  if (text === null) {
    warnings.push(`part ${part.partTypeId} (${prefab}): prefab file missing`);
    continue;
  }

  const force = readForce(text);
  const stack = readStack(text);
  if (force === null || stack === null || balloonForce === null) {
    warnings.push(`part ${part.partTypeId} (${prefab}): m_force / m_numberOfBalloons / BalloonForce missing`);
    continue;
  }

  const forcePerBalloon = force * balloonForce;
  parts[part.partTypeId] = {
    prefab,
    name: part.name ?? "",
    stack,
    forcePerBalloon,
    balloonForce,
    tickRate: TICK_RATE,
    runtimeMassPerBalloon: RUNTIME_MASS,
    mass: Number((RUNTIME_MASS * stack).toFixed(6)),
    liftPerTick: Number(((forcePerBalloon * stack) / TICK_RATE).toFixed(6)),
  };
}

const prefabCount = readdirSync(GAMEOBJECT).filter(
  (entry) => entry.startsWith("Part_") && entry.endsWith(".prefab") && /^Part_Balloons?\d*_/.test(entry),
).length;

const report = {
  bple: BPLE,
  balloonForce,
  tickRate: TICK_RATE,
  sources: { tickRate: TICK_RATE_SOURCE, mass: MASS_SOURCE, force: FORCE_SOURCE },
  prefabs: prefabCount,
  warnings,
  unmapped,
  parts,
};
mkdirSync(dirname(OUT_JSON), { recursive: true });
writeFileSync(OUT_JSON, `${JSON.stringify(report, null, 2)}\n`);

const md = [];
md.push("# 原版气球升力报告（`m_force` × `BalloonForce`）", "");
md.push("来源：`tools/bple-lift/extract-lift.mjs`，读取原版 `Part_Balloon*` prefab 的 `m_force`、`m_numberOfBalloons`");
md.push("与 `INSettingsBExp.json` 的 `BalloonForce`。", "");
md.push("原版真值：", "");
md.push("- `Balloon.cs:7` `m_force` 默认 10，prefab 序列化 11.5；`Balloon.cs:181` 乘 `BalloonForce`。", "");
md.push(`- 实测 feature 值：\`BalloonForce = ${balloonForce}\` → 单个气球 ${Number((11.5 * (balloonForce ?? 0)).toFixed(4))} N。`, "");
md.push("- `Balloon.cs:207-210` `FixedUpdate` 对气球**自身刚体**执行 `AddForce(force * up, ForceMode.Force)`。", "");
md.push(`- 容器每 tick 施加一次冲量，故内容值是 \`力 / ${TICK_RATE}\`（` + "`" + TICK_RATE_SOURCE + "`）。", "");
md.push("- `Balloon.cs:129` 运行时把气球刚体质量改写为 0.1（prefab 的 `m_mass` 是 0），`Balloon.cs:112-120` 多气球是 N 个独立 0.1 kg 刚体。", "");
md.push("", "| partTypeId | prefab | 叠加数 | 每气球力 (N) | 总力 (N) | 质量 (kg) | balloonLiftPerTick |", "|---|---|---|---|---|---|---|");
for (const [partTypeId, entry] of Object.entries(parts).sort((a, b) => Number(a[0]) - Number(b[0]))) {
  md.push(
    `| ${partTypeId} | \`${entry.prefab}\` | ${entry.stack} | ${entry.forcePerBalloon} | ${Number((entry.forcePerBalloon * entry.stack).toFixed(4))} | ${entry.mass} | ${entry.liftPerTick} |`,
  );
}

if (unmapped.length > 0) {
  md.push("", "## 未映射到 prefab 的 partTypeId", "", unmapped.join(", "), "");
}

if (warnings.length > 0) {
  md.push("", "## 警告", "");
  for (const warning of warnings) {
    md.push(`- ${warning}`);
  }
}

writeFileSync(OUT_MD, `${md.join("\n")}\n`);

console.log(`balloonForce: ${balloonForce}; balloon-family parts: ${Object.keys(parts).length}; balloon prefabs: ${prefabCount}`);
if (warnings.length > 0) {
  console.log(`warnings: ${warnings.length}`);
}

console.log(`report: ${OUT_JSON}\n        ${OUT_MD}`);
