using PigForge.Core.Construction;
using PigForge.Core.Content;
using PigForge.Physics.Abstractions;

namespace PigForge.Core.Tests;

/// <summary>
/// The original's <c>ChangeVisualConnections</c> turned into physics geometry: a conditional
/// attachment marker is solid exactly while the connection state shows it (Rocket.cs:157-160 sets
/// the hidden marker's collider to a trigger), and a glider wing's root collider takes its thin
/// top-only or thick bottom form (Wings.cs:62-71). The server mirrors the client's
/// <c>connectableSides</c>/<c>conditionalSpriteVisible</c> to decide which is which
/// (<see cref="ConnectionShapes"/>); these tests pin the resulting shape set and sizes.
/// </summary>
public sealed class ConditionalShapeTests
{
    private const uint PartFrame = 1; // wooden-block: jointConnectionType source
    private const uint PartRocket = 13; // rocket: attachmentFallback, four 0.25 x 0.14 marker boxes
    private const uint PartGlider = 31; // wooden glider wing: frame rule, body box 1.9 x 0.6122213

    // ---------------------------------------------------------------- Task 6: attachment markers

    [Fact]
    public void AnIsolatedRocketKeepsItsBottomGhostSolid()
    {
        // Rocket.cs:153-156: the bottom marker is also the ghost shown when no other side can
        // connect, so a rocket with no neighbours still carries one solid bracket -- the bottom.
        (ConstructionRules rules, PartContentLibrary content) = CreateRules();
        EntityId rocket = rules.Place(PartRocket, 0f, 0f, 0f, 1f, 0).Entity;

        Assert.Empty(ConnectionShapes.ConnectableSides(rocket, rules, content));

        IReadOnlyList<PartShapeDefinition> shapes = ConnectionShapes.SpawnShapes(rocket, rules, content);
        Assert.Single(shapes, shape => shape.ConditionSide is null);
        Assert.Single(shapes, shape => shape.ConditionSide == "bottom");
        Assert.DoesNotContain(shapes, shape => shape.ConditionSide is "top" or "left" or "right");

        // The body the spawn path builds carries the same two shapes.
        IReadOnlyList<CompoundChild> children = ClusterChildren(rocket, rules, content);
        Assert.Equal(2, children.Count);
        Assert.Contains(children, child => AttachmentExtent(child.Shape));
    }

    [Fact]
    public void ARocketWithATopNeighbourSwapsItsGhostForTheTopMarker()
    {
        // The ghost is not additive: once a real side connects, only that side's marker is solid.
        (ConstructionRules rules, PartContentLibrary content) = CreateRules();
        EntityId rocket = rules.Place(PartRocket, 10f, 0f, 0f, 1f, 0).Entity;
        EntityId above = rules.Place(PartFrame, 10f, 1f, 0f, 1f, 0).Entity;

        Assert.Equal(new[] { LocalSide.Top }, ConnectionShapes.ConnectableSides(rocket, rules, content));

        IReadOnlyList<PartShapeDefinition> shapes = ConnectionShapes.SpawnShapes(rocket, rules, content);
        Assert.Single(shapes, shape => shape.ConditionSide is null);
        Assert.Single(shapes, shape => shape.ConditionSide == "top");
        Assert.DoesNotContain(shapes, shape => shape.ConditionSide == "bottom");

        Assert.Equal(2, ClusterChildren(rocket, rules, content).Count);
        Assert.Contains(above.Value, rules.ConnectionsOf(rocket));
    }

    [Fact]
    public void ATntMarkerFollowsItsOwnSideWithNoGhost()
    {
        // TNT.cs:94-106 has no fallback: with nothing connected, no marker is solid at all. The part
        // declares that rule in content (tools/bple-connections), which is why the plain rule is
        // reachable rather than inferred from the shape kinds.
        PartContentLibrary content = new(PartContentParser.Parse("""
        {
            "format": "pigforge.part-content",
            "schemaVersion": 1,
            "contentVersion": "conditional-plain-test-v1",
            "physics": { "maximumAngularSpeed": 7.0, "damping": { "linear": 0.2, "angular": 0.05 } },
            "parts": [
                { "partTypeId": 1, "name": "frame", "mode": "dynamic", "mass": 1,
                  "capabilities": { "jointConnectionType": "source" },
                  "shapes": [ { "kind": "box", "halfExtents": [0.5, 0.5, 0.5] } ] },
                { "partTypeId": 90, "name": "tnt", "mode": "dynamic", "mass": 1,
                  "connectionVisual": "attachmentPlain",
                  "capabilities": { "jointConnectionType": "target" },
                  "shapes": [
                    { "kind": "box", "halfExtents": [0.5, 0.5, 0.5] },
                    { "kind": "box", "halfExtents": [0.25, 0.14, 0.5], "offset": [0, -0.37, 0], "condition": { "kind": "attachment", "side": "bottom" } },
                    { "kind": "box", "halfExtents": [0.25, 0.14, 0.5], "offset": [0, 0.37, 0], "condition": { "kind": "attachment", "side": "top" } }
                  ] }
            ]
        }
        """));
        EntityStore entities = new();
        ConstructionRules rules = new(entities, new PartStore(entities), new TransformStore(entities), content);
        EntityId isolated = rules.Place(90, 0f, 0f, 0f, 1f, 0).Entity;

        Assert.Empty(ConnectionShapes.ConnectableSides(isolated, rules, content));
        IReadOnlyList<PartShapeDefinition> alone = ConnectionShapes.SpawnShapes(isolated, rules, content);
        Assert.Single(alone);
        Assert.Null(alone[0].ConditionSide);

        EntityId supported = rules.Place(90, 10f, 0f, 0f, 1f, 0).Entity;
        rules.Place(1, 10f, -1f, 0f, 1f, 0);
        Assert.Equal(new[] { LocalSide.Bottom }, ConnectionShapes.ConnectableSides(supported, rules, content));
        IReadOnlyList<PartShapeDefinition> withBottom = ConnectionShapes.SpawnShapes(supported, rules, content);
        Assert.Single(withBottom, shape => shape.ConditionSide is null);
        Assert.Single(withBottom, shape => shape.ConditionSide == "bottom");
        Assert.DoesNotContain(withBottom, shape => shape.ConditionSide == "top");
    }

    [Fact]
    public void TheConditionalRuleTableMirrorsTheOriginalPerScriptFormulas()
    {
        PartShapeDefinition bottom = Marker("bottom");
        PartShapeDefinition top = Marker("top");
        PartShapeDefinition left = Marker("left");
        PartShapeDefinition topRight = Marker("topRight");
        HashSet<LocalSide> empty = new();
        HashSet<LocalSide> up = new() { LocalSide.Top };
        HashSet<LocalSide> diagonal = new() { LocalSide.TopRight };

        // Rocket.cs: bottom is the ghost while nothing connects, and steps aside for a real side.
        Assert.True(ConnectionShapes.ConditionVisible(bottom, empty, ConnectionVisualKind.AttachmentFallback, 0f));
        Assert.False(ConnectionShapes.ConditionVisible(bottom, up, ConnectionVisualKind.AttachmentFallback, 0f));
        Assert.True(ConnectionShapes.ConditionVisible(bottom, new HashSet<LocalSide> { LocalSide.Bottom }, ConnectionVisualKind.AttachmentFallback, 0f));
        Assert.False(ConnectionShapes.ConditionVisible(top, empty, ConnectionVisualKind.AttachmentFallback, 0f));
        Assert.True(ConnectionShapes.ConditionVisible(top, up, ConnectionVisualKind.AttachmentFallback, 0f));

        // TNT.cs: each marker follows its own side, with no fallback.
        Assert.False(ConnectionShapes.ConditionVisible(bottom, empty, ConnectionVisualKind.AttachmentPlain, 0f));
        Assert.True(ConnectionShapes.ConditionVisible(bottom, new HashSet<LocalSide> { LocalSide.Bottom }, ConnectionVisualKind.AttachmentPlain, 0f));

        // SpotLight.cs/GrapplingHook.cs: the diagonals need a 45-degree turn and a real side.
        Assert.True(ConnectionShapes.ConditionVisible(topRight, diagonal, ConnectionVisualKind.AttachmentEight, MathF.PI / 4f));
        Assert.False(ConnectionShapes.ConditionVisible(topRight, diagonal, ConnectionVisualKind.AttachmentEight, 0f));
        Assert.False(ConnectionShapes.ConditionVisible(topRight, empty, ConnectionVisualKind.AttachmentEight, MathF.PI / 4f));
        Assert.True(ConnectionShapes.ConditionVisible(left, new HashSet<LocalSide> { LocalSide.Left }, ConnectionVisualKind.AttachmentEight, 0f));
        Assert.False(ConnectionShapes.ConditionVisible(left, new HashSet<LocalSide> { LocalSide.Left }, ConnectionVisualKind.AttachmentEight, MathF.PI / 4f));

        // A frame mount is a sprite pair, not a collider.
        Assert.False(ConnectionShapes.ConditionVisible(top, up, ConnectionVisualKind.Frame, 0f));
    }

    [Fact]
    public void ConditionalShapesWithoutARuleAreRejectedAtParseTime()
    {
        // A conditional marker with no rule has no connection state to follow; refusing it keeps a
        // part from silently losing every bracket at spawn.
        PartContentException error = Assert.Throws<PartContentException>(() => PartContentParser.Parse("""
        {
            "format": "pigforge.part-content",
            "schemaVersion": 1,
            "contentVersion": "conditional-no-rule-test-v1",
            "physics": { "maximumAngularSpeed": 7.0, "damping": { "linear": 0.2, "angular": 0.05 } },
            "parts": [
                { "partTypeId": 1, "name": "orphan", "mode": "dynamic", "mass": 1,
                  "shapes": [
                    { "kind": "box", "halfExtents": [0.5, 0.5, 0.5] },
                    { "kind": "box", "halfExtents": [0.25, 0.14, 0.5], "condition": { "kind": "attachment", "side": "top" } }
                  ] }
            ]
        }
        """));
        Assert.Contains("connectionVisual", error.Message, StringComparison.Ordinal);
    }

    // --------------------------------------------------------------------- Task 7: the wing box

    [Fact]
    public void AnUnconnectedGliderUsesTheThickBottomCollider()
    {
        // Wings.cs:62-71: with no mount connection the bottom frame (and the thick, low collider)
        // is the current state. The prefab's serialized size.y is 0.6122213, but the script always
        // overwrites it, so the running game is exactly 0.6 tall and centred at -0.15.
        (ConstructionRules rules, PartContentLibrary content) = CreateRules();
        EntityId glider = rules.Place(PartGlider, 0f, 0f, 0f, 1f, 0).Entity;

        Assert.Empty(ConnectionShapes.ConnectableSides(glider, rules, content));
        IReadOnlyList<PartShapeDefinition> shapes = ConnectionShapes.SpawnShapes(glider, rules, content);

        PartShapeDefinition body = Assert.Single(shapes, shape => shape.ConditionSide is null);
        Assert.Equal(new[] { 0.95f, 0.3f, 0.75f }, body.BoxHalfExtents!);
        Assert.Equal(new[] { -0.5f, -0.15f, 0f }, body.Offset!);

        BoxShapeDefinition physics = Assert.IsType<BoxShapeDefinition>(Assert.Single(
            content.PlaceShapes(PartGlider, 1f, shapes)).Shape);
        Assert.Equal(0.3f, physics.HalfExtentY, precision: 6);
    }

    [Fact]
    public void AGliderWeldedOnlyAboveUsesTheThinTopCollider()
    {
        // flag true, flag2 false => thin and high: size.y 0.3 about +0.05.
        (ConstructionRules rules, PartContentLibrary content) = CreateRules();
        EntityId glider = rules.Place(PartGlider, 0f, 0f, 0f, 1f, 0).Entity;
        rules.Place(PartFrame, 0f, 1f, 0f, 1f, 0);

        Assert.Equal(new[] { LocalSide.Top }, ConnectionShapes.ConnectableSides(glider, rules, content));
        IReadOnlyList<PartShapeDefinition> shapes = ConnectionShapes.SpawnShapes(glider, rules, content);

        PartShapeDefinition body = Assert.Single(shapes, shape => shape.ConditionSide is null);
        Assert.Equal(new[] { 0.95f, 0.15f, 0.75f }, body.BoxHalfExtents!);
        Assert.Equal(new[] { -0.5f, 0.05f, 0f }, body.Offset!);

        BoxShapeDefinition physics = Assert.IsType<BoxShapeDefinition>(Assert.Single(
            content.PlaceShapes(PartGlider, 1f, shapes)).Shape);
        Assert.Equal(0.15f, physics.HalfExtentY, precision: 6);
    }

    [Fact]
    public void AGliderWeldedToItsSideKeepsTheThickBottomCollider()
    {
        // A side connection lands in both OR sets (Wings.cs:43-44), so the thick/low form stays.
        (ConstructionRules rules, PartContentLibrary content) = CreateRules();
        EntityId glider = rules.Place(PartGlider, 0f, 0f, 0f, 1f, 0).Entity;
        rules.Place(PartFrame, 1f, 0f, 0f, 1f, 0);

        Assert.Equal(new[] { LocalSide.Right }, ConnectionShapes.ConnectableSides(glider, rules, content));
        PartShapeDefinition body = Assert.Single(
            ConnectionShapes.SpawnShapes(glider, rules, content),
            shape => shape.ConditionSide is null);
        Assert.Equal(0.3f, body.BoxHalfExtents![1], precision: 6);
        Assert.Equal(-0.15f, body.Offset![1], precision: 6);
    }

    [Fact]
    public void TheGliderFrameBracketNeverBecomesBodyGeometry()
    {
        // The `frame` shape is the art-derived alignment box (ADR-018), not a prefab collider, so it
        // stays out of the body in either state.
        (ConstructionRules rules, PartContentLibrary content) = CreateRules();
        EntityId glider = rules.Place(PartGlider, 0f, 0f, 0f, 1f, 0).Entity;
        PartShapeDefinition bracket = Assert.Single(content.GetPart(PartGlider).Shapes.Where(shape => shape.ConditionKind == "frame"));

        Assert.Single(ConnectionShapes.SpawnShapes(glider, rules, content));
        Assert.DoesNotContain(bracket, ConnectionShapes.SpawnShapes(glider, rules, content));
    }

    // ------------------------------------------------------------------------------------ helpers

    private static PartShapeDefinition Marker(string side) =>
        new(PhysicsShapeKind.Box, new[] { 0.25f, 0.14f, 0.5f }, null, null, null, null, new[] { 0f, 0.37f, 0f }, side, "attachment");

    private static bool AttachmentExtent(ShapeDefinition shape) =>
        shape is BoxShapeDefinition box && MathF.Abs(box.HalfExtentX - 0.25f) < 1e-5f && MathF.Abs(box.HalfExtentY - 0.14f) < 1e-5f;

    /// <summary>The leaf children of the body a cluster of one entity builds.</summary>
    private static IReadOnlyList<CompoundChild> ClusterChildren(EntityId entity, ConstructionRules rules, PartContentLibrary content)
    {
        CompoundCluster cluster = Assert.Single(CompoundAssembler.Assemble(new[] { entity }, rules, content).Clusters);
        BodyDefinition body = cluster.CreateBodyDefinition(content, rules);
        if (body.Shapes.Count == 1 && body.Shapes[0] is CompoundShapeDefinition compound)
        {
            return compound.Children;
        }

        return body.Shapes.Select(shape => new CompoundChild(shape, PhysicsVector3.Zero)).ToArray();
    }

    private static (ConstructionRules Rules, PartContentLibrary Content) CreateRules()
    {
        PartContentLibrary content = PartContentLibrary.Load(FindRepositoryFile("content/parts.json"));
        EntityStore entities = new();
        return (new ConstructionRules(entities, new PartStore(entities), new TransformStore(entities), content), content);
    }

    private static string FindRepositoryFile(string relativePath)
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, relativePath)))
        {
            directory = directory.Parent;
        }

        return directory is null
            ? throw new FileNotFoundException($"Could not locate {relativePath} from {AppContext.BaseDirectory}.")
            : Path.Combine(directory.FullName, relativePath);
    }
}
