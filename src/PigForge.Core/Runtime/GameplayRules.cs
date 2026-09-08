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
    uint MaxTicks = 0)
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

    public void Clear()
    {
        Commands.Clear();
        DestroyedEntities.Clear();
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
    private readonly WheelStore _wheels;
    private readonly PigStore _pigs;
    private readonly EggStore _eggs;
    private readonly WingStore _wings;
    private readonly TailStore _tails;
    private readonly UmbrellaStore _umbrellas;
    private readonly PhysicsBodyStore _bodies;
    private readonly GameplayConfig _config;

    private readonly Dictionary<uint, EntityId> _entitiesByBody = new();
    private readonly Dictionary<uint, (PhysicsVector3 Position, PhysicsVector3 Velocity)> _kinematicsByBody = new();
    private readonly HashSet<uint> _touchedBodies = new();
    private readonly HashSet<uint> _brokenJoints = new();
    private readonly HashSet<uint> _dynamicBodies = new();
    private readonly Dictionary<uint, PhysicsVector3> _previousVelocities = new();
    private int _alivePigs;

    public GameplayRules(
        EntityStore entities,
        MotorStore motors,
        BalloonStore balloons,
        FanStore fans,
        SpringStore springs,
        RocketStore rockets,
        TntStore tnt,
        WheelStore wheels,
        PigStore pigs,
        EggStore eggs,
        WingStore wings,
        TailStore tails,
        UmbrellaStore umbrellas,
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
        _eggs = eggs ?? throw new ArgumentNullException(nameof(eggs));
        _wheels = wheels ?? throw new ArgumentNullException(nameof(wheels));
        _pigs = pigs ?? throw new ArgumentNullException(nameof(pigs));
        _wings = wings ?? throw new ArgumentNullException(nameof(wings));
        _tails = tails ?? throw new ArgumentNullException(nameof(tails));
        _umbrellas = umbrellas ?? throw new ArgumentNullException(nameof(umbrellas));
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
    }

    public void AddPig(EntityId entity)
    {
        _pigs.Set(entity, default);
        _alivePigs++;
    }

    public void AddTnt(EntityId entity, ushort fuseTicks) =>
        _tnt.Set(entity, new TntState(fuseTicks, Ignited: false));

    public void AddMotor(EntityId entity, float impulsePerTick, float directionX) =>
        _motors.Set(entity, new MotorState(impulsePerTick, directionX));

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

    public void AddWheel(EntityId entity) => _wheels.Set(entity, default);
    public void Tick(uint tick, ReadOnlySpan<PhysicsEvent> events, ReadOnlySpan<PhysicsBodySnapshot> snapshots, GameplayTickOutput output)
    {
        if (Phase != GameplayPhase.Playing)
        {
            return;
        }

        output.Clear();
        _touchedBodies.Clear();
        IngestSnapshots(snapshots);
        ProcessEvents(events, output);
        RunMotors(output);
        RunBalloons(output);
        RunFans(output);
        RunAerodynamics(output);
        RunSprings(output);
        RunRockets(output);
        RunTntFuses(output);
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
            || tnt.Ignited)
        {
            return;
        }

        _tnt.Set(entity, tnt with { Ignited = true });
    }
    private void RunMotors(GameplayTickOutput output)
    {
        var motors = _motors.GetEnumerator();
        while (motors.MoveNext())
        {
            if (!_bodies.TryGet(motors.CurrentId, out PhysicsBodyLink link)
                || !_kinematicsByBody.ContainsKey(link.Body.Value))
            {
                continue;
            }

            // Wheel-driven motors only push while their wheel touches something this tick.
            if (_wheels.TryGet(motors.CurrentId, out _) && !_touchedBodies.Contains(link.Body.Value))
            {
                continue;
            }

            MotorState motor = motors.CurrentValue;
            output.Commands.Add(PhysicsCommand.ApplyImpulse(
                link.Body,
                new PhysicsVector3(motor.ImpulsePerTick * motor.DirectionX, 0f, 0f),
                _kinematicsByBody[link.Body.Value].Position));
        }
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
            // lift; it does not require ground contact (pure vertical lift).
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
            rocket = rocket with { Ignited = true };
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
            Explode(new EntityId(entityValue), output);
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
            output.Commands.RemoveAll(command => !_dynamicBodies.Contains(command.Body.Value));
        }
    }

    private void CheckObjectives(uint tick)
    {
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
        _bodies.Remove(entity);
    }

    /// <summary>
    /// Prepares a fresh run after re-entering build mode with the keep policy: per-body
    /// telemetry is dropped (bodies are recreated and re-linked on the next Start),
    /// charges are re-armed with their remaining fuse, and the outcome reverts to
    /// playing. Frozen entities keep their roles, pig count and fuse remainders.
    /// </summary>
    public void ResetForRebuild()
    {
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

        Phase = GameplayPhase.Playing;
        RestartRequested = false;
    }

    /// <summary>Drops all gameplay state for a full level reset (clear policy).</summary>
    public void ResetAll()
    {
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
        _bodies.Clear();
        _alivePigs = 0;
        Phase = GameplayPhase.Playing;
        RestartRequested = false;
    }

    private void DestroyEntity(EntityId entity, GameplayTickOutput output)
    {
        if (_pigs.TryGet(entity, out _))
        {
            _alivePigs--;
        }

        if (_bodies.TryGet(entity, out PhysicsBodyLink link))
        {
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
        _bodies.Remove(entity);
        _entities.Destroy(entity);
        output.DestroyedEntities.Add(entity);
    }
}
