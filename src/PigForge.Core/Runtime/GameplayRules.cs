using PigForge.Core.Content;
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
/// The original's own camera rectangle (<c>LevelManager.CameraLimits</c>, <c>LevelManager.cs:9-16</c>):
/// <see cref="TopLeftX"/>/<see cref="TopLeftY"/> is its top-left corner and
/// <see cref="SizeX"/>/<see cref="SizeY"/> extends right and down. A pig is outside it when it drops
/// below the bottom edge, runs past <c>topLeft.x + size.x * 1.1</c> or back past
/// <c>topLeft.x - size.x * 0.1</c> (<c>Pig.cs:396-403</c>) -- the x margins are the original's own
/// asymmetry and there is no upper bound at all. The original answers by returning to its building
/// state (<c>GameMode.cs:384-387</c>).
/// </summary>
public readonly record struct CameraLimits(float TopLeftX, float TopLeftY, float SizeX, float SizeY)
{
    public bool Contains(PhysicsVector3 position) =>
        position.Y >= TopLeftY - SizeY
        && position.X <= TopLeftX + SizeX * 1.1f
        && position.X >= TopLeftX - SizeX * 0.1f;
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
    bool RestitutionAppliedNatively = false,
    // The level's own camera rectangle (a v5 document's `cameraLimits`): the pig's real bound. A
    // document without one (PigForge's hand-made v1 slope/terrain rooms) falls back to `MapBounds`.
    CameraLimits? CameraLimits = null,
    // The level's challenge markers (a v8 prop's `triggers`, `docs/specs/level-challenges.md`): the pig
    // reaching one collects it. A level with no challenges leaves this null.
    IReadOnlyList<ChallengeTriggerDefinition>? ChallengeTriggers = null)
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

/// <summary>
/// One challenge marker of a level (`docs/specs/level-challenges.md`): the prop it belongs to and the
/// world-space box the pig has to reach. The box is the original's own trigger collider, placed at the
/// prop's pose and grown to the axis-aligned bounds a rotated box needs.
/// </summary>
public sealed record ChallengeTriggerDefinition(int PropIndex, GameplayZone Zone);

/// <summary>
/// Turns a level document's placed props into the challenge markers the rules test
/// (`docs/specs/level-challenges.md` §3): every v8 prop's own `triggers`, in the prop's own frame, taken
/// to world space exactly as `GameRoom.EnsurePropBodies` places a prop's boxes.
/// </summary>
public static class LevelChallengeTriggers
{
    public static IReadOnlyList<ChallengeTriggerDefinition> Build(LevelContentDocument level)
    {
        ArgumentNullException.ThrowIfNull(level);
        List<ChallengeTriggerDefinition> triggers = new();
        for (int index = 0; index < level.Props.Count; index++)
        {
            LevelPropDefinition prop = level.Props[index];
            foreach (LevelPropBoxDefinition box in prop.Triggers)
            {
                bool mirrored = prop.ScaleX < 0f;
                float scaleX = MathF.Abs(prop.ScaleX);
                float scaleY = MathF.Abs(prop.ScaleY);
                (float cos, float sin) = (MathF.Cos(prop.Rotation), MathF.Sin(prop.Rotation));
                float offsetX = (mirrored ? -box.X : box.X) * scaleX;
                float offsetY = box.Y * scaleY;
                PhysicsVector3 center = new(
                    prop.X + (offsetX * cos) - (offsetY * sin),
                    prop.Y + (offsetX * sin) + (offsetY * cos),
                    prop.Z + box.Z);
                float rotation = prop.Rotation + (mirrored ? -box.Rotation : box.Rotation);
                // The axis-aligned bounds a box of these extents needs at that angle: the marker is a
                // "someone came through here" test, so the coarse box is the point.
                float halfWidth = 0.5f * ((MathF.Abs(MathF.Cos(rotation)) * box.SizeX * scaleX) + (MathF.Abs(MathF.Sin(rotation)) * box.SizeY * scaleY));
                float halfHeight = 0.5f * ((MathF.Abs(MathF.Sin(rotation)) * box.SizeX * scaleX) + (MathF.Abs(MathF.Cos(rotation)) * box.SizeY * scaleY));
                // A flat marker (the fan's own trigger has no depth) gets the play plane's own thickness,
                // because the zone has to hold the pig's frame on its plane.
                float halfDepth = box.SizeZ > 0f ? 0.5f * box.SizeZ : 0.5f;
                triggers.Add(new ChallengeTriggerDefinition(
                    index,
                    new GameplayZone(
                        new PhysicsVector3(center.X - halfWidth, center.Y - halfHeight, center.Z - halfDepth),
                        new PhysicsVector3(center.X + halfWidth, center.Y + halfHeight, center.Z + halfDepth))));
            }
        }

        return triggers;
    }
}

/// <summary>
/// The original's extra finish cargo (<c>GameMode.PlayerHasRequiredObjects</c>,
/// <c>GameMode.cs:356-375</c>): a level that grants an <c>Egg</c> only completes when an egg is in
/// the goal as well, otherwise a level that grants a <c>Pumpkin</c> wants the pumpkin. The two flags
/// come from the level's own allowance table -- <c>LevelManager.EggRequired</c>/
/// <c>PumpkinRequired</c> are <c>GetPartTypeCount(type) &gt; 0</c>
/// (<c>LevelManager.cs:413-432</c>), and <c>BaseGameMode.GetPartCount</c>
/// (<c>BaseGameMode.cs:558-578</c>) answers straight from <c>m_partTypeCounts</c>, which is
/// schemaVersion 9's <c>build.partCounts</c>. The original tests the egg first and never looks at
/// the pumpkin when an egg is granted, so this resolves the same way.
/// </summary>
public static class LevelGoalRequirements
{
    /// <summary>
    /// The granted part family the level also wants in the goal, or <c>null</c> when the pig alone
    /// finishes it (no <c>build</c> block, or neither family in the table).
    /// </summary>
    public static uint? RequiredExtraPartFamily(LevelContentDocument level, PartContentLibrary content)
    {
        ArgumentNullException.ThrowIfNull(level);
        ArgumentNullException.ThrowIfNull(content);
        if (level.Build is not LevelBuildLimits build)
        {
            return null;
        }

        uint pumpkin = 0;
        foreach (LevelPartAllowance allowance in build.PartCounts)
        {
            if (allowance.Count == 0 || !content.TryGetPart(allowance.PartTypeId, out PartDefinition part))
            {
                continue;
            }

            PartCapabilities? capabilities = part.Capabilities;
            if (capabilities is null)
            {
                continue;
            }

            if (capabilities.IsEgg)
            {
                return allowance.PartTypeId;
            }

            if (capabilities.IsPumpkin && pumpkin == 0)
            {
                pumpkin = allowance.PartTypeId;
            }
        }

        return pumpkin == 0 ? null : pumpkin;
    }
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

    /// <summary>Props collected this tick: indices into the level document's `props`
    /// (`docs/specs/level-challenges.md` §4). The room marks them and tells the clients.</summary>
    public List<int> CollectedProps { get; } = new();

    /// <summary>Parts whose damping changed this tick, which the room folds into the body each of
    /// them rides (a PigForge body carries one damping pair for all its members). The original's
    /// <c>Pig.FixedUpdate</c> rewrites its own rigidbody's drag and angularDrag every fixed step
    /// (<c>Pig.cs:249-262</c>), so this is a per-tick value, not a spawn-time one.</summary>
    public List<PartDampingOverride> DampingOverrides { get; } = new();

    /// <summary>Bodies whose damping a rule rewrites every tick but which are not a compound
    /// member (the rope's segment bodies, `docs/specs/rope.md` §4.5): the room passes them straight
    /// to <c>IPhysicsWorld.SetBodyDamping</c>. The original's <c>Rope.FixedUpdate</c> writes the
    /// same drag onto every node of one rope, and the angular term stays the prefab's own.</summary>
    public List<BodyDampingOverride> BodyDampingOverrides { get; } = new();

    /// <summary>Pigs whose own transform left the level's camera rectangle this tick
    /// (<see cref="GameplayConfig.CameraLimits"/>, <c>Pig.cs:396-403</c>). The room answers the way the
    /// original does -- <c>GameMode.cs:384-387</c> returns to the building state.</summary>
    public List<EntityId> PigsOutOfBounds { get; } = new();

    public void Clear()
    {
        Commands.Clear();
        DestroyedEntities.Clear();
        DetachedEntities.Clear();
        DampingOverrides.Clear();
        BodyDampingOverrides.Clear();
        PigsOutOfBounds.Clear();
        CollectedProps.Clear();
    }
}

/// <summary>One part's damping for this tick: the linear/angular pair the original wrote onto the
/// part's own rigidbody. See <see cref="GameplayTickOutput.DampingOverrides"/>.</summary>
public readonly record struct PartDampingOverride(EntityId Entity, float Linear, float Angular);

/// <summary>One body's damping for this tick, for a body no compound member owns (a rope segment).</summary>
public readonly record struct BodyDampingOverride(PhysicsBodyId Body, float Linear, float Angular);

/// <summary>
/// Runtime gameplay rules (motors, wheels, pigs, TNT, joint breaks, level outcome) that
/// consume physics events and snapshots only. Semantics per ADR-002: pigs are
/// indestructible bouncy cargo; TNT is a pure momentum source; win is delivery into the
/// goal zone; a pig leaving the level's camera rectangle is reported so the room can do what
/// the original does -- return to the building state.
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
    /// <summary>The level props already collected this run, so a marker reports once and `RESET` clears it
    /// (`docs/specs/level-challenges.md` §4).</summary>
    private readonly HashSet<int> _collectedChallenges = new();
    // The original's extra finish cargo (`GameMode.PlayerHasRequiredObjects`, gap G114): the level
    // may want an egg or a pumpkin delivered with the pig, and `PartsInGoal` is a running record --
    // entering the goal is latched, leaving it does not take the entry back.
    private bool _goalExtraPartRequired;
    private readonly HashSet<uint> _goalTargetParts = new();
    private bool _goalPigArrived;
    private bool _goalExtraArrived;
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
    // The surface a body is standing on, oriented away from the other body of the contact and
    // kept only if it is the most upward one this tick. MotorWheel.FixedUpdate needs it: its drive
    // direction is the tangent of the ground it rests on (MotorWheel.cs:288), not a world axis.
    private readonly Dictionary<uint, PhysicsVector3> _groundNormalByBody = new();
    // The frame a hinged wheel's non-spinning transform belongs to (its chassis's live rotation
    // times the wheel's build rotation inside it). A wheel body spins about its own axle, so its
    // pose can never answer `transform.right` -- which is the axis MotorWheel.FixedUpdate gates its
    // drive on (MotorWheel.cs:287). Declared by the placement layer where the hinge is bound.
    private readonly Dictionary<uint, WheelAttach> _wheelAttachByEntity = new();
    private readonly HashSet<uint> _brokenJoints = new();
    // The original's run clock (`LevelManager.TimeElapsed`, `BaseGameMode.cs:325-328` and
    // `LevelManager.cs:1431-1441`): the reference pig's own transform is recorded when the room
    // starts the run, the clock starts on the first tick the pig is a metre away from that pose
    // (the tripping tick itself adds nothing -- the original returns from `UpdateRunning` before
    // accumulating), and every later tick adds one tick of time. It freezes by itself: the tick
    // counter only advances while the phase is Playing, so the goal's tick is the last one counted
    // and the room reads the original's `CompletionTime` (`GameMode.cs:379`) as this value.
    private EntityId _runPig;
    private PhysicsVector3 _runPigStart;
    private bool _hasRunStart;
    private bool _runTimerStarted;
    private uint _runTimerStartTick;
    private readonly HashSet<uint> _dynamicBodies = new();
    private readonly Dictionary<uint, PhysicsVector3> _previousVelocities = new();
    // Elasticity is a per-part content property, but a contact names bodies: a pig welded into
    // a craft shares one compound body with plain structure. The body therefore carries the
    // strongest restitution and the summed mass of its members, and the members are tracked so
    // a seam split or a rebind recomputes them instead of dropping the value.
    private readonly Dictionary<uint, List<uint>> _membersByBody = new();
    // The level's own crates: a static prop body whose own component blows it up when something hits
    // it (`TNTBox.cs:15-19`). The room declares one per prop body it builds; a body that has exploded
    // is dropped, so the state's presence is also "this crate is still there".
    private readonly Dictionary<uint, ExplosivePropState> _explosiveProps = new();
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
    private readonly SortedDictionary<uint, RopeState> _ropes = new();
    private readonly Dictionary<uint, float> _powerFactorByCluster = new();
    private readonly Dictionary<uint, uint> _powerHostByEntity = new();
    // The vanilla declaration defaults leave IN DynamicPowerSystem off, so the original runs its
    // legacy power branch, whose denominator depends on whether a motor wheel is off the ground
    // (Contraption.cs:556-582). That makes the factor change with contact state, so every cluster is
    // recomputed once per tick, and the reusable key list keeps that path allocation-free.
    private readonly List<uint> _powerClusterKeys = new();
    // `MotorWheel.m_hasContact` starts TRUE (MotorWheel.cs:34) and is only overwritten by the
    // wheel's own ground raycast, i.e. a wheel counts as grounded until a step says otherwise. The
    // event stream is the same proxy RunMotors gates its drive on, so a motor wheel is "airborne"
    // only once a processed tick failed to report a contact for its body.
    private bool _hasProcessedTick;
    // Propulsion parts the placement layer found WITHOUT a chassis neighbour (see
    // ConstructionRules.HasChassisNeighbor). The original rejects such a part in ValidatePart
    // (BasePropulsion.cs:13-20, Wings.cs:14-31, Tail.cs:12-29), so it never fires; PigForge lets
    // the player build it but it emits no force. Absence means anchored, which keeps level actors
    // and unit harnesses ungated -- the placement layer declares the exceptions at materialisation.
    private readonly HashSet<uint> _unanchoredPropulsion = new();

    /// <summary>
    /// The original's <c>EnginePowerLimit</c> in the vanilla declaration defaults
    /// (<c>INDeclarationSettingsExp.json</c>, 1.0 — profile B's 4.0 is a mod value, gaps G105): the
    /// raw ratio is capped at <c>10 * EnginePowerLimit</c> = 10 before the exponent
    /// (Contraption.cs:545, the same expression in both the dynamic and the legacy branch). The two
    /// exponents are the verbatim constants of Contraption.cs:553.
    /// </summary>
    public const float EnginePowerLimit = 1f;

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

    /// <summary>The run clock's tick duration: PigForge's own tick rate, not the original's
    /// <c>Time.deltaTime</c>. The original integrates at 50 Hz, this room at 60 (see
    /// `docs/specs/level-stars.md` §8 D2).</summary>
    private const float RunTimerSecondsPerTick = 1f / 60f;

    /// <summary>How far the reference pig's own transform must leave its start pose before the
    /// original's clock runs: <c>Vector3.Distance(...) >= 1f</c> (<c>LevelManager.cs:1436</c>).</summary>
    private const float RunTimerStartDistance = 1f;

    /// <summary>Floor that keeps a massless part from producing an unbounded bounce impulse.</summary>
    private const float MinimumPartMass = 0.001f;

    /// <summary>A driven wheel's top speed in world units per second per unit of engine power
    /// factor: <c>m_maximumSpeed = 15 * enginePowerFactor</c> (MotorWheel.cs:103, StickyWheel.cs
    /// through the same override chain). At the cap the wheel emits nothing at all; below it the
    /// force tapers as <c>sqrt(1 - |v| / max)</c> (MotorWheel.cs:292-299), so a driven rig tops
    /// out instead of accelerating forever (gap list G24).</summary>
    private const float MotorWheelMaximumSpeed = 15f;

    /// <summary>
    /// The original's own bounce threshold: Unity's <c>Physics.bounceThreshold</c> is
    /// <c>ProjectSettings/DynamicsManager.asset</c>'s <c>m_BounceThreshold: 2</c> in BPLE, and
    /// PhysX drops the restitution of a contact whose relative normal velocity is below it. So a
    /// landing slower than 2 m/s does not bounce -- the pig settling on the ground is not a
    /// rubber ball, which is exactly what the original looks like (gap G89).
    /// </summary>
    private const float MinimumBounceApproachSpeed = 2f;

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

    /// <summary>
    /// How long a motor's own drive takes to reach full force (<c>MotorWheel.cs:259-261</c>): the timer
    /// counts the seconds the motor has been enabled, capped at one, and the delivered force is
    /// <c>pow(timer, 0.4)</c> of full -- 20.9 % on the original's own first frame (<c>0.02^0.4</c>,
    /// measured on its editor), 75.8 % at half a second, full at one. The step is the room's fixed tick,
    /// because the ramp's length is a duration and not a tick count (gap G110).
    /// </summary>
    private const float MotorThrustRampSecondsPerTick = 1f / 60f;

    /// <summary>The original's own exponent for that ramp (<c>MotorWheel.cs:261</c>).</summary>
    private const float MotorThrustRampExponent = 0.4f;

    /// <summary>The strongest speed a body has carried recently, tagged with the tick that set it,
    /// so a stale peak can be discarded instead of bouncing a gentle later touch.</summary>
    private readonly record struct PeakApproachSample(PhysicsVector3 Velocity, uint Tick);

    /// <summary>The entity whose body carries a hinged wheel's non-spinning frame, and the wheel's
    /// own build rotation inside that entity's body.</summary>
    private readonly record struct WheelAttach(EntityId Parent, PhysicsQuaternion LocalRotation);

    /// <summary>One level crate that blows up when it is hit: the prop it stands for (so the room can
    /// hide it) and the original component's own three numbers.</summary>
    private readonly record struct ExplosivePropState(int PropIndex, float TriggerSpeed, float Radius, float Impulse);

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

    /// <summary>The original's <c>TimeElapsed</c> in seconds: nothing until the reference pig first
    /// left <see cref="BeginRun"/>'s pose by a metre, then the ticks since that one. It stops
    /// advancing with the phase, which is what freezes the original's <c>CompletionTime</c> at the
    /// goal (`GameMode.cs:379`).</summary>
    public float ElapsedSeconds =>
        _runTimerStarted ? (_tick - _runTimerStartTick) * RunTimerSecondsPerTick : 0f;

    /// <summary>
    /// Records the original's running entry (`BaseGameMode.cs:325-328`): the room calls it when it
    /// materialises the contraption, handing over the reference pig and that pig's <b>own</b>
    /// transform position -- the pose the clock measures from, the same transform the camera bound
    /// tests (`Pig.cs:396-403`). Every later Start calls it again, which zeroes the clock.
    /// </summary>
    public void BeginRun(EntityId pig, PhysicsVector3 ownPosition)
    {
        _runPig = pig;
        _runPigStart = ownPosition;
        _hasRunStart = true;
        _runTimerStarted = false;
        _runTimerStartTick = 0;
        // The original's running entry clears the goal's own record (`BaseGameMode.cs:325-328`:
        // `levelManager.PartsInGoal.Clear()`), so every Start begins with no part delivered.
        _goalPigArrived = false;
        _goalExtraArrived = false;
    }

    /// <summary>
    /// Whether two parts sit in the same joint graph component. The original's connected component
    /// <b>is</b> its joint map (`Contraption.cs:1293` unions every `m_jointMap` entry), which is the
    /// graph <see cref="LinkPowerCluster"/> builds here -- a merged body, a seam, a weld, a wheel
    /// hinge and a spring edge are all edges of it -- so this answers
    /// <c>ContraptionRunning.IsConnectedToPig</c> (`LevelManager.cs:1241`), the transport
    /// challenge's first branch (`docs/specs/level-stars.md` §4.2).
    /// </summary>
    public bool AreConnected(EntityId left, EntityId right)
    {
        uint leftKey = PowerClusterKey(left);
        uint rightKey = PowerClusterKey(right);
        if (leftKey == rightKey)
        {
            return true;
        }

        if (_ropes.Count == 0)
        {
            return false;
        }

        // Each intact rope merges the components at its two ends (the original's rope-link
        // component, `Contraption.cs:1111-1125`); a cut rope no longer does.
        Dictionary<uint, uint> parent = new();
        uint Find(uint key)
        {
            if (!parent.TryGetValue(key, out uint value))
            {
                parent[key] = key;
                return key;
            }

            while (value != key)
            {
                key = value;
                if (!parent.TryGetValue(key, out value))
                {
                    parent[key] = key;
                    value = key;
                }
            }

            return key;
        }

        void Union(uint a, uint b)
        {
            uint rootA = Find(a);
            uint rootB = Find(b);
            if (rootA != rootB)
            {
                parent[rootA] = rootB;
            }
        }

        foreach (RopeState rope in _ropes.Values)
        {
            if (rope.CutLink >= 0 || !rope.Left.IsValid || !rope.Right.IsValid)
            {
                continue;
            }

            Union(ClusterKeyOfBody(rope.Left), ClusterKeyOfBody(rope.Right));
        }

        return Find(leftKey) == Find(rightKey);
    }

    /// <summary>The power-cluster key of a body (its representative entity, then the power host).</summary>
    private uint ClusterKeyOfBody(PhysicsBodyId body) =>
        _entitiesByBody.TryGetValue(body.Value, out EntityId entity) ? PowerClusterKey(entity) : body.Value;

    /// <summary>Drops every collected challenge marker: the room's `Retry` runs it, matching the
    /// original's `Collectable.OnReset` (`docs/specs/level-challenges.md` §2).</summary>
    public void ClearChallenges() => _collectedChallenges.Clear();

    /// <summary>
    /// Declares a level prop that blows up when something hits it hard enough: the vanilla `TNT_Box`
    /// crate, whose own component carries the trigger speed, the radius and the impulse
    /// (`TNTBox.cs:15-19`, `docs/specs/level-props.md` §3.8). The room declares one per static prop
    /// body it builds; the impact rule then reads that body's own contact events, the way the
    /// original's crate reads its own collider's `OnCollisionEnter`.
    /// </summary>
    public void AddExplosiveProp(PhysicsBodyId body, int propIndex, float triggerSpeed, float radius, float impulse)
    {
        _explosiveProps[body.Value] = new ExplosivePropState(propIndex, triggerSpeed, radius, impulse);
    }

    /// <summary>Forgets every declared crate: the room's `Retry` rebuilds the prop bodies and declares
    /// them again, so the stale body ids must not answer for the new ones.</summary>
    public void ClearExplosiveProps() => _explosiveProps.Clear();

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

    /// <summary>
    /// Registers a pig. <paramref name="ownLinear"/>/<paramref name="ownAngular"/> are the part's
    /// content damping (what the original writes at spawn and restores once its ramp is idle), and
    /// the three ramp values come from content <c>capabilities.dampingRamp</c> — absent on every
    /// class whose <c>FixedUpdate</c> leaves its damping alone (see <see cref="PigMarker"/>).
    /// </summary>
    public void AddPig(
        EntityId entity,
        float ownLinear,
        float ownAngular,
        float rampSpeedThreshold = 0f,
        float rampBase = 0f,
        float rampSlope = 0f)
    {
        _pigs.Set(entity, new PigMarker(rampSpeedThreshold, rampBase, rampSlope, ownLinear, ownAngular));
        _alivePigs++;
    }

    /// <summary>
    /// Declares a charge (`docs/specs/tnt-blast.md`): the fuse it is armed with, whether it chains and
    /// ignites on impact, and the original component's own blast -- radius, impulse, the impact speed
    /// that arms it -- plus the two class rules an `AlienTNT` flips (it survives its own blast and
    /// re-arms; no other charge may chain it). A zero radius/impulse/trigger speed falls back to the
    /// room's `GameplayConfig`, which is what a hand-built fixture relies on.
    /// </summary>
    public void AddTnt(
        EntityId entity,
        ushort fuseTicks,
        bool chainDetonate = true,
        bool igniteOnImpact = true,
        float radius = 0f,
        float impulse = 0f,
        float triggerSpeed = 0f,
        bool repeatable = false,
        bool chainTarget = true) =>
        _tnt.Set(entity, new TntState(
            fuseTicks,
            Ignited: false,
            chainDetonate,
            igniteOnImpact,
            radius,
            impulse,
            triggerSpeed,
            repeatable,
            chainTarget,
            ReloadTicks: fuseTicks));

    public void AddBlaster(EntityId entity, float radius, float impulse, float chainRadius) =>
        _blasters.Set(entity, new BlasterState(radius, impulse, chainRadius));

    /// <summary>Marks a part as super glue: its compound cluster never splits along a seam.</summary>
    public void AddGlue(EntityId entity) => _glues.Set(entity, default);

    public bool HasGlue(EntityId entity) => _glues.TryGet(entity, out _);

    /// <summary>
    /// Registers a motor's drive. <paramref name="thrustTimer"/> is the original's own part state
    /// (<c>m_thrustTimer</c>, seconds of enabled drive, capped at one): a part placed and started fresh
    /// begins at zero and ramps (gap G110), while a fixture can model a motor that is already wound up.
    /// </summary>
    public void AddMotor(EntityId entity, float impulsePerTick, float directionX, float thrustTimer = 0f) =>
        _motors.Set(entity, new MotorState(impulsePerTick, directionX, thrustTimer));

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

    /// <summary>
    /// Declares that this level wants an extra part delivered with the pig
    /// (<c>GameMode.PlayerHasRequiredObjects</c>, gap G114; which family is the level's own business,
    /// <see cref="LevelGoalRequirements"/>). Idempotent: every return from the building phase
    /// re-installs the level's rules.
    /// </summary>
    public void RequireExtraGoalPart() => _goalExtraPartRequired = true;

    /// <summary>Registers one part of the family the level also wants in the goal. A part destroyed
    /// later stops answering its own arrival, but an arrival already recorded stands
    /// (<c>LevelManager.PartsInGoal</c> is never trimmed).</summary>
    public void AddGoalTargetPart(EntityId entity) => _goalTargetParts.Add(entity.Value);

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

    /// <summary>A motor's own ramp reading, in seconds of enabled drive (0 when the entity is not a
    /// motor). The delivered force is <c>pow(this, 0.4)</c> of full (gap G110).</summary>
    public float MotorThrustTimer(EntityId entity) =>
        _motors.TryGet(entity, out MotorState state) ? state.ThrustTimer : 0f;

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

    /// <summary>
    /// Declares the frame a hinged wheel's non-spinning transform belongs to: its chassis's live
    /// rotation times the wheel's build rotation inside it (<paramref name="localRotation"/>).
    /// The original's wheel part root is welded to the rig, so <c>base.transform.right</c> is the
    /// rig's own right axis -- but a PigForge wheel body spins about its axle (ADR-009), and a spin
    /// about the same Z axis the build rotation uses cannot be told apart from it afterwards.
    /// The placement layer therefore hands the frame over where it binds the hinge.
    /// </summary>
    public void LinkWheelAttach(EntityId wheel, EntityId parent, PhysicsQuaternion localRotation) =>
        _wheelAttachByEntity[wheel.Value] = new WheelAttach(parent, localRotation);

    /// <summary>Forgets the frame a wheel's non-spinning transform was rigid to (its chassis is
    /// gone, or the wheel became free): the wheel is then rigid to its own body again.</summary>
    public void UnlinkWheelAttach(EntityId wheel) => _wheelAttachByEntity.Remove(wheel.Value);
    public void Tick(uint tick, ReadOnlySpan<PhysicsEvent> events, ReadOnlySpan<PhysicsBodySnapshot> snapshots, GameplayTickOutput output)
    {
        if (Phase != GameplayPhase.Playing)
        {
            return;
        }

        output.Clear();
        _tick = tick;
        _touchedBodies.Clear();
        _groundNormalByBody.Clear();
        IngestSnapshots(snapshots);
        UpdateRunTimer(tick);
        ProcessEvents(events, output);
        ReArmBounces();
        // The legacy power denominator depends on which motor wheels are off the ground, so the
        // factor is refreshed every tick before any consumer reads it (Contraption.cs:556-582).
        RecomputePowerClusters();
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
        RunSlowSpeedDamping(output);
        RunRopes(output);
        DropCommandsForDestroyedBodies(output);
        CheckChallenges(output);
        CheckObjectives(tick, output);
        StorePreviousVelocities();
        _hasProcessedTick = true;
    }

    /// <summary>
    /// Starts the original's clock: while it has not started, the reference pig's own transform is
    /// compared with the pose <see cref="BeginRun"/> recorded, and the first tick it is
    /// <see cref="RunTimerStartDistance"/> away arms the clock (<c>LevelManager.cs:1431-1441</c> --
    /// that tick itself contributes nothing, because the original returns before accumulating).
    /// The elapsed value is read straight off the tick counters, so nothing else accumulates it.
    /// </summary>
    private void UpdateRunTimer(uint tick)
    {
        if (!_hasRunStart || _runTimerStarted)
        {
            return;
        }

        if (!_pigs.TryGet(_runPig, out _)
            || !_bodies.TryGet(_runPig, out PhysicsBodyLink link)
            || !_kinematicsByBody.TryGetValue(link.Body.Value, out var kinematics))
        {
            return;
        }

        ResolvePartFrame(_runPig, link.Body.Value, kinematics.Position, out PhysicsVector3 pigPosition, out _);
        if (PhysicsVector3.Distance(pigPosition, _runPigStart) < RunTimerStartDistance)
        {
            return;
        }

        _runTimerStarted = true;
        _runTimerStartTick = tick;
    }

    /// <summary>
    /// The original's runtime damping ramp: while the contraption runs, <c>Pig.FixedUpdate</c>
    /// (<c>Pig.cs:249-262</c>) rewrites its own rigidbody's drag <b>and</b> angularDrag every fixed
    /// step — below the content's <c>speedThreshold</c> m/s both become
    /// <c>base + slope * (1 - |v|)</c> (0.2 + 2.5 * (1 - |v|), up to 2.7 at rest), and at or above it
    /// both go back to the part's own pair. The speed is the body's, because the pig's rigidbody is
    /// what the original reads; a body carrying no such part is never touched, and neither is a pig
    /// whose class does not declare the method (<c>KingPig</c>/<c>GoldenPig</c> are
    /// <c>BasePart</c> subclasses).
    /// </summary>
    private void RunSlowSpeedDamping(GameplayTickOutput output)
    {
        var pigs = _pigs.GetEnumerator();
        while (pigs.MoveNext())
        {
            ref readonly PigMarker marker = ref pigs.CurrentValue;
            if (!marker.HasDampingRamp || !_bodies.TryGet(pigs.CurrentId, out PhysicsBodyLink link))
            {
                continue;
            }

            float speed = _kinematicsByBody.TryGetValue(link.Body.Value, out var kinematics)
                ? PhysicsVector3.Distance(kinematics.Velocity, PhysicsVector3.Zero)
                : 0f;
            if (speed >= marker.RampSpeedThreshold)
            {
                output.DampingOverrides.Add(new PartDampingOverride(pigs.CurrentId, marker.OwnLinear, marker.OwnAngular));
                continue;
            }

            // The original writes the ramped expression into *both* fields, so the pig's own
            // angular value (0.05) does not survive the branch -- at the threshold the pair jumps
            // from (0.2, 0.05) to (0.2, 0.2), exactly as Pig.cs:253-254 does.
            float ramped = marker.RampBase + (marker.RampSlope * (1f - speed));
            output.DampingOverrides.Add(new PartDampingOverride(pigs.CurrentId, ramped, ramped));
        }
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
                    // `+ContactNormal` separates BodyA, so each body sees the normal pointing away
                    // from the other one. Keep the most upward of a body's contacts: that is the
                    // surface it stands on, which is what the original's wheel raycast finds.
                    RecordGroundNormal(physicsEvent.BodyA.Value, physicsEvent.ContactNormal);
                    RecordGroundNormal(
                        physicsEvent.BodyB.Value,
                        new PhysicsVector3(
                            -physicsEvent.ContactNormal.X,
                            -physicsEvent.ContactNormal.Y,
                            -physicsEvent.ContactNormal.Z));
                    // Per ADR-002 a strong impact ignites TNT, breaks an egg and fires a detacher; a
                    // gentle touch (resting on ground) must not trigger them. The signal is the pair's
                    // approach speed -- the same estimate the bounce gate uses (ADR-031: the impact
                    // quantity is how fast the pair was closing, not how much either body's velocity
                    // happened to change). Reading it off the event alone is not enough: the backend's
                    // `ContactStarted` for a hard landing arrives after the solver has absorbed most of
                    // it (measured on a box landing at 11.61 m/s: the event carried 2.60 m/s two steps
                    // after the last free tick), so the recent peak is what rescues the ignition -- gap
                    // G116.
                    //
                    // Ignition reads a *persisted* contact too: the original's charge fires on its own
                    // collider's `OnCollisionEnter` (`TNT.cs:78-83`), while our contact events are per
                    // body pair, so a rig already touching the terrain body and then crashing into
                    // another face of that same body is one continuing pair and never ignited (measured
                    // 2026-10-13: a cart drove into the level's own cliff with its charge intact). Only
                    // the ignition moved: the egg, the detacher and the bounce are unchanged reads of
                    // `ContactStarted`, the event that names a pair's first contact.
                    float pairImpact = PairApproachSpeed(physicsEvent);
                    IgniteTntOnBody(physicsEvent.BodyA, pairImpact);
                    IgniteTntOnBody(physicsEvent.BodyB, pairImpact);
                    ExplodePropOnImpact(physicsEvent.BodyA, pairImpact, output);
                    ExplodePropOnImpact(physicsEvent.BodyB, pairImpact, output);
                    if (physicsEvent.Kind == PhysicsEventKind.ContactStarted)
                    {
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

    /// <summary>
    /// Keeps the most upward contact normal a body reported this tick. The original's wheel does not
    /// pick a contact at all: it raycasts along the direction of the last contact it had
    /// (MotorWheel.cs:112-123 records it, :285-287 casts it), so "the surface it stands on" is its
    /// own answer. Picking the most upward normal of the wheel's contacts gives the same answer
    /// wherever the wheel rests on ground, and it stays deterministic where the original's
    /// first-in-array contact would not be.
    /// </summary>
    private void RecordGroundNormal(uint body, PhysicsVector3 outwardNormal)
    {
        if (_groundNormalByBody.TryGetValue(body, out PhysicsVector3 existing) && existing.Y >= outwardNormal.Y)
        {
            return;
        }

        _groundNormalByBody[body] = outwardNormal;
    }

    /// <summary>
    /// Ignites every charge riding the body an impact landed on. The original's charge is ignited by
    /// its **own** collider (`TNT.cs:78-83`), so a TNT welded into a contraption counts even though it
    /// is not that body's single representative entity -- reading the representative alone missed
    /// exactly that case, which is why a rig's TNT never blew up (measured 2026-10-13).
    /// </summary>
    private void IgniteTntOnBody(PhysicsBodyId body, float impactSpeed)
    {
        if (!_membersByBody.TryGetValue(body.Value, out List<uint>? members))
        {
            return;
        }

        foreach (uint memberValue in members)
        {
            EntityId entity = new(memberValue);
            // Each charge's own `m_triggerSpeed` arms it (`TNT.cs:78-83`); a fixture without one falls
            // back to the room's default.
            if (_tnt.TryGet(entity, out TntState tnt)
                && !tnt.Ignited
                && tnt.IgniteOnImpact
                && impactSpeed >= (tnt.TriggerSpeed > 0f ? tnt.TriggerSpeed : _config.TntIgniteImpactSpeed))
            {
                _tnt.Set(entity, tnt with { Ignited = true });
            }
        }
    }

    /// <summary>
    /// A level crate taking a hit: the vanilla `TNT_Box` is a static collider whose own
    /// <c>OnCollisionEnter</c> runs <c>Explode</c> once the collision's relative speed beats its own
    /// `m_triggerSpeed` (`TNTBox.cs:15-19`). <c>Explode</c> then
    /// (<c>TNTBox.cs:44-100</c>) takes every rigidbody a `Physics.OverlapSphere(m_explosionRadius)`
    /// finds, hands it `AddExplosionForce`'s own impulse -- <c>m_explosionImpulse / max(distance, 1)^1.5</c>,
    /// scaled down for a target lighter than 0.4 kg -- along the direction from the crate to it, calls
    /// <c>Explode</c> on every charge the sphere found, and finally hides the crate and deactivates its
    /// GameObject. This reports the crate through <see cref="GameplayTickOutput.CollectedProps"/> (the
    /// channel the client already hides a prop's art through) and drops its own state, so the room takes
    /// its static body out and the same crate can never fire twice.
    /// <para>
    /// One deviation, the same one the charge's own blast makes: the impulse lands at the target body's
    /// own centre of mass with no per-collider divisor, because a PigForge compound is one body where the
    /// original has one rigidbody per collider (<c>ADR-029</c> §偏差).
    /// </para>
    /// </summary>
    private void ExplodePropOnImpact(PhysicsBodyId body, float impactSpeed, GameplayTickOutput output)
    {
        if (!_explosiveProps.TryGetValue(body.Value, out ExplosivePropState crate)
            || impactSpeed < crate.TriggerSpeed
            || !_kinematicsByBody.TryGetValue(body.Value, out var center))
        {
            return;
        }

        _explosiveProps.Remove(body.Value);
        output.CollectedProps.Add(crate.PropIndex);
        PhysicsVector3 origin = center.Position;
        ApplyBlast(origin, crate.Radius, crate.Impulse, output);

        // The sphere detonates the charges it found, exactly as the original's loop calls their own
        // `Explode` -- here their fuse is what arms them.
        IgniteChargesInRadius(default, origin, crate.Radius);
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
        if (_config.RestitutionAppliedNatively)
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
        // The same estimate the impact rules read (see PairApproachSpeed): the backend's reading, the
        // previous tick's velocities, and the peak a free fall left behind.
        float approachSpeed = PairApproachSpeed(physicsEvent);
        // The threshold gates that estimate, not the event's own reading: the backend's contact
        // event understates a landing (the solver has already absorbed part of it by the time the
        // event arrives), so a slow-looking event on a fast landing must still bounce -- and a
        // genuinely slow one must not, which is the original's `m_BounceThreshold: 2`.
        if (approachSpeed <= MinimumBounceApproachSpeed)
        {
            return;
        }

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

    /// <summary>
    /// How fast a contact pair was closing along the surface normal: the strongest of the backend's own
    /// pre-solver reading, the previous step's velocities, and the peak the last few ticks left behind.
    /// Every speed read at the moment a contact event arrives understates the impact, because the
    /// solver's soft recovery starts absorbing before the shapes look like they have met: measured on a
    /// box landing at 11.61 m/s, the backend's <c>ContactStarted</c> arrived two steps after the last
    /// free tick and carried 2.60 m/s, and this layer's own previous-tick velocity was 2.60 as well --
    /// only the peak the free fall left behind still holds the real number, which is why the impact
    /// rules (ADR-002) and the bounce gate (ADR-025's <c>m_BounceThreshold</c>) both read this one
    /// estimate (gap G116).
    /// </summary>
    private float PairApproachSpeed(in PhysicsEvent physicsEvent)
    {
        uint bodyA = physicsEvent.BodyA.Value;
        uint bodyB = physicsEvent.BodyB.Value;
        bool dynamicA = _dynamicBodies.Contains(bodyA);
        bool dynamicB = _dynamicBodies.Contains(bodyB);
        PhysicsVector3 normal = physicsEvent.ContactNormal;
        float approach = MathF.Max(
            ApproachFromHistory(bodyA, bodyB, normal, dynamicA, dynamicB),
            physicsEvent.ApproachSpeed);
        return MathF.Max(approach, PeakApproach(bodyA, bodyB, normal, dynamicA, dynamicB));
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
    /// and enclosure changes all funnel through here; the per-tick path is
    /// <see cref="RecomputePowerClusters"/>).</summary>
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
    /// Recomputes one cluster's factor from its members, on the vanilla declaration defaults:
    /// <c>DynamicPowerSystem</c> is off, so the original runs its *legacy* branch
    /// (Contraption.cs:556-582). Two consequences, both verbatim:
    /// <list type="bullet">
    /// <item>the denominator is the **assembly-time** sum of every member's
    /// <c>m_powerConsumption</c> (Contraption.cs:1378-1379). The per-step re-sum that filters on
    /// <c>IsEnabled</c> (:2633-2644) only runs when the switch is on, so a switched-off consumer
    /// still consumes;</item>
    /// <item>it then subtracts <c>0.9 * m_powerConsumption</c> for every motor wheel of the
    /// cluster whose <c>HasContact</c> is false, i.e. a wheel off the ground frees 90% of its
    /// draw.</item>
    /// </list>
    /// The engine side is unchanged: only an enclosed engine is a valid part at all (Engine.cs:61).
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

            if (power.PowerConsumption > 0f)
            {
                consumption += power.PowerConsumption;
            }
        }

        var motors = _motors.GetEnumerator();
        while (motors.MoveNext())
        {
            if (PowerClusterKey(motors.CurrentId) != key
                || !_powers.TryGet(motors.CurrentId, out PowerState motor)
                || motor.PowerConsumption <= 0f
                || !_bodies.TryGet(motors.CurrentId, out PhysicsBodyLink link)
                || IsMotorWheelGrounded(link.Body))
            {
                continue;
            }

            consumption -= 0.9f * motor.PowerConsumption;
        }

        _powerFactorByCluster[key] = ComputePowerFactor(enginePower, consumption);
    }

    /// <summary>
    /// Whether a motor wheel's body counts as on the ground (the original's
    /// <c>MotorWheel.HasContact</c>): true until a processed tick has failed to report a contact
    /// for it, which is the same proxy <c>RunMotors</c> gates its drive on.
    /// </summary>
    private bool IsMotorWheelGrounded(PhysicsBodyId body) =>
        !_hasProcessedTick || _touchedBodies.Contains(body.Value);

    /// <summary>
    /// Recomputes every cluster's factor. The legacy denominator depends on which motor wheels are
    /// off the ground, and that changes with the physics, so the per-tick path has to refresh it
    /// (membership and switch changes still recompute eagerly through
    /// <see cref="RecomputePowerCluster(EntityId)"/>).
    /// </summary>
    private void RecomputePowerClusters()
    {
        _powerClusterKeys.Clear();
        var powers = _powers.GetEnumerator();
        while (powers.MoveNext())
        {
            uint key = PowerClusterKey(powers.CurrentId);
            if (!_powerClusterKeys.Contains(key))
            {
                _powerClusterKeys.Add(key);
            }
        }

        foreach (uint key in _powerClusterKeys)
        {
            RecomputePowerCluster(key);
        }
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
            EntityId motorEntity = motors.CurrentId;
            if (!_bodies.TryGet(motorEntity, out PhysicsBodyLink link)
                || !_kinematicsByBody.ContainsKey(link.Body.Value))
            {
                continue;
            }

            MotorState motor = motors.CurrentValue;
            if (!IsDriven(motorEntity))
            {
                // `m_thrustTimer = 0f; m_thrust = 0f` (MotorWheel.cs:277-279): a motor that is switched
                // off re-arms from zero, so switching it back on ramps all over again.
                if (motor.ThrustTimer != 0f)
                {
                    _motors.Set(motorEntity, motor with { ThrustTimer = 0f });
                }

                continue;
            }

            // The ramp advances on every driven tick, ground or not: the original advances the timer
            // before it raycasts (MotorWheel.cs:259-261), so an airborne wheel keeps winding up.
            motor = motor with { ThrustTimer = MathF.Min(1f, motor.ThrustTimer + MotorThrustRampSecondsPerTick) };
            _motors.Set(motorEntity, motor);

            // MotorWheel.FixedUpdate drives off the surface the wheel stands on: it raycasts from
            // the wheel pivot and returns without driving when it finds nothing (MotorWheel.cs:285-286).
            // No contact normal this tick is that same "no ground".
            if (!_groundNormalByBody.TryGetValue(link.Body.Value, out PhysicsVector3 groundNormal))
            {
                continue;
            }

            // `Vector3.Cross(hitInfo.normal, Vector3.forward)` (MotorWheel.cs:288): the ground's
            // tangent, not a world axis. On flat ground that is +X; on a ramp it is the slope,
            // so a driven rig climbs it. Measured on the original's editor (MotorWheelProbe,
            // Unity 2021.3.45f2): a 30 degree ramp turns it to (0.866, 0.5) and the rig travels
            // (7.23, 4.18); a 45 degree one to (0.7071, 0.7071).
            PhysicsVector3 tangent = new(groundNormal.Y, -groundNormal.X, 0f);
            if (PhysicsVector3.Distance(tangent, PhysicsVector3.Zero) <= float.Epsilon)
            {
                // A vertical surface has no tangent in the plane.
                continue;
            }

            // Power gating (spec docs/specs/power-system.md §4 items 3-4): the drive is scaled by
            // the cluster's power factor, so a wheel does not move at all without an enclosed
            // engine in that cluster (Contraption.cs:540-556, MotorWheel.cs:101-109).
            if (!TryDriveFactor(motors.CurrentId, out float powerFactor))
            {
                continue;
            }

            float directionX = motor.DirectionX;
            // A gearbox on the same body flips the drive (reverse gear).
            if (reverseBodies.Contains(link.Body.Value))
            {
                directionX = -directionX;
            }

            // `SpeedInDirection(base.transform.right)` (MotorWheel.cs:287): the gate reads the speed
            // along the WHEEL's own right axis, which is the chassis's live rotation this wheel was
            // built into (a wheel body spins about its axle, so its own pose cannot answer that --
            // see LinkWheelAttach). The original's direction formula never looks at the part, which
            // the probe confirmed the other way round too: a wheel rotated 90 degrees still drove
            // along the ground tangent while its gate read the perpendicular axis.
            ResolveWheelRight(motors.CurrentId, link.Body.Value, out PhysicsVector3 wheelRight);

            // MotorWheel.cs:292-299 gates the drive on that reading -- `num2 < m_maximumSpeed &&
            // num2 > -m_maximumSpeed` -- and tapers it as sqrt(1 - |num2| / m_maximumSpeed), so the
            // rig approaches the cap asymptotically and never passes it. The cap itself is
            // 15 * powerFactor (MotorWheel.cs:101-103).
            float maximumSpeed = MotorWheelMaximumSpeed * powerFactor;
            float axialSpeed = PhysicsVector3.Dot(_kinematicsByBody[link.Body.Value].Velocity, wheelRight);
            if (MathF.Abs(axialSpeed) >= maximumSpeed)
            {
                continue;
            }

            // `num3 = m_thrust * m_maximumForce * f` (MotorWheel.cs:294-296): the ramped motor force
            // times the speed taper, both of which scale the drive (gap G110).
            float thrust = motor.ImpulsePerTick * directionX * powerFactor
                * MathF.Pow(motor.ThrustTimer, MotorThrustRampExponent)
                * MathF.Sqrt(1f - (MathF.Abs(axialSpeed) / maximumSpeed));

            output.Commands.Add(PhysicsCommand.ApplyImpulse(
                link.Body,
                new PhysicsVector3(tangent.X * thrust, tangent.Y * thrust, 0f),
                _kinematicsByBody[link.Body.Value].Position));
        }
    }

    /// <summary>
    /// The world direction of a wheel's own <c>transform.right</c>. A hinged wheel's frame belongs
    /// to its chassis (LinkWheelAttach); without that declaration the wheel is rigid to its own body,
    /// which is what a wheel welded into a compound is.
    /// </summary>
    private void ResolveWheelRight(EntityId entity, uint body, out PhysicsVector3 right)
    {
        if (_wheelAttachByEntity.TryGetValue(entity.Value, out WheelAttach attach)
            && _bodies.TryGet(attach.Parent, out PhysicsBodyLink parentLink)
            && _rotationByBody.TryGetValue(parentLink.Body.Value, out PhysicsQuaternion parentRotation))
        {
            right = (parentRotation * attach.LocalRotation).Rotate(new PhysicsVector3(1f, 0f, 0f));
            return;
        }

        ResolvePartFrame(
            entity,
            body,
            _kinematicsByBody.TryGetValue(body, out var kinematics) ? kinematics.Position : PhysicsVector3.Zero,
            out _,
            out PhysicsQuaternion partRotation);
        right = partRotation.Rotate(new PhysicsVector3(1f, 0f, 0f));
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

    // ------------------------------------------------------------------ ropes
    // The original's rope is a chain of segment bodies with no joints between them and a hand-written
    // force chain (`Rope.cs:334-462`). The constants below are that method's literals; tools/bple-ropes
    // asserts each one against the source, so they are never hand-tuned here.
    private const float RopeForcePerTick = 1f / 60f;
    private const float RopeSpacing = 0.5f;
    private const float RopeSlack = 0.4f;
    private const float RopePullForce = 70f;
    private const float RopePushForce = 50f;
    private const float RopeBreakInner = 1.25f;
    private const float RopeBreakEnd = 1.75f;
    private const float RopeStretchLimit = 1.18f;
    private const float RopeStretchResponseSteepness = 8f;
    private const float RopeStretchResponseBias = 0.01798f;
    private const float RopeStretchResponseScale = 0.9641f;
    private const float RopeStretchFactorCap = 0.4f;
    private const float RopeEndpointPullForce = 750f;
    private const float RopeEndpointPullCap = 300f;
    private const float RopeAirDrag = 0.02f;
    private const float RopeNodeDragBase = 0.5f;
    private const float RopeNodeDragScale = 6f;
    private const float RopeNodeDragSpeedFactor = 4f;
    private const float RopeSegmentAngularDamping = 0.5f;

    private sealed class RopeState(EntityId host, PhysicsBodyId[] segments, PhysicsBodyId left, PhysicsBodyId right)
    {
        public EntityId Host { get; } = host;

        public PhysicsBodyId[] Segments { get; } = segments;

        /// <summary>The non-rope neighbour at each end; invalid when that end is free.</summary>
        public PhysicsBodyId Left { get; set; } = left;

        public PhysicsBodyId Right { get; set; } = right;

        /// <summary>The cut link index (1..n-1), or -1 while intact (`Rope.cs:370`).</summary>
        public int CutLink { get; set; } = -1;

        public float PreviousStretch { get; set; }
    }

    /// <summary>
    /// Declares one placed rope (`docs/specs/rope.md` §7): the segment bodies the room
    /// materialized and the non-rope bodies its two ends hinge to (invalid for a free end).
    /// </summary>
    public void AddRope(EntityId host, ReadOnlySpan<PhysicsBodyId> segments, PhysicsBodyId left, PhysicsBodyId right)
    {
        if (segments.Length < 2)
        {
            throw new ArgumentException("A rope needs at least two segments.", nameof(segments));
        }

        _ropes[host.Value] = new RopeState(host, segments.ToArray(), left, right);
    }

    /// <summary>
    /// Re-points a rope's connectivity anchors after a rope-to-rope joint is made (the room calls
    /// this once all ropes exist): the rules' `AreConnected` treats each intact rope as an edge
    /// between its two ends, the original's own rope-link component (`Contraption.cs:1111-1125`).
    /// </summary>
    public void SetRopeAnchors(EntityId host, PhysicsBodyId left, PhysicsBodyId right)
    {
        if (_ropes.TryGetValue(host.Value, out RopeState? rope))
        {
            rope.Left = left;
            rope.Right = right;
        }
    }

    /// <summary>Removes one rope's rule state (its host was destroyed).</summary>
    public void RemoveRope(EntityId host) => _ropes.Remove(host.Value);

    /// <summary>Drops every rope: the room's `Retry`/build-mode re-entry destroys the bodies.</summary>
    public void ClearRopes() => _ropes.Clear();

    /// <summary>
    /// Cuts the link before <paramref name="link"/> (the original's `m_cutIndex`), if the rope is
    /// still intact. The room resolves the click position to a segment index and clamps it.
    /// </summary>
    public void CutRope(EntityId host, int link)
    {
        if (_ropes.TryGetValue(host.Value, out RopeState? rope) && rope.CutLink < 0)
        {
            rope.CutLink = Math.Clamp(link, 1, rope.Segments.Length - 1);
        }
    }

    /// <summary>True once this rope has been cut (test/room reading).</summary>
    public bool IsRopeCut(EntityId host) => _ropes.TryGetValue(host.Value, out RopeState? rope) && rope.CutLink >= 0;

    /// <summary>The cut link index, or -1 while intact (test reading).</summary>
    public int RopeCutLink(EntityId host) => _ropes.TryGetValue(host.Value, out RopeState? rope) ? rope.CutLink : -1;

    private void RunRopes(GameplayTickOutput output)
    {
        if (_ropes.Count == 0)
        {
            return;
        }

        foreach (RopeState rope in _ropes.Values)
        {
            PhysicsBodyId[] segments = rope.Segments;
            int count = segments.Length;
            // The original's trigger button (`ProcessTouch()` with no position) cuts the middle
            // node (`Rope.cs:169-190`); the room's own SetPartActive path only sets the flag.
            if (rope.CutLink < 0 && TryConsumeButtonPress(rope.Host))
            {
                rope.CutLink = count / 2;
            }

            // Every node shares one drag, computed from the two end speeds (`Rope.cs:366`); the
            // angular term is the RopeSegment prefab's own 0.5.
            float endSpeed = 0f;
            if (_kinematicsByBody.TryGetValue(segments[0].Value, out var first))
            {
                endSpeed = MathF.Sqrt(PhysicsVector3.Dot(first.Velocity, first.Velocity));
            }

            if (_kinematicsByBody.TryGetValue(segments[count - 1].Value, out var last))
            {
                endSpeed = MathF.Max(endSpeed, MathF.Sqrt(PhysicsVector3.Dot(last.Velocity, last.Velocity)));
            }

            float drag = RopeNodeDragBase + (RopeNodeDragScale / (1f + (RopeNodeDragSpeedFactor * endSpeed * endSpeed)));
            foreach (PhysicsBodyId segment in segments)
            {
                output.BodyDampingOverrides.Add(new BodyDampingOverride(segment, drag, RopeSegmentAngularDamping));
            }

            // Air drag per node: q = v - 0.4*v_prev - 0.4*v_next, force -0.02*|q|^2*v̂ (Rope.cs:356-365).
            for (int index = 0; index < count; index++)
            {
                if (!_kinematicsByBody.TryGetValue(segments[index].Value, out var mine))
                {
                    continue;
                }

                PhysicsVector3 previous = index > 0 && _kinematicsByBody.TryGetValue(segments[index - 1].Value, out var before)
                    ? before.Velocity
                    : mine.Velocity;
                PhysicsVector3 next = index < count - 1 && _kinematicsByBody.TryGetValue(segments[index + 1].Value, out var after)
                    ? after.Velocity
                    : mine.Velocity;
                PhysicsVector3 relative = mine.Velocity - (previous * 0.4f) - (next * 0.4f);
                float squared = PhysicsVector3.Dot(relative, relative);
                if (squared <= 0f)
                {
                    continue;
                }

                PhysicsVector3 direction = PhysicsVector3.Normalize(mine.Velocity);
                if (direction == PhysicsVector3.Zero)
                {
                    continue;
                }

                output.Commands.Add(PhysicsCommand.ApplyImpulse(
                    segments[index],
                    direction * (-RopeAirDrag * squared * RopeForcePerTick),
                    mine.Position));
            }

            // Link forces: pull past the rest length, push inside the slack band (Rope.cs:370-390).
            for (int link = 1; link < count; link++)
            {
                if (link == rope.CutLink)
                {
                    continue;
                }

                if (!_kinematicsByBody.TryGetValue(segments[link - 1].Value, out var before)
                    || !_kinematicsByBody.TryGetValue(segments[link].Value, out var after))
                {
                    continue;
                }

                PhysicsVector3 delta = after.Position - before.Position;
                float distance = MathF.Sqrt(PhysicsVector3.Dot(delta, delta));
                if (distance <= 0f)
                {
                    continue;
                }

                PhysicsVector3 direction = delta * (1f / distance);
                float force;
                if (distance > RopeSpacing)
                {
                    float excess = distance - RopeSpacing;
                    excess = excess < 1f ? MathF.Pow(excess, 0.75f) : excess * excess;
                    excess = Math.Clamp(excess, 0f, 2f);
                    force = RopePullForce * excess;
                }
                else if (distance < RopeSlack)
                {
                    force = RopePushForce * (distance - RopeSlack);
                }
                else
                {
                    continue;
                }

                PhysicsVector3 impulse = direction * (force * RopeForcePerTick);
                output.Commands.Add(PhysicsCommand.ApplyImpulse(segments[link - 1], impulse, before.Position));
                output.Commands.Add(PhysicsCommand.ApplyImpulse(segments[link], impulse * -1f, after.Position));
            }

            if (rope.CutLink >= 0)
            {
                continue;
            }

            // Tension break first, then the stretch pull (Rope.cs:399-462). Both stop once cut.
            float total = 0f;
            bool broke = false;
            for (int link = 1; link < count; link++)
            {
                if (!_kinematicsByBody.TryGetValue(segments[link - 1].Value, out var before)
                    || !_kinematicsByBody.TryGetValue(segments[link].Value, out var after))
                {
                    continue;
                }

                float distance = PhysicsVector3.Distance(before.Position, after.Position);
                total += distance;
                bool inner = link > 1 && link < count - 2;
                if (distance > (inner ? RopeBreakInner : RopeBreakEnd))
                {
                    rope.CutLink = link;
                    broke = true;
                    break;
                }
            }

            if (broke)
            {
                continue;
            }

            float rest = RopeStretchLimit * RopeSpacing * count;
            float stretch = (total / rest) - 1f;
            float factor = RopeStretchFactorCap * RopeResponseCurve(stretch / RopeStretchFactorCap);
            factor = Math.Clamp(factor, 0f, RopeStretchFactorCap);
            float change = factor - rope.PreviousStretch;
            rope.PreviousStretch = factor;
            if (total <= rest)
            {
                continue;
            }

            if (change < 0f)
            {
                factor *= 0.7f;
            }

            float pull = Math.Clamp(RopeEndpointPullForce * factor, 0f, RopeEndpointPullCap) * RopeForcePerTick;
            if (rope.Left.IsValid
                && _kinematicsByBody.TryGetValue(segments[0].Value, out var head)
                && _kinematicsByBody.TryGetValue(segments[1].Value, out var headNext))
            {
                PhysicsVector3 direction = PhysicsVector3.Normalize(headNext.Position - head.Position);
                output.Commands.Add(PhysicsCommand.ApplyImpulse(segments[0], direction * pull, head.Position));
            }

            if (rope.Right.IsValid
                && _kinematicsByBody.TryGetValue(segments[count - 1].Value, out var tail)
                && _kinematicsByBody.TryGetValue(segments[count - 2].Value, out var tailBefore))
            {
                PhysicsVector3 direction = PhysicsVector3.Normalize(tailBefore.Position - tail.Position);
                output.Commands.Add(PhysicsCommand.ApplyImpulse(segments[count - 1], direction * pull, tail.Position));
            }
        }
    }

    /// <summary>`(sigmoid(8(t-0.5)) - 0.01798) / 0.9641`, the original's stretch response curve
    /// (`Rope.cs:326-332`).</summary>
    private static float RopeResponseCurve(float value) =>
        ((1f / (1f + MathF.Exp(-RopeStretchResponseSteepness * (value - 0.5f)))) - RopeStretchResponseBias)
        / RopeStretchResponseScale;

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
            bool hasState = _tnt.TryGet(entity, out TntState state);
            bool chain = hasState && state.ChainDetonate;
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
                // The chain reaches as far as this charge's own blast (`TNT.cs:129,152-156`), which is
                // the component's radius, not the fixture's default.
                IgniteChargesInRadius(
                    entity,
                    blastCenter,
                    hasState && state.Radius > 0f ? state.Radius : _config.TntBlastRadius);
            }
        }
    }

    /// <summary>Original TNT chain: every other charge inside the blast radius lights up
    /// (its own fuse runs from the next tick). Sorted entity ids keep it deterministic;
    /// the chain never recurses inside the same tick.</summary>
    private void IgniteChargesInRadius(EntityId source, PhysicsVector3 center, float radius)
    {

        List<uint> ignited = null!;
        var charges = _tnt.GetEnumerator();
        while (charges.MoveNext())
        {
            // `chainTarget` false is the original's `!(component is AlienTNT)`: a normal charge never
            // detonates an alien one (`TNT.cs:152-156`).
            if (charges.CurrentId == source || charges.CurrentValue.Ignited || !charges.CurrentValue.ChainTarget)
            {
                continue;
            }

            if (!_bodies.TryGet(charges.CurrentId, out PhysicsBodyLink chargeLink)
                || !_kinematicsByBody.TryGetValue(chargeLink.Body.Value, out var kinematics))
            {
                continue;
            }

            if (PhysicsVector3.Distance(kinematics.Position, center) < radius)
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

        TntState state = _tnt.TryGet(tntEntity, out TntState tnt) ? tnt : default;
        // The blast radiates from the charge part, not from the compound's centre of mass: a
        // welded charge shares its body with the parts around it, and the original measures every
        // target from TNT.transform.position (TNT.cs:236-252 AddExplosionForce). The charge's own
        // body is included, so the very contraption holding the charge is driven -- and torn --
        // by the blast the way the original's per-part rigidbodies are.
        ResolvePartFrame(tntEntity, link.Body.Value, center.Position, out PhysicsVector3 origin, out _);
        ApplyBlast(
            origin,
            state.Radius > 0f ? state.Radius : _config.TntBlastRadius,
            state.Impulse > 0f ? state.Impulse : _config.TntBlastImpulse,
            output);

        // The plain charge removes itself (`TNT.cs:170` `RemovePart`); an `AlienTNT` keeps the part and
        // clears `m_triggered` in `LateUpdate`, so it re-arms -- the vanilla cooling time is 0
        // (`AlienTNT.cs:8-52`, `INDeclarationSettingsExp.json`). Content declares the split as
        // `repeatable`, and the fuse it was authored with is restored with the state.
        if (state.Repeatable)
        {
            _tnt.Set(tntEntity, state with { Ignited = false, FuseTicks = state.ReloadTicks });
            return;
        }

        DestroyEntity(tntEntity, output);
    }

    /// <summary>
    /// The original's blast, one implementation for both the charge and a level's own crate
    /// (`TNT.cs:211-233` `AddExplosionForce`): every dynamic body inside <paramref name="radius"/>
    /// takes <c>impulse / max(distance, 1)^1.5</c> along the direction from the blast centre to it,
    /// scaled down for a target lighter than 0.4 kg exactly as the original does. Sorted body ids keep
    /// the command order deterministic across replays, and the impulse lands at the target's own centre
    /// of mass (a compound is one body here where the original has one rigidbody per collider).
    /// </summary>
    private void ApplyBlast(PhysicsVector3 origin, float radius, float impulse, GameplayTickOutput output)
    {
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
            if (distance >= radius)
            {
                continue;
            }

            float magnitude = impulse / MathF.Pow(MathF.Max(distance, 1f), 1.5f);
            if (_massByBody.TryGetValue(bodyId, out float mass) && mass > 0f)
            {
                if (mass < 0.1f)
                {
                    magnitude *= mass;
                }
                else if (mass < 0.4f)
                {
                    magnitude *= mass / 0.4f;
                }
            }

            PhysicsVector3 direction = distance > float.Epsilon
                ? PhysicsVector3.Normalize(kinematics.Position - origin)
                : new PhysicsVector3(0f, 1f, 0f);
            // Impulse at the target's own centre of mass: blasts push, they do not spin.
            output.Commands.Add(PhysicsCommand.ApplyImpulse(
                new PhysicsBodyId(bodyId),
                direction * magnitude,
                kinematics.Position));
        }
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

    /// <summary>
    /// The level's challenge markers (`docs/specs/level-challenges.md` §4): once the pig reaches a prop's
    /// own trigger box, that prop is collected for the rest of the run. The original tests the part whose
    /// **collider** entered the box, and accepts any part of the pig's own connected component
    /// (`Goal.cs:18-20` -&gt; `IsConnectedToPig`, plus `CheckIfPartReachedGoal`'s same-component 2.5 m
    /// fallback, `Goal.cs:31-53`) -- so a rig whose frame sweeps the box collects it even though the pig
    /// itself never touched it, which is what a car built around the pig does (measured 2026-10-13: a
    /// cart drove straight through the box and nothing happened). A welded member rides the pig's body; a
    /// hinged wheel is a body of its own, and both answer
    /// <see cref="AreConnected"/>, the joint graph the original's component is.
    /// </summary>
    private void CheckChallenges(GameplayTickOutput output)
    {
        if (_config.ChallengeTriggers is not { Count: > 0 } triggers)
        {
            return;
        }

        var pigs = _pigs.GetEnumerator();
        while (pigs.MoveNext())
        {
            if (!_bodies.TryGet(pigs.CurrentId, out _))
            {
                continue;
            }

            uint cluster = PowerClusterKey(pigs.CurrentId);
            foreach (var body in _membersByBody)
            {
                if (!_kinematicsByBody.TryGetValue(body.Key, out var kinematics))
                {
                    continue;
                }

                foreach (uint memberValue in body.Value)
                {
                    EntityId member = new(memberValue);
                    if (PowerClusterKey(member) != cluster)
                    {
                        continue;
                    }

                    ResolvePartFrame(member, body.Key, kinematics.Position, out PhysicsVector3 position, out _);
                    foreach (ChallengeTriggerDefinition trigger in triggers)
                    {
                        if (_collectedChallenges.Contains(trigger.PropIndex) || !trigger.Zone.Contains(position))
                        {
                            continue;
                        }

                        _collectedChallenges.Add(trigger.PropIndex);
                        output.CollectedProps.Add(trigger.PropIndex);
                    }
                }
            }
        }
    }

    private void CheckObjectives(uint tick, GameplayTickOutput output)
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
                // `GameMode.NotifyGoalReachedByPart` (GameMode.cs:336-354) records the pig in
                // `PartsInGoal` and nothing takes it back, so a pig that came through and left again
                // still counts when the egg it waits for arrives afterwards.
                _goalPigArrived = true;
                continue;
            }

            if (_config.CameraLimits is CameraLimits cameraLimits)
            {
                // The original's bound is the level's own camera rectangle tested against the pig's own
                // transform (Pig.cs:396-403), not the cluster's centre: the pig sits somewhere inside a
                // contraption and the rectangle is generous enough that only a real escape leaves it.
                // The answer is the building state (GameMode.cs:384-387), which the room runs.
                ResolvePartFrame(pigs.CurrentId, link.Body.Value, kinematics.Position, out PhysicsVector3 pigPosition, out _);
                if (!cameraLimits.Contains(pigPosition))
                {
                    output.PigsOutOfBounds.Add(pigs.CurrentId);
                    return;
                }
            }
            else if (!_config.MapBounds.Contains(kinematics.Position))
            {
                // A document with no camera limits (PigForge's own v1 slope/terrain rooms): the coarse
                // map box stays the pig's bound and leaving it still ends the run.
                Phase = GameplayPhase.Failed;
                RestartRequested = true;
                return;
            }
        }

        if (_goalExtraPartRequired && !_goalExtraArrived)
        {
            CheckGoalExtraArrival();
        }

        if (_goalPigArrived && (!_goalExtraPartRequired || _goalExtraArrived))
        {
            Phase = GameplayPhase.Won;
            return;
        }

        if (_config.MaxTicks > 0 && tick >= _config.MaxTicks)
        {
            Phase = GameplayPhase.Failed;
        }
    }

    /// <summary>
    /// The goal's own part test (`Goal.CheckIfPartReachedGoal`, `Goal.cs:34-66`): every part of the
    /// family the level also wants counts when its own transform reaches the zone, exactly as the
    /// original records the part's type in <c>PartsInGoal</c> from the collider that entered.
    /// </summary>
    private void CheckGoalExtraArrival()
    {
        foreach (uint entityValue in _goalTargetParts)
        {
            EntityId entity = new(entityValue);
            if (!_bodies.TryGet(entity, out PhysicsBodyLink link)
                || !_kinematicsByBody.TryGetValue(link.Body.Value, out var kinematics))
            {
                continue;
            }

            ResolvePartFrame(entity, link.Body.Value, kinematics.Position, out PhysicsVector3 position, out _);
            if (_config.GoalZone.Contains(position))
            {
                _goalExtraArrived = true;
                return;
            }
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
        _goalTargetParts.Remove(entity.Value);
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
        _groundNormalByBody.Clear();
        _brokenJoints.Clear();
        _hasRunStart = false;
        _runTimerStarted = false;
        _wheelAttachByEntity.Clear();
        var tntComponents = _tnt.GetEnumerator();
        while (tntComponents.MoveNext())
        {
            _tnt.Set(tntComponents.CurrentId, tntComponents.CurrentValue with { Ignited = false });
        }

        // A relaunch is a fresh contraption in the original (the parts are rebuilt from the proto), so
        // every motor's own ramp starts from zero again (MotorWheel.cs:38,259-261; gap G110).
        var rebuiltMotors = _motors.GetEnumerator();
        while (rebuiltMotors.MoveNext())
        {
            _motors.Set(rebuiltMotors.CurrentId, rebuiltMotors.CurrentValue with { ThrustTimer = 0f });
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
        _groundNormalByBody.Clear();
        _brokenJoints.Clear();
        _hasRunStart = false;
        _runTimerStarted = false;
        _wheelAttachByEntity.Clear();
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
        // The room re-runs `SetupFromLevel` after a clear reset, which re-installs the level's goal
        // requirement and re-registers its actors; a stale set could otherwise keep a part from a
        // previous level family answering for this one.
        _goalExtraPartRequired = false;
        _goalTargetParts.Clear();
        _goalPigArrived = false;
        _goalExtraArrived = false;
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
        _goalTargetParts.Remove(entity.Value);
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
