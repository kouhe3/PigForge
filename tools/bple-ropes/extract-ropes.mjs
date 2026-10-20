#!/usr/bin/env node
// Extracts the original rope's START materialization (`docs/specs/rope.md`): the segment prefab's
// mass/radius/material, the IN segment-count and vanilla flags, and the class constants Rope.cs
// runs the force chain with. Read-only over BPLE; `apply-ropes.mjs` writes the content capability.
//
// Usage:
//   node tools/bple-ropes/extract-ropes.mjs [--bple <dir>] [--json <file>]
//   node tools/bple-ropes/apply-ropes.mjs [--report <file>] [--dry-run]

import { existsSync, readFileSync, writeFileSync, readdirSync } from "node:fs";
import { basename, join } from "node:path";
import { arg } from "../lib/args.mjs";
import { bpleProject, reportJson, reportMd } from "../lib/paths.mjs";
import { checks, fail } from "../lib/report.mjs";
import { buildClassBases, buildGuidIndex, classChain, prefabText, readField } from "../lib/unity.mjs";

// Rope.cs is quoted as text, so read the pristine copy pinned to the original's editor (the same
// rule as the other tools that assert .cs literals).
const BPLE = bpleProject("BPLE 2022.1.9");
const ASSETS = join(BPLE, "Assets");
const GAMEOBJECT = join(ASSETS, "GameObject");
const SCRIPTS = join(ASSETS, "Scripts", "Assembly-CSharp");
const OUT_JSON = reportJson("ropes");
const OUT_MD = reportMd("ropes");

if (!existsSync(GAMEOBJECT)) {
  fail(`BPLE project not found at ${BPLE} (expected Assets/GameObject).`);
}

const check = checks();
const assert = (condition, message) => {
  if (!condition) check.push(message);
};
const guidToClass = buildGuidIndex(SCRIPTS, { declaredClass: true });
const classBases = buildClassBases(SCRIPTS);
const classOfPrefab = (name) => {
  const text = prefabText(GAMEOBJECT, name);
  if (text === null) return null;
  const classes = [...text.matchAll(/m_Script: \{fileID: -?\d+, guid: ([0-9a-f]{32})/g)]
    .map((match) => guidToClass.get(match[1]))
    .filter(Boolean);
  return classes.find((className) => classChain(classBases, className).includes("BasePart")) ?? classes[0] ?? null;
};

const num = (value) => (value === undefined ? null : Number(value));

// ------------------------------------------------------------------ part prefabs
const partNames = Array.from({ length: 8 }, (_, index) => `Part_Rope_0${index + 1}_SET`);
const prefabs = partNames.map((name) => {
  const text = prefabText(GAMEOBJECT, name);
  assert(text !== null, `${name}: prefab missing`);
  return {
    name,
    class: classOfPrefab(name),
    partType: num(text && readField(text, "m_partType")),
    mass: num(text && readField(text, "m_mass")),
    customDirection: num(text && readField(text, "m_customJointConnectionDirection")),
    grid: text && ["gridXmin", "gridXmax", "gridYmin", "gridYmax"].map((field) => num(readField(text, `m_${field}`))),
  };
});
const ropePrefabs = prefabs.filter((prefab) => prefab.class === "Rope");
const platePrefabs = prefabs.filter((prefab) => prefab.class === "HingePlate");
assert(prefabs.length === 8, `rope prefab census: expected 8, got ${prefabs.length}`);
assert(ropePrefabs.length === 4, `Rope class census: expected 4, got ${ropePrefabs.length}`);
assert(platePrefabs.length === 4, `HingePlate class census: expected 4, got ${platePrefabs.length}`);
assert(ropePrefabs.every((prefab) => prefab.partType === 30), "rope PartType: every Rope prefab must be 30");
assert(ropePrefabs.every((prefab) => prefab.customDirection === 5), "rope custom connection direction: expected 5 (LeftAndRight)");
assert(ropePrefabs.every((prefab) => prefab.grid.join(",") === "0,0,0,0"), "rope grid: every variant must declare one cell");

// ------------------------------------------------------------------ segment prefab
const segmentText = prefabText(GAMEOBJECT, "RopeSegment");
assert(segmentText !== null, "RopeSegment.prefab missing");
const segment = {
  mass: num(segmentText && readField(segmentText, "m_Mass")),
  radius: num(segmentText && readField(segmentText, "m_Radius")),
  constraints: num(segmentText && readField(segmentText, "m_Constraints")),
  angularDrag: num(segmentText && readField(segmentText, "m_AngularDrag")),
};
assert(segment.mass === 0.05, `RopeSegment mass: expected 0.05, got ${segment.mass}`);
assert(segment.radius === 0.09, `RopeSegment sphere radius: expected 0.09, got ${segment.radius}`);
assert(segment.constraints === 56, `RopeSegment constraints: expected 56 (2.5D), got ${segment.constraints}`);
assert(segment.angularDrag === 0.5, `RopeSegment angularDrag: expected 0.5, got ${segment.angularDrag}`);

// The collider's PhysicMaterial, resolved through the .meta guid index.
const sphereBlock = /SphereCollider:[\s\S]*?m_Material: \{fileID: -?\d+, guid: ([0-9a-f]{32})/.exec(segmentText ?? "");
assert(sphereBlock !== null, "RopeSegment: sphere collider material missing");
const materialGuid = sphereBlock?.[1];
const materialFile = materialGuid
  ? readdirSync(join(ASSETS, "PhysicMaterial")).find((entry) => {
      if (!entry.endsWith(".meta")) return false;
      return readFileSync(join(ASSETS, "PhysicMaterial", entry), "utf8").includes(materialGuid);
    })
  : undefined;
assert(materialFile !== undefined, `RopeSegment: no PhysicMaterial carries ${materialGuid}`);
const materialText = materialFile ? readFileSync(join(ASSETS, "PhysicMaterial", materialFile.replace(/\.meta$/, "")), "utf8") : "";
const segmentFriction = num(/dynamicFriction:\s*([0-9.]+)/.exec(materialText)?.[1]);
assert(segmentFriction === 0, `RopeSegment friction: expected 0, got ${segmentFriction}`);

// ------------------------------------------------------------------ IN settings
const settings = JSON.parse(readFileSync(join(ASSETS, "TextAsset", "INDeclarationSettingsExp.json"), "utf8"));
const setting = (name) => settings.items.find((entry) => entry.name === name)?.value;
const segments = setting("RopeSegmentCount");
assert(segments === 8, `RopeSegmentCount (vanilla): expected 8, got ${segments}`);
for (const name of ["StableRope", "UnbreakableRope", "CollidableRope", "HingePlate"]) {
  assert(setting(name) === false, `${name} (vanilla): expected false, got ${setting(name)}`);
}

// ------------------------------------------------------------------ Rope.cs literals
const ropeSource = readFileSync(join(SCRIPTS, "Rope.cs"), "utf8");
const literal = (pattern, expected) => {
  const match = pattern.exec(ropeSource);
  assert(match !== null, `Rope.cs: literal ${expected} missing`);
  return match === null ? null : expected;
};
const constants = {
  zigzagDegrees: literal(/float num = ([0-9.]+)f;/, 74.4),
  spacing: literal(/-\s*0\.5f \* base\.transform\.right;/, 0.5) === null ? null : 0.5,
  pullForce: literal(/AddForce\(([0-9.]+)f \* vector, ForceMode\.Force\)/, 70),
  pushForce: literal(/AddForce\(([0-9.]+)f \* \(magnitude - 0\.4f\)/, 50),
  slackLength: literal(/magnitude < ([0-9.]+)f\)/, 0.4) === null ? null : 0.4,
  breakInner: literal(/if \(num2 > ([0-9.]+)f\)/, 1.25) === null ? null : 1.25,
  breakEnd: literal(/else if \(num2 > ([0-9.]+)f\)/, 1.75) === null ? null : 1.75,
  stretchLimit: literal(/num \/ \(([0-9.]+)f \* num3\)/, 1.18) === null ? null : 1.18,
  endpointPullForce: literal(/Clamp\(([0-9.]+)f \* num4/, 750) === null ? null : 750,
  endpointPullCap: literal(/num4, 0f, ([0-9.]+)f\)/, 300) === null ? null : 300,
  airDrag: literal(/AddForce\(-([0-9.]+)f \* num2 \* rigidbody\.velocity\.normalized/, 0.02) === null ? null : 0.02,
  nodeDrag: literal(/drag = 0\.5f \+ 6f \/ \(1f \+ 4f \* num \* num\)/, "0.5 + 6/(1+4V²)") === null ? null : "0.5 + 6/(1+4V²)",
  responseSteepness: literal(/Sigmoid\(([0-9.]+)f \* \(t - 0\.5f\)\)/, 8) === null ? null : 8,
  responseBias: literal(/- ([0-9.]+)f\) \/ ([0-9.]+)f;/, 0.01798) === null ? null : 0.01798,
  responseScale: literal(/- 0\.01798f\) \/ ([0-9.]+)f;/, 0.9641) === null ? null : 0.9641,
};

// ------------------------------------------------------------------ report
const report = {
  format: "pigforge.bple-ropes-report",
  schemaVersion: 1,
  source: BPLE,
  settings: { segments, stable: setting("StableRope"), unbreakable: setting("UnbreakableRope"), collidable: setting("CollidableRope"), hingePlate: setting("HingePlate") },
  segment: { ...segment, friction: segmentFriction, material: materialFile?.replace(/\.meta$/, "") ?? null },
  prefabs,
  constants,
  checks: check.failures,
};

check.verify("rope extraction checks failed:");

writeFileSync(OUT_JSON, `${JSON.stringify(report, null, 2)}\n`);
const lines = [
  "# BPLE rope materialization",
  "",
  `Source: ${BPLE}`,
  "",
  "| prefab | class | PartType | mass | custom direction | grid |",
  "|---|---|---|---|---|---|",
  ...prefabs.map((prefab) => `| ${prefab.name} | ${prefab.class} | ${prefab.partType} | ${prefab.mass} | ${prefab.customDirection} | ${prefab.grid?.join(",")} |`),
  "",
  `Segment: mass ${segment.mass}, radius ${segment.radius}, friction ${segmentFriction}, constraints ${segment.constraints}, angularDrag ${segment.angularDrag}`,
  "",
  `IN (declaration defaults): segments ${segments}, StableRope ${setting("StableRope")}, UnbreakableRope ${setting("UnbreakableRope")}, CollidableRope ${setting("CollidableRope")}, HingePlate ${setting("HingePlate")}`,
  "",
  `Constants: ${JSON.stringify(constants)}`,
];
writeFileSync(OUT_MD, `${lines.join("\n")}\n`);
console.log(`report: ${OUT_JSON}`);
console.log(`checks: ${check.failures.length === 0 ? "ok" : `${check.failures.length} failed`}; rope prefabs ${ropePrefabs.length}, hinge plates ${platePrefabs.length}`);
console.log(`segment: ${segment.mass} kg / r ${segment.radius} / friction ${segmentFriction}`);
console.log(`constants: ${JSON.stringify(constants)}`);
