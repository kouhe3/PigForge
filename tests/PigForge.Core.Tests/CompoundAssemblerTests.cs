using PigForge.Core.Construction;
using PigForge.Core.Content;
using PigForge.Physics.Abstractions;

namespace PigForge.Core.Tests;

/// <summary>
/// Compound merge and seam split (issue #4 / ADR-002): connected dynamic boxes weld
/// into one rigid cluster; an impulse over the seam threshold splits along that seam
/// with no damage accumulation. Hashes are stable across double runs.
/// </summary>
public sealed class CompoundAssemblerTests
{
    private const uint PartBlock = 1;
    private const uint PartGround = 2;
    private const uint PartWheel = 3;

    [Fact]
    public void AdjacentDynamicBoxesMergeIntoOneClusterWithOneSeam()
    {
        (ConstructionRules rules, PartContentLibrary content) = CreateRules();
        EntityId left = rules.Place(PartBlock, 0f, 0f, 0f, 1f, 0).Entity;
        EntityId right = rules.Place(PartBlock, 1.1f, 0f, 0f, 1f, 0).Entity;

        List<CompoundCluster> clusters = CompoundAssembler.Assemble(new[] { left, right }, rules, content);

        CompoundCluster cluster = Assert.Single(clusters);
        Assert.True(cluster.IsMerged);
        Assert.Equal(2, cluster.Members.Count);
        CompoundSeam seam = Assert.Single(cluster.Seams);
        Assert.Equal(left, seam.Left);
        Assert.Equal(right, seam.Right);
        Assert.Equal(CompoundAssembler.DefaultSeamBreakImpulse, seam.BreakImpulse);
        Assert.Equal(2f, cluster.Mass, precision: 4);
        Assert.Equal(0.55f, cluster.WorldPosition.X, precision: 4);
    }

    [Fact]
    public void DistantPartsStaySingletonClusters()
    {
        (ConstructionRules rules, PartContentLibrary content) = CreateRules();
        EntityId left = rules.Place(PartBlock, 0f, 0f, 0f, 1f, 0).Entity;
        EntityId right = rules.Place(PartBlock, 2.5f, 0f, 0f, 1f, 0).Entity;

        List<CompoundCluster> clusters = CompoundAssembler.Assemble(new[] { left, right }, rules, content);

        Assert.Equal(2, clusters.Count);
        Assert.All(clusters, cluster => Assert.False(cluster.IsMerged));
    }

    [Fact]
    public void StaticPartsNeverMerge()
    {
        (ConstructionRules rules, PartContentLibrary content) = CreateRules();
        EntityId ground = rules.Place(PartGround, 0f, -1f, 0f, 1f, 0).Entity;
        EntityId block = rules.Place(PartBlock, 0f, 0.1f, 0f, 1f, 0).Entity;

        List<CompoundCluster> clusters = CompoundAssembler.Assemble(new[] { ground, block }, rules, content);

        Assert.Equal(2, clusters.Count);
        Assert.All(clusters, cluster => Assert.False(cluster.IsMerged));
    }

    [Fact]
    public void NestedCompoundShapesAreRejected()
    {
        BoxShapeDefinition box = new(0.5f, 0.5f, 0.5f);
        CompoundShapeDefinition inner = new(new[] { new CompoundChild(box, PhysicsVector3.Zero) });

        Assert.Throws<ArgumentException>(() => new CompoundShapeDefinition(new[]
        {
            new CompoundChild(inner, PhysicsVector3.Zero)
        }));
        Assert.Throws<ArgumentException>(() => new CompoundShapeDefinition(Array.Empty<CompoundChild>()));
    }

    [Fact]
    public void SplitAlongSeamYieldsTwoSingletons()
    {
        (ConstructionRules rules, PartContentLibrary content) = CreateRules();
        EntityId left = rules.Place(PartBlock, 0f, 0f, 0f, 1f, 0).Entity;
        EntityId right = rules.Place(PartBlock, 1.1f, 0f, 0f, 1f, 0).Entity;
        CompoundCluster merged = Assert.Single(CompoundAssembler.Assemble(new[] { left, right }, rules, content));

        IReadOnlyList<CompoundCluster> pieces = CompoundAssembler.SplitAlongSeam(merged, merged.Seams[0]);

        Assert.Equal(2, pieces.Count);
        Assert.All(pieces, piece => Assert.False(piece.IsMerged));
        Assert.Equal(left, pieces[0].Members[0].Entity);
        Assert.Equal(right, pieces[1].Members[0].Entity);
        Assert.Equal(0f, pieces[0].WorldPosition.X, precision: 4);
        Assert.Equal(1.1f, pieces[1].WorldPosition.X, precision: 4);
    }

    [Fact]
    public void AdjacentSphereAndBoxMergeIntoOneCluster()
    {
        (ConstructionRules rules, PartContentLibrary content) = CreateRules();
        EntityId block = rules.Place(PartBlock, 0f, 0f, 0f, 1f, 0).Entity;
        EntityId wheel = rules.Place(PartWheel, 0f, -1f, 0f, 1f, 0).Entity;

        CompoundCluster cluster = Assert.Single(CompoundAssembler.Assemble(new[] { block, wheel }, rules, content));

        Assert.True(cluster.IsMerged);
        Assert.Equal(new[] { block, wheel }, cluster.Members.Select(member => member.Entity));
        Assert.Equal(1.5f, cluster.Mass, precision: 4);
        CompoundSeam seam = Assert.Single(cluster.Seams);
        Assert.Equal(block, seam.Left);
        Assert.Equal(wheel, seam.Right);
    }

    [Fact]
    public void WoodenWheelSupportColliderConnectsToFrameDirectlyAbove()
    {
        // The original wheel is a tire sphere plus a support box at the top; only that
        // box reaches a wooden frame placed one grid cell above the wheel.
        const uint WoodenBlock = 1;
        const uint WoodenWheel = 7;
        PartContentLibrary content = PartContentLibrary.Load(FindRepositoryFile("content/parts.json"));
        EntityStore entities = new();
        ConstructionRules rules = new(entities, new PartStore(entities), new TransformStore(entities), content);
        EntityId frame = rules.Place(WoodenBlock, 0f, 0f, 0f, 1f, 0).Entity;
        EntityId wheel = rules.Place(WoodenWheel, 0f, -1f, 0f, 1f, 0).Entity;
        Assert.Contains(wheel.Value, rules.ConnectionsOf(frame));

        // Wheels keep their own body and attach through a revolute joint so they roll
        // instead of skidding with the chassis.
        List<CompoundCluster> clusters = CompoundAssembler.Assemble(new[] { frame, wheel }, rules, content);
        Assert.Equal(2, clusters.Count);
        Assert.All(clusters, cluster => Assert.False(cluster.IsMerged));
        CompoundHinge hinge = Assert.Single(CompoundAssembler.CollectHinges(new[] { frame, wheel }, rules, content));
        Assert.Equal(wheel, hinge.Wheel);
        Assert.Equal(frame, hinge.Parent);
    }

    private static string FindRepositoryFile(string relativePath)
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, relativePath)))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        return Path.Combine(directory!.FullName, relativePath);
    }

    [Fact]
    public void NearestSeamPicksTheCloserWeld()
    {
        (ConstructionRules rules, PartContentLibrary content) = CreateRules();
        EntityId a = rules.Place(PartBlock, 0f, 0f, 0f, 1f, 0).Entity;
        EntityId b = rules.Place(PartBlock, 1.1f, 0f, 0f, 1f, 0).Entity;
        EntityId c = rules.Place(PartBlock, 2.2f, 0f, 0f, 1f, 0).Entity;
        CompoundCluster cluster = Assert.Single(CompoundAssembler.Assemble(new[] { a, b, c }, rules, content));
        Assert.Equal(2, cluster.Seams.Count);

        CompoundSeam? nearA = CompoundAssembler.NearestSeam(cluster, new PhysicsVector3(0.55f, 0f, 0f));
        CompoundSeam? nearC = CompoundAssembler.NearestSeam(cluster, new PhysicsVector3(1.65f, 0f, 0f));

        Assert.Equal(a, nearA!.Value.Left);
        Assert.Equal(b, nearA.Value.Right);
        Assert.Equal(b, nearC!.Value.Left);
        Assert.Equal(c, nearC.Value.Right);
    }

    [Fact]
    public void CompoundSplitScriptIsDeterministicAcrossRuns()
    {
        Assert.Equal(RunSplitScript(), RunSplitScript());
        Assert.NotEqual(0, RunSplitScript());
    }

    private static long RunSplitScript()
    {
        (ConstructionRules rules, PartContentLibrary content) = CreateRules();
        List<EntityId> placed = new();
        placed.Add(rules.Place(PartBlock, 0f, 0f, 0f, 1f, 0).Entity);
        placed.Add(rules.Place(PartBlock, 1.1f, 0f, 0f, 1f, 0).Entity);
        placed.Add(rules.Place(PartBlock, 2.2f, 0f, 0f, 1f, 0).Entity);
        CompoundCluster cluster = Assert.Single(CompoundAssembler.Assemble(placed, rules, content));

        long hash = cluster.ComputeHash();
        CompoundSeam? seam = CompoundAssembler.NearestSeam(cluster, new PhysicsVector3(0.55f, 0f, 0f));
        foreach (CompoundCluster piece in CompoundAssembler.SplitAlongSeam(cluster, seam!.Value))
        {
            hash = unchecked((hash * 31) + piece.ComputeHash());
        }

        return hash;
    }

    private static (ConstructionRules Rules, PartContentLibrary Content) CreateRules()
    {
        PartContentLibrary content = new(PartContentParser.Parse("""
        {
            "format": "pigforge.part-content",
            "schemaVersion": 1,
            "contentVersion": "compound-test-v1",
            "parts": [
                { "partTypeId": 1, "name": "block", "mode": "dynamic", "mass": 1, "shapes": [ { "kind": "box", "halfExtents": [0.5, 0.5, 0.5] } ] },
                { "partTypeId": 2, "name": "ground", "mode": "static", "mass": 0, "shapes": [ { "kind": "box", "halfExtents": [4, 0.5, 4] } ] },
                { "partTypeId": 3, "name": "wheel", "mode": "dynamic", "mass": 0.5, "shapes": [ { "kind": "sphere", "radius": 0.45 } ] }
            ]
        }
        """));
        EntityStore entities = new();
        return (new ConstructionRules(entities, new PartStore(entities), new TransformStore(entities), content), content);
    }
}
