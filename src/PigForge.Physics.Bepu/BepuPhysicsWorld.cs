using System.Numerics;
using BepuPhysics;
using BepuPhysics.Collidables;
using BepuPhysics.CollisionDetection;
using BepuPhysics.Constraints;
using BepuUtilities;
using BepuUtilities.Memory;
using BepuCompoundChild = BepuPhysics.Collidables.CompoundChild;
using PigForge.Physics.Abstractions;

namespace PigForge.Physics.Bepu;

public sealed class BepuPhysicsWorld : IPhysicsWorld
{
    private readonly BufferPool _bufferPool;
    private readonly Simulation _simulation;
    private readonly Dictionary<PhysicsBodyId, BodyHandle> _dynamicBodies = new();
    private readonly Dictionary<PhysicsBodyId, StaticHandle> _staticBodies = new();
    private readonly Dictionary<PhysicsBodyId, TypedIndex> _shapesByBody = new();
    private readonly Dictionary<TypedIndex, List<TypedIndex>> _childShapesByCompound = new();
    private readonly Dictionary<int, float> _frictionByDynamicHandle = new();
    private readonly Dictionary<int, float> _frictionByStaticHandle = new();
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
        PhysicsBodyId id = AllocateBodyId();
        RigidPose pose = CreatePose(definition.Position, definition.Rotation);

        TypedIndex shapeIndex;
        BodyInertia inertia;
        BodyActivityDescription activity;
        if (definition.Shapes.Count == 1 && definition.Shapes[0] is BoxShapeDefinition box)
        {
            Box physicsShape = new(box.HalfExtentX * 2, box.HalfExtentY * 2, box.HalfExtentZ * 2);
            shapeIndex = _simulation.Shapes.Add(physicsShape);
            inertia = physicsShape.ComputeInertia(definition.Mass);
            activity = BodyDescription.GetDefaultActivity(physicsShape);
        }
        else if (definition.Shapes.Count == 1 && definition.Shapes[0] is SphereShapeDefinition sphere)
        {
            Sphere physicsShape = new(sphere.Radius);
            shapeIndex = _simulation.Shapes.Add(physicsShape);
            inertia = physicsShape.ComputeInertia(definition.Mass);
            activity = BodyDescription.GetDefaultActivity(physicsShape);
        }
        else if (definition.Shapes.Count == 1 && definition.Shapes[0] is CompoundShapeDefinition compound)
        {
            if (definition.Mode == PhysicsBodyMode.Static)
            {
                throw new NotSupportedException("BepuPhysics backend does not support static compound bodies yet.");
            }

            shapeIndex = BuildCompoundShape(compound, definition.Mass, out inertia);
            activity = new BodyActivityDescription(0.01f);
        }
        else
        {
            throw new NotSupportedException("BepuPhysics backend supports exactly one box, sphere, or compound shape per body.");
        }

        if (definition.Mode == PhysicsBodyMode.Static)
        {
            StaticHandle handle = _simulation.Statics.Add(new StaticDescription(pose, shapeIndex));
            _staticBodies.Add(id, handle);
            _staticIdsByHandle.Add(handle.Value, id);
            _frictionByStaticHandle.Add(handle.Value, definition.Material.Friction);
        }
        else
        {
            BodyDescription body = BodyDescription.CreateDynamic(
                pose,
                new BodyVelocity(ToNumerics(definition.LinearVelocity), ToNumerics(definition.AngularVelocity)),
                inertia,
                new CollidableDescription(shapeIndex),
                activity);
            BodyHandle handle = _simulation.Bodies.Add(body);
            _dynamicBodies.Add(id, handle);
            _dynamicIdsByHandle.Add(handle.Value, id);
            _frictionByDynamicHandle.Add(handle.Value, definition.Material.Friction);
            // Sleeping bodies would ignore impulses, and waking via the BodyReference
            // setter corrupts solver state; keep dynamics always awake.
            _simulation.Bodies[handle].Activity.SleepThreshold = -1f;
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
            _frictionByDynamicHandle.Remove(dynamicHandle.Value);
            _simulation.Bodies.Remove(dynamicHandle);
        }
        else if (_staticBodies.Remove(body, out StaticHandle staticHandle))
        {
            _staticIdsByHandle.Remove(staticHandle.Value);
            _frictionByStaticHandle.Remove(staticHandle.Value);
            _simulation.Statics.Remove(staticHandle);
        }
        else
        {
            throw new KeyNotFoundException($"Physics body {body.Value} does not exist.");
        }

        if (_shapesByBody.Remove(body, out TypedIndex shapeIndex))
        {
            _simulation.Shapes.RemoveAndDispose(shapeIndex, _bufferPool);
            if (_childShapesByCompound.Remove(shapeIndex, out List<TypedIndex>? childShapes))
            {
                foreach (TypedIndex childShape in childShapes)
                {
                    _simulation.Shapes.Remove(childShape);
                }
            }
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
            // Wake BEFORE writing velocity: waking after ApplyImpulse corrupts the
            // sleeping body's integration state (observed garbage velocities).
            body.Awake = true;
            Vector3 impulse = ToNumerics(command.Impulse);
            Vector3 offset = ToNumerics(command.WorldPoint) - body.Pose.Position;
            body.ApplyImpulse(impulse, offset);
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
        _childShapesByCompound.Clear();
        _frictionByDynamicHandle.Clear();
        _frictionByStaticHandle.Clear();
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

    internal float CombineFriction(CollidablePair pair)
    {
        float frictionA = GetMaterialFriction(pair.A);
        float frictionB = GetMaterialFriction(pair.B);
        return (frictionA + frictionB) * 0.5f;
    }

    private float GetMaterialFriction(CollidableReference reference) => reference.Mobility == CollidableMobility.Static
        ? _frictionByStaticHandle.GetValueOrDefault(reference.StaticHandle.Value, PhysicsMaterial.Default.Friction)
        : _frictionByDynamicHandle.GetValueOrDefault(reference.BodyHandle.Value, PhysicsMaterial.Default.Friction);

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

    /// <summary>
    /// Builds a Compound from the definition's children. <see cref="CompoundBuilder.BuildDynamicCompound"/>
    /// recentres children onto the assembly centre of mass; callers should pose the body
    /// at that centre so snapshots report the centre-of-mass frame. Child shapes are
    /// registered in the shape collection and removed with the compound.
    /// Source: https://github.com/bepu/bepuphysics2/blob/master/BepuPhysics/Collidables/CompoundBuilder.cs
    /// </summary>
    private TypedIndex BuildCompoundShape(CompoundShapeDefinition compound, float totalMass, out BodyInertia inertia)
    {
        int count = compound.Children.Count;
        Span<float> childMasses = stackalloc float[count];
        float totalVolume = 0f;
        for (int index = 0; index < count; index++)
        {
            float volume = BoxVolume(compound.Children[index].Shape);
            childMasses[index] = volume;
            totalVolume += volume;
        }

        if (totalVolume <= 0f)
        {
            throw new ArgumentOutOfRangeException(nameof(compound), "A compound requires positive child volume.");
        }

        for (int index = 0; index < count; index++)
        {
            childMasses[index] *= totalMass / totalVolume;
        }

        var builder = new CompoundBuilder(_bufferPool, _simulation.Shapes, count);
        try
        {
            for (int index = 0; index < count; index++)
            {
                Abstractions.CompoundChild child = compound.Children[index];
                Box box = BoxFor(child.Shape);
                RigidPose pose = new(ToNumerics(child.Offset), ToNumerics(child.Rotation));
                builder.Add(in box, in pose, childMasses[index]);
            }

            builder.BuildDynamicCompound(out Buffer<BepuCompoundChild> children, out inertia, out _);
            List<TypedIndex> childShapes = new(count);
            for (int index = 0; index < count; index++)
            {
                childShapes.Add(children[index].ShapeIndex);
            }

            TypedIndex compoundIndex = _simulation.Shapes.Add(new Compound(children));
            _childShapesByCompound.Add(compoundIndex, childShapes);
            return compoundIndex;
        }
        finally
        {
            builder.Dispose();
        }
    }

    private static Box BoxFor(ShapeDefinition shape) => shape switch
    {
        BoxShapeDefinition box => new Box(box.HalfExtentX * 2, box.HalfExtentY * 2, box.HalfExtentZ * 2),
        _ => throw new NotSupportedException("BepuPhysics compound children must be boxes for now.")
    };

    private static float BoxVolume(ShapeDefinition shape) => shape switch
    {
        BoxShapeDefinition box => 8f * box.HalfExtentX * box.HalfExtentY * box.HalfExtentZ,
        _ => throw new NotSupportedException("BepuPhysics compound children must be boxes for now.")
    };

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
                // Per-body friction from content, combined as the pair average.
                // BepuPhysics v2 has no restitution support; see PhysicsMaterial docs.
                FrictionCoefficient = _world.CombineFriction(pair),
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
