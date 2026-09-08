using System.Runtime.InteropServices;
using PigForge.Core.Content;
using PigForge.Physics.Abstractions;
using PigForge.Physics.Bepu;

namespace PigForge.Core.Tests;

/// <summary>
/// Runtime rules tests rewritten against ADR-002 semantics: pigs are indestructible
/// bouncy cargo, TNT is a pure momentum source, win is delivery into the goal zone,
/// and a pig leaving the map bounds requests a restart.
/// </summary>
public sealed class GameplayRulesTests
{
    [Fact]
    public void MotorDrivesWheelOnlyWhileWheelTouchesSomething()
    {
        EntityStore entities = new();
        GameplayHarness harness = new(entities, FarZonesConfig());
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
    public void PigIsIndestructibleUnderImpacts()
    {
        EntityStore entities = new();
        GameplayHarness harness = new(entities, FarZonesConfig());
        EntityId pig = entities.Create();
        harness.Rules.AddPig(pig);
        harness.Link(pig, new PhysicsBodyId(1));
        harness.IngestBody(new PhysicsBodyId(1), new PhysicsVector3(0, 1, 0), new PhysicsVector3(20, 0, 0));
        harness.IngestBody(new PhysicsBodyId(2), new PhysicsVector3(0, 1, 0), PhysicsVector3.Zero);

        harness.Tick(1, new[] { PhysicsEvent.ContactStarted(new PhysicsBodyId(1), new PhysicsBodyId(2)) });
        harness.Tick(2, new[] { PhysicsEvent.ContactStarted(new PhysicsBodyId(1), new PhysicsBodyId(2)) });

        Assert.Equal(1, harness.Rules.AlivePigs);
        Assert.Empty(harness.Output.DestroyedEntities);
        Assert.Equal(GameplayPhase.Playing, harness.Rules.Phase);
        Assert.False(harness.Rules.RestartRequested);
    }

    [Fact]
    public void TntIgnitesOnContactAndExplodesIntoPureImpulse()
    {
        EntityStore entities = new();
        GameplayHarness harness = new(entities, FarZonesConfig());
        EntityId tnt = entities.Create();
        EntityId neighbour = entities.Create();
        harness.Rules.AddTnt(tnt, fuseTicks: 1);
        harness.Link(tnt, new PhysicsBodyId(1));
        harness.Link(neighbour, new PhysicsBodyId(2));

        // Establish pre-impact velocities, then the contact tick shows the velocity
        // change the strike produces (the real impact signal per ADR-002).
        harness.IngestBody(new PhysicsBodyId(1), new PhysicsVector3(0, 0, 0), PhysicsVector3.Zero);
        harness.IngestBody(new PhysicsBodyId(2), new PhysicsVector3(1, 0, 0), new PhysicsVector3(10, 0, 0));
        harness.Tick(1, Array.Empty<PhysicsEvent>());

        harness.IngestBody(new PhysicsBodyId(1), new PhysicsVector3(0, 0, 0), new PhysicsVector3(5, 0, 0));
        harness.IngestBody(new PhysicsBodyId(2), new PhysicsVector3(1, 0, 0), PhysicsVector3.Zero);
        harness.Tick(2, new[] { PhysicsEvent.ContactStarted(new PhysicsBodyId(1), new PhysicsBodyId(2)) });
        Assert.Empty(harness.Output.Commands);
        Assert.Empty(harness.Output.DestroyedEntities);

        // Ignition and the fuse decrement share the contact tick; the blast follows next tick.
        harness.Tick(3, Array.Empty<PhysicsEvent>());

        PhysicsCommand blast = Assert.Single(harness.Output.Commands);
        Assert.Equal(new PhysicsBodyId(2), blast.Body);
        Assert.True(blast.Impulse.X > 0, "Blast must push the neighbour away from the charge.");
        Assert.Contains(tnt, harness.Output.DestroyedEntities);
        Assert.Equal(GameplayPhase.Playing, harness.Rules.Phase);
    }

    [Fact]
    public void PigEnteringGoalZoneWins()
    {
        EntityStore entities = new();
        GameplayHarness harness = new(entities, new GameplayConfig(
            GoalZone: new GameplayZone(new PhysicsVector3(-9, 0, -2), new PhysicsVector3(-7, 4, 2)),
            MapBounds: new GameplayZone(new PhysicsVector3(-1000, -1000, -1000), new PhysicsVector3(1000, 1000, 1000)),
            TntBlastRadius: 4f,
            TntBlastImpulse: 12f,
            TntIgniteImpactSpeed: 5f));
        EntityId pig = entities.Create();
        harness.Rules.AddPig(pig);
        harness.Link(pig, new PhysicsBodyId(1));
        harness.IngestBody(new PhysicsBodyId(1), new PhysicsVector3(-8, 1, 0), PhysicsVector3.Zero);

        harness.Tick(1, Array.Empty<PhysicsEvent>());

        Assert.Equal(GameplayPhase.Won, harness.Rules.Phase);
    }

    [Fact]
    public void PigLeavingMapBoundsRequestsRestart()
    {
        EntityStore entities = new();
        GameplayHarness harness = new(entities, new GameplayConfig(
            GoalZone: new GameplayZone(new PhysicsVector3(-9, 0, -2), new PhysicsVector3(-7, 4, 2)),
            MapBounds: new GameplayZone(new PhysicsVector3(-50, -10, -50), new PhysicsVector3(50, 50, 50)),
            TntBlastRadius: 4f,
            TntBlastImpulse: 12f,
            TntIgniteImpactSpeed: 5f));
        EntityId pig = entities.Create();
        harness.Rules.AddPig(pig);
        harness.Link(pig, new PhysicsBodyId(1));
        harness.IngestBody(new PhysicsBodyId(1), new PhysicsVector3(60, 1, 0), PhysicsVector3.Zero);

        harness.Tick(1, Array.Empty<PhysicsEvent>());

        Assert.Equal(GameplayPhase.Failed, harness.Rules.Phase);
        Assert.True(harness.Rules.RestartRequested);
        Assert.Equal(1, harness.Rules.AlivePigs);
    }

    [Fact]
    public void JointBreakEventsAreRecorded()
    {
        EntityStore entities = new();
        GameplayHarness harness = new(entities, FarZonesConfig());

        harness.Tick(1, new[] { PhysicsEvent.JointBroken(new PhysicsJointId(7)) });

        Assert.Contains(7u, harness.Rules.BrokenJoints);
    }

    [Fact]
    public void TypicalLevelFixtureDeliversPigAndIsDeterministic()
    {
        long first = PhysicsDrivenLevel.RunTypical();
        long second = PhysicsDrivenLevel.RunTypical();

        Assert.Equal(first, second);
        Assert.NotEqual(0, first);
    }

    [Fact]
    public void StressLevelFixtureIsDeterministicAcrossManyBodies()
    {
        long first = PhysicsDrivenLevel.RunStress();
        long second = PhysicsDrivenLevel.RunStress();

        Assert.Equal(first, second);
        Assert.NotEqual(0, first);
    }

    private static GameplayConfig FarZonesConfig() => new(
        GoalZone: new GameplayZone(new PhysicsVector3(500, 500, 500), new PhysicsVector3(501, 501, 501)),
        MapBounds: new GameplayZone(new PhysicsVector3(-1000, -1000, -1000), new PhysicsVector3(1000, 1000, 1000)),
        TntBlastRadius: 4f,
        TntBlastImpulse: 12f,
        TntIgniteImpactSpeed: 5f);

    private sealed class GameplayHarness
    {
        private readonly PhysicsBodyStore _bodies;
        private readonly List<PhysicsBodySnapshot> _snapshots = new();

        public GameplayRules Rules { get; }

        public GameplayTickOutput Output { get; } = new();

        public GameplayHarness(EntityStore entities, GameplayConfig config)
        {
            _bodies = new PhysicsBodyStore(entities);
            Rules = new GameplayRules(
                entities,
                new MotorStore(entities),
                new BalloonStore(entities),
                new FanStore(entities),
                new SpringStore(entities),
                new RocketStore(entities),
                new TntStore(entities),
                new WheelStore(entities),
                new PigStore(entities),
                _bodies,
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

    /// <summary>Orchestrates a real Bepu world with rules and level content, mirroring the room loop.</summary>
    private sealed class PhysicsDrivenLevel : IDisposable
    {
        private static readonly FixedTimeStep TimeStep = FixedTimeStep.FromSeconds(1f / 60f);
        private static readonly PhysicsVector3 Gravity = new(0, -9.81f, 0);
        private static readonly PartContentLibrary Content = new(PartContentParser.Parse(PartContentJson));
        private static readonly LevelContentDocument TypicalLevel = LevelContentLibrary.Parse(TypicalLevelJson);
        private static readonly LevelContentDocument StressLevel = LevelContentLibrary.Parse(StressLevelJson);

        private readonly EntityStore _entities = new();
        private readonly PartStore _parts;
        private readonly TransformStore _transforms;
        private readonly PhysicsBodyStore _bodies;
        private readonly GameplayRules _rules;
        private readonly BepuPhysicsWorld _world;
        private readonly GameplayTickOutput _output = new();
        private readonly Dictionary<uint, PhysicsBodyId> _bodyByEntity = new();
        private PhysicsEvent[] _eventBuffer = Array.Empty<PhysicsEvent>();
        private PhysicsBodySnapshot[] _snapshotBuffer = Array.Empty<PhysicsBodySnapshot>();

        private PhysicsDrivenLevel(LevelContentDocument level)
        {
            _parts = new PartStore(_entities);
            _transforms = new TransformStore(_entities);
            _bodies = new PhysicsBodyStore(_entities);
            _world = new BepuPhysicsWorld(Gravity);
            _rules = new GameplayRules(
                _entities,
                new MotorStore(_entities),
                new BalloonStore(_entities),
                new FanStore(_entities),
                new SpringStore(_entities),
                new RocketStore(_entities),
                new TntStore(_entities),
                new WheelStore(_entities),
                new PigStore(_entities),
                _bodies,
                new GameplayConfig(level.GoalZone, level.MapBounds, TntBlastRadius: 4f, TntBlastImpulse: 25f, TntIgniteImpactSpeed: 5f));
            foreach (LevelSpawnDefinition spawn in level.Spawns)
            {
                Spawn(spawn);
            }
        }

        public static long RunTypical()
        {
            using PhysicsDrivenLevel level = new(TypicalLevel);
            for (uint tick = 1; tick <= 300 && level._rules.Phase == GameplayPhase.Playing; tick++)
            {
                level.StepTick(tick);
            }

            Assert.Equal(GameplayPhase.Won, level._rules.Phase);
            return level.ComputeStateHash();
        }

        public static long RunStress()
        {
            using PhysicsDrivenLevel level = new(StressLevel);
            for (uint tick = 1; tick <= 240 && level._rules.Phase == GameplayPhase.Playing; tick++)
            {
                level.StepTick(tick);
            }

            return level.ComputeStateHash();
        }

        private void Spawn(LevelSpawnDefinition spawn)
        {
            EntityId entity = _entities.Create();
            _parts.Set(entity, new PartLink(spawn.PartTypeId));
            _transforms.Set(entity, new EntityTransform(spawn.Position, PhysicsQuaternion.Identity));
            PhysicsBodyId body = _world.CreateBody(Content.CreateBodyDefinition(spawn.PartTypeId, spawn.Position, PhysicsQuaternion.Identity));
            _bodies.Set(entity, new PhysicsBodyLink(body));
            _rules.LinkBody(entity, body);
            _bodyByEntity.Add(entity.Value, body);
            EnsureBuffers();

            if (spawn.MotorImpulsePerTick != 0f)
            {
                _rules.AddMotor(entity, spawn.MotorImpulsePerTick, spawn.MotorDirectionX);
            }

            if (spawn.IsWheel)
            {
                _rules.AddWheel(entity);
            }

            if (spawn.Role == LevelActorRole.Pig)
            {
                _rules.AddPig(entity);
            }
            else if (spawn.Role == LevelActorRole.Tnt)
            {
                _rules.AddTnt(entity, spawn.TntFuseTicks);
            }
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
            hash = unchecked((hash * 31) + _bodyByEntity.Count.GetHashCode());
            return hash;
        }

        private void EnsureBuffers()
        {
            int bodyCount = _bodyByEntity.Count;
            _snapshotBuffer = new PhysicsBodySnapshot[bodyCount];
            _eventBuffer = new PhysicsEvent[(bodyCount * (bodyCount - 1) / 2) + bodyCount + 16];
        }

        public void Dispose() => _world.Dispose();
    }

    private const string PartContentJson = """
    {
        "format": "pigforge.part-content",
        "schemaVersion": 1,
        "contentVersion": "gameplay-test-v1",
        "parts": [
            { "partTypeId": 1, "name": "block", "mode": "dynamic", "mass": 1, "shapes": [ { "kind": "box", "halfExtents": [0.5, 0.5, 0.5] } ] },
            { "partTypeId": 2, "name": "pig", "mode": "dynamic", "mass": 1, "material": { "restitution": 0.2, "friction": 0.4 }, "shapes": [ { "kind": "box", "halfExtents": [0.4, 0.4, 0.4] } ] },
            { "partTypeId": 3, "name": "tnt", "mode": "dynamic", "mass": 1, "shapes": [ { "kind": "box", "halfExtents": [0.4, 0.4, 0.4] } ] },
            { "partTypeId": 4, "name": "wheel", "mode": "dynamic", "mass": 0.8, "shapes": [ { "kind": "box", "halfExtents": [0.5, 0.5, 0.5] } ] },
            { "partTypeId": 5, "name": "ground", "mode": "static", "mass": 0, "material": { "restitution": 0, "friction": 0.8 }, "shapes": [ { "kind": "box", "halfExtents": [40, 0.5, 10] } ] }
        ]
    }
    """;

    private const string TypicalLevelJson = """
    {
        "format": "pigforge.level-content",
        "schemaVersion": 1,
        "contentVersion": "typical-level-v1",
        "goalZone": { "min": [-9, 0, -2], "max": [-7, 4, 2] },
        "bounds": { "min": [-100, -5, -20], "max": [100, 60, 20] },
        "spawns": [
            { "partTypeId": 5, "position": [0, -0.5, 0] },
            { "partTypeId": 2, "position": [7.5, 1, 0], "role": "pig" },
            { "partTypeId": 3, "position": [8.9, 1, 0], "role": "tnt" },
            { "partTypeId": 1, "position": [8.9, 8, 0] }
        ]
    }
    """;

    private const string StressLevelJson = """
    {
        "format": "pigforge.level-content",
        "schemaVersion": 1,
        "contentVersion": "stress-level-v1",
        "goalZone": { "min": [-9, 0, -2], "max": [-7, 4, 2] },
        "bounds": { "min": [-100, -5, -20], "max": [100, 60, 20] },
        "spawns": [
            { "partTypeId": 5, "position": [0, -0.5, 0] },
            { "partTypeId": 2, "position": [2, 1, 0], "role": "pig" },
            { "partTypeId": 2, "position": [4.4, 1, 0], "role": "pig" },
            { "partTypeId": 2, "position": [6.8, 1, 0], "role": "pig" },
            { "partTypeId": 2, "position": [9.2, 1, 0], "role": "pig" },
            { "partTypeId": 2, "position": [11.6, 1, 0], "role": "pig" },
            { "partTypeId": 2, "position": [14, 1, 0], "role": "pig" },
            { "partTypeId": 2, "position": [16.4, 1, 0], "role": "pig" },
            { "partTypeId": 2, "position": [18.8, 1, 0], "role": "pig" },
            { "partTypeId": 3, "position": [3.2, 1, 0], "role": "tnt" },
            { "partTypeId": 3, "position": [8, 1, 0], "role": "tnt" },
            { "partTypeId": 3, "position": [12.8, 1, 0], "role": "tnt" },
            { "partTypeId": 3, "position": [17.6, 1, 0], "role": "tnt" },
            { "partTypeId": 1, "position": [0.6, 6, 0] },
            { "partTypeId": 1, "position": [1.8, 6, 0] },
            { "partTypeId": 1, "position": [3, 6, 0] },
            { "partTypeId": 1, "position": [4.2, 6, 0] },
            { "partTypeId": 1, "position": [5.4, 6, 0] },
            { "partTypeId": 1, "position": [6.6, 6, 0] },
            { "partTypeId": 1, "position": [7.8, 6, 0] },
            { "partTypeId": 1, "position": [9, 6, 0] },
            { "partTypeId": 1, "position": [10.2, 6, 0] },
            { "partTypeId": 1, "position": [11.4, 6, 0] },
            { "partTypeId": 1, "position": [12.6, 6, 0] },
            { "partTypeId": 1, "position": [13.8, 6, 0] },
            { "partTypeId": 1, "position": [15, 6, 0] },
            { "partTypeId": 1, "position": [16.2, 6, 0] },
            { "partTypeId": 1, "position": [17.4, 6, 0] },
            { "partTypeId": 1, "position": [18.6, 6, 0] },
            { "partTypeId": 1, "position": [19.8, 6, 0] },
            { "partTypeId": 1, "position": [0.6, 7.1, 0] },
            { "partTypeId": 1, "position": [1.8, 7.1, 0] },
            { "partTypeId": 1, "position": [3, 7.1, 0] },
            { "partTypeId": 1, "position": [4.2, 7.1, 0] },
            { "partTypeId": 1, "position": [5.4, 7.1, 0] },
            { "partTypeId": 1, "position": [6.6, 7.1, 0] },
            { "partTypeId": 1, "position": [7.8, 7.1, 0] },
            { "partTypeId": 1, "position": [9, 7.1, 0] },
            { "partTypeId": 1, "position": [10.2, 7.1, 0] },
            { "partTypeId": 1, "position": [11.4, 7.1, 0] },
            { "partTypeId": 1, "position": [12.6, 7.1, 0] },
            { "partTypeId": 1, "position": [13.8, 7.1, 0] },
            { "partTypeId": 1, "position": [15, 7.1, 0] },
            { "partTypeId": 1, "position": [16.2, 7.1, 0] },
            { "partTypeId": 1, "position": [17.4, 7.1, 0] },
            { "partTypeId": 1, "position": [18.6, 7.1, 0] },
            { "partTypeId": 1, "position": [19.8, 7.1, 0] },
            { "partTypeId": 1, "position": [0.6, 8.2, 0] },
            { "partTypeId": 1, "position": [1.8, 8.2, 0] },
            { "partTypeId": 1, "position": [3, 8.2, 0] },
            { "partTypeId": 1, "position": [4.2, 8.2, 0] },
            { "partTypeId": 1, "position": [5.4, 8.2, 0] },
            { "partTypeId": 1, "position": [6.6, 8.2, 0] },
            { "partTypeId": 1, "position": [7.8, 8.2, 0] },
            { "partTypeId": 1, "position": [9, 8.2, 0] },
            { "partTypeId": 1, "position": [10.2, 8.2, 0] },
            { "partTypeId": 1, "position": [11.4, 8.2, 0] },
            { "partTypeId": 1, "position": [12.6, 8.2, 0] },
            { "partTypeId": 1, "position": [13.8, 8.2, 0] },
            { "partTypeId": 1, "position": [15, 8.2, 0] },
            { "partTypeId": 1, "position": [16.2, 8.2, 0] },
            { "partTypeId": 1, "position": [17.4, 8.2, 0] },
            { "partTypeId": 1, "position": [18.6, 8.2, 0] },
            { "partTypeId": 1, "position": [19.8, 8.2, 0] }
        ]
    }
    """;

    [Fact]
    public void BalloonAppliesPureVerticalLiftEveryTick()
    {
        EntityStore entities = new();
        GameplayHarness harness = new(entities, FarZonesConfig());
        EntityId balloon = entities.Create();
        harness.Rules.AddBalloon(balloon, 1.5f);
        harness.Link(balloon, new PhysicsBodyId(1));
        harness.IngestBody(new PhysicsBodyId(1), new PhysicsVector3(0, 1, 0), PhysicsVector3.Zero);

        harness.Tick(1, Array.Empty<PhysicsEvent>());
        PhysicsCommand command = Assert.Single(harness.Output.Commands);
        Assert.Equal(new PhysicsBodyId(1), command.Body);
        Assert.Equal(0f, command.Impulse.X);
        Assert.Equal(1.5f, command.Impulse.Y);
        Assert.Equal(0f, command.Impulse.Z);

        harness.Tick(2, Array.Empty<PhysicsEvent>());
        Assert.Single(harness.Output.Commands);
    }

    [Fact]
    public void FanPushesAlongNormalizedPlanarDirection()
    {
        EntityStore entities = new();
        GameplayHarness harness = new(entities, FarZonesConfig());
        EntityId fan = entities.Create();
        harness.Rules.AddFan(fan, 2f, 1f, 1f);
        harness.Link(fan, new PhysicsBodyId(1));
        harness.IngestBody(new PhysicsBodyId(1), new PhysicsVector3(0, 1, 0), PhysicsVector3.Zero);
        harness.Tick(1, Array.Empty<PhysicsEvent>());
        PhysicsCommand command = Assert.Single(harness.Output.Commands);
        Assert.Equal(new PhysicsBodyId(1), command.Body);
        float normalization = MathF.Sqrt(2f);
        Assert.Equal(2f / normalization, command.Impulse.X, 5);
        Assert.Equal(2f / normalization, command.Impulse.Y, 5);
        Assert.Equal(0f, command.Impulse.Z);
    }

    [Fact]
    public void FanWithZeroDirectionProducesNoImpulse()
    {
        EntityStore entities = new();
        GameplayHarness harness = new(entities, FarZonesConfig());
        EntityId fan = entities.Create();
        harness.Rules.AddFan(fan, 2f, 0f, 0f);
        harness.Link(fan, new PhysicsBodyId(1));
        harness.IngestBody(new PhysicsBodyId(1), new PhysicsVector3(0, 1, 0), PhysicsVector3.Zero);

        harness.Tick(1, Array.Empty<PhysicsEvent>());
        Assert.Empty(harness.Output.Commands);
    }

    [Fact]
    public void SpringLaunchesOncePerTouchdown()
    {
        EntityStore entities = new();
        GameplayHarness harness = new(entities, FarZonesConfig());
        EntityId spring = entities.Create();
        harness.Rules.AddSpring(spring, 12f);
        harness.Link(spring, new PhysicsBodyId(1));
        harness.IngestBody(new PhysicsBodyId(1), new PhysicsVector3(0, 1, 0), PhysicsVector3.Zero);

        // First grounded tick: one launch impulse.
        harness.Tick(1, new[] { PhysicsEvent.ContactPersisted(new PhysicsBodyId(1), new PhysicsBodyId(2)) });
        PhysicsCommand command = Assert.Single(harness.Output.Commands);
        Assert.Equal(new PhysicsBodyId(1), command.Body);
        Assert.Equal(12f, command.Impulse.Y);

        // Still grounded next tick: no second launch (BouncedRecently holds).
        harness.Tick(2, new[] { PhysicsEvent.ContactPersisted(new PhysicsBodyId(1), new PhysicsBodyId(2)) });
        Assert.Empty(harness.Output.Commands);

        // Leaves ground, lands again: second launch.
        harness.Tick(3, Array.Empty<PhysicsEvent>());
        harness.Tick(4, new[] { PhysicsEvent.ContactPersisted(new PhysicsBodyId(1), new PhysicsBodyId(2)) });
        Assert.Single(harness.Output.Commands);
    }

    [Fact]
    public void RocketThrustsThenSelfDestructsAfterDuration()
    {
        EntityStore entities = new();
        GameplayHarness harness = new(entities, FarZonesConfig());
        EntityId rocket = entities.Create();
        harness.Rules.AddRocket(rocket, 4f, 1f, durationTicks: 2);
        harness.Link(rocket, new PhysicsBodyId(1));
        harness.IngestBody(new PhysicsBodyId(1), new PhysicsVector3(0, 1, 0), PhysicsVector3.Zero);

        harness.Tick(1, Array.Empty<PhysicsEvent>());
        PhysicsCommand command = Assert.Single(harness.Output.Commands);
        Assert.Equal(new PhysicsBodyId(1), command.Body);
        Assert.Equal(4f, command.Impulse.X);
        Assert.Empty(harness.Output.DestroyedEntities);

        harness.Tick(2, Array.Empty<PhysicsEvent>());
        Assert.Single(harness.Output.Commands);
        Assert.Empty(harness.Output.DestroyedEntities);

        // Duration exhausted: the rocket self-destructs (destroyed entity surfaced).
        harness.Tick(3, Array.Empty<PhysicsEvent>());
        Assert.Empty(harness.Output.Commands);
        Assert.Contains(rocket, harness.Output.DestroyedEntities);
    }

    [Fact]
    public void RocketWithZeroRemainingTicksSpendsImmediately()
    {
        EntityStore entities = new();
        GameplayHarness harness = new(entities, FarZonesConfig());
        EntityId rocket = entities.Create();
        harness.Rules.AddRocket(rocket, 2f, 1f, durationTicks: 0);
        harness.Link(rocket, new PhysicsBodyId(1));
        harness.IngestBody(new PhysicsBodyId(1), new PhysicsVector3(0, 1, 0), PhysicsVector3.Zero);

        harness.Tick(1, Array.Empty<PhysicsEvent>());
        Assert.Empty(harness.Output.Commands);
        Assert.Contains(rocket, harness.Output.DestroyedEntities);
    }
}
