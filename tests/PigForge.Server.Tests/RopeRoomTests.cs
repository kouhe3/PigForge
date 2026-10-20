using PigForge.Core;
using PigForge.Core.Construction;
using PigForge.Physics.Abstractions;
using PigForge.Protocol;
using PigForge.Server;

namespace PigForge.Server.Tests;

/// <summary>
/// The rope in a real room (`docs/specs/rope.md`): one placed part materializes eight segment
/// bodies, the host keeps no body, the end segments hinge to the neighbouring frame, the trigger
/// button cuts the middle, and a retry rebuilds the chain.
/// </summary>
public sealed class RopeRoomTests
{
    private const uint PartFrame = 1;
    private const uint PartRope = 41;

    [Fact]
    public void ARopeMaterializesEightSegmentsAndHidesItsHost()
    {
        using GameRoom room = PlayHost.CreateTerrainRoom();
        uint frame = Seed(room, PartFrame, 0f, -2.5f);
        uint rope = Seed(room, PartRope, 1f, -2.5f);

        room.Start();

        Assert.Equal(1, room.RopeCount);
        Assert.Equal(8, room.SubEntityCount);
        List<SnapshotEntity> entities = PublishEntities(room);
        // The host has no body but is still published (`bodyId 0`) for its trigger button; its
        // segments carry the sub-entity flag and the host's part type.
        SnapshotEntity host = entities.Single(entity => entity.EntityId == rope);
        Assert.Equal(0u, host.PhysicsBodyId);
        List<SnapshotEntity> segments = entities
            .Where(entity => entity.PartTypeId == PartRope && (entity.Flags & SnapshotFrame.FlagSubEntity) != 0)
            .ToList();
        Assert.Equal(8, segments.Count);
        Assert.All(segments, segment => Assert.Equal(SnapshotFrame.FlagSubEntity, segment.Flags & SnapshotFrame.FlagSubEntity));
        Assert.Contains(entities, entity => entity.EntityId == frame);
    }

    [Fact]
    public void TheEndpointsHoldTheChainToTheFrame()
    {
        using GameRoom room = PlayHost.CreateTerrainRoom();
        uint frame = Seed(room, PartFrame, 0f, -2.5f);
        Seed(room, PartRope, 1f, -2.5f);
        room.Start();
        for (int tick = 0; tick < 120; tick++)
        {
            room.Tick();
        }

        List<SnapshotEntity> entities = PublishEntities(room);
        SnapshotEntity frameEntity = entities.Single(entity => entity.EntityId == frame);
        SnapshotEntity first = entities
            .Where(entity => entity.PartTypeId == PartRope && (entity.Flags & SnapshotFrame.FlagSubEntity) != 0)
            .OrderBy(entity => entity.EntityId)
            .First();
        float dx = first.Position.X - frameEntity.Position.X;
        float dy = first.Position.Y - frameEntity.Position.Y;
        float distance = MathF.Sqrt((dx * dx) + (dy * dy));

        // The left endpoint hinges at the rope's half-cell (0.5 m from the frame's origin); the
        // revolute keeps it there through the hang, so a metre is a generous bound.
        Assert.InRange(distance, 0.2f, 1f);
    }

    [Fact]
    public void TheButtonCutsTheMiddle()
    {
        using GameRoom room = PlayHost.CreateTerrainRoom();
        Seed(room, PartFrame, 0f, -2.5f);
        uint rope = Seed(room, PartRope, 1f, -2.5f);
        room.Start();
        room.Tick();

        CommandOutcome outcome = room.Submit(PlayHost.BindPlayer(
            new SetPartActiveCommand(0, 1, 0, rope, true),
            0));
        Assert.True(outcome.IsAccepted, $"{outcome.Status}/{outcome.Error}");
        room.Tick();

        // `Cut(n/2)` with eight nodes is node 4 (`Rope.cs:169-190`).
        Assert.Equal(4, room.RopeCutLink(new EntityId(rope)));
    }

    [Fact]
    public void TwoRopesChainTheirEndSegments()
    {
        using GameRoom room = PlayHost.CreateTerrainRoom();
        Seed(room, PartFrame, 0f, -2.5f);
        uint first = Seed(room, PartRope, 1f, -2.5f);
        Seed(room, PartRope, 2f, -2.5f);
        Seed(room, PartFrame, 3f, -2.5f);
        room.Start();
        for (int tick = 0; tick < 60; tick++)
        {
            room.Tick();
        }

        Assert.Equal(2, room.RopeCount);
        Assert.Equal(16, room.SubEntityCount);
        Assert.True(room.HasRopeChainJoint(new EntityId(first)), "the first rope's right end is not hinged to the second");
        List<SnapshotEntity> segments = PublishEntities(room)
            .Where(entity => entity.PartTypeId == PartRope && (entity.Flags & SnapshotFrame.FlagSubEntity) != 0)
            .OrderBy(entity => entity.EntityId)
            .ToList();
        Assert.Equal(16, segments.Count);
        float dx = segments[8].Position.X - segments[7].Position.X;
        float dy = segments[8].Position.Y - segments[7].Position.Y;
        float gap = MathF.Sqrt((dx * dx) + (dy * dy));
        Assert.True(gap < 0.1f, $"gap {gap}: {string.Join(";", segments.Select(segment => $"{segment.EntityId}@{segment.Position.X:0.00},{segment.Position.Y:0.00}"))}");
    }

    [Fact]
    public void AClickCutsTheRopeAtTheNearestSegment()
    {
        using GameRoom room = PlayHost.CreateTerrainRoom();
        Seed(room, PartFrame, 0f, -2.5f);
        uint rope = Seed(room, PartRope, 1f, -2.5f);
        room.Start();
        room.Tick();
        EntityId host = new(rope);

        // Nothing within the original's 0.5 m radius: rejected, not "cut the middle".
        CommandOutcome miss = room.Submit(PlayHost.BindPlayer(new CutRopeCommand(0, 1, 0, 20f, 20f), 0));
        Assert.Equal(CommandStatus.RuleRejected, miss.Status);
        Assert.Equal(ConstructionError.RopeNotAtPoint, miss.Error);

        List<SnapshotEntity> segments = PublishEntities(room)
            .Where(entity => entity.PartTypeId == PartRope && (entity.Flags & SnapshotFrame.FlagSubEntity) != 0)
            .OrderBy(entity => entity.EntityId)
            .ToList();
        SnapshotEntity target = segments[3];
        CommandOutcome hit = room.Submit(PlayHost.BindPlayer(new CutRopeCommand(0, 2, 0, target.Position.X, target.Position.Y), 0));
        Assert.True(hit.IsAccepted, $"{hit.Status}/{hit.Error}");
        Assert.Equal(3, room.RopeCutLink(host));

        // A second click is accepted and leaves the first cut alone (`Rope.OnTouch` no-ops).
        SnapshotEntity later = segments[6];
        CommandOutcome again = room.Submit(PlayHost.BindPlayer(new CutRopeCommand(0, 3, 0, later.Position.X, later.Position.Y), 0));
        Assert.True(again.IsAccepted, $"{again.Status}/{again.Error}");
        Assert.Equal(3, room.RopeCutLink(host));
    }

    [Fact]
    public void AnIntactRopeConnectsItsTwoSidesAndACutRopeDoesNot()
    {
        using GameRoom room = PlayHost.CreateTerrainRoom();
        uint left = Seed(room, PartFrame, 0f, -2.5f);
        uint rope = Seed(room, PartRope, 1f, -2.5f);
        uint right = Seed(room, PartFrame, 2f, -2.5f);
        room.Start();
        room.Tick();

        Assert.True(room.AreConnected(new EntityId(left), new EntityId(right)));

        List<SnapshotEntity> segments = PublishEntities(room)
            .Where(entity => entity.PartTypeId == PartRope && (entity.Flags & SnapshotFrame.FlagSubEntity) != 0)
            .OrderBy(entity => entity.EntityId)
            .ToList();
        SnapshotEntity middle = segments[4];
        CommandOutcome cut = room.Submit(PlayHost.BindPlayer(
            new CutRopeCommand(0, 1, 0, middle.Position.X, middle.Position.Y),
            0));
        Assert.True(cut.IsAccepted, $"{cut.Status}/{cut.Error}");

        Assert.False(room.AreConnected(new EntityId(left), new EntityId(right)));
    }

    [Fact]
    public void ARetryDropsTheSegmentsAndAStartRebuildsThem()
    {
        using GameRoom room = PlayHost.CreateTerrainRoom();
        Seed(room, PartFrame, 0f, -2.5f);
        Seed(room, PartRope, 1f, -2.5f);
        room.Start();
        List<uint> before = PublishEntities(room)
            .Where(entity => entity.PartTypeId == PartRope && (entity.Flags & SnapshotFrame.FlagSubEntity) != 0)
            .Select(entity => entity.EntityId)
            .OrderBy(value => value)
            .ToList();
        Assert.Equal(8, before.Count);

        room.Retry();
        Assert.Equal(0, room.SubEntityCount);
        Assert.Equal(0, room.RopeCount);

        room.Start();
        List<uint> after = PublishEntities(room)
            .Where(entity => entity.PartTypeId == PartRope && (entity.Flags & SnapshotFrame.FlagSubEntity) != 0)
            .Select(entity => entity.EntityId)
            .OrderBy(value => value)
            .ToList();
        Assert.Equal(8, after.Count);
        Assert.NotEqual(before, after);
    }

    private static uint Seed(GameRoom room, uint partTypeId, float x, float y)
    {
        ConstructionResult placed = room.SeedPart(partTypeId, x, y, 0f, 1f, mirrored: false);
        Assert.True(placed.IsSuccess, $"seed {partTypeId} at ({x},{y}): {placed.Error}");
        return placed.Entity.Value;
    }

    private static List<SnapshotEntity> PublishEntities(GameRoom room)
    {
        byte[] buffer = new byte[SnapshotFrame.GetMaxByteCount(128)];
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
