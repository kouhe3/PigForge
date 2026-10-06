using PigForge.Core;
using PigForge.Core.Content;
using PigForge.Physics.Abstractions;
using PigForge.Physics.Bepu;
using PigForge.Protocol;
using PigForge.Server;

namespace PigForge.Server.Tests;

/// <summary>
/// The boxing glove in a real room (docs/specs/boxing-glove.md §3-§6), real Bepu and real content:
/// a placed glove part spawns a second body, its button (content `activation: trigger`) throws that
/// body down the part's own -Y to the skin's distance, the throw knocks the part behind it off the
/// contraption, the limp glove winds back home on its own shoot time, and the same button throws it
/// again. The numbers below are what the shipped part does.
/// </summary>
public sealed class BoxingGloveRoomTests
{
    private const uint PartBoxingGlove = 28;
    private const uint PartWoodenBlock = 1;

    /// <summary>The skin's throw: 2.5 m down the part's local -Y (spec §1.1 and §3).</summary>
    private const float ThrowDistance = 2.5f;

    [Fact]
    public void APlacedGloveSpawnsAFistTheFrameHidesUntilItIsThrown()
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
        // Wound up, the fist is the original's *inactive* GameObject (`InitilizeBoxingGlove` ends
        // with `m_BoxingGlove.SetActive(false)`, SpringBoxingGlove.cs:215-222): nothing is drawn, so
        // nothing is published -- the client shows the box alone. The body itself is still in the
        // world (SubEntityCount above) and the physics-level test covers the drive that holds it.
        Assert.False(FistIsOut(entities, glove));
        Assert.Single(GloveParts(entities));
        Assert.Equal(glove, GloveParts(entities)[0].EntityId);
        // The sub-entity is inside the room's own snapshot bound (ADR-027).
        Assert.True(room.MaxSnapshotEntityCount >= entities.Count);

        // The throw puts it on the wire, flagged as the sub-entity it is (PGFS v5 bit1) so the
        // client draws the fist instead of a second glove part.
        Assert.True(room.Submit(PlayHost.BindPlayer(new SetPartActiveCommand(0, ++sequence, player, glove, true), player)).IsAccepted);
        room.RunTicks(6);
        List<SnapshotEntity> thrown = PublishEntities(room);
        (SnapshotEntity host, SnapshotEntity sub) = GlovePair(thrown, glove);
        Assert.Equal((byte)0, host.Flags & 0b10);
        Assert.Equal((byte)0b10, sub.Flags & 0b10);
        Assert.NotEqual(host.PhysicsBodyId, sub.PhysicsBodyId);
        Assert.NotEqual(0u, sub.PhysicsBodyId);
        Assert.True(room.MaxSnapshotEntityCount >= thrown.Count);
    }

    [Fact]
    public void APunchThrowsTheGloveToTheSkinsDistanceThenWindsItHome()
    {
        using GameRoom room = CreateGloveRoom();
        uint player = PlayHost.NextPlayerId();
        uint sequence = 0;
        uint glove = Place(room, ref sequence, player, PartBoxingGlove, 0f, 8f);
        Assert.True(room.Submit(PlayHost.BindPlayer(new StartSimulationCommand(0, ++sequence, player), player)).IsAccepted);

        // Wound up: the fist is stowed, so the frame carries the box alone.
        Assert.False(FistIsOut(PublishEntities(room), glove));

        // The button: the glove is thrown (activation: trigger means SetPartActive is the press).
        Assert.True(room.Submit(PlayHost.BindPlayer(new SetPartActiveCommand(0, ++sequence, player, glove, true), player)).IsAccepted);

        // The fist only joins the frame once the machine has run: its first tick is what throws it.
        SnapshotEntity host = default;
        SnapshotEntity sub = default;
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

        // Then the wind-back: limp, home within the original's "or home within 0.1 m" test. The
        // fist leaves the frame when the machine reaches WindedUp again -- the original deactivates
        // the glove GameObject there, which is exactly the stowed state the client stops drawing.
        int windTicks = 0;
        for (; windTicks < 120; windTicks++)
        {
            room.Tick();
            if (!FistIsOut(PublishEntities(room), glove))
            {
                break;
            }
        }

        Assert.True(windTicks < 120, "the limp glove must come home");
        // The original's own probe measures 0.22-0.34 s; the room's 1/60 steps put it in that
        // neighbourhood (the home test fires well before the declared 1 s wind time).
        Assert.InRange(windTicks * (1f / 60f), 0.05f, 0.9f);

        // Repeatable: the button is momentary, so a fresh press throws the same glove again.
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
    public void APressTheThrowRefusesIsStillSpent()
    {
        using GameRoom room = CreateGloveRoom();
        uint player = PlayHost.NextPlayerId();
        uint sequence = 0;
        uint glove = Place(room, ref sequence, player, PartBoxingGlove, 0f, 8f);
        Assert.True(room.Submit(PlayHost.BindPlayer(new StartSimulationCommand(0, ++sequence, player), player)).IsAccepted);

        Assert.True(room.Submit(PlayHost.BindPlayer(new SetPartActiveCommand(0, ++sequence, player, glove, true), player)).IsAccepted);
        room.RunTicks(6);

        // The fist is out, so a second press changes nothing (the machine only throws from rest) —
        // but it must not leave the button latched on either: the next snapshot reports it clear, so
        // the bar keeps drawing a button rather than a switch stuck on its "on" position.
        Assert.True(room.Submit(PlayHost.BindPlayer(new SetPartActiveCommand(0, ++sequence, player, glove, true), player)).IsAccepted);
        room.Tick();
        Assert.Equal((byte)0, PublishEntities(room).Single(entity => entity.EntityId == glove).Flags & 1);
    }

    [Fact]
    public void AToggleGloveThrowsOnItsSwitchLevelAndStaysLatchedOn()
    {
        // The branch the original's IN `SwitchableBoxingGlove` selects (spec §2/§5): the same
        // machine, driven by the switch level, and the level stays on. Shipped content is the
        // momentary branch instead, so the room is built from the shipped document with that one
        // field flipped -- which is also what proves content decides the path.
        using GameRoom room = CreateGloveRoom(PartActivation.Toggle);
        uint player = PlayHost.NextPlayerId();
        uint sequence = 0;
        uint glove = Place(room, ref sequence, player, PartBoxingGlove, 0f, 8f);
        Assert.True(room.Submit(PlayHost.BindPlayer(new StartSimulationCommand(0, ++sequence, player), player)).IsAccepted);

        Assert.True(room.Submit(PlayHost.BindPlayer(new SetPartActiveCommand(0, ++sequence, player, glove, true), player)).IsAccepted);
        room.Tick();

        (SnapshotEntity host, SnapshotEntity sub) = GlovePair(PublishEntities(room), glove);
        float peak = 0f;
        for (int tick = 0; tick < 24; tick++)
        {
            room.Tick();
            (host, sub) = GlovePair(PublishEntities(room), glove);
            peak = MathF.Max(peak, Offset(host, sub));
        }

        // A level for this content, not a button press: the switch stays on in the snapshot.
        Assert.Equal((byte)1, PublishEntities(room).Single(entity => entity.EntityId == glove).Flags & 1);
        Assert.True(peak >= ThrowDistance * 0.9f, $"peak {peak} must reach the throw distance");

        // The machine still runs its own cycle (the branches differ in what drives the throw, not
        // in the machine): the limp fist is home within the wind time while the level is still on,
        // and the frame drops it again the moment the machine is wound up.
        room.RunTicks(60);
        Assert.False(FistIsOut(PublishEntities(room), glove));
        Assert.Equal((byte)1, PublishEntities(room).Single(entity => entity.EntityId == glove).Flags & 1);

        // A press while the level is already on is not a fresh off→on edge: nothing throws.
        Assert.True(room.Submit(PlayHost.BindPlayer(new SetPartActiveCommand(0, ++sequence, player, glove, true), player)).IsAccepted);
        room.RunTicks(6);
        Assert.False(FistIsOut(PublishEntities(room), glove));

        // Off and on again throws: the level is the trigger, the press is not.
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

        Assert.True(secondPeak >= ThrowDistance * 0.9f, $"the fresh edge must throw too ({secondPeak})");
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
        // Rebuilt and wound up again: the box is back on the wire, the fist is stowed with it.
        Assert.Single(GloveParts(entities));
        Assert.Equal(glove, GloveParts(entities)[0].EntityId);
        Assert.False(FistIsOut(entities, glove));
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

    /// <summary>A sandbox room at the default gameplay config; the glove needs no custom numbers.
    /// The shipped content is the momentary branch, so the toggle branch builds its own room from
    /// the shipped document with part 28's activation flipped.</summary>
    private static GameRoom CreateGloveRoom(PartActivation activation = PartActivation.Trigger)
    {
        if (activation == PartActivation.Trigger)
        {
            return PlayHost.CreateSandboxRoom();
        }

        string root = FindRepositoryRoot();
        PartContentDocument document = PartContentParser.Parse(
            File.ReadAllText(Path.Combine(root, "content", "parts.json")));
        PartContentDocument overridden = document with
        {
            Parts = document.Parts
                .Select(part => part.PartTypeId == PartBoxingGlove && part.Capabilities is not null
                    ? part with { Capabilities = part.Capabilities with { Activation = activation } }
                    : part)
                .ToArray(),
        };
        LevelContentDocument level = LevelContentLibrary.Parse(
            File.ReadAllText(Path.Combine(root, "content", "levels", "terrain-v1.json")));
        GameplayConfig config = new(
            level.GoalZone,
            level.MapBounds,
            TntBlastRadius: 4f,
            TntBlastImpulse: 25f,
            TntIgniteImpactSpeed: 5f,
            MaxTicks: 1200,
            ObjectivesEnabled: false);
        GameRoom room = new(GameRoomOptions.Create(
            new PartContentLibrary(overridden),
            () => new BepuPhysicsWorld(new PhysicsVector3(0f, -9.81f, 0f)),
            config,
            sandboxMode: true));
        room.SetupFromLevel(level);
        return room;
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "content", "parts.json")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        return directory!.FullName;
    }

    /// <summary>The frame's entities carrying the glove part's type: the placed box alone while the
    /// fist is stowed, the box and the fist while it is thrown.</summary>
    private static List<SnapshotEntity> GloveParts(List<SnapshotEntity> entities) =>
        entities.Where(entity => entity.PartTypeId == PartBoxingGlove).ToList();

    /// <summary>True when the frame carries the glove's fist: the host's wound-up state stows it
    /// (the original's inactive GameObject, <c>SpringBoxingGlove.cs:215-222</c>), so the client
    /// draws the box alone until the throw puts it back on the wire.</summary>
    private static bool FistIsOut(List<SnapshotEntity> entities, uint hostEntityId) =>
        entities.Any(entity => entity.EntityId != hostEntityId
            && entity.PartTypeId == PartBoxingGlove
            && (entity.Flags & 0b10) == 0b10);

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
