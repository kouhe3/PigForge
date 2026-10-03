using PigForge.Core;
using PigForge.Core.Content;
using PigForge.Protocol;
using PigForge.Physics.Abstractions;
using PigForge.Server;

namespace PigForge.Server.Tests;

/// <summary>
/// The 2.5D guarantee on a real room and the real Bepu backend: no authoritative body may
/// leave the build plane, through a seam split or through a runtime rope. The original freezes
/// the Z translation and the X/Y rotations on every contraption body
/// (<c>RigidbodyConstraints</c> 56 — <c>Sandbag.cs:135</c>, <c>Pig.cs:235</c>), and PigForge
/// carries that as <c>BodyDefinition.Constraints</c>, applied whenever a body is created — so
/// the halves a seam split creates inherit the lock of the compound they came from.
/// </summary>
public sealed class PlanarStabilityTests
{
    private const uint PartWoodenBlock = 1;  // frame, jointConnectionType source
    private const uint PartDetacher = 43;    // switch-fired seam split (source, activation trigger)
    private const uint PartSandbag = 21;     // runtime rope tie, attachment direction up
    private const uint PartBalloon = 10;     // runtime rope tie, attachment direction down

    /// <summary>The planarity a body must hold: the lock snaps the frozen axes exactly, so these
    /// are equality assertions, not tolerances.</summary>
    private static void AssertPlanar(SnapshotEntity entity, string context)
    {
        Assert.Equal(0f, entity.Position.Z);
        Assert.Equal(0f, entity.Rotation.X);
        Assert.Equal(0f, entity.Rotation.Y);
        Assert.Equal(0f, entity.AngularVelocity.X);
        Assert.Equal(0f, entity.AngularVelocity.Y);
        Assert.True(float.IsFinite(entity.Position.Y), $"{context}: entity {entity.EntityId} left the world");
    }

    [Fact]
    public void SplitHalvesAndTheirSandbagStayInTheBuildPlane()
    {
        SplitScript script = RunSplitScript();

        // The detacher broke its seam and left the rig: the blocks and the detacher are now
        // separate bodies.
        Assert.True(script.SplitBodies > 1, $"the rig must break: distinct bodies {script.SplitBodies}");

        // Everything stays planar for 5 seconds after the break, sandbag included.
        foreach (SnapshotEntity entity in script.Entities)
        {
            AssertPlanar(entity, "after the split");
        }

        Assert.True(
            MathF.Abs(script.SandbagY - script.BlockY) < 2f,
            $"the sandbag stays tied to the rig: sandbag y={script.SandbagY}, rig y={script.BlockY}");
    }

    [Fact]
    public void SplitScriptIsDeterministicAcrossRuns()
    {
        SplitScript first = RunSplitScript();
        SplitScript second = RunSplitScript();

        Assert.Equal(first.Hash, second.Hash);
        Assert.NotEqual(0, first.Hash);
    }

    /// <summary>
    /// Builds a welded wooden rig with a sandbag tied under it, drops it, then fires the
    /// detacher that sits in the rig: the room splits the compound along the seam nearest the
    /// detacher (<c>GameRoom.DetachFromCompound</c>), which is the user's "the vehicle breaks in
    /// the middle" case. From then on both halves and the sandbag must stay in the X-Y plane.
    /// </summary>
    private static SplitScript RunSplitScript()
    {
        using GameRoom room = PlayHost.CreateSandboxRoom();
        uint player = PlayHost.NextPlayerId();
        uint sequence = 0;

        uint left = Place(room, ref sequence, player, PartWoodenBlock, -2f, -1.4f);
        uint right = Place(room, ref sequence, player, PartWoodenBlock, -1f, -1.4f);
        uint detacher = Place(room, ref sequence, player, PartDetacher, 0f, -1.4f);
        uint sandbag = Place(room, ref sequence, player, PartSandbag, -2f, -2.4f);
        Assert.True(room.Submit(PlayHost.BindPlayer(new StartSimulationCommand(0, ++sequence, player), player)).IsAccepted);

        // Let the rig fall to the terrain and the sandbag settle into its tie.
        room.RunTicks(120);
        Assert.True(
            room.Submit(PlayHost.BindPlayer(new SetPartActiveCommand(0, ++sequence, player, detacher, true), player)).IsAccepted,
            "the detacher switch must accept");
        room.RunTicks(360);

        List<SnapshotEntity> entities = PublishEntities(room);
        SnapshotEntity leftEntity = entities.Single(entity => entity.EntityId == left);
        SnapshotEntity rightEntity = entities.Single(entity => entity.EntityId == right);
        SnapshotEntity detacherEntity = entities.Single(entity => entity.EntityId == detacher);
        SnapshotEntity sandbagEntity = entities.Single(entity => entity.EntityId == sandbag);
        int splitBodies = new[] { leftEntity.PhysicsBodyId, rightEntity.PhysicsBodyId, detacherEntity.PhysicsBodyId }.Distinct().Count();

        return new SplitScript(entities, splitBodies, leftEntity.Position.Y, sandbagEntity.Position.Y, room.ComputeStateHash());
    }

    [Fact]
    public void BalloonTieAndSandbagTieKeepTheRigPlanar()
    {
        using GameRoom room = PlayHost.CreateSandboxRoom();
        uint player = PlayHost.NextPlayerId();
        uint sequence = 0;

        uint frame = Place(room, ref sequence, player, PartWoodenBlock, 0f, 5f);
        uint sandbag = Place(room, ref sequence, player, PartSandbag, 0f, 4f);
        uint balloon = Place(room, ref sequence, player, PartBalloon, 0f, 6f);
        Assert.True(room.Submit(PlayHost.BindPlayer(new StartSimulationCommand(0, ++sequence, player), player)).IsAccepted);

        // Long enough for the ropes to tauten, the pair to fall, land and swing.
        room.RunTicks(400);

        List<SnapshotEntity> entities = PublishEntities(room);
        foreach (SnapshotEntity entity in entities)
        {
            AssertPlanar(entity, "rope swing");
        }

        // The ties are real joints to the frame's own body, not welds: the sandbag and the
        // balloon keep separate bodies and hang off the rig.
        SnapshotEntity frameEntity = entities.Single(entity => entity.EntityId == frame);
        SnapshotEntity sandbagEntity = entities.Single(entity => entity.EntityId == sandbag);
        SnapshotEntity balloonEntity = entities.Single(entity => entity.EntityId == balloon);
        Assert.NotEqual(frameEntity.PhysicsBodyId, sandbagEntity.PhysicsBodyId);
        Assert.NotEqual(frameEntity.PhysicsBodyId, balloonEntity.PhysicsBodyId);
        Assert.True(frameEntity.Position.Y - sandbagEntity.Position.Y > 0.3f, "the sandbag hangs below the frame");
    }

    private sealed record SplitScript(
        IReadOnlyList<SnapshotEntity> Entities,
        int SplitBodies,
        float BlockY,
        float SandbagY,
        long Hash);

    private static uint Place(GameRoom room, ref uint sequence, uint player, uint partTypeId, float x, float y)
    {
        CommandOutcome outcome = room.Submit(PlayHost.BindPlayer(
            new PlacePartCommand(0, ++sequence, player, partTypeId, x, y, 0f, 1f),
            player));
        Assert.True(outcome.IsAccepted, $"place {partTypeId} at ({x},{y}): {outcome.Status}/{outcome.Error}");
        return outcome.EntityId;
    }

    private static List<SnapshotEntity> PublishEntities(GameRoom room)
    {
        byte[] buffer = new byte[SnapshotFrame.GetMaxByteCount(64)];
        Assert.True(room.TryPublishSnapshot(buffer, out int bytesWritten));
        Assert.True(SnapshotFrame.TryDecodeHeader(buffer.AsSpan(0, bytesWritten), out _, out SnapshotFrameReader reader));
        List<SnapshotEntity> entities = new();
        while (reader.TryReadEntity(out SnapshotEntity entity))
        {
            entities.Add(entity);
        }

        return entities;
    }
}
