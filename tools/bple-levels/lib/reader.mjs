// The original's binary level format (little-endian; `BinaryReader` semantics, so strings are
// 7-bit length prefixed UTF-8). LevelLoader.cs:88-208 is the working reader, LevelFormatReader.cs
// :9-124 the same thing with the Unity calls stripped out; both agree field for field.
//
//   int32 rootCount, then rootCount objects.
//   object  := int16 childCount
//              childCount == 0 -> prefab instance: string name; int16 prefabIndex;
//                                 Vector3 position, Vector3 euler, Vector3 localScale;
//                                 then data
//              childCount  > 0 -> group: string name; Vector3 position; childCount objects
//   data    := uint8 type: 0 none | 1 terrain | 2 prefabOverrides
//   terrain := float fillTextureTileOffsetX, float fillTextureTileOffsetY;
//              (int32 n, n * Vector2) fillVertices; (int32 m, m * int16) fillTriangles;
//              uint32 fillColor (RGBA bytes, read back through LevelLoader.ReadColor);
//              int32 fillTextureIndex (into the loader's `m_references`);
//              (int32 n, n * Vector2) curveVertices; (int32 m, m * int16) curveTriangles;
//              int32 curveTextureCount, then per entry: int32 textureIndex, Vector2 size,
//              bool fixedAngle, float fadeThreshold;
//              int32 controlFlag; if > 0: int32 byteLength + byteLength bytes (a PNG);
//              bool hasCollider
//   overrides := int32 byteLength + byteLength bytes (UTF-8, ObjectDeserializer format)
//
// The triangle indices are int16: a version of this tool that read them as int32 desynchronised
// the whole stream. Every instance's position/scale is multiplied by `IN TerrainScale x user
// TerrainScale` (LevelLoader.cs:186-193, declared 1.0 in INDeclarationSettingsExp.json), so
// vanilla reading needs no extra factor; a terrain with `hasCollider` extrudes its **fill**
// polygon (LevelLoader.cs:339-380). This module is the reader only: it returns positions and
// meshes, never interpreted content.

class Reader {
  constructor(buffer, check) {
    this.buffer = buffer;
    this.offset = 0;
    this.check = check;
  }
  int16() {
    const value = this.buffer.readInt16LE(this.offset);
    this.offset += 2;
    return value;
  }
  int32() {
    const value = this.buffer.readInt32LE(this.offset);
    this.offset += 4;
    return value;
  }
  uint32() {
    const value = this.buffer.readUInt32LE(this.offset);
    this.offset += 4;
    return value;
  }
  float() {
    const value = this.buffer.readFloatLE(this.offset);
    this.offset += 4;
    return value;
  }
  byte() {
    return this.buffer[this.offset++];
  }
  bool() {
    return this.byte() !== 0;
  }
  string() {
    let length = 0;
    let shift = 0;
    for (;;) {
      const byte = this.byte();
      length |= (byte & 0x7f) << shift;
      if ((byte & 0x80) === 0) break;
      shift += 7;
    }
    const value = this.buffer.toString("utf8", this.offset, this.offset + length);
    this.offset += length;
    return value;
  }
  vector2() {
    return [this.float(), this.float()];
  }
  vector3() {
    return [this.float(), this.float(), this.float()];
  }
  /// Reads a mesh block. `vertices` and `indices` keep the position list / triangle indices;
  /// without them the block is skipped at its known stride. Both meshes are kept: the fill mesh
  /// (whose boundary is the collider outline and the drawing) and the curve mesh (the edge trim's
  /// two rows).
  mesh({ vertices = false, indices = false } = {}) {
    const vertexCount = this.int32();
    this.check(vertexCount >= 0, `negative vertex count ${vertexCount}`);
    let positions = null;
    if (vertices) {
      positions = new Array(vertexCount);
      for (let index = 0; index < vertexCount; index += 1) positions[index] = this.vector2();
    } else {
      this.offset += vertexCount * 8;
    }
    const indexCount = this.int32();
    this.check(indexCount >= 0, `negative index count ${indexCount}`);
    const triangles = indices ? new Array(indexCount) : null;
    if (triangles) {
      for (let index = 0; index < indexCount; index += 1) {
        triangles[index] = this.int16();
      }
    } else {
      this.offset += indexCount * 2;
    }

    return { vertexCount, indexCount, vertices: positions, indices: triangles };
  }
}

/// Decodes one `<scene>_data.bytes`. Every check goes through `check` (a failure recorder the
/// caller owns), so the extractor reports drift exactly as it always did.
///
/// Returned shape:
//   {
//     rootCount, groups, instances,                     // counts
//     instanceList: [{ name, prefabIndex, position, euler, localScale, dataType }],
//     terrain: [{ instance, fillOffset, fill, fillColor, fillTextureIndex, curve,
//                 curveTextureCount, controlTextureBytes, hasCollider }],
//     overrides: [{ node, text }], overrideBytes, prefabIndexes, instanceNames, maxDepth, trailingBytes,
//     terrainTransforms, goalInstances                   // derived, for the report
//   }
/// `fill.vertices` / `fill.indices` are the raw 2D fill polygon the collider is built from;
/// `instance.position` is the world placement (TerrainScale 1.0), so a terrain entry is
/// `{ position: instance.position, depth, loops: boundary loops of fill }`.
export function readLevel(buffer, check = (condition, message) => { if (!condition) throw new Error(message); }) {
  const reader = new Reader(buffer, check);
  const level = {
    rootCount: 0,
    groups: 0,
    instances: 0,
    instanceList: [],
    terrain: [],
    overrides: [],
    overrideBytes: [],
    prefabIndexes: new Map(),
    instanceNames: new Map(),
    maxDepth: 0,
  };
  const readData = (node) => {
    const type = reader.byte();
    node.dataType = type;
    if (type === 1) {
      const fillOffset = reader.vector2();
      const fill = reader.mesh({ vertices: true, indices: true });
      const fillColor = reader.uint32();
      const fillTextureIndex = reader.int32();
      const curve = reader.mesh({ vertices: true, indices: true });
      const curveTextureCount = reader.int32();
      const curveTextures = [];
      for (let index = 0; index < curveTextureCount; index += 1) {
        curveTextures.push({
          textureIndex: reader.int32(),
          size: reader.vector2(),
          fixedAngle: reader.bool(),
          fadeThreshold: reader.float(),
        });
      }
      let controlTextureBytes = 0;
      let controlTexture = null;
      if (reader.int32() > 0) {
        controlTextureBytes = reader.int32();
        // The control texture is the PNG the editor wrote next to the mesh: one pixel high, one
        // texel per curve node, and its channels say which `e2dCurveTexture` layer a node uses.
        controlTexture = Buffer.from(reader.buffer.subarray(reader.offset, reader.offset + controlTextureBytes));
        reader.offset += controlTextureBytes;
      }
      const hasCollider = reader.bool();
      level.terrain.push({
        instance: node,
        fillOffset,
        fill,
        fillColor,
        fillTextureIndex,
        curve,
        curveTextureCount,
        curveTextures,
        controlTextureBytes,
        controlTexture,
        hasCollider,
      });
    } else if (type === 2) {
      const length = reader.int32();
      // `Overrides` is UTF-8 text in the original's own `ObjectDeserializer` format (one
      // tab-indented property per line); LevelLoader.cs:198-208 hands it to ReadFile, which
      // reconstructs the instance's components from it. Kept verbatim so a consumer decides
      // which fields matter.
      const text = reader.buffer.toString("utf8", reader.offset, reader.offset + length);
      reader.offset += length;
      level.overrideBytes.push(length);
      level.overrides.push({ node, text });
    }
  };
  const readObject = (depth) => {
    const childCount = reader.int16();
    level.maxDepth = Math.max(level.maxDepth, depth);
    if (childCount === 0) {
      const name = reader.string();
      const prefabIndex = reader.int16();
      const position = reader.vector3();
      const euler = reader.vector3();
      const localScale = reader.vector3();
      level.instances += 1;
      level.prefabIndexes.set(prefabIndex, (level.prefabIndexes.get(prefabIndex) ?? 0) + 1);
      level.instanceNames.set(name, (level.instanceNames.get(name) ?? 0) + 1);
      const node = { name, prefabIndex, position, euler, localScale, dataType: 0 };
      level.instanceList.push(node);
      readData(node);
    } else {
      reader.string();
      reader.vector3();
      level.groups += 1;
      for (let index = 0; index < childCount; index += 1) readObject(depth + 1);
    }
  };
  level.rootCount = reader.int32();
  for (let index = 0; index < level.rootCount; index += 1) readObject(0);
  level.trailingBytes = buffer.length - reader.offset;

  // Derived views the extractor's report reads; `e2dTerrain` names the terrain objects and
  // `Goal*` names the finish trigger (`LevelLoader.cs:104-138` keeps the label as the name).
  level.terrainTransforms = level.instanceList
    .filter((instance) => instance.name.includes("e2dTerrain"))
    .map((instance) => ({ euler: instance.euler, localScale: instance.localScale }));
  level.goalInstances = level.instanceList
    .filter((instance) => /^Goal/i.test(instance.name))
    .map((instance) => ({
      name: instance.name,
      prefabIndex: instance.prefabIndex,
      position: instance.position,
      euler: instance.euler,
      localScale: instance.localScale,
    }));
  return level;
}
