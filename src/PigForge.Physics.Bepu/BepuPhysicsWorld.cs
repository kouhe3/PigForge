using System.Numerics;
using BepuPhysics;
using BepuPhysics.Collidables;
using BepuPhysics.CollisionDetection;
using BepuPhysics.Constraints;
using BepuUtilities;
using BepuUtilities.Memory;
using PigForge.Physics.Abstractions;

namespace PigForge.Physics.Bepu;

public sealed class BepuPhysicsWorld : IPhysicsWorld
{
    private readonly BufferPool _bufferPool;
    private readonly Simulation _simulation;
    private readonly Dictionary<PhysicsBodyId, BodyHandle> _dynamicBodies = new();
    private readonly Dictionary<PhysicsBodyId, StaticHandle> _staticBodies = new();
    private readonly Dictionary<PhysicsBodyId, TypedIndex> _shapesByBody = new();
    private readonly Dictionary<int, PhysicsBodyId> _dynamicIdsByHandle = new();
    private readonly Dictionary<int, PhysicsBodyId> _staticIdsByHandle = new();
    private readonly List<PhysicsBodyId> _bodyOrder = new();
    private readonly List<PhysicsEvent> _events = new();
    private readonly HashSet<ContactPair> _activeContacts = new();
    private readonly HashSet<ContactPair> _currentContacts = new();
    private readonly List<ContactPair> _orderedContacts = new();
    private uint _nextBodyId = 1;
    private bool _disposed;

    public BepuPhysicsWorld(PhysicsVector3 gravity)
    {
        if (!gravity.IsFinite)
        {
            throw new ArgumentOutOfRangeException(nameof(gravity), "Gravity must contain only finite values.");
        }

        _bufferPool = new BufferPool();
        _simulation = Simulation.Create(
            _bufferPool,
            new NarrowPhaseCallbacks(this),
            new PoseIntegratorCallbacks(ToNumerics(gravity)),
            new SolveDescription(8, 1));
    }

    public PhysicsCapabilities Capabilities { get; } = new(
        new HashSet<PhysicsJointKind>(),
        SupportsContinuousCollision: false,
        SupportsPerBodyInertia: true);

    public PhysicsBodyId CreateBody(BodyDefinition definition)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(definition);
        BoxShapeDefinition shape = GetSingleBoxShape(definition);
        Box physicsShape = new(shape.HalfExtentX * 2, shape.HalfExtentY * 2, shape.HalfExtentZ * 2);
        PhysicsBodyId id = AllocateBodyId();
        RigidPose pose = CreatePose(definition.Position, definition.Rotation);
        TypedIndex shapeIndex = _simulation.Shapes.Add(physicsShape);

        if (definition.Mode == PhysicsBodyMode.Static)
        {
            StaticHandle handle = _simulation.Statics.Add(new StaticDescription(pose, shapeIndex));
            _staticBodies.Add(id, handle);
            _staticIdsByHandle.Add(handle.Value, id);
        }
        else
        {
            BodyDescription body = BodyDescription.CreateDynamic(
                pose,
                new BodyVelocity(ToNumerics(definition.LinearVelocity), ToNumerics(definition.AngularVelocity)),
                physicsShape.ComputeInertia(definition.Mass),
                new CollidableDescription(shapeIndex),
                BodyDescription.GetDefaultActivity(physicsShape));
            BodyHandle handle = _simulation.Bodies.Add(body);
            _dynamicBodies.Add(id, handle);
            _dynamicIdsByHandle.Add(handle.Value, id);
        }

        _shapesByBody.Add(id, shapeIndex);
        _bodyOrder.Add(id);
        _events.Add(PhysicsEvent.BodyCreated(id));
        return id;
    }

    public void DestroyBody(PhysicsBodyId body)
    {
        ThrowIfDisposed();
        if (_dynamicBodies.Remove(body, out BodyHandle dynamicHandle))
        {
            _dynamicIdsByHandle.Remove(dynamicHandle.Value);
            _simulation.Bodies.Remove(dynamicHandle);
        }
        else if (_staticBodies.Remove(body, out StaticHandle staticHandle))
        {
            _staticIdsByHandle.Remove(staticHandle.Value);
            _simulation.Statics.Remove(staticHandle);
        }
        else
        {
            throw new KeyNotFoundException($"Physics body {body.Value} does not exist.");
        }

        if (_shapesByBody.Remove(body, out TypedIndex shapeIndex))
        {
            _simulation.Shapes.RemoveAndDispose(shapeIndex, _bufferPool);
        }

        _bodyOrder.Remove(body);
        RemoveContactsForBody(body);
        _events.Add(PhysicsEvent.BodyDestroyed(body));
    }

    public PhysicsJointId CreateJoint(JointDefinition definition)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(definition);
        throw new NotSupportedException("BepuPhysics backend does not implement PigForge joints yet.");
    }

    public void DestroyJoint(PhysicsJointId joint)
    {
        ThrowIfDisposed();
        if (!joint.IsValid)
        {
            throw new ArgumentException("A joint ID must be valid.", nameof(joint));
        }

        throw new NotSupportedException("BepuPhysics backend does not implement PigForge joints yet.");
    }

    public void ApplyCommands(ReadOnlySpan<PhysicsCommand> commands)
    {
        ThrowIfDisposed();
        for (int index = 0; index < commands.Length; index++)
        {
            PhysicsCommand command = commands[index];
            if (command.Kind != PhysicsCommandKind.ApplyImpulse)
            {
                throw new ArgumentOutOfRangeException(nameof(commands), command.Kind, "Unknown physics command kind.");
            }

            if (!_dynamicBodies.TryGetValue(command.Body, out BodyHandle handle))
            {
                throw new KeyNotFoundException($"Impulse target body {command.Body.Value} does not exist or is not dynamic.");
            }

            BodyReference body = _simulation.Bodies[handle];
            Vector3 impulse = ToNumerics(command.Impulse);
            Vector3 impulseOffset = ToNumerics(command.WorldPoint) - body.Pose.Position;
            body.ApplyImpulse(impulse, impulseOffset);
        }
    }

    public void Step(FixedTimeStep timeStep)
    {
        ThrowIfDisposed();
        _currentContacts.Clear();
        _simulation.Timestep(timeStep.Seconds);

        _orderedContacts.Clear();
        _orderedContacts.AddRange(_currentContacts);
        _orderedContacts.Sort(ContactPairComparer.Instance);
        foreach (ContactPair contact in _orderedContacts)
        {
            _events.Add(_activeContacts.Contains(contact)
                ? PhysicsEvent.ContactPersisted(contact.A, contact.B)
                : PhysicsEvent.ContactStarted(contact.A, contact.B));
        }

        _orderedContacts.Clear();
        _orderedContacts.AddRange(_activeContacts);
        _orderedContacts.Sort(ContactPairComparer.Instance);
        foreach (ContactPair contact in _orderedContacts)
        {
            if (!_currentContacts.Contains(contact))
            {
                _events.Add(PhysicsEvent.ContactEnded(contact.A, contact.B));
            }
        }

        _activeContacts.Clear();
        _activeContacts.UnionWith(_currentContacts);
    }

    public int CopySnapshots(Span<PhysicsBodySnapshot> destination)
    {
        ThrowIfDisposed();
        if (destination.Length < _bodyOrder.Count)
        {
            throw new ArgumentException("The snapshot destination is too small.", nameof(destination));
        }

        for (int index = 0; index < _bodyOrder.Count; index++)
        {
            PhysicsBodyId id = _bodyOrder[index];
            if (_dynamicBodies.TryGetValue(id, out BodyHandle dynamicHandle))
            {
                BodyReference body = _simulation.Bodies[dynamicHandle];
                destination[index] = new(
                    id,
                    FromNumerics(body.Pose.Position),
                    FromNumerics(body.Pose.Orientation),
                    FromNumerics(body.Velocity.Linear),
                    FromNumerics(body.Velocity.Angular));
            }
            else
            {
                StaticReference body = _simulation.Statics[_staticBodies[id]];
                destination[index] = new(
                    id,
                    FromNumerics(body.Pose.Position),
                    FromNumerics(body.Pose.Orientation),
                    PhysicsVector3.Zero,
                    PhysicsVector3.Zero);
            }
        }

        return _bodyOrder.Count;
    }

    public int DrainEvents(Span<PhysicsEvent> destination)
    {
        ThrowIfDisposed();
        if (destination.Length < _events.Count)
        {
            throw new ArgumentException("The event destination is too small.", nameof(destination));
        }

        _events.CopyTo(destination);
        int count = _events.Count;
        _events.Clear();
        return count;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _simulation.Dispose();
        _bufferPool.Clear();
        _dynamicBodies.Clear();
        _staticBodies.Clear();
        _shapesByBody.Clear();
        _dynamicIdsByHandle.Clear();
        _staticIdsByHandle.Clear();
        _bodyOrder.Clear();
        _events.Clear();
        _activeContacts.Clear();
        _currentContacts.Clear();
        _orderedContacts.Clear();
    }

    private void RecordContact(CollidablePair pair)
    {
        if (TryGetBodyId(pair.A, out PhysicsBodyId bodyA)
            && TryGetBodyId(pair.B, out PhysicsBodyId bodyB)
            && bodyA != bodyB)
        {
            _currentContacts.Add(ContactPair.Create(bodyA, bodyB));
        }
    }

    private bool TryGetBodyId(CollidableReference reference, out PhysicsBodyId body)
    {
        return reference.Mobility == CollidableMobility.Static
            ? _staticIdsByHandle.TryGetValue(reference.StaticHandle.Value, out body)
            : _dynamicIdsByHandle.TryGetValue(reference.BodyHandle.Value, out body);
    }

    private void RemoveContactsForBody(PhysicsBodyId body)
    {
        _activeContacts.RemoveWhere(contact => contact.A == body || contact.B == body);
        _currentContacts.RemoveWhere(contact => contact.A == body || contact.B == body);
    }

    private PhysicsBodyId AllocateBodyId()
    {
        if (_nextBodyId == 0)
        {
            throw new InvalidOperationException("The physics body ID space is exhausted.");
        }

        return new PhysicsBodyId(_nextBodyId++);
    }

    private static BoxShapeDefinition GetSingleBoxShape(BodyDefinition definition)
    {
        if (definition.Shapes.Count != 1 || definition.Shapes[0] is not BoxShapeDefinition box)
        {
            throw new NotSupportedException("BepuPhysics backend currently supports exactly one box shape per body.");
        }

        return box;
    }

    private static RigidPose CreatePose(PhysicsVector3 position, PhysicsQuaternion rotation)
    {
        Quaternion orientation = ToNumerics(rotation);
        float lengthSquared = orientation.LengthSquared();
        if (!float.IsFinite(lengthSquared) || lengthSquared <= float.Epsilon)
        {
            throw new ArgumentException("A body rotation must have a finite, non-zero quaternion.", nameof(rotation));
        }

        return new RigidPose(ToNumerics(position), Quaternion.Normalize(orientation));
    }

    private static Vector3 ToNumerics(PhysicsVector3 value) => new(value.X, value.Y, value.Z);

    private static Quaternion ToNumerics(PhysicsQuaternion value) => new(value.X, value.Y, value.Z, value.W);

    private static PhysicsVector3 FromNumerics(Vector3 value) => new(value.X, value.Y, value.Z);

    private static PhysicsQuaternion FromNumerics(Quaternion value) => new(value.X, value.Y, value.Z, value.W);

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    private readonly record struct ContactPair(PhysicsBodyId A, PhysicsBodyId B)
    {
        public static ContactPair Create(PhysicsBodyId first, PhysicsBodyId second) => first.Value < second.Value
            ? new(first, second)
            : new(second, first);
    }

    private sealed class ContactPairComparer : IComparer<ContactPair>
    {
        public static ContactPairComparer Instance { get; } = new();

        public int Compare(ContactPair left, ContactPair right)
        {
            int first = left.A.Value.CompareTo(right.A.Value);
            return first != 0 ? first : left.B.Value.CompareTo(right.B.Value);
        }
    }

    private struct NarrowPhaseCallbacks : INarrowPhaseCallbacks
    {
        private readonly BepuPhysicsWorld _world;

        public NarrowPhaseCallbacks(BepuPhysicsWorld world) => _world = world;

        public void Initialize(Simulation simulation)
        {
        }

        public bool AllowContactGeneration(int workerIndex, CollidableReference a, CollidableReference b, ref float speculativeMargin)
            => a.Mobility == CollidableMobility.Dynamic || b.Mobility == CollidableMobility.Dynamic;

        public bool AllowContactGeneration(int workerIndex, CollidablePair pair, int childIndexA, int childIndexB) => true;

        public bool ConfigureContactManifold<TManifold>(
            int workerIndex,
            CollidablePair pair,
            ref TManifold manifold,
            out PairMaterialProperties pairMaterial)
            where TManifold : unmanaged, IContactManifold<TManifold>
        {
            pairMaterial = new PairMaterialProperties
            {
                FrictionCoefficient = 0.8f,
                MaximumRecoveryVelocity = 2f,
                SpringSettings = new SpringSettings(30, 1)
            };

            for (int contactIndex = 0; contactIndex < manifold.Count; contactIndex++)
            {
                if (manifold.GetDepth(ref manifold, contactIndex) >= 0)
                {
                    _world.RecordContact(pair);
                    break;
                }
            }

            return true;
        }

        public bool ConfigureContactManifold(
            int workerIndex,
            CollidablePair pair,
            int childIndexA,
            int childIndexB,
            ref ConvexContactManifold manifold) => true;

        public void Dispose()
        {
        }
    }

    private struct PoseIntegratorCallbacks : IPoseIntegratorCallbacks
    {
        private readonly Vector3 _gravity;
        private Vector3Wide _gravityWideDt;

        public PoseIntegratorCallbacks(Vector3 gravity)
        {
            _gravity = gravity;
            _gravityWideDt = default;
        }

        public AngularIntegrationMode AngularIntegrationMode => AngularIntegrationMode.Nonconserving;
        public bool AllowSubstepsForUnconstrainedBodies => false;
        public bool IntegrateVelocityForKinematics => false;

        public void Initialize(Simulation simulation)
        {
        }

        public void PrepareForIntegration(float dt) => _gravityWideDt = Vector3Wide.Broadcast(_gravity * dt);

        public void IntegrateVelocity(
            Vector<int> bodyIndices,
            Vector3Wide position,
            QuaternionWide orientation,
            BodyInertiaWide localInertia,
            Vector<int> integrationMask,
            int workerIndex,
            Vector<float> dt,
            ref BodyVelocityWide velocity)
        {
            velocity.Linear += _gravityWideDt;
        }
    }
}
