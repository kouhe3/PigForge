// The fill half of an `e2dTerrain`'s look. The original's ground is one textured polygon:
// `Assets/Resources/fill.shader` is `return tex2D(_MainTex, i.texcoord) * _Color;`, and the UVs are
// computed per vertex by `LevelLoader.ReadMesh` (`LevelLoader.cs:279-290`) as
//
//     uv = (world - FillTextureTileOffset) / FillTextureTileWidth|Height
//
// -- the texture comes from the level file's `m_references` table, `_Color` and the tile offset from
// the level file itself, but the **tile size only exists on the terrain prefab** (`LevelLoader.cs:
// 217-218` writes the offset and leaves the size alone; `e2dConstants.INIT_FILL_TEXTURE_WIDTH` = 1
// is merely the field's default). This module reads the prefab half and the texture import state;
// the level half is already in `lib/reader.mjs` (`fillOffset`, `fillColor`, `fillTextureIndex`).
//
// A fill texture is sampled with **Repeat + Bilinear** (`Ground_*.png.meta`: `wrapU: 0`, `wrapV: 0`,
// `filterMode: 1`): the UVs run far outside 0..1 (a 100 m level tiles 20 times at 5 m), so a Clamp
// texture would smear instead of tile. Both are asserted here rather than trusted.

import { existsSync, readFileSync } from "node:fs";
import { join } from "node:path";

/// Unity `TextureWrapMode.Repeat` / `FilterMode.Bilinear` as `.meta` writes them.
const WRAP_REPEAT = 0;
const FILTER_BILINEAR = 1;

/// The `e2dTerrain` script asset's guid, read out of its `.meta`: a prefab carries a component by
/// guid, so this is how a block in a prefab is identified as the terrain component.
export function readE2dTerrainGuid(bpleRoot, check) {
  const file = join(bpleRoot, "Assets", "Scripts", "Assembly-CSharp", "e2dTerrain.cs.meta");
  check(existsSync(file), `${file}: e2dTerrain.cs.meta does not exist`);
  const match = existsSync(file) ? /^guid:\s*([0-9a-f]{32})\s*$/m.exec(readFileSync(file, "utf8")) : null;
  check(Boolean(match), `${file}: no guid line`);
  return match?.[1] ?? null;
}

/// The document of a Unity YAML file that carries `m_Script: {..., guid: <guid>}` -- from that
/// line's `--- !u!114 &id` separator to the next one. `null` when the file has no such component.
function scriptBlock(text, guid) {
  const script = new RegExp(`^\\s*m_Script:\\s*\\{[^}]*guid:\\s*${guid},`, "m").exec(text);
  if (script === null) return null;
  const start = text.lastIndexOf("--- !u!", script.index);
  const next = text.indexOf("--- !u!", script.index);
  return text.slice(start < 0 ? 0 : start, next < 0 ? text.length : next);
}

/// One serialized float of a YAML block (`FillTextureTileWidth: 5`).
function numberField(block, name) {
  const match = new RegExp(`^\\s*${name}:\\s*(-?[0-9.eE+]+)\\s*$`, "m").exec(block);
  return match === null ? null : Number(match[1]);
}

/// The fill tile size of one terrain prefab, or `null` when it cannot be read (the caller keeps the
/// failure). The tile size is the one number of the fill's three inputs that the level file does not
/// carry, so a terrain whose prefab declares none cannot be emitted.
export function readTerrainFillTile(bpleRoot, prefabPath, guid, check) {
  const file = join(bpleRoot, prefabPath);
  check(existsSync(file), `${prefabPath}: terrain prefab does not exist`);
  if (!existsSync(file)) return null;
  const block = scriptBlock(readFileSync(file, "utf8"), guid);
  check(Boolean(block), `${prefabPath}: no e2dTerrain component`);
  if (!block) return null;
  const tileWidth = numberField(block, "FillTextureTileWidth");
  const tileHeight = numberField(block, "FillTextureTileHeight");
  check(
    Number.isFinite(tileWidth) && tileWidth > 0 && Number.isFinite(tileHeight) && tileHeight > 0,
    `${prefabPath}: FillTextureTileWidth/Height must be positive numbers, got ${tileWidth} x ${tileHeight}`,
  );
  if (!Number.isFinite(tileWidth) || tileWidth <= 0 || !Number.isFinite(tileHeight) || tileHeight <= 0) return null;
  return { tileWidth, tileHeight };
}

/// The observed size set over every terrain prefab the levels place. Today all 21 declare 5 x 5; a
/// second size appearing means the assertion (and the reviewer) should look -- the content writes each
/// terrain's own prefab size, so the pack would still be ported, but `LevelLoader`'s fill UVs deserve
/// a re-read.
export function assertTerrainFillTileSizes(tiles, check) {
  const sizes = new Map();
  for (const { tileWidth, tileHeight } of tiles.values()) {
    const key = `${tileWidth}x${tileHeight}`;
    sizes.set(key, (sizes.get(key) ?? 0) + 1);
  }
  const observed = [...sizes.entries()].map(([size, count]) => `${size} (${count})`).join(", ");
  check(
    sizes.size === 1 && sizes.has("5x5"),
    `terrain fill tile sizes are ${observed || "<none>"} over ${tiles.size} prefab(s); expected only 5x5 -- ` +
      "the content writes each terrain's own prefab size, so a second size means re-reading LevelLoader's fill UVs",
  );
}

/// `prefab path -> { tileWidth, tileHeight }` for every terrain prefab in `prefabPaths`, with the size
/// set asserted. Convenience wrapper for callers that know the whole prefab set up front.
export function loadTerrainFillTiles(bpleRoot, prefabPaths, check) {
  const guid = readE2dTerrainGuid(bpleRoot, check);
  const tiles = new Map();
  for (const path of [...prefabPaths].sort()) {
    const tile = readTerrainFillTile(bpleRoot, path, guid, check);
    if (tile) tiles.set(path, tile);
  }
  assertTerrainFillTileSizes(tiles, check);
  return tiles;
}

/// The import state of a fill texture, read from its `.meta`: `{ wrapU, wrapV, filterMode }`.
export function readTextureImport(bpleRoot, path, check) {
  const file = join(bpleRoot, `${path}.meta`);
  check(existsSync(file), `${path}.meta does not exist`);
  const text = existsSync(file) ? readFileSync(file, "utf8") : "";
  const field = (name) => {
    const match = new RegExp(`^\\s*${name}:\\s*(-?\\d+)\\s*$`, "m").exec(text);
    return match === null ? null : Number(match[1]);
  };
  const wrapU = field("wrapU");
  const wrapV = field("wrapV");
  const filterMode = field("filterMode");
  check(
    wrapU === WRAP_REPEAT && wrapV === WRAP_REPEAT,
    `${path}: fill UVs tile, so the texture must wrap Repeat; wrapU=${wrapU} wrapV=${wrapV}`,
  );
  check(filterMode === FILTER_BILINEAR, `${path}: expected FilterMode.Bilinear (1), got ${filterMode}`);
  return { wrapU, wrapV, filterMode };
}
