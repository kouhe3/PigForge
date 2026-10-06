using PigForge.Core;
using PigForge.Core.Content;
using PigForge.Protocol;
using PigForge.Server;

namespace PigForge.Server.Tests;

/// <summary>
/// A driven wheel pushes along the ground it stands on, not along a world axis: the original takes
/// <c>Vector3.Cross(hitInfo.normal, Vector3.forward)</c> from the surface under the wheel
/// (MotorWheel.cs:285-288). On the sandbox floor that is +X, so the flat-ground room tests cannot
/// tell the two apart -- the ramp is what proves it.
/// </summary>
public sealed class MotorWheelRoomTests
{
    private const uint PartFrame = 1;
    private const uint PartEngine = 8;
    private const uint PartMotorWheel = 17;

    // terrain-v1's ramp: three 12 m planks at 0.25 rad (content/levels/terrain-v1.json), the middle
    // one centred at (-6.934, 1.1). Its top face is `y = 1.342 + tan(0.25) * (x + 6.996)`.
    private const float RampSlope = 0.25534f;

    [Fact]
    public void ADrivenCartClimbsTheSandboxRamp()
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

        float Surface(float x) => 1.342f + (RampSlope * (x + 6.996f));

        Place(PartMotorWheel, -6.5f, Surface(-6.5f) + 0.5f);
        Place(PartMotorWheel, -5.5f, Surface(-5.5f) + 0.5f);
        uint frame = Place(PartFrame, -6f, Surface(-6f) + 1.5f);
        Place(PartEngine, -6f, Surface(-6f) + 1.5f); // enclosed in the frame

        Assert.True(room.Submit(PlayHost.BindPlayer(Start(++sequence, player), player)).IsAccepted);
        room.RunTicks(120);

        (float X, float Y) start = FramePose(room, frame);
        Assert.True(room.Submit(PlayHost.BindPlayer(SetTypeActive(++sequence, PartMotorWheel, active: true), player)).IsAccepted);

        // The first second is the drive spinning up while gravity still slides it back down the
        // slope; once it has the grip it walks the ramp's own line, at the ramp's own slope.
        room.RunTicks(120);
        (float X, float Y) mid = FramePose(room, frame);
        room.RunTicks(60);
        (float X, float Y) end = FramePose(room, frame);

        float dx = end.X - mid.X;
        float dy = end.Y - mid.Y;

        Assert.True(dx > 4f, $"the cart must drive up the ramp: dx {dx}, dy {dy}");
        Assert.True(dy > 1f, $"the cart must climb: dx {dx}, dy {dy}");
        // It follows the ground rather than a world axis: measured dy/dx 0.255 against the ramp's
        // own tan(0.25) = 0.2553. (The sandbox floor is flat, so only the ramp can show this.)
        Assert.Equal(RampSlope, dy / dx, 1);
        Assert.True(end.Y > start.Y + 1f, $"it must climb overall, spin-up included: {start.Y} -> {end.Y}");
    }

    private static (float X, float Y) FramePose(GameRoom room, uint entityId)
    {
        byte[] buffer = new byte[SnapshotFrame.GetMaxByteCount(64)];
        Assert.True(room.TryPublishSnapshot(buffer, out int bytesWritten));
        Assert.True(SnapshotFrame.TryDecodeHeader(buffer.AsSpan(0, bytesWritten), out _, out SnapshotFrameReader reader));
        while (reader.TryReadEntity(out SnapshotEntity entity))
        {
            if (entity.EntityId == entityId)
            {
                return (entity.Position.X, entity.Position.Y);
            }
        }

        throw new InvalidOperationException($"entity {entityId} is not in the frame");
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

    private static SetPartTypeActiveCommand SetTypeActive(uint sequence, uint partTypeId, bool active) =>
        new(Tick: 0, Sequence: sequence, PlayerId: 0, PartTypeId: partTypeId, Active: active);
}
