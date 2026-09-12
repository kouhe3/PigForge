using PigForge.Core;
using PigForge.Core.Construction;
using PigForge.Core.Content;
using PigForge.Protocol;
using PigForge.Physics.Abstractions;
using PigForge.Server;

namespace PigForge.Server.Tests;

public sealed class SandboxRoomTests
{
    private const uint PartBlock = 1;
    private const uint PartPig = 2;
    private const uint PartEgg = 4;
    private const uint PartGround = 5;
    private const uint PartMotor = 6;
    private const uint PlayerOne = 1;
    private const uint PlayerTwo = 2;

    private static readonly GameplayZone FarBounds = new(
        new PhysicsVector3(-1000f, -1000f, -1000f),
        new PhysicsVector3(1000f, 1000f, 1000f));

    [Fact]
    public void SandboxPlayersRegistersUnknownPlayersInAscendingOrder()
    {
        SandboxPlayers players = new();

        players.Register(7);
        players.Register(2);
        players.MarkMaterialized(5);

        Assert.Equal(new uint[] { 2, 5, 7 }, players.KnownPlayers);
        Assert.False(players.IsMaterialized(2));
        Assert.True(players.IsMaterialized(5));
        Assert.False(players.IsMaterialized(9));

        players.MarkEditing(5);

        Assert.False(players.IsMaterialized(5));
        Assert.Equal(new uint[] { 2, 5, 7 }, players.KnownPlayers);
    }

    [Fact]
    public void SandboxRoomStartsRunningWithLevelActorsBound()
    {
        ScriptedWorld world = new();
        using GameRoom room = CreateSandboxRoom(world);

        room.SetupFromLevel(Level(
            new LevelSpawnDefinition(PartGround, new PhysicsVector3(0f, -0.5f, 0f)),
            new LevelSpawnDefinition(PartBlock, new PhysicsVector3(0f, 4f, 0f))));

        Assert.Equal(RoomMode.Running, room.Mode);
        Assert.Equal(2, room.BodyCount);
        Assert.Equal(0u, room.CurrentTick);

        List<SnapshotEntity> entities = PublishEntities(room, out SnapshotFrameHeader header);

        Assert.Equal((byte)GameplayPhase.Playing, header.Phase);
        Assert.Equal(2u, header.EntityCount);
        Assert.All(entities, entity => Assert.NotEqual(0u, entity.PhysicsBodyId));
    }

    [Fact]
    public void LegacyRoomStaysBuildingAfterLevelSetup()
    {
        ScriptedWorld world = new();
        using GameRoom room = CreateLegacyRoom(world);

        room.SetupFromLevel(Level(new LevelSpawnDefinition(PartGround, new PhysicsVector3(0f, -0.5f, 0f))));

        Assert.Equal(RoomMode.Building, room.Mode);
        Assert.Equal(0, room.BodyCount);

        PublishEntities(room, out SnapshotFrameHeader header);

        Assert.Equal(SnapshotFrame.BuildingPhase, header.Phase);
    }

    [Fact]
    public void PlacedPartIsPublishedAsPreviewWithZeroBodyAndVelocities()
    {
        ScriptedWorld world = new();
        using GameRoom room = CreateSandboxRoom(world);
        room.SetupFromLevel(Level());

        CommandOutcome placed = room.Submit(PlacePart(1, PlayerOne, PartBlock, 2.25f, 3.75f));

        Assert.True(placed.IsAccepted);
        Assert.Equal(0, room.BodyCount);

        List<SnapshotEntity> entities = PublishEntities(room, out SnapshotFrameHeader header);

        Assert.Equal((byte)GameplayPhase.Playing, header.Phase);
        SnapshotEntity preview = Assert.Single(entities);
        Assert.Equal(placed.EntityId, preview.EntityId);
        Assert.Equal(0u, preview.PhysicsBodyId);
        Assert.Equal(2.25f, preview.Position.X);
        Assert.Equal(3.75f, preview.Position.Y);
        Assert.Equal(0f, preview.LinearVelocity.X);
        Assert.Equal(0f, preview.AngularVelocity.Z);
    }

    [Fact]
    public void StartMaterialisesOnlyThatPlayersLayout()
    {
        ScriptedWorld world = new();
        using GameRoom room = CreateSandboxRoom(world);
        room.SetupFromLevel(Level());
        uint first = room.Submit(PlacePart(1, PlayerOne, PartBlock, 0.5f, 0.5f)).EntityId;
        uint second = room.Submit(PlacePart(1, PlayerTwo, PartBlock, 6.5f, 0.5f)).EntityId;

        Assert.True(room.Submit(Start(2, PlayerOne)).IsAccepted);
        Assert.Equal(1, room.BodyCount);

        List<SnapshotEntity> entities = PublishEntities(room, out _);

        Assert.Equal(new[] { first, second }, entities.Select(entity => entity.EntityId).ToArray());
        Assert.NotEqual(0u, entities[0].PhysicsBodyId);
        Assert.Equal(0u, entities[1].PhysicsBodyId);
        Assert.Equal(0f, entities[1].LinearVelocity.X);
    }

    [Fact]
    public void ResetDestroysOnlyThatPlayersEntities()
    {
        ScriptedWorld world = new();
        using GameRoom room = CreateSandboxRoom(world);
        room.SetupFromLevel(Level());
        uint first = room.Submit(PlacePart(1, PlayerOne, PartBlock, 0.5f, 0.5f)).EntityId;
        uint second = room.Submit(PlacePart(1, PlayerTwo, PartBlock, 6.5f, 0.5f)).EntityId;
        Assert.True(room.Submit(Start(2, PlayerOne)).IsAccepted);
        Assert.True(room.Submit(Start(2, PlayerTwo)).IsAccepted);
        Assert.Equal(2, room.BodyCount);

        Assert.True(room.Submit(Reset(3, PlayerOne)).IsAccepted);

        List<SnapshotEntity> entities = PublishEntities(room, out _);

        SnapshotEntity survivor = Assert.Single(entities);
        Assert.Equal(second, survivor.EntityId);
        Assert.NotEqual(0u, survivor.PhysicsBodyId);
        Assert.DoesNotContain(world.DestroyedBodies, body => body.Value == survivor.PhysicsBodyId);
        Assert.Equal(1, room.BodyCount);

        // The reset player is back to editing and can place again.
        Assert.True(room.Submit(PlacePart(4, PlayerOne, PartBlock, 1.5f, 0.5f)).IsAccepted);
        Assert.DoesNotContain(PublishEntities(room, out _), entity => entity.EntityId == first);
    }

    [Fact]
    public void ResetLeavesOtherPlayersEntitiesUntouchedAcrossIdenticalRuns()
    {
        ResetScriptOutcome first = RunResetScript();
        ResetScriptOutcome second = RunResetScript();

        Assert.Equal(first.EntityIdsBeforeReset, second.EntityIdsBeforeReset);
        Assert.Equal(first.EntityIdsAfterReset, second.EntityIdsAfterReset);
        Assert.Equal(first.StateHashBeforeReset, second.StateHashBeforeReset);
        Assert.Equal(first.StateHashAfterReset, second.StateHashAfterReset);
        Assert.Equal(first.PlayerTwoBeforeReset, second.PlayerTwoBeforeReset);
        Assert.Equal(first.PlayerTwoAfterReset, second.PlayerTwoAfterReset);
        Assert.Equal(first.PlayerTwoBeforeReset, first.PlayerTwoAfterReset);
    }

    [Fact]
    public void MixedFramePublishesWhenPreviewCountExceedsBodyCount()
    {
        ScriptedWorld world = new();
        using GameRoom room = CreateSandboxRoom(world);
        room.SetupFromLevel(Level(new LevelSpawnDefinition(PartGround, new PhysicsVector3(0f, -0.5f, 0f))));
        for (uint index = 0; index < 10; index++)
        {
            Assert.True(room.Submit(PlacePart(index + 1, PlayerOne, PartBlock, 0.5f + index, 0.5f)).IsAccepted);
        }

        for (uint index = 0; index < 5; index++)
        {
            Assert.True(room.Submit(PlacePart(index + 1, PlayerTwo, PartBlock, 0.5f + index, 5.5f)).IsAccepted);
        }

        Assert.True(room.Submit(Start(6, PlayerTwo)).IsAccepted);
        Assert.Equal(6, room.BodyCount);
        Assert.True(room.MaxSnapshotEntityCount >= 16);

        byte[] buffer = new byte[SnapshotFrame.GetMaxByteCount(room.MaxSnapshotEntityCount)];
        Assert.True(room.TryPublishSnapshot(buffer, out int bytesWritten));
        Assert.True(SnapshotFrame.TryDecodeHeader(buffer.AsSpan(0, bytesWritten), out SnapshotFrameHeader header, out SnapshotFrameReader reader));
        List<SnapshotEntity> entities = new();
        while (reader.TryReadEntity(out SnapshotEntity entity))
        {
            entities.Add(entity);
        }

        Assert.Equal(16u, header.EntityCount);
        Assert.Equal(16, entities.Count);
        Assert.Equal(entities.Select(entity => entity.EntityId).OrderBy(value => value), entities.Select(entity => entity.EntityId));
        Assert.Equal(6, entities.Count(entity => entity.PhysicsBodyId != 0));
        Assert.Equal(10, entities.Count(entity => entity.PhysicsBodyId == 0));
    }

    [Fact]
    public void MixedFramePublishesBeyondTheLegacyTwoHundredFiftySixEntityCap()
    {
        ScriptedWorld world = new();
        using GameRoom room = CreateSandboxRoom(world);
        room.SetupFromLevel(Level(new LevelSpawnDefinition(PartGround, new PhysicsVector3(0f, -0.5f, 0f))));
        for (uint index = 0; index < 150; index++)
        {
            Assert.True(room.Submit(PlacePart(index + 1, PlayerOne, PartBlock, (index % 20) * 2f, (index / 20) * 2f)).IsAccepted);
            Assert.True(room.Submit(PlacePart(index + 1, PlayerTwo, PartBlock, (index % 20) * 2f, 40f + (index / 20) * 2f)).IsAccepted);
        }

        Assert.True(room.MaxSnapshotEntityCount > 256);

        byte[] buffer = new byte[SnapshotFrame.GetMaxByteCount(room.MaxSnapshotEntityCount)];
        Assert.True(room.TryPublishSnapshot(buffer, out int bytesWritten));
        Assert.True(SnapshotFrame.TryDecodeHeader(buffer.AsSpan(0, bytesWritten), out SnapshotFrameHeader header, out SnapshotFrameReader reader));
        List<SnapshotEntity> entities = new();
        while (reader.TryReadEntity(out SnapshotEntity entity))
        {
            entities.Add(entity);
        }

        Assert.Equal(301u, header.EntityCount);
        Assert.Equal(301, entities.Count);
        Assert.Equal(300, entities.Count(entity => entity.PhysicsBodyId == 0));
    }

    [Fact]
    public void MaterialisedPlayerCommandsAreRejectedWithWrongMode()
    {
        ScriptedWorld world = new();
        using GameRoom room = CreateSandboxRoom(world);
        room.SetupFromLevel(Level());
        uint entityId = room.Submit(PlacePart(1, PlayerOne, PartBlock, 0.5f, 0.5f)).EntityId;
        Assert.True(room.Submit(Start(2, PlayerOne)).IsAccepted);

        Assert.Equal(CommandStatus.WrongMode, room.Submit(PlacePart(3, PlayerOne, PartBlock, 3.5f, 0.5f)).Status);
        Assert.Equal(CommandStatus.WrongMode, room.Submit(Rotate(3, PlayerOne, entityId, 1.5f)).Status);
        Assert.Equal(CommandStatus.WrongMode, room.Submit(Move(3, PlayerOne, entityId, 2.5f, 0.5f)).Status);
        Assert.Equal(CommandStatus.WrongMode, room.Submit(Scale(3, PlayerOne, entityId, 2f)).Status);
        Assert.Equal(CommandStatus.WrongMode, room.Submit(Remove(3, PlayerOne, entityId)).Status);
        Assert.Equal(CommandStatus.WrongMode, room.Submit(Start(3, PlayerOne)).Status);
        Assert.Equal(CommandStatus.WrongMode, room.Submit(new EnterBuildModeCommand(0, 3, PlayerOne, BuildModePolicy.Keep)).Status);
        Assert.True(room.Submit(Reset(3, PlayerOne)).IsAccepted);
    }

    [Fact]
    public void EditingPlayerMayPlaceRotateRemoveStartAndReset()
    {
        ScriptedWorld world = new();
        using GameRoom room = CreateSandboxRoom(world);
        room.SetupFromLevel(Level());

        uint entityId = room.Submit(PlacePart(1, PlayerOne, PartBlock, 0.5f, 0.5f)).EntityId;
        Assert.True(room.Submit(Rotate(2, PlayerOne, entityId, MathF.PI / 2f)).IsAccepted);
        Assert.True(room.Submit(Remove(3, PlayerOne, entityId)).IsAccepted);
        Assert.Empty(PublishEntities(room, out _));

        Assert.True(room.Submit(PlacePart(4, PlayerOne, PartBlock, 0.5f, 0.5f)).IsAccepted);
        Assert.True(room.Submit(Start(5, PlayerOne)).IsAccepted);
        Assert.True(room.Submit(Reset(6, PlayerOne)).IsAccepted);
        Assert.True(room.Submit(PlacePart(7, PlayerOne, PartBlock, 2.5f, 0.5f)).IsAccepted);
    }

    [Fact]
    public void StartWithEmptyLayoutIsRejected()
    {
        ScriptedWorld world = new();
        using GameRoom room = CreateSandboxRoom(world);
        room.SetupFromLevel(Level());

        CommandOutcome outcome = room.Submit(Start(1, PlayerOne));

        Assert.Equal(CommandStatus.RuleRejected, outcome.Status);
        Assert.Equal(0, room.BodyCount);
    }

    [Fact]
    public void TouchingAnotherPlayersPartIsRejectedWithNotOwnedByPlayer()
    {
        ScriptedWorld world = new();
        using GameRoom room = CreateSandboxRoom(world);
        room.SetupFromLevel(Level());
        uint entityId = room.Submit(PlacePart(1, PlayerOne, PartBlock, 0.5f, 0.5f)).EntityId;

        CommandOutcome rotated = room.Submit(Rotate(1, PlayerTwo, entityId, 1.5f));
        CommandOutcome removed = room.Submit(Remove(2, PlayerTwo, entityId));
        CommandOutcome moved = room.Submit(Move(3, PlayerTwo, entityId, 2.5f, 0.5f));
        CommandOutcome scaled = room.Submit(Scale(4, PlayerTwo, entityId, 2f));

        Assert.Equal(CommandStatus.RuleRejected, rotated.Status);
        Assert.Equal(ConstructionError.NotOwnedByPlayer, rotated.Error);
        Assert.Equal(CommandStatus.RuleRejected, removed.Status);
        Assert.Equal(ConstructionError.NotOwnedByPlayer, removed.Error);
        Assert.Equal(CommandStatus.RuleRejected, moved.Status);
        Assert.Equal(ConstructionError.NotOwnedByPlayer, moved.Error);
        Assert.Equal(CommandStatus.RuleRejected, scaled.Status);
        Assert.Equal(ConstructionError.NotOwnedByPlayer, scaled.Error);
        Assert.Equal(entityId, Assert.Single(PublishEntities(room, out _)).EntityId);
    }

    [Fact]
    public void PerPlayerSequencesAreIndependentAndIdempotent()
    {
        ScriptedWorld world = new();
        using GameRoom room = CreateSandboxRoom(world);
        room.SetupFromLevel(Level());

        Assert.True(room.Submit(PlacePart(5, PlayerOne, PartBlock, 0.5f, 0.5f)).IsAccepted);
        Assert.True(room.Submit(PlacePart(5, PlayerTwo, PartBlock, 6.5f, 0.5f)).IsAccepted);
        Assert.Equal(CommandStatus.Duplicate, room.Submit(PlacePart(5, PlayerOne, PartBlock, 1.5f, 0.5f)).Status);
        Assert.Equal(CommandStatus.Duplicate, room.Submit(PlacePart(5, PlayerTwo, PartBlock, 7.5f, 0.5f)).Status);
        Assert.Equal(CommandStatus.StaleSequence, room.Submit(PlacePart(4, PlayerOne, PartBlock, 1.5f, 0.5f)).Status);
        Assert.Equal(CommandStatus.StaleSequence, room.Submit(PlacePart(4, PlayerTwo, PartBlock, 7.5f, 0.5f)).Status);

        // A gap is not stale: the next accepted sequence only has to exceed the last.
        Assert.True(room.Submit(PlacePart(9, PlayerOne, PartBlock, 1.5f, 0.5f)).IsAccepted);
        Assert.Equal(3, PublishEntities(room, out _).Count);
    }

    [Fact]
    public void AllEntitiesOutOfBoundsResetsOnlyThatPlayer()
    {
        GameplayZone bounds = new(new PhysicsVector3(-10f, -10f, -10f), new PhysicsVector3(10f, 10f, 10f));
        ScriptedWorld world = new();
        using GameRoom room = CreateSandboxRoom(world, bounds);
        room.SetupFromLevel(Level());
        uint first = room.Submit(PlacePart(1, PlayerOne, PartBlock, 0.5f, 0.5f)).EntityId;
        uint second = room.Submit(PlacePart(1, PlayerTwo, PartBlock, 5.5f, 0.5f)).EntityId;
        Assert.True(room.Submit(Start(2, PlayerOne)).IsAccepted);
        Assert.True(room.Submit(Start(2, PlayerTwo)).IsAccepted);
        Dictionary<uint, uint> bodiesByEntity = PublishEntities(room, out _)
            .ToDictionary(entity => entity.EntityId, entity => entity.PhysicsBodyId);

        world.QueueSnapshot(Snapshot(bodiesByEntity[first], new PhysicsVector3(100f, 0.5f, 0f), PhysicsVector3.Zero));
        world.QueueSnapshot(Snapshot(bodiesByEntity[second], new PhysicsVector3(5.5f, 0.5f, 0f), PhysicsVector3.Zero));

        room.Tick();

        List<SnapshotEntity> entities = PublishEntities(room, out _);

        Assert.DoesNotContain(entities, entity => entity.EntityId == first);
        SnapshotEntity survivor = Assert.Single(entities);
        Assert.Equal(second, survivor.EntityId);
        Assert.NotEqual(0u, survivor.PhysicsBodyId);

        // The reset player is editing again; the other player is still materialised.
        Assert.True(room.Submit(PlacePart(3, PlayerOne, PartBlock, 1.5f, 0.5f)).IsAccepted);
        Assert.Equal(CommandStatus.WrongMode, room.Submit(PlacePart(3, PlayerTwo, PartBlock, 7.5f, 0.5f)).Status);
    }

    [Fact]
    public void SingleEntityInsideBoundsDoesNotResetPlayer()
    {
        GameplayZone bounds = new(new PhysicsVector3(-10f, -10f, -10f), new PhysicsVector3(10f, 10f, 10f));
        ScriptedWorld world = new();
        using GameRoom room = CreateSandboxRoom(world, bounds);
        room.SetupFromLevel(Level());
        uint first = room.Submit(PlacePart(1, PlayerOne, PartBlock, 0.5f, 0.5f)).EntityId;
        uint second = room.Submit(PlacePart(2, PlayerOne, PartBlock, 20.5f, 0.5f)).EntityId;
        Assert.True(room.Submit(Start(3, PlayerOne)).IsAccepted);
        Dictionary<uint, uint> bodiesByEntity = PublishEntities(room, out _)
            .ToDictionary(entity => entity.EntityId, entity => entity.PhysicsBodyId);

        world.QueueSnapshot(Snapshot(bodiesByEntity[first], new PhysicsVector3(100f, 0.5f, 0f), PhysicsVector3.Zero));
        world.QueueSnapshot(Snapshot(bodiesByEntity[second], new PhysicsVector3(5.5f, 0.5f, 0f), PhysicsVector3.Zero));

        room.Tick();

        List<SnapshotEntity> entities = PublishEntities(room, out _);

        Assert.Equal(2, entities.Count);
        Assert.All(entities, entity => Assert.NotEqual(0u, entity.PhysicsBodyId));
        Assert.Equal(CommandStatus.WrongMode, room.Submit(PlacePart(4, PlayerOne, PartBlock, 1.5f, 0.5f)).Status);
    }

    [Fact]
    public void PreviewOutsideBoundsDoesNotResetEditingPlayer()
    {
        GameplayZone bounds = new(new PhysicsVector3(-10f, -10f, -10f), new PhysicsVector3(10f, 10f, 10f));
        ScriptedWorld world = new();
        using GameRoom room = CreateSandboxRoom(world, bounds);
        room.SetupFromLevel(Level());
        uint preview = room.Submit(PlacePart(1, PlayerOne, PartBlock, 500f, 0.5f)).EntityId;

        room.RunTicks(2);

        SnapshotEntity entity = Assert.Single(PublishEntities(room, out _));
        Assert.Equal(preview, entity.EntityId);
        Assert.Equal(0u, entity.PhysicsBodyId);
        Assert.True(room.Submit(PlacePart(2, PlayerOne, PartBlock, 1.5f, 0.5f)).IsAccepted);
    }

    [Fact]
    public void DestroyedPartReleasesItsCellForReuse()
    {
        ScriptedWorld world = new();
        using GameRoom room = CreateSandboxRoom(world);
        room.SetupFromLevel(Level(new LevelSpawnDefinition(PartGround, new PhysicsVector3(0f, -0.5f, 0f))));
        uint egg = room.Submit(PlacePart(1, PlayerOne, PartEgg, 0.5f, 0.5f)).EntityId;
        Assert.True(room.Submit(Start(2, PlayerOne)).IsAccepted);
        List<SnapshotEntity> setup = PublishEntities(room, out _);
        uint eggBody = setup.Single(entity => entity.EntityId == egg).PhysicsBodyId;
        uint groundBody = setup.Single(entity => entity.EntityId != egg).PhysicsBodyId;

        world.QueueSnapshot(Snapshot(eggBody, new PhysicsVector3(0.5f, 0.5f, 0f), PhysicsVector3.Zero));
        room.Tick();

        // A hard landing destroys the egg; its owner drops back to editing.
        world.QueueSnapshot(Snapshot(eggBody, new PhysicsVector3(0.5f, 0.5f, 0f), new PhysicsVector3(10f, 0f, 0f)));
        world.QueueEvent(PhysicsEvent.ContactStarted(new PhysicsBodyId(eggBody), new PhysicsBodyId(groundBody)));
        room.Tick();

        Assert.DoesNotContain(PublishEntities(room, out _), entity => entity.EntityId == egg);

        // The destroyed part's cell was released, so the same spot is placeable again.
        Assert.True(room.Submit(PlacePart(3, PlayerOne, PartEgg, 0.5f, 0.5f)).IsAccepted);
    }

    [Fact]
    public void LegacyKeepModeReleasesDestroyedPartCells()
    {
        ScriptedWorld world = new();
        using GameRoom room = CreateLegacyRoom(world);
        room.SetupFromLevel(Level());
        uint egg = room.Submit(PlacePart(1, PlayerOne, PartEgg, 0.5f, 0.5f)).EntityId;
        uint block = room.Submit(PlacePart(2, PlayerOne, PartBlock, 5.5f, 0.5f)).EntityId;
        Assert.True(room.Submit(Start(3, PlayerOne)).IsAccepted);
        Dictionary<uint, uint> bodiesByEntity = PublishEntities(room, out _)
            .ToDictionary(entity => entity.EntityId, entity => entity.PhysicsBodyId);

        world.QueueSnapshot(Snapshot(bodiesByEntity[egg], new PhysicsVector3(0.5f, 0.5f, 0f), PhysicsVector3.Zero));
        world.QueueSnapshot(Snapshot(bodiesByEntity[block], new PhysicsVector3(5.5f, 0.5f, 0f), PhysicsVector3.Zero));
        room.Tick();

        world.QueueSnapshot(Snapshot(bodiesByEntity[egg], new PhysicsVector3(0.5f, 0.5f, 0f), PhysicsVector3.Zero));
        world.QueueSnapshot(Snapshot(bodiesByEntity[block], new PhysicsVector3(5.5f, 0.5f, 0f), new PhysicsVector3(10f, 0f, 0f)));
        world.QueueEvent(PhysicsEvent.ContactStarted(new PhysicsBodyId(bodiesByEntity[egg]), new PhysicsBodyId(bodiesByEntity[block])));
        room.Tick();

        Assert.Equal(1, room.BodyCount);
        Assert.Equal(
            CommandStatus.Accepted,
            room.Submit(new EnterBuildModeCommand(room.CurrentTick, 4, PlayerOne, BuildModePolicy.Keep)).Status);
        Assert.Equal(RoomMode.Building, room.Mode);

        // The destroyed egg no longer occupies its build cell, so Keep does not freeze a ghost there.
        Assert.True(room.Submit(PlacePart(5, PlayerOne, PartEgg, 0.5f, 0.5f)).IsAccepted);
    }

    [Fact]
    public void SandboxRoomKeepsPlayingWhenObjectivesAreDisabled()
    {
        ScriptedWorld world = new();
        using GameRoom room = CreateSandboxRoom(world);
        room.SetupFromLevel(Level(new LevelSpawnDefinition(
            PartPig,
            new PhysicsVector3(500f, 500f, 500f),
            LevelActorRole.Pig)));

        room.RunTicks(3);

        Assert.Equal(RoomMode.Running, room.Mode);
        Assert.Equal(GameplayPhase.Playing, room.Phase);
        Assert.Equal(3u, room.CurrentTick);
    }

    [Fact]
    public void EditingPlayerMayMoveAndScaleAndPreviewFollowsTheTransform()
    {
        ScriptedWorld world = new();
        using GameRoom room = CreateSandboxRoom(world);
        room.SetupFromLevel(Level());
        uint entityId = room.Submit(PlacePart(1, PlayerOne, PartBlock, 0.5f, 0.5f)).EntityId;
        uint blockerId = room.Submit(PlacePart(2, PlayerOne, PartBlock, 6.5f, 0.5f)).EntityId;

        CommandOutcome blocked = room.Submit(Move(3, PlayerOne, entityId, 6.5f, 0.5f));
        CommandOutcome moved = room.Submit(Move(4, PlayerOne, entityId, 4.5f, 3.5f));
        CommandOutcome scaled = room.Submit(Scale(5, PlayerOne, entityId, 2f));

        Assert.Equal(CommandStatus.RuleRejected, blocked.Status);
        Assert.Equal(ConstructionError.TransformBlocked, blocked.Error);
        Assert.Equal(0u, blocked.EntityId);
        Assert.True(moved.IsAccepted);
        Assert.Equal(entityId, moved.EntityId);
        Assert.True(scaled.IsAccepted);
        Assert.Equal(entityId, scaled.EntityId);

        Dictionary<uint, SnapshotEntity> previews = PublishEntities(room, out _).ToDictionary(entity => entity.EntityId);
        Assert.Equal(0u, previews[entityId].PhysicsBodyId);
        Assert.Equal(4.5f, previews[entityId].Position.X);
        Assert.Equal(3.5f, previews[entityId].Position.Y);
        Assert.Equal(2f, previews[entityId].Scale);
        Assert.Equal(6.5f, previews[blockerId].Position.X);
    }

    [Fact]
    public void LegacyBuildingRoomAcceptsMoveAndScaleWithOwnerZero()
    {
        ScriptedWorld world = new();
        using GameRoom room = CreateLegacyRoom(world);
        room.SetupFromLevel(Level());
        uint entityId = room.Submit(PlacePart(1, PlayerOne, PartBlock, 0.5f, 0.5f)).EntityId;

        // Legacy parts belong to owner 0, so any connection may transform them.
        CommandOutcome moved = room.Submit(Move(2, PlayerTwo, entityId, 4.5f, 3.5f));
        CommandOutcome scaled = room.Submit(Scale(3, PlayerTwo, entityId, 1.5f));

        Assert.True(moved.IsAccepted);
        Assert.Equal(entityId, moved.EntityId);
        Assert.True(scaled.IsAccepted);
        Assert.Equal(entityId, scaled.EntityId);

        SnapshotEntity preview = Assert.Single(PublishEntities(room, out _));
        Assert.Equal(4.5f, preview.Position.X);
        Assert.Equal(3.5f, preview.Position.Y);
        Assert.Equal(1.5f, preview.Scale);

        room.Start();
        Assert.Equal(CommandStatus.WrongMode, room.Submit(Move(4, PlayerOne, entityId, 5.5f, 0.5f)).Status);
        Assert.Equal(CommandStatus.WrongMode, room.Submit(Scale(5, PlayerOne, entityId, 2f)).Status);
    }

    [Fact]
    public void SwitchCommandsAreRejectedWhileEditing()
    {
        ScriptedWorld world = new();
        using GameRoom room = CreateSandboxRoom(world);
        room.SetupFromLevel(Level());
        uint entityId = room.Submit(PlacePart(1, PlayerOne, PartMotor, 0.5f, 0.5f)).EntityId;

        CommandOutcome outcome = room.Submit(SetActive(2, PlayerOne, entityId, active: true));

        Assert.Equal(CommandStatus.WrongMode, outcome.Status);
    }

    [Fact]
    public void MaterialisedPlayerSwitchesOwnPartAndSnapshotCarriesTheFlag()
    {
        ScriptedWorld world = new();
        using GameRoom room = CreateSandboxRoom(world);
        room.SetupFromLevel(Level());
        uint entityId = room.Submit(PlacePart(1, PlayerOne, PartMotor, 0.5f, 0.5f)).EntityId;
        Assert.True(room.Submit(Start(2, PlayerOne)).IsAccepted);

        Assert.Equal((byte)0, Assert.Single(PublishEntities(room, out _)).Flags);

        CommandOutcome outcome = room.Submit(SetActive(3, PlayerOne, entityId, active: true));

        Assert.True(outcome.IsAccepted);
        Assert.Equal(entityId, outcome.EntityId);
        Assert.Equal((byte)1, Assert.Single(PublishEntities(room, out _)).Flags);
    }

    [Fact]
    public void SwitchingAnotherPlayersPartIsRejected()
    {
        ScriptedWorld world = new();
        using GameRoom room = CreateSandboxRoom(world);
        room.SetupFromLevel(Level());
        uint entityId = room.Submit(PlacePart(1, PlayerOne, PartMotor, 0.5f, 0.5f)).EntityId;
        Assert.True(room.Submit(Start(2, PlayerOne)).IsAccepted);
        room.Submit(PlacePart(1, PlayerTwo, PartMotor, 6.5f, 0.5f));
        Assert.True(room.Submit(Start(2, PlayerTwo)).IsAccepted);

        CommandOutcome outcome = room.Submit(SetActive(3, PlayerTwo, entityId, active: true));

        Assert.Equal(CommandStatus.RuleRejected, outcome.Status);
        Assert.Equal(ConstructionError.NotOwnedByPlayer, outcome.Error);
    }

    [Fact]
    public void SwitchingANonSwitchablePartIsRejected()
    {
        ScriptedWorld world = new();
        using GameRoom room = CreateSandboxRoom(world);
        room.SetupFromLevel(Level());
        uint entityId = room.Submit(PlacePart(1, PlayerOne, PartBlock, 0.5f, 0.5f)).EntityId;
        Assert.True(room.Submit(Start(2, PlayerOne)).IsAccepted);

        CommandOutcome outcome = room.Submit(SetActive(3, PlayerOne, entityId, active: true));

        Assert.Equal(CommandStatus.RuleRejected, outcome.Status);
        Assert.Equal(ConstructionError.PartNotSwitchable, outcome.Error);
    }

    [Fact]
    public void SetPartTypeActiveTogglesOnlyTheOwnersParts()
    {
        ScriptedWorld world = new();
        using GameRoom room = CreateSandboxRoom(world);
        room.SetupFromLevel(Level());
        uint first = room.Submit(PlacePart(1, PlayerOne, PartMotor, 0.5f, 0.5f)).EntityId;
        uint second = room.Submit(PlacePart(2, PlayerOne, PartMotor, 3.5f, 0.5f)).EntityId;
        uint foreign = room.Submit(PlacePart(1, PlayerTwo, PartMotor, 6.5f, 0.5f)).EntityId;
        Assert.True(room.Submit(Start(3, PlayerOne)).IsAccepted);
        Assert.True(room.Submit(Start(2, PlayerTwo)).IsAccepted);

        CommandOutcome outcome = room.Submit(SetTypeActive(4, PlayerOne, PartMotor, active: true));

        Assert.True(outcome.IsAccepted);
        List<SnapshotEntity> entities = PublishEntities(room, out _);
        Assert.Equal((byte)1, entities.Single(entity => entity.EntityId == first).Flags);
        Assert.Equal((byte)1, entities.Single(entity => entity.EntityId == second).Flags);
        Assert.Equal((byte)0, entities.Single(entity => entity.EntityId == foreign).Flags);
    }

    [Fact]
    public void LegacyRoomRejectsSwitchCommands()
    {
        ScriptedWorld world = new();
        using GameRoom room = CreateLegacyRoom(world);
        room.SetupFromLevel(Level());
        uint entityId = room.Submit(PlacePart(1, PlayerOne, PartMotor, 0.5f, 0.5f)).EntityId;

        CommandOutcome building = room.Submit(SetActive(2, PlayerOne, entityId, active: true));
        Assert.Equal(CommandStatus.WrongMode, building.Status);

        room.Start();
        CommandOutcome running = room.Submit(SetActive(3, PlayerOne, entityId, active: true));
        Assert.Equal(CommandStatus.RuleRejected, running.Status);
        Assert.Equal(ConstructionError.PartNotSwitchable, running.Error);
    }

    [Fact]
    public void SandboxCartHingesWheelsAndDrives()
    {
        using GameRoom room = PlayHost.CreateSandboxRoom();
        uint player = PlayHost.NextPlayerId();
        uint sequence = 0;

        uint Place(uint partTypeId, float x, float y)
        {
            CommandOutcome outcome = room.Submit(PlayHost.BindPlayer(PlacePart(++sequence, 0, partTypeId, x, y), player));
            Assert.True(outcome.IsAccepted, $"place {partTypeId} at ({x},{y}): {outcome.Status}/{outcome.Error}");
            return outcome.EntityId;
        }

        // Real content: motor-wheel (17, sphere) + wooden block (1) + pig (4, sphere).
        uint rearWheel = Place(17, -8.5f, -2.5f);
        uint frontWheel = Place(17, -7.5f, -2.5f);
        uint block = Place(1, -8.0f, -1.5f);
        uint pig = Place(4, -8.0f, -0.5f);
        Assert.True(room.Submit(PlayHost.BindPlayer(Start(++sequence, 0), player)).IsAccepted);

        for (int tick = 0; tick < 180; tick++)
        {
            room.Tick();
        }

        List<SnapshotEntity> settled = PublishEntities(room, out _);
        uint frameBody = settled.Single(entity => entity.EntityId == block).PhysicsBodyId;
        Assert.NotEqual(0u, frameBody);
        Assert.Equal(frameBody, settled.Single(entity => entity.EntityId == pig).PhysicsBodyId);
        uint rearBody = settled.Single(entity => entity.EntityId == rearWheel).PhysicsBodyId;
        uint frontBody = settled.Single(entity => entity.EntityId == frontWheel).PhysicsBodyId;
        Assert.NotEqual(frameBody, rearBody);
        Assert.NotEqual(rearBody, frontBody);

        Assert.True(room.Submit(PlayHost.BindPlayer(SetTypeActive(++sequence, 0, 17, active: true), player)).IsAccepted);
        float before = settled.Single(entity => entity.EntityId == pig).Position.X;
        for (int tick = 0; tick < 30; tick++)
        {
            room.Tick();
        }

        List<SnapshotEntity> moved = PublishEntities(room, out _);
        float after = moved.Single(entity => entity.EntityId == pig).Position.X;
        Assert.True(after > before + 0.2f, $"the motor must drive the hinged cart: {before} -> {after}");
        Assert.True(
            moved.Where(entity => entity.EntityId == rearWheel || entity.EntityId == frontWheel)
                .Any(entity => MathF.Abs(entity.AngularVelocity.Z) > 0.5f),
            "the motor must spin the wheels instead of dragging them");
    }

    [Fact]
    public void SandboxWheelUnderFrameHingesToFrame()
    {
        using GameRoom room = PlayHost.CreateSandboxRoom();
        uint player = PlayHost.NextPlayerId();
        uint sequence = 0;

        uint Place(uint partTypeId, float x, float y)
        {
            CommandOutcome outcome = room.Submit(PlayHost.BindPlayer(PlacePart(++sequence, 0, partTypeId, x, y), player));
            Assert.True(outcome.IsAccepted, $"place {partTypeId} at ({x},{y}): {outcome.Status}/{outcome.Error}");
            return outcome.EntityId;
        }

        // The user-visible regression: a wooden frame directly above a wooden wheel only
        // connects through the wheel's support collider, and the wheel keeps its own body
        // hinged at the axle so it can roll.
        uint frame = Place(1, -9f, -0.5f);
        uint wheel = Place(7, -9f, -1.5f);
        Assert.True(room.Submit(PlayHost.BindPlayer(Start(++sequence, 0), player)).IsAccepted);

        for (int tick = 0; tick < 120; tick++)
        {
            room.Tick();
        }

        List<SnapshotEntity> entities = PublishEntities(room, out _);
        SnapshotEntity frameEntity = entities.Single(entity => entity.EntityId == frame);
        SnapshotEntity wheelEntity = entities.Single(entity => entity.EntityId == wheel);
        Assert.NotEqual(0u, frameEntity.PhysicsBodyId);
        Assert.NotEqual(frameEntity.PhysicsBodyId, wheelEntity.PhysicsBodyId);
        float dx = wheelEntity.Position.X - frameEntity.Position.X;
        float dy = wheelEntity.Position.Y - frameEntity.Position.Y;
        Assert.InRange(MathF.Sqrt((dx * dx) + (dy * dy)), 0.5f, 1.5f);
    }

    private static ResetScriptOutcome RunResetScript()
    {
        ScriptedWorld world = new();
        using GameRoom room = CreateSandboxRoom(world);
        room.SetupFromLevel(Level(new LevelSpawnDefinition(PartGround, new PhysicsVector3(0f, -0.5f, 0f))));
        Assert.True(room.Submit(PlacePart(1, PlayerOne, PartBlock, 0.5f, 0.5f)).IsAccepted);
        Assert.True(room.Submit(PlacePart(2, PlayerOne, PartBlock, 3.5f, 0.5f)).IsAccepted);
        uint playerTwoFirst = room.Submit(PlacePart(1, PlayerTwo, PartBlock, 6.5f, 0.5f)).EntityId;
        uint playerTwoSecond = room.Submit(PlacePart(2, PlayerTwo, PartBlock, 9.5f, 0.5f)).EntityId;
        Assert.True(room.Submit(Start(3, PlayerOne)).IsAccepted);
        Assert.True(room.Submit(Start(3, PlayerTwo)).IsAccepted);

        List<SnapshotEntity> before = PublishEntities(room, out _);
        long hashBefore = room.ComputeStateHash();
        SnapshotEntity playerTwoBefore = before.Single(entity => entity.EntityId == playerTwoFirst);

        Assert.True(room.Submit(Reset(4, PlayerOne)).IsAccepted);

        List<SnapshotEntity> after = PublishEntities(room, out _);
        long hashAfter = room.ComputeStateHash();
        SnapshotEntity playerTwoAfter = after.Single(entity => entity.EntityId == playerTwoFirst);

        return new ResetScriptOutcome(
            before.Select(entity => entity.EntityId).ToArray(),
            after.Select(entity => entity.EntityId).ToArray(),
            hashBefore,
            hashAfter,
            playerTwoBefore,
            playerTwoAfter);
    }

    private sealed record ResetScriptOutcome(
        uint[] EntityIdsBeforeReset,
        uint[] EntityIdsAfterReset,
        long StateHashBeforeReset,
        long StateHashAfterReset,
        SnapshotEntity PlayerTwoBeforeReset,
        SnapshotEntity PlayerTwoAfterReset);

    private static GameRoom CreateSandboxRoom(ScriptedWorld world, GameplayZone? mapBounds = null) =>
        new(GameRoomOptions.Create(
            new PartContentLibrary(PartContentParser.Parse(ContentJson)),
            () => world,
            Config(mapBounds ?? FarBounds),
            sandboxMode: true));

    private static GameRoom CreateLegacyRoom(ScriptedWorld world) =>
        new(GameRoomOptions.Create(
            new PartContentLibrary(PartContentParser.Parse(ContentJson)),
            () => world,
            Config(FarBounds, objectivesEnabled: true)));

    private static GameplayConfig Config(GameplayZone mapBounds, bool objectivesEnabled = false) => new(
        GoalZone: new GameplayZone(new PhysicsVector3(500f, 500f, 500f), new PhysicsVector3(501f, 501f, 501f)),
        MapBounds: mapBounds,
        TntBlastRadius: 4f,
        TntBlastImpulse: 25f,
        TntIgniteImpactSpeed: 5f,
        ObjectivesEnabled: objectivesEnabled);

    private static LevelContentDocument Level(params LevelSpawnDefinition[] spawns) => new(
        ContentVersion: "sandbox-test-v1",
        GoalZone: new GameplayZone(new PhysicsVector3(500f, 500f, 500f), new PhysicsVector3(501f, 501f, 501f)),
        MapBounds: FarBounds,
        Spawns: spawns);

    [Fact]
    public void SandboxWoodenCartRollsDownTheLongSlope()
    {
        // The user-visible regression: a block on two wooden wheels froze on the slope (the
        // wheels' mounted support boxes collided with their own tires on the parent body) and
        // could not roll. Real content and the terrain-v1 level's 14.3 degree long slope.
        using GameRoom room = PlayHost.CreateSandboxRoom();
        uint player = PlayHost.NextPlayerId();
        uint sequence = 0;
        const float angle = 0.25f;

        uint Place(uint partTypeId, float x, float y)
        {
            CommandOutcome outcome = room.Submit(PlayHost.BindPlayer(
                new PlacePartCommand(0, ++sequence, 0, partTypeId, x, y, angle, 1f), player));
            Assert.True(outcome.IsAccepted, $"place {partTypeId} at ({x},{y}): {outcome.Status}/{outcome.Error}");
            return outcome.EntityId;
        }

        uint rear = Place(7, -8.6428f, 1.4741f);
        uint front = Place(7, -7.6428f, 1.7295f);
        uint block = Place(1, -8.3878f, 2.5612f);
        Assert.True(room.Submit(PlayHost.BindPlayer(new StartSimulationCommand(0, ++sequence, 0), player)).IsAccepted);

        List<SnapshotEntity> start = PublishEntities(room, out _);
        float startX = start.Single(entity => entity.EntityId == rear).Position.X;

        for (int tick = 0; tick < 150; tick++)
        {
            room.Tick();
        }

        List<SnapshotEntity> rolled = PublishEntities(room, out _);
        SnapshotEntity rearWheel = rolled.Single(entity => entity.EntityId == rear);
        SnapshotEntity frontWheel = rolled.Single(entity => entity.EntityId == front);
        uint chassisBody = rolled.Single(entity => entity.EntityId == block).PhysicsBodyId;

        Assert.True(
            rearWheel.Position.X < startX - 3f,
            $"the cart must coast down the slope: {startX} -> {rearWheel.Position.X}");
        // The block is one body; each wheel keeps its own body and rolls on it.
        Assert.NotEqual(0u, chassisBody);
        Assert.NotEqual(chassisBody, rearWheel.PhysicsBodyId);
        Assert.NotEqual(chassisBody, frontWheel.PhysicsBodyId);
        Assert.NotEqual(rearWheel.PhysicsBodyId, frontWheel.PhysicsBodyId);

        // Free rolling, not skidding: each wheel spins at v / r about its axle (r = 0.33). A
        // cart moving in -x rolls with a positive spin (the contact point is stationary).
        foreach (SnapshotEntity wheel in new[] { rearWheel, frontWheel })
        {
            float speed = MathF.Sqrt(
                (wheel.LinearVelocity.X * wheel.LinearVelocity.X) + (wheel.LinearVelocity.Y * wheel.LinearVelocity.Y));
            float expected = speed / 0.33f;
            Assert.True(speed > 2f, $"the wheel must be moving: {speed}");
            Assert.InRange(wheel.AngularVelocity.Z, expected * 0.9f, expected * 1.1f);
        }
    }

    /// <summary>
    /// The user-visible regression: a wheel's axle (its non-spinning sprites) is welded to the
    /// chassis in the original, so it must rotate with the chassis. The snapshot carries that
    /// frame as `AttachYaw`; without it the client froze the axle at the wheel's build angle
    /// while the chassis pitched. The chassis is placed at the opposite angle on purpose, so
    /// the two are distinguishable from the very first frame.
    /// </summary>
    [Fact]
    public void SandboxWheelAttachFrameFollowsTheChassis()
    {
        using GameRoom room = PlayHost.CreateSandboxRoom();
        uint player = PlayHost.NextPlayerId();
        uint sequence = 0;
        uint Place(uint partTypeId, float x, float y, float angle)
        {
            CommandOutcome outcome = room.Submit(PlayHost.BindPlayer(
                new PlacePartCommand(0, ++sequence, 0, partTypeId, x, y, angle, 1f), player));
            Assert.True(outcome.IsAccepted, $"place {partTypeId} at ({x},{y}): {outcome.Status}/{outcome.Error}");
            return outcome.EntityId;
        }

        // A cart dropped tilted from above the flat middle slab: it lands on a corner and rotates
        // to a face, so the chassis turns while the wheel keeps its own build angle.
        uint rear = Place(7, 0f, 2.2f, 0.9f);
        uint block = Place(1, 0f, 3.2f, 0.9f);
        CommandOutcome startOutcome = room.Submit(PlayHost.BindPlayer(new StartSimulationCommand(0, ++sequence, 0), player));
        Assert.True(startOutcome.IsAccepted, $"start: {startOutcome.Status}/{startOutcome.Error}");

        // The mount starts rigid to the chassis: same frame the wheel was built at, because the
        // chassis has not rotated yet.
        List<SnapshotEntity> start = PublishEntities(room, out _);
        SnapshotEntity startWheel = start.Single(entity => entity.EntityId == rear);
        SnapshotEntity startChassis = start.Single(entity => entity.EntityId == block);
        float wheelBuildYaw = YawOf(startWheel.Rotation);
        float chassisBuildYaw = YawOf(startChassis.Rotation);
        Assert.Equal(0.9f, wheelBuildYaw, 2);
        Assert.Equal(0.9f, chassisBuildYaw, 2);
        Assert.Equal(wheelBuildYaw, startWheel.AttachYaw, 2);
        // A part that is not hinged is its own attach frame.
        Assert.Equal(chassisBuildYaw, startChassis.AttachYaw, 3);

        for (int tick = 0; tick < 150; tick++)
        {
            room.Tick();
        }

        List<SnapshotEntity> settled = PublishEntities(room, out _);
        SnapshotEntity wheel = settled.Single(entity => entity.EntityId == rear);
        SnapshotEntity chassis = settled.Single(entity => entity.EntityId == block);
        float chassisYaw = YawOf(chassis.Rotation);
        // The chassis must actually have rotated, or the contract below proves nothing.
        Assert.True(
            MathF.Abs(chassisYaw - chassisBuildYaw) > 0.1f,
            $"chassis {chassisBuildYaw} -> {chassisYaw} (body {chassis.PhysicsBodyId}) wheel {YawOf(wheel.Rotation):F3} attach {wheel.AttachYaw:F3} body {wheel.PhysicsBodyId} startBody {startChassis.PhysicsBodyId} blockPos {chassis.Position.X:F2},{chassis.Position.Y:F2}");
        // The axle turns by exactly the chassis' rotation since assembly...
        Assert.Equal(wheelBuildYaw + (chassisYaw - chassisBuildYaw), wheel.AttachYaw, 2);
        // ...while the body's own rotation carries the roll, leaving the attach frame behind.
        Assert.True(
            MathF.Abs(YawOf(wheel.Rotation) - wheel.AttachYaw) > 0.1f,
            $"the wheel must spin relative to its attach frame: {YawOf(wheel.Rotation)} vs {wheel.AttachYaw}");
    }

    private static float YawOf(ReplayQuaternion rotation) =>
        MathF.Atan2(
            2f * ((rotation.W * rotation.Z) + (rotation.X * rotation.Y)),
            1f - (2f * ((rotation.Y * rotation.Y) + (rotation.Z * rotation.Z))));

    private static PlacePartCommand PlacePart(uint sequence, uint playerId, uint partTypeId, float positionX, float positionY) =>
        new(
            Tick: 0,
            Sequence: sequence,
            PlayerId: playerId,
            PartTypeId: partTypeId,
            PositionX: positionX,
            PositionY: positionY,
            Angle: 0f,
            Scale: 1f);

    private static StartSimulationCommand Start(uint sequence, uint playerId) =>
        new(Tick: 0, Sequence: sequence, PlayerId: playerId);

    private static RetryCommand Reset(uint sequence, uint playerId) =>
        new(Tick: 0, Sequence: sequence, PlayerId: playerId);

    private static RotatePartCommand Rotate(uint sequence, uint playerId, uint entityId, float angle) =>
        new(Tick: 0, Sequence: sequence, PlayerId: playerId, EntityId: entityId, Angle: angle);

    private static MovePartCommand Move(uint sequence, uint playerId, uint entityId, float positionX, float positionY) =>
        new(Tick: 0, Sequence: sequence, PlayerId: playerId, EntityId: entityId, PositionX: positionX, PositionY: positionY);

    private static ScalePartCommand Scale(uint sequence, uint playerId, uint entityId, float scale) =>
        new(Tick: 0, Sequence: sequence, PlayerId: playerId, EntityId: entityId, Scale: scale);

    private static RemovePartCommand Remove(uint sequence, uint playerId, uint entityId) =>
        new(Tick: 0, Sequence: sequence, PlayerId: playerId, EntityId: entityId);

    private static SetPartActiveCommand SetActive(uint sequence, uint playerId, uint entityId, bool active) =>
        new(Tick: 0, Sequence: sequence, PlayerId: playerId, EntityId: entityId, Active: active);

    private static SetPartTypeActiveCommand SetTypeActive(uint sequence, uint playerId, uint partTypeId, bool active) =>
        new(Tick: 0, Sequence: sequence, PlayerId: playerId, PartTypeId: partTypeId, Active: active);

    private static PhysicsBodySnapshot Snapshot(uint bodyId, PhysicsVector3 position, PhysicsVector3 velocity) =>
        new(new PhysicsBodyId(bodyId), position, PhysicsQuaternion.Identity, velocity, PhysicsVector3.Zero);

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

    private const string ContentJson = """
    {
        "format": "pigforge.part-content",
        "schemaVersion": 1,
        "contentVersion": "sandbox-test-v1",
        "parts": [
            { "partTypeId": 1, "name": "block", "mode": "dynamic", "mass": 1, "shapes": [ { "kind": "box", "halfExtents": [0.5, 0.5, 0.5] } ] },
            { "partTypeId": 2, "name": "pig", "mode": "dynamic", "mass": 1, "material": { "restitution": 0.2, "friction": 0.4 }, "shapes": [ { "kind": "box", "halfExtents": [0.4, 0.4, 0.4] } ], "capabilities": { "pig": true } },
            { "partTypeId": 3, "name": "tnt", "mode": "dynamic", "mass": 1, "shapes": [ { "kind": "box", "halfExtents": [0.4, 0.4, 0.4] } ], "capabilities": { "tnt": { "fuseTicks": 1 } } },
            { "partTypeId": 4, "name": "egg", "mode": "dynamic", "mass": 1, "shapes": [ { "kind": "box", "halfExtents": [0.4, 0.4, 0.4] } ], "capabilities": { "egg": true } },
            { "partTypeId": 5, "name": "ground", "mode": "static", "mass": 0, "material": { "restitution": 0, "friction": 0.8 }, "shapes": [ { "kind": "box", "halfExtents": [40, 0.5, 10] } ] },
            { "partTypeId": 6, "name": "motor", "mode": "dynamic", "mass": 1, "shapes": [ { "kind": "box", "halfExtents": [0.4, 0.4, 0.4] } ], "capabilities": { "motor": { "thrustPerTick": 2, "directionX": 1 }, "activation": "toggle" } },
            { "partTypeId": 7, "name": "rocket", "mode": "dynamic", "mass": 1, "shapes": [ { "kind": "box", "halfExtents": [0.4, 0.4, 0.4] } ], "capabilities": { "rocket": { "thrustPerTick": 4, "directionX": 1, "durationTicks": 30 }, "activation": "trigger" } }
        ]
    }
    """;

    /// <summary>Scripted physics world: bodies are created deterministically, snapshots
    /// are queued by the test, and impulses aimed at destroyed bodies throw the same way
    /// the real backend does.</summary>
    private sealed class ScriptedWorld : IPhysicsWorld
    {
        private readonly List<PhysicsBodySnapshot> _snapshots = new();
        private readonly List<PhysicsEvent> _events = new();
        private readonly HashSet<uint> _liveBodies = new();
        private uint _nextBodyId;

        public List<PhysicsBodyId> DestroyedBodies { get; } = new();

        public PhysicsCapabilities Capabilities { get; } = new(
            new HashSet<PhysicsJointKind>(),
            SupportsContinuousCollision: false,
            SupportsPerBodyInertia: false);

        public void QueueSnapshot(PhysicsBodySnapshot snapshot)
        {
            int index = _snapshots.FindIndex(existing => existing.Body == snapshot.Body);
            if (index >= 0)
            {
                _snapshots[index] = snapshot;
            }
            else
            {
                _snapshots.Add(snapshot);
            }
        }

        public void QueueEvent(PhysicsEvent @event) => _events.Add(@event);

        public PhysicsBodyId CreateBody(BodyDefinition definition)
        {
            var id = new PhysicsBodyId(++_nextBodyId);
            _liveBodies.Add(id.Value);
            _snapshots.Add(new PhysicsBodySnapshot(id, definition.Position, definition.Rotation, definition.LinearVelocity, definition.AngularVelocity));
            return id;
        }

        public void DestroyBody(PhysicsBodyId body)
        {
            DestroyedBodies.Add(body);
            _liveBodies.Remove(body.Value);
            _snapshots.RemoveAll(snapshot => snapshot.Body == body);
        }

        public PhysicsJointId CreateJoint(JointDefinition definition) => throw new NotSupportedException();

        public void DestroyJoint(PhysicsJointId joint) => throw new NotSupportedException();

        public void ApplyCommands(ReadOnlySpan<PhysicsCommand> commands)
        {
            for (int index = 0; index < commands.Length; index++)
            {
                if (!_liveBodies.Contains(commands[index].Body.Value))
                {
                    throw new KeyNotFoundException($"Impulse target body {commands[index].Body.Value} does not exist.");
                }
            }
        }

        public void Step(FixedTimeStep timeStep)
        {
        }

        public int CopySnapshots(Span<PhysicsBodySnapshot> destination)
        {
            for (int index = 0; index < _snapshots.Count; index++)
            {
                destination[index] = _snapshots[index];
            }

            return _snapshots.Count;
        }

        public int DrainEvents(Span<PhysicsEvent> destination)
        {
            _events.CopyTo(destination);
            int count = _events.Count;
            _events.Clear();
            return count;
        }

        public void Dispose()
        {
        }
    }
}
