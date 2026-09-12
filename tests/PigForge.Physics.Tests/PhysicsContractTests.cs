using PigForge.Physics.Abstractions;
using PigForge.Physics.Bepu;

namespace PigForge.Physics.Tests;

public sealed class PhysicsContractTests
{
    [Fact]
    public void StaticBodyAllowsZeroMassAndDynamicBodyRequiresPositiveMass()
    {
        BodyDefinition ground = new(
            PhysicsBodyMode.Static,
            PhysicsVector3.Zero,
            PhysicsQuaternion.Identity,
            mass: 0,
            new ShapeDefinition[] { new BoxShapeDefinition(10, 1, 10) });

        BodyDefinition dynamicBody = new(
            PhysicsBodyMode.Dynamic,
            PhysicsVector3.Zero,
            PhysicsQuaternion.Identity,
            mass: 1,
            new ShapeDefinition[] { new BoxShapeDefinition(0.5f, 0.5f, 0.5f) });

        Assert.Equal(PhysicsBodyMode.Static, ground.Mode);
        Assert.Equal(0, ground.Mass);
        Assert.Equal(PhysicsBodyMode.Dynamic, dynamicBody.Mode);
    }

    [Fact]
    public void BodyDefinitionsRejectNonFiniteTransformsAndInvalidShapes()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new BodyDefinition(
            PhysicsBodyMode.Dynamic,
            new PhysicsVector3(float.NaN, 0, 0),
            PhysicsQuaternion.Identity,
            1,
            new ShapeDefinition[] { new BoxShapeDefinition(1, 1, 1) }));

        Assert.Throws<ArgumentOutOfRangeException>(() => new BoxShapeDefinition(0, 1, 1));
    }

	[Fact]
	public void BodyDefinitionPreservesInitialVelocitiesAndRejectsStaticVelocity()
	{
		BodyDefinition body = new(
			PhysicsVector3.Zero,
			PhysicsQuaternion.Identity,
			1,
			new ShapeDefinition[] { new BoxShapeDefinition(0.5f, 0.5f, 0.5f) },
			new PhysicsVector3(1, 2, 3),
			new PhysicsVector3(4, 5, 6));

		Assert.Equal(new PhysicsVector3(1, 2, 3), body.LinearVelocity);
		Assert.Equal(new PhysicsVector3(4, 5, 6), body.AngularVelocity);
		Assert.Throws<ArgumentException>(() => new BodyDefinition(
			PhysicsBodyMode.Static,
			PhysicsVector3.Zero,
			PhysicsQuaternion.Identity,
			0,
			new ShapeDefinition[] { new BoxShapeDefinition(1, 1, 1) },
			new PhysicsVector3(1, 0, 0)));
	}

    [Fact]
    public void ImpulseCommandRequiresValidBodyAndFiniteValues()
    {
        PhysicsCommand command = PhysicsCommand.ApplyImpulse(
            new PhysicsBodyId(3),
            new PhysicsVector3(1, 2, 3),
            PhysicsVector3.Zero);

        Assert.Equal(PhysicsCommandKind.ApplyImpulse, command.Kind);
        Assert.Equal(new PhysicsBodyId(3), command.Body);
        Assert.Throws<ArgumentException>(() => PhysicsCommand.ApplyImpulse(
            default,
            PhysicsVector3.Zero,
            PhysicsVector3.Zero));
        Assert.Throws<ArgumentOutOfRangeException>(() => PhysicsCommand.ApplyImpulse(
            new PhysicsBodyId(3),
            new PhysicsVector3(float.PositiveInfinity, 0, 0),
            PhysicsVector3.Zero));
    }

    [Fact]
    public void PhysicsEventsUseFactoriesThatMatchTheirPayloads()
    {
        PhysicsEvent contact = PhysicsEvent.ContactStarted(new PhysicsBodyId(1), new PhysicsBodyId(2));
        PhysicsEvent broken = PhysicsEvent.JointBroken(new PhysicsJointId(9));
        PhysicsEvent destroyed = PhysicsEvent.BodyDestroyed(new PhysicsBodyId(4));

        Assert.Equal(PhysicsEventKind.ContactStarted, contact.Kind);
        Assert.Equal(new PhysicsBodyId(1), contact.BodyA);
        Assert.Equal(new PhysicsBodyId(2), contact.BodyB);
        Assert.Equal(PhysicsEventKind.JointBroken, broken.Kind);
        Assert.Equal(new PhysicsJointId(9), broken.Joint);
        Assert.Equal(PhysicsEventKind.BodyDestroyed, destroyed.Kind);
        Assert.Equal(new PhysicsBodyId(4), destroyed.BodyA);
    }

    [Fact]
    public void JointDefinitionRejectsInvalidBodyPairsAndBreakThresholds()
    {
        Assert.Throws<ArgumentException>(() => new JointDefinition(
            PhysicsJointKind.Fixed,
            new PhysicsBodyId(4),
            new PhysicsBodyId(4),
            PhysicsConstraintMask.None,
            1,
            1));

        Assert.Throws<ArgumentOutOfRangeException>(() => new JointDefinition(
            PhysicsJointKind.Fixed,
            new PhysicsBodyId(4),
            new PhysicsBodyId(5),
            PhysicsConstraintMask.None,
            float.NaN,
            1));
    }

    [Fact]
    public void DisposedWorldRejectsFurtherOperations()
    {
        RecordingPhysicsWorld world = new();
        world.Dispose();

        Assert.Throws<ObjectDisposedException>(() => world.Step(FixedTimeStep.FromSeconds(1f / 60f)));
    }
    [Fact]
    public void ContractFakeAppliesBatchBeforeStepAndPublishesSnapshotsAndEvents()
    {
        using RecordingPhysicsWorld world = new();
        PhysicsBodyId body = world.CreateBody(DynamicBox());
        PhysicsCommand[] commands =
        {
            PhysicsCommand.ApplyImpulse(body, new PhysicsVector3(0, 1, 0), PhysicsVector3.Zero)
        };

        world.ApplyCommands(commands);
        world.Step(FixedTimeStep.FromSeconds(1f / 60f));

        PhysicsBodySnapshot[] snapshots = new PhysicsBodySnapshot[1];
        PhysicsEvent[] events = new PhysicsEvent[2];
        Assert.Equal(1, world.CopySnapshots(snapshots));
        Assert.Equal(1, world.DrainEvents(events));
        Assert.Equal(body, snapshots[0].Body);
        Assert.Equal(PhysicsEventKind.ContactStarted, events[0].Kind);
        Assert.Equal(new[] { "apply", "step" }, world.Phases);
    }

    [Fact]
    public void BepuWorldDropsDynamicBoxOntoGroundAndPublishesContact()
    {
        using BepuPhysicsWorld world = new(new PhysicsVector3(0, -9.81f, 0));
        PhysicsBodyId ground = world.CreateBody(new BodyDefinition(
            PhysicsBodyMode.Static,
            PhysicsVector3.Zero,
            PhysicsQuaternion.Identity,
            0,
            new ShapeDefinition[] { new BoxShapeDefinition(10, 0.5f, 10) }));
        PhysicsBodyId box = world.CreateBody(new BodyDefinition(
            PhysicsBodyMode.Dynamic,
            new PhysicsVector3(0, 4, 0),
            PhysicsQuaternion.Identity,
            1,
            new ShapeDefinition[] { new BoxShapeDefinition(0.5f, 0.5f, 0.5f) }));

        PhysicsEvent[] events = new PhysicsEvent[8];
        _ = world.DrainEvents(events);
        bool contactStarted = false;
        FixedTimeStep timeStep = FixedTimeStep.FromSeconds(1f / 60f);
        for (int tick = 0; tick < 180; tick++)
        {
            world.ApplyCommands(ReadOnlySpan<PhysicsCommand>.Empty);
            world.Step(timeStep);
            int eventCount = world.DrainEvents(events);
            for (int index = 0; index < eventCount; index++)
            {
                contactStarted |= events[index].Kind == PhysicsEventKind.ContactStarted
                    && ((events[index].BodyA == ground && events[index].BodyB == box)
                        || (events[index].BodyA == box && events[index].BodyB == ground));
            }
        }

        PhysicsBodySnapshot[] snapshots = new PhysicsBodySnapshot[2];
        int snapshotCount = world.CopySnapshots(snapshots);
        PhysicsBodySnapshot boxSnapshot = Assert.Single(snapshots[..snapshotCount], snapshot => snapshot.Body == box);

        Assert.InRange(boxSnapshot.Position.Y, 0.95f, 1.1f);
        Assert.True(contactStarted);
    }

    [Fact]
    public void BepuWorldAppliesImpulseBeforeFixedStep()
    {
        using BepuPhysicsWorld world = new(new PhysicsVector3(0, 0, 0));
        PhysicsBodyId body = world.CreateBody(new BodyDefinition(
            PhysicsBodyMode.Dynamic,
            new PhysicsVector3(0, 2, 0),
            PhysicsQuaternion.Identity,
            2,
            new ShapeDefinition[] { new BoxShapeDefinition(0.5f, 0.5f, 0.5f) }));
        PhysicsEvent[] events = new PhysicsEvent[1];
        _ = world.DrainEvents(events);

        world.ApplyCommands(new[]
        {
            PhysicsCommand.ApplyImpulse(body, new PhysicsVector3(0, 4, 0), PhysicsVector3.Zero)
        });
        world.Step(FixedTimeStep.FromSeconds(1f / 60f));

        PhysicsBodySnapshot[] snapshots = new PhysicsBodySnapshot[1];
        world.CopySnapshots(snapshots);
        Assert.Equal(2f, snapshots[0].LinearVelocity.Y, precision: 4);
    }

    [Fact]
    public void BepuWorldRejectsUnsupportedShapeAndJoint()
    {
        using BepuPhysicsWorld world = new(PhysicsVector3.Zero);
        Assert.Throws<NotSupportedException>(() => world.CreateBody(new BodyDefinition(
            PhysicsBodyMode.Dynamic,
            PhysicsVector3.Zero,
            PhysicsQuaternion.Identity,
            1,
            new ShapeDefinition[] { new UnsupportedShapeDefinition() })));
        Assert.Equal(new[] { PhysicsJointKind.Revolute }, world.Capabilities.SupportedJointKinds);
        PhysicsBodyId first = world.CreateBody(DynamicBox());
        PhysicsBodyId second = world.CreateBody(DynamicBox());
        Assert.Throws<NotSupportedException>(() => world.CreateJoint(new JointDefinition(
            PhysicsJointKind.Distance, first, second, PhysicsConstraintMask.None, breakForce: 0f, breakTorque: 0f)));
    }

    [Fact]
    public void BepuWorldCreatesDynamicSphere()
    {
        using BepuPhysicsWorld world = new(new PhysicsVector3(0, -9.81f, 0));
        PhysicsBodyId body = world.CreateBody(new BodyDefinition(
            PhysicsBodyMode.Dynamic,
            new PhysicsVector3(0, 2, 0),
            PhysicsQuaternion.Identity,
            1,
            new ShapeDefinition[] { new SphereShapeDefinition(0.4f) }));
        world.Step(FixedTimeStep.FromSeconds(1f / 60f));
        PhysicsBodySnapshot[] snapshots = new PhysicsBodySnapshot[1];
        Assert.Equal(1, world.CopySnapshots(snapshots));
        Assert.Equal(body, snapshots[0].Body);
        Assert.True(snapshots[0].Position.Y < 2f);
        world.DestroyBody(body);
    }


    [Fact]
    public void BepuWorldCreatesCompoundDropsItAndReleasesShapes()
    {
        using BepuPhysicsWorld world = new(new PhysicsVector3(0, -9.81f, 0));
        PhysicsBodyId ground = world.CreateBody(new BodyDefinition(
            PhysicsBodyMode.Static,
            PhysicsVector3.Zero,
            PhysicsQuaternion.Identity,
            0,
            new ShapeDefinition[] { new BoxShapeDefinition(10, 0.5f, 10) }));
        CompoundShapeDefinition compound = new(new[]
        {
            new CompoundChild(new BoxShapeDefinition(0.5f, 0.5f, 0.5f), new PhysicsVector3(-0.55f, 0f, 0f)),
            new CompoundChild(new BoxShapeDefinition(0.5f, 0.5f, 0.5f), new PhysicsVector3(0.55f, 0f, 0f))
        });
        PhysicsBodyId body = world.CreateBody(new BodyDefinition(
            PhysicsBodyMode.Dynamic,
            new PhysicsVector3(0, 4, 0),
            PhysicsQuaternion.Identity,
            2,
            new ShapeDefinition[] { compound }));

        PhysicsEvent[] events = new PhysicsEvent[16];
        _ = world.DrainEvents(events);
        bool contactStarted = false;
        FixedTimeStep timeStep = FixedTimeStep.FromSeconds(1f / 60f);
        for (int tick = 0; tick < 180; tick++)
        {
            world.ApplyCommands(ReadOnlySpan<PhysicsCommand>.Empty);
            world.Step(timeStep);
            int eventCount = world.DrainEvents(events);
            for (int index = 0; index < eventCount; index++)
            {
                contactStarted |= events[index].Kind == PhysicsEventKind.ContactStarted
                    && ((events[index].BodyA == ground && events[index].BodyB == body)
                        || (events[index].BodyA == body && events[index].BodyB == ground));
            }
        }

        PhysicsBodySnapshot[] snapshots = new PhysicsBodySnapshot[2];
        int snapshotCount = world.CopySnapshots(snapshots);
        PhysicsBodySnapshot compoundSnapshot = Assert.Single(snapshots[..snapshotCount], snapshot => snapshot.Body == body);
        Assert.InRange(compoundSnapshot.Position.Y, 0.95f, 1.2f);
        Assert.True(contactStarted);

        world.DestroyBody(body);
        world.CreateBody(new BodyDefinition(
            PhysicsBodyMode.Dynamic,
            new PhysicsVector3(0, 3, 0),
            PhysicsQuaternion.Identity,
            1,
            new ShapeDefinition[] { new BoxShapeDefinition(0.5f, 0.5f, 0.5f) }));
    }

    [Fact]
    public void BepuWorldHingeHoldsBodiesTogetherWhileTheWheelSpins()
    {
        using BepuPhysicsWorld world = new(new PhysicsVector3(0, -9.81f, 0));
        PhysicsBodyId chassis = world.CreateBody(new BodyDefinition(
            PhysicsBodyMode.Dynamic,
            new PhysicsVector3(0, 4, 0),
            PhysicsQuaternion.Identity,
            2f,
            new ShapeDefinition[] { new BoxShapeDefinition(0.5f, 0.5f, 0.5f) }));
        PhysicsBodyId wheel = world.CreateBody(new BodyDefinition(
            PhysicsBodyMode.Dynamic,
            new PhysicsVector3(0, 3, 0),
            PhysicsQuaternion.Identity,
            1f,
            new ShapeDefinition[] { new SphereShapeDefinition(0.45f) }));
        PhysicsJointId joint = world.CreateJoint(new JointDefinition(
            PhysicsJointKind.Revolute,
            chassis,
            wheel,
            PhysicsConstraintMask.None,
            breakForce: 0f,
            breakTorque: 0f,
            localAnchorA: new PhysicsVector3(0f, -1f, 0f),
            localAnchorB: PhysicsVector3.Zero,
            localAxisA: new PhysicsVector3(0f, 0f, 1f),
            localAxisB: new PhysicsVector3(0f, 0f, 1f)));

        // Free fall with no ground: the hinge keeps the axle one unit below the chassis.
        FixedTimeStep step = FixedTimeStep.FromSeconds(1f / 60f);
        for (int tick = 0; tick < 120; tick++)
        {
            world.ApplyCommands(ReadOnlySpan<PhysicsCommand>.Empty);
            world.Step(step);
            _ = world.DrainEvents(new PhysicsEvent[8]);
        }

        PhysicsBodySnapshot[] snapshots = new PhysicsBodySnapshot[2];
        int count = world.CopySnapshots(snapshots);
        PhysicsBodySnapshot chassisSnapshot = Assert.Single(snapshots[..count], entry => entry.Body == chassis);
        PhysicsBodySnapshot wheelSnapshot = Assert.Single(snapshots[..count], entry => entry.Body == wheel);
        Assert.InRange(PhysicsVector3.Distance(chassisSnapshot.Position, wheelSnapshot.Position), 0.95f, 1.05f);

        world.DestroyJoint(joint);
    }

    [Fact]
    public void JointedBodiesOverlapWithoutBeingPushedApart()
    {
        // A hinged wheel body carries only its tires; the wheel's mounts ride the parent body
        // and overlap the tire on purpose (content: support box centre 0.038 above the axle of
        // an r = 0.33 tire). Those two bodies are one mechanism, so the pair must not collide:
        // otherwise the solver fights the mount out of the tire and the cart freezes.
        ShapeDefinition[] mountBox = new ShapeDefinition[] { new BoxShapeDefinition(0.2f, 0.32f, 0.5f) };
        PhysicsVector3 mountOffset = new(0f, 0.038f, 0f);

        Assert.True(
            StepsToRelativeDistance(jointed: true) < 0.02f,
            "the hinge must hold the tire and its mount together");
        Assert.True(
            StepsToRelativeDistance(jointed: false) > 0.3f,
            "overlapping bodies without a joint must be pushed apart");

        float StepsToRelativeDistance(bool jointed)
        {
            using BepuPhysicsWorld world = new(new PhysicsVector3(0, -9.81f, 0));
            PhysicsBodyId tire = world.CreateBody(new BodyDefinition(
                PhysicsBodyMode.Dynamic,
                new PhysicsVector3(0, 4, 0),
                PhysicsQuaternion.Identity,
                1f,
                new ShapeDefinition[] { new SphereShapeDefinition(0.33f) }));
            PhysicsBodyId mount = world.CreateBody(new BodyDefinition(
                PhysicsBodyMode.Dynamic,
                new PhysicsVector3(0, 4, 0) + mountOffset,
                PhysicsQuaternion.Identity,
                1f,
                mountBox));
            if (jointed)
            {
                world.CreateJoint(new JointDefinition(
                    PhysicsJointKind.Revolute,
                    mount,
                    tire,
                    PhysicsConstraintMask.None,
                    breakForce: 0f,
                    breakTorque: 0f,
                    localAnchorA: PhysicsVector3.Zero,
                    localAnchorB: PhysicsVector3.Zero,
                    localAxisA: new PhysicsVector3(0f, 0f, 1f),
                    localAxisB: new PhysicsVector3(0f, 0f, 1f)));
            }

            FixedTimeStep step = FixedTimeStep.FromSeconds(1f / 60f);
            PhysicsBodySnapshot[] snapshots = new PhysicsBodySnapshot[2];
            for (int tick = 0; tick < 120; tick++)
            {
                world.Step(step);
                _ = world.DrainEvents(new PhysicsEvent[8]);
            }

            int count = world.CopySnapshots(snapshots);
            PhysicsBodySnapshot tireSnapshot = Assert.Single(snapshots[..count], entry => entry.Body == tire);
            PhysicsBodySnapshot mountSnapshot = Assert.Single(snapshots[..count], entry => entry.Body == mount);
            return PhysicsVector3.Distance(tireSnapshot.Position, mountSnapshot.Position);
        }
    }

    [Fact]
    public void BepuWorldDropsCompoundWithSphereChildOntoGround()
    {
        using BepuPhysicsWorld world = new(new PhysicsVector3(0, -9.81f, 0));
        PhysicsBodyId ground = world.CreateBody(new BodyDefinition(
            PhysicsBodyMode.Static,
            PhysicsVector3.Zero,
            PhysicsQuaternion.Identity,
            0,
            new ShapeDefinition[] { new BoxShapeDefinition(10, 0.5f, 10) }));
        CompoundShapeDefinition compound = new(new[]
        {
            new CompoundChild(new BoxShapeDefinition(0.5f, 0.5f, 0.5f), new PhysicsVector3(-0.5f, 0f, 0f)),
            new CompoundChild(new SphereShapeDefinition(0.45f), new PhysicsVector3(0.5f, 0f, 0f))
        });
        PhysicsBodyId body = world.CreateBody(new BodyDefinition(
            PhysicsBodyMode.Dynamic,
            new PhysicsVector3(0, 4, 0),
            PhysicsQuaternion.Identity,
            2,
            new ShapeDefinition[] { compound }));

        PhysicsEvent[] events = new PhysicsEvent[16];
        _ = world.DrainEvents(events);
        bool contactStarted = false;
        FixedTimeStep timeStep = FixedTimeStep.FromSeconds(1f / 60f);
        for (int tick = 0; tick < 180; tick++)
        {
            world.ApplyCommands(ReadOnlySpan<PhysicsCommand>.Empty);
            world.Step(timeStep);
            int eventCount = world.DrainEvents(events);
            for (int index = 0; index < eventCount; index++)
            {
                contactStarted |= events[index].Kind == PhysicsEventKind.ContactStarted
                    && ((events[index].BodyA == ground && events[index].BodyB == body)
                        || (events[index].BodyA == body && events[index].BodyB == ground));
            }
        }

        PhysicsBodySnapshot[] snapshots = new PhysicsBodySnapshot[2];
        int snapshotCount = world.CopySnapshots(snapshots);
        PhysicsBodySnapshot compoundSnapshot = Assert.Single(snapshots[..snapshotCount], snapshot => snapshot.Body == body);
        Assert.InRange(compoundSnapshot.Position.Y, 0.9f, 1.1f);
        Assert.True(contactStarted, "the sphere child must reach the ground");
    }

    [Fact]
    public void BepuWorldRejectsStaticCompound()
    {
        using BepuPhysicsWorld world = new(PhysicsVector3.Zero);
        CompoundShapeDefinition compound = new(new[]
        {
            new CompoundChild(new BoxShapeDefinition(0.5f, 0.5f, 0.5f), PhysicsVector3.Zero)
        });
        Assert.Throws<NotSupportedException>(() => world.CreateBody(new BodyDefinition(
            PhysicsBodyMode.Static,
            PhysicsVector3.Zero,
            PhysicsQuaternion.Identity,
            0,
            new ShapeDefinition[] { compound })));
    }

    private sealed record UnsupportedShapeDefinition() : ShapeDefinition(PhysicsShapeKind.Sphere);

    private static BodyDefinition DynamicBox() => new(
        PhysicsBodyMode.Dynamic,
        PhysicsVector3.Zero,
        PhysicsQuaternion.Identity,
        1,
        new ShapeDefinition[] { new BoxShapeDefinition(0.5f, 0.5f, 0.5f) });

    private sealed class RecordingPhysicsWorld : IPhysicsWorld
    {
        private readonly List<string> _phases = new();
        private readonly List<PhysicsBodyId> _bodies = new();
        private bool _disposed;
        private bool _pendingEvent;

        public PhysicsCapabilities Capabilities { get; } = new(
            new HashSet<PhysicsJointKind> { PhysicsJointKind.Fixed },
            SupportsContinuousCollision: false,
            SupportsPerBodyInertia: false);

        public IReadOnlyList<string> Phases => _phases;

        public PhysicsBodyId CreateBody(BodyDefinition definition)
        {
            ThrowIfDisposed();
            ArgumentNullException.ThrowIfNull(definition);
            PhysicsBodyId body = new((uint)(_bodies.Count + 1));
            _bodies.Add(body);
            return body;
        }

        public void DestroyBody(PhysicsBodyId body)
        {
            ThrowIfDisposed();
            _bodies.Remove(body);
        }

        public PhysicsJointId CreateJoint(JointDefinition definition)
        {
            ThrowIfDisposed();
            return new(1);
        }

        public void DestroyJoint(PhysicsJointId joint)
        {
            ThrowIfDisposed();
        }

        public void ApplyCommands(ReadOnlySpan<PhysicsCommand> commands)
        {
            ThrowIfDisposed();
            Assert.Equal(1, commands.Length);
            _phases.Add("apply");
        }

        public void Step(FixedTimeStep timeStep)
        {
            ThrowIfDisposed();
            _phases.Add("step");
            _pendingEvent = true;
        }

        public int CopySnapshots(Span<PhysicsBodySnapshot> destination)
        {
            ThrowIfDisposed();
            Assert.True(destination.Length >= _bodies.Count);
            for (int index = 0; index < _bodies.Count; index++)
            {
                destination[index] = new(_bodies[index], PhysicsVector3.Zero, PhysicsQuaternion.Identity, PhysicsVector3.Zero, PhysicsVector3.Zero);
            }

            return _bodies.Count;
        }

        public int DrainEvents(Span<PhysicsEvent> destination)
        {
            ThrowIfDisposed();
            Assert.True(destination.Length > 0);
            if (!_pendingEvent)
            {
                return 0;
            }

            destination[0] = PhysicsEvent.ContactStarted(_bodies[0], new PhysicsBodyId(99));
            _pendingEvent = false;
            return 1;
        }

        public void Dispose() => _disposed = true;

        private void ThrowIfDisposed()
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
        }
    }
}
