using PigForge.Physics.Abstractions;
using PigForge.Physics.Bepu;

namespace PigForge.Physics.Tests;

/// <summary>
/// The original's per-rigidbody defaults on the Bepu backend: Unity's <c>Rigidbody.drag</c> /
/// <c>angularDrag</c> and the project-wide <c>maxAngularVelocity</c>. PhysX applies them in
/// <c>DyBodyCoreIntegrator.h::bodyCoreComputeUnconstrainedVelocity</c> as gravity first, then
/// <c>v *= fsel(1 - damping * dt, 1 - damping * dt, 0)</c>, then a magnitude clamp
/// <c>w *= sqrt(maxSq / |w|^2)</c>. The formulas and every number below were measured on the
/// original's own editor (Unity 2021.3.45f2, real PhysX 4.1) by
/// <c>unity/PigForge.WeldProbe</c>'s body-defaults probe; its 50 Hz reference series is quoted
/// where the discrete step differs from PigForge's 60 Hz.
/// </summary>
public sealed class BodyDampingTests
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
    public void LinearDampingDecaysTheVelocityByTheOriginalsFactor()
    {
        using BepuPhysicsWorld world = new(PhysicsVector3.Zero);
        PhysicsBodyId body = world.CreateBody(Body(PhysicsVector3.Zero, new PhysicsVector3(10f, 0f, 0f), damping: LinearDrag));
        FixedTimeStep timeStep = FixedTimeStep.FromSeconds(Step);

        // Measured on the original: v *= (1 - drag * dt), exactly -- 0.996 per 0.02 s step for
        // 0.2 drag, with a maximum absolute error of 0 over 51 samples against that prediction
        // (neither 1/(1 + c*dt) nor exp(-c*dt) fits).
        float factor = 1f - (LinearDrag * Step);
        float previousX = 10f;
        PhysicsBodySnapshot[] snapshots = new PhysicsBodySnapshot[1];
        for (int tick = 0; tick < 60; tick++)
        {
            world.Step(timeStep);
            Assert.Equal(1, world.CopySnapshots(snapshots));
            Assert.Equal(previousX * factor, snapshots[0].LinearVelocity.X, precision: 4);
            previousX = snapshots[0].LinearVelocity.X;
        }

        // 10 * (1 - 0.2/60)^60 = 8.18496 m/s after one second.
        Assert.Equal(8.184958f, snapshots[0].LinearVelocity.X, precision: 3);
        world.DestroyBody(body);
    }

    [Fact]
    public void AngularDampingDecaysTheSpinByTheOriginalsFactor()
    {
        using BepuPhysicsWorld world = new(PhysicsVector3.Zero);
        // The probe kept the seed below the 7 rad/s clamp (5 rad/s) so the clamp cannot mask the
        // decay: measured omega *= (1 - angularDrag * dt) = 0.999 per 0.02 s step.
        PhysicsBodyId body = world.CreateBody(Body(
            PhysicsVector3.Zero,
            PhysicsVector3.Zero,
            angularDamping: AngularDrag,
            angularVelocity: new PhysicsVector3(0f, 0f, 5f)));
        FixedTimeStep timeStep = FixedTimeStep.FromSeconds(Step);

        float factor = 1f - (AngularDrag * Step);
        PhysicsBodySnapshot[] snapshots = new PhysicsBodySnapshot[1];
        for (int tick = 0; tick < 60; tick++)
        {
            world.Step(timeStep);
            _ = world.CopySnapshots(snapshots);
        }

        Assert.Equal(5f * MathF.Pow(factor, 60f), snapshots[0].AngularVelocity.Z, precision: 4);
        world.DestroyBody(body);
    }

    [Fact]
    public void FreeFallWithTheOriginalsDragReachesTheDiscreteTerminalSpeed()
    {
        using BepuPhysicsWorld world = new(new PhysicsVector3(0f, -9.81f, 0f));
        PhysicsBodyId body = world.CreateBody(Body(PhysicsVector3.Zero, PhysicsVector3.Zero, damping: LinearDrag));
        FixedTimeStep timeStep = FixedTimeStep.FromSeconds(Step);

        // gravity before damping, as PhysX does: v <- (v + g*dt) * (1 - c*dt), whose fixed point is
        // (g/c)(1 - c*dt) = 49.05 * 0.996667 = 48.8865 m/s. The probe measured the same law at the
        // original's 50 Hz: 48.85183 m/s after 50 s against the predicted 48.8538.
        for (int tick = 0; tick < 3000; tick++)
        {
            world.Step(timeStep);
        }

        PhysicsBodySnapshot[] snapshots = new PhysicsBodySnapshot[1];
        _ = world.CopySnapshots(snapshots);
        float terminal = 9.81f * (1f - (LinearDrag * Step)) / LinearDrag;
        Assert.Equal(48.8865f, terminal, precision: 3);
        Assert.InRange(snapshots[0].LinearVelocity.Y, -terminal - 0.05f, -terminal + 0.05f);
        world.DestroyBody(body);
    }

    [Fact]
    public void TheAngularClampCapsTheMagnitudeAtTheProjectsSevenRadiansPerSecond()
    {
        using BepuPhysicsWorld world = new(PhysicsVector3.Zero);
        PhysicsBodyId capped = world.CreateBody(Body(
            PhysicsVector3.Zero,
            PhysicsVector3.Zero,
            maximumAngularSpeed: ProjectAngularCap,
            angularVelocity: new PhysicsVector3(0f, 0f, 100f)));
        PhysicsBodyId uncapped = world.CreateBody(Body(
            new PhysicsVector3(20f, 0f, 0f),
            PhysicsVector3.Zero,
            maximumAngularSpeed: 1000f,
            angularVelocity: new PhysicsVector3(0f, 0f, 100f)));
        PhysicsBodyId below = world.CreateBody(Body(
            new PhysicsVector3(40f, 0f, 0f),
            PhysicsVector3.Zero,
            maximumAngularSpeed: ProjectAngularCap,
            angularVelocity: new PhysicsVector3(0f, 0f, 3f)));

        world.Step(FixedTimeStep.FromSeconds(Step));

        PhysicsBodySnapshot[] snapshots = new PhysicsBodySnapshot[3];
        int count = world.CopySnapshots(snapshots);
        Assert.Equal(3, count);
        // Measured on the original: a body seeded at 100 rad/s reads exactly 7 after one step, and
        // the same seed with the cap raised to 1000 rad/s runs to 100 -- so the clamp is real and
        // magnitude-based, not a per-axis one.
        PhysicsBodySnapshot cappedSnapshot = Assert.Single(snapshots[..count], snapshot => snapshot.Body == capped);
        Assert.Equal(ProjectAngularCap, cappedSnapshot.AngularVelocity.Z, precision: 3);
        PhysicsBodySnapshot uncappedSnapshot = Assert.Single(snapshots[..count], snapshot => snapshot.Body == uncapped);
        Assert.Equal(100f, uncappedSnapshot.AngularVelocity.Z, precision: 3);
        PhysicsBodySnapshot belowSnapshot = Assert.Single(snapshots[..count], snapshot => snapshot.Body == below);
        Assert.Equal(3f, belowSnapshot.AngularVelocity.Z, precision: 3);

        world.DestroyBody(capped);
        world.DestroyBody(uncapped);
        world.DestroyBody(below);
    }

    [Fact]
    public void ZeroDampingAndZeroCapLeaveABodyExactlyAsTheContractAsked()
    {
        // The contract's zero means "no damping" and "no clamp" (PigForge's own convention).
        using BepuPhysicsWorld world = new(PhysicsVector3.Zero);
        PhysicsBodyId body = world.CreateBody(Body(
            PhysicsVector3.Zero,
            new PhysicsVector3(4f, 0f, 0f),
            angularVelocity: new PhysicsVector3(0f, 0f, 100f)));
        FixedTimeStep timeStep = FixedTimeStep.FromSeconds(Step);
        for (int tick = 0; tick < 20; tick++)
        {
            world.Step(timeStep);
        }

        PhysicsBodySnapshot[] snapshots = new PhysicsBodySnapshot[1];
        _ = world.CopySnapshots(snapshots);
        Assert.Equal(4f, snapshots[0].LinearVelocity.X, precision: 4);
        Assert.Equal(100f, snapshots[0].AngularVelocity.Z, precision: 3);
        world.DestroyBody(body);
    }

    [Fact]
    public void TheContractRejectsNegativeDampingAndACapOfNoUse()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Body(PhysicsVector3.Zero, PhysicsVector3.Zero, damping: -0.1f));
        Assert.Throws<ArgumentOutOfRangeException>(() => Body(PhysicsVector3.Zero, PhysicsVector3.Zero, angularDamping: -0.1f));
        Assert.Throws<ArgumentOutOfRangeException>(() => Body(PhysicsVector3.Zero, PhysicsVector3.Zero, maximumAngularSpeed: -1f));
    }
}
