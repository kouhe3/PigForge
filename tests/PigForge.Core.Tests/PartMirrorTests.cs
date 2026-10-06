using PigForge.Core.Construction;
using PigForge.Core.Content;
using PigForge.Physics.Abstractions;

namespace PigForge.Core.Tests;

/// <summary>
/// The build pose's handedness (ADR-030, docs/specs/part-mirror.md): the original's
/// <c>BasePart.SetFlipped</c> turns a part 180 degrees about its own up axis inside its own frame,
/// which a float yaw cannot express -- so the pose is <c>yaw + mirror</c>, the content gates which
/// parts accept it (<c>m_autoAlign == FlipVertically</c>, the wing and tail families), and the
/// handedness outlives the pose a simulation rewrites.
/// </summary>
public sealed class PartMirrorTests
{
    private const uint PartWing = 1;
    private const uint PartBlock = 2;

    [Fact]
    public void AMirroredPoseIsThePlainPoseTurnedAboutThePartsOwnUpAxis()
    {
        (ConstructionRules rules, _) = CreateRules();
        ConstructionResult plain = rules.Place(PartWing, 0f, 0f, 0.6f, 1f, 0);
        ConstructionResult mirrored = rules.Place(PartWing, 3f, 0f, 0.6f, 1f, 0, mirrored: true);

        Assert.True(plain.IsSuccess);
        Assert.True(mirrored.IsSuccess, mirrored.Error.ToString());
        Assert.False(rules.IsMirrored(plain.Entity));
        Assert.True(rules.IsMirrored(mirrored.Entity));
        Assert.Equal(BuildPose.Rotation(0.6f, mirrored: false), TransformOf(rules, plain.Entity).Rotation);
        Assert.Equal(BuildPose.Rotation(0.6f, mirrored: true), TransformOf(rules, mirrored.Entity).Rotation);

        // In the part's own frame the mirror negates x and z and leaves y: that is the difference
        // between a mirror and a 180-degree turn of the shape, and it is what the wing's collider
        // offset and the sprite art both follow. Yaw 0 shows the part's own frame directly.
        PhysicsQuaternion straight = BuildPose.Rotation(0f, mirrored: true);
        PhysicsVector3 right = straight.Rotate(new PhysicsVector3(1f, 0f, 0f));
        PhysicsVector3 up = straight.Rotate(new PhysicsVector3(0f, 1f, 0f));
        PhysicsVector3 forward = straight.Rotate(new PhysicsVector3(0f, 0f, 1f));
        Assert.Equal(new PhysicsVector3(-1f, 0f, 0f), right);
        Assert.Equal(new PhysicsVector3(0f, 1f, 0f), up);
        Assert.Equal(new PhysicsVector3(0f, 0f, -1f), forward);

        // Yawed, the two parts' own axes point exactly opposite each other: Rz(yaw) * Ry(180)
        // applied to the x axis is the negation of Rz(yaw) applied to it.
        PhysicsVector3 plainRight = TransformOf(rules, plain.Entity).Rotation.Rotate(new PhysicsVector3(1f, 0f, 0f));
        PhysicsVector3 mirroredRight = TransformOf(rules, mirrored.Entity).Rotation.Rotate(new PhysicsVector3(1f, 0f, 0f));
        Assert.Equal(-plainRight.X, mirroredRight.X, 5);
        Assert.Equal(-plainRight.Y, mirroredRight.Y, 5);
    }

    [Fact]
    public void APartWithoutTheMirrorCapabilityRefusesAMirroredPose()
    {
        (ConstructionRules rules, _) = CreateRules();

        ConstructionResult refused = rules.Place(PartBlock, 0f, 0f, 0f, 1f, 0, mirrored: true);
        Assert.False(refused.IsSuccess);
        Assert.Equal(ConstructionError.PartNotMirrorable, refused.Error);

        // The same part placed plain, then rotated into the mirror: still refused.
        ConstructionResult block = rules.Place(PartBlock, 0f, 0f, 0f, 1f, 0);
        ConstructionResult rotate = rules.Rotate(block.Entity, 0f, 0, mirrored: true);
        Assert.Equal(ConstructionError.PartNotMirrorable, rotate.Error);
        Assert.False(rules.IsMirrored(block.Entity));
    }

    [Fact]
    public void TheMirrorSurvivesAFreezeAndADragIsNotForcedToClearIt()
    {
        (ConstructionRules rules, _) = CreateRules();
        ConstructionResult placed = rules.Place(PartWing, 0f, 0f, 0.25f, 1f, 0, mirrored: true);
        Assert.True(placed.IsSuccess, placed.Error.ToString());

        // A rotate that does not mention the handedness keeps it (the original's RotateClockwise
        // never clears m_flipped), while an absolute rotate sets whatever it is told.
        Assert.True(rules.Rotate(placed.Entity, 1.5f, 0).IsSuccess);
        Assert.True(rules.IsMirrored(placed.Entity));
        Assert.Equal(1.5f, BuildPose.YawOf(TransformOf(rules, placed.Entity).Rotation, mirrored: true), 5);

        ConstructionResult cleared = rules.Rotate(placed.Entity, 1.5f, 0, mirrored: false);
        Assert.True(cleared.IsSuccess, cleared.Error.ToString());
        Assert.False(rules.IsMirrored(placed.Entity));
        Assert.Equal(BuildPose.Rotation(1.5f, mirrored: false), TransformOf(rules, placed.Entity).Rotation);
        Assert.True(rules.Rotate(placed.Entity, 1.5f, 0, mirrored: true).IsSuccess);
        Assert.True(rules.IsMirrored(placed.Entity));

        // Start rewrites the transforms with physics poses; the handedness is a property of how the
        // part was built and must outlive that (the room publishes it as PGFS bit2). A frozen part
        // is no longer editable, so this is the last thing the test can ask it.
        rules.FreezeAll(new Dictionary<uint, (PhysicsVector3 Position, PhysicsQuaternion Rotation)>
        {
            [placed.Entity.Value] = (new PhysicsVector3(1f, 2f, 0f), PhysicsQuaternion.FromZAngle(0.9f)),
        });
        Assert.True(rules.IsMirrored(placed.Entity));
        Assert.Equal(PhysicsQuaternion.FromZAngle(0.9f), TransformOf(rules, placed.Entity).Rotation);
        Assert.Equal(ConstructionError.FrozenEntity, rules.Rotate(placed.Entity, 2f, 0, mirrored: false).Error);
    }

    [Fact]
    public void AMirroredFootprintIsTheXMirrorOfThePlainOne()
    {
        (ConstructionRules rules, PartContentLibrary content) = CreateRules();
        PartDefinition wing = content.GetPart(PartWing);

        PartFootprint plain = PartFootprint.ForPart(wing, 0f, 0f, 0f, mirrored: false, 1f);
        PartFootprint mirrored = PartFootprint.ForPart(wing, 0f, 0f, 0f, mirrored: true, 1f);

        // The wing's collider sits at x = -0.5 in its own frame, so the mirror puts it at +0.5 and
        // the connection geometry flips exactly: [-1.45, 0.45] becomes [-0.45, 1.45].
        (float minX, _, float maxX, _) = plain.Bounds();
        (float mirroredMinX, _, float mirroredMaxX, _) = mirrored.Bounds();
        Assert.Equal(-maxX, mirroredMinX, 4);
        Assert.Equal(-minX, mirroredMaxX, 4);

        // A quarter turn moves the mirror to the y axis, exactly as it moves the part.
        PartFootprint turned = PartFootprint.ForPart(wing, 0f, 0f, MathF.PI / 2f, mirrored: true, 1f);
        (_, float minY, _, float maxY) = turned.Bounds();
        Assert.Equal(-maxX, minY, 4);
        Assert.Equal(-minX, maxY, 4);

        // And the occupancy box follows: the wing is a single default cell, so only a part with an
        // off-centre cell box could show the difference -- which is why this uses the collider union.
        Assert.True(rules.Place(PartWing, 0f, 0f, 0f, 1f, 0).IsSuccess);
    }

    [Fact]
    public void TheMirrorEntersTheLayoutHash()
    {
        (ConstructionRules plainRules, _) = CreateRules();
        (ConstructionRules mirroredRules, _) = CreateRules();

        plainRules.Place(PartWing, 0f, 0f, 0.4f, 1f, 0, mirrored: false);
        mirroredRules.Place(PartWing, 0f, 0f, 0.4f, 1f, 0, mirrored: true);

        Assert.NotEqual(plainRules.ComputeLayoutHash(), mirroredRules.ComputeLayoutHash());
    }

    private static EntityTransform TransformOf(ConstructionRules rules, EntityId entity)
    {
        Assert.True(rules.TryGetTransform(entity, out EntityTransform transform));
        return transform;
    }

    private static (ConstructionRules Rules, PartContentLibrary Content) CreateRules()
    {
        PartContentLibrary content = new(PartContentParser.Parse("""
        {
            "format": "pigforge.part-content",
            "schemaVersion": 1,
            "contentVersion": "part-mirror-test-v1",
            "physics": { "maximumAngularSpeed": 7.0, "damping": { "linear": 0.2, "angular": 0.05 } },
            "parts": [
                { "partTypeId": 1, "name": "glider-wing", "mode": "dynamic", "mass": 1, "capabilities": { "mirror": true, "wing": { "liftConstant": 0.8 } }, "shapes": [
                    { "kind": "box", "halfExtents": [0.95, 0.3061, 0.75], "offset": [-0.5, -0.15, 0] } ] },
                { "partTypeId": 2, "name": "block", "mode": "dynamic", "mass": 1, "shapes": [ { "kind": "box", "halfExtents": [0.5, 0.5, 0.5] } ] }
            ]
        }
        """));
        EntityStore entities = new();
        return (new ConstructionRules(entities, new PartStore(entities), new TransformStore(entities), content), content);
    }
}
