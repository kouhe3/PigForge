using PigForge.Physics.Abstractions;
using PigForge.Physics.Jolt;

namespace PigForge.Physics.Tests;

/// <summary>
/// The same per-rigidbody defaults on the Jolt backend. Jolt's own damping is the same first-order
/// factor PhysX uses -- <c>MotionProperties.inl::ApplyForceTorqueAndDragInternal</c>:
/// <c>v *= max(0, 1 - damping * dt)</c> -- and <c>ClampAngularVelocity</c> scales by
/// <c>max / |w|</c>, so both backends must land on the same numbers the original's editor
/// produced (<c>unity/PigForge.WeldProbe</c> body-defaults probe). Jolt's own defaults are not
/// PigForge's (0.05 damping, a 0.25*pi*60 rad/s cap, <c>BodyCreationSettings.h</c>), which is why
/// the backend writes both fields explicitly.
/// </summary>
/// <summary>
/// Jolt owns a process-wide native runtime, and two Jolt test classes running concurrently have
/// crashed the test host inside the native solver (the flakiness the repository already knows
/// about). One collection keeps every Jolt world serial, so the suites stay reliable.
/// </summary>
[Collection("Jolt")]
public sealed class JoltBodyDampingTests
{
    private const float Step = 1f / 60f;
    private const float LinearDrag = 0.2f;
    private const float AngularDrag = 0.05f;
    private const float ProjectAngularCap = 7f;

    private static BodyDefinition Body(
        PhysicsVector3 position,
        PhysicsVector3 velocity,
        float damping = 0f,
        float angularDamping = 0f,
        float maximumAngularSpeed = 0f,
        PhysicsVector3? angularVelocity = null) => new(
        PhysicsBodyMode.Dynamic,
        position,
        PhysicsQuaternion.Identity,
        mass: 1,
        new ShapeDefinition[] { new BoxShapeDefinition(0.5f, 0.5f, 0.5f) },
        velocity,
        angularVelocity ?? PhysicsVector3.Zero,
        material: null,
        constraints: PhysicsConstraintMask.None,
        linearDamping: damping,
        angularDamping: angularDamping,
        maximumAngularSpeed: maximumAngularSpeed);

    [Fact]
    public void JoltLinearDampingMatchesTheOriginalsFactor()
    {
        using JoltPhysicsWorld world = new(PhysicsVector3.Zero);
        PhysicsBodyId body = world.CreateBody(Body(PhysicsVector3.Zero, new PhysicsVector3(10f, 0f, 0f), damping: LinearDrag));
        FixedTimeStep timeStep = FixedTimeStep.FromSeconds(Step);

        for (int tick = 0; tick < 60; tick++)
        {
            world.Step(timeStep);
        }

        PhysicsBodySnapshot[] snapshots = new PhysicsBodySnapshot[1];
        _ = world.CopySnapshots(snapshots);
        Assert.Equal(10f * MathF.Pow(1f - (LinearDrag * Step), 60f), snapshots[0].LinearVelocity.X, precision: 3);
        world.DestroyBody(body);
    }

    [Fact]
    public void JoltFreeFallWithTheOriginalsDragReachesTheSameTerminalSpeed()
    {
        using JoltPhysicsWorld world = new(new PhysicsVector3(0f, -9.81f, 0f));
        PhysicsBodyId body = world.CreateBody(Body(PhysicsVector3.Zero, PhysicsVector3.Zero, damping: LinearDrag));

        for (int tick = 0; tick < 3000; tick++)
        {
            world.Step(FixedTimeStep.FromSeconds(Step));
        }

        PhysicsBodySnapshot[] snapshots = new PhysicsBodySnapshot[1];
        _ = world.CopySnapshots(snapshots);
        // The same fixed point the Bepu case asserts: (g/c)(1 - c*dt) = 48.8865 m/s.
        Assert.InRange(snapshots[0].LinearVelocity.Y, -48.9365f, -48.8365f);
        world.DestroyBody(body);
    }

    [Fact]
    public void JoltAngularClampCapsTheMagnitudeAtSeven()
    {
        using JoltPhysicsWorld world = new(PhysicsVector3.Zero);
        PhysicsBodyId capped = world.CreateBody(Body(
            PhysicsVector3.Zero,
            PhysicsVector3.Zero,
            maximumAngularSpeed: ProjectAngularCap,
            angularVelocity: new PhysicsVector3(0f, 0f, 100f)));
        PhysicsBodyId uncapped = world.CreateBody(Body(
            new PhysicsVector3(20f, 0f, 0f),
            PhysicsVector3.Zero,
            angularVelocity: new PhysicsVector3(0f, 0f, 100f)));

        world.Step(FixedTimeStep.FromSeconds(Step));

        PhysicsBodySnapshot[] snapshots = new PhysicsBodySnapshot[2];
        int count = world.CopySnapshots(snapshots);
        Assert.Equal(2, count);
        Assert.Equal(ProjectAngularCap, Assert.Single(snapshots[..count], snapshot => snapshot.Body == capped).AngularVelocity.Z, precision: 3);
        // A zero cap means "no clamp" in PigForge's contract, which Jolt's own zero would not: it
        // would freeze the spin, so the backend has to translate it.
        Assert.Equal(100f, Assert.Single(snapshots[..count], snapshot => snapshot.Body == uncapped).AngularVelocity.Z, precision: 2);

        world.DestroyBody(capped);
        world.DestroyBody(uncapped);
    }

    [Fact]
    public void JoltAngularDampingMatchesTheOriginalsFactor()
    {
        using JoltPhysicsWorld world = new(PhysicsVector3.Zero);
        PhysicsBodyId body = world.CreateBody(Body(
            PhysicsVector3.Zero,
            PhysicsVector3.Zero,
            angularDamping: AngularDrag,
            angularVelocity: new PhysicsVector3(0f, 0f, 5f)));

        for (int tick = 0; tick < 60; tick++)
        {
            world.Step(FixedTimeStep.FromSeconds(Step));
        }

        PhysicsBodySnapshot[] snapshots = new PhysicsBodySnapshot[1];
        _ = world.CopySnapshots(snapshots);
        Assert.Equal(5f * MathF.Pow(1f - (AngularDrag * Step), 60f), snapshots[0].AngularVelocity.Z, precision: 4);
        world.DestroyBody(body);
    }
}
