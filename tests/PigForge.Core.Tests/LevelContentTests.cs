using PigForge.Core.Content;
using PigForge.Physics.Abstractions;
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

    [Fact]
    public void AVersionTwoLevelCarriesItsTerrain()
    {
        const string json = """
        {
            "format": "pigforge.level-content",
            "schemaVersion": 2,
            "contentVersion": "terrain-v2",
            "goalZone": { "min": [0, 0, 0], "max": [1, 1, 1] },
            "bounds": { "min": [-10, -10, -10], "max": [10, 10, 10] },
            "spawns": [],
            "terrain": [
                {
                    "position": [-2.7907727, 9.021405, 0],
                    "depth": 10,
                    "loops": [ [ [0, 0], [4, 0], [4, 3] ], [ [10, 0], [12, 0], [12, 1] ] ]
                }
            ]
        }
        """;

        LevelContentDocument level = LevelContentParser.Parse(json);

        LevelTerrainDefinition terrain = Assert.Single(level.Terrain);
        Assert.Equal(-2.7907727f, terrain.Position.X);
        Assert.Equal(9.021405f, terrain.Position.Y);
        Assert.Equal(10f, terrain.Depth);
        Assert.Equal(2, terrain.Loops.Count);
        Assert.Equal(3, terrain.Loops[0].Count);
        Assert.Equal(4f, terrain.Loops[0][1].X);
        Assert.Equal(0f, terrain.Loops[0][1].Z);
    }

    [Fact]
    public void AVersionOneLevelStillParses()
    {
        string path = FindRepositoryFile("content/levels/slope-v1.json");
        LevelContentDocument level = LevelContentLibrary.Parse(File.ReadAllText(path));

        Assert.Empty(level.Terrain);
    }

    [Theory]
    [InlineData("""{ "position": [0, 0, 0], "depth": 10, "loops": [[[0,0],[1,0],[1,1]]], "extra": 1 }""", "extra")]
    [InlineData("""{ "position": [0, 0, 0], "depth": 0, "loops": [[[0,0],[1,0],[1,1]]] }""", "depth")]
    [InlineData("""{ "position": [0, 0, 0], "depth": 10, "loops": [] }""", "loops")]
    [InlineData("""{ "position": [0, 0, 0], "depth": 10, "loops": [[[0,0],[1,0]]] }""", "three points")]
    [InlineData("""{ "position": [0, 0, 0], "depth": 10, "loops": [[[0,0,0],[1,0,0],[1,1,0]]] }""", "[x, y]")]
    public void AMalformedTerrainIsRejected(string terrain, string expected)
    {
        string json = $$"""
        {
            "format": "pigforge.level-content",
            "schemaVersion": 2,
            "contentVersion": "bad-terrain",
            "goalZone": { "min": [0, 0, 0], "max": [1, 1, 1] },
            "bounds": { "min": [-10, -10, -10], "max": [10, 10, 10] },
            "spawns": [],
            "terrain": [ {{terrain}} ]
        }
        """;

        LevelContentException exception = Assert.Throws<LevelContentException>(() => LevelContentParser.Parse(json));
        Assert.Contains(expected, exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AnUnknownSchemaVersionIsRejected()
    {
        const string json = """
        {
            "format": "pigforge.level-content",
            "schemaVersion": 4,
            "contentVersion": "future",
            "goalZone": { "min": [0, 0, 0], "max": [1, 1, 1] },
            "bounds": { "min": [-10, -10, -10], "max": [10, 10, 10] },
            "spawns": []
        }
        """;

        LevelContentException exception = Assert.Throws<LevelContentException>(() => LevelContentParser.Parse(json));
        Assert.Contains("schemaVersion", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// v3 describes every `e2dTerrain` the original ships: the collider bit (498 of the 2146 terrains
    /// are decoration) and the ground's fill -- `fill.shader`'s texture, tint, tile offset and tile
    /// size (docs/specs/level-terrain-visuals.md).
    /// </summary>
    [Fact]
    public void AVersionThreeLevelCarriesTheGroundsFill()
    {
        const string json = """
        {
            "format": "pigforge.level-content",
            "schemaVersion": 3,
            "contentVersion": "terrain-v3",
            "goalZone": { "min": [0, 0, 0], "max": [1, 1, 1] },
            "bounds": { "min": [-10, -10, -10], "max": [10, 10, 10] },
            "spawns": [],
            "terrain": [
                {
                    "position": [-2.7907727, 9.021405, 0],
                    "depth": 10,
                    "collider": false,
                    "fill": {
                        "texture": "Ground_Rocks_Texture.png",
                        "color": [131, 131, 131, 255],
                        "tileOffset": [0, 6.2],
                        "tileSize": [5, 5]
                    },
                    "loops": [ [ [0, 0], [4, 0], [4, 3] ] ]
                }
            ]
        }
        """;

        LevelContentDocument level = LevelContentParser.Parse(json);

        LevelTerrainDefinition terrain = Assert.Single(level.Terrain);
        Assert.False(terrain.Collider);
        LevelTerrainFillDefinition fill = Assert.IsType<LevelTerrainFillDefinition>(terrain.Fill);
        Assert.Equal("Ground_Rocks_Texture.png", fill.Texture);
        Assert.Equal((byte)131, fill.Red);
        Assert.Equal((byte)131, fill.Green);
        Assert.Equal((byte)131, fill.Blue);
        Assert.Equal(byte.MaxValue, fill.Alpha);
        Assert.Equal(0f, fill.TileOffsetX);
        Assert.Equal(6.2f, fill.TileOffsetY);
        Assert.Equal(5f, fill.TileWidth);
        Assert.Equal(5f, fill.TileHeight);
    }

    [Fact]
    public void AVersionTwoTerrainIsCollidingAndFillLess()
    {
        const string json = """
        {
            "format": "pigforge.level-content",
            "schemaVersion": 2,
            "contentVersion": "terrain-v2",
            "goalZone": { "min": [0, 0, 0], "max": [1, 1, 1] },
            "bounds": { "min": [-10, -10, -10], "max": [10, 10, 10] },
            "spawns": [],
            "terrain": [
                {
                    "position": [0, 0, 0],
                    "depth": 10,
                    "loops": [ [ [0, 0], [4, 0], [4, 3] ] ]
                }
            ]
        }
        """;

        LevelTerrainDefinition terrain = Assert.Single(LevelContentParser.Parse(json).Terrain);
        Assert.True(terrain.Collider);
        Assert.Null(terrain.Fill);
    }

    [Theory]
    // v3 requires both new fields on every terrain, and older versions forbid them.
    [InlineData("""{ "position": [0, 0, 0], "depth": 10, "collider": true, "loops": [[[0,0],[1,0],[1,1]]] }""", 3, "fill")]
    [InlineData("""{ "position": [0, 0, 0], "depth": 10, "fill": { "texture": "a.png", "color": [1,2,3,4], "tileOffset": [0,0], "tileSize": [5,5] }, "loops": [[[0,0],[1,0],[1,1]]] }""", 3, "collider")]
    [InlineData("""{ "position": [0, 0, 0], "depth": 10, "collider": true, "loops": [[[0,0],[1,0],[1,1]]] }""", 2, "collider")]
    [InlineData("""{ "position": [0, 0, 0], "depth": 10, "fill": { "texture": "a.png", "color": [1,2,3,4], "tileOffset": [0,0], "tileSize": [5,5] }, "loops": [[[0,0],[1,0],[1,1]]] }""", 2, "fill")]
    public void TheFillAndColliderAreVersionBound(string terrain, int version, string expected)
    {
        string json = $$"""
        {
            "format": "pigforge.level-content",
            "schemaVersion": {{version}},
            "contentVersion": "versioned-terrain",
            "goalZone": { "min": [0, 0, 0], "max": [1, 1, 1] },
            "bounds": { "min": [-10, -10, -10], "max": [10, 10, 10] },
            "spawns": [],
            "terrain": [ {{terrain}} ]
        }
        """;

        LevelContentException exception = Assert.Throws<LevelContentException>(() => LevelContentParser.Parse(json));
        Assert.Contains(expected, exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("""{ "texture": "", "color": [1,2,3,4], "tileOffset": [0,0], "tileSize": [5,5] }""", "texture")]
    [InlineData("""{ "texture": " ground.png", "color": [1,2,3,4], "tileOffset": [0,0], "tileSize": [5,5] }""", "texture")]
    [InlineData("""{ "texture": "a.png", "color": [1,2,3], "tileOffset": [0,0], "tileSize": [5,5] }""", "color")]
    [InlineData("""{ "texture": "a.png", "color": [1,2,3,256], "tileOffset": [0,0], "tileSize": [5,5] }""", "color[3]")]
    [InlineData("""{ "texture": "a.png", "color": [1,2,3,4], "tileOffset": [0], "tileSize": [5,5] }""", "tileOffset")]
    [InlineData("""{ "texture": "a.png", "color": [1,2,3,4], "tileOffset": [0,0], "tileSize": [5,0] }""", "tileSize")]
    [InlineData("""{ "texture": "a.png", "color": [1,2,3,4], "tileOffset": [0,0] }""", "tileSize")]
    public void AMalformedFillIsRejected(string fill, string expected)
    {
        string json = $$"""
        {
            "format": "pigforge.level-content",
            "schemaVersion": 3,
            "contentVersion": "bad-fill",
            "goalZone": { "min": [0, 0, 0], "max": [1, 1, 1] },
            "bounds": { "min": [-10, -10, -10], "max": [10, 10, 10] },
            "spawns": [],
            "terrain": [
                {
                    "position": [0, 0, 0],
                    "depth": 10,
                    "collider": true,
                    "fill": {{fill}},
                    "loops": [ [ [0, 0], [4, 0], [4, 3] ] ]
                }
            ]
        }
        """;

        LevelContentException exception = Assert.Throws<LevelContentException>(() => LevelContentParser.Parse(json));
        Assert.Contains(expected, exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The extruded mesh must be exactly the original's construction: each loop point duplicated at
    /// z = +-depth/2, consecutive pairs joined by the quad (2i, 2i+1, 2i+2), (2i+2, 2i+1, 2i+3) and
    /// the last point wrapping back onto the first (<c>LevelLoader.cs:339-380</c>).
    /// </summary>
    [Fact]
    public void TheTerrainMeshIsTheOriginalsExtrusion()
    {
        LevelTerrainDefinition terrain = new(
            new PhysicsVector3(1f, 2f, 0f),
            Depth: 10f,
            Loops:
            [
                new[] { new PhysicsVector3(0f, 0f, 0f), new PhysicsVector3(4f, 0f, 0f), new PhysicsVector3(4f, 3f, 0f) },
                new[] { new PhysicsVector3(10f, 0f, 0f), new PhysicsVector3(12f, 0f, 0f), new PhysicsVector3(12f, 1f, 0f) },
            ]);

        TriangleMeshShapeDefinition mesh = LevelTerrainMesh.Build(terrain);

        Assert.Equal(12, mesh.Vertices.Count);
        Assert.Equal(36, mesh.Triangles.Count);
        Assert.Equal(-5f, mesh.Vertices[0].Z);
        Assert.Equal(5f, mesh.Vertices[1].Z);
        Assert.Equal(0f, mesh.Vertices[0].X);
        Assert.Equal(4f, mesh.Vertices[2].X);
        Assert.Equal(10f, mesh.Vertices[6].X);

        // First quad: (0, 1, 2), (2, 1, 3); second quad: (2, 3, 4), (4, 3, 5); third closes the loop
        // back onto vertex 0, exactly as the original's modulo indices do.
        int[] firstLoop = mesh.Triangles.Take(18).ToArray();
        Assert.Equal(new[] { 0, 1, 2, 2, 1, 3, 2, 3, 4, 4, 3, 5, 4, 5, 0, 0, 5, 1 }, firstLoop);
    }

    [Fact]
    public void AnEmptyTerrainLoopIsRejectedByTheMeshBuilder()
    {
        LevelTerrainDefinition terrain = new(
            new PhysicsVector3(0f, 0f, 0f),
            Depth: 10f,
            Loops: [Array.Empty<PhysicsVector3>()]);

        Assert.Throws<ArgumentException>(() => LevelTerrainMesh.Build(terrain));
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
