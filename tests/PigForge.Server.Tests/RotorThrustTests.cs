using PigForge.Core;
using PigForge.Core.Content;
using PigForge.Protocol;
using PigForge.Physics.Abstractions;
using PigForge.Server;

namespace PigForge.Server.Tests;

/// <summary>
/// The rotor on the real content and the Bepu backend. The original models the fan, the plane
/// propeller and the rotor as one class, `FanPropeller`, and the rotor is the one that used to be
/// a PigForge balloon: unbounded lift that climbed kilometres, destroyed by its own switch
/// (gap list G50). It now runs through the same path as the fan, with the original's cap
/// (`maximumSpeed = powerFactor * m_defaultSpeed * IN RotorSpeed`, FanPropeller.cs:90,100-106) and
/// the original's switch semantics (a toggle that only stops the thrust, FanPropeller.cs:49-56,
/// 265-296). Content and calibration: tools/bple-fans, docs/specs/fan-propeller.md.
/// </summary>
public sealed class RotorThrustTests
{
    private const uint PartFrame = 1;
    private const uint PartEngine = 8;
    private const uint PartRotor = 37;

    [Fact]
    public void ARotorClimbsAtABoundedSpeedAndItsSwitchStopsItWithoutRemovingIt()
    {
        using GameRoom room = PlayHost.CreateSandboxRoom();
        uint player = PlayHost.NextPlayerId();
        uint sequence = 0;

        uint frame = Place(room, ref sequence, player, PartFrame, 0f, 4f);
        Place(room, ref sequence, player, PartEngine, 0f, 4f);
        uint rotor = Place(room, ref sequence, player, PartRotor, 0f, 5f);
        Assert.True(room.Submit(PlayHost.BindPlayer(new StartSimulationCommand(0, ++sequence, player), player)).IsAccepted);

        // FanPropeller.cs:64-68 sets m_enabled = false in Awake, so a freshly built rotor thrusts
        // nothing: let the rig settle on the terrain and confirm it stays put.
        room.RunTicks(120);
        float resting = Position(room, frame).Y;
        room.RunTicks(30);
        Assert.True(MathF.Abs(Position(room, frame).Y - resting) < 0.5f, "a rotor whose switch is off must not lift");

        // Switch it on. The old rotor deflated and vanished here; the original just starts turning.
        Assert.True(room.Submit(PlayHost.BindPlayer(new SetPartActiveCommand(0, ++sequence, player, rotor, true), player)).IsAccepted);
        room.RunTicks(60);
        float lifted = Position(room, frame).Y - resting;
        SnapshotEntity climbing = PublishEntities(room).Single(entity => entity.EntityId == rotor);
        Assert.True(lifted > 1f, $"the powered rotor must lift the rig: {lifted} m in one second");
        // 7 m/s cap x the cluster's power factor (150 engine power against 200 consumption, which
        // the rules layer applies as factor^0.75) is about 5.6 m/s, so one second of climb stays
        // under the cap. The pre-fix lift was unbounded: the same rig reached hundreds of metres.
        Assert.True(lifted < 12f, $"the lift must be bounded by the rotor's top speed: {lifted} m in one second");
        Assert.True(climbing.Position.Y > resting, "the rotor entity must survive its own switch");

        // The climb rate is a speed, not an acceleration: two consecutive ticks cannot differ by
        // more than a fraction of the cap. Past the cap the rotor's own overspeed brake
        // (FanPropeller.cs:198-207) holds the rig there, so a tick at the cap can even give a
        // little back.
        float before = Position(room, frame).Y;
        room.Tick();
        float after = Position(room, frame).Y;
        Assert.InRange(after - before, -0.2f, 0.5f);

        // Under the vanilla cap (7 m/s x the cluster's factor) this rig does *not* hold a steady
        // climb: it rises, then the rotor's overspeed brake (FanPropeller.cs:198-207) and the
        // torque from a rotor mounted above the centre of mass put it into a limit cycle. What the
        // cap certainly does is bound the lift -- the pre-fix, uncapped rotor reached hundreds of
        // metres in the same second. The exact sustained behaviour under the vanilla cap needs a
        // measurement on the original before it can be asserted; recorded in
        // tasks/original-vs-implemented.md (G105's execution notes).

        // Switch off: the thrust stops and the rig falls. (Under the vanilla cap the rig is already
        // in the limit cycle described above, so there is no clean "momentum carries it higher"
        // moment to assert -- the observable is that the climb is gone.)
        Assert.True(room.Submit(PlayHost.BindPlayer(new SetPartActiveCommand(0, ++sequence, player, rotor, false), player)).IsAccepted);
        float atSwitchOff = Position(room, frame).Y;
        // Measured over a whole second, like the climb above: the limit cycle makes single ticks
        // noisy, the trend is what the switch changes.
        room.RunTicks(60);
        Assert.True(Position(room, frame).Y < atSwitchOff, "a rotor whose switch is off must stop thrusting: the rig must fall");
        Assert.Contains(PublishEntities(room), entity => entity.EntityId == rotor);
    }

    [Fact]
    public void ARotorWithoutAnEnclosedEngineInItsClusterDoesNotLift()
    {
        using GameRoom room = PlayHost.CreateSandboxRoom();
        uint player = PlayHost.NextPlayerId();
        uint sequence = 0;

        uint frame = Place(room, ref sequence, player, PartFrame, 0f, 4f);
        uint rotor = Place(room, ref sequence, player, PartRotor, 0f, 5f);
        Assert.True(room.Submit(PlayHost.BindPlayer(new StartSimulationCommand(0, ++sequence, player), player)).IsAccepted);

        room.RunTicks(120);
        float resting = Position(room, frame).Y;
        Assert.True(room.Submit(PlayHost.BindPlayer(new SetPartActiveCommand(0, ++sequence, player, rotor, true), player)).IsAccepted);
        room.RunTicks(60);

        // FanPropeller.cs:85-92 scales the force by the engine power factor, and the rotor still
        // has a chassis neighbour, so the gate that holds it down is the missing engine
        // (Engine.cs:61: an engine only supplies while it is enclosed in a frame).
        Assert.True(MathF.Abs(Position(room, frame).Y - resting) < 0.5f, "a rotor with no engine in its cluster must not lift");
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
