using PigForge.Core;
using PigForge.Core.Content;
using PigForge.Physics.Abstractions;
using PigForge.Physics.Bepu;
using PigForge.Protocol;
using PigForge.Server;

namespace PigForge.Server.Tests;

/// <summary>
/// A level's own solid furniture (schemaVersion 7 props' <c>boxes</c>, `docs/specs/level-props.md`
/// §3.6): the original's crates, walls, planks and doors are what a vehicle drives into, so the room
/// turns each box into a static body -- with no entity, like the terrain, so it never enters a
/// snapshot, belongs to no player and survives RESET.
/// <para>
/// The rig is a level with one prop at the origin carrying one 20 x 1 x 1 box, so its top plane
/// (y = 0.5) is the surface a pig lands on. The control is the identical level with the box removed,
/// where the same pig must fall straight through.
/// </para>
/// </summary>
public sealed class PropSolidRoomTests
{
    private const uint PartPig = 4;
    private const uint PartPlayer = 1;
    private const uint SequencePlace = 1;
    private const uint SequenceStart = 2;

    private static readonly PhysicsVector3 Drop = new(0f, 3f, 0f);

    private static readonly GameplayZone FarBounds = new(
        new PhysicsVector3(-1000f, -1000f, -1000f),
        new PhysicsVector3(1000f, 1000f, 1000f));

    /// <summary>One crate-sized prop at the origin: a 20 x 1 x 1 box whose top plane is y = 0.5.</summary>
    private static readonly LevelPropDefinition Crate = new("TNT_Box", X: 0f, Y: 0f, Z: 0f, Rotation: 0f, ScaleX: 1f, ScaleY: 1f)
    {
        Boxes = [new LevelPropBoxDefinition(X: 0f, Y: 0f, Z: 0f, SizeX: 20f, SizeY: 1f, SizeZ: 1f, Rotation: 0f)],
    };

    [Fact]
    public void APigRestsOnAPropsSolidBox()
    {
        float withBox = DropPigAt(Drop, Crate);
        float withoutBox = DropPigAt(Drop, prop: null);

        // The box is 1 m tall around y = 0, so the pig settles on its top plane (measured: its own
        // body origin rests about 0.46 m above the surface it stands on).
        Assert.InRange(withBox, 0.7f, 1.3f);

        // Without the box the same level lets it fall out of the world: the box is what held it.
        Assert.True(withoutBox < -20f, $"without the box the pig must fall: y = {withoutBox}");
    }

    [Fact]
    public void PropBodiesAreBuiltOncePerRoom()
    {
        using GameRoom room = CreateRoom(Crate, out LevelContentDocument level);
        Assert.Equal(1, room.PropBodyCount);

        // A return to the building phase re-runs SetupFromLevel on the same document; a level's own
        // furniture does not change, so neither route may add a second copy of it.
        room.SetupFromLevel(level);
        room.SetupFromLevel(level);

        Assert.Equal(1, room.PropBodyCount);
    }

    [Fact]
    public void ALevelWithoutSolidBoxesBuildsNoPropBodies()
    {
        using GameRoom room = CreateRoom(prop: null, out _);
        Assert.Equal(0, room.PropBodyCount);
    }

    [Fact]
    public void ABoxFollowsItsPropsOwnPose()
    {
        // The prop sits at (5, 10) turned a quarter turn and its box is offset 2 m along the prop's own
        // x, which the rotation sends to the box's centre (5, 12); the box's own angle turns with it, so
        // the 3 m side is the vertical one and its top plane is y = 13.5. A pig above that rests on it;
        // a pig above the prop's own origin falls through, because nothing is there.
        LevelPropDefinition offset = new("TNT_Box", X: 5f, Y: 10f, Z: 0f, Rotation: MathF.PI / 2f, ScaleX: 1f, ScaleY: 1f)
        {
            Boxes = [new LevelPropBoxDefinition(X: 2f, Y: 0f, Z: 0f, SizeX: 3f, SizeY: 1f, SizeZ: 1f, Rotation: 0f)],
        };

        Assert.InRange(DropPigAt(new PhysicsVector3(5f, 12f, 0f), offset), 13.6f, 14.3f);
        float besideIt = DropPigAt(new PhysicsVector3(5f, 10f, 0f), offset);
        Assert.True(besideIt < 0f, $"nothing holds a pig at the prop's own origin: y = {besideIt}");

        // A mirrored instance puts the same box on the prop's other side, top plane y = 9.5.
        Assert.InRange(DropPigAt(new PhysicsVector3(5f, 8f, 0f), offset with { ScaleX = -1f }), 9.6f, 10.3f);
    }

    /// <summary>Places the pig 3 m above the given point, starts, and returns its height after 3 s.</summary>
    private static float DropPigAt(PhysicsVector3 drop, LevelPropDefinition? prop)
    {
        using GameRoom room = CreateRoom(prop, out _);
        ReplayCommand place = PlayHost.BindPlayer(
            new PlacePartCommand(0, SequencePlace, 0, PartPig, drop.X, drop.Y + 3f, Angle: 0f, Scale: 1f),
            PartPlayer);
        CommandOutcome placed = room.Submit(place);
        Assert.True(placed.IsAccepted, $"place: {placed.Status}/{placed.Error}");
        Assert.True(room.Submit(PlayHost.BindPlayer(new StartSimulationCommand(0, SequenceStart, 0), PartPlayer)).IsAccepted);

        float height = drop.Y + 3f;
        for (int tick = 0; tick < 180; tick += 30)
        {
            room.RunTicks(30);
            SnapshotEntity pig = Publish(room).Single(entity => entity.EntityId == placed.EntityId);
            height = pig.Position.Y;
        }

        return height;
    }

    private static GameRoom CreateRoom(LevelPropDefinition? prop, out LevelContentDocument level)
    {
        string root = FindRepositoryRoot();
        PartContentLibrary parts = PartContentLibrary.Load(Path.Combine(root, "content", "parts.json"));

        level = new(
            prop is null ? "prop-solid-room-test-v6" : "prop-solid-room-test-v7",
            GoalZone: new GameplayZone(new PhysicsVector3(500f, 500f, 500f), new PhysicsVector3(501f, 501f, 501f)),
            MapBounds: FarBounds,
            Spawns: [])
        {
            Props = prop is null ? [] : [prop],
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
