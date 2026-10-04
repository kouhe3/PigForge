using PigForge.Protocol;
using PigForge.Server;

namespace PigForge.Server.Tests;

/// <summary>
/// The fan's application point on the real content and the Bepu backend: the original applies the
/// thrust with <c>AddForceAtPosition(..., position, ForceMode.Force)</c> at
/// <c>transform.position + dir * 0.5</c> (FanPropeller.cs:151-152, :209), so a fan mounted off the
/// rig's centre of mass turns it. PigForge used to push on the body centre, which cannot produce
/// that torque. Content and calibration: tools/bple-fans, docs/specs/fan-propeller.md.
/// </summary>
public sealed class FanThrustTests
{
    private const uint PartFrame = 1;
    private const uint PartEngine = 8;
    private const uint PartFan = 11;

    [Fact]
    public void AFanMountedAboveTheCentreOfMassTurnsTheRig()
    {
        using GameRoom room = PlayHost.CreateSandboxRoom();
        uint player = PlayHost.NextPlayerId();
        uint sequence = 0;

        uint frame = Place(room, ref sequence, player, PartFrame, 0f, 4f);
        Place(room, ref sequence, player, PartEngine, 0f, 4f); // enclosed in the frame, so it supplies power
        uint fan = Place(room, ref sequence, player, PartFan, 0f, 5f); // one cell above the compound's centre of mass
        Assert.True(room.Submit(PlayHost.BindPlayer(new StartSimulationCommand(0, ++sequence, player), player)).IsAccepted);

        // Let the rig settle on the terrain; the fan's own switch is off (FanPropeller.cs:49-56).
        // The original's own damping (0.2 drag / 0.05 angularDrag on every part, tools/bple-damping)
        // makes a dropped rig take longer to come to rest, so this is the 240 ticks it needs (120
        // was enough before damping existed).
        room.RunTicks(240);
        float resting = AngularVelocity(room, frame).Z;
        Assert.InRange(MathF.Abs(resting), 0f, 0.01f);

        Assert.True(room.Submit(PlayHost.BindPlayer(new SetPartActiveCommand(0, ++sequence, player, fan, true), player)).IsAccepted);
        room.RunTicks(30);

        // The fan's content axis is Left (-x) and its mount sits above the centre of mass, so the
        // torque r x F (r.y > 0, F.x < 0) is counter-clockwise about z. Measured 3.84 rad/s in 30
        // ticks -- 4.63 before the original's 0.05 angularDrag was modelled, which bleeds a few
        // percent off every tick. The body-centre application the rules layer used before cannot
        // produce a torque at all (measured 1.2e-7 rad/s).
        float turning = AngularVelocity(room, frame).Z;
        Assert.InRange(turning, 0.5f, 6f);
    }

    private static ReplayVector3 AngularVelocity(GameRoom room, uint entityId) =>
        PublishEntities(room).Single(entity => entity.EntityId == entityId).AngularVelocity;

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
