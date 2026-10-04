using PigForge.Core;
using PigForge.Protocol;
using PigForge.Server;

namespace PigForge.Server.Tests;

/// <summary>
/// Frame-to-frame welds in a real room (docs/specs/weld-compliance.md §4.2): the original keeps two
/// frames as two bodies joined by a real joint (<c>Contraption.cs:1507-1546</c>) instead of merging
/// them into one compound (ADR-011), so a room has to materialise one body per frame and hold the
/// pair with a compliant weld — and break it when an applied impulse clears the pair's threshold.
/// Real Bepu, real content, real sandbox room: the numbers below are what the shipped parts do.
/// </summary>
public sealed class FrameWeldRoomTests
{
    private const uint PartFrame = 1;
    private const uint PartTnt = 9;
    private const uint PartDetacher = 43;
    private const uint PartEngine = 8;
    private const uint PartMotorWheel = 17;

    [Fact]
    public void AFrameChainMaterialisesOneBodyPerFrameHeldByWelds()
    {
        using GameRoom room = PlayHost.CreateSandboxRoom();
        uint player = PlayHost.NextPlayerId();
        uint sequence = 0;

        uint[] chain = new uint[4];
        for (int index = 0; index < chain.Length; index++)
        {
            chain[index] = Place(room, ref sequence, player, PartFrame, index, 4f);
        }

        Assert.True(room.Submit(PlayHost.BindPlayer(new StartSimulationCommand(0, ++sequence, player), player)).IsAccepted);

        List<SnapshotEntity> entities = PublishEntities(room);
        uint[] bodies = chain.Select(entityId => entities.Single(entity => entity.EntityId == entityId).PhysicsBodyId).ToArray();
        Assert.All(bodies, body => Assert.NotEqual(0u, body));
        Assert.Equal(chain.Length, bodies.Distinct().Count());
        Assert.Equal(chain.Length - 1, room.WeldJointCount);
    }

    [Fact]
    public void AFrameChainRoomIsDeterministicAcrossRooms()
    {
        Assert.Equal(RunFrameChain(), RunFrameChain());
    }

    private static long RunFrameChain()
    {
        using GameRoom room = PlayHost.CreateSandboxRoom();
        uint player = PlayHost.NextPlayerId();
        uint sequence = 0;
        for (int index = 0; index < 4; index++)
        {
            Place(room, ref sequence, player, PartFrame, index, 4f);
        }

        Assert.True(room.Submit(PlayHost.BindPlayer(new StartSimulationCommand(0, ++sequence, player), player)).IsAccepted);
        room.RunTicks(180);

        return room.ComputeStateHash();
    }

    /// <summary>
    /// A blast over the pair's threshold drops the weld: the frames are already separate bodies, so
    /// the break is exactly the joint going away — and the power edge it carried with it.
    /// </summary>
    [Fact]
    public void ABlastOverTheThresholdBreaksTheFrameWeld()
    {
        using GameRoom room = PlayHost.CreateSandboxRoom();
        uint player = PlayHost.NextPlayerId();
        uint sequence = 0;

        uint left = Place(room, ref sequence, player, PartFrame, 0f, 4f);
        uint right = Place(room, ref sequence, player, PartFrame, 1f, 4f);
        uint charge = Place(room, ref sequence, player, PartTnt, 2f, 4f);
        Assert.True(room.Submit(PlayHost.BindPlayer(new StartSimulationCommand(0, ++sequence, player), player)).IsAccepted);
        // The two frames are welded to each other; the charge welds to its own frame with a seam.
        Assert.Equal(1, room.WeldJointCount);

        // Light the charge: its blast impulse lands on the frame it is welded to, over the weld's
        // own threshold (wood-wood is the unmodified GameplayConfig.SeamBreakImpulse).
        Assert.True(room.Submit(PlayHost.BindPlayer(new SetPartActiveCommand(0, ++sequence, player, charge, true), player)).IsAccepted);
        room.RunTicks(20);

        List<SnapshotEntity> entities = PublishEntities(room);
        string dump = string.Join(" ", entities.Select(entity => $"{entity.EntityId}:{entity.PartTypeId}@b{entity.PhysicsBodyId}"));
        Assert.True(entities.Any(entity => entity.EntityId == left), $"the left frame must survive: {dump}");
        Assert.True(entities.Any(entity => entity.EntityId == right), $"the right frame must survive: {dump}");
        Assert.Equal(0, room.WeldJointCount);
    }

    /// <summary>
    /// A seam split destroys the compound's body and binds new ones, and the backend drops every
    /// joint of a removed body — so the welds the destroyed body carried have to be rebound, or a
    /// split anywhere in a frame's compound would tear the frame off its neighbours.
    /// </summary>
    [Fact]
    public void ASplitRebindsTheWeldItDestroyed()
    {
        using GameRoom room = PlayHost.CreateSandboxRoom();
        uint player = PlayHost.NextPlayerId();
        uint sequence = 0;

        uint detacher = Place(room, ref sequence, player, PartDetacher, 0f, 4f);
        uint middle = Place(room, ref sequence, player, PartFrame, 1f, 4f);
        uint far = Place(room, ref sequence, player, PartFrame, 2f, 4f);
        Assert.True(room.Submit(PlayHost.BindPlayer(new StartSimulationCommand(0, ++sequence, player), player)).IsAccepted);
        Assert.Equal(1, room.WeldJointCount);

        // The detacher's switch splits its compound along the charge-free seam; the middle frame
        // gets a fresh body, and the weld to the far frame has to come with it.
        Assert.True(room.Submit(PlayHost.BindPlayer(new SetPartActiveCommand(0, ++sequence, player, detacher, true), player)).IsAccepted);
        room.RunTicks(2);

        Assert.Equal(1, room.WeldJointCount);
        List<SnapshotEntity> entities = PublishEntities(room);
        string dump = string.Join(" ", entities.Select(entity => $"{entity.EntityId}:{entity.PartTypeId}@b{entity.PhysicsBodyId}"));
        Assert.NotEqual(
            entities.Single(entity => entity.EntityId == middle).PhysicsBodyId,
            entities.Single(entity => entity.EntityId == far).PhysicsBodyId);
        Assert.True(entities.Any(entity => entity.EntityId == far), $"the far frame must survive: {dump}");
    }

    /// <summary>
    /// A weld is a power edge like every other joint: the original unions the whole joint graph into
    /// one power component (Contraption.cs:1293). An engine enclosed in one frame therefore powers a
    /// consumer welded onto the next one — without the edge the wheel's cluster holds no engine and
    /// nothing drives (docs/specs/power-system.md §4 item 3).
    /// </summary>
    [Fact]
    public void AnEngineInOneFramePowersAConsumerWeldedToTheNextOne()
    {
        using GameRoom room = PlayHost.CreateSandboxRoom();
        uint player = PlayHost.NextPlayerId();
        uint sequence = 0;

        // The wheel is adjacent to the near frame only, so it hinges to it; the engine sits in the
        // far frame's own cell, i.e. enclosed. The weld between the two frames is the only link.
        uint wheel = Place(room, ref sequence, player, PartMotorWheel, -8.5f, -2.5f);
        uint near = Place(room, ref sequence, player, PartFrame, -8f, -1.5f);
        uint far = Place(room, ref sequence, player, PartFrame, -7f, -1.5f);
        Place(room, ref sequence, player, PartEngine, -7f, -1.5f);
        Assert.True(room.Submit(PlayHost.BindPlayer(new StartSimulationCommand(0, ++sequence, player), player)).IsAccepted);
        Assert.Equal(1, room.WeldJointCount);
        room.RunTicks(180);

        Assert.True(room.Submit(PlayHost.BindPlayer(
            new SetPartTypeActiveCommand(0, ++sequence, player, PartMotorWheel, true), player)).IsAccepted);
        float fastest = 0f;
        for (int tick = 0; tick < 150; tick++)
        {
            room.Tick();
            SnapshotEntity frameEntity = PublishEntities(room).Single(entity => entity.EntityId == near);
            fastest = MathF.Max(fastest, MathF.Abs(frameEntity.LinearVelocity.X));
        }

        List<SnapshotEntity> entities = PublishEntities(room);
        Assert.NotEqual(
            entities.Single(entity => entity.EntityId == near).PhysicsBodyId,
            entities.Single(entity => entity.EntityId == far).PhysicsBodyId);
        Assert.True(entities.Any(entity => entity.EntityId == wheel), "the wheel must survive");
        Assert.True(fastest > 5f, $"the welded-in engine must drive the wheel: {fastest} m/s");
    }

    /// <summary>
    /// A sandbox RESET destroys the bodies the welds were bound to and keeps the layout, so the
    /// same frame pair is materialised again under the same entity ids. The stale definition must
    /// not shadow the fresh one the assembler registers, or the rebuilt chain would fall apart
    /// unwelded.
    /// </summary>
    [Fact]
    public void APlayerResetAndRebuildGetsItsWeldsAgain()
    {
        using GameRoom room = PlayHost.CreateSandboxRoom();
        uint player = PlayHost.NextPlayerId();
        uint sequence = 0;

        Place(room, ref sequence, player, PartFrame, 0f, 4f);
        Place(room, ref sequence, player, PartFrame, 1f, 4f);
        Assert.True(room.Submit(PlayHost.BindPlayer(new StartSimulationCommand(0, ++sequence, player), player)).IsAccepted);
        Assert.Equal(1, room.WeldJointCount);

        Assert.True(room.Submit(PlayHost.BindPlayer(new RetryCommand(0, ++sequence, player), player)).IsAccepted);
        Assert.Equal(0, room.WeldJointCount);

        // RESET kept the layout: Start alone rebuilds the chain, no re-placing.
        Assert.True(room.Submit(PlayHost.BindPlayer(new StartSimulationCommand(0, ++sequence, player), player)).IsAccepted);
        Assert.Equal(1, room.WeldJointCount);
    }

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
