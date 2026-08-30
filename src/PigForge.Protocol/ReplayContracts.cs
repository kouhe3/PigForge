namespace PigForge.Protocol;

public static class ReplayFormat
{
    public const string Name = "pigforge.physics.replay";
    public const ushort CurrentVersion = 2;

    public const uint MaxSimulationTicks = 100_000;
}

public static class ReplayHashAlgorithms
{
    public const string Sha256CanonicalV2 = "sha256-canonical-v2";
}

public enum ReplayJointKind
{
    Fixed,
    Distance,
    Revolute,
    Configurable
}

public enum ReplayEventKind
{
    ContactStarted,
    ContactPersisted,
    ContactEnded,
    JointBroken,
    EntityCreated,
    EntityDestroyed
}

public enum ReplayOutcome
{
    Success,
    Failure,
    Aborted
}

public sealed record ReplayHeader(
    ushort ProtocolVersion,
    string ContentVersion,
    string PhysicsBehaviorVersion,
    string StateHashAlgorithm,
    ushort FixedTickRate,
    uint SimulationTicks,
    uint RandomSeed);

public readonly record struct ReplayVector3(float X, float Y, float Z)
{
    public static ReplayVector3 Zero => new(0, 0, 0);
}

public readonly record struct ReplayQuaternion(float X, float Y, float Z, float W)
{
    public static ReplayQuaternion Identity => new(0, 0, 0, 1);
}

public sealed record ReplayEntityState(
    uint EntityId,
    uint PhysicsBodyId,
    uint PartTypeId,
    ReplayVector3 Position,
    ReplayQuaternion Rotation,
    ReplayVector3 LinearVelocity,
    ReplayVector3 AngularVelocity,
    float Scale = 1f);

public sealed record ReplayJointState(
    uint JointId,
    uint BodyA,
    uint BodyB,
    ReplayJointKind Kind,
    uint Constraints,
    float BreakForce,
    float BreakTorque);

public sealed record ReplayInitialState(
    IReadOnlyList<ReplayEntityState> Entities,
    IReadOnlyList<ReplayJointState> Joints);

public abstract record ReplayCommand(
    uint Tick,
    uint Sequence,
    uint PlayerId,
    ClientCommandKind Kind);

/// <summary>Free planar placement (v2): position in metres, <paramref name="Angle"/> in
/// radians, uniform <paramref name="Scale"/> in (0, 4].</summary>
public sealed record PlacePartCommand(
    uint Tick,
    uint Sequence,
    uint PlayerId,
    uint PartTypeId,
    float PositionX,
    float PositionY,
    float Angle,
    float Scale) : ReplayCommand(Tick, Sequence, PlayerId, ClientCommandKind.PlacePart);

public sealed record RemovePartCommand(
    uint Tick,
    uint Sequence,
    uint PlayerId,
    uint EntityId) : ReplayCommand(Tick, Sequence, PlayerId, ClientCommandKind.RemovePart);

public sealed record RotatePartCommand(
    uint Tick,
    uint Sequence,
    uint PlayerId,
    uint EntityId,
    float Angle) : ReplayCommand(Tick, Sequence, PlayerId, ClientCommandKind.RotatePart);

public sealed record StartSimulationCommand(
    uint Tick,
    uint Sequence,
    uint PlayerId) : ReplayCommand(Tick, Sequence, PlayerId, ClientCommandKind.StartSimulation);

/// <summary>What happens to the previous run's entities when a running room re-enters build mode.</summary>
public enum BuildModePolicy : byte
{
    Clear = 0,
    Keep = 1
}

public sealed record EnterBuildModeCommand(
    uint Tick,
    uint Sequence,
    uint PlayerId,
    BuildModePolicy Policy) : ReplayCommand(Tick, Sequence, PlayerId, ClientCommandKind.EnterBuildMode);

public sealed record ReplayEvent(
    ReplayEventKind Kind,
    uint? BodyA = null,
    uint? BodyB = null,
    uint? JointId = null,
    uint? EntityId = null);

public sealed record ReplayFrame(
    uint Tick,
    IReadOnlyList<ReplayEntityState> Snapshots,
    IReadOnlyList<ReplayEvent> Events);

public sealed record ReplayResult(
    ReplayOutcome Outcome,
    uint CompletedTick,
    string StateHash);

public sealed record ReplayDocument(
    string Format,
    ReplayHeader Header,
    ReplayInitialState InitialState,
    IReadOnlyList<ReplayCommand> Commands,
    IReadOnlyList<ReplayFrame> Frames,
    ReplayResult FinalResult);

public sealed record ReplayInput(
    string Format,
    ReplayHeader Header,
    ReplayInitialState InitialState,
    IReadOnlyList<ReplayCommand> Commands);

public sealed record ReplayOutput(
    IReadOnlyList<ReplayFrame> Frames,
    ReplayResult FinalResult);

public sealed record ReplayValidationResult(IReadOnlyList<string> Errors)
{
    public bool IsValid => Errors.Count == 0;
}
