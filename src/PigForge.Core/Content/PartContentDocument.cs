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
    PartCapabilities? Capabilities = null,
    uint? VariantOf = null,
    string? VariantName = null);

/// <summary>How a part's switch behaves: a persistent on/off effect or a one-shot action.</summary>
public enum PartActivation
{
    None,
    Toggle,
    Trigger
}

/// <summary>
/// The original's <c>BasePart.m_jointConnectionType</c> (BasePart.cs:111-116): whether a
/// part offers a weld (<see cref="Source"/>), accepts one (<see cref="Target"/>), or
/// refuses every weld (<see cref="None"/>). It is a per-part value, not a frame-vs-rest
/// flag: two parts may be jointed only when neither is <see cref="None"/> and at least
/// one is <see cref="Source"/> (Contraption.cs:690).
/// </summary>
public enum JointConnectionType
{
    None = 0,
    Source = 1,
    Target = 2
}

/// <summary>
/// The original's per-part <c>m_jointConnectionStrength</c> enum (BasePart.cs:130-137). The
/// floats live in <c>GameData.asset:101-105</c> and are resolved by
/// <see cref="Construction.CompoundAssembler"/>; <see cref="None"/> means the part is not
/// mapped to a prefab, and everything then falls back to <see cref="Normal"/>.
/// </summary>
public enum JointConnectionStrength
{
    None = 0,
    Weak = 1,
    Normal = 2,
    High = 3,
    Extreme = 4,
    HighlyExtreme = 5
}

/// <summary>
/// Which way a runtime attachment searches for its anchor (Sandbag.cs:96 searches
/// <c>m_direction = Vector3.up</c> and hangs below what it finds; Balloon.cs:104 searches
/// downward and floats above what it finds), and on which side of the anchor it rests.
/// </summary>
public enum AttachmentDirection
{
    Up,
    Down
}

/// <summary>
/// A runtime attachment: the part is tied to a released chassis by a rope joint built at
/// start of simulation, not by the design-time <see cref="JointConnectionType"/> rule
/// (balloon string, sandbag tie — Sandbag.cs:136-164, Balloon.cs:143-166). The build
/// searches one direction for its anchor and hangs/floats a fixed offset away.
/// </summary>
public sealed record PartAttachment(
    AttachmentDirection Direction,
    float MaxDistance = 0f,
    PhysicsVector3 Offset = default,
    float? DistanceFactor = null,
    float? DistanceOffset = null,
    float? PigDistanceBonus = null);

/// <summary>
/// The elastic wheel attachment of a part that overrides the original's
/// <c>BasePart.CustomConnectToPart</c> with a linear-limit spring
/// (OffRoadWheel.cs:202-220): the wheel's own joint locks every rotation and the
/// translations except its local Y, which is <c>Limited</c> and held at
/// <see cref="RestOffset"/> by <see cref="Stiffness"/> N/m with <see cref="Damper"/>
/// N·s/m. The axis is not content — it is the wheel's build-frame Y, perpendicular to
/// its axle, and the joint attaches it to the non-spinning parent body so a rolling
/// wheel cannot carry the suspension line around with it.
/// </summary>
public sealed record PartSuspension(
    float Stiffness,
    float Damper,
    float RestOffset);

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
    float? RocketDirectionY = null,
    ushort? RocketDurationTicks = null,
    float? RocketExplodeRadius = null,
    float? RocketExplodeImpulse = null,
    bool IsEgg = false,
    float? WingLiftCoef = null,
    float? WingMaxLift = null,
    float? TailDragCoef = null,
    float? UmbrellaDragCoef = null,
    bool IsGearbox = false,
    bool IsDetacher = false,
    float? BellowsBoostImpulse = null,
    float? LightRadius = null,
    float? GrappleImpulse = null,
    float? GrappleDirectionX = null,
    float? GrappleDirectionY = null,
    PartActivation Activation = PartActivation.None,
    bool TntChainDetonate = true,
    bool TntIgniteOnImpact = true,
    float? BlasterRadius = null,
    float? BlasterImpulse = null,
    float? BlasterChainRadius = null,
    bool IsGlue = false,
    JointConnectionType JointConnectionType = JointConnectionType.None,
    JointConnectionStrength JointConnectionStrength = JointConnectionStrength.None,
    bool CanEnclose = false,
    PartAttachment? Attachment = null,
    PartSuspension? Suspension = null,
    // Power system (spec docs/specs/power-system.md). Both come straight from the original
    // part prefabs: BasePart.cs:162,164 declare them, the template copies them at
    // BasePart.cs:1445-1446, and `tools/bple-power` extracts them for every mapped part --
    // never authored by hand. 0 means "not a consumer" / "not an engine" (the original
    // serializes both fields on every part, so 0 is the absent value).
    float PowerConsumption = 0f,
    float EnginePower = 0f)
{
    public bool HasMotor => MotorThrustPerTick is float thrust && thrust != 0f;

    /// <summary>
    /// True when the part's own joint is the original's spring-suspended wheel attachment
    /// instead of a rigid axle weld (see <see cref="PartSuspension"/>).
    /// </summary>
    public bool HasSuspension => Suspension is not null;

    public bool HasBalloon => BalloonLiftPerTick is float lift && lift != 0f;

    public bool HasFan => FanThrustPerTick is float thrust && thrust != 0f;

    public bool HasSpring => SpringBounceImpulsePerTick is float bounce && bounce != 0f;

    public bool HasRocket => RocketThrustPerTick is float thrust && thrust != 0f;

    public bool HasWing => WingLiftCoef is float lift && lift != 0f;

    public bool HasTail => TailDragCoef is float drag && drag != 0f;
    public bool HasUmbrella => UmbrellaDragCoef is float drag && drag != 0f;


    public bool HasBellows => BellowsBoostImpulse is float boost && boost != 0f;

    public bool HasGrapple => GrappleImpulse is float impulse && impulse != 0f;

    public bool HasBlaster => BlasterRadius is float radius && radius != 0f;

    /// <summary>
    /// A consumer: <c>BasePart.IsPowered()</c> (BasePart.cs:601-603). While its switch is on it
    /// adds <see cref="PowerConsumption"/> to its cluster's consumption, the denominator of the
    /// power factor (Contraption.cs:540-556, :2633-2644).
    /// </summary>
    public bool IsPowered => PowerConsumption > 0f;

    /// <summary>
    /// A power source: <c>BasePart.IsEngine()</c> (BasePart.cs:606-608). While enclosed
    /// (Engine.cs:61 <c>ValidatePart</c>) it adds <see cref="EnginePower"/> to its cluster's
    /// engine power, the numerator of the same factor. It never applies force itself
    /// (Engine.cs:29,138).
    /// </summary>
    public bool IsEngine => EnginePower > 0f;

    /// <summary>
    /// The original derives this: <c>BasePart.CanBeEnclosed()</c> is true for every part
    /// except a frame, and only a frame's <c>CanEncloseParts()</c> returns true
    /// (BasePart.cs:1143-1166, Frame.cs:32). Derived here, never authored in content.
    /// </summary>
    public bool CanBeEnclosed => !CanEnclose;
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
