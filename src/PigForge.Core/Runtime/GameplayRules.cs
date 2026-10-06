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
    private readonly Dictionary<uint, PhysicsQuaternion> _rotationByBody = new();
    // A member's pose inside its body (the compound's local offset), so a rule can resolve the
    // part's own world position: a blast's origin is the charge part, not the body's centre of
    // mass it shares with the rest of the contraption (original TNT.transform.position).
    private readonly Dictionary<uint, PhysicsVector3> _localOffsetByEntity = new();
    // The same member's rotation inside its body. The original reads a part's thrust (and its
    // other directional effects) off the part's own transform -- `transform.TransformDirection`
    // in FanPropeller.cs:155 -- so a rule that only knows the compound's rotation would aim a
    // fan the way it was never built.
    private readonly Dictionary<uint, PhysicsQuaternion> _localRotationByEntity = new();

    /// <summary>
    /// The build pose's handedness per entity (ADR-030). The pose quaternion already carries it --
    /// a mirrored part's frame is turned 180 degrees about its own up axis -- but the wing and the
    /// tail read it <em>again</em> as <c>IsFlipped()</c> in their angle-of-attack terms
    /// (Wings.cs:111, Tail.cs:64,68), and a live physics pose can no longer be asked which
    /// handedness it was built with. Published by <see cref="LinkBody"/> at bind time.
    /// </summary>
    private readonly HashSet<uint> _mirroredByEntity = new();
    // A rotor's own axis at the moment it spawned (the original's `m_originalDirection` /
    // `m_rotorTargetDirection`, FanPropeller.cs:73-79, read once in `Initialize()`), keyed by
    // entity. Its thrust is blended back toward it while the live axis still points the same way
    // (FanPropeller.cs:156-161), which is what keeps a rotor pushing where it was built instead of
    // following every tilt of the rig.
    private readonly Dictionary<uint, PhysicsVector3> _fanSpawnDirectionByEntity = new();
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
    // Propulsion parts the placement layer found WITHOUT a chassis neighbour (see
    // ConstructionRules.HasChassisNeighbor). The original rejects such a part in ValidatePart
    // (BasePropulsion.cs:13-20, Wings.cs:14-31, Tail.cs:12-29), so it never fires; PigForge lets
    // the player build it but it emits no force. Absence means anchored, which keeps level actors
    // and unit harnesses ungated -- the placement layer declares the exceptions at materialisation.
    private readonly HashSet<uint> _unanchoredPropulsion = new();

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

    /// <summary>The original states a FanPropeller's thrust and its rotor overspeed brake as
    /// per-second forces (FanPropeller.cs:198-207,209) and PigForge applies one impulse per tick,
    /// so both convert through the tick duration -- the same division content uses for the balloon
    /// family and every FanPropeller value (ADR-013 decision 4, tools/bple-fans and tools/bple-lift).
    /// The room ticks at 60 Hz (<c>GameRoomOptions.TickRateHz</c>, PlayHost); a room built at
    /// another rate would need this to follow it.</summary>
    private const float ForceSecondsPerImpulse = 1f / 60f;

    /// <summary>Floor that keeps a massless part from producing an unbounded bounce impulse.</summary>
    private const float MinimumPartMass = 0.001f;

    /// <summary>A driven wheel's top speed in world units per second per unit of engine power
    /// factor: <c>m_maximumSpeed = 15 * enginePowerFactor</c> (MotorWheel.cs:103, StickyWheel.cs
    /// through the same override chain). At the cap the wheel emits nothing at all; below it the
    /// force tapers as <c>sqrt(1 - |v| / max)</c> (MotorWheel.cs:292-299), so a driven rig tops
    /// out instead of accelerating forever (gap list G24).</summary>
    private const float MotorWheelMaximumSpeed = 15f;

    /// <summary>A landing slower than this is not an impact worth bouncing (matches the order of
    /// the level's own impact thresholds).</summary>
    private const float MinimumBounceApproachSpeed = 0.5f;

    /// <summary>
    /// The puff's own length: the original pushes while <c>num &lt; 0.5 s</c>
    /// (<c>Bellows.cs:14,100-110</c>), then the belt goes quiet for the rest of the cycle.
    /// </summary>
    private const uint BellowsBoostTicks = 30;

    /// <summary>The original's 0.3 s wait after the puff (:15); together with the puff and the
    /// skin's own inflate duration it is what <c>OnTouch</c> waits out before it accepts a fresh
    /// press (:123-125).</summary>
    private const uint BellowsWaitTicks = 18;

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

    public void LinkBody(
        EntityId entity,
        PhysicsBodyId body,
        bool isDynamic = true,
        PhysicsVector3 localOffset = default,
        PhysicsQuaternion? localRotation = null,
        bool mirrored = false)
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

        if (localOffset != PhysicsVector3.Zero)
        {
            _localOffsetByEntity[entity.Value] = localOffset;
        }
        else
        {
            _localOffsetByEntity.Remove(entity.Value);
        }

        if (localRotation is PhysicsQuaternion rotation && rotation != PhysicsQuaternion.Identity)
        {
            _localRotationByEntity[entity.Value] = rotation;
        }
        else
        {
            _localRotationByEntity.Remove(entity.Value);
        }

        if (mirrored)
        {
            _mirroredByEntity.Add(entity.Value);
        }
        else
        {
            _mirroredByEntity.Remove(entity.Value);
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

    /// <summary>
    /// Drops an entity's body bookkeeping after the room unbound it (seam split, detach,
    /// destroy). The body keeps its dynamic flag while it still holds members, so an impulse
    /// already aimed at the surviving body is not discarded by the orphan filter.
    /// </summary>
    public void UnbindBody(EntityId entity)
    {
        if (_bodies.TryGet(entity, out PhysicsBodyLink link))
        {
            UnlinkBodyMember(entity, link.Body);
        }

        _localOffsetByEntity.Remove(entity.Value);
        _localRotationByEntity.Remove(entity.Value);
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
    /// Declares whether a propulsion part is attached to the chassis (see
    /// <see cref="PigForge.Core.Construction.ConstructionRules.HasChassisNeighbor"/>). The
    /// original refuses a propulsion part without a chassis neighbour outright —
    /// <c>BasePropulsion.ValidatePart</c> returns <c>neighbourCount &gt;= 1</c>
    /// (BasePropulsion.cs:13-20), and <c>Wings</c>/<c>Tail</c> repeat the loop (Wings.cs:14-31,
    /// Tail.cs:12-29); <c>Frame.IsPartOfChassis()</c> is what makes a neighbour count
    /// (Frame.cs:37-40, BasePart.cs:1169). PigForge keeps the part buildable and only strips its
    /// force, mirroring the "engine outside a frame supplies nothing" runtime treatment. Parts the
    /// layer never declares (level actors) stay anchored, i.e. ungated.
    /// </summary>
    public void SetChassisAnchored(EntityId entity, bool anchored)
    {
        if (anchored)
        {
            _unanchoredPropulsion.Remove(entity.Value);
        }
        else
        {
            _unanchoredPropulsion.Add(entity.Value);
        }
    }

    /// <summary>True unless the placement layer found this part without a chassis neighbour.</summary>
    public bool IsChassisAnchored(EntityId entity) => !_unanchoredPropulsion.Contains(entity.Value);

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
        uint previousKey = PowerClusterKey(member);
        if (previousKey == hostKey)
        {
            return;
        }

        // Every member of the member's body joins the host's cluster, not just the part that
        // declares the edge: LinkBody keeps the LAST member a body was bound with as its
        // representative, so a lone entry could be shadowed by that representative and the link
        // would silently never apply — a frame whose body also holds an enclosed engine is exactly
        // that case, and an engine in the next frame of a weld chain would then power nothing.
        if (_bodies.TryGet(member, out PhysicsBodyLink link)
            && _membersByBody.TryGetValue(link.Body.Value, out List<uint>? members))
        {
            for (int index = 0; index < members.Count; index++)
            {
                _powerHostByEntity[members[index]] = hostKey;
            }
        }
        else
        {
            _powerHostByEntity[member.Value] = hostKey;
        }

        RecomputePowerCluster(new EntityId(previousKey));
        RecomputePowerCluster(new EntityId(hostKey));
    }

    /// <summary>
    /// Drops an extra power-cluster edge after the joint that carried it went away — a hinged wheel
    /// that came off, or a frame weld that broke — and refreshes the cluster the member leaves.
    /// The original re-unions the whole joint graph on every change (Contraption.cs:1293); an edge
    /// stored downstream of this one keeps the key this edge gave it, so the caller re-links the
    /// edges that remain (see GameRoom.BreakWeld).
    /// </summary>
    public void UnlinkPowerCluster(EntityId member)
    {
        uint previousKey = PowerClusterKey(member);
        bool removed = false;
        if (_bodies.TryGet(member, out PhysicsBodyLink link)
            && _membersByBody.TryGetValue(link.Body.Value, out List<uint>? members))
        {
            for (int index = 0; index < members.Count; index++)
            {
                removed |= _powerHostByEntity.Remove(members[index]);
            }
        }
        else
        {
            removed = _powerHostByEntity.Remove(member.Value);
        }

        if (!removed)
        {
            return;
        }

        uint currentKey = PowerClusterKey(member);
        if (previousKey != currentKey)
        {
            RecomputePowerCluster(new EntityId(previousKey));
        }

        RecomputePowerCluster(new EntityId(currentKey));
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

    /// <summary>
    /// Resolves whether a powered part drives this tick and with what multiplier — the one rule
    /// every consumer uses (spec docs/specs/power-system.md §4 items 3-4): a part with no power
    /// data drives unconditionally (legacy content); a part that declares power data but consumes
    /// nothing is an engine, and the original's engine applies no force of its own
    /// (Engine.cs:29,138), so it never drives; a consumer is scaled by its cluster's power factor
    /// and stops dead when the cluster holds no enclosed engine (Contraption.cs:540-556). This is
    /// distinct from the switch state: <see cref="IsDriven"/> is "the player turned it on", the
    /// factor being 0 is "there is no power in this contraption" — the original keeps the two
    /// apart as <c>m_enabled</c> (FanPropeller.cs:209) and <c>powerFactor</c>
    /// (FanPropeller.cs:85-92), and so do we.
    /// </summary>
    private bool TryDriveFactor(EntityId entity, out float factor)
    {
        factor = 1f;
        if (!_powers.TryGet(entity, out PowerState power))
        {
            return true;
        }

        if (power.PowerConsumption <= 0f)
        {
            return false;
        }

        factor = ClusterPowerFactor(entity);
        return factor > 0f;
    }

    public void AddBalloon(EntityId entity, float liftPerTick) =>
        _balloons.Set(entity, new BalloonState(liftPerTick));

    public void AddFan(EntityId entity, float impulsePerTick, float directionX, float directionY, float maxSpeed = 0f, bool isRotor = false) =>
        _fans.Set(entity, new FanState(impulsePerTick, directionX, directionY, maxSpeed, isRotor));

    public void AddRocket(
        EntityId entity,
        float thrustPerTick,
        float directionX,
        float directionY,
        ushort ignitionTicks,
        ushort boostTicks,
        ushort endTicks,
        float maxSpeed,
        bool visualization,
        float explodeRadius = 0f,
        float explodeImpulse = 0f) =>
        _rockets.Set(entity, new RocketState(
            thrustPerTick,
            directionX,
            directionY,
            ignitionTicks,
            boostTicks,
            endTicks,
            maxSpeed,
            visualization,
            Ignited: false,
            ElapsedTicks: 0u,
            explodeRadius,
            explodeImpulse));

    public void AddEgg(EntityId entity) => _eggs.Set(entity, default);

    public void AddWing(EntityId entity, float liftConstant) =>
        _wings.Set(entity, new WingState(liftConstant));

    public void AddTail(EntityId entity, float liftConstant) =>
        _tails.Set(entity, new TailState(liftConstant));

    public void AddUmbrella(EntityId entity, float dragCoef) =>
        _umbrellas.Set(entity, new UmbrellaState(dragCoef));

    public void AddGearbox(EntityId entity) => _gearboxes.Set(entity, default);

    public void AddBellows(EntityId entity, float thrustPerTick, float directionX, float directionY, ushort inflateTicks) =>
        _bellows.Set(entity, new BellowsState(
            thrustPerTick,
            directionX,
            directionY,
            inflateTicks,
            Active: false,
            ElapsedTicks: 0u,
            ReadyAtTick: 0u));

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

    /// <summary>
    /// Consumes a momentary button press (content <c>activation: "trigger"</c>): false when the
    /// part carries no activation (the caller falls back to its legacy contact rule) or the button
    /// is not pressed. The press is spent the moment it is read, before any of the part's own
    /// gates, so a button whose effect is refused cannot stay latched on in the switch bar -- the
    /// original's bar button is <c>BasePart.OnButtonTriggered</c> -&gt; <c>ProcessTouch()</c>
    /// (<c>BasePart.cs:1428</c>), a one-shot, never a level (docs/specs/play-part-switches.md
    /// Assumption 2).
    /// </summary>
    public bool TryConsumeButtonPress(EntityId entity)
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
            _rotationByBody[snapshot.Body.Value] = snapshot.Rotation;
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

        // Re-arm guard: while a body keeps touching something it must not bounce once per tick.
        // Cleared by ReArmBounces the moment the body leaves contact.
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

    /// <summary>
    /// Drops one member from its cluster bookkeeping (seam split, detach, destroy). A body that
    /// still holds members keeps its dynamic flag and hands its representative entity to the next
    /// member: the entity lookups (impact ignition, egg break, detacher trigger) and the blast
    /// filters all key on those maps, so clearing a body that still has a live part would silently
    /// drop that part's role and any impulse aimed at the surviving body. Only an orphan body
    /// loses its entries, mirroring <c>GameRoom.UnbindEntity</c>.
    /// </summary>
    private void UnlinkBodyMember(EntityId entity, PhysicsBodyId body)
    {
        _localOffsetByEntity.Remove(entity.Value);
        _localRotationByEntity.Remove(entity.Value);
        _mirroredByEntity.Remove(entity.Value);
        _fanSpawnDirectionByEntity.Remove(entity.Value);
        if (_membersByBody.TryGetValue(body.Value, out List<uint>? members))
        {
            members.Remove(entity.Value);
            RecomputeBodyMaterial(body);
            RecomputeBodyCluster(body);
            if (_membersByBody.TryGetValue(body.Value, out List<uint>? remaining) && remaining.Count > 0)
            {
                if (!_entitiesByBody.TryGetValue(body.Value, out EntityId primary) || primary == entity)
                {
                    _entitiesByBody[body.Value] = new EntityId(remaining[0]);
                }

                return;
            }
        }

        _entitiesByBody.Remove(body.Value);
        _dynamicBodies.Remove(body.Value);
        _rotationByBody.Remove(body.Value);
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

            // Power gating (spec docs/specs/power-system.md §4 items 3-4): the drive is scaled by
            // the cluster's power factor, so a wheel does not move at all without an enclosed
            // engine in that cluster (Contraption.cs:540-556, MotorWheel.cs:101-109).
            if (!TryDriveFactor(motors.CurrentId, out float powerFactor))
            {
                continue;
            }

            MotorState motor = motors.CurrentValue;
            float directionX = motor.DirectionX;
            // A gearbox on the same body flips the drive (reverse gear).
            if (reverseBodies.Contains(link.Body.Value))
            {
                directionX = -directionX;
            }

            // MotorWheel.cs:292-299 gates the drive on the speed along the wheel's own axis --
            // `num2 < m_maximumSpeed && num2 > -m_maximumSpeed` -- and tapers it as
            // sqrt(1 - |num2| / m_maximumSpeed), so the rig approaches the cap asymptotically and
            // never passes it. The cap itself is 15 * powerFactor (MotorWheel.cs:101-103).
            float maximumSpeed = MotorWheelMaximumSpeed * powerFactor;
            float axialSpeed = _kinematicsByBody[link.Body.Value].Velocity.X;
            if (MathF.Abs(axialSpeed) >= maximumSpeed)
            {
                continue;
            }

            float thrust = motor.ImpulsePerTick * directionX * powerFactor
                * MathF.Sqrt(1f - (MathF.Abs(axialSpeed) / maximumSpeed));

            output.Commands.Add(PhysicsCommand.ApplyImpulse(
                link.Body,
                new PhysicsVector3(thrust, 0f, 0f),
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
            // Balloon.cs carries no chassis gate and no power term, so a balloon is pure unpowered
            // cargo lift and is never gated. The rotor used to ride this path as a powered balloon;
            // it is a FanPropeller like the fan and the propeller, and now runs through RunFans with
            // its thrust capped instead (docs/specs/fan-propeller.md).
            output.Commands.Add(PhysicsCommand.ApplyImpulse(
                link.Body,
                new PhysicsVector3(0f, balloon.LiftPerTick, 0f),
                _kinematicsByBody[link.Body.Value].Position));
        }
    }

    /// <summary>
    /// Resolves the part's own frame inside its body: where the member sits in world space
    /// (<c>body.Position + bodyRotation * localOffset</c>) and the rotation its original
    /// <c>transform</c> carries there (<c>bodyRotation * localRotation</c>).
    /// </summary>
    /// <remarks>
    /// Every rule that reads a content direction goes through this frame, because the original
    /// hands those values to <c>transform.TransformDirection</c> -- <c>FanPropeller.cs:155</c> and
    /// <c>:209</c>, <c>Bellows.cs:84-87</c>, <c>Rocket.cs:298-300</c>,
    /// <c>GrapplingHook.cs:467</c> -- which carries both the build rotation
    /// (<c>Contraption.SetRotation</c>, a z rotation, so a part built a quarter turn round pushes a
    /// quarter turn round) and the rig's live rotation. A content direction applied as a world axis
    /// ignores how the player aimed the part.
    /// </remarks>
    private void ResolvePartFrame(
        EntityId entity,
        uint body,
        PhysicsVector3 bodyPosition,
        out PhysicsVector3 partPosition,
        out PhysicsQuaternion partRotation)
    {
        PhysicsQuaternion bodyRotation = _rotationByBody.TryGetValue(body, out PhysicsQuaternion bodyPose)
            ? bodyPose
            : PhysicsQuaternion.Identity;
        partPosition = bodyPosition;
        if (_localOffsetByEntity.TryGetValue(entity.Value, out PhysicsVector3 partOffset))
        {
            partPosition += bodyRotation.Rotate(partOffset);
        }

        partRotation = _localRotationByEntity.TryGetValue(entity.Value, out PhysicsQuaternion localRotation)
            ? bodyRotation * localRotation
            : bodyRotation;
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

            FanState fan = fans.CurrentValue;
            PhysicsVector3 contentAxis = new(fan.DirectionX, fan.DirectionY, 0f);
            float magnitude = PhysicsVector3.Distance(contentAxis, PhysicsVector3.Zero);
            if (magnitude <= float.Epsilon)
            {
                continue;
            }

            // The thrust axis is the part's own (FanPropeller.cs:155), so the build rotation and the
            // rig's live rotation both aim it -- see ResolvePartFrame.
            ResolvePartFrame(
                fans.CurrentId,
                link.Body.Value,
                _kinematicsByBody[link.Body.Value].Position,
                out PhysicsVector3 partPosition,
                out PhysicsQuaternion partRotation);
            PhysicsVector3 axis = partRotation.Rotate(contentAxis * (1f / magnitude));
            PhysicsVector3 direction = axis;

            // FanPropeller.cs:73-79 keeps the axis the part had when it was created
            // (`m_originalDirection` / `m_rotorTargetDirection`, with a downward one clamped to
            // +y) and :156-161 blends it back in at half weight while the live axis still points
            // the same way, so a rotor keeps pushing where it was built instead of following every
            // tilt. Recorded on the part's first simulated tick -- before the switch is read, so a
            // rotor that starts switched off still captures the build pose `Initialize()` saw
            // rather than whatever tilt it settled into.
            if (fan.IsRotor)
            {
                if (!_fanSpawnDirectionByEntity.TryGetValue(fans.CurrentId.Value, out PhysicsVector3 target))
                {
                    target = axis.Y < 0f ? new PhysicsVector3(axis.X, 1f, axis.Z) : axis;
                    _fanSpawnDirectionByEntity[fans.CurrentId.Value] = target;
                }
            }

            if (!IsDriven(fans.CurrentId))
            {
                continue;
            }

            // A fan needs a chassis neighbour (BasePropulsion.ValidatePart, BasePropulsion.cs:13-20)
            // and its force is scaled by the engine power factor (FanPropeller.cs:85-92, which the
            // rotor and the propeller share: both are FanPropeller variants).
            if (!IsChassisAnchored(fans.CurrentId) || !TryDriveFactor(fans.CurrentId, out float powerFactor))
            {
                continue;
            }

            if (fan.IsRotor && _fanSpawnDirectionByEntity.TryGetValue(fans.CurrentId.Value, out PhysicsVector3 spawnAxis))
            {
                if (PhysicsVector3.Dot(axis, spawnAxis) > 0f)
                {
                    direction = (axis + spawnAxis) * 0.5f;
                }
            }

            PhysicsVector3 velocity = _kinematicsByBody[link.Body.Value].Velocity;
            float thrust = fan.ImpulsePerTick * powerFactor;

            // LimitForceForSpeed (FanPropeller.cs:245-257): the force decays with the part of the
            // body's velocity that runs along the thrust axis, so a fan stops pushing once the rig
            // is already at its top speed * powerFactor. `maxSpeed` 0 is the propeller, whose
            // original never caps it (`PropellerSpeed` is Infinity).
            float axialSpeed = (direction.X * velocity.X) + (direction.Y * velocity.Y);
            if (fan.MaxSpeed > 0f)
            {
                float maxSpeed = fan.MaxSpeed * powerFactor;
                if (axialSpeed > maxSpeed)
                {
                    thrust /= 1f + axialSpeed - maxSpeed;
                }
            }

            PhysicsVector3 impulse = direction * thrust;

            // The rotor's overspeed brake (FanPropeller.cs:198-207): a rotor still moving past its
            // cap gets a quadratic counter-force along its own velocity, stated in the same
            // per-second Newtons as the thrust the content already divided by the tick duration.
            if (fan.IsRotor && fan.MaxSpeed > 0f)
            {
                float speed = PhysicsVector3.Distance(velocity, PhysicsVector3.Zero);
                float maxSpeed = fan.MaxSpeed * powerFactor;
                if (speed > maxSpeed && ((velocity.X * direction.X) + (velocity.Y * direction.Y)) > 0f)
                {
                    float excess = speed - maxSpeed;
                    float brake = 4f * excess * excess * ForceSecondsPerImpulse / speed;
                    impulse -= velocity * brake;
                }
            }

            if (PhysicsVector3.Distance(impulse, PhysicsVector3.Zero) <= float.Epsilon)
            {
                continue;
            }

            output.Commands.Add(PhysicsCommand.ApplyImpulse(
                link.Body,
                impulse,
                // FanPropeller.cs:152 puts the force at `transform.position + vector * 0.5` with
                // the live axis, not the blended one the force itself follows (`vector2`).
                partPosition + (axis * 0.5f)));
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

            // A wing is only valid next to the chassis (Wings.ValidatePart, Wings.cs:14-31).
            if (!IsChassisAnchored(wings.CurrentId))
            {
                continue;
            }

            WingState wing = wings.CurrentValue;
            // The original evaluates its response curve in the wing's own frame (Wings.cs:104-118),
            // so the build angle and the live rig rotation both aim the lift -- and a mirrored wing
            // (whose pose carries the extra 180-degree Y turn, ADR-030) measures its angle of attack
            // on the other handedness and pushes the other way.
            ResolvePartFrame(wings.CurrentId, link.Body.Value, kinematics.Position, out _, out PhysicsQuaternion frame);
            bool mirrored = _mirroredByEntity.Contains(wings.CurrentId.Value);
            PhysicsVector3 right = frame.Rotate(new PhysicsVector3(1f, 0f, 0f));
            PhysicsVector3 forward = frame.Rotate(new PhysicsVector3(0f, 0f, 1f));
            float angleOfAttack = Aerodynamics.WingAngleOfAttack(kinematics.Velocity, right, mirrored);
            PhysicsVector3 force = Aerodynamics.Force(
                wing.LiftConstant,
                kinematics.Velocity,
                Aerodynamics.WingCoefficient(angleOfAttack),
                forward);
            if (PhysicsVector3.Dot(force, force) > 0f)
            {
                output.Commands.Add(PhysicsCommand.ApplyImpulse(
                    link.Body,
                    force * (1f / Aerodynamics.ForcePerTickDivisor),
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

            // A tail is only valid next to the chassis (Tail.ValidatePart, Tail.cs:12-29).
            if (!IsChassisAnchored(tails.CurrentId))
            {
                continue;
            }

            TailState tail = tails.CurrentValue;
            // The same clamped |v|^2 curve as the wing, with the original's own 0.4 * (num2 - 30)
            // twist about the part's forward axis and its double use of the flip (Tail.cs:57-75).
            ResolvePartFrame(tails.CurrentId, link.Body.Value, kinematics.Position, out _, out PhysicsQuaternion frame);
            bool mirrored = _mirroredByEntity.Contains(tails.CurrentId.Value);
            PhysicsVector3 right = frame.Rotate(new PhysicsVector3(1f, 0f, 0f));
            PhysicsVector3 forward = frame.Rotate(new PhysicsVector3(0f, 0f, 1f));
            float angleOfAttack = Aerodynamics.TailAngleOfAttack(kinematics.Velocity, right, forward, mirrored);
            PhysicsVector3 force = Aerodynamics.Force(
                tail.LiftConstant,
                kinematics.Velocity,
                Aerodynamics.TailCoefficient(angleOfAttack),
                forward);
            if (PhysicsVector3.Dot(force, force) > 0f)
            {
                output.Commands.Add(PhysicsCommand.ApplyImpulse(
                    link.Body,
                    force * (1f / Aerodynamics.ForcePerTickDivisor),
                    kinematics.Position));
            }
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

            // An electric umbrella is a powered part: its force is m_force x engine power factor
            // (PoweredUmbrella.cs:70-89), so it only pulls with an engine in its cluster. A part
            // that declares no consumption (the black umbrella) keeps its legacy unconditional
            // drag, which is what a missing power entry means everywhere else.
            if (!TryDriveFactor(umbrellas.CurrentId, out float powerFactor))
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
                    new PhysicsVector3(0f, -kinematics.Velocity.Y * umbrella.DragCoef * powerFactor, 0f),
                    kinematics.Position));
            }
        }
    }

    private void RunBellows(GameplayTickOutput output)
    {
        var bellows = _bellows.GetEnumerator();
        while (bellows.MoveNext())
        {
            EntityId entity = bellows.CurrentId;
            BellowsState state = bellows.CurrentValue;
            // The bar button is momentary (BasePart.OnButtonTriggered -> ProcessTouch,
            // BasePart.cs:1428; Bellows.OnTouch, Bellows.cs:123-142): the press is spent the moment
            // it is read, before any gate, so a bellows that cannot puff never leaves its button
            // latched on.
            bool pressed = TryConsumeButtonPress(entity);

            if (!_bodies.TryGet(entity, out PhysicsBodyLink link)
                || !_kinematicsByBody.TryGetValue(link.Body.Value, out var kinematics))
            {
                continue;
            }

            bool switched = HasSwitch(entity);
            bool touched = _touchedBodies.Contains(link.Body.Value);

            if (state.Active)
            {
                // The original's puff: `num = Time.time - m_timeBoostStarted` keeps rising, and the
                // force is applied only while `num < 0.5 s` (Bellows.cs:100-110).
                if (state.ElapsedTicks >= BellowsBoostTicks)
                {
                    _bellows.Set(entity, state with { Active = false });
                    continue;
                }

                // `num2 = 1 - (1 - num/0.5)^2` (Bellows.cs:103-104): zero at the start of the puff,
                // peaking at its end. The force point is the part's own mount,
                // `transform.position + vector * 0.5` (:107), and the axis is the part's own
                // `transform.TransformDirection(m_direction)` (:106) -- the same reference frame a
                // fan's m_forceDirection goes through (docs/specs/fan-propeller.md §7.2).
                float t = (float)state.ElapsedTicks / BellowsBoostTicks;
                float scale = 1f - ((1f - t) * (1f - t));
                _bellows.Set(entity, state with { ElapsedTicks = state.ElapsedTicks + 1u });
                if (scale <= 0f)
                {
                    continue;
                }

                PhysicsVector3 puffAxis = ResolveAxis(entity, link.Body.Value, kinematics.Position, state.DirectionX, state.DirectionY, out PhysicsVector3 puffPosition);
                output.Commands.Add(PhysicsCommand.ApplyImpulse(
                    link.Body,
                    puffAxis * (scale * state.ThrustPerTick),
                    puffPosition + (puffAxis * 0.5f)));
                continue;
            }

            if (_tick < state.ReadyAtTick)
            {
                // A legacy bellows (no activation) re-arms the moment it leaves the ground; a
                // pressed one waits out the original's own cycle (0.5 s puff + 0.3 s wait + the
                // skin's inflate, Bellows.cs:123-125).
                if (!switched && !touched)
                {
                    _bellows.Set(entity, state with { ReadyAtTick = 0 });
                }

                continue;
            }

            // A switch (or the button) is the only player trigger; legacy content still fires on
            // touchdown (docs/specs/play-part-switches.md Assumption 4).
            if (!pressed && (switched || !touched))
            {
                continue;
            }

            // A bellows is a BasePropulsion part: it needs a chassis neighbour
            // (BasePropulsion.cs:13-20; CanBeEnabled() => m_isConnected, Bellows.cs:50-53).
            if (!IsChassisAnchored(entity))
            {
                continue;
            }

            _bellows.Set(entity, state with
            {
                Active = true,
                ElapsedTicks = 0u,
                ReadyAtTick = _tick + BellowsBoostTicks + BellowsWaitTicks + state.InflateTicks,
            });
        }
    }

    /// <summary>
    /// The part's own axis for a planar content direction, plus the point the force is applied at:
    /// the part's own `transform.position` (ADR-029). Shared by the bellows' puff, which reads
    /// `transform.TransformDirection(m_direction)` and `transform.position + vector * 0.5`.
    /// </summary>
    private PhysicsVector3 ResolveAxis(
        EntityId entity,
        uint body,
        PhysicsVector3 bodyPosition,
        float directionX,
        float directionY,
        out PhysicsVector3 partPosition)
    {
        ResolvePartFrame(entity, body, bodyPosition, out partPosition, out PhysicsQuaternion partRotation);
        PhysicsVector3 direction = new(directionX, directionY, 0f);
        float magnitude = PhysicsVector3.Distance(direction, PhysicsVector3.Zero);
        return magnitude <= float.Epsilon ? PhysicsVector3.Zero : partRotation.Rotate(direction * (1f / magnitude));
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
            // The bar button is momentary: the press is spent before this part's own gates, so a
            // hook that cannot fire never leaves its button latched on.
            bool pressed = TryConsumeButtonPress(grapples.CurrentId);
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

            bool fire = switched ? pressed : touched;
            if (!fire)
            {
                continue;
            }

            // A landing (legacy content) or the switch fires the hook toward its
            // direction: one strong pull impulse (cast + drag merged).
            _grapples.Set(grapples.CurrentId, grapple with { FiredRecently = true });
            // The pull runs along the part's own transform: GrapplingHook.cs:467 hands
            // `m_direction` to `transform.TransformDirection`, so the build rotation aims it --
            // the same reference frame the fan family goes through. The original spends that
            // launch impulse on the hook head's own rigidbody and lets the joint drag the rig;
            // PigForge approximates cast and drag with this one impulse, so it lands on the
            // part's own transform too.
            ResolvePartFrame(
                grapples.CurrentId,
                link.Body.Value,
                kinematics.Position,
                out PhysicsVector3 partPosition,
                out PhysicsQuaternion partRotation);
            PhysicsVector3 direction = partRotation.Rotate(
                new PhysicsVector3(grapple.DirectionX / magnitude, grapple.DirectionY / magnitude, 0f));
            output.Commands.Add(PhysicsCommand.ApplyImpulse(
                link.Body,
                direction * grapple.Impulse,
                partPosition));
        }
    }

    private void RunDetachers(GameplayTickOutput output)
    {
        var detachers = _detachers.GetEnumerator();
        while (detachers.MoveNext())
        {
            // A switched detacher fires on its switch; legacy content keeps the
            // impact path in DetachOnImpact.
            if (TryConsumeButtonPress(detachers.CurrentId))
            {
                output.DetachedEntities.Add(detachers.CurrentId);
            }
        }
    }

    private void RunRockets(GameplayTickOutput output)
    {
        List<(PhysicsVector3 Origin, float Radius, float Impulse)> spent = null!;
        var rockets = _rockets.GetEnumerator();
        while (rockets.MoveNext())
        {
            // The entity id is read once: ending a burn removes the entry, and a store's enumerator
            // must not be asked for its current id after a mutation (it would hand back the slot's
            // new occupant, and the blast below resolves the part's own frame from it).
            EntityId rocketEntity = rockets.CurrentId;
            // The bar button is momentary: the press is spent before the chassis gate, so a rocket
            // that never fires cannot stay latched on.
            bool pressed = TryConsumeButtonPress(rocketEntity);
            if (!_bodies.TryGet(rocketEntity, out PhysicsBodyLink link)
                || !_kinematicsByBody.ContainsKey(link.Body.Value))
            {
                continue;
            }

            // A rocket (and the jet engine, which shares the class hierarchy) is a BasePropulsion
            // part: without a chassis neighbour it never fires at all
            // (BasePropulsion.cs:13-20) -- no ignition, no thrust, no end-of-burn blast.
            if (!IsChassisAnchored(rocketEntity))
            {
                continue;
            }

            RocketState rocket = rockets.CurrentValue;
            if (!rocket.Ignited)
            {
                // A switched rocket waits for its switch; legacy content auto-ignites.
                if (HasSwitch(rocketEntity) && !pressed)
                {
                    continue;
                }

                rocket = rocket with { Ignited = true, ElapsedTicks = 0u };
            }
            else
            {
                // One tick of burn has passed since the ignition tick, which is `num` seconds after
                // `m_timeBoostStarted` in the original (Rocket.cs:228).
                rocket = rocket with { ElapsedTicks = rocket.ElapsedTicks + 1u };
            }

            uint burnTicks = (uint)rocket.IgnitionTicks + rocket.BoostTicks + rocket.EndTicks;
            if (rocket.ElapsedTicks >= burnTicks)
            {
                // The burn is spent. The original only stops the thrust (`m_enabled = false`): the
                // part stays in the world, and a charge with `m_explodes` blasts where it stands
                // (Rocket.cs:281-284, :627-634) -- the explosion radiates from the charge's own pose,
                // not from the compound's centre of mass.
                // Dropping the role is what makes it a one-shot: `m_boostUsed` keeps OnTouch from
                // ever igniting the same part again (Rocket.cs:570-573).
                _rockets.Remove(rocketEntity);
                if (rocket.ExplodeRadius <= 0f || rocket.ExplodeImpulse <= 0f)
                {
                    continue;
                }

                ResolvePartFrame(
                    rocketEntity,
                    link.Body.Value,
                    _kinematicsByBody[link.Body.Value].Position,
                    out PhysicsVector3 blastOrigin,
                    out _);
                (spent ??= new List<(PhysicsVector3, float, float)>()).Add(
                    (blastOrigin, rocket.ExplodeRadius, rocket.ExplodeImpulse));
                continue;
            }

            _rockets.Set(rocketEntity, rocket);

            // The bottle family does not push during its ignition phase: the original returns
            // before the force when `num < m_ignitionTime` and the prefab carries an
            // `m_visualization` (Rocket.cs:235-240). A plain rocket has none and thrusts from its
            // first tick.
            if (rocket.Visualization && rocket.ElapsedTicks < rocket.IgnitionTicks)
            {
                continue;
            }

            float magnitude = PhysicsVector3.Distance(
                new PhysicsVector3(rocket.DirectionX, rocket.DirectionY, 0f), PhysicsVector3.Zero);
            if (magnitude <= float.Epsilon)
            {
                continue;
            }

            // The third phase is the original's linear ramp back to zero (Rocket.cs:266-269):
            // `1 - (num - ignitionTime - boostDuration) / boostEndDuration`.
            float phase = 1f;
            if (rocket.EndTicks > 0 && rocket.ElapsedTicks > (uint)rocket.IgnitionTicks + rocket.BoostTicks)
            {
                phase = 1f - ((float)(rocket.ElapsedTicks - (uint)rocket.IgnitionTicks - rocket.BoostTicks) / rocket.EndTicks);
            }

            // `LimitForceForSpeed` (Rocket.cs:529-541): the component of the body's velocity along
            // the thrust axis, and past `m_maximumSpeed` the force divides by `1 + v - maxSpeed`.
            float force = phase * rocket.ThrustPerTick;
            if (rocket.MaxSpeed > 0f
                && _kinematicsByBody.TryGetValue(link.Body.Value, out var flight)
                && PhysicsVector3.Dot(PhysicsVector3.Normalize(flight.Velocity), new PhysicsVector3(rocket.DirectionX / magnitude, rocket.DirectionY / magnitude, 0f)) is float along && along > 0f)
            {
                float speed = PhysicsVector3.Distance(flight.Velocity, PhysicsVector3.Zero) * along;
                if (speed > rocket.MaxSpeed)
                {
                    force /= 1f + speed - rocket.MaxSpeed;
                }
            }

            // Rocket.cs:298-300 reads the axis off the part's own transform
            // (`transform.TransformDirection(m_direction)`) and pushes there
            // (`AddForceAtPosition(..., transform.position + zero * 0.5f)`), so a rocket built the
            // other way round pushes the other way -- the same reference frame a fan goes through.
            ResolvePartFrame(
                rocketEntity,
                link.Body.Value,
                _kinematicsByBody[link.Body.Value].Position,
                out PhysicsVector3 partPosition,
                out PhysicsQuaternion partRotation);
            PhysicsVector3 direction = partRotation.Rotate(
                new PhysicsVector3(rocket.DirectionX / magnitude, rocket.DirectionY / magnitude, 0f));
            output.Commands.Add(PhysicsCommand.ApplyImpulse(
                link.Body,
                direction * force,
                partPosition));
        }

        if (spent is null)
        {
            return;
        }

        foreach ((PhysicsVector3 origin, float radius, float impulse) in spent)
        {
            // The charge's own body is in the blast: the original's `Physics.OverlapSphere` at
            // `transform.position` (Rocket.cs:630) includes the charge's own collider, so a part that
            // explodes is driven by its own explosion -- the same rule ADR-019 recorded for TNT. The
            // part itself survives as a spent husk; the original only stops its thrust.
            if (radius > 0f && impulse > 0f)
            {
                RadialBlast(origin, radius, impulse, source: default, output);
            }
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
            if (!state.Ignited && HasSwitch(tntComponents.CurrentId) && TryConsumeButtonPress(tntComponents.CurrentId))
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
            if (!blasters.CurrentValue.Spent && TryConsumeButtonPress(blasters.CurrentId))
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
                TryConsumeButtonPress(entity);
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

        // The blast radiates from the charge part, not from the compound's centre of mass: a
        // welded charge shares its body with the parts around it, and the original measures every
        // target from TNT.transform.position (TNT.cs:236-252 AddExplosionForce). The charge's own
        // body is included, so the very contraption holding the charge is driven -- and torn --
        // by the blast the way the original's per-part rigidbodies are.
        ResolvePartFrame(tntEntity, link.Body.Value, center.Position, out PhysicsVector3 origin, out _);

        // Sorted body ids keep the command order deterministic across replays.
        uint[] bodyIds = _kinematicsByBody.Keys.ToArray();
        Array.Sort(bodyIds);
        foreach (uint bodyId in bodyIds)
        {
            if (!_dynamicBodies.Contains(bodyId)
                || !_kinematicsByBody.TryGetValue(bodyId, out var kinematics))
            {
                continue;
            }

            float distance = PhysicsVector3.Distance(kinematics.Position, origin);
            if (distance >= _config.TntBlastRadius)
            {
                continue;
            }

            float falloff = 1f - (distance / _config.TntBlastRadius);
            PhysicsVector3 direction = distance > float.Epsilon
                ? PhysicsVector3.Normalize(kinematics.Position - origin)
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
        }

        _motors.Remove(entity);
        _balloons.Remove(entity);
        _fans.Remove(entity);
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
        _unanchoredPropulsion.Remove(entity.Value);
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

        var bellows = _bellows.GetEnumerator();
        while (bellows.MoveNext())
        {
            if (bellows.CurrentValue.ReadyAtTick != 0)
            {
                _bellows.Set(bellows.CurrentId, bellows.CurrentValue with { ReadyAtTick = 0 });
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
        _unanchoredPropulsion.Clear();
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
        }

        _motors.Remove(entity);
        _balloons.Remove(entity);
        _fans.Remove(entity);
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
        _localOffsetByEntity.Clear();
        _localRotationByEntity.Clear();
        _mirroredByEntity.Clear();
        _fanSpawnDirectionByEntity.Clear();
        _rotationByBody.Clear();
        _restitutionByBody.Clear();
        _massByBody.Clear();
        _bouncedBodies.Clear();
        _peakApproachByBody.Clear();
        _powerFactorByCluster.Clear();
        _powerHostByEntity.Clear();
    }
}
