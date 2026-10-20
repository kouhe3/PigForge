using System.Text.Json;
using PigForge.Core;
using PigForge.Core.Construction;
using PigForge.Server;

namespace PigForge.Server.Tests;

/// <summary>
/// The dev harness' content and query side (`docs/specs/dev-harness.md`): scenarios load strictly,
/// seed real construction parts, and a world query resolves to exactly one world. The HTTP/websocket
/// wiring itself is exercised by the real-server smoke in the slice's verification.
/// </summary>
public sealed class DevHarnessTests
{
    [Fact]
    public void TheShippedScenariosAreValid()
    {
        string root = FindRepositoryRoot();

        (IReadOnlyList<ScenarioDocument> scenarios, IReadOnlyList<string> errors) = ScenarioLoader.ListWithErrors(root);

        Assert.Empty(errors);
        Assert.Equal(["rope-bench", "sky-bench", "switch-bench", "tnt-bench"], scenarios.Select(scenario => scenario.Id));
        Assert.All(scenarios, scenario =>
        {
            Assert.False(string.IsNullOrWhiteSpace(scenario.Name));
            Assert.EndsWith(".json", scenario.Level, StringComparison.Ordinal);
            Assert.NotEmpty(scenario.Build);
            string level = Path.Combine(root, "content", "levels", scenario.Level);
            Assert.True(File.Exists(level), $"{scenario.Id}: level {scenario.Level} is missing");
        });
        Assert.True(scenarios.Single(scenario => scenario.Id == "sky-bench").Start);
        Assert.False(scenarios.Single(scenario => scenario.Id == "tnt-bench").Start);
    }

    [Fact]
    public void AScenarioSeedsItsPartsAndStartsOnRequest()
    {
        string root = FindRepositoryRoot();
        ScenarioDocument scenario = ScenarioLoader.Load(Path.Combine(root, "content", "scenarios", "switch-bench.json"));
        using GameRoom room = PlayHost.CreateTerrainRoom();

        int before = room.MaxSnapshotEntityCount;
        for (int index = 0; index < scenario.Build.Count; index++)
        {
            ScenarioPart part = scenario.Build[index];
            ConstructionResult placed = room.SeedPart(part.PartTypeId, part.X, part.Y, part.Angle, part.Scale, part.Mirrored);
            Assert.True(placed.IsSuccess, $"build[{index}] (part {part.PartTypeId}) was rejected: {placed.Error}");
        }

        Assert.Equal(scenario.Build.Count, room.MaxSnapshotEntityCount - before);
        Assert.Equal(RoomMode.Building, room.Mode);

        room.Start();
        Assert.Equal(RoomMode.Running, room.Mode);
    }

    [Fact]
    public void ARejectedScenarioPartIsAnErrorNotASilentSkip()
    {
        using GameRoom room = PlayHost.CreateTerrainRoom();

        int before = room.MaxSnapshotEntityCount;
        Assert.True(room.SeedPart(1, 0f, -2.5f, 0f, 1f, mirrored: false).IsSuccess);
        ConstructionResult overlapping = room.SeedPart(1, 0f, -2.5f, 0f, 1f, mirrored: false);
        Assert.Equal(ConstructionError.CellsOccupied, overlapping.Error);
        Assert.Equal(1, room.MaxSnapshotEntityCount - before);

        ConstructionResult unknown = room.SeedPart(9999, 4f, -2.5f, 0f, 1f, mirrored: false);
        Assert.Equal(ConstructionError.UnknownPartType, unknown.Error);
    }

    [Fact]
    public void AWorldQueryResolvesExactlyOneWorld()
    {
        string root = FindRepositoryRoot();

        DevWorld sandbox = DevWorlds.Resolve(root, scenarioId: null, levelFile: null);
        Assert.True(sandbox.Sandbox);
        Assert.Null(sandbox.Scenario);
        Assert.Equal("terrain-v1.json", sandbox.LevelFile);

        DevWorld scenario = DevWorlds.Resolve(root, "sky-bench", levelFile: null);
        Assert.False(scenario.Sandbox);
        Assert.Equal("sky-bench", scenario.Scenario?.Id);
        Assert.Equal("bench-v1.json", scenario.LevelFile);
        using JsonDocument levelDocument = JsonDocument.Parse(scenario.LevelJson);
        Assert.Equal("bench-v1", levelDocument.RootElement.GetProperty("contentVersion").GetString());

        DevWorld level = DevWorlds.Resolve(root, scenarioId: null, "original/episode_1_levels/Level_05.json");
        Assert.False(level.Sandbox);
        Assert.Null(level.Scenario);
        Assert.Equal("original/episode_1_levels/Level_05.json", level.LevelFile);

        Assert.Throws<InvalidDataException>(() => DevWorlds.Resolve(root, "sky-bench", "terrain-v1.json"));
        Assert.Throws<InvalidDataException>(() => DevWorlds.Resolve(root, scenarioId: null, "../parts.json"));
        Assert.Throws<InvalidDataException>(() => DevWorlds.Resolve(root, scenarioId: null, Path.Combine(root, "content", "parts.json")));
        Assert.Throws<InvalidDataException>(() => DevWorlds.Resolve(root, "missing-bench", levelFile: null));
    }

    [Fact]
    public void TheWorldListCarriesEveryScenarioAndLevel()
    {
        string root = FindRepositoryRoot();
        using JsonDocument list = JsonDocument.Parse(DevWorlds.ListJson(root));
        JsonElement rootElement = list.RootElement;

        Assert.Equal(4, rootElement.GetProperty("scenarios").GetArrayLength());
        Assert.Equal(0, rootElement.GetProperty("errors").GetArrayLength());
        // 277 converted original levels plus the three hand-made test levels (slope, terrain, bench).
        Assert.Equal(280, rootElement.GetProperty("levels").GetArrayLength());
        Assert.Contains(
            rootElement.GetProperty("levels").EnumerateArray(),
            level => level.GetProperty("path").GetString() == "original/episode_1_levels/Level_05.json");
    }

    [Fact]
    public void ABrokenScenarioFileIsRejectedWithItsReason()
    {
        string path = Path.Combine(Path.GetTempPath(), $"pigforge-scenario-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(path, """{ "format": "pigforge.scenario", "schemaVersion": 1, "id": "bad", "name": "x", "level": "terrain-v1.json", "build": [ { "partTypeId": 1, "x": 0, "y": 0, "extra": 1 } ] }""");
            InvalidDataException error = Assert.Throws<InvalidDataException>(() => ScenarioLoader.Load(path));
            Assert.Contains("extra", error.Message, StringComparison.OrdinalIgnoreCase);

            File.WriteAllText(path, """{ "format": "pigforge.scenario", "schemaVersion": 2, "id": "bad", "name": "x", "level": "terrain-v1.json", "build": [] }""");
            Assert.Throws<InvalidDataException>(() => ScenarioLoader.Load(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "content", "parts.json")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new Xunit.Sdk.XunitException("Could not locate content/parts.json.");
    }
}
