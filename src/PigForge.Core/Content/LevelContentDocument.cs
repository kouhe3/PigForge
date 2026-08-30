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
    bool IsWheel = false);

/// <summary>Engine-agnostic level definition: spawns, goal trigger zone and map bounds (ADR-002).</summary>
public sealed record LevelContentDocument(
    string ContentVersion,
    GameplayZone GoalZone,
    GameplayZone MapBounds,
    IReadOnlyList<LevelSpawnDefinition> Spawns)
{
    public const string Format = "pigforge.level-content";
    public const ushort SchemaVersion = 1;
}
