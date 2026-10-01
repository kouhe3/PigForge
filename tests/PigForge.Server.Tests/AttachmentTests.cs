using PigForge.Core;
using PigForge.Core.Content;
using PigForge.Protocol;
using PigForge.Physics.Abstractions;
using PigForge.Server;

namespace PigForge.Server.Tests;

/// <summary>
/// Runtime attachments on the real content and the Bepu backend: a balloon string and a
/// sandbag tie are built at start of simulation by searching the build grid for the first
/// chassis (or pig) and tying to it with a rope joint (Sandbag.cs:136-164, Balloon.cs:143-166).
/// Without one the part is free — its design-time joint capability is `none`, so the assembler
/// no longer welds it to whatever it happens to touch.
/// </summary>
public sealed class AttachmentTests
{
    private const uint PartFrame = 1;    // wooden-block, jointConnectionType source
    private const uint PartBalloon = 10; // balloon, attachment direction down
    private const uint PartSandbag = 21; // sandbag, attachment direction up (maxDistance 0.5)

    /// <summary>terrain-v1 puts the ground slabs' top at y = -3, so a free sandbag settles
    /// around y = -2.87. Any part still well above that is being held up.</summary>
    private const float GroundTopY = -2.87f;

    [Fact]
    public void SandbagBindsToTheFrameAboveAndHangsFromIt()
    {
        using GameRoom room = PlayHost.CreateSandboxRoom();
        uint player = PlayHost.NextPlayerId();
        uint sequence = 0;

        uint frame = Place(room, ref sequence, player, PartFrame, 0f, 2f);
        uint sandbag = Place(room, ref sequence, player, PartSandbag, 0f, 0f);
        Assert.True(room.Submit(PlayHost.BindPlayer(new StartSimulationCommand(0, ++sequence, player), player)).IsAccepted);

        for (int tick = 0; tick < 180; tick++)
        {
            room.Tick();
        }

        List<SnapshotEntity> settled = PublishEntities(room);
        SnapshotEntity frameEntity = settled.Single(entity => entity.EntityId == frame);
        SnapshotEntity sandbagEntity = settled.Single(entity => entity.EntityId == sandbag);

        // The rope is a runtime joint, not a weld: the sandbag keeps its own body (the
        // assembler no longer welds everything adjacent).
        Assert.NotEqual(0u, sandbagEntity.PhysicsBodyId);
        Assert.NotEqual(frameEntity.PhysicsBodyId, sandbagEntity.PhysicsBodyId);

        // The rope pulls the pair to at most maxDistance (0.5) plus the spring's sag, instead of
        // letting the sandbag fall away: the two start 2 apart and stay well under 1.2 apart.
        Assert.True(
            frameEntity.Position.Y - sandbagEntity.Position.Y < 1.2f,
            $"the sandbag must hang from the frame: frame y={frameEntity.Position.Y}, sandbag y={sandbagEntity.Position.Y}");
        Assert.True(
            frameEntity.Position.Y > sandbagEntity.Position.Y,
            $"the sandbag hangs below its frame: frame y={frameEntity.Position.Y}, sandbag y={sandbagEntity.Position.Y}");
    }

    [Fact]
    public void BalloonBindsDownwardAndLiftsTheChassis()
    {
        using GameRoom room = PlayHost.CreateSandboxRoom();
        uint player = PlayHost.NextPlayerId();
        uint sequence = 0;

        uint frame = Place(room, ref sequence, player, PartFrame, 0f, 0f);
        uint balloon = Place(room, ref sequence, player, PartBalloon, 0f, 1f);
        List<SnapshotEntity> before = PublishEntities(room);
        float buildY = before.Single(entity => entity.EntityId == frame).Position.Y;
        Assert.True(room.Submit(PlayHost.BindPlayer(new StartSimulationCommand(0, ++sequence, player), player)).IsAccepted);

        // The rope starts slack (both anchors coincide), so the balloon has to take it up before
        // it can pull the frame: a handful of ticks, while staying well inside the level bounds.
        for (int tick = 0; tick < 15; tick++)
        {
            room.Tick();
        }

        List<SnapshotEntity> lifted = PublishEntities(room);
        SnapshotEntity frameEntity = lifted.Single(entity => entity.EntityId == frame);
        SnapshotEntity balloonEntity = lifted.Single(entity => entity.EntityId == balloon);

        Assert.NotEqual(frameEntity.PhysicsBodyId, balloonEntity.PhysicsBodyId);
        // Balloon lift is a pure vertical impulse on the balloon's own body; the rope carries it
        // into the frame, which is the whole point of binding instead of welding.
        Assert.True(
            frameEntity.Position.Y > buildY + 0.05f,
            $"the balloon must lift the frame it is tied to: {buildY} -> {frameEntity.Position.Y}");
    }

    [Fact]
    public void SandbagWithNoAnchorInRangeStaysFree()
    {
        using GameRoom room = PlayHost.CreateSandboxRoom();
        uint player = PlayHost.NextPlayerId();
        uint sequence = 0;

        // 12 cells up: outside the 10-cell search radius of the original
        // (INSettingsBExp.json SandbagConnectionDistance).
        uint frame = Place(room, ref sequence, player, PartFrame, 0f, 12f);
        uint sandbag = Place(room, ref sequence, player, PartSandbag, 0f, 0f);
        Assert.True(room.Submit(PlayHost.BindPlayer(new StartSimulationCommand(0, ++sequence, player), player)).IsAccepted);

        for (int tick = 0; tick < 5; tick++)
        {
            room.Tick();
        }

        List<SnapshotEntity> entities = PublishEntities(room);
        float separation = entities.Single(entity => entity.EntityId == frame).Position.Y
            - entities.Single(entity => entity.EntityId == sandbag).Position.Y;

        // No rope: both parts free-fall, so the separation is untouched.
        Assert.True(separation > 10f, $"an out-of-range frame must not bind: separation {separation}");

        for (int tick = 0; tick < 175; tick++)
        {
            room.Tick();
        }

        List<SnapshotEntity> settled = PublishEntities(room);
        float freeY = settled.Single(entity => entity.EntityId == sandbag).Position.Y;

        // A free sandbag is cargo: it falls all the way to the terrain.
        Assert.True(freeY < GroundTopY + 0.2f, $"an unbound sandbag must fall: y={freeY}");
    }

    [Fact]
    public void AttachmentBindingIsDeterministicAcrossRuns()
    {
        Assert.Equal(RunAttachmentScript(), RunAttachmentScript());
    }

    private static long RunAttachmentScript()
    {
        using GameRoom room = PlayHost.CreateSandboxRoom();
        uint player = PlayHost.NextPlayerId();
        uint sequence = 0;

        uint frame = Place(room, ref sequence, player, PartFrame, 0f, 2f);
        uint sandbag = Place(room, ref sequence, player, PartSandbag, 0f, 0f);
        uint balloon = Place(room, ref sequence, player, PartBalloon, 0f, 3f);
        Assert.True(room.Submit(PlayHost.BindPlayer(new StartSimulationCommand(0, ++sequence, player), player)).IsAccepted);

        for (int tick = 0; tick < 60; tick++)
        {
            room.Tick();
        }

        long hash = 17;
        foreach (SnapshotEntity entity in PublishEntities(room).OrderBy(entity => entity.EntityId))
        {
            if (entity.EntityId != frame && entity.EntityId != sandbag && entity.EntityId != balloon)
            {
                continue;
            }

            hash = unchecked((hash * 31) + entity.EntityId);
            hash = unchecked((hash * 31) + entity.PhysicsBodyId);
            hash = unchecked((hash * 31) + entity.Position.X.GetHashCode());
            hash = unchecked((hash * 31) + entity.Position.Y.GetHashCode());
        }

        return hash;
    }

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
