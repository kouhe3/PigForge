using PigForge.Physics.Abstractions;

namespace PigForge.Core.Content;

/// <summary>Engine-agnostic part and collision content, authored as JSON against part-content-v1.</summary>
public sealed record PartContentDocument(
    string ContentVersion,
    IReadOnlyList<PartDefinition> Parts)
{
    public const string Format = "pigforge.part-content";
    public const ushort SchemaVersion = 1;
}

public sealed record PartDefinition(
    uint PartTypeId,
    string Name,
    PhysicsBodyMode Mode,
    float Mass,
    float Restitution,
    float Friction,
    IReadOnlyList<PartShapeDefinition> Shapes,
    PartCapabilities? Capabilities = null);

/// <summary>
/// Gameplay capabilities a part carries (ADR-002): a pig is indestructible bouncy
/// cargo, a wheel gates motor thrust to ground contact, a motor pushes the body each
/// tick, and TNT is a pure momentum source with a fuse. Absence of a flag means the
/// part has no such role; capabilities are content, not code constants.
/// </summary>
public sealed record PartCapabilities(
    bool IsPig = false,
    bool IsWheel = false,
    float? MotorThrustPerTick = null,
    float? MotorDirectionX = null,
    ushort? TntFuseTicks = null,
    float? BalloonLiftPerTick = null,
    float? FanThrustPerTick = null,
    float? FanDirectionX = null,
    float? FanDirectionY = null,
    float? SpringBounceImpulsePerTick = null,
    float? RocketThrustPerTick = null,
    float? RocketDirectionX = null,
    ushort? RocketDurationTicks = null)
{
    public bool HasMotor => MotorThrustPerTick is float thrust && thrust != 0f;

    public bool HasBalloon => BalloonLiftPerTick is float lift && lift != 0f;

    public bool HasFan => FanThrustPerTick is float thrust && thrust != 0f;

    public bool HasSpring => SpringBounceImpulsePerTick is float bounce && bounce != 0f;

    public bool HasRocket => RocketThrustPerTick is float thrust && thrust != 0f;
}

public sealed record PartShapeDefinition(
    PhysicsShapeKind Kind,
    float[]? BoxHalfExtents,
    float? Radius,
    float? CylinderHalfHeight,
    float[][]? Vertices,
    uint[]? Triangles,
    float[]? Offset)
{
    public static PartShapeDefinition Box(float halfExtentX, float halfExtentY, float halfExtentZ) => new(
        PhysicsShapeKind.Box,
        new[] { halfExtentX, halfExtentY, halfExtentZ },
        Radius: null,
        CylinderHalfHeight: null,
        Vertices: null,
        Triangles: null,
        Offset: null);

    public static PartShapeDefinition Sphere(float radius) => new(
        PhysicsShapeKind.Sphere,
        BoxHalfExtents: null,
        radius,
        CylinderHalfHeight: null,
        Vertices: null,
        Triangles: null,
        Offset: null);
}
