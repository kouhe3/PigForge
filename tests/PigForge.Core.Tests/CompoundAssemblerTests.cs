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
    private const uint PartRamp = 4;
    private const uint PartStrengthLeft = 11;
    private const uint PartStrengthRight = 12;

    [Fact]
    public void TheSeamThresholdScalesWithBothEndsDeclaredStrength()
    {
        // Normal-Normal keeps the caller's fallback: the original's ConnectionStrength factor
        // cancels against the Normal pair (Contraption.cs:1541-1543, :2268).
        Assert.Equal(CompoundAssembler.DefaultSeamBreakImpulse, SeamBreak(null, null), precision: 4);
        // Weak 125 / High 600 / HighlyExtreme 1200 against Normal 250 in a 2 x 250 denominator.
        Assert.Equal(CompoundAssembler.DefaultSeamBreakImpulse * 0.5f, SeamBreak("weak", "weak"), precision: 4);
        Assert.Equal(CompoundAssembler.DefaultSeamBreakImpulse * 1.7f, SeamBreak("normal", "high"), precision: 4);
        Assert.Equal(CompoundAssembler.DefaultSeamBreakImpulse * 2.4f, SeamBreak("high", "high"), precision: 4);
        Assert.Equal(CompoundAssembler.DefaultSeamBreakImpulse * 4.8f, SeamBreak("highlyExtreme", "highlyExtreme"), precision: 4);
    }

    [Fact]
    public void TheCatalogGivesMetalWeldsTheOriginalStrengthRatio()
    {
        const uint WoodenBlock = 1;
        const uint MetalBox = 18;
        PartContentLibrary content = PartContentLibrary.Load(FindRepositoryFile("content/parts.json"));

        // Wood-wood (Normal 250 each) keeps the fallback; metal-metal (High 600 each) is 2.4x.
        // This is the wooden-vs-metal difference the extraction exists to reproduce.
        // Both parts are frames, so the pair is two bodies held by a weld rather than one compound
        // with a seam (docs/specs/weld-compliance.md) — the strength maths is the same.
        Assert.Equal(CompoundAssembler.DefaultSeamBreakImpulse, CatalogFrameBreak(content, WoodenBlock, WoodenBlock), precision: 4);
        Assert.Equal(CompoundAssembler.DefaultSeamBreakImpulse * 2.4f, CatalogFrameBreak(content, MetalBox, MetalBox), precision: 4);
    }

    private static float CatalogFrameBreak(PartContentLibrary content, uint leftPart, uint rightPart)
    {
        EntityStore entities = new();
        ConstructionRules rules = new(entities, new PartStore(entities), new TransformStore(entities), content);
        EntityId left = rules.Place(leftPart, 0f, 0f, 0f, 1f, 0).Entity;
        EntityId right = rules.Place(rightPart, 1.1f, 0f, 0f, 1f, 0).Entity;

        CompoundAssembly assembly = CompoundAssembler.Assemble(new[] { left, right }, rules, content);
        Assert.Equal(2, assembly.Clusters.Count);
        Assert.All(assembly.Clusters, cluster => Assert.False(cluster.IsMerged));

        return Assert.Single(assembly.Welds).BreakImpulse;
    }

    [Fact]
    public void TwoAdjacentFramesAreTwoBodiesHeldByOneWeld()
    {
        // The original never merges a frame pair: each frame keeps its own body and the two are
        // joined by a real joint with all six degrees of freedom locked (Contraption.cs:1507-1546),
        // which is what lets a frame chain bend (docs/specs/weld-compliance.md §4.2).
        const uint WoodenBlock = 1;
        PartContentLibrary content = PartContentLibrary.Load(FindRepositoryFile("content/parts.json"));
        (ConstructionRules rules, EntityId left, EntityId right) = PlaceCatalogPair(content, WoodenBlock, WoodenBlock, 1.1f);

        CompoundAssembly assembly = CompoundAssembler.Assemble(new[] { left, right }, rules, content);

        Assert.Equal(2, assembly.Clusters.Count);
        Assert.All(assembly.Clusters, cluster => Assert.False(cluster.IsMerged));
        CompoundWeld weld = Assert.Single(assembly.Welds);
        Assert.Equal(left, weld.Left);
        Assert.Equal(right, weld.Right);
        // The original's anchors: half the vector to the other part's origin, in each part's own
        // frame (Contraption.AddFixedJoint), i.e. the same world midpoint seen from both ends.
        Assert.Equal(0.55f, weld.AnchorInLeft.X, precision: 4);
        Assert.Equal(0f, weld.AnchorInLeft.Y, precision: 4);
        Assert.Equal(-0.55f, weld.AnchorInRight.X, precision: 4);
        Assert.Equal(CompoundAssembler.DefaultSeamBreakImpulse, weld.BreakImpulse, precision: 4);
    }

    [Fact]
    public void AFrameChainIsOneBodyPerFrameAndOneWeldPerJoint()
    {
        const uint WoodenBlock = 1;
        PartContentLibrary content = PartContentLibrary.Load(FindRepositoryFile("content/parts.json"));
        EntityStore entities = new();
        ConstructionRules rules = new(entities, new PartStore(entities), new TransformStore(entities), content);
        List<EntityId> chain = new();
        for (int index = 0; index < 8; index++)
        {
            chain.Add(rules.Place(WoodenBlock, index * 1f, 0f, 0f, 1f, 0).Entity);
        }

        CompoundAssembly assembly = CompoundAssembler.Assemble(chain, rules, content);

        Assert.Equal(8, assembly.Clusters.Count);
        Assert.All(assembly.Clusters, cluster => Assert.False(cluster.IsMerged));
        Assert.Equal(7, assembly.Welds.Count);
        for (int index = 0; index < assembly.Welds.Count; index++)
        {
            Assert.Equal(chain[index], assembly.Welds[index].Left);
            Assert.Equal(chain[index + 1], assembly.Welds[index].Right);
        }
    }

    [Fact]
    public void AFrameStillMergesWithThePartItEncloses()
    {
        // Enclosure welds a part to its frame whatever the joint capability says (Frame.cs:44-49),
        // and that pair is not two frames: it stays one compound body with one seam, exactly as
        // before the frame weld split.
        const uint WoodenBlock = 1;
        const uint Engine = 8;
        PartContentLibrary content = PartContentLibrary.Load(FindRepositoryFile("content/parts.json"));
        EntityStore entities = new();
        ConstructionRules rules = new(entities, new PartStore(entities), new TransformStore(entities), content);
        EntityId frame = rules.Place(WoodenBlock, 0f, 4f, 0f, 1f, 0).Entity;
        EntityId engine = rules.Place(Engine, 0f, 4f, 0f, 1f, 0).Entity;
        Assert.Equal(frame, rules.EnclosedBy(engine));

        CompoundAssembly assembly = CompoundAssembler.Assemble(new[] { frame, engine }, rules, content);

        CompoundCluster cluster = Assert.Single(assembly.Clusters);
        Assert.True(cluster.IsMerged);
        Assert.Empty(assembly.Welds);
        Assert.Equal(2, cluster.Members.Count);
    }

    [Fact]
    public void TheFrameWeldComplianceIsTheFittedPair()
    {
        // The calibration the original's own chain measurement produced (docs/specs/weld-compliance.md
        // §4.4). tests/PigForge.Physics.Tests/WeldComplianceTests.cs runs the eight-frame chain with
        // the same pair: changing either value invalidates that acceptance, so this pins them.
        Assert.Equal(20.5f, CompoundAssembler.FrameWeldSpringFrequency);
        Assert.Equal(1f, CompoundAssembler.FrameWeldSpringDampingRatio);
    }

    [Fact]
    public void FrameWeldsAreDeterministicAcrossRuns()
    {
        Assert.Equal(RunFrameChain(), RunFrameChain());
    }

    private static long RunFrameChain()
    {
        const uint WoodenBlock = 1;
        PartContentLibrary content = PartContentLibrary.Load(FindRepositoryFile("content/parts.json"));
        EntityStore entities = new();
        ConstructionRules rules = new(entities, new PartStore(entities), new TransformStore(entities), content);
        List<EntityId> chain = new();
        for (int index = 0; index < 6; index++)
        {
            chain.Add(rules.Place(WoodenBlock, index * 1f, 0f, 0f, 1f, 0).Entity);
        }

        long hash = rules.ComputeLayoutHash();
        CompoundAssembly assembly = CompoundAssembler.Assemble(chain, rules, content);
        foreach (CompoundCluster cluster in assembly.Clusters)
        {
            hash = unchecked((hash * 31) + cluster.ComputeHash());
        }

        foreach (CompoundWeld weld in assembly.Welds)
        {
            hash = unchecked((hash * 31) + (int)weld.Left.Value);
            hash = unchecked((hash * 31) + (int)weld.Right.Value);
            hash = unchecked((hash * 31) + weld.BreakImpulse.GetHashCode());
            hash = unchecked((hash * 31) + weld.AnchorInLeft.GetHashCode());
            hash = unchecked((hash * 31) + weld.AnchorInRight.GetHashCode());
        }

        return hash;
    }

    private static (ConstructionRules Rules, EntityId Left, EntityId Right) PlaceCatalogPair(
        PartContentLibrary content,
        uint leftPart,
        uint rightPart,
        float spacing)
    {
        EntityStore entities = new();
        ConstructionRules rules = new(entities, new PartStore(entities), new TransformStore(entities), content);
        EntityId left = rules.Place(leftPart, 0f, 0f, 0f, 1f, 0).Entity;
        EntityId right = rules.Place(rightPart, spacing, 0f, 0f, 1f, 0).Entity;
        return (rules, left, right);
    }

    private static float SeamBreak(string? leftStrength, string? rightStrength)
    {
        (ConstructionRules rules, PartContentLibrary content) = CreateRulesWithStrengths(leftStrength, rightStrength);
        EntityId left = rules.Place(PartStrengthLeft, 0f, 0f, 0f, 1f, 0).Entity;
        EntityId right = rules.Place(PartStrengthRight, 1.1f, 0f, 0f, 1f, 0).Entity;

        CompoundCluster cluster = Assert.Single(CompoundAssembler.Assemble(new[] { left, right }, rules, content).Clusters);

        return Assert.Single(cluster.Seams).BreakImpulse;
    }

    /// <summary>Two adjacent weldable parts whose declared strengths the test chooses; null
    /// leaves the key out, which is how a part with no extracted strength reads.</summary>
    private static (ConstructionRules Rules, PartContentLibrary Content) CreateRulesWithStrengths(string? leftStrength, string? rightStrength)
    {
        string left = leftStrength is null ? string.Empty : $", \"jointConnectionStrength\": \"{leftStrength}\"";
        string right = rightStrength is null ? string.Empty : $", \"jointConnectionStrength\": \"{rightStrength}\"";
        PartContentLibrary content = new(PartContentParser.Parse($$"""
        {
            "format": "pigforge.part-content",
            "schemaVersion": 1,
            "contentVersion": "compound-strength-test-v1",
            "physics": { "maximumAngularSpeed": 7.0, "damping": { "linear": 0.2, "angular": 0.05 } },
            "parts": [
                { "partTypeId": 11, "name": "left", "mode": "dynamic", "mass": 1, "shapes": [ { "kind": "box", "halfExtents": [0.5, 0.5, 0.5] } ], "capabilities": { "jointConnectionType": "source"{{left}} } },
                { "partTypeId": 12, "name": "right", "mode": "dynamic", "mass": 1, "shapes": [ { "kind": "box", "halfExtents": [0.5, 0.5, 0.5] } ], "capabilities": { "jointConnectionType": "target"{{right}} } }
            ]
        }
        """));
        EntityStore entities = new();

        return (new ConstructionRules(entities, new PartStore(entities), new TransformStore(entities), content), content);
    }

    [Fact]
    public void AdjacentDynamicBoxesMergeIntoOneClusterWithOneSeam()
    {
        (ConstructionRules rules, PartContentLibrary content) = CreateRules();
        EntityId left = rules.Place(PartBlock, 0f, 0f, 0f, 1f, 0).Entity;
        EntityId right = rules.Place(PartBlock, 1.1f, 0f, 0f, 1f, 0).Entity;

        IReadOnlyList<CompoundCluster> clusters = CompoundAssembler.Assemble(new[] { left, right }, rules, content).Clusters;

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

        IReadOnlyList<CompoundCluster> clusters = CompoundAssembler.Assemble(new[] { left, right }, rules, content).Clusters;

        Assert.Equal(2, clusters.Count);
        Assert.All(clusters, cluster => Assert.False(cluster.IsMerged));
    }

    [Fact]
    public void StaticPartsNeverMerge()
    {
        (ConstructionRules rules, PartContentLibrary content) = CreateRules();
        EntityId ground = rules.Place(PartGround, 0f, -1f, 0f, 1f, 0).Entity;
        EntityId block = rules.Place(PartBlock, 0f, 0.1f, 0f, 1f, 0).Entity;

        IReadOnlyList<CompoundCluster> clusters = CompoundAssembler.Assemble(new[] { ground, block }, rules, content).Clusters;

        Assert.Equal(2, clusters.Count);
        Assert.All(clusters, cluster => Assert.False(cluster.IsMerged));
    }

    [Fact]
    public void StaticPartAtFractionalPositionKeepsPrimitiveBody()
    {
        (ConstructionRules rules, PartContentLibrary content) = CreateRules();
        // The sandbox level spawns ramp planks at fractional coordinates (terrain-v1). The
        // volume-weighted centroid used to round-trip such a position through multiply and
        // divide, and the residue pushed the lone static member onto the compound path —
        // which the physics contract has no support for, so the room failed to load.
        EntityId ramp = rules.Place(PartRamp, -18.125f, -1.758f, 0.25f, 1f, 0).Entity;

        CompoundCluster cluster = Assert.Single(CompoundAssembler.Assemble(new[] { ramp }, rules, content).Clusters);
        BodyDefinition body = cluster.CreateBodyDefinition(content, rules);

        Assert.Equal(PhysicsBodyMode.Static, body.Mode);
        BoxShapeDefinition shape = Assert.IsType<BoxShapeDefinition>(Assert.Single(body.Shapes));
        Assert.Equal(6f, shape.HalfExtentX, precision: 4);
        Assert.Equal(0.25f, shape.HalfExtentY, precision: 4);
        Assert.Equal(-18.125f, body.Position.X, precision: 4);
        Assert.Equal(-1.758f, body.Position.Y, precision: 4);
        Assert.Equal(PhysicsQuaternion.FromZAngle(0.25f), body.Rotation);
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
        CompoundCluster merged = Assert.Single(CompoundAssembler.Assemble(new[] { left, right }, rules, content).Clusters);

        IReadOnlyList<CompoundCluster> pieces = CompoundAssembler.SplitAlongSeam(merged, merged.Seams[0]);

        Assert.Equal(2, pieces.Count);
        Assert.All(pieces, piece => Assert.False(piece.IsMerged));
        Assert.Equal(left, pieces[0].Members[0].Entity);
        Assert.Equal(right, pieces[1].Members[0].Entity);
        Assert.Equal(0f, pieces[0].WorldPosition.X, precision: 4);
        Assert.Equal(1.1f, pieces[1].WorldPosition.X, precision: 4);
    }

    [Fact]
    public void SplitAlongSeamsSeversEverySeamOfOneMember()
    {
        // The boxing glove's punch destroys every FixedJoint of the part it hits
        // (SpringBoxingGlove.cs:224-262). For a body PigForge merged several parts into, that is
        // every seam the target carries: the middle block of a chain of three is cut out on both
        // sides, exactly as a weld-joined part loses every weld.
        (ConstructionRules rules, PartContentLibrary content) = CreateRules();
        EntityId left = rules.Place(PartBlock, 0f, 0f, 0f, 1f, 0).Entity;
        EntityId middle = rules.Place(PartBlock, 1.1f, 0f, 0f, 1f, 0).Entity;
        EntityId right = rules.Place(PartBlock, 2.2f, 0f, 0f, 1f, 0).Entity;
        CompoundCluster merged = Assert.Single(CompoundAssembler.Assemble(new[] { left, middle, right }, rules, content).Clusters);
        Assert.Equal(2, merged.Seams.Count);

        List<CompoundSeam> both = merged.Seams.Where(seam => seam.Left == middle || seam.Right == middle).ToList();
        IReadOnlyList<CompoundCluster> pieces = CompoundAssembler.SplitAlongSeams(merged, both);

        Assert.Equal(3, pieces.Count);
        Assert.All(pieces, piece => Assert.False(piece.IsMerged));
        Assert.Equal(new[] { left, middle, right }, pieces.Select(piece => piece.Members[0].Entity).ToArray());

        // One end's only seam leaves the other two together, ordered by their first member like
        // the one-seam split this generalises.
        List<CompoundSeam> oneSide = merged.Seams.Where(seam => seam.Left == right || seam.Right == right).ToList();
        IReadOnlyList<CompoundCluster> halves = CompoundAssembler.SplitAlongSeams(merged, oneSide);
        Assert.Equal(2, halves.Count);
        Assert.Equal(2, halves[0].Members.Count);
        Assert.Single(halves[1].Members);
        Assert.Equal(right, halves[1].Members[0].Entity);

        // One seam of a chain is a bridge, exactly as the one-seam split has always behaved, and a
        // seam named twice is deduplicated rather than counted twice.
        Assert.Equal(2, CompoundAssembler.SplitAlongSeams(merged, new[] { merged.Seams[0] }).Count);
        Assert.Equal(2, CompoundAssembler.SplitAlongSeams(merged, new[] { merged.Seams[0], merged.Seams[0] }).Count);

        // A seam the cluster does not own — or an empty set — is rejected, not silently ignored.
        Assert.Throws<ArgumentException>(() => CompoundAssembler.SplitAlongSeams(halves[1], new[] { merged.Seams[0] }));
        Assert.Throws<ArgumentException>(() => CompoundAssembler.SplitAlongSeams(merged, Array.Empty<CompoundSeam>()));
    }

    [Fact]
    public void AMergedClusterCarriesTheAggregatedMemberMaterial()
    {
        // The original gives every collider its own PhysicMaterial, so a PigForge body with several
        // members folds them with Unity's own pair rule (ADR-016): the strongest restitution
        // (ADR-010 decision 5), the highest-priority combine mode, and that mode applied over the
        // member coefficients.
        PartContentLibrary materialContent = new(PartContentParser.Parse("""
        {
            "format": "pigforge.part-content",
            "schemaVersion": 1,
            "contentVersion": "compound-material-test-v1",
            "physics": { "maximumAngularSpeed": 7.0, "damping": { "linear": 0.2, "angular": 0.05 } },
            "parts": [
                { "partTypeId": 1, "name": "frame", "mode": "dynamic", "mass": 1,
                  "material": { "restitution": 0.2, "friction": 0.7 },
                  "capabilities": { "jointConnectionType": "source" },
                  "shapes": [ { "kind": "box", "halfExtents": [0.5, 0.5, 0.5] } ] },
                { "partTypeId": 2, "name": "tyre", "mode": "dynamic", "mass": 1,
                  "material": { "restitution": 0.5, "friction": 0.5, "frictionCombine": "multiply" },
                  "capabilities": { "jointConnectionType": "target" },
                  "shapes": [ { "kind": "box", "halfExtents": [0.5, 0.5, 0.5] } ] },
                { "partTypeId": 3, "name": "plank", "mode": "dynamic", "mass": 1,
                  "material": { "restitution": 0, "friction": 0.8 },
                  "capabilities": { "jointConnectionType": "target" },
                  "shapes": [ { "kind": "box", "halfExtents": [0.5, 0.5, 0.5] } ] }
            ]
        }
        """));
        EntityStore materialEntities = new();
        ConstructionRules materialRules = new(materialEntities, new PartStore(materialEntities), new TransformStore(materialEntities), materialContent);

        EntityId frame = materialRules.Place(1, 0.5f, 0.5f, 0f, 1f, 0).Entity;
        EntityId tyre = materialRules.Place(2, 1.5f, 0.5f, 0f, 1f, 0).Entity;
        CompoundCluster mixed = Assert.Single(CompoundAssembler.Assemble(new[] { frame, tyre }, materialRules, materialContent).Clusters);
        BodyDefinition mixedBody = mixed.CreateBodyDefinition(materialContent, materialRules);

        // The tyre's Multiply outranks the frame's Average, so it wins and multiplies the two
        // coefficients; the strongest restitution survives.
        Assert.Equal(0.5f, mixedBody.Material.Restitution, precision: 5);
        Assert.Equal(FrictionCombine.Multiply, mixedBody.Material.FrictionCombine);
        Assert.Equal(0.35f, mixedBody.Material.Friction, precision: 5);

        // Two Average members keep the historical average of their coefficients.
        EntityId plainFrame = materialRules.Place(1, 10.5f, 0.5f, 0f, 1f, 0).Entity;
        EntityId plank = materialRules.Place(3, 11.5f, 0.5f, 0f, 1f, 0).Entity;
        CompoundCluster average = Assert.Single(CompoundAssembler.Assemble(new[] { plainFrame, plank }, materialRules, materialContent).Clusters);
        BodyDefinition averageBody = average.CreateBodyDefinition(materialContent, materialRules);

        Assert.Equal(0.2f, averageBody.Material.Restitution, precision: 5);
        Assert.Equal(FrictionCombine.Average, averageBody.Material.FrictionCombine);
        Assert.Equal(0.75f, averageBody.Material.Friction, precision: 5);
    }

    [Fact]
    public void AdjacentSphereAndBoxMergeIntoOneCluster()
    {
        (ConstructionRules rules, PartContentLibrary content) = CreateRules();
        EntityId block = rules.Place(PartBlock, 0f, 0f, 0f, 1f, 0).Entity;
        EntityId wheel = rules.Place(PartWheel, 0f, -1f, 0f, 1f, 0).Entity;

        CompoundCluster cluster = Assert.Single(CompoundAssembler.Assemble(new[] { block, wheel }, rules, content).Clusters);

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
        CompoundAssembly assembly = CompoundAssembler.Assemble(new[] { frame, wheel }, rules, content);
        Assert.Equal(2, assembly.Clusters.Count);
        Assert.All(assembly.Clusters, cluster => Assert.False(cluster.IsMerged));

        // The axle is the tire centre, not the part origin: the wooden wheel's tire is a
        // r = 0.33 sphere centred 0.2057 below its origin, and hinging at the origin made the
        // tire orbit the joint like a cam instead of rolling.
        CompoundHinge hinge = Assert.Single(assembly.Hinges);
        Assert.Equal(wheel, hinge.Wheel);
        Assert.Equal(frame, hinge.Parent);
        Assert.Equal(0.0106f, hinge.LocalAxle.X, precision: 4);
        Assert.Equal(-0.2057f, hinge.LocalAxle.Y, precision: 4);

        // The wheel body sits on that axle and carries one centred tire sphere; its support box
        // rides the frame body, which does not spin, so an axle mount cannot sweep into it.
        CompoundCluster wheelCluster = assembly.Clusters.Single(cluster => cluster.Members[0].Entity == wheel);
        Assert.Equal(-1f + hinge.LocalAxle.Y, wheelCluster.WorldPosition.Y, precision: 4);
        BodyDefinition wheelBody = wheelCluster.CreateBodyDefinition(content, rules);
        Assert.Equal(wheelCluster.WorldPosition.Y, wheelBody.Position.Y, precision: 6);
        SphereShapeDefinition tire = Assert.IsType<SphereShapeDefinition>(Assert.Single(wheelBody.Shapes));
        Assert.Equal(0.33f, tire.Radius, precision: 4);

        CompoundCluster frameCluster = assembly.Clusters.Single(cluster => cluster.Members[0].Entity == frame);
        CompoundShapeDefinition frameBody = Assert.IsType<CompoundShapeDefinition>(
            Assert.Single(frameCluster.CreateBodyDefinition(content, rules).Shapes));
        Assert.Equal(2, frameBody.Children.Count);
        Assert.Contains(frameBody.Children, child => MathF.Abs(((BoxShapeDefinition)child.Shape).HalfExtentX - 0.2f) < 1e-4f);
        Assert.Contains(frameBody.Children, child => child.Shape.Kind == PhysicsShapeKind.Box && MathF.Abs(((BoxShapeDefinition)child.Shape).HalfExtentX - 0.5f) < 1e-4f);
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
        CompoundCluster cluster = Assert.Single(CompoundAssembler.Assemble(new[] { a, b, c }, rules, content).Clusters);
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
        CompoundCluster cluster = Assert.Single(CompoundAssembler.Assemble(placed, rules, content).Clusters);

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
            "physics": { "maximumAngularSpeed": 7.0, "damping": { "linear": 0.2, "angular": 0.05 } },
            "parts": [
                { "partTypeId": 1, "name": "block", "mode": "dynamic", "mass": 1, "shapes": [ { "kind": "box", "halfExtents": [0.5, 0.5, 0.5] } ], "capabilities": { "jointConnectionType": "source" } },
                { "partTypeId": 2, "name": "ground", "mode": "static", "mass": 0, "shapes": [ { "kind": "box", "halfExtents": [4, 0.5, 4] } ] },
                { "partTypeId": 3, "name": "wheel", "mode": "dynamic", "mass": 0.5, "shapes": [ { "kind": "sphere", "radius": 0.45 } ], "capabilities": { "jointConnectionType": "target" } },
                { "partTypeId": 4, "name": "ramp", "mode": "static", "mass": 0, "shapes": [ { "kind": "box", "halfExtents": [6, 0.25, 1] } ] }
            ]
        }
        """));
        EntityStore entities = new();
        return (new ConstructionRules(entities, new PartStore(entities), new TransformStore(entities), content), content);
    }
}
