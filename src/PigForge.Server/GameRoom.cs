using System.Runtime.InteropServices;
using PigForge.Core;
using PigForge.Core.Content;
using PigForge.Physics.Abstractions;

namespace PigForge.Server;

/// <summary>Per-room gameplay roles layered on top of part content during level setup.</summary>
public sealed record RoomSpawnSpec(
    uint PartTypeId,
    PhysicsVector3 Position,
    RoomActorRole Role = RoomActorRole.Part,
    float HitPoints = 1f,
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
    GameplayConfig? GameplayConfig = null)
{
    public static GameRoomOptions Create(PartContentLibrary content, Func<IPhysicsWorld> worldFactory, ushort tickRateHz = 60) =>
        new(tickRateHz, content, worldFactory);
}

/// <summary>
/// One room owns exactly one authoritative physics world. Each tick advances through
/// ordered phases: input (apply the rules' commands from the previous tick) → physics
/// step → snapshot/event collection → rules update → destruction. Ticks are driven
/// manually so execution stays deterministic; wall-clock scheduling is a transport concern.
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
    private readonly DamageStore _damage;
    private readonly MotorStore _motors;
    private readonly TntStore _tnt;
    private readonly WheelStore _wheels;
    private readonly PigStore _pigs;
    private readonly GameplayRules _rules;
    private readonly GameplayTickOutput _output = new();
    private readonly Dictionary<uint, PhysicsBodyId> _bodyByEntity = new();
    private PhysicsBodySnapshot[] _snapshotBuffer = Array.Empty<PhysicsBodySnapshot>();
    private PhysicsEvent[] _eventBuffer = Array.Empty<PhysicsEvent>();
    private bool _sealed;
    private bool _disposed;

    public GameRoom(GameRoomOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _content = options.Content ?? throw new ArgumentException("Room options must carry part content.", nameof(options));
        _world = options.WorldFactory?.Invoke() ?? throw new ArgumentException("Room options must provide a world factory.", nameof(options));
        _timeStep = FixedTimeStep.FromSeconds(1f / (options.TickRateHz == 0 ? (ushort)60 : options.TickRateHz));
        _parts = new PartStore(_entities);
        _transforms = new TransformStore(_entities);
        _bodies = new PhysicsBodyStore(_entities);
        _damage = new DamageStore(_entities);
        _motors = new MotorStore(_entities);
        _tnt = new TntStore(_entities);
        _wheels = new WheelStore(_entities);
        _pigs = new PigStore(_entities);
        _rules = new GameplayRules(
            _entities, _damage, _motors, _tnt, _wheels, _pigs, _bodies, _transforms, options.GameplayConfig);
    }

    public uint CurrentTick { get; private set; }

    public GameplayPhase Phase => _rules.Phase;

    public int AlivePigs => _rules.AlivePigs;

    public int BodyCount => _bodyByEntity.Count;

    /// <summary>Spawns a level actor. Allowed only during setup, before the first tick.</summary>
    public EntityId Spawn(in RoomSpawnSpec spec)
    {
        ThrowIfDisposed();
        if (_sealed)
        {
            throw new InvalidOperationException("Level setup is sealed after the first tick.");
        }

        EntityId entity = _entities.Create();
        _parts.Set(entity, new PartLink(spec.PartTypeId));
        _transforms.Set(entity, new EntityTransform(spec.Position, PhysicsQuaternion.Identity));
        PhysicsBodyId body = _world.CreateBody(_content.CreateBodyDefinition(spec.PartTypeId, spec.Position, PhysicsQuaternion.Identity));
        _bodies.Set(entity, new PhysicsBodyLink(body));
        _rules.LinkBody(entity, body, isDynamic: spec.Role != RoomActorRole.Part || _content.GetPart(spec.PartTypeId).Mode == PhysicsBodyMode.Dynamic);
        _bodyByEntity.Add(entity.Value, body);

        switch (spec.Role)
        {
            case RoomActorRole.Pig:
                _rules.AddPig(entity, spec.HitPoints);
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

        EnsureBuffers();
        return entity;
    }

    public void Tick()
    {
        ThrowIfDisposed();
        _sealed = true;
        CurrentTick++;

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
            if (_bodyByEntity.Remove(destroyed.Value, out PhysicsBodyId body))
            {
                _world.DestroyBody(body);
            }
        }
    }

    public void RunTicks(uint count)
    {
        for (uint index = 0; index < count; index++)
        {
            Tick();
        }
    }

    /// <summary>Deterministic state hash over the authoritative snapshots plus rule outcomes.</summary>
    public long ComputeStateHash()
    {
        ThrowIfDisposed();
        int count = _world.CopySnapshots(_snapshotBuffer);
        PhysicsBodySnapshot[] ordered = _snapshotBuffer.Take(count).OrderBy(snapshot => snapshot.Body.Value).ToArray();
        long hash = 17;
        hash = unchecked((hash * 31) + CurrentTick.GetHashCode());
        hash = unchecked((hash * 31) + (int)Phase);
        hash = unchecked((hash * 31) + _rules.AlivePigs);
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
        _world.Dispose();
        _bodyByEntity.Clear();
        _output.Clear();
        _snapshotBuffer = Array.Empty<PhysicsBodySnapshot>();
        _eventBuffer = Array.Empty<PhysicsEvent>();
    }

    private void EnsureBuffers()
    {
        int bodyCount = _bodyByEntity.Count;
        _snapshotBuffer = new PhysicsBodySnapshot[bodyCount];
        _eventBuffer = new PhysicsEvent[(bodyCount * (bodyCount - 1) / 2) + bodyCount + 16];
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);
}
