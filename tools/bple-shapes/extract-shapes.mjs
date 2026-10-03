#!/usr/bin/env node
// Extracts the original Bad Piggies part colliders (BPLE Unity project) as the
// authority for PigForge `content/parts.json` shapes. Reads the original project
// read-only and prints a JSON report on stdout; `apply-shapes.mjs` turns that report
// into the content file. See docs/decisions/ADR-005.
//
// Usage:
//   node tools/bple-shapes/extract-shapes.mjs [--bple <dir>] [--out <file>]
//   node tools/bple-shapes/apply-shapes.mjs [--report <file>] [--dry-run]
//
// Collider provenance (verified against BPLE source):
//   - Part prefabs live in <BPLE>/Assets/GameObject/Part_*_SET.prefab and map to
//     PigForge partTypeIds through tools/bple-textures/part-map.json.
//   - BoxCollider / SphereCollider / CapsuleCollider components carry the physics
//     shape. The transform origin is the part's grid/attachment point, so colliders
//     are reported as a local offset from it (the prefab root's own staging position
//     is excluded, exactly like the texture extractor).
//   - Some parts add their collider at runtime: Balloon -> sphere r=0.5 (Balloon.cs:123),
//     Sandbag -> sphere r=0.13 at y=-0.1 (Sandbag.cs:124-127). Rope has no collider.
//   - The game is 2.5D: rigidbodies freeze Z position and X/Y rotation, so only the
//     X/Y extents and the shape kind are observable. Z sizes are reported but are
//     design artefacts, not gameplay dimensions.

import { existsSync, readFileSync, writeFileSync } from "node:fs";
import { dirname, join, resolve } from "node:path";
import { fileURLToPath } from "node:url";

const HERE = dirname(fileURLToPath(import.meta.url));
const REPO = resolve(HERE, "..", "..");

function arg(name, fallback) {
  const index = process.argv.indexOf(`--${name}`);
  return index >= 0 && process.argv[index + 1] ? process.argv[index + 1] : fallback;
}

const BPLE = resolve(arg("bple", process.env.BPLE_ROOT ?? join(REPO, "..", "BPLE_Unity6")));
const OUT = arg("out", "");
const GAMEOBJECT = join(BPLE, "Assets", "GameObject");

if (!existsSync(GAMEOBJECT)) {
  console.error(`BPLE project not found: ${GAMEOBJECT}`);
  process.exit(1);
}

// ---------------------------------------------------------------- prefab parsing
const CLASS = { GameObject: 1, Transform: 4, MeshRenderer: 23, BoxCollider: 65, SphereCollider: 135, CapsuleCollider: 136 };

function vec3(body, name) {
  const match = new RegExp(`^\\s*${name}: \\{x: ([^,]+), y: ([^,]+), z: ([^}]+)\\}$`, "m").exec(body);
  return match ? [Number(match[1]), Number(match[2]), Number(match[3])] : undefined;
}

function quat(body, name) {
  const match = new RegExp(`^\\s*${name}: \\{x: ([^,]+), y: ([^,]+), z: ([^,]+), w: ([^}]+)\\}$`, "m").exec(body);
  return match ? [Number(match[1]), Number(match[2]), Number(match[3]), Number(match[4])] : undefined;
}

function field(body, name) {
  const match = new RegExp(`^\\s*${name}:\\s*(.*)$`, "m").exec(body);
  return match ? match[1].trim() : undefined;
}

function parsePrefab(text) {
  const blocks = [];
  for (const chunk of text.split(/^--- !u!/m).slice(1)) {
    const header = /^(\d+) &(\d+)(?: stripped)?\n/.exec(chunk);
    if (header) blocks.push({ classId: Number(header[1]), fileId: header[2], body: chunk.slice(header[0].length) });
  }
  const gameObjects = new Map();
  const transforms = new Map();
  const transformByGameObject = new Map();
  const colliders = [];
  let part = null;
  for (const { classId, fileId, body } of blocks) {
    if (classId === CLASS.GameObject) {
      gameObjects.set(fileId, { name: field(body, "m_Name"), active: field(body, "m_IsActive") !== "0" });
    } else if (classId === CLASS.Transform) {
      const rot = quat(body, "m_LocalRotation");
      transforms.set(fileId, {
        gameObject: /m_GameObject: \{fileID: (\d+)\}/.exec(body)?.[1],
        father: /m_Father: \{fileID: (\d+)\}/.exec(body)?.[1],
        pos: vec3(body, "m_LocalPosition") ?? [0, 0, 0],
        rot: rot ?? [0, 0, 0, 1],
        scale: vec3(body, "m_LocalScale") ?? [1, 1, 1],
      });
    } else if (classId === CLASS.BoxCollider) {
      colliders.push({ kind: "box", gameObject: /m_GameObject: \{fileID: (\d+)\}/.exec(body)?.[1], size: vec3(body, "m_Size"), center: vec3(body, "m_Center") ?? [0, 0, 0], trigger: field(body, "m_IsTrigger") === "1" });
    } else if (classId === CLASS.SphereCollider) {
      colliders.push({ kind: "sphere", gameObject: /m_GameObject: \{fileID: (\d+)\}/.exec(body)?.[1], radius: Number(field(body, "m_Radius")), center: vec3(body, "m_Center") ?? [0, 0, 0], trigger: field(body, "m_IsTrigger") === "1" });
    } else if (classId === CLASS.CapsuleCollider) {
      colliders.push({ kind: "capsule", gameObject: /m_GameObject: \{fileID: (\d+)\}/.exec(body)?.[1], radius: Number(field(body, "m_Radius")), height: Number(field(body, "m_Height")), direction: Number(field(body, "m_Direction")), center: vec3(body, "m_Center") ?? [0, 0, 0], trigger: field(body, "m_IsTrigger") === "1" });
    } else if (classId === 114) {
      const mass = field(body, "m_mass");
      if (mass !== undefined && part === null) {
        part = { mass: Number(mass), partType: Number(field(body, "m_partType")), interactiveRadius: Number(field(body, "m_interactiveRadius")) };
      }
    }
  }
  for (const [fileId, transform] of transforms) {
    if (transform.gameObject) transformByGameObject.set(transform.gameObject, fileId);
  }
  return { gameObjects, transforms, transformByGameObject, colliders, part };
}

/** Local pose of a GameObject relative to the prefab root, excluding the root's own staging transform. */
function localPose(prefab, gameObject) {
  let transformId = prefab.transformByGameObject.get(gameObject);
  const chain = [];
  while (transformId && prefab.transforms.has(transformId)) {
    const transform = prefab.transforms.get(transformId);
    chain.push(transform);
    transformId = transform.father;
  }
  const rootFirst = chain.slice(0, Math.max(chain.length - 1, 0)).reverse();
  const position = [0, 0, 0];
  const scale = [1, 1, 1];
  let angle = 0;
  for (const node of rootFirst) {
    const cos = Math.cos(angle);
    const sin = Math.sin(angle);
    position[0] += node.pos[0] * cos - node.pos[1] * sin;
    position[1] += node.pos[0] * sin + node.pos[1] * cos;
    position[2] += node.pos[2];
    scale[0] *= node.scale[0];
    scale[1] *= node.scale[1];
    scale[2] *= node.scale[2];
    angle += 2 * Math.atan2(node.rot[2], node.rot[3]);
  }
  return { x: position[0], y: position[1], z: position[2], angle, scale };
}

function rotateZ([x, y, z], angle) {
  const cos = Math.cos(angle);
  const sin = Math.sin(angle);
  return [x * cos - y * sin, x * sin + y * cos, z];
}

const round = (value) => Math.round(value * 1e5) / 1e5;

/** Collider geometry in part-local space: size/radius plus the centre offset from the part origin. */
function colliderShape(prefab, collider) {
  const pose = localPose(prefab, collider.gameObject);
  const centreLocal = [collider.center[0] * pose.scale[0], collider.center[1] * pose.scale[1], collider.center[2] * pose.scale[2]];
  const rotated = rotateZ(centreLocal, pose.angle);
  const offset = [pose.x + rotated[0], pose.y + rotated[1], pose.z + rotated[2]];
  const name = prefab.gameObjects.get(collider.gameObject)?.name ?? "";
  if (collider.kind === "box") {
    const size = collider.size.map((value, axis) => value * pose.scale[axis]);
    return { kind: "box", name, size: size.map(round), offset: offset.map(round), angle: round(pose.angle), trigger: collider.trigger };
  }
  if (collider.kind === "sphere") {
    const radius = collider.radius * Math.max(pose.scale[0], pose.scale[1]);
    return { kind: "sphere", name, radius: round(radius), offset: offset.map(round), angle: round(pose.angle), trigger: collider.trigger };
  }
  const radius = collider.radius * Math.max(pose.scale[0], pose.scale[1]);
  return { kind: "capsule", name, radius: round(radius), height: round(collider.height * pose.scale[1]), direction: collider.direction, offset: offset.map(round), angle: round(pose.angle), trigger: collider.trigger };
}

// ------------------------------------------------------- runtime collider patches

/** Colliders the original adds from script at spawn, keyed by prefab name. */
const RUNTIME_COLLIDERS = {
  Part_Balloon_01_SET: [{ kind: "sphere", name: "runtime", radius: 0.5, offset: [0, 0, 0], angle: 0, trigger: false, source: "Balloon.cs:123" }],
  Part_Balloons2_01_SET: [{ kind: "sphere", name: "runtime", radius: 0.5, offset: [0, 0, 0], angle: 0, trigger: false, source: "Balloon.cs:123" }],
  Part_Balloons3_01_SET: [{ kind: "sphere", name: "runtime", radius: 0.5, offset: [0, 0, 0], angle: 0, trigger: false, source: "Balloon.cs:123" }],
  Part_Sandbag_01_SET: [{ kind: "sphere", name: "runtime", radius: 0.13, offset: [0, -0.1, 0], angle: 0, trigger: false, source: "Sandbag.cs:124-127" }],
  Part_Sandbags2_01_SET: [{ kind: "sphere", name: "runtime", radius: 0.13, offset: [0, -0.1, 0], angle: 0, trigger: false, source: "Sandbag.cs:124-127" }],
  Part_Sandbags3_01_SET: [{ kind: "sphere", name: "runtime", radius: 0.13, offset: [0, -0.1, 0], angle: 0, trigger: false, source: "Sandbag.cs:124-127" }],
};

/**
 * Joint attachment markers: node name -> the part-local side its collider stands for. The
 * original shows each one conditionally (`ChangeVisualConnections`) and turns its collider into
 * a trigger while it is hidden (Rocket.cs:157-160), so a marker is not body geometry — but it is
 * exactly what a build-time connection and a drag snap line up against. Same table as the
 * texture extractor, so a shape and its sprite carry the same side.
 */
const CONDITION_NODES = {
  TopAttachment: "top",
  BottomAttachment: "bottom",
  LeftAttachment: "left",
  RightAttachment: "right",
  TopLeftAttachment: "topLeft",
  TopRightAttachment: "topRight",
  BottomLeftAttachment: "bottomLeft",
  BottomRightAttachment: "bottomRight",
};

// ---------------------------------------------------------------------- main

const map = JSON.parse(readFileSync(join(REPO, "tools", "bple-textures", "part-map.json"), "utf8"));
const assignments = { ...map.parts, ...map.variants };
const parts = {};
const warnings = [];

for (const [partTypeId, prefabName] of Object.entries(assignments)) {
  if (!prefabName) continue;
  const path = join(GAMEOBJECT, `${prefabName}.prefab`);
  if (!existsSync(path)) {
    warnings.push(`prefab missing: ${prefabName}`);
    continue;
  }
  const prefab = parsePrefab(readFileSync(path, "utf8"));
  // Every non-trigger collider is reported. Joint attachment markers keep their collider but
  // are tagged `condition`, which keeps them out of physics and cell occupancy while drag
  // snapping and connection proximity still see them; script helper colliders (the King Pig's
  // mouth) stay out entirely.
  const shapes = prefab.colliders
    .filter((collider) => !collider.trigger && collider.gameObject)
    .map((collider) => colliderShape(prefab, collider))
    .map((shape) => (Object.hasOwn(CONDITION_NODES, shape.name)
      ? { ...shape, condition: { kind: "attachment", side: CONDITION_NODES[shape.name] } }
      : shape))
    .filter((shape) => shape.name !== "MouthPos")
    .filter((shape) => (shape.kind === "box" ? shape.size[0] > 0 && shape.size[1] > 0 : shape.radius > 0));
  for (const shape of RUNTIME_COLLIDERS[prefabName] ?? []) shapes.push(shape);
  const rootName = [...prefab.transforms.values()].find((transform) => transform.father === "0")?.gameObject;
  const root = rootName ? prefab.gameObjects.get(rootName)?.name : undefined;
  for (const shape of shapes) shape.onRoot = shape.name === root;
  parts[partTypeId] = { prefab: prefabName, mass: prefab.part?.mass ?? null, partType: prefab.part?.partType ?? null, shapes };
  if (shapes.length === 0) warnings.push(`${prefabName} has no collider (runtime or authored)`);
}

const report = {
  format: "pigforge.bple-part-shapes",
  schemaVersion: 1,
  source: BPLE,
  parts,
  warnings,
};
const text = `${JSON.stringify(report, null, 2)}\n`;
if (OUT) {
  writeFileSync(resolve(OUT), text);
  console.log(`wrote ${resolve(OUT)}`);
} else {
  process.stdout.write(text);
}
