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
    private readonly Dictionary<int, PhysicsMaterial> _materialByDynamicHandle = new();
    private readonly Dictionary<int, PhysicsMaterial> _materialByStaticHandle = new();
    // A hinged wheel's body and its parent are one mechanism: the original keeps a wheel's
    // support collider and its tire on the same rigid body, and after the split that keeps
    // wheels spinning (see ADR-009) the two overlap on purpose. Contacts between them would
    // be a permanent, deeply penetrating collision. Only revolute (wheel) joints suppress:
    // the original's runtime ropes are `SpringJoint`s and never call `Physics.IgnoreCollision`
    // (Sandbag.cs:136-164, Balloon.cs:143-166), so a sandbag or balloon collides with the part
    // it is tied to, while a frame-enclosed pair is one body and cannot collide by construction.
    private readonly HashSet<long> _jointedPairs = new();
    // Pairs whose contact must be skipped for the step a bounce is delivered, so the injected
    // separation speed is not pulled back to the contact's own velocity goal. Cleared every tick.
    private readonly HashSet<long> _suppressedPairs = new();
    private readonly Dictionary<int, PhysicsBodyId> _dynamicIdsByHandle = new();
    private readonly Dictionary<int, PhysicsBodyId> _staticIdsByHandle = new();
    // Per-body frozen degrees of freedom, indexed by BodyHandle.Value. The pose integrator
    // zeroes exactly these velocity components every substep, so a body can never leave the
    // build plane (the original's `RigidbodyConstraints`, see BodyDefinition.Constraints).
    private PhysicsConstraintMask[] _constraintsByHandle = new PhysicsConstraintMask[64];
    // The Z a frozen body was created at: the original's freeze pins the degree of freedom at
    // the value it had when the constraint was applied, and a body is born in the build plane.
    private float[] _frozenPositionZByHandle = new float[64];
    // Per-body damping and angular clamp, indexed by BodyHandle.Value like the constraints above.
    // Bepu 2.4.0's BodyDescription carries neither, so the pose integrator applies them from this
    // table with the original's own formulas (see BodyDefinition.LinearDamping).
    private BodyMotionSettings[] _motionByHandle = new BodyMotionSettings[64];
    private readonly List<PhysicsBodyId> _bodyOrder = new();
    private readonly List<PhysicsEvent> _events = new();
    private readonly HashSet<ContactPair> _activeContacts = new();
    private readonly HashSet<ContactPair> _currentContacts = new();
    // Pre-solve impact data per contact pair, filled by the narrow phase and consumed when
    // the contact events are emitted. Cleared at the start of every step.
    private readonly Dictionary<ContactPair, ContactImpact> _contactImpacts = new();
    private readonly List<ContactPair> _orderedContacts = new();
    // One PigForge joint may own several solver constraints: a sprung wheel is an angular
    // hinge plus a rigid line lock plus the spring itself (see CreateJoint).
    private readonly Dictionary<PhysicsJointId, ConstraintHandle[]> _joints = new();
    private readonly List<(PhysicsJointId Joint, BodyHandle A, BodyHandle B)> _jointBodies = new();
    private uint _nextBodyId = 1;
    private uint _nextJointId = 1;
    private bool _disposed;

    /// <summary>Bepu's own stiff spring (30 Hz, critically damped): what a constraint that
    /// must not visibly give uses, and the default the contact material builds as well.</summary>
    private static readonly SpringSettings RigidSpring = new(30f, 1f);

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
            new PoseIntegratorCallbacks(this, ToNumerics(gravity)),
            new SolveDescription(8, 1));
    }

    public PhysicsCapabilities Capabilities { get; } = new(
        new HashSet<PhysicsJointKind> { PhysicsJointKind.Revolute, PhysicsJointKind.Distance, PhysicsJointKind.Weld },
        SupportsContinuousCollision: false,
        SupportsPerBodyInertia: true,
        // BepuPhysics v2 has no restitution term in PairMaterialProperties, so the rules
        // layer synthesizes the bounce from the reported contact impact instead.
        AppliesRestitutionNatively: false);

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
            _materialByStaticHandle.Add(handle.Value, definition.Material);
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
            _materialByDynamicHandle.Add(handle.Value, definition.Material);
            SetMotion(handle.Value, definition);
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
            for (int index = _jointBodies.Count - 1; index >= 0; index--)
            {
                (PhysicsJointId jointId, BodyHandle jointA, BodyHandle jointB) = _jointBodies[index];
                if (jointA != dynamicHandle && jointB != dynamicHandle)
                {
                    continue;
                }

                if (_joints.Remove(jointId, out ConstraintHandle[]? constraints))
                {
                    foreach (ConstraintHandle constraint in constraints)
                    {
                        _simulation.Solver.Remove(constraint);
                    }
                }

                _jointedPairs.Remove(PairKey(jointA, jointB));
                _jointBodies.RemoveAt(index);
            }

            _dynamicIdsByHandle.Remove(dynamicHandle.Value);
            _materialByDynamicHandle.Remove(dynamicHandle.Value);
            SetConstraints(dynamicHandle.Value, PhysicsConstraintMask.None, 0f);
            _simulation.Bodies.Remove(dynamicHandle);
        }
        else if (_staticBodies.Remove(body, out StaticHandle staticHandle))
        {
            _staticIdsByHandle.Remove(staticHandle.Value);
            _materialByStaticHandle.Remove(staticHandle.Value);
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
        if (definition.Kind is not (PhysicsJointKind.Revolute or PhysicsJointKind.Distance or PhysicsJointKind.Weld))
        {
            throw new NotSupportedException($"BepuPhysics backend only implements revolute, distance and weld joints, not {definition.Kind}.");
        }

        if (!_dynamicBodies.TryGetValue(definition.BodyA, out BodyHandle handleA)
            || !_dynamicBodies.TryGetValue(definition.BodyB, out BodyHandle handleB))
        {
            throw new KeyNotFoundException("Both joint bodies must be dynamic bodies created by this world.");
        }

        ConstraintHandle[] handles;
        if (definition.Kind == PhysicsJointKind.Revolute)
        {
            handles = definition.LocalSuspensionAxis == PhysicsVector3.Zero
                ? new[] { AddRigidHinge(handleA, handleB, definition) }
                : AddSprungWheel(handleA, handleB, definition);
        }
        else if (definition.Kind == PhysicsJointKind.Weld)
        {
            // BepuPhysics.Constraints.Weld constrains all six degrees of freedom at once and
            // carries its own SpringSettings covering both the position and the orientation
            // part (BepuPhysics 2.4 Weld: LocalOffset, LocalOrientation, SpringSettings), so
            // the optional compliance needs no extra constraint. The rest pose is the one the
            // definition describes: the two local anchors are the same world point expressed in
            // each body's frame (Unity's AddFixedJoint builds them that way), so B's origin in
            // A's frame is anchorA - RestRotation * anchorB -- subtracting the anchors directly
            // is only right while the rest rotation is identity -- and B's frame sits at
            // RestRotation inside A's (Bepu's LocalOrientation is exactly "target orientation of
            // body B in body A's local space"), which is what a quarter-turn-placed frame pair
            // needs. A zero frequency is the same 30 Hz rigid
            // spring the other rigid joints use; a positive one is the definition's
            // frequency/damping pair directly.
            Weld weld = new()
            {
                LocalOffset = ToNumerics(definition.LocalAnchorA - definition.RestRotation.Rotate(definition.LocalAnchorB)),
                LocalOrientation = ToNumerics(definition.RestRotation),
                SpringSettings = definition.SpringFrequency > 0f
                    ? new SpringSettings(definition.SpringFrequency, definition.SpringDampingRatio)
                    : RigidSpring,
            };
            handles = new[] { _simulation.Solver.Add(handleA, handleB, weld) };
        }
        else
        {
            // The rope of a runtime attachment (balloon string, sandbag tie): the two anchors
            // may move freely between the minimum and the maximum separation, pulled back by
            // the spring. Unity's SpringJoint (min/max/spring/damper) maps onto exactly this.
            DistanceLimit limit = new()
            {
                LocalOffsetA = ToNumerics(definition.LocalAnchorA),
                LocalOffsetB = ToNumerics(definition.LocalAnchorB),
                MinimumDistance = definition.MinimumDistance,
                MaximumDistance = definition.MaximumDistance,
                SpringSettings = new SpringSettings(definition.SpringFrequency, definition.SpringDampingRatio),
            };
            handles = new[] { _simulation.Solver.Add(handleA, handleB, limit) };
        }

        if (_nextJointId == 0)
        {
            throw new InvalidOperationException("The physics joint ID space is exhausted.");
        }

        PhysicsJointId id = new(_nextJointId++);
        _joints.Add(id, handles);
        _jointBodies.Add((id, handleA, handleB));
        // Only the wheel hinge suppresses contacts between its ends: the tire sphere and the
        // support box that rides the parent overlap on purpose (ADR-009). The runtime ropes
        // (balloon string, sandbag tie) are Unity SpringJoints whose pair still collides, and
        // a weld's pair collides too (the original's adjacent parts are joint plus contact,
        // see the weld-compliance spec's decision 3).
        if (definition.Kind == PhysicsJointKind.Revolute)
        {
            _jointedPairs.Add(PairKey(handleA, handleB));
        }

        return id;
    }

    /// <summary>Order-independent key for a pair of jointed bodies.</summary>
    private static long PairKey(BodyHandle left, BodyHandle right) =>
        left.Value <= right.Value
            ? ((long)left.Value << 32) | (uint)right.Value
            : ((long)right.Value << 32) | (uint)left.Value;

    /// <summary>
    /// The rigid revolute attachment: a hinge keeps the two anchors coincident and lets the
    /// bodies rotate about the shared axis, with stiff settings so it behaves as a rigid axle.
    /// Its linear part is one rigid point constraint, so it cannot express compliance — a
    /// sprung wheel takes <see cref="AddSprungWheel"/> instead.
    /// </summary>
    private ConstraintHandle AddRigidHinge(BodyHandle handleA, BodyHandle handleB, JointDefinition definition)
    {
        Hinge hinge = new()
        {
            LocalOffsetA = ToNumerics(definition.LocalAnchorA),
            LocalOffsetB = ToNumerics(definition.LocalAnchorB),
            LocalHingeAxisA = ToNumerics(PhysicsVector3.Normalize(definition.LocalAxisA)),
            LocalHingeAxisB = ToNumerics(PhysicsVector3.Normalize(definition.LocalAxisB)),
            SpringSettings = RigidSpring,
        };
        return _simulation.Solver.Add(handleA, handleB, hinge);
    }

    /// <summary>
    /// The elastic wheel attachment. The wheel spins about the axle and its centre may
    /// translate along the suspension axis (body A's <see cref="JointDefinition.LocalSuspensionAxis"/>,
    /// perpendicular to that axle) against the spring, while the other two translations stay
    /// rigid — the original's ConfigurableJoint with Locked angular XYZ, Locked x/z,
    /// Limited y and a linear-limit spring at a zero offset (OffRoadWheel.cs:202-220).
    /// <para>
    /// Built from three constraints because no single Bepu constraint has that shape:
    /// an <see cref="AngularHinge"/> for the spin, a <see cref="PointOnLineServo"/> pinning
    /// B's anchor to the line through A's anchor along the axis (the rigid x/z locks), and a
    /// <see cref="LinearAxisServo"/> for the sprung axial degree of freedom. Both servos use
    /// <see cref="ServoSettings.Default"/>: unlimited speed and force, so what is left is the
    /// spring itself, with the accumulated impulse free to push either way.
    /// </para>
    /// </summary>
    private ConstraintHandle[] AddSprungWheel(BodyHandle handleA, BodyHandle handleB, JointDefinition definition)
    {
        // Only A's frame carries the axis: it must not ride the wheel's spin (B is the wheel).
        Vector3 axis = ToNumerics(PhysicsVector3.Normalize(definition.LocalSuspensionAxis));
        Vector3 anchorA = ToNumerics(definition.LocalAnchorA);
        Vector3 anchorB = ToNumerics(definition.LocalAnchorB);

        AngularHinge spin = new()
        {
            LocalHingeAxisA = ToNumerics(PhysicsVector3.Normalize(definition.LocalAxisA)),
            LocalHingeAxisB = ToNumerics(PhysicsVector3.Normalize(definition.LocalAxisB)),
            SpringSettings = RigidSpring,
        };

        PointOnLineServo lateralLock = new()
        {
            LocalOffsetA = anchorA,
            LocalOffsetB = anchorB,
            LocalDirection = axis,
            ServoSettings = ServoSettings.Default,
            SpringSettings = RigidSpring,
        };

        LinearAxisServo suspension = new()
        {
            LocalOffsetA = anchorA,
            LocalOffsetB = anchorB,
            LocalPlaneNormal = axis,
            TargetOffset = definition.SuspensionRestOffset,
            ServoSettings = ServoSettings.Default,
            SpringSettings = new SpringSettings(definition.SpringFrequency, definition.SpringDampingRatio),
        };

        return new[]
        {
            _simulation.Solver.Add(handleA, handleB, spin),
            _simulation.Solver.Add(handleA, handleB, lateralLock),
            _simulation.Solver.Add(handleA, handleB, suspension),
        };
    }

    /// <summary>Statics get their own key space so a dynamic-vs-static pair can be keyed too —
    /// a bounce pairs the pig with the floor, and the floor is static.</summary>
    private const long StaticKeyBit = 1L << 40;

    private static long CollidableKey(in CollidableReference reference) =>
        reference.Mobility == CollidableMobility.Static
            ? StaticKeyBit | (uint)reference.StaticHandle.Value
            : reference.BodyHandle.Value;

    private static long CollidablePairKey(in CollidableReference left, in CollidableReference right)
    {
        long first = CollidableKey(left);
        long second = CollidableKey(right);
        return first <= second
            ? (first << 32) | (uint)second
            : (second << 32) | (uint)first;
    }

    /// <summary>Maps a PigForge body id into the same key space as <see cref="CollidableKey"/>,
    /// so a suppression queued from rule commands matches the collidables the narrow phase sees.</summary>
    private bool TryCollidableKey(PhysicsBodyId body, out long key)
    {
        if (_dynamicBodies.TryGetValue(body, out BodyHandle dynamicHandle))
        {
            key = dynamicHandle.Value;
            return true;
        }

        if (_staticBodies.TryGetValue(body, out StaticHandle staticHandle))
        {
            key = StaticKeyBit | (uint)staticHandle.Value;
            return true;
        }

        key = 0;
        return false;
    }

    /// <summary>True when two collidables are the two ends of a PigForge joint.</summary>
    private bool AreJointed(in CollidableReference left, in CollidableReference right)
    {
        if (left.Mobility != CollidableMobility.Dynamic || right.Mobility != CollidableMobility.Dynamic)
        {
            return false;
        }

        return _jointedPairs.Contains(PairKey(left.BodyHandle, right.BodyHandle));
    }

    /// <summary>True when the pair's contact must be skipped for this step (see
    /// <see cref="PhysicsCommandKind.SuppressContact"/>). A bounce pairs a dynamic part with
    /// static terrain, so unlike <see cref="AreJointed"/> this must not require two dynamics.</summary>
    private bool IsSuppressed(in CollidableReference left, in CollidableReference right) =>
        _suppressedPairs.Contains(CollidablePairKey(left, right));

    /// <summary>Records a body's frozen degrees of freedom for the pose integrator, growing the
    /// handle-indexed table as Bepu reuses low handles.</summary>
    private void SetConstraints(int handle, PhysicsConstraintMask constraints, float positionZ)
    {
        if (handle >= _constraintsByHandle.Length)
        {
            int capacity = _constraintsByHandle.Length;
            while (capacity <= handle)
            {
                capacity *= 2;
            }

            Array.Resize(ref _constraintsByHandle, capacity);
            Array.Resize(ref _frozenPositionZByHandle, capacity);
            Array.Resize(ref _motionByHandle, capacity);
        }

        _constraintsByHandle[handle] = constraints;
        _frozenPositionZByHandle[handle] = positionZ;
    }

    /// <summary>Records a dynamic body's damping and angular clamp for the pose integrator.</summary>
    private void SetMotion(int handle, BodyDefinition definition)
    {
        SetConstraints(handle, definition.Constraints, definition.Position.Z);
        _motionByHandle[handle] = new BodyMotionSettings
        {
            LinearDamping = definition.LinearDamping,
            AngularDamping = definition.AngularDamping,
            MaximumAngularSpeed = definition.MaximumAngularSpeed,
        };
    }

    /// <summary>The mask the pose integrator applies to one body handle; none when the handle
    /// was never seen (a static body, or a freed dynamic slot).</summary>
    private PhysicsConstraintMask ConstraintsOf(int handle) =>
        (uint)handle < (uint)_constraintsByHandle.Length ? _constraintsByHandle[handle] : PhysicsConstraintMask.None;

    /// <summary>One body's damping and angular clamp; all zeros when the handle was never seen
    /// (a static body, or a freed dynamic slot), which the integrator reads as "no damping and no
    /// clamp" — the same out-of-range tolerance <see cref="ConstraintsOf"/> gives.</summary>
    private BodyMotionSettings MotionOf(int handle) =>
        (uint)handle < (uint)_motionByHandle.Length ? _motionByHandle[handle] : default;

    /// <summary>
    /// The per-body dynamics Bepu 2.4.0's <c>BodyDescription</c> cannot carry: Unity's
    /// <c>Rigidbody.drag</c> / <c>angularDrag</c> and its <c>maxAngularVelocity</c>
    /// (<see cref="BodyDefinition.LinearDamping"/>).
    /// </summary>
    private struct BodyMotionSettings
    {
        public float LinearDamping;
        public float AngularDamping;
        public float MaximumAngularSpeed;
    }

    /// <summary>
    /// The last word on a frozen degree of freedom. The pose integrator already drops the locked
    /// velocity components before the pose moves, but a solver constraint (a rope or a contact)
    /// writes them back during the step, and a non-conserving orientation integration can then
    /// rotate a body by a fraction of a degree over hundreds of ticks. The original snaps those
    /// axes instead of trusting the solver (`RigidbodyRotationConstraints` restores the locked
    /// components of the rotation every `LateUpdate`), so this does the same once per step:
    /// locked axis values return to what the body was created with, everything else is untouched.
    /// Bodies are walked in the world's own creation order, so the pass stays deterministic.
    /// </summary>
    private void ApplyFrozenDegreesOfFreedom()
    {
        for (int index = 0; index < _bodyOrder.Count; index++)
        {
            if (!_dynamicBodies.TryGetValue(_bodyOrder[index], out BodyHandle handle))
            {
                continue;
            }

            PhysicsConstraintMask constraints = ConstraintsOf(handle.Value);
            if (constraints == PhysicsConstraintMask.None)
            {
                continue;
            }

            BodyReference body = _simulation.Bodies[handle];
            BodyVelocity velocity = body.Velocity;
            Vector3 position = body.Pose.Position;
            Quaternion orientation = body.Pose.Orientation;
            if ((constraints & PhysicsConstraintMask.LockPositionX) != 0)
            {
                velocity.Linear.X = 0f;
            }

            if ((constraints & PhysicsConstraintMask.LockPositionY) != 0)
            {
                velocity.Linear.Y = 0f;
            }

            if ((constraints & PhysicsConstraintMask.LockPositionZ) != 0)
            {
                velocity.Linear.Z = 0f;
                position.Z = _frozenPositionZByHandle[handle.Value];
            }

            if ((constraints & PhysicsConstraintMask.LockRotationX) != 0)
            {
                velocity.Angular.X = 0f;
                orientation.X = 0f;
            }

            if ((constraints & PhysicsConstraintMask.LockRotationY) != 0)
            {
                velocity.Angular.Y = 0f;
                orientation.Y = 0f;
            }

            if ((constraints & PhysicsConstraintMask.LockRotationZ) != 0)
            {
                velocity.Angular.Z = 0f;
                orientation.Z = 0f;
            }

            if ((constraints & (PhysicsConstraintMask.LockRotationX | PhysicsConstraintMask.LockRotationY | PhysicsConstraintMask.LockRotationZ)) != 0)
            {
                orientation = Quaternion.Normalize(orientation);
            }

            body.Velocity = velocity;
            body.Pose = new RigidPose(position, orientation);
        }
    }

    public void DestroyJoint(PhysicsJointId joint)
    {
        ThrowIfDisposed();
        if (!joint.IsValid)
        {
            throw new ArgumentException("A joint ID must be valid.", nameof(joint));
        }

        if (_joints.Remove(joint, out ConstraintHandle[]? handles))
        {
            foreach (ConstraintHandle handle in handles)
            {
                _simulation.Solver.Remove(handle);
            }

            for (int index = _jointBodies.Count - 1; index >= 0; index--)
            {
                (PhysicsJointId jointId, BodyHandle jointA, BodyHandle jointB) = _jointBodies[index];
                if (jointId != joint)
                {
                    continue;
                }

                _jointedPairs.Remove(PairKey(jointA, jointB));
                _jointBodies.RemoveAt(index);
            }
        }
    }

    public void ApplyCommands(ReadOnlySpan<PhysicsCommand> commands)
    {
        ThrowIfDisposed();
        // Suppressions are one-step: whatever was queued for the previous step has been consumed.
        _suppressedPairs.Clear();
        for (int index = 0; index < commands.Length; index++)
        {
            PhysicsCommand command = commands[index];
            if (command.Kind == PhysicsCommandKind.SuppressContact)
            {
                if (TryCollidableKey(command.Body, out long first)
                    && TryCollidableKey(command.SecondBody, out long second))
                {
                    _suppressedPairs.Add(first <= second
                        ? (first << 32) | (uint)second
                        : (second << 32) | (uint)first);
                }

                continue;
            }

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
        _contactImpacts.Clear();
        _simulation.Timestep(timeStep.Seconds);
        ApplyFrozenDegreesOfFreedom();

        _orderedContacts.Clear();
        _orderedContacts.AddRange(_currentContacts);
        _orderedContacts.Sort(ContactPairComparer.Instance);
        foreach (ContactPair contact in _orderedContacts)
        {
            ContactImpact impact = _contactImpacts.GetValueOrDefault(contact);
            _events.Add(_activeContacts.Contains(contact)
                ? PhysicsEvent.ContactPersisted(contact.A, contact.B, impact.Normal, impact.ApproachSpeed)
                : PhysicsEvent.ContactStarted(contact.A, contact.B, impact.Normal, impact.ApproachSpeed));
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
        _materialByDynamicHandle.Clear();
        _materialByStaticHandle.Clear();
        _dynamicIdsByHandle.Clear();
        _staticIdsByHandle.Clear();
        _bodyOrder.Clear();
        _joints.Clear();
        _jointBodies.Clear();
        _jointedPairs.Clear();
        _suppressedPairs.Clear();
        _events.Clear();
        _activeContacts.Clear();
        _currentContacts.Clear();
        _contactImpacts.Clear();
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

    /// <summary>
    /// Records the pre-solve impact of one contact manifold for the pair's key order. The
    /// normal is oriented against the pre-solve relative velocity instead of trusting the
    /// engine's manifold convention: <c>+Normal</c> always pushes <see cref="ContactPair.A"/>
    /// away from <see cref="ContactPair.B"/>. A resting or sliding manifold records nothing,
    /// and the reduction below keeps the strongest approach, so the emitted event cannot
    /// depend on manifold iteration order.
    /// </summary>
    private void RecordContactImpact(CollidablePair pair, Vector3 normal)
    {
        if (!TryGetBodyId(pair.A, out PhysicsBodyId bodyA)
            || !TryGetBodyId(pair.B, out PhysicsBodyId bodyB)
            || bodyA == bodyB)
        {
            return;
        }

        ContactPair key = ContactPair.Create(bodyA, bodyB);
        Vector3 velocityA = GetPreSolveLinearVelocity(pair.A);
        Vector3 velocityB = GetPreSolveLinearVelocity(pair.B);
        Vector3 relative = key.A == bodyA ? velocityA - velocityB : velocityB - velocityA;
        float along = Vector3.Dot(relative, normal);
        float approachSpeed = MathF.Abs(along);
        if (approachSpeed <= 0f)
        {
            return;
        }

        Vector3 oriented = along < 0f ? normal : -normal;
        PhysicsVector3 candidate = new(oriented.X, oriented.Y, oriented.Z);
        if (_contactImpacts.TryGetValue(key, out ContactImpact existing)
            && (existing.ApproachSpeed > approachSpeed
                || (existing.ApproachSpeed == approachSpeed && CompareContactNormals(existing.Normal, candidate) >= 0)))
        {
            return;
        }

        _contactImpacts[key] = new ContactImpact(candidate, approachSpeed);
    }

    private static int CompareContactNormals(PhysicsVector3 left, PhysicsVector3 right)
    {
        int x = left.X.CompareTo(right.X);
        if (x != 0)
        {
            return x;
        }

        int y = left.Y.CompareTo(right.Y);
        return y != 0 ? y : left.Z.CompareTo(right.Z);
    }

    /// <summary>Linear velocity before the solver ran, read from inside the narrow phase.</summary>
    private Vector3 GetPreSolveLinearVelocity(CollidableReference reference) =>
        reference.Mobility == CollidableMobility.Static
            ? default
            : _simulation.Bodies[reference.BodyHandle].Velocity.Linear;

    /// <summary>
    /// The pair's friction coefficient. Unity picks the higher-priority combine mode of the two
    /// surfaces (Average &lt; Minimum &lt; Multiply &lt; Maximum) and applies only that mode
    /// (see <see cref="PhysicsMaterial.FrictionWith"/>); until the extraction landed every
    /// material was Average, so this is the same pair average it has always been.
    /// </summary>
    internal float CombineFriction(CollidablePair pair) => GetMaterial(pair.A).FrictionWith(GetMaterial(pair.B));

    private PhysicsMaterial GetMaterial(CollidableReference reference) => reference.Mobility == CollidableMobility.Static
        ? _materialByStaticHandle.GetValueOrDefault(reference.StaticHandle.Value, PhysicsMaterial.Default)
        : _materialByDynamicHandle.GetValueOrDefault(reference.BodyHandle.Value, PhysicsMaterial.Default);

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
            float volume = ShapeMetrics.Volume(compound.Children[index].Shape);
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
                RigidPose pose = new(ToNumerics(child.Offset), ToNumerics(child.Rotation));
                AddCompoundChild(ref builder, child.Shape, in pose, childMasses[index]);
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

    private static void AddCompoundChild(ref CompoundBuilder builder, ShapeDefinition shape, in RigidPose pose, float mass)
    {
        switch (shape)
        {
            case BoxShapeDefinition box:
            {
                Box physicsBox = new(box.HalfExtentX * 2, box.HalfExtentY * 2, box.HalfExtentZ * 2);
                builder.Add(in physicsBox, in pose, mass);
                break;
            }

            case SphereShapeDefinition sphere:
            {
                Sphere physicsSphere = new(sphere.Radius);
                builder.Add(in physicsSphere, in pose, mass);
                break;
            }

            default:
                throw new NotSupportedException("BepuPhysics compound children must be boxes or spheres.");
        }
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

    /// <summary>Contact normal oriented so that pushing <see cref="ContactPair.A"/> along
    /// <c>+Normal</c> separates the pair, plus the relative approach speed measured before
    /// the solver ran.</summary>
    private readonly record struct ContactImpact(PhysicsVector3 Normal, float ApproachSpeed);

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
            => (a.Mobility == CollidableMobility.Dynamic || b.Mobility == CollidableMobility.Dynamic)
            && !_world.AreJointed(a, b)
            && !_world.IsSuppressed(a, b);

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
                // Per-body friction from content, combined by Unity's PhysicMaterialCombine rule.
                // BepuPhysics v2 exposes no restitution term; the rules layer turns the
                // recorded contact impact into a bounce impulse instead (see PhysicsMaterial).
                FrictionCoefficient = _world.CombineFriction(pair),
                // BepuPhysics v2 has no restitution term, so the clamp only bounds penetration
                // recovery; raising it to 30 changed a measured landing bounce by nothing at all.
                MaximumRecoveryVelocity = 2f,
                SpringSettings = new SpringSettings(30, 1)
            };

            for (int contactIndex = 0; contactIndex < manifold.Count; contactIndex++)
            {
                if (manifold.GetDepth(ref manifold, contactIndex) >= 0)
                {
                    _world.RecordContact(pair);
                    _world.RecordContactImpact(pair, manifold.GetNormal(ref manifold, contactIndex));
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
        private readonly BepuPhysicsWorld _world;
        private readonly Vector3 _gravity;
        private Vector3Wide _gravityWideDt;

        public PoseIntegratorCallbacks(BepuPhysicsWorld world, Vector3 gravity)
        {
            _world = world;
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
            // The original's per-rigidbody damping and angular clamp, in PhysX's own order
            // (DyBodyCoreIntegrator.h::bodyCoreComputeUnconstrainedVelocity): gravity, then
            // `v *= max(0, 1 - damping * dt)`, then the magnitude clamp, all before the solver.
            // Frozen degrees of freedom (the original's `RigidbodyConstraints`): a body that must
            // stay in the build plane never integrates the locked component, whatever the solver
            // wrote into it. Velocity is written back, so the freeze survives to the next substep.
            // `integrationMask` is deliberately not consulted: a lane that is not integrated keeps
            // the base integrator's own garbage index, whose mask lookup resolves to None.
            int laneCount = Vector<float>.Count;
            for (int lane = 0; lane < laneCount; lane++)
            {
                BodyMotionSettings motion = _world.MotionOf(bodyIndices[lane]);
                if (motion.LinearDamping > 0f)
                {
                    float factor = MathF.Max(0f, 1f - motion.LinearDamping * dt[lane]);
                    velocity.Linear.X = Vector.WithElement(velocity.Linear.X, lane, velocity.Linear.X[lane] * factor);
                    velocity.Linear.Y = Vector.WithElement(velocity.Linear.Y, lane, velocity.Linear.Y[lane] * factor);
                    velocity.Linear.Z = Vector.WithElement(velocity.Linear.Z, lane, velocity.Linear.Z[lane] * factor);
                }

                if (motion.AngularDamping > 0f)
                {
                    float factor = MathF.Max(0f, 1f - motion.AngularDamping * dt[lane]);
                    velocity.Angular.X = Vector.WithElement(velocity.Angular.X, lane, velocity.Angular.X[lane] * factor);
                    velocity.Angular.Y = Vector.WithElement(velocity.Angular.Y, lane, velocity.Angular.Y[lane] * factor);
                    velocity.Angular.Z = Vector.WithElement(velocity.Angular.Z, lane, velocity.Angular.Z[lane] * factor);
                }

                if (motion.MaximumAngularSpeed > 0f)
                {
                    float angularX = velocity.Angular.X[lane];
                    float angularY = velocity.Angular.Y[lane];
                    float angularZ = velocity.Angular.Z[lane];
                    float lengthSquared = (angularX * angularX) + (angularY * angularY) + (angularZ * angularZ);
                    float maximum = motion.MaximumAngularSpeed;
                    if (lengthSquared > maximum * maximum)
                    {
                        float scale = maximum / MathF.Sqrt(lengthSquared);
                        velocity.Angular.X = Vector.WithElement(velocity.Angular.X, lane, angularX * scale);
                        velocity.Angular.Y = Vector.WithElement(velocity.Angular.Y, lane, angularY * scale);
                        velocity.Angular.Z = Vector.WithElement(velocity.Angular.Z, lane, angularZ * scale);
                    }
                }

                PhysicsConstraintMask constraints = _world.ConstraintsOf(bodyIndices[lane]);
                if ((constraints & PhysicsConstraintMask.LockPositionX) != 0)
                {
                    velocity.Linear.X = Vector.WithElement(velocity.Linear.X, lane, 0f);
                }

                if ((constraints & PhysicsConstraintMask.LockPositionY) != 0)
                {
                    velocity.Linear.Y = Vector.WithElement(velocity.Linear.Y, lane, 0f);
                }

                if ((constraints & PhysicsConstraintMask.LockPositionZ) != 0)
                {
                    velocity.Linear.Z = Vector.WithElement(velocity.Linear.Z, lane, 0f);
                }

                if ((constraints & PhysicsConstraintMask.LockRotationX) != 0)
                {
                    velocity.Angular.X = Vector.WithElement(velocity.Angular.X, lane, 0f);
                }

                if ((constraints & PhysicsConstraintMask.LockRotationY) != 0)
                {
                    velocity.Angular.Y = Vector.WithElement(velocity.Angular.Y, lane, 0f);
                }

                if ((constraints & PhysicsConstraintMask.LockRotationZ) != 0)
                {
                    velocity.Angular.Z = Vector.WithElement(velocity.Angular.Z, lane, 0f);
                }
            }
        }
    }
}
