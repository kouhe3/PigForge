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
    float TntIgniteImpactSpeed)
{
    public static GameplayConfig Default { get; } = new(
        GoalZone: new GameplayZone(new PhysicsVector3(-9, 0, -2), new PhysicsVector3(-7, 4, 2)),
        MapBounds: new GameplayZone(new PhysicsVector3(-100, -5, -20), new PhysicsVector3(100, 60, 20)),
        TntBlastRadius: 4f,
        TntBlastImpulse: 25f,
        TntIgniteImpactSpeed: 5f);
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
    private readonly TntStore _tnt;
    private readonly WheelStore _wheels;
    private readonly PigStore _pigs;
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
        TntStore tnt,
        WheelStore wheels,
        PigStore pigs,
        PhysicsBodyStore bodies,
        GameplayConfig config)
    {
        _entities = entities ?? throw new ArgumentNullException(nameof(entities));
        _motors = motors ?? throw new ArgumentNullException(nameof(motors));
        _tnt = tnt ?? throw new ArgumentNullException(nameof(tnt));
        _wheels = wheels ?? throw new ArgumentNullException(nameof(wheels));
        _pigs = pigs ?? throw new ArgumentNullException(nameof(pigs));
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

        _entitiesByBody.Add(body.Value, entity);
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
        ProcessEvents(events);
        RunMotors(output);
        RunTntFuses(output);
        DropCommandsForDestroyedBodies(output);
        CheckObjectives();
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

    private void ProcessEvents(ReadOnlySpan<PhysicsEvent> events)
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

    private void CheckObjectives()
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
        _tnt.Remove(entity);
        _wheels.Remove(entity);
        _pigs.Remove(entity);
        _bodies.Remove(entity);
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
        _tnt.Remove(entity);
        _wheels.Remove(entity);
        _pigs.Remove(entity);
        _bodies.Remove(entity);
        _entities.Destroy(entity);
        output.DestroyedEntities.Add(entity);
    }
}
