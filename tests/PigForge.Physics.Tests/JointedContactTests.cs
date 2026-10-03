using PigForge.Physics.Abstractions;
using PigForge.Physics.Bepu;

namespace PigForge.Physics.Tests;

/// <summary>
/// Which jointed pairs skip contact generation. The original suppresses exactly two cases:
/// a frame-enclosed part (one rigid body, so the pair cannot collide by construction —
/// <c>Frame.cs:44-52</c>) and a wheel's tire against the parent it hinges to (PigForge keeps
/// those as one mechanism on purpose, ADR-009). A runtime rope is a Unity <c>SpringJoint</c>
/// and never calls <c>Physics.IgnoreCollision</c> (<c>Sandbag.cs:136-164</c>,
/// <c>Balloon.cs:143-166</c>), so a sandbag or balloon collides with the part it is tied to.
/// </summary>
public sealed class JointedContactTests
{
    [Fact]
    public void BepuWorldGeneratesContactsBetweenRopeJointedBodies()
    {
        using BepuPhysicsWorld world = new(PhysicsVector3.Zero);
        PhysicsBodyId first = OverlappingSphere(world, new PhysicsVector3(-0.1f, 0f, 0f));
        PhysicsBodyId second = OverlappingSphere(world, new PhysicsVector3(0.1f, 0f, 0f));
        world.CreateJoint(new JointDefinition(
            PhysicsJointKind.Distance,
            first,
            second,
            PhysicsConstraintMask.None,
            breakForce: 0f,
            breakTorque: 0f,
            minimumDistance: 0f,
            maximumDistance: 2f,
            springFrequency: 5f,
            springDampingRatio: 1f));

        Assert.True(HasContact(world, first, second), "an overlapping rope pair must collide (original SpringJoint does)");
    }

    [Fact]
    public void BepuWorldSuppressesContactsBetweenHingedBodies()
    {
        using BepuPhysicsWorld world = new(PhysicsVector3.Zero);
        PhysicsBodyId wheel = OverlappingSphere(world, new PhysicsVector3(-0.1f, 0f, 0f));
        PhysicsBodyId parent = OverlappingSphere(world, new PhysicsVector3(0.1f, 0f, 0f));
        world.CreateJoint(new JointDefinition(
            PhysicsJointKind.Revolute,
            parent,
            wheel,
            PhysicsConstraintMask.None,
            breakForce: 0f,
            breakTorque: 0f,
            localAxisA: new PhysicsVector3(0f, 0f, 1f),
            localAxisB: new PhysicsVector3(0f, 0f, 1f)));

        Assert.False(HasContact(world, wheel, parent), "a hinged wheel and its parent are one mechanism and must not collide");
    }

    private static PhysicsBodyId OverlappingSphere(BepuPhysicsWorld world, PhysicsVector3 position) =>
        world.CreateBody(new BodyDefinition(
            PhysicsBodyMode.Dynamic,
            position,
            PhysicsQuaternion.Identity,
            1f,
            new ShapeDefinition[] { new SphereShapeDefinition(0.5f) }));

    private static bool HasContact(BepuPhysicsWorld world, PhysicsBodyId first, PhysicsBodyId second)
    {
        PhysicsEvent[] events = new PhysicsEvent[64];
        for (int tick = 0; tick < 5; tick++)
        {
            world.Step(FixedTimeStep.FromSeconds(1f / 60f));
            int count = world.DrainEvents(events);
            for (int index = 0; index < count; index++)
            {
                PhysicsEvent @event = events[index];
                if (@event.Kind is PhysicsEventKind.ContactStarted or PhysicsEventKind.ContactPersisted
                    && ((@event.BodyA == first && @event.BodyB == second) || (@event.BodyA == second && @event.BodyB == first)))
                {
                    return true;
                }
            }
        }

        return false;
    }
}
