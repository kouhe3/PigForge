namespace PigForge.Core;

public readonly record struct MotorState(float ImpulsePerTick, float DirectionX);

/// <summary>Charge state: <paramref name="ChainDetonate"/> ignites other charges inside
/// the blast radius (original TNT chains), <paramref name="IgniteOnImpact"/> false means
/// only the part switch can light it (original AlienTNT ignores collisions).</summary>
public readonly record struct TntState(ushort FuseTicks, bool Ignited, bool ChainDetonate = true, bool IgniteOnImpact = true);

public readonly record struct WheelMarker;

/// <summary>
/// A pig. <c>Pig.FixedUpdate</c> (<c>Pig.cs:249-262</c>) rewrites its own rigidbody's damping every
/// fixed step while the contraption runs: below <paramref name="RampSpeedThreshold"/> m/s both drags
/// become <c>RampBase + RampSlope * (1 - |v|)</c>, and at or above it both go back to the part's own
/// pair (<paramref name="OwnLinear"/>/<paramref name="OwnAngular"/>, i.e. the content's damping).
/// Only the original's <c>Pig</c> class declares that method — <c>KingPig</c> and <c>GoldenPig</c>
/// are <c>BasePart</c> subclasses — so only a part carrying content
/// <c>capabilities.dampingRamp</c> has a non-zero threshold; a zero threshold means no ramp.
/// Flattened into the store the way <see cref="FanState"/> and <see cref="RocketState"/> are.
/// </summary>
public readonly record struct PigMarker(
    float RampSpeedThreshold = 0f,
    float RampBase = 0f,
    float RampSlope = 0f,
    float OwnLinear = 0f,
    float OwnAngular = 0f)
{
    /// <summary>True for the class whose <c>FixedUpdate</c> ramps its damping (content
    /// <c>capabilities.dampingRamp</c>).</summary>
    public bool HasDampingRamp => RampSpeedThreshold > 0f;
}

/// <summary>Lift per tick applied straight up (a balloon supplies buoyancy).</summary>
public readonly record struct BalloonState(float LiftPerTick);

/// <summary>Air thrust per tick along a planar direction (a fan, a plane propeller and a rotor all
/// push the rig through the air — the original drives all three from one `FanPropeller`).
/// <paramref name="MaxSpeed"/> is the top speed along the thrust axis per unit engine power factor
/// (`m_defaultSpeed * IN &lt;X&gt;Speed`, FanPropeller.cs:90,100-106); 0 means the original never
/// caps it (the propeller, whose `PropellerSpeed` is Infinity). <paramref name="IsRotor"/> is
/// `m_isRotor`, which adds the overspeed brake at FanPropeller.cs:198-207.
/// See docs/specs/fan-propeller.md.</summary>
public readonly record struct FanState(float ImpulsePerTick, float DirectionX, float DirectionY, float MaxSpeed, bool IsRotor);

/// <summary>
/// A rocket's burn, which the original runs in three phases (Rocket.cs:228-300):
/// <paramref name="IgnitionTicks"/> first (<b>no thrust</b> for the bottle family, whose
/// <c>m_visualization</c> makes the class return early — <paramref name="Visualization"/>), then
/// <paramref name="BoostTicks"/> at full <paramref name="ThrustPerTick"/>, then
/// <paramref name="EndTicks"/> of linear ramp back to zero. <paramref name="MaxSpeed"/> is the
/// original's <c>m_maximumSpeed</c>: past that speed along the thrust axis the force is divided by
/// <c>1 + v - maxSpeed</c> (Rocket.cs:529-541). <paramref name="ElapsedTicks"/> counts from the
/// ignition tick (0 on it). At the end of the burn the part <b>stays</b> in the world — spent, with
/// no thrust — and a part with <paramref name="ExplodeRadius"/>/<paramref name="ExplodeImpulse"/>
/// blasts where it stands (the original's <c>m_explodes</c>, Rocket_03/RedRocket_03 only).
/// </summary>
public readonly record struct RocketState(
    float ThrustPerTick,
    float DirectionX,
    float DirectionY,
    ushort IgnitionTicks,
    ushort BoostTicks,
    ushort EndTicks,
    float MaxSpeed,
    bool Visualization,
    bool Ignited,
    uint ElapsedTicks = 0u,
    float ExplodeRadius = 0f,
    float ExplodeImpulse = 0f);

/// <summary>Fragile cargo marker: a hard impact (velocity change above the level's
/// eggBreakImpactSpeed) destroys the egg and requests a replay.</summary>
public readonly record struct EggMarker;

/// <summary>
/// A wing: the original's <c>Wings</c> class, whose only per-prefab number is
/// <c>m_liftConstant</c>. The force itself is the class's clamped <c>|v|^2</c> response curve
/// evaluated in the part's own frame (docs/specs/part-mirror.md, <see cref="Aerodynamics"/>).
/// </summary>
public readonly record struct WingState(float LiftConstant);

/// <summary>
/// A tail: the same response curve with its own coefficients and the original's
/// <c>0.4 * (num2 - 30)</c> twist, again with <c>m_liftConstant</c> as the only content value
/// (<see cref="Aerodynamics.TailAngleOfAttack"/>).
/// </summary>
public readonly record struct TailState(float LiftConstant);

/// <summary>Falling damper (umbrella): while the body descends (vy &lt; 0) an
/// upward impulse proportional to fall speed slows the drop; a black umbrella
/// makes a controlled leaping landing.</summary>
public readonly record struct UmbrellaState(float DragCoef);

public sealed class MotorStore(EntityStore entities) : ComponentStore<MotorState>(entities);

public sealed class BalloonStore(EntityStore entities) : ComponentStore<BalloonState>(entities);

public sealed class FanStore(EntityStore entities) : ComponentStore<FanState>(entities);

public sealed class RocketStore(EntityStore entities) : ComponentStore<RocketState>(entities);

public sealed class TntStore(EntityStore entities) : ComponentStore<TntState>(entities);

public sealed class WheelStore(EntityStore entities) : ComponentStore<WheelMarker>(entities);

public sealed class PigStore(EntityStore entities) : ComponentStore<PigMarker>(entities);

public sealed class EggStore(EntityStore entities) : ComponentStore<EggMarker>(entities);

public sealed class WingStore(EntityStore entities) : ComponentStore<WingState>(entities);

public sealed class TailStore(EntityStore entities) : ComponentStore<TailState>(entities);

public sealed class UmbrellaStore(EntityStore entities) : ComponentStore<UmbrellaState>(entities);

/// <summary>Reverse-gear marker: a gearbox on a body makes every motor on that
/// body push the opposite way (original gearbox lever direction switch).</summary>
public readonly record struct GearboxMarker;

/// <summary>
/// The original's puff (Bellows.cs:92-121): while <c>num &lt; 0.5 s</c> the force is
/// <c>(1 - (1 - num/0.5)^2) * m_boostForce</c> along the part's own +X, then nothing until the
/// cycle ends. <paramref name="Active"/>/<paramref name="ElapsedTicks"/> track that window, and
/// <c>ReadyAtTick</c> is the tick the cycle lets the bellows puff again (0.5 s + 0.3 s + the
/// skin's inflate, Bellows.cs:14-20,123-142). Firing is a button press, or a landing for legacy
/// content with no switch.
/// </summary>
public readonly record struct BellowsState(
    float ThrustPerTick,
    float DirectionX,
    float DirectionY,
    ushort InflateTicks,
    bool Active,
    uint ElapsedTicks,
    uint ReadyAtTick);

public sealed class GearboxStore(EntityStore entities) : ComponentStore<GearboxMarker>(entities);

public sealed class BellowsStore(EntityStore entities) : ComponentStore<BellowsState>(entities);

/// <summary>Detachar marker: when the rig carrying it takes a hard impact the part
/// detaches from the compound (original detacher part, seam split on impact).</summary>
public readonly record struct DetacherMarker;

public sealed class DetacherStore(EntityStore entities) : ComponentStore<DetacherMarker>(entities);

/// <summary>Grappling-hook launch: on touchdown the hook fires along the normalized
/// planar (<paramref name="DirectionX"/>, <paramref name="DirectionY"/>) direction
/// with a strong one-shot <paramref name="Impulse"/> (hook cast + pull merged);
/// re-arms when airborne.</summary>
public readonly record struct GrappleState(float Impulse, float DirectionX, float DirectionY, bool FiredRecently);

public sealed class GrappleStore(EntityStore entities) : ComponentStore<GrappleState>(entities);

/// <summary>One-shot shockwave (original BlasterTNT): the switch fires a single radial
/// impulse inside <paramref name="Radius"/> and sets off other blasters within
/// <paramref name="ChainRadius"/>; the part itself survives, spent.</summary>
public readonly record struct BlasterState(float Radius, float Impulse, float ChainRadius, bool Spent = false);

public sealed class BlasterStore(EntityStore entities) : ComponentStore<BlasterState>(entities);

/// <summary>Super-glue marker (original AlienEgg): the compound cluster holding this part
/// never splits along a seam while the part exists.</summary>
public readonly record struct GlueMarker;

public sealed class GlueStore(EntityStore entities) : ComponentStore<GlueMarker>(entities);

/// <summary>Switch state for a part whose content declares an activation. Absent = no switch.</summary>
public readonly record struct ActivationState(bool Active);

public sealed class ActivationStore(EntityStore entities) : ComponentStore<ActivationState>(entities);

/// <summary>Elasticity of a part: its content restitution and its own mass. A backend that
/// owns the restitution term (Jolt) applies it in the solver; for one that does not
/// (BepuPhysics v2 has no restitution term) the rules layer turns the reported pre-solve
/// contact impact into a bounce impulse scaled by the pair's reduced mass.</summary>
public readonly record struct RestitutionState(float Restitution, float Mass);

public sealed class RestitutionStore(EntityStore entities) : ComponentStore<RestitutionState>(entities);

/// <summary>
/// Power data of one part, straight from the original's serialized fields: the consumption it
/// adds to its cluster while its switch is on and the power it supplies to that cluster as an
/// engine (`m_powerConsumption`/`m_enginePower`, BasePart.cs:162,164). The consumption and engine
/// power mirror the content capability of the same name; <c>EngineEnclosed</c> is the placement
/// fact the engine needs to be a valid part at all -- <c>ValidatePart() =&gt; m_enclosedInto != null</c>
/// (Engine.cs:61), so an engine outside a frame supplies nothing (spec docs/specs/power-system.md §4 item 2).
/// </summary>
public readonly record struct PowerState(float PowerConsumption, float EnginePower, bool EngineEnclosed);

public sealed class PowerStore(EntityStore entities) : ComponentStore<PowerState>(entities);
