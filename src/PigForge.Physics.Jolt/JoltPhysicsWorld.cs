using System.Numerics;
using JoltPhysicsSharp;
using PigForge.Physics.Abstractions;

namespace PigForge.Physics.Jolt;

/// <summary>
/// JoltPhysicsSharp-backed implementation of <see cref="IPhysicsWorld"/>.
/// Every PigForge body is bound to exactly one native BodyID (RID) for its lifetime:
/// the mapping is locked in both directions and stale RIDs (recycled indices with a
/// different sequence number) are rejected in callbacks.
/// </summary>
public sealed class JoltPhysicsWorld : IPhysicsWorld
{
    private static readonly ObjectLayer NonMovingLayer = new(0);
    private static readonly ObjectLayer MovingLayer = new(1);

    private static readonly object FoundationLock = new();
    private static int _foundationRefCount;

    private readonly PhysicsSystem _physicsSystem;
    private readonly JobSystemThreadPool _jobSystem;
    private readonly BodyInterface _bodyInterface;
    private readonly Dictionary<PhysicsBodyId, BodyID> _ridsByBody = new();
    private readonly Dictionary<uint, PhysicsBodyId> _bodiesByRid = new();
    private readonly HashSet<uint> _dynamicRids = new();
    private readonly List<PhysicsBodyId> _bodyOrder = new();
    private readonly List<PhysicsEvent> _events = new();
    private readonly HashSet<ContactPair> _activeContacts = new();
    private readonly HashSet<ContactPair> _currentContacts = new();
    private readonly List<ContactPair> _orderedContacts = new();
    private readonly Dictionary<PhysicsJointId, TwoBodyConstraint> _joints = new();
    // Reused scratch list so destroying a body with joints stays allocation-free.
    private readonly List<PhysicsJointId> _doomedJoints = new();
    private uint _nextBodyId = 1;
    private uint _nextJointId = 1;
    private bool _disposed;

    static JoltPhysicsWorld()
    {
        // Resolve native types eagerly so a broken platform layout fails fast.
        _ = typeof(BodyID);
    }

    public JoltPhysicsWorld(PhysicsVector3 gravity)
    {
        if (!gravity.IsFinite)
        {
            throw new ArgumentOutOfRangeException(nameof(gravity), "Gravity must contain only finite values.");
        }

        AcquireNativeRuntime();

        _jobSystem = new JobSystemThreadPool(new JobSystemThreadPoolConfig
        {
            maxJobs = 2048,
            maxBarriers = 8,
            numThreads = 1
        });

        var broadPhaseLayers = new BroadPhaseLayerInterfaceTable(numObjectLayers: 2, numBroadPhaseLayers: 2);
        broadPhaseLayers.MapObjectToBroadPhaseLayer(NonMovingLayer, new BroadPhaseLayer(0));
        broadPhaseLayers.MapObjectToBroadPhaseLayer(MovingLayer, new BroadPhaseLayer(1));

        var objectLayerFilter = new ObjectLayerPairFilterTable(numObjectLayers: 2);
        objectLayerFilter.EnableCollision(MovingLayer, NonMovingLayer);
        objectLayerFilter.EnableCollision(MovingLayer, MovingLayer);

        var settings = new PhysicsSystemSettings
        {
            MaxBodies = 1024,
            MaxBodyPairs = 1024,
            MaxContactConstraints = 1024,
            BroadPhaseLayerInterface = broadPhaseLayers,
            ObjectLayerPairFilter = objectLayerFilter,
            ObjectVsBroadPhaseLayerFilter = new ObjectVsBroadPhaseLayerFilterTable(
                broadPhaseLayers,
                numBroadPhaseLayers: 2,
                objectLayerFilter,
                numObjectLayers: 2)
        };

        _physicsSystem = new PhysicsSystem(settings);
        PhysicsSettings physicsSettings = _physicsSystem.Settings;
        physicsSettings.DeterministicSimulation = true;
        _physicsSystem.Settings = physicsSettings;
        _physicsSystem.Gravity = ToVector3(gravity);
        _physicsSystem.OnContactAdded += OnContactAdded;
        _physicsSystem.OnContactPersisted += OnContactPersisted;
        _bodyInterface = _physicsSystem.BodyInterface;
    }

    public PhysicsCapabilities Capabilities { get; } = new(
        new HashSet<PhysicsJointKind> { PhysicsJointKind.Weld },
        SupportsContinuousCollision: true,
        SupportsPerBodyInertia: true,
        // Jolt owns the restitution term (see BodyCreationSettings.Restitution below), so the
        // rules layer must not synthesize a second bounce on top of it.
        AppliesRestitutionNatively: true);

    /// <summary>
    /// <see cref="BodyDefinition.MaximumAngularSpeed"/> uses zero for "unlimited", but Jolt's
    /// <c>ClampAngularVelocity</c> scales by <c>max / |w|</c>, so zero there would freeze every
    /// rotation. A finite sentinel keeps "no clamp" expressible: its square stays finite
    /// (<c>1e36</c>) and far above any real spin (a body at 1000 rad/s has <c>len_sq</c> = 1e6), so
    /// Jolt's <c>len_sq &gt; max_sq</c> test can never fire.
    /// </summary>
    private const float UnlimitedAngularSpeed = 1e18f;

    public PhysicsBodyId CreateBody(BodyDefinition definition)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(definition);
        if (definition.Constraints != PhysicsConstraintMask.None)
        {
            // The 2.5D lock (BodyDefinition.Constraints) has no mapping here yet; failing loudly
            // keeps a Jolt run from silently simulating bodies the room expects to stay planar.
            throw new NotSupportedException("JoltPhysicsSharp backend does not implement per-body constraint masks yet.");
        }

        BoxShapeDefinition shape = GetSingleBoxShape(definition);

        float minHalfExtent = Math.Min(shape.HalfExtentX, Math.Min(shape.HalfExtentY, shape.HalfExtentZ));
        var joltShape = new BoxShape(
            new Vector3(shape.HalfExtentX, shape.HalfExtentY, shape.HalfExtentZ),
            Math.Min(0.05f, minHalfExtent * 0.5f));

        bool isStatic = definition.Mode == PhysicsBodyMode.Static;
        var creationSettings = new BodyCreationSettings(
            joltShape,
            ToVector3(definition.Position),
            CreateRotation(definition.Rotation),
            isStatic ? MotionType.Static : MotionType.Dynamic,
            isStatic ? NonMovingLayer : MovingLayer)
        {
            LinearVelocity = ToVector3(definition.LinearVelocity),
            AngularVelocity = ToVector3(definition.AngularVelocity),
            // Unity's (drag, angularDrag) map straight onto Jolt's own damping, which applies the
            // same first-order factor PhysX does (`MotionProperties.inl::
            // ApplyForceTorqueAndDragInternal`: `v *= max(0, 1 - damping * dt)`, then a magnitude
            // clamp). Jolt's defaults are not PigForge's -- 0.05 damping and a 0.25*pi*60 rad/s
            // angular clamp (BodyCreationSettings.h) -- so both are always written explicitly.
            LinearDamping = definition.LinearDamping,
            AngularDamping = definition.AngularDamping,
            MaxAngularVelocity = definition.MaximumAngularSpeed > 0f ? definition.MaximumAngularSpeed : UnlimitedAngularSpeed,
            Friction = definition.Material.Friction,
            Restitution = definition.Material.Restitution,
            AllowSleeping = false
        };

        if (!isStatic)
        {
            creationSettings.OverrideMassProperties = OverrideMassProperties.CalculateInertia;
            MassProperties massProperties = creationSettings.MassPropertiesOverride;
            massProperties.Mass = definition.Mass;
            creationSettings.MassPropertiesOverride = massProperties;
        }

        BodyID rid = _bodyInterface.CreateAndAddBody(creationSettings, Activation.Activate);
        if (!rid.IsValid)
        {
            throw new InvalidOperationException("The Jolt native runtime refused to allocate a body RID.");
        }

        PhysicsBodyId id = AllocateBodyId();
        _ridsByBody.Add(id, rid);
        _bodiesByRid.Add(rid.ID, id);
        if (!isStatic)
        {
            _dynamicRids.Add(rid.ID);
        }

        _bodyOrder.Add(id);
        _events.Add(PhysicsEvent.BodyCreated(id));
        return id;
    }

    public void DestroyBody(PhysicsBodyId body)
    {
        ThrowIfDisposed();
        if (!_ridsByBody.Remove(body, out BodyID rid))
        {
            throw new KeyNotFoundException($"Physics body {body.Value} does not exist.");
        }

        DetachJointsForBody(rid);
        _bodiesByRid.Remove(rid.ID);
        _dynamicRids.Remove(rid.ID);
        _bodyInterface.RemoveAndDestroyBody(rid);
        _bodyOrder.Remove(body);
        RemoveContactsForBody(body);
        _events.Add(PhysicsEvent.BodyDestroyed(body));
    }

    public void SetBodyMass(PhysicsBodyId body, float mass)
    {
        ThrowIfDisposed();
        // Jolt could rescale the inertia itself, but the only consumer today is the boxing glove,
        // whose drive joint this backend does not build either; failing loudly keeps a Jolt run
        // from pretending the glove works (ADR-011/012's convention).
        throw new NotSupportedException("JoltPhysicsSharp backend does not implement runtime body mass changes.");
    }

    public void SetBodyCollisionEnabled(PhysicsBodyId body, bool enabled)
    {
        ThrowIfDisposed();
        throw new NotSupportedException("JoltPhysicsSharp backend does not implement runtime collider toggling.");
    }

    public PhysicsJointId CreateJoint(JointDefinition definition)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(definition);
        if (definition.Kind != PhysicsJointKind.Weld)
        {
            // Only the weld is implemented on Jolt. Anything else fails loudly instead of
            // silently degrading, exactly like the other unimplemented capabilities here.
            throw new NotSupportedException($"JoltPhysicsSharp backend only implements weld joints, not {definition.Kind}.");
        }

        if (!_ridsByBody.TryGetValue(definition.BodyA, out BodyID first)
            || !_ridsByBody.TryGetValue(definition.BodyB, out BodyID second))
        {
            throw new KeyNotFoundException("Both joint bodies must have been created by this world.");
        }

        if (!_dynamicRids.Contains(first.ID) || !_dynamicRids.Contains(second.ID))
        {
            throw new ArgumentException("Both joint bodies must be dynamic.", nameof(definition));
        }

        // SixDOFConstraint is Jolt's all-six-degrees-of-freedom constraint. Fixed axes are
        // hard, so the rigid weld is every axis fixed, the anchors are the constraint points and
        // body B's constraint axes are body A's turned by the inverse of the rest rotation, so
        // the two frames line up exactly when B sits at the rotation the pair was built with —
        // the contract's rest pose (anchors coincident, B at RestRotation inside A). Body A's
        // axes stay the identity basis (AxisX1/AxisY1/Jolt derives Z). The optional compliance
        // can only live
        // on the three translation axes here because JoltPhysicsSharp's SixDOFConstraintSettings
        // exposes JPH_SixDOFConstraintSettings.limitsSpringSettings[3] ("spring settings for
        // the translation limits"); Jolt's rotational limits have no spring, so a soft weld
        // is a translation spring with the rotations still hard — the angular compliance the
        // Bepu backend gets from Weld.SpringSettings has no Jolt equivalent. That divergence
        // is deliberate and visible, not a silent degrade of the kind.
        SixDOFConstraintSettings settings = new()
        {
            // The anchors are body-local offsets, and a Jolt body with one centred box puts
            // its centre of mass at the body origin, so they are already COM-relative. The
            // Y axis (the second constraint axis) is what the solver derives Z from.
            Space = ConstraintSpace.LocalToBodyCOM,
            Position1 = ToVector3(definition.LocalAnchorA),
            Position2 = ToVector3(definition.LocalAnchorB),
            AxisX1 = Vector3.UnitX,
            AxisY1 = Vector3.UnitY,
            AxisX2 = ToVector3(definition.RestRotation.Inverse.Rotate(new PhysicsVector3(1f, 0f, 0f))),
            AxisY2 = ToVector3(definition.RestRotation.Inverse.Rotate(new PhysicsVector3(0f, 1f, 0f))),
        };
        settings.MakeFixedAxis(SixDOFConstraintAxis.TranslationX);
        settings.MakeFixedAxis(SixDOFConstraintAxis.TranslationY);
        settings.MakeFixedAxis(SixDOFConstraintAxis.TranslationZ);
        settings.MakeFixedAxis(SixDOFConstraintAxis.RotationX);
        settings.MakeFixedAxis(SixDOFConstraintAxis.RotationY);
        settings.MakeFixedAxis(SixDOFConstraintAxis.RotationZ);
        if (definition.SpringFrequency > 0f)
        {
            // A limited axis carries the spring instead of the hard limit: zero width, one
            // frequency/damping pair shared by the three translations.
            SpringSettings spring = new(SpringMode.FrequencyAndDamping, definition.SpringFrequency, definition.SpringDampingRatio);
            for (int axis = 0; axis < (int)SixDOFConstraintAxis.NumTranslation; axis++)
            {
                settings.SetLimitedAxis((SixDOFConstraintAxis)axis, 0f, 0f);
                settings.LimitsSpringSettings[axis] = spring;
            }
        }

        BodyLockInterface locks = _physicsSystem.BodyLockInterface;
        locks.LockRead(first, out BodyLockRead firstLock);
        locks.LockRead(second, out BodyLockRead secondLock);
        TwoBodyConstraint constraint;
        try
        {
            Body? bodyA = firstLock.Body;
            Body? bodyB = secondLock.Body;
            if (bodyA is null || bodyB is null)
            {
                throw new InvalidOperationException("The Jolt native runtime no longer owns a jointed body.");
            }

            constraint = settings.CreateConstraint(bodyA, bodyB);
        }
        finally
        {
            locks.UnlockRead(secondLock);
            locks.UnlockRead(firstLock);
        }

        _physicsSystem.AddConstraint(constraint);
        if (_nextJointId == 0)
        {
            throw new InvalidOperationException("The physics joint ID space is exhausted.");
        }

        PhysicsJointId id = new(_nextJointId++);
        _joints.Add(id, constraint);
        return id;
    }

    public void DestroyJoint(PhysicsJointId joint)
    {
        ThrowIfDisposed();
        if (!joint.IsValid)
        {
            throw new ArgumentException("A joint ID must be valid.", nameof(joint));
        }

        // An unknown joint is a no-op, exactly like the Bepu backend.
        if (_joints.Remove(joint, out TwoBodyConstraint? constraint))
        {
            DestroyConstraint(constraint);
        }
    }

    /// <summary>Removes every joint that still references a body about to be destroyed, so the
    /// native solver never keeps a constraint whose end has been freed.</summary>
    private void DetachJointsForBody(BodyID rid)
    {
        _doomedJoints.Clear();
        foreach ((PhysicsJointId jointId, TwoBodyConstraint constraint) in _joints)
        {
            if (constraint.Body1.ID == rid || constraint.Body2.ID == rid)
            {
                _doomedJoints.Add(jointId);
            }
        }

        for (int index = 0; index < _doomedJoints.Count; index++)
        {
            TwoBodyConstraint constraint = _joints[_doomedJoints[index]];
            _joints.Remove(_doomedJoints[index]);
            DestroyConstraint(constraint);
        }

        _doomedJoints.Clear();
    }

    private void DestroyConstraint(TwoBodyConstraint constraint)
    {
        _physicsSystem.RemoveConstraint(constraint);
        constraint.Dispose();
    }

    public void ApplyCommands(ReadOnlySpan<PhysicsCommand> commands)
    {
        ThrowIfDisposed();
        for (int index = 0; index < commands.Length; index++)
        {
            PhysicsCommand command = commands[index];
            if (command.Kind == PhysicsCommandKind.SuppressContact)
            {
                // Jolt owns the restitution term (see Capabilities.AppliesRestitutionNatively), so
                // the rules layer never sends this: a bounce is the solver's own business here.
                throw new NotSupportedException(
                    "Jolt applies restitution in its solver, so contact suppression is never requested.");
            }

            if (command.Kind != PhysicsCommandKind.ApplyImpulse)
            {
                throw new ArgumentOutOfRangeException(nameof(commands), command.Kind, "Unknown physics command kind.");
            }

            if (!_ridsByBody.TryGetValue(command.Body, out BodyID rid) || !_dynamicRids.Contains(rid.ID))
            {
                throw new KeyNotFoundException($"Impulse target body {command.Body.Value} does not exist or is not dynamic.");
            }

            // Sleeping bodies ignore impulses; activate first.
            _bodyInterface.ActivateBody(rid);
            _bodyInterface.AddImpulse(rid, ToVector3(command.Impulse), ToVector3(command.WorldPoint));
        }
    }

    public void Step(FixedTimeStep timeStep)
    {
        ThrowIfDisposed();
        _currentContacts.Clear();
        PhysicsUpdateError error = _physicsSystem.Update(timeStep.Seconds, collisionSteps: 1, _jobSystem);
        if (error != PhysicsUpdateError.None)
        {
            throw new InvalidOperationException($"The Jolt physics update failed with {error}.");
        }

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
            BodyID rid = _ridsByBody[id];
            Vector3 linearVelocity = _dynamicRids.Contains(rid.ID)
                ? _bodyInterface.GetLinearVelocity(rid)
                : Vector3.Zero;
            Vector3 angularVelocity = _dynamicRids.Contains(rid.ID)
                ? _bodyInterface.GetAngularVelocity(rid)
                : Vector3.Zero;

            destination[index] = new(
                id,
                FromVector3(_bodyInterface.GetPosition(rid)),
                FromQuaternion(_bodyInterface.GetRotation(rid)),
                FromVector3(linearVelocity),
                FromVector3(angularVelocity));
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
        _physicsSystem.OnContactAdded -= OnContactAdded;
        _physicsSystem.OnContactPersisted -= OnContactPersisted;
        foreach (TwoBodyConstraint constraint in _joints.Values)
        {
            DestroyConstraint(constraint);
        }

        _joints.Clear();
        ((IDisposable)_physicsSystem).Dispose();
        _jobSystem.Dispose();
        _ridsByBody.Clear();
        _bodiesByRid.Clear();
        _dynamicRids.Clear();
        _bodyOrder.Clear();
        _events.Clear();
        _activeContacts.Clear();
        _currentContacts.Clear();
        _orderedContacts.Clear();
        ReleaseNativeRuntime();
    }

    private void OnContactAdded(PhysicsSystem system, in Body body1, in Body body2, in ContactManifold manifold, ref ContactSettings settings)
        => RecordContact(body1, body2);

    private void OnContactPersisted(PhysicsSystem system, in Body body1, in Body body2, in ContactManifold manifold, ref ContactSettings settings)
        => RecordContact(body1, body2);

    private void RecordContact(Body body1, Body body2)
    {
        BodyID ridA = body1.ID;
        BodyID ridB = body2.ID;
        if (!_bodiesByRid.TryGetValue(ridA.ID, out PhysicsBodyId idA)
            || !_bodiesByRid.TryGetValue(ridB.ID, out PhysicsBodyId idB)
            || idA == idB)
        {
            return;
        }

        _currentContacts.Add(ContactPair.Create(idA, idB));
    }

    private static void AcquireNativeRuntime()
    {
        lock (FoundationLock)
        {
            if (_foundationRefCount == 0)
            {
                try
                {
                    if (!Foundation.Init(doublePrecision: false))
                    {
                        throw new InvalidOperationException("The Jolt native runtime (joltc.dll) failed to initialize.");
                    }
                }
                catch (DllNotFoundException exception)
                {
                    throw new InvalidOperationException(
                        "The Jolt native runtime (joltc.dll) is missing. Install the JoltPhysicsSharp native package for the current platform (runtimes/win-x64/native).",
                        exception);
                }
                catch (BadImageFormatException exception)
                {
                    throw new InvalidOperationException(
                        "The Jolt native runtime (joltc.dll) does not match the process architecture. Use the win-x64 native runtime on a 64-bit Windows process.",
                        exception);
                }
                catch (EntryPointNotFoundException exception)
                {
                    throw new InvalidOperationException(
                        "The installed Jolt native runtime (joltc.dll) is incompatible with the managed JoltPhysicsSharp binding.",
                        exception);
                }
            }

            _foundationRefCount++;
        }
    }

    private static void ReleaseNativeRuntime()
    {
        lock (FoundationLock)
        {
            _foundationRefCount--;
            if (_foundationRefCount == 0)
            {
                Foundation.Shutdown();
            }
        }
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
            throw new NotSupportedException("Jolt backend currently supports exactly one box shape per body.");
        }

        return box;
    }

    private static Quaternion CreateRotation(PhysicsQuaternion rotation)
    {
        Quaternion orientation = ToQuaternion(rotation);
        float lengthSquared = orientation.LengthSquared();
        if (!float.IsFinite(lengthSquared) || lengthSquared <= float.Epsilon)
        {
            throw new ArgumentException("A body rotation must have a finite, non-zero quaternion.", nameof(rotation));
        }

        return Quaternion.Normalize(orientation);
    }

    private static Vector3 ToVector3(PhysicsVector3 value) => new(value.X, value.Y, value.Z);

    private static Quaternion ToQuaternion(PhysicsQuaternion value) => new(value.X, value.Y, value.Z, value.W);

    private static PhysicsVector3 FromVector3(Vector3 value) => new(value.X, value.Y, value.Z);

    private static PhysicsQuaternion FromQuaternion(Quaternion value) => new(value.X, value.Y, value.Z, value.W);

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
}
