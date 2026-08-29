using PigForge.Physics.Abstractions;

namespace PigForge.Core;

/// <summary>
/// Stable entity handle: low bits hold the slot index (+1 so the zero value stays invalid),
/// high bits hold the generation that invalidates handles after slot reuse.
/// </summary>
public readonly record struct EntityId(uint Value)
{
	public const int SlotBits = 20;
	public const uint SlotMask = (1u << SlotBits) - 1;
	public const uint GenerationModulus = 1u << (32 - SlotBits);

	public bool IsValid => Value != 0;

	public uint SlotIndex => (Value & SlotMask) - 1;

	public uint Generation => Value >> SlotBits;

	public static EntityId FromSlotAndGeneration(uint slotIndex, uint generation) =>
		new(((generation << SlotBits) | (slotIndex + 1)) & uint.MaxValue);

	public static EntityId FromSlotAndGenerationChecked(uint slotIndex, uint generation)
	{
		if (slotIndex >= SlotMask)
		{
			throw new ArgumentOutOfRangeException(nameof(slotIndex), slotIndex, $"Slot index must stay below {SlotMask}.");
		}

		if (generation is 0 or >= GenerationModulus)
		{
			throw new ArgumentOutOfRangeException(nameof(generation), generation, $"Generation must be in [1, {GenerationModulus - 1}].");
		}

		return FromSlotAndGeneration(slotIndex, generation);
	}
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
