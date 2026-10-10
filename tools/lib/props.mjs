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

  /** A number out of a component block, falling back to the C# field initializer. */
  const field = (name, block, className, fallback) => {
    const value = fieldOf(block, name);
    if (value === undefined) return fallback;
    const number = Number(value);
    must(Number.isFinite(number), `${name}: ${className}.${name} is not a number ("${value}")`);
    return number;
  };

  /** The atlas a renderer's first material points at, as `{ name, path, size }`. */
  const atlasOf = (name, rendererBlock) => {
    const materialGuid = /m_Materials:\s*\n\s*- \{fileID: \d+, guid: ([0-9a-f]{32})/.exec(rendererBlock)?.[1];
    must(Boolean(materialGuid), `${name}: renderer with no material`);
    const textureGuid = materialTextureGuid(materialGuid);
    must(Boolean(textureGuid), `${name}: material ${materialGuid} has no _MainTex`);
    const path = guidToPath.get(textureGuid);
    must(Boolean(path), `${name}: texture ${textureGuid} resolves to no asset`);
    const size = pngSize(path);
    must(Boolean(size), `${name}: texture ${path} is not a PNG`);
    return { name: basename(path), path, size };
  };

  /** One `UnmanagedSprite`'s quad: its own fields decide the uv cell and the mesh size. */
  const unmanagedArt = (name, block, atlas) => {
    const value = (key) => field(key, block, "UnmanagedSprite", UNMANAGED_FIELDS[key]);
    const grid = value("m_atlasGridSubdivisions");
    const border = value("m_border");
    const [u, v, columns, rows] = [value("m_UVx"), value("m_UVy"), value("m_width"), value("m_height")];
    const inset = HALF_TEXEL + border / 1024;
    const u0 = u / grid + inset;
    const v0 = v / grid + inset;
    const du = columns / grid - 2 * inset;
    const dv = rows / grid - 2 * inset;
    const [sx, sy] = [value("m_spriteWidth"), value("m_spriteHeight")];
    must(sx > 0 && sy > 0, `${name}: UnmanagedSprite has no serialized quad size (${sx}x${sy})`);
    return {
      atlas: atlas.name,
      x: round(u0 * atlas.size[0]),
      y: round(v0 * atlas.size[1]),
      w: round(du * atlas.size[0]),
      h: round(dv * atlas.size[1]),
      cx: 0,
      cy: 0,
      sx: round(sx * UNITS_PER_PIXEL),
      sy: round(sy * UNITS_PER_PIXEL),
    };
  };

  /** One `Sprite`'s quad: the library row decides the size and the pivot, the mapping the uv rect. */
  const managedArt = (name, block, atlas) => {
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
    return {
      atlas: atlas.name,
      x: round((uv[0] + insetU) * atlas.size[0]),
      y: round((uv[1] + insetV) * atlas.size[1]),
      w: round((uv[2] - 2 * insetU) * atlas.size[0]),
      h: round((uv[3] - 2 * insetV) * atlas.size[1]),
      cx: round(-2 * pivotX * UNITS_PER_PIXEL),
      cy: round(-2 * pivotY * UNITS_PER_PIXEL),
      sx: round(Math.trunc(scaleX * row.width) * UNITS_PER_PIXEL),
      sy: round(Math.trunc(scaleY * row.height) * UNITS_PER_PIXEL),
    };
  };

  /**
   * What one prop prefab is. `kind` is one of
   *
   *   terrain  the `e2dTerrain` family -- the level file's own terrain objects (see `docs/specs/original-level-pack.md`)
   *   system   no renderer at all: `LevelStart`, `LevelManager`, `CameraSystem`, challenges, `DessertPlace`, clouds...
   *   solid    carries a collider: `TNT_Box`, `BoxChallenge`, `StarBox`, `BreakableWall`, the background sets...
   *   decor    exactly one decoration quad and nothing else -- the family `art` is for
   *   special  anything else that draws: multi-quad prefabs, `SpriteReference`/`PointLightSource` variants, birds, the slingshot
   */
  const read = (prefabPath) => {
    if (propCache.has(prefabPath)) return propCache.get(prefabPath);
    const name = basename(prefabPath, ".prefab");
    const file = join(bpleRoot, prefabPath);
    must(existsSync(file), `${name}: no prefab at ${prefabPath}`);
    const blocks = readFileSync(file, "utf8").split(/\n--- !u!/).slice(1);
    const id = (block) => Number(/^(\d+)/.exec(block)?.[1]);
    const classesOf = (wanted) =>
      blocks.filter((block) => wanted.includes(CLASS_IDS[id(block)])).map((block) => ({ block, class: CLASS_IDS[id(block)] }));
    const monos = blocks
      .filter((block) => id(block) === 114)
      .map((block) => ({
        block,
        class: guidToClass.get(/m_Script: \{fileID: \d+, guid: ([0-9a-f]{32})/.exec(block)?.[1]) ?? "?unknown",
      }));
    const scripts = monos.map((mono) => mono.class);
    const renderers = classesOf(["MeshRenderer", "SpriteRenderer"]);
    const colliders = classesOf(["MeshCollider", "BoxCollider", "SphereCollider", "CapsuleCollider"]);
    const gameObjects = blocks.filter((block) => id(block) === 1).length;
    const kind = scripts.includes("e2dTerrain")
      ? "terrain"
      : renderers.length === 0
        ? "system"
        : colliders.length > 0
          ? "solid"
          : renderers.length === 1 && gameObjects === 1 && monos.length === 1 && SPRITE_CLASSES.includes(scripts[0])
            ? "decor"
            : "special";

    const record = { name, path: prefabPath, kind, scripts, gameObjects, quads: renderers.length, colliders: colliders.length };
    if (kind === "decor") {
      const atlas = atlasOf(name, renderers[0].block);
      record.class = scripts[0];
      record.atlasPath = atlas.path;
      record.art = scripts[0] === "UnmanagedSprite" ? unmanagedArt(name, monos[0].block, atlas) : managedArt(name, monos[0].block, atlas);
    }
    propCache.set(prefabPath, record);
    return record;
  };

  return { read, database, guidToPath, assetsRoot };
}

/// Four decimals, the precision `part-textures.json` already uses: the manifest is a client asset,
/// not float32 content, and the original's own numbers reach it through divisions that would
/// otherwise print 17 digits of noise (a 2048 atlas' half texel is 0.0005 px).
function round(value) {
  return Number(value.toFixed(4));
}
