using PigForge.Physics.Abstractions;
using PigForge.Physics.Bepu;

namespace PigForge.Physics.Tests;

/// <summary>
/// The boxing glove's joint on the real Bepu backend (docs/specs/boxing-glove.md §3, §6): a
/// <see cref="PhysicsJointKind.Configurable"/> drive that throws a 0.5 kg sphere down the host's
/// local -Y to the skin's distance, overshoots it, and then winds home limp. The reference is the
/// original's own probe (tasks/boxing-glove-probe.json, 2021.3.45f2): 380 N/m of drive and 3.5
/// N·s/m of damper carry the glove 2.5 m out and past it before it settles.
/// </summary>
public sealed class BoxingGloveJointTests
{
    private const float Gravity = 9.81f;

    private static readonly FixedTimeStep Step = FixedTimeStep.FromSeconds(1f / 60f);

    /// <summary>The original's measured throw: 2.5 m out, peaked at 3.248 m after 0.1 s.</summary>
    private const float ProbeDistance = 2.5f;

    private const float ProbePeak = 3.248f;

    /// <summary>The original's own home threshold for the wind-back, 0.1 m
    /// (<c>SpringBoxingGlove.cs:280-330</c>; the spec's §4 exit test).</summary>
    private const float HomeOffset = 0.1f;

    [Fact]
    public void TheDriveThrowsTheGloveToTheSkinsDistanceAndOvershootsIt()
    {
        using BepuPhysicsWorld world = new(new PhysicsVector3(0f, -Gravity, 0f));
        (PhysicsBodyId host, PhysicsBodyId glove, PhysicsJointId joint) =
            ThrowRig(world, driveTarget: 2.5f, driveSpring: 380f, driveDamper: 3.5f);
        Assert.True(joint.IsValid);

        float peak = 0f;
        float settled = 0f;
        for (int tick = 0; tick < 180; tick++)
        {
            world.Step(Step);
            float offset = Offset(world, host, glove);
            peak = MathF.Max(peak, offset);
            settled = offset;
        }

        // The throw reaches the skin's own distance and stays there: the probe's 2.5 m, within the
        // ±10% the acceptance allows.
        Assert.InRange(settled, ProbeDistance * 0.9f, ProbeDistance * 1.1f);
        // And it is a real throw, not a slow slide: the drive's damping ratio is low enough that the
        // glove passes the target first. The probe's own overshoot is 30% (3.248/2.5).
        Assert.True(peak > ProbeDistance * 1.05f, $"peak {peak} must overshoot the {ProbeDistance} m target");
        Assert.True(peak >= ProbePeak * 0.9f, $"peak {peak} must be near the probe's {ProbePeak} m");
    }

    [Fact]
    public void TheLockedAxesHoldTheGloveOnItsLine()
    {
        using BepuPhysicsWorld world = new(new PhysicsVector3(0f, -Gravity, 0f));
        (PhysicsBodyId host, PhysicsBodyId glove, _) = ThrowRig(world, driveTarget: 2.5f, driveSpring: 380f, driveDamper: 3.5f);

        for (int tick = 0; tick < 180; tick++)
        {
            world.Step(Step);
        }

        PhysicsBodySnapshot hostSnapshot = Snapshot(world, host);
        PhysicsBodySnapshot gloveSnapshot = Snapshot(world, glove);
        // The glove is a ball on a rail: the lateral (x) and locked (z) axes keep it exactly on the
        // line through the part's origin, and its rotation is held at the part's own (angular XYZ
        // Locked). Only the driven -Y offset is free.
        Assert.Equal(hostSnapshot.Position.X, gloveSnapshot.Position.X, precision: 4);
        Assert.Equal(hostSnapshot.Position.Z, gloveSnapshot.Position.Z, precision: 4);
        Assert.Equal(0f, RelativeYaw(hostSnapshot.Rotation, gloveSnapshot.Rotation), precision: 4);
    }

    [Fact]
    public void TheWindBackPullsTheLimpGloveHome()
    {
        using BepuPhysicsWorld world = new(new PhysicsVector3(0f, -Gravity, 0f));
        (PhysicsBodyId host, PhysicsBodyId glove, PhysicsJointId throwJoint) =
            ThrowRig(world, driveTarget: 2.5f, driveSpring: 380f, driveDamper: 3.5f);

        // Out for the skin's 0.4 s of shoot time.
        for (int tick = 0; tick < 24; tick++)
        {
            world.Step(Step);
        }

        Assert.True(Offset(world, host, glove) > 1f, "the glove must be out before it winds back");

        // The original's wind-back: target home, a softer drive, the limp mass and no collider
        // (SpringBoxingGlove.cs:280-330). The probe measures it home in 0.22-0.34 s.
        world.DestroyJoint(throwJoint);
        world.SetBodyMass(glove, 0.01f);
        world.SetBodyCollisionEnabled(glove, false);
        PhysicsJointId windJoint = world.CreateJoint(WindBackJoint(host, glove));
        Assert.True(windJoint.IsValid);

        int ticksToHome = 0;
        for (int tick = 0; tick < 180; tick++)
        {
            world.Step(Step);
            ticksToHome++;
            if (MathF.Abs(Offset(world, host, glove)) < HomeOffset)
            {
                break;
            }
        }

        Assert.True(ticksToHome < 180, "the limp glove must come home");
        // Measured 21 ticks = 0.35 s against the probe's 0.34 s for the 2.5 m skin (+3%; the 5 m
        // skin reads 0.22 s there). The band leaves room for the solver's own rounding.
        float seconds = ticksToHome * Step.Seconds;
        Assert.InRange(seconds, 0.2f, 0.5f);
    }

    [Fact]
    public void SetBodyMassRescalesTheInertiaWithoutMovingTheBody()
    {
        using BepuPhysicsWorld world = new(new PhysicsVector3(0f, -Gravity, 0f));
        PhysicsBodyId body = world.CreateBody(new BodyDefinition(
            PhysicsBodyMode.Dynamic,
            new PhysicsVector3(0f, 1f, 0f),
            PhysicsQuaternion.Identity,
            0.5f,
            new ShapeDefinition[] { new SphereShapeDefinition(0.3f) }));
        // A control that takes the same mass change and no impulse: gravity alone is all it feels.
        PhysicsBodyId control = world.CreateBody(new BodyDefinition(
            new PhysicsVector3(0f, 5f, 0f),
            PhysicsQuaternion.Identity,
            0.5f,
            new ShapeDefinition[] { new SphereShapeDefinition(0.3f) }));

        // Ten times lighter: the same impulse produces ten times the velocity (uniform density, so
        // the shape's inertia follows the mass), and the mass change alone leaves the fall to
        // gravity exactly as it was — the control fell the same distance.
        world.SetBodyMass(body, 0.05f);
        world.SetBodyMass(control, 0.05f);
        world.ApplyCommands(new[] { PhysicsCommand.ApplyImpulse(body, new PhysicsVector3(1f, 0f, 0f), PhysicsVector3.Zero) });
        world.Step(Step);
        PhysicsBodySnapshot dynamic = Snapshot(world, body);
        Assert.Equal(20f, dynamic.LinearVelocity.X, precision: 3);
        float controlFall = Snapshot(world, control).Position.Y - 5f;
        Assert.Equal(controlFall, dynamic.Position.Y - 1f, precision: 6);
    }

    [Fact]
    public void ADisabledColliderGeneratesNoContactPair()
    {
        using BepuPhysicsWorld world = new(new PhysicsVector3(0f, -Gravity, 0f));
        PhysicsBodyId ground = world.CreateBody(new BodyDefinition(
            PhysicsBodyMode.Static,
            PhysicsVector3.Zero,
            PhysicsQuaternion.Identity,
            0f,
            new ShapeDefinition[] { new BoxShapeDefinition(10f, 0.5f, 10f) }));

        // The control pair: the same drop with the collider on lands on the ground.
        PhysicsBodyId landed = world.CreateBody(new BodyDefinition(
            new PhysicsVector3(3f, 4f, 0f),
            PhysicsQuaternion.Identity,
            1f,
            new ShapeDefinition[] { new BoxShapeDefinition(0.5f, 0.5f, 0.5f) }));
        // And the glove's own case: the collider a phase switched off collides with nothing.
        PhysicsBodyId through = world.CreateBody(new BodyDefinition(
            new PhysicsVector3(-3f, 4f, 0f),
            PhysicsQuaternion.Identity,
            1f,
            new ShapeDefinition[] { new BoxShapeDefinition(0.5f, 0.5f, 0.5f) }));
        world.SetBodyCollisionEnabled(through, false);
        // The round trip: off while the body is still in the air, then on again, and it lands.
        PhysicsBodyId reEnabled = world.CreateBody(new BodyDefinition(
            new PhysicsVector3(6f, 4f, 0f),
            PhysicsQuaternion.Identity,
            1f,
            new ShapeDefinition[] { new BoxShapeDefinition(0.5f, 0.5f, 0.5f) }));
        world.SetBodyCollisionEnabled(reEnabled, false);

        PhysicsEvent[] events = new PhysicsEvent[8];
        for (int tick = 0; tick < 120; tick++)
        {
            world.Step(Step);
            if (tick == 20)
            {
                world.SetBodyCollisionEnabled(reEnabled, true);
            }

            int count = world.DrainEvents(events);
            for (int index = 0; index < count; index++)
            {
                if (events[index].Kind is PhysicsEventKind.ContactStarted or PhysicsEventKind.ContactPersisted or PhysicsEventKind.ContactEnded)
                {
                    Assert.NotEqual(through, events[index].BodyA);
                    Assert.NotEqual(through, events[index].BodyB);
                }
            }
        }

        Assert.InRange(Snapshot(world, landed).Position.Y, 0.95f, 1.1f);
        Assert.True(Snapshot(world, through).Position.Y < -1f, "a disabled collider falls through the ground");
        Assert.InRange(Snapshot(world, reEnabled).Position.Y, 0.95f, 1.1f);
    }

    [Fact]
    public void TheThrowRigIsDeterministicAcrossRuns()
    {
        Assert.Equal(RunThrow(), RunThrow());
    }

    private static string RunThrow()
    {
        using BepuPhysicsWorld world = new(new PhysicsVector3(0f, -Gravity, 0f));
        (PhysicsBodyId host, PhysicsBodyId glove, _) = ThrowRig(world, 2.5f, 380f, 3.5f);
        List<string> trace = new();
        for (int tick = 0; tick < 120; tick++)
        {
            world.Step(Step);
            PhysicsBodySnapshot snapshot = Snapshot(world, glove);
            trace.Add($"{tick}:{Offset(world, host, glove):F6}:{snapshot.LinearVelocity.Y:F6}");
        }

        return string.Join("|", trace);
    }

    /// <summary>
    /// A heavy host block with the glove sphere hanging under it on the skin's own joint: the same
    /// shape the room builds, at the extracted numbers of the 28 skin (mass 0.5, limit 1, yDrive
    /// 380/3.5, xDrive 1000/5, deviationX 0). The host is heavy but dynamic, because the physics
    /// contract has no static-dynamic joint — the same stand-in the other fixtures use.
    /// </summary>
    private static (PhysicsBodyId Host, PhysicsBodyId Glove, PhysicsJointId Joint) ThrowRig(
        BepuPhysicsWorld world,
        float driveTarget,
        float driveSpring,
        float driveDamper)
    {
        const float hostMass = 200f;
        const float gloveMass = 0.5f;
        PhysicsBodyId host = world.CreateBody(new BodyDefinition(
            PhysicsBodyMode.Dynamic,
            new PhysicsVector3(0f, 10f, 0f),
            PhysicsQuaternion.Identity,
            hostMass,
            new ShapeDefinition[] { new BoxShapeDefinition(0.5f, 0.5f, 0.5f) },
            constraints: PhysicsConstraintMask.LockPositionX | PhysicsConstraintMask.LockPositionY | PhysicsConstraintMask.LockPositionZ
                | PhysicsConstraintMask.LockRotationX | PhysicsConstraintMask.LockRotationY | PhysicsConstraintMask.LockRotationZ));
        PhysicsBodyId glove = world.CreateBody(new BodyDefinition(
            new PhysicsVector3(0f, 10f, 0f),
            PhysicsQuaternion.Identity,
            gloveMass,
            new ShapeDefinition[] { new SphereShapeDefinition(0.3f) }));

        return (host, glove, world.CreateJoint(ThrowJoint(host, glove, hostMass, gloveMass, driveTarget, driveSpring, driveDamper)));
    }

    private static JointDefinition ThrowJoint(
        PhysicsBodyId host,
        PhysicsBodyId glove,
        float hostMass,
        float gloveMass,
        float driveTarget,
        float driveSpring,
        float driveDamper)
    {
        (float driveFrequency, float driveDampingRatio) = SpringResponse(driveSpring, driveDamper, hostMass, gloveMass);
        // The skin's xDrive: 1000 N/m, 5 N*s/m (PartGlove.XDrive of the extracted content).
        (float lateralFrequency, float lateralDampingRatio) = SpringResponse(1000f, 5f, hostMass, gloveMass);
        // The skin's own shoot limit spring: 0.1 N/m, nearly slack, which is what lets the drive
        // carry the glove past the 1 m band the limit draws at rest (the original rewrites
        // linearLimitSpring.spring on the throw; without it the hard limit stops the glove there).
        (float limitFrequency, float limitDampingRatio) = SpringResponse(0.1f, 0f, hostMass, gloveMass);
        return new JointDefinition(
            PhysicsJointKind.Configurable,
            host,
            glove,
            PhysicsConstraintMask.None,
            breakForce: 0f,
            breakTorque: 0f,
            // Both anchors at the two bodies' origins; the throw runs down A's local -Y, exactly
            // what the room's drive axis is.
            localAnchorA: PhysicsVector3.Zero,
            localAnchorB: PhysicsVector3.Zero,
            restRotation: PhysicsQuaternion.Identity,
            configurable: new ConfigurableJointDefinition(
                DriveAxisInA: new PhysicsVector3(0f, -1f, 0f),
                DriveTargetOffset: driveTarget,
                DriveFrequency: driveFrequency,
                DriveDampingRatio: driveDampingRatio,
                LateralAxisInA: new PhysicsVector3(1f, 0f, 0f),
                LateralTargetOffset: 0f,
                LateralFrequency: lateralFrequency,
                LateralDampingRatio: lateralDampingRatio,
                LimitMinimumOffset: -1f,
                LimitMaximumOffset: 1f,
                LimitFrequency: limitFrequency,
                LimitDampingRatio: limitDampingRatio));
    }

    /// <summary>The wind-back's softer drive, the skin's own wind numbers (25 N/m, 2.5 N·s/m).</summary>
    private static JointDefinition WindBackJoint(PhysicsBodyId host, PhysicsBodyId glove) =>
        ThrowJoint(host, glove, 200f, 0.01f, 0f, 25f, 2.5f);

    /// <summary>Unity's spring and damper in the solver's form over the pair's reduced mass — the
    /// same conversion <c>GameRoom.TrySpringResponse</c> applies to the extracted content.</summary>
    private static (float Frequency, float DampingRatio) SpringResponse(float spring, float damper, float massA, float massB)
    {
        float reducedMass = massA * massB / (massA + massB);
        return (
            MathF.Sqrt(spring / reducedMass) / (2f * MathF.PI),
            damper / (2f * MathF.Sqrt(spring * reducedMass)));
    }

    private static float Offset(IPhysicsWorld world, PhysicsBodyId host, PhysicsBodyId glove)
    {
        PhysicsBodySnapshot hostSnapshot = Snapshot(world, host);
        PhysicsBodySnapshot gloveSnapshot = Snapshot(world, glove);
        // A's local -Y in world space, the throw line.
        PhysicsVector3 axis = hostSnapshot.Rotation.Rotate(new PhysicsVector3(0f, -1f, 0f));
        return PhysicsVector3.Dot(gloveSnapshot.Position - hostSnapshot.Position, axis);
    }

    private static float RelativeYaw(PhysicsQuaternion host, PhysicsQuaternion glove)
    {
        PhysicsQuaternion relative = host.Inverse * glove;
        return 2f * MathF.Atan2(relative.Z, relative.W);
    }

    private static PhysicsBodySnapshot Snapshot(IPhysicsWorld world, PhysicsBodyId body)
    {
        PhysicsBodySnapshot[] snapshots = new PhysicsBodySnapshot[8];
        int count = world.CopySnapshots(snapshots);
        for (int index = 0; index < count; index++)
        {
            if (snapshots[index].Body == body)
            {
                return snapshots[index];
            }
        }

        throw new InvalidOperationException($"Body {body.Value} is not in the world's snapshots.");
    }
}
