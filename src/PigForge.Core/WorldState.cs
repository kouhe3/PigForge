using PigForge.Physics.Abstractions;

namespace PigForge.Core;

public readonly record struct EntityId(uint Value)
{
	public bool IsValid => Value != 0;
}

public readonly record struct Tick(uint Value);

public sealed class EntityState
{
	public EntityState(EntityId id, PhysicsBodyId physicsBody)
	{
		if (!id.IsValid)
		{
			throw new ArgumentException("An entity must have a non-zero identifier.", nameof(id));
		}

		if (!physicsBody.IsValid)
		{
			throw new ArgumentException("An entity must reference a valid physics body.", nameof(physicsBody));
		}

		Id = id;
		PhysicsBody = physicsBody;
	}

	public EntityId Id { get; }
	public PhysicsBodyId PhysicsBody { get; }
}

public sealed class WorldState
{
	private readonly Dictionary<EntityId, EntityState> _entities = new();

	public Tick CurrentTick { get; private set; }

	public int EntityCount => _entities.Count;

	public void AddEntity(EntityState entity)
	{
		ArgumentNullException.ThrowIfNull(entity);
		if (!_entities.TryAdd(entity.Id, entity))
		{
			throw new InvalidOperationException($"Entity {entity.Id.Value} already exists.");
		}
	}

	public bool TryGetEntity(EntityId id, out EntityState? entity)
	{
		return _entities.TryGetValue(id, out entity);
	}

	public void AdvanceTick()
	{
		CurrentTick = new(checked(CurrentTick.Value + 1));
	}
}
