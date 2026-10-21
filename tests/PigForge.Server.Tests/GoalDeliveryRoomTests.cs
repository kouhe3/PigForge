using PigForge.Core;
using PigForge.Core.Content;
using PigForge.Physics.Abstractions;
using PigForge.Physics.Bepu;
using PigForge.Protocol;
using PigForge.Server;

namespace PigForge.Server.Tests;

/// <summary>
/// The original's extra finish cargo (gap G114, <c>GameMode.PlayerHasRequiredObjects</c>,
/// <c>GameMode.cs:356-375</c>): a level that grants an egg only completes when the egg reaches the
/// goal too, otherwise one that grants a pumpkin wants the pumpkin. Which family that is comes from
/// the level's own allowance table (<see cref="LevelGoalRequirements"/>), so the room never hard-codes
/// a part id.
/// <para>
/// The rig is a level with no terrain: the pig is placed inside a goal column and the egg above it,
/// so the egg falls through the zone a moment after the Start. <see cref="GameplayZone"/> membership
/// is latched (the original's <c>PartsInGoal</c>), which the core suite covers directly.
/// </para>
/// </summary>
public sealed class GoalDeliveryRoomTests
{
    private const uint PartPig = 4;
    private const uint PartEgg = 27;
    private const uint PartPumpkin = 287;
    private const uint Player = 1;

    private static readonly GameplayZone GoalColumn = new(
        new PhysicsVector3(-1f, -1f, -2f),
        new PhysicsVector3(1f, 3f, 2f));

    private static readonly GameplayZone FarBounds = new(
        new PhysicsVector3(-1000f, -1000f, -1000f),
        new PhysicsVector3(1000f, 1000f, 1000f));

    /// <summary>A build grid 32 columns wide and 12 rows tall anchored on the origin: every cell these
    /// tests place into is inside it (<c>LevelManager.m_constructionGridRows</c>, -1 = all bits).</summary>
    private static readonly int[] EveryCell = [.. Enumerable.Repeat(-1, 12)];

    [Fact]
    public void ALevelThatGrantsAnEggNeedsItInTheGoal()
    {
        using GameRoom room = StartRoom(
            [new LevelPartAllowance(PartPig, 1), new LevelPartAllowance(PartEgg, 1)],
            (PartPig, 0f, 1f),
            (PartEgg, 0f, 5f));

        // The pig is inside the goal from the first tick; the egg drops in about 0.6 s later.
        Assert.Equal(GameplayPhase.Won, room.Phase);
    }

    /// <summary>The same rig with the egg placed out of the goal's reach: the pig alone must not
    /// finish the level, which is the whole of G114.</summary>
    [Fact]
    public void ALevelThatGrantsAnEggDoesNotCompleteOnThePigAlone()
    {
        using GameRoom room = StartRoom(
            [new LevelPartAllowance(PartPig, 1), new LevelPartAllowance(PartEgg, 1)],
            (PartPig, 0f, 1f),
            (PartEgg, 8f, 1f));

        Assert.Equal(GameplayPhase.Playing, room.Phase);
    }

    /// <summary>A level that grants neither family keeps the goal's own rule: the pig alone wins.</summary>
    [Fact]
    public void ALevelWithNoExtraPartCompletesOnThePigAlone()
    {
        using GameRoom room = StartRoom(
            [new LevelPartAllowance(PartPig, 1)],
            (PartPig, 0f, 1f));

        Assert.Equal(GameplayPhase.Won, room.Phase);
    }

    /// <summary>The shipped levels carry the split themselves: an egg level, a Halloween pumpkin
    /// level, and a level that wants the pig alone.</summary>
    [Fact]
    public void TheRealLevelsSayWhichExtraPartTheyWant()
    {
        using GameRoom egg = PlayHost.CreateLevelRoom("original/episode_4_levels/Level_43.json");
        Assert.Equal(PartEgg, egg.GoalExtraPartFamily);

        using GameRoom pumpkin = PlayHost.CreateLevelRoom("original/episode_5_levels/scenario_02.json");
        Assert.Equal(PartPumpkin, pumpkin.GoalExtraPartFamily);

        using GameRoom pigOnly = PlayHost.CreateLevelRoom("original/episode_1_levels/Level_05.json");
        Assert.Null(pigOnly.GoalExtraPartFamily);
    }

    /// <summary>A room over a level that grants <paramref name="allowances"/>, whose pig and extra
    /// parts are placed at the given build poses before the run starts.</summary>
    private static GameRoom StartRoom(
        LevelPartAllowance[] allowances,
        params (uint PartTypeId, float X, float Y)[] placements)
    {
        string root = FindRepositoryRoot();
        PartContentLibrary parts = PartContentLibrary.Load(Path.Combine(root, "content", "parts.json"));

        LevelContentDocument level = new(
            "goal-delivery-room-test-v9",
            GoalZone: GoalColumn,
            MapBounds: FarBounds,
            Spawns: [])
        {
            Build = new LevelBuildLimits(0f, 0f, EveryCell, allowances),
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

        uint sequence = 0;
        foreach ((uint partTypeId, float x, float y) in placements)
        {
            CommandOutcome placed = room.Submit(PlayHost.BindPlayer(
                new PlacePartCommand(0, ++sequence, 0, partTypeId, x, y, Angle: 0f, Scale: 1f),
                Player));
            Assert.True(placed.IsAccepted, $"place {partTypeId} at ({x},{y}): {placed.Status}/{placed.Error}");
        }

        Assert.True(room.Submit(PlayHost.BindPlayer(new StartSimulationCommand(0, ++sequence, 0), Player)).IsAccepted);
        room.RunTicks(120);
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
