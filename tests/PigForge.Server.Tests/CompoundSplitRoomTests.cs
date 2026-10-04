using PigForge.Core;
using PigForge.Core.Content;
using PigForge.Protocol;
using PigForge.Server;

namespace PigForge.Server.Tests;

/// <summary>
/// A compound that comes apart while one of its parts is still emitting a command every tick.
/// <c>GameRoom.Tick</c> decides a seam split from the impulse it *applied* this tick, then destroys
/// the body -- but the rules have already produced this tick's commands against that same body, and
/// the next tick is what applies them. On the real backend a command aimed at a body that no longer
/// exists is fatal: <c>BepuPhysicsWorld.ApplyCommands</c> throws "Impulse target body N does not
/// exist or is not dynamic" and the room stops ticking, which the player sees as the whole rig
/// freezing. Reachable with shipped content: a charge welds into the rig and splits it out from
/// under a part that emits a command every tick. The tail stands in for any such part -- it emits
/// unconditionally, needs no switch and no engine, and its impulse is zero while the rig rests,
/// which is the point: the crash is not about magnitude.
/// </summary>
public sealed class CompoundSplitRoomTests
{
    private const uint PartFrame = 1;
    private const uint PartTnt = 9;
    private const uint PartTail = 33;

    [Fact]
    public void ASplitUnderAStillThrustingPartKeepsTheRoomTicking()
    {
        using GameRoom room = PlayHost.CreateSandboxRoom();
        uint player = PlayHost.NextPlayerId();
        uint sequence = 0;

        // The tail welds on its right side (part 33, jointConnectionDirection "right"), so the
        // frame sits to its right; the charge welds to the frame's left.
        uint tail = Place(room, ref sequence, player, PartTail, 0f, 4f);
        uint frame = Place(room, ref sequence, player, PartFrame, 1f, 4f);
        uint tnt = Place(room, ref sequence, player, PartTnt, 2f, 4f);
        Assert.True(room.Submit(PlayHost.BindPlayer(new StartSimulationCommand(0, ++sequence, player), player)).IsAccepted);

        room.RunTicks(120);
        Assert.True(PublishEntities(room).Single(entity => entity.EntityId == tail).PhysicsBodyId
            == PublishEntities(room).Single(entity => entity.EntityId == frame).PhysicsBodyId,
            "the rig must be one compound before the charge goes off");

        // Light the charge. Its blast exceeds the seam threshold, so the room splits the rig while
        // the tail's command for that same tick is already queued against the doomed body.
        Assert.True(room.Submit(PlayHost.BindPlayer(new SetPartActiveCommand(0, ++sequence, player, tnt, true), player)).IsAccepted);
        room.RunTicks(20);

        // Surviving the ticks is the assertion: the pre-fix room threw inside the next
        // ApplyCommands. The rig must also still be published, on two bodies, i.e. actually split.
        List<SnapshotEntity> entities = PublishEntities(room);
        string dump = string.Join(" ", entities.Select(entity => $"{entity.EntityId}:{entity.PartTypeId}@b{entity.PhysicsBodyId}"));
        Assert.True(entities.Any(entity => entity.EntityId == tail), $"the tail must survive: {dump}");
        Assert.True(entities.Any(entity => entity.EntityId == frame), $"the frame must survive: {dump}");
        Assert.False(entities.Any(entity => entity.EntityId == tnt), $"the charge must be spent: {dump}");
        SnapshotEntity frameEntity = entities.Single(entity => entity.EntityId == frame);
        SnapshotEntity tailEntity = entities.Single(entity => entity.EntityId == tail);
        Assert.NotEqual(frameEntity.PhysicsBodyId, tailEntity.PhysicsBodyId);
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
