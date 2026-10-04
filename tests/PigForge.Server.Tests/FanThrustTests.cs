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

    [Fact]
    public void AFanTurnedAroundPushesTheOtherWay()
    {
        // The original reads the thrust axis off the part's own transform --
        // `transform.TransformDirection(GetDirectionVector(m_forceDirection))`, FanPropeller.cs:154-155
        // -- and a build rotation of 180 degrees (`Contraption.SetRotation`, a z rotation) turns a
        // `Left` fan into a `Right` one (`BasePart.Rotate`: (Left + Deg_180) % 4 == Right). Aiming
        // the fan is the whole point of the part, and the content value is the part-local axis, not
        // a world one: applying it in world space makes the thrust ignore how the player built it.
        //
        // Both mounts face the frame -- a fan's connector and its thrust are on the same side
        // (`m_jointConnectionDirection` 3 = Left, `m_forceDirection` 2 = Left), which is also what
        // the original's placement auto-align produces (`Contraption.cs:1889`).
        float upright = FanDrivenVelocity(0f, -11f);
        float turned = FanDrivenVelocity(MathF.PI, -13f);

        Assert.True(upright < -2f, $"the unrotated Left fan must push the rig left: {upright} m/s");
        Assert.True(turned > 2f, $"a fan built the other way round must push the rig right: {turned} m/s");
    }

    /// <summary>Builds frame + enclosed engine + fan at <paramref name="fanAngle"/>, switches the
    /// fan on in the air and reports the rig's velocity along x after half a second.</summary>
    private static float FanDrivenVelocity(float fanAngle, float fanX)
    {
        using GameRoom room = PlayHost.CreateSandboxRoom();
        uint player = PlayHost.NextPlayerId();
        uint sequence = 0;

        uint frame = Place(room, ref sequence, player, PartFrame, -12f, 14f);
        Place(room, ref sequence, player, PartEngine, -12f, 14f);
        uint fan = Place(room, ref sequence, player, PartFan, fanX, 14f, fanAngle);
        Assert.True(room.Submit(PlayHost.BindPlayer(new StartSimulationCommand(0, ++sequence, player), player)).IsAccepted);

        // FanPropeller.cs:64-68 leaves the switch off in Awake, so nothing moves until it is set.
        Assert.True(room.Submit(PlayHost.BindPlayer(new SetPartActiveCommand(0, ++sequence, player, fan, true), player)).IsAccepted);
        room.RunTicks(30);
        return PublishEntities(room).Single(entity => entity.EntityId == frame).LinearVelocity.X;
    }

    private static ReplayVector3 AngularVelocity(GameRoom room, uint entityId) =>
        PublishEntities(room).Single(entity => entity.EntityId == entityId).AngularVelocity;

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
