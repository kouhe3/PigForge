#!/usr/bin/env node
// Extracts Bad Piggies part sprites (BPLE Unity project) into a PigForge client
// texture manifest. Reads the original project read-only; writes only into the
// output directory (default clients/web/public/assets/original, gitignored).
//
// Usage:
//   node tools/bple-textures/extract.mjs [--bple <BPLE_Unity6>] [--out <dir>]
//
// Rect provenance (verified against BPLE source):
//   - Assets/Resources/guisystem/spritemapping.txt holds each sprite's trimmed
//     normalized UV (Unity origin: bottom-left). Pixel rect = round(uv * textureSize),
//     top-origin y = textureHeight - y - height. The sibling sprites.txt selection
//     rect is a stale 1024 design grid and is used only for trimmed pixel sizes.
//   - Assets/Resources/guisystem/sprites.txt holds per-sprite trimmed width/height
//     (columns 12/13) and the sprite pivot.
//   - UnmanagedSprite prefabs carry exact grid UVs in the prefab itself.
//   - The atlas PNG is the sprite GameObject's MeshRenderer material -> _MainTex.

import { copyFileSync, existsSync, mkdirSync, readFileSync, readdirSync, writeFileSync } from "node:fs";
import { basename, dirname, join, resolve } from "node:path";
import { fileURLToPath } from "node:url";

const HERE = dirname(fileURLToPath(import.meta.url));
const REPO = resolve(HERE, "..", "..");
const UNITS_PER_PIXEL = 20 / 768; // BPLE: 768 px = 20 world units (Sprite.cs camera height).

function arg(name, fallback) {
  const index = process.argv.indexOf(`--${name}`);
  return index >= 0 && process.argv[index + 1] ? process.argv[index + 1] : fallback;
}

const BPLE = resolve(arg("bple", process.env.BPLE_ROOT ?? join(REPO, "..", "BPLE_Unity6")));
const OUT = resolve(arg("out", join(REPO, "clients", "web", "public", "assets", "original")));
const ASSETS = join(BPLE, "Assets");
const GAMEOBJECT = join(ASSETS, "GameObject");

if (!existsSync(ASSETS)) {
  console.error(`BPLE project not found: ${ASSETS}\nPass --bple <path to BPLE_Unity6>.`);
  process.exit(1);
}

// ---------------------------------------------------------------- guid index

const guidToPath = new Map();
(function walk(dir) {
  for (const entry of readdirSync(dir, { withFileTypes: true })) {
    const path = join(dir, entry.name);
    if (entry.isDirectory()) {
      walk(path);
    } else if (entry.name.endsWith(".meta")) {
      const head = readFileSync(path, "utf8").slice(0, 256);
      const match = /^guid: ([0-9a-f]{32})/m.exec(head);
      if (match) guidToPath.set(match[1], path.slice(0, -5));
    }
  }
})(ASSETS);

function scriptGuid(scriptName) {
  const meta = join(ASSETS, "Scripts", "Assembly-CSharp", `${scriptName}.cs.meta`);
  const match = /^guid: ([0-9a-f]{32})/m.exec(readFileSync(meta, "utf8"));
  return match[1];
}

const SPRITE_SCRIPT = scriptGuid("Sprite");
const UNMANAGED_SCRIPT = scriptGuid("UnmanagedSprite");
const NAMED_SCRIPT = scriptGuid("INSerializedSprite");

// ------------------------------------------------------------ sprite tables

/**
 * sprites.txt: id -> the sprite database row (GUISystem/Sprites).
 * Columns: 0 id | 1 name | 2 materialId | 3-6 selection x/y/w/h | 7-8 pivot x/y |
 * 9-10 UV x/y | 11-12 width/height | 13 subdivisions | 14 opaqueBorderPixels.
 * `selection` is the sprite's box in the 1024-px design grid and `uv`/`width`/`height` its
 * packed rect; Sprite.SelectSprite derives the mesh's pivot offset from all four (see below).
 */
const spriteCells = new Map();
for (const line of readFileSync(join(ASSETS, "Resources", "guisystem", "sprites.txt"), "utf8").split("\n")) {
  const f = line.split("\t");
  if (f.length < 14 || !f[0]) continue;
  const int = (index) => Number(f[index]);
  spriteCells.set(f[0], {
    selectionX: int(3),
    selectionY: int(4),
    selectionWidth: int(5),
    selectionHeight: int(6),
    pivotX: int(7),
    pivotY: int(8),
    uvX: int(9),
    uvY: int(10),
    w: int(11),
    h: int(12),
  });
}
/** spritemapping.txt: id -> normalized trimmed UV [x, y, w, h] (Unity bottom-left). */
const spriteUv = new Map();
for (const line of readFileSync(join(ASSETS, "Resources", "guisystem", "spritemapping.txt"), "utf8").split("\n")) {
  const f = line.split("\t");
  if (f.length < 5 || !f[0]) continue;
  spriteUv.set(f[0], [Number(f[1]), Number(f[2]), Number(f[3]), Number(f[4])]);
}

/** <Atlas>_TextAsset.txt: header "<atlas> <width> <height>", then "<name> x y w h scaleX scaleY screenHeight" (top-left origin). */
const namedSprites = new Map();
for (const file of readdirSync(join(ASSETS, "TextAsset"))) {
  if (!file.endsWith("_TextAsset.txt")) continue;
  const lines = readFileSync(join(ASSETS, "TextAsset", file), "utf8").split("\n");
  const atlasName = lines[0].trim().split(/\s+/)[0];
  for (const line of lines.slice(1)) {
    const f = line.trim().split(/\s+/);
    if (f.length < 8) continue;
    namedSprites.set(`${atlasName}\0${f[0]}`, {
      x: Number(f[1]),
      y: Number(f[2]),
      w: Number(f[3]),
      h: Number(f[4]),
      scaleX: Number(f[5]),
      scaleY: Number(f[6]),
      screenHeight: Number(f[7]),
    });
  }
}

// ------------------------------------------------------------- prefab parsing


function parsePrefab(text) {
  const blocks = [];
  for (const chunk of text.split(/^--- !u!/m).slice(1)) {
    const header = /^(\d+) &(\d+)(?: stripped)?\n/.exec(chunk);
    if (header) blocks.push({ classId: Number(header[1]), fileId: header[2], body: chunk.slice(header[0].length) });
  }
  const field = (body, name) => {
    const match = new RegExp(`^\\s*${name}:\\s*(.*)$`, "m").exec(body);
    return match ? match[1].trim() : undefined;
  };
  const gameObjects = new Map();
  const transforms = new Map();
  const renderers = new Map();
  for (const { classId, fileId, body } of blocks) {
    if (classId === 1) {
      gameObjects.set(fileId, { name: field(body, "m_Name"), active: field(body, "m_IsActive") !== "0" });
    } else if (classId === 4) {
      const pos = /m_LocalPosition: \{x: ([-\d.eE+]+), y: ([-\d.eE+]+), z: ([-\d.eE+]+)\}/.exec(body);
      const rot = /m_LocalRotation: \{x: ([-\d.eE+]+), y: ([-\d.eE+]+), z: ([-\d.eE+]+), w: ([-\d.eE+]+)\}/.exec(body);
      transforms.set(fileId, {
        gameObject: /m_GameObject: \{fileID: (\d+)\}/.exec(body)?.[1],
        father: /m_Father: \{fileID: (\d+)\}/.exec(body)?.[1],
        pos: pos ? [Number(pos[1]), Number(pos[2]), Number(pos[3])] : [0, 0, 0],
        rot: rot ? [Number(rot[1]), Number(rot[2]), Number(rot[3]), Number(rot[4])] : [0, 0, 0, 1],
      });
    } else if (classId === 23) {
      const gameObject = /m_GameObject: \{fileID: (\d+)\}/.exec(body)?.[1];
      const material = /^\s*- \{fileID: 2100000, guid: ([0-9a-f]{32}), type: 2\}/m.exec(body)?.[1];
      if (gameObject) renderers.set(gameObject, material);
    }
  }
  // GameObject -> its transform file id
  const transformByGameObject = new Map();
  for (const [fileId, transform] of transforms) {
    if (transform.gameObject) transformByGameObject.set(transform.gameObject, fileId);
  }
  const sprites = [];
  for (const { classId, body } of blocks) {
    if (classId !== 114) continue;
    const script = /m_Script: \{fileID: \d+, guid: ([0-9a-f]{32})/.exec(body)?.[1];
    if (script !== SPRITE_SCRIPT && script !== UNMANAGED_SCRIPT && script !== NAMED_SCRIPT) continue;
    const gameObject = /m_GameObject: \{fileID: (\d+)\}/.exec(body)?.[1];
    if (!gameObject) continue;
    sprites.push({
      kind: script === SPRITE_SCRIPT ? "sprite" : script === UNMANAGED_SCRIPT ? "grid" : "named",
      gameObject,
      fields: Object.fromEntries(
        [...body.matchAll(/^\s*(m_[A-Za-z0-9_]+):\s*(.*)$/gm)].map((m) => [m[1], m[2].trim()]),
      ),
    });
  }
  return { gameObjects, transforms, transformByGameObject, renderers, sprites };
}

function textureOfMaterial(materialGuid) {
  const materialPath = guidToPath.get(materialGuid);
  if (!materialPath || !existsSync(materialPath)) return undefined;
  const text = readFileSync(materialPath, "utf8");
  const textureGuid = /m_Texture: \{fileID: \d+, guid: ([0-9a-f]{32})/.exec(text)?.[1];
  const texturePath = textureGuid ? guidToPath.get(textureGuid) : undefined;
  return texturePath && texturePath.endsWith(".png") && existsSync(texturePath) ? texturePath : undefined;
}

function pngSize(path) {
  const head = readFileSync(path).subarray(0, 24);
  if (head.subarray(0, 8).toString("hex") !== "89504e470d0a1a0a") return undefined;
  return { width: head.readUInt32BE(16), height: head.readUInt32BE(20) };
}

// ---------------------------------------------------------------- extraction

/**
 * Child nodes whose local rotation the original drives at runtime: the wheel pivots
 * (CartWheel.cs:101,106) and the fan/rotor/propeller visualization (FanPropeller.cs:323).
 * A sprite under one of these turns with the part's spin; every other sprite — e.g. a
 * wheel's axle, which sits on the prefab root — keeps the part's own orientation.
 */
const SPINNING_NODES = new Set(["WheelPivot", "FakeWheelPivot", "FanVisualization"]);

/** Sprite centre offset from the prefab root, in BPLE world units (root's own offset
 * excluded), plus whether the sprite rides a node the original rotates. */
function localOffset(prefab, gameObject) {
  let transformId = prefab.transformByGameObject.get(gameObject);
  const chain = [];
  while (transformId && prefab.transforms.has(transformId)) {
    const transform = prefab.transforms.get(transformId);
    chain.push(transform);
    transformId = transform.father;
  }
  // Walk from the root down so each step's accumulated offset is that node's own offset
  // from the root (the sprite's own local position must not leak into the pivot).
  const position = [0, 0, 0];
  let angle = 0;
  let pivot = null;
  for (let i = chain.length - 2; i >= 0; i -= 1) {
    const t = chain[i];
    position[0] += t.pos[0];
    position[1] += t.pos[1];
    position[2] += t.pos[2];
    angle += 2 * Math.atan2(t.rot[2], t.rot[3]);
    if (SPINNING_NODES.has(prefab.gameObjects.get(t.gameObject)?.name)) {
      // The node the original rotates: the axis this sprite — and the whole wheel — turns
      // about. The renderer spins rotating sprites around it.
      pivot = [position[0], position[1]];
    }
  }

  return {
    x: position[0],
    y: position[1],
    z: position[2],
    angle,
    spin: pivot !== null,
    pivot,
  };
}

function activeChain(prefab, gameObject) {
  let transformId = prefab.transformByGameObject.get(gameObject);
  while (transformId && prefab.transforms.has(transformId)) {
    const transform = prefab.transforms.get(transformId);
    const owner = transform.gameObject && prefab.gameObjects.get(transform.gameObject);
    if (owner && !owner.active) return false;
    transformId = transform.father;
  }
  return true;
}

const warnings = [];
const usedAtlases = new Map();

function extractSprite(prefab, sprite) {
  if (!activeChain(prefab, sprite.gameObject)) return undefined;
  const materialGuid = prefab.renderers.get(sprite.gameObject);
  const texturePath = materialGuid && textureOfMaterial(materialGuid);
  if (!texturePath) {
    warnings.push(`no atlas material/texture for ${prefab.gameObjects.get(sprite.gameObject)?.name}`);
    return undefined;
  }
  const size = pngSize(texturePath);
  const atlas = texturePath.split(/[\\/]/).pop();
  const offset = localOffset(prefab, sprite.gameObject);
  const f = sprite.fields;
  // Sprite.SelectSprite/CreateMesh rebuild each sprite's quad at runtime around the database
  // pivot, offsetting it by (selection centre - packed-rect centre + pivot) source pixels,
  // so a node's local position alone is NOT the artwork's centre. Ignoring it stacks a wheel's
  // tyre on its fork instead of hanging it on the axle. UnmanagedSprite/INSerializedSprite
  // centre their quads on the node and carry no pivot, so only the Sprite path corrects.
  let artX = offset.x;
  let artY = offset.y;
  let rect;
  let quadW;
  let quadH;
  let unitsPerPixel = UNITS_PER_PIXEL;
  if (sprite.kind === "grid") {
    const subdivisions = Number(f.m_atlasGridSubdivisions);
    const cellW = size.width / subdivisions;
    const cellH = size.height / subdivisions;
    const x = Number(f.m_UVx) * cellW;
    const yBottom = Number(f.m_UVy) * cellH;
    const w = Number(f.m_width) * cellW;
    const h = Number(f.m_height) * cellH;
    rect = { x, y: size.height - yBottom - h, w, h };
    quadW = Number(f.m_spriteWidth);
    quadH = Number(f.m_spriteHeight);
  } else if (sprite.kind === "named") {
    // INSerializedSprite: name -> Assets/TextAsset/<Atlas>_TextAsset.txt (top-left origin).
    const named = namedSprites.get(`${atlas.replace(/\.png$/, "")}\0${f.m_name}`);
    if (!named) {
      warnings.push(`named sprite '${f.m_name}' missing from the ${atlas} text asset`);
      return undefined;
    }
    rect = { x: named.x, y: named.y, w: named.w, h: named.h };
    quadW = named.w * named.scaleX;
    quadH = named.h * named.scaleY;
    unitsPerPixel = 20 / named.screenHeight;
  } else {
    const cell = spriteCells.get(f.m_id);
    const uv = spriteUv.get(f.m_id);
    if (!cell || !uv) {
      warnings.push(`sprite ${f.m_id} missing from sprites.txt/spritemapping.txt`);
      return undefined;
    }
    const [u, v, uw, uh] = uv;
    const x = Math.round(u * size.width);
    const yBottom = Math.round(v * size.height);
    const w = Math.round(uw * size.width);
    const h = Math.round(uh * size.height);
    rect = { x, y: size.height - yBottom - h, w, h };
    const scaleX = Number(f.m_scaleX ?? 1);
    const scaleY = Number(f.m_scaleY ?? 1);
    quadW = scaleX * cell.w;
    quadH = scaleY * cell.h;
    // The pivot the original rotates this quad about, in source pixels, in the same
    // frame as the packed rect (its centre) — a node offset in world units.
    const pivotOffsetX = cell.selectionX + cell.selectionWidth / 2 - (cell.uvX + cell.w / 2) + cell.pivotX + Number(f.m_pivotX ?? 0);
    const pivotOffsetY = cell.selectionY + cell.selectionHeight / 2 - (cell.uvY + cell.h / 2) + cell.pivotY + Number(f.m_pivotY ?? 0);
    artX = offset.x - scaleX * pivotOffsetX * UNITS_PER_PIXEL;
    artY = offset.y - scaleY * pivotOffsetY * UNITS_PER_PIXEL;
  }
  if (!(rect.w > 0 && rect.h > 0 && quadW > 0 && quadH > 0)) return undefined;
  const transformId = prefab.transformByGameObject.get(sprite.gameObject);
  usedAtlases.set(atlas, { size, path: texturePath });
  return {
    name: prefab.gameObjects.get(sprite.gameObject)?.name ?? "",
    root: prefab.transforms.get(transformId)?.father === "0",
    rotates: offset.spin,
    pivot: offset.pivot,
    atlas,
    x: rect.x,
    y: rect.y,
    w: rect.w,
    h: rect.h,
    cx: artX,
    cy: artY,
    sx: quadW * unitsPerPixel,
    sy: quadH * unitsPerPixel,
    rot: offset.angle,
    z: offset.z,
  };
}

function extractPart(prefabName) {
  const path = join(GAMEOBJECT, `${prefabName}.prefab`);
  if (!existsSync(path)) {
    warnings.push(`prefab missing: ${prefabName}`);
    return undefined;
  }
  const prefab = parsePrefab(readFileSync(path, "utf8"));
  const found = prefab.sprites.map((s) => extractSprite(prefab, s)).filter(Boolean);
  if (found.length === 0) {
    warnings.push(`no extractable sprite in ${prefabName}`);
    return undefined;
  }
  // Joint attachment markers are conditional in-game (one per connection side);
  // every other sprite in the prefab is part of the visual (body, face, crown,
  // wheel rim, light cone, extra balloons/sandbags) and is kept.
  const seen = new Set();
  const filtered = found.filter((s) => {
    if (/attachment/i.test(s.name)) return false;
    const key = `${s.atlas}|${s.x}|${s.y}|${s.w}|${s.h}|${s.cx}|${s.cy}|${s.rot}`;
    if (seen.has(key)) return false;
    seen.add(key);
    return true;
  });
  if (filtered.length === 0) {
    // A prefab whose only sprites are attachment markers still has a visual.
    warnings.push(`${prefabName} has only attachment sprites`);
    return undefined;
  }
  const sprites = filtered;
  let minX = Infinity;
  let minY = Infinity;
  let maxX = -Infinity;
  let maxY = -Infinity;
  for (const s of sprites) {
    minX = Math.min(minX, s.cx - s.sx / 2);
    maxX = Math.max(maxX, s.cx + s.sx / 2);
    minY = Math.min(minY, s.cy - s.sy / 2);
    maxY = Math.max(maxY, s.cy + s.sy / 2);
  }
  const bbox = [maxX - minX, maxY - minY];
  const centreX = (minX + maxX) / 2;
  const centreY = (minY + maxY) / 2;
  const round = (value) => Math.round(value * 1e4) / 1e4;
  // The axis the part's rotating sprites turn about, in the same frame as `cx`/`cy`
  // (relative to the composite's layout anchor). Absent for parts that never spin. Read
  // before sorting so the axis never depends on the paint order below.
  const pivot = sprites.find((s) => s.pivot)?.pivot;
  // Paint order, far to near. The game camera sits at z = -15 looking towards +z
  // (IngameCamera.cs:440,1038), so a larger z is farther away, and Unity's transparent queue
  // draws far geometry first. The array therefore runs z descending, and the client blits it
  // in order. Reversing this stacks a wheel's tire over the spokes that show through its rim
  // hole and hides its axle behind the wheel (the reported motor-wheel regression).
  sprites.sort((a, b) => b.z - a.z);
  return {
    bbox: [round(bbox[0]), round(bbox[1])],
    ...(pivot ? { pivot: [round(pivot[0] - centreX), round(pivot[1] - centreY)] } : {}),
    sprites: sprites.map((s) => ({
      atlas: s.atlas,
      x: s.x,
      y: s.y,
      w: s.w,
      h: s.h,
      cx: round(s.cx - centreX),
      cy: round(s.cy - centreY),
      sx: round(s.sx),
      sy: round(s.sy),
      rot: round(s.rot),
      rotates: s.rotates,
    })),
  };
}

// ---------------------------------------------------------------------- main

const map = JSON.parse(readFileSync(join(HERE, "part-map.json"), "utf8"));
const assignments = { ...map.parts, ...map.variants };
const parts = {};
let mapped = 0;
for (const [partTypeId, prefabName] of Object.entries(assignments)) {
  if (!prefabName) continue;
  const entry = extractPart(prefabName);
  if (entry) {
    parts[partTypeId] = entry;
    mapped += 1;
  } else {
    warnings.push(`part ${partTypeId} (${prefabName}) skipped`);
  }
}

const manifest = {
  format: "pigforge.part-textures",
  schemaVersion: 2,
  source: basename(BPLE),
  unitsPerPixel: UNITS_PER_PIXEL,
  atlases: Object.fromEntries([...usedAtlases].map(([name, entry]) => [name, entry.size])),
  parts,
};

mkdirSync(OUT, { recursive: true });
for (const [atlas, entry] of usedAtlases) {
  copyFileSync(entry.path, join(OUT, atlas));
}
writeFileSync(join(OUT, "part-textures.json"), `${JSON.stringify(manifest, null, 2)}\n`);

console.log(`bple:   ${BPLE}`);
console.log(`out:    ${OUT}`);
console.log(`parts:  ${mapped}/${Object.keys(assignments).length} mapped, ${Object.keys(parts).length} emitted`);
console.log(`atlas:  ${[...usedAtlases.keys()].join(", ")}`);
if (warnings.length) {
  console.log(`warnings (${warnings.length}):`);
  for (const warning of warnings) console.log(`  - ${warning}`);
}
