using System.Runtime.InteropServices;
using PigForge.Physics.Abstractions;
using PigForge.Protocol;

namespace PigForge.Replay;

public interface IReplayPhysicsContent
{
    BodyDefinition CreateBody(ReplayEntityState entity);
}

public sealed class PhysicsReplaySimulation : IReplaySimulation, IDisposable
{
    private readonly IPhysicsWorld _world;
    private readonly IReplayPhysicsContent _content;
    private readonly Dictionary<PhysicsBodyId, BodyBinding> _bindingsByPhysicsBody = new();
    private readonly Dictionary<uint, PhysicsBodyId> _physicsBodyByEntity = new();
    private readonly List<PhysicsBodySnapshot> _snapshotBuffer = new();
    private readonly List<PhysicsEvent> _eventBuffer = new();
    private bool _loaded;
    private bool _disposed;

    public PhysicsReplaySimulation(IPhysicsWorld world, IReplayPhysicsContent content)
    {
        _world = world ?? throw new ArgumentNullException(nameof(world));
        _content = content ?? throw new ArgumentNullException(nameof(content));
    }

    public ReplayOutcome Outcome { get; private set; } = ReplayOutcome.Success;

    public void LoadInitialState(ReplayInitialState initialState)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(initialState);
        if (_loaded)
        {
            throw new InvalidOperationException("A physics replay simulation can load its initial state only once.");
        }

        foreach (ReplayEntityState entity in initialState.Entities)
        {
            BodyDefinition definition = _content.CreateBody(entity)
                ?? throw new InvalidOperationException($"Content returned no body definition for entity {entity.EntityId}.");
            PhysicsBodyId physicsBody = _world.CreateBody(definition);
            if (!_bindingsByPhysicsBody.TryAdd(physicsBody, new BodyBinding(entity.EntityId, entity.PhysicsBodyId, entity.PartTypeId, entity.Scale)))
            {
                throw new InvalidOperationException($"Physics body {physicsBody.Value} was returned more than once.");
            }

            if (!_physicsBodyByEntity.TryAdd(entity.EntityId, physicsBody))
            {
                throw new InvalidOperationException($"Entity {entity.EntityId} was loaded more than once.");
            }
        }

        DrainAndDiscardInitialEvents();
        _loaded = true;
    }

    public void ApplyCommand(ReplayCommand command)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(command);
        EnsureLoaded();

        switch (command)
        {
            case StartSimulationCommand:
                return;
            case RemovePartCommand remove:
                RemoveEntity(remove.EntityId);
                return;
            case PlacePartCommand:
                throw new NotSupportedException("PhysicsReplaySimulation requires a logical entity allocator for PlacePartCommand.");
            case RotatePartCommand:
                throw new NotSupportedException("PhysicsReplaySimulation does not support RotatePartCommand without a pose mutation contract.");
            case MovePartCommand:
                throw new NotSupportedException("PhysicsReplaySimulation does not support MovePartCommand without a pose mutation contract.");
            case ScalePartCommand:
                throw new NotSupportedException("PhysicsReplaySimulation does not support ScalePartCommand without a pose mutation contract.");
            case SetPartActiveCommand:
            case SetPartTypeActiveCommand:
                throw new NotSupportedException("PhysicsReplaySimulation has no gameplay switch contract.");
            default:
                throw new ArgumentOutOfRangeException(nameof(command), command.Kind, "Unknown replay command type.");
        }
    }

    public void Step(FixedTimeStep timeStep)
    {
        ThrowIfDisposed();
        EnsureLoaded();
        _world.Step(timeStep);
    }

    public IReadOnlyList<ReplayEntityState> CaptureSnapshots()
    {
        ThrowIfDisposed();
        EnsureLoaded();
        EnsureSnapshotCapacity(_bindingsByPhysicsBody.Count);
        int count = _world.CopySnapshots(CollectionsMarshal.AsSpan(_snapshotBuffer));
        ReplayEntityState[] snapshots = new ReplayEntityState[count];

        for (int index = 0; index < count; index++)
        {
            PhysicsBodySnapshot snapshot = _snapshotBuffer[index];
            if (!_bindingsByPhysicsBody.TryGetValue(snapshot.Body, out BodyBinding binding))
            {
                throw new InvalidOperationException($"Physics snapshot references unknown body {snapshot.Body.Value}.");
            }

            snapshots[index] = new ReplayEntityState(
                binding.EntityId,
                binding.ReplayBodyId,
                binding.PartTypeId,
                ToReplay(snapshot.Position),
                ToReplay(snapshot.Rotation),
                ToReplay(snapshot.LinearVelocity),
                ToReplay(snapshot.AngularVelocity),
                binding.Scale);
        }

        return snapshots;
    }

    public IReadOnlyList<ReplayEvent> DrainEvents()
    {
        ThrowIfDisposed();
        EnsureLoaded();
        EnsureEventCapacity(_bindingsByPhysicsBody.Count);
        int count = _world.DrainEvents(CollectionsMarshal.AsSpan(_eventBuffer));
        ReplayEvent[] events = new ReplayEvent[count];

        for (int index = 0; index < count; index++)
        {
            PhysicsEvent physicsEvent = _eventBuffer[index];
            events[index] = ConvertEvent(physicsEvent);
            if (physicsEvent.Kind == PhysicsEventKind.BodyDestroyed)
            {
                _bindingsByPhysicsBody.Remove(physicsEvent.BodyA);
            }
        }

        return events;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _world.Dispose();
        _bindingsByPhysicsBody.Clear();
        _physicsBodyByEntity.Clear();
        _snapshotBuffer.Clear();
        _eventBuffer.Clear();
    }

    private void RemoveEntity(uint entityId)
    {
        if (!_physicsBodyByEntity.Remove(entityId, out PhysicsBodyId physicsBody))
        {
            throw new KeyNotFoundException($"Replay entity {entityId} does not exist.");
        }

        _world.DestroyBody(physicsBody);
    }

    private ReplayEvent ConvertEvent(PhysicsEvent physicsEvent)
    {
        return physicsEvent.Kind switch
        {
            PhysicsEventKind.ContactStarted => ContactEvent(ReplayEventKind.ContactStarted, physicsEvent),
            PhysicsEventKind.ContactPersisted => ContactEvent(ReplayEventKind.ContactPersisted, physicsEvent),
            PhysicsEventKind.ContactEnded => ContactEvent(ReplayEventKind.ContactEnded, physicsEvent),
            PhysicsEventKind.BodyCreated => LifecycleEvent(ReplayEventKind.EntityCreated, physicsEvent),
            PhysicsEventKind.BodyDestroyed => LifecycleEvent(ReplayEventKind.EntityDestroyed, physicsEvent),
            PhysicsEventKind.JointBroken => throw new NotSupportedException("Joint break replay events are not supported by this adapter."),
            _ => throw new ArgumentOutOfRangeException(nameof(physicsEvent), physicsEvent.Kind, "Unknown physics event kind.")
        };
    }

    private ReplayEvent ContactEvent(ReplayEventKind kind, PhysicsEvent physicsEvent)
    {
        if (!_bindingsByPhysicsBody.TryGetValue(physicsEvent.BodyA, out BodyBinding bodyA) ||
            !_bindingsByPhysicsBody.TryGetValue(physicsEvent.BodyB, out BodyBinding bodyB))
        {
            throw new InvalidOperationException("Contact event references an unknown physics body.");
        }

        return new ReplayEvent(kind, bodyA.ReplayBodyId, bodyB.ReplayBodyId);
    }

    private ReplayEvent LifecycleEvent(ReplayEventKind kind, PhysicsEvent physicsEvent)
    {
        if (!_bindingsByPhysicsBody.TryGetValue(physicsEvent.BodyA, out BodyBinding body))
        {
            throw new InvalidOperationException("Body lifecycle event references an unknown physics body.");
        }

        return new ReplayEvent(kind, EntityId: body.EntityId);
    }

    private void DrainAndDiscardInitialEvents()
    {
        EnsureEventCapacity(_bindingsByPhysicsBody.Count);
        _ = _world.DrainEvents(CollectionsMarshal.AsSpan(_eventBuffer));
    }

    private void EnsureSnapshotCapacity(int count)
    {
        while (_snapshotBuffer.Count < count)
        {
            _snapshotBuffer.Add(default);
        }
    }

    private void EnsureEventCapacity(int bodyCount)
    {
        int pairCount = checked(bodyCount * (bodyCount - 1) / 2);
        int required = checked(Math.Max(1, pairCount + bodyCount));
        while (_eventBuffer.Count < required)
        {
            _eventBuffer.Add(default);
        }
    }

    private void EnsureLoaded()
    {
        if (!_loaded)
        {
            throw new InvalidOperationException("LoadInitialState must be called before reading replay output.");
        }
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    private static ReplayVector3 ToReplay(PhysicsVector3 value) => new(value.X, value.Y, value.Z);

    private static ReplayQuaternion ToReplay(PhysicsQuaternion value) => new(value.X, value.Y, value.Z, value.W);

    private readonly record struct BodyBinding(uint EntityId, uint ReplayBodyId, uint PartTypeId, float Scale);
}
