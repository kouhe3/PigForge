#!/usr/bin/env node
// TNT extractor: the blast every charge prefab carries, and the class rules that decide what its
// blast does. This is the ONLY admissible source for the numbers `content/parts.json`'s `tnt`
// capability used to leave out (its radius, impulse and impact trigger were PigForge constants:
// `GameplayConfig.TntBlastRadius` = 4, `TntBlastImpulse` = 25, `TntIgniteImpactSpeed` = 5).
//
// The original's blast (`TNT.cs:123-183`, `TNTBox` shares the same helper):
//
//   OverlapSphere(transform.position, m_explosionRadius * IN TNTExplosionRadius)
//   per collider: FindParentWithRigidBody -> AddExplosionForce(go, IN TNTExplosionForce / colliderCount)
//   AddExplosionForce (`TNT.cs:211-233`):
//       d      = max(|target - charge|, 1)
//       impulse= forceFactor * m_explosionImpulse / d^1.5          (NOT a linear falloff)
//       mass < 0.1 -> * mass ;  mass < 0.4 -> * mass / 0.4
//       a pig target also takes * 1.15 (`PrepareForTNT`)
//   then every `TNT` the sphere found whose class is NOT `AlienTNT` and which has no generator
//   reference is exploded (`:152-156`); `BlasterTNT` within 2 units goes through ExplodeSpecial.
//   A plain charge then removes itself (`RemovePart`), an `AlienTNT` does not: its own
//   `Explode` (`AlienTNT.cs:8-52`) leaves the part in the world and `LateUpdate` clears
//   `m_triggered`, so it re-arms -- `AlienTNTExplosionCoolingTime` is 0.0 in the vanilla settings.
//   `AlienTNT.OnCollisionEnter` is empty: an alien charge never fires on impact.
//
// So the extractor writes, per part:
//   radius        m_explosionRadius * IN <class>ExplosionRadius   (vanilla 1.0 -> the prefab value)
//   impulse       m_explosionImpulse * IN <class>ExplosionForce   (vanilla 1.0)
//   triggerSpeed  m_triggerSpeed (the impact speed that arms it)
//   repeatable    class is AlienTNT
//   chainTarget   class is not AlienTNT (only those may be chained by another charge)
//   igniteOnImpact class has a collision handler (everything but AlienTNT)
// and asserts the IN multipliers are the vanilla 1.0, the census (7 parts: 5 TNT skins, the
// TimeBomb and the alien) and the histograms {impulse 25:7} {radius 8:7} {trigger 4:7}.
//
// Usage: node tools/bple-tnt/extract-tnt.mjs [--bple <path>] [--json <path>] [--md <path>]
import { readFileSync } from "node:fs";
import { basename } from "node:path";
import { CONTENT, bpleProject, gameObjects, reportJson, reportMd, scriptAssembly } from "../lib/paths.mjs";
import { checks, fail, writeJsonArtifact, writeMarkdownArtifact } from "../lib/report.mjs";
import { assignments, blockWith, buildGuidIndex, prefabText } from "../lib/unity.mjs";
import { loadVanillaSettings, VANILLA_SETTINGS_NAME } from "../in-settings/vanilla-settings.mjs";

const BPLE = bpleProject("BPLE 2022.1.9");
const OUT_JSON = reportJson("tnt");
const OUT_MD = reportMd("tnt");

/** The component classes a charge prefab may carry, each with its IN multiplier keys. */
const CHARGE_CLASSES = {
  TNT: { radiusKey: "TNTExplosionRadius", forceKey: "TNTExplosionForce" },
  TimeBomb: { radiusKey: "TNTExplosionRadius", forceKey: "TNTExplosionForce" },
  AlienTNT: { radiusKey: "AlienTNTExplosionRadius", forceKey: "AlienTNTExplosionForce" },
};

const guidToClass = buildGuidIndex(scriptAssembly(BPLE));
const objects = gameObjects(BPLE);
const settings = loadVanillaSettings(BPLE);
const content = JSON.parse(readFileSync(CONTENT, "utf8"));
const byPart = assignments();
const classOf = (block) =>
  guidToClass.get(/m_Script: \{fileID: \d+, guid: ([0-9a-f]{32})/.exec(block)?.[1]);

const fieldOf = (block, name) => {
  const match = new RegExp(`^\\s*${name}:\\s*(-?[0-9.eE+-]+)\\s*$`, "m").exec(block);
  return match === null ? null : Number(match[1]);
};

const parts = {};
const classHistogram = {};
const impulseHistogram = {};
const radiusHistogram = {};
const triggerHistogram = {};
const check = checks();

for (const part of content.parts) {
  const tnt = part.capabilities?.tnt;
  if (tnt === undefined) {
    continue;
  }

  const prefab = byPart.get(part.partTypeId);
  if (prefab === undefined) {
    fail(`part ${part.partTypeId} (${part.name}) carries tnt but maps to no prefab`);
  }

  const name = basename(prefab).replace(/\.prefab$/i, "");
  const text = prefabText(objects, name);
  if (text === null) {
    fail(`part ${part.partTypeId} (${part.name}): no prefab at ${prefab}`);
  }

  const blocks = text.split(/\n--- !u!/).slice(1);
  const charge = blocks.find((block) => CHARGE_CLASSES[classOf(block)] !== undefined);
  if (charge === undefined) {
    fail(`part ${part.partTypeId} (${part.name}): ${name}.prefab carries no TNT/TimeBomb/AlienTNT component`);
  }

  const className = classOf(charge);
  const keys = CHARGE_CLASSES[className];
  const radiusMultiplier = settings.getFloat(keys.radiusKey);
  const forceMultiplier = settings.getFloat(keys.forceKey);
  const radius = fieldOf(charge, "m_explosionRadius");
  const impulse = fieldOf(charge, "m_explosionImpulse");
  const triggerSpeed = fieldOf(charge, "m_triggerSpeed");
  if (radius === null || impulse === null || triggerSpeed === null) {
    fail(`part ${part.partTypeId} (${part.name}): ${className} without m_explosionRadius/m_explosionImpulse/m_triggerSpeed`);
  }

  parts[part.partTypeId] = {
    name: part.name,
    prefab,
    className,
    fuseTicks: tnt.fuseTicks ?? null,
    radius: radius * radiusMultiplier,
    impulse: impulse * forceMultiplier,
    triggerSpeed,
    repeatable: className === "AlienTNT",
    chainTarget: className !== "AlienTNT",
    igniteOnImpact: className !== "AlienTNT",
    settings: { [keys.radiusKey]: radiusMultiplier, [keys.forceKey]: forceMultiplier },
  };

  classHistogram[className] = (classHistogram[className] ?? 0) + 1;
  impulseHistogram[impulse] = (impulseHistogram[impulse] ?? 0) + 1;
  radiusHistogram[radius] = (radiusHistogram[radius] ?? 0) + 1;
  triggerHistogram[triggerSpeed] = (triggerHistogram[triggerSpeed] ?? 0) + 1;

  // The content already declares its own fuse and (for the alien) its own impact flag; the class
  // decides both, so a mismatch means the hand-authored block drifted from the prefab.
  check.equal(`part ${part.partTypeId} igniteOnImpact`, tnt.igniteOnImpact ?? true, className !== "AlienTNT");
}

// `TNT_Box`-style crates are props, not parts, and carry the same class: `TNTBox` is not in the
// content at all, so the census below only ever covers real parts.
check.equal("parts with a tnt capability", Object.keys(parts).length, 7);
check.equal(
  "charge classes",
  JSON.stringify(Object.entries(classHistogram).sort(([left], [right]) => (left < right ? -1 : 1))),
  JSON.stringify([["AlienTNT", 1], ["TNT", 5], ["TimeBomb", 1]]),
);
check.histogram("m_explosionImpulse", impulseHistogram, [[25, 7]]);
check.histogram("m_explosionRadius", radiusHistogram, [[8, 7]]);
check.histogram("m_triggerSpeed", triggerHistogram, [[4, 7]]);
// The vanilla declaration profile is the baseline (`docs/specs/in-settings-profiles.md`): every
// multiplier this tool folds in must be the identity, or the numbers above stop being the prefab's.
for (const [key, value] of [
  ["TNTExplosionRadius", settings.getFloat("TNTExplosionRadius")],
  ["TNTExplosionForce", settings.getFloat("TNTExplosionForce")],
  ["AlienTNTExplosionRadius", settings.getFloat("AlienTNTExplosionRadius")],
  ["AlienTNTExplosionForce", settings.getFloat("AlienTNTExplosionForce")],
  ["AlienTNTExplosionCoolingTime", settings.getFloat("AlienTNTExplosionCoolingTime")],
  ["BlasterTNT", settings.getBool("BlasterTNT") ? 1 : 0],
]) {
  check.equal(`${VANILLA_SETTINGS_NAME} ${key}`, value, key.endsWith("CoolingTime") || key === "BlasterTNT" ? 0 : 1);
}

if (check.failures.length > 0) {
  fail(check.failures.join("\n"));
}

const report = {
  format: "pigforge.bple-tnt.report",
  schemaVersion: 1,
  bple: BPLE,
  settings: VANILLA_SETTINGS_NAME,
  census: {
    parts: Object.keys(parts).length,
    classes: classHistogram,
    impulse: impulseHistogram,
    radius: radiusHistogram,
    triggerSpeed: triggerHistogram,
  },
  parts,
};
writeJsonArtifact(OUT_JSON, report);

writeMarkdownArtifact(OUT_MD, [
  "# 炸药族（TNT / TimeBomb / AlienTNT）实参",
  "",
  `源：\`${BPLE}\`，设定档 \`${VANILLA_SETTINGS_NAME}\`（声明默认 = vanilla，所有倍率 1.0）。`,
  "",
  "| part | 类 | prefab | impulse | radius | triggerSpeed | 炸后留在世界 | 可被连锁 | 撞击点火 |",
  "|---|---|---|---|---|---|---|---|---|",
  ...Object.entries(parts).map(
    ([id, p]) =>
      `| ${id} ${p.name} | \`${p.className}\` | \`${basename(p.prefab)}\` | ${p.impulse} | ${p.radius} | ${p.triggerSpeed} | ${p.repeatable ? "是" : "否"} | ${p.chainTarget ? "是" : "否"} | ${p.igniteOnImpact ? "是" : "否"} |`,
  ),
  "",
  "原版依据：`TNT.cs:123-183`（`OverlapSphere` + `RemovePart`）、`TNT.cs:211-233`（`AddExplosionForce`：",
  "`impulse / max(d,1)^1.5`，质量 <0.1 / <0.4 折减，猪再 ×1.15）、`TNT.cs:152-156`（只连锁非 `AlienTNT`）、",
  "`AlienTNT.cs:8-52`（不 `RemovePart`、`LateUpdate` 清 `m_triggered`；冷却时间 vanilla = 0）、",
  "`AlienTNT.cs:54-56`（`OnCollisionEnter` 空 ⇒ 撞击不点火）。",
]);

console.log(`report -> ${OUT_JSON}`);
console.log(`report -> ${OUT_MD}`);
console.log(`parts: ${Object.keys(parts).length}  classes: ${JSON.stringify(classHistogram)}`);
