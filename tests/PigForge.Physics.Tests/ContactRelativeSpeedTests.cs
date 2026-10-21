using PigForge.Physics.Abstractions;
using PigForge.Physics.Bepu;

namespace PigForge.Physics.Tests;

/// <summary>
/// <see cref="PhysicsEvent.RelativeSpeed"/> beside <see cref="PhysicsEvent.ApproachSpeed"/>: the
/// original's charge and its level crate test the relative-velocity *magnitude*
/// (<c>TNT.cs:80</c> / <c>TNTBox.cs:24</c>: <c>c.relativeVelocity.magnitude</c>), so a body dragged
/// across a surface must report a large relative speed while its approach along that surface's
/// normal stays at zero. Both readings come from the same pre-solve velocities in the narrow phase.
/// </summary>
public sealed class ContactRelativeSpeedTests
{
    private static readonly FixedTimeStep Tick = FixedTimeStep.FromSeconds(1f / 60f);

    [Fact]
    public void ASlidingBodyReportsItsSpeedWhileItsApproachStaysZero()
    {
        using BepuPhysicsWorld world = new(new PhysicsVector3(0f, -9.81f, 0f));
        // A frictionless slab whose top face is y = 0, and a box already resting on it.
        PhysicsBodyId ground = world.CreateBody(new BodyDefinition(
            PhysicsBodyMode.Static,
            new PhysicsVector3(0f, -0.5f, 0f),
            PhysicsQuaternion.Identity,
            0f,
            [new BoxShapeDefinition(30f, 0.5f, 5f)],
            material: new PhysicsMaterial(Restitution: 0f, Friction: 0f)));
        PhysicsBodyId slider = world.CreateBody(new BodyDefinition(
            PhysicsBodyMode.Dynamic,
            new PhysicsVector3(0f, 0.5f, 0f),
            PhysicsQuaternion.Identity,
            1f,
            [new BoxShapeDefinition(0.5f, 0.5f, 0.5f)],
            new PhysicsVector3(6f, 0f, 0f),
            PhysicsVector3.Zero,
            material: new PhysicsMaterial(Restitution: 0f, Friction: 0f)));

        // The first few ticks are the box settling onto the slab; from there it only slides.
        List<PhysicsEvent> contacts = [];
        for (int tick = 0; tick < 20; tick++)
        {
            world.Step(Tick);
            if (tick >= 5)
            {
                contacts.AddRange(DrainContacts(world, slider, ground));
            }
            else
            {
                world.DrainEvents(new PhysicsEvent[64]);
            }
        }

        Assert.NotEmpty(contacts);
        // The body moves across the surface, not into it: the projected approach is nothing ...
        Assert.All(contacts, contact => Assert.InRange(contact.ApproachSpeed, 0f, 0.2f));
        // ... while the magnitude is the slide itself, which is what the original's charge compares.
        Assert.All(contacts, contact => Assert.InRange(contact.RelativeSpeed, 5.5f, 6.2f));
    }

    [Fact]
    public void ARestingPairReportsNoSpeedToIgniteOn()
    {
        using BepuPhysicsWorld world = new(new PhysicsVector3(0f, -9.81f, 0f));
        PhysicsBodyId ground = world.CreateBody(new BodyDefinition(
            PhysicsBodyMode.Static,
            new PhysicsVector3(0f, -0.5f, 0f),
            PhysicsQuaternion.Identity,
            0f,
            [new BoxShapeDefinition(30f, 0.5f, 5f)]));
        PhysicsBodyId resting = world.CreateBody(new BodyDefinition(
            new PhysicsVector3(0f, 0.5f, 0f),
            PhysicsQuaternion.Identity,
            1f,
            [new BoxShapeDefinition(0.5f, 0.5f, 0.5f)]));

        // A vehicle parked on the ground must never be holding an ignition speed: the pair reports a
        // contact every tick, and the only velocity in it is one tick of gravity before the solver
        // cancels it (9.81 / 60 = 0.16 m/s), well under any trigger in the content (4 for a charge,
        // 2 for the level crate).
        List<PhysicsEvent> contacts = [];
        for (int tick = 0; tick < 30; tick++)
        {
            world.Step(Tick);
            if (tick >= 5)
            {
                contacts.AddRange(DrainContacts(world, resting, ground));
            }
            else
            {
                world.DrainEvents(new PhysicsEvent[64]);
            }
        }

        Assert.NotEmpty(contacts);
        Assert.All(contacts, contact => Assert.InRange(contact.RelativeSpeed, 0f, 0.5f));
    }

    private static List<PhysicsEvent> DrainContacts(BepuPhysicsWorld world, PhysicsBodyId first, PhysicsBodyId second)
    {
        PhysicsEvent[] events = new PhysicsEvent[64];
        int count = world.DrainEvents(events);
        List<PhysicsEvent> contacts = [];
        for (int index = 0; index < count; index++)
        {
            PhysicsEvent @event = events[index];
            if (@event.Kind is PhysicsEventKind.ContactStarted or PhysicsEventKind.ContactPersisted
                && (@event.BodyA == first || @event.BodyB == first)
                && (@event.BodyA == second || @event.BodyB == second))
            {
                contacts.Add(@event);
            }
        }

        return contacts;
    }
}
