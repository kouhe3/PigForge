using PigForge.Core;
using PigForge.Core.Content;
using PigForge.Physics.Abstractions;
using PigForge.Physics.Bepu;
using PigForge.Protocol;
using PigForge.Server;

namespace PigForge.Server.Tests;

/// <summary>
/// A level room's return to its building state keeps the player's layout *and its entity ids*: the
/// room re-enters Building when the pig leaves the level's camera rectangle (`ADR-036`) and whenever
/// the player asks for it, and in both cases the parts that survived the run stay the same
/// construction entities in the same cells. That is the sandbox's own rule (`ResetPlayer`, ADR-026):
/// ownership never rides the wire -- a player learns its ids from its own Place acks -- so a layout
/// rebuilt under fresh ids would leave the page holding ids the room no longer knows, and the run
/// that follows could not be built on. Only what the run destroyed is placed again, with the fresh id
/// a placement always gets.
/// </summary>
public sealed class LevelRetryRoomTests
{
    private const uint PartFrame = 1;
    private const uint PartFan = 11;
    private const uint PartEngine = 8;
    private const uint PartTnt = 9;
    private const uint PartPlayer = 1;

    private static readonly GameplayZone FarBounds = new(
        new PhysicsVector3(-1000f, -1000f, -1000f),
        new PhysicsVector3(1000f, 1000f, 1000f));

    /// <summary>A goal zone far below the rigs: a fall never wins a run inside a test window.</summary>
    private static readonly GameplayZone FarGoal = new(
        new PhysicsVector3(-100f, -500f, -2f),
        new PhysicsVector3(100f, -499f, 2f));

    [Fact]
    public void RetryingKeepsThePlacedPartsEntityIds()
    {
        using GameRoom room = StartLevelRoom();
        uint sequence = 0;
        uint block = Place(room, ref sequence, PartFrame, 0f, 20f);
        uint fan = Place(room, ref sequence, PartFan, 0f, 21f);
        Assert.True(room.Submit(Start(++sequence)).IsAccepted);
        room.RunTicks(30);

        room.Retry();

        Assert.Equal(RoomMode.Building, room.Mode);
        List<SnapshotEntity> layout = PublishEntities(room);
        SnapshotEntity blockPreview = layout.Single(entity => entity.EntityId == block);
        Assert.Equal(0u, blockPreview.PhysicsBodyId);
        Assert.Equal(0f, blockPreview.Position.X);
        Assert.Equal(20f, blockPreview.Position.Y);
        Assert.Equal(fan, layout.Single(entity => entity.EntityId == fan).EntityId);
    }

    [Fact]
    public void RetryingRebuildsOnlyWhatTheRunDestroyed()
    {
        using GameRoom room = StartLevelRoom();
        uint sequence = 0;
        uint block = Place(room, ref sequence, PartFrame, 0f, 20f);
        uint tnt = Place(room, ref sequence, PartTnt, 1f, 20f);
        Assert.True(room.Submit(Start(++sequence)).IsAccepted);

        // The TNT's own button burns its fuse (5 ticks) and the charge leaves the world with its blast.
        Assert.True(room.Submit(SetActive(++sequence, tnt, true)).IsAccepted);
        room.RunTicks(60);
        Assert.DoesNotContain(PublishEntities(room), entity => entity.EntityId == tnt);

        room.Retry();

        List<SnapshotEntity> layout = PublishEntities(room);
        // The survivor is the same construction entity: same id, same cell, back as a preview.
        SnapshotEntity blockPreview = layout.Single(entity => entity.EntityId == block);
        Assert.Equal(0u, blockPreview.PhysicsBodyId);
        // The destroyed part is back at its captured build pose under the fresh id a placement gets.
        SnapshotEntity rebuilt = layout.Single(entity => entity.PartTypeId == PartTnt);
        Assert.NotEqual(tnt, rebuilt.EntityId);
        Assert.Equal(1f, rebuilt.Position.X);
        Assert.Equal(20f, rebuilt.Position.Y);
    }

    [Fact]
    public void ARetriedLayoutRunsAgainAndItsSwitchesComeBackOff()
    {
        using GameRoom room = StartLevelRoom();
        uint sequence = 0;
        uint block = Place(room, ref sequence, PartFrame, 0f, 20f);
        Place(room, ref sequence, PartEngine, 0f, 20f); // enclosed in the frame, so it supplies power
        uint fan = Place(room, ref sequence, PartFan, 0f, 21f);
        Assert.True(room.Submit(Start(++sequence)).IsAccepted);
        Assert.True(room.Submit(SetActive(++sequence, fan, true)).IsAccepted);
        room.RunTicks(30);

        room.Retry();
        Assert.True(room.Submit(Start(++sequence)).IsAccepted);
        room.RunTicks(10);

        List<SnapshotEntity> running = PublishEntities(room);
        SnapshotEntity blockBody = running.Single(entity => entity.EntityId == block);
        Assert.NotEqual(0u, blockBody.PhysicsBodyId);
        Assert.Equal(fan, running.Single(entity => entity.PartTypeId == PartFan).EntityId);

        // The roles were re-registered over the restored layout, so the switch starts off again and
        // answers again on the second run (the same registration a fresh placement gets).
        Assert.Equal(0, running.Single(entity => entity.EntityId == fan).Flags);
        Assert.True(room.Submit(SetActive(++sequence, fan, true)).IsAccepted);
        Assert.Equal((byte)1, PublishEntities(room).Single(entity => entity.EntityId == fan).Flags);
    }

    private static GameRoom StartLevelRoom()
    {
        string root = FindRepositoryRoot();
        PartContentLibrary parts = PartContentLibrary.Load(Path.Combine(root, "content", "parts.json"));
        LevelContentDocument level = new(
            "level-retry-room-test-v10",
            GoalZone: FarGoal,
            MapBounds: FarBounds,
            Spawns: []);
        GameplayConfig config = new(
            level.GoalZone,
            level.MapBounds,
            TntBlastRadius: 4f,
            TntBlastImpulse: 25f,
            TntIgniteImpactSpeed: 5f,
            MaxTicks: 0,
            ObjectivesEnabled: true,
            ChallengeTriggers: LevelChallengeTriggers.Build(level));
        GameRoom room = new(GameRoomOptions.Create(
            parts,
            () => new BepuPhysicsWorld(new PhysicsVector3(0f, -9.81f, 0f)),
            config,
            sandboxMode: false));
        room.SetupFromLevel(level);
        return room;
    }

    private static uint Place(GameRoom room, ref uint sequence, uint partTypeId, float x, float y)
    {
        CommandOutcome outcome = room.Submit(PlayHost.BindPlayer(
            new PlacePartCommand(0, ++sequence, PartPlayer, partTypeId, x, y, Angle: 0f, Scale: 1f),
            PartPlayer));
        Assert.True(outcome.IsAccepted, $"place {partTypeId} at ({x},{y}): {outcome.Status}/{outcome.Error}");
        return outcome.EntityId;
    }

    private static StartSimulationCommand Start(uint sequence) => new(0, sequence, PartPlayer);

    private static SetPartActiveCommand SetActive(uint sequence, uint entityId, bool active) =>
        new(0, sequence, PartPlayer, entityId, active);

    private static List<SnapshotEntity> PublishEntities(GameRoom room)
    {
        List<SnapshotEntity> entities = new();
        byte[] buffer = new byte[SnapshotFrame.GetMaxByteCount(64)];
        Assert.True(room.TryPublishSnapshot(buffer, out int written));
        Assert.True(SnapshotFrame.TryDecodeHeader(buffer.AsSpan(0, written), out SnapshotFrameHeader header, out SnapshotFrameReader reader));
        for (uint index = 0; index < header.EntityCount; index++)
        {
            Assert.True(reader.TryReadEntity(out SnapshotEntity entity));
            entities.Add(entity);
        }

        return entities;
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "content", "parts.json")))
        {
            directory = directory.Parent;
        }

        if (directory is null)
        {
            throw new InvalidOperationException($"Could not locate content/parts.json above {AppContext.BaseDirectory}.");
        }

        return directory.FullName;
    }
}
