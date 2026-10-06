using PigForge.Core.Construction;
using PigForge.Protocol;
using PigForge.Server;

namespace PigForge.Server.Tests;

/// <summary>
/// The build pose's handedness through a real room (ADR-030, docs/specs/part-mirror.md): the
/// original's <c>BasePart.SetFlipped</c> turns a part 180 degrees about its own up axis inside its
/// own frame -- a chirality a float yaw cannot express -- so PGFC v3 carries it beside the absolute
/// angle and PGFS v6 publishes it as <c>flags</c> bit2. Content gates it: only the 17 parts whose
/// prefab says <c>m_autoAlign == FlipVertically</c> (the wing and tail families) accept it.
/// </summary>
public sealed class PartMirrorRoomTests
{
    private const uint PartFrame = 1;
    private const uint PartGliderWing = 31;

    [Fact]
    public void APlacedMirroredPartPublishesTheMirrorFlagAndAPlainOneDoesNot()
    {
        using GameRoom room = PlayHost.CreateSandboxRoom();
        uint player = PlayHost.NextPlayerId();
        uint sequence = 0;

        uint plain = Place(room, ref sequence, player, PartGliderWing, -14f, 14f, mirrored: false);
        uint mirrored = Place(room, ref sequence, player, PartGliderWing, -12f, 14f, mirrored: true);

        List<SnapshotEntity> entities = PublishEntities(room);
        Assert.Equal((byte)0, entities.Single(entity => entity.EntityId == plain).Flags);
        Assert.Equal(SnapshotFrame.FlagMirrored, entities.Single(entity => entity.EntityId == mirrored).Flags);
    }

    [Fact]
    public void APartWithoutTheMirrorCapabilityIsRejected()
    {
        using GameRoom room = PlayHost.CreateSandboxRoom();
        uint player = PlayHost.NextPlayerId();
        uint sequence = 0;

        CommandOutcome refused = room.Submit(PlayHost.BindPlayer(
            new PlacePartCommand(0, ++sequence, player, PartFrame, -14f, 14f, 0f, 1f, Mirrored: true),
            player));

        Assert.Equal(CommandStatus.RuleRejected, refused.Status);
        Assert.Equal(ConstructionError.PartNotMirrorable, refused.Error);
        // The sandbox level's own geometry is published regardless; what matters is that no entity
        // claims the mirror and no part was placed.
        Assert.All(PublishEntities(room), entity => Assert.NotEqual(SnapshotFrame.FlagMirrored, entity.Flags));
    }

    [Fact]
    public void ARotateCarriesTheAbsoluteMirror()
    {
        using GameRoom room = PlayHost.CreateSandboxRoom();
        uint player = PlayHost.NextPlayerId();
        uint sequence = 0;

        uint wing = Place(room, ref sequence, player, PartGliderWing, -14f, 14f, mirrored: true);
        Assert.Equal(SnapshotFrame.FlagMirrored, PublishEntities(room).Single(entity => entity.EntityId == wing).Flags);

        // The field is absolute: a rotate that says false clears it, and one that says true sets it
        // again -- exactly how the client sends the mirror it is showing (ADR-030).
        Assert.True(room.Submit(PlayHost.BindPlayer(
            new RotatePartCommand(0, ++sequence, player, wing, 1.5f, Mirrored: false), player)).IsAccepted);
        Assert.Equal((byte)0, PublishEntities(room).Single(entity => entity.EntityId == wing).Flags);

        Assert.True(room.Submit(PlayHost.BindPlayer(
            new RotatePartCommand(0, ++sequence, player, wing, 1.5f, Mirrored: true), player)).IsAccepted);
        Assert.Equal(SnapshotFrame.FlagMirrored, PublishEntities(room).Single(entity => entity.EntityId == wing).Flags);
    }

    [Fact]
    public void AResetRebuildsTheLayoutMirrored()
    {
        using GameRoom room = PlayHost.CreateSandboxRoom();
        uint player = PlayHost.NextPlayerId();
        uint sequence = 0;

        uint wing = Place(room, ref sequence, player, PartGliderWing, -14f, 14f, mirrored: true);
        uint frame = Place(room, ref sequence, player, PartFrame, -14f, 15f, angle: 0.4f);
        Assert.True(room.Submit(PlayHost.BindPlayer(new StartSimulationCommand(0, ++sequence, player), player)).IsAccepted);

        List<SnapshotEntity> running = PublishEntities(room);
        Assert.Equal(SnapshotFrame.FlagMirrored, running.Single(entity => entity.EntityId == wing).Flags);

        // RESET puts the layout back to its pre-Start state, hand and yaw included: the rebuild
        // places from the captured layout, so a mirror that only lived in the pose would be lost.
        Assert.True(room.Submit(PlayHost.BindPlayer(new RetryCommand(0, ++sequence, player), player)).IsAccepted);
        List<SnapshotEntity> rebuilt = PublishEntities(room);
        Assert.Equal(SnapshotFrame.FlagMirrored, rebuilt.Single(entity => entity.EntityId == wing).Flags);
        Assert.Equal((byte)0, rebuilt.Single(entity => entity.EntityId == frame).Flags);
        Assert.Equal(0.4f, YawOf(rebuilt.Single(entity => entity.EntityId == frame).Rotation), 2);
    }

    private static float YawOf(ReplayQuaternion rotation) =>
        MathF.Atan2(
            2f * ((rotation.W * rotation.Z) + (rotation.X * rotation.Y)),
            1f - (2f * ((rotation.Y * rotation.Y) + (rotation.Z * rotation.Z))));

    private static uint Place(GameRoom room, ref uint sequence, uint player, uint partTypeId, float x, float y, float angle = 0f, bool mirrored = false)
    {
        CommandOutcome outcome = room.Submit(PlayHost.BindPlayer(
            new PlacePartCommand(0, ++sequence, player, partTypeId, x, y, angle, 1f, Mirrored: mirrored),
            player));
        Assert.True(outcome.IsAccepted, $"place {partTypeId} at ({x},{y}) angle {angle} mirrored {mirrored}: {outcome.Status}/{outcome.Error}");
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
