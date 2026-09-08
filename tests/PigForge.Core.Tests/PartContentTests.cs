using PigForge.Core.Content;
using PigForge.Physics.Abstractions;
using Xunit.Sdk;

namespace PigForge.Core.Tests;

public sealed class PartContentTests
{
    [Fact]
    public void SampleContentFromRepositoryLoads()
    {
        string path = FindRepositoryFile("content/parts.json");

        PartContentLibrary library = PartContentLibrary.Load(path);

        Assert.Equal("pigforge-base-content-v1", library.Document.ContentVersion);
        Assert.Equal(27, library.Document.Parts.Count);
        Assert.NotNull(library.GetPart(1));
    }

    [Fact]
    public void ValidDocumentParsesWithShapesAndModes()
    {
        PartContentDocument document = PartContentParser.Parse("""
        {
            "format": "pigforge.part-content",
            "schemaVersion": 1,
            "contentVersion": "test-content-v1",
            "parts": [
                {
                    "partTypeId": 1,
                    "name": "block",
                    "mode": "dynamic",
                    "mass": 1.5,
                    "shapes": [ { "kind": "box", "halfExtents": [0.5, 0.5, 0.5] } ]
                },
                {
                    "partTypeId": 2,
                    "name": "slab",
                    "mode": "static",
                    "mass": 0,
                    "shapes": [ { "kind": "sphere", "radius": 1 } ]
                }
            ]
        }
        """);

        Assert.Equal(2, document.Parts.Count);
        Assert.Equal(PhysicsBodyMode.Dynamic, document.Parts[0].Mode);
        Assert.Equal(1.5f, document.Parts[0].Mass);
        Assert.Equal(PhysicsShapeKind.Box, document.Parts[0].Shapes[0].Kind);
        Assert.Equal(PhysicsShapeKind.Sphere, document.Parts[1].Shapes[0].Kind);
    }

    [Theory]
    [InlineData("engineAssetGuid", "unknown property")]
    [InlineData("schemaVersion", "only version 1")]
    [InlineData("contentVersion", "")]
    public void RootViolationsAreRejected(string mutatedProperty, string expectedErrorFragment)
    {
        string json = """
        {
            "format": "pigforge.part-content",
            "schemaVersion": 1,
            "contentVersion": "test-content-v1",
            "parts": [
                { "partTypeId": 1, "name": "block", "mode": "dynamic", "mass": 1, "shapes": [ { "kind": "box", "halfExtents": [1, 1, 1] } ] }
            ]
        }
        """;

        string mutated = mutatedProperty switch
        {
            "engineAssetGuid" => json.Replace("\"parts\":", "\"unityAssetGuid\": \"abc123\", \"parts\":"),
            "schemaVersion" => json.Replace("\"schemaVersion\": 1", "\"schemaVersion\": 2"),
            _ => json.Replace("\"contentVersion\": \"test-content-v1\"", "\"contentVersion\": \"\"")
        };

        PartContentException exception = Assert.Throws<PartContentException>(() => PartContentParser.Parse(mutated));
        Assert.Contains(exception.Errors, error => error.Contains(expectedErrorFragment, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void EngineAssetGuidPropertyIsRejectedExplicitly()
    {
        string json = """
        {
            "format": "pigforge.part-content",
            "schemaVersion": 1,
            "contentVersion": "test-content-v1",
            "parts": [
                {
                    "partTypeId": 1,
                    "name": "block",
                    "mode": "dynamic",
                    "mass": 1,
                    "partPrefabGuid": "0123456789abcdef0123456789abcdef",
                    "shapes": [ { "kind": "box", "halfExtents": [1, 1, 1] } ]
                }
            ]
        }
        """;

        PartContentException exception = Assert.Throws<PartContentException>(() => PartContentParser.Parse(json));

        Assert.Contains(exception.Errors, error => error.Contains("engine asset reference", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void InvalidPartsAreRejectedBeforeStartup()
    {
        AssertRejected(
            """{ "partTypeId": 1, "name": "block", "mode": "dynamic", "mass": 0, "shapes": [ { "kind": "box", "halfExtents": [1, 1, 1] } ] }""",
            "positive mass");
        AssertRejected(
            """{ "partTypeId": 1, "name": "block", "mode": "static", "mass": 3, "shapes": [ { "kind": "box", "halfExtents": [1, 1, 1] } ] }""",
            "mass of zero");
        AssertRejected(
            """{ "partTypeId": 1, "name": "block", "mode": "dynamic", "mass": 1, "shapes": [] }""",
            "non-empty array");
        AssertRejected(
            """{ "partTypeId": 1, "name": "block", "mode": "dynamic", "mass": 1, "shapes": [ { "kind": "box" } ] }""",
            "halfExtents");
        AssertRejected(
            """{ "partTypeId": 1, "name": "block", "mode": "dynamic", "mass": 1, "shapes": [ { "kind": "sphere", "radius": 1, "halfExtents": [1, 1, 1] } ] }""",
            "not valid for kind 'sphere'");
        AssertRejected(
            """{ "partTypeId": 0, "name": "block", "mode": "dynamic", "mass": 1, "shapes": [ { "kind": "box", "halfExtents": [1, 1, 1] } ] }""",
            "positive 32-bit integer");
        AssertRejected(
            """{ "partTypeId": 1, "name": "", "mode": "dynamic", "mass": 1, "shapes": [ { "kind": "box", "halfExtents": [1, 1, 1] } ] }""",
            "1 to 64 characters");
        AssertRejected(
            """{ "partTypeId": 1, "name": "mesh", "mode": "dynamic", "mass": 1, "shapes": [ { "kind": "convexMesh", "vertices": [[0,0,0],[1,0,0],[0,1,0]] } ] }""",
            "at least four vertices");
    }

    [Fact]
    public void DuplicatePartTypeIdsAreRejected()
    {
        string json = """
        {
            "format": "pigforge.part-content",
            "schemaVersion": 1,
            "contentVersion": "test-content-v1",
            "parts": [
                { "partTypeId": 1, "name": "a", "mode": "dynamic", "mass": 1, "shapes": [ { "kind": "box", "halfExtents": [1, 1, 1] } ] },
                { "partTypeId": 1, "name": "b", "mode": "dynamic", "mass": 2, "shapes": [ { "kind": "box", "halfExtents": [2, 2, 2] } ] }
            ]
        }
        """;

        PartContentException exception = Assert.Throws<PartContentException>(() => PartContentParser.Parse(json));

        Assert.Contains(exception.Errors, error => error.Contains("more than once", StringComparison.Ordinal));
    }

    [Fact]
    public void ParserReportsAllErrorsInOnePass()
    {
        string json = """
        {
            "format": "pigforge.part-content",
            "schemaVersion": 9,
            "contentVersion": "test-content-v1",
            "parts": [
                { "partTypeId": 1, "name": "a", "mode": "dynamic", "mass": 0, "shapes": [ { "kind": "box", "halfExtents": [1, 1, 1] } ] }
            ]
        }
        """;

        PartContentException exception = Assert.Throws<PartContentException>(() => PartContentParser.Parse(json));

        Assert.True(exception.Errors.Count >= 2, "Expected the schema version and mass violations in one report.");
    }

    [Fact]
    public void LibraryMapsPartToBodyDefinition()
    {
        PartContentLibrary library = new(PartContentParser.Parse(SinglePartJson));

        BodyDefinition definition = library.CreateBodyDefinition(
            1,
            new PhysicsVector3(0, 4, 0),
            PhysicsQuaternion.Identity,
            linearVelocity: new PhysicsVector3(0, 1, 0));

        Assert.Equal(PhysicsBodyMode.Dynamic, definition.Mode);
        Assert.Equal(2f, definition.Mass);
        Assert.Equal(new PhysicsVector3(0, 4, 0), definition.Position);
        BoxShapeDefinition box = Assert.IsType<BoxShapeDefinition>(Assert.Single(definition.Shapes));
        Assert.Equal(0.5f, box.HalfExtentX);
    }

    [Fact]
    public void LibraryRejectsUnknownPartStaticVelocityAndUnsupportedShapeKinds()
    {
        PartContentLibrary library = new(PartContentParser.Parse("""
        {
            "format": "pigforge.part-content",
            "schemaVersion": 1,
            "contentVersion": "test-content-v1",
            "parts": [
                { "partTypeId": 1, "name": "block", "mode": "dynamic", "mass": 2, "shapes": [ { "kind": "box", "halfExtents": [0.5, 0.5, 0.5] } ] },
                { "partTypeId": 2, "name": "slab", "mode": "static", "mass": 0, "shapes": [ { "kind": "box", "halfExtents": [10, 0.5, 10] } ] },
                { "partTypeId": 3, "name": "ball", "mode": "dynamic", "mass": 1, "shapes": [ { "kind": "sphere", "radius": 0.4 } ] }
            ]
        }
        """));

        Assert.Throws<KeyNotFoundException>(() => library.GetPart(99));
        Assert.Throws<ArgumentException>(() => library.CreateBodyDefinition(
            2,
            PhysicsVector3.Zero,
            PhysicsQuaternion.Identity,
            linearVelocity: new PhysicsVector3(1, 0, 0)));
        BodyDefinition ball = library.CreateBodyDefinition(3, PhysicsVector3.Zero, PhysicsQuaternion.Identity);
        SphereShapeDefinition sphere = Assert.IsType<SphereShapeDefinition>(Assert.Single(ball.Shapes));
        Assert.Equal(0.4f, sphere.Radius);
    }

    private const string SinglePartJson = """
    {
        "format": "pigforge.part-content",
        "schemaVersion": 1,
        "contentVersion": "test-content-v1",
        "parts": [
            { "partTypeId": 1, "name": "block", "mode": "dynamic", "mass": 2, "shapes": [ { "kind": "box", "halfExtents": [0.5, 0.5, 0.5] } ] }
        ]
    }
    """;

    private static void AssertRejected(string partJson, string expectedErrorFragment)
    {
        string json = $$"""
        {
            "format": "pigforge.part-content",
            "schemaVersion": 1,
            "contentVersion": "test-content-v1",
            "parts": [ {{partJson}} ]
        }
        """;

        PartContentException exception = Assert.Throws<PartContentException>(() => PartContentParser.Parse(json));

        Assert.Contains(exception.Errors, error => error.Contains(expectedErrorFragment, StringComparison.OrdinalIgnoreCase));
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
