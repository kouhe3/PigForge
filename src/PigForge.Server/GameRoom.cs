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
    bool IsWheel = false,
    float Angle = 0f);

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
    GameplayConfig GameplayConfig,
    bool SandboxMode = false)
{
    public static GameRoomOptions Create(PartContentLibrary content, Func<IPhysicsWorld> worldFactory, GameplayConfig gameplayConfig, ushort tickRateHz = 60, bool sandboxMode = false) =>
        new(tickRateHz, content, worldFactory, gameplayConfig, sandboxMode);
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
    private readonly BalloonStore _balloons;
    private readonly FanStore _fans;
    private readonly SpringStore _springs;
    private readonly RocketStore _rockets;
    private readonly TntStore _tnt;
    private readonly WheelStore _wheels;
    private readonly PigStore _pigs;
    private readonly EggStore _eggs;
    private readonly WingStore _wings;
    private readonly TailStore _tails;
    private readonly UmbrellaStore _umbrellas;
    private readonly GearboxStore _gearboxes;
    private readonly BellowsStore _bellows;
    private readonly DetacherStore _detachers;
    private readonly GrappleStore _grapples;
    private readonly ActivationStore _activations;
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
    private readonly GameplayConfig _gameplayConfig;
    private readonly bool _sandboxMode;
    private readonly SandboxPlayers _sandboxPlayers = new();
    private readonly List<CommandOutcome> _outcomeLog = new();
    private PhysicsBodySnapshot[] _snapshotBuffer = Array.Empty<PhysicsBodySnapshot>();
    private PhysicsEvent[] _eventBuffer = Array.Empty<PhysicsEvent>();
    private readonly List<uint> _entityOrder = new();
    private LevelContentDocument? _level;
    private readonly List<(uint EntityValue, uint PartTypeId, float PositionX, float PositionY, float Angle, float Scale)> _retryLayout = new();
    private bool _disposed;

    public GameRoom(GameRoomOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _content = options.Content ?? throw new ArgumentException("Room options must carry part content.", nameof(options));
        _world = options.WorldFactory?.Invoke() ?? throw new ArgumentException("Room options must provide a world factory.", nameof(options));
        _timeStep = FixedTimeStep.FromSeconds(1f / (options.TickRateHz == 0 ? (ushort)60 : options.TickRateHz));
        _gameplayConfig = options.GameplayConfig;
        _sandboxMode = options.SandboxMode;
        _seamBreakImpulse = options.GameplayConfig.SeamBreakImpulse;
        _parts = new PartStore(_entities);
        _transforms = new TransformStore(_entities);
        _bodies = new PhysicsBodyStore(_entities);
        _motors = new MotorStore(_entities);
        _balloons = new BalloonStore(_entities);
        _fans = new FanStore(_entities);
        _springs = new SpringStore(_entities);
        _rockets = new RocketStore(_entities);
        _tnt = new TntStore(_entities);
        _wheels = new WheelStore(_entities);
        _pigs = new PigStore(_entities);
        _eggs = new EggStore(_entities);
        _wings = new WingStore(_entities);
        _tails = new TailStore(_entities);
        _umbrellas = new UmbrellaStore(_entities);
        _gearboxes = new GearboxStore(_entities);
        _bellows = new BellowsStore(_entities);
        _detachers = new DetacherStore(_entities);
        _grapples = new GrappleStore(_entities);
        _activations = new ActivationStore(_entities);
        _construction = new ConstructionRules(_entities, _parts, _transforms, _content);
        _rules = new GameplayRules(
            _entities, _motors, _balloons, _fans, _springs, _rockets, _tnt, _wheels, _pigs, _eggs, _wings, _tails, _umbrellas, _gearboxes, _bellows, _detachers, _grapples, _activations, _bodies, options.GameplayConfig);
    }

    public RoomMode Mode { get; private set; } = RoomMode.Building;

    public uint CurrentTick { get; private set; }

    public GameplayPhase Phase => _rules.Phase;

    public int AlivePigs => _rules.AlivePigs;

    public bool RestartRequested => _rules.RestartRequested;

    public int BodyCount => _bodyByEntity.Count;

    /// <summary>Upper bound on the entity count of the next published frame. Sandbox
    /// frames also carry previews that have no physics body, so transports must size
    /// their encode buffer from this value rather than from <see cref="BodyCount"/>.</summary>
    public int MaxSnapshotEntityCount => Math.Max(_parts.Count, _bodyByEntity.Count);

    public IReadOnlyList<CommandOutcome> OutcomeLog => _outcomeLog;

    /// <summary>Spawns a level actor during the building phase, bypassing grid occupancy (level authoring).</summary>
    public EntityId Spawn(in RoomSpawnSpec spec)
    {
        ThrowIfDisposed();
        EnsureBuilding("Spawn");

        EntityId entity = _entities.Create();
        _parts.Set(entity, new PartLink(spec.PartTypeId));
        _transforms.Set(entity, new EntityTransform(spec.Position, PhysicsQuaternion.FromZAngle(spec.Angle)));

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

        if (_sandboxMode && IsSwitchable(spec.PartTypeId))
        {
            _rules.AddActivation(entity);
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
                spawn.IsWheel,
                spawn.Angle));
        }

        if (_sandboxMode)
        {
            // The sandbox world runs from tick zero: level actors become bodies
            // immediately and the room never enters the Building phase.
            MaterializeLevelActors();
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

        if (_sandboxMode)
        {
            return SubmitSandbox(command);
        }

        CommandStatus status = _validator.Validate(command, Mode, CurrentTick);
        ConstructionError ruleError = ConstructionError.None;
        uint entityId = 0;
        if (status == CommandStatus.Accepted)
        {
            (status, ruleError, entityId) = ExecuteCommand(command);
        }

        CommandOutcome outcome = new(command, status, ruleError, entityId);
        _outcomeLog.Add(outcome);
        return outcome;
    }

    private (CommandStatus Status, ConstructionError Error, uint EntityId) ExecuteCommand(ReplayCommand command)
    {
        switch (command)
        {
            case PlacePartCommand place:
            {
                ConstructionResult placed = _construction.Place(
                    place.PartTypeId, place.PositionX, place.PositionY, place.Angle, place.Scale, 0);
                if (!placed.IsSuccess)
                {
                    return (CommandStatus.RuleRejected, placed.Error, 0);
                }

                RegisterPlacedRole(placed.Entity, place.PartTypeId);
                return (CommandStatus.Accepted, ConstructionError.None, placed.Entity.Value);
            }

            case RotatePartCommand rotate:
            {
                ConstructionResult rotated = _construction.Rotate(new EntityId(rotate.EntityId), rotate.Angle, 0);
                return rotated.IsSuccess
                    ? (CommandStatus.Accepted, ConstructionError.None, rotate.EntityId)
                    : (CommandStatus.RuleRejected, rotated.Error, 0);
            }

            case MovePartCommand move:
            {
                ConstructionResult moved = _construction.Move(new EntityId(move.EntityId), move.PositionX, move.PositionY, 0);
                return moved.IsSuccess
                    ? (CommandStatus.Accepted, ConstructionError.None, move.EntityId)
                    : (CommandStatus.RuleRejected, moved.Error, 0);
            }

            case ScalePartCommand scale:
            {
                ConstructionResult scaled = _construction.Scale(new EntityId(scale.EntityId), scale.Scale, 0);
                return scaled.IsSuccess
                    ? (CommandStatus.Accepted, ConstructionError.None, scale.EntityId)
                    : (CommandStatus.RuleRejected, scaled.Error, 0);
            }

            case RemovePartCommand remove:
            {
                EntityId entity = new(remove.EntityId);
                ConstructionResult removed = _construction.Remove(entity, 0);
                if (removed.IsSuccess)
                {
                    _rules.CleanupEntityStores(entity);
                    UnbindEntity(entity, destroyBodyIfOrphan: true);
                }

                return removed.IsSuccess
                    ? (CommandStatus.Accepted, ConstructionError.None, remove.EntityId)
                    : (CommandStatus.RuleRejected, removed.Error, 0);
            }

            case SetPartActiveCommand activate:
                return SetPartActive(activate.EntityId, activate.Active, owner: 0);

            case SetPartTypeActiveCommand activateType:
                return SetPartTypeActive(activateType.PartTypeId, activateType.Active, owner: 0);

            case StartSimulationCommand:
                try
                {
                    Start();
                }
                catch (NotSupportedException)
                {
                    return (CommandStatus.RuleRejected, ConstructionError.UnsupportedShape, 0);
                }

                return (CommandStatus.Accepted, ConstructionError.None, 0);
            case EnterBuildModeCommand enterBuild:
                EnterBuildMode(enterBuild.Policy);
                return (CommandStatus.Accepted, ConstructionError.None, 0);

            case RetryCommand:
                Retry();
                return (CommandStatus.Accepted, ConstructionError.None, 0);

            default:
                return (CommandStatus.UnknownKind, ConstructionError.None, 0);
        }
    }

    private void RegisterPlacedRole(EntityId entity, uint partTypeId)
    {
        PartCapabilities? capabilities = _content.GetPart(partTypeId).Capabilities;
        if (capabilities is null)
        {
            return;
        }

        if (capabilities.IsPig)
        {
            _rules.AddPig(entity);
        }

        if (capabilities.HasMotor)
        {
            _rules.AddMotor(entity, capabilities.MotorThrustPerTick!.Value, capabilities.MotorDirectionX ?? 1f);
        }

        if (capabilities.IsWheel)
        {
            _rules.AddWheel(entity);
        }

        if (capabilities.TntFuseTicks is ushort fuse)
        {
            _rules.AddTnt(entity, fuse);
        }

        if (capabilities.HasBalloon)
        {
            _rules.AddBalloon(entity, capabilities.BalloonLiftPerTick!.Value);
        }

        if (capabilities.HasFan)
        {
            _rules.AddFan(entity, capabilities.FanThrustPerTick!.Value, capabilities.FanDirectionX ?? 1f, capabilities.FanDirectionY ?? 0f);
        }

        if (capabilities.HasSpring)
        {
            _rules.AddSpring(entity, capabilities.SpringBounceImpulsePerTick!.Value);
        }

        if (capabilities.HasRocket)
        {
            _rules.AddRocket(entity, capabilities.RocketThrustPerTick!.Value, capabilities.RocketDirectionX ?? 1f, capabilities.RocketDirectionY ?? 0f, capabilities.RocketDurationTicks!.Value, capabilities.RocketExplodeRadius ?? 0f, capabilities.RocketExplodeImpulse ?? 0f);
        }

        if (capabilities.IsEgg)
        {
            _rules.AddEgg(entity);
        }

        if (capabilities.HasWing)
        {
            _rules.AddWing(entity, capabilities.WingLiftCoef!.Value, capabilities.WingMaxLift ?? 0f);
        }

        if (capabilities.HasTail)
        {
            _rules.AddTail(entity, capabilities.TailDragCoef!.Value);
        }

        if (capabilities.HasUmbrella)
        {
            _rules.AddUmbrella(entity, capabilities.UmbrellaDragCoef!.Value);
        }

        if (capabilities.IsGearbox)
        {
            _rules.AddGearbox(entity);
        }

        if (capabilities.HasBellows)
        {
            _rules.AddBellows(entity, capabilities.BellowsBoostImpulse!.Value);
        }

        if (capabilities.HasGrapple)
        {
            _rules.AddGrapple(entity, capabilities.GrappleImpulse!.Value, capabilities.GrappleDirectionX ?? 1f, capabilities.GrappleDirectionY ?? 0f);
        }

        if (_sandboxMode && IsSwitchable(partTypeId))
        {
            _rules.AddActivation(entity);
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

        List<EntityId> entities = CollectPartEntities();
        _retryLayout.Clear();
        foreach (uint entityValue in _construction.PlacedEntities)
        {
            EntityId entity = new(entityValue);
            if (!_parts.TryGet(entity, out PartLink partLink) || !_transforms.TryGet(entity, out EntityTransform transform))
            {
                continue;
            }

            _retryLayout.Add((entityValue, partLink.PartTypeId, transform.Position.X, transform.Position.Y, PlanarAngle(transform.Rotation), transform.Scale));
        }

        _retryLayout.Sort((left, right) => left.Item1.CompareTo(right.Item1));

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

    /// <summary>
    /// Returns a running room to the pre-play build state (command Retry). All
    /// authoritative bodies are destroyed; the player's construction layout captured at
    /// the last Start is restored at its build poses with its gameplay roles, and the
    /// configured level actors are re-spawned. Unlike Keep/Clear this never leaves a
    /// second vehicle in the field, so the room is a single-vehicle rebuild loop.
    /// </summary>
    public void Retry()
    {
        ThrowIfDisposed();
        if (Mode != RoomMode.Running)
        {
            throw new InvalidOperationException("Only a running room can retry.");
        }

        _output.Clear();
        uint[] entityValues = _bodyByEntity.Keys.ToArray();
        Array.Sort(entityValues);
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

        Mode = RoomMode.Building;
        _construction.ResetAll();
        _rules.ResetAll();
        var parts = _parts.GetEnumerator();
        while (parts.MoveNext())
        {
            // Runtime rules may have destroyed an entity (TNT blast, detached member)
            // leaving a stale entry in the part/transform stores. Only destroy handles
            // that are still alive; dead ones are removed to keep the stores clean.
            if (_entities.IsAlive(parts.CurrentId))
            {
                _entities.Destroy(parts.CurrentId);
            }
            _parts.Remove(parts.CurrentId);
            _transforms.Remove(parts.CurrentId);
        }

        if (_level is not null)
        {
            SetupFromLevel(_level);
        }

        foreach ((uint _, uint partTypeId, float positionX, float positionY, float angle, float scale) in _retryLayout)
        {
            ConstructionResult placed = _construction.Place(partTypeId, positionX, positionY, angle, scale, 0);
            if (placed.IsSuccess)
            {
                RegisterPlacedRole(placed.Entity, partTypeId);
            }
        }
    }

    private CommandOutcome SubmitSandbox(ReplayCommand command)
    {
        _sandboxPlayers.Register(command.PlayerId);
        CommandStatus status = _validator.Validate(
            command,
            Mode,
            CurrentTick,
            sandboxMode: true,
            materialized: _sandboxPlayers.IsMaterialized(command.PlayerId));
        ConstructionError ruleError = ConstructionError.None;
        uint entityId = 0;
        if (status == CommandStatus.Accepted)
        {
            (status, ruleError, entityId) = ExecuteSandboxCommand(command);
        }

        CommandOutcome outcome = new(command, status, ruleError, entityId);
        _outcomeLog.Add(outcome);
        return outcome;
    }

    private (CommandStatus Status, ConstructionError Error, uint EntityId) ExecuteSandboxCommand(ReplayCommand command)
    {
        switch (command)
        {
            case PlacePartCommand place:
            {
                ConstructionResult placed = _construction.Place(
                    place.PartTypeId, place.PositionX, place.PositionY, place.Angle, place.Scale, place.PlayerId);
                if (!placed.IsSuccess)
                {
                    return (CommandStatus.RuleRejected, placed.Error, 0);
                }

                RegisterPlacedRole(placed.Entity, place.PartTypeId);
                return (CommandStatus.Accepted, ConstructionError.None, placed.Entity.Value);
            }

            case RotatePartCommand rotate:
            {
                ConstructionResult rotated = _construction.Rotate(new EntityId(rotate.EntityId), rotate.Angle, rotate.PlayerId);
                return rotated.IsSuccess
                    ? (CommandStatus.Accepted, ConstructionError.None, rotate.EntityId)
                    : (CommandStatus.RuleRejected, rotated.Error, 0);
            }

            case MovePartCommand move:
            {
                ConstructionResult moved = _construction.Move(new EntityId(move.EntityId), move.PositionX, move.PositionY, move.PlayerId);
                return moved.IsSuccess
                    ? (CommandStatus.Accepted, ConstructionError.None, move.EntityId)
                    : (CommandStatus.RuleRejected, moved.Error, 0);
            }

            case ScalePartCommand scale:
            {
                ConstructionResult scaled = _construction.Scale(new EntityId(scale.EntityId), scale.Scale, scale.PlayerId);
                return scaled.IsSuccess
                    ? (CommandStatus.Accepted, ConstructionError.None, scale.EntityId)
                    : (CommandStatus.RuleRejected, scaled.Error, 0);
            }

            case SetPartActiveCommand activate:
                return SetPartActive(activate.EntityId, activate.Active, activate.PlayerId);

            case SetPartTypeActiveCommand activateType:
                return SetPartTypeActive(activateType.PartTypeId, activateType.Active, activateType.PlayerId);

            case RemovePartCommand remove:
            {
                EntityId entity = new(remove.EntityId);
                ConstructionResult removed = _construction.Remove(entity, remove.PlayerId);
                if (removed.IsSuccess)
                {
                    _rules.CleanupEntityStores(entity);
                    UnbindEntity(entity, destroyBodyIfOrphan: true);
                }

                return removed.IsSuccess
                    ? (CommandStatus.Accepted, ConstructionError.None, remove.EntityId)
                    : (CommandStatus.RuleRejected, removed.Error, 0);
            }

            case StartSimulationCommand start:
                try
                {
                    if (!StartPlayer(start.PlayerId))
                    {
                        return (CommandStatus.RuleRejected, ConstructionError.EntityNotFound, 0);
                    }
                }
                catch (NotSupportedException)
                {
                    return (CommandStatus.RuleRejected, ConstructionError.UnsupportedShape, 0);
                }

                return (CommandStatus.Accepted, ConstructionError.None, 0);

            case RetryCommand retry:
                ResetPlayer(retry.PlayerId);
                return (CommandStatus.Accepted, ConstructionError.None, 0);

            default:
                return (CommandStatus.UnknownKind, ConstructionError.None, 0);
        }
    }

    private (CommandStatus Status, ConstructionError Error, uint EntityId) SetPartActive(uint entityValue, bool active, uint owner)
    {
        EntityId entity = new(entityValue);
        if (!_entities.IsAlive(entity) || !_parts.TryGet(entity, out PartLink part))
        {
            return (CommandStatus.RuleRejected, ConstructionError.EntityNotFound, 0);
        }

        if (_construction.OwnerOf(entity) != owner)
        {
            return (CommandStatus.RuleRejected, ConstructionError.NotOwnedByPlayer, 0);
        }

        if (!IsSwitchable(part.PartTypeId) || !_rules.HasSwitch(entity))
        {
            return (CommandStatus.RuleRejected, ConstructionError.PartNotSwitchable, 0);
        }

        _rules.SetActive(entity, active);
        return (CommandStatus.Accepted, ConstructionError.None, entityValue);
    }

    /// <summary>Sets the switch for every part of one type the owner has placed, ascending.</summary>
    private (CommandStatus Status, ConstructionError Error, uint EntityId) SetPartTypeActive(uint partTypeId, bool active, uint owner)
    {
        if (!IsSwitchable(partTypeId))
        {
            return (CommandStatus.RuleRejected, ConstructionError.PartNotSwitchable, 0);
        }

        bool any = false;
        foreach (uint entityValue in _construction.PlacedEntitiesOf(owner))
        {
            EntityId entity = new(entityValue);
            if (_parts.TryGet(entity, out PartLink part) && part.PartTypeId == partTypeId && _rules.HasSwitch(entity))
            {
                _rules.SetActive(entity, active);
                any = true;
            }
        }

        return any
            ? (CommandStatus.Accepted, ConstructionError.None, 0u)
            : (CommandStatus.RuleRejected, ConstructionError.PartNotSwitchable, 0u);
    }

    private bool IsSwitchable(uint partTypeId) =>
        _content.GetPart(partTypeId).Capabilities?.Activation is PartActivation.Toggle or PartActivation.Trigger;

    /// <summary>Materialises one sandbox player's layout into authoritative bodies.
    /// Returns false when the player has no placed parts.</summary>
    private bool StartPlayer(uint playerId)
    {
        IReadOnlyCollection<uint> owned = _construction.PlacedEntitiesOf(playerId);
        if (owned.Count == 0)
        {
            return false;
        }

        List<EntityId> entities = new(owned.Count);
        foreach (uint entityValue in owned)
        {
            entities.Add(new EntityId(entityValue));
        }

        List<CompoundCluster> clusters = CompoundAssembler.Assemble(entities, _construction, _content, _seamBreakImpulse);
        foreach (CompoundCluster cluster in clusters)
        {
            BindCluster(cluster, cluster.CreateBodyDefinition(_content));
        }

        _sandboxPlayers.MarkMaterialized(playerId);
        EnsureBuffers();
        return true;
    }

    /// <summary>Per-player RESET: destroys only this player's entities and layout in
    /// ascending entity order, then returns the player to editing. Idempotent, and
    /// never touches the room-wide rules state.</summary>
    private void ResetPlayer(uint playerId)
    {
        foreach (uint entityValue in _construction.PlacedEntitiesOf(playerId))
        {
            EntityId entity = new(entityValue);
            _construction.Remove(entity, playerId);
            _rules.CleanupEntityStores(entity);
            UnbindEntity(entity, destroyBodyIfOrphan: true);
        }

        _sandboxPlayers.MarkEditing(playerId);
        DropOrphanedCommands();
    }

    /// <summary>Binds the level's owner-0 actors as bodies at setup so the sandbox world
    /// runs from tick zero.</summary>
    private void MaterializeLevelActors()
    {
        List<EntityId> entities = CollectPartEntities();
        List<CompoundCluster> clusters = CompoundAssembler.Assemble(entities, _construction, _content, _seamBreakImpulse);
        foreach (CompoundCluster cluster in clusters)
        {
            BindCluster(cluster, cluster.CreateBodyDefinition(_content));
        }

        EnsureBuffers();
        Mode = RoomMode.Running;
    }

    private List<EntityId> CollectPartEntities()
    {
        List<EntityId> entities = new();
        var parts = _parts.GetEnumerator();
        while (parts.MoveNext())
        {
            entities.Add(parts.CurrentId);
        }

        entities.Sort((left, right) => left.Value.CompareTo(right.Value));
        return entities;
    }

    /// <summary>Per-player out-of-bounds cleanup: a materialised player whose every
    /// entity is outside the map bounds is reset. Preview entities never participate
    /// and other players are unaffected.</summary>
    private void ResetPlayersOutOfBounds()
    {
        IReadOnlyList<uint> known = _sandboxPlayers.KnownPlayers;
        bool anyMaterialized = false;
        for (int index = 0; index < known.Count; index++)
        {
            if (_sandboxPlayers.IsMaterialized(known[index]))
            {
                anyMaterialized = true;
                break;
            }
        }

        if (!anyMaterialized)
        {
            return;
        }

        int snapshotCount = _world.CopySnapshots(_snapshotBuffer);
        for (int index = 0; index < known.Count; index++)
        {
            uint playerId = known[index];
            if (!_sandboxPlayers.IsMaterialized(playerId) || !IsPlayerOutOfBounds(playerId, snapshotCount))
            {
                continue;
            }

            ResetPlayer(playerId);
        }
    }

    private bool IsPlayerOutOfBounds(uint playerId, int snapshotCount)
    {
        IReadOnlyCollection<uint> owned = _construction.PlacedEntitiesOf(playerId);
        if (owned.Count == 0)
        {
            // Every part was destroyed during the run: the player is back to editing.
            return true;
        }

        foreach (uint entityValue in owned)
        {
            EntityId entity = new(entityValue);
            PhysicsVector3 position;
            if (_bodyByEntity.TryGetValue(entityValue, out PhysicsBodyId body))
            {
                if (!TryFindSnapshot(body, snapshotCount, out PhysicsBodySnapshot snapshot))
                {
                    return false;
                }

                (position, _) = WorldPose(entityValue, snapshot);
            }
            else if (_transforms.TryGet(entity, out EntityTransform transform))
            {
                position = transform.Position;
            }
            else
            {
                continue;
            }

            if (_gameplayConfig.MapBounds.Contains(position))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Drops queued rule commands whose target body no longer exists, so a
    /// per-player reset cannot feed impulses to destroyed bodies on the next tick.</summary>
    private void DropOrphanedCommands()
    {
        List<PhysicsCommand> commands = _output.Commands;
        for (int index = commands.Count - 1; index >= 0; index--)
        {
            if (!_entitiesByBody.ContainsKey(commands[index].Body.Value))
            {
                commands.RemoveAt(index);
            }
        }
    }

    private static float PlanarAngle(PhysicsQuaternion rotation)
    {
        // Construction places parts at Z-axis rotations only, so the yaw is atan2 of
        // the Z components of the quaternion (2*z, up to sign in the W-projection).
        float angle = 2f * MathF.Atan2(rotation.Z, rotation.W);
        return float.IsFinite(angle) ? angle : 0f;
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

        // Phase 5 (cleanup): bodies of destroyed entities leave the authoritative scene
        // and their construction footprint is released so the cells can be reused.
        foreach (EntityId destroyed in _output.DestroyedEntities)
        {
            _construction.Forget(destroyed);
            UnbindEntity(destroyed, destroyBodyIfOrphan: true);
        }

        // Detachers fired this tick: split their compound so the part flies free.
        foreach (EntityId detached in _output.DetachedEntities)
        {
            DetachFromCompound(detached, snapshotCount);
        }

        SplitFromAppliedCommands(snapshotCount);

        if (_sandboxMode)
        {
            ResetPlayersOutOfBounds();
        }
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
        if (_sandboxMode)
        {
            return TryPublishSandboxSnapshot(destination, out bytesWritten);
        }

        if (Mode == RoomMode.Building)
        {
            return TryPublishBuildingSnapshot(destination, out bytesWritten);
        }

        if (!SnapshotFrame.TryEncodeHeader(
                destination,
                new SnapshotFrameHeader(SnapshotFrame.CurrentVersion, CurrentTick, (byte)Phase, (uint)_bodyByEntity.Count),
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
                    _transforms.TryGet(new EntityId(entityValue), out EntityTransform transform) ? transform.Scale : 1f,
                    _rules.IsPartActive(new EntityId(entityValue)) ? (byte)1 : (byte)0)))
            {
                bytesWritten = 0;
                return false;
            }
        }

        bytesWritten = writer.WrittenBytes;
        return true;
    }

    private bool TryPublishBuildingSnapshot(Span<byte> destination, out int bytesWritten)
    {
        FillConstructionOrder();
        if (!SnapshotFrame.TryEncodeHeader(
                destination,
                new SnapshotFrameHeader(SnapshotFrame.CurrentVersion, CurrentTick, SnapshotFrame.BuildingPhase, (uint)_entityOrder.Count),
                out SnapshotFrameWriter writer))
        {
            bytesWritten = 0;
            return false;
        }

        ReplayVector3 zero = ReplayVector3.Zero;
        for (int index = 0; index < _entityOrder.Count; index++)
        {
            uint entityValue = _entityOrder[index];
            EntityId entity = new(entityValue);
            if (!_parts.TryGet(entity, out PartLink part) || !_transforms.TryGet(entity, out EntityTransform transform))
            {
                bytesWritten = 0;
                return false;
            }

            if (!writer.WriteEntity(new SnapshotEntity(
                    entityValue,
                    0,
                    part.PartTypeId,
                    ToReplay(transform.Position),
                    ToReplay(transform.Rotation),
                    zero,
                    zero,
                    transform.Scale,
                    _rules.IsPartActive(entity) ? (byte)1 : (byte)0)))
            {
                bytesWritten = 0;
                return false;
            }
        }

        bytesWritten = writer.WrittenBytes;
        return true;
    }

    /// <summary>
    /// Sandbox frame: one mixed frame per tick carrying every entity with a part link.
    /// Bound entities use the running-style record (real body id, world pose, real
    /// velocities); previews carry body id 0 at their transform pose with zero
    /// velocities. Entities ascend by id and the phase is always Playing.
    /// </summary>
    private bool TryPublishSandboxSnapshot(Span<byte> destination, out int bytesWritten)
    {
        FillConstructionOrder();
        if (!SnapshotFrame.TryEncodeHeader(
                destination,
                new SnapshotFrameHeader(SnapshotFrame.CurrentVersion, CurrentTick, (byte)GameplayPhase.Playing, (uint)_entityOrder.Count),
                out SnapshotFrameWriter writer))
        {
            bytesWritten = 0;
            return false;
        }

        int count = _world.CopySnapshots(_snapshotBuffer);
        ReplayVector3 zero = ReplayVector3.Zero;
        for (int index = 0; index < _entityOrder.Count; index++)
        {
            uint entityValue = _entityOrder[index];
            EntityId entity = new(entityValue);
            if (!_parts.TryGet(entity, out PartLink part) || !_transforms.TryGet(entity, out EntityTransform transform))
            {
                bytesWritten = 0;
                return false;
            }

            uint bodyId = 0;
            PhysicsVector3 position = transform.Position;
            PhysicsQuaternion rotation = transform.Rotation;
            ReplayVector3 linearVelocity = zero;
            ReplayVector3 angularVelocity = zero;
            if (_bodyByEntity.TryGetValue(entityValue, out PhysicsBodyId body))
            {
                if (!TryFindSnapshot(body, count, out PhysicsBodySnapshot snapshot))
                {
                    bytesWritten = 0;
                    return false;
                }

                bodyId = snapshot.Body.Value;
                (position, rotation) = WorldPose(entityValue, snapshot);
                linearVelocity = ToReplay(snapshot.LinearVelocity);
                angularVelocity = ToReplay(snapshot.AngularVelocity);
            }

            if (!writer.WriteEntity(new SnapshotEntity(
                    entityValue,
                    bodyId,
                    part.PartTypeId,
                    ToReplay(position),
                    ToReplay(rotation),
                    linearVelocity,
                    angularVelocity,
                    transform.Scale,
                    _rules.IsPartActive(entity) ? (byte)1 : (byte)0)))
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

        hash = unchecked((hash * 31) + _rules.ComputeActivationHash());
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

    /// <summary>Splits the compound carrying <paramref name="entity"/> at the seam
    /// nearest that part's own pose, so a fired detacher leaves the rig as its own
    /// body (original detacher part). No-op when the body is already single-piece.</summary>
    private void DetachFromCompound(EntityId entity, int snapshotCount)
    {
        if (!_bodies.TryGet(entity, out PhysicsBodyLink link)
            || !TryFindSnapshot(link.Body, snapshotCount, out PhysicsBodySnapshot snapshot))
        {
            return;
        }

        int liveIndex = _liveCompounds.FindIndex(candidate => candidate.Body == link.Body);
        if (liveIndex < 0)
        {
            return;
        }

        (PhysicsVector3 position, PhysicsQuaternion rotation) = WorldPose(entity.Value, snapshot);
        LiveCompound live = _liveCompounds[liveIndex];
        live.Cluster.WorldPosition = snapshot.Position;
        live.Cluster.WorldRotation = snapshot.Rotation;
        CompoundSeam? seam = CompoundAssembler.NearestSeam(live.Cluster, position);
        if (seam is null)
        {
            return;
        }

        IReadOnlyList<CompoundCluster> pieces = CompoundAssembler.SplitAlongSeam(live.Cluster, seam.Value);
        if (pieces.Count == 1)
        {
            live.Cluster = pieces[0];
            return;
        }

        List<uint> members = _entitiesByBody.TryGetValue(link.Body.Value, out List<uint>? bound)
            ? new List<uint>(bound)
            : new List<uint>();
        foreach (uint entityValue in members)
        {
            UnbindEntity(new EntityId(entityValue), destroyBodyIfOrphan: false);
        }

        _world.DestroyBody(link.Body);
        foreach (CompoundCluster piece in pieces)
        {
            BindCluster(piece, piece.CreateBodyDefinition(_content, snapshot.LinearVelocity, snapshot.AngularVelocity));
        }

        EnsureBuffers();
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

    private void FillConstructionOrder()
    {
        _entityOrder.Clear();
        var parts = _parts.GetEnumerator();
        while (parts.MoveNext())
        {
            _entityOrder.Add(parts.CurrentId.Value);
        }

        _entityOrder.Sort();
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
        int entityCapacity = Math.Max(bodyCount, MaxSnapshotEntityCount);
        if (_snapshotBuffer.Length < entityCapacity)
        {
            _snapshotBuffer = new PhysicsBodySnapshot[entityCapacity];
        }

        int eventCapacity = (bodyCount * (bodyCount - 1) / 2) + bodyCount + 16;
        if (_eventBuffer.Length < eventCapacity)
        {
            _eventBuffer = new PhysicsEvent[eventCapacity];
        }

        if (_entityOrder.Capacity < entityCapacity)
        {
            _entityOrder.Capacity = entityCapacity;
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
