using PigForge.Core.Construction;
using PigForge.Core.Content;
using PigForge.Physics.Abstractions;

namespace PigForge.Core.Tests;

/// <summary>
/// The original's assembly model (spec docs/specs/nesting-and-pig-cargo.md): whether two
/// adjacent parts weld is a per-part three-valued capability (Contraption.cs:690), and a part
/// whose footprint overlaps a free frame is enclosed by it instead of rejected. Running-attach
/// parts (balloon/sandbag) find their anchor by walking the build grid.
/// </summary>
public sealed class JointAndEnclosureTests
{
    private const uint PartFrame = 1;
    private const uint PartPig = 2;
    private const uint PartTnt = 3;
    private const uint PartWheel = 4;
    private const uint PartSandbag = 5;
    private const uint PartBalloon = 6;

    // --- pairing rule (Contraption.cs:690) -------------------------------------------------

    [Fact]
    public void TwoAdjacentPigsDoNotMerge()
    {
        (ConstructionRules rules, PartContentLibrary content) = CreateRules();
        EntityId left = rules.Place(PartPig, 0f, 0f, 0f, 1f, 0).Entity;
        EntityId right = rules.Place(PartPig, 0.9f, 0f, 0f, 1f, 0).Entity;
        Assert.Contains(right.Value, rules.ConnectionsOf(left));

        IReadOnlyList<CompoundCluster> clusters = CompoundAssembler.Assemble(new[] { left, right }, rules, content).Clusters;

        Assert.Equal(2, clusters.Count);
        Assert.All(clusters, cluster => Assert.False(cluster.IsMerged));
    }

    [Fact]
    public void PigAdjacentToAFrameDoesNotMerge()
    {
        (ConstructionRules rules, PartContentLibrary content) = CreateRules();
        EntityId frame = rules.Place(PartFrame, 0f, 0f, 0f, 1f, 0).Entity;
        EntityId pig = rules.Place(PartPig, 1.0f, 0f, 0f, 1f, 0).Entity;
        Assert.Contains(pig.Value, rules.ConnectionsOf(frame));

        IReadOnlyList<CompoundCluster> clusters = CompoundAssembler.Assemble(new[] { frame, pig }, rules, content).Clusters;

        Assert.Equal(2, clusters.Count);
        Assert.All(clusters, cluster => Assert.False(cluster.IsMerged));
    }

    [Fact]
    public void TntAdjacentToAFrameMerges()
    {
        (ConstructionRules rules, PartContentLibrary content) = CreateRules();
        EntityId frame = rules.Place(PartFrame, 0f, 0f, 0f, 1f, 0).Entity;
        EntityId tnt = rules.Place(PartTnt, 1.1f, 0f, 0f, 1f, 0).Entity;

        CompoundCluster cluster = Assert.Single(CompoundAssembler.Assemble(new[] { frame, tnt }, rules, content).Clusters);

        Assert.True(cluster.IsMerged);
        Assert.Equal(new[] { frame, tnt }, cluster.Members.Select(member => member.Entity));
        Assert.Single(cluster.Seams);
    }

    [Fact]
    public void TwoAdjacentTntsDoNotMerge()
    {
        (ConstructionRules rules, PartContentLibrary content) = CreateRules();
        EntityId left = rules.Place(PartTnt, 0f, 0f, 0f, 1f, 0).Entity;
        EntityId right = rules.Place(PartTnt, 1.0f, 0f, 0f, 1f, 0).Entity;
        Assert.Contains(right.Value, rules.ConnectionsOf(left));

        IReadOnlyList<CompoundCluster> clusters = CompoundAssembler.Assemble(new[] { left, right }, rules, content).Clusters;

        Assert.Equal(2, clusters.Count);
        Assert.All(clusters, cluster => Assert.False(cluster.IsMerged));
    }

    [Fact]
    public void WheelStillHingesInsteadOfMerging()
    {
        (ConstructionRules rules, PartContentLibrary content) = CreateRules();
        EntityId frame = rules.Place(PartFrame, 0f, 0f, 0f, 1f, 0).Entity;
        EntityId wheel = rules.Place(PartWheel, 0.9f, 0f, 0f, 1f, 0).Entity;
        Assert.Contains(wheel.Value, rules.ConnectionsOf(frame));

        CompoundAssembly assembly = CompoundAssembler.Assemble(new[] { frame, wheel }, rules, content);

        Assert.Equal(2, assembly.Clusters.Count);
        Assert.All(assembly.Clusters, cluster => Assert.False(cluster.IsMerged));
        CompoundHinge hinge = Assert.Single(assembly.Hinges);
        Assert.Equal(wheel, hinge.Wheel);
        Assert.Equal(frame, hinge.Parent);
    }

    [Fact]
    public void CanMergePairIsTheVerbatimContraptionRule()
    {
        (ConstructionRules rules, PartContentLibrary content) = CreateRules();
        EntityId frame = rules.Place(PartFrame, 0f, 0f, 0f, 1f, 0).Entity;
        EntityId tnt = rules.Place(PartTnt, 1.1f, 0f, 0f, 1f, 0).Entity;
        EntityId pig = rules.Place(PartPig, 3f, 3f, 0f, 1f, 0).Entity;

        Assert.True(CompoundAssembler.CanMergePair(frame, tnt, rules, content));
        Assert.False(CompoundAssembler.CanMergePair(frame, pig, rules, content));
        Assert.False(CompoundAssembler.CanMergePair(pig, pig, rules, content));
    }

    // --- enclosure (spec §2.2-5) -----------------------------------------------------------

    [Fact]
    public void PlacementInsideAFreeFrameIsAcceptedAndRecordsTheRelation()
    {
        (ConstructionRules rules, PartContentLibrary content) = CreateRules();
        EntityId frame = rules.Place(PartFrame, 0f, 0f, 0f, 1f, 0).Entity;

        ConstructionResult enclosed = rules.Place(PartPig, 0f, 0f, 0f, 1f, 0);

        Assert.True(enclosed.IsSuccess);
        Assert.Equal(frame, rules.EnclosedBy(enclosed.Entity));
        Assert.Equal(enclosed.Entity, rules.EnclosedPart(frame));

        // The enclosed part shares the frame's body: the original welds it with a FixedJoint
        // (Frame.cs:44-49) regardless of the design-time joint capability.
        CompoundCluster cluster = Assert.Single(CompoundAssembler.Assemble(new[] { frame, enclosed.Entity }, rules, content).Clusters);
        Assert.True(cluster.IsMerged);
        Assert.Equal(2, cluster.Members.Count);
    }

    [Fact]
    public void SecondEnclosureOfTheSameFrameIsRejected()
    {
        (ConstructionRules rules, _) = CreateRules();
        EntityId frame = rules.Place(PartFrame, 0f, 0f, 0f, 1f, 0).Entity;
        Assert.True(rules.Place(PartPig, 0f, 0f, 0f, 1f, 0).IsSuccess);

        ConstructionResult differentType = rules.Place(PartTnt, 0f, 0f, 0f, 1f, 0);
        ConstructionResult sameType = rules.Place(PartPig, 0f, 0f, 0f, 1f, 0);

        Assert.Equal(ConstructionError.CellsOccupied, differentType.Error);
        Assert.Equal(ConstructionError.CellsOccupied, sameType.Error);
        Assert.Equal(2, rules.PartCount);
        Assert.NotNull(rules.EnclosedPart(frame));
    }

    [Fact]
    public void AFrameCannotBeEnclosed()
    {
        (ConstructionRules rules, _) = CreateRules();
        Assert.True(rules.Place(PartFrame, 0f, 0f, 0f, 1f, 0).IsSuccess);

        Assert.Equal(ConstructionError.CellsOccupied, rules.Place(PartFrame, 0f, 0f, 0f, 1f, 0).Error);
    }

    [Fact]
    public void UnsupportedOverlapIsStillRejected()
    {
        (ConstructionRules rules, _) = CreateRules();
        Assert.True(rules.Place(PartPig, 0f, 0f, 0f, 1f, 0).IsSuccess);

        // A pig is not a frame: a second pig on top of it stays a plain occupancy conflict.
        Assert.Equal(ConstructionError.CellsOccupied, rules.Place(PartPig, 0f, 0f, 0f, 1f, 0).Error);
        Assert.Equal(ConstructionError.CellsOccupied, rules.Place(PartTnt, 0f, 0f, 0f, 1f, 0).Error);
    }

    [Fact]
    public void RemovingTheEnclosedPartFreesTheFrameSlot()
    {
        (ConstructionRules rules, _) = CreateRules();
        EntityId frame = rules.Place(PartFrame, 0f, 0f, 0f, 1f, 0).Entity;
        EntityId pig = rules.Place(PartPig, 0f, 0f, 0f, 1f, 0).Entity;

        Assert.True(rules.Remove(pig, 0).IsSuccess);

        Assert.Null(rules.EnclosedBy(pig));
        Assert.Null(rules.EnclosedPart(frame));
        Assert.True(rules.Place(PartTnt, 0f, 0f, 0f, 1f, 0).IsSuccess);
    }

    [Fact]
    public void MovingOrScalingTheFrameReleasesTheEnclosure()
    {
        (ConstructionRules rules, _) = CreateRules();
        EntityId frame = rules.Place(PartFrame, 0f, 0f, 0f, 1f, 0).Entity;
        EntityId pig = rules.Place(PartPig, 0f, 0f, 0f, 1f, 0).Entity;

        Assert.True(rules.Move(frame, 10f, 0f, 0).IsSuccess);
        Assert.Null(rules.EnclosedBy(pig));
        Assert.Null(rules.EnclosedPart(frame));

        // The released pig is a plain part again and the frame's slot is free.
        Assert.True(rules.Move(pig, 20f, 0f, 0).IsSuccess);
    }

    [Fact]
    public void ForgettingADestroyedFrameReleasesTheEnclosure()
    {
        (ConstructionRules rules, EntityStore entities) = CreateRulesWithStore();
        EntityId frame = rules.Place(PartFrame, 0f, 0f, 0f, 1f, 0).Entity;
        EntityId pig = rules.Place(PartPig, 0f, 0f, 0f, 1f, 0).Entity;

        entities.Destroy(frame);
        rules.Forget(frame);

        Assert.Null(rules.EnclosedBy(pig));
        Assert.Null(rules.EnclosedPart(frame));
    }

    [Fact]
    public void ResetAllReleasesEveryEnclosure()
    {
        (ConstructionRules rules, _) = CreateRules();
        EntityId frame = rules.Place(PartFrame, 0f, 0f, 0f, 1f, 0).Entity;
        EntityId pig = rules.Place(PartPig, 0f, 0f, 0f, 1f, 0).Entity;

        rules.ResetAll();

        Assert.Null(rules.EnclosedBy(pig));
        Assert.Null(rules.EnclosedPart(frame));
        Assert.True(rules.Place(PartPig, 0f, 0f, 0f, 1f, 0).IsSuccess);
    }

    // --- runtime attachment anchors --------------------------------------------------------

    [Fact]
    public void AttachmentSearchStopsAtTheFirstChassis()
    {
        (ConstructionRules rules, _) = CreateRules();
        EntityId balloon = rules.Place(PartBalloon, 0f, 2f, 0f, 1f, 0).Entity;
        EntityId wheel = rules.Place(PartWheel, 0f, 1f, 0f, 1f, 0).Entity;
        EntityId frame = rules.Place(PartFrame, 0f, 0f, 0f, 1f, 0).Entity;

        // A balloon searches five cells down (BalloonConnectionDistance 5 in the vanilla
        // declaration defaults): the wheel is neither chassis nor pig, so the walk skips it and
        // keeps going to the frame.
        Assert.Equal(frame, rules.FindAttachmentTarget(balloon, 0, -1, ConstructionRules.BalloonAttachmentSearchCells));
        // Upward the same walk meets nothing but non-anchors in range.
        Assert.Null(rules.FindAttachmentTarget(wheel, 0, 1, ConstructionRules.SandbagAttachmentSearchCells));
    }

    [Fact]
    public void AttachmentSearchAcceptsAPigAnchor()
    {
        (ConstructionRules rules, _) = CreateRules();
        EntityId sandbag = rules.Place(PartSandbag, 0f, 0f, 0f, 1f, 0).Entity;
        EntityId pig = rules.Place(PartPig, 0f, 1f, 0f, 1f, 0).Entity;

        Assert.Equal(pig, rules.FindAttachmentTarget(sandbag, 0, 1, ConstructionRules.SandbagAttachmentSearchCells));
    }

    [Fact]
    public void ASandbagOnlyReachesTheCellAboveIt()
    {
        (ConstructionRules rules, _) = CreateRules();
        EntityId near = rules.Place(PartSandbag, 0f, 0f, 0f, 1f, 0).Entity;
        EntityId nearFrame = rules.Place(PartFrame, 0f, 2f, 0f, 1f, 0).Entity;
        EntityId far = rules.Place(PartSandbag, 4f, 0f, 0f, 1f, 0).Entity;
        EntityId farFrame = rules.Place(PartFrame, 4f, 3f, 0f, 1f, 0).Entity;

        // SandbagConnectionDistance is 1 in the vanilla declaration defaults: the frame whose cell
        // is the one directly above the sandbag is in range ...
        Assert.Equal(nearFrame, rules.FindAttachmentTarget(near, 0, 1, ConstructionRules.SandbagAttachmentSearchCells));
        // ... and the frame a cell further up is not (the balloon's five cells would reach it).
        Assert.Null(rules.FindAttachmentTarget(far, 0, 1, ConstructionRules.SandbagAttachmentSearchCells));
        Assert.Equal(farFrame, rules.FindAttachmentTarget(far, 0, 1, ConstructionRules.BalloonAttachmentSearchCells));
    }

    [Fact]
    public void ABalloonReachesFiveCells()
    {
        (ConstructionRules rules, _) = CreateRules();
        EntityId near = rules.Place(PartBalloon, 0f, 0f, 0f, 1f, 0).Entity;
        EntityId frame = rules.Place(PartFrame, 0f, -ConstructionRules.BalloonAttachmentSearchCells, 0f, 1f, 0).Entity;
        EntityId far = rules.Place(PartBalloon, 4f, 0f, 0f, 1f, 0).Entity;
        Assert.True(rules.Place(PartFrame, 4f, -(ConstructionRules.BalloonAttachmentSearchCells + 1f), 0f, 1f, 0).IsSuccess);

        Assert.Equal(frame, rules.FindAttachmentTarget(near, 0, -1, ConstructionRules.BalloonAttachmentSearchCells));
        // One cell past the distance is out of range.
        Assert.Null(rules.FindAttachmentTarget(far, 0, -1, ConstructionRules.BalloonAttachmentSearchCells));
    }

    // --- determinism -----------------------------------------------------------------------

    [Fact]
    public void EnclosureAndAttachmentScriptIsDeterministicAcrossRuns()
    {
        (long hash, ConstructionError[] rejections, int clusters, int hinges) first = RunScript();
        (long hash, ConstructionError[] rejections, int clusters, int hinges) second = RunScript();

        Assert.Equal(first.hash, second.hash);
        Assert.Equal(first.clusters, second.clusters);
        Assert.Equal(first.hinges, second.hinges);
        Assert.Equal(first.rejections, second.rejections);
        Assert.NotEqual(0, first.hash);
        Assert.Equal(
            new[] { ConstructionError.CellsOccupied, ConstructionError.CellsOccupied },
            first.rejections);
    }

    private static (long Hash, ConstructionError[] Rejections, int Clusters, int Hinges) RunScript()
    {
        (ConstructionRules rules, PartContentLibrary content) = CreateRules();
        List<ConstructionError> rejections = new();
        List<EntityId> entities = new();

        void Place(uint partTypeId, float x, float y)
        {
            ConstructionResult result = rules.Place(partTypeId, x, y, 0f, 1f, 0);
            Assert.True(result.IsSuccess, result.Error.ToString());
            entities.Add(result.Entity);
        }

        Place(PartFrame, 0f, 0f);
        Place(PartPig, 0f, 0f);          // enclosed in the frame
        Place(PartTnt, 1.1f, 0f);        // welded to the frame (source + target)
        Place(PartPig, 1.4f, 3f);        // free roaming part
        Place(PartSandbag, 0f, -1f);     // attaches upward to the frame
        Place(PartBalloon, 0f, 1f);      // attaches downward to the frame

        ConstructionResult blocked = rules.Place(PartPig, 0f, 0f, 0f, 1f, 0);
        rejections.Add(blocked.Error);
        ConstructionResult unsupported = rules.Place(PartTnt, 1.4f, 3f, 0f, 1f, 0);
        rejections.Add(unsupported.Error);

        Place(PartWheel, -1.1f, 0f);

        CompoundAssembly assembly = CompoundAssembler.Assemble(entities, rules, content);
        long hash = rules.ComputeLayoutHash();
        foreach (CompoundCluster cluster in assembly.Clusters)
        {
            hash = unchecked((hash * 31) + cluster.ComputeHash());
        }

        return (hash, rejections.ToArray(), assembly.Clusters.Count, assembly.Hinges.Count);
    }

    private static (ConstructionRules Rules, PartContentLibrary Content) CreateRules()
    {
        (ConstructionRules rules, _, PartContentLibrary content) = CreateRulesCore();
        return (rules, content);
    }

    private static (ConstructionRules Rules, EntityStore Entities) CreateRulesWithStore()
    {
        (ConstructionRules rules, EntityStore entities, _) = CreateRulesCore();
        return (rules, entities);
    }

    private static (ConstructionRules Rules, EntityStore Entities, PartContentLibrary Content) CreateRulesCore()
    {
        PartContentLibrary content = new(PartContentParser.Parse(ContentJson));
        EntityStore entities = new();
        return (new ConstructionRules(entities, new PartStore(entities), new TransformStore(entities), content), entities, content);
    }

    private const string ContentJson = """
        {
            "format": "pigforge.part-content",
            "schemaVersion": 1,
            "contentVersion": "joint-test-v1",
            "physics": { "maximumAngularSpeed": 7.0, "damping": { "linear": 0.2, "angular": 0.05 } },
            "parts": [
                { "partTypeId": 1, "name": "frame", "mode": "dynamic", "mass": 1, "shapes": [ { "kind": "box", "halfExtents": [0.5, 0.5, 0.5] } ], "capabilities": { "jointConnectionType": "source", "canEnclose": true } },
                { "partTypeId": 2, "name": "pig", "mode": "dynamic", "mass": 1, "shapes": [ { "kind": "sphere", "radius": 0.42 } ], "capabilities": { "jointConnectionType": "none", "pig": true, "canBeEnclosed": true } },
                { "partTypeId": 3, "name": "tnt", "mode": "dynamic", "mass": 1, "shapes": [ { "kind": "box", "halfExtents": [0.475, 0.475, 0.475] } ], "capabilities": { "jointConnectionType": "target", "tnt": { "fuseTicks": 5 }, "activation": "trigger", "canBeEnclosed": true } },
                { "partTypeId": 4, "name": "wheel", "mode": "dynamic", "mass": 0.5, "shapes": [ { "kind": "sphere", "radius": 0.33 } ], "capabilities": { "jointConnectionType": "target", "wheel": true } },
                { "partTypeId": 5, "name": "sandbag", "mode": "dynamic", "mass": 3, "shapes": [ { "kind": "sphere", "radius": 0.13 } ], "capabilities": { "jointConnectionType": "none", "attachment": { "direction": "up", "maxDistance": 0.5, "offset": [-0.15, -0.15, -0.01] } } },
                { "partTypeId": 6, "name": "balloon", "mode": "dynamic", "mass": 0.3, "shapes": [ { "kind": "sphere", "radius": 0.5 } ], "capabilities": { "jointConnectionType": "none", "attachment": { "direction": "down", "offset": [0, 0.5, 0], "distanceFactor": 1, "distanceOffset": -0.5, "pigDistanceBonus": 0.3 } } }
            ]
        }
        """;
}
