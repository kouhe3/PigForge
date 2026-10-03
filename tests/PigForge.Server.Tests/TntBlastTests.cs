using PigForge.Core;
using PigForge.Core.Content;
using PigForge.Protocol;
using PigForge.Physics.Abstractions;
using PigForge.Physics.Bepu;
using PigForge.Server;

namespace PigForge.Server.Tests;

/// <summary>
/// Regression coverage for "TNT has no impact on a welded contraption". PigForge welds one build
/// into a single compound body, so a charge planted next to a frame shares that body with it:
/// the blast must drive the charge's own body too, and tearing the charge out of it must not
/// leave the split path rebinding an entity the room already destroyed. The original gives every
/// part its own Rigidbody and measures each target from the charge's own part transform
/// (<c>TNT.cs:236-252 AddExplosionForce</c>), which is what these numbers reproduce.
/// Real Bepu and a hand-built room; the content mirrors the shipped parts (wooden-block 1:
/// 0.5 half-extents, source weld, friction 0.7 / tnt 9: 0.475 half-extents, target weld, weak,
/// fuse 5, trigger switch), so the test cannot be broken by unrelated content edits.
/// </summary>
public sealed class TntBlastTests
{
    private const uint PartBlock = 1;
    private const uint PartTnt = 9;
    private const uint PartGround = 5;
    private const uint PlayerOne = 1;

    private static readonly GameplayZone Bounds = new(
        new PhysicsVector3(-100f, -50f, -50f),
        new PhysicsVector3(100f, 50f, 50f));

    [Fact]
    public void BlastDrivesTheWeldedBlockNextToTheChargeAndKeepsEveryLiveBlock()
    {
        BlastScene scene = RunBlastScene();

        SnapshotEntity near = Assert.Single(scene.Entities, entity => entity.EntityId == scene.NearBlock);
        SnapshotEntity far = Assert.Single(scene.Entities, entity => entity.EntityId == scene.FarBlock);

        // The charge is gone, and neither live block may vanish from the snapshot: the earlier
        // failure mode was the room throwing inside the split path, which wiped the whole frame.
        Assert.DoesNotContain(scene.Entities, entity => entity.EntityId == scene.Tnt);
        Assert.NotEqual(0u, near.PhysicsBodyId);
        Assert.NotEqual(0u, far.PhysicsBodyId);

        float nearVx = near.LinearVelocity.X;
        float farVx = far.LinearVelocity.X;
        Assert.True(nearVx > 1f, $"the block welded next to the charge must be thrown, got vx {nearVx}");
        Assert.True(farVx > 1f, $"the unwelded block must still be thrown, got vx {farVx}");
        // Both are one blast inside the 4 m radius, so their speeds stay within the same order of
        // magnitude: the welded block sits closer, so it may only be pushed harder, and the
        // pre-fix build left it at exactly zero. Measured: near 10.69 / far 6.12.
        Assert.InRange(nearVx / farVx, 0.25f, 4f);
    }

    [Fact]
    public void BlastSceneIsDeterministicAcrossRuns()
    {
        long first = RunBlastScene().Hash;
        long second = RunBlastScene().Hash;

        Assert.Equal(first, second);
        Assert.NotEqual(0, first);
    }

    /// <summary>
    /// A seam split destroys the compound and rebuilds its pieces: the rules layer must let go of
    /// the body it vacated, or the next blast aims an impulse at a body the world already
    /// destroyed (real Bepu throws <c>Impulse target body ... does not exist</c> and the room
    /// stops publishing). The first charge tears its own rig apart, the second fires after it.
    /// </summary>
    [Fact]
    public void BlastAfterASeamSplitDoesNotTargetTheVacatedBody()
    {
        using GameRoom room = CreateRoom();
        uint sequence = 0;
        uint left = Place(room, ref sequence, PartBlock, -1f, 0.5f);
        uint right = Place(room, ref sequence, PartBlock, 0f, 0.5f);
        uint first = Place(room, ref sequence, PartTnt, 1f, 0.5f);
        uint second = Place(room, ref sequence, PartTnt, -3f, 0.5f);
        Assert.True(room.Submit(new StartSimulationCommand(0, ++sequence, PlayerOne)).IsAccepted);
        Assert.True(room.Submit(new SetPartActiveCommand(0, ++sequence, PlayerOne, first, true)).IsAccepted);

        List<SnapshotEntity> entities = new();
        for (uint tick = 1; tick <= 40; tick++)
        {
            if (tick == 12)
            {
                Assert.True(room.Submit(new SetPartActiveCommand(0, ++sequence, PlayerOne, second, true)).IsAccepted);
            }

            room.Tick();
            entities = PublishEntities(room);
        }

        SnapshotEntity leftBlock = Assert.Single(entities, entity => entity.EntityId == left);
        SnapshotEntity rightBlock = Assert.Single(entities, entity => entity.EntityId == right);
        Assert.DoesNotContain(entities, entity => entity.EntityId == first);
        Assert.DoesNotContain(entities, entity => entity.EntityId == second);
        Assert.NotEqual(0u, leftBlock.PhysicsBodyId);
        Assert.NotEqual(0u, rightBlock.PhysicsBodyId);
    }

    /// <summary>
    /// The diagnosis's scene: the charge at x=-1, a wooden block welded 1.0 away (edge gap
    /// 0.025) and another block 3.0 away, all on the flat slab; the charge fires from its own
    /// switch and the scene runs through the blast and the tick that applies its impulse.
    /// </summary>
    private static BlastScene RunBlastScene()
    {
        using GameRoom room = CreateRoom();
        uint sequence = 0;
        uint tnt = Place(room, ref sequence, PartTnt, -1f, 0.5f);
        uint near = Place(room, ref sequence, PartBlock, 0f, 0.5f);
        uint far = Place(room, ref sequence, PartBlock, 2f, 0.5f);
        Assert.True(room.Submit(new StartSimulationCommand(0, ++sequence, PlayerOne)).IsAccepted);
        Assert.True(room.Submit(new SetPartActiveCommand(0, ++sequence, PlayerOne, tnt, true)).IsAccepted);

        room.RunTicks(10);
        return new BlastScene(PublishEntities(room), tnt, near, far, room.ComputeStateHash());
    }

    private static GameRoom CreateRoom()
    {
        GameRoom room = new(GameRoomOptions.Create(
            new PartContentLibrary(PartContentParser.Parse(ContentJson)),
            () => new BepuPhysicsWorld(new PhysicsVector3(0f, -9.81f, 0f)),
            new GameplayConfig(
                GoalZone: new GameplayZone(new PhysicsVector3(500f, 500f, 500f), new PhysicsVector3(501f, 501f, 501f)),
                MapBounds: Bounds,
                TntBlastRadius: 4f,
                TntBlastImpulse: 25f,
                TntIgniteImpactSpeed: 5f,
                ObjectivesEnabled: false),
            sandboxMode: true));
        room.SetupFromLevel(new LevelContentDocument(
            ContentVersion: "tnt-blast-test-v1",
            GoalZone: new GameplayZone(new PhysicsVector3(500f, 500f, 500f), new PhysicsVector3(501f, 501f, 501f)),
            MapBounds: Bounds,
            Spawns: new[] { new LevelSpawnDefinition(PartGround, new PhysicsVector3(0f, -0.5f, 0f)) }));
        return room;
    }

    private static uint Place(GameRoom room, ref uint sequence, uint partTypeId, float x, float y)
    {
        CommandOutcome outcome = room.Submit(new PlacePartCommand(
            Tick: 0,
            Sequence: ++sequence,
            PlayerId: PlayerOne,
            PartTypeId: partTypeId,
            PositionX: x,
            PositionY: y,
            Angle: 0f,
            Scale: 1f));
        Assert.True(outcome.IsAccepted, $"place {partTypeId} at ({x},{y}): {outcome.Status}/{outcome.Error}");
        return outcome.EntityId;
    }

    private static List<SnapshotEntity> PublishEntities(GameRoom room)
    {
        byte[] buffer = new byte[SnapshotFrame.GetMaxByteCount(room.MaxSnapshotEntityCount)];
        Assert.True(room.TryPublishSnapshot(buffer, out int bytesWritten));
        Assert.True(SnapshotFrame.TryDecodeHeader(buffer.AsSpan(0, bytesWritten), out _, out SnapshotFrameReader reader));
        List<SnapshotEntity> entities = new();
        while (reader.TryReadEntity(out SnapshotEntity entity))
        {
            entities.Add(entity);
        }

        return entities;
    }

    private sealed record BlastScene(
        IReadOnlyList<SnapshotEntity> Entities,
        uint Tnt,
        uint NearBlock,
        uint FarBlock,
        long Hash);

    private const string ContentJson = """
    {
        "format": "pigforge.part-content",
        "schemaVersion": 1,
        "contentVersion": "tnt-blast-test-v1",
        "parts": [
            { "partTypeId": 1, "name": "wooden-block", "mode": "dynamic", "mass": 1.0, "material": { "restitution": 0, "friction": 0.7 }, "capabilities": { "jointConnectionType": "source", "jointConnectionStrength": "normal" }, "shapes": [ { "kind": "box", "halfExtents": [0.5, 0.5, 0.5] } ] },
            { "partTypeId": 9, "name": "tnt", "mode": "dynamic", "mass": 1.0, "material": { "restitution": 0, "friction": 0.7 }, "capabilities": { "jointConnectionType": "target", "jointConnectionStrength": "weak", "tnt": { "fuseTicks": 5 }, "activation": "trigger" }, "shapes": [ { "kind": "box", "halfExtents": [0.475, 0.475, 0.475] } ] },
            { "partTypeId": 5, "name": "ground", "mode": "static", "mass": 0, "material": { "restitution": 0, "friction": 0.9 }, "shapes": [ { "kind": "box", "halfExtents": [10, 0.5, 10] } ] }
        ]
    }
    """;
}
