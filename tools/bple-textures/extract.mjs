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
//   - Animation descriptors (schemaVersion 3):
//       spin  — the node `FanPropeller.m_fanVisualization` points at marks the blades; the
//               original compresses their scale by |cos(angle)| instead of rotating them.
//       clips — a SpriteAnimation component marks the sprite whose mesh it swaps; its frame
//               ids resolve against sprites.txt/spritemapping.txt and reuse the owning
//               Sprite's material, scales and pivots, because the original rebuilds that
//               one mesh in place (SpriteAnimation.cs:196-215 -> Sprite.SelectSprite).
//       expression — the Pig/KingPig component marks a part as running the expression machine.
//     A frame id's own materialId column is a runtime material and never resolves to an asset.

import { copyFileSync, existsSync, mkdirSync, readFileSync, readdirSync } from "node:fs";
import { basename, dirname, join, resolve } from "node:path";
import { arg } from "../lib/args.mjs";
import { MANIFEST, assets, bpleProject, gameObjects, scriptAssembly } from "../lib/paths.mjs";
import { fail, writeJsonArtifact } from "../lib/report.mjs";
import { fieldOf, indexAssetGuids, prefabText, readPartMap } from "../lib/unity.mjs";

const UNITS_PER_PIXEL = 20 / 768; // BPLE: 768 px = 20 world units (Sprite.cs camera height).

const BPLE = bpleProject();
const OUT = resolve(arg("out", dirname(MANIFEST)));
const ASSETS = assets(BPLE);
const GAMEOBJECT = gameObjects(BPLE);
const SCRIPTS = scriptAssembly(BPLE);

if (!existsSync(ASSETS)) {
  fail(`BPLE project not found: ${ASSETS}\nPass --bple <path to BPLE_Unity6>.`);
}

// ---------------------------------------------------------------- guid index

const guidToPath = indexAssetGuids(ASSETS, { suffix: "" });

function scriptGuid(scriptName) {
  const meta = join(SCRIPTS, `${scriptName}.cs.meta`);
  const match = /^guid: ([0-9a-f]{32})/m.exec(readFileSync(meta, "utf8"));
  return match[1];
}

const SPRITE_SCRIPT = scriptGuid("Sprite");
const UNMANAGED_SCRIPT = scriptGuid("UnmanagedSprite");
const NAMED_SCRIPT = scriptGuid("INSerializedSprite");
const SPRITE_ANIMATION_SCRIPT = scriptGuid("SpriteAnimation");
const FAN_PROPELLER_SCRIPT = scriptGuid("FanPropeller");
const PIG_SCRIPT = scriptGuid("Pig");
const KING_PIG_SCRIPT = scriptGuid("KingPig");
// Connection-visual hosts: the script a prefab mounts decides which rule its `*Attachment`
// or `*FrameSprite` nodes obey (see clients/web/src/renderer/connectionVisuals.ts).
const ROCKET_SCRIPT = scriptGuid("Rocket");
const TNT_SCRIPT = scriptGuid("TNT");
const BLASTER_TNT_SCRIPT = scriptGuid("BlasterTNT");
const SPOTLIGHT_SCRIPT = scriptGuid("SpotLight");
const GRAPPLING_HOOK_SCRIPT = scriptGuid("GrapplingHook");
const EXPLODING_GRAPPLING_HOOK_SCRIPT = scriptGuid("ExplodingGrapplingHook");
const WINGS_SCRIPT = scriptGuid("Wings");
// Prefab fields a part instantiates at run time: the sub-entity's art ships in its own prefab,
// referenced by guid (SpringBoxingGlove.cs:38 m_BoxingGlovePrefab -> BoxingGlove*.prefab).
const SUB_ENTITY_PREFAB_FIELDS = ["m_BoxingGlovePrefab"];

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
  const gameObjects = new Map();
  const transforms = new Map();
  const renderers = new Map();
  // component file id -> the GameObject it belongs to, so a serialized reference to a component
  // (Rocket.m_content points at the MeshRenderer, not at the Sprite component) resolves to the
  // node -- and from there to the extracted sprite -- the same way the game resolves it.
  const components = new Map();
  for (const { classId, fileId, body } of blocks) {
    const owner = /m_GameObject: \{fileID: (\d+)\}/.exec(body)?.[1];
    if (owner) components.set(fileId, owner);
    if (classId === 1) {
      gameObjects.set(fileId, { name: fieldOf(body, "m_Name"), active: fieldOf(body, "m_IsActive") !== "0" });
    } else if (classId === 4) {
      const pos = /m_LocalPosition: \{x: ([-\d.eE+]+), y: ([-\d.eE+]+), z: ([-\d.eE+]+)\}/.exec(body);
      const rot = /m_LocalRotation: \{x: ([-\d.eE+]+), y: ([-\d.eE+]+), z: ([-\d.eE+]+), w: ([-\d.eE+]+)\}/.exec(body);
      const scale = /m_LocalScale: \{x: ([-\d.eE+]+), y: ([-\d.eE+]+), z: ([-\d.eE+]+)\}/.exec(body);
      transforms.set(fileId, {
        gameObject: /m_GameObject: \{fileID: (\d+)\}/.exec(body)?.[1],
        father: /m_Father: \{fileID: (\d+)\}/.exec(body)?.[1],
        pos: pos ? [Number(pos[1]), Number(pos[2]), Number(pos[3])] : [0, 0, 0],
        rot: rot ? [Number(rot[1]), Number(rot[2]), Number(rot[3]), Number(rot[4])] : [0, 0, 0, 1],
        scale: scale ? [Number(scale[1]), Number(scale[2]), Number(scale[3])] : [1, 1, 1],
      });
    } else if (classId === 23) {
      const gameObject = owner;
      const material = /^\s*- \{fileID: 2100000, guid: ([0-9a-f]{32}), type: 2\}/m.exec(body)?.[1];
      if (gameObject) renderers.set(gameObject, material);
    }
  }
  // GameObject -> its transform file id
  const transformByGameObject = new Map();
  for (const [fileId, transform] of transforms) {
    if (transform.gameObject) transformByGameObject.set(transform.gameObject, fileId);
  }
  // Every MonoBehaviour, so the animation pass can read SpriteAnimation/FanPropeller/Pig next
  // to the sprite components (the sprite list below is a filter over the same blocks).
  const behaviours = [];
  for (const { classId, body } of blocks) {
    if (classId !== 114) continue;
    const script = /m_Script: \{fileID: \d+, guid: ([0-9a-f]{32})/.exec(body)?.[1];
    const gameObject = /m_GameObject: \{fileID: (\d+)\}/.exec(body)?.[1];
    if (!script || !gameObject) continue;
    behaviours.push({
      script,
      gameObject,
      body,
      fields: Object.fromEntries(
        [...body.matchAll(/^\s*(m_[A-Za-z0-9_]+):\s*(.*)$/gm)].map((m) => [m[1], m[2].trim()]),
      ),
    });
  }
  const sprites = behaviours
    .filter(({ script }) => script === SPRITE_SCRIPT || script === UNMANAGED_SCRIPT || script === NAMED_SCRIPT)
    .map(({ script, gameObject, fields }) => ({
      kind: script === SPRITE_SCRIPT ? "sprite" : script === UNMANAGED_SCRIPT ? "grid" : "named",
      gameObject,
      fields,
    }));
  return { gameObjects, transforms, transformByGameObject, renderers, components, sprites, behaviours };
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

/** FanPropeller.cs:92,108-112 with `powerFactor` 1 (`1000 * powerFactor + 700`): the original
 * derives its maximum speed from the engine's power factor instead of serializing it. */
const FAN_SPIN_DEGREES_PER_SECOND = 1700;
/** `FrameTiming.time` default (SpriteAnimation.cs:37) for a frame that carries no time. */
const DEFAULT_FRAME_SECONDS = 0.2;
/**
 * Pig expression thresholds. The original compares absolute m/s (`speedFunThreshold` 8,
 * `speedFearThreshold` 14 — Pig.cs:416-424, `Part_Pig_01_SET.prefab:443-445`), which PigForge
 * crosses within a tenth of a second because it has no original motor speed limit; these are
 * the calibrated ratios of `vRef` instead (spec "阈值标定（实测）"). Parts may tune them.
 */
const PIG_EXPRESSION = {
  speedFunRatio: 0.15,
  speedFearfulRatio: 0.3,
  speedFearRatio: 0.5,
  speedReference: 20,
  hitDeltaV: 5,
};
/** `Pig.fallFearThreshold` fallback for a pig prefab that omits it (Pig.cs:41). */
const DEFAULT_FALL_FEAR_THRESHOLD = 3;

/**
 * Node name -> the connection side a conditional sprite stands for. The label is part-local:
 * `Rocket.ChangeVisualConnections` (Rocket.cs:149-156) asks `CanConnectTo(Rotate(Direction.Up,
 * m_gridRotation))` for `TopAttachment`, so the client rotates the label into the grid with the
 * entity's yaw. Sides come from Rocket.cs:149-156, TNT.cs:88-106, SpotLight.cs:84-100 and
 * GrapplingHook.cs:233-249; the two mounts from Wings.cs:56-59.
 */
const CONDITION_NODES = new Map([
  ["TopAttachment", { kind: "attachment", side: "top" }],
  ["BottomAttachment", { kind: "attachment", side: "bottom" }],
  ["LeftAttachment", { kind: "attachment", side: "left" }],
  ["RightAttachment", { kind: "attachment", side: "right" }],
  ["TopLeftAttachment", { kind: "attachment", side: "topLeft" }],
  ["TopRightAttachment", { kind: "attachment", side: "topRight" }],
  ["BottomLeftAttachment", { kind: "attachment", side: "bottomLeft" }],
  ["BottomRightAttachment", { kind: "attachment", side: "bottomRight" }],
  ["TopFrameSprite", { kind: "frame", mount: "top" }],
  ["BottomFrameSprite", { kind: "frame", mount: "bottom" }],
]);

/**
 * Host script -> the visibility rule it implements. `BlasterTNT` inherits `TNT`'s rule
 * (BlasterTNT.cs:4); the two grappling hooks share `GrapplingHook`'s eight-side rule with the
 * spring node dropped (GrapplingHook.cs:218-255, ExplodingGrapplingHook.cs:75-110). Wings and
 * JetEngine share the mount rule (Wings.cs:41-72, JetEngine.cs:202-214); JetEngine maps to no
 * PigForge part. A host outside this table keeps no rule: its conditional sprites stay in the
 * manifest but the client draws them nowhere, exactly as before this change.
 */
const CONNECTION_VISUAL_SCRIPTS = new Map([
  [ROCKET_SCRIPT, "attachmentFallback"],
  [TNT_SCRIPT, "attachmentPlain"],
  [BLASTER_TNT_SCRIPT, "attachmentPlain"],
  [SPOTLIGHT_SCRIPT, "attachmentEight"],
  [GRAPPLING_HOOK_SCRIPT, "attachmentEight"],
  [EXPLODING_GRAPPLING_HOOK_SCRIPT, "attachmentEight"],
  [WINGS_SCRIPT, "frame"],
]);

/** The rule a prefab's conditional sprites obey, taken from the script it mounts. */
function connectionVisualOf(prefab) {
  for (const behaviour of prefab.behaviours) {
    const visual = CONNECTION_VISUAL_SCRIPTS.get(behaviour.script);
    if (visual) return visual;
  }
  return undefined;
}

// ------------------------------------------------------- activation animation

/**
 * `Rocket.m_visualization` is resolved at run time by name (`Awake` does
 * `transform.Find("BottleVisualization")`, Rocket.cs:114-118), so the node is matched by name:
 * a plain Rocket/RedRocket prefab has none and jitters nothing.
 */
const BOTTLE_VISUALIZATION_NODE = "BottleVisualization";
/** The cork is a child of the visualization node; the rocket reparents it out and calls
 * `Cork.Fly(-20 * transform.right, 200, 0.75)` when the ignition ends (Rocket.cs:255-262). */
const CORK_NODE = "Cork";
/** `Cork.Update` integrates `velocity + dt * 9.81 * down` and spins `dt * 200` degrees about
 * its own forward axis, then destroys the object once the lifetime runs out (Cork.cs:15-33). */
const CORK_FLIGHT = { speed: 20, spinDegreesPerSecond: 200, lifetime: 0.75 };
/** `Rocket.FixedUpdate` jitters the visualization inside a 0.1-radius circle every frame while
 * `num < m_ignitionTime` (`Random.insideUnitCircle * 0.1`, Rocket.cs:238-240) and resets it to
 * the node's own origin the moment the ignition ends (:250-252). */
const BOTTLE_JITTER_RADIUS = 0.1;
/**
 * `Rocket.Update` (Rocket.cs:202-226) cross-fades the two `BottleContent` sprites: `m_content`
 * 1 -> 0 and `m_content2` 0 -> 1 while the ignition runs, then `m_content2` 1 -> 0 once `num`
 * passes `m_ignitionTime` (it was seeded at 1 in `Awake`). Both alphas step by
 * `Time.deltaTime`, so each leg takes exactly one second.
 */
const BOTTLE_FADE_SECONDS = 1;
/** The Rocket component's two cross-faded sprite fields, in fade order. */
const BOTTLE_CONTENT_FIELDS = ["m_content", "m_content2"];
/** `BlasterTNT.m_blasterSprite = transform.Find("BlasterSprite")` (BlasterTNT.cs:79). The node
 * has no Sprite component -- its art is a quad with a standalone texture -- and the prefab parks
 * it inactive; the blast is what turns it on. */
const BLASTER_SPRITE_NODE = "BlasterSprite";
/**
 * `BlasterTNT.ExplodeSpecial` seeds a `BlasterInfo` (BlasterTNT.cs:123-140) and `FixedUpdate`
 * grows the ring for two seconds before hiding it (:163-169, :219-227): the radius starts at 0.5
 * and grows at 160 m/s against 0.2 drag, integrated in fixed 0.02 s steps (`DeltaTime`), the quad
 * is scaled to `2 * radius` world units, and the alpha is `min(64 / radius^2, 0.25)`.
 */
const BLASTER_RING = {
  startRadius: 0.5,
  radiusVelocity: 160,
  radiusDrag: 0.2,
  stepSeconds: 0.02,
  alphaNumerator: 64,
  alphaCap: 0.25,
  seconds: 2,
};

/** Transform id of the first node with this name: the run-time `transform.Find` lookups the
 * original makes are not serialized in the prefab. */
function transformByName(prefab, name) {
  for (const [gameObjectId, gameObject] of prefab.gameObjects) {
    if (gameObject.name === name) return prefab.transformByGameObject.get(gameObjectId);
  }
  return undefined;
}

/** The extracted sprite sitting on the node a serialized component reference points at
 * (`Rocket.m_content` references the content node's MeshRenderer, not its Sprite component). */
function spriteOfReference(prefab, sprites, reference) {
  const componentId = /\{fileID: (\d+)\}/.exec(reference ?? "")?.[1];
  const gameObjectId = componentId === undefined ? undefined : prefab.components.get(componentId);
  return gameObjectId === undefined ? undefined : sprites.find((sprite) => sprite.gameObject === gameObjectId);
}

/**
 * The bottle family's activation animation: the ignition jitter of the `BottleVisualization`
 * subtree, the cross-fade of its two `BottleContent` sprites, and the cork's launch. Every
 * reference here is a sprite object; `emitActivation` turns them into paint-order indices once
 * the sprite order is final.
 */
function bottleActivation(prefab, sprites, prefabName) {
  const rocket = prefab.behaviours.find((behaviour) => behaviour.script === ROCKET_SCRIPT);
  if (!rocket) return undefined;
  const visualizationId = transformByName(prefab, BOTTLE_VISUALIZATION_NODE);
  if (visualizationId === undefined) return undefined;
  const jittered = sprites.filter((sprite) => sprite.node.chain.includes(visualizationId));
  const contents = BOTTLE_CONTENT_FIELDS.map((field) => spriteOfReference(prefab, sprites, rocket.fields[field]));
  if (jittered.length === 0 || contents.some((sprite) => sprite === undefined)) {
    warnings.push(`${prefabName}: bottle visualization without its sprites or contents`);
    return undefined;
  }
  const ignition = Number(rocket.fields.m_ignitionTime);
  const ignitionSeconds = Number.isFinite(ignition) && ignition > 0 ? ignition : 1;
  const [content, content2] = contents;
  const corkId = transformByName(prefab, CORK_NODE);
  const cork = corkId === undefined ? undefined : jittered.find((sprite) => sprite.node.chain.includes(corkId));
  return {
    jitter: { sprites: jittered, radius: BOTTLE_JITTER_RADIUS, seconds: ignitionSeconds },
    fade: [
      { sprite: content, from: 1, to: 0, start: 0, seconds: BOTTLE_FADE_SECONDS },
      { sprite: content2, from: 0, to: 1, start: 0, seconds: BOTTLE_FADE_SECONDS },
      { sprite: content2, from: 1, to: 0, start: ignitionSeconds, seconds: BOTTLE_FADE_SECONDS },
    ],
    // The launch coincides with the end of the ignition: `FixedUpdate` resets the visualization's
    // local position and reparents the cork in the same `num > m_ignitionTime` branch (:247-262).
    ...(cork ? { launch: { sprite: cork, start: ignitionSeconds, ...CORK_FLIGHT } } : {}),
    seconds: ignitionSeconds + BOTTLE_FADE_SECONDS,
  };
}

/**
 * The BlasterTNT's expanding ring. The `BlasterSprite` node carries no Sprite component, so its
 * art never enters the sprite list: it is a built-in quad whose material points at a standalone
 * texture, drawn at the blast radius. Its own image is registered as an atlas so the client can
 * load it, and the whole texture is the rect (the quad's UVs cover all of it).
 */
function blasterActivation(prefab, prefabName) {
  if (!prefab.behaviours.some((behaviour) => behaviour.script === BLASTER_TNT_SCRIPT)) return undefined;
  const nodeId = transformByName(prefab, BLASTER_SPRITE_NODE);
  const gameObjectId = nodeId === undefined ? undefined : prefab.transforms.get(nodeId)?.gameObject;
  const materialGuid = gameObjectId === undefined ? undefined : prefab.renderers.get(gameObjectId);
  const texturePath = materialGuid ? textureOfMaterial(materialGuid) : undefined;
  const size = texturePath ? pngSize(texturePath) : undefined;
  if (!texturePath || !size) {
    warnings.push(`${prefabName}: BlasterSprite without a texture`);
    return undefined;
  }
  const atlas = texturePath.split(/[\\/]/).pop();
  usedAtlases.set(atlas, { size, path: texturePath });
  return {
    ring: { atlas, x: 0, y: 0, w: size.width, h: size.height, ...BLASTER_RING },
    seconds: BLASTER_RING.seconds,
  };
}

/** One activation descriptor with every sprite reference turned into the index it has in the
 * emitted sprite array (paint order). Indices stay in `emit`'s rounding frame: they are integers,
 * and a reference the dedupe dropped invalidates the whole descriptor instead of shifting it. */
function emitActivation(activation, sprites, prefabName) {
  const indexOf = (sprite) => sprites.indexOf(sprite);
  const round = (value) => Math.round(value * 1e4) / 1e4;
  const indices = [
    ...(activation.jitter?.sprites ?? []),
    ...(activation.fade ?? []).map((leg) => leg.sprite),
    ...(activation.launch ? [activation.launch.sprite] : []),
  ].map(indexOf);
  if (indices.some((index) => index < 0)) {
    warnings.push(`${prefabName}: activation references a sprite the manifest dropped`);
    return undefined;
  }
  return {
    seconds: round(activation.seconds),
    ...(activation.jitter
      ? {
          jitter: {
            sprites: activation.jitter.sprites.map(indexOf),
            radius: round(activation.jitter.radius),
            seconds: round(activation.jitter.seconds),
          },
        }
      : {}),
    ...(activation.fade
      ? {
          fade: activation.fade.map((leg) => ({
            sprite: indexOf(leg.sprite),
            from: leg.from,
            to: leg.to,
            start: round(leg.start),
            seconds: round(leg.seconds),
          })),
        }
      : {}),
    ...(activation.launch
      ? {
          launch: {
            sprite: indexOf(activation.launch.sprite),
            start: round(activation.launch.start),
            speed: activation.launch.speed,
            spinDegreesPerSecond: activation.launch.spinDegreesPerSecond,
            lifetime: activation.launch.lifetime,
          },
        }
      : {}),
    ...(activation.ring ? { ring: activation.ring } : {}),
  };
}

/** Sprite centre offset from the prefab root, in BPLE world units (root's own offset
 * excluded), whether the sprite rides a node the original rotates, and the transform ids
 * from the sprite up to the root (the animation pass matches `m_fanVisualization` on them). */
function localOffset(prefab, gameObject) {
  let transformId = prefab.transformByGameObject.get(gameObject);
  const chain = [];
  const chainIds = [];
  while (transformId && prefab.transforms.has(transformId)) {
    const transform = prefab.transforms.get(transformId);
    chain.push(transform);
    chainIds.push(transformId);
    transformId = transform.father;
  }
  // Walk from the root down so each step's accumulated offset is that node's own offset
  // from the root (the sprite's own local position must not leak into the pivot).
  const position = [0, 0, 0];
  const scale = [1, 1, 1];
  let angle = 0;
  let pivot = null;
  for (let i = chain.length - 2; i >= 0; i -= 1) {
    const t = chain[i];
    position[0] += t.pos[0];
    position[1] += t.pos[1];
    position[2] += t.pos[2];
    scale[0] *= t.scale[0];
    scale[1] *= t.scale[1];
    scale[2] *= t.scale[2];
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
    scaleX: scale[0],
    scaleY: scale[1],
    spin: pivot !== null,
    pivot,
    chain: chainIds,
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

/**
 * The art one `Sprite` component row draws: its atlas rect, its quad size in source pixels,
 * and its centre offset from the node it hangs on, in BPLE world units. `Sprite.SelectSprite`/
 * `CreateMesh` rebuild the quad at runtime around the database pivot, offsetting it by
 * (selection centre - packed-rect centre + pivot) source pixels, so a node's local position
 * alone is NOT the artwork's centre — ignoring it stacks a wheel's tyre on its fork instead of
 * hanging it on the axle. UnmanagedSprite/INSerializedSprite centre their quads on the node and
 * carry no pivot, so only this path corrects.
 *
 * `fields` are the owning Sprite component's serialized fields and `rowId` the sprite-database
 * row to draw: a SpriteAnimation frame passes the frame's id with the same component's fields,
 * because the original rebuilds that one mesh in place and keeps its scales, pivots and node.
 */
function spriteRowArt(fields, rowId, offset, atlasSize, what) {
  const cell = spriteCells.get(rowId);
  const uv = spriteUv.get(rowId);
  if (!cell || !uv) {
    warnings.push(`${what}: sprite ${rowId} missing from sprites.txt/spritemapping.txt`);
    return undefined;
  }
  const [u, v, uw, uh] = uv;
  const x = Math.round(u * atlasSize.width);
  const yBottom = Math.round(v * atlasSize.height);
  const w = Math.round(uw * atlasSize.width);
  const h = Math.round(uh * atlasSize.height);
  const scaleX = Number(fields.m_scaleX ?? 1);
  const scaleY = Number(fields.m_scaleY ?? 1);
  const pivotOffsetX = cell.selectionX + cell.selectionWidth / 2 - (cell.uvX + cell.w / 2) + cell.pivotX + Number(fields.m_pivotX ?? 0);
  const pivotOffsetY = cell.selectionY + cell.selectionHeight / 2 - (cell.uvY + cell.h / 2) + cell.pivotY + Number(fields.m_pivotY ?? 0);
  // The mesh is built in its node's own frame, so the pivot offset is scaled and rotated by that
  // node's transform before it becomes an offset from the root — Unity applies scale, then
  // rotation, then translation. Skipping this put a joint-attachment bracket on the wrong hinge
  // radius as soon as a node carried a 90/180/270 degree rotation (every `*Attachment` does).
  const localX = -scaleX * pivotOffsetX * UNITS_PER_PIXEL;
  const localY = -scaleY * pivotOffsetY * UNITS_PER_PIXEL;
  const cos = Math.cos(offset.angle);
  const sin = Math.sin(offset.angle);
  const scaledX = localX * offset.scaleX;
  const scaledY = localY * offset.scaleY;
  return {
    rect: { x, y: atlasSize.height - yBottom - h, w, h },
    quadW: scaleX * cell.w * Math.abs(offset.scaleX),
    quadH: scaleY * cell.h * Math.abs(offset.scaleY),
    artX: offset.x + scaledX * cos - scaledY * sin,
    artY: offset.y + scaledX * sin + scaledY * cos,
  };
}

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
    quadW = Number(f.m_spriteWidth) * Math.abs(offset.scaleX);
    quadH = Number(f.m_spriteHeight) * Math.abs(offset.scaleY);
  } else if (sprite.kind === "named") {
    // INSerializedSprite: name -> Assets/TextAsset/<Atlas>_TextAsset.txt (top-left origin).
    const named = namedSprites.get(`${atlas.replace(/\.png$/, "")}\0${f.m_name}`);
    if (!named) {
      warnings.push(`named sprite '${f.m_name}' missing from the ${atlas} text asset`);
      return undefined;
    }
    rect = { x: named.x, y: named.y, w: named.w, h: named.h };
    quadW = named.w * named.scaleX * Math.abs(offset.scaleX);
    quadH = named.h * named.scaleY * Math.abs(offset.scaleY);
    unitsPerPixel = 20 / named.screenHeight;
  } else {
    const art = spriteRowArt(f, f.m_id, offset, size, prefab.gameObjects.get(sprite.gameObject)?.name ?? "sprite");
    if (!art) return undefined;
    rect = art.rect;
    quadW = art.quadW;
    quadH = art.quadH;
    artX = art.artX;
    artY = art.artY;
  }
  if (!(rect.w > 0 && rect.h > 0 && quadW > 0 && quadH > 0)) return undefined;
  const transformId = prefab.transformByGameObject.get(sprite.gameObject);
  usedAtlases.set(atlas, { size, path: texturePath });
  return {
    name: prefab.gameObjects.get(sprite.gameObject)?.name ?? "",
    root: prefab.transforms.get(transformId)?.father === "0",
    rotates: offset.spin,
    pivot: offset.pivot,
    // Internal — the animation pass resolves fan blades and frame animations from these:
    // the node it hangs on with its offset and prefab chain, and its own Sprite component
    // fields (a frame reuses the row's scales and pivots). The emitter drops them.
    gameObject: sprite.gameObject,
    node: offset,
    fields: f,
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
    // Unity mirrors a node's whole subtree through a negative transform scale; the renderer
    // applies it as a canvas scale so the art is not silently flipped away.
    flipX: offset.scaleX < 0,
    flipY: offset.scaleY < 0,
    z: offset.z,
  };
}

/**
 * `SpriteAnimation.m_animations` (SpriteAnimation.cs:12-31): named clips, each a list of
 * `{ id, time }` frames. Read line by line because the serialized block is nested YAML: it
 * holds two-space list items with deeper fields, and everything else is the next field of the
 * component (`m_childAnimations`, `m_AutoPlay`, ...).
 */
function parseAnimations(body) {
  const lines = body.split("\n");
  const start = lines.findIndex((line) => line.trim() === "m_animations:");
  if (start < 0) return [];
  const animations = [];
  for (let index = start + 1; index < lines.length; index += 1) {
    const line = lines[index];
    if (!line.startsWith("    ") && !line.startsWith("  - ")) break;
    const field = /^\s*(?:- )?([A-Za-z0-9_]+):\s*(.*)$/.exec(line);
    if (!field) continue;
    const [, name, value] = field;
    if (name === "name") {
      animations.push({ name: value, loop: false, frames: [] });
      continue;
    }
    if (animations.length === 0) continue;
    const animation = animations[animations.length - 1];
    if (name === "loop") {
      animation.loop = value === "1";
    } else if (name === "id") {
      animation.frames.push({ id: value, seconds: 0 });
    } else if (name === "time") {
      animation.frames[animation.frames.length - 1].seconds = Number(value);
    }
  }
  return animations.filter((animation) => animation.name.length > 0 && animation.frames.length > 0);
}

/**
 * One clip frame as the original builds it: the frame id's own sprite-database row drawn by the
 * owning Sprite component, so the node, material, scales and pivots stay the component's and
 * only the rect and quad size come from the frame (`SpriteAnimation.InitializeAnimations` calls
 * `SelectSprite(frameId)`, which rebuilds that component's mesh — SpriteAnimation.cs:196-215).
 */
function frameSprite(owner, clipName, frame) {
  const atlas = usedAtlases.get(owner.atlas);
  const art = atlas && spriteRowArt(owner.fields, frame.id, owner.node, atlas.size, `${owner.name} ${clipName}`);
  if (!art || !(art.rect.w > 0 && art.rect.h > 0 && art.quadW > 0 && art.quadH > 0)) return undefined;
  return {
    atlas: owner.atlas,
    x: art.rect.x,
    y: art.rect.y,
    w: art.rect.w,
    h: art.rect.h,
    cx: art.artX,
    cy: art.artY,
    sx: art.quadW * UNITS_PER_PIXEL,
    sy: art.quadH * UNITS_PER_PIXEL,
    rot: owner.rot,
    seconds: frame.seconds > 0 ? frame.seconds : DEFAULT_FRAME_SECONDS,
  };
}

/**
 * The node a fan, propeller or rotor turns and the axis it turns about: the original writes
 * `m_fanVisualization.localRotation` and compresses the blades' scale by |cos(angle)| every
 * frame (FanPropeller.cs:120-142, 319-324). `m_isRotor` picks up for a rotor and right for the
 * rest (`Part_Rotor_01_SET.prefab` `m_isRotor: 1`).
 */
function fanSpin(prefab) {
  const fan = prefab.behaviours.find((behaviour) => behaviour.script === FAN_PROPELLER_SCRIPT);
  if (!fan) return undefined;
  const nodeId = /\{fileID: (\d+)\}/.exec(fan.fields.m_fanVisualization ?? "")?.[1];
  if (!nodeId) {
    warnings.push(`FanPropeller without m_fanVisualization on ${prefab.gameObjects.get(fan.gameObject)?.name}`);
    return undefined;
  }
  return { nodeId, axis: fan.fields.m_isRotor === "1" ? "y" : "x", maxDegreesPerSecond: FAN_SPIN_DEGREES_PER_SECOND };
}

/** The expression block of a pig: only a Pig/KingPig component runs the expression machine. */
function pigExpression(prefab) {
  const pig = prefab.behaviours.find((behaviour) => behaviour.script === PIG_SCRIPT || behaviour.script === KING_PIG_SCRIPT);
  if (!pig) return undefined;
  const fall = Number(pig.fields.fallFearThreshold);
  return { ...PIG_EXPRESSION, fallFearThreshold: Number.isFinite(fall) && fall > 0 ? fall : DEFAULT_FALL_FEAR_THRESHOLD };
}

function extractPart(prefabName) {
  const text = prefabText(GAMEOBJECT, prefabName);
  if (text === null) {
    warnings.push(`prefab missing: ${prefabName}`);
    return undefined;
  }
  const prefab = parsePrefab(text);
  const found = prefab.sprites.map((s) => extractSprite(prefab, s)).filter(Boolean);
  if (found.length === 0) {
    warnings.push(`no extractable sprite in ${prefabName}`);
    return undefined;
  }
  // Joint attachment markers and the two wing mounts are conditional in-game: the original
  // shows each only when the matching side can connect (`ChangeVisualConnections`), so they
  // are kept and tagged instead of dropped, and the client gates them on the neighbour state
  // it derives from the layout. Every other sprite is part of the visual (body, face, crown,
  // wheel rim, light cone, extra balloons/sandbags) and stays unconditional.
  const seen = new Set();
  const sprites = found.filter((s) => {
    const key = `${s.atlas}|${s.x}|${s.y}|${s.w}|${s.h}|${s.cx}|${s.cy}|${s.rot}`;
    if (seen.has(key)) return false;
    seen.add(key);
    return true;
  });
  const connectionVisual = connectionVisualOf(prefab);
  for (const sprite of sprites) {
    const condition = CONDITION_NODES.get(sprite.name);
    if (condition) sprite.condition = condition;
  }
  if (connectionVisual === undefined && sprites.some((sprite) => sprite.condition)) {
    // The client draws conditional sprites only for a known rule, so an unlisted host keeps
    // its markers hidden exactly as they were before they entered the manifest.
    warnings.push(`${prefabName} has conditional sprites but no host script rule`);
  }
  // Fan blades: the sprites hanging on the node the FanPropeller turns get the spin
  // descriptor (the compressor reads their axis and speed). Wheels get none — their sprites
  // ride a hinged body whose snapshot rotation is the roll (ADR-008/009).
  const fan = fanSpin(prefab);
  if (fan) {
    for (const sprite of sprites) {
      // The FanPropeller's node is the authority on what turns in a fan prefab: the name-based
      // flag alone both misses a differently named node it does drive (Rotor_09's blades) and
      // marks a same-named node it never touches (that prefab's hub).
      sprite.rotates = sprite.node.chain.includes(fan.nodeId);
      sprite.spin =
        sprite.rotates ? { axis: fan.axis, maxDegreesPerSecond: fan.maxDegreesPerSecond } : undefined;
    }
    if (!sprites.some((sprite) => sprite.spin)) {
      warnings.push(`no sprite under the fan node ${fan.nodeId} in ${prefabName}`);
    }
  }
  // Original frame animations: the Sprite component that also drives SpriteAnimation swaps the
  // mesh of that very node through the clip's frames (SpriteAnimation.cs:196-215), so the clips
  // ride that sprite and every child animation with the same clip names follows it in lockstep.
  for (const behaviour of prefab.behaviours) {
    if (behaviour.script !== SPRITE_ANIMATION_SCRIPT) continue;
    const owner = sprites.find((sprite) => sprite.gameObject === behaviour.gameObject);
    if (!owner) {
      warnings.push(`SpriteAnimation without an extracted sprite in ${prefabName}`);
      continue;
    }
    const clips = {};
    for (const animation of parseAnimations(behaviour.body)) {
      const frames = animation.frames.map((frame) => frameSprite(owner, animation.name, frame)).filter(Boolean);
      if (frames.length !== animation.frames.length) {
        warnings.push(`${owner.name}: clip ${animation.name} dropped ${animation.frames.length - frames.length} frame(s)`);
      }
      if (frames.length > 0) clips[animation.name] = { loop: animation.loop, frames };
    }
    if (Object.keys(clips).length > 0) owner.clips = clips;
  }
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
  const expression = pigExpression(prefab);
  // Activation-time animation (schemaVersion 6): what the original does to a *placed* part's art
  // the moment its switch fires -- a bottle's ignition jitter, cross-fade and cork, a blaster's
  // expanding ring. Read after the sort, because the descriptor addresses sprites by their
  // paint-order index.
  const activationSource = bottleActivation(prefab, sprites, prefabName) ?? blasterActivation(prefab, prefabName);
  const activation = activationSource ? emitActivation(activationSource, sprites, prefabName) : undefined;
  // Everything below stays in the PART-ORIGIN frame (`localOffset`'s accumulation), because that
  // is the frame the wire uses: `PGFS` carries the part's own origin (a lone member's body pose is
  // its shape centre, and the room re-bases each entity by the member's local offset), so a sprite
  // offset measured from the part origin lands exactly where the original's own node chain puts it.
  //
  // This used to re-base every offset onto the composite's art centre instead. That looked
  // equivalent only while a part's art happened to be centred on its origin: the king pig's crown
  // lifts its art centre 0.63 above the part origin, so its art was drawn 0.63 below its collision
  // (the reported "collision box and texture are misaligned"). Palette thumbnails do not depend on
  // this frame -- `thumbnailPlacements` fits the composite's own bounds.
  const emit = (s) => ({
    atlas: s.atlas,
    x: s.x,
    y: s.y,
    w: s.w,
    h: s.h,
    cx: round(s.cx),
    cy: round(s.cy),
    sx: round(s.sx),
    sy: round(s.sy),
    rot: round(s.rot),
    rotates: s.rotates,
    ...(s.flipX ? { flipX: true } : {}),
    ...(s.flipY ? { flipY: true } : {}),
    ...(s.condition ? { condition: s.condition } : {}),
    ...(s.spin ? { spin: s.spin } : {}),
    ...(s.clips
      ? {
          clips: Object.fromEntries(
            Object.entries(s.clips).map(([name, clip]) => [
              name,
              {
                loop: clip.loop,
                frames: clip.frames.map((frame) => ({
                  atlas: frame.atlas,
                  x: frame.x,
                  y: frame.y,
                  w: frame.w,
                  h: frame.h,
                  cx: round(frame.cx),
                  cy: round(frame.cy),
                  sx: round(frame.sx),
                  sy: round(frame.sy),
                  rot: round(frame.rot),
                  seconds: frame.seconds,
                })),
              },
            ]),
          ),
        }
      : {}),
  });
  const subSprites = extractSubEntityPrefab(prefab, prefabName);
  return {
    bbox: [round(bbox[0]), round(bbox[1])],
    ...(pivot ? { pivot: [round(pivot[0]), round(pivot[1])] } : {}),
    ...(expression ? { expression } : {}),
    ...(connectionVisual ? { connectionVisual } : {}),
    ...(activation ? { activation } : {}),
    ...(subSprites ? { subSprites: subSprites.map(emit) } : {}),
    sprites: sprites.map(emit),
  };
}

/**
 * The art of the prefab a part instantiates at run time (`m_BoxingGlovePrefab` on
 * SpringBoxingGlove.cs:38, the reference is a guid). The sub-entity borrows its host's part type
 * on the wire, so without this list a client would draw the host's own composite over it -- the
 * reported "the glove box pops out something that is not a fist". Offsets stay in the sub-entity's
 * own origin frame, exactly like a part's (`localOffset`).
 */
function extractSubEntityPrefab(prefab, prefabName) {
  for (const behaviour of prefab.behaviours) {
    for (const field of SUB_ENTITY_PREFAB_FIELDS) {
      const reference = behaviour.fields[field];
      if (!reference) continue;
      const guid = /\{fileID: \d+, guid: ([0-9a-f]{32})/.exec(reference)?.[1];
      const path = guid ? guidToPath.get(guid) : undefined;
      if (!path || !existsSync(path)) {
        warnings.push(`${prefabName}: ${field} points at a prefab the index does not have`);
        return undefined;
      }
      const subPrefab = parsePrefab(readFileSync(path, "utf8"));
      const sprites = subPrefab.sprites.map((s) => extractSprite(subPrefab, s)).filter(Boolean);
      if (sprites.length === 0) {
        warnings.push(`${prefabName}: sub-entity prefab ${basename(path)} has no extractable sprite`);
        return undefined;
      }
      // Same paint order as a part's own sprites: far to near (see the note on `sprites.sort`).
      sprites.sort((a, b) => b.z - a.z);
      return sprites;
    }
  }
  return undefined;
}

// ---------------------------------------------------------------------- main

const map = readPartMap();
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
  // v3 adds the optional animation descriptors (sprite `spin`/`clips`, part `expression`);
  // v4 adds the optional connection conditions (sprite `condition`, part `connectionVisual`).
  // v5 adds the optional sub-entity art (part `subSprites`, the prefab a part instantiates at
  // run time -- a boxing glove's fist).
  // v6 adds the optional activation-time animation (part `activation`: the bottle family's
  // ignition jitter, cross-fade and cork launch, plus the blaster's expanding ring).
  // Every addition is optional, so each older manifest is the newer one minus that layer.
  schemaVersion: 6,
  source: basename(BPLE),
  unitsPerPixel: UNITS_PER_PIXEL,
  atlases: Object.fromEntries([...usedAtlases].map(([name, entry]) => [name, entry.size])),
  parts,
};

mkdirSync(OUT, { recursive: true });
for (const [atlas, entry] of usedAtlases) {
  copyFileSync(entry.path, join(OUT, atlas));
}
writeJsonArtifact(join(OUT, "part-textures.json"), manifest);

console.log(`bple:   ${BPLE}`);
console.log(`out:    ${OUT}`);
console.log(`parts:  ${mapped}/${Object.keys(assignments).length} mapped, ${Object.keys(parts).length} emitted`);
console.log(`atlas:  ${[...usedAtlases.keys()].join(", ")}`);
if (warnings.length) {
  console.log(`warnings (${warnings.length}):`);
  for (const warning of warnings) console.log(`  - ${warning}`);
}
