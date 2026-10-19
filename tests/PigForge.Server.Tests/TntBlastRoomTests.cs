using PigForge.Core;
using PigForge.Core.Content;
using PigForge.Physics.Abstractions;
using PigForge.Physics.Bepu;
using PigForge.Protocol;
using PigForge.Server;

namespace PigForge.Server.Tests;

/// <summary>
/// The charge's own blast in a real room (`docs/specs/tnt-blast.md`): content carries the original
/// component's numbers, so a charge reaches the radius its prefab declares rather than the room's
/// fallback. The room below is configured with the old 4 m default on purpose -- the chain that makes
/// this test pass can only come from the component's own 8 m (`TNT.cs:129,152-156`).
/// </summary>
public sealed class TntBlastRoomTests
{
    private const uint PartTnt = 9;
    private const uint PartPlayer = 1;

    private static readonly GameplayZone FarGoal = new(
        new PhysicsVector3(-100f, -500f, -2f),
        new PhysicsVector3(100f, -499f, 2f));

    private static readonly GameplayZone FarBounds = new(
        new PhysicsVector3(-1000f, -1000f, -1000f),
        new PhysicsVector3(1000f, 1000f, 1000f));

    [Fact]
    public void AContentChargeChainsOneSixMetresAway()
    {
        using GameRoom room = StartRoom();

        // Six metres apart: inside the component's 8 m, outside the room's own 4 m default.
        uint source = Place(room, sequence: 1, x: 0f);
        uint target = Place(room, sequence: 2, x: 6f);
        Assert.True(room.Submit(PlayHost.BindPlayer(new StartSimulationCommand(0, 3, PartPlayer), PartPlayer)).IsAccepted);

        // The switch is the lighter; the fuse then burns and the blast chains the neighbour.
        Assert.True(room.Submit(PlayHost.BindPlayer(new SetPartActiveCommand(0, 4, PartPlayer, source, true), PartPlayer)).IsAccepted);
        room.RunTicks(40);

        Assert.Null(Find(room, source));
        Assert.Null(Find(room, target));
    }

    private static GameRoom StartRoom()
    {
        string root = FindRepositoryRoot();
        PartContentLibrary parts = PartContentLibrary.Load(Path.Combine(root, "content", "parts.json"));
        LevelContentDocument level = new("tnt-blast-room-test-v11", FarGoal, FarBounds, Spawns: []);
        GameplayConfig config = new(
            level.GoalZone,
            level.MapBounds,
            TntBlastRadius: 4f,
            TntBlastImpulse: 25f,
            TntIgniteImpactSpeed: 5f,
            MaxTicks: 0,
            ObjectivesEnabled: false);
        GameRoom room = new(GameRoomOptions.Create(
            parts,
            () => new BepuPhysicsWorld(new PhysicsVector3(0f, -9.81f, 0f)),
            config,
            sandboxMode: false));
        room.SetupFromLevel(level);
        return room;
    }

    private static uint Place(GameRoom room, uint sequence, float x)
    {
        CommandOutcome outcome = room.Submit(PlayHost.BindPlayer(
            new PlacePartCommand(0, sequence, PartPlayer, PartTnt, x, 4f, Angle: 0f, Scale: 1f),
            PartPlayer));
        Assert.True(outcome.IsAccepted, $"place the charge at ({x},4): {outcome.Status}/{outcome.Error}");
        return outcome.EntityId;
    }

    private static SnapshotEntity? Find(GameRoom room, uint entityId)
    {
        byte[] buffer = new byte[SnapshotFrame.GetMaxByteCount(64)];
        Assert.True(room.TryPublishSnapshot(buffer, out int written));
        Assert.True(SnapshotFrame.TryDecodeHeader(buffer.AsSpan(0, written), out _, out SnapshotFrameReader reader));
        while (reader.TryReadEntity(out SnapshotEntity entity))
        {
            if (entity.EntityId == entityId)
            {
                return entity;
            }
        }

        return null;
    }

    private static string FindRepositoryRoot()
    {
        string directory = AppContext.BaseDirectory;
        while (!File.Exists(Path.Combine(directory, "content", "parts.json")))
        {
            directory = Path.GetDirectoryName(directory) ?? throw new InvalidOperationException("the repository root is above this binary");
        }

        return directory;
    }
}
