using PigForge.Core;
using PigForge.Core.Content;
using PigForge.Physics.Abstractions;

namespace PigForge.Core.Tests;

/// <summary>
/// Which extra part a level wants in the goal with the pig (<see cref="LevelGoalRequirements"/>,
/// gap G114). The original derives it from the level's own allowance table
/// (<c>LevelManager.EggRequired</c>/<c>PumpkinRequired</c>, <c>LevelManager.cs:413-432</c>), so the
/// table and the content's own egg/pumpkin families have to line up.
/// </summary>
public sealed class GoalRequirementsTests
{
    [Fact]
    public void TheGrantedEggOrPumpkinIsTheExtraCargoAndTheEggWins()
    {
        PartContentLibrary content = PartContentLibrary.Load(FindRepositoryFile("content/parts.json"));

        Assert.Null(LevelGoalRequirements.RequiredExtraPartFamily(Level(new LevelPartAllowance(4, 1)), content));
        Assert.Equal(27u, LevelGoalRequirements.RequiredExtraPartFamily(
            Level(new LevelPartAllowance(4, 1), new LevelPartAllowance(27, 1)),
            content));
        Assert.Equal(287u, LevelGoalRequirements.RequiredExtraPartFamily(
            Level(new LevelPartAllowance(4, 1), new LevelPartAllowance(287, 1)),
            content));

        // `GameMode.PlayerHasRequiredObjects` returns inside the egg branch, so the pumpkin never
        // counts once an egg is granted.
        Assert.Equal(27u, LevelGoalRequirements.RequiredExtraPartFamily(
            Level(new LevelPartAllowance(287, 1), new LevelPartAllowance(27, 1)),
            content));

        // A zero-count slot is a family the level does not grant (`ConstructionUI.CheckUnlockedParts`
        // hides it), and a document with no `build` block grants nothing at all.
        Assert.Null(LevelGoalRequirements.RequiredExtraPartFamily(Level(new LevelPartAllowance(27, 0)), content));
        Assert.Null(LevelGoalRequirements.RequiredExtraPartFamily(
            new LevelContentDocument("goal-requirements-test", GoalZone: Zone, MapBounds: Zone, Spawns: []),
            content));
    }

    /// <summary>The pack's own split: 45 levels grant an egg (all on an egg goal prefab), 15 grant a
    /// pumpkin (all on the Halloween one), and the rest want the pig alone.</summary>
    [Fact]
    public void TheRealPackSplitsItsExtraCargoBetweenEggsAndPumpkins()
    {
        PartContentLibrary content = PartContentLibrary.Load(FindRepositoryFile("content/parts.json"));

        Assert.Equal(27u, RequiredOf("original/episode_4_levels/Level_43.json", content));
        Assert.Equal(287u, RequiredOf("original/episode_5_levels/scenario_02.json", content));
        Assert.Null(RequiredOf("original/episode_1_levels/Level_05.json", content));
    }

    private static readonly GameplayZone Zone = new(
        new PhysicsVector3(-1f, -1f, -1f),
        new PhysicsVector3(1f, 1f, 1f));

    private static LevelContentDocument Level(params LevelPartAllowance[] counts) =>
        new("goal-requirements-test", GoalZone: Zone, MapBounds: Zone, Spawns: [])
        {
            Build = new LevelBuildLimits(0f, 0f, [1], counts),
        };

    private static uint? RequiredOf(string relativePath, PartContentLibrary content) =>
        LevelGoalRequirements.RequiredExtraPartFamily(
            LevelContentLibrary.Parse(File.ReadAllText(FindRepositoryFile(Path.Combine("content", "levels", relativePath)))),
            content);

    private static string FindRepositoryFile(string relativePath)
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

        return Path.Combine(directory.FullName, relativePath);
    }
}
