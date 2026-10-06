using PigForge.Physics.Abstractions;
using PigForge.Physics.Bepu;

namespace PigForge.Physics.Tests;

/// <summary>
/// The frame-chain acceptance for the weld work (docs/specs/weld-compliance.md §4.1/§4.4).
/// <para>
/// The original does not merge two frames into one rigid body: it keeps two bodies and locks all six
/// degrees of freedom with a real joint (<c>Contraption.cs:1507-1546</c>), and a chain of eight of
/// them sags visibly under its own weight. Measured on the original's own editor (2021.3.45f2) with
/// its own physics settings, cell <c>chain8_ppon_gap0</c> of <c>tasks/weld-compliance-probe.json</c>
/// reads: tip drop <b>1.8415 m</b>, worst single joint <b>10.175 deg</b>, total curvature
/// <b>22.661 deg</b> (all as the largest excursion of a 6 s run).
/// </para>
/// <para>
/// PigForge reproduces that with a compliant <see cref="JointDefinition.Weld"/> instead of PhysX's
/// iterative joint residual, at the fitted frequency/damping pair below. Both remaining deviations
/// are the ones the spec's ±25% band leaves room for: the solver (Bepu vs PhysX 4.1) and the tick
/// rate (PigForge 60 Hz vs the original's 50 Hz).
/// </para>
/// </summary>
public sealed class WeldComplianceTests
{
    /// <summary>The fitted compliance of one frame-to-frame weld, in Hz: the value that brings the
    /// chain below into the original's band. It mirrors <c>CompoundAssembler.FrameWeldSpringFrequency</c>
    /// in PigForge.Core (this project deliberately sits below Core, and
    /// <c>CompoundAssemblerTests.TheFrameWeldComplianceIsTheFittedPair</c> pins the shipped value to
    /// this same pair — change one and that test tells you).</summary>
    private const float FittedFrequency = 20f;

    private const float FittedDampingRatio = 1f;

    private const float ProbeTipDrop = 1.8415f;
    private const float ProbeMaxJointDegrees = 10.175f;
    private const float ProbeSumJointDegrees = 22.661f;

    /// <summary>The spec's ±25% band around every probe number.</summary>
    private const float Tolerance = 0.25f;

    [Fact]
    public void TheBepuChainSagsLikeTheOriginalFrameChain()
    {
        using BepuPhysicsWorld world = new(PhysicsVector3.Zero);
        WeldChainScenario.ChainMeasurement measurement =
            WeldChainScenario.Run(world, FittedFrequency, FittedDampingRatio, steps: 360);

        Assert.InRange(measurement.TipDrop, -ProbeTipDrop * (1f + Tolerance), -ProbeTipDrop * (1f - Tolerance));
        Assert.InRange(measurement.MaxJointAngleDegrees, ProbeMaxJointDegrees * (1f - Tolerance), ProbeMaxJointDegrees * (1f + Tolerance));
        Assert.InRange(measurement.SumJointAngleDegrees, ProbeSumJointDegrees * (1f - Tolerance), ProbeSumJointDegrees * (1f + Tolerance));
    }

    [Fact]
    public void TheBepuChainSagIsDeterministic()
    {
        using BepuPhysicsWorld first = new(PhysicsVector3.Zero);
        using BepuPhysicsWorld second = new(PhysicsVector3.Zero);

        Assert.Equal(
            WeldChainScenario.Run(first, FittedFrequency, FittedDampingRatio, steps: 360),
            WeldChainScenario.Run(second, FittedFrequency, FittedDampingRatio, steps: 360));
    }

    /// <summary>
    /// The non-vacuous half of the acceptance: a rigid weld — the backend's own 30 Hz
    /// <c>RigidSpring</c>, i.e. what the joint would be if the fitted compliance were dropped —
    /// holds the chain far stiffer than the original ever does. Without the fitted spring the
    /// numbers above would not be reachable.
    /// </summary>
    [Fact]
    public void ARigidWeldDoesNotReproduceTheOriginalFrameChainSag()
    {
        using BepuPhysicsWorld world = new(PhysicsVector3.Zero);
        WeldChainScenario.ChainMeasurement measurement =
            WeldChainScenario.Run(world, springFrequency: 0f, springDampingRatio: 1f, steps: 360);

        Assert.True(
            measurement.SumJointAngleDegrees < ProbeSumJointDegrees * (1f - Tolerance),
            $"a rigid weld must be stiffer than the original's chain, but curved {measurement.SumJointAngleDegrees:F3} deg");
    }
}

/// <summary>
/// The eight-frame hanging chain of the original baseline: one fixed frame, seven welded below it,
/// each a 1x1x1 box of 0.5 kg with the original's rigidbody setup, sagging under gravity. Three of
/// the original's settings exist here too: the frames are jointed but still collide face to face
/// (the original's 1x1x1 grid), every body freezes Z and the X/Y rotations
/// (<c>RigidbodyConstraints</c> 56), and the root is immovable. The frames also carry the original's
/// per-part drag pair through <see cref="WeldChainScenario.FrameLinearDamping"/> /
/// <see cref="WeldChainScenario.FrameAngularDamping"/> — the probe's chain is made of real parts, and
/// so is the same chain when a room builds it from content (ADR-025), so an undamped fixture would
/// fit the weld spring against something the game never runs.
/// <para>
/// The original holds the chain on a <em>kinematic</em> rigidbody. The physics contract has no joint
/// between a static and a dynamic body (Bepu refuses it), so the root is a dynamic body frozen on
/// every axis (<see cref="AnchorLock"/>) — an immovable body by another route — and the chain's
/// weight is fed in as one impulse per tick (<c>m g dt</c> at each body's own centre) over a
/// gravity-free world, which is exactly what the original's gravity does to the same chain.
/// </para>
/// </summary>
internal static class WeldChainScenario
{
    public const int FrameCount = 8;
    public const float TimeStepSeconds = 1f / 60f;

    private const float Gravity = 9.81f;
    private const float FrameMass = 0.5f;

    /// <summary>An immovable root still needs a mass for the solver's effective mass, and the
    /// original's kinematic anchor is infinitely heavy: 1000 kg against the chain's 3.5 kg keeps the
    /// root's own share of the load negligible (the probe's rig cell makes the same substitution
    /// with 50 kg).</summary>
    private const float AnchorMass = 1000f;

    private const float HalfExtent = 0.5f;
    private const float Spacing = 1f;
    private const PhysicsConstraintMask PlanarLock =
        PhysicsConstraintMask.LockPositionZ | PhysicsConstraintMask.LockRotationX | PhysicsConstraintMask.LockRotationY;

    /// <summary>Every degree of freedom locked; the physics contract's way of being immovable and
    /// the stand-in for the original's kinematic rigidbody.</summary>
    private const PhysicsConstraintMask AnchorLock =
        PhysicsConstraintMask.LockPositionX | PhysicsConstraintMask.LockPositionY | PhysicsConstraintMask.LockPositionZ
        | PhysicsConstraintMask.LockRotationX | PhysicsConstraintMask.LockRotationY | PhysicsConstraintMask.LockRotationZ;

    /// <summary>Tip drop is negative, the way the probe records it.</summary>
    public readonly record struct ChainMeasurement(float TipDrop, float MaxJointAngleDegrees, float SumJointAngleDegrees);

    /// <summary>The per-part rigidbody pair every original part carries (<c>BasePart.EnsureRigidbody</c>,
    /// <c>BasePart.cs:1200-1201</c>, extracted by <c>tools/bple-damping</c>): the framed chain in the
    /// original's probe is made of real parts, so its frames are damped — and so is the same chain
    /// when a room builds it from content. The anchor is immovable, so its own pair cannot matter.</summary>
    public const float FrameLinearDamping = 0.2f;

    public const float FrameAngularDamping = 0.05f;

    public static ChainMeasurement Run(IPhysicsWorld world, float springFrequency, float springDampingRatio, int steps)
    {
        PhysicsBodyId[] frames = new PhysicsBodyId[FrameCount];
        frames[0] = world.CreateBody(new BodyDefinition(
            PhysicsBodyMode.Dynamic,
            new PhysicsVector3(0f, 0f, 0f),
            PhysicsQuaternion.Identity,
            AnchorMass,
            new ShapeDefinition[] { new BoxShapeDefinition(HalfExtent, HalfExtent, HalfExtent) },
            constraints: AnchorLock));
        for (int index = 1; index < FrameCount; index++)
        {
            frames[index] = world.CreateBody(new BodyDefinition(
                PhysicsBodyMode.Dynamic,
                new PhysicsVector3(index * Spacing, 0f, 0f),
                PhysicsQuaternion.Identity,
                FrameMass,
                new ShapeDefinition[] { new BoxShapeDefinition(HalfExtent, HalfExtent, HalfExtent) },
                constraints: PlanarLock,
                linearDamping: FrameLinearDamping,
                angularDamping: FrameAngularDamping));
        }

        for (int index = 1; index < FrameCount; index++)
        {
            // The original's anchors: half the vector to the other part's origin, in each part's
            // own frame (Contraption.AddFixedJoint: `InverseTransformPoint(other.position) * 0.5f`).
            PhysicsJointId joint = world.CreateJoint(JointDefinition.Weld(
                frames[index - 1],
                frames[index],
                localAnchorA: new PhysicsVector3(HalfExtent, 0f, 0f),
                localAnchorB: new PhysicsVector3(-HalfExtent, 0f, 0f),
                springFrequency: springFrequency,
                springDampingRatio: springDampingRatio));
            if (!joint.IsValid)
            {
                throw new InvalidOperationException("The backend refused a chain weld joint.");
            }
        }

        FixedTimeStep timeStep = FixedTimeStep.FromSeconds(TimeStepSeconds);
        PhysicsEvent[] events = new PhysicsEvent[64];
        PhysicsBodySnapshot[] snapshots = new PhysicsBodySnapshot[FrameCount];
        PhysicsVector3[] centre = new PhysicsVector3[FrameCount];
        for (int index = 0; index < FrameCount; index++)
        {
            centre[index] = new PhysicsVector3(index * Spacing, 0f, 0f);
        }

        PhysicsCommand[] weight = new PhysicsCommand[FrameCount - 1];
        float tipDrop = 0f;
        float maxJoint = 0f;
        float maxSum = 0f;
        for (int step = 0; step < steps; step++)
        {
            for (int index = 1; index < FrameCount; index++)
            {
                weight[index - 1] = PhysicsCommand.ApplyImpulse(
                    frames[index],
                    new PhysicsVector3(0f, -FrameMass * Gravity * TimeStepSeconds, 0f),
                    centre[index]);
            }

            world.ApplyCommands(weight);
            world.Step(timeStep);
            _ = world.DrainEvents(events);
            int count = world.CopySnapshots(snapshots);
            float[] angles = new float[FrameCount];
            float tip = 0f;
            float root = 0f;
            for (int index = 0; index < count; index++)
            {
                int slot = SlotOf(frames, snapshots[index].Body);
                if (slot < 0)
                {
                    continue;
                }

                angles[slot] = ZAngle(snapshots[index].Rotation);
                centre[slot] = snapshots[index].Position;
                if (slot == FrameCount - 1)
                {
                    tip = snapshots[index].Position.Y;
                }
                else if (slot == 0)
                {
                    root = snapshots[index].Position.Y;
                }
            }

            // The probe's three numbers are the largest excursion of a 6 s run, so the same
            // running maxima are what get compared.
            tipDrop = MathF.Min(tipDrop, tip - root);
            float sum = 0f;
            for (int index = 1; index < FrameCount; index++)
            {
                float relative = Normalize(angles[index] - angles[index - 1]);
                sum += relative;
                maxJoint = MathF.Max(maxJoint, MathF.Abs(relative));
            }

            maxSum = MathF.Max(maxSum, MathF.Abs(sum));
        }

        return new ChainMeasurement(tipDrop, maxJoint, maxSum);
    }

    private static int SlotOf(PhysicsBodyId[] frames, PhysicsBodyId body)
    {
        for (int index = 0; index < frames.Length; index++)
        {
            if (frames[index] == body)
            {
                return index;
            }
        }

        return -1;
    }

    private static float ZAngle(PhysicsQuaternion rotation) =>
        MathF.Atan2(
            2f * ((rotation.W * rotation.Z) + (rotation.X * rotation.Y)),
            1f - (2f * ((rotation.Y * rotation.Y) + (rotation.Z * rotation.Z)))) * (180f / MathF.PI);

    private static float Normalize(float degrees)
    {
        while (degrees > 180f)
        {
            degrees -= 360f;
        }

        while (degrees < -180f)
        {
            degrees += 360f;
        }

        return degrees;
    }
}
