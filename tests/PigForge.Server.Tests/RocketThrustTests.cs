using PigForge.Protocol;
using PigForge.Server;

namespace PigForge.Server.Tests;

/// <summary>
/// The rocket's thrust axis on the real content and the Bepu backend: the original reads it off the
/// part's own transform -- <c>transform.TransformDirection(m_direction)</c>, Rocket.cs:298-300 --
/// so the build rotation aims it. PigForge used to apply the content direction as a world axis,
/// which makes "turn the rocket round" do nothing.
/// The thrust curve, the speed cap and the burn length in <c>content/parts.json</c> are still
/// hand-written (they predate the extractors: <c>49e9153</c>) and are tracked as their own gap.
/// </summary>
public sealed class RocketThrustTests
{
    private const uint PartFrame = 1;
    private const uint PartRocket = 13;

    [Fact]
    public void ARocketTurnedAroundPushesTheOtherWay()
    {
        // A build rotation of 180 degrees (`Contraption.SetRotation`, a z rotation) turns the
        // rocket's content axis (+x) into -x. Both placements weld to the frame: the rocket's
        // connector is `any` and the frame is a source (ADR-011).
        float upright = RocketDrivenVelocity(0f);
        float turned = RocketDrivenVelocity(MathF.PI);

        Assert.True(upright > 1f, $"an unrotated rocket's content axis is +x: {upright} m/s");
        Assert.True(turned < -1f, $"a rocket built the other way round must push -x: {turned} m/s");
    }

    /// <summary>Builds a frame with a rocket beside it at <paramref name="rocketAngle"/>, presses
    /// the rocket's button while the rig hangs in the air and reports the rig's velocity along x
    /// after half a second.</summary>
    private static float RocketDrivenVelocity(float rocketAngle)
    {
        using GameRoom room = PlayHost.CreateSandboxRoom();
        uint player = PlayHost.NextPlayerId();
        uint sequence = 0;

        uint frame = Place(room, ref sequence, player, PartFrame, -12f, 14f);
        uint rocket = Place(room, ref sequence, player, PartRocket, -13f, 14f, rocketAngle);
        Assert.True(room.Submit(PlayHost.BindPlayer(new StartSimulationCommand(0, ++sequence, player), player)).IsAccepted);

        // The rocket's content activation is a button (`trigger`, ADR-028), so the press is what
        // starts the burn; `SetPartActive` carries that single press.
        Assert.True(room.Submit(PlayHost.BindPlayer(new SetPartActiveCommand(0, ++sequence, player, rocket, true), player)).IsAccepted);
        room.RunTicks(30);
        return PublishEntities(room).Single(entity => entity.EntityId == frame).LinearVelocity.X;
    }

    private static uint Place(GameRoom room, ref uint sequence, uint player, uint partTypeId, float x, float y, float angle = 0f)
    {
        CommandOutcome outcome = room.Submit(PlayHost.BindPlayer(
            new PlacePartCommand(0, ++sequence, player, partTypeId, x, y, angle, 1f),
            player));
        Assert.True(outcome.IsAccepted, $"place {partTypeId} at ({x},{y}) angle {angle}: {outcome.Status}/{outcome.Error}");
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
