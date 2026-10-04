using PigForge.Protocol;
using PigForge.Server;

namespace PigForge.Server.Tests;

/// <summary>
/// The plane propeller on the real content and the Bepu backend. The original models the fan, the
/// plane propeller and the rotor as one class, `FanPropeller`, and the propeller is the one whose
/// content used to say `wheel` + `motor` (gap list G50): the rules layer then built it its own
/// hinged body (`capabilities.wheel`) and gated the drive on ground contact (`RunMotors`), so a rig
/// carrying one never moved. It is a `fan` welded into its cluster now, and its original carries no
/// speed cap at all (`PropellerSpeed` is Infinity, INSettingsBExp.json:299-301), so the content
/// declares none and the rig's terminal speed is the original's own rigidbody damping (ADR-025).
/// Content and calibration: tools/bple-fans, docs/specs/fan-propeller.md.
/// </summary>
public sealed class PropellerThrustTests
{
    private const uint PartFrame = 1;
    private const uint PartEngine = 8;
    private const uint PartPropeller = 38;

    [Fact]
    public void APropellerPushesTheClusterItIsWeldedTo()
    {
        using GameRoom room = PlayHost.CreateSandboxRoom();
        uint player = PlayHost.NextPlayerId();
        uint sequence = 0;

        uint frame = Place(room, ref sequence, player, PartFrame, -12f, 6f);
        Place(room, ref sequence, player, PartEngine, -12f, 6f); // enclosed in the frame, so it supplies power
        uint propeller = Place(room, ref sequence, player, PartPropeller, -11f, 6f);
        Assert.True(room.Submit(PlayHost.BindPlayer(new StartSimulationCommand(0, ++sequence, player), player)).IsAccepted);

        // A wheel gets its own body on a revolute joint (CompoundAssembler's hinge collection);
        // a fan is welded into the compound. Sharing the frame's body is what lets the thrust move
        // the rig at all -- the old `wheel` modelling gave this part a body of its own.

        List<SnapshotEntity> built = PublishEntities(room);
        Assert.Equal(
            built.Single(entity => entity.EntityId == frame).PhysicsBodyId,
            built.Single(entity => entity.EntityId == propeller).PhysicsBodyId);

        // FanPropeller.cs:64-68 leaves `m_enabled` false in Awake: a freshly built propeller
        // thrusts nothing, so the rig only falls.
        float resting = Position(room, frame).X;
        room.RunTicks(30);
        Assert.InRange(MathF.Abs(Position(room, frame).X - resting), 0f, 0.05f);

        Assert.True(room.Submit(PlayHost.BindPlayer(new SetPartActiveCommand(0, ++sequence, player, propeller, true), player)).IsAccepted);
        room.RunTicks(30);

        // Its original axis is Right (`m_forceDirection` 0, Part_PlanePropeller_01_SET.prefab) and
        // the engine gives the cluster the power factor (150 / 100)^0.585 = 1.2678, so half a second
        // of thrust has to show up as travel along +x -- measured 1.7667 m here, against exactly
        // 0 m for the `wheel` + `motor` content this replaced (its own hinged body never touched
        // the ground, so `RunMotors` gated the drive off entirely).
        float travelled = Position(room, frame).X - resting;
        Assert.True(travelled > 1f, $"a powered propeller must push its rig: {travelled} m in half a second");
    }

    private static ReplayVector3 Position(GameRoom room, uint entityId) =>
        PublishEntities(room).Single(entity => entity.EntityId == entityId).Position;

    private static uint Place(GameRoom room, ref uint sequence, uint player, uint partTypeId, float x, float y)
    {
        CommandOutcome outcome = room.Submit(PlayHost.BindPlayer(
            new PlacePartCommand(0, ++sequence, player, partTypeId, x, y, 0f, 1f),
            player));
        Assert.True(outcome.IsAccepted, $"place {partTypeId} at ({x},{y}): {outcome.Status}/{outcome.Error}");
        return outcome.EntityId;
    }

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
}
