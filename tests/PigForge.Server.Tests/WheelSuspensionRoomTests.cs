using PigForge.Core;
using PigForge.Core.Content;
using PigForge.Physics.Abstractions;
using PigForge.Physics.Bepu;
using PigForge.Protocol;
using PigForge.Server;

namespace PigForge.Server.Tests;

/// <summary>
/// The elastic wheel attachment through the whole production chain: parsed content capability →
/// <see cref="PartCapabilities.Suspension"/> → <c>GameRoom.BindWheelHinges</c>' conversion to the
/// solver's frequency/damping ratio → the real Bepu backend. The content here is a fixture, but
/// the code path is the shipped one, and the spring numbers are the ones
/// <c>tools/bple-springs</c> extracts from the original (50 N/m, 5 N*s/m, rest offset 0 — the
/// OffRoadWheel's linear-limit spring, OffRoadWheel.cs:202-220). The deflection itself is
/// measured at the joint level in PigForge.Physics.Tests/WheelSuspensionTests (a fixture's
/// free-fall and impulse phases are easier to control there than a whole room's).
/// <para>
/// The extracted wheel is not in <c>content/parts.json</c>: its prefab is an IN extension part
/// the catalog never imported, and its art comes from the original's runtime IN sprite system,
/// so cataloguing it is a separate decision. The fixture therefore carries the same numbers by
/// hand and the physics/server assertions stay about the mechanism.
/// </para>
/// </summary>
public sealed class WheelSuspensionRoomTests
{
    private const uint PartFrame = 1;
    private const uint PartWheel = 2;
    private const uint PartSprungWheel = 3;
    private const uint PartGround = 4;

    /// <summary>The extracted original spring: OffRoadWheel m_springStiffness 50 N/m.</summary>
    private const float SpringStiffness = 50f;

    private const float Gravity = 9.81f;

    private static readonly GameplayZone FarBounds = new(
        new PhysicsVector3(-1000f, -1000f, -1000f),
        new PhysicsVector3(1000f, 1000f, 1000f));

    [Fact]
    public void SandboxSprungWheelRunIsDeterministic()
    {
        long first = RunSprungCart();
        long second = RunSprungCart();
        Assert.Equal(first, second);
    }

    private static long RunSprungCart()
    {
        using GameRoom room = CreateRoom();
        uint player = 1;
        uint sequence = 0;
        PlaceCart(room, ref sequence, player, PartSprungWheel);
        Assert.True(room.Submit(new StartSimulationCommand(0, ++sequence, player)).IsAccepted);
        for (int tick = 0; tick < 240; tick++)
        {
            room.Tick();
        }

        return room.ComputeStateHash();
    }

    /// <summary>A two-frame beam on two wheels, placed just above the fixture's ground.</summary>
    private static (uint LeftFrame, uint LeftWheel, uint RightFrame, uint RightWheel) PlaceCart(
        GameRoom room,
        ref uint sequence,
        uint player,
        uint wheelPart)
    {
        uint leftFrame = Place(room, ref sequence, player, PartFrame, 0f, 1.35f);
        uint rightFrame = Place(room, ref sequence, player, PartFrame, 1f, 1.35f);
        uint leftWheel = Place(room, ref sequence, player, wheelPart, 0f, 0.35f);
        uint rightWheel = Place(room, ref sequence, player, wheelPart, 1f, 0.35f);
        return (leftFrame, leftWheel, rightFrame, rightWheel);
    }

    private static GameRoom CreateRoom()
    {
        GameRoom room = new(GameRoomOptions.Create(
            new PartContentLibrary(PartContentParser.Parse(ContentJson)),
            () => new BepuPhysicsWorld(new PhysicsVector3(0f, -Gravity, 0f)),
            new GameplayConfig(
                GoalZone: new GameplayZone(new PhysicsVector3(500f, 500f, 500f), new PhysicsVector3(501f, 501f, 501f)),
                MapBounds: FarBounds,
                TntBlastRadius: 4f,
                TntBlastImpulse: 25f,
                TntIgniteImpactSpeed: 5f,
                ObjectivesEnabled: false),
            sandboxMode: true));
        room.SetupFromLevel(new LevelContentDocument(
            ContentVersion: "wheel-suspension-test-v1",
            GoalZone: new GameplayZone(new PhysicsVector3(500f, 500f, 500f), new PhysicsVector3(501f, 501f, 501f)),
            MapBounds: FarBounds,
            Spawns: new[] { new LevelSpawnDefinition(PartGround, new PhysicsVector3(0f, -0.5f, 0f)) }));
        return room;
    }

    private static uint Place(GameRoom room, ref uint sequence, uint player, uint partTypeId, float x, float y)
    {
        CommandOutcome outcome = room.Submit(new PlacePartCommand(0, ++sequence, player, partTypeId, x, y, 0f, 1f));
        Assert.True(outcome.IsAccepted, $"place {partTypeId} at ({x},{y}): {outcome.Status}/{outcome.Error}");
        return outcome.EntityId;
    }

    private static PhysicsVector3 Delta(List<SnapshotEntity> entities, uint frame, uint wheel)
    {
        SnapshotEntity frameEntity = entities.Single(entity => entity.EntityId == frame);
        SnapshotEntity wheelEntity = entities.Single(entity => entity.EntityId == wheel);
        return ToVector(wheelEntity.Position) - ToVector(frameEntity.Position);
    }

    /// <summary>How far the wheel moved along its chassis' own Y between two snapshots, i.e. the
    /// suspension travel. Measuring in the chassis' frame keeps a tipping chassis from looking
    /// like suspension travel, and measuring a change needs no knowledge of the content's axle
    /// offset: at the build pose the two anchors coincide, so the change is the deflection.</summary>
    private static float Deflection(List<SnapshotEntity> rest, List<SnapshotEntity> settled, uint frame, uint wheel)
    {
        SnapshotEntity frameEntity = settled.Single(entity => entity.EntityId == frame);
        PhysicsQuaternion rotation = new(
            frameEntity.Rotation.X, frameEntity.Rotation.Y, frameEntity.Rotation.Z, frameEntity.Rotation.W);
        PhysicsVector3 up = rotation.Rotate(new PhysicsVector3(0f, 1f, 0f));
        return PhysicsVector3.Dot(Delta(settled, frame, wheel) - Delta(rest, frame, wheel), up);
    }

    private static PhysicsVector3 ToVector(ReplayVector3 value) => new(value.X, value.Y, value.Z);

    private static List<SnapshotEntity> PublishEntities(GameRoom room)
    {
        byte[] buffer = new byte[SnapshotFrame.GetMaxByteCount(64)];
        Assert.True(room.TryPublishSnapshot(buffer, out int bytesWritten));
        Assert.True(SnapshotFrame.TryDecodeHeader(buffer.AsSpan(0, bytesWritten), out _, out SnapshotFrameReader reader));
        List<SnapshotEntity> entities = new();
        while (reader.TryReadEntity(out SnapshotEntity entity))
        {
            entities.Add(entity);
        }

        return entities;
    }

    /// <summary>Fixture content: a frame, a rigid wheel, a wheel carrying the extracted
    /// suspension, and the ground it rests on.</summary>
    private const string ContentJson = """
    {
        "format": "pigforge.part-content",
        "schemaVersion": 1,
        "contentVersion": "wheel-suspension-test-v1",
        "parts": [
            { "partTypeId": 1, "name": "frame", "mode": "dynamic", "mass": 1, "capabilities": { "jointConnectionType": "source" }, "shapes": [ { "kind": "box", "halfExtents": [0.5, 0.5, 0.5] } ] },
            { "partTypeId": 2, "name": "wheel", "mode": "dynamic", "mass": 0.5, "capabilities": { "wheel": true, "jointConnectionType": "target" }, "shapes": [ { "kind": "sphere", "radius": 0.33 } ] },
            { "partTypeId": 3, "name": "sprung-wheel", "mode": "dynamic", "mass": 0.5, "capabilities": { "wheel": true, "jointConnectionType": "target", "suspension": { "stiffness": 50, "damper": 5, "restOffset": 0 } }, "shapes": [ { "kind": "sphere", "radius": 0.33 } ] },
            { "partTypeId": 4, "name": "ground", "mode": "static", "mass": 0, "material": { "restitution": 0, "friction": 0.7 }, "shapes": [ { "kind": "box", "halfExtents": [20, 0.5, 10] } ] }
        ]
    }
    """;
}
