using PigForge.Physics.Abstractions;
using PigForge.Physics.Bepu;

namespace PigForge.Physics.Tests;

/// <summary>
/// The spring on the real Bepu backend (docs/specs/spring-joint.md). Both of the original's
/// paths collapse onto one contract machine — a <see cref="PhysicsJointKind.Distance"/> link
/// whose free band is shut (<c>MinimumDistance == MaximumDistance</c>) plus the spring
/// frequency/damping ratio the server derives with <c>GameRoom.TrySpringResponse</c>:
/// path A (the pull-only <c>SpringJoint</c> at its auto-configured rest pose) and path B (the y
/// soft limit) differ in spring calibration, not in constraint shape.
/// <para>
/// The original's probe (tasks/spring-probe.json, 2021.3.45f2) reads a load-carrying,
/// non-oscillating link that breaks on the declared force (245 N at <c>breakForce</c> 250,
/// ~1200 N at 1200). These fixtures check the shipped shape: the link holds its spacing and its
/// deflection is load over the declared stiffness, and the break lands on the threshold.
/// </para>
/// </summary>
public sealed class SpringDistanceJointTests
{
    private const float Gravity = 9.81f;

    /// <summary>The original's spring pair: <c>SPRING_LIMIT_SPRING</c> / <c>SPRING_DAMPING</c>
    /// (Spring.cs:7,9), also the two numbers its <c>SpringJoint</c> carries.</summary>
    private const float Stiffness = 250f;
    private const float Damper = 20f;

    /// <summary>The assembly spacing the original's auto-configured <c>connectedAnchor</c>
    /// turns into the distance link's rest length.</summary>
    private const float RestDistance = 2f;

    /// <summary>An immovable root still needs a mass for the solver; the original hangs the
    /// spring from a kinematic body, and 1000 kg against a 1 kg load is the same stand-in the
    /// weld chain uses (an immovable body by another route).</summary>
    private const float AnchorMass = 1000f;

    private static readonly FixedTimeStep Step = FixedTimeStep.FromSeconds(1f / 60f);

    private const PhysicsConstraintMask AnchorLock =
        PhysicsConstraintMask.LockPositionX | PhysicsConstraintMask.LockPositionY | PhysicsConstraintMask.LockPositionZ
        | PhysicsConstraintMask.LockRotationX | PhysicsConstraintMask.LockRotationY | PhysicsConstraintMask.LockRotationZ;

    [Fact]
    public void SlacklessDistanceJointHoldsItsSpacingAndCarriesLoad()
    {
        float light = Sag(load: 1f);
        float heavy = Sag(load: 2f);

        // Holds the declared spacing within 5%: the spring's own sag is the only give.
        Assert.InRange(light, -0.05f * RestDistance, 0.05f * RestDistance);

        // Carries the load: deflection is load over the declared stiffness (the same
        // frequency-to-N/m equivalence GameRoom.TrySpringResponse relies on).
        float expected = 1f * Gravity / Stiffness;
        Assert.InRange(light, expected * 0.7f, expected * 1.3f);

        // Deflection tracks the load, so the link is a spring and not a rigid rod.
        Assert.InRange(heavy / light, 1.6f, 2.4f);
        Assert.InRange(heavy, 2f * expected * 0.7f, 2f * expected * 1.3f);
    }

    [Theory]
    [InlineData(250f, 30f)]
    [InlineData(1200f, 140f)]
    public void SlacklessDistanceJointBreaksAtItsDeclaredForce(float breakForce, float load)
    {
        // The load's weight (294 N / 1373 N) is above the threshold, and it is carried only by
        // the link, so the reaction passes the threshold within the run.
        BreakRun run = RunUntilBreak(breakForce, breakImpulse: 0f, load, maxTicks: 600);

        Assert.True(run.BreakTick >= 0, $"a {load} kg load ({load * Gravity} N) must break a {breakForce} N link");
        Assert.True(run.BodiesAlive, "both bodies must survive the break");
        Assert.True(
            run.FinalDistance > RestDistance + 5f,
            $"the link is gone and gravity separates the pair: {run.FinalDistance}");
    }

    [Fact]
    public void SlacklessDistanceJointBelowItsThresholdSurvives()
    {
        // A 1 kg load is 9.81 N of tension, well under both a 250 N force threshold and the
        // 1 N*s impulse threshold (9.81/60 = 0.16 N*s per step): the link must hold.
        BreakRun run = RunUntilBreak(breakForce: 250f, breakImpulse: 1f, load: 1f, maxTicks: 300);

        Assert.Equal(-1, run.BreakTick);
        Assert.True(run.BodiesAlive);
        Assert.InRange(run.FinalDistance, RestDistance, RestDistance + 0.1f);
    }

    [Fact]
    public void SlacklessDistanceJointBreaksOnItsImpulseThreshold()
    {
        // 2 N*s is the impulse of a 120 N reaction over a 1/60 s step; the 30 kg load's
        // reaction passes it on the way to its 294 N equilibrium, so the impulse tier fires
        // without any force threshold being set.
        BreakRun run = RunUntilBreak(breakForce: 0f, breakImpulse: 2f, load: 30f, maxTicks: 300);

        Assert.True(run.BreakTick >= 0, "the impulse threshold must break the link");
        Assert.True(run.BodiesAlive, "both bodies must survive the break");
    }

    [Fact]
    public void SlacklessDistanceJointBreakIsDeterministic()
    {
        BreakRun first = RunUntilBreak(250f, breakImpulse: 0f, load: 30f, maxTicks: 240);
        BreakRun second = RunUntilBreak(250f, breakImpulse: 0f, load: 30f, maxTicks: 240);

        Assert.Equal(first.BreakTick, second.BreakTick);
        Assert.Equal(BitConverter.SingleToInt32Bits(first.FinalDistance), BitConverter.SingleToInt32Bits(second.FinalDistance));
    }

    [Fact]
    public void SlacklessDistanceJointRejectsInvalidBreakImpulse()
    {
        PhysicsBodyId first = new(1);
        PhysicsBodyId second = new(2);
        Assert.Throws<ArgumentOutOfRangeException>(() => new JointDefinition(
            PhysicsJointKind.Distance, first, second, PhysicsConstraintMask.None,
            breakForce: 0f, breakTorque: 0f,
            minimumDistance: 1f, maximumDistance: 1f, springFrequency: 5f, springDampingRatio: 1f,
            breakImpulse: -1f));
        Assert.Throws<ArgumentOutOfRangeException>(() => new JointDefinition(
            PhysicsJointKind.Distance, first, second, PhysicsConstraintMask.None,
            breakForce: 0f, breakTorque: 0f,
            minimumDistance: 1f, maximumDistance: 1f, springFrequency: 5f, springDampingRatio: 1f,
            breakImpulse: float.NaN));

        // The slackless spring is a legal Distance joint: min == max is the spring's tier.
        JointDefinition spring = new(
            PhysicsJointKind.Distance, first, second, PhysicsConstraintMask.None,
            breakForce: 250f, breakTorque: 0f,
            minimumDistance: RestDistance, maximumDistance: RestDistance,
            springFrequency: 3f, springDampingRatio: 0.6f, breakImpulse: 4f);
        Assert.Equal(RestDistance, spring.MinimumDistance);
        Assert.Equal(RestDistance, spring.MaximumDistance);
        Assert.Equal(4f, spring.BreakImpulse);
    }

    /// <summary>Runs the loaded link and returns its deflection: <c>distance - rest</c>,
    /// positive downward. 300 ticks (5 s) is past the 0.4-0.6 damping-ratio settling time.</summary>
    private static float Sag(float load)
    {
        using BepuPhysicsWorld world = new(new PhysicsVector3(0f, -Gravity, 0f));
        (PhysicsBodyId anchor, PhysicsBodyId hanging, _) = BuildSpring(world, load);
        PhysicsEvent[] events = new PhysicsEvent[8];
        for (int tick = 0; tick < 300; tick++)
        {
            world.Step(Step);
            _ = world.DrainEvents(events);
        }

        return Distance(world, anchor, hanging) - RestDistance;
    }

    private readonly record struct BreakRun(int BreakTick, bool BodiesAlive, float FinalDistance);

    /// <summary>Runs the whole window so the final distance shows the pair separating after the
    /// break, and records the first tick the link reported <see cref="PhysicsEventKind.JointBroken"/>.</summary>
    private static BreakRun RunUntilBreak(float breakForce, float breakImpulse, float load, int maxTicks)
    {
        using BepuPhysicsWorld world = new(new PhysicsVector3(0f, -Gravity, 0f));
        (PhysicsBodyId anchor, PhysicsBodyId hanging, PhysicsJointId joint) =
            BuildSpring(world, load, breakForce, breakImpulse);
        PhysicsEvent[] events = new PhysicsEvent[16];
        int breakTick = -1;
        for (int tick = 0; tick < maxTicks; tick++)
        {
            world.Step(Step);
            int count = world.DrainEvents(events);
            for (int index = 0; index < count; index++)
            {
                if (breakTick < 0
                    && events[index].Kind == PhysicsEventKind.JointBroken
                    && events[index].Joint == joint)
                {
                    breakTick = tick;
                }
            }
        }

        return new BreakRun(
            breakTick,
            HasBody(world, anchor) && HasBody(world, hanging),
            Distance(world, anchor, hanging));
    }

    /// <summary>The spring fixture: a locked root and a hanging sphere joined by the slackless
    /// distance spring whose rate is <c>load</c>.</summary>
    private static (PhysicsBodyId Anchor, PhysicsBodyId Hanging, PhysicsJointId Joint) BuildSpring(
        BepuPhysicsWorld world,
        float load,
        float breakForce = 0f,
        float breakImpulse = 0f)
    {
        PhysicsBodyId anchor = world.CreateBody(new BodyDefinition(
            PhysicsBodyMode.Dynamic,
            new PhysicsVector3(0f, 4f, 0f),
            PhysicsQuaternion.Identity,
            AnchorMass,
            new ShapeDefinition[] { new SphereShapeDefinition(0.1f) },
            constraints: AnchorLock));
        PhysicsBodyId hanging = world.CreateBody(new BodyDefinition(
            new PhysicsVector3(0f, 4f - RestDistance, 0f),
            PhysicsQuaternion.Identity,
            load,
            new ShapeDefinition[] { new SphereShapeDefinition(0.1f) }));
        (float frequency, float dampingRatio) = SpringResponse(AnchorMass, load);
        PhysicsJointId joint = world.CreateJoint(new JointDefinition(
            PhysicsJointKind.Distance,
            anchor,
            hanging,
            PhysicsConstraintMask.None,
            breakForce: breakForce,
            breakTorque: 0f,
            minimumDistance: RestDistance,
            maximumDistance: RestDistance,
            springFrequency: frequency,
            springDampingRatio: dampingRatio,
            breakImpulse: breakImpulse));
        return (anchor, hanging, joint);
    }

    /// <summary>Unity's spring and damper in the solver's form over the pair's reduced mass —
    /// the same conversion GameRoom.TrySpringResponse applies to extracted content.</summary>
    private static (float Frequency, float DampingRatio) SpringResponse(float massA, float massB)
    {
        float reducedMass = massA * massB / (massA + massB);
        return (
            MathF.Sqrt(Stiffness / reducedMass) / (2f * MathF.PI),
            Damper / (2f * MathF.Sqrt(Stiffness * reducedMass)));
    }

    private static float Distance(BepuPhysicsWorld world, PhysicsBodyId first, PhysicsBodyId second) =>
        PhysicsVector3.Distance(Position(world, first), Position(world, second));

    private static PhysicsVector3 Position(BepuPhysicsWorld world, PhysicsBodyId body)
    {
        PhysicsBodySnapshot[] snapshots = new PhysicsBodySnapshot[4];
        int count = world.CopySnapshots(snapshots);
        for (int index = 0; index < count; index++)
        {
            if (snapshots[index].Body == body)
            {
                return snapshots[index].Position;
            }
        }

        throw new InvalidOperationException($"Body {body.Value} is not in the world's snapshots.");
    }

    private static bool HasBody(BepuPhysicsWorld world, PhysicsBodyId body)
    {
        PhysicsBodySnapshot[] snapshots = new PhysicsBodySnapshot[4];
        int count = world.CopySnapshots(snapshots);
        for (int index = 0; index < count; index++)
        {
            if (snapshots[index].Body == body)
            {
                return true;
            }
        }

        return false;
    }
}
