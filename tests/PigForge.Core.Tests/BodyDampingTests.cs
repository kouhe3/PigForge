using PigForge.Core.Construction;
using PigForge.Core.Content;
using PigForge.Physics.Abstractions;

namespace PigForge.Core.Tests;

/// <summary>
/// The original's per-rigidbody defaults: Unity's <c>Rigidbody.drag</c> / <c>angularDrag</c>
/// (0.2 / 0.05 from <c>BasePart.EnsureRigidbody</c>, overridden by the wing, tail, balloon,
/// sandbag and king-pig classes) and the project-wide <c>Physics.defaultMaxAngularSpeed</c>
/// (<c>ProjectSettings/DynamicsManager.asset</c>, 7 rad/s, which no script and no prefab
/// overrides). Extracted by <c>tools/bple-damping</c> and measured on the original's own editor
/// (Unity 2021.3.45f2) by <c>unity/PigForge.WeldProbe</c>'s body-defaults probe.
/// </summary>
public sealed class BodyDampingTests
{
    /// <summary>The pair <c>BasePart.EnsureRigidbody</c> writes on every part (BasePart.cs:1200-1201).</summary>
    private const float DefaultLinear = 0.2f;
    private const float DefaultAngular = 0.05f;

    /// <summary>The original's project-wide floor is the same pair; the content declares it once.</summary>
    private static readonly string Physics = $@"""physics"": {{ ""maximumAngularSpeed"": 7.0, ""damping"": {{ ""linear"": {DefaultLinear}, ""angular"": {DefaultAngular} }} }}";

    private const uint PartBlock = 11;
    private const uint PartHeavy = 12;

    [Fact]
    public void ADynamicPartInheritsTheDocumentsDampingAndAStaticPartTakesNone()
    {
        PartContentLibrary content = new(PartContentParser.Parse($$"""
        {
            "format": "pigforge.part-content",
            "schemaVersion": 1,
            "contentVersion": "damping-inheritance-test-v1",
            {{Physics}},
            "parts": [
                { "partTypeId": 11, "name": "block", "mode": "dynamic", "mass": 1, "shapes": [ { "kind": "box", "halfExtents": [0.5, 0.5, 0.5] } ] },
                { "partTypeId": 12, "name": "override", "mode": "dynamic", "mass": 1, "damping": { "linear": 2.0, "angular": 0.5 }, "shapes": [ { "kind": "box", "halfExtents": [0.5, 0.5, 0.5] } ] },
                { "partTypeId": 13, "name": "ground", "mode": "static", "mass": 0, "shapes": [ { "kind": "box", "halfExtents": [4, 0.5, 4] } ] }
            ]
        }
        """));

        BodyDefinition inherited = content.CreateBodyDefinition(PartBlock, PhysicsVector3.Zero, PhysicsQuaternion.Identity);
        Assert.Equal(DefaultLinear, inherited.LinearDamping, precision: 6);
        Assert.Equal(DefaultAngular, inherited.AngularDamping, precision: 6);
        Assert.Equal(7f, inherited.MaximumAngularSpeed, precision: 6);

        BodyDefinition overridden = content.CreateBodyDefinition(PartHeavy, PhysicsVector3.Zero, PhysicsQuaternion.Identity);
        Assert.Equal(2f, overridden.LinearDamping, precision: 6);
        Assert.Equal(0.5f, overridden.AngularDamping, precision: 6);

        // A static level piece is not a rigidbody in the original at all, so it carries nothing --
        // the parser refuses a damping key on one (see below) and the body takes the neutral value.
        BodyDefinition ground = content.CreateBodyDefinition(13, PhysicsVector3.Zero, PhysicsQuaternion.Identity);
        Assert.Equal(0f, ground.LinearDamping);
        Assert.Equal(0f, ground.AngularDamping);
    }

    [Fact]
    public void TheShippedContentCarriesTheOriginalsDampingAndAngularClamp()
    {
        PartContentLibrary content = PartContentLibrary.Load(FindRepositoryFile("content/parts.json"));

        // The project default, declared once: Unity clamps every original rigidbody's angular speed
        // magnitude to 7 rad/s (measured: a body seeded at 100 rad/s reads 7 after one step, and a
        // 1000 rad/s cap runs to 119.9999 -- unity/PigForge.WeldProbe BodyDefaultsProbe).
        Assert.Equal(7f, content.Document.Physics.MaximumAngularSpeed, precision: 6);
        Assert.Equal(DefaultLinear, content.Document.Physics.Damping.Linear, precision: 6);
        Assert.Equal(DefaultAngular, content.Document.Physics.Damping.Angular, precision: 6);

        // The five classes that override BasePart's pair, one catalogued part each.
        Assert.Equal(new PartDamping(1f, 0.2f), content.DampingOf(content.GetPart(31)));   // Wings.cs:99-100
        Assert.Equal(new PartDamping(1f, 0.2f), content.DampingOf(content.GetPart(33)));   // Tail.cs:52-53
        Assert.Equal(new PartDamping(2f, 0.5f), content.DampingOf(content.GetPart(10)));   // Balloon.cs:130-131
        Assert.Equal(new PartDamping(1f, 10f), content.DampingOf(content.GetPart(21)));    // Sandbag.cs:133-134
        Assert.Equal(new PartDamping(0.5f, 1f), content.DampingOf(content.GetPart(24)));   // KingPig.cs:79-81

        // Everything else inherits the pair, and every part resolves to one of the extracted values.
        Assert.Equal(new PartDamping(DefaultLinear, DefaultAngular), content.DampingOf(content.GetPart(1)));
        Assert.Equal(new PartDamping(DefaultLinear, DefaultAngular), content.DampingOf(content.GetPart(8)));
        foreach (PartDefinition part in content.Document.Parts.Where(part => part.Mode == PhysicsBodyMode.Dynamic))
        {
            PartDamping damping = content.DampingOf(part);
            Assert.Contains(damping, new[]
            {
                new PartDamping(DefaultLinear, DefaultAngular),
                new PartDamping(1f, 0.2f),
                new PartDamping(2f, 0.5f),
                new PartDamping(1f, 10f),
                new PartDamping(0.5f, 1f),
            });
        }
    }

    [Fact]
    public void MergedMembersFoldTheirDampingByMass()
    {
        PartContentLibrary content = new(PartContentParser.Parse($$"""
        {
            "format": "pigforge.part-content",
            "schemaVersion": 1,
            "contentVersion": "damping-fold-test-v1",
            {{Physics}},
            "parts": [
                { "partTypeId": 11, "name": "light", "mode": "dynamic", "mass": 1, "damping": { "linear": 1.0, "angular": 0.2 },
                  "shapes": [ { "kind": "box", "halfExtents": [0.5, 0.5, 0.5] } ], "capabilities": { "jointConnectionType": "source" } },
                { "partTypeId": 12, "name": "heavy", "mode": "dynamic", "mass": 3,
                  "shapes": [ { "kind": "box", "halfExtents": [0.5, 0.5, 0.5] } ], "capabilities": { "jointConnectionType": "target" } }
            ]
        }
        """));

        EntityStore entities = new();
        ConstructionRules rules = new(entities, new PartStore(entities), new TransformStore(entities), content);
        EntityId light = rules.Place(PartBlock, 0f, 0f, 0f, 1f, 0).Entity;
        EntityId heavy = rules.Place(PartHeavy, 1.1f, 0f, 0f, 1f, 0).Entity;

        CompoundAssembly assembly = CompoundAssembler.Assemble(new[] { light, heavy }, rules, content);
        CompoundCluster cluster = Assert.Single(assembly.Clusters);
        Assert.True(cluster.IsMerged);

        // One rigid body cannot carry two drags, so the pair is folded by mass -- the same weight
        // the body's own mass uses: (1*1.0 + 3*0.2) / 4 and (1*0.2 + 3*0.05) / 4. The original
        // keeps two bodies with their own drags (tools/bple-damping); this is the honest fold.
        Assert.Equal(0.4f, cluster.Damping.Linear, precision: 4);
        Assert.Equal(0.0875f, cluster.Damping.Angular, precision: 4);
        Assert.Equal(0.4f, cluster.CreateBodyDefinition(content, rules).LinearDamping, precision: 4);

        // A lone member is not folded with anything: it keeps its own value exactly.
        EntityStore other = new();
        ConstructionRules otherRules = new(other, new PartStore(other), new TransformStore(other), content);
        EntityId alone = otherRules.Place(PartBlock, 0f, 0f, 0f, 1f, 0).Entity;
        CompoundCluster single = Assert.Single(CompoundAssembler.Assemble(new[] { alone }, otherRules, content).Clusters);
        Assert.Equal(1f, single.Damping.Linear, precision: 6);
        Assert.Equal(0.2f, single.Damping.Angular, precision: 6);
    }

    [Fact]
    public void AWheelHingeKeepsTheMountsDampingOffTheParentBody()
    {
        // The wheel's non-spinning mount rides the parent body (ADR-009). It is not a member there,
        // so it contributes neither mass nor damping: the parent keeps the frame's own drag, and
        // the wheel body keeps the wheel's.
        PartContentLibrary content = new(PartContentParser.Parse($$"""
        {
            "format": "pigforge.part-content",
            "schemaVersion": 1,
            "contentVersion": "damping-hinge-test-v1",
            {{Physics}},
            "parts": [
                { "partTypeId": 11, "name": "frame", "mode": "dynamic", "mass": 1,
                  "shapes": [ { "kind": "box", "halfExtents": [0.5, 0.5, 0.5] } ],
                  "capabilities": { "jointConnectionType": "source", "jointConnectionDirection": "any", "canEnclose": true } },
                { "partTypeId": 12, "name": "wheel", "mode": "dynamic", "mass": 0.5, "damping": { "linear": 0.9, "angular": 0.8 },
                  "shapes": [
                      { "kind": "box", "halfExtents": [0.2, 0.32, 0.5], "offset": [0, 0.1702, 0] },
                      { "kind": "sphere", "radius": 0.33, "offset": [0.0106, -0.2057, 0] }
                  ],
                  "capabilities": { "wheel": true, "jointConnectionType": "target", "jointConnectionDirection": "up" } }
            ]
        }
        """));

        EntityStore entities = new();
        ConstructionRules rules = new(entities, new PartStore(entities), new TransformStore(entities), content);
        EntityId frame = rules.Place(PartBlock, 0f, 0f, 0f, 1f, 0).Entity;
        EntityId wheel = rules.Place(PartHeavy, 0f, -1f, 0f, 1f, 0).Entity;

        CompoundAssembly assembly = CompoundAssembler.Assemble(new[] { frame, wheel }, rules, content);
        CompoundHinge hinge = Assert.Single(assembly.Hinges);
        Assert.Equal(wheel, hinge.Wheel);

        CompoundCluster wheelCluster = assembly.Clusters.Single(cluster => cluster.Members[0].Entity == wheel);
        Assert.Equal(0.9f, wheelCluster.Damping.Linear, precision: 6);
        Assert.Equal(0.8f, wheelCluster.Damping.Angular, precision: 6);

        CompoundCluster frameCluster = assembly.Clusters.Single(cluster => cluster.Members[0].Entity == frame);
        Assert.NotEmpty(frameCluster.Attachments);
        Assert.Equal(DefaultLinear, frameCluster.Damping.Linear, precision: 6);
        Assert.Equal(DefaultAngular, frameCluster.Damping.Angular, precision: 6);
    }

    [Fact]
    public void TheParserRefusesAStaticPartWithDamping()
    {
        PartContentException error = Assert.Throws<PartContentException>(() => PartContentParser.Parse($$"""
        {
            "format": "pigforge.part-content",
            "schemaVersion": 1,
            "contentVersion": "damping-static-test-v1",
            {{Physics}},
            "parts": [
                { "partTypeId": 11, "name": "ground", "mode": "static", "mass": 0, "damping": { "linear": 0.2, "angular": 0.05 },
                  "shapes": [ { "kind": "box", "halfExtents": [4, 0.5, 4] } ] }
            ]
        }
        """));

        Assert.Contains(error.Errors, message => message.Contains("static part has no rigidbody", StringComparison.Ordinal));
    }

    [Fact]
    public void TheParserRefusesADocumentWithoutThePhysicsDefaults()
    {
        // Without the block the whole world would silently fall back to zero damping, which is the
        // pre-G86 behaviour: a hard error keeps the extraction load-bearing.
        PartContentException error = Assert.Throws<PartContentException>(() => PartContentParser.Parse("""
        {
            "format": "pigforge.part-content",
            "schemaVersion": 1,
            "contentVersion": "damping-missing-test-v1",
            "parts": [
                { "partTypeId": 11, "name": "block", "mode": "dynamic", "mass": 1, "shapes": [ { "kind": "box", "halfExtents": [0.5, 0.5, 0.5] } ] }
            ]
        }
        """));

        Assert.Contains(error.Errors, message => message.Contains("root: missing required property 'physics'", StringComparison.Ordinal));
    }

    [Fact]
    public void TheParserRefusesAnUnknownDampingKey()
    {
        PartContentException error = Assert.Throws<PartContentException>(() => PartContentParser.Parse($$"""
        {
            "format": "pigforge.part-content",
            "schemaVersion": 1,
            "contentVersion": "damping-unknown-key-test-v1",
            {{Physics}},
            "parts": [
                { "partTypeId": 11, "name": "block", "mode": "dynamic", "mass": 1, "damping": { "linear": 0.2, "angular": 0.05, "quadratic": 1 },
                  "shapes": [ { "kind": "box", "halfExtents": [0.5, 0.5, 0.5] } ] }
            ]
        }
        """));

        Assert.Contains(error.Errors, message => message.Contains("damping", StringComparison.Ordinal));
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
}
