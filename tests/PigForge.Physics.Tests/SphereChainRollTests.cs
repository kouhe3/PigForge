using PigForge.Physics.Abstractions;
using PigForge.Physics.Bepu;

namespace PigForge.Physics.Tests;

/// <summary>
/// The approximation <c>tools/bple-shapes</c> uses for the original's capsules (the physics contract
/// has no capsule shape): a chain of spheres of the capsule's own radius along its own axis, which is
/// what the king pig's and the egg's collision is built from. A chain of spheres rolls -- given a
/// push on flat ground, friction spins it up to <c>|v| = |omega| * radius</c> and it keeps rolling --
/// whereas the envelope box it replaced (the king pig's old square collision) slides, because a flat
/// face gives friction no lever arm about the centre of mass.
/// </summary>
public sealed class SphereChainRollTests
{
    private const float Radius = 0.9f;
    private const float Reach = 0.15f; // the king pig's capsule: height/2 - radius
    private const float Push = 5f;

    private static PhysicsBodyId[] GroundAndBody(IPhysicsWorld world, PhysicsBodyId body)
    {
        PhysicsBodyId floor = world.CreateBody(new BodyDefinition(
            PhysicsBodyMode.Static,
            // A thin slab whose top face is y = 0, so the chain's spheres (centred 0.9 above their
            // contact) sit on it with the part origin 0.5 up.
            new PhysicsVector3(0f, -0.5f, 0f),
            PhysicsQuaternion.Identity,
            mass: 0,
            new ShapeDefinition[] { new BoxShapeDefinition(20f, 0.5f, 2f) }));
        return new[] { floor, body };
    }

    [Fact]
    public void AChainOfSpheresRollsAfterAPush()
    {
        using BepuPhysicsWorld world = new(new PhysicsVector3(0f, -9.81f, 0f));
        CompoundShapeDefinition chain = new(new[]
        {
            new CompoundChild(new SphereShapeDefinition(Radius), new PhysicsVector3(-Reach, 0.4f, 0f)),
            new CompoundChild(new SphereShapeDefinition(Radius), new PhysicsVector3(Reach, 0.4f, 0f)),
        });
        PhysicsBodyId pig = world.CreateBody(new BodyDefinition(
            PhysicsBodyMode.Dynamic,
            new PhysicsVector3(0f, 0.45f, 0f),
            PhysicsQuaternion.Identity,
            mass: 4f,
            new ShapeDefinition[] { chain },
            new PhysicsVector3(Push, 0f, 0f)));
        _ = GroundAndBody(world, pig);

        FixedTimeStep timeStep = FixedTimeStep.FromSeconds(1f / 60f);
        for (int tick = 0; tick < 120; tick++)
        {
            world.Step(timeStep);
        }

        PhysicsBodySnapshot[] snapshots = new PhysicsBodySnapshot[2];
        int count = world.CopySnapshots(snapshots);
        PhysicsBodySnapshot pigSnapshot = Assert.Single(snapshots[..count], snapshot => snapshot.Body == pig);

        // Rolling: the contact point is the sphere's surface, so the centre travels `radius` per
        // radian. Friction has spun the chain up by now (it started sliding at 5 m/s with no spin).
        float speed = pigSnapshot.LinearVelocity.X;
        float spin = MathF.Abs(pigSnapshot.AngularVelocity.Z);
        Assert.True(speed > 1f, $"the chain must keep moving: {speed} m/s");
        Assert.True(spin > 1f, $"a round body spins as it rolls: {spin} rad/s");
        Assert.InRange(speed / spin, Radius * 0.8f, Radius * 1.2f);
        world.DestroyBody(pig);
    }

    [Fact]
    public void TheEnvelopeBoxItReplacedSlidesInstead()
    {
        // The old approximation, same mass and envelope, as one box: pushed the same way it slides
        // on its flat face and barely spins (friction has no lever arm about its centre).
        using BepuPhysicsWorld world = new(new PhysicsVector3(0f, -9.81f, 0f));
        PhysicsBodyId box = world.CreateBody(new BodyDefinition(
            PhysicsBodyMode.Dynamic,
            new PhysicsVector3(0f, 0.45f, 0f),
            PhysicsQuaternion.Identity,
            mass: 4f,
            new ShapeDefinition[] { new BoxShapeDefinition(1.05f, 0.9f, 0.9f) },
            new PhysicsVector3(Push, 0f, 0f)));
        _ = GroundAndBody(world, box);

        FixedTimeStep timeStep = FixedTimeStep.FromSeconds(1f / 60f);
        for (int tick = 0; tick < 120; tick++)
        {
            world.Step(timeStep);
        }

        PhysicsBodySnapshot[] snapshots = new PhysicsBodySnapshot[2];
        int count = world.CopySnapshots(snapshots);
        PhysicsBodySnapshot boxSnapshot = Assert.Single(snapshots[..count], snapshot => snapshot.Body == box);
        Assert.True(
            MathF.Abs(boxSnapshot.AngularVelocity.Z) < 1f,
            $"a box on a flat face does not roll: {boxSnapshot.AngularVelocity.Z} rad/s");
        world.DestroyBody(box);
    }
}
