using PigForge.Physics.Abstractions;
using PigForge.Physics.Bepu;

namespace PigForge.Physics.Tests;

/// <summary>
/// The 2.5D lock (<see cref="BodyDefinition.Constraints"/>) on the real Bepu backend. The
/// original freezes the Z translation and the X/Y rotations on every contraption body
/// (<c>RigidbodyConstraints</c> 56 — <c>Sandbag.cs:135</c>, <c>Pig.cs:235</c>), so the build
/// plane is the only plane a body can leave; PigForge carries that as a per-body mask and the
/// room applies it to every body it creates.
/// </summary>
public sealed class PlanarConstraintTests
{
    /// <summary>What the room applies: freeze Z translation, X/Y rotation; Z spin and X/Y
    /// translation stay free, so a wheel still rolls and a rig still drives.</summary>
    private const PhysicsConstraintMask PlanarLock =
        PhysicsConstraintMask.LockPositionZ | PhysicsConstraintMask.LockRotationX | PhysicsConstraintMask.LockRotationY;

    [Fact]
    public void BepuWorldFreezesZTranslationAndOutOfPlaneRotation()
    {
        using BepuPhysicsWorld world = new(new PhysicsVector3(0f, -9.81f, 0f));
        PhysicsBodyId locked = Launch(world, new PhysicsVector3(0f, 5f, 0f), PlanarLock);
        PhysicsBodyId free = Launch(world, new PhysicsVector3(10f, 5f, 0f), PhysicsConstraintMask.None);

        for (int tick = 0; tick < 120; tick++)
        {
            world.Step(FixedTimeStep.FromSeconds(1f / 60f));
        }

        PhysicsBodySnapshot[] snapshots = new PhysicsBodySnapshot[2];
        Assert.Equal(2, world.CopySnapshots(snapshots));
        PhysicsBodySnapshot lockedBody = snapshots.Single(snapshot => snapshot.Body == locked);
        PhysicsBodySnapshot freeBody = snapshots.Single(snapshot => snapshot.Body == free);

        // The locked body keeps its plane and its in-plane spin (Z 1 rad/s from the launch)...
        Assert.Equal(0f, lockedBody.Position.Z);
        Assert.Equal(0f, lockedBody.Rotation.X);
        Assert.Equal(0f, lockedBody.Rotation.Y);
        Assert.Equal(0f, lockedBody.AngularVelocity.X);
        Assert.Equal(0f, lockedBody.AngularVelocity.Y);
        Assert.Equal(1f, lockedBody.AngularVelocity.Z, precision: 4);
        Assert.Equal(0.841471f, lockedBody.Rotation.Z, precision: 5);
        Assert.Equal(6f, lockedBody.Position.X, precision: 3);

        // ...while the control body, launched identically but unmasked, leaves the plane.
        Assert.True(freeBody.Position.Z > 1f, $"an unmasked body must leave the plane: z={freeBody.Position.Z}");
        Assert.True(MathF.Abs(freeBody.Rotation.X) > 0.1f, $"an unmasked body must tumble: qx={freeBody.Rotation.X}");
    }

    [Fact]
    public void BepuWorldKeepsARopePairPlanarAcrossTheSwing()
    {
        // The sandbag/balloon case: a rope pulls a body around for hundreds of ticks. Nothing
        // may leak into Z or into out-of-plane rotation, however hard the rope torques it.
        using BepuPhysicsWorld world = new(new PhysicsVector3(0f, -9.81f, 0f));
        PhysicsBodyId anchor = world.CreateBody(new BodyDefinition(
            PhysicsBodyMode.Dynamic,
            new PhysicsVector3(0f, 4f, 0f),
            PhysicsQuaternion.Identity,
            1f,
            new ShapeDefinition[] { new BoxShapeDefinition(0.5f, 0.5f, 0.5f) },
            default,
            default,
            null,
            PlanarLock));
        PhysicsBodyId hanging = world.CreateBody(new BodyDefinition(
            PhysicsBodyMode.Dynamic,
            new PhysicsVector3(0f, 2.6f, 0f),
            PhysicsQuaternion.Identity,
            3f,
            new ShapeDefinition[] { new SphereShapeDefinition(0.13f) },
            default,
            default,
            null,
            PlanarLock));
        world.CreateJoint(new JointDefinition(
            PhysicsJointKind.Distance,
            anchor,
            hanging,
            PhysicsConstraintMask.None,
            breakForce: 0f,
            breakTorque: 0f,
            // The anchor point sits a centimetre off the plane, exactly like the sandbag's
            // extracted attachment offset (content/parts.json `attachment.offset` z = -0.01).
            localAnchorA: new PhysicsVector3(0f, -0.5f, -0.01f),
            localAnchorB: new PhysicsVector3(0f, 0.5f, 0f),
            minimumDistance: 0f,
            maximumDistance: 0.5f,
            springFrequency: 5f,
            springDampingRatio: 0.5f));

        // Kick the hanging body sideways so the rope actually swings it.
        world.ApplyCommands(new[]
        {
            PhysicsCommand.ApplyImpulse(hanging, new PhysicsVector3(3f, 0f, 0f), new PhysicsVector3(0f, 2.6f, 0f))
        });
        PhysicsBodySnapshot[] samples = new PhysicsBodySnapshot[2];
        float swung = 0f;
        for (int tick = 0; tick < 600; tick++)
        {
            world.Step(FixedTimeStep.FromSeconds(1f / 60f));
            int sampleCount = world.CopySnapshots(samples);
            foreach (PhysicsBodySnapshot sample in samples[..sampleCount])
            {
                // Every tick of the swing: the frozen axes never move, not even by a fraction
                // of a degree, and nothing leaks into the plane's normal.
                Assert.Equal(0f, sample.Position.Z);
                Assert.Equal(0f, sample.Rotation.X);
                Assert.Equal(0f, sample.Rotation.Y);
                Assert.Equal(0f, sample.AngularVelocity.X);
                Assert.Equal(0f, sample.AngularVelocity.Y);
                if (sample.Body == hanging)
                {
                    swung = MathF.Max(swung, MathF.Abs(sample.Position.X));
                }
            }
        }

        Assert.True(swung > 0.2f, $"the rope must actually swing the body: max |x| = {swung}");

        PhysicsBodySnapshot[] snapshots = new PhysicsBodySnapshot[2];
        Assert.Equal(2, world.CopySnapshots(snapshots));
        PhysicsBodySnapshot hangingBody = snapshots.Single(snapshot => snapshot.Body == hanging);
        PhysicsBodySnapshot anchorBody = snapshots.Single(snapshot => snapshot.Body == anchor);
        Assert.True(
            anchorBody.Position.Y - hangingBody.Position.Y < 3f,
            $"the rope still ties the pair: {anchorBody.Position.Y - hangingBody.Position.Y}");
    }

    private static PhysicsBodyId Launch(BepuPhysicsWorld world, PhysicsVector3 position, PhysicsConstraintMask constraints) =>
        world.CreateBody(new BodyDefinition(
            PhysicsBodyMode.Dynamic,
            position,
            PhysicsQuaternion.Identity,
            1f,
            new ShapeDefinition[] { new BoxShapeDefinition(0.5f, 0.5f, 0.5f) },
            new PhysicsVector3(3f, 0f, 2f),
            new PhysicsVector3(4f, 7f, 1f),
            null,
            constraints));
}
