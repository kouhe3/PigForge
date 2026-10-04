using PigForge.Core;
using PigForge.Protocol;
using PigForge.Server;

namespace PigForge.Server.Tests;

/// <summary>
/// The boxing glove in a real room (docs/specs/boxing-glove.md §3-§6), real Bepu and real content:
/// a placed glove part spawns a second body, the toggle throws it down the part's own -Y to the
/// skin's distance, the throw knocks the part behind it off the contraption, the limp glove winds
/// back home, and the whole thing can be thrown again. The numbers below are what the shipped part
/// does.
/// </summary>
public sealed class BoxingGloveRoomTests
{
    private const uint PartBoxingGlove = 28;
    private const uint PartWoodenBlock = 1;

    /// <summary>The skin's throw: 2.5 m down the part's local -Y (spec §1.1 and §3).</summary>
    private const float ThrowDistance = 2.5f;

    [Fact]
    public void APlacedGloveSpawnsASecondBodyHeldAtHome()
    {
        using GameRoom room = PlayHost.CreateSandboxRoom();
        uint player = PlayHost.NextPlayerId();
        uint sequence = 0;
        uint glove = Place(room, ref sequence, player, PartBoxingGlove, 0f, 6f);
        Assert.True(room.Submit(PlayHost.BindPlayer(new StartSimulationCommand(0, ++sequence, player), player)).IsAccepted);

        // One runtime sub-entity for the glove body, and no spring seam: the glove is a driven
        // body, not a spring (the old bounce-pad path is gone from the rules layer entirely).
        Assert.Equal(1, room.SubEntityCount);
        Assert.Equal(0, room.SpringJointCount);

        room.RunTicks(30);
        List<SnapshotEntity> entities = PublishEntities(room);
        (SnapshotEntity host, SnapshotEntity sub) = GlovePair(entities, glove);
        Assert.NotEqual(host.PhysicsBodyId, sub.PhysicsBodyId);
        Assert.NotEqual(0u, sub.PhysicsBodyId);
        // Wound up: the skin's yDrive holds the glove against the part (only the servo's own sag).
        Assert.InRange(MathF.Abs(Offset(host, sub)), 0f, 0.05f);
        // The sub-entity is inside the room's own snapshot bound (ADR-027).
        Assert.True(room.MaxSnapshotEntityCount >= entities.Count);
    }

    [Fact]
    public void APunchThrowsTheGloveToTheSkinsDistanceThenWindsItHome()
    {
        using GameRoom room = CreateGloveRoom();
        uint player = PlayHost.NextPlayerId();
        uint sequence = 0;
        uint glove = Place(room, ref sequence, player, PartBoxingGlove, 0f, 8f);
        Assert.True(room.Submit(PlayHost.BindPlayer(new StartSimulationCommand(0, ++sequence, player), player)).IsAccepted);

        (SnapshotEntity host, SnapshotEntity sub) = GlovePair(PublishEntities(room), glove);
        Assert.InRange(MathF.Abs(Offset(host, sub)), 0f, 0.05f);

        // Toggle on: the glove is thrown (activation: toggle means SetPartActive is the trigger).
        Assert.True(room.Submit(PlayHost.BindPlayer(new SetPartActiveCommand(0, ++sequence, player, glove, true), player)).IsAccepted);

        float peak = 0f;
        float atShootEnd = 0f;
        for (int tick = 0; tick < 24; tick++)
        {
            room.Tick();
            (host, sub) = GlovePair(PublishEntities(room), glove);
            peak = MathF.Max(peak, Offset(host, sub));
            atShootEnd = Offset(host, sub);
        }

        // The throw reaches the skin's 2.5 m and is settled there when the shoot time runs out
        // (the original's 0.4 s; the acceptance band is ±10%).
        Assert.True(peak >= ThrowDistance * 0.9f, $"peak {peak} must reach the throw distance");
        Assert.InRange(atShootEnd, ThrowDistance * 0.9f, ThrowDistance * 1.1f);

        // Then the wind-back: limp, home within the original's "or home within 0.1 m" test.
        int windTicks = 0;
        for (; windTicks < 120; windTicks++)
        {
            room.Tick();
            (host, sub) = GlovePair(PublishEntities(room), glove);
            if (MathF.Abs(Offset(host, sub)) < 0.1f)
            {
                break;
            }
        }

        Assert.True(windTicks < 120, "the limp glove must come home");
        // The original's own probe measures 0.22-0.34 s; the room's 1/60 steps put it in that
        // neighbourhood (the home test fires well before the declared 1 s wind time).
        Assert.InRange(windTicks * (1f / 60f), 0.05f, 0.9f);

        // Repeatable: the switch is still on, so the glove waits for a fresh off→on edge and throws
        // again.
        Assert.True(room.Submit(PlayHost.BindPlayer(new SetPartActiveCommand(0, ++sequence, player, glove, false), player)).IsAccepted);
        room.Tick();
        Assert.True(room.Submit(PlayHost.BindPlayer(new SetPartActiveCommand(0, ++sequence, player, glove, true), player)).IsAccepted);

        float secondPeak = 0f;
        for (int tick = 0; tick < 24; tick++)
        {
            room.Tick();
            (host, sub) = GlovePair(PublishEntities(room), glove);
            secondPeak = MathF.Max(secondPeak, Offset(host, sub));
        }

        Assert.True(secondPeak >= ThrowDistance * 0.9f, $"the second punch must throw too ({secondPeak})");
    }

    [Fact]
    public void APunchDetachesThePartBehindTheGlove()
    {
        using GameRoom room = CreateGloveRoom();
        uint player = PlayHost.NextPlayerId();
        uint sequence = 0;
        uint glove = Place(room, ref sequence, player, PartBoxingGlove, 0f, 7f);
        uint behind = Place(room, ref sequence, player, PartWoodenBlock, 0f, 6f);
        Assert.True(room.Submit(PlayHost.BindPlayer(new StartSimulationCommand(0, ++sequence, player), player)).IsAccepted);

        List<SnapshotEntity> before = PublishEntities(room);
        // The part in the effect direction (the glove's own -Y, one cell below) is welded into the
        // glove's body while the glove is wound up.
        Assert.Equal(BodyOf(before, glove), BodyOf(before, behind));

        Assert.True(room.Submit(PlayHost.BindPlayer(new SetPartActiveCommand(0, ++sequence, player, glove, true), player)).IsAccepted);
        room.RunTicks(3);

        List<SnapshotEntity> after = PublishEntities(room);
        // The throw destroys every joint the part behind the glove carries, so it goes its own way
        // (SpringBoxingGlove.cs:224-262) — detached, not destroyed.
        Assert.NotEqual(BodyOf(after, glove), BodyOf(after, behind));
        Assert.True(after.Any(entity => entity.EntityId == glove), "the glove host must survive");
        Assert.True(after.Any(entity => entity.EntityId == behind), "the detached part must survive");
        // And the glove itself is still the sub-entity the punch threw.
        (SnapshotEntity host, SnapshotEntity sub) = GlovePair(after, glove);
        Assert.NotEqual(host.PhysicsBodyId, sub.PhysicsBodyId);
        Assert.NotEqual(BodyOf(after, behind), sub.PhysicsBodyId);
    }

    [Fact]
    public void APlayerResetTakesTheGloveWithItAndAFreshStartRebuildsIt()
    {
        using GameRoom room = CreateGloveRoom();
        uint player = PlayHost.NextPlayerId();
        uint sequence = 0;
        uint glove = Place(room, ref sequence, player, PartBoxingGlove, 0f, 7f);
        Assert.True(room.Submit(PlayHost.BindPlayer(new StartSimulationCommand(0, ++sequence, player), player)).IsAccepted);
        Assert.Equal(1, room.SubEntityCount);

        Assert.True(room.Submit(PlayHost.BindPlayer(new RetryCommand(0, ++sequence, player), player)).IsAccepted);
        Assert.Equal(0, room.SubEntityCount);

        Assert.True(room.Submit(PlayHost.BindPlayer(new StartSimulationCommand(0, ++sequence, player), player)).IsAccepted);
        Assert.Equal(1, room.SubEntityCount);
        List<SnapshotEntity> entities = PublishEntities(room);
        (SnapshotEntity host, SnapshotEntity sub) = GlovePair(entities, glove);
        Assert.NotEqual(host.PhysicsBodyId, sub.PhysicsBodyId);
    }

    [Fact]
    public void APlayerLeavingTakesTheGloveSubEntityWithIt()
    {
        using GameRoom room = CreateGloveRoom();
        uint player = PlayHost.NextPlayerId();
        uint sequence = 0;
        Place(room, ref sequence, player, PartBoxingGlove, 0f, 7f);
        Assert.True(room.Submit(PlayHost.BindPlayer(new StartSimulationCommand(0, ++sequence, player), player)).IsAccepted);
        Assert.Equal(1, room.SubEntityCount);

        room.LeavePlayer(player);

        Assert.Equal(0, room.SubEntityCount);
    }

    [Fact]
    public void TheGloveRoomIsDeterministicAcrossRuns()
    {
        Assert.Equal(RunGloveRig(), RunGloveRig());
    }

    private static long RunGloveRig()
    {
        using GameRoom room = CreateGloveRoom();
        uint player = PlayHost.NextPlayerId();
        uint sequence = 0;
        uint glove = Place(room, ref sequence, player, PartBoxingGlove, 0f, 7f);
        Place(room, ref sequence, player, PartWoodenBlock, 0f, 6f);
        Assert.True(room.Submit(PlayHost.BindPlayer(new StartSimulationCommand(0, ++sequence, player), player)).IsAccepted);
        Assert.True(room.Submit(PlayHost.BindPlayer(new SetPartActiveCommand(0, ++sequence, player, glove, true), player)).IsAccepted);
        room.RunTicks(90);
        return room.ComputeStateHash();
    }

    /// <summary>A sandbox room at the default gameplay config; the glove needs no custom numbers.</summary>
    private static GameRoom CreateGloveRoom() => PlayHost.CreateSandboxRoom();

    /// <summary>The two published entities sharing the glove part's type: the placed part and the
    /// sub-entity the room spawned for it.</summary>
    private static (SnapshotEntity Host, SnapshotEntity Sub) GlovePair(List<SnapshotEntity> entities, uint hostEntityId)
    {
        SnapshotEntity host = entities.Single(entity => entity.EntityId == hostEntityId);
        SnapshotEntity sub = entities.Single(
            entity => entity.EntityId != hostEntityId && entity.PartTypeId == host.PartTypeId);
        return (host, sub);
    }

    private static uint BodyOf(List<SnapshotEntity> entities, uint entityId) =>
        entities.Single(entity => entity.EntityId == entityId).PhysicsBodyId;

    /// <summary>
    /// The glove's offset down the host part's own -Y in metres, straight off the wire: exactly the
    /// distance the machine's throw and wind-back are measured in.
    /// </summary>
    private static float Offset(in SnapshotEntity host, in SnapshotEntity sub)
    {
        float yaw = 2f * MathF.Atan2(host.Rotation.Z, host.Rotation.W);
        float dx = sub.Position.X - host.Position.X;
        float dy = sub.Position.Y - host.Position.Y;
        // The part's local -Y in world space: (sin yaw, -cos yaw).
        return (dx * MathF.Sin(yaw)) - (dy * MathF.Cos(yaw));
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
