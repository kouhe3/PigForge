using PigForge.Core.Construction;
using PigForge.Core.Content;
using PigForge.Physics.Abstractions;

namespace PigForge.Core.Tests;

/// <summary>
/// Kept-contraption semantics for issue #7: freezing a previous run's build group must
/// release its grid cells, keep it non-editable, keep it counting toward limits and the
/// layout hash, and keep fresh placements independent of it.
/// </summary>
public sealed class FrozenContraptionTests
{
    private const uint PartBlock = 1;

    [Fact]
    public void FreezeAllReleasesCellsKeepsPosesAndBlocksConnections()
    {
        (ConstructionRules rules, _) = CreateRules();
        ConstructionResult first = rules.Place(PartBlock, 0f, 0f, 0f, 1f, 0);
        ConstructionResult second = rules.Place(PartBlock, 1f, 0f, 0f, 1f, 0);
        var poses = new Dictionary<uint, (PhysicsVector3, PhysicsQuaternion)>
        {
            [first.Entity.Value] = (new PhysicsVector3(10f, 3f, 0f), PhysicsQuaternion.Identity),
            [second.Entity.Value] = (new PhysicsVector3(10f, 4.2f, 0f), PhysicsQuaternion.Identity)
        };

        rules.FreezeAll(poses);

        Assert.Equal(2, rules.PartCount);
        Assert.Equal(2, rules.FrozenCount);
        Assert.True(rules.IsFrozen(first.Entity.Value));
        Assert.Equal(new PhysicsVector3(10f, 3f, 0f), TransformOf(rules, first.Entity).Position);

        // Released cells are buildable again and stay disconnected from the frozen group.
        ConstructionResult fresh = rules.Place(PartBlock, 0f, 0f, 0f, 1f, 0);
        Assert.True(fresh.IsSuccess);
        Assert.False(rules.IsFrozen(fresh.Entity.Value));
        Assert.Empty(rules.ConnectionsOf(fresh.Entity));
        Assert.Equal(new[] { second.Entity.Value }, rules.ConnectionsOf(first.Entity));
    }

    [Fact]
    public void FrozenEntitiesCountTowardThePartLimit()
    {
        (ConstructionRules rules, _) = CreateRules(new ConstructionLimits(MaxParts: 2, MaxConnectionsPerPart: 6, MaxFootprintCells: 64));
        ConstructionResult kept = rules.Place(PartBlock, 0f, 0f, 0f, 1f, 0);
        rules.FreezeAll(new Dictionary<uint, (PhysicsVector3, PhysicsQuaternion)>());

        Assert.True(rules.Place(PartBlock, 5f, 5f, 0f, 1f, 0).IsSuccess);
        Assert.Equal(ConstructionError.PartLimitReached, rules.Place(PartBlock, 9f, 9f, 0f, 1f, 0).Error);
        Assert.Equal(2, rules.PartCount);
    }

    [Fact]
    public void FrozenEntitiesRejectRemoveAndRotateWhileFreshPartsStayEditable()
    {
        (ConstructionRules rules, EntityStore entities) = CreateRules();
        ConstructionResult kept = rules.Place(PartBlock, 0f, 0f, 0f, 1f, 0);
        var movedPose = new Dictionary<uint, (PhysicsVector3, PhysicsQuaternion)>
        {
            [kept.Entity.Value] = (new PhysicsVector3(20f, 5f, 0f), PhysicsQuaternion.Identity)
        };
        rules.FreezeAll(movedPose);

        // The wreckage still occupies the spot it rests at...
        Assert.Equal(ConstructionError.CellsOccupied, rules.Place(PartBlock, 20f, 5f, 0f, 1f, 0).Error);
        // ...but its old cells are free and fresh parts stay editable.
        ConstructionResult fresh = rules.Place(PartBlock, 0f, 0f, 0f, 1f, 0);

        Assert.True(fresh.IsSuccess);
        Assert.Equal(ConstructionError.FrozenEntity, rules.Remove(kept.Entity, 0).Error);
        Assert.Equal(ConstructionError.FrozenEntity, rules.Rotate(kept.Entity, 1f, 0).Error);
        Assert.True(rules.Remove(fresh.Entity, 0).IsSuccess);
        Assert.True(entities.IsAlive(kept.Entity));
    }

    [Fact]
    public void FrozenPosesEnterTheLayoutHashDeterministically()
    {
        long samePoseFirst = HashAfterFreeze(new PhysicsVector3(5f, 1f, 0f));
        long samePoseSecond = HashAfterFreeze(new PhysicsVector3(5f, 1f, 0f));
        long otherPose = HashAfterFreeze(new PhysicsVector3(6f, 1f, 0f));

        Assert.Equal(samePoseFirst, samePoseSecond);
        Assert.NotEqual(samePoseFirst, otherPose);
    }

    [Fact]
    public void ResetAllDestroysBuildAndFrozenEntitiesAndReleasesEverything()
    {
        (ConstructionRules rules, EntityStore entities) = CreateRules();
        ConstructionResult frozen = rules.Place(PartBlock, 0f, 0f, 0f, 1f, 0);
        rules.FreezeAll(new Dictionary<uint, (PhysicsVector3, PhysicsQuaternion)>
        {
            [frozen.Entity.Value] = (new PhysicsVector3(20f, 5f, 0f), PhysicsQuaternion.Identity)
        });
        ConstructionResult fresh = rules.Place(PartBlock, 0f, 0f, 0f, 1f, 0);

        List<uint> destroyed = rules.ResetAll();

        Assert.Equal(new[] { frozen.Entity.Value, fresh.Entity.Value }, destroyed);
        Assert.Equal(0, rules.PartCount);
        Assert.Equal(0, entities.Count);
        Assert.False(rules.IsAlive(frozen.Entity));
        Assert.True(rules.Place(PartBlock, 20f, 5f, 0f, 1f, 0).IsSuccess);
    }

    private static long HashAfterFreeze(PhysicsVector3 pose)
    {
        (ConstructionRules rules, _) = CreateRules();
        ConstructionResult placed = rules.Place(PartBlock, 0f, 0f, 0f, 1f, 0);
        rules.FreezeAll(new Dictionary<uint, (PhysicsVector3, PhysicsQuaternion)>
        {
            [placed.Entity.Value] = (pose, PhysicsQuaternion.Identity)
        });
        return rules.ComputeLayoutHash();
    }

    [Fact]
    public void GameplayRulesResetForRebuildAllowsRelinkingTheSameBodies()
    {
        EntityStore entities = new();
        PhysicsBodyStore bodies = new(entities);
        GameplayRules rules = CreateGameplayRules(entities, bodies);
        EntityId pig = entities.Create();
        rules.AddPig(pig, 0.2f, 0.05f);
        PhysicsBodyId body = new(7);
        bodies.Set(pig, new PhysicsBodyLink(body));
        rules.LinkBody(pig, body);

        rules.ResetForRebuild();

        Assert.Equal(GameplayPhase.Playing, rules.Phase);
        Assert.False(rules.RestartRequested);
        Assert.True(bodies.TryGet(pig, out PhysicsBodyLink link));
        rules.LinkBody(pig, link.Body);
        Assert.Equal(1, rules.AlivePigs);
    }

    [Fact]
    public void GameplayRulesResetAllDropsStoresAndPigCount()
    {
        EntityStore entities = new();
        GameplayRules rules = CreateGameplayRules(entities, new PhysicsBodyStore(entities));
        EntityId pig = entities.Create();
        rules.AddPig(pig, 0.2f, 0.05f);
        rules.AddTnt(entities.Create(), fuseTicks: 3);

        rules.ResetAll();

        Assert.Equal(0, rules.AlivePigs);
        Assert.Equal(GameplayPhase.Playing, rules.Phase);
    }

    private static GameplayRules CreateGameplayRules(EntityStore entities, PhysicsBodyStore bodies) => new(
        entities,
        new MotorStore(entities),
        new BalloonStore(entities),
        new FanStore(entities),
        new RocketStore(entities),
        new TntStore(entities),
        new BlasterStore(entities),
        new GlueStore(entities),
        new WheelStore(entities),
        new PigStore(entities),
        new EggStore(entities),
        new WingStore(entities),
        new TailStore(entities),
        new UmbrellaStore(entities),
        new GearboxStore(entities),
        new BellowsStore(entities),
        new DetacherStore(entities),
        new GrappleStore(entities),
        new ActivationStore(entities),
        new RestitutionStore(entities),
        new PowerStore(entities),
        bodies,
        GameplayConfig.Default);

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
            "physics": { "maximumAngularSpeed": 7.0, "damping": { "linear": 0.2, "angular": 0.05 } },
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
