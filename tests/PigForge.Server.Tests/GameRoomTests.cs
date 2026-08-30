using PigForge.Core;
using PigForge.Core.Content;
using PigForge.Physics.Abstractions;
using PigForge.Physics.Bepu;
using PigForge.Server;

namespace PigForge.Server.Tests;

public sealed class GameRoomTests
{
    private const uint PartBlock = 1;
    private const uint PartPig = 2;
    private const uint PartTnt = 3;
    private const uint PartWheel = 4;
    private const uint PartGround = 5;

    [Fact]
    public void RoomAdvancesTickPhasesInOrder()
    {
        ScriptedPhysicsWorld world = new();
        GameRoom room = CreateRoom(() => world);
        room.Spawn(new RoomSpawnSpec(PartWheel, new PhysicsVector3(2f, 1f, 0f), MotorImpulsePerTick: 2f, MotorDirectionX: 1f, IsWheel: true));
        world.QueueSnapshot(new PhysicsBodySnapshot(new PhysicsBodyId(1), new PhysicsVector3(2f, 1f, 0f), PhysicsQuaternion.Identity, PhysicsVector3.Zero, PhysicsVector3.Zero));
        world.QueueEvent(PhysicsEvent.ContactPersisted(new PhysicsBodyId(1), new PhysicsBodyId(2)));

        // Tick 1: rules see the wheel touching ground and emit one motor command for tick 2.
        room.Tick();
        room.Tick();

        Assert.Equal(
            new[]
            {
                "apply:0", "step", "copy", "drain",
                "apply:1", "step", "copy", "drain"
            },
            world.OperationLog);
        Assert.Equal(2u, room.CurrentTick);
    }

    [Fact]
    public void DestroyedEntitiesReleaseTheirBodiesInTheAuthoritativeScene()
    {
        ScriptedPhysicsWorld world = new();
        GameRoom room = CreateRoom(() => world);
        room.Spawn(new RoomSpawnSpec(PartPig, new PhysicsVector3(0f, 1f, 0f), RoomActorRole.Pig, HitPoints: 1f));
        room.Spawn(new RoomSpawnSpec(PartBlock, new PhysicsVector3(0f, 1f, 0f)));
        world.QueueSnapshot(new PhysicsBodySnapshot(new PhysicsBodyId(1), new PhysicsVector3(0f, 1f, 0f), PhysicsQuaternion.Identity, new PhysicsVector3(15f, 0f, 0f), PhysicsVector3.Zero));
        world.QueueEvent(PhysicsEvent.ContactStarted(new PhysicsBodyId(1), new PhysicsBodyId(2)));

        room.Tick();

        Assert.Single(world.DestroyedBodies);
        Assert.Equal(1u, world.DestroyedBodies[0].Value);
        Assert.Equal(1, room.BodyCount);
        Assert.Equal(GameplayPhase.Won, room.Phase);
    }

    [Fact]
    public void DisposeReleasesTheWorldAndRejectsFurtherUse()
    {
        ScriptedPhysicsWorld world = new();
        GameRoom room = CreateRoom(() => world);

        room.Dispose();
        room.Dispose();

        Assert.Equal(1, world.DisposeCount);
        Assert.Throws<ObjectDisposedException>(() => room.Tick());
        Assert.Throws<ObjectDisposedException>(() => room.ComputeStateHash());
        Assert.Throws<ObjectDisposedException>(() => room.Spawn(new RoomSpawnSpec(PartBlock, PhysicsVector3.Zero)));
    }

    [Fact]
    public void SpawnAfterFirstTickIsRejected()
    {
        GameRoom room = CreateRoom(() => new ScriptedPhysicsWorld());
        room.Tick();

        Assert.Throws<InvalidOperationException>(() =>
            room.Spawn(new RoomSpawnSpec(PartBlock, PhysicsVector3.Zero)));
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
    public void PhysicsRoomReachesStableOutcomeAndReleasesAllBodies()
    {
        (long hash, GameplayPhase phase, int remainingBodies) = RunPhysicsRoomWithDetails();

        Assert.Equal(GameplayPhase.Won, phase);
        Assert.Equal(3, remainingBodies);
    }

    private static long RunPhysicsRoom()
    {
        (GameRoom room, _) = CreatePhysicsRoom();
        using (room)
        {
            room.RunTicks(240);
            return room.ComputeStateHash();
        }
    }

    private static (long Hash, GameplayPhase Phase, int RemainingBodies) RunPhysicsRoomWithDetails()
    {
        (GameRoom room, ScriptedPhysicsWorld _) = CreatePhysicsRoom();
        using (room)
        {
            for (uint tick = 0; tick < 240 && room.Phase == GameplayPhase.Playing; tick++)
            {
                room.Tick();
            }

            return (room.ComputeStateHash(), room.Phase, room.BodyCount);
        }
    }

    private static (GameRoom Room, ScriptedPhysicsWorld World) CreatePhysicsRoom()
    {
        ScriptedPhysicsWorld world = new();
        PartContentLibrary content = new(PartContentParser.Parse(LevelContentJson));
        GameRoomOptions options = GameRoomOptions.Create(content, () => new BepuPhysicsWorld(new PhysicsVector3(0f, -9.81f, 0f))) with
        {
            GameplayConfig = new GameplayConfig(
                MaxTicks: 600,
                ImpactSpeedThreshold: 5f,
                ImpactDamageFactor: 1f,
                TntBlastRadius: 4f,
                TntBlastImpulse: 12f,
                TntBlastDamage: 40f)
        };
        GameRoom room = new(options);
        room.Spawn(new RoomSpawnSpec(PartGround, new PhysicsVector3(0f, -0.5f, 0f)));
        room.Spawn(new RoomSpawnSpec(PartWheel, new PhysicsVector3(2f, 1f, 0f), MotorImpulsePerTick: 1.5f, MotorDirectionX: 1f, IsWheel: true));
        room.Spawn(new RoomSpawnSpec(PartPig, new PhysicsVector3(8f, 1f, 0f), RoomActorRole.Pig, HitPoints: 20f));
        room.Spawn(new RoomSpawnSpec(PartTnt, new PhysicsVector3(8.9f, 1f, 0f), RoomActorRole.Tnt));
        room.Spawn(new RoomSpawnSpec(PartBlock, new PhysicsVector3(8.9f, 8f, 0f)));
        return (room, world);
    }

    private static GameRoom CreateRoom(Func<IPhysicsWorld> factory)
    {
        PartContentLibrary content = new(PartContentParser.Parse(LevelContentJson));
        return new GameRoom(GameRoomOptions.Create(content, factory));
    }

    private const string LevelContentJson = """
    {
        "format": "pigforge.part-content",
        "schemaVersion": 1,
        "contentVersion": "server-test-v1",
        "parts": [
            { "partTypeId": 1, "name": "block", "mode": "dynamic", "mass": 1, "shapes": [ { "kind": "box", "halfExtents": [0.5, 0.5, 0.5] } ] },
            { "partTypeId": 2, "name": "pig", "mode": "dynamic", "mass": 1, "shapes": [ { "kind": "box", "halfExtents": [0.4, 0.4, 0.4] } ] },
            { "partTypeId": 3, "name": "tnt", "mode": "dynamic", "mass": 1, "shapes": [ { "kind": "box", "halfExtents": [0.4, 0.4, 0.4] } ] },
            { "partTypeId": 4, "name": "wheel", "mode": "dynamic", "mass": 0.8, "shapes": [ { "kind": "box", "halfExtents": [0.5, 0.5, 0.5] } ] },
            { "partTypeId": 5, "name": "ground", "mode": "static", "mass": 0, "shapes": [ { "kind": "box", "halfExtents": [40, 0.5, 10] } ] }
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
        private int _pendingCommandCount;

        public List<string> OperationLog => _log;

        public List<PhysicsBodyId> DestroyedBodies { get; } = new();

        public int DisposeCount { get; private set; }

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
            var id = new PhysicsBodyId((uint)(_snapshots.Count + 1));
            _snapshots.Add(new PhysicsBodySnapshot(id, definition.Position, definition.Rotation, definition.LinearVelocity, definition.AngularVelocity));
            return id;
        }

        public void DestroyBody(PhysicsBodyId body)
        {
            DestroyedBodies.Add(body);
            _snapshots.RemoveAll(snapshot => snapshot.Body == body);
        }

        public PhysicsJointId CreateJoint(JointDefinition definition) => throw new NotSupportedException();

        public void DestroyJoint(PhysicsJointId joint) => throw new NotSupportedException();

        public void ApplyCommands(ReadOnlySpan<PhysicsCommand> commands)
        {
            _pendingCommandCount = commands.Length;
            _log.Add($"apply:{commands.Length}");
        }

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
