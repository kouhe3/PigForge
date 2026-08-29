using PigForge.Core.Construction;
using PigForge.Core.Content;
using PigForge.Physics.Abstractions;

namespace PigForge.Core.Tests;

public sealed class ConstructionRulesTests
{
    private const uint PartBlock = 1;
    private const uint PartPlank = 2;

    [Fact]
    public void PlaceCreatesEntityWithPartsTransformAndConnections()
    {
        (ConstructionRules rules, _) = CreateRules();

        ConstructionResult first = rules.Place(PartBlock, gridX: 0, gridY: 0, rotation: 0);
        ConstructionResult second = rules.Place(PartBlock, gridX: 1, gridY: 0, rotation: 0);

        Assert.True(first.IsSuccess);
        Assert.True(second.IsSuccess);
        Assert.Equal(2, rules.PartCount);
        Assert.Equal(new PhysicsVector3(0.5f, 0.5f, 0f), TransformOf(rules, first.Entity).Position);
        Assert.Equal(new[] { second.Entity.Value }, rules.ConnectionsOf(first.Entity));
        Assert.Equal(new[] { first.Entity.Value }, rules.ConnectionsOf(second.Entity));
    }

    [Fact]
    public void PlaceOnOccupiedCellsIsRejected()
    {
        (ConstructionRules rules, _) = CreateRules();

        Assert.True(rules.Place(PartBlock, 0, 0, 0).IsSuccess);
        ConstructionResult blocked = rules.Place(PartBlock, 0, 0, 0);

        Assert.Equal(ConstructionError.CellsOccupied, blocked.Error);
        Assert.Equal(1, rules.PartCount);
    }

    [Theory]
    [InlineData(99u, 0, ConstructionError.UnknownPartType)]
    [InlineData(PartBlock, 4, ConstructionError.InvalidRotation)]
    public void InvalidPlacementCommandsAreRejected(uint partTypeId, byte rotation, ConstructionError expected)
    {
        (ConstructionRules rules, _) = CreateRules();

        ConstructionResult result = rules.Place(partTypeId, 0, 0, rotation);

        Assert.Equal(expected, result.Error);
        Assert.Equal(0, rules.PartCount);
    }

    [Fact]
    public void PartLimitIsEnforced()
    {
        (ConstructionRules rules, _) = CreateRules(new ConstructionLimits(MaxParts: 2, MaxConnectionsPerPart: 6, MaxFootprintCells: 64));

        Assert.True(rules.Place(PartBlock, 0, 0, 0).IsSuccess);
        Assert.True(rules.Place(PartBlock, 5, 0, 0).IsSuccess);
        ConstructionResult third = rules.Place(PartBlock, 9, 0, 0);

        Assert.Equal(ConstructionError.PartLimitReached, third.Error);
    }

    [Fact]
    public void ConnectionLimitIsEnforced()
    {
        (ConstructionRules rules, _) = CreateRules(new ConstructionLimits(MaxParts: 64, MaxConnectionsPerPart: 2, MaxFootprintCells: 64));

        Assert.True(rules.Place(PartBlock, 0, 0, 0).IsSuccess);
        Assert.True(rules.Place(PartBlock, 1, 0, 0).IsSuccess);
        Assert.True(rules.Place(PartBlock, -1, 0, 0).IsSuccess);
        ConstructionResult thirdNeighbour = rules.Place(PartBlock, 0, 1, 0);

        Assert.Equal(ConstructionError.ConnectionLimitReached, thirdNeighbour.Error);
    }

    [Fact]
    public void RemoveDestroysEntityAndFreesCellsForReuse()
    {
        (ConstructionRules rules, _) = CreateRules();
        ConstructionResult placed = rules.Place(PartBlock, 0, 0, 0);

        ConstructionResult removed = rules.Remove(placed.Entity);
        ConstructionResult replaced = rules.Place(PartBlock, 0, 0, 0);

        Assert.True(removed.IsSuccess);
        Assert.False(rules.IsAlive(placed.Entity));
        Assert.True(replaced.IsSuccess);
        Assert.NotEqual(placed.Entity, replaced.Entity);
    }

    [Fact]
    public void RemoveOfUnknownOrNonConstructionEntityIsRejected()
    {
        (ConstructionRules rules, EntityStore entities) = CreateRules();

        Assert.Equal(ConstructionError.EntityNotFound, rules.Remove(new EntityId(12345)).Error);

        EntityId foreign = entities.Create();
        Assert.Equal(ConstructionError.NotAConstructionEntity, rules.Remove(foreign).Error);
        Assert.True(rules.IsAlive(foreign), "A rejected removal must leave the entity intact.");
    }

    [Fact]
    public void RotateRecomputesOccupancyTransformAndConnections()
    {
        (ConstructionRules rules, _) = CreateRules();
        ConstructionResult plank = rules.Place(PartPlank, 0, 0, 0);
        ConstructionResult block = rules.Place(PartBlock, 2, 0, 0);
        Assert.True(plank.IsSuccess);
        Assert.True(block.IsSuccess);
        Assert.NotEmpty(rules.ConnectionsOf(plank.Entity));

        ConstructionResult rotated = rules.Rotate(plank.Entity, rotation: 1);

        Assert.True(rotated.IsSuccess);
        EntityTransform transform = TransformOf(rules, plank.Entity);
        Assert.Equal(0.5f, transform.Position.X, 3f);
        Assert.Equal(1f, transform.Position.Y, 3f);
        Assert.Equal(MathF.Sin(MathF.PI / 4f), transform.Rotation.Z, 3f);
        Assert.Empty(rules.ConnectionsOf(plank.Entity));
        Assert.Empty(rules.ConnectionsOf(block.Entity));
    }

    [Fact]
    public void RotateIntoOccupiedCellsIsBlocked()
    {
        (ConstructionRules rules, _) = CreateRules();
        ConstructionResult plank = rules.Place(PartPlank, 0, 0, 0);
        Assert.True(rules.Place(PartBlock, 0, 1, 0).IsSuccess);

        ConstructionResult rotated = rules.Rotate(plank.Entity, rotation: 1);

        Assert.Equal(ConstructionError.RotationBlocked, rotated.Error);
        EntityTransform transform = TransformOf(rules, plank.Entity);
        Assert.Equal(0f, transform.Rotation.Z, precision: 4);
        Assert.Single(rules.ConnectionsOf(plank.Entity));
    }

    [Fact]
    public void CommandScriptReplaysDeterministically()
    {
        (long hash, ConstructionError[] rejections) firstRun = RunFixtureScript();
        (long hash, ConstructionError[] rejections) secondRun = RunFixtureScript();

        Assert.Equal(firstRun.hash, secondRun.hash);
        Assert.Equal(firstRun.rejections, secondRun.rejections);
        Assert.Contains(ConstructionError.CellsOccupied, firstRun.rejections);
        Assert.Contains(ConstructionError.InvalidRotation, firstRun.rejections);
    }

    private static EntityTransform TransformOf(ConstructionRules rules, EntityId entity)
    {
        Assert.True(rules.TryGetTransform(entity, out EntityTransform transform));
        return transform;
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

    private static (long Hash, ConstructionError[] Rejections) RunFixtureScript()
    {
        (ConstructionRules rules, _) = CreateRules();
        List<ConstructionError> rejections = new();
        List<EntityId> entities = new();

        (char op, uint part, int x, int y, byte rotation)[] script =
        {
            ('p', PartBlock, 0, 0, 0),
            ('p', PartBlock, 1, 0, 0),
            ('p', PartPlank, 3, 0, 0),
            ('r', 0, 3, 0, 1),
            ('p', PartBlock, 1, 0, 0),
            ('p', PartBlock, 4, 0, 4),
            ('x', 0, 0, 0, 9),
        };

        foreach ((char op, uint part, int x, int y, byte rotation) in script)
        {
            switch (op)
            {
                case 'p':
                    ConstructionResult placed = rules.Place(part, x, y, rotation);
                    if (placed.IsSuccess)
                    {
                        entities.Add(placed.Entity);
                    }
                    else
                    {
                        rejections.Add(placed.Error);
                    }

                    break;
                case 'r':
                    ConstructionResult rotated = rules.Rotate(entities[^1], rotation);
                    if (!rotated.IsSuccess)
                    {
                        rejections.Add(rotated.Error);
                    }

                    break;
                case 'x':
                    ConstructionResult removed = rules.Remove(entities[0]);
                    if (!removed.IsSuccess)
                    {
                        rejections.Add(removed.Error);
                    }

                    break;
            }
        }

        return (rules.ComputeLayoutHash(), rejections.ToArray());
    }
}
