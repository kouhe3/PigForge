namespace PigForge.Core;

public readonly record struct MotorState(float ImpulsePerTick, float DirectionX);

public readonly record struct TntState(ushort FuseTicks, bool Ignited);

public readonly record struct WheelMarker;

public readonly record struct PigMarker;

public sealed class MotorStore(EntityStore entities) : ComponentStore<MotorState>(entities);

public sealed class TntStore(EntityStore entities) : ComponentStore<TntState>(entities);

public sealed class WheelStore(EntityStore entities) : ComponentStore<WheelMarker>(entities);

public sealed class PigStore(EntityStore entities) : ComponentStore<PigMarker>(entities);
