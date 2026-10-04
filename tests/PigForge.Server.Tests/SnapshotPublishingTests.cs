using PigForge.Core;
using PigForge.Core.Content;
using PigForge.Protocol;
using PigForge.Physics.Abstractions;
using PigForge.Physics.Bepu;
using PigForge.Server;

namespace PigForge.Server.Tests;

public sealed class SnapshotPublishingTests
{
    private const uint PartBlock = 1;
    private const uint PartPig = 2;
    private const uint PartTnt = 3;
    private const uint PartGround = 5;

    [Fact]
    public void PublishedFrameCarriesAuthoritativeStateForLocalConsumer()
    {
        using GameRoom room = CreateRunningRoom();
        byte[] buffer = new byte[SnapshotFrame.GetMaxByteCount(16)];

        Assert.True(room.TryPublishSnapshot(buffer, out int bytesWritten));
        Assert.True(SnapshotFrame.TryDecodeHeader(buffer.AsSpan(0, bytesWritten), out SnapshotFrameHeader header, out SnapshotFrameReader reader));

        Assert.Equal(0u, room.CurrentTick);
        Assert.Equal((uint)room.BodyCount, header.EntityCount);
        Assert.Equal((byte)GameplayPhase.Playing, header.Phase);

        Dictionary<uint, SnapshotEntity> decoded = new();
        while (reader.TryReadEntity(out SnapshotEntity entity))
        {
            decoded.Add(entity.EntityId, entity);
        }

        Assert.Equal(4, decoded.Count);
        // Ground entity keeps its authored transform; part type ids survive the wire.
        Assert.Equal(PartGround, decoded.Values.Single(entity => entity.Position.Y < 0f).PartTypeId);
    }

    [Fact]
    public void PublishedFramesTrackTickAndDestroyedEntities()
    {
        ScriptedWorld world = new();
        PartContentLibrary content = new(PartContentParser.Parse(LevelContentJson));
        using GameRoom room = new GameRoom(GameRoomOptions.Create(content, () => world, FarZonesConfig()));
        room.Spawn(new RoomSpawnSpec(PartTnt, new PhysicsVector3(0f, 1f, 0f), RoomActorRole.Tnt));
        room.Spawn(new RoomSpawnSpec(PartBlock, new PhysicsVector3(1f, 1f, 0f)));
        room.Start();
        world.QueueSnapshot(new PhysicsBodySnapshot(new PhysicsBodyId(1), new PhysicsVector3(0f, 1f, 0f), PhysicsQuaternion.Identity, PhysicsVector3.Zero, PhysicsVector3.Zero));
        world.QueueSnapshot(new PhysicsBodySnapshot(new PhysicsBodyId(2), new PhysicsVector3(1f, 1f, 0f), PhysicsQuaternion.Identity, new PhysicsVector3(10f, 0f, 0f), PhysicsVector3.Zero));
        room.Tick();

        world.QueueSnapshot(new PhysicsBodySnapshot(new PhysicsBodyId(1), new PhysicsVector3(0f, 1f, 0f), PhysicsQuaternion.Identity, PhysicsVector3.Zero, PhysicsVector3.Zero));
        world.QueueSnapshot(new PhysicsBodySnapshot(new PhysicsBodyId(2), new PhysicsVector3(1f, 1f, 0f), PhysicsQuaternion.Identity, PhysicsVector3.Zero, PhysicsVector3.Zero));
        world.QueueEvent(PhysicsEvent.ContactStarted(new PhysicsBodyId(1), new PhysicsBodyId(2)));
        room.Tick();
        room.Tick();

        byte[] buffer = new byte[SnapshotFrame.GetMaxByteCount(16)];
        Assert.True(room.TryPublishSnapshot(buffer, out int bytesWritten));
        Assert.True(SnapshotFrame.TryDecodeHeader(buffer.AsSpan(0, bytesWritten), out SnapshotFrameHeader header, out SnapshotFrameReader reader));

        Assert.Equal(3u, header.Tick);
        Assert.Equal(1u, header.EntityCount);
        Assert.True(reader.TryReadEntity(out SnapshotEntity survivor));
        Assert.NotEqual(1u, survivor.EntityId);
    }

    [Fact]
    public void IdenticalRoomsPublishIdenticalBytes()
    {
        byte[] first = PublishTypicalFrames();
        byte[] second = PublishTypicalFrames();

        Assert.Equal(first, second);
        Assert.NotEmpty(first);
    }

    [Fact]
    public void PublishingIsAllocationFreePerFrame()
    {
        using GameRoom room = CreateRunningRoom();
        byte[] buffer = new byte[SnapshotFrame.GetMaxByteCount(16)];

        for (int index = 0; index < 8; index++)
        {
            room.TryPublishSnapshot(buffer, out _);
        }

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int index = 0; index < 1_000; index++)
        {
            room.TryPublishSnapshot(buffer, out _);
        }

        long after = GC.GetAllocatedBytesForCurrentThread();
        Assert.Equal(0, after - before);
    }

    private static byte[] PublishTypicalFrames()
    {
        using GameRoom room = CreateRunningRoom();
        byte[] buffer = new byte[SnapshotFrame.GetMaxByteCount(16)];
        using MemoryStream stream = new();
        for (uint tick = 0; tick < 30; tick++)
        {
            if (tick > 0)
            {
                room.Tick();
            }

            Assert.True(room.TryPublishSnapshot(buffer, out int bytesWritten));
            stream.Write(buffer, 0, bytesWritten);
        }

        return stream.ToArray();
    }

    private static GameRoom CreateRunningRoom()
    {
        PartContentLibrary content = new(PartContentParser.Parse(LevelContentJson));
        GameRoomOptions options = GameRoomOptions.Create(content, () => new BepuPhysicsWorld(new PhysicsVector3(0f, -9.81f, 0f)), FarZonesConfig());
        GameRoom room = new(options);
        room.Spawn(new RoomSpawnSpec(PartGround, new PhysicsVector3(0f, -0.5f, 0f)));
        room.Spawn(new RoomSpawnSpec(PartPig, new PhysicsVector3(8f, 1f, 0f), RoomActorRole.Pig));
        room.Spawn(new RoomSpawnSpec(PartTnt, new PhysicsVector3(8.9f, 1f, 0f), RoomActorRole.Tnt));
        room.Spawn(new RoomSpawnSpec(PartBlock, new PhysicsVector3(8.9f, 8f, 0f)));
        room.Start();
        return room;
    }

    private static GameplayConfig FarZonesConfig() => new(
        GoalZone: new GameplayZone(new PhysicsVector3(500f, 500f, 500f), new PhysicsVector3(501f, 501f, 501f)),
        MapBounds: new GameplayZone(new PhysicsVector3(-1000f, -1000f, -1000f), new PhysicsVector3(1000f, 1000f, 1000f)),
        TntBlastRadius: 4f,
        TntBlastImpulse: 25f,
        TntIgniteImpactSpeed: 5f);

    private const string LevelContentJson = """
    {
        "format": "pigforge.part-content",
        "schemaVersion": 1,
        "contentVersion": "snapshot-test-v1",
        "physics": { "maximumAngularSpeed": 7.0, "damping": { "linear": 0.2, "angular": 0.05 } },
        "parts": [
            { "partTypeId": 1, "name": "block", "mode": "dynamic", "mass": 1, "shapes": [ { "kind": "box", "halfExtents": [0.5, 0.5, 0.5] } ] },
            { "partTypeId": 2, "name": "pig", "mode": "dynamic", "mass": 1, "shapes": [ { "kind": "box", "halfExtents": [0.4, 0.4, 0.4] } ] },
            { "partTypeId": 3, "name": "tnt", "mode": "dynamic", "mass": 1, "shapes": [ { "kind": "box", "halfExtents": [0.4, 0.4, 0.4] } ] },
            { "partTypeId": 5, "name": "ground", "mode": "static", "mass": 0, "shapes": [ { "kind": "box", "halfExtents": [40, 0.5, 10] } ] }
        ]
    }
    """;

    private sealed class ScriptedWorld : IPhysicsWorld
    {
        private readonly List<PhysicsBodySnapshot> _snapshots = new();
        private readonly List<PhysicsEvent> _events = new();

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

        public void DestroyBody(PhysicsBodyId body) => _snapshots.RemoveAll(snapshot => snapshot.Body == body);

        public void SetBodyMass(PhysicsBodyId body, float mass)
        {
        }

        public void SetBodyCollisionEnabled(PhysicsBodyId body, bool enabled)
        {
        }

        public PhysicsJointId CreateJoint(JointDefinition definition) => throw new NotSupportedException();

        public void DestroyJoint(PhysicsJointId joint) => throw new NotSupportedException();

        public void ApplyCommands(ReadOnlySpan<PhysicsCommand> commands)
        {
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
