using PigForge.Physics.Abstractions;
using PigForge.Physics.Bepu;

namespace PigForge.Physics.Tests;

/// <summary>
/// The contact normal the event stream carries. It is the surface's own geometry, not an impact
/// direction: a driven wheel reads it as the ground it stands on and pushes along its tangent
/// (MotorWheel.cs:288, gap G102), so it must stay the same while a body merely rests on something,
/// and it must tilt with the surface. Each body's side of the pair is oriented so that pushing it
/// along +Normal separates the pair (see <see cref="PhysicsEvent.ContactNormal"/>).
/// </summary>
public sealed class ContactNormalTests
{
    private static readonly FixedTimeStep Tick = FixedTimeStep.FromSeconds(1f / 60f);

    [Fact]
    public void ARestingBodyReportsTheSurfaceItStandsOnEveryTick()
    {
        using BepuPhysicsWorld world = new(new PhysicsVector3(0f, -9.81f, 0f));
        PhysicsBodyId ground = CreateGround(world, 0f);
        PhysicsBodyId body = world.CreateBody(new BodyDefinition(
            PhysicsBodyMode.Dynamic,
            new PhysicsVector3(0f, 0.55f, 0f),
            PhysicsQuaternion.Identity,
            1f,
            new ShapeDefinition[] { new BoxShapeDefinition(0.5f, 0.5f, 0.5f) }));

        List<PhysicsVector3> normals = new();
        for (int tick = 0; tick < 40; tick++)
        {
            world.Step(Tick);
            if (TryFindNormal(world, body, ground, out PhysicsVector3 normal))
            {
                normals.Add(normal);
            }
        }

        // A resting contact does not approach, so an implementation that oriented the normal by the
        // pre-solve relative velocity would report it only on the ticks where something re-approached
        // -- and would flip its sign on the ticks where the body drifted the other way.
        Assert.True(normals.Count >= 30, $"a resting body reports its ground every tick: {normals.Count} of 40");
        Assert.All(normals, normal => Assert.Equal(1f, normal.Y, 3));
        Assert.All(normals, normal => Assert.Equal(0f, normal.X, 3));
        Assert.All(normals, normal => Assert.Equal(0f, normal.Z, 3));
    }

    [Fact]
    public void AContactNormalTiltsWithTheSurfaceItIsOn()
    {
        const float slopeDegrees = 30f;
        float slope = slopeDegrees * MathF.PI / 180f;
        PhysicsVector3 expectedNormal = new(-MathF.Sin(slope), MathF.Cos(slope), 0f);

        using BepuPhysicsWorld world = new(new PhysicsVector3(0f, -9.81f, 0f));
        PhysicsBodyId ground = CreateGround(world, slopeDegrees);
        // A sphere on the ramp: it rolls down, but its contact normal is the ramp's own normal.
        PhysicsBodyId body = world.CreateBody(new BodyDefinition(
            PhysicsBodyMode.Dynamic,
            new PhysicsVector3(expectedNormal.X * 0.55f, expectedNormal.Y * 0.55f, expectedNormal.Z * 0.55f),
            PhysicsQuaternion.Identity,
            1f,
            new ShapeDefinition[] { new SphereShapeDefinition(0.5f) }));

        Assert.True(
            TryFindNormalOver(world, body, ground, out PhysicsVector3 measured),
            "the sphere must touch the ramp");

        Assert.Equal(expectedNormal.X, measured.X, 2);
        Assert.Equal(expectedNormal.Y, measured.Y, 2);

        // What the drive does with it: Vector3.Cross(normal, Vector3.forward) is the ground's
        // tangent (MotorWheel.cs:288), and on this ramp it points up-slope. A world axis would be
        // (1, 0) on every one of these cells, which is exactly the defect this covers.
        PhysicsVector3 tangent = new(measured.Y, -measured.X, 0f);
        Assert.Equal(MathF.Cos(slope), tangent.X, 2);
        Assert.Equal(MathF.Sin(slope), tangent.Y, 2);
    }

    private static PhysicsBodyId CreateGround(BepuPhysicsWorld world, float slopeDegrees)
    {
        float slope = slopeDegrees * MathF.PI / 180f;
        PhysicsVector3 normal = new(-MathF.Sin(slope), MathF.Cos(slope), 0f);
        // A big slab whose top face contains the origin, so a body placed against `normal` rests on it.
        return world.CreateBody(new BodyDefinition(
            PhysicsBodyMode.Static,
            new PhysicsVector3(-normal.X * 0.5f, -normal.Y * 0.5f, -normal.Z * 0.5f),
            PhysicsQuaternion.FromZAngle(slope),
            0f,
            new ShapeDefinition[] { new BoxShapeDefinition(30f, 0.5f, 5f) }));
    }

    /// <summary>The body's most upward normal on one tick, looking only at its contact with
    /// <paramref name="ground"/>.</summary>
    private static bool TryFindNormal(BepuPhysicsWorld world, PhysicsBodyId body, PhysicsBodyId ground, out PhysicsVector3 normal)
    {
        normal = default;
        PhysicsEvent[] events = new PhysicsEvent[64];
        int count = world.DrainEvents(events);
        bool found = false;
        for (int index = 0; index < count; index++)
        {
            PhysicsEvent @event = events[index];
            if (@event.Kind is not (PhysicsEventKind.ContactStarted or PhysicsEventKind.ContactPersisted)
                || @event.BodyA == @event.BodyB
                || (@event.BodyA != body && @event.BodyB != body)
                || (@event.BodyA != ground && @event.BodyB != ground))
            {
                continue;
            }

            PhysicsVector3 candidate = @event.BodyA == body
                ? @event.ContactNormal
                : new PhysicsVector3(-@event.ContactNormal.X, -@event.ContactNormal.Y, -@event.ContactNormal.Z);
            if (!found || candidate.Y > normal.Y)
            {
                normal = candidate;
                found = true;
            }
        }

        return found;
    }

    private static bool TryFindNormalOver(BepuPhysicsWorld world, PhysicsBodyId body, PhysicsBodyId ground, out PhysicsVector3 normal)
    {
        normal = default;
        for (int tick = 0; tick < 30; tick++)
        {
            world.Step(Tick);
            if (TryFindNormal(world, body, ground, out normal))
            {
                return true;
            }
        }

        return false;
    }
}
