using System.Runtime.InteropServices;
using PigForge.Core.Construction;
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
    public void TntSwitchIgnitesAndChainsIntoNeighbouringCharges()
    {
        EntityStore entities = new();
        GameplayHarness harness = new(entities, FarZonesConfig());
        EntityId first = entities.Create();
        EntityId second = entities.Create();
        harness.Rules.AddTnt(first, fuseTicks: 0);
        harness.Rules.AddTnt(second, fuseTicks: 1);
        harness.Rules.AddActivation(first);
        harness.Link(first, new PhysicsBodyId(1));
        harness.Link(second, new PhysicsBodyId(2));
        harness.IngestBody(new PhysicsBodyId(1), new PhysicsVector3(0, 0, 0), PhysicsVector3.Zero);
        harness.IngestBody(new PhysicsBodyId(2), new PhysicsVector3(2, 0, 0), PhysicsVector3.Zero);

        harness.Rules.SetActive(first, true);
        harness.Tick(1, Array.Empty<PhysicsEvent>());

        Assert.Contains(first, harness.Output.DestroyedEntities);
        Assert.DoesNotContain(second, harness.Output.DestroyedEntities);

        // The chain lights the neighbour: its own fuse runs from the next tick.
        harness.Tick(2, Array.Empty<PhysicsEvent>());
        Assert.DoesNotContain(second, harness.Output.DestroyedEntities);
        harness.Tick(3, Array.Empty<PhysicsEvent>());
        Assert.Contains(second, harness.Output.DestroyedEntities);
    }

    /// <summary>
    /// Destroying one member of a compound must not orphan the body's bookkeeping: the parts
    /// still welded into it keep their entity lookup and their dynamic flag, so an impulse
    /// already aimed at the surviving body is not dropped by the orphan filter.
    /// </summary>
    [Fact]
    public void DestroyingOneMemberKeepsTheSharedBodyDynamicForTheSurvivor()
    {
        EntityStore entities = new();
        GameplayHarness harness = new(entities, FarZonesConfig());
        EntityId removed = entities.Create();
        EntityId survivor = entities.Create();
        harness.Rules.AddMotor(survivor, 2f, 1f);
        harness.Link(removed, new PhysicsBodyId(1));
        harness.Link(survivor, new PhysicsBodyId(1));
        harness.IngestBody(new PhysicsBodyId(1), new PhysicsVector3(0, 1, 0), PhysicsVector3.Zero);

        harness.Tick(1, Array.Empty<PhysicsEvent>());
        PhysicsCommand motor = Assert.Single(harness.Output.Commands);
        Assert.Equal(new PhysicsBodyId(1), motor.Body);

        harness.Rules.CleanupEntityStores(removed);
        harness.Tick(2, Array.Empty<PhysicsEvent>());

        motor = Assert.Single(harness.Output.Commands);
        Assert.Equal(new PhysicsBodyId(1), motor.Body);
    }

    /// <summary>
    /// The entity lookup behind impact ignition follows the representative member: after one
    /// member of the compound is destroyed, the blast still resolves the surviving charge and
    /// lights it.
    /// </summary>
    [Fact]
    public void DestroyingOneMemberKeepsTheSharedBodysEntityLookupForImpactIgnition()
    {
        EntityStore entities = new();
        GameplayHarness harness = new(entities, FarZonesConfig());
        EntityId removed = entities.Create();
        EntityId charge = entities.Create();
        EntityId striker = entities.Create();
        harness.Rules.AddTnt(charge, fuseTicks: 1);
        harness.Link(removed, new PhysicsBodyId(1));
        harness.Link(charge, new PhysicsBodyId(1));
        harness.Link(striker, new PhysicsBodyId(2));

        harness.IngestBody(new PhysicsBodyId(1), new PhysicsVector3(0, 1, 0), PhysicsVector3.Zero);
        harness.IngestBody(new PhysicsBodyId(2), new PhysicsVector3(1, 1, 0), new PhysicsVector3(10, 0, 0));
        harness.Tick(1, Array.Empty<PhysicsEvent>());

        harness.Rules.CleanupEntityStores(removed);
        harness.IngestBody(new PhysicsBodyId(1), new PhysicsVector3(0, 1, 0), new PhysicsVector3(6, 0, 0));
        harness.IngestBody(new PhysicsBodyId(2), new PhysicsVector3(1, 1, 0), PhysicsVector3.Zero);
        harness.Tick(2, new[] { PhysicsEvent.ContactStarted(new PhysicsBodyId(1), new PhysicsBodyId(2)) });
        harness.Tick(3, Array.Empty<PhysicsEvent>());

        Assert.Contains(charge, harness.Output.DestroyedEntities);
    }

    [Fact]
    public void ChargeWithIgniteOnImpactDisabledOnlyFiresFromItsSwitch()
    {
        EntityStore entities = new();
        GameplayHarness harness = new(entities, FarZonesConfig());
        EntityId charge = entities.Create();
        EntityId striker = entities.Create();
        harness.Rules.AddTnt(charge, fuseTicks: 0, igniteOnImpact: false);
        harness.Link(charge, new PhysicsBodyId(1));
        harness.Link(striker, new PhysicsBodyId(2));

        harness.IngestBody(new PhysicsBodyId(1), new PhysicsVector3(0, 0, 0), PhysicsVector3.Zero);
        harness.IngestBody(new PhysicsBodyId(2), new PhysicsVector3(1, 0, 0), new PhysicsVector3(10, 0, 0));
        harness.Tick(1, Array.Empty<PhysicsEvent>());

        harness.IngestBody(new PhysicsBodyId(1), new PhysicsVector3(0, 0, 0), new PhysicsVector3(6, 0, 0));
        harness.IngestBody(new PhysicsBodyId(2), new PhysicsVector3(1, 0, 0), PhysicsVector3.Zero);
        harness.Tick(2, new[] { PhysicsEvent.ContactStarted(new PhysicsBodyId(1), new PhysicsBodyId(2)) });
        harness.Tick(3, Array.Empty<PhysicsEvent>());

        Assert.Empty(harness.Output.DestroyedEntities);
    }

    /// <summary>
    /// G89: the original's own bounce threshold. Unity's <c>Physics.bounceThreshold</c> is BPLE's
    /// <c>ProjectSettings/DynamicsManager.asset</c> <c>m_BounceThreshold: 2</c>, and PhysX drops a
    /// contact's restitution below it -- so a landing at 1.5 m/s settles, and the same landing at
    /// 3 m/s bounces. The threshold gates the *estimated* approach speed (history, event, peak),
    /// because the backend's own reading understates a landing the solver has already absorbed.
    /// </summary>
    [Theory]
    [InlineData(1.5f, false)]
    [InlineData(3f, true)]
    public void ABounceNeedsTheOriginalsBounceThreshold(float speed, bool bounces)
    {
        EntityStore entities = new();
        GameplayHarness harness = new(entities, FarZonesConfig());
        EntityId pig = entities.Create();
        harness.Rules.AddRestitution(pig, restitution: 0.5f, mass: 1f);
        harness.Link(pig, new PhysicsBodyId(1));
        harness.Link(entities.Create(), new PhysicsBodyId(2));
        // The bodies are already at rest by the time the event arrives -- the solver absorbs a
        // landing before the contact is reported, which is exactly why the bounce asks for the
        // outgoing velocity instead of adding an impulse -- so the impact speed travels in the
        // event (and in the history this layer keeps).
        PhysicsVector3 contactNormal = new(0f, 1f, 0f);
        harness.IngestBody(new PhysicsBodyId(1), new PhysicsVector3(0, 1, 0), PhysicsVector3.Zero);
        harness.IngestBody(new PhysicsBodyId(2), new PhysicsVector3(0, 0, 0), PhysicsVector3.Zero);

        // `ContactStarted`: a bounce is the first touch of a pair (a persisted contact is already
        // inside the solver's response), which is what `ProcessEvents` routes to `ApplyBounce`.
        harness.Tick(1, new[] { PhysicsEvent.ContactStarted(new PhysicsBodyId(1), new PhysicsBodyId(2), contactNormal, speed) });

        if (bounces)
        {
            Assert.Contains(harness.Output.Commands, command => command.Kind == PhysicsCommandKind.SuppressContact);
            return;
        }

        Assert.Empty(harness.Output.Commands);
    }

    [Fact]
    public void BlasterFiresOnceAndPushesBodiesInsideItsRadius()
    {
        EntityStore entities = new();
        GameplayHarness harness = new(entities, FarZonesConfig());
        EntityId blaster = entities.Create();
        EntityId neighbour = entities.Create();
        harness.Rules.AddBlaster(blaster, radius: 3.5f, impulse: 30f, chainRadius: 0f);
        harness.Rules.AddActivation(blaster);
        harness.Link(blaster, new PhysicsBodyId(1));
        harness.Link(neighbour, new PhysicsBodyId(2));
        harness.IngestBody(new PhysicsBodyId(1), new PhysicsVector3(0, 0, 0), PhysicsVector3.Zero);
        harness.IngestBody(new PhysicsBodyId(2), new PhysicsVector3(2, 0, 0), PhysicsVector3.Zero);

        harness.Rules.SetActive(blaster, true);
        harness.Tick(1, Array.Empty<PhysicsEvent>());

        PhysicsCommand push = Assert.Single(harness.Output.Commands);
        Assert.Equal(new PhysicsBodyId(2), push.Body);
        Assert.True(push.Impulse.X > 0, "The shockwave pushes bodies away from the blaster.");
        Assert.Empty(harness.Output.DestroyedEntities);

        // Spent: the part survives but never fires again in the same run.
        harness.Rules.SetActive(blaster, true);
        harness.Tick(2, Array.Empty<PhysicsEvent>());
        Assert.Empty(harness.Output.Commands);
    }

    [Fact]
    public void BlasterChainFiresNeighbouringBlastersInTheSameTick()
    {
        EntityStore entities = new();
        GameplayHarness harness = new(entities, FarZonesConfig());
        EntityId first = entities.Create();
        EntityId second = entities.Create();
        EntityId target = entities.Create();
        harness.Rules.AddBlaster(first, radius: 3.5f, impulse: 30f, chainRadius: 8f);
        harness.Rules.AddBlaster(second, radius: 3.5f, impulse: 30f, chainRadius: 0f);
        harness.Rules.AddActivation(first);
        harness.Rules.AddActivation(second);
        harness.Link(first, new PhysicsBodyId(1));
        harness.Link(second, new PhysicsBodyId(2));
        harness.Link(target, new PhysicsBodyId(3));
        harness.IngestBody(new PhysicsBodyId(1), new PhysicsVector3(0, 0, 0), PhysicsVector3.Zero);
        harness.IngestBody(new PhysicsBodyId(2), new PhysicsVector3(5, 0, 0), PhysicsVector3.Zero);
        harness.IngestBody(new PhysicsBodyId(3), new PhysicsVector3(6, 0, 0), PhysicsVector3.Zero);

        harness.Rules.SetActive(first, true);
        harness.Tick(1, Array.Empty<PhysicsEvent>());

        PhysicsCommand push = Assert.Single(harness.Output.Commands);
        Assert.Equal(new PhysicsBodyId(3), push.Body);
        Assert.False(harness.Rules.IsPartActive(second), "The chained blaster's switch is consumed too.");
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
    public void DisabledObjectivesKeepPlayingWhenPigEntersGoalZone()
    {
        EntityStore entities = new();
        GameplayHarness harness = new(entities, new GameplayConfig(
            GoalZone: new GameplayZone(new PhysicsVector3(-9, 0, -2), new PhysicsVector3(-7, 4, 2)),
            MapBounds: new GameplayZone(new PhysicsVector3(-1000, -1000, -1000), new PhysicsVector3(1000, 1000, 1000)),
            TntBlastRadius: 4f,
            TntBlastImpulse: 12f,
            TntIgniteImpactSpeed: 5f,
            ObjectivesEnabled: false));
        EntityId pig = entities.Create();
        harness.Rules.AddPig(pig);
        harness.Link(pig, new PhysicsBodyId(1));
        harness.IngestBody(new PhysicsBodyId(1), new PhysicsVector3(-8, 1, 0), PhysicsVector3.Zero);

        harness.Tick(1, Array.Empty<PhysicsEvent>());

        Assert.Equal(GameplayPhase.Playing, harness.Rules.Phase);
    }

    [Fact]
    public void DisabledObjectivesKeepPlayingWhenPigLeavesMapBounds()
    {
        EntityStore entities = new();
        GameplayHarness harness = new(entities, new GameplayConfig(
            GoalZone: new GameplayZone(new PhysicsVector3(-9, 0, -2), new PhysicsVector3(-7, 4, 2)),
            MapBounds: new GameplayZone(new PhysicsVector3(-50, -10, -50), new PhysicsVector3(50, 50, 50)),
            TntBlastRadius: 4f,
            TntBlastImpulse: 12f,
            TntIgniteImpactSpeed: 5f,
            ObjectivesEnabled: false));
        EntityId pig = entities.Create();
        harness.Rules.AddPig(pig);
        harness.Link(pig, new PhysicsBodyId(1));
        harness.IngestBody(new PhysicsBodyId(1), new PhysicsVector3(60, 1, 0), PhysicsVector3.Zero);

        harness.Tick(1, Array.Empty<PhysicsEvent>());

        Assert.Equal(GameplayPhase.Playing, harness.Rules.Phase);
        Assert.False(harness.Rules.RestartRequested);
    }

    [Fact]
    public void DisabledObjectivesKeepPlayingPastMaxTicks()
    {
        EntityStore entities = new();
        GameplayHarness harness = new(entities, new GameplayConfig(
            GoalZone: new GameplayZone(new PhysicsVector3(500, 500, 500), new PhysicsVector3(501, 501, 501)),
            MapBounds: new GameplayZone(new PhysicsVector3(-1000, -1000, -1000), new PhysicsVector3(1000, 1000, 1000)),
            TntBlastRadius: 4f,
            TntBlastImpulse: 12f,
            TntIgniteImpactSpeed: 5f,
            MaxTicks: 5,
            ObjectivesEnabled: false));

        harness.Tick(5, Array.Empty<PhysicsEvent>());

        Assert.Equal(GameplayPhase.Playing, harness.Rules.Phase);
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

    /// <summary>
    /// <c>Bellows.cs:103-104</c>: <c>num2 = 1 - (1 - num/0.5)^2</c>, sampled at the tick's own
    /// elapsed time inside the 0.5 s puff (30 ticks at 60 Hz).
    /// </summary>
    private static float BellowsRamp(uint elapsedTicks, float thrustPerTick)
    {
        float t = (float)elapsedTicks / 30f;
        return thrustPerTick * (1f - ((1f - t) * (1f - t)));
    }

    /// <summary>
    /// A plain rocket for the axis/point tests: no ignition phase, no ramp-down and no speed cap
    /// (the original's <c>Rocket</c> with an <c>m_ignitionTime</c> that only matters to the bottle
    /// family, which carries an <c>m_visualization</c>, and <c>m_maximumSpeed</c> 0 meaning "never
    /// limit" here because the tests measure the force itself).
    /// </summary>
    private static void AddPlainRocket(
        GameplayHarness harness,
        EntityId rocket,
        float thrustPerTick,
        float directionX,
        float directionY,
        ushort boostTicks,
        float maxSpeed = 0f,
        float explodeRadius = 0f,
        float explodeImpulse = 0f) =>
        harness.Rules.AddRocket(
            rocket,
            thrustPerTick,
            directionX,
            directionY,
            ignitionTicks: 0,
            boostTicks,
            endTicks: 0,
            maxSpeed,
            visualization: false,
            explodeRadius,
            explodeImpulse);

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
                new RocketStore(entities),
                new TntStore(entities),
                new BlasterStore(entities),
                new GlueStore(entities),
                new WheelStore(entities),
                new PigStore(entities),
                new EggStore(entities),
                new WingStore(entities),
                new TailStore(entities),
                new UmbrellaStore(entities),
                new GearboxStore(entities),
                new BellowsStore(entities),
                new DetacherStore(entities),
                new GrappleStore(entities),
                new ActivationStore(entities),
                new RestitutionStore(entities),
                new PowerStore(entities),
                _bodies,
                config);
        }


        public void Link(
            EntityId entity,
            PhysicsBodyId body,
            PhysicsVector3 localOffset = default,
            PhysicsQuaternion? localRotation = null,
            bool mirrored = false)
        {
            _bodies.Set(entity, new PhysicsBodyLink(body));
            Rules.LinkBody(entity, body, localOffset: localOffset, localRotation: localRotation, mirrored: mirrored);
        }

        public void IngestBody(
            PhysicsBodyId body,
            PhysicsVector3 position,
            PhysicsVector3 velocity,
            PhysicsQuaternion? rotation = null) =>
            _snapshots.Add(new PhysicsBodySnapshot(body, position, rotation ?? PhysicsQuaternion.Identity, velocity, PhysicsVector3.Zero));

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
                new RocketStore(_entities),
                new TntStore(_entities),
                new BlasterStore(_entities),
                new GlueStore(_entities),
                new WheelStore(_entities),
                new PigStore(_entities),
                new EggStore(_entities),
                new WingStore(_entities),
                new TailStore(_entities),
                new UmbrellaStore(_entities),
                new GearboxStore(_entities),
                new BellowsStore(_entities),
                new DetacherStore(_entities),
                new GrappleStore(_entities),
                new ActivationStore(_entities),
                new RestitutionStore(_entities),
                new PowerStore(_entities),
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
        "physics": { "maximumAngularSpeed": 7.0, "damping": { "linear": 0.2, "angular": 0.05 } },
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
    public void AMotorWheelTapersToItsTopSpeedAndStopsThere()
    {
        EntityStore entities = new();
        GameplayHarness harness = new(entities, FarZonesConfig());
        EntityId motor = entities.Create();
        harness.Rules.AddMotor(motor, 2f, 1f);
        harness.Link(motor, new PhysicsBodyId(1));

        // MotorWheel.cs:101-103 caps the drive at 15 * enginePowerFactor and :292-299 tapers the
        // force as sqrt(1 - |v| / max). Half the cap (no power data -> factor 1) still drives at
        // sqrt(0.5); the cap is symmetric, so the same holds driving backwards.
        harness.IngestBody(new PhysicsBodyId(1), new PhysicsVector3(0, 1, 0), new PhysicsVector3(-7.5f, 0f, 0f));
        harness.Tick(1, Array.Empty<PhysicsEvent>());
        PhysicsCommand command = Assert.Single(harness.Output.Commands);
        Assert.Equal(2f * MathF.Sqrt(0.5f), command.Impulse.X, 5);

        harness.IngestBody(new PhysicsBodyId(1), new PhysicsVector3(0, 1, 0), new PhysicsVector3(15f, 0f, 0f));
        harness.Tick(2, Array.Empty<PhysicsEvent>());
        Assert.Empty(harness.Output.Commands);
    }

    [Fact]
    public void AFanPushesAtItsOwnMountNotTheCompoundCentre()
    {
        EntityStore entities = new();
        GameplayHarness harness = new(entities, FarZonesConfig());
        EntityId fan = entities.Create();
        harness.Rules.AddFan(fan, 2f, 1f, 0f);
        PhysicsBodyId body = new(1);
        // The member sits one cell to the right of the compound's centre of mass.
        harness.Link(fan, body, localOffset: new PhysicsVector3(1f, 0f, 0f));
        harness.IngestBody(body, new PhysicsVector3(0f, 1f, 0f), PhysicsVector3.Zero);

        harness.Tick(1, Array.Empty<PhysicsEvent>());

        // FanPropeller.cs:152 applies the force at transform.position + dir * 0.5 -- the part's
        // own transform (1, 1, 0), not the body centre (0, 1, 0) the body-centre path used.
        PhysicsCommand command = Assert.Single(harness.Output.Commands);
        Assert.Equal(1.5f, command.WorldPoint.X, 5);
        Assert.Equal(1f, command.WorldPoint.Y, 5);
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
    public void AFanLosesThrustPastItsTopSpeed()
    {
        EntityStore entities = new();
        GameplayHarness harness = new(entities, FarZonesConfig());
        EntityId fan = entities.Create();
        harness.Rules.AddFan(fan, 2f, 1f, 0f, maxSpeed: 10f);
        harness.Link(fan, new PhysicsBodyId(1));
        harness.IngestBody(new PhysicsBodyId(1), new PhysicsVector3(0, 1, 0), new PhysicsVector3(20f, 0f, 0f));

        harness.Tick(1, Array.Empty<PhysicsEvent>());

        // FanPropeller.cs:245-257: past the cap the force is divided by (1 + v.dir - maximumSpeed).
        PhysicsCommand command = Assert.Single(harness.Output.Commands);
        Assert.Equal(2f / (1f + 20f - 10f), command.Impulse.X, 5);
        Assert.Equal(0f, command.Impulse.Y);
    }

    [Fact]
    public void AFanPushesAlongTheRotationItWasBuiltWith()
    {
        // FanPropeller.cs:155 reads the axis off the part's own transform,
        // `transform.TransformDirection(GetDirectionVector(m_forceDirection))`, and the build
        // rotation is a z rotation (`Contraption.SetRotation` -> `BasePart.Rotate`): a fan built a
        // quarter turn round turns its `Left` content axis into `Down` ((Left + Deg_90) % 4 ==
        // Down). Applying the content direction in world space ignores how the player aimed it.
        EntityStore entities = new();
        GameplayHarness harness = new(entities, FarZonesConfig());
        EntityId fan = entities.Create();
        harness.Rules.AddFan(fan, 2f, -1f, 0f);
        harness.Link(fan, new PhysicsBodyId(1), localRotation: PhysicsQuaternion.FromZAngle(MathF.PI / 2f));
        harness.IngestBody(new PhysicsBodyId(1), new PhysicsVector3(0, 1, 0), PhysicsVector3.Zero);

        harness.Tick(1, Array.Empty<PhysicsEvent>());

        PhysicsCommand command = Assert.Single(harness.Output.Commands);
        Assert.Equal(0f, command.Impulse.X, 5);
        Assert.Equal(-2f, command.Impulse.Y, 5);
    }

    [Fact]
    public void AFanFollowsTheTiltOfTheBodyItIsWeldedTo()
    {
        // The same `TransformDirection`: a part welded into a rig that has rolled carries the
        // roll, so its thrust rolls with it. An axis stated in world space cannot.
        EntityStore entities = new();
        GameplayHarness harness = new(entities, FarZonesConfig());
        EntityId fan = entities.Create();
        harness.Rules.AddFan(fan, 2f, -1f, 0f);
        harness.Link(fan, new PhysicsBodyId(1));
        harness.IngestBody(
            new PhysicsBodyId(1),
            new PhysicsVector3(0, 1, 0),
            PhysicsVector3.Zero,
            PhysicsQuaternion.FromZAngle(MathF.PI / 2f));

        harness.Tick(1, Array.Empty<PhysicsEvent>());

        PhysicsCommand command = Assert.Single(harness.Output.Commands);
        Assert.Equal(0f, command.Impulse.X, 5);
        Assert.Equal(-2f, command.Impulse.Y, 5);
    }

    [Fact]
    public void AnUncappedFanKeepsPushingAtAnySpeed()
    {
        EntityStore entities = new();
        GameplayHarness harness = new(entities, FarZonesConfig());
        EntityId propeller = entities.Create();
        // The plane propeller's original never caps its speed (`PropellerSpeed` is Infinity), so
        // its content omits maxSpeed and no velocity ever decays the thrust. 0.616667 is the value
        // tools/bple-fans derives for it (37 N / 60) and content parts 38/135-143 carry.
        harness.Rules.AddFan(propeller, 0.616667f, 1f, 0f);
        harness.Link(propeller, new PhysicsBodyId(1));
        harness.IngestBody(new PhysicsBodyId(1), new PhysicsVector3(0, 1, 0), new PhysicsVector3(40f, 0f, 0f));

        harness.Tick(1, Array.Empty<PhysicsEvent>());

        PhysicsCommand command = Assert.Single(harness.Output.Commands);
        Assert.Equal(0.616667f, command.Impulse.X, 5);
    }

    [Fact]
    public void ARotorBrakesOnceItMovesPastItsTopSpeed()
    {
        EntityStore entities = new();
        GameplayHarness harness = new(entities, FarZonesConfig());
        EntityId rotor = entities.Create();
        harness.Rules.AddFan(rotor, 10f, 0f, 1f, maxSpeed: 10f, isRotor: true);
        harness.Link(rotor, new PhysicsBodyId(1));
        harness.IngestBody(new PhysicsBodyId(1), new PhysicsVector3(0, 1, 0), new PhysicsVector3(0f, 20f, 0f));

        harness.Tick(1, Array.Empty<PhysicsEvent>());

        // The decayed thrust (FanPropeller.cs:163) plus the rotor's -4 * excess^2 * v-hat brake
        // (FanPropeller.cs:198-207), both stated per second and converted by the 60 Hz tick.
        PhysicsCommand command = Assert.Single(harness.Output.Commands);
        float thrust = 10f / (1f + 20f - 10f);
        float brake = 4f * 10f * 10f / 60f;
        Assert.Equal(thrust - brake, command.Impulse.Y, 4);
    }

    [Fact]
    public void AFanSwitchStopsTheThrustWithoutDestroyingThePart()
    {
        EntityStore entities = new();
        GameplayHarness harness = new(entities, FarZonesConfig());
        EntityId fan = entities.Create();
        harness.Rules.AddFan(fan, 2f, 1f, 0f, maxSpeed: 10f);
        harness.Rules.AddActivation(fan);
        harness.Link(fan, new PhysicsBodyId(1));
        harness.IngestBody(new PhysicsBodyId(1), new PhysicsVector3(0, 1, 0), PhysicsVector3.Zero);

        // A switch starts off, and off is only "not thrusting": the original's FanPropeller stops
        // its motor and never destroys the part (FanPropeller.cs:49-56,265-296). The rotor's old
        // balloon-style trigger destroyed it on the first press.
        harness.Tick(1, Array.Empty<PhysicsEvent>());
        Assert.Empty(harness.Output.Commands);
        Assert.Empty(harness.Output.DestroyedEntities);
        Assert.True(harness.Rules.HasSwitch(fan));

        harness.Rules.SetActive(fan, active: true);
        harness.Tick(2, Array.Empty<PhysicsEvent>());
        Assert.Single(harness.Output.Commands);
        Assert.Empty(harness.Output.DestroyedEntities);
    }

    [Fact]
    public void ARocketBurnsThenGoesQuietWhereItStands()
    {
        EntityStore entities = new();
        GameplayHarness harness = new(entities, FarZonesConfig());
        EntityId rocket = entities.Create();
        AddPlainRocket(harness, rocket, 4f, 1f, 0f, boostTicks: 2);
        harness.Link(rocket, new PhysicsBodyId(1));
        harness.IngestBody(new PhysicsBodyId(1), new PhysicsVector3(0, 1, 0), PhysicsVector3.Zero);

        harness.Tick(1, Array.Empty<PhysicsEvent>());
        PhysicsCommand command = Assert.Single(harness.Output.Commands);
        Assert.Equal(new PhysicsBodyId(1), command.Body);
        Assert.Equal(4f, command.Impulse.X);

        harness.Tick(2, Array.Empty<PhysicsEvent>());
        Assert.Single(harness.Output.Commands);

        // Burn exhausted: the original only stops the thrust (`m_enabled = false`, Rocket.cs:281-284)
        // and `m_boostUsed` keeps a one-shot from re-igniting (:570-581) -- the part stays in the
        // world, a spent husk, and is NOT destroyed.
        harness.Tick(3, Array.Empty<PhysicsEvent>());
        Assert.Empty(harness.Output.Commands);
        Assert.Empty(harness.Output.DestroyedEntities);

        harness.Tick(4, Array.Empty<PhysicsEvent>());
        Assert.Empty(harness.Output.Commands);
    }

    [Fact]
    public void ARocketBurnsInTheOriginalsThreePhases()
    {
        // Rocket.cs:228-300: m_ignitionTime (a no-thrust phase for a part that carries an
        // `m_visualization` -- the bottle family), m_boostDuration of full thrust, then
        // m_boostEndDuration of the linear ramp `1 - (num - ignition - boost) / end`.
        EntityStore entities = new();
        GameplayHarness harness = new(entities, FarZonesConfig());
        EntityId rocket = entities.Create();
        harness.Rules.AddRocket(
            rocket,
            thrustPerTick: 6f,
            directionX: 1f,
            directionY: 0f,
            ignitionTicks: 2,
            boostTicks: 3,
            endTicks: 2,
            maxSpeed: 0f,
            visualization: true);
        harness.Link(rocket, new PhysicsBodyId(1));
        harness.IngestBody(new PhysicsBodyId(1), new PhysicsVector3(0, 1, 0), PhysicsVector3.Zero);

        // The ignition tick and the one after it: the bottle wobbles but does not push.
        harness.Tick(1, Array.Empty<PhysicsEvent>());
        Assert.Empty(harness.Output.Commands);
        harness.Tick(2, Array.Empty<PhysicsEvent>());
        Assert.Empty(harness.Output.Commands);

        // The boost phase: four ticks at full thrust (with `end > 0` the ramp's first tick is the
        // last boost tick, exactly the continuous formula sampled at 60 Hz).
        for (uint tick = 3; tick <= 6; tick++)
        {
            harness.Tick(tick, Array.Empty<PhysicsEvent>());
            Assert.Equal(6f, Assert.Single(harness.Output.Commands).Impulse.X, 4);
        }

        // The ramp: half thrust, then the burn is spent.
        harness.Tick(7, Array.Empty<PhysicsEvent>());
        Assert.Equal(3f, Assert.Single(harness.Output.Commands).Impulse.X, 4);
        harness.Tick(8, Array.Empty<PhysicsEvent>());
        Assert.Empty(harness.Output.Commands);
        Assert.Empty(harness.Output.DestroyedEntities);
    }

    [Fact]
    public void APlainRocketPushesDuringItsIgnitionPhaseToo()
    {
        // The ignition phase is silent only because the bottle family's `m_visualization` makes the
        // original return early (Rocket.cs:236-240); a plain rocket has none and thrusts from its
        // first tick.
        EntityStore entities = new();
        GameplayHarness harness = new(entities, FarZonesConfig());
        EntityId rocket = entities.Create();
        harness.Rules.AddRocket(rocket, 5f, 1f, 0f, ignitionTicks: 2, boostTicks: 2, endTicks: 1, maxSpeed: 0f, visualization: false);
        harness.Link(rocket, new PhysicsBodyId(1));
        harness.IngestBody(new PhysicsBodyId(1), new PhysicsVector3(0, 1, 0), PhysicsVector3.Zero);

        harness.Tick(1, Array.Empty<PhysicsEvent>());
        Assert.Equal(5f, Assert.Single(harness.Output.Commands).Impulse.X, 4);
        harness.Tick(2, Array.Empty<PhysicsEvent>());
        Assert.Equal(5f, Assert.Single(harness.Output.Commands).Impulse.X, 4);
    }

    [Fact]
    public void ARocketTapersOffPastItsMaximumSpeed()
    {
        // `LimitForceForSpeed` (Rocket.cs:529-541): the part's own speed along the thrust axis past
        // `m_maximumSpeed` divides the force by `1 + v - maxSpeed`.
        EntityStore entities = new();
        GameplayHarness harness = new(entities, FarZonesConfig());
        EntityId rocket = entities.Create();
        AddPlainRocket(harness, rocket, 11f, 1f, 0f, boostTicks: 4, maxSpeed: 10f);
        harness.Link(rocket, new PhysicsBodyId(1));
        harness.IngestBody(new PhysicsBodyId(1), new PhysicsVector3(0, 1, 0), new PhysicsVector3(20f, 0, 0));

        harness.Tick(1, Array.Empty<PhysicsEvent>());
        Assert.Equal(1f, Assert.Single(harness.Output.Commands).Impulse.X, 4); // 11 / (1 + 20 - 10)

        // Under the cap the full thrust applies, and a velocity across the thrust axis is not the
        // one the limit measures.
        harness.IngestBody(new PhysicsBodyId(1), new PhysicsVector3(0, 1, 0), new PhysicsVector3(9f, 0, 0));
        harness.Tick(2, Array.Empty<PhysicsEvent>());
        Assert.Equal(11f, Assert.Single(harness.Output.Commands).Impulse.X, 4);

        harness.IngestBody(new PhysicsBodyId(1), new PhysicsVector3(0, 1, 0), new PhysicsVector3(0f, 40f, 0));
        harness.Tick(3, Array.Empty<PhysicsEvent>());
        Assert.Equal(11f, Assert.Single(harness.Output.Commands).Impulse.X, 4);
    }

    [Fact]
    public void ARocketWithAZeroLengthBurnNeverThrusts()
    {
        EntityStore entities = new();
        GameplayHarness harness = new(entities, FarZonesConfig());
        EntityId rocket = entities.Create();
        AddPlainRocket(harness, rocket, 2f, 1f, 0f, boostTicks: 0);
        harness.Link(rocket, new PhysicsBodyId(1));
        harness.IngestBody(new PhysicsBodyId(1), new PhysicsVector3(0, 1, 0), PhysicsVector3.Zero);

        harness.Tick(1, Array.Empty<PhysicsEvent>());
        Assert.Empty(harness.Output.Commands);
        Assert.Empty(harness.Output.DestroyedEntities);
    }

    [Fact]
    public void EggBreaksOnHardImpactAndRequestsReplay()
    {
        EntityStore entities = new();
        GameplayHarness harness = new(entities, FarZonesConfig());
        EntityId egg = entities.Create();
        harness.Rules.AddEgg(egg);
        harness.Link(egg, new PhysicsBodyId(1));
        harness.IngestBody(new PhysicsBodyId(1), new PhysicsVector3(0, 1, 0), new PhysicsVector3(0, 10, 0));

        // First tick just stores kinematics; a hard impact next tick breaks the egg.
        harness.Tick(1, Array.Empty<PhysicsEvent>());
        harness.IngestBody(new PhysicsBodyId(1), new PhysicsVector3(0, 0, 0), new PhysicsVector3(0, -9, 0));
        harness.Tick(2, new[] { PhysicsEvent.ContactStarted(new PhysicsBodyId(1), new PhysicsBodyId(2)) });

        Assert.Contains(egg, harness.Output.DestroyedEntities);
        Assert.True(harness.Rules.RestartRequested);
    }

    [Fact]
    public void EggSurvivesGentleContact()
    {
        EntityStore entities = new();
        GameplayHarness harness = new(entities, FarZonesConfig());
        EntityId egg = entities.Create();
        harness.Rules.AddEgg(egg);
        harness.Link(egg, new PhysicsBodyId(1));
        harness.IngestBody(new PhysicsBodyId(1), new PhysicsVector3(0, 1, 0), PhysicsVector3.Zero);

        harness.Tick(1, Array.Empty<PhysicsEvent>());
        harness.IngestBody(new PhysicsBodyId(1), new PhysicsVector3(0, 1, 0), new PhysicsVector3(0, -1, 0));
        harness.Tick(2, new[] { PhysicsEvent.ContactStarted(new PhysicsBodyId(1), new PhysicsBodyId(2)) });

        Assert.Empty(harness.Output.DestroyedEntities);
        Assert.False(harness.Rules.RestartRequested);
    }

    [Fact]
    public void RocketPushesAlongNormalizedPlanarDirection()
    {
        EntityStore entities = new();
        GameplayHarness harness = new(entities, FarZonesConfig());
        EntityId rocket = entities.Create();
        // 45-degree lift (1, 1) must be normalized and push up-right.
        AddPlainRocket(harness, rocket, 6f, 1f, 1f, boostTicks: 2);
        harness.Link(rocket, new PhysicsBodyId(1));
        harness.IngestBody(new PhysicsBodyId(1), new PhysicsVector3(0, 1, 0), PhysicsVector3.Zero);

        harness.Tick(1, Array.Empty<PhysicsEvent>());
        PhysicsCommand command = Assert.Single(harness.Output.Commands);
        Assert.Equal(new PhysicsBodyId(1), command.Body);
        float normalization = MathF.Sqrt(2f);
        Assert.Equal(6f / normalization, command.Impulse.X, 5);
        Assert.Equal(6f / normalization, command.Impulse.Y, 5);
        Assert.Equal(0f, command.Impulse.Z);
    }

    [Fact]
    public void ARocketTurnedAroundPushesTheOtherWay()
    {
        // Rocket.cs:298-300 hands `m_direction` to `transform.TransformDirection`, so the build
        // rotation (`Contraption.SetRotation`, a z rotation) aims the thrust: a rocket built a half
        // turn round pushes -x, not the +x its content direction names. Applying the content
        // direction as a world axis makes "turn the part round" do nothing.
        EntityStore entities = new();
        GameplayHarness harness = new(entities, FarZonesConfig());
        EntityId rocket = entities.Create();
        AddPlainRocket(harness, rocket, 4f, 1f, 0f, boostTicks: 2);
        harness.Link(rocket, new PhysicsBodyId(1), localRotation: PhysicsQuaternion.FromZAngle(MathF.PI));
        harness.IngestBody(new PhysicsBodyId(1), new PhysicsVector3(0, 1, 0), PhysicsVector3.Zero);

        harness.Tick(1, Array.Empty<PhysicsEvent>());

        PhysicsCommand command = Assert.Single(harness.Output.Commands);
        Assert.Equal(-4f, command.Impulse.X, 5);
        Assert.Equal(0f, command.Impulse.Y, 5);
    }

    [Fact]
    public void ARocketCarriesTheTiltOfTheBodyItIsWeldedTo()
    {
        // The same `TransformDirection`: a rocket welded into a rig that has rolled carries the
        // roll, so its thrust rolls with it. An axis stated in world space cannot.
        EntityStore entities = new();
        GameplayHarness harness = new(entities, FarZonesConfig());
        EntityId rocket = entities.Create();
        AddPlainRocket(harness, rocket, 4f, 1f, 0f, boostTicks: 2);
        harness.Link(rocket, new PhysicsBodyId(1));
        harness.IngestBody(
            new PhysicsBodyId(1),
            new PhysicsVector3(0, 1, 0),
            PhysicsVector3.Zero,
            PhysicsQuaternion.FromZAngle(MathF.PI / 2f));

        harness.Tick(1, Array.Empty<PhysicsEvent>());

        PhysicsCommand command = Assert.Single(harness.Output.Commands);
        Assert.Equal(0f, command.Impulse.X, 5);
        Assert.Equal(4f, command.Impulse.Y, 5);
    }

    [Fact]
    public void ARocketPushesAtItsOwnMountNotTheCompoundCentre()
    {
        EntityStore entities = new();
        GameplayHarness harness = new(entities, FarZonesConfig());
        EntityId rocket = entities.Create();
        AddPlainRocket(harness, rocket, 4f, 1f, 0f, boostTicks: 2);
        // The member sits one cell to the right of the compound's centre of mass.
        harness.Link(rocket, new PhysicsBodyId(1), localOffset: new PhysicsVector3(1f, 0f, 0f));
        harness.IngestBody(new PhysicsBodyId(1), new PhysicsVector3(0f, 1f, 0f), PhysicsVector3.Zero);

        harness.Tick(1, Array.Empty<PhysicsEvent>());

        // Rocket.cs:298-300 applies the force at the part's own transform
        // (`position = transform.position + zero * 0.5f`), not at the body centre.
        PhysicsCommand command = Assert.Single(harness.Output.Commands);
        Assert.Equal(1f, command.WorldPoint.X, 5);
        Assert.Equal(1f, command.WorldPoint.Y, 5);
    }

    [Fact]
    public void ARocketBlastsFromTheChargeNotTheCompoundCentre()
    {
        // Rocket.cs:627-634 overlaps the blast sphere at `transform.position`, so a charge welded
        // one cell to the right of a target measures the distance from there: a neighbour at
        // (0.2, 1) with the charge at (1, 1) is thrown toward -x.
        EntityStore entities = new();
        GameplayHarness harness = new(entities, FarZonesConfig());
        EntityId rocket = entities.Create();
        EntityId neighbour = entities.Create();
        AddPlainRocket(harness, rocket, 0f, 1f, 0f, boostTicks: 0, explodeRadius: 3f, explodeImpulse: 10f);
        harness.Link(rocket, new PhysicsBodyId(1), localOffset: new PhysicsVector3(1f, 0f, 0f));
        harness.Link(neighbour, new PhysicsBodyId(2));
        harness.IngestBody(new PhysicsBodyId(1), new PhysicsVector3(0f, 1f, 0f), PhysicsVector3.Zero);
        harness.IngestBody(new PhysicsBodyId(2), new PhysicsVector3(0.2f, 1f, 0f), PhysicsVector3.Zero);

        harness.Tick(1, Array.Empty<PhysicsEvent>());

        // Both bodies are in the blast: the original's OverlapSphere at the charge's own position
        // includes the charge's own collider (Rocket.cs:630), and the neighbour is thrown toward -x
        // because the sphere is centred on the charge at x = 1, not on the compound's centre.
        PhysicsCommand blast = Assert.Single(harness.Output.Commands, command => command.Body == new PhysicsBodyId(2));
        Assert.True(blast.Impulse.X < 0f, $"neighbour impulse {blast.Impulse}, all {string.Join(" | ", harness.Output.Commands)}");
        Assert.Contains(harness.Output.Commands, command => command.Body == new PhysicsBodyId(1));
        Assert.Empty(harness.Output.DestroyedEntities);
    }

    [Fact]
    public void ARocketWithMExplodesBlastsWhenItsBurnEndsAndStaysPut()
    {
        EntityStore entities = new();
        GameplayHarness harness = new(entities, FarZonesConfig());
        EntityId rocket = entities.Create();
        EntityId neighbour = entities.Create();
        AddPlainRocket(harness, rocket, 4f, 0f, 1f, boostTicks: 1, explodeRadius: 3f, explodeImpulse: 10f);
        harness.Link(rocket, new PhysicsBodyId(1));
        harness.Link(neighbour, new PhysicsBodyId(2));
        harness.IngestBody(new PhysicsBodyId(1), new PhysicsVector3(0, 1, 0), PhysicsVector3.Zero);
        harness.IngestBody(new PhysicsBodyId(2), new PhysicsVector3(1, 1, 0), PhysicsVector3.Zero);

        harness.Tick(1, Array.Empty<PhysicsEvent>());
        PhysicsCommand push = Assert.Single(harness.Output.Commands);
        Assert.Equal(new PhysicsBodyId(1), push.Body);

        // The burn is spent on the next tick: the charge blasts (its neighbour outward, itself too)
        // and stays in the world as a spent husk -- Rocket.cs:281-284 only stops the thrust.
        harness.Tick(2, Array.Empty<PhysicsEvent>());
        PhysicsCommand blast = Assert.Single(harness.Output.Commands, command => command.Body == new PhysicsBodyId(2));
        Assert.True(blast.Impulse.X > 0f);
        Assert.Empty(harness.Output.DestroyedEntities);

        harness.Tick(3, Array.Empty<PhysicsEvent>());
        Assert.Empty(harness.Output.Commands);
    }

    [Fact]
    public void RocketWithoutBlastRadiusBurnsOutQuietly()
    {
        EntityStore entities = new();
        GameplayHarness harness = new(entities, FarZonesConfig());
        EntityId rocket = entities.Create();
        AddPlainRocket(harness, rocket, 2f, 1f, 0f, boostTicks: 1);
        harness.Link(rocket, new PhysicsBodyId(1));
        harness.IngestBody(new PhysicsBodyId(1), new PhysicsVector3(0, 1, 0), PhysicsVector3.Zero);

        harness.Tick(1, Array.Empty<PhysicsEvent>());
        Assert.Single(harness.Output.Commands);
        harness.Tick(2, Array.Empty<PhysicsEvent>());
        Assert.Empty(harness.Output.Commands);
        Assert.Empty(harness.Output.DestroyedEntities);
    }

    /// <summary>
    /// The two response curves are the original's own tables (Wings.cs:76-88, Tail.cs:32-41),
    /// read through <c>ResponseCurve.Get</c>: piecewise linear between the listed knots and clamped
    /// to the first and last value outside them. The asymmetry -- a lift peak at 15 degrees, a stall
    /// by 22, a negative band on the other side -- is the whole aerodynamics.
    /// </summary>
    [Fact]
    public void TheAerodynamicResponseCurvesAreTheOriginalsTables()
    {
        Assert.Equal(1.75f, Aerodynamics.WingCoefficient(15f), 5);
        Assert.Equal(1.5f, Aerodynamics.WingCoefficient(10f), 5);
        Assert.Equal(0.75f, Aerodynamics.WingCoefficient(0f), 5);   // halfway from (-10, 0) to (10, 1.5)
        Assert.Equal(0.45f, Aerodynamics.WingCoefficient(20.5f), 5); // stalled: 0.8 at 19 falling to 0.1 at 22
        Assert.Equal(0f, Aerodynamics.WingCoefficient(-180f), 5);
        Assert.Equal(0f, Aerodynamics.WingCoefficient(500f), 5);     // clamped past the last knot

        Assert.Equal(1.5f, Aerodynamics.TailCoefficient(45f), 5);
        Assert.Equal(1f, Aerodynamics.TailCoefficient(10f), 5);
        Assert.Equal(-1.5f, Aerodynamics.TailCoefficient(135f), 5);
        Assert.Equal(0f, Aerodynamics.TailCoefficient(-300f), 5);
    }

    [Fact]
    public void AWingLiftsAlongItsOwnFrameAndIsClampedAtOneHundredNewtons()
    {
        EntityStore entities = new();
        GameplayHarness harness = new(entities, FarZonesConfig());
        EntityId wing = entities.Create();
        harness.Rules.AddWing(wing, liftConstant: 0.8f); // Part_WoodenWings_01_SET.prefab:154
        harness.Link(wing, new PhysicsBodyId(1));

        // Flying right with no angle of attack: the curve reads 0.75, so the force is
        // 0.8 * 10^2 * 0.75 = 60 N straight up (Wings.cs:111-116), one tick of which is 1 N*s.
        harness.IngestBody(new PhysicsBodyId(1), new PhysicsVector3(0, 1, 0), new PhysicsVector3(10, 0, 0));
        harness.Tick(1, Array.Empty<PhysicsEvent>());
        PhysicsCommand command = Assert.Single(harness.Output.Commands);
        Assert.Equal(new PhysicsBodyId(1), command.Body);
        Assert.Equal(0f, command.Impulse.X, 5);
        Assert.Equal(1f, command.Impulse.Y, 4); // 0.8 * 100 * 0.75 / 60
        Assert.Equal(0f, command.Impulse.Z, 5);

        // The original clamps a wing's force at 100 N (Wings.cs:115), so six times the speed does
        // not give thirty-six times the impulse.
        harness.IngestBody(new PhysicsBodyId(1), new PhysicsVector3(0, 1, 0), new PhysicsVector3(60, 0, 0));
        harness.Tick(2, Array.Empty<PhysicsEvent>());
        command = Assert.Single(harness.Output.Commands);
        Assert.Equal(100f / 60f, command.Impulse.Y, 4);

        // A parked wing has no angle of attack to read.
        harness.IngestBody(new PhysicsBodyId(1), new PhysicsVector3(0, 1, 0), PhysicsVector3.Zero);
        harness.Tick(3, Array.Empty<PhysicsEvent>());
        Assert.Empty(harness.Output.Commands);
    }

    /// <summary>
    /// The mirror is a real aerodynamic input, not a drawing flag: the original measures the angle
    /// of attack with an extra sign (<c>Wings.cs:111</c>) and turns the whole frame 180 degrees
    /// about the part's own up axis (ADR-030), so the same wing at the same velocity pushes the
    /// other way.
    /// </summary>
    [Fact]
    public void AMirroredWingAndTailPushTheOtherWay()
    {
        (float wingY, float tailY) = WingAndTailLift(mirrored: false);
        (float mirroredWingY, float mirroredTailY) = WingAndTailLift(mirrored: true);

        Assert.True(wingY < 0f, $"an unmirrored wing at 26.6 degrees of attack pushes down, was {wingY}");
        Assert.True(mirroredWingY > 0f, $"a mirrored wing pushes up, was {mirroredWingY}");
        Assert.True(tailY < 0f, $"an unmirrored tail pushes down, was {tailY}");
        Assert.True(mirroredTailY > 0f, $"a mirrored tail pushes up, was {mirroredTailY}");
    }

    /// <summary>
    /// The tail is trimmed, the wing is not: the original twists the tail's reference axis by
    /// <c>0.4 * (num2 - 30)</c> (Tail.cs:67) before it measures the angle of attack, so a tail
    /// flying straight with no pitch still reads a negative angle -- which is what makes the wooden
    /// tail (liftConstant 0.2) push *down* on a level glider while the wing's curve (0.75 at zero
    /// attack) pushes up.
    /// </summary>
    [Fact]
    public void ATailIsTrimmedByItsOwnTwistTerms()
    {
        EntityStore entities = new();
        GameplayHarness harness = new(entities, FarZonesConfig());
        EntityId tail = entities.Create();
        harness.Rules.AddTail(tail, liftConstant: 0.2f); // Part_WoodenTail_01_SET.prefab:97
        harness.Link(tail, new PhysicsBodyId(1));
        harness.IngestBody(new PhysicsBodyId(1), new PhysicsVector3(0, 1, 0), new PhysicsVector3(10, 0, 0));

        harness.Tick(1, Array.Empty<PhysicsEvent>());
        PhysicsCommand command = Assert.Single(harness.Output.Commands);
        Assert.True(command.Impulse.Y < 0f, $"a level tail pushes down, was {command.Impulse.Y}");
        Assert.Equal(0f, command.Impulse.X, 5);
    }

    /// <summary>One wing and one tail flying at the same sloped velocity, mirror or not.</summary>
    private (float WingY, float TailY) WingAndTailLift(bool mirrored)
    {
        EntityStore entities = new();
        GameplayHarness harness = new(entities, FarZonesConfig());
        EntityId wing = entities.Create();
        EntityId tail = entities.Create();
        harness.Rules.AddWing(wing, liftConstant: 0.8f);
        harness.Rules.AddTail(tail, liftConstant: 0.2f);

        // A mirrored part's whole frame is turned 180 degrees about its own up axis (ADR-030), and
        // the assembler publishes that pose as the body's rotation at spawn -- which is also how the
        // rules layer sees it here. The bit travels beside the pose because a live physics pose can
        // no longer be asked which handedness it was built with.
        PhysicsQuaternion buildPose = BuildPose.Rotation(yaw: 0f, mirrored);
        harness.Link(wing, new PhysicsBodyId(1), mirrored: mirrored);
        harness.Link(tail, new PhysicsBodyId(2), mirrored: mirrored);
        PhysicsVector3 velocity = new(10f, 5f, 0f);
        harness.IngestBody(new PhysicsBodyId(1), new PhysicsVector3(0, 1, 0), velocity, buildPose);
        harness.IngestBody(new PhysicsBodyId(2), new PhysicsVector3(0, 2, 0), velocity, buildPose);

        harness.Tick(1, Array.Empty<PhysicsEvent>());

        float wingY = 0f;
        float tailY = 0f;
        foreach (PhysicsCommand command in harness.Output.Commands)
        {
            if (command.Body == new PhysicsBodyId(1))
            {
                wingY = command.Impulse.Y;
            }
            else if (command.Body == new PhysicsBodyId(2))
            {
                tailY = command.Impulse.Y;
            }
        }

        return (wingY, tailY);
    }

    [Fact]
    public void UmbrellaSlowsOnlyDescendingBodies()
    {
        EntityStore entities = new();
        GameplayHarness harness = new(entities, FarZonesConfig());
        EntityId umbrella = entities.Create();
        harness.Rules.AddUmbrella(umbrella, dragCoef: 0.2f);
        harness.Link(umbrella, new PhysicsBodyId(1));
        harness.IngestBody(new PhysicsBodyId(1), new PhysicsVector3(0, 1, 0), new PhysicsVector3(0, -4, 0));

        harness.Tick(1, Array.Empty<PhysicsEvent>());
        PhysicsCommand command = Assert.Single(harness.Output.Commands);
        Assert.Equal(0.8f, command.Impulse.Y, 5); // -(-4) * 0.2 upward

        harness.IngestBody(new PhysicsBodyId(1), new PhysicsVector3(0, 1, 0), new PhysicsVector3(0, 4, 0));
        harness.Tick(2, Array.Empty<PhysicsEvent>());
        Assert.Empty(harness.Output.Commands); // rising bodies untouched
    }

    [Fact]
    public void GearboxReversesMotorDirectionOnSameBody()
    {
        EntityStore entities = new();
        GameplayHarness harness = new(entities, FarZonesConfig());
        EntityId motor = entities.Create();
        EntityId gearbox = entities.Create();
        harness.Rules.AddMotor(motor, 2f, 1f);
        harness.Rules.AddGearbox(gearbox);
        harness.Link(motor, new PhysicsBodyId(1));
        harness.Link(gearbox, new PhysicsBodyId(1));
        harness.IngestBody(new PhysicsBodyId(1), new PhysicsVector3(0, 1, 0), PhysicsVector3.Zero);

        harness.Tick(1, Array.Empty<PhysicsEvent>());
        PhysicsCommand command = Assert.Single(harness.Output.Commands);
        Assert.Equal(-2f, command.Impulse.X, 5); // reversed
    }

    /// <summary>
    /// The puff is a 0.5 s ramp (Bellows.cs:100-110): `num2 = 1 - (1 - num/0.5)^2` is zero on the
    /// frame the puff starts, 0.75 halfway and 1 at the end, so a bellows does not kick off with a
    /// full impulse the way the old single-impulse model did. Legacy content (no switch) re-arms
    /// on touchdown.
    /// </summary>
    [Fact]
    public void ALegacyBellowsRampsItsPuffUpFromTouchdown()
    {
        EntityStore entities = new();
        GameplayHarness harness = new(entities, FarZonesConfig());
        EntityId bellows = entities.Create();
        harness.Rules.AddBellows(bellows, thrustPerTick: 8f, directionX: 1f, directionY: 0f, inflateTicks: 18);
        harness.Link(bellows, new PhysicsBodyId(1));
        harness.IngestBody(new PhysicsBodyId(1), new PhysicsVector3(0, 1, 0), PhysicsVector3.Zero);

        // The touchdown starts the puff; its first frame is the ramp's zero.
        harness.Tick(1, Grounded());
        Assert.Empty(harness.Output.Commands);

        // Thirty frames, `num2 = 1 - (1 - elapsed/30)^2`: zero, then rising to a hair under the
        // force -- 8.0 N/s here, i.e. 0.5 N per tick was the old single-impulse value.
        for (uint tick = 2; tick <= 31; tick++)
        {
            harness.Tick(tick, Grounded());
            float expected = BellowsRamp(tick - 2, 8f);
            if (expected <= 0f)
            {
                Assert.Empty(harness.Output.Commands);
                continue;
            }

            Assert.Equal(expected, Assert.Single(harness.Output.Commands).Impulse.X, 4);
        }

        // Then the cycle waits (0.3 s plus the skin's inflate) while the rig is still grounded:
        // the bellows is silent until it is ready again at tick 67.
        for (uint tick = 32; tick < 67; tick++)
        {
            harness.Tick(tick, Grounded());
            Assert.Empty(harness.Output.Commands);
        }
    }

    /// <summary>One grounded contact, the shape a landing arrives in.</summary>
    private static PhysicsEvent[] Grounded() =>
        [PhysicsEvent.ContactPersisted(new PhysicsBodyId(1), new PhysicsBodyId(2))];

    [Fact]
    public void BellowsPuffsOnItsButtonAndAgainAfterItsOwnCycle()
    {
        EntityStore entities = new();
        GameplayHarness harness = new(entities, FarZonesConfig());
        EntityId bellows = entities.Create();
        harness.Rules.AddBellows(bellows, thrustPerTick: 8f, directionX: 1f, directionY: 0f, inflateTicks: 18);
        harness.Rules.AddActivation(bellows);
        harness.Link(bellows, new PhysicsBodyId(1));
        harness.IngestBody(new PhysicsBodyId(1), new PhysicsVector3(0, 1, 0), PhysicsVector3.Zero);

        // Grounded but unpressed: nothing fires.
        harness.Tick(1, Grounded());
        Assert.Empty(harness.Output.Commands);

        // The button is momentary: the press starts one puff (its first frame is the ramp's zero)
        // and is spent with it.
        harness.Rules.SetActive(bellows, true);
        harness.Tick(2, Array.Empty<PhysicsEvent>());
        Assert.Empty(harness.Output.Commands);
        Assert.False(harness.Rules.IsPartActive(bellows));

        for (uint tick = 3; tick <= 32; tick++)
        {
            harness.Tick(tick, Array.Empty<PhysicsEvent>());
            float expected = BellowsRamp(tick - 3, 8f);
            if (expected <= 0f)
            {
                Assert.Empty(harness.Output.Commands);
                continue;
            }

            Assert.Equal(expected, Assert.Single(harness.Output.Commands).Impulse.X, 4);
        }

        // The puff is over and the cycle still has 0.3 s of wait plus the inflate to run: a press
        // inside it is spent without puffing (so the bar button never sits latched on,
        // Bellows.cs:123-125) and nothing fires.
        harness.Rules.SetActive(bellows, true);
        for (uint tick = 33; tick < 68; tick++)
        {
            harness.Tick(tick, Array.Empty<PhysicsEvent>());
            Assert.Empty(harness.Output.Commands);
        }

        Assert.False(harness.Rules.IsPartActive(bellows));

        // The cycle is up at 2 + 66 ticks (30 + 18 + 18): the same button puffs again, and the new
        // puff ramps from zero the same way.
        harness.Rules.SetActive(bellows, true);
        harness.Tick(68, Array.Empty<PhysicsEvent>());
        Assert.Empty(harness.Output.Commands); // the start frame
        harness.Tick(69, Array.Empty<PhysicsEvent>());
        Assert.Empty(harness.Output.Commands); // `elapsed` 0: still the ramp's zero
        harness.Tick(70, Array.Empty<PhysicsEvent>());
        Assert.Equal(BellowsRamp(1, 8f), Assert.Single(harness.Output.Commands).Impulse.X, 4);
    }

    [Fact]
    public void DetacherRequestsSeparationOnHardImpact()
    {
        EntityStore entities = new();
        GameplayHarness harness = new(entities, FarZonesConfig());
        EntityId detacher = entities.Create();
        harness.Rules.AddDetacher(detacher);
        harness.Link(detacher, new PhysicsBodyId(1));
        harness.IngestBody(new PhysicsBodyId(1), new PhysicsVector3(0, 1, 0), new PhysicsVector3(0, 10, 0));

        harness.Tick(1, Array.Empty<PhysicsEvent>());
        harness.IngestBody(new PhysicsBodyId(1), new PhysicsVector3(0, 0, 0), new PhysicsVector3(0, -9, 0));
        harness.Tick(2, new[] { PhysicsEvent.ContactStarted(new PhysicsBodyId(1), new PhysicsBodyId(2)) });

        Assert.Contains(detacher, harness.Output.DetachedEntities);
        Assert.DoesNotContain(detacher, harness.Output.DestroyedEntities);
    }

    [Fact]
    public void DetacherStaysAttachedOnGentleContact()
    {
        EntityStore entities = new();
        GameplayHarness harness = new(entities, FarZonesConfig());
        EntityId detacher = entities.Create();
        harness.Rules.AddDetacher(detacher);
        harness.Link(detacher, new PhysicsBodyId(1));
        harness.IngestBody(new PhysicsBodyId(1), new PhysicsVector3(0, 1, 0), PhysicsVector3.Zero);

        harness.Tick(1, Array.Empty<PhysicsEvent>());
        harness.IngestBody(new PhysicsBodyId(1), new PhysicsVector3(0, 1, 0), new PhysicsVector3(0, -1, 0));
        harness.Tick(2, new[] { PhysicsEvent.ContactStarted(new PhysicsBodyId(1), new PhysicsBodyId(2)) });

        Assert.Empty(harness.Output.DetachedEntities);
    }

    [Fact]
    public void GrapplePullsOncePerTouchdownAlongNormalizedDirection()
    {
        EntityStore entities = new();
        GameplayHarness harness = new(entities, FarZonesConfig());
        EntityId hook = entities.Create();
        harness.Rules.AddGrapple(hook, 22f, 0.70710678f, 0.70710678f);
        harness.Link(hook, new PhysicsBodyId(1));
        harness.IngestBody(new PhysicsBodyId(1), new PhysicsVector3(0, 1, 0), PhysicsVector3.Zero);

        harness.Tick(1, new[] { PhysicsEvent.ContactPersisted(new PhysicsBodyId(1), new PhysicsBodyId(2)) });
        PhysicsCommand command = Assert.Single(harness.Output.Commands);
        float normalization = MathF.Sqrt(2f);
        Assert.Equal(22f / normalization, command.Impulse.X, 5);
        Assert.Equal(22f / normalization, command.Impulse.Y, 5);

        // Still grounded: no second pull until airborne.
        harness.Tick(2, new[] { PhysicsEvent.ContactPersisted(new PhysicsBodyId(1), new PhysicsBodyId(2)) });
        Assert.Empty(harness.Output.Commands);

        // Airborne resets; next touchdown fires again.
        harness.Tick(3, Array.Empty<PhysicsEvent>());
        harness.Tick(4, new[] { PhysicsEvent.ContactPersisted(new PhysicsBodyId(1), new PhysicsBodyId(2)) });
        Assert.Single(harness.Output.Commands);
    }

    [Fact]
    public void AGrappleTurnedAroundPullsTheOtherWay()
    {
        // GrapplingHook.cs:467 hands `m_direction` to `transform.TransformDirection`, so the build
        // rotation aims the pull: the hook's Right content axis built a quarter turn round becomes
        // Up ((Right + Deg_90) % 4 == Up).
        EntityStore entities = new();
        GameplayHarness harness = new(entities, FarZonesConfig());
        EntityId hook = entities.Create();
        harness.Rules.AddGrapple(hook, 22f, 1f, 0f);
        harness.Link(hook, new PhysicsBodyId(1), localRotation: PhysicsQuaternion.FromZAngle(MathF.PI / 2f));
        harness.IngestBody(new PhysicsBodyId(1), new PhysicsVector3(0, 1, 0), PhysicsVector3.Zero);

        harness.Tick(1, new[] { PhysicsEvent.ContactPersisted(new PhysicsBodyId(1), new PhysicsBodyId(2)) });

        PhysicsCommand command = Assert.Single(harness.Output.Commands);
        Assert.Equal(0f, command.Impulse.X, 5);
        Assert.Equal(22f, command.Impulse.Y, 5);
    }

    [Fact]
    public void AGrapplePullsAtItsOwnMountNotTheCompoundCentre()
    {
        EntityStore entities = new();
        GameplayHarness harness = new(entities, FarZonesConfig());
        EntityId hook = entities.Create();
        harness.Rules.AddGrapple(hook, 22f, 1f, 0f);
        harness.Link(hook, new PhysicsBodyId(1), localOffset: new PhysicsVector3(1f, 0f, 0f));
        harness.IngestBody(new PhysicsBodyId(1), new PhysicsVector3(0, 1, 0), PhysicsVector3.Zero);

        harness.Tick(1, new[] { PhysicsEvent.ContactPersisted(new PhysicsBodyId(1), new PhysicsBodyId(2)) });

        PhysicsCommand command = Assert.Single(harness.Output.Commands);
        Assert.Equal(1f, command.WorldPoint.X, 5);
        Assert.Equal(1f, command.WorldPoint.Y, 5);
    }

    [Fact]
    public void ToggleSwitchGatesMotorThrust()
    {
        EntityStore entities = new();
        GameplayHarness harness = new(entities, FarZonesConfig());
        EntityId motor = entities.Create();
        harness.Rules.AddMotor(motor, 2f, 1f);
        harness.Rules.AddActivation(motor);
        harness.Link(motor, new PhysicsBodyId(1));
        harness.IngestBody(new PhysicsBodyId(1), new PhysicsVector3(0, 1, 0), PhysicsVector3.Zero);

        harness.Tick(1, Array.Empty<PhysicsEvent>());
        Assert.Empty(harness.Output.Commands);

        harness.Rules.SetActive(motor, true);
        harness.Tick(2, Array.Empty<PhysicsEvent>());
        PhysicsCommand command = Assert.Single(harness.Output.Commands);
        Assert.Equal(2f, command.Impulse.X, 5);

        harness.Rules.SetActive(motor, false);
        harness.Tick(3, Array.Empty<PhysicsEvent>());
        Assert.Empty(harness.Output.Commands);
    }

    [Fact]
    public void GearboxReversesOnlyWhileItsSwitchIsOn()
    {
        EntityStore entities = new();
        GameplayHarness harness = new(entities, FarZonesConfig());
        EntityId motor = entities.Create();
        EntityId gearbox = entities.Create();
        harness.Rules.AddMotor(motor, 2f, 1f);
        harness.Rules.AddGearbox(gearbox);
        harness.Rules.AddActivation(gearbox);
        harness.Link(motor, new PhysicsBodyId(1));
        harness.Link(gearbox, new PhysicsBodyId(1));
        harness.IngestBody(new PhysicsBodyId(1), new PhysicsVector3(0, 1, 0), PhysicsVector3.Zero);

        harness.Tick(1, Array.Empty<PhysicsEvent>());
        PhysicsCommand forward = Assert.Single(harness.Output.Commands);
        Assert.Equal(2f, forward.Impulse.X, 5);

        harness.Rules.SetActive(gearbox, true);
        harness.Tick(2, Array.Empty<PhysicsEvent>());
        PhysicsCommand reverse = Assert.Single(harness.Output.Commands);
        Assert.Equal(-2f, reverse.Impulse.X, 5);
    }

    [Fact]
    public void BalloonPopsWhenItsSwitchFires()
    {
        EntityStore entities = new();
        GameplayHarness harness = new(entities, FarZonesConfig());
        EntityId balloon = entities.Create();
        harness.Rules.AddBalloon(balloon, 1.5f);
        harness.Rules.AddActivation(balloon);
        harness.Link(balloon, new PhysicsBodyId(1));
        harness.IngestBody(new PhysicsBodyId(1), new PhysicsVector3(0, 1, 0), PhysicsVector3.Zero);

        harness.Tick(1, Array.Empty<PhysicsEvent>());
        Assert.Single(harness.Output.Commands); // lift is passive

        harness.Rules.SetActive(balloon, true);
        harness.Tick(2, Array.Empty<PhysicsEvent>());
        Assert.Empty(harness.Output.Commands);
        Assert.Contains(balloon, harness.Output.DestroyedEntities);
    }

    [Fact]
    public void RocketWaitsForItsSwitch()
    {
        EntityStore entities = new();
        GameplayHarness harness = new(entities, FarZonesConfig());
        EntityId rocket = entities.Create();
        AddPlainRocket(harness, rocket, 4f, 1f, 0f, boostTicks: 2);
        harness.Rules.AddActivation(rocket);
        harness.Link(rocket, new PhysicsBodyId(1));
        harness.IngestBody(new PhysicsBodyId(1), new PhysicsVector3(0, 1, 0), PhysicsVector3.Zero);

        harness.Tick(1, Array.Empty<PhysicsEvent>());
        Assert.Empty(harness.Output.Commands);

        harness.Rules.SetActive(rocket, true);
        harness.Tick(2, Array.Empty<PhysicsEvent>());
        PhysicsCommand command = Assert.Single(harness.Output.Commands);
        Assert.Equal(4f, command.Impulse.X, 5);
    }

    [Fact]
    public void AButtonPressAPartsOwnGateRefusesIsStillSpent()
    {
        EntityStore entities = new();
        GameplayHarness harness = new(entities, FarZonesConfig());
        // Both of these are BasePropulsion parts: without a chassis neighbour they never fire
        // (BasePropulsion.cs:13-20). The press must be spent all the same -- otherwise the bar
        // shows a switch stuck on its "on" position, which is not a button any more.
        EntityId bellows = entities.Create();
        harness.Rules.AddBellows(bellows, thrustPerTick: 8f, directionX: 1f, directionY: 0f, inflateTicks: 18);
        harness.Rules.AddActivation(bellows);
        harness.Rules.SetChassisAnchored(bellows, anchored: false);
        harness.Link(bellows, new PhysicsBodyId(1));
        harness.IngestBody(new PhysicsBodyId(1), new PhysicsVector3(0, 1, 0), PhysicsVector3.Zero);

        EntityId rocket = entities.Create();
        AddPlainRocket(harness, rocket, 4f, 1f, 0f, boostTicks: 2);
        harness.Rules.AddActivation(rocket);
        harness.Rules.SetChassisAnchored(rocket, anchored: false);
        harness.Link(rocket, new PhysicsBodyId(2));
        harness.IngestBody(new PhysicsBodyId(2), new PhysicsVector3(0, 3, 0), PhysicsVector3.Zero);

        harness.Rules.SetActive(bellows, true);
        harness.Rules.SetActive(rocket, true);
        harness.Tick(1, Array.Empty<PhysicsEvent>());

        Assert.Empty(harness.Output.Commands);
        Assert.False(harness.Rules.IsPartActive(bellows));
        Assert.False(harness.Rules.IsPartActive(rocket));
    }

    [Fact]
    public void GrappleFiresOnItsSwitch()
    {
        EntityStore entities = new();
        GameplayHarness harness = new(entities, FarZonesConfig());
        EntityId hook = entities.Create();
        harness.Rules.AddGrapple(hook, 22f, 1f, 0f);
        harness.Rules.AddActivation(hook);
        harness.Link(hook, new PhysicsBodyId(1));
        harness.IngestBody(new PhysicsBodyId(1), new PhysicsVector3(0, 1, 0), PhysicsVector3.Zero);

        harness.Tick(1, new[] { PhysicsEvent.ContactPersisted(new PhysicsBodyId(1), new PhysicsBodyId(2)) });
        Assert.Empty(harness.Output.Commands);

        harness.Rules.SetActive(hook, true);
        harness.Tick(2, Array.Empty<PhysicsEvent>());
        PhysicsCommand command = Assert.Single(harness.Output.Commands);
        Assert.Equal(22f, command.Impulse.X, 5);
    }

    [Fact]
    public void DetacherFiresOnItsSwitch()
    {
        EntityStore entities = new();
        GameplayHarness harness = new(entities, FarZonesConfig());
        EntityId detacher = entities.Create();
        harness.Rules.AddDetacher(detacher);
        harness.Rules.AddActivation(detacher);
        harness.Link(detacher, new PhysicsBodyId(1));
        harness.IngestBody(new PhysicsBodyId(1), new PhysicsVector3(0, 1, 0), PhysicsVector3.Zero);

        harness.Tick(1, Array.Empty<PhysicsEvent>());
        Assert.Empty(harness.Output.DetachedEntities);

        harness.Rules.SetActive(detacher, true);
        harness.Tick(2, Array.Empty<PhysicsEvent>());
        Assert.Contains(detacher, harness.Output.DetachedEntities);
    }

    [Fact]
    public void SwitchedPartsReArmWhenRebuilding()
    {
        EntityStore entities = new();
        GameplayHarness harness = new(entities, FarZonesConfig());
        EntityId motor = entities.Create();
        harness.Rules.AddMotor(motor, 2f, 1f);
        harness.Rules.AddActivation(motor);
        harness.Link(motor, new PhysicsBodyId(1));
        harness.IngestBody(new PhysicsBodyId(1), new PhysicsVector3(0, 1, 0), PhysicsVector3.Zero);

        harness.Rules.SetActive(motor, true);
        harness.Rules.ResetForRebuild();
        harness.Tick(1, Array.Empty<PhysicsEvent>());
        Assert.Empty(harness.Output.Commands);
    }
}
