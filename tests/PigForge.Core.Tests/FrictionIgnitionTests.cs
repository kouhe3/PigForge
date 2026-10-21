using PigForge.Core.Content;
using PigForge.Physics.Abstractions;
using PigForge.Physics.Bepu;

namespace PigForge.Core.Tests;

/// <summary>
/// The whole ignition path on a real solver: a charge dragged across a surface at speed lights up
/// even though it never approaches that surface, because the original compares the pair's relative
/// velocity magnitude (<c>TNT.cs:80</c>: <c>c.relativeVelocity.magnitude &gt; m_triggerSpeed</c>).
/// Nothing here is synthesized: Bepu reports the contact, and the rules decide.
/// </summary>
public sealed class FrictionIgnitionTests
{
    private static readonly FixedTimeStep Tick = FixedTimeStep.FromSeconds(1f / 60f);

    [Fact]
    public void AChargeDraggedAcrossTheGroundIgnitesOnItsOwnRelativeSpeed()
    {
        Assert.True(ChargeSlidingAt(6f), "a 6 m/s slide beats the 5 m/s trigger");
    }

    [Fact]
    public void ASlowSlideStaysUnderTheTrigger()
    {
        Assert.False(ChargeSlidingAt(2f), "a 2 m/s slide is not an impact");
    }

    private static bool ChargeSlidingAt(float speed)
    {
        EntityStore entities = new();
        PhysicsBodyStore bodies = new(entities);
        GameplayConfig config = new(
            new GameplayZone(new PhysicsVector3(-100f, -500f, -2f), new PhysicsVector3(100f, -499f, 2f)),
            new GameplayZone(new PhysicsVector3(-1000f, -1000f, -1000f), new PhysicsVector3(1000f, 1000f, 1000f)),
            TntBlastRadius: 4f,
            TntBlastImpulse: 25f,
            TntIgniteImpactSpeed: 5f);
        GameplayRules rules = new(
            entities,
            new MotorStore(entities),
            new BalloonStore(entities),
            new FanStore(entities),
            new RocketStore(entities),
            new TntStore(entities),
            new BlasterStore(entities),
            new GlueStore(entities),
            new WheelStore(entities),
            new PigStore(entities),
            new EggStore(entities),
            new WingStore(entities),
            new TailStore(entities),
            new UmbrellaStore(entities),
            new GearboxStore(entities),
            new BellowsStore(entities),
            new DetacherStore(entities),
            new GrappleStore(entities),
            new ActivationStore(entities),
            new RestitutionStore(entities),
            new PowerStore(entities),
            bodies,
            config);

        using BepuPhysicsWorld world = new(new PhysicsVector3(0f, -9.81f, 0f));
        PhysicsMaterial slippery = new(Restitution: 0f, Friction: 0f);
        world.CreateBody(new BodyDefinition(
            PhysicsBodyMode.Static,
            new PhysicsVector3(0f, -0.5f, 0f),
            PhysicsQuaternion.Identity,
            0f,
            [new BoxShapeDefinition(30f, 0.5f, 5f)],
            material: slippery));
        // The charge itself: a unit box already resting on the slab, moving across it.
        PhysicsBodyId chargeBody = world.CreateBody(new BodyDefinition(
            PhysicsBodyMode.Dynamic,
            new PhysicsVector3(0f, 0.5f, 0f),
            PhysicsQuaternion.Identity,
            1f,
            [new BoxShapeDefinition(0.5f, 0.5f, 0.5f)],
            new PhysicsVector3(speed, 0f, 0f),
            PhysicsVector3.Zero,
            material: slippery));

        EntityId charge = entities.Create();
        bodies.Set(charge, new PhysicsBodyLink(chargeBody));
        rules.LinkBody(charge, chargeBody);
        rules.AddTnt(charge, fuseTicks: 0, triggerSpeed: 5f);

        PhysicsEvent[] events = new PhysicsEvent[64];
        PhysicsBodySnapshot[] snapshots = new PhysicsBodySnapshot[8];
        GameplayTickOutput output = new();
        for (uint tick = 1; tick <= 30; tick++)
        {
            world.Step(Tick);
            int eventCount = world.DrainEvents(events);
            int snapshotCount = world.CopySnapshots(snapshots);
            output.Clear();
            rules.Tick(tick, events.AsSpan(0, eventCount).ToArray(), snapshots.AsSpan(0, snapshotCount), output);
            if (output.DestroyedEntities.Contains(charge))
            {
                return true;
            }
        }

        return false;
    }
}
