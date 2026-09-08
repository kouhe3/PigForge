namespace PigForge.Core;

public readonly record struct MotorState(float ImpulsePerTick, float DirectionX);

public readonly record struct TntState(ushort FuseTicks, bool Ignited);

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
/// <paramref name="ThrustPerTick"/> along <paramref name="DirectionX"/> for
/// <paramref name="DurationTicks"/>, then the part self-destructs.</summary>
public readonly record struct RocketState(float ThrustPerTick, float DirectionX, ushort DurationTicks, bool Ignited);

public sealed class MotorStore(EntityStore entities) : ComponentStore<MotorState>(entities);

public sealed class BalloonStore(EntityStore entities) : ComponentStore<BalloonState>(entities);

public sealed class FanStore(EntityStore entities) : ComponentStore<FanState>(entities);

public sealed class SpringStore(EntityStore entities) : ComponentStore<SpringState>(entities);

public sealed class RocketStore(EntityStore entities) : ComponentStore<RocketState>(entities);

public sealed class TntStore(EntityStore entities) : ComponentStore<TntState>(entities);

public sealed class WheelStore(EntityStore entities) : ComponentStore<WheelMarker>(entities);

public sealed class PigStore(EntityStore entities) : ComponentStore<PigMarker>(entities);
