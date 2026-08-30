using PigForge.Physics.Abstractions;

namespace PigForge.Core;

public enum GameplayPhase
{
    Playing,
    Won,
    Failed
}

public sealed record GameplayConfig(
    uint MaxTicks,
    float ImpactSpeedThreshold,
    float ImpactDamageFactor,
    float TntBlastRadius,
    float TntBlastImpulse,
    float TntBlastDamage)
{
    public static GameplayConfig Default { get; } = new(
        MaxTicks: 600,
        ImpactSpeedThreshold: 5f,
        ImpactDamageFactor: 1f,
        TntBlastRadius: 4f,
        TntBlastImpulse: 12f,
        TntBlastDamage: 25f);
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
/// Runtime gameplay rules (motors, wheels, pigs, TNT, breakage, level outcome) that
/// consume physics events and snapshots only — no renderer, engine, or physics-native
/// state. Deterministic: identical event/snapshot streams produce identical outputs.
/// </summary>
public sealed class GameplayRules
{
    private readonly EntityStore _entities;
    private readonly DamageStore _damage;
    private readonly MotorStore _motors;
    private readonly TntStore _tnt;
    private readonly WheelStore _wheels;
    private readonly PigStore _pigs;
    private readonly PhysicsBodyStore _bodies;
    private readonly TransformStore _transforms;
    private readonly GameplayConfig _config;

    private readonly Dictionary<uint, EntityId> _entitiesByBody = new();
    private readonly Dictionary<uint, (PhysicsVector3 Position, PhysicsVector3 Velocity)> _kinematicsByBody = new();
    private readonly HashSet<uint> _touchedBodies = new();
    private readonly HashSet<uint> _brokenJoints = new();
    private readonly HashSet<uint> _dynamicBodies = new();
    private int _alivePigs;
    private int _pigsRegistered;

    public GameplayRules(
        EntityStore entities,
        DamageStore damage,
        MotorStore motors,
        TntStore tnt,
        WheelStore wheels,
        PigStore pigs,
        PhysicsBodyStore bodies,
        TransformStore transforms,
        GameplayConfig? config = null)
    {
        _entities = entities ?? throw new ArgumentNullException(nameof(entities));
        _damage = damage ?? throw new ArgumentNullException(nameof(damage));
        _motors = motors ?? throw new ArgumentNullException(nameof(motors));
        _tnt = tnt ?? throw new ArgumentNullException(nameof(tnt));
        _wheels = wheels ?? throw new ArgumentNullException(nameof(wheels));
        _pigs = pigs ?? throw new ArgumentNullException(nameof(pigs));
        _bodies = bodies ?? throw new ArgumentNullException(nameof(bodies));
        _transforms = transforms ?? throw new ArgumentNullException(nameof(transforms));
        _config = config ?? GameplayConfig.Default;
    }

    public GameplayPhase Phase { get; private set; } = GameplayPhase.Playing;

    public int AlivePigs => _alivePigs;

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

    public void AddPig(EntityId entity, float hitPoints)
    {
        _pigs.Set(entity, default);
        _damage.Set(entity, new DamageState(hitPoints, ImpactThreshold: 0f, ImpactFactor: 1f));
        _alivePigs++;
        _pigsRegistered++;
    }

    public void AddTnt(EntityId entity, ushort fuseTicks)
    {
        _tnt.Set(entity, new TntState(fuseTicks, Ignited: false));
        _damage.Set(entity, new DamageState(1f, ImpactThreshold: 0f, ImpactFactor: 0f));
    }

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
        ProcessEvents(events, output);
        RunMotors(output);
        RunTntFuses(tick, output);
        ReapDestroyed(output);
        DropCommandsForDestroyedBodies(output);

        if (_pigsRegistered > 0 && _alivePigs == 0)
        {
            Phase = GameplayPhase.Won;
        }
        else if (tick >= _config.MaxTicks)
        {
            Phase = GameplayPhase.Failed;
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
                        ApplyImpactDamage(physicsEvent.BodyA, physicsEvent.BodyB, output);
                        IgniteTntInContact(physicsEvent.BodyA, physicsEvent.BodyB);
                    }

                    break;
                case PhysicsEventKind.JointBroken:
                    _brokenJoints.Add(physicsEvent.Joint.Value);
                    break;
            }
        }
    }

    private void ApplyImpactDamage(PhysicsBodyId bodyA, PhysicsBodyId bodyB, GameplayTickOutput output)
    {
        if (!_kinematicsByBody.TryGetValue(bodyA.Value, out var kinematicsA)
            || !_kinematicsByBody.TryGetValue(bodyB.Value, out var kinematicsB))
        {
            return;
        }

        float relativeSpeed = PhysicsVector3.Distance(kinematicsA.Velocity, kinematicsB.Velocity);
        ApplyDamageToBody(bodyA, relativeSpeed, output);
        ApplyDamageToBody(bodyB, relativeSpeed, output);
    }

    private void ApplyDamageToBody(PhysicsBodyId body, float relativeSpeed, GameplayTickOutput output)
    {
        if (!_entitiesByBody.TryGetValue(body.Value, out EntityId entity)
            || !_damage.TryGet(entity, out DamageState damage))
        {
            return;
        }

        float impact = (relativeSpeed - damage.ImpactThreshold) * damage.ImpactFactor;
        if (impact <= 0)
        {
            return;
        }

        _damage.Set(entity, damage with { HitPoints = damage.HitPoints - impact });
    }

    private void IgniteTntInContact(PhysicsBodyId bodyA, PhysicsBodyId bodyB)
    {
        IgniteTntOnBody(bodyA);
        IgniteTntOnBody(bodyB);
    }

    private void IgniteTntOnBody(PhysicsBodyId body)
    {
        if (!_entitiesByBody.TryGetValue(body.Value, out EntityId entity)
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
                PhysicsVector3.Zero));
        }
    }

    private void RunTntFuses(uint tick, GameplayTickOutput output)
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
                || !_kinematicsByBody.TryGetValue(bodyId, out var kinematics)
                || !_entitiesByBody.TryGetValue(bodyId, out EntityId target))
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
            output.Commands.Add(PhysicsCommand.ApplyImpulse(
                new PhysicsBodyId(bodyId),
                direction * (_config.TntBlastImpulse * falloff),
                PhysicsVector3.Zero));

            if (_damage.TryGet(target, out DamageState damage))
            {
                _damage.Set(target, damage with { HitPoints = damage.HitPoints - (_config.TntBlastDamage * falloff) });
            }
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

    private void ReapDestroyed(GameplayTickOutput output)
    {
        // Slot-ordered sweep keeps destruction order deterministic.
        var damageComponents = _damage.GetEnumerator();
        List<EntityId> dead = null!;
        while (damageComponents.MoveNext())
        {
            if (damageComponents.CurrentValue.HitPoints <= 0)
            {
                (dead ??= new List<EntityId>()).Add(damageComponents.CurrentId);
            }
        }

        if (dead is null)
        {
            return;
        }

        foreach (EntityId entity in dead)
        {
            DestroyEntity(entity, output);
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
        }

        _damage.Remove(entity);
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

        _damage.Remove(entity);
        _motors.Remove(entity);
        _tnt.Remove(entity);
        _wheels.Remove(entity);
        _pigs.Remove(entity);
        _bodies.Remove(entity);
        _transforms.Remove(entity);
        _entities.Destroy(entity);
        output.DestroyedEntities.Add(entity);
    }
}
