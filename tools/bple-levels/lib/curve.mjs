// The `_curve` half of an `e2dTerrain`'s look: the art trim the original draws along every terrain
// outline. `e2dTerrainCurveMesh.RebuildMesh` builds it from `e2dTerrain.TerrainCurve` and its
// `StripeVertices` -- a two-row triangle strip whose even row is the curve node itself and whose odd
// row is that node pushed outwards by the node's `e2dCurveTexture.size.y`, kept inside the terrain's
// boundary rect (`e2dTerrainBoundary.EnsurePointIsInBoundary`) -- and `e2d/Curve` shades it:
//
//     texcoord0 = (nodeIndex / controlWidth + 0.5 / controlWidth, 0)
//     texcoord1 = (arclength * _SplatParams0.x, v)        // v = 1 on the node row, 0 on the stripe row
//     var2  = floor(tex2D(_Control, texcoord0).y)         // the control texture's green channel
//     var3  = tex2D(_Splat0, texcoord1)
//     var3.xyz += (tex2D(_Splat1, texcoord1).xyz - var3.xyz) * var2
//
// Four inputs come out of that, and every one of them is read out of the pack rather than authored:
// the mesh itself and the `e2dCurveTexture` table (`texture`, `size`, `fixedAngle`, `fadeThreshold`)
// from the level file, the two layer textures through their `m_references` index, and the per-node
// layer choice from the control texture PNG the file carries (one pixel high, one texel per node,
// `r` = layer 0 and `g` = layer 1 within the node's material). Files: `LevelLoader.ReadTerrain` /
// `ReadMesh`, `e2dTerrainCurveMesh.cs`, `e2dTerrainBoundary.cs`, `Assets/Resources/curve.shader`.
//
// What the content keeps: the strip's two rows, the two layer textures with their own Unity wrap
// mode (the shader's u runs far past 1 -- 8 of the 16 layer textures are `wrapU: 1`, i.e. Clamp, so
// a node's layer can be a single stretched texel column rather than tiling art), `_SplatParams0.x`
// and the runs of nodes that use the second layer. Everything else (`size.y` as the stripe offset,
// the material grouping, `fixedAngle`, `fadeThreshold`) is already folded into the mesh or never
// read by the shader.

import { existsSync, readFileSync } from "node:fs";
import { join } from "node:path";
import { inflateSync } from "node:zlib";
import { readImportState } from "./fill.mjs";

/// Unity `TextureWrapMode` as a `.meta` writes it; the shader's own sampler state follows the
/// texture import, so it is part of the look rather than an implementation detail.
const WRAP_REPEAT = 0;
const WRAP_CLAMP = 1;
const FILTER_BILINEAR = 1;

/// Decodes the level file's embedded control texture: an 8-bit, non-interlaced PNG (measured
/// 1 pixel high, `ARGB32` as `LevelLoader.ReadTerrain` reads it back through `LoadImage`).
/// Only what this pack contains is supported -- anything else is a failure, not a guess.
export function decodeControlTexture(buffer, check) {
  check(Buffer.isBuffer(buffer) && buffer.length > 8, "the control texture is empty");
  if (!Buffer.isBuffer(buffer) || buffer.length <= 8) return null;
  check(buffer.readUInt32BE(0) === 0x89504e47, "the control texture is not a PNG");
  let offset = 8;
  let width = 0;
  let height = 0;
  let depth = 0;
  let colorType = 0;
  let interlace = 0;
  const idat = [];
  while (offset + 8 <= buffer.length) {
    const length = buffer.readUInt32BE(offset);
    const type = buffer.toString("ascii", offset + 4, offset + 8);
    const data = buffer.subarray(offset + 8, offset + 8 + length);
    if (type === "IHDR") {
      width = data.readUInt32BE(0);
      height = data.readUInt32BE(4);
      depth = data[8];
      colorType = data[9];
      interlace = data[12];
    } else if (type === "IDAT") {
      idat.push(data);
    } else if (type === "IEND") {
      break;
    }
    offset += 12 + length;
  }
  const channels = { 0: 1, 2: 3, 4: 2, 6: 4 }[colorType];
  check(depth === 8, `the control texture is ${depth}-bit, expected 8`);
  check(interlace === 0, `the control texture is interlaced`);
  check(Boolean(channels), `the control texture has color type ${colorType}, expected one of 0/2/4/6`);
  check(height === 1, `the control texture is ${height} pixels high, expected 1`);
  if (depth !== 8 || interlace !== 0 || !channels || height !== 1) return null;
  const raw = inflateSync(Buffer.concat(idat));
  const stride = width * channels;
  const pixels = Buffer.alloc(stride * height);
  for (let y = 0; y < height; y += 1) {
    const filter = raw[y * (stride + 1)];
    const line = raw.subarray(y * (stride + 1) + 1, y * (stride + 1) + 1 + stride);
    const previous = y > 0 ? pixels.subarray((y - 1) * stride, y * stride) : Buffer.alloc(stride);
    const current = pixels.subarray(y * stride, (y + 1) * stride);
    for (let index = 0; index < stride; index += 1) {
      const left = index >= channels ? current[index - channels] : 0;
      const up = previous[index];
      const upLeft = index >= channels ? previous[index - channels] : 0;
      let value = line[index];
      if (filter === 1) value += left;
      else if (filter === 2) value += up;
      else if (filter === 3) value += (left + up) >> 1;
      else if (filter === 4) {
        const estimate = left + up - upLeft;
        const distanceLeft = Math.abs(estimate - left);
        const distanceUp = Math.abs(estimate - up);
        const distanceUpLeft = Math.abs(estimate - upLeft);
        value += distanceLeft <= distanceUp && distanceLeft <= distanceUpLeft ? left : distanceUp <= distanceUpLeft ? up : upLeft;
      } else check(filter === 0, `the control texture uses PNG filter ${filter}`);
      current[index] = value & 0xff;
    }
  }
  return { width, height, channels, pixels };
}

/// The runs of curve nodes whose layer is the *second* `e2dCurveTexture` (`_Splat1`), as
/// `[[start, count], ...]` in node order. `e2dTerrainCurveMesh.UpdateControlTextures` writes a node's
/// `texture % 4` into its control texel's channel -- `r` = 0, `g` = 1, `b` = 2, `a` = 3 -- and the
/// shader reads only green, so a node samples `_Splat1` exactly when its index within its material
/// is 1. One material covers every terrain in this pack (the table holds two entries in 2145
/// terrains and three in one), which is why the channels are `r`/`g` only and two textures suffice.
export function curveLayerRuns(controlTexture, nodeCount, check, label) {
  const png = decodeControlTexture(controlTexture, check);
  if (!png) return null;
  const expectedWidth = 2 ** Math.ceil(Math.log2(Math.max(nodeCount, 1)));
  check(
    png.width === expectedWidth,
    `${label}: the control texture is ${png.width} texels wide for ${nodeCount} node(s), expected ` +
      `${expectedWidth} (e2dTerrainCurveMesh.GetControlTextureSize = the next power of two)`,
  );
  if (png.width !== expectedWidth) return null;
  const runs = [];
  for (let node = 0; node < nodeCount; node += 1) {
    const base = node * png.channels;
    const red = png.pixels[base] ?? 0;
    const green = png.channels >= 3 ? png.pixels[base + 1] ?? 0 : 0;
    const blue = png.channels >= 3 ? png.pixels[base + 2] ?? 0 : 0;
    const alpha = png.channels === 4 ? png.pixels[base + 3] ?? 0 : png.channels === 2 ? png.pixels[base + 1] ?? 0 : 0;
    check(
      (red === 255 && green === 0) || (green === 255 && red === 0) || (red === 0 && green === 0 && blue === 0 && alpha === 0),
      `${label}: node ${node} of the control texture is ${red},${green},${blue},${alpha}; expected the ` +
        "first layer (r), the second (g) or nothing -- a third or fourth layer would need a texture the shader never samples",
    );
    const second = green === 255;
    if (second && runs.length > 0 && runs[runs.length - 1][0] + runs[runs.length - 1][1] === node) {
      runs[runs.length - 1][1] += 1;
    } else if (second) {
      runs.push([node, 1]);
    }
  }
  return runs;
}

/// The shader's `_SplatParams0.x`: `1f / CurveTextures[0].size.x`, computed in float32
/// (`e2dTerrainCurveMesh.RebuildMaterial`). Both operands are float32 already, so a double division
/// rounded once is the same number.
export function curveUScale(sizeX, check, label) {
  check(Number.isFinite(sizeX) && sizeX > 0, `${label}: CurveTextures[0].size.x must be positive, got ${sizeX}`);
  if (!Number.isFinite(sizeX) || sizeX <= 0) return null;
  return Math.fround(1 / Math.fround(sizeX));
}

/// One layer texture's own wrap mode, read from its `.meta`. The shader samples both layers with the
/// same u, which runs far past 1 (u = arclength * uScale, ~10 per metre in this pack), so `repeat`
/// tiles the art and `clamp` stretches the texture's last texel column -- measured 8 of the 16 layer
/// textures are Clamp, including most of the ground-texture layers.
export function readCurveTextureWrap(bpleRoot, assetPath, check) {
  const file = join(bpleRoot, `${assetPath}.meta`);
  check(existsSync(file), `${assetPath}: texture meta does not exist`);
  if (!existsSync(file)) return null;
  const importState = readImportState(bpleRoot, assetPath, check);
  check(
    importState.wrapU === importState.wrapV,
    `${assetPath}: wrapU ${importState.wrapU} != wrapV ${importState.wrapV}; the shader samples u and v with one sampler`,
  );
  if (importState.wrapU !== WRAP_REPEAT && importState.wrapU !== WRAP_CLAMP) return null;
  return importState.wrapU === WRAP_REPEAT ? "repeat" : "clamp";
}

/// The strip's two rows from the decoded mesh. `e2dTerrainCurveMesh.RebuildMesh` writes
/// `2 * nodeCount` vertices -- `array[2i]` the curve node, `array[2i + 1]` its stripe vertex -- and
/// `2 triangles = 6 indices` per segment, which is the invariant asserted here (measured 0
/// mismatches over 2146 terrains). The diagonal `RebuildMesh` picks is a `PointInTriangle` test on a
/// mesh whose vertices all share z, so both diagonals cover the same planar quad and the content
/// keeps neither.
export function curveRows(terrain, check, label) {
  const vertexCount = terrain.curve?.vertexCount ?? 0;
  const indexCount = terrain.curve?.indexCount ?? 0;
  check(vertexCount >= 4 && vertexCount % 2 === 0, `${label}: the curve mesh has ${vertexCount} vertices, expected an even count >= 4`);
  check(
    indexCount === (vertexCount / 2 - 1) * 6,
    `${label}: the curve mesh has ${indexCount} indices for ${vertexCount} vertices, expected ` +
      `${(vertexCount / 2 - 1) * 6} (two triangles per segment, e2dTerrainCurveMesh.RebuildMesh)`,
  );
  if (vertexCount < 4 || vertexCount % 2 !== 0) return null;
  const nodes = [];
  const stripe = [];
  for (let index = 0; index < vertexCount; index += 1) {
    const [x, y] = terrain.curve.vertices[index];
    check(Number.isFinite(x) && Number.isFinite(y), `${label}: curve vertex ${index} is not finite`);
    (index % 2 === 0 ? nodes : stripe).push([x, y]);
  }
  return { nodes, stripe };
}

/// One v4 `curve` block from a decoded terrain. `registerTexture(assetPath)` returns the file name the
/// document carries and records the asset (the caller owns the name-collision rule); it is called once
/// per layer texture, in slot order.
export function curveBlockOf({ terrain, referencePaths, bpleRoot, registerTexture, check, label }) {
  const rows = curveRows(terrain, check, label);
  if (!rows) return null;
  const table = terrain.curveTextures ?? [];
  check(table.length >= 2, `${label}: the terrain declares ${table.length} curve texture(s), expected at least 2`);
  if (table.length < 2) return null;
  const textures = [];
  for (let slot = 0; slot < 2; slot += 1) {
    const assetPath = referencePaths[table[slot].textureIndex];
    check(
      typeof assetPath === "string" && assetPath.endsWith(".png"),
      `${label}: curve texture ${slot} (index ${table[slot].textureIndex}) resolves to ${assetPath ?? "<out of range>"}, expected a PNG`,
    );
    if (typeof assetPath !== "string" || !assetPath.endsWith(".png")) return null;
    const wrap = readCurveTextureWrap(bpleRoot, assetPath, check);
    if (!wrap) return null;
    textures.push({ texture: registerTexture(assetPath), wrap });
  }
  const uScale = curveUScale(table[0].size[0], check, label);
  if (uScale === null) return null;
  const splat1 = curveLayerRuns(terrain.controlTexture, rows.nodes.length, check, label);
  if (!splat1) return null;
  return { nodes: rows.nodes, stripe: rows.stripe, textures, uScale, splat1 };
}
