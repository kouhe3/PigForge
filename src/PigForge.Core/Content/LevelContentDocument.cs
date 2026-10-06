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
/// One of a level's terrain colliders, exactly as the original places one: an <c>e2dTerrain</c>
/// object at <see cref="Position"/> whose fill outline is extruded along z by <see cref="Depth"/>
/// (<c>LevelLoader.CreateCollider</c>, <c>LevelLoader.cs:339-380</c>). <see cref="Loops"/> holds the
/// terrain's boundary loops in its own local frame -- points are 2D, so their z is always zero and
/// the extrusion supplies it. The original walks the fill mesh's vertex list in order and calls that
/// the outline; the converter walks the real boundary instead, because 5 of the 1648
/// collider-carrying terrains are two loops pinched at a point or carry a vertex that is not on the
/// boundary at all (see <c>docs/specs/original-level-pack.md</c> §3).
/// </summary>
public sealed record LevelTerrainDefinition(
    PhysicsVector3 Position,
    float Depth,
    IReadOnlyList<IReadOnlyList<PhysicsVector3>> Loops);

/// <summary>Engine-agnostic level definition: spawns, goal trigger zone and map bounds (ADR-002).</summary>
public sealed record LevelContentDocument(
    string ContentVersion,
    GameplayZone GoalZone,
    GameplayZone MapBounds,
    IReadOnlyList<LevelSpawnDefinition> Spawns)
{
    public const string Format = "pigforge.level-content";

    /// <summary>The version this code writes: v2 adds the static-mesh <see cref="Terrain"/>.</summary>
    public const ushort SchemaVersion = 2;

    /// <summary>v1 documents (no terrain) keep parsing; both shipped levels are still v1.</summary>
    public const ushort LegacySchemaVersion = 1;

    /// <summary>Static triangle-mesh collision the level brings; empty on a v1 document.</summary>
    public IReadOnlyList<LevelTerrainDefinition> Terrain { get; init; } = Array.Empty<LevelTerrainDefinition>();
}
