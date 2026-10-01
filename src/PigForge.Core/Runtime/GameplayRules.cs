using PigForge.Physics.Abstractions;

namespace PigForge.Core;

public enum GameplayPhase
{
    Playing,
    Won,
    Failed
}

public readonly record struct GameplayZone(PhysicsVector3 Min, PhysicsVector3 Max)
{
    public bool Contains(PhysicsVector3 position) =>
        position.X >= Min.X && position.X <= Max.X
        && position.Y >= Min.Y && position.Y <= Max.Y
        && position.Z >= Min.Z && position.Z <= Max.Z;
}

/// <summary>
/// Level-driven rule parameters. Per ADR-002 there are no damage primitives: only
/// impulses, position triggers (goal zone, map bounds) and restart.
/// </summary>
public sealed record GameplayConfig(
    GameplayZone GoalZone,
    GameplayZone MapBounds,
    float TntBlastRadius,
    float TntBlastImpulse,
    float TntIgniteImpactSpeed,
    float EggBreakImpactSpeed = 6f,
    float SeamBreakImpulse = 10f,
    uint MaxTicks = 0,
    bool ObjectivesEnabled = true,
    // True when the physics backend already applies restitution in its solver (see
    // PhysicsCapabilities.AppliesRestitutionNatively): the rules layer then leaves elasticity
    // alone instead of adding a second, synthesized bounce on top of it.
    bool RestitutionAppliedNatively = false)
{
    public static GameplayConfig Default { get; } = new(
        GoalZone: new GameplayZone(new PhysicsVector3(-9, 0, -2), new PhysicsVector3(-7, 4, 2)),
        MapBounds: new GameplayZone(new PhysicsVector3(-100, -5, -20), new PhysicsVector3(100, 60, 20)),
        TntBlastRadius: 4f,
        TntBlastImpulse: 25f,
        TntIgniteImpactSpeed: 5f,
        SeamBreakImpulse: 10f,
        MaxTicks: 0);

}

/// <summary>Per-tick rule output; the caller owns and reuses the instance to keep the tick path allocation-free.</summary>
public sealed class GameplayTickOutput
{
    public List<PhysicsCommand> Commands { get; } = new();

    public List<EntityId> DestroyedEntities { get; } = new();

    /// <summary>Entities that must be pulled out of their compound body (a detacher
    /// fired on impact): the room splits the cluster at the nearest seam so the part
    /// becomes its own body.</summary>
    public List<EntityId> DetachedEntities { get; } = new();

    public void Clear()
    {
        Commands.Clear();
        DestroyedEntities.Clear();
        DetachedEntities.Clear();
    }
}

/// <summary>
/// Runtime gameplay rules (motors, wheels, pigs, TNT, joint breaks, level outcome) that
/// consume physics events and snapshots only. Semantics per ADR-002: pigs are
/// indestructible bouncy cargo; TNT is a pure momentum source; win is delivery into the
/// goal zone; a pig leaving the map bounds requests a deterministic restart.
/// </summary>
public sealed class GameplayRules
{

    private readonly EntityStore _entities;
    private readonly MotorStore _motors;
    private readonly BalloonStore _balloons;
    private readonly FanStore _fans;
    private readonly SpringStore _springs;
    private readonly RocketStore _rockets;
    private readonly TntStore _tnt;
    private readonly BlasterStore _blasters;
    private readonly GlueStore _glues;
    private readonly WheelStore _wheels;
    private readonly PigStore _pigs;
    private readonly EggStore _eggs;
    private readonly WingStore _wings;
    private readonly TailStore _tails;
    private readonly UmbrellaStore _umbrellas;
    private readonly GearboxStore _gearboxes;
    private readonly BellowsStore _bellows;
    private readonly DetacherStore _detachers;
    private readonly GrappleStore _grapples;
    private readonly ActivationStore _activations;
    private readonly RestitutionStore _restitutions;
    private readonly PowerStore _powers;
    private readonly PhysicsBodyStore _bodies;
    private readonly GameplayConfig _config;

    private readonly Dictionary<uint, EntityId> _entitiesByBody = new();
    private readonly Dictionary<uint, (PhysicsVector3 Position, PhysicsVector3 Velocity)> _kinematicsByBody = new();
    private readonly HashSet<uint> _touchedBodies = new();
    private readonly HashSet<uint> _brokenJoints = new();
    private readonly HashSet<uint> _dynamicBodies = new();
    private readonly Dictionary<uint, PhysicsVector3> _previousVelocities = new();
    // Elasticity is a per-part content property, but a contact names bodies: a pig welded into
    // a craft shares one compound body with plain structure. The body therefore carries the
    // strongest restitution and the summed mass of its members, and the members are tracked so
    // a seam split or a rebind recomputes them instead of dropping the value.
    private readonly Dictionary<uint, List<uint>> _membersByBody = new();
    private readonly Dictionary<uint, float> _restitutionByBody = new();
    private readonly Dictionary<uint, float> _massByBody = new();
    private readonly HashSet<uint> _bouncedBodies = new();
    // Engine/propulsion power of a cluster (spec docs/specs/power-system.md): the engine power
    // and enabled consumption summed over its members, turned into the original's power factor.
    // The original's component is the JOINT graph, not the physics body (Contraption.cs:1293
    // unions every m_jointMap entry, so a wheel hinged to a chassis shares that chassis's engine
    // power), while a PigForge wheel keeps its own body and hinges to its chassis (ADR-009). The
    // placement layer therefore declares the extra edges with LinkPowerCluster, and this map holds
    // them; a part with no explicit host sits in its own physics body's cluster.
    private readonly Dictionary<uint, float> _powerFactorByCluster = new();
    private readonly Dictionary<uint, uint> _powerHostByEntity = new();

    /// <summary>
    /// The original's <c>EnginePowerLimit</c> (INSettingsBExp.json, 4.0): the raw ratio is capped
    /// at <c>10 * EnginePowerLimit</c> before the exponent (Contraption.cs:545). The two exponents
    /// are the verbatim constants of Contraption.cs:553.
    /// </summary>
    public const float EnginePowerLimit = 4f;

    private const float PowerFactorHighExponent = 0.585f;

    private const float PowerFactorLowExponent = 0.75f;

    /// <summary>Bound on the cluster-host walk: a hinge chain is wheel -> chassis in practice
    /// (wheel -> wheel is the assembler's fallback), and the bound keeps a malformed chain from
    /// spinning.</summary>
    private const int PowerClusterHopLimit = 8;

    /// <summary>Floor that keeps a massless part from producing an unbounded bounce impulse.</summary>
    private const float MinimumPartMass = 0.001f;

    /// <summary>A landing slower than this is not an impact worth bouncing (matches the order of
    /// the level's own impact thresholds).</summary>
    private const float MinimumBounceApproachSpeed = 0.5f;

    /// <summary>How many ticks a remembered approach speed stays usable. A contact event can
    /// arrive after the impact (measured: a 9.32 m/s landing was reported as 2.56 m/s because the
    /// solver had already absorbed the rest), so the impact speed has to outlive the event by a
    /// tick or two -- but anything older belongs to an earlier, unrelated touch and must not be
    /// charged to this one.</summary>
    private const uint PeakApproachLifetimeTicks = 2;

    /// <summary>The strongest speed a body has carried recently, tagged with the tick that set it,
    /// so a stale peak can be discarded instead of bouncing a gentle later touch.</summary>
    private readonly record struct PeakApproachSample(PhysicsVector3 Velocity, uint Tick);

    private readonly Dictionary<uint, PeakApproachSample> _peakApproachByBody = new();
    private uint _tick;

    private int _alivePigs;

    public GameplayRules(
        EntityStore entities,
        MotorStore motors,
        BalloonStore balloons,
        FanStore fans,
        SpringStore springs,
        RocketStore rockets,
        TntStore tnt,
        BlasterStore blasters,
        GlueStore glues,
        WheelStore wheels,
        PigStore pigs,
        EggStore eggs,
        WingStore wings,
        TailStore tails,
        UmbrellaStore umbrellas,
        GearboxStore gearboxes,
        BellowsStore bellows,
        DetacherStore detachers,
        GrappleStore grapples,
        ActivationStore activations,
        RestitutionStore restitutions,
        PowerStore powers,
        PhysicsBodyStore bodies,
        GameplayConfig config)
    {
        _entities = entities ?? throw new ArgumentNullException(nameof(entities));
        _motors = motors ?? throw new ArgumentNullException(nameof(motors));
        _balloons = balloons ?? throw new ArgumentNullException(nameof(balloons));
        _fans = fans ?? throw new ArgumentNullException(nameof(fans));
        _springs = springs ?? throw new ArgumentNullException(nameof(springs));
        _rockets = rockets ?? throw new ArgumentNullException(nameof(rockets));
        _tnt = tnt ?? throw new ArgumentNullException(nameof(tnt));
        _blasters = blasters ?? throw new ArgumentNullException(nameof(blasters));
        _glues = glues ?? throw new ArgumentNullException(nameof(glues));
        _eggs = eggs ?? throw new ArgumentNullException(nameof(eggs));
        _wheels = wheels ?? throw new ArgumentNullException(nameof(wheels));
        _pigs = pigs ?? throw new ArgumentNullException(nameof(pigs));
        _wings = wings ?? throw new ArgumentNullException(nameof(wings));
        _tails = tails ?? throw new ArgumentNullException(nameof(tails));
        _umbrellas = umbrellas ?? throw new ArgumentNullException(nameof(umbrellas));
        _gearboxes = gearboxes ?? throw new ArgumentNullException(nameof(gearboxes));
        _bellows = bellows ?? throw new ArgumentNullException(nameof(bellows));
        _detachers = detachers ?? throw new ArgumentNullException(nameof(detachers));
        _grapples = grapples ?? throw new ArgumentNullException(nameof(grapples));
        _activations = activations ?? throw new ArgumentNullException(nameof(activations));
        _restitutions = restitutions ?? throw new ArgumentNullException(nameof(restitutions));
        _powers = powers ?? throw new ArgumentNullException(nameof(powers));
        _bodies = bodies ?? throw new ArgumentNullException(nameof(bodies));
        _config = config ?? throw new ArgumentNullException(nameof(config));
    }

    public GameplayPhase Phase { get; private set; } = GameplayPhase.Playing;

    public int AlivePigs => _alivePigs;

    public bool RestartRequested { get; private set; }

    public IReadOnlyCollection<uint> BrokenJoints => _brokenJoints;

    public void LinkBody(EntityId entity, PhysicsBodyId body, bool isDynamic = true)
    {
        if (!_bodies.TryGet(entity, out PhysicsBodyLink link) || link.Body != body)
        {
            throw new ArgumentException($"Entity {entity.Value} has no physics body link matching {body.Value}.", nameof(body));
        }

        _entitiesByBody[body.Value] = entity;
        if (isDynamic)
        {
            _dynamicBodies.Add(body.Value);
        }

        if (!_membersByBody.TryGetValue(body.Value, out List<uint>? members))
        {
            members = new List<uint>();
            _membersByBody.Add(body.Value, members);
        }

        if (!members.Contains(entity.Value))
        {
            members.Add(entity.Value);
        }

        RecomputeBodyMaterial(body);
        RecomputeBodyCluster(body);
    }

    /// <summary>Elasticity of a part. <paramref name="mass"/> is the part's own mass, used to
    /// derive the pair's reduced mass when the rules layer has to synthesize a bounce.</summary>
    public void AddRestitution(EntityId entity, float restitution, float mass)
    {
        if (!float.IsFinite(restitution) || restitution is < 0f or > 1f)
        {
            throw new ArgumentOutOfRangeException(nameof(restitution), "A restitution must be finite and within [0, 1].");
        }

        if (!float.IsFinite(mass) || mass < 0f)
        {
            throw new ArgumentOutOfRangeException(nameof(mass), "A part mass must be finite and non-negative.");
        }

        _restitutions.Set(entity, new RestitutionState(restitution, mass));
    }

    public void AddPig(EntityId entity)
    {
        _pigs.Set(entity, default);
        _alivePigs++;
    }

    public void AddTnt(EntityId entity, ushort fuseTicks, bool chainDetonate = true, bool igniteOnImpact = true) =>
        _tnt.Set(entity, new TntState(fuseTicks, Ignited: false, chainDetonate, igniteOnImpact));

    public void AddBlaster(EntityId entity, float radius, float impulse, float chainRadius) =>
        _blasters.Set(entity, new BlasterState(radius, impulse, chainRadius));

    /// <summary>Marks a part as super glue: its compound cluster never splits along a seam.</summary>
    public void AddGlue(EntityId entity) => _glues.Set(entity, default);

    public bool HasGlue(EntityId entity) => _glues.TryGet(entity, out _);

    public void AddMotor(EntityId entity, float impulsePerTick, float directionX) =>
        _motors.Set(entity, new MotorState(impulsePerTick, directionX));

    /// <summary>
    /// Registers a part's power data (spec docs/specs/power-system.md §4 item 1): the content's
    /// <c>powerConsumption</c> and <c>enginePower</c>. Parts without either (the majority) get no
    /// entry, which keeps their drive unconditional. An engine starts unenclosed; the placement
    /// layer marks it enclosed while it sits in a frame (<see cref="SetEngineEnclosed"/>).
    /// </summary>
    public void AddPower(EntityId entity, float powerConsumption, float enginePower)
    {
        if (!float.IsFinite(powerConsumption) || powerConsumption < 0f)
        {
            throw new ArgumentOutOfRangeException(nameof(powerConsumption), "A power consumption must be finite and non-negative.");
        }

        if (!float.IsFinite(enginePower) || enginePower < 0f)
        {
            throw new ArgumentOutOfRangeException(nameof(enginePower), "An engine power must be finite and non-negative.");
        }

        _powers.Set(entity, new PowerState(powerConsumption, enginePower, EngineEnclosed: false));
        RecomputePowerCluster(entity);
    }

    /// <summary>
    /// Marks (or unmarks) an engine as sitting inside a frame: <c>ValidatePart() =&gt;
    /// m_enclosedInto != null</c> (Engine.cs:61), so an engine outside a frame supplies nothing
    /// (spec docs/specs/power-system.md §4 item 2). A part without engine power is unaffected.
    /// </summary>
    public void SetEngineEnclosed(EntityId entity, bool enclosed)
    {
        if (!_powers.TryGet(entity, out PowerState power) || power.EnginePower <= 0f || power.EngineEnclosed == enclosed)
        {
            return;
        }

        _powers.Set(entity, power with { EngineEnclosed = enclosed });
        RecomputePowerCluster(entity);
    }

    /// <summary>
    /// The original's power factor, verbatim (Contraption.cs:540-556): the raw ratio of a
    /// component's engine power to its consumption, capped at <c>10 * EnginePowerLimit</c>, raised
    /// to 0.585 above 1 and 0.75 otherwise. Consumption at or below 1 with an engine present is
    /// exactly 1; no engine is 0.
    /// </summary>
    public static float ComputePowerFactor(float enginePower, float powerConsumption)
    {
        float raw = 0f;
        if (powerConsumption > 1f)
        {
            raw = MathF.Min(enginePower / powerConsumption, 10f * EnginePowerLimit);
        }
        else if (enginePower > 0f)
        {
            raw = 1f;
        }

        return MathF.Pow(raw, raw > 1f ? PowerFactorHighExponent : PowerFactorLowExponent);
    }

    /// <summary>
    /// Declares that a part's power belongs to another part's cluster: the assembler hinges every
    /// wheel to a neighbour (its chassis, or the lowest-id neighbour), so a wheel's consumption must
    /// count toward the chassis's cluster and the chassis's engine power must reach the wheel
    /// (Contraption.cs:1293 unions the joint graph; spec docs/specs/power-system.md §4 item 3).
    /// </summary>
    public void LinkPowerCluster(EntityId member, EntityId host)
    {
        uint hostKey = PowerClusterKey(host);
        if (hostKey == member.Value || _powerHostByEntity.TryGetValue(member.Value, out uint existing) && existing == hostKey)
        {
            return;
        }

        uint previousKey = PowerClusterKey(member);
        _powerHostByEntity[member.Value] = hostKey;
        if (previousKey != hostKey)
        {
            RecomputePowerCluster(new EntityId(previousKey));
        }

        RecomputePowerCluster(new EntityId(hostKey));
    }

    /// <summary>
    /// The power factor of the cluster this part belongs to. A missing factor is resolved on the
    /// spot rather than defaulting to 1, so a membership change can never fail open.
    /// </summary>
    public float ClusterPowerFactor(EntityId entity)
    {
        uint key = PowerClusterKey(entity);
        if (!_powerFactorByCluster.TryGetValue(key, out float factor))
        {
            RecomputePowerCluster(new EntityId(key));
            factor = _powerFactorByCluster.TryGetValue(key, out float resolved) ? resolved : 0f;
        }

        return factor;
    }

    public void AddBalloon(EntityId entity, float liftPerTick) =>
        _balloons.Set(entity, new BalloonState(liftPerTick));

    public void AddFan(EntityId entity, float impulsePerTick, float directionX, float directionY) =>
        _fans.Set(entity, new FanState(impulsePerTick, directionX, directionY));

    public void AddSpring(EntityId entity, float bounceImpulsePerTick) =>
        _springs.Set(entity, new SpringState(bounceImpulsePerTick, BouncedRecently: false));

    public void AddRocket(EntityId entity, float thrustPerTick, float directionX, float directionY, ushort durationTicks, float explodeRadius = 0f, float explodeImpulse = 0f) =>
        _rockets.Set(entity, new RocketState(thrustPerTick, directionX, directionY, durationTicks, Ignited: false, explodeRadius, explodeImpulse));

    public void AddEgg(EntityId entity) => _eggs.Set(entity, default);

    public void AddWing(EntityId entity, float liftCoef, float maxLift) =>
        _wings.Set(entity, new WingState(liftCoef, maxLift));

    public void AddTail(EntityId entity, float dragCoef) =>
        _tails.Set(entity, new TailState(dragCoef));

    public void AddUmbrella(EntityId entity, float dragCoef) =>
        _umbrellas.Set(entity, new UmbrellaState(dragCoef));

    public void AddGearbox(EntityId entity) => _gearboxes.Set(entity, default);

    public void AddBellows(EntityId entity, float boostImpulse) =>
        _bellows.Set(entity, new BellowsState(boostImpulse, BoostedRecently: false));

    public void AddDetacher(EntityId entity) => _detachers.Set(entity, default);

    public void AddGrapple(EntityId entity, float impulse, float directionX, float directionY) =>
        _grapples.Set(entity, new GrappleState(impulse, directionX, directionY, FiredRecently: false));

    /// <summary>Declares a switch for a part; the switch starts off.</summary>
    public void AddActivation(EntityId entity) =>
        _activations.Set(entity, new ActivationState(Active: false));

    /// <summary>Flips a switch the caller has already validated as owned and switchable.</summary>
    public void SetActive(EntityId entity, bool active)
    {
        if (!_activations.TryGet(entity, out ActivationState state) || state.Active == active)
        {
            return;
        }

        _activations.Set(entity, state with { Active = active });
        RecomputePowerCluster(entity);
    }

    /// <summary>Snapshot/hash view: true only when the part has a switch and it is on.</summary>
    public bool IsPartActive(EntityId entity) =>
        _activations.TryGet(entity, out ActivationState state) && state.Active;

    /// <summary>Deterministic hash over switch state (entity id + active) in slot order.</summary>
    public long ComputeActivationHash()
    {
        long hash = 17;
        var activations = _activations.GetEnumerator();
        while (activations.MoveNext())
        {
            hash = unchecked((hash * 31) + activations.CurrentId.Value.GetHashCode());
            hash = unchecked((hash * 31) + (activations.CurrentValue.Active ? 1 : 0));
        }

        return hash;
    }

    /// <summary>No switch means legacy content: the part keeps its always-on behaviour.</summary>
    private bool IsDriven(EntityId entity) =>
        !_activations.TryGet(entity, out ActivationState state) || state.Active;

    /// <summary>True when the part has an activation entry, i.e. its content declared a switch.</summary>
    public bool HasSwitch(EntityId entity) => _activations.TryGet(entity, out _);

    /// <summary>Consumes a one-shot switch: false when the part has no switch (the caller
    /// falls back to its legacy contact rule) or the switch is off.</summary>
    private bool TryConsumeTrigger(EntityId entity)
    {
        if (!_activations.TryGet(entity, out ActivationState state))
        {
            return false;
        }

        if (!state.Active)
        {
            return false;
        }

        _activations.Set(entity, state with { Active = false });
        return true;
    }

    public void AddWheel(EntityId entity) => _wheels.Set(entity, default);
    public void Tick(uint tick, ReadOnlySpan<PhysicsEvent> events, ReadOnlySpan<PhysicsBodySnapshot> snapshots, GameplayTickOutput output)
    {
        if (Phase != GameplayPhase.Playing)
        {
            return;
        }

        output.Clear();
        _tick = tick;
        _touchedBodies.Clear();
        IngestSnapshots(snapshots);
        ProcessEvents(events, output);
        ReArmBounces();
        RunMotors(output);
        RunBalloons(output);
        RunFans(output);
        RunAerodynamics(output);
        RunSprings(output);
        RunBellows(output);
        RunGrapples(output);
        RunDetachers(output);
        RunRockets(output);
        RunTntFuses(output);
        RunBlasters(output);
        DropCommandsForDestroyedBodies(output);
        CheckObjectives(tick);
        StorePreviousVelocities();
    }

    private void StorePreviousVelocities()
    {
        _previousVelocities.Clear();
        foreach (var entry in _kinematicsByBody)
        {
            _previousVelocities[entry.Key] = entry.Value.Velocity;
            // Keep the stronger of this tick's speed and the remembered one. No decay: the peak
            // stays exact for the tick or two a late contact event needs, and PeakApproach()
            // drops anything older than its lifetime, which is what keeps it from sticking.
            if (!_peakApproachByBody.TryGetValue(entry.Key, out PeakApproachSample peak)
                || PhysicsVector3.Distance(entry.Value.Velocity, PhysicsVector3.Zero)
                    >= PhysicsVector3.Distance(peak.Velocity, PhysicsVector3.Zero))
            {
                _peakApproachByBody[entry.Key] = new PeakApproachSample(entry.Value.Velocity, _tick);
            }
        }
    }

    private void IngestSnapshots(ReadOnlySpan<PhysicsBodySnapshot> snapshots)
    {
        for (int index = 0; index < snapshots.Length; index++)
        {
            PhysicsBodySnapshot snapshot = snapshots[index];
            if (!snapshot.Body.IsValid)
            {
                continue;
            }

            _kinematicsByBody[snapshot.Body.Value] = (snapshot.Position, snapshot.LinearVelocity);
        }
    }

    private void ProcessEvents(ReadOnlySpan<PhysicsEvent> events, GameplayTickOutput output)
    {
        for (int index = 0; index < events.Length; index++)
        {
            PhysicsEvent physicsEvent = events[index];
            switch (physicsEvent.Kind)
            {
                case PhysicsEventKind.ContactStarted:
                case PhysicsEventKind.ContactPersisted:
                    _touchedBodies.Add(physicsEvent.BodyA.Value);
                    _touchedBodies.Add(physicsEvent.BodyB.Value);
                    if (physicsEvent.Kind == PhysicsEventKind.ContactStarted)
                    {
                        // Per ADR-002 the original game ignites TNT on strong impact only;
                        // a gentle touch (resting on ground) must not trigger it. Post-step
                        // velocities already include the collision response, so the impact
                        // signal is the pair's largest velocity change (impulse per mass) —
                        // a braced charge takes little of its own, but the striker shows it.
                        float pairImpact = MathF.Max(VelocityChange(physicsEvent.BodyA), VelocityChange(physicsEvent.BodyB));
                        IgniteTntOnBody(physicsEvent.BodyA, pairImpact);
                        IgniteTntOnBody(physicsEvent.BodyB, pairImpact);
                        ChallengeEggsOnImpact(physicsEvent.BodyA, pairImpact, output);
                        ChallengeEggsOnImpact(physicsEvent.BodyB, pairImpact, output);
                        DetachOnImpact(physicsEvent.BodyA, pairImpact, output);
                        DetachOnImpact(physicsEvent.BodyB, pairImpact, output);
                        ApplyBounce(physicsEvent, output);
                    }

                    break;
                case PhysicsEventKind.JointBroken:
                    _brokenJoints.Add(physicsEvent.Joint.Value);
                    break;
            }
        }
    }

    private float VelocityChange(PhysicsBodyId body)
    {
        if (!_kinematicsByBody.TryGetValue(body.Value, out var current)
            || !_previousVelocities.TryGetValue(body.Value, out var previous))
        {
            return 0f;
        }

        return PhysicsVector3.Distance(current.Velocity, previous);
    }

    private void IgniteTntOnBody(PhysicsBodyId body, float impactSpeed)
    {
        if (impactSpeed < _config.TntIgniteImpactSpeed
            || !_entitiesByBody.TryGetValue(body.Value, out EntityId entity)
            || !_tnt.TryGet(entity, out TntState tnt)
            || tnt.Ignited
            || !tnt.IgniteOnImpact)
        {
            return;
        }

        _tnt.Set(entity, tnt with { Ignited = true });
    }

    /// <summary>
    /// Synthesizes the bounce a backend without a restitution term cannot express, by asking for
    /// the outgoing velocity the restitution demands rather than adding an impulse. Two facts
    /// drive the shape: rule commands run before the next step's solver, and a contact constraint
    /// owns the normal velocity for as long as the pair touches — measured, a 9.32 m/s landing
    /// that was given the demanded separation speed kept 1.12 m/s of it, and raising
    /// <c>MaximumRecoveryVelocity</c> from 2 to 30 changed the result by nothing. So the pair is
    /// suppressed for exactly the step that carries the impulse: nothing constrains the normal
    /// velocity, the demanded separation speed survives, and the next step collides normally
    /// again with the pair already apart.
    /// </summary>
    private void ApplyBounce(PhysicsEvent physicsEvent, GameplayTickOutput output)
    {
        if (_config.RestitutionAppliedNatively || physicsEvent.ApproachSpeed <= MinimumBounceApproachSpeed)
        {
            return;
        }

        uint bodyA = physicsEvent.BodyA.Value;
        uint bodyB = physicsEvent.BodyB.Value;
        bool dynamicA = _dynamicBodies.Contains(bodyA);
        bool dynamicB = _dynamicBodies.Contains(bodyB);
        if (!dynamicA && !dynamicB)
        {
            return;
        }

        // Re-arm guard: while a body keeps touching something it must not bounce once per tick
        // (the same shape as the spring bounce guard). Cleared by ReArmBounces the moment the
        // body leaves contact.
        if ((dynamicA && _bouncedBodies.Contains(bodyA)) || (dynamicB && _bouncedBodies.Contains(bodyB)))
        {
            return;
        }

        // The bounciest surface in the pair dominates: a pig riding inside a wooden craft is
        // what makes that craft rebound (ADR-002), and every other part carries restitution 0.
        float restitution = MathF.Max(
            _restitutionByBody.GetValueOrDefault(bodyA),
            _restitutionByBody.GetValueOrDefault(bodyB));
        if (restitution <= 0f)
        {
            return;
        }

        float inverseMassA = dynamicA ? 1f / MathF.Max(_massByBody.GetValueOrDefault(bodyA), MinimumPartMass) : 0f;
        float inverseMassB = dynamicB ? 1f / MathF.Max(_massByBody.GetValueOrDefault(bodyB), MinimumPartMass) : 0f;
        float inverseTotal = inverseMassA + inverseMassB;
        if (inverseTotal <= 0f)
        {
            return;
        }

        PhysicsVector3 normal = physicsEvent.ContactNormal;
        float separation = RelativeNormalSpeed(physicsEvent.BodyA, physicsEvent.BodyB, normal);
        // Every speed read at the moment the event arrives understates the impact, because the
        // solver's soft recovery starts absorbing before the shapes look like they have met: a
        // measured 9.32 m/s landing was reported as 2.56 m/s by the backend and 2.56 m/s by this
        // layer's own previous-tick velocities (the event came two ticks after the last free
        // tick). Only the peak the free fall left behind still holds the real number.
        float approachSpeed = MathF.Max(
            ApproachFromHistory(bodyA, bodyB, normal, dynamicA, dynamicB),
            physicsEvent.ApproachSpeed);
        approachSpeed = MathF.Max(approachSpeed, PeakApproach(bodyA, bodyB, normal, dynamicA, dynamicB));
        float deltaVelocity = (restitution * approachSpeed) - separation;
        if (deltaVelocity <= 0f)
        {
            return;
        }

        output.Commands.Add(PhysicsCommand.SuppressContact(physicsEvent.BodyA, physicsEvent.BodyB));
        float magnitude = deltaVelocity / inverseTotal;
        if (dynamicA)
        {
            if (_kinematicsByBody.TryGetValue(bodyA, out var kinematicsA))
            {
                output.Commands.Add(PhysicsCommand.ApplyImpulse(
                    physicsEvent.BodyA, normal * magnitude, kinematicsA.Position));
            }

            _bouncedBodies.Add(bodyA);
        }

        if (dynamicB)
        {
            if (_kinematicsByBody.TryGetValue(bodyB, out var kinematicsB))
            {
                output.Commands.Add(PhysicsCommand.ApplyImpulse(
                    physicsEvent.BodyB, normal * -magnitude, kinematicsB.Position));
            }

            _bouncedBodies.Add(bodyB);
        }
    }

    /// <summary>How fast the pair was closing along <paramref name="normal"/> at the strongest of
    /// the last few ticks. This is what rescues a bounce whose contact event arrived after the
    /// solver had already eaten the approach speed, and it recovers that speed exactly, because a
    /// free fall sets a fresh peak every tick it accelerates. A peak older than its lifetime is
    /// ignored: it belongs to a different touch.</summary>
    private float PeakApproach(uint bodyA, uint bodyB, PhysicsVector3 normal, bool dynamicA, bool dynamicB)
    {
        float approach = 0f;
        if (dynamicA && TryRecentPeak(bodyA, out PhysicsVector3 peakA))
        {
            approach = MathF.Max(approach, -PhysicsVector3.Dot(peakA, normal));
        }

        if (dynamicB && TryRecentPeak(bodyB, out PhysicsVector3 peakB))
        {
            approach = MathF.Max(approach, PhysicsVector3.Dot(peakB, normal));
        }

        return approach;
    }

    /// <summary>The body's remembered peak, but only while it is recent enough to belong to the
    /// contact being resolved.</summary>
    private bool TryRecentPeak(uint body, out PhysicsVector3 peak)
    {
        if (_peakApproachByBody.TryGetValue(body, out PeakApproachSample sample)
            && _tick - sample.Tick <= PeakApproachLifetimeTicks)
        {
            peak = sample.Velocity;
            return true;
        }

        peak = PhysicsVector3.Zero;
        return false;
    }

    /// <summary>How fast the pair was closing along <paramref name="normal"/> before this tick's
    /// solve, taken from the velocities the bodies carried into it. <paramref name="normal"/>
    /// pushes <c>bodyA</c> away from <c>bodyB</c>, so a body moving into the contact shows a
    /// positive projection.</summary>
    private float ApproachFromHistory(uint bodyA, uint bodyB, PhysicsVector3 normal, bool dynamicA, bool dynamicB)
    {
        float approach = 0f;
        if (dynamicA && _previousVelocities.TryGetValue(bodyA, out PhysicsVector3 previousA))
        {
            approach = MathF.Max(approach, -PhysicsVector3.Dot(previousA, normal));
        }

        if (dynamicB && _previousVelocities.TryGetValue(bodyB, out PhysicsVector3 previousB))
        {
            approach = MathF.Max(approach, PhysicsVector3.Dot(previousB, normal));
        }

        return approach;
    }

    /// <summary>Relative normal speed of the pair along <paramref name="normal"/>, positive when
    /// the pair is separating. Read from this tick's snapshots, i.e. after the solver ran, which
    /// is what the bounce target has to be measured against.</summary>
    private float RelativeNormalSpeed(PhysicsBodyId bodyA, PhysicsBodyId bodyB, PhysicsVector3 normal)
    {
        PhysicsVector3 velocityA = _kinematicsByBody.TryGetValue(bodyA.Value, out var kinematicsA)
            ? kinematicsA.Velocity
            : PhysicsVector3.Zero;
        PhysicsVector3 velocityB = _kinematicsByBody.TryGetValue(bodyB.Value, out var kinematicsB)
            ? kinematicsB.Velocity
            : PhysicsVector3.Zero;
        return PhysicsVector3.Dot(velocityA - velocityB, normal);
    }

    /// <summary>Drops bounce marks for bodies that are no longer touching anything, so a
    /// later touchdown bounces again.</summary>
    private void ReArmBounces()
    {
        if (_bouncedBodies.Count == 0)
        {
            return;
        }

        _bouncedBodies.RemoveWhere(body => !_touchedBodies.Contains(body));
    }

    /// <summary>Recomputes the power factor of the cluster a part belongs to (membership, switch
    /// and enclosure changes all funnel through here; never the per-tick path).</summary>
    private void RecomputePowerCluster(EntityId entity) => RecomputePowerCluster(PowerClusterKey(entity));

    /// <summary>Recomputes the cluster of the body this member just joined or left.</summary>
    private void RecomputeBodyCluster(PhysicsBodyId body)
    {
        if (_membersByBody.TryGetValue(body.Value, out List<uint>? members) && members.Count > 0)
        {
            RecomputePowerCluster(new EntityId(members[0]));
        }
    }

    /// <summary>Drops a destroyed part's power data and its cluster membership, and refreshes the
    /// cluster it left (a destroyed chassis can leave hinged parts pointed at it).</summary>
    private void ForgetPower(EntityId entity)
    {
        uint previousKey = PowerClusterKey(entity);
        _powers.Remove(entity);
        _powerHostByEntity.Remove(entity.Value);
        RecomputePowerCluster(new EntityId(previousKey));
    }

    /// <summary>
    /// The key of the cluster a part's power belongs to: itself unless it is a member of a physics
    /// body (then the body's representative, which is what makes a welded chassis one cluster) or
    /// has been linked to another part's cluster by the placement layer (a hinged wheel).
    /// </summary>
    private uint PowerClusterKey(EntityId entity)
    {
        uint key = entity.Value;
        for (int hops = 0; hops < PowerClusterHopLimit; hops++)
        {
            if (_powerHostByEntity.TryGetValue(key, out uint host))
            {
                key = host;
                continue;
            }

            if (_bodies.TryGet(new EntityId(key), out PhysicsBodyLink link)
                && _entitiesByBody.TryGetValue(link.Body.Value, out EntityId representative)
                && representative.Value != key)
            {
                key = representative.Value;
                continue;
            }

            break;
        }

        return key;
    }

    /// <summary>
    /// Recomputes one cluster's factor from its members: the engine power of every enclosed engine
    /// plus the consumption of every enabled consumer (Contraption.cs:1378-1379 sums the engines
    /// over the component, Contraption.cs:2633-2644 re-sums only the enabled consumers every step).
    /// </summary>
    private void RecomputePowerCluster(uint key)
    {
        float enginePower = 0f;
        float consumption = 0f;
        var powers = _powers.GetEnumerator();
        while (powers.MoveNext())
        {
            if (PowerClusterKey(powers.CurrentId) != key)
            {
                continue;
            }

            PowerState power = powers.CurrentValue;
            // Only an enclosed engine is a valid part at all (Engine.cs:61).
            if (power.EnginePower > 0f && power.EngineEnclosed)
            {
                enginePower += power.EnginePower;
            }

            if (power.PowerConsumption > 0f && IsDriven(powers.CurrentId))
            {
                consumption += power.PowerConsumption;
            }
        }

        _powerFactorByCluster[key] = ComputePowerFactor(enginePower, consumption);
    }

    /// <summary>Deterministic hash over every power-bearing part's cluster factor, in slot order:
    /// the factor is rules state no physics snapshot shows (a gated wheel emits no command at all).</summary>
    public long ComputePowerHash()
    {
        long hash = 17;
        var powers = _powers.GetEnumerator();
        while (powers.MoveNext())
        {
            hash = unchecked((hash * 31) + powers.CurrentId.Value.GetHashCode());
            hash = unchecked((hash * 31) + ClusterPowerFactor(powers.CurrentId).GetHashCode());
        }

        return hash;
    }

    /// <summary>Recomputes a body's elasticity and mass from its members: the compound keeps
    /// the strongest restitution in it and the sum of its members' masses.</summary>
    private void RecomputeBodyMaterial(PhysicsBodyId body)
    {
        if (!_membersByBody.TryGetValue(body.Value, out List<uint>? members) || members.Count == 0)
        {
            _membersByBody.Remove(body.Value);
            _restitutionByBody.Remove(body.Value);
            _massByBody.Remove(body.Value);
            _bouncedBodies.Remove(body.Value);
            return;
        }

        float restitution = 0f;
        float mass = 0f;
        for (int index = 0; index < members.Count; index++)
        {
            if (!_restitutions.TryGet(new EntityId(members[index]), out RestitutionState state))
            {
                continue;
            }

            restitution = MathF.Max(restitution, state.Restitution);
            mass += state.Mass;
        }

        _restitutionByBody[body.Value] = restitution;
        _massByBody[body.Value] = mass;
    }

    /// <summary>Drops one member from its cluster bookkeeping (seam split, detach, destroy).</summary>
    private void UnlinkBodyMember(EntityId entity, PhysicsBodyId body)
    {
        if (!_membersByBody.TryGetValue(body.Value, out List<uint>? members))
        {
            return;
        }

        members.Remove(entity.Value);
        RecomputeBodyMaterial(body);
        RecomputeBodyCluster(body);
    }

    private void RunMotors(GameplayTickOutput output)
    {
        HashSet<uint> reverseBodies = CollectGearboxBodies();
        var motors = _motors.GetEnumerator();
        while (motors.MoveNext())
        {
            if (!_bodies.TryGet(motors.CurrentId, out PhysicsBodyLink link)
                || !_kinematicsByBody.ContainsKey(link.Body.Value))
            {
                continue;
            }

            if (!IsDriven(motors.CurrentId))
            {
                continue;
            }

            // Wheel-driven motors only push while their wheel touches something this tick.
            if (_wheels.TryGet(motors.CurrentId, out _) && !_touchedBodies.Contains(link.Body.Value))
            {
                continue;
            }

            // Power gating (spec docs/specs/power-system.md §4 items 3-4): the original's engine
            // applies no force itself -- it only supplies its component (Engine.cs:29,138) -- and a
            // consumer's drive is scaled by the cluster's power factor, so it does not move at all
            // without an enclosed engine in that cluster (Contraption.cs:540-556, MotorWheel.cs:101-109).
            float powerFactor = 1f;
            if (_powers.TryGet(motors.CurrentId, out PowerState power))
            {
                if (power.PowerConsumption <= 0f)
                {
                    // The original's engine supplies its component and applies no force of its own
                    // (Engine.cs:29,138): it never drives, whatever content gives it.
                    continue;
                }

                powerFactor = ClusterPowerFactor(motors.CurrentId);
                if (powerFactor <= 0f)
                {
                    continue;
                }
            }

            MotorState motor = motors.CurrentValue;
            float directionX = motor.DirectionX;
            // A gearbox on the same body flips the drive (reverse gear).
            if (reverseBodies.Contains(link.Body.Value))
            {
                directionX = -directionX;
            }

            output.Commands.Add(PhysicsCommand.ApplyImpulse(
                link.Body,
                new PhysicsVector3(motor.ImpulsePerTick * directionX * powerFactor, 0f, 0f),
                _kinematicsByBody[link.Body.Value].Position));
        }
    }

    private HashSet<uint> CollectGearboxBodies()
    {
        HashSet<uint> bodies = new();
        var gearboxes = _gearboxes.GetEnumerator();
        while (gearboxes.MoveNext())
        {
            if (IsDriven(gearboxes.CurrentId) && _bodies.TryGet(gearboxes.CurrentId, out PhysicsBodyLink link))
            {
                bodies.Add(link.Body.Value);
            }
        }

        return bodies;
    }

    private void ChallengeEggsOnImpact(PhysicsBodyId body, float impactSpeed, GameplayTickOutput output)
    {
        if (impactSpeed < _config.EggBreakImpactSpeed
            || !_entitiesByBody.TryGetValue(body.Value, out EntityId entity)
            || !_eggs.TryGet(entity, out _))
        {
            return;
        }

        // A fragile egg cannot survive a hard landing: destroy it and demand a replay.
        DestroyEntity(entity, output);
        RestartRequested = true;
    }

    private void DetachOnImpact(PhysicsBodyId body, float impactSpeed, GameplayTickOutput output)
    {
        if (impactSpeed < _config.TntIgniteImpactSpeed
            || !_entitiesByBody.TryGetValue(body.Value, out EntityId entity)
            || !_detachers.TryGet(entity, out _))
        {
            return;
        }

        // A hard impact fires the detacher: the room splits the compound at the
        // nearest seam so this part leaves the rig (original detacher part).
        output.DetachedEntities.Add(entity);
    }

    private void RunBalloons(GameplayTickOutput output)
    {
        var balloons = _balloons.GetEnumerator();
        while (balloons.MoveNext())
        {
            if (!_bodies.TryGet(balloons.CurrentId, out PhysicsBodyLink link)
                || !_kinematicsByBody.ContainsKey(link.Body.Value))
            {
                continue;
            }

            // A balloon supplies buoyancy every tick it stays attached and able to
            // lift; it does not require ground contact (pure vertical lift). Its switch
            // deflates it: the part leaves the world and the lift stops.
            if (_activations.TryGet(balloons.CurrentId, out ActivationState balloonSwitch) && balloonSwitch.Active)
            {
                DestroyEntity(balloons.CurrentId, output);
                continue;
            }

            BalloonState balloon = balloons.CurrentValue;
            output.Commands.Add(PhysicsCommand.ApplyImpulse(
                link.Body,
                new PhysicsVector3(0f, balloon.LiftPerTick, 0f),
                _kinematicsByBody[link.Body.Value].Position));
        }
    }

    private void RunFans(GameplayTickOutput output)
    {
        var fans = _fans.GetEnumerator();
        while (fans.MoveNext())
        {
            if (!_bodies.TryGet(fans.CurrentId, out PhysicsBodyLink link)
                || !_kinematicsByBody.ContainsKey(link.Body.Value))
            {
                continue;
            }

            if (!IsDriven(fans.CurrentId))
            {
                continue;
            }

            FanState fan = fans.CurrentValue;
            float magnitude = PhysicsVector3.Distance(
                new PhysicsVector3(fan.DirectionX, fan.DirectionY, 0f), PhysicsVector3.Zero);
            if (magnitude <= float.Epsilon)
            {
                continue;
            }

            PhysicsVector3 direction = new(fan.DirectionX / magnitude, fan.DirectionY / magnitude, 0f);
            output.Commands.Add(PhysicsCommand.ApplyImpulse(
                link.Body,
                direction * fan.ImpulsePerTick,
                _kinematicsByBody[link.Body.Value].Position));
        }
    }

    private void RunAerodynamics(GameplayTickOutput output)
    {
        RunWings(output);
        RunTails(output);
        RunUmbrellas(output);
    }

    private void RunWings(GameplayTickOutput output)
    {
        var wings = _wings.GetEnumerator();
        while (wings.MoveNext())
        {
            if (!_bodies.TryGet(wings.CurrentId, out PhysicsBodyLink link)
                || !_kinematicsByBody.TryGetValue(link.Body.Value, out var kinematics))
            {
                continue;
            }

            WingState wing = wings.CurrentValue;
            // Lift grows with the square of horizontal speed (a glider only flies
            // while moving forward), capped so a single wing cannot hover.
            float lift = MathF.Min(wing.LiftCoef * kinematics.Velocity.X * kinematics.Velocity.X, wing.MaxLift);
            if (lift > float.Epsilon)
            {
                output.Commands.Add(PhysicsCommand.ApplyImpulse(
                    link.Body,
                    new PhysicsVector3(0f, lift, 0f),
                    kinematics.Position));
            }
        }
    }

    private void RunTails(GameplayTickOutput output)
    {
        var tails = _tails.GetEnumerator();
        while (tails.MoveNext())
        {
            if (!_bodies.TryGet(tails.CurrentId, out PhysicsBodyLink link)
                || !_kinematicsByBody.TryGetValue(link.Body.Value, out var kinematics))
            {
                continue;
            }

            TailState tail = tails.CurrentValue;
            // A tail damps velocity proportionally (no spin-down thrust, just
            // air resistance to keep a loaded glider stable).
            output.Commands.Add(PhysicsCommand.ApplyImpulse(
                link.Body,
                kinematics.Velocity * -tail.DragCoef,
                kinematics.Position));
        }
    }

    private void RunUmbrellas(GameplayTickOutput output)
    {
        var umbrellas = _umbrellas.GetEnumerator();
        while (umbrellas.MoveNext())
        {
            if (!_bodies.TryGet(umbrellas.CurrentId, out PhysicsBodyLink link)
                || !_kinematicsByBody.TryGetValue(link.Body.Value, out var kinematics))
            {
                continue;
            }

            if (!IsDriven(umbrellas.CurrentId))
            {
                continue;
            }

            UmbrellaState umbrella = umbrellas.CurrentValue;
            // Only while descending: the fall damper slows the drop as an upward
            // impulse; rising bodies are unaffected.
            if (kinematics.Velocity.Y < 0f)
            {
                output.Commands.Add(PhysicsCommand.ApplyImpulse(
                    link.Body,
                    new PhysicsVector3(0f, -kinematics.Velocity.Y * umbrella.DragCoef, 0f),
                    kinematics.Position));
            }
        }
    }

    private void RunSprings(GameplayTickOutput output)
    {
        var springs = _springs.GetEnumerator();
        while (springs.MoveNext())
        {
            if (!_bodies.TryGet(springs.CurrentId, out PhysicsBodyLink link)
                || !_kinematicsByBody.ContainsKey(link.Body.Value))
            {
                continue;
            }

            SpringState spring = springs.CurrentValue;
            bool touched = _touchedBodies.Contains(link.Body.Value);
            if (touched && !spring.BouncedRecently)
            {
                _springs.Set(springs.CurrentId, spring with { BouncedRecently = true });
                output.Commands.Add(PhysicsCommand.ApplyImpulse(
                    link.Body,
                    new PhysicsVector3(0f, spring.BounceImpulsePerTick, 0f),
                    _kinematicsByBody[link.Body.Value].Position));
            }
            else if (!touched && spring.BouncedRecently)
            {
                _springs.Set(springs.CurrentId, spring with { BouncedRecently = false });
            }
        }
    }

    private void RunBellows(GameplayTickOutput output)
    {
        var bellows = _bellows.GetEnumerator();
        while (bellows.MoveNext())
        {
            if (!_bodies.TryGet(bellows.CurrentId, out PhysicsBodyLink link)
                || !_kinematicsByBody.ContainsKey(link.Body.Value))
            {
                continue;
            }

            BellowsState state = bellows.CurrentValue;
            bool switched = HasSwitch(bellows.CurrentId);
            bool touched = _touchedBodies.Contains(link.Body.Value);
            if (state.BoostedRecently)
            {
                // Legacy content re-arms on lift-off; a switched part stays spent.
                if (!switched && !touched)
                {
                    _bellows.Set(bellows.CurrentId, state with { BoostedRecently = false });
                }

                continue;
            }

            bool fire = switched ? TryConsumeTrigger(bellows.CurrentId) : touched;
            if (!fire)
            {
                continue;
            }

            // A landing (legacy content) or the switch fires the jet: one forward boost
            // along facing +X (original bellows m_boostForce one-shot) until the rig lifts off.
            _bellows.Set(bellows.CurrentId, state with { BoostedRecently = true });
            output.Commands.Add(PhysicsCommand.ApplyImpulse(
                link.Body,
                new PhysicsVector3(state.BoostImpulse, 0f, 0f),
                _kinematicsByBody[link.Body.Value].Position));
        }
    }

    private void RunGrapples(GameplayTickOutput output)
    {
        var grapples = _grapples.GetEnumerator();
        while (grapples.MoveNext())
        {
            if (!_bodies.TryGet(grapples.CurrentId, out PhysicsBodyLink link)
                || !_kinematicsByBody.TryGetValue(link.Body.Value, out var kinematics))
            {
                continue;
            }

            GrappleState grapple = grapples.CurrentValue;
            bool switched = HasSwitch(grapples.CurrentId);
            bool touched = _touchedBodies.Contains(link.Body.Value);
            if (grapple.FiredRecently)
            {
                // Legacy content re-arms on lift-off; a switched part stays spent.
                if (!switched && !touched)
                {
                    _grapples.Set(grapples.CurrentId, grapple with { FiredRecently = false });
                }

                continue;
            }

            float magnitude = PhysicsVector3.Distance(
                new PhysicsVector3(grapple.DirectionX, grapple.DirectionY, 0f), PhysicsVector3.Zero);
            if (magnitude <= float.Epsilon)
            {
                continue;
            }

            bool fire = switched ? TryConsumeTrigger(grapples.CurrentId) : touched;
            if (!fire)
            {
                continue;
            }

            // A landing (legacy content) or the switch fires the hook toward its
            // direction: one strong pull impulse (cast + drag merged).
            _grapples.Set(grapples.CurrentId, grapple with { FiredRecently = true });
            PhysicsVector3 direction = new(grapple.DirectionX / magnitude, grapple.DirectionY / magnitude, 0f);
            output.Commands.Add(PhysicsCommand.ApplyImpulse(
                link.Body,
                direction * grapple.Impulse,
                kinematics.Position));
        }
    }

    private void RunDetachers(GameplayTickOutput output)
    {
        var detachers = _detachers.GetEnumerator();
        while (detachers.MoveNext())
        {
            // A switched detacher fires on its switch; legacy content keeps the
            // impact path in DetachOnImpact.
            if (TryConsumeTrigger(detachers.CurrentId))
            {
                output.DetachedEntities.Add(detachers.CurrentId);
            }
        }
    }

    private void RunRockets(GameplayTickOutput output)
    {
        List<(EntityId Entity, float Radius, float Impulse)> spent = null!;
        var rockets = _rockets.GetEnumerator();
        while (rockets.MoveNext())
        {
            if (!_bodies.TryGet(rockets.CurrentId, out PhysicsBodyLink link)
                || !_kinematicsByBody.ContainsKey(link.Body.Value))
            {
                continue;
            }

            RocketState rocket = rockets.CurrentValue;
            if (!rocket.Ignited)
            {
                // A switched rocket waits for its switch; legacy content auto-ignites.
                if (HasSwitch(rockets.CurrentId) && !TryConsumeTrigger(rockets.CurrentId))
                {
                    continue;
                }

                rocket = rocket with { Ignited = true };
            }
            if (rocket.DurationTicks == 0)
            {
                (spent ??= new List<(EntityId, float, float)>()).Add((rockets.CurrentId, rocket.ExplodeRadius, rocket.ExplodeImpulse));
                _rockets.Remove(rockets.CurrentId);
                continue;
            }

            _rockets.Set(rockets.CurrentId, rocket with { DurationTicks = checked((ushort)(rocket.DurationTicks - 1)) });
            float magnitude = PhysicsVector3.Distance(
                new PhysicsVector3(rocket.DirectionX, rocket.DirectionY, 0f), PhysicsVector3.Zero);
            if (magnitude <= float.Epsilon)
            {
                continue;
            }

            PhysicsVector3 direction = new(rocket.DirectionX / magnitude, rocket.DirectionY / magnitude, 0f);
            output.Commands.Add(PhysicsCommand.ApplyImpulse(
                link.Body,
                direction * rocket.ThrustPerTick,
                _kinematicsByBody[link.Body.Value].Position));
        }

        if (spent is null)
        {
            return;
        }

        foreach ((EntityId entity, float radius, float impulse) in spent)
        {
            if (radius > 0f && impulse > 0f
                && _bodies.TryGet(entity, out PhysicsBodyLink link)
                && _kinematicsByBody.TryGetValue(link.Body.Value, out var center))
            {
                RadialBlast(center.Position, radius, impulse, link.Body, output);
            }

            output.DestroyedEntities.Add(entity);
        }
    }

    /// <summary>Radial outward impulse on every dynamic body within range (TNT blast
    /// and firework bursts share this); the originator body is excluded.</summary>
    private void RadialBlast(PhysicsVector3 center, float radius, float impulse, PhysicsBodyId source, GameplayTickOutput output)
    {
        uint[] bodyIds = _kinematicsByBody.Keys.ToArray();
        Array.Sort(bodyIds);
        foreach (uint bodyId in bodyIds)
        {
            if (bodyId == source.Value
                || !_dynamicBodies.Contains(bodyId)
                || !_kinematicsByBody.TryGetValue(bodyId, out var kinematics))
            {
                continue;
            }

            float distance = PhysicsVector3.Distance(kinematics.Position, center);
            if (distance >= radius)
            {
                continue;
            }

            float falloff = 1f - (distance / radius);
            PhysicsVector3 direction = distance > float.Epsilon
                ? PhysicsVector3.Normalize(kinematics.Position - center)
                : new PhysicsVector3(0f, 1f, 0f);
            output.Commands.Add(PhysicsCommand.ApplyImpulse(
                new PhysicsBodyId(bodyId),
                direction * (impulse * falloff),
                kinematics.Position));
        }
    }

    private void RunTntFuses(GameplayTickOutput output)
    {
        List<uint> exploded = null!;
        var tntComponents = _tnt.GetEnumerator();
        while (tntComponents.MoveNext())
        {
            TntState state = tntComponents.CurrentValue;
            if (!state.Ignited && HasSwitch(tntComponents.CurrentId) && TryConsumeTrigger(tntComponents.CurrentId))
            {
                // The switch is the player's lighter (original OnTouch -> Explode);
                // impact ignition stays available unless content disabled it.
                state = state with { Ignited = true };
                _tnt.Set(tntComponents.CurrentId, state);
            }

            if (!state.Ignited)
            {
                continue;
            }

            if (state.FuseTicks > 0)
            {
                _tnt.Set(tntComponents.CurrentId, state with { FuseTicks = checked((ushort)(state.FuseTicks - 1)) });
                continue;
            }

            (exploded ??= new List<uint>()).Add(tntComponents.CurrentId.Value);
        }

        if (exploded is null)
        {
            return;
        }

        exploded.Sort();
        foreach (uint entityValue in exploded)
        {
            EntityId entity = new(entityValue);
            bool chain = _tnt.TryGet(entity, out TntState state) && state.ChainDetonate;
            // The source's body link disappears when it explodes, so the chain needs the
            // blast centre captured up front.
            PhysicsVector3 blastCenter = default;
            bool hasCenter = false;
            if (_bodies.TryGet(entity, out PhysicsBodyLink link)
                && _kinematicsByBody.TryGetValue(link.Body.Value, out var center))
            {
                blastCenter = center.Position;
                hasCenter = true;
            }
            Explode(entity, output);
            if (chain && hasCenter)
            {
                IgniteChargesInRadius(entity, blastCenter);
            }
        }
    }

    /// <summary>Original TNT chain: every other charge inside the blast radius lights up
    /// (its own fuse runs from the next tick). Sorted entity ids keep it deterministic;
    /// the chain never recurses inside the same tick.</summary>
    private void IgniteChargesInRadius(EntityId source, PhysicsVector3 center)
    {

        List<uint> ignited = null!;
        var charges = _tnt.GetEnumerator();
        while (charges.MoveNext())
        {
            if (charges.CurrentId == source || charges.CurrentValue.Ignited)
            {
                continue;
            }

            if (!_bodies.TryGet(charges.CurrentId, out PhysicsBodyLink chargeLink)
                || !_kinematicsByBody.TryGetValue(chargeLink.Body.Value, out var kinematics))
            {
                continue;
            }

            if (PhysicsVector3.Distance(kinematics.Position, center) < _config.TntBlastRadius)
            {
                (ignited ??= new List<uint>()).Add(charges.CurrentId.Value);
            }
        }

        if (ignited is null)
        {
            return;
        }

        ignited.Sort();
        foreach (uint entityValue in ignited)
        {
            EntityId entity = new(entityValue);
            if (_tnt.TryGet(entity, out TntState state))
            {
                _tnt.Set(entity, state with { Ignited = true });
            }
        }
    }

    /// <summary>One-shot shockwave: a triggered blaster pushes every dynamic body inside its
    /// radius once, keeps itself alive (spent), and sets off blasters inside its chain radius.
    /// Worklist is sorted per hop so replay order is deterministic.</summary>
    private void RunBlasters(GameplayTickOutput output)
    {
        List<EntityId> fired = null!;
        var blasters = _blasters.GetEnumerator();
        while (blasters.MoveNext())
        {
            if (!blasters.CurrentValue.Spent && TryConsumeTrigger(blasters.CurrentId))
            {
                (fired ??= new List<EntityId>()).Add(blasters.CurrentId);
            }
        }

        if (fired is null)
        {
            return;
        }

        HashSet<uint> handled = new();
        while (fired.Count > 0)
        {
            List<EntityId> next = new();
            foreach (EntityId entity in fired.OrderBy(candidate => candidate.Value))
            {
                if (!handled.Add(entity.Value)
                    || !_blasters.TryGet(entity, out BlasterState blaster)
                    || blaster.Spent)
                {
                    continue;
                }

                // The charge survives as a spent husk (original BlasterTNT keeps its part).
                _blasters.Set(entity, blaster with { Spent = true });
                TryConsumeTrigger(entity);
                if (!_bodies.TryGet(entity, out PhysicsBodyLink link)
                    || !_kinematicsByBody.TryGetValue(link.Body.Value, out var center))
                {
                    continue;
                }

                RadialBlast(center.Position, blaster.Radius, blaster.Impulse, link.Body, output);

                if (blaster.ChainRadius <= 0f)
                {
                    continue;
                }

                var neighbours = _blasters.GetEnumerator();
                while (neighbours.MoveNext())
                {
                    if (neighbours.CurrentValue.Spent
                        || !_bodies.TryGet(neighbours.CurrentId, out PhysicsBodyLink neighbourLink)
                        || !_kinematicsByBody.TryGetValue(neighbourLink.Body.Value, out var neighbour))
                    {
                        continue;
                    }

                    if (PhysicsVector3.Distance(neighbour.Position, center.Position) < blaster.ChainRadius)
                    {
                        next.Add(neighbours.CurrentId);
                    }
                }
            }

            fired = next;
        }
    }

    private void Explode(EntityId tntEntity, GameplayTickOutput output)
    {
        if (!_bodies.TryGet(tntEntity, out PhysicsBodyLink link)
            || !_kinematicsByBody.TryGetValue(link.Body.Value, out var center))
        {
            return;
        }

        // Sorted body ids keep the command order deterministic across replays.
        uint[] bodyIds = _kinematicsByBody.Keys.ToArray();
        Array.Sort(bodyIds);
        foreach (uint bodyId in bodyIds)
        {
            if (bodyId == link.Body.Value
                || !_dynamicBodies.Contains(bodyId)
                || !_kinematicsByBody.TryGetValue(bodyId, out var kinematics))
            {
                continue;
            }

            float distance = PhysicsVector3.Distance(kinematics.Position, center.Position);
            if (distance >= _config.TntBlastRadius)
            {
                continue;
            }

            float falloff = 1f - (distance / _config.TntBlastRadius);
            PhysicsVector3 direction = distance > float.Epsilon
                ? PhysicsVector3.Normalize(kinematics.Position - center.Position)
                : new PhysicsVector3(0f, 1f, 0f);
            // Impulse at the target's own centre of mass: blasts push, they do not spin.
            output.Commands.Add(PhysicsCommand.ApplyImpulse(
                new PhysicsBodyId(bodyId),
                direction * (_config.TntBlastImpulse * falloff),
                kinematics.Position));
        }

        DestroyEntity(tntEntity, output);
    }

    private void DropCommandsForDestroyedBodies(GameplayTickOutput output)
    {
        if (output.Commands.Count > 0)
        {
            // A pair command survives while either end is still a live dynamic body: the bounce's
            // suppression names the static floor too, and dropping it there would silently undo
            // the bounce. Single-body commands keep their original rule.
            output.Commands.RemoveAll(command => command.Kind == PhysicsCommandKind.SuppressContact
                ? !_dynamicBodies.Contains(command.Body.Value) && !_dynamicBodies.Contains(command.SecondBody.Value)
                : !_dynamicBodies.Contains(command.Body.Value));
        }
    }

    private void CheckObjectives(uint tick)
    {
        if (!_config.ObjectivesEnabled)
        {
            return;
        }

        var pigs = _pigs.GetEnumerator();
        while (pigs.MoveNext())
        {
            if (!_bodies.TryGet(pigs.CurrentId, out PhysicsBodyLink link)
                || !_kinematicsByBody.TryGetValue(link.Body.Value, out var kinematics))
            {
                continue;
            }

            if (_config.GoalZone.Contains(kinematics.Position))
            {
                Phase = GameplayPhase.Won;
                return;
            }

            if (!_config.MapBounds.Contains(kinematics.Position))
            {
                Phase = GameplayPhase.Failed;
                RestartRequested = true;
                return;
            }
        }

        if (_config.MaxTicks > 0 && tick >= _config.MaxTicks)
        {
            Phase = GameplayPhase.Failed;
        }
    }

    /// <summary>
    /// Clears every gameplay store entry for an entity that was destroyed by another
    /// owner (e.g. construction rules removing a part). Adjusts the live-pig count.
    /// </summary>
    public void CleanupEntityStores(EntityId entity)
    {
        if (_pigs.TryGet(entity, out _))
        {
            _alivePigs--;
        }

        if (_bodies.TryGet(entity, out PhysicsBodyLink link))
        {
            UnlinkBodyMember(entity, link.Body);
            _entitiesByBody.Remove(link.Body.Value);
            _dynamicBodies.Remove(link.Body.Value);
        }

        _motors.Remove(entity);
        _balloons.Remove(entity);
        _fans.Remove(entity);
        _springs.Remove(entity);
        _rockets.Remove(entity);
        _tnt.Remove(entity);
        _wheels.Remove(entity);
        _pigs.Remove(entity);
        _eggs.Remove(entity);
        _wings.Remove(entity);
        _tails.Remove(entity);
        _umbrellas.Remove(entity);
        _gearboxes.Remove(entity);
        _bellows.Remove(entity);
        _detachers.Remove(entity);
        _grapples.Remove(entity);
        _blasters.Remove(entity);
        _glues.Remove(entity);
        _activations.Remove(entity);
        _restitutions.Remove(entity);
        ForgetPower(entity);
    }

    /// <summary>
    /// Prepares a fresh run after re-entering build mode with the keep policy: per-body
    /// telemetry is dropped (bodies are recreated and re-linked on the next Start),
    /// charges are re-armed with their remaining fuse, and the outcome reverts to
    /// playing. Frozen entities keep their roles, pig count and fuse remainders.
    /// </summary>
    public void ResetForRebuild()
    {
        ClearBodyMaterials();
        _entitiesByBody.Clear();
        _dynamicBodies.Clear();
        _kinematicsByBody.Clear();
        _previousVelocities.Clear();
        _touchedBodies.Clear();
        _brokenJoints.Clear();
        var tntComponents = _tnt.GetEnumerator();
        while (tntComponents.MoveNext())
        {
            _tnt.Set(tntComponents.CurrentId, tntComponents.CurrentValue with { Ignited = false });
        }

        var rockets = _rockets.GetEnumerator();
        while (rockets.MoveNext())
        {
            _rockets.Set(rockets.CurrentId, rockets.CurrentValue with { Ignited = false });
        }

        var blasters = _blasters.GetEnumerator();
        while (blasters.MoveNext())
        {
            if (blasters.CurrentValue.Spent)
            {
                _blasters.Set(blasters.CurrentId, blasters.CurrentValue with { Spent = false });
            }
        }

        var activations = _activations.GetEnumerator();
        while (activations.MoveNext())
        {
            if (activations.CurrentValue.Active)
            {
                _activations.Set(activations.CurrentId, activations.CurrentValue with { Active = false });
            }
        }

        var springs = _springs.GetEnumerator();
        while (springs.MoveNext())
        {
            if (springs.CurrentValue.BouncedRecently)
            {
                _springs.Set(springs.CurrentId, springs.CurrentValue with { BouncedRecently = false });
            }
        }

        var bellows = _bellows.GetEnumerator();
        while (bellows.MoveNext())
        {
            if (bellows.CurrentValue.BoostedRecently)
            {
                _bellows.Set(bellows.CurrentId, bellows.CurrentValue with { BoostedRecently = false });
            }
        }

        var grapples = _grapples.GetEnumerator();
        while (grapples.MoveNext())
        {
            if (grapples.CurrentValue.FiredRecently)
            {
                _grapples.Set(grapples.CurrentId, grapples.CurrentValue with { FiredRecently = false });
            }
        }

        Phase = GameplayPhase.Playing;
        RestartRequested = false;
    }

    /// <summary>Drops all gameplay state for a full level reset (clear policy).</summary>
    public void ResetAll()
    {
        ClearBodyMaterials();
        _entitiesByBody.Clear();
        _dynamicBodies.Clear();
        _kinematicsByBody.Clear();
        _previousVelocities.Clear();
        _touchedBodies.Clear();
        _brokenJoints.Clear();
        _motors.Clear();
        _balloons.Clear();
        _fans.Clear();
        _springs.Clear();
        _rockets.Clear();
        _tnt.Clear();
        _wheels.Clear();
        _pigs.Clear();
        _eggs.Clear();
        _wings.Clear();
        _tails.Clear();
        _umbrellas.Clear();
        _gearboxes.Clear();
        _bellows.Clear();
        _detachers.Clear();
        _grapples.Clear();
        _blasters.Clear();
        _glues.Clear();
        _activations.Clear();
        _bodies.Clear();
        _powers.Clear();
        _alivePigs = 0;
        Phase = GameplayPhase.Playing;
    }

    private void DestroyEntity(EntityId entity, GameplayTickOutput output)
    {
        if (_pigs.TryGet(entity, out _))
        {
            _alivePigs--;
        }

        if (_bodies.TryGet(entity, out PhysicsBodyLink link))
        {
            UnlinkBodyMember(entity, link.Body);
            _entitiesByBody.Remove(link.Body.Value);
            _dynamicBodies.Remove(link.Body.Value);
        }

        _motors.Remove(entity);
        _balloons.Remove(entity);
        _fans.Remove(entity);
        _springs.Remove(entity);
        _rockets.Remove(entity);
        _tnt.Remove(entity);
        _wheels.Remove(entity);
        _pigs.Remove(entity);
        _eggs.Remove(entity);
        _wings.Remove(entity);
        _tails.Remove(entity);
        _umbrellas.Remove(entity);
        _gearboxes.Remove(entity);
        _bellows.Remove(entity);
        _detachers.Remove(entity);
        _grapples.Remove(entity);
        _blasters.Remove(entity);
        _glues.Remove(entity);
        _activations.Remove(entity);
        _restitutions.Remove(entity);
        ForgetPower(entity);
        _bodies.Remove(entity);
        _entities.Destroy(entity);
        output.DestroyedEntities.Add(entity);
    }

    private void ClearBodyMaterials()
    {
        _membersByBody.Clear();
        _restitutionByBody.Clear();
        _massByBody.Clear();
        _bouncedBodies.Clear();
        _peakApproachByBody.Clear();
        _powerFactorByCluster.Clear();
        _powerHostByEntity.Clear();
    }
}
