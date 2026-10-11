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
            "schemaVersion": 11,
            "contentVersion": "future",
            "goalZone": { "min": [0, 0, 0], "max": [1, 1, 1] },
            "bounds": { "min": [-10, -10, -10], "max": [10, 10, 10] },
            "cameraLimits": { "topLeft": [0, 0], "size": [1, 1] },
            "spawns": []
        }
        """;

        LevelContentException exception = Assert.Throws<LevelContentException>(() => LevelContentParser.Parse(json));
        Assert.Contains("root.schemaVersion", exception.Message, StringComparison.Ordinal);
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

    /// <summary>
    /// v4 adds the edge trim: the original's `_curve` mesh (`nodes` on the terrain, `stripe` offset
    /// outwards) and the `e2d/Curve` inputs `LevelLoader.ReadTerrain` restores -- the two layer
    /// textures with their own wrap modes, the shader's u scale and the node runs that take the second
    /// layer (the control texture's green channel); see docs/specs/level-terrain-visuals.md.
    /// </summary>
    [Fact]
    public void AVersionFourLevelCarriesTheEdgeTrim()
    {
        const string json = """
        {
            "format": "pigforge.level-content",
            "schemaVersion": 4,
            "contentVersion": "terrain-v4",
            "goalZone": { "min": [0, 0, 0], "max": [1, 1, 1] },
            "bounds": { "min": [-10, -10, -10], "max": [10, 10, 10] },
            "spawns": [],
            "terrain": [
                {
                    "position": [-2.7907727, 9.021405, 0],
                    "depth": 10,
                    "collider": true,
                    "fill": {
                        "texture": "Ground_Rocks_Texture.png",
                        "color": [255, 255, 255, 255],
                        "tileOffset": [0, 6.2],
                        "tileSize": [5, 5]
                    },
                    "curve": {
                        "textures": [
                            { "texture": "Ground_Rocks_Texture.png", "wrap": "clamp" },
                            { "texture": "Ground_Rocks_Outline_Texture.png", "wrap": "repeat" }
                        ],
                        "uScale": 10,
                        "splat1": [[1, 2]],
                        "nodes": [[0, 0], [1, 0], [2, 3]],
                        "stripe": [[0, 0.1], [1, 0.1], [2, 3.1]]
                    },
                    "loops": [ [ [0, 0], [4, 0], [4, 3] ] ]
                }
            ]
        }
        """;

        LevelTerrainDefinition terrain = Assert.Single(LevelContentParser.Parse(json).Terrain);
        LevelCurveDefinition curve = Assert.IsType<LevelCurveDefinition>(terrain.Curve);
        Assert.Equal(3, curve.Nodes.Count);
        Assert.Equal(3, curve.Stripe.Count);
        Assert.Equal(2f, curve.Nodes[2].X);
        Assert.Equal(3.1f, curve.Stripe[2].Y);
        Assert.Equal(0f, curve.Nodes[0].Z);
        Assert.Equal(10f, curve.UScale);
        Assert.Equal(2, curve.Textures.Count);
        Assert.Equal("Ground_Rocks_Texture.png", curve.Textures[0].Texture);
        Assert.Equal(LevelCurveWrap.Clamp, curve.Textures[0].Wrap);
        Assert.Equal("Ground_Rocks_Outline_Texture.png", curve.Textures[1].Texture);
        Assert.Equal(LevelCurveWrap.Repeat, curve.Textures[1].Wrap);
        LevelCurveRun run = Assert.Single(curve.Splat1);
        Assert.Equal(1, run.Start);
        Assert.Equal(2, run.Count);
    }

    [Theory]
    // v4 requires the trim on every terrain, and older versions forbid it.
    [InlineData("""{ "position": [0, 0, 0], "depth": 10, "collider": true, "fill": { "texture": "a.png", "color": [1,2,3,4], "tileOffset": [0,0], "tileSize": [5,5] }, "loops": [[[0,0],[1,0],[1,1]]] }""", 4, "curve")]
    [InlineData("""{ "position": [0, 0, 0], "depth": 10, "collider": true, "fill": { "texture": "a.png", "color": [1,2,3,4], "tileOffset": [0,0], "tileSize": [5,5] }, "curve": { "textures": [ { "texture": "a.png", "wrap": "repeat" }, { "texture": "b.png", "wrap": "repeat" } ], "uScale": 1, "splat1": [], "nodes": [[0,0],[1,0]], "stripe": [[0,1],[1,1]] }, "loops": [[[0,0],[1,0],[1,1]]] }""", 3, "curve")]
    public void TheEdgeTrimIsVersionBound(string terrain, int version, string expected)
    {
        string json = $$"""
        {
            "format": "pigforge.level-content",
            "schemaVersion": {{version}},
            "contentVersion": "versioned-trim",
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
    [InlineData("""{ "textures": [ { "texture": "a.png", "wrap": "repeat" }, { "texture": "b.png", "wrap": "repeat" } ], "uScale": 1, "splat1": [], "nodes": [[0,0],[1,0]], "stripe": [[0,1]] }""", "stripe")]
    [InlineData("""{ "textures": [ { "texture": "a.png", "wrap": "repeat" }, { "texture": "b.png", "wrap": "repeat" } ], "uScale": 1, "splat1": [], "nodes": [[0,0]], "stripe": [[0,1]] }""", "at least 2")]
    [InlineData("""{ "textures": [ { "texture": "a.png", "wrap": "repeat" } ], "uScale": 1, "splat1": [], "nodes": [[0,0],[1,0]], "stripe": [[0,1],[1,1]] }""", "textures")]
    [InlineData("""{ "textures": [ { "texture": "a.png", "wrap": "tile" }, { "texture": "b.png", "wrap": "repeat" } ], "uScale": 1, "splat1": [], "nodes": [[0,0],[1,0]], "stripe": [[0,1],[1,1]] }""", "wrap")]
    [InlineData("""{ "textures": [ { "texture": "a.png", "wrap": "repeat" }, { "texture": "b.png", "wrap": "repeat" } ], "uScale": 0, "splat1": [], "nodes": [[0,0],[1,0]], "stripe": [[0,1],[1,1]] }""", "uScale")]
    [InlineData("""{ "textures": [ { "texture": "a.png", "wrap": "repeat" }, { "texture": "b.png", "wrap": "repeat" } ], "uScale": 1, "splat1": [[2,2]], "nodes": [[0,0],[1,0],[2,0]], "stripe": [[0,1],[1,1],[2,1]] }""", "splat1")]
    [InlineData("""{ "textures": [ { "texture": "a.png", "wrap": "repeat" }, { "texture": "b.png", "wrap": "repeat" } ], "uScale": 1, "splat1": [[1,2],[2,1]], "nodes": [[0,0],[1,0],[2,0],[3,0]], "stripe": [[0,1],[1,1],[2,1],[3,1]] }""", "sorted")]
    [InlineData("""{ "textures": [ { "texture": "a.png", "wrap": "repeat" }, { "texture": "b.png", "wrap": "repeat" } ], "uScale": 1, "splat1": [[0,0]], "nodes": [[0,0],[1,0]], "stripe": [[0,1],[1,1]] }""", "count")]
    public void AMalformedCurveIsRejected(string curve, string expected)
    {
        string json = $$"""
        {
            "format": "pigforge.level-content",
            "schemaVersion": 4,
            "contentVersion": "bad-curve",
            "goalZone": { "min": [0, 0, 0], "max": [1, 1, 1] },
            "bounds": { "min": [-10, -10, -10], "max": [10, 10, 10] },
            "spawns": [],
            "terrain": [
                {
                    "position": [0, 0, 0],
                    "depth": 10,
                    "collider": true,
                    "fill": { "texture": "a.png", "color": [1,2,3,4], "tileOffset": [0,0], "tileSize": [5,5] },
                    "curve": {{curve}},
                    "loops": [ [ [0, 0], [4, 0], [4, 3] ] ]
                }
            ]
        }
        """;

        LevelContentException exception = Assert.Throws<LevelContentException>(() => LevelContentParser.Parse(json));
        Assert.Contains(expected, exception.Message, StringComparison.OrdinalIgnoreCase);
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

    /// <summary>
    /// v5 adds the level's own camera rectangle, read out of the level file's `PrefabOverrides`
    /// (`LevelManager.m_cameraLimits`): `topLeft` is its top-left corner and `size` extends right and
    /// down. It is the pig's bound -- the original sends `PigOutOfBounds` when a pig leaves it and
    /// answers by returning to the building state (`Pig.cs:396-403`, `GameMode.cs:384-387`).
    /// </summary>
    [Fact]
    public void AVersionFiveLevelCarriesItsOwnCameraLimits()
    {
        const string json = """
        {
            "format": "pigforge.level-content",
            "schemaVersion": 5,
            "contentVersion": "terrain-v5",
            "goalZone": { "min": [0, 0, 0], "max": [1, 1, 1] },
            "bounds": { "min": [-10, -10, -10], "max": [10, 10, 10] },
            "cameraLimits": { "topLeft": [-10.46, 13.45], "size": [56.3, 24.7] },
            "spawns": [],
            "terrain": [
                {
                    "position": [-2.7907727, 9.021405, 0],
                    "depth": 10,
                    "collider": true,
                    "fill": {
                        "texture": "Ground_Rocks_Texture.png",
                        "color": [255, 255, 255, 255],
                        "tileOffset": [0, 6.2],
                        "tileSize": [5, 5]
                    },
                    "curve": {
                        "textures": [
                            { "texture": "Ground_Rocks_Texture.png", "wrap": "clamp" },
                            { "texture": "Ground_Rocks_Outline_Texture.png", "wrap": "repeat" }
                        ],
                        "uScale": 10,
                        "splat1": [[1, 2]],
                        "nodes": [[0, 0], [1, 0], [2, 3]],
                        "stripe": [[0, 0.1], [1, 0.1], [2, 3.1]]
                    },
                    "loops": [ [ [0, 0], [4, 0], [4, 3] ] ]
                }
            ]
        }
        """;

        LevelContentDocument level = LevelContentParser.Parse(json);

        CameraLimits limits = Assert.IsType<CameraLimits>(level.CameraLimits);
        Assert.Equal(-10.46f, limits.TopLeftX);
        Assert.Equal(13.45f, limits.TopLeftY);
        Assert.Equal(56.3f, limits.SizeX);
        Assert.Equal(24.7f, limits.SizeY);
        // The converted pack carries the rectangle the original's own level file does: Level_05's
        // `LevelManager` override reads topLeft(-10.46, 13.45) / size(56.3, 24.7).
        LevelContentDocument real =
            LevelContentLibrary.Parse(File.ReadAllText(FindRepositoryFile("content/levels/original/episode_1_levels/Level_05.json")));
        Assert.Equal(new CameraLimits(-10.46f, 13.45f, 56.3f, 24.7f), real.CameraLimits);
    }

    [Theory]
    // v5 requires the block at the root, and older versions forbid it.
    [InlineData("", 5, "cameraLimits")]
    [InlineData("""{ "topLeft": [0, 0], "size": [1, 1] }""", 4, "cameraLimits")]
    [InlineData("""3""", 5, "cameraLimits")]
    [InlineData("""{ "topLeft": [0, 0] }""", 5, "size")]
    [InlineData("""{ "topLeft": [0], "size": [1, 1] }""", 5, "topLeft")]
    [InlineData("""{ "topLeft": [0, 0], "size": [1, 1], "extra": 1 }""", 5, "extra")]
    [InlineData("""{ "topLeft": [0, 0], "size": [0, 1] }""", 5, "size")]
    public void TheCameraLimitsAreVersionBoundAndWellFormed(string cameraLimits, int version, string expected)
    {
        string limitsLine = cameraLimits.Length == 0 ? string.Empty : $$""" "cameraLimits": {{cameraLimits}}, """;
        string json = $$"""
        {
            "format": "pigforge.level-content",
            "schemaVersion": {{version}},
            "contentVersion": "versioned-camera-limits",
            "goalZone": { "min": [0, 0, 0], "max": [1, 1, 1] },
            "bounds": { "min": [-10, -10, -10], "max": [10, 10, 10] },
            {{limitsLine}}
            "spawns": []
        }
        """;

        LevelContentException exception = Assert.Throws<LevelContentException>(() => LevelContentParser.Parse(json));
        Assert.Contains(expected, exception.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// v6 places the level's decoration instances (`docs/specs/level-props.md`). The original's own
    /// props of this family are single sprite quads -- no collider, no behaviour -- so the client draws
    /// them and the room only relays the document; the parser still validates every field, and the
    /// values are the level file's own transform (`rotation` is `euler.z` in degrees, as radians, and
    /// a negative `scaleX` is the original's mirroring).
    /// </summary>
    [Fact]
    public void AVersionSixLevelPlacesItsDecorations()
    {
        const string json = """
        {
            "format": "pigforge.level-content",
            "schemaVersion": 6,
            "contentVersion": "props-v6",
            "goalZone": { "min": [0, 0, 0], "max": [1, 1, 1] },
            "bounds": { "min": [-10, -10, -10], "max": [10, 10, 10] },
            "cameraLimits": { "topLeft": [-10.46, 13.45], "size": [56.3, 24.7] },
            "spawns": [],
            "props": [
                { "id": "Star_01", "x": 37.530018, "y": -7.0505567, "z": -5, "rotation": 0, "scaleX": -1, "scaleY": 1 },
                { "id": "Grass_06", "x": 8.645143, "y": 4.4807925, "z": -5, "rotation": 6.060668, "scaleX": 1, "scaleY": 1 }
            ],
            "terrain": []
        }
        """;

        LevelContentDocument level = LevelContentParser.Parse(json);

        Assert.Equal((ushort)10, LevelContentDocument.SchemaVersion);
        Assert.Empty(level.Terrain);
        Assert.Collection(
            level.Props,
            prop =>
            {
                Assert.Equal("Star_01", prop.Id);
                Assert.Equal(37.530018f, prop.X);
                Assert.Equal(-7.0505567f, prop.Y);
                Assert.Equal(-5f, prop.Z);
                Assert.Equal(0f, prop.Rotation);
                Assert.Equal(-1f, prop.ScaleX);
                Assert.Equal(1f, prop.ScaleY);
            },
            prop =>
            {
                Assert.Equal("Grass_06", prop.Id);
                Assert.Equal(6.060668f, prop.Rotation);
            });

        // The converted pack carries them: the 321 drawable props are placed 18204 times across the
        // 277 levels (the cloud sets' own 1020 clouds and the 218 goal areas among them), and Level_05's
        // own first instance is its jungle backdrop at the origin.
        LevelContentDocument real =
            LevelContentLibrary.Parse(File.ReadAllText(FindRepositoryFile("content/levels/original/episode_1_levels/Level_05.json")));
        Assert.Equal(30, real.Props.Count);
        Assert.Equal(new LevelPropDefinition("background_jungle_01_set", 0f, 0f, 0f, 0f, 1f, 1f), real.Props[0]);
        Assert.Contains(real.Props, prop => prop.Id == "Star_01");

        // Its cloud set is one of the 152 the pack places, and its 8 clouds are their own instances --
        // in world coordinates, so the set's own scale never reaches them.
        Assert.Equal(8, real.Props.Count(prop => prop.Id.StartsWith("Background_Cloud", StringComparison.Ordinal)));
        Assert.All(real.Props, prop => Assert.False(string.IsNullOrWhiteSpace(prop.Id)));

        // This level is one of the 123 that declares v7: its three TNT crates are solid, and the
        // decoration beside them is not.
        Assert.Equal(3, real.Props.Count(prop => prop.Id == "TNT_Box"));
        Assert.All(real.Props.Where(prop => prop.Id == "TNT_Box"), prop => Assert.Single(prop.Boxes));
        Assert.All(real.Props.Where(prop => prop.Id != "TNT_Box"), prop => Assert.Empty(prop.Boxes));
    }

    /// <summary>
    /// v7 gives a placed prop its own solid boxes: the original's non-trigger box colliders, in the
    /// prop's own frame (`docs/specs/level-props.md` §3.6). A decoration carries none, a v6 document
    /// may not carry them at all, and a box with a non-positive size is not a shape.
    /// </summary>
    [Fact]
    public void AVersionSevenLevelGivesItsPropsSolidBoxes()
    {
        const string json = """
        {
            "format": "pigforge.level-content",
            "schemaVersion": 7,
            "contentVersion": "props-v7",
            "goalZone": { "min": [0, 0, 0], "max": [1, 1, 1] },
            "bounds": { "min": [-10, -10, -10], "max": [10, 10, 10] },
            "cameraLimits": { "topLeft": [-10.46, 13.45], "size": [56.3, 24.7] },
            "spawns": [],
            "props": [
                {
                    "id": "TNT_Box", "x": -43.76694, "y": 8.584375, "z": 0, "rotation": 0.14654502, "scaleX": 1, "scaleY": 1,
                    "boxes": [ { "dx": 0, "dy": 0, "dz": 0, "sizeX": 0.95, "sizeY": 0.95, "sizeZ": 0.95, "rotation": 0 } ]
                },
                { "id": "Grass_06", "x": 8.645143, "y": 4.4807925, "z": -5, "rotation": 6.060668, "scaleX": 1, "scaleY": 1 }
            ],
            "terrain": []
        }
        """;

        LevelContentDocument level = LevelContentParser.Parse(json);

        Assert.Equal((ushort)10, LevelContentDocument.SchemaVersion);
        Assert.Collection(
            level.Props,
            prop =>
            {
                Assert.Equal("TNT_Box", prop.Id);
                Assert.Equal(0.14654502f, prop.Rotation);
                LevelPropBoxDefinition box = Assert.Single(prop.Boxes);
                Assert.Equal(0f, box.X);
                Assert.Equal(0f, box.Y);
                Assert.Equal(0f, box.Z);
                Assert.Equal(0.95f, box.SizeX);
                Assert.Equal(0.95f, box.SizeY);
                Assert.Equal(0.95f, box.SizeZ);
                Assert.Equal(0f, box.Rotation);
            },
            prop =>
            {
                Assert.Equal("Grass_06", prop.Id);
                Assert.Empty(prop.Boxes);
            });

        // The converted pack carries them: 671 solid boxes across the 126 levels that place a crate, a
        // wall, a plank, a door or a challenge box, and every box is a shape.
        int boxes = 0;
        int levelsWithBoxes = 0;
        foreach (string file in Directory.EnumerateFiles(FindRepositoryDirectory("content/levels/original"), "*.json", SearchOption.AllDirectories))
        {
            LevelContentDocument document = LevelContentLibrary.Parse(File.ReadAllText(file));
            int here = document.Props.Sum(prop => prop.Boxes.Count);
            boxes += here;
            levelsWithBoxes += here > 0 ? 1 : 0;
            Assert.All(
                document.Props.SelectMany(prop => prop.Boxes),
                box => Assert.True(box.SizeX > 0f && box.SizeY > 0f && box.SizeZ > 0f, $"{file}: a box with a non-positive size"));
        }

        Assert.Equal(671, boxes);
        Assert.Equal(126, levelsWithBoxes);
    }

    /// <summary>
    /// v8 gives a placed prop its own trigger boxes: the original's trigger colliders, which the room
    /// overlaps with the pig to collect a challenge (`docs/specs/level-challenges.md` §3). They are a
    /// separate list from `boxes` -- a wall is built as a body, a marker is not.
    /// </summary>
    [Fact]
    public void AVersionEightLevelGivesItsPropsTriggerBoxes()
    {
        const string json = """
        {
            "format": "pigforge.level-content",
            "schemaVersion": 8,
            "contentVersion": "props-v8",
            "goalZone": { "min": [0, 0, 0], "max": [1, 1, 1] },
            "bounds": { "min": [-10, -10, -10], "max": [10, 10, 10] },
            "cameraLimits": { "topLeft": [-10.46, 13.45], "size": [56.3, 24.7] },
            "spawns": [],
            "props": [
                {
                    "id": "BoxChallenge", "x": 4, "y": 2, "z": 0, "rotation": 0, "scaleX": 1, "scaleY": 1,
                    "triggers": [ { "dx": 0, "dy": 0, "dz": 0, "sizeX": 1.328125, "sizeY": 1.328125, "sizeZ": 1, "rotation": 0 } ]
                },
                { "id": "Grass_06", "x": 0, "y": 0, "z": -5, "rotation": 0, "scaleX": 1, "scaleY": 1 }
            ],
            "terrain": []
        }
        """;

        LevelContentDocument level = LevelContentParser.Parse(json);

        LevelPropDefinition prop = level.Props[0];
        Assert.Equal("BoxChallenge", prop.Id);
        Assert.Empty(prop.Boxes);
        LevelPropBoxDefinition trigger = Assert.Single(prop.Triggers);
        Assert.Equal(1.328125f, trigger.SizeX);

        // The markers the rules test: one zone per trigger, centred on the prop's own pose.
        IReadOnlyList<ChallengeTriggerDefinition> triggers = LevelChallengeTriggers.Build(level);
        ChallengeTriggerDefinition zone = Assert.Single(triggers);
        Assert.Equal(0, zone.PropIndex);
        Assert.Equal(4f - (1.328125f / 2f), zone.Zone.Min.X, precision: 3);
        Assert.Equal(2f - (1.328125f / 2f), zone.Zone.Min.Y, precision: 3);
        Assert.Equal(4f + (1.328125f / 2f), zone.Zone.Max.X, precision: 3);
        Assert.True(zone.Zone.Contains(new PhysicsVector3(4f, 2f, 0f)));
        Assert.False(zone.Zone.Contains(new PhysicsVector3(4f, 4f, 0f)));

        // The converted pack carries them: 948 props across 277 levels, every box a real shape.
        int triggerProps = 0;
        foreach (string file in Directory.EnumerateFiles(FindRepositoryDirectory("content/levels/original"), "*.json", SearchOption.AllDirectories))
        {
            LevelContentDocument document = LevelContentLibrary.Parse(File.ReadAllText(file));
            triggerProps += document.Props.Count(candidate => candidate.Triggers.Count > 0);
            Assert.All(
                document.Props.SelectMany(candidate => candidate.Triggers),
                box => Assert.True(
                    box.SizeX > 0f && box.SizeY > 0f && box.SizeZ >= 0f,
                    $"{file}: a trigger with a non-positive size ({box.SizeX}, {box.SizeY}, {box.SizeZ})"));
        }

        Assert.Equal(948, triggerProps);
    }

    /// <summary>
    /// v9 carries the level's own construction limits: the build grid `LevelManager.CanPlacePartAtGridCell`
    /// walks (`LevelManager.cs:1280-1297`), anchored at the level's `LevelStart` instance, and the
    /// per-part-type quota `BaseGameMode.GetPartCount` answers (`BaseGameMode.cs:558-578`). A v9 document
    /// may carry none -- the sandbox levels whose own grid has no placeable cell do -- and a v1-v8
    /// document must not carry one.
    /// </summary>
    [Fact]
    public void AVersionNineLevelCarriesItsConstructionLimits()
    {
        const string json = """
        {
            "format": "pigforge.level-content",
            "schemaVersion": 9,
            "contentVersion": "build-v9",
            "goalZone": { "min": [0, 0, 0], "max": [1, 1, 1] },
            "bounds": { "min": [-10, -10, -10], "max": [10, 10, 10] },
            "cameraLimits": { "topLeft": [-10.46, 13.45], "size": [56.3, 24.7] },
            "spawns": [],
            "props": [],
            "terrain": [],
            "build": {
                "origin": [2.4379761, 5.4575901],
                "rows": [15, 15, 0],
                "partCounts": [ { "partTypeId": 1, "count": 3 }, { "partTypeId": 4, "count": 1 } ]
            }
        }
        """;

        LevelContentDocument level = LevelContentParser.Parse(json);

        LevelBuildLimits build = Assert.IsType<LevelBuildLimits>(level.Build);
        Assert.Equal(2.4379761f, build.OriginX);
        Assert.Equal(5.4575901f, build.OriginY);
        int[] expectedRows = [15, 15, 0];
        Assert.Equal(expectedRows, build.Rows);
        Assert.Collection(
            build.PartCounts,
            allowance =>
            {
                Assert.Equal(1u, allowance.PartTypeId);
                Assert.Equal(3u, allowance.Count);
            },
            allowance =>
            {
                Assert.Equal(4u, allowance.PartTypeId);
                Assert.Equal(1u, allowance.Count);
            });

        // A v9 document without one poses no construction limit: the sandbox levels' own grid has no
        // placeable cell, so they carry no `build` at all.
        LevelContentDocument unlimited = LevelContentParser.Parse("""
        {
            "format": "pigforge.level-content",
            "schemaVersion": 9,
            "contentVersion": "sandbox-v9",
            "goalZone": { "min": [0, 0, 0], "max": [1, 1, 1] },
            "bounds": { "min": [-10, -10, -10], "max": [10, 10, 10] },
            "cameraLimits": { "topLeft": [0, 0], "size": [1, 1] },
            "spawns": [],
            "props": [],
            "terrain": []
        }
        """);
        Assert.Null(unlimited.Build);
    }

    [Theory]
    // Only a v9 document may carry the block at all.
    [InlineData("""{ "origin": [0, 0], "rows": [1], "partCounts": [] }""", 8, "build")]
    // The origin is exactly two finite numbers, the rows are non-empty non-negative bit masks.
    [InlineData("""{ "origin": [0], "rows": [1], "partCounts": [] }""", 9, "origin")]
    [InlineData("""{ "origin": [0, 0], "rows": [], "partCounts": [] }""", 9, "rows")]
    [InlineData("""{ "origin": [0, 0], "rows": [1, -1], "partCounts": [] }""", 9, "rows")]
    // A quota is a positive base part type with a positive count, and nothing else.
    [InlineData("""{ "origin": [0, 0], "rows": [1], "partCounts": 4 }""", 9, "partCounts")]
    [InlineData("""{ "origin": [0, 0], "rows": [1], "partCounts": [ { "partTypeId": 4, "count": 0 } ] }""", 9, "count")]
    [InlineData("""{ "origin": [0, 0], "rows": [1], "partCounts": [ { "partTypeId": 4, "count": -1 } ] }""", 9, "count")]
    [InlineData("""{ "origin": [0, 0], "rows": [1], "partCounts": [ { "partTypeId": 0, "count": 1 } ] }""", 9, "partTypeId")]
    [InlineData("""{ "origin": [0, 0], "rows": [1], "partCounts": [ { "partTypeId": 4, "count": 1, "extra": 1 } ] }""", 9, "extra")]
    [InlineData("""{ "origin": [0, 0], "rows": [1], "partCounts": [], "extra": 1 }""", 9, "extra")]
    [InlineData("""{ "origin": [0, 0], "origin": [1, 1], "rows": [1], "partCounts": [] }""", 9, "duplicate")]
    public void TheBuildLimitsAreVersionBoundAndWellFormed(string build, int version, string expected)
    {
        string json = $$"""
        {
            "format": "pigforge.level-content",
            "schemaVersion": {{version}},
            "contentVersion": "versioned-build",
            "goalZone": { "min": [0, 0, 0], "max": [1, 1, 1] },
            "bounds": { "min": [-10, -10, -10], "max": [10, 10, 10] },
            "cameraLimits": { "topLeft": [-10.46, 13.45], "size": [56.3, 24.7] },
            "spawns": [],
            "props": [],
            "terrain": [],
            "build": {{build}}
        }
        """;

        LevelContentException exception = Assert.Throws<LevelContentException>(() => LevelContentParser.Parse(json));
        Assert.Contains(expected, exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    // v7 lets a prop carry boxes, and the shapes have to be real.
    [InlineData("""[{ "id": "TNT_Box", "x": 0, "y": 0, "z": 0, "rotation": 0, "scaleX": 1, "scaleY": 1, "boxes": 3 }]""", 7, "boxes")]
    [InlineData("""[{ "id": "TNT_Box", "x": 0, "y": 0, "z": 0, "rotation": 0, "scaleX": 1, "scaleY": 1, "boxes": [{ "dx": 0, "dy": 0, "dz": 0, "sizeX": 0, "sizeY": 1, "sizeZ": 1, "rotation": 0 }] }]""", 7, "positive")]
    [InlineData("""[{ "id": "TNT_Box", "x": 0, "y": 0, "z": 0, "rotation": 0, "scaleX": 1, "scaleY": 1, "boxes": [{ "dx": 0, "dy": 0, "dz": 0, "sizeX": 1, "sizeY": 1, "sizeZ": 1 }] }]""", 7, "rotation")]
    [InlineData("""[{ "id": "TNT_Box", "x": 0, "y": 0, "z": 0, "rotation": 0, "scaleX": 1, "scaleY": 1, "boxes": [{ "dx": 0, "dy": 0, "dz": 0, "sizeX": 1, "sizeY": 1, "sizeZ": 1, "rotation": 0, "extra": 1 }] }]""", 7, "extra")]
    [InlineData("""[{ "id": "TNT_Box", "x": 0, "y": 0, "z": 0, "rotation": 0, "scaleX": 1, "scaleY": 1, "boxes": [{ "dx": 0, "dy": 0, "dz": 0, "sizeX": 1, "sizeY": 1, "sizeZ": 1, "rotation": 0 }] }]""", 6, "boxes")]
    public void TheBoxesAreVersionBoundAndWellFormed(string props, int version, string expected)
    {
        string json = $$"""
        {
            "format": "pigforge.level-content",
            "schemaVersion": {{version}},
            "contentVersion": "versioned-boxes",
            "goalZone": { "min": [0, 0, 0], "max": [1, 1, 1] },
            "bounds": { "min": [-10, -10, -10], "max": [10, 10, 10] },
            "cameraLimits": { "topLeft": [0, 0], "size": [1, 1] },
            "spawns": [],
            "props": {{props}},
            "terrain": []
        }
        """;

        LevelContentException exception = Assert.Throws<LevelContentException>(() => LevelContentParser.Parse(json));
        Assert.Contains(expected, exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    // v6 requires the array at the root, and older versions forbid it.
    [InlineData("", 6, "props")]
    [InlineData("""[{ "id": "Grass_01", "x": 0, "y": 0, "z": 0, "rotation": 0, "scaleX": 1, "scaleY": 1 }]""", 5, "props")]
    [InlineData("""5""", 6, "props")]
    [InlineData("""[{ "id": "Grass_01", "x": 0, "y": 0, "z": 0, "rotation": 0, "scaleX": 1 }]""", 6, "scaleY")]
    [InlineData("""[{ "id": "", "x": 0, "y": 0, "z": 0, "rotation": 0, "scaleX": 1, "scaleY": 1 }]""", 6, "id")]
    [InlineData("""[{ "id": "Grass 01", "x": 0, "y": 0, "z": 0, "rotation": 0, "scaleX": 1, "scaleY": 1 }]""", 6, "id")]
    [InlineData("""[{ "id": "Grass_01", "x": null, "y": 0, "z": 0, "rotation": 0, "scaleX": 1, "scaleY": 1 }]""", 6, "x")]
    [InlineData("""[{ "id": "Grass_01", "x": 0, "y": 0, "z": 0, "rotation": 0, "scaleX": 1, "scaleY": 1, "extra": 1 }]""", 6, "extra")]
    public void ThePropsAreVersionBoundAndWellFormed(string props, int version, string expected)
    {
        string propsLine = props.Length == 0 ? string.Empty : $$""" "props": {{props}}, """;
        string json = $$"""
        {
            "format": "pigforge.level-content",
            "schemaVersion": {{version}},
            "contentVersion": "versioned-props",
            "goalZone": { "min": [0, 0, 0], "max": [1, 1, 1] },
            "bounds": { "min": [-10, -10, -10], "max": [10, 10, 10] },
            "cameraLimits": { "topLeft": [-10.46, 13.45], "size": [56.3, 24.7] },
            {{propsLine}}
            "spawns": []
        }
        """;

        LevelContentException exception = Assert.Throws<LevelContentException>(() => LevelContentParser.Parse(json));
        Assert.Contains(expected, exception.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// v10 carries the level's own challenges: the instance registry `Challenge.Awake` fills and
    /// `LevelComplete` takes the second and third star out of (`Challenge.cs:62-81`,
    /// `LevelComplete.cs:422/460`). `challenges[0]` is the second star, `challenges[1]` the third, a
    /// third only ever grants an icon, and two instances may share a name (3 of the 277 original levels
    /// do). Every kind carries exactly the properties it uses, and a v10 document with no challenge at
    /// all (14 of the 277 original levels) carries an empty array.
    /// </summary>
    [Fact]
    public void AVersionTenLevelCarriesItsChallenges()
    {
        const string json = """
        {
            "format": "pigforge.level-content",
            "schemaVersion": 10,
            "contentVersion": "challenges-v10",
            "goalZone": { "min": [12, -0.5, -2], "max": [16, 2.5, 2] },
            "bounds": { "min": [-30, -12, -8], "max": [30, 30, 8] },
            "cameraLimits": { "topLeft": [-10.46, 13.45], "size": [56.3, 24.7] },
            "spawns": [],
            "props": [
                { "id": "Grass_06", "x": 8.645143, "y": 4.4807925, "z": -5, "rotation": 6.060668, "scaleX": 1, "scaleY": 1 },
                { "id": "Box_01", "x": 4, "y": 2, "z": -5, "rotation": 0, "scaleX": 1, "scaleY": 1 }
            ],
            "challenges": [
                { "name": "BoxChallenge", "kind": "box", "propIndex": 1 },
                { "name": "TimeChallenge", "kind": "time", "targetTime": 4 },
                { "name": "PerfectFlightChallenge", "kind": "perfectFlight" },
                { "name": "DontUsePartChallenge", "kind": "dontUseParts", "partTypeId": 32 },
                { "name": "TransportChallenge", "kind": "transport", "partTypeId": 32 },
                { "name": "BoxChallenge", "kind": "box", "propIndex": 0 }
            ]
        }
        """;

        LevelContentDocument level = LevelContentParser.Parse(json);

        Assert.Equal((ushort)10, LevelContentDocument.SchemaVersion);
        Assert.Collection(
            level.Challenges,
            challenge =>
            {
                Assert.Equal("BoxChallenge", challenge.Name);
                Assert.Equal(LevelChallengeKind.Box, challenge.Kind);
                Assert.Equal(1, challenge.PropIndex);
                Assert.Equal(0f, challenge.TargetTime);
                Assert.Equal(0u, challenge.PartTypeId);
            },
            challenge =>
            {
                Assert.Equal("TimeChallenge", challenge.Name);
                Assert.Equal(LevelChallengeKind.Time, challenge.Kind);
                Assert.Equal(-1, challenge.PropIndex);
                Assert.Equal(4f, challenge.TargetTime);
                Assert.Equal(0u, challenge.PartTypeId);
            },
            challenge =>
            {
                Assert.Equal("PerfectFlightChallenge", challenge.Name);
                Assert.Equal(LevelChallengeKind.PerfectFlight, challenge.Kind);
                Assert.Equal(-1, challenge.PropIndex);
                Assert.Equal(0f, challenge.TargetTime);
                Assert.Equal(0u, challenge.PartTypeId);
            },
            challenge =>
            {
                Assert.Equal("DontUsePartChallenge", challenge.Name);
                Assert.Equal(LevelChallengeKind.DontUseParts, challenge.Kind);
                Assert.Equal(-1, challenge.PropIndex);
                Assert.Equal(0f, challenge.TargetTime);
                Assert.Equal(32u, challenge.PartTypeId);
            },
            challenge =>
            {
                Assert.Equal("TransportChallenge", challenge.Name);
                Assert.Equal(LevelChallengeKind.Transport, challenge.Kind);
                Assert.Equal(-1, challenge.PropIndex);
                Assert.Equal(0f, challenge.TargetTime);
                Assert.Equal(32u, challenge.PartTypeId);
            },
            challenge =>
            {
                Assert.Equal("BoxChallenge", challenge.Name);
                Assert.Equal(LevelChallengeKind.Box, challenge.Kind);
                Assert.Equal(0, challenge.PropIndex);
            });

        // Two instances of one level really do share a name (episode_6_level_25 / _32 / _IX), so
        // duplicates are legal and both keep their own place in the numbering order.
        Assert.Equal(2, level.Challenges.Count(challenge => challenge.Name == "BoxChallenge"));

        // A v10 document whose level has no challenge at all carries an empty array.
        LevelContentDocument bare = LevelContentParser.Parse("""
        {
            "format": "pigforge.level-content",
            "schemaVersion": 10,
            "contentVersion": "sandbox-v10",
            "goalZone": { "min": [0, 0, 0], "max": [1, 1, 1] },
            "bounds": { "min": [-10, -10, -10], "max": [10, 10, 10] },
            "cameraLimits": { "topLeft": [0, 0], "size": [1, 1] },
            "spawns": [],
            "props": [],
            "challenges": []
        }
        """);
        Assert.Empty(bare.Challenges);
    }

    [Theory]
    // Only a v10 document may carry the array, and a v10 one must.
    [InlineData("""[{ "name": "BoxChallenge", "kind": "box", "propIndex": 0 }]""", 9, "only a schemaVersion 10")]
    [InlineData("""3""", 10, "must be an array")]
    [InlineData("""[3]""", 10, "must be an object")]
    // `name` and `kind` always, and the name is a non-empty string.
    [InlineData("""[{ "kind": "box", "propIndex": 0 }]""", 10, "name")]
    [InlineData("""[{ "name": "", "kind": "box", "propIndex": 0 }]""", 10, "name")]
    [InlineData("""[{ "name": "BoxChallenge", "propIndex": 0 }]""", 10, "kind")]
    [InlineData("""[{ "name": "BoxChallenge", "kind": "boxes", "propIndex": 0 }]""", 10, "kind")]
    // A box challenge needs its prop's index, inside this document's own props.
    [InlineData("""[{ "name": "BoxChallenge", "kind": "box" }]""", 10, "propIndex")]
    [InlineData("""[{ "name": "BoxChallenge", "kind": "box", "propIndex": -1 }]""", 10, "propIndex")]
    [InlineData("""[{ "name": "BoxChallenge", "kind": "box", "propIndex": 4 }]""", 10, "propIndex")]
    [InlineData("""[{ "name": "BoxChallenge", "kind": "box", "propIndex": 0.5 }]""", 10, "propIndex")]
    // A time challenge needs a finite, non-negative target time.
    [InlineData("""[{ "name": "TimeChallenge", "kind": "time" }]""", 10, "targetTime")]
    [InlineData("""[{ "name": "TimeChallenge", "kind": "time", "targetTime": "4" }]""", 10, "targetTime")]
    [InlineData("""[{ "name": "TimeChallenge", "kind": "time", "targetTime": -1 }]""", 10, "targetTime")]
    // The part challenges need a positive base family id.
    [InlineData("""[{ "name": "DontUsePartChallenge", "kind": "dontUseParts" }]""", 10, "partTypeId")]
    [InlineData("""[{ "name": "DontUsePartChallenge", "kind": "dontUseParts", "partTypeId": 0 }]""", 10, "partTypeId")]
    [InlineData("""[{ "name": "TransportChallenge", "kind": "transport" }]""", 10, "partTypeId")]
    // A property the kind does not use is drift, and so is an unknown one.
    [InlineData("""[{ "name": "TimeChallenge", "kind": "time", "targetTime": 4, "propIndex": 0 }]""", 10, "propIndex")]
    [InlineData("""[{ "name": "PerfectFlightChallenge", "kind": "perfectFlight", "partTypeId": 32 }]""", 10, "partTypeId")]
    [InlineData("""[{ "name": "BoxChallenge", "kind": "box", "propIndex": 0, "extra": 1 }]""", 10, "extra")]
    [InlineData("""[{ "name": "BoxChallenge", "name": "Other", "kind": "box", "propIndex": 0 }]""", 10, "duplicate")]
    public void TheChallengesAreVersionBoundAndWellFormed(string challenges, int version, string expected)
    {
        string json = $$"""
        {
            "format": "pigforge.level-content",
            "schemaVersion": {{version}},
            "contentVersion": "versioned-challenges",
            "goalZone": { "min": [0, 0, 0], "max": [1, 1, 1] },
            "bounds": { "min": [-10, -10, -10], "max": [10, 10, 10] },
            "cameraLimits": { "topLeft": [-10.46, 13.45], "size": [56.3, 24.7] },
            "spawns": [],
            "props": [
                { "id": "Grass_06", "x": 8.645143, "y": 4.4807925, "z": -5, "rotation": 6.060668, "scaleX": 1, "scaleY": 1 },
                { "id": "Box_01", "x": 4, "y": 2, "z": -5, "rotation": 0, "scaleX": 1, "scaleY": 1 }
            ],
            "challenges": {{challenges}}
        }
        """;

        LevelContentException exception = Assert.Throws<LevelContentException>(() => LevelContentParser.Parse(json));
        Assert.Contains(expected, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AMissingChallengesArrayOnAVersionTenDocumentIsRejected()
    {
        const string json = """
        {
            "format": "pigforge.level-content",
            "schemaVersion": 10,
            "contentVersion": "no-challenges",
            "goalZone": { "min": [0, 0, 0], "max": [1, 1, 1] },
            "bounds": { "min": [-10, -10, -10], "max": [10, 10, 10] },
            "cameraLimits": { "topLeft": [-10.46, 13.45], "size": [56.3, 24.7] },
            "spawns": [],
            "props": []
        }
        """;

        LevelContentException exception = Assert.Throws<LevelContentException>(() => LevelContentParser.Parse(json));
        Assert.Contains("root.challenges", exception.Message, StringComparison.Ordinal);
    }

    private static string FindRepositoryDirectory(string relativePath)
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, relativePath)))
        {
            directory = directory.Parent;
        }

        if (directory is null)
        {
            throw new XunitException($"Could not locate {relativePath} above {AppContext.BaseDirectory}.");
        }

        return Path.Combine(directory.FullName, relativePath);
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
