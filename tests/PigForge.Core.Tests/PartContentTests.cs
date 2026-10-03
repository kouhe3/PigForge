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
        Assert.Equal(267, library.Document.Parts.Count);
        Assert.NotNull(library.GetPart(1));
    }

    [Fact]
    public void RepositoryContentDeclaresNoWheelSuspensionYet()
    {
        PartContentLibrary library = PartContentLibrary.Load(FindRepositoryFile("content/parts.json"));

        // tools/bple-springs found exactly one prefab in the original that declares a wheel
        // spring: Part_MotorWheel_08_SET (= the OffRoadWheel, BasePart.cs:509), an IN extension
        // part GameData.m_customParts never listed, so the catalog has no part for it (ADR-012
        // decision 2) and no wheel may claim a suspension. The capability itself is exercised by
        // the parser cases above and by WheelSuspensionRoomTests' fixture content.
        Assert.DoesNotContain(library.Document.Parts, part => part.Capabilities?.HasSuspension == true);
        Assert.True(library.GetPart(7).Capabilities!.IsWheel);
    }

    [Fact]
    public void ImportedOriginalVariantsCarryTheirCuratedOverrides()
    {
        PartContentLibrary library = PartContentLibrary.Load(FindRepositoryFile("content/parts.json"));

        // Skins copy their base entry: the balloon group is one example of many.
        PartDefinition balloonVariant = library.Document.Parts.Single(part => part.Name == "balloon-v02");
        Assert.Equal(10u, balloonVariant.VariantOf);
        Assert.Equal(library.GetPart(10).Mass, balloonVariant.Mass);
        Assert.Equal(library.GetPart(10).Capabilities, balloonVariant.Capabilities);

        // Curated parameter variants: heavy sandbag (original 5 vs 1.1), motorised small
        // wheel (MotorWheel prefab), light-bearing metal frame, alien bellows.
        Assert.Equal(13.6364f, library.Document.Parts.Single(part => part.Name == "sandbag-v05").Mass);
        PartCapabilities smallMotorWheel = library.Document.Parts.Single(part => part.Name == "small-wheel-v08").Capabilities!;
        Assert.True(smallMotorWheel.IsWheel);
        Assert.Equal(2.2f, smallMotorWheel.MotorThrustPerTick);
        Assert.Equal(PartActivation.Toggle, smallMotorWheel.Activation);
        Assert.Equal(2.14f, library.Document.Parts.Single(part => part.Name == "metal-box-v11").Capabilities!.LightRadius);
        Assert.Equal(32f, library.Document.Parts.Single(part => part.Name == "bellows-v07").Capabilities!.BellowsBoostImpulse);

        // The marker kicker (original customPartIndex 3) is inert: no detacher capability.
        // Every mapped part now carries a joint capability, so inertness is asserted on the
        // gameplay roles rather than on the object being absent.
        PartCapabilities markerKicker = library.Document.Parts.Single(part => part.Name == "detacher-v4").Capabilities!;
        Assert.False(markerKicker.IsDetacher);
        Assert.False(markerKicker.IsWheel);
        Assert.False(markerKicker.IsPig);
        Assert.Null(markerKicker.MotorThrustPerTick);
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

    [Fact]
    public void ActivationCapabilityParsesToggleAndTrigger()
    {
        PartContentDocument document = PartContentParser.Parse("""
        {
            "format": "pigforge.part-content",
            "schemaVersion": 1,
            "contentVersion": "test-content-v1",
            "parts": [
                { "partTypeId": 8, "name": "engine", "mode": "dynamic", "mass": 1, "capabilities": { "motor": { "thrustPerTick": 2, "directionX": 1 }, "activation": "toggle" }, "shapes": [ { "kind": "box", "halfExtents": [1, 1, 1] } ] },
                { "partTypeId": 13, "name": "rocket", "mode": "dynamic", "mass": 1, "capabilities": { "rocket": { "thrustPerTick": 4, "directionX": 1, "durationTicks": 30 }, "activation": "trigger" }, "shapes": [ { "kind": "box", "halfExtents": [1, 1, 1] } ] }
            ]
        }
        """);

        Assert.Equal(PartActivation.Toggle, document.Parts[0].Capabilities!.Activation);
        Assert.Equal(PartActivation.Trigger, document.Parts[1].Capabilities!.Activation);
        Assert.Null(PartContentParser.Parse(SinglePartJson).Parts[0].Capabilities);
    }

    [Fact]
    public void UnknownActivationValueIsRejected()
    {
        AssertRejected(
            """{ "partTypeId": 8, "name": "engine", "mode": "dynamic", "mass": 1, "capabilities": { "motor": { "thrustPerTick": 2, "directionX": 1 }, "activation": "latch" }, "shapes": [ { "kind": "box", "halfExtents": [1, 1, 1] } ] }""",
            "activation");
    }

    [Fact]
    public void JointEnclosureAndAttachmentCapabilitiesParse()
    {
        PartContentDocument document = PartContentParser.Parse("""
        {
            "format": "pigforge.part-content",
            "schemaVersion": 1,
            "contentVersion": "test-content-v1",
            "parts": [
                { "partTypeId": 1, "name": "frame", "mode": "dynamic", "mass": 1, "capabilities": { "jointConnectionType": "source", "canEnclose": true }, "shapes": [ { "kind": "box", "halfExtents": [0.5, 0.5, 0.5] } ] },
                { "partTypeId": 2, "name": "pig", "mode": "dynamic", "mass": 1, "capabilities": { "jointConnectionType": "none", "pig": true }, "shapes": [ { "kind": "sphere", "radius": 0.42 } ] },
                { "partTypeId": 3, "name": "sandbag", "mode": "dynamic", "mass": 3, "capabilities": { "jointConnectionType": "none", "attachment": { "direction": "up", "maxDistance": 0.5, "offset": [-0.15, -0.15, -0.01] } }, "shapes": [ { "kind": "sphere", "radius": 0.13 } ] },
                { "partTypeId": 4, "name": "balloon", "mode": "dynamic", "mass": 0.3, "capabilities": { "jointConnectionType": "none", "attachment": { "direction": "down", "offset": [0, 0.5, 0], "distanceFactor": 1, "distanceOffset": -0.5, "pigDistanceBonus": 0.3 } }, "shapes": [ { "kind": "sphere", "radius": 0.5 } ] },
                { "partTypeId": 5, "name": "offroad-wheel", "mode": "dynamic", "mass": 1, "capabilities": { "wheel": true, "suspension": { "stiffness": 50, "damper": 5, "restOffset": 0 } }, "shapes": [ { "kind": "sphere", "radius": 0.9 } ] }
            ]
        }
        """);

        PartCapabilities frame = document.Parts[0].Capabilities!;
        Assert.Equal(JointConnectionType.Source, frame.JointConnectionType);
        Assert.True(frame.CanEnclose);
        Assert.False(frame.CanBeEnclosed);
        Assert.Null(frame.Attachment);

        // Absence of a joint capability key defaults to none, and CanBeEnclosed is derived.
        PartCapabilities pig = document.Parts[1].Capabilities!;
        Assert.Equal(JointConnectionType.None, pig.JointConnectionType);
        Assert.True(pig.CanBeEnclosed);
        Assert.Null(pig.Attachment);

        PartAttachment sandbag = document.Parts[2].Capabilities!.Attachment!;
        Assert.Equal(AttachmentDirection.Up, sandbag.Direction);
        Assert.Equal(0.5f, sandbag.MaxDistance);
        Assert.Equal(new PhysicsVector3(-0.15f, -0.15f, -0.01f), sandbag.Offset);
        Assert.Null(sandbag.DistanceFactor);

        PartAttachment balloon = document.Parts[3].Capabilities!.Attachment!;
        Assert.Equal(AttachmentDirection.Down, balloon.Direction);
        Assert.Equal(1f, balloon.DistanceFactor);
        Assert.Equal(-0.5f, balloon.DistanceOffset);
        Assert.Equal(0.3f, balloon.PigDistanceBonus);
        Assert.Equal(new PhysicsVector3(0f, 0.5f, 0f), balloon.Offset);

        // The suspension is all-or-nothing: stiffness, damper and rest offset together.
        PartCapabilities sprungWheel = document.Parts[4].Capabilities!;
        Assert.True(sprungWheel.HasSuspension);
        Assert.Equal(50f, sprungWheel.Suspension!.Stiffness);
        Assert.Equal(5f, sprungWheel.Suspension.Damper);
        Assert.Equal(0f, sprungWheel.Suspension.RestOffset);
        Assert.Null(frame.Suspension);
    }

    [Fact]
    public void UnknownCapabilityKeysAreStillRejected()
    {
        AssertRejected(
            """{ "partTypeId": 1, "name": "block", "mode": "dynamic", "mass": 1, "capabilities": { "jointConnection": "source" }, "shapes": [ { "kind": "box", "halfExtents": [1, 1, 1] } ] }""",
            "unknown property");
        AssertRejected(
            """{ "partTypeId": 1, "name": "block", "mode": "dynamic", "mass": 1, "capabilities": { "attachment": { "direction": "up", "rope": 1 } }, "shapes": [ { "kind": "box", "halfExtents": [1, 1, 1] } ] }""",
            "unknown property");
    }

    [Theory]
    [InlineData("{ \"jointConnectionType\": \"both\" }", "jointConnectionType")]
    [InlineData("{ \"jointConnectionType\": true }", "jointConnectionType")]
    [InlineData("{ \"canEnclose\": \"yes\" }", "canEnclose")]
    [InlineData("{ \"attachment\": \"up\" }", "attachment")]
    [InlineData("{ \"attachment\": { \"maxDistance\": 0.5 } }", "direction")]
    [InlineData("{ \"attachment\": { \"direction\": \"sideways\" } }", "direction")]
    [InlineData("{ \"attachment\": { \"direction\": \"up\" } }", "maxDistance or distanceFactor")]
    [InlineData("{ \"attachment\": { \"direction\": \"up\", \"maxDistance\": 0.5, \"distanceFactor\": 1 } }", "mutually exclusive")]
    [InlineData("{ \"attachment\": { \"direction\": \"up\", \"maxDistance\": -1 } }", "maxDistance")]
    [InlineData("{ \"suspension\": 50 }", "suspension")]
    [InlineData("{ \"suspension\": { \"damper\": 5, \"restOffset\": 0 } }", "stiffness")]
    [InlineData("{ \"suspension\": { \"stiffness\": 0, \"damper\": 5, \"restOffset\": 0 } }", "stiffness")]
    [InlineData("{ \"suspension\": { \"stiffness\": 50, \"damper\": -1, \"restOffset\": 0 } }", "damper")]
    [InlineData("{ \"suspension\": { \"stiffness\": 50, \"damper\": 5 } }", "restOffset")]
    [InlineData("{ \"suspension\": { \"stiffness\": 50, \"damper\": 5, \"restOffset\": 0, \"limit\": 0 } }", "unknown property")]
    public void InvalidJointEnclosureAndAttachmentCapabilitiesAreRejected(string capabilities, string expectedErrorFragment)
    {
        AssertRejected(
            $$"""{ "partTypeId": 1, "name": "block", "mode": "dynamic", "mass": 1, "capabilities": {{capabilities}}, "shapes": [ { "kind": "box", "halfExtents": [1, 1, 1] } ] }""",
            expectedErrorFragment);
    }

    [Fact]
    public void RepositoryContentMarksSwitchableParts()
    {
        PartContentLibrary library = PartContentLibrary.Load(FindRepositoryFile("content/parts.json"));

        Assert.Equal(PartActivation.Toggle, library.GetPart(8).Capabilities!.Activation);
        Assert.Equal(PartActivation.Toggle, library.GetPart(39).Capabilities!.Activation);
        Assert.Equal(PartActivation.Trigger, library.GetPart(13).Capabilities!.Activation);
        Assert.Equal(PartActivation.Trigger, library.GetPart(10).Capabilities!.Activation);

        // Part 1 is the wooden frame: its only capability is its joint role (spec §2.1) — it is
        // not switchable. Level geometry still carries no capabilities at all.
        PartCapabilities frame = library.GetPart(1).Capabilities!;
        Assert.Equal(JointConnectionType.Source, frame.JointConnectionType);
        Assert.True(frame.CanEnclose);
        Assert.False(frame.CanBeEnclosed);
        Assert.Equal(PartActivation.None, frame.Activation);
        Assert.Null(library.GetPart(2).Capabilities);
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
    public void VariantPartsParseAndReferenceTheirBase()
    {
        PartContentDocument document = PartContentParser.Parse("""
        {
            "format": "pigforge.part-content",
            "schemaVersion": 1,
            "contentVersion": "test-content-v1",
            "parts": [
                { "partTypeId": 9, "name": "tnt", "mode": "dynamic", "mass": 1, "shapes": [ { "kind": "box", "halfExtents": [0.35, 0.35, 0.35] } ] },
                { "partTypeId": 47, "name": "tnt-nitro", "variantOf": 9, "variantName": "Nitro TNT", "mode": "dynamic", "mass": 1, "shapes": [ { "kind": "box", "halfExtents": [0.35, 0.35, 0.35] } ] }
            ]
        }
        """);

        PartDefinition variant = document.Parts[1];
        Assert.Equal(9u, variant.VariantOf);
        Assert.Equal("Nitro TNT", variant.VariantName);
        Assert.Null(document.Parts[0].VariantOf);
    }

    [Theory]
    [InlineData(47, "cannot be a variant of itself")]
    public void InvalidVariantBaseIsRejected(uint basePartTypeId, string expectedErrorFragment)
    {
        string json = $$"""
        {
            "format": "pigforge.part-content",
            "schemaVersion": 1,
            "contentVersion": "test-content-v1",
            "parts": [
                { "partTypeId": 9, "name": "tnt", "mode": "dynamic", "mass": 1, "shapes": [ { "kind": "box", "halfExtents": [0.35, 0.35, 0.35] } ] },
                { "partTypeId": 47, "name": "tnt-nitro", "variantOf": {{basePartTypeId}}, "mode": "dynamic", "mass": 1, "shapes": [ { "kind": "box", "halfExtents": [0.35, 0.35, 0.35] } ] }
            ]
        }
        """;

        PartContentException exception = Assert.Throws<PartContentException>(() => PartContentParser.Parse(json));
        Assert.Contains(exception.Errors, error => error.Contains(expectedErrorFragment, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void VariantChainsAreRejected()
    {
        string json = """
        {
            "format": "pigforge.part-content",
            "schemaVersion": 1,
            "contentVersion": "test-content-v1",
            "parts": [
                { "partTypeId": 9, "name": "tnt", "mode": "dynamic", "mass": 1, "shapes": [ { "kind": "box", "halfExtents": [0.35, 0.35, 0.35] } ] },
                { "partTypeId": 47, "name": "tnt-nitro", "variantOf": 9, "mode": "dynamic", "mass": 1, "shapes": [ { "kind": "box", "halfExtents": [0.35, 0.35, 0.35] } ] },
                { "partTypeId": 48, "name": "tnt-bomb", "variantOf": 47, "mode": "dynamic", "mass": 1, "shapes": [ { "kind": "box", "halfExtents": [0.35, 0.35, 0.35] } ] }
            ]
        }
        """;

        PartContentException exception = Assert.Throws<PartContentException>(() => PartContentParser.Parse(json));
        Assert.Contains(exception.Errors, error => error.Contains("itself a variant", StringComparison.OrdinalIgnoreCase));
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

    [Fact]
    public void ReadsTheJointConnectionStrength()
    {
        PartContentDocument document = PartContentParser.Parse("""
        {
            "format": "pigforge.part-content",
            "schemaVersion": 1,
            "contentVersion": "test-content-v1",
            "parts": [
                { "partTypeId": 1, "name": "frame", "mode": "dynamic", "mass": 1, "capabilities": { "jointConnectionType": "source", "jointConnectionStrength": "high" }, "shapes": [ { "kind": "box", "halfExtents": [0.5, 0.5, 0.5] } ] }
            ]
        }
        """);

        Assert.Equal(JointConnectionStrength.High, document.Parts[0].Capabilities!.JointConnectionStrength);
    }

    [Fact]
    public void ReadsTheFrictionCombineMode()
    {
        PartContentDocument document = PartContentParser.Parse("""
        {
            "format": "pigforge.part-content",
            "schemaVersion": 1,
            "contentVersion": "test-content-v1",
            "parts": [
                { "partTypeId": 1, "name": "tyre", "mode": "dynamic", "mass": 1, "material": { "restitution": 0, "friction": 0.025, "frictionCombine": "multiply" }, "shapes": [ { "kind": "box", "halfExtents": [0.5, 0.5, 0.5] } ] }
            ]
        }
        """);

        Assert.Equal(0.025f, document.Parts[0].Friction, precision: 4);
        Assert.Equal(FrictionCombine.Multiply, document.Parts[0].FrictionCombine);
    }

    [Fact]
    public void AMaterialWithoutACombineModeReadsAsAverage()
    {
        Assert.Equal(FrictionCombine.Average, PartContentParser.Parse(SinglePartJson).Parts[0].FrictionCombine);
    }

    [Fact]
    public void RejectsAnUnknownFrictionCombine()
    {
        AssertRejected(
            """{ "partTypeId": 1, "name": "block", "mode": "dynamic", "mass": 1, "material": { "restitution": 0, "friction": 0.5, "frictionCombine": "blend" }, "shapes": [ { "kind": "box", "halfExtents": [0.5, 0.5, 0.5] } ] }""",
            "material");
    }

    [Fact]
    public void TheRealContentGivesWheelsTheirTyreMaterial()
    {
        PartContentDocument document = PartContentParser.Parse(File.ReadAllText(FindRepositoryFile("content/parts.json")));
        Dictionary<uint, PartDefinition> parts = document.Parts.ToDictionary(part => part.PartTypeId);

        // A wheel's physics body is its tyre spheres (ADR-009) and its support box rides the
        // parent body, so the tyre's Multiply material is what touches the ground — not the hub's
        // Average 0.7 that the old "material most colliders use" rule picked.
        foreach (uint wheel in new uint[] { 7, 14, 15, 16, 17 })
        {
            Assert.Equal(FrictionCombine.Multiply, parts[wheel].FrictionCombine);
            Assert.InRange(parts[wheel].Friction, 0.02f, 0.06f);
        }

        Assert.Equal(0.05f, parts[15].Friction, precision: 3); // the wooden wheel's own tyre
        Assert.Equal(FrictionCombine.Average, parts[1].FrictionCombine); // wooden block
        Assert.Equal(0.7f, parts[1].Friction, precision: 4);
    }

    [Fact]
    public void TheRealContentCarriesTheExtractedJointStrength()
    {
        PartContentDocument document = PartContentParser.Parse(File.ReadAllText(FindRepositoryFile("content/parts.json")));
        Dictionary<uint, JointConnectionStrength> strength = document.Parts.ToDictionary(
            part => part.PartTypeId,
            part => part.Capabilities?.JointConnectionStrength ?? JointConnectionStrength.None);

        // Extracted coverage: every part that maps to a prefab carries one; only the three
        // hand-authored static level parts have no prefab to extract from.
        Assert.Equal(264, strength.Count(entry => entry.Value != JointConnectionStrength.None));
        Assert.All(new uint[] { 2, 5, 6 }, id => Assert.Equal(JointConnectionStrength.None, strength[id]));

        // The user-visible ordering this exists for: wooden (Normal) is weaker than metal
        // (High) — wood-wood 1.0x, wood-metal 1.7x, metal-metal 2.4x.
        Assert.Equal(JointConnectionStrength.Normal, strength[1]);
        Assert.Equal(JointConnectionStrength.High, strength[18]);
    }

    [Fact]
    public void APartWithCapabilitiesButNoStrengthReadsAsNone()
    {
        PartContentDocument document = PartContentParser.Parse("""
        {
            "format": "pigforge.part-content",
            "schemaVersion": 1,
            "contentVersion": "test-content-v1",
            "parts": [
                { "partTypeId": 1, "name": "frame", "mode": "dynamic", "mass": 1, "capabilities": { "jointConnectionType": "source" }, "shapes": [ { "kind": "box", "halfExtents": [0.5, 0.5, 0.5] } ] }
            ]
        }
        """);

        Assert.Equal(JointConnectionStrength.None, document.Parts[0].Capabilities!.JointConnectionStrength);
    }

    [Theory]
    [InlineData("highlyextreme")]
    [InlineData("1")]
    public void RejectsAnUnknownJointConnectionStrength(string value)
    {
        AssertRejected(
            $$"""{ "partTypeId": 1, "name": "frame", "mode": "dynamic", "mass": 1, "capabilities": { "jointConnectionStrength": "{{value}}" }, "shapes": [ { "kind": "box", "halfExtents": [0.5, 0.5, 0.5] } ] }""",
            "jointConnectionStrength");
    }

    [Fact]
    public void TheRealContentCarriesTheExtractedCellBoxes()
    {
        PartContentDocument document = PartContentParser.Parse(File.ReadAllText(FindRepositoryFile("content/parts.json")));
        Dictionary<uint, GridCellBox> boxes = document.Parts
            .Where(part => part.GridBox is not null)
            .ToDictionary(part => part.PartTypeId, part => part.GridBox!);

        // tools/bple-grid: of the original's 343 part prefabs, 332 declare one cell at the origin
        // (0..0, 0..0) and only the KingPig/GoldenPig families declare the 3x2 box x[-1, 1] y[0, 1].
        // Content omits the default, so exactly the seven imported king-pig entries carry a box --
        // the six skins map to KingPig_02..07 and the GoldenPig prefabs have no imported part.
        Assert.Equal(new uint[] { 24, 221, 222, 223, 224, 225, 226 }, boxes.Keys.OrderBy(id => id).ToArray());
        Assert.All(boxes.Values, box => Assert.Equal(new GridCellBox(-1, 1, 0, 1), box));

        // A part whose prefab declares the default carries no key at all, not a copy of it.
        Assert.Null(document.Parts.Single(part => part.PartTypeId == 1).GridBox);
        Assert.Null(document.Parts.Single(part => part.PartTypeId == 37).GridBox);
    }

    [Fact]
    public void ADeclaredCellBoxReadsBackExactly()
    {
        PartContentDocument document = PartContentParser.Parse("""
        {
            "format": "pigforge.part-content",
            "schemaVersion": 1,
            "contentVersion": "test-content-v1",
            "parts": [
                { "partTypeId": 1, "name": "king-pig", "mode": "dynamic", "mass": 1, "gridBox": { "minX": -1, "maxX": 1, "minY": 0, "maxY": 1 }, "shapes": [ { "kind": "box", "halfExtents": [1.05, 0.9, 0.5] } ] }
            ]
        }
        """);

        GridCellBox box = Assert.IsType<GridCellBox>(document.Parts[0].GridBox);
        Assert.Equal(new GridCellBox(-1, 1, 0, 1), box);
        Assert.Equal(3, box.Width);
        Assert.Equal(2, box.Height);
        Assert.Equal(0f, box.CentreX);
        Assert.Equal(0.5f, box.CentreY);
    }

    [Fact]
    public void APartWithoutACellBoxStandsOnTheOriginalDefault()
    {
        // 332 of the original's 343 prefabs declare 0..0, 0..0; the parser keeps that as "absent"
        // so "the prefab declares the default" and "the prefab declares nothing" stay distinct.
        Assert.Null(PartContentParser.Parse(SinglePartJson).Parts[0].GridBox);
        Assert.Equal(new GridCellBox(0, 0, 0, 0), GridCellBox.Single);
    }

    [Theory]
    [InlineData("{ \"minX\": 1, \"maxX\": -1, \"minY\": 0, \"maxY\": 1 }", "minX")]
    [InlineData("{ \"minX\": 0, \"maxX\": 0, \"minY\": 2, \"maxY\": 1 }", "minY")]
    [InlineData("{ \"minX\": 0.5, \"maxX\": 1, \"minY\": 0, \"maxY\": 1 }", "minX")]
    [InlineData("{ \"minX\": 0, \"maxX\": 0, \"minY\": 0 }", "maxY")]
    [InlineData("{ \"minX\": 0, \"maxX\": 0, \"minY\": 0, \"maxY\": 1, \"minZ\": 0 }", "minZ")]
    public void RejectsAMalformedCellBox(string box, string expectedErrorFragment)
    {
        AssertRejected(
            $$"""{ "partTypeId": 1, "name": "block", "mode": "dynamic", "mass": 1, "gridBox": {{box}}, "shapes": [ { "kind": "box", "halfExtents": [0.5, 0.5, 0.5] } ] }""",
            expectedErrorFragment);
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
