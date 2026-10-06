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
    private readonly BlasterStore _blasters;
    private readonly GlueStore _glues;
    private readonly ActivationStore _activations;
    private readonly RestitutionStore _restitutions;
    private readonly PowerStore _powers;
    private readonly ConstructionRules _construction;
    private readonly GameplayRules _rules;
    private readonly CommandValidator _validator = new();
    private readonly GameplayTickOutput _output = new();
    private readonly Dictionary<uint, PhysicsBodyId> _bodyByEntity = new();
    private readonly Dictionary<uint, EntityId> _entityByBody = new();
    private readonly Dictionary<uint, List<uint>> _entitiesByBody = new();
    private readonly Dictionary<uint, (PhysicsVector3 Offset, PhysicsQuaternion Rotation)> _compoundLocalByEntity = new();
    private readonly Dictionary<uint, (PhysicsVector3 Position, PhysicsQuaternion Rotation)> _bodyPose = new();
    private readonly List<(PhysicsJointId Joint, PhysicsBodyId Wheel, PhysicsBodyId Parent)> _wheelJoints = new();
    private readonly List<(PhysicsJointId Joint, PhysicsBodyId Left, PhysicsBodyId Right, CompoundWeld Weld)> _weldJoints = new();
    private readonly List<CompoundWeld> _welds = new();
    private readonly HashSet<long> _weldKeys = new();
    private readonly List<SpringJoint> _springJoints = new();
    private readonly List<CompoundSpring> _springs = new();
    private readonly HashSet<long> _springKeys = new();
    private readonly Dictionary<uint, List<SubEntity>> _subEntitiesByHost = new();
    private readonly HashSet<uint> _subEntityIds = new();
    // Sub-entities the original keeps inactive while their machine idles (a boxing glove's fist:
    // `InitilizeBoxingGlove` ends with `m_BoxingGlove.SetActive(false)`, SpringBoxingGlove.cs:215-222,
    // and the throw turns it back on, :224-262). An inactive GameObject is neither drawn nor
    // simulated, so a stowed sub-entity is left out of the published frame instead of being drawn
    // over its host.
    private readonly HashSet<uint> _stowedSubEntityIds = new();
    private int _subEntityCount;
    // Boxing gloves (docs/specs/boxing-glove.md): the host entity's runtime state, walked in
    // ascending host order each tick so the transitions — and the joints they rebuild — are
    // deterministic. _effectCandidates/_doomedSeams are tick scratch, reused so a punch never
    // allocates.
    private readonly Dictionary<uint, GloveLink> _glovesByHost = new();
    private readonly List<uint> _gloveHosts = new();
    private readonly List<uint> _effectCandidates = new();
    private readonly List<CompoundSeam> _doomedSeams = new();
    private readonly List<(PhysicsJointId Joint, PhysicsBodyId Attach, PhysicsBodyId Anchor)> _attachmentJoints = new();
    private readonly Dictionary<uint, (PhysicsBodyId Body, PhysicsQuaternion LocalRotation)> _attachByEntity = new();
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
    private readonly List<CapturedPart> _retryLayout = new();
    private readonly Dictionary<uint, List<CapturedPart>> _startLayoutByPlayer = new();
    private bool _disposed;

    /// <summary>
    /// One part of a layout captured at materialisation: the entity it was, and what it takes
    /// to build it again (part type, build pose, scale). Entity value first so a captured
    /// layout sorts deterministically and a rebuild places its parts in the same order they
    /// were built.
    /// </summary>
    private readonly record struct CapturedPart(
        uint EntityValue,
        uint PartTypeId,
        float PositionX,
        float PositionY,
        float Angle,
        float Scale,
        bool Mirrored);

    /// <summary>
    /// The 2.5D lock every authoritative body carries: freeze the Z translation and the X/Y
    /// rotations, exactly the original's <c>RigidbodyConstraints</c> 56
    /// (<c>FreezePositionZ | FreezeRotationX | FreezeRotationY</c>, Sandbag.cs:135, Pig.cs:235);
    /// the build plane is X-Y, only a Z rotation is free. It is applied when a body is created,
    /// so a body born from a seam split stays planar like the compound it came from, and it is
    /// the physics layer (the pose integrator) that enforces it, not the rules.
    /// </summary>
    private const PhysicsConstraintMask PlanarConstraintMask =
        PhysicsConstraintMask.LockPositionZ | PhysicsConstraintMask.LockRotationX | PhysicsConstraintMask.LockRotationY;

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
        _blasters = new BlasterStore(_entities);
        _glues = new GlueStore(_entities);
        _activations = new ActivationStore(_entities);
        _restitutions = new RestitutionStore(_entities);
        _powers = new PowerStore(_entities);
        _construction = new ConstructionRules(_entities, _parts, _transforms, _content);
        // Elasticity is owned by whichever side can express it: a backend with a native
        // restitution term applies it in the solver, otherwise the rules layer synthesizes it.
        _rules = new GameplayRules(
            _entities, _motors, _balloons, _fans, _rockets, _tnt, _blasters, _glues, _wheels, _pigs, _eggs, _wings, _tails, _umbrellas, _gearboxes, _bellows, _detachers, _grapples, _activations, _restitutions, _powers, _bodies, options.GameplayConfig with
            {
                RestitutionAppliedNatively = _world.Capabilities.AppliesRestitutionNatively,
            });
    }

    public RoomMode Mode { get; private set; } = RoomMode.Building;

    public uint CurrentTick { get; private set; }

    public GameplayPhase Phase => _rules.Phase;

    public int AlivePigs => _rules.AlivePigs;

    public bool RestartRequested => _rules.RestartRequested;

    public int BodyCount => _bodyByEntity.Count;

    /// <summary>Live frame-to-frame welds. Diagnostic like <see cref="BodyCount"/>: a weld is a
    /// joint between two bodies and appears in no snapshot (docs/specs/weld-compliance.md).</summary>
    public int WeldJointCount => _weldJoints.Count;

    /// <summary>Live spring seams. Diagnostic like <see cref="WeldJointCount"/>: a spring is a joint
    /// between two placed parts and appears in no snapshot of its own (docs/specs/spring-joint.md).</summary>
    public int SpringJointCount => _springJoints.Count;

    /// <summary>Live runtime sub-entities (ADR-027): bodies a host part spawned, whose first
    /// consumer is the spring's endpoint rigid body. Diagnostic like <see cref="WeldJointCount"/>.</summary>
    public int SubEntityCount => _subEntityCount;

    /// <summary>Upper bound on the entity count of the next published frame. Sandbox
    /// frames also carry previews that have no physics body, so transports must size
    /// their encode buffer from this value rather than from <see cref="BodyCount"/>.
    /// Runtime sub-entities (ADR-027) carry a part link and a body, so they count in both
    /// terms — a spring endpoint spawned mid-run must not push a frame past the buffer.</summary>
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
        PartDefinition part = _content.GetPart(spec.PartTypeId);
        _rules.AddRestitution(entity, part.Restitution, _content.MassOf(part));

        // Level actors carry no construction relations, so an engine spawned straight from a level
        // is never enclosed and supplies nothing (Engine.cs:61); a level-authored consumer still
        // gets its content power data so its drive is gated by the level's own engines.
        if (part.Capabilities is PartCapabilities capabilities && (capabilities.IsPowered || capabilities.IsEngine))
        {
            _rules.AddPower(entity, capabilities.PowerConsumption, capabilities.EnginePower);
        }

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
                    place.PartTypeId, place.PositionX, place.PositionY, place.Angle, place.Scale, 0, place.Mirrored);
                if (!placed.IsSuccess)
                {
                    return (CommandStatus.RuleRejected, placed.Error, 0);
                }

                RegisterPlacedRole(placed.Entity, place.PartTypeId);
                return (CommandStatus.Accepted, ConstructionError.None, placed.Entity.Value);
            }

            case RotatePartCommand rotate:
            {
                ConstructionResult rotated = _construction.Rotate(new EntityId(rotate.EntityId), rotate.Angle, 0, rotate.Mirrored);
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
        PartDefinition part = _content.GetPart(partTypeId);
        // Elasticity is plain material data, so it registers whether or not the part carries
        // any capability at all.
        _rules.AddRestitution(entity, part.Restitution, _content.MassOf(part));
        PartCapabilities? capabilities = part.Capabilities;
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

        if (capabilities.IsPowered || capabilities.IsEngine)
        {
            _rules.AddPower(entity, capabilities.PowerConsumption, capabilities.EnginePower);
        }

        if (capabilities.IsWheel)
        {
            _rules.AddWheel(entity);
        }

        if (capabilities.TntFuseTicks is ushort fuse)
        {
            _rules.AddTnt(entity, fuse, capabilities.TntChainDetonate, capabilities.TntIgniteOnImpact);
        }

        if (capabilities.HasBlaster)
        {
            _rules.AddBlaster(entity, capabilities.BlasterRadius!.Value, capabilities.BlasterImpulse ?? 0f, capabilities.BlasterChainRadius ?? 0f);
        }

        if (capabilities.IsGlue)
        {
            _rules.AddGlue(entity);
        }

        if (capabilities.HasBalloon)
        {
            _rules.AddBalloon(entity, capabilities.BalloonLiftPerTick!.Value);
        }

        if (capabilities.HasFan)
        {
            _rules.AddFan(
                entity,
                capabilities.FanThrustPerTick!.Value,
                capabilities.FanDirectionX ?? 1f,
                capabilities.FanDirectionY ?? 0f,
                capabilities.FanMaxSpeed ?? 0f,
                capabilities.FanIsRotor);
        }

        if (capabilities.HasRocket)
        {
            _rules.AddRocket(
                entity,
                capabilities.RocketThrustPerTick!.Value,
                capabilities.RocketDirectionX ?? 1f,
                capabilities.RocketDirectionY ?? 0f,
                capabilities.RocketIgnitionTicks!.Value,
                capabilities.RocketBoostTicks!.Value,
                capabilities.RocketEndTicks!.Value,
                capabilities.RocketMaxSpeed!.Value,
                capabilities.RocketVisualization,
                capabilities.RocketExplodeRadius ?? 0f,
                capabilities.RocketExplodeImpulse ?? 0f);
        }

        if (capabilities.IsEgg)
        {
            _rules.AddEgg(entity);
        }

        if (capabilities.HasWing)
        {
            _rules.AddWing(entity, capabilities.WingLiftConstant!.Value);
        }

        if (capabilities.HasTail)
        {
            _rules.AddTail(entity, capabilities.TailLiftConstant!.Value);
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
            _rules.AddBellows(
                entity,
                capabilities.BellowsThrustPerTick!.Value,
                capabilities.BellowsDirectionX ?? 1f,
                capabilities.BellowsDirectionY ?? 0f,
                capabilities.BellowsInflateTicks!.Value);
        }

        if (capabilities.HasGrapple)
        {
            _rules.AddGrapple(entity, capabilities.GrappleImpulse!.Value, capabilities.GrappleDirectionX ?? 1f, capabilities.GrappleDirectionY ?? 0f);
        }

        // A detacher is a switch-fired seam split: its trigger makes the room detach the part
        // along the nearest seam of the compound it sits in (GameRoom.DetachFromCompound,
        // original detacher part), and a hard impact does the same (DetachOnImpact).
        if (capabilities.IsDetacher)
        {
            _rules.AddDetacher(entity);
        }

        if (_sandboxMode && IsSwitchable(partTypeId))
        {
            _rules.AddActivation(entity);
        }
    }

    /// <summary>
    /// Publishes the current enclosure relation to the rules layer: an engine only supplies power
    /// while it sits inside a frame (<c>Engine.ValidatePart() =&gt; m_enclosedInto != null</c>,
    /// Engine.cs:61, spec docs/specs/power-system.md §4 item 2). Evaluated at materialisation,
    /// while the construction rules still own the layout -- a running room never re-encloses.
    /// </summary>
    private void SyncEngineEnclosure()
    {
        foreach (uint entityValue in _construction.PlacedEntities)
        {
            EntityId entity = new(entityValue);
            _rules.SetEngineEnclosed(entity, _construction.EnclosedBy(entity) is not null);
        }
    }

    /// <summary>
    /// Publishes the original's propulsion gate: a fan/rotor, rocket, bellows, wing or tail is only
    /// valid when at least one of its neighbours is part of the chassis
    /// (<c>BasePropulsion.cs:13-20</c>; <c>Wings.cs:14-31</c>; <c>Tail.cs:12-29</c>). PigForge keeps
    /// such a part buildable and only strips its force at runtime, so the flag is published here at
    /// materialisation (the same point as the engine enclosure) and the rules layer never re-derives
    /// it per tick. Level actors carry no construction relations and are never declared, which keeps
    /// them ungated.
    /// </summary>
    private void SyncChassisAnchors()
    {
        foreach (uint entityValue in _construction.PlacedEntities)
        {
            EntityId entity = new(entityValue);
            _rules.SetChassisAnchored(entity, _construction.HasChassisNeighbor(entity));
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
        _retryLayout.AddRange(CaptureLayout(_construction.PlacedEntities));

        SyncEngineEnclosure();
        SyncChassisAnchors();
        CompoundAssembly assembly = CompoundAssembler.Assemble(entities, _construction, _content, _seamBreakImpulse);
        foreach (CompoundCluster cluster in assembly.Clusters)
        {
            BindCluster(cluster, cluster.CreateBodyDefinition(_content, _construction, constraints: PlanarConstraintMask));
        }

        BindWheelHinges(assembly.Hinges);
        BindWeldJoints(assembly.Welds);
        BindSpringJoints(assembly.Springs);
        BindAttachments();
        BindGloves();

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
        _bodyPose.Clear();
        _wheelJoints.Clear();
        _weldJoints.Clear();
        _welds.Clear();
        _weldKeys.Clear();
        _springJoints.Clear();
        _springs.Clear();
        _springKeys.Clear();
        _attachmentJoints.Clear();
        _attachByEntity.Clear();
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

        // Runtime sub-entities never survive a build-mode re-entry: their bodies were destroyed with
        // every other body above, and their entity ids and part entries go now (ADR-027 decision 3).
        ClearSubEntities();
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
        _bodyPose.Clear();
        _wheelJoints.Clear();
        _weldJoints.Clear();
        _welds.Clear();
        _weldKeys.Clear();
        _springJoints.Clear();
        _springs.Clear();
        _springKeys.Clear();
        _attachmentJoints.Clear();
        _attachByEntity.Clear();
        _liveCompounds.Clear();
        ClearSubEntities();

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

        foreach (CapturedPart part in _retryLayout)
        {
            ConstructionResult placed = _construction.Place(part.PartTypeId, part.PositionX, part.PositionY, part.Angle, part.Scale, 0, part.Mirrored);
            if (placed.IsSuccess)
            {
                RegisterPlacedRole(placed.Entity, part.PartTypeId);
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
                    place.PartTypeId, place.PositionX, place.PositionY, place.Angle, place.Scale, place.PlayerId, place.Mirrored);
                if (!placed.IsSuccess)
                {
                    return (CommandStatus.RuleRejected, placed.Error, 0);
                }

                RegisterPlacedRole(placed.Entity, place.PartTypeId);
                return (CommandStatus.Accepted, ConstructionError.None, placed.Entity.Value);
            }

            case RotatePartCommand rotate:
            {
                ConstructionResult rotated = _construction.Rotate(new EntityId(rotate.EntityId), rotate.Angle, rotate.PlayerId, rotate.Mirrored);
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

        // Remember what this run was built from: RESET rebuilds exactly this layout (the parts
        // the player is editing again, at the poses they were built at), not a re-placed one.
        _startLayoutByPlayer[playerId] = CaptureLayout(owned);

        SyncEngineEnclosure();
        SyncChassisAnchors();
        List<EntityId> entities = new(owned.Count);
        foreach (uint entityValue in owned)
        {
            entities.Add(new EntityId(entityValue));
        }

        CompoundAssembly assembly = CompoundAssembler.Assemble(entities, _construction, _content, _seamBreakImpulse);
        foreach (CompoundCluster cluster in assembly.Clusters)
        {
            BindCluster(cluster, cluster.CreateBodyDefinition(_content, _construction, constraints: PlanarConstraintMask));
        }

        BindWheelHinges(assembly.Hinges);
        BindWeldJoints(assembly.Welds);
        BindSpringJoints(assembly.Springs);
        BindAttachments();
        BindGloves();

        _sandboxPlayers.MarkMaterialized(playerId);
        EnsureBuffers();
        return true;
    }

    /// <summary>
    /// Per-player RESET: the player's materialised bodies leave the world and the layout the
    /// run was started from comes back as previews, so RESET is a rebuild loop rather than a
    /// re-place -- Start can run the same contraption again without rebuilding it. Parts the
    /// run destroyed (a blast, a seam detach) are re-created from the captured layout; a part
    /// the player removed while editing stays removed. Every other player is untouched, and
    /// the room-wide rules state is never reset.
    /// </summary>
    private void ResetPlayer(uint playerId)
    {
        // Only a materialised player can have lost parts to the run; an editing player's
        // missing parts are ones it removed on purpose.
        bool materialized = _sandboxPlayers.IsMaterialized(playerId);
        List<uint> owned = SortedPlacedEntitiesOf(playerId);

        // Drop the bodies and the runtime rule state of the parts that are still there. The
        // construction entities stay: they keep their cells and become previews again at
        // their build poses (a running room never wrote the transform store).
        foreach (uint entityValue in owned)
        {
            EntityId entity = new(entityValue);
            _rules.CleanupEntityStores(entity);
            UnbindEntity(entity, destroyBodyIfOrphan: true);
        }

        // The frames survive the reset, so their weld definitions have to go with the joints the
        // destroyed bodies took with them (see ForgetWeldsForEntities).
        ForgetWeldsForEntities(owned);
        // Same for the spring parts: the next Start re-registers their seams from the assembler.
        ForgetSpringsForEntities(owned);

        if (materialized && _startLayoutByPlayer.TryGetValue(playerId, out List<CapturedPart>? layout))
        {
            foreach (CapturedPart part in layout)
            {
                if (_entities.IsAlive(new EntityId(part.EntityValue)))
                {
                    continue;
                }

                // A cell another player has taken since the part was destroyed keeps its owner:
                // the rebuilt part is left out rather than stealing the cell.
                ConstructionResult placed = _construction.Place(part.PartTypeId, part.PositionX, part.PositionY, part.Angle, part.Scale, playerId, part.Mirrored);
                if (placed.IsSuccess)
                {
                    RegisterPlacedRole(placed.Entity, part.PartTypeId);
                }
            }
        }

        // Roles come back from content, exactly as a fresh placement registers them: the
        // switch starts off again, charges re-arm, and the enclosure/chassis gates the rules
        // layer reads are re-published over the restored layout.
        foreach (uint entityValue in owned)
        {
            if (_parts.TryGet(new EntityId(entityValue), out PartLink part))
            {
                RegisterPlacedRole(new EntityId(entityValue), part.PartTypeId);
            }
        }

        SyncEngineEnclosure();
        SyncChassisAnchors();
        _sandboxPlayers.MarkEditing(playerId);
        DropOrphanedCommands();
    }

    /// <summary>
    /// A socket closed: the player leaves the room and its parts leave the world with it. The
    /// captured layout goes too, so a later connection is a new player with an empty build
    /// plane -- nothing of the departed player survives, and no ghost part can linger in a
    /// cell nobody owns.
    /// </summary>
    public void LeavePlayer(uint playerId)
    {
        ThrowIfDisposed();
        if (!_sandboxMode)
        {
            return;
        }

        List<uint> owned = SortedPlacedEntitiesOf(playerId);
        foreach (uint entityValue in owned)
        {
            EntityId entity = new(entityValue);
            _construction.Remove(entity, playerId);
            _rules.CleanupEntityStores(entity);
            UnbindEntity(entity, destroyBodyIfOrphan: true);
        }

        ForgetWeldsForEntities(owned);
        ForgetSpringsForEntities(owned);
        _startLayoutByPlayer.Remove(playerId);
        _sandboxPlayers.Forget(playerId);
        _validator.Forget(playerId);
        DropOrphanedCommands();
    }

    /// <summary>Binds the level's owner-0 actors as bodies at setup so the sandbox world
    /// runs from tick zero.</summary>
    private void MaterializeLevelActors()
    {
        SyncEngineEnclosure();
        SyncChassisAnchors();
        List<EntityId> entities = CollectPartEntities();
        CompoundAssembly assembly = CompoundAssembler.Assemble(entities, _construction, _content, _seamBreakImpulse);
        foreach (CompoundCluster cluster in assembly.Clusters)
        {
            BindCluster(cluster, cluster.CreateBodyDefinition(_content, _construction, constraints: PlanarConstraintMask));
        }

        BindWheelHinges(assembly.Hinges);
        BindWeldJoints(assembly.Welds);
        BindSpringJoints(assembly.Springs);
        BindAttachments();
        BindGloves();

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

    /// <summary>
    /// Captures what it takes to rebuild the given parts: part type, build pose and scale,
    /// ascending by entity so a rebuild places them in the order they were built.
    /// </summary>
    private List<CapturedPart> CaptureLayout(IEnumerable<uint> entityValues)
    {
        List<CapturedPart> layout = new();
        foreach (uint entityValue in entityValues)
        {
            EntityId entity = new(entityValue);
            if (_parts.TryGet(entity, out PartLink part) && _transforms.TryGet(entity, out EntityTransform transform))
            {
                bool mirrored = _construction.IsMirrored(entity);
                layout.Add(new CapturedPart(
                    entityValue,
                    part.PartTypeId,
                    transform.Position.X,
                    transform.Position.Y,
                    BuildPose.YawOf(transform.Rotation, mirrored),
                    transform.Scale,
                    mirrored));
            }
        }

        layout.Sort((left, right) => left.EntityValue.CompareTo(right.EntityValue));
        return layout;
    }

    /// <summary>One owner's construction entities, ascending: the order every reset walks them in.</summary>
    private List<uint> SortedPlacedEntitiesOf(uint playerId)
    {
        List<uint> entities = new(_construction.PlacedEntitiesOf(playerId));
        entities.Sort();
        return entities;
    }

    /// <summary>Per-player out-of-bounds cleanup: a materialised player whose every
    /// entity is outside the map bounds is reset (the same reset the RESET command
    /// runs, so the layout comes back as previews). Preview entities never participate
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
        HandleBrokenSprings(eventCount);
        foreach (EntityId destroyed in _output.DestroyedEntities)
        {
            _construction.Forget(destroyed);
            UnbindEntity(destroyed, destroyBodyIfOrphan: true);
        }

        // The gloves' state machines run on this tick's telemetry: the button/switch level, the throw's
        // timers and the glove's own offset. A transition rewrites the joint/body, and a throw can
        // split the host's compound (the part behind the glove leaves), so this runs before the
        // split paths that read _liveCompounds.
        RunGloves(snapshotCount);

        // Detachers fired this tick: split their compound so the part flies free.
        foreach (EntityId detached in _output.DetachedEntities)
        {
            DetachFromCompound(detached, snapshotCount);
        }

        SplitFromAppliedCommands(snapshotCount);
        BreakWeldsFromAppliedCommands(snapshotCount);
        BreakSpringsFromPull(snapshotCount);
        // A spring endpoint spawned this tick added a body after the buffers were sized, and the next
        // tick's snapshot copy must have room for it. Done last: growing the snapshot buffer discards
        // this tick's telemetry, which the phase above still reads.
        EnsureBuffers();

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
                    AttachYawOf(entityValue, rotation, count),
                    SnapshotFlags(new EntityId(entityValue)))))
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
                    YawOf(transform.Rotation),
                    SnapshotFlags(entity))))
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
                    bodyId == 0 ? YawOf(rotation) : AttachYawOf(entityValue, rotation, count),
                    SnapshotFlags(entity))))
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

    /// <summary>
    /// One entity's snapshot flags (PGFS v6): bit0 the part's switch is on, bit1 the entity is a
    /// runtime sub-entity of its host (ADR-027). A sub-entity borrows its host's part type on the
    /// wire, so without bit1 the client cannot tell a boxing glove's fist from a second glove part
    /// and draws the host's own art over it. Bit2 is the build pose's mirror (ADR-030), which the
    /// client cannot derive from the rotation either -- a mirrored pose at a quarter turn is an
    /// ordinary 180-degree rotation.
    /// </summary>
    private byte SnapshotFlags(EntityId entity) =>
        (byte)((_rules.IsPartActive(entity) ? SnapshotFrame.FlagActive : 0)
            | (_subEntityIds.Contains(entity.Value) ? SnapshotFrame.FlagSubEntity : 0)
            | (_construction.IsMirrored(entity) ? SnapshotFrame.FlagMirrored : 0));

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
        // The power factor is rules state the physics snapshots cannot show (a gated wheel emits
        // no command at all), so determinism has to see it here (spec docs/specs/power-system.md §4 item 3).
        hash = unchecked((hash * 31) + _rules.ComputePowerHash());
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
        _bodyPose.Clear();
        _wheelJoints.Clear();
        _weldJoints.Clear();
        _welds.Clear();
        _weldKeys.Clear();
        _springJoints.Clear();
        _springs.Clear();
        _springKeys.Clear();
        _attachmentJoints.Clear();
        _attachByEntity.Clear();
        _liveCompounds.Clear();
        _subEntitiesByHost.Clear();
        _subEntityIds.Clear();
        _stowedSubEntityIds.Clear();
        _subEntityCount = 0;
        _appliedCommands.Clear();
        _outcomeLog.Clear();
        _output.Clear();
        _snapshotBuffer = Array.Empty<PhysicsBodySnapshot>();
        _eventBuffer = Array.Empty<PhysicsEvent>();
    }

    private void BindCluster(CompoundCluster cluster, BodyDefinition definition)
    {
        PhysicsBodyId body = _world.CreateBody(definition);
        _bodyPose[body.Value] = (definition.Position, definition.Rotation);
        foreach (CompoundMember member in cluster.Members)
        {
            _bodies.Set(member.Entity, new PhysicsBodyLink(body));
            _rules.LinkBody(
                member.Entity,
                body,
                isDynamic: definition.Mode == PhysicsBodyMode.Dynamic,
                localOffset: member.LocalOffset,
                localRotation: member.LocalRotation,
                mirrored: _construction.IsMirrored(member.Entity));
            _bodyByEntity.Add(member.Entity.Value, body);
            if (!_entitiesByBody.TryGetValue(body.Value, out List<uint>? members))
            {
                members = new List<uint>();
                _entitiesByBody.Add(body.Value, members);
                _entityByBody.Add(body.Value, member.Entity);
            }

            members.Add(member.Entity.Value);
            if (cluster.IsMerged
                || member.LocalOffset != PhysicsVector3.Zero
                || member.LocalRotation != PhysicsQuaternion.Identity)
            {
                _compoundLocalByEntity[member.Entity.Value] = (member.LocalOffset, member.LocalRotation);
            }
        }

        if (cluster.IsMerged)
        {
            _liveCompounds.Add(new LiveCompound(body, cluster));
        }
    }

    /// <summary>
    /// Creates the revolute joints that attach every wheel part's own body to a neighbour
    /// body at the wheel axle, so wheels roll instead of skidding with the chassis. The axle
    /// is the wheel's spin centre (its tire centre), not its part origin: hinging anywhere
    /// else makes the tire orbit the joint like a cam.
    /// A wheel whose content carries a <see cref="PartSuspension"/> gets the original's
    /// elastic attachment instead of a rigid axle: the same revolute joint plus the sprung
    /// linear degree of freedom the original declares along the wheel's own Y
    /// (OffRoadWheel.CustomConnectToPart, OffRoadWheel.cs:202-220). The spring numbers are the
    /// extracted Unity ones, converted by the shared <see cref="TrySpringResponse"/>.
    /// </summary>
    private void BindWheelHinges(IReadOnlyList<CompoundHinge> hinges)
    {
        foreach (CompoundHinge hinge in hinges)
        {
            if (!_bodies.TryGet(hinge.Wheel, out PhysicsBodyLink wheelLink)
                || !_bodies.TryGet(hinge.Parent, out PhysicsBodyLink parentLink)
                || wheelLink.Body == parentLink.Body
                || !_bodyPose.TryGetValue(wheelLink.Body.Value, out (PhysicsVector3 Position, PhysicsQuaternion Rotation) wheelPose)
                || !_bodyPose.TryGetValue(parentLink.Body.Value, out (PhysicsVector3 Position, PhysicsQuaternion Rotation) parentPose)
                || !_transforms.TryGet(hinge.Wheel, out EntityTransform transform))
            {
                continue;
            }

            PhysicsVector3 axle = transform.Position + transform.Rotation.Rotate(hinge.LocalAxle);
            // The original locks every angular axis of the wheel and keeps only its local Y
            // Limited, so that Y is the suspension line; because it also locks the wheel's
            // rotation, the line is rigid to the chassis. Our wheel body spins (ADR-009), so
            // the line is expressed in the parent body's frame and the wheel's own build-frame
            // Y is the direction it points along.
            PhysicsVector3 suspensionAxis = PhysicsVector3.Zero;
            float suspensionFrequency = 0f;
            float suspensionDampingRatio = 1f;
            float suspensionRestOffset = 0f;
            if (_parts.TryGet(hinge.Wheel, out PartLink wheelPartLink)
                && _content.GetPart(wheelPartLink.PartTypeId).Capabilities?.Suspension is PartSuspension suspension)
            {
                // The spring joins the two bodies, so their masses are what turns the extracted
                // N/m into the solver's frequency: the wheel's own body (its tire) and the whole
                // parent body, mounts included, exactly the masses the constraint will solve
                // with. The original's stiffness is then the stiffness the solver applies, and
                // the sag stays load / stiffness whatever the chassis is made of.
                if (TrySpringResponse(
                        BodyMass(wheelLink.Body),
                        BodyMass(parentLink.Body),
                        suspension.Stiffness,
                        suspension.Damper,
                        out suspensionFrequency,
                        out suspensionDampingRatio))
                {
                    PhysicsVector3 wheelUp = transform.Rotation.Rotate(new PhysicsVector3(0f, 1f, 0f));
                    suspensionAxis = PhysicsVector3.Normalize(parentPose.Rotation.Inverse.Rotate(wheelUp));
                    suspensionRestOffset = suspension.RestOffset;
                }
            }

            JointDefinition definition = new(
                PhysicsJointKind.Revolute,
                parentLink.Body,
                wheelLink.Body,
                PhysicsConstraintMask.LockPositionX | PhysicsConstraintMask.LockPositionY | PhysicsConstraintMask.LockPositionZ
                    | PhysicsConstraintMask.LockRotationX | PhysicsConstraintMask.LockRotationY,
                breakForce: 0f,
                breakTorque: 0f,
                localAnchorA: parentPose.Rotation.Inverse.Rotate(axle - parentPose.Position),
                localAnchorB: wheelPose.Rotation.Inverse.Rotate(axle - wheelPose.Position),
                localAxisA: new PhysicsVector3(0f, 0f, 1f),
                localAxisB: new PhysicsVector3(0f, 0f, 1f),
                springFrequency: suspensionFrequency,
                springDampingRatio: suspensionDampingRatio,
                localSuspensionAxis: suspensionAxis,
                suspensionRestOffset: suspensionRestOffset);
            _wheelJoints.Add((_world.CreateJoint(definition), wheelLink.Body, parentLink.Body));
            // A hinged wheel's power belongs to the chassis's cluster: the original's power
            // component is the joint graph (Contraption.cs:1293 unions every m_jointMap entry),
            // while PigForge gives the wheel its own body (ADR-009).
            _rules.LinkPowerCluster(hinge.Wheel, hinge.Parent);
            // The wheel's mounts were handed to the parent body's compound at its own build
            // rotation, so their world frame is the parent's live rotation carried by that
            // local rotation — published as `AttachYaw` so the client draws the axle (and any
            // other non-spinning sprite) rigid to the chassis instead of freezing it.
            _attachByEntity[hinge.Wheel.Value] = (parentLink.Body, parentPose.Rotation.Inverse * transform.Rotation);
        }
    }

    /// <summary>
    /// Creates the compliant welds the assembler registered between frame pairs. The original keeps
    /// two frames as two bodies joined by a real six-degree-of-freedom joint
    /// (<c>Contraption.cs:1507-1546</c>) instead of merging them, which is why a frame chain bends
    /// under its own weight (docs/specs/weld-compliance.md); the pair keeps colliding exactly like
    /// the original's adjacent 1x1x1 frames. Binding is idempotent per pair, so a sandbox room can
    /// materialise several players' rigs through it.
    /// </summary>
    private void BindWeldJoints(IReadOnlyList<CompoundWeld> welds)
    {
        PruneDeadWelds();
        foreach (CompoundWeld weld in welds)
        {
            if (!_weldKeys.Add(PairKey(weld.Left.Value, weld.Right.Value)))
            {
                continue;
            }

            _welds.Add(weld);
            CreateWeldJoint(weld);
        }
    }

    /// <summary>
    /// Drops the weld definitions whose parts are gone. A player's reset removes its parts (bodies
    /// and joints with them) and re-places them under new ids, so keeping a stale definition would
    /// only leave an entry that no bind can ever use — and one the break path would keep re-linking
    /// onto a dead entity.
    /// </summary>
    private void PruneDeadWelds()
    {
        if (_welds.RemoveAll(weld => !_entities.IsAlive(weld.Left) || !_entities.IsAlive(weld.Right)) == 0)
        {
            return;
        }

        _weldKeys.Clear();
        foreach (CompoundWeld weld in _welds)
        {
            _weldKeys.Add(PairKey(weld.Left.Value, weld.Right.Value));
        }
    }

    /// <summary>
    /// Drops the weld definitions a player's reset invalidates. The reset destroys the bodies the
    /// joints were bound to but keeps the frames -- and their entity ids -- so the definitions
    /// survive <see cref="PruneDeadWelds"/> while they no longer have a joint. The next Start
    /// re-registers the frame pair from the assembler, and without this the idempotence key would
    /// swallow those fresh definitions and rebuild the chain unwelded.
    /// </summary>
    private void ForgetWeldsForEntities(IReadOnlyCollection<uint> entityValues)
    {
        if (_welds.Count == 0 || entityValues.Count == 0)
        {
            return;
        }

        HashSet<uint> owned = new(entityValues);
        if (_welds.RemoveAll(weld => owned.Contains(weld.Left.Value) || owned.Contains(weld.Right.Value)) == 0)
        {
            return;
        }

        _weldKeys.Clear();
        foreach (CompoundWeld weld in _welds)
        {
            _weldKeys.Add(PairKey(weld.Left.Value, weld.Right.Value));
        }
    }

    /// <summary>
    /// Binds one weld to the two bodies its frames sit in (a frame can share its body with the
    /// parts it encloses, and a rebuild can move it to a new one). The anchors are the original's,
    /// resolved into each body's frame, and the rest pose is the pair's <em>current</em> relative
    /// pose: at spawn that is the build pose, after a split it is wherever the pair got to, so a
    /// rebound weld never snaps a frame back to a pose it has left.
    /// </summary>
    private void CreateWeldJoint(CompoundWeld weld)
    {
        if (!_bodies.TryGet(weld.Left, out PhysicsBodyLink leftLink)
            || !_bodies.TryGet(weld.Right, out PhysicsBodyLink rightLink)
            || leftLink.Body == rightLink.Body
            || !_bodyPose.TryGetValue(leftLink.Body.Value, out (PhysicsVector3 Position, PhysicsQuaternion Rotation) leftPose)
            || !_bodyPose.TryGetValue(rightLink.Body.Value, out (PhysicsVector3 Position, PhysicsQuaternion Rotation) rightPose))
        {
            return;
        }

        JointDefinition definition = JointDefinition.Weld(
            leftLink.Body,
            rightLink.Body,
            localAnchorA: ToBodyLocal(weld.Left.Value, weld.AnchorInLeft),
            localAnchorB: ToBodyLocal(weld.Right.Value, weld.AnchorInRight),
            springFrequency: CompoundAssembler.FrameWeldSpringFrequency,
            springDampingRatio: CompoundAssembler.FrameWeldSpringDampingRatio,
            restRotation: leftPose.Rotation.Inverse * rightPose.Rotation);
        _weldJoints.Add((_world.CreateJoint(definition), leftLink.Body, rightLink.Body, weld));
        // The original's power component is the whole joint graph (Contraption.cs:1293 unions every
        // m_jointMap entry), so a weld is a power edge like any other: without it an engine enclosed
        // in one frame would not reach a consumer welded onto the next.
        _rules.LinkPowerCluster(weld.Right, weld.Left);
    }

    /// <summary>
    /// Recreates the welds a rebuild left unbound. A split destroys the compound's body and binds
    /// new ones; the backend drops every joint of a destroyed body, so the surviving pairs take
    /// their current relative pose as the new rest pose. Pairs with a destroyed end are left out
    /// (the assembler's entity may be long gone after a blast).
    /// </summary>
    private void RebindWeldJoints()
    {
        if (_welds.Count == 0)
        {
            return;
        }

        PruneDeadWelds();
        HashSet<long> bound = new();
        for (int index = _weldJoints.Count - 1; index >= 0; index--)
        {
            (_, PhysicsBodyId left, PhysicsBodyId right, CompoundWeld weld) = _weldJoints[index];
            if (!_entitiesByBody.ContainsKey(left.Value) || !_entitiesByBody.ContainsKey(right.Value))
            {
                _weldJoints.RemoveAt(index);
                continue;
            }

            bound.Add(PairKey(weld.Left.Value, weld.Right.Value));
        }

        foreach (CompoundWeld weld in _welds)
        {
            if (!bound.Contains(PairKey(weld.Left.Value, weld.Right.Value)))
            {
                CreateWeldJoint(weld);
            }
        }
    }

    /// <summary>
    /// Creates the soft distance links the assembler registered for the content's spring parts
    /// (docs/specs/spring-joint.md §3). The original's Spring never welds its neighbour: it holds it
    /// at the pair's assembly spacing with a spring (<c>Spring.cs:100-134</c>), so the pair is two
    /// bodies and one joint. Binding is idempotent per pair, like the frame welds.
    /// </summary>
    private void BindSpringJoints(IReadOnlyList<CompoundSpring> springs)
    {
        PruneDeadSprings();
        foreach (CompoundSpring spring in springs)
        {
            if (!_springKeys.Add(PairKey(spring.Left.Value, spring.Right.Value)))
            {
                continue;
            }

            _springs.Add(spring);
            CreateSpringJoint(spring);
        }
    }

    /// <summary>
    /// Drops the spring definitions whose parts are gone. A player's reset removes its parts (bodies
    /// and joints with them) and re-places them under new ids, so a stale definition would only
    /// leave an entry no bind can use (mirrors <see cref="PruneDeadWelds"/>).
    /// </summary>
    private void PruneDeadSprings()
    {
        if (_springs.RemoveAll(spring => !_entities.IsAlive(spring.Left) || !_entities.IsAlive(spring.Right)) == 0)
        {
            return;
        }

        _springKeys.Clear();
        foreach (CompoundSpring spring in _springs)
        {
            _springKeys.Add(PairKey(spring.Left.Value, spring.Right.Value));
        }
    }

    /// <summary>
    /// Drops the spring definitions a player's reset invalidates. The reset destroys the bodies the
    /// joints were bound to but keeps the placed parts and their entity ids, so the definitions
    /// survive <see cref="PruneDeadSprings"/> while they no longer have a joint; the next Start
    /// re-registers them, and without this the idempotence key would swallow the fresh pair
    /// (mirrors <see cref="ForgetWeldsForEntities"/>).
    /// </summary>
    private void ForgetSpringsForEntities(IReadOnlyCollection<uint> entityValues)
    {
        if (_springs.Count == 0 || entityValues.Count == 0)
        {
            return;
        }

        HashSet<uint> owned = new(entityValues);
        if (_springs.RemoveAll(spring => owned.Contains(spring.Left.Value) || owned.Contains(spring.Right.Value)) == 0)
        {
            return;
        }

        _springKeys.Clear();
        foreach (CompoundSpring spring in _springs)
        {
            _springKeys.Add(PairKey(spring.Left.Value, spring.Right.Value));
        }
    }

    /// <summary>
    /// Binds one spring seam between the two bodies its parts sit in (a spring part is a singleton
    /// cluster, but the enclosable path can still fold it into a frame's body). The link is the
    /// contract's slackless distance joint: the rest band is shut at the pair's own assembly anchor
    /// separation, and the rate is the declared content stiffness after the PigForge calibration
    /// (<see cref="CompoundAssembler.EffectiveStiffness"/>) converted by the shared
    /// <see cref="TrySpringResponse"/> over the two bodies' masses — the same conversion the wheel
    /// suspension and the runtime ropes use, never a second copy. The joint carries the extractor's
    /// declared break force and the 3 m pull check is the other break path; both hand the seam to an
    /// endpoint body (docs/specs/spring-joint.md §4).
    /// </summary>
    private void CreateSpringJoint(CompoundSpring spring)
    {
        if (!_bodies.TryGet(spring.Left, out PhysicsBodyLink leftLink)
            || !_bodies.TryGet(spring.Right, out PhysicsBodyLink rightLink)
            || leftLink.Body == rightLink.Body
            || !_bodyPose.TryGetValue(leftLink.Body.Value, out (PhysicsVector3 Position, PhysicsQuaternion Rotation) leftPose)
            || !_bodyPose.TryGetValue(rightLink.Body.Value, out (PhysicsVector3 Position, PhysicsQuaternion Rotation) rightPose))
        {
            return;
        }

        PhysicsVector3 anchorInLeft = ToBodyLocal(spring.Left.Value, spring.AnchorInLeft);
        PhysicsVector3 anchorInRight = ToBodyLocal(spring.Right.Value, spring.AnchorInRight);
        float restDistance = PhysicsVector3.Distance(
            leftPose.Position + leftPose.Rotation.Rotate(anchorInLeft),
            rightPose.Position + rightPose.Rotation.Rotate(anchorInRight));
        if (!(restDistance > 0f)
            || !TrySpringResponse(
                BodyMass(leftLink.Body),
                BodyMass(rightLink.Body),
                CompoundAssembler.EffectiveStiffness(spring),
                CompoundAssembler.EffectiveDamping(spring),
                out float frequency,
                out float dampingRatio))
        {
            return;
        }

        JointDefinition definition = new(
            PhysicsJointKind.Distance,
            leftLink.Body,
            rightLink.Body,
            PhysicsConstraintMask.None,
            // The extractor's published break force (250 N; profile B's StrongSpringConnection is
            // what doubles it to 1200, Spring.cs:38). The Bepu backend enforces it exactly as Unity's
            // SpringJoint does, so a spring that carries too much snaps and hands over to its
            // endpoint body; the 3 m pull below is the other break path (Spring.cs:78-92).
            breakForce: spring.BreakForce,
            breakTorque: 0f,
            localAnchorA: anchorInLeft,
            localAnchorB: anchorInRight,
            minimumDistance: restDistance,
            maximumDistance: restDistance,
            springFrequency: frequency,
            springDampingRatio: dampingRatio);
        EntityId host = SpringHostOf(spring);
        PhysicsJointId joint = _world.CreateJoint(definition);
        _springJoints.Add(new SpringJoint
        {
            Spring = spring,
            Host = host,
            Joint = joint,
            HostBody = host == spring.Left ? leftLink.Body : rightLink.Body,
            OtherBody = host == spring.Left ? rightLink.Body : leftLink.Body,
            AnchorInHostBody = host == spring.Left ? anchorInLeft : anchorInRight,
            AnchorInOtherBody = host == spring.Left ? anchorInRight : anchorInLeft,
        });
        // The original's power component is the whole joint graph (Contraption.cs:1293 unions every
        // m_jointMap entry), so a spring edge is a power edge like a weld's.
        _rules.LinkPowerCluster(spring.Right, spring.Left);
    }

    /// <summary>
    /// The end of a spring seam that owns the joint: the part carrying <c>capabilities.spring</c>
    /// (a spring pair is always a spring part next to a plain one). Falls back to <c>Left</c> for a
    /// seam two spring parts share.
    /// </summary>
    private EntityId SpringHostOf(in CompoundSpring spring) =>
        SpringCapabilityOf(spring.Left) is not null || SpringCapabilityOf(spring.Right) is null
            ? spring.Left
            : spring.Right;

    /// <summary>The spring capability of one placed part, or <c>null</c> when it has none.</summary>
    private PartSpring? SpringCapabilityOf(EntityId entity) =>
        _parts.TryGet(entity, out PartLink link) ? _content.GetPart(link.PartTypeId).Capabilities?.Spring : null;

    /// <summary>
    /// Recreates the spring links a rebuild left unbound. A split destroys a compound's body and
    /// binds new ones, and the backend drops every joint of a destroyed body, so the surviving pairs
    /// take their current relative pose as the new rest pose (mirrors <see cref="RebindWeldJoints"/>
    /// and ADR-024 decision 7).
    /// </summary>
    private void RebindSpringJoints()
    {
        if (_springs.Count == 0 && _subEntitiesByHost.Count == 0)
        {
            return;
        }

        PruneDeadSprings();
        HashSet<long> bound = new();
        for (int index = _springJoints.Count - 1; index >= 0; index--)
        {
            SpringJoint link = _springJoints[index];
            if (!_entitiesByBody.ContainsKey(link.HostBody.Value) || !_entitiesByBody.ContainsKey(link.OtherBody.Value))
            {
                _springJoints.RemoveAt(index);
                continue;
            }

            bound.Add(PairKey(link.Spring.Left.Value, link.Spring.Right.Value));
        }

        foreach (CompoundSpring spring in _springs)
        {
            if (!bound.Contains(PairKey(spring.Left.Value, spring.Right.Value)))
            {
                CreateSpringJoint(spring);
            }
        }

        RebindSubEntityJoints();
    }

    /// <summary>The pull break of the original's <c>FixedUpdate</c> (<c>Spring.cs:78-92</c>): every
    /// tick it measures the two anchor points, and once they are more than 3 m apart and the
    /// contraption carries no SuperGlue it tears the joint down and spawns the endpoint rigid body to
    /// continue the link. Springs are few, so the loop is direct and reads the tick's own snapshots
    /// (a link whose bodies were rebound this tick is simply skipped — the next tick sees them).</summary>
    private void BreakSpringsFromPull(int snapshotCount)
    {
        for (int index = _springJoints.Count - 1; index >= 0; index--)
        {
            SpringJoint link = _springJoints[index];
            if (!TryFindSnapshot(link.HostBody, snapshotCount, out PhysicsBodySnapshot hostSnapshot)
                || !TryFindSnapshot(link.OtherBody, snapshotCount, out PhysicsBodySnapshot otherSnapshot))
            {
                continue;
            }

            PhysicsVector3 hostAnchor = hostSnapshot.Position + hostSnapshot.Rotation.Rotate(link.AnchorInHostBody);
            PhysicsVector3 otherAnchor = otherSnapshot.Position + otherSnapshot.Rotation.Rotate(link.AnchorInOtherBody);
            if (PhysicsVector3.Distance(hostAnchor, otherAnchor) <= CompoundAssembler.SpringBreakDistance
                || IsGluedCompound(link.HostBody)
                || IsGluedCompound(link.OtherBody))
            {
                continue;
            }

            _world.DestroyJoint(link.Joint);
            BreakSpringLink(index);
        }
    }

    /// <summary>
    /// Takes over the spring links the backend tore down on their declared break force
    /// (<see cref="CompoundSpring.BreakForce"/>): the joint is already gone, so this only drops the
    /// seam and spawns the endpoint rigid body, exactly as the original's <c>OnJointBreak</c> does
    /// (<c>Spring.cs:60-67,131-176</c>).
    /// </summary>
    private void HandleBrokenSprings(int eventCount)
    {
        for (int index = 0; index < eventCount; index++)
        {
            PhysicsEvent physicsEvent = _eventBuffer[index];
            if (physicsEvent.Kind != PhysicsEventKind.JointBroken)
            {
                continue;
            }

            int linkIndex = _springJoints.FindIndex(link => link.Joint == physicsEvent.Joint);
            if (linkIndex < 0)
            {
                continue;
            }

            BreakSpringLink(linkIndex);
        }
    }

    /// <summary>Drops the spring seam at <paramref name="index"/> and hands it over to an endpoint
    /// body. The joint itself is destroyed by the caller (the pull path) or already gone (the
    /// backend's force break).</summary>
    private void BreakSpringLink(int index)
    {
        SpringJoint link = _springJoints[index];
        _springJoints.RemoveAt(index);
        _springs.Remove(link.Spring);
        _springKeys.Remove(PairKey(link.Spring.Left.Value, link.Spring.Right.Value));
        _rules.UnlinkPowerCluster(link.Spring.Right);
        // The links that remain are the component's other edges: re-resolving them in one
        // deterministic pass hands every member downstream of the broken edge the key that is left,
        // exactly as BreakWeldsFromAppliedCommands does for the weld graph.
        for (int remaining = 0; remaining < _springJoints.Count; remaining++)
        {
            _rules.LinkPowerCluster(_springJoints[remaining].Spring.Right, _springJoints[remaining].Spring.Left);
        }

        SpawnSpringEndpoint(link);
    }

    /// <summary>
    /// Spawns the runtime sub-entity that continues a broken spring: the original instantiates
    /// <c>SpringEndpoint.prefab</c> at the spring part's pose and hangs it off the same spring joint
    /// (<c>Spring.cs:131-176</c>). Its geometry and mass are the prefab's own (a 0.7 x 0.3 x 1 box at
    /// (0, -0.4, 0), mass 1, the project's 0/0.05 damping), and it is a plain sub-entity per ADR-027:
    /// an entity id, a body, the host's part type on the wire, and no construction footprint.
    /// </summary>
    private void SpawnSpringEndpoint(SpringJoint link)
    {
        if (!_bodyPose.TryGetValue(link.HostBody.Value, out (PhysicsVector3 Position, PhysicsQuaternion Rotation) hostPose)
            || !_parts.TryGet(link.Host, out PartLink hostPart))
        {
            return;
        }

        BodyDefinition definition = new(
            PhysicsBodyMode.Dynamic,
            hostPose.Position,
            hostPose.Rotation,
            SpringEndpointMass,
            new ShapeDefinition[]
            {
                new CompoundShapeDefinition(new[]
                {
                    new CompoundChild(new BoxShapeDefinition(0.35f, 0.15f, 0.5f), new PhysicsVector3(0f, -0.4f, 0f)),
                }),
            },
            material: new PhysicsMaterial(0f, 0.6f, FrictionCombine.Average),
            constraints: PlanarConstraintMask,
            linearDamping: 0f,
            angularDamping: 0.05f,
            maximumAngularSpeed: _content.MaximumAngularSpeed);
        AttachSpringEndpoint(CreateSubEntity(link.Host, hostPart.PartTypeId, definition, link.Spring));
    }

    /// <summary>
    /// Hangs a sub-entity off its host's body with the same soft distance link the broken spring was
    /// (the original wires the endpoint with its own spring joint, <c>Spring.cs:139-176</c>). The
    /// rest length is the separation the pair already has, so the link never yanks the endpoint.
    /// </summary>
    private void AttachSpringEndpoint(SubEntity sub)
    {
        if (sub.Spring is not CompoundSpring spring
            || !_bodies.TryGet(sub.Host, out PhysicsBodyLink hostLink)
            || !_bodyPose.TryGetValue(hostLink.Body.Value, out (PhysicsVector3 Position, PhysicsQuaternion Rotation) hostPose)
            || !_bodyPose.TryGetValue(sub.Body.Value, out (PhysicsVector3 Position, PhysicsQuaternion Rotation) endpointPose))
        {
            return;
        }

        PhysicsVector3 hostAnchorLocal = ToBodyLocal(
            sub.Host.Value,
            spring.Left == sub.Host ? spring.AnchorInLeft : spring.AnchorInRight);
        PhysicsVector3 hostAnchor = hostPose.Position + hostPose.Rotation.Rotate(hostAnchorLocal);
        float restDistance = PhysicsVector3.Distance(hostAnchor, endpointPose.Position);
        if (!(restDistance > 0f)
            || !TrySpringResponse(
                BodyMass(hostLink.Body),
                SpringEndpointMass,
                CompoundAssembler.EffectiveStiffness(spring),
                CompoundAssembler.EffectiveDamping(spring),
                out float frequency,
                out float dampingRatio))
        {
            return;
        }

        JointDefinition definition = new(
            PhysicsJointKind.Distance,
            hostLink.Body,
            sub.Body,
            PhysicsConstraintMask.None,
            breakForce: 0f,
            breakTorque: 0f,
            localAnchorA: hostAnchorLocal,
            localAnchorB: PhysicsVector3.Zero,
            minimumDistance: restDistance,
            maximumDistance: restDistance,
            springFrequency: frequency,
            springDampingRatio: dampingRatio);
        sub.Joint = _world.CreateJoint(definition);
        sub.JointHostBody = hostLink.Body;
    }

    /// <summary>
    /// Recreates the links a rebuild dropped (a split destroys the host's body and rebinds it, so
    /// every sub-entity joint has to follow). Each sub-entity gets its own consumer's joint: the
    /// glove's linear drive at the phase it is in, or the endpoint's soft distance link. A
    /// sub-entity whose host has no body any more keeps its own body and hangs free, exactly as the
    /// original's endpoint would once its spring is gone.
    /// </summary>
    private void RebindSubEntityJoints()
    {
        foreach (List<SubEntity> subEntities in _subEntitiesByHost.Values)
        {
            foreach (SubEntity sub in subEntities)
            {
                if (sub.Joint.IsValid && _entitiesByBody.ContainsKey(sub.JointHostBody.Value))
                {
                    continue;
                }

                if (sub.Glove is GloveLink glove)
                {
                    AttachGlove(sub, glove);
                }
                else
                {
                    AttachSpringEndpoint(sub);
                }
            }
        }
    }

    /// <summary>
    /// Finds the glove's neighbour in the effect direction — the original's
    /// <c>EffectDirection() = Rotate(Down, gridRotation)</c>, i.e. the part's own -Y, the same
    /// direction the glove is thrown, which the probe pins down (§1.1). Candidates are the members
    /// of the glove's own body plus the parts welded to them: PigForge keeps one connected
    /// component either merged into a single body or joined by weld joints, while the original's
    /// check is "same ConnectedComponent". The nearest candidate in the half-space is picked, ties
    /// by entity id.
    /// </summary>
    private bool TryFindEffectNeighbour(GloveLink link, int snapshotCount, out uint neighbourValue)
    {
        neighbourValue = 0;
        if (!_bodies.TryGet(link.Sub.Host, out PhysicsBodyLink hostLink))
        {
            return false;
        }

        PhysicsBodyId hostBody = hostLink.Body;
        _effectCandidates.Clear();
        if (_entitiesByBody.TryGetValue(hostBody.Value, out List<uint>? members))
        {
            _effectCandidates.AddRange(members);
        }

        // One weld hop reaches the nearest part across a frame pair's seam; further hops are
        // farther away than the candidate they would add.
        for (int index = 0; index < _welds.Count; index++)
        {
            CompoundWeld weld = _welds[index];
            if (_effectCandidates.Contains(weld.Left.Value))
            {
                AddEffectCandidate(weld.Right.Value);
            }
            else if (_effectCandidates.Contains(weld.Right.Value))
            {
                AddEffectCandidate(weld.Left.Value);
            }
        }

        (PhysicsVector3 hostLocal, PhysicsQuaternion localRotation) = PartLocalInBody(link.Sub.Host.Value);
        PhysicsVector3 axis = localRotation.Rotate(new PhysicsVector3(0f, -1f, 0f));
        float bestDistance = float.PositiveInfinity;
        bool found = false;
        for (int index = 0; index < _effectCandidates.Count; index++)
        {
            uint candidate = _effectCandidates[index];
            if (candidate == link.Sub.Host.Value
                || !TryPositionInBody(candidate, hostBody, snapshotCount, out PhysicsVector3 position))
            {
                continue;
            }

            PhysicsVector3 displacement = position - hostLocal;
            if (PhysicsVector3.Dot(displacement, axis) <= 0f)
            {
                continue;
            }

            float distance = PhysicsVector3.Distance(displacement, PhysicsVector3.Zero);
            if (distance > 0f
                && (distance < bestDistance
                    || (distance == bestDistance && found && candidate < neighbourValue)))
            {
                bestDistance = distance;
                neighbourValue = candidate;
                found = true;
            }
        }

        return found;
    }

    /// <summary>Adds a candidate once; the list is one contraption's members, so the linear probe
    /// is honest and keeps the tick allocation-free.</summary>
    private void AddEffectCandidate(uint candidate)
    {
        for (int index = 0; index < _effectCandidates.Count; index++)
        {
            if (_effectCandidates[index] == candidate)
            {
                return;
            }
        }

        _effectCandidates.Add(candidate);
    }

    /// <summary>An entity's own frame in another body's local space, from this tick's snapshots.</summary>
    private bool TryPositionInBody(uint entityValue, PhysicsBodyId hostBody, int snapshotCount, out PhysicsVector3 localPosition)
    {
        localPosition = PhysicsVector3.Zero;
        if (!_bodyByEntity.TryGetValue(entityValue, out PhysicsBodyId body)
            || !TryFindSnapshot(body, snapshotCount, out PhysicsBodySnapshot bodySnapshot)
            || !TryFindSnapshot(hostBody, snapshotCount, out PhysicsBodySnapshot hostSnapshot))
        {
            return false;
        }

        (PhysicsVector3 offset, _) = PartLocalInBody(entityValue);
        PhysicsVector3 world = bodySnapshot.Position + bodySnapshot.Rotation.Rotate(offset);
        localPosition = hostSnapshot.Rotation.Inverse.Rotate(world - hostSnapshot.Position);
        return true;
    }

    /// <summary>The throw's second half: the part behind the glove leaves the contraption
    /// (<c>SpringBoxingGlove.cs:224-262</c>). A part merged into the glove's body loses every seam
    /// it has, and a part joined by weld joints loses those.</summary>
    private void BreakEffectNeighbours(GloveLink link, int snapshotCount)
    {
        if (!TryFindEffectNeighbour(link, snapshotCount, out uint neighbourValue)
            || !_bodyByEntity.TryGetValue(neighbourValue, out PhysicsBodyId neighbourBody))
        {
            return;
        }

        if (_bodies.TryGet(link.Sub.Host, out PhysicsBodyLink hostLink) && neighbourBody == hostLink.Body)
        {
            SplitMemberFromBody(new EntityId(neighbourValue), hostLink.Body, snapshotCount);
            return;
        }

        BreakWeldsTouching(new EntityId(neighbourValue));
    }

    /// <summary>
    /// Severs one member's every seam and rebuilds the pieces — what "destroy all of the target's
    /// FixedJoints" means for a body PigForge merged several parts into
    /// (<c>SpringBoxingGlove.cs:224-262</c>).
    /// </summary>
    private void SplitMemberFromBody(EntityId member, PhysicsBodyId body, int snapshotCount)
    {
        int liveIndex = _liveCompounds.FindIndex(candidate => candidate.Body == body);
        if (liveIndex < 0 || !TryFindSnapshot(body, snapshotCount, out PhysicsBodySnapshot snapshot))
        {
            return;
        }

        LiveCompound live = _liveCompounds[liveIndex];
        live.Cluster.WorldPosition = snapshot.Position;
        live.Cluster.WorldRotation = snapshot.Rotation;
        CompoundCluster? pruned = PruneDeadMembers(live.Cluster);
        if (pruned is null)
        {
            return;
        }

        live.Cluster = pruned;
        _doomedSeams.Clear();
        foreach (CompoundSeam seam in live.Cluster.Seams)
        {
            if (seam.Left == member || seam.Right == member)
            {
                _doomedSeams.Add(seam);
            }
        }

        if (_doomedSeams.Count == 0)
        {
            return;
        }

        IReadOnlyList<CompoundCluster> pieces = CompoundAssembler.SplitAlongSeams(live.Cluster, _doomedSeams);
        if (pieces.Count == 1)
        {
            live.Cluster = pieces[0];
            return;
        }

        RebuildSplitBody(body, snapshot, pieces);
    }

    /// <summary>
    /// Destroys every weld joint a part carries and forgets the definitions, so the part leaves the
    /// contraption and no later rebind puts it back (the original destroys the target's FixedJoints;
    /// a weld is PigForge's frame-pair joint).
    /// </summary>
    private void BreakWeldsTouching(EntityId entity)
    {
        bool broke = false;
        for (int index = _weldJoints.Count - 1; index >= 0; index--)
        {
            (PhysicsJointId joint, _, _, CompoundWeld weld) = _weldJoints[index];
            if (weld.Left != entity && weld.Right != entity)
            {
                continue;
            }

            _world.DestroyJoint(joint);
            _weldJoints.RemoveAt(index);
            _rules.UnlinkPowerCluster(weld.Right);
            broke = true;
        }

        for (int index = _welds.Count - 1; index >= 0; index--)
        {
            CompoundWeld weld = _welds[index];
            if (weld.Left != entity && weld.Right != entity)
            {
                continue;
            }

            _welds.RemoveAt(index);
            _weldKeys.Remove(PairKey(weld.Left.Value, weld.Right.Value));
        }

        if (!broke)
        {
            return;
        }

        // The welds that remain are the component's other edges: re-resolving them hands every
        // member downstream the key the broken edge left (Contraption.cs:1293).
        for (int index = 0; index < _weldJoints.Count; index++)
        {
            (_, _, _, CompoundWeld remaining) = _weldJoints[index];
            _rules.LinkPowerCluster(remaining.Right, remaining.Left);
        }
    }

    /// <summary>
    /// Replaces one live compound's body with the bodies a split produced — the ADR-023/024 order
    /// once: unbind the members, forget the body's joints, destroy it, rebind the pieces and
    /// re-link the joints that followed them.
    /// </summary>
    private void RebuildSplitBody(PhysicsBodyId body, PhysicsBodySnapshot snapshot, IReadOnlyList<CompoundCluster> pieces)
    {
        List<uint> members = _entitiesByBody.TryGetValue(body.Value, out List<uint>? bound)
            ? new List<uint>(bound)
            : new List<uint>();
        foreach (uint entityValue in members)
        {
            UnbindEntity(new EntityId(entityValue), destroyBodyIfOrphan: false);
        }

        ForgetJointsForBody(body);
        _world.DestroyBody(body);
        DropPendingCommands(body);
        foreach (CompoundCluster piece in pieces)
        {
            BindCluster(piece, piece.CreateBodyDefinition(_content, _construction, snapshot.LinearVelocity, snapshot.AngularVelocity, PlanarConstraintMask));
        }

        RebindWeldJoints();
        RebindSpringJoints();
        EnsureBuffers();
    }

    /// <summary>
    /// One tick of every glove's state machine (docs/specs/boxing-glove.md §4/§5): the trigger
    /// path consumes this tick's button press (the original's <c>ProcessTouch</c>, which its bar
    /// button, its keyboard shortcut and its mouse all reach — a physics contact never does), the
    /// toggle path reads the switch level and fires on its off→on edge, and the timers — plus the
    /// glove's own offset — move the phase along. A phase change rewrites the drive, the mass and
    /// the collider, and a throw also severs the part behind it.
    /// </summary>
    private void RunGloves(int snapshotCount)
    {
        if (_gloveHosts.Count == 0)
        {
            return;
        }

        for (int index = 0; index < _gloveHosts.Count; index++)
        {
            if (!_glovesByHost.TryGetValue(_gloveHosts[index], out GloveLink? link)
                || !_entities.IsAlive(link.Sub.Entity)
                || !_bodyPose.ContainsKey(link.Sub.Body.Value))
            {
                continue;
            }

            bool changed = false;
            if (link.Activation == PartActivation.Toggle)
            {
                bool active = _rules.IsPartActive(link.Sub.Host);
                if (active != link.SwitchWasActive)
                {
                    link.SwitchWasActive = active;
                    changed = active
                        ? Punch(link, snapshotCount)
                        : Transition(link, BoxingGloveRules.Abort(link.State));
                }
            }
            else if (_rules.TryConsumeButtonPress(link.Sub.Host))
            {
                // The bar button (docs/specs/play-part-switches.md Assumption 2): momentary, spent
                // here even when the punch itself is refused, so the button never latches on.
                changed = Punch(link, snapshotCount);
            }

            if (changed)
            {
                ApplyGloveState(link);
            }

            if (link.State.Phase != BoxingGlovePhase.WindedUp)
            {
                BoxingGloveState state = link.State;
                bool woundUp = BoxingGloveRules.Tick(ref state, link.Glove, _timeStep.Seconds, GloveOffset(link, snapshotCount));
                link.State = state;
                if (woundUp)
                {
                    ApplyGloveState(link);
                }
            }
        }
    }

    /// <summary>
    /// The glove's throw, if the original would allow it: only from rest, and never while the part
    /// behind it is glued (unless that part is the timebomb the original's own check excepts). A
    /// refused throw leaves the glove resting with the switch as it is, exactly as the original's
    /// <c>m_CanBeEnabled</c> guard does.
    /// </summary>
    private bool Punch(GloveLink link, int snapshotCount)
    {
        if (link.State.Phase != BoxingGlovePhase.WindedUp || !CanPunch(link, snapshotCount))
        {
            return false;
        }

        bool changed = Transition(link, BoxingGloveRules.Trigger(link.State));
        if (changed)
        {
            // The throw severs the part behind the glove (SpringBoxingGlove.cs:224-262).
            BreakEffectNeighbours(link, snapshotCount);
        }

        return changed;
    }

    /// <summary>The original's <c>m_CanBeEnabled</c> over this rig's own layout.</summary>
    private bool CanPunch(GloveLink link, int snapshotCount)
    {
        if (!TryFindEffectNeighbour(link, snapshotCount, out uint neighbourValue))
        {
            return true;
        }

        EntityId neighbour = new(neighbourValue);
        bool glued = _rules.HasGlue(neighbour);
        bool tnt = _parts.TryGet(neighbour, out PartLink part)
            && _content.GetPart(part.PartTypeId).Capabilities?.TntFuseTicks is not null;
        return BoxingGloveRules.CanBeEnabled(glued, tnt);
    }

    /// <summary>Moves the machine to <paramref name="next"/>, reporting whether the phase changed.</summary>
    private static bool Transition(GloveLink link, BoxingGloveState next)
    {
        if (next.Phase == link.State.Phase)
        {
            return false;
        }

        link.State = next;
        return true;
    }

    /// <summary>
    /// Writes one phase's drive onto the world: the glove body's mass (the wind-back drops it to
    /// the skin's limp value), its collider (off while limp) and the joint. The joint is rebuilt
    /// rather than retargeted because every part of its definition changes with the phase — target
    /// offset, drive spring and limit spring — and a servo's target is not a mutable property of
    /// the contract.
    /// </summary>
    private void ApplyGloveState(GloveLink link)
    {
        BoxingGloveDrive drive = BoxingGloveRules.DriveFor(link.State.Phase, link.Glove);
        // The fist is the original's glove GameObject: wound up it is deactivated (nothing drawn,
        // nothing simulated), out or winding back it is on screen
        // (`InitilizeBoxingGlove` -> `m_BoxingGlove.SetActive(false)`, SpringBoxingGlove.cs:215-222;
        // the throw turns it on, :224-262).
        SetSubEntityStowed(link.Sub, link.State.Phase == BoxingGlovePhase.WindedUp);
        if (_bodyPose.ContainsKey(link.Sub.Body.Value))
        {
            _world.SetBodyMass(link.Sub.Body, drive.Mass);
            _world.SetBodyCollisionEnabled(link.Sub.Body, drive.ColliderEnabled);
        }

        if (link.Sub.Joint.IsValid)
        {
            _world.DestroyJoint(link.Sub.Joint);
            link.Sub.Joint = default;
        }

        AttachGlove(link.Sub, link);
    }


    /// <summary>
    /// Registers every placed part's boxing glove: the second rigidbody the original instantiates
    /// (<c>SpringBoxingGlove.cs:160-222</c>), a runtime sub-entity (ADR-027) held to its host by the
    /// driven joint §3 of docs/specs/boxing-glove.md describes. Hosts are taken in ascending entity
    /// order, so the sub-entity ids and their joints are created deterministically.
    /// </summary>
    private void BindGloves()
    {
        // Runtime sub-entities carry their host's part type on the wire, so a glove's own
        // sub-entity looks like another glove; only parts the construction or the level owns get
        // one. The dictionary is empty at materialisation, so this is a plain guard.
        HashSet<uint> runtimeEntities = new();
        foreach (List<SubEntity> subEntities in _subEntitiesByHost.Values)
        {
            foreach (SubEntity sub in subEntities)
            {
                runtimeEntities.Add(sub.Entity.Value);
            }
        }

        List<uint> hosts = new();
        var parts = _parts.GetEnumerator();
        while (parts.MoveNext())
        {
            if (!runtimeEntities.Contains(parts.CurrentId.Value))
            {
                hosts.Add(parts.CurrentId.Value);
            }
        }

        hosts.Sort();
        foreach (uint hostValue in hosts)
        {
            EntityId host = new(hostValue);
            if (_parts.TryGet(host, out PartLink link)
                && _content.GetPart(link.PartTypeId).Capabilities?.Glove is PartGlove glove)
            {
                SpawnGlove(host, link.PartTypeId, glove);
            }
        }
    }

    /// <summary>
    /// Creates the glove body at its host's own pose — the original instantiates the prefab there,
    /// inside the part, which is exactly why it then calls <c>IgnoreCollision</c> — and hangs it off
    /// the host with the wound-up drive.
    /// </summary>
    private void SpawnGlove(EntityId host, uint partTypeId, PartGlove glove)
    {
        if (!_bodies.TryGet(host, out PhysicsBodyLink hostLink)
            || !_bodyPose.TryGetValue(hostLink.Body.Value, out (PhysicsVector3 Position, PhysicsQuaternion Rotation) hostPose)
            || !_transforms.TryGet(host, out EntityTransform hostTransform))
        {
            return;
        }

        (PhysicsVector3 localOffset, PhysicsQuaternion localRotation) = PartLocalInBody(host.Value);
        PhysicsVector3 position = hostPose.Position + hostPose.Rotation.Rotate(localOffset);
        PhysicsQuaternion rotation = hostPose.Rotation * localRotation;
        PartDefinition hostPart = _content.GetPart(partTypeId);
        // The glove prefab's own colliders (a 0.3 m sphere on every shipped skin) at the part's
        // scale. The prefab's PhysicMaterial and rigidbody drag are not extracted, so the glove
        // carries the host's — it belongs to that part.
        PartContentLibrary.ShapePlacement[] placements = _content.PlaceShapes(partTypeId, hostTransform.Scale, glove.Shapes);
        ShapeDefinition[] shapes = placements.Length == 1 && placements[0].Offset == PhysicsVector3.Zero
            ? new[] { placements[0].Shape }
            : new ShapeDefinition[]
            {
                new CompoundShapeDefinition(Array.ConvertAll(
                    placements,
                    placement => new CompoundChild(placement.Shape, placement.Offset))),
            };
        PartDamping damping = _content.DampingOf(hostPart);
        BodyDefinition definition = new(
            PhysicsBodyMode.Dynamic,
            position,
            rotation,
            glove.Mass,
            shapes,
            material: new PhysicsMaterial(hostPart.Restitution, hostPart.Friction, hostPart.FrictionCombine),
            constraints: PlanarConstraintMask,
            linearDamping: damping.Linear,
            angularDamping: damping.Angular,
            maximumAngularSpeed: _content.MaximumAngularSpeed);

        SubEntity sub = CreateSubEntity(host, partTypeId, definition, spring: null);
        GloveLink link = new()
        {
            Sub = sub,
            Glove = glove,
            Activation = hostPart.Capabilities?.Activation ?? PartActivation.None,
            State = new BoxingGloveState { Phase = BoxingGlovePhase.WindedUp, Age = 0f },
            // The toggle is read as a level and only a fresh off→on edge throws a punch, so a
            // switch that was already on when the run started does not fire by itself (the
            // original punches on OnTouch, never from Update).
            SwitchWasActive = _rules.IsPartActive(host),
        };
        sub.Glove = link;
        // Spawned wound up: the original leaves the glove GameObject inactive until the throw.
        SetSubEntityStowed(sub, stowed: true);
        _glovesByHost[host.Value] = link;
        _gloveHosts.Add(host.Value);
        AttachGlove(sub, link);
    }

    /// <summary>
    /// Builds the host↔glove joint the current state asks for: the driven (y) axis with its spring
    /// and target offset, the lateral (x) axis with the skin's xDrive, a rigidly locked third axis,
    /// the skin's linear limit and the locked relative rotation — the original's ConfigurableJoint
    /// (<c>SpringBoxingGlove.cs:170-207</c>) as a <see cref="PhysicsJointKind.Configurable"/>
    /// definition. The content's N/m become solver frequencies through the shared
    /// <see cref="TrySpringResponse"/> over the two bodies' masses.
    /// </summary>
    private void AttachGlove(SubEntity sub, GloveLink link)
    {
        if (!_bodies.TryGet(sub.Host, out PhysicsBodyLink hostLink)
            || !_bodyPose.ContainsKey(hostLink.Body.Value))
        {
            return;
        }

        BoxingGloveDrive drive = BoxingGloveRules.DriveFor(link.State.Phase, link.Glove);
        (PhysicsVector3 localOffset, PhysicsQuaternion localRotation) = PartLocalInBody(sub.Host.Value);
        // The throw runs down the part's own -Y (probe §1.1: the original's targetPosition counts
        // it the other way round, which is why its glove ends up at a negative local y); the
        // lateral axis is the part's own X.
        PhysicsVector3 driveAxis = localRotation.Rotate(new PhysicsVector3(0f, -1f, 0f));
        PhysicsVector3 lateralAxis = localRotation.Rotate(new PhysicsVector3(1f, 0f, 0f));
        float hostMass = BodyMass(hostLink.Body);
        if (!TrySpringResponse(hostMass, drive.Mass, drive.DriveSpring, drive.DriveDamper, out float driveFrequency, out float driveDampingRatio)
            || !TrySpringResponse(hostMass, drive.Mass, link.Glove.XDrive.Spring, link.Glove.XDrive.Damper, out float lateralFrequency, out float lateralDampingRatio))
        {
            return;
        }

        // The original's linearLimitSpring is 0/0 at rest (a hard limit) and the skin's own, nearly
        // slack spring while thrown; a zero frequency is the contract's "hard" (the backend uses its
        // rigid stand-in).
        float limitFrequency = 0f;
        float limitDampingRatio = 0f;
        if (drive.LimitSpring > 0f
            && TrySpringResponse(hostMass, drive.Mass, drive.LimitSpring, 0f, out float limit, out float limitDamping))
        {
            limitFrequency = limit;
            limitDampingRatio = limitDamping;
        }

        ConfigurableJointDefinition payload = new(
            DriveAxisInA: driveAxis,
            DriveTargetOffset: drive.TargetOffset,
            DriveFrequency: driveFrequency,
            DriveDampingRatio: driveDampingRatio,
            LateralAxisInA: lateralAxis,
            LateralTargetOffset: drive.LateralOffset,
            LateralFrequency: lateralFrequency,
            LateralDampingRatio: lateralDampingRatio,
            LimitMinimumOffset: -link.Glove.Limit,
            LimitMaximumOffset: link.Glove.Limit,
            LimitFrequency: limitFrequency,
            LimitDampingRatio: limitDampingRatio);
        JointDefinition definition = new(
            PhysicsJointKind.Configurable,
            hostLink.Body,
            sub.Body,
            PhysicsConstraintMask.None,
            breakForce: 0f,
            breakTorque: 0f,
            localAnchorA: localOffset,
            localAnchorB: PhysicsVector3.Zero,
            restRotation: localRotation,
            configurable: payload);
        sub.Joint = _world.CreateJoint(definition);
        sub.JointHostBody = hostLink.Body;
    }

    /// <summary>Where a construction entity's own frame sits inside its body: the compound's local
    /// placement, or the body frame itself for an entity that does not share one.</summary>
    private (PhysicsVector3 Offset, PhysicsQuaternion Rotation) PartLocalInBody(uint entityValue) =>
        _compoundLocalByEntity.TryGetValue(entityValue, out (PhysicsVector3 Offset, PhysicsQuaternion Rotation) local)
            ? local
            : (PhysicsVector3.Zero, PhysicsQuaternion.Identity);

    /// <summary>
    /// The glove's own offset along its throw axis in metres, positive away from the part — the
    /// number the wind-back's "or home within 0.1 m" test reads. Measured from this tick's own
    /// snapshots, so it is what the solver did rather than a re-derivation.
    /// </summary>
    private float GloveOffset(GloveLink link, int snapshotCount)
    {
        if (!_bodies.TryGet(link.Sub.Host, out PhysicsBodyLink hostLink)
            || !TryFindSnapshot(hostLink.Body, snapshotCount, out PhysicsBodySnapshot hostSnapshot)
            || !TryFindSnapshot(link.Sub.Body, snapshotCount, out PhysicsBodySnapshot gloveSnapshot))
        {
            return 0f;
        }

        (PhysicsVector3 localOffset, PhysicsQuaternion localRotation) = PartLocalInBody(link.Sub.Host.Value);
        PhysicsVector3 anchor = hostSnapshot.Position + hostSnapshot.Rotation.Rotate(localOffset);
        PhysicsVector3 axis = hostSnapshot.Rotation.Rotate(localRotation.Rotate(new PhysicsVector3(0f, -1f, 0f)));
        return PhysicsVector3.Dot(gloveSnapshot.Position - anchor, axis);
    }

    /// <summary>
    /// Breaks the frame welds an applied impulse exceeded, the same rule the seams follow: the
    /// impulse must clear the threshold and land nearest a weld the body carries
    /// (docs/specs/weld-compliance.md decision 4). Both frames are already independent bodies, so
    /// the break only drops the joint — and with it the power edge the joint graph carried, exactly
    /// as the original re-unions the component when a joint goes away (Contraption.cs:1293).
    /// </summary>
    private void BreakWeldsFromAppliedCommands(int snapshotCount)
    {
        for (int index = 0; index < _appliedCommands.Count && _weldJoints.Count > 0; index++)
        {
            PhysicsCommand command = _appliedCommands[index];
            float magnitude = PhysicsVector3.Distance(command.Impulse, PhysicsVector3.Zero);
            if (magnitude <= _seamBreakImpulse)
            {
                continue;
            }

            int nearest = -1;
            float nearestDistance = float.PositiveInfinity;
            for (int weldIndex = 0; weldIndex < _weldJoints.Count; weldIndex++)
            {
                (_, PhysicsBodyId left, PhysicsBodyId right, CompoundWeld weld) = _weldJoints[weldIndex];
                if ((left != command.Body && right != command.Body)
                    || magnitude <= weld.BreakImpulse
                    || !TryFindSnapshot(left, snapshotCount, out PhysicsBodySnapshot leftSnapshot)
                    || !TryFindSnapshot(right, snapshotCount, out PhysicsBodySnapshot rightSnapshot))
                {
                    continue;
                }

                float distance = PhysicsVector3.Distance(
                    (leftSnapshot.Position + rightSnapshot.Position) * 0.5f,
                    command.WorldPoint);
                if (distance < nearestDistance)
                {
                    nearestDistance = distance;
                    nearest = weldIndex;
                }
            }

            if (nearest < 0)
            {
                continue;
            }

            (PhysicsJointId joint, _, _, CompoundWeld broken) = _weldJoints[nearest];
            _world.DestroyJoint(joint);
            _weldJoints.RemoveAt(nearest);
            _welds.Remove(broken);
            _weldKeys.Remove(PairKey(broken.Left.Value, broken.Right.Value));
            _rules.UnlinkPowerCluster(broken.Right);
            // The welds that remain are the component's other edges: re-resolving them in one
            // deterministic pass hands every member downstream of the broken edge the key that is
            // left, instead of the one the broken edge gave it (Contraption.cs:1293 re-unions the
            // joint graph on every change).
            for (int weldIndex = 0; weldIndex < _weldJoints.Count; weldIndex++)
            {
                (_, _, _, CompoundWeld remaining) = _weldJoints[weldIndex];
                _rules.LinkPowerCluster(remaining.Right, remaining.Left);
            }
        }
    }

    /// <summary>
    /// SpringJoint numbers of the original's runtime attachments, in Unity's units:
    /// <c>minDistance 0</c>, <c>spring 100</c>, <c>damper 10</c>, <c>enablePreprocessing false</c>
    /// (Sandbag.cs:157-163, Balloon.cs:146-160). The two solvers take the frequency/
    /// damping-ratio pair instead, converted per joint from the participants' reduced mass in
    /// <see cref="BindAttachments"/>; preprocessing has no PigForge equivalent.
    /// </summary>
    private const float AttachmentSpring = 100f;

    private const float AttachmentDamper = 10f;

    /// <summary>
    /// Unity's spring (N/m) and damper (N*s/m) in the solver's frequency/damping-ratio form:
    /// <c>omega = sqrt(k / m)</c> and <c>zeta = c / (2 sqrt(k m))</c>, with the pair's reduced
    /// mass <c>m = mA * mB / (mA + mB)</c>. Every extracted Unity spring goes through this one
    /// conversion — the runtime attachments (ADR-011) and the elastic wheel (ADR-012) alike —
    /// so the N/m an extractor reports is the stiffness the solver actually applies, for the
    /// masses it actually joins. Returns false when no usable spring exists (zero masses).
    /// </summary>
    internal static bool TrySpringResponse(
        float massA,
        float massB,
        float spring,
        float damper,
        out float frequency,
        out float dampingRatio)
    {
        frequency = 0f;
        dampingRatio = 1f;
        float reducedMass = massA > 0f && massB > 0f ? massA * massB / (massA + massB) : 0f;
        if (!(reducedMass > 0f) || !(spring > 0f) || !float.IsFinite(damper) || damper < 0f)
        {
            return false;
        }

        frequency = MathF.Sqrt(spring / reducedMass) / (2f * MathF.PI);
        dampingRatio = damper / (2f * MathF.Sqrt(spring * reducedMass));
        return float.IsFinite(frequency) && frequency > 0f && float.IsFinite(dampingRatio) && dampingRatio >= 0f;
    }

    /// <summary>The original anchors the rope half a unit along the attach part's facing edge
    /// (<c>Vector3.up * 0.5f</c> for a sandbag, <c>Vector3.up * -0.5f</c> for a balloon).</summary>
    private const float AttachmentAnchorOffset = 0.5f;

    /// <summary>
    /// <c>SpringEndpoint.prefab</c> (<c>GameObject/SpringEndpoint.prefab</c>): a BoxCollider
    /// 0.7 x 0.3 x 1 at (0, -0.4, 0), Rigidbody mass 1, drag 0 / angularDrag 0.05, constraints 56 —
    /// the body the original instantiates to continue a broken spring (<c>Spring.cs:131-176</c>).
    /// </summary>
    private const float SpringEndpointMass = 1f;

    /// <summary>
    /// Binds the runtime attachments at start of simulation: every part carrying an attachment
    /// capability searches its direction for the first chassis (or pig) and is tied to it with a
    /// rope joint, exactly as the original builds a SpringJoint in <c>Initialize</c>
    /// (Sandbag.cs:136-164, Balloon.cs:143-166). No anchor in range means no joint: the part
    /// simply stays free. Deterministic: the construction search walks cells outward and orders
    /// each cell by EntityId, and the parts are bound in ascending entity order.
    /// </summary>
    private void BindAttachments()
    {
        uint[] placed = _construction.PlacedEntities.ToArray();
        Array.Sort(placed);
        foreach (uint entityValue in placed)
        {
            EntityId entity = new(entityValue);
            if (!_parts.TryGet(entity, out PartLink link)
                || !_transforms.TryGet(entity, out EntityTransform transform))
            {
                continue;
            }

            PartDefinition attachPart = _content.GetPart(link.PartTypeId);
            PartAttachment? attachment = attachPart.Capabilities?.Attachment;
            if (attachment is null)
            {
                continue;
            }

            // The original's two attachment families search different distances in the vanilla
            // declaration defaults (Sandbag.cs:63, Balloon.cs:87): one cell for a sandbag, five for
            // a balloon. The family is the lift capability — balloons carry it, sandbags do not.
            int searchCells = attachPart.Capabilities?.HasBalloon == true
                ? ConstructionRules.BalloonAttachmentSearchCells
                : ConstructionRules.SandbagAttachmentSearchCells;
            int directionY = attachment.Direction == AttachmentDirection.Up ? 1 : -1;
            if (_construction.FindAttachmentTarget(entity, 0, directionY, searchCells) is not EntityId anchor
                || !_bodies.TryGet(entity, out PhysicsBodyLink attachLink)
                || !_bodies.TryGet(anchor, out PhysicsBodyLink anchorLink)
                || attachLink.Body == anchorLink.Body
                || !_bodyPose.TryGetValue(attachLink.Body.Value, out (PhysicsVector3 Position, PhysicsQuaternion Rotation) attachPose)
                || !_bodyPose.TryGetValue(anchorLink.Body.Value, out (PhysicsVector3 Position, PhysicsQuaternion Rotation) anchorPose)
                || !_parts.TryGet(anchor, out PartLink anchorPartLink)
                || !_transforms.TryGet(anchor, out EntityTransform anchorTransform))
            {
                continue;
            }

            PartDefinition anchorPart = _content.GetPart(anchorPartLink.PartTypeId);
            float distance = PhysicsVector3.Distance(attachPose.Position, anchorPose.Position);
            float maxDistance = attachment.DistanceFactor is float factor
                ? (factor * (distance + (attachment.DistanceOffset ?? 0f)))
                    + (anchorPart.Capabilities?.IsPig == true ? attachment.PigDistanceBonus ?? 0f : 0f)
                : attachment.MaxDistance;
            if (!float.IsFinite(maxDistance) || maxDistance <= 0f)
            {
                continue;
            }

            float attachMass = attachPart.Mass * transform.Scale * transform.Scale * transform.Scale;
            float anchorMass = anchorPart.Mass * anchorTransform.Scale * anchorTransform.Scale * anchorTransform.Scale;
            if (!TrySpringResponse(attachMass, anchorMass, AttachmentSpring, AttachmentDamper, out float frequency, out float dampingRatio))
            {
                continue;
            }

            JointDefinition definition = new(
                PhysicsJointKind.Distance,
                anchorLink.Body,
                attachLink.Body,
                PhysicsConstraintMask.LockPositionX | PhysicsConstraintMask.LockPositionY | PhysicsConstraintMask.LockPositionZ,
                breakForce: 0f,
                breakTorque: 0f,
                localAnchorA: ToBodyLocal(anchor.Value, attachment.Offset),
                localAnchorB: ToBodyLocal(entity.Value, new PhysicsVector3(0f, directionY * AttachmentAnchorOffset, 0f)),
                minimumDistance: 0f,
                maximumDistance: maxDistance,
                springFrequency: frequency,
                springDampingRatio: dampingRatio);
            _attachmentJoints.Add((_world.CreateJoint(definition), attachLink.Body, anchorLink.Body));
        }
    }

    /// <summary>Maps a point in a construction entity's own frame into its body's frame
    /// (identity for a singleton body).</summary>
    private PhysicsVector3 ToBodyLocal(uint entityValue, PhysicsVector3 point) =>
        _compoundLocalByEntity.TryGetValue(entityValue, out (PhysicsVector3 Offset, PhysicsQuaternion Rotation) local)
            ? local.Offset + local.Rotation.Rotate(point)
            : point;

    /// <summary>
    /// The mass of a body's own members: the sum of their content masses scaled the way
    /// <see cref="CompoundAssembler"/> scales them. A wheel's mounts are shapes hosted by its
    /// parent body, not members of it, so they are not counted here — and neither are they
    /// counted in the body the assembler builds, so this is the mass the solver uses.
    /// </summary>
    private float BodyMass(PhysicsBodyId body)
    {
        if (!_entitiesByBody.TryGetValue(body.Value, out List<uint>? members))
        {
            return 0f;
        }

        float mass = 0f;
        foreach (uint memberValue in members)
        {
            EntityId member = new(memberValue);
            if (_parts.TryGet(member, out PartLink link) && _transforms.TryGet(member, out EntityTransform transform))
            {
                mass += _content.MassOf(_content.GetPart(link.PartTypeId), transform.Scale);
            }
        }

        return mass;
    }

    /// <summary>
    /// World Z yaw of a rotation, matching the client's `yawFromQuaternion`. Every sprite the
    /// renderer draws is oriented in the plane, so the attach frame travels as this scalar even
    /// though the physics state is a quaternion.
    /// </summary>
    /// <summary>Order-independent key for a pair of entities: the weld table's index.</summary>
    private static long PairKey(uint left, uint right) =>
        left <= right ? ((long)left << 32) | right : ((long)right << 32) | left;

    private static float YawOf(PhysicsQuaternion rotation) =>
        MathF.Atan2(
            2f * ((rotation.W * rotation.Z) + (rotation.X * rotation.Y)),
            1f - (2f * ((rotation.Y * rotation.Y) + (rotation.Z * rotation.Z))));

    /// <summary>Yaw of the frame this entity's non-spinning sprites are attached to.</summary>
    private float AttachYawOf(uint entityValue, PhysicsQuaternion rotation, int snapshotCount)
    {
        if (!_attachByEntity.TryGetValue(entityValue, out (PhysicsBodyId Body, PhysicsQuaternion LocalRotation) attach)
            || !TryFindSnapshot(attach.Body, snapshotCount, out PhysicsBodySnapshot parent))
        {
            // Not hinged (or its chassis is gone): the part is rigid to its own body.
            return YawOf(rotation);
        }

        return YawOf(parent.Rotation * attach.LocalRotation);
    }

    private void ForgetJointsForBody(PhysicsBodyId body)
    {
        for (int index = _wheelJoints.Count - 1; index >= 0; index--)
        {
            (PhysicsJointId joint, PhysicsBodyId wheel, PhysicsBodyId parent) = _wheelJoints[index];
            if (wheel != body && parent != body)
            {
                continue;
            }

            _world.DestroyJoint(joint);
            _wheelJoints.RemoveAt(index);
            // A free wheel is rigid to itself again: its mounts follow its own body.
            if (_entityByBody.TryGetValue(wheel.Value, out EntityId wheelEntity))
            {
                _attachByEntity.Remove(wheelEntity.Value);
            }
        }

        for (int index = _attachmentJoints.Count - 1; index >= 0; index--)
        {
            (PhysicsJointId joint, PhysicsBodyId attach, PhysicsBodyId anchor) = _attachmentJoints[index];
            if (attach != body && anchor != body)
            {
                continue;
            }

            _world.DestroyJoint(joint);
            _attachmentJoints.RemoveAt(index);
        }

        // A destroyed body takes its welds with it (the backend drops every joint of a removed
        // body). The definitions stay in _welds: a rebuild can give the pair bodies again
        // (see RebindWeldJoints), while a pair whose part is gone is simply never bound again.
        for (int index = _weldJoints.Count - 1; index >= 0; index--)
        {
            (PhysicsJointId joint, PhysicsBodyId left, PhysicsBodyId right, _) = _weldJoints[index];
            if (left != body && right != body)
            {
                continue;
            }

            _world.DestroyJoint(joint);
            _weldJoints.RemoveAt(index);
        }

        // The same discipline for the spring seams: their definitions stay in _springs so a rebuild
        // can bind the pair again (RebindSpringJoints), and a broken-host pair is simply never
        // bound again.
        for (int index = _springJoints.Count - 1; index >= 0; index--)
        {
            SpringJoint link = _springJoints[index];
            if (link.HostBody != body && link.OtherBody != body)
            {
                continue;
            }

            _world.DestroyJoint(link.Joint);
            _springJoints.RemoveAt(index);
        }

        // And for the endpoint sub-entities' links: a destroyed host body takes the link with it, and
        // the endpoint is re-attached once the host has a body again (RebindSubEntityJoints).
        foreach (List<SubEntity> subEntities in _subEntitiesByHost.Values)
        {
            foreach (SubEntity sub in subEntities)
            {
                if (!sub.Joint.IsValid || (sub.JointHostBody != body && sub.Body != body))
                {
                    continue;
                }

                _world.DestroyJoint(sub.Joint);
                sub.Joint = default;
            }
        }
    }

    private void UnbindEntity(EntityId entity, bool destroyBodyIfOrphan)
    {
        if (destroyBodyIfOrphan)
        {
            // ADR-027 decision 3: a runtime sub-entity's life is its host's. A host that truly leaves
            // the world (a remove, a rule destruction, a RESET, a leave) takes its sub-entities with
            // it, joints before bodies — a compound split (destroyBodyIfOrphan: false) does not.
            DestroySubEntitiesOf(entity.Value);
        }

        if (!_bodyByEntity.Remove(entity.Value, out PhysicsBodyId body))
        {
            return;
        }

        _compoundLocalByEntity.Remove(entity.Value);
        // Keep the rules layer's body bookkeeping in step: without this the body a split just
        // vacated stays in its dynamic set, and a later blast would aim an impulse at a body the
        // world has already destroyed.
        _rules.UnbindBody(entity);
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
                    ForgetJointsForBody(body);
                    _bodyPose.Remove(body.Value);
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
            ForgetJointsForBody(body);
            _bodyPose.Remove(body.Value);
            DropPendingCommands(body);
            _world.DestroyBody(body);
        }
    }

    /// <summary>
    /// Registers a runtime sub-entity (ADR-027): an entity id, a part link carrying its host's part
    /// type (the wire has no other kind), a transform, and its own body. It never enters construction
    /// rules — no footprint, no cell, no command — and it counts toward
    /// <see cref="MaxSnapshotEntityCount"/> through both the part store and the body table.
    /// </summary>
    private SubEntity CreateSubEntity(EntityId host, uint partTypeId, BodyDefinition definition, CompoundSpring? spring)
    {
        EntityId entity = _entities.Create();
        _parts.Set(entity, new PartLink(partTypeId));
        _transforms.Set(entity, new EntityTransform(definition.Position, definition.Rotation));
        PhysicsBodyId body = _world.CreateBody(definition);
        _bodyPose[body.Value] = (definition.Position, definition.Rotation);
        _bodies.Set(entity, new PhysicsBodyLink(body));
        _bodyByEntity.Add(entity.Value, body);
        _entitiesByBody[body.Value] = new List<uint> { entity.Value };
        _entityByBody[body.Value] = entity;

        SubEntity sub = new()
        {
            Entity = entity,
            Host = host,
            PartTypeId = partTypeId,
            Body = body,
            Spring = spring,
        };
        if (!_subEntitiesByHost.TryGetValue(host.Value, out List<SubEntity>? subEntities))
        {
            subEntities = new List<SubEntity>();
            _subEntitiesByHost.Add(host.Value, subEntities);
        }

        subEntities.Add(sub);
        _subEntityIds.Add(entity.Value);
        _subEntityCount++;
        return sub;
    }

    /// <summary>Destroys every sub-entity one host spawned, in creation order (ADR-027 decision 3).</summary>
    private void DestroySubEntitiesOf(uint hostValue)
    {
        if (!_subEntitiesByHost.Remove(hostValue, out List<SubEntity>? subEntities))
        {
            return;
        }

        foreach (SubEntity sub in subEntities)
        {
            DestroySubEntity(sub);
        }
    }

    /// <summary>Destroys one sub-entity: its joint first, then its body, then its entity id and the
    /// part/transform entries it borrowed (the ADR-023 order discipline).</summary>
    private void DestroySubEntity(SubEntity sub)
    {
        if (sub.Joint.IsValid)
        {
            _world.DestroyJoint(sub.Joint);
            sub.Joint = default;
        }

        // A glove's life is its host's like any other sub-entity's: forget the machine with the
        // body so a later materialisation starts from a wound-up glove again.
        if (sub.Glove is not null)
        {
            _glovesByHost.Remove(sub.Host.Value);
            _gloveHosts.Remove(sub.Host.Value);
        }

        _bodyByEntity.Remove(sub.Entity.Value);
        _entitiesByBody.Remove(sub.Body.Value);
        _entityByBody.Remove(sub.Body.Value);
        _bodyPose.Remove(sub.Body.Value);
        _world.DestroyBody(sub.Body);
        if (_entities.IsAlive(sub.Entity))
        {
            _entities.Destroy(sub.Entity);
        }

        _parts.Remove(sub.Entity);
        _transforms.Remove(sub.Entity);
        _subEntityIds.Remove(sub.Entity.Value);
        _stowedSubEntityIds.Remove(sub.Entity.Value);
        _subEntityCount--;
    }

    /// <summary>
    /// Marks a sub-entity as inactive for its machine's idle state (the original's
    /// <c>GameObject.SetActive</c>): a stowed sub-entity is left out of the published frame, so the
    /// client draws the host part alone instead of drawing the sub-entity over it. Its body stays
    /// in the world -- that is what the machine then drives out.
    /// </summary>
    private void SetSubEntityStowed(SubEntity sub, bool stowed)
    {
        if (stowed)
        {
            _stowedSubEntityIds.Add(sub.Entity.Value);
        }
        else
        {
            _stowedSubEntityIds.Remove(sub.Entity.Value);
        }
    }

    /// <summary>
    /// Drops every sub-entity record on a path that already destroyed the world's bodies itself (a
    /// build-mode re-entry or a retry destroys each body once): the borrowed part/transform entries
    /// go and the entity ids are released, but the world is not touched again.
    /// </summary>
    private void ClearSubEntities()
    {
        foreach (List<SubEntity> subEntities in _subEntitiesByHost.Values)
        {
            foreach (SubEntity sub in subEntities)
            {
                if (_entities.IsAlive(sub.Entity))
                {
                    _entities.Destroy(sub.Entity);
                }

                _parts.Remove(sub.Entity);
                _transforms.Remove(sub.Entity);
            }
        }

        _subEntitiesByHost.Clear();
        _subEntityIds.Clear();
        _stowedSubEntityIds.Clear();
        _subEntityCount = 0;
        _glovesByHost.Clear();
        _gloveHosts.Clear();
    }

    /// <summary>
    /// A compound split destroys the body the rules aimed this tick's commands at, and the next
    /// tick is what applies those commands -- so they have to go with the body. The pieces are
    /// re-bound (BindCluster) and emit their own commands on the next rules pass, so the only
    /// thing dropped is a tick of thrust aimed at a body that no longer exists. Without this the
    /// room wedges: `BepuPhysicsWorld.ApplyCommands` throws "Impulse target body N does not exist
    /// or is not dynamic" and stops ticking (measured with a rotor whose thrust breaks its seam).
    /// </summary>
    private void DropPendingCommands(PhysicsBodyId body)
    {
        for (int index = _output.Commands.Count - 1; index >= 0; index--)
        {
            if (_output.Commands[index].Body.Value == body.Value)
            {
                _output.Commands.RemoveAt(index);
            }
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
            CompoundCluster? pruned = PruneDeadMembers(live.Cluster);
            if (pruned is null)
            {
                continue;
            }

            live.Cluster = pruned;
            CompoundSeam? seam = CompoundAssembler.NearestSeam(live.Cluster, command.WorldPoint);
            if (seam is null || magnitude <= seam.Value.BreakImpulse || IsGluedCompound(live.Body))
            {
                continue;
            }

            IReadOnlyList<CompoundCluster> pieces = CompoundAssembler.SplitAlongSeam(live.Cluster, seam.Value);
            if (pieces.Count == 1)
            {
                live.Cluster = pieces[0];
                continue;
            }

            RebuildSplitBody(live.Body, snapshot, pieces);
        }
    }

    /// <summary>
    /// A live compound's cluster can still list a member the room destroyed earlier this tick
    /// (its body link is gone but the cluster record is only rebuilt on a split). Splitting such
    /// a cluster would try to respawn the dead part, so drop those members first.
    /// </summary>
    private CompoundCluster? PruneDeadMembers(CompoundCluster cluster)
    {
        HashSet<uint>? dead = null;
        foreach (CompoundMember member in cluster.Members)
        {
            if (!_entities.IsAlive(member.Entity))
            {
                (dead ??= new HashSet<uint>()).Add(member.Entity.Value);
            }
        }

        return dead is null ? cluster : CompoundAssembler.WithoutMembers(cluster, dead);
    }

    /// <summary>Super glue (original AlienEgg): a compound holding a glue part never splits
    /// along a seam, however large the applied impulse.</summary>
    private bool IsGluedCompound(PhysicsBodyId body)
    {
        if (!_entitiesByBody.TryGetValue(body.Value, out List<uint>? members))
        {
            return false;
        }

        foreach (uint entityValue in members)
        {
            if (_rules.HasGlue(new EntityId(entityValue)))
            {
                return true;
            }
        }

        return false;
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
        CompoundCluster? pruned = PruneDeadMembers(live.Cluster);
        if (pruned is null)
        {
            return;
        }

        live.Cluster = pruned;
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

        RebuildSplitBody(link.Body, snapshot, pieces);
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
            // A stowed sub-entity (a wound-up glove's fist) is the original's inactive GameObject:
            // it is not drawn, so it is not published either. The body itself stays in the world --
            // it is what the throw then drives out.
            if (!_stowedSubEntityIds.Contains(parts.CurrentId.Value))
            {
                _entityOrder.Add(parts.CurrentId.Value);
            }
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
            if (!_stowedSubEntityIds.Contains(pair.Key))
            {
                _entityOrder.Add(pair.Key);
            }
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

    /// <summary>
    /// One bound spring seam: the two placed parts and the slackless distance link holding them
    /// (docs/specs/spring-joint.md §3). The anchors are stored in each body's frame so the pull break
    /// can measure them from the tick's snapshots without re-resolving the compound offsets.
    /// </summary>
    private sealed class SpringJoint
    {
        public CompoundSpring Spring { get; init; }

        /// <summary>The end that carries <c>capabilities.spring</c>: the joint belongs to it.</summary>
        public EntityId Host { get; init; }

        public PhysicsJointId Joint { get; init; }

        public PhysicsBodyId HostBody { get; init; }

        public PhysicsBodyId OtherBody { get; init; }

        public PhysicsVector3 AnchorInHostBody { get; init; }

        public PhysicsVector3 AnchorInOtherBody { get; init; }
    }

    /// <summary>
    /// A runtime sub-entity (ADR-027): a body a host part spawned. Its life follows the host's, and it
    /// carries a part link, a transform and a body but no construction footprint. Its first consumer
    /// is the spring's endpoint rigid body (<see cref="Spring"/>); the boxing glove's second body
    /// uses the same record (<see cref="Glove"/>), and the payload says which joint the sub-entity
    /// needs.
    /// </summary>
    private sealed class SubEntity
    {
        public EntityId Entity { get; init; }

        public EntityId Host { get; init; }

        public uint PartTypeId { get; init; }

        public PhysicsBodyId Body { get; init; }

        /// <summary>The seam this sub-entity continues (the spring that broke), for its own joint;
        /// null for a sub-entity another consumer owns (the boxing glove).</summary>
        public CompoundSpring? Spring { get; init; }

        /// <summary>The glove this sub-entity is, when its host carries <c>capabilities.glove</c>.</summary>
        public GloveLink? Glove { get; set; }

        public PhysicsJointId Joint { get; set; }

        public PhysicsBodyId JointHostBody { get; set; }
    }

    /// <summary>
    /// One host part's boxing glove (docs/specs/boxing-glove.md §3/§4): the sub-entity that is
    /// the glove rigidbody, the extracted content it runs on, and the machine's own state. The
    /// room owns the state so a client replay drives the same transitions.
    /// </summary>
    private sealed class GloveLink
    {
        public required SubEntity Sub { get; init; }

        public required PartGlove Glove { get; init; }

        /// <summary>The content's activation, i.e. which trigger path this glove follows.</summary>
        public required PartActivation Activation { get; init; }

        public BoxingGloveState State { get; set; }

        /// <summary>The toggle's last seen level, so only an off→on edge throws a punch.</summary>
        public bool SwitchWasActive { get; set; }

    }
}
