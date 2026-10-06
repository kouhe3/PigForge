using PigForge.Protocol;
using PigForge.Server;

namespace PigForge.Server.Tests;

/// <summary>
/// Elasticity regression. The shipping room runs on BepuPhysics, whose material record has no
/// restitution term, so a pig used to land dead flat (0.011 m of recovery from an 8 m drop).
/// Per ADR-002 the pig must bounce off instead, which the rules layer now does by turning the
/// backend's pre-solve contact impact into an impulse pair. These tests pin the behaviour that
/// the backend cannot express on its own, and pin that it stays deterministic.
/// </summary>
public sealed class PigBounceTests
{
    // content/parts.json: partTypeId 4 = pig, restitution 0.5 (original Pig_PhysMat bounciness).
    private const uint PigPartTypeId = 4;
    private const uint PlayerOne = 1;
    private const float DropStartY = 5f;
    private const float FloorTopY = -3f;
    private const int TickBudget = 400;

    [Fact]
    public void DroppedPigReboundsOnTheBepuBackend()
    {
        PigDrop drop = RunDrop();

        Assert.True(
            drop.ImpactSpeed > 10f,
            $"the pig should reach the floor at speed, got {drop.ImpactSpeed:0.###} m/s");
        // Measured 1.415 m for this 7.5 m drop, i.e. a delivered coefficient of 0.387 against the
        // pig's content value of 0.5. Two mechanisms eat the rest and the original has both: the
        // gravity of the one tick the impulse lands on (g / 60 s = 0.163 m/s against a 12.15 m/s
        // impact, ~1.3%), and the pig's own damping -- drag 0.2 / angularDrag 0.05 from
        // BasePart.EnsureRigidbody, which Pig.cs:236-237 writes unchanged (tools/bple-damping) --
        // which costs the rebound 0.35 m of rise (1.766 m before damping was modelled). Before the
        // bounce existed the rise was 0.011 m and with the earlier decayed estimate 1.514 m, so the
        // range still fails loudly on a regression while catching a runaway.
        Assert.InRange(drop.ReboundRise, 1.3f, 1.52f);
    }

    [Fact]
    public void DroppedPigTrajectoryIsDeterministic()
    {
        Assert.Equal(RunDrop().Trace, RunDrop().Trace);
    }

    [Fact]
    public void PigFallsThroughNothingAndSettlesOnTheFloor()
    {
        PigDrop drop = RunDrop();

        Assert.True(drop.RestingY > FloorTopY, $"the pig must rest above the floor, got y={drop.RestingY:0.###}");
    }

    /// <summary>
    /// A landing under the original's own bounce threshold does not bounce: Unity's
    /// <c>Physics.bounceThreshold</c> is BPLE's <c>DynamicsManager.asset</c> <c>m_BounceThreshold: 2</c>,
    /// and PhysX drops a contact's restitution below it (gap G89). A pig settling from a hand's
    /// height must therefore come to rest instead of hopping.
    /// </summary>
    [Fact]
    public void ASlowLandingDoesNotBounce()
    {
        // The pig's sphere chain rests with its origin half a metre above the floor, so starting
        // 0.63 up leaves a 0.13 m fall: about 1.6 m/s, under the original's 2 m/s threshold and
        // over the 0.5 the rules layer used to use.
        PigDrop drop = RunDrop(dropStartY: FloorTopY + 0.63f);

        Assert.InRange(drop.ImpactSpeed, 0.5f, 2f);
        Assert.True(
            drop.ReboundRise < 0.05f,
            $"a landing under the bounce threshold must not rebound, got {drop.ReboundRise:0.###} m");
    }

    private static PigDrop RunDrop(float dropStartY = DropStartY)
    {
        using GameRoom room = PlayHost.CreateSandboxRoom();

        Assert.True(room.Submit(new PlacePartCommand(0, 1, PlayerOne, PigPartTypeId, 20f, dropStartY, 0f, 1f)).IsAccepted);
        Assert.True(room.Submit(new StartSimulationCommand(0, 2, PlayerOne)).IsAccepted);

        List<float> trace = new();
        List<float> verticalSpeeds = new();
        for (int tick = 0; tick < TickBudget; tick++)
        {
            room.Tick();
            if (!TryReadPig(room, out SnapshotEntity pig))
            {
                continue;
            }

            trace.Add(pig.Position.Y);
            verticalSpeeds.Add(pig.LinearVelocity.Y);
        }

        Assert.NotEmpty(trace);

        int lowest = 0;
        for (int index = 1; index < trace.Count; index++)
        {
            if (trace[index] < trace[lowest])
            {
                lowest = index;
            }
        }

        float impactSpeed = 0f;
        for (int index = 0; index <= lowest; index++)
        {
            impactSpeed = MathF.Max(impactSpeed, MathF.Abs(verticalSpeeds[index]));
        }

        float reboundTop = trace[lowest];
        for (int index = lowest; index < trace.Count; index++)
        {
            reboundTop = MathF.Max(reboundTop, trace[index]);
        }

        return new PigDrop(impactSpeed, reboundTop - trace[lowest], trace[^1], trace);
    }

    private static bool TryReadPig(GameRoom room, out SnapshotEntity pig)
    {
        byte[] buffer = new byte[SnapshotFrame.GetMaxByteCount(room.MaxSnapshotEntityCount)];
        Assert.True(room.TryPublishSnapshot(buffer, out int bytesWritten));
        Assert.True(SnapshotFrame.TryDecodeHeader(buffer.AsSpan(0, bytesWritten), out _, out SnapshotFrameReader reader));
        while (reader.TryReadEntity(out SnapshotEntity entity))
        {
            if (entity.PartTypeId == PigPartTypeId && entity.PhysicsBodyId != 0)
            {
                pig = entity;
                return true;
            }
        }

        pig = default;
        return false;
    }

    private readonly record struct PigDrop(float ImpactSpeed, float ReboundRise, float RestingY, List<float> Trace);
}
