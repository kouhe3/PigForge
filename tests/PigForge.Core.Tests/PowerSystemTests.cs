using System.Runtime.InteropServices;
using PigForge.Core.Content;
using PigForge.Physics.Abstractions;

namespace PigForge.Core.Tests;

/// <summary>
/// Power system tests (spec docs/specs/power-system.md): the original's engine supplies its
/// component and applies no force (Engine.cs:29,138), a consumer's drive is scaled by the
/// component's power factor (Contraption.cs:540-556, MotorWheel.cs:101-109), and an engine that is
/// not enclosed supplies nothing (Engine.cs:61 ValidatePart).
/// </summary>
public sealed class PowerSystemTests
{
    // The original's serialized prefab values, used as the fixture: an engine supplies 150
    // (Part_Engine_01_SET.m_enginePower), a motor wheel consumes 100 (Part_MotorWheel_01_SET).
    private const float EnginePower = 150f;
    private const float WheelConsumption = 100f;
    private const float WheelImpulse = 2.2f;

    [Fact]
    public void PowerFactorFollowsTheOriginalFormulaIncludingCapAndBothExponents()
    {
        // Consumption at or below 1 with an engine present is exactly 1 (Contraption.cs:547-549).
        Assert.Equal(1f, GameplayRules.ComputePowerFactor(EnginePower, 1f));
        Assert.Equal(1f, GameplayRules.ComputePowerFactor(EnginePower, 0.5f));
        // 150 / 100 = 1.5 > 1 -> the 0.585 branch.
        Assert.Equal(1.2676f, GameplayRules.ComputePowerFactor(EnginePower, WheelConsumption), 3);
        // 25 / 100 = 0.25 <= 1 -> the 0.75 branch.
        Assert.Equal(0.353553f, GameplayRules.ComputePowerFactor(25f, WheelConsumption), 5);
        // 25 / 100... a bigger ratio on the 0.585 branch: 2500 / 100 = 25.
        Assert.Equal(6.5730f, GameplayRules.ComputePowerFactor(2500f, WheelConsumption), 3);

        // The raw ratio is capped at 10 * EnginePowerLimit = 40 (INSettingsBExp EnginePowerLimit
        // 4.0, Contraption.cs:545): 40 is the largest raw value, and everything above it is equal.
        float capped = 8.6539f;
        Assert.Equal(capped, GameplayRules.ComputePowerFactor(4000f, WheelConsumption), 3);
        Assert.Equal(capped, GameplayRules.ComputePowerFactor(5000f, WheelConsumption), 3);
        Assert.Equal(capped, GameplayRules.ComputePowerFactor(1_000_000f, WheelConsumption), 3);

        // No engine is 0 on both branches; an engine with nothing to drive is 1.
        Assert.Equal(0f, GameplayRules.ComputePowerFactor(0f, WheelConsumption));
        Assert.Equal(0f, GameplayRules.ComputePowerFactor(0f, 0f));
        Assert.Equal(0f, GameplayRules.ComputePowerFactor(0f, 1f));
        Assert.Equal(1f, GameplayRules.ComputePowerFactor(EnginePower, 0f));
    }

    [Fact]
    public void PoweredWheelWithoutAnEngineDoesNotDrive()
    {
        EntityStore entities = new();
        PowerHarness harness = new(entities);
        EntityId wheel = entities.Create();
        harness.Rules.AddMotor(wheel, WheelImpulse, 1f);
        harness.Rules.AddWheel(wheel);
        harness.Rules.AddPower(wheel, WheelConsumption, 0f);
        harness.Link(wheel, new PhysicsBodyId(1));
        harness.IngestBody(new PhysicsBodyId(1));

        Assert.Equal(0f, harness.Rules.ClusterPowerFactor(wheel));

        harness.Tick(1, new[] { PhysicsEvent.ContactPersisted(new PhysicsBodyId(1), new PhysicsBodyId(2)) });

        Assert.Empty(harness.Output.Commands);
    }

    [Fact]
    public void EnclosedEnginePowersAConsumerInItsCluster()
    {
        EntityStore entities = new();
        PowerHarness harness = new(entities);
        EntityId engine = entities.Create();
        EntityId wheel = entities.Create();
        harness.Rules.AddMotor(wheel, WheelImpulse, 1f);
        harness.Rules.AddWheel(wheel);
        harness.Rules.AddPower(engine, 0f, EnginePower);
        harness.Rules.AddPower(wheel, WheelConsumption, 0f);
        harness.Rules.SetEngineEnclosed(engine, enclosed: true);
        harness.Link(engine, new PhysicsBodyId(1));
        harness.Link(wheel, new PhysicsBodyId(1));
        harness.IngestBody(new PhysicsBodyId(1));

        Assert.Equal(1.2676f, harness.Rules.ClusterPowerFactor(wheel), 3);

        harness.Tick(1, new[] { PhysicsEvent.ContactPersisted(new PhysicsBodyId(1), new PhysicsBodyId(2)) });

        PhysicsCommand command = Assert.Single(harness.Output.Commands);
        Assert.Equal(WheelImpulse * 1.2676f, command.Impulse.X, 3);
    }

    [Fact]
    public void UnenclosedEngineSuppliesNothing()
    {
        EntityStore entities = new();
        PowerHarness harness = new(entities);
        EntityId engine = entities.Create();
        EntityId wheel = entities.Create();
        harness.Rules.AddMotor(wheel, WheelImpulse, 1f);
        harness.Rules.AddWheel(wheel);
        harness.Rules.AddPower(engine, 0f, EnginePower);
        harness.Rules.AddPower(wheel, WheelConsumption, 0f);
        harness.Link(engine, new PhysicsBodyId(1));
        harness.Link(wheel, new PhysicsBodyId(1));
        harness.IngestBody(new PhysicsBodyId(1));

        // Engine.ValidatePart() => m_enclosedInto != null (Engine.cs:61): the engine sits on its
        // own cluster, so it supplies nothing and the wheel stays put.
        Assert.Equal(0f, harness.Rules.ClusterPowerFactor(wheel));
        harness.Tick(1, new[] { PhysicsEvent.ContactPersisted(new PhysicsBodyId(1), new PhysicsBodyId(2)) });
        Assert.Empty(harness.Output.Commands);

        harness.Rules.SetEngineEnclosed(engine, enclosed: true);

        Assert.Equal(1.2676f, harness.Rules.ClusterPowerFactor(wheel), 3);
        harness.Tick(2, new[] { PhysicsEvent.ContactPersisted(new PhysicsBodyId(1), new PhysicsBodyId(2)) });
        PhysicsCommand command = Assert.Single(harness.Output.Commands);
        Assert.Equal(WheelImpulse * 1.2676f, command.Impulse.X, 3);
    }

    [Fact]
    public void TogglingAConsumerSwitchChangesTheClusterConsumption()
    {
        EntityStore entities = new();
        PowerHarness harness = new(entities);
        EntityId engine = entities.Create();
        EntityId first = entities.Create();
        EntityId second = entities.Create();
        harness.Rules.AddMotor(first, 2f, 1f);
        harness.Rules.AddMotor(second, 2f, 1f);
        harness.Rules.AddWheel(first);
        harness.Rules.AddWheel(second);
        harness.Rules.AddActivation(first);
        harness.Rules.AddActivation(second);
        harness.Rules.AddPower(engine, 0f, EnginePower);
        harness.Rules.AddPower(first, WheelConsumption, 0f);
        harness.Rules.AddPower(second, WheelConsumption, 0f);
        harness.Rules.SetEngineEnclosed(engine, enclosed: true);
        harness.Link(engine, new PhysicsBodyId(1));
        harness.Link(first, new PhysicsBodyId(1));
        harness.Link(second, new PhysicsBodyId(1));
        harness.IngestBody(new PhysicsBodyId(1));

        // Both switches off: nothing consumes, so the cluster's factor is 1 but no command is
        // emitted (Contraption.cs:2633-2644 sums only the enabled consumers).
        harness.Tick(1, new[] { PhysicsEvent.ContactPersisted(new PhysicsBodyId(1), new PhysicsBodyId(2)) });
        Assert.Empty(harness.Output.Commands);

        // One wheel on: 150 / 100 = 1.5 -> 1.5^0.585.
        harness.Rules.SetActive(first, true);
        Assert.Equal(1.2676f, harness.Rules.ClusterPowerFactor(first), 3);
        harness.Tick(2, new[] { PhysicsEvent.ContactPersisted(new PhysicsBodyId(1), new PhysicsBodyId(2)) });
        PhysicsCommand single = Assert.Single(harness.Output.Commands);
        Assert.Equal(2f * 1.2676f, single.Impulse.X, 3);

        // Both on: 150 / 200 = 0.75 -> 0.75^0.75, both wheels drive weaker.
        harness.Rules.SetActive(second, true);
        Assert.Equal(0.80593f, harness.Rules.ClusterPowerFactor(first), 4);
        harness.Tick(3, new[] { PhysicsEvent.ContactPersisted(new PhysicsBodyId(1), new PhysicsBodyId(2)) });
        Assert.Equal(2, harness.Output.Commands.Count);
        Assert.All(harness.Output.Commands, command => Assert.Equal(2f * 0.80593f, command.Impulse.X, 3));

        // Switching one off restores the stronger factor for the survivor, and the switched-off
        // wheel stops driving entirely (its own motor is gated by its switch).
        harness.Rules.SetActive(second, false);
        Assert.Equal(1.2676f, harness.Rules.ClusterPowerFactor(first), 3);
        harness.Tick(4, new[] { PhysicsEvent.ContactPersisted(new PhysicsBodyId(1), new PhysicsBodyId(2)) });
        PhysicsCommand survivor = Assert.Single(harness.Output.Commands);
        Assert.Equal(new PhysicsBodyId(1), survivor.Body);
        Assert.Equal(2f * 1.2676f, survivor.Impulse.X, 3);
    }

    [Fact]
    public void EnginePartNeverDrivesItsOwnBody()
    {
        EntityStore entities = new();
        PowerHarness harness = new(entities);
        EntityId engine = entities.Create();
        harness.Rules.AddMotor(engine, 2f, 1f);
        harness.Rules.AddPower(engine, 0f, EnginePower);
        harness.Rules.SetEngineEnclosed(engine, enclosed: true);
        harness.Link(engine, new PhysicsBodyId(1));
        harness.IngestBody(new PhysicsBodyId(1));

        // The engine's own cluster has engine power and no consumption: factor 1 (raw = 1), and
        // the engine still pushes nothing (Engine.cs:29,138 -- it has no motor force at all).
        Assert.Equal(1f, harness.Rules.ClusterPowerFactor(engine));

        harness.Tick(1, Array.Empty<PhysicsEvent>());
        harness.Tick(2, new[] { PhysicsEvent.ContactPersisted(new PhysicsBodyId(1), new PhysicsBodyId(2)) });

        Assert.Empty(harness.Output.Commands);
    }

    [Fact]
    public void HingedWheelSharesTheChassisClusterAcrossBodies()
    {
        EntityStore entities = new();
        PowerHarness harness = new(entities);
        EntityId engine = entities.Create();
        EntityId wheel = entities.Create();
        harness.Rules.AddMotor(wheel, WheelImpulse, 1f);
        harness.Rules.AddWheel(wheel);
        harness.Rules.AddPower(engine, 0f, EnginePower);
        harness.Rules.AddPower(wheel, WheelConsumption, 0f);
        harness.Rules.SetEngineEnclosed(engine, enclosed: true);
        harness.Link(engine, new PhysicsBodyId(1));
        harness.Link(wheel, new PhysicsBodyId(2));
        harness.IngestBody(new PhysicsBodyId(1));
        harness.IngestBody(new PhysicsBodyId(2));

        // A wheel keeps its own body (ADR-009), so without the assembled hinge the chassis's
        // engine does not reach it.
        Assert.Equal(0f, harness.Rules.ClusterPowerFactor(wheel));
        harness.Tick(1, new[] { PhysicsEvent.ContactPersisted(new PhysicsBodyId(2), new PhysicsBodyId(3)) });
        Assert.Empty(harness.Output.Commands);

        // The assembler hinges the wheel to the chassis: one cluster, so one factor.
        harness.Rules.LinkPowerCluster(wheel, engine);

        Assert.Equal(1.2676f, harness.Rules.ClusterPowerFactor(wheel), 3);
        Assert.Equal(1.2676f, harness.Rules.ClusterPowerFactor(engine), 3);
        harness.Tick(2, new[] { PhysicsEvent.ContactPersisted(new PhysicsBodyId(2), new PhysicsBodyId(3)) });
        PhysicsCommand command = Assert.Single(harness.Output.Commands);
        Assert.Equal(new PhysicsBodyId(2), command.Body);
        Assert.Equal(WheelImpulse * 1.2676f, command.Impulse.X, 3);
    }

    [Fact]
    public void TwoIdenticalRunsEmitIdenticalImpulsesFactorsAndHash()
    {
        (float First, float Second, long PowerHash, long ActivationHash) first = RunScriptedCart();
        (float First, float Second, long PowerHash, long ActivationHash) second = RunScriptedCart();

        Assert.Equal(first.First, second.First);
        Assert.Equal(first.Second, second.Second);
        Assert.Equal(first.PowerHash, second.PowerHash);
        Assert.Equal(first.ActivationHash, second.ActivationHash);
        Assert.NotEqual(0, first.PowerHash);
    }

    /// <summary>Builds a cart, toggles both wheels on and drives for a few ticks, returning the
    /// impulses of the last tick plus the rules-layer hashes.</summary>
    private static (float First, float Second, long PowerHash, long ActivationHash) RunScriptedCart()
    {
        EntityStore entities = new();
        PowerHarness harness = new(entities);
        EntityId engine = entities.Create();
        EntityId wheel = entities.Create();
        harness.Rules.AddMotor(wheel, WheelImpulse, 1f);
        harness.Rules.AddWheel(wheel);
        harness.Rules.AddActivation(wheel);
        harness.Rules.AddPower(engine, 0f, EnginePower);
        harness.Rules.AddPower(wheel, WheelConsumption, 0f);
        harness.Rules.SetEngineEnclosed(engine, enclosed: true);
        harness.Link(engine, new PhysicsBodyId(1));
        harness.Link(wheel, new PhysicsBodyId(1));
        harness.IngestBody(new PhysicsBodyId(1), new PhysicsVector3(0f, 0f, 0f), PhysicsVector3.Zero);

        harness.Rules.SetActive(wheel, true);
        for (uint tick = 1; tick <= 3; tick++)
        {
            harness.IngestBody(new PhysicsBodyId(1), new PhysicsVector3(tick, 0f, 0f), new PhysicsVector3(tick, 0f, 0f));
            harness.Tick(tick, new[] { PhysicsEvent.ContactPersisted(new PhysicsBodyId(1), new PhysicsBodyId(2)) });
        }

        PhysicsCommand command = Assert.Single(harness.Output.Commands);
        return (command.Impulse.X, harness.Rules.ClusterPowerFactor(wheel), harness.Rules.ComputePowerHash(), harness.Rules.ComputeActivationHash());
    }

    [Fact]
    public void RepositoryContentCarriesExtractedPowerData()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "content", "parts.json")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        PartContentLibrary library = PartContentLibrary.Load(Path.Combine(directory!.FullName, "content", "parts.json"));

        // Engines (BasePart.cs:606-608) are pure suppliers and never drive: the engine entries no
        // longer carry a motor capability, while their extracted engine power stays.
        PartCapabilities engine = library.GetPart(8).Capabilities!;
        Assert.Equal(150f, engine.EnginePower);
        Assert.Equal(0f, engine.PowerConsumption);
        Assert.True(engine.IsEngine);
        Assert.False(engine.IsPowered);
        Assert.Null(engine.MotorThrustPerTick);

        // The motor wheel is the consumer: it consumes and has no engine power of its own.
        PartCapabilities wheel = library.GetPart(17).Capabilities!;
        Assert.Equal(100f, wheel.PowerConsumption);
        Assert.Equal(0f, wheel.EnginePower);
        Assert.True(wheel.IsPowered);
        Assert.False(wheel.IsEngine);
        Assert.True(wheel.HasMotor);

        // A pig carries m_enginePower 20 in the prefab; the extractor copies the prefab truth.
        Assert.Equal(20f, library.GetPart(4).Capabilities!.EnginePower);
    }

    private sealed class PowerHarness
    {
        private readonly PhysicsBodyStore _bodies;
        private readonly List<PhysicsBodySnapshot> _snapshots = new();

        public GameplayRules Rules { get; }

        public GameplayTickOutput Output { get; } = new();

        public PowerHarness(EntityStore entities)
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
                new GameplayConfig(
                    GoalZone: new GameplayZone(new PhysicsVector3(500, 500, 500), new PhysicsVector3(501, 501, 501)),
                    MapBounds: new GameplayZone(new PhysicsVector3(-1000, -1000, -1000), new PhysicsVector3(1000, 1000, 1000)),
                    TntBlastRadius: 4f,
                    TntBlastImpulse: 12f,
                    TntIgniteImpactSpeed: 5f));
        }

        public void Link(EntityId entity, PhysicsBodyId body)
        {
            _bodies.Set(entity, new PhysicsBodyLink(body));
            Rules.LinkBody(entity, body);
        }

        public void IngestBody(PhysicsBodyId body) =>
            IngestBody(body, new PhysicsVector3(0f, 1f, 0f), PhysicsVector3.Zero);

        public void IngestBody(PhysicsBodyId body, PhysicsVector3 position, PhysicsVector3 velocity) =>
            _snapshots.Add(new PhysicsBodySnapshot(body, position, PhysicsQuaternion.Identity, velocity, PhysicsVector3.Zero));

        public void Tick(uint tick, IReadOnlyList<PhysicsEvent> events) =>
            Rules.Tick(tick, events.ToArray(), CollectionsMarshal.AsSpan(_snapshots), Output);
    }
}
