using PigForge.Core;
using PigForge.Core.Content;
using PigForge.Physics.Abstractions;
using PigForge.Physics.Bepu;
using PigForge.Protocol;
using PigForge.Server;

namespace PigForge.Server.Tests;

/// <summary>
/// Levels carry the original's baked terrain as a static triangle mesh (schemaVersion 2 `terrain`,
/// ADR-032). The room has to build one mesh body per terrain object without giving it an entity --
/// terrain is not a part, it belongs to no player, it survives RESET and build-mode returns, and the
/// client learns its geometry from the level document rather than from a snapshot.
/// <para>
/// The rig is a level whose terrain is a 40 x 4 rectangle in its own frame, so the extruded shell is
/// a box: the plane at y = 0 is the ground, spanning x in [-20, 20]. The shipped pig (4) is placed
/// 3 m above it and the room runs 3 s; the control is the identical level with the terrain entry
/// removed, where the same pig must fall straight through.
/// </para>
/// </summary>
public sealed class TerrainRoomTests
{
    private const uint PartPig = 4;
    private const uint PartPlayer = 1;
    private const uint SequencePlace = 1;
    private const uint SequenceStart = 2;

    private static readonly PhysicsVector3 Drop = new(0f, 3f, 0f);

    private static readonly GameplayZone FarBounds = new(
        new PhysicsVector3(-1000f, -1000f, -1000f),
        new PhysicsVector3(1000f, 1000f, 1000f));

    [Fact]
    public void APigRestsOnTheLevelsTriangleMeshTerrain()
    {
        float withTerrain = DropPigAndSampleHeight(withTerrain: true);
        float withoutTerrain = DropPigAndSampleHeight(withTerrain: false);

        // The pig lands on the terrain's top plane (y = 0) and stays there...
        Assert.InRange(withTerrain, 0.2f, 1.2f);

        // ...while the same level without the terrain entry lets it fall out of the world. That is
        // the non-empty half: the mesh body is what held it.
        Assert.True(withoutTerrain < -20f, $"without terrain the pig must fall: y = {withoutTerrain}");
    }

    [Fact]
    public void TerrainBodiesAreBuiltOncePerRoom()
    {
        using GameRoom room = CreateRoom(withTerrain: true, out LevelContentDocument level);
        Assert.Equal(1, room.TerrainBodyCount);

        // Returning to the building phase (EnterBuildMode with Clear, and Retry) re-runs
        // SetupFromLevel on the same document; the terrain of a level does not change, so neither
        // route may add a second copy of it.
        room.SetupFromLevel(level);
        room.SetupFromLevel(level);

        Assert.Equal(1, room.TerrainBodyCount);
    }

    [Fact]
    public void ALevelWithoutTerrainBuildsNoTerrainBodies()
    {
        using GameRoom room = CreateRoom(withTerrain: false, out _);
        Assert.Equal(0, room.TerrainBodyCount);
    }

    /// <summary>Places the pig 3 m up, starts, and returns its height after 3 s.</summary>
    private static float DropPigAndSampleHeight(bool withTerrain)
    {
        using GameRoom room = CreateRoom(withTerrain, out _);
        CommandOutcome placed = room.Submit(Place());
        Assert.True(placed.IsAccepted, $"place: {placed.Status}/{placed.Error}");
        Assert.True(room.Submit(PlayHost.BindPlayer(new StartSimulationCommand(0, SequenceStart, 0), PartPlayer)).IsAccepted);

        float height = Drop.Y;
        for (int tick = 0; tick < 180; tick += 30)
        {
            room.RunTicks(30);
            SnapshotEntity pig = Publish(room).Single(entity => entity.EntityId == placed.EntityId);
            height = pig.Position.Y;
        }

        return height;
    }

    private static ReplayCommand Place() => PlayHost.BindPlayer(
        new PlacePartCommand(0, SequencePlace, 0, PartPig, Drop.X, Drop.Y, Angle: 0f, Scale: 1f),
        PartPlayer);

    private static GameRoom CreateRoom(bool withTerrain, out LevelContentDocument level)
    {
        string root = FindRepositoryRoot();
        PartContentLibrary parts = PartContentLibrary.Load(Path.Combine(root, "content", "parts.json"));

        // A 40 x 4 rectangle in the terrain's own frame: its extruded shell is a box whose top plane
        // (y = 0) is the ground a pig lands on, exactly the shape the original's own outline gives a
        // flat stretch of level.
        LevelTerrainDefinition terrain = new(
            Position: PhysicsVector3.Zero,
            Depth: 10f,
            Loops:
            [
                new[]
                {
                    new PhysicsVector3(-20f, -4f, 0f),
                    new PhysicsVector3(20f, -4f, 0f),
                    new PhysicsVector3(20f, 0f, 0f),
                    new PhysicsVector3(-20f, 0f, 0f),
                },
            ]);

        level = new(
            withTerrain ? "terrain-room-test-v2" : "terrain-room-test-v1",
            GoalZone: new GameplayZone(new PhysicsVector3(500f, 500f, 500f), new PhysicsVector3(501f, 501f, 501f)),
            MapBounds: FarBounds,
            Spawns: [])
        {
            Terrain = withTerrain ? [terrain] : [],
        };

        GameplayConfig config = new(
            level.GoalZone,
            level.MapBounds,
            TntBlastRadius: 4f,
            TntBlastImpulse: 25f,
            TntIgniteImpactSpeed: 5f,
            MaxTicks: 1200,
            ObjectivesEnabled: false);
        GameRoom room = new(GameRoomOptions.Create(
            parts,
            () => new BepuPhysicsWorld(new PhysicsVector3(0f, -9.81f, 0f)),
            config,
            sandboxMode: true));
        room.SetupFromLevel(level);
        return room;
    }

    private static List<SnapshotEntity> Publish(GameRoom room)
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
