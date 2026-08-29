using PigForge.Physics.Abstractions;
using PigForge.Physics.Bepu;
using PigForge.Physics.Jolt;

namespace PigForge.Physics.Tests;

public sealed class JoltPhysicsContractTests
{
    [Fact]
    public void JoltWorldDropsDynamicBoxOntoGroundAndPublishesContact()
    {
        using JoltPhysicsWorld world = new(new PhysicsVector3(0, -9.81f, 0));
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
    public void JoltWorldAppliesImpulseBeforeFixedStep()
    {
        using JoltPhysicsWorld world = new(new PhysicsVector3(0, 0, 0));
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
    public void JoltWorldRunsTwiceWithIdenticalSnapshotSequence()
    {
        Assert.Equal(RunDeterministicBoxDrop(), RunDeterministicBoxDrop());
    }

    [Fact]
    public void JoltWorldRejectsUnsupportedShapeAndJoint()
    {
        using JoltPhysicsWorld world = new(PhysicsVector3.Zero);
        Assert.Throws<NotSupportedException>(() => world.CreateBody(new BodyDefinition(
            PhysicsBodyMode.Dynamic,
            PhysicsVector3.Zero,
            PhysicsQuaternion.Identity,
            1,
            new ShapeDefinition[] { new UnsupportedShapeDefinition() })));
        Assert.Empty(world.Capabilities.SupportedJointKinds);
    }

    [Fact]
    public void DisposedJoltWorldRejectsFurtherOperations()
    {
        JoltPhysicsWorld world = new(PhysicsVector3.Zero);
        world.Dispose();

        Assert.Throws<ObjectDisposedException>(() => world.Step(FixedTimeStep.FromSeconds(1f / 60f)));
        world.Dispose();
    }

    [Fact]
    public void JoltAndBepuBackendsProduceComparableBoxDropOutcomes()
    {
        BackendRun joltRun = RunBoxDrop(new JoltPhysicsWorld(new PhysicsVector3(0, -9.81f, 0)));
        BackendRun bepuRun = RunBoxDrop(new BepuPhysicsWorld(new PhysicsVector3(0, -9.81f, 0)));

        // Both backends must agree on the event-level story and land in the same
        // neighbourhood; exact trajectories legitimately differ between solvers.
        Assert.Equal(joltRun.ContactStartedGroundWithBox, bepuRun.ContactStartedGroundWithBox);
        Assert.InRange(joltRun.FinalBoxY, 0.95f, 1.1f);
        Assert.InRange(bepuRun.FinalBoxY, 0.95f, 1.1f);
        Assert.True(MathF.Abs(joltRun.FinalBoxY - bepuRun.FinalBoxY) < 0.05f,
            $"Final box heights diverge: Jolt {joltRun.FinalBoxY} vs Bepu {bepuRun.FinalBoxY}");
        Assert.Equal(joltRun.ImpulseVelocityY, bepuRun.ImpulseVelocityY, precision: 4);
    }

    private static float RunDeterministicBoxDrop()
    {
        using JoltPhysicsWorld world = new(new PhysicsVector3(0, -9.81f, 0));
        _ = world.CreateBody(new BodyDefinition(
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

        FixedTimeStep timeStep = FixedTimeStep.FromSeconds(1f / 60f);
        for (int tick = 0; tick < 120; tick++)
        {
            world.ApplyCommands(ReadOnlySpan<PhysicsCommand>.Empty);
            world.Step(timeStep);
        }

        PhysicsBodySnapshot[] snapshots = new PhysicsBodySnapshot[2];
        world.CopySnapshots(snapshots);
        return Assert.Single(snapshots, snapshot => snapshot.Body == box).Position.Y;
    }

    private static BackendRun RunBoxDrop(IPhysicsWorld world)
    {
        using (world)
        {
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
            world.CopySnapshots(snapshots);
            float finalBoxY = Assert.Single(snapshots, snapshot => snapshot.Body == box).Position.Y;

            PhysicsBodyId freeBody = world.CreateBody(new BodyDefinition(
                PhysicsBodyMode.Dynamic,
                new PhysicsVector3(0, 10, 0),
                PhysicsQuaternion.Identity,
                2,
                new ShapeDefinition[] { new BoxShapeDefinition(0.5f, 0.5f, 0.5f) }));
            _ = world.DrainEvents(events);
            world.ApplyCommands(new[]
            {
                PhysicsCommand.ApplyImpulse(freeBody, new PhysicsVector3(0, 4, 0), PhysicsVector3.Zero)
            });
            world.Step(timeStep);
            PhysicsBodySnapshot[] freeSnapshots = new PhysicsBodySnapshot[3];
            world.CopySnapshots(freeSnapshots);
            float impulseVelocityY = Assert.Single(freeSnapshots, snapshot => snapshot.Body == freeBody).LinearVelocity.Y;

            return new BackendRun(contactStarted, finalBoxY, impulseVelocityY);
        }
    }

    private readonly record struct BackendRun(bool ContactStartedGroundWithBox, float FinalBoxY, float ImpulseVelocityY);

    private sealed record UnsupportedShapeDefinition() : ShapeDefinition(PhysicsShapeKind.Sphere);
}
