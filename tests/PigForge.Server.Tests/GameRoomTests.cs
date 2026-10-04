using PigForge.Core;
using PigForge.Core.Construction;
using PigForge.Core.Content;
using PigForge.Protocol;
using PigForge.Physics.Abstractions;
using PigForge.Physics.Bepu;
using PigForge.Server;

namespace PigForge.Server.Tests;

public sealed class GameRoomTests
{
    private const uint PartBlock = 1;
    private const uint PartPig = 2;
    private const uint PartTnt = 3;
    private const uint PartGround = 5;
    private const uint PlayerOne = 1;

    [Fact]
    public void RoomAdvancesTickPhasesInOrder()
    {
        ScriptedPhysicsWorld world = new();
        GameRoom room = CreateRoom(() => world);
        room.Spawn(new RoomSpawnSpec(PartPig, new PhysicsVector3(0f, 1f, 0f), RoomActorRole.Pig));
        room.Start();
        world.QueueSnapshot(new PhysicsBodySnapshot(new PhysicsBodyId(1), new PhysicsVector3(0f, 1f, 0f), PhysicsQuaternion.Identity, PhysicsVector3.Zero, PhysicsVector3.Zero));

        room.Tick();
        room.Tick();

        Assert.Equal(
            new[] { "apply:0", "step", "copy", "drain", "apply:0", "step", "copy", "drain" },
            world.OperationLog);
        Assert.Equal(2u, room.CurrentTick);
    }

    [Fact]
    public void TntSelfDestructionReleasesItsBodyInTheAuthoritativeScene()
    {
        ScriptedPhysicsWorld world = new();
        GameRoom room = CreateRoom(() => world);
        room.Spawn(new RoomSpawnSpec(PartTnt, new PhysicsVector3(0f, 1f, 0f), RoomActorRole.Tnt));
        room.Spawn(new RoomSpawnSpec(PartBlock, new PhysicsVector3(0f, 1f, 0f)));
        room.Start();
        // Impact velocity change ignites the charge; fuse 1; blast next tick.
        world.QueueSnapshot(new PhysicsBodySnapshot(new PhysicsBodyId(1), new PhysicsVector3(0f, 1f, 0f), PhysicsQuaternion.Identity, PhysicsVector3.Zero, PhysicsVector3.Zero));
        world.QueueSnapshot(new PhysicsBodySnapshot(new PhysicsBodyId(2), new PhysicsVector3(1f, 1f, 0f), PhysicsQuaternion.Identity, new PhysicsVector3(10f, 0f, 0f), PhysicsVector3.Zero));
        room.Tick();

        world.QueueSnapshot(new PhysicsBodySnapshot(new PhysicsBodyId(1), new PhysicsVector3(0f, 1f, 0f), PhysicsQuaternion.Identity, PhysicsVector3.Zero, PhysicsVector3.Zero));
        world.QueueSnapshot(new PhysicsBodySnapshot(new PhysicsBodyId(2), new PhysicsVector3(1f, 1f, 0f), PhysicsQuaternion.Identity, PhysicsVector3.Zero, PhysicsVector3.Zero));
        world.QueueEvent(PhysicsEvent.ContactStarted(new PhysicsBodyId(1), new PhysicsBodyId(2)));
        room.Tick();

        Assert.Empty(world.DestroyedBodies);

        // Ignition consumed one fuse tick; the blast (and self-destruction) follows next tick.
        room.Tick();

        Assert.Single(world.DestroyedBodies);
        Assert.Equal(1u, world.DestroyedBodies[0].Value);
        Assert.Equal(1, room.BodyCount);
        Assert.Equal(GameplayPhase.Playing, room.Phase);
    }

    [Fact]
    public void GluePartPreventsSeamSplitUnderAnOverThresholdImpulse()
    {
        ScriptedPhysicsWorld gluedWorld = new();
        CreateMotorRoom(() => gluedWorld, withGlue: true).RunTicks(2);
        Assert.Empty(gluedWorld.DestroyedBodies);

        ScriptedPhysicsWorld plainWorld = new();
        CreateMotorRoom(() => plainWorld, withGlue: false).RunTicks(2);
        Assert.NotEmpty(plainWorld.DestroyedBodies);
    }

    /// <summary>
    /// A seam split destroys the welded compound and rebuilds one body per piece: every member
    /// entity must be bound to a freshly created body. A dangling entity would publish the
    /// building-phase marker (physicsBodyId 0) and silently vanish from the running room.
    /// </summary>
    [Fact]
    public void SeamSplitRebindsEveryMemberToItsNewBody()
    {
        ScriptedPhysicsWorld world = new();
        GameRoom room = CreateMotorRoom(() => world, withGlue: false);
        room.RunTicks(2);
        Assert.NotEmpty(world.DestroyedBodies);

        List<SnapshotEntity> entities = PublishEntities(room);

        Assert.Equal(2, entities.Count);
        Assert.All(entities, entity => Assert.NotEqual(0u, entity.PhysicsBodyId));
        Assert.Equal(2, entities.Select(entity => entity.PhysicsBodyId).Distinct().Count());
    }

    [Fact]
    public void AStrongerWeldedPairSurvivesTheImpulseThatSplitsTheNormalPair()
    {
        // Same layout and the same 30 impulse: only the declared strengths differ, so the split
        // outcome proves the per-part threshold is live (plan P3 of the joint-strength batch).
        ScriptedPhysicsWorld normalWorld = new();
        CreateMotorRoom(() => normalWorld, withGlue: false).RunTicks(2);
        Assert.NotEmpty(normalWorld.DestroyedBodies);

        ScriptedPhysicsWorld strongWorld = new();
        CreateMotorRoom(() => strongWorld, withGlue: false, StrongMotorContentJson).RunTicks(2);
        Assert.Empty(strongWorld.DestroyedBodies);
    }

    [Fact]
    public void DisposeReleasesTheWorldAndRejectsFurtherUse()
    {
        ScriptedPhysicsWorld world = new();
        GameRoom room = CreateRoom(() => world);

        room.Dispose();
        room.Dispose();

        Assert.Equal(1, world.DisposeCount);
        Assert.Equal(RoomMode.Closed, room.Mode);
        Assert.Throws<ObjectDisposedException>(() => room.Tick());
        Assert.Throws<ObjectDisposedException>(() => room.ComputeStateHash());
        Assert.Throws<ObjectDisposedException>(() => room.Spawn(new RoomSpawnSpec(PartBlock, PhysicsVector3.Zero)));
    }

    [Fact]
    public void RoomLifecycleGatesSpawningAndTicking()
    {
        GameRoom room = CreateRoom(() => new ScriptedPhysicsWorld());

        Assert.Throws<InvalidOperationException>(() => room.Tick());

        room.Start();
        Assert.Equal(RoomMode.Running, room.Mode);
        Assert.Throws<InvalidOperationException>(() =>
            room.Spawn(new RoomSpawnSpec(PartBlock, PhysicsVector3.Zero)));
        Assert.Throws<InvalidOperationException>(() => room.Start());
    }

    [Fact]
    public void DuplicatePlaceCommandIsIdempotent()
    {
        GameRoom room = CreateRoom(() => new ScriptedPhysicsWorld());

        CommandOutcome first = room.Submit(Place(1, 0, 0));
        long hashAfterFirst = room.ComputeStateHash();
        CommandOutcome duplicate = room.Submit(Place(1, 0, 0));

        Assert.True(first.IsAccepted);
        Assert.Equal(CommandStatus.Duplicate, duplicate.Status);
        Assert.Equal(hashAfterFirst, room.ComputeStateHash());
    }

    [Fact]
    public void StaleSequencesAreRejectedWhileGapsAreAllowed()
    {
        GameRoom room = CreateRoom(() => new ScriptedPhysicsWorld());

        Assert.True(room.Submit(Place(2, 0, 0)).IsAccepted);
        Assert.Equal(CommandStatus.StaleSequence, room.Submit(Place(1, 5, 0)).Status);
        Assert.True(room.Submit(Place(5, 5, 0)).IsAccepted);
        Assert.Equal(CommandStatus.StaleSequence, room.Submit(Place(4, 9, 0)).Status);
        Assert.Equal(CommandStatus.Duplicate, room.Submit(Place(5, 9, 0)).Status);
    }

    [Fact]
    public void RuleRejectionsAreRecordedWithoutMutatingState()
    {
        GameRoom room = CreateRoom(() => new ScriptedPhysicsWorld());

        CommandOutcome first = room.Submit(Place(1, 0, 0));
        long hashAfterFirst = room.ComputeStateHash();
        CommandOutcome occupied = room.Submit(Place(2, 0, 0));
        CommandOutcome invalidAngle = room.Submit(new PlacePartCommand(
            Tick: 0, Sequence: 3, PlayerId: PlayerOne, PartTypeId: PartBlock,
            PositionX: float.NaN, PositionY: 0.5f, Angle: 0f, Scale: 1f));

        Assert.True(first.IsAccepted);
        Assert.Equal(CommandStatus.RuleRejected, occupied.Status);
        Assert.Equal(ConstructionError.CellsOccupied, occupied.Error);
        Assert.Equal(CommandStatus.RuleRejected, invalidAngle.Status);
        Assert.Equal(hashAfterFirst, room.ComputeStateHash());
    }

    [Fact]
    public void BuildCommandsAfterStartAreRejectedAndStartIsOneWay()
    {
        GameRoom room = CreateRoom(() => new ScriptedPhysicsWorld());
        room.Start();

        CommandOutcome buildAfterStart = room.Submit(Place(1, 0, 0));
        CommandOutcome secondStart = room.Submit(new StartSimulationCommand(Tick: 0, Sequence: 2, PlayerId: PlayerOne));

        Assert.Equal(CommandStatus.WrongMode, buildAfterStart.Status);
        Assert.Equal(CommandStatus.WrongMode, secondStart.Status);
        Assert.Throws<InvalidOperationException>(() => room.Start());
    }

    [Fact]
    public void EnterBuildModeIsGatedByModeAndTick()
    {
        GameRoom room = CreateRoom(() => new ScriptedPhysicsWorld());

        Assert.Equal(
            CommandStatus.WrongMode,
            room.Submit(new EnterBuildModeCommand(Tick: 0, Sequence: 1, PlayerId: PlayerOne, Policy: BuildModePolicy.Keep)).Status);

        room.Start();
        room.RunTicks(3);
        Assert.Equal(
            CommandStatus.StaleTick,
            room.Submit(new EnterBuildModeCommand(Tick: 2, Sequence: 2, PlayerId: PlayerOne, Policy: BuildModePolicy.Keep)).Status);

        CommandOutcome entered = room.Submit(new EnterBuildModeCommand(Tick: 3, Sequence: 3, PlayerId: PlayerOne, Policy: BuildModePolicy.Keep));
        Assert.True(entered.IsAccepted);
        Assert.Equal(RoomMode.Building, room.Mode);
        Assert.Equal(
            CommandStatus.Duplicate,
            room.Submit(new EnterBuildModeCommand(Tick: 3, Sequence: 3, PlayerId: PlayerOne, Policy: BuildModePolicy.Keep)).Status);

        // Build commands in a re-entered building phase still carry Tick 0.
        Assert.True(room.Submit(Place(4, 6, 6)).IsAccepted);
    }

    [Fact]
    public void KeepPolicyFreezesTheContraptionAndBlocksEdits()
    {
        ScriptedPhysicsWorld world = new();
        GameRoom room = CreateRoom(() => world);
        room.Spawn(new RoomSpawnSpec(PartPig, new PhysicsVector3(0f, 1f, 0f), RoomActorRole.Pig));
        room.Submit(Place(1, 3, 0));
        room.Start();
        // The contraption moved during the run; the world reports its resting pose.
        world.QueueSnapshot(new PhysicsBodySnapshot(new PhysicsBodyId(2), new PhysicsVector3(5f, 2f, 0f), PhysicsQuaternion.Identity, PhysicsVector3.Zero, PhysicsVector3.Zero));
        uint placedBlockEntity = (1u << 20) | 2; // second slot, first generation

        Assert.True(room.Submit(new EnterBuildModeCommand(Tick: 0, Sequence: 2, PlayerId: PlayerOne, Policy: BuildModePolicy.Keep)).IsAccepted);

        Assert.Equal(RoomMode.Building, room.Mode);
        Assert.Equal(0, room.BodyCount);
        Assert.Equal(new[] { new PhysicsBodyId(1), new PhysicsBodyId(2) }, world.DestroyedBodies);
        Assert.Equal(GameplayPhase.Playing, room.Phase);
        Assert.Equal(1, room.AlivePigs);

        // The frozen part is no longer editable...
        CommandOutcome removeFrozen = room.Submit(new RemovePartCommand(Tick: 0, Sequence: 3, PlayerId: PlayerOne, EntityId: placedBlockEntity));
        Assert.Equal(CommandStatus.RuleRejected, removeFrozen.Status);
        Assert.Equal(ConstructionError.FrozenEntity, removeFrozen.Error);
        // ...and a second vehicle can be built and relaunched alongside it.
        Assert.True(room.Submit(Place(4, 6, 6)).IsAccepted);
        room.Start();

        Assert.Equal(3, room.BodyCount);
        PhysicsBodySnapshot frozenRelaunch = Assert.Single(world.Snapshots, snapshot => snapshot.Position == new PhysicsVector3(5f, 2f, 0f));
        Assert.Equal(new PhysicsBodyId(2), frozenRelaunch.Body);
    }

    [Fact]
    public void ClearPolicyRestoresTheLevelSpawns()
    {
        ScriptedPhysicsWorld world = new();
        GameRoom room = CreateRoom(() => world);
        room.SetupFromLevel(new LevelContentDocument(
            ContentVersion: "clear-test-v1",
            GoalZone: new GameplayZone(new PhysicsVector3(500f, 500f, 500f), new PhysicsVector3(501f, 501f, 501f)),
            MapBounds: new GameplayZone(new PhysicsVector3(-1000f, -1000f, -1000f), new PhysicsVector3(1000f, 1000f, 1000f)),
            Spawns: new[]
            {
                new LevelSpawnDefinition(PartGround, new PhysicsVector3(0f, -0.5f, 0f)),
                new LevelSpawnDefinition(PartPig, new PhysicsVector3(7.5f, 1f, 0f), LevelActorRole.Pig)
            }));
        room.Submit(Place(1, 2, 2));
        room.Start();
        room.RunTicks(2);

        Assert.True(room.Submit(new EnterBuildModeCommand(Tick: 2, Sequence: 2, PlayerId: PlayerOne, Policy: BuildModePolicy.Clear)).IsAccepted);

        Assert.Equal(RoomMode.Building, room.Mode);
        Assert.Equal(0, room.BodyCount);
        Assert.Equal(1, room.AlivePigs);

        // Player construction was wiped; the same cells are buildable again.
        Assert.True(room.Submit(Place(3, 2, 2)).IsAccepted);
        room.Start();
        Assert.Equal(3, room.BodyCount);
    }

    [Fact]
    public void BuildModeLifecycleDoubleRunIsDeterministic()
    {
        Assert.Equal(RunLifecycleRoom(BuildModePolicy.Keep), RunLifecycleRoom(BuildModePolicy.Keep));
        Assert.Equal(RunLifecycleRoom(BuildModePolicy.Clear), RunLifecycleRoom(BuildModePolicy.Clear));
        Assert.NotEqual(
            RunLifecycleRoom(BuildModePolicy.Keep).FinalBodies,
            RunLifecycleRoom(BuildModePolicy.Clear).FinalBodies);
    }

    private static (long TransitionHash, long FinalHash, int FinalBodies) RunLifecycleRoom(BuildModePolicy policy)
    {
        using GameRoom room = CreateLifecycleRoom();
        room.RunTicks(90);
        room.Submit(new EnterBuildModeCommand(Tick: 90, Sequence: 10, PlayerId: PlayerOne, Policy: policy));
        long transitionHash = room.ComputeStateHash();
        room.Submit(Place(11, 40, 0));
        room.Submit(new StartSimulationCommand(Tick: 0, Sequence: 12, PlayerId: PlayerOne));
        room.RunTicks(90);
        return (transitionHash, room.ComputeStateHash(), room.BodyCount);
    }

    private static GameRoom CreateLifecycleRoom()
    {
        PartContentLibrary content = new(PartContentParser.Parse(LevelContentJson));
        GameRoom room = new(GameRoomOptions.Create(
            content,
            () => new BepuPhysicsWorld(new PhysicsVector3(0f, -9.81f, 0f)),
            new GameplayConfig(
                GoalZone: new GameplayZone(new PhysicsVector3(-9f, 0f, -2f), new PhysicsVector3(-7f, 4f, 2f)),
                MapBounds: new GameplayZone(new PhysicsVector3(-100f, -5f, -20f), new PhysicsVector3(100f, 60f, 20f)),
                TntBlastRadius: 4f,
                TntBlastImpulse: 25f,
                TntIgniteImpactSpeed: 5f)));
        room.Spawn(new RoomSpawnSpec(PartGround, new PhysicsVector3(0f, -0.5f, 0f)));
        room.Spawn(new RoomSpawnSpec(PartPig, new PhysicsVector3(7.5f, 1f, 0f), RoomActorRole.Pig));
        room.Spawn(new RoomSpawnSpec(PartTnt, new PhysicsVector3(8.9f, 1f, 0f), RoomActorRole.Tnt));
        room.Submit(Place(1, 20, 0));
        room.Submit(Place(2, 21, 0));
        room.Start();
        return room;
    }

    [Fact]
    public void MaliciousCommandScriptIsDeterministic()
    {
        (long hash, CommandStatus[] statuses) first = RunMaliciousScript();
        (long hash, CommandStatus[] statuses) second = RunMaliciousScript();

        Assert.Equal(first.hash, second.hash);
        Assert.Equal(first.statuses, second.statuses);
        Assert.Contains(CommandStatus.RuleRejected, first.statuses);
        Assert.Contains(CommandStatus.Duplicate, first.statuses);
        Assert.Contains(CommandStatus.StaleSequence, first.statuses);
        Assert.Contains(CommandStatus.Accepted, first.statuses);
    }

    [Fact]
    public void PlacementAngleAndScaleEnterTheBuildingStateHash()
    {
        static long HashWith(float angle, float scale)
        {
            using GameRoom room = CreateRoom(() => new ScriptedPhysicsWorld());
            room.Submit(new PlacePartCommand(Tick: 0, Sequence: 1, PlayerId: PlayerOne, PartTypeId: PartBlock, PositionX: 1f, PositionY: 0.5f, Angle: angle, Scale: scale));
            return room.ComputeStateHash();
        }

        Assert.NotEqual(HashWith(0f, 1f), HashWith(0.3f, 1f));
        Assert.NotEqual(HashWith(0f, 1f), HashWith(0f, 2f));
    }

    [Fact]
    public void ConnectedPartsShareOnePhysicsBodyAtStart()
    {
        ScriptedPhysicsWorld world = new();
        using GameRoom room = CreateRoom(() => world);
        room.Submit(new PlacePartCommand(Tick: 0, Sequence: 1, PlayerId: PlayerOne, PartTypeId: PartBlock, PositionX: 0f, PositionY: 0f, Angle: 0f, Scale: 1f));
        room.Submit(new PlacePartCommand(Tick: 0, Sequence: 2, PlayerId: PlayerOne, PartTypeId: PartBlock, PositionX: 1.1f, PositionY: 0f, Angle: 0f, Scale: 1f));
        room.Start();

        Assert.Equal(2, room.BodyCount);
        Assert.Single(world.Snapshots);
    }

    private static (long Hash, CommandStatus[] Statuses) RunMaliciousScript()
    {
        GameRoom room = CreateRoom(() => new ScriptedPhysicsWorld());
        room.Spawn(new RoomSpawnSpec(PartPig, new PhysicsVector3(50f, 1f, 0f), RoomActorRole.Pig));

        uint sequence = 0;
        room.Submit(Place(++sequence, 0, 0));
        room.Submit(Place(sequence, 0, 0));
        room.Submit(Place(++sequence, 0, 0));
        room.Submit(Place(1, 9, 9, rotation: 4));
        room.Submit(Place(++sequence, 1, 0));
        room.Submit(Place(2, 9, 9));
        room.Submit(new StartSimulationCommand(Tick: 0, Sequence: ++sequence, PlayerId: PlayerOne));
        room.Submit(Place(++sequence, 5, 5));

        room.RunTicks(30);
        return (room.ComputeStateHash(), room.OutcomeLog.Select(outcome => outcome.Status).ToArray());
    }

    [Fact]
    public void IdenticalRoomsProduceIdenticalHashesWithRealPhysics()
    {
        long first = RunPhysicsRoom();
        long second = RunPhysicsRoom();

        Assert.Equal(first, second);
        Assert.NotEqual(0, first);
    }

    [Fact]
    public void PhysicsRoomDeliversPigIntoGoalZone()
    {
        using GameRoom room = CreatePhysicsRoom();
        for (uint tick = 0; tick < 240 && room.Phase == GameplayPhase.Playing; tick++)
        {
            room.Tick();
        }

        Assert.Equal(GameplayPhase.Won, room.Phase);
        Assert.False(room.RestartRequested);
        Assert.Equal(3, room.BodyCount);
    }

    private static long RunPhysicsRoom()
    {
        using GameRoom room = CreatePhysicsRoom();
        room.RunTicks(240);
        return room.ComputeStateHash();
    }

    private static GameRoom CreatePhysicsRoom()
    {
        PartContentLibrary content = new(PartContentParser.Parse(LevelContentJson));
        GameRoomOptions options = GameRoomOptions.Create(
            content,
            () => new BepuPhysicsWorld(new PhysicsVector3(0f, -9.81f, 0f)),
            new GameplayConfig(
                GoalZone: new GameplayZone(new PhysicsVector3(-9f, 0f, -2f), new PhysicsVector3(-7f, 4f, 2f)),
                MapBounds: new GameplayZone(new PhysicsVector3(-100f, -5f, -20f), new PhysicsVector3(100f, 60f, 20f)),
                TntBlastRadius: 4f,
                TntBlastImpulse: 25f,
                TntIgniteImpactSpeed: 5f));
        GameRoom room = new(options);
        room.Spawn(new RoomSpawnSpec(PartGround, new PhysicsVector3(0f, -0.5f, 0f)));
        room.Spawn(new RoomSpawnSpec(PartPig, new PhysicsVector3(7.5f, 1f, 0f), RoomActorRole.Pig));
        room.Spawn(new RoomSpawnSpec(PartTnt, new PhysicsVector3(8.9f, 1f, 0f), RoomActorRole.Tnt));
        room.Spawn(new RoomSpawnSpec(PartBlock, new PhysicsVector3(8.9f, 8f, 0f)));
        room.Start();
        return room;
    }

    private static GameRoom CreateRoom(Func<IPhysicsWorld> factory)
    {
        PartContentLibrary content = new(PartContentParser.Parse(LevelContentJson));
        return new GameRoom(GameRoomOptions.Create(
            content,
            factory,
            new GameplayConfig(
                GoalZone: new GameplayZone(new PhysicsVector3(500f, 500f, 500f), new PhysicsVector3(501f, 501f, 501f)),
                MapBounds: new GameplayZone(new PhysicsVector3(-1000f, -1000f, -1000f), new PhysicsVector3(1000f, 1000f, 1000f)),
                TntBlastRadius: 4f,
                TntBlastImpulse: 12f,
                TntIgniteImpactSpeed: 5f)));
    }

    // v1 tests author placements in grid cells; the free-pose command takes metres,
    // so a 1×1 cell maps to its centre (gx + 0.5, gy + 0.5).
    private static PlacePartCommand Place(uint sequence, int gridX, int gridY, byte rotation = 0) =>
        new PlacePartCommand(
            Tick: 0,
            Sequence: sequence,
            PlayerId: PlayerOne,
            PartTypeId: PartBlock,
            PositionX: gridX + 0.5f,
            PositionY: gridY + 0.5f,
            Angle: rotation * (MathF.PI / 2f),
            Scale: 1f);

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

    /// <summary>Motor + block (+ glue) placed adjacent so they weld into one compound; the
    /// motor's 30 impulse exceeds the default 10 seam threshold from the first tick.</summary>
    private static GameRoom CreateMotorRoom(Func<IPhysicsWorld> factory, bool withGlue, string contentJson = MotorContentJson)
    {
        PartContentLibrary content = new(PartContentParser.Parse(contentJson));
        GameRoom room = new(GameRoomOptions.Create(
            content,
            factory,
            new GameplayConfig(
                GoalZone: new GameplayZone(new PhysicsVector3(500f, 500f, 500f), new PhysicsVector3(501f, 501f, 501f)),
                MapBounds: new GameplayZone(new PhysicsVector3(-1000f, -1000f, -1000f), new PhysicsVector3(1000f, 1000f, 1000f)),
                TntBlastRadius: 4f,
                TntBlastImpulse: 12f,
                TntIgniteImpactSpeed: 5f)));
        room.Submit(PlacePart(1, partTypeId: 2, gridX: 0, gridY: 0));
        room.Submit(PlacePart(2, partTypeId: 1, gridX: 1, gridY: 0));
        if (withGlue)
        {
            room.Submit(PlacePart(3, partTypeId: 3, gridX: 2, gridY: 0));
        }

        room.Start();
        return room;
    }

    private static PlacePartCommand PlacePart(uint sequence, uint partTypeId, int gridX, int gridY) =>
        new(
            Tick: 0,
            Sequence: sequence,
            PlayerId: PlayerOne,
            PartTypeId: partTypeId,
            PositionX: gridX + 0.5f,
            PositionY: gridY + 0.5f,
            Angle: 0f,
            Scale: 1f);

    private const string MotorContentJson = """
    {
        "format": "pigforge.part-content",
        "schemaVersion": 1,
        "contentVersion": "server-glue-test-v1",
        "physics": { "maximumAngularSpeed": 7.0, "damping": { "linear": 0.2, "angular": 0.05 } },
        "parts": [
            { "partTypeId": 1, "name": "block", "mode": "dynamic", "mass": 1, "capabilities": { "jointConnectionType": "source" }, "shapes": [ { "kind": "box", "halfExtents": [0.5, 0.5, 0.5] } ] },
            { "partTypeId": 2, "name": "motor", "mode": "dynamic", "mass": 1, "capabilities": { "jointConnectionType": "target", "motor": { "thrustPerTick": 30, "directionX": 1 } }, "shapes": [ { "kind": "box", "halfExtents": [0.5, 0.5, 0.5] } ] },
            { "partTypeId": 3, "name": "glue", "mode": "dynamic", "mass": 1, "capabilities": { "jointConnectionType": "target", "glue": true }, "shapes": [ { "kind": "box", "halfExtents": [0.5, 0.5, 0.5] } ] }
        ]
    }
    """;

    /// <summary>The motor room's two welded parts declared HighlyExtreme: the pair's seam
    /// threshold rises from 10 to 48, so the motor's 30 impulse no longer splits it.</summary>
    private const string StrongMotorContentJson = """
    {
        "format": "pigforge.part-content",
        "schemaVersion": 1,
        "contentVersion": "server-strength-test-v1",
        "physics": { "maximumAngularSpeed": 7.0, "damping": { "linear": 0.2, "angular": 0.05 } },
        "parts": [
            { "partTypeId": 1, "name": "block", "mode": "dynamic", "mass": 1, "capabilities": { "jointConnectionType": "source", "jointConnectionStrength": "highlyExtreme" }, "shapes": [ { "kind": "box", "halfExtents": [0.5, 0.5, 0.5] } ] },
            { "partTypeId": 2, "name": "motor", "mode": "dynamic", "mass": 1, "capabilities": { "jointConnectionType": "target", "jointConnectionStrength": "highlyExtreme", "motor": { "thrustPerTick": 30, "directionX": 1 } }, "shapes": [ { "kind": "box", "halfExtents": [0.5, 0.5, 0.5] } ] }
        ]
    }
    """;

    private const string LevelContentJson = """
    {
        "format": "pigforge.part-content",
        "schemaVersion": 1,
        "contentVersion": "server-test-v1",
        "physics": { "maximumAngularSpeed": 7.0, "damping": { "linear": 0.2, "angular": 0.05 } },
        "parts": [
            { "partTypeId": 1, "name": "block", "mode": "dynamic", "mass": 1, "capabilities": { "jointConnectionType": "source" }, "shapes": [ { "kind": "box", "halfExtents": [0.5, 0.5, 0.5] } ] },
            { "partTypeId": 2, "name": "pig", "mode": "dynamic", "mass": 1, "material": { "restitution": 0.2, "friction": 0.4 }, "shapes": [ { "kind": "box", "halfExtents": [0.4, 0.4, 0.4] } ] },
            { "partTypeId": 3, "name": "tnt", "mode": "dynamic", "mass": 1, "shapes": [ { "kind": "box", "halfExtents": [0.4, 0.4, 0.4] } ] },
            { "partTypeId": 5, "name": "ground", "mode": "static", "mass": 0, "material": { "restitution": 0, "friction": 0.8 }, "shapes": [ { "kind": "box", "halfExtents": [40, 0.5, 10] } ] }
        ]
    }
    """;

    /// <summary>
    /// A scripted IPhysicsWorld that records phase operations so the room's ordering
    /// contract can be asserted without a physics backend.
    /// </summary>
    private sealed class ScriptedPhysicsWorld : IPhysicsWorld
    {
        private readonly List<PhysicsBodySnapshot> _snapshots = new();
        private readonly List<PhysicsEvent> _events = new();
        private readonly List<string> _log = new();

        public List<string> OperationLog => _log;

        public List<PhysicsBodyId> DestroyedBodies { get; } = new();

        public IReadOnlyList<PhysicsBodySnapshot> Snapshots => _snapshots;

        public int DisposeCount { get; private set; }

        public PhysicsCapabilities Capabilities { get; } = new(
            new HashSet<PhysicsJointKind>(),
            SupportsContinuousCollision: false,
            SupportsPerBodyInertia: false,
            AppliesRestitutionNatively: false);

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
            var id = new PhysicsBodyId((uint)(_snapshots.Count + 1));
            _snapshots.Add(new PhysicsBodySnapshot(id, definition.Position, definition.Rotation, definition.LinearVelocity, definition.AngularVelocity));
            return id;
        }

        public void DestroyBody(PhysicsBodyId body)
        {
            DestroyedBodies.Add(body);
            _snapshots.RemoveAll(snapshot => snapshot.Body == body);
        }

        public void SetBodyMass(PhysicsBodyId body, float mass)
        {
        }

        public void SetBodyCollisionEnabled(PhysicsBodyId body, bool enabled)
        {
        }

        public PhysicsJointId CreateJoint(JointDefinition definition) => throw new NotSupportedException();

        public void DestroyJoint(PhysicsJointId joint) => throw new NotSupportedException();

        public void ApplyCommands(ReadOnlySpan<PhysicsCommand> commands) => _log.Add($"apply:{commands.Length}");

        public void Step(FixedTimeStep timeStep) => _log.Add("step");

        public int CopySnapshots(Span<PhysicsBodySnapshot> destination)
        {
            for (int index = 0; index < _snapshots.Count; index++)
            {
                destination[index] = _snapshots[index];
            }

            _log.Add("copy");
            return _snapshots.Count;
        }

        public int DrainEvents(Span<PhysicsEvent> destination)
        {
            _events.CopyTo(destination);
            int count = _events.Count;
            _events.Clear();
            _log.Add("drain");
            return count;
        }

        public void Dispose() => DisposeCount++;
    }
}
