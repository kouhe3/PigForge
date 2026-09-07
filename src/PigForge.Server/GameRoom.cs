using System.Runtime.InteropServices;
using PigForge.Core;
using PigForge.Core.Construction;
using PigForge.Core.Content;
using PigForge.Physics.Abstractions;
using PigForge.Protocol;

namespace PigForge.Server;

/// <summary>Per-room gameplay roles layered on top of part content during level setup.</summary>
public sealed record RoomSpawnSpec(
    uint PartTypeId,
    PhysicsVector3 Position,
    RoomActorRole Role = RoomActorRole.Part,
    ushort TntFuseTicks = 1,
    float MotorImpulsePerTick = 0f,
    float MotorDirectionX = 0f,
    bool IsWheel = false);

public enum RoomActorRole
{
    Part,
    Pig,
    Tnt
}

/// <summary>Room setup parameters. The factory owns construction of the single authoritative physics scene.</summary>
public sealed record GameRoomOptions(
    ushort TickRateHz,
    PartContentLibrary Content,
    Func<IPhysicsWorld> WorldFactory,
    GameplayConfig GameplayConfig)
{
    public static GameRoomOptions Create(PartContentLibrary content, Func<IPhysicsWorld> worldFactory, GameplayConfig gameplayConfig, ushort tickRateHz = 60) =>
        new(tickRateHz, content, worldFactory, gameplayConfig);
}

/// <summary>
/// One room owns exactly one authoritative physics world and progresses through two
/// modes: Building (construction commands mutate pure rules state, no bodies exist)
/// and Running (bodies are created at Start, then every tick advances through ordered
/// phases: input → physics step → telemetry → rules → destruction). A running room can
/// re-enter Building via <see cref="EnterBuildMode"/> (issue #7), destroying all bodies;
/// the keep policy freezes the previous contraption at its last poses while the clear
/// policy resets to the configured level. Ticks are driven manually so execution stays
/// deterministic; wall-clock scheduling is a transport concern.
/// </summary>
public sealed class GameRoom : IDisposable
{
    private readonly IPhysicsWorld _world;
    private readonly PartContentLibrary _content;
    private readonly FixedTimeStep _timeStep;
    private readonly EntityStore _entities = new();
    private readonly PartStore _parts;
    private readonly TransformStore _transforms;
    private readonly PhysicsBodyStore _bodies;
    private readonly MotorStore _motors;
    private readonly TntStore _tnt;
    private readonly WheelStore _wheels;
    private readonly PigStore _pigs;
    private readonly ConstructionRules _construction;
    private readonly GameplayRules _rules;
    private readonly CommandValidator _validator = new();
    private readonly GameplayTickOutput _output = new();
    private readonly Dictionary<uint, PhysicsBodyId> _bodyByEntity = new();
    private readonly Dictionary<uint, EntityId> _entityByBody = new();
    private readonly Dictionary<uint, List<uint>> _entitiesByBody = new();
    private readonly Dictionary<uint, (PhysicsVector3 Offset, PhysicsQuaternion Rotation)> _compoundLocalByEntity = new();
    private readonly List<LiveCompound> _liveCompounds = new();
    private readonly List<PhysicsCommand> _appliedCommands = new();
    private readonly float _seamBreakImpulse;
    private readonly List<CommandOutcome> _outcomeLog = new();
    private PhysicsBodySnapshot[] _snapshotBuffer = Array.Empty<PhysicsBodySnapshot>();
    private PhysicsEvent[] _eventBuffer = Array.Empty<PhysicsEvent>();
    private readonly List<uint> _entityOrder = new();
    private LevelContentDocument? _level;
    private bool _disposed;

    public GameRoom(GameRoomOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _content = options.Content ?? throw new ArgumentException("Room options must carry part content.", nameof(options));
        _world = options.WorldFactory?.Invoke() ?? throw new ArgumentException("Room options must provide a world factory.", nameof(options));
        _timeStep = FixedTimeStep.FromSeconds(1f / (options.TickRateHz == 0 ? (ushort)60 : options.TickRateHz));
        _seamBreakImpulse = options.GameplayConfig.SeamBreakImpulse;
        _parts = new PartStore(_entities);
        _transforms = new TransformStore(_entities);
        _bodies = new PhysicsBodyStore(_entities);
        _motors = new MotorStore(_entities);
        _tnt = new TntStore(_entities);
        _wheels = new WheelStore(_entities);
        _pigs = new PigStore(_entities);
        _construction = new ConstructionRules(_entities, _parts, _transforms, _content);
        _rules = new GameplayRules(
            _entities, _motors, _tnt, _wheels, _pigs, _bodies, options.GameplayConfig);
    }

    public RoomMode Mode { get; private set; } = RoomMode.Building;

    public uint CurrentTick { get; private set; }

    public GameplayPhase Phase => _rules.Phase;

    public int AlivePigs => _rules.AlivePigs;

    public bool RestartRequested => _rules.RestartRequested;

    public int BodyCount => _bodyByEntity.Count;

    public IReadOnlyList<CommandOutcome> OutcomeLog => _outcomeLog;

    /// <summary>Spawns a level actor during the building phase, bypassing grid occupancy (level authoring).</summary>
    public EntityId Spawn(in RoomSpawnSpec spec)
    {
        ThrowIfDisposed();
        EnsureBuilding("Spawn");

        EntityId entity = _entities.Create();
        _parts.Set(entity, new PartLink(spec.PartTypeId));
        _transforms.Set(entity, new EntityTransform(spec.Position, PhysicsQuaternion.Identity));

        switch (spec.Role)
        {
            case RoomActorRole.Pig:
                _rules.AddPig(entity);
                break;
            case RoomActorRole.Tnt:
                _rules.AddTnt(entity, spec.TntFuseTicks);
                break;
        }

        if (spec.MotorImpulsePerTick != 0f)
        {
            _rules.AddMotor(entity, spec.MotorImpulsePerTick, spec.MotorDirectionX);
        }

        if (spec.IsWheel)
        {
            _rules.AddWheel(entity);
        }

        return entity;
    }

    /// <summary>Spawns every actor of an engine-agnostic level document (building phase).
    /// The document is remembered so a clear-policy build-mode re-entry can restore it.</summary>
    public void SetupFromLevel(LevelContentDocument level)
    {
        ArgumentNullException.ThrowIfNull(level);
        _level = level;
        foreach (LevelSpawnDefinition spawn in level.Spawns)
        {
            Spawn(new RoomSpawnSpec(
                spawn.PartTypeId,
                spawn.Position,
                spawn.Role switch
                {
                    LevelActorRole.Pig => RoomActorRole.Pig,
                    LevelActorRole.Tnt => RoomActorRole.Tnt,
                    _ => RoomActorRole.Part
                },
                spawn.TntFuseTicks,
                spawn.MotorImpulsePerTick,
                spawn.MotorDirectionX,
                spawn.IsWheel));
        }
    }

    /// <summary>
    /// Submits a client command. Validation makes duplicates idempotent and rejects
    /// stale, out-of-order, wrong-mode, and rule-invalid submissions deterministically;
    /// every outcome is recorded in <see cref="OutcomeLog"/>.
    /// </summary>
    public CommandOutcome Submit(ReplayCommand command)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(command);

        CommandStatus status = _validator.Validate(command, Mode, CurrentTick);
        ConstructionError ruleError = ConstructionError.None;
        if (status == CommandStatus.Accepted)
        {
            (status, ruleError) = ExecuteCommand(command);
        }

        CommandOutcome outcome = new(command, status, ruleError);
        _outcomeLog.Add(outcome);
        return outcome;
    }

    private (CommandStatus, ConstructionError) ExecuteCommand(ReplayCommand command)
    {
        switch (command)
        {
            case PlacePartCommand place:
            {
                ConstructionResult placed = _construction.Place(
                    place.PartTypeId, place.PositionX, place.PositionY, place.Angle, place.Scale);
                return placed.IsSuccess
                    ? (CommandStatus.Accepted, ConstructionError.None)
                    : (CommandStatus.RuleRejected, placed.Error);
            }

            case RotatePartCommand rotate:
            {
                ConstructionResult rotated = _construction.Rotate(new EntityId(rotate.EntityId), rotate.Angle);
                return rotated.IsSuccess
                    ? (CommandStatus.Accepted, ConstructionError.None)
                    : (CommandStatus.RuleRejected, rotated.Error);
            }

            case RemovePartCommand remove:
            {
                EntityId entity = new(remove.EntityId);
                ConstructionResult removed = _construction.Remove(entity);
                if (removed.IsSuccess)
                {
                    _rules.CleanupEntityStores(entity);
                    UnbindEntity(entity, destroyBodyIfOrphan: true);
                }

                return removed.IsSuccess
                    ? (CommandStatus.Accepted, ConstructionError.None)
                    : (CommandStatus.RuleRejected, removed.Error);
            }

            case StartSimulationCommand:
                Start();
                return (CommandStatus.Accepted, ConstructionError.None);

            case EnterBuildModeCommand enterBuild:
                EnterBuildMode(enterBuild.Policy);
                return (CommandStatus.Accepted, ConstructionError.None);

            default:
                return (CommandStatus.UnknownKind, ConstructionError.None);
        }
    }

    /// <summary>
    /// Transitions Building → Running and materialises authoritative bodies from
    /// construction connections: connected dynamic boxes become one compound, other
    /// parts stay one body each. Slot order of clusters is deterministic.
    /// </summary>
    public void Start()
    {
        ThrowIfDisposed();
        if (Mode != RoomMode.Building)
        {
            throw new InvalidOperationException("Only a building room can start simulation.");
        }

        List<EntityId> entities = new();
        var parts = _parts.GetEnumerator();
        while (parts.MoveNext())
        {
            entities.Add(parts.CurrentId);
        }

        entities.Sort((left, right) => left.Value.CompareTo(right.Value));
        List<CompoundCluster> clusters = CompoundAssembler.Assemble(entities, _construction, _content, _seamBreakImpulse);
        foreach (CompoundCluster cluster in clusters)
        {
            BindCluster(cluster, cluster.CreateBodyDefinition(_content));
        }

        EnsureBuffers();
        Mode = RoomMode.Running;
    }

    /// <summary>
    /// Running → Building transition (issue #7). Both policies destroy every
    /// authoritative body, returning the room to a body-free building phase. Keep
    /// freezes each living entity at its last telemetry pose: the previous contraption
    /// stays in the field (non-editable, counted toward part limits and the layout
    /// hash) and relaunches on the next Start; charges are re-armed with remaining
    /// fuse. Clear destroys everything and re-spawns the configured level actors.
    /// </summary>
    private void EnterBuildMode(BuildModePolicy policy)
    {
        Mode = RoomMode.Building;
        _output.Clear();

        uint[] entityValues = _bodyByEntity.Keys.ToArray();
        Array.Sort(entityValues);
        Dictionary<uint, (PhysicsVector3 Position, PhysicsQuaternion Rotation)> posesByEntity = CapturePoses();
        HashSet<uint> destroyedBodies = new();
        foreach (uint entityValue in entityValues)
        {
            PhysicsBodyId body = _bodyByEntity[entityValue];
            if (destroyedBodies.Add(body.Value))
            {
                _world.DestroyBody(body);
            }
        }

        _bodyByEntity.Clear();
        _entityByBody.Clear();
        _entitiesByBody.Clear();
        _compoundLocalByEntity.Clear();
        _liveCompounds.Clear();

        if (policy == BuildModePolicy.Keep)
        {
            _construction.FreezeAll(posesByEntity);
            foreach (uint entityValue in entityValues)
            {
                // Level actors (pigs, TNT spawns) are not construction entities; they
                // freeze at their poses directly and keep their gameplay roles.
                if (!_construction.IsFrozen(entityValue)
                    && posesByEntity.TryGetValue(entityValue, out (PhysicsVector3 Position, PhysicsQuaternion Rotation) pose))
                {
                    _transforms.Set(new EntityId(entityValue), new EntityTransform(pose.Position, pose.Rotation));
                }
            }

            _rules.ResetForRebuild();
        }
        else
        {
            _construction.ResetAll();
            _rules.ResetAll();
            List<EntityId> remainingActors = new();
            var parts = _parts.GetEnumerator();
            while (parts.MoveNext())
            {
                remainingActors.Add(parts.CurrentId);
            }

            foreach (EntityId actor in remainingActors)
            {
                _entities.Destroy(actor);
            }

            if (_level is not null)
            {
                SetupFromLevel(_level);
            }
        }
    }

    private Dictionary<uint, (PhysicsVector3 Position, PhysicsQuaternion Rotation)> CapturePoses()
    {
        int count = _world.CopySnapshots(_snapshotBuffer);
        Dictionary<uint, (PhysicsVector3, PhysicsQuaternion)> poses = new(_bodyByEntity.Count);
        foreach (KeyValuePair<uint, PhysicsBodyId> pair in _bodyByEntity)
        {
            if (!TryFindSnapshot(pair.Value, count, out PhysicsBodySnapshot snapshot))
            {
                continue;
            }

            poses[pair.Key] = WorldPose(pair.Key, snapshot);
        }

        return poses;
    }

    public void Tick()
    {
        ThrowIfDisposed();
        if (Mode != RoomMode.Running)
        {
            throw new InvalidOperationException("The room must start simulation before ticking.");
        }

        CurrentTick++;

        _appliedCommands.Clear();
        _appliedCommands.AddRange(_output.Commands);

        // Phase 1 (input): commands the rules produced last tick drive the physics world.
        _world.ApplyCommands(CollectionsMarshal.AsSpan(_output.Commands));

        // Phase 2 (physics): one authoritative fixed step.
        _world.Step(_timeStep);

        // Phase 3 (telemetry): collect the tick's snapshots and events.
        int snapshotCount = _world.CopySnapshots(_snapshotBuffer);
        int eventCount = _world.DrainEvents(_eventBuffer);

        // Phase 4 (rules): gameplay consumes the telemetry and produces commands/destructions.
        _rules.Tick(CurrentTick, _eventBuffer.AsSpan(0, eventCount), _snapshotBuffer.AsSpan(0, snapshotCount), _output);

        // Phase 5 (cleanup): bodies of destroyed entities leave the authoritative scene.
        foreach (EntityId destroyed in _output.DestroyedEntities)
        {
            UnbindEntity(destroyed, destroyBodyIfOrphan: true);
        }

        // Phase 5.5: ADR-002 seam split from the impulses that just landed.
        SplitFromAppliedCommands(snapshotCount);
    }

    public void RunTicks(uint count)
    {
        for (uint index = 0; index < count; index++)
        {
            Tick();
        }
    }

    /// <summary>
    /// Publishes the authoritative snapshot frame in the binary wire format. The hot
    /// path uses span writes only: no JSON and no allocations.
    /// </summary>
    public bool TryPublishSnapshot(Span<byte> destination, out int bytesWritten)
    {
        ThrowIfDisposed();
        if (!SnapshotFrame.TryEncodeHeader(
                destination,
                new SnapshotFrameHeader(ProtocolVersion.Current, CurrentTick, (byte)Phase, (uint)_bodyByEntity.Count),
                out SnapshotFrameWriter writer))
        {
            bytesWritten = 0;
            return false;
        }

        int count = _world.CopySnapshots(_snapshotBuffer);
        FillEntityOrder();
        for (int index = 0; index < _entityOrder.Count; index++)
        {
            uint entityValue = _entityOrder[index];
            PhysicsBodyId body = _bodyByEntity[entityValue];
            if (!TryFindSnapshot(body, count, out PhysicsBodySnapshot snapshot)
                || !_parts.TryGet(new EntityId(entityValue), out PartLink part))
            {
                bytesWritten = 0;
                return false;
            }

            (PhysicsVector3 position, PhysicsQuaternion rotation) = WorldPose(entityValue, snapshot);
            if (!writer.WriteEntity(new SnapshotEntity(
                    entityValue,
                    snapshot.Body.Value,
                    part.PartTypeId,
                    ToReplay(position),
                    ToReplay(rotation),
                    ToReplay(snapshot.LinearVelocity),
                    ToReplay(snapshot.AngularVelocity),
                    _transforms.TryGet(new EntityId(entityValue), out EntityTransform transform) ? transform.Scale : 1f)))
            {
                bytesWritten = 0;
                return false;
            }
        }

        bytesWritten = writer.WrittenBytes;
        return true;
    }

    private static ReplayVector3 ToReplay(PhysicsVector3 value) => new(value.X, value.Y, value.Z);

    private static ReplayQuaternion ToReplay(PhysicsQuaternion value) => new(value.X, value.Y, value.Z, value.W);

    /// <summary>Deterministic hash: layout hash in Building, authoritative snapshot hash in Running.</summary>
    public long ComputeStateHash()
    {
        ThrowIfDisposed();
        long hash = 17;
        hash = unchecked((hash * 31) + CurrentTick.GetHashCode());
        hash = unchecked((hash * 31) + (int)Phase);
        hash = unchecked((hash * 31) + _rules.AlivePigs);

        if (Mode == RoomMode.Building)
        {
            return unchecked((hash * 31) + _construction.ComputeLayoutHash());
        }

        int count = _world.CopySnapshots(_snapshotBuffer);
        PhysicsBodySnapshot[] ordered = _snapshotBuffer.Take(count).OrderBy(snapshot => snapshot.Body.Value).ToArray();
        foreach (PhysicsBodySnapshot snapshot in ordered)
        {
            hash = unchecked((hash * 31) + snapshot.Body.Value.GetHashCode());
            hash = unchecked((hash * 31) + snapshot.Position.GetHashCode());
            hash = unchecked((hash * 31) + snapshot.Rotation.GetHashCode());
            hash = unchecked((hash * 31) + snapshot.LinearVelocity.GetHashCode());
            hash = unchecked((hash * 31) + snapshot.AngularVelocity.GetHashCode());
        }

        return hash;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Mode = RoomMode.Closed;
        _world.Dispose();
        _bodyByEntity.Clear();
        _entityByBody.Clear();
        _entitiesByBody.Clear();
        _compoundLocalByEntity.Clear();
        _liveCompounds.Clear();
        _appliedCommands.Clear();
        _outcomeLog.Clear();
        _output.Clear();
        _snapshotBuffer = Array.Empty<PhysicsBodySnapshot>();
        _eventBuffer = Array.Empty<PhysicsEvent>();
    }

    private void BindCluster(CompoundCluster cluster, BodyDefinition definition)
    {
        PhysicsBodyId body = _world.CreateBody(definition);
        foreach (CompoundMember member in cluster.Members)
        {
            _bodies.Set(member.Entity, new PhysicsBodyLink(body));
            _rules.LinkBody(member.Entity, body, isDynamic: definition.Mode == PhysicsBodyMode.Dynamic);
            _bodyByEntity.Add(member.Entity.Value, body);
            if (!_entitiesByBody.TryGetValue(body.Value, out List<uint>? members))
            {
                members = new List<uint>();
                _entitiesByBody.Add(body.Value, members);
                _entityByBody.Add(body.Value, member.Entity);
            }

            members.Add(member.Entity.Value);
            if (cluster.IsMerged)
            {
                _compoundLocalByEntity[member.Entity.Value] = (member.LocalOffset, member.LocalRotation);
            }
        }

        if (cluster.IsMerged)
        {
            _liveCompounds.Add(new LiveCompound(body, cluster));
        }
    }

    private void UnbindEntity(EntityId entity, bool destroyBodyIfOrphan)
    {
        if (!_bodyByEntity.Remove(entity.Value, out PhysicsBodyId body))
        {
            return;
        }

        _compoundLocalByEntity.Remove(entity.Value);
        if (_entitiesByBody.TryGetValue(body.Value, out List<uint>? members))
        {
            members.Remove(entity.Value);
            if (members.Count == 0)
            {
                _entitiesByBody.Remove(body.Value);
                _entityByBody.Remove(body.Value);
                _liveCompounds.RemoveAll(live => live.Body == body);
                if (destroyBodyIfOrphan)
                {
                    _world.DestroyBody(body);
                }
            }
            else if (_entityByBody.TryGetValue(body.Value, out EntityId primary) && primary == entity)
            {
                _entityByBody[body.Value] = new EntityId(members[0]);
            }
        }
        else if (destroyBodyIfOrphan)
        {
            _entityByBody.Remove(body.Value);
            _world.DestroyBody(body);
        }
    }

    private void SplitFromAppliedCommands(int snapshotCount)
    {
        for (int index = 0; index < _appliedCommands.Count; index++)
        {
            PhysicsCommand command = _appliedCommands[index];
            float magnitude = PhysicsVector3.Distance(command.Impulse, PhysicsVector3.Zero);
            if (magnitude <= _seamBreakImpulse)
            {
                continue;
            }

            int liveIndex = _liveCompounds.FindIndex(candidate => candidate.Body == command.Body);
            if (liveIndex < 0 || !TryFindSnapshot(command.Body, snapshotCount, out PhysicsBodySnapshot snapshot))
            {
                continue;
            }

            LiveCompound live = _liveCompounds[liveIndex];
            live.Cluster.WorldPosition = snapshot.Position;
            live.Cluster.WorldRotation = snapshot.Rotation;
            CompoundSeam? seam = CompoundAssembler.NearestSeam(live.Cluster, command.WorldPoint);
            if (seam is null || magnitude <= seam.Value.BreakImpulse)
            {
                continue;
            }

            IReadOnlyList<CompoundCluster> pieces = CompoundAssembler.SplitAlongSeam(live.Cluster, seam.Value);
            if (pieces.Count == 1)
            {
                live.Cluster = pieces[0];
                continue;
            }

            List<uint> members = _entitiesByBody.TryGetValue(live.Body.Value, out List<uint>? bound)
                ? new List<uint>(bound)
                : new List<uint>();
            foreach (uint entityValue in members)
            {
                UnbindEntity(new EntityId(entityValue), destroyBodyIfOrphan: false);
            }

            _world.DestroyBody(live.Body);
            foreach (CompoundCluster piece in pieces)
            {
                BindCluster(piece, piece.CreateBodyDefinition(_content, snapshot.LinearVelocity, snapshot.AngularVelocity));
            }

            EnsureBuffers();
        }
    }

    private (PhysicsVector3 Position, PhysicsQuaternion Rotation) WorldPose(uint entityValue, PhysicsBodySnapshot snapshot)
    {
        if (!_compoundLocalByEntity.TryGetValue(entityValue, out (PhysicsVector3 Offset, PhysicsQuaternion Rotation) local))
        {
            return (snapshot.Position, snapshot.Rotation);
        }

        return (snapshot.Position + snapshot.Rotation.Rotate(local.Offset), snapshot.Rotation * local.Rotation);
    }

    private bool TryFindSnapshot(PhysicsBodyId body, int snapshotCount, out PhysicsBodySnapshot snapshot)
    {
        for (int index = 0; index < snapshotCount; index++)
        {
            if (_snapshotBuffer[index].Body == body)
            {
                snapshot = _snapshotBuffer[index];
                return true;
            }
        }

        snapshot = default;
        return false;
    }

    private void FillEntityOrder()
    {
        if (_entityOrder.Capacity < _bodyByEntity.Count)
        {
            _entityOrder.Capacity = _bodyByEntity.Count;
        }

        _entityOrder.Clear();
        foreach (KeyValuePair<uint, PhysicsBodyId> pair in _bodyByEntity)
        {
            _entityOrder.Add(pair.Key);
        }

        _entityOrder.Sort();
    }

    private void EnsureBuffers()
    {
        int bodyCount = Math.Max(1, _entitiesByBody.Count);
        if (_snapshotBuffer.Length < bodyCount)
        {
            _snapshotBuffer = new PhysicsBodySnapshot[bodyCount];
        }

        int eventCapacity = (bodyCount * (bodyCount - 1) / 2) + bodyCount + 16;
        if (_eventBuffer.Length < eventCapacity)
        {
            _eventBuffer = new PhysicsEvent[eventCapacity];
        }

        if (_entityOrder.Capacity < _bodyByEntity.Count)
        {
            _entityOrder.Capacity = _bodyByEntity.Count;
        }
    }

    private void EnsureBuilding(string operation)
    {
        if (Mode != RoomMode.Building)
        {
            throw new InvalidOperationException($"{operation} is only allowed while the room is building.");
        }
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    private sealed class LiveCompound(PhysicsBodyId body, CompoundCluster cluster)
    {
        public PhysicsBodyId Body { get; } = body;

        public CompoundCluster Cluster { get; set; } = cluster;
    }
}
