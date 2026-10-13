using PigForge.Core;
using PigForge.Core.Content;
using PigForge.Physics.Abstractions;
using PigForge.Physics.Bepu;
using PigForge.Protocol;
using PigForge.Server;

namespace PigForge.Server.Tests;

/// <summary>
/// The level's own challenges and the stars they earn (schemaVersion 10,
/// `docs/specs/level-stars.md`): the room judges them once, on the tick the pig reaches the goal,
/// which is where the original's <c>LevelComplete</c> reads each <c>Challenge.IsCompleted()</c>. The
/// three-star run is the goal star plus one star per completed challenge among the level's first two.
/// <para>
/// The rig is a level with no terrain: the pig is dropped and falls into the goal zone, so the run
/// clock starts (the pose leaves the start by a metre) before the win.
/// </para>
/// </summary>
public sealed class LevelStarRoomTests
{
    private const uint PartPig = 4;
    private const uint PartTnt = 9;
    private const uint PartWoodenBlock = 1;

    /// <summary>A rocket of the <c>m_explodes</c> family. Its switch is a button
    /// (<c>activation: "trigger"</c>), and a level room registers switches like any other room
    /// (gap G115), so this fixture presses it after Start.</summary>
    private const uint PartRocket = 158;
    private const uint PartPlayer = 1;
    private const uint SequencePlace = 1;
    private const uint SequenceStart = 2;

    private static readonly PhysicsVector3 Drop = new(0f, 10f, 0f);

    private static readonly GameplayZone FarBounds = new(
        new PhysicsVector3(-1000f, -1000f, -1000f),
        new PhysicsVector3(1000f, 1000f, 1000f));

    /// <summary>A goal column under the drop point: a falling pig crosses it about half a second in,
    /// which leaves the clock past its own metre and the two-decimal completion time above zero.</summary>
    private static readonly GameplayZone DropGoal = new(
        new PhysicsVector3(-50f, -100f, -2f),
        new PhysicsVector3(50f, 8f, 2f));

    /// <summary>One challenge marker at the origin: a 4 x 4 x 2 box, the way the collected props use it.</summary>
    private static readonly LevelPropDefinition Marker = new("BoxChallenge", X: 0f, Y: 0f, Z: 0f, Rotation: 0f, ScaleX: 1f, ScaleY: 1f)
    {
        Triggers = [new LevelPropBoxDefinition(X: 0f, Y: 0f, Z: 0f, SizeX: 4f, SizeY: 4f, SizeZ: 2f, Rotation: 0f)],
    };

    /// <summary>A goal column below the rig's build pose: the run is won once the fall crosses it, a
    /// couple of seconds after anything the rig does on its own.</summary>
    private static readonly GameplayZone FallGoal = new(
        new PhysicsVector3(-50f, -100f, -2f),
        new PhysicsVector3(50f, -2f, 2f));

    [Fact]
    public void AGoalReachedInTimeEarnsTheGoalStarAndTheChallengeStar()
    {
        using GameRoom room = StartRoom(
            [new LevelChallengeDefinition("TimeChallenge", LevelChallengeKind.Time, -1, 30f, 0)],
            DropGoal);

        Assert.Equal(GameplayPhase.Won, room.Phase);
        Assert.True(room.ElapsedSeconds > 0f, "the fall left the start pose, so the run clock ran");
        Assert.Equal(2, room.StarCount);
        Assert.Equal(0b01, room.ChallengeFlags);
    }

    [Fact]
    public void TheSameRunMissesATimeChallengeTighterThanItsCompletionTime()
    {
        using GameRoom room = StartRoom(
            [new LevelChallengeDefinition("TimeChallenge", LevelChallengeKind.Time, -1, 0f, 0)],
            DropGoal);

        // The original floors the completion time to a hundredth of a second (TimeChallenge.cs:11), and
        // the fall takes longer than that, so the challenge is lost and only the goal star is left.
        Assert.Equal(GameplayPhase.Won, room.Phase);
        Assert.True(room.ElapsedSeconds >= 0.01f);
        Assert.Equal(1, room.StarCount);
        Assert.Equal(0, room.ChallengeFlags);
    }

    [Fact]
    public void APartTheBuildHoldsAndAPartItCarriesAreJudgedAgainstTheRoom()
    {
        // The build holds the pig, so a challenge forbidding TNT is earned and a part the pig carries
        // (the pig family itself rides its own component) is transported.
        using GameRoom forNothing = StartRoom(
            [
                new LevelChallengeDefinition("DontUsePartChallenge", LevelChallengeKind.DontUseParts, -1, 0f, PartTnt),
                new LevelChallengeDefinition("TransportChallenge", LevelChallengeKind.Transport, -1, 0f, PartPig),
            ],
            DropGoal);
        Assert.Equal(0b11, forNothing.ChallengeFlags);
        Assert.Equal(3, forNothing.StarCount);

        // The mirrored pair: the build holds a pig, and no TNT rides the contraption.
        using GameRoom forThePig = StartRoom(
            [
                new LevelChallengeDefinition("DontUsePartChallenge", LevelChallengeKind.DontUseParts, -1, 0f, PartPig),
                new LevelChallengeDefinition("TransportChallenge", LevelChallengeKind.Transport, -1, 0f, PartTnt),
            ],
            DropGoal);
        Assert.Equal(0, forThePig.ChallengeFlags);
        Assert.Equal(1, forThePig.StarCount);
    }

    [Fact]
    public void CollectingTheChallengeBoxAndReachingTheGoalTogetherTakeAllThreeStars()
    {
        // The marker sits at the origin and the goal zone is the marker's own box, so the pig starts
        // inside both: the box is collected and the run is won on the same tick.
        GameplayZone markerGoal = new(new PhysicsVector3(-2f, -2f, -2f), new PhysicsVector3(2f, 2f, 2f));
        using GameRoom room = StartRoom(
            [
                new LevelChallengeDefinition("BoxChallenge", LevelChallengeKind.Box, 0, 0f, 0),
                new LevelChallengeDefinition("TimeChallenge", LevelChallengeKind.Time, -1, 30f, 0),
            ],
            markerGoal,
            props: [Marker],
            drop: new PhysicsVector3(0f, 0.6f, 0f));

        Assert.Equal(1, room.CollectedPropCount);
        Assert.Equal(0b11, room.ChallengeFlags);
        Assert.Equal(3, room.StarCount);
    }

    [Fact]
    public void RetryTakesTheFinishedRunsStarsBack()
    {
        using GameRoom room = StartRoom(
            [new LevelChallengeDefinition("TimeChallenge", LevelChallengeKind.Time, -1, 30f, 0)],
            DropGoal);
        Assert.Equal(2, room.StarCount);

        room.Retry();

        Assert.Equal(GameplayPhase.Playing, room.Phase);
        Assert.Equal(0, room.StarCount);
        Assert.Equal(0, room.ChallengeFlags);
        Assert.Equal(0f, room.ElapsedSeconds);
    }

    [Fact]
    public void APerfectFlightChallengeIsLostOnceAPartLeavesTheContraption()
    {
        // A rocket welded next to the pig's frame burns once its button is pressed (the rig presses it
        // after Start -- `Rocket.Awake` leaves `m_enabled` false, Rocket.cs:123) and its thrust tears
        // the seam it shares with the rig: the rig comes apart mid-run, which is the joint loss the
        // original marks the contraption broken for (Contraption.cs:2810-2812). The room's seam
        // threshold is zero in this fixture, so the split is that thrust and not a physics coincidence;
        // what the test pins is the room's break -> challenge wiring, not the threshold
        // (docs/specs/level-stars.md §4.2).
        using GameRoom lost = StartWeldedRig(withRocket: true);
        Assert.Equal(GameplayPhase.Won, lost.Phase);
        Assert.Equal(0, lost.ChallengeFlags);
        Assert.Equal(1, lost.StarCount);

        // The same rig without the rocket never loses a seam.
        using GameRoom whole = StartWeldedRig(withRocket: false);
        Assert.Equal(0b01, whole.ChallengeFlags);
        Assert.Equal(2, whole.StarCount);
    }

    /// <summary>
    /// A rig that falls into a goal column: a wooden frame with the pig enclosed in it (one compound),
    /// optionally with a rocket welded on that tears that compound apart as soon as it pushes.
    /// </summary>
    private static GameRoom StartWeldedRig(bool withRocket)
    {
        string root = FindRepositoryRoot();
        PartContentLibrary parts = PartContentLibrary.Load(Path.Combine(root, "content", "parts.json"));

        LevelContentDocument level = new(
            "level-star-welded-rig-test-v10",
            GoalZone: FallGoal,
            MapBounds: FarBounds,
            Spawns: [])
        {
            Challenges = [new LevelChallengeDefinition("PerfectFlightChallenge", LevelChallengeKind.PerfectFlight, -1, 0f, 0)],
        };

        GameplayConfig config = new(
            level.GoalZone,
            level.MapBounds,
            TntBlastRadius: 4f,
            TntBlastImpulse: 25f,
            TntIgniteImpactSpeed: 5f,
            SeamBreakImpulse: 0f,
            MaxTicks: 0,
            ObjectivesEnabled: true,
            ChallengeTriggers: LevelChallengeTriggers.Build(level));
        GameRoom room = new(GameRoomOptions.Create(
            parts,
            () => new BepuPhysicsWorld(new PhysicsVector3(0f, -9.81f, 0f)),
            config,
            sandboxMode: false));
        room.SetupFromLevel(level);

        uint sequence = 1;
        Place(room, ref sequence, PartWoodenBlock, 0f, 0.5f);
        uint rocket = 0;
        if (withRocket)
        {
            rocket = Place(room, ref sequence, PartRocket, 1f, 0.5f);
        }

        Place(room, ref sequence, PartPig, 0f, 0.5f);
        Assert.True(room.Submit(PlayHost.BindPlayer(new StartSimulationCommand(0, ++sequence, 0), PartPlayer)).IsAccepted);
        if (withRocket)
        {
            // `Rocket.Awake` clears `m_enabled` (Rocket.cs:123), so the part waits for its switch:
            // the press is what arms the burn whose thrust tears the seam.
            Assert.True(room.Submit(PlayHost.BindPlayer(
                new SetPartActiveCommand(0, ++sequence, PartPlayer, rocket, true), PartPlayer)).IsAccepted);
        }

        room.RunTicks(240);
        return room;
    }

    private static uint Place(GameRoom room, ref uint sequence, uint partTypeId, float x, float y)
    {
        CommandOutcome outcome = room.Submit(PlayHost.BindPlayer(
            new PlacePartCommand(0, ++sequence, 0, partTypeId, x, y, Angle: 0f, Scale: 1f),
            PartPlayer));
        Assert.True(outcome.IsAccepted, $"place {partTypeId} at ({x},{y}): {outcome.Status}/{outcome.Error}");
        return outcome.EntityId;
    }

    /// <summary>A level room with the given challenges, its pig placed at <paramref name="drop"/> and started.</summary>
    private static GameRoom StartRoom(
        IReadOnlyList<LevelChallengeDefinition> challenges,
        GameplayZone goal,
        IReadOnlyList<LevelPropDefinition>? props = null,
        PhysicsVector3? drop = null)
    {
        string root = FindRepositoryRoot();
        PartContentLibrary parts = PartContentLibrary.Load(Path.Combine(root, "content", "parts.json"));

        LevelContentDocument level = new(
            "level-star-room-test-v10",
            GoalZone: goal,
            MapBounds: FarBounds,
            Spawns: [])
        {
            Props = props ?? [],
            Challenges = challenges,
        };

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
        PhysicsVector3 placement = drop ?? Drop;
        Assert.True(room.Submit(PlayHost.BindPlayer(
            new PlacePartCommand(0, SequencePlace, 0, PartPig, placement.X, placement.Y, Angle: 0f, Scale: 1f),
            PartPlayer)).IsAccepted);
        Assert.True(room.Submit(PlayHost.BindPlayer(new StartSimulationCommand(0, SequenceStart, 0), PartPlayer)).IsAccepted);
        room.RunTicks(240);
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
