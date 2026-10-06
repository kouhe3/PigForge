using PigForge.Physics.Abstractions;

namespace PigForge.Core.Content;

public enum LevelActorRole
{
    Part,
    Pig,
    Tnt
}

public sealed record LevelSpawnDefinition(
    uint PartTypeId,
    PhysicsVector3 Position,
    LevelActorRole Role = LevelActorRole.Part,
    ushort TntFuseTicks = 1,
    float MotorImpulsePerTick = 0f,
    float MotorDirectionX = 0f,
    bool IsWheel = false,
    float Angle = 0f);

/// <summary>
/// The ground's look of one terrain, as the original's <c>e2d/Fill</c> shader draws it:
/// <c>tex2D(_MainTex, uv) * _Color</c> with <c>uv = (world - tileOffset) / tileSize</c>
/// (<c>LevelLoader.cs:214-230</c> writes the fill material and <c>:279-290</c> computes the UVs per
/// vertex). <see cref="Texture"/> is a file name inside the client's level-texture directory -- the
/// document never carries a path, and the original art is not in this repository. The color is the
/// level file's own RGBA quad (<c>LevelLoader.ReadColor</c> multiplies each byte by 0.003921569f),
/// <see cref="TileOffsetX"/>/<see cref="TileOffsetY"/> come from the level file and
/// <see cref="TileWidth"/>/<see cref="TileHeight"/> from the terrain prefab -- the one input the level
/// file does not carry. See <c>docs/specs/level-terrain-visuals.md</c>.
/// </summary>
public sealed record LevelTerrainFillDefinition(
    string Texture,
    byte Red,
    byte Green,
    byte Blue,
    byte Alpha,
    float TileOffsetX,
    float TileOffsetY,
    float TileWidth,
    float TileHeight);

/// <summary>
/// One of a level's terrain objects, exactly as the original places one: an <c>e2dTerrain</c> at
/// <see cref="Position"/> whose fill outline is extruded along z by <see cref="Depth"/>
/// (<c>LevelLoader.CreateCollider</c>, <c>LevelLoader.cs:339-380</c>). <see cref="Loops"/> holds the
/// terrain's boundary loops in its own local frame -- points are 2D, so their z is always zero and
/// the extrusion supplies it. The original walks the fill mesh's vertex list in order and calls that
/// the outline; the converter walks the real boundary instead, because 5 of the 1648
/// collider-carrying terrains are two loops pinched at a point or carry a vertex that is not on the
/// boundary at all (see <c>docs/specs/original-level-pack.md</c> §3).
/// <see cref="Collider"/> is the object's own <c>hasCollider</c>: 498 of the original's 2146 terrains
/// are decoration and never enter the world as a body, but they still draw. <see cref="Fill"/> is the
/// ground's texture/tint/tiling on a v3 document; a v1/v2 document carries neither.
/// </summary>
public sealed record LevelTerrainDefinition(
    PhysicsVector3 Position,
    float Depth,
    IReadOnlyList<IReadOnlyList<PhysicsVector3>> Loops,
    bool Collider = true,
    LevelTerrainFillDefinition? Fill = null);

/// <summary>Engine-agnostic level definition: spawns, goal trigger zone and map bounds (ADR-002).</summary>
public sealed record LevelContentDocument(
    string ContentVersion,
    GameplayZone GoalZone,
    GameplayZone MapBounds,
    IReadOnlyList<LevelSpawnDefinition> Spawns)
{
    public const string Format = "pigforge.level-content";

    /// <summary>The version this code writes: v3 gives every terrain its collider bit and its fill.</summary>
    public const ushort SchemaVersion = 3;

    /// <summary>v1 documents (no terrain) keep parsing, and so do v2 ones (terrain, no fill).</summary>
    public const ushort LegacySchemaVersion = 1;

    /// <summary>Static triangle-mesh collision the level brings; empty on a v1 document.</summary>
    public IReadOnlyList<LevelTerrainDefinition> Terrain { get; init; } = Array.Empty<LevelTerrainDefinition>();
}
