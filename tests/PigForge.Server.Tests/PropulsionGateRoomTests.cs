using PigForge.Protocol;
using PigForge.Server;

namespace PigForge.Server.Tests;

/// <summary>
/// Room-level propulsion gate (gap list G25): a rocket only fires while a chassis (frame) touches
/// it. The original refuses the part in <c>BasePropulsion.ValidatePart</c> — it counts the
/// neighbours whose <c>IsPartOfChassis()</c> is true (<c>BasePropulsion.cs:13-20</c>), and
/// <c>Frame.cs:37-40</c> is the only override that makes a part count. PigForge keeps the part
/// buildable and strips its force, and the room publishes that flag from the real construction
/// layout at materialisation (<c>GameRoom.SyncChassisAnchors</c>), so this test exercises
/// construction adjacency and not a stub.
/// </summary>
public sealed class PropulsionGateRoomTests
{
    // Real content: 1 = wooden frame (canEnclose, ADR-011), 13 = rocket (BasePropulsion, thrust 4).
    private const uint PartFrame = 1;
    private const uint PartRocket = 13;

    private const float RocketSpawnX = -9f;
    private const float RocketSpawnY = -1.5f;

    [Fact]
    public void ARocketOnlyFiresWhenAFrameNeighbourAnchorsIt()
    {
        float anchored = RunRocket(withFrame: true);
        float loose = RunRocket(withFrame: false);

        Assert.True(anchored > RocketSpawnX + 0.2f, $"the chassis-anchored rocket must fire: {anchored}");
        Assert.True(
            MathF.Abs(loose - RocketSpawnX) < 0.05f,
            $"a rocket with no chassis neighbour must not fire: {loose} (anchored: {anchored})");
    }

    /// <summary>Placement of the rocket, then the returned x of its body 20 ticks after ignition.</summary>
    private static float RunRocket(bool withFrame)
    {
        using GameRoom room = PlayHost.CreateSandboxRoom();
        uint player = PlayHost.NextPlayerId();
        uint sequence = 0;

        uint Place(uint partTypeId, float x, float y)
        {
            CommandOutcome outcome = room.Submit(PlayHost.BindPlayer(PlacePart(++sequence, partTypeId, x, y), player));
            Assert.True(outcome.IsAccepted, $"place {partTypeId} at ({x},{y}): {outcome.Status}/{outcome.Error}");
            return outcome.EntityId;
        }

        if (withFrame)
        {
            Place(PartFrame, RocketSpawnX + 1f, RocketSpawnY);
        }

        uint rocket = Place(PartRocket, RocketSpawnX, RocketSpawnY);

        Assert.True(room.Submit(PlayHost.BindPlayer(Start(++sequence, player), player)).IsAccepted);
        for (int tick = 0; tick < 60; tick++)
        {
            room.Tick();
        }

        // The rocket is a trigger part: it fires when its switch turns on (content activation).
        Assert.True(room.Submit(PlayHost.BindPlayer(
            new SetPartActiveCommand(0, ++sequence, player, rocket, true), player)).IsAccepted);
        for (int tick = 0; tick < 20; tick++)
        {
            room.Tick();
        }

        return PublishEntities(room).Single(entity => entity.EntityId == rocket).Position.X;
    }

    private static PlacePartCommand PlacePart(uint sequence, uint partTypeId, float positionX, float positionY) =>
        new(
            Tick: 0,
            Sequence: sequence,
            PlayerId: 0,
            PartTypeId: partTypeId,
            PositionX: positionX,
            PositionY: positionY,
            Angle: 0f,
            Scale: 1f);

    private static StartSimulationCommand Start(uint sequence, uint playerId) =>
        new(Tick: 0, Sequence: sequence, PlayerId: playerId);

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
