using PigForge.Core;
using PigForge.Core.Construction;
using PigForge.Core.Content;
using PigForge.Physics.Abstractions;
using PigForge.Physics.Bepu;
using PigForge.Protocol;
using PigForge.Server;

namespace PigForge.Server.Tests;

/// <summary>
/// A level's own construction limits (schemaVersion 9 <c>build</c>, see
/// <see cref="LevelBuildLimits"/>: the grid a placed part's anchor cell must sit in and the
/// per-family quota the build bar draws from). <see cref="GameRoom.SetupFromLevel"/> installs them on
/// the room (<see cref="ConstructionRules.UseLevelBuildLimits"/>); every rejection below is observed
/// through the command layer -- <see cref="GameRoom.Submit"/> answers <see cref="CommandStatus.RuleRejected"/>
/// with the matching <see cref="ConstructionError"/> -- never by calling the rules directly.
/// <para>
/// The rig is the shipped Level_05: its build origin (2.4379761, 5.45759) floors to cell (2, 5) and its
/// rows [15, 15, 0] open world columns x = 1..4 and rows y = 5..6, eight placeable cells, so x = 5.5 is
/// one column past the grid; its quota table allows three wooden blocks, one pig, and no TNT at all.
/// The controls are the identical document with <c>Build</c> cleared (the same coordinates are all
/// accepted, so the limits are introduced by <c>build</c> and nothing else) and the sandbox room.
/// </para>
/// </summary>
public sealed class ConstructionLimitsRoomTests
{
    private const string LevelFile = "original/episode_1_levels/Level_05.json";

    private const uint PartWoodenBlock = 1;
    private const uint PartPig = 4;
    private const uint PartTnt = 9;

    private const uint Player = 1;

    // Cell centres of Level_05's own grid (the anchor cell is the floor of the position).
    private const float X1 = 1.5f;
    private const float X2 = 2.5f;
    private const float X3 = 3.5f;
    private const float X4 = 4.5f;
    private const float Y5 = 5.5f;
    private const float Y6 = 6.5f;

    // One cell past each edge of the grid: x = 5 is one column right of 4, x = 0 one left of 1,
    // y = 7 one row above 6.
    private const float OffGridHighX = 5.5f;
    private const float OffGridLowX = 0.5f;
    private const float OffGridY = 7.5f;

    [Fact]
    public void ALevelRoomRejectsAPlacementOffItsOwnBuildGrid()
    {
        using GameRoom room = PlayHost.CreateLevelRoom(LevelFile);

        // Cell (2, 5) is inside the level's grid.
        CommandOutcome inside = Place(room, 1, PartWoodenBlock, X2, Y5);
        Assert.Equal(CommandStatus.Accepted, inside.Status);
        Assert.Equal(ConstructionError.None, inside.Error);

        AssertRejected(Place(room, 2, PartWoodenBlock, OffGridHighX, Y5), ConstructionError.OutsideConstructionGrid);
        AssertRejected(Place(room, 3, PartWoodenBlock, OffGridLowX, Y5), ConstructionError.OutsideConstructionGrid);
        AssertRejected(Place(room, 4, PartWoodenBlock, X2, OffGridY), ConstructionError.OutsideConstructionGrid);

        // The rejected commands never became entities, so the grid slot they name is still free.
        Assert.True(Place(room, 5, PartWoodenBlock, X4, Y6).IsAccepted);
    }

    [Fact]
    public void ALevelRoomRejectsAFamilyPastItsOwnQuota()
    {
        using GameRoom room = PlayHost.CreateLevelRoom(LevelFile);

        // The level allows three wooden blocks (1): the fourth is over quota, in the grid.
        Assert.True(Place(room, 1, PartWoodenBlock, X1, Y5).IsAccepted);
        Assert.True(Place(room, 2, PartWoodenBlock, X2, Y5).IsAccepted);
        Assert.True(Place(room, 3, PartWoodenBlock, X3, Y5).IsAccepted);
        AssertRejected(Place(room, 4, PartWoodenBlock, X4, Y5), ConstructionError.PartAllowanceExceeded);

        // The level allows one pig (4). Level_05 ships no actors of its own, and a level-authored
        // spawn would not count here either: level spawns are not construction entities.
        Assert.True(Place(room, 5, PartPig, X1, Y6).IsAccepted);
        AssertRejected(Place(room, 6, PartPig, X2, Y6), ConstructionError.PartAllowanceExceeded);

        // TNT (9) is not in the level's table at all: a family the table does not name allows none.
        AssertRejected(Place(room, 7, PartTnt, X3, Y6), ConstructionError.PartAllowanceExceeded);
    }

    [Fact]
    public void ReEnteringTheBuildingPhaseKeepsTheLevelsLimits()
    {
        using GameRoom room = PlayHost.CreateLevelRoom(LevelFile);
        Assert.True(Place(room, 1, PartWoodenBlock, X2, Y5).IsAccepted);
        Assert.True(room.Submit(PlayHost.BindPlayer(new StartSimulationCommand(0, 2, 0), Player)).IsAccepted);

        // Retry destroys the bodies, resets the construction registry and re-runs SetupFromLevel;
        // ResetAll never clears the grid or the family table, so the limits survive the re-entry.
        Assert.True(room.Submit(PlayHost.BindPlayer(new RetryCommand(0, 3, 0), Player)).IsAccepted);
        Assert.Equal(RoomMode.Building, room.Mode);

        // The retry replayed the one block it captured, so grid and quota are still live.
        AssertRejected(Place(room, 4, PartWoodenBlock, OffGridHighX, Y5), ConstructionError.OutsideConstructionGrid);
        Assert.True(Place(room, 5, PartWoodenBlock, X3, Y5).IsAccepted);
        Assert.True(Place(room, 6, PartWoodenBlock, X4, Y5).IsAccepted);
        AssertRejected(Place(room, 7, PartWoodenBlock, X1, Y6), ConstructionError.PartAllowanceExceeded);
    }

    [Fact]
    public void TheSameDocumentWithoutItsBuildBlockIsUnrestricted()
    {
        LevelContentDocument shipped = LevelContentLibrary.Parse(
            File.ReadAllText(Path.Combine(FindRepositoryRoot(), "content", "levels", "original", "episode_1_levels", "Level_05.json")));
        Assert.NotNull(shipped.Build);

        // Same document, same coordinates, one field cleared: every rejection above must go away.
        LevelContentDocument unbounded = shipped with { Build = null };
        using GameRoom room = CreateRoom(unbounded);

        Assert.True(Place(room, 1, PartWoodenBlock, X1, Y5).IsAccepted);
        Assert.True(Place(room, 2, PartWoodenBlock, X2, Y5).IsAccepted);
        Assert.True(Place(room, 3, PartWoodenBlock, X3, Y5).IsAccepted);
        Assert.True(Place(room, 4, PartWoodenBlock, X4, Y5).IsAccepted);
        Assert.True(Place(room, 5, PartWoodenBlock, OffGridHighX, Y5).IsAccepted);
        Assert.True(Place(room, 6, PartPig, X1, Y6).IsAccepted);
        Assert.True(Place(room, 7, PartPig, X2, Y6).IsAccepted);
        Assert.True(Place(room, 8, PartTnt, OffGridLowX, Y5).IsAccepted);
    }

    [Fact]
    public void ASandboxRoomStaysUnrestricted()
    {
        // The sandbox level (content/levels/terrain-v1.json) is a v1 document with no build block, so
        // the sandbox room installs no limits: the coordinates the level room rejects stay accepted.
        using GameRoom room = PlayHost.CreateSandboxRoom();

        Assert.True(Place(room, 1, PartWoodenBlock, OffGridHighX, Y5).IsAccepted);
        Assert.True(Place(room, 2, PartWoodenBlock, X1, Y5).IsAccepted);
        Assert.True(Place(room, 3, PartWoodenBlock, X2, Y5).IsAccepted);
        Assert.True(Place(room, 4, PartWoodenBlock, X3, Y5).IsAccepted);
    }

    /// <summary>Submits one build-phase placement through the room's command layer.</summary>
    private static CommandOutcome Place(GameRoom room, uint sequence, uint partTypeId, float x, float y) =>
        room.Submit(PlayHost.BindPlayer(
            new PlacePartCommand(0, sequence, 0, partTypeId, x, y, Angle: 0f, Scale: 1f),
            Player));

    private static void AssertRejected(CommandOutcome outcome, ConstructionError error)
    {
        Assert.Equal(CommandStatus.RuleRejected, outcome.Status);
        Assert.Equal(error, outcome.Error);
    }

    /// <summary>The room PlayHost builds, minus the file lookup: a non-sandbox level room.</summary>
    private static GameRoom CreateRoom(LevelContentDocument level)
    {
        PartContentLibrary parts = PartContentLibrary.Load(Path.Combine(FindRepositoryRoot(), "content", "parts.json"));
        GameplayConfig config = new(
            level.GoalZone,
            level.MapBounds,
            TntBlastRadius: 4f,
            TntBlastImpulse: 25f,
            TntIgniteImpactSpeed: 5f,
            MaxTicks: 0,
            ObjectivesEnabled: false,
            CameraLimits: level.CameraLimits,
            ChallengeTriggers: LevelChallengeTriggers.Build(level));
        GameRoom room = new(GameRoomOptions.Create(
            parts,
            () => new BepuPhysicsWorld(new PhysicsVector3(0f, -9.81f, 0f)),
            config,
            sandboxMode: false));
        room.SetupFromLevel(level);
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
