using PigForge.Core.Construction;
using PigForge.Core.Content;
using PigForge.Physics.Abstractions;

namespace PigForge.Core.Tests;

/// <summary>
/// Free-placement semantics (issue #4): arbitrary planar angles and uniform scales,
/// exact OBB occupancy behind a coarse spatial hash, and proximity connections.
/// </summary>
public sealed class FreePlacementTests
{
    private const uint PartBlock = 1;
    private const uint PartPlank = 2;

    [Fact]
    public void FreePlacementRecordsArbitraryAnglesAndScales()
    {
        (ConstructionRules rules, _) = CreateRules();

        ConstructionResult placed = rules.Place(PartBlock, 2.3f, 1.7f, angle: 0.6f, scale: 2f, owner: 0);

        Assert.True(placed.IsSuccess);
        Assert.True(rules.TryGetTransform(placed.Entity, out EntityTransform transform));
        Assert.Equal(new PhysicsVector3(2.3f, 1.7f, 0f), transform.Position);
        Assert.Equal(2f, transform.Scale);
        Assert.Equal(0.6f, 2f * MathF.Atan2(transform.Rotation.Z, transform.Rotation.W), precision: 5);
    }

    [Fact]
    public void OverlapIsRejectedRegardlessOfAlignmentWhileTouchingIsLegal()
    {
        (ConstructionRules rules, _) = CreateRules();

        // Two 45° rectangles whose centres are closer than their diagonal extents cross.
        Assert.True(rules.Place(PartBlock, 0f, 0f, 0.785398f, 1f, 0).IsSuccess);
        Assert.Equal(ConstructionError.CellsOccupied, rules.Place(PartBlock, 0.9f, 0.1f, 0.785398f, 1f, 0).Error);

        // Flush edge-to-edge placement is legal and connects through proximity.
        Assert.True(rules.Place(PartBlock, 10f, 0f, 0f, 1f, 0).IsSuccess);
        Assert.True(rules.Place(PartBlock, 11f, 0f, 0f, 1f, 0).IsSuccess);
        Assert.Contains(1048579u, rules.ConnectionsOf(new EntityId(1048578)));
    }

    [Fact]
    public void ProximityConnectsNearbyPartsButNotDistantOnes()
    {
        (ConstructionRules rules, _) = CreateRules();

        // 0.1 gap connects (within ConnectionProximity), 0.3 gap does not.
        Assert.True(rules.Place(PartBlock, 0f, 0f, 0f, 1f, 0).IsSuccess);
        Assert.True(rules.Place(PartBlock, 1.1f, 0f, 0f, 1f, 0).IsSuccess);
        Assert.True(rules.Place(PartBlock, 2.5f, 0f, 0f, 1f, 0).IsSuccess);

        Assert.Contains(1048578u, rules.ConnectionsOf(new EntityId(1048577)));
        Assert.DoesNotContain(1048579u, rules.ConnectionsOf(new EntityId(1048577)));
    }

    [Fact]
    public void ScaleMultipliesTheFootprintAreaLimit()
    {
        (ConstructionRules rules, _) = CreateRules(new ConstructionLimits(MaxParts: 8, MaxConnectionsPerPart: 6, MaxFootprintCells: 8));

        Assert.Equal(ConstructionError.FootprintTooLarge, rules.Place(PartBlock, 0f, 0f, 0f, scale: 4f, owner: 0).Error);
        Assert.True(rules.Place(PartBlock, 0f, 0f, 0f, scale: 2f, owner: 0).IsSuccess);
    }

    [Fact]
    public void FreePlacementScriptIsDeterministicAcrossRuns()
    {
        Assert.Equal(RunFreePlacementScript(), RunFreePlacementScript());
    }

    private static long RunFreePlacementScript()
    {
        (ConstructionRules rules, _) = CreateRules();
        float[] angles = { 0f, 0.35f, 1.2f, 2f };
        for (int index = 0; index < angles.Length; index++)
        {
            ConstructionResult result = rules.Place(PartBlock, 2f + (index * 10f), 3f + (index * 7f), angles[index], 1.5f, 0);
            Assert.True(result.IsSuccess, result.Error.ToString());
        }

        Assert.True(rules.Rotate(new EntityId(1048577), 0.9f, 0).IsSuccess);
        Assert.True(rules.Remove(new EntityId(1048578), 0).IsSuccess);
        Assert.True(rules.Place(PartPlank, 50f, 50f, -0.4f, 1f, 0).IsSuccess);
        return rules.ComputeLayoutHash();
    }

    [Fact]
    public void ContentLibraryScalesShapesAndMassCubically()
    {
        PartContentLibrary library = new(PartContentParser.Parse("""
        {
            "format": "pigforge.part-content",
            "schemaVersion": 1,
            "contentVersion": "scale-test-v1",
            "parts": [
                { "partTypeId": 1, "name": "block", "mode": "dynamic", "mass": 2, "shapes": [ { "kind": "box", "halfExtents": [0.5, 0.5, 0.5] } ] }
            ]
        }
        """));

        BodyDefinition scaled = library.CreateBodyDefinition(1, PhysicsVector3.Zero, PhysicsQuaternion.Identity, scale: 2f);

        BoxShapeDefinition box = Assert.IsType<BoxShapeDefinition>(Assert.Single(scaled.Shapes));
        Assert.Equal(1f, box.HalfExtentX);
        Assert.Equal(16f, scaled.Mass);
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            library.CreateBodyDefinition(1, PhysicsVector3.Zero, PhysicsQuaternion.Identity, scale: 0f));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            library.CreateBodyDefinition(1, PhysicsVector3.Zero, PhysicsQuaternion.Identity, scale: 9f));
    }

    private static (ConstructionRules Rules, EntityStore Entities) CreateRules(ConstructionLimits? limits = null)
    {
        PartContentLibrary content = new(PartContentParser.Parse("""
        {
            "format": "pigforge.part-content",
            "schemaVersion": 1,
            "contentVersion": "construction-test-v1",
            "parts": [
                { "partTypeId": 1, "name": "block", "mode": "dynamic", "mass": 1, "shapes": [ { "kind": "box", "halfExtents": [0.5, 0.5, 0.5] } ] },
                { "partTypeId": 2, "name": "plank", "mode": "dynamic", "mass": 0.5, "shapes": [ { "kind": "box", "halfExtents": [1.0, 0.5, 0.5] } ] }
            ]
        }
        """));
        EntityStore entities = new();
        return (new ConstructionRules(entities, new PartStore(entities), new TransformStore(entities), content, limits), entities);
    }
}
