using PigForge.Physics.Abstractions;
using PigForge.Physics.Bepu;

namespace PigForge.Physics.Tests;

/// <summary>
/// The elastic wheel attachment on the real Bepu backend: the revolute joint that spins the
/// wheel about its axle, plus the sprung linear degree of freedom the original declares
/// (OffRoadWheel.CustomConnectToPart, OffRoadWheel.cs:202-220, carried by
/// <see cref="JointDefinition.LocalSuspensionAxis"/>). The spring numbers here are the
/// extracted originals — 50 N/m and 5 N*s/m from tools/bple-springs — converted to the
/// solver's frequency/damping-ratio pair over the pair's reduced mass exactly like
/// GameRoom.TrySpringResponse does, so the measured sag (load per unit stiffness) also checks
/// that the conversion and the solver's spring agree.
/// </summary>
public sealed class WheelSuspensionTests
{
    private const float Gravity = 9.81f;
    private const float SpringStiffness = 50f;
    private const float SpringDamper = 5f;
    private const float WheelMass = 1f;
    private const float WheelRadius = 0.5f;
    private const float ChassisMass = 2f;

    private static readonly PhysicsVector3 WheelPosition = new(0f, WheelRadius, 0f);
    private static readonly PhysicsVector3 ChassisPosition = new(0f, 3f, 0f);

    /// <summary>The wheel centre in the chassis' frame: two and a half units below its centre.</summary>
    private static readonly PhysicsVector3 ChassisAnchor = new(0f, -2.5f, 0f);

    /// <summary>The suspension axis of the original's joint: the wheel's own Y, perpendicular
    /// to its axle (the joint's default axes, so the original serializes no axis at all).</summary>
    private static readonly PhysicsVector3 SuspensionAxis = new(0f, 1f, 0f);
    private static readonly PhysicsVector3 AxleAxis = new(0f, 0f, 1f);
    private static readonly FixedTimeStep Step = FixedTimeStep.FromSeconds(1f / 60f);

    /// <summary>The chassis' weight is what the spring carries: sag = mass * g / stiffness.</summary>
    private static float ExpectedSag => (ChassisMass * Gravity) / SpringStiffness;

    [Fact]
    public void SprungWheelDeflectsUnderLoadAndReturnsToRest()
    {
        using BepuPhysicsWorld world = new(new PhysicsVector3(0f, -Gravity, 0f));
        (PhysicsBodyId wheel, PhysicsBodyId chassis) = BuildCart(world, sprung: true);

        for (int tick = 0; tick < 240; tick++)
        {
            world.Step(Step);
            _ = world.DrainEvents(new PhysicsEvent[8]);
        }

        // Loaded: the chassis hangs from the spring, and the wheel's centre sits above its rest
        // position relative to the chassis' anchor by the load over the extracted stiffness.
        float loaded = Offset(world, wheel, chassis).Axial;
        Assert.InRange(loaded, ExpectedSag * 0.8f, ExpectedSag * 1.2f);
        Assert.True(loaded > 0.1f, $"the suspension must visibly deflect: {loaded}");

        // Unloaded: cancel the chassis' weight every tick and the spring pulls the axle back to
        // its rest offset (the wheel stays on the ground, its own weight carried by contact).
        PhysicsVector3 cancel = new(0f, ChassisMass * Gravity * Step.Seconds, 0f);
        for (int tick = 0; tick < 240; tick++)
        {
            world.ApplyCommands(new[] { PhysicsCommand.ApplyImpulse(chassis, cancel, BodyPosition(world, chassis)) });
            world.Step(Step);
            _ = world.DrainEvents(new PhysicsEvent[8]);
        }

        float unloaded = Offset(world, wheel, chassis).Axial;
        Assert.True(
            MathF.Abs(unloaded) < 0.02f,
            $"the suspension must return to its rest offset when unloaded: {loaded} -> {unloaded}");
    }

    [Fact]
    public void SprungWheelStillSpinsAboutItsAxleAndHoldsItsLine()
    {
        // No ground: nothing loads the spring, so the pair keeps its rest offset while the
        // wheel spins. The angular hinge must let the spin through and lock the other two
        // rotations; the line lock must keep the wheel from sliding off its axle.
        using BepuPhysicsWorld world = new(new PhysicsVector3(0f, -Gravity, 0f));
        (PhysicsBodyId wheel, PhysicsBodyId chassis) = BuildCart(
            world,
            sprung: true,
            angularVelocity: new PhysicsVector3(0f, 0f, 6f),
            includeGround: false);

        for (int tick = 0; tick < 120; tick++)
        {
            world.Step(Step);
            _ = world.DrainEvents(new PhysicsEvent[8]);
        }

        PhysicsBodySnapshot wheelSnapshot = Snapshot(world, wheel);
        PhysicsBodySnapshot chassisSnapshot = Snapshot(world, chassis);
        Assert.InRange(wheelSnapshot.AngularVelocity.Z, 5f, 7f);
        Assert.InRange(wheelSnapshot.AngularVelocity.X, -0.05f, 0.05f);
        Assert.InRange(wheelSnapshot.AngularVelocity.Y, -0.05f, 0.05f);
        Assert.True(
            MathF.Abs(chassisSnapshot.AngularVelocity.Z) < 0.05f,
            $"the chassis must not be spun by the wheel: {chassisSnapshot.AngularVelocity.Z}");

        (float axial, float lateral) = Offset(world, wheel, chassis);
        Assert.True(MathF.Abs(axial) < 0.02f, $"an unloaded spring stays at its rest offset: {axial}");
        Assert.True(lateral < 0.02f, $"the wheel must not slide off the suspension line: {lateral}");
    }

    [Fact]
    public void RigidWheelJointHoldsItsAxleUnderTheSameLoad()
    {
        // The contrast that keeps every other wheel untouched: without a suspension axis the
        // same fixture is the rigid axle PigForge has always built, and the load barely moves it.
        using BepuPhysicsWorld world = new(new PhysicsVector3(0f, -Gravity, 0f));
        (PhysicsBodyId wheel, PhysicsBodyId chassis) = BuildCart(world, sprung: false);

        for (int tick = 0; tick < 240; tick++)
        {
            world.Step(Step);
            _ = world.DrainEvents(new PhysicsEvent[8]);
        }

        float offset = Offset(world, wheel, chassis).Axial;
        Assert.True(MathF.Abs(offset) < 0.01f, $"a rigid axle must not deflect under load: {offset}");
    }

    [Fact]
    public void SprungWheelRunIsDeterministic()
    {
        long first = RunLoadedCart();
        long second = RunLoadedCart();
        Assert.Equal(first, second);
    }

    /// <summary>One deterministic scripted run of the loaded cart; returns a state hash.</summary>
    private static long RunLoadedCart()
    {
        using BepuPhysicsWorld world = new(new PhysicsVector3(0f, -Gravity, 0f));
        (PhysicsBodyId wheel, _) = BuildCart(world, sprung: true);
        for (int tick = 0; tick < 300; tick++)
        {
            if (tick == 150)
            {
                world.ApplyCommands(new[]
                {
                    PhysicsCommand.ApplyImpulse(wheel, new PhysicsVector3(3f, 0f, 0f), BodyPosition(world, wheel)),
                });
            }

            world.Step(Step);
            _ = world.DrainEvents(new PhysicsEvent[8]);
        }

        return HashSnapshots(world);
    }

    /// <summary>Ground, a wheel resting on it and a chassis above, joined at the wheel's centre.</summary>
    private static (PhysicsBodyId Wheel, PhysicsBodyId Chassis) BuildCart(
        BepuPhysicsWorld world,
        bool sprung,
        PhysicsVector3 angularVelocity = default,
        bool includeGround = true)
    {
        if (includeGround)
        {
            world.CreateBody(new BodyDefinition(
                PhysicsBodyMode.Static,
                new PhysicsVector3(0f, -1f, 0f),
                PhysicsQuaternion.Identity,
                mass: 0f,
                new ShapeDefinition[] { new BoxShapeDefinition(20f, 1f, 20f) }));
        }

        PhysicsBodyId wheel = world.CreateBody(new BodyDefinition(
            WheelPosition,
            PhysicsQuaternion.Identity,
            WheelMass,
            new ShapeDefinition[] { new SphereShapeDefinition(WheelRadius) },
            angularVelocity: angularVelocity));
        PhysicsBodyId chassis = world.CreateBody(new BodyDefinition(
            ChassisPosition,
            PhysicsQuaternion.Identity,
            ChassisMass,
            new ShapeDefinition[] { new BoxShapeDefinition(0.5f, 0.5f, 0.5f) }));
        (float frequency, float dampingRatio) = SpringResponse(ChassisMass, WheelMass);
        world.CreateJoint(new JointDefinition(
            PhysicsJointKind.Revolute,
            chassis,
            wheel,
            PhysicsConstraintMask.None,
            breakForce: 0f,
            breakTorque: 0f,
            localAnchorA: ChassisAnchor,
            localAnchorB: PhysicsVector3.Zero,
            localAxisA: AxleAxis,
            localAxisB: AxleAxis,
            springFrequency: sprung ? frequency : 0f,
            springDampingRatio: sprung ? dampingRatio : 1f,
            localSuspensionAxis: sprung ? SuspensionAxis : PhysicsVector3.Zero,
            suspensionRestOffset: 0f));
        return (wheel, chassis);
    }

    /// <summary>Unity's spring and damper in the solver's form over the pair's reduced mass —
    /// the same conversion GameRoom.TrySpringResponse applies to extracted content.</summary>
    private static (float Frequency, float DampingRatio) SpringResponse(float massA, float massB)
    {
        float reducedMass = massA * massB / (massA + massB);
        return (
            MathF.Sqrt(SpringStiffness / reducedMass) / (2f * MathF.PI),
            SpringDamper / (2f * MathF.Sqrt(SpringStiffness * reducedMass)));
    }

    /// <summary>The wheel centre's offset from the chassis' anchor: along the suspension axis
    /// (the sprung degree of freedom) and perpendicular to it (the rigid line lock).</summary>
    private static (float Axial, float Lateral) Offset(BepuPhysicsWorld world, PhysicsBodyId wheel, PhysicsBodyId chassis)
    {
        PhysicsBodySnapshot wheelSnapshot = Snapshot(world, wheel);
        PhysicsBodySnapshot chassisSnapshot = Snapshot(world, chassis);
        PhysicsVector3 axis = chassisSnapshot.Rotation.Rotate(SuspensionAxis);
        PhysicsVector3 anchor = chassisSnapshot.Position + chassisSnapshot.Rotation.Rotate(ChassisAnchor);
        PhysicsVector3 delta = wheelSnapshot.Position - anchor;
        float axial = PhysicsVector3.Dot(delta, axis);
        return (axial, PhysicsVector3.Distance(delta, axis * axial));
    }

    private static PhysicsVector3 BodyPosition(BepuPhysicsWorld world, PhysicsBodyId body) =>
        Snapshot(world, body).Position;

    private static PhysicsBodySnapshot Snapshot(BepuPhysicsWorld world, PhysicsBodyId body)
    {
        PhysicsBodySnapshot[] snapshots = new PhysicsBodySnapshot[8];
        int count = world.CopySnapshots(snapshots);
        foreach (PhysicsBodySnapshot snapshot in snapshots[..count])
        {
            if (snapshot.Body == body)
            {
                return snapshot;
            }
        }

        throw new InvalidOperationException($"Body {body.Value} is not in the world's snapshots.");
    }

    private static long HashSnapshots(BepuPhysicsWorld world)
    {
        PhysicsBodySnapshot[] snapshots = new PhysicsBodySnapshot[8];
        int count = world.CopySnapshots(snapshots);
        long hash = 17;
        for (int index = 0; index < count; index++)
        {
            PhysicsBodySnapshot snapshot = snapshots[index];
            hash = unchecked((hash * 31) + snapshot.Body.Value);
            hash = Mix(hash, snapshot.Position);
            hash = Mix(hash, snapshot.Rotation);
            hash = Mix(hash, snapshot.AngularVelocity);
        }

        return hash;
    }

    private static long Mix(long hash, PhysicsVector3 value)
    {
        hash = unchecked((hash * 31) + BitConverter.SingleToInt32Bits(value.X));
        hash = unchecked((hash * 31) + BitConverter.SingleToInt32Bits(value.Y));
        return unchecked((hash * 31) + BitConverter.SingleToInt32Bits(value.Z));
    }

    private static long Mix(long hash, PhysicsQuaternion value)
    {
        hash = unchecked((hash * 31) + BitConverter.SingleToInt32Bits(value.X));
        hash = unchecked((hash * 31) + BitConverter.SingleToInt32Bits(value.Y));
        hash = unchecked((hash * 31) + BitConverter.SingleToInt32Bits(value.Z));
        return unchecked((hash * 31) + BitConverter.SingleToInt32Bits(value.W));
    }
}
