using PigForge.Physics.Abstractions;
using PigForge.Physics.Bepu;

namespace PigForge.Physics.Tests;

public sealed class WeldJointTests
{
    [Fact]
    public void WeldJointDefinitionCarriesAnchorsAndOptionalCompliance()
    {
        JointDefinition rigid = new(
            PhysicsJointKind.Weld,
            new PhysicsBodyId(1),
            new PhysicsBodyId(2),
            PhysicsConstraintMask.None,
            breakForce: 0f,
            breakTorque: 0f,
            localAnchorA: new PhysicsVector3(0f, -0.5f, 0f),
            localAnchorB: new PhysicsVector3(0f, 0.5f, 0f));

        Assert.Equal(PhysicsJointKind.Weld, rigid.Kind);
        Assert.Equal(0f, rigid.SpringFrequency);
        Assert.Equal(new PhysicsVector3(0f, -0.5f, 0f), rigid.LocalAnchorA);
        Assert.Equal(new PhysicsVector3(0f, 0.5f, 0f), rigid.LocalAnchorB);

        JointDefinition sprung = JointDefinition.Weld(
            new PhysicsBodyId(1),
            new PhysicsBodyId(2),
            localAnchorA: new PhysicsVector3(0f, -0.5f, 0f),
            localAnchorB: new PhysicsVector3(0f, 0.5f, 0f),
            springFrequency: 2f,
            springDampingRatio: 0.5f);

        Assert.Equal(PhysicsJointKind.Weld, sprung.Kind);
        Assert.Equal(2f, sprung.SpringFrequency);
        Assert.Equal(0.5f, sprung.SpringDampingRatio);
        Assert.Equal(new PhysicsVector3(0f, -0.5f, 0f), sprung.LocalAnchorA);
    }

    [Fact]
    public void WeldJointDefinitionRejectsInvalidSpringParametersAndAnchors()
    {
        // A negative or non-finite frequency is never a weld.
        Assert.Throws<ArgumentOutOfRangeException>(() => new JointDefinition(
            PhysicsJointKind.Weld, new PhysicsBodyId(1), new PhysicsBodyId(2), PhysicsConstraintMask.None,
            breakForce: 0f, breakTorque: 0f, springFrequency: -1f));
        Assert.Throws<ArgumentOutOfRangeException>(() => new JointDefinition(
            PhysicsJointKind.Weld, new PhysicsBodyId(1), new PhysicsBodyId(2), PhysicsConstraintMask.None,
            breakForce: 0f, breakTorque: 0f, springFrequency: float.NaN));

        // The damping ratio is bounded to [0, 1] whether or not a spring is requested.
        Assert.Throws<ArgumentOutOfRangeException>(() => new JointDefinition(
            PhysicsJointKind.Weld, new PhysicsBodyId(1), new PhysicsBodyId(2), PhysicsConstraintMask.None,
            breakForce: 0f, breakTorque: 0f, springFrequency: 5f, springDampingRatio: 1.5f));
        Assert.Throws<ArgumentOutOfRangeException>(() => new JointDefinition(
            PhysicsJointKind.Weld, new PhysicsBodyId(1), new PhysicsBodyId(2), PhysicsConstraintMask.None,
            breakForce: 0f, breakTorque: 0f, springFrequency: 5f, springDampingRatio: -0.1f));
        Assert.Throws<ArgumentOutOfRangeException>(() => new JointDefinition(
            PhysicsJointKind.Weld, new PhysicsBodyId(1), new PhysicsBodyId(2), PhysicsConstraintMask.None,
            breakForce: 0f, breakTorque: 0f, springDampingRatio: float.NaN));

        // The shared anchor validation still applies.
        Assert.Throws<ArgumentException>(() => new JointDefinition(
            PhysicsJointKind.Weld, new PhysicsBodyId(1), new PhysicsBodyId(2), PhysicsConstraintMask.None,
            breakForce: 0f, breakTorque: 0f, localAnchorA: new PhysicsVector3(float.NaN, 0f, 0f)));
    }

    [Fact]
    public void WeldJointDefinitionCarriesTheRestRotation()
    {
        JointDefinition plain = JointDefinition.Weld(new PhysicsBodyId(1), new PhysicsBodyId(2));
        Assert.Equal(PhysicsQuaternion.Identity, plain.RestRotation);

        PhysicsQuaternion quarterTurn = PhysicsQuaternion.FromZAngle(MathF.PI / 2f);
        JointDefinition turned = JointDefinition.Weld(
            new PhysicsBodyId(1),
            new PhysicsBodyId(2),
            restRotation: quarterTurn);
        Assert.Equal(quarterTurn, turned.RestRotation);

        // A non-finite rest rotation is rejected like every other joint parameter.
        Assert.Throws<ArgumentException>(() => JointDefinition.Weld(
            new PhysicsBodyId(1),
            new PhysicsBodyId(2),
            restRotation: new PhysicsQuaternion(float.NaN, 0f, 0f, 1f)));
    }

    [Fact]
    public void BepuWorldWeldHoldsTheQuarterTurnThePairWasBuiltWith()
    {
        using BepuPhysicsWorld world = new(PhysicsVector3.Zero);
        WeldMeasurement measurement = WeldScenario.RunQuarterTurn(world, steps: 120);

        Assert.InRange(measurement.Separation, 0.98f, 1.02f);
        Assert.InRange(measurement.RelativeRotationDegrees, 85f, 95f);
    }

    [Fact]
    public void BepuWorldCreatesWeldJointAndKeepsRelativePoseUnderLoad()
    {
        using BepuPhysicsWorld world = new(PhysicsVector3.Zero);
        WeldMeasurement measurement = WeldScenario.Run(world, springFrequency: 0f, steps: 120);

        Assert.InRange(measurement.Separation, 0.99f, 1.01f);
        Assert.True(
            measurement.Extension < 0.02f,
            $"a rigid weld keeps the rest separation under a steady load: extension={measurement.Extension}");
        Assert.True(
            measurement.RelativeRotationDegrees < 1f,
            $"a rigid weld keeps the relative orientation under a steady load: {measurement.RelativeRotationDegrees} deg");
    }

    [Fact]
    public void BepuWorldSofterWeldSpringDisplacesMoreThanAStiffOne()
    {
        using BepuPhysicsWorld stiffWorld = new(PhysicsVector3.Zero);
        using BepuPhysicsWorld softWorld = new(PhysicsVector3.Zero);
        WeldMeasurement stiff = WeldScenario.Run(stiffWorld, springFrequency: 60f, steps: 120);
        WeldMeasurement soft = WeldScenario.Run(softWorld, springFrequency: 2f, steps: 120);

        Assert.True(stiff.Extension > 0f, $"the stiff spring still gives a measurable extension: {stiff.Extension}");
        Assert.True(
            soft.Extension > stiff.Extension * 5f,
            $"the soft weld must give more than the stiff one: soft={soft.Extension} stiff={stiff.Extension}");
    }

    [Fact]
    public void BepuWorldSofterWeldSpringRotatesMoreThanAStiffOne()
    {
        // Bepu's Weld SpringSettings soften the orientation part too (the spec's chain of
        // welded frames has to bend); a pure couple is exactly the moment that exercises it.
        using BepuPhysicsWorld stiffWorld = new(PhysicsVector3.Zero);
        using BepuPhysicsWorld softWorld = new(PhysicsVector3.Zero);
        WeldMeasurement stiff = WeldScenario.RunBending(stiffWorld, springFrequency: 60f, steps: 60);
        WeldMeasurement soft = WeldScenario.RunBending(softWorld, springFrequency: 2f, steps: 60);

        Assert.True(stiff.RelativeRotationDegrees > 0f, $"the stiff spring still bends measurably: {stiff.RelativeRotationDegrees} deg");
        Assert.True(
            soft.RelativeRotationDegrees > stiff.RelativeRotationDegrees * 5f,
            $"the soft weld must bend more than the stiff one: soft={soft.RelativeRotationDegrees} stiff={stiff.RelativeRotationDegrees}");
    }

    [Fact]
    public void BepuWorldWeldPairStillCollides()
    {
        // The original's adjacent parts keep colliding (the spec's decision 3: a frame chain
        // is joint plus contact), so unlike a hinged wheel a welded pair must not suppress
        // its own contacts.
        using BepuPhysicsWorld world = new(PhysicsVector3.Zero);
        PhysicsBodyId first = world.CreateBody(new BodyDefinition(
            PhysicsBodyMode.Dynamic,
            new PhysicsVector3(-0.1f, 0f, 0f),
            PhysicsQuaternion.Identity,
            1f,
            new ShapeDefinition[] { new SphereShapeDefinition(0.5f) }));
        PhysicsBodyId second = world.CreateBody(new BodyDefinition(
            PhysicsBodyMode.Dynamic,
            new PhysicsVector3(0.1f, 0f, 0f),
            PhysicsQuaternion.Identity,
            1f,
            new ShapeDefinition[] { new SphereShapeDefinition(0.5f) }));
        world.CreateJoint(JointDefinition.Weld(
            first,
            second,
            localAnchorA: new PhysicsVector3(0.1f, 0f, 0f),
            localAnchorB: new PhysicsVector3(-0.1f, 0f, 0f)));

        PhysicsEvent[] events = new PhysicsEvent[64];
        bool contact = false;
        for (int tick = 0; tick < 5 && !contact; tick++)
        {
            world.Step(FixedTimeStep.FromSeconds(WeldScenario.TimeStepSeconds));
            int count = world.DrainEvents(events);
            for (int index = 0; index < count; index++)
            {
                contact |= events[index].Kind is PhysicsEventKind.ContactStarted or PhysicsEventKind.ContactPersisted
                    && ((events[index].BodyA == first && events[index].BodyB == second)
                        || (events[index].BodyA == second && events[index].BodyB == first));
            }
        }

        Assert.True(contact, "a welded pair must still collide, like the original's adjacent parts");
    }

    [Fact]
    public void BepuWorldWeldScenarioIsDeterministic()
    {
        using BepuPhysicsWorld first = new(PhysicsVector3.Zero);
        using BepuPhysicsWorld second = new(PhysicsVector3.Zero);
        Assert.Equal(
            WeldScenario.Run(first, springFrequency: 2f, steps: 120),
            WeldScenario.Run(second, springFrequency: 2f, steps: 120));
    }
}

/// <summary>
/// What one weld run measured: the separation of the two bodies against the configured rest
/// separation, and the angle between their orientations.
/// </summary>
internal readonly record struct WeldMeasurement(float Separation, float Extension, float RelativeRotationDegrees);

/// <summary>
/// The two-body weld scenario both backends run, so their behaviour is compared on identical
/// numbers. Two equal dynamic boxes one unit apart are welded at the midpoint between them and
/// then loaded by equal, opposite impulses every tick with no net momentum, so the pair's centre
/// of mass never moves and the measured displacement is the joint's own compliance. The default
/// load pushes the boxes apart along the weld axis (a steady 15 N tension); the bending load
/// applies a pure couple to each box in opposite senses (zero net force and zero net moment),
/// so only the relative orientation the weld carries moves and the pair does not spin up.
/// </summary>
internal static class WeldScenario
{
    public const float TimeStepSeconds = 1f / 60f;
    private const float RestSeparation = 1f;
    private const float AxialImpulse = 0.25f;
    private const float CoupleImpulse = 0.02f;
    private static readonly PhysicsVector3 UpperAnchor = new(0f, -0.5f, 0f);
    private static readonly PhysicsVector3 LowerAnchor = new(0f, 0.5f, 0f);

    public static WeldMeasurement Run(IPhysicsWorld world, float springFrequency, int steps) =>
        Run(world, springFrequency, steps, axial: true);

    public static WeldMeasurement RunBending(IPhysicsWorld world, float springFrequency, int steps) =>
        Run(world, springFrequency, steps, axial: false);

    private static WeldMeasurement Run(IPhysicsWorld world, float springFrequency, int steps, bool axial)
    {
        PhysicsBodyId upper = world.CreateBody(new BodyDefinition(
            PhysicsBodyMode.Dynamic,
            PhysicsVector3.Zero,
            PhysicsQuaternion.Identity,
            1f,
            new ShapeDefinition[] { new BoxShapeDefinition(0.25f, 0.25f, 0.25f) }));
        PhysicsVector3 lowerCentre = new(0f, -RestSeparation, 0f);
        PhysicsBodyId lower = world.CreateBody(new BodyDefinition(
            PhysicsBodyMode.Dynamic, lowerCentre, PhysicsQuaternion.Identity, 1f,
            new ShapeDefinition[] { new BoxShapeDefinition(0.25f, 0.25f, 0.25f) }));
        PhysicsJointId joint = world.CreateJoint(JointDefinition.Weld(
            upper,
            lower,
            localAnchorA: UpperAnchor,
            localAnchorB: LowerAnchor,
            springFrequency: springFrequency,
            springDampingRatio: 1f));
        if (!joint.IsValid)
        {
            throw new InvalidOperationException("The backend refused the weld joint.");
        }

        FixedTimeStep timeStep = FixedTimeStep.FromSeconds(TimeStepSeconds);
        // The bending load is a couple on each body in opposite senses (two opposite impulses
        // offset in Z per body): net force and net moment are both zero, so the pair does not
        // spin up and only the relative orientation the weld carries moves.
        PhysicsVector3 coupleOffsetAbove = new(0f, 0f, -0.25f);
        PhysicsVector3 coupleOffsetBelow = new(0f, 0f, 0.25f);
        PhysicsCommand[] commands = axial
            ?
            [
                PhysicsCommand.ApplyImpulse(upper, new PhysicsVector3(0f, AxialImpulse, 0f), PhysicsVector3.Zero),
                PhysicsCommand.ApplyImpulse(lower, new PhysicsVector3(0f, -AxialImpulse, 0f), lowerCentre)
            ]
            :
            [
                PhysicsCommand.ApplyImpulse(upper, new PhysicsVector3(CoupleImpulse, 0f, 0f), coupleOffsetAbove),
                PhysicsCommand.ApplyImpulse(upper, new PhysicsVector3(-CoupleImpulse, 0f, 0f), coupleOffsetBelow),
                PhysicsCommand.ApplyImpulse(lower, new PhysicsVector3(-CoupleImpulse, 0f, 0f), lowerCentre + coupleOffsetAbove),
                PhysicsCommand.ApplyImpulse(lower, new PhysicsVector3(CoupleImpulse, 0f, 0f), lowerCentre + coupleOffsetBelow)
            ];
        PhysicsEvent[] events = new PhysicsEvent[16];
        for (int tick = 0; tick < steps; tick++)
        {
            world.ApplyCommands(commands);
            world.Step(timeStep);
            _ = world.DrainEvents(events);
        }

        PhysicsBodySnapshot[] snapshots = new PhysicsBodySnapshot[2];
        int count = world.CopySnapshots(snapshots);
        PhysicsBodySnapshot upperSnapshot = default;
        PhysicsBodySnapshot lowerSnapshot = default;
        for (int index = 0; index < count; index++)
        {
            if (snapshots[index].Body == upper)
            {
                upperSnapshot = snapshots[index];
            }
            else if (snapshots[index].Body == lower)
            {
                lowerSnapshot = snapshots[index];
            }
        }

        float separation = PhysicsVector3.Distance(upperSnapshot.Position, lowerSnapshot.Position);
        PhysicsQuaternion relative = upperSnapshot.Rotation.Inverse * lowerSnapshot.Rotation;
        // atan2 of the vector part over the scalar part keeps the precision near identity,
        // where acos(w) on a float quaternion would round a small angle to exactly zero.
        float sinHalfAngle = MathF.Sqrt(
            (relative.X * relative.X) + (relative.Y * relative.Y) + (relative.Z * relative.Z));
        float degrees = 2f * MathF.Atan2(sinHalfAngle, MathF.Abs(relative.W)) * (180f / MathF.PI);
        return new WeldMeasurement(separation, separation - RestSeparation, degrees);
    }

    /// <summary>
    /// A pair placed the way the original places one: each body's anchor is half-way toward the
    /// other in its OWN frame (Contraption.AddFixedJoint: <c>anchor =
    /// InverseTransformPoint(other.position) * 0.5f</c>, so both anchors are the same world point
    /// seen from each body), and the second body is turned a quarter turn. A weld has to hold that
    /// quarter turn AND that separation: assuming the frames are aligned would untwist the pair,
    /// and subtracting the two anchors directly would pull it to the wrong offset.
    /// </summary>
    public static WeldMeasurement RunQuarterTurn(IPhysicsWorld world, int steps)
    {
        PhysicsQuaternion quarterTurn = PhysicsQuaternion.FromZAngle(MathF.PI / 2f);
        PhysicsVector3 upperCentre = PhysicsVector3.Zero;
        PhysicsVector3 lowerCentre = new(0f, -RestSeparation, 0f);
        PhysicsBodyId upper = world.CreateBody(new BodyDefinition(
            PhysicsBodyMode.Dynamic,
            upperCentre,
            PhysicsQuaternion.Identity,
            1f,
            new ShapeDefinition[] { new BoxShapeDefinition(0.25f, 0.25f, 0.25f) }));
        PhysicsBodyId lower = world.CreateBody(new BodyDefinition(
            PhysicsBodyMode.Dynamic,
            lowerCentre,
            quarterTurn,
            1f,
            new ShapeDefinition[] { new BoxShapeDefinition(0.25f, 0.25f, 0.25f) }));
        PhysicsVector3 anchorA = (lowerCentre - upperCentre) * 0.5f;
        PhysicsVector3 anchorB = quarterTurn.Inverse.Rotate(upperCentre - lowerCentre) * 0.5f;
        PhysicsJointId joint = world.CreateJoint(JointDefinition.Weld(
            upper,
            lower,
            localAnchorA: anchorA,
            localAnchorB: anchorB,
            restRotation: quarterTurn));
        if (!joint.IsValid)
        {
            throw new InvalidOperationException("The backend refused the quarter-turn weld joint.");
        }

        FixedTimeStep timeStep = FixedTimeStep.FromSeconds(TimeStepSeconds);
        PhysicsEvent[] events = new PhysicsEvent[16];
        for (int tick = 0; tick < steps; tick++)
        {
            world.Step(timeStep);
            _ = world.DrainEvents(events);
        }

        PhysicsBodySnapshot[] snapshots = new PhysicsBodySnapshot[2];
        int count = world.CopySnapshots(snapshots);
        PhysicsBodySnapshot upperSnapshot = default;
        PhysicsBodySnapshot lowerSnapshot = default;
        for (int index = 0; index < count; index++)
        {
            if (snapshots[index].Body == upper)
            {
                upperSnapshot = snapshots[index];
            }
            else if (snapshots[index].Body == lower)
            {
                lowerSnapshot = snapshots[index];
            }
        }

        float separation = PhysicsVector3.Distance(upperSnapshot.Position, lowerSnapshot.Position);
        PhysicsQuaternion relative = upperSnapshot.Rotation.Inverse * lowerSnapshot.Rotation;
        float sinHalfAngle = MathF.Sqrt(
            (relative.X * relative.X) + (relative.Y * relative.Y) + (relative.Z * relative.Z));
        float degrees = 2f * MathF.Atan2(sinHalfAngle, MathF.Abs(relative.W)) * (180f / MathF.PI);
        return new WeldMeasurement(separation, separation - RestSeparation, degrees);
    }
}
