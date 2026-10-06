using PigForge.Protocol;
using PigForge.Server;

namespace PigForge.Server.Tests;

/// <summary>
/// Room-level power wiring (spec docs/specs/power-system.md §4 items 1-4): the sandbox cart's
/// motor wheels only drive while the cluster carries an ENCLOSED engine, and the factor the rule
/// layer computes is part of the room's state hash.
/// </summary>
public sealed class PowerSystemRoomTests
{
    // Real content: 17 = motor-wheel (consumption 100), 8 = engine (power 150), 1 = wooden frame
    // (canEnclose), 4 = pig, 16 = sticky-wheel (consumption 80, driven like the motor wheel).
    private const uint PartFrame = 1;
    private const uint PartPig = 4;
    private const uint PartEngine = 8;
    private const uint PartStickyWheel = 16;
    private const uint PartMotorWheel = 17;

    [Fact]
    public void CartWithoutAnEngineIgnoresItsWheelSwitches()
    {
        CartRun switched = RunCart(engine: EnginePlacement.None, activateWheels: true);
        CartRun idle = RunCart(engine: EnginePlacement.None, activateWheels: false);

        // No engine in the cluster: the factor is 0, so the wheels emit no impulse at all and the
        // rig is bit-for-bit what it is with the switches off.
        Assert.Equal(idle.BlockX, switched.BlockX);
        Assert.Equal(idle.BlockY, switched.BlockY);
        Assert.Equal(idle.BlockBody, switched.BlockBody);
    }

    [Fact]
    public void EngineOutsideAFrameSuppliesNothing()
    {
        CartRun switched = RunCart(engine: EnginePlacement.Adjacent, activateWheels: true);
        CartRun idle = RunCart(engine: EnginePlacement.Adjacent, activateWheels: false);

        // Engine.cs:61 ValidatePart() => m_enclosedInto != null: an engine that is not inside a
        // frame is not a valid part, so it supplies no power and the wheels stay gated.
        Assert.Equal(idle.BlockX, switched.BlockX);
        Assert.NotEqual(idle.EngineBody, idle.BlockBody);
    }

    [Fact]
    public void EnclosedEnginePowersTheHingedWheelsAndTheRunIsDeterministic()
    {
        CartRun first = RunCart(engine: EnginePlacement.Enclosed, activateWheels: true);
        CartRun second = RunCart(engine: EnginePlacement.Enclosed, activateWheels: true);
        CartRun idle = RunCart(engine: EnginePlacement.Enclosed, activateWheels: false);

        // The engine is welded into the frame's body, so the hinged wheels share its cluster and
        // drive with the cluster's factor (150 / 200 -> 0.75^0.75).
        Assert.Equal(first.BlockBody, first.EngineBody);
        Assert.NotEqual(first.BlockBody, first.WheelBody);
        Assert.True(first.BlockX > idle.BlockX + 0.2f, $"the powered cart must drive: {idle.BlockX} -> {first.BlockX}");

        // The factor is rules state the snapshots cannot show, so it is folded into the room hash.
        Assert.Equal(first.StateHash, second.StateHash);
    }

    [Fact]
    public void StickyWheelCartDrivesOnceAnEnclosedEnginePowersIt()
    {
        CartRun first = RunCart(EnginePlacement.Enclosed, activateWheels: true, wheelPart: PartStickyWheel);
        CartRun second = RunCart(EnginePlacement.Enclosed, activateWheels: true, wheelPart: PartStickyWheel);
        CartRun idle = RunCart(EnginePlacement.Enclosed, activateWheels: false, wheelPart: PartStickyWheel);
        CartRun unpowered = RunCart(EnginePlacement.None, activateWheels: true, wheelPart: PartStickyWheel);
        CartRun unpoweredOff = RunCart(EnginePlacement.None, activateWheels: false, wheelPart: PartStickyWheel);

        // The original's sticky wheel is a driven wheel (StickyWheel.cs:117-122, m_force 100), so
        // the same rig drives on the same rule path as the motor wheels.
        Assert.True(first.BlockX > idle.BlockX + 0.2f, $"the sticky-wheel cart must drive: {idle.BlockX} -> {first.BlockX}");

        // And it is gated by the cluster factor, not by the switch: with no enclosed engine the
        // switch changes nothing (150 / 160 -> 0.9375^0.585 when it is enclosed).
        Assert.Equal(unpoweredOff.BlockX, unpowered.BlockX);
        Assert.Equal(unpoweredOff.BlockY, unpowered.BlockY);

        Assert.Equal(first.StateHash, second.StateHash);
    }

    [Fact]
    public void AMotorCartNeverExceedsItsTopSpeed()
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

        Place(PartMotorWheel, -8.5f, -2.5f);
        Place(PartMotorWheel, -7.5f, -2.5f);
        uint frame = Place(PartFrame, -8.0f, -1.5f);
        Place(PartPig, -8.0f, -0.5f);
        Place(PartEngine, -8.0f, -1.5f); // enclosed in the frame

        Assert.True(room.Submit(PlayHost.BindPlayer(Start(++sequence, player), player)).IsAccepted);
        room.RunTicks(180);
        Assert.True(room.Submit(PlayHost.BindPlayer(SetTypeActive(++sequence, PartMotorWheel, active: true), player)).IsAccepted);

        // Two motor wheels draw 200 against the engine's 150. On the vanilla declaration defaults
        // the original runs its legacy power branch (Contraption.cs:556-582), so the denominator
        // frees 90% of an airborne wheel's draw and the cap is not constant:
        //   both wheels grounded  150 / 200 -> 0.80593, MotorWheel.cs:101-103 caps 15 * factor;
        //   one wheel in the air  150 / 110 -> the cart may exceed the grounded cap.
        // Measured peaks: 11.35 m/s while both wheels roll, 13.69 m/s on the sandbox floor's bumps.
        float groundedCap = 15f * 0.80593f;
        float oneAirborneCap = 15f * MathF.Pow(150f / 110f, 0.585f);
        float fastest = 0f;
        for (int tick = 0; tick < 150; tick++)
        {
            room.Tick();
            SnapshotEntity body = PublishEntities(room, out _).Single(entity => entity.EntityId == frame);
            fastest = MathF.Max(fastest, MathF.Abs(body.LinearVelocity.X));
        }

        Assert.True(fastest > 5f, $"the powered cart must actually drive: {fastest} m/s");
        Assert.True(fastest <= oneAirborneCap + 0.5f, $"the cart must stay at or below the one-wheel-airborne cap {oneAirborneCap}: {fastest} m/s");
        // Non-vacuous for the legacy branch: without the airborne discount the cart could not pass
        // the all-grounded cap at all.
        Assert.True(fastest > groundedCap, $"the airborne discount must lift the cap above {groundedCap}: {fastest} m/s");
    }

    private enum EnginePlacement
    {
        None,
        /// <summary>Placed next to the frame, i.e. not enclosed (Engine.cs:61 keeps it invalid).</summary>
        Adjacent,
        /// <summary>Placed in the frame's own cell, which encloses it (Frame.cs:44-49).</summary>
        Enclosed
    }

    private sealed record CartRun(float BlockX, float BlockY, uint BlockBody, uint EngineBody, uint WheelBody, long StateHash);

    private static CartRun RunCart(EnginePlacement engine, bool activateWheels, uint wheelPart = PartMotorWheel)
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

        uint rearWheel = Place(wheelPart, -8.5f, -2.5f);
        uint frontWheel = Place(wheelPart, -7.5f, -2.5f);
        uint frame = Place(PartFrame, -8.0f, -1.5f);
        Place(PartPig, -8.0f, -0.5f);
        uint engineEntity = engine switch
        {
            EnginePlacement.None => 0u,
            EnginePlacement.Adjacent => Place(PartEngine, -9.0f, -1.5f),
            _ => Place(PartEngine, -8.0f, -1.5f)
        };

        Assert.True(room.Submit(PlayHost.BindPlayer(Start(++sequence, 0), player)).IsAccepted);
        for (int tick = 0; tick < 180; tick++)
        {
            room.Tick();
        }

        if (activateWheels)
        {
            Assert.True(room.Submit(PlayHost.BindPlayer(SetTypeActive(++sequence, wheelPart, active: true), player)).IsAccepted);
        }

        for (int tick = 0; tick < 30; tick++)
        {
            room.Tick();
        }

        List<SnapshotEntity> entities = PublishEntities(room, out _);
        SnapshotEntity frameEntity = entities.Single(entity => entity.EntityId == frame);
        SnapshotEntity wheelEntity = entities.Single(entity => entity.EntityId == rearWheel);
        uint engineBody = engineEntity == 0
            ? 0u
            : entities.Single(entity => entity.EntityId == engineEntity).PhysicsBodyId;
        return new CartRun(
            frameEntity.Position.X,
            frameEntity.Position.Y,
            frameEntity.PhysicsBodyId,
            engineBody,
            wheelEntity.PhysicsBodyId,
            room.ComputeStateHash());
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

    private static List<SnapshotEntity> PublishEntities(GameRoom room, out SnapshotFrameHeader header)
    {
        byte[] buffer = new byte[SnapshotFrame.GetMaxByteCount(64)];
        Assert.True(room.TryPublishSnapshot(buffer, out int bytesWritten));
        Assert.True(SnapshotFrame.TryDecodeHeader(buffer.AsSpan(0, bytesWritten), out header, out SnapshotFrameReader reader));
        List<SnapshotEntity> entities = new();
        while (reader.TryReadEntity(out SnapshotEntity entity))
        {
            entities.Add(entity);
        }

        return entities;
    }
}
