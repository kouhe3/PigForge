// The original's level props: which of the level palette's non-part prefabs are plain decoration
// quads, and the art each of those draws with.
//
// Two original components make such a quad, and both build the mesh at runtime (`MeshFilter.m_Mesh`
// is 0 in every prefab):
//
//   UnmanagedSprite.CreatePlane(w, h)          half extents = (w, h) * 10 / 768 world units, centred
//   UnmanagedSprite.CalculateUVs(x, y, w, h)   a grid cell of AtlasGridSubdivisions, inset by a
//                                              half texel of a 1024 atlas plus Border/1024
//   Sprite.SelectSprite / CreateMesh           size = (int)(scale * db.width/height) pixels, centre
//                                              offset = -2 * pivot * 10 / 768, uv = the sprite
//                                              library's own rect
//
// So the world size is `pixels * 20 / 768` (the parts' own `unitsPerPixel`) in both families, and
// every input is machine-read: the prefab's serialized fields, `Assets/Resources/GUISystem/sprites
// .txt` + `spritemapping.txt` (the runtime sprite library) and the `.mat` asset's `_MainTex`. Only
// the mesh size is taken from the prefab verbatim -- the editor-side recomputation
// (`ResetSize`/`OnDrawGizmos`) only fills `m_spriteWidth` when it is zero, so the serialized value
// is what a build ships (see `docs/specs/level-props.md` §7 deviation 1). A field Unity did not
// serialize falls back to the C# field initializer, which is where the defaults below come from.

import { existsSync, readFileSync } from "node:fs";
import { basename, join } from "node:path";
import { assets, scriptAssembly } from "./paths.mjs";
import { buildGuidIndex, fieldOf, indexAssetGuids } from "./unity.mjs";

/// The original's pixels -> world units: 768 px is 20 world units (`Sprite.DefaultScreenHeight` over
/// `DefaultCameraHeight`, the same factor `tools/bple-textures` writes as `unitsPerPixel`).
const UNITS_PER_PIXEL = 20 / 768;

/// `UnmanagedSprite.CalculateUVs`' `num2 = 0.00048828125f`, a half texel of the 1024 atlas the
/// component's constants assume.
const HALF_TEXEL = 1 / 2048;

/// The classes that mean "this prefab is a sprite quad". A prop carrying any other script is not a
/// decoration and is left to another slice.
const SPRITE_CLASSES = ["Sprite", "UnmanagedSprite"];

/// Scripts a drawable prop may carry besides its own sprite components: they either add no geometry
/// (`PointLightSource` a glow this slice does not model; `DynamicObject` is an *empty* marker class,
/// `DynamicObject.cs:3-5`) or only act when something calls them at runtime
/// (`SpriteAnimation.OnEnable` plays only with `m_AutoPlay` set and a name -- every prefab this tool
/// accepts has neither, so the `Sprite` component's own `m_id` is the picture; `AnimationHandler.Update`
/// is empty; `Bird`/`FaceRotation`/`FixedRotation` are the bird's flight behaviour; `Slingshot` builds
/// two runtime `LineRenderer`s and leaves the sprites to its children, and `bandQuads` models those).
const ALLOWED_EXTRA = [
  "PointLightSource",
  "DynamicObject",
  "SpriteAnimation",
  "AnimationHandler",
  "Bird",
  "FaceRotation",
  "FixedRotation",
  "Slingshot",
];

/// The window props' own pair: `Panel` builds an `m_width x m_height` tile mesh out of the sprite the
/// `SpriteReference` beside it names (`Panel.Rebuild`), and `SpriteReference.RenderingEnabled()` is
/// false (`SpriteReference.cs:3-6`) so the plain `Sprite` component that also sits on those objects --
/// and the one quad it writes to the same `MeshFilter` first -- is not what stays on screen.
const PANEL_CLASSES = ["Panel", "SpriteReference"];

/// Scripts the level's own *solid* furniture may carry: a crate, a wall, a plank, a door, a fan, the
/// goal's flag, a secret, a button, a challenge box. Each was read for what it does between `Awake` and
/// the first frame, and each leaves the prefab's own pose alone there:
///
///   `TNTBox`/`DynamicTNTBox`  art changes only when the charge blows (`TNTBox.cs:125-154`)
///   `LevelRigidbody`          `Awake` reads joints and a material, nothing visual (`LevelRigidbody.cs:93`)
///   `RigidbodyFreezer`        `Start` collects rigidbodies and their constraints (`RigidbodyFreezer.cs:19`)
///   `RigidbodyRotationConstraints`  a constraint, no renderer
///   `Fan`                     `Awake` caches `fanSprite`; the blades turn only while it is blowing
///   `ActivatedHingeDoor`      no `Awake`/`Start` at all -- it turns when the level activates it
///   `GoalBox`                 `Start` reads a material and a light (`GoalBox.cs:36-42`)
///   `SecretPlace`/`SecretStatue`  hide only when the save already found the secret
///   `PartBox`                 adds a *disabled* part preview child (`PartBox.cs:36-47`)
///   `PartSecret`              destroys itself only when the save already collected it
///   `PressureButton`          the button's own child is toggled by its press handler, not at load
///
/// The families whose art is *generated* at load (`AncestorPig`'s autoplaying eyes, `LevelBalloon`'s and
/// `RopeVisualization`'s rope and balloon, `RandomChildActivator`'s pick of one goal child) stay out, and
/// so do the challenge boxes (`Collectable`/`WaypointChallenge`/`StarBox`) -- those are P5's own slice,
/// where the art arrives together with the collecting it stands for.
const SOLID_CLASSES = [
  "TNTBox",
  "DynamicTNTBox",
  "LevelRigidbody",
  "RigidbodyFreezer",
  "RigidbodyRotationConstraints",
  "Fan",
  "ActivatedHingeDoor",
  "GoalBox",
  "SecretPlace",
  "SecretStatue",
  "PartBox",
  "PartSecret",
  "PressureButton",
];

/// The component classes that make a prop physical, by Unity's class id. The picture never depends on
/// them; the census does (a background set carries the level's own ground collider, `TNT_Box` its crate),
/// and `solid boxes` are written out of the box colliders among them.
const COLLIDER_IDS = { 64: "MeshCollider", 65: "BoxCollider", 68: "SphereCollider", 135: "CapsuleCollider", 136: "CapsuleCollider" };

/// The frame the level instance itself establishes: its own transform is what
/// `LevelLoader.ReadPrefabInstance` writes over the prefab root's.
const IDENTITY_FRAME = [1, 0, 0, 1, 0, 0];

/**
 * The id a prop is named by in the level content and the client's manifest: the prefab's name with
 * whitespace folded to underscores, because a content identifier may not contain any (two of the
 * backdrop sets are `background_cave_01_set 1` and `background_night_01_set 1` -- the original's own
 * file names). Both tools go through here, and the extractor asserts the ids stay unique.
 */
export const propId = (prefabName) => prefabName.replace(/\s+/g, "_");

/// Unity's class ids, as far as the props need them.
const CLASS_IDS = {
  1: "GameObject",
  23: "MeshRenderer",
  64: "MeshCollider",
  65: "BoxCollider",
  114: "MonoBehaviour",
  135: "SphereCollider",
  136: "CapsuleCollider",
  212: "SpriteRenderer",
};

/// The serialized fields each sprite component needs, with the C# field initializer as the default
/// (a prefab saved before a field existed does not carry it).
const UNMANAGED_FIELDS = { m_scale: 1, m_spriteWidth: 0, m_spriteHeight: 0, m_UVx: 0, m_UVy: 0, m_width: 16, m_height: 16, m_atlasGridSubdivisions: 16, m_border: 0 };
const MANAGED_FIELDS = { m_scaleX: 1, m_scaleY: 1, m_pivotX: 0, m_pivotY: 0 };

/// `ParallaxManager.Update`'s per-layer factors, by the tag the manager finds the layer with
/// (`GameObject.FindGameObjectsWithTag`). `x` is how much of the camera's own horizontal movement the
/// layer takes with it -- 1 means "locked to the screen" -- and `y` the same vertically (only the
/// foreground follows, at 0.2, and only downwards from its own start height).
const PARALLAX_TAGS = {
  ParallaxLayerForeground: { x: -0.4, y: 0.2 },
  ParallaxLayerFurther: { x: 0.7, y: 0 },
  ParallaxLayerFar: { x: 0.6, y: 0 },
  ParallaxLayerNear: { x: 0.4, y: 0 },
  ParallaxLayerSky: { x: 0.8, y: 0 },
  ParallaxLayerFixedFollowCamera: { x: 1, y: 0 },
};

/// A component block's own header: `--- !u!<class> &<fileID>`, split off by the callers.
const BLOCK_HEADER = /^(\d+) &(\d+)/;

/** The `<fileID>` of a `{fileID: N}` reference field, or null. */
function referenceOf(body, field) {
  const match = new RegExp(`^\\s*${field}: \\{fileID: (\\d+)\\}`, "m").exec(body);
  return match === null ? null : match[1];
}

/** The `<guid>` of an asset reference field (`{fileID: N, guid: G, type: T}`), or null. */
function assetReferenceOf(body, field) {
  const match = new RegExp(`^\\s*${field}: \\{fileID: \\d+, guid: ([0-9a-f]{32})`, "m").exec(body);
  return match === null ? null : match[1];
}

/** The numbers of a `{x: .., y: ..}`-style field (Unity writes them on one line). */
function numbersOf(body, field) {
  const match = new RegExp(`^\\s*${field}: \\{([^}]*)\\}`, "m").exec(body);
  if (match === null) return [];
  return [...match[1].matchAll(/(-?[0-9.eE+-]+)/g)].map((entry) => Number(entry[1]));
}

/**
 * One prefab as the props need it: every GameObject with its tag, active flag, component classes and
 * Transform, keyed by the fileID the GameObject's `m_Component` list points at. The root's own
 * local transform is kept out of the way: `LevelLoader.ReadPrefabInstance` overwrites it with the
 * level instance's transform, so only the children's own frames matter.
 */
function readHierarchy(text, guidToClass) {
  const blocks = text.split(/\n--- !u!/).slice(1).map((body) => {
    const header = BLOCK_HEADER.exec(body);
    return { classId: Number(header?.[1]), anchor: header?.[2] ?? null, body };
  });
  const byAnchor = new Map(blocks.map((block) => [block.anchor, block]));
  const objects = [];
  for (const block of blocks) {
    if (block.classId !== 1) continue;
    const components = [...block.body.matchAll(/- component: \{fileID: (\d+)\}/g)].map((entry) => entry[1]);
    const parts = components.map((anchor) => byAnchor.get(anchor)).filter(Boolean);
    const transform = parts.find((part) => part.classId === 4);
    const classOf = (part) => guidToClass.get(/m_Script: \{fileID: \d+, guid: ([0-9a-f]{32})/.exec(part.body)?.[1]);
    objects.push({
      anchor: block.anchor,
      name: /^\s*m_Name: (.*)$/m.exec(block.body)?.[1]?.trim() ?? "",
      tag: /^\s*m_TagString: (.*)$/m.exec(block.body)?.[1]?.trim() ?? "",
      active: (/^\s*m_IsActive: (\d)/m.exec(block.body)?.[1] ?? "1") === "1",
      classes: parts.filter((part) => part.classId === 114).map((part) => classOf(part) ?? "?unknown"),
      sprite: parts.find((part) => part.classId === 114 && SPRITE_CLASSES.includes(classOf(part))),
      /// The window pair: `Panel` is the mesh generator, `SpriteReference` the sprite it reads.
      panel: parts.find((part) => part.classId === 114 && classOf(part) === "Panel"),
      spriteReference: parts.find((part) => part.classId === 114 && classOf(part) === "SpriteReference"),
      /// The runtime pieces the picture has to be told about: a slingshot's own component (its bands)
      /// and every frame animation (refused unless it stays off).
      slingshot: parts.find((part) => part.classId === 114 && classOf(part) === "Slingshot"),
      spriteAnimation: parts.find((part) => part.classId === 114 && classOf(part) === "SpriteAnimation"),
      renderer: parts.find((part) => part.classId === 23 || part.classId === 212),
      renderers: parts.filter((part) => part.classId === 23 || part.classId === 212).length,
      collider: parts.some((part) => COLLIDER_IDS[part.classId] !== undefined),
      /// Every collider the object itself carries, so the solid props' own boxes can be written out
      /// (a trigger is the original's own "someone walked through here" marker, not a wall).
      colliders: parts
        .filter((part) => COLLIDER_IDS[part.classId] !== undefined)
        .map((part) => ({
          kind: COLLIDER_IDS[part.classId],
          trigger: (fieldOf(part.body, "m_IsTrigger") ?? "0") === "1",
          size: numbersOf(part.body, "m_Size"),
          center: numbersOf(part.body, "m_Center"),
          radius: Number(fieldOf(part.body, "m_Radius") ?? "NaN"),
        })),
      transformAnchor: transform?.anchor ?? null,
      father: transform === undefined ? null : referenceOf(transform.body, "m_Father"),
      children: transform === undefined ? [] : [...transform.body.matchAll(/- \{fileID: (\d+)\}/g)].map((entry) => entry[1]),
      position: transform === undefined ? [0, 0, 0] : numbersOf(transform.body, "m_LocalPosition"),
      rotation: transform === undefined ? [0, 0, 0, 1] : numbersOf(transform.body, "m_LocalRotation"),
      scale: transform === undefined ? [1, 1, 1] : numbersOf(transform.body, "m_LocalScale"),
    });
  }
  return objects;
}

/** `parent` applied after `child` (the child's local frame, expressed in the parent's). */
function composeFrame(parent, child) {
  return [
    parent[0] * child[0] + parent[2] * child[1],
    parent[1] * child[0] + parent[3] * child[1],
    parent[0] * child[2] + parent[2] * child[3],
    parent[1] * child[2] + parent[3] * child[3],
    parent[0] * child[4] + parent[2] * child[5] + parent[4],
    parent[1] * child[4] + parent[3] * child[5] + parent[5],
  ];
}

/**
 * A GameObject's own local frame as `[a, b, c, d, tx, ty]` (the canvas-ordered matrix
 * `[[a, c, tx], [b, d, ty]]`). Every prop transform is 2D -- a rotation about z around a non-uniform
 * scale, or the half turn about y a mirrored child uses -- so the frame is exact; anything else is
 * refused rather than silently flattened. The z scale never reaches the picture (the quad lies in the
 * xy plane) and is accepted as-is.
 */
function localFrame(object, describe) {
  const [x, y, z, w] = object.rotation;
  const [sx, sy] = object.scale;
  const [px, py] = object.position;
  if (Math.abs(x) <= 1e-6 && Math.abs(z) <= 1e-6 && Math.abs(Math.abs(y) - 1) <= 1e-6) {
    // A half turn about y: inside the xy plane it is a mirror in x.
    return [-sx, 0, 0, sy, px, py];
  }

  if (Math.abs(x) > 1e-6 || Math.abs(y) > 1e-6) {
    throw new PropException(`${describe}: out-of-plane rotation ${object.rotation.join(",")}`);
  }

  // The quaternion is (0, 0, z, w): a rotation of 2*atan2(z, w) about z.
  const angle = 2 * Math.atan2(z, w);
  const [cos, sin] = [Math.cos(angle), Math.sin(angle)];
  return [sx * cos, sx * sin, -sy * sin, sy * cos, px, py];
}

/**
 * A composed frame as the numbers a painter can apply -- `translate(dx, dy)`, `rotate(-rotation)`
 * and `scale(scaleX, scaleY)` -- around the quad's own art, plus `dz`, the depth the transform chain
 * puts the quad on (an in-plane transform never changes z, and the original's depth test is what
 * layers a backdrop's own children: its sky sits 20 units behind the level and its beach bands 20 in
 * front). Any invertible 2D matrix factors as
 * `R(rotation) * diag(scaleX, scaleY)` exactly when it has no shear; a composed hierarchy can introduce
 * one (a non-uniformly scaled parent above a rotated child), so the factorisation is verified and a
 * sheared frame is reported instead of drawn wrong.
 */
function frameToQuad(frame, depth, describe) {
  const [a, b, c, d, tx, ty] = frame;
  const determinant = a * d - b * c;
  if (Math.abs(determinant) < 1e-12) throw new PropException(`${describe}: the transform collapses (determinant ${determinant})`);
  const scaleX = Math.hypot(a, b);
  const rotation = Math.atan2(b, a);
  const scaleY = determinant / scaleX;
  const [cos, sin] = [Math.cos(rotation), Math.sin(rotation)];
  const residual = Math.max(
    Math.abs(a - cos * scaleX),
    Math.abs(b - sin * scaleX),
    Math.abs(c + sin * scaleY),
    Math.abs(d - cos * scaleY),
  );
  if (residual > 1e-6) throw new PropException(`${describe}: the transform shears (residual ${residual})`);
  return { dx: round(tx), dy: round(ty), dz: round(depth), rotation: round(rotation), scaleX: round(scaleX), scaleY: round(scaleY) };
}

export class PropException extends Error {}

/// The runtime sprite library (`RuntimeSpriteDatabase.LoadFast`): `sprites.txt` is tab separated and
/// 14 or 15 columns long (`id`, `"name"`, `materialId`, `selectionX/Y/Width/Height`, `pivotX/Y`,
/// `UVx/UVy`, `width`, `height`, `subdivisions`[, `opaqueBorderPixels`]); `spritemapping.txt` is
/// `id x y w h` with the uv rect normalised.
function readSpriteDatabase(assetsRoot) {
  const directory = join(assetsRoot, "Resources", "GUISystem");
  const sprites = new Map();
  for (const line of readFileSync(join(directory, "sprites.txt"), "utf8").split("\n")) {
    const columns = line.split("\t").filter((column) => column.length > 0);
    if (columns.length < 14) continue;
    const number = (index) => Number(columns[index]);
    sprites.set(columns[0], {
      id: columns[0],
      selectionX: number(3),
      selectionY: number(4),
      selectionWidth: number(5),
      selectionHeight: number(6),
      pivotX: number(7),
      pivotY: number(8),
      uvX: number(9),
      uvY: number(10),
      width: number(11),
      height: number(12),
      subdivisions: number(13),
      opaqueBorderPixels: columns.length > 14 ? number(14) : 0,
    });
  }

  const mapping = new Map();
  for (const line of readFileSync(join(directory, "spritemapping.txt"), "utf8").split("\n")) {
    const columns = line.split("\t").filter((column) => column.length > 0);
    if (columns.length !== 5) continue;
    mapping.set(columns[0], [Number(columns[1]), Number(columns[2]), Number(columns[3]), Number(columns[4])]);
  }

  return { sprites, mapping };
}

/// The width/height of a PNG, read out of its IHDR, or null when the file is not a PNG.
export function pngSize(path) {
  const bytes = readFileSync(path).subarray(0, 24);
  if (bytes.length < 24 || bytes.toString("ascii", 1, 4) !== "PNG") return null;
  return [bytes.readUInt32BE(16), bytes.readUInt32BE(20)];
}

/**
 * A prop reader over one BPLE checkout. Building it walks `Assets/**` once (guid -> asset path) and
 * indexes the decompiled scripts (`guid -> class`), so it is created once per tool run and reused
 * per prefab. `read` takes the palette's own path (relative to the BPLE root, the shape
 * `tools/bple-levels` resolves palette guids to) and throws `PropException` for anything it cannot
 * resolve, so the caller decides whether to stop or to record and carry on.
 */
export function openProps(bpleRoot) {
  const assetsRoot = assets(bpleRoot);
  const guidToPath = indexAssetGuids(assetsRoot, { suffix: "", ignoreUnreadable: true });
  const guidToClass = buildGuidIndex(scriptAssembly(bpleRoot));
  const database = readSpriteDatabase(assetsRoot);
  const materialCache = new Map();
  const propCache = new Map();
  /** Atlas file name -> its absolute path in the original project (what the extractor copies). */
  const atlases = new Map();

  const must = (condition, message) => {
    if (!condition) throw new PropException(message);
  };

  /** The `_MainTex` texture guid an `.mat` asset points at (Unity nests it under `_MainTex:`). */
  const materialTextureGuid = (materialGuid) => {
    if (materialCache.has(materialGuid)) return materialCache.get(materialGuid);
    const path = guidToPath.get(materialGuid);
    const text = path && /\.mat$/i.test(path) ? readFileSync(path, "utf8") : null;
    const guid = text === null ? null : /_MainTex:\s*\n\s*m_Texture: \{fileID: \d+, guid: ([0-9a-f]{32})/.exec(text)?.[1] ?? null;
    materialCache.set(materialGuid, guid);
    return guid;
  };

  /** An `.mat`'s own `_Color`, for the materials that sample no texture at all. */
  const materialColor = (materialGuid) => {
    const path = guidToPath.get(materialGuid);
    const text = path && /\.mat$/i.test(path) ? readFileSync(path, "utf8") : null;
    const match = text === null ? null : /^\s*_Color: \{r: (-?[0-9.eE+-]+), g: (-?[0-9.eE+-]+), b: (-?[0-9.eE+-]+), a: (-?[0-9.eE+-]+)\}/m.exec(text);
    return match === null ? null : match.slice(1, 5).map((value) => round(Number(value)));
  };

  /** A number out of a component block, falling back to the C# field initializer. */
  const field = (name, block, className, fallback) => {
    const value = fieldOf(block, name);
    if (value === undefined) return fallback;
    const number = Number(value);
    must(Number.isFinite(number), `${name}: ${className}.${name} is not a number ("${value}")`);
    return number;
  };

  /**
   * What a renderer's first material paints with: the atlas it samples, or -- for the background
   * layers' `*_Fill` quads, whose material is `_Custom/Unlit_Monochrome` and samples nothing -- the
   * solid colour the shader returns (`_Color`, alpha included).
   */
  const paintOf = (name, rendererBlock) => {
    const materialGuid = /m_Materials:\s*\n\s*- \{fileID: \d+, guid: ([0-9a-f]{32})/.exec(rendererBlock)?.[1];
    must(Boolean(materialGuid), `${name}: renderer with no material`);
    const textureGuid = materialTextureGuid(materialGuid);
    if (textureGuid === null) {
      const color = materialColor(materialGuid);
      must(color !== null, `${name}: material ${materialGuid} has neither a _MainTex nor a _Color`);
      return { color };
    }
    const path = guidToPath.get(textureGuid);
    must(Boolean(path), `${name}: texture ${textureGuid} resolves to no asset`);
    const size = pngSize(path);
    must(Boolean(size), `${name}: texture ${path} is not a PNG`);
    atlases.set(basename(path), path);
    return { atlas: basename(path), path, size };
  };

  /**
   * A uv rect -- Unity's own, with `v` measured up from the texture's bottom -- as the source rect a
   * painter hands to `drawImage`: `x/y/w/h` in atlas pixels with `y` measured **down** from the top.
   * `tools/bple-textures` writes the part atlases the same way (`extract.mjs`'s
   * `atlasSize.height - yBottom - h`), and the canvas painter reads `y` as a top-down source row, so a
   * manifest that kept Unity's own direction would draw every quad from the mirrored row.
   */
  const sourceRect = (paint, u, v, du, dv) => ({
    x: round(u * paint.size[0]),
    y: round(paint.size[1] - (v + dv) * paint.size[1]),
    w: round(du * paint.size[0]),
    h: round(dv * paint.size[1]),
  });

  /** One `UnmanagedSprite`'s quad: its own fields decide the uv cell and the mesh size. */
  const unmanagedArt = (name, block, paint) => {
    const value = (key) => field(key, block, "UnmanagedSprite", UNMANAGED_FIELDS[key]);
    const [sx, sy] = [value("m_spriteWidth"), value("m_spriteHeight")];
    must(sx > 0 && sy > 0, `${name}: UnmanagedSprite has no serialized quad size (${sx}x${sy})`);
    const size = { cx: 0, cy: 0, sx: round(sx * UNITS_PER_PIXEL), sy: round(sy * UNITS_PER_PIXEL) };
    if (paint.color !== undefined) return { color: paint.color, ...size };

    const grid = value("m_atlasGridSubdivisions");
    const border = value("m_border");
    const [u, v, columns, rows] = [value("m_UVx"), value("m_UVy"), value("m_width"), value("m_height")];
    const inset = HALF_TEXEL + border / 1024;
    const u0 = u / grid + inset;
    const v0 = v / grid + inset;
    const du = columns / grid - 2 * inset;
    const dv = rows / grid - 2 * inset;
    return {
      atlas: paint.atlas,
      ...sourceRect(paint, u0, v0, du, dv),
      ...size,
    };
  };

  /** One `Sprite`'s quad: the library row decides the size and the pivot, the mapping the uv rect. */
  const managedArt = (name, block, paint) => {
    const id = fieldOf(block, "m_id") ?? "";
    const row = database.sprites.get(id);
    must(Boolean(row), `${name}: sprite id "${id}" is not in the sprite library`);
    const uv = database.mapping.get(id) ?? [row.uvX, row.uvY, row.width, row.height];
    const value = (key) => field(key, block, "Sprite", MANAGED_FIELDS[key]);
    const scaleX = value("m_scaleX");
    const scaleY = value("m_scaleY");
    // `ResetUVs` insets the mapped rect by `opaqueBorderPixels` (a half texel of the source sprite).
    const insetU = (row.opaqueBorderPixels * uv[2]) / row.width;
    const insetV = (row.opaqueBorderPixels * uv[3]) / row.height;
    // `SelectSprite`'s num9/num10: the scaled distance between the selection centre and the uv cell
    // centre, plus the row's and the prefab's own pivots.
    const pivotX = Math.trunc(scaleX * (row.selectionX + row.selectionWidth / 2 - (row.uvX + row.width / 2) + row.pivotX + value("m_pivotX")));
    const pivotY = Math.trunc(scaleY * (row.selectionY + row.selectionHeight / 2 - (row.uvY + row.height / 2) + row.pivotY + value("m_pivotY")));
    const size = {
      cx: round(-2 * pivotX * UNITS_PER_PIXEL),
      cy: round(-2 * pivotY * UNITS_PER_PIXEL),
      sx: round(Math.trunc(scaleX * row.width) * UNITS_PER_PIXEL),
      sy: round(Math.trunc(scaleY * row.height) * UNITS_PER_PIXEL),
    };
    if (paint.color !== undefined) return { color: paint.color, ...size };
    return {
      atlas: paint.atlas,
      ...sourceRect(paint, uv[0] + insetU, uv[1] + insetV, uv[2] - 2 * insetU, uv[3] - 2 * insetV),
      ...size,
    };
  };

  /**
   * One `Panel`'s tiles (`Panel.cs:86-125`): the mesh is an `m_width x m_height` grid whose tiles are
   * `((int)(scaleX * row.width / 3), (int)(scaleY * row.height / 3))` pixels of the sprite its
   * `SpriteReference` names, and the sprite's own uv rect is cut in thirds -- the border tiles take the
   * edge cells, every other tile the centre one (`Panel.GetUvs`) -- so a 3x3 window sprite becomes a
   * stretched nine-slice. The tiles sit on the object's own plane (z = 0 in `CreateMesh`), centred on
   * it, with the first tile at `-0.5 * m_width` and the first row at `-0.5 * m_height`.
   */
  const panelArt = (name, object, frame, depth) => {
    const width = field("m_width", object.panel.body, "Panel", 5);
    const height = field("m_height", object.panel.body, "Panel", 3);
    must(object.spriteReference !== undefined, `${name}/${object.name}: a Panel with no SpriteReference`);
    const id = fieldOf(object.spriteReference.body, "m_id") ?? "";
    const row = database.sprites.get(id);
    must(Boolean(row), `${name}/${object.name}: sprite id "${id}" is not in the sprite library`);
    const uv = database.mapping.get(id) ?? [row.uvX, row.uvY, row.width, row.height];
    const scaleX = field("m_scaleX", object.spriteReference.body, "SpriteReference", 1);
    const scaleY = field("m_scaleY", object.spriteReference.body, "SpriteReference", 1);
    const [tileX, tileY] = [Math.trunc((scaleX * row.width) / 3), Math.trunc((scaleY * row.height) / 3)];
    must(tileX > 0 && tileY > 0, `${name}/${object.name}: a ${tileX}x${tileY} pixel panel tile`);
    const paint = paintOf(name, object.renderer.body);
    must(paint.atlas !== undefined, `${name}/${object.name}: a panel whose material samples no texture`);
    const [cellU, cellV] = [uv[2] / 3, uv[3] / 3];
    // `Panel.CreateMesh`'s `num2 = 2 * tileWidth * 10 / 768` is the same "pixel times `20/768`" a
    // sprite is, so the tile's world size is the pixel count times the one factor, not twice it.
    const [tileWidth, tileHeight] = [tileX * UNITS_PER_PIXEL, tileY * UNITS_PER_PIXEL];
    const quads = [];
    for (let line = 0; line < height; line += 1) {
      for (let column = 0; column < width; column += 1) {
        const dx = tileWidth * (column - 0.5 * width + 0.5);
        const dy = tileHeight * (line - 0.5 * height + 0.5);
        // The uv cell and the tile move together: both take 0, 1 or 2 cells from the sprite's rect.
        const [cu, cv] = [column === 0 ? 0 : column === width - 1 ? 2 : 1, line === 0 ? 0 : line === height - 1 ? 2 : 1];
        quads.push({
          atlas: paint.atlas,
          ...sourceRect(paint, uv[0] + cu * cellU, uv[1] + cv * cellV, cellU, cellV),
          cx: 0,
          cy: 0,
          sx: round(tileWidth),
          sy: round(tileHeight),
          ...frameToQuad(composeFrame(frame, [1, 0, 0, 1, dx, dy]), depth, `${name}/${object.name}`),
        });
      }
    }
    return quads;
  };

  /**
   * The two rubber bands of a `Slingshot` (`Slingshot.cs:26-52`): the class builds them as runtime
   * `LineRenderer`s (nothing in the prefab) at `SetWidth(0.25, 0.25)`, anchored on two points it
   * hardcodes and ending on the pad's own rest position, because `SetDrawPosition(Vector3.zero)` puts
   * the second end at `m_restPosition` -- the `Pad` child's local position, which the prefab does hold.
   * A `LineRenderer` with two vertices and no texture draws one quad: `0.25` units wide, `|p1 - p0|`
   * long, along the line. `Material/RubberBand.mat` samples no texture, so the band is its `_Color`.
   */
  const bandQuads = (name, scriptBody, pad) => {
    const text = readFileSync(join(scriptAssembly(bpleRoot), "Slingshot.cs"), "utf8");
    // The class writes Unity's float literals (`0.25f`), so the suffix comes off before the number does.
    const literal = (value) => Number(value.trim().replace(/f$/, ""));
    const width = literal(/SetWidth\(([-0-9.ef]+), *[-0-9.ef]+\)/.exec(text)?.[1] ?? "");
    const anchors = [...text.matchAll(/m_line\d\.SetPosition\(0, new Vector3\(([^)]*)\)\)/g)].map((match) =>
      match[1].split(",").map(literal),
    );
    must(anchors.length === 2, `${name}: Slingshot.cs sets ${anchors.length} band anchors, expected 2`);
    must(anchors.every((anchor) => anchor.length === 3 && anchor.every((value) => Number.isFinite(value))), `${name}: a band anchor is not three numbers`);
    must(Number.isFinite(width) && width > 0, `${name}: Slingshot.cs declares no band width`);
    const materialGuid = assetReferenceOf(scriptBody, "m_rubberBandMaterial");
    must(materialGuid !== null, `${name}: the Slingshot component names no rubber band material`);
    const paint = materialColor(materialGuid);
    must(paint !== null, `${name}: the rubber band material has no _Color`);
    return anchors.map((anchor) => {
      const [x0, y0, z0] = anchor;
      const [x1, y1] = [pad.position[0], pad.position[1]];
      const length = Math.hypot(x1 - x0, y1 - y0);
      must(length > 0, `${name}: a rubber band of zero length`);
      return {
        color: paint,
        cx: 0,
        cy: 0,
        sx: round(length),
        sy: round(width),
        dx: round((x0 + x1) / 2),
        dy: round((y0 + y1) / 2),
        dz: round(z0),
        rotation: round(Math.atan2(y1 - y0, x1 - x0)),
        scaleX: 1,
        scaleY: 1,
      };
    });
  };

  /**
   * One solid box: a non-trigger `BoxCollider` of `props.mjs`'s walk, in the prop's own frame, the way
   * the quads are -- `dx/dy/dz` and `rotation` place it, `sizeX/sizeY/sizeZ` are the collider's own
   * `m_Size` folded through the transform chain's scale (its `m_Center` is the box's own offset). The
   * chain's scale is what makes a `BreakableWall` at instance scale 1.1 a 1.2 m box, and a mirrored
   * chain (`det < 0`) is a half turn of a box, which the box's own symmetry already covers.
   */
  const boxQuad = (name, object, frame, depth, collider) => {
    const [a, b, c, d] = frame;
    const determinant = a * d - b * c;
    must(Math.abs(determinant) > 1e-12, `${name}/${object.name}: a collapsing collider frame`);
    const scaleX = Math.hypot(a, b);
    const scaleY = determinant / scaleX;
    const [centreX, centreY, centreZ] = collider.center.length === 3 ? collider.center : [0, 0, 0];
    // Raw numbers, not the manifest's four decimals: these land in *content*, which is float32-exact
    // (`lib/write.mjs`), so the level builder folds them with `Math.fround` instead.
    return {
      dx: a * centreX + c * centreY + frame[4],
      dy: b * centreX + d * centreY + frame[5],
      dz: depth + (centreZ ?? 0),
      sizeX: Math.abs(scaleX) * collider.size[0],
      sizeY: Math.abs(scaleY) * collider.size[1],
      sizeZ: collider.size[2],
      rotation: Math.atan2(b, a),
    };
  };

  /**
   * What one prop prefab is. `kind` is one of
   *
   *   terrain  the `e2dTerrain` family -- the level file's own terrain objects (see `docs/specs/original-level-pack.md`)
   *   system   no renderer at all: `LevelStart`, `LevelManager`, `CameraSystem`, challenges, `DessertPlace`, clouds...
   *   solid    carries a collider: `TNT_Box`, `BoxChallenge`, `StarBox`, `BreakableWall`, the background sets...
   *   decor    exactly one quad, one GameObject and no collider -- the family the props document places first
   *   special  anything else that draws: behaviour props and the families whose art comes from elsewhere
   *
   * and `drawable` says whether `quads` is the whole picture: every renderer-bearing GameObject of the
   * active hierarchy carries a `Sprite`/`UnmanagedSprite` (or the `Panel` that builds its own tiles) and
   * the prefab's scripts stay inside `SPRITE_CLASSES`, `ALLOWED_EXTRA` and `PANEL_CLASSES`. A drawable
   * prop may be many quads (the backdrop sets are 50 to 250 of them, a window nine, a slingshot five) and
   * may still carry a collider -- the room owns its colliders, the picture stays whole.
   */
  const read = (prefabPath) => {
    if (propCache.has(prefabPath)) return propCache.get(prefabPath);
    const name = basename(prefabPath, ".prefab");
    const file = join(bpleRoot, prefabPath);
    must(existsSync(file), `${name}: no prefab at ${prefabPath}`);
    const objects = readHierarchy(readFileSync(file, "utf8"), guidToClass);
    const roots = objects.filter((object) => object.father === null || object.father === "0");
    must(roots.length === 1, `${name}: ${roots.length} root GameObjects, expected 1`);
    const childrenOf = new Map();
    for (const object of objects) {
      if (object.father === null) continue;
      childrenOf.set(object.father, [...(childrenOf.get(object.father) ?? []), object]);
    }

    const scripts = [...new Set(objects.flatMap((object) => object.classes))];
    const rendererObjects = objects.filter((object) => object.renderers > 0);
    const kind = scripts.includes("e2dTerrain")
      ? "terrain"
      : rendererObjects.length === 0
        ? "system"
        : objects.some((object) => object.collider)
          ? "solid"
          : rendererObjects.length === 1 && objects.length === 1 && SPRITE_CLASSES.includes(scripts[0])
            ? "decor"
            : "special";
    // `SpriteReference` draws nothing on its own (`SpriteReference.RenderingEnabled()` is false); it
    // only ever feeds the `Panel` next to it, so one without a panel would be a lie in the picture.
    for (const object of objects) {
      must(object.spriteReference === undefined || object.panel !== undefined, `${name}/${object.name}: a SpriteReference with no Panel`);
      // An autoplaying `SpriteAnimation` would replace the sprite this tool draws with the first frame
      // of the animation it names; none of the accepted props does, and if one starts to, the picture
      // would silently be a frame behind -- so it is refused rather than drawn as the wrong cell.
      const animation = object.spriteAnimation?.body;
      if (animation !== undefined) {
        must(
          (fieldOf(animation, "m_AutoPlay") ?? "0") === "0" && (fieldOf(animation, "m_AutoPlayName") ?? "") === "",
          `${name}/${object.name}: an autoplaying SpriteAnimation`,
        );
      }
    }
    const drawable =
      rendererObjects.length > 0 &&
      rendererObjects.every((object) => object.renderers === 1 && (object.sprite !== undefined || object.panel !== undefined)) &&
      scripts.every(
        (className) =>
          SPRITE_CLASSES.includes(className) ||
          ALLOWED_EXTRA.includes(className) ||
          PANEL_CLASSES.includes(className) ||
          SOLID_CLASSES.includes(className),
      );

    const record = { name, id: propId(name), path: prefabPath, kind, drawable, scripts, gameObjects: objects.length, quads: [], boxes: [] };
    if (drawable) {
      // What the walk has to visit: everything that draws, plus every object whose *own* box collider
      // is part of the prop's solid geometry. A trigger is the original's own "someone came through
      // here" marker -- a challenge's box, a goal, a secret, a pressure plate -- and the backdrop sets'
      // own ground strip (`GroundCollider`, 188 m wide, in the backdrop's plane behind the level) is
      // the backdrop's business: our terrain is the floor.
      const skippedColliderObjects = ["GroundCollider", "CloudCollider"];
      const relevant = new Set();
      const collect = (object) => {
        let needed =
          object.renderers > 0 ||
          object.colliders.some(
            (collider) => collider.kind === "BoxCollider" && !collider.trigger && !skippedColliderObjects.includes(object.name),
          );
        // Every child is visited: `some`/`||` would stop at the first one that would be walked.
        for (const child of childrenOf.get(object.transformAnchor) ?? []) {
          if (collect(child)) needed = true;
        }
        if (needed) relevant.add(object.anchor);
        return relevant.has(object.anchor);
      };
      collect(roots[0]);
      // Depth first from the root, carrying the active flag and the nearest parallax tag down. The
      // root's own transform is what `LevelLoader.ReadPrefabInstance` overwrites with the level
      // instance's, so it starts at identity and every child frame lands in that same world frame.
      const foreground = [];
      const painted = new Set();
      const walk = (object, parentFrame, parentDepth, inheritedParallax, inheritedActive) => {
        const active = inheritedActive && object.active;
        const frame = parentFrame === null ? IDENTITY_FRAME : composeFrame(parentFrame, localFrame(object, `${name}/${object.name}`));
        // An in-plane transform never changes z, so a quad's depth is the sum of the chain's own z.
        // The root contributes nothing: the level instance overwrites its whole transform, z included.
        const depth = parentFrame === null ? 0 : (parentDepth ?? 0) + (object.position[2] ?? 0);
        const parallax = PARALLAX_TAGS[object.tag] ?? inheritedParallax;
        if (object.renderers > 0 && active) {
          const quads =
            object.panel !== undefined
              ? panelArt(name, object, frame, depth)
              : [
                  {
                    ...(object.classes.includes("UnmanagedSprite")
                      ? unmanagedArt(name, object.sprite.body, paintOf(name, object.renderer.body))
                      : managedArt(name, object.sprite.body, paintOf(name, object.renderer.body))),
                    ...frameToQuad(frame, depth, `${name}/${object.name}`),
                  },
                ];
          for (const quad of quads) record.quads.push({ ...quad, parallax: { x: parallax.x, y: parallax.y } });
          painted.add(object.anchor);
          if (parallax.y !== 0) foreground.push({ y: frame[5], quad: record.quads[record.quads.length - 1] });
        }
        // The solid boxes sit on the transform chain just like the quads do, so an off-origin child
        // collider (`BreakableWall`'s own `Collider`) lands where the original put it.
        if (active) {
          for (const collider of object.colliders) {
            if (collider.kind !== "BoxCollider" || collider.trigger || skippedColliderObjects.includes(object.name)) continue;
            must(collider.size.length === 3, `${name}/${object.name}: a box collider without a size`);
            record.boxes.push(boxQuad(name, object, frame, depth, collider));
          }
        }
        for (const child of childrenOf.get(object.transformAnchor) ?? []) {
          if (relevant.has(child.anchor)) walk(child, frame, depth, parallax, active);
        }
      };
      walk(roots[0], null, 0, { x: 0, y: 0 }, true);
      // `ParallaxManager.Start` takes the first foreground layer's own height as the limit the whole
      // foreground layer tracks the camera down to.
      if (foreground.length > 0) {
        const limit = round(foreground[0].y);
        for (const entry of foreground) entry.quad.parallax.limitY = limit;
      }
      must(painted.size === rendererObjects.length, `${name}: ${painted.size} of ${rendererObjects.length} renderers are drawn`);
      // The slingshot's rubber bands are the one piece of art no prefab holds: `Slingshot.Awake` builds
      // them, and at load `SetDrawPosition(zero)` leaves them anchored on the pad's own rest position.
      if (scripts.includes("Slingshot")) {
        const pad = objects.find((object) => object.name === "Pad");
        must(pad !== undefined, `${name}: a Slingshot with no Pad child`);
        const script = objects.find((object) => object.slingshot !== undefined);
        must(script !== undefined, `${name}: a Slingshot with no Slingshot component`);
        for (const quad of bandQuads(name, script.slingshot.body, pad)) record.quads.push({ ...quad, parallax: { x: 0, y: 0 } });
      }
    }
    propCache.set(prefabPath, record);
    return record;
  };

  return { read, database, atlases, guidToPath, assetsRoot };
}

/// Four decimals, the precision `part-textures.json` already uses: the manifest is a client asset,
/// not float32 content, and the original's own numbers reach it through divisions that would
/// otherwise print 17 digits of noise (a 2048 atlas' half texel is 0.0005 px).
function round(value) {
  return Number(value.toFixed(4));
}
