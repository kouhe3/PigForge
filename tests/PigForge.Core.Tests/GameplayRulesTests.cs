using System.Runtime.InteropServices;
using PigForge.Core.Content;
using PigForge.Physics.Abstractions;
using PigForge.Physics.Bepu;

namespace PigForge.Core.Tests;

public sealed class GameplayRulesTests
{
    private const uint PartBlock = 1;
    private const uint PartPig = 2;
    private const uint PartTnt = 3;
    private const uint PartWheel = 4;
    private const uint PartGround = 5;

    [Fact]
    public void MotorDrivesWheelOnlyWhileWheelTouchesSomething()
    {
        EntityStore entities = new();
        GameplayHarness harness = new(entities);
        EntityId axle = entities.Create();
        harness.Rules.AddMotor(axle, 2f, 1f);
        harness.Rules.AddWheel(axle);
        harness.Link(axle, new PhysicsBodyId(1));
        harness.IngestBody(new PhysicsBodyId(1), new PhysicsVector3(0, 1, 0), PhysicsVector3.Zero);

        harness.Tick(1, Array.Empty<PhysicsEvent>());
        Assert.Empty(harness.Output.Commands);

        harness.Tick(2, new[] { PhysicsEvent.ContactPersisted(new PhysicsBodyId(1), new PhysicsBodyId(2)) });
        PhysicsCommand command = Assert.Single(harness.Output.Commands);
        Assert.Equal(new PhysicsBodyId(1), command.Body);
        Assert.Equal(2f, command.Impulse.X);
    }

    [Fact]
    public void ImpactAboveThresholdDamagesPigAndWinsLevel()
    {
        EntityStore entities = new();
        GameplayHarness harness = new(entities);
        EntityId pig = entities.Create();
        harness.Rules.AddPig(pig, hitPoints: 8f);
        harness.Link(pig, new PhysicsBodyId(1));
        harness.IngestBody(new PhysicsBodyId(1), new PhysicsVector3(0, 1, 0), new PhysicsVector3(15, 0, 0));
        harness.IngestBody(new PhysicsBodyId(2), new PhysicsVector3(0, 1, 0), PhysicsVector3.Zero);

        harness.Tick(1, new[] { PhysicsEvent.ContactStarted(new PhysicsBodyId(1), new PhysicsBodyId(2)) });

        Assert.Equal(0, harness.Rules.AlivePigs);
        Assert.Contains(pig, harness.Output.DestroyedEntities);
        Assert.Equal(GameplayPhase.Won, harness.Rules.Phase);
    }

    [Fact]
    public void ImpactBelowThresholdLeavesPigUnharmed()
    {
        EntityStore entities = new();
        GameplayHarness harness = new(entities);
        EntityId pig = entities.Create();
        harness.Rules.AddPig(pig, hitPoints: 8f);
        harness.Link(pig, new PhysicsBodyId(1));
        harness.IngestBody(new PhysicsBodyId(1), new PhysicsVector3(0, 1, 0), new PhysicsVector3(2, 0, 0));
        harness.IngestBody(new PhysicsBodyId(2), new PhysicsVector3(0, 1, 0), PhysicsVector3.Zero);

        harness.Tick(1, new[] { PhysicsEvent.ContactStarted(new PhysicsBodyId(1), new PhysicsBodyId(2)) });

        Assert.Equal(1, harness.Rules.AlivePigs);
        Assert.Empty(harness.Output.DestroyedEntities);
        Assert.Equal(GameplayPhase.Playing, harness.Rules.Phase);
    }

    [Fact]
    public void TntIgnitesOnContactAndExplodesAfterFuse()
    {
        EntityStore entities = new();
        GameplayHarness harness = new(entities);
        EntityId tnt = entities.Create();
        EntityId neighbour = entities.Create();
        harness.Rules.AddTnt(tnt, fuseTicks: 1);
        harness.Link(tnt, new PhysicsBodyId(1));
        harness.Link(neighbour, new PhysicsBodyId(2));
        harness.IngestBody(new PhysicsBodyId(1), new PhysicsVector3(0, 0, 0), PhysicsVector3.Zero);
        harness.IngestBody(new PhysicsBodyId(2), new PhysicsVector3(1, 0, 0), PhysicsVector3.Zero);

        harness.Tick(1, new[] { PhysicsEvent.ContactStarted(new PhysicsBodyId(1), new PhysicsBodyId(2)) });
        Assert.Empty(harness.Output.Commands);
        Assert.Empty(harness.Output.DestroyedEntities);

        harness.Tick(2, Array.Empty<PhysicsEvent>());

        PhysicsCommand blast = Assert.Single(harness.Output.Commands);
        Assert.Equal(new PhysicsBodyId(2), blast.Body);
        Assert.True(blast.Impulse.X > 0, "Blast must push the neighbour away from the charge.");
        Assert.Contains(tnt, harness.Output.DestroyedEntities);
    }

    [Fact]
    public void LevelFailsOnTimeout()
    {
        EntityStore entities = new();
        GameplayHarness harness = new(entities, new GameplayConfig(
            MaxTicks: 2, ImpactSpeedThreshold: 5f, ImpactDamageFactor: 1f,
            TntBlastRadius: 4f, TntBlastImpulse: 12f, TntBlastDamage: 25f));
        EntityId pig = entities.Create();
        harness.Rules.AddPig(pig, hitPoints: 100f);
        harness.Link(pig, new PhysicsBodyId(1));

        harness.Tick(1, Array.Empty<PhysicsEvent>());
        harness.Tick(2, Array.Empty<PhysicsEvent>());

        Assert.Equal(GameplayPhase.Failed, harness.Rules.Phase);
    }

    [Fact]
    public void JointBreakEventsAreRecorded()
    {
        EntityStore entities = new();
        GameplayHarness harness = new(entities);

        harness.Tick(1, new[] { PhysicsEvent.JointBroken(new PhysicsJointId(7)) });

        Assert.Contains(7u, harness.Rules.BrokenJoints);
    }

    [Fact]
    public void TypicalReplayFixtureIsDeterministicAndCompletes()
    {
        long first = PhysicsDrivenLevel.RunTypical();
        long second = PhysicsDrivenLevel.RunTypical();

        Assert.Equal(first, second);
        Assert.NotEqual(0, first);
    }

    [Fact]
    public void StressReplayFixtureIsDeterministicAcrossManyBodies()
    {
        long first = PhysicsDrivenLevel.RunStress();
        long second = PhysicsDrivenLevel.RunStress();

        Assert.Equal(first, second);
        Assert.NotEqual(0, first);
    }

    private sealed class GameplayHarness
    {
        private readonly PhysicsBodyStore _bodies;
        private readonly List<PhysicsBodySnapshot> _snapshots = new();

        public GameplayRules Rules { get; }

        public GameplayTickOutput Output { get; } = new();

        public GameplayHarness(EntityStore entities, GameplayConfig? config = null)
        {
            _bodies = new PhysicsBodyStore(entities);
            Rules = new GameplayRules(
                entities,
                new DamageStore(entities),
                new MotorStore(entities),
                new TntStore(entities),
                new WheelStore(entities),
                new PigStore(entities),
                _bodies,
                new TransformStore(entities),
                config);
        }

        public void Link(EntityId entity, PhysicsBodyId body)
        {
            _bodies.Set(entity, new PhysicsBodyLink(body));
            Rules.LinkBody(entity, body);
        }

        public void IngestBody(PhysicsBodyId body, PhysicsVector3 position, PhysicsVector3 velocity) =>
            _snapshots.Add(new PhysicsBodySnapshot(body, position, PhysicsQuaternion.Identity, velocity, PhysicsVector3.Zero));

        public void Tick(uint tick, IReadOnlyList<PhysicsEvent> events) =>
            Rules.Tick(tick, events.ToArray(), CollectionsMarshal.AsSpan(_snapshots), Output);
    }

    /// <summary>Orchestrates a real Bepu world with the rules, mirroring the future room loop: ApplyCommands → Step → events/snapshots → rules.</summary>
    private sealed class PhysicsDrivenLevel : IDisposable
    {
        private const float BlastRadius = 4f;
        private static readonly FixedTimeStep TimeStep = FixedTimeStep.FromSeconds(1f / 60f);
        private static readonly PhysicsVector3 Gravity = new(0, -9.81f, 0);

        private readonly PartContentLibrary _content;
        private readonly EntityStore _entities;
        private readonly PartStore _parts;
        private readonly TransformStore _transforms;
        private readonly PhysicsBodyStore _bodies;
        private readonly DamageStore _damage;
        private readonly GameplayRules _rules;
        private readonly BepuPhysicsWorld _world;
        private readonly GameplayTickOutput _output = new();
        private readonly Dictionary<uint, PhysicsBodyId> _bodyByEntity = new();
        private PhysicsEvent[] _eventBuffer = new PhysicsEvent[64];
        private PhysicsBodySnapshot[] _snapshotBuffer = new PhysicsBodySnapshot[16];

        private PhysicsDrivenLevel(GameplayConfig config)
        {
            _content = new PartContentLibrary(PartContentParser.Parse(LevelContentJson));
            _entities = new EntityStore();
            _parts = new PartStore(_entities);
            _transforms = new TransformStore(_entities);
            _bodies = new PhysicsBodyStore(_entities);
            _damage = new DamageStore(_entities);
            _world = new BepuPhysicsWorld(Gravity);
            _rules = new GameplayRules(
                _entities,
                _damage,
                new MotorStore(_entities),
                new TntStore(_entities),
                new WheelStore(_entities),
                new PigStore(_entities),
                _bodies,
                _transforms,
                config);
        }

        public static long RunTypical()
        {
            using PhysicsDrivenLevel level = new(DefaultConfig());
            level.SpawnGround();
            EntityId wheel = level.Spawn(PartWheel, new PhysicsVector3(2f, 1f, 0f));
            level._rules.AddMotor(wheel, 1.5f, 1f);
            level._rules.AddWheel(wheel);
            level.SpawnPig(new PhysicsVector3(8f, 1f, 0f), hitPoints: 20f);
            level.SpawnTnt(new PhysicsVector3(8.9f, 1f, 0f));
            level.Spawn(PartBlock, new PhysicsVector3(8.9f, 8f, 0f));

            for (uint tick = 1; tick <= 300; tick++)
            {
                level.StepTick(tick);
                if (level._rules.Phase != GameplayPhase.Playing)
                {
                    break;
                }
            }

            Assert.Equal(GameplayPhase.Won, level._rules.Phase);
            return level.ComputeStateHash();
        }

        public static long RunStress()
        {
            using PhysicsDrivenLevel level = new(DefaultConfig());
            level.SpawnGround();
            for (int index = 0; index < 8; index++)
            {
                level.SpawnPig(new PhysicsVector3(2f + (index * 2.4f), 1f, 0f), hitPoints: 30f);
            }

            for (int index = 0; index < 4; index++)
            {
                level.SpawnTnt(new PhysicsVector3(3.2f + (index * 4.8f), 1f, 0f));
            }

            for (int index = 0; index < 64; index++)
            {
                float x = 0.6f + ((index % 16) * 1.2f);
                float y = 6f + ((index / 16) * 1.1f);
                level.Spawn(PartBlock, new PhysicsVector3(x, y, 0f));
            }

            for (uint tick = 1; tick <= 240; tick++)
            {
                level.StepTick(tick);
                if (level._rules.Phase != GameplayPhase.Playing)
                {
                    break;
                }
            }

            return level.ComputeStateHash();
        }

        private static GameplayConfig DefaultConfig() => new(
            MaxTicks: 300,
            ImpactSpeedThreshold: 5f,
            ImpactDamageFactor: 1f,
            TntBlastRadius: BlastRadius,
            TntBlastImpulse: 12f,
            TntBlastDamage: 40f);

        private void SpawnGround()
        {
            // The ground spawns through the same path but is flagged static so rule
            // impulses never target it.
            EntityId ground = _entities.Create();
            _parts.Set(ground, new PartLink(PartGround));
            _transforms.Set(ground, new EntityTransform(new PhysicsVector3(0f, -0.5f, 0f), PhysicsQuaternion.Identity));
            PhysicsBodyId body = _world.CreateBody(_content.CreateBodyDefinition(PartGround, new PhysicsVector3(0f, -0.5f, 0f), PhysicsQuaternion.Identity));
            _bodies.Set(ground, new PhysicsBodyLink(body));
            _rules.LinkBody(ground, body, isDynamic: false);
            _bodyByEntity.Add(ground.Value, body);
            EnsureBuffers();
        }

        private EntityId SpawnPig(PhysicsVector3 position, float hitPoints)
        {
            EntityId pig = Spawn(PartPig, position);
            _rules.AddPig(pig, hitPoints);
            return pig;
        }

        private EntityId SpawnTnt(PhysicsVector3 position)
        {
            EntityId tnt = Spawn(PartTnt, position);
            _rules.AddTnt(tnt, fuseTicks: 1);
            return tnt;
        }

        private EntityId Spawn(uint partTypeId, PhysicsVector3 position)
        {
            EntityId entity = _entities.Create();
            _parts.Set(entity, new PartLink(partTypeId));
            _transforms.Set(entity, new EntityTransform(position, PhysicsQuaternion.Identity));
            PhysicsBodyId body = _world.CreateBody(_content.CreateBodyDefinition(partTypeId, position, PhysicsQuaternion.Identity));
            _bodies.Set(entity, new PhysicsBodyLink(body));
            _rules.LinkBody(entity, body);
            _bodyByEntity.Add(entity.Value, body);
            EnsureBuffers();
            return entity;
        }

        private void StepTick(uint tick)
        {
            _world.ApplyCommands(CollectionsMarshal.AsSpan(_output.Commands));
            _world.Step(TimeStep);
            int eventCount = _world.DrainEvents(_eventBuffer);
            int snapshotCount = _world.CopySnapshots(_snapshotBuffer);
            _rules.Tick(tick, _eventBuffer.AsSpan(0, eventCount), _snapshotBuffer.AsSpan(0, snapshotCount), _output);

            foreach (EntityId destroyed in _output.DestroyedEntities)
            {
                if (_bodyByEntity.Remove(destroyed.Value, out PhysicsBodyId body))
                {
                    _world.DestroyBody(body);
                }
            }
        }

        private long ComputeStateHash()
        {
            long hash = 17;
            hash = unchecked((hash * 31) + (int)_rules.Phase);
            hash = unchecked((hash * 31) + _rules.AlivePigs);
            hash = unchecked((hash * 31) + _rules.BrokenJoints.Count);

            Dictionary<uint, float> hitPointsByEntity = new();
            var damage = _damage.GetEnumerator();
            while (damage.MoveNext())
            {
                hitPointsByEntity[damage.CurrentId.Value] = damage.CurrentValue.HitPoints;
            }

            foreach (uint entityValue in hitPointsByEntity.Keys.OrderBy(value => value))
            {
                hash = unchecked((hash * 31) + entityValue);
                hash = unchecked((hash * 31) + BitConverter.SingleToInt32Bits(hitPointsByEntity[entityValue]));
            }

            return hash;
        }

        private void EnsureBuffers()
        {
            int bodyCount = _bodyByEntity.Count + 1;
            _snapshotBuffer = new PhysicsBodySnapshot[bodyCount];
            _eventBuffer = new PhysicsEvent[(bodyCount * (bodyCount - 1) / 2) + bodyCount + 16];
        }

        public void Dispose() => _world.Dispose();
    }

    private const string LevelContentJson = """
    {
        "format": "pigforge.part-content",
        "schemaVersion": 1,
        "contentVersion": "gameplay-test-v1",
        "parts": [
            { "partTypeId": 1, "name": "block", "mode": "dynamic", "mass": 1, "shapes": [ { "kind": "box", "halfExtents": [0.5, 0.5, 0.5] } ] },
            { "partTypeId": 2, "name": "pig", "mode": "dynamic", "mass": 1, "shapes": [ { "kind": "box", "halfExtents": [0.4, 0.4, 0.4] } ] },
            { "partTypeId": 3, "name": "tnt", "mode": "dynamic", "mass": 1, "shapes": [ { "kind": "box", "halfExtents": [0.4, 0.4, 0.4] } ] },
            { "partTypeId": 4, "name": "wheel", "mode": "dynamic", "mass": 0.8, "shapes": [ { "kind": "box", "halfExtents": [0.5, 0.5, 0.5] } ] },
            { "partTypeId": 5, "name": "ground", "mode": "static", "mass": 0, "shapes": [ { "kind": "box", "halfExtents": [40, 0.5, 10] } ] }
        ]
    }
    """;
}
