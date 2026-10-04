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
        // 14 m/s cap x the cluster's power factor (150 engine power against 200 consumption), plus
        // the few m/s the cap's decay and the overspeed brake let it creep past. The pre-fix lift
        // was unbounded: the same rig reached hundreds of metres.
        Assert.True(lifted < 30f, $"the lift must be bounded by the rotor's top speed: {lifted} m in one second");
        Assert.True(climbing.Position.Y > resting, "the rotor entity must survive its own switch");

        // The climb rate is a speed, not an acceleration: two consecutive ticks cannot differ by
        // more than a fraction of the cap.
        float before = Position(room, frame).Y;
        room.Tick();
        float after = Position(room, frame).Y;
        Assert.InRange(after - before, 0f, 0.5f);

        // Switch off: the thrust stops. The rig keeps its momentum for a moment -- a rotor that
        // were still turning would hold the climb rate at the cap -- and then falls.
        Assert.True(room.Submit(PlayHost.BindPlayer(new SetPartActiveCommand(0, ++sequence, player, rotor, false), player)).IsAccepted);
        float atSwitchOff = Position(room, frame).Y;
        room.Tick();
        Assert.True(Position(room, frame).Y > atSwitchOff, "momentum must carry the rig past the switch");
        room.RunTicks(90);
        float coasted = Position(room, frame).Y;
        room.Tick();
        Assert.True(Position(room, frame).Y - coasted < 0f, "a rotor whose switch is off must stop thrusting: the rig must fall");
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
