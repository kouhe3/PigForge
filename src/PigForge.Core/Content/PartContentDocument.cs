using PigForge.Physics.Abstractions;

namespace PigForge.Core.Content;

/// <summary>Engine-agnostic part and collision content, authored as JSON against part-content-v1.</summary>
public sealed record PartContentDocument(
    string ContentVersion,
    PartContentPhysics Physics,
    IReadOnlyList<PartDefinition> Parts)
{
    public const string Format = "pigforge.part-content";
    public const ushort SchemaVersion = 1;
}

/// <summary>
/// The original's project-wide physics defaults that every rigidbody inherits, rather than a
/// per-part choice: extracted from its own <c>ProjectSettings</c> and from
/// <c>BasePart.EnsureRigidbody</c> by <c>tools/bple-damping</c>.
/// <see cref="MaximumAngularSpeed"/> is Unity's <c>Physics.defaultMaxAngularSpeed</c> — the
/// original's project declares 7 rad/s (<c>ProjectSettings/DynamicsManager.asset</c>, field
/// <c>m_DefaultMaxAngularSpeed</c>), no script overrides it per body and none of the 343
/// <c>Part_*.prefab</c> serialize <c>m_MaxAngularVelocity</c>, so every original part is clamped
/// to that magnitude (<c>DyBodyCoreIntegrator.h::bodyCoreComputeUnconstrainedVelocity</c>).
/// <see cref="Damping"/> is the pair <c>BasePart.EnsureRigidbody</c> gives every part unless its
/// class overrides it (<c>BasePart.cs:1200-1201</c>), so content writes a per-part
/// <see cref="PartDefinition.Damping"/> only where a class does (wing, tail, balloon, sandbag,
/// king pig).
/// </summary>
public sealed record PartContentPhysics(float MaximumAngularSpeed, PartDamping Damping);

/// <summary>
/// Unity's per-rigidbody <c>Rigidbody.drag</c> / <c>angularDrag</c> (renamed
/// <c>linearDamping</c> / <c>angularDamping</c> in Unity 6), extracted per part class by
/// <c>tools/bple-damping</c>: 0.2 / 0.05 from <c>BasePart.EnsureRigidbody</c>
/// (<c>BasePart.cs:1192-1205</c>), 1 / 0.2 on a wing (<c>Wings.cs:91-102</c>) and a tail
/// (<c>Tail.cs:44-55</c>), 2 / 0.5 on a balloon (<c>Balloon.cs:83,129-132</c>), 1 / 10 on a
/// sandbag (<c>Sandbag.cs:61,132-135</c>) and 0.5 / 1 on the king pig (<c>KingPig.cs:79-82</c>).
/// A dynamic part gets <see cref="PartContentPhysics.Damping"/> unless it declares its own; a
/// static part must not declare one, because the original's static level pieces are not
/// rigidbodies at all.
/// </summary>
public readonly record struct PartDamping(float Linear, float Angular);

public sealed record PartDefinition(
    uint PartTypeId,
    string Name,
    PhysicsBodyMode Mode,
    float Mass,
    float Restitution,
    float Friction,
    IReadOnlyList<PartShapeDefinition> Shapes,
    FrictionCombine FrictionCombine = FrictionCombine.Average,
    PartCapabilities? Capabilities = null,
    uint? VariantOf = null,
    string? VariantName = null,
    GridCellBox? GridBox = null,
    ConnectionVisualKind? ConnectionVisual = null,
    PartDamping? Damping = null);

/// <summary>
/// The script the original prefab mounts to decide which of a part's conditional colliders are
/// solid for the current connection state (<c>Rocket.cs:139-165</c>, <c>TNT.cs:86-108</c>,
/// <c>SpotLight.cs:70-105</c>, <c>GrapplingHook.cs:218-255</c>, <c>Wings.cs:41-75</c>). The
/// client renderer reads the same value from the sprite manifest (ADR-014); content carries it
/// so the server can mirror the rule instead of guessing it from shape kinds
/// (<c>tools/bple-connections</c>).
/// </summary>
public enum ConnectionVisualKind
{
    /// <summary>Rocket.cs: a side marker follows its own direction, and the bottom marker is also
    /// the ghost shown when no other side can connect.</summary>
    AttachmentFallback = 0,

    /// <summary>TNT.cs: each side marker follows its own direction only, with no fallback.</summary>
    AttachmentPlain = 1,

    /// <summary>SpotLight.cs/GrapplingHook.cs: eight markers; the diagonals show only while the
    /// part sits on a 45-degree turn, the orthogonal ones otherwise.</summary>
    AttachmentEight = 2,

    /// <summary>Wings.cs/JetEngine.cs: two mutually exclusive mounts (top and bottom), and the
    /// part's root collider switches between its thin top-only and thick bottom form.</summary>
    Frame = 3
}

/// <summary>
/// The original's per-part build-grid cell box: the <b>inclusive</b> rectangle of build-grid
/// cells a part covers around its own coordinate (<c>BasePart.cs:197-200</c> declares the four
/// serialized fields; <c>ConstructionUI.cs:1279,1344</c> tests membership as
/// <c>x + MinX &lt;= cellX &amp;&amp; x + MaxX &gt;= cellX</c> and iterates the same inclusive
/// range when clearing the cells a placement covers). Extracted per prefab by
/// <c>tools/bple-grid</c>, never authored: 332 of the original's 343 prefabs declare the default
/// single cell at the origin -- every propeller, fan, wing, spotlight, grapple, spring and pig --
/// and only the KingPig/GoldenPig families declare the 3x2 box <c>x[-1, 1] y[0, 1]</c>.
/// <para>
/// Cell (0,0) is the cell the part stands in (<c>ConstructionUI.GridPositionToWorldPosition</c>
/// maps grid coordinate <c>(x, y)</c> to <c>contraption.position + right*x + up*y</c>), so the box
/// is centred half a cell outside each bound and this is the original's own account of the space a
/// part blocks. A collider that overhangs a neighbouring cell does <b>not</b> occupy it.
/// </para>
/// </summary>
public sealed record GridCellBox(int MinX, int MaxX, int MinY, int MaxY)
{
    /// <summary>
    /// The original's default: one cell, the one the part stands in. Content omits
    /// <c>gridBox</c> for the parts whose prefab declares exactly this box, and absence is read as
    /// this value -- the same shape as <c>capabilities.canEnclose</c> being written only for frames.
    /// </summary>
    public static GridCellBox Single { get; } = new(0, 0, 0, 0);

    /// <summary>Cells covered along X (<see cref="MaxX"/> inclusive).</summary>
    public int Width => MaxX - MinX + 1;

    /// <summary>Cells covered along Y (<see cref="MaxY"/> inclusive).</summary>
    public int Height => MaxY - MinY + 1;

    /// <summary>Centre of the box relative to the part origin, in cells.</summary>
    public float CentreX => (MinX + MaxX) / 2f;

    /// <summary>Centre of the box relative to the part origin, in cells.</summary>
    public float CentreY => (MinY + MaxY) / 2f;
}

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
/// The original's per-part <c>m_jointConnectionDirection</c> (BasePart.cs:118-127): the
/// part-local sides this part may weld on. Extracted per prefab by <c>tools/bple-joints</c>;
/// the client's build-time alignment only snaps a part on the sides it declares.
/// </summary>
public enum JointConnectionDirection
{
    Any = 0,
    Right = 1,
    Up = 2,
    Left = 3,
    Down = 4,
    LeftAndRight = 5,
    UpAndDown = 6,
    None = 7
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
    // The original's FanPropeller (spec docs/specs/fan-propeller.md). `FanMaxSpeed` is
    // `m_defaultSpeed * IN <X>Speed` (FanPropeller.cs:90,100-106) -- the top speed along the
    // thrust axis per unit of engine power factor, which the rules layer multiplies by its
    // cluster's factor. 0 means the original never caps it (`PropellerSpeed = Infinity`).
    // `FanIsRotor` is `m_isRotor`, which adds the overspeed brake (FanPropeller.cs:198-207).
    float? FanMaxSpeed = null,
    bool FanIsRotor = false,
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
    JointConnectionDirection JointConnectionDirection = JointConnectionDirection.Any,
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
    float[]? Offset,
    string? ConditionSide = null,
    string? ConditionKind = null)
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
