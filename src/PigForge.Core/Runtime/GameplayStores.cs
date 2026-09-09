namespace PigForge.Core;

public readonly record struct MotorState(float ImpulsePerTick, float DirectionX);

/// <summary>Charge state: <paramref name="ChainDetonate"/> ignites other charges inside
/// the blast radius (original TNT chains), <paramref name="IgniteOnImpact"/> false means
/// only the part switch can light it (original AlienTNT ignores collisions).</summary>
public readonly record struct TntState(ushort FuseTicks, bool Ignited, bool ChainDetonate = true, bool IgniteOnImpact = true);

public readonly record struct WheelMarker;

public readonly record struct PigMarker;

/// <summary>Lift per tick applied straight up (a balloon supplies buoyancy).</summary>
public readonly record struct BalloonState(float LiftPerTick);

/// <summary>Air thrust per tick along a planar direction (a fan/propeller pushes the
/// rig through the air).</summary>
public readonly record struct FanState(float ImpulsePerTick, float DirectionX, float DirectionY);

/// <summary>Bounce impulse applied once per ground contact (a springboard launches
/// the rig upward each time it lands; the state clears when the body leaves the
/// ground so a later touchdown bounces again).</summary>
public readonly record struct SpringState(float BounceImpulsePerTick, bool BouncedRecently);

/// <summary>Rocket thrust: auto-ignites on simulation start, applies
/// <paramref name="ThrustPerTick"/> along the normalized planar
/// (<paramref name="DirectionX"/>, <paramref name="DirectionY"/>) direction for
/// <paramref name="DurationTicks"/>, then self-destructs — exploding with
/// <paramref name="ExplodeRadius"/>/<paramref name="ExplodeImpulse"/> when either is
/// positive (fireworks semantics; 0/0 = plain burn-out).</summary>
public readonly record struct RocketState(
    float ThrustPerTick,
    float DirectionX,
    float DirectionY,
    ushort DurationTicks,
    bool Ignited,
    float ExplodeRadius = 0f,
    float ExplodeImpulse = 0f);

/// <summary>Fragile cargo marker: a hard impact (velocity change above the level's
/// eggBreakImpactSpeed) destroys the egg and requests a replay.</summary>
public readonly record struct EggMarker;

/// <summary>Aerodynamic lift: a wing generates vertical lift proportional to the
/// square of the body's horizontal speed, capped at <paramref name="MaxLift"/>.
/// A glider stays airborne once it is moving fast enough sideways.</summary>
public readonly record struct WingState(float LiftCoef, float MaxLift);

/// <summary>Velocity damper (tailplane stability): a tail applies a linear
/// drag impulse opposite the body velocity so the glider does not spin or
/// accelerate out of control.</summary>
public readonly record struct TailState(float DragCoef);

/// <summary>Falling damper (umbrella): while the body descends (vy &lt; 0) an
/// upward impulse proportional to fall speed slows the drop; a black umbrella
/// makes a controlled leaping landing.</summary>
public readonly record struct UmbrellaState(float DragCoef);

public sealed class MotorStore(EntityStore entities) : ComponentStore<MotorState>(entities);

public sealed class BalloonStore(EntityStore entities) : ComponentStore<BalloonState>(entities);

public sealed class FanStore(EntityStore entities) : ComponentStore<FanState>(entities);

public sealed class SpringStore(EntityStore entities) : ComponentStore<SpringState>(entities);

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

/// <summary>Forward boost: a bellows emits a one-shot impulse along its facing
/// direction each time the rig lands (original m_boostForce jet).</summary>
public readonly record struct BellowsState(float BoostImpulse, bool BoostedRecently);

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
