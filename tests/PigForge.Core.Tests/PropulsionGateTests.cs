using System.Runtime.InteropServices;

using PigForge.Core.Construction;
using PigForge.Core.Content;
using PigForge.Physics.Abstractions;

namespace PigForge.Core.Tests;

/// <summary>
/// The original's propulsion gate (gap list G25). A propulsion part is only valid while a chassis
/// (frame) touches it: <c>BasePropulsion.ValidatePart</c> counts the neighbours whose
/// <c>IsPartOfChassis()</c> is true (<c>BasePropulsion.cs:13-20</c>), <c>Frame.cs:37-40</c> is the
/// only override that makes a part count, and <c>Wings.cs:14-31</c> / <c>Tail.cs:12-29</c> repeat
/// the same loop. PigForge keeps such a part buildable and strips its force at runtime instead of
/// rejecting the build, matching the "engine outside a frame supplies nothing" treatment.
/// </summary>
public sealed class PropulsionGateTests
{
    private const uint PartFrame = 1;
    private const uint PartFan = 2;
    private const uint PartRocket = 3;

    [Fact]
    public void OnlyAFrameNeighbourMakesAChassisAnchor()
    {
        (ConstructionRules rules, _) = CreateRules();

        ConstructionResult frame = rules.Place(PartFrame, 0.5f, 0.5f, 0f, 1f, 0);
        ConstructionResult attached = rules.Place(PartFan, 1.5f, 0.5f, 0f, 1f, 0);
        ConstructionResult loose = rules.Place(PartFan, 6.5f, 0.5f, 0f, 1f, 0);

        Assert.True(frame.IsSuccess && attached.IsSuccess && loose.IsSuccess, attached.Error.ToString());
        Assert.True(rules.IsChassis(frame.Entity));
        Assert.False(rules.IsChassis(attached.Entity));

        // Fan.cs/rocket at (1.5, 0.5) touches the frame's cell, so it has a chassis neighbour.
        Assert.True(rules.HasChassisNeighbor(attached.Entity));

        // One cell of empty space to its left: nothing there is part of the chassis.
        Assert.False(rules.HasChassisNeighbor(loose.Entity));

        // A frame is not its own chassis neighbour, and the fan is not a chassis for anything.
        Assert.False(rules.HasChassisNeighbor(frame.Entity));
        Assert.Empty(rules.ConnectionsOf(loose.Entity));
    }

    [Fact]
    public void AnEnclosedPartCountsAsAttachedToItsFrame()
    {
        (ConstructionRules rules, _) = CreateRules();

        ConstructionResult frame = rules.Place(PartFrame, 0.5f, 0.5f, 0f, 1f, 0);
        ConstructionResult enclosed = rules.Place(PartFan, 0.5f, 0.5f, 0f, 1f, 0);

        Assert.True(enclosed.IsSuccess, enclosed.Error.ToString());
        Assert.Equal(frame.Entity, rules.EnclosedBy(enclosed.Entity));

        // Frame.cs:44-50 bolts the enclosed part to the frame with a FixedJoint, so it is attached
        // to the chassis even though it shares the frame's cell rather than sitting beside it.
        Assert.True(rules.HasChassisNeighbor(enclosed.Entity));
    }

    [Fact]
    public void AChassisAnchoredFanThrustsWhileALooseFanEmitsNothing()
    {
        GateHarness harness = new();
        EntityId anchored = harness.Create();
        EntityId loose = harness.Create();
        harness.Rules.AddFan(anchored, ImpulsePerTick, 1f, 0f);
        harness.Rules.AddFan(loose, ImpulsePerTick, 1f, 0f);
        harness.Link(anchored, new PhysicsBodyId(1));
        harness.Link(loose, new PhysicsBodyId(2));
        harness.Rules.SetChassisAnchored(anchored, anchored: true);
        harness.Rules.SetChassisAnchored(loose, anchored: false);
        harness.Ingest(new PhysicsBodyId(1), new PhysicsVector3(0f, 1f, 0f));
        harness.Ingest(new PhysicsBodyId(2), new PhysicsVector3(4f, 1f, 0f));

        harness.Tick(1);

        PhysicsCommand command = Assert.Single(harness.Output.Commands);
        Assert.Equal(new PhysicsBodyId(1), command.Body);
        Assert.Equal(ImpulsePerTick, command.Impulse.X);
        Assert.False(harness.Rules.IsChassisAnchored(loose));
    }

    [Fact]
    public void AFanWithoutAnEnclosedEngineInItsClusterEmitsNothing()
    {
        GateHarness harness = new();
        EntityId fan = harness.Create();
        harness.Rules.AddFan(fan, ImpulsePerTick, 1f, 0f);
        harness.Rules.AddPower(fan, 30f, 0f);
        PhysicsBodyId body = new(1);
        harness.Link(fan, body);
        harness.Ingest(body, new PhysicsVector3(0f, 1f, 0f));
        harness.Rules.SetChassisAnchored(fan, anchored: true);

        // FanPropeller.cs:85-92 scales the force by the engine power factor, so a fan whose cluster
        // carries no engine pushes nothing at all, however its switch reads.
        harness.Tick(1);
        Assert.Empty(harness.Output.Commands);

        // Engine.cs:61: the engine only supplies once it is enclosed in a frame.
        harness.AddEnclosedEngine(body);
        harness.Tick(2);

        PhysicsCommand command = Assert.Single(harness.Output.Commands);
        Assert.Equal(body, command.Body);
        Assert.Equal(ImpulsePerTick * GameplayRules.ComputePowerFactor(20f, 30f), command.Impulse.X, 5);
    }

    [Fact]
    public void ARotorIsAFanPropellerSoItIsGatedScaledAndCappedWhileABalloonIsNot()
    {
        GateHarness harness = new();
        EntityId rotor = harness.Create();
        EntityId balloon = harness.Create();
        // part 37 as content now carries it (tools/bple-fans: Part_Rotor_01_SET m_force 120 / 60,
        // m_defaultSpeed 7 x RotorSpeed 2.0, m_isRotor 1).
        harness.Rules.AddFan(rotor, RotorThrustPerTick, 0f, 1f, maxSpeed: 14f, isRotor: true);
        harness.Rules.AddPower(rotor, 200f, 0f);
        harness.Rules.AddBalloon(balloon, 0.383333f);
        PhysicsBodyId rotorBody = new(1);
        PhysicsBodyId balloonBody = new(2);
        harness.Link(rotor, rotorBody);
        harness.Link(balloon, balloonBody);
        harness.Ingest(rotorBody, new PhysicsVector3(0f, 1f, 0f));
        harness.Ingest(balloonBody, new PhysicsVector3(4f, 1f, 0f));

        // The rotor is a FanPropeller in the original (BasePropulsion.cs:7-20,
        // docs/specs/fan-propeller.md), so a rotor with no chassis neighbour supplies no thrust.
        // A balloon is Balloon.cs: no chassis gate, no power term -- pure unpowered lift.
        harness.Rules.SetChassisAnchored(rotor, anchored: false);
        harness.Tick(1);

        PhysicsCommand balloonLift = Assert.Single(harness.Output.Commands);
        Assert.Equal(balloonBody, balloonLift.Body);
        Assert.Equal(0.383333f, balloonLift.Impulse.Y, 5);

        // Anchored but unpowered: 200 powerConsumption with no engine in the cluster is still zero.
        harness.Rules.SetChassisAnchored(rotor, anchored: true);
        harness.Tick(2);

        balloonLift = Assert.Single(harness.Output.Commands);
        Assert.Equal(balloonBody, balloonLift.Body);

        harness.AddEnclosedEngine(rotorBody);
        harness.Tick(3);

        PhysicsCommand powered = Assert.Single(
            harness.Output.Commands.Where(command => command.Body == rotorBody));
        // FanPropeller.cs:83-112: the thrust is m_force x power factor; the ingested 1 m/s is
        // inside the cap (14 x factor ~ 2.49), so LimitForceForSpeed does not decay it and the
        // rotor's overspeed brake does not fire.
        Assert.Equal(0f, powered.Impulse.X);
        Assert.Equal(RotorThrustPerTick * GameplayRules.ComputePowerFactor(20f, 200f), powered.Impulse.Y, 4);
    }

    [Fact]
    public void AnElectricUmbrellaOnlyDampsOnceItsClusterHasPower()
    {
        GateHarness harness = new();
        EntityId umbrella = harness.Create();
        harness.Rules.AddUmbrella(umbrella, 0.35f);
        harness.Rules.AddPower(umbrella, 100f, 0f);
        PhysicsBodyId body = new(1);
        harness.Link(umbrella, body);
        harness.Ingest(body, new PhysicsVector3(0f, 5f, 0f), new PhysicsVector3(0f, -10f, 0f));

        // PoweredUmbrella.cs:70-89: m_maximumForce = m_force x engine power factor, so an electric
        // umbrella with no engine in its cluster damps nothing.
        harness.Tick(1);
        Assert.Empty(harness.Output.Commands);

        harness.AddEnclosedEngine(body);
        harness.Tick(2);

        PhysicsCommand command = Assert.Single(harness.Output.Commands);
        Assert.Equal(10f * 0.35f * GameplayRules.ComputePowerFactor(20f, 100f), command.Impulse.Y, 5);
    }

    private const float ImpulsePerTick = 1.2f;

    /// <summary>Content part 37's thrust (tools/bple-fans: Part_Rotor_01_SET m_force 120 / 60).</summary>
    private const float RotorThrustPerTick = 2f;

    private static (ConstructionRules Rules, EntityStore Entities) CreateRules()
    {
        PartContentLibrary content = new(PartContentParser.Parse("""
        {
            "format": "pigforge.part-content",
            "schemaVersion": 1,
            "contentVersion": "propulsion-gate-test-v1",
            "physics": { "maximumAngularSpeed": 7.0, "damping": { "linear": 0.2, "angular": 0.05 } },
            "parts": [
                { "partTypeId": 1, "name": "frame", "mode": "dynamic", "mass": 1,
                  "capabilities": { "jointConnectionType": "source", "canEnclose": true },
                  "shapes": [ { "kind": "box", "halfExtents": [0.5, 0.5, 0.5] } ] },
                { "partTypeId": 2, "name": "fan", "mode": "dynamic", "mass": 0.5,
                  "capabilities": { "jointConnectionType": "target", "fan": { "thrustPerTick": 1.2, "directionX": 1 }, "activation": "toggle" },
                  "shapes": [ { "kind": "box", "halfExtents": [0.4, 0.4, 0.5] } ] },
                { "partTypeId": 3, "name": "rocket", "mode": "dynamic", "mass": 1,
                  "capabilities": { "jointConnectionType": "target" },
                  "shapes": [ { "kind": "box", "halfExtents": [0.4, 0.4, 0.5] } ] }
            ]
        }
        """));
        EntityStore entities = new();
        return (new ConstructionRules(entities, new PartStore(entities), new TransformStore(entities), content), entities);
    }

    /// <summary>Gameplay rules over empty stores: the gate is declared explicitly, exactly as
    /// GameRoom.SyncChassisAnchors publishes it from the construction layout.</summary>
    private sealed class GateHarness
    {
        private readonly EntityStore _entities = new();
        private readonly PhysicsBodyStore _bodies;
        private readonly List<PhysicsBodySnapshot> _snapshots = new();

        public GateHarness()
        {
            _bodies = new PhysicsBodyStore(_entities);
            Rules = new GameplayRules(
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
                new GameplayConfig(
                    GoalZone: new GameplayZone(new PhysicsVector3(500, 500, 500), new PhysicsVector3(501, 501, 501)),
                    MapBounds: new GameplayZone(new PhysicsVector3(-1000, -1000, -1000), new PhysicsVector3(1000, 1000, 1000)),
                    TntBlastRadius: 4f,
                    TntBlastImpulse: 12f,
                    TntIgniteImpactSpeed: 5f));
        }

        public GameplayRules Rules { get; }

        public GameplayTickOutput Output { get; } = new();

        public EntityId Create() => _entities.Create();

        public void Link(EntityId entity, PhysicsBodyId body)
        {
            _bodies.Set(entity, new PhysicsBodyLink(body));
            Rules.LinkBody(entity, body);
        }

        /// <summary>A pig-shaped engine (enginePower 20) enclosed in a frame, so it supplies.</summary>
        public EntityId AddEnclosedEngine(PhysicsBodyId body, float enginePower = 20f)
        {
            EntityId engine = _entities.Create();
            Rules.AddPower(engine, 0f, enginePower);
            Link(engine, body);
            Rules.SetEngineEnclosed(engine, enclosed: true);
            return engine;
        }

        public void Ingest(PhysicsBodyId body, PhysicsVector3 position) =>
            Ingest(body, position, PhysicsVector3.Zero);

        public void Ingest(PhysicsBodyId body, PhysicsVector3 position, PhysicsVector3 velocity) =>
            _snapshots.Add(new PhysicsBodySnapshot(body, position, PhysicsQuaternion.Identity, velocity, PhysicsVector3.Zero));

        public void Tick(uint tick) =>
            Rules.Tick(tick, Array.Empty<PhysicsEvent>(), CollectionsMarshal.AsSpan(_snapshots), Output);
    }
}
