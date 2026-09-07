using PigForge.Core.Content;
using Xunit.Sdk;

namespace PigForge.Core.Tests;

public sealed class LevelContentTests
{
    [Fact]
    public void SlopeLevelFromRepositoryLoadsWithAngle()
    {
        string path = FindRepositoryFile("content/levels/slope-v1.json");
        LevelContentDocument level = LevelContentLibrary.Parse(File.ReadAllText(path));
        Assert.Equal("slope-v1", level.ContentVersion);
        Assert.Single(level.Spawns);
        Assert.Equal(6u, level.Spawns[0].PartTypeId);
        Assert.Equal(-0.35f, level.Spawns[0].Angle);
        Assert.Equal(4.5f, level.GoalZone.Min.X);
    }
    [Fact]
    public void NonFiniteSpawnAngleIsRejected()
    {
        const string json = """
        {
            "format": "pigforge.level-content",
            "schemaVersion": 1,
            "contentVersion": "bad-angle",
            "goalZone": { "min": [0, 0, 0], "max": [1, 1, 1] },
            "bounds": { "min": [-10, -10, -10], "max": [10, 10, 10] },
            "spawns": [ { "partTypeId": 1, "position": [0, 0, 0], "angle": true } ]
        }
        """;

        LevelContentException exception = Assert.Throws<LevelContentException>(() => LevelContentParser.Parse(json));
        Assert.Contains("angle", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void InvertedGoalZoneIsRejected()
    {
        const string json = """
        {
            "format": "pigforge.level-content",
            "schemaVersion": 1,
            "contentVersion": "bad-zone",
            "goalZone": { "min": [1, 0, 0], "max": [0, 1, 1] },
            "bounds": { "min": [-10, -10, -10], "max": [10, 10, 10] },
            "spawns": []
        }
        """;

        Assert.Throws<LevelContentException>(() => LevelContentParser.Parse(json));
    }

    private static string FindRepositoryFile(string relativePath)
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, relativePath)))
        {
            directory = directory.Parent;
        }

        if (directory is null)
        {
            throw new XunitException($"Could not locate {relativePath} above {AppContext.BaseDirectory}.");
        }

        return Path.Combine(directory.FullName, relativePath);
    }
}
