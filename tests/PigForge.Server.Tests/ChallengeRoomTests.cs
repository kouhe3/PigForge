using PigForge.Core;
using PigForge.Core.Content;
using PigForge.Physics.Abstractions;
using PigForge.Physics.Bepu;
using PigForge.Protocol;
using PigForge.Server;

namespace PigForge.Server.Tests;

/// <summary>
/// A level's challenge markers (schemaVersion 8 props' <c>triggers</c>,
/// `docs/specs/level-challenges.md`): once the pig reaches a marker's box, the prop is collected for the
/// rest of the run, and the room's <c>Retry</c> clears the marks the way the original's
/// <c>Collectable.OnReset</c> puts a box back.
/// <para>
/// The rig is a level with one prop at the origin carrying one 4 x 4 x 2 trigger box. The pig either
/// starts inside it or 50 m away.
/// </para>
/// </summary>
public sealed class ChallengeRoomTests
{
    private const uint PartPig = 4;
    private const uint PartPlayer = 1;
    private const uint SequencePlace = 1;
    private const uint SequenceStart = 2;

    private static readonly PhysicsVector3 Inside = new(0f, 0.6f, 0f);
    private static readonly PhysicsVector3 Far = new(50f, 0.6f, 0f);

    private static readonly GameplayZone FarBounds = new(
        new PhysicsVector3(-1000f, -1000f, -1000f),
        new PhysicsVector3(1000f, 1000f, 1000f));

    /// <summary>One challenge marker at the origin: a 4 x 4 x 2 box the pig has to reach.</summary>
    private static readonly LevelPropDefinition Marker = new("BoxChallenge", X: 0f, Y: 0f, Z: 0f, Rotation: 0f, ScaleX: 1f, ScaleY: 1f)
    {
        Triggers = [new LevelPropBoxDefinition(X: 0f, Y: 0f, Z: 0f, SizeX: 4f, SizeY: 4f, SizeZ: 2f, Rotation: 0f)],
    };

    [Fact]
    public void APigReachingAChallengeMarkerCollectsIt()
    {
        using GameRoom room = StartPigAt(Inside);
        Assert.Equal(1, room.CollectedPropCount);

        // The marker reports once, however long the pig stays inside it.
        room.RunTicks(120);
        Assert.Equal(1, room.CollectedPropCount);
    }

    [Fact]
    public void APigThatNeverReachesTheMarkerCollectsNothing()
    {
        using GameRoom room = StartPigAt(Far);
        room.RunTicks(120);
        Assert.Equal(0, room.CollectedPropCount);
    }

    [Fact]
    public void RetryPutsTheCollectedMarkersBack()
    {
        using GameRoom room = StartPigAt(Inside);
        Assert.Equal(1, room.CollectedPropCount);

        // The original's `Collectable.OnReset` shows the box again; the room's Retry is that route.
        room.Retry();
        Assert.Equal(0, room.CollectedPropCount);

        // And it can be collected again on the next run.
        Assert.True(room.Submit(PlayHost.BindPlayer(new StartSimulationCommand(0, SequenceStart + 1, 0), PartPlayer)).IsAccepted);
        room.RunTicks(30);
        Assert.Equal(1, room.CollectedPropCount);
    }

    /// <summary>A room whose level carries the marker, with the pig placed at <paramref name="drop"/> and started.</summary>
    private static GameRoom StartPigAt(PhysicsVector3 drop)
    {
        string root = FindRepositoryRoot();
        PartContentLibrary parts = PartContentLibrary.Load(Path.Combine(root, "content", "parts.json"));

        LevelContentDocument level = new(
            "challenge-room-test-v8",
            GoalZone: new GameplayZone(new PhysicsVector3(500f, 500f, 500f), new PhysicsVector3(501f, 501f, 501f)),
            MapBounds: FarBounds,
            Spawns: [])
        {
            Props = [Marker],
        };

        GameplayConfig config = new(
            level.GoalZone,
            level.MapBounds,
            TntBlastRadius: 4f,
            TntBlastImpulse: 25f,
            TntIgniteImpactSpeed: 5f,
            MaxTicks: 0,
            ObjectivesEnabled: false,
            ChallengeTriggers: LevelChallengeTriggers.Build(level));
        GameRoom room = new(GameRoomOptions.Create(
            parts,
            () => new BepuPhysicsWorld(new PhysicsVector3(0f, -9.81f, 0f)),
            config,
            sandboxMode: false));
        room.SetupFromLevel(level);
        Assert.True(room.Submit(PlayHost.BindPlayer(
            new PlacePartCommand(0, SequencePlace, 0, PartPig, drop.X, drop.Y, Angle: 0f, Scale: 1f),
            PartPlayer)).IsAccepted);
        Assert.True(room.Submit(PlayHost.BindPlayer(new StartSimulationCommand(0, SequenceStart, 0), PartPlayer)).IsAccepted);
        room.RunTicks(30);
        return room;
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
